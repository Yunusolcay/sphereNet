using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Magic;

/// <summary>
/// Spell engine. Maps to CChar::Spell_* functions in Source-X CCharSpell.cpp.
/// Handles cast start, cast done, spell effects, damage/heal, resist.
/// </summary>
public sealed class SpellEngine
{
    private readonly GameWorld _world;
    private readonly SpellRegistry _spells;
    private readonly Random _rand = new();

    public SpellEngine(GameWorld world, SpellRegistry spells)
    {
        _world = world;
        _spells = spells;
    }

    /// <summary>Callback to play a sound at a location.</summary>
    public Action<Point3D, ushort>? OnPlaySound { get; set; }

    /// <summary>Source-X CClientMsg::SysMessage hook for the active caster.
    /// Program.cs wires this to the owning GameClient so spell-specific
    /// failure/success messages (recall blank rune, gate already there,
    /// poison resisted, etc.) reach only the caster, matching upstream.</summary>
    public Action<Character, string>? OnSysMessage { get; set; }

    /// <summary>Callback fired when a spell is interrupted. Args: (Character caster, string reason).</summary>
    public Action<Character, string>? OnSpellInterrupt { get; set; }

    /// <summary>Fired when a cast starts after we've turned the caster
    /// to face the target — Program.cs uses this to broadcast a 0x77
    /// MobileMoving so other clients see the new facing while the cast
    /// animation plays. Without this, the caster appears to throw the
    /// spell sideways.</summary>
    public Action<Character>? OnCasterFacingChanged { get; set; }

    /// <summary>Callback fired when a character's personal light level
    /// changes (e.g. after Night Sight). Program.cs wires this to the
    /// matching GameClient so it can send a fresh 0x4E packet.</summary>
    public Action<Character>? OnPersonalLightChanged { get; set; }

    /// <summary>One entry per active time-limited spell effect. Captures
    /// what was applied (stat deltas, light level, flag) so UndoEffect can
    /// revert exactly those changes when the timer fires. Runtime-only —
    /// effects do not persist across server restart, matching Source-X buff
    /// semantics.</summary>
    private sealed class ActiveSpellEffect
    {
        public required Character Target { get; init; }
        public required SpellType Spell { get; init; }
        public long ExpireTick { get; set; }
        public short StrDelta { get; set; }
        public short DexDelta { get; set; }
        public short IntDelta { get; set; }
        public byte OldLightLevel { get; set; }
        public bool LightChanged { get; set; }
        public StatFlag AppliedFlag { get; set; }
    }

    /// <summary>Active time-limited spell effects. Walked once per world tick
    /// by <see cref="ProcessExpirations"/>; when the tick is reached the
    /// entry is removed and <see cref="UndoEffect"/> reverts its recorded
    /// deltas.</summary>
    private readonly List<ActiveSpellEffect> _activeEffects = [];

    /// <summary>Get a spell definition by type (for flag checks, etc.).</summary>
    public SpellDef? GetSpellDef(SpellType spell) => _spells.Get(spell);

    /// <summary>
    /// Check and apply spell interruption from damage.
    /// Call this when a casting character takes damage.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromDamage(Character caster, int damage)
    {
        if (!caster.TryGetTag("SPELL_CASTING", out _))
            return false;

        // Interrupt chance = damage / maxHits * 100 (Source-X style)
        int chance = caster.MaxHits > 0 ? (damage * 100) / caster.MaxHits : 100;
        chance = Math.Clamp(chance, 5, 95); // always at least 5% chance, never 100%

        // Protection spell halves the interrupt chance. Uses the
        // ArcherCanMove bit as a Protection marker (see the Protection
        // case in ApplySpecificSpell for the rationale).
        if (caster.IsStatFlag(StatFlag.ArcherCanMove))
            chance /= 2;

        if (_rand.Next(100) < chance)
        {
            ClearCastState(caster);
            caster.RemoveTag("CAST_TIMER");
            OnSpellInterrupt?.Invoke(caster, "damaged");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Check and apply spell interruption from movement.
    /// Call this when a casting character moves.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromMovement(Character caster)
    {
        if (!caster.TryGetTag("SPELL_CASTING", out _))
            return false;

        // GMs can cast while moving
        if (caster.PrivLevel >= PrivLevel.GM)
            return false;

        ClearCastState(caster);
        caster.RemoveTag("CAST_TIMER");
        OnSpellInterrupt?.Invoke(caster, "moved");
        return true;
    }

    /// <summary>
    /// Check and apply spell interruption from equipment change.
    /// Call this when a casting character equips/unequips an item.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromEquip(Character caster)
    {
        if (!caster.TryGetTag("SPELL_CASTING", out _))
            return false;

        ClearCastState(caster);
        caster.RemoveTag("CAST_TIMER");
        OnSpellInterrupt?.Invoke(caster, "equip_changed");
        return true;
    }

    /// <summary>
    /// Begin casting a spell. Maps to Spell_CastStart.
    /// Returns cast time in milliseconds, or -1 on failure.
    /// </summary>
    public int CastStart(Character caster, SpellType spell, Serial targetUid, Point3D targetPos)
    {
        var def = _spells.Get(spell);
        if (def == null || def.IsFlag(SpellFlag.Disabled))
            return -1;

        if (caster.IsDead)
            return -1;

        // Region NoMagic check
        if (_world != null)
        {
            var region = _world.FindRegion(caster.Position);
            if (region != null && region.NoMagic && caster.PrivLevel < Core.Enums.PrivLevel.GM)
                return -1;
        }

        // Mana check
        if (caster.Mana < def.ManaCost)
            return -1;

        // Skill check
        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);
        int difficulty = def.GetDifficulty();

        // Wand/scroll: reduce difficulty and bypass reagent cost (the item is
        // the reagent). GM+ also bypasses reagent requirements.
        var weapon = caster.GetEquippedItem(Layer.OneHanded);
        bool isWand = weapon?.ItemType == ItemType.Wand;
        if (isWand) difficulty = 1;

        // Reagent availability check (before starting cast). Wand/scroll/GM skip.
        if (!isWand && caster.PrivLevel < PrivLevel.GM &&
            Character.ReagentsRequiredEnabled && !HasRequiredReagents(caster, def))
        {
            OnSpellInterrupt?.Invoke(caster, "You lack the reagents to cast that spell.");
            return -1;
        }

        // Cast time
        int castTimeTenths = def.GetCastTime(skillVal);
        if (caster.PrivLevel >= PrivLevel.GM)
            castTimeTenths = 1;

        // Store cast state on character
        caster.SetTag("SPELL_CASTING", ((int)spell).ToString());
        caster.SetTag("SPELL_TARGET_UID", targetUid.Value.ToString());
        caster.SetTag("SPELL_TARGET_X", targetPos.X.ToString());
        caster.SetTag("SPELL_TARGET_Y", targetPos.Y.ToString());
        caster.SetTag("SPELL_TARGET_Z", targetPos.Z.ToString());

        // Source-X CCharSkill::Skill_Magery -> UpdateDir(m_Act_p): turn the
        // caster to face the spell target so the cast animation plays in
        // the correct direction. Skip if self-target or same-tile.
        if (!targetPos.Equals(caster.Position))
        {
            var newDir = caster.Position.GetDirectionTo(targetPos);
            if (newDir != caster.Direction)
            {
                caster.Direction = newDir;
                OnCasterFacingChanged?.Invoke(caster);
            }
        }

        return castTimeTenths * 100; // convert to ms
    }

    /// <summary>
    /// Complete a spell cast. Maps to Spell_CastDone.
    /// Called after cast timer expires.
    /// </summary>
    public bool CastDone(Character caster)
    {
        if (!caster.TryGetTag("SPELL_CASTING", out string? spellStr))
            return false;

        if (!int.TryParse(spellStr, out int spellId))
            return false;

        var spell = (SpellType)spellId;
        var def = _spells.Get(spell);
        if (def == null) return false;

        // Consume mana
        if (caster.Mana < def.ManaCost)
            return false;
        caster.Mana -= (short)def.ManaCost;

        // Consume reagents (unless using a wand / GM override).
        var castWeapon = caster.GetEquippedItem(Layer.OneHanded);
        bool castWithWand = castWeapon?.ItemType == ItemType.Wand;
        if (!castWithWand && caster.PrivLevel < PrivLevel.GM &&
            Character.ReagentsRequiredEnabled)
        {
            ConsumeReagents(caster, def);
        }

        // Resolve target
        Serial targetUid = Serial.Invalid;
        if (caster.TryGetTag("SPELL_TARGET_UID", out string? uidStr) && uint.TryParse(uidStr, out uint uid))
            targetUid = new Serial(uid);

        Point3D targetPos = caster.Position;
        if (caster.TryGetTag("SPELL_TARGET_X", out string? xs) &&
            caster.TryGetTag("SPELL_TARGET_Y", out string? ys) &&
            caster.TryGetTag("SPELL_TARGET_Z", out string? zs))
        {
            short.TryParse(xs, out short tx);
            short.TryParse(ys, out short ty);
            sbyte.TryParse(zs, out sbyte tz);
            targetPos = new Point3D(tx, ty, tz, caster.MapIndex);
        }

        // Line-of-sight check for spells that target a remote location. Self-buffs
        // and same-tile targets skip the check. Source-X equivalent: CSpell::OnTarget
        // CanSeeLOS block — "You cannot see that target."
        if (_world != null &&
            caster.PrivLevel < PrivLevel.GM &&
            (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj) ||
             def.IsFlag(SpellFlag.Area)     || def.IsFlag(SpellFlag.Field) ||
             def.IsFlag(SpellFlag.Summon)))
        {
            int losDist = Math.Max(Math.Abs(caster.X - targetPos.X), Math.Abs(caster.Y - targetPos.Y));
            if (losDist > 0 && !_world.CanSeeLOS(caster.Position, targetPos))
            {
                ClearCastState(caster);
                OnSpellInterrupt?.Invoke(caster, "Target not in line of sight.");
                return false;
            }
        }

        // Clear cast state
        ClearCastState(caster);

        // Get skill level for effect calculation
        var primarySkill = def.GetPrimarySkill();
        int skillLevel = caster.GetSkill(primarySkill);

        // Apply spell effect
        if (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj))
        {
            var target = _world?.FindChar(targetUid);
            if (target != null)
                ApplyCharEffect(caster, target, def, skillLevel);
        }
        else if (def.IsFlag(SpellFlag.Area))
        {
            ApplyAreaEffect(caster, targetPos, def, skillLevel);
        }
        else if (def.IsFlag(SpellFlag.Field))
        {
            CreateField(caster, targetPos, def);
        }
        else if (def.IsFlag(SpellFlag.Summon))
        {
            SummonCreature(caster, targetPos, def, skillLevel);
        }
        else
        {
            // Self-buff or ground target
            ApplyCharEffect(caster, caster, def, skillLevel);
        }

        // Sound
        if (def.Sound > 0)
            OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);

        return true;
    }

    /// <summary>Return true if the caster's backpack holds at least the needed
    /// amount of every reagent the spell requires. Reagents are identified by
    /// BaseId; stacked amounts contribute per-stack.</summary>
    private bool HasRequiredReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return true;
        if (caster.Backpack == null) return false;
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            int have = 0;
            foreach (var item in _world.GetContainerContents(caster.Backpack.Uid))
            {
                if (item.IsDeleted) continue;
                if (item.BaseId != regBaseId) continue;
                have += Math.Max(1, (int)item.Amount);
                if (have >= needed) break;
            }
            if (have < needed) return false;
        }
        return true;
    }

    /// <summary>Deduct the spell's reagent cost from the caster's backpack.
    /// Removes stacks that hit zero. Caller must have already verified
    /// availability with HasRequiredReagents.</summary>
    private void ConsumeReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return;
        if (caster.Backpack == null) return;
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            int remaining = needed;
            // Snapshot — we mutate items/delete, can't iterate live collection.
            var stacks = _world.GetContainerContents(caster.Backpack.Uid).ToList();
            foreach (var item in stacks)
            {
                if (remaining <= 0) break;
                if (item.IsDeleted) continue;
                if (item.BaseId != regBaseId) continue;
                int stackAmt = Math.Max(1, (int)item.Amount);
                int take = Math.Min(remaining, stackAmt);
                ushort newAmt = (ushort)(stackAmt - take);
                item.Amount = newAmt;
                remaining -= take;
                if (newAmt == 0)
                {
                    _world.DeleteObject(item);
                    item.Delete();
                }
            }
        }
    }

    /// <summary>Apply spell effect to a single character target.</summary>
    private void ApplyCharEffect(Character caster, Character target, SpellDef def, int skillLevel)
    {
        // Magic Reflect: the first harmful spell targeted at a mobile with
        // the Reflection flag is bounced back to the caster. Matches
        // Source-X Magic_Reflect and ServUO MagicReflectSpell behaviour:
        // the flag is single-use — consumed as soon as a harmful spell
        // hits, so the reflected spell itself will NOT be re-reflected
        // by the original caster (even if they also have Reflection up).
        bool harmful = def.IsFlag(SpellFlag.Damage) || def.IsFlag(SpellFlag.Curse);
        if (harmful && caster != target && target.IsStatFlag(StatFlag.Reflection))
        {
            target.ClearStatFlag(StatFlag.Reflection);
            // Remove the now-consumed flag's expiration entry so UndoEffect
            // doesn't try to clear it again later.
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                if (_activeEffects[i].Target == target && _activeEffects[i].Spell == SpellType.MagicReflect)
                { _activeEffects.RemoveAt(i); break; }
            }
            (caster, target) = (target, caster);
        }

        int effect = def.GetEffect(skillLevel);

        // Randomize potency (Source-X: iSkillLevel/2 + rand(iSkillLevel/2))
        int potency = skillLevel / 2 + _rand.Next(Math.Max(1, skillLevel / 2));
        effect = def.GetEffect(potency);

        // Magic resist
        if (def.IsFlag(SpellFlag.Resist) && caster != target)
        {
            int resist = CalcMagicResist(target, def, caster);
            effect -= effect * resist / 100;
        }

        // Damage spells
        if (def.IsFlag(SpellFlag.Damage))
        {
            var dmgType = GetSpellDamageType(def.Id);
            int damage = Math.Max(0, effect);
            // Apply elemental resist
            damage = CombatEngine.ApplyElementalResist(target, damage, dmgType);
            if (damage > 0)
            {
                target.Hits -= (short)damage;
                target.RecordAttack(caster.Uid, damage);
                if (target.Hits <= 0)
                    target.Kill();
            }
        }
        // Heal spells
        else if (def.IsFlag(SpellFlag.Heal))
        {
            target.Hits = (short)Math.Min(target.Hits + effect, target.MaxHits);
        }
        // Buff/debuff
        else if (def.IsFlag(SpellFlag.Bless))
        {
            ApplyBuff(caster, target, def, effect);
        }
        else if (def.IsFlag(SpellFlag.Curse))
        {
            ApplyCurse(caster, target, def, effect);
        }
        // Specific spells
        else
        {
            ApplySpecificSpell(caster, target, def, effect);
        }
    }

    /// <summary>Apply area effect. Maps to SPELLFLAG_AREA logic.</summary>
    private void ApplyAreaEffect(Character caster, Point3D center, SpellDef def, int skillLevel)
    {
        int range = 3 + skillLevel / 300; // scale range with skill
        foreach (var target in _world.GetCharsInRange(center, range))
        {
            if (target == caster && def.IsFlag(SpellFlag.TargNoSelf)) continue;
            if (target.IsDead && !def.IsFlag(SpellFlag.TargDead)) continue;

            ApplyCharEffect(caster, target, def, skillLevel);
        }
    }

    /// <summary>Create a field item at target location.</summary>
    private void CreateField(Character caster, Point3D pos, SpellDef def)
    {
        var fieldItem = _world.CreateItem();
        fieldItem.BaseId = def.EffectId;
        fieldItem.Name = def.Name + " field";
        fieldItem.SetTag("FIELD_CASTER", caster.Uid.Value.ToString());
        fieldItem.SetTag("FIELD_CASTER_UUID", caster.Uuid.ToString("D"));
        fieldItem.SetTag("FIELD_DAMAGE", def.GetEffect(caster.GetSkill(def.GetPrimarySkill())).ToString());
        fieldItem.DecayTime = Environment.TickCount64 + 30_000; // 30s duration
        _world.PlaceItem(fieldItem, pos);
    }

    /// <summary>Summon a creature at target location.</summary>
    private void SummonCreature(Character caster, Point3D pos, SpellDef def, int skillLevel)
    {
        var creature = _world.CreateCharacter();
        creature.Name = def.Name;
        creature.NpcBrain = NpcBrainType.Monster;
        creature.NpcMaster = caster.Uid;

        int duration = def.GetDuration(skillLevel);
        creature.SetTag("SUMMON_DURATION", duration.ToString());
        creature.SetTag("SUMMON_MASTER", caster.Uid.Value.ToString());
        creature.SetTag("SUMMON_MASTER_UUID", caster.Uuid.ToString("D"));

        _world.PlaceCharacter(creature, pos);
    }

    /// <summary>
    /// Calculate magic resistance. Maps to resist logic in OnSpellEffect.
    /// Returns resist percentage (0-25 typically).
    /// </summary>
    private int CalcMagicResist(Character target, SpellDef def, Character caster)
    {
        int mr = target.GetSkill(SkillType.MagicResistance);
        int magery = caster.GetSkill(SkillType.Magery);

        int resistFirst = mr / 50; // MR/5 → tenths, so /50 in 0-1000 scale
        int resistSecond = ((magery - 200) / 50) + (1 + (int)def.Id / 8) * 5;

        bool success = _rand.Next(100) < resistFirst + Math.Max(0, resistFirst - resistSecond);
        return success ? 25 : 0;
    }

    /// <summary>Get damage type for spell.</summary>
    private static DamageType GetSpellDamageType(SpellType spell) => spell switch
    {
        SpellType.Fireball or SpellType.Flamestrike or SpellType.FireField or
        SpellType.MeteorSwarm or SpellType.Explosion => DamageType.Fire,

        SpellType.Lightning or SpellType.ChainLightning or SpellType.EnergyBolt or
        SpellType.EnergyVortex or SpellType.EnergyField => DamageType.Energy,

        SpellType.Harm => DamageType.Cold,
        SpellType.Poison or SpellType.PoisonField => DamageType.Poison,

        _ => DamageType.Magic,
    };

    private void ApplyBuff(Character caster, Character target, SpellDef def, int effect)
    {
        short bonus = (short)Math.Max(1, effect / 10);
        switch (def.Id)
        {
            case SpellType.Strength:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = bonus; target.Str += bonus;
                break;
            }
            case SpellType.Agility:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.DexDelta = bonus; target.Dex += bonus;
                break;
            }
            case SpellType.Cunning:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.IntDelta = bonus; target.Int += bonus;
                break;
            }
            case SpellType.Bless:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = bonus; eff.DexDelta = bonus; eff.IntDelta = bonus;
                target.Str += bonus; target.Dex += bonus; target.Int += bonus;
                break;
            }
        }
    }

    private void ApplyCurse(Character caster, Character target, SpellDef def, int effect)
    {
        short penalty = (short)Math.Max(1, effect / 10);
        switch (def.Id)
        {
            case SpellType.Weaken:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = (short)-penalty; target.Str -= penalty;
                break;
            }
            case SpellType.Clumsy:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.DexDelta = (short)-penalty; target.Dex -= penalty;
                break;
            }
            case SpellType.Feeblemind:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.IntDelta = (short)-penalty; target.Int -= penalty;
                break;
            }
            case SpellType.Curse:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.StrDelta = (short)-penalty; eff.DexDelta = (short)-penalty; eff.IntDelta = (short)-penalty;
                target.Str -= penalty; target.Dex -= penalty; target.Int -= penalty;
                break;
            }
        }
    }

    private void ApplySpecificSpell(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Teleport:
                // Already handled by target position
                break;
            case SpellType.Recall:
                // Source-X CCharSpell Recall: rune must be marked, otherwise
                // 'spell_recall_blank' / 'spell_recall_notrune'.
                if (target.TryGetTag("RUNE_X", out string? rx) &&
                    target.TryGetTag("RUNE_Y", out string? ry))
                {
                    short.TryParse(rx, out short rxx);
                    short.TryParse(ry, out short ryy);
                    sbyte rzz = 0;
                    if (target.TryGetTag("RUNE_Z", out string? rz))
                        sbyte.TryParse(rz, out rzz);
                    _world.MoveCharacter(caster, new Point3D(rxx, ryy, rzz, caster.MapIndex));
                }
                else
                {
                    OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                }
                break;
            case SpellType.Mark:
                // Mark a rune with current location
                if (target != caster)
                {
                    var item = _world.FindItem(new Serial((uint)effect));
                    if (item != null)
                    {
                        item.SetTag("RUNE_X", caster.X.ToString());
                        item.SetTag("RUNE_Y", caster.Y.ToString());
                        item.SetTag("RUNE_Z", caster.Z.ToString());
                        OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellMarkCont));
                    }
                }
                break;
            case SpellType.GateTravel:
                // Source-X CCharSpell GateTravel: rune must be marked, else
                // 'spell_recall_blank'. The destination link is established
                // via the rune's RUNE_* tags — Mark seeds them.
                if (!target.TryGetTag("RUNE_X", out _))
                {
                    OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                    break;
                }
                var gate = _world.CreateItem();
                gate.BaseId = 0x0F6C; // moongate graphic
                gate.Name = "moongate";
                gate.DecayTime = Environment.TickCount64 + 30_000;
                _world.PlaceItem(gate, caster.Position);
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateOpen));
                break;
            case SpellType.Cure:
            case SpellType.ArchCure:
                target.CurePoison();
                break;
            case SpellType.Paralyze:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Freeze;
                target.SetStatFlag(StatFlag.Freeze);
                break;
            }
            case SpellType.Invisibility:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Invisible;
                target.SetStatFlag(StatFlag.Invisible);
                break;
            }
            case SpellType.Reveal:
                target.ClearStatFlag(StatFlag.Invisible);
                target.ClearStatFlag(StatFlag.Hidden);
                break;
            case SpellType.Dispel:
            case SpellType.MassDispel:
                // Remove summoned creatures near target
                if (target.IsStatFlag(StatFlag.Conjured))
                    target.Kill();
                break;
            case SpellType.Resurrection:
                if (target.IsDead)
                    target.Resurrect();
                break;
            case SpellType.Poison:
            {
                // Source-X CCharSpell Poison: level (1=lesser, 4=deadly) keys
                // the matching DEFMSG_SPELL_POISON_# string the victim sees.
                byte poisonLvl = caster.GetSkill(SkillType.Magery) switch
                {
                    >= 800 => 4, // deadly
                    >= 600 => 3, // greater
                    >= 400 => 2, // normal
                    _ => 1       // lesser
                };
                target.ApplyPoison(poisonLvl);
                string poisonKey = poisonLvl switch
                {
                    1 => Msg.SpellPoison1,
                    2 => Msg.SpellPoison2,
                    3 => Msg.SpellPoison3,
                    4 => Msg.SpellPoison4,
                    _ => Msg.SpellPoison5
                };
                OnSysMessage?.Invoke(target, ServerMessages.Get(poisonKey));
                break;
            }
            case SpellType.NightSight:
            {
                // 0x4E PacketPersonalLight adds brightness on top of global
                // lighting; higher value = brighter. 30 overrides typical
                // night global (~12). The DURATION script value feeds the
                // expiration timer below; when the timer fires, LightLevel
                // reverts to its pre-cast value and the stat flag is cleared.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.NightSight;
                eff.OldLightLevel = target.LightLevel;
                eff.LightChanged = true;
                target.SetStatFlag(StatFlag.NightSight);
                target.LightLevel = 30;
                OnPersonalLightChanged?.Invoke(target);
                break;
            }
            case SpellType.ReactiveArmor:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Reactive;
                target.SetStatFlag(StatFlag.Reactive);
                break;
            }
            case SpellType.Protection:
            {
                // ArcherCanMove bit is reused as a Protection marker — no
                // dedicated StatFlag.Protection exists yet. CombatEngine
                // checks IsStatFlag(ArcherCanMove) to reduce cast-interrupt
                // chance; documented here so the flag reuse is explicit.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.ArcherCanMove;
                target.SetStatFlag(StatFlag.ArcherCanMove);
                break;
            }
            case SpellType.Incognito:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Incognito;
                target.SetStatFlag(StatFlag.Incognito);
                break;
            }
            case SpellType.MagicReflect:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Reflection;
                target.SetStatFlag(StatFlag.Reflection);
                break;
            }
            case SpellType.Polymorph:
                // Polymorph body change — needs target prompt for body ID.
                // Deferred until the body-swap + name/hue save/restore path
                // (shared with Incognito visual changes) is implemented.
                break;
            case SpellType.ManaDrain:
                int drain = Math.Min(target.Mana, (short)effect);
                target.Mana -= (short)drain;
                caster.Mana = (short)Math.Min(caster.Mana + drain, caster.MaxMana);
                break;
            case SpellType.ManaVampire:
                int vamp = Math.Min(target.Mana, (short)effect);
                target.Mana -= (short)vamp;
                caster.Mana = (short)Math.Min(caster.Mana + vamp, caster.MaxMana);
                break;
        }
    }

    private static void ClearCastState(Character ch)
    {
        ch.RemoveTag("SPELL_CASTING");
        ch.RemoveTag("SPELL_TARGET_UID");
        ch.RemoveTag("SPELL_TARGET_X");
        ch.RemoveTag("SPELL_TARGET_Y");
        ch.RemoveTag("SPELL_TARGET_Z");
    }

    /// <summary>Register a spell's expiration with its undo data.
    /// Duration comes from <see cref="SpellDef.GetDuration"/>(caster's
    /// primary skill) — tenths of a second per the CAST_TIME /
    /// DURATION convention. ServUO semantics: duration scales with the
    /// CASTER's skill, not the target's. If the script leaves DURATION
    /// at 0 a 30-second floor kicks in so buffs don't expire instantly
    /// on scripts that forgot the field. Re-casting on the same target
    /// refreshes the timer and merges the delta rather than stacking.</summary>
    private ActiveSpellEffect ScheduleEffectExpiry(Character caster, Character target, SpellType spell, SpellDef def)
    {
        int casterSkill = caster.GetSkill(def.GetPrimarySkill());
        int durationTenths = def.GetDuration(casterSkill);
        if (durationTenths <= 0) durationTenths = 300; // 30s floor
        long expireTick = Environment.TickCount64 + (long)durationTenths * 100L;

        // Refresh on re-cast — revert the previous delta first so the new
        // cast stacks cleanly onto the base value, not on top of the old buff.
        for (int i = 0; i < _activeEffects.Count; i++)
        {
            var existing = _activeEffects[i];
            if (existing.Target == target && existing.Spell == spell)
            {
                RevertDeltas(existing);
                _activeEffects.RemoveAt(i);
                break;
            }
        }

        var eff = new ActiveSpellEffect { Target = target, Spell = spell, ExpireTick = expireTick };
        _activeEffects.Add(eff);
        return eff;
    }

    /// <summary>Walk the active-effect list once per world tick and undo
    /// any whose expire tick has passed. Called from Program.cs main
    /// loop. Cheap when the list is empty; no-op otherwise.</summary>
    public void ProcessExpirations(long now)
    {
        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            if (now < _activeEffects[i].ExpireTick) continue;
            var eff = _activeEffects[i];
            _activeEffects.RemoveAt(i);
            RevertDeltas(eff);
        }
    }

    /// <summary>Revert exactly what <see cref="ScheduleEffectExpiry"/>
    /// recorded for this effect — stat deltas subtracted, flag cleared,
    /// light level restored + 0x4E refresh dispatched. Safe to call
    /// even when the effect didn't touch a given field (the delta will
    /// be 0 / flag None / LightChanged false).</summary>
    private void RevertDeltas(ActiveSpellEffect eff)
    {
        var t = eff.Target;
        if (eff.StrDelta != 0) t.Str -= eff.StrDelta;
        if (eff.DexDelta != 0) t.Dex -= eff.DexDelta;
        if (eff.IntDelta != 0) t.Int -= eff.IntDelta;
        if (eff.AppliedFlag != StatFlag.None) t.ClearStatFlag(eff.AppliedFlag);
        if (eff.LightChanged)
        {
            t.LightLevel = eff.OldLightLevel;
            OnPersonalLightChanged?.Invoke(t);
        }
    }
}

/// <summary>
/// Registry of all spell definitions. Populated from scripts.
/// </summary>
public sealed class SpellRegistry
{
    private readonly Dictionary<SpellType, SpellDef> _spells = [];

    public void Register(SpellDef def) => _spells[def.Id] = def;
    public SpellDef? Get(SpellType id) => _spells.GetValueOrDefault(id);
    public IEnumerable<SpellDef> GetAll() => _spells.Values;
    public int Count => _spells.Count;
}

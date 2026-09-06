using System.Globalization;
using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Messages;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
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

    /// <summary>Fired when a cast RESOLVES at completion — for ANY caster (player,
    /// NPC, direct) — so the @SpellSuccess / @SpellEffect / @SpellFail triggers run
    /// from one engine point instead of only the client path. Args: caster, spell,
    /// success. Wired to fire the char triggers and the per-spell [SPELL] ON= block.
    /// (Cast-START failures and the targeting cursor's @SpellSelect/@SpellCast/
    /// @SpellTargetCancel stay on the client UI flow — NPCs gate casts via @NPCActCast.)</summary>
    public Action<Character, SpellType, bool>? OnCastResolved { get; set; }

    /// <summary>Fired when a cast starts after we've turned the caster
    /// to face the target — Program.cs uses this to broadcast a 0x77
    /// MobileMoving so other clients see the new facing while the cast
    /// animation plays. Without this, the caster appears to throw the
    /// spell sideways.</summary>
    public Action<Character>? OnCasterFacingChanged { get; set; }
    public Action<Character, ushort>? OnCastAnimation { get; set; }

    /// <summary>Callback fired to broadcast spell power words as overhead
    /// speech (e.g. "In Lor" for Night Sight). Program.cs wires this to
    /// send a 0x1C speech packet visible to nearby clients.</summary>
    public Action<Character, string>? OnSpellWords { get; set; }

    /// <summary>Styled power-words callback (@SpellCast LOCAL.WOPColor /
    /// LOCAL.WOPFont overrides): caster, words, hue (0 = caster default),
    /// font (0 = default). Preferred over <see cref="OnSpellWords"/> when set.</summary>
    public Action<Character, string, ushort, byte>? OnSpellWordsEx { get; set; }

    /// <summary>Callback fired when a CLIENTLESS caster's spell completes —
    /// the player completion path (TickSpellCast) sends its own bolt/impact
    /// effect, but NPC casts have no client and were entirely invisible.
    /// Args: caster, resolved char target (null = location/self), spell def.</summary>
    public Action<Character, Character?, SpellDef>? OnNpcCastFx { get; set; }

    /// <summary>Callback fired when a character's personal light level
    /// changes (e.g. after Night Sight). Program.cs wires this to the
    /// matching GameClient so it can send a fresh 0x4E packet.</summary>
    public Action<Character>? OnPersonalLightChanged { get; set; }

    /// <summary>Callback fired when a spell kills a target. Program.cs wires
    /// this to the DeathEngine pipeline so corpse/loot/triggers are processed
    /// instead of the bare Character.Kill() that skips them.</summary>
    public Action<Character, Character?>? OnTargetKilled { get; set; }

    public Action<Character, Point3D, byte>? OnSpellTeleport { get; set; }

    /// <summary>Remove a world item and broadcast its deletion (used by
    /// Dispel Field to clear a field item early).</summary>
    public Action<Item>? OnItemRemoved { get; set; }

    /// <summary>Play a sound only for this character's client (Source-X
    /// m_pClient-&gt;addSound) — hallucination trip sounds must not broadcast
    /// to bystanders.</summary>
    public Action<Character, ushort>? OnPlaySoundTo { get; set; }

    /// <summary>Re-send the nearby world view to this character's client
    /// (Source-X addChar/addPlayerSee on the hallucination tick — each
    /// refresh re-rolls the random hues).</summary>
    public Action<Character>? OnViewRefresh { get; set; }

    /// <summary>Overhead text spoken by the character, visible to everyone
    /// nearby (Source-X CChar::Speak — the drunk *hic* line).</summary>
    public Action<Character, string>? OnOverheadEmote { get; set; }

    /// <summary>Optional script dispatcher for item spell hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>One entry per active time-limited spell effect. Captures
    /// what was applied (stat deltas, light level, flag) so UndoEffect can
    /// revert exactly those changes when the timer fires. Saved as remaining
    /// time so Source-X-style spell memory survives restart.</summary>
    private sealed class ActiveSpellEffect
    {
        public required Character Target { get; init; }
        public required SpellType Spell { get; init; }
        public long ExpireTick { get; set; }
        public short StrDelta { get; set; }
        public short DexDelta { get; set; }
        public short IntDelta { get; set; }
        public int ArmorDelta { get; set; }
        public int MeditationDelta { get; set; }
        public byte OldLightLevel { get; set; }
        public byte NewLightLevel { get; set; }
        public bool LightChanged { get; set; }
        public StatFlag AppliedFlag { get; set; }
        public ushort OldBodyId { get; set; }
        public ushort NewBodyId { get; set; }
        public bool BodyChanged { get; set; }
        public string? OldName { get; set; }
        public string? NewName { get; set; }
        public bool NameChanged { get; set; }
        public int CurseWeaponLevel { get; set; }

        /// <summary>Effect magnitude reported to the client's buff tooltip —
        /// the Source-X m_itSpell.m_spelllevel equivalent, fed to the cliloc
        /// formatter arguments. Persisted with the effect record and mirrored
        /// into the spell memory's MOREY, so a reload keeps the exact value.
        /// Records written before this field existed fall back to the stat
        /// delta (see <see cref="GetBuffMagnitude"/>).</summary>
        public int BuffMagnitude { get; set; }

        // Periodic damage-over-time (reference SPELLFLAG_TICK spell memories:
        // Pain Spike, Strangle). Non-zero DotCharges marks an active DOT; the
        // tick pass in ProcessExpirations applies damage and decrements charges.
        public int DotCharges { get; set; }
        public int DotTotalCharges { get; set; }
        public long DotNextTickMs { get; set; }
        public int DotIntervalMs { get; set; }
        public int DotDamagePerTick { get; set; }
        public int DotPower { get; set; }
        public DamageType DotDamageType { get; set; }
        public bool DotDirect { get; set; }
        public Serial DotSource { get; set; }

        // Necromancy Blood Oath bond (state lives on the caster).
        public Serial BloodOathEnemy { get; set; }
        public int BloodOathLevel { get; set; }

        // Source-X equips a real IT_SPELL memory item per active effect
        // (Spell_Effect_Create). We mirror that: this is the worn spell-memory
        // item on the target, created on apply and deleted on every removal.
        // Null for effects that predate the memory (defensive) or fail to create.
        public Item? Memory { get; set; }
    }

    // Disguise names used by Incognito.
    private static readonly string[] s_incognitoNames =
    {
        "Adam", "Brom", "Cyne", "Doran", "Edric", "Faerd", "Gareth", "Halt",
        "Ivar", "Joran", "Kael", "Loric", "Maren", "Nyle", "Oren", "Pael",
        "Quenn", "Roth", "Sael", "Tarl", "Ulric", "Varis", "Wren", "Yorick",
    };

    /// <summary>Active time-limited spell effects. Walked once per world tick
    /// by <see cref="ProcessExpirations"/>; when the tick is reached the
    /// entry is removed and <see cref="UndoEffect"/> reverts its recorded
    /// deltas.</summary>
    private readonly List<ActiveSpellEffect> _activeEffects = [];
    private const int PersistedEffectVersion = 1;

    /// <summary>Get a spell definition by type (for flag checks, etc.).</summary>
    public SpellDef? GetSpellDef(SpellType spell) => _spells.Get(spell);

    /// <summary>Source-X Spell_CastStart timing: CAST_TIME minus two tenths
    /// per effective FASTERCASTING point, floored at one tenth.</summary>
    public static int CalculateCastTimeTenths(Character caster, SpellDef def, int? skillValue = null)
    {
        if (caster.PrivLevel >= PrivLevel.GM)
            return 1;

        int skill = skillValue ?? caster.GetSkill(def.GetPrimarySkill());
        long wait = def.GetCastTime(skill) - (2L * GetCastingPropertyValue(
            caster, SpellCastingProperties.FasterCasting));
        return (int)Math.Max(1, wait);
    }

    /// <summary>Character property plus equipped item instance/ITEMDEF values.</summary>
    public static int GetCastingPropertyValue(Character caster, string property)
    {
        if (!SpellCastingProperties.Contains(property))
            return 0;

        long total = caster.Tags.GetInt(property);
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var item = caster.GetEquippedItem((Layer)layerIndex);
            if (item == null)
                continue;

            if (item.TryGetTag(property, out string? raw) && TryParseInteger(raw, out int instanceValue))
            {
                total += instanceValue;
                continue;
            }

            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (itemDef != null && TryParseInteger(itemDef.TagDefs.Get(property), out int defValue))
                total += defValue;
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static bool TryParseInteger(string? raw, out int value) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Advance an in-progress cast timer. Returns true while still casting.
    /// Used by player TickSpellCast and NPC AI ticks.
    /// </summary>
    public bool TickCastTimer(Character caster)
    {
        if (!caster.IsCasting)
            return false;

        if (caster.IsCastTimerActive(Environment.TickCount64))
            return true;

        caster.SetCastTimerEnd(0);
        CastDone(caster);
        return false;
    }

    private static bool IsMagicFlag(MagicConfigFlags flag) =>
        (Character.MagicFlags & (int)flag) != 0;

    private static bool IsWieldingWand(Character caster)
    {
        var weapon = caster.GetEquippedItem(Layer.OneHanded)
                  ?? caster.GetEquippedItem(Layer.TwoHanded);
        return weapon?.ItemType == ItemType.Wand;
    }

    /// <summary>What a cast is being paid for FROM.</summary>
    private enum CastSourceKind { Self, Wand, Scroll }

    private Item? FindTaggedItem(string? raw) =>
        uint.TryParse(raw, out uint uid) ? _world?.FindItem(new Serial(uid)) : null;

    /// <summary>Source-X: "magic items must be on your person to use"
    /// (CCharSpell.cpp:2422) - the source's TOP-LEVEL owner must be the caster.
    /// Deliberately no GM bypass: the reference makes this test BEFORE its PRIV_GM
    /// shortcut (:2429), so a GM cannot cast off someone else's scroll either.</summary>
    private bool IsCastSourceOnPerson(Character caster, Item item)
    {
        var current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            if (!current.ContainedIn.IsValid) return false;   // lying in the world
            if (current.ContainedIn == caster.Uid) return true;
            var parent = _world?.FindItem(current.ContainedIn);
            if (parent == null) return false;
            current = parent;
        }
        return false;
    }

    /// <summary>Re-resolve the cast source at the moment it is needed, the way
    /// Source-X re-reads m_Act_Prv_UID at completion (CCharSpell.cpp:2882) and hands
    /// that same resolved pointer to Spell_CanCast (:3010). Answering from the TAG
    /// alone meant a scroll that had been destroyed, dropped or given away during the
    /// cast time still bought its owner the half-mana, no-reagent discount, and the
    /// consumption then took the scroll out of whoever's pack it had reached.
    ///
    /// Returns false when a source was claimed but is no longer usable: Spell_CanCast
    /// rejects a null source (:2330) and one that is not on the caster (:2422).</summary>
    private bool TryResolveCastSource(Character caster, out CastSourceKind kind, out Item? source)
    {
        source = null;
        kind = CastSourceKind.Self;

        if (caster.TryGetTag("WAND_UID", out string? wandStr))
        {
            kind = CastSourceKind.Wand;
            source = FindTaggedItem(wandStr);
        }
        else if (caster.TryGetTag("SCROLL_UID", out string? scrollStr))
        {
            kind = CastSourceKind.Scroll;
            source = FindTaggedItem(scrollStr);
        }
        else
        {
            // Nothing was recorded, so the caster is spending their own power. NPCs
            // never tag - their AI hands them the wand directly - so the wielded-wand
            // reading survives for them alone. For a PLAYER a wand merely held is not
            // a cast source: reading equipment here let a player swap a wand in during
            // the cast time and finish an ordinary spell at wand prices.
            if (!caster.IsPlayer && IsWieldingWand(caster))
                kind = CastSourceKind.Wand;
            return true;
        }

        return source != null && !source.IsDeleted && IsCastSourceOnPerson(caster, source);
    }

    /// <summary>Consume the wand charge / spell scroll that initiated this cast,
    /// once the cast has committed to success. The player path tags WAND_UID /
    /// SCROLL_UID at double-click (instead of decrementing immediately); NPC wand
    /// casts consume their own charge in the AI. Both tags are cleared here.</summary>
    private void ConsumeCastSource(Character caster, CastSourceKind kind, Item? source)
    {
        ClearCastSourceTags(caster);
        if (source == null || source.IsDeleted)
            return;

        if (kind == CastSourceKind.Wand)
            ConsumeWandCharge(source);
        else if (kind == CastSourceKind.Scroll)
        {
            if (source.Amount > 1) source.Amount--;
            else _world?.RemoveItem(source);
        }
    }

    /// <summary>Decrement a wand's CHARGES tag; depleting it clears the bound spell
    /// (More1). A wand with no CHARGES tag is treated as unlimited.</summary>
    private static void ConsumeWandCharge(Item wand)
    {
        if (!wand.TryGetTag("CHARGES", out string? ch) || !int.TryParse(ch, out int charges))
            return;
        charges--;
        if (charges <= 0)
        {
            wand.More1 = 0;
            wand.RemoveTag("CHARGES");
        }
        else
            wand.SetTag("CHARGES", charges.ToString());
    }

    /// <summary>Drop the cast-source tags WITHOUT consuming — used when a cast is
    /// interrupted / aborted so a half-started wand or scroll cast does not leak its
    /// tag into the next cast (which would then wrongly consume the charge / scroll).</summary>
    private static void ClearCastSourceTags(Character caster)
    {
        caster.RemoveTag("WAND_UID");
        caster.RemoveTag("SCROLL_UID");
    }

    private static bool IsSpellDisabledByConfig(SpellType spell)
    {
        return spell switch
        {
            SpellType.Mark when IsMagicFlag(MagicConfigFlags.DisableMark) => true,
            SpellType.Recall when IsMagicFlag(MagicConfigFlags.DisableRecall) => true,
            SpellType.GateTravel when IsMagicFlag(MagicConfigFlags.DisableGate) => true,
            _ => false
        };
    }

    private static void RevealOnCast(Character caster)
    {
        if (IsMagicFlag(MagicConfigFlags.NoRevealOnCast))
            return;
        caster.ClearHiddenState();
    }

    /// <summary>Precast mode from sphere.ini MAGICFLAGS bit 0x0002.</summary>
    public static bool IsPrecastEnabled(SpellDef def) =>
        IsMagicFlag(MagicConfigFlags.Precast) && !def.IsFlag(SpellFlag.NoPrecast);

    private static bool IsOutdoorOnlySpell(SpellType spell) => spell switch
    {
        SpellType.ChainLightning or SpellType.Flamestrike or
        SpellType.MeteorSwarm or SpellType.EnergyVortex or
        SpellType.Earthquake or SpellType.AirElemental or
        SpellType.EarthElemental or SpellType.FireElemental or
        SpellType.WaterElemental => true,
        _ => false,
    };

    private bool CanCastOutdoorSpell(Character caster, SpellDef def, Point3D pos)
    {
        if (!IsOutdoorOnlySpell(def.Id)) return true;
        if (IsMagicFlag(MagicConfigFlags.DungeonOutdoorSpells)) return true;
        if (caster.PrivLevel >= PrivLevel.GM) return true;
        var region = _world.FindRegion(pos);
        return region == null || !region.IsFlag(RegionFlag.Underground);
    }

    private void ApplyCastResourceLoss(Character caster, SpellDef def, bool wand, bool scroll,
        bool fizzle, bool abort)
    {
        if (caster.PrivLevel >= PrivLevel.GM)
            return;

        // A scroll cast owes no reagents whether it succeeds, fizzles or is aborted:
        // Source-X gates Calc_SpellReagentsConsume on the cast source being the
        // caster themselves, and calls it with that same source on every path
        // (CCharSpell.cpp:3380/3398). The caller passes the RESOLVED source rather
        // than the raw tag, so a scroll that no longer exists cannot buy the exemption.
        bool takeReagents = !wand && !scroll &&
            Character.ReagentsRequiredEnabled && HasRequiredReagents(caster, def);
        if (fizzle && !Character.ReagentLossFail) takeReagents = false;
        if (abort && !Character.ReagentLossAbort) takeReagents = false;

        bool takeMana = true;
        if (fizzle && !Character.ManaLossFail) takeMana = false;
        if (abort && !Character.ManaLossAbort) takeMana = false;

        if (takeMana && def.ManaCost > 0)
        {
            int cost = Math.Max(0, def.ManaCost * Character.ManaLossPercent / 100);
            caster.Mana = (short)Math.Max(0, caster.Mana - cost);
        }

        if (takeReagents)
            ConsumeReagents(caster, def);
    }

    /// <summary>A cast that reached its completion but cannot legally resolve.
    /// Source-X returns false from Spell_CastDone, which CCharSkill.cpp:3000 turns
    /// into SKTRIG_ABORT and prices through Spell_CastFail(fAbort = true) - so this
    /// is NOT an unconditional refund, it is the configured abort cost
    /// (MANALOSSABORT / REAGENTLOSSABORT, CCharSpell.cpp:3316).</summary>
    private bool FailCastAtCompletion(Character caster, SpellDef def, CastSourceKind kind, string? message)
    {
        ApplyCastResourceLoss(caster, def, kind == CastSourceKind.Wand,
            kind == CastSourceKind.Scroll, fizzle: false, abort: true);
        ClearCastSourceTags(caster);
        ClearCastState(caster);
        if (message != null)
            OnSysMessage?.Invoke(caster, message);
        return false;
    }

    private void InterruptCast(Character caster, string reason)
    {
        SpellDef? def = null;
        if (caster.TryGetCastingSpell(out SpellType spell))
            def = _spells.Get(spell);

        if (def != null)
        {
            TryResolveCastSource(caster, out CastSourceKind kind, out _);
            ApplyCastResourceLoss(caster, def, kind == CastSourceKind.Wand,
                kind == CastSourceKind.Scroll, fizzle: false, abort: true);
        }

        // An aborted wand/scroll cast must not leave its source tag behind, or the
        // next cast would consume the charge/scroll on success.
        ClearCastSourceTags(caster);
        ClearCastState(caster);

        string msg = reason switch
        {
            "damaged" or "moved" or "equip_changed" => ServerMessages.Get(Msg.SpellGenFizzles),
            _ => reason
        };
        OnSpellInterrupt?.Invoke(caster, msg);
        if (caster.IsPlayer)
            OnSysMessage?.Invoke(caster, msg);
    }

    /// <summary>
    /// Check and apply spell interruption from damage.
    /// Call this when a casting character takes damage.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromDamage(Character caster, int damage)
    {
        // Source-X CChar::OnTakeDamage: taking damage removes paralyze (the
        // LAYER_SPELL_Paralyze memory is deleted) unless DAMAGE_NOUNPARALYZE.
        // Every damage path — melee, NPC swing, spell — routes through this
        // method, so the break lives beside the other on-damage disturbs.
        if (damage > 0 && caster.IsStatFlag(StatFlag.Freeze))
            BreakParalyze(caster);

        if (!caster.IsCasting)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        // Reference disturb (OnTakeDamage): only players are disturbed; the
        // chance is the spell's INTERRUPT curve at the caster's skill
        // (per-mille) — the damage amount does not factor in.
        if (!caster.IsPlayer)
            return false;

        int chance = 1000;
        if (caster.TryGetCastingSpell(out SpellType castingSpell))
        {
            var def = GetSpellDef(castingSpell);
            if (def != null)
                chance = def.GetInterruptChance(caster.GetSkill(def.GetPrimarySkill()));
        }
        if (chance <= 0)
            return false;

        // Protection effect dampens the disturb (engine approximation of the
        // reference protection-spell cancel).
        if (caster.IsStatFlag(StatFlag.ArcherCanMove))
            chance /= 2;

        if (_rand.Next(1000) < chance)
        {
            InterruptCast(caster, "damaged");
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
        if (!caster.IsCasting)
            return false;

        if (caster.PrivLevel >= PrivLevel.GM)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        InterruptCast(caster, "moved");
        return true;
    }

    /// <summary>
    /// Check and apply spell interruption from equipment change.
    /// Call this when a casting character equips/unequips an item.
    /// Returns true if the spell was interrupted.
    /// </summary>
    public bool TryInterruptFromEquip(Character caster)
    {
        if (!caster.IsCasting)
            return false;

        if (IsMagicFlag(MagicConfigFlags.NoInterrupt))
            return false;

        InterruptCast(caster, "equip_changed");
        return true;
    }

    /// <summary>
    /// Begin casting a spell. Maps to Spell_CastStart.
    /// Returns cast time in milliseconds, or -1 on failure.
    /// <paramref name="wopOverride"/> comes from @SpellCast LOCAL.WOP:
    /// null = the spell's own power words, "" = silent cast, anything else
    /// replaces the spoken mantra.
    /// </summary>
    public int CastStart(Character caster, SpellType spell, Serial targetUid, Point3D targetPos,
        string? wopOverride = null, ushort wopHue = 0, byte wopFont = 0)
    {
        var def = _spells.Get(spell);
        if (def == null || def.IsFlag(SpellFlag.Disabled))
            return -1;

        if (IsSpellDisabledByConfig(spell))
            return -1;

        // Native handlers exist only for Magery/Necromancy (+ the 1000+ custom
        // Sphere spells). A school spell (Chivalry/Bushido/Ninjitsu/Spellweaving/
        // Mysticism range) whose def carries NO behaviour — no flags, no
        // effect/duration curve, no scripted ON= stage — used to swallow mana and
        // reagents and then silently no-op. Refuse it up front instead; the
        // schools themselves are a deferred project (PARITY.md "Deferred tail").
        // A pack that scripts these spells (flags/curves/trigger stages) passes.
        if (IsInertSchoolSpell(def))
        {
            OnSysMessage?.Invoke(caster, "That spell is not supported yet.");
            return -1;
        }

        if (caster.IsDead)
            return -1;
        if (caster.IsCasting)
            return -1;
        if (caster.IsStatFlag(StatFlag.Freeze))
            return -1;

        // [SPELL n] ON=@Select (Source-X SPTRIG_SELECT, fired from
        // Spell_CanCast so EVERY entry point gets it — client cast, scroll,
        // wand, NPC, console). RETURN 1 cancels the selection before any
        // resource is committed. Packs use this for form toggles (Reaper
        // Form / Stone Form re-cast while polymorphed).
        if (TriggerDispatcher != null)
        {
            var selectArgs = new TriggerArgs
            {
                CharSrc = caster,
                N1 = (int)spell,
                N2 = EffectiveManaCost(caster, def),
            };
            if (TriggerDispatcher.FireSpellTrigger(spell, "Select", caster, selectArgs)
                == TriggerResult.True)
                return -1;
        }
        // Region NoMagic / NoMagicDamage check (Source-X anti-magic sub-flags).
        if (_world != null)
        {
            var region = _world.FindRegion(caster.Position);
            if (region != null && caster.PrivLevel < Core.Enums.PrivLevel.GM)
            {
                if (region.NoMagic)
                    return -1;
                // REGION_ANTIMAGIC_DAMAGE: harmful magic is suppressed in this region.
                if (region.IsFlag(RegionFlag.NoMagicDamage) && IsHarmfulSpell(def))
                    return -1;
            }
        }

        var weapon = caster.GetEquippedItem(Layer.OneHanded);
        var offhand = caster.GetEquippedItem(Layer.TwoHanded);
        bool isWand = weapon?.ItemType == ItemType.Wand;
        bool fromScroll = caster.TryGetTag("SCROLL_UID", out _);

        // Mana check — the SAME discounted cost the completion will consume
        // (wand = free, scroll = half). Requiring the full effective cost up
        // front while the discount only applied at consumption meant a wand
        // wielder needed mana for a free cast (audit finding 7).
        int requiredMana = EffectiveManaCost(caster, def);
        if (isWand) requiredMana = 0;
        else if (fromScroll) requiredMana /= 2;
        if (caster.Mana < requiredMana)
            return -1;

        if (!CanCastOutdoorSpell(caster, def, targetPos))
        {
            OnSysMessage?.Invoke(caster, "That spell does not work here.");
            return -1;
        }

        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);
        // A wielded spellbook never blocks casting (reference: casting from
        // the book in hand is the normal flow); wands likewise.
        bool hasBlockingWeapon =
            (weapon != null && weapon.ItemType is not ItemType.Wand and not ItemType.Spellbook) ||
            (offhand != null && offhand.ItemType is not ItemType.Shield and not ItemType.Spellbook);

        if (!Character.EquippedCastEnabled && caster.IsPlayer && hasBlockingWeapon &&
            caster.PrivLevel < PrivLevel.GM)
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return -1;
        }

        // Reagent availability check (before starting cast). Wand/scroll/GM skip.
        // NPC casters are exempt — like the reference, monsters cast from innate
        // power and carry no reagents (mirrors the IsPlayer-gated spellbook check
        // below). Without this gate an NPC with an empty pack fails every cast.
        if (caster.IsPlayer && !isWand && !fromScroll && caster.PrivLevel < PrivLevel.GM &&
            Character.ReagentsRequiredEnabled && !HasRequiredReagents(caster, def))
        {
            OnSysMessage?.Invoke(caster, "You lack the reagents to cast that spell.");
            return -1;
        }

        // Spellbook requirement (reference Spell_CanCast): a player casting
        // from memory must have the spell in an accessible spellbook; scroll
        // and wand casts bypass the book. Only the classic 1-64 ids are
        // tracked through the book bit mask (More1/More2).
        if (Character.SpellbookRequiredEnabled && caster.IsPlayer &&
            !isWand && !fromScroll && caster.PrivLevel < PrivLevel.GM &&
            (int)def.Id is >= 1 and <= 64 &&
            !HasSpellInBook(caster, (int)def.Id))
        {
            OnSysMessage?.Invoke(caster, "You don't know that spell.");
            return -1;
        }

        // Cast time
        int castTimeTenths = CalculateCastTimeTenths(caster, def, skillVal);

        RevealOnCast(caster);

        // Store cast state on character
        caster.BeginCast(spell, targetUid, targetPos);

        if (!targetPos.Equals(caster.Position))
        {
            var newDir = caster.Position.GetDirectionTo(targetPos);
            if (newDir != caster.Direction)
            {
                caster.Direction = newDir;
                OnCasterFacingChanged?.Invoke(caster);
            }
        }

        var powerWords = wopOverride ?? def.GetPowerWords();
        if (!string.IsNullOrEmpty(powerWords))
        {
            if (OnSpellWordsEx != null)
                OnSpellWordsEx(caster, powerWords, wopHue, wopFont);
            else
                OnSpellWords?.Invoke(caster, powerWords);
        }

        bool isAreaSpell = targetUid == caster.Uid;
        ushort castAnim = isAreaSpell
            ? (ushort)Core.Enums.AnimationType.CastArea
            : (ushort)Core.Enums.AnimationType.CastDirected;
        OnCastAnimation?.Invoke(caster, castAnim);

        return castTimeTenths * 100;
    }

    /// <summary>
    /// Complete a spell cast. Maps to Spell_CastDone.
    /// Called after cast timer expires.
    /// </summary>
    public bool CastDone(Character caster)
    {
        // Capture the spell before the core clears cast state, then fire the
        // shared resolution hook so NPC/direct casts reach @SpellSuccess /
        // @SpellEffect / @SpellFail — the client path no longer fires these.
        bool wasCasting = caster.TryGetCastingSpell(out SpellType resolvedSpell);
        bool ok = CastDoneCore(caster);
        if (wasCasting)
            OnCastResolved?.Invoke(caster, resolvedSpell, ok);
        return ok;
    }

    private bool CastDoneCore(Character caster)
    {
        if (caster.IsDead)
        {
            ClearCastState(caster);
            return false;
        }

        if (!caster.TryGetCastingSpell(out SpellType spell))
            return false;

        var def = _spells.Get(spell);
        if (def == null)
        {
            ClearCastState(caster);
            return false;
        }

        // Resolve target before consumption so LOS can be checked first
        Serial targetUid = caster.CastTargetUid;
        Point3D targetPos = caster.CastTargetPos;

        // Source-X re-resolves the cast source at completion and refuses the cast if
        // it is gone or no longer on the caster (CCharSpell.cpp:2882 -> :3010).
        if (!TryResolveCastSource(caster, out CastSourceKind sourceKind, out Item? castSource))
            return FailCastAtCompletion(caster, def, sourceKind,
                ServerMessages.Get(Msg.SpellEnchantActivate));

        // LOS check BEFORE consuming resources
        if (_world != null &&
            caster.PrivLevel < PrivLevel.GM &&
            !IsMagicFlag(MagicConfigFlags.NoLos) &&
            (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj) ||
             def.IsFlag(SpellFlag.Area)     || def.IsFlag(SpellFlag.Field) ||
             def.IsFlag(SpellFlag.Summon)))
        {
            int losDist = Math.Max(Math.Abs(caster.X - targetPos.X), Math.Abs(caster.Y - targetPos.Y));
            if (losDist > 0 && !_world.CanSeeLOS(caster.Position, targetPos))
                return FailCastAtCompletion(caster, def, sourceKind, "Target not in line of sight.");
        }

        // Source-X opens Spell_CastDone with Spell_TargCheck (CCharSpell.cpp:2878)
        // and only reaches the reagent/mana/charge consumption 130 lines later
        // (:3010). SphereNet re-resolved the target AFTER those costs were taken, so
        // a target that died, walked away or vanished during the cast time was paid
        // for in full and still reported success to @SpellSuccess.
        if (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj))
        {
            var preTarget = _world?.FindChar(targetUid);
            if (preTarget != null)
            {
                // :2740 - a ghost is not a legal target unless the spell says so.
                if (preTarget.IsDead && !def.IsFlag(SpellFlag.TargDead))
                    return FailCastAtCompletion(caster, def, sourceKind,
                        ServerMessages.Get(Msg.SpellTargDead));
                if (preTarget.MapIndex != caster.MapIndex ||
                    caster.Position.GetDistanceTo(preTarget.Position) > 12)
                    return FailCastAtCompletion(caster, def, sourceKind, "That is too far away.");
            }
            // :2728 - "need a target". A spell that may also be aimed at the ground
            // (TARG_XYZ) is allowed to complete without one.
            else if (_world?.FindItem(targetUid) == null && !def.IsFlag(SpellFlag.TargXYZ))
                return FailCastAtCompletion(caster, def, sourceKind,
                    ServerMessages.Get(Msg.SpellTargObj));
        }

        var primarySkill = def.GetPrimarySkill();
        int skillVal = caster.GetSkill(primarySkill);
        int difficulty = def.GetDifficulty();
        bool castWithWand = sourceKind == CastSourceKind.Wand;
        bool castFromScroll = sourceKind == CastSourceKind.Scroll;
        if (castWithWand) difficulty = 10;          // reference: wand = minimal difficulty
        else if (castFromScroll) difficulty /= 2;   // reference: scroll = half difficulty

        // Source-X: skill check at cast completion — fizzle on failure.
        // GetDifficulty() is on the 0-1000 skill scale, but CheckSuccess expects
        // a 0-100 difficulty (it multiplies by 10 internally) — convert here so
        // the bell curve compares like-for-like against the 0-1000 skill value.
        bool fizzled = caster.PrivLevel < PrivLevel.GM &&
            !SkillEngine.CheckSuccess(caster, primarySkill, difficulty / 10);

        // Source-X Spell_CastDone awards the casting skill a gain attempt on every
        // resolved cast, whether it succeeds or fizzles. (GainExperience itself
        // guards GM/dead/locked/safe-region.)
        if (caster.PrivLevel < PrivLevel.GM)
            SkillEngine.GainExperience(caster, primarySkill, difficulty / 10);

        if (fizzled)
        {
            ApplyCastResourceLoss(caster, def, castWithWand, castFromScroll, fizzle: true, abort: false);
            ClearCastSourceTags(caster);
            ClearCastState(caster);
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return false;
        }

        // Source-X builds the summon and weighs it against the follower cap BEFORE
        // the spell is paid for: Spell_Summon_Try (CCharSpell.cpp:3002) creates the
        // chosen creature and refuses it on GetFollowerSlots (:2662), and only then
        // does Spell_CanCast consume (:3010) - deleting the summon again if that
        // consumption cannot be met (:3012). SphereNet summoned in the effect stage
        // instead, long after the mana, reagents and scroll had been taken, so a
        // refused summon was charged for in full. The refusal reports itself, hence
        // no second message here.
        Character? summoned = null;
        if (def.IsFlag(SpellFlag.Summon))
        {
            summoned = PrepareSummon(caster, targetPos, def, spell, skillVal);
            if (summoned == null)
                return FailCastAtCompletion(caster, def, sourceKind, null);
        }

        // Mana requirement and consumption share ONE discounted cost (wand
        // free, scroll half, Mind Rot via EffectiveManaCost). The old code
        // required only the BASE def.ManaCost here while consuming the
        // effective cost — the start/done checks disagreed (audit finding 7).
        int manaCost = Math.Max(0, EffectiveManaCost(caster, def) * Character.ManaLossPercent / 100);
        if (castWithWand) manaCost = 0;             // reference: wands cost no mana
        else if (castFromScroll) manaCost /= 2;     // reference: scrolls cost half mana
        if (caster.Mana < manaCost)
        {
            DiscardSummon(summoned);
            ClearCastSourceTags(caster);
            ClearCastState(caster);
            OnSysMessage?.Invoke(caster, "You lack the mana to cast that spell.");
            return false;
        }

        // Reagents are owed only by a player casting from their own power. Source-X
        // Calc_SpellReagentsConsume is gated on (pObj == pCharCaster), so a wand or
        // scroll cast pays none - the start check already exempted the scroll while
        // this consumption did not, so carrying reagents made the same scroll cost
        // more than casting it empty-handed.
        if (caster.IsPlayer && !castWithWand && !castFromScroll &&
            caster.PrivLevel < PrivLevel.GM && Character.ReagentsRequiredEnabled &&
            !ConsumeReagents(caster, def))
        {
            // The reagents were there when the cast began and are not there now:
            // the spell cannot be paid for, so it does not happen. Source-X fails the
            // whole cast the same way when its re-check at CastDone cannot pay.
            DiscardSummon(summoned);
            ClearCastSourceTags(caster);
            ClearCastState(caster);
            OnSysMessage?.Invoke(caster, "You lack the reagents to cast that spell.");
            return false;
        }

        caster.Mana -= (short)manaCost;

        // Consume the wand charge / scroll ONLY now that the cast has committed to
        // success (passed fizzle + mana). Moving this off the double-click / client
        // success path means a charge/scroll is never lost to an interrupted,
        // cancelled or fizzled cast, and NPC/precast casts consume correctly too.
        ConsumeCastSource(caster, sourceKind, castSource);

        // Clear cast state
        ClearCastState(caster);

        int skillLevel = skillVal;

        // Mark targets an item (rune), not a character
        if (spell == SpellType.Mark)
        {
            var rune = _world?.FindItem(targetUid);
            if (rune != null)
            {
                if (!IsItemAccessible(caster, rune))
                {
                    OnSysMessage?.Invoke(caster, "You must target a rune in your pack.");
                }
                else if (FireItemSpellEffect(caster, rune, def) == TriggerResult.True)
                {
                    // Script handled/cancelled the native mark.
                }
                else
                {
                    rune.SetRuneMark(caster.Position);
                    OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellMarkCont));
                }
            }
            else
            {
                OnSysMessage?.Invoke(caster, "You must target a recall rune.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Dispel Field targets a field ITEM (not a character) and removes it.
        if (spell == SpellType.DispelField)
        {
            var fieldItem = _world?.FindItem(targetUid);
            if (fieldItem != null && !fieldItem.IsDeleted &&
                (fieldItem.TryGetTag("FIELD_DAMAGE", out _) || fieldItem.ItemType == ItemType.Spell))
            {
                if (FireItemSpellEffect(caster, fieldItem, def) != TriggerResult.True)
                    OnItemRemoved?.Invoke(fieldItem);
            }
            else
            {
                OnSysMessage?.Invoke(caster, "That is not a magical field.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Animate Dead targets a CORPSE item and raises an undead controlled by
        // the caster (reference SPELL_Animate_Dead, CCharSpell.cpp:2608-2632).
        if (spell is SpellType.AnimateDeadAOS or SpellType.AnimateDead)
        {
            var corpse = _world?.FindItem(targetUid);
            if (corpse != null && !corpse.IsDeleted && corpse.ItemType == ItemType.Corpse &&
                CanReachCorpse(caster, corpse))
            {
                // A humanoid corpse raises a zombie; a creature corpse raises its
                // own kind (the corpse stores the original body in Amount).
                ushort corpseBody = corpse.Amount;
                bool humanoid = corpseBody is 0x0190 or 0x0191 or 0x025D or 0x025E;
                ushort body = humanoid ? (ushort)0x0003 : (corpseBody == 0 ? (ushort)0x0003 : corpseBody);
                // Stats/skills come from the raised creature's chardef @Create
                // (SummonCreature applies it) — not flat invented numbers.
                var undead = SummonCreature(caster, corpse.Position, def, skillLevel, bodyId: body);
                if (undead != null)
                    OnItemRemoved?.Invoke(corpse); // the corpse is consumed
            }
            else
            {
                OnSysMessage?.Invoke(caster, "You cannot animate that.");
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Bone Armor targets a SKELETON corpse and converts it into up to five
        // bone armor pieces at diminishing odds (reference SPELL_Bone_Armor,
        // CCharSpell.cpp:3234-3270). Any carried loot is dumped first.
        if (spell == SpellType.BoneArmor)
        {
            var corpse = _world?.FindItem(targetUid);
            if (corpse == null || corpse.IsDeleted || corpse.ItemType != ItemType.Corpse ||
                !CanReachCorpse(caster, corpse))
            {
                OnSysMessage?.Invoke(caster, "That is not a corpse!");
            }
            else if (corpse.Amount != 0x0032) // CREID_SKELETON only
            {
                OnSysMessage?.Invoke(caster, "The body stirs for a moment.");
            }
            else
            {
                foreach (var child in corpse.Contents.ToArray())
                {
                    corpse.RemoveItem(child);
                    _world!.PlaceItemWithDecay(child, corpse.Position);
                }
                // ITEMID_BONE_ARMS/ARMOR/GLOVES/HELM/LEGS, 1/(2+n) stop chance
                // per piece like the reference loop.
                ReadOnlySpan<ushort> bonePieces = [0x144E, 0x144F, 0x1450, 0x1451, 0x1452];
                int got = 0;
                foreach (ushort pieceId in bonePieces)
                {
                    if (_rand.Next(2 + got) == 0)
                        break;
                    var piece = _world!.CreateItem();
                    piece.BaseId = pieceId;
                    // Run the ITEMDEF @Create metadata (type, armor, durability,
                    // events) like every other scripted create path — a bare
                    // BaseId would leave the piece a graphic-only shell.
                    Definitions.ItemDefHelper.ApplyInstanceMetadata(piece, pieceId);
                    _world.PlaceItemWithDecay(piece, corpse.Position);
                    got++;
                }
                OnItemRemoved?.Invoke(corpse); // the corpse is consumed
            }
            if (def.Sound > 0) OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);
            return true;
        }

        // Apply spell effect
        if (spell is SpellType.Recall or SpellType.GateTravel or SpellType.SacredJourney)
        {
            var rune = _world?.FindItem(targetUid);
            if (rune != null && IsItemAccessible(caster, rune))
            {
                if (FireItemSpellEffect(caster, rune, def) != TriggerResult.True)
                    ApplyRuneTravelSpell(caster, rune, def);
            }
            else
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
        }
        else if (def.IsFlag(SpellFlag.TargChar) || def.IsFlag(SpellFlag.TargObj))
        {
            var target = _world?.FindChar(targetUid);
            if (target != null)
            {
                if (target.IsDead && !def.IsFlag(SpellFlag.TargDead))
                {
                    OnSysMessage?.Invoke(caster, "That target is dead.");
                }
                else if (target.MapIndex != caster.MapIndex ||
                    caster.Position.GetDistanceTo(target.Position) > 12)
                {
                    OnSysMessage?.Invoke(caster, "That is too far away.");
                }
                else
                {
                    ApplyCharEffect(caster, target, def, skillLevel);
                }
            }
            else if (def.IsFlag(SpellFlag.TargObj))
            {
                var itemTarget = _world?.FindItem(targetUid);
                if (itemTarget != null && CanSpellReachItem(caster, itemTarget))
                {
                    // Fire the scriptable @SpellEffect first; only apply the
                    // hardcoded item-spell behavior when no script overrode it.
                    if (FireItemSpellEffect(caster, itemTarget, def) != TriggerResult.True)
                        ApplyItemTargetSpell(itemTarget, def);
                }
            }
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
            // Already created above, before the costs were taken - see PrepareSummon.
        }
        else
        {
            // Self-buff or ground target
            ApplyCharEffect(caster, caster, def, skillLevel);
        }

        // NPC casters have no client-side completion path (the player's
        // TickSpellCast sends the impact/bolt effect there); emit their
        // completion FX through the host hook so observers see the spell land.
        if (!caster.IsPlayer)
            OnNpcCastFx?.Invoke(caster, _world?.FindChar(targetUid), def);

        // Sound
        if (def.Sound > 0)
            OnPlaySound?.Invoke(caster.Position, (ushort)def.Sound);

        return true;
    }

    /// <summary>Hardcoded behavior for item-targeted spells (Magic Lock/Unlock,
    /// Magic Trap/Untrap). Runs only when no @SpellEffect script overrode it.
    /// (Telekinesis remains script-driven via @SpellEffect — its real effect is
    /// a client-routed remote double-click.)</summary>
    private static void ApplyItemTargetSpell(Item item, SpellDef def)
    {
        switch (def.Id)
        {
            case SpellType.MagicLock:
                if (item.ItemType == ItemType.Container) item.ItemType = ItemType.ContainerLocked;
                else if (item.ItemType == ItemType.Door) item.ItemType = ItemType.DoorLocked;
                break;
            case SpellType.Unlock:
                if (item.ItemType == ItemType.ContainerLocked) item.ItemType = ItemType.Container;
                else if (item.ItemType == ItemType.DoorLocked) item.ItemType = ItemType.Door;
                break;
            case SpellType.MagicTrap:
                item.SetTag("TRAPPED", "1");
                break;
            case SpellType.MagicUntrap:
                item.RemoveTag("TRAPPED");
                break;
        }
    }

    private TriggerResult FireItemSpellEffect(Character caster, Item item, SpellDef def)
    {
        if (TriggerDispatcher == null)
            return TriggerResult.Default;

        return TriggerDispatcher.FireItemTrigger(item, ItemTrigger.SpellEffect, new TriggerArgs
        {
            CharSrc = caster,
            ItemSrc = item,
            O1 = caster,
            N1 = (int)def.Id,
            S1 = def.Name,
        });
    }

    /// <summary>Return true if the caster's backpack holds at least the needed
    /// amount of every reagent the spell requires. Reagents are identified by
    /// BaseId; stacked amounts contribute per-stack.</summary>
    /// <summary>True when the spell's bit is set in any accessible
    /// spellbook (equipped hands or top level of the backpack). Spellbook
    /// content uses the classic 64-bit mask in More1/More2.</summary>
    internal bool HasSpellInBook(Character caster, int spellId)
    {
        ulong bit = 1UL << (spellId - 1);
        foreach (var book in EnumerateSpellbooks(caster))
        {
            ulong bits = ((ulong)book.More2 << 32) | book.More1;
            if ((bits & bit) != 0)
                return true;
        }
        return false;
    }

    private IEnumerable<Item> EnumerateSpellbooks(Character caster)
    {
        var oneHand = caster.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == ItemType.Spellbook)
            yield return oneHand;
        var twoHand = caster.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == ItemType.Spellbook)
            yield return twoHand;
        if (caster.Backpack != null)
        {
            foreach (var item in caster.Backpack.Contents)
            {
                if (item.ItemType == ItemType.Spellbook)
                    yield return item;
            }
        }
    }

    /// <summary>Whether the caster can pay the spell's reagent cost right now.
    /// Searches the whole reachable pack, sub-bags included: Source-X
    /// CContainer::ContentConsume recurses through every searchable container, and
    /// SphereNet's own gold and crafting counts already do. Looking only at the top
    /// level meant a player who tidied their reagents into a pouch was told they
    /// lacked them.</summary>
    private bool HasRequiredReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return true;
        if (caster.Backpack == null) return false;
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            if (CountReagent(caster, regBaseId, needed) < needed)
                return false;
        }
        return true;
    }

    private int CountReagent(Character caster, ushort regBaseId, int stopAt)
    {
        int have = 0;
        foreach (var item in _world.GetContainerContentsRecursive(caster.Backpack!.Uid))
        {
            if (item.IsDeleted) continue;
            if (item.BaseId != regBaseId) continue;
            have += Math.Max(1, (int)item.Amount);
            if (have >= stopAt) break;
        }
        return have;
    }

    /// <summary>
    /// Deduct the spell's reagent cost. Returns false and consumes NOTHING when any
    /// reagent is short.
    ///
    /// This used to return void and trust a check made when the cast started. A cast
    /// takes time, and inventory moves during it: taking the reagents out of the pack
    /// mid-cast produced the spell for free. Source-X re-runs Spell_CanCast with
    /// fTest=false at Spell_CastDone (CCharSpell.cpp:3009) and fails the whole cast
    /// if it cannot pay, which is what this reproduces - the availability pass runs
    /// over every reagent before a single one is taken.
    /// </summary>
    private bool ConsumeReagents(Character caster, SpellDef def)
    {
        if (def.Reagents.Count == 0) return true;
        if (caster.Backpack == null) return false;

        // All-or-nothing: verify the full bill first so a partial spend cannot be
        // left behind when a later reagent turns out to be missing.
        foreach (var (regBaseId, needed) in def.Reagents)
        {
            if (CountReagent(caster, regBaseId, needed) < needed)
                return false;
        }

        foreach (var (regBaseId, needed) in def.Reagents)
        {
            int remaining = needed;
            // Snapshot — we mutate items/delete, can't iterate live collection.
            var stacks = _world.GetContainerContentsRecursive(caster.Backpack.Uid).ToList();
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
        return true;
    }

    /// <summary>AOS on-hit weapon proc (Source-X Fight_Hit → OnSpellEffect,
    /// CCharFight.cpp:2347-2360): applies the spell's effect directly on the
    /// victim — no cast time, mana or reagents — at the attacker's Magery.</summary>
    public void ApplyOnHitSpell(Character attacker, Character target, SpellType spell)
    {
        var def = GetSpellDef(spell);
        if (def == null)
            return;
        ApplyCharEffect(attacker, target, def, attacker.GetSkill(SkillType.Magery));
    }

    /// <summary>Source-X character SPELLEFFECT verb: apply a spell immediately
    /// with the supplied skill level, without cast-time/resource checks.</summary>
    public bool ApplyScriptSpellEffect(Character caster, Character target, SpellType spell, int skillLevel)
    {
        var def = GetSpellDef(spell);
        if (def == null) return false;
        ApplyCharEffect(caster, target, def, Math.Clamp(skillLevel, 0, ushort.MaxValue));
        return true;
    }

    private void ConsumeMagicReflection(Character character)
    {
        character.ClearStatFlag(StatFlag.Reflection);
        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            if (_activeEffects[i].Target == character &&
                _activeEffects[i].Spell == SpellType.MagicReflect)
            {
                DetachSpellMemory(_activeEffects[i]);
                _activeEffects.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>Apply spell effect to a single character target.</summary>
    /// <summary>Source-X CChar::OnSpellEffect entry for non-cast deliveries —
    /// potions (Use_Drink conveys the MORE1 spell at MORE2 strength), traps,
    /// scripted effects. Applies the spell's effect directly at the given
    /// strength with no cast time, mana, reagents or fizzle.</summary>
    public void ApplyDirectEffect(Character source, Character target, SpellType spell, int strength)
    {
        var def = _spells.Get(spell);
        if (def == null) return;
        ApplyCharEffect(source, target, def, Math.Max(0, strength));
    }

    private void ApplyCharEffect(Character caster, Character target, SpellDef def, int skillLevel)
    {
        if (def.Id is SpellType.Lightning or SpellType.ChainLightning)
            _world.LightFlash(target.Position);

        // Magic Reflect: the first harmful spell targeted at a mobile with
        // the Reflection flag is bounced back to the caster. Matches
        // Source-X Magic_Reflect and ServUO MagicReflectSpell behaviour:
        // the flag is single-use — consumed as soon as a harmful spell
        // hits, so the reflected spell itself will NOT be re-reflected
        // by the original caster (even if they also have Reflection up).
        bool harmful = IsHarmfulSpell(def);

        // A harmful spell on a player who is innocent FROM THE CASTER'S VIEW is a
        // crime (Source-X notoriety) — the caster goes grey, like a melee attack.
        // A target the caster holds SawCrime / HarmedBy of (one who struck first,
        // or whose crime the caster witnessed) is not innocent to them, so the
        // spell is self-defence rather than a crime. Checked before the reflect
        // swap so it credits the real aggressor.
        bool targetInnocentToCaster = !target.IsFlaggedAsCriminal &&
            caster.Memory_FindObjTypes(target.Uid, MemoryType.SawCrime | MemoryType.HarmedBy) == null;
        // COMBAT_ATTACK_NOAGGREIVED skips the aggrieved-based criminal marking
        // (Source-X OnAttackedBy is the single choke point for melee and
        // spells alike; SphereNet gates both sites with the same flag).
        if (harmful && caster != target && caster.IsPlayer && target.IsPlayer &&
            Character.AttackingIsACrimeEnabled && targetInnocentToCaster &&
            (Character.CombatFlags & (int)Combat.CombatFlags.AttackNoAggreived) == 0)
            caster.MakeCriminal();

        if (harmful && caster != target && target.IsStatFlag(StatFlag.Reflection))
        {
            ConsumeMagicReflection(target);

            if (caster.IsStatFlag(StatFlag.Reflection) &&
                !IsMagicFlag(MagicConfigFlags.NoReflectOwn))
            {
                // Both sides reflect: consume both charges and let the spell
                // land on its original target (Source-X bounce-back chain).
                ConsumeMagicReflection(caster);
            }
            else if (caster.IsStatFlag(StatFlag.Reflection) &&
                     IsMagicFlag(MagicConfigFlags.DeleteReflectOwn))
            {
                // NOREFLECTOWN + DELREFLECTOWN: the caster's charge absorbs the
                // bounced spell instead of damaging the caster.
                ConsumeMagicReflection(caster);
                return;
            }
            else
            {
                // Normal reflection recursively affects the original caster
                // with itself as SRC. Keeping caster unchanged preserves
                // Source-X damage attribution and prevents a second reflect.
                target = caster;
            }
        }

        int effect = def.GetEffect(skillLevel);

        // Randomize potency (Source-X: iSkillLevel/2 + rand(iSkillLevel/2))
        int potency = skillLevel / 2 + _rand.Next(Math.Max(1, skillLevel / 2));
        effect = def.GetEffect(potency);

        // Mind Blast: damage = (casterINT - targetINT)/2, capped at the victim's
        // STR/2; if the caster is less intelligent the spell rebounds onto them.
        if (def.Id == SpellType.MindBlast)
        {
            int diff = (caster.Int - target.Int) / 2;
            if (diff < 0) { target = caster; diff = -diff; }
            effect = Math.Min(diff, Math.Max(1, target.Str / 2));
        }
        // EvalInt scales offensive spell potency (Source-X spell-damage formula).
        else if (def.IsFlag(SpellFlag.Damage))
        {
            effect += effect * caster.GetSkill(SkillType.EvalInt) / 1000;
        }

        // Magic resist percent — computed BEFORE the trigger so a script can
        // read and override it (LOCAL.Resist), applied after.
        int resistPct = 0;
        if (def.IsFlag(SpellFlag.Resist) && caster != target)
            resistPct = CalcMagicResist(target, def, caster);

        // @SpellEffect — Source-X CChar::OnSpellEffect fires this on the
        // AFFECTED char (not the caster) with SRC = caster, ARGN1 = spell,
        // ARGN2 = skill level, and the LOCAL contract (Effect / Resist /
        // Duration / Sound / DamageType / CreateObject1 / Explode). RETURN 1
        // cancels the effect on this target; Effect / Resist / Duration
        // mutations are read back. The per-spell [SPELL] @EFFECT stage runs
        // right after with the same shared args, as in the reference.
        if (TriggerDispatcher != null)
        {
            var fxLocals = new SphereNet.Scripting.Variables.VarMap();
            int defDurationTenths = def.GetDuration(caster.GetSkill(def.GetPrimarySkill()));
            fxLocals.SetInt("DamageType", 0);
            fxLocals.SetInt("CreateObject1", def.EffectId);
            fxLocals.SetInt("Explode", 0);
            fxLocals.SetInt("Sound", def.Sound);
            fxLocals.SetInt("Effect", effect);
            fxLocals.SetInt("Resist", resistPct);
            fxLocals.SetInt("Duration", defDurationTenths);
            var fxArgs = new TriggerArgs
            {
                CharSrc = caster,
                N1 = (int)def.Id,
                N2 = skillLevel,
                Locals = fxLocals,
            };
            if (TriggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffect, fxArgs) == TriggerResult.True)
                return;
            if (TriggerDispatcher.FireSpellTrigger(def.Id, "Effect", target, fxArgs) == TriggerResult.True)
                return;

            effect = (int)fxLocals.GetInt("Effect", effect);
            resistPct = (int)fxLocals.GetInt("Resist", resistPct);
            long durOverride = fxLocals.GetInt("Duration", defDurationTenths);
            _durationOverrideTenths = durOverride != defDurationTenths && durOverride > 0
                ? (int)durOverride : null;
        }

        try
        {
            ApplyCharEffectResolved(caster, target, def, effect, resistPct);
        }
        finally
        {
            _durationOverrideTenths = null;
        }
    }

    /// <summary>Post-trigger application: resist subtraction + the per-flag
    /// dispatch. Split out so the @SpellEffect duration override is scoped
    /// with try/finally around every ScheduleEffectExpiry call.</summary>
    private void ApplyCharEffectResolved(Character caster, Character target, SpellDef def, int effect, int resistPct)
    {
        if (resistPct > 0)
            effect -= effect * resistPct / 100;

        // Sphere custom spells (1000+) with a native char handler dispatch by
        // id FIRST: their pack defs carry marker flags only — Hallucination
        // even carries spellflag_curse — so the generic flag branches would
        // mis-route them. Fire Bolt is NOT in this set: it is a plain damage
        // spell and takes the generic damage path below.
        if (HasNativeCustomCharHandler(def.Id))
        {
            ApplySpecificSpell(caster, target, def, effect);
            return;
        }

        // Damage spells
        if (def.IsFlag(SpellFlag.Damage))
        {
            var dmgType = GetSpellDamageType(def.Id);
            int damage = Math.Max(0, effect);
            // Apply elemental resist
            if (!IsMagicFlag(MagicConfigFlags.IgnoreArmor))
                damage = CombatEngine.ApplyElementalResist(target, damage, dmgType);

            // COMBAT_SLAYER on the magic path (Source-X OnTakeDamage with
            // DAMAGE_MAGIC, CCharFight.cpp:824): the slayer source is the
            // equipped spellbook, else the wielded weapon; the talisman
            // fallback lives inside ApplySlayerDamage.
            if (damage > 0 && (Character.CombatFlags & (int)Combat.CombatFlags.Slayer) != 0)
            {
                var oneHand = caster.GetEquippedItem(Layer.OneHanded);
                var twoHand = caster.GetEquippedItem(Layer.TwoHanded);
                var slayerSource = oneHand?.ItemType == ItemType.Spellbook ? oneHand
                    : twoHand?.ItemType == ItemType.Spellbook ? twoHand
                    : oneHand ?? twoHand;
                damage = CombatEngine.ApplySlayerDamage(caster, target, damage, slayerSource);
            }

            if (damage > 0 && !CombatEngine.IsDamageImmune(target))
            {
                target.Hits -= (short)Math.Min(damage, short.MaxValue);
                target.RecordAttack(caster.Uid, damage);

                // Reactive Armor reflects a quarter of the damage back at the
                // caster, the same as the melee path (previously melee-only).
                // Through the shared reflect entry so an invulnerable caster is not
                // damaged by its own victim (Source-X routes reflection back through
                // OnTakeDamage, whose Invul gate applies).
                if (target.IsStatFlag(StatFlag.Reactive) && caster != target && !caster.IsDead)
                    CombatEngine.ApplyReflectedDamage(caster, target, Math.Max(1, damage / 4));

                TryInterruptFromDamage(target, damage);

                // Victim feedback: spell damage used to apply silently — no
                // 0x0B damage number, no health-bar update — while the melee
                // and breath paths broadcast both. Without these the hit is
                // invisible until the victim dies.
                Character.BroadcastDamageNearby?.Invoke(target.Position, 18, target.Uid.Value, damage, 0);
                Character.BroadcastNearby?.Invoke(target.Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                        target.Uid.Value, target.MaxHits, target.Hits), 0);

                if (target.Hits <= 0 && !target.IsDead)
                {
                    if (OnTargetKilled != null)
                        OnTargetKilled.Invoke(target, caster);
                    else if (Character.OnLifecycleKill != null)
                        Character.OnLifecycleKill(target, caster);
                    else
                        target.Kill();
                }
            }
        }
        // Heal spells. Noble Sacrifice carries spellflag_heal in the pack but
        // has a NATIVE handler (area heal+cure at the paladin's expense) —
        // the generic branch used to swallow it into a plain caster heal.
        else if (def.IsFlag(SpellFlag.Heal) && def.Id != SpellType.NobleSacrifice)
        {
            caster.FlagForHelpingCriminalIfNeeded(target);
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
        int range = Math.Min(8, 3 + skillLevel / 300);
        bool harmful = IsHarmfulSpell(def);
        foreach (var target in _world.GetCharsInRange(center, range))
        {
            if (target == caster && (harmful || def.IsFlag(SpellFlag.TargNoSelf))) continue;
            if (target.IsDead && !def.IsFlag(SpellFlag.TargDead)) continue;

            ApplyCharEffect(caster, target, def, skillLevel);
        }
    }

    /// <summary>Create a multi-tile field wall centred on the target location,
    /// oriented perpendicular to the cast direction (UO-style 5-tile wall).</summary>
    /// <summary>The classic field tile pair per spell (Source-X Spell_CastDone
    /// CreateObject1/CreateObject2 defaults): E/W art when the wall runs
    /// east-west, N/S art when it runs north-south.</summary>
    private static (ushort EW, ushort NS) FieldTiles(SpellType spell) => spell switch
    {
        SpellType.WallOfStone => (0x0080, 0x0080),   // ITEMID_STONE_WALL
        SpellType.FireField => (0x398C, 0x3996),     // ITEMID_FX_FIRE_F_EW/NS
        SpellType.PoisonField => (0x3915, 0x3920),   // ITEMID_FX_POISON_F_EW/NS
        SpellType.ParalyzeField => (0x3967, 0x3979), // ITEMID_FX_PARA_F_EW/NS
        SpellType.EnergyField => (0x3946, 0x3956),   // ITEMID_FX_ENERGY_F_EW/NS
        _ => (0, 0),
    };

    /// <summary>Source-X CChar::Spell_Field: a 5-tile wall perpendicular to
    /// the caster→target axis using the CLASSIC field art per spell (the
    /// pack's EFFECT_ID is the cast FX, not the field tile — Wall of
    /// Stone/Fire/Energy carry EFFECT_ID=0, which used to make the field
    /// items invisible). Duration comes from the spell's DURATION curve;
    /// barrier fields (stone wall, energy) refuse to materialise on top of a
    /// character; each segment records its spell for the typed step effect.</summary>
    private void CreateField(Character caster, Point3D pos, SpellDef def)
    {
        int skill = caster.GetSkill(def.GetPrimarySkill());
        int dmg = def.GetEffect(skill);

        // Orient the wall perpendicular to the caster→target axis.
        int dx = pos.X - caster.X;
        int dy = pos.Y - caster.Y;
        bool wallRunsNorthSouth = Math.Abs(dx) >= Math.Abs(dy); // facing E/W → N-S wall

        var (tileEW, tileNS) = FieldTiles(def.Id);
        ushort tileId = wallRunsNorthSouth ? tileNS : tileEW;
        if (tileId == 0)
            tileId = def.EffectId; // custom scripted field spells keep EFFECT_ID

        int durTenths = def.GetDuration(skill);
        long durMs = durTenths > 0 ? durTenths * 100L : 30_000L;

        bool isBarrier = def.Id is SpellType.WallOfStone or SpellType.EnergyField;
        // Poison strength is fixed at cast time from the caster's skill
        // (Source-X field poisoning level), consumed by the step effect.
        byte poisonLevel = skill switch { >= 800 => 4, >= 600 => 3, >= 400 => 2, _ => 1 };

        for (int offset = -2; offset <= 2; offset++)
        {
            int tx = wallRunsNorthSouth ? pos.X : pos.X + offset;
            int ty = wallRunsNorthSouth ? pos.Y + offset : pos.Y;
            var tilePos = new Point3D((short)tx, (short)ty, pos.Z, pos.Map);

            // Don't lay a field segment on a blocked tile.
            var md = _world.MapData;
            if (md != null && !md.IsPassable(tilePos.Map, tilePos.X, tilePos.Y, tilePos.Z))
                continue;

            // Source-X: stone/energy walls never materialise over a character.
            if (isBarrier && _world.GetCharsInRange(tilePos, 0)
                    .Any(c => c.X == tx && c.Y == ty && !c.IsDead))
                continue;

            var fieldItem = _world.CreateItem();
            fieldItem.BaseId = tileId;
            fieldItem.Name = def.Name + " field";
            // A field segment is a spell manifestation, not a world item: it
            // can never be picked up (Source-X ATTR_MOVE_NEVER on fields) and
            // is typed IT_FIRE / IT_SPELL like the reference.
            fieldItem.ItemType = def.Id == SpellType.FireField ? ItemType.Fire : ItemType.Spell;
            fieldItem.SetAttr(ObjAttributes.Move_Never);
            fieldItem.SetTag("FIELD_CASTER", caster.Uid.Value.ToString());
            fieldItem.SetTag("FIELD_CASTER_UUID", caster.Uuid.ToString("D"));
            fieldItem.SetTag("FIELD_SPELL", ((int)def.Id).ToString());
            if (def.Id == SpellType.PoisonField)
                fieldItem.SetTag("FIELD_POISON", poisonLevel.ToString());
            // Flat step damage only for damage-flagged fields (fire); the
            // typed effects (poison/paralyze) and barriers carry none.
            if (def.IsFlag(SpellFlag.Damage) && dmg > 0)
                fieldItem.SetTag("FIELD_DAMAGE", dmg.ToString());
            fieldItem.DecayTime = Environment.TickCount64 + durMs;
            if (!_world.PlaceItem(fieldItem, tilePos))
                _world.RemoveItem(fieldItem);
        }
    }

    /// <summary>Harming an innocent player with a field is a crime — the same
    /// notoriety rule the direct harmful-spell path applies (Source-X routes
    /// field hits through OnAttackedBy).</summary>
    private static void MarkFieldCrime(Character? caster, Character victim)
    {
        if (caster == null || caster == victim || !caster.IsPlayer || !victim.IsPlayer)
            return;
        if (!Character.AttackingIsACrimeEnabled || victim.IsFlaggedAsCriminal)
            return;
        if (caster.Memory_FindObjTypes(victim.Uid,
                MemoryType.SawCrime | MemoryType.HarmedBy) != null)
            return; // self-defence
        if ((Character.CombatFlags & (int)Combat.CombatFlags.AttackNoAggreived) != 0)
            return;
        caster.MakeCriminal();
    }

    /// <summary>Typed field step/stand effect (Source-X: walking into or
    /// standing in a field triggers the field's spell).
    ///
    /// <see cref="FieldTouchResult.NotHandled"/> lets the caller's legacy
    /// flat-damage path run (script-made fields with only FIELD_DAMAGE). The
    /// other two answers both mean "consumed here", and differ only in whether an
    /// effect actually landed — which is what caps the number of spell fields one
    /// step may set off.</summary>
    public FieldTouchResult ApplyFieldTouch(Character ch, Item field)
    {
        if (ch.IsDead) return FieldTouchResult.Handled;
        if (!field.TryGetTag("FIELD_SPELL", out string? fsStr) ||
            !int.TryParse(fsStr, out int fsId))
            return FieldTouchResult.NotHandled;

        Character? caster = null;
        if (field.TryGetTag("FIELD_CASTER", out string? cStr) && uint.TryParse(cStr, out uint cuid))
            caster = _world.FindChar(new Serial(cuid));

        var spellType = (SpellType)fsId;

        // Source-X runs a field touch through OnSpellEffect, whose harmful branch
        // turns an invulnerable target away BEFORE anything is applied, and answers
        // false so the cap below stays clear for the next field
        // (CCharSpell.cpp:3762). Fire tested its own immunity; poison and paralyze
        // did not, so an invulnerable character took no damage and yet was still
        // poisoned - timer, source UID and status notification and all - and still
        // frozen. Both field defs carry SPELLFLAG_HARM in the reference pack, so
        // the flag is read from the def rather than the type being listed here.
        if (ch.IsStatFlag(StatFlag.Invul) &&
            (_spells.Get(spellType)?.IsFlag(SpellFlag.Harm) ?? false))
            return FieldTouchResult.Handled;

        switch (spellType)
        {
            case SpellType.FireField:
            {
                if (CombatEngine.IsDamageImmune(ch)) return FieldTouchResult.Handled;
                MarkFieldCrime(caster, ch);
                int dmg = field.TryGetTag("FIELD_DAMAGE", out string? dStr) &&
                          int.TryParse(dStr, out int d) ? d : 2;
                dmg = CombatEngine.ApplyElementalResist(ch, Math.Max(1, dmg), DamageType.Fire);
                ch.Hits = (short)Math.Max(0, ch.Hits - dmg);
                if (caster != null && caster != ch)
                    ch.RecordAttack(caster.Uid, dmg);
                TryInterruptFromDamage(ch, dmg);
                if (ch.Hits <= 0 && !ch.IsDead)
                {
                    if (OnTargetKilled != null) OnTargetKilled.Invoke(ch, caster!);
                    else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, caster);
                    else ch.Kill();
                }
                return FieldTouchResult.SpellHit;
            }
            case SpellType.PoisonField:
            {
                MarkFieldCrime(caster, ch);
                byte level = field.TryGetTag("FIELD_POISON", out string? pStr) &&
                             byte.TryParse(pStr, out byte p) ? p : (byte)1;
                // The caster rides along as the poison SOURCE so a poison
                // death credits the kill/crime to the right character.
                ch.ApplyPoison(level, caster?.Uid ?? Serial.Invalid);
                return FieldTouchResult.SpellHit;
            }
            case SpellType.ParalyzeField:
            {
                // The engine's Paralyze handler gives the timed freeze with
                // proper expiry; skip when already frozen.
                if (!ch.IsStatFlag(StatFlag.Freeze))
                {
                    MarkFieldCrime(caster, ch);
                    ApplyDirectEffect(caster ?? ch, ch, SpellType.Paralyze, 300);
                }
                // Counts as the step's one spell hit even when the victim was already
                // held: the cap exists precisely to stop a Paralyze+Fire stack from
                // re-freezing at every damage tick (CCharAct.cpp:5000).
                return FieldTouchResult.SpellHit;
            }
            case SpellType.WallOfStone:
            case SpellType.EnergyField:
                // A barrier lands no effect, so it must not swallow a real field
                // sharing the tile.
                return FieldTouchResult.Handled; // passage is blocked by the tiledata
            default:
                return FieldTouchResult.NotHandled;
        }
    }


    /// <summary>Build the summon a completed cast calls for, before any of its cost
    /// is taken. Returns null when it may not be summoned, having already said why.
    ///
    /// Source-X orders it this way deliberately: Spell_Summon_Try runs at
    /// CCharSpell.cpp:3002 and the consumption only at :3010.</summary>
    private Character? PrepareSummon(Character caster, Point3D targetPos, SpellDef def,
        SpellType spell, int skillLevel)
    {
        // sm_summon menu pick stashed on the caster (Source-X
        // m_atMagery.m_uiSummonID): the COMPLETED cast conveys the chosen
        // creature — the menu no longer bypasses mana/reagents/cast time.
        string? summonSel = null;
        if (caster.TryGetTag("SUMMON_SELECT", out string? sel) &&
            !string.IsNullOrWhiteSpace(sel))
            summonSel = sel.Trim();
        caster.RemoveTag("SUMMON_SELECT");

        ushort summonBody = spell switch
        {
            SpellType.SummonFamiliar => 0x013D,
            SpellType.BladeSpirit => 0x023E,     // CREID_BLADE_SPIRIT
            SpellType.EnergyVortex => 0x00A4,    // CREID_ENERGY_VORTEX
            SpellType.AirElemental => 0x000D,    // CREID_AIR_ELEM
            SpellType.EarthElemental => 0x000E,
            SpellType.FireElemental => 0x000F,
            SpellType.WaterElemental => 0x0010,
            SpellType.SummonDaemon => 0x0009,    // CREID_DEMON
            SpellType.RisingColossus => 0x033D,  // CREID_RISING_COLOSSUS
            SpellType.AnimatedWeapon => 0x02B4,  // CREID_ANIMATED_WEAPON
            // Sphere custom Summon Undead: 1/15 lich, 4/15 skeleton,
            // else zombie (Source-X CCharSpell.cpp:2591).
            SpellType.SummonUndead => _rand.Next(15) switch
            {
                1 => (ushort)0x0018,             // CREID_LICH
                3 or 5 or 7 or 9 => (ushort)0x0032, // CREID_SKELETON
                _ => (ushort)0x0003,             // CREID_ZOMBIE
            },
            _ => 0,
        };
        return SummonCreature(caster, targetPos, def, skillLevel, summonBody, summonSel);
    }

    /// <summary>Take back a summon whose cast could not be paid for after all - the
    /// reference deletes it on the same failure (CCharSpell.cpp:3012).</summary>
    private void DiscardSummon(Character? summoned)
    {
        if (summoned == null) return;
        summoned.ClearOwnership(clearFriends: true);
        _world.DeleteObject(summoned);
        summoned.Delete();
    }

    /// <summary>Summon a creature at target location.</summary>
    private Character? SummonCreature(Character caster, Point3D pos, SpellDef def, int skillLevel,
        ushort bodyId = 0, string? defName = null)
    {
        if (IsMagicFlag(MagicConfigFlags.LimitSummons))
        {
            int activeSummons = 0;
            foreach (var ch in _world.GetCharsInRange(caster.Position, 24))
            {
                if (ch.IsSummoned && ch.OwnerSerial == caster.Uid)
                    activeSummons++;
            }
            if (activeSummons >= 2)
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
                return null;
            }
        }

        var creature = _world.CreateCharacter();
        creature.Name = def.Name;
        creature.NpcBrain = NpcBrainType.Monster;
        // A specific creature graphic (necro summons pass one); a conjured summon
        // is dispellable (reference STATF_Conjured).
        if (bodyId != 0)
        {
            creature.BodyId = bodyId;
            creature.BaseId = bodyId;
            creature.SetStatFlag(StatFlag.Conjured);

            // Source-X summons take the creature's own chardef (NPC_LoadScript
            // runs its @Create for stats/skills) — never flat invented numbers.
            var cdef = Definitions.DefinitionLoader.GetCharDef(bodyId);
            if (cdef != null)
            {
                creature.CharDefIndex = bodyId;
                if (!string.IsNullOrWhiteSpace(cdef.Name))
                    creature.Name = Definitions.DefinitionLoader.ResolveNames(cdef.Name);
                if (cdef.NpcBrain != NpcBrainType.None)
                    creature.NpcBrain = cdef.NpcBrain;
                Definitions.CharDefHelper.AfterApplyDefName?.Invoke(creature); // chardef @Create
                creature.Hits = creature.MaxHits;
                creature.Stam = creature.MaxStam;
                creature.Mana = creature.MaxMana;
            }
        }

        // A menu pick has to be applied BEFORE the follower cap is weighed. Source-X
        // creates the summon from the chosen id - CreateBasic(m_atMagery.m_uiSummonID),
        // CCharSpell.cpp:2640 - and only then measures it with GetFollowerSlots()
        // (:2662). Applying the pick afterwards meant the cap saw the placeholder's
        // single slot, so a five-slot creature walked through a one-slot allowance and
        // left the owner permanently over their limit.
        if (defName != null)
        {
            var res = Definitions.DefinitionLoader.StaticResources;
            if (res != null &&
                Definitions.CharDefHelper.TryApplyDefName(creature, defName, res, refresh: false))
            {
                creature.SetStatFlag(StatFlag.Conjured);
                creature.Hits = creature.MaxHits;
                creature.Stam = creature.MaxStam;
                creature.Mana = creature.MaxMana;
            }
        }

        int duration = def.GetDuration(skillLevel);
        if (!creature.TryAssignOwnership(caster, caster, summoned: true, enforceFollowerCap: true))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.PetslotsTrySummon));
            _world.DeleteObject(creature);
            creature.Delete();
            return null;
        }
        creature.SetTag("SUMMON_DURATION", duration.ToString());
        creature.SetTag("SUMMON_MASTER", caster.Uid.Value.ToString());
        creature.SetTag("SUMMON_MASTER_UUID", caster.Uuid.ToString("D"));
        creature.SetTag("SUMMON_EXPIRE_TICK", (Environment.TickCount64 + duration * 100L).ToString());

        if (!_world.PlaceCharacter(creature, pos))
        {
            creature.ClearOwnership(clearFriends: true);
            _world.DeleteObject(creature);
            creature.Delete();
            return null;
        }
        return creature;
    }

    /// <summary>
    /// Calculate magic resistance. Returns resist percentage (0-50).
    /// Graduated scaling: higher MR vs spell difficulty = more reduction.
    /// Also triggers MR skill gain via UseQuick.
    /// </summary>
    private int CalcMagicResist(Character target, SpellDef def, Character caster)
    {
        // Reference resist roll (Spell_CastDone): a quick MagicResistance
        // check against a chance derived from resist skill vs caster magery
        // and the spell id; success absorbs a flat 25% of the effect,
        // failure absorbs nothing.
        int chance = CalcResistChance(
            target.GetSkill(SkillType.MagicResistance),
            caster.GetSkill(SkillType.Magery),
            (int)def.Id);
        return SkillEngine.UseQuick(target, SkillType.MagicResistance, chance) ? 25 : 0;
    }

    /// <summary>Reference resist-chance formula:
    /// max(resist/50, resist - ((magery-200)/50 + (1 + spell/8) * 50)) / 30.</summary>
    internal static int CalcResistChance(int resistSkill, int casterMagery, int spellId)
    {
        int first = resistSkill / 50;
        int second = resistSkill - (((casterMagery - 200) / 50) + (1 + spellId / 8) * 50);
        return Math.Max(first, second) / 30;
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
        short bonus = (short)Math.Max(1, effect / 5);
        switch (def.Id)
        {
            case SpellType.Strength:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, bonus);
                eff.StrDelta = bonus; target.Str += bonus;
                break;
            }
            case SpellType.Agility:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, bonus);
                eff.DexDelta = bonus; target.Dex += bonus;
                break;
            }
            case SpellType.Cunning:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, bonus);
                eff.IntDelta = bonus; target.Int += bonus;
                break;
            }
            case SpellType.Bless:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, bonus);
                eff.StrDelta = bonus; eff.DexDelta = bonus; eff.IntDelta = bonus;
                target.Str += bonus; target.Dex += bonus; target.Int += bonus;
                break;
            }
            case SpellType.Protection:
            case SpellType.ArchProtection:
                ApplyProtectionWard(caster, target, def, effect);
                break;
        }
    }

    private void ApplyProtectionWard(Character caster, Character target, SpellDef def, int effect)
    {
        var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
        eff.ArmorDelta = Math.Max(0, effect);
        target.ProtectionArmor = (int)Math.Min(
            int.MaxValue, (long)target.ProtectionArmor + eff.ArmorDelta);
        eff.AppliedFlag = StatFlag.ArcherCanMove;
        target.SetStatFlag(StatFlag.ArcherCanMove);
    }

    private void ApplyCurse(Character caster, Character target, SpellDef def, int effect)
    {
        short penalty = (short)Math.Max(1, effect / 5);
        switch (def.Id)
        {
            case SpellType.Weaken:
            {
                short actual = (short)Math.Min(penalty, target.Str - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, actual);
                eff.StrDelta = (short)-actual; target.Str -= actual;
                break;
            }
            case SpellType.Clumsy:
            {
                short actual = (short)Math.Min(penalty, target.Dex - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, actual);
                eff.DexDelta = (short)-actual; target.Dex -= actual;
                break;
            }
            case SpellType.Feeblemind:
            {
                short actual = (short)Math.Min(penalty, target.Int - 1);
                if (actual <= 0) break;
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def, actual);
                eff.IntDelta = (short)-actual; target.Int -= actual;
                break;
            }
            // Mass Curse is the area variant; it is flagged Curse and routes here,
            // so it applies the same all-stat penalty per target.
            case SpellType.Curse:
            case SpellType.MassCurse:
            {
                short strP = (short)Math.Min(penalty, target.Str - 1);
                short dexP = (short)Math.Min(penalty, target.Dex - 1);
                short intP = (short)Math.Min(penalty, target.Int - 1);
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def,
                    Math.Max(0, Math.Max((int)strP, Math.Max((int)dexP, (int)intP))));
                if (strP > 0) { eff.StrDelta = (short)-strP; target.Str -= strP; }
                if (dexP > 0) { eff.DexDelta = (short)-dexP; target.Dex -= dexP; }
                if (intP > 0) { eff.IntDelta = (short)-intP; target.Int -= intP; }
                break;
            }
        }
    }

    private bool IsItemAccessible(Character ch, Item item)
    {
        if (ch.PrivLevel >= PrivLevel.GM) return true;
        if (!item.ContainedIn.IsValid)
            return ch.Position.GetDistanceTo(item.Position) <= 2;
        var current = item;
        for (int depth = 0; depth < 16 && current.ContainedIn.IsValid; depth++)
        {
            if (current.ContainedIn == ch.Uid) return true;
            var parent = _world?.FindItem(current.ContainedIn);
            if (parent == null) break;
            current = parent;
        }
        return false;
    }

    /// <summary>A corpse targeted by Animate Dead / Bone Armor must be a
    /// TOP-LEVEL world item (Source-X pCorpse-&gt;IsTopLevel()) on the caster's
    /// map within casting range — a known UID inside a container or across
    /// the world must not be consumable.</summary>
    private static bool CanReachCorpse(Character caster, Item corpse) =>
        !corpse.ContainedIn.IsValid &&
        corpse.Position.Map == caster.MapIndex &&
        caster.Position.GetDistanceTo(corpse.Position) <= 12;

    private bool CanSpellReachItem(Character caster, Item item)
    {
        if (item.IsDeleted)
            return false;
        if (IsItemAccessible(caster, item))
            return true;
        if (!item.ContainedIn.IsValid &&
            item.Position.Map == caster.MapIndex &&
            caster.Position.GetDistanceTo(item.Position) <= 12)
            return true;
        return false;
    }

    private void ApplyRuneTravelSpell(Character caster, Item rune, SpellDef def)
    {
        if (!rune.TryGetRuneMark(out Point3D dest))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
            return;
        }

        // A frozen/jailed/paralyzed or dead caster cannot travel out — otherwise
        // a jailed player could recall straight out of jail.
        if (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
            (caster.IsDead || caster.IsStatFlag(StatFlag.Freeze)))
        {
            OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
            return;
        }

        if (caster.PrivLevel < Core.Enums.PrivLevel.GM)
        {
            var srcRegion = _world.FindRegion(caster.Position);
            var destRegion = _world.FindRegion(dest);
            if (srcRegion != null && srcRegion.NoMagic)
            {
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGenFizzles));
                return;
            }
            if (destRegion != null && destRegion.NoMagic)
            {
                OnSysMessage?.Invoke(caster, "That location blocks magic.");
                return;
            }
            // Source-X anti-magic sub-flags (REGION_ANTIMAGIC_*): RECALL_OUT blocks
            // leaving by recall, RECALL_IN (the Recall flag) blocks arriving by
            // recall/gate, GATE blocks gate travel. The legacy numeric region
            // properties (RECALLOUT/RECALLIN/GATEOUT/GATEIN = 0) still work too.
            if (def.Id == SpellType.Recall)
            {
                if (srcRegion != null && (srcRegion.IsFlag(RegionFlag.RecallOut) ||
                    (srcRegion.TryGetProperty("RECALLOUT", out string? rOut) && rOut == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot recall from here."); return; }
                if (destRegion != null && (destRegion.IsFlag(RegionFlag.Recall) ||
                    (destRegion.TryGetProperty("RECALLIN", out string? rIn) && rIn == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot recall to that location."); return; }
            }
            if (def.Id == SpellType.GateTravel)
            {
                if (srcRegion != null && (srcRegion.IsFlag(RegionFlag.Gate) ||
                    (srcRegion.TryGetProperty("GATEOUT", out string? gOut) && gOut == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot open a gate here."); return; }
                if (destRegion != null && (destRegion.IsFlag(RegionFlag.Recall) || destRegion.IsFlag(RegionFlag.Gate) ||
                    (destRegion.TryGetProperty("GATEIN", out string? gIn) && gIn == "0")))
                { OnSysMessage?.Invoke(caster, "You cannot gate to that location."); return; }
            }
        }

        if (_world.GetSector(dest) == null)
        {
            OnSysMessage?.Invoke(caster, "That location is unreachable.");
            return;
        }

        var md = _world.MapData;
        if (md != null)
        {
            var (mapW, mapH) = md.GetMapSize(dest.Map);
            if (dest.X < 0 || dest.Y < 0 || dest.X >= mapW || dest.Y >= mapH)
            {
                OnSysMessage?.Invoke(caster, "That location is unreachable.");
                return;
            }
            // Don't teleport into a wall/water/impassable static — would leave
            // the traveller stuck. Applies to both Recall and Gate destinations.
            if (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
                !md.IsPassable(dest.Map, dest.X, dest.Y, dest.Z))
            {
                OnSysMessage?.Invoke(caster, "That location is blocked.");
                return;
            }
        }

        if (def.Id == SpellType.Recall)
        {
            byte oldMap = caster.MapIndex;
            if (_world.MoveCharacter(caster, dest))
                OnSpellTeleport?.Invoke(caster, dest, oldMap);
            return;
        }

        var gate = _world.CreateItem();
        gate.BaseId = 0x0F6C; // moongate graphic
        gate.ItemType = ItemType.Moongate;
        gate.Name = "moongate";
        gate.MoreP = dest;
        gate.DecayTime = Environment.TickCount64 + 30_000;
        _world.PlaceItem(gate, caster.Position);

        if (IsMagicFlag(MagicConfigFlags.GateBothSides))
        {
            var returnGate = _world.CreateItem();
            returnGate.BaseId = 0x0F6C;
            returnGate.ItemType = ItemType.Moongate;
            returnGate.Name = "moongate";
            returnGate.MoreP = caster.Position;
            returnGate.DecayTime = Environment.TickCount64 + 30_000;
            _world.PlaceItem(returnGate, dest);
        }

        OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellGateOpen));
    }

    /// <summary>True when the spell sits in the unimplemented-school id space
    /// (201..999: Chivalry/Bushido/Ninjitsu/Spellweaving/Mysticism/masteries)
    /// AND its def provides no behaviour at all, so casting it could only
    /// consume resources and no-op.</summary>
    /// <summary>Flags the effect dispatcher can actually act on. A school
    /// spell whose def carries only marker flags (playeronly, fx, harm, targ)
    /// would cast, consume mana/reagents and silently no-op — Source-X too
    /// damages only on SPELLFLAG_DAMAGE (CCharSpell.cpp:3817), so such a def
    /// is inert there as well.</summary>
    /// <summary>A spell that harms its target. Source-X keys the criminal
    /// marking, Magic Reflect bounce and REGION_ANTIMAGIC_DAMAGE suppression
    /// all off SPELLFLAG_HARM alone (CCharSpell.cpp OnSpellEffect,
    /// CRegion.cpp CheckAntiMagic) — pack spells like Poison, Paralyze and
    /// Mana Vampire carry HARM without DAMAGE/CURSE. Damage/Curse stay in
    /// the union for defs registered without the marker flag.</summary>
    private static bool IsHarmfulSpell(SpellDef def) =>
        (def.Flags & (SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Curse)) != 0;

    private static bool HasActionableFlags(SpellDef def) =>
        // Only flags the engine has a GENERIC handler for. Scripted counts
        // via HasScriptedStages (a bare SCRIPTED flag with no ON= body is
        // dead); Poly and Tick have per-spell handlers only, so a school def
        // carrying just those would still no-op after burning resources.
        (def.Flags & (SpellFlag.Damage | SpellFlag.Heal | SpellFlag.Bless |
                      SpellFlag.Curse | SpellFlag.Field | SpellFlag.Summon |
                      SpellFlag.Area)) != 0;

    /// <summary>School spells with a native SphereNet handler (dispatched in
    /// ApplySpecificSpell / the travel route) — always castable.</summary>
    private static bool HasNativeSchoolHandler(SpellType id) => id is
        SpellType.CleanseByFire or SpellType.CloseWounds or SpellType.DispelEvil or
        SpellType.DivineFury or SpellType.HolyLight or SpellType.NobleSacrifice or
        SpellType.RemoveCurse or SpellType.SacredJourney or SpellType.GiftOfRenewal or
        SpellType.ReaperForm or SpellType.StoneForm;

    /// <summary>Sphere custom spells (1000+) whose char effect dispatches by
    /// id in ApplySpecificSpell — their pack defs carry marker flags only
    /// (Hallucination even carries spellflag_curse), so the generic flag
    /// branches would mis-route them.</summary>
    private static bool HasNativeCustomCharHandler(SpellType id) => id is
        SpellType.Light or SpellType.Hallucination or SpellType.Stone or
        SpellType.Shrink or SpellType.Refresh or SpellType.Restore or
        SpellType.Mana or SpellType.Sustenance or SpellType.GenderSwap or
        SpellType.Trance or SpellType.ParticleForm or SpellType.Shield or
        SpellType.Steelskin or SpellType.Stoneskin or SpellType.Regenerate or
        SpellType.Ale or SpellType.Wine or SpellType.Liquor or
        SpellType.Chameleon or SpellType.BeastForm or SpellType.MonsterForm;

    /// <summary>Every Sphere custom spell (1000+) with native engine behavior
    /// — the char handlers above plus the summon/corpse/item routes (Summon
    /// Undead, Animate Dead, Bone Armor) and plain-damage Fire Bolt.
    /// Chameleon / Beast Form / Monster Form ride the reference's
    /// LAYER_SPELL_Polymorph path. Only Enchant and Forget remain refused —
    /// the Source-X reference has no case for either — unless the pack
    /// scripts them.</summary>
    private static bool HasNativeCustomHandler(SpellType id) =>
        HasNativeCustomCharHandler(id) || id is
        SpellType.SummonUndead or SpellType.AnimateDead or
        SpellType.BoneArmor or SpellType.FireBolt;

    internal static bool IsInertSchoolSpell(SpellDef def)
    {
        int id = (int)def.Id;
        if (id < 201)
            return false; // Magery / Necromancy native id space
        if (HasNativeSchoolHandler(def.Id) || HasNativeCustomHandler(def.Id))
            return false;
        if (def.HasScriptedStages)
            return false; // the pack owns the behavior
        return !HasActionableFlags(def);
    }

    private void ApplySpecificSpell(Character caster, Character target, SpellDef def, int effect)
    {
        switch (def.Id)
        {
            case SpellType.Teleport:
            {
                var dest = caster.CastTargetPos;
                if (dest.X == 0 && dest.Y == 0) break;
                // Validate the destination like Recall/Gate do: off-map or an
                // impassable tile (wall/water/blocking static) would strand the
                // caster. Recall/Gate guarded this; Teleport did not.
                var teleMd = _world.MapData;
                if (_world.GetSector(dest) == null)
                {
                    OnSysMessage?.Invoke(caster, "That location is unreachable.");
                    break;
                }
                if (teleMd != null)
                {
                    var (mapW, mapH) = teleMd.GetMapSize(dest.Map);
                    if (dest.X < 0 || dest.Y < 0 || dest.X >= mapW || dest.Y >= mapH ||
                        (caster.PrivLevel < Core.Enums.PrivLevel.GM &&
                         !teleMd.IsPassable(dest.Map, dest.X, dest.Y, dest.Z)))
                    {
                        OnSysMessage?.Invoke(caster, "That location is unreachable.");
                        break;
                    }
                }
                // REGION_ANTIMAGIC_TELEPORT: can't teleport into this region.
                if (caster.PrivLevel < Core.Enums.PrivLevel.GM)
                {
                    var teleRegion = _world.FindRegion(dest);
                    if (teleRegion != null && (teleRegion.IsFlag(RegionFlag.NoTeleport) || teleRegion.NoMagic))
                    {
                        OnSysMessage?.Invoke(caster, "You cannot teleport to that location.");
                        break;
                    }
                }
                byte oldMap = caster.MapIndex;
                if (_world.MoveCharacter(caster, dest))
                    OnSpellTeleport?.Invoke(caster, dest, oldMap);
                break;
            }
            case SpellType.Recall:
                // Recall/Gate use item rune state (MOREP/TryGetRuneMark).
                // A character target cannot be a valid rune.
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                break;
            case SpellType.Mark:
                // Handled in CastDone before ApplyCharEffect
                break;
            case SpellType.GateTravel:
                OnSysMessage?.Invoke(caster, ServerMessages.Get(Msg.SpellRecallBlank));
                break;
            case SpellType.Cure:
            case SpellType.ArchCure:
                caster.FlagForHelpingCriminalIfNeeded(target);
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
                target.ClearHiddenState();
                break;
            case SpellType.Dispel:
            case SpellType.MassDispel:
                // Source-X Dispel removes the target's dispellable ATTR_MAGIC
                // spell memories (buffs/curses), not only conjured creatures.
                StripDispellableEffects(target);
                DispelConjured(caster, target);
                break;
            case SpellType.Resurrection:
                if (target.IsDead)
                {
                    caster.FlagForHelpingCriminalIfNeeded(target);
                    if (Character.OnLifecycleResurrect != null)
                        Character.OnLifecycleResurrect(target);
                    else
                        target.Resurrect();
                }
                break;
            case SpellType.Poison:
            {
                if (target.IsDead) break;
                byte poisonLvl = caster.GetSkill(SkillType.Magery) switch
                {
                    >= 800 => 4, // deadly
                    >= 600 => 3, // greater
                    >= 400 => 2, // normal
                    _ => 1       // lesser
                };
                // OSI SetPoison distance falloff (CCharAct.cpp:4218): a poison
                // landed from more than 3 tiles away weakens by -dist/2.
                int poisonDist = caster.Position.GetDistanceTo(target.Position);
                if (poisonDist >= 4)
                    poisonLvl = (byte)Math.Max(1, poisonLvl - poisonDist / 2);
                // Necromancy Evil Omen: a poison spell lands one level higher on a
                // marked target, then the omen is spent (reference CCharAct.cpp:4239).
                if (target.ConsumeEvilOmen())
                    poisonLvl = (byte)Math.Min(5, poisonLvl + 1);
                target.ApplyPoison(poisonLvl, caster.Uid);
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
                eff.NewLightLevel = 30;
                eff.LightChanged = true;
                target.SetStatFlag(StatFlag.NightSight);
                target.LightLevel = eff.NewLightLevel;
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
            case SpellType.ArchProtection:
            {
                // Non-Bless custom spell definitions still use the same
                // LAYER_SPELL_Protection-equivalent ward path.
                ApplyProtectionWard(caster, target, def, effect);
                break;
            }
            case SpellType.Incognito:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Incognito;
                // Disguise: hide the real name behind a random alias (restored
                // on expiry). Source-X also randomizes skin/hair; name is the
                // visible part other players key off.
                eff.OldName = target.Name;
                eff.NewName = s_incognitoNames[_rand.Next(s_incognitoNames.Length)];
                eff.NameChanged = true;
                target.Name = eff.NewName;
                target.SetStatFlag(StatFlag.Incognito);
                Character.OnAppearanceChanged?.Invoke(target);
                break;
            }
            case SpellType.MagicReflect:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Reflection;
                target.SetStatFlag(StatFlag.Reflection);
                break;
            }
            case SpellType.HorrificBeast:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Polymorph;
                target.HorrificBeastActive = true;
                target.SetStatFlag(StatFlag.Polymorph);
                break;
            }
            case SpellType.WraithForm:
            {
                // Necromancy Wraith Form (reference SPELL_Wraith_Form): a
                // LAYER_SPELL_Polymorph form. While active, damaging hits drain
                // the target's mana (see CombatEngine.ApplyAosOnHitEffects).
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Polymorph;
                target.WraithFormActive = true;
                target.SetStatFlag(StatFlag.Polymorph);
                break;
            }
            case SpellType.LichForm:
            {
                // Necromancy Lich Form (reference SPELL_Lich_Form): a
                // LAYER_SPELL_Polymorph form that shifts elemental resists.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Polymorph;
                target.LichFormActive = true;
                target.SetStatFlag(StatFlag.Polymorph);
                ApplyLichFormResists(target, +1);
                break;
            }
            case SpellType.VampiricEmbrace:
            {
                // Necromancy Vampiric Embrace (reference SPELL_Vampiric_Embrace):
                // a form that leeches life on every hit (see ApplyAosOnHitEffects)
                // and lowers fire resist.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Polymorph;
                target.VampiricEmbraceActive = true;
                target.SetStatFlag(StatFlag.Polymorph);
                ApplyVampiricResists(target, +1);
                break;
            }
            case SpellType.CurseWeapon:
            {
                // Necromancy Curse Weapon (reference SPELL_Curse_Weapon): stores
                // m_spelllevel (EFFECT curve, 10-15) that is added to the wielded
                // weapon's HITLEECHLIFE percent on hit.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                int level = Math.Clamp(effect, 1, 100);
                eff.CurseWeaponLevel = level;
                target.CurseWeaponLevel = level;
                break;
            }
            case SpellType.CorpseSkin:
            {
                // Necromancy Corpse Skin (reference SPELL_Corpse_Skin): fire/poison
                // resist down, cold/physical resist up, for a Spirit-Speak duration.
                ScheduleEffectExpiry(caster, target, def.Id, def);
                ApplyCorpseSkinResists(target, +1);
                break;
            }
            case SpellType.MindRot:
            {
                // Necromancy Mind Rot (reference SPELL_Mind_Rot): raises the
                // target's spell mana cost while active.
                ScheduleEffectExpiry(caster, target, def.Id, def);
                target.MindRotActive = true;
                break;
            }
            case SpellType.PainSpike:
            {
                // Necromancy Pain Spike (reference SPELL_Pain_Spike): a total of
                // ((SpiritSpeak - MagicResist)/100)+18 direct damage over 10 one-
                // second ticks (damage per tick = total/10, DAMAGE_GOD = ignores
                // resist).
                int ss = caster.GetSkill(SkillType.SpiritSpeak);
                int mr = target.GetSkill(SkillType.MagicResistance);
                int total = Math.Max(10, (ss - mr) / 100 + 18);
                var eff = SetupDot(caster, target, def, charges: 10, intervalMs: 1000);
                eff.DotDamagePerTick = Math.Max(1, total / 10);
                eff.DotDamageType = DamageType.Physical;
                eff.DotDirect = true;
                break;
            }
            case SpellType.Strangle:
            {
                // Necromancy Strangle (reference SPELL_Strangle): power = max(4,
                // SpiritSpeak/100) ticks of poison damage that scale up as the
                // victim's stamina drops; the first tick lands after 5 seconds.
                int power = Math.Max(4, caster.GetSkill(SkillType.SpiritSpeak) / 100);
                var eff = SetupDot(caster, target, def, charges: power, intervalMs: 5000);
                eff.DotPower = power;
                eff.DotDamageType = DamageType.Poison;
                break;
            }
            case SpellType.BloodOath:
            {
                // Necromancy Blood Oath (reference SPELL_Blood_Oath): the state
                // lives on the CASTER, linked to the enemy; a blow the enemy lands
                // on the caster reflects (100 - level)% back. Source-X
                // CCharSpell.cpp:1313 computes level on the layer's OWNER (the
                // caster): (MagicResistance * 10 / 20) + 10, no clamp — high MR
                // zeroes the reflect via the (100 - level) term itself.
                int level = caster.GetSkill(SkillType.MagicResistance) * 10 / 20 + 10;
                var eff = ScheduleEffectExpiry(caster, caster, def.Id, def);
                eff.BloodOathEnemy = target.Uid;
                eff.BloodOathLevel = level;
                caster.BloodOathEnemy = target.Uid;
                caster.BloodOathLevel = level;
                // Blood Oath is the one spell that raises two different icons on
                // two different characters (Source-X CCharSpell.cpp:1312): the
                // victim carries the curse with the caster's name, the caster
                // carries the bond with the victim's name. The per-effect notify
                // cannot express that, so BloodOath is deliberately absent from
                // the spell->icon map and both sides are raised here.
                ushort oathSeconds = (ushort)Math.Clamp(
                    (eff.ExpireTick - Environment.TickCount64 + 999) / 1000, 1, ushort.MaxValue);
                RaiseBuffIcon(target, BuffIcon.BloodOathCurse, oathSeconds,
                    [caster.GetName(), caster.GetName()]);
                RaiseBuffIcon(caster, BuffIcon.BloodOathCaster, oathSeconds,
                    [target.GetName()]);
                break;
            }
            case SpellType.EvilOmen:
            {
                // Necromancy Evil Omen (reference SPELL_Evil_Omen): the target's
                // next harmful effect lands harder, then the marker is spent. A
                // one-shot flag with lazy expiry (not an ActiveSpellEffect).
                int casterSkill = caster.GetSkill(def.GetPrimarySkill());
                int durTenths = def.GetDuration(casterSkill);
                if (durTenths <= 0) durTenths = 300;
                target.EvilOmenActive = true;
                target.EvilOmenExpireTick = Environment.TickCount64 + (long)durTenths * 100L;
                break;
            }
            case SpellType.PoisonStrike:
            {
                // Necromancy Poison Strike (reference SPELL_Poison_Strike): direct
                // poison damage to the primary target, half to everything within
                // 2 tiles of it (reference area radius 2).
                DealSpellDamage(caster, target, effect, DamageType.Poison);
                foreach (var other in _world.GetCharsInRange(target.Position, 2))
                {
                    if (other == target || other == caster || other.IsDead) continue;
                    DealSpellDamage(caster, other, effect / 2, DamageType.Poison);
                }
                break;
            }
            case SpellType.Wither:
            {
                // Necromancy Wither (reference SPELL_Wither): cold damage to every
                // enemy within 4 tiles of the caster (reference area radius 4,
                // centred on the caster).
                foreach (var other in _world.GetCharsInRange(caster.Position, 4))
                {
                    if (other == caster || other.IsDead) continue;
                    DealSpellDamage(caster, other, effect, DamageType.Cold);
                }
                break;
            }
            case SpellType.VengefulSpirit:
            {
                // Necromancy Vengeful Spirit (reference SPELL_Vengeful_Spirit):
                // summon a revenant (CREID_REVENANT 0x2EE) that hunts the target,
                // controlled by the caster for a short duration.
                int summonSkill = caster.GetSkill(def.GetPrimarySkill());
                var spawnPos = new Point3D((short)(caster.X + 1), caster.Y, caster.Z, caster.Position.Map);
                // Stats/skills come from the revenant chardef @Create
                // (SummonCreature applies it) — not flat invented numbers.
                var revenant = SummonCreature(caster, spawnPos, def, summonSkill, bodyId: 0x02EE);
                if (revenant != null && target != caster)
                    revenant.FightTarget = target.Uid;
                break;
            }
            case SpellType.Polymorph:
            case SpellType.Chameleon:
            case SpellType.BeastForm:
            case SpellType.MonsterForm:
            {
                // Menu pick (sm_polymorph / sm_beast_form / sm_monster_form)
                // stashed on the caster (Source-X keeps the selection and
                // casts the real spell); no selection falls back to a
                // spell-appropriate classic form. The Sphere customs share
                // the reference's LAYER_SPELL_Polymorph path
                // (CCharSpell.cpp:4087).
                ushort newBody = 0;
                if (target.TryGetTag("POLY_SELECT", out string? polySel) &&
                    !string.IsNullOrWhiteSpace(polySel))
                {
                    newBody = Character.ResolvePolyBody(polySel);
                    target.RemoveTag("POLY_SELECT");
                }
                if (newBody == 0 && def.Id != SpellType.Chameleon)
                {
                    ReadOnlySpan<ushort> forms = def.Id switch
                    {
                        // Animals: dog, timber wolf, black bear, great hart, rabbit
                        SpellType.BeastForm =>
                            [0x00D9, 0x00E1, 0x00D3, 0x00EA, 0x00CD],
                        // Monsters: ettin, gargoyle, orc, lizardman, troll
                        SpellType.MonsterForm =>
                            [0x0002, 0x0004, 0x0011, 0x0021, 0x0036],
                        _ => [0x0033, 0x0034, 0x0035, 0x0036, 0x0037, 0x0038],
                    };
                    newBody = forms[_rand.Next(forms.Length)];
                }
                // Base body captured only AFTER the previous poly-layer
                // effect is reverted inside ScheduleEffectExpiry — a re-cast
                // must never record the current form as the restore body.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                if (target.OBody == 0)
                    target.OBody = target.BodyId;
                eff.AppliedFlag = StatFlag.Polymorph;
                target.SetStatFlag(StatFlag.Polymorph);
                // Chameleon without a menu pick keeps the body — the
                // reference's skin-match visual is client-side flavor it
                // ships commented out; only the poly layer is real there.
                if (newBody != 0)
                {
                    eff.OldBodyId = target.OBody;
                    eff.NewBodyId = newBody;
                    eff.BodyChanged = true;
                    target.BodyId = newBody;
                    Character.OnAppearanceChanged?.Invoke(target);
                }
                break;
            }
            case SpellType.ReaperForm:
            case SpellType.StoneForm:
            {
                // Source-X OnSpellEffect equips LAYER_SPELL_Polymorph and the
                // effect-add SetID()s the form body. Reaper uses its own
                // CREID_REAPER_FORM 0xE6 (the reference literally assigns
                // CREID_STONE_FORM to both — an obvious copy-paste slip we do
                // not reproduce); Stone Form is CREID_STONE_FORM 0x2C1.
                ushort formBody = def.Id == SpellType.ReaperForm
                    ? (ushort)0x00E6 : (ushort)0x02C1;
                // Capture the base body only AFTER ScheduleEffectExpiry has
                // reverted a previous poly-layer effect — otherwise a re-cast
                // records the CURRENT form as the body to restore.
                var formEff = ScheduleEffectExpiry(caster, target, def.Id, def);
                if (target.OBody == 0)
                    target.OBody = target.BodyId;
                // The pack ships Reaper Form with DURATION=0.0 — in the
                // reference a 0-duration poly memory has no timer: the form
                // holds until dispel/death/toggle (the Scripts-X @Select
                // FINDID.<RUNE_ITEM>.REMOVE path, wired through
                // RemoveEffectByMemory). No expiry at all, not a floor.
                if (def.GetDuration(caster.GetSkill(def.GetPrimarySkill())) <= 0)
                    formEff.ExpireTick = long.MaxValue;
                formEff.AppliedFlag = StatFlag.Polymorph;
                formEff.OldBodyId = target.OBody;
                formEff.NewBodyId = formBody;
                formEff.BodyChanged = true;
                target.BodyId = formBody;
                target.SetStatFlag(StatFlag.Polymorph);
                Character.OnAppearanceChanged?.Invoke(target);
                break;
            }

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

            // ---- Chivalry (201-210). Source-X ships this school over the
            // generic script engine (its native surface is buff bookkeeping
            // only); the handlers below give the classic behaviors that the
            // engine's existing primitives can express. Consecrate Weapon and
            // Enemy of One stay script territory (typed-damage combat
            // coupling), like the reference.
            case SpellType.CleanseByFire:
                // Cure the target's poison by holy fire.
                target.CurePoison();
                break;

            case SpellType.DispelEvil:
                // Area dispel around the paladin: strip dispellable effects
                // from and banish conjured creatures near the caster.
                foreach (var ch in _world.GetCharsInRange(caster.Position, 8).ToList())
                {
                    if (ch == caster || ch.IsDead || ch.IsPlayer)
                        continue;
                    if (ch.IsStatFlag(StatFlag.Conjured) ||
                        ch.IsStatFlag(StatFlag.Polymorph))
                        StripDispellableEffects(ch);
                    DispelConjured(caster, ch);
                }
                break;

            case SpellType.DivineFury:
            {
                // Stamina surges back; the paladin drops their guard for the
                // duration (classic -20 defense while the fury lasts).
                caster.Stam = caster.MaxStam;
                var fury = ScheduleEffectExpiry(caster, caster, def.Id, def);
                fury.ArmorDelta = -20;
                caster.ProtectionArmor += fury.ArmorDelta;
                break;
            }

            case SpellType.HolyLight:
                // Energy burst around the paladin — strikes the creatures
                // actively fighting the caster.
                foreach (var ch in _world.GetCharsInRange(caster.Position, 3).ToList())
                {
                    if (ch == caster || ch.IsDead || CombatEngine.IsDamageImmune(ch))
                        continue;
                    bool hostile = ch.FightTarget == caster.Uid ||
                                   caster.FightTarget == ch.Uid;
                    if (!hostile)
                        continue;
                    int dmg = CombatEngine.ApplyElementalResist(ch,
                        Math.Max(10, effect), DamageType.Energy);
                    ch.Hits = (short)Math.Max(0, ch.Hits - dmg);
                    ch.RecordAttack(caster.Uid, dmg);
                    if (ch.Hits <= 0 && !ch.IsDead)
                    {
                        if (OnTargetKilled != null) OnTargetKilled.Invoke(ch, caster);
                        else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, caster);
                        else ch.Kill();
                    }
                }
                break;

            case SpellType.NobleSacrifice:
            {
                // Heal + cure nearby allies at the paladin's own expense.
                // "Ally" is the caster's party plus the caster's own pets —
                // never every bystander (an enemy player standing in range
                // must not receive the heal+cure).
                var casterParty = Character.ResolvePartyManager?.Invoke()?.FindParty(caster.Uid);
                bool sacrificed = false;
                foreach (var ch in _world.GetCharsInRange(caster.Position, 3).ToList())
                {
                    if (ch == caster || ch.IsDead)
                        continue;
                    bool isAlly = ch.OwnerSerial == caster.Uid ||
                                  (casterParty?.IsMember(ch.Uid) ?? false);
                    if (!isAlly)
                        continue;
                    ch.CurePoison();
                    ch.Hits = (short)Math.Min(ch.Hits + Math.Max(10, effect), ch.MaxHits);
                    sacrificed = true;
                }
                if (sacrificed && caster.Hits > 20)
                    caster.Hits -= 20;
                break;
            }

            case SpellType.RemoveCurse:
                StripCurseEffects(target);
                break;

            // ---- Spellweaving: Gift of Renewal — heal-over-time (one heal
            // tick every 2 s for the spell's duration; negative DOT = heal).
            case SpellType.GiftOfRenewal:
            {
                int durTenths = def.GetDuration(caster.GetSkill(def.GetPrimarySkill()));
                int charges = Math.Clamp(durTenths / 20, 5, 15); // one tick / 2s
                var hot = SetupDot(caster, target, def, charges, 2000);
                hot.DotDamagePerTick = -Math.Max(5, effect / 3);
                hot.DotDirect = true;
                break;
            }

            // ---- Sphere custom spells (reference CCharSpell.cpp OnSpellEffect
            // SPELL_Light..SPELL_Liquor cases; ids remapped to 1000+).
            case SpellType.Light:
            {
                // Pack layer is layer_spell_night_sight: a timed personal
                // light boost exactly like Night Sight.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.NightSight;
                eff.OldLightLevel = target.LightLevel;
                eff.NewLightLevel = 30;
                eff.LightChanged = true;
                target.SetStatFlag(StatFlag.NightSight);
                target.LightLevel = eff.NewLightLevel;
                OnPersonalLightChanged?.Invoke(target);
                break;
            }
            case SpellType.Hallucination:
            {
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Hallucinating;
                target.SetStatFlag(StatFlag.Hallucinating);
                // Periodic trip sounds every 15-30 s (Source-X
                // Spell_Equip_OnTick plays 0x243/0x244). The duration expiry
                // ends the effect; the charge budget just outlasts it.
                int tripTenths = def.GetDuration(caster.GetSkill(def.GetPrimarySkill()));
                eff.DotCharges = Math.Max(2, tripTenths * 100 / 15_000 + 2);
                eff.DotTotalCharges = eff.DotCharges;
                eff.DotIntervalMs = 15_000;
                eff.DotNextTickMs = Environment.TickCount64 + 15_000 + _rand.Next(15_001);
                eff.DotSource = caster.Uid;
                // Source-X refreshes the whole view the moment the effect
                // lands (addChar + addPlayerSee) — the trip starts NOW, not
                // at the first 15-30 s tick.
                OnViewRefresh?.Invoke(target);
                break;
            }
            case SpellType.Stone:
            case SpellType.ParticleForm:
            {
                // Source-X: STATF_STONE for the duration — immobile and
                // untargetable.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.AppliedFlag = StatFlag.Stone;
                target.SetStatFlag(StatFlag.Stone);
                break;
            }
            case SpellType.Shrink:
            {
                // Source-X SPELL_Shrink: players are immune; a CONJURED
                // creature is killed outright (NPC_Shrink zeroes its STR);
                // any other NPC is packed into a figurine where it stood.
                if (target.IsPlayer || _world == null)
                    break;
                if (target.IsSummoned || target.IsStatFlag(StatFlag.Conjured))
                {
                    target.Hits = 0;
                    if (OnTargetKilled != null) OnTargetKilled.Invoke(target, caster);
                    else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(target, caster);
                    else target.Kill();
                    break;
                }
                var shrinkPos = target.Position;
                var figurine = _world.CreateItem();
                figurine.BaseId = 0x2106; // statuette graphic (pet-shrink path)
                if (NPCs.PetFigurine.ShrinkBySpell(caster, target, figurine, _world))
                    _world.PlaceItemWithDecay(figurine, shrinkPos);
                else
                    _world.RemoveItem(figurine);
                break;
            }
            case SpellType.Refresh:
                target.Stam = (short)Math.Min(target.Stam + Math.Max(1, effect), target.MaxStam);
                break;
            case SpellType.Restore:
                // Reference: increases both hit points and stamina.
                target.Stam = (short)Math.Min(target.Stam + Math.Max(1, effect), target.MaxStam);
                target.Hits = (short)Math.Min(target.Hits + Math.Max(1, effect), target.MaxHits);
                break;
            case SpellType.Mana:
                target.Mana = (short)Math.Min(target.Mana + Math.Max(1, effect), target.MaxMana);
                break;
            case SpellType.Sustenance:
                // Reference: fills the food meter to its maximum.
                target.Food = 60;
                break;
            case SpellType.GenderSwap:
            {
                ushort swapped = target.BodyId switch
                {
                    0x0190 => (ushort)0x0191, 0x0191 => (ushort)0x0190, // human
                    0x025D => (ushort)0x025E, 0x025E => (ushort)0x025D, // elf
                    0x029A => (ushort)0x029B, 0x029B => (ushort)0x029A, // gargoyle
                    _ => (ushort)0,
                };
                if (swapped != 0)
                {
                    target.BodyId = swapped;
                    Character.OnAppearanceChanged?.Invoke(target);
                }
                break;
            }
            case SpellType.Trance:
            {
                // Source-X SPELL_Trance: a timed Meditation skill bonus.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.MeditationDelta = Math.Max(10, effect);
                target.SetSkill(SkillType.Meditation, (ushort)Math.Min(ushort.MaxValue,
                    target.GetSkill(SkillType.Meditation) + eff.MeditationDelta));
                break;
            }
            case SpellType.Shield:
            case SpellType.Steelskin:
            case SpellType.Stoneskin:
            {
                // Source-X routes these to the Protection ward layer: a timed
                // AR bonus for the spell's duration.
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                eff.ArmorDelta = Math.Max(1, effect);
                target.ProtectionArmor = (int)Math.Min(
                    int.MaxValue, (long)target.ProtectionArmor + eff.ArmorDelta);
                break;
            }
            case SpellType.Regenerate:
            {
                // Source-X Spell_Equip_OnTick SPELL_Regenerate: one heal tick
                // every 2 s for the spell's duration (negative DOT = heal).
                int durTenths = def.GetDuration(caster.GetSkill(def.GetPrimarySkill()));
                int charges = Math.Max(1, durTenths / 20);
                var hot = SetupDot(caster, target, def, charges, 2000);
                hot.DotDamagePerTick = -Math.Max(1, effect);
                hot.DotDirect = true;
                break;
            }
            case SpellType.Ale:
            case SpellType.Wine:
            case SpellType.Liquor:
            {
                // Source-X Spell_Equip_OnTick: each 5 s drunk tick drains one
                // current stamina and mana point (Stat_AddVal DEX/INT vals),
                // speaks a random hiccup, and has a 10% chance per tick to
                // sober up early. The drink's strength sets the tick budget
                // (wine mild, liquor extreme).
                var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
                int baseTicks = def.Id switch
                {
                    SpellType.Liquor => 18,
                    SpellType.Ale => 12,
                    _ => 6,
                };
                int durTicks = def.GetDuration(caster.GetSkill(def.GetPrimarySkill())) * 100 / 5000;
                eff.DotCharges = Math.Max(baseTicks, durTicks);
                eff.DotTotalCharges = eff.DotCharges;
                eff.DotIntervalMs = 5000;
                eff.DotNextTickMs = Environment.TickCount64 + eff.DotIntervalMs;
                eff.DotSource = caster.Uid;
                // Keep the drunk effect alive until every tick is spent.
                eff.ExpireTick = Math.Max(eff.ExpireTick, Environment.TickCount64 +
                    (long)eff.DotCharges * eff.DotIntervalMs + 10_000L);
                OnSysMessage?.Invoke(target, "*hic*");
                break;
            }

            case SpellType.CreateFood:
            {
                // Materialize a food item into the caster's pack (was a no-op).
                var food = _world.CreateItem();
                food.BaseId = def.EffectId != 0 ? def.EffectId : (ushort)0x09D0; // apple default
                food.ItemType = ItemType.Food;
                food.Name = "food";
                bool canPack = target.Backpack != null &&
                    (target.PrivLevel >= PrivLevel.GM || target.CanCarry(food));
                if (canPack && target.Backpack!.TryAddItem(food))
                    break;
                if (!_world.PlaceItemWithDecay(food, target.Position))
                    _world.RemoveItem(food);
                break;
            }
        }
    }

    private static void ClearCastState(Character ch) => ch.ClearCastState();

    /// <summary>Spell mana cost after per-caster modifiers. Necromancy Mind Rot
    /// raises the victim's spell mana cost by 10% (reference LOWERMANACOST -10).</summary>
    private static int EffectiveManaCost(Character caster, SpellDef def)
    {
        int cost = def.ManaCost;
        if (caster.MindRotActive)
            cost += cost / 10;
        return cost;
    }

    /// <summary>Necromancy Corpse Skin resist shift (reference PolyStr/PolyDex):
    /// fire/poison down 15, cold/physical up 10. <paramref name="sign"/> is +1 to
    /// apply the debuff, -1 to revert it.</summary>
    private static void ApplyCorpseSkinResists(Character t, int sign)
    {
        t.ResFire = (short)(t.ResFire + sign * -15);
        t.ResPoison = (short)(t.ResPoison + sign * -15);
        t.ResCold = (short)(t.ResCold + sign * 10);
        t.ResPhysical = (short)(t.ResPhysical + sign * 10);
    }

    /// <summary>Necromancy Lich Form resist shift (reference CCharSpell.cpp:1038):
    /// fire down, poison and cold up. <paramref name="sign"/> +1 apply, -1 revert.</summary>
    private static void ApplyLichFormResists(Character t, int sign)
    {
        t.ResFire = (short)(t.ResFire + sign * -10);
        t.ResPoison = (short)(t.ResPoison + sign * 10);
        t.ResCold = (short)(t.ResCold + sign * 10);
    }

    /// <summary>Necromancy Vampiric Embrace resist shift (reference
    /// CCharSpell.cpp:1062): fire resist down. <paramref name="sign"/> +1/-1.</summary>
    private static void ApplyVampiricResists(Character t, int sign)
    {
        t.ResFire = (short)(t.ResFire + sign * -10);
    }

    /// <summary>Register a spell's expiration with its undo data.
    /// Duration comes from <see cref="SpellDef.GetDuration"/>(caster's
    /// primary skill) — tenths of a second per the CAST_TIME /
    /// DURATION convention. ServUO semantics: duration scales with the
    /// CASTER's skill, not the target's. If the script leaves DURATION
    /// at 0 a 30-second floor kicks in so buffs don't expire instantly
    /// on scripts that forgot the field. Re-casting on the same target
    /// refreshes the timer and merges the delta rather than stacking.</summary>
    /// <summary>@SpellEffect LOCAL.Duration override (tenths), scoped to the
    /// current ApplyCharEffect dispatch; null = use the spell def curve.</summary>
    private int? _durationOverrideTenths;

    private static void NotifySpellBuff(Character target, SpellType spell, bool add,
        ushort durationSeconds = 0, int magnitude = 0)
    {
        if (!ClientBuffCatalog.TryGet(spell, out var definition))
            return;
        Character.OnClientBuffChanged?.Invoke(target, definition.Icon, add, durationSeconds,
            add ? BuildBuffArgs(definition.Icon, magnitude) : null);
    }

    /// <summary>Raise a buff icon directly, for the effects whose icon does not
    /// follow the one-icon-per-spell-on-the-effect-target rule. Removes first,
    /// like every Source-X addBuff site: a client that still holds the icon
    /// would otherwise keep the old countdown instead of the new one.</summary>
    private static void RaiseBuffIcon(Character target, BuffIcon icon,
        ushort durationSeconds, string[]? args)
    {
        Character.OnClientBuffChanged?.Invoke(target, icon, false, 0, null);
        Character.OnClientBuffChanged?.Invoke(target, icon, true, durationSeconds, args);
    }

    /// <summary>Cliloc formatter arguments for a buff tooltip — Source-X
    /// resendBuffs/Spell_Effect_Add fill a NumBuff array with the effect
    /// magnitude before calling addBuff, and the client interpolates them into
    /// the description cliloc. Without them the tooltip renders its ~1_VAL~
    /// placeholders empty. Argument counts follow the reference exactly:
    /// one per affected stat, plus the four fixed 10s Curse also sends.</summary>
    private static string[]? BuildBuffArgs(BuffIcon icon, int magnitude)
    {
        if (magnitude <= 0)
            return null;
        string value = magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return icon switch
        {
            BuffIcon.Clumsy or BuffIcon.Feeblemind or BuffIcon.Weaken or
            BuffIcon.Strength or BuffIcon.Agility or BuffIcon.Cunning or
            BuffIcon.GiftOfRenewal or BuffIcon.AttuneWeapon or
            BuffIcon.Thunderstorm or BuffIcon.EssenceOfWind or
            BuffIcon.ArcaneEmpowerment or BuffIcon.CorpseSkin => [value],

            // Bless and Mass Curse touch all three base stats (STAT_BASE_QTY).
            BuffIcon.Bless or BuffIcon.MassCurse => [value, value, value],

            // Curse: three stat penalties followed by the four fixed resist
            // penalties Source-X hardcodes to 10.
            BuffIcon.Curse => [value, value, value, "10", "10", "10", "10"],

            _ => null
        };
    }

    /// <summary>Equip the IT_SPELL memory item that represents this effect
    /// (Source-X Spell_Effect_Create). The graphic is the spell's RUNE_ITEM;
    /// the item is hidden from the client and shows up under GM .edit. The
    /// engine's active-effect list stays the authority for expiry, so the memory
    /// is a faithful mirror that is deleted whenever the effect is removed.</summary>
    private static void AttachSpellMemory(Character? caster, ActiveSpellEffect eff, SpellDef? def)
    {
        ushort graphic = def?.RuneItemId ?? 0;
        Serial source = caster != null ? caster.Uid : Serial.Invalid;
        // MOREY carries the effect magnitude, matching Source-X
        // m_itSpell.m_spelllevel — the value resendBuffs reads back to fill the
        // buff tooltip, and what a script reading the memory's MOREY expects.
        eff.Memory = eff.Target.Memory_CreateSpellEffect(
            (int)eff.Spell, graphic, eff.BuffMagnitude, source, eff.Spell.ToString());
    }

    /// <summary>Remove the active effect represented by this IT_SPELL memory
    /// item — the reverse direction of the memory mirror. Source-X deletes
    /// the memory item and Spell_Effect_Remove reverts the effect; SphereNet
    /// scripts reach this through Character.SpellMemoryEffectRemover when
    /// they REMOVE the memory (the Scripts-X Reaper/Stone Form @Select
    /// toggles). Returns true when a matching effect was reverted.</summary>
    public bool RemoveEffectByMemory(Item memory)
    {
        for (int i = 0; i < _activeEffects.Count; i++)
        {
            var eff = _activeEffects[i];
            if (!ReferenceEquals(eff.Memory, memory))
                continue;
            _activeEffects.RemoveAt(i);
            RevertDeltas(eff); // detaches and deletes the memory item too
            NotifySpellBuff(eff.Target, eff.Spell, false);
            Character.OnSpellEffectRemove?.Invoke(eff.Target, (int)eff.Spell);
            FireSpellSectionStage(eff.Spell, "EffectRemove", eff.Target);
            return true;
        }
        return false;
    }

    /// <summary>Delete the effect's spell-memory item (Source-X deletes the
    /// IT_SPELL item, which unequips and runs Spell_Effect_Remove). Idempotent:
    /// a no-op when the effect never had a memory, so it is safe to call at every
    /// active-effect removal site.</summary>
    private static void DetachSpellMemory(ActiveSpellEffect eff)
    {
        var mem = eff.Memory;
        if (mem == null) return;
        eff.Memory = null;
        eff.Target.Memory_Delete(mem);
        mem.ContainedIn = Serial.Invalid; // drop the owner-keyed container-index entry
        mem.Delete();
    }

    private ActiveSpellEffect ScheduleEffectExpiry(Character caster, Character target,
        SpellType spell, SpellDef def, int buffMagnitude = 0)
    {
        int casterSkill = caster.GetSkill(def.GetPrimarySkill());
        int durationTenths = _durationOverrideTenths ?? def.GetDuration(casterSkill);
        if (durationTenths <= 0) durationTenths = 300; // 30s floor
        long expireTick = Environment.TickCount64 + (long)durationTenths * 100L;

        // Refresh on re-cast — revert the previous delta first so the new
        // cast stacks cleanly onto the base value, not on top of the old buff.
        for (int i = 0; i < _activeEffects.Count; i++)
        {
            var existing = _activeEffects[i];
            if (existing.Target == target &&
                (existing.Spell == spell ||
                 (IsProtectionSpell(existing.Spell) && IsProtectionSpell(spell)) ||
                 (IsPolymorphLayerSpell(existing.Spell) && IsPolymorphLayerSpell(spell))))
            {
                RevertDeltas(existing); // also detaches the old spell-memory item
                _activeEffects.RemoveAt(i);
                NotifySpellBuff(target, spell, false);
                // Source-X re-equips the spell memory on refresh: the old
                // effect's removal is observable before the new add.
                Character.OnSpellEffectRemove?.Invoke(target, (int)spell);
                FireSpellSectionStage(spell, "EffectRemove", target);
                break;
            }
        }

        var eff = new ActiveSpellEffect
        {
            Target = target, Spell = spell, ExpireTick = expireTick,
            BuffMagnitude = buffMagnitude
        };
        _activeEffects.Add(eff);
        // Source-X Spell_Effect_Create: equip the IT_SPELL memory item for this
        // effect (visible to .edit, hidden from the client). Deleted on removal.
        AttachSpellMemory(caster, eff, def);
        // @EffectAdd (Source-X) — a temporary effect was applied to the target.
        Character.OnEffectAdd?.Invoke(target, (int)spell);
        // @SpellEffectAdd (Source-X CCharSpell) — SRC = caster, ARGN1 = spell.
        Character.OnSpellEffectAdd?.Invoke(target, caster, (int)spell);
        // [SPELL n] @EffectAdd resource-section stage (SPTRIG_EFFECTADD).
        FireSpellSectionStage(spell, "EffectAdd", target);
        NotifySpellBuff(target, spell, false);
        NotifySpellBuff(target, spell, true,
            (ushort)Math.Clamp((durationTenths + 9) / 10, 1, ushort.MaxValue), buffMagnitude);
        return eff;
    }

    /// <summary>Run a [SPELL n] section stage on the affected char — the
    /// SPTRIG_EFFECTADD/EFFECTREMOVE lifecycle hooks that previously never
    /// fired. No-op without a dispatcher or a matching ON= block.</summary>
    private void FireSpellSectionStage(SpellType spell, string stage, Character target)
    {
        TriggerDispatcher?.FireSpellTrigger(spell, stage, target,
            new TriggerArgs { CharSrc = target, N1 = (int)spell });
    }

    /// <summary>Break an active paralyze early (Source-X: the paralyze spell
    /// memory is deleted when the victim takes damage). Clears the Freeze
    /// flag and retires the matching active-effect entry so its later expiry
    /// doesn't double-fire the removal event.</summary>
    public void BreakParalyze(Character victim)
    {
        victim.ClearStatFlag(StatFlag.Freeze);
        // Snapshot before firing callbacks — a removal callback can clear other
        // effects mid-pass, so the live list must not be walked by index.
        // Paralyze carries no stat delta (only the Freeze flag, cleared above),
        // so this detaches the memory rather than calling RevertDeltas.
        List<ActiveSpellEffect>? snapshot = null;
        foreach (var eff in _activeEffects)
            if (eff.Target == victim && eff.Spell == SpellType.Paralyze)
                (snapshot ??= []).Add(eff);
        if (snapshot == null) return;

        foreach (var eff in snapshot)
        {
            if (!_activeEffects.Remove(eff)) continue; // already retired by a callback
            DetachSpellMemory(eff);
            NotifySpellBuff(victim, SpellType.Paralyze, false);
            Character.OnSpellEffectRemove?.Invoke(victim, (int)SpellType.Paralyze);
            FireSpellSectionStage(SpellType.Paralyze, "EffectRemove", victim);
        }
    }

    /// <summary>Dispel: revert and remove every temporary spell effect on the
    /// target (Source-X removes the dispellable ATTR_MAGIC spell-memory items
    /// — Bless/Curse/NightSight/Protection/Reflect/Paralyze and the rest of
    /// the LAYER_SPELL family).</summary>
    /// <summary>Banish a conjured creature (shared by Dispel / Mass Dispel and
    /// Chivalry Dispel Evil): DISPELKILLSUMMONS routes through the kill
    /// pipeline (loot/corpse), otherwise the summon is deleted outright.</summary>
    private void DispelConjured(Character caster, Character target)
    {
        if (!target.IsStatFlag(StatFlag.Conjured) || target.IsDead)
            return;
        if (IsMagicFlag(MagicConfigFlags.DispelKillSummons))
        {
            if (OnTargetKilled != null)
                OnTargetKilled.Invoke(target, caster);
            else if (Character.OnLifecycleKill != null)
                Character.OnLifecycleKill(target, caster);
            else
                target.Kill();
        }
        else
        {
            _world.DeleteObject(target);
            target.Delete();
        }
    }

    private static bool IsCurseSpell(SpellType s) => s is
        SpellType.Clumsy or SpellType.Feeblemind or SpellType.Weaken or
        SpellType.Curse or SpellType.MassCurse or SpellType.CorpseSkin or
        SpellType.EvilOmen or SpellType.MindRot or SpellType.Strangle or
        SpellType.BloodOath or SpellType.PainSpike;

    /// <summary>Chivalry Remove Curse: strip only the CURSE-type active
    /// effects from the target, leaving beneficial buffs intact.</summary>
    public void StripCurseEffects(Character target)
    {
        RemoveMatchingEffects(eff => eff.Target == target && IsCurseSpell(eff.Spell));
    }

    public void StripDispellableEffects(Character target)
    {
        RemoveMatchingEffects(eff => eff.Target == target);
    }

    /// <summary>Create a periodic damage-over-time effect (reference
    /// SPELLFLAG_TICK spell memories). Reuses ScheduleEffectExpiry for re-cast
    /// dedup/refresh, then arms the tick fields; the expiry is pushed past the
    /// DOT's own lifetime so the tick pass — not the expiry pass — retires it.</summary>
    private ActiveSpellEffect SetupDot(Character caster, Character target, SpellDef def,
        int charges, int intervalMs)
    {
        var eff = ScheduleEffectExpiry(caster, target, def.Id, def);
        eff.DotCharges = Math.Max(1, charges);
        eff.DotTotalCharges = eff.DotCharges;
        eff.DotIntervalMs = Math.Max(1, intervalMs);
        eff.DotNextTickMs = Environment.TickCount64 + eff.DotIntervalMs;
        eff.DotSource = caster.Uid;
        eff.DotDamageType = DamageType.Physical;
        // Guard the effect from the expiry pass until every charge is spent.
        eff.ExpireTick = Environment.TickCount64 +
            (long)eff.DotCharges * eff.DotIntervalMs + 60_000L;
        return eff;
    }

    /// <summary>Per-tick damage for a DOT. Pain Spike is a fixed direct amount;
    /// Strangle scales rand(power-2..power+1) by the victim's fatigue
    /// (3 - 2*curStam/maxStam), reference CCharSpell.cpp:1951-1952.</summary>
    private int ComputeDotDamage(ActiveSpellEffect eff, Character victim)
    {
        if (eff.Spell == SpellType.Strangle)
        {
            int power = Math.Max(1, eff.DotPower);
            int spellPower = _rand.Next(power - 2, power + 2); // [power-2, power+1]
            int maxStam = Math.Max(1, (int)victim.MaxStam);
            int mult = Math.Max(1, 3 - 2 * Math.Max(0, (int)victim.Stam) / maxStam);
            return Math.Max(1, Math.Max(1, spellPower) * mult);
        }
        // Negative per-tick = heal-over-time (Spellweaving Gift of Renewal).
        if (eff.DotDamagePerTick < 0)
            return eff.DotDamagePerTick;
        return Math.Max(1, eff.DotDamagePerTick);
    }

    /// <summary>Delay to the next tick. Strangle accelerates 4,3,2,1,1s after its
    /// initial 5s tick (reference CCharSpell.cpp:1934-1949); others are fixed.</summary>
    private static int NextDotIntervalMs(ActiveSpellEffect eff)
    {
        if (eff.Spell == SpellType.Strangle)
        {
            int done = eff.DotTotalCharges - eff.DotCharges; // charges already spent
            int secs = done switch { 0 => 4, 1 => 3, 2 => 2, _ => 1 };
            return secs * 1000;
        }
        return eff.DotIntervalMs;
    }

    /// <summary>Apply one DOT tick's damage: elemental resist unless the tick is
    /// direct, damage credited to the source, health broadcast, death handled.</summary>
    private void ApplyDotDamage(Character victim, ActiveSpellEffect eff, int damage)
    {
        // Negative amount = heal tick (Gift of Renewal HoT).
        if (damage < 0)
        {
            if (victim.IsDead) return;
            victim.Hits = (short)Math.Min(victim.Hits - damage, victim.MaxHits);
            Character.BroadcastNearby?.Invoke(victim.Position, 18,
                new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                    victim.Uid.Value, victim.MaxHits, victim.Hits), 0);
            return;
        }

        if (!eff.DotDirect)
            damage = CombatEngine.ApplyElementalResist(victim, damage, eff.DotDamageType);
        damage = Math.Max(1, damage);

        if (CombatEngine.IsDamageImmune(victim))
            return;

        victim.Hits = (short)Math.Max(0, victim.Hits - damage);
        if (eff.DotSource.IsValid)
            victim.RecordAttack(eff.DotSource, damage);
        TryInterruptFromDamage(victim, damage);

        Character.BroadcastDamageNearby?.Invoke(victim.Position, 18, victim.Uid.Value, damage, 0);
        Character.BroadcastNearby?.Invoke(victim.Position, 18,
            new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                victim.Uid.Value, victim.MaxHits, victim.Hits), 0);

        if (victim.Hits <= 0 && !victim.IsDead)
        {
            var killer = eff.DotSource.IsValid ? Character.ResolveCharByUid?.Invoke(eff.DotSource) : null;
            if (OnTargetKilled != null) OnTargetKilled.Invoke(victim, killer!);
            else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(victim, killer);
            else victim.Kill();
        }
    }

    /// <summary>Deal one-shot elemental spell damage to a character: elemental
    /// resist (unless MAGICF ignore-armor), damage credited to the caster, health
    /// broadcast, interrupt and death handled. Used by the direct/area necro
    /// damage spells (Poison Strike, Wither).</summary>
    private void DealSpellDamage(Character caster, Character victim, int damage, DamageType type)
    {
        if (damage <= 0 || victim.IsDeleted || victim.IsDead) return;
        if (!IsMagicFlag(MagicConfigFlags.IgnoreArmor))
            damage = CombatEngine.ApplyElementalResist(victim, damage, type);
        damage = Math.Max(0, damage);
        if (damage <= 0 || CombatEngine.IsDamageImmune(victim, type)) return;

        victim.Hits -= (short)Math.Min(damage, short.MaxValue);
        victim.RecordAttack(caster.Uid, damage);
        TryInterruptFromDamage(victim, damage);

        Character.BroadcastDamageNearby?.Invoke(victim.Position, 18, victim.Uid.Value, damage, 0);
        Character.BroadcastNearby?.Invoke(victim.Position, 18,
            new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                victim.Uid.Value, victim.MaxHits, victim.Hits), 0);

        if (victim.Hits <= 0 && !victim.IsDead)
        {
            if (OnTargetKilled != null) OnTargetKilled.Invoke(victim, caster);
            else if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(victim, caster);
            else victim.Kill();
        }
    }

    /// <summary>Advance periodic damage-over-time effects: apply each due tick,
    /// reschedule, and retire the effect when its charges are spent.</summary>
    private void ProcessDotTicks(long now)
    {
        // Snapshot the due DOTs first: applying a tick can kill the victim and
        // clear its effects mid-pass (ClearAllEffectsOnDeath), so iterating the
        // live list by index is unsafe.
        List<ActiveSpellEffect>? due = null;
        foreach (var eff in _activeEffects)
            if (eff.DotCharges > 0 && now >= eff.DotNextTickMs)
                (due ??= []).Add(eff);
        if (due == null) return;

        foreach (var eff in due)
        {
            if (!_activeEffects.Contains(eff)) continue; // already retired elsewhere
            if (eff.Target.IsDeleted || eff.Target.IsDead)
            {
                DetachSpellMemory(eff);
                _activeEffects.Remove(eff);
                NotifySpellBuff(eff.Target, eff.Spell, false);
                continue;
            }

            if (!TryApplyCustomDotTick(eff, now))
            {
                int damage = ComputeDotDamage(eff, eff.Target);
                eff.DotCharges--;
                eff.DotNextTickMs = now + NextDotIntervalMs(eff);
                ApplyDotDamage(eff.Target, eff, damage);
            }

            if (eff.DotCharges <= 0 && _activeEffects.Remove(eff))
            {
                // RevertDeltas (not a bare memory detach): a custom tick
                // effect can carry an applied stat flag (Hallucinating) that
                // must clear when the charges run dry before the timer.
                RevertDeltas(eff);
                NotifySpellBuff(eff.Target, eff.Spell, false);
                Character.OnSpellEffectRemove?.Invoke(eff.Target, (int)eff.Spell);
                FireSpellSectionStage(eff.Spell, "EffectRemove", eff.Target);
            }
        }
    }

    /// <summary>Non-damage periodic behaviors (Source-X Spell_Equip_OnTick):
    /// hallucination trip sounds every 15-30 s, alcohol stamina/mana drain
    /// with hiccups and an early-sober chance. Returns true when the effect
    /// ticked here instead of the damage path.</summary>
    private bool TryApplyCustomDotTick(ActiveSpellEffect eff, long now)
    {
        switch (eff.Spell)
        {
            case SpellType.Hallucination:
                eff.DotCharges--;
                eff.DotNextTickMs = now + 15_000 + _rand.Next(15_001);
                // The trip sound goes ONLY to the hallucinating client
                // (Source-X m_pClient->addSound), and the view refresh
                // re-rolls the random hues the client sees.
                OnPlaySoundTo?.Invoke(eff.Target,
                    (ushort)(_rand.Next(2) == 0 ? 0x0243 : 0x0244));
                OnViewRefresh?.Invoke(eff.Target);
                return true;
            case SpellType.Ale:
            case SpellType.Wine:
            case SpellType.Liquor:
            {
                eff.DotCharges--;
                if (_rand.Next(100) < 10)
                    eff.DotCharges--; // chance to sober up quickly
                eff.DotNextTickMs = now + eff.DotIntervalMs;
                var drunk = eff.Target;
                drunk.Stam = (short)Math.Max(0, drunk.Stam - 1);
                drunk.Mana = (short)Math.Max(0, drunk.Mana - 1);
                if (_rand.Next(3) == 0)
                {
                    // Source-X: Speak(hic) + random facing + ANIM_BOW when
                    // not mounted — visible drunkenness, not a private note.
                    OnOverheadEmote?.Invoke(drunk, "*hic*");
                    if (!drunk.IsStatFlag(StatFlag.OnHorse))
                    {
                        drunk.Direction = (Direction)_rand.Next(8);
                        Character.BroadcastNearby?.Invoke(drunk.Position, 18,
                            new SphereNet.Network.Packets.Outgoing.PacketAnimation(
                                drunk.Uid.Value, (ushort)AnimationType.Bow), 0);
                    }
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Revert and retire every active effect matching
    /// <paramref name="match"/>, firing the removal callbacks (RevertDeltas +
    /// NotifySpellBuff + @EffectRemove / OnSpellEffectRemove). A callback can
    /// clear further effects on the same target mid-pass (e.g. by killing it),
    /// so a snapshot is taken up front and the live list is never indexed;
    /// Remove returning false means a callback already retired that effect, so
    /// it is reverted and observed at most once. Mirrors ProcessDotTicks.</summary>
    private void RemoveMatchingEffects(Func<ActiveSpellEffect, bool> match)
    {
        List<ActiveSpellEffect>? snapshot = null;
        foreach (var eff in _activeEffects)
            if (match(eff))
                (snapshot ??= []).Add(eff);
        if (snapshot == null) return;

        foreach (var eff in snapshot)
        {
            if (!_activeEffects.Remove(eff)) continue; // already retired by a callback
            RevertDeltas(eff);
            NotifySpellBuff(eff.Target, eff.Spell, false);
            Character.OnSpellEffectRemove?.Invoke(eff.Target, (int)eff.Spell);
            FireSpellSectionStage(eff.Spell, "EffectRemove", eff.Target);
        }
    }

    /// <summary>Walk the active-effect list once per world tick and undo
    /// any whose expire tick has passed. Called from Program.cs main
    /// loop. Cheap when the list is empty; no-op otherwise.</summary>
    public void ProcessExpirations(long now)
    {
        ProcessDotTicks(now);

        // Snapshot expired/orphaned effects first: a removal callback can clear
        // other effects on the same target mid-pass, so index iteration over
        // the live list is unsafe (an ArgumentOutOfRangeException here escapes
        // to the main tick and takes the server down).
        List<ActiveSpellEffect>? due = null;
        foreach (var eff in _activeEffects)
            if (eff.Target.IsDeleted || now >= eff.ExpireTick)
                (due ??= []).Add(eff);
        if (due == null) return;

        foreach (var eff in due)
        {
            if (!_activeEffects.Remove(eff)) continue; // already retired by a callback
            if (eff.Target.IsDeleted)
            {
                DetachSpellMemory(eff);
                continue;
            }
            RevertDeltas(eff);
            NotifySpellBuff(eff.Target, eff.Spell, false);
            Character.OnSpellEffectRemove?.Invoke(eff.Target, (int)eff.Spell);
            FireSpellSectionStage(eff.Spell, "EffectRemove", eff.Target);
        }
    }

    /// <summary>Revert exactly what <see cref="ScheduleEffectExpiry"/>
    /// recorded for this effect — stat deltas subtracted, flag cleared,
    /// light level restored + 0x4E refresh dispatched. Safe to call
    /// even when the effect didn't touch a given field (the delta will
    /// be 0 / flag None / LightChanged false).</summary>
    private void RevertDeltas(ActiveSpellEffect eff) => RevertDeltas(eff, detachMemory: true);

    /// <param name="detachMemory">True for a real effect removal (deletes the
    /// IT_SPELL memory item, Source-X behaviour). False for the save-time
    /// stat-unwind (<see cref="RevertAllForSave"/>), which only strips derived
    /// stats before persistence and is immediately paired with
    /// <see cref="ReapplyAllAfterSave"/> — the effect (and its memory) live on.</param>
    private void RevertDeltas(ActiveSpellEffect eff, bool detachMemory)
    {
        // Source-X: removing the effect deletes its IT_SPELL memory item.
        if (detachMemory)
            DetachSpellMemory(eff);
        var t = eff.Target;
        if (eff.StrDelta != 0) t.Str -= eff.StrDelta;
        if (eff.DexDelta != 0) t.Dex -= eff.DexDelta;
        if (eff.IntDelta != 0) t.Int -= eff.IntDelta;
        if (eff.ArmorDelta != 0)
            t.ProtectionArmor = Math.Max(0, t.ProtectionArmor - eff.ArmorDelta);
        if (eff.MeditationDelta != 0)
            t.SetSkill(SkillType.Meditation, (ushort)Math.Max(0,
                t.GetSkill(SkillType.Meditation) - eff.MeditationDelta));
        if (eff.Spell == SpellType.HorrificBeast)
            t.HorrificBeastActive = false;
        if (eff.Spell == SpellType.WraithForm)
            t.WraithFormActive = false;
        if (eff.Spell == SpellType.CurseWeapon)
            t.CurseWeaponLevel = 0;
        if (eff.Spell == SpellType.MindRot)
            t.MindRotActive = false;
        if (eff.Spell == SpellType.CorpseSkin)
            ApplyCorpseSkinResists(t, -1);
        if (eff.Spell == SpellType.LichForm)
        {
            t.LichFormActive = false;
            ApplyLichFormResists(t, -1);
        }
        if (eff.Spell == SpellType.VampiricEmbrace)
        {
            t.VampiricEmbraceActive = false;
            ApplyVampiricResists(t, -1);
        }
        if (eff.Spell == SpellType.BloodOath)
        {
            // Both ends of the bond drop their icon when it breaks — the caster
            // holds the effect, the bonded enemy only ever got the curse icon.
            Character.OnClientBuffChanged?.Invoke(t, BuffIcon.BloodOathCaster, false, 0, null);
            if (eff.BloodOathEnemy.IsValid && _world.FindChar(eff.BloodOathEnemy) is { } bonded)
                Character.OnClientBuffChanged?.Invoke(bonded, BuffIcon.BloodOathCurse, false, 0, null);
            t.BloodOathEnemy = Serial.Invalid;
            t.BloodOathLevel = 0;
        }
        if (eff.AppliedFlag != StatFlag.None) t.ClearStatFlag(eff.AppliedFlag);
        if (eff.NameChanged && eff.OldName != null)
        {
            t.Name = eff.OldName;
            Character.OnAppearanceChanged?.Invoke(t);
        }
        if (eff.BodyChanged) t.BodyId = eff.OldBodyId;
        if (eff.BodyChanged && IsPolymorphLayerSpell(eff.Spell))
        {
            t.ClearStatFlag(StatFlag.Polymorph);
            if (t.OBody != 0 && t.BodyId == t.OBody)
                t.OBody = 0;
        }
        if (eff.LightChanged)
        {
            t.LightLevel = eff.OldLightLevel;
            OnPersonalLightChanged?.Invoke(t);
        }
        // Source-X refreshes the whole view when hallucination ENDS too —
        // without it the last tick's random bodies/hues stay on the client
        // until each object happens to update naturally. Skipped for the
        // save-time stat unwind (detachMemory=false), which is not a real
        // removal and is immediately re-applied.
        if (detachMemory && eff.Spell == SpellType.Hallucination)
            OnViewRefresh?.Invoke(t);
    }

    private void ApplyDeltas(ActiveSpellEffect eff)
    {
        var t = eff.Target;
        if (eff.StrDelta != 0) t.Str += eff.StrDelta;
        if (eff.DexDelta != 0) t.Dex += eff.DexDelta;
        if (eff.IntDelta != 0) t.Int += eff.IntDelta;
        if (eff.ArmorDelta != 0)
            t.ProtectionArmor = (int)Math.Min(
                int.MaxValue, (long)t.ProtectionArmor + eff.ArmorDelta);
        if (eff.MeditationDelta != 0)
            t.SetSkill(SkillType.Meditation, (ushort)Math.Min(ushort.MaxValue,
                t.GetSkill(SkillType.Meditation) + eff.MeditationDelta));
        if (eff.Spell == SpellType.HorrificBeast)
            t.HorrificBeastActive = true;
        if (eff.Spell == SpellType.WraithForm)
            t.WraithFormActive = true;
        if (eff.Spell == SpellType.CurseWeapon)
            t.CurseWeaponLevel = eff.CurseWeaponLevel;
        if (eff.Spell == SpellType.MindRot)
            t.MindRotActive = true;
        if (eff.Spell == SpellType.CorpseSkin)
            ApplyCorpseSkinResists(t, +1);
        if (eff.Spell == SpellType.LichForm)
        {
            t.LichFormActive = true;
            ApplyLichFormResists(t, +1);
        }
        if (eff.Spell == SpellType.VampiricEmbrace)
        {
            t.VampiricEmbraceActive = true;
            ApplyVampiricResists(t, +1);
        }
        if (eff.Spell == SpellType.BloodOath)
        {
            t.BloodOathEnemy = eff.BloodOathEnemy;
            t.BloodOathLevel = eff.BloodOathLevel;
        }
        if (eff.AppliedFlag != StatFlag.None) t.SetStatFlag(eff.AppliedFlag);
        if (eff.NameChanged && eff.NewName != null)
        {
            t.Name = eff.NewName;
            Character.OnAppearanceChanged?.Invoke(t);
        }
        if (eff.BodyChanged && eff.NewBodyId != 0)
            t.BodyId = eff.NewBodyId;
        if (eff.LightChanged)
        {
            t.LightLevel = eff.NewLightLevel;
            OnPersonalLightChanged?.Invoke(t);
        }
    }

    /// <summary>Revert polymorph body on death when MAGICF bit 0x0008 is set.</summary>
    public void RevertPolymorphOnDeath(Character ch)
    {
        if (!IsMagicFlag(MagicConfigFlags.PolymorphRevertDeath))
            return;

        for (int i = _activeEffects.Count - 1; i >= 0; i--)
        {
            var eff = _activeEffects[i];
            if (eff.Target != ch || !eff.BodyChanged || eff.Spell != SpellType.Polymorph)
                continue;
            RevertDeltas(eff);
            _activeEffects.RemoveAt(i);
            NotifySpellBuff(ch, eff.Spell, false);
            Character.OnSpellEffectRemove?.Invoke(ch, (int)eff.Spell);
            FireSpellSectionStage(eff.Spell, "EffectRemove", ch);
            return;
        }
    }

    public void ClearAllEffectsOnDeath(Character ch)
    {
        RevertPolymorphOnDeath(ch);
        RemoveMatchingEffects(eff => eff.Target == ch);
    }

    /// <summary>Original body for resurrect after polymorph (OBody or active effect).</summary>
    public ushort GetResurrectBody(Character ch)
    {
        foreach (var eff in _activeEffects)
        {
            if (eff.Target == ch && eff.BodyChanged && eff.OldBodyId != 0)
                return eff.OldBodyId;
        }
        return ch.OBody != 0 ? ch.OBody : ch.BodyId;
    }

    public IEnumerable<string> GetPersistedEffectRecords(Character ch, long now)
    {
        foreach (var eff in _activeEffects)
        {
            if (eff.Target != ch || eff.Target.IsDeleted)
                continue;
            // In-flight damage-over-time ticks (Pain Spike/Strangle) are short-
            // lived; their per-tick state is not persisted, so a restart simply
            // ends them rather than resuming with a stale schedule. Blood Oath is
            // likewise short-lived and bound to a live enemy uid, so it ends too.
            if (eff.DotCharges > 0 || eff.Spell == SpellType.BloodOath)
                continue;
            yield return SerializeEffect(eff, now);
        }
    }

    /// <summary>Retire magical invisibility when Reveal, movement, combat or
    /// casting exposes the character before the original timer expires.</summary>
    public void BreakInvisibility(Character victim)
    {
        RemoveMatchingEffects(eff => eff.Target == victim && eff.Spell == SpellType.Invisibility);
    }

    /// <summary>Rebuild the client's buff bar after login/resync. Removing each
    /// icon first avoids the client retaining a stale countdown across reconnects.</summary>
    public void ResendBuffs(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.Hidden | StatFlag.Insubstantial))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Hidden, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Hidden, true, 0, null);
        }
        if (ch.IsStatFlag(StatFlag.Meditation))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.ActiveMeditation, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.ActiveMeditation, true, 0, null);
        }
        if (ch.IsStatFlag(StatFlag.Criminal))
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.CriminalStatus, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.CriminalStatus, true, 0, null);
        }
        if (ch.IsPoisoned)
        {
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Poison, false, 0, null);
            Character.OnClientBuffChanged?.Invoke(ch, BuffIcon.Poison, true,
                ch.Poison.RemainingDurationSeconds, null);
        }

        long now = Environment.TickCount64;
        foreach (var eff in _activeEffects)
        {
            if (eff.Target != ch || eff.Target.IsDeleted || eff.ExpireTick <= now)
                continue;
            NotifySpellBuff(ch, eff.Spell, false);
            long remainingMs = eff.ExpireTick - now;
            NotifySpellBuff(ch, eff.Spell, true,
                (ushort)Math.Clamp((remainingMs + 999) / 1000, 1, ushort.MaxValue),
                GetBuffMagnitude(eff));
        }
    }

    /// <summary>Effect magnitude for the buff tooltip. Prefers the value the
    /// cast recorded; after a world reload that field is gone, so fall back to
    /// the persisted stat delta — the same number Source-X keeps in the spell
    /// memory's m_spelllevel.</summary>
    private static int GetBuffMagnitude(ActiveSpellEffect eff)
    {
        if (eff.BuffMagnitude > 0)
            return eff.BuffMagnitude;
        int delta = Math.Max(Math.Abs(eff.StrDelta),
            Math.Max(Math.Abs(eff.DexDelta), Math.Abs(eff.IntDelta)));
        return delta;
    }

    public int RestorePersistedEffectsFromWorld()
    {
        int count = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is Character ch)
                count += RestorePersistedEffects(ch);
        }
        return count;
    }

    public int RestorePersistedEffects(Character ch)
    {
        if (ch.PendingSpellEffectRecords.Count == 0)
            return 0;

        int count = 0;
        long now = Environment.TickCount64;
        foreach (string record in ch.PendingSpellEffectRecords)
        {
            if (!TryDeserializeEffect(ch, record, now, out var eff))
                continue;
            _activeEffects.Add(eff);
            ApplyDeltas(eff);
            // Re-equip the IT_SPELL memory item for the restored effect. The
            // caster uid is not persisted in the effect record, so the memory's
            // LINK is left unattributed (Serial.Invalid), like a Source-X memory
            // whose source logged out.
            AttachSpellMemory(null, eff, GetSpellDef(eff.Spell));
            count++;
        }
        ch.ClearPendingSpellEffectRecords();
        return count;
    }

    private static string SerializeEffect(ActiveSpellEffect eff, long now)
    {
        long remainingMs = Math.Max(0, eff.ExpireTick - now);
        return string.Join('|',
            PersistedEffectVersion.ToString(CultureInfo.InvariantCulture),
            ((ushort)eff.Spell).ToString(CultureInfo.InvariantCulture),
            remainingMs.ToString(CultureInfo.InvariantCulture),
            eff.StrDelta.ToString(CultureInfo.InvariantCulture),
            eff.DexDelta.ToString(CultureInfo.InvariantCulture),
            eff.IntDelta.ToString(CultureInfo.InvariantCulture),
            eff.OldLightLevel.ToString(CultureInfo.InvariantCulture),
            eff.NewLightLevel.ToString(CultureInfo.InvariantCulture),
            eff.LightChanged ? "1" : "0",
            ((uint)eff.AppliedFlag).ToString(CultureInfo.InvariantCulture),
            eff.OldBodyId.ToString(CultureInfo.InvariantCulture),
            eff.NewBodyId.ToString(CultureInfo.InvariantCulture),
            eff.BodyChanged ? "1" : "0",
            EncodeEffectString(eff.OldName),
            EncodeEffectString(eff.NewName),
            eff.NameChanged ? "1" : "0",
            eff.ArmorDelta.ToString(CultureInfo.InvariantCulture),
            eff.CurseWeaponLevel.ToString(CultureInfo.InvariantCulture),
            eff.MeditationDelta.ToString(CultureInfo.InvariantCulture),
            eff.BuffMagnitude.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryDeserializeEffect(Character target, string record, long now, out ActiveSpellEffect eff)
    {
        eff = null!;
        var parts = record.Split('|');
        if (parts.Length is not (16 or 17 or 18 or 19 or 20))
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) ||
            version != PersistedEffectVersion)
            return false;
        if (!ushort.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort spellRaw))
            return false;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long remainingMs))
            return false;
        if (remainingMs <= 0)
            return false;
        if (!short.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out short strDelta))
            return false;
        if (!short.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out short dexDelta))
            return false;
        if (!short.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out short intDelta))
            return false;
        if (!byte.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte oldLight))
            return false;
        if (!byte.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte newLight))
            return false;
        if (!uint.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint flagRaw))
            return false;
        var appliedFlag = (StatFlag)flagRaw;
        if (appliedFlag is not (StatFlag.None or StatFlag.Freeze or StatFlag.Invisible or
            StatFlag.NightSight or StatFlag.Reactive or StatFlag.ArcherCanMove or
            StatFlag.Incognito or StatFlag.Reflection or StatFlag.Polymorph or
            StatFlag.Stone or StatFlag.Hallucinating))
            return false;
        if (!ushort.TryParse(parts[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort oldBody))
            return false;
        if (!ushort.TryParse(parts[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort newBody))
            return false;
        if (!TryDecodeEffectString(parts[13], out string? oldName))
            return false;
        if (!TryDecodeEffectString(parts[14], out string? newName))
            return false;
        int armorDelta = 0;
        if (parts.Length >= 17 &&
            !int.TryParse(parts[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out armorDelta))
            return false;
        int curseWeaponLevel = 0;
        if (parts.Length >= 18 &&
            !int.TryParse(parts[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out curseWeaponLevel))
            return false;
        int meditationDelta = 0;
        if (parts.Length >= 19 &&
            !int.TryParse(parts[18], NumberStyles.Integer, CultureInfo.InvariantCulture, out meditationDelta))
            return false;
        // Buff tooltip magnitude (Source-X m_spelllevel). Absent in records
        // written before it was tracked; those fall back to the stat delta.
        int buffMagnitude = 0;
        if (parts.Length >= 20 &&
            !int.TryParse(parts[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out buffMagnitude))
            return false;

        long expireTick = remainingMs > long.MaxValue - now ? long.MaxValue : now + remainingMs;
        eff = new ActiveSpellEffect
        {
            Target = target,
            Spell = (SpellType)spellRaw,
            ExpireTick = expireTick,
            StrDelta = strDelta,
            DexDelta = dexDelta,
            IntDelta = intDelta,
            ArmorDelta = Math.Max(0, armorDelta),
            MeditationDelta = Math.Max(0, meditationDelta),
            OldLightLevel = oldLight,
            NewLightLevel = newLight,
            LightChanged = parts[8] == "1",
            AppliedFlag = appliedFlag,
            OldBodyId = oldBody,
            NewBodyId = newBody,
            BodyChanged = parts[12] == "1",
            OldName = oldName,
            NewName = newName,
            NameChanged = parts[15] == "1",
            CurseWeaponLevel = curseWeaponLevel,
            BuffMagnitude = Math.Max(0, buffMagnitude),
        };
        return true;
    }

    private static bool IsProtectionSpell(SpellType spell) =>
        spell is SpellType.Protection or SpellType.ArchProtection;

    private static bool IsPolymorphLayerSpell(SpellType spell) =>
        spell is SpellType.Polymorph or SpellType.HorrificBeast or SpellType.WraithForm
            or SpellType.LichForm or SpellType.VampiricEmbrace
            or SpellType.ReaperForm or SpellType.StoneForm
            or SpellType.Chameleon or SpellType.BeastForm or SpellType.MonsterForm;

    private static string EncodeEffectString(string? value)
    {
        if (value == null)
            return "";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static bool TryDecodeEffectString(string value, out string? decoded)
    {
        decoded = null;
        if (value.Length == 0)
            return true;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Revert all active buff deltas so character stats are saved
    /// clean (base values only). Call before WorldSaver runs.</summary>
    public void RevertAllForSave()
    {
        foreach (var eff in _activeEffects)
            RevertDeltas(eff, detachMemory: false);
    }

    /// <summary>Re-apply all active buff deltas after a save completes.
    /// Paired with <see cref="RevertAllForSave"/>.</summary>
    public void ReapplyAllAfterSave()
    {
        foreach (var eff in _activeEffects)
            ApplyDeltas(eff);
    }
}

/// <summary>
/// Registry of all spell definitions. Populated from scripts.
/// </summary>
public sealed class SpellRegistry
{
    private readonly Dictionary<SpellType, SpellDef> _spells = [];

    public void Clear() => _spells.Clear();
    public void Register(SpellDef def) => _spells[def.Id] = def;
    public SpellDef? Get(SpellType id) => _spells.GetValueOrDefault(id);
    public IEnumerable<SpellDef> GetAll() => _spells.Values;
    public int Count => _spells.Count;
}

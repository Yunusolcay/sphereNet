using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Game.Death;

/// <summary>
/// Corpse and loot system. Maps to CChar::Death and CItemCorpse in Source-X.
/// Handles death processing, corpse creation, loot drop, and decay.
/// </summary>
public sealed class DeathEngine
{
    private readonly GameWorld _world;

    /// <summary>Corpse decay time for players (in seconds).</summary>
    public int CorpseDecayPlayer { get; set; } = 900; // 15 minutes

    /// <summary>Corpse decay time for NPCs (in seconds).</summary>
    public int CorpseDecayNPC { get; set; } = 300; // 5 minutes

    /// <summary>Whether looting others' corpses is a criminal act.</summary>
    public bool LootingIsACrime { get; set; } = true;

    /// <summary>Fired when a character is killed.</summary>
    public event Action<Character, Character?>? OnDeath;

    /// <summary>Party manager reference for loot rights.</summary>
    public Party.PartyManager? PartyManager { get; set; }

    /// <summary>Optional script trigger dispatcher for corpse item hooks.</summary>
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    /// <summary>Host hook: dismount a mounted victim before the corpse is
    /// made (Source-X CChar::Death runs Horse_UnMount first). Wired to the
    /// mount engine + appearance broadcasts; null in bare test setups.</summary>
    public Action<Character>? DismountHook { get; set; }

    /// <summary>Host hook: cancel any open secure trade the victim is part of
    /// (Source-X CChar::Death Trade_Delete) — otherwise the trade contents
    /// bypass the corpse loot drop and the partner keeps a stale window.</summary>
    public Action<Character>? CancelTradesHook { get; set; }

    /// <summary>Host hook: the "killed by ..." record (Source-X LOGM_KILLS log
    /// + party SysMessageAll). Args: the victim, the formatted message.</summary>
    public Action<Character, string>? KillMessageHook { get; set; }

    /// <summary>Host hook: the vanish burst when a summon dies corpseless
    /// (Source-X MakeCorpse ITEMID_FX_SPELL_FAIL).</summary>
    public Action<Character>? ConjuredVanishEffectHook { get; set; }

    public DeathEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Process a character's death. Maps to CChar::Death in Source-X.
    /// Creates corpse, drops loot, handles NPC cleanup.
    /// </summary>
    public Item? ProcessDeath(Character victim, Character? killer = null)
    {
        if (victim.IsDead)
            return null;

        // Source-X CChar::Death: an invulnerable character cannot die.
        if (victim.IsStatFlag(StatFlag.Invul))
            return null;

        Character? effectiveKiller = killer;
        if (killer != null && killer.NpcMaster.IsValid)
        {
            var master = _world.FindChar(killer.NpcMaster);
            if (master != null && !master.IsDeleted)
                effectiveKiller = master;
        }
        if (effectiveKiller == null)
        {
            // Some death sources arrive without a final-blow argument even
            // though damage attribution is present (delayed poison/effects).
            // Source-X still credits m_lastAttackers; choose the strongest
            // valid contributor as the representative killer, while the
            // normal offender loop below continues to credit every attacker.
            foreach (var rec in victim.Attackers
                .Where(r => !r.Ignored && r.TotalDamage > 0)
                .OrderByDescending(r => r.TotalDamage))
            {
                var candidate = _world.FindChar(rec.Uid);
                if (candidate == null || candidate.IsDeleted) continue;
                effectiveKiller = candidate.ResolveOwnerCharacter() ?? candidate;
                if (!effectiveKiller.IsDeleted)
                    break;
                effectiveKiller = null;
            }
        }

        // @Death — Source-X fires it before any death processing; RETURN 1 cancels
        // the death entirely (no corpse, the victim is not killed). Centralised
        // here so every death entry point (combat, spell, NPC, GM, offline) honours
        // it instead of firing-and-ignoring at each call site.
        // Source-X OnTrigger(CTRIG_Death, <empty>, this): SRC is the dying char
        // itself, not the killer. The killer stays reachable as ARGO (a SphereNet
        // extension — Source-X seeds no args here, so faithful scripts never read it).
        if (TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                new TriggerArgs { CharSrc = victim, O1 = effectiveKiller }) == TriggerResult.True)
            return null;

        var creditedOffenders = new List<Character>();
        int attackerCount = 1;
        if (effectiveKiller != null)
        {
            var offenders = new List<Character>();
            var creditedUids = new HashSet<uint>();
            foreach (var offender in EnumerateOffenders(victim, effectiveKiller))
                if (creditedUids.Add(offender.Uid.Value))
                    offenders.Add(offender);
            attackerCount = Math.Max(1, offenders.Count);

            foreach (var offender in offenders)
            {
                if (TriggerDispatcher?.FireCharTrigger(offender, CharTrigger.Kill,
                        new TriggerArgs
                        {
                            CharSrc = offender,
                            O1 = victim,
                            N1 = victim.Attackers.Count
                        }) != TriggerResult.True)
                    creditedOffenders.Add(offender);
            }
        }

        // Sleeping is cleared by Kill() below (Source-X clears it before
        // MakeCorpse too) — capture it first for the corpse forensics stamp.
        bool wasSleeping = victim.IsStatFlag(StatFlag.Sleeping);

        // Champion wave credit — Source-X routes this through the spawn
        // back-link at object destroy (CObjBase dtor → CCChampion::DelObj);
        // we credit at death time via the SPAWNITEM tag so candles/levels
        // advance while the corpse still exists.
        if (!victim.IsPlayer &&
            victim.TryGetTag("SPAWNITEM", out string? spawnItemUid) &&
            !string.IsNullOrEmpty(spawnItemUid))
        {
            string hexPart = spawnItemUid.Trim();
            if (hexPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hexPart = hexPart[2..];
            else if (hexPart.StartsWith('0') && hexPart.Length > 1) hexPart = hexPart[1..];
            if (uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out uint spawnerUid) &&
                _world.FindItem(new Serial(spawnerUid)) is { Champion: not null } altar)
            {
                altar.Champion.OnMemberDeath(victim);
            }
        }

        // Kill the character
        victim.Kill();

        // Source-X CChar::Death deletes any open trade window before the
        // corpse forms; the trade items return to the pack and so reach the
        // corpse with the rest of the loot.
        CancelTradesHook?.Invoke(victim);

        // Source-X CChar::Death clears the victim's FIGHT / HARMEDBY
        // memories — the ghost holds no grudges (and no self-defence
        // rights) from the fight that killed it.
        foreach (var mem in new List<Item>(victim.Memories))
            victim.Memory_ClearTypes(mem, MemoryType.Fight | MemoryType.HarmedBy);

        // Source-X CChar::Death order: the rider leaves the saddle before the
        // corpse is made — otherwise the mount-layer item is snapshotted into
        // the death state and the client keeps drawing a mounted body under
        // the ghost.
        if (victim.IsMounted)
            DismountHook?.Invoke(victim);

        // Karma/Fame/murder credit — skipped when @Kill returned 1.
        if (effectiveKiller != null)
        {
            // Source-X CChar::Death credits EVERY damaging attacker and divides the
            // fame/karma/experience reward by the attacker count (Noto_Kill's
            // iTotalKillers), so a group splits the spoils instead of the final
            // blow taking all of it. Deduped, pets credited to their master.
            foreach (var offender in creditedOffenders)
            {
                ApplyKarmaFameChange(offender, victim, attackerCount);

                // Experience award (Source-X ChangeExperience on kill): the
                // victim's own EXP value is the prize, split across attackers.
                if (!victim.IsPlayer && victim.Exp > 0)
                    offender.ChangeExperience(victim.Exp / attackerCount);
            }

            // PvP murder tracking — Source-X Noto_Kill marks EVERY unprovoked
            // attacker of an innocent, not just the final-blow killer, so ganking
            // an innocent flags the whole group.
            MarkMurderers(victim, creditedOffenders);
        }

        // Source-X kill record (CCharAct.cpp:4357-4389): "'<victim>' was
        // killed by 'A', 'B'." — logged for player deaths and echoed to the
        // victim's party (an unattributed death reads "accident").
        if (KillMessageHook != null && victim.IsPlayer)
        {
            string names = creditedOffenders.Count == 0
                ? ""
                : string.Join(", ", creditedOffenders
                    .Select(o => $"'{o.GetDisplayName()}'").Distinct());
            KillMessageHook(victim,
                $"'{victim.GetDisplayName()}' was killed by {(names.Length > 0 ? names : "accident")}.");
        }

        // Source-X clears m_lastAttackers immediately after kill credit and
        // the kill record are built. Player ghosts otherwise retained stale
        // damage contributors until resurrection (and persisted them on save).
        victim.ClearAttackers();

        int deathFlags = GetDeathFlags(victim);

        // Source-X CChar::Death player penalties (CCharAct.cpp:4443-4470):
        // a tenth of the experience is lost (min 1), a tenth of the fame
        // unless DEATHFLAGS & DEATH_NOFAMECHANGE (0x01), and the deaths
        // counter increments.
        if (victim.IsPlayer)
        {
            victim.ChangeExperience(-Math.Max(1, victim.Exp / 10));
            if ((deathFlags & 0x01) == 0)
                ApplyFame(victim, -(victim.Fame / 10));
            victim.Deaths = (short)Math.Min(victim.Deaths + 1, short.MaxValue);
        }

        // Source-X CChar::Death order is critical for players:
        //   1) MakeCorpse  (corpse.Amount = current/original body ID)
        //   2) Broadcast PacketDeath (0xAF) to nearby
        //   3) SetID(ghost) + SetHue(0)  ← only after the corpse exists
        // If we fire OnDeath here (which transitions the player to a ghost
        // body in OnCharacterDeath) BEFORE CreateCorpse runs, the corpse
        // will be created with amount=0x192 (ghost) instead of the player's
        // real body, and the corpse on the ground renders as a ghost shape
        // instead of a normal humanoid corpse. Source-X ordering avoids
        // exactly this.
        // Source-X MakeCorpse: summoned creatures and a DEATH_NOCORPSE flag leave
        // no corpse — they simply vanish (DeleteObject refreshes nearby clients).
        if (ShouldLeaveNoCorpse(victim, deathFlags))
        {
            // Source-X MakeCorpse: a summon that leaves no corpse bursts a
            // spell-fizzle effect instead of silently vanishing.
            if (victim.IsSummoned)
                ConjuredVanishEffectHook?.Invoke(victim);
            if (!victim.IsPlayer && !victim.IsBonded)
            {
                _world.DeleteObject(victim);
                victim.Delete();
            }
            return null;
        }

        var corpse = CreateCorpse(victim, wasSleeping);
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        corpse.SetTag("OWNER_UUID", victim.Uuid.ToString("D"));

        if (effectiveKiller != null)
        {
            corpse.SetTag("KILLER_UID", effectiveKiller.Uid.Value.ToString());
            corpse.SetTag("KILLER_UUID", effectiveKiller.Uuid.ToString("D"));
        }

        // An item held on the cursor is parented to the character but is neither
        // equipped nor in the pack, so the loot walk below never saw it: it stayed
        // attached to the corpse's owner and the ghost could simply drop it back
        // into its own pack. Source-X reaches it because a dragged item sits on
        // LAYER_DRAGGING and UnEquipAllItems walks that layer (CCharAct.cpp:636).
        // Settling it into the pack first puts it back under the normal rules,
        // protected-item handling included.
        ReleaseDraggedItem(victim);

        // Drop equipped items and backpack contents to corpse — unless DEATH_NOLOOTDROP
        // keeps everything on the (now-dead) body. (DEATH_NOLOOTDROP = 0x04.)
        if ((deathFlags & 0x04) == 0)
        {
            if (victim.IsPlayer)
                DropLootToCorpse(victim, corpse);
            else
                DropNpcLootToCorpse(victim, corpse);
        }

        // @DeathCorpse — fired on the victim once the corpse exists and the
        // loot has been transferred (Source-X CChar::Death fires it right
        // after MakeCorpse, which moves the items itself), with the corpse
        // as the argument object.
        TriggerDispatcher?.FireCharTrigger(victim, CharTrigger.DeathCorpse, new TriggerArgs
        {
            CharSrc = victim,
            O1 = corpse
        });

        // Now that the corpse has snapshotted the original body, fire the
        // death callbacks so OnCharacterDeath / OnNpcKill can swap the
        // mobile to its ghost body and broadcast the new appearance.
        OnDeath?.Invoke(victim, effectiveKiller);

        // Set corpse decay timer. Using the Item.DecayTime field (not a
        // TAG) routes this through the sector-tick Item.OnTick path —
        // the same mechanism spell fields and summoned items use.
        // Source-X does the same via CItem::_SetTimeout on the corpse,
        // driven from its sector, with no central scanner.
        int decaySeconds = victim.IsPlayer ? CorpseDecayPlayer : CorpseDecayNPC;
        corpse.DecayTime = Environment.TickCount64 + decaySeconds * 1000;

        // For NPCs, remove the mobile from world state immediately so it no
        // longer blocks movement or lingers in sector/object queries after the
        // corpse has been created. Bonded pets stay as ghosts (like players).
        if (!victim.IsPlayer && !victim.IsBonded)
        {
            _world.DeleteObject(victim);
            victim.Delete();
        }

        return corpse;
    }

    /// <summary>Source-X DEATHFLAGS (CChar.h): a per-character bitmask controlling
    /// corpse/loot/fame behaviour on death. Parsed from the DEATHFLAGS tag (hex or
    /// decimal). 0 when unset.</summary>
    private static int GetDeathFlags(Character victim)
    {
        if (!victim.TryGetTag("DEATHFLAGS", out string? df) || string.IsNullOrWhiteSpace(df))
            return 0;
        df = df.Trim();
        bool ok = df.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(df.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int v)
            : int.TryParse(df, out v);
        return ok ? v : 0;
    }

    /// <summary>Source-X MakeCorpse: no corpse for DEATH_NOCORPSE (0x02), or for a
    /// summoned creature unless DEATH_NOCONJUREDEFFECT (0x08) / DEATH_HASCORPSE
    /// (0x10) is set. Players always leave a corpse.</summary>
    private static bool ShouldLeaveNoCorpse(Character victim, int deathFlags)
    {
        if (victim.IsPlayer) return false;
        if ((deathFlags & 0x02) != 0) return true;
        if (victim.IsSummoned && (deathFlags & (0x08 | 0x10)) == 0) return true;
        return false;
    }

    /// <summary>Whether killer→victim is an unprovoked kill of an innocent, the
    /// only case Source-X Noto_Kill counts as murder (NotoThem &lt; NOTO_GUILD_SAME).
    /// False when the victim is a criminal/murderer (red or grey), or aggressed the
    /// killer first — in which case the killer holds a HarmedBy memory of the victim
    /// (Memory_Fight_Start tags the defender HarmedBy and the aggressor IAggressor).</summary>
    /// <summary>
    /// Mark a murder against every attacker of an innocent player victim — the
    /// final-blow killer plus everyone in the victim's attacker log — instead of
    /// just the killer (Source-X Noto_Kill loops the damage list). Each offender
    /// is resolved pet→master, deduped, and gated through @MurderMark (which can
    /// adjust the count, suppress the criminal flag, or block the mark).
    /// </summary>
    private void MarkMurderers(Character victim, IEnumerable<Character> offenders)
    {
        if (!victim.IsPlayer) return;

        var marked = new HashSet<uint>();
        foreach (var offender in offenders)
        {
            if (!offender.IsPlayer) continue;
            if (!marked.Add(offender.Uid.Value)) continue;
            if (!IsUnprovokedInnocentKill(offender, victim)) continue;

            int proposed = offender.Kills + 1;
            var decision = Character.OnMurderMark == null
                ? new Character.MurderMarkDecision(proposed, true)
                : Character.OnMurderMark(offender, victim, proposed);
            if (decision.Count.HasValue)
            {
                offender.Kills = (short)Math.Clamp(decision.Count.Value, 0, short.MaxValue);
                if (decision.MakeCriminal)
                    offender.SetCriminal(120_000); // 2 minutes criminal flag
            }
        }
    }

    /// <summary>The final-blow killer first, then every logged attacker, each
    /// resolved to its effective offender (a pet's hits credit its master).
    /// Ignored attackers (ATTACKER.n.IGNORE) are skipped.</summary>
    private IEnumerable<Character> EnumerateOffenders(Character victim, Character effectiveKiller)
    {
        yield return effectiveKiller;
        foreach (var rec in victim.Attackers)
        {
            if (rec.Ignored) continue;
            var attacker = _world.FindChar(rec.Uid);
            if (attacker == null || attacker.IsDeleted) continue;
            if (attacker.NpcMaster.IsValid)
            {
                var master = _world.FindChar(attacker.NpcMaster);
                if (master != null && !master.IsDeleted) { yield return master; continue; }
            }
            yield return attacker;
        }
    }

    private static bool IsUnprovokedInnocentKill(Character killer, Character victim)
    {
        if (victim.IsCriminal || victim.IsMurderer || victim.IsStatFlag(StatFlag.Criminal))
            return false;
        var killerMemOfVictim = killer.Memory_FindObj(victim.Uid);
        if (killerMemOfVictim != null && killerMemOfVictim.IsMemoryTypes(MemoryType.HarmedBy))
            return false; // victim struck first — self-defence, not murder
        return true;
    }

    /// <summary>Apply Karma/Fame changes when killer kills victim, divided by the
    /// total attacker count (Source-X Noto_Kill's iTotalKillers split):
    /// Calc_FameKill + Calc_KarmaKill + Calc_KarmaScale, each share /attackerCount.</summary>
    private static void ApplyKarmaFameChange(Character killer, Character victim, int attackerCount)
    {
        if (attackerCount < 1) attackerCount = 1;

        // Fame: Source-X Calc_FameKill — PC kill /10, NPC kill /200 — then split by
        // the attacker count. No per-kill magnitude cap (Source-X only clamps the
        // running total to 0..10000, which ApplyFame does). A zero-fame victim
        // still grants zero — no forced minimum — so trash mobs can't be farmed.
        int rawFame = Math.Max(0, (int)victim.Fame);
        int fameGain = (victim.IsPlayer ? rawFame / 10 : rawFame / 200) / attackerCount;
        ApplyFame(killer, fameGain);

        // Source-X Calc_KarmaKill: killing a criminal / red incurs NO karma change
        // — the would-be loss is clamped to 0 and no positive karma is awarded.
        if (victim.IsCriminal || victim.IsMurderer)
            return;

        // Source-X Calc_KarmaKill: karma change = negative of victim karma
        int karmaChange = -victim.Karma;
        if (victim.IsPlayer)
        {
            if (karmaChange < 0 && karmaChange > -5000)
                karmaChange = -5000;
            karmaChange /= 10;
        }
        else
        {
            if (karmaChange < 0 && karmaChange > -1000)
                karmaChange = -1000;
            karmaChange /= 20;
        }

        // Split across attackers BEFORE the diminishing-returns scale (Source-X
        // passes Calc_KarmaKill / iTotalKillers into Noto_Karma, which then scales).
        karmaChange = ScaleKarma(killer.Karma, karmaChange / attackerCount);
        ApplyKarma(killer, karmaChange);
    }

    // Config-driven fame/karma limits (sphere.ini MaxFame / MaxKarma / MinKarma).
    // Defaults match the previous hardcoded values, so unset configs behave identically.
    public static int MaxFame = 10000;
    public static int MaxKarma = 10000;
    public static int MinKarma = -10000;

    /// <summary>Apply a Fame delta after firing @FameChange (Source-X Noto_Fame).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [0, MaxFame].</summary>
    private static void ApplyFame(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnFameChanging != null)
        {
            int? adjusted = Character.OnFameChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Fame = (short)Math.Clamp(killer.Fame + delta, 0, MaxFame);
    }

    /// <summary>Apply a Karma delta after firing @KarmaChange (Source-X Noto_Karma).
    /// A script returning null cancels the change; otherwise the (possibly
    /// adjusted) delta is clamped into [-10000, 10000].</summary>
    private static void ApplyKarma(Character killer, int delta)
    {
        if (delta == 0) return;
        if (Character.OnKarmaChanging != null)
        {
            int? adjusted = Character.OnKarmaChanging(killer, delta);
            if (adjusted == null) return;
            delta = adjusted.Value;
        }
        killer.Karma = (short)Math.Clamp(killer.Karma + delta, MinKarma, MaxKarma);
    }

    /// <summary>Source-X Calc_KarmaScale: good chars lose karma 2x faster, gain 0.5x.</summary>
    private static int ScaleKarma(short currentKarma, int change)
    {
        if (currentKarma > 0)
        {
            if (change < 0) return change * 2;        // losing karma: double penalty
            if (change > 0 && change < currentKarma / 64) return 0; // diminishing returns
            return change / 2;                         // gaining karma: halved
        }
        return change;
    }

    /// <summary>Create a corpse item at the victim's position.</summary>
    private Item CreateCorpse(Character victim, bool wasSleeping = false)
    {
        var corpse = _world.CreateItem();
        corpse.BaseId = 0x2006; // ITEMID_CORPSE
        corpse.Amount = victim.BodyId; // body type for corpse display
        string victimName = victim.GetDisplayName();
        corpse.Name = ServerMessages.GetFormatted(Msg.CorpseName, "corpse", victimName);
        corpse.SetTag("CORPSE_NAME", victimName);
        corpse.ItemType = ItemType.Corpse;
        corpse.Hue = victim.Hue;
        corpse.Direction = (byte)((byte)victim.Direction & 0x07); // facing snapshot for carve/forensics
        corpse.SetAttr(ObjAttributes.Move_Never); // a corpse can't be dragged, only looted (Source-X)

        // Forensics reads DEATH_TIME / CORPSE_CARVED / CORPSE_SLEEPING. Stamp the
        // death time so the skill can report how long ago the death occurred; the
        // carved/sleeping flags default to unset and are set when carved/sleeping.
        corpse.SetTag("DEATH_TIME", Environment.TickCount64.ToString());
        if (wasSleeping || victim.IsStatFlag(StatFlag.Sleeping))
            corpse.SetTag("CORPSE_SLEEPING", "1");

        // Source-X MakeCorpse: corpses of bonded pets, summoned creatures and
        // sleeping bodies are born uncarvable (m_itCorpse.m_carved = 1).
        if (victim.IsBonded || victim.IsSummoned || wasSleeping ||
            victim.IsStatFlag(StatFlag.Sleeping))
            corpse.SetTag("CORPSE_CARVED", "1");

        _world.PlaceItem(corpse, victim.Position);
        return corpse;
    }

    /// <summary>
    /// Settle whatever the character is holding on the cursor back into the pack
    /// before the corpse is filled, and cancel the client-side drag.
    /// </summary>
    private void ReleaseDraggedItem(Character victim)
    {
        if (!victim.TryGetTag("DRAGGING", out string? raw) ||
            !uint.TryParse(raw, out uint uid) || uid == 0)
            return;

        // Preferred path: the client bridge also cancels the drag cursor (0x27).
        Character.OnDragRelease?.Invoke(victim, false);
        if (!victim.TryGetTag("DRAGGING", out _))
            return;

        // No client attached (an NPC, a replay, a headless run): make the same move
        // here so the item still reaches the normal death flow.
        victim.RemoveTag("DRAGGING");

        var item = _world.FindItem(new Serial(uid));
        if (item == null || item.IsDeleted)
            return;

        var pack = victim.Backpack;
        if (pack != null && pack.TryAddItem(item))
            return;

        // Nowhere to put it: the ground at the victim's feet is the defined
        // fallback, the same one the drag-release bridge uses.
        item.ContainedIn = Serial.Invalid;
        _world.PlaceItemWithDecay(item, victim.Position);
    }

    /// <summary>Drop player equipment and backpack to corpse.</summary>
    private void DropLootToCorpse(Character victim, Item corpse)
    {
        // Unequip all items (except hair, beard, etc.)
        Layer[] dropLayers = [
            Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt,
            Layer.Helm, Layer.Gloves, Layer.Ring, Layer.Talisman, Layer.Neck,
            Layer.Waist, Layer.Chest, Layer.Bracelet, Layer.Tunic, Layer.Earrings,
            Layer.Arms, Layer.Cape, Layer.Robe, Layer.Skirt, Layer.Legs
        ];

        foreach (var layer in dropLayers)
        {
            var item = victim.Unequip(layer);
            if (item != null)
            {
                if (StaysWithOwnerOnDeath(item))
                {
                    victim.Equip(item, layer);
                    continue;
                }

                item.SetTag("EQUIPLAYER", ((byte)layer).ToString());
                AddToCorpseOrGround(corpse, item);
            }
        }

        var pack = victim.Backpack;
        if (pack != null)
        {
            var contents = new List<Item>(pack.Contents);
            foreach (var item in contents)
            {
                if (StaysInPackOnDeath(item))
                    continue;

                pack.RemoveItem(item);
                AddToCorpseOrGround(corpse, item);
            }
        }

        // Source-X UnEquipAllItems: hair and beard are COPIED onto the corpse
        // (CreateDupeItem) so it renders with them; the originals stay on the
        // ghost and the dupes are discarded on corpse rejoin (RaiseCorpse
        // skips IT_HAIR/IT_BEARD).
        foreach (var hairLayer in new[] { Layer.Hair, Layer.FacialHair })
        {
            var hair = victim.GetEquippedItem(hairLayer);
            if (hair == null) continue;
            var dupe = _world.CreateItem();
            dupe.BaseId = hair.BaseId;
            dupe.Hue = hair.Hue;
            dupe.ItemType = hair.ItemType;
            dupe.Name = hair.GetName();
            dupe.SetTag("EQUIPLAYER", ((byte)hairLayer).ToString());
            dupe.SetTag("CORPSE_HAIR", "1"); // a render copy, never loot/restore
            AddToCorpseOrGround(corpse, dupe);
        }
    }

    /// <summary>Items that are NOT transferred to the corpse on death and remain
    /// with the owner (re-equipped or kept in the pack). Source-X MakeCorpse keeps
    /// blessed/newbie/move-never/no-trade items (plus the shard's insured/quest
    /// equivalents). SphereNet maps: Blessed/Blessed2/Newbie/Nodropt (no-drop),
    /// Move_Never (cannot be moved by players, so must not land in a lootable
    /// corpse), NotRading (no-trade) and Cursed2 (stays-with-owner cursed).</summary>
    private static bool StaysWithOwnerOnDeath(Item item) =>
        item.IsAttr(ObjAttributes.Blessed) || item.IsAttr(ObjAttributes.Blessed2) ||
        item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Nodropt) ||
        item.IsAttr(ObjAttributes.Move_Never) || item.IsAttr(ObjAttributes.NotRading) ||
        item.IsAttr(ObjAttributes.Cursed2);

    /// <summary>Items that stay in the pack rather than moving to the corpse.
    ///
    /// Source-X uses a NARROWER set here than for equipment: CContainer::ContentsTransfer
    /// keeps only ATTR_NEWBIE / ATTR_MOVE_NEVER / ATTR_CURSED2 / ATTR_BLESSED2
    /// (CContainer.cpp:528), while UnEquipAllItems additionally keeps ATTR_BLESSED,
    /// ATTR_INSURED, ATTR_NODROP, ATTR_NOTRADE and ATTR_QUESTITEM. Plain "blessed"
    /// therefore protects an item you are WEARING, not one loose in your pack.
    ///
    /// Note both engines transfer the pack one level deep: a protected item inside a
    /// plain bag travels with the bag in Source-X too. That is parity, not an
    /// oversight — the protection is a property of what you carry directly.</summary>
    private static bool StaysInPackOnDeath(Item item) =>
        item.IsAttr(ObjAttributes.Newbie) || item.IsAttr(ObjAttributes.Move_Never) ||
        item.IsAttr(ObjAttributes.Cursed2) || item.IsAttr(ObjAttributes.Blessed2);

    /// <summary>Drop NPC loot to corpse (all inventory + level-based loot).</summary>
    private void DropNpcLootToCorpse(Character victim, Item corpse)
    {
        DropLootToCorpse(victim, corpse);

        // Roll the NPC's deferred plain loot (chardef ITEM= entries that
        // were intentionally not materialised at spawn) straight into the
        // corpse — Source-X CTRIG_CreateLoot parity, and the reason living
        // NPCs carry no transient loot in the world save.
        victim.MaterializeDeathLoot(corpse);

        // Source-X drops ONLY what the chardef/loot scripts define (ITEM= /
        // @CreateLoot). A stat-tier gold/reagent/gem generator used to run on
        // top of the script loot here — every kill minted extra gold and
        // materials the pack never granted, inflating the economy.
    }

    /// <summary>
    /// Source-X "Resurrect with Corpse" — when a character is resurrected
    /// while standing on (or owning) their own corpse, automatically
    /// re-equip every item that was equipped at death (using the
    /// EQUIPLAYER tag DropLootToCorpse stamped on it) and dump the rest
    /// back into the backpack. The corpse is deleted once empty.
    ///
    /// Returns true iff a matching corpse was found and processed (the
    /// caller can then skip the "you are still naked" path). If the
    /// resurrected character has no backpack yet (rare — fresh char),
    /// remaining items fall to the ground at the corpse position so
    /// they aren't lost.
    ///
    /// Edge cases handled:
    ///   * The corpse may have decayed/been looted between death and
    ///     resurrect — search returns no match, return false.
    ///   * An equip slot may already be occupied (e.g. NPC healer
    ///     handed the player a robe) — fall back to the backpack.
    ///   * The character may be standing one tile off — we sweep the
    ///     character's tile only (matches Source-X CChar::ResurrectFromCorpse
    ///     which reads <c>g_World.GetItemsAt(GetTopPoint())</c>).
    /// </summary>
    public bool RestoreFromCorpse(Character resurrected)
    {
        Item? corpse = null;
        foreach (var item in _world.GetItemsInRange(resurrected.Position, 2))
        {
            if (item.ItemType != ItemType.Corpse) continue;

            // Source-X FindMyCorpse gates: the corpse must be top-level (not in a
            // container), not flagged NOREJOIN (e.g. a decayed bones pile the owner
            // can no longer rejoin), in line of sight, and owned by this character.
            if (item.ContainedIn.IsValid) continue;
            if (item.TryGetTag("NOREJOIN", out _)) continue;
            if (!_world.CanSeeLOS(resurrected.Position, item.Position)) continue;

            bool owned =
                (item.TryGetTag("OWNER_UUID", out string? uuidStr) &&
                 Guid.TryParse(uuidStr, out Guid uuid) && uuid == resurrected.Uuid) ||
                (item.TryGetTag("OWNER_UID", out string? ownerStr) &&
                 uint.TryParse(ownerStr, out uint ownerUid) && ownerUid == resurrected.Uid.Value);
            if (!owned) continue;

            corpse = item;
            break;
        }
        if (corpse == null) return false;

        // Snapshot first — RemoveItem mutates the underlying list and
        // would otherwise invalidate the iterator after the first call.
        var contents = new List<Item>(corpse.Contents);
        var pack = resurrected.Backpack;

        foreach (var item in contents)
        {
            corpse.RemoveItem(item);

            // The corpse-render hair/beard dupes are not real loot — the
            // originals never left the ghost (Source-X RaiseCorpse skips
            // IT_HAIR/IT_BEARD on rejoin).
            if (item.TryGetTag("CORPSE_HAIR", out _))
            {
                _world.RemoveItem(item);
                continue;
            }

            Layer? targetLayer = null;
            if (item.TryGetTag("EQUIPLAYER", out string? layerStr) &&
                byte.TryParse(layerStr, out byte layerByte))
            {
                targetLayer = (Layer)layerByte;
                item.RemoveTag("EQUIPLAYER");
            }

            bool placed = false;
            if (targetLayer.HasValue && targetLayer.Value != Layer.None)
            {
                // Re-equip on the original layer if free. Equip()
                // returns false on out-of-range; double-equipping the
                // same layer is handled internally by unequipping the
                // previous item, but we keep the slot-occupied check
                // explicit so that unexpected new gear (e.g. a healer
                // robe) isn't silently dropped on the ground.
                if (resurrected.GetEquippedItem(targetLayer.Value) == null &&
                    resurrected.Equip(item, targetLayer.Value))
                {
                    placed = true;
                }
            }

            if (!placed)
            {
                if (pack != null && pack.TryAddItem(item))
                {
                    placed = true;
                }
                else
                {
                    _world.PlaceItem(item, resurrected.Position);
                    placed = true;
                }
            }
        }

        // Drop any remaining tags so a half-looted corpse doesn't keep
        // the killer/owner metadata alive on the recycled UID.
        corpse.RemoveTag("OWNER_UID");
        corpse.RemoveTag("OWNER_UUID");
        corpse.RemoveTag("KILLER_UID");
        corpse.RemoveTag("KILLER_UUID");

        _world.DeleteObject(corpse);
        return true;
    }

    // === Death shroud / resurrection robe (Source-X CChar::Death / Spell_Resurrection) ===

    /// <summary>ITEMID_DEATHSHROUD — the grey robe a ghost wears.</summary>
    private const ushort DeathShroudId = 0x204E;

    /// <summary>ITEMID_ROBE — plain robe handed out on resurrection when no body
    /// covering was restored, so the player isn't resurrected naked.</summary>
    private const ushort ResurrectRobeId = 0x1F03;

    /// <summary>
    /// When true, a death shroud is equipped on the ghost at death and a
    /// resurrection robe is granted on resurrection when no robe was restored.
    /// Source-X gates the same behaviour behind a server flag.
    /// </summary>
    public static bool EnableDeathShroud { get; set; } = true;

    /// <summary>
    /// Equip a death shroud on the dying player's Robe layer. The real robe (if
    /// any) has already dropped to the corpse by the time the ghost transition
    /// runs, so the layer is free. The shroud is Move_Never (can't be dragged off
    /// the ghost) and Newbie (never drops), tagged DEATHSHROUD so resurrection can
    /// remove it. NPCs and already-robed bodies are skipped. Returns the shroud,
    /// or null when none was equipped. Maps to CChar::Death's death-shroud equip.
    /// </summary>
    public Item? EquipDeathShroud(Character victim)
    {
        if (!EnableDeathShroud) return null;
        if (!victim.IsPlayer) return null;
        if (victim.GetEquippedItem(Layer.Robe) != null) return null;

        var shroud = _world.CreateItem();
        shroud.BaseId = DeathShroudId;
        shroud.ItemType = ItemType.Clothing;
        shroud.Name = "death shroud";
        shroud.Hue = Color.Default;
        shroud.SetAttr(ObjAttributes.Move_Never); // looters can't strip the ghost
        shroud.SetAttr(ObjAttributes.Newbie);     // stays with the owner, never drops
        shroud.SetTag("DEATHSHROUD", "1");
        victim.Equip(shroud, Layer.Robe);
        return shroud;
    }

    /// <summary>
    /// Remove the death shroud (if present) from a character's Robe layer. Called
    /// at the start of resurrection so a robe restored from the corpse — or a
    /// resurrection robe — can take the Robe slot. DeleteObject unequips and
    /// unlinks in one step.
    /// </summary>
    public void RemoveDeathShroud(Character ch)
    {
        var robe = ch.GetEquippedItem(Layer.Robe);
        if (robe == null || !robe.TryGetTag("DEATHSHROUD", out _)) return;
        _world.DeleteObject(robe);
    }

    /// <summary>
    /// Grant a plain resurrection robe when the Robe layer is empty after corpse
    /// restore, so the player isn't resurrected naked (Source-X Spell_Resurrection
    /// hands out a robe when the corpse held no body covering). Returns the robe,
    /// or null when one already covers the Robe layer.
    /// </summary>
    public Item? EnsureResurrectionRobe(Character ch)
    {
        if (!EnableDeathShroud) return null;
        if (!ch.IsPlayer) return null;
        if (ch.GetEquippedItem(Layer.Robe) != null) return null;

        var robe = _world.CreateItem();
        robe.BaseId = ResurrectRobeId;
        robe.ItemType = ItemType.Clothing;
        robe.Name = "robe";
        robe.Hue = Color.Default;
        robe.SetAttr(ObjAttributes.Newbie); // res robe stays with the owner
        robe.SetTag("RESURRECTROBE", "1");
        ch.Equip(robe, Layer.Robe);
        return robe;
    }

    /// <summary>The human death cry. Source-X / ServUO Mobile.GetDeathSound for a
    /// human body returns a random gender-specific sound — female 0x314..0x317
    /// (Random(0x314, 4)), male 0x423..0x427 (Random(0x423, 5)). A single fixed
    /// sound for every player was a parity gap.</summary>
    public static int GetHumanDeathSound(bool female, Random rng) =>
        female ? 0x314 + rng.Next(4) : 0x423 + rng.Next(5);

    /// <summary>
    /// Check if looting a corpse is a criminal act.
    /// Maps to CChar::CheckCorpseCrime in Source-X.
    /// </summary>
    public bool IsLootingCriminal(Character looter, Item corpse)
    {
        if (!LootingIsACrime) return false;
        if (looter.PrivLevel >= PrivLevel.GM) return false;

        // Own corpse is not criminal — UUID check first, then Serial fallback
        if (corpse.TryGetTag("OWNER_UUID", out string? ownerUuidStr) &&
            Guid.TryParse(ownerUuidStr, out Guid ownerUuid) &&
            ownerUuid == looter.Uuid)
            return false;

        if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid) &&
            ownerUid == looter.Uid.Value)
            return false;

        // Resolve the still-living owner. Source-X CheckCorpseCrime keys off the
        // corpse's owner-ghost link: if the owner no longer exists, looting is never
        // a crime. A normal NPC is deleted on death (its corpse has no living
        // owner), so monster corpses are free to loot; only a corpse whose owner is
        // a still-present, innocent player makes looting criminal.
        if (!corpse.TryGetTag("OWNER_UID", out string? ownerUidStr) ||
            !uint.TryParse(ownerUidStr, out uint ownerUid2))
            return false;

        var ownerSerial = new Serial(ownerUid2);
        var owner = _world.FindChar(ownerSerial);
        if (owner == null || owner.IsDeleted) return false; // owner gone (NPC corpse)
        if (!owner.IsPlayer) return false;                  // creature corpse — free loot
        if (owner.IsCriminal || owner.IsMurderer) return false; // looting a red/criminal is allowed

        // Looting a party member's corpse is not criminal when that member granted
        // loot rights. The flag belongs to the CORPSE OWNER ("party may loot me"), not
        // the looter — checking the looter's own flag was inverted (a looter who
        // enabled their own flag could freely loot every party member).
        if (PartyManager != null)
        {
            var party = PartyManager.FindParty(looter.Uid);
            if (party != null && party.IsMember(ownerSerial) && party.GetLootFlag(ownerSerial))
                return false;
        }

        // Guild member is not criminal (same guild = shared loot rights)
        var guildMgr = Character.ResolveGuildManager?.Invoke(looter.Uid);
        if (guildMgr != null)
        {
            var looterGuild = guildMgr.FindGuildFor(looter.Uid);
            var ownerGuild = guildMgr.FindGuildFor(ownerSerial);
            if (looterGuild != null && ownerGuild != null && looterGuild == ownerGuild)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Carve a corpse (for hides, meat, etc.).
    /// Maps to @CarveCorpse trigger in Source-X.
    /// </summary>
    public List<Item> CarveCorpse(Character carver, Item corpse)
    {
        var results = new List<Item>();
        if (corpse.ItemType != ItemType.Corpse) return results;
        // Once per corpse (Source-X m_carved). Forensics reads CORPSE_CARVED, so
        // use that tag name here too (the old "CARVED" tag was never read back).
        if (corpse.TryGetTag("CORPSE_CARVED", out _) || corpse.TryGetTag("CARVED", out _))
            return results;

        // Source-X CheckCorpseCrime(fLooting=false): carving an innocent
        // player's corpse is as criminal as looting it.
        if (IsLootingCriminal(carver, corpse))
            carver.MakeCriminal();

        if (TriggerDispatcher?.FireItemTrigger(corpse, ItemTrigger.CarveCorpse, new TriggerArgs
        {
            CharSrc = carver,
            ItemSrc = corpse
        }) == TriggerResult.True)
            return results;

        // Reference Use_CarveCorpse: the parts come from the victim chardef's
        // RESOURCES list. Player-corpse parts are renamed "<part> of <victim>"
        // and fall to the ground with a decay timer; creature parts go into
        // the corpse container.
        Character? owner = null;
        if (corpse.TryGetTag("OWNER_UID", out string? ownerUidStr2) && uint.TryParse(ownerUidStr2, out uint carveOwnerUid))
            owner = _world.FindChar(new Serial(carveOwnerUid));
        var charDef = owner != null
            ? Definitions.DefinitionLoader.GetCharDef(owner.CharDefIndex)
            : Definitions.DefinitionLoader.GetCharDef(corpse.Amount);
        bool playerCorpse = owner?.IsPlayer ?? false;
        string victimName = corpse.TryGetTag("CORPSE_NAME", out string? vn) && !string.IsNullOrWhiteSpace(vn)
            ? vn!
            : owner?.GetDisplayName() ?? "";

        var resources = Definitions.DefinitionLoader.StaticResources;
        if (charDef != null && resources != null && charDef.CarveResources.Count > 0)
        {
            foreach (var (rid, amount, defName) in charDef.CarveResources)
            {
                int defIndex = Definitions.TemplateEngine.ResolveItemDefIndex(resources, defName);
                if (defIndex == 0) continue;
                ushort dispId = Definitions.TemplateEngine.ResolveDispId(resources, defName);
                if (dispId == 0) continue;

                var part = _world.CreateItem();
                part.BaseId = dispId;
                var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
                Definitions.ItemDefHelper.ApplyInstanceMetadata(part, defIndex,
                    setDisplayId: false, setName: false);
                if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                    part.Name = idef.Name;
                if (amount > 1)
                    part.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

                if (playerCorpse)
                {
                    if (!string.IsNullOrEmpty(victimName))
                        part.Name = ServerMessages.GetFormatted(Msg.CorpseName, part.GetName(), victimName);
                    _world.PlaceItemWithDecay(part, corpse.Position);
                }
                else
                {
                    AddToCorpseOrGround(corpse, part);
                }
                results.Add(part);
            }
        }

        if (results.Count == 0)
        {
            // Def carries no RESOURCES — keep the legacy random rolls so plain
            // creatures still yield something to the carver.
            if (Random.Shared.Next(100) < 70)
            {
                var hides = _world.CreateItem();
                hides.BaseId = 0x1079; // hides
                hides.Name = "hides";
                hides.Amount = (ushort)Random.Shared.Next(1, 4);
                AddToPackOrGround(carver, hides);
                results.Add(hides);
            }

            var meat = _world.CreateItem();
            meat.BaseId = 0x09F1; // raw ribs
            meat.Name = "raw ribs";
            meat.Amount = (ushort)Random.Shared.Next(1, 3);
            AddToPackOrGround(carver, meat);
            results.Add(meat);

            if (Random.Shared.Next(100) < 20)
            {
                var bones = _world.CreateItem();
                bones.BaseId = 0x0ECA; // bone pile
                bones.Name = "bones";
                bones.Amount = 1;
                AddToPackOrGround(carver, bones);
                results.Add(bones);
            }
        }

        corpse.SetTag("CORPSE_CARVED", "1");
        return results;
    }

    private void AddToPackOrGround(Character ch, Item item)
    {
        var pack = ch.Backpack;
        if (pack == null || !pack.TryAddItem(item))
            _world.PlaceItem(item, ch.Position);
    }

    private void AddToCorpseOrGround(Item corpse, Item item)
    {
        if (!corpse.TryAddItem(item))
            _world.PlaceItemWithDecay(item, corpse.Position);
    }

    // Corpse decay is now driven by the per-item Item.DecayTime /
    // Item.OnTick path (see Program.cs wiring of Item.OnCorpseDecay).
    // The full-world scan that used to live here burned ~100 ms per
    // tick on busy worlds and fought the sector-sleep design.
}

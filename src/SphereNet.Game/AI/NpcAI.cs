using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

/// <summary>
/// NPC AI flags. Maps to NPC_AI_* defines in Source-X CServerConfig.h.
/// </summary>
[Flags]
public enum NpcAIFlags : uint
{
    None = 0,
    Path = 0x0001,
    Food = 0x0002,
    Extra = 0x0004,
    AlwaysInt = 0x0008,
    IntFood = 0x0010,
    // 0x0020 is unused in Source-X (gap between INTFOOD and COMBAT) —
    // bit values below are aligned to the NPC_AI_* defines so a raw
    // script NPC.AI integer maps to the correct behaviours.
    Combat = 0x0040,
    VendTime = 0x0080,
    Looting = 0x0100,
    MoveObstacles = 0x0200,
    PersistentPath = 0x0400,
    Threat = 0x0800,
}

/// <summary>Source-X CRESND_TYPE — creature sound categories.</summary>
public enum CreatureSoundType : byte
{
    Idle = 0,
    Notice = 1,
    Hit = 2,
    GetHit = 3,
    Die = 4,
}

/// <summary>
/// NPC AI engine. Maps to CChar::NPC_* functions in Source-X CCharNPCAct.cpp.
/// Handles brain-based decision making and action execution per tick.
/// </summary>
public sealed partial class NpcAI
{
    public NpcAIFlags Flags { get; set; } =
        NpcAIFlags.Path | NpcAIFlags.Combat | NpcAIFlags.Threat | NpcAIFlags.PersistentPath;

    public Action<Character>? OnNpcFacingChanged { get; set; }

    public enum NpcDecisionType
    {
        None = 0,
        Move = 1,
        Legacy = 2
    }

    /// <summary>Per-NPC decision computed in the parallel build phase. The
    /// prestage fields carry read-only A* work done off the serial path (N2):
    /// <see cref="PrestagedPath"/> is a route toward <see cref="PrestageGoal"/>
    /// (null with <see cref="PrestageRan"/> set = the search ran and FAILED, so
    /// the serial side can take the fail-backoff without re-searching). The
    /// serial apply seeds these into the path cache only when its own state
    /// still calls for a recompute — serial remains the source of truth.</summary>
    public readonly record struct NpcDecision(
        uint NpcUid,
        NpcDecisionType Type,
        Point3D TargetPos,
        Direction Direction,
        long NextActionTick,
        List<Point3D>? PrestagedPath = null,
        Point3D PrestageGoal = default,
        bool PrestageRan = false,
        bool PrestageDeferred = false);

    private readonly GameWorld _world;

    private readonly Pathfinder _pathfinder;

    private readonly SphereConfig _config;

    private static Random _rand => Random.Shared;

    // Last fight target each NPC announced via OnNpcAttackNotify. Mirrors the
    // player path's per-session notify latch: the "*X is attacking Y!*" emote
    // fires once per target, again only when the NPC switches targets.
    private readonly Dictionary<uint, uint> _lastAttackNotify = [];

    /// <summary>How long a target-less NPC waits before re-running the full
    /// acquire scan (ModernUO ReacquireDelay). Reset to 0 on being attacked.</summary>
    private const long ReacquireDelayMs = 1500;

    /// <summary>@NPCActFight hook. Source-X NPC_Act_Fight fires it with ARGN1=dist,
    /// ARGN2=motivation, ARGO=target. RETURN 1 fully handles the action; otherwise
    /// the script may override motivation (ARGN2 readback) and force a cast via
    /// LOCAL.skill + LOCAL.spell. Args: (npc, target, dist, motivation).</summary>
    public Func<Character, Character, int, int, NpcFightDecision>? OnNpcActFight { get; set; }

    public Func<Character, Character, bool>? OnNpcLookAtChar { get; set; }

    /// <summary>Resolved @NPCActFight decision. <see cref="Handled"/> = RETURN 1
    /// (skip the engine's fight logic this tick). Otherwise <see cref="Motivation"/>
    /// is the (possibly script-mutated) motivation, <see cref="ForcedSpell"/> /
    /// <see cref="ForcedSkill"/> carry a script-forced cast (LOCAL.spell / LOCAL.skill),
    /// and <see cref="SkipHardcoded"/> (LOCAL.skiphardcoded) bypasses the engine's
    /// hardcoded breath/throw specials while keeping its magery/melee.</summary>
    public readonly record struct NpcFightDecision(
        bool Handled, int Motivation, SkillType ForcedSkill, SpellType ForcedSpell,
        bool SkipHardcoded = false);

    public Func<Character, bool>? OnNpcActWander { get; set; }

    /// <summary>What a follow trigger answered, and what it left behind.
    ///
    /// Source-X gives NPC_Act_Follow three outcomes (CCharNPCAct.cpp:1357): RETURN 1
    /// gives up following, RETURN 0 means the script handled this call and the native
    /// movement is skipped, and falling through carries on after reading ARGN1 (flee),
    /// ARGN2 (the distance to keep) and ARGN3 (move away) back. The adapter collapsed
    /// all of that into one bool, so "handled" and "carry on" were the same answer and
    /// a script's distance never reached the engine.</summary>
    public enum FollowTriggerResult
    {
        /// <summary>Carry on with the native follow, honouring <see cref="FollowTriggerArgs"/>.</summary>
        Continue = 0,
        /// <summary>The script dealt with this call; take no native step.</summary>
        Handled = 1,
        /// <summary>Give up following.</summary>
        GiveUp = 2,
    }

    /// <summary>The mutable half of the follow trigger, seeded before it runs and read
    /// back afterwards.</summary>
    public sealed class FollowTriggerArgs
    {
        public bool Flee { get; set; }
        public bool MoveAway { get; set; }

        /// <summary>How close the follower wants to be. Seeded with SphereNet's own
        /// following distance rather than the reference's 1, so a pack that scripts
        /// nothing keeps the behaviour it has today.</summary>
        public int MaxDistance { get; set; }
    }

    public Func<Character, Character, FollowTriggerArgs, FollowTriggerResult>? OnNpcActFollow { get; set; }

    /// <summary>@NPCActCast hook. Source-X NPC_FightMagery fires it per candidate
    /// spell with ARGN1=spell, ARGN2=wand-use, ARGO=target, LOCAL.HealThreshold.
    /// RETURN 1 aborts the cast and reverts to melee; otherwise the (possibly
    /// script-mutated) spell/target are cast. The bool arg is the wand-use flag
    /// (ARGN2 — the documented Source-X contract; upstream's own value is
    /// always 0 due to an early reset quirk). Returns the resolved decision; a
    /// null result (no hook) means "proceed with the given spell/target".</summary>
    public Func<Character, Character, SpellType, bool, NpcCastDecision>? OnNpcActCast { get; set; }

    /// <summary>@NPCLookAtItem hook (Source-X NPC_LookAtItem, CCharNPCAct.cpp:940):
    /// ARGN1 = distance, ARGN2 = want-score (writable), ARGO = the item.
    /// RETURN 1 = the script took the item over; RETURN 0 = ignore the item.
    /// Args: (npc, item, dist, wantScore).</summary>
    public Func<Character, Item, int, int, NpcLookDecision>? OnNpcLookAtItem { get; set; }

    /// <summary>@NPCSeeWantItem, fired immediately before native looting acts
    /// on an item the NPC desires. RETURN 1 cancels the pickup.</summary>
    public Func<Character, Item, bool>? OnNpcSeeWantItem { get; set; }

    /// <summary>Resolved @NPCLookAtItem outcome. <see cref="Handled"/> = RETURN 1
    /// (script owns the item), <see cref="Ignore"/> = RETURN 0 (leave it alone);
    /// otherwise <see cref="Want"/> is the possibly script-adjusted want-score.</summary>
    public readonly record struct NpcLookDecision(bool Handled, bool Ignore, int Want);

    /// <summary>Outcome of the @NPCActCast trigger. <see cref="Abort"/> true =
    /// RETURN 1 (revert to melee, no cast). Otherwise <see cref="Spell"/> /
    /// <see cref="Target"/> carry the spell and target to cast (script may have
    /// overridden them via ARGN1 / REF1).</summary>
    public readonly record struct NpcCastDecision(bool Abort, SpellType Spell, Character? Target);

    public NpcAI(GameWorld world, SphereConfig config)
    {
        _world = world;
        _config = config;
        _pathfinder = new Pathfinder(world);
        // Source-X parity: the global NPC_AI_* mask is configured (NPCAI), not
        // hardcoded. Seed the default flag set from config so every NPC inherits
        // it unless overridden per character via OVERRIDE.NPCAI.
        Flags = (NpcAIFlags)(uint)config.NpcAi;
    }

    /// <summary>Effective NPC_AI_* flags for a single NPC. Source-X
    /// CCharNPC::GetNpcAiFlags: a per-character OVERRIDE.NPCAI key wins over the
    /// global NPCAI config (mirrored here by the global <see cref="Flags"/>).
    /// The override value is a raw NPC_AI_* integer (hex or decimal).</summary>
    internal NpcAIFlags GetNpcFlags(Character npc)
    {
        if (npc.TryGetTag("OVERRIDE.NPCAI", out string? raw) && !string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim();
            uint val;
            bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                       (raw.Length > 1 && raw[0] == '0');
            ReadOnlySpan<char> digits = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? raw.AsSpan(2)
                : raw.AsSpan();
            bool ok = hex
                ? uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out val)
                : uint.TryParse(raw, out val);
            if (ok)
                return (NpcAIFlags)val;
        }
        return Flags;
    }

    /// <summary>
    /// Main NPC tick action. Maps to NPC_OnTickAction in Source-X.
    /// Called every tick for each living, non-frozen NPC.
    /// </summary>
    public void OnTickAction(Character npc)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted || npc.IsStatFlag(StatFlag.Ridden)) return;

        long now = Environment.TickCount64;

        if (npc.IsCasting)
        {
            OnNpcTickSpellCast?.Invoke(npc);
            if (npc.IsCasting)
            {
                npc.NextNpcActionTime = now + 250;
                return;
            }
        }

        if (now < npc.NextNpcActionTime)
            return;

        // Active-area gate: no player in view-range → park the NPC for 30-60s.
        // Pets bypass (they live next to their owner by definition). The long
        // park keeps timer wheel churn low on 100K+ NPC worlds; sector wake
        // in Program.cs reschedules NPCs immediately when a player enters.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = now + 30_000 + _rand.Next(0, 30_000);
            return;
        }

        // NPC tick cadence by role, scaled by CharDef.MoveRate.
        // Source-X: MoveRate default=100 (normal speed). Higher = slower.
        // Base delays: combat 400ms, idle wander 1000ms, service 3-5s.
        bool isActive = npc.FightTarget.IsValid ||
            (npc.NpcMaster.IsValid && npc.PetAIMode is PetAIMode.Attack
                or PetAIMode.Follow or PetAIMode.Come or PetAIMode.Guard);
        bool isService = npc.NpcBrain is NpcBrainType.Vendor or NpcBrainType.Banker
            or NpcBrainType.Stable or NpcBrainType.Healer;

        int moveRate = 100;
        var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
        if (charDef != null && charDef.MoveRate > 0)
            moveRate = charDef.MoveRate;

        // Badly-hurt creatures act a little slower (ModernUO BadlyHurtMoveDelay) —
        // natural "wounded" pacing plus a touch of CPU relief on big fights.
        int hurtDelay = (npc.MaxHits > 0 && npc.Hits > 0 && npc.Hits < npc.MaxHits / 3)
            ? (isActive ? 200 : 400)
            : 0;

        // Source-X NPC move cadence (CCharNPCAct move-tick): the step delay
        // scales with DEX and the creature's MoveRate, NOT a flat rate. Running
        // (combat/chase) is ~250ms at high DEX up to ~1.6s at low DEX; walking
        // (idle wander) is ~1s up to ~2.6s. A flat 400ms made ordinary (mid-DEX)
        // creatures chase noticeably faster than Source-X. Pets run a little
        // faster (DEX floored to 75). Clamped to [100ms, 5s] like Source-X.
        if (isActive)
        {
            int dex = npc.Dex;
            if (npc.NpcMaster.IsValid && dex < 75) dex = 75; // pets run faster
            int range = Math.Max(0, 100 - dex * moveRate / 100) / 5;
            int delay = Math.Clamp(250 + _rand.Next(range + 1) * 100, 100, 5000);
            npc.NextNpcActionTime = now + delay + hurtDelay;
        }
        else if (isService)
            npc.NextNpcActionTime = now + 3000 + _rand.Next(0, 2000);
        else
        {
            int range = Math.Max(0, 100 - npc.Dex * moveRate / 100) / 3;
            int delay = Math.Clamp(1000 + _rand.Next(range + 1) * 100, 100, 5000);
            npc.NextNpcActionTime = now + delay + hurtDelay;
        }

        // Atmospheric special trail — giant spiders web the ground, fire
        // elementals leave fire patches, as they move and fight.
        TryDropSpecialTrail(npc);

        // Pet behavior — owned NPCs follow pet AI mode
        if (npc.NpcMaster.IsValid)
        {
            ActPet(npc);
            if (!npc.IsDead && !npc.IsDeleted && GetNpcFlags(npc).HasFlag(NpcAIFlags.Extra))
                RunExtraAI(npc);
            return;
        }

        // Perceive nearby players for @NPCSeeNewPlayer greetings (free + skipped
        // entirely when no script hooks the trigger).
        LookForNewPlayers(npc);

        // Brain-based behavior
        switch (npc.NpcBrain)
        {
            case NpcBrainType.Guard:
                ActGuard(npc);
                break;
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
                ActMonster(npc);
                break;
            case NpcBrainType.Berserk:
                ActBerserk(npc);
                break;
            case NpcBrainType.Healer:
                ActHealer(npc);
                break;
            case NpcBrainType.Vendor:
            case NpcBrainType.Banker:
            case NpcBrainType.Stable:
                ActVendor(npc);
                break;
            case NpcBrainType.Animal:
                ActAnimal(npc);
                break;
            case NpcBrainType.Human:
            default:
                ActHuman(npc);
                break;
        }

        // Source-X CChar::_OnTick tail (CCharAct.cpp:5959): NPC_AI_EXTRA runs
        // the humanoid extra pass after the brain action.
        if (!npc.IsDead && GetNpcFlags(npc).HasFlag(NpcAIFlags.Extra))
            RunExtraAI(npc);
    }

    /// <summary>@NPCAction hook (fired by the NPC_AI_EXTRA pass; RETURN 1
    /// skips the hardcoded extra behaviors this tick).</summary>
    public Func<Character, bool>? OnNpcAction { get; set; }

    /// <summary>Light level at a position (WeatherEngine.GetLightLevel;
    /// 0 = bright .. 30 = black). Unwired = always day.</summary>
    public Func<Point3D, byte>? GetLightLevel { get; set; }

    /// <summary>An NPC shifted a blocking item out of its way
    /// (NPC_AI_MOVEOBSTACLES) — broadcast the item's new position.</summary>
    public Action<Character, Item>? OnNpcMovedItem { get; set; }

    /// <summary>Light levels at or above this read as "night" for the
    /// NPC_AI_EXTRA light-source behavior.</summary>
    private const byte NightLightLevel = 20;

    /// <summary>Source-X NPC_ExtraAI (CCharNPCAct.cpp:2670) — the NPC_AI_EXTRA
    /// pass for humanoid-brain NPCs: fire @NPCAction (RETURN 1 skips the pass),
    /// then in war mode equip a weapon/shield from the pack; in peace equip and
    /// light a hand light source by night and stow it by day.</summary>
    private void RunExtraAI(Character npc)
    {
        if (!IsHumanoidBrain(npc.NpcBrain))
            return;
        if (OnNpcAction != null && OnNpcAction(npc))
            return;

        var can = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.Can ?? CanFlags.None;
        if ((can & (CanFlags.C_Equip | CanFlags.C_UseHands)) == 0)
            return;

        var pack = npc.Backpack;

        if (npc.IsStatFlag(StatFlag.War))
        {
            var hand1 = npc.GetEquippedItem(Layer.OneHanded);
            var hand2 = npc.GetEquippedItem(Layer.TwoHanded);
            bool armed = (hand1 != null && IsWeaponItemType(hand1.ItemType)) ||
                         (hand2 != null && IsWeaponItemType(hand2.ItemType));
            if (!armed && pack != null)
            {
                var weapon = FindInPack(pack, it => IsWeaponItemType(it.ItemType));
                if (weapon != null)
                {
                    pack.RemoveItem(weapon);
                    bool twoHanded = weapon.IsTwoHanded ||
                        weapon.ItemType is ItemType.WeaponBow or ItemType.WeaponXBow;
                    npc.Equip(weapon, twoHanded ? Layer.TwoHanded : Layer.OneHanded);
                }
            }

            hand2 = npc.GetEquippedItem(Layer.TwoHanded);
            if (hand2 == null && pack != null)
            {
                var shield = FindInPack(pack, it => it.ItemType == ItemType.Shield);
                if (shield != null)
                {
                    pack.RemoveItem(shield);
                    npc.Equip(shield, Layer.TwoHanded);
                }
            }
            return;
        }

        // Peace: carry a light source through the night, stow it by day.
        bool dark = (GetLightLevel?.Invoke(npc.Position) ?? 0) >= NightLightLevel;
        var held = npc.GetEquippedItem(Layer.TwoHanded);
        bool holdingLight = held != null &&
            held.ItemType is ItemType.LightLit or ItemType.LightOut;
        if (dark)
        {
            if (!holdingLight && held == null && pack != null)
            {
                var light = FindInPack(pack,
                    it => it.ItemType is ItemType.LightLit or ItemType.LightOut);
                if (light != null)
                {
                    pack.RemoveItem(light);
                    npc.Equip(light, Layer.TwoHanded);
                    TryLightNpcSource(light);
                }
            }
            else if (held?.ItemType == ItemType.LightOut)
            {
                TryLightNpcSource(held);
            }
        }
        else if (holdingLight && pack != null)
        {
            npc.Unequip(Layer.TwoHanded);
            if (!pack.TryAddItem(held!))
                npc.Equip(held!, Layer.TwoHanded);
        }
    }

    /// <summary>Source-X NPCBRAIN_HUMAN group: the human-like brains the
    /// EXTRA pass applies to.</summary>
    private static bool IsHumanoidBrain(NpcBrainType brain) => brain is
        NpcBrainType.Human or NpcBrainType.Healer or NpcBrainType.Guard or
        NpcBrainType.Banker or NpcBrainType.Vendor or NpcBrainType.Stable;

    private static bool IsWeaponItemType(ItemType type) => type is
        ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponFence or
        ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or ItemType.WeaponMaceStaff or
        ItemType.WeaponMaceCrook or ItemType.WeaponMacePick or ItemType.WeaponWhip or
        ItemType.WeaponBow or ItemType.WeaponXBow or ItemType.WeaponThrowing;

    private static Item? FindInPack(Item pack, Func<Item, bool> match)
    {
        foreach (var it in pack.Contents)
            if (!it.IsDeleted && match(it))
                return it;
        return null;
    }

    private static bool TryLightNpcSource(Item light)
    {
        if (light.ItemType == ItemType.LightLit)
            return true;
        if (light.ItemType != ItemType.LightOut)
            return false;

        int charges = 20;
        if (light.TryGetTag("LIGHT_CHARGES", out string? raw) &&
            int.TryParse(raw, out int parsed))
            charges = parsed;
        if (charges <= 0 || light.TryGetTag("LIGHT_BURNED", out _))
            return false;

        // Charges burn one-per-minute via the lit timer (Item.OnLightBurnTick),
        // not per lighting — Source-X Use_Light semantics.
        light.SetTag("LIGHT_CHARGES", charges.ToString());
        light.ItemType = ItemType.LightLit;
        light.SetTimeout(Environment.TickCount64 + Item.LightBurnTickMs);
        return true;
    }

    /// <summary>
    /// Build a deterministic AI decision without mutating world state.
    /// Returns null when no action should be applied this tick.
    /// </summary>
    public NpcDecision? BuildDecision(Character npc, long nowTick)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted || npc.IsStatFlag(StatFlag.Ridden))
            return null;
        if (nowTick < npc.NextNpcActionTime)
            return null;

        // Active-area gate: no player nearby → park for 30-60s. Returns a None
        // decision so ApplyDecision sets NextNpcActionTime in the sequential phase
        // (no mutation during parallel compute).
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            long parkTime = nowTick + 30_000 + DeterministicJitter(npc.Uid.Value, nowTick, 30_000);
            return new NpcDecision(npc.Uid.Value, NpcDecisionType.None, npc.Position, npc.Direction, parkTime);
        }

        int spread = (int)((npc.Uid.Value * 2654435761u) % 400);
        long nextAction = nowTick + 600 + spread;

        // All brain types route through Legacy → OnTickAction for full Source-X
        // parity. Without this, service brains (Banker, Stable, Human, Animal)
        // only do deterministic wander and lose their ActVendor/ActHuman/ActAnimal logic.
        // Casting NPCs also need Legacy to run OnNpcTickSpellCast.
        //
        // N2: the heavy read-only piece of that serial work — the A* route for a
        // blocked combat/pet chase — is precomputed HERE, in the parallel build
        // phase (Pathfinder scratch state is [ThreadStatic] for exactly this).
        // The serial OnTickAction then finds a warm path cache instead of
        // burning up to ~17ms per NPC inside the single-threaded apply phase.
        var (path, goal, ran, deferred) = TryPrestagePathfind(npc);
        return new NpcDecision(npc.Uid.Value, NpcDecisionType.Legacy, npc.Position, npc.Direction,
            nextAction, path, goal, ran, deferred);
    }

    /// <summary>
    /// Apply a previously computed decision in a single-threaded phase.
    /// </summary>
    public void ApplyDecision(NpcDecision decision)
    {
        var npc = _world.FindChar(new Serial(decision.NpcUid));
        if (npc == null || npc.IsDeleted || npc.IsDead || npc.IsPlayer)
            return;

        switch (decision.Type)
        {
            case NpcDecisionType.Move:
                npc.NextNpcActionTime = decision.NextActionTick;
                npc.Direction = decision.Direction;
                if (CanNpcMoveTo(npc, decision.TargetPos))
                    _world.MoveCharacter(npc, decision.TargetPos);
                break;
            case NpcDecisionType.Legacy:
                // Let OnTickAction own the cadence update; setting NextNpcActionTime
                // before the legacy call would make the combat brain return early.
                if (decision.PrestageRan || decision.PrestageDeferred)
                    SeedPrestagedPath(npc, decision);
                OnTickAction(npc);
                break;
            default:
                npc.NextNpcActionTime = decision.NextActionTick;
                break;
        }
    }

    public Action<Character, string>? OnNpcSay { get; set; }

    public Action<Character>? OnGuardLightningStrike { get; set; }

    public Action<Character>? OnNpcTeleport { get; set; }

    /// <summary>Callback: wake an NPC for immediate action (e.g. retaliation).
    /// Program.cs reschedules the NPC in the timer wheel so it acts next tick.</summary>
    public Action<Character>? OnWakeNpc { get; set; }
}

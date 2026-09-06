using System.Reflection;
using Xunit.Sdk;

[assembly: SphereNet.Tests.ResetEngineStatics]

namespace SphereNet.Tests;

// The engine exposes process-wide mutable static hooks that tests wire in their
// setup (CreateWorld / AttachCharacter):
//   ObjBase.ResolveWorld, Item.ResolveWorld   — resolve the ambient world
//   Character.ResolveAccountForChar           — drives Character.PrivLevel
//   VendorEngine.World                         — vendor trade world
// If one test leaves a hook set, a later test (whose object UIDs can collide
// across its own fresh world) resolves against the stale hook — e.g. a player
// reading another test's GM account and gaining GM privileges, or a move
// resolving against the wrong world. xUnit does not guarantee test order, so
// this surfaced as intermittent, order-dependent CI failures.
//
// Reset every hook to null before AND after each test so each test starts from
// a clean slate and re-establishes only what its own setup wires. Combined with
// assembly-wide serialization (see TestParallelization.cs) this makes the suite
// deterministic regardless of execution order.
public sealed class ResetEngineStaticsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset();
    public override void After(MethodInfo methodUnderTest) => Reset();

    private static void Reset()
    {
        SphereNet.Game.Objects.ObjBase.ResolveWorld = null;
        SphereNet.Game.Definitions.DefinitionLoader.ResetForTests();
        SphereNet.Game.Objects.ObjBase.BroadcastNearby = null;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = null;
        SphereNet.Game.Objects.Items.Item.CreateTriggerHook = null;
        SphereNet.Game.Components.ChampionComponent.ResolveGameClockMs = null;
        SphereNet.Game.Components.SpawnComponent.ReleaseFromPreviousSpawner = null;
        SphereNet.Game.Objects.Characters.Character.ReleaseFromSpawner = null;
        SphereNet.Game.Objects.Items.Item.OnItemUnequipped = null;
        SphereNet.Game.Objects.Items.Item.ItemsMaxAmount = 60000;
        SphereNet.Game.Objects.Characters.Character.BroadcastNearby = null;
        SphereNet.Game.Objects.Characters.Character.FieldTouchHook = null;
        SphereNet.Game.Objects.Characters.Character.MagicFlags = 0;
        SphereNet.Game.Objects.Characters.Character.CombatSpeedEra = 0;
        SphereNet.Game.Objects.Characters.Character.CombatSpeedScaleFactor = 15_000;
        SphereNet.Game.Objects.Characters.Character.CombatParryingEra =
            (int)(SphereNet.Core.Enums.ParryEraFlags.PreSeFormula |
                  SphereNet.Core.Enums.ParryEraFlags.ShieldBlock);
        SphereNet.Game.Objects.Characters.Character.FeatureSE = 0;
        SphereNet.Game.Objects.Characters.Character.FeatureAOS = 0;
        SphereNet.Game.Objects.Characters.Character.RacialFlags = 0;
        SphereNet.Game.Objects.Characters.Character.OnFacingChanged = null;
        SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = null;
        SphereNet.Game.Objects.Characters.Character.ResolvePartyManager = null;
        SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRemover = null;
        SphereNet.Game.Objects.Characters.Character.NpcWantThisItem = null;
        SphereNet.Game.Objects.Characters.Character.NpcCanEatFood = null;
        SphereNet.Game.Trade.VendorEngine.World = null;
        SphereNet.Game.Combat.CombatEngine.OnHitDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnDirectDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnDirectCharacterDamageApplied = null;
        SphereNet.Game.Combat.CombatEngine.OnItemDamaged = null;
        SphereNet.Game.Combat.CombatEngine.OnItemBroken = null;
        SphereNet.Game.Combat.CombatEngine.BreakOnZeroHits = true;
        SphereNet.Game.Combat.CombatEngine.DefaultHits = 50;
        SphereNet.Game.Combat.CombatEngine.OnHitParry = null;
        SphereNet.Game.Objects.Items.Item.RedeedHouse = null;
        SphereNet.Game.Objects.Items.Item.RedeedShip = null;
        SphereNet.Game.Speech.CommandHandler.ServerCommandBridge = null;
        SphereNet.Game.Objects.Characters.Character.SpawnNpcFromScript = null;
        SphereNet.Game.Combat.CombatEngine.OnLeechEffect = null;
        SphereNet.Game.Combat.CombatEngine.OnHitAreaDamage = null;
        SphereNet.Game.Combat.CombatEngine.OnHitSpell = null;
        SphereNet.Scripting.Definitions.CharDef.DefNameResolver = null;
        SphereNet.Game.Objects.Characters.Character.OnFameChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnKarmaChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnExpChanging = null;
        SphereNet.Game.Objects.Characters.Character.OnExpLevelChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderMark = null;
        SphereNet.Game.Objects.Characters.Character.OnCriminalCheck = null;
        SphereNet.Game.Objects.Characters.CrimeWitnessService.OnCrimeNoticed = null;
        SphereNet.Game.Objects.Characters.CrimeWitnessService.SnoopCriminalChance = 100;
        SphereNet.Game.Objects.Characters.Character.OnCombatAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatDelete = null;
        SphereNet.Game.Objects.Characters.Character.OnCombatEnd = null;
        SphereNet.Game.Objects.Characters.Character.OnMurderDecay = null;
        SphereNet.Game.Objects.Characters.Character.OnNotoSend = null;
        SphereNet.Game.Objects.Characters.Character.ResolveNotoFlag = null;
        SphereNet.Game.Objects.Characters.Character.OnHealthBarStatusChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptSpellEffect = null;
        SphereNet.Game.Objects.Characters.Character.OnPersonalSpace = null;
        SphereNet.Game.Objects.Characters.Character.OnEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnRevealing = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectAdd = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectRemove = null;
        SphereNet.Game.Objects.Characters.Character.OnClientBuffChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnOwnViewRefreshNeeded = null;
        SphereNet.Game.Objects.Characters.Character.DeadCannotSeeLiving = 0;
        SphereNet.Game.Objects.Characters.Character.OnHiddenStateCleared = null;
        SphereNet.Game.Objects.Characters.Character.OnSpellEffectTick = null;
        SphereNet.Game.Objects.Characters.Character.OnPetDesert = null;
        SphereNet.Game.Objects.Characters.Character.OnJailed = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptDismount = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptMount = null;
        SphereNet.Game.Objects.Characters.Character.ScriptEquipItem = null;
        SphereNet.Game.Objects.Characters.Character.OnDragRelease = null;
        SphereNet.Game.Objects.Characters.Character.OnDragCancel = null;
        SphereNet.Game.Objects.Items.Item.OnTradeWindowChanged = null;
        SphereNet.Game.Objects.Characters.Character.OnHitIgnored = null;
        SphereNet.Game.Objects.Characters.Character.OnNpcLostTeleport = null;
        SphereNet.Game.Objects.Items.Item.OnTimerExpired = null;
        SphereNet.Game.Objects.Items.Item.ResolveHouse = null;
        SphereNet.Game.Objects.Items.Item.ResolveMultiDefId = null;
        SphereNet.Game.Objects.Items.Item.ResolveGuild = null;
        SphereNet.Game.Objects.Items.Item.ResolveGuildManager = null;
        SphereNet.Game.Objects.Items.Item.ResolveGuildCharacter = null;
        SphereNet.Game.Objects.Characters.Character.OnCanCastCheck = null;
        SphereNet.Game.Objects.Items.Item.OnSpawnStartStop = null;
        SphereNet.Game.Objects.Items.Item.OnScriptOpen = null;
        SphereNet.Game.Objects.Items.Item.OnScriptDClick = null;
        SphereNet.Game.Scripting.ScriptFileHandle.Diagnostic = null;
        SphereNet.Game.Objects.ObjBase.OnScriptTrigger = null;
        SphereNet.Game.Objects.ObjBase.OnScriptSingleClick = null;
        SphereNet.Game.World.Regions.Region.OnAllClients = null;
        SphereNet.Game.World.Regions.Room.OnAllClients = null;
        SphereNet.Game.Movement.WalkCheck.ResolveCustomDesign = null;
        SphereNet.Game.Objects.Characters.Character.OnMemoryEquip = null;
        SphereNet.Game.Objects.Characters.Character.OnEnvironChange = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuick = null;
        SphereNet.Game.Objects.Characters.Character.OnSkillUseQuickDetailed = null;
        SphereNet.Game.Objects.Characters.Character.OnScriptSkillUse = null;
        SphereNet.Game.Objects.Characters.Character.OnDamageActionInterrupt = null;
        SphereNet.Game.Objects.Characters.Character.ActiveSkillAborted = null;
        SphereNet.Game.Objects.Characters.Character.OnNpcSeeNewPlayer = null;
        SphereNet.Game.Objects.Characters.Character.MountedNpcDeletedHook = null;
        SphereNet.Game.Objects.Items.Item.FigurineDeletedHook = null;
        SphereNet.Game.Housing.House.OnRedeed = null;
        SphereNet.Game.Housing.HousingEngine.OnHouseCheck = null;
        SphereNet.Game.AI.NpcAI.PetFollowMaxDistance = 36;
        SphereNet.Game.Objects.Characters.Character.SpellbookRequiredEnabled = true;
        SphereNet.Game.Objects.Characters.Character.PacketDeathAnimationEnabled = true;
        SphereNet.Game.Clients.GameClient.ClientLingerSeconds = 60;
        SphereNet.Game.Clients.GameClient.ServerFeatureT2A = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureLBR = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureAOS = 0x0F;
        SphereNet.Game.Clients.GameClient.ServerFeatureSE = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureML = 0x01;
        SphereNet.Game.Clients.GameClient.ServerFeatureKR = 0;
        SphereNet.Game.Clients.GameClient.ServerFeatureSA = 0x03;
        SphereNet.Game.Clients.GameClient.ServerFeatureTOL = 0x01;
        SphereNet.Game.Clients.GameClient.ServerFeatureExtra = 0;
        SphereNet.Game.Clients.GameClient.ServerMaxCharsPerAccount = 7;
        SphereNet.Game.Clients.GameClient.ServerAutoResDisp = true;
        SphereNet.Game.Clients.GameClient.ServerToolTipMode = 1;
        SphereNet.Game.Clients.GameClient.ServerOptionFlags =
            SphereNet.Core.Enums.OptionFlags.FileCommands | SphereNet.Core.Enums.OptionFlags.Buffs;
        SphereNet.Network.Packets.Outgoing.PacketCharList.AosTooltipsEnabled = true;
        SphereNet.Game.Components.SpawnComponent.OnSpawnTrigger = null;
        SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = null;
        SphereNet.Game.Death.DeathEngine.EnableDeathShroud = true;
        SphereNet.Game.Skills.SkillEngine.OnSkillGainCheck = null;
        SphereNet.Game.Skills.SkillEngine.OnSkillDecrease = null;
        SphereNet.Game.Skills.SkillEngine.OnStatDecrease = null;
        SphereNet.Game.Skills.SkillHandlers.OnCraftSkillUsed = null;
        SphereNet.Game.Skills.SkillHandlers.OnScriptedSkillUse = null;
        SphereNet.Game.Skills.SkillEngine.SkillMaxOverrides.Clear();
        SphereNet.Game.Skills.SkillEngine.SkillSumMaxOverride = 7000;
        SphereNet.Game.Objects.Characters.Character.OnSkillChange = null;
        SphereNet.Game.Skills.SkillEngine.StatAdvCurves =
            [SphereNet.Scripting.Definitions.ValueCurve.Empty,
             SphereNet.Scripting.Definitions.ValueCurve.Empty,
             SphereNet.Scripting.Definitions.ValueCurve.Empty];
        SphereNet.Scripting.Expressions.ExpressionParser.ObsceneChecker = null;
        SphereNet.Core.Configuration.AccountNameValidator.ObsceneChecker = null;
        SphereNet.Game.Magic.SpellDef.RuneWordResolver = null;
        SphereNet.Game.Objects.ObjBase.DiagnosticLog = null;
        SphereNet.Game.Objects.Characters.Character.OnHearRouted = null;
        SphereNet.Game.Objects.Characters.Character.FindCharByClientIndex = null;
        SphereNet.Game.Objects.Characters.Character.FindCharBySocketId = null;
        SphereNet.Game.Housing.HouseDesignValidItems.ClearValidItems();
        SphereNet.Game.World.Sectors.Sector.SleepDelayMs = 10L * 60 * 1000;
        SphereNet.Game.Objects.Characters.Character.RegenHitsSeconds = 40;
        SphereNet.Game.Objects.Characters.Character.RegenManaSeconds = 20;
        SphereNet.Game.Objects.Characters.Character.RegenStamSeconds = 10;
        SphereNet.Game.Objects.Characters.Character.RegenFoodSeconds = 3600;
    }
}

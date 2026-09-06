using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a summon costs its owner in follower slots, and how its remaining life
/// survives a restart.
///
/// Source-X builds the summon from the CHOSEN creature id before it weighs the
/// cost - CreateBasic(m_atMagery.m_uiSummonID) at CCharSpell.cpp:2640, then
/// GetFollowerSlots() at :2662 - and refuses by deleting the creature and
/// returning null. Spell_CastDone calls that at :3002 and only reaches the
/// consumption at :3010, deleting the summon again if the cost cannot be met
/// (:3012). SphereNet created a placeholder, measured THAT against the cap, then
/// applied the pick afterwards, so a five-slot creature passed a one-slot
/// allowance - and it did so in the effect stage, after the mana, reagents and
/// scroll had already been taken.
///
/// The expiry is the other half. A summon's deadline lived as an absolute
/// TickCount64, which is uptime rather than wall time, and went into the save
/// verbatim; on a machine that had rebooted the summon then waited out a
/// threshold days away. Source-X saves a timer as the REMAINING milliseconds
/// (CObjBase.cpp:2081) and re-arms it against the load time (:2037).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SummonParity04CTests : IDisposable
{
    private const int ManaCost = 4;
    private const int DurationTenths = 600;     // 60 seconds

    // A spellbook and reagents are not what this round is about; the probe behind the
    // report switched them off too. Restored so no other test inherits the setting.
    private readonly bool _savedReagents = Character.ReagentsRequiredEnabled;
    private readonly bool _savedSpellbook = Character.SpellbookRequiredEnabled;

    public void Dispose()
    {
        Character.ReagentsRequiredEnabled = _savedReagents;
        Character.SpellbookRequiredEnabled = _savedSpellbook;
    }

    private static ResourceHolder LoadScript(string contents)
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_04c_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(byte maxFollower)
    {
        // Set here rather than in the constructor: the assembly-wide
        // ResetEngineStatics hook runs between construction and the test body and
        // puts SpellbookRequiredEnabled back to its default.
        Character.ReagentsRequiredEnabled = false;
        Character.SpellbookRequiredEnabled = false;

        var resources = LoadScript("""
            [CHARDEF 0d]
            DEFNAME=c_review_heavy
            NAME=Heavy Summon
            FOLLOWERSLOTS=5

            [CHARDEF 0e]
            DEFNAME=c_review_pair
            NAME=Paired Summon
            FOLLOWERSLOTS=2
            """);

        var registry = new SpellRegistry();
        // AFTER LoadAll: the loader rebuilds the registry from the script pack, so a
        // def registered before it would be dropped.
        new DefinitionLoader(resources, registry).LoadAll();
        registry.Register(new SpellDef
        {
            Id = SpellType.SummonCreature,
            Name = "Summon Creature",
            Flags = SpellFlag.TargXYZ | SpellFlag.Summon,
            ManaCost = ManaCost,
            // Both ends of the curve, or GetDuration interpolates DOWN towards an
            // unset scale and a high-skill caster gets a negative duration.
            DurationBase = DurationTenths,
            DurationScale = DurationTenths,
        });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxFollower = maxFollower;
        caster.SetSkill(SkillType.Magery, 1200);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private static bool Summon(SpellEngine engine, Character caster, string? pick)
    {
        if (pick != null) caster.SetTag("SUMMON_SELECT", pick);
        Assert.True(engine.CastStart(caster, SpellType.SummonCreature, caster.Uid,
            caster.Position) >= 0);
        return engine.CastDone(caster);
    }

    private static IReadOnlyList<Character> Summons(GameWorld world) =>
        world.GetAllObjects().OfType<Character>()
            .Where(c => !c.IsDeleted && c.IsSummoned).ToList();

    // --- SX-04C-01: the cap is weighed against the real creature -------------

    [Fact]
    public void AFiveSlotPickIsRefusedByAOneSlotAllowance()
    {
        var (world, engine, caster) = Setup(maxFollower: 1);

        Assert.False(Summon(engine, caster, "c_review_heavy"));
        Assert.Empty(Summons(world));
        Assert.Equal(0, caster.CurFollower);
    }

    [Fact]
    public void AFiveSlotPickFitsAFiveSlotAllowance()
    {
        // The control: the same creature, an allowance that can hold it.
        var (world, engine, caster) = Setup(maxFollower: 5);

        Assert.True(Summon(engine, caster, "c_review_heavy"));
        var summons = Summons(world);
        Assert.Single(summons);
        Assert.Equal(5, summons[0].ControlSlots);
        Assert.Equal(5, caster.CurFollower);
    }

    [Fact]
    public void APartlyFilledAllowanceStillCountsTheRealCost()
    {
        var (world, engine, caster) = Setup(maxFollower: 5);
        Assert.True(Summon(engine, caster, "c_review_pair"));   // 2 of 5
        Assert.Equal(2, caster.CurFollower);

        // 2 + 5 > 5, so the heavy one may not join.
        Assert.False(Summon(engine, caster, "c_review_heavy"));
        Assert.Single(Summons(world));
        Assert.Equal(2, caster.CurFollower);
    }

    [Fact]
    public void ARefusedSummonLeavesNothingBehind()
    {
        var (world, engine, caster) = Setup(maxFollower: 1);
        Summon(engine, caster, "c_review_heavy");

        Assert.DoesNotContain(world.GetAllObjects().OfType<Character>(),
            c => !c.IsDeleted && c.Uid != caster.Uid);
    }

    [Fact]
    public void ARefusedSummonIsNotChargedTheSuccessCost()
    {
        // Source-X never reaches Spell_CanCast's consumption when Spell_Summon_Try
        // fails (:3002 -> :3010). With the abort loss switched off, nothing is taken.
        bool savedMana = Character.ManaLossAbort;
        try
        {
            Character.ManaLossAbort = false;
            var (_, engine, caster) = Setup(maxFollower: 1);

            Assert.False(Summon(engine, caster, "c_review_heavy"));
            Assert.Equal(100, caster.Mana);
        }
        finally { Character.ManaLossAbort = savedMana; }
    }

    [Fact]
    public void ARefusedSummonStillPaysTheAbortPriceWhenConfigured()
    {
        // The mirror: the failure is priced as an ABORT, not refunded outright.
        bool savedMana = Character.ManaLossAbort;
        try
        {
            Character.ManaLossAbort = true;
            var (_, engine, caster) = Setup(maxFollower: 1);

            Assert.False(Summon(engine, caster, "c_review_heavy"));
            Assert.True(caster.Mana < 100, "the abort price was not charged");
        }
        finally { Character.ManaLossAbort = savedMana; }
    }

    [Fact]
    public void AnUnresolvablePickStillSummonsThePlaceholder()
    {
        // A pick that names nothing leaves the generic creature, which costs the
        // single default slot - the cap must be measured against that, not refused.
        var (world, engine, caster) = Setup(maxFollower: 1);

        Assert.True(Summon(engine, caster, "c_no_such_creature"));
        var summons = Summons(world);
        Assert.Single(summons);
        Assert.Equal(1, summons[0].ControlSlots);
    }

    [Fact]
    public void ASummonWithNoPickIsUnaffected()
    {
        var (world, engine, caster) = Setup(maxFollower: 1);

        Assert.True(Summon(engine, caster, pick: null));
        Assert.Single(Summons(world));
    }

    // --- SX-04C-02: the remaining life survives the restart ------------------

    [Fact]
    public void SaveWritesTheRemainingTimeAndLoadRebasesIt()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_04c_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (world, engine, caster) = Setup(maxFollower: 5);
            Assert.True(Summon(engine, caster, "c_review_heavy"));
            var summon = Summons(world)[0];

            var lf = LoggerFactory.Create(_ => { });
            Assert.True(new WorldSaver(lf).Save(world, tmp));

            // The absolute uptime threshold must not be what reaches the file.
            string text = string.Join("\n", Directory.GetFiles(tmp, "*.scp")
                .Select(File.ReadAllText));
            Assert.Contains("SUMMON_EXPIRE_REMAINING", text);
            Assert.DoesNotContain("SUMMON_EXPIRE_TICK", text);
            string saved = text.Split('\n')
                .First(l => l.Contains("SUMMON_EXPIRE_REMAINING", StringComparison.Ordinal)).Trim();
            // Close to the full minute, and nowhere near an absolute uptime figure.
            Assert.InRange(long.Parse(saved[(saved.IndexOf('=') + 1)..]), 55_000, 60_000);

            var dst = new GameWorld(lf);
            dst.InitMap(0, 6144, 4096);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => dst;
            new WorldLoader(lf).Load(dst, tmp);

            var reloaded = dst.FindChar(summon.Uid);
            Assert.NotNull(reloaded);
            // Roughly a minute of life left, measured on THIS clock.
            Assert.False(reloaded!.TickPetOwnershipTimers(Environment.TickCount64));
            Assert.True(reloaded.TickPetOwnershipTimers(Environment.TickCount64 + 61_000));
        }
        finally { try { Directory.Delete(tmp, recursive: true); } catch { } }
    }

    [Fact]
    public void ARemainingRecordIsRebasedOntoTheRunningClock()
    {
        var (world, _, _) = Setup(maxFollower: 5);
        var summon = world.CreateCharacter();
        summon.SetTag("SUMMON_EXPIRE_REMAINING", "60000");
        summon.SetStatFlag(StatFlag.Conjured);

        summon.RestoreSummonExpiry();

        Assert.False(summon.TryGetTag("SUMMON_EXPIRE_REMAINING", out _));
        Assert.True(summon.TryGetTag("SUMMON_EXPIRE_TICK", out string? tick));
        long deadline = long.Parse(tick!);
        Assert.InRange(deadline, Environment.TickCount64 + 55_000, Environment.TickCount64 + 65_000);
    }

    [Fact]
    public void ASummonAlreadyExpiredAtSaveTimeStaysExpired()
    {
        var (world, _, _) = Setup(maxFollower: 5);
        var summon = world.CreateCharacter();
        summon.SetTag("SUMMON_EXPIRE_REMAINING", "0");
        summon.SetStatFlag(StatFlag.Conjured);
        summon.TryAssignOwnership(null, summoned: true);

        summon.RestoreSummonExpiry();

        Assert.True(summon.TickPetOwnershipTimers(Environment.TickCount64 + 1));
    }

    [Fact]
    public void ALegacyAbsoluteRecordIsRebuiltFromTheDuration()
    {
        // An old save carries a threshold from another session's uptime. It cannot be
        // read against this clock, so the deadline is rebuilt from the summon's own
        // duration - generous, but bounded. Leaving it would strand the summon behind
        // a threshold days away.
        var (world, _, _) = Setup(maxFollower: 5);
        var summon = world.CreateCharacter();
        summon.SetTag("SUMMON_EXPIRE_TICK", "529875218");
        summon.SetTag("SUMMON_DURATION", DurationTenths.ToString());
        summon.SetStatFlag(StatFlag.Conjured);

        summon.RestoreSummonExpiry();

        Assert.False(summon.TickPetOwnershipTimers(Environment.TickCount64));
        Assert.True(summon.TickPetOwnershipTimers(Environment.TickCount64 + 61_000));
    }

    [Fact]
    public void ALegacyRecordWithoutADurationExpiresRatherThanLivingForever()
    {
        var (world, _, _) = Setup(maxFollower: 5);
        var summon = world.CreateCharacter();
        summon.SetTag("SUMMON_EXPIRE_TICK", "529875218");
        summon.SetStatFlag(StatFlag.Conjured);
        summon.TryAssignOwnership(null, summoned: true);

        summon.RestoreSummonExpiry();

        Assert.True(summon.TickPetOwnershipTimers(Environment.TickCount64 + 1));
    }

    [Fact]
    public void ACharacterThatIsNoSummonIsLeftAlone()
    {
        // The re-base runs over every loaded character; it must touch nothing else.
        var (world, _, _) = Setup(maxFollower: 5);
        var ch = world.CreateCharacter();
        ch.SetTag("SOME_OTHER_TICK", "529875218");

        ch.RestoreSummonExpiry();

        Assert.False(ch.TryGetTag("SUMMON_EXPIRE_TICK", out _));
        Assert.True(ch.TryGetTag("SOME_OTHER_TICK", out string? other));
        Assert.Equal("529875218", other);
    }
}

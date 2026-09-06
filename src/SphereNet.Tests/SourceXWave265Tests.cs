using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 265 — reconcile the 0xBF 0x2C / 0xF4 mislabel. Source-X 0xBF 0x2C is the
/// BandageMacro (targeted bandage use, receive.cpp:3196), NOT virtue; virtue
/// invocation is EXTCMD_INVOKE_VIRTUE riding the 0x12 text-command packet as
/// ext-type 0xF4 (CClientEvent.cpp:3127). SphereNet previously mapped virtue to
/// 0xBF 0x2C and left the bandage macro unimplemented.
/// </summary>
public sealed class SourceXWave265Tests
{
    private static (GameClient client, Character player, GameWorld world, TriggerDispatcher d) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 265);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(1000, 1000, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Equip(pack, Layer.Pack);

        TestHarness.AttachCharacter(client, player);

        var d = new TriggerDispatcher();
        client.SetEngines(
            skillHandlers: new SkillHandlers(world),
            spellEngine: new SpellEngine(world, new SpellRegistry()),
            triggerDispatcher: d);
        return (client, player, world, d);
    }

    private static byte[] BandagePayload(uint bandageUid, uint targetUid) => new byte[]
    {
        (byte)(bandageUid >> 24), (byte)(bandageUid >> 16), (byte)(bandageUid >> 8), (byte)bandageUid,
        (byte)(targetUid >> 24), (byte)(targetUid >> 16), (byte)(targetUid >> 8), (byte)targetUid,
    };

    // ---- 1. BandageMacro (0xBF 0x2C) applies Healing to the given target, no cursor ----

    [Fact]
    public void BandageMacro_AppliesHealingToPreselectedTarget_NoCursor()
    {
        var (client, player, world, d) = Setup();

        var bandage = world.CreateItem();
        bandage.ItemType = ItemType.Bandage;
        bandage.BaseId = 0x0E21;
        player.Backpack!.AddItem(bandage);

        long startedSkill = -1;
        // Return True to prove routing while short-circuiting before resolution.
        d.RegisterCharEvent("EVENTSPLAYER", "SkillStart",
            (_, a) => { startedSkill = a.N1; return TriggerResult.True; });

        client.HandleExtendedCommand(0x002C, BandagePayload(bandage.Uid.Value, player.Uid.Value));

        Assert.Equal((int)SkillType.Healing, startedSkill);
        // No target cursor was sent — the packet supplied both the bandage and target.
        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x6C);
    }

    // ---- 2. A non-bandage item uid is rejected (mirror Source-X IsType(IT_BANDAGE)) ----

    [Fact]
    public void BandageMacro_NonBandageItem_IsNoOp()
    {
        var (client, player, world, d) = Setup();

        var notBandage = world.CreateItem();
        notBandage.ItemType = ItemType.WeaponSword;
        notBandage.BaseId = 0x0F5E;
        player.Backpack!.AddItem(notBandage);

        long startedSkill = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SkillStart",
            (_, a) => { startedSkill = a.N1; return TriggerResult.True; });

        client.HandleExtendedCommand(0x002C, BandagePayload(notBandage.Uid.Value, player.Uid.Value));

        Assert.Equal(-1, startedSkill); // no healing started
    }

    // ---- 3. Virtue invoke rides 0x12/0xF4 with the virtue id in N1 ----

    [Fact]
    public void VirtueInvoke_FiresTriggerWithVirtueIdInN1()
    {
        var (client, player, _, d) = Setup();

        long invokedVirtue = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "UserVirtueInvoke",
            (_, a) => { invokedVirtue = a.N1; return TriggerResult.Default; });

        client.HandleVirtueInvoke(2); // Sacrifice

        Assert.Equal(2, invokedVirtue);
    }
}

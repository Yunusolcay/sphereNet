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
/// Wave 248 — finishing the 0xBF/0xD7 extended-command tail (EquipLastWeapon,
/// TargetedSkill, spell-select repoint) plus the per-char regen AMOUNT override
/// (Source-X CChar m_regenVal) deferred from Wave 247.
/// </summary>
public sealed class SourceXWave248Tests
{
    private static (GameClient client, Character player, GameWorld world, TriggerDispatcher d) Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 248);
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

    // ---- 1. EquipLastWeapon (0xD7 0x1E) ----

    [Fact]
    public void EquipLastWeapon_RewieldsDisarmedWeaponFromPack()
    {
        var (client, player, world, _) = Setup();

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.BaseId = 0x0F5E;
        player.Backpack!.AddItem(sword);

        // Wield it → the last-weapon uid is recorded.
        player.Equip(sword, Layer.OneHanded);
        Assert.Equal(sword.Uid, player.LastWeaponUid);

        // Disarm: back into the pack.
        player.Unequip(Layer.OneHanded);
        player.Backpack!.AddItem(sword);
        Assert.Null(player.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(player.Backpack!.Uid, sword.ContainedIn);   // sword sits in the pack
        Assert.Equal(player.Uid, player.Backpack!.ContainedIn);  // pack belongs to player

        // 0xD7 0x1E has no payload; the server re-wields the tracked weapon.
        client.HandleEncodedCommand(EncodedCommandRegistry.EquipLastWeapon,
            player.Uid.Value, new PacketBuffer(Array.Empty<byte>()));

        Assert.Same(sword, player.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void EquipLastWeapon_NoLastWeapon_IsNoOp()
    {
        var (client, player, _, _) = Setup();
        client.HandleEncodedCommand(EncodedCommandRegistry.EquipLastWeapon,
            player.Uid.Value, new PacketBuffer(Array.Empty<byte>()));
        Assert.Null(player.GetEquippedItem(Layer.OneHanded));
    }

    // ---- 2. TargetedSkill (0xBF 0x2E) ----

    [Fact]
    public void TargetedSkill_UsesSkillOnPreselectedTarget_NoCursor()
    {
        var (client, player, _, d) = Setup();
        long startedSkill = -1;
        // Return True to prove routing while short-circuiting before resolution.
        d.RegisterCharEvent("EVENTSPLAYER", "SkillStart",
            (_, a) => { startedSkill = a.N1; return TriggerResult.True; });

        int skillId = (int)SkillType.Healing; // Character-target skill
        uint target = player.Uid.Value;
        client.HandleExtendedCommand(0x002E, new byte[]
        {
            (byte)(skillId >> 8), (byte)skillId,
            (byte)(target >> 24), (byte)(target >> 16), (byte)(target >> 8), (byte)target,
        });

        Assert.Equal(skillId, startedSkill);
        // No target cursor was sent — the packet supplied the target.
        var packets = TestHarness.GetQueuedPackets(client.NetState);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x6C);
    }

    // ---- 3. Spell select repoint (0xBF 0x1C) ----

    [Fact]
    public void SpellSelect_0x1C_CastsSpellNotViewport()
    {
        var (client, player, _, d) = Setup();
        long selectedSpell = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SpellSelect",
            (_, a) => { selectedSpell = a.N1; return TriggerResult.True; });

        // ClassicUO 0xBF 0x1C payload: [skip word 0x0002][spell id]. Spell 5 = Magic Arrow.
        client.HandleExtendedCommand(0x001C, new byte[] { 0x00, 0x02, 0x00, 0x05 });

        Assert.Equal(5, selectedSpell);
    }

    // ---- 4. Per-char regen AMOUNT override (REGENVAL*) ----

    private static Character MakeWounded(ushort body)
    {
        var ch = new Character { BodyId = body, IsPlayer = true };
        ch.MaxHits = 100;
        ch.Hits = 50;
        ch.Food = 10;
        return ch;
    }

    [Fact]
    public void RegenVal_DefaultsToOne_AndRoundTrips()
    {
        var ch = new Character { BodyId = 0x0190 };
        ch.TryGetProperty("REGENVALHITS", out string def);
        Assert.Equal("1", def); // Source-X max(1, 0)

        ch.TrySetProperty("REGENVALHITS", "5");
        Assert.Equal((ushort)5, ch.RegenValHits);
        ch.TryGetProperty("REGENVALHITS", out string got);
        Assert.Equal("5", got);
    }

    [Fact]
    public void RegenValHits_OverridesRegenAmount()
    {
        // Gargoyle (no human racial): the flat per-event amount is the override.
        var garg = MakeWounded(0x029A);
        garg.TrySetProperty("REGENVALHITS", "5");
        garg.OnTick();
        Assert.Equal(55, garg.Hits); // 50 + 5

        // Human still adds its +2 racial on top of the override.
        var human = MakeWounded(0x0190);
        human.TrySetProperty("REGENVALHITS", "5");
        human.OnTick();
        Assert.Equal(57, human.Hits); // 50 + 5 + 2
    }
}

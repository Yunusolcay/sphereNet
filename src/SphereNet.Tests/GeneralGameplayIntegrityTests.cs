using Microsoft.Extensions.Logging;
using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Guild;
using SphereNet.Game.Mounts;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Trade;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("VendorStateSerial")]
public sealed class GeneralGameplayIntegrityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character CreatePlayer(GameWorld world, short x = 100, short y = 100)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Str = short.MaxValue;
        player.MaxHits = player.Hits = 100;
        world.PlaceCharacter(player, new Point3D(x, y, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;
        return player;
    }

    [Fact]
    public void DeepContainerTree_CountsAllWeightAndRejectsAncestorCycle()
    {
        var world = CreateWorld();
        var root = world.CreateItem();
        root.ItemType = ItemType.Container;
        var current = root;
        for (int i = 0; i < 24; i++)
        {
            var nested = world.CreateItem();
            nested.ItemType = ItemType.Container;
            Assert.True(current.TryAddItem(nested));
            current = nested;
        }

        var payload = world.CreateItem();
        payload.Amount = 4;
        Assert.True(current.TryAddItem(payload));

        Assert.True(root.TotalWeightTenths >= 29);
        Assert.False(current.TryAddItem(root));
    }

    [Fact]
    public void ContainerMove_RemovesOldParentReference()
    {
        var world = CreateWorld();
        var first = world.CreateItem(); first.ItemType = ItemType.Container;
        var second = world.CreateItem(); second.ItemType = ItemType.Container;
        var item = world.CreateItem();
        Assert.True(first.TryAddItem(item));

        Assert.True(second.TryAddItem(item));

        Assert.DoesNotContain(item, first.Contents);
        Assert.Contains(item, second.Contents);
        Assert.Equal(second.Uid, item.ContainedIn);
    }

    [Fact]
    public void VendorBuy_DuplicateStockSerialIsRejectedAtomically()
    {
        var world = CreateWorld();
        VendorEngine.World = world;
        var buyer = CreatePlayer(world);
        var gold = world.CreateItem(); gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 1000;
        buyer.Backpack!.AddItem(gold);
        var vendor = world.CreateCharacter(); vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, buyer.Position);
        var stock = world.CreateItem(); stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);
        var row = world.CreateItem(); row.BaseId = 0x0F52; row.Amount = 5; row.SetTag("PRICE", "10");
        stock.AddItem(row);

        int result = VendorEngine.ProcessBuy(buyer, vendor,
        [
            new TradeEntry { ItemUid = row.Uid, Amount = 3 },
            new TradeEntry { ItemUid = row.Uid, Amount = 3 },
        ]);

        Assert.Equal(-1, result);
        Assert.Equal(5, row.Amount);
        Assert.Equal(1000, VendorEngine.CountGold(buyer));
        Assert.DoesNotContain(buyer.Backpack.Contents, i => i.BaseId == row.BaseId);
    }

    [Fact]
    public void VendorBuy_FullBackpackDropsPaidGoodsAtBuyerFeet()
    {
        var world = CreateWorld();
        VendorEngine.World = world;
        var buyer = CreatePlayer(world);
        var gold = world.CreateItem(); gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 1000;
        buyer.Backpack!.AddItem(gold);
        for (int i = 1; i < Item.MaxContainerItems; i++)
        {
            var filler = world.CreateItem(); filler.BaseId = (ushort)(0x2000 + (i % 100));
            Assert.True(buyer.Backpack.TryAddItem(filler));
        }

        var vendor = world.CreateCharacter(); vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, buyer.Position);
        var stock = world.CreateItem(); stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);
        var row = world.CreateItem(); row.BaseId = 0x0F52; row.Amount = 1; row.SetTag("PRICE", "10");
        stock.AddItem(row);

        Assert.Equal(10, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));

        Assert.Equal(990, VendorEngine.CountGold(buyer));
        Assert.Contains(world.GetItemsInRange(buyer.Position, 0),
            i => i.BaseId == 0x0F52 && !i.IsDeleted && !i.ContainedIn.IsValid);
    }

    [Fact]
    public void VendorSell_MalformedBuyTemplateDoesNotBecomeBuyAnything()
    {
        var world = CreateWorld();
        VendorEngine.World = world;
        var seller = CreatePlayer(world);
        var item = world.CreateItem(); item.BaseId = 0x0F52; seller.Backpack!.AddItem(item);
        var vendor = world.CreateCharacter(); vendor.NpcBrain = NpcBrainType.Vendor;
        vendor.SetTag("VENDOR_BUY_LIST", "missing_vendor_template");
        vendor.SetTag("VENDOR_GOLD", "1000");

        Assert.NotNull(VendorEngine.GetVendorBuyFilter(vendor));
        Assert.Empty(VendorEngine.GetVendorBuyFilter(vendor)!);
        Assert.Equal(0, VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = item.Uid, Amount = 1 }]));
        Assert.Contains(item, seller.Backpack.Contents);
    }

    [Fact]
    public void SecureTrade_FullRecipientBackpackFailsBeforeTransfer()
    {
        var world = CreateWorld();
        var recipient = CreatePlayer(world);
        for (int i = 0; i < Item.MaxContainerItems; i++)
        {
            var filler = world.CreateItem(); filler.BaseId = (ushort)(0x3000 + (i % 100));
            Assert.True(recipient.Backpack!.TryAddItem(filler));
        }
        var offer = world.CreateItem(); offer.ItemType = ItemType.Container;
        offer.AddItem(world.CreateItem());

        Assert.False(TradeManager.CanAcceptTradeItems(recipient, world, offer, out string? reason));
        Assert.Contains("backpack", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureTrade_TradeCreateVetoReturnsInitialItemAndCreatesNoSession()
    {
        var world = CreateWorld();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 4401);
        var initiator = CreatePlayer(world);
        var partner = CreatePlayer(world, 101, 100);
        TestHarness.AttachCharacter(client, initiator);
        var offered = world.CreateItem(); initiator.Backpack!.AddItem(offered);
        var trades = new TradeManager();
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "TradeCreate", (_, _) => TriggerResult.True);
        client.SetEngines(tradeManager: trades, triggerDispatcher: triggers);

        // A trade partner must have an active client (Source-X Cmd_SecureTrade
        // refuses offline players); the test partner never logs in.
        partner.IsOnline = true;
        client.InitiateTrade(partner, offered);

        Assert.Null(trades.FindTradeFor(initiator));
        Assert.Contains(offered, initiator.Backpack.Contents);
        Assert.False(offered.IsDeleted);
    }

    [Fact]
    public void Party_NonLeaderCannotInviteAndFullForceAddDoesNotEvictTarget()
    {
        var parties = new PartyManager();
        var leader = new Serial(1); var member = new Serial(2); var target = new Serial(50);
        Assert.True(parties.AcceptInvite(leader, member));
        Assert.False(parties.AcceptInvite(member, new Serial(3)));

        var destination = parties.FindParty(leader)!;
        for (uint uid = 3; uid <= 10; uid++)
            Assert.True(destination.AddMember(new Serial(uid)));
        var oldParty = parties.CreateParty(target);
        Assert.True(oldParty.AddMember(new Serial(51)));
        Assert.True(oldParty.AddMember(new Serial(52)));

        parties.ForceAddMember(leader, target);

        Assert.Same(oldParty, parties.FindParty(target));
        Assert.False(destination.IsMember(target));
    }

    [Fact]
    public void PartyPrivateMessage_CannotTargetPlayerOutsideParty()
    {
        var world = CreateWorld();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 4402);
        var sender = CreatePlayer(world);
        var member = CreatePlayer(world, 101, 100);
        var outsider = CreatePlayer(world, 102, 100);
        TestHarness.AttachCharacter(client, sender);
        var parties = new PartyManager();
        Assert.True(parties.AcceptInvite(sender.Uid, member.Uid));
        var recipients = new List<Serial>();
        client.SendToChar = (uid, _) => recipients.Add(uid);
        client.SetEngines(partyManager: parties);

        client.HandleExtendedCommand(0x0006, BuildPrivatePartyMessage(outsider.Uid, "blocked"));
        Assert.DoesNotContain(outsider.Uid, recipients);

        client.HandleExtendedCommand(0x0006, BuildPrivatePartyMessage(member.Uid, "allowed"));
        Assert.Contains(member.Uid, recipients);
    }

    [Fact]
    public void PartyInviteTriggerVeto_DoesNotLeavePendingInvite()
    {
        var world = CreateWorld();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 4403);
        var inviter = CreatePlayer(world);
        var target = CreatePlayer(world, 101, 100);
        TestHarness.AttachCharacter(client, inviter);
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "PartyInvite", (_, _) => TriggerResult.True);
        client.SetEngines(partyManager: new PartyManager(), triggerDispatcher: triggers);
        uint uid = target.Uid.Value;

        client.HandleExtendedCommand(0x0006,
            [1, (byte)(uid >> 24), (byte)(uid >> 16), (byte)(uid >> 8), (byte)uid]);

        Assert.False(target.TryGetTag("PARTY_INVITE_FROM", out _));
        Assert.False(target.TryGetTag("PARTY_INVITE_TIME", out _));
    }

    [Fact]
    public void GuildManager_MirrorsWarAndAllianceDirections()
    {
        var manager = new GuildManager();
        var first = manager.CreateGuild(new Serial(100), "First", new Serial(1));
        var second = manager.CreateGuild(new Serial(200), "Second", new Serial(2));

        Assert.True(manager.DeclareWar(first.StoneUid, second.StoneUid));
        Assert.True(first.GetRelation(second.StoneUid)!.WeDeclaredWar);
        Assert.True(second.GetRelation(first.StoneUid)!.TheyDeclaredWar);
        Assert.False(first.IsAtWarWith(second.StoneUid));
        Assert.True(manager.DeclareWar(second.StoneUid, first.StoneUid));
        Assert.True(first.IsAtWarWith(second.StoneUid));
        Assert.True(second.IsAtWarWith(first.StoneUid));
        Assert.True(manager.DeclarePeace(first.StoneUid, second.StoneUid));
        Assert.False(first.IsAtWarWith(second.StoneUid));
        Assert.False(second.IsAtWarWith(first.StoneUid));

        Assert.True(manager.DeclareAlliance(first.StoneUid, second.StoneUid));
        Assert.True(manager.DeclareAlliance(second.StoneUid, first.StoneUid));
        Assert.True(first.IsAlliedWith(second.StoneUid));
        Assert.True(second.IsAlliedWith(first.StoneUid));
        Assert.True(manager.WithdrawAlliance(first.StoneUid, second.StoneUid));
        Assert.False(first.IsAlliedWith(second.StoneUid));
        Assert.False(second.IsAlliedWith(first.StoneUid));
    }

    [Fact]
    public void GuildCandidate_HasRecordButNoMemberRights()
    {
        var manager = new GuildManager();
        var guild = manager.CreateGuild(new Serial(100), "Guild", new Serial(1));
        var candidate = new Serial(2);
        guild.Abbreviation = "G";
        guild.AddRecruit(candidate);

        Assert.Null(manager.FindGuildFor(candidate));
        Assert.Same(guild, manager.FindGuildRecordFor(candidate));
        Assert.Equal("", manager.GetAbbrevSuffix(candidate));
    }

    [Fact]
    public void GuildDeserialize_RepairsLegacyMirroredWarFlags()
    {
        var world = CreateWorld();
        var firstMaster = world.CreateCharacter();
        var secondMaster = world.CreateCharacter();
        var firstStone = world.CreateItem();
        var secondStone = world.CreateItem();
        firstStone.SetTag("GUILD.NAME", "First");
        secondStone.SetTag("GUILD.NAME", "Second");
        firstStone.SetTag("GUILD.MEMBERS", $"0{firstMaster.Uid.Value:X}:2::0:0:1");
        secondStone.SetTag("GUILD.MEMBERS", $"0{secondMaster.Uid.Value:X}:2::0:0:1");
        firstStone.SetTag("GUILD.RELATIONS", $"0{secondStone.Uid.Value:X}:1:0:0:0");
        secondStone.SetTag("GUILD.RELATIONS", $"0{firstStone.Uid.Value:X}:1:0:0:0");

        var manager = new GuildManager();
        manager.DeserializeFromWorld(world);

        Assert.True(manager.GetGuild(firstStone.Uid)!.IsAtWarWith(secondStone.Uid));
        Assert.True(manager.GetGuild(secondStone.Uid)!.IsAtWarWith(firstStone.Uid));
    }

    [Fact]
    public void GuildMemberTitleWithComma_SurvivesSaveLoad_WithoutDroppingMembers()
    {
        var world = CreateWorld();
        var master = world.CreateCharacter();
        var member = world.CreateCharacter();
        var member2 = world.CreateCharacter();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        var manager = new GuildManager();
        var guild = manager.CreateGuild(stone.Uid, "Test", master.Uid);
        var rec = guild.AddRecruit(member.Uid);
        guild.AcceptMember(member.Uid);
        rec.Title = "Knight, Defender of the Realm"; // comma is the record separator
        var rec2 = guild.AddRecruit(member2.Uid);
        guild.AcceptMember(member2.Uid);
        rec2.Title = "Squire"; // packed AFTER the comma member — must not be dropped

        manager.SerializeAllToTags(world);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(world);
        var restored = reloaded.GetGuild(stone.Uid)!;

        Assert.NotNull(restored.FindMember(member.Uid));
        Assert.Equal("Knight, Defender of the Realm", restored.FindMember(member.Uid)!.Title);
        Assert.NotNull(restored.FindMember(member2.Uid)); // trailing member survived
        Assert.Equal("Squire", restored.FindMember(member2.Uid)!.Title);
    }

    [Fact]
    public void GuildHousesShips_SurviveSaveLoad_DroppingDeletedMultis()
    {
        var world = CreateWorld();
        var master = world.CreateCharacter();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        // Two live houses and a live ship; one house uid points at nothing.
        var house1 = world.CreateItem();
        var ship1 = world.CreateItem();
        var ghostHouse = new Serial(0xDEAD);

        var manager = new GuildManager();
        var guild = manager.CreateGuild(stone.Uid, "Builders", master.Uid);
        guild.AddHouse(house1.Uid);
        guild.AddHouse(ghostHouse);
        guild.AddShip(ship1.Uid);
        guild.MaxHouses = 7;

        manager.SerializeAllToTags(world);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(world);
        var restored = reloaded.GetGuild(stone.Uid)!;

        // The ghost house dropped off (its item no longer exists); the live ones stay.
        Assert.Equal(1, restored.HouseCount);
        Assert.Contains(house1.Uid, restored.Houses);
        Assert.DoesNotContain(ghostHouse, restored.Houses);
        Assert.Equal(1, restored.ShipCount);
        Assert.Contains(ship1.Uid, restored.Ships);
        Assert.Equal(7, restored.MaxHouses);
    }

    [Fact]
    public void GuildGump_ForgedMasterButtonIsRejectedForOutsider()
    {
        var world = CreateWorld();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var outsider = CreatePlayer(world);
        var master = CreatePlayer(world, 101, 100);
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, outsider.Position);
        var guilds = new GuildManager();
        var guild = guilds.CreateGuild(stone.Uid, "Guild", master.Uid);
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 4404);
        TestHarness.AttachCharacter(client, outsider);
        client.SetEngines(guildManager: guilds);
        typeof(GameClient).GetMethod("OpenGuildStoneGump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [stone]);
        uint gumpId = Assert.Single(client.Gumps.ActiveGumps);

        client.HandleGumpResponse(stone.Uid.Value, gumpId, 14, [], [(1, "hacked charter")]);

        Assert.Equal("", guild.Charter);
    }

    [Fact]
    public void MountEngine_RejectsWildOrForeignMountWithoutTakingOwnership()
    {
        var world = CreateWorld();
        var rider = CreatePlayer(world);
        var horse = world.CreateCharacter(); horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));

        Assert.False(new MountEngine(world).TryMount(rider, horse));
        Assert.False(horse.HasOwner(rider.Uid));
        Assert.False(rider.IsMounted);
    }

    [Fact]
    public void MountAndDismountTriggers_CanVetoBeforeStateMutation()
    {
        var world = CreateWorld();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var rider = CreatePlayer(world);
        var horse = world.CreateCharacter(); horse.BodyId = 0x00C8;
        horse.TryAssignOwnership(rider, rider);
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 4405);
        TestHarness.AttachCharacter(client, rider);
        var mountEngine = new MountEngine(world);
        var vetoMount = new TriggerDispatcher();
        vetoMount.RegisterCharEvent("EVENTSPLAYER", "Mount", (_, _) => TriggerResult.True);
        client.SetEngines(mountEngine: mountEngine, triggerDispatcher: vetoMount);

        client.HandleDoubleClick(horse.Uid.Value);
        Assert.False(rider.IsMounted);
        Assert.False(horse.IsStatFlag(StatFlag.Ridden));

        client.SetEngines(mountEngine: mountEngine, triggerDispatcher: null);
        Assert.True(mountEngine.TryMount(rider, horse));
        var vetoDismount = new TriggerDispatcher();
        vetoDismount.RegisterCharEvent("EVENTSPLAYER", "Dismount", (_, _) => TriggerResult.True);
        client.SetEngines(mountEngine: mountEngine, triggerDispatcher: vetoDismount);

        client.HandleDoubleClick(rider.Uid.Value);
        Assert.True(rider.IsMounted);
        Assert.True(horse.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void StableClaim_InvalidDestinationKeepsStoredPet()
    {
        var world = CreateWorld();
        var owner = CreatePlayer(world);
        var pet = world.CreateCharacter();
        pet.BodyId = 0x00C8;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        var stable = new StableEngine();
        Assert.True(stable.StablePet(owner, pet, world));

        Assert.Null(stable.ClaimPet(owner, 0, world, new Point3D(-1, -1, 0, 0)));
        Assert.Equal(1, stable.GetStabledCount(owner));
    }

    [Fact]
    public void PetFigurine_InvalidDestinationDoesNotConsumeFigurine()
    {
        var world = CreateWorld();
        var owner = CreatePlayer(world);
        var pet = world.CreateCharacter(); pet.BodyId = 0x00C8;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        var figurine = world.CreateItem(); owner.Backpack!.AddItem(figurine);
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        Assert.Null(PetFigurine.Restore(owner, figurine, world, new Point3D(-1, -1, 0, 0)));
        Assert.False(figurine.IsDeleted);
        Assert.Same(figurine, world.FindItem(figurine.Uid));
    }

    [Fact]
    public void DeleteObject_StaleReferenceCannotDeleteRecycledSerialObject()
    {
        var world = CreateWorld();
        var oldItem = world.CreateItem();
        var recycledSerial = oldItem.Uid;
        world.DeleteObject(oldItem);
        var current = world.CreateItem();
        Assert.Equal(recycledSerial, current.Uid);

        world.DeleteObject(oldItem);

        Assert.Same(current, world.FindItem(recycledSerial));
        Assert.False(current.IsDeleted);
    }

    [Fact]
    public void DeleteObject_CharacterAlsoDeletesEquipmentAndContainerTree()
    {
        var world = CreateWorld();
        var player = CreatePlayer(world);
        var pack = player.Backpack!;
        var nested = world.CreateItem();
        nested.ItemType = ItemType.Container;
        var content = world.CreateItem();
        Assert.True(pack.TryAddItem(nested));
        Assert.True(nested.TryAddItem(content));

        world.DeleteObject(player);

        Assert.Null(world.FindChar(player.Uid));
        Assert.Null(world.FindItem(pack.Uid));
        Assert.Null(world.FindItem(nested.Uid));
        Assert.Null(world.FindItem(content.Uid));
        Assert.True(pack.IsDeleted);
        Assert.True(nested.IsDeleted);
        Assert.True(content.IsDeleted);
    }

    [Fact]
    public void CharacterWeight_IgnoresBankAndVendorServiceContainers()
    {
        var world = CreateWorld();
        var player = CreatePlayer(world);
        var carried = world.CreateItem();
        carried.Amount = 10;
        Assert.True(player.Backpack!.TryAddItem(carried));
        int carriedWeight = player.GetTotalWeightTenths();

        var bank = world.CreateItem();
        bank.ItemType = ItemType.Container;
        Assert.True(player.Equip(bank, Layer.BankBox));
        var bankGold = world.CreateItem();
        bankGold.Amount = ushort.MaxValue;
        Assert.True(bank.TryAddItem(bankGold));

        Assert.Equal(carriedWeight, player.GetTotalWeightTenths());
    }

    [Fact]
    public void Equip_DetachesItemFromItsOldContainerAndPreviousLayer()
    {
        var world = CreateWorld();
        var player = CreatePlayer(world);
        var item = world.CreateItem();
        Assert.True(player.Backpack!.TryAddItem(item));

        Assert.True(player.Equip(item, Layer.OneHanded));
        Assert.DoesNotContain(item, player.Backpack.Contents);
        Assert.Same(item, player.GetEquippedItem(Layer.OneHanded));

        Assert.True(player.Equip(item, Layer.Ring));
        Assert.Null(player.GetEquippedItem(Layer.OneHanded));
        Assert.Same(item, player.GetEquippedItem(Layer.Ring));
        Assert.Equal(player.Uid, item.ContainedIn);
    }

    [Fact]
    public void MoveCharacter_InvalidDestinationReturnsFalseAndKeepsSectorPosition()
    {
        var world = CreateWorld();
        var player = CreatePlayer(world);
        var oldPosition = player.Position;

        Assert.False(world.MoveCharacter(player, new Point3D(-1, -1, 0, 0)));
        Assert.Equal(oldPosition, player.Position);
        Assert.Contains(player, world.GetCharsInRange(oldPosition, 0));
    }

    private static byte[] BuildPrivatePartyMessage(Serial target, string text)
    {
        byte[] encoded = System.Text.Encoding.BigEndianUnicode.GetBytes(text + "\0");
        byte[] data = new byte[5 + encoded.Length];
        data[0] = 3;
        data[1] = (byte)(target.Value >> 24);
        data[2] = (byte)(target.Value >> 16);
        data[3] = (byte)(target.Value >> 8);
        data[4] = (byte)target.Value;
        encoded.CopyTo(data, 5);
        return data;
    }
}

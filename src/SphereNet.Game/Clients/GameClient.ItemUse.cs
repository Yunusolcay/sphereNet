using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{

    public void HandleDoubleClick(uint uid)
    {
        if (_character == null) return;

        if (uid == _character.Uid.Value)
        {
            // If mounted, dismount on self-dclick
            if (_character.IsMounted && _mountEngine != null)
            {
                uint oldMountItemUid = _character.GetEquippedItem(Layer.Horse)?.Uid.Value ?? 0;
                var npc = _mountEngine.Dismount(_character);

                // Correct Z to terrain after body type change (mounted→foot)
                var mapData = _world.MapData;
                if (mapData != null)
                {
                    sbyte correctedZ = mapData.GetEffectiveZ(_character.MapIndex,
                        _character.X, _character.Y, _character.Z);
                    if (correctedZ != _character.Z)
                    {
                        _logger.LogInformation("[DISMOUNT] Z correction: {OldZ} -> {NewZ}", _character.Z, correctedZ);
                        _character.Position = new Point3D(_character.X, _character.Y, correctedZ, _character.MapIndex);
                    }
                }

                if (npc != null)
                {
                    // Immediately remove the old horse-layer item from all clients to avoid ghost mount visuals.
                    if (oldMountItemUid != 0)
                        BroadcastDeleteObject(oldMountItemUid);

                    // Snap the client back to the server-authoritative rider position
                    // and drop the horse right there. Without the snap the client
                    // keeps its predicted (possibly 1 tile ahead) position while the
                    // server places the NPC at the real rider.Position, producing
                    // the "horse spawns a tile behind me" complaint. With the snap
                    // the player may briefly slide back one tile, but the horse is
                    // always exactly under the character — which is the contract.
                    _nextMoveTime = 0;
                    _netState.WalkSequence = 0;
                    _netState.Send(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    // MobileMoving (0x77) to self + nearby — body update without screen reload.
                    byte flags = BuildMobileFlags(_character);
                    byte dir77 = (byte)((byte)_character.Direction & 0x07);
                    byte noto = GetNotoriety(_character);
                    var movePacket = new PacketMobileMoving(
                        _character.Uid.Value, _character.BodyId,
                        _character.X, _character.Y, _character.Z, dir77,
                        _character.Hue, flags, noto);
                    _netState.Send(movePacket); // self — update own body
                    if (BroadcastMoveNearby != null)
                        BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
                    else
                        BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

                    // Clear Ridden flag AFTER sending all dismount packets.
                    npc.ClearStatFlag(StatFlag.Ridden);

                    // Broadcast the dismounted NPC to nearby clients so it appears
                    // immediately. Without this the pet is invisible until the next
                    // view-delta tick and cannot be seen following the owner.
                    BroadcastCharacterAppear?.Invoke(npc);
                }
                return;
            }
            SendPaperdoll(_character);
            return;
        }

        var item = _world.FindItem(new Serial(uid));
        if (item != null)
        {
            // Fire @DClick on item — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DClick,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (result == TriggerResult.True)
                    return;
            }
            HandleItemUse(item);
            return;
        }

        var ch = _world.FindChar(new Serial(uid));
        if (ch != null)
        {
            // Fire @DClick on character — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.DClick,
                    new TriggerArgs { CharSrc = _character });
                if (result == TriggerResult.True)
                    return;
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Vendor)
            {
                HandleVendorInteraction(ch);
                return;
            }

            // Mount check — double-click mountable NPC
            if (!ch.IsPlayer && _mountEngine != null &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                // Already riding — block with message instead of falling through to paperdoll
                if (_character.IsMounted)
                {
                    SysMessage(ServerMessages.Get("mount_already_riding"));
                    return;
                }

                // UO mount-range rule: the mount must be adjacent (within 1 tile).
                // Without this check, a distant mount gets accepted by the server
                // while the client teleports the player to the mount's tile — the
                // classic "I got yanked onto my horse" glitch.
                int dx = Math.Abs(_character.X - ch.X);
                int dy = Math.Abs(_character.Y - ch.Y);
                if (_character.MapIndex != ch.MapIndex || dx > 1 || dy > 1)
                {
                    SysMessage("That is too far away.");
                    return;
                }

                uint mountNpcUid = ch.Uid.Value;
                if (_mountEngine.TryMount(_character, ch))
                {
                    // Correct Z to terrain after body type change (foot→mounted)
                    var mountMapData = _world.MapData;
                    if (mountMapData != null)
                    {
                        sbyte correctedZ = mountMapData.GetEffectiveZ(_character.MapIndex,
                            _character.X, _character.Y, _character.Z);
                        if (correctedZ != _character.Z)
                        {
                            _character.Position = new Point3D(_character.X, _character.Y, correctedZ, _character.MapIndex);
                        }
                    }

                    // Immediately remove the old NPC mount from nearby clients to prevent temporary duplicates.
                    BroadcastDeleteObject(mountNpcUid);

                    // Reset walk state — foot→mount speed transition
                    _netState.WalkSequence = 0;
                    _nextMoveTime = 0;

                    // MoveReject FIRST — clears walk queue + Offset.Z, sets exact position
                    _netState.Send(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    // DrawObject AFTER — body/equipment update with Steps queue already cleared.
                    // BroadcastDrawObject sends to self + nearby clients.
                    BroadcastDrawObject(_character);
                    return;
                }
            }

            if (IsHumanLikeBody(ch.BodyId))
                SendPaperdoll(ch);
        }
    }

    private static bool IsHumanLikeBody(ushort body) =>
        body is 0x0190 or 0x0191 or 0x0192 or 0x0193
            or 0x025D or 0x025E or 0x029A or 0x029B;

    /// <summary>
    /// Source-X CClient::Cmd_Use_Item parity dispatcher.
    /// The Source-X switch handles ~30 IT_* branches; SphereNet mirrors each
    /// branch to either a real handler or, when the underlying engine is not
    /// yet ported, the matching DEFMSG_ITEMUSE_* + target-cursor prompt so
    /// players see the exact upstream UX. Anything not matched falls through
    /// to DEFMSG_ITEMUSE_CANTTHINK like upstream.
    /// </summary>
    private void HandleItemUse(Item item)
    {
        if (_character == null) return;
        if (_character.IsDead)
        {
            SysMessage(ServerMessages.Get("death_cant_while_dead"));
            return;
        }

        switch (item.ItemType)
        {
            // ---- containers / corpses ----
            case ItemType.Container:
            case ItemType.Corpse:
            case ItemType.TrashCan:
            case ItemType.ShipHold:
                SendOpenContainer(item);
                break;

            case ItemType.ContainerLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockContNoKey));
                break;

            case ItemType.ShipHoldLock:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockHoldNoKey));
                break;

            // ---- doors ----
            case ItemType.Door:
                ToggleDoor(item);
                break;
            case ItemType.DoorLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                break;

            // ---- consumables / potions / books ----
            case ItemType.Potion:
                UsePotion(item);
                break;
            case ItemType.Food:
            case ItemType.Fruit:
            case ItemType.Drink:
                _character.Food = (ushort)Math.Min(_character.Food + 5, 60);
                SysMessage(ServerMessages.Get("itemuse_eat_food"));
                if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                {
                    item.Delete();
                }
                break;

            case ItemType.Book:
            case ItemType.Message:
                OpenBook(item, item.ItemType == ItemType.Book);
                break;

            case ItemType.Spellbook:
            case ItemType.SpellbookNecro:
            case ItemType.SpellbookPala:
            case ItemType.SpellbookBushido:
            case ItemType.SpellbookNinjitsu:
            case ItemType.SpellbookArcanist:
            case ItemType.SpellbookMystic:
            case ItemType.SpellbookMastery:
            case ItemType.SpellbookExtra:
            {
                ushort scrollOffset = item.ItemType switch
                {
                    ItemType.SpellbookNecro => 101,
                    ItemType.SpellbookPala => 201,
                    ItemType.SpellbookBushido => 401,
                    ItemType.SpellbookNinjitsu => 501,
                    ItemType.SpellbookArcanist => 601,
                    ItemType.SpellbookMystic => 677,
                    ItemType.SpellbookMastery => 701,
                    _ => 1
                };
                ulong spellBits = ((ulong)item.More2 << 32) | item.More1;
                _netState.Send(new PacketSpellbookContent(
                    item.Uid.Value, item.BaseId, scrollOffset, spellBits));
                _netState.Send(new PacketOpenContainer(item.Uid.Value, 0x003E, _netState.IsClientPost7090));
                break;
            }

            // ---- tools that target a follow-up object ----
            case ItemType.Bandage:
                SysMessage(ServerMessages.Get(Msg.ItemuseBandagePromt));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Healing, new Serial(serial)));
                break;

            case ItemType.Lockpick:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Lockpicking, new Serial(serial)));
                break;

            case ItemType.Scissors:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => HandleScissorsTarget(item, new Serial(serial)));
                break;

            case ItemType.Tracker:
                SysMessage(ServerMessages.Get(Msg.ItemuseTrackerAttune));
                SetPendingTarget((serial, x, y, z, gfx) => item.SetTag("LINK", serial.ToString()));
                break;

            case ItemType.Key:
            case ItemType.Keyring:
                if (item.ContainedIn != _character.Backpack?.Uid && _character.PrivLevel < PrivLevel.GM)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseKeyFail));
                    break;
                }
                SysMessage(ServerMessages.Get(Msg.ItemuseKeyPromt));
                SetPendingTarget((serial, x, y, z, gfx) => HandleKeyUse(item, new Serial(serial)));
                break;

            case ItemType.HairDye:
                if (_character.GetEquippedItem(Layer.Hair) == null && _character.GetEquippedItem(Layer.FacialHair) == null)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseDyeNohair));
                    break;
                }
                SysMessage("Choose a new color for your hair."); // Source-X dialog d_hair_dye not yet ported.
                break;

            case ItemType.Dye:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeVat));
                SetPendingTarget((serial, x, y, z, gfx) => HandleDyePickup(item, new Serial(serial)));
                break;

            case ItemType.DyeVat:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeTarg));
                SetPendingTarget((serial, x, y, z, gfx) => HandleDyeApply(item, new Serial(serial)));
                break;

            // ---- weapons (target prompt for stab/pluck) ----
            case ItemType.WeaponSword:
            case ItemType.WeaponFence:
            case ItemType.WeaponAxe:
            case ItemType.WeaponMaceSharp:
            case ItemType.WeaponMaceStaff:
            case ItemType.WeaponMaceSmith:
                SysMessage(ServerMessages.Get(Msg.ItemuseWeaponPromt));
                SetPendingTarget((serial, x, y, z, gfx) => { /* tinker/poison etc unimplemented */ });
                break;

            case ItemType.WeaponMaceCrook:
                SysMessage(ServerMessages.Get(Msg.ItemuseCrookPromt));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Herding, new Serial(serial)));
                break;

            case ItemType.WeaponMacePick:
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseMacepickTarg, item.Name ?? "pick"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Mining, new Serial(serial), new Point3D(x, y, z)));
                break;

            // ---- pole/sextant/spyglass ----
            case ItemType.FishPole:
                SysMessage(ServerMessages.Get("fishing_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Fishing, new Serial(serial), new Point3D(x, y, z)));
                break;
            case ItemType.Fish:
                SysMessage(ServerMessages.Get(Msg.ItemuseFishFail));
                break;
            case ItemType.Telescope:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Sextant:
                SysMessage($"Location: {_character.X}, {_character.Y}, {_character.Z}");
                break;
            case ItemType.SpyGlass:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Map:
            case ItemType.MapBlank:
                SysMessage("You unroll the map."); // gump pending
                break;

            // ---- ore / forge / ingot (overridable via @DClick trigger) ----
            case ItemType.Ore:
                SysMessage(ServerMessages.Get(Msg.ItemuseForge));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Mining, new Serial(serial), new Point3D(x, y, z)));
                break;
            case ItemType.Forge:
            case ItemType.Ingot:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- crafting tools → default crafting gump (overridable via @DClick trigger) ----
            case ItemType.Mortar:
                OpenCraftingGump(SkillType.Alchemy);
                break;
            case ItemType.Carpentry:
            case ItemType.CarpentryChop:
                OpenCraftingGump(SkillType.Carpentry);
                break;
            case ItemType.CartographyTool:
                OpenCraftingGump(SkillType.Cartography);
                break;
            case ItemType.CookingTool:
                OpenCraftingGump(SkillType.Cooking);
                break;
            case ItemType.TinkerTools:
                OpenCraftingGump(SkillType.Tinkering);
                break;
            case ItemType.SewingKit:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.ScrollBlank:
                OpenCraftingGump(SkillType.Inscription);
                break;

            // ---- ship / sign / shrine / runes ----
            case ItemType.ShipTiller:
                NpcSpeech(_character, ServerMessages.Get(Msg.ItemuseTillerman));
                break;
            case ItemType.Shrine:
                if (_character.IsDead)
                    SysMessage(ServerMessages.Get(Msg.HealingRes));
                else
                    SysMessage(ServerMessages.Get("itemuse_shrine"));
                break;
            case ItemType.Rune:
                SysMessage(ServerMessages.Get(Msg.ItemuseRuneName));
                break;

            // ---- bulletin / game / clock / spawn / animations ----
            case ItemType.BBoard:
                SysMessage("You open the bulletin board."); // bbox gump pending
                break;
            case ItemType.GameBoard:
                if (item.ContainedIn.IsValid)
                    SysMessage(ServerMessages.Get(Msg.ItemuseGameboardFail));
                else
                    SendOpenContainer(item);
                break;
            case ItemType.Clock:
                ObjectMessage(item, FormatLocalGameTime());
                break;
            case ItemType.AnimActive:
                SysMessage(ServerMessages.Get("item_in_use"));
                break;
            case ItemType.SpawnItem:
            case ItemType.SpawnChar:
                // Source-X parity: DClick on spawn item kills children and resets timer.
                if (item.SpawnChar != null)
                {
                    var defName = item.SpawnChar.GetSpawnDefName();
                    item.SpawnChar.KillAll();
                    item.SpawnChar.ResetTimer();
                    SysMessage($"Spawn reset: {defName}. {item.SpawnChar.CurrentCount}/{item.SpawnChar.MaxCount}");
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseSpawnReset));
                }
                break;

            // ---- spell tools (Source-X routes via CClient::Cmd_Skill_Magery) ----
            case ItemType.Wand:
                if (item.More1 > 0)
                    HandleCastSpell((SpellType)item.More1, 0);
                else
                    SysMessage("This wand has no charges.");
                break;
            case ItemType.Scroll:
                if (item.More1 > 0)
                {
                    var scrollSpell = (SpellType)item.More1;
                    _character.SetTag("SCROLL_UID", item.Uid.Value.ToString());
                    HandleCastSpell(scrollSpell, 0);
                }
                else
                {
                    SysMessage("The scroll is blank.");
                }
                break;

            // ---- crystal ball / cannon ----
            case ItemType.CrystalBall:
                break; // Source-X: gaze, no message.
            case ItemType.CannonBall:
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseCballPromt, item.Name ?? "cannon ball"));
                break;
            case ItemType.CannonMuzzle:
                SysMessage(ServerMessages.Get(Msg.ItemuseCannonTarg));
                break;

            // ---- containers / signs / multi (existing engines) ----
            case ItemType.StoneGuild:
                OpenGuildStoneGump(item);
                break;
            case ItemType.Multi:
            case ItemType.MultiCustom:
            case ItemType.SignGump:
                OpenHouseSignGump(item);
                break;

            case ItemType.Deed:
                if (_housingEngine != null)
                {
                    var house = _housingEngine.PlaceHouse(_character, item.BaseId, _character.Position);
                    if (house != null)
                    {
                        SysMessage(ServerMessages.Get("house_placed"));
                        if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                                new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                        {
                            item.Delete();
                        }
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get("house_cant_place"));
                    }
                }
                break;

            // ---- BankBox / VendorBox: anti-cheat reject ----
            // ---- light sources ----
            case ItemType.LightLit:
                item.ItemType = ItemType.LightOut;
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;
            case ItemType.LightOut:
                item.ItemType = ItemType.LightLit;
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;

            // ---- telepad / switch ----
            case ItemType.Telepad:
            {
                var dest = item.MoreP;
                if (dest.X != 0 || dest.Y != 0)
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                }
                break;
            }
            case ItemType.Switch:
                _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                break;

            // ---- beverages ----
            case ItemType.Booze:
                _character.Food = (ushort)Math.Min(_character.Food + 2, 60);
                SysMessage("*hic!*");
                if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                {
                    item.Delete();
                }
                break;

            // ---- musical instruments ----
            case ItemType.Musical:
                RouteSkillTarget(SkillType.Musicianship, item.Uid);
                break;

            // ---- figurine (pet shrink/unshrink) ----
            case ItemType.Figurine:
            {
                uint linkedSerial = item.More1;
                if (linkedSerial != 0 && _world != null)
                {
                    var pet = _world.FindChar(new Serial(linkedSerial));
                    if (pet != null)
                    {
                        pet.MoveTo(_character.Position);
                        _world.PlaceCharacter(pet, _character.Position);
                        item.Delete();
                        SysMessage("Your pet materializes beside you.");
                    }
                    else
                    {
                        SysMessage("The creature is lost.");
                    }
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.MsgFigurineNotyours));
                }
                break;
            }

            // ---- moongate ----
            case ItemType.Moongate:
            {
                var dest = item.MoreP;
                if (dest.X != 0 || dest.Y != 0)
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                    _netState.Send(new PacketEffect(2, 0, 0, 0x3728,
                        _character.X, _character.Y, (short)_character.Z,
                        _character.X, _character.Y, (short)_character.Z,
                        10, 30, true, false));
                }
                else
                {
                    _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item });
                }
                break;
            }

            // ---- training dummies ----
            case ItemType.TrainDummy:
            {
                ushort animId = (ushort)(item.BaseId == 0x1070 || item.BaseId == 0x1074 ? item.BaseId + 1 : item.BaseId);
                _netState.Send(new PacketSound(0x03B5, item.X, item.Y, item.Z));
                var skill = _character.GetEquippedItem(Layer.TwoHanded) != null
                    ? SkillType.Swordsmanship
                    : SkillType.Wrestling;
                RouteSkillTarget(skill, item.Uid);
                break;
            }
            case ItemType.TrainPickpocket:
                RouteSkillTarget(SkillType.Stealing, item.Uid);
                break;
            case ItemType.ArcheryButte:
                RouteSkillTarget(SkillType.Archery, item.Uid);
                break;

            // ---- kindling / bedroll / campfire ----
            case ItemType.Kindling:
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Bedroll:
                SysMessage("You lay out the bedroll.");
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Campfire:
                SysMessage("The fire is warm.");
                break;

            // ---- crafting stations (overridable via @DClick trigger) ----
            case ItemType.Loom:
            case ItemType.SpinWheel:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.Anvil:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- beehive / seed / pitcher ----
            case ItemType.BeeHive:
                SysMessage("You reach into the beehive.");
                break;
            case ItemType.Seed:
                SysMessage("Select where to plant the seed.");
                SetPendingTarget((serial, x, y, z, gfx) => { /* planting pending */ });
                break;
            case ItemType.Pitcher:
                UsePotion(item);
                break;
            case ItemType.PitcherEmpty:
                SysMessage("Select a water source to fill the pitcher.");
                SetPendingTarget((serial, x, y, z, gfx) => { /* fill pending */ });
                break;

            // ---- raw materials ----
            case ItemType.Cotton:
            case ItemType.Wool:
            case ItemType.Feather:
            case ItemType.Fur:
                SysMessage("Use a spinning wheel to process this material.");
                break;
            case ItemType.Thread:
            case ItemType.Yarn:
                SysMessage("Use a loom to weave this material.");
                break;
            case ItemType.Log:
            case ItemType.Board:
                SysMessage("Use a carpentry tool to craft with this.");
                break;
            case ItemType.Shaft:
                SysMessage("Use fletching tools to craft with this.");
                break;
            case ItemType.Bone:
                SysMessage("You examine the bone.");
                break;
            case ItemType.Rope:
                SysMessage("You examine the rope.");
                break;

            // ---- food variants ----
            case ItemType.FoodRaw:
            case ItemType.MeatRaw:
                SysMessage("This must be cooked first.");
                break;

            // ---- comm crystal ----
            case ItemType.CommCrystal:
                SysMessage("The crystal hums softly.");
                break;

            // ---- portcullis ----
            case ItemType.Portculis:
            case ItemType.PortLocked:
                ToggleDoor(item);
                break;

            // ---- fletching tool ----
            case ItemType.Fletching:
                OpenCraftingGump(SkillType.Bowcraft);
                break;

            case ItemType.EqBankBox:
            case ItemType.EqVendorBox:
                _logger.LogWarning("Suspicious dclick on bankbox/vendorbox uid={Uid}", item.Uid.Value);
                break;

            default:
                SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
                break;
        }
    }

    // ---- helpers used by HandleItemUse target callbacks ----

    /// <summary>Source-X arrow/bolt presence check before ranged swing.</summary>
    private bool HasAmmoInBackpack(ItemType ammo)
    {
        if (_character?.Backpack == null) return false;
        foreach (var it in _character.Backpack.Contents)
            if (it.ItemType == ammo && it.Amount > 0) return true;
        return false;
    }

    /// <summary>Find a key in the player's backpack that opens a locked container/door.</summary>
    private Item? FindBackpackKeyFor(Item locked)
    {
        if (_character?.Backpack == null) return null;
        uint linkId = locked.Uid.Value;
        foreach (var it in _character.Backpack.Contents)
        {
            if (it.ItemType is not (ItemType.Key or ItemType.Keyring)) continue;
            if (it.TryGetTag("LINK", out string? lk) && uint.TryParse(lk, out uint kv) && kv == linkId)
                return it;
        }
        return null;
    }

    /// <summary>Re-enter the active-skill pipeline with a pre-resolved Serial target.</summary>
    private void RouteSkillTarget(SkillType skill, Serial target, Point3D? point = null)
    {
        if (_character == null) return;
        var obj = target.IsValid ? _world.FindObject(target) : null;
        var sink = new InfoSkillSink(this, _character);
        _skillHandlers?.UseActiveSkill(sink, skill, obj, point);
    }

    /// <summary>Source-X uses scissors to convert hides/cloth to leather/bolts.</summary>
    private void HandleScissorsTarget(Item scissors, Serial target)
    {
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }
        switch (obj.ItemType)
        {
            case ItemType.Hide: obj.ItemType = ItemType.Leather; SysMessage("You cut the hide into leather."); break;
            case ItemType.Cloth: obj.ItemType = ItemType.ClothBolt; SysMessage("You cut the cloth into bolts."); break;
            case ItemType.BandageBlood: obj.Delete(); SysMessage(ServerMessages.Get(Msg.ItemuseBandageClean)); break;
            default: SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); break;
        }
    }

    /// <summary>Source-X key use: link key, lock/unlock door or container.</summary>
    private void HandleKeyUse(Item key, Serial target)
    {
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }

        bool linked = key.TryGetTag("LINK", out string? lk) && uint.TryParse(lk, out uint kv) && kv == obj.Uid.Value;
        if (!linked) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNokey)); return; }

        if (obj.ItemType == ItemType.ContainerLocked) obj.ItemType = ItemType.Container;
        else if (obj.ItemType == ItemType.Container) obj.ItemType = ItemType.ContainerLocked;
        else if (obj.ItemType == ItemType.DoorLocked) obj.ItemType = ItemType.Door;
        else if (obj.ItemType == ItemType.Door) obj.ItemType = ItemType.DoorLocked;
        else { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }
    }

    /// <summary>Pick a hue from a Dye onto a DyeVat (Source-X two-step).</summary>
    private void HandleDyePickup(Item dye, Serial target)
    {
        var vat = target.IsValid ? _world.FindObject(target) as Item : null;
        if (vat == null || vat.ItemType != ItemType.DyeVat)
        { SysMessage(ServerMessages.Get(Msg.ItemuseDyeFail)); return; }
        vat.SetTag("DYE_HUE", dye.Hue.ToString());
        SysMessage("You apply the dye to the vat.");
    }

    /// <summary>Apply a DyeVat hue to a target item.</summary>
    private void HandleDyeApply(Item vat, Serial target)
    {
        var dest = target.IsValid ? _world.FindObject(target) as Item : null;
        if (dest == null) { SysMessage(ServerMessages.Get(Msg.ItemuseDyeReach)); return; }
        if (vat.TryGetTag("DYE_HUE", out string? hueText) && ushort.TryParse(hueText, out ushort hue))
        {
            dest.Hue = new Core.Types.Color(hue);
            SysMessage("The item changes color.");
        }
    }

    /// <summary>Format a Source-X-style local game time string for IT_CLOCK.</summary>
    private static string FormatLocalGameTime()
    {
        var now = DateTime.Now;
        return $"It is {now.Hour:00}:{now.Minute:00}.";
    }

    /// <summary>
    /// Source-X CChar::NPC_OnHearPetCmd parity. Recognises every PC_* verb
    /// from upstream (FOLLOW/GUARD/STAY/STOP/COME/ATTACK/KILL/FRIEND/UNFRIEND/
    /// TRANSFER/RELEASE/DROP/DROP ALL/EQUIP/STATUS/CASH/BOUGHT/SAMPLES/STOCK/
    /// PRICE/GO/SPEAK/GUARD ME/FOLLOW ME) and routes pets through the matching
    /// PetAIMode + DEFMSG_NPC_PET_* output. Returns true when the input was a
    /// pet command -- caller then suppresses normal speech broadcast.
    /// </summary>
    private bool TryHandlePetCommand(string text)
    {
        if (_character == null) return false;
        string lower = text.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');

        // Pet command vocabulary table mirrors sm_Pet_table in Source-X.
        // Order matters because we longest-prefix match (e.g. "follow me" before "follow").
        ReadOnlySpan<string> vocab =
        [
            "all follow", "all guard", "all stay", "all stop", "all come",
            "all attack", "all kill", "all friend", "all unfriend", "all transfer",
            "all release", "all drop all", "all drop", "all equip", "all status",
            "all guard me", "all follow me", "all go", "all speak",
            "follow me", "guard me", "drop all"
        ];

        // "all <verb>" path.
        if (lower.StartsWith("all "))
        {
            string verb = NormalizePetVerb(lower[4..], allMode: true);
            return DispatchAllPets(verb);
        }

        // "<petname> <verb>" path -- longest-match verb.
        int spaceIdx = lower.IndexOf(' ');
        if (spaceIdx <= 0) return false;
        string name = lower[..spaceIdx];
        string rest = NormalizePetVerb(lower[(spaceIdx + 1)..], allMode: false);
        return DispatchNamedPet(name, rest);
    }

    private static string NormalizePetVerb(string rawVerb, bool allMode)
    {
        string verb = rawVerb.Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
        verb = verb switch
        {
            "kills" => "kill",
            "attacks" => "attack",
            "comes" => "come",
            "follows" => "follow",
            _ => verb
        };

        // Source-style shortcut: "all follow" behaves like "all follow me".
        if (allMode && verb == "follow")
            return "follow me";
        return verb;
    }

    /// <summary>Source-X PC_*: target a single pet by name prefix.</summary>
    private bool DispatchNamedPet(string namePrefix, string verb)
    {
        if (_character == null) return false;
        var pet = CollectCommandablePets(namePrefix).FirstOrDefault();
        if (pet == null)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        return ApplyPetVerb(pet, verb);
    }

    /// <summary>Source-X PC_*: broadcast verb to every nearby pet of mine.</summary>
    private bool DispatchAllPets(string verb)
    {
        if (_character == null) return false;
        var pets = CollectCommandablePets().ToList();
        if (pets.Count == 0)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        if (IsPetTargetVerb(verb))
        {
            EmitPetTargetPrompt(pets, verb);
            return true;
        }

        bool any = false;
        foreach (var pet in pets)
            if (ApplyPetVerb(pet, verb)) any = true;
        return any;
    }

    private static bool IsPetTargetVerb(string verb) => verb switch
    {
        "attack" or "kill" or "guard" or "follow" or "go" or
        "friend" or "unfriend" or "transfer" or "release" or
        "price" or "bought" or "samples" or "stock" or "cash" => true,
        _ => false
    };

    /// <summary>
    /// Apply a Source-X PC_* verb to a single pet, emitting the matching
    /// DEFMSG_NPC_PET_* message. Verbs that need a target store a pending
    /// callback so the next click resolves.
    /// </summary>
    private bool ApplyPetVerb(Character pet, string verb)
    {
        if (_character == null) return false;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        // Source-X: conjured/summoned NPCs can't be transferred or friended
        if (pet.IsSummoned && verb is "transfer" or "friend" or "unfriend")
        {
            NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
            return true;
        }

        // Source-X: dead bonded pets accept only passive commands
        if (pet.IsDead)
        {
            bool allowed = verb is "follow me" or "come" or "stay" or "stop" or "follow";
            if (!allowed)
            {
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                return true;
            }
        }

        switch (verb)
        {
            case "follow me":
                pet.PetAIMode = PetAIMode.Follow;
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "come":
                pet.PetAIMode = PetAIMode.Come;
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "stay":
            case "stop":
                pet.PetAIMode = PetAIMode.Stay;
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "guard me":
                pet.PetAIMode = PetAIMode.Guard;
                pet.SetTag("GUARD_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "speak":
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop all":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "equip":
                if (pet.Backpack == null)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                bool equippedAny = false;
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    Layer layer = ResolveWearableLayer(carried);
                    if (layer == Layer.None || pet.GetEquippedItem(layer) != null)
                        continue;
                    pet.Backpack.RemoveItem(carried);
                    pet.Equip(carried, layer);
                    equippedAny = true;
                }
                NpcSpeech(pet, ServerMessages.Get(equippedAny ? Msg.NpcPetSuccess : Msg.NpcPetFailure));
                return true;

            case "status":
                if (pet.TryGetTag("HIRE_DAYS_LEFT", out string? days))
                    NpcSpeech(pet, ServerMessages.GetFormatted(Msg.NpcPetDaysLeft, days ?? "0"));
                else
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetEmployed));
                return true;

            case "attack":
            case "kill":
            case "guard":
            case "follow":
            case "go":
            case "friend":
            case "unfriend":
            case "transfer":
            case "release":
            case "price":
            case "bought":
            case "samples":
            case "stock":
            case "cash":
                EmitPetTargetPrompt(pet, verb);
                return true;

            default:
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetConfused));
                return false;
        }
    }

    /// <summary>
    /// Source-X verbs that need a target open the cursor with the matching
    /// DEFMSG_NPC_PET_TARG_* prompt. The follow-up click is wired into
    /// ApplyPetTarget().
    /// </summary>
    private void EmitPetTargetPrompt(Character pet, string verb)
    {
        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard"            => Msg.NpcPetTargGuard,
            "follow"           => Msg.NpcPetTargFollow,
            "friend"           => Msg.NpcPetTargFriend,
            "unfriend"         => Msg.NpcPetTargUnfriend,
            "transfer"         => Msg.NpcPetTargTransfer,
            "go"               => Msg.NpcPetTargGo,
            "price"            => Msg.NpcPetSetprice,
            _                  => Msg.NpcPetSuccess,
        };
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget(
            (serial, x, y, z, gfx) => ApplyPetTarget(pet, verb, new Serial(serial), x, y, z),
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    private void EmitPetTargetPrompt(IReadOnlyList<Character> pets, string verb)
    {
        if (pets.Count == 0)
            return;

        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard" => Msg.NpcPetTargGuard,
            "follow" => Msg.NpcPetTargFollow,
            "friend" => Msg.NpcPetTargFriend,
            "unfriend" => Msg.NpcPetTargUnfriend,
            "transfer" => Msg.NpcPetTargTransfer,
            "go" => Msg.NpcPetTargGo,
            "price" => Msg.NpcPetSetprice,
            _ => Msg.NpcPetSuccess,
        };

        var petUids = pets.Select(p => p.Uid).ToList();
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget((serial, x, y, z, gfx) =>
            {
                foreach (var petUid in petUids)
                {
                    var pet = _world.FindChar(petUid);
                    if (pet == null || pet.IsDeleted || pet.IsDead || _character == null ||
                        !pet.CanAcceptPetCommandFrom(_character))
                    {
                        continue;
                    }

                    ApplyPetTarget(pet, verb, new Serial(serial), x, y, z);
                }
            },
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    /// <summary>Resolve a target picked after EmitPetTargetPrompt and apply the verb.</summary>
    private void ApplyPetTarget(Character pet, string verb, Serial uid, short x, short y, sbyte z)
    {
        if (_character == null) return;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return;
        }

        var obj = uid.IsValid ? _world.FindObject(uid) : null;

        switch (verb)
        {
            case "attack":
            case "kill":
                if (obj is Character victim && victim != pet &&
                    !victim.IsDead && !victim.IsStatFlag(StatFlag.Invul) &&
                    !victim.IsStatFlag(StatFlag.Ridden))
                {
                    pet.SetTag("ATTACK_TARGET", victim.Uid.Value.ToString());
                    pet.FightTarget = victim.Uid;
                    pet.PetAIMode = PetAIMode.Attack;
                    OnWakeNpc?.Invoke(pet);
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "guard":
                if (obj is Character guarded)
                {
                    pet.SetTag("GUARD_TARGET", guarded.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Guard;
                    SysMessage(ServerMessages.Get(Msg.NpcPetTargGuardSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "follow":
                if (obj is Character followee)
                {
                    pet.SetTag("FOLLOW_TARGET", followee.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Follow;
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "friend":
                if (obj is Character friend && friend.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendSummoned));
                    }
                    else if (pet.IsFriendOf(friend.Uid))
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendAlready));
                    else
                    {
                        pet.AddFriend(friend);
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess1, friend.Name));
                        if (friend != _character)
                            SendToChar?.Invoke(friend.Uid, new PacketSpeechUnicodeOut(
                                0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System",
                                ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, pet.Name)));
                    }
                }
                break;

            case "unfriend":
                if (obj is Character unfriend && pet.IsFriendOf(unfriend.Uid))
                {
                    pet.RemoveFriend(unfriend);
                    SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargUnfriendSuccess1, unfriend.Name));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetTargUnfriendNotfriend));
                break;

            case "transfer":
                if (obj is Character newOwner && newOwner.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargTransferSummoned));
                    }
                    else if (pet.TryAssignOwnership(newOwner, newOwner, summoned: false, enforceFollowerCap: true))
                    {
                        pet.PetAIMode = PetAIMode.Follow;
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, newOwner.Name));
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                    }
                }
                break;

            case "release":
                if (obj is Character releaseOwner && pet.HasOwner(releaseOwner.Uid))
                {
                    pet.ClearOwnership(clearFriends: true);
                    pet.PetAIMode = PetAIMode.Stay;
                    pet.RemoveTag("ATTACK_TARGET");
                    pet.RemoveTag("GUARD_TARGET");
                    pet.RemoveTag("FOLLOW_TARGET");
                    pet.RemoveTag("GO_TARGET");
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "go":
                pet.SetTag("GO_TARGET", $"{x},{y},{z},{_character.MapIndex}");
                pet.PetAIMode = PetAIMode.Come;
                SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                break;

            case "price":
                if (obj is Item priced)
                {
                    priced.SetTag("PRICE", priced.Price > 0 ? priced.Price.ToString() : "1");
                    SendInputPromptGump(priced, "PRICE", 9);
                }
                break;
        }
    }

    private Layer ResolveWearableLayer(Item item)
    {
        var itemDef = DefinitionLoader.GetItemDef(item.BaseId);
        Layer layer = itemDef?.Layer ?? Layer.None;
        if (layer == Layer.None && _world.MapData != null)
        {
            var tile = _world.MapData.GetItemTileData(item.BaseId);
            if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
            {
                layer = (Layer)tile.Quality;
            }
        }
        return layer;
    }

    private IEnumerable<Character> CollectCommandablePets(string? namePrefix = null)
    {
        if (_character == null)
            return Enumerable.Empty<Character>();

        return _world.GetCharsInRange(_character.Position, 12)
            .Where(p =>
                !p.IsPlayer &&
                !p.IsDead &&
                !p.IsDeleted &&
                !p.IsStatFlag(StatFlag.Ridden) &&
                p.CanAcceptPetCommandFrom(_character) &&
                (string.IsNullOrEmpty(namePrefix) ||
                 p.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)));
    }

    private void HandleVendorInteraction(Character vendor)
    {
        if (_character == null) return;

        // Build a buy/sell gump for the vendor
        var gump = new GumpBuilder(_character.Uid.Value, vendor.Uid.Value, 400, 300);
        gump.AddResizePic(0, 0, 5054, 400, 300);
        gump.AddText(30, 20, 0, vendor.GetName());
        gump.AddText(30, 50, 0, "How may I help you?");
        gump.AddButton(30, 100, 4005, 4007, 1);  // Buy
        gump.AddText(70, 100, 0, "Buy");
        gump.AddButton(30, 130, 4005, 4007, 2);  // Sell
        gump.AddText(70, 130, 0, "Sell");
        gump.AddButton(150, 250, 4017, 4019, 0); // Cancel

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (buttonId == 1)
                SendVendorBuyList(vendor);
            else if (buttonId == 2)
                SendVendorSellList(vendor);
        });
    }

    /// <summary>
    /// Source-X CClient::Cmd_VendorBuy parity. Public entry used when the
    /// player triggers buy via speech ("vendor buy", "buy") or by clicking
    /// the buy gump button. Wraps the private packet-formatting helper so
    /// callers outside this client (e.g. NPC speech dispatch in Program.cs)
    /// don't need to poke private members.
    /// </summary>
    public void OpenVendorBuy(Character vendor) => SendVendorBuyList(vendor);

    /// <summary>
    /// Source-X CClient::Cmd_VendorSell parity. Public entry used when the
    /// player triggers sell via speech or via the vendor gump button.
    /// </summary>
    public void OpenVendorSell(Character vendor) => SendVendorSellList(vendor);

    /// <summary>Send the vendor's buy list (items available for purchase) to the client.</summary>
    private void SendVendorBuyList(Character vendor)
    {
        if (_character == null) return;

        // Auto-restock if needed (TAG.VENDORINV path — used by GM-set
        // inventory definitions).
        if (VendorEngine.NeedsRestock(vendor))
            VendorEngine.RestockVendor(vendor);

        // Source-X parity: vendors restock from their @NPCRestock
        // trigger (SELL=VENDOR_S_*, BUY=VENDOR_B_*) when their stock
        // pack is empty. The spawn-time hook fires this on freshly
        // spawned NPCs, but vendors that were loaded from a prior
        // world save never went through that path. Re-fire on demand
        // so legacy persisted vendors get a stock list as soon as a
        // player tries to buy from them.
        // Vendor's stock lives on LAYER_VENDOR_STOCK (26). ClassicUO's
        // BuyList handler hard-rejects any other layer (Backpack = 21
        // is silently dropped), so we MUST source / reference the
        // dedicated vendor stock container.
        var stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
        if (stockContainer == null ||
            !_world.GetContainerContents(stockContainer.Uid).Any())
        {
            _triggerDispatcher?.FireCharTrigger(vendor,
                SphereNet.Core.Enums.CharTrigger.NPCRestock,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = _character });
            // Refresh after restock — the trigger may have created it.
            stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
        }

        // Collect vendor inventory items (items in vendor's "sell" container / buy pack)
        var vendorItems = GetVendorBuyInventory(vendor);
        if (vendorItems.Count == 0 || stockContainer == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_no_goods"));
            return;
        }

        // Source-X / RunUO order (CClient::addVendorBuy):
        //   1) 0x2E equip the vendor stock container at LAYER_VENDOR_STOCK
        //      (=ClassicUO Layer.ShopBuyRestock 0x1A) so the client knows
        //      the entity exists.
        //   2) 0x3C container contents — every item that the buy list will
        //      reference. The client uses these entries to look up
        //      itemId/hue/amount when drawing each row of the buy window;
        //      without it the rows are blank.
        //   3) 0x74 vendor buy list — prices + descriptions. ClassicUO's
        //      BuyList(0x74) handler ONLY decorates the items with prices
        //      and display names; it does NOT push them into the
        //      ShopGump's display list. (See ShopGump.Update —
        //      `if (_shopItems.Count == 0) Dispose()` will close the
        //      gump after one frame if nothing was added.)
        //   4) 0x24 OpenContainer with gumpId=0x0030 + VENDOR MOBILE serial
        //      — THIS is what actually opens and populates the buy gump.
        //      The client's OpenContainer handler iterates
        //      vendor.FindItemByLayer(Layer.ShopBuyRestock..ShopBuy) and
        //      calls ShopGump.AddItem for every child item. Skipping this
        //      step is exactly why our buy menu used to "vanish" — the
        //      gump did spawn briefly and then auto-disposed because
        //      `_shopItems` stayed empty.
        var buyPack = stockContainer;
        uint buyContainerSerial = buyPack.Uid.Value;

        // (0) PRE-SYNC the vendor stock container as a worn item.
        //     Equipping at LAYER_VENDOR_STOCK (26 == ClassicUO
        //     Layer.ShopBuyRestock 0x1A) is mandatory: ClassicUO's
        //     BuyList(0x74) handler explicitly checks
        //     `container.Layer == Layer.ShopBuyRestock || == Layer.ShopBuy`
        //     and silently bails out for any other layer (including
        //     Backpack = 0x15).
        _netState.Send(new PacketWornItem(
            buyPack.Uid.Value, buyPack.BaseId, (byte)Layer.VendorStock,
            vendor.Uid.Value, buyPack.Hue.Value));

        // (0b) ALSO equip a container at LAYER_VENDOR_EXTRA (27 ==
        //      ClassicUO Layer.ShopBuy 0x1B). ClassicUO's OpenContainer
        //      handler for gump 0x0030 unconditionally iterates BOTH
        //      ShopBuyRestock and ShopBuy layers and calls `item.Items`
        //      on each — without a NULL-check. If the second layer is
        //      empty, `vendor.FindItemByLayer(Layer.ShopBuy)` returns
        //      null and the client CRASHES with NullReferenceException
        //      the moment we send our 0x24 to open the buy gump.
        //      Source-X NPCs always have both stock containers (LAYER
        //      26 + LAYER 27) for exactly this reason; we lazily mint
        //      the second one here so legacy / freshly-spawned vendors
        //      don't crash the client.
        var extraContainer = vendor.GetEquippedItem(Layer.VendorExtra);
        if (extraContainer == null)
        {
            extraContainer = _world.CreateItem();
            extraContainer.BaseId = 0x408D; // i_vendor_box (Source-X stock graphic)
            vendor.Equip(extraContainer, Layer.VendorExtra);
        }
        _netState.Send(new PacketWornItem(
            extraContainer.Uid.Value, extraContainer.BaseId, (byte)Layer.VendorExtra,
            vendor.Uid.Value, extraContainer.Hue.Value));

        var contentEntries = new List<PacketContainerContents.Entry>(vendorItems.Count);
        for (int i = 0; i < vendorItems.Count; i++)
        {
            var vi = vendorItems[i];
            // Cascade items inside the buy pack so the client can render
            // distinct rows. Five-wide grid matches Source-X / RunUO layout.
            short x = (short)(20 + (i % 5) * 30);
            short y = (short)(20 + (i / 5) * 20);
            contentEntries.Add(new PacketContainerContents.Entry(
                vi.Serial, vi.ItemId, 0, vi.Amount,
                x, y, buyContainerSerial, vi.Hue, (byte)i));
        }
        _netState.Send(new PacketContainerContents(contentEntries, _netState.IsClientPost6017));
        _netState.Send(new PacketVendorBuyList(buyContainerSerial, vendorItems));

        // (4) Open the buy gump. ClassicUO's OpenContainer handler with
        //     gumpId=0x0030 walks vendor.FindItemByLayer(Layer.ShopBuyRestock
        //     .. Layer.ShopBuy), pulls every child item out, and calls
        //     ShopGump.AddItem. Without this packet, the gump that BuyList
        //     creates auto-disposes one frame later because its
        //     `_shopItems` dictionary is empty (see ShopGump.Update).
        //     Note: the serial here is the VENDOR MOBILE — not the
        //     container — because the handler does
        //     `World.Mobiles.Get(serial)`.
        _netState.Send(new PacketOpenContainer(vendor.Uid.Value, 0x0030,
            _netState.IsClientPost7090));
    }

    /// <summary>Send the sell list (items player can sell to this vendor) to the client.</summary>
    private void SendVendorSellList(Character vendor)
    {
        if (_character == null) return;

        var backpack = _character.Backpack;
        if (backpack == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        // Build list of items the vendor will buy from the player's backpack
        var sellItems = new List<VendorItem>();
        foreach (var item in _world.GetContainerContents(backpack.Uid))
        {
            if (item.ItemType == ItemType.Gold) continue; // don't sell gold
            if (item.IsDeleted) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            if (price <= 0) continue;

            sellItems.Add(new VendorItem
            {
                Serial = item.Uid.Value,
                ItemId = item.DispIdFull,
                Hue = item.Hue.Value,
                Amount = (ushort)item.Amount,
                Price = price,
                Name = item.GetName()
            });

            if (sellItems.Count >= 50) break; // limit
        }

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        _netState.Send(new PacketVendorSellList(vendor.Uid.Value, sellItems));
    }

    /// <summary>
    /// Build vendor buy inventory from vendor's TAG.SELL entries or equipped buy-pack items.
    /// In Sphere, vendor inventory is defined in CHARDEF with item entries.
    /// </summary>
    private List<VendorItem> GetVendorBuyInventory(Character vendor)
    {
        var items = new List<VendorItem>();

        // Items live on LAYER_VENDOR_STOCK (Source-X parity). ClassicUO
        // BuyList(0x74) only accepts containers equipped at that layer
        // (or LAYER_VENDOR_EXTRA = 27) — Backpack-based stock is dropped.
        var vendorPack = vendor.GetEquippedItem(Layer.VendorStock)
                         ?? vendor.GetEquippedItem(Layer.VendorExtra);
        if (vendorPack != null)
        {
            foreach (var item in _world.GetContainerContents(vendorPack.Uid))
            {
                if (item.IsDeleted) continue;

                int price = GetVendorItemPrice(vendor, item);
                items.Add(new VendorItem
                {
                    Serial = item.Uid.Value,
                    ItemId = item.DispIdFull,
                    Hue = item.Hue.Value,
                    Amount = Math.Max((ushort)1, (ushort)item.Amount),
                    Price = price,
                    Name = item.GetName()
                });

                if (items.Count >= 50) break;
            }
        }

        return items;
    }

    // ==================== Crafting Gump ====================
}

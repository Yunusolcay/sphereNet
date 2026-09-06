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

/// <summary>
/// World-features handler extracted from the GameClient.WorldFeatures partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Crafting gump, vendor buy/sell, secure trade, rename, guild/house gumps,
/// doors, potions, skill use, 0xBF extended-command dispatch, party commands,
/// context menus. Method bodies moved verbatim; the private context shims
/// below enumerate exactly what this handler needs from GameClient.
/// </summary>
public sealed class ClientWorldFeaturesHandler
{
    private readonly IClientContext _client;

    internal ClientWorldFeaturesHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private HousingEngine? _housingEngine => _client.Housing;
    private TradeManager? _tradeManager => _client.TradeM;
    private CraftingEngine? _craftingEngine => _client.CraftE;
    private GuildManager? _guildManager => _client.GuildM;
    private PartyManager? _partyManager => _client.PartyM;
    private SkillHandlers? _skillHandlers => _client.SkillH;
    private ClientTargetState Targets => _client.Targets;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private const int UpdateRange = GameClient.UpdateRange;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint, Character>? BroadcastMoveNearby => _client.BroadcastMoveNearby;
    private Action<Character>? BroadcastCharacterAppear => _client.BroadcastCharacterAppear;
    private Action<Serial, SphereNet.Network.Packets.PacketWriter>? SendToChar => _client.SendToChar;
    private Action<Character, Character, Item, Item>? SendTradeToPartner => _client.SendTradeToPartner;
    private Action<Character, Item, Item>? SendTradeItemToPartner => _client.SendTradeItemToPartner;
    private Action<Character, uint>? SendTradeCloseToPartner => _client.SendTradeCloseToPartner;
    private Action<Character, SecureTrade>? SendTradeUpdateToPartner => _client.SendTradeUpdateToPartner;
    private Action<Character, string>? SendTradeMessageToPartner => _client.SendTradeMessageToPartner;
    private Action<Character>? RefreshBackpackForPartner => _client.RefreshBackpackForPartner;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) => _client.SetPendingTarget(callback, cursorType);
    private void NpcSpeech(Character npc, string text) => _client.NpcSpeech(npc, text);
    private void RefreshBackpackContents() => _client.RefreshBackpackContents();
    private void SendCharacterStatus(Character ch) => _client.SendCharacterStatus(ch);
    private SphereNet.Network.Packets.PacketWriter BuildWorldItemPacket(uint serial, ushort itemId, ushort amount, short x, short y, sbyte z, ushort hue, byte direction = 0) => _client.BuildWorldItemPacket(serial, itemId, amount, x, y, z, hue, direction);
    private void SendPaperdoll(Character ch) => _client.SendPaperdoll(ch);
    private void SendOpenContainer(Item container) => _client.SendOpenContainer(container);
    private void HandleVendorInteraction(Character vendor) => _client.HandleVendorInteraction(vendor);
    private void OpenBankBox() => _client.OpenBankBox();
    private void HandleDoubleClick(uint uid) => _client.HandleDoubleClick(uid);
    private Character? DismountCharacter() => _client.DismountCharacter();
    private void BroadcastDeleteObject(uint uid) => _client.BroadcastDeleteObject(uid);
    private void ResetWalkValidator() => _client.ResetWalkValidator();
    private static byte BuildMobileFlags(Character ch) => GameClient.BuildMobileFlags(ch);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private void BeginInfoSkill(SkillType skill, int skillId) => _client.BeginInfoSkill(skill, skillId);
    private void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind) => _client.BeginActiveSkill(skill, skillId, kind);
    private void BeginTargetedSkill(SkillType skill, int skillId, Serial targetUid) => _client.BeginTargetedSkill(skill, skillId, targetUid);
    private void BeginHouseCustomization(Item multi) => _client.BeginHouseCustomization(multi);
    private void HandleQueryDesignDetails(byte[] data) => _client.HandleQueryDesignDetails(data);
    private void HandleChatOpen() => _client.HandleChatOpen();

    private static readonly IReadOnlyDictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>> s_extendedCommandHandlers =
        BuildExtendedCommandHandlers();


    /// <summary>
    /// Open a crafting gump for the given skill.
    /// Lists available recipes and lets the player select one to craft.
    /// </summary>
    /// <summary>Recipes shown per craft-menu page (rows that fit between y=50..390).</summary>
    private const int CraftRecipesPerPage = 15;
    private const int CraftButtonNextPage = 1;
    private const int CraftButtonPrevPage = 2;
    private const int CraftButtonRecipeBase = 100;
    private const uint CraftMaterialGumpId = 0x43524D54; // "CRMT"
    private const int CraftMaterialsPerPage = 12;
    private const int CraftMaterialButtonNextPage = 1;
    private const int CraftMaterialButtonPrevPage = 2;
    private const int CraftMaterialButtonBack = 3;
    private const int CraftMaterialButtonBase = 100;

    public void OpenCraftingGump(SkillType craftSkill) => OpenCraftingGump(craftSkill, 0, fireMenuTrigger: true);

    private void OpenCraftingGump(SkillType craftSkill, int page, bool fireMenuTrigger = false)
    {
        if (_character == null || _craftingEngine == null) return;
        if (!SkillHandlers.CanUse(_character, craftSkill)) return;

        if (fireMenuTrigger &&
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMenu,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True)
            return;

        var recipes = _craftingEngine.GetRecipesBySkill(craftSkill);
        if (recipes.Count == 0)
        {
            SysMessage(ServerMessages.Get("craft_no_recipes"));
            return;
        }

        // Clamp the page so a stale Next/Prev (or a recipe-count change) can't land
        // on an empty page — long recipe lists are paged instead of truncated, so
        // every recipe is reachable (the old single page cut off at y>390).
        int pageCount = (recipes.Count + CraftRecipesPerPage - 1) / CraftRecipesPerPage;
        page = Math.Clamp(page, 0, Math.Max(0, pageCount - 1));
        int skip = page * CraftRecipesPerPage;

        var gump = new GumpBuilder(_character.Uid.Value, 0, 530, 437);
        gump.AddResizePic(0, 0, 5054, 530, 437);
        gump.AddText(15, 15, 0, $"{craftSkill} Menu (page {page + 1}/{pageCount})");

        int y = 50;
        for (int i = skip; i < recipes.Count && i < skip + CraftRecipesPerPage; i++)
        {
            var recipe = recipes[i];
            string name = string.IsNullOrEmpty(recipe.ResultName)
                ? $"Item 0x{recipe.ResultItemId:X4}"
                : recipe.ResultName;
            bool canMake = _craftingEngine.CanCraft(_character, recipe);
            int hue = canMake ? 0x0044 : 0x0020; // green vs red

            gump.AddButton(15, y, 4005, 4007, CraftButtonRecipeBase + (i - skip));
            gump.AddText(55, y, hue, name);

            if (recipe.Resources.Count > 0)
            {
                var resText = string.Join(", ", recipe.Resources.Select(r =>
                    r.Type.HasValue ? $"{r.Amount}x {r.Type.Value}" : $"{r.Amount}x 0x{r.ItemId:X4}"));
                gump.AddText(280, y, 0, resText);
            }

            y += 22;
        }

        // Prev / Next paging buttons
        if (page > 0)
        {
            gump.AddButton(200, 400, 4014, 4016, CraftButtonPrevPage);
            gump.AddText(240, 400, 0, "Prev");
        }
        if (page < pageCount - 1)
        {
            gump.AddButton(320, 400, 4005, 4007, CraftButtonNextPage);
            gump.AddText(360, 400, 0, "Next");
        }

        gump.AddButton(15, 400, 4017, 4019, 0);
        gump.AddText(55, 400, 0, "Close");

        int capturedSkip = skip;
        int capturedPage = page;
        SendGump(gump, (pressedButton, switches, textEntries) =>
        {
            if (pressedButton == CraftButtonNextPage)
                OpenCraftingGump(craftSkill, capturedPage + 1, fireMenuTrigger: false);
            else if (pressedButton == CraftButtonPrevPage)
                OpenCraftingGump(craftSkill, capturedPage - 1, fireMenuTrigger: false);
            else if (pressedButton >= CraftButtonRecipeBase)
            {
                int index = capturedSkip + ((int)pressedButton - CraftButtonRecipeBase);
                if (index < recipes.Count)
                {
                    var recipe = recipes[index];
                    var materials = _craftingEngine.GetPrimaryResourceOptions(_character, recipe);
                    if (materials.Count > 1)
                        OpenCraftMaterialGump(recipe, craftSkill, materials, 0);
                    else
                        BeginPendingCraft(recipe, craftSkill, reopenGump: true,
                            materials.Count == 1 ? materials[0].Hue : null);
                }
            }
        });
    }

    private void OpenCraftMaterialGump(CraftRecipe recipe, SkillType craftSkill,
        IReadOnlyList<CraftMaterialOption> materials, int page)
    {
        if (_character == null || _craftingEngine == null || materials.Count == 0)
            return;

        int pageCount = (materials.Count + CraftMaterialsPerPage - 1) / CraftMaterialsPerPage;
        page = Math.Clamp(page, 0, pageCount - 1);
        int skip = page * CraftMaterialsPerPage;
        var gump = new GumpBuilder(_character.Uid.Value, CraftMaterialGumpId, 430, 390);
        gump.AddResizePic(0, 0, 5054, 430, 390);
        gump.AddText(20, 15, 0, $"Select material (page {page + 1}/{pageCount})");

        int y = 48;
        for (int i = skip; i < materials.Count && i < skip + CraftMaterialsPerPage; i++)
        {
            var material = materials[i];
            gump.AddButton(15, y, 4005, 4007, CraftMaterialButtonBase + (i - skip));
            if (material.DisplayId != 0)
                gump.AddTilePicHue(48, y - 3, material.DisplayId, material.Hue);
            gump.AddCroppedText(90, y, 220, 20, 0,
                $"{material.Name} ({material.Available})");
            gump.AddText(315, y, 0, $"0x{material.Hue:X4}");
            y += 25;
        }

        if (page > 0)
            gump.AddButton(175, 350, 4014, 4016, CraftMaterialButtonPrevPage);
        if (page < pageCount - 1)
            gump.AddButton(285, 350, 4005, 4007, CraftMaterialButtonNextPage);
        gump.AddButton(15, 350, 4014, 4016, CraftMaterialButtonBack);
        gump.AddText(55, 350, 0, "Back");

        int capturedPage = page;
        int capturedSkip = skip;
        SendGump(gump, (pressedButton, _, _) =>
        {
            if (pressedButton == CraftMaterialButtonNextPage)
                OpenCraftMaterialGump(recipe, craftSkill, materials, capturedPage + 1);
            else if (pressedButton == CraftMaterialButtonPrevPage)
                OpenCraftMaterialGump(recipe, craftSkill, materials, capturedPage - 1);
            else if (pressedButton == CraftMaterialButtonBack)
                OpenCraftingGump(craftSkill, 0, fireMenuTrigger: false);
            else if (pressedButton >= CraftMaterialButtonBase)
            {
                int index = capturedSkip + ((int)pressedButton - CraftMaterialButtonBase);
                if (index < materials.Count)
                    BeginPendingCraft(recipe, craftSkill, reopenGump: true, materials[index].Hue);
            }
        });
    }

    // --- multi-stroke crafting (reference Skill_Stroke) ----------------------
    private CraftRecipe? _pendingCraftRecipe;
    private SkillType _pendingCraftSkill;
    private int _pendingCraftStrokes;
    private long _pendingCraftNextStroke;
    private bool _pendingCraftReopenGump;
    private ushort? _pendingCraftResourceHue;
    private Point3D _pendingCraftStartPosition;

    /// <summary>Start a craft as a stroke loop (reference Skill_MakeItem →
    /// Skill_Stroke): two work strokes with the skill's DELAY-curve timeout
    /// (one-second floor), anim + sound per stroke; the roll and resource
    /// consumption happen at completion, after a CanCraft re-check (covers
    /// walking away from the forge mid-craft).</summary>
    internal bool BeginPendingCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump,
        ushort? primaryResourceHue = null)
    {
        if (_character == null || _craftingEngine == null)
            return false;
        if (!SkillHandlers.CanUse(_character, craftSkill))
            return false;
        if (_character.IsCasting || _character.HasActiveSkillPending())
        {
            SysMessage("You must wait to perform another action.");
            return false;
        }
        if (_pendingCraftRecipe != null)
        {
            SysMessage(ServerMessages.Get("craft_busy"));
            return false;
        }
        if (!_craftingEngine.CanCraft(_character, recipe, primaryResourceHue))
        {
            SysMessage(ServerMessages.Get("craft_fail"));
            return false;
        }

        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillSelect,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True ||
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True ||
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill }) == TriggerResult.True)
            return false;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMakeItem,
            new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });

        _pendingCraftRecipe = recipe;
        _pendingCraftSkill = craftSkill;
        _pendingCraftStrokes = 2;
        _pendingCraftReopenGump = reopenGump;
        _pendingCraftResourceHue = primaryResourceHue;
        _pendingCraftStartPosition = _character.Position;
        EmitCraftStroke(craftSkill);
        _pendingCraftNextStroke = Environment.TickCount64 + GetCraftStrokeIntervalMs(craftSkill);
        return true;
    }

    /// <summary>Advance the pending craft stroke loop. Called from the
    /// per-client tick pump.</summary>
    internal void TickPendingCraft()
    {
        if (_pendingCraftRecipe == null || _character == null || _craftingEngine == null)
            return;
        if (_character.IsDead || _character.IsDeleted ||
            (SkillEngine.HasFlag(_pendingCraftSkill, SkillFlag.Immobile) &&
             _character.Position != _pendingCraftStartPosition))
        {
            CancelPendingCraft();
            return;
        }
        if (Environment.TickCount64 < _pendingCraftNextStroke)
            return;

        // Source-X Skill_Stroke: @SkillStroke fires per stroke with
        // LOCAL.Strokes seeded to the remaining count and WRITABLE — a
        // script can lengthen or shorten the craft per recipe (the default
        // stays the reference's fixed 2); RETURN 1 aborts the craft.
        if (_triggerDispatcher != null)
        {
            var strokeLocals = new SphereNet.Scripting.Variables.VarMap();
            strokeLocals.SetInt("Strokes", _pendingCraftStrokes);
            var strokeArgs = new TriggerArgs
            {
                CharSrc = _character,
                N1 = (int)_pendingCraftSkill,
                Locals = strokeLocals,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStroke,
                    strokeArgs) == TriggerResult.True)
            {
                CancelPendingCraft();
                return;
            }
            long rewrittenStrokes = strokeLocals.GetInt("Strokes", _pendingCraftStrokes);
            if (rewrittenStrokes != _pendingCraftStrokes && rewrittenStrokes >= 0)
                _pendingCraftStrokes = (int)Math.Min(rewrittenStrokes, 100);
        }

        _pendingCraftStrokes--;
        if (_pendingCraftStrokes > 0)
        {
            EmitCraftStroke(_pendingCraftSkill);
            _pendingCraftNextStroke = Environment.TickCount64 + GetCraftStrokeIntervalMs(_pendingCraftSkill);
            return;
        }

        var recipe = _pendingCraftRecipe;
        var craftSkill = _pendingCraftSkill;
        bool reopen = _pendingCraftReopenGump;
        ushort? primaryResourceHue = _pendingCraftResourceHue;
        _pendingCraftRecipe = null;
        _pendingCraftResourceHue = null;

        // Re-check at completion (reference SKTRIG_SUCCESS re-validates the
        // work site): walking away from the forge or losing materials
        // mid-craft fails without the roll.
        if (!_craftingEngine.CanCraft(_character, recipe, primaryResourceHue))
        {
            SysMessage(ServerMessages.Get("craft_fail"));
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillFail,
                new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });
            if (reopen)
                OpenCraftingGump(craftSkill, 0, fireMenuTrigger: false);
            return;
        }

        CompleteCraft(recipe, craftSkill, reopen, primaryResourceHue);
    }

    private void CancelPendingCraft(bool notify = true)
    {
        if (_pendingCraftRecipe == null)
            return;
        int skillId = (int)_pendingCraftSkill;
        _pendingCraftRecipe = null;
        _pendingCraftStrokes = 0;
        _pendingCraftNextStroke = 0;
        _pendingCraftResourceHue = null;
        _triggerDispatcher?.FireCharTrigger(_character!, CharTrigger.SkillAbort,
            new TriggerArgs { CharSrc = _character, N1 = skillId });
        if (notify)
            SysMessage("You stop what you were doing.");
    }

    internal void CancelPendingCraftOnInterrupt() => CancelPendingCraft();
    internal void CancelPendingCraftOnDisconnect() => CancelPendingCraft(notify: false);

    private void EmitCraftStroke(SkillType craftSkill)
    {
        if (_character == null)
            return;
        int stroke = 3 - _pendingCraftStrokes;
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill, N2 = stroke });
        var (craftAnim, craftSound) = GetCraftAnimAndSound(craftSkill);
        if (!SkillEngine.HasFlag(craftSkill, SkillFlag.NoAnim))
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketAnimation(_character.Uid.Value, craftAnim), 0);
        if (!SkillEngine.HasFlag(craftSkill, SkillFlag.NoSfx))
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketSound(craftSound, _character.X, _character.Y, _character.Z), 0);
    }

    private int GetCraftStrokeIntervalMs(SkillType craftSkill)
    {
        int delayMs = Skills.SkillEngine.GetSkillDelayMs(craftSkill, _character?.GetSkill(craftSkill) ?? 0);
        return Math.Max(1000, delayMs); // reference floor: 10 tenths per stroke
    }

    private void CompleteCraft(CraftRecipe recipe, SkillType craftSkill, bool reopenGump,
        ushort? primaryResourceHue)
    {
        if (_character == null || _craftingEngine == null)
            return;

        var result = _craftingEngine.TryCraft(_character, recipe, primaryResourceHue);

        if (result != null)
        {
            _triggerDispatcher?.FireItemTrigger(result, ItemTrigger.Create,
                new TriggerArgs { CharSrc = _character, ItemSrc = result });
            string craftedName = result.GetName();
            var pack = _character.Backpack;
            if (pack != null)
            {
                var actual = pack.TryAddItemWithStack(result);
                if (actual == null)
                {
                    _world.PlaceItemWithDecay(result, _character.Position);
                    actual = result;
                }
                if (actual != result)
                    _world.RemoveItem(result);

                if (actual.ContainedIn == pack.Uid)
                    _netState.Send(new PacketContainerItem(
                        actual.Uid.Value, actual.DispIdFull, 0,
                        actual.Amount, actual.X, actual.Y,
                        pack.Uid.Value, actual.Hue,
                        _netState.IsClientPost6017));

            }
            else
            {
                _world.PlaceItemWithDecay(result, _character.Position);
            }
            SysMessage(ServerMessages.GetFormatted("craft_success", craftedName));
        }
        else
            SysMessage(ServerMessages.Get("craft_fail"));

        _triggerDispatcher?.FireCharTrigger(_character,
            result != null ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
            new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });

        if (reopenGump)
            OpenCraftingGump(craftSkill, 0, fireMenuTrigger: false);
    }

    /// <summary>Handle vendor buy packet (0x3B).</summary>
    public void HandleVendorBuy(uint vendorSerial, byte flag,
        List<SphereNet.Network.Packets.Incoming.VendorBuyEntry> buyItems)
    {
        if (_character == null) return;
        // A vendor window outlives the player: they can die with it open, and the
        // client can still send a queued confirmation afterwards. Source-X CanTouch
        // refuses a dead character every item that is not death-immune, so the
        // transaction must not reach the stock at all.
        if (_character.IsDead) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || !VendorEngine.IsVendorLike(vendor)) return;
        if (_character.MapIndex != vendor.MapIndex ||
            _character.Position.GetDistanceTo(vendor.Position) > 3)
            return;

        if (flag == 0 || buyItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Buy trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "BUY" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var bi in buyItems)
        {
            var item = _world.FindItem(new Serial(bi.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = bi.Amount
            });
        }

        // @Buy fires per item BEFORE the purchase; RETURN 1 drops that line.
        entries = FilterVendorEntriesByTrigger(vendor, entries, ItemTrigger.Buy);
        if (entries.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            RefreshBackpackContents();
            return;
        }

        int result = VendorEngine.ProcessBuy(_character, vendor, entries);
        if (result < 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nomoney1"));
        else if (result == 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
        else
            NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_b1", result, result == 1 ? "" : "s"));

        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Handle vendor sell packet (0x9F).</summary>
    public void HandleVendorSell(uint vendorSerial,
        List<SphereNet.Network.Packets.Incoming.VendorSellEntry> sellItems)
    {
        if (_character == null) return;
        // A vendor window outlives the player: they can die with it open, and the
        // client can still send a queued confirmation afterwards. Source-X CanTouch
        // refuses a dead character every item that is not death-immune, so the
        // transaction must not reach the stock at all.
        if (_character.IsDead) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || !VendorEngine.IsVendorLike(vendor)) return;
        if (_character.MapIndex != vendor.MapIndex ||
            _character.Position.GetDistanceTo(vendor.Position) > 3)
            return;

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Sell trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "SELL" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var si in sellItems)
        {
            var item = _world.FindItem(new Serial(si.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = si.Amount
            });
        }

        // @Sell fires per item BEFORE the sale; RETURN 1 drops that line.
        entries = FilterVendorEntriesByTrigger(vendor, entries, ItemTrigger.Sell);
        if (entries.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            RefreshBackpackContents();
            return;
        }

        int result = VendorEngine.ProcessSell(_character, vendor, entries, out bool shortfall);
        // Source-X partial fill: the purse ran dry mid-sale — bark the shortfall
        // so the player knows why only part (or none) of the batch was bought.
        if (shortfall)
            NpcSpeech(vendor, "I cannot afford to buy all of that from thee.");
        if (result > 0 || !shortfall)
            NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_sell_ty", result, result == 1 ? "" : "s"));
        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Get the buy price for an item from vendor inventory. Uses TAG.PRICE or defaults.</summary>
    internal static int GetVendorItemPrice(Character vendor, Item item)
    {
        if (item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int price))
            return price;
        // Itemdef VALUE, like Source-X — never the art tile id.
        return Math.Max(1, SphereNet.Game.Trade.VendorEngine.GetDefValue(item.BaseId));
    }

    /// <summary>Get the sell price (what vendor pays the player) — same
    /// VENDORMARKUP math the server-side check uses, so the displayed list
    /// matches the payout.</summary>
    internal static int GetVendorItemSellPrice(Character vendor, Item item)
    {
        return VendorEngine.GetServerSellPrice(vendor, item);
    }

    /// <summary>Fire the per-item @Buy / @Sell trigger BEFORE the transfer and
    /// return the entries that were NOT cancelled. Source-X runs @Buy/@Sell ahead
    /// of moving the item so RETURN 1 can veto that line (it used to fire after
    /// the trade had already completed, with its return value ignored).
    ///
    /// Argument contract (Source-X CClientEvent.cpp:1288 / :1504):
    /// <c>ARGN1</c> = amount, <c>ARGN2</c> = the LINE TOTAL (amount x price), not
    /// the unit price, and <c>ARGO</c> = the vendor. @Buy additionally carries
    /// <c>LOCAL.TOTALCOST</c>, the running total of the lines still standing, which
    /// a vetoed line is subtracted from.
    ///
    /// Deliberate deviation: Source-X builds ARGN2 from the price in the CLIENT
    /// packet. SphereNet uses the server-resolved price the transaction will
    /// actually charge, so a script cannot be shown a figure the engine disagrees
    /// with. Do not "restore parity" here by reading the client value.</summary>
    private List<TradeEntry> FilterVendorEntriesByTrigger(Character vendor, IReadOnlyList<TradeEntry> entries, ItemTrigger trigger)
    {
        if (_triggerDispatcher == null || _character == null)
            return entries.ToList();

        long runningTotal = 0;
        foreach (var entry in entries)
            runningTotal += (long)entry.Price * entry.Amount;

        var kept = new List<TradeEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var item = _world.FindItem(entry.ItemUid);
            if (item == null) { kept.Add(entry); continue; }

            long lineTotal = (long)entry.Price * entry.Amount;

            var locals = new SphereNet.Scripting.Variables.VarMap();
            if (trigger == ItemTrigger.Buy)
                locals.SetInt("TOTALCOST", (int)Math.Clamp(runningTotal, int.MinValue, int.MaxValue));

            var result = _triggerDispatcher.FireItemTrigger(item, trigger, new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = item,
                O1 = vendor,
                N1 = entry.Amount,
                N2 = (int)Math.Clamp(lineTotal, int.MinValue, int.MaxValue),
                Locals = locals
            });

            if (result == TriggerResult.True)
            {
                // Source-X subtracts a vetoed line from the running total so a later
                // line's LOCAL.TOTALCOST reflects what is still being bought.
                runningTotal -= lineTotal;
                continue;
            }
            kept.Add(entry);
        }
        return kept;
    }

    /// <summary>
    /// Handle secure trade packet (0x6F).
    /// Actions: 0=display, 1=close, 2=update (check/uncheck accept).
    /// </summary>
    public void HandleSecureTrade(byte action, uint containerSerial, uint param)
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindByContainer(containerSerial);
        if (trade == null) return;
        if (!trade.IsParticipant(_character)) return;

        // Source-X receive.cpp:1132 refuses the packet when the named container is
        // not the sender's own (`character != container->GetParent()`). Without it,
        // reading the accept value from the packet would let a client name the
        // PARTNER's container and set the partner's flag - turning a toggle bug into
        // one-sided completion. Now that each window is parented to its owner
        // (CreateTradeContainer) this is a direct comparison.
        if (trade.GetOwnContainer(_character).Uid.Value != containerSerial)
            return;

        switch (action)
        {
            case 1: // Cancel
                CancelTrade(trade);
                break;
            case 2: // Accept / un-accept, per the packet's own flag
            {
                bool bothAccepted = trade.SetAccept(_character, param != 0);
                SendTradeUpdateToBoth(trade);

                if (bothAccepted)
                {
                    if (!TryCompleteTrade(trade))
                    {
                        trade.ResetAcceptance();
                        SendTradeUpdateToBoth(trade);
                    }
                    else
                        return;
                }
                break;
            }
        }
    }

    /// <summary>Cancel active trade on disconnect — return items, notify partner.</summary>
    internal void AbortActiveTradeOnDisconnect()
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindTradeFor(_character);
        if (trade == null) return;

        var partner = trade.GetPartner(_character);
        FinalizeTradeCancel(trade, partner, sendSelfClose: false);
    }

    private bool TryCompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;

        if (!TradeManager.CanAcceptTradeItems(partner, _world, trade.InitiatorContainer, out string? reason))
        {
            SysMessage(reason ?? "Trade failed.");
            SendTradeMessageToPartner?.Invoke(partner, reason ?? "Trade failed.");
            SendTradeUpdateToBoth(trade);
            return false;
        }

        if (!TradeManager.CanAcceptTradeItems(initiator, _world, trade.PartnerContainer, out reason))
        {
            SysMessage(reason ?? "Trade failed.");
            SendTradeMessageToPartner?.Invoke(partner, "Your partner cannot carry that much.");
            SendTradeUpdateToBoth(trade);
            return false;
        }

        CompleteTrade(trade);
        return true;
    }

    /// <summary>
    /// Build one side of a secure trade window and attach it to its owner.
    ///
    /// Source-X Cmd_SecureTrade (CClientUse.cpp:1414/1420) does
    /// <c>LayerAdd(pCont, LAYER_SPECIAL)</c> on each character with type
    /// IT_EQ_TRADE_WINDOW, so the window belongs to a person. SphereNet left both
    /// containers as loose world items with no parent and no position, which is why
    /// nothing downstream could tell whose window it was: the pickup reach check
    /// found neither an owner nor an opened-container record, so a player could not
    /// take back what they had just offered.
    ///
    /// Attached by hand rather than through <see cref="Character.Equip"/>: Layer.Special
    /// is shared with memory items, which deliberately stay out of the equipment slot
    /// array (CharacterMemoryState), so writing the slot here would collide with them.
    /// </summary>
    private Item CreateTradeContainer(Character owner)
    {
        var cont = _world.CreateItem();
        cont.BaseId = 0x1E5E;
        cont.ItemType = Core.Enums.ItemType.EqTradeWindow;
        cont.Name = "Trade Container";
        cont.IsEquipped = true;
        cont.EquipLayer = Core.Enums.Layer.Special;
        cont.ContainedIn = owner.Uid;
        return cont;
    }

    /// <summary>
    /// Open a secure trade with <paramref name="partner"/>, optionally seeded with
    /// the item that was dropped on them. Returns false when the trade did not open.
    ///
    /// The result matters: Source-X Event_Item_Drop bounces the dropped item when
    /// Cmd_SecureTrade fails (CClientEvent.cpp:325). This used to return void, so
    /// every early refusal - a dead or distant partner, REFUSETRADES, either side
    /// already trading - left the item parented to the character with no drag and no
    /// container, unreachable through any normal inventory view, while the client was
    /// told the drop had succeeded.
    /// </summary>
    public bool InitiateTrade(Character partner, Item? firstItem = null)
    {
        if (_character == null || _tradeManager == null) return false;
        if (partner == _character || partner.IsDeleted || !partner.IsPlayer)
        { SysMessage("That is not a valid trade partner."); return false; }
        if (_character.IsDead || partner.IsDead) { SysMessage("You cannot trade while dead."); return false; }
        // Source-X Cmd_SecureTrade (CClientUse.cpp:1338) refuses a partner with no
        // active client, "and also offline players" — a character standing in the
        // world is not the same thing as a player who can answer.
        if (!partner.IsOnline)
        { SysMessage($"{partner.Name} is not available for trade."); return false; }
        if (_character.MapIndex != partner.MapIndex ||
            _character.Position.GetDistanceTo(partner.Position) > 3)
        { SysMessage("That person is too far away."); return false; }
        if (partner.TryGetTag("REFUSETRADES", out string? refuse) &&
            (!int.TryParse(refuse, out int refuseValue) || refuseValue != 0))
        { SysMessage($"{partner.Name} is refusing trade requests."); return false; }

        var existing = _tradeManager.FindTradeFor(_character);
        if (existing != null) { SysMessage("You are already trading."); return false; }

        var partnerTrade = _tradeManager.FindTradeFor(partner);
        if (partnerTrade != null) { SysMessage("They are already trading."); return false; }

        var cont1 = CreateTradeContainer(_character);
        var cont2 = CreateTradeContainer(partner);

        var trade = _tradeManager.StartTrade(_character, partner, cont1, cont2);
        if (FireTradeTrigger(_character, CharTrigger.TradeCreate, trade, partner, firstItem) == TriggerResult.True ||
            FireTradeTrigger(partner, CharTrigger.TradeCreate, trade, _character, firstItem) == TriggerResult.True)
        {
            if (firstItem != null)
                TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
            trade.Cancel();
            _tradeManager.EndTrade(trade);
            _world.RemoveItem(cont1);
            _world.RemoveItem(cont2);
            return false;
        }

        if (firstItem != null && _triggerDispatcher?.FireItemTrigger(firstItem,
            ItemTrigger.DropOnTrade, new TriggerArgs
            {
                CharSrc = _character,
                ItemSrc = firstItem,
                O1 = partner,
                N1 = (int)trade.SessionId.Value
            }) == TriggerResult.True)
        {
            TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
            trade.Cancel();
            _tradeManager.EndTrade(trade);
            _world.RemoveItem(cont1);
            _world.RemoveItem(cont2);
            return false;
        }

        _netState.Send(BuildWorldItemPacket(cont1.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(BuildWorldItemPacket(cont2.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketSecureTradeOpen(
            partner.Uid.Value, cont1.Uid.Value, cont2.Uid.Value, partner.GetName()));

        SendTradeToPartner?.Invoke(partner, _character, cont1, cont2);

        if (firstItem != null)
        {
            if (!cont1.TryAddItem(firstItem))
            {
                TradeManager.ReturnItemToCharacter(_world, _character, firstItem);
                CancelTrade(trade);
                return false;
            }
            _netState.Send(new PacketContainerItem(
                firstItem.Uid.Value, firstItem.DispIdFull, 0,
                firstItem.Amount, 30, 30,
                cont1.Uid.Value, firstItem.Hue, _netState.IsClientPost6017));
            SendTradeItemToPartner?.Invoke(partner, firstItem, cont1);
        }

        return true;
    }

    private void CancelTrade(SecureTrade trade)
    {
        var partner = trade.GetPartner(_character!);
        FinalizeTradeCancel(trade, partner, sendSelfClose: true);
    }

    /// <summary>Server-initiated trade cancel — Source-X CChar::Death deletes
    /// any open trade window when a participant dies. Runs the same finalize
    /// path as a client-side close (items return to packs, both windows close,
    /// @TradeClose fires) so the returned items reach the corpse loot drop.</summary>
    public void CancelActiveTradeOnDeath()
    {
        if (_character == null || _tradeManager == null) return;
        var trade = _tradeManager.FindTradeFor(_character);
        if (trade == null) return;
        FinalizeTradeCancel(trade, trade.GetPartner(_character), sendSelfClose: true);
    }

    private void FinalizeTradeCancel(SecureTrade trade, Character partner, bool sendSelfClose)
    {
        if (_character == null || _tradeManager == null) return;

        TradeManager.ReturnTradeItems(_world, trade);

        if (sendSelfClose)
        {
            var myCont = trade.GetOwnContainer(_character);
            _netState.Send(new PacketSecureTradeClose(myCont.Uid.Value));
        }

        SendTradeCloseToPartner?.Invoke(partner, trade.GetPartnerContainer(_character).Uid.Value);

        FireTradeTrigger(_character, CharTrigger.TradeClose, trade, partner);
        FireTradeTrigger(partner, CharTrigger.TradeClose, trade, _character);

        trade.Cancel();
        _tradeManager.EndTrade(trade);

        _world.RemoveItem(trade.InitiatorContainer);
        _world.RemoveItem(trade.PartnerContainer);
    }

    private void CompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;
        var cont1 = trade.InitiatorContainer;
        var cont2 = trade.PartnerContainer;

        // @TradeAccepted fires BEFORE any item changes hands. RETURN 1 from either
        // side vetoes THIS EXCHANGE, and nothing more: Source-X
        // CItemContainer.cpp:189 simply returns, without Trade_Delete, so the window
        // stays open with both offers untouched and the script's caller can adjust
        // the offer and accept again. Cancelling the whole trade instead handed the
        // goods back and fired @TradeClose on both sides, which a script written
        // against the reference does not expect.
        //
        // Source-X also leaves both check marks SET on this path (:145 sets them,
        // :188 returns before anything clears them), so a later Trade_Status
        // re-fires the trigger. That is reproduced deliberately rather than
        // clearing the flags, which would be a different contract.
        if (FireTradeTrigger(initiator, CharTrigger.TradeAccepted, trade, partner) == TriggerResult.True ||
            FireTradeTrigger(partner, CharTrigger.TradeAccepted, trade, initiator) == TriggerResult.True)
        {
            return;
        }

        foreach (var item in cont1.Contents.ToList())
            TradeManager.ReturnItemToCharacter(_world, partner, item);
        foreach (var item in cont2.Contents.ToList())
            TradeManager.ReturnItemToCharacter(_world, initiator, item);

        _netState.Send(new PacketSecureTradeClose(
            trade.GetOwnContainer(_character!).Uid.Value));
        SendTradeCloseToPartner?.Invoke(
            trade.GetPartner(_character!),
            trade.GetPartnerContainer(_character!).Uid.Value);

        FireTradeTrigger(initiator, CharTrigger.TradeClose, trade, partner);
        FireTradeTrigger(partner, CharTrigger.TradeClose, trade, initiator);

        trade.Complete();
        _tradeManager!.EndTrade(trade);

        _world.RemoveItem(cont1);
        _world.RemoveItem(cont2);

        RefreshBackpackContents();
        RefreshBackpackForPartner?.Invoke(trade.GetPartner(_character!));

        SysMessage("Trade complete.");
        SendTradeMessageToPartner?.Invoke(trade.GetPartner(_character!), "Trade complete.");
    }

    internal void SendTradeUpdateToBoth(SecureTrade trade)
    {
        var myCont = trade.GetOwnContainer(_character!);
        bool myAcc = _character == trade.Initiator ? trade.InitiatorAccepted : trade.PartnerAccepted;
        bool theirAcc = _character == trade.Initiator ? trade.PartnerAccepted : trade.InitiatorAccepted;
        _netState.Send(new PacketSecureTradeUpdate(myCont.Uid.Value, myAcc, theirAcc));

        var partner = trade.GetPartner(_character!);
        SendTradeUpdateToPartner?.Invoke(partner, trade);
    }

    private TriggerResult FireTradeTrigger(Character target, CharTrigger trigger, SecureTrade trade,
        Character other, Item? offeredItem = null)
    {
        bool accepted = trigger == CharTrigger.TradeAccepted;
        return _triggerDispatcher?.FireCharTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = other,
            O1 = (Core.Interfaces.IScriptObj?)offeredItem ?? other,
            N1 = accepted
                ? trade.GetPartnerContainer(target).Contents.Count
                : (int)trade.SessionId.Value,
            N2 = accepted ? trade.GetOwnContainer(target).Contents.Count : 0
        }) ?? TriggerResult.Default;
    }


    /// <summary>Handle rename request (0x75).</summary>
    public void HandleRename(uint serial, string name)
    {
        if (_character == null) return;

        if (_character.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("rename_no_permission"));
            return;
        }

        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 30)
        {
            SysMessage("Invalid name length.");
            return;
        }

        foreach (char c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '\'')
            {
                SysMessage("Name contains invalid characters.");
                return;
            }
        }

        var target = _world.FindChar(new Serial(serial));
        if (target != null)
        {
            string oldName = target.Name;
            var result = _triggerDispatcher?.FireCharTrigger(target, CharTrigger.Rename, new TriggerArgs
            {
                CharSrc = _character,
                S1 = trimmed
            });
            if (result == TriggerResult.True)
                return;

            target.Name = trimmed;
            SysMessage(ServerMessages.GetFormatted("msg_rename_success", oldName, target.Name));
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            item.Name = trimmed;
            SysMessage(ServerMessages.GetFormatted("rename_item_ok", item.Name));
        }
    }

    /// <summary>Handle client view range change (0xC8).</summary>
    public void HandleViewRange(byte range)
    {
        // Clamp to valid range (4-24)
        if (range < 4) range = 4;
        if (range > 24) range = 24;
        _netState.ViewRange = range;
    }

    /// <summary>Open guild stone gump with member list, options.</summary>
    internal void OpenGuildStoneGump(Item stone)
    {
        if (_character == null || _guildManager == null) return;

        // Source-X: a town stone runs the same stone engine — only the
        // wording, the memory type and who may FOUND it differ. Towns are
        // shard infrastructure, so establishing one takes Counsel+.
        bool isTown = stone.ItemType == ItemType.StoneTown;
        string stoneWord = isTown ? "Town" : "Guild";

        var guild = _guildManager.GetGuild(stone.Uid);
        if (guild == null)
        {
            if (isTown && _character.PrivLevel < PrivLevel.Counsel)
            {
                SysMessage("No town is established at this stone.");
                return;
            }

            // No record on this stone yet — offer to create one
            var createGump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 400, 300);
            createGump.AddResizePic(0, 0, 5054, 400, 300);
            createGump.AddText(30, 20, 0, $"{stoneWord} Stone");
            createGump.AddText(30, 50, 0, $"No {stoneWord.ToLowerInvariant()} is registered to this stone.");
            createGump.AddText(30, 80, 0, isTown ? "Establish a town here?" : "Create a new guild?");
            createGump.AddButton(30, 130, 4005, 4007, 1); // Create
            createGump.AddText(70, 130, 0, isTown ? "Establish Town" : "Create Guild");
            createGump.AddButton(150, 250, 4017, 4019, 0); // Cancel

            SendGump(createGump, (buttonId, switches, textEntries) =>
            {
                if (buttonId == 1)
                {
                    if (_character == null || _character.IsDeleted || stone.IsDeleted ||
                        _world.FindItem(stone.Uid) != stone || _guildManager.GetGuild(stone.Uid) != null ||
                        _guildManager.FindGuildRecordFor(_character.Uid, isTown) != null ||
                        (isTown && _character.PrivLevel < PrivLevel.Counsel) ||
                        _character.MapIndex != stone.MapIndex ||
                        _character.Position.GetDistanceTo(stone.Position) > 3)
                    {
                        SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                        return;
                    }
                    // A town takes its name from the GM-named stone item.
                    string newName = isTown && !string.IsNullOrWhiteSpace(stone.Name)
                        ? stone.Name!
                        : $"{_character.Name}'s {stoneWord}";
                    var newGuild = _guildManager.CreateGuild(stone.Uid, newName, _character.Uid, isTown);
                    StampStoneMemory(_character, stone, add: true);
                    SysMessage(ServerMessages.GetFormatted("guild_created", newGuild.Name));
                }
            });
            return;
        }

        // Show stone info gump
        var gump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 500, 520);
        gump.AddResizePic(0, 0, 5054, 500, 520);
        gump.AddText(30, 10, 0, $"{stoneWord}: {guild.Name}");
        gump.AddText(30, 30, 0, $"Abbreviation: [{guild.Abbreviation}]");
        if (!string.IsNullOrEmpty(guild.Charter))
            gump.AddText(30, 50, 0, $"Charter: {guild.Charter}");
        gump.AddText(30, 70, 0,
            $"{(isTown ? "Citizens" : "Members")}: {guild.MemberCount} | Wars: {guild.Wars.Count()} | Allies: {guild.Allies.Count()}");

        // Member list with titles and candidate status
        int y = 100;
        int memberIdx = 0;
        foreach (var member in guild.Members)
        {
            var ch = _world.FindChar(member.CharUid);
            string memberName = ch?.Name ?? $"UID 0x{member.CharUid.Value:X}";
            string privText = member.Priv switch
            {
                GuildPriv.Master => isTown ? " [Mayor]" : " [Master]",
                GuildPriv.Candidate => " [Candidate]",
                _ => ""
            };
            string titleText = !string.IsNullOrEmpty(member.Title) ? $" ({member.Title})" : "";
            int hue = member.Priv == GuildPriv.Candidate ? 33 : 0; // yellow for candidates
            gump.AddText(50, y, hue, $"{memberName}{privText}{titleText}");
            y += 20;
            memberIdx++;
            if (y > 350) break;
        }

        var myMember = guild.FindMember(_character.Uid);
        int btnY = 370;

        if (myMember == null)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1); // Join
            gump.AddText(70, btnY, 0, isTown ? "Request Citizenship" : "Request to Join");
            btnY += 25;
        }
        else if (myMember.Priv == GuildPriv.Master)
        {
            // Two columns — the master verb surface mirrors the Source-X
            // guild-stone MASTERMENU (CItemStone_functions.tbl).
            gump.AddButton(30, btnY, 4005, 4007, 2); // Disband
            gump.AddText(70, btnY, 0, isTown ? "Dissolve Town" : "Disband Guild");
            gump.AddButton(260, btnY, 4005, 4007, 15); // Dismiss member
            gump.AddText(300, btnY, 0, "Dismiss Member");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10); // Accept candidates
            gump.AddText(70, btnY, 0, "Accept Candidate");
            gump.AddButton(260, btnY, 4005, 4007, 16); // Declare alliance
            gump.AddText(300, btnY, 0, "Declare Alliance");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11); // Set title
            gump.AddText(70, btnY, 0, "Set Member Title");
            gump.AddButton(260, btnY, 4005, 4007, 17); // Break alliance
            gump.AddText(300, btnY, 0, "Break Alliance");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12); // Declare war
            gump.AddText(70, btnY, 0, "Declare War");
            gump.AddButton(260, btnY, 4005, 4007, 13); // Declare peace
            gump.AddText(300, btnY, 0, "Declare Peace");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14); // Set charter
            gump.AddText(70, btnY, 0, "Set Charter");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 1, guild.Charter);
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 18); // Rename guild
            gump.AddText(70, btnY, 0, "Set Name");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 2, guild.Name);
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 19); // Set abbreviation
            gump.AddText(70, btnY, 0, "Set Abbreviation");
            gump.AddTextEntry(170, btnY, 100, 20, 0, 3, guild.Abbreviation);
            btnY += 25;
        }
        else
        {
            gump.AddButton(30, btnY, 4005, 4007, 3); // Leave
            gump.AddText(70, btnY, 0, isTown ? "Renounce Citizenship" : "Leave Guild");
            btnY += 25;
        }
        if (myMember != null && guild.IsMember(_character.Uid))
        {
            // Member-level verbs: abbreviation display toggle + fealty vote
            // (Source-X TOGGLEABBREVIATION / DECLAREFEALTY).
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, myMember.ShowAbbrev ? "Hide Abbreviation" : "Show Abbreviation");
            gump.AddButton(260, btnY, 4005, 4007, 21);
            gump.AddText(300, btnY, 0, "Declare Fealty");
            btnY += 25;
        }
        if (!string.IsNullOrEmpty(guild.WebUrl))
        {
            gump.AddButton(30, btnY, 4005, 4007, 4); // Visit web page (0xA5)
            gump.AddText(70, btnY, 0, "Visit Web Page");
        }
        gump.AddButton(350, 480, 4017, 4019, 0); // Close

        var capturedGuild = guild;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleGuildGumpResponse(stone, capturedGuild, buttonId, textEntries);
        });
    }

    /// <summary>Source-X CStoneMember link: membership carries a MEMORY_GUILD /
    /// MEMORY_TOWN memory of the stone, readable by scripts. Stamped on
    /// acceptance/founding, cleared on leave/dismiss/disband.</summary>
    private void StampStoneMemory(Character ch, Item stone, bool add)
    {
        var memType = stone.ItemType == ItemType.StoneTown ? MemoryType.Town : MemoryType.Guild;
        if (add)
        {
            ch.Memory_AddObjTypes(stone.Uid, memType);
        }
        else
        {
            var mem = ch.Memory_FindObjTypes(stone.Uid, memType);
            if (mem != null)
                ch.Memory_ClearTypes(mem, memType);
        }
    }

    private void HandleGuildGumpResponse(Item stone, GuildDef guild, uint buttonId, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null || _guildManager == null) return;
        if (stone.IsDeleted || _world.FindItem(stone.Uid) != stone ||
            _guildManager.GetGuild(stone.Uid) != guild)
            return;

        if ((buttonId is 2 or (>= 10 and <= 19)) &&
            guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
        {
            SysMessage(ServerMessages.Get("msg_insufficient_priv"));
            return;
        }

        switch (buttonId)
        {
            case 1: // Join request
                // One guild AND one town per character — the pools are
                // independent (Source-X MEMORY_GUILD vs MEMORY_TOWN), so a
                // guild member may still become a town citizen and vice versa.
                if (_guildManager.FindGuildRecordFor(_character.Uid,
                        stone.ItemType == ItemType.StoneTown) != null)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                guild.AddRecruit(_character.Uid);
                SysMessage(ServerMessages.Get("guild_join_request"));
                break;
            case 2: // Disband — only guild master may disband
            {
                var disbandMember = guild.FindMember(_character.Uid);
                if (disbandMember == null || disbandMember.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                foreach (var m in guild.Members.ToArray())
                {
                    var mch = _world.FindChar(m.CharUid);
                    if (mch != null)
                        StampStoneMemory(mch, stone, add: false);
                }
                _guildManager.RemoveGuild(stone.Uid);
                SysMessage(ServerMessages.Get("guild_disbanded"));
                break;
            }
            case 4: // Visit web page — 0xA5 opens the client's browser
                if (!string.IsNullOrEmpty(guild.WebUrl))
                    Send(new PacketWebLink(guild.WebUrl));
                break;
            case 3: // Leave — must actually belong to this guild (button can be
                    // spoofed by a crafted client packet, so don't trust the gump).
            {
                var leaveMember = guild.FindMember(_character.Uid);
                if (leaveMember == null)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                guild.RemoveMember(_character.Uid);
                StampStoneMemory(_character, stone, add: false);
                SysMessage(ServerMessages.Get("guild_left"));
                break;
            }
            case 10: // Accept candidate
                SysMessage(ServerMessages.Get("guild_target_candidate"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (guild.AcceptMember(target.Uid))
                    {
                        StampStoneMemory(target, stone, add: true);
                        SysMessage(ServerMessages.GetFormatted("guild_member_added", target.Name));
                    }
                    else
                        SysMessage(ServerMessages.GetFormatted("guild_not_candidate", target.Name));
                });
                break;
            case 11: // Set member title
                SysMessage(ServerMessages.Get("guild_target_title"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var member = guild.FindMember(target.Uid);
                    if (member == null) { SysMessage(ServerMessages.Get("guild_not_member")); return; }
                    // Use text entry if provided
                    var titleEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                    if (!string.IsNullOrWhiteSpace(titleEntry.Text))
                    {
                        member.Title = titleEntry.Text.Trim()[..Math.Min(40, titleEntry.Text.Trim().Length)];
                        SysMessage(ServerMessages.GetFormatted("guild_title_set", target.Name, member.Title));
                    }
                    else
                        SysMessage(ServerMessages.Get("guild_no_title"));
                });
                break;
            case 12: // Declare war
                SysMessage(ServerMessages.Get("guild_target_enemy"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var enemyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (enemyGuild == null) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    _guildManager.DeclareWar(stone.Uid, targetItem.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_war_declared", enemyGuild.Name));
                });
                break;
            case 13: // Declare peace
                SysMessage(ServerMessages.Get("guild_target_peace"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    _guildManager.DeclarePeace(stone.Uid, targetItem.Uid);
                    SysMessage(ServerMessages.Get("guild_peace_declared"));
                });
                break;
            case 14: // Set charter
            {
                var charterEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                if (!string.IsNullOrWhiteSpace(charterEntry.Text))
                {
                    var charter = charterEntry.Text.Trim();
                    guild.Charter = charter[..Math.Min(200, charter.Length)];
                    SysMessage(ServerMessages.Get("guild_charter_updated"));
                }
                break;
            }
            case 15: // Dismiss member (Source-X DISMISSMEMBER) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the member to dismiss.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (target.Uid == _character.Uid) { SysMessage("Use Disband or Leave instead."); return; }
                    if (guild.FindMember(target.Uid) == null)
                    {
                        SysMessage(ServerMessages.Get("guild_not_member"));
                        return;
                    }
                    guild.RemoveMember(target.Uid);
                    StampStoneMemory(target, stone, add: false);
                    SysMessage($"{target.Name} has been dismissed from the {(stone.ItemType == ItemType.StoneTown ? "town" : "guild")}.");
                });
                break;
            }
            case 16: // Declare alliance (Source-X DECLAREPEACE/ally path) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the guild stone to ally with.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var allyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (allyGuild == null || allyGuild == guild) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    _guildManager.DeclareAlliance(stone.Uid, targetItem.Uid);
                    _guildManager.DeclareAlliance(targetItem.Uid, stone.Uid);
                    SysMessage($"Alliance declared with {allyGuild.Name}.");
                });
                break;
            }
            case 17: // Break alliance — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the allied guild stone.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master) return;
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    _guildManager.WithdrawAlliance(stone.Uid, targetItem.Uid);
                    _guildManager.WithdrawAlliance(targetItem.Uid, stone.Uid);
                    SysMessage("The alliance has been dissolved.");
                });
                break;
            }
            case 18: // Rename guild (Source-X SETNAME) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                var nameEntry = textEntries.FirstOrDefault(e => e.Id == 2);
                if (!string.IsNullOrWhiteSpace(nameEntry.Text))
                {
                    var guildName = nameEntry.Text.Trim();
                    guild.Name = guildName[..Math.Min(40, guildName.Length)];
                    SysMessage($"The guild is now known as {guild.Name}.");
                }
                break;
            }
            case 19: // Set abbreviation (Source-X SETABBREVIATION) — master only
            {
                if (guild.FindMember(_character.Uid)?.Priv != GuildPriv.Master)
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                var abbrevEntry = textEntries.FirstOrDefault(e => e.Id == 3);
                if (!string.IsNullOrWhiteSpace(abbrevEntry.Text))
                {
                    var abbreviation = abbrevEntry.Text.Trim();
                    guild.Abbreviation = abbreviation[..Math.Min(4, abbreviation.Length)];
                    SysMessage($"Guild abbreviation set to [{guild.Abbreviation}].");
                }
                break;
            }
            case 20: // Toggle abbreviation display (Source-X TOGGLEABBREVIATION)
            {
                var toggleMember = guild.FindMember(_character.Uid);
                if (toggleMember == null || !guild.IsMember(_character.Uid))
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                toggleMember.ShowAbbrev = !toggleMember.ShowAbbrev;
                SysMessage(toggleMember.ShowAbbrev
                    ? "Your guild abbreviation is now shown."
                    : "Your guild abbreviation is now hidden.");
                break;
            }
            case 21: // Declare fealty (Source-X DECLAREFEALTY) — vote, then recount
            {
                var voter = guild.FindMember(_character.Uid);
                if (voter == null || !guild.IsMember(_character.Uid))
                {
                    SysMessage(ServerMessages.Get("msg_insufficient_priv"));
                    break;
                }
                SysMessage("Target the member you pledge your loyalty to.");
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var currentVoter = guild.FindMember(_character.Uid);
                    if (currentVoter == null || !guild.IsMember(_character.Uid)) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (!guild.IsMember(target.Uid))
                    {
                        SysMessage(ServerMessages.Get("guild_not_member"));
                        return;
                    }
                    currentVoter.LoyalTo = target.Uid;
                    guild.ElectMaster(); // recount — mastership follows the votes
                    SysMessage($"You are now loyal to {target.Name}.");
                });
                break;
            }
        }
    }

    /// <summary>Open house management gump from house sign or multi item.</summary>
    internal void OpenHouseSignGump(Item signOrMulti)
    {
        if (_character == null || _housingEngine == null) return;

        // Find the house — could be the multi item itself or linked via tag
        var house = _housingEngine.GetHouse(signOrMulti.Uid);
        if (house == null && signOrMulti.Link.IsValid)
            house = _housingEngine.GetHouse(signOrMulti.Link);
        if (house == null && signOrMulti.TryGetTag("HOUSE_UID", out string? houseUidStr) &&
            uint.TryParse(houseUidStr, out uint houseUid))
        {
            house = _housingEngine.GetHouse(new Serial(houseUid));
        }

        if (house == null)
        {
            SysMessage(ServerMessages.Get("house_not_house"));
            return;
        }

        // Auto-refresh on owner visit
        _housingEngine.OnCharacterEnterHouse(_character, house);

        var priv = house.GetPriv(_character.Uid);
        var ownerCh = _world.FindChar(house.Owner);
        string ownerName = ownerCh?.Name ?? "Unknown";

        bool isStaff = _character.PrivLevel >= PrivLevel.GM;
        bool isOwner = priv == HousePriv.Owner || isStaff;
        bool canManageStorage = priv is HousePriv.Owner or HousePriv.CoOwner || isStaff;

        var gump = new GumpBuilder(_character.Uid.Value, signOrMulti.Uid.Value, 420, 540);
        gump.AddResizePic(0, 0, 5054, 420, 540);
        gump.AddText(30, 10, 0, "House Management");
        gump.AddText(30, 35, 0, $"Owner: {ownerName}");
        gump.AddText(30, 55, 0, $"Type: {house.Type}");
        gump.AddText(30, 75, 0, $"Storage: {house.Lockdowns.Count}/{house.MaxLockdowns} lockdowns, {house.SecureContainers.Count}/{house.MaxSecure} secure");
        gump.AddText(30, 95, 0, $"Condition: {house.DecayStage}");
        gump.AddText(30, 115, 0, $"Co-Owners: {house.CoOwners.Count}  Friends: {house.Friends.Count}");

        int btnY = 145;
        if (isOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1);
            gump.AddText(70, btnY, 0, "Transfer House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 2);
            gump.AddText(70, btnY, 0, "Demolish House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10);
            gump.AddText(70, btnY, 0, "Add Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12);
            gump.AddText(70, btnY, 0, "Remove Co-Owner");
            btnY += 25;
        }
        if (canManageStorage)
        {
            gump.AddButton(30, btnY, 4005, 4007, 11);
            gump.AddText(70, btnY, 0, "Add Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13);
            gump.AddText(70, btnY, 0, "Remove Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14);
            gump.AddText(70, btnY, 0, "Lock Down Item");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 15);
            gump.AddText(70, btnY, 0, "Release Lockdown");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 16);
            gump.AddText(70, btnY, 0, "Secure Container");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 17);
            gump.AddText(70, btnY, 0, "Release Secure");
            btnY += 25;
        }
        if (isOwner && house.MultiItem.ItemType == ItemType.MultiCustom)
        {
            gump.AddButton(30, btnY, 4005, 4007, 4);
            gump.AddText(70, btnY, 0, "Customize House");
            btnY += 25;
        }
        if (isOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, "Ban Player");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 21);
            gump.AddText(70, btnY, 0, "Unban Player");
            btnY += 25;
        }
        if (isStaff || house.CanAccess(_character.Uid))
        {
            gump.AddButton(30, btnY, 4005, 4007, 3);
            gump.AddText(70, btnY, 0, "Open Door");
            btnY += 25;
        }
        gump.AddButton(280, 505, 4017, 4019, 0); // Close

        var capturedHouse = house;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleHouseGumpResponse(signOrMulti, capturedHouse, buttonId);
        });
    }

    private void HandleHouseGumpResponse(Item signOrMulti, House house, uint buttonId)
    {
        if (_character == null || _housingEngine == null) return;

        bool IsRegistered() => _housingEngine.GetHouse(house.MultiItem.Uid) == house;
        bool HasOwnerAuthority() => IsRegistered() &&
            (_character.PrivLevel >= PrivLevel.GM || house.Owner == _character.Uid);
        bool HasStorageAuthority() => IsRegistered() &&
            (_character.PrivLevel >= PrivLevel.GM || house.CanLockdown(_character.Uid));

        bool authorized = buttonId switch
        {
            1 or 2 or 4 or 10 or 12 or 20 or 21 => HasOwnerAuthority(),
            11 or 13 or 14 or 15 or 16 or 17 => HasStorageAuthority(),
            3 => IsRegistered() && (_character.PrivLevel >= PrivLevel.GM || house.CanAccess(_character.Uid)),
            _ => true,
        };
        if (!authorized)
        {
            SysMessage("You do not have permission to manage this house.");
            return;
        }

        switch (buttonId)
        {
            case 1: // Transfer — target the new owner
                SysMessage(ServerMessages.Get("house_select_owner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer)
                    {
                        SysMessage(ServerMessages.Get("msg_invalid_target"));
                        return;
                    }
                    // Transfer must respect the recipient's house cap, same as
                    // PlaceHouse — otherwise it's an easy way to exceed the limit.
                    if (!_housingEngine.TransferHouse(house, _character, target))
                    {
                        SysMessage(ServerMessages.Get("house_add_limit"));
                        return;
                    }
                    SysMessage(ServerMessages.GetFormatted("house_transferred", target.Name));
                });
                break;
            case 2: // Demolish
                var deed = _housingEngine.RemoveHouse(house.MultiItem.Uid, _character);
                if (deed != null)
                    SysMessage(ServerMessages.Get("house_demolished"));
                else
                    SysMessage(ServerMessages.Get("house_cant_demolish"));
                break;
            case 3: // Open door
                OpenDoor();
                break;
            case 4: // Customize House — enter the client design editor
                BeginHouseCustomization(house.MultiItem);
                break;
            case 10: // Add Co-Owner
                SysMessage(ServerMessages.Get("house_add_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_coowner", target.Name));
                });
                break;
            case 11: // Add Friend
                SysMessage(ServerMessages.Get("house_add_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_friend", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_friend", target.Name));
                });
                break;
            case 12: // Remove Co-Owner
                SysMessage(ServerMessages.Get("house_remove_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_coowner"));
                });
                break;
            case 13: // Remove Friend
                SysMessage(ServerMessages.Get("house_remove_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_friend", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_friend"));
                });
                break;
            case 14: // Lock Down Item
                SysMessage(ServerMessages.Get("house_lockdown"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    var lockItem = _world.FindItem(targetUid);
                    if (lockItem == null || _housingEngine?.FindHouseAt(lockItem.Position) != house)
                    {
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                        return;
                    }
                    if (house.Lockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                });
                break;
            case 15: // Release Lockdown
                SysMessage(ServerMessages.Get("house_lockdown_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.ReleaseLockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_released"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_not"));
                });
                break;
            case 16: // Secure Container
                SysMessage(ServerMessages.Get("house_secure"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.SecureContainer(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_fail"));
                });
                break;
            case 17: // Release Secure
                SysMessage(ServerMessages.Get("house_secure_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasStorageAuthority()) return;
                    var targetUid = new Serial(serial);
                    if (house.ReleaseSecure(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_released"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_not"));
                });
                break;
            case 20: // Ban
                SysMessage(ServerMessages.Get("house_ban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (_housingEngine.BanFromHouse(house, target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_banned", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_banned", target.Name));
                });
                break;
            case 21: // Unban
                SysMessage(ServerMessages.Get("house_unban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (!HasOwnerAuthority()) return;
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_unbanned", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_banned"));
                });
                break;
        }
    }


    public void OpenDoor()
    {
        if (_character == null) return;
        if (_character.IsDead) return;
        foreach (var item in _world.GetItemsInRange(_character.Position, 2))
        {
            if (!DoorHelper.IsDoorItem(item, _world.MapData))
                continue;
            if (item.ItemType == ItemType.DoorLocked)
            {
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                return;
            }
            ToggleDoor(item);
            return;
        }

        TryToggleNearestMapStaticDoor(0);
    }

    internal bool TryToggleNearestMapStaticDoor(uint clientSerial)
    {
        if (_character == null) return false;
        if (_character.IsDead) return false;
        if (!DoorHelper.FindNearestStaticDoor(
                _world.MapData, _character.MapIndex, _character.X, _character.Y, 2,
                out short x, out short y, out sbyte z, out ushort tileId, out ushort hue))
            return false;

        bool open = _world.IsMapStaticDoorOpen(_character.MapIndex, x, y, z);
        // Classic-set doors: derive the pair arts from the doordir slot so
        // closing restores the STATIC's own art (the old tileId−1 walked
        // below the closed art when the map static was the closed leaf), and
        // shift the open visual by the Source-X hinge offset.
        int doorDir = DoorHelper.GetDoorDir(tileId);
        ushort closedArt = doorDir >= 0 ? (ushort)(tileId - (doorDir & 1)) : (ushort)(tileId - 1);
        ushort openArt = doorDir >= 0 ? (ushort)(closedArt + 1) : (ushort)(tileId + 1);
        bool opening = !open;
        ushort newTile = opening ? openArt : closedArt;
        short vx = x, vy = y;
        if (opening && doorDir >= 0)
        {
            var (sx, sy) = DoorHelper.GetDoorShift(doorDir & ~1);
            vx += sx;
            vy += sy;
        }
        _world.SetMapStaticDoorOpen(_character.MapIndex, x, y, z, opening);

        // Reuse the client's serial only when it is one of OUR synthetic static
        // serials (item-flagged coordinate encoding). Broadcasting the door art
        // under any other serial re-types that object client-side — with a
        // character uid it made the mobile vanish into a door graphic.
        uint serial = clientSerial != 0 && (clientSerial & Serial.ItemFlag) != 0
            ? clientSerial
            : (uint)(Serial.ItemFlag | (uint)((x & 0x7FFF) << 16) | (uint)((y & 0x3FFF) << 3) | (uint)(z & 0x07));
        BroadcastMapStaticDoorUpdate(serial, newTile, vx, vy, z, hue, opening);
        return true;
    }

    private void BroadcastMapStaticDoorUpdate(
        uint serial, ushort tileId, short x, short y, sbyte z, ushort hue, bool opening)
    {
        if (_character == null) return;
        var pos = new Point3D(x, y, z, _character.MapIndex);
        ushort soundId = (ushort)(opening ? 0x00EA : 0x00F1);
        var soundPacket = new PacketSound(soundId, x, y, z);
        BroadcastNearby?.Invoke(pos, UpdateRange, soundPacket, 0);

        _netState.Send(BuildWorldItemPacket(serial, tileId, 1, x, y, z, hue));
        var broadcastPacket = new PacketWorldItem(serial, tileId, 1, x, y, z, hue);
        BroadcastNearby?.Invoke(pos, UpdateRange, broadcastPacket, _character.Uid.Value);
    }

    /// <summary>Raise or lower a portcullis. Source-X gives the vertical gate its own
    /// function (Use_Portculis, CItem.cpp:4583): it moves the gate between the two
    /// heights in MORE1 and MORE2 and does NOT touch the graphic. SphereNet sent it
    /// down the hinged-door path instead, which swapped the art by an offset of two
    /// and left the gate at its old Z, so the designed heights never took effect and
    /// the classic door's 20-second timer was inherited along with them.</summary>
    internal bool UsePortcullis(Item gate)
    {
        // Not while it is inside something (:4587).
        if (gate.ContainedIn.IsValid)
            return false;

        sbyte lowered = unchecked((sbyte)gate.More1);
        sbyte raised = unchecked((sbyte)gate.More2);
        sbyte target = gate.Z == lowered ? raised : lowered;
        if (target == gate.Z)
            return false;       // both heights the same: nothing to do (:4596)

        var world = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke();
        var pos = new Point3D(gate.X, gate.Y, target, gate.MapIndex);
        if (world != null)
            world.PlaceItem(gate, pos);
        else
            gate.Position = pos;

        // PORTCULISSOUND overrides the default (:4602).
        ushort sound = gate.TryGetTag("PORTCULISSOUND", out string? raw) &&
            ushort.TryParse(raw, out ushort custom) && custom != 0 ? custom : (ushort)0x021D;
        BroadcastNearby?.Invoke(gate.Position, UpdateRange,
            new PacketSound(sound, gate.X, gate.Y, gate.Z), 0);
        BroadcastNearby?.Invoke(gate.Position, UpdateRange,
            new PacketWorldItem(gate.Uid.Value, gate.DispIdFull, gate.Amount,
                gate.X, gate.Y, gate.Z, gate.Hue), 0);
        return true;
    }

    /// <summary>Follow an item's LINK chain after it has been used. Source-X returns
    /// MASK_RETURN_FOLLOW_LINKS from Do_Use_Item and Use_Item then walks m_uidLink,
    /// calling Do_Use_Item(target, fLink: true) for up to 64 hops, stopping on a
    /// missing target or a return to the item it started from (CCharUse.cpp:1962).
    /// A lever's graphic flipped but the door it was linked to never moved.
    ///
    /// The chain applies the LINKED use, not a fresh double-click: a link only ever
    /// opens a door (:4641), and it carries the authority that lets it work a locked
    /// portcullis (:1771).</summary>
    internal void FollowItemLinks(Item start)
    {
        var current = start;
        for (int hop = 0; hop < 64; hop++)
        {
            if (!current.Link.IsValid)
                return;

            var next = _world.FindItem(current.Link);
            if (next == null || next.IsDeleted || next == start)
                return;

            switch (next.ItemType)
            {
                case ItemType.Door:
                case ItemType.DoorLocked:
                    ToggleDoor(next, justOpen: true, viaLink: true);
                    break;
                case ItemType.Portculis:
                case ItemType.PortLocked:
                    UsePortcullis(next);
                    break;
            }

            current = next;
        }
    }

    /// <param name="justOpen">A link signal only ever opens: Source-X returns early
    /// when a just-open use finds the door already open (Use_DoorNew, CItem.cpp:4641),
    /// so a second pull of the lever does not close it.</param>
    /// <param name="viaLink">The use arrived through a LINK rather than from the
    /// player's own hand, so the door may be out of reach - the lever was what they
    /// touched.</param>
    internal void ToggleDoor(Item door, bool justOpen = false, bool viaLink = false)
    {
        if (_character == null) return;
        if (_character.IsDead) return;

        // Source-X refuses a door that is not top-level before touching anything
        // (Use_DoorNew :4637, Use_Door :4695). Without it, a door carried in a pack
        // had its container slot read as world coordinates: the hinge shift was added
        // to them and PlaceItem then dropped the door out of the pack onto the map.
        // The timer close path reached the same code, so a door picked up while open
        // left the backpack on its own.
        if (door.ContainedIn.IsValid)
            return;

        if (!viaLink)
        {
            int dx = Math.Abs(_character.X - door.X);
            int dy = Math.Abs(_character.Y - door.Y);
            if (_character.MapIndex != door.MapIndex || dx > 2 || dy > 2)
            {
                SysMessage("That is too far away.");
                return;
            }
        }

        bool isOpen = door.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1";
        int doorDir = DoorHelper.GetDoorDir(door.DispIdFull);
        // The graphic IS the state for a classic set, whose ids come in closed/open
        // pairs. A custom door's alternate art is not part of that table - it may even
        // land on another set's closed slot - so its state is the flag alone, which is
        // what the reference reads too (ATTR_OPENED).
        if (doorDir >= 0 && door.DoorOpenId == 0)
            isOpen = (doorDir & 1) != 0;

        if (justOpen && isOpen)
            return;

        // Source-X Use_DoorNew resolves an alternate open graphic first - the
        // DOOROPENID override, or the itemdef's door switch id - and only falls back
        // to the classic hinge table when there is none (CItem.cpp:4649). SphereNet
        // had no DOOROPENID at all, so a door scripted with a custom open art and
        // offset behaved like an ordinary one.
        if (door.DoorOpenId != 0)
        {
            UseCustomDoor(door, isOpen);
            return;
        }

        ushort displayId = door.DispIdFull;
        ushort newDisplayId = (ushort)(displayId + (isOpen ? -1 : 1));
        if (door.DispIdOverride != 0)
            door.TrySetProperty("DISPID", $"0{newDisplayId:X}");
        else
            door.BaseId = newDisplayId;

        if (isOpen)
            door.RemoveTag("DOOR_OPEN");
        else
            door.SetTag("DOOR_OPEN", "1");

        // Source-X Use_Door: the leaf swings around its hinge, so the item
        // MOVES as it opens/closes. Without the shift the open art renders
        // anchored at the closed tile — the door looks like it opens
        // backwards / into the wall.
        DoorHelper.MoveDoorLeaf(door, doorDir);

        // Source-X _SetTimeoutS(20): an opened door swings shut on its own.
        door.SetTimeout(isOpen ? 0 : Environment.TickCount64 + 20_000);

        // Play door sound and broadcast updated item to nearby clients
        ushort soundId = (ushort)(isOpen ? 0x00F1 : 0x00EA); // close/open sounds
        var soundPacket = new PacketSound(soundId, door.X, door.Y, door.Z);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, soundPacket, 0);

        _netState.Send(BuildWorldItemPacket(
            door.Uid.Value, door.DispIdFull, door.Amount,
            door.X, door.Y, door.Z, door.Hue));
        var doorBroadcast = new PacketWorldItem(
            door.Uid.Value, door.DispIdFull, door.Amount,
            door.X, door.Y, door.Z, door.Hue);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, doorBroadcast, _character.Uid.Value);
    }

    /// <summary>The custom half of Source-X Use_DoorNew (CItem.cpp:4633): swap to the
    /// alternate graphic, shift by MOREP rather than the hinge table, and remember the
    /// graphic just replaced in DOOROPENID so the next use swaps straight back.</summary>
    private void UseCustomDoor(Item door, bool isOpen)
    {
        short dx = door.MoreP.X;
        short dy = door.MoreP.Y;

        var pos = new Point3D(
            (short)(isOpen ? door.X - dx : door.X + dx),
            (short)(isOpen ? door.Y - dy : door.Y + dy),
            door.Z, door.MapIndex);

        // The graphic being replaced becomes the alternate for the way back (:4681).
        ushort previous = door.DispIdFull;
        ushort alternate = door.DoorOpenId;
        door.DoorOpenId = previous;
        if (door.DispIdOverride != 0)
            door.TrySetProperty("DISPID", $"0{alternate:X}");
        else
            door.BaseId = alternate;

        if (isOpen)
            door.RemoveTag("DOOR_OPEN");
        else
            door.SetTag("DOOR_OPEN", "1");

        var world = SphereNet.Game.Objects.ObjBase.ResolveWorld?.Invoke();
        if (world != null)
            world.PlaceItem(door, pos);
        else
            door.Position = pos;

        door.SetTimeout(isOpen ? 0 : Environment.TickCount64 + 20_000);

        ushort soundId = (ushort)(isOpen ? 0x00F1 : 0x00EA);
        BroadcastNearby?.Invoke(door.Position, UpdateRange,
            new PacketSound(soundId, door.X, door.Y, door.Z), 0);
        _netState.Send(BuildWorldItemPacket(
            door.Uid.Value, door.DispIdFull, door.Amount,
            door.X, door.Y, door.Z, door.Hue));
        BroadcastNearby?.Invoke(door.Position, UpdateRange,
            new PacketWorldItem(door.Uid.Value, door.DispIdFull, door.Amount,
                door.X, door.Y, door.Z, door.Hue), _character!.Uid.Value);
    }

    internal static (ushort Anim, ushort Sound) GetCraftAnimAndSound(SkillType skill) => skill switch
    {
        SkillType.Blacksmithing => ((ushort)AnimationType.Attack1HBash, (ushort)0x002A),
        SkillType.Carpentry => ((ushort)AnimationType.Attack2HSlash, (ushort)0x023D),
        SkillType.Tailoring => ((ushort)AnimationType.Bow, (ushort)0x0248),
        SkillType.Tinkering => ((ushort)AnimationType.Attack1HBash, (ushort)0x002A),
        SkillType.Cooking => ((ushort)AnimationType.Bow, (ushort)0x0225),
        SkillType.Alchemy => ((ushort)AnimationType.Bow, (ushort)0x0242),
        SkillType.Bowcraft => ((ushort)AnimationType.Bow, (ushort)0x023D),
        SkillType.Inscription => ((ushort)AnimationType.Bow, (ushort)0x0249),
        SkillType.Cartography => ((ushort)AnimationType.Bow, (ushort)0x0249),
        _ => ((ushort)AnimationType.Bow, (ushort)0x002A),
    };

    internal void UsePotion(Item potion)
    {
        if (_character == null) return;

        // Source-X routes IT_POTION/IT_PITCHER through Use_Drink, which refuses an
        // item the user cannot move (a placed potion/pitcher fixture) before drinking
        // it — otherwise a non-GM double-click would destroy the fixture.
        if (!ItemMoveRules.CanMove(_character, potion, out _))
        {
            SysMessage(ServerMessages.Get("drink_cantmove"));
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextPotionTimeMs)
        {
            SysMessage("You must wait before using another potion.");
            return;
        }
        _nextPotionTimeMs = now + 2000;

        // Source-X Use_Drink IT_POTION: the potion CONVEYS THE SPELL stored in
        // MORE1 at strength MORE2 (m_itPotion.m_Type / m_dwSkillQuality),
        // delivered through OnSpellEffect — no hardcoded potion families.
        // Strength/agility therefore become TIMED spell effects (the old code
        // did a permanent Str/Dex += 10), and a bottle with no resolvable
        // effect is just a drink — the old "default to heal" made any tagless
        // liquid (including a full water pitcher) a free heal potion.
        var drinkSpell = ResolveDrinkSpell(potion);
        if (drinkSpell != 0 && _client.Spells != null)
        {
            int strength = (int)Math.Min(potion.More2, 2000);
            if (strength <= 0)
                strength = 500; // legacy bottle with no stored alchemy quality
            _client.Spells.ApplyDirectEffect(_character, _character, drinkSpell, strength);
        }
        else if (potion.ItemType == ItemType.Potion &&
                 potion.TryGetTag("POTION_TYPE", out string? oldType) && oldType != null)
        {
            // Old-system bottles (pre spell-driven potions): keep the bounded
            // restores; stat potions route through the timed spell above via
            // ResolveDrinkSpell, so no permanent gain path remains.
            switch (oldType.ToLowerInvariant())
            {
                case "refresh":
                    _character.Stam = (short)Math.Min(_character.Stam + 25, _character.MaxStam);
                    SysMessage(ServerMessages.GetFormatted("potion_stamina", 25));
                    break;
                case "totalrefresh":
                    _character.Stam = _character.MaxStam;
                    SysMessage(ServerMessages.GetFormatted("potion_stamina", 60));
                    break;
                default:
                    SysMessage(ServerMessages.Get("potion_drink"));
                    break;
            }
        }
        else
        {
            SysMessage(ServerMessages.Get("potion_drink"));
        }

        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketAnimation(_character.Uid.Value, (ushort)AnimationType.Eat), 0);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange,
            new PacketSound(0x0031, _character.X, _character.Y, _character.Z), 0);

        // Update stats
        SendCharacterStatus(_character);

        // Consume exactly one potion. Source-X parity: @Destroy RETURN 1 keeps the
        // bottle; a stack burns one unit, never the whole pile (one drink used to
        // delete every potion in the stack).
        var drinkContainer = potion.ContainedIn.IsValid ? _world.FindItem(potion.ContainedIn) : null;
        var drinkPos = potion.Position;
        var drinkDef = DefinitionLoader.GetItemDef(potion.BaseId);

        if (potion.Amount > 1)
        {
            potion.Amount--;
            if (potion.ContainedIn.IsValid)
                _netState.Send(new PacketContainerItem(
                    potion.Uid.Value, potion.DispIdFull, 0, potion.Amount, potion.X, potion.Y,
                    potion.ContainedIn.Value, potion.Hue, _netState.IsClientPost6017));
        }
        else if (_triggerDispatcher?.FireItemTrigger(potion, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = potion }) != TriggerResult.True)
        {
            _world.RemoveItem(potion);
        }

        // Source-X Use_Drink returns the empty container (m_ttDrink.m_ridEmpty,
        // script TDATA1: i_bottle_empty for potions, the empty pitcher for a
        // water pitcher) — previously the container simply vanished.
        ushort emptyId = Item.ResolveTDataId(drinkDef?.TData1 ?? 0, drinkDef?.TData1Name);
        if (emptyId != 0)
        {
            var empty = _world.CreateItem();
            empty.BaseId = emptyId;
            if (drinkContainer != null && drinkContainer.TryAddItem(empty))
            {
                _netState.Send(new PacketContainerItem(
                    empty.Uid.Value, empty.DispIdFull, 0, empty.Amount, empty.X, empty.Y,
                    drinkContainer.Uid.Value, empty.Hue, _netState.IsClientPost6017));
            }
            else
            {
                _world.PlaceItemWithDecay(empty, drinkPos);
            }
        }
    }

    /// <summary>Resolve the spell a drink conveys (Source-X m_itPotion.m_Type):
    /// numeric MORE1, the MORE1_DEFNAME routing tag (s_heal, ...), or — for
    /// old-system bottles — the legacy POTION_TYPE tag mapped to its spell.</summary>
    private SpellType ResolveDrinkSpell(Item potion)
    {
        if (potion.More1 is > 0 and < 1000)
            return (SpellType)potion.More1;

        string? name = null;
        if (potion.TryGetTag("MORE1_DEFNAME", out string? m1d) && !string.IsNullOrWhiteSpace(m1d))
            name = m1d;
        else if (potion.TryGetTag("POTION_TYPE", out string? pt))
            name = pt?.ToLowerInvariant() switch
            {
                "heal" => "s_heal",
                "greatheal" => "s_greater_heal",
                "cure" => "s_cure",
                "strength" => "s_strength",
                "agility" => "s_agility",
                _ => null,
            };
        if (string.IsNullOrEmpty(name))
            return 0;

        var rid = _triggerDispatcher?.Resources?.ResolveDefName(name);
        if (rid is { IsValid: true, Type: ResType.SpellDef })
            return (SpellType)rid.Value.Index;
        return 0;
    }

    /// <summary>Handle UseSkill request (from packet 0x12 or extended command).</summary>
    public void HandleUseSkill(int skillId)
    {
        if (_character == null || _character.IsDead) return;
        if (skillId < 0 || skillId >= SkillEngine.BaseSkillCount) return;

        var skill = (SkillType)skillId;
        if (!SkillHandlers.IsClientUsable(skill))
        {
            SysMessage("You cannot use that skill directly.");
            return;
        }

        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) ||
            _character.IsCasting)
        {
            SysMessage("You must wait to perform another action.");
            return;
        }
        if (!SkillHandlers.CanUse(_character, skill))
            return;

        int menuSkill = _character.TryGetTag("SKILL_MENU_PENDING", out string? menuSkillText) &&
            int.TryParse(menuSkillText, out int parsedMenuSkill) ? parsedMenuSkill : -1;
        int currentSkill = _character.HasActiveSkillPending()
            ? _character.SkillPendingId
            : _pendingCraftRecipe != null ? (int)_pendingCraftSkill
            : Targets.SkillCancelId >= 0 ? Targets.SkillCancelId
            : menuSkill;
        TriggerResult waitResult = TriggerResult.Default;
        if (currentSkill >= 0)
        {
            var waitArgs = new TriggerArgs { CharSrc = _character, N1 = skillId, N2 = currentSkill };
            waitResult = _triggerDispatcher?.FireCharTrigger(
                _character, CharTrigger.SkillWait, waitArgs) ?? TriggerResult.Default;
            if (waitResult == TriggerResult.True)
                return;
        }

        if (currentSkill >= 0)
        {
            bool cancelCurrent = waitResult == TriggerResult.False ||
                (currentSkill != skillId && (SkillType)currentSkill is
                    SkillType.Meditation or SkillType.Hiding or SkillType.Stealth);
            if (!cancelCurrent)
            {
                SysMessage("You must wait to perform another action.");
                return;
            }

            if (_character.HasActiveSkillPending())
            {
                int aborted = _character.ClearActiveSkillPending();
                if (aborted >= 0)
                    Character.ActiveSkillAborted?.Invoke(_character, aborted);
            }
            CancelPendingCraft(notify: false);
            if (Targets.SkillCancelId >= 0)
            {
                int cancelledTargetSkill = Targets.SkillCancelId;
                _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
                Targets.Clear();
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = cancelledTargetSkill });
            }
            if (menuSkill >= 0)
            {
                _character.RemoveTag("SKILL_MENU_PENDING");
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = _character, N1 = menuSkill });
            }
        }

        if (_character.IsStatFlag(StatFlag.Meditation) && skill != SkillType.Meditation)
            _character.InterruptMeditation();

        if (skill is not (SkillType.Stealth or SkillType.Snooping or SkillType.Stealing))
            _character.ClearHiddenState();

        // Opening a craft menu is selection/UI. The chosen recipe owns the
        // single SkillSelect/PreStart/Start/Stroke/Success chain.
        if (!SkillEngine.HasFlag(skill, SkillFlag.Scripted) && SkillHandlers.IsCraftSkill(skill))
        {
            _character.Act = Serial.Invalid;
            _character.ActPrv = Serial.Invalid;
            _character.ActP = _character.Position;
            _skillHandlers?.UseSkill(_character, skill);
            return;
        }

        var selectResult = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillSelect,
            new TriggerArgs { CharSrc = _character, N1 = skillId }) ?? TriggerResult.Default;
        if (selectResult == TriggerResult.True)
            return;

        // A fresh client action must not inherit object/point state from the
        // previous skill (notably Camping, Poisoning and bard multi-targets).
        _character.Act = Serial.Invalid;
        _character.ActPrv = Serial.Invalid;
        _character.ActP = _character.Position;

        // SKF_SCRIPTED overrides the native implementation even for a built-in
        // skill id. A prompt makes it targeted; otherwise it resolves at once.
        if (SkillEngine.HasFlag(skill, SkillFlag.Scripted))
        {
            BeginActiveSkill(skill, skillId, SkillHandlers.GetActiveSkillTarget(skill));
            return;
        }

        // Source-X parity: information skills prompt for a target before emitting
        // any message. Route through the info-skill pipeline in BeginInfoSkill so
        // the player sees the exact CClientTarg.cpp text sequence.
        if (SkillHandlers.IsInfoSkill(skill))
        {
            BeginInfoSkill(skill, skillId);
            return;
        }

        // Active skills with parity coverage in ActiveSkillEngine.
        var activeKind = SkillHandlers.GetActiveSkillTarget(skill);
        if (activeKind != SkillHandlers.ActiveSkillTargetKind.Unsupported)
        {
            BeginActiveSkill(skill, skillId, activeKind);
            return;
        }

        // Fire @SkillPreStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var preResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (preResult == TriggerResult.True)
                return;
        }

        // Fire @SkillStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (result == TriggerResult.True)
                return;
        }

        // Fire @SkillStroke — the main action moment
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = skillId });

        bool success = _skillHandlers?.UseSkill(_character, skill) ?? false;

        // Fire @SkillSuccess or @SkillFail
        if (_triggerDispatcher != null)
        {
            var trigger = success ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
            _triggerDispatcher.FireCharTrigger(_character, trigger,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
        }

        if (success)
            SysMessage(ServerMessages.GetFormatted("skill_use_ok", skill));
        else
            SysMessage(ServerMessages.GetFormatted("skill_use_fail", skill));
    }

    /// <summary>Handle extended command (0xBF sub-commands).</summary>
    public void HandleExtendedCommand(ushort subCmd, byte[] data)
    {
        if (s_extendedCommandHandlers.TryGetValue(subCmd, out var handler))
            handler(this, data);
    }

    /// <summary>0xBF 0x33 — wheel-boat steering (Source-X PacketWheelBoatMove /
    /// SetPilot). Payload: serial(4), moving dir, facing dir, speed
    /// (0 = stop, 1 = one tile, 2+ = continuous). The steering char must be
    /// aboard; an assigned pilot excludes everyone else at the wheel.</summary>
    private void HandleExtendedWheelBoatMove(byte[] data)
    {
        if (_character == null || data.Length < 7) return;
        var engine = Item.ResolveShipEngine?.Invoke();
        if (engine == null) return;

        var ship = engine.FindShipAt(_character.Position);
        if (ship == null) return;
        if (ship.Pilot != _character.Uid) return;

        byte dir = (byte)(data[4] & 0x07);
        byte speed = data[6];
        if (speed == 0)
            engine.Stop(ship);
        else
            engine.SetMoveDir(ship, (Direction)dir,
                speed >= 2 ? ShipMovementType.Normal : ShipMovementType.OneTile,
                wheelMove: true);
    }

    private static IReadOnlyDictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>> BuildExtendedCommandHandlers()
    {
        var handlers = new Dictionary<ushort, Action<ClientWorldFeaturesHandler, byte[]>>
        {
            [0x0005] = static (client, data) => client.HandleExtendedScreenSize(data),
            [0x0006] = static (client, data) => client.HandleExtendedParty(data),
            [0x0007] = static (client, _) => client.FireExtendedButtonTrigger(CharTrigger.UserQuestArrowClick, 0x0007),
            [0x0009] = static (client, _) => client.HandleWrestleSpecialMove(0x05), // Wrestle Disarm
            [0x000A] = static (client, _) => client.HandleWrestleSpecialMove(0x0B), // Wrestle Stun (Paralyzing Blow)
            [0x000B] = static (client, data) =>
            {
                if (data.Length >= 3)
                    client._netState.ClientLanguage = System.Text.Encoding.ASCII.GetString(data, 0, 3);
                client.FireExtendedButtonTrigger(CharTrigger.UserChatButton, 0x000B);
            },
            [0x0013] = static (client, data) => client.HandleExtendedContextMenuRequest(data),
            [0x0015] = static (client, data) => client.HandleExtendedContextMenuResponse(data),
            [0x001A] = static (client, data) => client.HandleExtendedStatLock(data),
            [0x001C] = static (client, data) => client.HandleSpellSelect(data),
            [0x001E] = static (client, data) => client.HandleQueryDesignDetails(data),
            [0x0024] = static (client, _) => client.FireExtendedButtonTrigger(CharTrigger.UserKRToolbar, 0x0024),
            [0x002C] = static (client, data) => client.HandleBandageMacro(data),
            [0x002E] = static (client, data) => client.HandleTargetedSkill(data),
            [0x0032] = static (client, data) => client.HandleGargoyleFly(data),
            [0x0033] = static (client, data) => client.HandleExtendedWheelBoatMove(data),
        };

        if (handlers.Keys.Any(subCmd => !ExtendedCommandRegistry.IsKnown(subCmd)) ||
            ExtendedCommandRegistry.KnownSubCommands.Any(subCmd => !handlers.ContainsKey(subCmd)))
            throw new InvalidOperationException("0xBF extended command handlers drifted from ExtendedCommandRegistry.");

        return handlers;
    }

    private void HandleExtendedStatLock(byte[] data)
    {
        if (data.Length < 2 || _character == null)
            return;

        if (data[0] > 2 || data[1] > 2)
            return;

        _character.SetStatLock(data[0], data[1]);
    }

    private long _lastContextMenuRequestMs;
    private long _nextPotionTimeMs;

    private void HandleExtendedContextMenuRequest(byte[] data)
    {
        if (data.Length < 4)
            return;

        long now = Environment.TickCount64;
        if (now - _lastContextMenuRequestMs < 500)
            return;
        _lastContextMenuRequestMs = now;

        uint targetSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        SendContextMenu(targetSerial);
    }

    private void HandleExtendedContextMenuResponse(byte[] data)
    {
        if (data.Length < 6)
            return;

        uint respSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        ushort entryTag = (ushort)((data[4] << 8) | data[5]);
        HandleContextMenuResponse(respSerial, entryTag);
    }

    private void HandleExtendedParty(byte[] data)
    {
        if (data.Length >= 1)
            HandlePartyCommand(data);
    }

    private void HandleExtendedScreenSize(byte[] data)
    {
        if (data.Length < 8 || _character == null)
            return;

        ushort width = (ushort)((data[4] << 8) | data[5]);
        ushort height = (ushort)((data[6] << 8) | data[7]);
        _netState.ScreenWidth = width;
        _netState.ScreenHeight = height;
        _character.SetScreenSize(width, height);
    }

    // 0xBF 0x1C — cast a spell selected from the client UI/macro
    // (Source-X PacketSpellSelect, receive.cpp:3087). Payload is a 2-byte skip
    // then the 1-based spell id. The modern client (>= 6.0.1.42) uses this;
    // older clients cast via the 0x12/0x56 text command, handled elsewhere.
    // (This slot previously mis-handled viewport size, which the client actually
    // reports via 0xBF 0x05 / 0xC8 — the duplicate handler was dead.)
    private void HandleSpellSelect(byte[] data)
    {
        if (data.Length < 4 || _character == null)
            return;

        int spellId = (data[2] << 8) | data[3];
        if (spellId > 0)
            _client.HandleCastSpell((SpellType)spellId, 0);
    }

    /// <summary>0xF4 crash report → @UserBugReport.</summary>
    public void HandleCrashReport() =>
        FireExtendedButtonTrigger(CharTrigger.UserBugReport, 0x00F4);

    /// <summary>Stateless client UI button packets: 0xFA Ultima Store,
    /// 0xB5 chat window open.</summary>
    public void HandleClientUiButton(byte opcode)
    {
        switch (opcode)
        {
            case 0xFA:
                FireExtendedButtonTrigger(CharTrigger.UserUltimaStoreButton, opcode);
                break;
            case 0xB5:
                FireExtendedButtonTrigger(CharTrigger.UserGlobalChatButton, opcode);
                HandleChatOpen();
                break;
        }
    }

    private void FireExtendedButtonTrigger(CharTrigger trigger, ushort subCmd)
    {
        if (_character == null)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, trigger,
            new TriggerArgs { CharSrc = _character, N1 = subCmd });
    }

    /// <summary>0xBF 0x32 — Source-X GargoyleFly (receive.cpp:3329): a living
    /// gargoyle carrying the RACIALF_GARG_FLY racial toggles hovering flight.
    /// Flip STATF_HOVERING and the BI_GARGOYLEFLY buff, then resend the mobile so
    /// the flight state rides the MobileFlags 0x04 bit to the player and every
    /// nearby observer.</summary>
    private void HandleGargoyleFly(byte[] data)
    {
        if (_character == null || _character.IsDead || !_character.IsGargoyle)
            return;
        if ((Character.RacialFlags & (int)RacialFlags.GargoyleFly) == 0)
            return;

        bool flying = !_character.IsStatFlag(StatFlag.Hovering);
        if (flying)
            _character.SetStatFlag(StatFlag.Hovering);
        else
            _character.ClearStatFlag(StatFlag.Hovering);

        Character.OnClientBuffChanged?.Invoke(_character, BuffIcon.GargoyleFly, flying, 0, null);

        _client.SendSelfRedraw();
        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        var moving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z,
            (byte)((byte)_character.Direction & 0x07), _character.Hue, flags, noto);
        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby(_character.Position, UpdateRange, moving, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, moving, _character.Uid.Value);
    }

    /// <summary>0xBF 0x09/0x0A — pre-AOS wrestling special moves (disarm/stun).
    /// Source-X routes both through Event_CombatAbilitySelect, which fires
    /// @UserSpecialMove with the ability id (0x5 disarm / 0xB paralyzing blow),
    /// the same sink as the AOS special-move packet (0xD7 0x19).</summary>
    private void HandleWrestleSpecialMove(int ability)
    {
        if (_character == null)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserSpecialMove,
            new TriggerArgs { CharSrc = _character, N1 = ability });
    }

    /// <summary>Virtue invocation — Source-X EXTCMD_INVOKE_VIRTUE, which rides the
    /// 0x12 text-command packet as ext-type 0xF4 (CClientEvent.cpp:3127), NOT 0xBF
    /// 0x2C (that is the bandage macro). Fires @UserVirtueInvoke with the virtue id
    /// (1=Honor, 2=Sacrifice, 3=Valor) in N1, matching the reference m_iN1 =
    /// iVirtueID. Distinct from the virtue-gump select (@UserVirtue /
    /// Event_VirtueSelect), which is the 0xB1 dialog path.</summary>
    internal void HandleVirtueInvoke(int virtueId)
    {
        if (_character == null || virtueId <= 0)
            return;

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserVirtueInvoke,
            new TriggerArgs { CharSrc = _character, N1 = virtueId });
    }

    // 0xBF 0x2E — TargetedSkill (Source-X PacketTargetedSkill, receive.cpp:3280):
    // [word skillId][dword targetUID]. Use the skill against the pre-selected
    // target with no cursor round-trip. skillId 0 ("last skill") is not tracked
    // and is ignored. Runs the same busy/can-use gates as HandleUseSkill.
    private void HandleTargetedSkill(byte[] data)
    {
        if (_character == null || _character.IsDead || data.Length < 6)
            return;

        int skillId = (data[0] << 8) | data[1];
        uint targetSerial = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        if (skillId <= 0 || skillId >= SkillEngine.BaseSkillCount)
            return;

        var skill = (SkillType)skillId;
        if (!SkillHandlers.IsClientUsable(skill))
            return;
        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) || _character.IsCasting)
            return;
        if (!SkillHandlers.CanUse(_character, skill))
            return;

        BeginTargetedSkill(skill, skillId, new Serial(targetSerial));
    }

    // 0xBF 0x2C — BandageMacro (Source-X PacketBandageMacro, receive.cpp:3196):
    // [dword bandageUID][dword targetUID]. The client's bandage hotkey double-
    // clicks the bandage and applies it to the pre-selected target with no cursor
    // round-trip. Mirror the reference gates: the item must be a bandage and the
    // caster must be able to use Healing; the Healing pipeline then validates the
    // target and consumes a bandage from the pack.
    private void HandleBandageMacro(byte[] data)
    {
        if (_character == null || _character.IsDead || data.Length < 8)
            return;

        uint bandageUid = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        uint targetUid = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);

        var bandage = _world.FindItem(new Serial(bandageUid));
        if (bandage == null || bandage.ItemType != ItemType.Bandage)
            return;
        if (_character.IsStatFlag(StatFlag.Sleeping | StatFlag.Freeze | StatFlag.Stone) || _character.IsCasting)
            return;
        if (!SkillHandlers.CanUse(_character, SkillType.Healing))
            return;

        BeginTargetedSkill(SkillType.Healing, (int)SkillType.Healing, new Serial(targetUid));
    }

    /// <summary>
    /// Handle party sub-commands (0xBF sub 0x0006).
    /// Sub-types: 1=Add, 2=Remove, 3=PrivateMsg, 4=PublicMsg, 6=SetLoot, 8=Accept, 9=Decline.
    /// </summary>
    private void HandlePartyCommand(byte[] data)
    {
        if (_character == null || _partyManager == null) return;
        byte partyCmd = data[0];

        switch (partyCmd)
        {
            case 1: // Add member (invite)
                if (data.Length >= 5)
                {
                    uint targetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var target = _world.FindChar(new Serial(targetUid));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (target.Uid == _character.Uid) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }

                    var existingParty = _partyManager.FindParty(_character.Uid);
                    if (existingParty != null && existingParty.IsFull) { SysMessage(ServerMessages.Get("party_is_full")); return; }
                    if (existingParty != null && existingParty.Master != _character.Uid)
                    { SysMessage(ServerMessages.Get("party_notleader")); return; }
                    if (_partyManager.FindParty(target.Uid) != null)
                    { SysMessage(ServerMessages.Get("party_join_failed")); return; }

                    // Fire @PartyInvite trigger on target
                    if (_triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
                        new TriggerArgs { CharSrc = _character }) == TriggerResult.True)
                        return;

                    // Store pending invite and send invite packet to target
                    target.SetTag("PARTY_INVITE_FROM", _character.Uid.Value.ToString());
                    target.SetTag("PARTY_INVITE_TIME", Environment.TickCount64.ToString());
                    SendToChar?.Invoke(target.Uid, new PacketPartyInvitation(_character.Uid.Value));
                    SysMessage(ServerMessages.GetFormatted("party_invite", target.Name));
                }
                break;

            case 2: // Remove member
                if (data.Length >= 5)
                {
                    uint removeUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party == null) break;
                    var removeSerial = new Serial(removeUid);
                    if (!party.IsMember(removeSerial))
                    {
                        SysMessage(ServerMessages.Get("msg_invalid_target"));
                        break;
                    }
                    if (party.Master != _character.Uid && removeSerial != _character.Uid)
                    {
                        SysMessage(ServerMessages.Get("party_notleader"));
                        break;
                    }
                    // Fire @PartyRemove trigger on removed member
                    var removedChar = _world.FindChar(new Serial(removeUid));
                    if (removedChar != null)
                        _triggerDispatcher?.FireCharTrigger(removedChar, CharTrigger.PartyRemove,
                            new TriggerArgs { CharSrc = _character });

                    // Snapshot members before the leave, which may disband the
                    // party (drops to a single member).
                    var membersBefore = party.Members.ToList();
                    _partyManager.Leave(removeSerial);
                    SysMessage(ServerMessages.GetFormatted("party_leave_1",
                        removedChar?.Name ?? "A member"));

                    if (party.MemberCount == 0)
                    {
                        // Party disbanded — tell every former member to clear
                        // their party UI, instead of broadcasting an empty list.
                        var emptyList = Array.Empty<uint>();
                        foreach (var formerUid in membersBefore)
                        {
                            SendToChar?.Invoke(formerUid,
                                new PacketPartyRemoveMember(formerUid.Value, emptyList));
                            // Clear every other former member's radar waypoint pin
                            // from this member's map now that the party is gone.
                            foreach (var otherUid in membersBefore)
                                if (otherUid != formerUid)
                                    SendToChar?.Invoke(formerUid, new PacketWaypointRemove(otherUid.Value));
                            // @PartyDisband (Source-X) — fires on each former member.
                            var formerChar = _world.FindChar(formerUid);
                            if (formerChar != null)
                                _triggerDispatcher?.FireCharTrigger(formerChar, CharTrigger.PartyDisband,
                                    new TriggerArgs { CharSrc = formerChar });
                        }
                    }
                    else
                    {
                        BroadcastPartyUpdate(party, new Serial(removeUid));
                    }
                }
                break;

            case 3: // Private party message
                if (data.Length >= 5)
                {
                    uint pmTargetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    string pmMsg = data.Length > 5
                        ? System.Text.Encoding.BigEndianUnicode.GetString(data, 5, data.Length - 5).TrimEnd('\0')
                        : "";
                    if (!string.IsNullOrEmpty(pmMsg))
                    {
                        var party = _partyManager.FindParty(_character.Uid);
                        var targetUid = new Serial(pmTargetUid);
                        if (party == null || !party.IsMember(targetUid))
                        {
                            SysMessage(ServerMessages.Get("party_join_failed"));
                            break;
                        }
                        SendToChar?.Invoke(targetUid,
                            new PacketPartyMessage(_character.Uid.Value, pmMsg, isPrivate: true));
                        SysMessage(ServerMessages.GetFormatted("party_msg", $"{pmTargetUid:X}", pmMsg));
                    }
                }
                break;

            case 4: // Public party message
                if (data.Length >= 2)
                {
                    string msg = System.Text.Encoding.BigEndianUnicode.GetString(data, 1, data.Length - 1).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(msg)) break;
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null)
                    {
                        var chatPacket = new PacketPartyMessage(_character.Uid.Value, msg);
                        foreach (var memberUid in party.Members)
                            SendToChar?.Invoke(memberUid, chatPacket);
                    }
                }
                break;

            case 6: // Set loot flag
                if (data.Length >= 2)
                {
                    bool canLoot = data[1] != 0;
                    var party = _partyManager.FindParty(_character.Uid);
                    party?.SetLootFlag(_character.Uid, canLoot);
                    SysMessage(canLoot ? "Party loot sharing enabled." : "Party loot sharing disabled.");
                }
                break;

            case 8: // Accept invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? inviterStr) &&
                    uint.TryParse(inviterStr, out uint inviterUid) &&
                    _character.TryGetTag("PARTY_INVITE_TIME", out string? inviteTimeStr) &&
                    long.TryParse(inviteTimeStr, out long inviteTime))
                {
                    _character.RemoveTag("PARTY_INVITE_FROM");
                    _character.RemoveTag("PARTY_INVITE_TIME");
                    var inviterSerial = new Serial(inviterUid);
                    var inviter = _world.FindChar(inviterSerial);
                    var inviterParty = _partyManager.FindParty(inviterSerial);
                    long now = Environment.TickCount64;
                    bool validInvite = inviteTime >= 0 && inviteTime <= now && now - inviteTime <= 120_000 &&
                        inviter != null && inviter.IsPlayer && !inviter.IsDeleted && !inviter.IsDead &&
                        (inviterParty == null || inviterParty.Master == inviterSerial);
                    // Honour AcceptInvite's result — it fails if already partied
                    // or the inviter's party is gone. Don't claim success blindly.
                    if (validInvite && _partyManager.AcceptInvite(inviterSerial, _character.Uid))
                    {
                        SysMessage(ServerMessages.Get("party_added"));
                        var party = _partyManager.FindParty(_character.Uid);
                        if (party != null) BroadcastPartyUpdate(party);
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get("party_join_failed"));
                    }
                }
                else
                {
                    _character.RemoveTag("PARTY_INVITE_FROM");
                    _character.RemoveTag("PARTY_INVITE_TIME");
                    SysMessage(ServerMessages.Get("party_join_failed"));
                }
                break;
            }

            case 9: // Decline invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? declineInviterStr) &&
                    uint.TryParse(declineInviterStr, out uint declineInviterUid))
                {
                    // Notify the inviter their invitation was declined. Previously this
                    // sent a null packet (null!) — a NullReferenceException waiting in the
                    // send pipeline. party_decline_1: "<name>: Does not wish to join the party."
                    string declineNote = ServerMessages.GetFormatted("party_decline_1", _character.Name ?? "Someone");
                    SendToChar?.Invoke(new Serial(declineInviterUid),
                        new PacketSpeechUnicodeOut(0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", declineNote));
                }
                _character.RemoveTag("PARTY_INVITE_FROM");
                _character.RemoveTag("PARTY_INVITE_TIME");
                SysMessage(ServerMessages.GetFormatted("party_decline_2",
                    (declineInviterStr != null && uint.TryParse(declineInviterStr, out uint dn)
                        ? _world.FindChar(new Serial(dn))?.Name : null) ?? "them"));
                break;
            }
        }
    }

    /// <summary>Send party member list update to all members.</summary>
    private void BroadcastPartyUpdate(PartyDef party, Serial? removedMember = null)
    {
        var memberSerials = party.Members.Select(m => m.Value).ToArray();
        if (removedMember.HasValue)
        {
            var removePacket = new PacketPartyRemoveMember(removedMember.Value.Value, memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, removePacket);
            SendToChar?.Invoke(removedMember.Value, removePacket);

            // Clear the departed member's radar waypoint pin from every remaining
            // member's map, and clear all remaining members' pins from the departed
            // member's own map. PushPartyStats re-broadcasts live pins each tick.
            var dropDeparted = new PacketWaypointRemove(removedMember.Value.Value);
            foreach (var memberUid in party.Members)
            {
                SendToChar?.Invoke(memberUid, dropDeparted);
                SendToChar?.Invoke(removedMember.Value, new PacketWaypointRemove(memberUid.Value));
            }
        }
        else
        {
            var listPacket = new PacketPartyMemberList(memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, listPacket);
        }
    }

    internal void SendContextMenu(uint targetSerial)
    {
        if (_character == null) return;

        var ch = _world.FindChar(new Serial(targetSerial));
        var item = ch == null ? _world.FindItem(new Serial(targetSerial)) : null;
        if (ch != null && (ch.MapIndex != _character.MapIndex ||
            _character.Position.GetDistanceTo(ch.Position) > 12))
            return;
        if (item != null && !item.ContainedIn.IsValid && (item.Position.Map != _character.MapIndex ||
            _character.Position.GetDistanceTo(item.Position) > 3))
            return;

        var entries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();
        var scriptEntries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();
        _client.ScriptContextEntries = scriptEntries;

        if (ch != null)
        {
            FireContextMenuTrigger(ch, CharTrigger.ContextMenuRequest, 0);
            entries.Add((1, 3006123, 0)); // Open Paperdoll
            if (ch == _character)
            {
                entries.Add((2, 3006145, 0)); // Open Backpack
            }
            if (VendorEngine.IsVendorLike(ch))
            {
                entries.Add((3, 3006103, 0)); // Buy
                entries.Add((4, 3006106, 0)); // Sell
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Banker)
            {
                entries.Add((5, 3006105, 0)); // Open Bankbox
            }
            // Mount / Dismount: exposed as a context-menu action so the client
            // does not require a DoubleClick to saddle. Double-click remains
            // equivalent. Entry is filtered by IsMountable so non-ridable
            // mobs (monsters, humans) don't get a useless "Mount Me" line.
            if (!ch.IsPlayer && ch != _character &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                entries.Add((6, 3006155, 0)); // Mount Me
            }
            if (ch == _character && _character.IsMounted)
            {
                entries.Add((7, 3006112, 0)); // Dismount
            }
        }
        else if (item != null)
        {
            FireContextMenuTrigger(item, ItemTrigger.ContextMenuRequest, 0);
        }

        entries.AddRange(scriptEntries);
        _client.ScriptContextEntries = null;

        if (entries.Count > 0)
            _netState.Send(new PacketContextMenu(targetSerial, entries.ToArray()));
    }

    internal void HandleContextMenuResponse(uint targetSerial, ushort entryTag)
    {
        if (_character == null) return;

        var charTarget = _world.FindChar(new Serial(targetSerial));
        if (charTarget != null && (charTarget.MapIndex != _character.MapIndex ||
            _character.Position.GetDistanceTo(charTarget.Position) > 12))
            return;
        var itemObj = charTarget == null ? _world.FindItem(new Serial(targetSerial)) : null;
        if (itemObj != null && !itemObj.ContainedIn.IsValid && (itemObj.Position.Map != _character.MapIndex ||
            _character.Position.GetDistanceTo(itemObj.Position) > 3))
            return;

        var target = charTarget;
        if (target != null)
            FireContextMenuTrigger(target, CharTrigger.ContextMenuSelect, entryTag);
        else if (_world.FindItem(new Serial(targetSerial)) is { } itemTarget)
            FireContextMenuTrigger(itemTarget, ItemTrigger.ContextMenuSelect, entryTag);

        switch (entryTag)
        {
            case 1: // Open Paperdoll
                if (target != null) SendPaperdoll(target);
                break;
            case 2: // Open Backpack
                if (_character.Backpack != null)
                    SendOpenContainer(_character.Backpack);
                break;
            case 3: // Buy
                var vendor = _world.FindChar(new Serial(targetSerial));
                if (vendor != null) HandleVendorInteraction(vendor);
                break;
            case 4: // Sell
                if (_world.FindChar(new Serial(targetSerial)) is { } sellVendor &&
                    VendorEngine.IsVendorLike(sellVendor))
                {
                    _client.OpenVendorSell(sellVendor);
                }
                break;
            case 5: // Open Bankbox
            {
                var banker = _world.FindChar(new Serial(targetSerial));
                if (banker != null && banker.NpcBrain == NpcBrainType.Banker &&
                    _character.Position.GetDistanceTo(banker.Position) <= 3 &&
                    _character.MapIndex == banker.MapIndex)
                {
                    OpenBankBox();
                }
                break;
            }
            case 6: // Mount Me
                HandleDoubleClick(targetSerial);
                break;
            case 7: // Dismount
            {
                if (_character.IsMounted && _mountEngine != null)
                {
                    uint oldMountItemUid = _character.GetEquippedItem(Core.Enums.Layer.Horse)?.Uid.Value ?? 0;
                    BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                        new PacketSound(0x0140, _character.X, _character.Y, _character.Z), 0);
                    var npc = DismountCharacter();

                    // Re-seat via the shared standing resolver (audit design;
                    // multi decks / house floors included).
                    var wfStand = _world.Standing.ResolveStandingSurface(_character,
                        _character.MapIndex, _character.X, _character.Y, _character.Z,
                        SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
                    if (wfStand.Found && wfStand.Z != _character.Z)
                        _character.Position = new Point3D(_character.X, _character.Y, wfStand.Z, _character.MapIndex);

                    if (oldMountItemUid != 0)
                        BroadcastDeleteObject(oldMountItemUid);

                    ResetWalkValidator();
                    _netState.WalkSequence = 0;
                    _netState.SendPriority(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    byte flags = BuildMobileFlags(_character);
                    byte dir77 = (byte)((byte)_character.Direction & 0x07);
                    byte noto = GetNotoriety(_character);
                    var movePacket = new PacketMobileMoving(
                        _character.Uid.Value, _character.BodyId,
                        _character.X, _character.Y, _character.Z, dir77,
                        _character.Hue, flags, noto);
                    _netState.Send(movePacket);
                    if (BroadcastMoveNearby != null)
                        BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
                    else
                        BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

                    if (npc != null)
                    {
                        npc.ClearStatFlag(Core.Enums.StatFlag.Ridden);
                        BroadcastCharacterAppear?.Invoke(npc);
                    }
                }
                break;
            }
        }
    }

    private void FireContextMenuTrigger(Character target, CharTrigger trigger, ushort entryTag)
    {
        _triggerDispatcher?.FireCharTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = _character,
            N1 = entryTag
        });
    }

    private void FireContextMenuTrigger(Item target, ItemTrigger trigger, ushort entryTag)
    {
        _triggerDispatcher?.FireItemTrigger(target, trigger, new TriggerArgs
        {
            CharSrc = _character,
            ItemSrc = target,
            N1 = entryTag
        });
    }

    // ==================== Single Click ====================
}

using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Script trigger arguments passed to trigger handlers.
/// Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public sealed class TriggerArgs
{
    public int N1 { get; set; }
    public int N2 { get; set; }
    public int N3 { get; set; }
    public string S1 { get; set; } = "";
    public IScriptObj? O1 { get; set; }
    public ITextConsole? ScriptConsole { get; set; }

    // Convenience typed references
    public Character? CharSrc { get; set; }
    public Item? ItemSrc { get; set; }
}

/// <summary>
/// Trigger dispatcher. Routes CTRIG/ITRIG events through the Source-X trigger chain:
/// 
/// Character trigger order:
///   1. @Char* on source char (cross-target)
///   2. EVENTS (dynamic event scripts on this char)
///   3. TEVENTS (from CHARDEF)
///   4. CHARDEF (body definition)
///   5. EVENTSPET / EVENTSPLAYER (global config)
///
/// Item trigger order:
///   1. @Item* on source char
///   2. EVENTS (dynamic event scripts on this item)
///   3. TEVENTS (from ITEMDEF)
///   4. EVENTSITEM (global config)
///   5. TYPEDEF (item type behavior)
///   6. ITEMDEF (base definition)
/// </summary>
public sealed class TriggerDispatcher
{
    /// <summary>
    /// Delegate for trigger handlers registered by scripts.
    /// </summary>
    public delegate TriggerResult TriggerHandler(IScriptObj obj, TriggerArgs args);

    private readonly Dictionary<string, List<TriggerHandler>> _globalCharHandlers = [];
    private readonly Dictionary<string, List<TriggerHandler>> _globalItemHandlers = [];
    private readonly Dictionary<string, List<TriggerHandler>> _typeDefHandlers = [];

    /// <summary>Optional resource holder for resolving CHARDEF/ITEMDEF scripts.</summary>
    public ResourceHolder? Resources { get; set; }

    /// <summary>Optional trigger runner for executing script blocks.</summary>
    public TriggerRunner? Runner { get; set; }

    public bool ScriptDebug { get; set; }
    public Action<string>? DebugLog { get; set; }

    /// <summary>
    /// Fire a character trigger. Maps to CChar::OnTrigger.
    /// </summary>
    public TriggerResult FireCharTrigger(Character ch, CharTrigger trigger, TriggerArgs args)
    {
        string trigName = GetCharTriggerName(trigger);

        if (ScriptDebug)
            DebugLog?.Invoke($"[script_debug] CTRIG @{trigName} on char 0x{ch.Uid.Value:X8} '{ch.Name}' src={args.CharSrc?.Name ?? "-"}");

        // 1. @Char* on source character (cross-target triggers)
        if (args.CharSrc != null && args.CharSrc != ch)
        {
            string crossTrigName = "char" + trigName;
            var result = RunObjectHandlers(args.CharSrc, crossTrigName, args);
            if (result == TriggerResult.True)
            {
                if (ScriptDebug)
                    DebugLog?.Invoke($"[script_debug]   @{crossTrigName} returned TRUE (blocked)");
                return TriggerResult.True;
            }
        }

        // 2. Dynamic EVENTS on this character
        var evResult = RunObjectHandlers(ch, trigName, args);
        if (evResult == TriggerResult.True)
        {
            if (ScriptDebug)
                DebugLog?.Invoke($"[script_debug]   EVENTS @{trigName} returned TRUE (blocked)");
            return TriggerResult.True;
        }

        // 3. TEVENTS from CHARDEF definition (type-level event scripts)
        if (Resources != null && Runner != null)
        {
            var charDef = Definitions.DefinitionLoader.GetCharDef(ch.CharDefIndex);
            if (charDef != null)
            {
                foreach (var tevRid in charDef.Events)
                {
                    var tevLink = Resources.GetResource(tevRid);
                    if (tevLink == null) continue;
                    var result = Runner.RunTriggerByName(tevLink, trigName, ch, args.ScriptConsole, WrapArgs(args));
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // 4. CHARDEF own triggers (ON=@Trigger in the CHARDEF body)
            var charDefLink = Resources.GetResource(ResType.CharDef, ch.CharDefIndex);
            if (charDefLink != null)
            {
                // CHARDEF links may not have trigger bitmasks precomputed, so resolve by name.
                var result = Runner.RunTriggerByName(charDefLink, trigName, ch, args.ScriptConsole, WrapArgs(args));
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        // 5. Global EVENTSPET / EVENTSPLAYER
        string globalKey = ch.IsPlayer ? "EVENTSPLAYER" : "EVENTSPET";
        if (_globalCharHandlers.TryGetValue(globalKey + "." + trigName, out var globalHandlers))
        {
            foreach (var handler in globalHandlers)
            {
                var result = handler(ch, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire an item trigger. Maps to CItem::OnTrigger.
    /// </summary>
    public TriggerResult FireItemTrigger(Item item, ItemTrigger trigger, TriggerArgs args)
    {
        string trigName = GetItemTriggerName(trigger);

        if (ScriptDebug)
            DebugLog?.Invoke($"[script_debug] ITRIG @{trigName} on item 0x{item.Uid.Value:X8} id=0x{item.BaseId:X4} src={args.CharSrc?.Name ?? "-"}");

        // 1. @Item* on source character
        if (args.CharSrc != null)
        {
            string charTrigName = "item" + trigName;
            var result = RunObjectHandlers(args.CharSrc, charTrigName, args);
            if (result == TriggerResult.True)
            {
                if (ScriptDebug)
                    DebugLog?.Invoke($"[script_debug]   @{charTrigName} returned TRUE (blocked)");
                return TriggerResult.True;
            }
        }

        // 2. Dynamic EVENTS on this item
        var evResult = RunObjectHandlers(item, trigName, args);
        if (evResult == TriggerResult.True)
        {
            if (ScriptDebug)
                DebugLog?.Invoke($"[script_debug]   EVENTS @{trigName} returned TRUE (blocked)");
            return TriggerResult.True;
        }

        // 3. TEVENTS from ITEMDEF definition
        if (Resources != null && Runner != null)
        {
            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (itemDef != null)
            {
                foreach (var tevRid in itemDef.Events)
                {
                    var tevLink = Resources.GetResource(tevRid);
                    if (tevLink == null) continue;
                    var result = Runner.RunTriggerByName(tevLink, trigName, item, args.ScriptConsole, WrapArgs(args));
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // ITEMDEF own triggers (resolved graphic's def).
            var itemDefLink = Resources.GetResource(ResType.ItemDef, item.BaseId);
            if (itemDefLink != null)
            {
                var result = Runner.RunTriggerByName(itemDefLink, trigName, item, args.ScriptConsole, WrapArgs(args));
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }

            // Named (scripted) ITEMDEF triggers — when [itemdef i_moongate]
            // sets id=i_moongate_blue, the item's BaseId becomes 0x0F6C
            // (the graphic) but the @DClick / @Step triggers live on the
            // i_moongate section itself, keyed by its string-hash index.
            // TryAddAtTarget stashes that index in TAG.SCRIPTDEF so the
            // dispatcher can route triggers back to the scripted def.
            if (item.TryGetTag("SCRIPTDEF", out string? scriptDefStr) &&
                int.TryParse(scriptDefStr, out int scriptDefIdx) &&
                scriptDefIdx != item.BaseId)
            {
                var scriptLink = Resources.GetResource(ResType.ItemDef, scriptDefIdx);
                if (scriptLink != null)
                {
                    var result = Runner.RunTriggerByName(scriptLink, trigName, item, args.ScriptConsole, WrapArgs(args));
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        // 4. Global EVENTSITEM
        if (_globalItemHandlers.TryGetValue("EVENTSITEM." + trigName, out var globalHandlers))
        {
            foreach (var handler in globalHandlers)
            {
                var result = handler(item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        // 5. TYPEDEF (item type behavior)
        string typeKey = item.ItemType.ToString();
        if (_typeDefHandlers.TryGetValue(typeKey + "." + trigName, out var typeHandlers))
        {
            foreach (var handler in typeHandlers)
            {
                var result = handler(item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        // 6. ITEMDEF (base definition script) — already covered by step 3

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire SPEECH triggers for an NPC hearing speech.
    /// Iterates the CharDef's SpeechResources list and runs the first matching pattern.
    /// </summary>
    public TriggerResult FireSpeechTrigger(Character npc, Character speaker, string text)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var charDef = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        if (charDef == null || charDef.SpeechResources.Count == 0)
            return TriggerResult.Default;

        var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, 0, 0, text)
        {
            Object1 = npc,
            Object2 = speaker
        };

        foreach (var speechRid in charDef.SpeechResources)
        {
            var link = Resources.GetResource(speechRid);
            if (link == null) continue;

            var result = Runner.RunSpeechTrigger(link, text, npc, null, args);
            if (result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire region event scripts for a given trigger name.
    /// Iterates the region's EVENTS list and runs matching triggers.
    /// </summary>
    public void FireRegionEvents(World.Regions.Region region, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null) return;

        foreach (var eventRid in region.Events)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;
            Runner.RunTriggerByName(link, trigName, ch, args.ScriptConsole, WrapArgs(args));
        }
    }

    /// <summary>
    /// Fire room event scripts for a given trigger name.
    /// Iterates the room's EVENTS list and runs matching triggers.
    /// </summary>
    public void FireRoomEvents(World.Regions.Room room, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null) return;

        foreach (var eventRid in room.Events)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;
            Runner.RunTriggerByName(link, trigName, ch, args.ScriptConsole, WrapArgs(args));
        }
    }

    /// <summary>
    /// Fire a trigger on a REGIONRESOURCE script (e.g. @ResourceGather, @ResourceTest).
    /// Runs ON=@TrigName blocks defined in the REGIONRESOURCE section.
    /// </summary>
    public TriggerResult FireResourceTrigger(RegionResourceDef resDef, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var link = Resources.GetResource(resDef.Id);
        if (link == null)
            return TriggerResult.Default;

        return Runner.RunTriggerByName(link, trigName, ch, args.ScriptConsole, WrapArgs(args));
    }

    /// <summary>Register a global character event handler.</summary>
    public void RegisterCharEvent(string eventKey, string trigName, TriggerHandler handler)
    {
        string key = eventKey + "." + trigName;
        if (!_globalCharHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _globalCharHandlers[key] = list;
        }
        list.Add(handler);
    }

    /// <summary>Register a global item event handler.</summary>
    public void RegisterItemEvent(string eventKey, string trigName, TriggerHandler handler)
    {
        string key = eventKey + "." + trigName;
        if (!_globalItemHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _globalItemHandlers[key] = list;
        }
        list.Add(handler);
    }

    /// <summary>Register a TYPEDEF handler.</summary>
    public void RegisterTypeDef(string itemType, string trigName, TriggerHandler handler)
    {
        string key = itemType + "." + trigName;
        if (!_typeDefHandlers.TryGetValue(key, out var list))
        {
            list = [];
            _typeDefHandlers[key] = list;
        }
        list.Add(handler);
    }

    /// <summary>
    /// Run handlers attached directly to an object (EVENTS list).
    /// Iterates the object's runtime Events list and runs matching triggers from EVENTS scripts.
    /// Also checks TAG.EVENT_* overrides for backward compat.
    /// </summary>
    private TriggerResult RunObjectHandlers(IScriptObj obj, string trigName, TriggerArgs args)
    {
        // Check TAG.EVENT_<trigName> override first
        if (obj.TryGetProperty("TAG.EVENT_" + trigName.ToUpperInvariant(), out string value))
        {
            if (value == "1")
                return TriggerResult.True;
        }

        // Run through the object's EVENTS list
        if (Resources != null && Runner != null)
        {
            IReadOnlyList<ResourceId>? events = obj switch
            {
                Character ch => ch.Events,
                Item item => item.Events,
                _ => null
            };

            if (events != null)
            {
                var snapshot = events.ToArray();
                foreach (var eventRid in snapshot)
                {
                    var eventLink = Resources.GetResource(eventRid);
                    if (eventLink == null) continue;

                    var result = Runner.RunTriggerByName(eventLink, trigName, obj, args.ScriptConsole, WrapArgs(args));
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        return TriggerResult.Default;
    }

    /// <summary>Convert TriggerArgs to ITriggerArgs for the script engine.</summary>
    private static SphereNet.Scripting.Execution.TriggerArgs WrapArgs(TriggerArgs args)
    {
        var wrapped = new SphereNet.Scripting.Execution.TriggerArgs(
            args.CharSrc ?? (IScriptObj?)args.ItemSrc,
            args.N1, args.N2, args.S1);
        wrapped.Object1 = args.O1;
        wrapped.Object2 = args.CharSrc ?? (IScriptObj?)args.ItemSrc;
        return wrapped;
    }


    // --- Trigger name mapping ---

    private static string GetCharTriggerName(CharTrigger trigger) => trigger switch
    {
        CharTrigger.Click => "Click",
        CharTrigger.DClick => "DClick",
        CharTrigger.Create => "Create",
        CharTrigger.CreateLoot => "CreateLoot",
        CharTrigger.Death => "Death",
        CharTrigger.Destroy => "Destroy",
        CharTrigger.Kill => "Kill",
        CharTrigger.Hit => "Hit",
        CharTrigger.GetHit => "GetHit",
        CharTrigger.HitMiss => "HitMiss",
        CharTrigger.HitParry => "HitParry",
        CharTrigger.HitCheck => "HitCheck",
        CharTrigger.HitIgnore => "HitIgnore",
        CharTrigger.HitTry => "HitTry",
        CharTrigger.SpellCast => "SpellCast",
        CharTrigger.SpellEffect => "SpellEffect",
        CharTrigger.SpellSuccess => "SpellSuccess",
        CharTrigger.SpellFail => "SpellFail",
        CharTrigger.SpellSelect => "SpellSelect",
        CharTrigger.SkillPreStart => "SkillPreStart",
        CharTrigger.SkillStart => "SkillStart",
        CharTrigger.SkillStroke => "SkillStroke",
        CharTrigger.SkillSuccess => "SkillSuccess",
        CharTrigger.SkillFail => "SkillFail",
        CharTrigger.SkillGain => "SkillGain",
        CharTrigger.SkillAbort => "SkillAbort",
        CharTrigger.SkillSelect => "SkillSelect",
        CharTrigger.SkillMakeItem => "SkillMakeItem",
        CharTrigger.SkillWait => "SkillWait",
        CharTrigger.LogIn => "LogIn",
        CharTrigger.LogOut => "LogOut",
        CharTrigger.Mount => "Mount",
        CharTrigger.Dismount => "Dismount",
        CharTrigger.RegionEnter => "RegionEnter",
        CharTrigger.RegionLeave => "RegionLeave",
        CharTrigger.RegionStep => "RegionStep",
        CharTrigger.RoomEnter => "RoomEnter",
        CharTrigger.RoomLeave => "RoomLeave",
        CharTrigger.RoomStep => "RoomStep",
        CharTrigger.CombatStart => "CombatStart",
        CharTrigger.CombatEnd => "CombatEnd",
        CharTrigger.CombatAdd => "CombatAdd",
        CharTrigger.CombatDelete => "CombatDelete",
        CharTrigger.Criminal => "Criminal",
        CharTrigger.Attack => "Attack",
        CharTrigger.FameChange => "FameChange",
        CharTrigger.KarmaChange => "KarmaChange",
        CharTrigger.Hunger => "Hunger",
        CharTrigger.Eat => "Eat",
        CharTrigger.Follow => "Follow",
        CharTrigger.Jail => "Jail",
        CharTrigger.NPCAction => "NPCAction",
        CharTrigger.NPCActFight => "NPCActFight",
        CharTrigger.NPCActFollow => "NPCActFollow",
        CharTrigger.NPCActWander => "NPCActWander",
        CharTrigger.NPCActCast => "NPCActCast",
        CharTrigger.NPCSeeNewPlayer => "NPCSeeNewPlayer",
        CharTrigger.NPCHearGreeting => "NPCHearGreeting",
        CharTrigger.NPCHearUnknown => "NPCHearUnknown",
        CharTrigger.NPCLookAtChar => "NPCLookAtChar",
        CharTrigger.NPCLookAtItem => "NPCLookAtItem",
        CharTrigger.NPCSpecialAction => "NPCSpecialAction",
        CharTrigger.NPCAcceptItem => "NPCAcceptItem",
        CharTrigger.NPCRefuseItem => "NPCRefuseItem",
        CharTrigger.NPCRestock => "NPCRestock",
        CharTrigger.NPCSeeWantItem => "NPCSeeWantItem",
        CharTrigger.NPCLostTeleport => "NPCLostTeleport",
        CharTrigger.PartyDisband => "PartyDisband",
        CharTrigger.PartyInvite => "PartyInvite",
        CharTrigger.PartyLeave => "PartyLeave",
        CharTrigger.PartyRemove => "PartyRemove",
        CharTrigger.PersonalSpace => "PersonalSpace",
        CharTrigger.PetDesert => "PetDesert",
        CharTrigger.Resurrect => "Resurrect",
        CharTrigger.ReceiveItem => "ReceiveItem",
        CharTrigger.Rename => "Rename",
        CharTrigger.SeeCrime => "SeeCrime",
        CharTrigger.StatChange => "StatChange",
        CharTrigger.StepStealth => "StepStealth",
        CharTrigger.TradeAccepted => "TradeAccepted",
        CharTrigger.TradeClose => "TradeClose",
        CharTrigger.TradeCreate => "TradeCreate",
        CharTrigger.UserWarmode => "UserWarmode",
        CharTrigger.UserChatButton => "UserChatButton",
        CharTrigger.UserGuildButton => "UserGuildButton",
        CharTrigger.UserQuestButton => "UserQuestButton",
        CharTrigger.UserStats => "UserStats",
        CharTrigger.UserVirtue => "UserVirtue",
        CharTrigger.UserVirtueInvoke => "UserVirtueInvoke",
        CharTrigger.ToolTip => "ToolTip",
        CharTrigger.Profile => "Profile",
        _ => trigger.ToString(),
    };

    private static string GetItemTriggerName(ItemTrigger trigger) => trigger switch
    {
        ItemTrigger.Click => "Click",
        ItemTrigger.DClick => "DClick",
        ItemTrigger.Create => "Create",
        ItemTrigger.Damage => "Damage",
        ItemTrigger.Destroy => "Destroy",
        ItemTrigger.Equip => "Equip",
        ItemTrigger.EquipTest => "EquipTest",
        ItemTrigger.Unequip => "Unequip",
        ItemTrigger.DropOnChar => "DropOn_Char",
        ItemTrigger.DropOnGround => "DropOn_Ground",
        ItemTrigger.DropOnItem => "DropOn_Item",
        ItemTrigger.DropOnSelf => "DropOn_Self",
        ItemTrigger.PickupGround => "Pickup_Ground",
        ItemTrigger.PickupPack => "Pickup_Pack",
        ItemTrigger.Step => "Step",
        ItemTrigger.Timer => "Timer",
        ItemTrigger.Tooltip => "ToolTip",
        ItemTrigger.Sell => "Sell",
        ItemTrigger.Buy => "Buy",
        ItemTrigger.Spawn => "Spawn",
        ItemTrigger.PreSpawn => "PreSpawn",
        ItemTrigger.AddObj => "AddObj",
        ItemTrigger.DelObj => "DelObj",
        ItemTrigger.SpellEffect => "SpellEffect",
        ItemTrigger.Hit => "Hit",
        ItemTrigger.GetHit => "GetHit",
        ItemTrigger.ResourceGather => "ResourceGather",
        ItemTrigger.ResourceTest => "ResourceTest",
        _ => trigger.ToString(),
    };
}

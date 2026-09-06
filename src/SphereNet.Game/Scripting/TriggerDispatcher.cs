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

    /// <summary>Shared LOCAL.* pool for the trigger chain (Source-X
    /// CScriptTriggerArgs.m_VarsLocal). The engine seeds values before the
    /// fire and reads script writes back from the SAME map afterwards —
    /// the @SpellEffectTick LOCAL.EFFECT/DELAY/CHARGES contract.</summary>
    public SphereNet.Scripting.Variables.VarMap? Locals { get; set; }

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

    // Source-X IsTrigUsed parity: the set of char-trigger names hooked anywhere —
    // by a registered C# handler, an [ON=@X] block in any loaded resource, or an
    // f_onchar_<x> function. Lets a hot path (e.g. ComputeNotoriety → @NotoSend)
    // skip firing entirely when nothing hooks the trigger. Registrations update it
    // live; BuildUsedTriggerCache scans the loaded scripts once after load.
    private readonly HashSet<string> _usedCharTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedItemTriggers = new(StringComparer.OrdinalIgnoreCase);

    // Trigger names that have an f_onchar_<x>/f_onitem_<x> [FUNCTION] fallback.
    // Gates FireCharTriggerByName step 6 / FireItemTriggerByName step 7 so the
    // per-fire name concat + ToLowerInvariant + args wrapping only happens when
    // such a function actually exists. Until BuildUsedTriggerCache runs the gate
    // stays open (always try), preserving behavior when no cache is built.
    private bool _funcTriggerGateBuilt;
    private readonly HashSet<string> _funcCharTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _funcItemTriggers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional resource holder for resolving CHARDEF/ITEMDEF scripts.</summary>
    public ResourceHolder? Resources { get; set; }

    /// <summary>Optional trigger runner for executing script blocks.</summary>
    public TriggerRunner? Runner { get; set; }

    public List<ResourceId> GlobalPlayerEvents { get; } = [];
    public List<ResourceId> GlobalPetEvents { get; } = [];
    public List<ResourceId> GlobalItemEvents { get; } = [];
    public List<ResourceId> GlobalRegionEvents { get; } = [];
    public List<ResourceId> SpeechSelfResources { get; } = [];
    public List<ResourceId> SpeechPetResources { get; } = [];

    public bool ScriptDebug { get; set; }
    public Action<string>? DebugLog { get; set; }

    /// <summary>
    /// Full on-hit trigger pipeline for a connecting swing (Source-X
    /// CChar::Fight_Hit @Hit + CChar::OnTakeDamage @GetHit block,
    /// CCharFight.cpp:750). Fires, in order: attacker @Hit, defender @GetHit
    /// (with the LOCAL.ItemDamageLayer / ItemDamageChance armor-damage roll and
    /// the LOCAL.DamagePercent* elemental split), weapon item @Hit, and item
    /// @GetHit on the worn piece at the script-final ItemDamageLayer. ARGN1
    /// (damage) threads through every stage; RETURN 1 anywhere cancels the hit
    /// (ctx.Cancelled) and returns 0. Script writes to the armor-damage locals
    /// copy back into ctx for CombatEngine's durability roll.
    /// </summary>
    public int RunHitDamageTriggers(Combat.HitDamageContext ctx)
    {
        var attacker = ctx.Attacker;
        var target = ctx.Target;
        var weapon = ctx.Weapon;
        int dmg = ctx.Damage;
        int dmgType = (int)Combat.CombatEngine.GetWeaponDamageType(weapon);

        // Source-X @Hit (Fight_Hit Init(iDmg, iDmgType, 0, pWeapon)): SRC = the
        // victim, ARGO = the weapon, ARGN1 = damage (writable), ARGN2 = type.
        // LOCAL.ItemDamageChance gates the weapon's durability wear and
        // LOCAL.ItemPoisonReductionChance/Amount the poison-charge spend
        // (CCharFight.cpp:2148); the weapon item @Hit below shares the SAME
        // locals pool, matching Source-X's shared pScriptArgs.
        var hitLocals = new SphereNet.Scripting.Variables.VarMap();
        hitLocals.SetInt("ItemDamageChance", ctx.WeaponDamageChance);
        hitLocals.SetInt("ItemPoisonReductionChance", ctx.PoisonReductionChance);
        hitLocals.SetInt("ItemPoisonReductionAmount", ctx.PoisonReductionAmount);
        // Ranged: LOCAL.Arrow = the live pack ammo stack UID; a script may
        // write LOCAL.ArrowHandled=1 to take the ammo over (CCharFight:2158).
        if (ctx.AmmoUid != 0)
            hitLocals.SetInt("Arrow", ctx.AmmoUid);
        var hitArgs = new TriggerArgs { CharSrc = target, O1 = weapon, ItemSrc = weapon, N1 = dmg, N2 = dmgType, Locals = hitLocals };
        if (FireCharTrigger(attacker, CharTrigger.Hit, hitArgs) == TriggerResult.True)
        {
            ctx.Cancelled = true;
            return 0;
        }
        dmg = Math.Max(0, hitArgs.N1);

        // Source-X @GetHit: SRC = the attacker, ARGN1 = damage (writable),
        // ARGN2 = damage type. LOCAL.ItemDamageLayer picks which worn piece
        // takes the item @GetHit and the durability wear (script-writable),
        // LOCAL.ItemDamageChance the wear chance; the elemental split is
        // exposed as LOCAL.DamagePercent*.
        var getHitLocals = new SphereNet.Scripting.Variables.VarMap();
        getHitLocals.SetInt("ItemDamageLayer", (int)ctx.ItemDamageLayer);
        getHitLocals.SetInt("ItemDamageChance", ctx.ItemDamageChance);
        if (ctx.Elemental)
        {
            getHitLocals.SetInt("DamagePercentPhysical", ctx.DamPercentPhysical);
            getHitLocals.SetInt("DamagePercentFire", ctx.DamPercentFire);
            getHitLocals.SetInt("DamagePercentCold", ctx.DamPercentCold);
            getHitLocals.SetInt("DamagePercentPoison", ctx.DamPercentPoison);
            getHitLocals.SetInt("DamagePercentEnergy", ctx.DamPercentEnergy);
        }
        var getHitArgs = new TriggerArgs { CharSrc = attacker, N1 = dmg, N2 = dmgType, Locals = getHitLocals };
        if (FireCharTrigger(target, CharTrigger.GetHit, getHitArgs) == TriggerResult.True)
        {
            ctx.Cancelled = true;
            return 0;
        }
        dmg = Math.Max(0, getHitArgs.N1);
        // The char @GetHit script may redirect the armor-damage roll.
        ctx.ItemDamageLayer = (Layer)getHitLocals.GetInt("ItemDamageLayer");

        if (weapon != null)
        {
            var wArgs = new TriggerArgs { CharSrc = attacker, ItemSrc = weapon, O1 = target, N1 = dmg, N2 = dmgType, Locals = hitLocals };
            if (FireItemTrigger(weapon, ItemTrigger.Hit, wArgs) == TriggerResult.True)
            {
                ctx.Cancelled = true;
                return 0;
            }
            dmg = Math.Max(0, wArgs.N1);
        }
        // Script-final weapon wear / poison-spend knobs back into the context
        // for CombatEngine's post-trigger rolls; ArrowHandled hands the ammo
        // fate to the script (checked after BOTH @Hit stages, like Source-X).
        ctx.WeaponDamageChance = (int)hitLocals.GetInt("ItemDamageChance");
        ctx.PoisonReductionChance = (int)hitLocals.GetInt("ItemPoisonReductionChance");
        ctx.PoisonReductionAmount = (int)hitLocals.GetInt("ItemPoisonReductionAmount");
        ctx.ArrowHandled = hitLocals.GetInt("ArrowHandled") != 0;

        // Item @GetHit on the worn piece at the (script-final) layer — Source-X
        // fires it with the SAME args/locals ("ItemDamageLayer" is read-only
        // from here on, but the item script may still adjust ItemDamageChance).
        var armorHit = target.GetEquippedItem(ctx.ItemDamageLayer);
        if (armorHit != null)
        {
            var aArgs = new TriggerArgs { CharSrc = attacker, ItemSrc = armorHit, O1 = target, N1 = dmg, N2 = dmgType, Locals = getHitLocals };
            if (FireItemTrigger(armorHit, ItemTrigger.GetHit, aArgs) == TriggerResult.True)
            {
                ctx.Cancelled = true;
                return 0;
            }
            dmg = Math.Max(0, aArgs.N1);
        }
        ctx.ItemDamageChance = (int)getHitLocals.GetInt("ItemDamageChance");

        return dmg;
    }

    /// <summary>
    /// Fire a character trigger. Maps to CChar::OnTrigger. For the Skill*
    /// family, Source-X additionally runs the [SKILL n] resource-section stage
    /// (Skill_OnTrigger: ON=@START/@STROKE/@SUCCESS/...) with the same args —
    /// those blocks never executed in SphereNet before W-E. ARGN1 = skill id.
    /// </summary>
    public TriggerResult FireCharTrigger(Character ch, CharTrigger trigger, TriggerArgs args)
    {
        var result = FireCharTriggerByName(ch, GetCharTriggerName(trigger), args);
        if (result == TriggerResult.True)
            return result;

        string? stage = GetSkillSectionStage(trigger);
        if (stage != null)
        {
            var skillResult = FireSkillTrigger(args.N1, stage, ch, args);
            if (skillResult != TriggerResult.Default)
                return skillResult;
        }

        // [SPELL n] section stages that mirror the char-level spell triggers
        // (Source-X Spell_OnTrigger SPTRIG_START/TARGETCANCEL). ARGN1 =
        // spell id. Success/Fail/Effect fire from their engine sites already;
        // Select fires from SpellEngine.CastStart (the Spell_CanCast
        // equivalent every entry point funnels through) — mapping it here too
        // would run pack @Select bodies twice per client cast.
        string? spellStage = trigger switch
        {
            CharTrigger.SpellCast => "Start",
            CharTrigger.SpellTargetCancel => "TargetCancel",
            _ => null,
        };
        if (spellStage != null && args.N1 > 0)
        {
            var spellResult = FireSpellTrigger((SphereNet.Core.Enums.SpellType)args.N1, spellStage, ch, args);
            if (spellResult != TriggerResult.Default)
                return spellResult;
        }
        return result;
    }

    /// <summary>Source-X SKTRIG_* stage name for a char-level skill trigger,
    /// or null when the trigger has no [SKILL] section counterpart.</summary>
    private static string? GetSkillSectionStage(CharTrigger trigger) => trigger switch
    {
        CharTrigger.SkillPreStart => "PreStart",
        CharTrigger.SkillStart => "Start",
        CharTrigger.SkillStroke => "Stroke",
        CharTrigger.SkillSuccess => "Success",
        CharTrigger.SkillFail => "Fail",
        CharTrigger.SkillAbort => "Abort",
        CharTrigger.SkillGain => "Gain",
        CharTrigger.SkillSelect => "Select",
        CharTrigger.SkillUseQuick => "UseQuick",
        CharTrigger.SkillWait => "Wait",
        CharTrigger.SkillTargetCancel => "TargetCancel",
        _ => null,
    };

    /// <summary>Run a [SKILL n] resource-section trigger stage (Source-X
    /// Skill_OnTrigger). Same wrapped-args contract as FireSpellTrigger.</summary>
    public TriggerResult FireSkillTrigger(int skillId, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null || skillId < 0)
            return TriggerResult.Default;

        var link = Resources.GetResource(ResType.SkillDef, skillId);
        if (link == null)
            return TriggerResult.Default;

        return RunWrapped(link, trigName, ch, args);
    }

    /// <summary>Name-based variant — used by the enum path above and by the
    /// script TRIGGER verb (Source-X CV_TRIGGER), which may fire arbitrary
    /// custom trigger names defined only in TEVENTS/EVENTS sections.</summary>
    public TriggerResult FireCharTriggerByName(Character ch, string trigName, TriggerArgs args)
    {
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
                    var result = RunWrapped(tevLink, trigName, ch, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // 4. CHARDEF own triggers (ON=@Trigger in the CHARDEF body)
            var charDefLink = Resources.GetResource(ResType.CharDef, ch.CharDefIndex);
            if (charDefLink != null)
            {
                // CHARDEF links may not have trigger bitmasks precomputed, so resolve by name.
                var result = RunWrapped(charDefLink, trigName, ch, args);
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
        var globalEventResult = RunResourceEventHandlers(
            ch.IsPlayer ? GlobalPlayerEvents : GlobalPetEvents, trigName, ch, args);
        if (globalEventResult == TriggerResult.True)
            return TriggerResult.True;

        if (Runner != null && (!_funcTriggerGateBuilt || _funcCharTriggers.Contains(trigName)))
        {
            string funcName = "f_onchar_" + trigName.ToLowerInvariant();
            if (Runner.TryRunFunction(funcName, ch, args.ScriptConsole, WrapArgs(args), out var result) &&
                result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire an item trigger. Maps to CItem::OnTrigger.
    /// </summary>
    public TriggerResult FireItemTrigger(Item item, ItemTrigger trigger, TriggerArgs args)
        => FireItemTriggerByName(item, GetItemTriggerName(trigger), args);

    /// <summary>Name-based variant — used by the enum path above and by the
    /// script TRIGGER verb, which may fire custom trigger names.</summary>
    public TriggerResult FireItemTriggerByName(Item item, string trigName, TriggerArgs args)
    {
        if (ScriptDebug)
            DebugLog?.Invoke($"[script_debug] ITRIG @{trigName} on item 0x{item.Uid.Value:X8} id=0x{item.BaseId:X4} src={args.CharSrc?.Name ?? "-"}");

        // 1. @Item* on source character
        if (args.CharSrc != null)
        {
            // Legacy Scripts-X bola resources used @ItemUnEquipTest for the
            // pre-unequip veto. Current Source-X calls this @itemUNEQUIP; run
            // the old alias first without replacing the canonical trigger.
            if (trigName.Equals("Unequip", StringComparison.OrdinalIgnoreCase))
            {
                var legacyResult = RunObjectHandlers(args.CharSrc, "itemUnequipTest", args);
                if (legacyResult == TriggerResult.True)
                    return TriggerResult.True;
            }

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
                    // Older saves may contain copied TEVENTS in the instance EVENTS
                    // list. RunObjectHandlers already executed those above.
                    if (item.Events.Contains(tevRid)) continue;
                    var tevLink = Resources.GetResource(tevRid);
                    if (tevLink == null) continue;
                    var result = RunWrapped(tevLink, trigName, item, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            // ITEMDEF own triggers (resolved graphic's def).
            var itemDefLink = Resources.GetResource(ResType.ItemDef, item.BaseId);
            if (itemDefLink != null)
            {
                var result = RunWrapped(itemDefLink, trigName, item, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }

            // Named (scripted) ITEMDEF triggers — when [itemdef i_moongate]
            // sets id=i_moongate_blue, the item's BaseId becomes 0x0F6C
            // (the graphic) but the @DClick / @Step triggers live on the
            // i_moongate section itself, keyed by its string-hash index.
            // TryAddAtTarget stashes that index in TAG.SCRIPTDEF so the
            // dispatcher can route triggers back to the scripted def.
            int scriptDefIdx = Definitions.ItemDefHelper.ResolveInstanceDefIndex(item, Resources);
            if (scriptDefIdx != 0 && scriptDefIdx != item.BaseId)
            {
                var namedDef = Definitions.DefinitionLoader.GetItemDef(scriptDefIdx);
                if (namedDef != null)
                {
                    foreach (var tevRid in namedDef.Events)
                    {
                        if (item.Events.Contains(tevRid)) continue;
                        var tevLink = Resources.GetResource(tevRid);
                        if (tevLink == null) continue;
                        var tevResult = RunWrapped(tevLink, trigName, item, args);
                        if (tevResult == TriggerResult.True)
                            return TriggerResult.True;
                    }
                }

                var scriptLink = Resources.GetResource(ResType.ItemDef, scriptDefIdx);
                if (scriptLink != null)
                {
                    var result = RunWrapped(scriptLink, trigName, item, args);
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
        var globalEventResult = RunResourceEventHandlers(GlobalItemEvents, trigName, item, args);
        if (globalEventResult == TriggerResult.True)
            return TriggerResult.True;

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
        if (Resources != null && Runner != null)
        {
            var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (!string.IsNullOrWhiteSpace(itemDef?.TypeRaw))
            {
                var typeRid = Resources.ResolveDefName(itemDef.TypeRaw.Trim());
                var typeLink = typeRid.IsValid && typeRid.Type == ResType.TypeDef
                    ? Resources.GetResource(typeRid)
                    : null;
                if (typeLink != null)
                {
                    var result = RunWrapped(typeLink, trigName, item, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        // 6. ITEMDEF (base definition script) — already covered by step 3

        // 7. Global f_onitem_* function (parity with f_onchar_*)
        if (Runner != null && (!_funcTriggerGateBuilt || _funcItemTriggers.Contains(trigName)))
        {
            string funcName = "f_onitem_" + trigName.ToLowerInvariant();
            if (Runner.TryRunFunction(funcName, item, args.ScriptConsole, WrapArgs(args), out var result) &&
                result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Fire SPEECH triggers for an NPC hearing speech.
    /// Iterates the CharDef's SpeechResources list and runs the first matching pattern.
    /// </summary>
    public TriggerResult FireSpeechTrigger(Character npc, Character speaker, string text,
        int mode = 0, ITextConsole? sourceConsole = null)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var charDef = Definitions.DefinitionLoader.GetCharDef(npc.CharDefIndex);
        bool hasDSpeech = npc.DSpeech.Count > 0;
        bool hasDefSpeech = charDef != null && charDef.SpeechResources.Count > 0;
        if (hasDSpeech || hasDefSpeech)
        {
            var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, mode, 0, text)
            {
                Object1 = npc,
                Object2 = speaker
            };

            // Source-X CChar::OnTriggerSpeech runs the char's per-instance m_Speech
            // (DSPEECH) list first, then the CharDef's static SPEECH list.
            if (hasDSpeech)
            {
                foreach (var speechRid in npc.DSpeech)
                {
                    var link = Resources.GetResource(speechRid);
                    if (link == null) continue;

                    var result = Runner.RunSpeechTrigger(link, text, npc, sourceConsole, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }

            if (hasDefSpeech)
            {
                foreach (var speechRid in charDef!.SpeechResources)
                {
                    var link = Resources.GetResource(speechRid);
                    if (link == null) continue;

                    var result = Runner.RunSpeechTrigger(link, text, npc, sourceConsole, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        return RunSpeechResourceHandlers(SpeechPetResources, npc, speaker, text, mode, sourceConsole, out _);
    }

    public TriggerResult FireSpeechSelfTrigger(Character speaker, string text, int mode,
        ITextConsole? sourceConsole = null)
        => RunSpeechResourceHandlers(SpeechSelfResources, speaker, speaker, text, mode, sourceConsole, out _);

    /// <summary>Fire the speaker's @Speech self-trigger; <paramref name="rewrittenText"/>
    /// returns the utterance the script may have rewritten via ARGS (Source-X @Speech
    /// text rewrite), or the original text when unchanged.</summary>
    public TriggerResult FireSpeechSelfTrigger(Character speaker, string text, int mode,
        out string rewrittenText, ITextConsole? sourceConsole = null)
        => RunSpeechResourceHandlers(SpeechSelfResources, speaker, speaker, text, mode, sourceConsole, out rewrittenText);

    /// <summary>
    /// Run TSPEECH/SPEECH resources attached to a [MULTIDEF]. Source-X routes
    /// speech heard in a house/ship region through CItemMulti::OnHearRegion.
    /// </summary>
    public TriggerResult FireMultiSpeechTrigger(Item multi, Character speaker, string text,
        int mode = 0, ITextConsole? sourceConsole = null)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var multiLink = Resources.GetResource(ResType.MultiDef, multi.BaseId);
        if (multiLink?.StoredKeys == null)
            return TriggerResult.Default;

        var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, mode, 0, text)
        {
            Object1 = multi,
            Object2 = speaker
        };

        foreach (var key in multiLink.StoredKeys)
        {
            if (!key.Key.Equals("TSPEECH", StringComparison.OrdinalIgnoreCase) &&
                !key.Key.Equals("SPEECH", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string token in key.Arg.Split(',',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var rid = Resources.ResolveDefName(token);
                if (!rid.IsValid || rid.Type != ResType.Speech)
                    rid = ResourceId.FromString(token, ResType.Speech);
                var speechLink = Resources.GetResource(rid);
                if (speechLink == null) continue;

                var result = Runner.RunSpeechTrigger(speechLink, text, multi, sourceConsole, args);
                if (result == TriggerResult.True)
                    return TriggerResult.True;
            }
        }

        return TriggerResult.Default;
    }

    private TriggerResult RunSpeechResourceHandlers(
        IReadOnlyList<ResourceId> speechResources,
        Character target,
        Character speaker,
        string text,
        int mode,
        ITextConsole? sourceConsole,
        out string rewrittenText)
    {
        rewrittenText = text;
        if (Resources == null || Runner == null || speechResources.Count == 0)
            return TriggerResult.Default;

        var args = new SphereNet.Scripting.Execution.TriggerArgs(speaker, mode, 0, text)
        {
            Object1 = target,
            Object2 = speaker
        };

        foreach (var speechRid in speechResources)
        {
            var link = Resources.GetResource(speechRid);
            if (link == null) continue;

            var result = Runner.RunSpeechTrigger(link, text, target, sourceConsole, args);
            // A script may rewrite the utterance via ARGS; carry it to the next block
            // and back to the caller (the interpreter writes it onto args.ArgString).
            rewrittenText = args.ArgString;
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
            RunWrapped(link, trigName, ch, args);
        }

        RunResourceEventHandlers(GlobalRegionEvents, trigName, ch, args);
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
            RunWrapped(link, trigName, ch, args);
        }

        RunResourceEventHandlers(GlobalRegionEvents, trigName, ch, args);
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

        // Copy the script's ARGN/ARGS mutations back (e.g. @ResourceGather changing
        // the reaped item id / amount) via the shared RunWrapped path.
        return RunWrapped(link, trigName, ch, args);
    }

    /// <summary>
    /// Fire a trigger on a [SPELL] def's own ON=@TrigName block (Source-X
    /// Spell_OnTrigger). The [SPELL] ResourceLink already retains its trigger
    /// bodies (SpellDef is a definition type), so this just resolves the link by
    /// spell index and runs the body — mirroring the ITEMDEF/REGIONRESOURCE path.
    /// ARGN1/2/3 mutations are copied back so a script can override id/amount.
    /// </summary>
    public TriggerResult FireSpellTrigger(SphereNet.Core.Enums.SpellType spell, string trigName, Character ch, TriggerArgs args)
    {
        if (Resources == null || Runner == null)
            return TriggerResult.Default;

        var link = Resources.GetResource(ResType.SpellDef, (int)spell);
        if (link == null)
            return TriggerResult.Default;

        return RunWrapped(link, trigName, ch, args);
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
        _usedCharTriggers.Add(trigName);
    }

    private TriggerResult RunResourceEventHandlers(
        IReadOnlyList<ResourceId> eventResources,
        string trigName,
        IScriptObj target,
        TriggerArgs args)
    {
        if (Resources == null || Runner == null || eventResources.Count == 0)
            return TriggerResult.Default;

        foreach (var eventRid in eventResources)
        {
            var link = Resources.GetResource(eventRid);
            if (link == null) continue;

            var result = RunWrapped(link, trigName, target, args);
            if (result == TriggerResult.True)
                return TriggerResult.True;
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Scan all loaded script resources for the char-trigger names they hook
    /// (<c>[ON=@X]</c> blocks and <c>f_onchar_x</c> functions) and fold them into
    /// the used-trigger set. Registered C# handlers are already tracked live by
    /// <see cref="RegisterCharEvent"/>. Call once after scripts are loaded.
    /// </summary>
    public void BuildUsedTriggerCache()
    {
        if (Resources == null)
            return;

        foreach (var link in Resources.GetAllResources())
        {
            var keys = link.StoredKeys;
            if (keys == null) continue;
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                    key.Arg.StartsWith('@'))
                {
                    // A scanned [ON=@X] name could hook either a char or an item
                    // trigger; record it for both (a safe over-approximation — the
                    // gate must never report a hooked trigger as unused).
                    string trigName = key.Arg[1..].Trim();
                    _usedCharTriggers.Add(trigName);
                    _usedItemTriggers.Add(trigName);
                }
            }
        }

        // f_onchar_<name>/f_onitem_<name> function fallbacks (FireCharTrigger
        // step 6 / FireItemTrigger step 7). Scanning loaded function defnames
        // instead of the trigger enums also covers custom trigger names fired
        // via the TRIGGER verb. The f_f_* variants account for the automatic
        // f_-prefix retry in function resolution.
        _funcCharTriggers.Clear();
        Resources.CollectFunctionDefNameSuffixes("f_onchar_", _funcCharTriggers);
        Resources.CollectFunctionDefNameSuffixes("f_f_onchar_", _funcCharTriggers);
        _usedCharTriggers.UnionWith(_funcCharTriggers);

        _funcItemTriggers.Clear();
        Resources.CollectFunctionDefNameSuffixes("f_onitem_", _funcItemTriggers);
        Resources.CollectFunctionDefNameSuffixes("f_f_onitem_", _funcItemTriggers);
        _usedItemTriggers.UnionWith(_funcItemTriggers);

        _funcTriggerGateBuilt = true;
    }

    /// <summary>
    /// O(1) check: is <paramref name="trigger"/> hooked by any script block,
    /// registered handler, or function? Also covers the cross-fired
    /// <c>@char&lt;Name&gt;</c> mirror. Returns false only when nothing can fire it,
    /// letting hot-path callers skip the dispatch entirely.
    /// </summary>
    public bool IsCharTriggerUsed(CharTrigger trigger)
    {
        string name = GetCharTriggerName(trigger);
        return _usedCharTriggers.Contains(name) || _usedCharTriggers.Contains("char" + name);
    }

    /// <summary>
    /// O(1) item-trigger counterpart of <see cref="IsCharTriggerUsed"/>. Also
    /// covers the cross-fired <c>@item&lt;Name&gt;</c> mirror. Lets a hot item path
    /// (e.g. memory creation during combat) skip the dispatch when nothing hooks it.
    /// </summary>
    public bool IsItemTriggerUsed(ItemTrigger trigger)
    {
        string name = GetItemTriggerName(trigger);
        return _usedItemTriggers.Contains(name) || _usedItemTriggers.Contains("item" + name);
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
        _usedItemTriggers.Add(trigName);
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

                    var result = RunWrapped(eventLink, trigName, obj, args);
                    if (result == TriggerResult.True)
                        return TriggerResult.True;
                }
            }
        }

        return TriggerResult.Default;
    }

    /// <summary>Run one trigger block with wrapped args, then copy the script's
    /// ARGN1/2/3 mutations back into the caller's TriggerArgs. Source-X reads
    /// pScriptArgs->m_iN1/2 back after OnTrigger (e.g. @NPCActFight dist/motivation,
    /// @HitTry swing delay); the wrapped instance is per-block, so without this
    /// copy-back those mutations would be lost. LOCAL.* readback already works
    /// via the shared Locals pool. Copying after each block in a chain also
    /// forward-propagates the values to the next block, matching Source-X.</summary>
    private TriggerResult RunWrapped(SphereNet.Scripting.Resources.ResourceLink link,
        string trigName, IScriptObj obj, TriggerArgs args)
    {
        var wrapped = WrapArgs(args);
        var result = Runner!.RunTriggerByName(link, trigName, obj, args.ScriptConsole, wrapped);
        args.N1 = wrapped.Number1;
        args.N2 = wrapped.Number2;
        args.N3 = wrapped.Number3;
        args.S1 = wrapped.ArgString; // ARGS the script rewrote (Source-X m_s1 readback)
        return result;
    }

    /// <summary>Convert TriggerArgs to ITriggerArgs for the script engine.</summary>
    private static SphereNet.Scripting.Execution.TriggerArgs WrapArgs(TriggerArgs args)
    {
        var wrapped = new SphereNet.Scripting.Execution.TriggerArgs(
            args.CharSrc ?? (IScriptObj?)args.ItemSrc,
            args.N1, args.N2, args.S1);
        // Seed ARGN3 into the script args. The constructor only takes N1/N2, so
        // without this <ARGN3> read as 0 inside every trigger even though the caller
        // set it (e.g. @DropOn_* drop-Z, @SkillUseQuick result) — and the N3 write-
        // back below then read the un-seeded 0 too.
        wrapped.Number3 = args.N3;
        wrapped.Object1 = args.O1;
        wrapped.Object2 = args.CharSrc ?? (IScriptObj?)args.ItemSrc;
        // Reference, not copy: every chain step's wrapped args share the one
        // LOCAL pool, so cross-step and engine readback semantics hold.
        wrapped.SharedLocals = args.Locals;
        return wrapped;
    }


    // --- Trigger name mapping ---

    private static string GetCharTriggerName(CharTrigger trigger) => trigger switch
    {
        CharTrigger.AfterClick => "AfterClick",
        CharTrigger.Attack => "Attack",
        CharTrigger.CallGuards => "CallGuards",
        CharTrigger.CharAttack => "CharAttack",
        CharTrigger.CharClick => "CharClick",
        CharTrigger.CharClientTooltip => "CharClientTooltip",
        CharTrigger.CharContextMenuRequest => "CharContextMenuRequest",
        CharTrigger.CharContextMenuSelect => "CharContextMenuSelect",
        CharTrigger.CharDClick => "CharDClick",
        CharTrigger.CharTradeAccepted => "CharTradeAccepted",
        CharTrigger.Click => "Click",
        CharTrigger.ClientTooltip => "ClientTooltip",
        CharTrigger.ContextMenuRequest => "ContextMenuRequest",
        CharTrigger.ContextMenuSelect => "ContextMenuSelect",
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
        CharTrigger.SpellInterrupt => "SpellInterrupt",
        CharTrigger.SpellSelect => "SpellSelect",
        CharTrigger.SpellBook => "SpellBook",
        CharTrigger.SpellTargetCancel => "SpellTargetCancel",
        CharTrigger.SkillPreStart => "SkillPreStart",
        CharTrigger.SkillStart => "SkillStart",
        CharTrigger.SkillStroke => "SkillStroke",
        CharTrigger.SkillSuccess => "SkillSuccess",
        CharTrigger.SkillFail => "SkillFail",
        CharTrigger.SkillGain => "SkillGain",
        CharTrigger.SkillAbort => "SkillAbort",
        CharTrigger.SkillChange => "SkillChange",
        CharTrigger.SkillSelect => "SkillSelect",
        CharTrigger.SkillMakeItem => "SkillMakeItem",
        CharTrigger.SkillMenu => "SkillMenu",
        CharTrigger.SkillTargetCancel => "SkillTargetCancel",
        CharTrigger.SkillUseQuick => "SkillUseQuick",
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
        CharTrigger.EffectAdd => "EffectAdd",
        CharTrigger.EnvironChange => "EnvironChange",
        CharTrigger.ExpChange => "ExpChange",
        CharTrigger.ExpLevelChange => "ExpLevelChange",
        CharTrigger.FameChange => "FameChange",
        CharTrigger.KarmaChange => "KarmaChange",
        CharTrigger.Hunger => "Hunger",
        CharTrigger.Drink => "Drink",
        CharTrigger.Eat => "Eat",
        CharTrigger.Follow => "Follow",
        CharTrigger.Jail => "Jailed", // Source-X CChar sm_szTrigName: @Jailed (not @Jail)
        CharTrigger.HouseDesignCommit => "HouseDesignCommit",
        CharTrigger.HouseDesignExit => "HouseDesignExit",
        CharTrigger.MurderDecay => "MurderDecay",
        CharTrigger.MurderMark => "MurderMark",
        CharTrigger.NotoSend => "NotoSend",
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
        CharTrigger.PartyAdd => "PartyAdd",
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
        CharTrigger.SeeSnoop => "SeeSnoop",
        CharTrigger.StatChange => "StatChange",
        CharTrigger.StepStealth => "StepStealth",
        CharTrigger.TradeAccepted => "TradeAccepted",
        CharTrigger.TradeClose => "TradeClose",
        CharTrigger.TradeCreate => "TradeCreate",
        CharTrigger.Targon_Cancel => "Targon_Cancel",
        CharTrigger.UserBugReport => "UserBugReport",
        CharTrigger.UserWarmode => "UserWarmode",
        CharTrigger.UserChatButton => "UserChatButton",
        CharTrigger.UserExtCmd => "UserExtCmd",
        CharTrigger.UserExWalkLimit => "UserExWalkLimit",
        CharTrigger.UserGlobalChatButton => "UserGlobalChatButton",
        CharTrigger.UserGuildButton => "UserGuildButton",
        CharTrigger.UserKRToolbar => "UserKRToolbar",
        CharTrigger.UserMailBag => "UserMailBag",
        CharTrigger.UserQuestArrowClick => "UserQuestArrowClick",
        CharTrigger.UserQuestButton => "UserQuestButton",
        CharTrigger.UserSkills => "UserSkills",
        CharTrigger.UserSpecialMove => "UserSpecialMove",
        CharTrigger.UserStats => "UserStats",
        CharTrigger.UserUltimaStoreButton => "UserUltimaStoreButton",
        CharTrigger.UserVirtue => "UserVirtue",
        CharTrigger.UserVirtueInvoke => "UserVirtueInvoke",
        CharTrigger.AddMulti => "AddMulti",
        CharTrigger.HouseDesignBegin => "HouseDesignBegin",
        CharTrigger.ToolTip => "ToolTip",
        CharTrigger.Profile => "Profile",
        CharTrigger.itemAfterClick => "itemAfterClick",
        CharTrigger.itemBuy => "itemBuy",
        CharTrigger.itemClick => "itemClick",
        CharTrigger.itemClientTooltip => "itemClientTooltip",
        CharTrigger.itemContextMenuRequest => "itemContextMenuRequest",
        CharTrigger.itemContextMenuSelect => "itemContextMenuSelect",
        CharTrigger.itemCreate => "itemCreate",
        CharTrigger.itemDamage => "itemDamage",
        CharTrigger.itemDClick => "itemDClick",
        CharTrigger.itemDestroy => "itemDestroy",
        CharTrigger.itemDropOnChar => "itemDropOn_Char",
        CharTrigger.itemDropOnGround => "itemDropOn_Ground",
        CharTrigger.itemDropOnItem => "itemDropOn_Item",
        CharTrigger.itemDropOnSelf => "itemDropOn_Self",
        CharTrigger.itemDropOnTrade => "itemDropOn_Trade",
        CharTrigger.itemEquip => "itemEquip",
        CharTrigger.itemEquipTest => "itemEquipTest",
        CharTrigger.itemMemoryEquip => "itemMemoryEquip",
        CharTrigger.itemPickupGround => "itemPickup_Ground",
        CharTrigger.itemPickupPack => "itemPickup_Pack",
        CharTrigger.itemPickupSelf => "itemPickup_Self",
        CharTrigger.itemPickupStack => "itemPickup_Stack",
        CharTrigger.itemSell => "itemSell",
        CharTrigger.itemSpellEffect => "itemSPELL", // Source-X CChar sm_szTrigName: @itemSPELL
        CharTrigger.itemStep => "itemStep",
        CharTrigger.itemTargOnCancel => "itemTargOn_Cancel",
        CharTrigger.itemTargOnChar => "itemTargOn_Char",
        CharTrigger.itemTargOnGround => "itemTargOn_Ground",
        CharTrigger.itemTargOnItem => "itemTargOn_Item",
        CharTrigger.itemTimer => "itemTimer",
        CharTrigger.itemToolTip => "itemToolTip",
        CharTrigger.itemUnequip => "itemUnequip",
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
        ItemTrigger.DropOnTrade => "DropOn_Trade",
        ItemTrigger.PickupGround => "Pickup_Ground",
        ItemTrigger.PickupPack => "Pickup_Pack",
        ItemTrigger.PickupSelf => "Pickup_Self",
        ItemTrigger.PickupStack => "Pickup_Stack",
        ItemTrigger.Step => "Step",
        ItemTrigger.Timer => "Timer",
        ItemTrigger.Tooltip => "ToolTip",
        ItemTrigger.AfterClick => "AfterClick",
        ItemTrigger.ClientTooltip => "ClientTooltip",
        ItemTrigger.ClientTooltipAfterDefault => "ClientTooltipAfterDefault",
        ItemTrigger.ContextMenuRequest => "ContextMenuRequest",
        ItemTrigger.ContextMenuSelect => "ContextMenuSelect",
        ItemTrigger.TargOnCancel => "TargOn_Cancel",
        ItemTrigger.TargOnChar => "TargOn_Char",
        ItemTrigger.TargOnGround => "TargOn_Ground",
        ItemTrigger.TargOnItem => "TargOn_Item",
        ItemTrigger.Sell => "Sell",
        ItemTrigger.Buy => "Buy",
        ItemTrigger.Spawn => "Spawn",
        ItemTrigger.PreSpawn => "PreSpawn",
        ItemTrigger.AddObj => "AddObj",
        ItemTrigger.DelObj => "DelObj",
        ItemTrigger.AddRedCandle => "AddRedCandle",
        ItemTrigger.AddWhiteCandle => "AddWhiteCandle",
        ItemTrigger.DelRedCandle => "DelRedCandle",
        ItemTrigger.DelWhiteCandle => "DelWhiteCandle",
        ItemTrigger.SpellEffect => "SpellEffect",
        ItemTrigger.Hit => "Hit",
        ItemTrigger.GetHit => "GetHit",
        ItemTrigger.ResourceGather => "ResourceGather",
        ItemTrigger.ResourceTest => "ResourceTest",
        ItemTrigger.CarveCorpse => "CarveCorpse",
        ItemTrigger.Complete => "Complete",
        ItemTrigger.Dye => "Dye",
        ItemTrigger.Level => "Level",
        ItemTrigger.MemoryEquip => "MemoryEquip",
        ItemTrigger.Redeed => "Redeed",
        ItemTrigger.RegionEnter => "RegionEnter",
        ItemTrigger.RegionLeave => "RegionLeave",
        // Source-X CItem sm_szTrigName uses the underscore forms.
        ItemTrigger.ShipMove => "Ship_Move",
        ItemTrigger.ShipStop => "Ship_Stop",
        ItemTrigger.ShipTurn => "Ship_Turn",
        ItemTrigger.Smelt => "Smelt",
        ItemTrigger.Start => "Start",
        ItemTrigger.Stop => "Stop",
        ItemTrigger.Hear => "Hear",
        _ => trigger.ToString(),
    };
}

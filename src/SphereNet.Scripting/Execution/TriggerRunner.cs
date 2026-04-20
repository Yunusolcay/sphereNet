using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Trigger execution engine. Manages the trigger chain for objects.
/// Maps to the OnTrigger dispatch chain in Source-X:
///   1. Object's own @Trigger block
///   2. TYPEDEF / NPC brain triggers
///   3. EVENTS list triggers
///   4. Base def (ITEMDEF/CHARDEF) triggers
///   5. f_onchar_* / f_onitem_* global functions
/// </summary>
public sealed class TriggerRunner
{
    private readonly ScriptInterpreter _interpreter;
    private readonly ResourceHolder _resources;
    private readonly ILogger _logger;

    public TriggerRunner(ScriptInterpreter interpreter, ResourceHolder resources, ILogger<TriggerRunner> logger)
    {
        _interpreter = interpreter;
        _resources = resources;
        _logger = logger;
    }

    /// <summary>Underlying interpreter — exposed so callers can swap in
    /// per-invocation resolvers on its ExpressionParser.</summary>
    public ScriptInterpreter Interpreter => _interpreter;

    /// <summary>
    /// Execute a trigger on a ResourceLink, reading its script on demand.
    /// </summary>
    public TriggerResult RunTrigger(
        ResourceLink link,
        int triggerIndex,
        string triggerName,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args)
    {
        if (!link.IsTriggerActive(triggerIndex))
            return TriggerResult.Default;

        using var scriptFile = link.OpenAtStoredPosition();
        if (scriptFile == null)
            return TriggerResult.Default;

        var sections = scriptFile.ReadAllSections();
        foreach (var section in sections)
        {
            // Find the ON=@TriggerName block
            foreach (var key in section.Keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                    key.Arg.Equals($"@{triggerName}", StringComparison.OrdinalIgnoreCase))
                {
                    int startIdx = section.Keys.IndexOf(key) + 1;
                    var triggerLines = CollectTriggerBody(section.Keys, startIdx);

                    var scope = new ScriptScope();
                    return _interpreter.Execute(triggerLines, target, source, args, scope);
                }
            }
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Execute a trigger on a ResourceLink by name only (no bitmask check).
    /// Used for EVENTS scripts where the bitmask may not be set.
    /// </summary>
    public TriggerResult RunTriggerByName(
        ResourceLink link,
        string triggerName,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args)
    {
        // Verbose body tracing was needed during vendor-restock parser
        // bring-up; now disabled to keep the log readable. Flip to true
        // temporarily when investigating new trigger plumbing issues.
        bool verbose = false;

        using var scriptFile = link.OpenAtStoredPosition();
        if (scriptFile == null)
        {
            if (verbose)
                _logger.LogDebug(
                    "[trig_runner] {Trig} on {Target}: scriptFile=null (link unresolved)",
                    triggerName, target);
            return TriggerResult.Default;
        }

        var sections = scriptFile.ReadAllSections();
        int sectionsScanned = 0;
        foreach (var section in sections)
        {
            sectionsScanned++;
            foreach (var key in section.Keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) &&
                    key.Arg.Equals($"@{triggerName}", StringComparison.OrdinalIgnoreCase))
                {
                    int startIdx = section.Keys.IndexOf(key) + 1;
                    var triggerLines = CollectTriggerBody(section.Keys, startIdx);

                    if (verbose)
                    {
                        _logger.LogDebug(
                            "[trig_runner] {Trig} matched section #{Idx} body lines={N}",
                            triggerName, sectionsScanned, triggerLines.Count);
                        foreach (var ln in triggerLines)
                            _logger.LogDebug(
                                "[trig_runner]   line key='{Key}' hasArg={HasArg} arg='{Arg}'",
                                ln.Key, ln.HasArg, ln.Arg);
                    }

                    var scope = new ScriptScope();
                    return _interpreter.Execute(triggerLines, target, source, args, scope);
                }
            }
        }

        if (verbose)
            _logger.LogDebug(
                "[trig_runner] {Trig} not found in any of {N} sections (target={Target})",
                triggerName, sectionsScanned, target);
        return TriggerResult.Default;
    }

    /// <summary>
    /// Execute a named function from the resource system.
    /// Functions are stored as [FUNCTION name] sections.
    /// </summary>
    public TriggerResult RunFunction(
        string funcName,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args)
    {
        if (!TryRunFunction(funcName, target, source, args, out var result))
            _logger.LogWarning("Function not found: {Name}", funcName);
        return result;
    }

    /// <summary>
    /// Try to execute a named function from the resource system.
    /// Returns false only when the function cannot be resolved.
    /// </summary>
    public bool TryRunFunction(
        string funcName,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args,
        out TriggerResult result)
    {
        result = TriggerResult.Default;

        // Resolve function by defname (registered during script loading)
        var rid = _resources.ResolveDefName(funcName);
        if (!rid.IsValid)
        {
            // Try with f_ prefix if not found (common Sphere convention)
            rid = _resources.ResolveDefName("f_" + funcName);
        }

        ResourceLink? link = rid.IsValid ? _resources.GetResource(rid) : null;
        if (link == null)
            return false;

        using var scriptFile = link.OpenAtStoredPosition();
        if (scriptFile == null)
        {
            _logger.LogWarning("Function script file could not be opened: {Name} at {Path}:{Line}",
                funcName, link.ScriptFilePath, link.ScriptLineNumber);
            return true;
        }

        var sections = scriptFile.ReadAllSections();
        if (sections.Count == 0)
        {
            _logger.LogWarning("Function has no sections: {Name} at {Path}:{Line}",
                funcName, link.ScriptFilePath, link.ScriptLineNumber);
            return true;
        }

        var scope = new ScriptScope();
        result = _interpreter.Execute(sections[0].Keys, target, source, args, scope);
        return true;
    }

    /// <summary>
    /// Execute a SPEECH trigger on a ResourceLink by matching spoken text against ON=*keyword* patterns.
    /// Wildcard rules: *keyword* = contains, keyword* = startsWith, *keyword = endsWith, keyword = exact.
    /// All matches are case-insensitive.
    /// </summary>
    public TriggerResult RunSpeechTrigger(
        ResourceLink link,
        string spokenText,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args)
    {
        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0)
            return TriggerResult.Default;

        string lower = spokenText.ToLowerInvariant();

        for (int i = 0; i < keys.Count; i++)
        {
            if (!keys[i].Key.Equals("ON", StringComparison.OrdinalIgnoreCase) || !keys[i].HasArg)
                continue;

            string pattern = keys[i].Arg.Trim();
            if (pattern.Length == 0 || pattern[0] == '@')
                continue; // Skip @Trigger-style entries

            if (!MatchSpeechPattern(pattern, lower))
                continue;

            // Found a match — collect and execute the body
            var body = CollectTriggerBody(keys, i + 1);
            var scope = new ScriptScope();
            return _interpreter.Execute(body, target, source, args, scope);
        }

        return TriggerResult.Default;
    }

    /// <summary>
    /// Match a SPEECH pattern against spoken text.
    /// Patterns: *keyword* = contains, keyword* = startsWith, *keyword = endsWith, keyword = exact.
    /// </summary>
    private static bool MatchSpeechPattern(string pattern, string lowerText)
    {
        bool startsWithStar = pattern[0] == '*';
        bool endsWithStar = pattern[^1] == '*';

        string keyword = pattern.Trim('*').ToLowerInvariant();
        if (keyword.Length == 0)
            return false;

        if (startsWithStar && endsWithStar)
            return lowerText.Contains(keyword, StringComparison.Ordinal);
        if (startsWithStar)
            return lowerText.EndsWith(keyword, StringComparison.Ordinal);
        if (endsWithStar)
            return lowerText.StartsWith(keyword, StringComparison.Ordinal);
        return lowerText.Equals(keyword, StringComparison.Ordinal);
    }

    private static List<ScriptKey> CollectTriggerBody(List<ScriptKey> allKeys, int startIdx)
    {
        var body = new List<ScriptKey>();
        for (int i = startIdx; i < allKeys.Count; i++)
        {
            string cmd = allKeys[i].Key.ToUpperInvariant();
            // Stop at next ON= trigger or end of section
            if (cmd == "ON" && allKeys[i].HasArg && allKeys[i].Arg.StartsWith('@'))
                break;

            body.Add(allKeys[i]);
        }
        return body;
    }

    /// <summary>Collect body for a dialog-button handler. Stops at ANY next
    /// <c>ON=</c> line (unlike the @Trigger collector, numeric button handlers
    /// are terminated by the next handler, not just by a fresh @Trigger).</summary>
    private static List<ScriptKey> CollectDialogButtonBody(List<ScriptKey> allKeys, int startIdx)
    {
        var body = new List<ScriptKey>();
        for (int i = startIdx; i < allKeys.Count; i++)
        {
            string cmd = allKeys[i].Key.ToUpperInvariant();
            // Accept both ON= and ONBUTTON= as the next-handler delimiter
            // (match TryRunDialogButton's lookup).
            if ((cmd == "ON" || cmd == "ONBUTTON") && allKeys[i].HasArg)
                break;
            body.Add(allKeys[i]);
        }
        return body;
    }

    /// <summary>Run a dialog button handler. Walks the provided section keys,
    /// finds the <c>ON=buttonId</c> (or matching range <c>ON=lo hi</c>) line,
    /// and executes its body. Returns <c>true</c> if a handler matched.</summary>
    public bool TryRunDialogButton(
        ScriptSection buttonSection,
        int buttonId,
        IScriptObj target,
        ITextConsole? source,
        ITriggerArgs? args)
    {
        var keys = buttonSection.Keys;
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            // Sphere script packs use either "ON=buttonId" or the explicit
            // "ONBUTTON=buttonId" form; accept both so imported scripts
            // work without rewrites.
            bool isOn = key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase)
                     || key.Key.Equals("ONBUTTON", StringComparison.OrdinalIgnoreCase);
            if (!isOn || !key.HasArg)
                continue;
            if (key.Arg.StartsWith('@'))
                continue; // @Trigger handlers, not for us

            if (!MatchesButton(key.Arg, buttonId))
                continue;

            var body = CollectDialogButtonBody(keys, i + 1);
            var scope = new ScriptScope();
            _interpreter.Execute(body, target, source, args, scope);
            return true;
        }
        return false;
    }

    /// <summary>Check whether an <c>ON=</c> argument matches a numeric button.
    /// Forms: <c>N</c> (exact), <c>N M</c> or <c>N,M</c> (inclusive range).</summary>
    private static bool MatchesButton(string arg, int buttonId)
    {
        string trimmed = arg.Trim();
        int sep = trimmed.IndexOfAny(new[] { ' ', '\t', ',' });
        if (sep < 0)
        {
            if (ScriptKey.TryParseNumber(trimmed.AsSpan(), out long single))
                return single == buttonId;
            return false;
        }

        string loStr = trimmed[..sep].Trim();
        string hiStr = trimmed[(sep + 1)..].Trim();
        if (!ScriptKey.TryParseNumber(loStr.AsSpan(), out long lo)) return false;
        if (!ScriptKey.TryParseNumber(hiStr.AsSpan(), out long hi)) return false;
        if (lo > hi) (lo, hi) = (hi, lo);
        return buttonId >= lo && buttonId <= hi;
    }
}

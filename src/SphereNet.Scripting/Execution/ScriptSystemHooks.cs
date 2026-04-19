using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Central dispatcher for Source-X style global lifecycle hooks.
/// Produces a consistent trigger arg shape (SRC/ARGO/ARGS/ARGN) and logs failures.
/// </summary>
public sealed class ScriptSystemHooks
{
    private readonly TriggerRunner _runner;
    private readonly ILogger<ScriptSystemHooks> _logger;
    private static readonly Dictionary<string, string[]> ServerHookAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = ["onserver_start"],
        ["exit"] = ["onserver_exit", "onserver_exit_later"],
        ["save"] = ["onserver_save", "onserver_save_before", "onserver_save_ok", "onserver_save_finished"],
        ["resync"] = ["onserver_resync_start", "onserver_resync_success"]
    };
    private static readonly Dictionary<string, string[]> AccountHookAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connect"] = ["login"],
        ["pwchange"] = ["pinchange"]
    };
    private static readonly Dictionary<string, string[]> ClientHookAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unkdata"] = ["unknown_client_data"],
        ["quotaexceed"] = ["exceed_network_quota"]
    };

    public ScriptSystemHooks(TriggerRunner runner, ILogger<ScriptSystemHooks> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public bool Dispatch(
        string functionName,
        IScriptObj? source,
        IScriptObj? argo = null,
        string args = "",
        int argn1 = 0,
        int argn2 = 0,
        int argn3 = 0,
        ITextConsole? console = null)
    {
        IScriptObj? target = source ?? argo;
        if (target == null)
        {
            _logger.LogDebug("Hook skipped (no target): {Function}", functionName);
            return false;
        }

        var triggerArgs = new TriggerArgs(source, argn1, argn2, args)
        {
            Number3 = argn3,
            Object1 = argo,
            Object2 = source
        };

        if (!_runner.TryRunFunction(functionName, target, console, triggerArgs, out var result))
        {
            _logger.LogDebug("Hook function not found: {Function}", functionName);
            return false;
        }

        return result == Core.Enums.TriggerResult.True;
    }

    public bool DispatchServer(string hookSuffix, IScriptObj serverContext, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
    {
        if (Dispatch($"f_onserver_{hookSuffix}", serverContext, null, args, argn1, argn2, argn3))
            return true;

        if (!ServerHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            string function = alias.StartsWith("f_", StringComparison.OrdinalIgnoreCase)
                ? alias
                : $"f_{alias}";
            if (Dispatch(function, serverContext, null, args, argn1, argn2, argn3))
                return true;
        }

        return false;
    }

    public bool DispatchAccount(string hookSuffix, IScriptObj accountObj, IScriptObj? argo = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
    {
        if (Dispatch($"f_onaccount_{hookSuffix}", accountObj, argo, args, argn1, argn2, argn3))
            return true;

        if (!AccountHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            string function = alias.StartsWith("f_", StringComparison.OrdinalIgnoreCase)
                ? alias
                : $"f_onaccount_{alias}";
            if (Dispatch(function, accountObj, argo, args, argn1, argn2, argn3))
                return true;
        }

        return false;
    }

    public bool DispatchClient(string hookSuffix, IScriptObj clientObj, IScriptObj? argo = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0, ITextConsole? console = null)
    {
        if (Dispatch($"f_onclient_{hookSuffix}", clientObj, argo, args, argn1, argn2, argn3, console))
            return true;

        // Source-X compatibility: support legacy/verbose client hook names.
        if (!ClientHookAliases.TryGetValue(hookSuffix, out var aliases))
            return false;

        foreach (string alias in aliases)
        {
            if (Dispatch($"f_onclient_{alias}", clientObj, argo, args, argn1, argn2, argn3, console))
                return true;
        }

        return false;
    }

    public bool DispatchObject(string hookSuffix, IScriptObj obj, IScriptObj? source = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
        => Dispatch($"f_onobj_{hookSuffix}", source ?? obj, obj, args, argn1, argn2, argn3);

    public bool DispatchItem(string hookSuffix, IScriptObj itemObj, IScriptObj? source = null, string args = "", int argn1 = 0, int argn2 = 0, int argn3 = 0)
        => Dispatch($"f_onitem_{hookSuffix}", source ?? itemObj, itemObj, args, argn1, argn2, argn3);

    public bool DispatchPacket(byte opcode, IScriptObj source, IScriptObj? argo = null, string args = "")
        => Dispatch($"f_packet_0x{opcode:X2}", source, argo, args, opcode);
}

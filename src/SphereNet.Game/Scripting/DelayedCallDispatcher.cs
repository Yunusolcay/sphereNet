using SphereNet.Core.Interfaces;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Execution;

namespace SphereNet.Game.Scripting;

/// <summary>
/// Runs a delayed TIMERF/TIMERFMS payload on the object that scheduled it, the way
/// Source-X does when the timer object ticks (CTimedFunction.cpp:43):
///
/// <list type="bullet">
/// <item>SRC is the payload target's TOP-LEVEL object when that is a character —
/// the player carrying the item, not the item — and the server console otherwise.</item>
/// <item>The payload is dispatched as a normal verb line: the built-in verb table
/// first, the script [FUNCTION] only when no verb owns the name
/// (CObjBase::r_Verb, CObjBase.cpp:2134).</item>
/// <item>The function receives its argument through the normal preparation, so the
/// leading numbers arrive as ARGN1/2/3 as well as ARGS
/// (CScriptTriggerArgs::Init, CScriptTriggerArgs.cpp:112).</item>
/// </list>
///
/// This lives here, rather than inline in the host wiring, so the contract is
/// reachable without booting a server.
/// </summary>
public sealed class DelayedCallDispatcher
{
    private readonly Func<TriggerRunner?> _resolveRunner;
    private readonly Func<Character, ITextConsole?>? _resolveClientConsole;
    private readonly ITextConsole _serverConsole;
    private readonly IScriptObj? _serverObj;
    private readonly Action<Exception, string>? _onError;

    /// <param name="resolveRunner">The script runner, read at call time — the host
    /// wires the dispatcher before the runner exists.</param>
    /// <param name="resolveClientConsole">The console of a character's connected
    /// client, or null when they have none (an NPC, or an offline player).</param>
    /// <param name="serverConsole">The console used when the top-level object is not a
    /// character (Source-X g_Serv).</param>
    /// <param name="serverObj">SRC for that same case, when the host has a server
    /// script object to offer.</param>
    public DelayedCallDispatcher(
        Func<TriggerRunner?> resolveRunner,
        Func<Character, ITextConsole?>? resolveClientConsole,
        ITextConsole serverConsole,
        IScriptObj? serverObj = null,
        Action<Exception, string>? onError = null)
    {
        _resolveRunner = resolveRunner;
        _resolveClientConsole = resolveClientConsole;
        _serverConsole = serverConsole;
        _serverObj = serverObj;
        _onError = onError;
    }

    public void Run(ObjBase obj, ObjBase.TimerFEntry entry) =>
        Run(obj, entry.FunctionName, entry.Args);

    public void Run(ObjBase obj, string payloadName, string payloadArgs)
    {
        if (string.IsNullOrWhiteSpace(payloadName))
            return;

        var (src, console) = ResolveCaller(obj);

        // The built-in verb wins the name. Running the script function first let a
        // pack's [FUNCTION REMOVE] shadow the engine's own REMOVE, so a delayed
        // command behaved differently from the same word typed directly.
        try
        {
            if (obj.TryExecuteCommand(payloadName, payloadArgs, console))
                return;
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex, payloadName);
            return;
        }

        var runner = _resolveRunner();
        if (runner == null)
            return;

        var args = new SphereNet.Scripting.Execution.TriggerArgs { Source = src };
        args.InitFromRaw(payloadArgs);
        try
        {
            runner.TryRunFunction(payloadName, obj, console, args, out _);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex, payloadName);
        }
    }

    /// <summary>SRC and the console for a delayed call on <paramref name="obj"/>.</summary>
    public (IScriptObj? Src, ITextConsole Console) ResolveCaller(ObjBase obj)
    {
        if (obj.GetTopLevelObj() is not Character topChar)
            return (_serverObj, _serverConsole);

        // Their own client when they have one - it carries the character context the
        // command handlers read. Otherwise a console standing in for the character, so
        // the line runs at THEIR privilege level: handing every delayed verb the
        // server's console let a player's item run engine commands as an administrator.
        var console = _resolveClientConsole?.Invoke(topChar);
        return (topChar, console ?? new CharacterConsole(topChar));
    }

    /// <summary>A character with no connected client, standing in as the caller of a
    /// delayed line: their name and their privilege level, with nowhere to write.</summary>
    private sealed class CharacterConsole(Character character) : ITextConsole
    {
        public Core.Enums.PrivLevel GetPrivLevel() => character.PrivLevel;
        public string GetName() => character.Name;
        public void SysMessage(string text) { }
    }
}

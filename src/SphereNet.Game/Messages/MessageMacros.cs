using System.Text;
using System.Text.RegularExpressions;

namespace SphereNet.Game.Messages;

/// <summary>
/// In-line macros that DEFMSG_* templates can carry. ServerMessages.GetFormatted
/// only handles printf placeholders -- for character-context-dependent tokens
/// (Source-X <c>&lt;SEX X/Y&gt;</c>, <c>&lt;NAME&gt;</c>, ...) callers funnel
/// the result through <see cref="Resolve"/>.
///
/// Source-X reference: <c>CObjBase::ParseScripts</c> /
/// <c>CChar::ParseText</c> in CCharBase.cpp -- the speech replacement layer
/// runs after GetDefaultMsg lookup and before delivery to the client.
/// </summary>
public static class MessageMacros
{
    // <SEX maleWord/femaleWord> -- captures both halves; / is the delimiter.
    // Embedded < or > are not allowed inside the alternatives, mirroring Source-X.
    private static readonly Regex s_sexRx = new(
        @"<SEX\s+([^/<>]*?)/([^<>]*?)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // <NAME> -- replaced with the speaker's name. Source-X also exposes
    // <CNAME> (capitalised) and <NAME_TITLE>; only the bare form is needed
    // for any DEFMSG default in the current tbl.
    private static readonly Regex s_nameRx = new(
        @"<NAME>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Optional inputs into a single resolve call. Pass <c>null</c> for any
    /// component the caller does not have at hand -- unresolved tags fall
    /// through unchanged so a downstream consumer (or a script) may finish
    /// the substitution.
    /// </summary>
    public readonly record struct Context(bool? IsFemale, string? Name)
    {
        public static readonly Context Empty = new(null, null);

        public static Context FromCharacter(bool isFemale, string? name = null) =>
            new(isFemale, name);
    }

    /// <summary>
    /// Replace every supported &lt;TAG&gt; in <paramref name="text"/> using
    /// <paramref name="ctx"/>. Returns the input unchanged when no tag is
    /// present (fast path) or no relevant context is supplied.
    /// </summary>
    public static string Resolve(string text, Context ctx)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            return text;

        string result = text;

        if (ctx.IsFemale.HasValue)
        {
            bool female = ctx.IsFemale.Value;
            result = s_sexRx.Replace(result, m => female ? m.Groups[2].Value : m.Groups[1].Value);
        }

        if (ctx.Name is not null)
        {
            string name = ctx.Name;
            result = s_nameRx.Replace(result, name);
        }

        return result;
    }

    /// <summary>
    /// Convenience overload: resolve directly from a character's gender.
    /// </summary>
    public static string Resolve(string text, bool isFemale, string? name = null) =>
        Resolve(text, new Context(isFemale, name));

    /// <summary>
    /// Lookup + macro resolve in a single call. Equivalent to
    /// <c>Resolve(ServerMessages.Get(key), ctx)</c> -- preferred when the
    /// caller has neither printf args nor a strongly-typed Msg constant
    /// to point at.
    /// </summary>
    public static string GetResolved(string key, Context ctx) =>
        Resolve(ServerMessages.Get(key), ctx);

    /// <summary>
    /// Format + macro resolve in one shot. Printf substitution runs first
    /// (so args may themselves contain &lt;SEX&gt; markup that the macro
    /// pass then expands), then macro substitution.
    /// </summary>
    public static string FormatResolved(string key, Context ctx, params object[] args)
    {
        string formatted = args.Length == 0
            ? ServerMessages.Get(key)
            : ServerMessages.GetFormatted(key, args);
        return Resolve(formatted, ctx);
    }
}

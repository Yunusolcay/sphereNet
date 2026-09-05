namespace SphereNet.Core.Configuration;

/// <summary>
/// Account name normalisation and validation.
///
/// Port of Source-X <c>CAccount::NameStrip</c> (CAccount.cpp) plus the reserved
/// section names SphereNet's own account reader needs. Account records are written
/// as bare <c>[name]</c> sections — Source-X <c>CAccount::r_Write</c> does the same,
/// so the file stays loadable by a classic server — which means a name that cannot
/// survive that round-trip must never enter the live account table in the first
/// place. Without this gate a name carrying a line break aborts every subsequent
/// account write, and a name shaped like a reserved section is dropped on load.
///
/// Two rule sets, deliberately different:
/// <list type="bullet">
/// <item><see cref="TryNormalize"/> — strict, applied when an account is created.</item>
/// <item><see cref="IsReservedSection"/> / <see cref="IsWritable"/> — loose, matching
/// exactly what the reader skips, so names already present in a legacy file keep
/// loading and saving.</item>
/// </list>
/// </summary>
public static class AccountNameValidator
{
    /// <summary>Source-X MAX_ACCOUNT_NAME_SIZE (sphereproto.h) is a buffer size,
    /// so the usable name length is one less.</summary>
    public const int MaxLength = 29;

    /// <summary>Source-X ACCOUNT_NAME_VALID_CHAR. Despite the name this is the list
    /// handed to Str_GetBare as its <i>strip</i> set, so these characters are
    /// removed rather than allowed.</summary>
    private const string StripChars = @" !""#$%&()*,/:;<=>?@[\]^{|}~";

    /// <summary>Source-X CAccounts::sm_szVerbKeys — an account group verb cannot
    /// double as a section name in the account file.</summary>
    private static readonly string[] ReservedVerbs =
        ["ADD", "ADDMD5", "BLOCKED", "HELP", "JAILED", "UNUSED", "UPDATE"];

    /// <summary>Prefixes a NEW account name may not take. EOF and ACCOUNT come from
    /// Source-X NameStrip; WORLD/SPHERE/GLOBALS/LIST are the save-section families,
    /// rejected here without their trailing space so no near-miss can be created.</summary>
    private static readonly string[] ForbiddenNewNamePrefixes =
        ["EOF", "ACCOUNT", "WORLD", "SPHERE", "GLOBALS", "LIST"];

    /// <summary>Optional obscene-word gate (Source-X g_Cfg.IsObscene). Wired at boot
    /// from the script pack's [OBSCENE] list; unset means no filtering.</summary>
    public static Func<string, bool>? ObsceneChecker { get; set; }

    /// <summary>
    /// Str_GetBare with the account strip set: drop control characters, anything
    /// outside printable ASCII and the punctuation Source-X strips, then truncate
    /// to <see cref="MaxLength"/>.
    /// </summary>
    public static string Strip(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (char c in raw)
        {
            if (sb.Length >= MaxLength) break;
            if (c < ' ' || c >= (char)127) continue;   // control / non-ASCII
            if (StripChars.Contains(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Source-X CAccount::NameStrip — the admission check for a NEW account. Strips
    /// <paramref name="raw"/> to its bare form and reports whether the result is
    /// usable. Never call this to look up an existing account: a legacy file may
    /// hold names that predate these rules.
    /// </summary>
    public static bool TryNormalize(string? raw, out string name, out string? error)
    {
        name = Strip(raw);

        if (name.Length == 0)
        {
            error = "name is empty after stripping unusable characters";
            return false;
        }

        foreach (string prefix in ForbiddenNewNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"name starts with the reserved section prefix '{prefix}'";
                name = string.Empty;
                return false;
            }
        }

        foreach (string verb in ReservedVerbs)
        {
            if (name.Equals(verb, StringComparison.OrdinalIgnoreCase))
            {
                error = $"name collides with the account verb '{verb}'";
                name = string.Empty;
                return false;
            }
        }

        if (ObsceneChecker?.Invoke(name) == true)
        {
            error = "name is on the obscene word list";
            name = string.Empty;
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// True when a save section belongs to a reserved family rather than an account.
    /// The single source of truth for the account reader's skip list — deliberately
    /// looser than <see cref="TryNormalize"/> so an existing account called, say,
    /// "Lister" keeps loading while "LIST foo" stays a list section.
    /// </summary>
    public static bool IsReservedSection(string section) =>
        section.Equals("EOF", StringComparison.OrdinalIgnoreCase) ||
        section.StartsWith("WORLD", StringComparison.OrdinalIgnoreCase) ||
        section.StartsWith("SPHERE", StringComparison.OrdinalIgnoreCase) ||
        section.StartsWith("GLOBALS", StringComparison.OrdinalIgnoreCase) ||
        section.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="name"/> can be written as a section and read back
    /// verbatim. Applied on the save path as a last line of defence, so one bad
    /// legacy name cannot abort the whole account file.
    /// </summary>
    public static bool IsWritable(string? name) =>
        !string.IsNullOrEmpty(name) &&
        string.Equals(name, Strip(name), StringComparison.Ordinal) &&
        !IsReservedSection(name);
}

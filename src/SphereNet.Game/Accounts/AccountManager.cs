using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;

namespace SphereNet.Game.Accounts;

/// <summary>
/// Account manager. Maps to CAccounts in Source-X.
/// Handles account creation, lookup, and persistence.
/// </summary>
public sealed class AccountManager
{
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<AccountManager> _logger;
    private bool _autoCreateAccounts;

    public int Count => _accounts.Count;
    public bool AutoCreateAccounts { get => _autoCreateAccounts; set => _autoCreateAccounts = value; }
    /// <summary>Source-X MD5PASSWORDS. Defaults to on, matching SphereConfig — a
    /// manager built without a config must not silently start storing plaintext.</summary>
    public bool Md5Passwords { get; set; } = true;
    public int DefaultMaxChars { get; set; } = 7;

    /// <summary>Default PrivLevel for auto-created accounts. Maps to DEFAULTCOMMANDLEVEL in sphere.ini.</summary>
    public Core.Enums.PrivLevel DefaultPrivLevel { get; set; } = Core.Enums.PrivLevel.Guest;
    public event Action<Account>? AccountCreated;
    public event Action<Account>? AccountLogin;
    public event Action<Account>? AccountBlocked;
    public event Action<Account>? AccountUnblocked;
    public event Action<Account>? AccountDeleted;
    public event Action<Account>? AccountPasswordChanged;
    /// <summary>Fired after any admin/panel mutation that should be written to disk.</summary>
    public event Action? AccountsChanged;

    public AccountManager(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AccountManager>();
    }

    /// <summary>Look up an account by name. An exact hit wins so every name already
    /// in a legacy file stays reachable; only when that misses is the name stripped
    /// to its bare form and retried, which is what Source-X CAccounts::Account_Find
    /// does before its lookup. The strict creation rules are deliberately NOT
    /// applied here — they would lock out accounts that predate them.</summary>
    public Account? FindAccount(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_accounts.TryGetValue(name, out var exact))
            return exact;

        string stripped = AccountNameValidator.Strip(name);
        if (stripped.Length == 0 || stripped.Equals(name, StringComparison.Ordinal))
            return null;
        return _accounts.GetValueOrDefault(stripped);
    }

    /// <summary>Return the account at a zero-based index in a STABLE (name-ordered)
    /// sequence, or null when out of range. Source-X <c>SERV.ACCOUNT.n</c> indexed
    /// access — admin dialogs iterate 0..Count-1 to list accounts, so the order must
    /// be deterministic across reads (a raw dictionary enumeration is not).</summary>
    public Account? GetByIndex(int index)
    {
        if (index < 0 || index >= _accounts.Count)
            return null;
        return _accounts.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ElementAt(index);
    }

    /// <summary>
    /// Authenticate: find or auto-create, then check password.
    /// Returns null if auth fails.
    /// </summary>
    public Account? Authenticate(string name, string password)
    {
        var account = FindAccount(name);
        if (account == null)
        {
            if (!_autoCreateAccounts)
            {
                _logger.LogWarning("Account '{Name}' not found (auto-create disabled)", name);
                return null;
            }

            _logger.LogWarning("[AUTH] Account '{Name}' not found, auto-creating (DefaultPrivLevel={Def})",
                name, DefaultPrivLevel);
            account = CreateAccount(name, password);
            if (account == null) return null;
        }
        else
        {
            _logger.LogDebug("[AUTH] Account '{Name}' found, PLEVEL={Level}({LevelInt})",
                name, account.PrivLevel, (int)account.PrivLevel);
        }

        if (account.IsBanned)
        {
            _logger.LogWarning("Account '{Name}' is banned", name);
            AccountBlocked?.Invoke(account);
            return null;
        }

        if (!account.CheckPassword(password))
        {
            _logger.LogWarning("Wrong password for account '{Name}'", name);
            return null;
        }

        // A classic account file may hold the password verbatim. With hashing on,
        // convert it once the correct password has actually been presented — this is
        // the migration path Source-X performs at load time. With hashing off,
        // plaintext IS the storage form, so there is nothing to upgrade.
        if (account.UseMd5Passwords && PasswordHelper.NeedsUpgrade(account.PasswordHash))
        {
            account.PasswordHash = PasswordHelper.StoreForm(password, useMd5: true);
            _logger.LogInformation("Password hash upgraded for account '{Name}'", name);
            NotifyAccountsChanged();
        }

        account.LastLogin = DateTime.UtcNow;
        AccountLogin?.Invoke(account);
        return account;
    }

    public Account? CreateAccount(string name, string password)
    {
        // Source-X CAccount::CAccount runs NameStrip before the account exists.
        // A name that survives creation but not the save file (a control character,
        // or a reserved section prefix) silently drops the account on the next
        // restart, or aborts the whole account write, so it is rejected here.
        if (!AccountNameValidator.TryNormalize(name, out string normalized, out string? reason))
        {
            _logger.LogWarning("Rejected account name '{Name}': {Reason}",
                Sanitize(name), reason);
            return null;
        }
        name = normalized;

        if (_accounts.ContainsKey(name))
        {
            _logger.LogWarning("Account '{Name}' already exists", name);
            return null;
        }

        var account = new Account
        {
            Name = name,
            PrivLevel = DefaultPrivLevel,
            UseMd5Passwords = Md5Passwords,
            MaxChars = DefaultMaxChars,
        };
        account.SetPassword(password);
        _accounts[name] = account;
        _logger.LogInformation("Account '{Name}' created", name);
        AccountCreated?.Invoke(account);
        NotifyAccountsChanged();
        return account;
    }

    /// <summary>Render a rejected name safely for the log — control characters in
    /// a name coming off the wire must not be able to forge log lines.</summary>
    private static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "<empty>";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(c < ' ' || c >= (char)127 ? '?' : c);
        return sb.ToString();
    }

    public bool DeleteAccount(string name)
    {
        if (!_accounts.TryGetValue(name, out var account))
            return false;

        _accounts.Remove(name);
        AccountDeleted?.Invoke(account);
        NotifyAccountsChanged();
        return true;
    }

    public bool SetAccountPassword(string name, string newPassword)
    {
        var account = FindAccount(name);
        if (account == null)
            return false;
        account.SetPassword(newPassword);
        AccountPasswordChanged?.Invoke(account);
        NotifyAccountsChanged();
        return true;
    }

    public bool SetAccountBlocked(string name, bool blocked)
    {
        var account = FindAccount(name);
        if (account == null)
            return false;
        account.IsBanned = blocked;
        if (blocked)
            AccountBlocked?.Invoke(account);
        else
            AccountUnblocked?.Invoke(account);
        NotifyAccountsChanged();
        return true;
    }

    public bool SetAccountPrivLevel(string name, Core.Enums.PrivLevel level)
    {
        var account = FindAccount(name);
        if (account == null)
            return false;
        account.PrivLevel = level;
        NotifyAccountsChanged();
        return true;
    }

    private void NotifyAccountsChanged() => AccountsChanged?.Invoke();

    public IEnumerable<Account> GetAllAccounts() => _accounts.Values;

    /// <summary>Inject a fully-populated account object (typically from disk
    /// during load). Used by persistence layer — replaces any existing entry
    /// under the same name so repeated loads are idempotent.</summary>
    public void AddLoaded(Account account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Name)) return;
        account.UseMd5Passwords = Md5Passwords;
        _accounts[account.Name] = account;
    }
}

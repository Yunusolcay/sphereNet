using Microsoft.Extensions.Logging;

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

    /// <summary>Default PrivLevel for auto-created accounts. Maps to DEFAULTCOMMANDLEVEL in sphere.ini.</summary>
    public Core.Enums.PrivLevel DefaultPrivLevel { get; set; } = Core.Enums.PrivLevel.Guest;
    public event Action<Account>? AccountCreated;
    public event Action<Account>? AccountLogin;
    public event Action<Account>? AccountBlocked;
    public event Action<Account>? AccountUnblocked;
    public event Action<Account>? AccountDeleted;
    public event Action<Account>? AccountPasswordChanged;

    public AccountManager(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AccountManager>();
    }

    public Account? FindAccount(string name) =>
        _accounts.GetValueOrDefault(name);

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

            account = CreateAccount(name, password);
            if (account == null) return null;
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

        account.LastLogin = DateTime.UtcNow;
        AccountLogin?.Invoke(account);
        return account;
    }

    public Account? CreateAccount(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_accounts.ContainsKey(name))
        {
            _logger.LogWarning("Account '{Name}' already exists", name);
            return null;
        }

        var account = new Account
        {
            Name = name,
            PrivLevel = DefaultPrivLevel,
        };
        account.SetPassword(password);
        _accounts[name] = account;
        _logger.LogInformation("Account '{Name}' created", name);
        AccountCreated?.Invoke(account);
        return account;
    }

    public bool DeleteAccount(string name)
    {
        if (!_accounts.TryGetValue(name, out var account))
            return false;

        _accounts.Remove(name);
        AccountDeleted?.Invoke(account);
        return true;
    }

    public bool SetAccountPassword(string name, string newPassword)
    {
        var account = FindAccount(name);
        if (account == null)
            return false;
        account.SetPassword(newPassword);
        AccountPasswordChanged?.Invoke(account);
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
        return true;
    }

    public IEnumerable<Account> GetAllAccounts() => _accounts.Values;

    /// <summary>Inject a fully-populated account object (typically from disk
    /// during load). Used by persistence layer — replaces any existing entry
    /// under the same name so repeated loads are idempotent.</summary>
    public void AddLoaded(Account account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Name)) return;
        _accounts[account.Name] = account;
    }
}

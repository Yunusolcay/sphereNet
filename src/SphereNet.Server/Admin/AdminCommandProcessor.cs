using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;

namespace SphereNet.Server.Admin;

/// <summary>
/// Shared command processor for admin commands.
/// Used by both TelnetConsole and the server console input.
/// </summary>
public sealed class AdminCommandProcessor
{
    private readonly GameWorld _world;
    private readonly AccountManager _accounts;
    private readonly SphereConfig _config;
    private readonly Func<int> _getActiveConnections;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HashSet<string> _blockedIPs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Check if an IP address is blocked.</summary>
    public bool IsIPBlocked(string ip) => _blockedIPs.Contains(ip);

    public event Action? OnSaveRequested;
    public event Action? OnShutdownRequested;
    public event Action? OnResyncRequested;
    public event Action<string>? OnBroadcast;
    public event Action<string, PrivLevel>? OnAccountPrivLevelChanged;
    public event Action? OnRespawnRequested;
    public event Action? OnRestockRequested;
    public event Action<Action<string>>? OnDebugToggleRequested;
    public event Action<Action<string>>? OnScriptDebugToggleRequested;

    public AdminCommandProcessor(GameWorld world, AccountManager accounts,
        SphereConfig config, Func<int> getActiveConnections, ILoggerFactory loggerFactory)
    {
        _world = world;
        _accounts = accounts;
        _config = config;
        _getActiveConnections = getActiveConnections;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Process a command line. Returns response lines via the output action.
    /// Returns false if the session should close (quit/exit).
    /// </summary>
    public bool ProcessCommand(string input, Action<string> output)
    {
        if (string.IsNullOrWhiteSpace(input)) return true;

        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToUpperInvariant();
        string args = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "HELP":
                output("Commands:");
                output("  STATUS / INFO              - World statistics");
                output("  ACCOUNT                    - List all accounts");
                output("  ACCOUNT ADD <name> <pass>  - Create account");
                output("  ACCOUNT <name>             - Show account info");
                output("  ACCOUNT <name> PASSWORD <pass> - Change password");
                output("  ACCOUNT <name> PLEVEL <0-7>    - Set privilege level");
                output("  ACCOUNT <name> DELETE      - Delete account");
                output("  ACCOUNT <name> BAN         - Ban account");
                output("  ACCOUNT <name> UNBAN       - Unban account");
                output("  SAVE                       - Save world & accounts");
                output("  RESYNC / RY                - Reload scripts");
                output("  DEBUG                      - Toggle DebugPackets on/off");
                output("  SCRIPTDEBUG                - Toggle ScriptDebug on/off");
                output("  BROADCAST <msg>            - Message to all players");
                output("  WHO                        - Online connections");
                output("  INFORMATION                - Server information");
                output("  LOG <msg>                  - Write to server log");
                output("  RESPAWN                    - Respawn all NPCs");
                output("  RESTOCK                    - Restock all vendors");
                output("  GARBAGE                    - Force garbage collection");
                output("  BLOCKIP <ip>               - Block an IP address");
                output("  UNBLOCKIP <ip>             - Unblock an IP address");
                output("  SHUTDOWN                   - Shutdown server");
                output("  QUIT / EXIT                - Close session");
                break;

            case "STATUS":
            case "INFO":
                var (chars, items, sectors) = _world.GetStats();
                output($"Characters: {chars}");
                output($"Items: {items}");
                output($"Sectors: {sectors}");
                output($"Connections: {_getActiveConnections()}");
                output($"Accounts: {_accounts.Count}");
                output($"Ticks: {_world.TickCount}");
                break;

            case "SAVE":
                output("Save requested...");
                OnSaveRequested?.Invoke();
                break;

            case "SHUTDOWN":
                output("Shutdown initiated...");
                OnShutdownRequested?.Invoke();
                break;

            case "ACCOUNT":
                HandleAccountCommand(args, output);
                break;

            case "BROADCAST":
                output($"[Broadcast]: {args}");
                OnBroadcast?.Invoke(args);
                break;

            case "WHO":
                output($"Online connections: {_getActiveConnections()}");
                break;

            case "RESYNC":
            case "RY":
                output("ReSync: reloading modified script files...");
                OnResyncRequested?.Invoke();
                break;


            case "DEBUG":
                OnDebugToggleRequested?.Invoke(output);
                break;

            case "SCRIPTDEBUG":
                OnScriptDebugToggleRequested?.Invoke(output);
                break;

            case "INFORMATION":
            {
                output($"SphereNet Server v1.0");
                output($"Server: {_config.ServName}");
                var (c, i, s) = _world.GetStats();
                output($"Characters: {c}, Items: {i}, Sectors: {s}");
                output($"Connections: {_getActiveConnections()}");
                output($"Accounts: {_accounts.Count}");
                output($"Memory: {GC.GetTotalMemory(false) / 1024} KB");
                break;
            }

            case "LOG":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    var logger = _loggerFactory.CreateLogger("ScriptLog");
                    logger.LogInformation("{Message}", args);
                    output($"Logged: {args}");
                }
                else
                    output("Usage: LOG <message>");
                break;

            case "RESPAWN":
                output("Respawn requested...");
                OnRespawnRequested?.Invoke();
                break;

            case "RESTOCK":
                output("Restock requested...");
                OnRestockRequested?.Invoke();
                break;

            case "GARBAGE":
                output("Forcing garbage collection...");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                output($"GC complete. Memory: {GC.GetTotalMemory(true) / 1024} KB");
                break;

            case "BLOCKIP":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    _blockedIPs.Add(args.Trim());
                    output($"IP blocked: {args.Trim()}");
                }
                else
                    output("Usage: BLOCKIP <address>");
                break;

            case "UNBLOCKIP":
                if (!string.IsNullOrWhiteSpace(args))
                {
                    if (_blockedIPs.Remove(args.Trim()))
                        output($"IP unblocked: {args.Trim()}");
                    else
                        output($"IP not in block list: {args.Trim()}");
                }
                else
                    output("Usage: UNBLOCKIP <address>");
                break;

            case "QUIT":
            case "EXIT":
                output("Bye.");
                return false;

            default:
                output($"Unknown command: {cmd}");
                break;
        }

        return true;
    }

    private void HandleAccountCommand(string args, Action<string> output)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            // List all accounts
            var allAccounts = _accounts.GetAllAccounts().ToList();
            output($"Accounts ({allAccounts.Count}):");
            foreach (var acc in allAccounts)
            {
                string flags = acc.IsBanned ? " [BANNED]" : "";
                output($"  {acc.Name} (PLEVEL={acc.PrivLevel}{flags})");
            }
            return;
        }

        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // ACCOUNT ADD <name> <pass>
        if (parts[0].Equals("ADD", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                output("Usage: ACCOUNT ADD <name> <password>");
                return;
            }
            string name = parts[1];
            string pass = parts[2];
            var created = _accounts.CreateAccount(name, pass);
            if (created != null)
                output($"Account '{name}' created.");
            else
                output($"Failed to create account '{name}' (already exists?).");
            return;
        }

        // ACCOUNT <name> [subcommand]
        string accountName = parts[0];
        var account = _accounts.FindAccount(accountName);

        if (parts.Length == 1)
        {
            // Show account info
            if (account == null)
            {
                output($"Account '{accountName}' not found.");
                return;
            }
            output($"Account: {account.Name}");
            output($"  PLEVEL: {account.PrivLevel} ({(int)account.PrivLevel})");
            output($"  Banned: {account.IsBanned}");
            output($"  LastIP: {account.LastIp}");
            output($"  LastLogin: {account.LastLogin}");
            output($"  Created: {account.CreateDate}");
            output($"  Characters: {account.CharCount}");
            return;
        }

        string subCmd = parts[1].ToUpperInvariant();

        switch (subCmd)
        {
            case "PASSWORD":
                if (parts.Length < 3)
                {
                    output("Usage: ACCOUNT <name> PASSWORD <newpass>");
                    return;
                }
                if (account == null) { output($"Account '{accountName}' not found."); return; }
                _accounts.SetAccountPassword(accountName, parts[2]);
                output($"Password changed for '{accountName}'.");
                break;

            case "PLEVEL":
                if (parts.Length < 3 || !int.TryParse(parts[2], out int plevel) || plevel < 0 || plevel > 7)
                {
                    output("Usage: ACCOUNT <name> PLEVEL <0-7>");
                    return;
                }
                if (account == null) { output($"Account '{accountName}' not found."); return; }
                account.PrivLevel = (PrivLevel)plevel;
                output($"PLEVEL for '{accountName}' set to {account.PrivLevel} ({plevel}).");
                OnAccountPrivLevelChanged?.Invoke(accountName, account.PrivLevel);
                break;

            case "DELETE":
                if (_accounts.DeleteAccount(accountName))
                    output($"Account '{accountName}' deleted.");
                else
                    output($"Account '{accountName}' not found.");
                break;

            case "BAN":
            case "BLOCK":
                if (account == null) { output($"Account '{accountName}' not found."); return; }
                _accounts.SetAccountBlocked(accountName, true);
                output($"Account '{accountName}' banned.");
                break;

            case "UNBAN":
                if (account == null) { output($"Account '{accountName}' not found."); return; }
                _accounts.SetAccountBlocked(accountName, false);
                output($"Account '{accountName}' unbanned.");
                break;

            default:
                output($"Unknown account subcommand: {subCmd}");
                output("Use HELP for available commands.");
                break;
        }
    }

}

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Runtime DB bridge for script db.* verbs.
/// Supports multiple named connections with per-connection settings.
/// Each connection maintains its own rowset context for db.row access.
/// </summary>
public sealed class ScriptDbAdapter : IDisposable
{
    private readonly ILogger<ScriptDbAdapter> _logger;
    private readonly ConcurrentDictionary<string, DbSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private string _activeSessionName = "default";

    public ScriptDbAdapter(ILogger<ScriptDbAdapter> logger)
    {
        _logger = logger;
    }

    /// <summary>Register a connection configuration. Does not connect yet.</summary>
    public void RegisterConnection(DbConnectionConfig config)
    {
        var session = new DbSession(config, _logger);
        _sessions[config.Name] = session;
        _logger.LogInformation("Registered DB connection '{Name}' -> {Host}/{Db}",
            config.Name, config.Host, config.Database);
    }

    /// <summary>Name of the currently active connection for DB.QUERY/EXECUTE/ROW.</summary>
    public string ActiveSessionName
    {
        get => _activeSessionName;
        set => _activeSessionName = value;
    }

    /// <summary>True if the active session is connected.</summary>
    public bool IsConnected => GetActiveSession()?.IsConnected ?? false;

    /// <summary>Check if a specific named connection is connected.</summary>
    public bool IsConnected_Named(string name)
    {
        return _sessions.TryGetValue(name, out var s) && s.IsConnected;
    }

    /// <summary>Get the list of registered connection names.</summary>
    public IEnumerable<string> ConnectionNames => _sessions.Keys;

    /// <summary>Connect the active session using its registered config.</summary>
    public bool Connect(out string error)
    {
        return Connect(_activeSessionName, out error);
    }

    /// <summary>Connect a named session using its registered config.</summary>
    public bool Connect(string name, out string error)
    {
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Connect(out error);
    }

    /// <summary>Connect with explicit provider/connection string (legacy).</summary>
    public bool Connect(string providerInvariantName, string connectionString, out string error)
    {
        error = "";
        var session = GetOrCreateActiveSession();
        return session.Connect(providerInvariantName, connectionString, out error);
    }

    /// <summary>Connect the default session (backward compat).</summary>
    public bool ConnectDefault(out string error)
    {
        return Connect("default", out error);
    }

    /// <summary>Close the active session.</summary>
    public void Close()
    {
        Close(_activeSessionName);
    }

    /// <summary>Close a named session.</summary>
    public void Close(string name)
    {
        if (_sessions.TryGetValue(name, out var session))
            session.Close();
    }

    /// <summary>Close all sessions.</summary>
    public void CloseAll()
    {
        foreach (var session in _sessions.Values)
            session.Close();
    }

    /// <summary>Switch the active connection to the named one.</summary>
    public bool Select(string name, out string error)
    {
        error = "";
        if (!_sessions.ContainsKey(name))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        _activeSessionName = name;
        return true;
    }

    /// <summary>Execute a non-query SQL on the active session.</summary>
    public bool Execute(string sql, out int affectedRows, out string error)
    {
        return Execute(_activeSessionName, sql, out affectedRows, out error);
    }

    /// <summary>Execute a non-query SQL on a named session.</summary>
    public bool Execute(string name, string sql, out int affectedRows, out string error)
    {
        affectedRows = 0;
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Execute(sql, out affectedRows, out error);
    }

    /// <summary>Execute a query on the active session.</summary>
    public bool Query(string sql, out int rowCount, out string error)
    {
        return Query(_activeSessionName, sql, out rowCount, out error);
    }

    /// <summary>Execute a query on a named session.</summary>
    public bool Query(string name, string sql, out int rowCount, out string error)
    {
        rowCount = 0;
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Query(sql, out rowCount, out error);
    }

    /// <summary>Resolve db.row.* variables from the active session's last query result.</summary>
    public bool TryResolveRowValue(string key, out string value)
    {
        return TryResolveRowValue(_activeSessionName, key, out value);
    }

    /// <summary>Resolve db.row.* variables from a named session's last query result.</summary>
    public bool TryResolveRowValue(string name, string key, out string value)
    {
        value = "";
        if (!_sessions.TryGetValue(name, out var session))
            return false;
        return session.TryResolveRowValue(key, out value);
    }

    // Legacy compat properties for default session
    public string DefaultProvider
    {
        get => _sessions.TryGetValue("default", out var s) ? s.Config?.Provider ?? "" : "";
        set
        {
            var session = GetOrCreateDefaultSession();
            if (session.Config != null) session.Config.Provider = value;
        }
    }

    public string DefaultConnectionString
    {
        get => _sessions.TryGetValue("default", out var s) ? s.Config?.BuildConnectionString() ?? "" : "";
        set
        {
            // Legacy: store raw connection string
            var session = GetOrCreateDefaultSession();
            session.LegacyConnectionString = value;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Close();
        _sessions.Clear();
    }

    private DbSession? GetActiveSession()
    {
        _sessions.TryGetValue(_activeSessionName, out var session);
        return session;
    }

    private DbSession GetOrCreateActiveSession()
    {
        if (_sessions.TryGetValue(_activeSessionName, out var session))
            return session;
        session = new DbSession(new DbConnectionConfig { Name = _activeSessionName }, _logger);
        _sessions[_activeSessionName] = session;
        return session;
    }

    private DbSession GetOrCreateDefaultSession()
    {
        if (_sessions.TryGetValue("default", out var session))
            return session;
        session = new DbSession(new DbConnectionConfig { Name = "default" }, _logger);
        _sessions["default"] = session;
        return session;
    }

    /// <summary>
    /// Per-connection session. Holds its own DbConnection, lock, and rowset.
    /// </summary>
    private sealed class DbSession
    {
        private readonly object _sync = new();
        private readonly ILogger _logger;
        private DbConnection? _connection;
        private DataTable? _rowTable;
        private Thread? _workerThread;
        private readonly BlockingCollection<Action>? _workQueue;

        public DbConnectionConfig? Config { get; }
        public string? LegacyConnectionString { get; set; }

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _connection != null && _connection.State == ConnectionState.Open;
                }
            }
        }

        public DbSession(DbConnectionConfig config, ILogger logger)
        {
            Config = config;
            _logger = logger;
            if (config.UseThread)
                _workQueue = new BlockingCollection<Action>();
        }

        public bool Connect(out string error)
        {
            error = "";
            if (Config == null)
            {
                error = "No configuration available.";
                return false;
            }

            string provider = Config.Provider;
            string connStr = LegacyConnectionString ?? Config.BuildConnectionString();

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connStr))
            {
                error = "DB provider or connection string is not configured.";
                return false;
            }

            return Connect(provider, connStr, out error);
        }

        public bool Connect(string providerInvariantName, string connectionString, out string error)
        {
            error = "";
            lock (_sync)
            {
                try
                {
                    CloseInternal();

                    var factory = DbProviderFactories.GetFactory(providerInvariantName);
                    var connection = factory.CreateConnection();
                    if (connection == null)
                    {
                        error = $"Provider '{providerInvariantName}' did not create a connection instance.";
                        return false;
                    }

                    connection.ConnectionString = connectionString;
                    connection.Open();
                    _connection = connection;
                    _logger.LogInformation("DB session '{Name}' connected with provider {Provider}",
                        Config?.Name ?? "?", providerInvariantName);

                    if (Config?.UseThread == true && _workerThread == null)
                        StartWorkerThread();

                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' connect failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                CloseInternal();
            }
            StopWorkerThread();
        }

        public bool Execute(string sql, out int affectedRows, out string error)
        {
            affectedRows = 0;
            error = "";

            if (Config?.UseThread == true && _workQueue != null)
            {
                int result = 0;
                string? err = null;
                bool ok = false;
                using var done = new ManualResetEventSlim(false);
                _workQueue.Add(() =>
                {
                    ok = ExecuteInternal(sql, out result, out err);
                    done.Set();
                });
                done.Wait(TimeSpan.FromSeconds(Config.ReadTimeout > 0 ? Config.ReadTimeout : 30));
                affectedRows = result;
                error = err ?? "";
                return ok;
            }

            return ExecuteInternal(sql, out affectedRows, out error);
        }

        public bool Query(string sql, out int rowCount, out string error)
        {
            rowCount = 0;
            error = "";

            if (Config?.UseThread == true && _workQueue != null)
            {
                int result = 0;
                string? err = null;
                bool ok = false;
                using var done = new ManualResetEventSlim(false);
                _workQueue.Add(() =>
                {
                    ok = QueryInternal(sql, out result, out err);
                    done.Set();
                });
                done.Wait(TimeSpan.FromSeconds(Config.ReadTimeout > 0 ? Config.ReadTimeout : 30));
                rowCount = result;
                error = err ?? "";
                return ok;
            }

            return QueryInternal(sql, out rowCount, out error);
        }

        public bool TryResolveRowValue(string key, out string value)
        {
            value = "";
            lock (_sync)
            {
                if (_rowTable == null)
                    return false;

                if (key.Equals("db.row.numrows", StringComparison.OrdinalIgnoreCase))
                {
                    value = _rowTable.Rows.Count.ToString();
                    return true;
                }

                if (!key.StartsWith("db.row.", StringComparison.OrdinalIgnoreCase))
                    return false;

                string[] parts = key.Split('.', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    return false;

                if (!int.TryParse(parts[2], out int rowIndex))
                    return false;
                if (rowIndex < 0 || rowIndex >= _rowTable.Rows.Count)
                    return false;

                string colKey = parts[3];
                object? cell = null;
                if (int.TryParse(colKey, out int colIndex))
                {
                    if (colIndex < 0 || colIndex >= _rowTable.Columns.Count)
                        return false;
                    cell = _rowTable.Rows[rowIndex][colIndex];
                }
                else if (_rowTable.Columns.Contains(colKey))
                {
                    cell = _rowTable.Rows[rowIndex][colKey];
                }
                else
                {
                    return false;
                }

                value = cell?.ToString() ?? "";
                return true;
            }
        }

        private bool ExecuteInternal(string sql, out int affectedRows, out string error)
        {
            affectedRows = 0;
            error = "";
            lock (_sync)
            {
                if (!EnsureConnection(out error))
                    return false;

                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = sql;
                    if (Config?.ReadTimeout > 0)
                        cmd.CommandTimeout = Config.ReadTimeout;
                    affectedRows = cmd.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' execute failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        private bool QueryInternal(string sql, out int rowCount, out string error)
        {
            rowCount = 0;
            error = "";
            lock (_sync)
            {
                if (!EnsureConnection(out error))
                    return false;

                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = sql;
                    if (Config?.ReadTimeout > 0)
                        cmd.CommandTimeout = Config.ReadTimeout;
                    using var reader = cmd.ExecuteReader();
                    var table = new DataTable();
                    table.Load(reader);
                    _rowTable = table;
                    rowCount = table.Rows.Count;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' query failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        private bool EnsureConnection(out string error)
        {
            error = "";
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                if (Config?.KeepAlive == true && Config.Host.Length > 0)
                {
                    return Connect(out error);
                }
                error = "DB is not connected.";
                return false;
            }
            return true;
        }

        private void CloseInternal()
        {
            _rowTable = null;
            if (_connection == null) return;

            try
            {
                _connection.Close();
                _connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DB session '{Name}' close failed", Config?.Name ?? "?");
            }
            _connection = null;
        }

        private void StartWorkerThread()
        {
            if (_workQueue == null) return;
            _workerThread = new Thread(() =>
            {
                foreach (var action in _workQueue.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DB worker thread error for '{Name}'", Config?.Name ?? "?");
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"DB-Worker-{Config?.Name ?? "?"}"
            };
            _workerThread.Start();
        }

        private void StopWorkerThread()
        {
            _workQueue?.CompleteAdding();
            _workerThread?.Join(TimeSpan.FromSeconds(5));
            _workerThread = null;
        }
    }
}

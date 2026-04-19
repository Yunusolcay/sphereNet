using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Runtime DB bridge for script db.* verbs.
/// Keeps a single session/rowset context similar to Source-X db.row access.
/// </summary>
public sealed class ScriptDbAdapter
{
    private readonly object _sync = new();
    private readonly ILogger<ScriptDbAdapter> _logger;
    private DbConnection? _connection;
    private DataTable? _rowTable;

    public ScriptDbAdapter(ILogger<ScriptDbAdapter> logger)
    {
        _logger = logger;
    }

    public string DefaultProvider { get; set; } = "";
    public string DefaultConnectionString { get; set; } = "";

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
                _logger.LogInformation("ScriptDbAdapter connected with provider {Provider}", providerInvariantName);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "ScriptDbAdapter connect failed");
                return false;
            }
        }
    }

    public bool ConnectDefault(out string error)
    {
        if (string.IsNullOrWhiteSpace(DefaultProvider) || string.IsNullOrWhiteSpace(DefaultConnectionString))
        {
            error = "db provider/connection string is not configured";
            return false;
        }
        return Connect(DefaultProvider, DefaultConnectionString, out error);
    }

    public void Close()
    {
        lock (_sync)
        {
            CloseInternal();
        }
    }

    public bool Execute(string sql, out int affectedRows, out string error)
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
                affectedRows = cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "ScriptDbAdapter execute failed");
                return false;
            }
        }
    }

    public bool Query(string sql, out int rowCount, out string error)
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
                _logger.LogWarning(ex, "ScriptDbAdapter query failed");
                return false;
            }
        }
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

    private bool EnsureConnection(out string error)
    {
        error = "";
        if (_connection == null || _connection.State != ConnectionState.Open)
        {
            error = "db is not connected";
            return false;
        }
        return true;
    }

    private void CloseInternal()
    {
        _rowTable = null;
        if (_connection == null)
            return;

        try
        {
            _connection.Close();
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ScriptDbAdapter close failed");
        }

        _connection = null;
    }
}

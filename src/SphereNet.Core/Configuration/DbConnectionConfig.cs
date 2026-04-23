namespace SphereNet.Core.Configuration;

/// <summary>
/// Per-connection database settings. Loaded from [MYSQL name] INI sections
/// or synthesized from legacy [SPHERE] MySQL* keys.
/// </summary>
public sealed class DbConnectionConfig
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "MySqlConnector";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";

    /// <summary>Keep the connection alive between queries (persistent).</summary>
    public bool KeepAlive { get; set; }

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectTimeout { get; set; } = 30;

    /// <summary>Read (command) timeout in seconds.</summary>
    public int ReadTimeout { get; set; } = 30;

    /// <summary>Write timeout in seconds (provider-dependent).</summary>
    public int WriteTimeout { get; set; } = 30;

    /// <summary>Run queries on a dedicated background thread.</summary>
    public bool UseThread { get; set; }

    /// <summary>Auto-connect on server startup.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>True if this is a SQLite provider.</summary>
    public bool IsSqlite => Provider.Contains("Sqlite", System.StringComparison.OrdinalIgnoreCase);

    public string BuildConnectionString()
    {
        if (IsSqlite)
            return BuildSqliteConnectionString();

        return BuildMySqlConnectionString();
    }

    private string BuildMySqlConnectionString()
    {
        var parts = new System.Text.StringBuilder(256);
        parts.Append($"Server={Host};");
        if (Port != 3306)
            parts.Append($"Port={Port};");
        parts.Append($"User ID={User};Password={Password};Database={Database};");
        parts.Append($"Connection Timeout={ConnectTimeout};");
        parts.Append($"Default Command Timeout={ReadTimeout};");
        if (KeepAlive)
            parts.Append("Keepalive=60;");
        return parts.ToString();
    }

    private string BuildSqliteConnectionString()
    {
        var parts = new System.Text.StringBuilder(128);
        parts.Append($"Data Source={Database};");
        if (!string.IsNullOrWhiteSpace(Password))
            parts.Append($"Password={Password};");
        return parts.ToString();
    }
}

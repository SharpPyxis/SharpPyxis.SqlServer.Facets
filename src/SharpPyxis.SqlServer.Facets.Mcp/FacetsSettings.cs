using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets.Mcp;

/// <summary>
/// What the server reads from its environment: how many rows a query may return, where its
/// connections are stored, and the single connection an environment variable may declare instead of
/// that file.
/// </summary>
internal sealed record FacetsSettings(int MaxRows, string ConfigPath, ConnectionEntry? EnvironmentConnection)
{
    /// <summary>How many rows a query returns at most when nothing else caps it.</summary>
    public const int DefaultMaxRows = 200;

    /// <summary>
    /// Reads <c>FACETS_MAX_ROWS</c>, <c>FACETS_CONFIG_PATH</c>, <c>CONNECTION_STRING</c> and
    /// <c>FACETS_DESCRIPTION</c>, all optional. <c>CONNECTION_STRING</c> declares one connection without
    /// the encrypted file, which is how the server runs where DPAPI does not.
    /// </summary>
    public static FacetsSettings FromEnvironment()
    {
        var configPath = Environment.GetEnvironmentVariable("FACETS_CONFIG_PATH") is { Length: > 0 } path
            ? path
            : ConnectionStore.DefaultPath;

        return new FacetsSettings(ReadMaxRows(), configPath, ReadEnvironmentConnection());
    }

    // A cap the model could raise is no cap at all, so it lives in the environment like the
    // connections do. An unusable value stops the server at startup rather than at the first call.
    private static int ReadMaxRows()
    {
        if (Environment.GetEnvironmentVariable("FACETS_MAX_ROWS") is not { Length: > 0 } value)
            return DefaultMaxRows;

        if (!int.TryParse(value, out var maxRows) || maxRows <= 0)
            throw new InvalidOperationException($"FACETS_MAX_ROWS must be a positive integer, not '{value}'.");

        return maxRows;
    }

    private static ConnectionEntry? ReadEnvironmentConnection()
    {
        if (Environment.GetEnvironmentVariable("CONNECTION_STRING") is not { Length: > 0 } connectionString)
            return null;

        var builder = new SqlConnectionStringBuilder(connectionString);
        var label = Environment.GetEnvironmentVariable("FACETS_DESCRIPTION") is { Length: > 0 } value ? value : null;

        return new ConnectionEntry
        {
            Id = 0,
            Server = builder.DataSource,
            Database = builder.InitialCatalog is { Length: > 0 } catalog ? catalog : null,
            Login = builder.IntegratedSecurity ? null : builder.UserID,
            Password = builder.IntegratedSecurity ? null : builder.Password,
            Label = label,
            TrustServerCertificate = builder.TrustServerCertificate,
        };
    }
}

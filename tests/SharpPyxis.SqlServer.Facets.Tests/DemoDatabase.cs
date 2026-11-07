using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// The demo database, created on LocalDB once for the whole run from the three scripts the repository
/// ships, in their order, and dropped afterwards. The tests read what a user would read, from the
/// same objects.
/// </summary>
/// <remarks>
/// Under Windows authentication: LocalDB makes its owner sysadmin, so the rights of the login are
/// proved by <c>Verify-FacetsRights.sql</c>, never by these tests.
/// </remarks>
public sealed partial class DemoDatabase : IDisposable
{
    /// <summary>The server the demo database is created on.</summary>
    public const string Server = @"(localdb)\MSSQLLocalDB";

    /// <summary>The name of the demo database.</summary>
    public const string Name = "FacetsTests";

    public DemoDatabase()
    {
        // A previous run stopped halfway leaves the database behind: start from nothing either way.
        ExecuteOnMaster($"""
            if db_id(N'{Name}') is not null
            begin
                alter database [{Name}] set single_user with rollback immediate;
                drop database [{Name}];
            end;
            create database [{Name}] collate French_100_CI_AS;
            """);

        using var connection = Open();
        RunScript(connection, "Create-FacetsDictionary.sql");
        RunScript(connection, "Create-FacetsDictionaryProcedures.sql");
        RunScript(connection, "Create-DemoDatabase.sql");
    }

    /// <summary>The connection string to the demo database.</summary>
    public string ConnectionString { get; } = new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = Name,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
    }.ConnectionString;

    /// <summary>Opens a connection to the demo database.</summary>
    public SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static void RunScript(SqlConnection connection, string fileName)
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));

        foreach (var batch in BatchSeparator().Split(script).Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            using var command = new SqlCommand(batch, connection);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        // Pooled connections would keep the database in use.
        SqlConnection.ClearAllPools();
        ExecuteOnMaster($"alter database [{Name}] set single_user with rollback immediate; drop database [{Name}];");
    }

    private static void ExecuteOnMaster(string sql)
    {
        using var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = Server, InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true,
        }.ConnectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    // GO is not T-SQL: SSMS and sqlcmd split on it, and so does this.
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();
}

/// <summary>Shares one demo database between every test class.</summary>
[CollectionDefinition(Name)]
public sealed class DemoDatabaseCollection : ICollectionFixture<DemoDatabase>
{
    public const string Name = "demo database";
}

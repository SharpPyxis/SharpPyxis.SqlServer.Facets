using Microsoft.Data.SqlClient;
using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// A column enters the dictionary export only, and the designer opens it under their own login. The
/// procedures close and never open: the agent cannot undo what the designer kept closed.
/// </summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class ExportOnlyTests(DemoDatabase database)
{
    [Fact]
    public void The_procedures_refuse_to_open_a_column()
    {
        var refusal = Execute("exec facets_dict.annotate_column N'v_customers', N'email', N'test', @is_export_only = 0;");

        Assert.Equal(50002, refusal.Number);
        Assert.StartsWith("Only the designer opens a column to queries", refusal.Message);
    }

    [Fact]
    public void The_procedures_refuse_to_show_a_hidden_view()
    {
        var refusal = Execute("exec facets_dict.annotate_view N'v_margins', N'test', @is_exposed_to_model = 1;");

        Assert.Equal(50002, refusal.Number);
        Assert.StartsWith("Only the designer shows a hidden view again", refusal.Message);
    }

    [Fact]
    public async Task Describe_view_names_an_export_only_column_and_shows_none_of_its_values()
    {
        var entry = new ConnectionEntry
        {
            Id = 0, Server = DemoDatabase.Server, Database = DemoDatabase.Name, TrustServerCertificate = true,
        };
        var settings = new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, entry);
        var tools = new FacetsTools(settings, new ConnectionStore(UnusedConfigPath));

        var description = await tools.DescribeView("v_customers", TestContext.Current.CancellationToken);

        Assert.Contains("email\tlabel\tE-mail\t\texport only: its values may go to an exported file, never to a result\t\n", description);
        Assert.DoesNotContain("Values of email", description);
    }

    // The refusals come before any write, so nothing is left behind; the transaction makes sure of it.
    private SqlException Execute(string sql)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = new SqlCommand(sql, connection, transaction);
        return Assert.Throws<SqlException>(() => command.ExecuteNonQuery());
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");
}

using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// The cap of a view is set by the designer and replaces the default of the server, above or below;
/// the agent reads it before it queries.
/// </summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class ViewCapTests
{
    [Theory]
    [InlineData("v_customers", 50)]
    [InlineData("v_sales", FacetsSettings.DefaultMaxRows)]
    public async Task Describe_view_gives_the_cap_of_the_view(string view, int cap)
    {
        var entry = new ConnectionEntry
        {
            Id = 0, Server = DemoDatabase.Server, Database = DemoDatabase.Name, TrustServerCertificate = true,
        };
        var settings = new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, entry);
        var tools = new FacetsTools(settings, new ConnectionStore(UnusedConfigPath));

        var description = await tools.DescribeView(view, TestContext.Current.CancellationToken);

        Assert.Contains($"\nRows per query, at most: {cap}\n", description);
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");
}

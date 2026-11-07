using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// A client sends parallel tool calls in one batch, and the SDK runs each on a task of its own: a call
/// may reach the tools before the use_connection sent with it.
/// </summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class SelectionTests
{
    [Fact]
    public async Task A_call_made_before_the_choice_waits_for_it()
    {
        var tools = ServerOnly();
        var cancellationToken = TestContext.Current.CancellationToken;

        var views = tools.ListViews(cancellationToken);
        Assert.False(views.IsCompleted);

        tools.UseConnection(0, DemoDatabase.Name);

        Assert.Contains("v_sales", await views);
    }

    [Fact]
    public async Task A_call_with_no_choice_coming_is_refused()
    {
        var tools = ServerOnly(TimeSpan.FromMilliseconds(100));

        Assert.StartsWith("No connection is selected",
            await tools.ListViews(TestContext.Current.CancellationToken));
    }

    // A connection naming the server only: nothing is chosen until use_connection names a database,
    // and choosing one reads the server first.
    private static FacetsTools ServerOnly(TimeSpan? selectionWait = null)
    {
        var entry = new ConnectionEntry { Id = 0, Server = DemoDatabase.Server, TrustServerCertificate = true };
        var settings = new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, entry);
        return new FacetsTools(settings, new ConnectionStore(UnusedConfigPath), selectionWait);
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");
}

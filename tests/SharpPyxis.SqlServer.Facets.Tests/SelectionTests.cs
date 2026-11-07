using System.Diagnostics;
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

        var views = tools.ListViews(TestContext.Current.CancellationToken);
        Assert.False(views.IsCompleted);

        tools.UseConnection(0, DemoDatabase.Name);

        Assert.Contains("v_sales", await views);
    }

    [Fact]
    public async Task A_call_with_no_choice_coming_is_answered_at_once()
    {
        var tools = ServerOnly();
        var clock = Stopwatch.StartNew();

        var answer = await tools.ListViews(TestContext.Current.CancellationToken);

        Assert.Equal(FacetsTools.NoConnection, answer);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Answered after {clock.Elapsed}.");
    }

    [Fact]
    public async Task A_choice_refused_releases_the_call_waiting_for_it()
    {
        var tools = ServerOnly();

        var views = tools.ListViews(TestContext.Current.CancellationToken);
        Assert.StartsWith("No database named no_such_database",
            tools.UseConnection(0, "no_such_database"));

        Assert.Equal(FacetsTools.NoConnection, await views.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    // A connection naming the server only: nothing is chosen until use_connection names a database,
    // and choosing one reads the server first.
    private static FacetsTools ServerOnly()
    {
        var entry = new ConnectionEntry { Id = 0, Server = DemoDatabase.Server, TrustServerCertificate = true };
        var settings = new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, entry);
        return new FacetsTools(settings, new ConnectionStore(UnusedConfigPath));
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");
}

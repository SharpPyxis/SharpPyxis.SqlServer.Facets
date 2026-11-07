using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// The agent names a working folder under the export root of the connection, one level deep; it follows
/// the connection, and listing shows what an earlier session left there.
/// </summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class ExportFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", $"exports-{Guid.NewGuid():N}");

    [Fact]
    public async Task Lists_the_folders_under_the_root_and_the_one_in_use()
    {
        Directory.CreateDirectory(Path.Combine(_root, DemoDatabase.Name, "Sales 2025"));
        var tools = Tools();
        tools.UseConnection(0, DemoDatabase.Name);

        var listing = await tools.UseExportFolder(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains($"Export root: {Path.Combine(_root, DemoDatabase.Name)}\n", listing);
        Assert.Contains("Folders:\nSales 2025\n", listing);
        Assert.EndsWith("In use: none, files go to the root.", listing);
    }

    [Fact]
    public async Task Chooses_a_folder_one_level_deep_and_says_when_its_name_changed()
    {
        var tools = Tools();
        tools.UseConnection(0, DemoDatabase.Name);

        var answer = await tools.UseExportFolder("../Sales: 2026", TestContext.Current.CancellationToken);

        Assert.Contains($"Exports now go to {Path.Combine(_root, DemoDatabase.Name, "_Sales_ 2026")}", answer);
        Assert.Contains("the name '../Sales: 2026' became '_Sales_ 2026'", answer);
        Assert.False(Directory.Exists(Path.Combine(_root, DemoDatabase.Name, "_Sales_ 2026")));
        Assert.EndsWith("In use: _Sales_ 2026", await tools.UseExportFolder(cancellationToken: TestContext.Current.CancellationToken));

        Assert.StartsWith("Refused: '..' leaves no usable folder name",
            (await tools.UseExportFolder("..", TestContext.Current.CancellationToken)).Split('\n')[^1]);
    }

    [Fact]
    public async Task The_folder_follows_the_connection()
    {
        var tools = Tools();
        tools.UseConnection(0, DemoDatabase.Name);
        await tools.UseExportFolder("Sales", TestContext.Current.CancellationToken);

        tools.UseConnection(0, DemoDatabase.Name);
        Assert.EndsWith("In use: Sales", await tools.UseExportFolder(cancellationToken: TestContext.Current.CancellationToken));

        tools.UseConnection(0, "master");
        Assert.EndsWith("In use: none, files go to the root.", await tools.UseExportFolder(cancellationToken: TestContext.Current.CancellationToken));
    }

    // A connection naming the server only, with a declared root: each database has its folder under it.
    private FacetsTools Tools()
    {
        var entry = new ConnectionEntry { Id = 0, Server = DemoDatabase.Server, TrustServerCertificate = true, ExportRoot = _root };
        var settings = new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, entry);
        return new FacetsTools(settings, new ConnectionStore(UnusedConfigPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");
}

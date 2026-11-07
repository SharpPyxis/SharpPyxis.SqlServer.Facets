using System.Text;
using System.Text.Json;
using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

/// <summary>
/// query_to_file runs the request of query and writes the whole result to a CSV Excel opens as it is,
/// with a sheet beside it. The export only columns go to the file; the model gets the path, the count
/// and the sheet.
/// </summary>
[Collection(DemoDatabaseCollection.Name)]
public sealed class QueryToFileTests(DemoDatabase database) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", $"exports-{Guid.NewGuid():N}");

    private async Task<Catalog> CatalogAsync()
    {
        using var connection = database.Open();
        return await CatalogReader.ReadAsync(connection, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_file_takes_an_export_only_column_and_has_no_cap()
    {
        var query = SqlComposer.ComposeForFile(await CatalogAsync(), new QueryRequest
        {
            View = "v_customers",
            Dimensions = [new("email"), new("city")],
            Filters = [new("email", FilterOperator.NotEmpty, [])],
        });

        Assert.StartsWith("select [email] as [email], [city] as [city]\n", query.Sql);
        Assert.Contains("where ([email] is not null and [email] <> N'')", query.Sql);
        Assert.Null(query.Limit);
        Assert.Null(query.Cap);
    }

    [Fact]
    public async Task A_file_filters_an_export_only_column_by_empty_or_not_empty_only()
    {
        var catalog = await CatalogAsync();
        var refusal = Assert.Throws<QueryRefusedException>(() => SqlComposer.ComposeForFile(catalog, new QueryRequest
        {
            View = "v_customers",
            Dimensions = [new("name")],
            Filters = [new("email", FilterOperator.Equals, ["port@epicerie.example"])],
        }));

        Assert.Equal(["Column 'email' is for export only: a filter on it may only be empty or not empty."], refusal.Faults);
    }

    [Fact]
    public async Task A_file_keeps_the_top_it_was_given()
    {
        var query = SqlComposer.ComposeForFile(await CatalogAsync(), new QueryRequest
        {
            View = "v_customers",
            Dimensions = [new("name")],
            Top = 1000,
        });

        // Above the cap of the view, 50: a file is not bounded by it.
        Assert.StartsWith("select top (1000) ", query.Sql);
        Assert.Equal(1000, query.Limit);
    }

    [Theory]
    [InlineData("Nantes", "Nantes")]
    [InlineData("Livré; porte arrière", "\"Livré; porte arrière\"")]
    [InlineData("Café \"Le Zinc\"", "\"Café \"\"Le Zinc\"\"\"")]
    [InlineData("two\r\nlines", "\"two\r\nlines\"")]
    public void A_cell_is_quoted_only_when_it_has_to(string cell, string expected)
    {
        Assert.Equal(expected, FileExport.Quote(cell));
    }

    [Fact]
    public void A_value_is_written_as_Excel_reads_it()
    {
        Assert.Equal("1", FileExport.Format(true, 1));
        Assert.Equal("0", FileExport.Format(false, 1));
        Assert.Equal("line one\nline two", FileExport.Format("line one\nline two", 1));
        Assert.Equal("2026-01-31", FileExport.Format(new DateTime(2026, 1, 31), 1));
        Assert.Equal("2026-01-31 14:30:00", FileExport.Format(new DateTime(2026, 1, 31, 14, 30, 0), 1));
        Assert.Equal("1250.5", FileExport.Format(125050L, 100, 2));
        Assert.Equal(string.Empty, FileExport.Format(null, 1));
    }

    [Fact]
    public async Task Writes_the_file_and_its_sheet_and_returns_no_row()
    {
        var tools = Tools();
        await tools.UseExportFolder("Customers", TestContext.Current.CancellationToken);

        var answer = await tools.QueryToFileAsync(
            "v_customers", "Customers with an e-mail",
            dimensions: [new() { Column = "name" }, new() { Column = "email" }, new() { Column = "is_active" }],
            measures: [new() { Column = "paid_cents", Aggregation = "sum" }],
            filters: [new() { Column = "email", Operator = "not empty" }],
            orderBy: new() { By = "dimension", Index = 0 }, top: null,
            client: "tests", onRows: null, TestContext.Current.CancellationToken);

        var folder = Path.Combine(_root, "Customers");
        var csv = Assert.Single(Directory.GetFiles(folder, "*.csv"));
        Assert.Matches(@"\\\d{4}-\d{2}-\d{2}_\d{2}-\d{2}_Customers with an e-mail\.csv$", csv);
        Assert.Contains($"Written: {csv}\n5 rows.", answer);
        Assert.DoesNotContain("port@epicerie.example", answer);

        var bytes = await File.ReadAllBytesAsync(csv, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("name;email;is_active;sum_paid_cents", lines[0]);
        Assert.StartsWith("Atelier Verdon;contact@verdon.example;1;", lines[1]);
        Assert.Contains("Épicerie du Port;port@epicerie.example;1;2040", lines);
        Assert.Equal(7, lines.Length); // header, 5 rows, and the empty string after the last CRLF

        var sheet = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csv, ".json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(5, sheet.GetProperty("rows").GetInt64());
        Assert.Equal("tests", sheet.GetProperty("client").GetString());
        Assert.Equal("v_customers", sheet.GetProperty("view").GetString());
        Assert.Equal("E-mail", sheet.GetProperty("columns")[1].GetProperty("label").GetString());
        Assert.Equal("EUR", sheet.GetProperty("columns")[3].GetProperty("unit").GetString());
        Assert.Equal("not empty", sheet.GetProperty("filters")[0].GetProperty("operator").GetString());
        Assert.False(File.Exists(csv + ".partial"));
    }

    [Fact]
    public async Task Writes_nothing_when_no_row_matches_and_refuses_an_empty_label()
    {
        var tools = Tools();

        var none = await tools.QueryToFileAsync(
            "v_customers", "Nobody", [new() { Column = "name" }], null,
            [new() { Column = "city", Operator = "equals", Values = ["Nowhere"] }], null, null,
            "tests", null, TestContext.Current.CancellationToken);
        Assert.EndsWith("No row matches the request: no file was written.", none);
        Assert.Empty(Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : []);

        var refused = await tools.QueryToFileAsync(
            "v_customers", "..", [new() { Column = "name" }], null, null, null, null,
            "tests", null, TestContext.Current.CancellationToken);
        Assert.EndsWith("Refused: The label '..' leaves no usable file name: give a few words.", refused);
    }

    [Fact]
    public void A_name_already_taken_gets_a_number_and_the_pair_stays_whole()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "2026-11-07_15-02_Sales.csv"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "2026-11-07_15-02_Sales-2.json"), string.Empty);

        Assert.Equal(Path.Combine(_root, "2026-11-07_15-02_Sales-3.csv"), FacetsTools.FreePath(_root, "2026-11-07_15-02_Sales"));
    }

    // A connection naming the database, with a declared root: files go straight under it.
    private FacetsTools Tools()
    {
        var entry = new ConnectionEntry
        {
            Id = 0, Server = DemoDatabase.Server, Database = DemoDatabase.Name, TrustServerCertificate = true, ExportRoot = _root,
        };
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

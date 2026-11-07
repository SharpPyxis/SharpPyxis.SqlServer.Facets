namespace SharpPyxis.SqlServer.Facets.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class CatalogTests(DemoDatabase database)
{
    private async Task<Catalog> ReadAsync()
    {
        using var connection = database.Open();
        return await CatalogReader.ReadAsync(connection, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reads_the_exposed_views_and_hides_the_rest()
    {
        var catalog = await ReadAsync();

        Assert.Equal(["v_customers", "v_sales"], catalog.Views.Select(view => view.Name));
        Assert.Null(catalog.FindView("v_margins"));
    }

    [Fact]
    public async Task Reads_every_nature_of_v_sales()
    {
        var sales = (await ReadAsync()).FindView("v_sales")!;

        Assert.Equal("Sales, one row per order line", sales.Label);
        Assert.Equal(15, sales.Columns.Count);
        Assert.Equal(Enum.GetValues<Nature>().OrderBy(nature => nature),
                     sales.Columns.Select(column => column.Nature!.Value).Distinct().OrderBy(nature => nature));
    }

    [Fact]
    public async Task Reads_unit_divisor_and_values()
    {
        var sales = (await ReadAsync()).FindView("v_sales")!;

        var amount = sales.FindColumn("line_amount_cents")!;
        Assert.Equal(Nature.Amount, amount.Nature);
        Assert.Equal("EUR", amount.Unit);
        Assert.Equal(100m, amount.Divisor);

        var status = sales.FindColumn("status")!;
        Assert.Equal(["C", "O", "P"], status.Values.Select(value => value.Value));
        Assert.Equal("Paid", status.Values.Single(value => value.Value == "P").Label);
    }

    [Fact]
    public async Task Shows_an_export_only_column_and_one_without_nature()
    {
        var customers = (await ReadAsync()).FindView("v_customers")!;

        var email = customers.FindColumn("email")!;
        Assert.True(email.IsExportOnly);
        Assert.Empty(email.Values);
        Assert.False(customers.FindColumn("city")!.IsExportOnly);

        var createdOn = customers.FindColumn("created_on")!;
        Assert.Null(createdOn.Nature);
        Assert.Equal("date", createdOn.SqlType);
    }

    [Fact]
    public async Task Reads_the_note_and_the_questions()
    {
        var catalog = await ReadAsync();

        Assert.StartsWith("A small furniture and lighting shop.", catalog.DomainNote);

        var question = Assert.Single(catalog.Questions);
        Assert.Equal("v_sales", question.ViewName);
        Assert.Equal("line_amount_cents", question.ColumnName);
        Assert.NotNull(question.AnsweredAt);
        Assert.StartsWith("No: it is quantity times unit price.", question.Answer);
    }

    [Fact]
    public async Task Reads_the_cap_the_designer_set_on_a_view()
    {
        var catalog = await ReadAsync();

        Assert.Equal(50, catalog.FindView("v_customers")!.MaxRows);
        Assert.Null(catalog.FindView("v_sales")!.MaxRows);
        Assert.Equal(200, catalog.FindView("v_sales")!.Cap(200));
    }
}

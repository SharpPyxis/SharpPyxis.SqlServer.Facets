namespace SharpPyxis.SqlServer.Facets.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class SqlComposerTests(DemoDatabase database)
{
    private async Task<Catalog> CatalogAsync()
    {
        using var connection = database.Open();
        return await CatalogReader.ReadAsync(connection, TestContext.Current.CancellationToken);
    }

    private static QueryRefusedException Refusal(Catalog catalog, QueryRequest request) =>
        Assert.Throws<QueryRefusedException>(() => SqlComposer.Compose(catalog, request, 200));

    [Fact]
    public async Task Composes_a_grouped_sum_with_a_filter()
    {
        var query = SqlComposer.Compose(await CatalogAsync(), new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("status")],
            Measures = [new("line_amount_cents", Aggregation.Sum)],
            Filters = [new("is_paid", FilterOperator.Equals, ["true"])],
        }, 200);

        Assert.Equal("""
            select top (201) [status] as [status], sum([line_amount_cents]) as [sum_line_amount_cents]
            from facets.[v_sales]
            where [is_paid] = @p0
            group by [status]
            order by [sum_line_amount_cents] desc;
            """, query.Sql);
        var parameter = Assert.Single(query.Parameters);
        Assert.Equal(System.Data.SqlDbType.Bit, parameter.Type);
        Assert.Equal(true, parameter.Value);
        Assert.Equal([1m, 100m], query.Columns.Select(column => column.Divisor));
    }

    [Fact]
    public async Task Groups_a_date_at_a_level_and_caps_the_top()
    {
        var query = SqlComposer.Compose(await CatalogAsync(), new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("ordered_on", DateLevel.Month)],
            Measures = [new("sale_id", Aggregation.Count)],
            OrderBy = new(OrderTarget.Dimension, 0),
            Top = 5000,
        }, 200);

        Assert.Contains("concat(datepart(year, [ordered_on]), N'-', right(concat(N'0', datepart(month, [ordered_on])), 2)) as [ordered_on_month]", query.Sql);
        Assert.StartsWith("select top (201)", query.Sql);
        Assert.EndsWith("order by [ordered_on_month];", query.Sql);
        Assert.Equal(200, query.Limit);
    }

    [Fact]
    public async Task Refuses_what_the_catalog_does_not_have()
    {
        var catalog = await CatalogAsync();

        Assert.Contains("no view 'v_margins'", Refusal(catalog, new QueryRequest { View = "v_margins" }).Message);

        var faults = Refusal(catalog, new QueryRequest
        {
            View = "v_customers",
            Dimensions = [new("email"), new("created_on")],
            Measures = [new("paid_cents", Aggregation.Sum)],
        }).Faults;
        Assert.Equal(2, faults.Count);
        Assert.Contains("has no column 'email'", faults[0]);
        Assert.Contains("'created_on' has no nature yet", faults[1]);
    }

    [Fact]
    public async Task Refuses_what_a_nature_does_not_allow()
    {
        var faults = Refusal(await CatalogAsync(), new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("line_amount_cents"), new("status", DateLevel.Year)],
            Measures = [new("discount_rate", Aggregation.Sum)],
            Filters = [new("note", FilterOperator.Equals, ["x"]), new("status", FilterOperator.In, [])],
        }).Faults;

        Assert.Equal(5, faults.Count);
        Assert.Contains("'line_amount_cents' is an amount and cannot group rows", faults[0]);
        Assert.Contains("a level applies to a date only", faults[1]);
        Assert.Contains("sum is not allowed on 'discount_rate', which is a rate: allowed avg, min, max, count", faults[2]);
        Assert.Contains("equals is not allowed on 'note', which is free text: allowed contains, empty", faults[3]);
        Assert.Contains("in on 'status' takes one or more values, not 0", faults[4]);
    }

    [Fact]
    public async Task Refuses_a_value_that_does_not_convert_and_an_order_out_of_range()
    {
        var faults = Refusal(await CatalogAsync(), new QueryRequest
        {
            View = "v_sales",
            Measures = [new("quantity", Aggregation.Sum)],
            Filters = [new("ordered_on", FilterOperator.Between, ["2026-01-01", "soon"])],
            OrderBy = new(OrderTarget.Measure, 1),
        }).Faults;

        Assert.Equal(2, faults.Count);
        Assert.Contains("'soon' is not a valid date for 'ordered_on'", faults[0]);
        Assert.Contains("The order names measure 1, and the request has 1", faults[1]);
    }

    [Fact]
    public async Task Escapes_a_contains_pattern_and_types_the_parameters()
    {
        var query = SqlComposer.Compose(await CatalogAsync(), new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("product_name")],
            Filters =
            [
                new("product_name", FilterOperator.Contains, ["50%_[a]"]),
                new("product_sku", FilterOperator.In, ["CHAIR-OAK", "LAMP-BRASS"]),
                new("unit_price_cents", FilterOperator.GreaterOrEqual, ["10000"]),
                new("note", FilterOperator.Empty, []),
            ],
        }, 200);

        Assert.Contains(@"[product_name] like @p0 escape N'\'", query.Sql);
        Assert.Contains("[product_sku] in (@p1, @p2)", query.Sql);
        Assert.Contains("[unit_price_cents] >= @p3", query.Sql);
        Assert.Contains("([note] is null or [note] = N'')", query.Sql);
        Assert.Equal(@"%50\%\_\[a]%", query.Parameters[0].Value);
        Assert.Equal(System.Data.SqlDbType.VarChar, query.Parameters[1].Type);
        Assert.Equal(10000L, query.Parameters[3].Value);
    }
}

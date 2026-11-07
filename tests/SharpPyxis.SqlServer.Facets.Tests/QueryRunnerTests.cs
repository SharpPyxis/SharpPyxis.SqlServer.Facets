namespace SharpPyxis.SqlServer.Facets.Tests;

[Collection(DemoDatabaseCollection.Name)]
public sealed class QueryRunnerTests(DemoDatabase database)
{
    private async Task<QueryResult> RunAsync(QueryRequest request, int maxRows = 200)
    {
        using var connection = database.Open();
        var catalog = await CatalogReader.ReadAsync(connection, TestContext.Current.CancellationToken);
        var query = SqlComposer.Compose(catalog, request, maxRows);
        return await QueryRunner.RunAsync(connection, query, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Sums_paid_sales_by_status_in_euros()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("status")],
            Measures = [new("line_amount_cents", Aggregation.Sum), new("sale_id", Aggregation.Count)],
            Filters = [new("is_paid", FilterOperator.Equals, ["true"])],
        });

        // The demo holds 706 500 cents of paid lines, on 11 lines; the divisor turns them into euros.
        Assert.Equal("""
            status	sum_line_amount_cents	count_sale_id
            P	7065	11

            """, result.ToTsv());
    }

    [Fact]
    public async Task Gives_an_aggregated_amount_to_the_cent()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Measures = [new("line_amount_cents", Aggregation.Avg), new("line_amount_cents", Aggregation.Count)],
            Filters = [new("is_paid", FilterOperator.Equals, ["true"])],
        });

        // 706 500 cents over 11 paid lines: 642.2727… euros, given as 642.27; the count stays a count.
        Assert.Equal(["642.27", "11"], result.Rows[0]);

        // Half a cent rounds away from zero, as T-SQL does, where Math.Round alone would give 2.34.
        Assert.Equal("2.35", QueryRunner.Format(2.345m, 1, 2));
        Assert.Equal("-2.35", QueryRunner.Format(-2.345m, 1, 2));
        Assert.Equal("120.5", QueryRunner.Format(12050, 100, 2));
        Assert.Equal("0.333333", QueryRunner.Format(0.333333m, 1));
    }

    [Fact]
    public async Task Filters_an_amount_in_euros_on_a_column_of_cents()
    {
        var all = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Measures = [new("line_amount_cents", Aggregation.Count), new("line_amount_cents", Aggregation.Min)],
        });
        var cheapest = all.Rows[0][1];

        // The cheapest line, given in euros, is found again by a filter written in euros.
        var filtered = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Measures = [new("line_amount_cents", Aggregation.Count)],
            Filters = [new("line_amount_cents", FilterOperator.LessOrEqual, [cheapest])],
        });
        var above = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Measures = [new("line_amount_cents", Aggregation.Count)],
            Filters = [new("line_amount_cents", FilterOperator.Greater, [cheapest])],
        });

        Assert.NotEqual("0", filtered.Rows[0][0]);
        Assert.Equal(int.Parse(all.Rows[0][0]), int.Parse(filtered.Rows[0][0]) + int.Parse(above.Rows[0][0]));
    }

    [Theory]
    // Read from the calendar table of a real database, which follows ISO 8601 to the day.
    [InlineData("2020-12-31", "2020-W53")]
    [InlineData("2021-01-03", "2020-W53")]
    [InlineData("2021-01-04", "2021-W01")]
    [InlineData("2025-12-28", "2025-W52")]
    [InlineData("2025-12-29", "2026-W01")]
    [InlineData("2026-01-04", "2026-W01")]
    [InlineData("2026-01-05", "2026-W02")]
    [InlineData("2026-12-31", "2026-W53")]
    [InlineData("2027-01-03", "2026-W53")]
    [InlineData("2027-01-04", "2027-W01")]
    public async Task Gives_a_week_its_ISO_year(string day, string week)
    {
        using var connection = database.Open();
        var catalog = await CatalogReader.ReadAsync(connection, TestContext.Current.CancellationToken);
        var column = catalog.FindView("v_sales")!.FindColumn("ordered_on")!;

        // The demo holds no turn of the year: the expression the composer writes runs on the day alone.
        using var command = new Microsoft.Data.SqlClient.SqlCommand(
            $"select {SqlComposer.DimensionExpression(column, DateLevel.Week)} from (values (cast(@day as date))) as d([ordered_on]);",
            connection);
        command.Parameters.AddWithValue("@day", day);

        Assert.Equal(week, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Groups_by_month_and_averages_a_rate()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("ordered_on", DateLevel.Month)],
            Measures = [new("discount_rate", Aggregation.Avg), new("quantity", Aggregation.Sum)],
            Filters = [new("ordered_on", FilterOperator.Between, ["2026-01-01", "2026-02-28"])],
            OrderBy = new(OrderTarget.Dimension, 0),
        });

        Assert.Equal(["2026-01", "2026-02"], result.Rows.Select(row => row[0]));
        // January: 0 % on order 1001 (2 lines) and 5 % on 1002 (2 lines).
        Assert.Equal("0.025", result.Rows[0][1]);
        Assert.Equal("14", result.Rows[0][2]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Cuts_at_the_limit_and_says_so()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("sale_id")],
            OrderBy = new(OrderTarget.Dimension, 0),
        }, maxRows: 3);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal(["100101", "100102", "100201"], result.Rows.Select(row => row[0]));
        Assert.EndsWith("(3 rows shown, the most this view returns, and there are more: narrow the request, or order it "
                        + "so the rows that matter come first. If the answer needs them all, say so with ask_question: the owner "
                        + "of the dictionary sets the cap of each view.)\n", result.ToTsv());
    }

    [Fact]
    public async Task Counts_distinct_customers_per_city_and_searches_a_label()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("customer_city")],
            Measures = [new("customer_name", Aggregation.CountDistinct)],
            Filters = [new("product_name", FilterOperator.Contains, ["oak"])],
            OrderBy = new(OrderTarget.Measure, 0, Descending: true),
        });

        Assert.Equal("Nantes", result.Rows[0][0]);
        Assert.Equal("2", result.Rows[0][1]);
    }

    [Fact]
    public async Task Formats_dates_booleans_and_empty_cells()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_sales",
            Dimensions = [new("ordered_on"), new("is_paid"), new("customer_city")],
            Filters = [new("order_id", FilterOperator.In, ["1001", "1004"])],
            OrderBy = new(OrderTarget.Dimension, 0),
        });

        Assert.Equal(["2026-01-08", "true", "Nantes"], result.Rows[0]);
        Assert.Equal(["2026-02-10", "false", "Nantes"], result.Rows[1]);

        Assert.Equal("", QueryRunner.Format(null, 1));
        Assert.Equal("120.5", QueryRunner.Format(12050, 100));
        Assert.Equal("3", QueryRunner.Format(3.000000m, 1));
        Assert.Equal("2026-01-08 10:30:00", QueryRunner.Format(new DateTime(2026, 1, 8, 10, 30, 0), 1));
        Assert.Equal("a b", QueryRunner.Format("a\tb", 1));
    }

    [Fact]
    public async Task Top_cannot_raise_the_cap_but_can_lower_it()
    {
        var result = await RunAsync(new QueryRequest
        {
            View = "v_customers",
            Dimensions = [new("name")],
            Measures = [new("paid_cents", Aggregation.Sum)],
            Top = 2,
        });

        Assert.Equal(2, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal("Épicerie du Port", result.Rows[0][0]);
        Assert.Equal("2040", result.Rows[0][1]);
        // Cut by the request, not by the view: the agent may ask for more, up to the cap of the view.
        Assert.EndsWith("(2 rows shown, as top asked, and there are more: this view returns up to 50.)\n", result.ToTsv());
    }
}

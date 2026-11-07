using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

public sealed class FacetsToolsTests
{
    [Fact]
    public void Words_of_a_request_are_those_describe_view_uses()
    {
        var faults = new List<string>();

        Assert.Equal(Aggregation.CountDistinct, FacetsTools.Parse(FacetsTools.Aggregations, "count distinct", "aggregation", faults));
        Assert.Equal(FilterOperator.GreaterOrEqual, FacetsTools.Parse(FacetsTools.Operators, "Greater Or Equal", "operator", faults));
        Assert.Equal(DateLevel.Month, FacetsTools.Parse(FacetsTools.Levels, "month", "level", faults));
        Assert.Null(FacetsTools.Parse(FacetsTools.Levels, null, "level", faults));
        Assert.Empty(faults);
    }

    [Fact]
    public void An_unknown_word_is_a_fault_naming_the_known_ones()
    {
        var faults = new List<string>();

        Assert.Null(FacetsTools.Parse(FacetsTools.Aggregations, "total", "aggregation", faults));

        Assert.Equal(["'total' is not a known aggregation: use sum, avg, min, max, count, count distinct."], faults);
    }

    [Fact]
    public void A_refusal_of_the_database_is_said_in_words()
    {
        Assert.Equal("Refused: a unit applies to an amount, a quantity or a rate only.",
            FacetsTools.Explain(547, "The UPDATE statement conflicted with the CHECK constraint \"ck_columns_unit_on_measure\". The conflict occurred in database \"demo\", table \"facets_dict.columns\"."));

        Assert.Equal("Refused: a sensitive column cannot be exposed to the model: hide it, or make it not sensitive.",
            FacetsTools.Explain(547, "... CHECK constraint \"ck_columns_sensitive_not_exposed\". ..."));

        Assert.StartsWith("Refused: 'that' is not a nature: use amount, quantity,",
            FacetsTools.Explain(547, "... FOREIGN KEY constraint \"fk_columns_nature\". ..."));

        Assert.Equal("Refused: The column is not in the dictionary: run refresh_catalog, or check the names of the view and the column.",
            FacetsTools.Explain(50001, "The column is not in the dictionary: run refresh_catalog, or check the names of the view and the column."));

        Assert.Equal("Failed: Timeout expired.", FacetsTools.Explain(-2, "Timeout expired."));
    }
}

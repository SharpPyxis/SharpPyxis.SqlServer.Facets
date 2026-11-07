namespace SharpPyxis.SqlServer.Facets.Tests;

public sealed class NatureRulesTests
{
    [Fact]
    public void A_rate_is_never_summed()
    {
        Assert.DoesNotContain(Aggregation.Sum, NatureRules.Aggregations(Nature.Rate));
        Assert.Contains(Aggregation.Avg, NatureRules.Aggregations(Nature.Rate));
    }

    [Fact]
    public void Count_is_accepted_on_every_nature()
    {
        foreach (var nature in Enum.GetValues<Nature>())
            Assert.Contains(Aggregation.Count, NatureRules.Aggregations(nature));
    }

    [Fact]
    public void Text_is_searched_and_never_grouped()
    {
        Assert.Equal([FilterOperator.Contains, FilterOperator.Empty, FilterOperator.NotEmpty], NatureRules.Operators(Nature.Text));
        Assert.False(NatureRules.Groups(Nature.Text));
        Assert.DoesNotContain(Aggregation.CountDistinct, NatureRules.Aggregations(Nature.Text));
    }

    [Fact]
    public void Not_empty_is_allowed_wherever_empty_is()
    {
        foreach (var nature in Enum.GetValues<Nature>())
        {
            var operators = NatureRules.Operators(nature);
            Assert.Equal(operators.Contains(FilterOperator.Empty), operators.Contains(FilterOperator.NotEmpty));
        }
    }

    [Fact]
    public void Measures_do_not_group_and_dimensions_do()
    {
        foreach (var nature in Enum.GetValues<Nature>())
            Assert.Equal(!NatureRules.IsMeasure(nature) && nature != Nature.Text, NatureRules.Groups(nature));
    }

    [Fact]
    public void Codes_round_trip_with_the_dictionary()
    {
        foreach (var nature in Enum.GetValues<Nature>())
            Assert.Equal(nature, NatureRules.Parse(NatureRules.Code(nature)));

        Assert.Null(NatureRules.Parse(null));
        Assert.Null(NatureRules.Parse("period"));
    }
}

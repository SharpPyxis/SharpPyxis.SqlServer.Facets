using System.Text.Json;
using SharpPyxis.SqlServer.Facets.Mcp;

namespace SharpPyxis.SqlServer.Facets.Tests;

public sealed class ArgumentCheckTests
{
    [Fact]
    public void A_call_without_arguments_is_told_what_the_tool_requires()
    {
        var result = ArgumentCheck.Check("describe_view", Schema("describe_view"), Arguments("{}"));

        Assert.Equal("describe_view requires view, which the call did not provide. Nothing was run. "
                     + "Arguments received: none. Parameters of describe_view: view (string, required). "
                     + "Call it again with these names.",
                     result);
    }

    [Fact]
    public void A_renamed_argument_is_named_back()
    {
        var result = ArgumentCheck.Check("annotate_column", Schema("annotate_column"),
            Arguments("""{"view": "v_sales", "column": "status", "nature_code": "code"}"""));

        Assert.StartsWith("annotate_column has no parameter nature_code. Nothing was run.", result);
        Assert.Contains("nature (string, optional)", result);
        Assert.Contains("divisor (number, optional)", result);
    }

    [Fact]
    public void A_required_argument_sent_as_null_is_missing()
    {
        var result = ArgumentCheck.Check("query", Schema("query"), Arguments("""{"view": null}"""));

        Assert.StartsWith("query requires view, which the call did not provide.", result);
    }

    [Fact]
    public void A_complete_query_goes_through_and_its_arrays_are_objects()
    {
        var schema = Schema("query");

        Assert.Null(ArgumentCheck.Check("query", schema, Arguments(
            """{"view": "v_sales", "dimensions": [{"column": "status"}], "measures": [{"column": "quantity", "aggregation": "sum"}], "top": 5}""")));

        var dimensions = schema.GetProperty("properties").GetProperty("dimensions");
        Assert.Contains("array", dimensions.GetProperty("type").ToString());
    }

    [Fact]
    public void A_tool_without_parameters_accepts_an_empty_call()
    {
        Assert.Null(ArgumentCheck.Check("list_views", Schema("list_views"), Arguments("{}")));
    }

    [Fact]
    public void The_server_injected_parameters_are_not_in_the_schema()
    {
        var properties = Schema("refresh_catalog").TryGetProperty("properties", out var found) ? found.EnumerateObject().Count() : 0;
        Assert.Equal(0, properties);
    }

    private static readonly string UnusedConfigPath =
        Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", "connections.dat");

    private static JsonElement Schema(string toolName) =>
        new FacetsTools(new FacetsSettings(FacetsSettings.DefaultMaxRows, UnusedConfigPath, null), new ConnectionStore(UnusedConfigPath))
            .CreateTools().Single(tool => tool.ProtocolTool.Name == toolName).ProtocolTool.InputSchema;

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}

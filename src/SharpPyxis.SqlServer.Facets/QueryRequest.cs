namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// What the agent asks: one view, the columns that group its rows, the measures folded over each
/// group, the filters, the order and how many rows. Every name is a name of the catalog; the
/// composer refuses anything else. Values travel as text and are converted to the type of their
/// column by the composer, which is what lets a client send them as JSON strings.
/// </summary>
public sealed record QueryRequest
{
    /// <summary>The view of the schema <c>facets</c>, by its name in the catalog.</summary>
    public required string View { get; init; }

    /// <summary>The columns that group the rows, in the order of the result. May be empty.</summary>
    public IReadOnlyList<DimensionRequest> Dimensions { get; init; } = [];

    /// <summary>The measures, in the order of the result. May be empty when dimensions are asked alone.</summary>
    public IReadOnlyList<MeasureRequest> Measures { get; init; } = [];

    /// <summary>The filters, all of which a row must satisfy.</summary>
    public IReadOnlyList<FilterRequest> Filters { get; init; } = [];

    /// <summary>The order of the rows. By default, the first measure descending, or the first dimension ascending.</summary>
    public OrderRequest? OrderBy { get; init; }

    /// <summary>How many rows at most. Capped by the server, never raised by the request.</summary>
    public int? Top { get; init; }
}

/// <summary>A column that groups rows; a date groups at a level, which defaults to the day.</summary>
public sealed record DimensionRequest(string Column, DateLevel? Level = null);

/// <summary>A column folded over each group by an aggregation.</summary>
public sealed record MeasureRequest(string Column, Aggregation Aggregation);

/// <summary>
/// A condition on a column. The operator decides how many values it takes: one for a comparison,
/// two for <see cref="FilterOperator.Between"/>, one or more for <see cref="FilterOperator.In"/>,
/// none for <see cref="FilterOperator.Empty"/> and <see cref="FilterOperator.NotEmpty"/>.
/// </summary>
public sealed record FilterRequest(string Column, FilterOperator Operator, IReadOnlyList<string> Values);

/// <summary>Which result column orders the rows: a dimension or a measure, by its index in the request.</summary>
public sealed record OrderRequest(OrderTarget Target, int Index, bool Descending = false);

/// <summary>What an <see cref="OrderRequest"/> points at.</summary>
public enum OrderTarget
{
    /// <summary>A dimension, by its index in the request.</summary>
    Dimension,

    /// <summary>A measure, by its index in the request.</summary>
    Measure,
}

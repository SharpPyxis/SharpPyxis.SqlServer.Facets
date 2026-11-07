namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// What a column is, inside its type. The nature decides what the composer accepts on the column:
/// which aggregations, which filter operators, whether it groups rows. The codes are those of the
/// table <c>facets_dict.nature</c>; the rules are here, and only here.
/// </summary>
public enum Nature
{
    /// <summary>Money. Summed and averaged; unit and divisor apply.</summary>
    Amount,

    /// <summary>A count, or a measured quantity. Summed and averaged; unit and divisor apply.</summary>
    Quantity,

    /// <summary>
    /// A ratio, a percentage, or a unit price: an amount per piece keeps its precision, where an
    /// amount is given to the cent. Averaged, never summed.
    /// </summary>
    Rate,

    /// <summary>A day or an instant. Groups rows by year, quarter, month, week or day.</summary>
    Date,

    /// <summary>A value from a closed list. Groups and filters rows as it is.</summary>
    Code,

    /// <summary>A short name that identifies something. Groups and filters rows, and can be searched.</summary>
    Label,

    /// <summary>The key of a row. Counted and filtered by equality, never summed.</summary>
    Identifier,

    /// <summary>Yes or no.</summary>
    Boolean,

    /// <summary>Free prose. Searched, never grouped.</summary>
    Text,
}

/// <summary>How a measure is folded over the rows of a group.</summary>
public enum Aggregation
{
    /// <summary>The total of the column over the group.</summary>
    Sum,

    /// <summary>The mean of the column over the group.</summary>
    Avg,

    /// <summary>The smallest value of the column in the group.</summary>
    Min,

    /// <summary>The largest value of the column in the group.</summary>
    Max,

    /// <summary>The rows where the column is not null. Accepted on every nature.</summary>
    Count,

    /// <summary>The distinct values of the column.</summary>
    CountDistinct,
}

/// <summary>What a filter compares a column with.</summary>
public enum FilterOperator
{
    /// <summary>The column equals the one value.</summary>
    Equals,

    /// <summary>The column equals one of the values.</summary>
    In,

    /// <summary>The column lies between the two values, both included.</summary>
    Between,

    /// <summary>The column is below the value.</summary>
    Less,

    /// <summary>The column is below or equal to the value.</summary>
    LessOrEqual,

    /// <summary>The column is above the value.</summary>
    Greater,

    /// <summary>The column is above or equal to the value.</summary>
    GreaterOrEqual,

    /// <summary>The column contains the text, anywhere.</summary>
    Contains,

    /// <summary>The column is null, or an empty string.</summary>
    Empty,

    /// <summary>
    /// The column holds a value: neither null nor an empty string. The one negative operator, for "the
    /// customers who have an e-mail"; a general negation waits for a real case.
    /// </summary>
    NotEmpty,
}

/// <summary>The level a date groups rows at.</summary>
public enum DateLevel
{
    /// <summary>One row per year.</summary>
    Year,

    /// <summary>One row per quarter of a year.</summary>
    Quarter,

    /// <summary>One row per month of a year.</summary>
    Month,

    /// <summary>One row per ISO week of a year.</summary>
    Week,

    /// <summary>One row per day.</summary>
    Day,
}

/// <summary>
/// The matrix of what each nature allows. It lives in code rather than in the dictionary because
/// the composer must know how to write each aggregation and each operator anyway: a row naming one
/// it cannot write would be an error at run time, not a configuration.
/// </summary>
public static class NatureRules
{
    private static readonly Aggregation[] Measures = [Aggregation.Sum, Aggregation.Avg, Aggregation.Min, Aggregation.Max, Aggregation.Count];
    private static readonly Aggregation[] Rates = [Aggregation.Avg, Aggregation.Min, Aggregation.Max, Aggregation.Count];
    private static readonly Aggregation[] Dates = [Aggregation.Min, Aggregation.Max, Aggregation.Count];
    private static readonly Aggregation[] Dimensions = [Aggregation.Count, Aggregation.CountDistinct];
    private static readonly Aggregation[] Prose = [Aggregation.Count];

    private static readonly FilterOperator[] Ordered =
    [
        FilterOperator.Equals, FilterOperator.Between, FilterOperator.Less, FilterOperator.LessOrEqual,
        FilterOperator.Greater, FilterOperator.GreaterOrEqual, FilterOperator.Empty, FilterOperator.NotEmpty,
    ];
    private static readonly FilterOperator[] Listed = [FilterOperator.Equals, FilterOperator.In, FilterOperator.Empty, FilterOperator.NotEmpty];
    private static readonly FilterOperator[] Searched =
        [FilterOperator.Equals, FilterOperator.In, FilterOperator.Contains, FilterOperator.Empty, FilterOperator.NotEmpty];
    private static readonly FilterOperator[] Keys = [FilterOperator.Equals, FilterOperator.In];
    private static readonly FilterOperator[] Flags = [FilterOperator.Equals];
    private static readonly FilterOperator[] Free = [FilterOperator.Contains, FilterOperator.Empty, FilterOperator.NotEmpty];

    /// <summary>The aggregations the composer accepts on a column of this nature.</summary>
    public static IReadOnlyList<Aggregation> Aggregations(Nature nature) => nature switch
    {
        Nature.Amount or Nature.Quantity => Measures,
        Nature.Rate => Rates,
        Nature.Date => Dates,
        Nature.Code or Nature.Label or Nature.Identifier or Nature.Boolean => Dimensions,
        Nature.Text => Prose,
        _ => throw new ArgumentOutOfRangeException(nameof(nature), nature, null),
    };

    /// <summary>The filter operators the composer accepts on a column of this nature.</summary>
    public static IReadOnlyList<FilterOperator> Operators(Nature nature) => nature switch
    {
        Nature.Amount or Nature.Quantity or Nature.Rate or Nature.Date => Ordered,
        Nature.Code => Listed,
        Nature.Label => Searched,
        Nature.Identifier => Keys,
        Nature.Boolean => Flags,
        Nature.Text => Free,
        _ => throw new ArgumentOutOfRangeException(nameof(nature), nature, null),
    };

    /// <summary>Whether a column of this nature may group rows: a dimension, or a date at a level.</summary>
    public static bool Groups(Nature nature) => nature is Nature.Date or Nature.Code or Nature.Label or Nature.Identifier or Nature.Boolean;

    /// <summary>Whether unit and divisor apply: the three measures.</summary>
    public static bool IsMeasure(Nature nature) => nature is Nature.Amount or Nature.Quantity or Nature.Rate;

    /// <summary>
    /// The decimals a value of this nature is rounded to once aggregated and divided, or null when it
    /// keeps its own. An amount is money, given to the cent; a quantity or a rate is not.
    /// </summary>
    public static int? Decimals(Nature nature, Aggregation aggregation) =>
        nature == Nature.Amount && aggregation is not (Aggregation.Count or Aggregation.CountDistinct) ? 2 : null;

    /// <summary>The code of the nature as the dictionary stores it.</summary>
    public static string Code(Nature nature) => nature.ToString().ToLowerInvariant();

    /// <summary>The nature behind a code of the dictionary, or null for a code this version does not know.</summary>
    public static Nature? Parse(string? code) =>
        code is null ? null : Enum.TryParse<Nature>(code, ignoreCase: true, out var nature) ? nature : null;
}

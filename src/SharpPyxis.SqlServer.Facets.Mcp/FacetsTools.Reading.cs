using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.Facets.Mcp;

// The reading tools: what the dictionary says, and the aggregated query. The catalog is read again at
// every call, one round trip, so that an annotation made between two calls is seen.
internal sealed partial class FacetsTools
{
    private const string NoConnection = "No connection is selected: call use_connection first.";

    /// <summary>The names of the levels, aggregations and operators as the tools take them.</summary>
    internal static readonly Dictionary<string, DateLevel> Levels =
        Enum.GetValues<DateLevel>().ToDictionary(level => level.ToString().ToLowerInvariant(), level => level, StringComparer.OrdinalIgnoreCase);

    internal static readonly Dictionary<string, Aggregation> Aggregations =
        Enum.GetValues<Aggregation>().ToDictionary(SqlComposer.Word, aggregation => aggregation, StringComparer.OrdinalIgnoreCase);

    internal static readonly Dictionary<string, FilterOperator> Operators =
        Enum.GetValues<FilterOperator>().ToDictionary(SqlComposer.Word, filterOperator => filterOperator, StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists the views the agent may query.</summary>
    [McpServerTool(Name = "list_views", ReadOnly = true, Idempotent = true)]
    [Description("Lists the views of the SQL Server database an agent may query, with what each one is and the "
               + "questions it answers. Call it first, before choosing a view for a query; call describe_view "
               + "for the columns of one.")]
    public async Task<string> ListViews(CancellationToken cancellationToken)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var catalog = await ReadCatalogAsync(active, cancellationToken);
        if (catalog.Views.Count == 0)
            return active.Stamp("No view is exposed to the agent yet: the owner writes views in the schema facets, "
                                + "runs refresh_catalog, and annotates them.");

        var report = new StringBuilder("view\tlabel\tanswers\n");
        foreach (var view in catalog.Views)
            report.Append(view.Name).Append('\t').Append(view.Label).Append('\t').Append(view.Answers).Append('\n');

        return active.Stamp(report.ToString());
    }

    /// <summary>Describes one view: its columns, what each allows, the values of its codes, the questions asked about it.</summary>
    [McpServerTool(Name = "describe_view", ReadOnly = true, Idempotent = true)]
    [Description("Describes one view of the SQL Server database: each column with its nature, its label and "
               + "description, its unit, what the server allows on it (grouping, aggregations, filter "
               + "operators), the values of its codes, and the questions already asked about it. Call it before "
               + "a query on the view: a query may only name what this describes.")]
    public async Task<string> DescribeView(
        [Description("Name of the view, as list_views gives it.")] string view,
        CancellationToken cancellationToken)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var catalog = await ReadCatalogAsync(active, cancellationToken);
        if (catalog.FindView(view) is not { } found)
            return active.Stamp($"The catalog has no view '{view}'. Views: {string.Join(", ", catalog.Views.Select(v => v.Name))}.");

        var report = new StringBuilder();
        report.Append($"View {found.Name}: {found.Label}\n");
        if (found.Answers.Length > 0)
            report.Append($"Answers: {found.Answers}\n");

        report.Append("column\tnature\tlabel\tunit\tallows\tdescription\n");
        foreach (var column in found.Columns)
        {
            report.Append(column.Name).Append('\t')
                  .Append(column.Nature is { } nature ? NatureRules.Code(nature) : "(none)").Append('\t')
                  .Append(column.Label).Append('\t')
                  .Append(Unit(column)).Append('\t')
                  .Append(Allows(column)).Append('\t')
                  .Append(column.Description).Append('\n');
        }

        foreach (var column in found.Columns.Where(column => column.Values.Count > 0))
        {
            report.Append($"Values of {column.Name}: ");
            report.AppendJoin("; ", column.Values.Select(value =>
                value.Description.Length > 0 ? $"{value.Value} = {value.Label} ({value.Description})" : $"{value.Value} = {value.Label}"));
            report.Append('\n');
        }

        AppendQuestions(report, "Questions about this view", catalog.Questions.Where(question =>
            string.Equals(question.ViewName, found.Name, StringComparison.OrdinalIgnoreCase)));

        return active.Stamp(report.ToString());
    }

    /// <summary>Returns the note of domain and the general questions.</summary>
    [McpServerTool(Name = "read_domain_note", ReadOnly = true, Idempotent = true)]
    [Description("Returns what the owner of the SQL Server database wrote for the agent about the data as a whole: "
               + "the business, the conventions, the calendar; and the questions already asked about the data in "
               + "general, with their answers. Call it at the start of a session, before any query.")]
    public async Task<string> ReadDomainNote(CancellationToken cancellationToken)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var catalog = await ReadCatalogAsync(active, cancellationToken);
        var report = new StringBuilder();
        report.Append(catalog.DomainNote.Length > 0 ? catalog.DomainNote : "(No note of domain yet: the owner writes one with write_domain_note.)").Append('\n');

        AppendQuestions(report, "General questions", catalog.Questions.Where(question => question.ViewName is null));
        return active.Stamp(report.ToString());
    }

    /// <summary>Runs an aggregated query on one view.</summary>
    [McpServerTool(Name = "query", ReadOnly = true, Idempotent = true)]
    [Description("Queries one view of the SQL Server database, aggregated: rows grouped by the dimensions, each "
               + "measure folded by its aggregation, after the filters. Names are those describe_view gives; "
               + "values are text, converted to the type of their column. Amounts come divided by their "
               + "divisor, in their unit and to the cent, and a filter on them is given in that unit. Returns tab-separated rows with a header, capped by the server: a "
               + "last line says when there were more. A refused request names every fault to correct.")]
    public async Task<string> Query(
        [Description("Name of the view, as list_views gives it.")] string view,
        [Description("Columns that group the rows, in the order of the result. A date takes a level: year, quarter, month, week or day.")]
        QueryDimension[]? dimensions = null,
        [Description("Columns folded over each group, with an aggregation: sum, avg, min, max, count or count distinct.")]
        QueryMeasure[]? measures = null,
        [Description("Conditions every row must satisfy. Operators: equals, in, between, less, less or equal, greater, greater or equal, contains, empty.")]
        QueryFilter[]? filters = null,
        [Description("Which result column orders the rows: 'dimension' or 'measure', its index in the request, and whether descending. Default: the first measure descending.")]
        QueryOrder? orderBy = null,
        [Description("How many rows at most. The server caps it; it cannot be raised above the cap.")]
        int? top = null,
        CancellationToken cancellationToken = default)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var faults = new List<string>();
        var request = new QueryRequest
        {
            View = view,
            Dimensions = [.. (dimensions ?? []).Select(d => new DimensionRequest(d.Column, Parse(Levels, d.Level, "level", faults)))],
            Measures = [.. (measures ?? []).Select(m => new MeasureRequest(m.Column, Parse(Aggregations, m.Aggregation, "aggregation", faults) ?? Aggregation.Count))],
            Filters = [.. (filters ?? []).Select(f => new FilterRequest(f.Column, Parse(Operators, f.Operator, "operator", faults) ?? FilterOperator.Equals, f.Values ?? []))],
            OrderBy = orderBy is null ? null : new OrderRequest(
                orderBy.By?.Equals("measure", StringComparison.OrdinalIgnoreCase) == true ? OrderTarget.Measure : OrderTarget.Dimension,
                orderBy.Index, orderBy.Descending),
            Top = top,
        };

        if (faults.Count > 0)
            return active.Stamp("Refused: " + string.Join(" ", faults));

        using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var catalog = await CatalogReader.ReadAsync(connection, cancellationToken);

        ComposedQuery composed;
        try
        {
            composed = SqlComposer.Compose(catalog, request, settings.MaxRows);
        }
        catch (QueryRefusedException refusal)
        {
            return active.Stamp("Refused: " + string.Join(" ", refusal.Faults));
        }

        var result = await QueryRunner.RunAsync(connection, composed, cancellationToken);

        var report = new StringBuilder(result.ToTsv());
        var units = composed.Columns
            .Where(column => column.Aggregation is not (null or Aggregation.Count or Aggregation.CountDistinct) && column.Column.Unit.Length > 0)
            .Select(column => $"{column.Alias} in {column.Column.Unit}")
            .ToList();
        if (units.Count > 0)
            report.Append("Units: ").AppendJoin(", ", units).Append('\n');

        return active.Stamp(report.ToString());
    }

    private static async Task<Catalog> ReadCatalogAsync(ActiveConnection active, CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return await CatalogReader.ReadAsync(connection, cancellationToken);
    }

    // A word the request does not know is a fault with the words it may use; null means "not given".
    internal static T? Parse<T>(Dictionary<string, T> words, string? word, string what, List<string> faults) where T : struct
    {
        if (word is null or { Length: 0 })
            return null;

        if (words.TryGetValue(word.Trim(), out var value))
            return value;

        faults.Add($"'{word}' is not a known {what}: use {string.Join(", ", words.Keys)}.");
        return null;
    }

    private static string Unit(FacetColumn column) =>
        column.Divisor == 1 ? column.Unit : $"{column.Unit} (stored /{column.Divisor:0.####})";

    private static string Allows(FacetColumn column)
    {
        if (column.Nature is not { } nature)
            return "not queryable yet: the owner has to set its nature";

        var parts = new List<string>();
        if (NatureRules.Groups(nature))
            parts.Add(nature == Nature.Date ? "groups by year, quarter, month, week, day" : "groups");
        parts.Add(string.Join(", ", NatureRules.Aggregations(nature).Select(SqlComposer.Word)));
        parts.Add(string.Join(", ", NatureRules.Operators(nature).Select(SqlComposer.Word)));
        return string.Join("; ", parts);
    }

    private static void AppendQuestions(StringBuilder report, string heading, IEnumerable<Question> questions)
    {
        var list = questions.ToList();
        if (list.Count == 0)
            return;

        report.Append(heading).Append(":\n");
        foreach (var question in list)
        {
            var scope = question.ColumnName is null ? "" : $" ({question.ColumnName})";
            var answer = question.AnsweredAt is null ? "not answered yet" : $"answered: {question.Answer}";
            report.Append($"#{question.Id}{scope} {question.Text} | {answer}\n");
        }
    }
}

/// <summary>A column that groups rows, with a level for a date.</summary>
public sealed record QueryDimension
{
    /// <summary>The column, as describe_view names it.</summary>
    [Description("The column, as describe_view names it.")]
    public required string Column { get; init; }

    /// <summary>For a date: year, quarter, month, week or day. Day when omitted.</summary>
    [Description("For a date: year, quarter, month, week or day. Day when omitted.")]
    public string? Level { get; init; }
}

/// <summary>A column folded by an aggregation.</summary>
public sealed record QueryMeasure
{
    /// <summary>The column, as describe_view names it.</summary>
    [Description("The column, as describe_view names it.")]
    public required string Column { get; init; }

    /// <summary>sum, avg, min, max, count or count distinct, among what describe_view allows on the column.</summary>
    [Description("sum, avg, min, max, count or count distinct, among what describe_view allows on the column.")]
    public required string Aggregation { get; init; }
}

/// <summary>A condition on a column.</summary>
public sealed record QueryFilter
{
    /// <summary>The column, as describe_view names it.</summary>
    [Description("The column, as describe_view names it.")]
    public required string Column { get; init; }

    /// <summary>equals, in, between, less, less or equal, greater, greater or equal, contains or empty, among what describe_view allows.</summary>
    [Description("equals, in, between, less, less or equal, greater, greater or equal, contains or empty, among what describe_view allows on the column.")]
    public required string Operator { get; init; }

    /// <summary>The values, as text: one for a comparison, two for between, one or more for in, none for empty.</summary>
    [Description("The values, as text: one for a comparison, two for between, one or more for in, none for empty. A date as 2026-01-31, a boolean as true or false, an amount in its unit as results give it.")]
    public string[]? Values { get; init; }
}

/// <summary>Which result column orders the rows.</summary>
public sealed record QueryOrder
{
    /// <summary>'dimension' or 'measure'.</summary>
    [Description("'dimension' or 'measure'.")]
    public string? By { get; init; }

    /// <summary>The index of that dimension or measure in the request, from 0.</summary>
    [Description("The index of that dimension or measure in the request, from 0.")]
    public int Index { get; init; }

    /// <summary>True for the largest or latest first.</summary>
    [Description("True for the largest or latest first.")]
    public bool Descending { get; init; }
}

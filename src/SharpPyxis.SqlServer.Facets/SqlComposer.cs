using System.Data;
using System.Globalization;
using System.Text;

namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// Turns a request into SQL, or refuses it. Every identifier in the SQL comes from the catalog and
/// is quoted; every value is a parameter typed after its column. A refusal names each fault, so that
/// the agent corrects the request rather than retrying it.
/// </summary>
public static class SqlComposer
{
    /// <summary>
    /// Composes the query. <paramref name="defaultMaxRows"/> is the cap of the server, which a view
    /// replaces with its own, above or below. The query asks for one row more, so that the runner can
    /// tell when the limit was hit.
    /// </summary>
    /// <exception cref="QueryRefusedException">The request names what the catalog does not have, or asks what a nature does not allow.</exception>
    public static ComposedQuery Compose(Catalog catalog, QueryRequest request, int defaultMaxRows) =>
        Compose(catalog, request, defaultMaxRows, forFile: false);

    /// <summary>
    /// Composes the same request for a file written on the workstation, which the model never reads: no
    /// cap, the top of the request only when it gives one, and the export only columns accepted as
    /// dimensions and measures. As a filter, an export only column takes empty or not empty and nothing
    /// else: the count of the file would tell whether a known value, such as an e-mail, is in the data.
    /// </summary>
    /// <exception cref="QueryRefusedException">The request names what the catalog does not have, or asks what a nature does not allow.</exception>
    public static ComposedQuery ComposeForFile(Catalog catalog, QueryRequest request) =>
        Compose(catalog, request, defaultMaxRows: null, forFile: true);

    private static ComposedQuery Compose(Catalog catalog, QueryRequest request, int? defaultMaxRows, bool forFile)
    {
        var faults = new List<string>();

        var view = catalog.FindView(request.View);
        if (view is null)
            throw new QueryRefusedException([$"The catalog has no view '{request.View}'. Views: {string.Join(", ", catalog.Views.Select(v => v.Name))}."]);

        if (request.Dimensions.Count == 0 && request.Measures.Count == 0)
            faults.Add("The request asks for no dimension and no measure.");

        var select = new List<string>();
        var groupBy = new List<string>();
        var columns = new List<ResultColumn>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dimension in request.Dimensions)
        {
            if (Resolve(view, dimension.Column, faults, forFile) is not { } column)
                continue;

            var nature = column.Nature!.Value;
            if (!NatureRules.Groups(nature))
            {
                faults.Add($"Column '{column.Name}' is {Describe(nature)} and cannot group rows.");
                continue;
            }

            if (dimension.Level is not null && nature != Nature.Date)
            {
                faults.Add($"Column '{column.Name}' is {Describe(nature)}: a level applies to a date only.");
                continue;
            }

            var level = nature == Nature.Date ? dimension.Level ?? DateLevel.Day : (DateLevel?)null;
            var expression = DimensionExpression(column, level);
            var alias = Alias(aliases, level is null ? column.Name : $"{column.Name}_{level.Value.ToString().ToLowerInvariant()}");

            select.Add($"{expression} as [{alias}]");
            groupBy.Add(expression);
            columns.Add(new ResultColumn(alias, column, Aggregation: null, Divisor: 1, Decimals: null));
        }

        foreach (var measure in request.Measures)
        {
            if (Resolve(view, measure.Column, faults, forFile) is not { } column)
                continue;

            var nature = column.Nature!.Value;
            var allowed = NatureRules.Aggregations(nature);
            if (!allowed.Contains(measure.Aggregation))
            {
                faults.Add($"{Word(measure.Aggregation)} is not allowed on '{column.Name}', which is {Describe(nature)}: "
                           + $"allowed {string.Join(", ", allowed.Select(Word))}.");
                continue;
            }

            var alias = Alias(aliases, $"{Word(measure.Aggregation).Replace(' ', '_')}_{column.Name}");
            select.Add($"{MeasureExpression(column, measure.Aggregation)} as [{alias}]");

            // Count is a number of rows, not a value of the column: the divisor does not apply.
            var divisor = measure.Aggregation is Aggregation.Count or Aggregation.CountDistinct ? 1 : column.Divisor;
            columns.Add(new ResultColumn(alias, column, measure.Aggregation, divisor, NatureRules.Decimals(nature, measure.Aggregation)));
        }

        var parameters = new List<QueryParameter>();
        var where = new List<string>();
        foreach (var filter in request.Filters)
        {
            if (Resolve(view, filter.Column, faults, forFile) is not { } column)
                continue;

            if (column.IsExportOnly && filter.Operator is not (FilterOperator.Empty or FilterOperator.NotEmpty))
            {
                faults.Add($"Column '{column.Name}' is for export only: a filter on it may only be empty or not empty.");
                continue;
            }

            var nature = column.Nature!.Value;
            var allowed = NatureRules.Operators(nature);
            if (!allowed.Contains(filter.Operator))
            {
                faults.Add($"{Word(filter.Operator)} is not allowed on '{column.Name}', which is {Describe(nature)}: "
                           + $"allowed {string.Join(", ", allowed.Select(Word))}.");
                continue;
            }

            if (FilterExpression(column, filter, parameters, faults) is { } expression)
                where.Add(expression);
        }

        var orderBy = OrderExpression(request, columns, faults);

        if (faults.Count > 0)
            throw new QueryRefusedException(faults);

        // A query asks for one row more than its limit, so that the runner can tell when it was hit; a file
        // takes what it is asked, all of it when the request gives no top.
        int? cap = defaultMaxRows is { } fallback ? view.Cap(fallback) : null;
        int? asked = request.Top is > 0 and var top ? top : null;
        var limit = cap is { } most ? Math.Min(asked ?? most, most) : asked;
        var fetched = cap is null ? limit : limit + 1;

        // LF whatever the machine: the SQL is logged and compared, and a line ending is not a choice to leave to the host.
        var sql = new StringBuilder();
        sql.Append(fetched is { } rows ? $"select top ({rows}) " : "select ").AppendJoin(", ", select).Append('\n');
        sql.Append($"from facets.[{view.Name}]").Append('\n');
        if (where.Count > 0)
            sql.Append("where ").AppendJoin(" and ", where).Append('\n');
        if (groupBy.Count > 0)
            sql.Append("group by ").AppendJoin(", ", groupBy).Append('\n');
        sql.Append("order by ").Append(orderBy).Append(';');

        return new ComposedQuery(sql.ToString(), parameters, columns, limit, cap);
    }

    private static FacetColumn? Resolve(FacetView view, string name, List<string> faults, bool forFile)
    {
        var column = view.FindColumn(name);
        if (column is null)
        {
            faults.Add($"View '{view.Name}' has no column '{name}'. Columns: {string.Join(", ", view.Columns.Select(c => c.Name))}.");
            return null;
        }

        // Named by describe_view, so it can be asked for: refused wherever it is in a query, since a
        // dimension would return its values, min and max one of them, and a filter would tell whether a
        // known value is there. A file takes it; its filters are bounded by the caller.
        if (column.IsExportOnly && !forFile)
        {
            faults.Add($"Column '{column.Name}' is for export only: its values may go to an exported file, never to a result.");
            return null;
        }

        if (column.Nature is null)
        {
            faults.Add($"Column '{column.Name}' has no nature yet, so the server cannot tell what it allows: ask the owner of the dictionary to set one.");
            return null;
        }

        return column;
    }

    internal static string DimensionExpression(FacetColumn column, DateLevel? level)
    {
        var name = $"[{column.Name}]";
        return level switch
        {
            null => name,
            DateLevel.Year => $"datepart(year, {name})",
            DateLevel.Quarter => $"concat(datepart(year, {name}), N'-Q', datepart(quarter, {name}))",
            DateLevel.Month => $"concat(datepart(year, {name}), N'-', right(concat(N'0', datepart(month, {name})), 2))",
            // The ISO week with its own year, which is not always the calendar year: 2025-12-29 is in
            // 2026-W01, 2027-01-01 in 2026-W53. Moved by 26 days less its week number, any day lands
            // inside the middle of its ISO year, whatever @@DATEFIRST says.
            DateLevel.Week => $"concat(datepart(year, dateadd(day, 26 - datepart(iso_week, {name}), {name})), N'-W', "
                              + $"right(concat(N'0', datepart(iso_week, {name})), 2))",
            DateLevel.Day => $"cast({name} as date)",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
        };
    }

    private static string MeasureExpression(FacetColumn column, Aggregation aggregation)
    {
        var name = $"[{column.Name}]";
        return aggregation switch
        {
            Aggregation.Sum => $"sum({name})",
            // An integer column averages as an integer in T-SQL: the cast keeps the decimals.
            Aggregation.Avg => $"avg(cast({name} as decimal(38, 6)))",
            Aggregation.Min => $"min({name})",
            Aggregation.Max => $"max({name})",
            Aggregation.Count => $"count({name})",
            Aggregation.CountDistinct => $"count(distinct {name})",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, null),
        };
    }

    private static string? FilterExpression(FacetColumn column, FilterRequest filter, List<QueryParameter> parameters, List<string> faults)
    {
        var name = $"[{column.Name}]";
        var kind = SqlType.Of(column.SqlType);
        var expected = filter.Operator switch
        {
            FilterOperator.Empty or FilterOperator.NotEmpty => 0,
            FilterOperator.Between => 2,
            FilterOperator.In => -1,
            _ => 1,
        };

        if (expected >= 0 && filter.Values.Count != expected || expected < 0 && filter.Values.Count == 0)
        {
            faults.Add($"{Word(filter.Operator)} on '{column.Name}' takes {(expected < 0 ? "one or more values" : expected switch { 0 => "no value", 1 => "one value", _ => $"{expected} values" })}, "
                       + $"not {filter.Values.Count}.");
            return null;
        }

        if (filter.Operator == FilterOperator.Empty)
            return kind.IsText ? $"({name} is null or {name} = N'')" : $"{name} is null";

        if (filter.Operator == FilterOperator.NotEmpty)
            return kind.IsText ? $"({name} is not null and {name} <> N'')" : $"{name} is not null";

        if (filter.Operator == FilterOperator.Contains)
        {
            var pattern = "%" + filter.Values[0].Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
            return $"{name} like {Add(parameters, SqlDbType.NVarChar, pattern)} escape N'\\'";
        }

        var converted = new List<string>();
        foreach (var value in filter.Values)
        {
            // A column with a divisor is read in its unit, so a value is given in that unit too: 120.5
            // euros on a column of cents compares with 12050. A decimal, so that a fraction of the stored
            // unit compares as it is instead of being cut.
            if (column.Divisor != 1)
            {
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var inUnit))
                    converted.Add(Add(parameters, SqlDbType.Decimal, inUnit * column.Divisor));
                else
                    faults.Add($"'{value}' is not a number for '{column.Name}'.");
            }
            else if (kind.TryConvert(value, out var typed))
                converted.Add(Add(parameters, kind.DbType, typed));
            else
                faults.Add($"'{value}' is not a valid {column.SqlType} for '{column.Name}'.");
        }

        if (converted.Count != filter.Values.Count)
            return null;

        return filter.Operator switch
        {
            FilterOperator.Equals => $"{name} = {converted[0]}",
            FilterOperator.In => $"{name} in ({string.Join(", ", converted)})",
            FilterOperator.Between => $"{name} between {converted[0]} and {converted[1]}",
            FilterOperator.Less => $"{name} < {converted[0]}",
            FilterOperator.LessOrEqual => $"{name} <= {converted[0]}",
            FilterOperator.Greater => $"{name} > {converted[0]}",
            FilterOperator.GreaterOrEqual => $"{name} >= {converted[0]}",
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, null),
        };
    }

    private static string Add(List<QueryParameter> parameters, SqlDbType type, object value)
    {
        var name = $"@p{parameters.Count}";
        parameters.Add(new QueryParameter(name, type, value));
        return name;
    }

    private static string OrderExpression(QueryRequest request, List<ResultColumn> columns, List<string> faults)
    {
        var dimensions = columns.Where(column => column.Aggregation is null).ToList();
        var measures = columns.Where(column => column.Aggregation is not null).ToList();

        if (request.OrderBy is null)
        {
            var first = measures.Count > 0 ? measures[0] : dimensions.FirstOrDefault();
            return first is null ? "1" : measures.Count > 0 ? $"[{first.Alias}] desc" : $"[{first.Alias}]";
        }

        var (target, index, descending) = request.OrderBy;
        var list = target == OrderTarget.Measure ? measures : dimensions;
        var requested = target == OrderTarget.Measure ? request.Measures.Count : request.Dimensions.Count;
        if (index < 0 || index >= requested)
        {
            faults.Add($"The order names {target.ToString().ToLowerInvariant()} {index}, and the request has {requested}.");
            return "1";
        }

        // A refused column is already a fault; the order is reported once the list is whole.
        return index < list.Count ? $"[{list[index].Alias}]{(descending ? " desc" : "")}" : "1";
    }

    private static string Alias(HashSet<string> taken, string wanted)
    {
        var alias = wanted;
        for (var n = 2; !taken.Add(alias); n++)
            alias = $"{wanted}_{n}";
        return alias;
    }

    private static string Describe(Nature nature) => nature switch
    {
        Nature.Amount => "an amount",
        Nature.Quantity => "a quantity",
        Nature.Rate => "a rate",
        Nature.Date => "a date",
        Nature.Code => "a code",
        Nature.Label => "a label",
        Nature.Identifier => "an identifier",
        Nature.Boolean => "a boolean",
        Nature.Text => "free text",
        _ => nature.ToString().ToLowerInvariant(),
    };

    /// <summary>The aggregation as the request and the result spell it.</summary>
    public static string Word(Aggregation aggregation) => aggregation switch
    {
        Aggregation.CountDistinct => "count distinct",
        _ => aggregation.ToString().ToLowerInvariant(),
    };

    /// <summary>The operator as the request spells it.</summary>
    public static string Word(FilterOperator filterOperator) => filterOperator switch
    {
        FilterOperator.Equals => "equals",
        FilterOperator.In => "in",
        FilterOperator.Between => "between",
        FilterOperator.Less => "less",
        FilterOperator.LessOrEqual => "less or equal",
        FilterOperator.Greater => "greater",
        FilterOperator.GreaterOrEqual => "greater or equal",
        FilterOperator.Contains => "contains",
        FilterOperator.Empty => "empty",
        FilterOperator.NotEmpty => "not empty",
        _ => filterOperator.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The family of a SQL Server type, read from the type the dictionary stores: how a value typed as
    /// text converts, and how the parameter is typed.
    /// </summary>
    private sealed record SqlType(SqlDbType DbType, bool IsText, Func<string, object?> Convert)
    {
        public static SqlType Of(string sqlType)
        {
            var name = sqlType.Split('(')[0].Trim().ToLowerInvariant();
            return name switch
            {
                "bit" => new(SqlDbType.Bit, false, value => value.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" => true,
                    "false" or "0" or "no" => false,
                    _ => null,
                }),
                "tinyint" or "smallint" or "int" or "bigint" => new(SqlDbType.BigInt, false,
                    value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null),
                "decimal" or "numeric" or "money" or "smallmoney" => new(SqlDbType.Decimal, false,
                    value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null),
                "float" or "real" => new(SqlDbType.Float, false,
                    value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null),
                "date" => new(SqlDbType.Date, false,
                    value => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var d) ? d.ToDateTime(TimeOnly.MinValue) : null),
                "datetime" or "datetime2" or "smalldatetime" => new(SqlDbType.DateTime2, false,
                    value => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null),
                "datetimeoffset" => new(SqlDbType.DateTimeOffset, false,
                    value => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null),
                "time" => new(SqlDbType.Time, false,
                    value => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var t) ? t : null),
                "uniqueidentifier" => new(SqlDbType.UniqueIdentifier, false,
                    value => Guid.TryParse(value, out var g) ? g : null),
                "char" or "varchar" => new(SqlDbType.VarChar, true, value => value),
                _ => new(SqlDbType.NVarChar, true, value => value),
            };
        }

        public bool TryConvert(string value, out object typed)
        {
            typed = Convert(value)!;
            return typed is not null;
        }
    }
}

/// <summary>
/// The SQL and its parameters, with what each result column is. <paramref name="Limit"/> is the most
/// rows it returns, <paramref name="Cap"/> the most the view allows: the limit is below the cap when the
/// request asked for fewer. For a file there is no cap, and no limit unless the request gave a top.
/// </summary>
public sealed record ComposedQuery(string Sql, IReadOnlyList<QueryParameter> Parameters, IReadOnlyList<ResultColumn> Columns, int? Limit, int? Cap);

/// <summary>One parameter of a composed query, typed after its column.</summary>
public sealed record QueryParameter(string Name, SqlDbType Type, object Value);

/// <summary>
/// One column of the result: its alias in the SQL, the column of the catalog it comes from, the
/// aggregation when it is a measure, the divisor the runner applies to its values, and the decimals
/// it rounds them to after dividing, when their nature fixes any.
/// </summary>
public sealed record ResultColumn(string Alias, FacetColumn Column, Aggregation? Aggregation, decimal Divisor, int? Decimals);

/// <summary>A request the composer will not turn into SQL, with every fault it found.</summary>
public sealed class QueryRefusedException(IReadOnlyList<string> faults) : Exception(string.Join(" ", faults))
{
    /// <summary>Each fault, in the order of the request.</summary>
    public IReadOnlyList<string> Faults { get; } = faults;
}

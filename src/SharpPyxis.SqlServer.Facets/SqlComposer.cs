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
    /// Composes the query. <paramref name="maxRows"/> is the cap of the server: the query asks for one
    /// row more, so that the runner can tell when the cap was hit.
    /// </summary>
    /// <exception cref="QueryRefusedException">The request names what the catalog does not have, or asks what a nature does not allow.</exception>
    public static ComposedQuery Compose(Catalog catalog, QueryRequest request, int maxRows)
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
            if (Resolve(view, dimension.Column, faults) is not { } column)
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
            columns.Add(new ResultColumn(alias, column, Aggregation: null, Divisor: 1));
        }

        foreach (var measure in request.Measures)
        {
            if (Resolve(view, measure.Column, faults) is not { } column)
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
            columns.Add(new ResultColumn(alias, column, measure.Aggregation, divisor));
        }

        var parameters = new List<QueryParameter>();
        var where = new List<string>();
        foreach (var filter in request.Filters)
        {
            if (Resolve(view, filter.Column, faults) is not { } column)
                continue;

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

        var top = Math.Min(request.Top is > 0 and var asked ? asked : maxRows, maxRows);

        // LF whatever the machine: the SQL is logged and compared, and a line ending is not a choice to leave to the host.
        var sql = new StringBuilder();
        sql.Append($"select top ({top + 1}) ").AppendJoin(", ", select).Append('\n');
        sql.Append($"from facets.[{view.Name}]").Append('\n');
        if (where.Count > 0)
            sql.Append("where ").AppendJoin(" and ", where).Append('\n');
        if (groupBy.Count > 0)
            sql.Append("group by ").AppendJoin(", ", groupBy).Append('\n');
        sql.Append("order by ").Append(orderBy).Append(';');

        return new ComposedQuery(sql.ToString(), parameters, columns, top);
    }

    private static FacetColumn? Resolve(FacetView view, string name, List<string> faults)
    {
        var column = view.FindColumn(name);
        if (column is null)
        {
            faults.Add($"View '{view.Name}' has no column '{name}'. Columns: {string.Join(", ", view.Columns.Select(c => c.Name))}.");
            return null;
        }

        if (column.Nature is null)
        {
            faults.Add($"Column '{column.Name}' has no nature yet, so the server cannot tell what it allows: ask the owner of the dictionary to set one.");
            return null;
        }

        return column;
    }

    private static string DimensionExpression(FacetColumn column, DateLevel? level)
    {
        var name = $"[{column.Name}]";
        return level switch
        {
            null => name,
            DateLevel.Year => $"datepart(year, {name})",
            DateLevel.Quarter => $"concat(datepart(year, {name}), N'-Q', datepart(quarter, {name}))",
            DateLevel.Month => $"concat(datepart(year, {name}), N'-', right(concat(N'0', datepart(month, {name})), 2))",
            // The ISO week with the calendar year: the first days of January may read as week 52 or 53
            // of that year, which is wrong by one year three days a year, and accepted.
            DateLevel.Week => $"concat(datepart(year, {name}), N'-W', right(concat(N'0', datepart(iso_week, {name})), 2))",
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
            FilterOperator.Empty => 0,
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

        if (filter.Operator == FilterOperator.Contains)
        {
            var pattern = "%" + filter.Values[0].Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
            return $"{name} like {Add(parameters, SqlDbType.NVarChar, pattern)} escape N'\\'";
        }

        var converted = new List<string>();
        foreach (var value in filter.Values)
        {
            if (kind.TryConvert(value, out var typed))
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

/// <summary>The SQL and its parameters, with what each result column is.</summary>
public sealed record ComposedQuery(string Sql, IReadOnlyList<QueryParameter> Parameters, IReadOnlyList<ResultColumn> Columns, int Limit);

/// <summary>One parameter of a composed query, typed after its column.</summary>
public sealed record QueryParameter(string Name, SqlDbType Type, object Value);

/// <summary>
/// One column of the result: its alias in the SQL, the column of the catalog it comes from, the
/// aggregation when it is a measure, and the divisor the runner applies to its values.
/// </summary>
public sealed record ResultColumn(string Alias, FacetColumn Column, Aggregation? Aggregation, decimal Divisor);

/// <summary>A request the composer will not turn into SQL, with every fault it found.</summary>
public sealed class QueryRefusedException(IReadOnlyList<string> faults) : Exception(string.Join(" ", faults))
{
    /// <summary>Each fault, in the order of the request.</summary>
    public IReadOnlyList<string> Faults { get; } = faults;
}

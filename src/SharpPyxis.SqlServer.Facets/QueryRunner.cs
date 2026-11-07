using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// Runs a composed query and shapes its rows for the model: values as text, divisors applied,
/// at most the limit of the query, and a flag when there were more.
/// </summary>
public static class QueryRunner
{
    /// <summary>Runs the query on an open connection.</summary>
    public static async Task<QueryResult> RunAsync(SqlConnection connection, ComposedQuery query, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand(query.Sql, connection);
        foreach (var parameter in query.Parameters)
            command.Parameters.Add(parameter.Name, parameter.Type).Value = parameter.Value;

        var rows = new List<string[]>();
        var truncated = false;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // The query asks for one row more than the limit: that row says there were more, and is not kept.
            if (rows.Count == query.Limit)
            {
                truncated = true;
                break;
            }

            var row = new string[query.Columns.Count];
            for (var i = 0; i < row.Length; i++)
                row[i] = Format(reader.IsDBNull(i) ? null : reader.GetValue(i), query.Columns[i].Divisor, query.Columns[i].Decimals);
            rows.Add(row);
        }

        return new QueryResult(query.Columns, rows, truncated, query.Cap);
    }

    /// <summary>
    /// One value as the model reads it: invariant culture, no thousands separator, dates without a
    /// midnight time, numbers divided by the divisor of their column then rounded to its decimals when
    /// it has any, null as an empty cell.
    /// </summary>
    public static string Format(object? value, decimal divisor, int? decimals = null) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        byte or short or int or long => divisor == 1
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Number(Convert.ToDecimal(value, CultureInfo.InvariantCulture) / divisor, decimals),
        decimal d => Number(d / divisor, decimals),
        float or double => Number((decimal)Convert.ToDouble(value, CultureInfo.InvariantCulture) / divisor, decimals),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        string s => s.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // Rounded half away from zero, as T-SQL rounds: the banker's rounding of Math.Round would part
    // from the database on an exact half cent. Trailing zeros are noise the model pays for: 120.50
    // reads as 120.5, 3.000000 as 3.
    private static string Number(decimal number, int? decimals)
    {
        if (decimals is { } places)
            number = Math.Round(number, places, MidpointRounding.AwayFromZero);

        var text = number.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }
}

/// <summary>
/// The rows of a query, as text, with the columns they answer to. <paramref name="Cap"/> is the most rows
/// the view allows, which says whether a cut came from the request or from the view.
/// </summary>
public sealed record QueryResult(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<string[]> Rows, bool Truncated, int Cap)
{
    /// <summary>
    /// The result as the model reads it: a header line of aliases, one line per row, tab-separated,
    /// LF line endings whatever the machine, and a last line when the rows were cut at the limit.
    /// </summary>
    public string ToTsv()
    {
        var text = new StringBuilder();
        text.AppendJoin('\t', Columns.Select(column => column.Alias)).Append('\n');
        foreach (var row in Rows)
            text.AppendJoin('\t', row).Append('\n');

        // Cut at the cap, the agent cannot ask for more, and splitting the request in two would cost
        // more calls and a result it reads badly: the cap is the owner's to raise, so it is told to say so.
        if (Truncated && Rows.Count < Cap)
            text.Append($"({Rows.Count} rows shown, as top asked, and there are more: this view returns up to {Cap}.)\n");
        else if (Truncated)
            text.Append($"({Rows.Count} rows shown, the most this view returns, and there are more: narrow the request, or order it "
                        + "so the rows that matter come first. If the answer needs them all, say so with ask_question: the owner "
                        + "of the dictionary sets the cap of each view.)\n");

        return text.ToString();
    }
}

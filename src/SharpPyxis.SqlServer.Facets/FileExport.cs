using System.Text;
using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// Writes the result of a query composed for a file as a CSV that Excel opens as it is, in French as in
/// English: separated by <c>;</c>, UTF-8 with its byte order mark, CRLF, a header of the result columns.
/// Rows are written as they are read, so their number is bounded by nothing but the disk.
/// </summary>
public static class FileExport
{
    /// <summary>How long the query may run: a whole view of a large database, not one aggregate.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Every how many rows <c>onRows</c> is told how far the file is.</summary>
    public const int ProgressStep = 5000;

    /// <summary>
    /// Runs the query and writes its rows to <paramref name="path"/>, which must not exist. Writes nothing
    /// when there is no row. The file appears under its name only once complete: it is written beside,
    /// under a <c>.partial</c> name, and removed if anything fails.
    /// </summary>
    /// <returns>The number of rows written.</returns>
    public static async Task<long> WriteAsync(
        SqlConnection connection, ComposedQuery query, string path, Action<long>? onRows, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand(query.Sql, connection) { CommandTimeout = (int)Timeout.TotalSeconds };
        foreach (var parameter in query.Parameters)
            command.Parameters.Add(parameter.Name, parameter.Type).Value = parameter.Value;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return 0;

        // Created before the try: a partial file that exists already is someone else's, and stays.
        var partial = path + ".partial";
        var stream = new FileStream(partial, FileMode.CreateNew);
        long rows = 0;
        try
        {
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.NewLine = "\r\n";
                await writer.WriteLineAsync(string.Join(';', query.Columns.Select(column => Quote(column.Alias))));

                var cells = new string[query.Columns.Count];
                do
                {
                    for (var i = 0; i < cells.Length; i++)
                        cells[i] = Quote(Format(reader.IsDBNull(i) ? null : reader.GetValue(i), query.Columns[i].Divisor, query.Columns[i].Decimals));

                    await writer.WriteLineAsync(string.Join(';', cells));

                    if (++rows % ProgressStep == 0)
                        onRows?.Invoke(rows);
                }
                while (await reader.ReadAsync(cancellationToken));
            }

            File.Move(partial, path);
            return rows;
        }
        catch
        {
            await stream.DisposeAsync();
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>
    /// One value as Excel reads it: text kept whole, line breaks included; a boolean as 1 or 0, which
    /// Excel counts and filters as a number where true and false stay text in a French Excel; a number
    /// with a decimal point and dates in ISO, as the model reads them.
    /// </summary>
    public static string Format(object? value, decimal divisor, int? decimals = null) => value switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        string s => s,
        _ => QueryRunner.Format(value, divisor, decimals),
    };

    /// <summary>A cell between quotes when it holds the separator, a quote or a line break; its quotes doubled.</summary>
    public static string Quote(string cell) =>
        cell.AsSpan().IndexOfAny(";\"\r\n") < 0 ? cell : $"\"{cell.Replace("\"", "\"\"")}\"";
}

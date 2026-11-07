using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.Facets.Mcp;

// The exports: where they go. The root belongs to the user, declared with configure; the agent only
// names a working folder under it, one level deep.
internal sealed partial class FacetsTools
{
    /// <summary>Chooses the working folder of the exports, or lists the folders already there.</summary>
    [McpServerTool(Name = "use_export_folder", ReadOnly = true)]
    [Description("Chooses the folder, under the export root of the connection, where query_to_file writes its "
               + "files; called without arguments, lists the folders already there and tells which one is in use. "
               + "Choose one when a subject starts, named after it in a few words, and propose a new one when the "
               + "subject changes: not at every export. Without one, files go to the root. Changing the "
               + "connection starts without a folder.")]
    public async Task<string> UseExportFolder(
        [Description("Name of the folder, in a few words, such as 'Customers without an order in 2026'. One "
                   + "level under the root: a path is not accepted. Omit to list the folders.")]
        string? folder = null,
        CancellationToken cancellationToken = default)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var root = ExportLocation.Root(active.Entry, active.Database);

        if (folder is not { Length: > 0 })
            return active.Stamp(ListExportFolders(root, active.ExportFolder));

        if (ExportLocation.FolderName(folder) is not { } name)
            return active.Stamp($"Refused: '{folder}' leaves no usable folder name. Give a few words, without a path.");

        active.ExportFolder = name;

        var report = new StringBuilder($"Exports now go to {Path.Combine(root, name)}");
        if (!string.Equals(name, folder.Trim(), StringComparison.Ordinal))
            report.Append($" (the name '{folder}' became '{name}', which Windows accepts)");
        report.Append(". The folder is created with the first export.");

        return active.Stamp(report.ToString());
    }

    /// <summary>Runs a request as query does, and writes the whole result to a file on the workstation.</summary>
    [McpServerTool(Name = "query_to_file")]
    [Description("Runs the same request as query and writes the whole result to a CSV file on this machine, in the "
               + "folder chosen with use_export_folder, with a .json sheet beside it that says what the file holds. "
               + "Returns the path, the number of rows and the sheet, never the rows: do not try to read the file, "
               + "it is for the user. No cap on the rows; top limits them when given. Columns that are export only "
               + "are accepted as dimensions and measures, and in a filter with empty or not empty only. Use it when "
               + "the user wants the result as a file, or when query was cut and every row is needed. The file opens "
               + "in Excel: separated by ;, UTF-8, dates as 2026-01-31, decimals with a point, booleans as 1 or 0.")]
    public Task<string> QueryToFile(
        McpServer server,
        [Description("Name of the view, as list_views gives it.")] string view,
        [Description("What the file holds, in a few words: it names the file, after the date and time, such as 'Customers with an e-mail'.")]
        string label,
        [Description("Columns that group the rows, in the order of the file. A date takes a level: year, quarter, month, week or day. To write each row of the view, group by its identifier.")]
        QueryDimension[]? dimensions = null,
        [Description("Columns folded over each group, with an aggregation: sum, avg, min, max, count or count distinct.")]
        QueryMeasure[]? measures = null,
        [Description("Conditions every row must satisfy. Operators: equals, in, between, less, less or equal, greater, greater or equal, contains, empty, not empty.")]
        QueryFilter[]? filters = null,
        [Description("Which column orders the rows: 'dimension' or 'measure', its index in the request, and whether descending. Default: the first measure descending.")]
        QueryOrder? orderBy = null,
        [Description("How many rows at most. Omit for all of them.")]
        int? top = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) =>
        QueryToFileAsync(
            view, label, dimensions, measures, filters, orderBy, top, Author(server),
            progress is null ? null : rows => progress.Report(new ProgressNotificationValue { Progress = rows, Message = $"{rows} rows written" }),
            cancellationToken);

    // Apart from the tool, so that the tests run it without a protocol: the client is given, not read.
    internal async Task<string> QueryToFileAsync(
        string view, string label, QueryDimension[]? dimensions, QueryMeasure[]? measures, QueryFilter[]? filters,
        QueryOrder? orderBy, int? top, string client, Action<long>? onRows, CancellationToken cancellationToken)
    {
        if (await ActiveAsync(cancellationToken) is not { } active)
            return NoConnection;

        var faults = new List<string>();
        var request = BuildRequest(view, dimensions, measures, filters, orderBy, top, faults);
        var name = ExportLocation.FolderName(label);
        if (name is null)
            faults.Add($"The label '{label}' leaves no usable file name: give a few words.");
        if (faults.Count > 0 || name is null)
            return active.Stamp("Refused: " + string.Join(" ", faults));

        using var connection = new SqlConnection(active.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var catalog = await CatalogReader.ReadAsync(connection, cancellationToken);

        ComposedQuery composed;
        try
        {
            composed = SqlComposer.ComposeForFile(catalog, request);
        }
        catch (QueryRefusedException refusal)
        {
            return active.Stamp("Refused: " + string.Join(" ", refusal.Faults));
        }

        var folder = active.ExportPath;
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.Now;
        var csv = FreePath(folder, $"{now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture)}_{name}");

        var rows = await FileExport.WriteAsync(connection, composed, csv, onRows, cancellationToken);
        if (rows == 0)
            return active.Stamp("No row matches the request: no file was written.");

        var sheet = new ExportSheet(
            Path.GetFileName(csv), folder, rows, now, active.Describe(), active.Database, client, request.View,
            [.. composed.Columns.Select(column => new ExportSheetColumn(
                column.Alias, column.Column.Name, column.Column.Label,
                column.Aggregation is { } aggregation ? SqlComposer.Word(aggregation) : null,
                column.Aggregation is not (Aggregation.Count or Aggregation.CountDistinct) && column.Column.Unit.Length > 0 ? column.Column.Unit : null))],
            [.. request.Filters.Select(filter => new ExportSheetFilter(filter.Column, SqlComposer.Word(filter.Operator), filter.Values))],
            request.Top);
        var json = JsonSerializer.Serialize(sheet, SheetOptions);
        await File.WriteAllTextAsync(Path.ChangeExtension(csv, ".json"), json, new UTF8Encoding(false), cancellationToken);

        var report = new StringBuilder($"Written: {csv}\n{rows} rows. They are in the file only, for the user: do not read it.\n");
        if (active.ExportFolder is null)
            report.Append("No working folder is chosen, so the file went to the export root: use_export_folder groups the files of a subject.\n");
        report.Append($"Sheet, beside the file as {Path.GetFileName(Path.ChangeExtension(csv, ".json"))}:\n{json}");
        return active.Stamp(report.ToString());
    }

    // Never over a file: the same label in the same minute takes -2, -3. The sheet and a file being
    // written count as taken, so that a pair is never split.
    internal static string FreePath(string folder, string stem)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? stem : $"{stem}-{n}";
            var csv = Path.Combine(folder, name + ".csv");
            if (!File.Exists(csv) && !File.Exists(csv + ".partial") && !File.Exists(Path.Combine(folder, name + ".json")))
                return csv;
        }
    }

    private static readonly JsonSerializerOptions SheetOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Accents and quotes as they are: the sheet is read by a person as much as by a tool.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ListExportFolders(string root, string? current)
    {
        var report = new StringBuilder($"Export root: {root}\n");

        var folders = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        report.Append(folders.Count == 0 ? "No folder yet.\n" : "Folders:\n" + string.Join('\n', folders) + '\n');

        report.Append(current is null ? "In use: none, files go to the root." : $"In use: {current}");
        return report.ToString();
    }
}

/// <summary>
/// What a file holds, written beside it and returned to the agent: one can tell what a file is without
/// opening it, and the agent learns what it wrote without reading a row.
/// </summary>
internal sealed record ExportSheet(
    string File, string Folder, long Rows, DateTimeOffset ExportedAt, string Connection, string Database, string Client,
    string View, IReadOnlyList<ExportSheetColumn> Columns, IReadOnlyList<ExportSheetFilter> Filters, int? Top);

/// <summary>One column of the file: its header, the column of the view, its label, and for a measure its aggregation and unit.</summary>
internal sealed record ExportSheetColumn(string Name, string Column, string Label, string? Aggregation, string? Unit);

/// <summary>One filter of the request, in the words of the request.</summary>
internal sealed record ExportSheetFilter(string Column, string Operator, IReadOnlyList<string> Values);

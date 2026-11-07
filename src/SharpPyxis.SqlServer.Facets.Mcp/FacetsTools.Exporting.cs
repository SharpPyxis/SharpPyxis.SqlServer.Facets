using System.ComponentModel;
using System.Text;
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

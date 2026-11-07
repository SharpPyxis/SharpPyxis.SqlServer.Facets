namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// Where the exports of a connection go. The root is the one the user declared with <c>configure</c>,
/// or a folder named after the connection under <c>%USERPROFILE%\Facets</c>; a connection that names a
/// server only adds one folder per database under it. The agent names folders and files under the
/// root, never above it, and every name it gives goes through <see cref="FolderName"/>.
/// </summary>
public static class ExportLocation
{
    /// <summary>The longest name kept for one folder or file: a path has to stay readable, and open.</summary>
    public const int MaxNameLength = 100;

    // The characters Windows refuses in a file name, whatever the system the server runs on: the file
    // is opened on Windows in the end, by Excel or by the tool that imports it.
    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The default root, when the user declared none. Not under Documents: Windows offers to sync it with
    /// OneDrive, and a managed workstation often does, which would send exported data to a cloud nobody
    /// chose.
    /// </summary>
    public static string DefaultRoot(ConnectionEntry entry) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Facets",
        FolderName(entry.Label ?? string.Empty) ?? FolderName(entry.Server) ?? "connection");

    /// <summary>The root of the connection, before any folder of a database.</summary>
    public static string BaseRoot(ConnectionEntry entry) =>
        entry.ExportRoot is { Length: > 0 } declared ? declared : DefaultRoot(entry);

    /// <summary>
    /// The root the exports of <paramref name="database"/> go under: the base root, plus a folder for the
    /// database when the connection names a server only.
    /// </summary>
    public static string Root(ConnectionEntry entry, string database) =>
        entry.Database is { Length: > 0 }
            ? BaseRoot(entry)
            : Path.Combine(BaseRoot(entry), FolderName(database) ?? "database");

    /// <summary>
    /// A name made safe for one folder or file: a character Windows refuses becomes <c>_</c>, spaces and
    /// dots go from both ends, a reserved device name takes a <c>_</c> before its extension, and the length is cut at
    /// <see cref="MaxNameLength"/>. A separator never survives, so the name stays one level deep; null when
    /// nothing is left, as with <c>..</c>.
    /// </summary>
    public static string? FolderName(string name)
    {
        var chars = name.Select(c => c < ' ' || Forbidden.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim(' ', '.');

        if (cleaned.Length > MaxNameLength)
            cleaned = cleaned[..MaxNameLength].TrimEnd(' ', '.');

        if (cleaned.Length == 0)
            return null;

        // CON is refused with or without an extension: CON.csv is CON to Windows, and so is CON.csv_.
        var stem = cleaned.Split('.')[0];
        return Reserved.Contains(stem) ? $"{stem}_{cleaned[stem.Length..]}" : cleaned;
    }
}

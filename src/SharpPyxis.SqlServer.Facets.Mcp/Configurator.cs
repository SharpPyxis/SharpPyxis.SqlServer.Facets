using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets.Mcp;

/// <summary>
/// The <c>configure</c> sub-command: the one place where credentials are typed.
/// </summary>
/// <remarks>
/// It runs in the foreground, started by the user. The server never asks for anything: a stdio
/// process blocked on a prompt misses the handshake the client waits for, and a window would open
/// without focus, behind the client or on another desktop.
/// </remarks>
internal static class Configurator
{
    /// <summary>Runs the sub-command and returns the process exit code.</summary>
    public static int Run(ConnectionStore store, string[] arguments)
    {
        // Checked before anything is asked: otherwise 'add' takes every answer, the password included,
        // and fails only when it writes the file.
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "'configure' keeps the connections in a file encrypted with DPAPI, which exists on Windows only.\n"
                + "Outside Windows, declare one connection with the CONNECTION_STRING environment variable, in the\n"
                + "configuration of the MCP client. Its password is then written there in plain text.");
            return 1;
        }

        // No verb prints the help. Starting an interactive prompt on a bare command hides the other
        // sub-commands from whoever did not already know they existed.
        if (arguments.Length <= 1)
            return Usage(store);

        return arguments[1].ToLowerInvariant() switch
        {
            "add" or "--add" => Add(store),
            "list" or "--list" => List(store),
            "remove" or "--remove" => Remove(store, arguments),
            "test" or "--test" => Test(store, arguments),
            "set-password" => SetPassword(store, arguments),
            "set-export-root" => SetExportRoot(store, arguments),
            _ => Usage(store),
        };
    }

    private static int Add(ConnectionStore store)
    {
        Console.WriteLine("New connection. An empty answer keeps the value unset.");

        var server = Ask("Server (host, host\\instance or host,port)");
        if (server.Length == 0)
        {
            Console.Error.WriteLine("A server is required.");
            return 1;
        }

        var database = Ask("Database (empty to choose it per session)");
        var login = Ask("SQL login (empty for Windows authentication)");
        var password = login.Length == 0 ? string.Empty : AskSecret("Password");
        var label = Ask("Label, to name this target in plain words (optional)");
        var trust = AskYesNo("Trust a server certificate the machine does not trust?");

        var entry = new ConnectionEntry
        {
            Id = 0,
            Server = server,
            Database = database.Length == 0 ? null : database,
            Login = login.Length == 0 ? null : login,
            Password = password.Length == 0 ? null : password,
            Label = label.Length == 0 ? null : label,
            TrustServerCertificate = trust,
        };
        entry = entry with { ExportRoot = AskExportRoot(entry) };

        // A failed test does not throw the answers away: the instance may simply be down, and
        // retyping a password because of that is how a tool loses its user.
        if (!Verify(entry) && !AskYesNo("Save it anyway?"))
            return 1;

        var stored = store.Add(entry);
        Console.WriteLine($"Saved as {stored.Id}: {stored.Describe()}");
        Console.WriteLine($"File: {store.FilePath}");
        return 0;
    }

    private static int List(ConnectionStore store)
    {
        var connections = store.Load();
        if (connections.Count == 0)
        {
            Console.WriteLine($"No connection stored. File: {store.FilePath}");
            return 0;
        }

        // Seeing them listed is also what shows where logins have been left lying around.
        foreach (var entry in connections)
            Console.WriteLine($"{entry.Id}\t{entry.Describe()}\n\texports: {DescribeExportRoot(entry)}");

        Console.WriteLine($"File: {store.FilePath}");
        return 0;
    }

    private static int Remove(ConnectionStore store, string[] arguments)
    {
        if (arguments.Length < 3 || !int.TryParse(arguments[2], out var id))
        {
            Console.Error.WriteLine("Usage: configure remove <id>");
            return 1;
        }

        if (!store.Remove(id))
        {
            Console.Error.WriteLine($"No connection {id}.");
            return 1;
        }

        Console.WriteLine($"Connection {id} removed. The other identifiers do not move.");
        return 0;
    }

    /// <summary>The sub-commands as the help lists them, shared with server_info so the two never disagree.</summary>
    public const string Commands = """
          configure add                    record a new connection, testing it first
          configure list                   show what is recorded
          configure test [id]              test one recorded connection, or all of them
          configure set-password <id>      replace the password of one connection
          configure set-export-root <id>   choose the folder the exports of one connection go to
          configure remove <id>            delete one connection
        """;

    private static int Usage(ConnectionStore store)
    {
        Console.WriteLine($"""
            Manages the connections this server can work against.

            {Commands}

            Nothing is recorded until a connection has been tested, or kept despite a failed test.
            """);
        Console.WriteLine($"File: {store.FilePath}");
        return 1;
    }

    private static int Test(ConnectionStore store, string[] arguments)
    {
        var connections = store.Load();
        if (arguments.Length >= 3)
        {
            if (!int.TryParse(arguments[2], out var id) || connections.FirstOrDefault(entry => entry.Id == id) is not { } one)
            {
                Console.Error.WriteLine($"No connection {arguments[2]}.");
                return 1;
            }

            connections = [one];
        }

        if (connections.Count == 0)
        {
            Console.WriteLine("No connection recorded.");
            return 0;
        }

        var failures = 0;
        foreach (var entry in connections)
        {
            Console.Write($"{entry.Id}\t{entry.Describe()}... ");
            if (!Verify(entry, silent: true))
                failures++;
        }

        return failures == 0 ? 0 : 1;
    }

    // Replacing a password without retyping a server, a database and a login is what keeps a
    // rotated secret from being a chore.
    private static int SetPassword(ConnectionStore store, string[] arguments)
    {
        if (arguments.Length < 3
            || !int.TryParse(arguments[2], out var id)
            || store.Load().FirstOrDefault(entry => entry.Id == id) is not { } existing)
        {
            Console.Error.WriteLine("Usage: configure set-password <id>");
            return 1;
        }

        if (existing.Login is null)
        {
            Console.Error.WriteLine($"Connection {id} uses Windows authentication and has no password.");
            return 1;
        }

        var updated = existing with { Password = AskSecret($"New password for {existing.Describe()}") };

        if (!Verify(updated) && !AskYesNo("Save it anyway?"))
            return 1;

        store.Replace(updated);
        Console.WriteLine($"Password of {id} replaced.");
        return 0;
    }

    private static int SetExportRoot(ConnectionStore store, string[] arguments)
    {
        if (arguments.Length < 3
            || !int.TryParse(arguments[2], out var id)
            || store.Load().FirstOrDefault(entry => entry.Id == id) is not { } existing)
        {
            Console.Error.WriteLine("Usage: configure set-export-root <id>");
            return 1;
        }

        Console.WriteLine($"Exports of {existing.Describe()} go to {DescribeExportRoot(existing)}.");
        store.Replace(existing with { ExportRoot = AskExportRoot(existing) });
        Console.WriteLine($"Export root of {id} saved.");
        return 0;
    }

    // The root is the user's. What keeps the agent from reading an export is the access its client gives
    // it to the files, and the place where the user puts the root: said here, where the place is chosen.
    private static string? AskExportRoot(ConnectionEntry entry)
    {
        var fallback = ExportLocation.DefaultRoot(entry);
        Console.WriteLine("Exported files are written under a folder of this machine. An agent reads them only if its "
                          + "client gives it access to that folder: keep it out of the folders opened to the agent, "
                          + "unless the agent may read what is exported. A folder synced to a cloud, such as OneDrive, "
                          + "sends the exports there.");

        // Asked again rather than refused: the answers typed before, the password among them, are kept.
        while (true)
        {
            var answer = Ask($"Export folder (empty for {fallback})");
            if (answer.Length == 0)
                return null;

            if (Path.IsPathFullyQualified(answer))
                return answer;

            Console.Error.WriteLine("  A full path is needed, such as D:\\Exports\\Facets.");
        }
    }

    private static string DescribeExportRoot(ConnectionEntry entry)
    {
        var root = ExportLocation.BaseRoot(entry);
        var origin = entry.ExportRoot is null ? " (default)" : string.Empty;
        return entry.Database is null ? $"{root}{origin}, one folder per database" : $"{root}{origin}";
    }

    // Failing here, in front of the person who typed the values, beats failing later inside a client
    // that only shows that the server stopped.
    private static bool Verify(ConnectionEntry entry, bool silent = false)
    {
        if (!silent)
            Console.Write("Testing the connection... ");

        try
        {
            using var connection = new SqlConnection(entry.BuildConnectionString());
            connection.Open();
            Console.WriteLine("ok.");

            // Said here first: the person choosing the login is reading, and this is where it is chosen.
            if (LoginCheck.Check(connection) is { } reason)
                Console.WriteLine($"  Warning: {LoginCheck.Warning(reason)}");

            return true;
        }
        catch (SqlException exception)
        {
            Console.WriteLine("failed.");
            Console.Error.WriteLine($"  {exception.Message}");
            return false;
        }
    }

    private static string Ask(string prompt)
    {
        Console.Write($"{prompt}: ");
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    private static bool AskYesNo(string prompt)
    {
        Console.Write($"{prompt} [y/N]: ");
        var answer = (Console.ReadLine() ?? string.Empty).Trim();
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    // Masked, but not blind: one star per character, and the length restated at the end. Pasting a
    // password shows nothing otherwise, so a paste that silently failed is indistinguishable from
    // one that worked — which is exactly how a wrong secret reaches the connection test.
    private static string AskSecret(string prompt)
    {
        Console.Write($"{prompt} (paste with a right click, or Ctrl+V in Windows Terminal): ");

        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (char.IsControl(key.KeyChar))
                continue;

            secret.Append(key.KeyChar);
            Console.Write('*');
        }

        Console.WriteLine($"  ({secret.Length} characters)");
        return secret.ToString();
    }
}

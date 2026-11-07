using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpPyxis.SqlServer.Facets;
using SharpPyxis.SqlServer.Facets.Mcp;

var settings = FacetsSettings.FromEnvironment();
var store = new ConnectionStore(settings.ConfigPath);

var verb = args is [var first, ..] ? first.ToLowerInvariant() : null;

// Credentials are typed here, in the foreground, started by the user. The server never asks.
if (verb == "configure")
    return Configurator.Run(store, args);

// An MCP client always launches this with stdin on a pipe; a person launching it from a console
// does not. That is what tells a server start from someone looking for the command line — and
// 'serve' says it outright, for a configuration that would rather be explicit.
if (verb != "serve" && !Console.IsInputRedirected)
{
    Console.WriteLine("""
        SharpPyxis.SqlServer.Facets — aggregated access to the views a SQL Server database exposes to an agent.

        This is an MCP server: an MCP client starts it and speaks to it over standard input.
        Started from a console, it has nothing to say, so it shows this instead.

          facets-mcp configure   manage the connections it works against
          facets-mcp serve       run the server against this console, for debugging
        """);
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout belongs to the MCP protocol: every log goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var tools = new FacetsTools(settings, store).CreateTools().ToList();

builder.Services
    .AddMcpServer(options => options.ServerInstructions =
        "Aggregated access to the views a SQL Server database owner exposed for an agent, through a dictionary "
        + "that says what each view and column means. No SQL is accepted: a query names a view, dimensions, "
        + "measures and filters from the dictionary. Call use_connection to see the configured targets and to "
        + "choose one; every result names the target it came from.")
    .WithStdioServerTransport()
    .WithTools(tools)
    .WithRequestFilters(filters => filters.AddCallToolFilter(ArgumentCheck.Filter(tools)));

await builder.Build().RunAsync();
return 0;

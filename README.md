# SharpPyxis.SqlServer.Facets

English · [Français](README.fr.md)

Local MCP server that lets an AI agent query a SQL Server database through views it aggregates, and
never through SQL of its own: the agent names a view, dimensions, measures and filters, the server
composes the SQL from a dictionary and runs it under a dedicated login.

In development: no release yet. The database scripts, the engine and the MCP server are here and run
on a demo database; the first trial on a real database is the next step.

## The idea

An agent that analyses data needs the data. The question is how much of it leaves, and under whose
control. Facets answers with three things:

- **views**, written by whoever knows the database, in a schema of their own, `facets`. A view there
  can be queried; anything else does not exist for the server;
- **a dictionary**, `facets_dict`, that says what each view and each column means, and what a column
  is: an amount, a quantity, a rate, a date, a code, a label, an identifier, a boolean or free text.
  That nature decides what the server accepts on the column, such as summing an amount and never a
  rate;
- **a login** that reads the views and the dictionary, writes the dictionary through its procedures,
  and can do nothing else. The rights are the guarantee; the code of the server is the second line.

The agent never supplies a SQL identifier. It names things from the dictionary; the server takes the
identifiers from there and passes the values as parameters.

This server is the counterpart of [SharpPyxis.SqlServer.SchemaMcp](https://github.com/SharpPyxis/SharpPyxis.SqlServer.SchemaMcp),
which reads the structure of a database and never its data. The two are used together: SchemaMcp to
write the views, Facets to query them. They share nothing on the machine: each has its own login
and its own file of connections.

## Where it runs

The MCP server is a program on the workstation of the person who talks to the agent. The MCP client,
the AI application, starts it and speaks to it over its standard input and output. The server opens
no network port; its only connection is the one it makes to the SQL Server instance, with the login
you declared. Nothing is installed on the instance but the two schemas and the login.

## What guarantees what the agent reads

Two protections, and only one of them is the guarantee.

The first is the code of this server: no tool accepts SQL, and every query is composed from the
dictionary, with the identifiers taken from it and the values passed as parameters. A column the
dictionary hides, or marks sensitive, is never read; a column without a nature is never queried.

The second is the rights of the login, and that one holds even if the code had a defect or had been
changed. The login of `Create-FacetsLogin.sql` holds `SELECT` on the schema `facets`, `SELECT` and
`EXECUTE` on the schema `facets_dict`, and nothing else. It reads the tables behind the views through
ownership chaining, with no right on them; it writes the dictionary through its procedures, with no
right on its tables. `Verify-FacetsRights.sql`, run as that login, proves it in six checks.

An administrator login, `sysadmin` or `db_owner`, reads everything: the server says so when such a
login is declared, and again in the first result after it is selected.

## The database

Five scripts, in the `db/` folder, run in this order in the database to expose. Each one is commented
in detail, and says what it needs before it runs.

1. `Create-FacetsDictionary.sql` creates the two schemas, `facets` empty and `facets_dict` with its six
   tables and the nine natures. Run once, as an administrator; it refuses to run twice.
2. `Create-FacetsDictionaryProcedures.sql` creates the seven procedures through which the dictionary
   is written: `refresh_catalog`, which aligns the dictionary on the views of `facets` and quarantines
   what disappeared without deleting anything, `annotate_view`, `annotate_column`, `annotate_value`,
   `write_domain_note`, `ask_question` and `answer_question`. Run again at every version of the
   product.
3. `Create-DemoDatabase.sql`, optional, for trying the server without a database that matters: a small
   shop with rows, three views that cover the nine natures, and the dictionary annotated through the
   procedures, the way an agent would do it.
4. `Create-FacetsLogin.sql` creates the login, in SQLCMD mode, and grants its role the three rights
   above.
5. `Verify-FacetsRights.sql` runs **as that login** and gives six verdicts: it sees the views of
   `facets`, reads a view and no table, reads the dictionary, writes it through the procedures only,
   writes nothing else, and is not an administrator. Run it after a first view exists in `facets`.

Then write your views in the schema `facets`, one per question the agent should answer, with or
without SchemaMcp; run `refresh_catalog`; and annotate them, in SQL or by telling the agent what
each one is.

## What the agent gets

Thirteen tools. Every result names the database it comes from.

| Tool | What it does |
| --- | --- |
| `use_connection` | Lists the declared connections, or selects one. |
| `server_info` | The executable, its version, where the connections are, the commands that manage them. |
| `read_domain_note` | What the owner wrote about the data as a whole, and the general questions with their answers. |
| `list_views` | The views the agent may query, with what each one is and the questions it answers. |
| `describe_view` | The columns of one view: nature, label, description, unit, what the server allows on each, the values of its codes, the questions asked about it. |
| `query` | One aggregated query: a view, dimensions, measures, filters, an order, a top. Rows come tab-separated, amounts in their unit and to the cent, capped by the server. |
| `refresh_catalog` | Aligns the dictionary on the views of `facets`: adds what is new, quarantines what is gone, deletes nothing. |
| `annotate_view`, `annotate_column`, `annotate_value` | Write what a view, a column or a value of a code means. A parameter left out changes nothing. |
| `write_domain_note` | Replaces the note about the data as a whole. |
| `ask_question`, `answer_question` | Record what the dictionary did not say, and the answer when the owner gives it. |

A request names only what `describe_view` showed. Anything else, or anything a nature does not
allow, comes back as a refusal that names every fault at once: the agent corrects the request rather
than retrying it. Every write records who wrote: the MCP client, as it introduced itself to the
server.

### What is not there yet

Exporting a panel of rows to a file on the workstation, and returning rows rather than aggregates to
the model, wait for the first trials on a real database.

## What it costs

To the AI model: a query returns tab-separated rows with a header line, at most the cap of the
server, 200 rows unless `FACETS_MAX_ROWS` says otherwise. The agent cannot raise it. `describe_view`
costs one line per column.

To the SQL Server instance: one query per call, on one view, grouped and filtered as the request
says, under the login you declared. The dictionary is read again at every call, one round trip.

## Installation

No release yet. The executable is built from the source:

```powershell
dotnet publish src/SharpPyxis.SqlServer.Facets.Mcp -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\Facets
```

The result is a single executable, `facets-mcp.exe`, for Windows x64, holding the .NET runtime.
`THIRD-PARTY-NOTICES.md` lists what it contains.

### The connections

Connections are declared in a console, with the executable itself. You type the name of the instance,
the database, the login and the password. The MCP server never asks for them during a conversation,
and the agent never sees them.

```text
facets-mcp configure add                 record a new connection, testing it first
facets-mcp configure list                show what is recorded
facets-mcp configure test [id]           test one recorded connection, or all of them
facets-mcp configure set-password <id>   replace the password of one connection
facets-mcp configure remove <id>         delete one connection
```

Connections are stored in a file encrypted with DPAPI, the encryption mechanism of Windows, for the
current Windows account. This encryption protects the file if it is stolen, or if it is exposed by
accident: copied elsewhere, included in a backup, shown during a screen share. A program running under
the same Windows account, however, can decrypt it, exactly as the MCP server does. This level of
protection matches what the password allows: reading the views the owner chose to expose, and
writing their dictionary.

The file cannot be decrypted from another Windows account, nor on another machine. If you move to a
new machine, the connections have to be declared again.

### The client

The client is the AI application that starts the MCP server. Each client declares its MCP servers in a
configuration file of its own, whose format differs from one client to another, and sometimes from one
version to the next. This README therefore describes it for no client in particular. The declaration
depends on the application, whatever AI model it runs.

The declaration comes down to three pieces of information, the same for every client:

- a name for the MCP server, for example `sqlserver-facets`. The client uses it to designate the
  tools of the server;
- the command that starts the MCP server: the full path of the executable, for example
  `C:\Tools\Facets\facets-mcp.exe`;
- the environment variable `FACETS_CONFIG_PATH`, which tells where the file of connections is. It is
  optional: without it, the file is in the `%APPDATA%` folder of your account, where `configure`
  writes it by default.

The simplest way is to ask the agent of your client to write this declaration. Give it these three
pieces of information, and ask it where its application declares local MCP servers. The agent usually
knows the application it runs in, and it can read its configuration file. What it knows may date from
an earlier version, though, as online documentation may: the check below settles it.

Most clients read their configuration at startup. Restart yours after the declaration, then ask the
agent which version of the MCP server is running. To answer, the agent calls the tool `server_info`,
which returns the path of the executable, its version and the selected connection. If the agent does
not find this tool, the client has not loaded the MCP server.

If you have declared several connections, none is chosen at startup. The agent then asks you which
database to work on, and it only switches to another one when you ask it to.

Four environment variables set up the MCP server:

| Variable | Role |
| --- | --- |
| `FACETS_CONFIG_PATH` | Where the file of connections is. Optional. |
| `FACETS_MAX_ROWS` | The maximum number of rows in an answer, 200 by default. The agent cannot change it. |
| `CONNECTION_STRING` | A single connection, declared without the file. It serves where DPAPI does not exist, outside Windows. The password is then in plain text in the configuration of the client. |
| `FACETS_DESCRIPTION` | A label for that single connection, in plain words. Optional. |

## Tests

```powershell
dotnet test --solution SharpPyxis.SqlServer.Facets.slnx -c Release
```

The integration tests need LocalDB. They create the demo database from the scripts of `db/`, run the
engine and the tools against it, and then drop it. They run under Windows authentication: the rights
of the login are proved by `Verify-FacetsRights.sql`, not by the tests.

## License

MIT. See `LICENSE`.

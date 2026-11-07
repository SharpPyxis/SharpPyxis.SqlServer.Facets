# SharpPyxis.SqlServer.Facets

English · [Français](README.fr.md)

Local MCP server that lets an AI agent query a SQL Server database through views it aggregates, and
never through SQL of its own: the agent names a view, dimensions, measures and filters, the server
composes the SQL from a dictionary and runs it under a dedicated login. The result goes back to the
model, or into a file on the workstation that the model does not read.

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
dictionary, with the identifiers taken from it and the values passed as parameters. A column enters
the dictionary export only: the agent sees its name, never its values, until the designer of the
database opens it. Its values may be written to a file on the workstation, never returned to the
model. A column without a nature is never queried.

The second is the rights of the login, and that one holds even if the code had a defect or had been
changed. The login of `Create-FacetsLogin.sql` holds `SELECT` on the schema `facets`, `SELECT` and
`EXECUTE` on the schema `facets_dict`, and nothing else. It reads the tables behind the views through
ownership chaining, with no right on them; it writes the dictionary through its procedures, with no
right on its tables. `Verify-FacetsRights.sql`, run as that login, proves it in six checks.

The procedures close and never open: through them the agent may hide a view or make a column export
only, never the reverse. Opening a column to queries, or showing a hidden view again, is an update of
the dictionary under the login of the designer, which the login of the server has no right to make.

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
   procedures, the way an agent would do it. Its columns are opened the way a designer would, all
   but an e-mail.
4. `Create-FacetsLogin.sql` creates the login, in SQLCMD mode, and grants its role the three rights
   above.
5. `Verify-FacetsRights.sql` runs **as that login** and gives six verdicts: it sees the views of
   `facets`, reads a view and no table, reads the dictionary, writes it through the procedures only,
   writes nothing else, and is not an administrator. Run it after a first view exists in `facets`.

Then write your views in the schema `facets`, one per question the agent should answer, with or
without SchemaMcp; run `refresh_catalog`; and annotate them, in SQL or by telling the agent what
each one is.

Last, open the columns the agent may query. Every column enters the dictionary export only, and a
column the agent must not see at all has no place in the view. Opening is your gesture, under your
own login:

```sql
update c set is_export_only = 0, modified_at = sysdatetimeoffset(), modified_by = suser_sname()
from facets_dict.columns as c join facets_dict.views as v on v.view_id = c.view_id
where v.view_name = N'sales' and c.column_name <> N'email';
```

You may also set how many rows a query on a view returns to the model. Without a value, the view
takes the default of the server, 200 rows unless `FACETS_MAX_ROWS` says otherwise. A value replaces
that default, above or below it: a view of a few hundred product families can return them all, a view
with one row per customer can return fewer. This is your gesture too, under your own login; the
procedures never touch it.

```sql
update facets_dict.views set max_rows = 500, modified_at = sysdatetimeoffset(), modified_by = suser_sname()
where view_name = N'product_families';
```

This cap bounds how much one answer brings into the conversation. It does not bound what the agent
can read: an agent that splits its request, by period or by sorting both ways, gets around it, at the
cost of more calls. What the agent reads is decided by the views, the export only columns and the
rights of the login.

### Upgrading

Each version only works with its own dictionary: run its upgrade script and replace the executable
at the same time. The scripts stop, and change nothing, on a dictionary that is not at the version
they expect; each runs in one transaction.

- From 0.2.0: run `Upgrade-0.3.0.sql` once, as an administrator. It adds `max_rows` to the views,
  empty, so every view keeps the default of the server. The procedures stay as they are.
- From 0.1.0: run `Upgrade-0.2.0.sql`, then `Create-FacetsDictionaryProcedures.sql`, then
  `Upgrade-0.3.0.sql`. An open column stays open; a column that was sensitive or hidden becomes export
  only.

## What the agent gets

Fifteen tools. Every result names the database it comes from.

| Tool | What it does |
| --- | --- |
| `use_connection` | Lists the declared connections, or selects one. |
| `server_info` | The executable, its version, where the connections are and where the files go, the commands that manage them. |
| `read_domain_note` | What the owner wrote about the data as a whole, and the general questions with their answers. |
| `list_views` | The views the agent may query, with what each one is and the questions it answers. |
| `describe_view` | The columns of one view: nature, label, description, unit, what the server allows on each, the values of its codes, the most rows a query on it returns, the questions asked about it. |
| `query` | One aggregated query: a view, dimensions, measures, filters, an order, a top. Rows come tab-separated, amounts in their unit and to the cent, capped by the view. |
| `use_export_folder` | Chooses the folder where files are written, under the root of the connection, or lists the folders already there. |
| `query_to_file` | The same request as `query`, written whole to a CSV file on the workstation. The model gets the path, the number of rows and a description of the file, never the rows. |
| `refresh_catalog` | Aligns the dictionary on the views of `facets`: adds what is new, quarantines what is gone, deletes nothing. |
| `annotate_view`, `annotate_column`, `annotate_value` | Write what a view, a column or a value of a code means. A parameter left out changes nothing. They can hide a view or make a column export only, never the reverse. |
| `write_domain_note` | Replaces the note about the data as a whole. |
| `ask_question`, `answer_question` | Record what the dictionary did not say, and the answer when the owner gives it. |

A request names only what `describe_view` showed. Anything else, or anything a nature does not
allow, comes back as a refusal that names every fault at once: the agent corrects the request rather
than retrying it. Every write records who wrote: the MCP client, as it introduced itself to the
server.

The filters are equals, in, between, less, less or equal, greater, greater or equal, contains, empty
and not empty, each where the nature of the column allows it. Not empty is the only negative
operator.

## Files on the workstation

`query_to_file` takes the same request as `query` and writes the whole result to a file, with no cap
on the rows. It serves for an analysis the user wants to keep or share, for a list to import into
another tool, or for a result that `query` cut at the cap of the view. An export only column may be
written to the file, as a dimension or a measure. As a filter, it accepts empty and not empty only:
the number of rows returned would otherwise tell whether a given value, such as an e-mail address, is
in the data.

### Where the files go

Each connection has an export root, declared with `configure add` or `configure set-export-root`.
Without a declared root, files go to `%USERPROFILE%\Facets\<label of the connection>`, or the name of
the server when the connection has no label. For a connection that names a server only, each database
has its own folder under the root.

The default root is outside `Documents` on purpose. Windows offers to synchronise `Documents` with
OneDrive, and managed workstations often do: exported data would then be sent to a cloud storage
without anyone having chosen it. If you choose a synchronised folder yourself, the files go there too.

Under the root, the agent chooses a working folder with `use_export_folder`, named after the subject
in a few words. It keeps that folder from one export to the next, and starts without one when you
switch to another connection; without a folder, files go to the root. The agent cannot write
anywhere else: its names lose every character Windows refuses in a file name, and a name made of dots
is refused. Files are never deleted by the server: tidying the folders is up to you.

### What the model sees, and what it can read

The model receives the path of the file, the number of rows, and a description of the file. It never
receives the rows. The server chooses where it writes; whether the agent can open the file afterwards
depends on the access its client gives it to the folders of the machine. Some clients only open the
folders you entrust to them, others ask for your approval, some can be given full access. Keep the
export root out of the folders opened to the agent, unless the agent may read what is exported.

### The file

Each file is named `yyyy-MM-dd_HH-mm_<label>.csv`, the label being the few words the agent gave. A
name already taken receives `-2`, `-3`: a file is never overwritten. Beside it, a `.json` file with
the same name describes it: the view, the columns with their label, the filters, the number of rows,
the date, the connection and the MCP client. It is the description the model received, so you can
tell what a file holds without opening it.

The CSV is meant to open in Excel as it is, in French as in English:

- columns separated by `;`, text in UTF-8 with a byte order mark, so that accents are read correctly;
- the names of the result columns as the first line;
- a value that holds `;`, a quote or a line break between quotes, and kept whole;
- dates as `2026-01-31`, and `2026-01-31 14:30:00` with a time, which Excel reads as dates;
- booleans as `1` or `0`, which Excel counts and filters as numbers;
- decimal numbers with a point. A French Excel reads them as text; replacing the point with a comma
  in the column turns them into numbers.

A text that starts with `=`, `+`, `-` or `@` is read by Excel as a formula. The file keeps the values
as the database holds them, so a value typed into the database in that form, such as a phone number
written `+33…`, is computed by Excel when the file is opened.

Rows are written as they are read from the database, so their number is bounded by the disk only. The
file is written under a temporary name and appears under its own once complete. A query may run ten
minutes. A client that asks for progress notifications receives one every 5,000 rows; whether it
shows them depends on the client. A result without rows writes no file.

## What it costs

To the AI model: a query returns tab-separated rows with a header line, at most the cap of the view,
which `describe_view` gives; the agent cannot raise it. An export returns its description only, a few
lines per column. `describe_view` costs one line per column.

To the SQL Server instance: one query per call, on one view, grouped and filtered as the request
says, under the login you declared. The dictionary is read again at every call, one round trip. An
export reads the whole result, which on a large view takes as long as the same query run directly.

## Installation

The MCP server is a single executable, `facets-mcp.exe`, of about 84 MB, for Windows x64. It contains
the .NET runtime: nothing else needs to be installed on the machine. `THIRD-PARTY-NOTICES.md` lists
what it contains.

### From a release

Each release of this repository publishes the executable, a file holding its SHA-256 fingerprint,
and the notices of the .NET runtime it contains. The following block, run in PowerShell, downloads
the executable of the latest release into `C:\Tools\Facets`, then checks its fingerprint. Change the
folder if you prefer another one.

```powershell
$folder = 'C:\Tools\Facets'
$release = 'https://github.com/SharpPyxis/SharpPyxis.SqlServer.Facets/releases/latest/download'
New-Item -ItemType Directory -Force $folder | Out-Null
Invoke-WebRequest "$release/facets-mcp.exe" -OutFile "$folder\facets-mcp.exe"
Invoke-WebRequest "$release/facets-mcp.exe.sha256" -OutFile "$folder\facets-mcp.exe.sha256"
$expected = (Get-Content "$folder\facets-mcp.exe.sha256").Split(' ')[0]
$actual = (Get-FileHash "$folder\facets-mcp.exe" -Algorithm SHA256).Hash
if ($actual -ne $expected) { Remove-Item "$folder\facets-mcp.exe"; throw 'Fingerprint mismatch: the executable was removed.' }
"Fingerprint verified: $actual"
```

The executable and its fingerprint come from the same release. So the check proves that the download
is complete and intact. It does not prove who built the file.

The executable is not signed yet. On a machine where Smart App Control is on, Windows may block it.

### From the source

The executable is built with the following command:

```powershell
dotnet publish src/SharpPyxis.SqlServer.Facets.Mcp -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\Facets
```

The result is the same single executable as the one published in the releases.

### The connections

Connections are declared in a console, with the executable itself. You type the name of the instance,
the database, the login and the password. The MCP server never asks for them during a conversation,
and the agent never sees them.

```text
facets-mcp configure add                    record a new connection, testing it first
facets-mcp configure list                   show what is recorded
facets-mcp configure test [id]              test one recorded connection, or all of them
facets-mcp configure set-password <id>      replace the password of one connection
facets-mcp configure set-export-root <id>   choose the folder the exports of one connection go to
facets-mcp configure remove <id>            delete one connection
```

`configure add` also asks for the export root of the connection. An empty answer keeps the default,
which it shows. A connection recorded before version 0.3.0 has the default.

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
| `FACETS_MAX_ROWS` | The maximum number of rows in an answer, 200 by default, for a view that sets no cap of its own. The agent cannot change it. |
| `CONNECTION_STRING` | A single connection, declared without the file. It serves where DPAPI does not exist, outside Windows. The password is then in plain text in the configuration of the client, and its files go to the default root. |
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

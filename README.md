# SharpPyxis.SqlServer.Facets

English · [Français](README.fr.md)

Local MCP server that lets an AI agent query a SQL Server database through views it aggregates, and
never through SQL of its own: the agent names a view, dimensions, measures and filters, the server
composes the SQL from a dictionary and runs it under a dedicated login.

In development. Nothing is released yet: `db/` holds what the server will read, and it can be set up
and tried on its own.

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

## The database

Four scripts, in the `db/` folder, run in this order in the database to expose. Each one is commented
in detail, and says what it needs before it runs.

1. `Create-FacetsDictionary.sql` creates the two schemas, `facets` empty and `facets_dict` with its six
   tables and the nine natures. Run once, as an administrator; it refuses to run twice.
2. `Create-FacetsDictionaryProcedures.sql` creates the seven procedures through which the dictionary
   is written: `refresh_catalog`, which aligns the dictionary on the views of `facets` and quarantines
   what disappeared without deleting anything, `annotate_view`, `annotate_column`, `annotate_value`,
   `write_domain_note`, `ask_question` and `answer_question`. Run again at every version of the
   product.
3. `Create-FacetsLogin.sql` creates the login, in SQLCMD mode, and grants its role three rights:
   `SELECT` on `facets`, `SELECT` and `EXECUTE` on `facets_dict`. Both schemas belong to `dbo`, so the
   login reads the tables behind the views and writes the tables of the dictionary without any right
   on them.
4. `Verify-FacetsRights.sql` runs **as that login** and gives six verdicts: it sees the views of
   `facets`, reads a view and no table, reads the dictionary, writes it through the procedures only,
   writes nothing else, and is not an administrator. Run it after a first view exists in `facets`.

`Create-DemoDatabase.sql` is a fifth script, for trying the server without a database that matters: a
small shop with rows, three views that cover the nine natures, and the dictionary annotated through the
procedures, the way an agent would do it. It runs after the first two scripts, before the login.

## License

MIT. See `LICENSE`.

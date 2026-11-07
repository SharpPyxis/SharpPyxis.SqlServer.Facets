# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The result of a query can go into a file on the workstation, whole, and each view sets how many rows a
query on it returns to the model.

### Added

- `query_to_file` runs the same request as `query` and writes the whole result to a CSV file on the
  workstation, with no cap on the rows, and a `.json` description beside it. The model receives the
  path, the number of rows and the description, never the rows. Export only columns are accepted as
  dimensions and measures; as a filter, they accept empty and not empty only. The CSV opens in Excel as
  it is: separated by `;`, UTF-8 with a byte order mark, dates in ISO, booleans as 1 or 0, decimals
  with a point. A client that asks for progress notifications receives one every 5,000 rows.
- `use_export_folder` chooses the working folder of the files under the export root of the connection,
  or lists the folders already there.
- Each connection has an export root, asked by `configure add` and changed by the new
  `configure set-export-root`. The default is `%USERPROFILE%\Facets\<label or server>`, outside
  `Documents`, which Windows often synchronises with OneDrive.
- `max_rows` on a view of the dictionary: how many rows a query on it returns to the model, above or
  below the default of the server. The designer sets it under their own login. `describe_view` shows
  it, and a result cut at the cap says that the owner of the dictionary sets it.
- The filter operator `not empty`, wherever `empty` is allowed.

### Changed

- `FACETS_MAX_ROWS` is the default cap of a view that sets none.
- A result cut by the `top` of the request says that the view returns more, up to its cap.

### Upgrading from 0.2.0

Run `Upgrade-0.3.0.sql` once, as an administrator, and replace the executable at the same time. Every
view keeps the default cap until the designer sets one. The procedures of 0.2.0 stay as they are.

## [0.2.0] - 2026-11-07

A column enters the dictionary closed, and only the designer of the database opens it.

### Changed

- A column of a view carries one flag, `is_export_only`, instead of `is_exposed_to_model` and
  `is_sensitive`. An export only column is shown by `describe_view` by its name, with "export only"
  where the others say what they allow, and none of its values; `query` refuses it as a dimension, a
  measure or a filter, and says why. A column the agent must not see at all has no place in the view.
- A column enters the dictionary export only. The designer opens it to queries with an update of
  `facets_dict.columns`, under their own login; the README shows how.
- `annotate_column` takes `exportOnly`, which only closes, and `annotate_view` takes `hide`. The
  procedures refuse the reverse with error 50002, which the agent receives in words.

### Security

- In 0.1.0 the agent could open a hidden or sensitive column, or show a hidden view, through
  `annotate_column` and `annotate_view`. The procedures now close and never open; opening is an update
  of the table, which the login of the server has no right to make.

### Upgrading from 0.1.0

Run `Upgrade-0.2.0.sql` once, as an administrator, then `Create-FacetsDictionaryProcedures.sql`, and
replace the executable at the same time. An open column stays open; a column that was sensitive or
hidden becomes export only.

## [0.1.0] - 2026-11-07

The first release. The agent queries the views of a schema `facets` by aggregating them, and never
writes SQL: it names a view, dimensions, measures and filters, and the server composes the query from
a dictionary.

### Added

- Five scripts in `db/`: the dictionary `facets_dict` and its nine natures, the seven procedures that
  write it, a demo database, the login of the server, and the check of its rights, run as that login.
- The reading tools: `read_domain_note`, `list_views`, `describe_view`, and `query`, which groups,
  folds, filters and orders one view, and returns tab-separated rows capped by the server. Amounts come
  in their unit and to the cent; a week is the ISO week, with its ISO year. A request the server
  refuses names every fault at once.
- The documenting tools, one per procedure: `refresh_catalog`, `annotate_view`, `annotate_column`,
  `annotate_value`, `write_domain_note`, `ask_question`, `answer_question`. Every write records the MCP
  client as its author.
- `use_connection` and `server_info`, and the `configure` command that records the connections in a
  file encrypted for the Windows account. Every result names the database it comes from, and the
  server warns when its login is an administrator.
- A call sent in the same batch as `use_connection` waits for the choice; with no choice under way, it
  answers at once that no database is chosen yet.

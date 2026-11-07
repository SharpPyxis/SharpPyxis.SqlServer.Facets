# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

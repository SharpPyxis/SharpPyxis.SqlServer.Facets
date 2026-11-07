# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

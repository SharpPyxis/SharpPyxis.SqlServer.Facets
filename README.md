# SharpPyxis.SqlServer.Facets

English · [Français](README.fr.md)

Local MCP server that lets an AI agent query a SQL Server database through views it aggregates, and
never through SQL of its own: the agent names a view, dimensions, measures and filters, the server
composes the SQL from a dictionary and runs it under a dedicated login.

In development. Nothing is released yet: `db/` holds the dictionary the server will read.

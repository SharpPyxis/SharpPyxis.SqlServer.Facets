# SharpPyxis.SqlServer.Facets

[English](README.md) · Français

Serveur MCP local qui permet à un agent IA d'interroger une base SQL Server à travers des vues qu'il
agrège, jamais par du SQL à lui : l'agent nomme une vue, des dimensions, des mesures et des filtres,
le serveur compose le SQL depuis un dictionnaire et l'exécute sous un login dédié.

En cours de développement. Rien n'est publié : `db/` porte le dictionnaire que le serveur lira.

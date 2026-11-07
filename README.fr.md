# SharpPyxis.SqlServer.Facets

[English](README.md) · Français

Serveur MCP local qui permet à un agent IA d'interroger une base SQL Server à travers des vues qu'il
agrège, jamais par du SQL à lui : l'agent nomme une vue, des dimensions, des mesures et des filtres,
le serveur compose le SQL depuis un dictionnaire et l'exécute sous un login dédié.

En cours de développement. Rien n'est publié : `db/` porte ce que le serveur lira, et s'installe et
s'essaie seul.

## L'idée

Un agent qui analyse des données a besoin des données. La question est ce qui en sort, et sous le
contrôle de qui. Facets répond par trois choses :

- **des vues**, écrites par qui connaît la base, dans un schéma à elles, `facets`. Une vue qui s'y
  trouve s'interroge ; tout le reste n'existe pas pour le serveur ;
- **un dictionnaire**, `facets_dict`, qui dit ce que chaque vue et chaque colonne signifie, et ce
  qu'une colonne est : un montant, une quantité, un taux, une date, un code, un libellé, un
  identifiant, un booléen ou du texte libre. Cette nature décide de ce que le serveur accepte sur la
  colonne, par exemple sommer un montant et jamais un taux ;
- **un login** qui lit les vues et le dictionnaire, écrit le dictionnaire par ses procédures, et ne
  peut rien d'autre. Les droits font la garantie ; le code du serveur est la seconde ligne.

L'agent ne fournit jamais un identifiant SQL. Il nomme des choses du dictionnaire ; le serveur y prend
les identifiants et passe les valeurs en paramètres.

## La base

Quatre scripts, dans le dossier `db/`, à passer dans cet ordre dans la base à exposer. Chacun est
commenté en détail et dit ce qu'il attend avant de tourner.

1. `Create-FacetsDictionary.sql` crée les deux schémas, `facets` vide et `facets_dict` avec ses six
   tables et les neuf natures. Une fois, en administrateur ; il refuse de tourner deux fois.
2. `Create-FacetsDictionaryProcedures.sql` crée les sept procédures par lesquelles le dictionnaire
   s'écrit : `refresh_catalog`, qui aligne le dictionnaire sur les vues de `facets` et met en
   quarantaine ce qui a disparu sans rien supprimer, `annotate_view`, `annotate_column`,
   `annotate_value`, `write_domain_note`, `ask_question` et `answer_question`. À repasser à chaque
   version du produit.
3. `Create-FacetsLogin.sql` crée le login, en mode SQLCMD, et accorde à son rôle trois droits :
   `SELECT` sur `facets`, `SELECT` et `EXECUTE` sur `facets_dict`. Les deux schémas appartiennent à
   `dbo` : le login lit les tables derrière les vues et écrit celles du dictionnaire sans aucun droit
   dessus.
4. `Verify-FacetsRights.sql` tourne **sous ce login** et rend six verdicts : il voit les vues de
   `facets`, lit une vue et aucune table, lit le dictionnaire, ne l'écrit que par les procédures,
   n'écrit rien d'autre, et n'est pas administrateur. À passer après une première vue dans `facets`.

`Create-DemoDatabase.sql` est un cinquième script, pour essayer le serveur sans une base qui compte :
une petite boutique avec des lignes, trois vues qui couvrent les neuf natures, et le dictionnaire
annoté par les procédures, comme un agent le ferait. Il passe après les deux premiers scripts, avant
le login.

## Licence

MIT. Voir `LICENSE`.

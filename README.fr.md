# SharpPyxis.SqlServer.Facets

[English](README.md) · Français

Serveur MCP local qui permet à un agent IA d'interroger une base SQL Server à travers des vues qu'il
agrège, jamais par du SQL à lui : l'agent nomme une vue, des dimensions, des mesures et des filtres,
le serveur compose le SQL depuis un dictionnaire et l'exécute sous un login dédié.

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

Ce serveur est le pendant de [SharpPyxis.SqlServer.SchemaMcp](https://github.com/SharpPyxis/SharpPyxis.SqlServer.SchemaMcp),
qui lit la structure d'une base et jamais ses données. Les deux s'emploient ensemble : SchemaMcp pour
écrire les vues, Facets pour les interroger. Ils ne partagent rien sur le poste : chacun a son login
et son fichier de connexions.

## Où ça tourne

Le serveur MCP est un programme sur le poste de la personne qui dialogue avec l'agent. Le client MCP,
l'application d'IA, le lance et lui parle par son entrée et sa sortie standard. Le serveur n'ouvre
aucun port réseau ; sa seule connexion est celle qu'il établit vers l'instance SQL Server, avec le
login que vous avez déclaré. Rien n'est installé sur l'instance en dehors des deux schémas et du login.

## Ce qui garantit ce que l'agent lit

Deux protections, et une seule fait la garantie.

La première est le code de ce serveur : aucun outil n'accepte de SQL, et chaque requête est composée
depuis le dictionnaire, les identifiants pris dedans et les valeurs passées en paramètres. Une colonne
entre au dictionnaire en export seulement : l'agent voit son nom, jamais ses valeurs, tant que le
concepteur de la base ne l'a pas ouverte. Une colonne sans nature n'est jamais interrogée.

La seconde est faite des droits du login, et celle-là tient même si le code avait un défaut ou avait
été modifié. Le login de `Create-FacetsLogin.sql` détient `SELECT` sur le schéma `facets`, `SELECT` et
`EXECUTE` sur le schéma `facets_dict`, et rien d'autre. Il lit les tables derrière les vues par la
chaîne de propriétaires, sans droit dessus ; il écrit le dictionnaire par ses procédures, sans droit
sur ses tables. `Verify-FacetsRights.sql`, passé sous ce login, le prouve en six contrôles.

Les procédures ferment et n'ouvrent jamais : par elles, l'agent peut cacher une vue ou passer une
colonne en export seulement, jamais l'inverse. Ouvrir une colonne aux requêtes, ou montrer de nouveau
une vue cachée, est une mise à jour du dictionnaire sous le login du concepteur, que le login du
serveur n'a pas le droit de faire.

Un login administrateur, `sysadmin` ou `db_owner`, lit tout : le serveur le dit quand un tel login est
déclaré, et encore dans le premier résultat après sa sélection.

## La base

Cinq scripts, dans le dossier `db/`, à passer dans cet ordre dans la base à exposer. Chacun est
commenté en détail et dit ce qu'il attend avant de tourner.

1. `Create-FacetsDictionary.sql` crée les deux schémas, `facets` vide et `facets_dict` avec ses six
   tables et les neuf natures. Une fois, en administrateur ; il refuse de tourner deux fois.
2. `Create-FacetsDictionaryProcedures.sql` crée les sept procédures par lesquelles le dictionnaire
   s'écrit : `refresh_catalog`, qui aligne le dictionnaire sur les vues de `facets` et met en
   quarantaine ce qui a disparu sans rien supprimer, `annotate_view`, `annotate_column`,
   `annotate_value`, `write_domain_note`, `ask_question` et `answer_question`. À repasser à chaque
   version du produit.
3. `Create-DemoDatabase.sql`, facultatif, pour essayer le serveur sans une base qui compte : une
   petite boutique avec des lignes, trois vues qui couvrent les neuf natures, et le dictionnaire
   annoté par les procédures, comme un agent le ferait. Ses colonnes sont ouvertes comme un
   concepteur le ferait, toutes sauf un e-mail.
4. `Create-FacetsLogin.sql` crée le login, en mode SQLCMD, et accorde à son rôle les trois droits
   ci-dessus.
5. `Verify-FacetsRights.sql` tourne **sous ce login** et rend six verdicts : il voit les vues de
   `facets`, lit une vue et aucune table, lit le dictionnaire, ne l'écrit que par les procédures,
   n'écrit rien d'autre, et n'est pas administrateur. À passer après une première vue dans `facets`.

Écrivez ensuite vos vues dans le schéma `facets`, une par question que l'agent doit savoir traiter,
avec ou sans SchemaMcp ; passez `refresh_catalog` ; et annotez-les, en SQL ou en disant à l'agent ce
que chacune est.

Enfin, ouvrez les colonnes que l'agent peut interroger. Chaque colonne entre au dictionnaire en export
seulement, et une colonne que l'agent ne doit pas voir du tout n'a pas sa place dans la vue. Ouvrir
est votre geste, sous votre propre login :

```sql
update c set is_export_only = 0, modified_at = sysdatetimeoffset(), modified_by = suser_sname()
from facets_dict.columns as c join facets_dict.views as v on v.view_id = c.view_id
where v.view_name = N'sales' and c.column_name <> N'email';
```

### Mettre à jour depuis la 0.1.0

Passez `Upgrade-0.2.0.sql` une fois, en administrateur, puis `Create-FacetsDictionaryProcedures.sql`,
et remplacez l'exécutable en même temps : chaque version ne fonctionne qu'avec son dictionnaire. Une
colonne ouverte le reste ; une colonne sensible ou cachée passe en export seulement.

## Ce que l'agent obtient

Treize outils. Chaque résultat nomme la base dont il vient.

| Outil | Ce qu'il fait |
| --- | --- |
| `use_connection` | Liste les connexions déclarées, ou en sélectionne une. |
| `server_info` | L'exécutable, sa version, où sont les connexions, les commandes qui les gèrent. |
| `read_domain_note` | Ce que le propriétaire a écrit sur les données dans leur ensemble, et les questions générales avec leurs réponses. |
| `list_views` | Les vues que l'agent peut interroger, avec ce que chacune est et les questions auxquelles elle répond. |
| `describe_view` | Les colonnes d'une vue : nature, libellé, description, unité, ce que le serveur permet sur chacune, les valeurs de ses codes, les questions posées dessus. |
| `query` | Une requête agrégée : une vue, des dimensions, des mesures, des filtres, un tri, un top. Les lignes viennent séparées par des tabulations, les montants dans leur unité et au centime, plafonnées par le serveur. |
| `refresh_catalog` | Aligne le dictionnaire sur les vues de `facets` : ajoute ce qui est nouveau, met en quarantaine ce qui a disparu, ne supprime rien. |
| `annotate_view`, `annotate_column`, `annotate_value` | Écrivent ce qu'une vue, une colonne ou une valeur d'un code signifie. Un paramètre omis ne change rien. Elles peuvent cacher une vue ou passer une colonne en export seulement, jamais l'inverse. |
| `write_domain_note` | Remplace la note sur les données dans leur ensemble. |
| `ask_question`, `answer_question` | Enregistrent ce que le dictionnaire ne disait pas, et la réponse quand le propriétaire la donne. |

Une requête ne nomme que ce que `describe_view` a montré. Tout le reste, ou tout ce qu'une nature
n'autorise pas, revient comme un refus qui nomme toutes les fautes d'un coup : l'agent corrige la
requête au lieu de la réessayer. Chaque écriture enregistre qui a écrit : le client MCP, tel qu'il
s'est présenté au serveur.

### Ce qui n'y est pas encore

L'export d'un panel de lignes vers un fichier du poste, et le retour de lignes plutôt que d'agrégats
au modèle, viendront dans une version ultérieure.

## Ce que ça coûte

Au modèle d'IA : une requête rend des lignes séparées par des tabulations avec une ligne d'en-tête,
au plus le plafond du serveur, 200 lignes sauf si `FACETS_MAX_ROWS` en décide autrement. L'agent ne
peut pas le relever. `describe_view` coûte une ligne par colonne.

À l'instance SQL Server : une requête par appel, sur une vue, groupée et filtrée comme la demande le
dit, sous le login que vous avez déclaré. Le dictionnaire est relu à chaque appel, un aller-retour.

## Installation

Le serveur MCP est un exécutable unique, `facets-mcp.exe`, d'environ 84 Mo, pour Windows x64. Il
contient le runtime .NET : rien d'autre n'est à installer sur la machine. `THIRD-PARTY-NOTICES.md`
liste ce qu'il contient.

### Depuis une release

Chaque release de ce dépôt publie l'exécutable, un fichier contenant son empreinte SHA-256, et l'avis
du runtime .NET qu'il contient. Le bloc suivant, exécuté dans PowerShell, télécharge l'exécutable de
la dernière release dans `C:\Tools\Facets`, puis vérifie son empreinte. Changez le dossier si vous en
préférez un autre.

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

L'exécutable et son empreinte viennent de la même release. La vérification prouve donc que le
téléchargement est complet et intact. Elle ne prouve pas qui a construit le fichier.

L'exécutable n'est pas encore signé. Sur une machine où le Contrôle intelligent des applications
(*Smart App Control*) est activé, Windows peut le bloquer.

### Depuis les sources

L'exécutable se construit avec la commande suivante :

```powershell
dotnet publish src/SharpPyxis.SqlServer.Facets.Mcp -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Tools\Facets
```

Le résultat est le même exécutable unique que celui publié dans les releases.

### Les connexions

Les connexions se déclarent dans une console, avec l'exécutable lui-même. Vous y tapez le nom de
l'instance, la base, le login et le mot de passe. Le serveur MCP ne les demande jamais pendant une
conversation, et l'agent ne les voit jamais.

```text
facets-mcp configure add                 record a new connection, testing it first
facets-mcp configure list                show what is recorded
facets-mcp configure test [id]           test one recorded connection, or all of them
facets-mcp configure set-password <id>   replace the password of one connection
facets-mcp configure remove <id>         delete one connection
```

Les connexions sont rangées dans un fichier chiffré par DPAPI, le mécanisme de chiffrement de
Windows, pour le compte Windows courant. Ce chiffrement protège le fichier s'il est volé, ou exposé
par accident : copié ailleurs, pris dans une sauvegarde, montré pendant un partage d'écran. Un
programme qui tourne sous le même compte Windows peut en revanche le déchiffrer, exactement comme le
serveur MCP le fait. Ce niveau de protection est proportionné à ce que le mot de passe permet : lire
les vues que le propriétaire a choisi d'exposer, et écrire leur dictionnaire.

Le fichier ne se déchiffre ni depuis un autre compte Windows, ni sur une autre machine. Si vous
changez de poste, les connexions sont à redéclarer.

### Le client

Le client est l'application d'IA qui lance le serveur MCP. Chaque client déclare ses serveurs MCP dans
un fichier de configuration à lui, dont le format diffère d'un client à l'autre, et parfois d'une
version à la suivante. Ce README le décrit donc pour aucun client en particulier. La déclaration
dépend de l'application, quel que soit le modèle d'IA qu'elle fait tourner.

La déclaration tient en trois informations, les mêmes pour tous les clients :

- un nom pour le serveur MCP, par exemple `sqlserver-facets`. Le client s'en sert pour désigner les
  outils du serveur ;
- la commande qui lance le serveur MCP : le chemin complet de l'exécutable, par exemple
  `C:\Tools\Facets\facets-mcp.exe` ;
- la variable d'environnement `FACETS_CONFIG_PATH`, qui dit où est le fichier de connexions. Elle est
  facultative : sans elle, le fichier est dans le dossier `%APPDATA%` de votre compte, où `configure`
  l'écrit par défaut.

Le plus simple est de demander à l'agent de votre client d'écrire cette déclaration. Donnez-lui ces
trois informations, et demandez-lui où son application déclare les serveurs MCP locaux. L'agent
connaît généralement l'application dans laquelle il tourne, et il sait lire son fichier de
configuration. Ce qu'il sait peut dater d'une version antérieure, comme la documentation en ligne : le
contrôle ci-dessous tranche.

La plupart des clients lisent leur configuration au démarrage. Relancez le vôtre après la déclaration,
puis demandez à l'agent quelle version du serveur MCP tourne. Pour répondre, l'agent appelle l'outil
`server_info`, qui rend le chemin de l'exécutable, sa version et la connexion sélectionnée. Si l'agent
ne trouve pas cet outil, le client n'a pas chargé le serveur MCP.

Si vous avez déclaré plusieurs connexions, aucune n'est choisie au démarrage. L'agent vous demande
alors sur quelle base travailler, et il n'en change que lorsque vous le lui demandez.

Quatre variables d'environnement règlent le serveur MCP :

| Variable | Rôle |
| --- | --- |
| `FACETS_CONFIG_PATH` | Où est le fichier de connexions. Facultative. |
| `FACETS_MAX_ROWS` | Le nombre maximal de lignes d'une réponse, 200 par défaut. L'agent ne peut pas le changer. |
| `CONNECTION_STRING` | Une connexion unique, déclarée sans le fichier. Elle sert là où DPAPI n'existe pas, hors de Windows. Le mot de passe est alors en clair dans la configuration du client. |
| `FACETS_DESCRIPTION` | Un libellé pour cette connexion unique, en mots. Facultative. |

## Tests

```powershell
dotnet test --solution SharpPyxis.SqlServer.Facets.slnx -c Release
```

Les tests d'intégration ont besoin de LocalDB. Ils créent la base de démonstration depuis les scripts
de `db/`, font tourner le moteur et les outils dessus, puis la suppriment. Ils tournent en
authentification Windows : les droits du login sont prouvés par `Verify-FacetsRights.sql`, pas par
les tests.

## Licence

MIT. Voir `LICENSE`.

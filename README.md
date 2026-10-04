# MCP AutoCAD Map 3D

Connecteur [MCP](https://modelcontextprotocol.io) qui permet à un assistant IA de lire et de modifier le dessin
ouvert dans **AutoCAD, AutoCAD Map 3D ou Civil 3D** (versions 2026 et 2027, voir [Compatibilité](#compatibilité)) :
dessin 2D, modélisation 3D, cotation, présentations et PDF, systèmes de coordonnées, connexions FDO, import-export
de fichiers SIG (SHP, MapInfo, GML…), données d'objet Map 3D et import du cadastre français (IGN, BAN).

Le serveur suit le protocole MCP standard (transport stdio). Il fonctionne avec Claude Desktop et Claude Code, et
avec les autres clients MCP : Cursor, Antigravity, Hermes Agent, VS Code, Codex CLI, Gemini CLI… (voir
[Autres clients MCP](#autres-clients-mcp)).

```
Client MCP (Claude, Cursor, Antigravity, Hermes…) ──stdio──> McpMap3D.Server.exe ──named pipe──> plug-in dans AutoCAD
```

Exemples de demandes à l'assistant :

- « Importe le cadastre autour du 12 rue de la Paix à Lyon, avec les bâtiments en 3D. »
- « Dessine une boîte de 2 × 1 × 0,5 m, perce-la d'un cylindre de 20 cm de diamètre, puis donne son volume. »
- « Crée une présentation A3 avec une fenêtre au 1:500 sur les parcelles, et exporte-la en PDF. »

> *English summary: MCP server and AutoCAD plug-in that let an AI assistant (Claude, Cursor, Antigravity, Hermes or
> any MCP client) read and edit the drawing open in AutoCAD, AutoCAD Map 3D or Civil 3D 2026/2027 (160 tools: 2D
> drafting, 3D solids, STEP/IGES import, materials and lights, FDO connections, GIS file import/export (SHP, MapInfo, GML), dimensions, layouts and PDF, coordinate systems,
> external references, Map 3D object data, French cadastre import). Documentation is in French.*

**État du projet** : projet personnel, version 0.28.0, 160 outils. Il n'y a pas de version compilée à télécharger :
l'installation compile les sources.

> **Attention** : l'assistant modifie réellement le dessin. Travaillez sur une copie, et gardez en tête que chaque
> appel d'outil s'annule en une étape avec U (voir [Annulation](#annulation)).

Sommaire : [Compatibilité](#compatibilité) · [Prérequis](#prérequis) · [Installer](#installer) ·
[Enregistrer dans un client MCP](#enregistrer-dans-un-client-mcp) · [Vérifier](#vérifier) · [Outils MCP](#outils-mcp) ·
[Protocole du pipe](#protocole-du-pipe) · [Sécurité et données](#sécurité-et-données) · [Structure](#structure) ·
[Licence](#licence)

Spécification pour les contributeurs (et pour Claude Code) : [CLAUDE.md](CLAUDE.md).

## Compatibilité

| Version | Release | .NET | État |
| --- | --- | --- | --- |
| 2025 | R25.0 | .NET 8 | Pas encore prise en charge : le bundle ne se charge qu'à partir de R25.1, et l'installation ne compile pas pour 2025 |
| 2026 | R25.1 | .NET 8 | Prise en charge, testée dans Civil 3D 2026 |
| 2027 | R26.0 | .NET 10 | Prise en charge à la compilation, pas encore testée |

Les DLL ne sont pas compatibles d'une version à l'autre : `install.ps1` en compile une par version installée.

| Produit | Outils disponibles |
| --- | --- |
| AutoCAD Map 3D, ou Civil 3D qui l'inclut | Tous. Les tests se font en partie dans Civil 3D et en partie dans Map 3D. |
| AutoCAD (sans Map 3D) | Dessin, modélisation, présentations, impression, rendu, Xref. Le cadastre s'importe en Lambert-93, avec ses attributs en XData ; `get_coordinate_system`, `search_coordinate_systems`, `list_od_tables` et `get_od_records` fonctionnent en mode réduit. `set_coordinate_system`, `set_od_value` et les outils FDO demandent Map 3D. Pas testé régulièrement. |

La compilation a besoin des DLL de Map 3D (dossier `Map` d'AutoCAD). Pour AutoCAD seul, compilez le plug-in sur un
poste équipé de Map 3D ou de Civil 3D de même version, puis copiez le bundle installé.

## Prérequis

- AutoCAD, AutoCAD Map 3D ou Civil 3D 2026 ou 2027, installé dans `C:\Program Files\Autodesk\AutoCAD 2026\`
  ou `AutoCAD 2027\` (sinon : `-p:AcadDir=...`)
- SDK .NET 8, plus le SDK .NET 10 pour AutoCAD 2027 (les packages NuGet viennent de nuget.org, déclaré dans
  [nuget.config](nuget.config))
- Windows 64 bits, et un client MCP : Claude Desktop, Claude Code, Cursor, Antigravity, Hermes Agent…

## Installer

```powershell
git clone https://github.com/seb21-art/MCP_AutocadMap3D.git
cd MCP_AutocadMap3D
powershell -ExecutionPolicy Bypass -File scripts\install.ps1                    # plug-in + serveur
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Component Server  # serveur seul
```

- **Plug-in** : copié dans `%APPDATA%\Autodesk\ApplicationPlugins\MCPMap3D.bundle` et chargé au démarrage d'AutoCAD.
  Le script compile une DLL par version d'AutoCAD trouvée (2026 en .NET 8, 2027 en .NET 10) ; `-AcadVersion 2027`
  n'en compile qu'une.
  AutoCAD doit être fermé pendant l'installation. Au premier lancement, AutoCAD peut demander s'il faut charger
  la DLL non signée.
- **Serveur MCP** : exécutable unique copié dans `%USERPROFILE%\.mcpmap3d\server\McpMap3D.Server.exe`.
  Il utilise le runtime .NET 8, installé avec le SDK des prérequis. S'il est en cours d'utilisation par un client
  MCP, l'ancien exécutable est renommé : les sessions ouvertes le gardent, les nouvelles utilisent le nouveau.
- Le serveur et les journaux sont volontairement hors d'AppData : Claude Desktop (version Microsoft Store)
  redirige vers son espace privé les nouveaux dossiers AppData créés par les programmes qu'il lance, ce qui les
  rendrait invisibles depuis un terminal ou pour AutoCAD lancé normalement.

## Enregistrer dans un client MCP

Le serveur installé est un exécutable sans argument, qui dialogue avec le client en stdio :
`%USERPROFILE%\.mcpmap3d\server\McpMap3D.Server.exe`. Tout client MCP capable de lancer un serveur local peut
l'utiliser.

### Claude Code et Claude Desktop

`register.ps1` fait l'enregistrement :

```powershell
powershell -ExecutionPolicy Bypass -File scripts\register.ps1
```

- **Claude Code** : `claude mcp add --scope user map3d`. Possible à tout moment ; vérification :
  `claude mcp get map3d`.
- **Claude Desktop** (discussion et onglet Code) : entrée `map3d` dans `claude_desktop_config.json`, avec une
  copie `.bak` créée avant modification. Claude Desktop garde ce fichier en mémoire et le réécrit en entier :
  **quittez-le complètement** (zone de notification > Quitter) avant de lancer `register.ps1 -DesktopOnly`
  depuis un terminal, puis relancez-le. Le script refuse de modifier la configuration si Claude Desktop tourne.

### Autres clients MCP

Procédure générale :

1. Installez le plug-in et le serveur avec `install.ps1`, AutoCAD fermé (voir [Installer](#installer)).
2. Déclarez dans le client un serveur MCP local (stdio) nommé `map3d`. Sa commande est le chemin complet de
   `McpMap3D.Server.exe`, sans argument ni variable d'environnement.
3. Redémarrez le client, ou rechargez ses serveurs MCP. Lancez AutoCAD, puis demandez à l'assistant un ping
   d'AutoCAD.

La plupart des clients lisent un fichier JSON de cette forme (en JSON, doublez les barres obliques inverses) :

```json
{
  "mcpServers": {
    "map3d": {
      "command": "C:\\Users\\<vous>\\.mcpmap3d\\server\\McpMap3D.Server.exe",
      "args": []
    }
  }
}
```

| Client | Où déclarer le serveur |
| --- | --- |
| Cursor | `%USERPROFILE%\.cursor\mcp.json` (tous les projets) ou `.cursor\mcp.json` dans un projet, forme ci-dessus |
| Antigravity | Panneau de l'agent > « … » > MCP Servers > Manage MCP Servers > View raw config (`mcp_config.json`), forme ci-dessus |
| Hermes Agent | `~/.hermes/config.yaml`, section `mcp_servers` : `map3d:` puis `command: "…/McpMap3D.Server.exe"` et `args: []` |
| VS Code (Copilot) | `.vscode\mcp.json` ou `mcp.json` du profil : même contenu, sous la clé `servers`, avec `"type": "stdio"` |
| Codex CLI | `%USERPROFILE%\.codex\config.toml` : section `[mcp_servers.map3d]` avec `command = '…\McpMap3D.Server.exe'` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json`, forme ci-dessus |

L'emplacement de ces fichiers change parfois d'une version à l'autre des clients : en cas de doute, voyez leur
documentation.

- **Même poste, même utilisateur** : le serveur dialogue avec AutoCAD par un named pipe réservé à l'utilisateur
  Windows courant et à sa session. Le client doit donc tourner sur le poste d'AutoCAD, sous le même compte. Un client
  lancé dans WSL (Hermes Agent, par exemple) peut lancer l'exécutable Windows par son chemin
  `/mnt/c/Users/<vous>/.mcpmap3d/server/McpMap3D.Server.exe` : l'exécutable tourne alors côté Windows (non testé).
- **Nombre d'outils** : le serveur expose 160 outils. Si le client en limite le nombre, désactivez-y les familles
  inutiles.
- **Délai d'attente** : un import de cadastre ou un export PDF peut durer plus d'une minute. Si le client coupe les
  appels trop tôt, allongez son délai d'attente.
- Les descriptions des outils et les messages sont en français.

Désinstallation complète (AutoCAD, Claude Desktop et les autres clients MCP fermés) : `scripts\uninstall.ps1`.
Il retire aussi l'enregistrement dans Claude Code et Claude Desktop ; dans les autres clients, supprimez l'entrée
`map3d` vous-même.

## Vérifier

- Dans AutoCAD : `MCPMAP_STATUS` (utilisable aussi en transparent : `'MCPMAP_STATUS`).
- Dans le client MCP : « Fais un ping d'AutoCAD ».
- Depuis PowerShell :

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-mcp.ps1         # serveur MCP de bout en bout (stdio)
powershell -ExecutionPolicy Bypass -File scripts\test-pipe.ps1        # pipe seul : ping
powershell -ExecutionPolicy Bypass -File scripts\test-pipe.ps1 -Method status
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1       # non-régression du pipe
powershell -ExecutionPolicy Bypass -File scripts\test-tools.ps1       # lecture, édition, OD, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-modeling.ps1    # outils 3D (volumes contrôlés), puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-drafting.ps1    # dessin 2D (aires, cotes, blocs), puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-editing.ps1     # réseaux, ajuster/prolonger, raccords…, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-dimensions.ps1  # styles de cote et remplacements, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-layouts.ps1     # présentations et fenêtres (viewports), puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-cadastre.ps1    # cadastre français (BAN, APICARTO, OD), puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-plot.ps1        # impression et export PDF, puis vérification
powershell -ExecutionPolicy Bypass -File scripts\test-drawings.ps1    # dessins : créer, enregistrer, ouvrir, fermer (dossier temporaire)
powershell -ExecutionPolicy Bypass -File scripts\test-xrefs.ps1       # Xref : attacher, décharger, réparer, lier, détacher, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-scene.ps1       # matériaux, lumières, soleil et ambiance de vue, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-fdo.ps1         # connexions FDO : fournisseurs, SDF, WMS, refus, puis déconnexion
powershell -ExecutionPolicy Bypass -File scripts\test-gis.ps1         # fichiers SIG : export SHP/MIF/TAB/GML/SDF, réimport, attributs, système, puis annulation
powershell -ExecutionPolicy Bypass -File scripts\test-batch.ps1       # outil de lot run_batch par le serveur MCP (stdio), puis annulation
```

Les scripts qui annulent leurs modifications tolèrent jusqu'à trois étapes d'annulation créées par d'autres
applications (voir « Annulation ») : elles sont signalées par un avertissement, pas comptées comme des échecs.

Diagnostic : lancée avec la variable d'environnement `MCPMAP3D_TRACE_COMMANDS=1`, AutoCAD journalise dans le journal
du plug-in les commandes et expressions LISP exécutées, ce qui permet d'identifier l'origine d'une étape d'annulation.

`test-mcp.ps1` et `test-batch.ps1` utilisent par défaut `artifacts\server` ; `-Server` permet de viser l'exécutable installé.

`test-tools.ps1`, `test-modeling.ps1`, `test-drafting.ps1`, `test-editing.ps1`, `test-dimensions.ps1`, `test-scene.ps1` et `test-batch.ps1` **modifient** le dessin ouvert avant d'annuler chacune de leurs modifications. Chacun refuse de
s'exécuter si le nom du dessin ne correspond pas à `-DrawingNameLike` (`*test*` par défaut) : n'ouvrez qu'une
copie. Chacun s'exécute en quelques dizaines de secondes, surtout pour annuler pas à pas. Pour préparer une copie de test :

```powershell
Copy-Item "C:\chemin\vers\dessin.dwg" artifacts\test\dessin-test.dwg
```

Journaux : plug-in dans `%USERPROFILE%\.mcpmap3d\logs\plugin.log` ; serveur MCP sur sa sortie d'erreur, recueillie
par le client (Claude Desktop : journaux `mcp-server-map3d.log`).

## Outils MCP

| Outil | Rôle |
| --- | --- |
| `ping` | Vérifie qu'AutoCAD est lancé, que le plug-in répond et que le thread principal est disponible |
| `run_batch` | Enchaîne jusqu'à 50 appels aux autres outils en un seul appel, dans l'ordre ; arrêt à la première erreur par défaut (`stopOnError`), résultat ou erreur de chaque appel. Propre au serveur MCP : chaque appel suit le même chemin qu'un appel direct et reste une étape d'annulation. Un appel ne peut pas reprendre le résultat d'un appel précédent du même lot |
| `list_drawings` | Dessins ouverts : nom, chemin, dessin actif, lecture seule, modifications non enregistrées |
| `new_drawing` | Crée un dessin à partir d'un gabarit et l'active, enregistré aussitôt en .dwg si un chemin est donné |
| `open_drawing` | Ouvre un .dwg, .dxf ou .dwt (lecture seule possible), ou active un dessin déjà ouvert par son nom ou son chemin |
| `save_drawing` | Enregistre un dessin, sous son nom ou sous un autre (le dessin prend ce nom) ; n'écrase un fichier qu'avec `overwrite` |
| `close_drawing` | Ferme un dessin ; refuse s'il a des modifications non enregistrées, sauf `save` ou `discardChanges` |
| `get_drawing_info` | Fichier, unités, étendue, calque et espace courants, présentations, infos Map 3D |
| `list_layers` | Calques, avec couleur, type de ligne, épaisseur, états et description |
| `list_entities` | Objets filtrés par calque, type et espace, paginés, avec handle et résumé par type (contenu des cellules pour un tableau, renflements d'une polyligne avec la géométrie) |
| `create_layer` | Crée un calque ou met à jour ses propriétés |
| `create_line` | Crée une ligne : segment, droite infinie (XLINE, ligne de construction) ou demi-droite (RAY) |
| `create_polyline` | Crée une polyligne, ouverte ou fermée, dans n'importe quel plan : points 3D coplanaires (normale calculée), `plane` xz ou yz en coordonnées locales, ou `normal` imposée |
| `create_text` | Crée un texte sur une ligne ; avec un style, son facteur de largeur et son inclinaison s'appliquent |
| `move_entities` | Déplace des objets d'un vecteur, ou d'un point à un autre |
| `change_layer` | Change des objets de calque |
| `erase_entities` | Supprime des objets |
| `set_entity_properties` | Change couleur, type de ligne et son échelle, épaisseur de ligne et épaisseur d'extrusion d'objets |
| `list_linetypes` | Types de ligne chargés, et ceux disponibles dans le fichier `.lin` d'AutoCAD |
| `list_od_tables` | Tables de données d'objet Map 3D et définition de leurs champs |
| `get_od_records` | Enregistrements de données d'objet attachés à des objets |
| `set_od_value` | Écrit une valeur de donnée d'objet, en attachant l'enregistrement au besoin |

Modélisation 3D :

| Outil | Rôle |
| --- | --- |
| `create_box` | Boîte, par son coin minimal, avec rotation autour de Z |
| `create_wedge` | Biseau (boîte à face supérieure en pente) |
| `create_cylinder` | Cylindre, cône (`topRadius` = 0) ou tronc de cône, d'axe quelconque |
| `create_sphere` | Sphère |
| `create_torus` | Tore |
| `create_pyramid` | Pyramide régulière de 3 à 32 côtés, ou tronc de pyramide, d'axe quelconque |
| `create_3d_polyline` | Polyligne 3D, avec une altitude par sommet |
| `create_helix` | Hélice cylindrique ou conique, d'axe quelconque, par nombre de tours ou par pas, dans les deux sens ; sert de chemin à `extrude` pour un ressort (cercle, `alignProfile` à vrai) ou un filetage (peigne dans un plan contenant l'axe, par exemple `create_region` avec `points` 3D ou `plane` xz, `alignProfile` à faux, moins large que le pas : au-delà d'environ deux pas, les spires se recouvrent et le balayage échoue) |
| `extrude` | Extrusion de profils fermés : hauteur, dépouille, direction, ou balayage le long d'un chemin (torsion, échelle, alignement) ; surfaces extrudées ou balayées avec `surface`, courbes ouvertes comprises |
| `revolve` | Révolution de profils fermés autour d'un axe ; surfaces de révolution avec `surface` |
| `boolean_solids` | Union, soustraction ou intersection de solides |
| `rotate_entities` | Rotation d'objets autour d'un axe 3D |
| `get_solid_properties` | Volume, surface et centre de gravité des solides, inerties sur demande ; sommets, faces, lissage et volume des maillages ; aire, longueur et limites des autres objets |
| `set_view` | Vue de dessus, de face ou isométrique, style visuel, et cadrage : étendue du dessin, fenêtre `window` ou centre `center` (avec `width`/`height`, ou recentrage seul), sans créer d'objet ; `lonLat` prend ces coordonnées en longitude/latitude, converties dans le système du dessin (Lambert-93 sans Map 3D) |
| `capture_view` | Image PNG de la vue courante, renvoyée à l'assistant pour contrôler visuellement son travail |
| `create_region` | Régions à partir de courbes jointives (contour en plusieurs morceaux → profil extrudable), ou directement d'une liste de sommets `points` (mêmes options de plan que `create_polyline`), sans courbe intermédiaire dans le dessin |
| `slice_solid` | Coupe de solides par un plan, en gardant un côté ou les deux |
| `get_section` | Région de coupe de solides par un plan (aire, périmètre), solides intacts |
| `loft` | Solide lissé entre des sections, avec guides ou chemin ; surface lissée avec `surface`, sections ouvertes comprises |
| `mirror_3d` | Symétrie par un plan quelconque de l'espace |
| `align_3d` | Alignement par une à trois paires de points, avec mise à l'échelle possible |
| `check_interference` | Collisions entre solides : paires et volumes communs, dessinés sur demande |
| `buildings_to_solids` | Bâtiments en épaisseur (import cadastre 3D) convertis en solides, handle et données d'objet conservés, cours soustraites |
| `get_solid_topology` | Faces et arêtes numérotées d'un solide : type, aire, normale, extrémités, rayon, faces voisines |
| `fillet_edges` | Congés sur des arêtes de solide, désignées par indice ou filtre (verticales, contour du dessus…) |
| `chamfer_edges` | Chanfreins sur les arêtes d'une face de base, avec deux distances |
| `shell_solid` | Coque d'épaisseur constante, faces ouvertes au choix (bac, cuve, caisson) |
| `edit_solid_faces` | Faces de solide poussées ou tirées (avec dépouille), décalées, déplacées, pivotées, effilées, supprimées, colorées ou copiées |
| `thicken_surface` | Surfaces ou régions épaissies en solides |
| `separate_solid` | Solide fait de volumes disjoints séparé en solides indépendants, après nettoyage |
| `imprint_solid` | Courbes, régions ou solides imprimés sur les faces d'un solide, qui se découpent en faces modifiables |
| `clean_solid` | Arêtes et sommets superflus retirés des solides (SOLIDEDIT Corps Nettoyer) |
| `create_surface` | Surfaces planes (contours ou deux coins), de réseau de courbes, NURBS par points de contrôle, ou décalées |
| `sculpt_solid` | Solide délimité par des surfaces qui enferment un volume (SCULPTER) |
| `project_curves` | Courbes ou points projetés sur un solide ou une surface, verticalement par défaut |
| `create_mesh` | Maillage par sommets et faces, avec niveau de lissage |
| `convert_mesh` | Maillages convertis en solides ou en surfaces, et solides, surfaces ou régions en maillages |
| `smooth_mesh` | Niveau de lissage de maillages, de 0 à 4 |
| `create_terrain_mesh` | Maillage de terrain (MNT) par triangulation de Delaunay de points cotés : tableau, points, blocs, lignes ou polylignes par calque ; grands triangles retirés au besoin ; maillage ou polyface |
| `export_stl` | Export STL de solides, ramenés à l'origine par défaut (impression 3D) |
| `export_sat` | Export ACIS (SAT) de solides, surfaces et régions |
| `import_sat` | Import d'un fichier ACIS texte (SAT) |
| `import_3d_model` | Import d'un fichier STEP (.stp, .step) ou IGES (.igs, .iges), comme IMPORT : conversion par le traducteur d'AutoCAD (`acTranslators.exe`) hors du thread principal, puis insertion en bloc (ou décomposée jusqu'aux solides et surfaces), unités du fichier converties vers celles du dessin. Chemin absolu requis |

Dessin technique 2D :

| Outil | Rôle |
| --- | --- |
| `create_circle` | Cercle |
| `create_arc` | Arc, par centre, rayon et angles, ou par trois points |
| `create_ellipse` | Ellipse par centre, demi-axes et orientation, ou arc d'ellipse entre deux angles |
| `create_spline` | Spline passant par des points (lissage) ou définie par ses sommets de contrôle (degré 1 à 10), ouverte ou fermée |
| `create_rectangle` | Rectangle (polyligne fermée) par deux coins opposés |
| `create_polygon` | Polygone régulier de 3 à 1024 côtés : par centre et rayon (inscrit ou circonscrit), ou par un côté |
| `create_point` | Points, un ou plusieurs par appel, avec l'aspect (PDMODE) et la taille (PDSIZE) des points du dessin |
| `create_revision_cloud` | Nuage de révision (festons) autour de sommets, d'un rectangle ou d'une courbe fermée existante |
| `create_wipeout` | Masque (WIPEOUT) qui cache les objets dessinés avant lui, avec réglage de son cadre |
| `create_boundary` | Contour fermé autour d'un point (commande CONTOUR), en polylignes ou en région, îlots compris |
| `create_mtext` | Texte multiligne : largeur, point d'attache, rotation, style, masque d'arrière-plan |
| `create_table` | Tableau : titre fusionné, en-têtes, lignes de données, largeurs estimées d'après le contenu, alignement par colonne |
| `list_text_styles` | Styles de texte : police SHX ou TrueType, gras, italique, hauteur fixe, largeur, inclinaison, style courant |
| `set_text_style` | Crée un style de texte (copie d'un style) ou le modifie : police, gras, italique, hauteur, largeur, inclinaison ; le rend courant si demandé |
| `create_hatch` | Hachure, remplissage ou dégradé de couleurs, sur contours existants (associative) ou sur une liste de sommets |
| `create_dimension` | Cotes alignée, horizontale, verticale, pivotée, de rayon, de diamètre, angulaire, de coordonnée (X ou Y), de longueur d'arc |
| `list_blocks` | Définitions de blocs : objets, insertions, attributs |
| `create_block` | Bloc à partir d'objets existants, avec attributs ; les objets sont convertis, conservés ou supprimés |
| `insert_block` | Insertion d'un bloc, avec échelle, rotation et valeurs d'attributs |
| `copy_entities` | Copie, éventuellement répétée (réseau linéaire) ; attributs et données d'objet suivent |
| `mirror_entities` | Symétrie par rapport à un axe, en gardant les textes lisibles |
| `scale_entities` | Mise à l'échelle par rapport à un point de base |
| `offset_entities` | Décalage de courbes (parallèles) du côté d'un point |
| `edit_text` | Contenu ou hauteur d'un texte, texte imposé d'une cote, cellules d'un tableau (lignes ajoutées au besoin), valeurs d'attributs d'un bloc |

- **Points** : leur aspect et leur taille sont des réglages communs à tout le dessin ; les changer modifie aussi les
  points existants, affichés à jour après un regen fait par l'outil.
- **Masques** : un masque cache ce qui a été dessiné avant lui. Pour cacher un objet créé ensuite, passez le masque
  devant avec `set_draw_order`. Le cadre (WIPEOUTFRAME) est commun à tous les masques du dessin.
- **Contours** : AutoCAD ne suit que les objets affichés à l'écran. Si le point n'est entouré de rien de visible, la
  vue passe un instant en vue de dessus sur l'étendue du dessin, puis de plus en plus près autour du point, et revient
  enfin à celle de l'utilisateur.
- **Tableaux** : lignes et colonnes sont comptées à partir de 0, titre compris. Les nombres s'écrivent tels qu'ils
  sont reçus ; une chaîne permet la virgule décimale ou une unité. Le texte est de 2,5 par défaut, le titre 1,4 fois
  plus haut ; AutoCAD agrandit une ligne dont le texte passe à la ligne.
- **Styles de texte** : une police TrueType se donne par son nom de famille (Arial, avec gras et italique) ou par son
  fichier (arial.ttf) ; une police SHX par son fichier (romans.shx). Un avertissement signale un fichier introuvable.

Édition avancée :

| Outil | Rôle |
| --- | --- |
| `array_rectangular` | Réseau en rangées, colonnes et niveaux (Z), éventuellement tourné |
| `array_polar` | Réseau autour d'un centre, sur un tour ou un angle partiel, éléments tournés ou non |
| `trim_entities` | Ajuster : découpe aux arêtes de coupe et suppression du morceau désigné par un point |
| `extend_entities` | Prolonger jusqu'à la limite la plus proche (lignes, arcs, polylignes à segment d'extrémité droit) |
| `fillet` | Raccord entre deux lignes, ou sur tous les sommets d'une polyligne ; rayon 0 = angle vif |
| `chamfer` | Chanfrein entre deux lignes, ou sur tous les sommets d'une polyligne |
| `explode_entities` | Décomposition d'un niveau ; les attributs de blocs deviennent des textes avec leur valeur |
| `create_leader` | Ligne de repère multiple (MLEADER) avec texte |
| `array_path` | Réseau le long d'une courbe, par nombre et/ou espacement, éléments alignés sur la tangente ou non |
| `join_entities` | Joint lignes, arcs et polylignes ouvertes bout à bout en polylignes, fermées si la chaîne se referme |
| `break_entities` | Coupe une courbe en un point, ou supprime la partie entre deux points (cercle compris) |
| `edit_polyline` | Sommets d'une polyligne : déplacer, ajouter, supprimer, arrondir un segment, largeurs, ouvrir, fermer, inverser |
| `stretch_entities` | Étirement par fenêtre (rectangle ou polygone) : points dans la fenêtre déplacés, objets entièrement dedans déplacés |
| `set_draw_order` | Ordre d'affichage : premier plan, arrière-plan, au-dessus ou au-dessous d'un objet |

- Les **réseaux** ne sont pas associatifs : chaque élément est une copie indépendante (attributs et données d'objet
  compris). Le long d'un chemin, les originaux sont le premier élément, au début du chemin ; chaque copie garde le
  même décalage par rapport au chemin.
- **Ajuster et prolonger** calculent les intersections en vue de dessus (projection sur le plan XY) : des objets à
  des altitudes différentes se coupent comme à l'écran. Chaque cible est un couple `{ handle, pick }`, le point
  désignant le morceau à supprimer ou l'extrémité à prolonger. Quand un seul morceau subsiste (ligne, arc,
  polyligne), l'objet d'origine est modifié sur place et garde son handle et ses données d'objet. Les autres
  morceaux sont de nouveaux objets, sans données d'objet.
- **Raccord et chanfrein** entre deux lignes gardent, de chacune, le côté de l'extrémité la plus éloignée du coin.
  Les lignes peuvent ne pas se toucher. Sur une polyligne, seuls les sommets entre deux segments droits sont
  traités, et un sommet trop proche de ses voisins est signalé plutôt que déformé.
- **Décomposer** fait perdre les données d'objet de l'objet d'origine.
- **Lignes de repère** : le texte se place du côté vers lequel pointe le dernier segment, et la ligne aboutit
  exactement au dernier point donné.
- **Joindre** : les objets doivent être horizontaux, à la même altitude, et se toucher à `tolerance` près (1e-6 par
  défaut). Une polyligne de la chaîne garde son handle et ses données d'objet ; sinon la nouvelle polyligne reprend
  les propriétés du premier objet. Les objets isolés sont signalés, pas modifiés.
- **Couper** : les points sont ramenés sur la courbe vue de dessus. Une courbe fermée demande deux points ; la partie
  supprimée va du premier au second dans le sens de la courbe (antihoraire pour un cercle, qui devient un arc de
  nouveau handle).
- **Étirer** suit la commande ETIRER : sans `handles`, tous les objets de l'espace courant sont examinés. Les objets
  des calques verrouillés sont signalés, et les hachures associatives suivent leur contour.
- **Polylignes** : `edit_polyline` travaille sur les polylignes 2D (LWPOLYLINE), avec des indices de sommet à partir
  de 0 (-1 pour le dernier). Ajouter un sommet sur un arc le partage en deux arcs.

Mesures, sans modification du dessin :

| Outil | Rôle |
| --- | --- |
| `get_intersections` | Points d'intersection entre objets, paire par paire, vus de dessus ou en 3D, objets prolongés ou non |
| `measure_curve` | Longueur et aire d'une courbe, points et tangentes à des distances, abscisse et décalage de points, division, mesure à intervalle |

- Les **abscisses et décalages** de `measure_curve` se lisent comme un PK et un déport le long d'un axe : distance
  depuis le début de la courbe du point le plus proche vu de dessus, et distance à la courbe, positive à gauche.

Format des cotations :

| Outil | Rôle |
| --- | --- |
| `list_dimension_styles` | Styles de cote : style courant, nombre de cotes par style, réglages |
| `set_dimension_style` | Crée un style (copie d'un style existant) ou le modifie, et le rend courant si demandé |
| `set_dimension_format` | Sur des cotes : change de style, efface les remplacements, ou crée des remplacements |
| `get_dimension_format` | Sur des cotes : style, mesure, texte affiché, réglages effectifs et liste des remplacements |

Les réglages passent par un objet `format`, identique pour un style et pour une cote ; seuls ceux fournis changent :

| Réglage | Variable | Valeurs |
| --- | --- | --- |
| `textHeight`, `arrowSize` | DIMTXT, DIMASZ | longueurs, avant l'échelle globale |
| `arrowhead` | DIMBLK | `closed_filled`, `closed_blank`, `closed`, `dot`, `small_dot`, `dot_blank`, `architectural_tick`, `oblique`, `open`, `open30`, `right_angle`, `origin`, `box_filled`, `box_blank`, `datum_filled`, `integral`, `none`, ou un bloc du dessin |
| `precision`, `angularPrecision` | DIMDEC, DIMADEC | 0 à 8 décimales |
| `unitFormat` | DIMLUNIT | `decimal`, `scientific`, `engineering`, `architectural`, `fractional`, `windows` |
| `angularUnit` | DIMAUNIT | `degrees`, `dms`, `grads`, `radians`, `surveyor` |
| `decimalSeparator` | DIMDSEP | `,` ou `.` |
| `prefix`, `suffix` | DIMPOST | textes avant et après la mesure ; chaîne vide pour retirer |
| `scale` | DIMSCALE | échelle globale des tailles (texte, flèches, écarts) |
| `measurementScale` | DIMLFAC | facteur de la valeur affichée (1000 : mètres affichés en mm) |
| `roundOff` | DIMRND | arrondi de la mesure |
| `textPosition` | DIMTAD | `centered`, `above`, `outside`, `jis`, `below` |
| `textAlignment` | DIMTIH, DIMTOH | `aligned`, `horizontal`, `iso` |
| `textColor`, `lineColor`, `extensionLineColor` | DIMCLRT, DIMCLRD, DIMCLRE | couleurs, comme ailleurs |
| `lineWeight` | DIMLWD, DIMLWE | épaisseur des lignes de cote et d'attache |
| `textStyle` | DIMTXSTY | style de texte existant |
| `extensionOffset`, `extensionBeyond`, `textGap` | DIMEXO, DIMEXE, DIMGAP | longueurs |
| `zeroSuppression` | DIMZIN | `none`, `leading`, `trailing`, `both` |

- Modifier un style **met à jour ses cotes**, sauf leurs remplacements, y compris sur les calques verrouillés.
  Quand le style modifié est le style courant, les variables DIM* du dessin suivent, pour que les commandes de
  cotation d'AutoCAD utilisent les nouveaux réglages.
- Sur une cote, un réglage de `format` devient un **remplacement** du style (comme dans la palette Propriétés).
  `clearOverrides` les efface tous ; `get_dimension_format` les liste.
- Le **texte affiché** est lu dans le bloc de la cote régénéré par AutoCAD : il tient compte du préfixe, du
  suffixe, du facteur de mesure, de l'arrondi et du séparateur décimal. La **mesure** reste géométrique (unités du
  dessin, ou degrés), sans le facteur DIMLFAC.
- Les flèches prédéfinies absentes du dessin y sont chargées ; U les retire avec le reste de l'appel.

Espace Présentation et fenêtres :

| Outil | Rôle |
| --- | --- |
| `list_layouts` | Présentations du dessin : ordre d'onglet, format de papier (mm, média), traceur, nombre de fenêtres, statut courant |
| `set_current_layout` | Active une présentation ou l'espace Objet (« Model »), rendant son espace papier actif pour le dessin |
| `create_layout` | Crée une nouvelle présentation, éventuellement en copiant une existante (`copyFrom`) |
| `rename_layout` | Renomme une présentation existante |
| `delete_layout` | Supprime une présentation (sauf l'espace Objet ou la dernière présentation restante) |
| `list_viewports` | Fenêtres de présentation (Viewports) : handle, centre papier, dimensions, centre de vue objet, échelle, verrouillage |
| `create_viewport` | Crée une fenêtre de présentation sur la feuille courante (dimensions en mm, centre objet, échelle comme `"1:500"`, verrouillage) |
| `set_viewport` | Modifie une fenêtre de présentation : échelle, centre de vue dans l'espace objet, dimensions, verrouillage |

- **Dessiner dans une présentation** : utilisez `set_current_layout` pour activer la présentation souhaitée. Tous les outils
  de dessin existants (`create_rectangle` pour la bordure, `insert_block` pour un cartouche avec attributs, `create_text` pour les titres,
  `create_dimension`…) dessinent alors directement sur la feuille de cette présentation.
- **Échelle des fenêtres** : `create_viewport` et `set_viewport` acceptent une valeur numérique (`customScale`) ou un ratio
  textuel (`"1:500"`, `"1/1000"`). Si le dessin est en mètres (`INSUNITS = 6`), l'outil applique automatiquement le facteur
  terrain/papier (`1000 / 500 = 2.0`).
- **Verrouillage** : l'option `locked: true` verrouille la vue pour éviter les zooms et décalages involontaires dans AutoCAD.

Impression et exportation PDF :

| Outil | Rôle |
| --- | --- |
| `list_plot_devices` | Traceurs et imprimantes disponibles (PC3, PDF, Windows) et formats de papier détaillés d'un traceur |
| `list_plot_styles` | Tables de styles de tracé disponibles (.ctb dépendant des couleurs et .stb nommés) |
| `export_pdf` | Exporte l'espace Objet ou une présentation en PDF avec le moteur de tracé d'AutoCAD |
| `plot_drawing` | Lance un tracé vers une imprimante physique, un traceur réseau ou un fichier (.plt, .pdf, .jpg...) |

- **Export PDF** : `export_pdf` trace avec le moteur d'AutoCAD (`PlotEngine`, en avant-plan), en PDF vectoriel. Le
  pilote est choisi automatiquement : `AutoCAD PDF (General Documentation).pc3`, sinon `DWG To PDF.pc3`.
- **Formats de papier** : `"A4"` à `"A0"`, `"Letter"`…, en version pleine page sans marges (`ISO full bleed`) quand
  elle existe, en orientation `"landscape"` ou `"portrait"`.
- **Zone de tracé** : étendue (`"extents"`), présentation entière (`"layout"`), fenêtre (`"window"`,
  `[minX, minY, maxX, maxY]`) ou vue courante (`"display"`) ; échelle ajustée au papier (`fitToPaper: true`) ou
  nominale (`"1:500"`).
- **Styles de tracé** : tables CTB, par exemple `"monochrome.ctb"` ou `"acad.ctb"`.
- **Présentation tracée** : n'importe quelle présentation, ou l'espace objet, peut être exportée quelle que soit
  la présentation courante. AutoCAD ne sait tracer que la présentation courante : elle est activée le temps du tracé,
  puis l'utilisateur retrouve la sienne (`temporarilyActivatedFrom` dans le résultat).
- **Format par défaut** : sans `paperSize`, le format de la présentation est conservé sur le pilote PDF (même format,
  ou mêmes dimensions), au lieu du format par défaut du pilote.
- **Échelle** : `"1:500"` est une échelle réelle, calculée d'après les unités du dessin (INSUNITS : un dessin en mètres
  donne 2 mm de papier par mètre), le papier étant mis en millimètres. Dessin sans unités : 1 mm = 500 unités.
- **Traceur ou imprimante** : `plot_drawing` imprime sur une imprimante Windows ou un traceur, avec un nombre
  d'exemplaires (`copies`).

`list_entities` accepte aussi `handles`, pour décrire des objets précis. Il donne le texte en clair des MTEXT, la
mesure des cotes et les valeurs d'attributs des blocs.

Propriétés graphiques (`set_entity_properties`, et à la création via `create_layer`, `create_line`,
`create_polyline`, `create_3d_polyline`) :

- **Couleur** : index 1-255, `R,G,B`, nom (`rouge`, `jaune`, `vert`, `cyan`, `bleu`, `magenta`, `blanc`/`noir`,
  `gris`), `ByLayer` ou `ByBlock`.
- **Type de ligne** : nom du fichier `.lin` (`acadiso.lin` en métrique ; en français `CACHE`, `AXES`, `INTERROMPU`,
  `POINTILLE`…), chargé automatiquement s'il manque au dessin. Le chargement fait partie de l'appel : U l'annule aussi.
- **Épaisseur de ligne** (trait tracé) : valeur normalisée en mm (0.13, 0.25, 0.35, 0.50…), `ByLayer`, `ByBlock` ou
  `Default`. Elle ne s'affiche à l'écran que si l'affichage des épaisseurs (LWDISPLAY) est actif.
- **Épaisseur d'extrusion** (`thickness`, propriété « Épaisseur » d'AutoCAD) : hauteur donnée à un objet 2D (lignes,
  cercles, arcs, polylignes, textes, points). Les objets qui ne la gèrent pas sont signalés dans la réponse.

Système de coordonnées :

| Outil | Rôle |
| --- | --- |
| `get_coordinate_system` | Système attribué au dessin : code, description, EPSG, unités, domaine d'usage, centre du dessin en lon/lat |
| `search_coordinate_systems` | Recherche dans le catalogue de Map 3D (≈ 8800 systèmes) par mots, code Autodesk ou numéro EPSG |
| `set_coordinate_system` | Attribue un système au dessin (ou le retire avec un code vide) |

- Attribuer un système **ne déplace pas les objets** : cela déclare dans quel système sont exprimées leurs coordonnées.
- **Compatibilité** : un système est compatible si les quatre coins de l'étendue du dessin, convertis en
  longitude/latitude, tombent dans son domaine d'usage. Par exemple, un dessin en Lambert-93 est reconnu
  compatible avec `Lambert93` et incompatible avec les zones `RGF93.CC42` à `RGF93.CC50`.
- `set_coordinate_system` **refuse** un système incompatible, sauf avec `force=true` (dessin pas encore
  géoréférencé, par exemple).
- La première recherche indexe le catalogue en 2 à 4 s ; les suivantes sont immédiates.

Systèmes de coordonnées utilisateur (SCU) :

| Outil | Rôle |
| --- | --- |
| `list_ucs` | SCU courant de l'espace actif (origine et axes en SCG, SCU nommés correspondants) et SCU nommés du dessin |
| `set_ucs` | Définit le SCU courant : SCG ou SCU orthogonal (`preset`), SCU nommé, 3 points, axes, aligné sur un objet, origine seule, rotation autour d'un axe ; peut l'enregistrer sous un nom (`saveAs`) |
| `delete_ucs` | Supprime des SCU nommés |
| `convert_ucs_points` | Convertit des points entre SCG (`world`), SCU courant (`current`) et SCU nommés, sans modifier le dessin |

- **Tous les autres outils restent en SCG**, quel que soit le SCU courant : le SCU sert à l'utilisateur (saisie,
  réticule, plan de travail) et `convert_ucs_points` traduit les coordonnées locales avant de les passer aux outils.
- `set_ucs` accepte un seul mode à la fois ; `origin` s'ajoute à `preset`, aux axes ou à `handle`, et seule déplace le
  SCU courant. La rotation (`rotateAxis`, `rotateAngle`) se fait autour d'un axe du SCU obtenu, par son origine.
- Alignement sur un objet (`handle`) : ligne (X le long de la ligne), cercle, arc, ellipse (origine au centre),
  polyligne ou courbe plane (X le long du premier segment), bloc (axes du bloc), texte.
- `setCurrent=false` avec `saveAs` définit un SCU nommé sans changer le SCU courant. UCSNAME d'AutoCAD ne reflète
  que les SCU restaurés par la commande SCU : `list_ucs` indique à part les SCU nommés qui correspondent au courant.

Références externes (Xref) de dessins DWG :

| Outil | Rôle |
| --- | --- |
| `list_xrefs` | Xref du dessin, imbriquées comprises : type (attache ou superposition), état (chargée, déchargée, introuvable…), chemin enregistré et fichier chargé, parents, insertions (handle, position, échelle, rotation, calque, espace) |
| `attach_xref` | Attache ou superpose un DWG et insère une référence (position, échelle, rotation, calque) ; chemin enregistré complet, relatif ou nom seul |
| `detach_xrefs` | Détache des Xref : insertions et définition supprimées |
| `reload_xrefs` | Recharge des Xref (toutes par défaut), déchargées ou non, et signale celles qui restent introuvables |
| `unload_xrefs` | Décharge des Xref, qui restent attachées |
| `bind_xrefs` | Lie des Xref chargées au dessin : mode `bind` (calques préfixés `NOM$0$`) ou `insert` (fusion) |
| `edit_xref` | Nouveau fichier ou forme du chemin (réparer une Xref introuvable, passer en relatif), renommage, attache ou superposition |

- Les noms acceptent les jokers `*` et `?`. Une **Xref imbriquée** se voit dans `list_xrefs` (avec ses parents),
  mais ne se détache, ne se décharge, ne se recharge ni ne se lie seule : on agit sur la Xref qui la contient.
- **Chemins** : `pathType` vaut `auto` par défaut, soit un chemin relatif au dessin hôte s'il est enregistré et sur
  le même lecteur (comme ATTACHER), sinon complet. Un `filePath` relatif part du dossier du dessin hôte.
- Seules les Xref de dessins DWG sont gérées : pas les images, PDF, DWF ou DGN sous-jacents, ni le découpage (XCLIP).
- Détachement, rechargement, déchargement et liaison ont lieu une fois la transaction de l'outil validée, dans la
  même commande : l'appel reste une seule étape d'annulation.

Rendu : matériaux, lumières, soleil et ambiance de la vue.

| Outil | Rôle |
| --- | --- |
| `list_materials` | Matériaux du dessin : couleur diffuse, opacité (1 = opaque), brillance, réflexion, texture, recto-verso |
| `set_material` | Crée ou modifie un matériau (couleur, opacité, brillance, réflexion, texture image, recto-verso) |
| `assign_material` | Affecte un matériau à des objets, à des calques, ou à une face de solide (indice de `get_solid_topology`) |
| `list_lights` | Lumières des présentations : type, position, cible, intensité, couleur, ombres |
| `set_light` | Crée ou modifie une lumière ponctuelle, un projecteur, une lumière lointaine ou photométrique (fichier IES) |
| `delete_lights` | Supprime des lumières |
| `get_sun` | Soleil de la vue active : date, heure, ombres, azimut, et lieu géographique du dessin |
| `set_sun` | Date, heure et ombres ; le lieu est calé sur le système de coordonnées (nord de la projection, heure de Paris pour un système français ou un lieu en France) |
| `get_view_ambiance` | Arrière-plan, luminosité, contraste et éclairage par défaut de la vue |
| `set_view_ambiance` | Fond uni, dégradé, image ou ciel, luminosité, éclairage par défaut (`off`, `one`, `two`) et style visuel |

- **Matériaux** : `ByLayer` et `ByBlock` sont réservés et ne s'affectent qu'aux objets. `shininess` règle le reflet spéculaire (brillance), `reflectivity` l'effet miroir. `ByLayer` sur une face retire le matériau propre à cette face. Une texture est un fichier image présent sur le disque.
- **Lumières** : dans l'espace courant. L'intensité suit `LIGHTINGUNITS` : sans unité, ou intensité de la lampe en candelas en photométrique (le facteur d'intensité reste à 1 ; `list_lights` l'indique s'il diffère). Les lumières imbriquées dans un bloc ne sont pas listées. `hotspot` et `falloff` sont en degrés.
- **Soleil** : la latitude, la longitude et le nord sont calés sur le système de coordonnées du dessin, au centre de l'emprise ou au point `at`. Sans système attribué, un dessin dont l'emprise tombe en Lambert-93 est traité comme tel (`assumedCoordinateSystem`). Un système français (Lambert-93, CC42 à CC50, Lambert NTF), ou un lieu situé en France métropolitaine quel que soit le système (UTM, WGS84…), utilise l'heure de Paris, heure d'été comprise. Ailleurs, le décalage est solaire (un fuseau par 15° de longitude, sans heure d'été) et un avertissement le dit : `utcOffset` et `daylightSaving` donnent l'heure légale. Azimut et hauteur du soleil sont en degrés. Vérifié dans Civil 3D 2026 avec Lambert-93, CC46, UTM 31N, 18N et 56S, et LL84. `latitude` et `longitude` remplacent le calcul ; ce lieu manuel est conservé par les appels suivants (`manualPlace`) jusqu'à `calibrate` vrai. `north`, `utcOffset` et `daylightSaving` s'appliquent même si le lieu n'est pas trouvé. Le marqueur géographique du dessin (GEOGRAPHICLOCATION) n'est jamais modifié.
- **Ambiance** : la vue active, ou la fenêtre désignée par `viewport`. Le style visuel Filaire 2D n'affiche pas l'arrière-plan : `visualStyle` (`realistic`, `shaded`, `conceptual`…) change le style de la vue, et un avertissement le rappelle. Le fond `sky` utilise le soleil de cette vue. La luminosité est celle de la propriété Brightness d'AutoCAD.

Connexions FDO (carte courante, plateforme géospatiale).

| Outil | Rôle |
| --- | --- |
| `list_fdo_providers` | Fournisseurs installés (SDF, SHP, SQLite, WFS…) |
| `list_fdo_connections` | Sources créées par le connecteur (`Library://MCP_…`), et celles déjà affichées ; `includeLibrary` ajoute tout le dépôt Library |
| `connect_fdo` | Teste une chaîne `Clé=valeur` et enregistre la source à la racine du dépôt (`Library://MCP_nom`), où `MAPCONNECT` la montre ; `createFile` crée un SDF vide ; un nom déjà pris est refusé, sauf `replace` (jamais si un calque l'affiche) |
| `describe_fdo` | Schémas, classes, propriétés et contextes spatiaux |
| `add_fdo_layer` | Affiche une classe (`Schéma:Classe`) comme calque de la carte : vectoriel, ou raster pour une image ou un WMS |
| `list_fdo_layers` | Calques FDO : visibilité, source, classe, géométrie |
| `query_fdo` | Lit des objets (filtre FDO, 200 au plus) sans les modifier |
| `set_fdo_layer` | Visibilité, nom, cadrage ; un calque raster (WMS : Map 3D échoue ou cadre le monde entier) ou vide est cadré sur l'étendue déclarée par sa source, limitée au domaine du système du dessin, et un raster sans étendue exploitable sur ce domaine, la France pour un WMS national en Lambert-93 (`zoom.method` : `layer`, `spatialContext`, `coordinateSystemDomain` ou `none`) |
| `remove_fdo_layer` | Retire le calque ; `disconnect` retire aussi la définition devenue inutile, seulement si le connecteur l'a créée |
| `disconnect_fdo` | Retire la définition d'une source créée par le connecteur qui n'est plus affichée (pas le fichier) |
| `get_fdo_selection` | Objets FDO de la sélection courante |

- **Dépôt partagé avec Map 3D** : les sources et définitions créées par le connecteur sont à la racine de `Library://`, comme celles de `MAPCONNECT` (qui n'affiche que la racine), avec le préfixe `MCP_`. Ce dépôt est enregistré dans le dessin. `disconnect_fdo` et `remove_fdo_layer` ne retirent jamais une source sans ce préfixe (connexion faite dans Map 3D, par exemple), ni une source encore affichée ; chaque calque a sa propre définition. Les sources de `Library://MCPMap3D/` des versions 0.23 restent reconnues.
- **WMS et images** : une classe raster devient un calque de grille (`GridLayerDefinition`), un calque vectoriel ne montrerait rien. Pour un WMS, `add_fdo_layer` crée comme `MAPCONNECT` une source dédiée à la couche (`Library://MCP_<calque>_WMS`) : adresse en `version=1.3.0`, document `config://…` qui déclare la couche (PNG transparent, EPSG:4326, systèmes du serveur recopiés tels quels). Map 3D reprojette l'image dans le système du dessin, qui doit en avoir un.
- **Pas d'annulation U** : une source ou un calque FDO reste après U. Retirez-les avec `remove_fdo_layer` et `disconnect_fdo`.
- **Lecture seule des données** : ces outils ne font ni insertion, ni mise à jour, ni suppression d'objets dans la source, ni SQL.
- Les mots de passe des sources déjà stockées ne sont pas renvoyés.

Fichiers SIG (API ImportExport de Map 3D, comme `MAPIMPORT` et `MAPEXPORT`). Contrairement aux connexions FDO, les objets deviennent des objets AutoCAD du dessin.

| Outil | Rôle |
| --- | --- |
| `import_gis` | Importe un fichier SHP, MapInfo (MIF/MID, TAB), GML, E00, DGN ou SDF : un calque par couche du fichier (ou `layer`), attributs dans une nouvelle table de données d'objet (`odTable`, suffixée si elle existe), coordonnées converties du système du fichier (`.prj`) vers celui du dessin ; `preview` liste couches, colonnes et systèmes sans importer ; `inputLayers` choisit les couches d'un fichier qui en a plusieurs |
| `export_gis` | Exporte des objets (par `handles`, par `layers` ou tout l'espace objet) avec leurs données d'objet en colonnes ; une géométrie par fichier : points (points, blocs, textes), lignes (courbes) ou polygones (courbes fermées, MPolygon) ; `targetCoordinateSystem` convertit, `dimension` garde les Z en SHP, `overwrite` remplace le fichier et ses compagnons ; les courbes de longueur nulle sont ignorées et signalées |

- **Format** : déduit de l'extension (`.shp`, `.mif`, `.tab`, `.gml`, `.e00`, `.dgn`, `.sdf`), ou `format` (nom Map 3D : `SHP`, `MIF`, `MAPINFO`, `GML`, `E00`, `DGN`, `SDF`).
- **Système de coordonnées** : à l'import, conversion seulement si le fichier et le dessin ont chacun un système ; sinon les coordonnées sont reprises telles quelles et un avertissement le dit. À l'export, le système du dessin est écrit dans le fichier.
- **Colonnes** : en SHP, les noms de champs sont limités à 10 caractères (format dBase) ; un champ présent dans deux tables est préfixé par le nom de sa table.
- **Annulation** : un import s'annule en une étape avec U ; un export ne modifie pas le dessin. Requiert Map 3D (ou Civil 3D).

Cadastre français (données ouvertes de l'IGN et Base Adresse Nationale) :

| Outil | Rôle |
| --- | --- |
| `search_cadastre_address` | Géocode une adresse (BAN) et donne la parcelle qui la contient, puis les parcelles du rayon triées par distance, sans modifier le dessin |
| `import_cadastre` | Télécharge le plan cadastral (APICARTO de l'IGN) et le dessine dans le système du dessin : parcelles, numéros, feuilles de sections, bâtiments en option (2D ou 3D), données d'objet |

- **Modes d'import** :
  - `address` : carré de demi-côté `buffer` (100 m par défaut) autour d'une adresse. Une adresse ambiguë (même rue
    dans plusieurs communes) ou mal reconnue est refusée plutôt qu'importée au mauvais endroit.
  - `extent` : emprise des objets du dessin, plus `buffer` (20 m par défaut). Le dessin doit avoir un système de
    coordonnées.
  - `parcel` : code INSEE, section (`A` est complété en `0A`) et un ou plusieurs numéros (`81, 82`), ou toute la
    section. Avec `buffer`, les parcelles voisines dans ce rayon sont importées aussi.
- **Garde-fous** : au-delà de `maxParcels` parcelles (2000 par défaut) ou d'une zone de 10 km de côté, rien n'est
  importé et le message donne le nombre de parcelles. Les réponses d'APICARTO sont paginées (sans troncature
  silencieuse) et refiltrées localement : pour une section inexistante, le service renvoie toute la commune.
- **Bâtiments** (`includeBuildings`, désactivé par défaut) : ceux de la zone, ou en mode `parcel` ceux qui touchent
  les parcelles importées, téléchargés sur la Géoplateforme de l'IGN (WFS). Deux sources (`buildingSource`) :
  - `cadastre` (par défaut) : bâti du plan cadastral (Parcellaire Express), aligné sur les parcelles, en dur ou léger ;
  - `bdtopo` : BD TOPO, avec nature, usage, hauteur, nombre d'étages et de logements, altitudes du sol et du toit,
    identifiant RNB.
  Calques `CADASTRE_BATI_DUR` (couleur 8) et `CADASTRE_BATI_LEGER` (couleur 33), tracés sous les parcelles. Table
  `CADASTRE_BATIMENTS` : `ID`, `SOURCE`, `TYPE`, `LEGER`, `USAGE`, `HAUTEUR`, `ETAGES`, `LOGEMENTS`, `ALT_SOL`,
  `ALT_TOIT`, `RNB`, `CODE_INSEE` (0 ou vide si inconnu). Plafond : `maxBuildings` (4000 par défaut).
- **Bâtiments en 3D** (`buildings3d`, BD TOPO choisie d'office) : chaque polyligne est posée à l'altitude NGF du sol
  (élévation) et extrudée de la hauteur du bâtiment (épaisseur). Ceux dont la hauteur est inconnue restent à plat et
  sont signalés. La vue est cadrée sur la zone : `set_view` en isométrique (`zoomExtents` faux) la montre en 3D.
- **Réimport** : les parcelles, feuilles et bâtiments déjà présents (même identifiant en données d'objet sur les calques du
  préfixe) sont ignorées (`skipExisting`), ce qui permet de compléter un import sans doublons.
- **Système de coordonnées** : celui du dessin s'il en a un, à condition que la zone soit dans son domaine (sinon
  refus). Sinon, `coordinateSystem`, ou le système légal de la zone : `Lambert93` en métropole et en Corse,
  UTM locaux outre-mer (Antilles, Guyane, La Réunion, Mayotte). Un avertissement signale un dessin non vide.
- **Calques** (préfixe `CADASTRE` par défaut, `layerPrefix`) :
  - `CADASTRE_PARCELLES` (couleur 30, 0,25 mm) : une polyligne fermée par contour, trous compris, sans sommet
    de fermeture répété.
  - `CADASTRE_NUMEROS` (couleur 2) : numéro sans zéros de tête, placé à l'intérieur de la parcelle même concave ;
    « section numéro » quand les sections ne sont pas importées.
  - `CADASTRE_SECTIONS` (couleur 1, 0,50 mm) : feuilles cadastrales, avec le code de section trois fois plus grand.
- **Données d'objet** : table `CADASTRE` (`ID`, `COMMUNE`, `CODE_INSEE`, `SECTION`, `NUMERO`, `CONTENANCE` en m²)
  sur chaque contour de parcelle, et table `CADASTRE_SECTIONS` (`ID`, `COMMUNE`, `CODE_INSEE`, `SECTION`,
  `FEUILLE`). Les tables portent le nom du préfixe ; une table existante de structure différente est signalée.
- **Résultat** : nombres importés et ignorés, système utilisé, emprise, et identifiant, étiquette et handle des
  100 premières parcelles.
- **AutoCAD sans Map 3D** : l'import du cadastre fonctionne aussi, en France métropolitaine et en Lambert-93
  seulement (EPSG:2154).
  - La projection est calculée en C# (algorithme IGN NTG_71), sans bibliothèque géodésique.
  - Les attributs (`ID`, `COMMUNE`, `CODE_INSEE`, `SECTION`, `NUMERO`, `CONTENANCE` ou `FEUILLE`) sont stockés en
    XData (applications `CADASTRE` et `CADASTRE_SECTIONS`) au lieu de données d'objet, et se lisent avec
    `get_od_records`. `skipExisting` s'appuie sur ces XData.
  - En mode `extent`, l'emprise du dessin est supposée en Lambert-93 et convertie en WGS84.
- **Réseau** : délai de 90 s par requête, deux nouvelles tentatives en cas de coupure ou d'erreur serveur ; la BAN
  est interrogée sur la Géoplateforme de l'IGN, puis sur l'ancienne adresse de l'API Adresse.
- **Annulation** : l'import entier, attribution du système comprise, s'annule en une seule étape avec U.

Les objets sont désignés par leur **handle**, tel que renvoyé par `list_entities`. Les coordonnées sont celles du
repère général du dessin, et **les angles sont en degrés** partout (paramètres et résultats).

Messages d'erreur renvoyés au client MCP : AutoCAD non lancé, plug-in non chargé, AutoCAD occupé (commande ou boîte
de dialogue en cours), liaison interrompue, calque ou table absents, handle inconnu, calque verrouillé.

### Annulation

Chaque appel d'un outil de modification du dessin s'exécute en contexte commande AutoCAD : la commande **U** (ou Ctrl+Z)
défait l'appel entier en une seule étape, y compris les écritures de données d'objet. Deux conséquences :

- un appel d'écriture **en échec** consomme lui aussi une étape d'annulation, sans rien changer ;
- d'autres applications peuvent glisser leurs propres étapes. Avec Covadis, la commande `COVANEWS` (fenêtre
  « COVADIS - Informations »), mise en file au démarrage d'AutoCAD, s'exécute lors de la première commande de la
  session et laisse une étape vide entre les premières opérations ;
- enchaînés en rafale, des U peuvent être absorbés quand une annulation déclenche une opération Map :
  annulez pas à pas.

Un lot `run_batch` n'est pas une étape d'annulation unique : chacun de ses appels de modification reste une étape
séparée, défaite par son propre U.

Les outils de gestion des dessins (`new_drawing`, `open_drawing`, `save_drawing`, `close_drawing`) agissent sur
les fichiers et les fenêtres d'AutoCAD, en contexte application : ils ne s'annulent pas avec U.

### Méthodes de diagnostic

Servies par le pipe mais volontairement **non exposées comme outils MCP**, elles servent aux scripts de test :

| Méthode | Rôle |
| --- | --- |
| `status` | État du connecteur ; répond même si AutoCAD est occupé |
| `_undo` | Met la commande U d'AutoCAD dans la file (paramètre `count`) |
| `_create_od_table` | Crée une table de données d'objet (`name`, `description`, `fields`) |
| `_convert_3d_model` | Conversion d'un fichier STEP ou IGES en DWG temporaire par `acTranslators.exe`, hors du thread principal (`filePath`, `timeoutSeconds`) ; premier temps de `import_3d_model` |
| `_insert_3d_model` | Insertion d'un DWG converti en bloc (`dwgPath`, `sourceFile`, `position`, `scale`, `blockName`, `explode`, `layer`, `color`, `deleteDwg`) ; second temps de `import_3d_model` |
| `_fdo_diagnostic` | Calques FDO de la carte avec leur type, XML de leur définition et de leur source ; données nommées d'une source (`source`, `dataNames`) ; configuration par défaut d'un fournisseur (`provider`, `connectionString`) |

## Sécurité et données

- Le pipe n'accepte que l'utilisateur Windows courant, dans sa session. Rien n'écoute sur le réseau.
- Aucun outil n'exécute de LISP ni de commande AutoCAD arbitraire : l'assistant ne peut faire que ce que les
  outils décrits ici permettent. Le seul programme lancé est le traducteur d'AutoCAD (`acTranslators.exe`, dans le
  dossier d'installation), par `import_3d_model`, avec le fichier à importer et un DWG temporaire pour seuls arguments.
- Les seuls accès à Internet sont ceux des outils de cadastre, vers les services publics de l'IGN
  (`apicarto.ign.fr`, `data.geopf.fr`) et de la Base Adresse Nationale (`api-adresse.data.gouv.fr`), et ceux des
  connexions FDO vers les serveurs que vous indiquez (WMS, WFS…), faits par Map 3D. Aucune télémétrie.
- Le contenu du dessin lu par les outils est transmis au client MCP, et donc au modèle d'IA qu'il utilise, comme
  tout ce que renvoie un connecteur MCP.

## Protocole du pipe

Pipe `\\.\pipe\McpMap3D.v1.s<session Windows>`, réservé à l'utilisateur courant. Une requête JSON par ligne,
une réponse JSON par ligne (UTF-8) :

```json
{"id":"1","method":"ping","params":{},"timeoutMs":15000}
{"id":"1","ok":true,"result":{"pong":true,"plugin":"0.20.0","mapApi":"disponible","activeDocument":"Dessin1.dwg"}}
{"id":"2","ok":false,"error":{"code":"autocad_busy","message":"AutoCAD est occupé (la commande LINE est en cours) : ..."}}
```

- Les outils s'exécutent sur le thread principal d'AutoCAD, dans `Application.Idle`, seulement s'il n'y a
  ni commande en cours ni boîte de dialogue modale.
- Si AutoCAD n'est pas disponible avant `timeoutMs` (15 s par défaut, de 1 s à 5 min), la requête est abandonnée
  sans rien modifier et le code `autocad_busy` est renvoyé.
- `status` répond immédiatement, sans passer par le thread principal.
- Codes d'erreur : `bad_request`, `unknown_method`, `invalid_params`, `autocad_busy`, `no_document`,
  `map_unavailable`, `shutting_down`, `internal_error`.

## Structure

| Dossier | Contenu |
| --- | --- |
| `src/Shared` | Contrat du pipe, compilé dans le plug-in et le serveur MCP |
| `src/McpMap3D.Plugin` | Plug-in AutoCAD (`net8.0-windows`, x64) ; références AutoCAD, Map 3D et plateforme géospatiale (`OSGeo.MapGuide.*`) sans copie locale |
| `src/McpMap3D.Server` | Serveur MCP (`net8.0`, stdio, SDK `ModelContextProtocol` 2.2.0) |
| `bundle/MCPMap3D.bundle` | `PackageContents.xml` du bundle |
| `scripts` | Installation, enregistrement, désinstallation et tests de bout en bout |
| `tests/McpMap3D.Tests` | Tests unitaires (projection Lambert-93, triangulation du maillage de terrain) : `dotnet test tests\McpMap3D.Tests` |

## Licence

[MIT](LICENSE).

Projet indépendant, ni affilié à Autodesk, ni approuvé par Autodesk. AutoCAD, AutoCAD Map 3D et Civil 3D sont des
marques d'Autodesk, Inc. Le dépôt ne contient aucune DLL Autodesk : le plug-in se compile contre celles de
l'installation d'AutoCAD.

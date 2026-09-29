# Connecteur MCP gratuit pour AutoCAD Map 3D 2026

## Objectif
Plug-in personnel et gratuit permettant à Codex (Codex Desktop et Codex) de lire ET de modifier le dessin ouvert dans AutoCAD Map 3D 2026, y compris les données d'objet (OD) de Map 3D.

Code 100 % original : ne jamais décompiler, copier ou réutiliser le code d'un autre connecteur ou plug-in.

## Environnement
- Windows 64 bits, AutoCAD Map 3D 2026 = release R25.1, .NET 8 (`net8.0-windows`, x64). Fonctionne aussi dans Civil 3D 2026, qui inclut Map 3D.
- AutoCAD 2027 (R26.0) exige .NET 10 et n'est pas compatible en binaire avec 2026 : le plug-in se compile une fois par version avec `-p:AcadVersion=2026` (défaut, `net8.0-windows`) ou `-p:AcadVersion=2027` (`net10.0-windows`, SDK .NET 10). Le serveur MCP reste en .NET 8 pour les deux.
- Les DLL AutoCAD et Map 3D sont référencées sans copie locale (`<Private>false</Private>`) depuis `$(AcadDir)`, par défaut `C:\Program Files\Autodesk\AutoCAD $(AcadVersion)\` (sinon `-p:AcadDir=...`) : `accoremgd`, `acdbmgd`, `acmgd`, `acdbmgdbrep`, `Map\ManagedMapApi`, `Map\AcMapCoordsysCoreMgd` et les `OSGeo.MapGuide.*` de `Map\bin\GisPlatform`.
- Version commune dans `Directory.Build.props`, à reporter dans `AppVersion` de `bundle/MCPMap3D.bundle/PackageContents.xml`.

## Architecture

### 1. Plug-in AutoCAD (bibliothèque de classes)
- `IExtensionApplication` démarre un serveur named pipe (`PipeOptions.CurrentUserOnly`) sur un thread d'arrière-plan.
- Requêtes et réponses en JSON (`System.Text.Json`).
- Toute opération sur le dessin s'exécute sur le thread principal d'AutoCAD (file d'attente traitée dans `Application.Idle`), avec `LockDocument` et une `Transaction`.
- Chaque appel d'outil doit pouvoir être annulé en une seule étape (commande U).
- Timeout explicite si AutoCAD est occupé (commande ou boîte de dialogue en cours).
- Dépendances minimales, pour éviter les conflits avec les DLL déjà chargées par AutoCAD.
- Commande de diagnostic `MCPMAP_STATUS`.

### 2. Serveur MCP (application console séparée, net8.0)
- Package NuGet `ModelContextProtocol` (SDK C# officiel), transport stdio.
- Chaque outil MCP relaie la requête au plug-in via le named pipe.
- Message d'erreur clair si Map 3D n'est pas lancé ou si le plug-in n'est pas chargé.

### 3. Bundle de chargement automatique
- Dossier `MCPMap3D.bundle` contenant un `PackageContents.xml` avec un bloc `Components` par version : `Contents/2026/` (`SeriesMin="R25.1" SeriesMax="R25.1"`) et `Contents/2027/` (`SeriesMin="R26.0" SeriesMax="R26.0"`), `Platform="Map|Civil3D|AutoCAD"` et `LoadOnAutoCADStartup="True"`.
- Script PowerShell d'installation vers `%APPDATA%\Autodesk\ApplicationPlugins` (pas de droits administrateur), qui compile une DLL pour chaque version d'AutoCAD installée.

## Outils (159)
Chaque outil existe deux fois : déclaré côté serveur dans `src/McpMap3D.Server/Tools/*.cs` (`[McpServerTool]`), et enregistré côté plug-in dans `src/McpMap3D.Plugin/Tools/ToolRegistry.cs` avec son accès au dessin (`None`, `Read`, `Write`, `Document`). Le README décrit chaque outil et ses limites. Seule exception : `run_batch`, propre au serveur (`Tools/BatchTools.cs`), qui appelle les autres outils par le SDK MCP.
- Système : `ping`, `run_batch` (lot d'appels d'outils, 50 au plus)
- Dessins : `list_drawings`, `new_drawing`, `open_drawing`, `save_drawing`, `close_drawing` (contexte application, hors annulation U)
- Lecture : `get_drawing_info`, `list_layers`, `list_linetypes`, `list_entities` (filtres par type et calque, pagination)
- Édition : `create_layer`, `create_line`, `create_polyline`, `create_text`, `move_entities`, `change_layer`, `set_entity_properties`, `erase_entities`
- Dessin 2D : `create_circle`, `create_arc`, `create_ellipse`, `create_spline`, `create_rectangle`, `create_polygon`, `create_point`, `create_revision_cloud`, `create_wipeout`, `create_boundary`, `create_mtext`, `create_table`, `list_text_styles`, `set_text_style`, `create_hatch`, `create_dimension`, `list_blocks`, `create_block`, `insert_block`, `copy_entities`, `mirror_entities`, `scale_entities`, `offset_entities`, `edit_text`
- Édition avancée : `array_rectangular`, `array_polar`, `array_path`, `trim_entities`, `extend_entities`, `fillet`, `chamfer`, `explode_entities`, `create_leader`, `join_entities`, `break_entities`, `edit_polyline`, `stretch_entities`, `set_draw_order`
- Mesures : `get_intersections`, `measure_curve` (sans modification du dessin)
- Cotation : `list_dimension_styles`, `set_dimension_style`, `set_dimension_format`, `get_dimension_format`
- 3D : `create_box`, `create_wedge`, `create_cylinder`, `create_sphere`, `create_torus`, `create_pyramid`, `create_3d_polyline`, `create_helix`, `extrude`, `revolve`, `loft`, `boolean_solids`, `rotate_entities`, `mirror_3d`, `align_3d`, `create_region`, `slice_solid`, `get_section`, `check_interference`, `get_solid_properties`, `get_solid_topology`, `fillet_edges`, `chamfer_edges`, `shell_solid`, `edit_solid_faces`, `imprint_solid`, `clean_solid`, `thicken_surface`, `separate_solid`, `buildings_to_solids`, `set_view`, `capture_view`
- Surfaces et maillages : `create_surface`, `sculpt_solid`, `project_curves`, `create_mesh`, `convert_mesh`, `smooth_mesh`, `create_terrain_mesh` (MNT par triangulation de Delaunay)
- Échanges 3D : `export_stl`, `export_sat`, `import_sat`
- Map 3D : `list_od_tables`, `get_od_records` (par handle), `set_od_value`
- Systèmes de coordonnées : `get_coordinate_system`, `search_coordinate_systems`, `set_coordinate_system`
- SCU : `list_ucs`, `set_ucs`, `delete_ucs`, `convert_ucs_points` (les autres outils restent en SCG)
- Xref (références externes DWG) : `list_xrefs`, `attach_xref`, `detach_xrefs`, `reload_xrefs`, `unload_xrefs`, `bind_xrefs`, `edit_xref`
- Présentation : `list_layouts`, `set_current_layout`, `create_layout`, `rename_layout`, `delete_layout`, `list_viewports`, `create_viewport`, `set_viewport`
- Impression / PDF : `list_plot_devices`, `list_plot_styles`, `export_pdf`, `plot_drawing`
- Cadastre : `search_cadastre_address`, `import_cadastre` (IGN APICARTO, BAN, bâtiments BD TOPO en 2D ou 3D ; projection Lambert-93 en C# pur quand Map 3D est absent)
- Rendu : `list_materials`, `set_material`, `assign_material`, `list_lights`, `set_light`, `delete_lights`, `get_sun`, `set_sun` (calé sur le système de coordonnées), `get_view_ambiance`, `set_view_ambiance`
- FDO : `list_fdo_providers`, `list_fdo_connections`, `connect_fdo`, `describe_fdo`, `add_fdo_layer`, `list_fdo_layers`, `query_fdo`, `set_fdo_layer`, `remove_fdo_layer`, `disconnect_fdo`, `get_fdo_selection`
- Fichiers SIG (API ImportExport de Map 3D, comme MAPIMPORT et MAPEXPORT) : `import_gis`, `export_gis` (SHP, MapInfo MIF/TAB, GML, E00, DGN, SDF ; attributs en données d'objet)

## Construire et tester
- `scripts\install.ps1` construit et installe le plug-in (AutoCAD fermé) et le serveur ; `scripts\register.ps1` l'enregistre dans Claude Code et Claude Desktop.
- Tests unitaires : `dotnet test tests\McpMap3D.Tests` (projection Lambert-93, triangulation de Delaunay du maillage de terrain, calage du soleil).
- Tests de bout en bout, AutoCAD lancé avec une copie de dessin ouverte : `scripts\test-*.ps1` (`test-tools`, `test-editing`, `test-drafting`, `test-dimensions`, `test-modeling`, `test-layouts`, `test-plot`, `test-drawings`, `test-xrefs`, `test-scene`, `test-fdo`, `test-gis`, `test-batch`, `test-cadastre`, `test-mcp`, `test-pipe`, `smoke-test`). Ceux qui modifient le dessin annulent leurs modifications avec U. Les connexions FDO ne sont pas annulées par U : `test-fdo.ps1` les retire lui-même.
- Un nouvel outil s'accompagne de ses contrôles dans le script de test de sa famille, de sa ligne dans le README et d'une montée de version.

## Règles
- Pas d'outil d'exécution de LISP ou de commandes arbitraires.
- Tests uniquement sur des copies de fichiers DWG.

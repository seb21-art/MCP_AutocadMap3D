using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Import et export de fichiers SIG par l'API ImportExport de Map 3D (MAPIMPORT, MAPEXPORT).</summary>
[McpServerToolType]
public sealed class GisTools(PluginClient plugin)
{
    private const string FormatHelp =
        "Format Map 3D : SHP, MIF, MAPINFO (TAB), GML, E00, DGN ou SDF. Déduit de l'extension si omis.";

    private const string AttributesHelp =
        "object_data (par défaut) : les attributs passent par des données d'objet ; none : géométrie seule.";

    [McpServerTool(Name = "import_gis", Title = "Importer un fichier SIG",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Importe un fichier SIG (SHP, MapInfo MIF/TAB, GML, E00, DGN, SDF) en objets AutoCAD, comme " +
                 "MAPIMPORT : un calque par couche du fichier, attributs dans une nouvelle table de données d'objet " +
                 "(voir get_od_records), coordonnées converties du système du fichier (.prj) vers celui du dessin. " +
                 "preview liste les couches, colonnes et systèmes sans rien importer. Renvoie le nombre d'objets par " +
                 "type et par calque, l'étendue et les handles. Requiert Map 3D.")]
    public Task<string> ImportGis(
        [Description("Fichier à importer, chemin complet ou relatif au dossier du dessin.")] string filePath,
        [Description(FormatHelp)] string? format = null,
        [Description("Aperçu seulement : couches du fichier, colonnes et systèmes de coordonnées, sans import.")] bool preview = false,
        [Description("Calque AutoCAD de destination, créé au besoin. Par défaut le nom de chaque couche du fichier.")] string? layer = null,
        [Description(AttributesHelp)] string attributes = "object_data",
        [Description("Nom de la table de données d'objet (suffixé _1, _2… si elle existe). Par défaut le nom de la couche.")] string? odTable = null,
        [Description("Couches du fichier à importer (formats à plusieurs couches), jokers * et ? acceptés. Toutes si omis.")] string[]? inputLayers = null,
        [Description("Convertir vers le système de coordonnées du dessin quand les deux sont connus (vrai par défaut).")] bool transform = true,
        [Description("Polygones en polylignes fermées (vrai par défaut) ; sinon en MPolygon.")] bool polygonsAsClosedPolylines = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("import_gis",
            new { filePath, format, preview, layer, attributes, odTable, inputLayers, transform, polygonsAsClosedPolylines },
            busyTimeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);

    [McpServerTool(Name = "export_gis", Title = "Exporter vers un fichier SIG",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Exporte des objets du dessin vers un fichier SIG (SHP, MapInfo MIF/TAB, GML, E00, DGN, SDF), comme " +
                 "MAPEXPORT, avec leurs données d'objet en colonnes. Un fichier ne garde qu'une géométrie : points " +
                 "(points, blocs, textes), lignes (courbes) ou polygones (courbes fermées, MPolygon) ; une sélection " +
                 "mélangée demande geometry et un fichier par géométrie. Le système de coordonnées du dessin est " +
                 "écrit dans le fichier (.prj en SHP). Le dessin n'est pas modifié.")]
    public Task<string> ExportGis(
        [Description("Fichier à écrire, chemin complet ou relatif au dossier du dessin, avec son extension.")] string filePath,
        [Description(FormatHelp)] string? format = null,
        [Description("Handles des objets à exporter. Sinon tout l'espace objet, filtré par layers.")] string[]? handles = null,
        [Description("Calques à exporter, jokers * et ? acceptés.")] string[]? layers = null,
        [Description("auto (par défaut), point, line ou polygon. Les autres objets sont ignorés et comptés.")] string geometry = "auto",
        [Description(AttributesHelp)] string attributes = "object_data",
        [Description("Tables de données d'objet à exporter, jokers acceptés. Toutes celles des objets si omis.")] string[]? odTables = null,
        [Description("Système de coordonnées cible (code Map, voir search_coordinate_systems). Celui du dessin si omis.")] string? targetCoordinateSystem = null,
        [Description("SHP : 2d (par défaut) ou 3d pour garder les Z.")] string dimension = "2d",
        [Description("Remplacer un fichier existant (et ses fichiers compagnons .shx, .dbf, .prj…). Faux par défaut.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_gis",
            new { filePath, format, handles, layers, geometry, attributes, odTables, targetCoordinateSystem, dimension, overwrite },
            busyTimeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken);
}

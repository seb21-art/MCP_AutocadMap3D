using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Points topographiques et topologies de réseau de Map 3D.</summary>
[McpServerToolType]
public sealed class NetworkTools(PluginClient plugin)
{
    [McpServerTool(Name = "import_points", Title = "Importer des points topographiques",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Importe un fichier de points (CSV, TXT, XYZ) : un point AutoCAD, ou un bloc à attributs, par ligne. " +
                 "Séparateur détecté (; tabulation | , ou espaces), décimales à virgule acceptées, en-tête ignoré. " +
                 "Avec layerFromCode, chaque code va sur son calque (créé au besoin). odTable attache numéro, code et " +
                 "altitude en données d'objet. Les points servent ensuite à create_terrain_mesh (par leur calque). " +
                 "100 000 points au plus.")]
    public Task<string> ImportPoints(
        [Description("Chemin complet du fichier.")] string filePath,
        [Description("Ordre des colonnes : P numéro, X (ou E), Y (ou N), Z, D code ou description, - colonne ignorée. PXYZD par défaut.")] string? columns = null,
        [Description("Séparateur : un caractère, tab ou space. Détecté si omis.")] string? delimiter = null,
        [Description("Système des coordonnées du fichier, s'il diffère du dessin : lonlat, EPSG:n, code Autodesk.")] string? from = null,
        [Description("Calque des points ; avec layerFromCode, préfixe du nom des calques.")] string? layer = null,
        [Description("Un calque par code (colonne D), nommé préfixe + code.")] bool layerFromCode = false,
        [Description("Bloc à insérer à la place des points AutoCAD (voir list_blocks).")] string? block = null,
        [Description("Avec block : attributs remplis depuis les colonnes, par exemple {\"ALT\":\"Z\",\"NUM\":\"P\"}.")] JsonElement? attributes = null,
        [Description("Avec block : échelle du bloc.")] double? scale = null,
        [Description("Table de données d'objet où ranger NUMERO, CODE et ALTITUDE (créée si elle manque).")] string? odTable = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("import_points",
            new { filePath, columns, delimiter, from, layer, layerFromCode = layerFromCode ? true : (bool?)null, block, attributes, scale, odTable },
            busyTimeout: TimeSpan.FromSeconds(180), cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_network_topology", Title = "Créer une topologie de réseau",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une topologie de réseau Map 3D à partir des courbes du dessin (liens : conduites, câbles…) et, en " +
                 "option, de points ou blocs (nœuds : vannes, regards…). Les extrémités distantes de moins de tolerance " +
                 "sont reliées. Sert ensuite à trace_network. Map 3D seulement.")]
    public Task<string> CreateNetworkTopology(
        [Description("Nom de la topologie (lettres, chiffres, _ et -).")] string name,
        [Description("Calques des liens ; les jokers * et ? sont acceptés.")] string[]? layers = null,
        [Description("Types DXF des liens.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Handles des liens, au lieu d'un filtre.")] string[]? handles = null,
        [Description("Calques des points ou blocs qui servent de nœuds.")] string[]? nodeLayers = null,
        [Description("Distance en dessous de laquelle deux extrémités se rejoignent (0,01 par défaut).")] double? tolerance = null,
        [Description("Remplacer une topologie du même nom.")] bool replace = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_network_topology",
            new { name, layers, types, where, handles, nodeLayers, tolerance, replace = replace ? true : (bool?)null },
            busyTimeout: TimeSpan.FromSeconds(180), cancellationToken: cancellationToken);

    [McpServerTool(Name = "trace_network", Title = "Analyser un réseau",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Analyse une topologie de réseau : path (plus court chemin de start à end), flood (tout ce qui est relié " +
                 "à start, à moins de maxDistance), upstream (chemin de start vers la source), downstream (ce qui n'est " +
                 "plus alimenté depuis la source si start est coupé), isolated (ce que coupent les vannes closed). Les " +
                 "vannes fermées (closed) bloquent le passage. start, end, source et closed sont des points [x, y] ou des " +
                 "handles, rattachés au nœud le plus proche. Renvoie les handles des liens et des nœuds et la longueur ; " +
                 "select=true les sélectionne dans AutoCAD.")]
    public Task<string> TraceNetwork(
        [Description("Nom de la topologie.")] string topology,
        [Description("Analyse : path, flood, upstream, downstream ou isolated.")] string mode,
        [Description("Point de départ : [x, y] ou handle.")] JsonElement? start = null,
        [Description("Arrivée, pour path.")] JsonElement? end = null,
        [Description("Source (réservoir, poste), pour upstream, downstream et isolated.")] JsonElement? source = null,
        [Description("Nœuds fermés (vannes) : liste de points [x, y] ou de handles.")] JsonElement? closed = null,
        [Description("Pour flood : distance maximale le long du réseau.")] double? maxDistance = null,
        [Description("Sélectionner le résultat dans AutoCAD.")] bool select = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("trace_network",
            new { topology, mode, start, end, source, closed, maxDistance, select = select ? true : (bool?)null },
            busyTimeout: TimeSpan.FromSeconds(120), cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_topology", Title = "Supprimer une topologie",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Supprime une topologie Map 3D ; les objets du dessin restent.")]
    public Task<string> DeleteTopology(
        [Description("Nom de la topologie.")] string name,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_topology", new { name }, cancellationToken: cancellationToken);
}

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Connexions FDO de Map 3D : fournisseurs, sources, calques de la carte et sélection.</summary>
[McpServerToolType]
public sealed class FdoTools(PluginClient plugin)
{
    [McpServerTool(Name = "list_fdo_providers", Title = "Lister les fournisseurs FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Fournisseurs FDO installés (SDF, SHP, SQLite, WFS…). Si l'énumération Map échoue, le registre local est lu et un avertissement l'indique. Le nom technique s'utilise avec connect_fdo.")]
    public Task<string> ListProviders(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("list_fdo_providers", cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_fdo_connections", Title = "Lister les connexions FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sources créées par connect_fdo (Library://MCP_…), et celles déjà affichées par un calque. includeLibrary ajoute tout le dépôt. Les mots de passe ne sont pas renvoyés.")]
    public Task<string> ListConnections(
        [Description("Inclure tout le dépôt Library, pas seulement les sources créées par connect_fdo.")] bool includeLibrary = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_fdo_connections", new { includeLibrary }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "connect_fdo", Title = "Connecter une source FDO",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Teste une connexion et l'enregistre à la racine du dépôt (Library://MCP_nom), où MAPCONNECT la montre aussi. connectionString est une suite Clé=valeur. createFile crée un SDF vide, dans le système de coordonnées du dessin " +
                 "(longitude/latitude s'il n'en a pas), avec une classe (className), ses champs (fields) et son type de géométrie (geometry). " +
                 "Une source du même nom est refusée, sauf avec replace (et jamais si un calque l'affiche). " +
                 "Ne modifie pas les objets de la source (voir insert_fdo_features). La définition n'est pas annulée par U.")]
    public Task<string> Connect(
        [Description("Nom technique du fournisseur, tel que renvoyé par list_fdo_providers.")] string provider,
        [Description("Chaîne de connexion Clé=valeur;Clé=valeur. File=chemin pour un SDF.")] string? connectionString = null,
        [Description("Nom de la source (préfixé MCP_ dans le dépôt). Déduit du fichier si omis (« Source » sans fichier) : nommez les connexions aux bases de données.")] string? name = null,
        [Description("Créer un fichier SDF vide à l'emplacement File de la chaîne, puis le connecter.")] bool createFile = false,
        [Description("Remplacer une source existante du même nom, si aucun calque ne l'affiche. Faux par défaut.")] bool replace = false,
        [Description("Avec createFile : nom de la classe du SDF (Empty par défaut).")] string? className = null,
        [Description("Avec createFile : champs de la classe, en plus de l'identifiant automatique Id.")] SdfFieldInput[]? fields = null,
        [Description("Avec createFile : géométrie de la classe, point, line, polygon ou any (défaut).")] string? geometry = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("connect_fdo",
            new { provider, connectionString, name, createFile, replace, className, fields, geometry }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "describe_fdo", Title = "Décrire une source FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Schémas, classes, propriétés et contextes spatiaux d'une source FDO déjà connectée. " +
                 "resource est un identifiant (Library://…FeatureSource) ou le nom de la source.")]
    public Task<string> Describe(
        [Description("Identifiant ou nom de la source d'objets.")] string resource,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("describe_fdo", new { resource }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "add_fdo_layer", Title = "Ajouter un calque FDO",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Ajoute à la carte courante un calque affichant une classe d'une source déjà connectée. " +
                 "className est Schéma:Classe, tel que renvoyé par describe_fdo. Une classe d'image (WMS, raster) devient " +
                 "un calque raster ; pour un WMS, une source dédiée à la couche est créée comme le fait MAPCONNECT " +
                 "(dedicatedSource dans la réponse), avec l'image en PNG transparent reprojetée par Map 3D. Le calque FDO n'est " +
                 "pas un calque AutoCAD et n'est pas annulé par U : retirez-le avec remove_fdo_layer (disconnect retire aussi " +
                 "sa source dédiée).")]
    public Task<string> AddLayer(
        [Description("Identifiant ou nom de la source d'objets.")] string resource,
        [Description("Classe à afficher, Schéma:Classe.")] string className,
        [Description("Nom du calque dans la carte. Nom de la classe si omis.")] string? name = null,
        [Description("Propriété géométrique. Celle par défaut de la classe si omise.")] string? geometry = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("add_fdo_layer",
            new { resource, className, name, geometry }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_fdo_layers", Title = "Lister les calques FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Calques FDO de la carte courante : nom, visibilité, source, classe, géométrie et système de coordonnées.")]
    public Task<string> ListLayers(
        [Description("Filtre sur les noms, avec les jokers * et ?.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_fdo_layers", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "query_fdo", Title = "Interroger une classe FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lit des objets d'un calque FDO ou d'une classe de source connectée, sans les modifier. " +
                 "filter est un filtre FDO (FeatId = 12), pas une requête SQL. 50 objets au plus par défaut, 200 au maximum. " +
                 "Les géométries ne sont pas renvoyées, seulement leurs propriétés alphanumériques.")]
    public Task<string> Query(
        [Description("Nom du calque FDO. Ou bien resource et className.")] string? layer = null,
        [Description("Identifiant ou nom de la source, si layer est omis.")] string? resource = null,
        [Description("Classe Schéma:Classe, si layer est omis.")] string? className = null,
        [Description("Filtre FDO. Tous les objets si omis.")] string? filter = null,
        [Description("Nombre maximum d'objets, de 1 à 200.")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("query_fdo",
            new { layer, resource, className, filter, limit }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_fdo_layer", Title = "Modifier un calque FDO",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rend un calque FDO visible ou invisible, le renomme, ou cadre la vue dessus. Un calque raster (WMS), ou " +
                 "vectoriel que Map 3D ne sait pas cadrer (vide), est cadré sur l'étendue déclarée par sa source, limitée au " +
                 "domaine du système du dessin (zoom.method = spatialContext) ; un raster sans étendue exploitable l'est sur ce " +
                 "domaine (coordinateSystemDomain : la France pour un WMS national en Lambert-93) ; sinon la vue reste " +
                 "inchangée (none). set_view avec window ou center cadre une zone précise. " +
                 "N'est pas annulé par U.")]
    public Task<string> SetLayer(
        [Description("Nom du calque FDO.")] string name,
        [Description("Visible.")] bool? visible = null,
        [Description("Nouveau nom.")] string? rename = null,
        [Description("Cadrer la vue sur l'étendue du calque.")] bool zoom = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_fdo_layer",
            new { name, visible, rename, zoom }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "remove_fdo_layer", Title = "Retirer un calque FDO",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Retire un calque FDO de la carte. Ne supprime pas le fichier ni les objets de la source. " +
                 "disconnect retire aussi la définition de la source si plus aucun calque ne l'utilise, et seulement si elle " +
                 "a été créée par connect_fdo ou add_fdo_layer (Library://MCP_…) : une connexion faite dans Map 3D est toujours conservée " +
                 "(sourceKept l'indique). N'est pas annulé par U.")]
    public Task<string> RemoveLayer(
        [Description("Nom du calque FDO.")] string name,
        [Description("Retirer aussi la définition de la source devenue inutile, si elle a été créée par connect_fdo ou add_fdo_layer.")] bool disconnect = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("remove_fdo_layer", new { name, disconnect }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "disconnect_fdo", Title = "Déconnecter une source FDO",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Retire la définition d'une source créée par connect_fdo (Library://MCP_…), pas le fichier. Refuse une source " +
                 "encore affichée par un calque, et toute source qui n'a pas été créée par connect_fdo. N'est pas annulé par U.")]
    public Task<string> Disconnect(
        [Description("Identifiant ou nom de la source.")] string resource,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("disconnect_fdo", new { resource }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_fdo_selection", Title = "Lire la sélection FDO",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Objets FDO de la sélection courante d'AutoCAD, par calque. 50 objets au plus par calque par défaut.")]
    public Task<string> GetSelection(
        [Description("Nombre maximum d'objets par calque, de 1 à 200.")] int limit = 50,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_fdo_selection", new { limit }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "insert_fdo_features", Title = "Ajouter des objets à une source FDO",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Ajoute des objets dans une source FDO (SDF, SHP, SQLite, PostGIS…), pas dans le dessin. Chaque objet " +
                 "a des propriétés et une géométrie : wkt (dans le système de la source), ou handle d'un objet du dessin " +
                 "(point, bloc, ligne, polyligne, cercle), converti du système du dessin vers celui de la source. Les calques " +
                 "qui affichent la source sont redessinés. Map 3D enregistre la modification pour U. 1000 objets au plus.")]
    public Task<string> InsertFeatures(
        [Description("Objets à ajouter.")] FdoFeatureInput[] features,
        [Description("Source (identifiant ou nom) ; ou indiquez layer.")] string? resource = null,
        [Description("Classe Schéma:Classe ou Classe (voir describe_fdo).")] string? className = null,
        [Description("Calque FDO dont la source et la classe sont utilisées, à la place de resource et className.")] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("insert_fdo_features", new { features, resource, className, layer },
            busyTimeout: TimeSpan.FromSeconds(120), cancellationToken: cancellationToken);

    [McpServerTool(Name = "update_fdo_features", Title = "Modifier des objets d'une source FDO",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Modifie les objets d'une source FDO qui répondent au filtre : propriétés et, au besoin, géométrie (wkt ou " +
                 "handle, comme insert_fdo_features). Au-delà de 100 objets, expected doit donner leur nombre exact. " +
                 "Map 3D enregistre la modification pour U.")]
    public Task<string> UpdateFeatures(
        [Description("Filtre FDO, par exemple NOM = 'V12', DIAMETRE >= 100, ou FeatId = 3.")] string? filter = null,
        [Description("Nouvelles valeurs, par exemple {\"ETAT\":\"fermée\"}.")] JsonElement? properties = null,
        [Description("Nouvelle géométrie en WKT, dans le système de la source.")] string? wkt = null,
        [Description("Nouvelle géométrie prise sur un objet du dessin.")] string? handle = null,
        [Description("Source (identifiant ou nom) ; ou indiquez layer.")] string? resource = null,
        [Description("Classe Schéma:Classe ou Classe.")] string? className = null,
        [Description("Calque FDO, à la place de resource et className.")] string? layer = null,
        [Description("Tous les objets de la classe, sans filtre.")] bool all = false,
        [Description("Nombre d'objets attendu, obligatoire au-delà de 100.")] int? expected = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("update_fdo_features",
            new { filter, properties, wkt, handle, resource, className, layer, all = all ? true : (bool?)null, expected },
            busyTimeout: TimeSpan.FromSeconds(120), cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_fdo_features", Title = "Supprimer des objets d'une source FDO",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Supprime de la source FDO les objets qui répondent au filtre. Au-delà de 100 objets, expected doit " +
                 "donner leur nombre exact ; all=true vise toute la classe. Map 3D enregistre la suppression pour U.")]
    public Task<string> DeleteFeatures(
        [Description("Filtre FDO, par exemple FeatId = 3 ou ETAT = 'abandonné'.")] string? filter = null,
        [Description("Source (identifiant ou nom) ; ou indiquez layer.")] string? resource = null,
        [Description("Classe Schéma:Classe ou Classe.")] string? className = null,
        [Description("Calque FDO, à la place de resource et className.")] string? layer = null,
        [Description("Tous les objets de la classe, sans filtre.")] bool all = false,
        [Description("Nombre d'objets attendu, obligatoire au-delà de 100.")] int? expected = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_fdo_features",
            new { filter, resource, className, layer, all = all ? true : (bool?)null, expected },
            busyTimeout: TimeSpan.FromSeconds(120), cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_fdo_layer_style", Title = "Styler un calque FDO",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle le style d'un calque FDO vectoriel : couleur, contour, épaisseur, transparence, taille des points, " +
                 "analyse thématique (une couleur par valeur ou par plage d'une propriété, plus « Autres ») et étiquettes. " +
                 "themeProperty seul colore chaque valeur distincte (30 au plus). Remplace le style précédent. " +
                 "N'est pas annulé par U.")]
    public Task<string> SetLayerStyle(
        [Description("Nom du calque FDO.")] string name,
        [Description(ToolHelp.Color + " Remplissage (surfaces), trait (lignes) ou symbole (points), sans thème.")] string? color = null,
        [Description("Couleur du contour des surfaces et des symboles.")] string? outlineColor = null,
        [Description("Épaisseur des traits en mm (0,25 par défaut).")] double? lineWeight = null,
        [Description("Transparence du remplissage des surfaces, 0 à 100 % (60 par défaut).")] int? transparency = null,
        [Description("Taille des symboles de points, en points (6 par défaut).")] double? pointSize = null,
        [Description("Propriété de l'analyse thématique.")] string? themeProperty = null,
        [Description("Valeurs de l'analyse thématique, avec leur couleur et leur légende.")] ThemeValueInput[]? themeValues = null,
        [Description("Plages de l'analyse thématique : min inclus, max exclu.")] ThemeRangeInput[]? themeRanges = null,
        [Description("Couleur des objets hors des valeurs ou plages (gris par défaut).")] string? otherColor = null,
        [Description("Propriété affichée en étiquette.")] string? labelProperty = null,
        [Description("Hauteur des étiquettes en points (9 par défaut).")] double? labelHeight = null,
        [Description("Couleur des étiquettes (noir par défaut).")] string? labelColor = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_fdo_layer_style",
            new { name, color, outlineColor, lineWeight, transparency, pointSize, themeProperty, themeValues, themeRanges, otherColor, labelProperty, labelHeight, labelColor },
            busyTimeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
}

/// <summary>Champ d'un SDF créé par connect_fdo.</summary>
public sealed record SdfFieldInput(
    [property: Description("Nom du champ.")] string Name,
    [property: Description("Type : string (défaut), int, double, bool ou date.")] string? Type = null,
    [property: Description("Longueur maximale d'un texte (255 par défaut).")] int? Length = null);

/// <summary>Objet à ajouter dans une source FDO.</summary>
public sealed record FdoFeatureInput(
    [property: Description("Valeurs des propriétés, par exemple {\"NOM\":\"V12\",\"DIAMETRE\":110}.")] JsonElement? Properties = null,
    [property: Description("Géométrie en WKT, dans le système de la source, par exemple POINT (850000 6550000).")] string? Wkt = null,
    [property: Description("Ou handle d'un objet du dessin dont la géométrie est reprise.")] string? Handle = null);

/// <summary>Valeur d'une analyse thématique.</summary>
public sealed record ThemeValueInput(
    [property: Description("Valeur de la propriété.")] JsonElement Value,
    [property: Description("Couleur ; palette automatique si omise.")] string? Color = null,
    [property: Description("Légende ; la valeur si omise.")] string? Label = null);

/// <summary>Plage d'une analyse thématique.</summary>
public sealed record ThemeRangeInput(
    [property: Description("Minimum inclus.")] double? Min = null,
    [property: Description("Maximum exclu.")] double? Max = null,
    [property: Description("Couleur ; palette automatique si omise.")] string? Color = null,
    [property: Description("Légende.")] string? Label = null);

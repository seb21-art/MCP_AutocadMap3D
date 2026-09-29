using System.ComponentModel;
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
    [Description("Teste une connexion et l'enregistre à la racine du dépôt (Library://MCP_nom), où MAPCONNECT la montre aussi. connectionString est une suite Clé=valeur. createFile crée un SDF vide. " +
                 "Une source du même nom est refusée, sauf avec replace (et jamais si un calque l'affiche). " +
                 "Ne modifie pas les objets de la source. La définition n'est pas annulée par U.")]
    public Task<string> Connect(
        [Description("Nom technique du fournisseur, tel que renvoyé par list_fdo_providers.")] string provider,
        [Description("Chaîne de connexion Clé=valeur;Clé=valeur. File=chemin pour un SDF.")] string? connectionString = null,
        [Description("Nom de la source (préfixé MCP_ dans le dépôt). Déduit du fichier si omis (« Source » sans fichier) : nommez les connexions aux bases de données.")] string? name = null,
        [Description("Créer un fichier SDF vide à l'emplacement File de la chaîne, puis le connecter.")] bool createFile = false,
        [Description("Remplacer une source existante du même nom, si aucun calque ne l'affiche. Faux par défaut.")] bool replace = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("connect_fdo",
            new { provider, connectionString, name, createFile, replace }, cancellationToken: cancellationToken);

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
}

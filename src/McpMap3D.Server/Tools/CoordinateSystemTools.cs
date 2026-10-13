using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Système de coordonnées du dessin et catalogue des systèmes de Map 3D.</summary>
[McpServerToolType]
public sealed class CoordinateSystemTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_coordinate_system", Title = "Système de coordonnées du dessin",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Indique le système de coordonnées attribué au dessin (code Autodesk, description, code EPSG, " +
                 "unités, domaine de validité) et si l'étendue du dessin est compatible avec ce système.")]
    public Task<string> GetCoordinateSystem(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("get_coordinate_system", cancellationToken: cancellationToken);

    [McpServerTool(Name = "search_coordinate_systems", Title = "Chercher un système de coordonnées",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Cherche dans le catalogue des systèmes de coordonnées de Map 3D, par mots (« Lambert 93 », " +
                 "« RGF93 CC46 », « UTM 31N », « WGS84 »), par code Autodesk ou par numéro EPSG (2154 pour " +
                 "Lambert-93). Pour chaque résultat, « compatible » indique si l'étendue du dessin tombe dans le " +
                 "domaine de validité du système, ce qui aide à identifier le système des coordonnées existantes. " +
                 "La première recherche indexe le catalogue et peut prendre quelques secondes.")]
    public Task<string> SearchCoordinateSystems(
        [Description("Mots-clés, code Autodesk ou numéro EPSG.")] string query,
        [Description("Nombre maximal de résultats, 20 par défaut, 100 au plus.")] int limit = 20,
        [Description("Ne garder que les systèmes compatibles avec l'étendue du dessin.")] bool compatibleOnly = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("search_coordinate_systems", new { query, limit, compatibleOnly },
            busyTimeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);

    [McpServerTool(Name = "convert_coordinates", Title = "Convertir des coordonnées",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Convertit des points d'un système de coordonnées à un autre, sans modifier le dessin : par exemple " +
                 "une longitude/latitude (GPS, search_cadastre_address) vers le système du dessin pour y placer un " +
                 "objet, ou des coordonnées CC46 vers Lambert-93. Le changement de datum est pris en compte ; " +
                 "l'altitude est recopiée telle quelle. Sans Map 3D : entre lonlat et Lambert-93 seulement.")]
    public Task<string> ConvertCoordinates(
        [Description("Points [[x,y],…] ou [[x,y,z],…], 1000 au plus. En longitude/latitude : [longitude, latitude].")] double[][] points,
        [Description("Système d'origine : drawing (système du dessin), lonlat (WGS84), EPSG:n ou numéro EPSG, ou code " +
                     "Autodesk (voir search_coordinate_systems).")] string from,
        [Description("Système d'arrivée, mêmes valeurs que from.")] string to,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("convert_coordinates", new { points, from, to },
            busyTimeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);

    [McpServerTool(Name = "reproject_entities", Title = "Reprojeter des objets",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Déplace des objets du dessin d'un système de coordonnées à un autre, changement de datum compris, par " +
                 "exemple de CC46 vers Lambert-93 (set_coordinate_system, lui, ne déplace rien). Les sommets des lignes, " +
                 "polylignes, splines et points sont convertis un à un ; cercles, arcs, textes, blocs, hachures et cotes " +
                 "sont déplacés, tournés et mis à l'échelle selon la déformation locale de la projection. Les altitudes " +
                 "ne changent pas. Tout l'espace objet par défaut, ou les objets filtrés. assign=true attribue ensuite le " +
                 "système d'arrivée au dessin.")]
    public Task<string> ReprojectEntities(
        [Description("Système d'arrivée : code Autodesk, EPSG:n ou numéro EPSG, lonlat.")] string to,
        [Description("Système d'origine : drawing (défaut, système du dessin), code, EPSG ou lonlat.")] string? from = null,
        [Description("Attribuer au dessin le système d'arrivée, une fois les objets reprojetés.")] bool assign = false,
        [Description("Calques à retenir ; les jokers * et ? sont acceptés. Tous si omis.")] string[]? layers = null,
        [Description("Types DXF à retenir. Tous si omis.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Handles d'objets précis, au lieu de tout l'espace objet.")] string[]? handles = null,
        [Description("Objets sélectionnés par l'utilisateur dans AutoCAD, au lieu de tout l'espace objet.")] bool selection = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("reproject_entities",
            new { to, from, assign = assign ? true : (bool?)null, layers, types, where, handles, selection = selection ? true : (bool?)null },
            busyTimeout: TimeSpan.FromSeconds(120), cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_coordinate_system", Title = "Attribuer un système de coordonnées au dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Attribue au dessin un système de coordonnées (code Autodesk, trouvé avec " +
                 "search_coordinate_systems). Les objets ne sont pas déplacés : l'outil déclare dans quel système " +
                 "sont exprimées les coordonnées existantes. Il refuse un système dont le domaine de validité ne " +
                 "contient pas l'étendue du dessin, sauf avec force=true. Une chaîne vide retire le système.")]
    public Task<string> SetCoordinateSystem(
        [Description("Code Autodesk du système, par exemple celui renvoyé par search_coordinate_systems ; chaîne vide pour retirer le système.")]
        string code,
        [Description("Attribuer même si l'étendue du dessin sort du domaine du système (dessin pas encore géoréférencé, par exemple).")]
        bool force = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_coordinate_system", new { code, force }, cancellationToken: cancellationToken);
}

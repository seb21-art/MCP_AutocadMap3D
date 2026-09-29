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

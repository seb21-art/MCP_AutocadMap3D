using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Mesures sans modification du dessin : intersections, mesures le long d'une courbe.</summary>
[McpServerToolType]
public sealed class MeasureTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_intersections", Title = "Calculer des intersections",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Calcule les points d'intersection entre objets (chaque paire), sans rien modifier : croisement de " +
                 "deux axes, d'une limite et d'un tracé, d'un cercle et d'une ligne. Vue de dessus par défaut, même " +
                 "si les objets sont à des altitudes différentes ; points donnés sur le premier objet de la paire. " +
                 "Avec extend both, les lignes, arcs et courbes ouvertes sont prolongés.")]
    public Task<string> GetIntersections(
        [Description("Handles des objets, de 2 à 200.")] string[] handles,
        [Description("none (objets tels quels, par défaut) ou both (objets prolongés).")] string? extend = null,
        [Description("Intersections vues de dessus (vrai par défaut) ; faux pour des intersections réelles en 3D.")] bool plan = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_intersections", new { handles, extend, plan }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "measure_curve", Title = "Mesurer le long d'une courbe",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Mesure une courbe (ligne, polyligne, arc, cercle, spline, ellipse) sans la modifier : longueur, " +
                 "extrémités, aire si elle est fermée, et au choix :\n" +
                 "- distances : point et direction de la tangente à ces distances depuis le début ;\n" +
                 "- points : abscisse curviligne et décalage de points (distance depuis le début du point le plus " +
                 "proche de la courbe vue de dessus, et distance à la courbe, positive à gauche du sens de parcours), " +
                 "comme un PK et un déport le long d'un axe ;\n" +
                 "- divide : points divisant la courbe en parties égales (commande DIVISER) ;\n" +
                 "- interval : points tous les « interval » depuis le début (commande MESURER).\n" +
                 "Angles en degrés, 0 vers +X, sens trigonométrique.")]
    public Task<string> MeasureCurve(
        [Description("Handle de la courbe.")] string handle,
        [Description("Distances depuis le début de la courbe (sur une courbe fermée, au-delà de la longueur, on fait le tour).")] double[]? distances = null,
        [Description("Points dont on veut l'abscisse et le décalage, [[x, y], …].")] double[][]? points = null,
        [Description("Nombre de parties égales (2 ou plus).")] int? divide = null,
        [Description("Intervalle entre deux points, depuis le début.")] double? interval = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("measure_curve",
            new { handle, distances, points, divide, interval }, cancellationToken: cancellationToken);
}

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Systèmes de coordonnées utilisateur (SCU). Les autres outils prennent et renvoient toujours des coordonnées
/// générales (SCG) : convert_ucs_points passe d'un repère à l'autre.
/// </summary>
[McpServerToolType]
public sealed class UcsTools(PluginClient plugin)
{
    private const string WcsNote =
        " Les autres outils restent en coordonnées générales (SCG), quel que soit le SCU courant : pour dessiner dans " +
        "un SCU, convertissez d'abord ses coordonnées avec convert_ucs_points.";

    [McpServerTool(Name = "list_ucs", Title = "SCU courant et SCU nommés",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Donne le SCU (système de coordonnées utilisateur) courant de l'espace actif : origine et axes X, Y, " +
                 "Z dans le SCG, s'il s'agit du SCG, et les SCU nommés de mêmes origine et axes. Liste aussi les SCU " +
                 "nommés du dessin." + WcsNote)]
    public Task<string> ListUcs(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("list_ucs", cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_ucs", Title = "Définir le SCU",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Définit le SCU courant de l'espace actif, comme la commande SCU, et peut l'enregistrer sous un nom. " +
                 "Un seul mode à la fois : preset (SCG ou SCU orthogonal), name (SCU nommé), points (3 points), " +
                 "xAxis/yAxis/zAxis (axes), handle (aligné sur un objet), ou origin seule (SCU courant déplacé). Une " +
                 "rotation rotateAngle autour d'un axe du SCU obtenu (ou du SCU courant) peut s'ajouter. Coordonnées " +
                 "et vecteurs en SCG." + WcsNote)]
    public Task<string> SetUcs(
        [Description("SCU prédéfini : world (SCG), top, bottom, front, back, left, right (SCU orthogonaux d'AutoCAD ; " +
                     "front : X = +X, Y = +Z, plan XZ vu de face).")] string? preset = null,
        [Description("Nom d'un SCU nommé à restaurer (voir list_ucs).")] string? name = null,
        [Description("Trois points [x,y,z] : origine, point sur l'axe X positif, point du plan XY côté Y positif.")]
        double[][]? points = null,
        [Description("Direction de l'axe X [x,y,z], avec yAxis ou zAxis.")] double[]? xAxis = null,
        [Description("Direction de l'axe Y [x,y,z], rendue perpendiculaire à X.")] double[]? yAxis = null,
        [Description("Direction de l'axe Z [x,y,z]. Seule, X est choisi comme AutoCAD le fait pour un plan de cette normale.")]
        double[]? zAxis = null,
        [Description("Handle d'un objet plan sur lequel aligner le SCU : ligne (origine au départ, X le long de la ligne), " +
                     "cercle, arc, ellipse (origine au centre), polyligne ou courbe plane (origine au départ, X le long du " +
                     "premier segment), bloc (point d'insertion et axes du bloc), texte.")] string? handle = null,
        [Description("Origine [x,y,z] en SCG. Avec preset, axes ou handle : remplace l'origine ; seule : déplace le SCU courant.")]
        double[]? origin = null,
        [Description("Axe de rotation du SCU, x, y ou z (z par défaut), passant par son origine.")] string? rotateAxis = null,
        [Description("Angle de rotation en degrés autour de rotateAxis, sens trigonométrique vu depuis le bout de l'axe.")]
        double? rotateAngle = null,
        [Description("Enregistrer le SCU obtenu sous ce nom (un SCU nommé existant est remplacé).")] string? saveAs = null,
        [Description("Rendre le SCU courant (vrai par défaut). À faux avec saveAs : définit un SCU nommé sans changer le courant.")]
        bool setCurrent = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_ucs",
            new { preset, name, points, xAxis, yAxis, zAxis, handle, origin, rotateAxis, rotateAngle, saveAs, setCurrent },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_ucs", Title = "Supprimer des SCU nommés",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime des SCU nommés du dessin. Le SCU courant n'est pas modifié.")]
    public Task<string> DeleteUcs(
        [Description("Noms des SCU nommés à supprimer (voir list_ucs).")] string[] names,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_ucs", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "convert_ucs_points", Title = "Convertir des points entre SCU et SCG",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Convertit des points d'un repère à l'autre : SCG (world), SCU courant (current) ou SCU nommé. Par " +
                 "défaut, du SCU courant vers le SCG, pour passer aux autres outils des coordonnées saisies dans le " +
                 "SCU. Le dessin n'est pas modifié.")]
    public Task<string> ConvertUcsPoints(
        [Description("Points [[x,y,z], …] ([x,y] : z = 0), 10 000 au plus.")] double[][] points,
        [Description("Repère des points donnés : world, current (par défaut) ou nom d'un SCU nommé.")] string from = "current",
        [Description("Repère des points renvoyés : world (par défaut), current ou nom d'un SCU nommé.")] string to = "world",
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("convert_ucs_points", new { points, from, to }, cancellationToken: cancellationToken);
}

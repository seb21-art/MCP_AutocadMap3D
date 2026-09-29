using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Surfaces, solides sculptés dans des surfaces et projection de courbes.</summary>
public sealed partial class ModelingTools
{
    [McpServerTool(Name = "create_surface", Title = "Créer des surfaces",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée des surfaces 3D. planar : surfaces planes dans des contours fermés (courbes qui se touchent " +
                 "bout à bout, ou régions), ou rectangle horizontal donné par deux coins, comme SURFPLANE. network : " +
                 "surface passant par un réseau de courbes croisées, comme SURFRESEAU. nurbs : surface NURBS définie " +
                 "par une grille de points de contrôle, qui passe par les quatre coins de la grille et suit ses bords. " +
                 "offset : surfaces parallèles à des surfaces existantes, comme SURFDECALER. Une surface s'épaissit en " +
                 "solide avec thicken_surface, et des surfaces qui enferment un volume en font un solide avec " +
                 "sculpt_solid.")]
    public Task<string> CreateSurface(
        [Description("Type : planar, network, nurbs ou offset.")] string type,
        [Description("planar : handles des courbes formant des contours fermés, ou de régions. offset : handles des " +
                     "surfaces à décaler.")] string[]? handles = null,
        [Description("planar, à la place de handles : deux coins opposés d'un rectangle horizontal, " +
                     "[[x1, y1, z], [x2, y2, z]].")] double[][]? corners = null,
        [Description("network : handles des courbes d'une direction, dans leur ordre (deux au moins).")] string[]? uCurves = null,
        [Description("network : handles des courbes de l'autre direction, dans leur ordre, chacune croisant toutes " +
                     "les courbes de uCurves (deux au moins).")] string[]? vCurves = null,
        [Description("nurbs : grille de points de contrôle en rangées de même longueur, 2 × 2 au moins, par exemple " +
                     "[[[0,0,0],[10,0,0],[20,0,0]],[[0,10,0],[10,10,5],[20,10,0]],[[0,20,0],[10,20,0],[20,20,0]]] " +
                     "pour une bosse. Les rangées se succèdent dans la direction U, les points d'une rangée dans la " +
                     "direction V.")] double[][][]? controlPoints = null,
        [Description("nurbs : degré dans la direction U, de 1 au nombre de rangées moins 1 (3 au plus par défaut).")] int? degreeU = null,
        [Description("nurbs : degré dans la direction V, de 1 au nombre de points par rangée moins 1 (3 au plus par " +
                     "défaut).")] int? degreeV = null,
        [Description("offset : distance de décalage, du côté de la normale de la surface si positive.")] double? distance = null,
        [Description("planar et network : supprimer les courbes ou régions d'origine.")] bool eraseSources = false,
        [Description(LayerHelp + " Par défaut : calque de l'objet d'origine.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_surface",
            new { type, handles, corners, uCurves, vCurves, controlPoints, degreeU, degreeV, distance, eraseSources, layer, color },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "sculpt_solid", Title = "Sculpter un solide dans des surfaces",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un solide 3D à partir de surfaces (et de solides) qui enferment complètement un volume, comme " +
                 "la commande SCULPTER : les surfaces sont ajustées à leurs intersections. Par exemple un tube de " +
                 "surface extrudée fermé par deux surfaces planes (create_surface, planar).")]
    public Task<string> SculptSolid(
        [Description("Handles des surfaces et solides qui enferment le volume.")] string[] handles,
        [Description("Supprimer les surfaces et solides d'origine.")] bool eraseSources = false,
        [Description(LayerHelp + " Par défaut : calque du premier objet.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("sculpt_solid", new { handles, eraseSources, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "project_curves", Title = "Projeter des courbes sur un solide ou une surface",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Projette des courbes ou des points sur un solide ou une surface, verticalement par défaut, comme la " +
                 "commande PROJECTGEOMETRY : tracé d'une limite ou d'un axe sur un terrain modélisé, contour à marquer " +
                 "sur une face avant imprint_solid. Les courbes obtenues sont posées sur la cible, qui reste intacte ; " +
                 "les objets qui ne la rencontrent pas sont listés dans notProjected.")]
    public Task<string> ProjectCurves(
        [Description("Handles des courbes ou points à projeter.")] string[] handles,
        [Description("Handle du solide ou de la surface cible.")] string target,
        [Description("Direction de projection [x, y, z], [0, 0, -1] par défaut (vers le bas).")] double[]? direction = null,
        [Description("Supprimer les objets qui ont été projetés.")] bool eraseSources = false,
        [Description(LayerHelp + " Par défaut : calque de l'objet projeté.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("project_curves",
            new { handles, target, direction, eraseSources, layer, color }, cancellationToken: cancellationToken);
}

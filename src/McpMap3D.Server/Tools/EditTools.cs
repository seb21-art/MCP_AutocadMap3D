using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Outils de modification du dessin. Chaque appel est annulable en une seule étape
/// avec la commande U d'AutoCAD.
/// </summary>
[McpServerToolType]
public sealed class EditTools(PluginClient plugin)
{
    [McpServerTool(Name = "create_layer", Title = "Créer ou modifier un calque",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Crée un calque, ou met à jour ses propriétés s'il existe déjà.")]
    public Task<string> CreateLayer(
        [Description("Nom du calque.")] string name,
        [Description(ToolHelp.Color + " Inchangée si omise.")] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight + " ByLayer et ByBlock sont exclus pour un calque.")] string? lineWeight = null,
        [Description("Description du calque.")] string? description = null,
        [Description("Éteindre le calque.")] bool? off = null,
        [Description("Verrouiller le calque.")] bool? locked = null,
        [Description("Rendre ce calque courant.")] bool current = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_layer",
            new { name, color, linetype, lineWeight, description, off, locked, current }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_line", Title = "Créer une ligne",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une ligne dans l'espace courant et renvoie son handle : segment (par défaut), droite infinie " +
                 "(XLINE, ligne de construction) ou demi-droite (RAY) passant par start et end.")]
    public Task<string> CreateLine(
        [Description("Point de départ, [x, y] ou [x, y, z], dans les coordonnées du dessin.")] double[] start,
        [Description("Point d'arrivée, [x, y] ou [x, y, z] ; pour xline et ray, second point donnant la direction.")] double[] end,
        [Description("segment (par défaut), xline (droite infinie dans les deux sens) ou ray (demi-droite partant de start).")] string? kind = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_line", new { start, end, kind, layer, color, linetype, lineWeight }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_polyline", Title = "Créer une polyligne",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une polyligne (LWPOLYLINE) plane dans l'espace courant et renvoie son handle. Elle peut être " +
                 "tracée dans n'importe quel plan : points 3D coplanaires (normale calculée), plan xz ou yz en " +
                 "coordonnées locales, ou normale imposée.")]
    public Task<string> CreatePolyline(
        [Description(ToolHelp.PlanarPoints)] double[][] points,
        [Description("Fermer la polyligne.")] bool closed = false,
        [Description("Largeur constante des segments.")] double width = 0,
        [Description(ToolHelp.Plane)] string? plane = null,
        [Description(ToolHelp.PlaneOffset)] double? planeOffset = null,
        [Description(ToolHelp.Normal)] double[]? normal = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_polyline",
            new { points, closed, width, plane, planeOffset, normal, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_text", Title = "Créer un texte",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un texte sur une ligne (TEXT) dans l'espace courant et renvoie son handle.")]
    public Task<string> CreateText(
        [Description("Contenu du texte.")] string text,
        [Description("Point d'insertion, [x, y] ou [x, y, z].")] double[] position,
        [Description("Hauteur du texte dans les unités du dessin (2,5 par défaut).")] double height = 2.5,
        [Description("Rotation en degrés.")] double rotation = 0,
        [Description("Style de texte existant (voir list_text_styles) ; son facteur de largeur et son inclinaison " +
                     "s'appliquent, comme avec la commande TEXTE.")] string? style = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_text",
            new { text, position, height, rotation, style, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "move_entities", Title = "Déplacer des objets",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Déplace des objets désignés par leur handle, d'un vecteur ou d'un point à un autre.")]
    public Task<string> MoveEntities(
        [Description("Handles des objets, tels que renvoyés par list_entities.")] string[] handles,
        [Description("Vecteur de déplacement [dx, dy] ou [dx, dy, dz]. À défaut, utilisez from et to.")] double[]? displacement = null,
        [Description("Point de départ du déplacement.")] double[]? from = null,
        [Description("Point d'arrivée du déplacement.")] double[]? to = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("move_entities",
            new { handles, displacement, from, to }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "change_layer", Title = "Changer des objets de calque",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Déplace des objets vers un autre calque, qui doit exister.")]
    public Task<string> ChangeLayer(
        [Description("Handles des objets, tels que renvoyés par list_entities.")] string[] handles,
        [Description("Calque de destination.")] string layer,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("change_layer", new { handles, layer }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_entity_properties", Title = "Changer couleur, type et épaisseur de ligne",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change les propriétés graphiques d'objets existants : couleur, type de ligne et son échelle, " +
                 "épaisseur de ligne (trait tracé, en mm) et épaisseur d'extrusion (hauteur donnée à un objet 2D : " +
                 "lignes, cercles, arcs, polylignes, textes, points). Seules les propriétés fournies changent. " +
                 "Pour modifier un calque entier, utilisez plutôt create_layer.")]
    public Task<string> SetEntityProperties(
        [Description("Handles des objets, tels que renvoyés par list_entities.")] string[] handles,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description("Échelle du type de ligne propre à l'objet (1 par défaut dans AutoCAD).")] double? linetypeScale = null,
        [Description(ToolHelp.LineWeight + " À l'écran, les épaisseurs ne s'affichent que si l'affichage des " +
                     "épaisseurs de ligne est activé (LWDISPLAY).")] string? lineWeight = null,
        [Description("Épaisseur d'extrusion (Thickness), dans les unités du dessin : hauteur donnée à un objet 2D, " +
                     "négative vers le bas. À ne pas confondre avec l'épaisseur de ligne.")] double? thickness = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_entity_properties",
            new { handles, color, linetype, linetypeScale, lineWeight, thickness }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "erase_entities", Title = "Supprimer des objets",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime du dessin les objets désignés par leur handle. Annulable en une étape avec U, " +
                 "mais confirmez la liste avec l'utilisateur avant d'appeler cet outil.")]
    public Task<string> EraseEntities(
        [Description("Handles des objets à supprimer, tels que renvoyés par list_entities.")] string[] handles,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("erase_entities", new { handles }, cancellationToken: cancellationToken);
}

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Formes 2D : points, polygones réguliers, nuages de révision, masques, contours et tableaux.</summary>
public sealed partial class DraftingTools
{
    private const string ContourPoints =
        "Sommets du contour, [[x,y],…] (fermé automatiquement). À la place : corner1 et corner2 pour un rectangle.";

    [McpServerTool(Name = "create_point", Title = "Créer des points",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un ou plusieurs points (POINT) : repères, points levés, sommets à marquer. Leur aspect et leur " +
                 "taille sont des réglages communs à tout le dessin (PDMODE, PDSIZE) : les changer modifie aussi les " +
                 "points existants.")]
    public Task<string> CreatePoint(
        [Description("Positions, [[x, y], …] ou [[x, y, z], …] ; 10 000 au maximum.")] double[][] points,
        [Description("Aspect des points (PDMODE) : forme 0 (point), 1 (rien), 2 (+), 3 (x) ou 4 (trait), plus 32 " +
                     "(cercle), 64 (carré) ou 96 (cercle et carré). Exemples : 3 (croix), 34 (cercle et +), 35.")] int? pointStyle = null,
        [Description("Taille des points (PDSIZE) : positive en unités du dessin, négative en pourcentage de l'écran, " +
                     "0 pour 5 % de l'écran.")] double? pointSize = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_point",
            new { points, pointStyle, pointSize, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_polygon", Title = "Créer un polygone régulier",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un polygone régulier (polyligne fermée), comme la commande POLYGONE : par centre et rayon, " +
                 "inscrit (rayon jusqu'aux sommets) ou circonscrit (rayon jusqu'au milieu des côtés), côté du bas " +
                 "horizontal par défaut ; ou par un côté edgeStart→edgeEnd, le polygone étant à gauche de ce côté. " +
                 "Renvoie centre, rayons, côté, périmètre et aire.")]
    public Task<string> CreatePolygon(
        [Description("Nombre de côtés, de 3 à 1024.")] int sides,
        [Description("Centre, [x, y] ou [x, y, z].")] double[]? center = null,
        [Description("Rayon, jusqu'aux sommets (inscribed) ou jusqu'au milieu des côtés (circumscribed).")] double? radius = null,
        [Description("inscribed (par défaut) ou circumscribed.")] string? mode = null,
        [Description("Rotation en degrés autour du centre (0 : côté du bas horizontal).")] double? rotation = null,
        [Description("À la place de center et radius : début d'un côté.")] double[]? edgeStart = null,
        [Description("Fin de ce côté ; le polygone est construit à sa gauche (sens trigonométrique).")] double[]? edgeEnd = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_polygon",
            new { sides, center, radius, mode, rotation, edgeStart, edgeEnd, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_revision_cloud", Title = "Créer un nuage de révision",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un nuage de révision (polyligne fermée en festons), comme la commande REVCLOUD, autour d'un " +
                 "contour : sommets, rectangle, ou courbe fermée existante (conservée). Sert à signaler une zone " +
                 "modifiée.")]
    public Task<string> CreateRevisionCloud(
        [Description(ContourPoints)] double[][]? points = null,
        [Description("Premier coin du rectangle.")] double[]? corner1 = null,
        [Description("Coin opposé du rectangle.")] double[]? corner2 = null,
        [Description("À la place : handle d'une courbe fermée horizontale (polyligne, cercle…), qui reste en place.")] string? handle = null,
        [Description("Longueur approximative de chaque feston (par défaut, 1/40 du périmètre).")] double? arcLength = null,
        [Description("Festons tournés vers l'intérieur (vers l'extérieur par défaut).")] bool inward = false,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_revision_cloud",
            new { points, corner1, corner2, handle, arcLength, inward, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_wipeout", Title = "Créer un masque",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un masque (WIPEOUT) : polygone de la couleur du fond qui cache les objets dessinés avant lui, " +
                 "par exemple sous une légende ou un cartouche. Les objets créés ensuite restent visibles par-dessus ; " +
                 "set_draw_order règle l'ordre.")]
    public Task<string> CreateWipeout(
        [Description(ContourPoints)] double[][]? points = null,
        [Description("Premier coin du rectangle.")] double[]? corner1 = null,
        [Description("Coin opposé du rectangle.")] double[]? corner2 = null,
        [Description("Cadre des masques, réglage commun à tout le dessin (WIPEOUTFRAME) : shown, hidden ou " +
                     "shown_not_plotted. Inchangé si omis.")] string? frame = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_wipeout",
            new { points, corner1, corner2, frame, layer }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_boundary", Title = "Créer un contour autour d'un point",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée le contour fermé qui entoure un point, comme la commande CONTOUR : AutoCAD suit les objets " +
                 "(lignes, arcs, polylignes, cercles… même s'ils se croisent) qui délimitent la zone, vue de dessus. " +
                 "Renvoie aires et périmètre ; le contour peut ensuite être hachuré (create_hatch). AutoCAD ne voit " +
                 "que les objets affichés : si rien n'est trouvé, la vue passe un instant en vue de dessus sur tout " +
                 "le dessin, puis de plus en plus près du point, et revient.")]
    public Task<string> CreateBoundary(
        [Description("Point intérieur à la zone, [x, y] ou [x, y, z].")] double[] point,
        [Description("polyline (par défaut, une polyligne par boucle) ou region (une région, îlots soustraits).")] string? type = null,
        [Description("Détecter les îlots (contours intérieurs), vrai par défaut.")] bool detectIslands = true,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_boundary",
            new { point, type, detectIslands, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_table", Title = "Créer un tableau",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un tableau (TABLE) : titre fusionné, ligne d'en-têtes et lignes de données, pour une " +
                 "nomenclature, un tableau de surfaces ou une légende. Largeurs de colonnes estimées d'après le " +
                 "contenu, lignes agrandies si un texte passe à la ligne. Les cellules se modifient ensuite avec " +
                 "edit_text.")]
    public Task<string> CreateTable(
        [Description("Coin supérieur gauche du tableau, [x, y] ou [x, y, z].")] double[] position,
        [Description("Lignes de données, par exemple [[\"Parcelle AB 12\", \"1 250 m²\"], [\"Parcelle AB 13\", 980]]. " +
                     "Nombres écrits tels quels ; envoyez une chaîne pour un autre format (virgule décimale, unité).")] JsonElement[][]? rows = null,
        [Description("En-têtes de colonnes.")] string[]? headers = null,
        [Description("Titre, sur une ligne fusionnée au-dessus.")] string? title = null,
        [Description("Hauteur du texte (2,5 par défaut) ; le titre est 1,4 fois plus haut.")] double? textHeight = null,
        [Description("Largeur des colonnes : une valeur pour toutes, ou une par colonne. Estimée d'après le contenu si omise.")] double[]? columnWidths = null,
        [Description("Hauteur des lignes ; AutoCAD l'augmente si le texte ne tient pas. Hauteur du texte plus les marges si omise.")] double? rowHeight = null,
        [Description("Alignement des données : une valeur pour toutes les colonnes ou une par colonne, parmi top_left, " +
                     "top_center, top_right, middle_left, middle_center, middle_right, bottom_left, bottom_center, " +
                     "bottom_right. Par exemple [\"middle_left\", \"middle_right\"] pour des libellés et des nombres.")] string[]? alignment = null,
        [Description("Style de tableau existant ; style courant si omis.")] string? style = null,
        [Description("Style de texte des cellules (voir list_text_styles) ; celui du style de tableau si omis.")] string? textStyle = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_table",
            new { position, rows, headers, title, textHeight, columnWidths, rowHeight, alignment, style, textStyle, layer, color },
            cancellationToken: cancellationToken);
}

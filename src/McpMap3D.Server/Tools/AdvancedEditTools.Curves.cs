using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Jonction, coupure, sommets de polyligne, étirement, ordre d'affichage, réseau le long d'un chemin.</summary>
public sealed partial class AdvancedEditTools
{
    [McpServerTool(Name = "join_entities", Title = "Joindre des courbes en polylignes",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Joint des lignes, arcs et polylignes 2D ouvertes dont les extrémités se touchent (vus de dessus, même " +
                 "altitude) en polylignes, comme la commande JOINDRE : les morceaux sont enchaînés quel que soit leur " +
                 "ordre ou leur sens, et une chaîne qui se referme donne une polyligne fermée, avec son aire. Une " +
                 "polyligne de la chaîne est conservée (handle et données d'objet), les autres objets sont supprimés.")]
    public Task<string> JoinEntities(
        [Description(HandlesHelp)] string[] handles,
        [Description("Écart maximal entre deux extrémités à joindre (1e-6 par défaut) ; à augmenter pour combler de " +
                     "petits écarts, par exemple dans des données importées.")] double? tolerance = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("join_entities", new { handles, tolerance }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "break_entities", Title = "Couper une courbe",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Coupe une courbe comme la commande COUPER : en un point (deux morceaux, sans rien supprimer), ou " +
                 "entre deux points (la partie entre eux est supprimée). Les points sont ramenés sur la courbe vue " +
                 "de dessus. Sur une courbe fermée, il faut deux points : la partie supprimée va de point à point2 " +
                 "dans le sens de la courbe (antihoraire pour un cercle, qui devient un arc). Le premier morceau garde " +
                 "le handle d'une ligne, d'un arc ou d'une polyligne.")]
    public Task<string> BreakEntities(
        [Description("Handle de la courbe : ligne, arc, cercle, polyligne, spline, ellipse.")] string handle,
        [Description("Point de coupure, [x, y] ou [x, y, z].")] double[] point,
        [Description("Second point : la partie entre point et point2 est supprimée.")] double[]? point2 = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("break_entities", new { handle, point, point2 }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "edit_polyline", Title = "Modifier une polyligne",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Modifie une polyligne 2D (LWPOLYLINE), comme la commande PEDIT. Actions :\n" +
                 "- move_vertex : index, point ;\n" +
                 "- add_vertex : point, index (position du nouveau sommet : 0 avant le premier, après le dernier si " +
                 "omis), bulge du nouveau segment ; sur un segment courbe, un point situé sur l'arc le partage en " +
                 "deux arcs ;\n" +
                 "- remove_vertex : index ;\n" +
                 "- set_bulge : index du sommet de départ du segment, bulge ou angle (angle au centre de l'arc en " +
                 "degrés, positif pour un arc tournant à gauche, qui bombe à droite du segment ; 0 pour le " +
                 "redresser) ;\n" +
                 "- set_width : width (largeur constante), ou index avec startWidth et/ou endWidth ;\n" +
                 "- close, open, reverse (inverse le sens).\n" +
                 "Index à partir de 0, -1 pour le dernier sommet. Points en coordonnées du dessin, ramenés dans le " +
                 "plan de la polyligne. Renvoie les sommets et renflements obtenus.")]
    public Task<string> EditPolyline(
        [Description("Handle de la polyligne.")] string handle,
        [Description("move_vertex, add_vertex, remove_vertex, set_bulge, set_width, close, open ou reverse.")] string action,
        [Description("Indice du sommet, à partir de 0 ; -1 pour le dernier.")] int? index = null,
        [Description("Position du sommet, [x, y] ou [x, y, z].")] double[]? point = null,
        [Description("Renflement : tangente du quart de l'angle au centre ; positif, l'arc tourne à gauche (sens " +
                     "trigonométrique) et bombe donc à droite du segment ; 1 : demi-cercle, 0 : segment droit.")] double? bulge = null,
        [Description("set_bulge : angle au centre de l'arc en degrés, à la place de bulge.")] double? angle = null,
        [Description("set_width : largeur constante de toute la polyligne.")] double? width = null,
        [Description("set_width avec index : largeur au début du segment.")] double? startWidth = null,
        [Description("set_width avec index : largeur à la fin du segment.")] double? endWidth = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("edit_polyline",
            new { handle, action, index, point, bulge, angle, width, startWidth, endWidth }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "stretch_entities", Title = "Étirer des objets",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Étire des objets comme la commande ETIRER avec une fenêtre de capture : les points caractéristiques " +
                 "(sommets, extrémités, points d'insertion, points de définition des cotes) situés dans la fenêtre, " +
                 "vue de dessus, sont déplacés ; un objet dont tous les points sont dedans est déplacé en entier. " +
                 "Par exemple, allonger un bâtiment en déplaçant sa façade. Les objets des calques verrouillés et les " +
                 "hachures associatives (qui suivent leur contour) ne sont pas touchés.")]
    public Task<string> StretchEntities(
        [Description("Premier coin de la fenêtre (rectangle vu de dessus).")] double[]? corner1 = null,
        [Description("Coin opposé de la fenêtre.")] double[]? corner2 = null,
        [Description("À la place d'un rectangle : sommets d'une fenêtre polygonale, [[x,y],…].")] double[][]? polygon = null,
        [Description("Vecteur de déplacement [dx, dy] ou [dx, dy, dz]. À défaut, utilisez from et to.")] double[]? displacement = null,
        [Description("Point de départ du déplacement.")] double[]? from = null,
        [Description("Point d'arrivée du déplacement.")] double[]? to = null,
        [Description("Objets à considérer ; tous ceux de l'espace courant si omis.")] string[]? handles = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("stretch_entities",
            new { corner1, corner2, polygon, displacement, from, to, handles }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_draw_order", Title = "Changer l'ordre d'affichage",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change l'ordre d'affichage d'objets (commande ORDRETRACE) : premier plan, arrière-plan, ou juste " +
                 "au-dessus ou au-dessous d'un objet de référence. Utile pour une hachure pleine, une image ou un " +
                 "masque qui recouvre des traits ou des textes. Renvoie le rang de chaque objet (0 : dessiné en " +
                 "premier, sous tous les autres).")]
    public Task<string> SetDrawOrder(
        [Description(HandlesHelp)] string[] handles,
        [Description("front (premier plan), back (arrière-plan), above ou below (par rapport à reference).")] string order,
        [Description("above/below : handle de l'objet de référence, dans le même espace.")] string? reference = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_draw_order", new { handles, order, reference }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "array_path", Title = "Réseau le long d'un chemin",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Répète des objets le long d'une courbe (ligne, polyligne, arc, cercle, spline…), comme la commande " +
                 "RESEAUCHEMIN : poteaux le long d'une clôture, arbres le long d'une allée, repères sur un tracé. Les " +
                 "originaux sont le premier élément, au début du chemin ; les copies gardent le même décalage par " +
                 "rapport au chemin et tournent avec lui (align). Espacement déduit de count, count déduit de " +
                 "spacing, ou les deux. Copies indépendantes.")]
    public Task<string> ArrayPath(
        [Description(HandlesHelp + " Placez-les au début du chemin.")] string[] handles,
        [Description("Handle de la courbe servant de chemin.")] string path,
        [Description("Nombre total d'éléments, originaux compris ; répartis sur toute la longueur si spacing est omis.")] int? count = null,
        [Description("Distance entre deux éléments, mesurée le long du chemin.")] double? spacing = null,
        [Description("Faire tourner les éléments avec la tangente du chemin (vrai par défaut) ; sinon ils gardent leur orientation.")] bool align = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("array_path", new { handles, path, count, spacing, align }, cancellationToken: cancellationToken);
}

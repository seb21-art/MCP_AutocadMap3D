using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Édition avancée : réseaux, ajuster/prolonger, raccords et chanfreins, décomposition, lignes de repère, jonction,
/// coupure, sommets de polyligne, étirement, ordre d'affichage.
/// Coordonnées dans le repère général du dessin, angles en degrés. Chaque appel est annulable en une étape avec U.
/// </summary>
[McpServerToolType]
public sealed partial class AdvancedEditTools(PluginClient plugin)
{
    private const string HandlesHelp = "Handles des objets, tels que renvoyés par list_entities.";

    [McpServerTool(Name = "array_rectangular", Title = "Réseau rectangulaire",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Répète des objets en rangées, colonnes et niveaux (Z). Les originaux occupent la première case ; les copies " +
                 "sont des objets indépendants (attributs et données d'objet copiés).")]
    public Task<string> ArrayRectangular(
        [Description(HandlesHelp)] string[] handles,
        [Description("Nombre de rangées (1 par défaut).")] int rows = 1,
        [Description("Nombre de colonnes (1 par défaut).")] int columns = 1,
        [Description("Écart entre rangées (négatif : vers le bas).")] double? rowSpacing = null,
        [Description("Écart entre colonnes (négatif : vers la gauche).")] double? columnSpacing = null,
        [Description("Rotation du réseau en degrés : direction des colonnes.")] double angle = 0,
        [Description("Nombre de niveaux empilés selon Z (1 par défaut), pour un réseau 3D.")] int levels = 1,
        [Description("Écart entre niveaux selon Z (négatif : vers le bas).")] double? levelSpacing = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("array_rectangular",
            new { handles, rows, columns, rowSpacing, columnSpacing, angle, levels, levelSpacing }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "array_polar", Title = "Réseau polaire",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Répète des objets autour d'un centre. Sur un tour complet, les éléments sont répartis également ; " +
                 "sur un angle partiel, le premier et le dernier sont aux extrémités.")]
    public Task<string> ArrayPolar(
        [Description(HandlesHelp)] string[] handles,
        [Description("Centre du réseau, [x, y] ou [x, y, z].")] double[] center,
        [Description("Nombre total d'éléments, originaux compris (2 au moins).")] int count,
        [Description("Angle à remplir en degrés (360 par défaut ; négatif : sens horaire).")] double fillAngle = 360,
        [Description("Faire tourner les éléments avec le réseau (vrai par défaut) ; sinon ils gardent leur orientation.")] bool rotateItems = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("array_polar",
            new { handles, center, count, fillAngle, rotateItems }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "trim_entities", Title = "Ajuster des courbes",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Ajuste des courbes sur des arêtes de coupe, comme la commande AJUSTER : chaque courbe est coupée " +
                 "à ses intersections avec les arêtes, et le morceau le plus proche du point « pick » est supprimé. " +
                 "Les intersections se calculent en vue de dessus. Quand un seul morceau reste, l'objet d'origine est " +
                 "conservé (handle et données d'objet).")]
    public Task<string> TrimEntities(
        [Description("Courbes à ajuster, chacune avec un point proche du morceau à supprimer.")] CurvePick[] targets,
        [Description("Handles des arêtes de coupe.")] string[] boundaries,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("trim_entities", new { targets, boundaries }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "extend_entities", Title = "Prolonger des courbes",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Prolonge des lignes, arcs ou polylignes ouvertes jusqu'à la limite la plus proche, comme la " +
                 "commande PROLONGE. L'extrémité prolongée est celle proche du point « pick ». L'objet garde son " +
                 "handle et ses données d'objet.")]
    public Task<string> ExtendEntities(
        [Description("Courbes à prolonger, chacune avec un point proche de l'extrémité à prolonger.")] CurvePick[] targets,
        [Description("Handles des objets limites.")] string[] boundaries,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("extend_entities", new { targets, boundaries }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "fillet", Title = "Raccorder",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Raccorde par un arc tangent, comme la commande RACCORD : soit deux lignes (même si elles ne se " +
                 "touchent pas ; on garde le côté éloigné du coin), soit tous les sommets d'une polyligne 2D. " +
                 "Rayon 0 : les deux lignes sont prolongées ou coupées pour former un angle vif.")]
    public Task<string> Fillet(
        [Description("Deux lignes (LINE), ou une polyligne (LWPOLYLINE).")] string[] handles,
        [Description("Rayon du raccord ; 0 pour un angle vif.")] double radius,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("fillet", new { handles, radius }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "chamfer", Title = "Chanfreiner",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Chanfreine le coin entre deux lignes (même si elles ne se touchent pas), ou tous les sommets d'une " +
                 "polyligne 2D, comme la commande CHANFREIN.")]
    public Task<string> Chamfer(
        [Description("Deux lignes (LINE), ou une polyligne (LWPOLYLINE).")] string[] handles,
        [Description("Distance de coupe sur la première ligne (ou le segment qui arrive au sommet).")] double distance1,
        [Description("Distance sur la seconde ligne (ou le segment qui repart) ; égale à distance1 si omise.")] double? distance2 = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("chamfer", new { handles, distance1, distance2 }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "explode_entities", Title = "Décomposer des objets",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Décompose des objets d'un niveau, comme la commande DECOMPOS : bloc en ses objets, polyligne en " +
                 "lignes et arcs, cote, hachure ou texte multiligne en éléments simples. Pour un bloc, les " +
                 "attributs deviennent des textes portant leur valeur. Les données d'objet de l'original sont " +
                 "perdues.")]
    public Task<string> ExplodeEntities(
        [Description(HandlesHelp)] string[] handles,
        [Description("Convertir les attributs des blocs en textes avec leur valeur (vrai par défaut) ; sinon ils redeviennent des définitions d'attributs.")] bool attributesAsText = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("explode_entities", new { handles, attributesAsText }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_leader", Title = "Créer une ligne de repère",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une ligne de repère multiple (MLEADER) : flèche sur le premier point, éventuels points " +
                 "intermédiaires, texte au bout du dernier. Utilise le style de ligne de repère courant.")]
    public Task<string> CreateLeader(
        [Description("Points : pointe de la flèche en premier, puis points intermédiaires éventuels, puis le point côté texte.")] double[][] points,
        [Description("Texte, sur plusieurs lignes si besoin (\\n).")] string text,
        [Description("Hauteur du texte dans les unités du dessin ; celle du style si omise.")] double? height = null,
        [Description("Taille de la flèche ; celle du style si omise.")] double? arrowSize = null,
        [Description("Style de ligne de repère existant.")] string? style = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_leader",
            new { points, text, height, arrowSize, style, layer, color }, cancellationToken: cancellationToken);
}

/// <summary>Courbe à ajuster ou prolonger, avec le point qui désigne le morceau ou l'extrémité.</summary>
public sealed record CurvePick(
    [property: Description("Handle de la courbe.")] string Handle,
    [property: Description("Point [x, y] proche du morceau à supprimer (ajuster) ou de l'extrémité à prolonger.")] double[] Pick);

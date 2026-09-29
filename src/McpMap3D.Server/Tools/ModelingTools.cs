using System.ComponentModel;
using System.Text.Json;
using McpMap3D.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Modélisation 3D : solides, surfaces, maillages et échanges de fichiers 3D. Coordonnées dans le repère général du
/// dessin, angles en degrés. Chaque appel de modification est annulable en une étape avec U.
/// </summary>
[McpServerToolType]
public sealed partial class ModelingTools(PluginClient plugin)
{
    private const string LayerHelp = ToolHelp.Layer;
    private const string ColorHelp = ToolHelp.Color;
    private const string PlaneHelp =
        "Le plan se donne par planePoint et planeNormal, ou par trois points planePoints.";
    private const string IndexHelp =
        "Les faces et arêtes se désignent par les indices de get_solid_topology, valables jusqu'à la prochaine " +
        "modification du solide, ou par un filtre.";

    [McpServerTool(Name = "create_box", Title = "Créer une boîte (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un solide parallélépipédique. Renvoie son handle et son volume.")]
    public Task<string> CreateBox(
        [Description("Coin minimal (x, y, z mini) de la boîte, [x, y] ou [x, y, z].")] double[] corner,
        [Description("Longueur selon X, avant rotation.")] double length,
        [Description("Largeur selon Y, avant rotation.")] double width,
        [Description("Hauteur selon Z.")] double height,
        [Description("Rotation autour de l'axe Z passant par le coin, en degrés.")] double rotation = 0,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_box",
            new { corner, length, width, height, rotation, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_wedge", Title = "Créer un biseau (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un biseau : boîte dont la face supérieure descend en pente de la hauteur totale (côté du coin) " +
                 "à zéro (à l'extrémité de la longueur).")]
    public Task<string> CreateWedge(
        [Description("Coin minimal (x, y, z mini), [x, y] ou [x, y, z].")] double[] corner,
        [Description("Longueur selon X, sur laquelle se fait la pente.")] double length,
        [Description("Largeur selon Y.")] double width,
        [Description("Hauteur maximale selon Z.")] double height,
        [Description("Rotation autour de l'axe Z passant par le coin, en degrés.")] double rotation = 0,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_wedge",
            new { corner, length, width, height, rotation, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_cylinder", Title = "Créer un cylindre ou un cône (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un cylindre, un cône (topRadius = 0) ou un tronc de cône. Vertical par défaut, " +
                 "orientable avec axis.")]
    public Task<string> CreateCylinder(
        [Description("Centre de la base, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon de la base.")] double radius,
        [Description("Hauteur, mesurée le long de l'axe.")] double height,
        [Description("Rayon du sommet : égal à radius si omis (cylindre), 0 pour un cône.")] double? topRadius = null,
        [Description("Direction de l'axe, par exemple [1, 0, 0] pour un cylindre horizontal. [0, 0, 1] par défaut.")] double[]? axis = null,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_cylinder",
            new { center, radius, height, topRadius, axis, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_sphere", Title = "Créer une sphère (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une sphère pleine.")]
    public Task<string> CreateSphere(
        [Description("Centre, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon.")] double radius,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_sphere", new { center, radius, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_torus", Title = "Créer un tore (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un tore dans le plan horizontal.")]
    public Task<string> CreateTorus(
        [Description("Centre, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon du cercle directeur, du centre au milieu du tube.")] double majorRadius,
        [Description("Rayon du tube.")] double minorRadius,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_torus",
            new { center, majorRadius, minorRadius, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_pyramid", Title = "Créer une pyramide (solide)",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une pyramide régulière de 3 à 32 côtés, ou un tronc de pyramide (topRadius > 0), comme la " +
                 "commande PYRAMIDE. Verticale par défaut, orientable avec axis. Pour un cône, utilisez " +
                 "create_cylinder avec topRadius = 0.")]
    public Task<string> CreatePyramid(
        [Description("Centre de la base, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon de la base : du centre au milieu d'un côté (cercle inscrit dans la base, comme l'option " +
                     "par défaut de PYRAMIDE), ou du centre à un sommet avec radiusToVertex.")] double radius,
        [Description("Hauteur, mesurée le long de l'axe.")] double height,
        [Description("Nombre de côtés, de 3 à 32 (4 par défaut).")] int sides = 4,
        [Description("Rayon du sommet, mesuré comme radius : 0 (défaut) pour une pointe, positif pour un tronc.")] double topRadius = 0,
        [Description("Mesurer radius et topRadius du centre aux sommets de la base.")] bool radiusToVertex = false,
        [Description("Direction de l'axe, [0, 0, 1] par défaut.")] double[]? axis = null,
        [Description("Rotation de la base autour de l'axe, en degrés. À 0°, une base carrée d'axe vertical a ses " +
                     "côtés parallèles aux axes X et Y.")] double rotation = 0,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_pyramid",
            new { center, radius, height, sides, topRadius, radiusToVertex, axis, rotation, layer, color },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_3d_polyline", Title = "Créer une polyligne 3D",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une polyligne 3D, dont chaque sommet a sa propre altitude (réseau, profil, ligne de rupture).")]
    public Task<string> Create3dPolyline(
        [Description("Sommets [x, y, z], deux au minimum, par exemple [[0,0,10],[5,0,12],[5,5,11]].")] double[][] points,
        [Description("Fermer la polyligne.")] bool closed = false,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_3d_polyline",
            new { points, closed, layer, color, linetype, lineWeight }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_helix", Title = "Créer une hélice",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une hélice 2D ou 3D, comme la commande HELICE : cylindrique, conique (topRadius différent de " +
                 "baseRadius). Verticale par défaut, orientable avec axis. Donnez le nombre de tours (turns) ou le pas " +
                 "(turnHeight), pas les deux. Sert de chemin (path) à extrude. Ressort : un petit cercle comme profil et " +
                 "alignProfile à vrai (sinon le profil n'est pas orienté le long de l'hélice). Filetage : un peigne " +
                 "(trapèze à flancs de 60° pour l'ISO) dessiné dans un plan contenant l'axe, au départ de l'hélice, " +
                 "balayé avec alignProfile à faux et basePoint au départ, puis soustrait de la tige avec " +
                 "boolean_solids ; le tout en un seul balayage, quel que soit le nombre de tours. Le peigne doit rester " +
                 "moins large que le pas le long de l'axe (coupé juste au-delà de la crête) : au-delà d'environ deux " +
                 "pas, les spires se recouvrent et le balayage échoue.")]
    public Task<string> CreateHelix(
        [Description("Centre de la base, sur l'axe, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon de la base.")] double baseRadius,
        [Description("Hauteur, mesurée le long de l'axe.")] double height,
        [Description("Rayon du sommet : égal à baseRadius si omis (hélice cylindrique).")] double? topRadius = null,
        [Description("Nombre de tours, décimal accepté, de 0 (exclu) à 500. 3 par défaut, comme HELICE.")] double? turns = null,
        [Description("Pas : hauteur d'un tour complet. Le nombre de tours vaut alors height / turnHeight.")] double? turnHeight = null,
        [Description("Tourner dans le sens horaire, vu depuis le sommet de l'axe. Sens trigonométrique par défaut.")] bool clockwise = false,
        [Description("Angle du point de départ autour de l'axe, en degrés (0 : direction X pour un axe vertical).")] double startAngle = 0,
        [Description("Direction de l'axe, [0, 0, 1] par défaut.")] double[]? axis = null,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_helix",
            new { center, baseRadius, height, topRadius, turns, turnHeight, clockwise, startAngle, axis, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "extrude", Title = "Extruder des profils en solides ou en surfaces",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Extrude des profils fermés (polyligne fermée, cercle, ellipse, région) en solides 3D, d'une hauteur " +
                 "ou le long d'un chemin (balayage, avec torsion et mise à l'échelle possibles). Chaque profil donne un solide, sur le calque du profil sauf si layer est " +
                 "précisé. Utile par exemple pour donner du volume à des emprises de bâtiments. Avec surface, " +
                 "chaque courbe, ouverte ou fermée, donne une surface extrudée ou balayée (mur mince à partir d'une " +
                 "polyligne en plan, par exemple).")]
    public Task<string> Extrude(
        [Description("Handles des profils fermés, ou des courbes quelconques avec surface.")] string[] handles,
        [Description("Hauteur d'extrusion (négative vers le bas). Ignorée si path est fourni.")] double? height = null,
        [Description("Angle de dépouille en degrés : positif, les faces se rapprochent en montant.")] double taperAngle = 0,
        [Description("Direction d'extrusion [x, y, z] ; par défaut la normale du profil (+Z pour un profil horizontal).")] double[]? direction = null,
        [Description("Handle d'une courbe servant de chemin d'extrusion (balayage).")] string? path = null,
        [Description("Supprimer les profils après extrusion.")] bool eraseProfiles = false,
        [Description(LayerHelp + " Par défaut : calque du profil.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description("Balayage : torsion totale du profil le long du chemin, en degrés.")] double? twistAngle = null,
        [Description("Balayage : facteur d'échelle du profil à l'extrémité du chemin (0.5 = moitié), variation progressive.")] double? scaleFactor = null,
        [Description("Balayage : orienter le profil perpendiculairement au chemin (vrai) ou le garder tel quel (faux, " +
                     "pour un peigne de filetage dessiné dans un plan contenant l'axe de l'hélice). Réglage d'AutoCAD si omis.")] bool? alignProfile = null,
        [Description("Balayage : point du profil qui suit le chemin, [x, y, z].")] double[]? basePoint = null,
        [Description("Balayage : incliner le profil dans les courbes d'un chemin 3D.")] bool? bank = null,
        [Description("Créer des surfaces au lieu de solides ; les courbes peuvent alors être ouvertes.")] bool surface = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("extrude",
            new { handles, height, taperAngle, direction, path, eraseProfiles, layer, color, twistAngle, scaleFactor, alignProfile, basePoint, bank, surface },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "revolve", Title = "Créer des solides ou des surfaces de révolution",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Fait tourner des profils fermés autour d'un axe pour créer des solides de révolution. L'axe doit " +
                 "être dans le plan du profil sans le traverser. Avec surface, chaque courbe, ouverte ou fermée, " +
                 "donne une surface de révolution (coupole, entonnoir).")]
    public Task<string> Revolve(
        [Description("Handles des profils fermés, ou des courbes quelconques avec surface.")] string[] handles,
        [Description("Premier point de l'axe de révolution.")] double[] axisStart,
        [Description("Second point de l'axe de révolution.")] double[] axisEnd,
        [Description("Angle de révolution en degrés, 360 par défaut.")] double angle = 360,
        [Description("Supprimer les profils après révolution.")] bool eraseProfiles = false,
        [Description(LayerHelp + " Par défaut : calque du profil.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description("Créer des surfaces au lieu de solides ; les courbes peuvent alors être ouvertes.")] bool surface = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("revolve",
            new { handles, axisStart, axisEnd, angle, eraseProfiles, layer, color, surface }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "boolean_solids", Title = "Opération booléenne sur des solides",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Combine des solides 3D : union, soustraction (target moins tools) ou intersection. Le résultat " +
                 "remplace le solide cible, et les solides outils sont consommés.")]
    public Task<string> BooleanSolids(
        [Description("Opération : union, subtract ou intersect.")] string operation,
        [Description("Handle du solide cible, qui reçoit le résultat.")] string target,
        [Description("Handles des solides outils, consommés par l'opération.")] string[] tools,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("boolean_solids", new { operation, target, tools }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "rotate_entities", Title = "Faire pivoter des objets en 3D",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Fait pivoter des objets autour d'un axe passant par un point de base (axe Z par défaut).")]
    public Task<string> RotateEntities(
        [Description("Handles des objets.")] string[] handles,
        [Description("Point de base, sur l'axe de rotation.")] double[] basePoint,
        [Description("Angle en degrés, sens trigonométrique autour de l'axe.")] double angle,
        [Description("Direction de l'axe [x, y, z], [0, 0, 1] par défaut.")] double[]? axis = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("rotate_entities", new { handles, basePoint, angle, axis }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_solid_properties", Title = "Propriétés des solides et objets",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renvoie les propriétés géométriques d'objets : volume, surface et centre de gravité des solides, " +
                 "aire et périmètre des régions, aire des surfaces, sommets, faces, niveau de lissage, aire et " +
                 "fermeture des maillages (volume s'ils sont fermés), longueur et aire des courbes, et limites de " +
                 "chaque objet. Avec " +
                 "inertia, ajoute les inerties des solides comme la commande PROPMECA (masse volumique 1).")]
    public Task<string> GetSolidProperties(
        [Description("Handles des objets.")] string[] handles,
        [Description("Ajouter les inerties des solides : moments et produits d'inertie par rapport aux axes du SCG " +
                     "(origine), moments principaux et axes principaux au centre de gravité, rayons de giration.")] bool inertia = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_solid_properties", new { handles, inertia }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_view", Title = "Changer la vue",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Oriente la vue de l'espace objet (dessus, faces, isométriques), la cadre sur l'étendue du dessin, " +
                 "sur une fenêtre ou autour d'un centre, et change le style visuel pour voir les volumes. Le cadrage ne " +
                 "crée aucun objet : il sert aussi à se placer sur un fond WMS ou une zone vide du dessin. Avec lonLat, " +
                 "window et center sont en longitude/latitude (par exemple celles de search_cadastre_address), converties " +
                 "dans le système de coordonnées du dessin. Renvoie le centre et la taille de la vue obtenue.")]
    public Task<string> SetView(
        [Description("Vue : top, bottom, front, back, left, right, sw_iso, se_iso, ne_iso ou nw_iso. Direction inchangée si omise.")] string? view = null,
        [Description("Zoomer sur l'étendue du dessin. Vrai par défaut quand view est donnée sans window ni center.")] bool? zoomExtents = null,
        [Description("Fenêtre à voir en entier, dans le plan XY : [minX, minY, maxX, maxY].")] double[]? window = null,
        [Description("Centre de la vue [x, y]. Sans width ni height, la vue est seulement recentrée.")] double[]? center = null,
        [Description("Largeur de la zone à voir autour de center, en unités du dessin (mètres en Lambert-93).")] double? width = null,
        [Description("Hauteur de la zone à voir autour de center, en unités du dessin. La zone entière reste visible.")] double? height = null,
        [Description("window et center sont en longitude puis latitude (degrés WGS84).")] bool lonLat = false,
        [Description("Style visuel : 2dwireframe, wireframe, hidden, realistic, conceptual, shaded, shaded_edges, " +
                     "shades_of_gray, sketchy ou xray. Inchangé si omis.")] string? visualStyle = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_view",
            new { view, zoomExtents, window, center, width, height, lonLat, visualStyle }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "capture_view", Title = "Capturer la vue",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renvoie une image PNG de la vue courante d'AutoCAD, pour vérifier visuellement un dessin ou un " +
                 "modèle 3D. Orientez d'abord la vue avec set_view (par exemple se_iso et un style visuel " +
                 "conceptual ou shaded_edges pour les volumes). L'image garde les proportions de la fenêtre " +
                 "d'AutoCAD : sa taille réelle, qui peut différer de la taille demandée, est renvoyée avec elle.")]
    public async Task<CallToolResult> CaptureView(
        [Description("Largeur de l'image en pixels, 64 à 2048 (1024 par défaut).")] int width = 1024,
        [Description("Hauteur de l'image en pixels, 64 à 2048 (768 par défaut).")] int height = 768,
        CancellationToken cancellationToken = default)
    {
        var result = await plugin.CallAsync("capture_view", new { width, height }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var image = Convert.FromBase64String(result.GetProperty("data").GetString() ?? "");
        var summary = new
        {
            width = result.GetProperty("width").GetInt32(),
            height = result.GetProperty("height").GetInt32(),
            space = result.GetProperty("space").GetString(),
            bytes = image.Length,
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(summary, PipeProtocol.JsonOptions) },
                ImageContentBlock.FromBytes(image, "image/png"),
            ],
        };
    }

    [McpServerTool(Name = "create_region", Title = "Créer des régions",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Forme des régions à partir de courbes coplanaires qui se touchent bout à bout (lignes, arcs, " +
                 "polylignes…), ou directement d'une liste de sommets (points) sans rien dessiner d'autre : un contour " +
                 "devient un profil extrudable ou révolvable, dans n'importe quel plan.")]
    public Task<string> CreateRegion(
        [Description("Handles des courbes formant un ou plusieurs contours fermés. Ou bien points.")] string[]? handles = null,
        [Description("Sommets d'un contour fermé, au lieu de handles (trois au minimum). " + ToolHelp.PlanarPoints)] double[][]? points = null,
        [Description(ToolHelp.Plane + " Avec points seulement.")] string? plane = null,
        [Description(ToolHelp.PlaneOffset)] double? planeOffset = null,
        [Description(ToolHelp.Normal + " Avec points seulement.")] double[]? normal = null,
        [Description("Supprimer les courbes d'origine (avec handles).")] bool eraseCurves = false,
        [Description(LayerHelp + " Par défaut : calque de la première courbe, ou calque courant avec points.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_region",
            new { handles, points, plane, planeOffset, normal, eraseCurves, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "slice_solid", Title = "Couper des solides par un plan",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Coupe des solides 3D par un plan. " + PlaneHelp + " Le côté positif est celui vers lequel pointe " +
                 "la normale.")]
    public Task<string> SliceSolid(
        [Description("Handles des solides à couper.")] string[] handles,
        [Description("Point du plan, [x, y, z].")] double[]? planePoint = null,
        [Description("Normale du plan, [x, y, z] ; par exemple [0, 0, 1] pour un plan horizontal.")] double[]? planeNormal = null,
        [Description("Trois points non alignés du plan, à la place de planePoint et planeNormal.")] double[][]? planePoints = null,
        [Description("Partie à garder : both (les deux, en deux solides ; défaut), positive ou negative.")] string keep = "both",
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("slice_solid",
            new { handles, planePoint, planeNormal, planePoints, keep }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_section", Title = "Section de solides par un plan",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée la région de coupe de solides 3D par un plan, sans modifier les solides : lecture d'une " +
                 "coupe, surface de plancher à une altitude. " + PlaneHelp + " Renvoie aire et périmètre.")]
    public Task<string> GetSection(
        [Description("Handles des solides.")] string[] handles,
        [Description("Point du plan, [x, y, z].")] double[]? planePoint = null,
        [Description("Normale du plan, [x, y, z] ; par exemple [0, 0, 1] pour un plan horizontal.")] double[]? planeNormal = null,
        [Description("Trois points non alignés du plan, à la place de planePoint et planeNormal.")] double[][]? planePoints = null,
        [Description(LayerHelp + " Par défaut : calque du solide.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_section",
            new { handles, planePoint, planeNormal, planePoints, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "loft", Title = "Lisser un solide ou une surface entre des sections",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un solide passant successivement par des sections fermées (polylignes, cercles, régions) : " +
                 "transitions, trémies, coques. Des courbes guides ou un chemin peuvent contrôler la forme. Avec " +
                 "surface, crée une surface lissée, dont les sections peuvent être ouvertes (talus entre deux " +
                 "profils en travers, par exemple).")]
    public Task<string> Loft(
        [Description("Handles des sections, fermées pour un solide, dans l'ordre du lissage (deux au moins).")] string[] sections,
        [Description("Handles de courbes guides, chacune touchant toutes les sections.")] string[]? guides = null,
        [Description("Handle d'une courbe chemin, traversant toutes les sections (exclusif avec guides).")] string? path = null,
        [Description("Surfaces réglées (droites) entre sections, au lieu d'un lissage continu.")] bool ruled = false,
        [Description("Refermer le lissage de la dernière section vers la première (forme en anneau).")] bool closed = false,
        [Description("Supprimer les sections après lissage.")] bool eraseSections = false,
        [Description(LayerHelp + " Par défaut : calque de la première section.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description("Créer une surface au lieu d'un solide ; les sections peuvent alors être ouvertes.")] bool surface = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("loft",
            new { sections, guides, path, ruled, closed, eraseSections, layer, color, surface }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "mirror_3d", Title = "Symétrie 3D par un plan",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée le symétrique d'objets par rapport à un plan quelconque de l'espace, comme la commande " +
                 "3DMIROIR. " + PlaneHelp + " Par défaut les originaux sont conservés.")]
    public Task<string> Mirror3d(
        [Description("Handles des objets.")] string[] handles,
        [Description("Point du plan, [x, y, z].")] double[]? planePoint = null,
        [Description("Normale du plan, [x, y, z] ; par exemple [0, 0, 1] pour un plan horizontal.")] double[]? planeNormal = null,
        [Description("Trois points non alignés du plan, à la place de planePoint et planeNormal.")] double[][]? planePoints = null,
        [Description("Supprimer les originaux (retourner les objets sur place).")] bool eraseOriginals = false,
        [Description("Garder les textes lisibles plutôt qu'inversés.")] bool keepTextReadable = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("mirror_3d",
            new { handles, planePoint, planeNormal, planePoints, eraseOriginals, keepTextReadable }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "align_3d", Title = "Aligner des objets en 3D",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Déplace et oriente des objets par paires de points source → cible, comme la commande ALIGNER : " +
                 "une paire translate, deux paires translatent et orientent une direction, trois paires posent un " +
                 "plan sur un autre (poser un objet sur une face).")]
    public Task<string> Align3d(
        [Description("Handles des objets.")] string[] handles,
        [Description("Points source, un à trois, [[x, y, z], …].")] double[][] sourcePoints,
        [Description("Points cibles correspondants, même nombre.")] double[][] targetPoints,
        [Description("Mettre à l'échelle pour que la distance entre les deux premiers points source égale celle des cibles.")] bool scale = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("align_3d",
            new { handles, sourcePoints, targetPoints, scale }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "check_interference", Title = "Détecter les collisions entre solides",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Cherche les solides 3D qui se chevauchent et donne le volume commun de chaque paire. Sans setB, " +
                 "toutes les paires de setA sont testées ; sinon chaque solide de setA contre chaque solide de setB. " +
                 "Les solides testés ne sont pas modifiés ; des solides qui se touchent seulement ne comptent pas. " +
                 "Avec createSolids, les volumes communs sont dessinés (rouge par défaut), annulables avec U.")]
    public Task<string> CheckInterference(
        [Description("Handles du premier ensemble de solides.")] string[] setA,
        [Description("Handles du second ensemble ; si omis, setA est testé contre lui-même.")] string[]? setB = null,
        [Description("Dessiner les volumes communs sous forme de solides.")] bool createSolids = false,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp + " Rouge par défaut.")] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("check_interference",
            new { setA, setB, createSolids, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "buildings_to_solids", Title = "Convertir les bâtiments en solides",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Convertit des polylignes fermées dotées d'une épaisseur (bâtiments importés par import_cadastre " +
                 "avec buildings3d) en vrais solides 3D, utilisables par boolean_solids, slice_solid ou " +
                 "check_interference. Par défaut chaque solide remplace sa polyligne en gardant son handle et ses " +
                 "données d'objet, et les cours intérieures sont soustraites.")]
    public Task<string> BuildingsToSolids(
        [Description("Handles des polylignes à convertir.")] string[]? handles = null,
        [Description("À défaut de handles : calques à parcourir, jokers acceptés, par exemple [\"*_BATI_*\"].")] string[]? layers = null,
        [Description("Garder les polylignes et créer les solides à côté (sans données d'objet).")] bool keepOriginals = false,
        [Description("Soustraire les cours intérieures (polylignes contenues dans une autre de même hauteur).")] bool subtractHoles = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("buildings_to_solids",
            new { handles, layers, keepOriginals, subtractHoles }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_solid_topology", Title = "Faces et arêtes d'un solide",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les faces et les arêtes numérotées d'un solide 3D, pour choisir celles à raccorder, " +
                 "chanfreiner ou modifier. Faces : type (plane, cylinder, cone, sphere, torus, nurbs), aire, normale " +
                 "extérieure des faces planes, centre et arêtes. Arêtes : nature (line, arc, circle…), extrémités, " +
                 "longueur, rayon, orientation (vertical ou horizontal) et faces voisines. Au-delà de 200 faces ou 400 " +
                 "arêtes, la liste est tronquée (truncated) ; les filtres des outils d'édition portent toujours sur " +
                 "tout le solide. Les indices ne valent que pour l'état actuel du solide : relisez la topologie après " +
                 "chaque modification.")]
    public Task<string> GetSolidTopology(
        [Description("Handle du solide 3D.")] string handle,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_solid_topology", new { handle }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "fillet_edges", Title = "Raccorder des arêtes de solide",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Arrondit des arêtes d'un solide 3D par un congé de rayon constant, comme la commande " +
                 "RACCORDARETE. " + IndexHelp + " Renvoie le nouveau volume.")]
    public Task<string> FilletEdges(
        [Description("Handle du solide 3D.")] string handle,
        [Description("Rayon du congé.")] double radius,
        [Description("Indices des arêtes (get_solid_topology).")] int[]? edges = null,
        [Description("À défaut d'edges : vertical (arêtes verticales), horizontal, top (contour des faces du dessus), " +
                     "bottom (contour des faces du dessous) ou all.")] string? edgeFilter = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("fillet_edges",
            new { handle, radius, edges, edgeFilter }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "chamfer_edges", Title = "Chanfreiner des arêtes de solide",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Chanfreine des arêtes d'un solide 3D, comme la commande CHANFREINARETE. Les arêtes bordent toutes " +
                 "une même face de base, sur laquelle se mesure distance ; otherDistance se mesure sur les faces " +
                 "voisines. Sans edges, tout le contour de la face de base est chanfreiné. " + IndexHelp)]
    public Task<string> ChamferEdges(
        [Description("Handle du solide 3D.")] string handle,
        [Description("Distance de chanfrein sur la face de base.")] double distance,
        [Description("Indice de la face de base (get_solid_topology).")] int? baseFace = null,
        [Description("À défaut de baseFace : top ou bottom, quand une seule face correspond.")] string? baseFaceFilter = null,
        [Description("Indices des arêtes à chanfreiner, toutes sur la face de base. Tout son contour si omis.")] int[]? edges = null,
        [Description("Distance sur les faces voisines ; égale à distance si omise.")] double? otherDistance = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("chamfer_edges",
            new { handle, distance, baseFace, baseFaceFilter, edges, otherDistance }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "shell_solid", Title = "Évider un solide en coque",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Évide un solide 3D en coque d'épaisseur constante, comme SOLIDEDIT Gainer : bac, cuve, caisson, " +
                 "regard. Les faces retirées restent ouvertes, par exemple le dessus d'un bac ; sans face retirée, la " +
                 "coque est fermée autour d'un vide. Par défaut la paroi est prise vers l'intérieur et les dimensions " +
                 "extérieures ne changent pas. " + IndexHelp)]
    public Task<string> ShellSolid(
        [Description("Handle du solide 3D.")] string handle,
        [Description("Épaisseur de la paroi.")] double thickness,
        [Description("Indices des faces à ouvrir (get_solid_topology).")] int[]? removeFaces = null,
        [Description("À défaut de removeFaces : top, bottom ou sides.")] string? removeFaceFilter = null,
        [Description("Paroi vers l'extérieur : le solide d'origine devient le vide intérieur.")] bool outward = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("shell_solid",
            new { handle, thickness, removeFaces, removeFaceFilter, outward }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "edit_solid_faces", Title = "Modifier des faces de solide",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Modifie des faces d'un solide 3D, comme SOLIDEDIT Face ou APPUYERTIRER. extrude : pousse ou tire " +
                 "des faces planes le long de leur normale, avec une dépouille possible. offset : décale des faces. " +
                 "move : déplace des faces d'un vecteur, les faces voisines suivent. rotate : fait pivoter des faces " +
                 "autour d'un axe. taper : incline des faces d'un angle de dépouille (parois d'un moule, talus). " +
                 "delete : supprime des faces (congés, chanfreins, perçages), les faces voisines se prolongent pour " +
                 "refermer le solide. color : colore des faces. copy : copie des faces en régions (faces planes) ou " +
                 "en corps, sans modifier le solide. " + IndexHelp)]
    public Task<string> EditSolidFaces(
        [Description("Handle du solide 3D.")] string handle,
        [Description("Opération : extrude, offset, move, rotate, taper, delete, color ou copy.")] string operation,
        [Description("Indices des faces (get_solid_topology).")] int[]? faces = null,
        [Description("À défaut de faces : top, bottom, sides ou all.")] string? faceFilter = null,
        [Description("extrude et offset : distance le long de la normale extérieure ; positive, le solide grossit, " +
                     "négative, il se creuse.")] double? distance = null,
        [Description("extrude : angle de dépouille en degrés, entre -90 et 90 ; positif, la face se rétrécit en " +
                     "avançant. taper (obligatoire) : inclinaison des faces, positive vers l'intérieur du solide.")] double taperAngle = 0,
        [Description("move : vecteur de déplacement [dx, dy, dz].")] double[]? displacement = null,
        [Description("rotate : angle en degrés, sens trigonométrique autour de l'axe.")] double? angle = null,
        [Description("rotate : direction de l'axe, [0, 0, 1] par défaut.")] double[]? axis = null,
        [Description("rotate (obligatoire) : point de l'axe de rotation. taper : point de la charnière, qui reste en " +
                     "place ; par défaut le point le plus bas des faces dans la direction de dépouille.")] double[]? basePoint = null,
        [Description("taper : direction de dépouille, [0, 0, 1] par défaut (faces qui s'inclinent en montant).")] double[]? draftDirection = null,
        [Description("color (obligatoire) : couleur des faces. copy : couleur des copies. " + ColorHelp)] string? color = null,
        [Description("copy : calque des copies. " + LayerHelp)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("edit_solid_faces",
            new { handle, operation, faces, faceFilter, distance, taperAngle, displacement, angle, axis, basePoint, draftDirection, color, layer },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "thicken_surface", Title = "Épaissir des surfaces en solides",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Donne une épaisseur à des surfaces ou à des régions pour en faire des solides 3D, comme la commande " +
                 "EPAISSIR : dalle, voile, toiture. Le solide prend les propriétés de la surface.")]
    public Task<string> ThickenSurface(
        [Description("Handles des surfaces ou régions.")] string[] handles,
        [Description("Épaisseur, du côté de la normale de la surface (+Z pour une région dessinée en plan) ; " +
                     "négative, de l'autre côté.")] double thickness,
        [Description("Épaissir des deux côtés de la surface.")] bool bothSides = false,
        [Description("Supprimer les surfaces d'origine.")] bool eraseSurfaces = false,
        [Description(LayerHelp + " Par défaut : calque de la surface.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("thicken_surface",
            new { handles, thickness, bothSides, eraseSurfaces, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "separate_solid", Title = "Séparer les parties d'un solide",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sépare un solide 3D fait de volumes disjoints (après une soustraction qui l'a coupé en deux, ou " +
                 "l'union de solides qui ne se touchent pas) en solides indépendants, comme SOLIDEDIT Corps Séparer. " +
                 "Le solide garde une partie, les autres deviennent de nouveaux solides avec les mêmes propriétés.")]
    public Task<string> SeparateSolid(
        [Description("Handles des solides 3D.")] string[] handles,
        [Description("Nettoyer d'abord les arêtes et sommets superflus, comme SOLIDEDIT Corps Nettoyer.")] bool clean = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("separate_solid", new { handles, clean }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "imprint_solid", Title = "Imprimer des objets sur un solide",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Imprime des courbes, régions, corps ou solides sur les faces d'un solide 3D, comme la commande " +
                 "IMPRIMER : les faces touchées sont découpées le long de l'intersection, et chaque morceau devient " +
                 "une face à part, modifiable avec edit_solid_faces (tirer une partie d'une face, par exemple). Les " +
                 "objets doivent toucher ou traverser le solide.")]
    public Task<string> ImprintSolid(
        [Description("Handle du solide 3D.")] string handle,
        [Description("Handles des objets à imprimer : courbes, régions, corps ou solides.")] string[] sources,
        [Description("Supprimer les objets imprimés.")] bool eraseSources = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("imprint_solid", new { handle, sources, eraseSources }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "clean_solid", Title = "Nettoyer des solides",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Nettoie des solides 3D comme SOLIDEDIT Corps Nettoyer : retire les arêtes et sommets superflus, " +
                 "par exemple ceux qui séparent deux morceaux d'une même face après une union. Renvoie le nombre de " +
                 "faces et d'arêtes avant et après.")]
    public Task<string> CleanSolid(
        [Description("Handles des solides 3D.")] string[] handles,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("clean_solid", new { handles }, cancellationToken: cancellationToken);
}

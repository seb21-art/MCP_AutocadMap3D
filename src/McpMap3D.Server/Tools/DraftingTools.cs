using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Dessin technique 2D : géométrie, annotation (texte multiligne, hachures, cotations), blocs et modifications.
/// Coordonnées dans le repère général du dessin, angles en degrés (sens trigonométrique, 0 = +X).
/// Chaque appel de modification est annulable en une étape avec U.
/// </summary>
[McpServerToolType]
public sealed partial class DraftingTools(PluginClient plugin)
{
    private const string HandlesHelp = "Handles des objets, tels que renvoyés par list_entities.";

    // ----- Géométrie -----

    [McpServerTool(Name = "create_circle", Title = "Créer un cercle",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un cercle dans l'espace courant et renvoie son handle.")]
    public Task<string> CreateCircle(
        [Description("Centre, [x, y] ou [x, y, z].")] double[] center,
        [Description("Rayon.")] double radius,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_circle",
            new { center, radius, layer, color, linetype, lineWeight }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_arc", Title = "Créer un arc",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un arc, défini soit par center, radius, startAngle et endAngle (parcouru dans le sens " +
                 "trigonométrique), soit par trois points start, mid et end. Renvoie centre, rayon, angles et " +
                 "longueur.")]
    public Task<string> CreateArc(
        [Description("Centre (première méthode).")] double[]? center = null,
        [Description("Rayon (première méthode).")] double? radius = null,
        [Description("Angle de départ en degrés (première méthode).")] double? startAngle = null,
        [Description("Angle d'arrivée en degrés (première méthode).")] double? endAngle = null,
        [Description("Point de départ (seconde méthode).")] double[]? start = null,
        [Description("Point intermédiaire, par lequel passe l'arc (seconde méthode).")] double[]? mid = null,
        [Description("Point d'arrivée (seconde méthode).")] double[]? end = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_arc",
            new { center, radius, startAngle, endAngle, start, mid, end, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_ellipse", Title = "Créer une ellipse",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une ellipse, ou un arc d'ellipse avec startAngle et endAngle, dans l'espace courant. Renvoie " +
                 "son handle, sa longueur et son aire (ellipse complète).")]
    public Task<string> CreateEllipse(
        [Description("Centre, [x, y] ou [x, y, z].")] double[] center,
        [Description("Demi-grand axe : distance du centre à l'extrémité du grand axe.")] double majorRadius,
        [Description("Demi-petit axe, au plus égal à majorRadius.")] double minorRadius,
        [Description("Orientation du grand axe en degrés (0 = parallèle à X).")] double rotation = 0,
        [Description("Arc d'ellipse : angle de départ en degrés, mesuré depuis le grand axe dans le sens trigonométrique.")]
        double? startAngle = null,
        [Description("Arc d'ellipse : angle d'arrivée en degrés.")] double? endAngle = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_ellipse",
            new { center, majorRadius, minorRadius, rotation, startAngle, endAngle, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_spline", Title = "Créer une spline",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une spline, courbe lisse, dans l'espace courant. Par défaut elle passe par les points donnés " +
                 "(lissage de degré 3, comme la commande SPLINE) ; avec controlPoints, les points sont les sommets de " +
                 "contrôle, que la courbe approche sans les traverser. Ouverte ou fermée. Renvoie son handle et sa " +
                 "longueur.")]
    public Task<string> CreateSpline(
        [Description("Points [[x, y], …] ou [[x, y, z], …] : points de passage, ou sommets de contrôle avec " +
                     "controlPoints. Deux au minimum, trois pour une spline fermée.")]
        double[][] points,
        [Description("Fermer la spline (courbe continue, sans angle au point de fermeture).")] bool closed = false,
        [Description("Les points sont des sommets de contrôle, et non des points de passage.")] bool controlPoints = false,
        [Description("Degré de la spline par sommets de contrôle, 1 à 10 (3 par défaut). Il faut au moins degree + 1 points.")]
        int? degree = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_spline",
            new { points, closed, controlPoints, degree, layer, color, linetype, lineWeight },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_rectangle", Title = "Créer un rectangle",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un rectangle (polyligne fermée) à partir de deux coins opposés. Renvoie son handle, ses " +
                 "dimensions et son aire. Pour l'incliner, utilisez ensuite rotate_entities.")]
    public Task<string> CreateRectangle(
        [Description("Premier coin, [x, y] ou [x, y, z].")] double[] corner1,
        [Description("Coin opposé.")] double[] corner2,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        [Description(ToolHelp.Linetype)] string? linetype = null,
        [Description(ToolHelp.LineWeight)] string? lineWeight = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_rectangle",
            new { corner1, corner2, layer, color, linetype, lineWeight }, cancellationToken: cancellationToken);

    // ----- Annotation -----

    [McpServerTool(Name = "create_mtext", Title = "Créer un texte multiligne",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un texte multiligne (MTEXT) : notes, légendes, cartouches. Les retours à la ligne du texte " +
                 "sont conservés ; avec une largeur, AutoCAD renvoie aussi à la ligne automatiquement. Un masque " +
                 "d'arrière-plan le rend lisible par-dessus des traits ou une hachure.")]
    public Task<string> CreateMText(
        [Description("Contenu, sur plusieurs lignes si besoin (\\n).")] string text,
        [Description("Point d'insertion, [x, y] ou [x, y, z] ; sa position dans le cadre dépend de attachment.")] double[] position,
        [Description("Hauteur des caractères dans les unités du dessin (2,5 par défaut).")] double height = 2.5,
        [Description("Largeur du cadre ; 0 (par défaut) = pas de retour à la ligne automatique.")] double width = 0,
        [Description("Rotation en degrés.")] double rotation = 0,
        [Description("Point d'attache : top_left (défaut), top_center, top_right, middle_left, middle_center, " +
                     "middle_right, bottom_left, bottom_center ou bottom_right.")] string? attachment = null,
        [Description("Style de texte existant dans le dessin (voir list_text_styles).")] string? style = null,
        [Description("Masque d'arrière-plan : cadre plein derrière le texte, qui cache ce qu'il recouvre.")] bool backgroundMask = false,
        [Description("Couleur du masque ; couleur du fond d'écran si omise. " + ToolHelp.Color)] string? maskColor = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_mtext",
            new { text, position, height, width, rotation, attachment, style, backgroundMask, maskColor, layer, color },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_hatch", Title = "Créer une hachure",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Hachure ou remplit une zone, délimitée soit par des contours fermés existants (boundaries, " +
                 "hachure associative qui suit leurs modifications), soit par une liste de sommets (points). Motif " +
                 "(pattern) ou remplissage en dégradé de couleurs (gradient). Renvoie l'aire hachurée. Pour un contour " +
                 "fermé par plusieurs objets qui se croisent, créez-le d'abord avec create_boundary.")]
    public Task<string> CreateHatch(
        [Description("Handles de contours fermés (polylignes fermées, cercles…). Un contour intérieur forme un îlot non hachuré.")] string[]? boundaries = null,
        [Description("Sommets d'un contour, par exemple [[0,0],[10,0],[10,5],[0,5]] (fermé automatiquement).")] double[][]? points = null,
        [Description("Motif : SOLID (remplissage plein, par défaut), ANSI31 (hachures à 45°), ANSI37, AR-CONC " +
                     "(béton), AR-SAND, GRAVEL, NET, DOTS…")] string? pattern = null,
        [Description("Échelle du motif (1 par défaut) ; à augmenter pour un dessin en mètres à grande étendue.")] double scale = 1,
        [Description("Angle du motif ou direction du dégradé, en degrés.")] double angle = 0,
        [Description("Hachure associative (avec boundaries uniquement, vrai par défaut).")] bool associative = true,
        [Description("Dégradé à la place d'un motif : linear, cylinder, invcylinder, spherical, invspherical, " +
                     "hemispherical, invhemispherical, curved ou invcurved.")] string? gradient = null,
        [Description("Couleurs du dégradé : une (vers le blanc) ou deux, par exemple [\"5\", \"255,255,153\"]. " +
                     "Bleu vers blanc par défaut.")] string[]? gradientColors = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        [Description(ToolHelp.Color)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_hatch",
            new { boundaries, points, pattern, scale, angle, associative, gradient, gradientColors, layer, color },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_dimension", Title = "Créer une cotation",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une cote avec le style de cote courant (ou style). Paramètres selon le type :\n" +
                 "- aligned, horizontal, vertical : start, end, et offset (distance de la ligne de cote : " +
                 "perpendiculaire, positive à gauche du sens start→end ; pour horizontal vers +Y, pour vertical " +
                 "vers -X) ou dimLinePoint ;\n" +
                 "- rotated : idem, avec angle (direction de la ligne de cote, en degrés) ;\n" +
                 "- radius, diameter : handle d'un cercle ou d'un arc (ou center et radius), angle de la ligne " +
                 "de cote (45° par défaut) ;\n" +
                 "- angular : center (sommet), start et end (un point sur chaque branche), arcPoint ou radius " +
                 "pour placer l'arc de cote ;\n" +
                 "- ordinate : coordonnée X ou Y du point start, mesurée depuis origin (0,0 par défaut), texte au " +
                 "bout de la ligne de repère end ; axis x ou y, sinon déduit comme la commande COTORD (ligne de " +
                 "repère plutôt verticale : X mesuré) ;\n" +
                 "- arc_length : handle d'un arc, placé à offset du milieu de l'arc vers l'extérieur (20 % du rayon " +
                 "par défaut) ou par arcPoint.\n" +
                 "Renvoie la mesure et la hauteur réelle du texte de cote, à comparer à l'échelle du dessin.")]
    public Task<string> CreateDimension(
        [Description("Type : aligned, horizontal, vertical, rotated, radius, diameter, angular, ordinate ou arc_length.")] string type,
        [Description("Premier point mesuré (linéaires), point sur la première branche (angular) ou point coté (ordinate).")] double[]? start = null,
        [Description("Second point mesuré (linéaires), point sur la seconde branche (angular) ou bout de la ligne de repère (ordinate).")] double[]? end = null,
        [Description("Distance entre les points mesurés et la ligne de cote (linéaires), ou entre l'arc et la cote (arc_length).")] double? offset = null,
        [Description("Point par lequel passe la ligne de cote (linéaires), à la place de offset.")] double[]? dimLinePoint = null,
        [Description("rotated : direction de la ligne de cote ; radius/diameter : direction de la ligne de cote. En degrés.")] double? angle = null,
        [Description("radius/diameter : handle du cercle ou de l'arc coté ; arc_length : handle de l'arc.")] string? handle = null,
        [Description("radius/diameter : centre, si pas de handle ; angular : sommet de l'angle.")] double[]? center = null,
        [Description("radius/diameter : rayon, si pas de handle ; angular : rayon de l'arc de cote.")] double? radius = null,
        [Description("angular, arc_length : point par lequel passe l'arc de cote.")] double[]? arcPoint = null,
        [Description("ordinate : x (abscisse mesurée) ou y (ordonnée mesurée).")] string? axis = null,
        [Description("ordinate : origine des coordonnées mesurées, [0, 0] par défaut.")] double[]? origin = null,
        [Description("Texte imposé ; « <> » y insère la valeur mesurée. Mesure seule si omis.")] string? text = null,
        [Description("Style de cote existant ; style courant si omis.")] string? style = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_dimension",
            new { type, start, end, offset, dimLinePoint, angle, handle, center, radius, arcPoint, axis, origin, text, style, layer },
            cancellationToken: cancellationToken);

    // ----- Blocs -----

    [McpServerTool(Name = "list_blocks", Title = "Lister les blocs",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les définitions de blocs du dessin : nombre d'objets, nombre d'insertions, bloc dynamique, " +
                 "et attributs (étiquette, invite, valeur par défaut).")]
    public Task<string> ListBlocks(
        [Description("Noms à retenir ; les jokers * et ? sont acceptés. Tous si omis.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_blocks", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_block", Title = "Créer un bloc à partir d'objets",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Définit un bloc à partir d'objets existants (symbole, cartouche…), avec des attributs " +
                 "facultatifs. Par défaut (mode convert), les objets sont remplacés par une insertion du bloc, " +
                 "comme avec la commande BLOC.")]
    public Task<string> CreateBlock(
        [Description("Nom du nouveau bloc.")] string name,
        [Description("Point de base du bloc (point d'insertion), dans les coordonnées du dessin.")] double[] basePoint,
        [Description(HandlesHelp)] string[] handles,
        [Description("Définitions d'attributs, avec position dans les coordonnées du dessin.")] AttributeDefinitionInput[]? attributes = null,
        [Description("convert (défaut) : remplacer les objets par le bloc ; keep : les conserver ; delete : les supprimer.")] string? mode = null,
        [Description("Description du bloc.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_block",
            new { name, basePoint, handles, attributes, mode, description }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "insert_block", Title = "Insérer un bloc",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Insère un bloc défini dans le dessin (voir list_blocks), avec échelle, rotation et valeurs " +
                 "d'attributs ; les attributs non fournis prennent leur valeur par défaut.")]
    public Task<string> InsertBlock(
        [Description("Nom du bloc.")] string name,
        [Description("Point d'insertion, [x, y] ou [x, y, z].")] double[] position,
        [Description("Échelle uniforme (1 par défaut).")] double scale = 1,
        [Description("Échelles distinctes [x, y, z], à la place de scale.")] double[]? scaleFactors = null,
        [Description("Rotation en degrés.")] double rotation = 0,
        [Description("Valeurs d'attributs par étiquette, par exemple {\"TITRE\": \"Plan de masse\", \"ECHELLE\": \"1/500\"}.")] Dictionary<string, string>? attributes = null,
        [Description(ToolHelp.Layer)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("insert_block",
            new { name, position, scale = scaleFactors is null ? (object)scale : scaleFactors, rotation, attributes, layer },
            cancellationToken: cancellationToken);

    // ----- Modifications -----

    [McpServerTool(Name = "copy_entities", Title = "Copier des objets",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Copie des objets d'un vecteur, ou d'un point à un autre. Avec count > 1, crée des copies " +
                 "successives régulièrement espacées (réseau linéaire). Les attributs de blocs et les données " +
                 "d'objet sont copiés aussi.")]
    public Task<string> CopyEntities(
        [Description(HandlesHelp)] string[] handles,
        [Description("Vecteur [dx, dy] ou [dx, dy, dz] entre deux copies. À défaut, utilisez from et to.")] double[]? displacement = null,
        [Description("Point de départ du déplacement.")] double[]? from = null,
        [Description("Point d'arrivée du déplacement.")] double[]? to = null,
        [Description("Nombre de copies (1 par défaut).")] int count = 1,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("copy_entities",
            new { handles, displacement, from, to, count }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "mirror_entities", Title = "Symétrie d'objets",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée le symétrique d'objets par rapport à un axe du plan défini par deux points. Par défaut les " +
                 "originaux sont conservés et les textes restent lisibles, comme la commande MIROIR.")]
    public Task<string> MirrorEntities(
        [Description(HandlesHelp)] string[] handles,
        [Description("Premier point de l'axe de symétrie.")] double[] axisStart,
        [Description("Second point de l'axe de symétrie.")] double[] axisEnd,
        [Description("Supprimer les originaux (retourner les objets sur place).")] bool eraseOriginals = false,
        [Description("Garder les textes lisibles plutôt qu'inversés.")] bool keepTextReadable = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("mirror_entities",
            new { handles, axisStart, axisEnd, eraseOriginals, keepTextReadable }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "scale_entities", Title = "Mettre des objets à l'échelle",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Met des objets à l'échelle par rapport à un point de base.")]
    public Task<string> ScaleEntities(
        [Description(HandlesHelp)] string[] handles,
        [Description("Point de base, qui reste fixe.")] double[] basePoint,
        [Description("Facteur d'échelle, strictement positif (2 = doubler, 0.5 = réduire de moitié).")] double factor,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("scale_entities", new { handles, basePoint, factor }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "offset_entities", Title = "Décaler des courbes",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée des parallèles à des courbes (lignes, polylignes, cercles, arcs) à une distance donnée, " +
                 "du côté du point side, comme la commande DECALER. Les nouvelles courbes gardent calque et " +
                 "propriétés.")]
    public Task<string> OffsetEntities(
        [Description("Handles des courbes à décaler.")] string[] handles,
        [Description("Distance de décalage.")] double distance,
        [Description("Point indiquant de quel côté décaler. Sans point, le signe de distance fixe le côté selon le sens de la courbe.")] double[]? side = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("offset_entities", new { handles, distance, side }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "edit_text", Title = "Modifier un texte, une cote, un tableau ou des attributs",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Modifie le contenu ou la hauteur d'un texte (TEXT ou MTEXT), le texte imposé d'une cote (chaîne " +
                 "vide pour revenir à la mesure), des cellules d'un tableau (cells ; une ligne au-delà de la dernière " +
                 "ajoute les lignes manquantes), ou les valeurs d'attributs d'une référence de bloc (cartouche, " +
                 "étiquette).")]
    public Task<string> EditText(
        [Description("Handle du texte, de la cote, du tableau ou de la référence de bloc.")] string handle,
        [Description("Nouveau contenu (texte ou cote).")] string? text = null,
        [Description("Nouvelle hauteur de texte (pour un tableau : celle des cellules modifiées).")] double? height = null,
        [Description("Référence de bloc : nouvelles valeurs par étiquette, par exemple {\"DATE\": \"25/09/2026\"}.")] Dictionary<string, string>? attributes = null,
        [Description("Tableau : cellules à écrire, lignes et colonnes comptées à partir de 0, titre compris (list_entities " +
                     "donne le contenu actuel).")] TableCellInput[]? cells = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("edit_text", new { handle, text, height, attributes, cells }, cancellationToken: cancellationToken);
}

/// <summary>Cellule de tableau pour edit_text.</summary>
public sealed record TableCellInput(
    [property: Description("Ligne, à partir de 0 (le titre fusionné est la ligne 0).")] int Row,
    [property: Description("Colonne, à partir de 0.")] int Column,
    [property: Description("Texte de la cellule ; chaîne vide pour la vider.")] string Text);

/// <summary>Définition d'attribut pour create_block.</summary>
public sealed record AttributeDefinitionInput(
    [property: Description("Étiquette, sans espace (mise en majuscules).")] string Tag,
    [property: Description("Invite affichée à l'insertion.")] string? Prompt = null,
    [property: Description("Valeur par défaut.")] string? Default = null,
    [property: Description("Position du texte d'attribut dans les coordonnées du dessin ; point de base si omise.")] double[]? Position = null,
    [property: Description("Hauteur du texte (2,5 par défaut).")] double? Height = null);

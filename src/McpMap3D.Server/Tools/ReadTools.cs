using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

[McpServerToolType]
public sealed class ReadTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_drawing_info", Title = "Informations sur le dessin ouvert",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Décrit le dessin actif d'AutoCAD Map 3D : fichier, unités (unité d'insertion, formats et précisions), étendue, calque courant, espace courant, " +
                 "nombre de calques et d'objets de l'espace objet, présentations, et pour Map 3D le système de " +
                 "coordonnées et le nombre de tables de données d'objet.")]
    public Task<string> GetDrawingInfo(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("get_drawing_info", cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_drawing_units", Title = "Changer les unités du dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Change les unités du dessin, comme la commande UNITES : unité d'insertion (INSUNITS), format et " +
                 "précision des longueurs et des angles, direction de l'angle 0 et sens des angles, système métrique " +
                 "ou impérial. Seuls les réglages fournis changent. Par défaut les objets gardent leurs coordonnées : " +
                 "avec scaleContent=true, ceux de l'espace objet sont mis à l'échelle de l'ancienne unité vers la " +
                 "nouvelle (de mm en m : facteur 0,001), calques verrouillés compris, et les fenêtres de présentation " +
                 "gardent leur cadrage. Refusé sur un dessin géoréférencé (système de coordonnées Map 3D) sauf force. " +
                 "Renvoie les réglages avant et après ; get_drawing_info les donne aussi.")]
    public Task<string> SetDrawingUnits(
        [Description("Unité d'insertion : millimeters (mm), centimeters (cm), decimeters (dm), meters (m), kilometers (km), " +
                     "inches (in), feet (ft), yards, miles, USSurveyFeet, unitless…")] string? insertionUnits = null,
        [Description("Format des longueurs : decimal, scientific, engineering, architectural, fractional.")] string? lengthFormat = null,
        [Description("Nombre de décimales des longueurs, de 0 à 8.")] int? lengthPrecision = null,
        [Description("Format des angles : degrees, dms (degrés/minutes/secondes), grads, radians, surveyor.")] string? angleFormat = null,
        [Description("Nombre de décimales des angles, de 0 à 8.")] int? anglePrecision = null,
        [Description("Direction de l'angle 0, en degrés depuis l'axe X (0 : est, 90 : nord).")] double? angleBase = null,
        [Description("Angles comptés dans le sens horaire (false : trigonométrique, par défaut d'AutoCAD).")] bool? clockwise = null,
        [Description("Système de mesure : metric ou imperial (fichiers de motifs de hachures et de types de ligne).")] string? measurement = null,
        [Description("Mettre à l'échelle les objets de l'espace objet pour garder leur taille réelle quand l'unité d'insertion change.")] bool scaleContent = false,
        [Description("Point de base de la mise à l'échelle [x, y, z] ; origine si omis.")] double[]? basePoint = null,
        [Description("Mettre à l'échelle même un dessin géoréférencé, seulement si l'utilisateur le demande.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_drawing_units",
            new { insertionUnits, lengthFormat, lengthPrecision, angleFormat, anglePrecision, angleBase, clockwise, measurement,
                  scaleContent, basePoint, force },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_layers", Title = "Lister les calques",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les calques du dessin avec leur couleur, type de ligne, épaisseur, état (éteint, gelé, " +
                 "verrouillé, traçable) et description.")]
    public Task<string> ListLayers(
        [Description("Noms de calques à retenir ; les jokers * et ? sont acceptés, par exemple [\"EAU*\"]. Tous si omis.")]
        string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_layers", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_linetypes", Title = "Lister les types de ligne",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les types de ligne chargés dans le dessin et, sur demande, ceux que définit le fichier .lin " +
                 "d'AutoCAD (acadiso.lin en métrique). Ces derniers sont chargés automatiquement dès qu'un outil " +
                 "les utilise.")]
    public Task<string> ListLinetypes(
        [Description("Inclure les types de ligne disponibles dans le fichier .lin mais pas encore chargés.")] bool includeAvailable = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_linetypes", new { includeAvailable }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_entities", Title = "Lister les objets du dessin",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les objets du dessin avec leur handle, leur type et leur calque, plus un résumé selon le type " +
                 "(longueur, aire, centre, texte, bloc). Le handle sert ensuite aux outils de modification et aux " +
                 "données d'objet. Filtres par calque, type et conditions where sur les propriétés, attributs de bloc " +
                 "ou données d'objet. Résultat paginé : « total » donne le nombre d'objets correspondant au filtre ; " +
                 "pour des totaux (longueurs, aires, nombres par valeur), utilisez summarize_entities.")]
    public Task<string> ListEntities(
        [Description("Calques à retenir ; les jokers * et ? sont acceptés. Tous si omis.")]
        string[]? layers = null,
        [Description("Types DXF à retenir, par exemple LINE, LWPOLYLINE, TEXT, MTEXT, INSERT, CIRCLE, ARC, HATCH. Tous si omis.")]
        string[]? types = null,
        [Description("Espace à parcourir : model (défaut), paper, current, ou le nom d'une présentation.")]
        string? space = null,
        [Description("Index du premier objet renvoyé, pour la pagination.")]
        int offset = 0,
        [Description("Nombre maximal d'objets renvoyés : 100 par défaut, 1000 au maximum.")]
        int limit = 100,
        [Description("Ajoute les détails géométriques : points, sommets, angles, échelles et limites.")]
        bool includeGeometry = false,
        [Description("Handles d'objets précis à décrire ; sans where, les autres filtres et la pagination sont alors ignorés.")]
        string[]? handles = null,
        [Description(ToolHelp.Where)]
        EntityCondition[]? where = null,
        [Description("Parcourir les objets sélectionnés par l'utilisateur dans AutoCAD, au lieu d'un espace.")]
        bool selection = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_entities",
            new { layers, types, space, offset, limit, includeGeometry, handles, where, selection = selection ? true : (bool?)null },
            cancellationToken: cancellationToken);
}

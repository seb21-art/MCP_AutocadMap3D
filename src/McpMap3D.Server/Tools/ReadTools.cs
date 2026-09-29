using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

[McpServerToolType]
public sealed class ReadTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_drawing_info", Title = "Informations sur le dessin ouvert",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Décrit le dessin actif d'AutoCAD Map 3D : fichier, unités, étendue, calque courant, espace courant, " +
                 "nombre de calques et d'objets de l'espace objet, présentations, et pour Map 3D le système de " +
                 "coordonnées et le nombre de tables de données d'objet.")]
    public Task<string> GetDrawingInfo(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("get_drawing_info", cancellationToken: cancellationToken);

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
                 "données d'objet. Résultat paginé : « total » donne le nombre d'objets correspondant au filtre.")]
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
        [Description("Handles d'objets précis à décrire ; les autres filtres et la pagination sont alors ignorés.")]
        string[]? handles = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_entities",
            new { layers, types, space, offset, limit, includeGeometry, handles }, cancellationToken: cancellationToken);
}

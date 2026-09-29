using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Références externes (Xref) de dessins DWG.</summary>
[McpServerToolType]
public sealed class XrefTools(PluginClient plugin)
{
    private const string NamesHelp =
        "Noms des Xref (voir list_xrefs), jokers * et ? acceptés, par exemple [\"FOND*\"].";

    private const string PathTypeHelp =
        "Chemin enregistré dans le dessin : auto (par défaut : relatif au dessin hôte s'il est enregistré et sur le " +
        "même lecteur, sinon complet), full (chemin complet), relative (relatif au dossier du dessin hôte) ou none " +
        "(nom de fichier seul, retrouvé dans le dossier du dessin et les chemins de recherche d'AutoCAD).";

    private const string TypeHelp =
        "attach (par défaut) : la Xref suit le dessin quand celui-ci est lui-même attaché ailleurs ; overlay " +
        "(superposition) : elle est ignorée dans ce cas, ce qui évite les références circulaires.";

    [McpServerTool(Name = "list_xrefs", Title = "Lister les Xref",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les références externes (Xref) DWG du dessin, imbriquées comprises : type (attach ou overlay), " +
                 "état (Resolved = chargée, Unloaded = déchargée, FileNotFound = fichier introuvable, Unresolved, " +
                 "Unreferenced = plus aucune insertion), chemin enregistré et fichier réellement chargé, Xref " +
                 "parentes d'une Xref imbriquée, et insertions (handle, position, échelle, rotation, calque, espace). " +
                 "Les calques d'une Xref s'appellent NOM|calque dans list_layers. Le dessin n'est pas modifié.")]
    public Task<string> ListXrefs(
        [Description("Filtre sur les noms, jokers * et ? acceptés. Toutes les Xref si omis.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_xrefs", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "attach_xref", Title = "Attacher une Xref",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Attache ou superpose un dessin DWG en référence externe et insère une référence dans l'espace " +
                 "courant, comme la commande ATTACHER. Si une Xref du même nom désigne déjà ce fichier, elle est " +
                 "insérée une fois de plus.")]
    public Task<string> AttachXref(
        [Description("Fichier DWG à attacher, chemin complet ou relatif au dossier du dessin hôte.")] string filePath,
        [Description("Nom de la Xref dans le dessin. Par défaut, le nom du fichier sans extension.")] string? name = null,
        [Description(TypeHelp)] string type = "attach",
        [Description(PathTypeHelp)] string pathType = "auto",
        [Description("Point d'insertion [x, y] ou [x, y, z] en SCG, [0,0,0] par défaut.")] double[]? position = null,
        [Description("Échelle uniforme (1 par défaut).")] double scale = 1,
        [Description("Échelles distinctes [x, y, z], à la place de scale.")] double[]? scaleFactors = null,
        [Description("Rotation en degrés.")] double rotation = 0,
        [Description(ToolHelp.Layer)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("attach_xref",
            new
            {
                filePath, name, type, pathType, position, scale = scaleFactors is null ? (object)scale : scaleFactors,
                rotation, layer,
            },
            busyTimeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);

    [McpServerTool(Name = "detach_xrefs", Title = "Détacher des Xref",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Détache des Xref, comme DETACHER dans la palette des références externes : toutes leurs insertions " +
                 "et leur définition sont supprimées du dessin, avec leurs Xref imbriquées qui ne servent plus. Une " +
                 "Xref imbriquée ne se détache pas seule.")]
    public Task<string> DetachXrefs(
        [Description(NamesHelp)] string[] names,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("detach_xrefs", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "reload_xrefs", Title = "Recharger des Xref",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Recharge des Xref depuis leur fichier, pour afficher les dernières modifications ou recharger une " +
                 "Xref déchargée ; ses Xref imbriquées sont rechargées avec elle. Toutes les Xref de premier niveau si " +
                 "names est omis. Renvoie l'état de chacune après rechargement, avec l'erreur d'AutoCAD (ReloadError, " +
                 "par exemple FileAccessErr pour un fichier introuvable) ; NotLoaded liste celles qui ne sont pas " +
                 "chargées.")]
    public Task<string> ReloadXrefs(
        [Description(NamesHelp + " Toutes les Xref de premier niveau si omis.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("reload_xrefs", new { names }, busyTimeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "unload_xrefs", Title = "Décharger des Xref",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Décharge des Xref : elles ne sont plus affichées ni chargées en mémoire, mais restent attachées, " +
                 "avec leurs insertions, et se rechargent avec reload_xrefs. Une Xref imbriquée ne se décharge pas seule.")]
    public Task<string> UnloadXrefs(
        [Description(NamesHelp)] string[] names,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("unload_xrefs", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "bind_xrefs", Title = "Lier des Xref",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Lie des Xref chargées au dessin, comme LIER : leur contenu devient un bloc ordinaire, qui ne suit " +
                 "plus le fichier d'origine. Leurs Xref imbriquées sont liées avec elles. Une Xref imbriquée ne se lie " +
                 "pas seule.")]
    public Task<string> BindXrefs(
        [Description(NamesHelp)] string[] names,
        [Description("bind (par défaut) : calques, styles et blocs de la Xref gardent un préfixe, NOM$0$calque ; " +
                     "insert : ils prennent leur nom d'origine et fusionnent avec ceux du dessin de même nom.")]
        string mode = "bind",
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("bind_xrefs", new { names, mode }, busyTimeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "edit_xref", Title = "Modifier une Xref",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Modifie une Xref : nouveau fichier ou forme du chemin enregistré (pour réparer une Xref introuvable " +
                 "ou passer en chemin relatif), renommage, passage d'attach à overlay ou inverse. Un changement de " +
                 "chemin recharge la Xref ; un rechargement impossible est signalé par ReloadError sans annuler la " +
                 "modification. Renvoie la Xref modifiée, comme list_xrefs.")]
    public Task<string> EditXref(
        [Description("Nom actuel de la Xref (voir list_xrefs).")] string name,
        [Description("Nouveau fichier DWG (chemin complet ou relatif au dossier du dessin hôte). Omis avec pathType : même fichier, chemin enregistré sous une autre forme.")]
        string? filePath = null,
        [Description(PathTypeHelp)] string? pathType = null,
        [Description("Nouveau nom de la Xref.")] string? newName = null,
        [Description("Nouveau type : " + TypeHelp)] string? type = null,
        [Description("Recharger la Xref après la modification. Par défaut : oui si le chemin change, non sinon.")]
        bool? reload = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("edit_xref", new { name, filePath, pathType, newName, type, reload },
            busyTimeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
}

using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Outils de gestion de l'espace Présentation (Layouts) et des fenêtres de présentation (Viewports).
/// </summary>
[McpServerToolType]
public sealed class LayoutTools(PluginClient plugin)
{
    [McpServerTool(Name = "list_layouts", Title = "Lister les présentations",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste toutes les présentations (Layouts) du dessin avec leur ordre d'onglet, format de papier " +
                 "(largeur, hauteur, mm/pouces, nom du média), imprimante/traceur configuré, statut courant et nombre de fenêtres.")]
    public Task<string> ListLayouts(
        [Description("Noms de présentations à filtrer ; les jokers * et ? sont acceptés. Toutes si omis.")]
        string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_layouts", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_current_layout", Title = "Activer une présentation ou l'espace Objet",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bascule l'affichage actif vers la présentation spécifiée (ou vers l'espace Objet avec « Model » ou « Objet »). " +
                 "Rend l'espace papier de cette présentation actif pour que les outils de dessin existants (create_rectangle, " +
                 "create_text, insert_block pour cartouche...) y ajoutent directement leurs objets.")]
    public Task<string> SetCurrentLayout(
        [Description("Nom de la présentation à activer, ou « Model » / « Objet » pour revenir à l'espace Objet.")]
        string name,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_current_layout", new { name }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_layout", Title = "Créer une présentation",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une nouvelle présentation (Layout) dans le dessin, éventuellement en copiant une présentation existante.")]
    public Task<string> CreateLayout(
        [Description("Nom de la nouvelle présentation.")]
        string name,
        [Description("Nom d'une présentation existante à dupliquer (copie les mises en page et objets papier).")]
        string? copyFrom = null,
        [Description("Rendre immédiatement cette nouvelle présentation courante.")]
        bool current = false,
        [Description("Format de papier par défaut (ex: \"A3\", \"A4\", \"A2\", \"A1\", \"A0\"). A3 par défaut si copyFrom est omis.")]
        string? paperSize = null,
        [Description("Orientation de la feuille (\"landscape\" ou \"portrait\"). Paysage par défaut.")]
        string? orientation = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_layout", new { name, copyFrom, current, paperSize, orientation }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "rename_layout", Title = "Renommer une présentation",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Renomme un onglet de présentation existant (l'espace Objet « Model » ne peut pas être renommé).")]
    public Task<string> RenameLayout(
        [Description("Nom actuel de la présentation.")]
        string oldName,
        [Description("Nouveau nom de la présentation.")]
        string newName,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("rename_layout", new { oldName, newName }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_layout", Title = "Supprimer une présentation",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime une présentation du dessin. L'espace Objet (« Model ») ou la dernière présentation restante ne peuvent pas être supprimés.")]
    public Task<string> DeleteLayout(
        [Description("Nom de la présentation à supprimer.")]
        string name,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_layout", new { name }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_viewports", Title = "Lister les fenêtres de présentation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les fenêtres de présentation (Viewports) d'une présentation donnée (ou de la présentation courante) : " +
                 "handle, position sur la feuille, dimensions, centre de vue cible dans le modèle, échelle et verrouillage.")]
    public Task<string> ListViewports(
        [Description("Nom de la présentation à inspecter. Présentation courante si omise.")]
        string? layout = null,
        [Description("Inclure la fenêtre globale de l'espace papier (Number = 1). Faux par défaut.")]
        bool includeOverall = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_viewports", new { layout, includeOverall }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_viewport", Title = "Créer une fenêtre de présentation",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une fenêtre de présentation (Viewport) sur la feuille de la présentation courante. " +
                 "Basculez d'abord sur la présentation avec set_current_layout.")]
    public Task<string> CreateViewport(
        [Description("Point central de la fenêtre sur la feuille papier en mm : [x, y] ou [x, y, z].")]
        double[] center,
        [Description("Largeur de la fenêtre sur la feuille papier en mm.")]
        double width,
        [Description("Hauteur de la fenêtre sur la feuille papier en mm.")]
        double height,
        [Description("Coordonnées [x, y] dans l'espace Objet à centrer dans la fenêtre. Centre de l'étendue du dessin si omis.")]
        double[]? viewCenter = null,
        [Description("Échelle de la vue : ratio textuel (ex: \"1:500\", \"1/1000\", \"1:1\"), ou valeur numérique directe.")]
        object? scale = null,
        [Description("Valeur numérique directe de CustomScale (rapport unités papier / unités objet).")]
        double? customScale = null,
        [Description("Verrouiller la vue de la fenêtre pour empêcher le zoom et déplacement intempestifs. Faux par défaut.")]
        bool locked = false,
        [Description("Activer l'affichage du modèle dans la fenêtre. Vrai par défaut.")]
        bool on = true,
        [Description(ToolHelp.Layer)]
        string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_viewport",
            new { center, width, height, viewCenter, scale, customScale, locked, on, layer }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_viewport", Title = "Modifier une fenêtre de présentation",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Modifie les propriétés d'une fenêtre de présentation existante (échelle, centre de vue cible dans le modèle, " +
                 "dimensions, verrouillage, calque).")]
    public Task<string> SetViewport(
        [Description("Handle de la fenêtre à modifier.")]
        string handle,
        [Description("Nouveau centre de la fenêtre sur la feuille papier en mm : [x, y] ou [x, y, z]. Inchangé si omis.")]
        double[]? center = null,
        [Description("Nouvelle largeur sur la feuille papier en mm. Inchangée si omise.")]
        double? width = null,
        [Description("Nouvelle hauteur sur la feuille papier en mm. Inchangée si omise.")]
        double? height = null,
        [Description("Nouveau centre de la vue dans l'espace Objet [x, y]. Inchangé si omis.")]
        double[]? viewCenter = null,
        [Description("Nouvelle échelle : ratio textuel (ex: \"1:500\", \"1/1000\"), ou valeur numérique.")]
        object? scale = null,
        [Description("Nouvelle valeur numérique directe de CustomScale.")]
        double? customScale = null,
        [Description("Verrouiller (true) ou déverrouiller (false) la fenêtre.")]
        bool? locked = null,
        [Description("Activer (true) ou désactiver (false) l'affichage de la fenêtre.")]
        bool? on = null,
        [Description(ToolHelp.Layer)]
        string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_viewport",
            new { handle, center, width, height, viewCenter, scale, customScale, locked, on, layer }, cancellationToken: cancellationToken);
}

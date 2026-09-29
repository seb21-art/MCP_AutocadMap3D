using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Gestion des dessins ouverts dans AutoCAD : liste, création, ouverture, enregistrement et fermeture.
/// </summary>
[McpServerToolType]
public sealed class DrawingTools(PluginClient plugin)
{
    private const string DrawingHelp =
        "Dessin ouvert visé : chemin complet ou nom de fichier (voir list_drawings). Dessin actif si omis.";

    private const string PathHelp =
        "Chemin du fichier .dwg ; un chemin relatif part du dossier du dessin actif (ou de Documents s'il n'est pas " +
        "enregistré), l'extension .dwg est ajoutée si elle manque.";

    [McpServerTool(Name = "list_drawings", Title = "Lister les dessins ouverts",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les dessins ouverts dans AutoCAD : nom, chemin du fichier (null s'il n'a jamais été " +
                 "enregistré), dessin actif, lecture seule et modifications non enregistrées. Les autres outils " +
                 "agissent toujours sur le dessin actif : changez-le avec open_drawing.")]
    public Task<string> ListDrawings(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("list_drawings", cancellationToken: cancellationToken);

    [McpServerTool(Name = "new_drawing", Title = "Créer un dessin",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un dessin à partir d'un gabarit (.dwt) et en fait le dessin actif, comme la commande NOUVEAU. " +
                 "Avec filePath, l'enregistre aussitôt en .dwg. Ne s'annule pas avec U : fermez-le avec close_drawing.")]
    public Task<string> NewDrawing(
        [Description("Gabarit : chemin complet, ou nom cherché dans le dossier des gabarits d'AutoCAD puis dans ses " +
                     "chemins de recherche (par exemple acadiso.dwt). Par défaut, le gabarit de NOUVEAU rapide des options " +
                     "d'AutoCAD, sinon acadiso.dwt.")]
        string? template = null,
        [Description("Enregistrer aussitôt le dessin sous ce chemin. " + PathHelp)] string? filePath = null,
        [Description("Remplacer le fichier filePath s'il existe déjà.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("new_drawing", new { template, filePath, overwrite }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "open_drawing", Title = "Ouvrir un dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ouvre un fichier .dwg, .dxf ou .dwt et en fait le dessin actif. Si le dessin est déjà ouvert, " +
                 "l'active simplement : c'est aussi le moyen de passer d'un dessin ouvert à un autre, par son nom " +
                 "(Dessin1.dwg) ou son chemin. Un fichier déjà ouvert par un autre programme ne s'ouvre qu'en lecture seule.")]
    public Task<string> OpenDrawing(
        [Description("Fichier à ouvrir, ou nom d'un dessin déjà ouvert à activer. Un chemin relatif part du dossier " +
                     "du dessin actif.")]
        string filePath,
        [Description("Ouvrir en lecture seule.")] bool readOnly = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("open_drawing", new { filePath, readOnly },
            busyTimeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);

    [McpServerTool(Name = "save_drawing", Title = "Enregistrer un dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Enregistre un dessin ouvert sous son nom (commande ENREG), ou sous un autre nom avec filePath " +
                 "(commande ENREGSOUS : le dessin prend alors ce nouveau nom). Un dessin jamais enregistré demande " +
                 "filePath. N'écrase un autre fichier existant qu'avec overwrite.")]
    public Task<string> SaveDrawing(
        [Description(DrawingHelp)] string? drawing = null,
        [Description("Enregistrer sous ce chemin. " + PathHelp)] string? filePath = null,
        [Description("Remplacer le fichier filePath s'il existe déjà.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("save_drawing", new { drawing, filePath, overwrite },
            busyTimeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);

    [McpServerTool(Name = "close_drawing", Title = "Fermer un dessin",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Ferme un dessin ouvert. S'il a des modifications non enregistrées, l'outil refuse de le fermer, " +
                 "sauf avec save (enregistrer d'abord, filePath requis pour un dessin jamais enregistré) ou " +
                 "discardChanges (abandonner les modifications, irréversible : ne l'utilisez qu'à la demande " +
                 "explicite de l'utilisateur).")]
    public Task<string> CloseDrawing(
        [Description(DrawingHelp)] string? drawing = null,
        [Description("Enregistrer le dessin avant de le fermer.")] bool save = false,
        [Description("Enregistrer sous ce chemin avant de fermer (avec save). " + PathHelp)] string? filePath = null,
        [Description("Remplacer le fichier filePath s'il existe déjà.")] bool overwrite = false,
        [Description("Fermer sans enregistrer, en abandonnant les modifications.")] bool discardChanges = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("close_drawing", new { drawing, save, filePath, overwrite, discardChanges },
            busyTimeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
}

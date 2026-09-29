using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Outils d'impression et d'exportation PDF d'AutoCAD.
/// </summary>
[McpServerToolType]
public sealed class PlotTools(PluginClient plugin)
{
    private static readonly TimeSpan PlotTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "list_plot_devices", Title = "Lister les périphériques d'impression et traceurs",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les traceurs et imprimantes disponibles dans AutoCAD (fichiers PC3, imprimantes système Windows, " +
                 "traceurs PDF). Si un périphérique précis est spécifié dans « device », renvoie la liste détaillée de tous les " +
                 "formats de papier (canonical media names et noms locaux) supportés par ce périphérique.")]
    public Task<string> ListPlotDevices(
        [Description("Nom d'un périphérique spécifique dont on souhaite inspecter les formats de papier. Tous si omis.")]
        string? device = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_plot_devices", new { device }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_plot_styles", Title = "Lister les tables de styles de tracé",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les tables de styles de tracé disponibles dans AutoCAD (fichiers .ctb dépendant des couleurs " +
                 "comme monochrome.ctb ou acad.ctb, et fichiers .stb nommés).")]
    public Task<string> ListPlotStyles(CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_plot_styles", cancellationToken: cancellationToken);

    [McpServerTool(Name = "export_pdf", Title = "Exporter une présentation ou l'espace objet en PDF",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Exporte le dessin actif vers un fichier PDF haute qualité à l'aide du moteur de tracé natif d'AutoCAD. " +
                 "Supporte l'espace Objet ou les présentations papier (Layouts), le choix du format de papier (A4, A3, A2, A1, A0...), " +
                 "l'orientation (portrait/paysage), la zone de tracé (étendue, présentation, fenêtre, affichage), l'échelle (ex: 1:500), " +
                 "le centrage et les styles de tracé CTB (ex: monochrome.ctb).")]
    public Task<string> ExportPdf(
        [Description("Nom de la présentation à exporter, ou « Model » / « Objet » pour l'espace Objet. Présentation courante si omis.")]
        string? layout = null,
        [Description("Chemin complet du fichier PDF à créer (ex: \"C:/Plans/Projet_A3.pdf\"). Si omis, créé à côté du fichier DWG ou dans le dossier temporaire.")]
        string? filePath = null,
        [Description("Format de papier : nom courant comme \"A4\", \"A3\", \"A2\", \"A1\", \"A0\", ou nom canonique AutoCAD (ex: \"ISO_full_bleed_A4_(210.00_x_297.00_MM)\").")]
        string? paperSize = null,
        [Description("Orientation de la feuille : \"landscape\" (paysage) ou \"portrait\".")]
        string? orientation = null,
        [Description("Zone à tracer : \"extents\" (étendue du dessin), \"layout\" (présentation papier entière), \"window\" (zone rectangulaire), \"display\" (vue écran courante). Par défaut : layout en espace papier, extents en espace objet.")]
        string? plotArea = null,
        [Description("Coordonnées de la fenêtre de tracé [minX, minY, maxX, maxY] lorsque plotArea est « window ».")]
        double[]? window = null,
        [Description("Ajuster le dessin à la feuille de papier (Fit to paper). Vrai par défaut pour l'espace objet et l'étendue.")]
        bool? fitToPaper = null,
        [Description("Échelle de tracé : ratio textuel (ex: \"1:500\", \"1/1000\", \"1:1\") ou valeur numérique directe.")]
        object? scale = null,
        [Description("Nom de la table des styles de tracé (ex: \"monochrome.ctb\", \"acad.ctb\", \"grayscale.ctb\").")]
        string? plotStyle = null,
        [Description("Centrer le tracé sur la feuille. Vrai par défaut pour l'espace objet et l'étendue.")]
        bool center = true,
        [Description("Pilote PC3 PDF à utiliser (ex: \"AutoCAD PDF (General Documentation).pc3\", \"DWG To PDF.pc3\"). Détecté automatiquement si omis.")]
        string? pdfDevice = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_pdf",
            new { layout, filePath, paperSize, orientation, plotArea, window, fitToPaper, scale, plotStyle, center, pdfDevice },
            busyTimeout: PlotTimeout,
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "plot_drawing", Title = "Tracer le dessin vers un traceur, imprimante ou fichier",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Lance un tracé complet du dessin actif vers un traceur physique, une imprimante Windows ou un pilote PC3 fichier " +
                 "(PDF, DWF, PLT, image). Consultez list_plot_devices pour la liste des périphériques installés.")]
    public Task<string> PlotDrawing(
        [Description("Nom du traceur ou imprimante cible (ex: \"DWG To PDF.pc3\", \"AutoCAD PDF (General Documentation).pc3\" ou le nom exact d'une imprimante Windows).")]
        string device,
        [Description("Nom de la présentation à tracer, ou « Model » / « Objet » pour l'espace Objet. Présentation courante si omis.")]
        string? layout = null,
        [Description("Chemin du fichier de destination si le traceur imprime dans un fichier.")]
        string? filePath = null,
        [Description("Format de papier (ex: \"A4\", \"A3\", \"ISO_A4_(210.00_x_297.00_MM)\").")]
        string? paperSize = null,
        [Description("Orientation de la feuille : \"landscape\" (paysage) ou \"portrait\".")]
        string? orientation = null,
        [Description("Zone à tracer : \"extents\", \"layout\", \"window\", \"display\".")]
        string? plotArea = null,
        [Description("Coordonnées de la fenêtre de tracé [minX, minY, maxX, maxY] lorsque plotArea est « window ».")]
        double[]? window = null,
        [Description("Ajuster le dessin à la feuille de papier (Fit to paper).")]
        bool? fitToPaper = null,
        [Description("Échelle de tracé : ratio textuel (ex: \"1:500\", \"1/1000\", \"1:1\") ou valeur numérique directe.")]
        object? scale = null,
        [Description("Nom de la table des styles de tracé (ex: \"monochrome.ctb\", \"acad.ctb\").")]
        string? plotStyle = null,
        [Description("Centrer le tracé sur la feuille.")]
        bool center = true,
        [Description("Nombre d'exemplaires à imprimer (1 par défaut).")]
        int copies = 1,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("plot_drawing",
            new { device, layout, filePath, paperSize, orientation, plotArea, window, fitToPaper, scale, plotStyle, center, copies },
            busyTimeout: PlotTimeout,
            cancellationToken: cancellationToken);
}

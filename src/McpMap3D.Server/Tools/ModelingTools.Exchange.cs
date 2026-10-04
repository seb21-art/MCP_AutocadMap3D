using System.ComponentModel;
using System.Text.Json;
using McpMap3D.Shared;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Échanges de fichiers 3D : STL, SAT, STEP et IGES.</summary>
public sealed partial class ModelingTools
{
    private const string ExchangePathHelp =
        "Chemin du fichier, relatif au dossier du dessin s'il n'est pas absolu ; extension ajoutée si elle manque. " +
        "Par défaut, le nom du dessin à côté de lui, ou dans le dossier temporaire s'il n'est pas enregistré. " +
        "Un fichier existant est remplacé.";

    [McpServerTool(Name = "export_stl", Title = "Exporter des solides en STL",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Exporte des solides 3D dans un fichier STL, pour l'impression 3D ou un autre logiciel, comme la " +
                 "commande STLOUT. Plusieurs solides sont réunis en un seul corps dans le fichier ; le dessin n'est " +
                 "pas modifié. Par défaut le modèle est ramené à l'origine (offset renvoyé) : le STL stocke ses " +
                 "coordonnées en simple précision, qui perdrait les décimales de coordonnées Lambert-93.")]
    public Task<string> ExportStl(
        [Description("Handles des solides 3D.")] string[] handles,
        [Description(ExchangePathHelp)] string? filePath = null,
        [Description("STL texte (ASCII) au lieu du binaire, plus compact.")] bool ascii = false,
        [Description("Ramener le coin minimal du modèle à l'origine. Sans cela, les solides doivent être entièrement " +
                     "du côté positif des axes X, Y et Z.")] bool moveToOrigin = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_stl",
            new { handles, filePath, ascii, moveToOrigin }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "export_sat", Title = "Exporter des solides et surfaces en SAT",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Exporte des solides, surfaces et régions dans un fichier ACIS texte (SAT), comme la commande " +
                 "EXPORTACIS : échange de modèles exacts avec d'autres modeleurs. Le dessin n'est pas modifié.")]
    public Task<string> ExportSat(
        [Description("Handles des solides, surfaces, régions ou corps.")] string[] handles,
        [Description(ExchangePathHelp)] string? filePath = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_sat", new { handles, filePath }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "import_sat", Title = "Importer un fichier SAT",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Importe les solides, surfaces et régions d'un fichier ACIS texte (SAT) dans l'espace courant, " +
                 "comme la commande IMPORTACIS. Les fichiers binaires .sab ne sont pas lus.")]
    public Task<string> ImportSat(
        [Description("Chemin du fichier .sat, relatif au dossier du dessin s'il n'est pas absolu.")] string filePath,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("import_sat", new { filePath, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "import_3d_model", Title = "Importer un modèle 3D STEP ou IGES",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Importe un fichier STEP (.stp, .step) ou IGES (.igs, .iges) dans l'espace courant, comme la " +
                 "commande IMPORT : le traducteur d'AutoCAD le convertit hors du dessin (AutoCAD reste disponible), " +
                 "puis le modèle est inséré comme un bloc, ou décomposé avec explode, en une étape d'annulation U. " +
                 "Sans scale, l'échelle convertit les unités du fichier (souvent le millimètre) vers celles du dessin " +
                 "(INSUNITS). Renvoie le bloc, la référence, les objets par type et l'étendue.")]
    public async Task<string> Import3dModel(
        [Description("Chemin absolu du fichier .stp, .step, .igs ou .iges.")] string filePath,
        [Description("Point d'insertion de l'origine du modèle, [x, y] ou [x, y, z] (origine du dessin par défaut).")] double[]? position = null,
        [Description("Échelle uniforme, à la place de la conversion d'unités automatique.")] double? scale = null,
        [Description("Nom du bloc créé, refusé s'il existe. Par défaut le nom du fichier, suffixé _2, _3… s'il est pris.")] string? blockName = null,
        [Description("Décomposer le modèle jusqu'aux objets de base (solides, surfaces…), blocs imbriqués des pièces " +
                     "d'un assemblage compris, directement dans l'espace courant.")] bool explode = false,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        [Description("Durée maximale de la conversion, en secondes (300 par défaut, 1800 au plus).")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 10, 1800);
        var conversion = await plugin.CallAsync("_convert_3d_model", new { filePath, timeoutSeconds },
            busyTimeout: TimeSpan.FromSeconds(timeoutSeconds), cancellationToken: cancellationToken).ConfigureAwait(false);
        var dwgPath = conversion.GetProperty("dwg").GetString();
        var import = await plugin.CallAsync("_insert_3d_model",
            new { dwgPath, sourceFile = filePath, position, scale, blockName, explode, layer, color, deleteDwg = true },
            busyTimeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);

        var seconds = conversion.TryGetProperty("seconds", out var value) ? value.GetDouble() : (double?)null;
        return JsonSerializer.Serialize(new { ConversionSeconds = seconds, Import = import }, PipeProtocol.JsonOptions);
    }
}

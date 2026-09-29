using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Échanges de fichiers 3D : STL et SAT.</summary>
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
}

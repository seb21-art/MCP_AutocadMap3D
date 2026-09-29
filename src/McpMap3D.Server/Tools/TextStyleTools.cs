using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Styles de texte : police SHX ou TrueType, hauteur fixe, facteur de largeur, inclinaison.</summary>
[McpServerToolType]
public sealed class TextStyleTools(PluginClient plugin)
{
    [McpServerTool(Name = "list_text_styles", Title = "Lister les styles de texte",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les styles de texte du dessin : police (fichier SHX ou famille TrueType), gras, italique, " +
                 "hauteur fixe (0 : libre), facteur de largeur, inclinaison, et le style courant.")]
    public Task<string> ListTextStyles(
        [Description("Filtre sur les noms, avec les jokers * et ?.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_text_styles", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_text_style", Title = "Créer ou modifier un style de texte",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Crée un style de texte (copie de basedOn ou du style courant) ou modifie un style existant : police, " +
                 "gras et italique (TrueType), hauteur fixe, facteur de largeur, inclinaison ; le rend courant sur " +
                 "demande. Un changement de police s'applique aussi aux textes existants du style. Le nom s'utilise " +
                 "ensuite avec create_text, create_mtext, create_table ou set_dimension_style (textStyle).")]
    public Task<string> SetTextStyle(
        [Description("Nom du style.")] string name,
        [Description("Création : style à copier ; style courant si omis.")] string? basedOn = null,
        [Description("Police : fichier SHX (romans.shx, isocp.shx), fichier TrueType (arial.ttf) ou nom de famille " +
                     "TrueType (Arial, Calibri, Segoe UI).")] string? font = null,
        [Description("Gras (police TrueType désignée par son nom de famille).")] bool? bold = null,
        [Description("Italique (police TrueType désignée par son nom de famille).")] bool? italic = null,
        [Description("Grande police SHX pour les langues asiatiques (bigfont.shx) ; chaîne vide pour la retirer.")] string? bigFont = null,
        [Description("Hauteur fixe ; 0 laisse chaque texte choisir sa hauteur (recommandé pour un style utilisé par des cotes).")] double? height = null,
        [Description("Facteur de largeur des caractères (1 : normal, 0,8 : plus étroit).")] double? widthFactor = null,
        [Description("Angle d'inclinaison des caractères en degrés, de -85 à 85.")] double? obliqueAngle = null,
        [Description("Rendre ce style courant.")] bool current = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_text_style",
            new { name, basedOn, font, bold, italic, bigFont, height, widthFactor, obliqueAngle, current },
            cancellationToken: cancellationToken);
}

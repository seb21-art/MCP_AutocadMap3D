using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Format des cotations : styles de cote, et remplacements de style sur des cotes précises.
/// Chaque appel de modification est annulable en une étape avec U.
/// </summary>
[McpServerToolType]
public sealed class DimensionTools(PluginClient plugin)
{
    private const string DimensionHandlesHelp = "Handles des cotes, tels que renvoyés par list_entities (type Dimension).";

    [McpServerTool(Name = "list_dimension_styles", Title = "Lister les styles de cote",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les styles de cote du dessin : style courant, nombre de cotes qui l'utilisent, et réglages " +
                 "(hauteur de texte, flèches, précision, unités, préfixe et suffixe, échelles, position du texte, " +
                 "couleurs, style de texte, lignes d'attache, suppression des zéros, unités angulaires).")]
    public Task<string> ListDimensionStyles(
        [Description("Noms à retenir ; les jokers * et ? sont acceptés. Tous si omis.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_dimension_styles", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_dimension_style", Title = "Créer ou modifier un style de cote",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un style de cote (copie de basedOn, ou du style courant) ou modifie un style existant, puis " +
                 "le rend courant si demandé. Seuls les réglages fournis dans format changent. Les cotes existantes " +
                 "du style sont mises à jour, sauf leurs remplacements de style.")]
    public Task<string> SetDimensionStyle(
        [Description("Nom du style : créé s'il n'existe pas, modifié sinon.")] string name,
        [Description("Création seulement : style à copier. Style courant si omis.")] string? basedOn = null,
        [Description("Réglages à appliquer au style.")] DimensionFormat? format = null,
        [Description("Rendre le style courant : les nouvelles cotes l'utiliseront.")] bool? current = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_dimension_style",
            new { name, basedOn, format, current }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_dimension_format", Title = "Modifier le format de cotes",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Modifie des cotes existantes, dans cet ordre : applique un autre style (style), efface les " +
                 "remplacements de style (clearOverrides), puis applique des réglages propres à ces cotes (format), " +
                 "qui deviennent des remplacements du style, comme avec la palette Propriétés. Pour changer toutes " +
                 "les cotes d'un style, préférez set_dimension_style.")]
    public Task<string> SetDimensionFormat(
        [Description(DimensionHandlesHelp)] string[] handles,
        [Description("Style de cote à appliquer (doit exister, voir list_dimension_styles).")] string? style = null,
        [Description("Effacer les remplacements de style : les cotes reprennent les réglages de leur style.")] bool? clearOverrides = null,
        [Description("Réglages propres à ces cotes.")] DimensionFormat? format = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_dimension_format",
            new { handles, style, clearOverrides, format }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_dimension_format", Title = "Lire le format de cotes",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Pour chaque cote : style, texte affiché, réglages effectifs, et liste des réglages remplacés " +
                 "(différents de ceux du style).")]
    public Task<string> GetDimensionFormat(
        [Description(DimensionHandlesHelp)] string[] handles,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_dimension_format", new { handles }, cancellationToken: cancellationToken);
}

/// <summary>Réglages de cote ; seuls ceux fournis sont appliqués.</summary>
public sealed record DimensionFormat(
    [property: Description("Hauteur du texte (DIMTXT), en unités du dessin avant l'échelle globale.")] double? TextHeight = null,
    [property: Description("Taille des flèches (DIMASZ).")] double? ArrowSize = null,
    [property: Description("Flèches, aux deux extrémités : closed_filled (défaut), closed_blank, closed, dot, " +
                           "small_dot, dot_blank, architectural_tick, oblique, open, open30, right_angle, origin, " +
                           "box_filled, box_blank, datum_filled, integral, none, ou le nom d'un bloc du dessin.")] string? Arrowhead = null,
    [property: Description("Nombre de décimales des cotes linéaires, 0 à 8 (DIMDEC).")] int? Precision = null,
    [property: Description("Format des unités linéaires : decimal, scientific, engineering, architectural, fractional, windows.")] string? UnitFormat = null,
    [property: Description("Séparateur décimal : « , » ou « . » (DIMDSEP).")] string? DecimalSeparator = null,
    [property: Description("Texte placé avant la mesure (chaîne vide pour le retirer).")] string? Prefix = null,
    [property: Description("Texte placé après la mesure, par exemple « m » (chaîne vide pour le retirer).")] string? Suffix = null,
    [property: Description("Échelle globale (DIMSCALE) : multiplie hauteur de texte, flèches et écarts, pas la mesure.")] double? Scale = null,
    [property: Description("Facteur appliqué à la mesure affichée (DIMLFAC), par exemple 1000 pour afficher en mm un dessin en m.")] double? MeasurementScale = null,
    [property: Description("Arrondi de la mesure, par exemple 0.05 (0 : aucun).")] double? RoundOff = null,
    [property: Description("Position du texte par rapport à la ligne de cote : centered, above, outside, jis, below.")] string? TextPosition = null,
    [property: Description("Orientation du texte : aligned (parallèle à la ligne de cote), horizontal, ou iso " +
                           "(aligné entre les lignes d'attache, horizontal à l'extérieur).")] string? TextAlignment = null,
    [property: Description("Couleur du texte. " + ToolHelp.Color)] string? TextColor = null,
    [property: Description("Couleur des lignes de cote et des flèches.")] string? LineColor = null,
    [property: Description("Couleur des lignes d'attache.")] string? ExtensionLineColor = null,
    [property: Description("Épaisseur des lignes de cote et d'attache. " + ToolHelp.LineWeight)] string? LineWeight = null,
    [property: Description("Style de texte, qui doit exister dans le dessin.")] string? TextStyle = null,
    [property: Description("Écart entre le point mesuré et le début de la ligne d'attache (DIMEXO).")] double? ExtensionOffset = null,
    [property: Description("Dépassement des lignes d'attache au-delà de la ligne de cote (DIMEXE).")] double? ExtensionBeyond = null,
    [property: Description("Écart entre le texte et la ligne de cote (DIMGAP).")] double? TextGap = null,
    [property: Description("Suppression des zéros : none, leading (0,50 → ,50), trailing (1,50 → 1,5) ou both.")] string? ZeroSuppression = null,
    [property: Description("Nombre de décimales des cotes angulaires, 0 à 8 (DIMADEC).")] int? AngularPrecision = null,
    [property: Description("Unité des cotes angulaires : degrees, dms (degrés/minutes/secondes), grads, radians, surveyor.")] string? AngularUnit = null);

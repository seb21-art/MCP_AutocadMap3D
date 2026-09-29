using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Propriétés graphiques communes : couleur, type de ligne, épaisseur de ligne (trait tracé, en mm) et
/// épaisseur d'extrusion (Thickness, hauteur donnée à un objet 2D).
/// </summary>
internal static class EntityProperties
{
    private static readonly Dictionary<string, short> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rouge"] = 1, ["red"] = 1,
        ["jaune"] = 2, ["yellow"] = 2,
        ["vert"] = 3, ["green"] = 3,
        ["cyan"] = 4,
        ["bleu"] = 5, ["blue"] = 5,
        ["magenta"] = 6,
        ["blanc"] = 7, ["white"] = 7, ["noir"] = 7, ["black"] = 7,
        ["gris"] = 8, ["gray"] = 8, ["grey"] = 8,
        ["gris clair"] = 9, ["light gray"] = 9, ["light grey"] = 9,
    };

    /// <summary>
    /// Couleur : index AutoCAD (0 à 256), « R,G,B », nom courant (rouge, bleu, vert…) ou ByLayer / ByBlock.
    /// La couleur 7 s'affiche en blanc sur fond sombre et en noir sur fond clair.
    /// </summary>
    public static Color ParseColor(string text)
    {
        text = text.Trim();
        if (string.Equals(text, "ByLayer", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "DuCalque", StringComparison.OrdinalIgnoreCase))
            return Color.FromColorIndex(ColorMethod.ByLayer, 256);

        if (string.Equals(text, "ByBlock", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "DuBloc", StringComparison.OrdinalIgnoreCase))
            return Color.FromColorIndex(ColorMethod.ByBlock, 0);

        if (ColorNames.TryGetValue(text, out var named))
            return Color.FromColorIndex(ColorMethod.ByAci, named);

        if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            return index is >= 0 and <= 256
                ? Color.FromColorIndex(ColorMethod.ByAci, index)
                : throw new PipeException(PipeErrorCodes.InvalidParams, "L'index de couleur doit être compris entre 0 et 256.");

        var parts = text.Split(',');
        if (parts.Length == 3
            && byte.TryParse(parts[0].Trim(), out var red)
            && byte.TryParse(parts[1].Trim(), out var green)
            && byte.TryParse(parts[2].Trim(), out var blue))
            return Color.FromRgb(red, green, blue);

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Couleur « {text} » non reconnue : index 1-255, « R,G,B », nom (rouge, jaune, vert, cyan, bleu, " +
            "magenta, blanc, noir, gris), ByLayer ou ByBlock.");
    }

    /// <summary>
    /// Épaisseur de ligne : valeur normalisée en millimètres (nombre ou texte, « 0.35 », « 0,35 », « 0.35 mm »),
    /// ou ByLayer, ByBlock, Default.
    /// </summary>
    public static LineWeight ReadLineWeight(ArgReader reader, string name)
    {
        if (!reader.TryGet(name, out var value))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est obligatoire.");

        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => value.GetString()!.Trim(),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » attend une épaisseur en mm ou ByLayer."),
        };

        switch (text.ToLowerInvariant())
        {
            case "bylayer" or "ducalque":
                return LineWeight.ByLayer;
            case "byblock" or "dubloc":
                return LineWeight.ByBlock;
            case "default" or "défaut" or "defaut":
                return LineWeight.ByLineWeightDefault;
        }

        var number = text.Replace("mm", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var millimetres))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Épaisseur de ligne « {text} » non reconnue.");

        // Les épaisseurs AutoCAD sont des valeurs fixes, exprimées en centièmes de millimètre.
        var hundredths = (int)Math.Round(millimetres * 100);
        return Enum.IsDefined(typeof(LineWeight), hundredths) && hundredths >= 0
            ? (LineWeight)hundredths
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Épaisseur de ligne {millimetres} mm non normalisée. Valeurs possibles (mm) : {string.Join(", ", StandardLineWeights())}.");
    }

    public static string FormatLineWeight(LineWeight weight) => weight switch
    {
        LineWeight.ByLayer => "ByLayer",
        LineWeight.ByBlock => "ByBlock",
        LineWeight.ByLineWeightDefault => "Default",
        _ => ((int)weight / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " mm",
    };

    /// <summary>
    /// Identifiant d'un type de ligne, chargé au besoin depuis le fichier .lin d'AutoCAD
    /// (acadiso.lin en unités métriques, acad.lin sinon). Le chargement fait partie de l'appel : U l'annule aussi.
    /// </summary>
    public static ObjectId EnsureLinetype(Database database, Transaction transaction, string name, out bool loaded)
    {
        loaded = false;
        var linetypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (linetypes.Has(name))
            return linetypes[name];

        var file = LinetypeFile(database);
        try
        {
            database.LoadLineTypeFile(name, file);
        }
        catch (AcException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le type de ligne « {name} » n'est ni chargé dans le dessin, ni défini dans {file}. " +
                "Utilisez list_linetypes pour voir les noms disponibles.");
        }

        linetypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
        if (!linetypes.Has(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le type de ligne « {name} » n'a pas pu être chargé depuis {file}.");

        loaded = true;
        return linetypes[name];
    }

    /// <summary>Fichier de types de ligne standard, selon le système de mesure du dessin.</summary>
    public static string LinetypeFile(Database database) =>
        database.Measurement == MeasurementValue.Metric ? "acadiso.lin" : "acad.lin";

    /// <summary>Épaisseur d'extrusion des objets 2D qui la gèrent ; null pour les autres.</summary>
    public static double? GetThickness(Entity entity) => entity switch
    {
        Line line => line.Thickness,
        Circle circle => circle.Thickness,
        Arc arc => arc.Thickness,
        Polyline polyline => polyline.Thickness,
        Polyline2d polyline => polyline.Thickness,
        DBText text => text.Thickness,
        DBPoint point => point.Thickness,
        Solid solid => solid.Thickness,
        Trace trace => trace.Thickness,
        _ => null,
    };

    /// <summary>Applique l'épaisseur d'extrusion ; renvoie false si l'objet ne la gère pas.</summary>
    public static bool TrySetThickness(Entity entity, double thickness)
    {
        switch (entity)
        {
            case Line line: line.Thickness = thickness; return true;
            case Circle circle: circle.Thickness = thickness; return true;
            case Arc arc: arc.Thickness = thickness; return true;
            case Polyline polyline: polyline.Thickness = thickness; return true;
            case Polyline2d polyline: polyline.Thickness = thickness; return true;
            case DBText text: text.Thickness = thickness; return true;
            case DBPoint point: point.Thickness = thickness; return true;
            case Solid solid: solid.Thickness = thickness; return true;
            case Trace trace: trace.Thickness = thickness; return true;
            default: return false;
        }
    }

    private static IEnumerable<string> StandardLineWeights() =>
        Enum.GetValues<LineWeight>()
            .Where(weight => (int)weight >= 0)
            .Select(weight => ((int)weight / 100.0).ToString("0.00", CultureInfo.InvariantCulture))
            .Distinct();
}

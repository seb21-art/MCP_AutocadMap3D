using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Unités du dessin, comme la commande UNITES : unité d'insertion (INSUNITS), format et précision des longueurs
/// (LUNITS, LUPREC) et des angles (AUNITS, AUPREC), direction et sens des angles (ANGBASE, ANGDIR), système
/// métrique ou impérial (MEASUREMENT). En option, les objets de l'espace objet sont mis à l'échelle pour garder leur
/// taille réelle, et les fenêtres de présentation corrigées pour garder leur cadrage. Une étape de U.
/// </summary>
internal static class UnitsTools
{
    private static readonly string[] LengthFormats = ["", "scientific", "decimal", "engineering", "architectural", "fractional"];
    private static readonly string[] AngleFormats = ["degrees", "dms", "grads", "radians", "surveyor"];

    private static readonly Dictionary<string, UnitsValue> UnitAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = UnitsValue.Undefined, ["unitless"] = UnitsValue.Undefined, ["sans"] = UnitsValue.Undefined,
        ["sans unité"] = UnitsValue.Undefined,
        ["mm"] = UnitsValue.Millimeters, ["millimètres"] = UnitsValue.Millimeters, ["millimetres"] = UnitsValue.Millimeters,
        ["cm"] = UnitsValue.Centimeters, ["centimètres"] = UnitsValue.Centimeters, ["centimetres"] = UnitsValue.Centimeters,
        ["dm"] = UnitsValue.Decimeters, ["décimètres"] = UnitsValue.Decimeters, ["decimetres"] = UnitsValue.Decimeters,
        ["m"] = UnitsValue.Meters, ["mètres"] = UnitsValue.Meters, ["metres"] = UnitsValue.Meters,
        ["km"] = UnitsValue.Kilometers, ["kilomètres"] = UnitsValue.Kilometers, ["kilometres"] = UnitsValue.Kilometers,
        ["in"] = UnitsValue.Inches, ["pouces"] = UnitsValue.Inches,
        ["ft"] = UnitsValue.Feet, ["pieds"] = UnitsValue.Feet,
        ["yd"] = UnitsValue.Yards, ["mi"] = UnitsValue.Miles,
        ["usfeet"] = UnitsValue.USSurveyFeet, ["us survey feet"] = UnitsValue.USSurveyFeet,
    };

    public static object GetUnits(Database database) => new
    {
        InsertionUnits = database.Insunits.ToString(),
        LengthFormat = Name(LengthFormats, database.Lunits),
        LengthPrecision = database.Luprec,
        AngleFormat = Name(AngleFormats, database.Aunits),
        AnglePrecision = database.Auprec,
        AngleBase = Format.Degrees(database.Angbase),
        Clockwise = database.Angdir,
        Measurement = database.Measurement == MeasurementValue.Metric ? "metric" : "imperial",
    };

    public static object SetDrawingUnits(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var transaction = context.RequireTransaction();
        var before = GetUnits(database);
        var warnings = new List<string>();

        var oldUnits = database.Insunits;
        var newUnits = reader.GetString("insertionUnits") is { } unitsText ? ParseUnits(unitsText) : oldUnits;
        var scaleContent = reader.GetBool("scaleContent", false);
        var system = CoordinateSystemTools.DrawingSystemUnits(database);

        // Validation complète avant toute écriture.
        var lengthFormat = reader.GetString("lengthFormat") is { } lf ? Index(LengthFormats, lf, "lengthFormat") : (int?)null;
        var angleFormat = reader.GetString("angleFormat") is { } af ? Index(AngleFormats, af, "angleFormat") : (int?)null;
        var lengthPrecision = reader.Has("lengthPrecision") ? reader.GetInt("lengthPrecision", 4, 0, 8) : (int?)null;
        var anglePrecision = reader.Has("anglePrecision") ? reader.GetInt("anglePrecision", 0, 0, 8) : (int?)null;
        var measurement = reader.GetString("measurement") is { } mt ? ParseMeasurement(mt) : (MeasurementValue?)null;

        double factor = 1;
        if (scaleContent && newUnits != oldUnits)
        {
            if (oldUnits == UnitsValue.Undefined || newUnits == UnitsValue.Undefined)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "scaleContent exige des unités définies avant et après le changement : la conversion depuis ou vers " +
                    "« sans unité » n'a pas de facteur. Utilisez scale_entities avec le facteur voulu.");
            if (system is not null && !reader.GetBool("force", false))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le dessin est géoréférencé ({system.Value.Code}, en {system.Value.Units}) : mettre ses objets à " +
                    "l'échelle fausserait leurs coordonnées. Pour changer de système, utilisez reproject_entities ; " +
                    "force=true met quand même à l'échelle, seulement si l'utilisateur le demande.");
            factor = UnitsConverter.GetConversionFactor(oldUnits, newUnits);
        }

        if (newUnits != oldUnits)
            database.Insunits = newUnits;
        if (lengthFormat is int lunits)
            database.Lunits = lunits;
        if (lengthPrecision is int luprec)
            database.Luprec = luprec;
        if (angleFormat is int aunits)
            database.Aunits = aunits;
        if (anglePrecision is int auprec)
            database.Auprec = auprec;
        if (reader.Has("angleBase"))
            database.Angbase = NormalizeAngle(reader.RequireDouble("angleBase") * Math.PI / 180);
        if (reader.Has("clockwise"))
            database.Angdir = reader.GetBool("clockwise", false);
        if (measurement is MeasurementValue value)
            database.Measurement = value;

        object? scaled = null;
        if (factor != 1)
        {
            var basePoint = reader.GetPoint("basePoint", Point3d.Origin);
            scaled = ScaleContent(database, transaction, factor, basePoint, warnings);
        }
        else if (scaleContent && newUnits == oldUnits)
            warnings.Add("Unité d'insertion inchangée : aucun objet mis à l'échelle.");

        if (system is not null && newUnits != oldUnits && !SameUnit(system.Value.MetersPerUnit, newUnits))
            warnings.Add($"Le système de coordonnées du dessin ({system.Value.Code}) est en {system.Value.Units} : " +
                         $"l'unité d'insertion {newUnits} ne lui correspond pas.");
        if (newUnits != oldUnits && !scaleContent && database.Extmin.X <= database.Extmax.X)
            warnings.Add("Les objets gardent leurs coordonnées : leur taille réelle change avec l'unité. " +
                         "scaleContent=true les convertit.");

        return new
        {
            Before = before,
            After = GetUnits(database),
            Scaled = scaled,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>
    /// Met à l'échelle tous les objets de l'espace objet autour du point de base, calques verrouillés compris
    /// (déverrouillés le temps de l'opération), puis corrige les fenêtres de présentation : même cadrage sur la feuille.
    /// </summary>
    private static object ScaleContent(Database database, Transaction transaction, double factor, Point3d basePoint, List<string> warnings)
    {
        var matrix = Matrix3d.Scaling(factor, basePoint);
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var unlocked = new List<LayerTableRecord>();
        foreach (var id in layers)
        {
            var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!layer.IsLocked)
                continue;
            layer.UpgradeOpen();
            layer.IsLocked = false;
            unlocked.Add(layer);
        }

        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        int count = 0, failed = 0;
        foreach (var id in modelSpace)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
            try
            {
                entity.TransformBy(matrix);
                count++;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                failed++;
            }
        }

        // Fenêtres : le point visé suit la mise à l'échelle, l'échelle de la fenêtre est divisée par le facteur.
        var viewports = 0;
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (var entry in layouts)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            if (layout.ModelType)
                continue;
            var paper = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var overall = true;
            foreach (var id in paper)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not Viewport viewport)
                    continue;
                // La première fenêtre d'une présentation est la fenêtre globale de l'espace papier.
                if (overall)
                {
                    overall = false;
                    continue;
                }

                viewport.UpgradeOpen();
                viewport.ViewTarget = basePoint + (viewport.ViewTarget - basePoint) * factor;
                viewport.ViewCenter = new Point2d(viewport.ViewCenter.X * factor, viewport.ViewCenter.Y * factor);
                viewport.CustomScale /= factor;
                viewports++;
            }
        }

        foreach (var layer in unlocked)
            layer.IsLocked = true;

        if (failed > 0)
            warnings.Add($"{failed} objet(s) n'ont pas pu être mis à l'échelle.");
        warnings.Add("La vue de l'espace objet n'a pas suivi : set_view (zoom étendu) recadre le dessin.");
        return new
        {
            Factor = factor,
            BasePoint = Format.Point(basePoint),
            Entities = count,
            Viewports = viewports > 0 ? viewports : (int?)null,
            UnlockedLayers = unlocked.Count > 0 ? unlocked.Count : (int?)null,
        };
    }

    private static UnitsValue ParseUnits(string text)
    {
        var key = text.Trim();
        if (UnitAliases.TryGetValue(key, out var alias))
            return alias;
        if (Enum.TryParse<UnitsValue>(key, true, out var value) && Enum.IsDefined(value))
            return value;
        if (Enum.TryParse(key + "s", true, out value) && Enum.IsDefined(value))
            return value;
        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Unité « {text} » inconnue. Exemples : millimeters (mm), centimeters (cm), meters (m), kilometers (km), " +
            "inches (in), feet (ft), yards, miles, unitless.");
    }

    private static MeasurementValue ParseMeasurement(string text) => text.Trim().ToLowerInvariant() switch
    {
        "metric" or "métrique" or "metrique" => MeasurementValue.Metric,
        "imperial" or "impérial" or "english" => MeasurementValue.English,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"measurement « {text} » : metric ou imperial attendu."),
    };

    private static int Index(string[] names, string text, string parameter)
    {
        var index = Array.FindIndex(names, name => name.Length > 0 && string.Equals(name, text.Trim(), StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new PipeException(PipeErrorCodes.InvalidParams,
            $"{parameter} « {text} » inconnu : {string.Join(", ", names.Where(name => name.Length > 0))}.");
    }

    private static string Name(string[] names, int index) =>
        index >= 0 && index < names.Length && names[index].Length > 0 ? names[index] : index.ToString(CultureInfo.InvariantCulture);

    private static double NormalizeAngle(double radians)
    {
        var angle = radians % (2 * Math.PI);
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    private static bool SameUnit(double metersPerUnit, UnitsValue units) =>
        units != UnitsValue.Undefined &&
        Math.Abs(UnitsConverter.GetConversionFactor(units, UnitsValue.Meters) - metersPerUnit) <= 1e-9 * metersPerUnit;
}

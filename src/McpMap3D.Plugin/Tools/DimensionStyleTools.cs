using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Format des cotations : styles de cote et remplacements sur des cotes précises. Styles (DimStyleTableRecord) et
/// cotes (Dimension) portent les mêmes variables DIM* ; elles sont lues et écrites ici par leur nom, une seule fois
/// pour les deux. Sur une cote, écrire une variable crée un remplacement du style.
/// </summary>
internal static class DimensionStyleTools
{
    public static object ListDimensionStyles(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var usage = CountDimensionsByStyle(context);

        var styles = new List<object>();
        var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);
        foreach (var id in table)
        {
            var style = (DimStyleTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (filter is not null && !filter.IsMatch(style.Name))
                continue;

            styles.Add(new
            {
                style.Name,
                Current = id == database.Dimstyle,
                Dimensions = usage.GetValueOrDefault(id),
                Format = Describe(context, style),
            });
        }

        return new { Count = styles.Count, Styles = styles };
    }

    /// <summary>Crée un style (copie d'un style existant) ou modifie un style, et le rend courant sur demande.</summary>
    public static object SetDimensionStyle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var name = reader.RequireString("name").Trim();
        var table = (DimStyleTable)transaction.GetObject(database.DimStyleTableId, OpenMode.ForRead);

        var created = !table.Has(name);
        DimStyleTableRecord style;
        if (created)
        {
            try
            {
                SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » n'est pas un nom de style valide.");
            }

            // Nouveau style : copie de basedOn, ou du style courant.
            var sourceId = reader.Has("basedOn") ? StyleId(context, reader.GetString("basedOn")!) : database.Dimstyle;
            var source = (DimStyleTableRecord)transaction.GetObject(sourceId, OpenMode.ForRead);
            style = (DimStyleTableRecord)source.Clone();
            style.Name = name;
            table.UpgradeOpen();
            table.Add(style);
            transaction.AddNewlyCreatedDBObject(style, true);
        }
        else
        {
            if (reader.Has("basedOn"))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le style « {name} » existe déjà : basedOn ne sert qu'à la création. Modifiez-le directement.");

            style = (DimStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
        }

        var applied = TryGetFormat(reader, out var format) ? Apply(context, format, style) : [];

        // Style rendu courant, ou style courant modifié : les variables DIM* du dessin, utilisées par les commandes
        // de cotation, doivent suivre le style.
        if (reader.GetBool("current", false))
            database.Dimstyle = style.ObjectId;
        if (database.Dimstyle == style.ObjectId && (applied.Count > 0 || reader.GetBool("current", false)))
            database.SetDimstyleData(style);

        // Les cotes du style ne se redessinent pas seules : on les recalcule.
        var updated = !created && applied.Count > 0 ? RecomputeDimensions(context, style.ObjectId) : 0;

        return new
        {
            style.Name,
            Created = created,
            Current = database.Dimstyle == style.ObjectId,
            Applied = applied,
            UpdatedDimensions = updated,
            Format = Describe(context, style),
        };
    }

    /// <summary>
    /// Sur des cotes : applique un style, efface les remplacements, et/ou crée des remplacements de format.
    /// Ordre : style, puis effacement, puis remplacements.
    /// </summary>
    public static object SetDimensionFormat(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var styleId = reader.Has("style") ? StyleId(context, reader.GetString("style")!) : ObjectId.Null;
        var clearOverrides = reader.GetBool("clearOverrides", false);
        var hasFormat = TryGetFormat(reader, out var format);
        if (styleId.IsNull && !clearOverrides && !hasFormat)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez au moins style, clearOverrides ou format.");

        var results = new List<object>();
        List<string> applied = [];
        foreach (var id in Handles.Resolve(context, reader))
        {
            var dimension = EditTools.OpenForWrite(id, transaction, context) as Dimension
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une cote.");

            if (!styleId.IsNull)
                dimension.DimensionStyle = styleId;

            if (clearOverrides)
                ClearOverrides(dimension);

            if (hasFormat)
                applied = Apply(context, format, dimension);

            dimension.RecomputeDimensionBlock(true);
            results.Add(DescribeDimension(context, dimension));
        }

        return new { Changed = results.Count, Applied = applied, Dimensions = results };
    }

    public static object GetDimensionFormat(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var results = Handles.Resolve(context, reader)
            .Select(id => transaction.GetObject(id, OpenMode.ForRead) as Dimension
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une cote."))
            .Select(dimension => DescribeDimension(context, dimension))
            .ToList();

        return new { Count = results.Count, Dimensions = results };
    }

    // ----- Lecture et écriture des variables de cote -----

    /// <summary>Paramètre « format » : objet de réglages ; absent, nul ou vide, il est ignoré.</summary>
    private static bool TryGetFormat(ArgReader reader, out ArgReader format)
    {
        format = new ArgReader(null);
        if (!reader.TryGet("format", out var element))
            return false;
        if (element.ValueKind != JsonValueKind.Object)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« format » doit être un objet de réglages.");
        if (!element.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.Null))
            return false;

        format = new ArgReader(element);
        return true;
    }

    /// <summary>Applique les réglages fournis à un style ou à une cote ; renvoie les noms des réglages appliqués.</summary>
    private static List<string> Apply(ToolContext context, ArgReader format, DBObject target)
    {
        var applied = new List<string>();
        void Set(string parameter, string property, object value)
        {
            target.GetType().GetProperty(property)!.SetValue(target, value);
            if (!applied.Contains(parameter))
                applied.Add(parameter);
        }

        if (format.Has("textHeight"))
            Set("textHeight", "Dimtxt", Positive(format, "textHeight"));
        if (format.Has("arrowSize"))
            Set("arrowSize", "Dimasz", NonNegative(format, "arrowSize"));
        if (format.Has("arrowhead"))
        {
            Set("arrowhead", "Dimsah", false);
            Set("arrowhead", "Dimblk", ArrowId(context, format.GetString("arrowhead")!));
        }

        if (format.Has("precision"))
            Set("precision", "Dimdec", format.GetInt("precision", 2, min: 0, max: 8));
        if (format.Has("unitFormat"))
            Set("unitFormat", "Dimlunit", FromName(UnitFormats, format.GetString("unitFormat")!, "unitFormat"));
        if (format.Has("decimalSeparator"))
        {
            var separator = format.GetString("decimalSeparator")!;
            if (separator.Length != 1)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le séparateur décimal doit être un seul caractère : « , » ou « . ».");
            Set("decimalSeparator", "Dimdsep", separator[0]);
        }

        if (format.Has("prefix") || format.Has("suffix"))
        {
            // DIMPOST : « préfixe<>suffixe », où <> représente la valeur mesurée.
            var (prefix, suffix) = SplitPost((string)Get(target, "Dimpost"));
            prefix = format.GetString("prefix") ?? prefix;
            suffix = format.GetString("suffix") ?? suffix;
            Set(format.Has("prefix") ? "prefix" : "suffix", "Dimpost", prefix.Length + suffix.Length == 0 ? "" : $"{prefix}<>{suffix}");
            if (format.Has("prefix") && format.Has("suffix"))
                applied.Add("suffix");
        }

        if (format.Has("scale"))
            Set("scale", "Dimscale", Positive(format, "scale"));
        if (format.Has("measurementScale"))
        {
            var factor = format.RequireDouble("measurementScale");
            Set("measurementScale", "Dimlfac", factor != 0 ? factor
                : throw new PipeException(PipeErrorCodes.InvalidParams, "Le facteur de mesure ne peut pas être nul."));
        }

        if (format.Has("roundOff"))
            Set("roundOff", "Dimrnd", NonNegative(format, "roundOff"));
        if (format.Has("textPosition"))
            Set("textPosition", "Dimtad", FromName(TextPositions, format.GetString("textPosition")!, "textPosition"));
        if (format.Has("textAlignment"))
        {
            var (inside, outside) = format.GetString("textAlignment")!.Trim().ToLowerInvariant() switch
            {
                "horizontal" => (true, true),
                "aligned" => (false, false),
                "iso" => (false, true),
                var other => throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Orientation « {other} » inconnue : horizontal, aligned ou iso."),
            };
            Set("textAlignment", "Dimtih", inside);
            Set("textAlignment", "Dimtoh", outside);
        }

        if (format.Has("textColor"))
            Set("textColor", "Dimclrt", EntityProperties.ParseColor(format.GetString("textColor")!));
        if (format.Has("lineColor"))
            Set("lineColor", "Dimclrd", EntityProperties.ParseColor(format.GetString("lineColor")!));
        if (format.Has("extensionLineColor"))
            Set("extensionLineColor", "Dimclre", EntityProperties.ParseColor(format.GetString("extensionLineColor")!));
        if (format.Has("lineWeight"))
        {
            var weight = EntityProperties.ReadLineWeight(format, "lineWeight");
            Set("lineWeight", "Dimlwd", weight);
            Set("lineWeight", "Dimlwe", weight);
        }

        if (format.Has("textStyle"))
            Set("textStyle", target is Dimension ? "TextStyleId" : "Dimtxsty", DraftingTools.TextStyle(context, format.GetString("textStyle")!));
        if (format.Has("extensionOffset"))
            Set("extensionOffset", "Dimexo", format.RequireDouble("extensionOffset"));
        if (format.Has("extensionBeyond"))
            Set("extensionBeyond", "Dimexe", format.RequireDouble("extensionBeyond"));
        if (format.Has("textGap"))
            Set("textGap", "Dimgap", format.RequireDouble("textGap"));
        if (format.Has("zeroSuppression"))
        {
            // Bits 1 et 2 : pieds et pouces, conservés ; 4 : zéros de tête ; 8 : zéros de queue.
            var bits = FromName(ZeroSuppressions, format.GetString("zeroSuppression")!, "zeroSuppression");
            Set("zeroSuppression", "Dimzin", ((int)Get(target, "Dimzin") & 3) | bits);
        }

        if (format.Has("angularPrecision"))
            Set("angularPrecision", "Dimadec", format.GetInt("angularPrecision", 0, min: 0, max: 8));
        if (format.Has("angularUnit"))
            Set("angularUnit", "Dimaunit", FromName(AngularUnits, format.GetString("angularUnit")!, "angularUnit"));

        if (applied.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun réglage reconnu dans « format ».");

        return applied;
    }

    /// <summary>Réglages effectifs d'un style ou d'une cote (remplacements compris), sous forme lisible.</summary>
    private static Dictionary<string, object?> Describe(ToolContext context, DBObject target)
    {
        var transaction = context.RequireTransaction();
        var (prefix, suffix) = SplitPost((string)Get(target, "Dimpost"));
        var textStyleId = (ObjectId)Get(target, target is Dimension ? "TextStyleId" : "Dimtxsty");
        var zeros = (int)Get(target, "Dimzin") & 12;
        var inside = (bool)Get(target, "Dimtih");
        var outside = (bool)Get(target, "Dimtoh");

        return new Dictionary<string, object?>
        {
            ["textHeight"] = Format.Number((double)Get(target, "Dimtxt")),
            ["arrowSize"] = Format.Number((double)Get(target, "Dimasz")),
            ["arrowhead"] = ArrowName((ObjectId)Get(target, "Dimblk"), transaction),
            ["precision"] = Get(target, "Dimdec"),
            ["unitFormat"] = ToName(UnitFormats, (int)Get(target, "Dimlunit")),
            ["decimalSeparator"] = ((char)Get(target, "Dimdsep")).ToString(),
            ["prefix"] = prefix,
            ["suffix"] = suffix,
            ["scale"] = Format.Number((double)Get(target, "Dimscale")),
            ["measurementScale"] = Format.Number((double)Get(target, "Dimlfac")),
            ["roundOff"] = Format.Number((double)Get(target, "Dimrnd")),
            ["textPosition"] = ToName(TextPositions, (int)Get(target, "Dimtad")),
            ["textAlignment"] = (inside, outside) switch { (true, true) => "horizontal", (false, false) => "aligned", (false, true) => "iso", _ => "mixed" },
            ["textColor"] = Format.Color((Autodesk.AutoCAD.Colors.Color)Get(target, "Dimclrt")),
            ["lineColor"] = Format.Color((Autodesk.AutoCAD.Colors.Color)Get(target, "Dimclrd")),
            ["extensionLineColor"] = Format.Color((Autodesk.AutoCAD.Colors.Color)Get(target, "Dimclre")),
            ["lineWeight"] = EntityProperties.FormatLineWeight((LineWeight)Get(target, "Dimlwd")),
            ["textStyle"] = textStyleId.IsNull ? null : ((TextStyleTableRecord)transaction.GetObject(textStyleId, OpenMode.ForRead)).Name,
            ["extensionOffset"] = Format.Number((double)Get(target, "Dimexo")),
            ["extensionBeyond"] = Format.Number((double)Get(target, "Dimexe")),
            ["textGap"] = Format.Number((double)Get(target, "Dimgap")),
            ["zeroSuppression"] = ToName(ZeroSuppressions, zeros),
            ["angularPrecision"] = Get(target, "Dimadec"),
            ["angularUnit"] = ToName(AngularUnits, (int)Get(target, "Dimaunit")),
        };
    }

    /// <summary>Cote : style, texte affiché, format effectif et réglages qui diffèrent du style (remplacements).</summary>
    private static object DescribeDimension(ToolContext context, Dimension dimension)
    {
        var transaction = context.RequireTransaction();
        var style = (DimStyleTableRecord)transaction.GetObject(dimension.DimensionStyle, OpenMode.ForRead);
        var effective = Describe(context, dimension);
        var fromStyle = Describe(context, style);
        var overrides = effective.Where(pair => !Equals(pair.Value?.ToString(), fromStyle[pair.Key]?.ToString()))
            .Select(pair => pair.Key).ToArray();

        return new
        {
            Handle = dimension.Handle.ToString(),
            Style = style.Name,
            Measurement = Measurement(dimension),
            DisplayedText = DisplayedText(dimension, transaction),
            Overrides = overrides,
            Format = effective,
        };
    }

    /// <summary>
    /// Mesure géométrique d'une cote : en degrés pour une cote angulaire, en unités du dessin sinon.
    /// Dimension.Measurement inclut le facteur DIMLFAC des cotes linéaires : il est retiré ici.
    /// </summary>
    internal static double Measurement(Dimension dimension)
    {
        if (dimension is Point3AngularDimension or LineAngularDimension2)
            return Format.Degrees(dimension.Measurement);

        var factor = dimension.Dimlfac;
        return Format.Number(factor > 0 ? dimension.Measurement / factor : dimension.Measurement);
    }

    /// <summary>
    /// Texte réellement affiché : celui du texte multiligne du bloc de la cote, régénéré par AutoCAD. Plus fiable que
    /// FormatMeasurement, qui exige une ouverture en écriture et applique deux fois le facteur DIMLFAC.
    /// </summary>
    private static string? DisplayedText(Dimension dimension, Transaction transaction)
    {
        if (dimension.DimBlockId.IsNull)
            return null;

        var block = (BlockTableRecord)transaction.GetObject(dimension.DimBlockId, OpenMode.ForRead);
        foreach (var id in block)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is MText text)
                return text.Text;
        }

        return null;
    }

    private static object Get(DBObject target, string property) => target.GetType().GetProperty(property)!.GetValue(target)!;

    /// <summary>Supprime les remplacements de style d'une cote (données étendues « ACAD » de DIMOVERRIDE).</summary>
    private static void ClearOverrides(Dimension dimension)
    {
        if (dimension.GetXDataForApplication("ACAD") is not null)
            dimension.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, "ACAD"));
    }

    // ----- Flèches -----

    private static readonly Dictionary<string, string> ArrowBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["closed_filled"] = "",
        ["closed_blank"] = "_CLOSEDBLANK",
        ["closed"] = "_CLOSED",
        ["dot"] = "_DOT",
        ["small_dot"] = "_DOTSMALL",
        ["dot_blank"] = "_DOTBLANK",
        ["architectural_tick"] = "_ARCHTICK",
        ["oblique"] = "_OBLIQUE",
        ["open"] = "_OPEN",
        ["open30"] = "_OPEN30",
        ["right_angle"] = "_OPEN90",
        ["origin"] = "_ORIGIN",
        ["box_filled"] = "_BOXFILLED",
        ["box_blank"] = "_BOXBLANK",
        ["datum_filled"] = "_DATUMFILLED",
        ["integral"] = "_INTEGRAL",
        ["none"] = "_NONE",
    };

    /// <summary>
    /// Bloc de flèche : nom convivial (oblique, dot…), nom AutoCAD (_OBLIQUE…) ou bloc du dessin. Les flèches
    /// prédéfinies absentes du dessin y sont chargées en passant brièvement par la variable DIMBLK.
    /// </summary>
    private static ObjectId ArrowId(ToolContext context, string name)
    {
        var key = name.Trim();
        var blockName = ArrowBlocks.TryGetValue(key, out var mapped) ? mapped : key;
        if (blockName.Length == 0)
            return ObjectId.Null; // Flèche pleine fermée, celle par défaut.

        // Recherche hors de la transaction de l'outil : la table des blocs ne doit pas y rester ouverte pendant
        // qu'AutoCAD crée le bloc de flèche.
        var database = context.Database;
        ObjectId Find()
        {
            using var lookup = database.TransactionManager.StartOpenCloseTransaction();
            var blocks = (BlockTable)lookup.GetObject(database.BlockTableId, OpenMode.ForRead);
            return blocks.Has(blockName) ? blocks[blockName] : ObjectId.Null;
        }

        var id = Find();
        if (id.IsNull && blockName.StartsWith('_'))
        {
            var previous = (string)AcApp.GetSystemVariable("DIMBLK");
            try
            {
                AcApp.SetSystemVariable("DIMBLK", blockName);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // Nom inconnu d'AutoCAD : signalé ci-dessous.
            }
            finally
            {
                AcApp.SetSystemVariable("DIMBLK", previous.Length == 0 ? "." : previous);
            }

            id = Find();
        }

        return !id.IsNull
            ? id
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Flèche « {name} » inconnue. Valeurs possibles : {string.Join(", ", ArrowBlocks.Keys)}, ou le nom d'un bloc du dessin.");
    }

    private static string ArrowName(ObjectId id, Transaction transaction)
    {
        if (id.IsNull)
            return "closed_filled";

        var block = ((BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name;
        return ArrowBlocks.FirstOrDefault(pair => string.Equals(pair.Value, block, StringComparison.OrdinalIgnoreCase)).Key ?? block;
    }

    // ----- Utilitaires -----

    private static readonly Dictionary<string, int> UnitFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scientific"] = 1, ["decimal"] = 2, ["engineering"] = 3, ["architectural"] = 4, ["fractional"] = 5, ["windows"] = 6,
    };

    private static readonly Dictionary<string, int> TextPositions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["centered"] = 0, ["above"] = 1, ["outside"] = 2, ["jis"] = 3, ["below"] = 4,
    };

    private static readonly Dictionary<string, int> ZeroSuppressions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = 0, ["leading"] = 4, ["trailing"] = 8, ["both"] = 12,
    };

    private static readonly Dictionary<string, int> AngularUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["degrees"] = 0, ["dms"] = 1, ["grads"] = 2, ["radians"] = 3, ["surveyor"] = 4,
    };

    private static int FromName(Dictionary<string, int> values, string name, string parameter) =>
        values.TryGetValue(name.Trim(), out var value)
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Valeur « {name} » inconnue pour {parameter} : {string.Join(", ", values.Keys)}.");

    private static string ToName(Dictionary<string, int> values, int value) =>
        values.FirstOrDefault(pair => pair.Value == value).Key ?? value.ToString(CultureInfo.InvariantCulture);

    private static (string Prefix, string Suffix) SplitPost(string post)
    {
        var marker = post.IndexOf("<>", StringComparison.Ordinal);
        return marker < 0 ? ("", post) : (post[..marker], post[(marker + 2)..]);
    }

    private static ObjectId StyleId(ToolContext context, string name)
    {
        var table = (DimStyleTable)context.RequireTransaction().GetObject(context.Database.DimStyleTableId, OpenMode.ForRead);
        return table.Has(name)
            ? table[name]
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le style de cote « {name} » n'existe pas (voir list_dimension_styles).");
    }

    /// <summary>Nombre de cotes par style, dans tous les espaces et blocs du dessin (références externes exclues).</summary>
    private static Dictionary<ObjectId, int> CountDimensionsByStyle(ToolContext context)
    {
        var counts = new Dictionary<ObjectId, int>();
        foreach (var dimension in AllDimensions(context, OpenMode.ForRead))
            counts[dimension.DimensionStyle] = counts.GetValueOrDefault(dimension.DimensionStyle) + 1;

        return counts;
    }

    private static int RecomputeDimensions(ToolContext context, ObjectId styleId)
    {
        var count = 0;
        foreach (var dimension in AllDimensions(context, OpenMode.ForRead).Where(d => d.DimensionStyle == styleId).ToList())
        {
            // Mise à jour d'affichage : autorisée aussi sur un calque verrouillé.
            var writable = (Dimension)context.RequireTransaction().GetObject(dimension.ObjectId, OpenMode.ForWrite, false, true);
            writable.RecomputeDimensionBlock(true);
            count++;
        }

        return count;
    }

    private static IEnumerable<Dimension> AllDimensions(ToolContext context, OpenMode mode)
    {
        var transaction = context.RequireTransaction();
        var dimensionClass = RXObject.GetClass(typeof(Dimension));
        var blocks = (BlockTable)transaction.GetObject(context.Database.BlockTableId, OpenMode.ForRead);
        foreach (var blockId in blocks)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference || block.IsDependent)
                continue;

            foreach (var id in block)
            {
                if (id.ObjectClass.IsDerivedFrom(dimensionClass))
                    yield return (Dimension)transaction.GetObject(id, mode);
            }
        }
    }

    private static double Positive(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value > 0 ? value : throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » doit être strictement positif.");
    }

    private static double NonNegative(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value >= 0 ? value : throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » ne peut pas être négatif.");
    }
}

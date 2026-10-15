using System.Collections;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Réglages du dessin : échelles des types de ligne (LTSCALE, CELTSCALE, PSLTSCALE, MSLTSCALE), échelles
/// d'annotation (CANNOSCALE), style des points (PDMODE, PDSIZE) et propriétés du dessin (PROPDESSIN).
/// Chaque modification est une étape de U.
/// </summary>
internal static class SettingsTools
{
    private const string AnnotationScales = "ACDB_ANNOTATIONSCALES";
    private static readonly string[] PointShapes = ["dot", "none", "plus", "cross", "tick"];

    /// <summary>Résumé des réglages pour get_drawing_info.</summary>
    public static object Describe(Database database) => new
    {
        LinetypeScale = DescribeLinetypeScale(database),
        AnnotationScale = database.Cannoscale?.Name,
        PointStyle = DescribePointStyle(database),
    };

    public static object SetLinetypeScale(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        if (!new[] { "globalScale", "newObjectScale", "paperSpaceScaling", "annotationScaling" }.Any(reader.Has))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Rien à changer : donnez globalScale, newObjectScale, paperSpaceScaling ou annotationScaling.");

        var before = DescribeLinetypeScale(database);
        if (reader.Has("globalScale"))
            database.Ltscale = Positive(reader, "globalScale");
        if (reader.Has("newObjectScale"))
            database.Celtscale = Positive(reader, "newObjectScale");
        if (reader.Has("paperSpaceScaling"))
            database.Psltscale = reader.GetBool("paperSpaceScaling", true);
        if (reader.Has("annotationScaling"))
            database.MsLtScale = reader.GetBool("annotationScaling", true);

        context.AfterCommit(() => context.Editor.Regen());
        return new { Before = before, After = DescribeLinetypeScale(database) };
    }

    public static object ListAnnotationScales(ToolContext context, JsonElement? args)
    {
        var database = context.Database;
        var scales = Scales(database).Select(scale => DescribeScale(scale, database)).ToList();
        return new { Count = scales.Count, Current = database.Cannoscale?.Name, Scales = scales };
    }

    /// <summary>
    /// Rend courante une échelle d'annotation, désignée par son nom ou par son ratio ; une échelle absente est créée
    /// (1 unité papier, en mm ou en pouces, pour N unités réelles converties dans l'unité du dessin).
    /// </summary>
    public static object SetAnnotationScale(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var name = reader.GetString("name")?.Trim();
        reader.TryGet("scale", out var scaleElement);
        if (string.IsNullOrEmpty(name) && scaleElement.ValueKind == JsonValueKind.Undefined)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Donnez name (échelle existante) ou scale (ratio, par exemple \"1:500\").");

        var collection = database.ObjectContextManager.GetContextCollection(AnnotationScales);
        var scales = Scales(database).ToList();
        var previous = database.Cannoscale?.Name;
        AnnotationScale? match = null;
        if (!string.IsNullOrEmpty(name))
            match = scales.FirstOrDefault(scale => string.Equals(scale.Name, name, StringComparison.OrdinalIgnoreCase));

        var created = false;
        if (match is null)
        {
            if (scaleElement.ValueKind == JsonValueKind.Undefined)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Échelle d'annotation « {name} » introuvable : list_annotation_scales donne la liste, scale en crée une.");
            var ratio = LayoutTools.ParseScale(database, scaleElement);
            match = string.IsNullOrEmpty(name) ? scales.FirstOrDefault(scale => Math.Abs(Ratio(scale) - ratio) <= 1e-9 * ratio) : null;
            if (match is null)
            {
                var newName = string.IsNullOrEmpty(name) ? LayoutTools.FormatScale(database, ratio) : name;
                if (collection.HasContext(newName))
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Une échelle « {newName} » existe déjà avec un autre ratio : choisissez un autre nom.");
                collection.AddContext(new AnnotationScale { Name = newName, PaperUnits = 1, DrawingUnits = 1 / ratio });
                match = (AnnotationScale)collection.GetContext(newName);
                created = true;
            }
        }

        if (reader.GetBool("current", true))
            database.Cannoscale = match;

        return new
        {
            Scale = DescribeScale(match, database),
            Created = created ? true : (bool?)null,
            Previous = previous,
            Current = database.Cannoscale?.Name,
        };
    }

    public static object SetPointStyle(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var before = DescribePointStyle(database);

        int mode;
        if (reader.Has("mode"))
        {
            mode = reader.GetInt("mode", 0, 0, 100);
            if (mode % 32 > 4)
                throw new PipeException(PipeErrorCodes.InvalidParams, "mode : 0 à 4, plus 32 (cercle) et/ou 64 (carré).");
        }
        else
        {
            var shape = reader.GetString("shape") is { } text
                ? Array.FindIndex(PointShapes, item => string.Equals(item, text.Trim(), StringComparison.OrdinalIgnoreCase))
                : database.Pdmode % 32;
            if (shape < 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"shape : {string.Join(", ", PointShapes)}.");
            var circle = reader.GetBool("circle", (database.Pdmode & 32) != 0);
            var square = reader.GetBool("square", (database.Pdmode & 64) != 0);
            mode = shape + (circle ? 32 : 0) + (square ? 64 : 0);
        }

        if (reader.Has("size") && reader.Has("percentOfScreen"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "size et percentOfScreen s'excluent.");
        database.Pdmode = mode;
        if (reader.Has("size"))
            database.Pdsize = Positive(reader, "size");
        else if (reader.Has("percentOfScreen"))
            database.Pdsize = -Positive(reader, "percentOfScreen");

        context.AfterCommit(() => context.Editor.Regen());
        return new { Before = before, After = DescribePointStyle(database) };
    }

    /// <summary>Propriétés du dessin (PROPDESSIN) : champs de résumé et propriétés personnalisées.</summary>
    public static object? DescribeProperties(Database database)
    {
        var info = database.SummaryInfo;
        var custom = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = info.CustomProperties;
        while (enumerator.MoveNext())
            custom[(string)enumerator.Key] = enumerator.Value?.ToString() ?? "";

        var fields = new Dictionary<string, object>
        {
            ["title"] = info.Title, ["subject"] = info.Subject, ["author"] = info.Author, ["keywords"] = info.Keywords,
            ["comments"] = info.Comments, ["revisionNumber"] = info.RevisionNumber, ["hyperlinkBase"] = info.HyperlinkBase,
            ["lastSavedBy"] = info.LastSavedBy,
        };
        var result = fields.Where(field => field.Value is string text && text.Length > 0).ToDictionary(field => field.Key, field => field.Value);
        if (custom.Count > 0)
            result["custom"] = custom;
        return result.Count > 0 ? result : null;
    }

    public static object SetDrawingProperties(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var builder = new DatabaseSummaryInfoBuilder(database.SummaryInfo);
        var changed = 0;
        void Set(string name, Action<string> apply)
        {
            if (reader.GetString(name) is { } value)
            {
                apply(value);
                changed++;
            }
        }

        Set("title", value => builder.Title = value);
        Set("subject", value => builder.Subject = value);
        Set("author", value => builder.Author = value);
        Set("keywords", value => builder.Keywords = value);
        Set("comments", value => builder.Comments = value);
        Set("revisionNumber", value => builder.RevisionNumber = value);
        Set("hyperlinkBase", value => builder.HyperlinkBase = value);

        if (reader.TryGet("customProperties", out var custom) && custom.ValueKind != JsonValueKind.Null)
        {
            if (custom.ValueKind != JsonValueKind.Object)
                throw new PipeException(PipeErrorCodes.InvalidParams, "customProperties attend un objet { \"nom\": \"valeur\" } (null retire la propriété).");
            var table = builder.CustomPropertyTable;
            foreach (var property in custom.EnumerateObject())
            {
                var key = property.Name.Trim();
                if (key.Length == 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams, "Une propriété personnalisée n'a pas de nom.");
                var existing = table.Keys.Cast<object>().Select(item => item.ToString()!)
                    .FirstOrDefault(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    table.Remove(existing);
                if (property.Value.ValueKind != JsonValueKind.Null)
                    table.Add(existing ?? key, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText());
                changed++;
            }
        }

        if (changed == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Rien à changer : title, subject, author, keywords, comments, revisionNumber, hyperlinkBase ou customProperties.");
        database.SummaryInfo = builder.ToDatabaseSummaryInfo();
        return new { Changed = changed, Properties = DescribeProperties(database) };
    }

    private static object DescribeLinetypeScale(Database database) => new
    {
        GlobalScale = Format.Number(database.Ltscale),
        NewObjectScale = Format.Number(database.Celtscale),
        PaperSpaceScaling = database.Psltscale,
        AnnotationScaling = database.MsLtScale,
    };

    private static object DescribePointStyle(Database database)
    {
        var mode = database.Pdmode;
        var shape = mode % 32 is var index && index < PointShapes.Length ? PointShapes[index] : index.ToString();
        var size = database.Pdsize;
        return new
        {
            Mode = mode,
            Shape = shape,
            Circle = (mode & 32) != 0,
            Square = (mode & 64) != 0,
            Size = size > 0 ? Format.Number(size) : (double?)null,
            PercentOfScreen = size < 0 ? Format.Number(-size) : (double?)null,
            DefaultSize = size == 0 ? "5 % de la hauteur de l'écran" : null,
        };
    }

    private static IEnumerable<AnnotationScale> Scales(Database database)
    {
        var collection = database.ObjectContextManager.GetContextCollection(AnnotationScales);
        foreach (ObjectContext context in collection)
            if (context is AnnotationScale scale)
                yield return scale;
    }

    private static double Ratio(AnnotationScale scale) => scale.DrawingUnits == 0 ? 0 : scale.PaperUnits / scale.DrawingUnits;

    private static object DescribeScale(AnnotationScale scale, Database database) => new
    {
        scale.Name,
        PaperUnits = Format.Number(scale.PaperUnits),
        DrawingUnits = Format.Number(scale.DrawingUnits),
        Ratio = LayoutTools.FormatScale(database, Ratio(scale)),
        Current = string.Equals(scale.Name, database.Cannoscale?.Name, StringComparison.Ordinal) ? true : (bool?)null,
    };

    private static double Positive(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value > 0 ? value : throw new PipeException(PipeErrorCodes.InvalidParams, $"{name} doit être strictement positif.");
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Import de points topographiques depuis un fichier texte (CSV, TXT, XYZ) : points AutoCAD ou blocs à attributs,
/// calques par code, données d'objet en option. Une seule étape d'annulation.
/// </summary>
internal static class SurveyTools
{
    private const int MaxPoints = 100_000;
    private const int MaxReportedLines = 10;

    private sealed record SurveyPoint(int Line, string? Number, double X, double Y, double Z, string? Code);

    public static object ImportPoints(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var path = reader.RequireString("filePath");
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        var columns = ParseColumns(reader.GetString("columns") ?? "PXYZD");
        var lines = ReadLines(path);
        var delimiter = reader.GetString("delimiter") is { Length: > 0 } given ? DelimiterFrom(given) : DetectDelimiter(lines);
        var (points, skipped) = Parse(lines, columns, delimiter);
        if (points.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucun point lu dans {Path.GetFileName(path)} avec les colonnes « {columns} »" +
                (skipped.Count > 0 ? $" : {string.Join(" ; ", skipped.Take(3))}" : ".") +
                " Vérifiez columns (P numéro, X, Y, Z, D code, - colonne ignorée) et delimiter.");
        if (points.Count > MaxPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop de points ({points.Count}) : {MaxPoints} au maximum par import.");

        // Coordonnées d'un autre système (GPS en longitude/latitude, autre projection) : converties vers le dessin.
        string? convertedFrom = null;
        if (reader.GetString("from") is { Length: > 0 } from && !from.Equals("drawing", StringComparison.OrdinalIgnoreCase))
        {
            using var transform = CoordinateSystemTools.CreateTransform(context.Database, from, "drawing");
            points = points.Select(point =>
            {
                var (x, y) = transform.Apply(point.X, point.Y);
                return point with { X = x, Y = y };
            }).ToList();
            convertedFrom = transform.FromCode;
        }

        var transaction = context.RequireTransaction();
        var database = context.Database;
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var baseLayer = reader.GetString("layer");
        var layerFromCode = reader.GetBool("layerFromCode", false);
        if (baseLayer is not null && !layerFromCode && !layers.Has(baseLayer))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {baseLayer} » n'existe pas. Créez-le d'abord avec create_layer.");

        // Bloc à attributs : étiquette -> colonne (P, X, Y, Z ou D).
        ObjectId? blockId = null;
        var mapping = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        if (reader.GetString("block") is { } blockName)
        {
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            blockId = blocks.Has(blockName) ? blocks[blockName]
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le bloc « {blockName} » n'est pas défini dans le dessin (voir list_blocks).");
            foreach (var (tag, column) in BlockTools.ReadAttributeValues(reader, "attributes"))
            {
                var letter = char.ToUpperInvariant(column.Trim().FirstOrDefault());
                mapping[tag] = "PXYZD".Contains(letter) && column.Trim().Length == 1 ? letter
                    : throw new PipeException(PipeErrorCodes.InvalidParams, $"Attribut « {tag} » : attendu une colonne P, X, Y, Z ou D, pas « {column} ».");
            }
        }
        else if (reader.Has("attributes"))
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, "« attributes » s'utilise avec « block ».");
        }

        var scale = BlockTools.ReadScale(reader);
        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        var createdLayers = new List<string>();
        var ids = new List<ObjectId>(points.Count);
        var extents = new Extents3d(new Point3d(points[0].X, points[0].Y, points[0].Z), new Point3d(points[0].X, points[0].Y, points[0].Z));
        foreach (var point in points)
        {
            var position = new Point3d(point.X, point.Y, point.Z);
            Entity entity = blockId is { } block
                ? BlockTools.Insert(context, block, position, scale, 0, Values(point, mapping), reader, space)
                : new DBPoint(position);
            entity.Layer = LayerFor(point, baseLayer, layerFromCode, layers, transaction, createdLayers) ?? entity.Layer;
            if (blockId is null)
            {
                space.AppendEntity(entity);
                transaction.AddNewlyCreatedDBObject(entity, true);
            }

            ids.Add(entity.ObjectId);
            extents.AddPoint(position);
        }

        // Données d'objet : rattachées après validation de la géométrie, sinon AutoCAD ne dessine pas les objets.
        var odTable = reader.GetString("odTable");
        if (odTable is not null)
        {
            var records = ids.Select((id, index) => (id, points[index].Number, points[index].Code, points[index].Z)).ToList();
            context.AfterCommit(() => MapTools.AttachPointData(database, odTable, records));
        }

        return new
        {
            Count = points.Count,
            Skipped = skipped.Count,
            SkippedLines = skipped.Count > 0 ? skipped.Take(MaxReportedLines).ToArray() : null,
            Delimiter = delimiter == ' ' ? "espaces" : delimiter.ToString(),
            ConvertedFrom = convertedFrom,
            Extents = Format.Extents(extents),
            CreatedLayers = createdLayers.Count > 0 ? createdLayers : null,
            OdTable = odTable,
            Handles = ids.Take(100).Select(id => id.Handle.ToString()).ToArray(),
            HandlesTruncated = ids.Count > 100 ? true : (bool?)null,
        };
    }

    /// <summary>Colonnes : P numéro, X (ou E), Y (ou N), Z, D code ou description, - colonne ignorée.</summary>
    private static string ParseColumns(string text)
    {
        var columns = new string(text.Trim().ToUpperInvariant().Replace('E', 'X').Replace('N', 'Y').Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (columns.Any(c => !"PXYZD-".Contains(c)))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"columns « {text} » : lettres attendues P (numéro), X ou E, Y ou N, Z, D (code), - (colonne ignorée).");
        if (!columns.Contains('X') || !columns.Contains('Y') || columns.GroupBy(c => c).Any(g => g.Key != '-' && g.Count() > 1))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"columns « {text} » : X et Y obligatoires, chaque lettre une seule fois.");
        return columns;
    }

    /// <summary>Lignes du fichier : UTF-8, ou Windows-1252 (Latin-1) si le fichier n'est pas de l'UTF-8 valide.</summary>
    private static string[] ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return text.TrimStart('﻿').Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
    }

    private static char DelimiterFrom(string text) => text.ToLowerInvariant() switch
    {
        "tab" or "\t" => '\t',
        "space" or "espace" or " " => ' ',
        _ when text.Length == 1 => text[0],
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, "delimiter : un caractère (; , |), tab ou space."),
    };

    /// <summary>Séparateur le plus fréquent sur les premières lignes : ; puis tabulation, | et virgule ; sinon des espaces.</summary>
    private static char DetectDelimiter(string[] lines)
    {
        var sample = lines.Where(line => line.Trim().Length > 0 && !IsComment(line)).Take(10).ToList();
        foreach (var candidate in new[] { ';', '\t', '|', ',' })
        {
            if (sample.Count > 0 && sample.All(line => line.Contains(candidate)))
                return candidate;
        }

        return ' ';
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("//");

    private static (List<SurveyPoint> Points, List<string> Skipped) Parse(string[] lines, string columns, char delimiter)
    {
        var points = new List<SurveyPoint>();
        var skipped = new List<string>();
        var decimalComma = delimiter != ',';
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length == 0 || IsComment(line))
                continue;

            var fields = delimiter == ' '
                ? line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : line.Split(delimiter).Select(field => field.Trim().Trim('"')).ToArray();
            string? Field(char letter) => columns.IndexOf(letter) is var at and >= 0 && at < fields.Length ? fields[at] : null;
            double? Number(char letter) =>
                Field(letter) is { Length: > 0 } text &&
                double.TryParse(decimalComma ? text.Replace(',', '.') : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value : null;

            var (x, y) = (Number('X'), Number('Y'));
            if (x is null || y is null)
            {
                // Une première ligne non numérique est un en-tête : elle n'est pas comptée comme une erreur.
                if (points.Count > 0 || skipped.Count > 0 || index > 0 && lines.Take(index).Any(previous => previous.Trim().Length > 0 && !IsComment(previous)))
                    skipped.Add($"ligne {index + 1} : X ou Y illisible");
                continue;
            }

            var z = Number('Z');
            if (columns.Contains('Z') && z is null && Field('Z') is { Length: > 0 })
            {
                skipped.Add($"ligne {index + 1} : Z illisible");
                continue;
            }

            points.Add(new SurveyPoint(index + 1, Field('P') is { Length: > 0 } number ? number : null, x.Value, y.Value, z ?? 0,
                Field('D') is { Length: > 0 } code ? code : null));
        }

        return (points, skipped);
    }

    private static Dictionary<string, string> Values(SurveyPoint point, Dictionary<string, char> mapping) =>
        mapping.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            'P' => point.Number ?? "",
            'X' => point.X.ToString("0.###", CultureInfo.InvariantCulture),
            'Y' => point.Y.ToString("0.###", CultureInfo.InvariantCulture),
            'Z' => point.Z.ToString("0.00#", CultureInfo.InvariantCulture),
            _ => point.Code ?? "",
        }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Calque d'un point : avec layerFromCode, préfixe layer + code (créé s'il manque) ; sinon, ou sans code, le calque
    /// layer s'il existe, ou le calque courant (null).
    /// </summary>
    private static string? LayerFor(SurveyPoint point, string? baseLayer, bool fromCode, LayerTable layers, Transaction transaction, List<string> created)
    {
        if (!fromCode || string.IsNullOrWhiteSpace(point.Code))
            return baseLayer is not null && layers.Has(baseLayer) ? baseLayer : null;

        var name = SymbolUtilityServices.RepairSymbolName((baseLayer ?? "") + point.Code.Trim(), false);
        if (!layers.Has(name))
        {
            if (!layers.IsWriteEnabled)
                layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            created.Add(name);
        }

        return name;
    }
}

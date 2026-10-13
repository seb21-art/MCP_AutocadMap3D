using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Publication d'objets du dessin pour le web et les SIG : GeoJSON, KML ou KMZ (Google Earth), en longitude/latitude
/// WGS84, avec le calque, le texte, les attributs de bloc et les données d'objet de chaque objet. Le dessin n'est pas
/// modifié.
/// </summary>
internal static class PublishTools
{
    private const int MaxFeatures = 50_000;

    private sealed record Feature(string Kind, List<double[]> Coordinates, string Name, string Layer, Dictionary<string, object?> Properties);

    public static object ExportGeodata(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("filePath"));
        var format = (reader.GetString("format") ?? Path.GetExtension(path).TrimStart('.')).ToLowerInvariant() switch
        {
            "geojson" or "json" => "geojson",
            "kml" => "kml",
            "kmz" => "kmz",
            var other => throw new PipeException(PipeErrorCodes.InvalidParams, $"Format « {other} » : geojson, kml ou kmz (déduit de l'extension)."),
        };
        if (File.Exists(path) && !reader.GetBool("overwrite", false))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{path} existe déjà : overwrite=true pour le remplacer.");
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le dossier de {path} n'existe pas.");

        var transaction = context.RequireTransaction();
        var filter = EntityFilter.Create(context, reader, transaction);
        var includeObjectData = reader.GetBool("includeObjectData", true) && MapInfo.IsAvailable;
        var odReader = includeObjectData ? MapTools.CreateOdRecordReader(context.Database) : null;
        var from = reader.GetString("from") ?? "drawing";
        using var transform = CoordinateSystemTools.CreateTransform(context.Database, from, "lonlat");

        var features = new List<Feature>();
        var skipped = new Dictionary<string, int>();
        foreach (var entity in QueryTools.Source(context, reader, transaction))
        {
            if (!filter.IsMatch(entity))
                continue;
            if (Shape(entity, transaction) is not { } shape)
            {
                var type = Format.DxfName(entity);
                skipped[type] = skipped.GetValueOrDefault(type) + 1;
                continue;
            }

            if (features.Count >= MaxFeatures)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Plus de {MaxFeatures} objets : filtrez par calque ou par type.");

            var coordinates = shape.Points.Select(point =>
            {
                var (lon, lat) = transform.Apply(point.X, point.Y);
                return point.Z == 0 ? new[] { Math.Round(lon, 8), Math.Round(lat, 8) } : [Math.Round(lon, 8), Math.Round(lat, 8), Format.Number(point.Z)];
            }).ToList();
            var properties = new Dictionary<string, object?>
            {
                ["handle"] = entity.Handle.ToString(),
                ["layer"] = entity.Layer,
                ["type"] = Format.DxfName(entity),
            };
            if (shape.Text is not null)
                properties["text"] = shape.Text;
            if (entity is BlockReference block)
            {
                properties["block"] = ((BlockTableRecord)transaction.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead)).Name;
                foreach (var (tag, value) in BlockTools.AttributeValues(block, transaction))
                    properties[$"attr.{tag}"] = value;
            }

            if (odReader is not null)
            {
                foreach (var (table, values) in odReader(entity.ObjectId))
                {
                    foreach (var (field, value) in values)
                        properties[$"{table}.{field}"] = value;
                }
            }

            var name = shape.Text ?? (properties.TryGetValue("block", out var blockName) ? blockName?.ToString() : null) ?? $"{entity.Layer} {entity.Handle}";
            features.Add(new Feature(shape.Kind, coordinates, name, entity.Layer, properties));
        }

        if (features.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun objet exportable ne correspond aux filtres (points, blocs, textes, lignes, polylignes, arcs, cercles).");

        var colors = LayerColors(context, transaction, features.Select(f => f.Layer).Distinct());
        if (File.Exists(path))
            File.Delete(path);
        var title = reader.GetString("title") ?? Path.GetFileNameWithoutExtension(path);
        switch (format)
        {
            case "geojson":
                File.WriteAllText(path, GeoJson(features, title), new UTF8Encoding(false));
                break;
            case "kml":
                File.WriteAllText(path, Kml(features, title, colors), new UTF8Encoding(false));
                break;
            default:
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using var entry = new StreamWriter(archive.CreateEntry("doc.kml").Open(), new UTF8Encoding(false));
                    entry.Write(Kml(features, title, colors));
                }

                break;
        }

        var all = features.SelectMany(f => f.Coordinates).ToList();
        return new
        {
            File = path,
            Format = format,
            Features = features.Count,
            ByGeometry = features.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()),
            Skipped = skipped.Count > 0 ? skipped : null,
            From = transform.FromCode,
            BoundsLonLat = new[] { all.Min(c => c[0]), all.Min(c => c[1]), all.Max(c => c[0]), all.Max(c => c[1]) },
            Bytes = new FileInfo(path).Length,
        };
    }

    /// <summary>Géométrie publiable : point, ligne ou polygone (courbe fermée), arcs échantillonnés.</summary>
    private static (string Kind, List<Point3d> Points, string? Text)? Shape(Entity entity, Transaction transaction)
    {
        static List<Point3d> Sample(Curve curve)
        {
            var length = curve.GetDistanceAtParameter(curve.EndParam);
            const int count = 64;
            return Enumerable.Range(0, count + 1).Select(k => curve.GetPointAtDist(length * k / count)).ToList();
        }

        static List<Point3d> Polyline(Polyline polyline)
        {
            var points = new List<Point3d>();
            var segments = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                points.Add(polyline.GetPoint3dAt(i));
                if (i < segments && polyline.GetBulgeAt(i) != 0)
                    for (var k = 1; k < 16; k++)
                        points.Add(polyline.GetPointAtParameter(i + k / 16.0));
            }

            return points;
        }

        (string, List<Point3d>, string?) Linear(List<Point3d> points, bool closed) =>
            closed && points.Count >= 3 ? ("Polygon", points, null) : ("LineString", points, null);

        try
        {
            return entity switch
            {
                DBPoint point => ("Point", [point.Position], null),
                BlockReference block => ("Point", [block.Position], null),
                DBText text => ("Point", [text.Position], text.TextString),
                MText mtext => ("Point", [mtext.Location], mtext.Text),
                Line line => ("LineString", [line.StartPoint, line.EndPoint], null),
                Polyline polyline => Linear(Polyline(polyline), polyline.Closed),
                Polyline2d polyline2d => Linear(polyline2d.Cast<ObjectId>().Select(id => ((Vertex2d)transaction.GetObject(id, OpenMode.ForRead)).Position).ToList(), polyline2d.Closed),
                Polyline3d polyline3d => Linear(polyline3d.Cast<ObjectId>().Select(id => ((PolylineVertex3d)transaction.GetObject(id, OpenMode.ForRead)).Position).ToList(), polyline3d.Closed),
                Circle or Ellipse => Linear(Sample((Curve)entity), ((Curve)entity).Closed),
                Arc or Spline => Linear(Sample((Curve)entity), ((Curve)entity).Closed),
                _ => null,
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    private static string GeoJson(List<Feature> features, string title)
    {
        var collection = new JsonObject
        {
            ["type"] = "FeatureCollection",
            ["name"] = title,
            ["features"] = new JsonArray(features.Select(feature => (JsonNode)new JsonObject
            {
                ["type"] = "Feature",
                ["geometry"] = new JsonObject
                {
                    ["type"] = feature.Kind,
                    ["coordinates"] = feature.Kind switch
                    {
                        "Point" => Coordinates(feature.Coordinates[0]),
                        "LineString" => new JsonArray(feature.Coordinates.Select(c => (JsonNode)Coordinates(c)).ToArray()),
                        _ => new JsonArray(new JsonArray(Closed(feature.Coordinates).Select(c => (JsonNode)Coordinates(c)).ToArray())),
                    },
                },
                ["properties"] = JsonSerializer.SerializeToNode(feature.Properties),
            }).ToArray()),
        };
        return collection.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        static JsonArray Coordinates(double[] c) => new(c.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
    }

    private static IEnumerable<double[]> Closed(List<double[]> ring) =>
        ring[0].SequenceEqual(ring[^1]) ? ring : ring.Append(ring[0]);

    private static string Kml(List<Feature> features, string title, Dictionary<string, string> colors)
    {
        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        string Coordinates(IEnumerable<double[]> points) => string.Join(" ", points.Select(c => string.Join(",", c.Select(Number))));
        var kml = new StringBuilder();
        kml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?><kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document>")
            .Append("<name>").Append(SecurityElement.Escape(title)).Append("</name>");
        foreach (var (layer, rgb) in colors)
        {
            // Couleurs KML : aabbggrr.
            var bgr = rgb[4..6] + rgb[2..4] + rgb[0..2];
            kml.Append("<Style id=\"").Append(StyleId(layer)).Append("\"><LineStyle><color>ff").Append(bgr).Append("</color><width>2</width></LineStyle>")
                .Append("<PolyStyle><color>66").Append(bgr).Append("</color></PolyStyle><IconStyle><color>ff").Append(bgr).Append("</color></IconStyle></Style>");
        }

        foreach (var group in features.GroupBy(f => f.Layer))
        {
            kml.Append("<Folder><name>").Append(SecurityElement.Escape(group.Key)).Append("</name>");
            foreach (var feature in group)
            {
                kml.Append("<Placemark><name>").Append(SecurityElement.Escape(feature.Name)).Append("</name><styleUrl>#").Append(StyleId(feature.Layer)).Append("</styleUrl>");
                kml.Append("<ExtendedData>");
                foreach (var (key, value) in feature.Properties)
                    kml.Append("<Data name=\"").Append(SecurityElement.Escape(key)).Append("\"><value>")
                        .Append(SecurityElement.Escape(value switch { double d => Number(d), double[] p => string.Join(" ", p.Select(Number)), null => "", _ => value.ToString() })).Append("</value></Data>");
                kml.Append("</ExtendedData>");
                kml.Append(feature.Kind switch
                {
                    "Point" => $"<Point><coordinates>{Coordinates(feature.Coordinates)}</coordinates></Point>",
                    "LineString" => $"<LineString><tessellate>1</tessellate><coordinates>{Coordinates(feature.Coordinates)}</coordinates></LineString>",
                    _ => $"<Polygon><outerBoundaryIs><LinearRing><coordinates>{Coordinates(Closed(feature.Coordinates))}</coordinates></LinearRing></outerBoundaryIs></Polygon>",
                });
                kml.Append("</Placemark>");
            }

            kml.Append("</Folder>");
        }

        return kml.Append("</Document></kml>").ToString();
    }

    private static string StyleId(string layer) => "c" + string.Concat(layer.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    /// <summary>Couleur RRGGBB de chaque calque, pour les styles KML.</summary>
    private static Dictionary<string, string> LayerColors(ToolContext context, Transaction transaction, IEnumerable<string> layers)
    {
        var table = (LayerTable)transaction.GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        return layers.ToDictionary(layer => layer, layer =>
        {
            var color = ((LayerTableRecord)transaction.GetObject(table[layer], OpenMode.ForRead)).Color.ColorValue;
            return $"{color.R:X2}{color.G:X2}{color.B:X2}";
        });
    }
}

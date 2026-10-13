using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map.Platform;
using McpMap3D.Shared;
using OSGeo.MapGuide;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Écriture dans une source FDO : ajout, modification et suppression d'objets (SDF, SHP, SQLite, PostGIS…). Les
/// changements partent dans la source elle-même, pas dans le dessin ; Map 3D les enregistre pour l'annulation U.
/// La géométrie vient d'un texte WKT (dans le système de la source) ou d'objets du dessin (convertis du système du
/// dessin vers celui de la source).
/// </summary>
internal static class FdoEditTools
{
    private const int MaxFeatures = 1000;

    public static object InsertFeatures(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return FdoTools.Map(() =>
        {
            var target = WritableClass(reader);
            if (!reader.TryGet("features", out var features) || features.ValueKind != JsonValueKind.Array || features.GetArrayLength() == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "« features » attend une liste d'objets, par exemple [{\"properties\":{\"NOM\":\"V12\"},\"handle\":\"2A3F\"}].");
            if (features.GetArrayLength() > MaxFeatures)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop d'objets ({features.GetArrayLength()}) : {MaxFeatures} au maximum par appel.");

            using var geometry = new GeometryBuilder(context, target);
            var commands = new MgFeatureCommandCollection();
            foreach (var feature in features.EnumerateArray())
            {
                var featureReader = new ArgReader(feature);
                var properties = Properties(target.Definition, featureReader);
                if (geometry.Read(featureReader) is { } agf)
                    properties.Add(new MgGeometryProperty(target.GeometryName!, agf));
                if (properties.GetCount() == 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams, "Chaque objet demande des propriétés, une géométrie (wkt ou handle), ou les deux.");
                commands.Add(new MgInsertFeatures(target.ClassName, properties));
            }

            Execute(target, commands, context);
            return new
            {
                Resource = target.Resource.ToString(),
                target.ClassName,
                Inserted = features.GetArrayLength(),
                GeometryConvertedFrom = geometry.ConvertedFrom,
            };
        });
    }

    public static object UpdateFeatures(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return FdoTools.Map(() =>
        {
            var target = WritableClass(reader);
            var filter = RequireFilter(reader);
            using var geometry = new GeometryBuilder(context, target);
            var properties = Properties(target.Definition, reader);
            if (geometry.Read(reader) is { } agf)
                properties.Add(new MgGeometryProperty(target.GeometryName!, agf));
            if (properties.GetCount() == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez properties, ou une géométrie (wkt ou handle).");

            var matching = Count(target, filter);
            Guard(matching, reader, "modifier");
            var commands = new MgFeatureCommandCollection();
            commands.Add(new MgUpdateFeatures(target.ClassName, properties, filter));
            Execute(target, commands, context);
            return new { Resource = target.Resource.ToString(), target.ClassName, Filter = filter, Updated = matching, GeometryConvertedFrom = geometry.ConvertedFrom };
        });
    }

    public static object DeleteFeatures(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return FdoTools.Map(() =>
        {
            var target = WritableClass(reader);
            var filter = RequireFilter(reader);
            var matching = Count(target, filter);
            Guard(matching, reader, "supprimer");
            var commands = new MgFeatureCommandCollection();
            commands.Add(new MgDeleteFeatures(target.ClassName, filter));
            Execute(target, commands, context);
            return new { Resource = target.Resource.ToString(), target.ClassName, Filter = filter, Deleted = matching };
        });
    }

    internal sealed record Target(MgResourceIdentifier Resource, string ClassName, MgClassDefinition Definition, MgGeometricPropertyDefinition? Geometry)
    {
        public string? GeometryName => Geometry?.GetName();
    }

    /// <summary>Source et classe désignées par resource et className (ou layer), en refusant les sources d'images.</summary>
    private static Target WritableClass(ArgReader reader)
    {
        MgResourceIdentifier resource;
        string requested;
        if (reader.GetString("layer") is { } layerName)
        {
            var layer = FdoTools.RequireLayer(layerName);
            resource = new MgResourceIdentifier(layer.GetFeatureSourceId());
            requested = layer.GetFeatureClassName();
        }
        else
        {
            resource = FdoTools.ResolveResource(reader.GetString("resource")
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez layer, ou resource et className."));
            requested = reader.GetString("className")
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez className (voir describe_fdo).");
        }

        if (FdoTools.IsWms(resource))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une source WMS ne contient que des images : elle ne s'écrit pas.");
        var (schema, className, definition) = FdoTools.ResolveClass(FdoTools.Features(), resource, requested);
        if (FdoTools.RasterProperty(definition) is not null)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"La classe « {schema}:{className} » est une image : elle ne s'écrit pas.");

        var geometryName = definition.GetDefaultGeometryPropertyName();
        var geometry = string.IsNullOrWhiteSpace(geometryName) ? null
            : FdoTools.AllProperties(definition).OfType<MgGeometricPropertyDefinition>().FirstOrDefault(p => p.GetName() == geometryName);
        return new Target(resource, $"{schema}:{className}", definition, geometry);
    }

    private static string RequireFilter(ArgReader reader)
    {
        if (reader.GetBool("all", false))
            return reader.Has("filter")
                ? throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez filter ou all, pas les deux.")
                : "";
        return reader.GetString("filter") is { } filter && filter.Trim().Length > 0
            ? filter.Trim()
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez filter, une expression FDO (par exemple NOM = 'V12' ou FeatId = 3), ou all=true pour tous les objets de la classe.");
    }

    /// <summary>Plus de 100 objets touchés : il faut le confirmer avec expected, pour éviter un filtre trop large.</summary>
    private static void Guard(int matching, ArgReader reader, string verb)
    {
        if (matching == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun objet de la source ne correspond au filtre : rien à " + verb + ".");
        if (matching > 100 && reader.GetInt("expected", -1) != matching)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"{matching} objets correspondent au filtre. Pour {verb} autant d'objets, confirmez avec expected={matching}.");
    }

    private static int Count(Target target, string filter)
    {
        var options = new MgFeatureQueryOptions();
        if (filter.Length > 0)
            options.SetFilter(filter);
        var featureReader = FdoTools.Features().SelectFeatures(target.Resource, target.ClassName, options);
        try
        {
            var count = 0;
            while (featureReader.ReadNext())
                count++;
            return count;
        }
        finally
        {
            featureReader.Close();
        }
    }

    /// <summary>Envoie les commandes à la source, enregistrées pour l'annulation, puis redessine les calques qui l'affichent.</summary>
    private static void Execute(Target target, MgFeatureCommandCollection commands, ToolContext context)
    {
        FdoTools.Features().UpdateFeatures(target.Resource, commands, false, true, true);
        foreach (var layer in FdoTools.LayersOf(target.Resource.ToString()))
            layer.ForceRefresh();
        context.AfterCommit(() => context.Editor.Regen());
    }

    /// <summary>Valeurs des propriétés (« properties »), converties dans le type de chaque champ de la classe.</summary>
    private static MgPropertyCollection Properties(MgClassDefinition definition, ArgReader reader)
    {
        var properties = new MgPropertyCollection();
        if (!reader.TryGet("properties", out var values))
            return properties;
        if (values.ValueKind != JsonValueKind.Object)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« properties » attend un objet, par exemple {\"NOM\":\"V12\",\"DIAMETRE\":110}.");

        var all = FdoTools.AllProperties(definition).ToList();
        foreach (var value in values.EnumerateObject())
        {
            var property = all.FirstOrDefault(p => string.Equals(p.GetName(), value.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Propriété « {value.Name} » inconnue : {string.Join(", ", all.OfType<MgDataPropertyDefinition>().Select(p => p.GetName()))}.");
            if (property is MgGeometricPropertyDefinition)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"« {value.Name} » est la géométrie : donnez-la par wkt ou handle.");
            if (property is not MgDataPropertyDefinition data)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {value.Name} » n'est pas une valeur simple.");
            if (data.GetReadOnly())
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {data.GetName()} » est en lecture seule (identifiant automatique, par exemple).");
            properties.Add(Typed(data, value.Value));
        }

        return properties;
    }

    private static MgProperty Typed(MgDataPropertyDefinition data, JsonElement value)
    {
        var name = data.GetName();
        var isNull = value.ValueKind == JsonValueKind.Null;
        string Text() => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        double Number() => value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : double.TryParse(Text().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {name} » attend un nombre, pas « {Text()} ».");

        MgNullableProperty property = data.GetDataType() switch
        {
            MgPropertyType.String or MgPropertyType.Clob => new MgStringProperty(name, isNull ? "" : Text()),
            MgPropertyType.Boolean => new MgBooleanProperty(name, !isNull && (value.ValueKind == JsonValueKind.True || Text() is "1" or "true" or "vrai")),
            MgPropertyType.Byte => new MgByteProperty(name, isNull ? (byte)0 : (byte)Number()),
            MgPropertyType.Int16 => new MgInt16Property(name, isNull ? (short)0 : (short)Math.Round(Number())),
            MgPropertyType.Int32 => new MgInt32Property(name, isNull ? 0 : (int)Math.Round(Number())),
            MgPropertyType.Int64 => new MgInt64Property(name, isNull ? 0 : (long)Math.Round(Number())),
            MgPropertyType.Single => new MgSingleProperty(name, isNull ? 0 : (float)Number()),
            MgPropertyType.Double or MgPropertyType.Decimal => new MgDoubleProperty(name, isNull ? 0 : Number()),
            MgPropertyType.DateTime => new MgDateTimeProperty(name, isNull ? new MgDateTime() : Date(name, Text())),
            var type => throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {name} » est d'un type non pris en charge ({type})."),
        };
        if (isNull)
            property.SetNull(true);
        return property;
    }

    private static MgDateTime Date(string name, string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new MgDateTime((short)date.Year, (short)date.Month, (short)date.Day, (short)date.Hour, (short)date.Minute, (short)date.Second, 0)
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"La propriété « {name} » attend une date (2026-10-13 ou 2026-10-13T14:30:00).");

    /// <summary>
    /// Géométrie d'un objet : wkt (dans le système de la source) ou handle d'un objet du dessin (point, ligne,
    /// polyligne, cercle), converti du système du dessin vers celui de la source quand les deux sont connus.
    /// </summary>
    private sealed class GeometryBuilder : IDisposable
    {
        private readonly ToolContext _context;
        private readonly Target _target;
        private PointTransform? _transform;
        private bool _transformResolved;

        public GeometryBuilder(ToolContext context, Target target) => (_context, _target) = (context, target);

        public string? ConvertedFrom { get; private set; }

        public MgByteReader? Read(ArgReader reader)
        {
            var wkt = reader.GetString("wkt");
            var handle = reader.GetString("handle");
            if (wkt is null && handle is null)
                return null;
            if (wkt is not null && handle is not null)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez wkt ou handle, pas les deux.");
            if (_target.Geometry is null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La classe « {_target.ClassName} » n'a pas de géométrie.");

            wkt ??= FromEntity(handle!);
            MgGeometry geometry;
            try
            {
                geometry = new MgWktReaderWriter().Read(wkt);
            }
            catch (MgException ex)
            {
                ex.Dispose();
                throw new PipeException(PipeErrorCodes.InvalidParams, $"WKT illisible : {Format.Truncate(wkt, 120)}");
            }

            return new MgAgfReaderWriter().Write(geometry);
        }

        private string FromEntity(string handle)
        {
            var transaction = _context.RequireTransaction();
            var entity = transaction.GetObject(Handles.ResolveOne(_context, handle), OpenMode.ForRead) as Entity
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {handle} n'est pas un objet graphique.");
            var transform = Transform();

            string Point(Point3d point)
            {
                var (x, y) = transform is null ? (point.X, point.Y) : transform.Apply(point.X, point.Y);
                return x.ToString("R", CultureInfo.InvariantCulture) + " " + y.ToString("R", CultureInfo.InvariantCulture);
            }

            string Shape(IReadOnlyList<Point3d> points, bool closed)
            {
                if (closed && points.Count >= 3)
                    return "POLYGON ((" + string.Join(", ", points.Append(points[0]).Select(Point)) + "))";
                return points.Count >= 2
                    ? "LINESTRING (" + string.Join(", ", points.Select(Point)) + ")"
                    : throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {handle} a moins de deux sommets.");
            }

            return entity switch
            {
                DBPoint point => "POINT (" + Point(point.Position) + ")",
                BlockReference block => "POINT (" + Point(block.Position) + ")",
                Line line => Shape([line.StartPoint, line.EndPoint], false),
                Circle circle => Shape(Sample(circle, 64), true),
                Polyline polyline => Shape(Vertices(polyline), polyline.Closed),
                Polyline2d polyline2d => Shape(polyline2d.Cast<ObjectId>().Select(id => ((Vertex2d)transaction.GetObject(id, OpenMode.ForRead)).Position).ToList(), polyline2d.Closed),
                Polyline3d polyline3d => Shape(polyline3d.Cast<ObjectId>().Select(id => ((PolylineVertex3d)transaction.GetObject(id, OpenMode.ForRead)).Position).ToList(), polyline3d.Closed),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {handle} ({Format.DxfName(entity)}) ne se convertit pas : points, blocs, lignes, polylignes et cercles seulement."),
            };
        }

        /// <summary>Sommets d'une polyligne, arcs (renflements) décomposés en huit segments.</summary>
        private static List<Point3d> Vertices(Polyline polyline)
        {
            var points = new List<Point3d>();
            var segments = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                points.Add(polyline.GetPoint3dAt(i));
                if (i < segments && polyline.GetBulgeAt(i) != 0)
                {
                    for (var k = 1; k < 8; k++)
                        points.Add(polyline.GetPointAtParameter(i + k / 8.0));
                }
            }

            return points;
        }

        private static List<Point3d> Sample(Curve curve, int count) =>
            Enumerable.Range(0, count).Select(k => curve.GetPointAtParameter(curve.StartParam + (curve.EndParam - curve.StartParam) * k / count)).ToList();

        /// <summary>Du système du dessin vers celui de la source ; aucune conversion si l'un des deux est inconnu.</summary>
        private PointTransform? Transform()
        {
            if (_transformResolved)
                return _transform;
            _transformResolved = true;
            var sourceCode = FdoTools.SourceSystemCode(_target.Resource, _target.Geometry!);
            if (sourceCode is null || CoordinateSystemTools.DrawingWkt(_context.Database) is null)
                return null;
            _transform = CoordinateSystemTools.CreateTransform(_context.Database, "drawing", sourceCode);
            if (_transform.IsIdentity)
            {
                _transform.Dispose();
                return _transform = null;
            }

            ConvertedFrom = $"{_transform.FromCode} vers {_transform.ToCode}";
            return _transform;
        }

        public void Dispose() => _transform?.Dispose();
    }
}

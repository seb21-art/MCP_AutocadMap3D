using System.Collections;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>Outils de lecture du dessin. Tous s'exécutent sous verrou de lecture et transaction.</summary>
internal static class ReadTools
{
    private const int MaxEntities = 1000;

    public static object GetDrawingInfo(ToolContext context, JsonElement? args)
    {
        var document = context.RequireDocument();
        var database = context.Database;
        var transaction = context.RequireTransaction();

        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var currentLayer = (LayerTableRecord)transaction.GetObject(database.Clayer, OpenMode.ForRead);
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);

        return new
        {
            FileName = Path.GetFileName(document.Name),
            Path = document.Name,
            Modified = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
            ReadOnly = document.IsReadOnly,
            Units = database.Insunits.ToString(),
            CurrentLayer = currentLayer.Name,
            CurrentSpace = database.TileMode ? "model" : "paper",
            Extents = database.Extmin.X <= database.Extmax.X
                ? Format.Extents(new Extents3d(database.Extmin, database.Extmax))
                : null,
            LayerCount = Count(layers),
            ModelSpaceEntityCount = Count(modelSpace),
            Layouts = layouts.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).OrderBy(name => name).ToArray(),
            Map = MapInfo.DescribeProject(database),
        };
    }

    public static object ListLayers(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var currentLayerId = database.Clayer;

        var result = new List<object>();
        foreach (var id in layers)
        {
            var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (filter is not null && !filter.IsMatch(layer.Name))
                continue;

            var linetype = (LinetypeTableRecord)transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
            result.Add(new
            {
                layer.Name,
                Color = Format.Color(layer.Color),
                Linetype = linetype.Name,
                LineWeight = EntityProperties.FormatLineWeight(layer.LineWeight),
                layer.IsOff,
                layer.IsFrozen,
                layer.IsLocked,
                layer.IsPlottable,
                IsCurrent = id == currentLayerId,
                Description = string.IsNullOrEmpty(layer.Description) ? null : layer.Description,
            });
        }

        return new { Count = result.Count, Layers = result };
    }

    /// <summary>
    /// Types de ligne chargés dans le dessin et, sur demande, ceux que définit le fichier .lin standard
    /// (chargés automatiquement dès qu'un outil les utilise).
    /// </summary>
    public static object ListLinetypes(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var transaction = context.RequireTransaction();
        var linetypes = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);

        var loaded = new List<object>();
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in linetypes)
        {
            var linetype = (LinetypeTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            loadedNames.Add(linetype.Name);
            loaded.Add(new
            {
                linetype.Name,
                Description = string.IsNullOrWhiteSpace(linetype.Comments) ? null : linetype.Comments.Trim(),
            });
        }

        var file = EntityProperties.LinetypeFile(database);
        object[]? available = null;
        if (reader.GetBool("includeAvailable", false))
        {
            available = ReadLinetypeFile(database, file)
                .Where(entry => !loadedNames.Contains(entry.Name))
                .Select(entry => (object)new { entry.Name, entry.Description })
                .ToArray();
        }

        return new { Loaded = loaded, File = file, Available = available };
    }

    /// <summary>Définitions d'un fichier .lin : lignes « *NOM,description ».</summary>
    private static IEnumerable<(string Name, string? Description)> ReadLinetypeFile(Database database, string file)
    {
        string path;
        try
        {
            path = HostApplicationServices.Current.FindFile(file, database, FindFileHint.Default);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new PipeException(PipeErrorCodes.Internal, $"Fichier de types de ligne {file} introuvable.");
        }

        foreach (var line in File.ReadLines(path, System.Text.Encoding.Latin1))
        {
            if (!line.StartsWith('*'))
                continue;

            var separator = line.IndexOf(',');
            var name = (separator < 0 ? line[1..] : line[1..separator]).Trim();
            var description = separator < 0 ? null : line[(separator + 1)..].Trim();
            if (name.Length > 0)
                yield return (name, string.IsNullOrEmpty(description) ? null : description);
        }
    }

    public static object ListEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var layerFilter = NameFilter.Create(reader.GetStrings("layers"));
        var typeFilter = NameFilter.Create(reader.GetStrings("types"));
        var offset = reader.GetInt("offset", 0, min: 0);
        var limit = reader.GetInt("limit", 100, min: 1, max: MaxEntities);
        var includeGeometry = reader.GetBool("includeGeometry", false);
        var space = reader.GetString("space", "model")!;

        var transaction = context.RequireTransaction();

        // Objets désignés : on les décrit tels quels, sans filtre ni pagination.
        if (reader.Has("handles"))
        {
            var described = Handles.Resolve(context, reader)
                .Select(id => transaction.GetObject(id, OpenMode.ForRead) as Entity
                    ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un objet graphique."))
                .Select(entity => Describe(entity, includeGeometry, transaction))
                .ToList();
            return new { Total = described.Count, Offset = 0, Limit = described.Count, Count = described.Count, Entities = described };
        }

        var container = OpenSpace(context, space, transaction);

        var entities = new List<object>();
        var total = 0;
        foreach (var id in container)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            if (layerFilter is not null && !layerFilter.IsMatch(entity.Layer))
                continue;

            if (typeFilter is not null && !typeFilter.IsMatch(Format.DxfName(entity)))
                continue;

            total++;
            if (total <= offset || entities.Count >= limit)
                continue;

            entities.Add(Describe(entity, includeGeometry, transaction));
        }

        return new { Total = total, Offset = offset, Limit = limit, Count = entities.Count, Entities = entities };
    }

    private static BlockTableRecord OpenSpace(ToolContext context, string space, Transaction transaction)
    {
        var database = context.Database;
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

        var name = space.ToLowerInvariant() switch
        {
            "model" => BlockTableRecord.ModelSpace,
            "paper" => BlockTableRecord.PaperSpace,
            "current" => database.TileMode ? BlockTableRecord.ModelSpace : BlockTableRecord.PaperSpace,
            _ => null,
        };

        if (name is not null)
            return (BlockTableRecord)transaction.GetObject(blocks[name], OpenMode.ForRead);

        // Sinon, nom d'une présentation : on ouvre son espace papier.
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DictionaryEntry entry in layouts)
        {
            if (!string.Equals((string)entry.Key, space, StringComparison.OrdinalIgnoreCase))
                continue;

            var layout = (Layout)transaction.GetObject((ObjectId)entry.Value!, OpenMode.ForRead);
            return (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        }

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Espace « {space} » introuvable. Valeurs possibles : model, paper, current ou le nom d'une présentation.");
    }

    private static object Describe(Entity entity, bool includeGeometry, Transaction transaction)
    {
        var description = new Dictionary<string, object?>
        {
            ["handle"] = entity.Handle.ToString(),
            ["type"] = Format.DxfName(entity),
            ["layer"] = entity.Layer,
        };

        // Propriétés graphiques : signalées seulement quand elles diffèrent de celles du calque.
        if (!entity.Color.IsByLayer)
            description["color"] = Format.Color(entity.Color);

        if (!string.Equals(entity.Linetype, "ByLayer", StringComparison.OrdinalIgnoreCase))
            description["linetype"] = entity.Linetype;

        if (entity.LinetypeScale != 1)
            description["linetypeScale"] = Format.Number(entity.LinetypeScale);

        if (entity.LineWeight != LineWeight.ByLayer)
            description["lineWeight"] = EntityProperties.FormatLineWeight(entity.LineWeight);

        if (EntityProperties.GetThickness(entity) is double thickness && thickness != 0)
            description["thickness"] = Format.Number(thickness);

        switch (entity)
        {
            case Line line:
                description["length"] = Format.Number(line.Length);
                if (includeGeometry)
                {
                    description["start"] = Format.Point(line.StartPoint);
                    description["end"] = Format.Point(line.EndPoint);
                }

                break;

            case Polyline polyline:
                description["vertices"] = polyline.NumberOfVertices;
                description["closed"] = polyline.Closed;
                description["length"] = Format.Number(polyline.Length);
                if (polyline.Closed)
                    description["area"] = Format.Number(polyline.Area);

                if (includeGeometry)
                {
                    description["points"] = Enumerable.Range(0, polyline.NumberOfVertices)
                        .Select(index => Format.Point(polyline.GetPoint2dAt(index))).ToArray();

                    // Arrondis : renflement de chaque segment (tangente du quart de l'angle de l'arc, positif à gauche).
                    var bulges = Enumerable.Range(0, polyline.NumberOfVertices).Select(polyline.GetBulgeAt).ToArray();
                    if (bulges.Any(bulge => bulge != 0))
                        description["bulges"] = bulges.Select(Format.Number).ToArray();
                }

                break;

            case Circle circle:
                description["center"] = Format.Point(circle.Center);
                description["radius"] = Format.Number(circle.Radius);
                break;

            case Arc arc:
                description["center"] = Format.Point(arc.Center);
                description["radius"] = Format.Number(arc.Radius);
                if (includeGeometry)
                {
                    description["startAngle"] = Format.Degrees(arc.StartAngle);
                    description["endAngle"] = Format.Degrees(arc.EndAngle);
                }

                break;

            case DBText text:
                description["text"] = Format.Truncate(text.TextString, 200);
                description["height"] = Format.Number(text.Height);
                if (includeGeometry)
                    description["position"] = Format.Point(text.Position);

                break;

            case MText mtext:
                description["text"] = Format.Truncate(mtext.Text, 200);
                description["height"] = Format.Number(mtext.TextHeight);
                if (includeGeometry)
                    description["position"] = Format.Point(mtext.Location);

                break;

            case Table table:
                description["rows"] = table.Rows.Count;
                description["columns"] = table.Columns.Count;
                description["position"] = Format.Point(table.Position);
                var (cells, truncated) = includeGeometry
                    ? DraftingTools.TableTexts(table, 1000, 100, 200)
                    : DraftingTools.TableTexts(table, 10, 12, 80);
                description["cells"] = cells;
                if (truncated)
                    description["cellsTruncated"] = true;

                break;

            case BlockReference reference:
                description["block"] = BlockName(reference, transaction);
                description["position"] = Format.Point(reference.Position);
                if (reference.AttributeCollection.Count > 0)
                    description["attributes"] = BlockTools.AttributeValues(reference, transaction);
                if (includeGeometry)
                {
                    description["rotation"] = Format.Degrees(reference.Rotation);
                    description["scale"] = new[]
                    {
                        Format.Number(reference.ScaleFactors.X),
                        Format.Number(reference.ScaleFactors.Y),
                        Format.Number(reference.ScaleFactors.Z),
                    };
                }

                break;

            case Hatch hatch:
                description["pattern"] = hatch.PatternName;
                description["area"] = TryArea(hatch);
                break;

            case Dimension dimension:
                description["measurement"] = DimensionStyleTools.Measurement(dimension);
                if (!string.IsNullOrEmpty(dimension.DimensionText))
                    description["text"] = dimension.DimensionText;

                break;

            case MLeader leader:
                if (leader.MText is MText content)
                    description["text"] = Format.Truncate(content.Text, 200);

                break;

            case DBPoint point:
                description["position"] = Format.Point(point.Position);
                break;

            case Xline xline:
                description["basePoint"] = Format.Point(xline.BasePoint);
                description["direction"] = Format.Vector(xline.UnitDir);
                break;

            case Ray ray:
                description["basePoint"] = Format.Point(ray.BasePoint);
                description["direction"] = Format.Vector(ray.UnitDir);
                break;

            case Polyline3d polyline3d:
                description["closed"] = polyline3d.Closed;
                description["length"] = Format.Number(polyline3d.GetDistanceAtParameter(polyline3d.EndParam));
                if (includeGeometry)
                    description["points"] = Vertices(polyline3d, transaction);

                break;

            case Solid3d solid when includeGeometry:
                description["volume"] = ModelingTools.TryVolume(solid);
                break;
        }

        if (includeGeometry)
            description["extents"] = Format.Extents(Format.TryGetExtents(entity));

        return description;
    }

    private static double[][] Vertices(Polyline3d polyline, Transaction transaction)
    {
        var points = new List<double[]>();
        foreach (ObjectId vertexId in polyline)
        {
            var vertex = (PolylineVertex3d)transaction.GetObject(vertexId, OpenMode.ForRead);
            points.Add(Format.Point(vertex.Position));
        }

        return [.. points];
    }

    private static string BlockName(BlockReference reference, Transaction transaction)
    {
        var definition = (BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead);
        return definition.Name;
    }

    private static double? TryArea(Hatch hatch)
    {
        try
        {
            return Format.Number(hatch.Area);
        }
        catch
        {
            return null;
        }
    }

    private static int Count(SymbolTable table)
    {
        var count = 0;
        foreach (var _ in table)
            count++;

        return count;
    }

    private static int Count(BlockTableRecord container)
    {
        var count = 0;
        foreach (var _ in container)
            count++;

        return count;
    }
}

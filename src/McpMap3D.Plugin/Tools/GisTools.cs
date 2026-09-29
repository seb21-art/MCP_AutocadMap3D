using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ImportExport;
using Autodesk.Gis.Map.Utilities;
using McpMap3D.Shared;
using AcMapCoordsysCore = Autodesk.Gis.Map.CoordinateSystem.AcMapCoordsysCore;
using OdOpenMode = Autodesk.Gis.Map.Constants.OpenMode;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Import et export de fichiers SIG (SHP, MapInfo, GML, E00, DGN, SDF) par l'API ImportExport de Map 3D, comme
/// MAPIMPORT et MAPEXPORT. L'import crée des objets AutoCAD avec leurs attributs en données d'objet ; l'export écrit
/// des objets du dessin et leurs données d'objet dans un fichier.
/// </summary>
/// <remarks>
/// Autodesk ne publie pas ManagedMapApi : les membres dont le nom .NET n'est confirmé par aucun exemple (système de
/// coordonnées d'une couche, activation d'une couche, colonnes) sont atteints par réflexion, sous leurs noms possibles.
/// S'ils manquent, l'outil le dit dans Warnings au lieu d'échouer.
/// </remarks>
internal static class GisTools
{
    private const int MaxListedHandles = 200;

    /// <summary>Format de l'API ImportExport d'après l'extension du fichier.</summary>
    private static readonly Dictionary<string, string> FormatsByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".shp"] = "SHP",
        [".mif"] = "MIF",
        [".tab"] = "MAPINFO",
        [".gml"] = "GML",
        [".xml"] = "GML",
        [".e00"] = "E00",
        [".dgn"] = "DGN",
        [".sdf"] = "SDF",
    };

    /// <summary>Fichiers qui accompagnent un fichier SIG de même nom (remplacés ensemble).</summary>
    private static readonly string[] CompanionExtensions =
        [".shp", ".shx", ".dbf", ".prj", ".cpg", ".sbn", ".sbx", ".qix", ".mif", ".mid", ".tab", ".dat", ".map", ".id", ".ind", ".xsd"];

    public static object ImportGis(ToolContext context, JsonElement? args) =>
        Guard(() => ImportCore(context, args), "import d'un fichier SIG");

    public static object ExportGis(ToolContext context, JsonElement? args) =>
        Guard(() => ExportCore(context, args), "export vers un fichier SIG");

    private static object Guard(Func<object> action, string operation)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, $"« {operation} » requiert AutoCAD Map 3D (ou Civil 3D).");
        return GuardCore(action, operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GuardCore(Func<object> action, string operation)
    {
        try
        {
            return action();
        }
        catch (MapException ex)
        {
            throw MapTools.Translate(ex, operation);
        }
    }

    // ---------------------------------------------------------------- import

    private static object ImportCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(reader.RequireString("filePath")), DrawingFolder(context));
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        var format = ResolveFormat(reader.GetString("format"), path);
        var preview = reader.GetBool("preview", false);
        var attributes = ParseAttributes(reader.GetString("attributes"));
        var transform = reader.GetBool("transform", true);
        var closedPolylines = reader.GetBool("polygonsAsClosedPolylines", true);
        var layerFilter = NameFilter.Create(reader.GetStrings("inputLayers"));
        var targetLayer = reader.GetString("layer");
        var odTable = reader.GetString("odTable");
        var drawingCs = AcMapCoordsysCore.GetCoordinateSystem(database) ?? "";
        var warnings = new List<string>();

        var importer = HostMapApplicationServices.Application.Importer;
        Init(format, path, (f, p) => importer.Init(f, p), () => HostMapApplicationServices.Application.ImportFormats);

        var layers = new List<InputLayer>();
        foreach (InputLayer layer in importer)
            layers.Add(layer);
        if (layers.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Map 3D ne trouve aucune couche à importer dans {path}.");

        var tables = MapInfo.RequireProject(database).ODTables;
        var reservedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<object>();
        var selected = 0;
        foreach (var layer in layers)
        {
            var name = LayerName(layer, path);
            var enabled = layerFilter is null || layerFilter.IsMatch(name) || layerFilter.IsMatch(FullLayerName(layer));
            var sourceCs = ReadString(layer, "OriginalCoordinateSys", "OriginalCoordSys", "OriginalCoordinateSystem");
            var columns = ReadColumns(layer);
            string? acadLayer = null;
            string? table = null;
            string? conversion = null;

            if (enabled)
            {
                selected++;
                acadLayer = CleanLayerName(targetLayer ?? name);
                layer.SetLayerName(LayerNameType.LayerNameDirect, acadLayer);

                if (attributes)
                {
                    var wanted = CleanTableName(odTable is null ? name : layers.Count == 1 ? odTable : $"{odTable}_{name}");
                    table = UniqueTableName(tables, reservedTables, wanted);
                    if (!preview)
                        layer.SetDataMapping(ImportDataMapping.NewObjectDataOnly, table);
                }

                conversion = PlanConversion(layer, name, sourceCs, drawingCs, transform, preview, warnings);
            }

            if (!preview && layerFilter is not null)
                Write(layer, enabled, warnings, name, "ImportFromInputLayerOn", "SetImportFromInputLayerOn");

            plans.Add(new
            {
                InputLayer = name,
                Imported = enabled,
                SourceCoordinateSystem = string.IsNullOrEmpty(sourceCs) ? null : sourceCs,
                Conversion = conversion,
                Layer = acadLayer,
                OdTable = table,
                Columns = columns,
            });
        }

        if (selected == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucune couche du fichier ne correspond à inputLayers. Couches : {string.Join(", ", layers.Select(l => LayerName(l, path)))}.");

        if (preview)
        {
            return new
            {
                File = path,
                Format = format,
                DrawingCoordinateSystem = string.IsNullOrEmpty(drawingCs) ? null : drawingCs,
                InputLayers = plans,
                Warnings = warnings.Count > 0 ? warnings : null,
                Note = "Aperçu seulement : rien n'a été importé.",
            };
        }

        importer.ImportPolygonsAsClosedPolylines = closedPolylines;

        // Map 3D importe avec ses propres transactions : l'import a lieu une fois la transaction de l'outil validée,
        // toujours dans la même commande, donc annulable en une étape.
        object? result = null;
        context.AfterCommit(() =>
        {
            var created = new List<ObjectId>();
            var entityClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Entity));
            void OnAppended(object sender, ObjectEventArgs e)
            {
                var id = e.DBObject.ObjectId;
                if (id.ObjectClass.IsDerivedFrom(entityClass))
                    created.Add(id);
            }

            database.ObjectAppended += OnAppended;
            long imported;
            try
            {
                var results = importer.Import(true);
                imported = Convert.ToInt64(results.EntitiesImported);
            }
            catch (MapException ex)
            {
                // Hors de Guard : l'import a lieu après le retour de l'outil.
                throw MapTools.Translate(ex, $"import de {Path.GetFileName(path)}");
            }
            finally
            {
                database.ObjectAppended -= OnAppended;
            }

            result = DescribeImport(database, path, format, drawingCs, imported, created, plans, warnings);
        });
        context.ResultAfterCommit = () => result;
        return new { File = path };
    }

    private static string? PlanConversion(InputLayer layer, string name, string sourceCs, string drawingCs, bool transform,
        bool preview, List<string> warnings)
    {
        if (string.IsNullOrEmpty(drawingCs))
        {
            if (!string.IsNullOrEmpty(sourceCs))
                warnings.Add($"{name} : le dessin n'a pas de système de coordonnées, les coordonnées de {sourceCs} sont " +
                             "reprises telles quelles. Attribuez-en un avec set_coordinate_system pour les convertir.");
            return null;
        }

        if (string.IsNullOrEmpty(sourceCs))
        {
            warnings.Add($"{name} : système de coordonnées du fichier inconnu (pas de .prj ?), coordonnées reprises telles " +
                         $"quelles dans {drawingCs}.");
            return null;
        }

        if (!transform || string.Equals(sourceCs, drawingCs, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!preview && !Write(layer, drawingCs, warnings, name, "TargetCoordinateSystem", "TargetCoordSys", "SetTargetCoordSys",
                "SetTargetCoordinateSystem"))
            return null;

        return $"{sourceCs} -> {drawingCs}";
    }

    private static object DescribeImport(Database database, string path, string format, string drawingCs, long imported,
        List<ObjectId> created, List<object> plans, List<string> warnings)
    {
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var handles = new List<string>();
        Autodesk.AutoCAD.Geometry.Point3d? min = null, max = null;
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            foreach (var id in created)
            {
                // Les objets placés dans une définition de bloc (symboles des points) ne sont pas comptés.
                if (id.IsErased || transaction.GetObject(id, OpenMode.ForRead) is not Entity entity
                    || transaction.GetObject(entity.OwnerId, OpenMode.ForRead) is not BlockTableRecord { IsLayout: true })
                    continue;

                var type = Format.DxfName(entity);
                byType[type] = byType.GetValueOrDefault(type) + 1;
                byLayer[entity.Layer] = byLayer.GetValueOrDefault(entity.Layer) + 1;
                if (handles.Count < MaxListedHandles)
                    handles.Add(Format.Handle(id));
                if (Format.TryGetExtents(entity) is { } extents)
                {
                    min = min is { } a ? new(Math.Min(a.X, extents.MinPoint.X), Math.Min(a.Y, extents.MinPoint.Y), Math.Min(a.Z, extents.MinPoint.Z)) : extents.MinPoint;
                    max = max is { } b ? new(Math.Max(b.X, extents.MaxPoint.X), Math.Max(b.Y, extents.MaxPoint.Y), Math.Max(b.Z, extents.MaxPoint.Z)) : extents.MaxPoint;
                }
            }

            transaction.Commit();
        }

        var count = byType.Values.Sum();
        if (count == 0 && imported == 0)
            warnings.Add("Aucun objet importé : vérifiez inputLayers, le format et le contenu du fichier.");

        return new
        {
            File = path,
            Format = format,
            EntitiesImported = imported,
            Count = count,
            ByType = byType,
            ByLayer = byLayer,
            Extents = min is { } lo && max is { } hi ? new { Min = Format.Point(lo), Max = Format.Point(hi) } : null,
            Handles = handles,
            Truncated = count > handles.Count ? true : (bool?)null,
            DrawingCoordinateSystem = string.IsNullOrEmpty(drawingCs) ? null : drawingCs,
            InputLayers = plans,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    // ---------------------------------------------------------------- export

    private static object ExportCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(reader.RequireString("filePath")), DrawingFolder(context));
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le dossier {directory} n'existe pas.");

        var format = ResolveFormat(reader.GetString("format"), path);
        var requestedGeometry = (reader.GetString("geometry") ?? "auto").Trim().ToLowerInvariant();
        if (requestedGeometry is not ("auto" or "point" or "line" or "polygon"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "geometry attend auto, point, line ou polygon.");

        var attributes = ParseAttributes(reader.GetString("attributes"));
        var tableFilter = NameFilter.Create(reader.GetStrings("odTables"));
        var overwrite = reader.GetBool("overwrite", false);
        var dimension = (reader.GetString("dimension") ?? "2d").Trim().ToUpperInvariant();
        if (dimension is not ("2D" or "3D"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "dimension attend 2d ou 3d.");

        var targetCs = reader.GetString("targetCoordinateSystem");
        var drawingCs = AcMapCoordsysCore.GetCoordinateSystem(database) ?? "";
        if (!string.IsNullOrWhiteSpace(targetCs) && string.IsNullOrEmpty(drawingCs))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin n'a pas de système de coordonnées : attribuez-en un avec set_coordinate_system avant de convertir à l'export.");

        var warnings = new List<string>();
        var (ids, geometry, skipped, degenerate) = SelectForExport(context, reader, requestedGeometry);
        if (degenerate.Count > 0)
            warnings.Add($"{degenerate.Count} courbe(s) de longueur nulle ignorée(s) : {string.Join(", ", degenerate.Take(20))}" +
                         (degenerate.Count > 20 ? "…" : "") + ".");
        var columns = attributes ? MapColumns(database, ids, tableFilter, format) : [];

        var existing = Companions(path).Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            if (!overwrite)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"{Path.GetFileName(existing[0])} existe déjà. Passez overwrite=true pour le remplacer.");
            foreach (var file in existing)
                File.Delete(file);
        }

        var started = DateTime.Now.AddSeconds(-2);
        var exporter = HostMapApplicationServices.Application.Exporter;
        Init(format, path, (f, p) => exporter.Init(f, p), () => HostMapApplicationServices.Application.ExportFormats);
        exporter.ExportAll = false;
        exporter.SetStorageOptions(StorageType.FileOneEntityType, ParseEnum<GeometryType>(geometry), string.Empty);
        exporter.ClosedPolylinesAsPolygons = geometry == "Polygon";
        exporter.SetSelectionSet(new ObjectIdCollection([.. ids]));

        if (format == "SHP")
        {
            var options = exporter.GetDriverOptions();
            var pair = new StringPair("FDO_SHAPE_DIMENSION", dimension);
            if (!options.Contains(pair))
                options.Add(pair);
            exporter.SetDriverOptions(options);
        }

        if (columns.Count > 0)
        {
            var mappings = exporter.GetExportDataMappings();
            foreach (var column in columns)
                mappings.Add($":{column.Field}@{column.Table}", column.Column);
            exporter.SetExportDataMappings(mappings);
        }

        string? conversion = null;
        if (!string.IsNullOrWhiteSpace(targetCs) && !string.Equals(targetCs, drawingCs, StringComparison.OrdinalIgnoreCase)
            && Write(exporter, targetCs.Trim(), warnings, "export", "TargetCoordinateSystem", "TargetCoordSys", "SetTargetCoordSys",
                "SetTargetCoordinateSystem"))
            conversion = $"{drawingCs} -> {targetCs.Trim()}";

        var results = exporter.Export(true);
        var exported = Convert.ToInt64(results.EntitiesExported);
        var untransformed = Convert.ToInt64(results.EntitiesSkippedCouldNotTransform);
        var written = Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(path) + ".*")
            .Where(file => File.GetLastWriteTime(file) >= started)
            .Select(file => new { File = file, Bytes = new FileInfo(file).Length })
            .ToList();
        if (written.Count == 0)
            warnings.Add($"Map 3D n'a signalé aucune erreur, mais aucun fichier {Path.GetFileNameWithoutExtension(path)}.* n'a été écrit.");
        if (untransformed > 0)
            warnings.Add($"{untransformed} objet(s) ignoré(s) : conversion de coordonnées impossible.");
        var missing = ids.Count - exported - untransformed;
        if (missing > 0)
            warnings.Add($"{missing} objet(s) sélectionné(s) non écrit(s) par Map 3D, sans raison donnée.");

        return new
        {
            File = path,
            Format = format,
            Geometry = geometry,
            Selected = ids.Count,
            EntitiesExported = exported,
            SkippedCouldNotTransform = untransformed > 0 ? untransformed : (long?)null,
            SkippedOtherGeometry = skipped.Count > 0 ? skipped : null,
            SkippedNotWritten = missing > 0 ? missing : (long?)null,
            Dimension = format == "SHP" ? dimension : null,
            CoordinateSystem = conversion ?? (string.IsNullOrEmpty(drawingCs) ? null : drawingCs),
            Columns = columns.Count > 0 ? columns : null,
            Files = written,
            Replaced = existing.Count > 0 ? true : (bool?)null,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private sealed record ColumnMapping(string Table, string Field, string Column);

    /// <summary>
    /// Objets à exporter (handles, sinon calques, sinon tout l'espace objet) triés par géométrie. Un fichier SIG ne
    /// garde qu'un type de géométrie : en auto, une sélection mélangée est refusée avec le compte de chaque type.
    /// </summary>
    private static (List<ObjectId> Ids, string Geometry, Dictionary<string, int> Skipped, List<string> Degenerate) SelectForExport(
        ToolContext context, ArgReader reader, string requested)
    {
        var database = context.Database;
        var layerFilter = NameFilter.Create(reader.GetStrings("layers"));
        var byGeometry = new Dictionary<string, List<ObjectId>>(StringComparer.Ordinal)
        {
            ["Point"] = [], ["Line"] = [], ["Polygon"] = [],
        };
        var unsupported = 0;
        var degenerate = new List<string>();

        using (var transaction = database.TransactionManager.StartTransaction())
        {
            IEnumerable<ObjectId> candidates;
            if (reader.GetStrings("handles").Count > 0)
            {
                candidates = Handles.Resolve(context, reader);
            }
            else
            {
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                candidates = ((BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead)).Cast<ObjectId>().ToList();
            }

            foreach (var id in candidates)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
                    continue;
                if (layerFilter is not null && !layerFilter.IsMatch(entity.Layer))
                    continue;

                var kind = Classify(entity);
                if (kind is null)
                    unsupported++;
                else if (kind != "Point" && IsDegenerate(entity))
                    degenerate.Add(Format.Handle(id));
                else
                    byGeometry[kind].Add(id);
            }

            transaction.Commit();
        }

        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        if (unsupported > 0)
            skipped["unsupported"] = unsupported;
        if (degenerate.Count > 0)
            skipped["zeroLength"] = degenerate.Count;

        string geometry;
        if (requested == "auto")
        {
            var present = byGeometry.Where(pair => pair.Value.Count > 0).ToList();
            // Des courbes ouvertes et fermées ensemble s'exportent en lignes.
            if (present.Count == 2 && byGeometry["Point"].Count == 0)
            {
                byGeometry["Line"].AddRange(byGeometry["Polygon"]);
                byGeometry["Polygon"].Clear();
                present = byGeometry.Where(pair => pair.Value.Count > 0).ToList();
            }

            if (present.Count == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun objet exportable (point, bloc, texte, courbe ou polygone) dans la sélection.");
            if (present.Count > 1)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"La sélection mélange des géométries ({string.Join(", ", present.Select(p => $"{p.Value.Count} {p.Key.ToLowerInvariant()}"))}) " +
                    "et un fichier SIG n'en garde qu'une : précisez geometry (point, line ou polygon), un fichier par géométrie.");
            geometry = present[0].Key;
        }
        else
        {
            geometry = char.ToUpperInvariant(requested[0]) + requested[1..];
            // Une ligne peut être fermée : les polygones s'exportent aussi en lignes.
            if (geometry == "Line")
            {
                byGeometry["Line"].AddRange(byGeometry["Polygon"]);
                byGeometry["Polygon"].Clear();
            }
        }

        foreach (var (kind, list) in byGeometry)
            if (kind != geometry && list.Count > 0)
                skipped[kind.ToLowerInvariant()] = list.Count;

        var ids = byGeometry[geometry];
        if (ids.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucun objet de géométrie {requested} dans la sélection.");
        return (ids, geometry, skipped, degenerate);
    }

    /// <summary>Courbe de longueur nulle (sommets confondus) : Map 3D ne l'écrit pas, sans le signaler.</summary>
    private static bool IsDegenerate(Entity entity)
    {
        if (entity is not Curve curve)
            return false;
        try
        {
            return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam) < 1e-9;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return true;
        }
    }

    /// <summary>Point, Line ou Polygon selon ce que MAPEXPORT sait écrire ; null pour le reste.</summary>
    private static string? Classify(Entity entity) => entity switch
    {
        DBPoint or BlockReference or DBText or MText => "Point",
        Circle => "Polygon",
        Polyline polyline => polyline.Closed ? "Polygon" : "Line",
        Polyline2d polyline => polyline.Closed ? "Polygon" : "Line",
        Polyline3d polyline => polyline.Closed ? "Polygon" : "Line",
        Curve curve => curve.Closed ? "Polygon" : "Line",
        _ when entity.GetRXClass().DxfName == "MPOLYGON" => "Polygon",
        _ => null,
    };

    /// <summary>
    /// Colonnes du fichier pour les tables de données d'objet présentes sur les objets exportés : nom du champ, préfixé
    /// par la table en cas de doublon, 10 caractères au plus en SHP (limite du format dBase).
    /// </summary>
    private static List<ColumnMapping> MapColumns(Database database, IReadOnlyList<ObjectId> ids, NameFilter? tableFilter, string format)
    {
        var tables = MapInfo.RequireProject(database).ODTables;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new List<ColumnMapping>();
        var maxLength = format == "SHP" ? 10 : 31;
        foreach (string? name in tables.GetTableNames())
        {
            if (name is null || (tableFilter is not null && !tableFilter.IsMatch(name)))
                continue;

            var table = tables[name];
            if (!ids.Any(id => HasRecord(table, id)))
                continue;

            var definitions = table.FieldDefinitions;
            for (var index = 0; index < definitions.Count; index++)
            {
                var field = definitions[index].Name;
                var column = Truncate(field, maxLength);
                if (used.Contains(column))
                    column = Unique(Truncate($"{name}_{field}", maxLength), used, maxLength);
                used.Add(column);
                columns.Add(new ColumnMapping(name, field, column));
            }
        }

        return columns;
    }

    private static bool HasRecord(Autodesk.Gis.Map.ObjectData.Table table, ObjectId id)
    {
        using var records = table.GetObjectTableRecords(0, id, OdOpenMode.OpenForRead, false);
        return records.Count > 0;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private static string Unique(string name, HashSet<string> used, int maxLength)
    {
        if (!used.Contains(name))
            return name;
        for (var index = 2; ; index++)
        {
            var suffix = $"_{index}";
            var candidate = Truncate(name, maxLength - suffix.Length) + suffix;
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    // ---------------------------------------------------------------- commun

    /// <summary>
    /// Format retenu, parmi ceux que l'outil sait piloter : un nom inconnu transmis à Init fait planter AutoCAD
    /// (constaté avec « EDIGEO » dans Civil 3D 2026), il est donc refusé avant tout appel à Map 3D.
    /// </summary>
    private static string ResolveFormat(string? requested, string path)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var name = requested.Trim().ToUpperInvariant();
            if (name == "TAB")
                name = "MAPINFO";
            return FormatsByExtension.ContainsValue(name)
                ? name
                : throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Format « {requested.Trim()} » non pris en charge : SHP, MIF, MAPINFO (TAB), GML, E00, DGN ou SDF. " +
                    "Pour le cadastre (EDIGEO), utilisez import_cadastre.");
        }

        return FormatsByExtension.TryGetValue(Path.GetExtension(path), out var format)
            ? format
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Format inconnu pour l'extension « {Path.GetExtension(path)} » : précisez format (SHP, MIF, MAPINFO, GML, E00, DGN, SDF).");
    }

    private static void Init(string format, string path, Action<string, string> init, Func<FormatCollection> formats)
    {
        try
        {
            init(format, path);
        }
        catch (MapException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Map 3D refuse le format « {format} » pour {Path.GetFileName(path)} ({MapTools.Translate(ex, "ouverture").Message}). " +
                $"Formats reconnus : {FormatNames(formats)}.");
        }
    }

    /// <summary>Noms des formats déclarés par Map 3D (FormatName et extension), sinon la liste usuelle.</summary>
    private static string FormatNames(Func<FormatCollection> formats)
    {
        try
        {
            var names = new List<string>();
            foreach (Autodesk.Gis.Map.ImportExport.Format item in formats())
                names.Add(string.IsNullOrEmpty(item.Extension) ? item.FormatName : $"{item.FormatName} ({item.Extension})");
            if (names.Count > 0)
                return string.Join(", ", names);
        }
        catch (MapException)
        {
        }

        return "SHP, MIF, MAPINFO (TAB), GML, E00, DGN, SDF";
    }

    private static bool ParseAttributes(string? value) => (value ?? "object_data").Trim().ToLowerInvariant() switch
    {
        "object_data" or "od" => true,
        "none" => false,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, "attributes attend object_data ou none."),
    };

    private static T ParseEnum<T>(string name) where T : struct, Enum =>
        Enum.TryParse<T>(name, true, out var value)
            ? value
            : throw new PipeException(PipeErrorCodes.Internal,
                $"{typeof(T).Name} n'a pas de valeur {name} dans cette version de Map 3D ({string.Join(", ", Enum.GetNames<T>())}).");

    /// <summary>
    /// Nom d'une couche d'entrée sans le schéma FDO que Map 3D met devant (« Default:reseau » devient « reseau »).
    /// </summary>
    private static string LayerName(InputLayer layer, string path)
    {
        var name = FullLayerName(layer);
        if (string.IsNullOrWhiteSpace(name))
            return Path.GetFileNameWithoutExtension(path);
        var colon = name.LastIndexOf(':');
        return colon >= 0 && colon < name.Length - 1 ? name[(colon + 1)..] : name;
    }

    private static string FullLayerName(InputLayer layer) => ReadString(layer, "Name");

    private static string CleanLayerName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), "[<>/\\\\\":;?*|,=`]", "_");
        return cleaned.Length == 0 ? "SIG" : cleaned;
    }

    /// <summary>Nom de table de données d'objet : lettres, chiffres et _, sans chiffre en tête, 31 caractères au plus.</summary>
    private static string CleanTableName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), "[^A-Za-z0-9_]", "_");
        if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
            cleaned = "T_" + cleaned;
        return Truncate(cleaned, 31);
    }

    private static string UniqueTableName(Autodesk.Gis.Map.ObjectData.Tables tables, HashSet<string> reserved, string name)
    {
        var candidate = name;
        for (var index = 1; tables.IsTableDefined(candidate) || reserved.Contains(candidate); index++)
        {
            var suffix = $"_{index}";
            candidate = Truncate(name, 31 - suffix.Length) + suffix;
        }

        reserved.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Colonnes d'une couche d'entrée : Map 3D 2026 les énumère sur la couche elle-même (objets Column, nom seul) ;
    /// les noms de collection sont gardés pour d'autres versions.
    /// </summary>
    private static List<object>? ReadColumns(InputLayer layer)
    {
        var collection = Read(layer, "Columns", "GetColumns", "ColumnIterator") ?? layer;
        if (collection is not System.Collections.IEnumerable items)
            return null;

        var columns = new List<object>();
        foreach (var column in items)
        {
            if (column is null)
                continue;
            var name = Read(column, "ColumnName", "Name")?.ToString();
            var type = Read(column, "ColumnType", "Type", "DataType")?.ToString();
            columns.Add(new { Name = name, Type = type });
        }

        return columns;
    }

    private static string ReadString(object target, params string[] names) => Read(target, names)?.ToString() ?? "";

    /// <summary>Première propriété ou méthode sans paramètre trouvée parmi <paramref name="names"/>.</summary>
    private static object? Read(object target, params string[] names)
    {
        var type = target.GetType();
        foreach (var name in names)
        {
            try
            {
                if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } property
                    && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target);
                if (type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is { } method)
                    return method.Invoke(target, null);
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Écrit une valeur par la première propriété ou méthode à un paramètre trouvée ; sinon un avertissement.
    /// Une erreur de Map 3D est remontée telle quelle.
    /// </summary>
    private static bool Write(object target, object value, List<string> warnings, string subject, params string[] names)
    {
        var type = target.GetType();
        foreach (var name in names)
        {
            try
            {
                if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } property
                    && property.PropertyType.IsInstanceOfType(value))
                {
                    property.SetValue(target, value);
                    return true;
                }

                if (type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, [value.GetType()]) is { } method)
                {
                    method.Invoke(target, [value]);
                    return true;
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }

        warnings.Add($"{subject} : réglage {names[0]} introuvable dans cette version de l'API Map 3D, ignoré.");
        return false;
    }

    private static IEnumerable<string> Companions(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        return CompanionExtensions.Select(extension => Path.Combine(folder, stem + extension)).Append(path).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Dossier du dessin enregistré, sinon le dossier temporaire.</summary>
    private static string DrawingFolder(ToolContext context)
    {
        var drawing = context.RequireDocument().Name;
        return Path.IsPathRooted(drawing) && File.Exists(drawing) ? Path.GetDirectoryName(drawing)! : Path.GetTempPath();
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.CoordinateSystem;
using Autodesk.Gis.Map.ObjectData;
using McpMap3D.Shared;
using OSGeo.MapGuide;
using DataType = Autodesk.Gis.Map.Constants.DataType;
using OdOpenMode = Autodesk.Gis.Map.Constants.OpenMode;
using OdTable = Autodesk.Gis.Map.ObjectData.Table;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Insertion dans le dessin du cadastre téléchargé par le serveur MCP (APICARTO de l'IGN) : parcelles et
/// feuilles de sections en polylignes fermées, étiquettes, données d'objet Map 3D. Les coordonnées arrivent en
/// longitude/latitude (WGS84) et sont projetées dans le système du dessin. Tout l'import tient en une étape d'annulation.
/// </summary>
internal static class CadastreTools
{
    private const int MaxListedParcels = 100;

    /// <summary>0 désigne le dessin courant dans les appels de l'API OD.</summary>
    private const uint CurrentDrawing = 0;

    private static readonly OdField[] ParcelFields =
    [
        new("ID", "Identifiant de la parcelle (IDU)", DataType.Character),
        new("COMMUNE", "Commune", DataType.Character),
        new("CODE_INSEE", "Code INSEE", DataType.Character),
        new("SECTION", "Section", DataType.Character),
        new("NUMERO", "Numéro", DataType.Character),
        new("CONTENANCE", "Contenance cadastrale en m²", DataType.Real),
    ];

    private static readonly OdField[] SectionFields =
    [
        new("ID", "Identifiant de la feuille", DataType.Character),
        new("COMMUNE", "Commune", DataType.Character),
        new("CODE_INSEE", "Code INSEE", DataType.Character),
        new("SECTION", "Section", DataType.Character),
        new("FEUILLE", "Numéro de feuille", DataType.Integer),
    ];

    /// <summary>Champs numériques à 0 quand la valeur est inconnue (toujours pour le bâti cadastral, sauf LEGER).</summary>
    private static readonly OdField[] BuildingFields =
    [
        new("ID", "Identifiant du bâtiment (cleabs BD TOPO, ou PCI-gid)", DataType.Character),
        new("SOURCE", "Source : cadastre ou bdtopo", DataType.Character),
        new("TYPE", "Type (cadastre) ou nature (BD TOPO)", DataType.Character),
        new("LEGER", "Construction légère : 1, sinon 0", DataType.Integer),
        new("USAGE", "Usage (BD TOPO)", DataType.Character),
        new("HAUTEUR", "Hauteur en m (BD TOPO, 0 si inconnue)", DataType.Real),
        new("ETAGES", "Nombre d'étages (BD TOPO, 0 si inconnu)", DataType.Integer),
        new("LOGEMENTS", "Nombre de logements (BD TOPO, 0 si inconnu)", DataType.Integer),
        new("ALT_SOL", "Altitude minimale du sol en m NGF (BD TOPO)", DataType.Real),
        new("ALT_TOIT", "Altitude maximale du toit en m NGF (BD TOPO)", DataType.Real),
        new("RNB", "Identifiant(s) RNB (BD TOPO)", DataType.Character),
        new("CODE_INSEE", "Code INSEE (cadastre)", DataType.Character),
    ];

    public static object ImportCadastreGeometry(ToolContext context, JsonElement? args)
    {
        if (MapInfo.IsAvailable)
            return ImportCadastreGeometryMap3D(context, args);

        return Import(context, new ArgReader(args), isMap3D: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object ImportCadastreGeometryMap3D(ToolContext context, JsonElement? args)
    {
        try
        {
            return Import(context, new ArgReader(args), isMap3D: true);
        }
        catch (MapException ex)
        {
            throw MapTools.Translate(ex, "import du cadastre");
        }
    }

    private static object Import(ToolContext context, ArgReader reader, bool isMap3D)
    {
        var database = context.Database;
        var transaction = context.RequireTransaction();
        var parcels = ReadFeatures(reader, "parcels");
        var sections = ReadFeatures(reader, "sections");
        var buildings = ReadBuildings(reader);
        if (parcels.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucune parcelle à importer.");

        var prefix = reader.GetString("layerPrefix", "CADASTRE")!.Trim();
        var parcelsLayer = $"{prefix}_PARCELLES";
        var numbersLayer = $"{prefix}_NUMEROS";
        var sectionsLayer = $"{prefix}_SECTIONS";
        var hardBuildingsLayer = $"{prefix}_BATI_DUR";
        var lightBuildingsLayer = $"{prefix}_BATI_LEGER";

        // Tables de données d'objet ou RegApp XData nommées comme le préfixe : CADASTRE, CADASTRE_SECTIONS et
        // CADASTRE_BATIMENTS par défaut.
        var parcelTableName = prefix;
        var sectionTableName = $"{prefix}_SECTIONS";
        var buildingTableName = $"{prefix}_BATIMENTS";
        try
        {
            SymbolUtilityServices.ValidateSymbolName(sectionsLayer, allowVerticalBar: false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {prefix} » ne peut pas servir de préfixe de nom de calque.");
        }

        var createLabels = reader.GetBool("createLabels", true);
        var labelHeight = reader.GetDouble("labelHeight", 2.0);
        if (labelHeight <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« labelHeight » doit être strictement positif.");

        var warnings = new List<string>();

        // Backend cartographique : CS-MAP et OD tables en Map 3D, projection autonome Lambert-93 et XData en AutoCAD classique.
        using var backend = CreateBackend(context, reader, warnings, isMap3D);
        backend.ValidateDomainAndAssign(database, parcels[0].Polygons[0][0][0]);

        EnsureLayer(database, transaction, parcelsLayer, 30, LineWeight.LineWeight025, warnings);
        if (createLabels)
            EnsureLayer(database, transaction, numbersLayer, 2, LineWeight.ByLayer, warnings);
        if (sections.Count > 0)
            EnsureLayer(database, transaction, sectionsLayer, 1, LineWeight.LineWeight050, warnings);
        if (buildings.Any(building => !building.Light))
            EnsureLayer(database, transaction, hardBuildingsLayer, 8, LineWeight.LineWeight018, warnings);
        if (buildings.Any(building => building.Light))
            EnsureLayer(database, transaction, lightBuildingsLayer, 33, LineWeight.LineWeight013, warnings);

        IAttributeBinding? parcelTable = null, sectionTable = null, buildingTable = null;
        if (reader.GetBool("attachObjectData", true))
        {
            parcelTable = backend.CreateParcelBinding(database, transaction, parcelTableName);
            if (sections.Count > 0)
                sectionTable = backend.CreateSectionBinding(database, transaction, sectionTableName);
            if (buildings.Count > 0)
                buildingTable = backend.CreateBuildingBinding(database, transaction, buildingTableName);
        }

        var skipExisting = reader.GetBool("skipExisting", true);
        var existingParcels = skipExisting && parcelTable is not null ? parcelTable.ExistingIds(transaction, database, parcelsLayer) : [];
        var existingSections = skipExisting && sectionTable is not null ? sectionTable.ExistingIds(transaction, database, sectionsLayer) : [];
        var existingBuildings = skipExisting && buildingTable is not null
            ? buildingTable.ExistingIds(transaction, database, hardBuildingsLayer)
                .Union(buildingTable.ExistingIds(transaction, database, lightBuildingsLayer)).ToHashSet(StringComparer.Ordinal)
            : [];

        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        var writer = new Writer(database, transaction, modelSpace, backend.Project);

        // Attributs rattachés une fois la géométrie validée : sur des objets créés dans la même transaction,
        // les OD empêchent AutoCAD de dessiner les polylignes, visibles seulement après un REGEN.
        var pendingData = new List<PendingRecord>();

        // Bâtiments d'abord : l'ordre de création fait l'ordre de tracé, et les limites de parcelles, souvent confondues
        // avec les murs, doivent rester visibles par-dessus.
        // En 3D, chaque bâtiment est posé à l'altitude de son sol et extrudé de sa hauteur (épaisseur de polyligne).
        var buildings3d = reader.GetBool("buildings3d", false);
        int importedBuildings = 0, skippedBuildings = 0, flatBuildings = 0;
        foreach (var building in buildings)
        {
            if (existingBuildings.Contains(building.Id))
            {
                skippedBuildings++;
                continue;
            }

            double elevation = 0, thickness = 0;
            if (buildings3d)
            {
                if (building is { Height: > 0, GroundAltitude: { } ground })
                    (elevation, thickness) = (ground, building.Height.Value);
                else
                    flatBuildings++;
            }

            var shape = writer.AddShape(building.Polygons, building.Light ? lightBuildingsLayer : hardBuildingsLayer, elevation, thickness);
            if (shape.IsEmpty)
                continue;

            importedBuildings++;
            if (buildingTable is not null)
                pendingData.Add(new PendingRecord(buildingTable, [.. shape.Ids],
                [
                    building.Id, building.Source, building.Type, building.Light ? 1 : 0, building.Usage,
                    building.Height ?? 0.0, building.Floors ?? 0, building.Dwellings ?? 0,
                    building.GroundAltitude ?? 0.0, building.RoofAltitude ?? 0.0, building.Rnb, building.CodeInsee,
                ]));
        }

        if (flatBuildings > 0)
            warnings.Add($"{flatBuildings} bâtiment(s) sans hauteur ou altitude du sol connue, dessinés à plat.");

        // Les numéros seuls suffisent quand les sections sont dessinées et étiquetées ; sinon « section numéro ».
        var numberOnly = sections.Count > 0;
        var created = new List<object>();
        int importedParcels = 0, skippedParcels = 0;
        foreach (var parcel in parcels)
        {
            if (existingParcels.Contains(parcel.Id))
            {
                skippedParcels++;
                continue;
            }

            var shape = writer.AddShape(parcel.Polygons, parcelsLayer);
            if (shape.IsEmpty)
                continue;

            importedParcels++;
            if (parcelTable is not null)
                pendingData.Add(new PendingRecord(parcelTable, [.. shape.Ids],
                    [parcel.Id, parcel.Commune, parcel.CodeInsee, parcel.Section, parcel.Numero, parcel.Contenance]));

            var number = parcel.Numero?.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : parcel.Numero ?? "";
            var label = numberOnly ? number : $"{parcel.Section} {number}".Trim();
            if (createLabels && label.Length > 0)
                writer.AddLabel(shape, label, labelHeight, numbersLayer);

            if (created.Count < MaxListedParcels)
                created.Add(new { parcel.Id, Label = $"{parcel.Section} {number}".Trim(), Handle = shape.FirstId.Handle.ToString() });
        }

        int importedSections = 0, skippedSections = 0;
        foreach (var section in sections)
        {
            if (existingSections.Contains(section.Id))
            {
                skippedSections++;
                continue;
            }

            var shape = writer.AddShape(section.Polygons, sectionsLayer);
            if (shape.IsEmpty)
                continue;

            importedSections++;
            if (sectionTable is not null)
                pendingData.Add(new PendingRecord(sectionTable, [.. shape.Ids],
                    [section.Id, section.Commune, section.CodeInsee, section.Section, section.Feuille ?? 0]));
            if (createLabels && !string.IsNullOrEmpty(section.Section))
                writer.AddLabel(shape, section.Section, labelHeight * 3, sectionsLayer);
        }


        if (writer.RejectedPoints > 0)
            warnings.Add($"{writer.RejectedPoints} sommet(s) n'ont pas pu être convertis dans « {backend.CoordinateSystemCode} » et ont été ignorés.");

        var extents = writer.Extents;
        var zoom = reader.GetBool("zoomExtents", true) ? extents : null;
        context.AfterCommit(() =>
        {
            AttachRecords(database, pendingData);
            if (zoom is { } area)
                ZoomTo(context, area);
        });

        return new
        {
            ImportedParcels = importedParcels,
            SkippedExistingParcels = skippedParcels,
            ImportedSections = importedSections,
            SkippedExistingSections = skippedSections,
            ImportedBuildings = buildings.Count > 0 ? importedBuildings : (int?)null,
            SkippedExistingBuildings = buildings.Count > 0 ? skippedBuildings : (int?)null,
            BuildingSource = buildings.Count > 0 ? reader.GetString("buildingSource") : null,
            Buildings3d = buildings.Count > 0 && buildings3d ? true : (bool?)null,
            Communes = parcels.Select(p => p.Commune).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToArray(),
            CoordinateSystem = backend.CoordinateSystemCode,
            CoordinateSystemAssigned = backend.CoordinateSystemAssigned,
            Layers = new[]
                {
                    parcelsLayer, createLabels ? numbersLayer : null, sections.Count > 0 ? sectionsLayer : null,
                    buildings.Any(b => !b.Light) ? hardBuildingsLayer : null, buildings.Any(b => b.Light) ? lightBuildingsLayer : null,
                }
                .Where(name => name is not null).ToArray(),
            ObjectData = backend.DescribeObjectData(parcelTable?.Name, sectionTable?.Name, buildingTable?.Name),
            Extents = extents is null ? null : new { Min = Format.Point(extents.Value.MinPoint), Max = Format.Point(extents.Value.MaxPoint) },
            Parcels = created,
            ParcelListTruncated = importedParcels > created.Count ? true : (bool?)null,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static ICadastreBackend CreateBackend(ToolContext context, ArgReader reader, List<string> warnings, bool isMap3D)
    {
        if (isMap3D)
            return CreateMap3DBackend(context, reader, warnings);
        return new ClassicCadastreBackend(reader, warnings);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ICadastreBackend CreateMap3DBackend(ToolContext context, ArgReader reader, List<string> warnings) =>
        new Map3DCadastreBackend(context, reader, warnings);

    private static void EnsureLayer(Database database, Transaction transaction, string name, short colorIndex, LineWeight lineWeight, List<string> warnings)
    {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name))
        {
            var existing = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForRead);
            if (existing.IsOff || existing.IsFrozen)
                warnings.Add($"Le calque « {name} » est {(existing.IsFrozen ? "gelé" : "désactivé")} : les objets importés ne sont pas visibles.");
            return;
        }

        layers.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = name,
            Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
            LineWeight = lineWeight,
            LinetypeObjectId = database.ContinuousLinetype,
        };
        layers.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>Enregistrement d'attributs (OD ou XData) à rattacher une fois la géométrie validée.</summary>
    private sealed record PendingRecord(IAttributeBinding Table, List<ObjectId> Ids, object?[] Values);

    /// <summary>
    /// Second temps de l'import, dans la même commande donc la même étape d'annulation.
    /// </summary>
    private static void AttachRecords(Database database, List<PendingRecord> pending)
    {
        if (pending.Count == 0)
            return;

        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var item in pending)
            item.Table.Attach(transaction, item.Ids, item.Values);
        transaction.Commit();
    }

    private interface IAttributeBinding
    {
        string Name { get; }
        HashSet<string> ExistingIds(Transaction transaction, Database database, string layer);
        void Attach(Transaction transaction, IEnumerable<ObjectId> ids, object?[] values);
    }

    private interface ICadastreBackend : IDisposable
    {
        string CoordinateSystemCode { get; }
        bool CoordinateSystemAssigned { get; }
        void ValidateDomainAndAssign(Database database, (double Lon, double Lat) sample);
        (double X, double Y)? Project(double lon, double lat);
        IAttributeBinding? CreateParcelBinding(Database database, Transaction transaction, string tableName);
        IAttributeBinding? CreateSectionBinding(Database database, Transaction transaction, string tableName);
        IAttributeBinding? CreateBuildingBinding(Database database, Transaction transaction, string tableName);
        object? DescribeObjectData(string? parcelTable, string? sectionTable, string? buildingTable);
    }

    private sealed class Map3DCadastreBackend : ICadastreBackend
    {
        private readonly MgCoordinateSystem _system;
        public string CoordinateSystemCode { get; }
        public bool CoordinateSystemAssigned { get; }

        public Map3DCadastreBackend(ToolContext context, ArgReader reader, List<string> warnings)
        {
            var (code, assign) = ChooseCoordinateSystem(context, reader, warnings);
            CoordinateSystemCode = code;
            CoordinateSystemAssigned = assign;
            _system = new MgCoordinateSystemFactory().CreateFromCode(code);
        }

        public void ValidateDomainAndAssign(Database database, (double Lon, double Lat) sample)
        {
            if (!CoordinateSystemTools.IsInDomain(_system, sample.Lon, sample.Lat))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"La zone importée (longitude {sample.Lon:0.####}°, latitude {sample.Lat:0.####}°) est hors du domaine du système « {CoordinateSystemCode} » " +
                    "du dessin : les parcelles tomberaient à des coordonnées absurdes. Importez dans un dessin vierge ou changez " +
                    "de système avec set_coordinate_system.");

            if (CoordinateSystemAssigned)
                AcMapCoordsysCore.SetCoordinateSystem(CoordinateSystemCode, database);
        }

        public (double X, double Y)? Project(double lon, double lat) =>
            CoordinateSystemTools.FromLonLat(_system, lon, lat);

        public IAttributeBinding? CreateParcelBinding(Database database, Transaction transaction, string tableName)
        {
            var tables = MapInfo.RequireProject(database).ODTables;
            return new OdAttributeBinding(OdBinding.Create(tables, tableName, "Parcelles cadastrales (APICARTO, IGN)", ParcelFields));
        }

        public IAttributeBinding? CreateSectionBinding(Database database, Transaction transaction, string tableName)
        {
            var tables = MapInfo.RequireProject(database).ODTables;
            return new OdAttributeBinding(OdBinding.Create(tables, tableName, "Feuilles cadastrales (APICARTO, IGN)", SectionFields));
        }

        public IAttributeBinding? CreateBuildingBinding(Database database, Transaction transaction, string tableName)
        {
            var tables = MapInfo.RequireProject(database).ODTables;
            return new OdAttributeBinding(OdBinding.Create(tables, tableName, "Bâtiments (IGN, Géoplateforme)", BuildingFields));
        }

        public object? DescribeObjectData(string? parcelTable, string? sectionTable, string? buildingTable) =>
            parcelTable is null ? null : new { Parcels = parcelTable, Sections = sectionTable, Buildings = buildingTable };

        public void Dispose() => _system.Dispose();

        private static (string Code, bool Assign) ChooseCoordinateSystem(ToolContext context, ArgReader reader, List<string> warnings)
        {
            var database = context.Database;
            var requested = reader.GetString("coordinateSystem")?.Trim();
            var current = AcMapCoordsysCore.GetCoordinateSystem(database);
            if (!string.IsNullOrEmpty(current))
            {
                if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, current, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Le dessin est déjà en « {current} » : « {requested} » a été ignoré (set_coordinate_system change le système).");
                return (current, false);
            }

            var epsg = reader.GetInt("epsg", 2154);
            var code = !string.IsNullOrEmpty(requested) ? requested
                : epsg == 2154 ? "Lambert93"
                : CoordinateSystemTools.CodeForEpsg(epsg)
                    ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Aucun système du catalogue ne correspond à l'EPSG {epsg} : indiquez coordinateSystem.");

            if (!CoordinateSystemTools.Exists(code))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Système de coordonnées « {code} » inconnu (voir search_coordinate_systems).");

            if (database.Extmin.X <= database.Extmax.X)
                warnings.Add($"Le dessin contenait déjà des objets sans système de coordonnées : « {code} » lui a été attribué, " +
                             "vérifiez que ces objets sont bien exprimés dans ce système.");
            return (code, true);
        }
    }

    private sealed class OdAttributeBinding(OdBinding binding) : IAttributeBinding
    {
        public string Name => binding.Name;

        public HashSet<string> ExistingIds(Transaction transaction, Database database, string layer) =>
            binding.ExistingIds(transaction, database, layer);

        public void Attach(Transaction transaction, IEnumerable<ObjectId> ids, object?[] values)
        {
            try
            {
                binding.Attach(ids, values);
            }
            catch (MapException ex)
            {
                var reason = MapTools.Translate(ex, "rattachement des données d'objet").Message;
                throw new PipeException(PipeErrorCodes.MapUnavailable,
                    $"Les parcelles sont dessinées, mais leurs données d'objet n'ont pas été rattachées ({reason}). " +
                    "La commande U annule l'import entier.");
            }
        }
    }

    private sealed class ClassicCadastreBackend : ICadastreBackend
    {
        public string CoordinateSystemCode => "Lambert93";
        public bool CoordinateSystemAssigned => false;

        public ClassicCadastreBackend(ArgReader reader, List<string> warnings)
        {
            var requested = reader.GetString("coordinateSystem")?.Trim();
            if (!string.IsNullOrEmpty(requested) && !string.Equals(requested, "Lambert93", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"AutoCAD classique : le système « {requested} » a été ignoré car le catalogue CS-MAP nécessite Map 3D. " +
                             "La projection autonome Lambert-93 (EPSG:2154) est utilisée.");
            }
            else
            {
                warnings.Add("AutoCAD classique détecté : projection Lambert-93 calculée directement en C# (IGN NTG_71).");
            }
        }

        public void ValidateDomainAndAssign(Database database, (double Lon, double Lat) sample)
        {
            // France métropolitaine : longitude -6° à 11°, latitude 41° à 52°
            if (sample.Lon < -6.0 || sample.Lon > 11.0 || sample.Lat < 41.0 || sample.Lat > 52.0)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"La zone importée (longitude {sample.Lon:0.####}°, latitude {sample.Lat:0.####}°) se situe hors de France métropolitaine. " +
                    "En version AutoCAD classique, seul l'algorithme Lambert-93 (France métropolitaine) est intégré sans Map 3D.");
            }
        }

        public (double X, double Y)? Project(double lon, double lat)
        {
            if (double.IsNaN(lon) || double.IsNaN(lat))
                return null;
            var (x, y) = Lambert93.ToLambert93(lon, lat);
            if (double.IsNaN(x) || double.IsNaN(y))
                return null;
            return (x, y);
        }

        public IAttributeBinding? CreateParcelBinding(Database database, Transaction transaction, string tableName) =>
            new XDataAttributeBinding(database, transaction, tableName, ["ID", "COMMUNE", "CODE_INSEE", "SECTION", "NUMERO", "CONTENANCE"]);

        public IAttributeBinding? CreateSectionBinding(Database database, Transaction transaction, string tableName) =>
            new XDataAttributeBinding(database, transaction, tableName, ["ID", "COMMUNE", "CODE_INSEE", "SECTION", "FEUILLE"]);

        public IAttributeBinding? CreateBuildingBinding(Database database, Transaction transaction, string tableName) =>
            new XDataAttributeBinding(database, transaction, tableName,
                ["ID", "SOURCE", "TYPE", "LEGER", "USAGE", "HAUTEUR", "ETAGES", "LOGEMENTS", "ALT_SOL", "ALT_TOIT", "RNB", "CODE_INSEE"]);

        public object? DescribeObjectData(string? parcelTable, string? sectionTable, string? buildingTable) =>
            parcelTable is null ? null : new
            {
                Parcels = $"{parcelTable} (XData)",
                Sections = sectionTable is null ? null : $"{sectionTable} (XData)",
                Buildings = buildingTable is null ? null : $"{buildingTable} (XData)",
            };

        public void Dispose() { }
    }

    private sealed class XDataAttributeBinding : IAttributeBinding
    {
        private readonly string _appName;
        private readonly string[] _fields;

        public string Name => _appName;

        public XDataAttributeBinding(Database database, Transaction transaction, string appName, string[] fields)
        {
            _appName = appName;
            _fields = fields;
            EnsureRegApp(database, transaction, appName);
        }

        private static void EnsureRegApp(Database database, Transaction transaction, string appName)
        {
            var regTable = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (!regTable.Has(appName))
            {
                regTable.UpgradeOpen();
                var regApp = new RegAppTableRecord { Name = appName };
                regTable.Add(regApp);
                transaction.AddNewlyCreatedDBObject(regApp, true);
            }
        }

        public HashSet<string> ExistingIds(Transaction transaction, Database database, string layer)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layer))
                return ids;

            var layerId = layers[layer];
            var polylineClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Polyline));
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (var id in modelSpace)
            {
                if (!id.ObjectClass.IsDerivedFrom(polylineClass))
                    continue;

                var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                if (entity.LayerId != layerId)
                    continue;

                using var rb = entity.GetXDataForApplication(_appName);
                if (rb is null)
                    continue;

                var enumerator = rb.GetEnumerator();
                if (enumerator.MoveNext()) // DxfCode.ExtendedDataRegAppName
                {
                    if (enumerator.MoveNext() && enumerator.Current.Value is string firstVal && !string.IsNullOrEmpty(firstVal))
                        ids.Add(firstVal);
                }
            }

            return ids;
        }

        public void Attach(Transaction transaction, IEnumerable<ObjectId> ids, object?[] values)
        {
            var bufferList = new List<TypedValue>
            {
                new((int)DxfCode.ExtendedDataRegAppName, _appName)
            };
            for (var i = 0; i < _fields.Length && i < values.Length; i++)
            {
                var val = values[i];
                switch (val)
                {
                    case double d:
                        bufferList.Add(new TypedValue((int)DxfCode.ExtendedDataReal, d));
                        break;
                    case int n:
                        bufferList.Add(new TypedValue((int)DxfCode.ExtendedDataInteger32, n));
                        break;
                    default:
                        bufferList.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, val?.ToString() ?? ""));
                        break;
                }
            }

            foreach (var id in ids)
            {
                var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
                using var rb = new ResultBuffer([.. bufferList]);
                entity.XData = rb;
            }
        }
    }

    /// <summary>Cadrage sur les objets importés ; un échec n'est qu'une gêne d'affichage, journalisée.</summary>
    private static void ZoomTo(ToolContext context, Extents3d extents)
    {
        try
        {
            ZoomToCore(context, extents);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            Log.Error("Cadrage après l'import du cadastre", ex);
        }
    }

    private static void ZoomToCore(ToolContext context, Extents3d extents)
    {
        var editor = context.Editor;
        using var view = editor.GetCurrentView();
        var width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 1.0) * 1.15;
        var height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 1.0) * 1.15;
        var ratio = view.Width / view.Height;
        if (width / height > ratio)
            height = width / ratio;
        else
            width = height * ratio;

        // Vue de dessus ciblée sur le centre de la zone : un changement de direction ensuite (set_view en isométrique,
        // pour voir les bâtiments en 3D) pivote autour de la zone importée, pas autour de l'origine du dessin.
        view.ViewDirection = Vector3d.ZAxis;
        view.Target = new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2, 0);
        view.ViewTwist = 0;
        view.CenterPoint = Point2d.Origin;
        view.Width = width;
        view.Height = height;
        editor.SetCurrentView(view);
    }

    // ----- Données reçues -----

    /// <summary>Parcelle ou feuille de section : polygones (anneau extérieur puis trous) en longitude/latitude.</summary>
    private sealed record Feature(
        string Id, string? Commune, string? CodeInsee, string? Section, string? Numero, double Contenance, int? Feuille,
        List<List<List<(double Lon, double Lat)>>> Polygons);

    private static List<Feature> ReadFeatures(ArgReader reader, string name) =>
        ReadPolygonItems(reader, name, (item, polygons) => new Feature(
            Text(item, "id") ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Élément de « {name} » sans « id »."),
            Text(item, "commune"), Text(item, "codeInsee"), Text(item, "section"), Text(item, "numero"),
            Number(item, "contenance") ?? 0,
            Number(item, "feuille") is { } sheet ? (int)sheet : null,
            polygons));

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    /// <summary>Bâtiment reçu du serveur ; hauteur et altitudes en mètres, null si inconnues.</summary>
    private sealed record Building(
        string Id, string Source, string? Type, bool Light, string? Usage, double? Height, int? Floors, int? Dwellings,
        double? GroundAltitude, double? RoofAltitude, string? Rnb, string? CodeInsee,
        List<List<List<(double Lon, double Lat)>>> Polygons);

    private static List<Building> ReadBuildings(ArgReader reader) =>
        ReadPolygonItems(reader, "buildings", (item, polygons) => new Building(
            Text(item, "id") ?? throw new PipeException(PipeErrorCodes.InvalidParams, "Bâtiment sans « id »."),
            Text(item, "source") ?? "", Text(item, "type"),
            item.TryGetProperty("light", out var light) && light.ValueKind == JsonValueKind.True,
            Text(item, "usage"), Number(item, "height"),
            Number(item, "floors") is { } floors ? (int)floors : null,
            Number(item, "dwellings") is { } dwellings ? (int)dwellings : null,
            Number(item, "groundAltitude"), Number(item, "roofAltitude"), Text(item, "rnb"), Text(item, "codeInsee"),
            polygons));

    /// <summary>Éléments d'un tableau qui portent des « polygons » ; ceux sans géométrie exploitable sont ignorés.</summary>
    private static List<T> ReadPolygonItems<T>(ArgReader reader, string name, Func<JsonElement, List<List<List<(double, double)>>>, T> create)
    {
        var items = new List<T>();
        if (!reader.TryGet(name, out var array))
            return items;
        if (array.ValueKind != JsonValueKind.Array)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » attend un tableau.");

        foreach (var item in array.EnumerateArray())
        {
            var polygons = ReadPolygons(item);
            if (polygons.Count > 0)
                items.Add(create(item, polygons));
        }

        return items;
    }

    private static List<List<List<(double, double)>>> ReadPolygons(JsonElement item)
    {
        var polygons = new List<List<List<(double, double)>>>();
        if (!item.TryGetProperty("polygons", out var polygonsElement) || polygonsElement.ValueKind != JsonValueKind.Array)
            return polygons;

        foreach (var polygonElement in polygonsElement.EnumerateArray())
        {
            var rings = polygonElement.EnumerateArray()
                .Select(ring => ring.EnumerateArray().Select(point => (point[0].GetDouble(), point[1].GetDouble())).ToList())
                .Where(ring => ring.Count >= 3)
                .ToList();
            if (rings.Count > 0)
                polygons.Add(rings);
        }

        return polygons;
    }

    // ----- Création des objets -----

    /// <summary>Contour projeté : polyligne créée et ses sommets.</summary>
    private sealed record Outline(ObjectId Id, List<Point2d> Points);

    /// <summary>Objet dessiné : par polygone, le contour extérieur puis les trous.</summary>
    private sealed record Shape(List<List<Outline>> Polygons)
    {
        public bool IsEmpty => Polygons.Count == 0;

        public ObjectId FirstId => Polygons[0][0].Id;

        public IEnumerable<ObjectId> Ids => Polygons.SelectMany(polygon => polygon).Select(outline => outline.Id);
    }

    private sealed class Writer(Database database, Transaction transaction, BlockTableRecord space, Func<double, double, (double X, double Y)?> projectPoint)
    {
        public int RejectedPoints { get; private set; }

        public Extents3d? Extents { get; private set; }

        /// <summary>
        /// Une polyligne fermée par anneau ; le sommet de fermeture répété par le GeoJSON est retiré. Élévation et
        /// épaisseur (extrusion selon Z) servent aux bâtiments en 3D.
        /// </summary>
        public Shape AddShape(List<List<List<(double Lon, double Lat)>>> polygons, string layer, double elevation = 0, double thickness = 0)
        {
            var result = new List<List<Outline>>();
            foreach (var polygon in polygons)
            {
                var outlines = new List<Outline>();
                for (var ringIndex = 0; ringIndex < polygon.Count; ringIndex++)
                {
                    var points = Project(polygon[ringIndex]);
                    if (points.Count < 3)
                    {
                        if (ringIndex == 0)
                            break; // Sans contour extérieur, les trous n'ont pas de sens.
                        continue;
                    }

                    var polyline = new Polyline(points.Count) { Closed = true, Layer = layer, Elevation = elevation, Thickness = thickness };
                    for (var index = 0; index < points.Count; index++)
                        polyline.AddVertexAt(index, points[index], 0, 0, 0);

                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    Grow(polyline.GeometricExtents);
                    outlines.Add(new Outline(polyline.ObjectId, points));
                }

                if (outlines.Count > 0)
                    result.Add(outlines);
            }

            return new Shape(result);
        }

        /// <summary>Étiquette centrée dans le plus grand polygone, à l'intérieur même s'il est concave ou troué.</summary>
        public void AddLabel(Shape shape, string label, double height, string layer)
        {
            var largest = shape.Polygons.MaxBy(polygon => Math.Abs(PolygonGeometry.Area(polygon[0].Points))
                - polygon.Skip(1).Sum(hole => Math.Abs(PolygonGeometry.Area(hole.Points))))!;
            var point = PolygonGeometry.LabelPoint(largest[0].Points, largest.Skip(1).Select(hole => hole.Points).ToList());
            var position = new Point3d(point.X, point.Y, 0);

            var text = new DBText
            {
                TextString = label,
                Height = height,
                Layer = layer,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                Position = position,
                AlignmentPoint = position,
            };
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            text.AdjustAlignment(database);
        }

        private List<Point2d> Project(List<(double Lon, double Lat)> ring)
        {
            var count = ring.Count;
            if (count > 1 && ring[0] == ring[count - 1])
                count--;

            var points = new List<Point2d>(count);
            for (var index = 0; index < count; index++)
            {
                if (projectPoint(ring[index].Lon, ring[index].Lat) is { } projected)
                    points.Add(new Point2d(projected.X, projected.Y));
                else
                    RejectedPoints++;
            }

            return points;
        }

        private void Grow(Extents3d extents)
        {
            if (Extents is { } current)
            {
                current.AddExtents(extents);
                Extents = current;
            }
            else
            {
                Extents = extents;
            }
        }
    }

    // ----- Données d'objet -----

    private sealed record OdField(string Name, string Description, DataType Type);

    /// <summary>Table de données d'objet et position de chaque champ attendu (la table peut exister avec d'autres champs en plus).</summary>
    private sealed class OdBinding(OdTable table, int[] indexes)
    {
        public string Name => table.Name;

        public static OdBinding Create(Tables tables, string name, string description, OdField[] fields)
        {
            if (!tables.IsTableDefined(name))
            {
                var definitions = FieldDefinitions.Create();
                for (var index = 0; index < fields.Length; index++)
                    definitions.Add(fields[index].Name, fields[index].Description, fields[index].Type, index);

                tables.Add(name, definitions, description, false);
                return new OdBinding(tables[name], [.. Enumerable.Range(0, fields.Length)]);
            }

            var table = tables[name];
            var existing = table.FieldDefinitions;
            var indexes = new int[fields.Length];
            var problems = new List<string>();
            for (var field = 0; field < fields.Length; field++)
            {
                indexes[field] = -1;
                for (var index = 0; index < existing.Count; index++)
                {
                    if (string.Equals(existing[index].Name, fields[field].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        indexes[field] = index;
                        if (existing[index].Type != fields[field].Type)
                            problems.Add($"{fields[field].Name} ({existing[index].Type} au lieu de {fields[field].Type})");
                        break;
                    }
                }

                if (indexes[field] < 0)
                    problems.Add($"{fields[field].Name} absent");
            }

            if (problems.Count > 0)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"La table de données d'objet « {name} » existe déjà avec une autre structure ({string.Join(", ", problems)}). " +
                    "Renommez-la ou supprimez-la, ou importez avec attachObjectData=false.");

            return new OdBinding(table, indexes);
        }

        /// <summary>Même enregistrement sur chaque contour de l'objet (parcelle en plusieurs morceaux, trous).</summary>
        public void Attach(IEnumerable<ObjectId> ids, params object?[] values)
        {
            foreach (var id in ids)
            {
                using var record = Record.Create();
                table.InitRecord(record);
                for (var field = 0; field < values.Length; field++)
                {
                    var target = record[indexes[field]];
                    switch (values[field])
                    {
                        case double number:
                            target.Assign(number);
                            break;
                        case int integer:
                            target.Assign(integer);
                            break;
                        case var text:
                            target.Assign((string?)text ?? "");
                            break;
                    }
                }

                table.AddRecord(record, id);
            }
        }

        /// <summary>Identifiants déjà présents : champ ID des enregistrements de cette table sur les polylignes du calque.</summary>
        public HashSet<string> ExistingIds(Transaction transaction, Database database, string layer)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layer))
                return ids;

            var layerId = layers[layer];
            var polylineClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Polyline));
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (var id in modelSpace)
            {
                if (!id.ObjectClass.IsDerivedFrom(polylineClass) || ((Entity)transaction.GetObject(id, OpenMode.ForRead)).LayerId != layerId)
                    continue;

                using var records = table.GetObjectTableRecords(CurrentDrawing, id, OdOpenMode.OpenForRead, false);
                // L'énumérateur de l'API Map lève une exception si la collection est vide.
                foreach (Record record in records.Count > 0 ? records.Cast<Record>() : [])
                    ids.Add(record[indexes[0]].StrValue);
            }

            return ids;
        }
    }
}

/// <summary>Géométrie plane des polygones : aire, point d'étiquette intérieur.</summary>
internal static class PolygonGeometry
{
    public static double Area(IReadOnlyList<Point2d> ring)
    {
        var sum = 0.0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return sum / 2;
    }

    /// <summary>
    /// Centre de gravité s'il est dans la surface (trous exclus) ; sinon, milieu du plus long segment intérieur
    /// parmi quelques horizontales réparties sur la hauteur du polygone.
    /// </summary>
    public static Point2d LabelPoint(IReadOnlyList<Point2d> outer, IReadOnlyList<List<Point2d>> holes)
    {
        var centroid = Centroid(outer);
        if (Inside(centroid, outer) && !holes.Any(hole => Inside(centroid, hole)))
            return centroid;

        var minY = outer.Min(p => p.Y);
        var maxY = outer.Max(p => p.Y);
        var rings = holes.Prepend(outer).ToList();
        Point2d? best = null;
        var bestWidth = 0.0;
        foreach (var y in Enumerable.Range(1, 15).Select(k => minY + (maxY - minY) * k / 16).Prepend(centroid.Y))
        {
            var crossings = new List<double>();
            foreach (var ring in rings)
            {
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    var (a, b) = (ring[j], ring[i]);
                    if ((a.Y > y) != (b.Y > y))
                        crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }

            crossings.Sort();
            for (var k = 0; k + 1 < crossings.Count; k += 2)
            {
                var width = crossings[k + 1] - crossings[k];
                if (width > bestWidth)
                {
                    bestWidth = width;
                    best = new Point2d((crossings[k] + crossings[k + 1]) / 2, y);
                }
            }
        }

        return best ?? centroid;
    }

    private static Point2d Centroid(IReadOnlyList<Point2d> ring)
    {
        double area = 0, x = 0, y = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var cross = ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
            area += cross;
            x += (ring[j].X + ring[i].X) * cross;
            y += (ring[j].Y + ring[i].Y) * cross;
        }

        if (Math.Abs(area) < 1e-12)
            return new Point2d(ring.Average(p => p.X), ring.Average(p => p.Y));

        return new Point2d(x / (3 * area), y / (3 * area));
    }

    private static bool Inside(Point2d point, IReadOnlyList<Point2d> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i].Y > point.Y) != (ring[j].Y > point.Y) &&
                point.X < (ring[j].X - ring[i].X) * (point.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        }

        return inside;
    }
}

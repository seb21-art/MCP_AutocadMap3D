using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map.Platform;
using Autodesk.Gis.Map.Platform.Interop;
using McpMap3D.Shared;
using OSGeo.MapGuide;
using AcMapCoordsysCore = Autodesk.Gis.Map.CoordinateSystem.AcMapCoordsysCore;
using AcSelection = Autodesk.AutoCAD.EditorInput.SelectionSet;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Connexions FDO de la carte courante : fournisseurs, sources du dépôt (Library://MCP_…), calques et sélection.
/// Map 3D n'expose pas de session : les sources créées ici vivent dans ce dossier du dépôt, pas dans le dessin.
/// Les écritures ne modifient pas les objets de la source et ne sont pas annulées par U.
/// </summary>
internal static class FdoTools
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    // Sources et calques créés ici : à la racine de Library://, comme ceux de MAPCONNECT (qui ne montre que la
    // racine), avec un préfixe qui les distingue des connexions faites dans Map 3D. Les versions 0.23 rangeaient les
    // sources dans Library://MCPMap3D/ : elles restent reconnues.
    private const string OwnedPrefix = "Library://MCP_";
    private const string LegacyFolder = "Library://MCPMap3D/";

    public static object ListProviders(ToolContext context, JsonElement? args)
    {
        _ = context;
        return Map(() =>
        {
            string? warning = null;
            List<ProviderInfo> providers;
            try
            {
                providers = ParseProviders(Xml(Read(Features().GetFeatureProviders())));
            }
            catch (MgException ex)
            {
                warning = FirstLine(ex.GetExceptionMessage());
                ex.Dispose();
                providers = ProvidersFromRegistry();
            }

            if (providers.Count == 0)
                providers = ProvidersFromRegistry();
            return new { Count = providers.Count, Warning = warning, Providers = providers };
        });
    }

    public static object ListConnections(ToolContext context, JsonElement? args)
    {
        _ = context;
        var includeLibrary = new ArgReader(args).GetBool("includeLibrary", false);
        return Map(() =>
        {
            var map = RequireMap();
            var resources = ResourceService();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var library = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(resources, "Library://", library);
            ids.UnionWith(includeLibrary ? library : library.Where(IsOwned));

            foreach (var layer in Layers(map))
            {
                var id = layer.GetFeatureSourceId();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }

            var connections = ids.Select(id => DescribeConnection(resources, id)).ToList();
            return new { Count = connections.Count, Connections = connections };
        });
    }

    public static object Connect(ToolContext context, JsonElement? args)
    {
        _ = context;
        var reader = new ArgReader(args);
        var provider = reader.RequireString("provider").Trim();
        var connectionString = reader.GetString("connectionString");
        var createFile = reader.GetBool("createFile", false);
        var replace = reader.GetBool("replace", false);
        return Map(() =>
        {
            if (createFile && !provider.StartsWith("OSGeo.SDF", StringComparison.OrdinalIgnoreCase))
                throw new PipeException(PipeErrorCodes.InvalidParams, "createFile ne crée qu'un fichier SDF (fournisseur OSGeo.SDF).");

            var parameters = ParseConnection(connectionString);
            if (createFile)
            {
                var file = Parameter(parameters, "File")
                    ?? throw new PipeException(PipeErrorCodes.InvalidParams, "createFile demande File=chemin dans connectionString.");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
                if (File.Exists(file))
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier existe déjà : {file}");
            }
            else if (parameters.Count == 0)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Indiquez connectionString (Clé=valeur;Clé=valeur), ou createFile pour un SDF vide.");
            }

            // Une source existante n'est jamais écrasée sans replace : deux connexions sans nom (« Source » par défaut)
            // se remplaceraient, et un calque affiché changerait de données sans prévenir.
            var name = SafeName(reader.GetString("name") ?? GuessName(parameters));
            var map = RequireMap();
            var resource = new MgResourceIdentifier($"{OwnedPrefix}{name}.FeatureSource");
            if (ResourceService().ResourceExists(resource))
            {
                if (!replace)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"La source « {name} » existe déjà ({resource.ToString()}). Donnez un autre name, ou replace=true pour la remplacer.");
                if (Layers(map).FirstOrDefault(layer => SameResource(layer.GetFeatureSourceId(), resource.ToString())) is { } shown)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"La source « {name} » est affichée par le calque FDO « {shown.GetName()} » : retirez-le avant de la remplacer.");
            }

            var features = Features();
            if (!createFile && !features.TestConnection(provider, Join(parameters)))
                throw new PipeException(PipeErrorCodes.InvalidParams, "La connexion FDO a été refusée. Vérifiez le fournisseur et la chaîne.");
            if (createFile)
                CreateSdf(features, resource, provider, Parameter(parameters, "File")!);
            else
                Save(ResourceService(), resource, FeatureSourceXml(provider, parameters));

            if (!features.TestConnection(resource))
                throw new PipeException(PipeErrorCodes.InvalidParams, "La source a été enregistrée, mais Map 3D ne parvient pas à l'ouvrir.");

            return new
            {
                Resource = resource.ToString(),
                Provider = provider,
                Name = name,
                Replaced = replace ? true : (bool?)null,
                CreatedFile = createFile ? Parameter(parameters, "File") : null,
                Classes = ClassNames(features, resource),
            };
        });
    }

    public static object Describe(ToolContext context, JsonElement? args)
    {
        _ = context;
        var resource = new ArgReader(args).RequireString("resource");
        return Map(() => DescribeResource(ResolveResource(resource)));
    }

    public static object AddLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return Map(() =>
        {
            var map = RequireMap();
            var resource = ResolveResource(reader.RequireString("resource"));
            var requested = reader.RequireString("className").Trim();
            var (schema, className, definition) = ResolveClass(Features(), resource, requested);
            // Une classe raster (WMS, image) s'affiche par une définition de grille : en calque vectoriel, Map 3D
            // chercherait des géométries au lieu de demander l'image au serveur, et le calque resterait vide.
            var geometry = reader.GetString("geometry") ?? definition.GetDefaultGeometryPropertyName();
            var raster = RasterProperty(definition)
                ?? (string.IsNullOrWhiteSpace(geometry) && IsWms(resource) ? "Raster" : null);
            var isRaster = raster is not null && (string.IsNullOrWhiteSpace(geometry) || string.Equals(geometry, raster, StringComparison.OrdinalIgnoreCase));
            if (isRaster)
                geometry = raster!;
            else if (string.IsNullOrWhiteSpace(geometry))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La classe « {schema}:{className} » n'a ni géométrie ni image : elle ne peut pas devenir un calque.");

            var layerName = reader.GetString("name") ?? className;
            var layers = map.GetLayers();
            if (layers.Contains(layerName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Un calque FDO « {layerName} » existe déjà.");

            // Définition propre à ce calque : « A B » et « A_B » donnent le même nom sûr, et une définition laissée par
            // un calque retiré ne doit pas être réutilisée par un autre.
            var resources = ResourceService();
            var baseName = SafeName(layerName);
            var layerId = new MgResourceIdentifier($"{OwnedPrefix}{baseName}.LayerDefinition");
            for (var suffix = 2; resources.ResourceExists(layerId); suffix++)
                layerId = new MgResourceIdentifier($"{OwnedPrefix}{baseName}_{suffix}.LayerDefinition");
            // WMS : comme MAPCONNECT, une source dédiée à cette couche, avec son document de configuration. Sans lui,
            // Map 3D accepte le calque mais n'affiche aucune image.
            MgResourceIdentifier? wmsSource = null;
            if (isRaster && IsWms(resource))
                wmsSource = CreateWmsLayerSource(resources, resource, baseName, className);

            Save(resources, layerId, isRaster
                ? GridLayerXml((wmsSource ?? resource).ToString(), wmsSource is null ? $"{schema}:{className}" : $"{WmsSchema}:{className}")
                : LayerXml(resource.ToString(), $"{schema}:{className}", geometry, definition));
            AcMapLayer layer;
            string? warning = null;
            try
            {
                layer = AcMapLayer.Create(layerId, resources);
                layer.SetName(layerName);
                layer.SetLegendLabel(layerName);
                layer.SetDisplayInLegend(true);
                layer.SetVisible(true);
                layers.Add(layer);
            }
            catch (MgException ex) when (layers.Contains(layerName))
            {
                // Map 3D signale une erreur mais le calque est dans la carte : il est gardé (le supprimer laisserait un
                // calque orphelin), et l'erreur est rendue en avertissement.
                warning = FirstLine(ex.GetExceptionMessage());
                ex.Dispose();
                layer = (AcMapLayer)layers.GetItem(layerName);
            }
            catch (MgException)
            {
                // Calque refusé : sa définition et sa source dédiée ne doivent pas rester dans le dépôt.
                TryDelete(resources, layerId);
                if (wmsSource is not null)
                    TryDelete(resources, wmsSource);
                throw;
            }

            context.AfterCommit(() => context.Editor.Regen());
            return new
            {
                Layer = DescribeLayer(layer),
                LayerDefinition = layerId.ToString(),
                DedicatedSource = wmsSource?.ToString(),
                Warning = warning,
            };
        });
    }

    public static object ListLayers(ToolContext context, JsonElement? args)
    {
        _ = context;
        var filter = NameFilter.Create(new ArgReader(args).GetStrings("names"));
        return Map(() =>
        {
            var layers = Layers(RequireMap())
                .Where(layer => filter is null || filter.IsMatch(layer.GetName()))
                .Select(layer => DescribeLayer(layer))
                .ToList();
            return new { Count = layers.Count, Layers = layers };
        });
    }

    public static object Query(ToolContext context, JsonElement? args)
    {
        _ = context;
        var reader = new ArgReader(args);
        var limit = reader.GetInt("limit", DefaultLimit, 1, MaxLimit);
        return Map(() =>
        {
            var (resource, className) = ResolveQuery(reader);
            var options = new MgFeatureQueryOptions();
            if (reader.GetString("filter") is { } filter)
                options.SetFilter(filter);
            var featureReader = Features().SelectFeatures(resource, className, options);
            try
            {
                return ReadFeatures(featureReader, className, limit);
            }
            finally
            {
                featureReader.Close();
            }
        });
    }

    public static object SetLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return Map(() =>
        {
            var layer = RequireLayer(reader.RequireString("name"));
            if (!reader.Has("visible") && !reader.Has("rename") && !reader.GetBool("zoom", false))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez visible, rename ou zoom.");
            if (reader.Has("visible"))
                layer.SetVisible(reader.GetBool("visible", true));
            if (reader.GetString("rename") is { } rename)
            {
                if (RequireMap().GetLayers().Contains(rename) && !string.Equals(rename, layer.GetName(), StringComparison.OrdinalIgnoreCase))
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Un calque FDO « {rename} » existe déjà.");
                layer.SetName(rename);
            }

            var zoom = reader.GetBool("zoom", false) ? ZoomToLayer(context, layer) : null;
            layer.ForceRefresh();
            context.AfterCommit(() => context.Editor.Regen());
            return DescribeLayer(layer, zoom);
        });
    }

    /// <summary>
    /// Cadre la vue sur un calque. Sur un calque raster, ZoomToLayer de Map 3D échoue (un WMS n'a pas de géométrie dont
    /// calculer l'emprise) ou cadre l'emprise mondiale du serveur (mesuré dans un dessin vierge en Lambert-93 : une vue
    /// de 23 500 km). Passent donc d'abord l'étendue du contexte spatial de la source, limitée au domaine du système du
    /// dessin, puis ce domaine lui-même : la source dédiée d'un WMS déclare une étendue nulle, et pour un WMS national
    /// le domaine de Lambert-93 est la France. Un calque vectoriel que Map 3D refuse de cadrer (vide) ne prend que
    /// l'étendue de sa source. Le cadrage n'échoue jamais : une exception annulerait la transaction, et avec elle les
    /// sources du dépôt.
    /// </summary>
    private static object ZoomToLayer(ToolContext context, AcMapLayer layer)
    {
        const string Advice = "Pour une zone précise, cadrez avec set_view et window ou center (lonLat=true pour des longitudes/latitudes, comme celles de search_cadastre_address).";
        var raster = Safe<bool?>(() => layer.IsVectorLayer()) == false;
        string? refusal = null;
        if (!raster && (refusal = TryMapZoom(layer)) is null)
            return new { Method = "layer" };

        // Source WMS : son document de configuration (écrit comme celui de MAPCONNECT) déclare une étendue nulle, et
        // demander ses contextes spatiaux a fait planter Map 3D dans un dessin vierge : elle n'est pas interrogée.
        var sourceId = Safe(layer.GetFeatureSourceId);
        var wms = !string.IsNullOrWhiteSpace(sourceId) && IsWms(new MgResourceIdentifier(sourceId));
        var (box, detail) = wms ? (null, "une source WMS ne déclare pas l'étendue de ses couches") : SourceExtent(context.Database, layer);
        var method = "spatialContext";
        var covered = detail;
        if (box is null && raster && DomainExtent(context.Database) is { } domain)
        {
            (box, method) = (domain.Box, "coordinateSystemDomain");
            covered = $"le domaine du système « {domain.Code} » du dessin, car {detail}";
        }

        if (box is { } extent)
        {
            using (var view = context.Editor.GetCurrentView())
            {
                ModelingTools.FitBox(view, new Point3d(extent.MinX, extent.MinY, 0), new Point3d(extent.MaxX, extent.MaxY, 0), 1.05);
                context.Editor.SetCurrentView(view);
            }

            return new
            {
                Method = method,
                Window = new[] { extent.MinX, extent.MinY, extent.MaxX, extent.MaxY }.Select(Format.Number).ToArray(),
                Note = (raster ? "Calque raster" : $"Map 3D ne sait pas cadrer ce calque ({refusal})") + $" : la vue couvre {covered}. {Advice}",
            };
        }

        if (raster && (refusal = TryMapZoom(layer)) is null)
            return new { Method = "layer", Note = $"Cadrage de Map 3D, car {detail} : pour un WMS, il peut couvrir le monde entier. {Advice}" };

        return new { Method = "none", Note = $"Map 3D n'a pas pu cadrer ce calque ({refusal}), et {detail}. {Advice}" };
    }

    /// <summary>ZoomToLayer de Map 3D ; renvoie le motif du refus, ou null s'il a cadré.</summary>
    private static string? TryMapZoom(AcMapLayer layer)
    {
        try
        {
            layer.ZoomToLayer();
            return null;
        }
        catch (MgException ex)
        {
            var refusal = FirstLine(ex.GetExceptionMessage());
            ex.Dispose();
            return refusal;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            return FirstLine(ex.Message);
        }
    }

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>
    /// Étendue déclarée par le premier contexte spatial exploitable de la source du calque, dans le système du dessin.
    /// Chaque point d'une grille est ramené dans le domaine de ce système avant conversion : l'emprise mondiale d'un
    /// WMS national devient celle du domaine (la France pour Lambert-93). Le changement de datum est ignoré, ce qui
    /// suffit pour un cadrage. Sinon, Detail dit pourquoi chaque contexte a été écarté.
    /// </summary>
    private static (Box? Box, string Detail) SourceExtent(Database database, AcMapLayer layer)
    {
        var sourceId = Safe(layer.GetFeatureSourceId);
        if (string.IsNullOrWhiteSpace(sourceId))
            return (null, "le calque n'a pas de source");
        var drawingCode = AcMapCoordsysCore.GetCoordinateSystem(database);
        if (string.IsNullOrEmpty(drawingCode))
            return (null, "le dessin n'a pas de système de coordonnées dans lequel convertir l'étendue de la source");

        var factory = new MgCoordinateSystemFactory();
        var target = factory.CreateFromCode(drawingCode);
        var rejected = new List<string>();
        try
        {
            var contexts = Features().GetSpatialContexts(new MgResourceIdentifier(sourceId), false);
            try
            {
                while (contexts.ReadNext())
                {
                    var (box, reason) = ContextExtent(contexts, factory, target);
                    if (box is not null)
                        return (box, $"l'étendue du contexte spatial « {contexts.GetName()} » de la source, limitée au domaine de « {drawingCode} »");
                    rejected.Add($"« {contexts.GetName()} » : {reason}");
                }
            }
            finally
            {
                contexts.Close();
            }
        }
        catch (MgException ex)
        {
            var message = FirstLine(ex.GetExceptionMessage());
            ex.Dispose();
            return (null, $"l'étendue de sa source est illisible ({message})");
        }
        finally
        {
            target.Dispose();
        }

        return (null, rejected.Count == 0
            ? "sa source ne déclare aucun contexte spatial"
            : $"sa source ne déclare pas d'étendue exploitable ({string.Join(" ; ", rejected)})");
    }

    private static (Box? Box, string? Reason) ContextExtent(MgSpatialContextReader contexts, MgCoordinateSystemFactory factory, MgCoordinateSystem target)
    {
        var wkt = contexts.GetCoordinateSystemWkt();
        if (string.IsNullOrWhiteSpace(wkt))
            return (null, "pas de système de coordonnées");
        var envelope = new MgAgfReaderWriter().Read(contexts.GetExtent()).Envelope();
        var (lower, upper) = (envelope.GetLowerLeftCoordinate(), envelope.GetUpperRightCoordinate());
        var (minX, minY, maxX, maxY) = (lower.GetX(), lower.GetY(), upper.GetX(), upper.GetY());
        if (!(maxX > minX && maxY > minY) || !double.IsFinite(maxX - minX) || !double.IsFinite(maxY - minY))
            return (null, $"étendue vide ({Format.Number(minX)}, {Format.Number(minY)} à {Format.Number(maxX)}, {Format.Number(maxY)})");

        var source = factory.Create(wkt);
        try
        {
            var box = ProjectGrid(target, minX, minY, maxX, maxY, (x, y) =>
            {
                var lonLat = source.ConvertToLonLat(x, y);
                var point = (lonLat.GetX(), lonLat.GetY());
                lonLat.Dispose();
                return point;
            });
            return box is null ? (null, "étendue hors du domaine du système du dessin") : (box, null);
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>Domaine d'usage du système du dessin, en coordonnées du dessin (null s'il n'en déclare pas).</summary>
    private static (Box Box, string Code)? DomainExtent(Database database)
    {
        var code = AcMapCoordsysCore.GetCoordinateSystem(database);
        if (string.IsNullOrEmpty(code))
            return null;
        var target = new MgCoordinateSystemFactory().CreateFromCode(code);
        try
        {
            var (lonMin, lonMax, latMin, latMax) = (target.GetLonMin(), target.GetLonMax(), target.GetLatMin(), target.GetLatMax());
            if (!(lonMax > lonMin && latMax > latMin))
                return null;
            return ProjectGrid(target, lonMin, latMin, lonMax, latMax, (lon, lat) => (lon, lat)) is { } box ? (box, code) : null;
        }
        finally
        {
            target.Dispose();
        }
    }

    /// <summary>
    /// Boîte, dans le système cible, d'une grille de points d'un rectangle : chaque point est converti en longitude et
    /// latitude, ramené dans le domaine de la cible, puis projeté. Les points qui ne se convertissent pas sont ignorés.
    /// </summary>
    private static Box? ProjectGrid(MgCoordinateSystem target, double minX, double minY, double maxX, double maxY,
        Func<double, double, (double Lon, double Lat)> toLonLat)
    {
        var (lonMin, lonMax, latMin, latMax) = (target.GetLonMin(), target.GetLonMax(), target.GetLatMin(), target.GetLatMax());
        var hasDomain = lonMax > lonMin && latMax > latMin;
        var points = new List<(double X, double Y)>();
        const int steps = 8;
        for (var i = 0; i <= steps; i++)
        {
            for (var j = 0; j <= steps; j++)
            {
                double lon, lat;
                try
                {
                    (lon, lat) = toLonLat(minX + (maxX - minX) * i / steps, minY + (maxY - minY) * j / steps);
                }
                catch (MgException ex)
                {
                    ex.Dispose();
                    continue;
                }

                if (double.IsNaN(lon) || double.IsNaN(lat))
                    continue;
                if (hasDomain)
                    (lon, lat) = (Math.Clamp(lon, lonMin, lonMax), Math.Clamp(lat, latMin, latMax));
                if (CoordinateSystemTools.FromLonLat(target, lon, lat) is { } projected && double.IsFinite(projected.X) && double.IsFinite(projected.Y))
                    points.Add(projected);
            }
        }

        if (points.Count == 0)
            return null;
        var box = new Box(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        return box.MaxX > box.MinX && box.MaxY > box.MinY ? box : null;
    }

    public static object RemoveLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        return Map(() =>
        {
            var map = RequireMap();
            var layer = RequireLayer(reader.RequireString("name"));
            var source = layer.GetFeatureSourceId();
            var name = layer.GetName();
            map.GetLayers().Remove(layer);
            string? disconnected = null, kept = null;
            if (reader.GetBool("disconnect", false))
            {
                // Seules les sources créées par connect_fdo sont retirées : une connexion faite dans Map 3D (Data
                // Connect) ou ailleurs dans le dépôt reste en place.
                if (!IsOwned(source))
                    kept = $"Source {source} conservée : seules les sources créées par connect_fdo ({OwnedPrefix}…) sont retirées.";
                else if (Layers(map).Any(other => SameResource(other.GetFeatureSourceId(), source)))
                    kept = "Source conservée : elle est encore affichée par un autre calque.";
                else
                {
                    ResourceService().DeleteResource(new MgResourceIdentifier(source));
                    disconnected = source;
                }
            }

            context.AfterCommit(() => context.Editor.Regen());
            return new { Removed = name, FeatureSource = source, Disconnected = disconnected, SourceKept = kept };
        });
    }

    public static object Disconnect(ToolContext context, JsonElement? args)
    {
        _ = context;
        var resourceText = new ArgReader(args).RequireString("resource");
        return Map(() =>
        {
            var map = RequireMap();
            var resource = ResolveResource(resourceText);
            var id = resource.ToString();
            if (!IsOwned(id))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"« {id} » n'a pas été créée par connect_fdo ({OwnedPrefix}…) : seules ces sources peuvent être retirées. " +
                    "Une connexion faite dans Map 3D se gère dans Map 3D (Data Connect).");
            if (Layers(map).Any(layer => SameResource(layer.GetFeatureSourceId(), id)))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Cette source est encore affichée par un calque. Retirez-le d'abord avec remove_fdo_layer.");
            ResourceService().DeleteResource(resource);
            return new { Disconnected = id };
        });
    }

    public static object GetSelection(ToolContext context, JsonElement? args)
    {
        var limit = new ArgReader(args).GetInt("limit", DefaultLimit, 1, MaxLimit);
        return Map(() =>
        {
            var implied = context.Editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value is null)
                return new { Count = 0, Layers = Array.Empty<object>() };

            var selection = AcMapFeatureEntityService.GetSelection(implied.Value);
            if (selection is not AcMapSelection mapSelection)
                return new { Count = 0, Layers = Array.Empty<object>(), Warning = "La sélection courante ne contient pas d'objets FDO." };

            var groups = new List<object>();
            var total = 0;
            foreach (var layer in Layers(RequireMap()))
            {
                var className = layer.GetFeatureClassName();
                if (string.IsNullOrWhiteSpace(className))
                    continue;
                var featureReader = mapSelection.GetSelectedFeatures(layer, className, false);
                try
                {
                    var page = ReadFeatures(featureReader, className, limit);
                    if (page.Count == 0)
                        continue;
                    total += page.Count;
                    groups.Add(new { Layer = layer.GetName(), page.ClassName, page.Count, page.Truncated, page.Features });
                }
                finally
                {
                    featureReader.Close();
                }
            }

            return new { Count = total, Layers = groups };
        });
    }

    private static object DescribeResource(MgResourceIdentifier resource)
    {
        var features = Features();
        var schemas = features.GetSchemas(resource);
        var described = new List<object>();
        for (var i = 0; i < schemas.GetCount(); i++)
        {
            var schema = schemas.GetItem(i);
            var classes = features.GetClasses(resource, schema);
            var items = new List<object>();
            for (var c = 0; c < classes.GetCount(); c++)
            {
                var className = classes.GetItem(c);
                items.Add(DescribeClass(ClassDefinition(features, resource, schema, className), schema, className));
            }

            described.Add(new { Name = schema, Classes = items });
        }

        return new
        {
            Resource = resource.ToString(),
            Connected = features.TestConnection(resource),
            SpatialContexts = SpatialContexts(features, resource),
            Schemas = described,
        };
    }

    private static object DescribeClass(MgClassDefinition definition, string schema, string name)
    {
        var items = new List<object>();
        foreach (var property in AllProperties(definition))
        {
            items.Add(new
            {
                Name = property.GetName(),
                Type = property.GetType().Name,
                Geometry = property is MgGeometricPropertyDefinition geometric ? geometric.GetGeometryTypes() : (int?)null,
            });
        }

        return new
        {
            Name = $"{schema}:{name}",
            Class = definition.Name,
            Description = string.IsNullOrWhiteSpace(definition.GetDescription()) ? null : definition.GetDescription(),
            Geometry = definition.GetDefaultGeometryPropertyName(),
            Properties = items,
        };
    }

    private static IReadOnlyList<string> ClassNames(AcMapFeatureService features, MgResourceIdentifier resource)
    {
        var names = new List<string>();
        var schemas = features.GetSchemas(resource);
        for (var i = 0; i < schemas.GetCount(); i++)
        {
            var schema = schemas.GetItem(i);
            var classes = features.GetClasses(resource, schema);
            for (var c = 0; c < classes.GetCount(); c++)
                names.Add($"{schema}:{classes.GetItem(c)}");
        }

        return names;
    }

    private static (string Schema, string ClassName, MgClassDefinition Definition) ResolveClass(
        AcMapFeatureService features, MgResourceIdentifier resource, string requested)
    {
        var separator = requested.IndexOf(':');
        if (separator > 0)
        {
            var schema = requested[..separator];
            var className = requested[(separator + 1)..];
            return (schema, className, ClassDefinition(features, resource, schema, className));
        }

        string? foundSchema = null;
        string? foundClass = null;
        var schemas = features.GetSchemas(resource);
        for (var i = 0; i < schemas.GetCount(); i++)
        {
            var schema = schemas.GetItem(i);
            var classes = features.GetClasses(resource, schema);
            for (var c = 0; c < classes.GetCount(); c++)
            {
                if (!string.Equals(classes.GetItem(c), requested, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (foundClass is not null)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Plusieurs classes « {requested} ». Précisez Schéma:Classe.");
                foundSchema = schema;
                foundClass = classes.GetItem(c);
            }
        }

        return foundSchema is null || foundClass is null
            ? throw new PipeException(PipeErrorCodes.InvalidParams, $"Classe « {requested} » introuvable. Voyez describe_fdo.")
            : (foundSchema, foundClass, ClassDefinition(features, resource, foundSchema, foundClass));
    }

    private static (MgResourceIdentifier Resource, string ClassName) ResolveQuery(ArgReader reader)
    {
        if (reader.GetString("layer") is { } layerName)
        {
            var layer = RequireLayer(layerName);
            var featureClass = layer.GetFeatureClassName();
            if (string.IsNullOrWhiteSpace(featureClass))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layerName} » n'a pas de classe d'objets.");
            return (new MgResourceIdentifier(layer.GetFeatureSourceId()), featureClass);
        }

        if (reader.GetString("resource") is { } resource && reader.GetString("className") is { } requestedClass)
            return (ResolveResource(resource), requestedClass);

        throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez layer, ou resource et className.");
    }

    private static MgResourceIdentifier ResolveResource(string text)
    {
        text = text.Trim();
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("Session:", StringComparison.OrdinalIgnoreCase))
            return new MgResourceIdentifier(text.EndsWith(".FeatureSource", StringComparison.OrdinalIgnoreCase) ? text : text);

        var map = RequireMap();
        var resources = ResourceService();
        foreach (var candidate in new[]
        {
            $"{OwnedPrefix}{text}.FeatureSource",
            $"{LegacyFolder}{text}.FeatureSource",
            $"Library://{text}.FeatureSource",
        })
        {
            var id = new MgResourceIdentifier(candidate);
            if (resources.ResourceExists(id))
                return id;
        }

        foreach (var layer in Layers(map))
        {
            if (string.Equals(layer.GetName(), text, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(layer.GetFeatureSourceId()))
                return new MgResourceIdentifier(layer.GetFeatureSourceId());
        }

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Source FDO « {text} » introuvable. Connectez-la avec connect_fdo, ou donnez son identifiant.");
    }

    /// <summary>Source créée par connect_fdo : directement dans le dossier réservé, sans remontée de chemin.</summary>
    private static bool IsOwned(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains("..", StringComparison.Ordinal) &&
        ((id.StartsWith(OwnedPrefix, StringComparison.OrdinalIgnoreCase) && id.IndexOf('/', "Library://".Length) < 0) ||
         (id.StartsWith(LegacyFolder, StringComparison.OrdinalIgnoreCase) && id.IndexOf('/', LegacyFolder.Length) < 0));

    private static bool SameResource(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static AcMapLayer RequireLayer(string name)
    {
        var layers = RequireMap().GetLayers();
        if (!layers.Contains(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Calque FDO « {name} » introuvable. Voyez list_fdo_layers.");
        return layers.GetItem(name) as AcMapLayer
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » n'est pas un calque FDO de la plateforme.");
    }

    private static IEnumerable<AcMapLayer> Layers(AcMapMap map)
    {
        var layers = map.GetLayers();
        for (var i = 0; i < layers.GetCount(); i++)
        {
            if (layers.GetItem(i) is AcMapLayer layer)
                yield return layer;
        }
    }

    /// <summary>Classe d'un calque ; certains calques de grille lèvent « Non mis en œuvre » : la description s'en passe.</summary>
    private static MgClassDefinition? ClassOf(AcMapLayer layer)
    {
        try
        {
            return layer.GetClassDefinition();
        }
        catch (MgException ex)
        {
            ex.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Description d'un calque. Sur un calque raster, Map 3D refuse certaines lectures (GetCoordinateSystemId,
    /// GetClassDefinition) : chacune est protégée, car une exception ici ferait annuler la transaction, et avec elle
    /// les sources et définitions du dépôt, stocké dans le dessin, en laissant un calque orphelin dans la carte.
    /// </summary>
    private static object DescribeLayer(AcMapLayer layer, object? zoom = null)
    {
        var definition = ClassOf(layer);
        return new
        {
            Name = layer.GetName(),
            Visible = Safe(layer.IsVisible),
            FeatureSource = Safe(layer.GetFeatureSourceId),
            ClassName = Safe(layer.GetFeatureClassName),
            Geometry = definition?.GetDefaultGeometryPropertyName(),
            CoordinateSystem = Safe(layer.GetCoordinateSystemId),
            Vector = Safe(layer.IsVectorLayer),
            Raster = definition is not null ? RasterProperty(definition) is not null : (bool?)null,
            Text = Safe(layer.IsTextLayer),
            Loaded = Safe(() => layer.IsLoaded),
            Zoom = zoom,
        };
    }

    private static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (MgException ex)
        {
            ex.Dispose();
            return default;
        }
    }

    private static object DescribeConnection(MgResourceService resources, string id)
    {
        string? provider = null;
        try
        {
            if (resources.ResourceExists(new MgResourceIdentifier(id)))
            {
                var document = Xml(Read(resources.GetResourceContent(new MgResourceIdentifier(id))));
                provider = Child(document.Root, "Provider");
            }
        }
        catch (MgException ex)
        {
            ex.Dispose();
        }

        return new { Resource = id, Provider = provider, Library = id.StartsWith("Library:", StringComparison.OrdinalIgnoreCase) };
    }

    private static void Collect(MgResourceService resources, string folder, ISet<string> ids)
    {
        try
        {
            var document = Xml(Read(resources.EnumerateResources(new MgResourceIdentifier(folder), -1, "FeatureSource")));
            foreach (var id in document.Descendants().Where(element => element.Name.LocalName == "ResourceId").Select(element => element.Value))
            {
                if (id.EndsWith(".FeatureSource", StringComparison.OrdinalIgnoreCase))
                    ids.Add(id.Trim());
            }
        }
        catch (MgException ex)
        {
            ex.Dispose();
        }
    }

    private static IReadOnlyList<string> SpatialContexts(AcMapFeatureService features, MgResourceIdentifier resource)
    {
        var names = new List<string>();
        var reader = features.GetSpatialContexts(resource, false);
        try
        {
            while (reader.ReadNext())
            {
                var name = reader.GetName();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }
        finally
        {
            reader.Close();
        }

        return names;
    }

    private static dynamic ReadFeatures(MgFeatureReader reader, string className, int limit)
    {
        var features = new List<object>();
        var truncated = false;
        while (reader.ReadNext())
        {
            if (features.Count >= limit)
            {
                truncated = true;
                break;
            }

            var values = new Dictionary<string, object?>();
            for (var i = 0; i < reader.GetPropertyCount(); i++)
            {
                var name = reader.GetPropertyName(i);
                if (reader.GetPropertyType(name) == MgPropertyType.Geometry || reader.IsNull(name))
                {
                    if (reader.GetPropertyType(name) != MgPropertyType.Geometry)
                        values[name] = null;
                    continue;
                }

                values[name] = ReadValue(reader, name);
            }

            features.Add(values);
        }

        return new { ClassName = className, Count = features.Count, Truncated = truncated ? true : (bool?)null, Features = features };
    }

    private static object? ReadValue(MgFeatureReader reader, string name) => reader.GetPropertyType(name) switch
    {
        MgPropertyType.Boolean => reader.GetBoolean(name),
        MgPropertyType.Byte => reader.GetByte(name),
        MgPropertyType.Int16 => reader.GetInt16(name),
        MgPropertyType.Int32 => reader.GetInt32(name),
        MgPropertyType.Int64 => reader.GetInt64(name),
        MgPropertyType.Single => reader.GetSingle(name),
        MgPropertyType.Double => reader.GetDouble(name),
        MgPropertyType.String or MgPropertyType.Clob => reader.GetString(name),
        MgPropertyType.DateTime => reader.GetDateTime(name).ToString(),
        _ => null,
    };

    private static void CreateSdf(AcMapFeatureService features, MgResourceIdentifier resource, string provider, string file)
    {
        var id = new MgDataPropertyDefinition("Id");
        id.SetDataType((int)MgPropertyType.Int32);
        id.SetNullable(false);
        id.SetAutoGeneration(true);

        var geometry = new MgGeometricPropertyDefinition("Geometry");
        geometry.SetGeometryTypes(7);
        geometry.SetSpatialContextAssociation("Default");

        var featureClass = new MgClassDefinition();
        featureClass.SetName("Empty");
        featureClass.GetProperties().Add(id);
        featureClass.GetProperties().Add(geometry);
        featureClass.GetIdentityProperties().Add(id);
        featureClass.SetDefaultGeometryPropertyName("Geometry");

        var schema = new MgFeatureSchema("Default", "Source créée par MCP Map 3D");
        schema.GetClasses().Add(featureClass);

        const string wkt = "GEOGCS[\"LL84\",DATUM[\"WGS84\",SPHEROID[\"WGS84\",6378137.000,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"Degree\",0.017453292519943295]]";
        var parameters = new MgFileFeatureSourceParams(provider, "Default", wkt, schema);
        parameters.SetFileName(file);
        features.CreateFeatureSource(resource, parameters);
    }

    private static void Save(MgResourceService resources, MgResourceIdentifier resource, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        var source = new MgByteSource(bytes, bytes.Length);
        source.SetMimeType("text/xml");
        resources.SetResource(resource, source.GetReader(), null);
    }

    private static string FeatureSourceXml(string provider, IReadOnlyList<(string Name, string Value)> parameters, string? configurationDocument = null)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        body.Append("<FeatureSource xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"FeatureSource-1.0.0.xsd\">");
        body.Append("<Provider>").Append(Escape(provider)).Append("</Provider>");
        foreach (var (name, value) in parameters)
        {
            body.Append("<Parameter><Name>").Append(Escape(name)).Append("</Name><Value>")
                .Append(Escape(value)).Append("</Value></Parameter>");
        }

        if (configurationDocument is not null)
            body.Append("<ConfigurationDocument>").Append(Escape(configurationDocument)).Append("</ConfigurationDocument>");
        body.Append("</FeatureSource>");
        return body.ToString();
    }

    private static string LayerXml(string resource, string className, string geometry, MgClassDefinition definition)
    {
        var geometric = definition.GetProperties().GetItem(geometry) as MgGeometricPropertyDefinition;
        var types = geometric?.GetGeometryTypes() ?? 4;
        var style = (types & 1) != 0 ? PointStyle() : (types & 2) != 0 ? LineStyle() : AreaStyle();
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<LayerDefinition xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"LayerDefinition-2.4.0.xsd\" version=\"2.4.0\">"
            + "<VectorLayerDefinition><ResourceId>" + Escape(resource) + "</ResourceId><FeatureName>" + Escape(className)
            + "</FeatureName><FeatureNameType>FeatureClass</FeatureNameType><Geometry>" + Escape(geometry)
            + "</Geometry><VectorScaleRange>" + style + "</VectorScaleRange></VectorLayerDefinition></LayerDefinition>";
    }

    /// <summary>
    /// Définition de calque raster, sur le modèle exact de celle qu'écrit MAPCONNECT : version 1.0.0, propriété
    /// géométrique vide (le fournisseur désigne l'image), couleurs de la bande 1. Une version 2.4.0 ou une propriété
    /// nommée est refusée par Map 3D (« Non mis en œuvre »). L'image est demandée au serveur (GetMap) à chaque vue.
    /// </summary>
    private static string GridLayerXml(string resource, string className) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<LayerDefinition xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"LayerDefinition-1.0.0.xsd\" version=\"1.0.0\">"
        + "<GridLayerDefinition><ResourceId>" + Escape(resource) + "</ResourceId><FeatureName>" + Escape(className)
        + "</FeatureName><Geometry></Geometry><GridScaleRange><ColorStyle><ColorRule><LegendLabel></LegendLabel>"
        + "<Color><Band>1</Band></Color></ColorRule></ColorStyle><RebuildFactor>1</RebuildFactor></GridScaleRange>"
        + "</GridLayerDefinition></LayerDefinition>";

    /// <summary>
    /// Définition complète d'une classe. Pour certains fournisseurs (WMS), GetClassDefinition renvoie une classe sans
    /// propriétés : DescribeSchema donne alors la vraie, avec sa propriété raster.
    /// </summary>
    private static MgClassDefinition ClassDefinition(AcMapFeatureService features, MgResourceIdentifier resource, string schema, string className)
    {
        var definition = features.GetClassDefinition(resource, schema, className);
        if (definition.GetProperties().GetCount() > 0)
            return definition;

        var names = new MgStringCollection();
        names.Add(className);
        var schemas = features.DescribeSchema(resource, schema, names);
        for (var i = 0; i < schemas.GetCount(); i++)
        {
            var classes = schemas.GetItem(i).GetClasses();
            for (var c = 0; c < classes.GetCount(); c++)
            {
                var candidate = classes.GetItem(c);
                if (string.Equals(candidate.GetName(), className, StringComparison.Ordinal) && candidate.GetProperties().GetCount() > 0)
                    return candidate;
            }
        }

        return definition;
    }

    private const string WmsSchema = "WMSLayers";

    /// <summary>Suppression de nettoyage : son échec ne doit pas masquer l'erreur d'origine.</summary>
    private static void TryDelete(MgResourceService resources, MgResourceIdentifier id)
    {
        try
        {
            resources.DeleteResource(id);
        }
        catch (MgException ex)
        {
            ex.Dispose();
        }
    }

    /// <summary>
    /// Source dédiée à une couche WMS, sur le modèle de MAPCONNECT : mêmes paramètres de connexion que la source
    /// d'origine, et un document config.xml qui déclare la seule classe « WMSLayers:couche » (propriétés Id et Image),
    /// demandée en PNG transparent. Les systèmes de coordonnées du serveur viennent de la configuration par défaut du
    /// fournisseur ; l'image est demandée en EPSG:4326, comme le fait Map 3D, qui la reprojette lui-même.
    /// </summary>
    private static MgResourceIdentifier CreateWmsLayerSource(MgResourceService resources, MgResourceIdentifier source, string baseName, string className)
    {
        var content = Xml(Read(resources.GetResourceContent(source)));
        var provider = Child(content.Root, "Provider") ?? "OSGeo.WMS";
        var parameters = content.Root!.Elements()
            .Where(element => element.Name.LocalName == "Parameter")
            .Select(element => (Name: Child(element, "Name") ?? "", Value: Child(element, "Value") ?? ""))
            .Where(parameter => parameter.Name.Length > 0)
            .ToList();

        // Texte brut de la configuration par défaut : ses systèmes de coordonnées sont recopiés tels quels. Resérialisés
        // par .NET, chacun redéclarerait ses espaces de noms, et FDO ne les lirait plus (« données de bande » en échec).
        var mappingText = Read(Features().GetSchemaMapping(provider, Join(parameters.Where(p => p.Value.Length > 0).ToList())));
        var mapping = Xml(mappingText);
        var systems = mapping.Descendants().Where(element => element.Name.LocalName == "DerivedCRS").ToList();
        var first = mappingText.IndexOf("<gml:DerivedCRS", StringComparison.Ordinal);
        var last = mappingText.LastIndexOf("</gml:DerivedCRS>", StringComparison.Ordinal);
        var systemsXml = first >= 0 && last > first ? mappingText[first..(last + "</gml:DerivedCRS>".Length)] : "";
        // Titre de la couche, repris de la classe correspondante de la configuration par défaut.
        var title = mapping.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "complexType" &&
                                       (string?)element.Attribute("name") == FdoXmlName(className) + "Type")
            ?.Descendants().FirstOrDefault(element => element.Name.LocalName == "documentation")?.Value ?? "";
        var ids = systems.Select(element => element.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value).OfType<string>().ToList();
        var srs = ids.FirstOrDefault(id => id == "EPSG:4326") ?? ids.FirstOrDefault() ?? "EPSG:4326";
        var providerVersion = mapping.Descendants().FirstOrDefault(element => element.Name.LocalName == "SchemaMapping")
            ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "provider")?.Value ?? "OSGeo.WMS.4.2";

        var id = new MgResourceIdentifier($"{OwnedPrefix}{baseName}_WMS.FeatureSource");
        for (var suffix = 2; resources.ResourceExists(id); suffix++)
            id = new MgResourceIdentifier($"{OwnedPrefix}{baseName}_WMS_{suffix}.FeatureSource");

        // Comme MAPCONNECT : version WMS explicite dans l'adresse, et document désigné par « config://nom de la source »
        // (Map 3D ne retrouve pas un document désigné par « config.xml », même attaché sous ce nom).
        parameters = parameters
            .Select(p => p.Name.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) && !p.Value.Contains("version=", StringComparison.OrdinalIgnoreCase)
                ? (p.Name, p.Value + (p.Value.Contains('?') ? "&" : "?") + "version=1.3.0")
                : p)
            .ToList();
        foreach (var empty in new[] { "ProxyServerPassword", "ProxyServerUsername", "Username", "Password", "ProxyServerPort", "ProxyServerName" })
        {
            if (!parameters.Any(p => p.Name.Equals(empty, StringComparison.OrdinalIgnoreCase)))
                parameters.Add((empty, ""));
        }

        var sourceName = id.GetName();
        Save(resources, id, FeatureSourceXml(provider, parameters, $"config://{sourceName}"));
        var bytes = Encoding.UTF8.GetBytes(WmsConfig(systemsXml, srs, providerVersion, className, title));
        var data = new MgByteSource(bytes, bytes.Length);
        data.SetMimeType("text/xml");
        resources.SetResourceData(id, "config.xml", MgResourceDataType.Stream, data.GetReader());
        return id;
    }

    private static string WmsConfig(string systemsXml, string srs, string providerVersion, string layer, string title)
    {
        var name = FdoXmlName(layer);
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>")
            .Append("<fdo:DataStore xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
            .Append("xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:gml=\"http://www.opengis.net/gml\" xmlns:fdo=\"http://fdo.osgeo.org/schemas\" ")
            .Append("xmlns:fds=\"http://fdo.osgeo.org/schemas/fds\">");
        body.Append(systemsXml);

        body.Append("<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"http://fdo.osgeo.org/schemas/feature/").Append(WmsSchema)
            .Append("\" xmlns:fdo=\"http://fdo.osgeo.org/schemas\" xmlns:gml=\"http://www.opengis.net/gml\" xmlns:").Append(WmsSchema)
            .Append("=\"http://fdo.osgeo.org/schemas/feature/").Append(WmsSchema).Append("\" elementFormDefault=\"qualified\" attributeFormDefault=\"unqualified\">")
            .Append("<xs:element name=\"").Append(name).Append("\" type=\"").Append(WmsSchema).Append(':').Append(name)
            .Append("Type\" abstract=\"false\" substitutionGroup=\"gml:_Feature\"><xs:key name=\"").Append(name)
            .Append("Key\"><xs:selector xpath=\".//").Append(name).Append("\"/><xs:field xpath=\"Id\"/></xs:key></xs:element>")
            .Append("<xs:complexType name=\"").Append(name).Append("Type\" abstract=\"false\" fdo:hasGeometry=\"false\"><xs:annotation><xs:documentation>").Append(Escape(title))
            .Append("</xs:documentation></xs:annotation><xs:complexContent>")
            .Append("<xs:extension base=\"gml:AbstractFeatureType\"><xs:sequence><xs:element name=\"Id\"><xs:annotation><xs:documentation/></xs:annotation><xs:simpleType><xs:restriction base=\"xs:string\">")
            .Append("<xs:maxLength value=\"256\"/></xs:restriction></xs:simpleType></xs:element><xs:element name=\"Image\" type=\"fdo:RasterPropertyType\" ")
            .Append("fdo:defaultImageXSize=\"1024\" fdo:defaultImageYSize=\"1024\" fdo:srsName=\"").Append(Escape(srs))
            .Append("\"/></xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:schema>")
            .Append("<SchemaMapping provider=\"").Append(Escape(providerVersion)).Append("\" name=\"").Append(WmsSchema)
            .Append("\" xmlns=\"http://fdowms.osgeo.org/schemas\"><complexType name=\"").Append(name).Append("Type\"><RasterDefinition name=\"Image\">")
            .Append("<Format>PNG</Format><FormatType>image/png</FormatType><Transparent>true</Transparent><UseTileCache>false</UseTileCache>")
            .Append("<BackgroundColor></BackgroundColor><Time></Time><Elevation></Elevation><SpatialContext>").Append(Escape(srs))
            .Append("</SpatialContext><MinX>0.000000</MinX><MaxX>0.000000</MaxX><MinY>0.000000</MinY><MaxY>0.000000</MaxY>")
            .Append("<Layer name=\"WMS_Schema:").Append(Escape(layer)).Append("\"/></RasterDefinition></complexType></SchemaMapping></fdo:DataStore>");
        return body.ToString();
    }

    /// <summary>Nom FDO encodé pour XML : « A B » devient « A-x20-B » (codage de FDO pour les caractères non admis).</summary>
    private static string FdoXmlName(string name)
    {
        var encoded = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            var allowed = char.IsLetter(character) || character is '_' || (i > 0 && (char.IsDigit(character) || character is '.'));
            encoded.Append(allowed ? character.ToString() : $"-x{(int)character:x2}-");
        }

        return encoded.ToString();
    }

    /// <summary>Source servie par le fournisseur FDO WMS, dont chaque classe porte l'image dans la propriété « Raster ».</summary>
    private static bool IsWms(MgResourceIdentifier resource)
    {
        try
        {
            var provider = Child(Xml(Read(ResourceService().GetResourceContent(resource))).Root, "Provider");
            return provider?.StartsWith("OSGeo.WMS", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (MgException ex)
        {
            ex.Dispose();
            return false;
        }
    }

    /// <summary>Nom de la propriété raster de la classe, s'il y en a une.</summary>
    private static string? RasterProperty(MgClassDefinition definition) =>
        AllProperties(definition).OfType<MgRasterPropertyDefinition>().FirstOrDefault()?.GetName();

    /// <summary>Propriétés de la classe et de ses classes de base (le WMS range FeatId et Raster dans la classe de base).</summary>
    private static IEnumerable<MgPropertyDefinition> AllProperties(MgClassDefinition definition)
    {
        for (var current = definition; current is not null; current = current.GetBaseClassDefinition())
        {
            var properties = current.GetProperties();
            for (var i = 0; i < properties.GetCount(); i++)
                yield return properties.GetItem(i);
        }
    }

    /// <summary>
    /// Diagnostic, non exposé comme outil MCP : tous les calques de la carte avec leur type réel, et le XML de leur
    /// définition et de leur source, pour comparer avec ce que produit MAPCONNECT.
    /// </summary>
    public static object Diagnostic(ToolContext context, JsonElement? args)
    {
        _ = context;
        return Map(() =>
        {
            var map = RequireMap();
            var resources = ResourceService();
            string? Content(MgResourceIdentifier? id)
            {
                if (id is null)
                    return null;
                try
                {
                    return Read(resources.GetResourceContent(id));
                }
                catch (MgException ex)
                {
                    var message = ex.GetExceptionMessage();
                    ex.Dispose();
                    return $"(illisible : {message})";
                }
            }

            var layers = map.GetLayers();
            var described = new List<object>();
            for (var i = 0; i < layers.GetCount(); i++)
            {
                var layer = layers.GetItem(i);
                var definition = layer.GetLayerDefinition();
                described.Add(new
                {
                    Type = layer.GetType().FullName,
                    Name = layer.GetName(),
                    Group = layer.GetGroup()?.GetName(),
                    LayerDefinition = definition?.ToString(),
                    FeatureSource = layer.GetFeatureSourceId(),
                    ClassName = layer.GetFeatureClassName(),
                    LayerDefinitionXml = Content(definition),
                    FeatureSourceXml = string.IsNullOrWhiteSpace(layer.GetFeatureSourceId()) ? null : Content(new MgResourceIdentifier(layer.GetFeatureSourceId())),
                    ResourceData = string.IsNullOrWhiteSpace(layer.GetFeatureSourceId()) ? null : Data(new MgResourceIdentifier(layer.GetFeatureSourceId())),
                });
            }

            // Données attachées à une source, dont le document de configuration WMS (config://…).
            Dictionary<string, string?> Data(MgResourceIdentifier id)
            {
                var result = new Dictionary<string, string?>();
                try
                {
                    var list = Xml(Read(resources.EnumerateResourceData(id)));
                    foreach (var name in list.Descendants().Where(e => e.Name.LocalName == "Name").Select(e => e.Value))
                    {
                        try
                        {
                            result[name] = Read(resources.GetResourceData(id, name));
                        }
                        catch (MgException ex)
                        {
                            result[name] = $"(illisible : {ex.GetExceptionMessage()})";
                            ex.Dispose();
                        }
                    }
                }
                catch (MgException ex)
                {
                    result["(énumération)"] = ex.GetExceptionMessage();
                    ex.Dispose();
                }

                return result;
            }

            // Lectures ciblées : données nommées d'une source, et configuration par défaut d'un fournisseur.
            var reader = new ArgReader(args);
            var named = new Dictionary<string, string?>();
            if (reader.GetString("source") is { } source)
            {
                foreach (var name in reader.GetStrings("dataNames"))
                {
                    try
                    {
                        named[name] = Read(resources.GetResourceData(new MgResourceIdentifier(source), name));
                    }
                    catch (MgException ex)
                    {
                        named[name] = $"(illisible : {ex.GetExceptionMessage()})";
                        ex.Dispose();
                    }
                }
            }

            string? mapping = null;
            if (reader.GetString("provider") is { } provider && reader.GetString("connectionString") is { } connection)
            {
                try
                {
                    mapping = Read(Features().GetSchemaMapping(provider, connection));
                }
                catch (MgException ex)
                {
                    mapping = $"(illisible : {ex.GetExceptionMessage()})";
                    ex.Dispose();
                }
            }

            return new
            {
                LayerCount = layers.GetCount(),
                GroupCount = map.GetLayerGroups().GetCount(),
                Layers = described,
                NamedData = named.Count > 0 ? named : null,
                SchemaMappingLength = mapping?.Length,
                SchemaMapping = mapping is null ? null : mapping.Length > 60000 ? mapping[..60000] : mapping,
            };
        });
    }

    private static string PointStyle() =>
        "<PointTypeStyle><PointRule><LegendLabel/><PointSymbolization2D><Mark><Unit>Points</Unit><SizeContext>DeviceUnits</SizeContext><SizeX>6</SizeX><SizeY>6</SizeY>"
        + "<Fill><FillPattern>Solid</FillPattern><ForegroundColor>FFCC3300</ForegroundColor><BackgroundColor>FFCC3300</BackgroundColor></Fill>"
        + "<Edge><LineStyle>Solid</LineStyle><Thickness>1</Thickness><Color>FF000000</Color><Unit>Points</Unit></Edge></Mark></PointSymbolization2D></PointRule></PointTypeStyle>";

    private static string LineStyle() =>
        "<LineTypeStyle><LineRule><LegendLabel/><LineSymbolization2D><LineStyle>Solid</LineStyle><Thickness>1</Thickness><Color>FF003399</Color><Unit>Points</Unit></LineSymbolization2D></LineRule></LineTypeStyle>";

    private static string AreaStyle() =>
        "<AreaTypeStyle><AreaRule><LegendLabel/><AreaSymbolization2D><Fill><FillPattern>Solid</FillPattern><ForegroundColor>66007ACC</ForegroundColor><BackgroundColor>66007ACC</BackgroundColor></Fill>"
        + "<Stroke><LineStyle>Solid</LineStyle><Thickness>1</Thickness><Color>FF003366</Color><Unit>Points</Unit></Stroke></AreaSymbolization2D></AreaRule></AreaTypeStyle>";

    private static List<(string Name, string Value)> ParseConnection(string? text)
    {
        var items = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(text))
            return items;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Paramètre « {part} » : attendu Clé=valeur.");
            items.Add((part[..separator].Trim(), part[(separator + 1)..].Trim().Trim('"')));
        }

        return items;
    }

    private static string Join(IReadOnlyList<(string Name, string Value)> parameters) =>
        string.Join(";", parameters.Select(item => $"{item.Name}={item.Value}"));

    private static string? Parameter(IReadOnlyList<(string Name, string Value)> parameters, string name) =>
        parameters.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string GuessName(IReadOnlyList<(string Name, string Value)> parameters)
    {
        var file = Parameter(parameters, "File") ?? Parameter(parameters, "DefaultFileLocation");
        return string.IsNullOrWhiteSpace(file) ? "Source" : Path.GetFileNameWithoutExtension(file);
    }

    private static string SafeName(string name)
    {
        var cleaned = new string(name.Trim().Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned)
            ? throw new PipeException(PipeErrorCodes.InvalidParams, "Le nom de la source est vide.")
            : cleaned;
    }

    private sealed record ProviderInfo(string Name, string RegisteredName, string? DisplayName, string? Description);

    private static List<ProviderInfo> ParseProviders(XDocument document) =>
        document.Descendants()
            .Where(element => element.Name.LocalName.Equals("FeatureProvider", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var registered = Child(element, "Name") ?? "";
                return new ProviderInfo(ShortProvider(registered), registered, Child(element, "DisplayName"), Child(element, "Description"));
            })
            .Where(provider => !string.IsNullOrWhiteSpace(provider.RegisteredName))
            .ToList();

    private static List<ProviderInfo> ProvidersFromRegistry()
    {
        var folder = Path.GetDirectoryName(typeof(AcMapFeatureService).Assembly.Location);
        var path = Path.Combine(folder ?? "", "bin", "FDO", "providers.xml");
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Le registre des fournisseurs FDO est introuvable, et Map 3D n'a pas pu les énumérer.");
        return ParseProviders(XDocument.Load(path));
    }

    private static string ShortProvider(string registered)
    {
        var parts = registered.Split('.');
        return parts.Length >= 3 && parts[^1].All(char.IsDigit) && parts[^2].All(char.IsDigit)
            ? string.Join('.', parts[..^2])
            : registered;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Map 3D n'a pas pu énumérer les fournisseurs.";
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length <= 180 ? line : line[..180];
    }

    private static AcMapMap RequireMap()
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Les connexions FDO requièrent AutoCAD Map 3D.");
        return AcMapMap.GetCurrentMap() ?? throw new PipeException(PipeErrorCodes.MapUnavailable, "Aucune carte FDO n'est ouverte dans ce dessin.");
    }

    private static AcMapFeatureService Features() =>
        AcMapServiceFactory.GetService(MgServiceType.FeatureService) as AcMapFeatureService
        ?? throw new PipeException(PipeErrorCodes.MapUnavailable, "Le service d'objets FDO n'est pas disponible.");

    private static MgResourceService ResourceService() =>
        AcMapServiceFactory.GetService(MgServiceType.ResourceService) as AcMapResourceService
        ?? throw new PipeException(PipeErrorCodes.MapUnavailable, "Le service de ressources FDO n'est pas disponible.");

    private static object Map(Func<object> action)
    {
        try
        {
            return action();
        }
        catch (PipeException)
        {
            throw;
        }
        catch (MgException ex)
        {
            var message = string.Join(" ", new[] { ex.GetExceptionMessage(), ex.GetDetails() }.Where(part => !string.IsNullOrWhiteSpace(part)));
            ex.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, string.IsNullOrWhiteSpace(message) ? "Map 3D a refusé l'opération FDO." : message);
        }
    }

    private static string Read(MgByteReader reader)
    {
        try
        {
            return reader.ToString();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static XDocument Xml(string text) =>
        string.IsNullOrWhiteSpace(text) ? new XDocument(new XElement("empty")) : XDocument.Parse(text);

    private static string? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

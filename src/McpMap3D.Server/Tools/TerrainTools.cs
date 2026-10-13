using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using McpMap3D.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Terrain (MNT de l'IGN, courbes de niveau, analyses), images géoréférencées et publication GeoJSON/KML.</summary>
[McpServerToolType]
public sealed class TerrainTools(PluginClient plugin)
{
    private const int MaxTerrainPoints = 100_000;
    private const int ElevationBatch = 5000;
    private const string ElevationUrl = "https://data.geopf.fr/altimetrie/1.0/calcul/alti/rest/elevation.json";
    private const string ElevationService = "Le service d'altimétrie de la Géoplateforme (IGN)";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
        DefaultRequestHeaders = { { "User-Agent", "McpMap3D (connecteur MCP pour AutoCAD Map 3D)" } },
    };

    [McpServerTool(Name = "import_terrain", Title = "Importer le MNT de l'IGN",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Télécharge les altitudes du RGE ALTI de l'IGN (Géoplateforme, France entière, résolution jusqu'à 1 m) sur " +
                 "une grille régulière et en fait un maillage de terrain (MNT) dans le système du dessin, avec en option " +
                 "les courbes de niveau. Zone : autour d'une adresse ou d'un point lon/lat (size), un rectangle lon/lat " +
                 "(bbox), ou l'emprise des objets du dessin (extent). Si le dessin n'a pas de système, celui de la zone lui " +
                 "est attribué (Lambert-93 en métropole). 100 000 points au plus (1 km² au pas de 3,2 m). Une étape de U.")]
    public async Task<string> ImportTerrain(
        [Description("Adresse au centre de la zone.")] string? address = null,
        [Description("Centre de la zone [longitude, latitude].")] double[]? center = null,
        [Description("Côté du carré autour de address ou center, en mètres (300 par défaut).")] double size = 300,
        [Description("Rectangle [lonMin, latMin, lonMax, latMax].")] double[]? bbox = null,
        [Description("Zone = emprise des objets du dessin, plus margin.")] bool extent = false,
        [Description("Avec extent : marge en mètres (20 par défaut).")] double margin = 20,
        [Description("Pas de la grille en mètres (5 par défaut, 1 au minimum).")] double step = 5,
        [Description(ToolHelp.Layer + " Créé s'il manque ; MNT par défaut.")] string layer = "MNT",
        [Description("Équidistance des courbes de niveau en mètres (aucune si omis).")] double? contourInterval = null,
        [Description("Une courbe sur majorEvery est maîtresse (5 par défaut).")] int majorEvery = 5,
        [Description("Objet créé : mesh (défaut) ou polyface.")] string meshType = "mesh",
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (step < 1)
            throw new McpException("step doit valoir au moins 1 m (résolution du RGE ALTI).");
        await plugin.CallAsync("status", cancellationToken: cancellationToken).ConfigureAwait(false);

        BoundingBox zone;
        object? located = null;
        if (bbox is { Length: 4 })
        {
            zone = new BoundingBox(bbox[0], bbox[1], bbox[2], bbox[3]);
        }
        else if (extent)
        {
            var extents = await plugin.CallAsync("get_drawing_extents_lonlat", cancellationToken: cancellationToken).ConfigureAwait(false);
            zone = new BoundingBox(extents.GetProperty("minLon").GetDouble(), extents.GetProperty("minLat").GetDouble(),
                extents.GetProperty("maxLon").GetDouble(), extents.GetProperty("maxLat").GetDouble()).Expand(margin);
        }
        else if (center is { Length: 2 })
        {
            zone = BoundingBox.Around(center[0], center[1], size / 2);
        }
        else if (!string.IsNullOrWhiteSpace(address))
        {
            var match = Geocoder.ChooseStrict(await Geocoder.SearchAsync(address, cancellationToken).ConfigureAwait(false), address);
            zone = BoundingBox.Around(match.Lon, match.Lat, size / 2);
            located = Geocoder.Describe(match);
        }
        else
        {
            throw new McpException("Indiquez la zone : address, center [lon, lat], bbox [lonMin, latMin, lonMax, latMax] ou extent=true.");
        }

        if (zone.MaxLon <= zone.MinLon || zone.MaxLat <= zone.MinLat)
            throw new McpException("La zone est vide : vérifiez bbox (lonMin < lonMax, latMin < latMax).");
        // Tolérance d'arrondi : une zone de 100 m mesure parfois 99,9999 m en degrés reconvertis.
        var (columns, rows) = ((int)Math.Floor(zone.WidthMeters / step + 1e-6) + 1, (int)Math.Floor(zone.HeightMeters / step + 1e-6) + 1);
        if ((long)columns * rows > MaxTerrainPoints)
            throw new McpException($"{columns} × {rows} = {(long)columns * rows} points au pas de {step} m, au-delà de {MaxTerrainPoints} : " +
                                   $"augmentez step (au moins {Math.Ceiling(Math.Sqrt(zone.WidthMeters * zone.HeightMeters / MaxTerrainPoints))} m) ou réduisez la zone.");

        var (dLon, dLat) = ((zone.MaxLon - zone.MinLon) / Math.Max(columns - 1, 1), (zone.MaxLat - zone.MinLat) / Math.Max(rows - 1, 1));
        var grid = new List<(double Lon, double Lat)>(columns * rows);
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
                grid.Add((zone.MinLon + i * dLon, zone.MinLat + j * dLat));
        }

        var points = new List<double[]>(grid.Count);
        for (var start = 0; start < grid.Count; start += ElevationBatch)
        {
            var batch = grid.Skip(start).Take(ElevationBatch).ToList();
            var elevations = await ElevationsAsync(batch, cancellationToken).ConfigureAwait(false);
            for (var k = 0; k < batch.Count; k++)
            {
                // -99999 : hors des données (mer, étranger).
                if (elevations[k] > -1000)
                    points.Add([Math.Round(batch[k].Lon, 8), Math.Round(batch[k].Lat, 8), elevations[k]]);
            }

            progress?.Report(new ProgressNotificationValue
            {
                Progress = Math.Min(start + ElevationBatch, grid.Count),
                Total = grid.Count,
                Message = $"Altitudes : {Math.Min(start + ElevationBatch, grid.Count)} / {grid.Count}",
            });
        }

        if (points.Count < 3)
            throw new McpException("Le RGE ALTI n'a pas d'altitude sur cette zone (mer ou hors de France).");

        var centerLon = (zone.MinLon + zone.MaxLon) / 2;
        var centerLat = (zone.MinLat + zone.MaxLat) / 2;
        var result = await plugin.CallAsync("create_terrain_mesh", new
        {
            points,
            from = "lonlat",
            assignCoordinateSystem = $"EPSG:{Geo.DefaultEpsg(centerLon, centerLat)}",
            layer,
            createLayer = true,
            meshType,
            maxEdgeLength = step * 3,
            contourInterval,
            majorEvery,
        }, busyTimeout: TimeSpan.FromMinutes(3), cancellationToken: cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            Address = located,
            Grid = new { Columns = columns, Rows = rows, Step = step, Points = points.Count, NoData = grid.Count - points.Count },
            ZoneMeters = new[] { Math.Round(zone.WidthMeters), Math.Round(zone.HeightMeters) },
            Terrain = result,
            Source = "RGE ALTI (IGN, Géoplateforme)",
        }, PipeProtocol.JsonOptions);
    }

    /// <summary>Altitudes d'un lot de points (5000 au plus), avec deux nouvelles tentatives sur erreur passagère.</summary>
    private static async Task<double[]> ElevationsAsync(List<(double Lon, double Lat)> points, CancellationToken cancellationToken)
    {
        var body = new
        {
            lon = string.Join("|", points.Select(p => p.Lon.ToString("0.########", CultureInfo.InvariantCulture))),
            lat = string.Join("|", points.Select(p => p.Lat.ToString("0.########", CultureInfo.InvariantCulture))),
            resource = "ign_rge_alti_wld",
            zonly = "true",
        };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await Http.PostAsJsonAsync(ElevationUrl, body, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new McpException($"{ElevationService} a refusé la requête (HTTP {(int)response.StatusCode}).");
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var values = document.RootElement.GetProperty("elevations").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                return values.Length == points.Count ? values : throw new McpException($"{ElevationService} a renvoyé {values.Length} altitudes pour {points.Count} points.");
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new McpException($"{ElevationService} est injoignable ({ex.Message}). Vérifiez la connexion Internet.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException($"{ElevationService} n'a pas répondu en 90 s : réessayez, ou augmentez step.");
            }
        }
    }

    [McpServerTool(Name = "create_contours", Title = "Créer des courbes de niveau",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Dessine les courbes de niveau d'un maillage de terrain (import_terrain, create_terrain_mesh, ou tout " +
                 "maillage ou polyface) : polylignes posées à leur altitude, une maîtresse toutes les majorEvery sur son " +
                 "propre calque. Calques créés s'ils manquent. Une étape de U.")]
    public Task<string> CreateContours(
        [Description("Handle du maillage de terrain.")] string handle,
        [Description("Équidistance en unités du dessin, par exemple 1 ou 0,5.")] double interval,
        [Description("Une courbe sur majorEvery est maîtresse (5 par défaut, 0 : aucune).")] int majorEvery = 5,
        [Description("Calque des courbes (COURBES par défaut).")] string? contourLayer = null,
        [Description("Calque des courbes maîtresses (COURBES_MAITRESSES par défaut).")] string? majorLayer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_contours", new { handle, contourInterval = interval, majorEvery, contourLayer, majorLayer },
            busyTimeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);

    [McpServerTool(Name = "analyze_terrain", Title = "Analyser un terrain",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Analyse un maillage de terrain sans le modifier : elevation (altitude en des points), profile (profil en " +
                 "long le long d'une courbe ou de points : distance, x, y, z, dénivelés, pente maximale), volume (déblai et " +
                 "remblai dans un contour fermé par rapport à une altitude de référence, sur une grille) ou slope (pente " +
                 "moyenne et maximale, surfaces par classe de pente, dans tout le maillage ou un contour).")]
    public Task<string> AnalyzeTerrain(
        [Description("Handle du maillage de terrain.")] string handle,
        [Description("elevation, profile, volume ou slope.")] string mode,
        [Description("Points [[x, y], …] : altitudes (elevation) ou tracé du profil (profile).")] double[][]? points = null,
        [Description("Profile : handle de la courbe du tracé, à la place de points.")] string? path = null,
        [Description("Profile : pas d'échantillonnage (longueur / 200 par défaut, 1000 points au plus).")] double? step = null,
        [Description("Volume et slope : handle d'un contour fermé.")] string? boundary = null,
        [Description("Volume : altitude de référence (plateforme, fond de fouille).")] double? referenceZ = null,
        [Description("Volume : taille des mailles de la grille (environ 40 000 mailles si omis).")] double? cellSize = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("analyze_terrain", new { handle, mode, points, path, step, boundary, referenceZ, cellSize },
            busyTimeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);

    [McpServerTool(Name = "attach_image", Title = "Insérer une image géoréférencée",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Insère une image raster (orthophoto, plan scanné : TIF, JPG, PNG…) à sa place : d'après son fichier monde " +
                 "(.tfw, .jgw, .pgw, .wld…) ou ses balises GeoTIFF, dont les coordonnées doivent être dans le système du " +
                 "dessin ; sinon à position (coin en bas à gauche) avec la largeur width. Le fichier reste lié, pas incorporé. " +
                 "Une étape de U.")]
    public Task<string> AttachImage(
        [Description("Chemin complet de l'image.")] string filePath,
        [Description("Coin en bas à gauche [x, y], si l'image n'est pas géoréférencée (ou pour l'ignorer).")] double[]? position = null,
        [Description("Avec position : largeur de l'image en unités du dessin.")] double? width = null,
        [Description("Rendre transparent le fond des images qui en ont un (PNG).")] bool transparency = false,
        [Description(ToolHelp.Layer)] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("attach_image", new { filePath, position, width, transparency, layer }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "export_geodata", Title = "Publier en GeoJSON ou KML",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Publie des objets du dessin en GeoJSON, KML ou KMZ (Google Earth, cartes web), en longitude/latitude WGS84 : " +
                 "points, blocs et textes en points, lignes et polylignes en lignes, courbes fermées en polygones, avec " +
                 "calque, texte, attributs de bloc et données d'objet. Mêmes filtres que list_entities. Le dessin n'est " +
                 "pas modifié ; le fichier est écrit (overwrite pour le remplacer).")]
    public Task<string> ExportGeodata(
        [Description("Chemin du fichier .geojson, .kml ou .kmz.")] string filePath,
        [Description("Format, s'il ne se déduit pas de l'extension : geojson, kml ou kmz.")] string? format = null,
        [Description("Calques à retenir ; les jokers * et ? sont acceptés. Tous si omis.")] string[]? layers = null,
        [Description("Types DXF à retenir. Tous si omis.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Handles d'objets précis.")] string[]? handles = null,
        [Description("Objets sélectionnés par l'utilisateur dans AutoCAD.")] bool selection = false,
        [Description("Système des coordonnées du dessin s'il n'en a pas d'attribué : code, EPSG ou lonlat.")] string? from = null,
        [Description("Inclure les données d'objet (vrai par défaut).")] bool includeObjectData = true,
        [Description("Titre du document (nom du fichier par défaut).")] string? title = null,
        [Description("Remplacer le fichier s'il existe.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_geodata",
            new { filePath, format, layers, types, where, handles, selection = selection ? true : (bool?)null, from, includeObjectData, title, overwrite = overwrite ? true : (bool?)null },
            busyTimeout: TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
}

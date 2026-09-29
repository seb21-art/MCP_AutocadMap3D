using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpMap3D.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Cadastre français : recherche d'adresse (Base Adresse Nationale) et import du plan cadastral vectoriel
/// (APICARTO de l'IGN, données PCI). Les téléchargements se font ici ; le plug-in ne reçoit que la géométrie,
/// en longitude/latitude, et la projette dans le système du dessin.
/// </summary>
[McpServerToolType]
public sealed class CadastreTools(PluginClient plugin)
{
    private const int DefaultMaxParcels = 2000;
    private const int DefaultMaxBuildings = 4000;
    private const int SearchMaxParcels = 300;
    private const double MaxZoneMeters = 10_000;

    [McpServerTool(Name = "import_cadastre", Title = "Importer le plan cadastral",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Télécharge le plan cadastral français (APICARTO de l'IGN) et le dessine dans l'espace objet : parcelles " +
                 "en polylignes fermées (calque <préfixe>_PARCELLES), numéros au centre des parcelles (<préfixe>_NUMEROS), " +
                 "feuilles de sections avec le code de section (<préfixe>_SECTIONS). Les attributs sont rattachés en données " +
                 "d'objet Map 3D : table <préfixe> (ID, COMMUNE, CODE_INSEE, SECTION, NUMERO, CONTENANCE) et table " +
                 "<préfixe>_SECTIONS (ID, COMMUNE, CODE_INSEE, SECTION, FEUILLE). Modes : « address » (autour d'une adresse), « extent » (emprise des objets du dessin) " +
                 "ou « parcel » (code INSEE, section, numéros, et voisines avec buffer). Avec includeBuildings, les bâtiments " +
                 "de la zone (en mode parcel, ceux des parcelles importées) sont dessinés sur <préfixe>_BATI_DUR et " +
                 "<préfixe>_BATI_LEGER, avec la table <préfixe>_BATIMENTS : bâti du plan cadastral par défaut, ou BD TOPO " +
                 "(hauteur, usage, étages, altitudes), en 3D si buildings3d. Les objets déjà importés sont ignorés " +
                 "(skipExisting). Si le dessin n'a pas de système de coordonnées, celui de la zone lui est attribué " +
                 "(Lambert-93 en métropole, UTM local outre-mer). Au-delà de maxParcels parcelles ou maxBuildings bâtiments, " +
                 "rien n'est importé : réduisez la zone. Annulable en une seule étape avec U.")]
    public async Task<string> ImportCadastre(
        [Description("« address », « extent » ou « parcel ». Déduit des paramètres si omis : address si une adresse est " +
                     "donnée, parcel si codeInsee et section le sont, extent sinon.")] string? mode = null,
        [Description("Adresse postale, le plus précise possible (numéro, rue, code postal ou commune).")] string? address = null,
        [Description("Code INSEE de la commune (5 caractères, par exemple 33063 ; pour Paris, Lyon et Marseille, celui de " +
                     "l'arrondissement).")] string? codeInsee = null,
        [Description("Section cadastrale : 1 ou 2 caractères (A, 0A, KM…).")] string? section = null,
        [Description("Numéro(s) de parcelle, séparés par des virgules (81, 0081, « 81, 82, 115 »). Toute la section si omis.")] string? numero = null,
        [Description("Marge en mètres : demi-côté du carré autour de l'adresse (100 par défaut), marge autour de l'emprise " +
                     "du dessin (20 par défaut), ou rayon d'import des parcelles voisines en mode parcel (0 par défaut).")] double? buffer = null,
        [Description("Importer les feuilles de sections (vrai par défaut).")] bool includeSections = true,
        [Description("Importer aussi les bâtiments (faux par défaut).")] bool includeBuildings = false,
        [Description("Source des bâtiments : « cadastre » (bâti du plan cadastral, aligné sur les parcelles, dur ou léger ; " +
                     "par défaut) ou « bdtopo » (BD TOPO de l'IGN : nature, usage, hauteur, étages, logements, altitudes du " +
                     "sol et du toit, identifiant RNB).")] string? buildingSource = null,
        [Description("Bâtiments en 3D : polylignes posées à l'altitude du sol et extrudées de leur hauteur (épaisseur). " +
                     "Nécessite la BD TOPO, choisie d'office. Faux par défaut.")] bool buildings3d = false,
        [Description("Étiqueter les parcelles (numéro) et les sections (code) (vrai par défaut).")] bool createLabels = true,
        [Description("Hauteur des numéros de parcelles en unités du dessin (2 par défaut) ; les codes de section font le triple.")] double labelHeight = 2.0,
        [Description("Rattacher les attributs en données d'objet Map 3D (vrai par défaut).")] bool attachObjectData = true,
        [Description("Système à attribuer si le dessin n'en a pas (code Map 3D, voir search_coordinate_systems). Choisi " +
                     "d'après la zone si omis. Ignoré si le dessin a déjà un système.")] string? coordinateSystem = null,
        [Description("Préfixe des noms de calques et de tables de données d'objet (CADASTRE par défaut).")] string layerPrefix = "CADASTRE",
        [Description("Cadrer la vue sur les objets importés (vrai par défaut).")] bool zoomExtents = true,
        [Description("Ignorer les parcelles et feuilles déjà présentes dans le dessin, reconnues par leur identifiant en " +
                     "données d'objet (vrai par défaut).")] bool skipExisting = true,
        [Description("Nombre maximal de parcelles (2000 par défaut) : au-delà, l'import est refusé.")] int maxParcels = DefaultMaxParcels,
        [Description("Nombre maximal de bâtiments (4000 par défaut) : au-delà, l'import est refusé.")] int maxBuildings = DefaultMaxBuildings,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maxParcels < 1 || maxBuildings < 1)
            throw new McpException("« maxParcels » et « maxBuildings » doivent valoir au moins 1.");

        var source = buildingSource?.Trim().ToLowerInvariant() switch
        {
            null or "" => buildings3d ? "bdtopo" : "cadastre",
            "cadastre" or "pci" => "cadastre",
            "bdtopo" or "bd topo" or "bd_topo" => "bdtopo",
            var other => throw new McpException($"Source de bâtiments « {other} » inconnue : « cadastre » ou « bdtopo »."),
        };
        if (buildings3d && source == "cadastre")
            throw new McpException("Les bâtiments en 3D demandent leur hauteur, que seule la BD TOPO fournit : utilisez buildingSource « bdtopo ».");
        if (labelHeight <= 0)
            throw new McpException("« labelHeight » doit être strictement positif.");
        if (buffer < 0)
            throw new McpException("« buffer » ne peut pas être négatif.");

        var actualMode = mode?.Trim().ToLowerInvariant() is { Length: > 0 } given ? given
            : !string.IsNullOrWhiteSpace(address) ? "address"
            : !string.IsNullOrWhiteSpace(codeInsee) || !string.IsNullOrWhiteSpace(section) ? "parcel"
            : "extent";

        // AutoCAD et le plug-in d'abord : inutile de télécharger si le dessin est inaccessible.
        await plugin.CallAsync("status", cancellationToken: cancellationToken).ConfigureAwait(false);

        var download = new Download(maxParcels, maxBuildings, progress, cancellationToken);
        object? addressInfo = null;
        BoundingBox? zoneOfBuildings = null;
        switch (actualMode)
        {
            case "address":
            {
                if (string.IsNullOrWhiteSpace(address))
                    throw new McpException("Le mode « address » demande le paramètre « address ».");

                var candidates = await Geocoder.SearchAsync(address, cancellationToken).ConfigureAwait(false);
                var match = Geocoder.ChooseStrict(candidates, address);
                var zone = CheckSize(BoundingBox.Around(match.Lon, match.Lat, Math.Max(buffer ?? 100, 1)), "Réduisez « buffer ».");
                await download.ParcelsAsync($"geom={zone.ToGeoJson()}", TooManyInZone("autour de l'adresse", "buffer")).ConfigureAwait(false);
                if (includeSections)
                    await download.SectionsAsync($"geom={zone.ToGeoJson()}").ConfigureAwait(false);

                zoneOfBuildings = zone;
                addressInfo = new
                {
                    Match = Geocoder.Describe(match),
                    ParcelAtAddress = Geo.ParcelAt(match.Lon, match.Lat, download.Parcels)?.Id,
                };
                break;
            }

            case "extent":
            {
                var extents = await plugin.CallAsync("get_drawing_extents_lonlat", cancellationToken: cancellationToken).ConfigureAwait(false);
                var zone = CheckSize(new BoundingBox(
                    extents.GetProperty("minLon").GetDouble(), extents.GetProperty("minLat").GetDouble(),
                    extents.GetProperty("maxLon").GetDouble(), extents.GetProperty("maxLat").GetDouble()).Expand(buffer ?? 20),
                    "L'emprise du dessin est trop étendue : importez par adresse ou par parcelle.");

                await download.ParcelsAsync($"geom={zone.ToGeoJson()}", TooManyInZone("dans l'emprise du dessin", "buffer")).ConfigureAwait(false);
                if (includeSections)
                    await download.SectionsAsync($"geom={zone.ToGeoJson()}").ConfigureAwait(false);
                zoneOfBuildings = zone;
                break;
            }

            case "parcel":
            {
                var insee = NormalizeInsee(codeInsee);
                var sectionCode = NormalizeSection(section);
                var numbers = NormalizeNumbers(numero);
                string TooManyInSection(int matched) =>
                    $"APICARTO renvoie {matched} parcelles pour la section « {sectionCode} » de {insee}, au-delà de maxParcels " +
                    $"({maxParcels}). Si la section n'existe pas dans cette commune, le service renvoie toute la commune : " +
                    "vérifiez le code de section (search_cadastre_address donne les sections autour d'une adresse).";

                var query = $"code_insee={insee}&section={sectionCode}";
                if (numbers.Count == 0)
                    await download.ParcelsAsync(query, TooManyInSection, p => p.Section == sectionCode).ConfigureAwait(false);
                foreach (var number in numbers)
                    await download.ParcelsAsync($"{query}&numero={number}", TooManyInSection,
                        p => p.Section == sectionCode && p.Numero == number).ConfigureAwait(false);

                if (download.Parcels.Count == 0)
                    throw new McpException(
                        $"Aucune parcelle trouvée : commune {insee}, section {sectionCode}" +
                        (numbers.Count > 0 ? $", numéro(s) {string.Join(", ", numbers)}." : ".") +
                        " Vérifiez le code INSEE (celui de la commune, pas le code postal) et la section.");

                if (buffer > 0)
                {
                    var zone = CheckSize(BoundingBox.Of(download.Parcels).Expand(buffer.Value), "Réduisez « buffer ».");
                    await download.ParcelsAsync($"geom={zone.ToGeoJson()}", TooManyInZone("avec les voisines", "buffer")).ConfigureAwait(false);
                    if (includeSections)
                        await download.SectionsAsync($"geom={zone.ToGeoJson()}").ConfigureAwait(false);
                }
                else if (includeSections)
                {
                    await download.SectionsAsync(query, s => s.Section == sectionCode).ConfigureAwait(false);
                }

                break;
            }

            default:
                throw new McpException($"Mode « {mode} » inconnu : address, extent ou parcel.");
        }

        if (download.Parcels.Count == 0)
            throw new McpException("Aucune parcelle cadastrale dans la zone demandée (domaine public non cadastré ?). Élargissez la zone avec buffer.");

        if (includeBuildings)
        {
            // En mode parcel, seulement les bâtiments des parcelles importées (l'emprise déborde sur les voisines).
            var touchesParcels = Geo.TouchesAny(download.Parcels);
            await download.BuildingsAsync(zoneOfBuildings ?? BoundingBox.Of(download.Parcels), source,
                matched => $"{matched} bâtiments dans la zone, au-delà de maxBuildings ({maxBuildings}) : rien n'a été importé. " +
                           "Réduisez la zone, ou augmentez maxBuildings en connaissance de cause.",
                actualMode == "parcel" ? building => touchesParcels(building.Polygons) : null).ConfigureAwait(false);
        }

        var first = download.Parcels[0].Polygons[0][0][0];
        var payload = new
        {
            coordinateSystem = string.IsNullOrWhiteSpace(coordinateSystem) ? null : coordinateSystem.Trim(),
            epsg = Geo.DefaultEpsg(first[0], first[1]),
            layerPrefix,
            attachObjectData,
            createLabels,
            labelHeight,
            zoomExtents,
            skipExisting,
            buildingSource = includeBuildings ? source : null,
            buildings3d,
            parcels = download.Parcels,
            sections = download.Sections,
            buildings = download.Buildings,
        };

        progress?.Report(new ProgressNotificationValue
        {
            Progress = download.Parcels.Count,
            Total = download.Parcels.Count,
            Message = $"Insertion de {download.Parcels.Count} parcelle(s) dans AutoCAD",
        });
        var result = await plugin.CallAsync("import_cadastre_geometry", payload, cancellationToken: cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            Mode = actualMode,
            Address = addressInfo,
            Downloaded = new
            {
                Parcels = download.Parcels.Count,
                SectionSheets = download.Sections.Count,
                Buildings = includeBuildings ? download.Buildings.Count : (int?)null,
            },
            Import = result,
            Source = "APICARTO (IGN), plan cadastral informatisé" + (includeBuildings
                ? source == "bdtopo" ? " ; bâtiments : BD TOPO (IGN, Géoplateforme)" : " ; bâtiments : Parcellaire Express (IGN, Géoplateforme)"
                : ""),
        }, PipeProtocol.JsonOptions);

        static BoundingBox CheckSize(BoundingBox zone, string advice) =>
            zone.WidthMeters <= MaxZoneMeters && zone.HeightMeters <= MaxZoneMeters ? zone
                : throw new McpException($"Zone de {zone.WidthMeters / 1000:0.#} × {zone.HeightMeters / 1000:0.#} km, au-delà de " +
                                         $"{MaxZoneMeters / 1000:0} km de côté. {advice}");

        Func<int, string> TooManyInZone(string where, string parameter) => matched =>
            $"{matched} parcelles {where}, au-delà de maxParcels ({maxParcels}) : rien n'a été importé. " +
            $"Réduisez « {parameter} », ou augmentez maxParcels en connaissance de cause (import plus long).";
    }

    [McpServerTool(Name = "search_cadastre_address", Title = "Rechercher une adresse et ses parcelles",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Géocode une adresse (Base Adresse Nationale) et donne, sans modifier le dessin : la parcelle cadastrale " +
                 "qui contient le point de l'adresse, puis les parcelles dans le rayon, triées par distance (identifiant, " +
                 "section, numéro, contenance). Signale les adresses ambiguës (même rue dans plusieurs communes) avec les " +
                 "autres correspondances. Utile avant import_cadastre en mode parcel.")]
    public async Task<string> SearchCadastreAddress(
        [Description("Adresse postale, par exemple « 12 rue Sainte-Catherine 33000 Bordeaux ».")] string address,
        [Description("Rayon de recherche des parcelles en mètres, de 1 à 500 (50 par défaut).")] double radius = 50.0,
        CancellationToken cancellationToken = default)
    {
        if (radius is < 1 or > 500)
            throw new McpException("Le rayon doit être compris entre 1 et 500 m.");

        var candidates = await Geocoder.SearchAsync(address, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
            throw new McpException($"Aucune adresse trouvée pour « {address} ».");

        var match = candidates[0];
        var zone = BoundingBox.Around(match.Lon, match.Lat, radius);
        var download = new Download(SearchMaxParcels, DefaultMaxBuildings, null, cancellationToken);
        await download.ParcelsAsync($"geom={zone.ToGeoJson()}",
            matched => $"{matched} parcelles dans ce rayon : réduisez-le (au plus {SearchMaxParcels}).").ConfigureAwait(false);

        var nearby = download.Parcels
            .Select(parcel => (Parcel: parcel, Distance: Geo.DistanceMeters(match.Lon, match.Lat, parcel.Polygons)))
            .Where(item => item.Distance <= radius)
            .OrderBy(item => item.Distance)
            .ToList();
        var containing = Geo.ParcelAt(match.Lon, match.Lat, nearby);

        var notes = new List<string>();
        if (Geocoder.Rivals(candidates).Count > 0)
            notes.Add("Adresse ambiguë : d'autres communes ont la même correspondance (voir otherCandidates). Précisez le code postal ou la commune.");
        if (match.Score < 0.5)
            notes.Add($"Correspondance faible (score {match.Score:0.00}) : vérifiez que l'adresse trouvée est la bonne.");
        if (match.Type is "street" or "municipality" or "locality")
            notes.Add("Adresse localisée à la rue ou à la commune, pas au numéro : le point n'est pas sur une parcelle précise.");
        if (containing is not null && nearby[0].Distance > 0)
            notes.Add($"Le point de l'adresse est sur la limite de la parcelle {containing.Id} (point placé en façade).");
        if (containing is null && nearby.Count > 0)
            notes.Add($"Le point de l'adresse ne tombe dans aucune parcelle (voirie ou domaine public non cadastré) ; " +
                      $"la plus proche est {nearby[0].Parcel.Id}, à {nearby[0].Distance:0.#} m.");

        return JsonSerializer.Serialize(new
        {
            Address = Geocoder.Describe(match),
            OtherCandidates = candidates.Count > 1 ? candidates.Skip(1).Select(Geocoder.Describe).ToArray() : null,
            ParcelAtAddress = containing is null ? null : Summarize(containing, 0),
            RadiusMeters = radius,
            ParcelCount = nearby.Count,
            Parcels = nearby.Select(item => Summarize(item.Parcel, item.Distance)).ToArray(),
            Notes = notes.Count > 0 ? notes : null,
        }, PipeProtocol.JsonOptions);
    }

    private static object Summarize(ParcelData parcel, double distance) => new
    {
        parcel.Id,
        parcel.Commune,
        parcel.CodeInsee,
        parcel.Section,
        parcel.Numero,
        ContenanceM2 = parcel.Contenance,
        DistanceMeters = Math.Round(distance, 1),
    };

    // ----- Normalisation des identifiants cadastraux -----

    private static string NormalizeInsee(string? codeInsee)
    {
        var code = codeInsee?.Trim().ToUpperInvariant() ?? "";
        if (code.Length == 0)
            throw new McpException("Le mode « parcel » demande le code INSEE de la commune (codeInsee).");
        if (!Regex.IsMatch(code, "^(\\d{5}|2[AB]\\d{3})$"))
            throw new McpException($"« {codeInsee} » n'est pas un code INSEE : 5 caractères, par exemple 33063 (2A004 en Corse).");
        return code;
    }

    /// <summary>APICARTO attend 2 caractères : « A » devient « 0A ».</summary>
    private static string NormalizeSection(string? section)
    {
        var code = section?.Trim().ToUpperInvariant() ?? "";
        if (code.Length == 0)
            throw new McpException("Le mode « parcel » demande la section cadastrale (section).");
        if (!Regex.IsMatch(code, "^[0-9A-Z]{1,2}$"))
            throw new McpException($"« {section} » n'est pas une section cadastrale : 1 ou 2 lettres ou chiffres (A, 0A, KM).");
        return code.PadLeft(2, '0');
    }

    /// <summary>Numéros sur 4 chiffres : « 81 » devient « 0081 ».</summary>
    private static List<string> NormalizeNumbers(string? numero)
    {
        var numbers = new List<string>();
        foreach (var part in (numero ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Regex.IsMatch(part, "^\\d{1,4}$"))
                throw new McpException($"« {part} » n'est pas un numéro de parcelle (1 à 4 chiffres).");
            var padded = part.PadLeft(4, '0');
            if (!numbers.Contains(padded))
                numbers.Add(padded);
        }

        return numbers;
    }
}

// ----- Données échangées avec le plug-in (sérialisées en camelCase) -----

/// <summary>Polygones : liste de polygones, chacun = anneau extérieur puis trous ; points [longitude, latitude].</summary>
public sealed record ParcelData(
    string Id, string? Commune, string? CodeInsee, string? Section, string? Numero, double Contenance, List<List<List<double[]>>> Polygons);

public sealed record SectionData(
    string Id, string? Commune, string? CodeInsee, string? Section, int? Feuille, List<List<List<double[]>>> Polygons);

/// <summary>
/// Bâtiment du plan cadastral (source « cadastre » : type et code INSEE seulement) ou de la BD TOPO (« bdtopo »).
/// Hauteur en mètres, altitudes NGF en mètres ; null si inconnues.
/// </summary>
public sealed record BuildingData(
    string Id, string Source, string? Type, bool Light, string? Usage, double? Height, int? Floors, int? Dwellings,
    double? GroundAltitude, double? RoofAltitude, string? Rnb, string? CodeInsee, List<List<List<double[]>>> Polygons);

/// <summary>
/// Téléchargements d'un appel d'outil : parcelles et feuilles par APICARTO, bâtiments par le WFS de la Géoplateforme ;
/// pagination, plafond, dédoublonnage par identifiant.
/// </summary>
internal sealed class Download(int maxParcels, int maxBuildings, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
{
    private const string ApicartoUrl = "https://apicarto.ign.fr/api/cadastre/";
    private const string ApicartoService = "APICARTO (IGN)";
    private const int ApicartoPageSize = 500;
    private const string WfsUrl = "https://data.geopf.fr/wfs/ows";
    private const string WfsService = "La Géoplateforme de l'IGN (WFS)";
    private const int WfsPageSize = 1000;

    private readonly Dictionary<string, ParcelData> _parcels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SectionData> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuildingData> _buildings = new(StringComparer.Ordinal);

    public List<ParcelData> Parcels => [.. _parcels.Values];

    public List<SectionData> Sections => [.. _sections.Values];

    public List<BuildingData> Buildings => [.. _buildings.Values];

    /// <summary>
    /// Bâtiments qui touchent la zone, du plan cadastral (Parcellaire Express) ou de la BD TOPO. La pagination du WFS
    /// n'est stable qu'avec un tri explicite.
    /// </summary>
    public async Task BuildingsAsync(BoundingBox zone, string source, Func<int, string> tooMany, Func<BuildingData, bool>? keep = null)
    {
        var (typeName, sortBy, parse) = source == "bdtopo"
            ? ("BDTOPO_V3:batiment", "cleabs", (Func<JsonElement, BuildingData?>)ParseTopoBuilding)
            : ("CADASTRALPARCELS.PARCELLAIRE_EXPRESS:batiment", "gid", ParseCadastreBuilding);
        var query = $"SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAMES={typeName}&BBOX={zone.ToWfsBbox()}" +
                    $"&SRSNAME=EPSG:4326&OUTPUTFORMAT=application/json&SORTBY={sortBy}";

        await FetchAsync(start => $"{WfsUrl}?{query}&COUNT={WfsPageSize}&STARTINDEX={start}", WfsService, "bâtiments",
            WfsPageSize, maxBuildings, tooMany, feature =>
            {
                if (parse(feature) is { } building && (keep?.Invoke(building) ?? true))
                    _buildings.TryAdd(building.Id, building);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Parcelles d'une requête, filtrées ici aussi : APICARTO ignore parfois un critère (section inexistante) au
    /// lieu de ne rien renvoyer.
    /// </summary>
    public async Task ParcelsAsync(string query, Func<int, string> tooMany, Func<ParcelData, bool>? keep = null)
    {
        await FetchApicartoAsync("parcelle", query, maxParcels, tooMany, feature =>
        {
            if (ParseParcel(feature) is { } parcel && (keep?.Invoke(parcel) ?? true))
                _parcels.TryAdd(parcel.Id, parcel);
        }).ConfigureAwait(false);

        if (_parcels.Count > maxParcels)
            throw new McpException($"{_parcels.Count} parcelles au total, au-delà de maxParcels ({maxParcels}) : rien n'a été importé. Réduisez la zone.");
    }

    public Task SectionsAsync(string query, Func<SectionData, bool>? keep = null) =>
        FetchApicartoAsync("division", query, 1000, matched => $"{matched} feuilles cadastrales dans la zone : réduisez-la.", feature =>
        {
            if (ParseSection(feature) is { } section && (keep?.Invoke(section) ?? true))
                _sections.TryAdd(section.Id, section);
        });

    private Task FetchApicartoAsync(string resource, string query, int max, Func<int, string> tooMany, Action<JsonElement> add) =>
        FetchAsync(start => $"{ApicartoUrl}{resource}?{query}&_limit={ApicartoPageSize}&_start={start}", ApicartoService,
            resource, ApicartoPageSize, max, tooMany, add);

    /// <summary>Lecture page par page ; refus avant tout téléchargement si le total annoncé dépasse le plafond.</summary>
    private async Task FetchAsync(Func<int, string> pageUrl, string service, string what, int pageSize, int max,
        Func<int, string> tooMany, Action<JsonElement> add)
    {
        var matched = -1;
        for (var start = 0; ; start += pageSize)
        {
            using var document = await WebJson.GetAsync(pageUrl(start), service, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var features = root.TryGetProperty("features", out var array) && array.ValueKind == JsonValueKind.Array ? array : default;
            var count = features.ValueKind == JsonValueKind.Array ? features.GetArrayLength() : 0;
            if (matched < 0)
            {
                matched = root.TryGetProperty("numberMatched", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : count;
                if (matched > max)
                    throw new McpException(tooMany(matched));
            }

            if (count > 0)
            {
                foreach (var feature in features.EnumerateArray())
                    add(feature);
            }

            progress?.Report(new ProgressNotificationValue
            {
                Progress = Math.Min(start + count, matched),
                Total = matched,
                Message = $"Téléchargement {what} : {Math.Min(start + count, matched)} / {matched}",
            });

            if (count < pageSize || start + count >= matched)
                return;
        }
    }

    /// <summary>Bâti du plan cadastral : « Bâtiment en dur » ou « Construction légère ».</summary>
    private static BuildingData? ParseCadastreBuilding(JsonElement feature)
    {
        var polygons = Geo.ReadPolygons(feature);
        if (polygons.Count == 0 || !feature.TryGetProperty("properties", out var properties) || Number(properties, "gid") is not { } gid)
            return null;

        var type = Text(properties, "type");
        return new BuildingData(string.Create(CultureInfo.InvariantCulture, $"PCI-{gid:0}"), "cadastre", type,
            type?.Contains("légère", StringComparison.OrdinalIgnoreCase) == true, null, null, null, null, null, null, null,
            Text(properties, "code_insee"), polygons);
    }

    private static BuildingData? ParseTopoBuilding(JsonElement feature)
    {
        var polygons = Geo.ReadPolygons(feature);
        if (polygons.Count == 0 || !feature.TryGetProperty("properties", out var properties) || Text(properties, "cleabs") is not { } id)
            return null;

        var usage = string.Join(" / ", new[] { Text(properties, "usage_1"), Text(properties, "usage_2") }.Where(u => !string.IsNullOrEmpty(u)));
        return new BuildingData(id, "bdtopo", Text(properties, "nature"),
            properties.TryGetProperty("construction_legere", out var light) && light.ValueKind == JsonValueKind.True,
            usage.Length > 0 ? usage : null, Number(properties, "hauteur"),
            Number(properties, "nombre_d_etages") is { } floors ? (int)floors : null,
            Number(properties, "nombre_de_logements") is { } dwellings ? (int)dwellings : null,
            Number(properties, "altitude_minimale_sol"), Number(properties, "altitude_maximale_toit"),
            Text(properties, "identifiants_rnb"), null, polygons);
    }

    private static double? Number(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static ParcelData? ParseParcel(JsonElement feature)
    {
        var polygons = Geo.ReadPolygons(feature);
        if (polygons.Count == 0 || !feature.TryGetProperty("properties", out var properties))
            return null;

        var id = Text(properties, "idu") ?? (feature.TryGetProperty("id", out var fid) ? fid.ToString() : null);
        if (string.IsNullOrEmpty(id))
            return null;

        return new ParcelData(id, Text(properties, "nom_com"), Text(properties, "code_insee"), Text(properties, "section"),
            Text(properties, "numero"),
            properties.TryGetProperty("contenance", out var area) && area.ValueKind == JsonValueKind.Number ? area.GetDouble() : 0,
            polygons);
    }

    private static SectionData? ParseSection(JsonElement feature)
    {
        var polygons = Geo.ReadPolygons(feature);
        if (polygons.Count == 0 || !feature.TryGetProperty("properties", out var properties))
            return null;

        var sheet = properties.TryGetProperty("feuille", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : (int?)null;
        var id = $"{Text(properties, "code_insee")}{Text(properties, "com_abs")}{Text(properties, "section")}{sheet ?? 0:00}";
        return new SectionData(id, Text(properties, "nom_com"), Text(properties, "code_insee"), Text(properties, "section"), sheet, polygons);
    }

    private static string? Text(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

// ----- Base Adresse Nationale -----

public sealed record AddressCandidate(string Label, string Type, double Score, string? City, string? Postcode, string? CityCode, double Lon, double Lat);

internal static class Geocoder
{
    private const string Service = "Le service d'adresses (BAN)";

    /// <summary>Géoplateforme de l'IGN, puis l'ancienne adresse de l'API Adresse si la première ne répond pas.</summary>
    private static readonly string[] Endpoints =
        ["https://data.geopf.fr/geocodage/search", "https://api-adresse.data.gouv.fr/search/"];

    public static async Task<List<AddressCandidate>> SearchAsync(string address, CancellationToken cancellationToken)
    {
        var query = address?.Trim() ?? "";
        if (query.Length is < 3 or > 200)
            throw new McpException("L'adresse doit compter de 3 à 200 caractères.");

        McpException? failure = null;
        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var document = await WebJson.GetAsync(
                    $"{endpoint}?q={Uri.EscapeDataString(query)}&limit=5", Service, cancellationToken).ConfigureAwait(false);
                return Parse(document.RootElement);
            }
            catch (McpException ex)
            {
                failure ??= ex;
            }
        }

        throw failure!;
    }

    /// <summary>Pour un import : refuse une adresse introuvable, ambiguë ou mal reconnue plutôt que d'importer au mauvais endroit.</summary>
    public static AddressCandidate ChooseStrict(List<AddressCandidate> candidates, string address)
    {
        if (candidates.Count == 0)
            throw new McpException($"Aucune adresse trouvée pour « {address} ».");

        var best = candidates[0];
        var rivals = Rivals(candidates);
        if (rivals.Count > 0)
            throw new McpException(
                $"Adresse ambiguë : « {address} » correspond aussi bien à « {best.Label} » qu'à " +
                $"{string.Join(", ", rivals.Select(c => $"« {c.Label} »"))}. Précisez le code postal ou la commune.");
        if (best.Score < 0.5)
            throw new McpException(
                $"Correspondance trop faible pour « {address} » : le meilleur résultat est « {best.Label} » " +
                $"(score {best.Score:0.00}). Précisez l'adresse.");
        return best;
    }

    /// <summary>Autres communes avec une correspondance presque aussi bonne que la première.</summary>
    public static List<AddressCandidate> Rivals(List<AddressCandidate> candidates) =>
        candidates.Count < 2 ? [] : candidates.Skip(1)
            .Where(c => c.Score >= candidates[0].Score - 0.03 && c.CityCode != candidates[0].CityCode).ToList();

    public static object Describe(AddressCandidate candidate) => new
    {
        candidate.Label,
        candidate.Type,
        Score = Math.Round(candidate.Score, 2),
        candidate.City,
        candidate.Postcode,
        CodeInsee = candidate.CityCode,
        Longitude = Math.Round(candidate.Lon, 6),
        Latitude = Math.Round(candidate.Lat, 6),
    };

    private static List<AddressCandidate> Parse(JsonElement root)
    {
        var candidates = new List<AddressCandidate>();
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return candidates;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || !geometry.TryGetProperty("coordinates", out var point) ||
                point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2 ||
                !feature.TryGetProperty("properties", out var properties))
                continue;

            string? Text(string name) => properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            candidates.Add(new AddressCandidate(
                Text("label") ?? "", Text("type") ?? "",
                properties.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0,
                Text("city"), Text("postcode"), Text("citycode"),
                point[0].GetDouble(), point[1].GetDouble()));
        }

        return candidates;
    }
}

// ----- HTTP -----

internal static class WebJson
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout,
        DefaultRequestHeaders = { { "User-Agent", "McpMap3D (connecteur MCP pour AutoCAD Map 3D)" } },
    };

    /// <summary>GET JSON avec deux nouvelles tentatives sur coupure réseau, erreur 5xx ou 429 (APICARTO coupe parfois la connexion).</summary>
    public static async Task<JsonDocument> GetAsync(string url, string service, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new McpException($"{service} est injoignable ({ex.Message}). Vérifiez la connexion Internet, puis réessayez.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpException($"{service} n'a pas répondu en {Timeout.TotalSeconds:0} s : il est peut-être surchargé. " +
                                       "Réessayez plus tard, ou réduisez la zone.");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (JsonException)
                    {
                        throw new McpException($"{service} a renvoyé une réponse illisible.");
                    }
                }

                var status = (int)response.StatusCode;
                if ((status >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new McpException($"{service} a refusé la requête (HTTP {status}) : {Explain(body)}");
            }
        }
    }

    /// <summary>Messages d'erreur du service (champs « msg » ou « message »), sinon début de la réponse.</summary>
    private static string Explain(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var messages = new List<string>();
            Collect(document.RootElement, messages);
            if (messages.Count > 0)
                return string.Join(" ; ", messages.Distinct());
        }
        catch (JsonException)
        {
            // Réponse non JSON.
        }

        return body.Length > 300 ? body[..300] + "…" : body;

        static void Collect(JsonElement element, List<string> messages)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Name is "msg" or "message" or "detail")
                        messages.Add(property.Value.GetString()!);
                    else
                        Collect(property.Value, messages);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Collect(item, messages);
            }
        }
    }
}

// ----- Géométrie en longitude/latitude -----

internal readonly record struct BoundingBox(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    private const double MetersPerDegreeLat = 110_574.0;

    private double MetersPerDegreeLon => 111_320.0 * Math.Max(Math.Cos((MinLat + MaxLat) / 2 * Math.PI / 180), 0.01);

    public double WidthMeters => (MaxLon - MinLon) * MetersPerDegreeLon;

    public double HeightMeters => (MaxLat - MinLat) * MetersPerDegreeLat;

    public static BoundingBox Around(double lon, double lat, double meters) => new BoundingBox(lon, lat, lon, lat).Expand(meters);

    public static BoundingBox Of(IEnumerable<ParcelData> parcels)
    {
        var points = parcels.SelectMany(p => p.Polygons).SelectMany(polygon => polygon[0]).ToList();
        return new BoundingBox(points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1]));
    }

    public BoundingBox Expand(double meters)
    {
        var dLon = meters / MetersPerDegreeLon;
        var dLat = meters / MetersPerDegreeLat;
        return new BoundingBox(MinLon - dLon, MinLat - dLat, MaxLon + dLon, MaxLat + dLat);
    }

    /// <summary>BBOX d'une requête WFS 2.0 : en EPSG:4326, la latitude vient en premier.</summary>
    public string ToWfsBbox() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{MinLat:0.0000000},{MinLon:0.0000000},{MaxLat:0.0000000},{MaxLon:0.0000000},urn:ogc:def:crs:EPSG::4326");

    public string ToGeoJson()
    {
        string P(double lon, double lat) => string.Create(CultureInfo.InvariantCulture, $"[{lon:0.0000000},{lat:0.0000000}]");
        var ring = $"{P(MinLon, MinLat)},{P(MaxLon, MinLat)},{P(MaxLon, MaxLat)},{P(MinLon, MaxLat)},{P(MinLon, MinLat)}";
        return Uri.EscapeDataString($"{{\"type\":\"Polygon\",\"coordinates\":[[{ring}]]}}");
    }
}

internal static class Geo
{
    /// <summary>Système de coordonnées légal de la zone (code EPSG) : Lambert-93 en métropole, UTM local outre-mer.</summary>
    public static int DefaultEpsg(double lon, double lat) => (lon, lat) switch
    {
        ( >= -63.2 and <= -60.7, >= 14.3 and <= 18.2) => 5490, // Antilles : RGAF09 / UTM 20N
        ( >= -54.7 and <= -51.5, >= 2.0 and <= 6.0) => 2972,   // Guyane : RGFG95 / UTM 22N
        ( >= 55.1 and <= 56.0, >= -21.5 and <= -20.8) => 2975, // La Réunion : RGR92 / UTM 40S
        ( >= 44.9 and <= 45.4, >= -13.1 and <= -12.5) => 4471, // Mayotte : RGM04 / UTM 38S
        _ => 2154,                                             // Métropole et Corse : Lambert-93
    };

    public static List<List<List<double[]>>> ReadPolygons(JsonElement feature)
    {
        var polygons = new List<List<List<double[]>>>();
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
            !geometry.TryGetProperty("type", out var type) || !geometry.TryGetProperty("coordinates", out var coordinates))
            return polygons;

        switch (type.GetString())
        {
            case "Polygon":
                AddPolygon(coordinates, polygons);
                break;
            case "MultiPolygon":
                foreach (var polygon in coordinates.EnumerateArray())
                    AddPolygon(polygon, polygons);
                break;
        }

        return polygons;

        static void AddPolygon(JsonElement rings, List<List<List<double[]>>> polygons)
        {
            var polygon = rings.EnumerateArray()
                .Select(ring => ring.EnumerateArray()
                    .Where(point => point.ValueKind == JsonValueKind.Array && point.GetArrayLength() >= 2)
                    .Select(point => new[] { point[0].GetDouble(), point[1].GetDouble() })
                    .ToList())
                .Where(ring => ring.Count >= 4)
                .ToList();
            if (polygon.Count > 0)
                polygons.Add(polygon);
        }
    }

    /// <summary>
    /// Points de la BAN placés sur la façade, donc souvent sur la limite de la parcelle : au-delà de la parcelle qui
    /// contient le point, on retient la plus proche à moins de 2 m.
    /// </summary>
    public const double FacadeToleranceMeters = 2.0;

    public static ParcelData? ParcelAt(double lon, double lat, IEnumerable<ParcelData> parcels) =>
        ParcelAt(lon, lat, parcels.Select(parcel => (parcel, DistanceMeters(lon, lat, parcel.Polygons))).OrderBy(item => item.Item2).ToList());

    public static ParcelData? ParcelAt(double lon, double lat, List<(ParcelData Parcel, double Distance)> sortedByDistance) =>
        sortedByDistance.Count > 0 && sortedByDistance[0].Distance <= FacadeToleranceMeters ? sortedByDistance[0].Parcel : null;

    /// <summary>Distance en mètres d'un point aux polygones : 0 à l'intérieur (trous exclus), sinon au bord le plus proche.</summary>
    public static double DistanceMeters(double lon, double lat, List<List<List<double[]>>> polygons)
    {
        var kx = 111_320.0 * Math.Cos(lat * Math.PI / 180);
        const double ky = 110_574.0;
        (double X, double Y) Local(double[] p) => ((p[0] - lon) * kx, (p[1] - lat) * ky);

        var best = double.MaxValue;
        foreach (var polygon in polygons)
        {
            var rings = polygon.Select(ring => ring.Select(Local).ToList()).ToList();
            if (ContainsOrigin(rings[0]) && !rings.Skip(1).Any(ContainsOrigin))
                return 0;

            foreach (var ring in rings)
            {
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    best = Math.Min(best, DistanceToSegment(ring[j], ring[i]));
            }
        }

        return best;
    }

    /// <summary>
    /// Le bâtiment touche-t-il l'une des parcelles ? Son centre ou un de ses sommets est dans une parcelle, ou un sommet
    /// de parcelle est dans le bâtiment. Calcul plan en longitude/latitude, suffisant à l'échelle d'une parcelle.
    /// </summary>
    public static Func<List<List<List<double[]>>>, bool> TouchesAny(IReadOnlyList<ParcelData> parcels)
    {
        var rings = parcels.SelectMany(parcel => parcel.Polygons).Select(polygon => (Ring: polygon[0], Bounds: Bounds(polygon[0]))).ToList();
        return building =>
        {
            var outlines = building.Select(polygon => polygon[0]).ToList();
            var points = outlines.SelectMany(ring => ring).ToList();
            var bounds = Bounds(points);
            var center = new[] { points.Average(p => p[0]), points.Average(p => p[1]) };
            foreach (var (ring, box) in rings)
            {
                if (box.MaxX < bounds.MinX || box.MinX > bounds.MaxX || box.MaxY < bounds.MinY || box.MinY > bounds.MaxY)
                    continue;
                if (InsideRing(center, ring) || points.Any(p => InsideRing(p, ring)) || outlines.Any(outline => ring.Any(q => InsideRing(q, outline))))
                    return true;
            }

            return false;
        };
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(List<double[]> points) =>
        (points.Min(p => p[0]), points.Min(p => p[1]), points.Max(p => p[0]), points.Max(p => p[1]));

    private static bool InsideRing(double[] point, List<double[]> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            if ((ring[i][1] > point[1]) != (ring[j][1] > point[1]) &&
                point[0] < (ring[j][0] - ring[i][0]) * (point[1] - ring[i][1]) / (ring[j][1] - ring[i][1]) + ring[i][0])
                inside = !inside;
        }

        return inside;
    }

    private static bool ContainsOrigin(List<(double X, double Y)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (a, b) = (ring[i], ring[j]);
            if ((a.Y > 0) != (b.Y > 0) && 0 < (b.X - a.X) * (0 - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    private static double DistanceToSegment((double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(-(a.X * dx + a.Y * dy) / lengthSquared, 0, 1);
        var (x, y) = (a.X + t * dx, a.Y + t * dy);
        return Math.Sqrt(x * x + y * y);
    }
}

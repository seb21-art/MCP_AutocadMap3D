using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map.CoordinateSystem;
using McpMap3D.Shared;
using OSGeo.MapGuide;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Système de coordonnées du dessin et catalogue CS-MAP de la plateforme géospatiale de Map 3D.
/// Attribuer un système ne déplace aucun objet : cela déclare dans quel système sont exprimées les coordonnées.
/// Les objets MapGuide ont une méthode Dispose sans implémenter IDisposable : ceux créés en nombre sont libérés
/// explicitement, les autres laissés au ramasse-miettes.
/// </summary>
internal static class CoordinateSystemTools
{
    private const int MaxResults = 100;
    private const int MaxCompatibilityChecks = 600;

    public static object GetCoordinateSystem(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            return GetCoordinateSystemClassic(context);
        return GetCoordinateSystemMap3D(context);
    }

    private static object GetCoordinateSystemClassic(ToolContext context)
    {
        var extents = DrawingExtents(context.Database);
        var inLambert93 = extents is not null && Lambert93.IsLambert93Range(extents.MinX, extents.MinY, extents.MaxX, extents.MaxY);
        return new
        {
            Assigned = false,
            MapAvailable = false,
            DrawingExtents = extents?.Describe(),
            EstimatedSystem = inLambert93 ? "Lambert93 (d'après l'étendue du dessin)" : null,
            DrawingCenterLonLat = inLambert93 && extents is not null
                ? FormatLonLat(Lambert93.ToWgs84(extents.CenterX, extents.CenterY))
                : null,
            Message = "AutoCAD classique : la gestion native des systèmes CS-MAP requiert AutoCAD Map 3D. La projection Lambert-93 est intégrée de façon autonome.",
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GetCoordinateSystemMap3D(ToolContext context)
    {
        var extents = DrawingExtents(context.Database);
        var code = AcMapCoordsysCore.GetCoordinateSystem(context.Database);
        if (string.IsNullOrEmpty(code))
            return new { Assigned = false, DrawingExtents = extents?.Describe() };

        var system = TryCreate(new MgCoordinateSystemFactory(), code);
        if (system is null)
            return new { Assigned = true, Code = code, Known = false };

        return new
        {
            Assigned = true,
            Known = true,
            System = Describe(system),
            DrawingExtents = extents?.Describe(),
            DrawingCenterLonLat = extents is null ? null : ToLonLat(system, extents.CenterX, extents.CenterY),
            Compatible = IsCompatible(system, extents),
        };
    }

    /// <summary>
    /// Étendue du dessin en longitude/latitude (WGS84), pour les services cartographiques. Les quatre coins sont
    /// convertis : en projection conique, le rectangle du dessin n'est pas aligné sur les méridiens.
    /// </summary>
    public static object GetDrawingExtentsLonLat(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            return GetDrawingExtentsLonLatClassic(context);
        return GetDrawingExtentsLonLatMap3D(context);
    }

    private static object GetDrawingExtentsLonLatClassic(ToolContext context)
    {
        var database = context.Database;
        var extents = DrawingExtents(database)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin est vide : il n'a pas d'emprise. Importez par adresse ou par parcelle.");

        if (!Lambert93.IsLambert93Range(extents.MinX, extents.MinY, extents.MaxX, extents.MaxY))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'emprise du dessin ({extents.Describe()}) n'est pas dans l'étendue métropolitaine de Lambert-93. " +
                "En version AutoCAD classique, l'emprise du dessin doit être en Lambert-93 (X: 50 000..1 350 000, Y: 6 000 000..7 200 000). " +
                "Sinon, importez par adresse ou par parcelle.");

        var corners = new List<(double Lon, double Lat)>();
        foreach (var (x, y) in extents.Corners())
        {
            var (lon, lat) = Lambert93.ToWgs84(x, y);
            corners.Add((lon, lat));
        }

        return new
        {
            Code = "Lambert93",
            MinLon = corners.Min(c => c.Lon),
            MinLat = corners.Min(c => c.Lat),
            MaxLon = corners.Max(c => c.Lon),
            MaxLat = corners.Max(c => c.Lat),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GetDrawingExtentsLonLatMap3D(ToolContext context)
    {
        var database = context.Database;
        var code = AcMapCoordsysCore.GetCoordinateSystem(database);
        if (string.IsNullOrEmpty(code))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin n'a pas de système de coordonnées : son emprise ne peut pas être située sur la carte. " +
                "Attribuez-en un avec set_coordinate_system, ou importez par adresse ou par parcelle.");

        var system = TryCreate(new MgCoordinateSystemFactory(), code)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Système de coordonnées « {code} » inconnu.");

        var extents = DrawingExtents(database)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin est vide : il n'a pas d'emprise. Importez par adresse ou par parcelle.");

        var corners = new List<(double Lon, double Lat)>();
        foreach (var (x, y) in extents.Corners())
        {
            try
            {
                var lonLat = system.ConvertToLonLat(x, y);
                corners.Add((lonLat.GetX(), lonLat.GetY()));
                lonLat.Dispose();
            }
            catch (MgException)
            {
                // Coin hors du domaine du système : signalé ci-dessous s'il n'en reste aucun.
            }
        }

        if (corners.Count < 4 || corners.Any(c => double.IsNaN(c.Lon) || double.IsNaN(c.Lat)))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'emprise du dessin ({extents.Describe()}) ne se convertit pas en longitude/latitude avec « {code} » : " +
                "le système attribué ne correspond pas aux coordonnées des objets.");

        return new
        {
            Code = code,
            MinLon = corners.Min(c => c.Lon),
            MinLat = corners.Min(c => c.Lat),
            MaxLon = corners.Max(c => c.Lon),
            MaxLat = corners.Max(c => c.Lat),
        };
    }

    /// <summary>
    /// Points (longitude, latitude) exprimés dans le système du dessin, par exemple pour cadrer la vue sur une adresse.
    /// Sans Map 3D, le dessin est supposé en Lambert-93, comme pour l'import du cadastre.
    /// </summary>
    public static List<(double X, double Y)> ProjectLonLat(Database database, IReadOnlyList<(double Lon, double Lat)> points)
    {
        if (!MapInfo.IsAvailable)
            return ProjectLonLatClassic(points);
        return ProjectLonLatMap3D(database, points);
    }

    private static List<(double X, double Y)> ProjectLonLatClassic(IReadOnlyList<(double Lon, double Lat)> points)
    {
        foreach (var (lon, lat) in points)
        {
            if (lon < -6.0 || lon > 11.0 || lat < 41.0 || lat > 52.0)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le point (longitude {Format.Number(lon)}°, latitude {Format.Number(lat)}°) est hors de France métropolitaine : " +
                    "sans Map 3D, seule la projection Lambert-93 est disponible. Vérifiez aussi l'ordre : longitude puis latitude.");
        }

        return points.Select(point => Lambert93.ToLambert93(point.Lon, point.Lat)).ToList();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(double X, double Y)> ProjectLonLatMap3D(Database database, IReadOnlyList<(double Lon, double Lat)> points)
    {
        var code = AcMapCoordsysCore.GetCoordinateSystem(database);
        if (string.IsNullOrEmpty(code))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin n'a pas de système de coordonnées : une longitude/latitude ne peut pas y être placée. " +
                "Attribuez-en un avec set_coordinate_system, ou donnez les coordonnées du dessin.");

        var system = TryCreate(new MgCoordinateSystemFactory(), code)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Système de coordonnées « {code} » inconnu.");
        try
        {
            var projected = new List<(double X, double Y)>(points.Count);
            foreach (var (lon, lat) in points)
            {
                if (!IsInDomain(system, lon, lat))
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Le point (longitude {Format.Number(lon)}°, latitude {Format.Number(lat)}°) est hors du domaine du système « {code} » " +
                        $"du dessin ({DescribeLonLatRange(system)}). Vérifiez l'ordre : longitude puis latitude.");
                projected.Add(FromLonLat(system, lon, lat)
                    ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Le point (longitude {Format.Number(lon)}°, latitude {Format.Number(lat)}°) ne se convertit pas dans « {code} »."));
            }

            return projected;
        }
        finally
        {
            system.Dispose();
        }
    }

    /// <summary>Le point (longitude, latitude) est-il dans le domaine d'usage du système ?</summary>
    public static bool IsInDomain(MgCoordinateSystem system, double lon, double lat)
    {
        var (lonMin, lonMax, latMin, latMax) = (system.GetLonMin(), system.GetLonMax(), system.GetLatMin(), system.GetLatMax());
        return !(lonMax > lonMin && latMax > latMin) || (lon >= lonMin && lon <= lonMax && lat >= latMin && lat <= latMax);
    }

    public static bool Exists(string code)
    {
        var system = TryCreate(new MgCoordinateSystemFactory(), code);
        system?.Dispose();
        return system is not null;
    }

    /// <summary>Code du catalogue correspondant à un code EPSG (null si aucun).</summary>
    public static string? CodeForEpsg(int epsg) =>
        Rank(new MgCoordinateSystemFactory(), epsg.ToString(CultureInfo.InvariantCulture)).FirstOrDefault()?.Code;

    public static object SearchCoordinateSystems(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            return SearchCoordinateSystemsClassic(context, args);
        return SearchCoordinateSystemsMap3D(context, args);
    }

    private static object SearchCoordinateSystemsClassic(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var query = reader.RequireString("query").Trim();
        var extents = DrawingExtents(context.Database);
        var inLambert93 = extents is not null && Lambert93.IsLambert93Range(extents.MinX, extents.MinY, extents.MaxX, extents.MaxY);

        var matches = new List<object>();
        if (query.Length == 0 || "Lambert93".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            "2154".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            "RGF93".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            "France".Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(new
            {
                Code = "Lambert93",
                Description = "RGF93 / Lambert-93 (projection autonome intégrée en C#)",
                Epsg = (int?)2154,
                Units = "Meter",
                Projection = "Lambert Conformal Conic",
                Location = "France métropolitaine",
                Compatible = inLambert93 ? true : (bool?)null,
            });
        }

        return new
        {
            Query = query,
            Matches = matches.Count,
            Count = matches.Count,
            DrawingExtents = extents?.Describe(),
            CatalogSize = 1,
            Results = matches,
            MapAvailable = false,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object SearchCoordinateSystemsMap3D(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var query = reader.RequireString("query").Trim();
        var limit = reader.GetInt("limit", 20, min: 1, max: MaxResults);
        var compatibleOnly = reader.GetBool("compatibleOnly", false);
        var extents = DrawingExtents(context.Database);
        if (compatibleOnly && extents is null)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le dessin est vide : impossible de filtrer les systèmes compatibles avec son étendue.");

        var factory = new MgCoordinateSystemFactory();
        var ranked = Rank(factory, query);

        var results = new List<object>();
        var checks = 0;
        foreach (var entry in ranked)
        {
            if (results.Count >= limit || checks >= MaxCompatibilityChecks)
                break;

            bool? compatible = null;
            if (extents is not null)
            {
                checks++;
                var system = TryCreate(factory, entry.Code);
                compatible = system is null ? null : IsCompatible(system, extents);
                system?.Dispose();
            }

            if (compatibleOnly && compatible != true)
                continue;

            results.Add(new
            {
                entry.Code,
                entry.Description,
                Epsg = entry.Epsg > 0 ? entry.Epsg : (int?)null,
                entry.Units,
                entry.Projection,
                Location = string.IsNullOrEmpty(entry.Location) ? null : entry.Location,
                Compatible = compatible,
            });
        }

        return new
        {
            Query = query,
            Matches = ranked.Count,
            Count = results.Count,
            DrawingExtents = extents?.Describe(),
            CatalogSize = Catalog.Entries.Count,
            Results = results,
        };
    }

    public static object SetCoordinateSystem(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable,
                "L'attribution d'un système de coordonnées CS-MAP requiert AutoCAD Map 3D. " +
                "En version AutoCAD classique, la projection Lambert-93 est assurée de manière autonome lors de l'import du cadastre.");
        return SetCoordinateSystemMap3D(context, args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object SetCoordinateSystemMap3D(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var code = (reader.GetString("code") ?? throw new PipeException(PipeErrorCodes.InvalidParams,
            "Le paramètre « code » est obligatoire (chaîne vide pour retirer le système).")).Trim();
        var force = reader.GetBool("force", false);
        var database = context.Database;
        var previous = AcMapCoordsysCore.GetCoordinateSystem(database);
        previous = string.IsNullOrEmpty(previous) ? null : previous;

        if (code.Length == 0)
        {
            AcMapCoordsysCore.SetCoordinateSystem("", database);
            return new { Removed = true, Previous = previous };
        }

        var system = TryCreate(new MgCoordinateSystemFactory(), code) ?? throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Système de coordonnées « {code} » inconnu du catalogue. Cherchez le bon code avec search_coordinate_systems.");

        // Les limites du dessin se recalculent ici : on est en écriture, en contexte commande.
        database.UpdateExt(true);
        var extents = DrawingExtents(database);
        var compatible = IsCompatible(system, extents);
        if (compatible == false && !force)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'étendue du dessin ({extents!.Describe()}) ne correspond pas au domaine d'usage de {system.GetCsCode()} " +
                $"({DescribeLonLatRange(system)}) : ses coordonnées ne sont probablement pas exprimées dans ce système. " +
                "Vérifiez le choix avec search_coordinate_systems (champ « compatible »), ou passez force=true si c'est voulu.");

        AcMapCoordsysCore.SetCoordinateSystem(system.GetCsCode(), database);
        return new
        {
            Assigned = AcMapCoordsysCore.GetCoordinateSystem(database),
            Previous = previous,
            System = Describe(system),
            Compatible = compatible,
            DrawingCenterLonLat = extents is null ? null : ToLonLat(system, extents.CenterX, extents.CenterY),
            DrawingUnits = database.Insunits.ToString(),
            Note = "Les objets n'ont pas été déplacés : seul le système de référence du dessin a changé.",
        };
    }

    private static object Describe(MgCoordinateSystem system)
    {
        var epsg = system.GetEpsgCode();
        return new
        {
            Code = system.GetCsCode(),
            Description = system.GetDescription(),
            Epsg = epsg > 0 ? epsg : (int?)null,
            Units = system.GetUnits(),
            Projection = system.GetProjection(),
            Datum = system.GetDatum(),
            Geodetic = system.IsGeodetic(),
            UsefulRange = DescribeLonLatRange(system),
        };
    }

    private static string DescribeLonLatRange(MgCoordinateSystem system) =>
        $"longitude {Format.Number(system.GetLonMin())}° à {Format.Number(system.GetLonMax())}°, " +
        $"latitude {Format.Number(system.GetLatMin())}° à {Format.Number(system.GetLatMax())}°";

    /// <summary>
    /// Compatible si les quatre coins de l'étendue du dessin, convertis en longitude/latitude par ce système,
    /// tombent dans son domaine d'usage. (IsValidXY ne contrôle que la validité mathématique de la conversion.)
    /// </summary>
    private static bool? IsCompatible(MgCoordinateSystem system, Extents? extents)
    {
        if (extents is null)
            return null;

        var (lonMin, lonMax, latMin, latMax) = (system.GetLonMin(), system.GetLonMax(), system.GetLatMin(), system.GetLatMax());
        var hasRange = lonMax > lonMin && latMax > latMin;
        foreach (var (x, y) in extents.Corners())
        {
            try
            {
                if (!system.IsValidXY(x, y))
                    return false;

                if (!hasRange)
                    continue;

                var lonLat = system.ConvertToLonLat(x, y);
                var (lon, lat) = (lonLat.GetX(), lonLat.GetY());
                if (double.IsNaN(lon) || double.IsNaN(lat) || lon < lonMin || lon > lonMax || lat < latMin || lat > latMax)
                    return false;
            }
            catch (MgException)
            {
                return false;
            }
        }

        return true;
    }

    private static object? ToLonLat(MgCoordinateSystem system, double x, double y)
    {
        try
        {
            var lonLat = system.ConvertToLonLat(x, y);
            return new { Longitude = Math.Round(lonLat.GetX(), 6), Latitude = Math.Round(lonLat.GetY(), 6) };
        }
        catch (MgException)
        {
            return null;
        }
    }

    private static object FormatLonLat((double Lon, double Lat) point) =>
        new { Longitude = Math.Round(point.Lon, 6), Latitude = Math.Round(point.Lat, 6) };

    public static (double X, double Y)? FromLonLat(MgCoordinateSystem system, double lon, double lat)
    {
        try
        {
            var coord = system.ConvertFromLonLat(lon, lat);
            var x = coord.GetX();
            var y = coord.GetY();
            coord.Dispose();
            return (x, y);
        }
        catch (MgException)
        {
            return null;
        }
    }

    private static MgCoordinateSystem? TryCreate(MgCoordinateSystemFactory factory, string code)
    {
        try
        {
            return factory.CreateFromCode(code);
        }
        catch (MgException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classement des systèmes du catalogue pour une requête. Un nombre seul est un code EPSG : seule la
    /// correspondance exacte compte. Sinon : code exact, puis tous les mots dans le code, puis dans la
    /// description ou la zone.
    /// </summary>
    private static List<Catalog.Entry> Rank(MgCoordinateSystemFactory factory, string query)
    {
        var entries = Catalog.Entries;
        if (int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var epsg))
        {
            var converted = EpsgToCode(factory, epsg);
            return entries
                .Where(entry => entry.Epsg == epsg || string.Equals(entry.Code, converted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => string.Equals(entry.Code, converted, StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var tokens = query.Split([' ', ',', ';', '/', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.ToLowerInvariant()).ToArray();

        return entries
            .Select(entry => (Entry: entry, Score: Score(entry, query, tokens)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entry.Code, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Entry)
            .ToList();
    }

    private static int Score(Catalog.Entry entry, string query, string[] tokens)
    {
        if (string.Equals(entry.Code, query, StringComparison.OrdinalIgnoreCase))
            return 1000;

        if (tokens.Length == 0 || !tokens.All(token => entry.SearchText.Contains(token, StringComparison.Ordinal)))
            return 0;

        var code = entry.Code.ToLowerInvariant();
        return tokens.All(token => code.Contains(token, StringComparison.Ordinal)) ? 500 - code.Length : 100;
    }

    private static string? EpsgToCode(MgCoordinateSystemFactory factory, int epsg)
    {
        try
        {
            return factory.ConvertWktToCoordinateSystemCode(factory.ConvertEpsgCodeToWkt(epsg));
        }
        catch (MgException)
        {
            return null;
        }
    }

    private static Extents? DrawingExtents(Database database)
    {
        var min = database.Extmin;
        var max = database.Extmax;
        return min.X > max.X || min.Y > max.Y ? null : new Extents(min.X, min.Y, max.X, max.Y);
    }

    private sealed record Extents(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double CenterX => (MinX + MaxX) / 2;

        public double CenterY => (MinY + MaxY) / 2;

        public (double X, double Y)[] Corners() => [(MinX, MinY), (MaxX, MinY), (MinX, MaxY), (MaxX, MaxY)];

        public string Describe() =>
            $"X {Format.Number(MinX)} à {Format.Number(MaxX)}, Y {Format.Number(MinY)} à {Format.Number(MaxY)}";
    }

    /// <summary>Ancre géographique d'un point du dessin, pour caler le soleil sur le système de coordonnées.</summary>
    internal readonly record struct SunAnchor(double X, double Y, double Longitude, double Latitude, string Source, string? Code);

    /// <summary>
    /// Convertit un point du dessin (ou le centre de l'emprise) en longitude/latitude. Système attribué au dessin,
    /// sinon estimation Lambert-93 si le point est dans l'étendue métropolitaine. L'erreur explique l'échec.
    /// </summary>
    internal static bool TryLocate(Database database, double? x, double? y, out SunAnchor anchor, out string? error)
    {
        anchor = default;
        error = null;
        if (!MapInfo.IsAvailable)
            return TryLocateClassic(database, x, y, out anchor, out error);
        return TryLocateMap(database, x, y, out anchor, out error);
    }

    private static bool TryLocateClassic(Database database, double? x, double? y, out SunAnchor anchor, out string? error)
    {
        anchor = default;
        if (!ResolveAnchorPoint(database, x, y, out var px, out var py, out error))
            return false;

        if (!Lambert93.IsLambert93Range(px, py, px, py))
        {
            error = "Le dessin n'a pas de système de coordonnées, et le point n'est pas dans l'étendue Lambert-93.";
            return false;
        }

        var (lon, lat) = Lambert93.ToWgs84(px, py);
        if (double.IsNaN(lon) || double.IsNaN(lat))
        {
            error = "La conversion Lambert-93 du point d'ancrage a échoué.";
            return false;
        }

        anchor = new SunAnchor(px, py, lon, lat, "lambert93", "Lambert93");
        error = null;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryLocateMap(Database database, double? x, double? y, out SunAnchor anchor, out string? error)
    {
        anchor = default;
        var code = AcMapCoordsysCore.GetCoordinateSystem(database);
        if (string.IsNullOrEmpty(code))
            return TryLocateClassic(database, x, y, out anchor, out error);

        if (!ResolveAnchorPoint(database, x, y, out var px, out var py, out error))
            return false;

        var system = TryCreate(new MgCoordinateSystemFactory(), code);
        if (system is null)
        {
            error = $"Système de coordonnées « {code} » inconnu du catalogue.";
            return false;
        }

        try
        {
            var lonLat = system.ConvertToLonLat(px, py);
            var lon = lonLat.GetX();
            var lat = lonLat.GetY();
            lonLat.Dispose();
            if (double.IsNaN(lon) || double.IsNaN(lat))
            {
                error = $"Le point ({Format.Number(px)}, {Format.Number(py)}) ne se convertit pas avec « {code} ».";
                return false;
            }

            anchor = new SunAnchor(px, py, lon, lat, "coordinateSystem", code);
            error = null;
            return true;
        }
        catch (MgException)
        {
            error = $"Le point ({Format.Number(px)}, {Format.Number(py)}) est hors du domaine de « {code} ».";
            return false;
        }
        finally
        {
            system.Dispose();
        }
    }

    private static bool ResolveAnchorPoint(Database database, double? x, double? y, out double px, out double py, out string? error)
    {
        error = null;
        if (x is double givenX && y is double givenY)
        {
            px = givenX;
            py = givenY;
            return true;
        }

        if (x is not null || y is not null)
        {
            px = py = 0;
            error = "Indiquez les deux coordonnées du point d'ancrage.";
            return false;
        }

        var extents = DrawingExtents(database);
        if (extents is null)
        {
            px = py = 0;
            error = "Le dessin est vide : indiquez « at », ou latitude et longitude.";
            return false;
        }

        px = extents.CenterX;
        py = extents.CenterY;
        return true;
    }

    /// <summary>
    /// Index du catalogue, construit une fois au premier usage puis gardé en mémoire (le catalogue ne change
    /// pas en cours de session). Toujours manipulé depuis le thread principal d'AutoCAD.
    /// </summary>
    private static class Catalog
    {
        private static List<Entry>? _entries;

        public static IReadOnlyList<Entry> Entries => _entries ??= Build();

        private static List<Entry> Build()
        {
            var clock = Stopwatch.StartNew();
            var dictionary = new MgCoordinateSystemFactory().GetCatalog().GetCoordinateSystemDictionary();
            var enumerator = dictionary.GetEnum();

            var entries = new List<Entry>(dictionary.GetSize());
            while (true)
            {
                var names = enumerator.NextName(1000);
                var count = names.GetCount();
                if (count == 0)
                {
                    names.Dispose();
                    break;
                }

                // Des milliers d'objets natifs : on les libère au fil de l'eau.
                for (var index = 0; index < count; index++)
                {
                    try
                    {
                        var system = dictionary.GetCoordinateSystem(names.GetItem(index));
                        entries.Add(new Entry(
                            system.GetCsCode(),
                            system.GetDescription(),
                            system.GetEpsgCode(),
                            system.GetUnits(),
                            system.GetProjection(),
                            system.GetLocation()));
                        system.Dispose();
                    }
                    catch (MgException)
                    {
                        // Définition invalide dans le catalogue : ignorée.
                    }
                }

                names.Dispose();
            }

            Log.Info($"Catalogue des systèmes de coordonnées indexé : {entries.Count} systèmes en {clock.ElapsedMilliseconds} ms");
            return entries;
        }

        public sealed record Entry(string Code, string Description, int Epsg, string Units, string Projection, string Location)
        {
            public string SearchText { get; } =
                $"{Code} {Description} {Location} {(Epsg > 0 ? Epsg.ToString(CultureInfo.InvariantCulture) : "")}".ToLowerInvariant();
        }
    }
}

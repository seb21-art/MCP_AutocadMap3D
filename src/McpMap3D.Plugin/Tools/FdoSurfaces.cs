using System.Globalization;
using System.Text;
using OSGeo.MapGuide;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Calques FDO de surface (MNT : ASC, DEM, GeoTIFF d'altitudes…). Map 3D les affiche par une définition de grille
/// avec style de surface, ombrage et règles de couleur sur Height(1), comme celle qu'écrit MAPCONNECT ; la définition
/// d'image (couleur de la bande 1) donnerait un aplat uniforme. Ici, les règles forment une teinte hypsométrique sur
/// l'étendue des altitudes quand elle est connue.
/// </summary>
internal static class FdoSurfaces
{
    private const long MaxAscValues = 30_000_000;

    /// <summary>Teinte hypsométrique : vert des fonds, jaune, ocre, brun, puis gris clair des sommets.</summary>
    private static readonly (double At, int R, int G, int B)[] Ramp =
    [
        (0.00, 46, 125, 50), (0.25, 139, 195, 74), (0.50, 245, 230, 99), (0.75, 205, 133, 63), (1.00, 240, 240, 240),
    ];

    public sealed record Range(double Min, double Max, string Source);

    /// <summary>Vrai si la première image de la classe est un modèle de données (altitudes), pas une image.</summary>
    public static bool IsElevation(MgResourceIdentifier resource, string featureName, string rasterProperty)
    {
        try
        {
            var reader = FdoTools.Features().SelectFeatures(resource, featureName, new MgFeatureQueryOptions());
            try
            {
                return reader.ReadNext() && reader.GetRaster(rasterProperty).GetDataModelType() == MgRasterDataModelType.Data;
            }
            finally
            {
                reader.Close();
            }
        }
        catch (MgException ex)
        {
            ex.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Étendue des altitudes lue dans les fichiers ESRI ASCII (.asc) de la source : le fichier désigné, ou ceux du
    /// dossier (celui de la classe s'il existe). Null pour les autres formats ou au-delà de 30 millions de valeurs.
    /// </summary>
    public static Range? AscRange(MgResourceIdentifier resource, string className)
    {
        string? location;
        try
        {
            var document = FdoTools.Xml(FdoTools.Read(FdoTools.ResourceService().GetResourceContent(resource)));
            location = document.Descendants().Where(element => element.Name.LocalName == "Parameter")
                .FirstOrDefault(element => string.Equals(element.Elements().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value,
                    "DefaultRasterFileLocation", StringComparison.OrdinalIgnoreCase))
                ?.Elements().FirstOrDefault(child => child.Name.LocalName == "Value")?.Value;
        }
        catch (MgException ex)
        {
            ex.Dispose();
            return null;
        }

        if (string.IsNullOrWhiteSpace(location))
            return null;
        IEnumerable<string> files = File.Exists(location) ? [location]
            : Directory.Exists(location) ? Directory.EnumerateFiles(location, "*.asc") : [];
        var list = files.Where(file => file.EndsWith(".asc", StringComparison.OrdinalIgnoreCase)).ToList();
        var own = list.Where(file => string.Equals(Path.GetFileNameWithoutExtension(file), className, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count > 0)
            list = own;
        if (list.Count == 0)
            return null;

        double min = double.MaxValue, max = double.MinValue;
        long count = 0;
        foreach (var file in list)
        {
            if (!ReadAsc(file, ref min, ref max, ref count))
                return null;
        }

        return count > 0 ? new Range(min, max, list.Count == 1 ? Path.GetFileName(list[0]) : $"{list.Count} fichiers ASC") : null;
    }

    private static bool ReadAsc(string file, ref double min, ref double max, ref long count)
    {
        using var reader = new StreamReader(file, Encoding.ASCII, false, 1 << 16);
        double? noData = null;
        long expected = 1;
        string? line;
        // En-tête : ncols, nrows, xllcorner, yllcorner, cellsize, NODATA_value (facultatif).
        while ((line = reader.ReadLine()) is not null)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && char.IsLetter(parts[0][0]))
            {
                var key = parts[0].ToLowerInvariant();
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return false;
                if (key is "ncols" or "nrows")
                    expected *= (long)value;
                if (key == "nodata_value")
                    noData = value;
                continue;
            }

            break;
        }

        if (count + expected > MaxAscValues)
            return false;
        for (; line is not null; line = reader.ReadLine())
        {
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var z) || z == noData)
                    continue;
                min = Math.Min(min, z);
                max = Math.Max(max, z);
                count++;
            }
        }

        return true;
    }

    /// <summary>
    /// Définition de calque de surface : même forme que celle de MAPCONNECT (version 1.0.0, bande 1, ombrage, règles
    /// sur Height(1), règle par défaut), avec une teinte en <paramref name="classes"/> plages sur l'étendue connue.
    /// </summary>
    public static string LayerXml(string resource, string featureName, Range? range, int classes, bool hillshade)
    {
        var rules = new StringBuilder();
        if (range is null || classes <= 1 || range.Max <= range.Min)
        {
            rules.Append(Rule("Toutes altitudes", "(Height(1) > -1.7976931348623158e+308) AND (Height(1) < 1.7976931348623158e+308)", Color(0.25)));
        }
        else
        {
            var step = (range.Max - range.Min) / classes;
            for (var i = 0; i < classes; i++)
            {
                var low = range.Min + i * step;
                var high = i == classes - 1 ? range.Max : low + step;
                // Bornes extrêmes ouvertes : rien n'échappe aux plages à cause d'un arrondi.
                var lowTest = i == 0 ? "Height(1) > -1.7976931348623158e+308" : $"Height(1) >= {Number(low)}";
                var highTest = i == classes - 1 ? "Height(1) < 1.7976931348623158e+308" : $"Height(1) < {Number(high)}";
                rules.Append(Rule($"{Label(low)} à {Label(high)} m", $"({lowTest}) AND ({highTest})", Color(classes == 1 ? 0 : (double)i / (classes - 1))));
            }
        }

        rules.Append("<ColorRule><LegendLabel>{DEFAULT}</LegendLabel><Color><ExplicitColor>FF808080</ExplicitColor></Color></ColorRule>");
        var shade = hillshade ? "<HillShade><Band>1</Band><Azimuth>315</Azimuth><Altitude>45</Altitude></HillShade>" : "";
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<LayerDefinition xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"LayerDefinition-1.0.0.xsd\" version=\"1.0.0\">"
            + "<GridLayerDefinition><ResourceId>" + FdoTools.Escape(resource) + "</ResourceId><FeatureName>" + FdoTools.Escape(featureName)
            + "</FeatureName><Geometry></Geometry><GridScaleRange>"
            + "<SurfaceStyle><Band>1</Band><DefaultColor>00808080</DefaultColor></SurfaceStyle>"
            + "<ColorStyle>" + shade + rules + "</ColorStyle><RebuildFactor>1</RebuildFactor>"
            + "</GridScaleRange></GridLayerDefinition></LayerDefinition>";
    }

    private static string Rule(string label, string filter, string color) =>
        "<ColorRule><LegendLabel>" + FdoTools.Escape(label) + "</LegendLabel><Filter>" + FdoTools.Escape(filter)
        + "</Filter><Color><ExplicitColor>" + color + "</ExplicitColor></Color></ColorRule>";

    private static string Color(double at)
    {
        var upper = Array.FindIndex(Ramp, stop => stop.At >= at);
        if (upper <= 0)
            return Argb(Ramp[0].R, Ramp[0].G, Ramp[0].B);
        var (a, b) = (Ramp[upper - 1], Ramp[upper]);
        var t = (at - a.At) / (b.At - a.At);
        return Argb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static string Argb(int r, int g, int b) => $"FF{r:X2}{g:X2}{b:X2}";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Label(double value) => value.ToString("0.#", CultureInfo.GetCultureInfo("fr-FR"));
}

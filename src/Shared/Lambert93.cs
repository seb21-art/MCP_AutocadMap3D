namespace McpMap3D.Shared;

/// <summary>
/// Algorithme officiel IGN (NTG_71) de projection conique conforme de Lambert-93 (EPSG:2154).
/// Permet la conversion autonome WGS84 (lon, lat en degrés) <-> Lambert-93 (X, Y en mètres)
/// sans aucune dépendance vers les bibliothèques géodésiques de Map 3D.
/// </summary>
public static class Lambert93
{
    // Constantes officielles IGN / EPSG:2154 pour Lambert-93 sur ellipsoïde GRS80/WGS84
    private const double A = 6378137.0; // Demi-grand axe
    private const double E = 0.0818191910428158; // Première excentricité
    private const double N = 0.725607765053267; // Exposant de la projection
    private const double C = 11754255.426096; // Constante de projection
    private const double Xs = 700000.0; // Coordonnées du pôle en projection
    private const double Ys = 12655612.049876;
    private const double Lambda0 = 3.0 * Math.PI / 180.0; // Méridien central (3° Est)

    /// <summary>
    /// Convertit des coordonnées géographiques WGS84 (longitude, latitude en degrés)
    /// en coordonnées projetées planes Lambert-93 (X, Y en mètres).
    /// </summary>
    public static (double X, double Y) ToLambert93(double lonDeg, double latDeg)
    {
        if (double.IsNaN(lonDeg) || double.IsNaN(latDeg) || double.IsInfinity(lonDeg) || double.IsInfinity(latDeg))
            return (double.NaN, double.NaN);
        if (latDeg <= -90.0 || latDeg >= 90.0)
            return (double.NaN, double.NaN);

        var latRad = latDeg * Math.PI / 180.0;
        var lonRad = lonDeg * Math.PI / 180.0;

        var sinLat = Math.Sin(latRad);
        // Latitude isométrique L(phi)
        var latIso = Math.Log(Math.Tan(Math.PI / 4.0 + latRad / 2.0) *
                              Math.Pow((1.0 - E * sinLat) / (1.0 + E * sinLat), E / 2.0));

        var gamma = N * (lonRad - Lambda0);
        var rho = C * Math.Exp(-N * latIso);

        var x = Xs + rho * Math.Sin(gamma);
        var y = Ys - rho * Math.Cos(gamma);

        return (x, y);
    }

    /// <summary>
    /// Convertit des coordonnées planes Lambert-93 (X, Y en mètres)
    /// en coordonnées géographiques WGS84 (longitude, latitude en degrés).
    /// </summary>
    public static (double Lon, double Lat) ToWgs84(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            return (double.NaN, double.NaN);

        var dx = x - Xs;
        var dy = Ys - y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r < 1e-6)
            return (Lambda0 * 180.0 / Math.PI, 90.0);

        var gamma = Math.Atan2(dx, dy);
        var lonRad = Lambda0 + gamma / N;

        var latIso = -1.0 / N * Math.Log(r / C);

        // Résolution itérative de la latitude (converge en 3-4 itérations à 10^-11 rad)
        var phi = 2.0 * Math.Atan(Math.Exp(latIso)) - Math.PI / 2.0;
        for (var i = 0; i < 5; i++)
        {
            var s = Math.Sin(phi);
            phi = 2.0 * Math.Atan(Math.Pow((1.0 + E * s) / (1.0 - E * s), E / 2.0) * Math.Exp(latIso)) - Math.PI / 2.0;
        }

        return (lonRad * 180.0 / Math.PI, phi * 180.0 / Math.PI);
    }

    /// <summary>
    /// Vérifie si des coordonnées X, Y sont compatibles avec l'étendue métropolitaine de Lambert-93.
    /// </summary>
    public static bool IsLambert93Range(double minX, double minY, double maxX, double maxY) =>
        minX >= 50000.0 && maxX <= 1350000.0 && minY >= 6000000.0 && maxY <= 7200000.0;
}

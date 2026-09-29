using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace McpMap3D.Plugin.Tools;

/// <summary>Mise en forme compacte des valeurs AutoCAD pour le JSON renvoyé à Claude.</summary>
internal static class Format
{
    private const int Decimals = 6;

    public static double Number(double value) => Math.Round(value, Decimals);

    /// <summary>Les angles échangés avec Claude sont en degrés ; AutoCAD travaille en radians.</summary>
    public static double Degrees(double radians) => Number(radians * 180.0 / Math.PI);

    public static double Radians(double degrees) => degrees * Math.PI / 180.0;

    public static double[] Vector(Vector3d vector) => [Number(vector.X), Number(vector.Y), Number(vector.Z)];

    public static double[] Point(Point3d point) => [Number(point.X), Number(point.Y), Number(point.Z)];

    public static double[] Point(Point2d point) => [Number(point.X), Number(point.Y)];

    public static string Handle(ObjectId id) => id.Handle.ToString();

    /// <summary>Nom DXF de l'objet : LINE, LWPOLYLINE, INSERT, TEXT…</summary>
    public static string DxfName(DBObject dbObject) => dbObject.GetRXClass().DxfName;

    public static string Color(Color color) => color.IsByLayer ? "ByLayer"
        : color.IsByBlock ? "ByBlock"
        : color.ColorMethod == ColorMethod.ByColor ? $"RGB({color.Red},{color.Green},{color.Blue})"
        : color.ColorIndex.ToString();

    public static object? Extents(Extents3d? extents) =>
        extents is null ? null : new { Min = Point(extents.Value.MinPoint), Max = Point(extents.Value.MaxPoint) };

    /// <summary>Extrait des limites d'un objet ; certaines entités n'en ont pas.</summary>
    public static Extents3d? TryGetExtents(Entity entity)
    {
        try
        {
            return entity.GeometricExtents;
        }
        catch
        {
            return null;
        }
    }

    public static string Truncate(string? text, int maximum)
    {
        text ??= "";
        return text.Length <= maximum ? text : text[..maximum] + "…";
    }
}

/// <summary>Filtre par nom, avec les jokers * et ? d'AutoCAD ; la casse est ignorée.</summary>
internal sealed class NameFilter
{
    private readonly Regex[] _patterns;

    private NameFilter(Regex[] patterns) => _patterns = patterns;

    public static NameFilter? Create(IReadOnlyList<string> patterns) =>
        patterns.Count == 0 ? null : new NameFilter([.. patterns.Select(ToRegex)]);

    public bool IsMatch(string name) => _patterns.Any(pattern => pattern.IsMatch(name));

    private static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

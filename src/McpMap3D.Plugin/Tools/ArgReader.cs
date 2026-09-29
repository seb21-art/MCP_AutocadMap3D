using System.Text.Json;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Lecture des paramètres d'un outil. Toute valeur absente ou mal typée produit une erreur
/// « invalid_params » explicite, renvoyée telle quelle à Claude.
/// </summary>
internal readonly struct ArgReader(JsonElement? args)
{
    public bool Has(string name) => TryGet(name, out _);

    public bool TryGet(string name, out JsonElement value)
    {
        value = default;
        if (args is not { ValueKind: JsonValueKind.Object } element)
            return false;

        return element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public string? GetString(string name, string? fallback = null)
    {
        if (!TryGet(name, out var value))
            return fallback;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Invalid(name, "une chaîne");
    }

    public string RequireString(string name) =>
        GetString(name) ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est obligatoire.");

    public bool GetBool(string name, bool fallback)
    {
        if (!TryGet(name, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(name, "un booléen"),
        };
    }

    public int GetInt(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!TryGet(name, out var value))
            return fallback;

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw Invalid(name, "un entier");

        return number < min || number > max
            ? throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » doit être compris entre {min} et {max}.")
            : number;
    }

    public double GetDouble(string name, double fallback)
    {
        if (!TryGet(name, out var value))
            return fallback;

        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw Invalid(name, "un nombre");
    }

    public double RequireDouble(string name) =>
        TryGet(name, out _) ? GetDouble(name, 0) : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est obligatoire.");

    /// <summary>Accepte une chaîne seule ou un tableau de chaînes ; liste vide si absent.</summary>
    public IReadOnlyList<string> GetStrings(string name)
    {
        if (!TryGet(name, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.String)
            return [value.GetString()!];

        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(name, "une chaîne ou un tableau de chaînes");

        var items = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Invalid(name, "un tableau de chaînes");

            items.Add(item.GetString()!);
        }

        return items;
    }

    /// <summary>Tableau d'exactement <paramref name="count"/> nombres, par exemple une fenêtre [minX, minY, maxX, maxY] ; null si absent.</summary>
    public double[]? GetNumbers(string name, int count)
    {
        if (!TryGet(name, out var value))
            return null;

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count
            || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Number))
            throw Invalid(name, $"un tableau de {count} nombres");

        return value.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    }

    /// <summary>Point au format [x, y] ou [x, y, z].</summary>
    public Point3d RequirePoint(string name)
    {
        if (!TryGet(name, out var value))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est obligatoire.");

        return ReadPoint(value, name);
    }

    public Point3d GetPoint(string name, Point3d fallback) =>
        TryGet(name, out var value) ? ReadPoint(value, name) : fallback;

    /// <summary>Liste de points, par exemple les sommets d'une polyligne.</summary>
    public IReadOnlyList<Point3d> RequirePoints(string name, int minimum)
    {
        if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw Invalid(name, "un tableau de points, par exemple [[0,0],[10,5]]");

        var points = value.EnumerateArray().Select(item => ReadPoint(item, name)).ToList();
        return points.Count >= minimum
            ? points
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » demande au moins {minimum} points.");
    }

    /// <summary>Grille de points : rangées de même longueur, par exemple les points de contrôle d'une surface.</summary>
    public IReadOnlyList<IReadOnlyList<Point3d>> RequirePointGrid(string name, int minimum)
    {
        if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw Invalid(name, "un tableau de rangées de points, par exemple [[[0,0,0],[10,0,0]],[[0,10,0],[10,10,2]]]");

        var rows = new List<IReadOnlyList<Point3d>>();
        foreach (var row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
                throw Invalid(name, "des rangées de points");

            rows.Add(row.EnumerateArray().Select(item => ReadPoint(item, name)).ToList());
        }

        if (rows.Count < minimum || rows.Any(row => row.Count < minimum))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le paramètre « {name} » demande au moins {minimum} rangées de {minimum} points.");

        return rows.All(row => row.Count == rows[0].Count)
            ? rows
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Les rangées de « {name} » doivent avoir le même nombre de points.");
    }

    private static Point3d ReadPoint(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(name, "un point au format [x, y] ou [x, y, z]");

        var coordinates = new List<double>(3);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
                throw Invalid(name, "des coordonnées numériques");

            coordinates.Add(item.GetDouble());
        }

        return coordinates.Count switch
        {
            2 => new Point3d(coordinates[0], coordinates[1], 0),
            3 => new Point3d(coordinates[0], coordinates[1], coordinates[2]),
            _ => throw Invalid(name, "un point au format [x, y] ou [x, y, z]"),
        };
    }

    private static PipeException Invalid(string name, string expected) =>
        new(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » attend {expected}.");
}

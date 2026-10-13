using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Images raster géoréférencées (orthophotos, plans scannés) : image AutoCAD placée d'après son fichier monde
/// (.tfw, .jgw, .pgw, .wld…) ou les balises GeoTIFF, ou à une position et une largeur données. Une étape de U.
/// </summary>
internal static class RasterTools
{
    /// <summary>Géoréférencement au format du fichier monde : A, D, B, E, C, F (C, F au centre du pixel en haut à gauche).</summary>
    private sealed record WorldFile(double A, double D, double B, double E, double C, double F, string Source);

    public static object AttachImage(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var path = reader.RequireString("filePath");
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        var database = context.Database;
        var transaction = context.RequireTransaction();
        var dictionaryId = RasterImageDef.GetImageDictionary(database);
        if (dictionaryId.IsNull)
            dictionaryId = RasterImageDef.CreateImageDictionary(database);
        var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForWrite);

        var definition = new RasterImageDef { SourceFileName = path };
        try
        {
            definition.Load();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            definition.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD ne sait pas lire l'image {Path.GetFileName(path)} ({ex.ErrorStatus}).");
        }

        var baseName = SymbolUtilityServices.RepairSymbolName(Path.GetFileNameWithoutExtension(path), false);
        var name = baseName;
        for (var suffix = 2; dictionary.Contains(name); suffix++)
            name = $"{baseName}_{suffix}";
        var definitionId = dictionary.SetAt(name, definition);
        transaction.AddNewlyCreatedDBObject(definition, true);

        var (pixelsX, pixelsY) = (definition.Size.X, definition.Size.Y);
        WorldFile world;
        if (reader.Has("position"))
        {
            // Placement manuel : coin en bas à gauche et largeur, hauteur d'après les proportions de l'image.
            var corner = reader.RequirePoint("position");
            var width = reader.RequireDouble("width");
            if (width <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "width doit être strictement positif.");
            var pixel = width / pixelsX;
            world = new WorldFile(pixel, 0, 0, -pixel, corner.X + pixel / 2, corner.Y + pixel * pixelsY - pixel / 2, "manuel");
        }
        else
        {
            world = ReadWorldFile(path) ?? ReadGeoTiff(path)
                ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"{Path.GetFileName(path)} n'a ni fichier monde (.tfw, .jgw, .pgw, .wld…) ni balises GeoTIFF : donnez position et width.");
        }

        // Coin haut gauche de l'image, puis vecteurs largeur (u) et hauteur (v, vers le haut).
        var upperLeft = new Point3d(world.C - world.A / 2 - world.B / 2, world.F - world.D / 2 - world.E / 2, 0);
        var u = new Vector3d(world.A * pixelsX, world.D * pixelsX, 0);
        var down = new Vector3d(world.B * pixelsY, world.E * pixelsY, 0);
        var origin = upperLeft + down;
        var image = new RasterImage
        {
            ImageDefId = definitionId,
            Orientation = new CoordinateSystem3d(origin, u, -down),
            ShowImage = true,
            ImageTransparency = reader.GetBool("transparency", false),
        };
        var appended = EditTools.Append(context, image, reader);
        // Lien image -> définition, comme IMAGEATTACH. Avec le réglage par défaut de MAPIOPTIONS, Map 3D demande
        // (« Erasing Image ») s'il faut dissocier l'image quand sa dernière occurrence disparaît, y compris par U.
        image.AssociateRasterDef(definition);

        return new
        {
            Image = appended,
            Name = name,
            Pixels = new[] { (int)pixelsX, (int)pixelsY },
            Georeference = world.Source,
            LowerLeft = Format.Point(origin),
            Width = Format.Number(u.Length),
            Height = Format.Number(down.Length),
            PixelSize = Format.Number(Math.Sqrt(world.A * world.A + world.D * world.D)),
            Rotated = world.B != 0 || world.D != 0 ? true : (bool?)null,
        };
    }

    /// <summary>Fichier monde à côté de l'image : .tfw, .tifw, .jgw, .pgw, .wld…</summary>
    private static WorldFile? ReadWorldFile(string imagePath)
    {
        var extension = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        var candidates = new List<string>();
        if (extension.Length >= 2)
            candidates.Add($"{extension[0]}{extension[^1]}w");
        candidates.AddRange([$"{extension}w", "wld"]);
        foreach (var candidate in candidates.Distinct())
        {
            var file = Path.ChangeExtension(imagePath, candidate);
            if (!File.Exists(file))
                continue;
            var values = File.ReadAllLines(file).Where(line => line.Trim().Length > 0).Take(6)
                .Select(line => double.TryParse(line.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN)
                .ToList();
            if (values.Count == 6 && values.All(double.IsFinite))
                return new WorldFile(values[0], values[1], values[2], values[3], values[4], values[5], $"fichier monde {Path.GetFileName(file)}");
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Fichier monde {Path.GetFileName(file)} illisible : six nombres attendus.");
        }

        return null;
    }

    /// <summary>
    /// Balises GeoTIFF d'un TIFF classique (pas BigTIFF) : échelle des pixels (33550) et point d'attache (33922), ou
    /// matrice de transformation (34264). Le point d'attache désigne le coin du pixel (PixelIsArea).
    /// </summary>
    private static WorldFile? ReadGeoTiff(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".tif" or ".tiff"))
            return null;
        using var stream = File.OpenRead(path);
        using var binary = new BinaryReader(stream);
        var order = binary.ReadBytes(2);
        var little = order[0] == 'I';
        if (!little && order[0] != 'M')
            return null;
        ushort U16() { var b = binary.ReadBytes(2); if (!little) Array.Reverse(b); return BitConverter.ToUInt16(b); }
        uint U32() { var b = binary.ReadBytes(4); if (!little) Array.Reverse(b); return BitConverter.ToUInt32(b); }
        double F64() { var b = binary.ReadBytes(8); if (!little) Array.Reverse(b); return BitConverter.ToDouble(b); }
        if (U16() != 42)
            return null;
        stream.Position = U32();
        var count = U16();
        var tags = new Dictionary<ushort, (uint Count, uint Offset)>();
        for (var i = 0; i < count; i++)
        {
            var tag = U16();
            _ = U16();
            var items = U32();
            var offset = U32();
            tags[tag] = (items, offset);
        }

        double[] Doubles(ushort tag)
        {
            var (items, offset) = tags[tag];
            stream.Position = offset;
            return Enumerable.Range(0, (int)items).Select(_ => F64()).ToArray();
        }

        if (tags.ContainsKey(34264))
        {
            var m = Doubles(34264);
            return new WorldFile(m[0], m[4], m[1], m[5], m[3] + m[0] / 2 + m[1] / 2, m[7] + m[4] / 2 + m[5] / 2, "balises GeoTIFF (matrice)");
        }

        if (!tags.ContainsKey(33550) || !tags.ContainsKey(33922))
            return null;
        var scale = Doubles(33550);
        var tie = Doubles(33922);
        var (a, e) = (scale[0], -scale[1]);
        var (c, f) = (tie[3] - tie[0] * a, tie[4] - tie[1] * e);
        return new WorldFile(a, 0, 0, e, c + a / 2, f + e / 2, "balises GeoTIFF");
    }
}

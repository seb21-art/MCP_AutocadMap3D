using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using DbSurface = Autodesk.AutoCAD.DatabaseServices.Surface;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Échanges de fichiers 3D : export STL (impression 3D) et SAT (ACIS), import SAT. Un chemin relatif part du dossier
/// du dessin ; sans chemin, le fichier prend le nom du dessin, à côté de lui (dans le dossier temporaire tant que le
/// dessin n'est pas enregistré).
/// </summary>
internal static partial class ModelingTools
{
    private const int MaxListedImports = 500;

    /// <summary>
    /// Export STL de solides, comme la commande STLOUT. Plusieurs solides sont réunis dans une copie, le dessin restant
    /// intact. Par défaut la copie est ramenée à l'origine : le STL stocke ses coordonnées en simple précision, qui
    /// perdrait les décimales de coordonnées Lambert-93, et AutoCAD veut des objets dans l'octant positif.
    /// </summary>
    public static object ExportStl(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var solids = new List<Solid3d>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            solids.Add(transaction.GetObject(id, OpenMode.ForRead) as Solid3d
                ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} n'est pas un solide 3D : seuls les solides s'exportent en STL (un maillage se " +
                    "convertit avec convert_mesh, to = solid)."));
        }

        var ascii = reader.GetBool("ascii", false);
        var moveToOrigin = reader.GetBool("moveToOrigin", true);
        var path = ExchangePath(context, reader, ".stl");
        var overwritten = File.Exists(path);

        using var copy = (Solid3d)solids[0].Clone();
        foreach (var other in solids.Skip(1))
        {
            using var part = (Solid3d)other.Clone();
            try
            {
                copy.BooleanOperation(BooleanOperationType.BoolUnite, part);
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu réunir le solide {other.Handle} aux autres pour l'export ({ex.ErrorStatus}).");
            }
        }

        Vector3d? offset = null;
        if (moveToOrigin && Format.TryGetExtents(copy) is Extents3d box)
        {
            offset = Point3d.Origin - box.MinPoint;
            copy.TransformBy(Matrix3d.Displacement(offset.Value));
        }

        try
        {
            copy.StlOut(path, ascii);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu écrire le fichier STL {path} ({ex.ErrorStatus})" + (moveToOrigin
                    ? "."
                    : " : sans moveToOrigin, les solides doivent être entièrement du côté positif des axes X, Y et Z."));
        }

        return new
        {
            File = path,
            Solids = solids.Count,
            Format = ascii ? "ascii" : "binary",
            Bytes = WrittenLength(path),
            Offset = offset is Vector3d vector ? Format.Vector(vector) : null,
            Overwritten = overwritten ? true : (bool?)null,
        };
    }

    /// <summary>Export ACIS (SAT) de solides, surfaces et régions, comme la commande EXPORTACIS.</summary>
    public static object ExportSat(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var objects = new DBObjectCollection();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            if (entity is not (Solid3d or DbSurface or Region or Body))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} ({Format.DxfName(entity)}) n'est pas un modèle ACIS : solides, surfaces, régions ou " +
                    "corps attendus.");

            objects.Add(entity);
        }

        var path = ExchangePath(context, reader, ".sat");
        var overwritten = File.Exists(path);
        try
        {
            Body.AcisOut(path, objects);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu écrire le fichier SAT {path} ({ex.ErrorStatus}).");
        }

        return new
        {
            File = path,
            Objects = objects.Count,
            Bytes = WrittenLength(path),
            Overwritten = overwritten ? true : (bool?)null,
        };
    }

    /// <summary>Import d'un fichier ACIS texte (SAT), comme la commande IMPORTACIS : les objets vont dans l'espace courant.</summary>
    public static object ImportSat(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("filePath"), DrawingFolder(context));
        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        DBObjectCollection objects;
        try
        {
            objects = Body.AcisIn(path);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu lire {path} ({ex.ErrorStatus}) : fichier ACIS texte (.sat) attendu, les fichiers " +
                "binaires .sab ne se lisent pas ici.");
        }

        var created = new List<object>();
        foreach (DBObject dbObject in objects)
        {
            if (dbObject is not Entity entity)
            {
                dbObject.Dispose();
                continue;
            }

            var appended = EditTools.Append(context, entity, reader);
            created.Add(new
            {
                Object = appended,
                Volume = entity is Solid3d solid ? TryVolume(solid) : null,
                Area = entity is DbSurface surface ? TryNumber(surface.GetArea) : entity is Region region ? TryNumber(() => region.Area) : null,
            });
        }

        if (created.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} ne contient aucun objet à importer.");

        return new
        {
            File = path,
            Count = created.Count,
            Objects = created.Take(MaxListedImports).ToArray(),
            Truncated = created.Count > MaxListedImports ? true : (bool?)null,
        };
    }

    /// <summary>
    /// Chemin du fichier à écrire : « filePath », relatif au dossier du dessin, complété par l'extension s'il n'en a
    /// pas ; sinon le nom du dessin avec <paramref name="extension"/>.
    /// </summary>
    private static string ExchangePath(ToolContext context, ArgReader reader, string extension)
    {
        var folder = DrawingFolder(context);
        var requested = reader.GetString("filePath");
        var path = string.IsNullOrWhiteSpace(requested)
            ? Path.Combine(folder, Path.GetFileNameWithoutExtension(context.RequireDocument().Name) + extension)
            : Path.GetFullPath(requested, folder);
        if (!Path.HasExtension(path))
            path += extension;

        var directory = Path.GetDirectoryName(path);
        return directory is not null && Directory.Exists(directory)
            ? path
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le dossier {directory} n'existe pas.");
    }

    /// <summary>Dossier du dessin enregistré, sinon le dossier temporaire.</summary>
    private static string DrawingFolder(ToolContext context)
    {
        var drawing = context.RequireDocument().Name;
        return Path.IsPathRooted(drawing) && File.Exists(drawing) ? Path.GetDirectoryName(drawing)! : Path.GetTempPath();
    }

    private static long WrittenLength(string path) =>
        File.Exists(path)
            ? new FileInfo(path).Length
            : throw new PipeException(PipeErrorCodes.Internal, $"AutoCAD n'a pas signalé d'erreur, mais le fichier {path} n'a pas été écrit.");
}

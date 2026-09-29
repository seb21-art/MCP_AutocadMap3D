using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>Étirement par fenêtre et ordre d'affichage.</summary>
internal static partial class ModifyTools
{
    private const int MaxListedHandles = 500;

    /// <summary>
    /// Étire des objets comme la commande ETIRER avec une fenêtre de capture : les points caractéristiques (sommets,
    /// extrémités, points d'insertion, points de définition des cotes) situés dans la fenêtre, vue de dessus, sont
    /// déplacés ; un objet dont tous les points sont dans la fenêtre est déplacé en entier.
    /// </summary>
    public static object StretchEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var (polygon, min, max) = ReadWindow(reader);
        var displacement = ReadDisplacement(reader);
        var matrix = Matrix3d.Displacement(displacement);

        // Sans handles : tous les objets de l'espace courant, comme une sélection par capture.
        var explicitHandles = reader.Has("handles");
        IEnumerable<ObjectId> candidates = explicitHandles
            ? Handles.Resolve(context, reader)
            : ((BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>();

        var stretched = new List<string>();
        var moved = new List<string>();
        var locked = new List<string>();
        foreach (var id in candidates)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity || entity is Viewport)
                continue;

            // Une hachure associative suit son contour : l'étirer à part romprait l'association.
            if (entity is Hatch { Associative: true })
                continue;

            if (!explicitHandles && Format.TryGetExtents(entity) is Extents3d extents
                && (extents.MaxPoint.X < min.X || extents.MinPoint.X > max.X || extents.MaxPoint.Y < min.Y || extents.MinPoint.Y > max.Y))
                continue;

            var points = new Point3dCollection();
            try
            {
                entity.GetStretchPoints(points);
            }
            catch (AcException)
            {
                continue; // Objet sans points d'étirement.
            }

            var inside = new IntegerCollection();
            for (var index = 0; index < points.Count; index++)
            {
                if (Inside(polygon, min, max, points[index]))
                    inside.Add(index);
            }

            if (inside.Count == 0)
                continue;

            if (((LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked)
            {
                locked.Add(entity.Handle.ToString());
                continue;
            }

            entity.UpgradeOpen();
            if (inside.Count == points.Count)
            {
                entity.TransformBy(matrix);
                moved.Add(entity.Handle.ToString());
            }
            else
            {
                entity.MoveStretchPointsAt(inside, displacement);
                stretched.Add(entity.Handle.ToString());
            }

            if (entity is Dimension dimension)
                dimension.RecomputeDimensionBlock(true);
        }

        if (stretched.Count + moved.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                locked.Count > 0
                    ? $"Les seuls objets concernés sont sur des calques verrouillés : {string.Join(", ", locked.Take(20))}."
                    : "Aucun point d'objet dans la fenêtre (vue de dessus) : rien à étirer.");

        return new
        {
            Stretched = stretched.Count,
            Moved = moved.Count,
            StretchedHandles = stretched.Take(MaxListedHandles).ToArray(),
            MovedHandles = moved.Take(MaxListedHandles).ToArray(),
            HandlesTruncated = stretched.Count > MaxListedHandles || moved.Count > MaxListedHandles ? true : (bool?)null,
            LockedSkipped = locked.Count > 0 ? locked.Take(MaxListedHandles).ToArray() : null,
            Displacement = Format.Vector(displacement),
        };
    }

    /// <summary>Fenêtre : rectangle corner1-corner2 ou polygone, avec ses limites en plan.</summary>
    private static (List<Point2d>? Polygon, Point2d Min, Point2d Max) ReadWindow(ArgReader reader)
    {
        var hasRectangle = reader.Has("corner1") || reader.Has("corner2");
        if (hasRectangle == reader.Has("polygon"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez la fenêtre soit par « corner1 » et « corner2 » (rectangle), soit par « polygon » (sommets), vue de dessus.");

        if (hasRectangle)
        {
            var a = reader.RequirePoint("corner1");
            var b = reader.RequirePoint("corner2");
            if (Math.Abs(a.X - b.X) == 0 || Math.Abs(a.Y - b.Y) == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La fenêtre corner1-corner2 est plate : ses coins doivent différer en X et en Y.");

            return (null, new Point2d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point2d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        }

        var polygon = reader.RequirePoints("polygon", minimum: 3).Select(point => new Point2d(point.X, point.Y)).ToList();
        return (polygon, new Point2d(polygon.Min(point => point.X), polygon.Min(point => point.Y)),
            new Point2d(polygon.Max(point => point.X), polygon.Max(point => point.Y)));
    }

    /// <summary>Point dans la fenêtre vue de dessus : rectangle bords compris, ou polygone (règle pair-impair).</summary>
    private static bool Inside(List<Point2d>? polygon, Point2d min, Point2d max, Point3d point)
    {
        if (point.X < min.X || point.X > max.X || point.Y < min.Y || point.Y > max.Y)
            return false;

        if (polygon is null)
            return true;

        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var (a, b) = (polygon[current], polygon[previous]);
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// Ordre d'affichage (ORDRETRACE) : objets au premier plan, à l'arrière-plan, ou juste au-dessus ou au-dessous
    /// d'un objet de référence. Utile pour une hachure pleine ou un masque qui recouvre des traits.
    /// </summary>
    public static object SetDrawOrder(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);
        var order = reader.RequireString("order").Trim().ToLowerInvariant();
        if (order is not ("front" or "back" or "above" or "below"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Ordre « {order} » inconnu : front (premier plan), back (arrière-plan), above ou below (par rapport à « reference »).");

        var owner = ObjectId.Null;
        foreach (var id in ids)
        {
            var entity = transaction.GetObject(id, OpenMode.ForRead) as Entity
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un objet graphique du dessin.");
            if (!owner.IsNull && entity.OwnerId != owner)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les objets doivent être dans le même espace (objet ou présentation).");

            owner = entity.OwnerId;
        }

        var reference = ObjectId.Null;
        if (order is "above" or "below")
        {
            reference = reader.Has("reference")
                ? Handles.Resolve(context, reader, "reference").Single()
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"Avec « {order} », indiquez l'objet de référence « reference ».");
            if (ids.Contains(reference))
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'objet de référence ne peut pas faire partie des objets déplacés.");
            if (transaction.GetObject(reference, OpenMode.ForRead) is not Entity { } referenceEntity || referenceEntity.OwnerId != owner)
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'objet de référence doit être dans le même espace que les objets.");
        }

        var space = (BlockTableRecord)transaction.GetObject(owner, OpenMode.ForRead);
        var table = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
        var collection = new ObjectIdCollection([.. ids]);
        switch (order)
        {
            case "front":
                table.MoveToTop(collection);
                break;
            case "back":
                table.MoveToBottom(collection);
                break;
            case "above":
                table.MoveAbove(collection, reference);
                break;
            default:
                table.MoveBelow(collection, reference);
                break;
        }

        var full = table.GetFullDrawOrder(0);
        var ranks = new Dictionary<ObjectId, int>(full.Count);
        for (var index = 0; index < full.Count; index++)
            ranks[full[index]] = index;

        // L'écran ne suit le nouvel ordre qu'après un regen.
        context.AfterCommit(() => context.Editor.Regen());

        return new
        {
            Order = order,
            Total = full.Count,
            Ranks = ids.Take(MaxListedHandles).Select(id => new { Handle = id.Handle.ToString(), Rank = ranks.GetValueOrDefault(id, -1) }).ToArray(),
            ReferenceRank = reference.IsNull ? (int?)null : ranks.GetValueOrDefault(reference, -1),
            Note = "Rang 0 : dessiné en premier, sous tous les autres objets de l'espace.",
        };
    }
}

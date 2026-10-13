using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Reprojection d'objets du dessin d'un système de coordonnées à un autre, changement de datum compris. Les sommets
/// des lignes, polylignes, splines et points sont convertis un à un ; les autres objets (cercles, arcs, textes,
/// blocs, hachures, cotes…) sont déplacés, tournés et mis à l'échelle selon la déformation locale de la projection,
/// calculée en leur centre. Les altitudes ne changent pas. Une seule étape d'annulation.
/// </summary>
internal static class ReprojectTools
{
    private const int MaxReported = 10;

    public static object ReprojectEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var to = reader.RequireString("to").Trim();
        var from = reader.GetString("from")?.Trim() is { Length: > 0 } given ? given : "drawing";
        var assign = reader.GetBool("assign", false);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var filter = EntityFilter.Create(context, reader, transaction);

        using var transform = CoordinateSystemTools.CreateTransform(database, from, to);
        if (transform.IsIdentity)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Systèmes d'origine et d'arrivée identiques ({transform.FromCode}) : rien à reprojeter.");

        // Pas du calcul de la déformation locale : un mètre, ou l'équivalent en degrés.
        var step = transform.SourceAngular ? 1e-5 : 1.0;
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var (exact, local) = (0, 0);
        var skipped = new List<string>();
        foreach (var entity in QueryTools.Source(context, reader, transaction).ToList())
        {
            if (!filter.IsMatch(entity))
                continue;
            if (((LayerTableRecord)transaction.GetObject(layers[entity.Layer], OpenMode.ForRead)).IsLocked)
            {
                skipped.Add($"{entity.Handle} ({Format.DxfName(entity)}) : calque {entity.Layer} verrouillé");
                continue;
            }

            try
            {
                entity.UpgradeOpen();
                if (TransformVertices(entity, transform, transaction))
                    exact++;
                else if (TransformLocally(entity, transform, step))
                    local++;
                else
                    skipped.Add($"{entity.Handle} ({Format.DxfName(entity)}) : sans étendue, non reprojeté");
            }
            catch (AcException ex)
            {
                skipped.Add($"{entity.Handle} ({Format.DxfName(entity)}) : {ex.ErrorStatus}");
            }
        }

        if (exact + local == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                skipped.Count > 0 ? $"Aucun objet reprojeté : {string.Join(" ; ", skipped.Take(3))}." : "Aucun objet ne correspond aux filtres.");

        if (assign)
            CoordinateSystemTools.AssignDrawingSystem(database, transform.ToCode);

        return new
        {
            From = transform.FromCode,
            To = transform.ToCode,
            Reprojected = exact + local,
            VerticesConverted = exact,
            LocallyTransformed = local,
            Skipped = skipped.Count,
            SkippedObjects = skipped.Count > 0 ? skipped.Take(MaxReported).ToArray() : null,
            AssignedToDrawing = assign ? transform.ToCode : null,
            Note = assign ? null : "Le système du dessin n'a pas changé : passez assign=true pour lui attribuer le système d'arrivée.",
        };
    }

    /// <summary>Conversion sommet par sommet des objets linéaires et ponctuels plans (normale Z).</summary>
    private static bool TransformVertices(Entity entity, PointTransform transform, Transaction transaction)
    {
        Point3d Convert(Point3d point)
        {
            var (x, y) = transform.Apply(point.X, point.Y);
            return new Point3d(x, y, point.Z);
        }

        switch (entity)
        {
            case DBPoint point:
                point.Position = Convert(point.Position);
                return true;

            case Line line:
                (line.StartPoint, line.EndPoint) = (Convert(line.StartPoint), Convert(line.EndPoint));
                return true;

            case Polyline polyline when polyline.Normal.IsParallelTo(Vector3d.ZAxis):
                for (var i = 0; i < polyline.NumberOfVertices; i++)
                {
                    var vertex = polyline.GetPoint2dAt(i);
                    var (x, y) = transform.Apply(vertex.X, vertex.Y);
                    polyline.SetPointAt(i, new Point2d(x, y));
                }

                return true;

            case Polyline2d polyline2d when polyline2d.Normal.IsParallelTo(Vector3d.ZAxis):
                foreach (ObjectId id in polyline2d)
                {
                    var vertex = (Vertex2d)transaction.GetObject(id, OpenMode.ForWrite);
                    vertex.Position = Convert(vertex.Position);
                }

                return true;

            case Polyline3d polyline3d:
                foreach (ObjectId id in polyline3d)
                {
                    var vertex = (PolylineVertex3d)transaction.GetObject(id, OpenMode.ForWrite);
                    vertex.Position = Convert(vertex.Position);
                }

                return true;

            case Spline { HasFitData: true } fitted:
                for (var i = 0; i < fitted.NumFitPoints; i++)
                    fitted.SetFitPointAt(i, Convert(fitted.GetFitPointAt(i)));
                return true;

            case Spline spline:
                for (var i = 0; i < spline.NumControlPoints; i++)
                    spline.SetControlPointAt(i, Convert(spline.GetControlPointAt(i)));
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Similitude (déplacement, rotation, échelle uniforme) qui approche la projection au centre de l'objet : les
    /// cercles restent des cercles et les textes gardent leurs proportions.
    /// </summary>
    private static bool TransformLocally(Entity entity, PointTransform transform, double step)
    {
        if (Format.TryGetExtents(entity) is not { } extents)
            return false;

        var center = extents.MinPoint + (extents.MaxPoint - extents.MinPoint) / 2;
        var origin = transform.Apply(center.X, center.Y);
        var alongX = transform.Apply(center.X + step, center.Y);
        var alongY = transform.Apply(center.X, center.Y + step);
        var (a, c) = ((alongX.X - origin.X) / step, (alongX.Y - origin.Y) / step);
        var (b, d) = ((alongY.X - origin.X) / step, (alongY.Y - origin.Y) / step);
        var scale = Math.Sqrt(Math.Abs(a * d - b * c));
        var rotation = Math.Atan2(c, a);
        var matrix = Matrix3d.Displacement(new Vector3d(origin.X - center.X, origin.Y - center.Y, 0))
                     * Matrix3d.Rotation(rotation, Vector3d.ZAxis, center)
                     * Matrix3d.Scaling(scale, center);
        entity.TransformBy(matrix);
        return true;
    }
}

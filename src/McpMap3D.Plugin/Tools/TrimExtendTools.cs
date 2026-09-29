using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Ajuster et prolonger. Les intersections se calculent en projection sur le plan XY, pour que des objets à des
/// altitudes différentes (fréquent en cartographie) se coupent comme à l'écran en vue de dessus.
/// </summary>
internal static class TrimExtendTools
{
    private static readonly Plane Horizontal = new(Point3d.Origin, Vector3d.ZAxis);

    /// <summary>
    /// Ajuste des courbes sur des arêtes de coupe : chaque courbe est découpée à ses intersections, et le morceau
    /// le plus proche du point désigné est supprimé, comme en cliquant avec la commande AJUSTER.
    /// </summary>
    public static object TrimEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var boundaries = OpenBoundaries(context, reader);

        var results = new List<object>();
        foreach (var (id, pick) in ReadTargets(context, reader))
        {
            var curve = EditTools.OpenForWrite(id, transaction, context) as Curve
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe.");

            var parameters = Intersections(curve, boundaries, id, Intersect.OnBothOperands)
                .Select(point => curve.GetParameterAtPoint(curve.GetClosestPointTo(point, Vector3d.ZAxis, false)))
                .Where(parameter => curve.Closed || (parameter - curve.StartParam > 1e-9 && curve.EndParam - parameter > 1e-9))
                .OrderBy(parameter => parameter)
                .ToList();
            parameters = WithoutDuplicates(parameters);

            if (parameters.Count == 0 || (curve.Closed && parameters.Count < 2))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"La courbe {id.Handle} ne coupe pas les arêtes de coupe (ou pas assez de fois pour être ajustée).");

            var pieces = Split(curve, parameters);
            var removed = pieces.OrderBy(piece => PlanDistance(piece, pick)).First();
            var kept = pieces.Where(piece => !ReferenceEquals(piece, removed)).ToList();
            removed.Dispose();

            results.Add(new { Handle = id.Handle.ToString(), Kept = Replace(context, curve, kept) });
        }

        return new { Trimmed = results.Count, Results = results };
    }

    /// <summary>
    /// Prolonge des courbes (lignes, arcs, polylignes ouvertes à segment d'extrémité droit) jusqu'à la limite la plus
    /// proche. L'extrémité est celle proche du point désigné, ou fixée par « end ».
    /// </summary>
    public static object ExtendEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var boundaries = OpenBoundaries(context, reader);

        var results = new List<object>();
        foreach (var (id, pick) in ReadTargets(context, reader))
        {
            var curve = EditTools.OpenForWrite(id, transaction, context) as Curve
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe.");
            if (curve.Closed)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La courbe {id.Handle} est fermée : elle ne peut pas être prolongée.");

            var atStart = PlanDistance(curve.StartPoint, pick) < PlanDistance(curve.EndPoint, pick);
            var candidates = Intersections(curve, boundaries, id, Intersect.ExtendThis);
            var extended = curve switch
            {
                Line line => ExtendLine(line, atStart, candidates),
                Arc arc => ExtendArc(arc, atStart, candidates),
                Polyline polyline => ExtendPolyline(polyline, atStart, candidates),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Prolongement non pris en charge pour {Format.DxfName(curve)} : lignes, arcs et polylignes 2D uniquement."),
            };

            if (extended is null)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Aucune limite n'est atteinte en prolongeant l'extrémité {(atStart ? "de départ" : "d'arrivée")} de {id.Handle}.");

            results.Add(new
            {
                Handle = id.Handle.ToString(),
                End = atStart ? "start" : "end",
                ExtendedBy = Format.Number(extended.Value),
                NewPoint = Format.Point(atStart ? curve.StartPoint : curve.EndPoint),
            });
        }

        return new { Extended = results.Count, Results = results };
    }

    // ----- Prolongement -----

    private static double? ExtendLine(Line line, bool atStart, List<Point3d> candidates)
    {
        var end = atStart ? line.StartPoint : line.EndPoint;
        var outward = Flat(atStart ? line.StartPoint - line.EndPoint : line.EndPoint - line.StartPoint).GetNormal();
        var best = candidates
            .Select(point => (Point: point, Distance: Flat(point - end).DotProduct(outward)))
            .Where(item => item.Distance > 1e-9)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();
        if (best.Distance <= 0)
            return null;

        // On garde la pente de la ligne : le nouveau point est pris le long de sa direction 3D.
        var direction = (atStart ? line.StartPoint - line.EndPoint : line.EndPoint - line.StartPoint);
        var planLength = Flat(direction).Length;
        var newPoint = end + direction * (best.Distance / planLength);
        if (atStart)
            line.StartPoint = newPoint;
        else
            line.EndPoint = newPoint;

        return best.Distance;
    }

    private static double? ExtendArc(Arc arc, bool atStart, List<Point3d> candidates)
    {
        var sweep = Normalize(arc.EndAngle - arc.StartAngle);
        var gap = 2 * Math.PI - sweep;
        double? best = null;
        foreach (var point in candidates)
        {
            var angle = Math.Atan2(point.Y - arc.Center.Y, point.X - arc.Center.X);
            var delta = atStart ? Normalize(arc.StartAngle - angle) : Normalize(angle - arc.EndAngle);
            if (delta > 1e-9 && delta < gap - 1e-9 && (best is null || delta < best))
                best = delta;
        }

        if (best is not double travel)
            return null;

        if (atStart)
            arc.StartAngle -= travel;
        else
            arc.EndAngle += travel;

        return travel * arc.Radius;
    }

    private static double? ExtendPolyline(Polyline polyline, bool atStart, List<Point3d> candidates)
    {
        var segment = atStart ? 0 : polyline.NumberOfVertices - 2;
        if (polyline.GetBulgeAt(segment) != 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le segment d'extrémité de {polyline.Handle} est un arc : seuls les segments droits sont prolongés.");

        var endIndex = atStart ? 0 : polyline.NumberOfVertices - 1;
        var otherIndex = atStart ? 1 : polyline.NumberOfVertices - 2;
        var end = polyline.GetPoint3dAt(endIndex);
        var outward = Flat(end - polyline.GetPoint3dAt(otherIndex)).GetNormal();
        var best = candidates
            .Select(point => Flat(point - end).DotProduct(outward))
            .Where(distance => distance > 1e-9)
            .DefaultIfEmpty(0)
            .Min();
        if (best <= 0)
            return null;

        var newPoint = new Point2d(end.X + outward.X * best, end.Y + outward.Y * best);
        polyline.SetPointAt(endIndex, newPoint);
        return best;
    }

    // ----- Ajustement -----

    internal static List<Curve> Split(Curve curve, List<double> parameters)
    {
        try
        {
            return curve.GetSplitCurves(new DoubleCollection([.. parameters])).Cast<Curve>().ToList();
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu découper {curve.Handle} ({ex.ErrorStatus}).");
        }
    }

    /// <summary>
    /// Remplace la courbe par les morceaux conservés. Le premier morceau est reporté sur l'objet d'origine quand
    /// c'est possible (ligne, arc, polyligne), ce qui garde son handle et ses données d'objet.
    /// </summary>
    internal static string[] Replace(ToolContext context, Curve original, List<Curve> kept)
    {
        var transaction = context.RequireTransaction();
        var handles = new List<string>();
        var reuseOriginal = kept.Count > 0 && CopyGeometry(kept[0], original);
        if (reuseOriginal)
        {
            kept[0].Dispose();
            handles.Add(original.Handle.ToString());
        }
        else
        {
            original.Erase();
        }

        var space = (BlockTableRecord)transaction.GetObject(original.OwnerId, OpenMode.ForWrite);
        foreach (var piece in kept.Skip(reuseOriginal ? 1 : 0))
        {
            space.AppendEntity(piece);
            transaction.AddNewlyCreatedDBObject(piece, true);
            handles.Add(piece.Handle.ToString());
        }

        return [.. handles];
    }

    internal static bool CopyGeometry(Curve source, Curve target)
    {
        switch ((source, target))
        {
            case (Line from, Line to):
                to.StartPoint = from.StartPoint;
                to.EndPoint = from.EndPoint;
                return true;

            case (Arc from, Arc to):
                to.Center = from.Center;
                to.Radius = from.Radius;
                to.StartAngle = from.StartAngle;
                to.EndAngle = from.EndAngle;
                return true;

            case (Polyline from, Polyline to):
                while (to.NumberOfVertices > 2)
                    to.RemoveVertexAt(to.NumberOfVertices - 1);

                // Deux sommets minimum : on réécrit les deux premiers, puis on ajoute les suivants.
                for (var index = 0; index < from.NumberOfVertices; index++)
                {
                    if (index < 2)
                    {
                        to.SetPointAt(index, from.GetPoint2dAt(index));
                        to.SetBulgeAt(index, from.GetBulgeAt(index));
                        to.SetStartWidthAt(index, from.GetStartWidthAt(index));
                        to.SetEndWidthAt(index, from.GetEndWidthAt(index));
                    }
                    else
                    {
                        to.AddVertexAt(index, from.GetPoint2dAt(index), from.GetBulgeAt(index),
                            from.GetStartWidthAt(index), from.GetEndWidthAt(index));
                    }
                }

                to.Closed = from.Closed;
                return true;

            default:
                return false;
        }
    }

    // ----- Commun -----

    private static List<Entity> OpenBoundaries(ToolContext context, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        return Handles.Resolve(context, reader, "boundaries")
            .Select(id => (Entity)transaction.GetObject(id, OpenMode.ForRead))
            .ToList();
    }

    /// <summary>Cibles : [{ handle, pick }], le point désignant le morceau à supprimer ou l'extrémité à prolonger.</summary>
    private static List<(ObjectId Id, Point3d Pick)> ReadTargets(ToolContext context, ArgReader reader)
    {
        if (!reader.TryGet("targets", out var element) || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le paramètre « targets » attend une liste [{ \"handle\": \"…\", \"pick\": [x, y] }].");

        var targets = new List<(ObjectId, Point3d)>();
        foreach (var item in element.EnumerateArray())
        {
            var itemReader = new ArgReader(item);
            var id = Handles.Resolve(context, itemReader, "handle").Single();
            targets.Add((id, itemReader.RequirePoint("pick")));
        }

        return targets;
    }

    private static List<Point3d> Intersections(Curve curve, List<Entity> boundaries, ObjectId self, Intersect mode)
    {
        var points = new List<Point3d>();
        foreach (var boundary in boundaries)
        {
            if (boundary.ObjectId == self)
                continue;

            var found = new Point3dCollection();
            try
            {
                curve.IntersectWith(boundary, mode, Horizontal, found, IntPtr.Zero, IntPtr.Zero);
            }
            catch (AcException)
            {
                continue; // Objet sans géométrie d'intersection (texte, bloc complexe…).
            }

            points.AddRange(found.Cast<Point3d>());
        }

        return points;
    }

    private static Vector3d Flat(Vector3d vector) => new(vector.X, vector.Y, 0);

    private static double PlanDistance(Point3d a, Point3d b) => new Point2d(a.X, a.Y).GetDistanceTo(new Point2d(b.X, b.Y));

    private static double PlanDistance(Curve curve, Point3d pick) =>
        PlanDistance(curve.GetClosestPointTo(new Point3d(pick.X, pick.Y, curve.StartPoint.Z), Vector3d.ZAxis, false), pick);

    private static double Normalize(double angle)
    {
        var twoPi = 2 * Math.PI;
        angle %= twoPi;
        return angle < 0 ? angle + twoPi : angle;
    }

    /// <summary>Paramètres triés : on écarte ceux trop proches du précédent (intersection comptée deux fois).</summary>
    private static List<double> WithoutDuplicates(List<double> sorted)
    {
        var result = new List<double>(sorted.Count);
        foreach (var value in sorted)
        {
            if (result.Count == 0 || value - result[^1] > 1e-7)
                result.Add(value);
        }

        return result;
    }
}

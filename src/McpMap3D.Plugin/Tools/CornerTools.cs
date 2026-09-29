using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Raccords (arcs tangents) et chanfreins, entre deux lignes ou sur les sommets d'une polyligne.
/// Calcul dans le plan XY : les lignes doivent être à la même altitude.
/// </summary>
internal static class CornerTools
{
    public static object Fillet(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var radius = reader.RequireDouble("radius");
        if (radius < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le rayon ne peut pas être négatif (0 : raccord en angle vif).");

        return Apply(context, reader, new Corner(Radius: radius, Distance1: 0, Distance2: 0));
    }

    public static object Chamfer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var distance1 = reader.RequireDouble("distance1");
        var distance2 = reader.GetDouble("distance2", distance1);
        if (distance1 < 0 || distance2 < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les distances de chanfrein ne peuvent pas être négatives.");

        return Apply(context, reader, new Corner(Radius: null, Distance1: distance1, Distance2: distance2));
    }

    /// <summary>Radius renseigné : raccord ; sinon chanfrein de distances Distance1 et Distance2.</summary>
    private sealed record Corner(double? Radius, double Distance1, double Distance2);

    private static object Apply(ToolContext context, ArgReader reader, Corner corner)
    {
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);
        var entities = ids.Select(id => EditTools.OpenForWrite(id, transaction, context)).ToList();

        return entities switch
        {
            [Line first, Line second] => TwoLines(context, first, second, corner),
            [Polyline polyline] => PolylineCorners(polyline, corner),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez soit deux lignes (LINE), soit une polyligne 2D (LWPOLYLINE) dont traiter tous les sommets."),
        };
    }

    /// <summary>
    /// Raccord ou chanfrein entre deux lignes, qui peuvent ne pas se toucher. De chaque ligne, on garde le côté de
    /// l'extrémité la plus éloignée du point d'intersection.
    /// </summary>
    private static object TwoLines(ToolContext context, Line first, Line second, Corner corner)
    {
        var corner2d = Intersection(first, second)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux lignes sont parallèles : pas de coin à traiter.");
        var apex = new Point3d(corner2d.X, corner2d.Y, first.StartPoint.Z);

        var (far1, keepStart1) = FarEnd(first, apex);
        var (far2, keepStart2) = FarEnd(second, apex);
        var direction1 = Flat(far1 - apex).GetNormal();
        var direction2 = Flat(far2 - apex).GetNormal();
        var angle = direction1.GetAngleTo(direction2);
        if (angle < 1e-6 || Math.PI - angle < 1e-6)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux lignes sont alignées : pas de coin à traiter.");

        var available1 = Flat(far1 - apex).Length;
        var available2 = Flat(far2 - apex).Length;
        Entity? added = null;
        Point3d end1, end2;

        if (corner.Radius is double radius)
        {
            var tangent = radius / Math.Tan(angle / 2);
            if (tangent > available1 + 1e-9 || tangent > available2 + 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Rayon trop grand : il faudrait {Format.Number(tangent)} de ligne de chaque côté du coin.");

            end1 = apex + direction1 * tangent;
            end2 = apex + direction2 * tangent;
            if (radius > 0)
            {
                var bisector = (direction1 + direction2).GetNormal();
                var center = apex + bisector * (radius / Math.Sin(angle / 2));
                added = TangentArc(center, radius, end1, end2, angle);
            }
        }
        else
        {
            if (corner.Distance1 > available1 + 1e-9 || corner.Distance2 > available2 + 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Distances de chanfrein plus longues que les lignes.");

            end1 = apex + direction1 * corner.Distance1;
            end2 = apex + direction2 * corner.Distance2;
            if (end1.DistanceTo(end2) > 1e-9)
                added = new Line(end1, end2);
        }

        SetKeptSegment(first, far1, end1, keepStart1);
        SetKeptSegment(second, far2, end2, keepStart2);

        string? addedHandle = null;
        if (added is not null)
        {
            var transaction = context.RequireTransaction();
            added.SetPropertiesFrom(first);
            var space = (BlockTableRecord)transaction.GetObject(first.OwnerId, OpenMode.ForWrite);
            space.AppendEntity(added);
            transaction.AddNewlyCreatedDBObject(added, true);
            addedHandle = added.Handle.ToString();
        }

        return new
        {
            Mode = corner.Radius is null ? "chamfer" : "fillet",
            Lines = new[] { first.Handle.ToString(), second.Handle.ToString() },
            Added = addedHandle,
            AddedLength = added is Curve curve ? Format.Number(curve.GetDistanceAtParameter(curve.EndParam)) : (double?)null,
            Corner = Format.Point(apex),
        };
    }

    /// <summary>
    /// Raccorde ou chanfreine tous les sommets d'une polyligne situés entre deux segments droits. La polyligne est
    /// modifiée sur place (handle et données d'objet conservés) ; les sommets impossibles sont signalés.
    /// </summary>
    private static object PolylineCorners(Polyline polyline, Corner corner)
    {
        var count = polyline.NumberOfVertices;
        var closed = polyline.Closed;
        var points = Enumerable.Range(0, count).Select(polyline.GetPoint2dAt).ToArray();
        var bulges = Enumerable.Range(0, count).Select(polyline.GetBulgeAt).ToArray();
        var constantWidth = Enumerable.Range(0, count).All(i => polyline.GetStartWidthAt(i) == polyline.GetStartWidthAt(0)
            && polyline.GetEndWidthAt(i) == polyline.GetStartWidthAt(0)) ? polyline.GetStartWidthAt(0) : 0;

        var vertices = new List<(Point2d Point, double Bulge)>();
        var treated = 0;
        var skipped = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var isEnd = !closed && (index == 0 || index == count - 1);
            var previous = (index - 1 + count) % count;
            var next = (index + 1) % count;
            if (isEnd || bulges[previous] != 0 || bulges[index] != 0)
            {
                vertices.Add((points[index], bulges[index]));
                continue;
            }

            var apex = points[index];
            var toPrevious = points[previous] - apex;
            var toNext = points[next] - apex;
            var angle = toPrevious.GetAngleTo(toNext);
            if (toPrevious.Length < 1e-9 || toNext.Length < 1e-9 || angle < 1e-6 || Math.PI - angle < 1e-6)
            {
                vertices.Add((points[index], bulges[index]));
                continue; // Sommet aligné ou doublon : rien à traiter.
            }

            // Chaque segment peut céder au plus la moitié de sa longueur, pour laisser la place au coin voisin.
            var (back, forward) = corner.Radius is double radius
                ? (radius / Math.Tan(angle / 2), radius / Math.Tan(angle / 2))
                : (corner.Distance1, corner.Distance2);
            if (back > toPrevious.Length / 2 + 1e-9 || forward > toNext.Length / 2 + 1e-9)
            {
                skipped.Add(index);
                vertices.Add((points[index], bulges[index]));
                continue;
            }

            var start = apex + toPrevious.GetNormal() * back;
            var end = apex + toNext.GetNormal() * forward;
            var bulge = 0.0;
            if (corner.Radius is > 0)
            {
                // Arc tangent : balayage π − angle, sens selon le virage (à gauche : positif).
                var turn = (apex - points[previous]).GetNormal();
                var leftTurn = turn.X * toNext.Y - turn.Y * toNext.X > 0;
                bulge = Math.Tan((Math.PI - angle) / 4) * (leftTurn ? 1 : -1);
            }

            vertices.Add((start, bulge));
            if (start.GetDistanceTo(end) > 1e-9)
                vertices.Add((end, 0));

            treated++;
        }

        // Réécriture sur place : on garde deux sommets (minimum d'une polyligne), puis on reconstruit.
        while (polyline.NumberOfVertices > 2)
            polyline.RemoveVertexAt(polyline.NumberOfVertices - 1);

        for (var index = 0; index < vertices.Count; index++)
        {
            var (point, bulge) = vertices[index];
            if (index < 2)
            {
                polyline.SetPointAt(index, point);
                polyline.SetBulgeAt(index, bulge);
                polyline.SetStartWidthAt(index, constantWidth);
                polyline.SetEndWidthAt(index, constantWidth);
            }
            else
            {
                polyline.AddVertexAt(index, point, bulge, constantWidth, constantWidth);
            }
        }

        return new
        {
            Mode = corner.Radius is null ? "chamfer" : "fillet",
            Handle = polyline.Handle.ToString(),
            CornersTreated = treated,
            CornersSkipped = skipped.Count > 0 ? skipped.Select(index => $"sommet {index} : segments trop courts").ToArray() : null,
            Vertices = polyline.NumberOfVertices,
            Length = Format.Number(polyline.Length),
            Area = polyline.Closed ? Format.Number(polyline.Area) : (double?)null,
        };
    }

    private static Point2d? Intersection(Line first, Line second)
    {
        var a = new Line2d(new Point2d(first.StartPoint.X, first.StartPoint.Y), new Point2d(first.EndPoint.X, first.EndPoint.Y));
        var b = new Line2d(new Point2d(second.StartPoint.X, second.StartPoint.Y), new Point2d(second.EndPoint.X, second.EndPoint.Y));
        var points = a.IntersectWith(b);
        return points is { Length: > 0 } ? points[0] : null;
    }

    private static (Point3d Far, bool IsStart) FarEnd(Line line, Point3d apex) =>
        Flat(line.StartPoint - apex).Length >= Flat(line.EndPoint - apex).Length
            ? (line.StartPoint, true)
            : (line.EndPoint, false);

    private static void SetKeptSegment(Line line, Point3d far, Point3d newEnd, bool farIsStart)
    {
        var end = new Point3d(newEnd.X, newEnd.Y, (farIsStart ? line.EndPoint : line.StartPoint).Z);
        if (farIsStart)
            line.EndPoint = end;
        else
            line.StartPoint = end;
    }

    /// <summary>Arc tangent aux deux lignes, parcouru dans le sens trigonométrique sur le petit côté.</summary>
    private static Arc TangentArc(Point3d center, double radius, Point3d end1, Point3d end2, double angle)
    {
        var a1 = Math.Atan2(end1.Y - center.Y, end1.X - center.X);
        var a2 = Math.Atan2(end2.Y - center.Y, end2.X - center.X);
        var sweep = Math.PI - angle;
        var ccw = (a2 - a1) % (2 * Math.PI);
        if (ccw < 0)
            ccw += 2 * Math.PI;

        return Math.Abs(ccw - sweep) < 1e-6
            ? new Arc(center, radius, a1, a2)
            : new Arc(center, radius, a2, a1);
    }

    private static Vector3d Flat(Vector3d vector) => new(vector.X, vector.Y, 0);
}

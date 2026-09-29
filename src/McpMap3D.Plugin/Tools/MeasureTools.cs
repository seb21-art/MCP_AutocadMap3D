using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Mesures sans modification du dessin : intersections entre objets et mesures le long d'une courbe (points à une
/// distance, abscisse et décalage d'un point, division, mesure à intervalle).
/// </summary>
internal static class MeasureTools
{
    private const int MaxObjects = 200;
    private const int MaxPoints = 10000;

    /// <summary>
    /// Intersections de chaque paire d'objets, vues de dessus par défaut (objets à des altitudes différentes compris),
    /// ou en 3D. Les points sont donnés sur le premier objet de la paire.
    /// </summary>
    public static object GetIntersections(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader).Distinct().ToList();
        if (ids.Count < 2 || ids.Count > MaxObjects)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Indiquez de 2 à {MaxObjects} objets.");

        var extend = (reader.GetString("extend") ?? "none").Trim().ToLowerInvariant();
        var mode = extend switch
        {
            "none" => Intersect.OnBothOperands,
            "both" => Intersect.ExtendBoth,
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« extend » vaut none (objets tels quels) ou both (lignes, arcs et courbes ouvertes prolongés), pas « {extend} »."),
        };
        var plan = reader.GetBool("plan", true);
        var horizontal = new Plane(Point3d.Origin, Vector3d.ZAxis);

        var entities = ids.Select(id => transaction.GetObject(id, OpenMode.ForRead) as Entity
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un objet graphique du dessin.")).ToList();

        var pairs = new List<object>();
        var unsupported = new List<string>();
        var total = 0;
        for (var i = 0; i < entities.Count; i++)
        {
            for (var j = i + 1; j < entities.Count; j++)
            {
                var (first, second) = (entities[i], entities[j]);
                var found = new Point3dCollection();
                try
                {
                    if (plan)
                        first.IntersectWith(second, mode, horizontal, found, IntPtr.Zero, IntPtr.Zero);
                    else
                        first.IntersectWith(second, mode, found, IntPtr.Zero, IntPtr.Zero);
                }
                catch (AcException)
                {
                    unsupported.Add($"{first.Handle}/{second.Handle}");
                    continue;
                }

                var points = Distinct(found.Cast<Point3d>().Select(point => OnFirst(first, point, plan)).ToList(), Size(first, second));
                if (points.Count == 0)
                    continue;

                total += points.Count;
                if (total > MaxPoints)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Plus de {MaxPoints} intersections : réduisez la sélection.");

                pairs.Add(new
                {
                    First = first.Handle.ToString(),
                    Second = second.Handle.ToString(),
                    Points = points.Select(Format.Point).ToArray(),
                });
            }
        }

        return new
        {
            Count = total,
            Pairs = pairs,
            Plan = plan,
            Extend = extend,
            Unsupported = unsupported.Count > 0 ? unsupported : null,
        };
    }

    /// <summary>
    /// Mesures le long d'une courbe : longueur, points à des distances depuis le début, abscisse et décalage de points
    /// (vus de dessus, décalage positif à gauche), division en parties égales, points à intervalle régulier.
    /// </summary>
    public static object MeasureCurve(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = Handles.Resolve(context, reader, "handle").Single();
        var curve = transaction.GetObject(id, OpenMode.ForRead) as Curve
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe (ligne, arc, cercle, polyligne, spline, ellipse).");
        if (curve is Xline or Ray)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une droite infinie n'a pas de longueur mesurable.");

        var length = curve.GetDistanceAtParameter(curve.EndParam);
        var closed = curve.Closed;

        var distances = ReadNumbers(reader, "distances");
        var points = reader.Has("points") ? reader.RequirePoints("points", minimum: 1) : [];
        var divide = reader.Has("divide") ? reader.GetInt("divide", 2, min: 2, max: MaxPoints) : (int?)null;
        var interval = reader.Has("interval") ? reader.RequireDouble("interval") : (double?)null;
        if (interval <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'intervalle doit être strictement positif.");
        if (interval is double step && length / step > MaxPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Intervalle trop petit : plus de {MaxPoints} points.");
        if (distances.Count + points.Count > MaxPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{MaxPoints} distances et points au maximum.");

        return new
        {
            Handle = id.Handle.ToString(),
            Type = Format.DxfName(curve),
            Length = Format.Number(length),
            Closed = closed,
            Start = Format.Point(curve.StartPoint),
            End = Format.Point(curve.EndPoint),
            Area = closed ? Format.Number(Math.Abs(curve.Area)) : (double?)null,
            AtDistances = distances.Count > 0 ? distances.Select(distance => StationAt(curve, WrapDistance(distance, length, closed))).ToList() : null,
            Stations = points.Count > 0 ? points.Select(point => StationOf(curve, point, length, closed)).ToList() : null,
            Divided = divide is int parts ? DivisionDistances(length, parts, closed).Select(distance => StationAt(curve, distance)).ToList() : null,
            Measured = interval is double every ? IntervalDistances(length, every, closed).Select(distance => StationAt(curve, distance)).ToList() : null,
        };
    }

    /// <summary>Point et direction de la tangente vue de dessus (degrés, 0 vers +X, sens trigonométrique).</summary>
    private static object StationAt(Curve curve, double distance)
    {
        var parameter = curve.GetParameterAtDistance(distance);
        var tangent = curve.GetFirstDerivative(parameter);
        return new
        {
            Distance = Format.Number(distance),
            Point = Format.Point(curve.GetPointAtParameter(parameter)),
            Angle = Format.Degrees(Normalize(Math.Atan2(tangent.Y, tangent.X))),
        };
    }

    /// <summary>
    /// Abscisse curviligne et décalage d'un point : point de la courbe le plus proche vu de dessus, distance depuis le
    /// début, et distance en plan, positive à gauche du sens de la courbe.
    /// </summary>
    private static object StationOf(Curve curve, Point3d point, double length, bool closed)
    {
        Point3d closest;
        try
        {
            closest = curve.GetClosestPointTo(point, Vector3d.ZAxis, false);
        }
        catch (AcException)
        {
            closest = curve.GetClosestPointTo(point, false); // Courbe dans un plan vertical.
        }

        var parameter = curve.GetParameterAtPoint(closest);
        var distance = curve.GetDistanceAtParameter(parameter);
        var tangent = curve.GetFirstDerivative(parameter);
        var offset = new Vector2d(point.X - closest.X, point.Y - closest.Y);
        var side = tangent.X * offset.Y - tangent.Y * offset.X;
        var tolerance = Math.Max(length, 1e-9) * 1e-9;
        return new
        {
            Point = Format.Point(point),
            Closest = Format.Point(closest),
            Distance = Format.Number(distance),
            Offset = Format.Number(side < 0 ? -offset.Length : offset.Length),
            AtEnd = closed ? null : distance <= tolerance ? "start" : distance >= length - tolerance ? "end" : null,
        };
    }

    /// <summary>Distance ramenée sur la courbe : modulo la longueur si elle est fermée, erreur sinon.</summary>
    private static double WrapDistance(double distance, double length, bool closed)
    {
        if (closed)
            return ((distance % length) + length) % length;

        var tolerance = Math.Max(length, 1e-9) * 1e-9;
        return distance >= -tolerance && distance <= length + tolerance
            ? Math.Clamp(distance, 0, length)
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"La distance {distance} sort de la courbe, longue de {Format.Number(length)}.");
    }

    /// <summary>Division en parties égales : points intérieurs d'une courbe ouverte, ou départ compris si elle est fermée.</summary>
    private static IEnumerable<double> DivisionDistances(double length, int parts, bool closed) =>
        Enumerable.Range(closed ? 0 : 1, closed ? parts : parts - 1).Select(index => length * index / parts);

    /// <summary>Points à intervalle régulier depuis le début, jusqu'à la fin (exclue si la courbe est fermée).</summary>
    private static IEnumerable<double> IntervalDistances(double length, double interval, bool closed)
    {
        var tolerance = Math.Max(length, 1e-9) * 1e-9;
        for (var index = 1; ; index++)
        {
            var distance = index * interval;
            if (distance > length + tolerance || (closed && distance >= length - tolerance))
                yield break;

            yield return Math.Min(distance, length);
        }
    }

    private static List<double> ReadNumbers(ArgReader reader, string name)
    {
        if (!reader.TryGet(name, out var value))
            return [];

        return value.ValueKind switch
        {
            JsonValueKind.Number => [value.GetDouble()],
            JsonValueKind.Array when value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number) =>
                [.. value.EnumerateArray().Select(item => item.GetDouble())],
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » doit être un nombre ou un tableau de nombres."),
        };
    }

    /// <summary>Point d'intersection ramené sur le premier objet, qui garde ainsi son altitude.</summary>
    private static Point3d OnFirst(Entity first, Point3d point, bool plan)
    {
        if (first is not Curve curve)
            return point;

        try
        {
            return plan ? curve.GetClosestPointTo(point, Vector3d.ZAxis, false) : point;
        }
        catch (AcException)
        {
            return point;
        }
    }

    /// <summary>Taille de la paire d'objets, pour la tolérance de dédoublonnage.</summary>
    private static double Size(Entity first, Entity second)
    {
        var size = 0.0;
        foreach (var entity in new[] { first, second })
        {
            if (Format.TryGetExtents(entity) is Extents3d extents)
                size = Math.Max(size, extents.MinPoint.DistanceTo(extents.MaxPoint));
        }

        return size;
    }

    /// <summary>Points distincts : une intersection tangente peut être renvoyée deux fois.</summary>
    private static List<Point3d> Distinct(List<Point3d> points, double size)
    {
        var tolerance = Math.Max(size, 1) * 1e-9;
        var result = new List<Point3d>();
        foreach (var point in points)
        {
            if (!result.Any(existing => existing.DistanceTo(point) <= tolerance))
                result.Add(point);
        }

        return result;
    }

    private static double Normalize(double angle)
    {
        var twoPi = 2 * Math.PI;
        return ((angle % twoPi) + twoPi) % twoPi;
    }
}

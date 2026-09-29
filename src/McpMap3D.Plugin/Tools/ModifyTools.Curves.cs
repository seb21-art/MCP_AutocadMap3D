using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Édition des courbes : jonction en polylignes, coupure en un ou deux points, sommets d'une polyligne.
/// </summary>
internal static partial class ModifyTools
{
    private const int MaxJoined = 5000;
    private const int MaxListedVertices = 500;

    // ----- Jonction -----

    /// <summary>
    /// Joint des lignes, arcs et polylignes 2D ouvertes dont les extrémités se touchent, en polylignes (fermées quand
    /// la chaîne se referme), comme la commande JOINDRE. Une polyligne de la chaîne est conservée (handle et données
    /// d'objet), sinon la nouvelle polyligne reprend les propriétés du premier objet.
    /// </summary>
    public static object JoinEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);
        if (ids.Count < 2 || ids.Count > MaxJoined)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Indiquez de 2 à {MaxJoined} objets à joindre.");

        var tolerance = reader.GetDouble("tolerance", 1e-6);
        if (tolerance <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La tolérance doit être strictement positive.");

        var pieces = new List<Piece>();
        foreach (var id in ids.Distinct())
            pieces.Add(ReadPiece(EditTools.OpenForWrite(id, transaction, context)));

        var elevation = pieces[0].Elevation;
        if (pieces.FirstOrDefault(piece => Math.Abs(piece.Elevation - elevation) > tolerance) is { } other)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"{other.Source.Handle} est à l'altitude {Format.Number(other.Elevation)}, {pieces[0].Source.Handle} à " +
                $"{Format.Number(elevation)} : seuls des objets de même altitude se joignent.");

        if (pieces.Any(piece => piece.Source.OwnerId != pieces[0].Source.OwnerId))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les objets à joindre doivent être dans le même espace.");

        var results = new List<object>();
        var notJoined = new List<string>();
        foreach (var chain in Chain(pieces, tolerance))
        {
            if (chain.Count == 1)
            {
                notJoined.Add(chain[0].Source.Handle.ToString());
                continue;
            }

            results.Add(JoinChain(context, chain, pieces, tolerance, elevation));
        }

        if (results.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucune extrémité commune entre ces objets (tolérance {tolerance}) : rien n'a été joint. Augmentez « tolerance » " +
                "pour combler de petits écarts.");

        return new
        {
            Joined = results.Count,
            Results = results,
            NotJoined = notJoined.Count > 0 ? notJoined : null,
        };
    }

    /// <summary>Morceau à joindre, dans le plan XY : sommets, puis renflement et largeurs de chaque segment.</summary>
    private sealed class Piece
    {
        public required Entity Source { get; init; }
        public required double Elevation { get; init; }
        public required List<Point2d> Points { get; init; }
        public required List<double> Bulges { get; init; }
        public required List<(double Start, double End)> Widths { get; init; }

        public Point2d Start => Points[0];

        public Point2d End => Points[^1];

        /// <summary>Même morceau parcouru à l'envers : les arcs changent de sens, les largeurs s'inversent.</summary>
        public Piece Reversed() => new()
        {
            Source = Source,
            Elevation = Elevation,
            Points = [.. Enumerable.Reverse(Points)],
            Bulges = [.. Enumerable.Reverse(Bulges).Select(bulge => -bulge)],
            Widths = [.. Enumerable.Reverse(Widths).Select(width => (width.End, width.Start))],
        };
    }

    private static Piece ReadPiece(Entity entity)
    {
        switch (entity)
        {
            case Line line when Math.Abs(line.StartPoint.Z - line.EndPoint.Z) <= Math.Max(line.Length, 1) * 1e-9:
                return new Piece
                {
                    Source = line,
                    Elevation = line.StartPoint.Z,
                    Points = [Flat(line.StartPoint), Flat(line.EndPoint)],
                    Bulges = [0],
                    Widths = [(0, 0)],
                };

            case Arc arc when arc.Normal.IsParallelTo(Vector3d.ZAxis):
            {
                // Renflement : tangente du quart de l'angle au centre, positif dans le sens trigonométrique vu de dessus.
                var sweep = arc.EndAngle - arc.StartAngle;
                if (sweep <= 0)
                    sweep += 2 * Math.PI;

                return new Piece
                {
                    Source = arc,
                    Elevation = arc.Center.Z,
                    Points = [Flat(arc.StartPoint), Flat(arc.EndPoint)],
                    Bulges = [Math.Tan(sweep / 4) * Math.Sign(arc.Normal.Z)],
                    Widths = [(0, 0)],
                };
            }

            case Polyline { Closed: false } polyline when polyline.Normal.IsParallelTo(Vector3d.ZAxis):
            {
                var sign = Math.Sign(polyline.Normal.Z);
                var segments = polyline.NumberOfVertices - 1;
                return new Piece
                {
                    Source = polyline,
                    Elevation = polyline.GetPoint3dAt(0).Z,
                    Points = [.. Enumerable.Range(0, polyline.NumberOfVertices).Select(index => Flat(polyline.GetPoint3dAt(index)))],
                    Bulges = [.. Enumerable.Range(0, segments).Select(index => polyline.GetBulgeAt(index) * sign)],
                    Widths = [.. Enumerable.Range(0, segments).Select(index => (polyline.GetStartWidthAt(index), polyline.GetEndWidthAt(index)))],
                };
            }

            case Polyline { Closed: true }:
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La polyligne {entity.Handle} est fermée : elle ne peut pas être jointe.");

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"{entity.Handle} ({Format.DxfName(entity)}) : seuls les lignes, arcs et polylignes 2D ouvertes, " +
                    "horizontaux (vus de dessus), se joignent.");
        }
    }

    /// <summary>
    /// Chaînes de morceaux bout à bout : chacune part du premier morceau libre (dans l'ordre donné), s'allonge par sa
    /// fin puis par son début, en retournant les morceaux au besoin, et s'arrête quand elle se referme.
    /// </summary>
    private static List<List<Piece>> Chain(List<Piece> pieces, double tolerance)
    {
        var free = new List<Piece>(pieces);
        var chains = new List<List<Piece>>();
        while (free.Count > 0)
        {
            var chain = new List<Piece> { free[0] };
            free.RemoveAt(0);

            while (!Closes(chain, tolerance) && Take(free, chain[^1].End, tolerance, atStart: true) is { } next)
                chain.Add(next);

            while (!Closes(chain, tolerance) && Take(free, chain[0].Start, tolerance, atStart: false) is { } previous)
                chain.Insert(0, previous);

            chains.Add(chain);
        }

        return chains;
    }

    /// <summary>
    /// Retire des morceaux libres le premier qui touche le point : orienté pour commencer au point (atStart) ou pour
    /// y finir.
    /// </summary>
    private static Piece? Take(List<Piece> free, Point2d point, double tolerance, bool atStart)
    {
        for (var index = 0; index < free.Count; index++)
        {
            var piece = free[index];
            var touchesStart = piece.Start.GetDistanceTo(point) <= tolerance;
            var touchesEnd = piece.End.GetDistanceTo(point) <= tolerance;
            if (!touchesStart && !touchesEnd)
                continue;

            free.RemoveAt(index);
            return atStart == touchesStart ? piece : piece.Reversed();
        }

        return null;
    }

    private static bool Closes(List<Piece> chain, double tolerance) =>
        chain.Count > 1 && chain[^1].End.GetDistanceTo(chain[0].Start) <= tolerance;

    private static object JoinChain(ToolContext context, List<Piece> chain, List<Piece> order, double tolerance, double elevation)
    {
        var transaction = context.RequireTransaction();
        var closed = Closes(chain, tolerance);

        // Sommets bout à bout : chaque jonction prend l'extrémité du morceau précédent.
        var points = new List<Point2d> { chain[0].Start };
        var bulges = new List<double>();
        var widths = new List<(double Start, double End)>();
        foreach (var piece in chain)
        {
            for (var segment = 0; segment < piece.Bulges.Count; segment++)
            {
                bulges.Add(piece.Bulges[segment]);
                widths.Add(piece.Widths[segment]);
                points.Add(piece.Points[segment + 1]);
            }
        }

        if (closed)
        {
            // Le dernier sommet retombe sur le premier : le segment de fermeture garde le renflement du dernier.
            points.RemoveAt(points.Count - 1);
        }
        else
        {
            bulges.Add(0);
            widths.Add((0, 0));
        }

        var joined = new Polyline(points.Count);
        for (var index = 0; index < points.Count; index++)
            joined.AddVertexAt(index, points[index], bulges[index], widths[index].Start, widths[index].End);

        joined.Closed = closed;
        joined.Normal = Vector3d.ZAxis;
        joined.Elevation = elevation;

        // Polyligne conservée : la première de la chaîne dans l'ordre donné, si elle est dessinée vue de dessus.
        var inChain = chain.Select(piece => piece.Source.ObjectId).ToHashSet();
        var sources = order.Where(piece => inChain.Contains(piece.Source.ObjectId)).Select(piece => piece.Source).ToList();
        var kept = sources.OfType<Polyline>().FirstOrDefault(polyline => polyline.Normal.Z > 0);
        if (kept is not null && TrimExtendTools.CopyGeometry(joined, kept))
        {
            joined.Dispose();
        }
        else
        {
            kept = joined;
            kept.SetPropertiesFrom(sources[0]);
            var space = (BlockTableRecord)transaction.GetObject(sources[0].OwnerId, OpenMode.ForWrite);
            space.AppendEntity(kept);
            transaction.AddNewlyCreatedDBObject(kept, true);
        }

        foreach (var source in sources.Where(source => source.ObjectId != kept.ObjectId))
            source.Erase();

        return new
        {
            Handle = kept.Handle.ToString(),
            Pieces = chain.Count,
            Sources = chain.Select(piece => piece.Source.Handle.ToString()).ToArray(),
            Closed = closed,
            Vertices = kept.NumberOfVertices,
            Length = Format.Number(kept.Length),
            Area = closed ? Format.Number(kept.Area) : (double?)null,
        };
    }

    private static Point2d Flat(Point3d point) => new(point.X, point.Y);

    // ----- Coupure -----

    /// <summary>
    /// Coupe une courbe en un point (deux morceaux), ou supprime la partie entre deux points, comme la commande
    /// COUPER. Les points sont ramenés sur la courbe vue de dessus. Sur une courbe fermée, la partie supprimée va du
    /// premier point au second dans le sens de la courbe (antihoraire pour un cercle).
    /// </summary>
    public static object BreakEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = Handles.Resolve(context, reader, "handle").Single();
        var curve = EditTools.OpenForWrite(id, transaction, context) as Curve
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe.");
        if (curve is Xline or Ray)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les droites et demi-droites infinies ne se coupent pas avec cet outil.");

        var length = curve.GetDistanceAtParameter(curve.EndParam);
        var tolerance = Math.Max(length, 1e-9) * 1e-9;
        var first = Station(curve, reader.RequirePoint("point"));
        var second = reader.Has("point2") ? Station(curve, reader.RequirePoint("point2")) : ((double Parameter, double Distance)?)null;

        if (second is null || Math.Abs(second.Value.Distance - first.Distance) <= tolerance)
        {
            if (curve.Closed)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Une courbe fermée se coupe entre deux points : indiquez aussi « point2 ». La partie supprimée va de point " +
                    "à point2 dans le sens de la courbe (antihoraire pour un cercle).");

            if (first.Distance <= tolerance || first.Distance >= length - tolerance)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le point de coupure est à une extrémité de la courbe : rien à couper.");

            return new
            {
                Handle = id.Handle.ToString(),
                Kept = TrimExtendTools.Replace(context, curve, TrimExtendTools.Split(curve, [first.Parameter])),
                RemovedLength = 0.0,
            };
        }

        var (from, to) = (first, second.Value);
        List<double> cuts;
        Func<double, bool> isRemoved;
        if (curve.Closed)
        {
            cuts = [.. new[] { from.Parameter, to.Parameter }.Order()];
            isRemoved = from.Distance < to.Distance
                ? distance => distance > from.Distance && distance < to.Distance
                : distance => distance > from.Distance || distance < to.Distance;
        }
        else
        {
            if (from.Distance > to.Distance)
                (from, to) = (to, from);

            if (from.Distance <= tolerance && to.Distance >= length - tolerance)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Ces deux points couvrent toute la courbe : pour la supprimer, utilisez erase_entities.");

            cuts = [.. new[] { from, to }.Where(station => station.Distance > tolerance && station.Distance < length - tolerance)
                .Select(station => station.Parameter)];
            var (start, end) = (from.Distance, to.Distance);
            isRemoved = distance => distance > start && distance < end;
        }

        // Chaque morceau est classé par la position de son milieu sur la courbe d'origine.
        var kept = new List<Curve>();
        var removedLength = 0.0;
        foreach (var piece in TrimExtendTools.Split(curve, cuts))
        {
            var pieceLength = piece.GetDistanceAtParameter(piece.EndParam);
            var middle = curve.GetDistAtPoint(curve.GetClosestPointTo(piece.GetPointAtDist(pieceLength / 2), false));
            if (isRemoved(middle))
            {
                removedLength += pieceLength;
                piece.Dispose();
            }
            else
            {
                kept.Add(piece);
            }
        }

        if (kept.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La coupure supprimerait toute la courbe : utilisez erase_entities.");

        return new
        {
            Handle = id.Handle.ToString(),
            Kept = TrimExtendTools.Replace(context, curve, kept),
            RemovedLength = Format.Number(removedLength),
        };
    }

    /// <summary>Point de la courbe le plus proche vu de dessus : paramètre et distance depuis le début.</summary>
    private static (double Parameter, double Distance) Station(Curve curve, Point3d point)
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
        return (parameter, curve.GetDistanceAtParameter(parameter));
    }

    // ----- Sommets d'une polyligne -----

    /// <summary>
    /// Modifie une polyligne 2D (LWPOLYLINE) : déplacer, ajouter ou supprimer un sommet, arrondir un segment,
    /// largeurs, ouvrir, fermer, inverser, comme la commande PEDIT. Les points sont en coordonnées du dessin,
    /// ramenés dans le plan de la polyligne.
    /// </summary>
    public static object EditPolyline(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = Handles.Resolve(context, reader, "handle").Single();
        var polyline = EditTools.OpenForWrite(id, transaction, context) as Polyline
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {id.Handle} n'est pas une polyligne 2D (LWPOLYLINE) : les polylignes 3D ne sont pas prises en charge.");
        var action = reader.RequireString("action").Trim().ToLowerInvariant();
        var count = polyline.NumberOfVertices;
        var toPlane = Matrix3d.WorldToPlane(polyline.Normal);

        switch (action)
        {
            case "move_vertex":
                polyline.SetPointAt(VertexIndex(reader, count), InPlane(reader.RequirePoint("point"), toPlane));
                break;

            case "add_vertex":
                AddVertex(polyline, reader, InPlane(reader.RequirePoint("point"), toPlane));
                break;

            case "remove_vertex":
                if (count <= 2)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Une polyligne garde au moins deux sommets : supprimez-la plutôt avec erase_entities.");

                polyline.RemoveVertexAt(VertexIndex(reader, count));
                break;

            case "set_bulge":
            {
                var index = VertexIndex(reader, count);
                if (!polyline.Closed && index == count - 1)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        "Le dernier sommet d'une polyligne ouverte ne commence aucun segment : l'arrondi se règle sur le sommet de départ du segment.");

                polyline.SetBulgeAt(index, ReadBulge(reader));
                break;
            }

            case "set_width":
                if (reader.Has("index"))
                {
                    var index = VertexIndex(reader, count);
                    if (!reader.Has("startWidth") && !reader.Has("endWidth"))
                        throw new PipeException(PipeErrorCodes.InvalidParams, "Avec « index », indiquez « startWidth » et/ou « endWidth ».");

                    if (reader.Has("startWidth"))
                        polyline.SetStartWidthAt(index, NonNegative(reader, "startWidth"));
                    if (reader.Has("endWidth"))
                        polyline.SetEndWidthAt(index, NonNegative(reader, "endWidth"));
                }
                else
                {
                    polyline.ConstantWidth = reader.Has("width")
                        ? NonNegative(reader, "width")
                        : throw new PipeException(PipeErrorCodes.InvalidParams,
                            "Indiquez « width » (largeur constante), ou « index » avec « startWidth »/« endWidth » pour un segment.");
                }

                break;

            case "close":
                // Dernier sommet posé sur le premier : il devient inutile une fois la polyligne fermée.
                if (count > 2 && polyline.GetPoint2dAt(count - 1).GetDistanceTo(polyline.GetPoint2dAt(0)) <= Math.Max(polyline.Length, 1) * 1e-12)
                    polyline.RemoveVertexAt(count - 1);

                polyline.Closed = true;
                break;

            case "open":
                polyline.Closed = false;
                break;

            case "reverse":
                polyline.ReverseCurve();
                break;

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Action « {action} » inconnue : move_vertex, add_vertex, remove_vertex, set_bulge, set_width, close, open ou reverse.");
        }

        var vertices = polyline.NumberOfVertices;
        var bulges = Enumerable.Range(0, vertices).Select(index => Format.Number(polyline.GetBulgeAt(index))).ToArray();
        return new
        {
            Handle = polyline.Handle.ToString(),
            Action = action,
            Vertices = vertices,
            Closed = polyline.Closed,
            Length = Format.Number(polyline.Length),
            Area = polyline.Closed ? Format.Number(polyline.Area) : (double?)null,
            Points = vertices <= MaxListedVertices
                ? Enumerable.Range(0, vertices).Select(index => Format.Point(polyline.GetPoint2dAt(index))).ToArray()
                : null,
            Bulges = vertices <= MaxListedVertices && bulges.Any(bulge => bulge != 0) ? bulges : null,
        };
    }

    /// <summary>
    /// Ajoute un sommet à la position index (0 : avant le premier ; par défaut après le dernier). Sur un segment
    /// courbe, un point situé sur l'arc le partage en deux arcs ; sinon les deux segments deviennent droits.
    /// </summary>
    private static void AddVertex(Polyline polyline, ArgReader reader, Point2d point)
    {
        var count = polyline.NumberOfVertices;
        var index = reader.GetInt("index", count, min: 0, max: count);

        // Segment partagé : celui qui part du sommet précédent ; en tête d'une polyligne fermée, le segment de fermeture.
        var previous = index > 0 ? index - 1 : count - 1;
        var splitsSegment = polyline.Closed || (index > 0 && index < count);
        var (before, after) = (0.0, 0.0);
        if (splitsSegment && polyline.GetBulgeAt(previous) != 0 && SplitArc(polyline, previous, point) is { } halves)
            (before, after) = halves;

        // Largeurs reprises du segment partagé, ou du sommet voisin en bout de polyligne ouverte.
        var widthSource = splitsSegment ? previous : index == 0 ? 0 : count - 1;
        var (startWidth, endWidth) = (polyline.GetStartWidthAt(widthSource), polyline.GetEndWidthAt(widthSource));
        if (splitsSegment)
            polyline.SetBulgeAt(previous, before);

        if (reader.Has("bulge") && !splitsSegment && index == count)
        {
            // Sommet ajouté après le dernier d'une polyligne ouverte : le nouveau segment part de l'ancien dernier.
            polyline.SetBulgeAt(count - 1, reader.RequireDouble("bulge"));
            polyline.AddVertexAt(index, point, 0, startWidth, endWidth);
            return;
        }

        polyline.AddVertexAt(index, point, reader.Has("bulge") ? reader.RequireDouble("bulge") : after, startWidth, endWidth);
    }

    /// <summary>Renflements des deux arcs obtenus en coupant l'arc du segment au point, s'il est dessus.</summary>
    private static (double First, double Second)? SplitArc(Polyline polyline, int segment, Point2d point)
    {
        var arc = polyline.GetArcSegment2dAt(segment);
        if (Math.Abs(arc.Center.GetDistanceTo(point) - arc.Radius) > arc.Radius * 1e-6)
            return null;

        var sweep = 4 * Math.Atan(polyline.GetBulgeAt(segment));
        var turn = (point - arc.Center).Angle - (polyline.GetPoint2dAt(segment) - arc.Center).Angle;
        var twoPi = 2 * Math.PI;
        turn = sweep > 0 ? (turn % twoPi + twoPi) % twoPi : -((-turn % twoPi + twoPi) % twoPi);
        return Math.Abs(turn) <= Math.Abs(sweep)
            ? (Math.Tan(turn / 4), Math.Tan((sweep - turn) / 4))
            : null;
    }

    /// <summary>Renflement donné tel quel, ou par l'angle au centre de l'arc en degrés (positif : arc à gauche).</summary>
    private static double ReadBulge(ArgReader reader)
    {
        if (reader.Has("bulge"))
            return reader.RequireDouble("bulge");

        if (!reader.Has("angle"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez « bulge » (renflement) ou « angle » (angle au centre de l'arc en degrés, positif pour un arc " +
                "tournant à gauche, 0 pour un segment droit).");

        var angle = reader.RequireDouble("angle");
        return Math.Abs(angle) < 360
            ? Math.Tan(Format.Radians(angle) / 4)
            : throw new PipeException(PipeErrorCodes.InvalidParams, "L'angle de l'arc doit être compris entre -360 et 360 degrés (exclus).");
    }

    /// <summary>Indice de sommet, compté à partir de 0 ; -1 désigne le dernier, -2 l'avant-dernier.</summary>
    private static int VertexIndex(ArgReader reader, int count)
    {
        if (!reader.Has("index"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « index » est obligatoire (0 pour le premier sommet, -1 pour le dernier).");

        var index = reader.GetInt("index", 0);
        if (index < -count || index >= count)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Sommet {index} inexistant : la polyligne en compte {count}, de 0 à {count - 1} (ou de -1 à -{count} depuis la fin).");

        return index < 0 ? count + index : index;
    }

    private static Point2d InPlane(Point3d point, Matrix3d toPlane)
    {
        var local = point.TransformBy(toPlane);
        return new Point2d(local.X, local.Y);
    }

    private static double NonNegative(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value >= 0
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » doit être positif ou nul.");
    }
}

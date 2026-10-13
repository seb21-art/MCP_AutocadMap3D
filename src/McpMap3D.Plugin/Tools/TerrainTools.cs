using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Surface de terrain triangulée, lue dans un maillage du dessin (maillage AutoCAD ou polyface), indexée par une grille
/// pour retrouver vite le triangle sous un point.
/// </summary>
internal sealed class Tin
{
    private readonly Dictionary<(int, int), List<int>> _cells = [];
    private readonly double _minX, _minY, _cell;

    public Tin(IReadOnlyList<Point3d> vertices, IReadOnlyList<int> corners)
    {
        Vertices = vertices;
        Corners = corners;
        if (corners.Count < 3)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le maillage n'a aucun triangle.");
        (_minX, _minY) = (vertices.Min(v => v.X), vertices.Min(v => v.Y));
        var (width, height) = (vertices.Max(v => v.X) - _minX, vertices.Max(v => v.Y) - _minY);
        _cell = Math.Max(Math.Max(width, height) / Math.Max(1, Math.Sqrt(corners.Count / 3.0)), 1e-6);
        for (var t = 0; t < corners.Count / 3; t++)
        {
            var (a, b, c) = Triangle(t);
            var (x0, x1) = (Cell(Math.Min(a.X, Math.Min(b.X, c.X)), _minX), Cell(Math.Max(a.X, Math.Max(b.X, c.X)), _minX));
            var (y0, y1) = (Cell(Math.Min(a.Y, Math.Min(b.Y, c.Y)), _minY), Cell(Math.Max(a.Y, Math.Max(b.Y, c.Y)), _minY));
            for (var i = x0; i <= x1; i++)
            {
                for (var j = y0; j <= y1; j++)
                {
                    if (!_cells.TryGetValue((i, j), out var list))
                        _cells[(i, j)] = list = [];
                    list.Add(t);
                }
            }
        }
    }

    public IReadOnlyList<Point3d> Vertices { get; }

    public IReadOnlyList<int> Corners { get; }

    public int TriangleCount => Corners.Count / 3;

    public (Point3d A, Point3d B, Point3d C) Triangle(int t) => (Vertices[Corners[3 * t]], Vertices[Corners[3 * t + 1]], Vertices[Corners[3 * t + 2]]);

    private int Cell(double value, double origin) => (int)Math.Floor((value - origin) / _cell);

    /// <summary>Altitude de la surface sous un point (interpolation dans le triangle), null hors du maillage.</summary>
    public double? ZAt(double x, double y)
    {
        if (!_cells.TryGetValue((Cell(x, _minX), Cell(y, _minY)), out var triangles))
            return null;
        foreach (var t in triangles)
        {
            var (a, b, c) = Triangle(t);
            var det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(det) < 1e-12)
                continue;
            var l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / det;
            var l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / det;
            var l3 = 1 - l1 - l2;
            const double tolerance = -1e-9;
            if (l1 >= tolerance && l2 >= tolerance && l3 >= tolerance)
                return l1 * a.Z + l2 * b.Z + l3 * c.Z;
        }

        return null;
    }

    /// <summary>Triangles d'un maillage AutoCAD (faces découpées en éventail) ou d'un maillage polyface.</summary>
    public static Tin FromEntity(Entity entity, Transaction transaction)
    {
        var vertices = new List<Point3d>();
        var corners = new List<int>();
        void AddFace(IReadOnlyList<int> face)
        {
            for (var k = 1; k + 1 < face.Count; k++)
                corners.AddRange([face[0], face[k], face[k + 1]]);
        }

        switch (entity)
        {
            case SubDMesh mesh:
            {
                foreach (Point3d vertex in mesh.Vertices)
                    vertices.Add(vertex);
                var faces = mesh.FaceArray;
                for (var i = 0; i < faces.Count; i += faces[i] + 1)
                    AddFace(Enumerable.Range(i + 1, faces[i]).Select(k => faces[k]).ToList());
                break;
            }

            case PolyFaceMesh polyface:
                foreach (ObjectId id in polyface)
                {
                    switch (transaction.GetObject(id, OpenMode.ForRead))
                    {
                        case PolyFaceMeshVertex vertex:
                            vertices.Add(vertex.Position);
                            break;
                        case FaceRecord face:
                            AddFace(Enumerable.Range(0, 4).Select(k => (int)Math.Abs(face.GetVertexAt((short)k)))
                                .Where(index => index > 0).Select(index => index - 1).Distinct().ToList());
                            break;
                    }
                }

                break;

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {entity.Handle} ({Format.DxfName(entity)}) n'est pas un maillage : attendu un MNT de create_terrain_mesh ou import_terrain (maillage ou polyface).");
        }

        return new Tin(vertices, corners);
    }
}

/// <summary>
/// Courbes de niveau et analyses d'un terrain triangulé : altitude en des points, profil en long, pentes, cubatures
/// de déblai et de remblai par rapport à une altitude de référence.
/// </summary>
internal static class TerrainTools
{
    private const int MaxLevels = 2000;
    private const int MaxProfileSamples = 1000;

    public static object CreateContours(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var mesh = transaction.GetObject(Handles.ResolveOne(context, reader.RequireString("handle")), OpenMode.ForRead) as Entity
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, "handle ne désigne pas un objet graphique.");
        var tin = Tin.FromEntity(mesh, transaction);
        return DrawContours(context, tin, reader);
    }

    /// <summary>
    /// Courbes de niveau tous les « contourInterval » ; une sur « majorEvery » est maîtresse, sur son propre calque.
    /// Les points de passage sont repérés par l'arête du triangle qu'ils coupent : les morceaux se raccordent exactement.
    /// </summary>
    internal static object DrawContours(ToolContext context, Tin tin, ArgReader reader)
    {
        var interval = reader.GetDouble("contourInterval", reader.GetDouble("interval", 0));
        if (interval <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'équidistance (contourInterval) doit être strictement positive, par exemple 1 ou 0,5.");
        var majorEvery = reader.GetInt("majorEvery", 5, 0, 1000);
        var (minZ, maxZ) = (tin.Vertices.Min(v => v.Z), tin.Vertices.Max(v => v.Z));
        var first = Math.Ceiling(minZ / interval);
        var last = Math.Floor(maxZ / interval);
        if (last - first + 1 > MaxLevels)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"{last - first + 1} niveaux entre {Format.Number(minZ)} et {Format.Number(maxZ)} : augmentez l'équidistance ({MaxLevels} niveaux au plus).");

        var transaction = context.RequireTransaction();
        var minorLayer = EnsureLayer(context, reader.GetString("contourLayer") ?? "COURBES", 8);
        var majorLayer = EnsureLayer(context, reader.GetString("majorLayer") ?? "COURBES_MAITRESSES", 1);
        var space = (BlockTableRecord)transaction.GetObject(context.Database.CurrentSpaceId, OpenMode.ForWrite);
        var (minor, major, segmentsTotal) = (0, 0, 0);
        for (var step = first; step <= last; step++)
        {
            var level = step * interval;
            // Décalage infime : un sommet pile à l'altitude du niveau ne crée pas de point double.
            var cut = level + interval * 1e-9;
            var segments = new List<((int, int) From, (int, int) To)>();
            for (var t = 0; t < tin.TriangleCount; t++)
            {
                var ids = new[] { tin.Corners[3 * t], tin.Corners[3 * t + 1], tin.Corners[3 * t + 2] };
                var crossings = new List<(int, int)>(2);
                for (var e = 0; e < 3; e++)
                {
                    var (i, j) = (ids[e], ids[(e + 1) % 3]);
                    if ((tin.Vertices[i].Z - cut) * (tin.Vertices[j].Z - cut) < 0)
                        crossings.Add(i < j ? (i, j) : (j, i));
                }

                if (crossings.Count == 2)
                    segments.Add((crossings[0], crossings[1]));
            }

            if (segments.Count == 0)
                continue;
            segmentsTotal += segments.Count;
            var isMajor = majorEvery > 0 && Math.Abs(step % majorEvery) < 1e-9;
            foreach (var chain in Chains(segments))
            {
                var polyline = new Polyline { Elevation = level, Layer = isMajor ? majorLayer : minorLayer };
                var closed = chain.Count > 2 && chain[0] == chain[^1];
                var points = closed ? chain.Take(chain.Count - 1).ToList() : chain;
                for (var k = 0; k < points.Count; k++)
                {
                    var (a, b) = (tin.Vertices[points[k].Item1], tin.Vertices[points[k].Item2]);
                    var ratio = (cut - a.Z) / (b.Z - a.Z);
                    polyline.AddVertexAt(k, new Point2d(a.X + (b.X - a.X) * ratio, a.Y + (b.Y - a.Y) * ratio), 0, 0, 0);
                }

                polyline.Closed = closed;
                space.AppendEntity(polyline);
                transaction.AddNewlyCreatedDBObject(polyline, true);
                if (isMajor)
                    major++;
                else
                    minor++;
            }
        }

        return new
        {
            Interval = Format.Number(interval),
            Levels = (int)(last - first + 1),
            MinZ = Format.Number(minZ),
            MaxZ = Format.Number(maxZ),
            Contours = minor + major,
            MajorContours = major,
            ContourLayer = minorLayer,
            MajorLayer = majorLayer,
        };
    }

    /// <summary>Morceaux de courbe reliés bout à bout par les arêtes qu'ils partagent.</summary>
    private static List<List<(int, int)>> Chains(List<((int, int) From, (int, int) To)> segments)
    {
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (var s = 0; s < segments.Count; s++)
        {
            foreach (var edge in new[] { segments[s].From, segments[s].To })
            {
                if (!byEdge.TryGetValue(edge, out var list))
                    byEdge[edge] = list = [];
                list.Add(s);
            }
        }

        var used = new bool[segments.Count];
        var chains = new List<List<(int, int)>>();
        // Les extrémités ouvertes (une seule occurrence) d'abord, puis les boucles.
        var starts = byEdge.Where(pair => pair.Value.Count == 1).Select(pair => pair.Key).Concat(segments.Select(s => s.From));
        foreach (var start in starts)
        {
            var first = byEdge[start].FirstOrDefault(s => !used[s], -1);
            if (first < 0)
                continue;
            var chain = new List<(int, int)> { start };
            var (current, edge) = (first, start);
            while (current >= 0)
            {
                used[current] = true;
                edge = segments[current].From == edge ? segments[current].To : segments[current].From;
                chain.Add(edge);
                current = byEdge[edge].FirstOrDefault(s => !used[s], -1);
            }

            chains.Add(chain);
        }

        return chains;
    }

    private static string EnsureLayer(ToolContext context, string name, short color)
    {
        var transaction = context.RequireTransaction();
        var layers = (LayerTable)transaction.GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name))
            return name;
        layers.UpgradeOpen();
        var layer = new LayerTableRecord { Name = name, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color) };
        layers.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
        return name;
    }

    public static object AnalyzeTerrain(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var mesh = transaction.GetObject(Handles.ResolveOne(context, reader.RequireString("handle")), OpenMode.ForRead) as Entity
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, "handle ne désigne pas un objet graphique.");
        var tin = Tin.FromEntity(mesh, transaction);
        var mode = reader.RequireString("mode").Trim().ToLowerInvariant();
        return mode switch
        {
            "elevation" => Elevations(tin, reader),
            "profile" => Profile(context, tin, reader),
            "volume" => Volume(context, tin, reader),
            "slope" => Slopes(context, tin, reader),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"mode « {mode} » : elevation, profile, volume ou slope."),
        };
    }

    private static object Elevations(Tin tin, ArgReader reader)
    {
        var points = reader.RequirePoints("points", 1);
        if (points.Count > 10_000)
            throw new PipeException(PipeErrorCodes.InvalidParams, "10 000 points au plus.");
        var values = points.Select(point => tin.ZAt(point.X, point.Y) is { } z ? Format.Number(z) : (double?)null).ToList();
        return new
        {
            Elevations = values,
            Outside = values.Count(value => value is null) is > 0 and var outside ? outside : (int?)null,
        };
    }

    /// <summary>Profil en long le long d'une courbe (handle) ou d'une suite de points, échantillonné tous les « step ».</summary>
    private static object Profile(ToolContext context, Tin tin, ArgReader reader)
    {
        Curve curve;
        var temporary = false;
        if (reader.GetString("path") is { } pathHandle)
        {
            curve = context.RequireTransaction().GetObject(Handles.ResolveOne(context, pathHandle), OpenMode.ForRead) as Curve
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, "path doit désigner une courbe (ligne, polyligne, arc).");
        }
        else
        {
            var points = reader.RequirePoints("points", 2);
            var polyline = new Polyline();
            for (var i = 0; i < points.Count; i++)
                polyline.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            (curve, temporary) = (polyline, true);
        }

        try
        {
            var length = curve.GetDistanceAtParameter(curve.EndParam);
            var step = reader.GetDouble("step", Math.Max(length / 200, 0.01));
            if (step <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "step doit être strictement positif.");
            if (length / step > MaxProfileSamples)
                step = length / MaxProfileSamples;

            var samples = new List<double[]>();
            double? previous = null;
            var (climb, descent, maxSlope) = (0.0, 0.0, 0.0);
            var outside = 0;
            for (var distance = 0.0; ; distance += step)
            {
                var at = Math.Min(distance, length);
                var point = curve.GetPointAtDist(at);
                if (tin.ZAt(point.X, point.Y) is { } z)
                {
                    if (previous is { } last && samples.Count > 0)
                    {
                        var run = at - samples[^1][0];
                        var rise = z - last;
                        (climb, descent) = rise > 0 ? (climb + rise, descent) : (climb, descent - rise);
                        if (run > 0)
                            maxSlope = Math.Max(maxSlope, Math.Abs(rise / run) * 100);
                    }

                    samples.Add([Format.Number(at), Format.Number(point.X), Format.Number(point.Y), Format.Number(z)]);
                    previous = z;
                }
                else
                {
                    outside++;
                }

                if (at >= length)
                    break;
            }

            if (samples.Count == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le tracé ne passe pas au-dessus du terrain.");
            return new
            {
                Length = Format.Number(length),
                Step = Format.Number(step),
                MinZ = samples.Min(s => s[3]),
                MaxZ = samples.Max(s => s[3]),
                Climb = Format.Number(climb),
                Descent = Format.Number(descent),
                MaxSlopePercent = Format.Number(maxSlope),
                OutsideSamples = outside > 0 ? outside : (int?)null,
                Columns = new[] { "distance", "x", "y", "z" },
                Samples = samples,
            };
        }
        finally
        {
            if (temporary)
                curve.Dispose();
        }
    }

    /// <summary>Contour fermé (handle) en polygone, arcs échantillonnés.</summary>
    private static List<Point2d> Boundary(ToolContext context, string handle)
    {
        var curve = context.RequireTransaction().GetObject(Handles.ResolveOne(context, handle), OpenMode.ForRead) as Curve
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, "boundary doit désigner une courbe fermée (polyligne, cercle).");
        if (!curve.Closed)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le contour boundary doit être fermé.");
        var length = curve.GetDistanceAtParameter(curve.EndParam);
        const int count = 720;
        return Enumerable.Range(0, count).Select(k => curve.GetPointAtDist(length * k / count)).Select(p => new Point2d(p.X, p.Y)).ToList();
    }

    private static bool Inside(List<Point2d> polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if ((polygon[i].Y > y) != (polygon[j].Y > y) &&
                x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        }

        return inside;
    }

    /// <summary>
    /// Cubatures dans un contour fermé par rapport à une altitude de référence (plateforme) : grille d'environ 40 000
    /// mailles, chaque maille prend l'altitude du terrain en son centre.
    /// </summary>
    private static object Volume(ToolContext context, Tin tin, ArgReader reader)
    {
        var polygon = Boundary(context, reader.RequireString("boundary"));
        var reference = reader.RequireDouble("referenceZ");
        var (minX, maxX, minY, maxY) = (polygon.Min(p => p.X), polygon.Max(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.Y));
        var cell = reader.Has("cellSize") ? reader.GetDouble("cellSize", 1) : Math.Max(Math.Sqrt((maxX - minX) * (maxY - minY) / 40_000), 0.05);
        if (cell <= 0 || (maxX - minX) / cell * (maxY - minY) / cell > 4_000_000)
            throw new PipeException(PipeErrorCodes.InvalidParams, "cellSize trop petit pour ce contour (4 millions de mailles au plus).");

        var (cut, fill, area, uncovered) = (0.0, 0.0, 0.0, 0.0);
        for (var x = minX + cell / 2; x < maxX; x += cell)
        {
            for (var y = minY + cell / 2; y < maxY; y += cell)
            {
                if (!Inside(polygon, x, y))
                    continue;
                if (tin.ZAt(x, y) is not { } z)
                {
                    uncovered += cell * cell;
                    continue;
                }

                area += cell * cell;
                if (z > reference)
                    cut += (z - reference) * cell * cell;
                else
                    fill += (reference - z) * cell * cell;
            }
        }

        return new
        {
            ReferenceZ = Format.Number(reference),
            Cut = Format.Number(cut),
            Fill = Format.Number(fill),
            Net = Format.Number(cut - fill),
            Area = Format.Number(area),
            UncoveredArea = uncovered > 0 ? Format.Number(uncovered) : (double?)null,
            CellSize = Format.Number(cell),
            Note = "Déblai (cut) : terrain au-dessus de la référence ; remblai (fill) : en dessous. Volumes en unités du dessin au cube.",
        };
    }

    /// <summary>Pentes des triangles, pondérées par leur surface en plan, dans tout le maillage ou dans un contour.</summary>
    private static object Slopes(ToolContext context, Tin tin, ArgReader reader)
    {
        var polygon = reader.GetString("boundary") is { } boundary ? Boundary(context, boundary) : null;
        double[] limits = [2, 5, 10, 20, 50];
        var classes = new double[limits.Length + 1];
        var (total, weighted, max) = (0.0, 0.0, 0.0);
        for (var t = 0; t < tin.TriangleCount; t++)
        {
            var (a, b, c) = tin.Triangle(t);
            if (polygon is not null && !Inside(polygon, (a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3))
                continue;
            var normal = (b - a).CrossProduct(c - a);
            var planArea = Math.Abs(normal.Z) / 2;
            if (planArea < 1e-12)
                continue;
            var slope = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y) / Math.Abs(normal.Z) * 100;
            total += planArea;
            weighted += slope * planArea;
            max = Math.Max(max, slope);
            var index = Array.FindIndex(limits, limit => slope < limit);
            classes[index < 0 ? limits.Length : index] += planArea;
        }

        if (total == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun triangle dans la zone.");
        var labels = new[] { "0-2 %", "2-5 %", "5-10 %", "10-20 %", "20-50 %", "> 50 %" };
        return new
        {
            PlanArea = Format.Number(total),
            MeanSlopePercent = Format.Number(weighted / total),
            MaxSlopePercent = Format.Number(max),
            Classes = labels.Select((label, i) => new { Class = label, Area = Format.Number(classes[i]), Share = Format.Number(classes[i] / total * 100) }).ToList(),
        };
    }
}

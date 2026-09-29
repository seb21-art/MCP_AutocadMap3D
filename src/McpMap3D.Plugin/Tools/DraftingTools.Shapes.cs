using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Formes 2D : points, polygones réguliers, nuages de révision, masques (wipeouts) et contours tracés autour d'un
/// point, comme la commande CONTOUR.
/// </summary>
internal static partial class DraftingTools
{
    private const int MaxPoints = 10000;

    /// <summary>
    /// Points (DBPoint), un ou plusieurs par appel. Leur aspect (PDMODE) et leur taille (PDSIZE) sont des réglages
    /// globaux du dessin, qui s'appliquent à tous les points existants.
    /// </summary>
    public static object CreatePoint(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var points = reader.RequirePoints("points", minimum: 1);
        if (points.Count > MaxPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop de points en une fois ({points.Count}) : {MaxPoints} au maximum.");

        var styleChanged = false;
        if (reader.Has("pointStyle"))
        {
            var mode = reader.GetInt("pointStyle", 0, min: 0, max: 100);
            if (mode % 32 > 4 || mode / 32 > 3)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Style de point {mode} invalide : une forme 0 (point), 1 (rien), 2 (+), 3 (x) ou 4 (trait), plus 32 " +
                    "(cercle), 64 (carré) ou 96 (les deux). Exemples : 0, 2, 3, 34, 35, 66.");

            styleChanged |= database.Pdmode != mode;
            database.Pdmode = mode;
        }

        if (reader.Has("pointSize"))
        {
            var size = reader.GetDouble("pointSize", 0);
            styleChanged |= database.Pdsize != size;
            database.Pdsize = size;
        }

        var handles = new List<string>(points.Count);
        string? layer = null;
        foreach (var point in points)
        {
            var dbPoint = new DBPoint(point);
            EditTools.Append(context, dbPoint, reader);
            handles.Add(dbPoint.Handle.ToString());
            layer ??= dbPoint.Layer;
        }

        // Les points existants ne prennent le nouveau style qu'à la régénération.
        if (styleChanged)
            context.AfterCommit(() => context.Editor.Regen());

        return new
        {
            Created = handles.Count,
            Layer = layer,
            PointStyle = database.Pdmode,
            PointSize = Format.Number(database.Pdsize),
            Handles = handles,
        };
    }

    /// <summary>
    /// Polygone régulier (polyligne fermée), comme la commande POLYGONE : par centre et rayon, inscrit dans le cercle
    /// (rayon jusqu'aux sommets) ou circonscrit (rayon jusqu'au milieu des côtés), ou par un côté.
    /// Par défaut le côté du bas est horizontal ; « rotation » tourne le polygone autour de son centre.
    /// </summary>
    public static object CreatePolygon(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        if (!reader.Has("sides"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « sides » (nombre de côtés, 3 à 1024) est obligatoire.");

        var sides = reader.GetInt("sides", 0, min: 3, max: 1024);
        var half = Math.PI / sides;
        Point3d center;
        double circumradius, firstAngle;
        if (reader.Has("edgeStart") || reader.Has("edgeEnd"))
        {
            if (reader.Has("center") || reader.Has("radius") || reader.Has("mode") || reader.Has("rotation"))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Définissez le polygone soit par center et radius (avec mode et rotation), soit par un côté edgeStart " +
                    "et edgeEnd, pas les deux.");

            // Polygone à gauche du côté edgeStart → edgeEnd : ses sommets se suivent dans le sens trigonométrique.
            var a = reader.RequirePoint("edgeStart");
            var b = reader.RequirePoint("edgeEnd");
            var edge = new Vector2d(b.X - a.X, b.Y - a.Y);
            if (edge.Length < 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les points edgeStart et edgeEnd sont confondus en plan.");

            var apothem = edge.Length / (2 * Math.Tan(half));
            var left = new Vector2d(-edge.Y, edge.X).GetNormal();
            center = new Point3d((a.X + b.X) / 2 + left.X * apothem, (a.Y + b.Y) / 2 + left.Y * apothem, a.Z);
            circumradius = edge.Length / (2 * Math.Sin(half));
            firstAngle = Math.Atan2(a.Y - center.Y, a.X - center.X);
        }
        else
        {
            center = reader.RequirePoint("center");
            var radius = Positive(reader, "radius");
            var mode = (reader.GetString("mode") ?? "inscribed").Trim().ToLowerInvariant();
            circumradius = mode switch
            {
                "inscribed" or "inscrit" => radius,
                "circumscribed" or "circonscrit" => radius / Math.Cos(half),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« mode » vaut inscribed ou circumscribed, pas « {mode} »."),
            };

            // Côté du bas horizontal : son milieu est à -90°, ses deux sommets à -90° ± 180°/n.
            firstAngle = -Math.PI / 2 + half + Format.Radians(reader.GetDouble("rotation", 0));
        }

        var polyline = new Polyline(sides) { Closed = true, Elevation = center.Z };
        for (var k = 0; k < sides; k++)
        {
            var angle = firstAngle + 2 * half * k;
            polyline.AddVertexAt(k, new Point2d(center.X + circumradius * Math.Cos(angle), center.Y + circumradius * Math.Sin(angle)), 0, 0, 0);
        }

        var result = EditTools.Append(context, polyline, reader);
        return new
        {
            Polygon = result,
            Sides = sides,
            Center = Format.Point(center),
            Circumradius = Format.Number(circumradius),
            Apothem = Format.Number(circumradius * Math.Cos(half)),
            Side = Format.Number(2 * circumradius * Math.Sin(half)),
            Perimeter = Format.Number(polyline.Length),
            Area = Format.Number(polyline.Area),
        };
    }

    /// <summary>
    /// Nuage de révision : polyligne fermée en festons (arcs) autour d'un contour, comme la commande REVCLOUD. Le contour
    /// est une liste de sommets, un rectangle, ou une courbe fermée existante, conservée.
    /// </summary>
    public static object CreateRevisionCloud(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        Curve contour;
        var temporary = false;
        if (reader.Has("handle"))
        {
            if (reader.Has("points") || reader.Has("corner1"))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez le contour par handle, par points ou par corner1 et corner2, pas plusieurs.");

            var id = Handles.Resolve(context, reader, "handle").Single();
            contour = transaction.GetObject(id, OpenMode.ForRead) is Curve { Closed: true } closed
                ? closed
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe fermée.");

            Plane plane;
            try
            {
                plane = contour.GetPlane();
            }
            catch (AcException)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La courbe {id.Handle} n'est pas plane.");
            }

            if (!plane.Normal.IsParallelTo(Vector3d.ZAxis))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La courbe {id.Handle} n'est pas dans un plan horizontal.");
        }
        else
        {
            contour = ContourPolyline(ReadContour(reader));
            temporary = true;
        }

        Polyline cloud;
        int arcs;
        double chord;
        try
        {
            var length = contour.GetDistanceAtParameter(contour.EndParam) - contour.GetDistanceAtParameter(contour.StartParam);
            var arcLength = reader.Has("arcLength") ? Positive(reader, "arcLength") : length / 40;
            arcs = (int)Math.Round(length / arcLength);
            if (arcs > MaxPoints)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"« arcLength » trop petite : {arcs} arcs pour un contour de {Format.Number(length)} ({MaxPoints} au maximum).");

            arcs = Math.Max(arcs, 3);
            chord = length / arcs;
            var points = Enumerable.Range(0, arcs).Select(k => contour.GetPointAtDist(k * chord)).ToList();

            // Un arc de renflement positif tourne dans le sens trigonométrique : il bombe à droite de son sens de parcours.
            // Pour un contour parcouru dans le sens trigonométrique, l'extérieur est à droite.
            var counterclockwise = SignedArea(points) > 0;
            var outward = !reader.GetBool("inward", false);
            var bulge = CloudBulge * (counterclockwise == outward ? 1 : -1);
            cloud = new Polyline(arcs) { Closed = true, Elevation = points[0].Z };
            for (var k = 0; k < arcs; k++)
                cloud.AddVertexAt(k, new Point2d(points[k].X, points[k].Y), bulge, 0, 0);
        }
        finally
        {
            if (temporary)
                contour.Dispose();
        }

        var result = EditTools.Append(context, cloud, reader);
        return new
        {
            Cloud = result,
            Arcs = arcs,
            Chord = Format.Number(chord),
            Length = Format.Number(cloud.Length),
            Area = Format.Number(cloud.Area),
        };
    }

    /// <summary>Renflement des festons : tan(θ/4) pour un arc d'environ 106°, proche de celui de REVCLOUD.</summary>
    private const double CloudBulge = 0.5;

    /// <summary>
    /// Masque (WIPEOUT) : polygone qui cache les objets placés dessous dans l'ordre d'affichage. Son cadre s'affiche
    /// selon le réglage global WIPEOUTFRAME, modifiable avec « frame ».
    /// </summary>
    public static object CreateWipeout(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var vertices = ReadContour(reader);
        var frame = reader.GetString("frame")?.Trim().ToLowerInvariant();
        short? frameValue = frame switch
        {
            null => null,
            "hidden" or "off" => 0,
            "shown" or "on" => 1,
            "shown_not_plotted" => 2,
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« frame » vaut shown, hidden ou shown_not_plotted, pas « {frame} »."),
        };

        // SetFrom attend un contour fermé : le premier sommet est répété à la fin.
        var outline = new Point2dCollection(vertices.Select(point => new Point2d(point.X, point.Y)).ToArray());
        outline.Add(outline[0]);

        var wipeout = new Wipeout();
        wipeout.SetDatabaseDefaults(context.Database);
        try
        {
            wipeout.SetFrom(outline, Vector3d.ZAxis);
        }
        catch (AcException ex)
        {
            wipeout.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer le masque ({ex.ErrorStatus}) : contour croisé ?");
        }

        if (Math.Abs(vertices[0].Z) > 1e-12)
            wipeout.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, vertices[0].Z)));

        var result = EditTools.Append(context, wipeout, reader);
        if (frameValue is short value)
            AcApp.SetSystemVariable("WIPEOUTFRAME", value);

        return new
        {
            Wipeout = result,
            Vertices = vertices.Count,
            Area = Format.Number(Math.Abs(SignedArea(vertices))),
            Frame = Convert.ToInt32(AcApp.GetSystemVariable("WIPEOUTFRAME")) switch
            {
                0 => "hidden",
                2 => "shown_not_plotted",
                _ => "shown",
            },
            Note = "Le masque cache les objets dessinés avant lui ; ceux créés ensuite restent visibles (voir set_draw_order).",
        };
    }

    /// <summary>
    /// Contour fermé autour d'un point, comme la commande CONTOUR : AutoCAD suit les objets qui entourent le point
    /// et crée une polyligne (ou une région) par boucle, îlots compris si demandé.
    /// </summary>
    public static object CreateBoundary(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var editor = context.Editor;
        var seed = reader.RequirePoint("point");
        var islands = reader.GetBool("detectIslands", true);
        var type = (reader.GetString("type") ?? "polyline").Trim().ToLowerInvariant();
        if (type is not ("polyline" or "region"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« type » vaut polyline ou region, pas « {type} ».");

        // TraceBoundary attend le point dans le SCU courant, et ne voit que les objets de la vue courante.
        var seedUcs = seed.TransformBy(editor.CurrentUserCoordinateSystem.Inverse());
        var loops = TraceBoundary(editor, seedUcs, islands);
        var viewAdjusted = false;
        if (loops.Count == 0 && context.Database.TileMode)
        {
            // Point hors de la vue, ou objets trop petits à l'écran : on réessaie en vue de dessus sur l'étendue du
            // dessin, puis sur des vues dix fois plus serrées à chaque essai autour du point (objets petits devant
            // l'étendue, par exemple près de l'origine d'un dessin en Lambert 93). La vue de l'utilisateur est rétablie.
            using var saved = editor.GetCurrentView();
            try
            {
                using var view = editor.GetCurrentView();
                view.ViewDirection = Vector3d.ZAxis;
                view.ViewTwist = 0;
                ModelingTools.ZoomExtents(view, context.Database);
                for (var attempt = 0; attempt < BoundaryZoomAttempts && loops.Count == 0; attempt++)
                {
                    if (attempt > 0)
                    {
                        view.CenterPoint = InView(view, seed);
                        view.Width /= 10;
                        view.Height /= 10;
                    }

                    editor.SetCurrentView(view);
                    viewAdjusted = true;
                    loops = TraceBoundary(editor, seedUcs, islands);
                }
            }
            finally
            {
                editor.SetCurrentView(saved);
            }
        }

        if (loops.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucun contour fermé autour du point {string.Join(", ", Format.Point(seed))} : vérifiez qu'il est à " +
                "l'intérieur d'une zone entièrement fermée par des objets du plan XY, et que ces objets sont sur des " +
                "calques allumés et dégelés.");

        // La boucle extérieure est la plus grande ; les autres sont des îlots.
        var ordered = loops.OrderByDescending(CurveArea).ToList();
        var outerArea = CurveArea(ordered[0]);
        var islandArea = ordered.Skip(1).Sum(CurveArea);
        var perimeter = ordered[0].GetDistanceAtParameter(ordered[0].EndParam);
        var handles = new List<string>();
        if (type == "region")
        {
            var regions = ordered.Select(loop => ToRegion(loop)).ToList();
            foreach (var loop in ordered)
                loop.Dispose();

            var outer = regions[0];
            foreach (var island in regions.Skip(1))
                outer.BooleanOperation(BooleanOperationType.BoolSubtract, island);

            EditTools.Append(context, outer, reader);
            handles.Add(outer.Handle.ToString());
            foreach (var island in regions.Skip(1))
                island.Dispose();
        }
        else
        {
            foreach (var loop in ordered)
            {
                EditTools.Append(context, loop, reader);
                handles.Add(loop.Handle.ToString());
            }
        }

        return new
        {
            Created = handles.Count,
            Type = type,
            Loops = ordered.Count,
            Islands = ordered.Count - 1,
            OuterArea = Format.Number(outerArea),
            NetArea = Format.Number(outerArea - islandArea),
            Perimeter = Format.Number(perimeter),
            ViewAdjusted = viewAdjusted ? true : (bool?)null,
            Handles = handles,
        };
    }

    // Étendue du dessin, puis vues serrées d'un facteur 10 à 10 000 000 autour du point.
    private const int BoundaryZoomAttempts = 8;

    /// <summary>Point du dessin exprimé dans le repère de la vue (DCS), où se définit son centre.</summary>
    private static Point2d InView(ViewTableRecord view, Point3d point)
    {
        var eyeToWorld = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
            * Matrix3d.Displacement(view.Target - Point3d.Origin)
            * Matrix3d.PlaneToWorld(view.ViewDirection);
        var eye = point.TransformBy(eyeToWorld.Inverse());
        return new Point2d(eye.X, eye.Y);
    }

    private static List<Curve> TraceBoundary(Autodesk.AutoCAD.EditorInput.Editor editor, Point3d seedUcs, bool islands)
    {
        DBObjectCollection traced;
        try
        {
            traced = editor.TraceBoundary(seedUcs, islands);
        }
        catch (AcException)
        {
            return [];
        }

        var curves = new List<Curve>();
        foreach (DBObject item in traced)
        {
            if (item is Curve curve)
                curves.Add(curve);
            else
                item.Dispose();
        }

        return curves;
    }

    private static double CurveArea(Curve curve)
    {
        try
        {
            return Math.Abs(curve.Area);
        }
        catch (AcException)
        {
            return 0;
        }
    }

    private static Region ToRegion(Curve loop)
    {
        var regions = Region.CreateFromCurves(new DBObjectCollection { loop });
        if (regions.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "AutoCAD n'a pas pu transformer le contour en région.");

        for (var i = 1; i < regions.Count; i++)
            regions[i].Dispose();

        return (Region)regions[0];
    }

    /// <summary>Contour fermé donné par « points » (3 sommets au moins) ou par un rectangle « corner1 », « corner2 ».</summary>
    private static List<Point3d> ReadContour(ArgReader reader)
    {
        if (reader.Has("points") == reader.Has("corner1"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez le contour soit par « points » (sommets), soit par un rectangle « corner1 » et « corner2 ».");

        List<Point3d> vertices;
        if (reader.Has("points"))
        {
            vertices = [.. reader.RequirePoints("points", minimum: 3)];
            if (vertices.Count > MaxPoints)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Trop de sommets ({vertices.Count}) : {MaxPoints} au maximum.");

            // Contour déjà refermé par l'appelant : on retire le doublon.
            if (vertices.Count > 3 && vertices[0].DistanceTo(vertices[^1]) < 1e-9)
                vertices.RemoveAt(vertices.Count - 1);
        }
        else
        {
            var a = reader.RequirePoint("corner1");
            var b = reader.RequirePoint("corner2");
            if (Math.Abs(a.X - b.X) < 1e-9 || Math.Abs(a.Y - b.Y) < 1e-9)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux coins doivent différer en X et en Y.");

            vertices = [a, new Point3d(b.X, a.Y, a.Z), new Point3d(b.X, b.Y, a.Z), new Point3d(a.X, b.Y, a.Z)];
        }

        if (Math.Abs(SignedArea(vertices)) < 1e-12)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les sommets du contour sont alignés : son aire est nulle.");

        return vertices;
    }

    /// <summary>Polyligne fermée en mémoire, à l'altitude du premier sommet.</summary>
    private static Polyline ContourPolyline(IReadOnlyList<Point3d> vertices)
    {
        var polyline = new Polyline(vertices.Count) { Closed = true, Elevation = vertices[0].Z };
        for (var i = 0; i < vertices.Count; i++)
            polyline.AddVertexAt(i, new Point2d(vertices[i].X, vertices[i].Y), 0, 0, 0);

        return polyline;
    }

    /// <summary>Aire signée en plan (formule de Gauss) : positive si les sommets tournent dans le sens trigonométrique.</summary>
    internal static double SignedArea(IReadOnlyList<Point3d> points)
    {
        double sum = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            sum += (a.X - points[0].X) * (b.Y - points[0].Y) - (b.X - points[0].X) * (a.Y - points[0].Y);
        }

        return sum / 2;
    }
}

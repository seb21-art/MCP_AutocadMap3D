using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using DbSurface = Autodesk.AutoCAD.DatabaseServices.Surface;
using NurbSurface = Autodesk.AutoCAD.DatabaseServices.NurbSurface;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Surfaces : planes, par réseau de courbes, NURBS par points de contrôle et décalées ; solide sculpté dans des
/// surfaces qui enferment un volume ; projection de courbes sur un solide ou une surface.
/// </summary>
internal static partial class ModelingTools
{
    private const int MaxControlPoints = 10_000;

    public static object CreateSurface(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var type = reader.RequireString("type").ToLowerInvariant();
        return type switch
        {
            "planar" => CreatePlanarSurfaces(context, reader),
            "network" => CreateNetworkSurface(context, reader),
            "nurbs" => CreateNurbsSurface(context, reader),
            "offset" => CreateOffsetSurfaces(context, reader),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Type de surface « {type} » inconnu : planar, network, nurbs ou offset."),
        };
    }

    /// <summary>Surfaces planes dans des contours fermés (courbes ou régions), ou rectangle horizontal donné par deux coins.</summary>
    private static object CreatePlanarSurfaces(ToolContext context, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        if (reader.Has("corners"))
        {
            if (reader.Has("handles"))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez « corners » ou « handles », pas les deux.");

            using var outline = Rectangle(reader.RequirePoints("corners", minimum: 2));
            var fromCorners = RegionsFrom([outline]);
            var created = fromCorners.Select(region => PlanarFromRegion(context, region, reader, owned: true)).ToList();
            return new { Count = created.Count, Surfaces = created };
        }

        var ids = Handles.Resolve(context, reader);
        var surfaces = new List<object>();
        var curves = new List<Curve>();
        foreach (var id in ids)
        {
            switch (transaction.GetObject(id, OpenMode.ForRead))
            {
                case Region region:
                    surfaces.Add(PlanarFromRegion(context, region, reader, owned: false));
                    break;
                case Curve curve:
                    curves.Add(curve);
                    break;
                case var other:
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"L'objet {id.Handle} ({Format.DxfName(other)}) n'est ni une courbe ni une région.");
            }
        }

        // Les courbes forment des contours fermés, comme pour create_region : une surface par contour.
        if (curves.Count > 0)
        {
            foreach (var region in RegionsFrom(curves))
            {
                region.Layer = curves[0].Layer;
                surfaces.Add(PlanarFromRegion(context, region, reader, owned: true));
            }
        }

        var eraseSources = reader.GetBool("eraseSources", false);
        if (eraseSources)
        {
            foreach (var id in ids)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Count = surfaces.Count, Surfaces = surfaces, SourcesErased = eraseSources };
    }

    /// <summary>Rectangle horizontal fermé, hors du dessin, à partir de deux coins opposés à la même altitude.</summary>
    private static Polyline Rectangle(IReadOnlyList<Point3d> corners)
    {
        if (corners.Count != 2)
            throw new PipeException(PipeErrorCodes.InvalidParams, "« corners » attend exactement deux coins opposés.");

        var (a, b) = (corners[0], corners[1]);
        if (Math.Abs(a.Z - b.Z) > Tolerance)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Les deux coins doivent être à la même altitude : le rectangle est horizontal. Pour un autre plan, " +
                "dessinez le contour puis passez-le dans « handles ».");

        if (Math.Abs(a.X - b.X) <= Tolerance || Math.Abs(a.Y - b.Y) <= Tolerance)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux coins sont alignés sur X ou sur Y : le rectangle serait plat.");

        var outline = new Polyline(4) { Elevation = a.Z };
        outline.AddVertexAt(0, new Point2d(a.X, a.Y), 0, 0, 0);
        outline.AddVertexAt(1, new Point2d(b.X, a.Y), 0, 0, 0);
        outline.AddVertexAt(2, new Point2d(b.X, b.Y), 0, 0, 0);
        outline.AddVertexAt(3, new Point2d(a.X, b.Y), 0, 0, 0);
        outline.Closed = true;
        return outline;
    }

    private static List<Region> RegionsFrom(IEnumerable<Curve> curves)
    {
        var collection = new DBObjectCollection();
        foreach (var curve in curves)
            collection.Add(curve);

        DBObjectCollection regions;
        try
        {
            regions = Region.CreateFromCurves(collection);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu former de contour fermé ({ex.ErrorStatus}). Les courbes doivent être coplanaires et " +
                "se toucher bout à bout, sans se recouper.");
        }

        return regions.Count > 0
            ? regions.Cast<Region>().ToList()
            : throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun contour fermé trouvé : les courbes doivent se toucher bout à bout.");
    }

    /// <summary>Surface plane d'une région ; <paramref name="owned"/> : région créée ici, hors du dessin, à libérer.</summary>
    private static object PlanarFromRegion(ToolContext context, Region region, ArgReader reader, bool owned)
    {
        var surface = new PlaneSurface();
        try
        {
            surface.CreateFromRegion(region);
            surface.SetPropertiesFrom(region);
        }
        catch (AcException ex)
        {
            surface.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer de surface plane ({ex.ErrorStatus}).");
        }
        finally
        {
            if (owned)
                region.Dispose();
        }

        return CreatedSurface(context, surface, reader);
    }

    /// <summary>Surface passant par un réseau de courbes croisées, comme la commande SURFRESEAU.</summary>
    private static object CreateNetworkSurface(ToolContext context, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        var uIds = Handles.Resolve(context, reader, "uCurves");
        var vIds = Handles.Resolve(context, reader, "vCurves");
        if (uIds.Count < 2 || vIds.Count < 2)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "La surface de réseau demande au moins deux courbes dans chaque direction (« uCurves » et « vCurves »).");

        if (uIds.Intersect(vIds).Any())
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une même courbe ne peut pas servir dans les deux directions.");

        var uCurves = uIds.Select(id => OpenCurve(id, transaction)).ToList();
        var vCurves = vIds.Select(id => OpenCurve(id, transaction)).ToList();
        Profile3d[] uProfiles = [.. uCurves.Select(curve => new Profile3d(curve))];
        Profile3d[] vProfiles = [.. vCurves.Select(curve => new Profile3d(curve))];
        DbSurface? surface;
        try
        {
            surface = DbSurface.CreateNetworkSurface(uProfiles, vProfiles);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu créer la surface de réseau ({ex.ErrorStatus}). Chaque courbe U doit croiser toutes " +
                "les courbes V, et les courbes de chaque direction se donnent dans l'ordre.");
        }
        finally
        {
            foreach (var profile in uProfiles.Concat(vProfiles))
                profile.Dispose();
        }

        if (surface is null)
            throw new PipeException(PipeErrorCodes.InvalidParams, "AutoCAD n'a renvoyé aucune surface de réseau pour ces courbes.");

        surface.SetPropertiesFrom(uCurves[0]);
        var created = CreatedSurface(context, surface, reader);

        var eraseSources = reader.GetBool("eraseSources", false);
        if (eraseSources)
        {
            foreach (var id in uIds.Concat(vIds))
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Surface = created, UCurves = uIds.Count, VCurves = vIds.Count, SourcesErased = eraseSources };
    }

    /// <summary>
    /// Surface NURBS non rationnelle définie par une grille de points de contrôle, avec des vecteurs nodaux uniformes
    /// serrés : la surface passe par les quatre coins de la grille et suit ses bords.
    /// </summary>
    private static object CreateNurbsSurface(ToolContext context, ArgReader reader)
    {
        var grid = reader.RequirePointGrid("controlPoints", minimum: 2);
        var (uCount, vCount) = (grid.Count, grid[0].Count);
        if (uCount * vCount > MaxControlPoints)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Trop de points de contrôle ({uCount * vCount}) : {MaxControlPoints} au maximum.");

        var degreeU = reader.GetInt("degreeU", Math.Min(3, uCount - 1), min: 1, max: uCount - 1);
        var degreeV = reader.GetInt("degreeV", Math.Min(3, vCount - 1), min: 1, max: vCount - 1);

        // L'ordre attendu des points de contrôle n'est pas documenté : la grille est vérifiée point par point après
        // construction, et reconstruite dans l'autre ordre si elle ne correspond pas.
        NurbSurface? surface = null;
        foreach (var rowsFirst in new[] { true, false })
        {
            var candidate = BuildNurbs(grid, degreeU, degreeV, rowsFirst);
            if (MatchesGrid(candidate, grid))
            {
                surface = candidate;
                break;
            }

            candidate.Dispose();
        }

        if (surface is null)
            throw new PipeException(PipeErrorCodes.Internal, "La surface NURBS construite ne reprend pas la grille de points de contrôle.");

        var created = CreatedSurface(context, surface, reader);
        return new { Surface = created, ControlPoints = new[] { uCount, vCount }, DegreeU = degreeU, DegreeV = degreeV };
    }

    private static NurbSurface BuildNurbs(IReadOnlyList<IReadOnlyList<Point3d>> grid, int degreeU, int degreeV, bool rowsFirst)
    {
        var (uCount, vCount) = (grid.Count, grid[0].Count);
        var ordered = rowsFirst
            ? grid.SelectMany(row => row)
            : Enumerable.Range(0, vCount).SelectMany(v => grid.Select(row => row[v]));

        using var points = new Point3dCollection([.. ordered]);
        using var uKnots = ClampedKnots(uCount, degreeU);
        using var vKnots = ClampedKnots(vCount, degreeV);
        var weights = new DoubleCollection([.. Enumerable.Repeat(1.0, uCount * vCount)]);
        try
        {
            return new NurbSurface(degreeU, degreeV, false, uCount, vCount, points, weights, uKnots, vKnots);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu créer la surface NURBS ({ex.ErrorStatus}) : vérifiez que la grille ne se replie pas sur elle-même.");
        }
    }

    private static bool MatchesGrid(NurbSurface surface, IReadOnlyList<IReadOnlyList<Point3d>> grid)
    {
        if (surface.NumberOfControlPointsInU != grid.Count || surface.NumberOfControlPointsInV != grid[0].Count)
            return false;

        for (var u = 0; u < grid.Count; u++)
        {
            for (var v = 0; v < grid[u].Count; v++)
            {
                // Relecture exacte à l'arrondi près : un écart d'un pas de grille, même en Lambert-93, dépasse ce seuil.
                if (surface.GetControlPointAt(u, v).DistanceTo(grid[u][v]) > 1e-9 * Math.Max(1, grid[u][v].GetAsVector().Length))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Vecteur nodal uniforme serré (degré + 1 nœuds répétés à chaque extrémité), paramètres de 0 à 1.</summary>
    private static KnotCollection ClampedKnots(int count, int degree)
    {
        var knots = new KnotCollection();
        var spans = count - degree;
        for (var i = 0; i <= degree; i++)
            knots.Add(0);

        for (var i = 1; i < spans; i++)
            knots.Add((double)i / spans);

        for (var i = 0; i <= degree; i++)
            knots.Add(1);

        return knots;
    }

    /// <summary>Surfaces décalées d'une distance, du côté de la normale si elle est positive, comme SURFDECALER.</summary>
    private static object CreateOffsetSurfaces(ToolContext context, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        var distance = reader.RequireDouble("distance");
        if (distance == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La distance de décalage ne peut pas être nulle.");

        var surfaces = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not DbSurface source)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} n'est pas une surface. Pour décaler les faces d'un solide, utilisez edit_solid_faces (offset).");

            Entity? offset;
            try
            {
                offset = DbSurface.CreateOffsetSurface(source, distance);
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu décaler la surface {id.Handle} ({ex.ErrorStatus}) : distance trop grande pour sa courbure ?");
            }

            if (offset is null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a renvoyé aucune surface décalée pour {id.Handle}.");

            offset.SetPropertiesFrom(source);
            var appended = EditTools.Append(context, offset, reader);
            surfaces.Add(new
            {
                Source = id.Handle.ToString(),
                Surface = appended,
                Area = offset is DbSurface surface ? TryNumber(surface.GetArea) : null,
            });
        }

        return new { Count = surfaces.Count, Surfaces = surfaces, Distance = distance };
    }

    /// <summary>
    /// Solide délimité par des surfaces (et des solides) qui enferment un volume, comme la commande SCULPTER : les
    /// surfaces sont ajustées à leurs intersections.
    /// </summary>
    public static object SculptSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);
        var bodies = new List<Entity>();
        foreach (var id in ids)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            bodies.Add(entity is DbSurface or Solid3d
                ? entity
                : throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} ({Format.DxfName(entity)}) n'est ni une surface ni un solide. Transformez les " +
                    "régions en surfaces planes avec create_surface (planar)."));
        }

        var solid = new Solid3d();
        try
        {
            // Un indicateur par objet ; zéro laisse AutoCAD déterminer le côté à garder, comme la commande.
            solid.CreateSculptedSolid([.. bodies], new IntegerCollection(new int[bodies.Count]));
        }
        catch (AcException ex)
        {
            solid.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu sculpter de solide ({ex.ErrorStatus}) : les surfaces doivent enfermer complètement un " +
                "volume, sans jour entre elles.");
        }

        if (solid.IsNull)
        {
            solid.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les surfaces n'enferment aucun volume : aucun solide créé.");
        }

        solid.SetPropertiesFrom(bodies[0]);
        var appended = EditTools.Append(context, solid, reader);

        var eraseSources = reader.GetBool("eraseSources", false);
        if (eraseSources)
        {
            foreach (var id in ids)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Solid = appended, Volume = TryVolume(solid), Sources = ids.Count, SourcesErased = eraseSources };
    }

    /// <summary>
    /// Projette des courbes ou des points sur un solide ou une surface, comme la commande PROJECTGEOMETRY : les
    /// courbes obtenues sont posées sur les faces touchées, la cible restant intacte.
    /// </summary>
    public static object ProjectCurves(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var targets = Handles.Resolve(context, reader, "target");
        if (targets.Count != 1)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez une seule cible dans « target » : un solide ou une surface.");

        var target = (Entity)transaction.GetObject(targets[0], OpenMode.ForRead);
        if (target is not (Solid3d or DbSurface))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"La cible {targets[0].Handle} ({Format.DxfName(target)}) n'est ni un solide ni une surface.");

        var direction = reader.Has("direction") ? Direction(reader, "direction") : -Vector3d.ZAxis;
        var ids = Handles.Resolve(context, reader);
        if (ids.Contains(targets[0]))
            throw new PipeException(PipeErrorCodes.InvalidParams, "La cible ne peut pas figurer parmi les objets à projeter.");

        var projected = new List<object>();
        var projectedIds = new List<ObjectId>();
        var missed = new List<string>();
        foreach (var id in ids)
        {
            var source = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            if (source is not (Curve or DBPoint))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} ({Format.DxfName(source)}) n'est ni une courbe ni un point.");

            Entity[]? results;
            try
            {
                results = target is Solid3d solid
                    ? solid.ProjectOnToSolid(source, direction)
                    : ((DbSurface)target).ProjectOnToSurface(source, direction);
            }
            catch (AcException)
            {
                results = null;
            }

            if (results is not { Length: > 0 })
            {
                missed.Add(id.Handle.ToString());
                continue;
            }

            foreach (var result in FirstHits(results, direction))
            {
                result.SetPropertiesFrom(source);
                projected.Add(new { Source = id.Handle.ToString(), Projected = EditTools.Append(context, result, reader) });
            }

            projectedIds.Add(id);
        }

        if (projected.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucun objet ne se projette sur la cible dans la direction {string.Join(", ", Format.Vector(direction))} : " +
                "vérifiez que la cible se trouve dans cette direction (par défaut vers le bas, [0, 0, -1]).");

        var eraseSources = reader.GetBool("eraseSources", false);
        if (eraseSources)
        {
            foreach (var id in projectedIds)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new
        {
            Target = targets[0].Handle.ToString(),
            Direction = Format.Vector(direction),
            Count = projected.Count,
            Projections = projected,
            NotProjected = missed.Count > 0 ? missed : null,
            SourcesErased = eraseSources,
        };
    }

    /// <summary>
    /// ProjectOnToSolid pose la courbe sur toutes les faces traversées, y compris celles de l'autre côté du solide
    /// (le dessous d'une boîte projetée vers le bas). On ne garde que les morceaux vus depuis la source : ceux
    /// qu'aucun autre morceau ne précède sur la même droite de projection.
    /// </summary>
    private static List<Entity> FirstHits(Entity[] results, Vector3d direction)
    {
        var axis = direction.GetNormal();
        var samples = results.Select(Sample).ToArray();
        var kept = new List<Entity>();
        for (var i = 0; i < results.Length; i++)
        {
            var hidden = samples[i] is { } sample && results.Where((_, j) => j != i).Any(other =>
            {
                var point = sample.Point;
                Point3d closest;
                try
                {
                    closest = other switch
                    {
                        Curve curve => curve.GetClosestPointTo(point, axis, false),
                        DBPoint dbPoint => dbPoint.Position,
                        _ => point,
                    };
                }
                catch (AcException)
                {
                    return false;
                }

                // Tolérances à l'échelle du morceau et non de ses coordonnées : en Lambert-93, 1e-6 fois les
                // coordonnées ferait près de 7 m, de quoi garder le dessous d'un bâtiment bas ou masquer le toit
                // d'une annexe voisine plus basse.
                var offset = closest - point;
                var along = offset.DotProduct(axis);
                var rounding = 1e-9 * Math.Max(1, point.GetAsVector().Length);
                return along < -rounding && (offset - axis * along).Length <= rounding + 1e-3 * sample.Size;
            });

            if (hidden)
                results[i].Dispose();
            else
                kept.Add(results[i]);
        }

        return kept;

        // Milieu du morceau et sa longueur (0 si elle ne se mesure pas).
        static (Point3d Point, double Size)? Sample(Entity entity)
        {
            try
            {
                return entity switch
                {
                    Curve curve => (curve.GetPointAtParameter((curve.StartParam + curve.EndParam) / 2),
                        TryNumber(() => curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam)) ?? 0),
                    DBPoint dbPoint => (dbPoint.Position, 0.0),
                    _ => null,
                };
            }
            catch (AcException)
            {
                return null;
            }
        }
    }
}

using System.Drawing.Imaging;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Modélisation 3D avancée : capture de la vue, régions, coupe et section de solides, lissage, alignement 3D,
/// détection d'interférences et conversion des bâtiments en épaisseur en solides.
/// </summary>
internal static partial class ModelingTools
{
    private const int MaxInterferencePairs = 5000;

    /// <summary>
    /// Image PNG de la vue courante, encodée en base64 : le serveur MCP la transmet à Claude comme contenu image,
    /// pour qu'il vérifie visuellement ce qu'il a construit.
    /// </summary>
    public static object CaptureView(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var width = reader.GetInt("width", 1024, min: 64, max: 2048);
        var height = reader.GetInt("height", 768, min: 64, max: 2048);
        var document = context.RequireDocument();

        // La vue a pu changer juste avant (set_view, zoom) : on force l'affichage avant la capture.
        document.Editor.UpdateScreen();
        AcApp.UpdateScreen();

        using var bitmap = document.CapturePreviewImage((uint)width, (uint)height)
            ?? throw new PipeException(PipeErrorCodes.Internal, "AutoCAD n'a pas renvoyé d'image de la vue.");
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);

        return new
        {
            Width = bitmap.Width,
            Height = bitmap.Height,
            MimeType = "image/png",
            Space = context.Database.TileMode ? "model" : "paper",
            Data = Convert.ToBase64String(stream.ToArray()),
        };
    }

    /// <summary>
    /// Régions construites à partir de courbes jointives formant un ou plusieurs contours fermés, ou d'une liste
    /// de sommets : le contour est alors une polyligne fermée gardée en mémoire, jamais ajoutée au dessin.
    /// </summary>
    public static object CreateRegion(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        if (reader.Has("points"))
        {
            if (reader.Has("handles"))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez « handles » ou « points », pas les deux.");

            return CreateRegionFromPoints(context, reader);
        }

        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);

        var curves = new DBObjectCollection();
        foreach (var id in ids)
        {
            curves.Add(transaction.GetObject(id, OpenMode.ForRead) as Curve
                ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} n'est pas une courbe (ligne, arc, polyligne, cercle, ellipse, spline)."));
        }

        DBObjectCollection regions;
        try
        {
            regions = Region.CreateFromCurves(curves);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu former de région ({ex.ErrorStatus}). Les courbes doivent être coplanaires et " +
                "se toucher bout à bout pour former des contours fermés, sans se recouper.");
        }

        if (regions.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Aucun contour fermé trouvé : les courbes doivent se toucher bout à bout.");

        var layer = ((Entity)curves[0]).Layer;
        var created = new List<object>();
        foreach (Region region in regions)
        {
            region.Layer = layer;
            var appended = EditTools.Append(context, region, reader);
            created.Add(new { Region = appended, Area = TryNumber(() => region.Area), Perimeter = TryNumber(() => region.Perimeter) });
        }

        var eraseCurves = reader.GetBool("eraseCurves", false);
        if (eraseCurves)
        {
            foreach (var id in ids)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Count = created.Count, Regions = created, CurvesErased = eraseCurves };
    }

    private static object CreateRegionFromPoints(ToolContext context, ArgReader reader)
    {
        using var outline = EditTools.BuildPlanarPolyline(reader, "points", closed: true, width: 0);
        if (outline.NumberOfVertices < 3)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une région demande au moins trois sommets.");

        outline.SetDatabaseDefaults(context.Database);

        DBObjectCollection regions;
        try
        {
            regions = Region.CreateFromCurves(new DBObjectCollection { outline });
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu former de région ({ex.ErrorStatus}). Le contour ne doit pas se recouper.");
        }

        if (regions.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "AutoCAD n'a formé aucune région : le contour se recoupe-t-il ?");

        var created = new List<object>();
        foreach (Region region in regions)
        {
            region.Layer = outline.Layer;
            var appended = EditTools.Append(context, region, reader);
            created.Add(new { Region = appended, Area = TryNumber(() => region.Area), Perimeter = TryNumber(() => region.Perimeter) });
        }

        return new
        {
            Count = created.Count,
            Regions = created,
            CurvesErased = false,
            Normal = Format.Vector(outline.Normal),
        };
    }

    /// <summary>
    /// Coupe des solides par un plan. AutoCAD conserve dans le solide d'origine la partie située du côté de la
    /// normale du plan ; avec keep = both, l'autre partie devient un nouveau solide.
    /// </summary>
    public static object SliceSolid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var plane = ReadPlane(reader);
        var keep = reader.GetString("keep", "both")!.ToLowerInvariant();
        if (keep is not ("both" or "positive" or "negative"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« keep » vaut both, positive ou negative, pas « {keep} ».");

        // Garder le côté négatif revient à couper par le plan retourné en ne gardant que le côté positif.
        var cutPlane = keep == "negative" ? new Plane(plane.PointOnPlane, -plane.Normal) : plane;

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var solid = OpenSolidForWrite(id, transaction, context);
            Solid3d? other;
            try
            {
                other = solid.Slice(cutPlane, keep == "both");
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu couper le solide {id.Handle} ({ex.ErrorStatus}). Vérifiez que le plan le traverse.");
            }

            if (solid.IsNull)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le solide {id.Handle} est entièrement du côté supprimé du plan : opération annulée.");

            object? otherPart = null;
            if (other is not null && !other.IsNull)
            {
                other.SetPropertiesFrom(solid);
                var appended = EditTools.Append(context, other, default);
                otherPart = new { Solid = appended, Volume = TryVolume(other) };
            }
            else
            {
                other?.Dispose();
            }

            results.Add(new { Handle = solid.Handle.ToString(), Volume = TryVolume(solid), OtherPart = otherPart });
        }

        return new
        {
            Keep = keep,
            PlanePoint = Format.Point(plane.PointOnPlane),
            PlaneNormal = Format.Vector(plane.Normal),
            Count = results.Count,
            Solids = results,
        };
    }

    /// <summary>Section plane de solides : une région par solide coupé, les solides restent intacts.</summary>
    public static object GetSection(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var plane = ReadPlane(reader);

        var sections = new List<object>();
        var missed = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Solid3d solid)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un solide 3D.");

            Region? region;
            try
            {
                region = solid.GetSection(plane);
            }
            catch (AcException)
            {
                region = null;
            }

            if (region is null || TryNumber(() => region.Area) is not > 0)
            {
                region?.Dispose();
                missed.Add(id.Handle.ToString());
                continue;
            }

            region.SetPropertiesFrom(solid);
            var appended = EditTools.Append(context, region, reader);
            sections.Add(new
            {
                Solid = id.Handle.ToString(),
                Region = appended,
                Area = TryNumber(() => region.Area),
                Perimeter = TryNumber(() => region.Perimeter),
            });
        }

        if (sections.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le plan ne coupe aucun des solides indiqués.");

        return new { Count = sections.Count, Sections = sections, NotCut = missed.Count > 0 ? missed : null };
    }

    /// <summary>
    /// Solide lissé passant par des sections fermées successives, guidé par des courbes ou un chemin ; ou surface
    /// lissée, dont les sections peuvent être ouvertes.
    /// </summary>
    public static object Loft(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var sectionIds = Handles.Resolve(context, reader, "sections");
        if (sectionIds.Count < 2)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le lissage demande au moins deux sections.");

        var asSurface = reader.GetBool("surface", false);
        var sections = sectionIds.Select(id => asSurface ? OpenCurve(id, transaction) : OpenProfile(id, transaction)).ToArray();
        var guides = reader.Has("guides")
            ? Handles.Resolve(context, reader, "guides").Select(id => OpenCurve(id, transaction)).ToArray()
            : Array.Empty<Curve>();
        Entity? path = reader.Has("path") ? OpenCurve(Handles.Resolve(context, reader, "path").Single(), transaction) : null;
        if (path is not null && guides.Length > 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez des guides ou un chemin, pas les deux.");

        using var options = new LoftOptionsBuilder
        {
            Ruled = reader.GetBool("ruled", false),
            Closed = reader.GetBool("closed", false),
        }.ToLoftOptions();

        Entity lofted = asSurface ? new LoftedSurface() : new Solid3d();
        try
        {
            if (lofted is LoftedSurface surface)
                surface.CreateLoftedSurface(sections, guides, path, options);
            else
                ((Solid3d)lofted).CreateLoftedSolid(sections, guides, path, options);
        }
        catch (AcException ex)
        {
            lofted.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu lisser ces sections ({ex.ErrorStatus}). Donnez les sections dans l'ordre du " +
                "lissage, sans qu'elles se coupent ; chaque guide doit toucher toutes les sections.");
        }

        lofted.Layer = sections[0].Layer;
        var result = lofted is LoftedSurface loftedSurface
            ? CreatedSurface(context, loftedSurface, reader)
            : Created(context, (Solid3d)lofted, reader);

        var eraseSections = reader.GetBool("eraseSections", false);
        if (eraseSections)
        {
            foreach (var id in sectionIds)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return new { Loft = result, Sections = sectionIds.Count, Guides = guides.Length, Path = path is not null, SectionsErased = eraseSections };
    }

    /// <summary>
    /// Alignement 3D par paires de points : une paire translate, deux paires translatent et orientent,
    /// trois paires posent un repère sur un autre (poser un objet sur une face).
    /// </summary>
    public static object Align3d(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var sources = reader.RequirePoints("sourcePoints", minimum: 1);
        var targets = reader.RequirePoints("targetPoints", minimum: 1);
        if (sources.Count != targets.Count || sources.Count > 3)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Donnez autant de points cibles que de points source : une, deux ou trois paires.");

        var scale = reader.GetBool("scale", false);
        if (scale && sources.Count < 2)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La mise à l'échelle demande au moins deux paires de points.");

        Matrix3d matrix;
        double factor = 1;
        if (sources.Count == 1)
        {
            matrix = Matrix3d.Displacement(targets[0] - sources[0]);
        }
        else
        {
            var sourceX = NonZero(sources[1] - sources[0], "sourcePoints");
            var targetX = NonZero(targets[1] - targets[0], "targetPoints");
            if (sources.Count == 2)
            {
                matrix = Matrix3d.Displacement(targets[0] - sources[0]) * RotationBetween(sourceX, targetX, sources[0]);
            }
            else
            {
                var (sx, sy, sz) = Frame(sources, "sourcePoints");
                var (tx, ty, tz) = Frame(targets, "targetPoints");
                matrix = Matrix3d.AlignCoordinateSystem(sources[0], sx, sy, sz, targets[0], tx, ty, tz);
            }

            if (scale)
            {
                factor = targetX.Length / sourceX.Length;
                matrix = Matrix3d.Scaling(factor, targets[0]) * matrix;
            }
        }

        var aligned = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = EditTools.OpenForWrite(id, transaction, context);
            entity.TransformBy(matrix);
            aligned.Add(entity.Handle.ToString());
        }

        return new { Aligned = aligned.Count, Pairs = sources.Count, ScaleFactor = Format.Number(factor), Handles = aligned };
    }

    /// <summary>
    /// Détection des collisions entre solides : volume commun de chaque paire qui se chevauche, calculé sur des
    /// copies (les solides indiqués ne sont pas modifiés). Les solides qui se touchent seulement ne comptent pas.
    /// </summary>
    public static object CheckInterference(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var setA = Handles.Resolve(context, reader, "setA").Distinct().ToList();
        var setB = reader.Has("setB") ? Handles.Resolve(context, reader, "setB").Distinct().ToList() : null;
        var createSolids = reader.GetBool("createSolids", false);

        var pairs = new List<(ObjectId A, ObjectId B)>();
        if (setB is null)
        {
            for (var i = 0; i < setA.Count; i++)
                for (var j = i + 1; j < setA.Count; j++)
                    pairs.Add((setA[i], setA[j]));
        }
        else
        {
            foreach (var a in setA)
                foreach (var b in setB.Where(b => b != a))
                    pairs.Add((a, b));
        }

        if (pairs.Count > MaxInterferencePairs)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Trop de paires à tester ({pairs.Count}) : {MaxInterferencePairs} au maximum. Réduisez les ensembles.");

        var solids = new Dictionary<ObjectId, Solid3d>();
        foreach (var id in setA.Concat(setB ?? new List<ObjectId>()).Distinct())
        {
            solids[id] = transaction.GetObject(id, OpenMode.ForRead) as Solid3d
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un solide 3D.");
        }

        var clashes = new List<object>();
        var tested = 0;
        foreach (var (idA, idB) in pairs)
        {
            var (a, b) = (solids[idA], solids[idB]);
            if (Format.TryGetExtents(a) is not Extents3d boxA || Format.TryGetExtents(b) is not Extents3d boxB || !Overlap(boxA, boxB))
                continue;

            tested++;
            var common = (Solid3d)a.Clone();
            var other = (Solid3d)b.Clone();
            try
            {
                common.BooleanOperation(BooleanOperationType.BoolIntersect, other);
            }
            catch (AcException)
            {
                common.Dispose();
                continue;
            }
            finally
            {
                other.Dispose();
            }

            var volume = common.IsNull ? null : TryVolume(common);
            if (volume is not > 1e-9)
            {
                common.Dispose();
                continue;
            }

            object? created = null;
            if (createSolids)
            {
                common.ColorIndex = 1; // Rouge, sauf couleur demandée.
                created = EditTools.Append(context, common, reader);
            }
            else
            {
                common.Dispose();
            }

            clashes.Add(new { A = idA.Handle.ToString(), B = idB.Handle.ToString(), Volume = volume, CommonSolid = created });
        }

        return new { Interfere = clashes.Count > 0, Pairs = pairs.Count, PairsTested = tested, Count = clashes.Count, Clashes = clashes };
    }

    /// <summary>
    /// Convertit des polylignes fermées dotées d'une épaisseur (bâtiments de import_cadastre avec buildings3d) en
    /// solides extrudés. Par défaut le solide prend la place de la polyligne (même handle, données d'objet
    /// conservées) ; les cours intérieures, polylignes contenues dans une autre, sont soustraites.
    /// </summary>
    public static object BuildingsToSolids(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var keepOriginals = reader.GetBool("keepOriginals", false);
        var subtractHoles = reader.GetBool("subtractHoles", true);

        var candidates = new List<Polyline>();
        var skipped = 0;
        foreach (var id in SelectBuildings(context, reader, transaction))
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is Polyline { Closed: true, Thickness: not 0 } polyline && polyline.NumberOfVertices >= 3)
                candidates.Add(polyline);
            else
                skipped++;
        }

        if (candidates.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Aucune polyligne fermée avec épaisseur parmi les objets indiqués. Importez les bâtiments avec " +
                "import_cadastre (buildings3d) ou donnez une épaisseur avec set_entity_properties.");

        // Une cour est une polyligne dont un sommet est à l'intérieur d'une autre de même altitude et même hauteur.
        var holes = new Dictionary<Polyline, Polyline>();
        if (subtractHoles)
        {
            foreach (var inner in candidates)
            {
                var outer = candidates.FirstOrDefault(other => other != inner
                    && Math.Abs(other.Elevation - inner.Elevation) < 1e-6
                    && Math.Abs(other.Thickness - inner.Thickness) < 1e-6
                    && Contains(other, inner.GetPoint2dAt(0)));
                if (outer is not null)
                    holes[inner] = outer;
            }
        }

        var solids = new Dictionary<Polyline, Solid3d>();
        foreach (var polyline in candidates.Where(p => !holes.ContainsKey(p)))
            solids[polyline] = ExtrudeBuilding(polyline);

        foreach (var (hole, outer) in holes)
        {
            using var courtyard = ExtrudeBuilding(hole);
            if (solids.TryGetValue(outer, out var target))
                target.BooleanOperation(BooleanOperationType.BoolSubtract, courtyard);
        }

        var created = new List<object>();
        foreach (var (polyline, solid) in solids)
        {
            solid.SetPropertiesFrom(polyline);
            if (keepOriginals)
            {
                EditTools.Append(context, solid, default);
            }
            else
            {
                // Le solide reprend l'identité de la polyligne : handle, XData, dictionnaire d'extension, données d'objet.
                // HandOverTo est refusé dans une transaction (InvalidContext) : on échange les identités, puis on
                // efface la polyligne, qui porte désormais l'identité provisoire du solide.
                var original = EditTools.OpenForWrite(polyline.ObjectId, transaction, context);
                var space = (BlockTableRecord)transaction.GetObject(original.OwnerId, OpenMode.ForWrite);
                space.AppendEntity(solid);
                transaction.AddNewlyCreatedDBObject(solid, true);
                try
                {
                    original.SwapIdWith(solid.ObjectId, true, true);
                }
                catch (AcException ex)
                {
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"AutoCAD n'a pas pu remplacer la polyligne {polyline.Handle} par un solide ({ex.ErrorStatus}). " +
                        "Réessayez avec keepOriginals = true.");
                }

                original.Erase();
            }

            created.Add(new { Handle = solid.Handle.ToString(), solid.Layer, Volume = TryVolume(solid) });
        }

        if (!keepOriginals)
        {
            foreach (var hole in holes.Keys)
                EditTools.OpenForWrite(hole.ObjectId, transaction, context).Erase();
        }

        return new
        {
            Converted = created.Count,
            CourtyardsSubtracted = holes.Count,
            Skipped = skipped > 0 ? skipped : (int?)null,
            OriginalsKept = keepOriginals,
            Solids = created.Take(500).ToArray(),
            SolidsTruncated = created.Count > 500 ? true : (bool?)null,
        };
    }

    private static IEnumerable<ObjectId> SelectBuildings(ToolContext context, ArgReader reader, Transaction transaction)
    {
        if (reader.Has("handles"))
            return Handles.Resolve(context, reader);

        var layers = NameFilter.Create(reader.GetStrings("layers"))
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez les objets par « handles » ou par « layers » (par exemple [\"*_BATI_*\"]).");

        var modelSpace = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(context.Database), OpenMode.ForRead);
        var ids = new List<ObjectId>();
        foreach (var id in modelSpace)
        {
            if (id.ObjectClass.DxfName == "LWPOLYLINE" && layers.IsMatch(((Entity)transaction.GetObject(id, OpenMode.ForRead)).Layer))
                ids.Add(id);
        }

        return ids;
    }

    private static Solid3d ExtrudeBuilding(Polyline polyline)
    {
        // Copie sans épaisseur : le profil est la polyligne à son altitude, extrudé selon sa normale.
        using var profile = (Polyline)polyline.Clone();
        profile.Thickness = 0;
        var solid = new Solid3d();
        using var options = new SweepOptionsBuilder().ToSweepOptions();
        try
        {
            solid.CreateExtrudedSolid(profile, polyline.Normal * polyline.Thickness, options);
        }
        catch (AcException ex)
        {
            solid.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu extruder la polyligne {polyline.Handle} ({ex.ErrorStatus}) : contour qui se recoupe ?");
        }

        return solid;
    }

    /// <summary>Point dans un polygone (sommets de la polyligne, arcs ignorés), dans le plan de la polyligne.</summary>
    private static bool Contains(Polyline polyline, Point2d point)
    {
        var inside = false;
        var count = polyline.NumberOfVertices;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            var (a, b) = (polyline.GetPoint2dAt(i), polyline.GetPoint2dAt(j));
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }

    private static bool Overlap(Extents3d a, Extents3d b) =>
        a.MinPoint.X <= b.MaxPoint.X && b.MinPoint.X <= a.MaxPoint.X
        && a.MinPoint.Y <= b.MaxPoint.Y && b.MinPoint.Y <= a.MaxPoint.Y
        && a.MinPoint.Z <= b.MaxPoint.Z && b.MinPoint.Z <= a.MaxPoint.Z;

    /// <summary>Plan donné par trois points (planePoints) ou par un point et une normale (planePoint, planeNormal).</summary>
    internal static Plane ReadPlane(ArgReader reader)
    {
        if (reader.Has("planePoints"))
        {
            var points = reader.RequirePoints("planePoints", minimum: 3);
            if (points.Count != 3)
                throw new PipeException(PipeErrorCodes.InvalidParams, "« planePoints » attend exactement trois points.");

            var normal = (points[1] - points[0]).CrossProduct(points[2] - points[0]);
            if (normal.IsZeroLength())
                throw new PipeException(PipeErrorCodes.InvalidParams, "Les trois points du plan sont alignés.");

            return new Plane(points[0], normal.GetNormal());
        }

        if (!reader.Has("planePoint") || !reader.Has("planeNormal"))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez le plan : « planePoint » et « planeNormal », ou trois points « planePoints ».");

        return new Plane(reader.RequirePoint("planePoint"), Direction(reader, "planeNormal"));
    }

    private static Curve OpenCurve(ObjectId id, Transaction transaction) =>
        transaction.GetObject(id, OpenMode.ForRead) as Curve
        ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas une courbe.");

    /// <summary>Rotation qui amène la direction <paramref name="from"/> sur <paramref name="to"/> autour de <paramref name="at"/>.</summary>
    private static Matrix3d RotationBetween(Vector3d from, Vector3d to, Point3d at)
    {
        var (u, v) = (from.GetNormal(), to.GetNormal());
        if (u.IsCodirectionalTo(v))
            return Matrix3d.Identity;

        if (u.IsCodirectionalTo(-v))
            return Matrix3d.Rotation(Math.PI, u.GetPerpendicularVector(), at);

        return Matrix3d.Rotation(u.GetAngleTo(v), u.CrossProduct(v), at);
    }

    /// <summary>Repère orthonormé défini par trois points : X vers le deuxième, Y du côté du troisième.</summary>
    private static (Vector3d X, Vector3d Y, Vector3d Z) Frame(IReadOnlyList<Point3d> points, string name)
    {
        var x = (points[1] - points[0]).GetNormal();
        var z = x.CrossProduct(points[2] - points[0]);
        if (z.IsZeroLength())
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Les trois points de « {name} » sont alignés.");

        z = z.GetNormal();
        return (x, z.CrossProduct(x), z);
    }

    private static Vector3d NonZero(Vector3d vector, string name) =>
        vector.IsZeroLength()
            ? throw new PipeException(PipeErrorCodes.InvalidParams, $"Les deux premiers points de « {name} » sont confondus.")
            : vector;
}

using System.Collections;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using DbSurface = Autodesk.AutoCAD.DatabaseServices.Surface;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Modélisation 3D de base : primitives solides, polyligne 3D, extrusion, révolution, opérations booléennes,
/// rotation 3D, propriétés des solides et vues. Les angles sont exprimés en degrés.
/// </summary>
internal static partial class ModelingTools
{
    public static object CreateBox(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var (length, width, height) = (Positive(reader, "length"), Positive(reader, "width"), Positive(reader, "height"));
        var solid = new Solid3d();
        solid.CreateBox(length, width, height);
        return PlaceByCorner(context, solid, reader, length, width, height);
    }

    public static object CreateWedge(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var (length, width, height) = (Positive(reader, "length"), Positive(reader, "width"), Positive(reader, "height"));
        var solid = new Solid3d();
        solid.CreateWedge(length, width, height);
        return PlaceByCorner(context, solid, reader, length, width, height);
    }

    public static object CreateCylinder(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var radius = Positive(reader, "radius");
        var height = Positive(reader, "height");
        var topRadius = reader.GetDouble("topRadius", radius);
        if (topRadius < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « topRadius » ne peut pas être négatif.");

        var axis = reader.Has("axis") ? Direction(reader, "axis") : Vector3d.ZAxis;

        // Tronc de cône centré sur l'origine, d'axe Z : base ramenée à l'origine, puis orientation et placement.
        var solid = new Solid3d();
        solid.CreateFrustum(height, radius, radius, topRadius);
        solid.TransformBy(
            Matrix3d.Displacement(center - Point3d.Origin)
            * AlignZAxis(axis)
            * Matrix3d.Displacement(new Vector3d(0, 0, height / 2)));

        return Created(context, solid, reader);
    }

    /// <summary>Pyramide ou tronc de pyramide régulière, comme la commande PYRAMIDE.</summary>
    public static object CreatePyramid(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var radius = Positive(reader, "radius");
        var height = Positive(reader, "height");
        var sides = reader.GetInt("sides", 4, min: 3, max: 32);
        var topRadius = reader.GetDouble("topRadius", 0);
        if (topRadius < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « topRadius » ne peut pas être négatif.");

        // Le rayon se mesure par défaut au cercle inscrit dans la base (du centre au milieu d'un côté), radiusToVertex
        // le mesure aux sommets. CreatePyramid, lui, attend le rayon aux sommets (constaté dans AutoCAD 2026).
        if (!reader.GetBool("radiusToVertex", false))
        {
            var ratio = Math.Cos(Math.PI / sides);
            (radius, topRadius) = (radius / ratio, topRadius / ratio);
        }

        var axis = reader.Has("axis") ? Direction(reader, "axis") : Vector3d.ZAxis;
        var rotation = Format.Radians(reader.GetDouble("rotation", 0));

        // Pyramide centrée sur l'origine, d'axe Z : base ramenée à l'origine, rotation sur l'axe, orientation, placement.
        var solid = new Solid3d();
        try
        {
            solid.CreatePyramid(height, sides, radius, topRadius);
        }
        catch (AcException ex)
        {
            solid.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer la pyramide ({ex.ErrorStatus}).");
        }

        solid.TransformBy(
            Matrix3d.Displacement(center - Point3d.Origin)
            * AlignZAxis(axis)
            * Matrix3d.Rotation(rotation, Vector3d.ZAxis, Point3d.Origin)
            * Matrix3d.Displacement(new Vector3d(0, 0, height / 2)));

        return Created(context, solid, reader);
    }

    public static object CreateSphere(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var solid = new Solid3d();
        solid.CreateSphere(Positive(reader, "radius"));
        solid.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
        return Created(context, solid, reader);
    }

    public static object CreateTorus(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var majorRadius = Positive(reader, "majorRadius");
        var minorRadius = Positive(reader, "minorRadius");
        var solid = new Solid3d();
        solid.CreateTorus(majorRadius, minorRadius);
        solid.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
        return Created(context, solid, reader);
    }

    public static object Create3dPolyline(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var points = reader.RequirePoints("points", minimum: 2);
        var polyline = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(points.ToArray()), reader.GetBool("closed", false));
        return EditTools.Append(context, polyline, reader);
    }

    /// <summary>Hélice, comme la commande HELICE : ressort, filetage, rampe ou escalier hélicoïdal.</summary>
    public static object CreateHelix(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var baseRadius = Positive(reader, "baseRadius");
        var topRadius = reader.Has("topRadius") ? Positive(reader, "topRadius") : baseRadius;
        var height = Positive(reader, "height");
        if (reader.Has("turns") && reader.Has("turnHeight"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez « turns » ou « turnHeight », pas les deux.");

        var turns = reader.Has("turnHeight") ? height / Positive(reader, "turnHeight") : reader.GetDouble("turns", 3);
        if (turns <= 0 || turns > 500)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le nombre de tours doit être compris entre 0 (exclu) et 500, comme pour HELICE (ici {Format.Number(turns)}).");

        var clockwise = reader.GetBool("clockwise", false);
        var startAngle = Format.Radians(reader.GetDouble("startAngle", 0));
        var axis = reader.Has("axis") ? Direction(reader, "axis") : Vector3d.ZAxis;

        // Hélice d'axe Z posée sur l'origine, puis orientation et placement, comme create_cylinder.
        var helix = new Helix();
        try
        {
            helix.SetAxisPoint(Point3d.Origin, false);
            helix.AxisVector = Vector3d.ZAxis;
            helix.StartPoint = new Point3d(baseRadius * Math.Cos(startAngle), baseRadius * Math.Sin(startAngle), 0);
            helix.BaseRadius = baseRadius;
            helix.TopRadius = topRadius;
            // La contrainte se pose avant les tours : sinon, changer les tours garde le pas (1 par défaut) et
            // recalcule la hauteur.
            helix.Constrain = ConstrainType.Height;
            helix.Height = height;
            helix.Turns = turns;
            helix.Twist = !clockwise;
            helix.CreateHelix();

            // Le sens réel est mesuré sur la tangente au départ plutôt que déduit de Twist, dont la convention
            // n'est pas documentée : on retourne l'hélice si elle ne tourne pas dans le sens demandé.
            if (IsClockwise(helix) != clockwise)
            {
                helix.Twist = !helix.Twist;
                helix.CreateHelix();
            }
        }
        catch (AcException ex)
        {
            helix.Dispose();
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer l'hélice ({ex.ErrorStatus}).");
        }

        helix.TransformBy(Matrix3d.Displacement(center - Point3d.Origin) * AlignZAxis(axis));
        var result = EditTools.Append(context, helix, reader);
        return new
        {
            Helix = result,
            BaseRadius = Format.Number(helix.BaseRadius),
            TopRadius = Format.Number(helix.TopRadius),
            Height = Format.Number(helix.Height),
            Turns = Format.Number(helix.Turns),
            TurnHeight = Format.Number(helix.TurnHeight),
            Clockwise = clockwise,
            StartPoint = Format.Point(helix.StartPoint),
            EndPoint = Format.Point(helix.EndPoint),
            Length = Format.Number(helix.TotalLength),
        };
    }

    /// <summary>Sens de rotation d'une hélice d'axe Z vue du dessus, mesuré sur la tangente au point de départ.</summary>
    private static bool IsClockwise(Helix helix)
    {
        var radial = helix.StartPoint - Point3d.Origin;
        var tangent = helix.GetFirstDerivative(helix.StartParam);
        return radial.CrossProduct(tangent).Z < 0;
    }

    public static object Extrude(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var eraseProfiles = reader.GetBool("eraseProfiles", false);

        Entity? path = null;
        if (reader.Has("path"))
        {
            var pathId = Handles.Resolve(context, reader, "path").Single();
            path = transaction.GetObject(pathId, OpenMode.ForRead) as Curve
                ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Le chemin {pathId.Handle} n'est pas une courbe.");
        }

        var height = 0.0;
        if (path is null)
        {
            height = reader.RequireDouble("height");
            if (height == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur d'extrusion ne peut pas être nulle.");
        }

        Vector3d? direction = reader.Has("direction") ? Direction(reader, "direction") : null;
        var builder = new SweepOptionsBuilder { DraftAngle = Format.Radians(reader.GetDouble("taperAngle", 0)) };
        ApplySweepOptions(builder, reader, path is not null);
        using var options = builder.ToSweepOptions();
        var asSurface = reader.GetBool("surface", false);
        // Un profil gardé tel quel peut être mesuré le long de l'axe d'une hélice ; aligné, AutoCAD le fait pivoter.
        var profileKept = reader.Has("alignProfile") && !reader.GetBool("alignProfile", true);

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (asSurface)
            {
                // Une surface accepte aussi un profil ouvert : un mur à partir d'une polyligne en plan, par exemple.
                var curve = OpenCurve(id, transaction);
                DbSurface surface = path is null ? new ExtrudedSurface() : new SweptSurface();
                try
                {
                    if (surface is SweptSurface swept)
                        swept.CreateSweptSurface(curve, path, options);
                    else
                        ((ExtrudedSurface)surface).CreateExtrudedSurface(curve, (direction ?? SurfaceAxis(curve)) * height, options);
                }
                catch (AcException ex)
                {
                    surface.Dispose();
                    throw new PipeException(PipeErrorCodes.InvalidParams, path is Helix surfaceHelix
                        ? $"AutoCAD n'a pas pu balayer la courbe {id.Handle} le long de l'hélice {path.Handle} ({ex.ErrorStatus})." +
                          HelixSweepHint(surfaceHelix, curve, profileKept)
                        : $"AutoCAD n'a pas pu extruder la courbe {id.Handle} en surface ({ex.ErrorStatus}). " +
                          "La direction d'extrusion ne doit pas être dans le plan de la courbe.");
                }

                surface.Layer = curve.Layer;
                results.Add(CreatedSurface(context, surface, reader));
            }
            else
            {
                var profile = OpenProfile(id, transaction);
                var solid = new Solid3d();
                try
                {
                    if (path is not null)
                        solid.CreateSweptSolid(profile, path, options);
                    else
                        solid.CreateExtrudedSolid(profile, (direction ?? ExtrusionAxis(profile)) * height, options);
                }
                catch (AcException ex)
                {
                    solid.Dispose();
                    throw new PipeException(PipeErrorCodes.InvalidParams, path is Helix helix
                        ? $"AutoCAD n'a pas pu balayer le profil {id.Handle} le long de l'hélice {path.Handle} ({ex.ErrorStatus})." +
                          HelixSweepHint(helix, profile, profileKept)
                        : $"AutoCAD n'a pas pu extruder le profil {id.Handle} ({ex.ErrorStatus}). " +
                          "Vérifiez que le profil ne se recoupe pas et, avec une dépouille, que la hauteur reste compatible avec l'angle.");
                }

                solid.Layer = profile.Layer;
                results.Add(Created(context, solid, reader));
            }

            if (eraseProfiles)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return asSurface
            ? new { Count = results.Count, Surfaces = results, ProfilesErased = eraseProfiles }
            : (object)new { Count = results.Count, Solids = results, ProfilesErased = eraseProfiles };
    }

    public static object Revolve(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var axisStart = reader.RequirePoint("axisStart");
        var axisEnd = reader.RequirePoint("axisEnd");
        var axis = axisEnd - axisStart;
        if (axis.IsZeroLength())
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les deux points de l'axe de révolution sont confondus.");

        var angle = reader.GetDouble("angle", 360);
        if (angle == 0 || Math.Abs(angle) > 360)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'angle de révolution doit être compris entre -360 et 360 degrés, hors zéro.");

        var eraseProfiles = reader.GetBool("eraseProfiles", false);
        var asSurface = reader.GetBool("surface", false);
        using var options = new RevolveOptions();

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            if (asSurface)
            {
                var curve = OpenCurve(id, transaction);
                var surface = new RevolvedSurface();
                try
                {
                    surface.CreateRevolvedSurface(curve, axisStart, axis, Format.Radians(angle), 0, options);
                }
                catch (AcException ex)
                {
                    surface.Dispose();
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"AutoCAD n'a pas pu faire tourner la courbe {id.Handle} en surface ({ex.ErrorStatus}). " +
                        "L'axe ne doit pas traverser la courbe.");
                }

                surface.Layer = curve.Layer;
                results.Add(CreatedSurface(context, surface, reader));
            }
            else
            {
                var profile = OpenProfile(id, transaction);
                var solid = new Solid3d();
                try
                {
                    solid.CreateRevolvedSolid(profile, axisStart, axis, Format.Radians(angle), 0, options);
                }
                catch (AcException ex)
                {
                    solid.Dispose();
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"AutoCAD n'a pas pu faire tourner le profil {id.Handle} ({ex.ErrorStatus}). " +
                        "L'axe doit être dans le plan du profil et ne pas le traverser.");
                }

                solid.Layer = profile.Layer;
                results.Add(Created(context, solid, reader));
            }

            if (eraseProfiles)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        return asSurface
            ? new { Count = results.Count, Surfaces = results, ProfilesErased = eraseProfiles }
            : (object)new { Count = results.Count, Solids = results, ProfilesErased = eraseProfiles };
    }

    public static object BooleanSolids(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var operationName = reader.RequireString("operation");
        var operation = operationName.ToLowerInvariant() switch
        {
            "union" => BooleanOperationType.BoolUnite,
            "subtract" => BooleanOperationType.BoolSubtract,
            "intersect" => BooleanOperationType.BoolIntersect,
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Opération « {operationName} » inconnue : union, subtract ou intersect."),
        };

        var targetId = Handles.Resolve(context, reader, "target").Single();
        var toolIds = Handles.Resolve(context, reader, "tools");
        if (toolIds.Contains(targetId))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le solide cible ne peut pas figurer parmi les outils.");

        var target = OpenSolidForWrite(targetId, transaction, context);
        foreach (var toolId in toolIds)
        {
            var tool = OpenSolidForWrite(toolId, transaction, context);
            try
            {
                target.BooleanOperation(operation, tool);
            }
            catch (AcException ex)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD n'a pas pu combiner {targetId.Handle} et {toolId.Handle} ({ex.ErrorStatus}).");
            }

            // L'opération vide le solide outil : il ne reste qu'à le supprimer.
            tool.Erase();
        }

        // Une exception annule toute la transaction : le dessin reste intact.
        if (target.IsNull)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Le résultat serait vide (par exemple une intersection de solides disjoints) : opération annulée.");

        return new
        {
            Handle = target.Handle.ToString(),
            Operation = operationName.ToLowerInvariant(),
            Consumed = toolIds.Select(id => id.Handle.ToString()).ToArray(),
            Volume = TryVolume(target),
        };
    }

    public static object RotateEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var basePoint = reader.RequirePoint("basePoint");
        var angle = reader.RequireDouble("angle");
        var axis = reader.Has("axis") ? Direction(reader, "axis") : Vector3d.ZAxis;
        var rotation = Matrix3d.Rotation(Format.Radians(angle), axis, basePoint);

        var rotated = new List<string>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = EditTools.OpenForWrite(id, transaction, context);
            entity.TransformBy(rotation);
            rotated.Add(entity.Handle.ToString());
        }

        return new { Rotated = rotated.Count, Angle = angle, Axis = Format.Vector(axis), Handles = rotated };
    }

    public static object GetSolidProperties(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var inertia = reader.GetBool("inertia", false);

        var results = new List<object>();
        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            var properties = new Dictionary<string, object?>
            {
                ["handle"] = entity.Handle.ToString(),
                ["type"] = Format.DxfName(entity),
                ["layer"] = entity.Layer,
            };

            switch (entity)
            {
                case Solid3d solid:
                    try
                    {
                        var mass = solid.MassProperties;
                        properties["volume"] = Format.Number(mass.Volume);
                        properties["centroid"] = Format.Point(mass.Centroid);
                        if (inertia)
                        {
                            // Masse volumique 1 (masse = volume), comme la commande PROPMECA : moments et produits par
                            // rapport aux axes du SCG passant par l'origine, moments principaux et rayons de giration
                            // par rapport aux axes principaux passant par le centre de gravité.
                            // L'API d'AutoCAD écrit « Intertia », et ses axes principaux sont l'indexeur de la structure.
                            var axes = new[] { mass[0], mass[1], mass[2] };
                            properties["inertia"] = new
                            {
                                MomentsOfInertia = Format.Vector(mass.MomentsOfIntertia),
                                ProductsOfInertia = Format.Vector(mass.ProductsOfIntertia),
                                PrincipalMoments = Format.Vector(mass.PrincipalMoments),
                                PrincipalAxes = axes.Select(Format.Vector).ToArray(),
                                RadiiOfGyration = Format.Vector(mass.RadiiOfGyration),
                            };
                        }
                    }
                    catch (AcException)
                    {
                        properties["volume"] = null;
                    }

                    properties["surfaceArea"] = TryNumber(() => solid.Area);
                    break;

                case Region region:
                    properties["area"] = TryNumber(() => region.Area);
                    properties["perimeter"] = TryNumber(() => region.Perimeter);
                    break;

                case SubDMesh mesh:
                    properties["vertices"] = mesh.NumberOfVertices;
                    properties["faces"] = mesh.NumberOfFaces;
                    properties["smoothLevel"] = mesh.SmoothLevel;
                    properties["area"] = TryNumber(mesh.ComputeSurfaceArea);
                    properties["watertight"] = mesh.Watertight;
                    if (mesh.Watertight)
                        properties["volume"] = TryNumber(mesh.ComputeVolume);

                    break;

                case DbSurface surface:
                    properties["area"] = TryNumber(surface.GetArea);
                    break;

                case Curve curve:
                    properties["length"] = TryNumber(() => curve.GetDistanceAtParameter(curve.EndParam));
                    if (curve.Closed)
                        properties["area"] = TryNumber(() => curve.Area);

                    break;
            }

            properties["extents"] = Format.Extents(Format.TryGetExtents(entity));
            results.Add(properties);
        }

        return new { Count = results.Count, Objects = results };
    }

    public static object SetView(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var document = context.RequireDocument();
        var database = context.Database;
        var transaction = context.RequireTransaction();
        if (!database.TileMode)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "set_view ne s'applique qu'à l'espace objet : activez l'onglet Objet dans AutoCAD.");

        var preset = reader.GetString("view")?.ToLowerInvariant();
        var direction = preset is null ? (Vector3d?)null : PresetDirection(preset);
        var framing = ReadFraming(context.Database, reader);
        if (framing is not null && reader.GetBool("zoomExtents", false))
            throw new PipeException(PipeErrorCodes.InvalidParams, "zoomExtents ne se combine pas avec window ou center : choisissez l'un ou l'autre.");

        // Sans cadrage demandé, une vue prédéfinie zoome sur l'étendue, comme avant ; un changement de style seul garde le cadrage.
        var zoomExtents = framing is null && reader.GetBool("zoomExtents", direction is not null);
        if (direction is null && framing is null && !zoomExtents && !reader.Has("visualStyle"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez view, window, center, zoomExtents ou visualStyle.");

        var editor = document.Editor;
        using (var view = editor.GetCurrentView())
        {
            if (direction is { } newDirection)
            {
                view.ViewDirection = newDirection;
                view.ViewTwist = 0;
            }

            if (framing is { } frame)
                ApplyFraming(view, frame);
            else if (zoomExtents)
                ZoomExtents(view, database);

            if (reader.Has("visualStyle"))
                view.VisualStyleId = FindVisualStyle(database, transaction, reader.GetString("visualStyle")!);

            editor.SetCurrentView(view);
        }

        using var applied = editor.GetCurrentView();
        return new
        {
            View = preset,
            Direction = Format.Vector(applied.ViewDirection),
            Center = Format.Point(ViewCenter(applied)),
            Width = Format.Number(applied.Width),
            Height = Format.Number(applied.Height),
            VisualStyle = VisualStyleName(database, transaction, applied.VisualStyleId),
        };
    }

    private static Vector3d PresetDirection(string preset) =>
        preset switch
        {
            "top" => new Vector3d(0, 0, 1),
            "bottom" => new Vector3d(0, 0, -1),
            "front" => new Vector3d(0, -1, 0),
            "back" => new Vector3d(0, 1, 0),
            "left" => new Vector3d(-1, 0, 0),
            "right" => new Vector3d(1, 0, 0),
            "sw_iso" => new Vector3d(-1, -1, 1),
            "se_iso" => new Vector3d(1, -1, 1),
            "ne_iso" => new Vector3d(1, 1, 1),
            "nw_iso" => new Vector3d(-1, 1, 1),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Vue « {preset} » inconnue : top, bottom, front, back, left, right, sw_iso, se_iso, ne_iso ou nw_iso."),
        };

    /// <summary>Zone à cadrer dans le plan XY du SCG. Sans taille (centre seul), la vue est seulement recentrée.</summary>
    private sealed record Framing(Point3d Min, Point3d Max, bool KeepSize);

    /// <summary>
    /// Cadrage demandé par window [minX, minY, maxX, maxY] ou center [x, y] (avec width et height facultatifs), en
    /// coordonnées du dessin ou, avec lonLat, en longitude/latitude converties dans le système du dessin.
    /// </summary>
    private static Framing? ReadFraming(Database database, ArgReader reader)
    {
        var window = reader.GetNumbers("window", 4);
        var center = reader.GetNumbers("center", 2);
        var lonLat = reader.GetBool("lonLat", false);
        if (window is not null && center is not null)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez window ou center, pas les deux.");
        if (center is null && (reader.Has("width") || reader.Has("height")))
            throw new PipeException(PipeErrorCodes.InvalidParams, "width et height s'utilisent avec center.");
        if (window is null && center is null)
        {
            return lonLat
                ? throw new PipeException(PipeErrorCodes.InvalidParams, "lonLat s'applique aux coordonnées de window ou de center.")
                : null;
        }

        if (window is not null)
        {
            var (minX, maxX) = (Math.Min(window[0], window[2]), Math.Max(window[0], window[2]));
            var (minY, maxY) = (Math.Min(window[1], window[3]), Math.Max(window[1], window[3]));
            if (maxX - minX <= 0 || maxY - minY <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "window doit avoir une largeur et une hauteur non nulles : [minX, minY, maxX, maxY].");
            if (!lonLat)
                return new Framing(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0), false);

            // Projetés, les bords d'une fenêtre en longitude/latitude se courbent : coins et milieux des côtés sont convertis.
            var (midX, midY) = ((minX + maxX) / 2, (minY + maxY) / 2);
            var points = CoordinateSystemTools.ProjectLonLat(database,
                [(minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY), (midX, minY), (midX, maxY), (minX, midY), (maxX, midY)]);
            return new Framing(
                new Point3d(points.Min(p => p.X), points.Min(p => p.Y), 0),
                new Point3d(points.Max(p => p.X), points.Max(p => p.Y), 0), false);
        }

        var (x, y) = lonLat ? CoordinateSystemTools.ProjectLonLat(database, [(center![0], center[1])])[0] : (center![0], center[1]);
        var width = reader.Has("width") ? Positive(reader, "width") : 0;
        var height = reader.Has("height") ? Positive(reader, "height") : 0;
        return new Framing(new Point3d(x - width / 2, y - height / 2, 0), new Point3d(x + width / 2, y + height / 2, 0),
            width == 0 && height == 0);
    }

    private static void ApplyFraming(ViewTableRecord view, Framing framing)
    {
        if (!framing.KeepSize)
        {
            FitBox(view, framing.Min, framing.Max, 1.0);
            return;
        }

        // Recentrage seul : la cible passe au point demandé, la taille de la vue ne change pas.
        view.Target = framing.Min;
        view.CenterPoint = Point2d.Origin;
    }

    /// <summary>Centre de la vue dans le SCG : CenterPoint est exprimé dans le repère de la vue (DCS).</summary>
    private static Point3d ViewCenter(ViewTableRecord view) =>
        new Point3d(view.CenterPoint.X, view.CenterPoint.Y, 0).TransformBy(EyeToWorld(view));

    /// <summary>
    /// Cause la plus fréquente d'un balayage hélicoïdal refusé : un profil plus large que deux pas environ le long de
    /// l'axe recouvre plusieurs spires voisines, et AutoCAD échoue (GeneralModelingFailure) dès deux tours. Mesuré dans
    /// Civil 3D 2026 avec un peigne ISO de pas 1,5 : 1,6 pas passe, 2,4 pas échoue, quel que soit le nombre de tours.
    /// </summary>
    private static string HelixSweepHint(Helix helix, Entity profile, bool profileKept)
    {
        var pitch = helix.TurnHeight;
        var measured = profileKept && profile is Curve curve && pitch > 0
            && AxialExtent(curve, helix.AxisVector.GetNormal()) is > 0 and var extent
            ? $" Le profil mesure {Format.Number(extent)} le long de l'axe, soit {Format.Number(extent / pitch)} pas."
            : "";
        return $" Le pas de l'hélice vaut {Format.Number(pitch)}.{measured} Au-delà d'environ deux pas le long de l'axe, " +
               "le profil recouvre plusieurs spires et le balayage échoue. Pour un filetage, dessinez le peigne dans un plan " +
               "contenant l'axe, au départ de l'hélice, balayez avec alignProfile à faux et coupez-le juste au-delà de la " +
               "crête, pour qu'il reste moins large qu'un pas.";
    }

    /// <summary>
    /// Étendue d'une courbe le long d'un axe, par échantillonnage (sommets des polylignes compris). Nulle si la courbe
    /// ne se laisse pas évaluer : l'explication ne doit pas masquer l'erreur qu'elle accompagne.
    /// </summary>
    private static double? AxialExtent(Curve curve, Vector3d axis)
    {
        double min = double.MaxValue, max = double.MinValue;
        void Add(double parameter)
        {
            var z = (curve.GetPointAtParameter(parameter) - Point3d.Origin).DotProduct(axis);
            min = Math.Min(min, z);
            max = Math.Max(max, z);
        }

        try
        {
            const int samples = 360;
            for (var i = 0; i <= samples; i++)
                Add(curve.StartParam + (curve.EndParam - curve.StartParam) * i / samples);

            // Les sommets d'une polyligne tombent sur les paramètres entiers.
            for (var k = Math.Ceiling(curve.StartParam); k <= curve.EndParam; k++)
                Add(k);
        }
        catch (AcException)
        {
            return null;
        }

        return max - min;
    }

    /// <summary>Options propres au balayage le long d'un chemin : torsion, échelle, alignement, point de base, inclinaison.</summary>
    private static void ApplySweepOptions(SweepOptionsBuilder builder, ArgReader reader, bool hasPath)
    {
        string[] sweepOnly = ["twistAngle", "scaleFactor", "alignProfile", "basePoint", "bank"];
        if (!hasPath)
        {
            if (sweepOnly.FirstOrDefault(reader.Has) is { } name)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » ne s'applique qu'au balayage : indiquez « path ».");

            return;
        }

        if (reader.Has("twistAngle"))
            builder.TwistAngle = Format.Radians(reader.GetDouble("twistAngle", 0));

        if (reader.Has("scaleFactor"))
        {
            var scale = reader.GetDouble("scaleFactor", 1);
            builder.ScaleFactor = scale > 0
                ? scale
                : throw new PipeException(PipeErrorCodes.InvalidParams, "« scaleFactor » doit être strictement positif.");
        }

        if (reader.Has("alignProfile"))
            builder.Align = reader.GetBool("alignProfile", true)
                ? SweepOptionsAlignOption.AlignSweepEntityToPath
                : SweepOptionsAlignOption.NoAlignment;

        if (reader.Has("basePoint"))
            builder.BasePoint = reader.RequirePoint("basePoint");

        if (reader.Has("bank"))
            builder.Bank = reader.GetBool("bank", false);
    }

    internal static double? TryVolume(Solid3d solid)
    {
        try
        {
            return Format.Number(solid.MassProperties.Volume);
        }
        catch (AcException)
        {
            return null;
        }
    }

    /// <summary>Les primitives sont créées centrées sur l'origine : le coin minimal est amené sur « corner ».</summary>
    private static object PlaceByCorner(ToolContext context, Solid3d solid, ArgReader reader, double length, double width, double height)
    {
        var corner = reader.RequirePoint("corner");
        var rotation = Format.Radians(reader.GetDouble("rotation", 0));
        solid.TransformBy(
            Matrix3d.Rotation(rotation, Vector3d.ZAxis, corner)
            * Matrix3d.Displacement(corner - Point3d.Origin + new Vector3d(length / 2, width / 2, height / 2)));

        return Created(context, solid, reader);
    }

    private static object Created(ToolContext context, Solid3d solid, ArgReader reader)
    {
        var appended = EditTools.Append(context, solid, reader);
        return new { Solid = appended, Volume = TryVolume(solid) };
    }

    private static object CreatedSurface(ToolContext context, DbSurface surface, ArgReader reader)
    {
        var appended = EditTools.Append(context, surface, reader);
        return new { Surface = appended, Area = TryNumber(surface.GetArea) };
    }

    /// <summary>Rotation qui amène l'axe Z sur <paramref name="axis"/>.</summary>
    private static Matrix3d AlignZAxis(Vector3d axis)
    {
        var target = axis.GetNormal();
        if (target.IsCodirectionalTo(Vector3d.ZAxis))
            return Matrix3d.Identity;

        if (target.IsCodirectionalTo(-Vector3d.ZAxis))
            return Matrix3d.Rotation(Math.PI, Vector3d.XAxis, Point3d.Origin);

        return Matrix3d.Rotation(Vector3d.ZAxis.GetAngleTo(target), Vector3d.ZAxis.CrossProduct(target), Point3d.Origin);
    }

    /// <summary>Profil d'extrusion ou de révolution : région, ou courbe plane fermée.</summary>
    private static Entity OpenProfile(ObjectId id, Transaction transaction)
    {
        var entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
        return entity switch
        {
            Region => entity,
            Curve { Closed: true, IsPlanar: true } => entity,
            Curve { Closed: false } => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le profil {id.Handle} ({Format.DxfName(entity)}) n'est pas fermé : fermez la polyligne, ou utilisez un cercle ou une région."),
            Curve => throw new PipeException(PipeErrorCodes.InvalidParams, $"Le profil {id.Handle} n'est pas plan."),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {id.Handle} n'est ni une courbe fermée ni une région : il ne peut pas servir de profil."),
        };
    }

    /// <summary>Axe d'extrusion par défaut : la normale du profil, ramenée à +Z pour un profil horizontal.</summary>
    private static Vector3d ExtrusionAxis(Entity profile)
    {
        var normal = profile switch
        {
            Region region => region.Normal,
            Curve curve => curve.GetPlane().Normal,
            _ => Vector3d.ZAxis,
        };

        return normal.IsParallelTo(Vector3d.ZAxis) ? Vector3d.ZAxis : normal.GetNormal();
    }

    /// <summary>
    /// Direction d'extrusion d'une surface par défaut : celle de la courbe (sa normale, +Z pour une courbe dessinée en
    /// plan), la normale d'une courbe plane fermée, sinon +Z. Le plan d'une ligne n'est pas défini : on n'y lit rien.
    /// </summary>
    private static Vector3d SurfaceAxis(Curve curve)
    {
        var normal = curve switch
        {
            Line line => line.Normal,
            Arc arc => arc.Normal,
            Circle circle => circle.Normal,
            Ellipse ellipse => ellipse.Normal,
            Polyline polyline => polyline.Normal,
            Polyline2d polyline => polyline.Normal,
            { Closed: true, IsPlanar: true } => curve.GetPlane().Normal,
            _ => Vector3d.ZAxis,
        };

        return normal.IsParallelTo(Vector3d.ZAxis) ? Vector3d.ZAxis : normal.GetNormal();
    }

    private static Solid3d OpenSolidForWrite(ObjectId id, Transaction transaction, ToolContext context)
    {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Solid3d)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} n'est pas un solide 3D.");

        return (Solid3d)EditTools.OpenForWrite(id, transaction, context);
    }

    internal static void ZoomExtents(ViewTableRecord view, Database database)
    {
        database.UpdateExt(true);
        var min = database.Extmin;
        var max = database.Extmax;
        if (min.X > max.X)
            return; // Dessin vide.

        FitBox(view, min, max, 1.05);
    }

    /// <summary>
    /// Cadre la vue, dans sa direction actuelle, sur une boîte du SCG agrandie de <paramref name="margin"/>. La cible
    /// passe au centre de la boîte : une rotation de vue ensuite pivote autour d'elle, pas autour d'un point éloigné.
    /// </summary>
    internal static void FitBox(ViewTableRecord view, Point3d min, Point3d max, double margin)
    {
        view.Target = new Point3d((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var worldToEye = EyeToWorld(view).Inverse();

        var corners = new[]
        {
            new Point3d(min.X, min.Y, min.Z), new Point3d(max.X, min.Y, min.Z),
            new Point3d(min.X, max.Y, min.Z), new Point3d(max.X, max.Y, min.Z),
            new Point3d(min.X, min.Y, max.Z), new Point3d(max.X, min.Y, max.Z),
            new Point3d(min.X, max.Y, max.Z), new Point3d(max.X, max.Y, max.Z),
        }.Select(corner => corner.TransformBy(worldToEye)).ToArray();

        var (left, right) = (corners.Min(p => p.X), corners.Max(p => p.X));
        var (bottom, top) = (corners.Min(p => p.Y), corners.Max(p => p.Y));
        var width = Math.Max(right - left, 1e-6) * margin;
        var height = Math.Max(top - bottom, 1e-6) * margin;

        // On conserve les proportions de la fenêtre.
        var ratio = view.Width / view.Height;
        if (width / height > ratio)
            height = width / ratio;
        else
            width = height * ratio;

        view.CenterPoint = new Point2d((left + right) / 2, (bottom + top) / 2);
        view.Width = width;
        view.Height = height;
    }

    /// <summary>Passage du repère de la vue (DCS), où se définissent centre, largeur et hauteur, au repère général.</summary>
    private static Matrix3d EyeToWorld(ViewTableRecord view) =>
        Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
        * Matrix3d.Displacement(view.Target - Point3d.Origin)
        * Matrix3d.PlaneToWorld(view.ViewDirection);

    private static readonly Dictionary<string, string> VisualStyleAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2dwireframe"] = "2dWireframe",
        ["wireframe"] = "Wireframe",
        ["hidden"] = "Hidden",
        ["realistic"] = "Realistic",
        ["conceptual"] = "Conceptual",
        ["shaded"] = "Shaded",
        ["shaded_edges"] = "Shaded with edges",
        ["shades_of_gray"] = "Shades of Gray",
        ["sketchy"] = "Sketchy",
        ["xray"] = "X-Ray",
    };

    internal static ObjectId FindVisualStyle(Database database, Transaction transaction, string name)
    {
        var styles = VisualStyles(database, transaction);
        var key = VisualStyleAliases.TryGetValue(name, out var alias) ? alias : name;
        foreach (DictionaryEntry entry in styles)
        {
            if (string.Equals((string)entry.Key, key, StringComparison.OrdinalIgnoreCase))
                return (ObjectId)entry.Value!;
        }

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Style visuel « {name} » introuvable. Valeurs possibles : {string.Join(", ", VisualStyleAliases.Keys)}.");
    }

    internal static string? VisualStyleName(Database database, Transaction transaction, ObjectId id)
    {
        if (id.IsNull)
            return null;

        foreach (DictionaryEntry entry in VisualStyles(database, transaction))
        {
            if ((ObjectId)entry.Value! == id)
                return (string)entry.Key;
        }

        return null;
    }

    private static DBDictionary VisualStyles(Database database, Transaction transaction)
    {
        var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        return (DBDictionary)transaction.GetObject(named.GetAt("ACAD_VISUALSTYLE"), OpenMode.ForRead);
    }

    private static double Positive(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value > 0
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » doit être strictement positif.");
    }

    private static Vector3d Direction(ArgReader reader, string name)
    {
        var point = reader.RequirePoint(name);
        var vector = new Vector3d(point.X, point.Y, point.Z);
        return vector.IsZeroLength()
            ? throw new PipeException(PipeErrorCodes.InvalidParams, $"Le vecteur « {name} » ne peut pas être nul.")
            : vector.GetNormal();
    }

    private static double? TryNumber(Func<double> read)
    {
        try
        {
            return Format.Number(read());
        }
        catch (AcException)
        {
            return null;
        }
    }
}

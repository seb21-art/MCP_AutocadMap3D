using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Outils de modification. Chaque appel s'exécute sous verrou d'écriture nommé et dans une transaction :
/// il est annulable en une seule étape avec la commande U d'AutoCAD.
/// </summary>
internal static class EditTools
{
    private const int MaxHandles = 5000;

    public static object CreateLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name");
        var transaction = context.RequireTransaction();
        var database = context.Database;

        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {name} » n'est pas un nom de calque valide (caractères interdits : < > / \\ \" : ; ? * | , = `).");
        }

        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForWrite);
        var created = !layers.Has(name);
        LayerTableRecord layer;
        if (created)
        {
            // Type de ligne explicite : sinon il reste nul jusqu'à la fin de la transaction.
            layer = new LayerTableRecord { Name = name, LinetypeObjectId = database.ContinuousLinetype };
            layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }
        else
        {
            layer = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForWrite);
        }

        if (reader.Has("color"))
            layer.Color = ParseColor(reader.GetString("color")!);

        if (reader.Has("description"))
            layer.Description = reader.GetString("description");

        var linetypeLoaded = false;
        if (reader.Has("linetype"))
            layer.LinetypeObjectId = EntityProperties.EnsureLinetype(database, transaction, reader.GetString("linetype")!, out linetypeLoaded);

        if (reader.Has("lineWeight"))
        {
            var weight = EntityProperties.ReadLineWeight(reader, "lineWeight");
            if (weight is LineWeight.ByLayer or LineWeight.ByBlock)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "Un calque ne peut pas avoir l'épaisseur ByLayer ou ByBlock : indiquez une valeur en mm ou Default.");

            layer.LineWeight = weight;
        }

        if (reader.Has("off"))
            layer.IsOff = reader.GetBool("off", false);

        if (reader.Has("locked"))
            layer.IsLocked = reader.GetBool("locked", false);

        if (reader.GetBool("current", false))
            database.Clayer = layer.ObjectId;

        var linetype = (LinetypeTableRecord)transaction.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
        return new
        {
            Created = created,
            layer.Name,
            Color = Format.Color(layer.Color),
            Linetype = linetype.Name,
            LinetypeLoaded = linetypeLoaded ? true : (bool?)null,
            LineWeight = EntityProperties.FormatLineWeight(layer.LineWeight),
        };
    }

    /// <summary>Segment de start à end ; ou droite infinie (XLINE) ou demi-droite (RAY) passant par ces deux points.</summary>
    public static object CreateLine(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var start = reader.RequirePoint("start");
        var end = reader.RequirePoint("end");
        var kind = (reader.GetString("kind") ?? "segment").Trim().ToLowerInvariant();
        if (kind is "xline" or "ray" && (end - start).IsZeroLength())
            throw new PipeException(PipeErrorCodes.InvalidParams, "Les points start et end sont confondus : direction indéfinie.");

        Entity line = kind switch
        {
            "segment" => new Line(start, end),
            "xline" => new Xline { BasePoint = start, UnitDir = (end - start).GetNormal() },
            "ray" => new Ray { BasePoint = start, UnitDir = (end - start).GetNormal() },
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Type de ligne « {kind} » inconnu : segment, xline (droite infinie) ou ray (demi-droite partant de start)."),
        };

        return Append(context, line, reader);
    }

    public static object CreatePolyline(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var polyline = BuildPlanarPolyline(reader, "points", reader.GetBool("closed", false), reader.GetDouble("width", 0));
        return Append(context, polyline, reader);
    }

    /// <summary>
    /// Polyligne plane construite en mémoire à partir des sommets « name ». Sans « plane » ni « normal », des points
    /// de même Z donnent une polyligne horizontale à cette élévation, comme avant ; des points 3D coplanaires donnent
    /// une polyligne dans leur plan, de normale calculée. « plane » (xy, xz, yz) lit les sommets comme des
    /// coordonnées [u, v] dans ce plan, décalé de « planeOffset » ; « normal » impose la normale.
    /// </summary>
    internal static Polyline BuildPlanarPolyline(ArgReader reader, string name, bool closed, double width)
    {
        var points = reader.RequirePoints(name, minimum: 2);
        Vector3d? normal = null;

        if (reader.Has("plane"))
        {
            if (reader.Has("normal"))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez « plane » ou « normal », pas les deux.");

            var offset = reader.GetDouble("planeOffset", 0);
            var plane = reader.GetString("plane")!.ToLowerInvariant();
            (points, normal) = plane switch
            {
                "xy" => (points.Select(p => new Point3d(p.X, p.Y, offset)).ToList(), Vector3d.ZAxis),
                "xz" => (points.Select(p => new Point3d(p.X, offset, p.Y)).ToList(), Vector3d.YAxis),
                "yz" => (points.Select(p => new Point3d(offset, p.X, p.Y)).ToList(), Vector3d.XAxis),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« plane » vaut xy, xz ou yz, pas « {plane} »."),
            };
        }
        else if (reader.Has("normal"))
        {
            var vector = reader.RequirePoint("normal");
            var given = new Vector3d(vector.X, vector.Y, vector.Z);
            if (given.IsZeroLength())
                throw new PipeException(PipeErrorCodes.InvalidParams, "Le vecteur « normal » ne peut pas être nul.");

            normal = given.GetNormal();
        }

        var origin = points[0];
        var extent = points.Max(p => p.DistanceTo(origin));
        var tolerance = Math.Max(1e-9, extent * 1e-6);

        // Points de même Z sans normale imposée : polyligne horizontale, exactement comme avant.
        if (normal is null && points.All(p => Math.Abs(p.Z - origin.Z) <= tolerance))
            normal = Vector3d.ZAxis;

        var n = normal ?? NewellNormal(points, tolerance);
        var deviation = points.Max(p => Math.Abs((p - origin).DotProduct(n)));
        if (deviation > tolerance)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Les sommets ne sont pas coplanaires : l'un d'eux s'écarte de {Format.Number(deviation)} du plan " +
                (normal is null ? "qu'ils définissent" : "perpendiculaire à la normale") +
                ". Donnez des coordonnées plus précises, ou dessinez une polyligne 3D.");

        var polyline = new Polyline(points.Count) { Closed = closed };
        if (n.IsCodirectionalTo(Vector3d.ZAxis))
        {
            polyline.Elevation = origin.Z;
            for (var index = 0; index < points.Count; index++)
                polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0, width, width);

            return polyline;
        }

        // Plan quelconque : sommets exprimés dans un repère (u, v, n) du plan, polyligne tracée dans XY
        // puis amenée dans ce plan ; AutoCAD en déduit la normale et l'élévation dans son repère objet (SCO).
        var u = FirstDirection(points, origin, n);
        var v = n.CrossProduct(u);
        for (var index = 0; index < points.Count; index++)
        {
            var offset = points[index] - origin;
            polyline.AddVertexAt(index, new Point2d(offset.DotProduct(u), offset.DotProduct(v)), 0, width, width);
        }

        polyline.TransformBy(Matrix3d.AlignCoordinateSystem(
            Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, origin, u, v, n));
        return polyline;
    }

    /// <summary>
    /// Normale d'un contour 3D par la méthode de Newell, tournée pour que sa plus grande composante soit positive
    /// (plan XZ : +Y, plan YZ : +X), ce qui la rend indépendante du sens de parcours.
    /// </summary>
    private static Vector3d NewellNormal(IReadOnlyList<Point3d> points, double tolerance)
    {
        double x = 0, y = 0, z = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var (a, b) = (points[index], points[(index + 1) % points.Count]);
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }

        var normal = new Vector3d(x, y, z);
        if (normal.Length <= tolerance * tolerance)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Les sommets sont alignés : leur plan est indéterminé. Précisez « normal » ou « plane ».");

        normal = normal.GetNormal();
        var largest = Math.Abs(normal.X) >= Math.Abs(normal.Y) && Math.Abs(normal.X) >= Math.Abs(normal.Z) ? normal.X
            : Math.Abs(normal.Y) >= Math.Abs(normal.Z) ? normal.Y : normal.Z;
        return largest < 0 ? -normal : normal;
    }

    /// <summary>Axe u du plan : direction du premier sommet distinct de l'origine, projetée dans le plan.</summary>
    private static Vector3d FirstDirection(IReadOnlyList<Point3d> points, Point3d origin, Vector3d normal)
    {
        foreach (var point in points)
        {
            var offset = point - origin;
            var inPlane = offset - normal * offset.DotProduct(normal);
            if (!inPlane.IsZeroLength())
                return inPlane.GetNormal();
        }

        return normal.GetPerpendicularVector().GetNormal();
    }

    public static object CreateText(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var text = new DBText
        {
            TextString = reader.RequireString("text"),
            Position = reader.RequirePoint("position"),
            Height = reader.GetDouble("height", 2.5),
            Rotation = Format.Radians(reader.GetDouble("rotation", 0)),
        };

        if (text.Height <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "La hauteur du texte doit être supérieure à zéro.");

        if (reader.Has("style"))
        {
            var styleName = reader.GetString("style")!;
            var styles = (TextStyleTable)context.RequireTransaction().GetObject(context.Database.TextStyleTableId, OpenMode.ForRead);
            if (!styles.Has(styleName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le style de texte « {styleName} » n'existe pas dans le dessin.");

            text.TextStyleId = styles[styleName];

            // Comme la commande TEXTE : facteur de largeur et inclinaison du style.
            var style = (TextStyleTableRecord)context.RequireTransaction().GetObject(text.TextStyleId, OpenMode.ForRead);
            text.WidthFactor = style.XScale;
            text.Oblique = style.ObliquingAngle;
        }

        return Append(context, text, reader);
    }

    public static object MoveEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();

        Vector3d displacement;
        if (reader.Has("displacement"))
        {
            var vector = reader.RequirePoint("displacement");
            displacement = new Vector3d(vector.X, vector.Y, vector.Z);
        }
        else if (reader.Has("from") && reader.Has("to"))
        {
            displacement = reader.RequirePoint("to") - reader.RequirePoint("from");
        }
        else
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez le déplacement : soit « displacement » ([dx, dy] ou [dx, dy, dz]), soit « from » et « to ».");
        }

        var moved = new List<string>();
        foreach (var id in ResolveHandles(context, reader))
        {
            var entity = OpenForWrite(id, transaction, context);
            entity.TransformBy(Matrix3d.Displacement(displacement));
            moved.Add(entity.Handle.ToString());
        }

        return new
        {
            Moved = moved.Count,
            Displacement = new[] { Format.Number(displacement.X), Format.Number(displacement.Y), Format.Number(displacement.Z) },
            Handles = moved,
        };
    }

    public static object ChangeLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var layerName = reader.RequireString("layer");
        var layers = (LayerTable)transaction.GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(layerName))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le calque « {layerName} » n'existe pas. Créez-le d'abord avec create_layer.");

        var changed = new List<string>();
        foreach (var id in ResolveHandles(context, reader))
        {
            var entity = OpenForWrite(id, transaction, context);
            entity.Layer = layerName;
            changed.Add(entity.Handle.ToString());
        }

        return new { Changed = changed.Count, Layer = layerName, Handles = changed };
    }

    public static object EraseEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();

        var erased = new List<string>();
        foreach (var id in ResolveHandles(context, reader))
        {
            var entity = OpenForWrite(id, transaction, context);
            var handle = entity.Handle.ToString();
            entity.Erase();
            erased.Add(handle);
        }

        return new { Erased = erased.Count, Handles = erased };
    }

    /// <summary>Ajoute l'objet dans l'espace courant, sur le calque demandé.</summary>
    internal static object Append(ToolContext context, Entity entity, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        var database = context.Database;

        if (reader.Has("layer"))
        {
            var layerName = reader.GetString("layer")!;
            var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layerName))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le calque « {layerName} » n'existe pas. Créez-le d'abord avec create_layer.");

            entity.Layer = layerName;
        }

        if (reader.Has("color"))
            entity.Color = ParseColor(reader.GetString("color")!);

        if (reader.Has("linetype"))
            entity.LinetypeId = EntityProperties.EnsureLinetype(database, transaction, reader.GetString("linetype")!, out _);

        if (reader.Has("lineWeight"))
            entity.LineWeight = EntityProperties.ReadLineWeight(reader, "lineWeight");

        var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        space.AppendEntity(entity);
        transaction.AddNewlyCreatedDBObject(entity, true);

        return new
        {
            Handle = entity.Handle.ToString(),
            Type = Format.DxfName(entity),
            entity.Layer,
            Space = database.TileMode ? "model" : "paper",
        };
    }

    private static IReadOnlyList<ObjectId> ResolveHandles(ToolContext context, ArgReader reader) =>
        Handles.Resolve(context, reader);

    internal static Entity OpenForWrite(ObjectId id, Transaction transaction, ToolContext context)
    {
        if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {id.Handle} n'est pas un objet graphique du dessin.");

        var layers = (LayerTable)transaction.GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        var layer = (LayerTableRecord)transaction.GetObject(layers[entity.Layer], OpenMode.ForRead);
        if (layer.IsLocked)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'objet {entity.Handle} est sur le calque verrouillé « {entity.Layer} » : déverrouillez-le d'abord.");

        entity.UpgradeOpen();
        return entity;
    }

    internal static Color ParseColor(string text) => EntityProperties.ParseColor(text);

    /// <summary>
    /// Change les propriétés graphiques d'objets existants : couleur, type de ligne et son échelle,
    /// épaisseur de ligne, épaisseur d'extrusion. Seules les propriétés fournies sont modifiées.
    /// </summary>
    public static object SetEntityProperties(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;

        string[] properties = ["color", "linetype", "linetypeScale", "lineWeight", "thickness"];
        if (!properties.Any(reader.Has))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Indiquez au moins une propriété à modifier : {string.Join(", ", properties)}.");

        var color = reader.Has("color") ? ParseColor(reader.GetString("color")!) : null;
        var linetypeLoaded = false;
        var linetypeId = reader.Has("linetype")
            ? EntityProperties.EnsureLinetype(database, transaction, reader.GetString("linetype")!, out linetypeLoaded)
            : ObjectId.Null;
        double? linetypeScale = reader.Has("linetypeScale") ? reader.GetDouble("linetypeScale", 1) : null;
        if (linetypeScale <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle du type de ligne doit être strictement positive.");

        LineWeight? lineWeight = reader.Has("lineWeight") ? EntityProperties.ReadLineWeight(reader, "lineWeight") : null;
        double? thickness = reader.Has("thickness") ? reader.GetDouble("thickness", 0) : null;

        var changed = new List<string>();
        var withoutThickness = new List<string>();
        foreach (var id in ResolveHandles(context, reader))
        {
            var entity = OpenForWrite(id, transaction, context);
            if (color is not null)
                entity.Color = color;

            if (!linetypeId.IsNull)
                entity.LinetypeId = linetypeId;

            if (linetypeScale is double scale)
                entity.LinetypeScale = scale;

            if (lineWeight is LineWeight weight)
                entity.LineWeight = weight;

            if (thickness is double value && !EntityProperties.TrySetThickness(entity, value))
                withoutThickness.Add($"{entity.Handle} ({Format.DxfName(entity)})");

            changed.Add(entity.Handle.ToString());
        }

        return new
        {
            Changed = changed.Count,
            Color = color is null ? null : Format.Color(color),
            Linetype = reader.GetString("linetype"),
            LinetypeLoaded = linetypeLoaded ? EntityProperties.LinetypeFile(database) : null,
            LinetypeScale = linetypeScale,
            LineWeight = lineWeight is LineWeight shown ? EntityProperties.FormatLineWeight(shown) : null,
            Thickness = thickness,
            ThicknessNotApplicable = withoutThickness.Count > 0 ? withoutThickness : null,

            // Sans l'affichage des épaisseurs (bouton EL/LWT, variable LWDISPLAY), les traits restent fins à l'écran.
            LineWeightDisplayOn = lineWeight is null ? (bool?)null : database.LineWeightDisplay,
            Handles = changed,
        };
    }
}

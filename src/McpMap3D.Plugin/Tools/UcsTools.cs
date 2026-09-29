using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Systèmes de coordonnées utilisateur (SCU) : SCU courant de l'espace actif, SCU nommés et conversion de points.
/// Les autres outils restent en coordonnées générales (SCG) : le SCU ne change que ce que voit l'utilisateur, et
/// convert_ucs_points passe de l'un à l'autre.
/// </summary>
internal static class UcsTools
{
    private const double Tolerance = 1e-9;

    /// <summary>Repère orthonormé direct, exprimé dans le SCG.</summary>
    private readonly record struct Ucs(Point3d Origin, Vector3d X, Vector3d Y)
    {
        public static readonly Ucs World = new(Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis);

        public Vector3d Z => X.CrossProduct(Y);

        /// <summary>Matrice qui porte les coordonnées du SCU vers le SCG.</summary>
        public Matrix3d ToWorld => Matrix3d.AlignCoordinateSystem(
            Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, Origin, X, Y, Z);

        public bool IsWorld => IsSame(World);

        public bool IsSame(Ucs other) =>
            Origin.DistanceTo(other.Origin) < 1e-6 && X.IsEqualTo(other.X, new Tolerance(1e-9, 1e-9))
            && Y.IsEqualTo(other.Y, new Tolerance(1e-9, 1e-9));

        public static Ucs FromMatrix(Matrix3d matrix)
        {
            var cs = matrix.CoordinateSystem3d;
            return new Ucs(cs.Origin, cs.Xaxis.GetNormal(), cs.Yaxis.GetNormal());
        }

        /// <summary>X normé, Y rendu perpendiculaire à X dans le plan (X, Y).</summary>
        public static Ucs FromAxes(Point3d origin, Vector3d x, Vector3d y, string what)
        {
            if (x.Length < Tolerance || y.Length < Tolerance)
                throw Invalid($"{what} : un axe est nul.");

            x = x.GetNormal();
            var yPerp = y - x * y.DotProduct(x);
            if (yPerp.Length < 1e-9 * y.Length)
                throw Invalid($"{what} : les axes X et Y sont colinéaires.");

            return new Ucs(origin, x, yPerp.GetNormal());
        }

        public static Ucs FromZ(Point3d origin, Vector3d z) => new(origin, ArbitraryX(z), z.GetNormal().CrossProduct(ArbitraryX(z)));

        public object Describe() => new
        {
            Origin = Format.Point(Origin),
            XAxis = Format.Vector(X),
            YAxis = Format.Vector(Y),
            ZAxis = Format.Vector(Z),
            IsWorld,
        };
    }

    public static object ListUcs(ToolContext context, JsonElement? args)
    {
        var transaction = context.RequireTransaction();
        var current = Ucs.FromMatrix(context.Editor.CurrentUserCoordinateSystem);
        var named = ReadNamed(context.Database, transaction);

        return new
        {
            Space = context.Database.TileMode ? "model" : "paper",
            Current = new
            {
                Origin = Format.Point(current.Origin),
                XAxis = Format.Vector(current.X),
                YAxis = Format.Vector(current.Y),
                ZAxis = Format.Vector(current.Z),
                current.IsWorld,
                // UCSNAME n'est renseigné que pour un SCU restauré par la commande SCU ; les SCU nommés de mêmes
                // origine et axes sont donnés à part.
                Name = AcApp.GetSystemVariable("UCSNAME") is string { Length: > 0 } ucsName ? ucsName : null,
                MatchingNamedUcs = named.Where(pair => pair.Ucs.IsSame(current)).Select(pair => pair.Name).ToList(),
            },
            NamedCount = named.Count,
            Named = named.Select(pair => new
            {
                pair.Name,
                Origin = Format.Point(pair.Ucs.Origin),
                XAxis = Format.Vector(pair.Ucs.X),
                YAxis = Format.Vector(pair.Ucs.Y),
                ZAxis = Format.Vector(pair.Ucs.Z),
            }).ToList(),
        };
    }

    public static object SetUcs(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var database = context.Database;
        var transaction = context.RequireTransaction();
        var editor = context.Editor;
        var current = Ucs.FromMatrix(editor.CurrentUserCoordinateSystem);

        var modes = new[] { "preset", "name", "points", "handle" }.Where(reader.Has).ToList();
        if (reader.Has("xAxis") || reader.Has("yAxis") || reader.Has("zAxis"))
            modes.Add("xAxis/yAxis/zAxis");

        if (modes.Count > 1)
            throw Invalid($"Un seul mode de définition du SCU à la fois : {string.Join(", ", modes)}.");

        var hasOrigin = reader.Has("origin");
        var rotates = reader.Has("rotateAngle");
        if (reader.Has("rotateAxis") && !rotates)
            throw Invalid("« rotateAxis » demande un angle « rotateAngle ».");

        if (modes.Count == 0 && !hasOrigin && !rotates)
            throw Invalid("Indiquez le SCU : preset (world, top, front…), name (SCU nommé), points (3 points), " +
                          "origin avec xAxis et yAxis ou zAxis, handle (aligné sur un objet), ou une rotation " +
                          "rotateAngle du SCU courant.");

        if (hasOrigin && modes.FirstOrDefault() is "name" or "points")
            throw Invalid($"« origin » ne se combine pas avec « {modes[0]} ».");

        var mode = modes.FirstOrDefault();
        var ucs = mode switch
        {
            "preset" => Preset(reader.RequireString("preset"), reader.GetPoint("origin", Point3d.Origin)),
            "name" => FindNamed(database, transaction, reader.RequireString("name")),
            "points" => FromPoints(reader.RequirePoints("points", 3)),
            "handle" => FromEntity(context, reader),
            "xAxis/yAxis/zAxis" => FromAxes(reader, reader.GetPoint("origin", current.Origin)),
            _ => current,
        };

        // Origine seule : le SCU courant (ou celui de l'objet) est déplacé, ses axes gardés.
        if (hasOrigin && mode is null or "handle")
            ucs = ucs with { Origin = reader.RequirePoint("origin") };

        if (rotates)
            ucs = Rotate(ucs, reader.GetString("rotateAxis", "z")!, reader.RequireDouble("rotateAngle"));

        var saveAs = reader.GetString("saveAs");
        if (saveAs is not null)
            Save(database, transaction, saveAs, ucs);

        var setCurrent = reader.GetBool("setCurrent", true);
        if (!setCurrent && saveAs is null)
            throw Invalid("Avec setCurrent à faux, indiquez « saveAs » : sinon l'appel n'aurait aucun effet.");

        if (setCurrent)
            editor.CurrentUserCoordinateSystem = ucs.ToWorld;

        return new
        {
            Space = database.TileMode ? "model" : "paper",
            Current = setCurrent,
            SavedAs = saveAs,
            Ucs = ucs.Describe(),
        };
    }

    public static object DeleteUcs(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var names = reader.GetStrings("names");
        if (names.Count == 0)
            throw Invalid("Le paramètre « names » est obligatoire : noms des SCU à supprimer (voir list_ucs).");

        var transaction = context.RequireTransaction();
        var table = (UcsTable)transaction.GetObject(context.Database.UcsTableId, OpenMode.ForRead);
        var ids = names.Select(name => table.Has(name)
            ? table[name]
            : throw Invalid($"Aucun SCU nommé « {name} » dans ce dessin (voir list_ucs).")).ToList();

        foreach (var id in ids)
            transaction.GetObject(id, OpenMode.ForWrite).Erase();

        return new { Deleted = ids.Count, Names = names };
    }

    public static object ConvertUcsPoints(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var points = reader.RequirePoints("points", 1);
        if (points.Count > 10000)
            throw Invalid("Trop de points en une fois : 10 000 au maximum.");

        var from = reader.GetString("from", "current")!;
        var to = reader.GetString("to", "world")!;
        var transform = Resolve(context, to).ToWorld.Inverse() * Resolve(context, from).ToWorld;
        return new
        {
            From = from,
            To = to,
            Points = points.Select(point => Format.Point(point.TransformBy(transform))).ToList(),
        };
    }

    /// <summary>« world », « current » ou le nom d'un SCU nommé.</summary>
    private static Ucs Resolve(ToolContext context, string name) => name.ToLowerInvariant() switch
    {
        "world" => Ucs.World,
        "current" => Ucs.FromMatrix(context.Editor.CurrentUserCoordinateSystem),
        _ => FindNamed(context.Database, context.RequireTransaction(), name),
    };

    private static List<(string Name, Ucs Ucs)> ReadNamed(Database database, Transaction transaction)
    {
        var table = (UcsTable)transaction.GetObject(database.UcsTableId, OpenMode.ForRead);
        var result = new List<(string, Ucs)>();
        foreach (ObjectId id in table)
        {
            var record = (UcsTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!record.IsErased)
                result.Add((record.Name, Ucs.FromAxes(record.Origin, record.XAxis, record.YAxis, record.Name)));
        }

        return result;
    }

    private static Ucs FindNamed(Database database, Transaction transaction, string name)
    {
        var table = (UcsTable)transaction.GetObject(database.UcsTableId, OpenMode.ForRead);
        if (!table.Has(name))
            throw Invalid($"Aucun SCU nommé « {name} » dans ce dessin (voir list_ucs). Pour le SCG, indiquez " +
                          "preset = world.");

        var record = (UcsTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
        return Ucs.FromAxes(record.Origin, record.XAxis, record.YAxis, name);
    }

    private static void Save(Database database, Transaction transaction, string name, Ucs ucs)
    {
        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, false);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw Invalid($"« {name} » n'est pas un nom de SCU valide (caractères interdits : < > / \\ \" : ; ? * | , = `).");
        }

        if (name.Equals("world", StringComparison.OrdinalIgnoreCase) || name.Equals("current", StringComparison.OrdinalIgnoreCase))
            throw Invalid($"« {name} » est réservé : choisissez un autre nom de SCU.");

        var table = (UcsTable)transaction.GetObject(database.UcsTableId, OpenMode.ForRead);
        UcsTableRecord record;
        if (table.Has(name))
        {
            record = (UcsTableRecord)transaction.GetObject(table[name], OpenMode.ForWrite);
        }
        else
        {
            record = new UcsTableRecord { Name = name };
            table.UpgradeOpen();
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        record.Origin = ucs.Origin;
        record.XAxis = ucs.X;
        record.YAxis = ucs.Y;
    }

    /// <summary>SCU orthogonaux d'AutoCAD, par rapport au SCG.</summary>
    private static Ucs Preset(string preset, Point3d origin) => preset.ToLowerInvariant() switch
    {
        "world" => Ucs.World with { Origin = origin },
        "top" => new Ucs(origin, Vector3d.XAxis, Vector3d.YAxis),
        "bottom" => new Ucs(origin, Vector3d.XAxis, -Vector3d.YAxis),
        "front" => new Ucs(origin, Vector3d.XAxis, Vector3d.ZAxis),
        "back" => new Ucs(origin, -Vector3d.XAxis, Vector3d.ZAxis),
        "left" => new Ucs(origin, -Vector3d.YAxis, Vector3d.ZAxis),
        "right" => new Ucs(origin, Vector3d.YAxis, Vector3d.ZAxis),
        _ => throw Invalid($"SCU « {preset} » inconnu : world, top, bottom, front, back, left ou right."),
    };

    /// <summary>Origine, point sur l'axe X positif, point du plan XY du côté des Y positifs (commande SCU 3P).</summary>
    private static Ucs FromPoints(IReadOnlyList<Point3d> points)
    {
        if (points.Count != 3)
            throw Invalid("« points » attend exactement 3 points : origine, point sur l'axe X, point du plan XY côté Y positif.");

        return Ucs.FromAxes(points[0], points[1] - points[0], points[2] - points[0], "points");
    }

    private static Ucs FromAxes(ArgReader reader, Point3d origin)
    {
        var x = reader.Has("xAxis") ? reader.RequirePoint("xAxis") - Point3d.Origin : (Vector3d?)null;
        var y = reader.Has("yAxis") ? reader.RequirePoint("yAxis") - Point3d.Origin : (Vector3d?)null;
        var z = reader.Has("zAxis") ? reader.RequirePoint("zAxis") - Point3d.Origin : (Vector3d?)null;

        return (x, y, z) switch
        {
            ({ } vx, { } vy, null) => Ucs.FromAxes(origin, vx, vy, "xAxis, yAxis"),
            // Z est respecté, X ramené dans le plan perpendiculaire.
            ({ } vx, null, { } vz) when vz.Length > Tolerance => Ucs.FromAxes(origin,
                vx - vz.GetNormal() * vx.DotProduct(vz.GetNormal()), vz.CrossProduct(vx), "xAxis, zAxis"),
            (null, null, { } vz) when vz.Length > Tolerance => Ucs.FromZ(origin, vz),
            (null, null, { }) or ({ }, null, { }) => throw Invalid("« zAxis » est nul."),
            _ => throw Invalid("Axes attendus : xAxis et yAxis, xAxis et zAxis, ou zAxis seul (X choisi comme AutoCAD le " +
                               "fait pour un plan de normale donnée)."),
        };
    }

    /// <summary>SCU posé sur un objet plan, à la manière de l'option Objet de la commande SCU.</summary>
    private static Ucs FromEntity(ToolContext context, ArgReader reader)
    {
        var ids = Handles.Resolve(context, reader, "handle");
        if (ids.Count != 1)
            throw Invalid("« handle » désigne un seul objet.");

        var entity = context.RequireTransaction().GetObject(ids[0], OpenMode.ForRead) as Entity
            ?? throw Invalid($"L'objet {Format.Handle(ids[0])} n'est pas une entité graphique.");

        switch (entity)
        {
            case Line line:
                {
                    var direction = line.EndPoint - line.StartPoint;
                    return line.Normal.IsParallelTo(direction) ? Ucs.FromZ(line.StartPoint, direction)
                        : Ucs.FromAxes(line.StartPoint, direction, line.Normal.CrossProduct(direction), "ligne");
                }
            case Circle circle:
                return Ucs.FromZ(circle.Center, circle.Normal);
            case Arc arc:
                return Ucs.FromAxes(arc.Center, arc.StartPoint - arc.Center, arc.Normal.CrossProduct(arc.StartPoint - arc.Center), "arc");
            case Ellipse ellipse:
                return Ucs.FromAxes(ellipse.Center, ellipse.MajorAxis, ellipse.MinorAxis, "ellipse");
            case BlockReference block:
                {
                    var transform = block.BlockTransform;
                    return Ucs.FromAxes(block.Position, Vector3d.XAxis.TransformBy(transform), Vector3d.YAxis.TransformBy(transform), "bloc");
                }
            case DBText text:
                {
                    var x = ArbitraryX(text.Normal).RotateBy(text.Rotation, text.Normal.GetNormal());
                    return Ucs.FromAxes(text.Position, x, text.Normal.CrossProduct(x), "texte");
                }
            case MText mtext:
                return Ucs.FromAxes(mtext.Location, mtext.Direction, mtext.Normal.CrossProduct(mtext.Direction), "texte multiligne");
            case Curve curve when curve.IsPlanar:
                {
                    var normal = curve.GetPlane().Normal;
                    if (entity is Polyline polyline)
                        normal = polyline.Normal;

                    // Axe X le long du premier segment : tangente au départ, ramenée dans le plan de la courbe.
                    var tangent = curve.GetFirstDerivative(curve.StartParam);
                    tangent -= normal.GetNormal() * tangent.DotProduct(normal.GetNormal());
                    return tangent.Length < Tolerance ? Ucs.FromZ(curve.StartPoint, normal)
                        : Ucs.FromAxes(curve.StartPoint, tangent, normal.CrossProduct(tangent), "courbe");
                }
            default:
                throw Invalid($"Impossible d'aligner un SCU sur l'objet {Format.Handle(ids[0])} ({Format.DxfName(entity)}) : " +
                              "objets gérés : lignes, cercles, arcs, ellipses, polylignes et courbes planes, blocs, textes.");
        }
    }

    /// <summary>Rotation du SCU autour de l'un de ses propres axes, passant par son origine (règle de la main droite).</summary>
    private static Ucs Rotate(Ucs ucs, string axis, double degrees)
    {
        var vector = axis.ToLowerInvariant() switch
        {
            "x" => ucs.X,
            "y" => ucs.Y,
            "z" => ucs.Z,
            _ => throw Invalid($"« rotateAxis » vaut x, y ou z, pas « {axis} »."),
        };

        var rotation = Matrix3d.Rotation(Format.Radians(degrees), vector, ucs.Origin);
        return Ucs.FromAxes(ucs.Origin, ucs.X.TransformBy(rotation), ucs.Y.TransformBy(rotation), "rotation");
    }

    /// <summary>Algorithme d'axe arbitraire d'AutoCAD : axe X du système de coordonnées objet d'une normale donnée.</summary>
    private static Vector3d ArbitraryX(Vector3d normal)
    {
        var n = normal.GetNormal();
        var x = Math.Abs(n.X) < 1.0 / 64 && Math.Abs(n.Y) < 1.0 / 64
            ? Vector3d.YAxis.CrossProduct(n)
            : Vector3d.ZAxis.CrossProduct(n);
        return x.GetNormal();
    }

    private static PipeException Invalid(string message) => new(PipeErrorCodes.InvalidParams, message);
}

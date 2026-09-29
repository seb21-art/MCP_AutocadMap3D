using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Réseaux rectangulaires, polaires et le long d'un chemin. Les éléments sont des copies profondes indépendantes
/// (réseau non associatif) : attributs de blocs et données d'objet suivent, et chaque copie reste modifiable seule.
/// </summary>
internal static class ArrayTools
{
    private const int MaxItems = 20000;
    private const int MaxListedHandles = 500;

    public static object ArrayRectangular(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var rows = reader.GetInt("rows", 1, min: 1, max: 1000);
        var columns = reader.GetInt("columns", 1, min: 1, max: 1000);
        var levels = reader.GetInt("levels", 1, min: 1, max: 1000);
        if ((long)rows * columns * levels < 2)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le réseau doit compter au moins deux éléments (rows × columns × levels).");

        var rowSpacing = rows > 1 ? NonZero(reader, "rowSpacing") : 0;
        var columnSpacing = columns > 1 ? NonZero(reader, "columnSpacing") : 0;
        var levelSpacing = levels > 1 ? NonZero(reader, "levelSpacing") : 0;
        var angle = Format.Radians(reader.GetDouble("angle", 0));
        var sources = Handles.Resolve(context, reader);
        if ((long)rows * columns * levels > MaxItems)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Réseau trop grand : {MaxItems} éléments au maximum.");

        CheckSize(sources.Count, rows * columns * levels - 1);

        // Axes du réseau, éventuellement tournés : colonnes le long de l'angle, rangées perpendiculairement.
        var columnAxis = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
        var rowAxis = new Vector3d(-Math.Sin(angle), Math.Cos(angle), 0);

        // Niveaux empilés selon Z, pour un réseau 3D (étages, poteaux).
        var created = new List<string>();
        for (var level = 0; level < levels; level++)
        {
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (level == 0 && row == 0 && column == 0)
                        continue; // Les originaux occupent la première case.

                    var offset = columnAxis * (column * columnSpacing) + rowAxis * (row * rowSpacing) + Vector3d.ZAxis * (level * levelSpacing);
                    created.AddRange(CopyWith(context, sources, Matrix3d.Displacement(offset)));
                }
            }
        }

        return Result(created, rows * columns * levels, new
        {
            Rows = rows,
            Columns = columns,
            Levels = levels > 1 ? levels : (int?)null,
            RowSpacing = rowSpacing,
            ColumnSpacing = columnSpacing,
            LevelSpacing = levels > 1 ? levelSpacing : (double?)null,
        });
    }

    public static object ArrayPolar(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var center = reader.RequirePoint("center");
        var count = reader.Has("count")
            ? reader.GetInt("count", 2, min: 2, max: MaxItems)
            : throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « count » (nombre total d'éléments, originaux compris) est obligatoire.");
        var fillAngle = reader.GetDouble("fillAngle", 360);
        if (fillAngle == 0 || Math.Abs(fillAngle) > 360)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'angle à remplir doit être compris entre -360 et 360 degrés, hors zéro.");

        var rotateItems = reader.GetBool("rotateItems", true);
        var sources = Handles.Resolve(context, reader);
        CheckSize(sources.Count, count - 1);

        // Sur un tour complet, le dernier élément ne doit pas retomber sur le premier.
        var step = Math.Abs(Math.Abs(fillAngle) - 360) < 1e-9 ? fillAngle / count : fillAngle / (count - 1);

        // Sans rotation des éléments, chaque copie est translatée : son point de référence (centre de la sélection)
        // suit le cercle, comme avec la commande RESEAU.
        var reference = SelectionCenter(context, sources);

        var created = new List<string>();
        for (var index = 1; index < count; index++)
        {
            var rotation = Matrix3d.Rotation(Format.Radians(step * index), Vector3d.ZAxis, center);
            var matrix = rotateItems ? rotation : Matrix3d.Displacement(reference.TransformBy(rotation) - reference);
            created.AddRange(CopyWith(context, sources, matrix));
        }

        return Result(created, count, new { Count = count, FillAngle = fillAngle, AngleBetweenItems = Format.Number(step), RotateItems = rotateItems });
    }

    /// <summary>
    /// Réseau le long d'une courbe : les originaux sont le premier élément, au début du chemin ; chaque copie garde
    /// le même décalage par rapport au chemin, à sa distance le long de celui-ci, et tourne avec sa tangente vue de
    /// dessus (align), comme la commande RESEAUCHEMIN.
    /// </summary>
    public static object ArrayPath(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var sources = Handles.Resolve(context, reader);
        var pathId = Handles.Resolve(context, reader, "path").Single();
        if (sources.Contains(pathId))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le chemin ne peut pas faire partie des objets copiés.");

        var path = transaction.GetObject(pathId, OpenMode.ForRead) as Curve
            ?? throw new PipeException(PipeErrorCodes.InvalidParams, $"Le chemin {pathId.Handle} n'est pas une courbe.");
        if (path is Xline or Ray)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le chemin doit être une courbe finie, pas une droite infinie.");

        var length = path.GetDistanceAtParameter(path.EndParam);
        if (length <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le chemin {pathId.Handle} est de longueur nulle.");

        // Sur un chemin fermé, le dernier élément ne doit pas retomber sur le premier.
        var closed = path.Closed;
        var (count, spacing) = ReadPathLayout(reader, length, closed);
        var align = reader.GetBool("align", true);
        CheckSize(sources.Count, count - 1);

        var start = path.StartPoint;
        var startAngle = PlanAngle(path, 0);
        var created = new List<string>();
        for (var index = 1; index < count; index++)
        {
            var distance = Math.Min(index * spacing, length);
            var point = path.GetPointAtDist(distance);
            var matrix = Matrix3d.Displacement(point - start);
            if (align)
                matrix = Matrix3d.Rotation(PlanAngle(path, distance) - startAngle, Vector3d.ZAxis, point) * matrix;

            created.AddRange(CopyWith(context, sources, matrix));
        }

        return Result(created, count, new
        {
            Count = count,
            Spacing = Format.Number(spacing),
            PathLength = Format.Number(length),
            PathClosed = closed,
            Aligned = align,
        });
    }

    /// <summary>Nombre d'éléments et espacement : l'un déduit de l'autre et de la longueur du chemin, ou les deux.</summary>
    private static (int Count, double Spacing) ReadPathLayout(ArgReader reader, double length, bool closed)
    {
        var hasCount = reader.Has("count");
        var hasSpacing = reader.Has("spacing");
        if (!hasCount && !hasSpacing)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Indiquez « count » (nombre d'éléments, originaux compris) et/ou « spacing » (distance entre éléments le long du chemin).");

        var spacing = hasSpacing ? reader.RequireDouble("spacing") : 0;
        if (hasSpacing && spacing <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "L'espacement doit être strictement positif.");

        if (hasCount)
        {
            var count = reader.GetInt("count", 2, min: 2, max: MaxItems);
            var intervals = closed ? count : count - 1;
            if (!hasSpacing)
                return (count, length / intervals);

            if (intervals * spacing > length * (1 + 1e-9))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"{count} éléments espacés de {spacing} demandent {Format.Number(intervals * spacing)} le long du chemin, " +
                    $"qui mesure {Format.Number(length)}.");

            return (count, spacing);
        }

        var fitted = closed
            ? (long)Math.Ceiling(length / spacing - 1e-9)
            : (long)Math.Floor(length / spacing + 1e-9) + 1;
        if (fitted < 2)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"L'espacement {spacing} dépasse la longueur du chemin ({Format.Number(length)}) : un seul élément tiendrait.");

        return fitted <= MaxItems
            ? ((int)fitted, spacing)
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Réseau trop grand : {MaxItems} éléments au maximum.");
    }

    /// <summary>Direction de la tangente vue de dessus, en radians, à une distance le long de la courbe.</summary>
    private static double PlanAngle(Curve path, double distance)
    {
        var derivative = path.GetFirstDerivative(path.GetParameterAtDistance(distance));
        return Math.Atan2(derivative.Y, derivative.X);
    }

    private static List<string> CopyWith(ToolContext context, IReadOnlyList<ObjectId> sources, Matrix3d matrix)
    {
        var transaction = context.RequireTransaction();
        var handles = new List<string>();
        foreach (var (_, clone) in Cloning.DeepClone(context, sources))
        {
            var entity = (Entity)transaction.GetObject(clone, OpenMode.ForWrite);
            entity.TransformBy(matrix);
            handles.Add(entity.Handle.ToString());
        }

        return handles;
    }

    private static Point3d SelectionCenter(ToolContext context, IReadOnlyList<ObjectId> sources)
    {
        var transaction = context.RequireTransaction();
        Extents3d? all = null;
        foreach (var id in sources)
        {
            if (Format.TryGetExtents((Entity)transaction.GetObject(id, OpenMode.ForRead)) is not Extents3d extents)
                continue;

            if (all is Extents3d current)
            {
                current.AddExtents(extents);
                all = current;
            }
            else
            {
                all = extents;
            }
        }

        return all is Extents3d box
            ? new Point3d((box.MinPoint.X + box.MaxPoint.X) / 2, (box.MinPoint.Y + box.MaxPoint.Y) / 2, (box.MinPoint.Z + box.MaxPoint.Z) / 2)
            : Point3d.Origin;
    }

    private static object Result(List<string> created, int items, object layout) => new
    {
        Items = items,
        Created = created.Count,
        Layout = layout,
        Handles = created.Take(MaxListedHandles).ToArray(),
        HandlesTruncated = created.Count > MaxListedHandles ? true : (bool?)null,
    };

    private static void CheckSize(int sources, int copies)
    {
        if ((long)sources * copies > MaxItems)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Réseau trop grand : {sources} objet(s) × {copies} copie(s), {MaxItems} objets créés au maximum.");
    }

    private static double NonZero(ArgReader reader, string name)
    {
        var value = reader.RequireDouble(name);
        return value != 0
            ? value
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » ne peut pas être nul.");
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Sélection courante de l'utilisateur, filtres par propriété, attribut de bloc ou donnée d'objet, et métrés
/// (nombres, longueurs, aires et sommes regroupés par valeur). Lecture seule.
/// </summary>
internal static class QueryTools
{
    private const int MaxSelection = 1000;
    private const int MaxGroups = 500;
    private const int MaxGroupFields = 3;
    private const int MaxSumFields = 5;

    public static object GetSelection(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var limit = reader.GetInt("limit", 200, min: 1, max: MaxSelection);
        var includeGeometry = reader.GetBool("includeGeometry", false);
        var transaction = context.RequireTransaction();
        var ids = SelectedIds(context);
        var entities = ids.Take(limit)
            .Select(id => transaction.GetObject(id, OpenMode.ForRead))
            .OfType<Entity>()
            .Select(entity => ReadTools.Describe(entity, includeGeometry, transaction))
            .ToList();
        return new
        {
            Count = ids.Count,
            Truncated = ids.Count > limit ? true : (bool?)null,
            Entities = entities,
            Note = ids.Count == 0 ? NoSelectionNote() : null,
        };
    }

    /// <summary>
    /// Métré : nombre d'objets, longueur des courbes, aire des contours fermés, hachures et régions, et sommes de
    /// champs numériques, au total et par groupe de valeurs.
    /// </summary>
    public static object SummarizeEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var filter = EntityFilter.Create(context, reader, transaction);
        var groupBy = ParseFields(reader, "groupBy", MaxGroupFields);
        var sums = ParseFields(reader, "sum", MaxSumFields);
        var fields = filter.Fields;

        var total = new Totals(sums.Count);
        var groups = new Dictionary<string, (object?[] Key, Totals Totals)>(StringComparer.Ordinal);
        foreach (var entity in Source(context, reader, transaction))
        {
            if (!filter.IsMatch(entity))
                continue;

            var length = EntityFields.Length(entity);
            var area = EntityFields.Area(entity);
            var values = sums.Select(field => ToNumber(fields.Get(entity, field))).ToArray();
            total.Add(length, area, values);
            if (groupBy.Count == 0)
                continue;

            var key = groupBy.Select(field => fields.Get(entity, field)).ToArray();
            var text = string.Join("\u001f", key.Select(value => value is null ? "\u0000" : Text(value)));
            if (!groups.TryGetValue(text, out var group))
                groups[text] = group = (key, new Totals(sums.Count));
            group.Totals.Add(length, area, values);
        }

        var ordered = groups.Values
            .OrderBy(group => group.Key, KeyComparer.Instance)
            .ToList();
        return new
        {
            Units = context.Database.Insunits.ToString(),
            Count = total.Count,
            Length = total.Length,
            Area = total.Area,
            Sums = total.Sums(sums),
            GroupCount = groupBy.Count > 0 ? ordered.Count : (int?)null,
            GroupsTruncated = ordered.Count > MaxGroups ? true : (bool?)null,
            Groups = groupBy.Count == 0 ? null : ordered.Take(MaxGroups).Select(group => new
            {
                Key = groupBy.Select((field, index) => (field.Name, Value: group.Key[index]))
                    .ToDictionary(item => item.Name, item => item.Value),
                group.Totals.Count,
                group.Totals.Length,
                group.Totals.Area,
                Sums = group.Totals.Sums(sums),
            }).ToList(),
        };
    }

    /// <summary>Objets désignés par handles, objets de la sélection courante, ou objets d'un espace.</summary>
    internal static IEnumerable<Entity> Source(ToolContext context, ArgReader reader, Transaction transaction)
    {
        var selection = reader.GetBool("selection", false);
        if (selection && reader.Has("handles"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez handles ou selection, pas les deux.");

        IEnumerable<ObjectId> ids = reader.Has("handles") ? Handles.Resolve(context, reader)
            : selection ? SelectedIds(context)
            : ReadTools.OpenSpace(context, reader.GetString("space", "model")!, transaction).Cast<ObjectId>();
        foreach (var id in ids)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity)
                yield return entity;
        }
    }

    internal static IReadOnlyList<ObjectId> SelectedIds(ToolContext context)
    {
        var implied = context.Editor.SelectImplied();
        return implied.Status == PromptStatus.OK && implied.Value is not null ? implied.Value.GetObjectIds() : [];
    }

    private static string NoSelectionNote()
    {
        var pickFirst = Convert.ToInt32(AcApp.GetSystemVariable("PICKFIRST"));
        return pickFirst == 0
            ? "La variable PICKFIRST vaut 0 : AutoCAD ne garde pas les objets sélectionnés hors commande. " +
              "Demandez à l'utilisateur de la mettre à 1, puis de sélectionner les objets."
            : "Aucun objet sélectionné : demandez à l'utilisateur de sélectionner les objets dans AutoCAD, sans " +
              "commande en cours, puis rappelez l'outil.";
    }

    internal static IReadOnlyList<EntityField> ParseFields(ArgReader reader, string name, int maximum)
    {
        var fields = reader.GetStrings(name).Select(EntityField.Parse).ToList();
        return fields.Count <= maximum
            ? fields
            : throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » accepte au plus {maximum} champs.");
    }

    internal static double? ToNumber(object? value) => value switch
    {
        null => null,
        double number => number,
        int number => number,
        long number => number,
        string text when double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    internal static string Text(object value) => value switch
    {
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        double[] point => string.Join(",", point.Select(coordinate => Text(coordinate))),
        _ => value.ToString() ?? "",
    };

    /// <summary>Cumuls d'un groupe : les longueurs et aires ne comptent que les objets qui en ont une.</summary>
    private sealed class Totals(int sumCount)
    {
        private double _length, _area;
        private int _lengthCount, _areaCount;
        private readonly double[] _sums = new double[sumCount];
        private readonly int[] _sumCounts = new int[sumCount];

        public int Count { get; private set; }

        public double? Length => _lengthCount > 0 ? Format.Number(_length) : null;

        public double? Area => _areaCount > 0 ? Format.Number(_area) : null;

        public void Add(double? length, double? area, double?[] values)
        {
            Count++;
            if (length is { } l)
                (_length, _lengthCount) = (_length + l, _lengthCount + 1);
            if (area is { } a)
                (_area, _areaCount) = (_area + a, _areaCount + 1);
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] is { } v)
                    (_sums[i], _sumCounts[i]) = (_sums[i] + v, _sumCounts[i] + 1);
            }
        }

        public Dictionary<string, double?>? Sums(IReadOnlyList<EntityField> fields) =>
            fields.Count == 0 ? null : fields.Select((field, index) => (field.Name, Value: _sumCounts[index] > 0 ? Format.Number(_sums[index]) : (double?)null))
                .ToDictionary(item => item.Name, item => item.Value);
    }

    /// <summary>Tri des groupes : valeurs numériques dans l'ordre des nombres, textes sans tenir compte de la casse.</summary>
    private sealed class KeyComparer : IComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(object?[]? x, object?[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                var result = (x[i], y[i]) switch
                {
                    (null, null) => 0,
                    (null, _) => 1,
                    (_, null) => -1,
                    var (a, b) when ToNumber(a) is { } na && ToNumber(b) is { } nb => na.CompareTo(nb),
                    var (a, b) => string.Compare(Text(a!), Text(b!), StringComparison.OrdinalIgnoreCase),
                };
                if (result != 0)
                    return result;
            }

            return 0;
        }
    }
}

/// <summary>
/// Champ d'un objet utilisable dans un filtre, un regroupement ou une somme : propriété (layer, type, color,
/// linetype, lineweight, block, text, length, area, handle), attribut de bloc (attr:ÉTIQUETTE) ou donnée d'objet
/// (od:TABLE.CHAMP).
/// </summary>
internal sealed record EntityField(string Name, string Kind, string? Table = null, string? Key = null)
{
    private static readonly string[] Properties = ["layer", "type", "color", "linetype", "lineweight", "block", "text", "length", "area", "handle"];

    public static EntityField Parse(string text)
    {
        var name = text.Trim();
        if (name.StartsWith("od:", StringComparison.OrdinalIgnoreCase))
        {
            var path = name[3..];
            var dot = path.IndexOf('.');
            return dot > 0 && dot < path.Length - 1
                ? new EntityField(name, "od", path[..dot], path[(dot + 1)..])
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"Champ « {name} » : attendu od:TABLE.CHAMP.");
        }

        if (name.StartsWith("attr:", StringComparison.OrdinalIgnoreCase))
        {
            return name.Length > 5
                ? new EntityField(name, "attr", Key: name[5..])
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"Champ « {name} » : attendu attr:ÉTIQUETTE.");
        }

        var property = Properties.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
        return property is not null
            ? new EntityField(property, property)
            : throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Champ « {name} » inconnu : {string.Join(", ", Properties)}, attr:ÉTIQUETTE ou od:TABLE.CHAMP.");
    }
}

/// <summary>Lecture des champs d'un objet ; les données d'objet ne sont lues que si un champ od: est demandé.</summary>
internal sealed class EntityFields(ToolContext context, Transaction transaction)
{
    private Func<ObjectId, string, string, object?>? _od;

    public object? Get(Entity entity, EntityField field) => field.Kind switch
    {
        "layer" => entity.Layer,
        "type" => Format.DxfName(entity),
        "color" => Format.Color(entity.Color),
        "linetype" => entity.Linetype,
        "lineweight" => EntityProperties.FormatLineWeight(entity.LineWeight),
        "block" => entity is BlockReference reference
            ? ((BlockTableRecord)transaction.GetObject(reference.DynamicBlockTableRecord, OpenMode.ForRead)).Name
            : null,
        "text" => entity switch
        {
            DBText text => text.TextString,
            MText mtext => mtext.Text,
            MLeader { MText: { } content } => content.Text,
            _ => null,
        },
        "length" => Length(entity) is { } length ? Format.Number(length) : null,
        "area" => Area(entity) is { } area ? Format.Number(area) : null,
        "handle" => entity.Handle.ToString(),
        "attr" => entity is BlockReference block
            ? BlockTools.AttributeValues(block, transaction)
                .FirstOrDefault(pair => pair.Key.Equals(field.Key, StringComparison.OrdinalIgnoreCase)).Value
            : null,
        "od" => (_od ??= MapTools.CreateOdLookup(context.Database))(entity.ObjectId, field.Table!, field.Key!),
        _ => null,
    };

    /// <summary>Longueur d'une courbe finie (les droites et demi-droites n'en ont pas).</summary>
    public static double? Length(Entity entity)
    {
        if (entity is not Curve curve || entity is Xline or Ray)
            return null;
        try
        {
            return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }

    /// <summary>Aire d'une courbe fermée, d'une hachure ou d'une région.</summary>
    public static double? Area(Entity entity)
    {
        try
        {
            return entity switch
            {
                Xline or Ray => null,
                Curve { Closed: true } curve => curve.Area,
                Hatch hatch => hatch.Area,
                Region region => region.Area,
                _ => null,
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Filtre d'objets : calques, types DXF et conditions where [{ field, op, value }] sur les champs d'EntityField,
/// toutes vérifiées (et logique).
/// </summary>
internal sealed class EntityFilter
{
    private static readonly string[] Operators = ["=", "!=", "<", "<=", ">", ">=", "like", "notlike", "exists", "missing"];

    private readonly NameFilter? _layers;
    private readonly NameFilter? _types;
    private readonly List<(EntityField Field, string Op, JsonElement Value)> _conditions;

    private EntityFilter(NameFilter? layers, NameFilter? types, List<(EntityField, string, JsonElement)> conditions, EntityFields fields)
    {
        (_layers, _types, _conditions, Fields) = (layers, types, conditions, fields);
    }

    public EntityFields Fields { get; }

    public bool HasConditions => _conditions.Count > 0;

    public static EntityFilter Create(ToolContext context, ArgReader reader, Transaction transaction)
    {
        var conditions = new List<(EntityField, string, JsonElement)>();
        if (reader.TryGet("where", out var where))
        {
            if (where.ValueKind != JsonValueKind.Array)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    "« where » attend un tableau de conditions, par exemple [{\"field\":\"od:CONDUITES.DIAMETRE\",\"op\":\">=\",\"value\":100}].");
            foreach (var condition in where.EnumerateArray())
            {
                if (condition.ValueKind != JsonValueKind.Object || !condition.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String)
                    throw new PipeException(PipeErrorCodes.InvalidParams, "Chaque condition de « where » demande « field ».");
                var op = condition.TryGetProperty("op", out var opValue) && opValue.ValueKind == JsonValueKind.String ? opValue.GetString()!.Trim().ToLowerInvariant() : "=";
                op = op switch { "==" or "eq" => "=", "<>" or "ne" => "!=", _ => op };
                if (!Operators.Contains(op))
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Opérateur « {op} » inconnu : {string.Join(", ", Operators)}.");
                var value = condition.TryGetProperty("value", out var v) ? v.Clone() : default;
                if (op is not ("exists" or "missing") && value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"La condition sur « {field.GetString()} » demande « value ».");
                conditions.Add((EntityField.Parse(field.GetString()!), op, value));
            }
        }

        return new EntityFilter(
            NameFilter.Create(reader.GetStrings("layers")),
            NameFilter.Create(reader.GetStrings("types")),
            conditions,
            new EntityFields(context, transaction));
    }

    public bool IsMatch(Entity entity)
    {
        if (_layers is not null && !_layers.IsMatch(entity.Layer))
            return false;
        if (_types is not null && !_types.IsMatch(Format.DxfName(entity)))
            return false;
        return _conditions.All(condition => Test(Fields.Get(entity, condition.Field), condition.Op, condition.Value));
    }

    private static bool Test(object? actual, string op, JsonElement value)
    {
        switch (op)
        {
            case "exists":
                return actual is not null && !(actual is string text && text.Length == 0);
            case "missing":
                return actual is null || (actual is string empty && empty.Length == 0);
            case "=":
                return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Any(item => AreEqual(actual, item)) : AreEqual(actual, value);
            case "!=":
                return value.ValueKind == JsonValueKind.Array ? !value.EnumerateArray().Any(item => AreEqual(actual, item)) : !AreEqual(actual, value);
            case "like":
            case "notlike":
                var pattern = new Regex("^" + Regex.Escape(Scalar(value)).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var matches = actual is not null && pattern.IsMatch(QueryTools.Text(actual));
                return op == "like" ? matches : !matches;
            default:
                if (QueryTools.ToNumber(actual) is not { } number || !TryNumber(value, out var limit))
                    return false;
                return op switch
                {
                    "<" => number < limit,
                    "<=" => number <= limit + Tolerance(limit),
                    ">" => number > limit,
                    _ => number >= limit - Tolerance(limit),
                };
        }
    }

    private static bool AreEqual(object? actual, JsonElement value)
    {
        if (actual is null)
            return false;
        if (TryNumber(value, out var expected) && QueryTools.ToNumber(actual) is { } number)
            return Math.Abs(number - expected) <= Tolerance(expected);
        return string.Equals(QueryTools.Text(actual), Scalar(value), StringComparison.OrdinalIgnoreCase);
    }

    private static double Tolerance(double value) => 1e-9 * Math.Max(1, Math.Abs(value));

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out number),
            JsonValueKind.String => double.TryParse(value.GetString()!.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number),
            _ => false,
        };
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"Valeur de condition inattendue : {value.GetRawText()}."),
    };
}

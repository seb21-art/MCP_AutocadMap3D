using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>Blocs : définitions, création à partir d'objets existants, insertion avec attributs.</summary>
internal static class BlockTools
{
    public static object ListBlocks(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var blocks = (BlockTable)transaction.GetObject(context.Database.BlockTableId, OpenMode.ForRead);

        var result = new List<(string Name, object Block)>();
        foreach (var id in blocks)
        {
            var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (block.IsLayout || block.IsAnonymous || block.IsFromExternalReference || block.IsDependent)
                continue;

            if (filter is not null && !filter.IsMatch(block.Name))
                continue;

            var entityCount = 0;
            var attributes = new List<object>();
            foreach (var entityId in block)
            {
                entityCount++;
                if (transaction.GetObject(entityId, OpenMode.ForRead) is AttributeDefinition definition)
                {
                    attributes.Add(new
                    {
                        definition.Tag,
                        Prompt = string.IsNullOrEmpty(definition.Prompt) ? null : definition.Prompt,
                        Default = definition.TextString,
                        Constant = definition.Constant ? true : (bool?)null,
                    });
                }
            }

            result.Add((block.Name, new
            {
                block.Name,
                Description = string.IsNullOrWhiteSpace(block.Comments) ? null : block.Comments,
                EntityCount = entityCount,
                Insertions = block.GetBlockReferenceIds(true, false).Count,
                Dynamic = block.IsDynamicBlock ? true : (bool?)null,
                Attributes = attributes.Count > 0 ? attributes : null,
            }));
        }

        return new
        {
            Count = result.Count,
            Blocks = result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => item.Block).ToList(),
        };
    }

    /// <summary>
    /// Définit un bloc à partir d'objets existants, avec des définitions d'attributs facultatives.
    /// Les objets d'origine sont remplacés par une insertion du bloc (convert), conservés (keep) ou supprimés (delete).
    /// </summary>
    public static object CreateBlock(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var name = reader.RequireString("name").Trim();
        var basePoint = reader.RequirePoint("basePoint");
        var mode = (reader.GetString("mode") ?? "convert").ToLowerInvariant();
        if (mode is not ("convert" or "keep" or "delete"))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Mode « {mode} » inconnu : convert, keep ou delete.");

        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
        }
        catch (AcException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » n'est pas un nom de bloc valide.");
        }

        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
        if (blocks.Has(name))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le bloc « {name} » existe déjà. Choisissez un autre nom (voir list_blocks).");

        var sources = Handles.Resolve(context, reader);
        var attributes = ReadAttributeDefinitions(reader, basePoint, database.Textstyle);

        var block = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
        if (reader.Has("description"))
            block.Comments = reader.GetString("description");

        var blockId = blocks.Add(block);
        transaction.AddNewlyCreatedDBObject(block, true);

        // Copie profonde des objets dans la définition, ramenés pour que le point de base devienne l'origine.
        var toOrigin = Matrix3d.Displacement(Point3d.Origin - basePoint);
        foreach (var (_, clone) in Cloning.DeepClone(context, sources, blockId))
            ((Entity)transaction.GetObject(clone, OpenMode.ForWrite)).TransformBy(toOrigin);

        foreach (var definition in attributes)
        {
            block.AppendEntity(definition);
            transaction.AddNewlyCreatedDBObject(definition, true);
        }

        string? insertedHandle = null;
        if (mode is "convert" or "delete")
        {
            foreach (var id in sources)
                EditTools.OpenForWrite(id, transaction, context).Erase();
        }

        if (mode == "convert")
        {
            var reference = Insert(context, blockId, basePoint, new Scale3d(1), 0, new Dictionary<string, string>(), reader);
            insertedHandle = reference.Handle.ToString();
        }

        return new
        {
            Block = name,
            EntityCount = sources.Count,
            Attributes = attributes.Select(attribute => attribute.Tag).ToArray(),
            Mode = mode,
            Reference = insertedHandle,
        };
    }

    public static object InsertBlock(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var name = reader.RequireString("name");
        var blocks = (BlockTable)transaction.GetObject(context.Database.BlockTableId, OpenMode.ForRead);
        if (!blocks.Has(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le bloc « {name} » n'est pas défini dans le dessin (voir list_blocks).");

        var scale = ReadScale(reader);
        var rotation = Format.Radians(reader.GetDouble("rotation", 0));
        var values = ReadAttributeValues(reader);
        var reference = Insert(context, blocks[name], reader.RequirePoint("position"), scale, rotation, values, reader);

        return new
        {
            Handle = reference.Handle.ToString(),
            Block = name,
            reference.Layer,
            Attributes = AttributeValues(reference, transaction),
        };
    }

    /// <summary>Insère une référence de bloc et crée ses attributs, avec les valeurs fournies ou les valeurs par défaut.</summary>
    private static BlockReference Insert(
        ToolContext context, ObjectId blockId, Point3d position, Scale3d scale, double rotation,
        IReadOnlyDictionary<string, string> values, ArgReader reader)
    {
        var transaction = context.RequireTransaction();
        var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
        var definitions = new List<AttributeDefinition>();
        foreach (var id in block)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition { Constant: false } definition)
                definitions.Add(definition);
        }

        var unknown = values.Keys.Where(tag => !definitions.Any(d => string.Equals(d.Tag, tag, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Attribut(s) inconnu(s) pour le bloc « {block.Name} » : {string.Join(", ", unknown)}. " +
                $"Attributs disponibles : {(definitions.Count > 0 ? string.Join(", ", definitions.Select(d => d.Tag)) : "aucun")}.");

        var reference = new BlockReference(position, blockId) { ScaleFactors = scale, Rotation = rotation };
        EditTools.Append(context, reference, reader);

        foreach (var definition in definitions)
        {
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(definition, reference.BlockTransform);
            var value = values.FirstOrDefault(pair => string.Equals(pair.Key, definition.Tag, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null)
                attribute.TextString = value;

            reference.AttributeCollection.AppendAttribute(attribute);
            transaction.AddNewlyCreatedDBObject(attribute, true);
        }

        return reference;
    }

    internal static Dictionary<string, string?> AttributeValues(BlockReference reference, Transaction transaction)
    {
        var values = new Dictionary<string, string?>();
        foreach (ObjectId id in reference.AttributeCollection)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is AttributeReference attribute)
                values[attribute.Tag] = attribute.TextString;
        }

        return values;
    }

    /// <summary>Valeurs d'attributs : objet JSON { "ÉTIQUETTE": "valeur" }.</summary>
    internal static Dictionary<string, string> ReadAttributeValues(ArgReader reader, string name = "attributes")
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!reader.TryGet(name, out var element))
            return values;

        if (element.ValueKind != JsonValueKind.Object)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » attend un objet, par exemple {{\"TITRE\": \"Plan de masse\"}}.");

        foreach (var property in element.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"La valeur de l'attribut « {property.Name} » doit être un texte."),
            };
        }

        return values;
    }

    /// <summary>
    /// Définitions d'attributs : [{ tag, prompt, default, position, height }]. Les positions sont données dans le
    /// repère du dessin et ramenées dans celui du bloc (point de base à l'origine).
    /// </summary>
    private static List<AttributeDefinition> ReadAttributeDefinitions(ArgReader reader, Point3d basePoint, ObjectId textStyle)
    {
        var definitions = new List<AttributeDefinition>();
        if (!reader.TryGet("attributes", out var element))
            return definitions;

        if (element.ValueKind != JsonValueKind.Array)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « attributes » attend un tableau de définitions.");

        foreach (var item in element.EnumerateArray())
        {
            var itemReader = new ArgReader(item);
            var tag = itemReader.RequireString("tag").Trim().ToUpperInvariant();
            if (tag.Length == 0 || tag.Contains(' '))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Étiquette d'attribut « {tag} » invalide : pas d'espace.");

            var position = itemReader.GetPoint("position", basePoint);
            var definition = new AttributeDefinition(
                position - basePoint.GetAsVector(),
                itemReader.GetString("default") ?? "",
                tag,
                itemReader.GetString("prompt") ?? tag,
                textStyle)
            {
                Height = itemReader.GetDouble("height", 2.5),
            };

            definitions.Add(definition);
        }

        return definitions;
    }

    internal static Scale3d ReadScale(ArgReader reader)
    {
        if (!reader.TryGet("scale", out var element))
            return new Scale3d(1);

        if (element.ValueKind == JsonValueKind.Number)
        {
            var factor = element.GetDouble();
            return factor != 0 ? new Scale3d(factor) : throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle ne peut pas être nulle.");
        }

        var point = reader.RequirePoint("scale");
        return new Scale3d(point.X, point.Y, point.Z == 0 ? 1 : point.Z);
    }
}

/// <summary>Copie profonde d'objets (attributs, données d'objet et dictionnaires d'extension compris).</summary>
internal static class Cloning
{
    /// <summary>
    /// Clone les objets dans <paramref name="ownerId"/> (même espace si null). Renvoie les paires source → clone.
    /// DeepCloneObjects exige que les objets d'un même appel aient le même propriétaire : on regroupe.
    /// </summary>
    public static List<(ObjectId Source, ObjectId Clone)> DeepClone(ToolContext context, IReadOnlyList<ObjectId> ids, ObjectId? ownerId = null)
    {
        var transaction = context.RequireTransaction();
        var pairs = new List<(ObjectId, ObjectId)>();
        foreach (var group in ids.GroupBy(id => transaction.GetObject(id, OpenMode.ForRead).OwnerId))
        {
            var mapping = new IdMapping();
            context.Database.DeepCloneObjects(new ObjectIdCollection(group.ToArray()), ownerId ?? group.Key, mapping, false);
            foreach (IdPair pair in mapping)
            {
                if (pair.IsPrimary && pair.IsCloned)
                    pairs.Add((pair.Key, pair.Value));
            }
        }

        return pairs;
    }
}

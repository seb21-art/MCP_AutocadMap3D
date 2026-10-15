using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Gestion des calques : renommer, supprimer, fusionner (comme FUSIONCALQUES : les objets passent sur le calque
/// cible, y compris dans les définitions de blocs, puis les calques sources sont supprimés). Une étape de U.
/// </summary>
internal static class LayerTools
{
    public static object RenameLayer(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var name = reader.RequireString("name").Trim();
        var newName = reader.RequireString("newName").Trim();
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Calque « {name} » introuvable.");

        var layer = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForRead);
        if (IsReserved(layer.Name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layer.Name} » ne peut pas être renommé.");
        if (layer.IsDependent)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {layer.Name} » vient d'une référence externe : il se renomme dans son dessin.");
        try
        {
            SymbolUtilityServices.ValidateSymbolName(newName, allowVerticalBar: false);
        }
        catch (AcException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {newName} » n'est pas un nom de calque valide.");
        }

        if (layers.Has(newName) && layers[newName] != layer.ObjectId)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le calque « {newName} » existe déjà : pour réunir les deux calques, utilisez delete_layers avec mergeInto.");

        var previous = layer.Name;
        layer.UpgradeOpen();
        layer.Name = newName;
        return new { Renamed = previous, Name = layer.Name };
    }

    /// <summary>
    /// Supprime des calques. Sans mergeInto, un calque qui porte encore des objets est refusé ; avec mergeInto, ses
    /// objets (espaces, définitions de blocs, attributs) passent d'abord sur le calque cible.
    /// </summary>
    public static object DeleteLayers(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var names = reader.GetStrings("names");
        if (names.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "names : calques à supprimer (jokers * et ? acceptés).");
        var filter = NameFilter.Create(names)!;

        ObjectId? targetId = null;
        if (reader.GetString("mergeInto")?.Trim() is { Length: > 0 } targetName)
        {
            if (!layers.Has(targetName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Calque cible « {targetName} » introuvable : créez-le avec create_layer.");
            targetId = layers[targetName];
        }

        var selected = new Dictionary<ObjectId, string>();
        var skipped = new List<object>();
        foreach (var id in layers)
        {
            var layer = (LayerTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!filter.IsMatch(layer.Name) || id == targetId)
                continue;
            string? reason = IsReserved(layer.Name) ? "calque réservé d'AutoCAD"
                : layer.IsDependent ? "calque d'une référence externe"
                : id == database.Clayer && targetId is null ? "calque courant (mergeInto le remplace comme calque courant)"
                : null;
            if (reason is null)
                selected[id] = layer.Name;
            else
                skipped.Add(new { Layer = layer.Name, Reason = reason });
        }

        if (selected.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                skipped.Count > 0 ? "Aucun des calques désignés ne peut être supprimé (calque réservé, courant ou de référence externe)."
                    : "Aucun calque ne correspond à names.");

        // Objets portés par ces calques, dans tous les blocs du dessin (hors références externes).
        var onLayers = new List<ObjectId>();
        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (var blockId in blocks)
        {
            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (block.IsFromExternalReference || block.IsDependent)
                continue;
            foreach (var id in block)
            {
                var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                if (selected.ContainsKey(entity.LayerId))
                    onLayers.Add(id);
                if (entity is BlockReference reference)
                    foreach (ObjectId attributeId in reference.AttributeCollection)
                        if (selected.ContainsKey(((Entity)transaction.GetObject(attributeId, OpenMode.ForRead)).LayerId))
                            onLayers.Add(attributeId);
            }
        }

        if (targetId is not ObjectId target)
        {
            if (onLayers.Count > 0)
            {
                var used = onLayers.Select(id => ((Entity)transaction.GetObject(id, OpenMode.ForRead)).LayerId)
                    .GroupBy(id => id).Select(group => $"{selected[group.Key]} ({group.Count()})");
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Calques encore utilisés : {string.Join(", ", used)} objet(s). Passez mergeInto pour déplacer leurs objets " +
                    "sur un autre calque, ou supprimez d'abord ces objets.");
            }
        }
        else
        {
            foreach (var id in onLayers)
            {
                // Un objet sur un calque verrouillé s'ouvre quand même : il change seulement de calque.
                var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite, false, true);
                entity.LayerId = target;
            }

            if (selected.ContainsKey(database.Clayer))
                database.Clayer = target;
        }

        // Purge dit lesquels ne sont plus référencés nulle part (fenêtres, styles, filtres…).
        var candidates = new ObjectIdCollection(selected.Keys.ToArray());
        database.Purge(candidates);
        var deleted = new List<string>();
        foreach (ObjectId id in candidates)
        {
            transaction.GetObject(id, OpenMode.ForWrite).Erase();
            deleted.Add(selected[id]);
        }

        foreach (var id in selected.Keys.Where(id => !candidates.Contains(id)))
            skipped.Add(new { Layer = selected[id], Reason = "encore référencé (fenêtre, style, référence externe…)" });

        return new
        {
            Deleted = deleted.Count,
            Layers = deleted,
            MergedInto = targetId is ObjectId mergeTarget ? ((LayerTableRecord)transaction.GetObject(mergeTarget, OpenMode.ForRead)).Name : null,
            ObjectsMoved = targetId is not null ? onLayers.Count : (int?)null,
            Skipped = skipped.Count > 0 ? skipped : null,
        };
    }

    private static bool IsReserved(string name) =>
        string.Equals(name, "0", StringComparison.Ordinal) || string.Equals(name, "Defpoints", StringComparison.OrdinalIgnoreCase);
}

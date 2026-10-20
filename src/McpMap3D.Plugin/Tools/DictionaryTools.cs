using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Parcours des dictionnaires du dessin (dictionnaire nommé, dictionnaires d'extension) : objets non graphiques
/// des applications (catalogues, réseaux, réglages). Lecture seule.
/// </summary>
internal static class DictionaryTools
{
    private const int MaxEntries = 1000;

    /// <summary>Entrées d'un dictionnaire : son énumérateur typé donne des DBDictionaryEntry (Cast donnerait des DictionaryEntry).</summary>
    internal static List<DBDictionaryEntry> Entries(DBDictionary dictionary)
    {
        var entries = new List<DBDictionaryEntry>();
        foreach (DBDictionaryEntry entry in dictionary)
            entries.Add(entry);
        return entries;
    }

    public static object ListDictionary(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;

        DBDictionary dictionary;
        string? path = null;
        if (reader.GetString("handle") is { Length: > 0 } handle)
        {
            var target = transaction.GetObject(Handles.ResolveOne(context, handle), OpenMode.ForRead);
            // Un objet qui n'est pas un dictionnaire donne accès à son dictionnaire d'extension.
            dictionary = target as DBDictionary
                ?? (!target.ExtensionDictionary.IsNull
                    ? (DBDictionary)transaction.GetObject(target.ExtensionDictionary, OpenMode.ForRead)
                    : throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"L'objet {handle} ({target.GetRXClass().DxfName}) n'est pas un dictionnaire et n'a pas de dictionnaire d'extension."));
        }
        else
        {
            path = (reader.GetString("path") ?? "").Trim().Trim('/');
            dictionary = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
            var walked = new List<string>();
            foreach (var key in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!dictionary.Contains(key))
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Clé « {key} » absente de {(walked.Count == 0 ? "la racine" : string.Join("/", walked))} : list_dictionary donne les clés.");
                var entry = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
                walked.Add(key);
                dictionary = entry as DBDictionary
                    ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"« {string.Join("/", walked)} » est un objet {entry.GetRXClass().DxfName}, pas un dictionnaire : get_entity_dxf avec son handle {entry.ObjectId.Handle} le décrit.");
            }
        }

        var offset = reader.GetInt("offset", 0, 0);
        var limit = reader.GetInt("limit", 200, 1, MaxEntries);
        var all = Entries(dictionary);
        var entries = all.Skip(offset).Take(limit).Select(entry =>
        {
            var item = transaction.GetObject(entry.Value, OpenMode.ForRead, true);
            var rxClass = item.GetRXClass();
            return new
            {
                entry.Key,
                Handle = entry.Value.Handle.ToString(),
                Type = rxClass.DxfName,
                ClassName = rxClass.Name,
                Entries = item is DBDictionary child ? child.Count : (int?)null,
                Erased = item.IsErased ? true : (bool?)null,
            };
        }).ToList();

        var owner = dictionary.OwnerId;
        return new
        {
            Path = path,
            Handle = dictionary.ObjectId.Handle.ToString(),
            Type = dictionary.GetRXClass().DxfName,
            Owner = owner.IsNull ? null : owner.Handle.ToString(),
            Total = all.Count,
            Count = entries.Count,
            Offset = offset,
            Entries = entries,
        };
    }
}

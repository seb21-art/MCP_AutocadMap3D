using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpMap3D.Server;

/// <summary>
/// Allège les schémas de paramètres que le SDK génère à partir des signatures C#, relus par le modèle à chaque
/// conversation. Un paramètre nullable (<c>string? layer = null</c>) y figure comme
/// <c>"type":["string","null"],"default":null</c> : ne pas être dans « required » suffit déjà à le rendre facultatif.
/// On garde <c>"type":"string"</c> et les valeurs par défaut non nulles. La liaison des arguments ne dépend pas du
/// schéma : un paramètre absent ou envoyé à null vaut toujours null.
/// </summary>
internal static class ToolSchemaCompactor
{
    public static JsonElement Compact(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        Compact(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Compact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types && types.Any(IsNull))
                {
                    var kept = types.Where(type => !IsNull(type)).Select(type => type!.DeepClone()).ToArray();
                    obj["type"] = kept.Length == 1 ? kept[0] : new JsonArray(kept);
                }

                if (obj.TryGetPropertyValue("default", out var value) && value is null)
                    obj.Remove("default");

                foreach (var (_, child) in obj.ToArray())
                    Compact(child);

                break;

            case JsonArray array:
                foreach (var child in array)
                    Compact(child);

                break;
        }
    }

    private static bool IsNull(JsonNode? type) =>
        type is JsonValue value && value.TryGetValue<string>(out var name) && name == "null";
}

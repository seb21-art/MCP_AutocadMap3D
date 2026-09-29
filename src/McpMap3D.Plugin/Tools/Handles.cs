using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>Conversion des handles fournis par Claude en identifiants d'objets du dessin.</summary>
internal static class Handles
{
    private const int Maximum = 5000;

    public static IReadOnlyList<ObjectId> Resolve(ToolContext context, ArgReader reader, string parameterName = "handles")
    {
        var handles = reader.GetStrings(parameterName);
        if (handles.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le paramètre « {parameterName} » est obligatoire : indiquez les handles renvoyés par list_entities.");

        if (handles.Count > Maximum)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Trop d'objets en une fois ({handles.Count}) : {Maximum} au maximum.");

        var database = context.Database;
        var ids = new List<ObjectId>(handles.Count);
        foreach (var text in handles)
        {
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"« {text} » n'est pas un handle valide : attendu un nombre hexadécimal, par exemple 52C0.");

            if (!database.TryGetObjectId(new Handle(value), out var id) || id.IsNull)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucun objet de handle {text} dans ce dessin.");

            if (id.IsErased)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {text} est déjà supprimé.");

            ids.Add(id);
        }

        return ids;
    }
}

using System.Text.Json;
using McpMap3D.Shared;

namespace McpMap3D.Plugin;

/// <summary>
/// Réglages explicites de l'utilisateur pour les outils sensibles, désactivés par défaut : fichier
/// %USERPROFILE%\.mcpmap3d\settings.json (relu à chaque appel, sans redémarrer AutoCAD) ou variable d'environnement
/// lue au lancement d'AutoCAD.
/// </summary>
internal static class PluginSettings
{
    public static string FilePath => Path.Combine(InstallPaths.Root, "settings.json");

    /// <summary>set_entity_dxf : écriture de codes DXF bruts par acdbEntMod.</summary>
    public static bool SetEntityDxfEnabled =>
        Environment.GetEnvironmentVariable("MCPMAP3D_ENABLE_SET_ENTITY_DXF") == "1" || ReadFlag("enableSetEntityDxf");

    private static bool ReadFlag(string name)
    {
        try
        {
            if (!File.Exists(FilePath))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Info($"Réglages illisibles ({FilePath}) : {ex.Message}");
            return false;
        }
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpMap3D.Server;

/// <summary>
/// Ajoute ou retire ce serveur dans claude_desktop_config.json, en conservant le reste du fichier
/// et en créant une copie de sauvegarde avant toute modification.
/// </summary>
internal static class ClaudeDesktopConfig
{
    private const string FileName = "claude_desktop_config.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Register(string serverName, string executable)
    {
        var entry = new JsonObject { ["command"] = executable, ["args"] = new JsonArray() };
        var paths = FindConfigFiles();
        if (paths.Count == 0)
            paths.Add(DefaultConfigFile());

        foreach (var path in paths)
        {
            var root = Load(path);
            var servers = root["mcpServers"] as JsonObject;
            if (servers is null)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            if (servers[serverName]?.ToJsonString() == entry.ToJsonString())
            {
                Console.WriteLine($"Déjà enregistré : {path}");
                continue;
            }

            servers[serverName] = entry.DeepClone();
            Save(path, root);
            Console.WriteLine($"Enregistré « {serverName} » dans {path}");
        }

        return 0;
    }

    public static int Unregister(string serverName)
    {
        foreach (var path in FindConfigFiles())
        {
            var root = Load(path);
            if (root["mcpServers"] is not JsonObject servers || !servers.Remove(serverName))
                continue;

            Save(path, root);
            Console.WriteLine($"Retiré « {serverName} » de {path}");
        }

        return 0;
    }

    /// <summary>
    /// Claude Desktop installé depuis le Microsoft Store (MSIX) lit sa configuration dans le dossier
    /// virtualisé du paquet ; l'installation classique, dans %APPDATA%\Claude. On traite les deux.
    /// </summary>
    private static List<string> FindConfigFiles()
    {
        var candidates = new List<string>();
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            candidates.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                .Select(dir => Path.Combine(dir, "LocalCache", "Roaming", "Claude", FileName)));
        }

        candidates.Add(DefaultConfigFile());
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DefaultConfigFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", FileName);

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        var options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: options) as JsonObject
            ?? throw new InvalidDataException($"{path} ne contient pas un objet JSON.");
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Copy(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: true);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
}

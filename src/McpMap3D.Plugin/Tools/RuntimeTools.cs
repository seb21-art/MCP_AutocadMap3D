using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Inventaire de l'exécution d'AutoCAD : classes enregistrées (objets personnalisés, proxys) et modules chargés
/// (ARX, DBX, assemblages .NET). Lecture seule.
/// </summary>
internal static class RuntimeTools
{
    private const long MaxScannedHandles = 5_000_000;

    private sealed record ClassInfo(string Name, string? DxfName, string? AppName, string? Parent, int ProxyFlags, int Instances, int? ProxyInstances);

    private sealed record ModuleInfo(string Kind, string Name, string? Path, string? FileVersion, string? AssemblyVersion, string? Company, string? Product);

    /// <summary>
    /// Classes du dictionnaire des classes d'AutoCAD, avec le nombre d'objets de chacune dans le dessin actif (un
    /// objet proxy compte pour la classe d'origine, sous proxyInstances).
    /// </summary>
    public static object ListClasses(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var prefixes = reader.GetStrings("prefix");
        var usedOnly = reader.GetBool("usedOnly", false);
        var database = context.Database;
        var transaction = context.RequireTransaction();

        // Nombre d'objets par classe : parcours des handles du dessin, sans ouvrir les objets (sauf les proxys).
        var instances = new Dictionary<string, int>(StringComparer.Ordinal);
        var proxies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seed = database.Handseed.Value;
        var scanned = Math.Min(seed, MaxScannedHandles);
        for (long value = 1; value < scanned; value++)
        {
            if (!database.TryGetObjectId(new Handle(value), out var id) || id.IsNull || id.IsErased)
                continue;
            var name = id.ObjectClass.Name;
            instances[name] = instances.TryGetValue(name, out var count) ? count + 1 : 1;
            if (name is "AcDbProxyEntity" or "AcDbProxyObject")
            {
                var original = transaction.GetObject(id, OpenMode.ForRead) switch
                {
                    ProxyEntity entity => entity.OriginalClassName,
                    ProxyObject proxy => proxy.OriginalClassName,
                    _ => null,
                };
                if (!string.IsNullOrEmpty(original))
                    proxies[original] = proxies.TryGetValue(original, out var proxyCount) ? proxyCount + 1 : 1;
            }
        }

        bool Matches(string? text) => prefixes.Count == 0
            || (text is not null && prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        var classes = new List<ClassInfo>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in SystemObjects.ClassDictionary)
        {
            if (entry.Value is not RXClass rxClass)
                continue;
            known.Add(rxClass.Name);
            if (!Matches(rxClass.Name) && !Matches(rxClass.DxfName))
                continue;
            var count = instances.GetValueOrDefault(rxClass.Name);
            var proxyCount = proxies.GetValueOrDefault(rxClass.Name);
            if (usedOnly && count == 0 && proxyCount == 0)
                continue;
            classes.Add(new ClassInfo(
                rxClass.Name,
                string.IsNullOrEmpty(rxClass.DxfName) ? null : rxClass.DxfName,
                string.IsNullOrEmpty(rxClass.AppName) ? null : rxClass.AppName,
                rxClass.MyParent?.Name,
                rxClass.ProxyFlags,
                count,
                proxyCount > 0 ? proxyCount : null));
        }

        // Classes connues seulement par leurs proxys : l'application qui les définit n'est pas chargée.
        var unloaded = proxies.Where(item => !known.Contains(item.Key) && Matches(item.Key))
            .Select(item => new { Name = item.Key, ProxyInstances = item.Value }).ToList();

        return new
        {
            Count = classes.Count,
            Classes = classes.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ProxyOnlyClasses = unloaded.Count > 0 ? unloaded : null,
            HandlesScanned = scanned,
            Note = seed > MaxScannedHandles ? $"Comptage limité aux {MaxScannedHandles} premiers handles (HANDSEED = {seed})." : null,
        };
    }

    /// <summary>Modules ARX/DBX chargés par AutoCAD et assemblages .NET du processus, avec version et éditeur.</summary>
    public static object ListLoadedModules(ToolContext context, JsonElement? args)
    {
        _ = context;
        var reader = new ArgReader(args);
        var kind = reader.GetString("kind", "all")!.Trim().ToLowerInvariant();
        if (kind is not ("all" or "arx" or "dotnet"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "kind : all (défaut), arx ou dotnet.");
        var filter = NameFilter.Create(reader.GetStrings("names"));

        // Chemins complets des modules natifs du processus, par nom de fichier.
        var native = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            native.TryAdd(Path.GetFileName(module.FileName), module.FileName);

        var modules = new List<ModuleInfo>();
        if (kind is "all" or "arx")
        {
            foreach (var loaded in SystemObjects.DynamicLinker.GetLoadedModules())
            {
                if (string.IsNullOrEmpty(loaded))
                    continue;
                var file = Path.GetFileName(loaded);
                if (filter is not null && !filter.IsMatch(file))
                    continue;
                var path = Path.IsPathRooted(loaded) ? loaded : native.GetValueOrDefault(file);
                modules.Add(Describe("arx", file, path, null));
            }
        }

        if (kind is "all" or "dotnet")
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;
                var name = assembly.GetName();
                var path = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
                var file = path is null ? name.Name ?? "?" : Path.GetFileName(path);
                if (filter is not null && !filter.IsMatch(file) && !filter.IsMatch(name.Name ?? ""))
                    continue;
                modules.Add(Describe("dotnet", file, path, name.Version?.ToString()));
            }
        }

        var sorted = modules.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new { Count = sorted.Count, Modules = sorted };
    }

    private static ModuleInfo Describe(string kind, string name, string? path, string? assemblyVersion)
    {
        FileVersionInfo? info = null;
        if (path is not null && File.Exists(path))
        {
            try
            {
                info = FileVersionInfo.GetVersionInfo(path);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return new ModuleInfo(
            kind,
            name,
            path,
            string.IsNullOrWhiteSpace(info?.FileVersion) ? null : info!.FileVersion,
            assemblyVersion,
            string.IsNullOrWhiteSpace(info?.CompanyName) ? null : info!.CompanyName,
            string.IsNullOrWhiteSpace(info?.ProductName) ? null : info!.ProductName);
    }
}

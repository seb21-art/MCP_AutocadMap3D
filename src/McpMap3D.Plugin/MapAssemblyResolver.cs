using System.Reflection;

namespace McpMap3D.Plugin;

/// <summary>
/// Les DLL de Map 3D ne sont pas toutes chargées au démarrage, et leurs dossiers ne font pas partie des chemins
/// de recherche d'AutoCAD : si une référence du plug-in n'est pas trouvée, on la cherche dans ces dossiers.
/// </summary>
internal static class MapAssemblyResolver
{
    private static readonly string[] Folders = BuildFolders();

    public static void Register() => AppDomain.CurrentDomain.AssemblyResolve += Resolve;

    public static void Unregister() => AppDomain.CurrentDomain.AssemblyResolve -= Resolve;

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is null)
            return null;

        foreach (var folder in Folders)
        {
            var path = Path.Combine(folder, name + ".dll");
            if (File.Exists(path))
                return Assembly.LoadFrom(path);
        }

        return null;
    }

    private static string[] BuildFolders()
    {
        // Dossier d'AutoCAD, déduit de l'emplacement d'une DLL qu'il a forcément chargée.
        var acadDir = Path.GetDirectoryName(typeof(Autodesk.AutoCAD.DatabaseServices.Database).Assembly.Location)!;
        return [Path.Combine(acadDir, "Map"), Path.Combine(acadDir, "Map", "bin", "GisPlatform")];
    }
}

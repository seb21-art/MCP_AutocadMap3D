using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

/// <summary>
/// Diagnostic : journalise les commandes et expressions LISP exécutées par AutoCAD, pour comprendre les étapes
/// d'annulation créées par d'autres applications (Map 3D, Covadis…). Désactivé sauf si la variable
/// d'environnement MCPMAP3D_TRACE_COMMANDS vaut 1 au lancement d'AutoCAD.
/// </summary>
internal static class CommandTrace
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("MCPMAP3D_TRACE_COMMANDS") == "1";

    public static void Start()
    {
        if (!Enabled)
            return;

        foreach (Document document in AcApp.DocumentManager)
            Attach(document);

        AcApp.DocumentManager.DocumentCreated += (_, e) => Attach(e.Document);
        Log.Info("[trace] journalisation des commandes activée");
    }

    public static void Note(string message)
    {
        if (Enabled)
            Log.Info($"[trace] {message}");
    }

    private static void Attach(Document document)
    {
        var name = Path.GetFileName(document.Name);
        document.CommandWillStart += (_, e) => Log.Info($"[trace] {name} : début de {e.GlobalCommandName}");
        document.CommandEnded += (_, e) => Log.Info($"[trace] {name} : fin de {e.GlobalCommandName}");
        document.CommandCancelled += (_, e) => Log.Info($"[trace] {name} : annulation de {e.GlobalCommandName}");
        document.LispWillStart += (_, e) => Log.Info($"[trace] {name} : LISP {e.FirstLine}");
    }
}

using Autodesk.AutoCAD.Runtime;
using McpMap3D.Plugin;
using McpMap3D.Plugin.Tools;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: ExtensionApplication(typeof(PluginApp))]
[assembly: CommandClass(typeof(Commands))]

namespace McpMap3D.Plugin;

/// <summary>Point d'entrée chargé par AutoCAD : démarre la file du thread principal et le serveur pipe.</summary>
public sealed class PluginApp : IExtensionApplication
{
    internal static string Version { get; } = typeof(PluginApp).Assembly.GetName().Version?.ToString(3) ?? "?";

    internal static MainThreadDispatcher? Dispatcher { get; private set; }

    internal static PipeServer? Server { get; private set; }

    /// <summary>Langue du produit AutoCAD (LCID), lue au chargement sur le thread principal ; anglais par défaut.</summary>
    internal static int ProductLcid { get; private set; } = 1033;

    public void Initialize()
    {
        try
        {
            PluginStats.MarkLoaded();
            MapAssemblyResolver.Register();
            Log.Info($"Chargement du plug-in {Version} dans AutoCAD {AcApp.Version}");
            ProductLcid = ReadProductLcid();
            Dispatcher = new MainThreadDispatcher();
            Server = new PipeServer(new RequestProcessor(ToolRegistry.CreateDefault(), Dispatcher));
            Server.Start();
            CommandTrace.Start();

            WriteMessage(Server.State == PipeServerState.Listening
                ? $"\nMCP Map 3D {Version} chargé. Tapez MCPMAP_STATUS pour le diagnostic.\n"
                : $"\nMCP Map 3D {Version} chargé, mais le pipe est déjà servi par une autre instance d'AutoCAD.\n");
        }
        catch (Exception ex)
        {
            Log.Error("Échec de l'initialisation", ex);
            WriteMessage($"\nMCP Map 3D : échec de l'initialisation ({ex.Message}). Détails : {Log.FilePath}\n");
        }
    }

    public void Terminate()
    {
        try
        {
            Server?.Dispose();
            Dispatcher?.Dispose();
            MapAssemblyResolver.Unregister();
            Log.Info("Plug-in arrêté");
        }
        catch (Exception ex)
        {
            Log.Error("Erreur à l'arrêt", ex);
        }
    }

    private static int ReadProductLcid()
    {
        try
        {
            return SystemObjects.DynamicLinker.ProductLcid;
        }
        catch (Exception ex)
        {
            Log.Error("Langue du produit illisible", ex);
            return 1033;
        }
    }

    private static void WriteMessage(string message) =>
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
}

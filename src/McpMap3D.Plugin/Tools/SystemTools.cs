using System.Runtime.InteropServices;
using System.Text.Json;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

internal static class SystemTools
{
    /// <summary>
    /// Diagnostic : met la commande U d'AutoCAD dans la file d'exécution. Sert aux tests d'annulation
    /// et n'est pas exposé comme outil MCP.
    /// </summary>
    public static object? Undo(ToolContext context, JsonElement? args)
    {
        var count = new ArgReader(args).GetInt("count", 1, min: 1, max: 20);
        var doc = context.RequireDocument();
        doc.SendStringToExecute(string.Concat(Enumerable.Repeat("_.U ", count)), true, false, false);
        return new { Queued = count };
    }

    public static object? Ping(ToolContext context, JsonElement? args)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        return new
        {
            Pong = true,
            Plugin = PluginApp.Version,
            Protocol = PipeProtocol.Version,
            AutoCad = AcApp.Version.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            MapApi = MapInfo.Describe(),
            ActiveDocument = doc?.Name,
            ServerTime = DateTimeOffset.Now,
        };
    }
}

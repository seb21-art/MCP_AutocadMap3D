using System.Runtime.InteropServices;
using System.Text;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

public sealed class Commands
{
    /// <summary>
    /// Diagnostic du connecteur. Transparente ('MCPMAP_STATUS) pour pouvoir diagnostiquer
    /// un blocage pendant une commande, et sans marque d'annulation.
    /// </summary>
    [CommandMethod("MCPMAP", "MCPMAP_STATUS", CommandFlags.Transparent | CommandFlags.NoUndoMarker)]
    public static void Status()
    {
        var editor = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (editor is null)
            return;

        var s = StatusReport.Capture();
        var idle = s.LastIdleAt is null
            ? "jamais reçu"
            : $"{s.IdleEvents} reçu(s), dernier à {s.LastIdleAt:HH:mm:ss}";

        var text = new StringBuilder()
            .AppendLine()
            .AppendLine("=== MCP Map 3D : diagnostic ===")
            .AppendLine($"Plug-in         : {s.Plugin} ({typeof(Commands).Assembly.Location}), chargé à {s.LoadedAt:HH:mm:ss}")
            .AppendLine($"AutoCAD         : {AcApp.Version} / {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"API Map 3D      : {MapInfo.Describe()}")
            .AppendLine($"Pipe            : {s.Pipe}")
            .AppendLine($"Serveur         : {s.Server}")
            .AppendLine($"Clients         : {s.ConnectedClients} connecté(s)")
            .AppendLine($"Requêtes        : {s.Requests} traitée(s), {s.Failures} en échec, {s.Pending} en attente" +
                        (s.Pending > 0 && s.BusyReason is not null ? $" ({s.BusyReason})" : ""))
            .AppendLine($"Dernière requête: {s.LastRequest ?? "aucune"}")
            .AppendLine($"Dernière erreur : {s.LastError ?? "aucune"}")
            .AppendLine($"Événements Idle : {idle}, fenêtre {s.MainWindow}")
            .AppendLine($"Journal         : {s.LogFile}");

        editor.WriteMessage(text.ToString());
    }
}

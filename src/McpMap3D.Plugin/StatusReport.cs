using McpMap3D.Shared;

namespace McpMap3D.Plugin;

/// <summary>
/// État du connecteur, lisible depuis n'importe quel thread (aucun objet AutoCAD) :
/// sert à MCPMAP_STATUS et à la méthode « status » du pipe, qui répond même si AutoCAD est occupé.
/// </summary>
internal sealed record StatusReport(
    string Plugin,
    string Pipe,
    string Server,
    DateTime LoadedAt,
    int ConnectedClients,
    long Requests,
    long Failures,
    int Pending,
    string? LastRequest,
    string? LastError,
    long IdleEvents,
    DateTime? LastIdleAt,
    string? BusyReason,
    string MainWindow,
    string LogFile)
{
    public static StatusReport Capture()
    {
        var server = PluginApp.Server;
        var dispatcher = PluginApp.Dispatcher;
        var state = server is null ? "non initialisé (voir le journal)" : server.State switch
        {
            PipeServerState.Listening => $"en écoute depuis {server.ListeningSince:HH:mm:ss}",
            PipeServerState.AnotherInstanceActive => "inactif : une autre instance d'AutoCAD sert déjà le pipe",
            PipeServerState.Faulted => "en panne (voir la dernière erreur)",
            PipeServerState.Stopped => "arrêté",
            _ => "non démarré",
        };

        return new StatusReport(
            Plugin: PluginApp.Version,
            Pipe: $@"\\.\pipe\{PipeProtocol.PipeName}",
            Server: state,
            LoadedAt: PluginStats.LoadedAt,
            ConnectedClients: PluginStats.ConnectedClients,
            Requests: PluginStats.Requests,
            Failures: PluginStats.Failures,
            Pending: dispatcher?.PendingCount ?? 0,
            LastRequest: PluginStats.LastRequest,
            LastError: PluginStats.LastError,
            IdleEvents: dispatcher?.IdleEvents ?? 0,
            LastIdleAt: dispatcher?.LastIdleAt,
            BusyReason: dispatcher?.BusyReason,
            MainWindow: $"0x{(dispatcher?.MainWindow ?? IntPtr.Zero):X}",
            LogFile: Log.FilePath);
    }
}

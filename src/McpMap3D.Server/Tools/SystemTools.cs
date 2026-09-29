using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

[McpServerToolType]
public sealed class SystemTools(PluginClient plugin)
{
    [McpServerTool(Name = "ping", Title = "Tester la liaison avec AutoCAD Map 3D",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Vérifie qu'AutoCAD Map 3D est lancé, que le plug-in MCP Map 3D est chargé et que le thread " +
                 "principal d'AutoCAD est disponible. Renvoie les versions (plug-in, AutoCAD, .NET), la disponibilité " +
                 "de l'API Map 3D et le nom du dessin actif.")]
    public Task<string> Ping(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("ping", cancellationToken: cancellationToken);
}

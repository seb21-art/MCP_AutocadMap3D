using System.ComponentModel;
using ModelContextProtocol;
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

    [McpServerTool(Name = "undo", Title = "Annuler les derniers appels",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Annule, avec la commande U d'AutoCAD, les derniers appels d'outils de modification du connecteur " +
                 "dans le dessin actif : un appel = une étape (un run_batch compte autant d'étapes que d'appels de " +
                 "modification). Refuse de remonter au-delà d'une commande lancée entre-temps dans AutoCAD, à la main " +
                 "ou par une autre application. Ce qui n'est pas annulable par U (connexions FDO, présentations, " +
                 "fichiers enregistrés) n'est pas défait.")]
    public async Task<string> Undo(
        [Description("Nombre d'appels à annuler, 1 à 20.")] int count = 1,
        [Description("Annuler même au-delà des appels du connecteur, donc des modifications faites dans AutoCAD : " +
                     "seulement si l'utilisateur le demande explicitement.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var result = await plugin.CallForTextAsync("undo", new { count, force = force ? true : (bool?)null }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // AutoCAD exécute les U dès qu'il a rendu la main : on attend qu'ils soient finis, pour que l'appel suivant
        // trouve le dessin déjà annulé.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await plugin.CallAsync("_undo_progress", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!progress.TryGetProperty("inProgress", out var pending) || pending.GetInt32() == 0)
                return result;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new McpException("Les annulations ont été demandées à AutoCAD, mais ne sont pas terminées après 20 s : " +
                               "vérifiez l'état du dessin avant de continuer.");
    }
}

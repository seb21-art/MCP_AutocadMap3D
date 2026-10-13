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

    /// <summary>
    /// Annule les dernières étapes laissées par le connecteur dans le dessin actif. Sans force, refuse de remonter
    /// au-delà de la dernière commande lancée par quelqu'un d'autre : une modification faite à la main entre-temps
    /// n'est jamais défaite par erreur. Les commandes U s'exécutent dès que l'outil a rendu la main, avant tout appel
    /// suivant.
    /// </summary>
    public static object? UndoConnector(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var count = reader.GetInt("count", 1, min: 1, max: 20);
        var force = reader.GetBool("force", false);
        var doc = context.RequireDocument();
        var available = UndoJournal.ConnectorSteps(doc);
        if (count > available && !force)
            throw new PipeException(PipeErrorCodes.InvalidParams, available == 0
                ? "Rien à annuler pour le connecteur : aucune modification du connecteur depuis la dernière commande " +
                  "lancée dans AutoCAD (à la main ou par une autre application). force=true annule quand même, " +
                  "seulement si l'utilisateur le demande."
                : (available == 1 ? "Seule la dernière étape vient du connecteur" : $"Seules les {available} dernières étapes viennent du connecteur") +
                  " ; au-delà, U déferait une commande " +
                  "lancée dans AutoCAD (à la main ou par une autre application). Réduisez count, ou force=true " +
                  "seulement si l'utilisateur le demande.");

        UndoJournal.UndoQueued(doc, count);
        doc.SendStringToExecute(string.Concat(Enumerable.Repeat("_.U ", count)), true, false, false);
        return new { Undone = count, RemainingConnectorSteps = UndoJournal.ConnectorSteps(doc), Forced = force && count > available ? true : (bool?)null };
    }

    /// <summary>Annulations de l'outil undo pas encore exécutées par AutoCAD : le serveur attend qu'il n'y en ait plus.</summary>
    public static object? UndoProgress(ToolContext context, JsonElement? args) =>
        new { InProgress = context.Document is { } doc ? UndoJournal.UndosInProgress(doc) : 0 };

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

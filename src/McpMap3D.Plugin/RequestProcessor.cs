using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using McpMap3D.Plugin.Tools;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

/// <summary>Décode une requête, l'exécute sur le thread principal et produit la réponse.</summary>
internal sealed class RequestProcessor(ToolRegistry registry, MainThreadDispatcher dispatcher)
{
    public const string StatusMethod = "status";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    public async Task<PipeResponse> ProcessAsync(string line, CancellationToken cancellationToken)
    {
        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PipeRequest>(line, PipeProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fail(null, "?", TimeSpan.Zero, PipeErrorCodes.BadRequest, $"JSON invalide : {ex.Message}");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Method))
            return Fail(request?.Id, "?", TimeSpan.Zero, PipeErrorCodes.BadRequest, "Champ « method » manquant.");

        // Diagnostic servi directement, sans attendre le thread principal.
        if (request.Method == StatusMethod)
            return PipeResponse.Success(request.Id, Serialize(StatusReport.Capture()));

        if (!registry.TryGet(request.Method, out var tool))
            return Fail(request.Id, request.Method, TimeSpan.Zero, PipeErrorCodes.UnknownMethod,
                $"Méthode inconnue « {request.Method} ». Méthodes disponibles : {StatusMethod}, {string.Join(", ", registry.Names)}.");

        var timeout = request.TimeoutMs is int ms ? TimeSpan.FromMilliseconds(ms) : DefaultTimeout;
        timeout = timeout < MinTimeout ? MinTimeout : timeout > MaxTimeout ? MaxTimeout : timeout;

        var clock = Stopwatch.StartNew();
        try
        {
            var result = await dispatcher.InvokeAsync(() => ExecuteAsync(tool, request.Params), timeout, cancellationToken)
                .ConfigureAwait(false);
            PluginStats.RequestCompleted(tool.Name, ok: true, clock.Elapsed, error: null);
            return PipeResponse.Success(request.Id, result);
        }
        catch (PipeException ex)
        {
            return Fail(request.Id, tool.Name, clock.Elapsed, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Échec de « {tool.Name} »", ex);
            return Fail(request.Id, tool.Name, clock.Elapsed, PipeErrorCodes.Internal, $"{ex.GetType().Name} : {ex.Message}");
        }
    }

    /// <summary>
    /// Démarré sur le thread principal, depuis Application.Idle. Le résultat est sérialisé pendant que la
    /// transaction est ouverte, pour qu'aucun objet AutoCAD ne soit jamais lu depuis un autre thread.
    /// </summary>
    private static Task<JsonElement?> ExecuteAsync(ToolDefinition tool, JsonElement? args)
    {
        if (tool.Access == DrawingAccess.None)
            return Task.FromResult(Serialize(tool.Handler(new ToolContext(AcApp.DocumentManager.MdiActiveDocument, null), args)));

        var doc = AcApp.DocumentManager.MdiActiveDocument
            ?? throw new PipeException(PipeErrorCodes.NoDocument, "Aucun dessin ouvert dans AutoCAD.");

        if (tool.Access == DrawingAccess.Read)
        {
            using var docLock = doc.LockDocument(DocumentLockMode.Read, null, null, false);
            using var transaction = doc.Database.TransactionManager.StartTransaction();
            var result = Serialize(tool.Handler(new ToolContext(doc, transaction), args));
            transaction.Commit();
            return Task.FromResult(result);
        }

        if (tool.Access == DrawingAccess.Document)
        {
            using var docLock = doc.LockDocument();
            CommandTrace.Note($"document {tool.Name}");
            var result = Serialize(tool.Handler(new ToolContext(doc, null), args));
            return Task.FromResult(result);
        }

        return ExecuteWriteAsync(doc, tool, args);
    }

    /// <summary>
    /// Les modifications passent par le contexte commande : AutoCAD y verrouille le document et enregistre
    /// un point d'annulation, si bien que la commande U défait l'appel entier en une seule étape.
    /// Depuis le contexte application, les mêmes modifications ne seraient pas annulables.
    /// </summary>
    private static async Task<JsonElement?> ExecuteWriteAsync(Document doc, ToolDefinition tool, JsonElement? args)
    {
        JsonElement? result = null;
        ExceptionDispatchInfo? failure = null;
        CommandTrace.Note($"écriture {tool.Name} : entrée en contexte commande");

        await AcApp.DocumentManager.ExecuteInCommandContextAsync(
            _ =>
            {
                try
                {
                    ToolContext context;
                    using (var transaction = doc.Database.TransactionManager.StartTransaction())
                    {
                        context = new ToolContext(doc, transaction);
                        result = Serialize(tool.Handler(context, args));
                        transaction.Commit();
                    }

                    context.RunAfterCommit();
                    if (context.ResultAfterCommit is { } deferred)
                        result = Serialize(deferred());
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }

                return Task.CompletedTask;
            },
            null);

        CommandTrace.Note($"écriture {tool.Name} : terminée");
        failure?.Throw();
        return result;
    }

    private static JsonElement? Serialize(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, PipeProtocol.JsonOptions);

    private static PipeResponse Fail(string? id, string method, TimeSpan elapsed, string code, string message)
    {
        PluginStats.RequestCompleted(method, ok: false, elapsed, $"[{code}] {message}");
        return PipeResponse.Failure(id, code, message);
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpMap3D.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Un appel d'outil dans un lot.</summary>
public sealed record BatchCall(
    [property: JsonPropertyName("tool"), Description("Nom de l'outil, par exemple create_line.")] string Tool,
    [property: JsonPropertyName("arguments"), Description("Arguments de l'outil, comme pour un appel direct. Vide si omis.")]
    Dictionary<string, JsonElement>? Arguments = null);

/// <summary>
/// Outil de lot : enchaîne des appels aux autres outils de ce serveur en une seule requête du client. Chaque appel
/// passe par le même chemin qu'un appel direct (liaison et contrôle des arguments, logique propre au serveur, plug-in),
/// si bien que le lot n'ajoute rien côté AutoCAD : chaque appel de modification reste une étape d'annulation U.
/// Le lot ne compte pas ces étapes : un appel en échec peut en consommer une vide, et certains outils (FDO,
/// dessins) ne s'annulent pas ; seul l'outil appelé sait lequel de ces cas s'applique.
/// </summary>
[McpServerToolType]
public sealed class BatchTools
{
    public const string ToolName = "run_batch";
    public const int MaxCalls = 50;

    [McpServerTool(Name = ToolName, Title = "Exécuter un lot d'appels d'outils",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Exécute dans l'ordre une suite d'appels aux autres outils de ce serveur, en un seul appel : pour " +
                 "enchaîner des opérations indépendantes (créer plusieurs objets, lire plusieurs listes…) sans un " +
                 "aller-retour par outil. 50 appels au plus, run_batch exclu. Les arguments de chaque appel sont ceux " +
                 "de l'outil appelé ; un appel ne peut pas reprendre le résultat d'un appel précédent du même lot " +
                 "(handle créé…) : faites alors deux lots. Par défaut le lot s'arrête à la première erreur, les appels " +
                 "suivants ne sont pas exécutés et ceux déjà faits restent faits. Chaque appel de modification reste " +
                 "une étape d'annulation séparée. Renvoie, pour chaque appel, ok et result, ou error ; le lot est " +
                 "signalé en erreur si un appel a échoué.")]
    public static async Task<CallToolResult> RunBatch(
        McpServer server,
        IServiceProvider services,
        [Description("Appels à exécuter dans l'ordre, chacun { tool, arguments }.")] BatchCall[] calls,
        [Description("Arrêter le lot à la première erreur (par défaut). false : exécuter tous les appels.")] bool stopOnError = true,
        CancellationToken cancellationToken = default)
    {
        if (calls.Length == 0)
            throw new McpException("Lot vide : donnez au moins un appel dans calls.");
        if (calls.Length > MaxCalls)
            throw new McpException($"Lot de {calls.Length} appels : {MaxCalls} au plus. Découpez-le en plusieurs lots.");

        var tools = server.ServerOptions.ToolCollection;
        for (var i = 0; i < calls.Length; i++)
        {
            var name = calls[i].Tool;
            if (name == ToolName)
                throw new McpException($"Appel {i} : run_batch ne peut pas s'appeler lui-même.");
            if (tools is null || !tools.TryGetPrimitive(name ?? "", out _))
                throw new McpException($"Appel {i} : outil inconnu « {name} ». Aucun appel du lot n'a été exécuté.");
        }

        var results = new List<BatchCallResult>(calls.Length);
        var attachments = new List<ContentBlock>();
        var failed = 0;
        for (var i = 0; i < calls.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tools!.TryGetPrimitive(calls[i].Tool, out var tool);
            var outcome = await InvokeAsync(server, services, tool!, i, calls[i], attachments, cancellationToken)
                .ConfigureAwait(false);
            results.Add(outcome);

            if (!outcome.Ok)
            {
                failed++;
                if (stopOnError)
                    break;
            }
        }

        var summary = new
        {
            count = calls.Length,
            succeeded = results.Count - failed,
            failed,
            skipped = calls.Length - results.Count,
            results,
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(summary, PipeProtocol.JsonOptions) }, .. attachments],
            IsError = failed > 0,
        };
    }

    private static async Task<BatchCallResult> InvokeAsync(
        McpServer server, IServiceProvider services, McpServerTool tool, int index, BatchCall call,
        List<ContentBlock> attachments, CancellationToken cancellationToken)
    {
        var parameters = new CallToolRequestParams
        {
            Name = call.Tool,
            Arguments = call.Arguments ?? [],
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId($"{ToolName}-{index}"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        var context = new RequestContext<CallToolRequestParams>(server, request, parameters) { Services = services };

        try
        {
            var result = await tool.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

            // Images (capture_view…) : jointes au résultat du lot, dans l'ordre des appels.
            attachments.AddRange(result.Content.Where(block => block is not TextContentBlock));
            return result.IsError == true
                ? new BatchCallResult(index, call.Tool, false, null, text.Length > 0 ? text : "Erreur sans détail.")
                : new BatchCallResult(index, call.Tool, true, ParseResult(text), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BatchCallResult(index, call.Tool, false, null, ex.Message);
        }
    }

    /// <summary>Résultat JSON repris tel quel (sans double encodage), texte libre sinon.</summary>
    private static JsonNode? ParseResult(string text)
    {
        if (text.Length == 0)
            return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private sealed record BatchCallResult(int Index, string Tool, bool Ok, JsonNode? Result, string? Error);
}

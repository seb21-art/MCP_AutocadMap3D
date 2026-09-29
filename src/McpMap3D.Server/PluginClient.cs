using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using McpMap3D.Shared;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace McpMap3D.Server;

/// <summary>
/// Relaie une requête au plug-in AutoCAD par le named pipe. Une connexion par appel :
/// pas d'état partagé, et une reconnexion implicite si AutoCAD a redémarré.
/// Toute erreur est convertie en <see cref="McpException"/>, dont le message est transmis à Claude.
/// </summary>
public sealed class PluginClient(ILogger<PluginClient> logger)
{
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(15);

    private const int ConnectTimeoutMs = 3000;
    private static readonly TimeSpan ExecutionMargin = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private long _nextId;

    /// <summary>
    /// Comme <see cref="CallAsync"/>, mais renvoie le résultat en texte JSON : le SDK MCP le transmet tel quel,
    /// sans réécrire les accents en séquences \uXXXX.
    /// </summary>
    public async Task<string> CallForTextAsync(
        string method,
        object? parameters = null,
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(method, parameters, busyTimeout, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, PipeProtocol.JsonOptions);
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object? parameters = null,
        TimeSpan? busyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeout = busyTimeout ?? DefaultBusyTimeout;
        var request = new PipeRequest
        {
            Id = Interlocked.Increment(ref _nextId).ToString(),
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, PipeProtocol.JsonOptions),
            TimeoutMs = (int)timeout.TotalMilliseconds,
        };

        if (!IsAutoCadRunning())
            throw new McpException(
                "AutoCAD Map 3D n'est pas lancé. Demandez à l'utilisateur de le démarrer, " +
                "d'attendre la fin du chargement puis de réessayer.");

        await using var pipe = new NamedPipeClientStream(
            ".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new McpException(
                "AutoCAD est lancé, mais le plug-in MCP Map 3D ne répond pas : il n'est pas chargé " +
                "(bundle MCPMap3D absent ou chargement refusé au démarrage) ou AutoCAD termine son démarrage. " +
                "Dans AutoCAD, la commande MCPMAP_STATUS indique l'état du plug-in.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new McpException(
                $"Le pipe {PipeProtocol.PipeName} appartient à un autre utilisateur Windows : connexion refusée.");
        }

        using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

        // Le plug-in borne l'attente de disponibilité d'AutoCAD ; la marge couvre l'exécution elle-même.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout + ExecutionMargin);

        var clock = Stopwatch.StartNew();
        string? line;
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeProtocol.JsonOptions).AsMemory(), deadline.Token)
                .ConfigureAwait(false);
            line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                $"AutoCAD n'a pas répondu à « {method} » en {clock.Elapsed.TotalSeconds:0} s. " +
                "L'opération a peut-être été exécutée malgré tout : vérifiez le dessin avant de réessayer.");
        }
        catch (IOException ex)
        {
            throw new McpException($"Liaison avec AutoCAD interrompue pendant « {method} » (AutoCAD fermé ?) : {ex.Message}");
        }

        if (line is null)
            throw new McpException($"AutoCAD a fermé la liaison sans répondre à « {method} » (AutoCAD en cours de fermeture ?).");

        var response = JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.JsonOptions)
            ?? throw new McpException("Réponse vide du plug-in AutoCAD.");

        logger.LogInformation("{Method} : {Outcome} en {Elapsed} ms",
            method, response.Ok ? "OK" : response.Error?.Code, clock.ElapsedMilliseconds);

        if (!response.Ok)
            throw new McpException(DescribeError(response.Error));

        return response.Result ?? JsonSerializer.SerializeToElement<object?>(null);
    }

    private static string DescribeError(PipeError? error) => error switch
    {
        null => "Le plug-in AutoCAD a signalé une erreur sans détail.",
        { Code: PipeErrorCodes.Busy or PipeErrorCodes.NoDocument or PipeErrorCodes.InvalidParams } => error.Message,
        { Code: PipeErrorCodes.UnknownMethod } =>
            $"{error.Message} Le plug-in chargé dans AutoCAD est probablement plus ancien que ce serveur : réinstallez-le.",
        _ => $"{error.Message} (code {error.Code})",
    };

    private static bool IsAutoCadRunning()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName("acad");
        try
        {
            return processes.Any(p => p.SessionId == sessionId);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}

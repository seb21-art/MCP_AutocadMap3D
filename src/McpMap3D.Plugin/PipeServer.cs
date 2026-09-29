using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using McpMap3D.Shared;

namespace McpMap3D.Plugin;

internal enum PipeServerState
{
    NotStarted,
    Listening,
    AnotherInstanceActive,
    Faulted,
    Stopped,
}

/// <summary>
/// Serveur named pipe réservé à l'utilisateur courant, sur un thread d'arrière-plan.
/// Chaque client est servi en parallèle ; ses requêtes sont traitées l'une après l'autre.
/// </summary>
internal sealed class PipeServer(RequestProcessor processor) : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly CancellationTokenSource _cts = new();
    private volatile PipeServerState _state = PipeServerState.NotStarted;
    private Mutex? _ownerMutex;
    private Thread? _thread;

    public string PipeName => PipeProtocol.PipeName;

    public PipeServerState State
    {
        get => _state;
        private set => _state = value;
    }

    public DateTime? ListeningSince { get; private set; }

    /// <summary>Doit être appelé sur le thread principal (le mutex y est acquis et libéré).</summary>
    public void Start()
    {
        // Si plusieurs AutoCAD tournent dans la session, seul le premier sert le pipe.
        _ownerMutex = new Mutex(initiallyOwned: false, $@"Local\{PipeName}.owner");
        bool owned;
        try
        {
            owned = _ownerMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // L'instance précédente s'est arrêtée brutalement.
        }

        if (!owned)
        {
            _ownerMutex.Dispose();
            _ownerMutex = null;
            State = PipeServerState.AnotherInstanceActive;
            Log.Info($"Pipe {PipeName} déjà servi par une autre instance d'AutoCAD : serveur non démarré.");
            return;
        }

        State = PipeServerState.Listening;
        ListeningSince = DateTime.Now;
        _thread = new Thread(Run) { IsBackground = true, Name = "McpMap3D pipe server" };
        _thread.Start();
        Log.Info($"Serveur en écoute sur \\\\.\\pipe\\{PipeName}");
    }

    private void Run()
    {
        try
        {
            AcceptLoopAsync(_cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Une exception non gérée sur ce thread fermerait AutoCAD.
            State = PipeServerState.Faulted;
            PluginStats.RecordError($"Serveur pipe arrêté : {ex.Message}");
            Log.Error("Boucle d'acceptation arrêtée", ex);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var connection = pipe;
                pipe = null;
                _ = Task.Run(() => ServeClientAsync(connection, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PluginStats.RecordError($"Pipe : {ex.Message}");
                Log.Error("Erreur d'acceptation de connexion", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        PluginStats.ClientConnected();
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                        break; // Client déconnecté.

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var response = await processor.ProcessAsync(line, cancellationToken).ConfigureAwait(false);
                    var json = JsonSerializer.Serialize(response, PipeProtocol.JsonOptions);
                    await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Client parti pendant l'échange.
        }
        catch (Exception ex)
        {
            PluginStats.RecordError($"Client pipe : {ex.Message}");
            Log.Error("Erreur de traitement d'un client", ex);
        }
        finally
        {
            PluginStats.ClientDisconnected();
        }
    }

    /// <summary>Doit être appelé sur le thread principal.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_ownerMutex is not null)
        {
            _ownerMutex.ReleaseMutex();
            _ownerMutex.Dispose();
            _ownerMutex = null;
        }

        if (State == PipeServerState.Listening)
            State = PipeServerState.Stopped;
    }
}

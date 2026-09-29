using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

/// <summary>
/// File d'attente de travaux exécutés sur le thread principal d'AutoCAD, dans Application.Idle,
/// uniquement quand AutoCAD est au repos (ni commande en cours, ni boîte de dialogue modale).
/// </summary>
internal sealed class MainThreadDispatcher : IDisposable
{
    private const uint WM_NULL = 0x0000;
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleBudget = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BusyLogInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private IntPtr _mainWindow;
    private int _pending;
    private bool _running;
    private volatile bool _inFlight;
    private bool _disposed;
    private long _idleEvents;
    private long _lastIdleTicks;
    private long _lastBusyLogTicks;
    private volatile string? _busyReason;

    /// <summary>Doit être construit sur le thread principal.</summary>
    public MainThreadDispatcher()
    {
        _mainWindow = TryGetMainWindow();
        AcApp.Idle += OnIdle;
        Log.Info($"File du thread principal prête (fenêtre 0x{_mainWindow:X})");
    }

    public int PendingCount => Volatile.Read(ref _pending);

    public long IdleEvents => Interlocked.Read(ref _idleEvents);

    public DateTime? LastIdleAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastIdleTicks);
            return ticks > 0 ? new DateTime(ticks) : null;
        }
    }

    /// <summary>Raison d'occupation constatée au dernier Idle, ou null si AutoCAD était au repos.</summary>
    public string? BusyReason => _busyReason;

    public IntPtr MainWindow => _mainWindow;

    /// <summary>
    /// Démarre <paramref name="action"/> sur le thread principal et attend la fin de la tâche qu'elle renvoie
    /// (une écriture se poursuit en contexte commande). Si AutoCAD n'a pas pu démarrer le travail dans
    /// <paramref name="startTimeout"/>, il est abandonné et une erreur « occupé » est levée.
    /// Un travail déjà démarré n'est jamais interrompu : on attend sa fin.
    /// </summary>
    public async Task<T> InvokeAsync<T>(Func<Task<T>> action, TimeSpan startTimeout, CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new PipeException(PipeErrorCodes.ShuttingDown, "Le plug-in est en cours d'arrêt.");

        var item = new WorkItem(async () => (object?)await action().ConfigureAwait(false));
        var idleEventsAtStart = IdleEvents;
        Interlocked.Increment(ref _pending);
        _queue.Enqueue(item);
        Wake();

        var clock = Stopwatch.StartNew();
        while (!item.Completion.Task.IsCompleted)
        {
            var remaining = startTimeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
            {
                if (item.TryCancel())
                {
                    Interlocked.Decrement(ref _pending);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new PipeException(PipeErrorCodes.Busy, BusyMessage(idleEventsAtStart, startTimeout));
                }
                break; // Déjà démarré : on attend la fin ci-dessous.
            }

            var delay = remaining < WakeInterval ? remaining : WakeInterval;
            await Task.WhenAny(item.Completion.Task, Task.Delay(delay, cancellationToken)).ConfigureAwait(false);

            // Application.Idle n'est levé qu'à la sortie de la boucle de messages : on la relance.
            if (!item.Completion.Task.IsCompleted)
                Wake();
        }

        return (T)(await item.Completion.Task.ConfigureAwait(false))!;
    }

    private string BusyMessage(long idleEventsAtStart, TimeSpan timeout)
    {
        // Sans aucun Idle pendant l'attente, la boucle principale est bloquée (dialogue modal, calcul long).
        var cause = IdleEvents == idleEventsAtStart
            ? "AutoCAD ne répond pas (boîte de dialogue ouverte ou traitement long en cours)"
            : $"AutoCAD est occupé ({_busyReason ?? "il n'est pas au repos"})";

        return $"{cause} : opération abandonnée après {timeout.TotalSeconds:0.#} s, sans rien modifier. " +
               "Terminez l'action en cours dans AutoCAD (Échap ou fermeture de la boîte de dialogue) puis réessayez.";
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_running || _inFlight)
            return;

        _running = true;
        try
        {
            Interlocked.Increment(ref _idleEvents);
            Interlocked.Exchange(ref _lastIdleTicks, DateTime.Now.Ticks);

            if (_mainWindow == IntPtr.Zero)
                _mainWindow = TryGetMainWindow();

            if (_queue.IsEmpty)
                return;

            var clock = Stopwatch.StartNew();
            while (!_inFlight && clock.Elapsed < IdleBudget)
            {
                var busyReason = GetBusyReason();
                _busyReason = busyReason;
                if (busyReason is not null)
                {
                    LogBusy(busyReason);
                    return;
                }

                if (!_queue.TryDequeue(out var item))
                    return;

                if (!item.TryStart())
                    continue; // Abandonné par son appelant.

                Interlocked.Decrement(ref _pending);

                // Une écriture se termine en contexte commande, après le retour d'Idle : on ne lance
                // le travail suivant qu'une fois celui-ci achevé. Une lecture, elle, finit sur place.
                _inFlight = true;
                item.ExecuteAsync().ContinueWith(
                    _ =>
                    {
                        _inFlight = false;
                        Wake();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (PendingCount > 0)
                Wake();
        }
        catch (Exception ex)
        {
            Log.Error("Erreur dans Application.Idle", ex);
        }
        finally
        {
            _running = false;
        }
    }

    private string? GetBusyReason()
    {
        if (_mainWindow != IntPtr.Zero && !IsWindowEnabled(_mainWindow))
            return "une boîte de dialogue est ouverte";

        var doc = AcApp.DocumentManager.MdiActiveDocument;
        if (doc is null || doc.Editor.IsQuiescent)
            return null;

        var command = doc.CommandInProgress;
        return string.IsNullOrEmpty(command)
            ? "une commande ou une expression LISP est en cours"
            : $"la commande {command} est en cours";
    }

    /// <summary>Trace au plus une fois toutes les 5 s pourquoi la file n'avance pas.</summary>
    private void LogBusy(string reason)
    {
        var now = DateTime.Now.Ticks;
        if (now - _lastBusyLogTicks < BusyLogInterval.Ticks)
            return;

        _lastBusyLogTicks = now;
        Log.Info($"En attente : {PendingCount} travail(aux), {reason}");
    }

    private void Wake()
    {
        var hwnd = _mainWindow;
        if (hwnd != IntPtr.Zero && !PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero))
            PluginStats.RecordError($"PostMessage a échoué (code {Marshal.GetLastPInvokeError()})");
    }

    private static IntPtr TryGetMainWindow()
    {
        try
        {
            return AcApp.MainWindow?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Doit être appelé sur le thread principal.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        AcApp.Idle -= OnIdle;
        while (_queue.TryDequeue(out var item))
        {
            if (!item.TryCancel())
                continue;

            Interlocked.Decrement(ref _pending);
            item.Completion.TrySetException(new PipeException(PipeErrorCodes.ShuttingDown, "AutoCAD se ferme."));
        }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private sealed class WorkItem(Func<Task<object?>> action)
    {
        private const int Pending = 0;
        private const int Started = 1;
        private const int Cancelled = 2;
        private int _state;

        // Continuations asynchrones : le code du pipe ne doit jamais reprendre sur le thread d'AutoCAD.
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryCancel() => Interlocked.CompareExchange(ref _state, Cancelled, Pending) == Pending;

        public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Pending) == Pending;

        public async Task ExecuteAsync()
        {
            try
            {
                Completion.SetResult(await action().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Completion.SetException(ex);
            }
        }
    }
}

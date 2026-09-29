namespace McpMap3D.Plugin;

/// <summary>Compteurs affichés par MCPMAP_STATUS, mis à jour depuis plusieurs threads.</summary>
internal static class PluginStats
{
    private static int _connectedClients;
    private static long _requests;
    private static long _failures;
    private static string? _lastRequest;
    private static string? _lastError;

    public static DateTime LoadedAt { get; private set; }

    public static int ConnectedClients => Volatile.Read(ref _connectedClients);

    public static long Requests => Interlocked.Read(ref _requests);

    public static long Failures => Interlocked.Read(ref _failures);

    public static string? LastRequest => Volatile.Read(ref _lastRequest);

    public static string? LastError => Volatile.Read(ref _lastError);

    public static void MarkLoaded() => LoadedAt = DateTime.Now;

    public static void ClientConnected() => Interlocked.Increment(ref _connectedClients);

    public static void ClientDisconnected() => Interlocked.Decrement(ref _connectedClients);

    public static void RequestCompleted(string method, bool ok, TimeSpan elapsed, string? error)
    {
        Interlocked.Increment(ref _requests);
        var summary = $"{method} à {DateTime.Now:HH:mm:ss} ({(ok ? "OK" : "échec")}, {elapsed.TotalMilliseconds:0} ms)";
        Volatile.Write(ref _lastRequest, summary);
        if (!ok)
        {
            Interlocked.Increment(ref _failures);
            RecordError($"{method} : {error}");
        }
    }

    public static void RecordError(string message) =>
        Volatile.Write(ref _lastError, $"{DateTime.Now:HH:mm:ss} {message}");
}

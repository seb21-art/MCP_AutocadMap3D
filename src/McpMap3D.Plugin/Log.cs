using System.Text;
using McpMap3D.Shared;

namespace McpMap3D.Plugin;

/// <summary>Journal texte minimal, utilisable depuis n'importe quel thread.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    // BOM écrit à la création du fichier : lisible tel quel par Windows PowerShell 5.1.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static string FilePath { get; } = Path.Combine(InstallPaths.Logs, "plugin.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} : {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}", Utf8WithBom);
            }
        }
        catch
        {
            // Le journal ne doit jamais faire échouer le plug-in.
        }
    }
}

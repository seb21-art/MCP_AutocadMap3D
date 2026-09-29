namespace McpMap3D.Shared;

/// <summary>
/// Emplacements des fichiers propres au connecteur, volontairement hors d'AppData : Claude Desktop (paquet MSIX)
/// redirige vers son espace privé les nouveaux dossiers créés dans AppData par les processus qu'il lance,
/// ce qui les rendrait invisibles pour AutoCAD ou un terminal lancés normalement.
/// </summary>
public static class InstallPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcpmap3d");

    public static string Logs => Path.Combine(Root, "logs");
}

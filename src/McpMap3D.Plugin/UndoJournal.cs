using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin;

/// <summary>
/// Compte, par dessin, les étapes d'annulation laissées par le connecteur depuis la dernière commande lancée par
/// quelqu'un d'autre (l'utilisateur, une autre application). L'outil undo ne remonte pas au-delà sans force : il ne
/// défait ainsi que le travail du connecteur, jamais une modification faite à la main entre-temps.
/// Les commandes lancées pendant une écriture du connecteur (une commande mise en file par une autre application,
/// comme COVANEWS de Covadis) sont ignorées : elles s'exécutent dans le même tour que l'écriture.
/// Tout se passe sur le thread principal d'AutoCAD.
/// </summary>
internal static class UndoJournal
{
    private static readonly Dictionary<Document, int> Steps = [];
    private static readonly Dictionary<Document, int> PendingUndos = [];
    private static readonly Dictionary<Document, int> Outstanding = [];
    private static readonly HashSet<Document> Attached = [];
    private static Document? _writing;

    public static void Start()
    {
        foreach (Document document in AcApp.DocumentManager)
            Attach(document);

        AcApp.DocumentManager.DocumentCreated += (_, e) => Attach(e.Document);
        AcApp.DocumentManager.DocumentToBeDestroyed += (_, e) =>
        {
            Steps.Remove(e.Document);
            PendingUndos.Remove(e.Document);
            Outstanding.Remove(e.Document);
            Attached.Remove(e.Document);
        };
    }

    /// <summary>Étapes du connecteur que undo peut défaire dans ce dessin.</summary>
    public static int ConnectorSteps(Document document) => Steps.GetValueOrDefault(document);

    /// <summary>Commandes U mises en file par l'outil undo et pas encore terminées.</summary>
    public static int UndosInProgress(Document document) => Outstanding.GetValueOrDefault(document);

    /// <summary>Encadre une écriture du connecteur, qui laisse une étape d'annulation, même en cas d'échec.</summary>
    public static void BeginWrite(Document document) => _writing = document;

    public static void EndWrite(Document document)
    {
        _writing = null;
        Steps[document] = ConnectorSteps(document) + 1;
    }

    /// <summary>Annulations mises en file par l'outil undo : leurs commandes U ne remettent pas le compte à zéro.</summary>
    public static void UndoQueued(Document document, int count)
    {
        Steps[document] = Math.Max(0, ConnectorSteps(document) - count);
        PendingUndos[document] = PendingUndos.GetValueOrDefault(document) + count;
        Outstanding[document] = UndosInProgress(document) + count;
    }

    private static void Attach(Document document)
    {
        if (!Attached.Add(document))
            return;

        document.CommandWillStart += (_, e) => OnCommand(document, e.GlobalCommandName);
        document.CommandEnded += (_, e) => OnCommandFinished(document, e.GlobalCommandName);
        document.CommandCancelled += (_, e) => OnCommandFinished(document, e.GlobalCommandName);
        document.CommandFailed += (_, e) => OnCommandFinished(document, e.GlobalCommandName);
    }

    private static void OnCommandFinished(Document document, string command)
    {
        if (command.Equals("U", StringComparison.OrdinalIgnoreCase) && UndosInProgress(document) > 0)
            Outstanding[document]--;
    }

    private static void OnCommand(Document document, string command)
    {
        if (_writing == document)
            return;

        if (command.Equals("U", StringComparison.OrdinalIgnoreCase) && PendingUndos.GetValueOrDefault(document) > 0)
        {
            PendingUndos[document]--;
            return;
        }

        // Toute autre commande peut laisser sa propre étape d'annulation (même ZOOM ou PAN) : le compte repart de zéro.
        Steps[document] = 0;
        PendingUndos.Remove(document);
    }
}

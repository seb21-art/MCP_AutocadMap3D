using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Gestion des dessins ouverts : liste, création, ouverture, enregistrement et fermeture. Ces outils sont déclarés
/// sans accès au dessin (DrawingAccess.None) : ils s'exécutent dans Application.Idle, en contexte application, le
/// seul où AutoCAD accepte d'ouvrir, d'activer et de fermer des documents. Ils ne s'annulent pas avec U.
/// </summary>
internal static class DrawingTools
{
    private static readonly string[] OpenableExtensions = [".dwg", ".dxf", ".dwt"];

    public static object ListDrawings(ToolContext context, JsonElement? args)
    {
        var active = AcApp.DocumentManager.MdiActiveDocument;
        var drawings = AcApp.DocumentManager.Cast<Document>().Select(doc => Describe(doc, doc == active)).ToArray();
        return new { Count = drawings.Length, Active = active?.Name, Drawings = drawings };
    }

    /// <summary>Nouveau dessin, à partir d'un gabarit, qui devient le dessin actif ; enregistré si « filePath » est donné.</summary>
    public static object NewDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var template = ResolveTemplate(reader.GetString("template"));

        // Vérifié avant de créer le dessin : une erreur ne doit pas laisser de document orphelin.
        var target = reader.GetString("filePath") is { Length: > 0 } requested
            ? SaveTarget(requested, currentPath: null, reader.GetBool("overwrite", false))
            : null;

        Document doc;
        try
        {
            doc = AcApp.DocumentManager.Add(template);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu créer de dessin à partir de {template} ({ex.ErrorStatus}).");
        }

        Activate(doc);
        if (target is not null)
            SaveAs(doc, target);

        return new { Template = template, Drawing = Describe(doc, isActive: true) };
    }

    /// <summary>Ouvre un fichier, ou active un dessin déjà ouvert désigné par son chemin ou son nom.</summary>
    public static object OpenDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var requested = reader.RequireString("filePath");
        var readOnly = reader.GetBool("readOnly", false);

        // Par son nom d'abord, puis par son chemin complet : un chemin relatif peut désigner un dessin déjà ouvert.
        var path = FullPath(requested);
        var open = FindOpen(requested) ?? FindOpen(path);
        if (open is not null)
        {
            Activate(open);
            return new { AlreadyOpen = true, Drawing = Describe(open, isActive: true) };
        }

        if (!OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{path} n'est pas un dessin : extensions acceptées {string.Join(", ", OpenableExtensions)}.");

        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        // Un fichier déjà ouvert ailleurs ferait apparaître une boîte de dialogue qui bloquerait AutoCAD.
        if (!readOnly && !CanWrite(path))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le fichier {path} est en lecture seule ou déjà ouvert par une autre application ou un autre utilisateur : " +
                "ouvrez-le avec readOnly = true.");

        Document doc;
        try
        {
            doc = AcApp.DocumentManager.Open(path, readOnly);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu ouvrir {path} ({ex.ErrorStatus}).");
        }

        Activate(doc);
        return new { AlreadyOpen = false, Drawing = Describe(doc, isActive: true) };
    }

    /// <summary>Enregistre un dessin ouvert, sous son nom ou sous un autre (« filePath »).</summary>
    public static object SaveDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var doc = Target(reader);
        var current = SavedPath(doc);
        var requested = reader.GetString("filePath");

        if (string.IsNullOrWhiteSpace(requested))
        {
            if (current is null)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le dessin {doc.Name} n'a jamais été enregistré : indiquez son chemin dans « filePath ».");

            if (doc.IsReadOnly)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le dessin {current} est ouvert en lecture seule : enregistrez-le sous un autre nom avec « filePath ».");

            if (string.Equals(Path.GetExtension(current), ".dxf", StringComparison.OrdinalIgnoreCase))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le dessin {current} vient d'un fichier DXF : enregistrez-le en .dwg avec « filePath ».");

            Save(doc);
        }
        else
        {
            SaveAs(doc, SaveTarget(requested, current, reader.GetBool("overwrite", false)));
        }

        return new { Drawing = Describe(doc, doc == AcApp.DocumentManager.MdiActiveDocument) };
    }

    /// <summary>Ferme un dessin ; refuse de perdre des modifications sans save ou discardChanges.</summary>
    public static object CloseDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var doc = Target(reader);
        var save = reader.GetBool("save", false);
        var discard = reader.GetBool("discardChanges", false);
        if (save && discard)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Choisissez entre « save » et « discardChanges ».");

        var name = doc.Name;
        var modified = IsModified(doc);
        if (save)
            SaveDrawing(context, args);
        else if (modified && !discard)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le dessin {name} a des modifications non enregistrées : fermez-le avec save = true pour les enregistrer, " +
                "ou discardChanges = true pour les abandonner.");

        try
        {
            doc.CloseAndDiscard();
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu fermer {name} ({ex.ErrorStatus}).");
        }

        return new
        {
            Closed = name,
            Saved = save,
            ChangesDiscarded = modified && !save,
            Active = AcApp.DocumentManager.MdiActiveDocument?.Name,
            Remaining = AcApp.DocumentManager.Count,
        };
    }

    private static object Describe(Document doc, bool isActive) => new
    {
        Name = Path.GetFileName(doc.Name),
        Path = SavedPath(doc),
        Active = isActive,
        ReadOnly = doc.IsReadOnly,
        Modified = IsModified(doc),
    };

    /// <summary>Dessin désigné par « drawing » (chemin ou nom de fichier), le dessin actif par défaut.</summary>
    private static Document Target(ArgReader reader)
    {
        var requested = reader.GetString("drawing");
        if (string.IsNullOrWhiteSpace(requested))
            return AcApp.DocumentManager.MdiActiveDocument
                ?? throw new PipeException(PipeErrorCodes.NoDocument, "Aucun dessin ouvert dans AutoCAD.");

        return FindOpen(requested)
            ?? throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Aucun dessin ouvert ne correspond à « {requested} » (voir list_drawings).");
    }

    /// <summary>Dessin ouvert dont le chemin complet, ou à défaut le nom de fichier, correspond.</summary>
    private static Document? FindOpen(string requested)
    {
        var docs = AcApp.DocumentManager.Cast<Document>().ToList();
        if (Path.IsPathRooted(requested))
        {
            var full = LongPath(requested);
            return docs.FirstOrDefault(doc => string.Equals(LongPath(doc.Name), full, StringComparison.OrdinalIgnoreCase));
        }

        var byName = docs.Where(doc => string.Equals(Path.GetFileName(doc.Name), requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(doc.Name), requested, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Plusieurs dessins ouverts s'appellent « {requested} » : indiquez le chemin complet (voir list_drawings)."),
        };
    }

    private static void Activate(Document doc)
    {
        if (AcApp.DocumentManager.MdiActiveDocument != doc)
            AcApp.DocumentManager.MdiActiveDocument = doc;
    }

    /// <summary>Chemin du fichier du dessin, ou null s'il n'a jamais été enregistré (Dessin1.dwg).</summary>
    private static string? SavedPath(Document doc) =>
        doc.IsNamedDrawing && Path.IsPathRooted(doc.Name) ? doc.Name : null;

    /// <summary>
    /// État « modifié » lu par l'interface COM du document (propriété Saved), valable pour tout dessin ouvert, alors
    /// que la variable DBMOD ne se lit que sur le dessin actif.
    /// </summary>
    private static bool IsModified(Document doc)
    {
        try
        {
            dynamic com = doc.GetAcadDocument();
            return !(bool)com.Saved;
        }
        catch (Exception)
        {
            return doc == AcApp.DocumentManager.MdiActiveDocument && Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0;
        }
    }

    private static void Save(Document doc)
    {
        try
        {
            dynamic com = doc.GetAcadDocument();
            com.Save();
        }
        catch (Exception ex) when (ex is not PipeException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu enregistrer {doc.Name} : {ex.Message}");
        }
    }

    /// <summary>Enregistrer sous : le document prend le nouveau nom, comme avec la commande ENREGSOUS.</summary>
    private static void SaveAs(Document doc, string path)
    {
        try
        {
            dynamic com = doc.GetAcadDocument();
            com.SaveAs(path);
        }
        catch (Exception ex) when (ex is not PipeException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu enregistrer {doc.Name} sous {path} : {ex.Message}");
        }

        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.Internal, $"AutoCAD n'a pas signalé d'erreur, mais le fichier {path} n'a pas été écrit.");
    }

    /// <summary>Chemin d'enregistrement : extension .dwg, dossier existant, pas d'écrasement implicite.</summary>
    private static string SaveTarget(string requested, string? currentPath, bool overwrite)
    {
        var path = FullPath(requested);
        if (!Path.HasExtension(path))
            path += ".dwg";

        if (!string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{path} : seul l'enregistrement en .dwg est pris en charge.");

        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le dossier {directory} n'existe pas.");

        if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            return path;

        if (FindOpen(path) is not null)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} est ouvert dans un autre dessin : fermez-le d'abord.");

        if (File.Exists(path) && !overwrite)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} existe déjà : indiquez overwrite = true pour le remplacer.");

        return path;
    }

    /// <summary>Chemin complet ; un chemin relatif part du dossier du dessin actif, sinon du dossier Documents.</summary>
    private static string FullPath(string requested)
    {
        var active = AcApp.DocumentManager.MdiActiveDocument;
        var folder = active is not null && SavedPath(active) is string saved
            ? Path.GetDirectoryName(saved)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return LongPath(Path.GetFullPath(Environment.ExpandEnvironmentVariables(requested), folder));
    }

    /// <summary>
    /// Chemin complet aux noms longs : %TEMP% vaut souvent C:\Users\ADMINN~1\…, alors qu'AutoCAD nomme ses dessins
    /// par le chemin long. Le dossier doit exister ; le nom de fichier est gardé tel quel.
    /// </summary>
    private static string LongPath(string path)
    {
        if (!Path.IsPathRooted(path))
            return path;

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
            return full;

        var buffer = new StringBuilder(1024);
        var length = GetLongPathName(directory, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? Path.Combine(buffer.ToString(), Path.GetFileName(full)) : full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);

    /// <summary>
    /// Gabarit demandé (chemin, ou nom cherché dans le dossier des gabarits puis dans les chemins de recherche
    /// d'AutoCAD), sinon celui de la commande NOUVEAU rapide (option QNEW), sinon acadiso.dwt ou acad.dwt selon MEASUREINIT.
    /// </summary>
    private static string ResolveTemplate(string? requested)
    {
        var name = requested;
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                dynamic preferences = AcApp.Preferences;
                name = (string)preferences.Files.QNewTemplateFile;
            }
            catch (Exception)
            {
                name = null;
            }

            if (string.IsNullOrWhiteSpace(name))
                name = Convert.ToInt32(AcApp.GetSystemVariable("MEASUREINIT")) == 1 ? "acadiso.dwt" : "acad.dwt";
        }

        name = Environment.ExpandEnvironmentVariables(name);
        if (!Path.HasExtension(name))
            name += ".dwt";

        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le gabarit {name} n'existe pas.");

        // Le dossier des gabarits (options d'AutoCAD, onglet Fichiers) ne fait pas partie des chemins de recherche.
        var templateFolder = TemplateFolder();
        if (templateFolder is not null && File.Exists(Path.Combine(templateFolder, name)))
            return Path.Combine(templateFolder, name);

        try
        {
            var database = AcApp.DocumentManager.MdiActiveDocument?.Database ?? HostApplicationServices.WorkingDatabase;
            var found = database is null ? null : HostApplicationServices.Current.FindFile(name, database, FindFileHint.Default);
            if (!string.IsNullOrEmpty(found))
                return found;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
        }

        throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Gabarit « {name} » introuvable dans le dossier des gabarits{(templateFolder is null ? "" : $" ({templateFolder})")} " +
            "ni dans les chemins de recherche d'AutoCAD : indiquez son chemin complet.");
    }

    /// <summary>Dossier des gabarits des options d'AutoCAD, ou null s'il n'est pas lisible.</summary>
    private static string? TemplateFolder()
    {
        try
        {
            dynamic preferences = AcApp.Preferences;
            var folder = Environment.ExpandEnvironmentVariables((string)preferences.Files.TemplateDwgPath);
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Faux si le fichier est en lecture seule ou tenu en écriture par un autre programme.</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

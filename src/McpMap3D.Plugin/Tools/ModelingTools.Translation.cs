using System.Diagnostics;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Import de modèles 3D STEP et IGES, comme la commande IMPORT. Il n'existe pas d'API .NET pour ces formats : le
/// traducteur d'AutoCAD (acTranslators.exe, celui que lancent IMPORT et acCoreConsole) convertit le fichier en DWG
/// dans un dossier temporaire, hors du thread principal ; le DWG est ensuite inséré comme un bloc, en une étape
/// d'annulation. Le serveur MCP enchaîne les deux temps dans l'outil import_3d_model.
/// </summary>
internal static partial class ModelingTools
{
    private const string TranslatorExe = "acTranslators.exe";
    private static readonly string[] TranslatedExtensions = [".stp", ".step", ".igs", ".iges"];
    private static readonly string TranslationRoot = Path.Combine(Path.GetTempPath(), "McpMap3D", "import3d");

    /// <summary>
    /// Premier temps, hors du thread principal (accès Background) : conversion du fichier en DWG temporaire.
    /// N'utilise aucun objet AutoCAD. Le chemin doit être absolu, le dossier du dessin n'étant pas lisible ici.
    /// </summary>
    public static object ConvertModel(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var path = reader.RequireString("filePath");
        if (!Path.IsPathFullyQualified(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Chemin absolu attendu pour le fichier à importer : « {path} ».");

        if (!File.Exists(path))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} n'existe pas.");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!TranslatedExtensions.Contains(extension))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Format « {extension} » non pris en charge : fichiers STEP (.stp, .step) ou IGES (.igs, .iges) attendus " +
                "(un fichier ACIS .sat s'importe avec import_sat).");

        var translator = Path.Combine(AppContext.BaseDirectory, TranslatorExe);
        if (!File.Exists(translator))
        {
            var acad = Process.GetCurrentProcess().MainModule?.FileName;
            translator = Path.Combine(Path.GetDirectoryName(acad) ?? "", TranslatorExe);
            if (!File.Exists(translator))
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Traducteur {TranslatorExe} introuvable dans le dossier d'AutoCAD ({Path.GetDirectoryName(translator)}) : " +
                    "l'import STEP/IGES de cette installation n'est pas disponible.");
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(reader.GetDouble("timeoutSeconds", 300), 10, 1800));
        var folder = Path.Combine(TranslationRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".dwg");

        // Mêmes options que celles qu'acCoreConsole passe au traducteur : -p désigne le processus AutoCAD appelant,
        // -l la langue du produit ; -t et -m reprennent les valeurs observées, non documentées.
        var start = new ProcessStartInfo(translator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
        };
        foreach (var argument in new[]
                 {
                     "-i", path, "-o", output, "-t", "0", "-m", "0",
                     "-p", Environment.ProcessId.ToString(), "-l", PluginApp.ProductLcid.ToString(),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        var clock = Stopwatch.StartNew();
        int exitCode;
        string messages;
        try
        {
            using var process = Process.Start(start)
                ?? throw new PipeException(PipeErrorCodes.Internal, $"Le traducteur {translator} n'a pas démarré.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Terminé entre-temps.
                }

                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Conversion de {Path.GetFileName(path)} interrompue après {timeout.TotalSeconds:0} s : augmentez " +
                    "timeoutSeconds pour un gros assemblage.");
            }

            process.WaitForExit();
            exitCode = process.ExitCode;
            messages = string.Join(" ", new[] { stdout.Result, stderr.Result }.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        }
        catch
        {
            DeleteTranslationFolder(output);
            throw;
        }

        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            DeleteTranslationFolder(output);
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le traducteur d'AutoCAD n'a pas pu convertir {path} (code de sortie {exitCode})" +
                (messages.Length > 0 ? $" : {Format.Truncate(messages, 500)}" : ".") +
                " Vérifiez que le fichier s'ouvre avec la commande IMPORT.");
        }

        return new
        {
            File = path,
            Dwg = output,
            Bytes = new FileInfo(output).Length,
            Seconds = Format.Number(clock.Elapsed.TotalSeconds),
            ExitCode = exitCode != 0 ? exitCode : (int?)null,
        };
    }

    /// <summary>
    /// Second temps : insertion du DWG converti dans l'espace courant, comme un bloc (ou décomposé). Par défaut,
    /// l'échelle convertit les unités du fichier (INSUNITS du DWG converti) vers celles du dessin.
    /// </summary>
    public static object InsertModel(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var dwg = reader.RequireString("dwgPath");
        try
        {
            return InsertModel(context, reader, dwg);
        }
        finally
        {
            if (reader.GetBool("deleteDwg", false))
                DeleteTranslationFolder(dwg);
        }
    }

    private static object InsertModel(ToolContext context, ArgReader reader, string dwg)
    {
        var transaction = context.RequireTransaction();
        var database = context.Database;
        if (!File.Exists(dwg))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier converti {dwg} n'existe pas.");

        var sourceFile = reader.GetString("sourceFile") ?? dwg;
        using var source = new Database(false, true);
        try
        {
            source.ReadDwgFile(dwg, FileOpenMode.OpenForReadAndAllShare, true, "");
            source.CloseInput(true);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu lire le fichier converti {dwg} ({ex.ErrorStatus}).");
        }

        var sourceUnits = source.Insunits;
        var drawingUnits = database.Insunits;
        double scale;
        if (reader.Has("scale"))
        {
            scale = reader.GetDouble("scale", 1);
            if (!(scale > 0))
                throw new PipeException(PipeErrorCodes.InvalidParams, "L'échelle doit être strictement positive.");
        }
        else
        {
            scale = sourceUnits != UnitsValue.Undefined && drawingUnits != UnitsValue.Undefined
                ? UnitsConverter.GetConversionFactor(sourceUnits, drawingUnits)
                : 1;
        }

        // Tout ce qui peut échouer se vérifie avant Database.Insert, qui écrit dans le dessin hors de la transaction.
        var contents = ModelSpaceContents(source);
        if (contents.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {sourceFile} ne contient aucun objet à importer.");

        if (reader.GetString("layer") is string layer
            && !((LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead)).Has(layer))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le calque « {layer} » n'existe pas. Créez-le d'abord avec create_layer.");

        if (reader.GetString("color") is string color)
            EditTools.ParseColor(color);

        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var name = ModelBlockName(blocks, reader.GetString("blockName"), sourceFile);
        var existingBlocks = new HashSet<ObjectId>(blocks.Cast<ObjectId>());
        ObjectId blockId;
        try
        {
            blockId = database.Insert(name, source, true);
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu insérer le modèle converti ({ex.ErrorStatus}).");
        }

        var position = reader.GetPoint("position", Point3d.Origin);
        var reference = new BlockReference(position, blockId) { ScaleFactors = new Scale3d(scale) };
        var common = new
        {
            File = sourceFile,
            SourceUnits = sourceUnits.ToString(),
            DrawingUnits = drawingUnits.ToString(),
            Scale = Format.Number(scale),
            Contents = contents,
        };

        if (!reader.GetBool("explode", false))
        {
            var appended = EditTools.Append(context, reference, reader);
            return new
            {
                common.File,
                Block = name,
                Reference = appended,
                common.SourceUnits,
                common.DrawingUnits,
                common.Scale,
                common.Contents,
                Extents = Format.Extents(Format.TryGetExtents(reference)),
            };
        }

        var pieces = new List<Entity>();
        try
        {
            ExplodeToLeaves(reference, pieces, 0);
        }
        catch (AcException ex)
        {
            foreach (var piece in pieces)
                piece.Dispose();

            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"AutoCAD n'a pas pu décomposer le modèle ({ex.ErrorStatus}) : importez-le sans explode.");
        }

        var created = new List<object>();
        Extents3d? extents = null;
        foreach (var entity in pieces)
        {
            created.Add(EditTools.Append(context, entity, reader));
            if (Format.TryGetExtents(entity) is Extents3d box)
            {
                if (extents is Extents3d union)
                {
                    union.AddExtents(box);
                    extents = union;
                }
                else
                {
                    extents = box;
                }
            }
        }

        // Le bloc du modèle et ses blocs imbriqués créés par l'import (pièces d'un assemblage) ne servent plus.
        var nestedBlocks = NestedBlocks(transaction, blockId);
        transaction.GetObject(blockId, OpenMode.ForWrite).Erase();
        foreach (var nestedId in nestedBlocks)
        {
            var nested = (BlockTableRecord)transaction.GetObject(nestedId, OpenMode.ForRead);
            if (!existingBlocks.Contains(nestedId) && nested.GetBlockReferenceIds(true, false).Count == 0)
            {
                nested.UpgradeOpen();
                nested.Erase();
            }
        }

        return new
        {
            common.File,
            Count = created.Count,
            Objects = created.Take(MaxListedImports).ToArray(),
            Truncated = created.Count > MaxListedImports ? true : (bool?)null,
            common.SourceUnits,
            common.DrawingUnits,
            common.Scale,
            common.Contents,
            Extents = Format.Extents(extents),
        };
    }

    /// <summary>
    /// Décompose une référence jusqu'aux objets de base : les pièces d'un assemblage, et le bloc dans lequel le
    /// traducteur range un modèle, sont des blocs imbriqués. La référence passée est libérée.
    /// </summary>
    private static void ExplodeToLeaves(BlockReference reference, List<Entity> leaves, int depth)
    {
        var pieces = new DBObjectCollection();
        try
        {
            reference.Explode(pieces);
        }
        finally
        {
            reference.Dispose();
        }

        foreach (DBObject piece in pieces)
        {
            if (piece is BlockReference nested && depth < 20)
                ExplodeToLeaves(nested, leaves, depth + 1);
            else if (piece is Entity entity)
                leaves.Add(entity);
            else
                piece.Dispose();
        }
    }

    /// <summary>Définitions de blocs référencées, directement ou non, par un bloc.</summary>
    private static HashSet<ObjectId> NestedBlocks(Transaction transaction, ObjectId blockId)
    {
        var found = new HashSet<ObjectId>();
        var pending = new Stack<ObjectId>([blockId]);
        while (pending.Count > 0)
        {
            foreach (var id in (BlockTableRecord)transaction.GetObject(pending.Pop(), OpenMode.ForRead))
            {
                if (id.ObjectClass.DxfName == "INSERT"
                    && transaction.GetObject(id, OpenMode.ForRead) is BlockReference child && found.Add(child.BlockTableRecord))
                    pending.Push(child.BlockTableRecord);
            }
        }

        return found;
    }

    /// <summary>Nombre d'objets de l'espace objet du DWG converti, par type.</summary>
    private static SortedDictionary<string, int> ModelSpaceContents(Database source)
    {
        var contents = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var transaction = source.TransactionManager.StartOpenCloseTransaction();
        var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForRead);
        foreach (var id in space)
        {
            var type = id.ObjectClass.DxfName;
            contents[type] = contents.GetValueOrDefault(type) + 1;
        }

        transaction.Commit();
        return contents;
    }

    /// <summary>Nom du bloc : celui demandé (refusé s'il existe), sinon le nom du fichier, suffixé s'il est pris.</summary>
    private static string ModelBlockName(BlockTable blocks, string? requested, string sourceFile)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                SymbolUtilityServices.ValidateSymbolName(requested, false);
            }
            catch (AcException)
            {
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Nom de bloc invalide : « {requested} ».");
            }

            return !blocks.Has(requested)
                ? requested
                : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le bloc « {requested} » existe déjà dans le dessin.");
        }

        var baseName = SymbolUtilityServices.RepairSymbolName(Path.GetFileNameWithoutExtension(sourceFile), false);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Modele3D";

        var name = baseName;
        for (var index = 2; blocks.Has(name); index++)
            name = $"{baseName}_{index}";

        return name;
    }

    /// <summary>Supprime le dossier temporaire d'une conversion, jamais un fichier situé ailleurs.</summary>
    private static void DeleteTranslationFolder(string dwg)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(dwg));
            if (folder is not null && Path.GetDirectoryName(folder) is string parent
                && string.Equals(parent, TranslationRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"Dossier de conversion non supprimé ({dwg}) : {ex.Message}");
        }
    }
}

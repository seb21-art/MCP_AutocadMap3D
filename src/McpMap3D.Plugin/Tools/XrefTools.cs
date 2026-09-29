using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Références externes (Xref) de dessins DWG : liste, attache ou superposition, détachement, rechargement,
/// déchargement, liaison et modification (chemin, nom, type). Les images, PDF, DWF et DGN sous-jacents ne sont pas
/// concernés. Détacher, recharger, décharger et lier passent par la base de données une fois la transaction de l'outil
/// validée (AfterCommit), comme le font les exemples d'Autodesk : ces méthodes gèrent elles-mêmes les objets qu'elles
/// ouvrent.
/// </summary>
internal static class XrefTools
{
    private const int MaxListedReferences = 50;

    /// <summary>Xref du dessin, lues dans le graphe des références externes et dans la table des blocs.</summary>
    private sealed record XrefInfo(
        string Name, ObjectId BlockId, XrefStatus Status, bool IsNested, string? LoadedFrom, IReadOnlyList<string> Parents);

    public static object ListXrefs(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var transaction = context.RequireTransaction();
        var xrefs = ReadXrefs(context.Database).Where(xref => filter is null || filter.IsMatch(xref.Name)).ToList();
        return new
        {
            HostDrawing = context.Database.Filename,
            Count = xrefs.Count,
            Xrefs = xrefs.Select(xref => Describe(xref, transaction)).ToList(),
        };
    }

    /// <summary>
    /// Attache (ou superpose) un dessin DWG et insère une référence dans l'espace courant. Si une Xref du même nom
    /// désigne déjà ce fichier, sa définition est réutilisée, comme le fait la commande ATTACHER.
    /// </summary>
    public static object AttachXref(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var file = RequireDwg(context, reader.RequireString("filePath"));
        if (string.Equals(file, FullHostPath(context), StringComparison.OrdinalIgnoreCase))
            throw Invalid("Un dessin ne peut pas se référencer lui-même.");

        var overlay = ReadType(reader.GetString("type") ?? "attach", "type");
        var name = (reader.GetString("name") ?? Path.GetFileNameWithoutExtension(file)).Trim();
        ValidateName(name);
        var storedPath = StoredPath(context, file, reader.GetString("pathType") ?? "auto", out var pathWarning);

        var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        ObjectId blockId;
        var reused = false;
        if (blocks.Has(name))
        {
            var existing = (BlockTableRecord)transaction.GetObject(blocks[name], OpenMode.ForRead);
            if (!existing.IsFromExternalReference || existing.IsDependent)
                throw Invalid($"Le nom « {name} » est déjà celui d'un bloc du dessin : choisissez-en un autre avec « name ».");

            var existingFile = ReadXrefs(database).FirstOrDefault(xref => xref.BlockId == existing.ObjectId)?.LoadedFrom
                ?? ResolveStoredPath(context, existing.PathName);
            if (!string.Equals(existingFile, file, StringComparison.OrdinalIgnoreCase))
                throw Invalid($"La Xref « {name} » désigne déjà un autre fichier ({existing.PathName}) : choisissez un autre " +
                              "nom, ou changez son chemin avec edit_xref.");

            blockId = existing.ObjectId;
            reused = true;
        }
        else
        {
            try
            {
                blockId = overlay ? database.OverlayXref(file, name) : database.AttachXref(file, name);
            }
            catch (AcException ex)
            {
                throw Invalid($"AutoCAD n'a pas pu attacher {file} ({ex.ErrorStatus}).");
            }

            if (blockId.IsNull)
                throw Invalid($"AutoCAD n'a pas pu attacher {file}.");

            var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForWrite);
            if (!string.Equals(block.PathName, storedPath, StringComparison.OrdinalIgnoreCase))
                block.PathName = storedPath;
        }

        var reference = new BlockReference(reader.GetPoint("position", Point3d.Origin), blockId)
        {
            ScaleFactors = BlockTools.ReadScale(reader),
            Rotation = Format.Radians(reader.GetDouble("rotation", 0)),
        };
        EditTools.Append(context, reference, reader);

        // GetBlockReferenceIds ne voit la nouvelle insertion qu'une fois la transaction validée.
        var handle = reference.Handle.ToString();
        var layer = reference.Layer;
        context.ResultAfterCommit = () => new
        {
            Handle = handle,
            Layer = layer,
            ExistingDefinition = reused ? true : (bool?)null,
            Xref = DescribeCommitted(database, blockId),
            Warning = pathWarning,
        };

        return new { Handle = handle };
    }

    public static object DetachXrefs(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var xrefs = Select(context, reader, "détacher", topLevelOnly: true);
        var transaction = context.RequireTransaction();
        var references = xrefs.Sum(xref => ((BlockTableRecord)transaction.GetObject(xref.BlockId, OpenMode.ForRead))
            .GetBlockReferenceIds(true, false).Count);

        var database = context.Database;
        context.AfterCommit(() =>
        {
            foreach (var xref in xrefs)
                Run($"détacher « {xref.Name} »", () => database.DetachXref(xref.BlockId));
        });

        return new
        {
            Detached = xrefs.Select(xref => xref.Name).ToList(),
            ErasedReferences = references,
        };
    }

    public static object ReloadXrefs(ToolContext context, JsonElement? args) =>
        LoadOrUnload(context, args, reload: true);

    public static object UnloadXrefs(ToolContext context, JsonElement? args) =>
        LoadOrUnload(context, args, reload: false);

    /// <summary>
    /// Lie des Xref : leur contenu devient un bloc ordinaire du dessin. En mode « bind », les calques et styles
    /// gardent le préfixe NOM$0$ ; en mode « insert », ils se fondent dans ceux du dessin, comme une insertion.
    /// </summary>
    public static object BindXrefs(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var mode = (reader.GetString("mode") ?? "bind").ToLowerInvariant();
        if (mode is not ("bind" or "insert"))
            throw Invalid($"Mode « {mode} » inconnu : bind (préfixe NOM$0$ conservé) ou insert (fusion avec le dessin).");

        var xrefs = Select(context, reader, "lier", topLevelOnly: true);
        var unresolved = xrefs.Where(xref => xref.Status != XrefStatus.Resolved).ToList();
        if (unresolved.Count > 0)
            throw Invalid("Seules les Xref chargées peuvent être liées : " +
                          string.Join(", ", unresolved.Select(xref => $"{xref.Name} ({xref.Status})")) +
                          ". Rechargez-les d'abord avec reload_xrefs, ou corrigez leur chemin avec edit_xref.");

        var database = context.Database;
        var ids = new ObjectIdCollection(xrefs.Select(xref => xref.BlockId).ToArray());
        context.AfterCommit(() => Run("lier les Xref", () => database.BindXrefs(ids, mode == "insert")));
        context.ResultAfterCommit = () =>
        {
            using var transaction = database.TransactionManager.StartTransaction();
            // La définition de la Xref devient le bloc lié, sous le même identifiant.
            var blocks = xrefs.Select(xref =>
            {
                var block = xref.BlockId.IsErased ? null : (BlockTableRecord)transaction.GetObject(xref.BlockId, OpenMode.ForRead);
                return new
                {
                    Xref = xref.Name,
                    Block = block?.Name,
                    StillXref = block is { IsFromExternalReference: true } ? true : (bool?)null,
                    Insertions = block?.GetBlockReferenceIds(true, false).Count,
                };
            }).ToList();
            transaction.Commit();
            return new { Mode = mode, Bound = blocks, RemainingXrefs = ReadXrefs(database).Select(xref => xref.Name).ToList() };
        };

        return new { Mode = mode };
    }

    /// <summary>Change le chemin, le nom ou le type (attache ou superposition) d'une Xref, puis la recharge.</summary>
    public static object EditXref(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var xref = Find(context, reader.RequireString("name"));
        if (!new[] { "filePath", "pathType", "type", "newName", "reload" }.Any(reader.Has))
            throw Invalid("Rien à modifier : indiquez filePath, pathType, type, newName ou reload.");

        var block = (BlockTableRecord)transaction.GetObject(xref.BlockId, OpenMode.ForRead);
        var changes = new List<string>();
        string? pathWarning = null;

        if (reader.Has("filePath") || reader.Has("pathType"))
        {
            if (xref.IsNested)
                throw Invalid($"« {xref.Name} » est une Xref imbriquée : son chemin se change dans le dessin qui l'attache.");

            var file = reader.Has("filePath")
                ? RequireDwg(context, reader.RequireString("filePath"))
                : xref.LoadedFrom ?? ResolveStoredPath(context, block.PathName)
                  ?? throw Invalid($"Le fichier de « {xref.Name} » est introuvable ({block.PathName}) : indiquez « filePath ».");
            if (!File.Exists(file))
                throw Invalid($"Le fichier {file} n'existe pas.");
            if (string.Equals(file, FullHostPath(context), StringComparison.OrdinalIgnoreCase))
                throw Invalid("Un dessin ne peut pas se référencer lui-même.");

            var storedPath = StoredPath(context, file, reader.GetString("pathType") ?? "auto", out pathWarning);
            if (!string.Equals(storedPath, block.PathName, StringComparison.Ordinal))
            {
                block.UpgradeOpen();
                block.PathName = storedPath;
                changes.Add("path");
            }
        }

        if (reader.GetString("type") is { } type)
        {
            if (xref.IsNested)
                throw Invalid($"« {xref.Name} » est une Xref imbriquée : son type se change dans le dessin qui l'attache.");

            var overlay = ReadType(type, "type");
            if (overlay != block.IsFromOverlayReference)
            {
                block.UpgradeOpen();
                block.IsFromOverlayReference = overlay;
                changes.Add("type");
            }
        }

        if (reader.GetString("newName") is { } requestedName)
        {
            var newName = requestedName.Trim();
            if (xref.IsNested)
                throw Invalid($"« {xref.Name} » est une Xref imbriquée : elle ne peut pas être renommée ici.");

            if (!string.Equals(newName, block.Name, StringComparison.Ordinal))
            {
                ValidateName(newName);
                var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                if (blocks.Has(newName) && blocks[newName] != xref.BlockId)
                    throw Invalid($"Le nom « {newName} » est déjà pris par un bloc ou une Xref du dessin.");

                block.UpgradeOpen();
                block.Name = newName;
                changes.Add("name");
            }
        }

        // Un nouveau chemin ne prend effet qu'au rechargement ; une Xref déchargée le reste si reload est faux.
        var reload = reader.GetBool("reload", changes.Contains("path"));
        var blockId = xref.BlockId;
        string? reloadError = null;
        if (reload)
            context.AfterCommit(() => reloadError = TryReload(database, blockId));

        context.ResultAfterCommit = () => new
        {
            Changed = changes,
            Reloaded = reload && reloadError is null,
            ReloadError = reloadError,
            Xref = DescribeCommitted(database, blockId),
            Warning = pathWarning,
        };

        return new { Changed = changes };
    }

    private static object LoadOrUnload(ToolContext context, JsonElement? args, bool reload)
    {
        var reader = new ArgReader(args);
        var xrefs = Select(context, reader, reload ? "recharger" : "décharger", topLevelOnly: true, allWhenOmitted: reload);
        if (xrefs.Count == 0)
            return new { Count = 0, Xrefs = Array.Empty<object>() };

        var database = context.Database;
        var failures = new Dictionary<ObjectId, string>();
        if (reload)
        {
            // Une par une : ReloadXrefs lève une exception (FileAccessErr…) pour un fichier introuvable, ce qui
            // arrêterait les suivantes ; l'échec est signalé dans le résultat au lieu de faire échouer l'appel.
            context.AfterCommit(() =>
            {
                foreach (var xref in xrefs)
                {
                    if (TryReload(database, xref.BlockId) is { } error)
                        failures[xref.BlockId] = error;
                }
            });
        }
        else
        {
            var ids = new ObjectIdCollection(xrefs.Select(xref => xref.BlockId).ToArray());
            context.AfterCommit(() => Run("décharger les Xref", () => database.UnloadXrefs(ids)));
        }

        context.ResultAfterCommit = () =>
        {
            var selected = xrefs.Select(xref => xref.BlockId).ToHashSet();
            var after = ReadXrefs(database).Where(xref => selected.Contains(xref.BlockId)).ToList();
            return new
            {
                Count = after.Count,
                Xrefs = after.Select(xref => new
                {
                    xref.Name,
                    Status = xref.Status.ToString(),
                    xref.LoadedFrom,
                    ReloadError = failures.GetValueOrDefault(xref.BlockId),
                }).ToList(),
                NotLoaded = reload && after.Any(xref => xref.Status != XrefStatus.Resolved)
                    ? after.Where(xref => xref.Status != XrefStatus.Resolved).Select(xref => xref.Name).ToList()
                    : null,
            };
        };

        return new { Count = xrefs.Count };
    }

    /// <summary>Xref désignées par « names » (jokers * et ? acceptés), toutes si omis et <paramref name="allWhenOmitted"/>.</summary>
    private static List<XrefInfo> Select(ToolContext context, ArgReader reader, string action, bool topLevelOnly, bool allWhenOmitted = false)
    {
        var patterns = reader.GetStrings("names");
        var xrefs = ReadXrefs(context.Database);
        if (patterns.Count == 0)
        {
            if (!allWhenOmitted)
                throw Invalid("Le paramètre « names » est obligatoire : noms des Xref (voir list_xrefs), jokers * et ? acceptés.");

            return topLevelOnly ? xrefs.Where(xref => !xref.IsNested).ToList() : xrefs;
        }

        var selected = new List<XrefInfo>();
        foreach (var pattern in patterns)
        {
            var filter = NameFilter.Create([pattern])!;
            var matches = xrefs.Where(xref => filter.IsMatch(xref.Name)).ToList();
            if (matches.Count == 0)
                throw Invalid($"Aucune Xref ne correspond à « {pattern} ». Xref du dessin : " +
                              (xrefs.Count > 0 ? string.Join(", ", xrefs.Select(xref => xref.Name)) : "aucune") + ".");

            selected.AddRange(matches.Where(xref => !selected.Contains(xref)));
        }

        var nested = topLevelOnly ? selected.Where(xref => xref.IsNested).ToList() : [];
        if (nested.Count > 0)
            throw Invalid($"Impossible de {action} une Xref imbriquée ({string.Join(", ", nested.Select(xref => xref.Name))}) : " +
                          "agissez sur la Xref qui la contient (voir parents dans list_xrefs).");

        return selected;
    }

    private static XrefInfo Find(ToolContext context, string name)
    {
        var xrefs = ReadXrefs(context.Database);
        return xrefs.FirstOrDefault(xref => string.Equals(xref.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid($"Aucune Xref « {name} » dans le dessin. Xref du dessin : " +
                             (xrefs.Count > 0 ? string.Join(", ", xrefs.Select(xref => xref.Name)) : "aucune") + ".");
    }

    /// <summary>Parcourt le graphe des Xref du dessin : le nœud 0 est le dessin lui-même.</summary>
    private static List<XrefInfo> ReadXrefs(Database database)
    {
        using var graph = database.GetHostDwgXrefGraph(true);
        var xrefs = new List<XrefInfo>();
        for (var i = 1; i < graph.NumNodes; i++)
        {
            var node = graph.GetXrefNode(i);
            if (node.BlockTableRecordId.IsNull || node.BlockTableRecordId.IsErased)
                continue;

            var parents = new List<string>();
            for (var j = 0; j < node.NumIn; j++)
            {
                // Le dessin hôte, parent des Xref directes, n'a pas de définition de bloc.
                if (node.In(j) is XrefGraphNode parent && !parent.BlockTableRecordId.IsNull)
                    parents.Add(parent.Name);
            }

            string? loadedFrom = null;
            if (node.XrefStatus == XrefStatus.Resolved && node.Database is { } xrefDatabase)
                loadedFrom = string.IsNullOrEmpty(xrefDatabase.Filename) ? null : xrefDatabase.Filename;

            xrefs.Add(new XrefInfo(node.Name, node.BlockTableRecordId, node.XrefStatus, node.IsNested, loadedFrom, parents));
        }

        return xrefs.OrderBy(xref => xref.IsNested).ThenBy(xref => xref.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static object Describe(XrefInfo xref, Transaction transaction)
    {
        var block = (BlockTableRecord)transaction.GetObject(xref.BlockId, OpenMode.ForRead);
        var references = new List<object>();
        var ids = block.GetBlockReferenceIds(true, false);
        foreach (ObjectId id in ids)
        {
            if (references.Count >= MaxListedReferences)
                break;

            if (transaction.GetObject(id, OpenMode.ForRead) is not BlockReference reference)
                continue;

            var owner = (BlockTableRecord)transaction.GetObject(reference.OwnerId, OpenMode.ForRead);
            references.Add(new
            {
                Handle = reference.Handle.ToString(),
                Position = Format.Point(reference.Position),
                Scale = Format.Point(new Point3d(reference.ScaleFactors.X, reference.ScaleFactors.Y, reference.ScaleFactors.Z)),
                Rotation = Format.Degrees(reference.Rotation),
                reference.Layer,
                Space = owner.IsLayout ? SpaceName(owner, transaction) : $"bloc {owner.Name}",
            });
        }

        return new
        {
            xref.Name,
            Type = block.IsFromOverlayReference ? "overlay" : "attach",
            Status = xref.Status.ToString(),
            SavedPath = block.PathName,
            PathType = PathKind(block.PathName),
            xref.LoadedFrom,
            Nested = xref.IsNested ? true : (bool?)null,
            Parents = xref.Parents.Count > 0 ? xref.Parents : null,
            ReferenceCount = ids.Count,
            References = references,
            ReferencesTruncated = ids.Count > references.Count ? true : (bool?)null,
        };
    }

    private static string SpaceName(BlockTableRecord space, Transaction transaction)
    {
        var layout = (Layout)transaction.GetObject(space.LayoutId, OpenMode.ForRead);
        return layout.ModelType ? "model" : $"présentation {layout.LayoutName}";
    }

    private static string PathKind(string path) =>
        string.IsNullOrEmpty(path) ? "none"
        : path.StartsWith(".", StringComparison.Ordinal) ? "relative"
        : Path.IsPathFullyQualified(path) ? "full"
        : Path.GetFileName(path) == path ? "none"
        : "relative";

    /// <summary>Recharge une Xref ; renvoie l'erreur d'AutoCAD (fichier introuvable…) au lieu de la lever.</summary>
    private static string? TryReload(Database database, ObjectId blockId)
    {
        try
        {
            database.ReloadXrefs(new ObjectIdCollection([blockId]));
            return null;
        }
        catch (AcException ex)
        {
            return ex.ErrorStatus.ToString();
        }
    }

    /// <summary>Xref décrite dans une transaction à part, une fois celle de l'outil validée.</summary>
    private static object? DescribeCommitted(Database database, ObjectId blockId)
    {
        using var transaction = database.TransactionManager.StartTransaction();
        var xref = ReadXrefs(database).FirstOrDefault(item => item.BlockId == blockId);
        var result = xref is null ? null : Describe(xref, transaction);
        transaction.Commit();
        return result;
    }

    private static void Run(string action, Action operation)
    {
        try
        {
            operation();
        }
        catch (AcException ex)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams, $"AutoCAD n'a pas pu {action} ({ex.ErrorStatus}).");
        }
    }

    private static bool ReadType(string type, string parameter) => type.ToLowerInvariant() switch
    {
        "attach" => false,
        "overlay" => true,
        _ => throw Invalid($"« {parameter} » vaut attach (Xref suivie dans les dessins qui attachent celui-ci) ou overlay (superposée, ignorée par eux)."),
    };

    private static void ValidateName(string name)
    {
        try
        {
            SymbolUtilityServices.ValidateSymbolName(name, allowVerticalBar: false);
        }
        catch (AcException)
        {
            throw Invalid($"« {name} » n'est pas un nom de Xref valide.");
        }
    }

    /// <summary>Fichier DWG existant ; un chemin relatif part du dossier du dessin hôte s'il est enregistré.</summary>
    private static string RequireDwg(ToolContext context, string requested)
    {
        var path = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
        var file = FullHostPath(context) is { } host
            ? Path.GetFullPath(path, Path.GetDirectoryName(host)!)
            : Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(file), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw Invalid($"{file} n'est pas un fichier DWG : seules les Xref de dessins DWG sont gérées.");

        return File.Exists(file) ? file : throw Invalid($"Le fichier {file} n'existe pas.");
    }

    private static string? FullHostPath(ToolContext context) =>
        context.RequireDocument().IsNamedDrawing ? Path.GetFullPath(context.Database.Filename) : null;

    /// <summary>
    /// Chemin enregistré dans le dessin : complet (full), relatif au dessin hôte (relative, qui doit être enregistré)
    /// ou nom de fichier seul (none, retrouvé par les chemins de recherche). « auto » choisit relatif quand c'est
    /// possible, comme ATTACHER par défaut.
    /// </summary>
    private static string StoredPath(ToolContext context, string file, string pathType, out string? warning)
    {
        warning = null;
        var host = FullHostPath(context);
        switch (pathType.ToLowerInvariant())
        {
            case "full":
                return file;
            case "none":
                return Path.GetFileName(file);
            case "relative" or "auto":
                if (host is null)
                {
                    if (pathType.Equals("relative", StringComparison.OrdinalIgnoreCase))
                        throw Invalid("Un chemin relatif demande un dessin hôte enregistré : enregistrez-le d'abord (save_drawing) ou utilisez pathType full.");
                    return file;
                }

                var relative = Path.GetRelativePath(Path.GetDirectoryName(host)!, file);
                if (Path.IsPathFullyQualified(relative))
                {
                    if (pathType.Equals("relative", StringComparison.OrdinalIgnoreCase))
                        warning = "Le fichier est sur un autre lecteur que le dessin hôte : chemin complet enregistré.";
                    return file;
                }

                return relative.StartsWith("..", StringComparison.Ordinal) ? relative : ".\\" + relative;
            default:
                throw Invalid($"pathType « {pathType} » inconnu : auto, full, relative ou none.");
        }
    }

    /// <summary>Fichier désigné par un chemin enregistré : complet, ou relatif au dossier du dessin hôte.</summary>
    private static string? ResolveStoredPath(ToolContext context, string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            return null;

        if (Path.IsPathFullyQualified(storedPath))
            return Path.GetFullPath(storedPath);

        return FullHostPath(context) is { } host
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host)!, storedPath))
            : null;
    }

    private static PipeException Invalid(string message) => new(PipeErrorCodes.InvalidParams, message);
}

using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using McpMap3D.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Entretien du dessin : purge des éléments nommés inutilisés (comme PURGE) et contrôle d'intégrité (comme AUDIT).
/// </summary>
internal static class MaintenanceTools
{
    private const int MaxNamesPerType = 200;
    private const int MaxPasses = 10;

    private static readonly string[] AllTypes =
        ["layers", "blocks", "linetypes", "textStyles", "dimensionStyles", "tableStyles", "multileaderStyles", "multilineStyles"];

    /// <summary>
    /// Supprime les éléments nommés que rien n'utilise. Plusieurs passes : purger un bloc peut libérer les calques,
    /// types de ligne ou blocs imbriqués qu'il utilisait. Les éléments des références externes ne sont pas touchés.
    /// </summary>
    public static object PurgeDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var requested = reader.GetStrings("types");
        foreach (var type in requested.Where(type => !AllTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Type « {type} » inconnu : {string.Join(", ", AllTypes)}.");
        var types = requested.Count > 0
            ? AllTypes.Where(type => requested.Contains(type, StringComparer.OrdinalIgnoreCase)).ToArray()
            : AllTypes;
        var filter = NameFilter.Create(reader.GetStrings("names"));
        var dryRun = reader.GetBool("dryRun", false);

        var purged = types.ToDictionary(type => type, _ => new List<string>());
        var passes = 0;
        while (passes < MaxPasses)
        {
            var candidates = new Dictionary<ObjectId, (string Type, string Name)>();
            foreach (var type in types)
                foreach (var (id, name) in Candidates(database, transaction, type))
                    if (filter is null || filter.IsMatch(name))
                        candidates[id] = (type, name);
            if (candidates.Count == 0)
                break;

            var ids = new ObjectIdCollection(candidates.Keys.ToArray());
            database.Purge(ids);
            if (ids.Count == 0)
                break;
            passes++;
            foreach (ObjectId id in ids)
            {
                var (type, name) = candidates[id];
                purged[type].Add(name);
                if (!dryRun)
                    transaction.GetObject(id, OpenMode.ForWrite).Erase();
            }

            // En simulation, rien n'est effacé : une seconde passe redonnerait les mêmes éléments.
            if (dryRun)
                break;
        }

        var total = purged.Values.Sum(list => list.Count);
        return new
        {
            DryRun = dryRun ? true : (bool?)null,
            Purged = total,
            Passes = passes,
            Items = purged.Where(item => item.Value.Count > 0).ToDictionary(
                item => item.Key,
                item => new
                {
                    Count = item.Value.Count,
                    Names = item.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(MaxNamesPerType).ToList(),
                }),
            Note = dryRun && total > 0
                ? "Simulation : purger ces éléments peut en libérer d'autres (calques ou blocs utilisés seulement par eux)."
                : null,
        };
    }

    /// <summary>
    /// Contrôle d'intégrité du dessin, comme AUDIT. Le rapport (fichier .adt à côté du dessin, AUDITCTL activé le
    /// temps de l'appel) est relu et renvoyé. Les corrections, avec fixErrors, se font hors transaction.
    /// </summary>
    public static object AuditDrawing(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var fixErrors = reader.GetBool("fixErrors", false);
        var document = context.RequireDocument();
        var database = context.Database;
        var report = ReportPath(document);
        var started = DateTime.UtcNow.AddSeconds(-2);

        context.AfterCommit(() =>
        {
            var previous = AcApp.GetSystemVariable("AUDITCTL");
            AcApp.SetSystemVariable("AUDITCTL", 1);
            try
            {
                database.Audit(fixErrors, true);
            }
            finally
            {
                AcApp.SetSystemVariable("AUDITCTL", previous);
            }
        });

        context.ResultAfterCommit = () =>
        {
            string[]? lines = null;
            if (report is not null && File.Exists(report) && File.GetLastWriteTimeUtc(report) >= started)
                lines = File.ReadAllLines(report).Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToArray();
            return new
            {
                FixErrors = fixErrors,
                Report = lines is null ? null : report,
                Lines = lines?.Length > 60 ? lines[^60..] : lines,
                Note = lines is null
                    ? "Contrôle effectué ; AutoCAD n'a pas laissé de rapport .adt (dessin jamais enregistré ?). Le détail s'affiche sur sa ligne de commande."
                    : null,
            };
        };
        return new { };
    }

    private static string? ReportPath(Document document)
    {
        var name = document.Database.Filename;
        if (string.IsNullOrEmpty(name) || !Path.IsPathRooted(name))
            name = document.Name;
        return Path.IsPathRooted(name) ? Path.ChangeExtension(name, ".adt") : null;
    }

    private static IEnumerable<(ObjectId Id, string Name)> Candidates(Database database, Transaction transaction, string type)
    {
        switch (type)
        {
            case "layers":
                return Symbols(database.LayerTableId, transaction);
            case "linetypes":
                return Symbols(database.LinetypeTableId, transaction);
            case "textStyles":
                return Symbols(database.TextStyleTableId, transaction);
            case "dimensionStyles":
                return Symbols(database.DimStyleTableId, transaction);
            case "blocks":
                return Blocks(database, transaction);
            case "tableStyles":
                return Dictionary(database.TableStyleDictionaryId, transaction);
            case "multileaderStyles":
                return Dictionary(database.MLeaderStyleDictionaryId, transaction);
            case "multilineStyles":
                return Dictionary(database.MLStyleDictionaryId, transaction);
            default:
                return [];
        }
    }

    private static IEnumerable<(ObjectId, string)> Symbols(ObjectId tableId, Transaction transaction)
    {
        var table = (SymbolTable)transaction.GetObject(tableId, OpenMode.ForRead);
        foreach (var id in table)
        {
            var record = (SymbolTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!record.IsErased && !record.IsDependent && !string.IsNullOrEmpty(record.Name))
                yield return (id, record.Name);
        }
    }

    private static IEnumerable<(ObjectId, string)> Blocks(Database database, Transaction transaction)
    {
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (var id in table)
        {
            var block = (BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (!block.IsErased && !block.IsLayout && !block.IsFromExternalReference && !block.IsDependent)
                yield return (id, block.Name);
        }
    }

    private static IEnumerable<(ObjectId, string)> Dictionary(ObjectId dictionaryId, Transaction transaction)
    {
        if (dictionaryId.IsNull)
            yield break;
        var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForRead);
        foreach (var entry in dictionary)
            yield return (entry.Value, entry.Key);
    }
}

using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Données DXF brutes des objets, comme (entget) et (entmod), pour analyser des objets personnalisés (Covadis…),
/// et export du dessin en DXF. La lecture passe par acdbEntGet : le dxfOutFields de l'objet est réellement appelé.
/// </summary>
internal static class DxfTools
{
    private const int MaxHandles = 50;
    private const int MaxReferenced = 200;
    private const int MaxPairs = 20_000;

    // ---------- get_entity_dxf ----------

    public static object GetEntityDxf(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var ids = Handles.Resolve(context, reader);
        if (ids.Count > MaxHandles)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{ids.Count} handles : {MaxHandles} au plus par appel.");

        var applications = Applications(reader);
        var maxBinary = reader.GetInt("maxBinaryBytes", 256, 0, 1 << 20);
        var depth = reader.GetInt("followReferences", 0, 0, 2);
        var extension = reader.GetBool("includeExtensionDictionary", false);

        var objects = ids.Select(id => Describe(id, transaction, applications, maxBinary, extension)).ToList();

        // Objets désignés par les codes 330 à 369 (pointeurs et propriétaires), sur depth niveaux, sans doublon.
        var referenced = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<ObjectId>(ids);
        var frontier = ids.ToList();
        var truncated = false;
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<ObjectId>();
            foreach (var id in frontier)
            {
                foreach (var target in References(id, applications))
                {
                    if (!seen.Add(target))
                        continue;
                    if (referenced.Count >= MaxReferenced)
                    {
                        truncated = true;
                        break;
                    }

                    referenced[target.Handle.ToString()] = Describe(target, transaction, applications, maxBinary, false);
                    next.Add(target);
                }
            }

            frontier = next;
        }

        return new
        {
            Count = objects.Count,
            Objects = objects,
            Referenced = depth > 0 ? referenced : null,
            ReferencedTruncated = truncated ? true : (bool?)null,
            Note = "Doubles en pleine précision (aller-retour exact), points [x, y, z], noms d'entité donnés par leur handle.",
        };
    }

    private static IReadOnlyList<string>? Applications(ArgReader reader)
    {
        if (!reader.TryGet("includeXdata", out var value))
            return ["*"];
        return value.ValueKind switch
        {
            JsonValueKind.False => null,
            JsonValueKind.True => ["*"],
            JsonValueKind.String => value.GetString() is { Length: > 0 } text ? [text] : null,
            JsonValueKind.Array => reader.GetStrings("includeXdata") is { Count: > 0 } list ? list : null,
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, "includeXdata : nom d'application, liste de noms, \"*\" ou false."),
        };
    }

    private static Dictionary<string, object?> Describe(ObjectId id, Transaction transaction, IReadOnlyList<string>? applications, int maxBinary, bool extension)
    {
        var dbObject = transaction.GetObject(id, OpenMode.ForRead, true);
        var rxClass = dbObject.GetRXClass();
        var result = new Dictionary<string, object?>
        {
            ["handle"] = id.Handle.ToString(),
            ["dxfName"] = rxClass.DxfName,
            ["className"] = rxClass.Name,
            ["entity"] = dbObject is Entity,
            ["erased"] = dbObject.IsErased ? true : null,
        };

        if (dbObject is ProxyEntity proxyEntity)
            result["proxyOf"] = proxyEntity.OriginalClassName;
        else if (dbObject is ProxyObject proxyObject)
            result["proxyOf"] = proxyObject.OriginalClassName;

        using var buffer = NativeAds.Get(id, applications);
        if (buffer is null)
        {
            result["error"] = "acdbEntGet n'a rien renvoyé pour cet objet.";
            return result;
        }

        var pairs = new List<object>();
        foreach (TypedValue value in buffer)
        {
            if (pairs.Count >= MaxPairs)
            {
                result["pairsTruncated"] = true;
                break;
            }

            pairs.Add(Pair(value, maxBinary));
        }

        result["pairCount"] = pairs.Count;
        result["pairs"] = pairs;

        if (extension && !dbObject.ExtensionDictionary.IsNull)
        {
            var dictionary = (DBDictionary)transaction.GetObject(dbObject.ExtensionDictionary, OpenMode.ForRead);
            result["extensionDictionary"] = new
            {
                Handle = dictionary.ObjectId.Handle.ToString(),
                Entries = DictionaryTools.Entries(dictionary).Select(entry => new
                {
                    entry.Key,
                    Object = Describe(entry.Value, transaction, applications, maxBinary, false),
                }).ToList(),
            };
        }

        return result;
    }

    /// <summary>Paire DXF : code, type, valeur (handles pour les noms d'entité, hexadécimal pour le binaire).</summary>
    private static object Pair(TypedValue value, int maxBinary)
    {
        var code = (int)value.TypeCode;
        switch (value.Value)
        {
            case null:
                return new { Code = code, Type = "marker", Value = (object?)null };
            case string text:
                return new { Code = code, Type = "string", Value = (object?)text };
            case double number:
                return new { Code = code, Type = "real", Value = (object?)number };
            case short number:
                return new { Code = code, Type = "int16", Value = (object?)number };
            case int number:
                return new { Code = code, Type = "int32", Value = (object?)number };
            case long number:
                return new { Code = code, Type = "int64", Value = (object?)number };
            case Point3d point:
                return new { Code = code, Type = "point", Value = (object?)new[] { point.X, point.Y, point.Z } };
            case Point2d point:
                return new { Code = code, Type = "point", Value = (object?)new[] { point.X, point.Y, 0.0 } };
            case Vector3d vector:
                return new { Code = code, Type = "point", Value = (object?)new[] { vector.X, vector.Y, vector.Z } };
            case ObjectId target:
                return new { Code = code, Type = "handle", Value = (object?)(target.IsNull ? null : target.Handle.ToString()) };
            case byte[] bytes:
                var shown = bytes.Length > maxBinary ? bytes.AsSpan(0, maxBinary) : bytes;
                return new
                {
                    Code = code,
                    Type = "binary",
                    Value = (object?)System.Convert.ToHexString(shown),
                    Length = bytes.Length,
                    Truncated = bytes.Length > maxBinary ? true : (bool?)null,
                };
            default:
                return new { Code = code, Type = value.Value.GetType().Name, Value = (object?)System.Convert.ToString(value.Value, CultureInfo.InvariantCulture) };
        }
    }

    /// <summary>Objets désignés par les codes 330 à 369 et les handles des données étendues (1005).</summary>
    private static IEnumerable<ObjectId> References(ObjectId id, IReadOnlyList<string>? applications)
    {
        using var buffer = NativeAds.Get(id, applications);
        if (buffer is null)
            yield break;
        var database = id.Database;
        foreach (TypedValue value in buffer)
        {
            var code = (int)value.TypeCode;
            if (code is >= 330 and <= 369 && value.Value is ObjectId target && !target.IsNull && !target.IsErased)
                yield return target;
            else if (code == 1005 && value.Value is string handle
                     && long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
                     && database.TryGetObjectId(new Handle(number), out var linked) && !linked.IsNull && !linked.IsErased)
                yield return linked;
        }
    }

    // ---------- set_entity_dxf ----------

    /// <summary>
    /// Remplace des valeurs DXF d'un objet (entmod puis entupd), en une étape de U. Désactivé sauf réglage explicite.
    /// Avec dryRun (par défaut), rien n'est écrit : la réponse montre les valeurs avant et après.
    /// </summary>
    public static object SetEntityDxf(ToolContext context, JsonElement? args)
    {
        if (!PluginSettings.SetEntityDxfEnabled)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "set_entity_dxf est désactivé. Pour l'activer, l'utilisateur crée le fichier " +
                $"{PluginSettings.FilePath} avec {{\"enableSetEntityDxf\": true}}, ou lance AutoCAD avec la variable " +
                "d'environnement MCPMAP3D_ENABLE_SET_ENTITY_DXF=1.");

        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var id = Handles.ResolveOne(context, reader.RequireString("handle"));
        var dryRun = reader.GetBool("dryRun", true);
        var applications = Applications(reader);

        var dbObject = transaction.GetObject(id, OpenMode.ForRead);
        if (dbObject is Entity entity && ((LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"L'objet {id.Handle} est sur le calque verrouillé « {entity.Layer} » : déverrouillez-le d'abord.");

        if (!reader.TryGet("changes", out var changesElement) || changesElement.ValueKind != JsonValueKind.Array || changesElement.GetArrayLength() == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "changes : liste de { code, occurrence, value }.");

        TypedValue[] values;
        using (var buffer = NativeAds.Get(id, applications)
                            ?? throw new PipeException(PipeErrorCodes.Internal, $"acdbEntGet n'a rien renvoyé pour l'objet {id.Handle}."))
            values = buffer.AsArray();

        var report = new List<object>();
        foreach (var change in changesElement.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || !change.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code))
                throw new PipeException(PipeErrorCodes.InvalidParams, "Chaque modification demande un code DXF entier.");
            if (code is -1 or 0 or 5 or 100 or 102 or 105 or -3 or 1001)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le code {code} (nom, type, handle, marqueur de sous-classe ou de groupe) ne se modifie pas.");
            var occurrence = change.TryGetProperty("occurrence", out var occurrenceElement) && occurrenceElement.TryGetInt32(out var n) ? n : 1;
            if (occurrence < 1)
                throw new PipeException(PipeErrorCodes.InvalidParams, "occurrence commence à 1.");
            if (!change.TryGetProperty("value", out var newValue))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Code {code} : value manquante.");

            var index = -1;
            for (int i = 0, seen = 0; i < values.Length; i++)
            {
                if ((int)values[i].TypeCode == code && ++seen == occurrence)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Le code {code} n'apparaît pas {(occurrence == 1 ? "" : $"{occurrence} fois ")}dans les données de l'objet {id.Handle}.");

            var before = values[index];
            values[index] = new TypedValue(before.TypeCode, ConvertValue(before, newValue, code, id.Database));
            report.Add(new { Code = code, Occurrence = occurrence, Before = Pair(before, 64), After = Pair(values[index], 64) });
        }

        if (dryRun)
            return new { Handle = id.Handle.ToString(), DryRun = true, Changes = report, Note = "Simulation : rien n'a été écrit. dryRun=false applique les modifications." };

        // entmod ouvre l'objet lui-même : il s'exécute après la transaction de l'outil, toujours en contexte commande
        // (même étape de U).
        int status = 0;
        context.AfterCommit(() =>
        {
            using var modified = new ResultBuffer(values);
            status = NativeAds.Modify(id, modified);
        });

        context.ResultAfterCommit = () =>
        {
            if (status != NativeAds.RtNorm)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"AutoCAD a refusé la modification de l'objet {id.Handle} (acdbEntMod : code {status}) : valeur incohérente ou code non modifiable pour ce type d'objet.");
            using var check = id.Database.TransactionManager.StartOpenCloseTransaction();
            var reread = Describe(id, check, applications, 256, false);
            check.Commit();
            return new { Handle = id.Handle.ToString(), Applied = true, Changes = report, After = reread };
        };
        return new { };
    }

    /// <summary>Nouvelle valeur au type de l'ancienne.</summary>
    private static object ConvertValue(TypedValue before, JsonElement value, int code, Database database)
    {
        try
        {
            return before.Value switch
            {
                string => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(),
                double => value.GetDouble(),
                short => value.GetInt16(),
                int => value.GetInt32(),
                long => value.GetInt64(),
                Point3d => Point(value) is { } point ? new Point3d(point[0], point[1], point.Length > 2 ? point[2] : 0) : throw new FormatException(),
                Point2d => Point(value) is { } flat ? new Point2d(flat[0], flat[1]) : throw new FormatException(),
                ObjectId => long.TryParse(value.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number)
                            && database.TryGetObjectId(new Handle(number), out var target) && !target.IsErased
                    ? target
                    : throw new PipeException(PipeErrorCodes.InvalidParams, $"Code {code} : aucun objet de handle « {value} »."),
                byte[] => System.Convert.FromHexString(value.GetString()!),
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"Code {code} : type {before.Value?.GetType().Name ?? "vide"} non modifiable."),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Code {code} : valeur « {value.GetRawText()} » incompatible avec le type {TypeName(before.Value)} de ce code.");
        }
    }

    private static string TypeName(object? value) => value switch
    {
        null => "marker",
        string => "string",
        double => "real",
        short => "int16",
        int => "int32",
        long => "int64",
        Point3d or Point2d or Vector3d => "point",
        ObjectId => "handle",
        byte[] => "binary",
        _ => value.GetType().Name,
    };

    private static double[]? Point(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is 2 or 3 && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number)
            ? value.EnumerateArray().Select(item => item.GetDouble()).ToArray()
            : null;

    /// <summary>
    /// Diagnostic, non exposé comme outil MCP : prépare les données d'essai de scripts/test-dxf.ps1 en une étape de U.
    /// Données étendues MCP_TEST sur l'objet désigné, et dictionnaire nommé MCP_TEST_DICT (Xrecord DATA avec un
    /// binaire de 100 octets, sous-dictionnaire SUB), et dictionnaire d'extension de l'objet (Xrecord EXT). Un groupe 310
    /// ne dépasse pas 127 octets : au-delà, acdbEntGet ne renvoie plus rien pour l'objet.
    /// </summary>
    public static object CreateTestFixture(ToolContext context, JsonElement? args)
    {
        const string App = "MCP_TEST";
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var database = context.Database;
        var target = (Entity)transaction.GetObject(Handles.ResolveOne(context, reader.RequireString("handle")), OpenMode.ForWrite);

        var applications = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        if (!applications.Has(App))
        {
            applications.UpgradeOpen();
            var record = new RegAppTableRecord { Name = App };
            applications.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        target.XData = new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "Regard R1"),
            new TypedValue((int)DxfCode.ExtendedDataReal, 0.1 + 0.2),
            new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)42),
            new TypedValue((int)DxfCode.ExtendedDataHandle, target.OwnerId.Handle.ToString()));

        var root = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForWrite);
        if (root.Contains("MCP_TEST_DICT"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "MCP_TEST_DICT existe déjà dans ce dessin.");
        var dictionary = new DBDictionary();
        root.SetAt("MCP_TEST_DICT", dictionary);
        transaction.AddNewlyCreatedDBObject(dictionary, true);
        var binary = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var data = new Xrecord
        {
            Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "réseau d'essai"),
                new TypedValue((int)DxfCode.Real, Math.PI),
                new TypedValue((int)DxfCode.Int16, (short)7),
                new TypedValue((int)DxfCode.BinaryChunk, binary)),
        };
        dictionary.SetAt("DATA", data);
        transaction.AddNewlyCreatedDBObject(data, true);
        var sub = new DBDictionary();
        dictionary.SetAt("SUB", sub);
        transaction.AddNewlyCreatedDBObject(sub, true);
        var leaf = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, "feuille")) };
        sub.SetAt("LEAF", leaf);
        transaction.AddNewlyCreatedDBObject(leaf, true);

        if (target.ExtensionDictionary.IsNull)
            target.CreateExtensionDictionary();
        var extension = (DBDictionary)transaction.GetObject(target.ExtensionDictionary, OpenMode.ForWrite);
        var ext = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Int32, 2026)) };
        extension.SetAt("EXT", ext);
        transaction.AddNewlyCreatedDBObject(ext, true);

        return new
        {
            Target = target.Handle.ToString(),
            Dictionary = dictionary.Handle.ToString(),
            Data = data.Handle.ToString(),
            Sub = sub.Handle.ToString(),
        };
    }

    // ---------- export_dxf ----------

    private static readonly Dictionary<string, DwgVersion> Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["R2018"] = DwgVersion.AC1032, ["2018"] = DwgVersion.AC1032, ["AC1032"] = DwgVersion.AC1032,
        ["R2013"] = DwgVersion.AC1027, ["2013"] = DwgVersion.AC1027, ["AC1027"] = DwgVersion.AC1027,
        ["R2010"] = DwgVersion.AC1024, ["2010"] = DwgVersion.AC1024, ["AC1024"] = DwgVersion.AC1024,
        ["R2007"] = DwgVersion.AC1021, ["2007"] = DwgVersion.AC1021, ["AC1021"] = DwgVersion.AC1021,
        ["R2004"] = DwgVersion.AC1800, ["2004"] = DwgVersion.AC1800, ["AC1800"] = DwgVersion.AC1800,
        ["R2000"] = DwgVersion.AC1015, ["2000"] = DwgVersion.AC1015, ["AC1015"] = DwgVersion.AC1015,
    };

    /// <summary>
    /// Écrit une copie DXF du dessin actif (Database.DxfOut) : le dessin garde son nom, son fichier et son état
    /// « modifié ». DXF ASCII seulement : l'API ne produit pas de DXF binaire. DxfOut laisse une étape d'annulation
    /// vide : l'export passe donc par le contexte commande, hors de la transaction de l'outil, pour que cette étape
    /// compte parmi celles du connecteur (sinon l'outil undo se décalerait d'une étape).
    /// </summary>
    public static object ExportDxf(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var document = context.RequireDocument();
        var versionName = reader.GetString("version", "R2018")!.Trim();
        if (!Versions.TryGetValue(versionName, out var version))
            throw new PipeException(PipeErrorCodes.InvalidParams, "version : R2018 (défaut), R2013, R2010, R2007, R2004 ou R2000.");
        var precision = reader.GetInt("precision", 16, 0, 16);
        if (!reader.GetBool("ascii", true))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le DXF binaire n'est pas proposé par l'API d'AutoCAD : ascii doit rester vrai.");

        var path = DrawingTools.FullPath(reader.RequireString("filePath"));
        if (!Path.HasExtension(path))
            path += ".dxf";
        if (!string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{path} : l'extension doit être .dxf.");
        var directory = Path.GetDirectoryName(path);
        if (directory is null || !Directory.Exists(directory))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le dossier {directory} n'existe pas.");
        if (string.Equals(path, document.Database.Filename, StringComparison.OrdinalIgnoreCase))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"{path} est le fichier du dessin lui-même.");
        if (File.Exists(path))
        {
            if (!reader.GetBool("overwrite", false))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le fichier {path} existe déjà : indiquez overwrite = true pour le remplacer.");
            File.Delete(path);
        }

        var modified = DrawingTools.IsModified(document);
        Autodesk.AutoCAD.Runtime.ErrorStatus? failure = null;
        context.AfterCommit(() =>
        {
            try
            {
                document.Database.DxfOut(path, precision, version);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                failure = ex.ErrorStatus;
            }
        });

        context.ResultAfterCommit = () =>
        {
            if (failure is not null)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Export DXF refusé par AutoCAD ({failure}).");
            var info = new FileInfo(path);
            return new
            {
                FilePath = info.FullName,
                Version = version.ToString(),
                Precision = precision,
                Bytes = info.Exists ? info.Length : 0,
                DrawingName = document.Name,
                DrawingModifiedBefore = modified,
                DrawingModifiedAfter = DrawingTools.IsModified(document),
            };
        };
        return new { };
    }
}

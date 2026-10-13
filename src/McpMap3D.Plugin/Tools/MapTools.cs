using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Gis.Map;
using Autodesk.Gis.Map.ObjectData;
using Autodesk.Gis.Map.Utilities;
using DataType = Autodesk.Gis.Map.Constants.DataType;
using OdOpenMode = Autodesk.Gis.Map.Constants.OpenMode;
using OdTable = Autodesk.Gis.Map.ObjectData.Table;
using McpMap3D.Shared;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Données d'objet (OD) de Map 3D. Les objets renvoyés par l'API Map appartiennent au projet :
/// seules les collections ouvertes ici (Records) sont libérées.
/// </summary>
internal static class MapTools
{
    /// <summary>0 désigne le dessin courant dans les appels de l'API OD.</summary>
    private const uint CurrentDrawing = 0;

    public static object ListOdTables(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            return ListOdTablesClassic(context, args);
        return Guard(() => ListOdTablesCore(context, args), "lecture des tables de données d'objet");
    }

    public static object GetOdRecords(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            return GetOdRecordsClassic(context, args);
        return Guard(() => GetOdRecordsCore(context, args), "lecture des données d'objet");
    }

    public static object SetOdValue(ToolContext context, JsonElement? args) =>
        Guard(() => SetOdValueCore(context, args), "écriture d'une donnée d'objet");

    /// <summary>Diagnostic des scripts de test : crée la table si elle manque, sans erreur si elle existe.</summary>
    public static object CreateOdTableIfMissing(ToolContext context, JsonElement? args) =>
        Guard(() => CreateOdTableIfMissingCore(context, args), "création d'une table de données d'objet");

    public static object CreateOdTable(ToolContext context, JsonElement? args) =>
        Guard(() => CreateOdTableCore(context, args), "création d'une table de données d'objet");

    public static object EditOdTable(ToolContext context, JsonElement? args) =>
        Guard(() => EditOdTableCore(context, args), "modification d'une table de données d'objet");

    public static object DeleteOdTable(ToolContext context, JsonElement? args) =>
        Guard(() => DeleteOdTableCore(context, args), "suppression d'une table de données d'objet");

    /// <summary>
    /// Lecture des données d'objet d'un objet par table et champ, pour les filtres et métrés. Les enregistrements
    /// d'un objet sont lus une fois, puis gardés tant que l'objet demandé ne change pas.
    /// </summary>
    internal static Func<ObjectId, string, string, object?> CreateOdLookup(Database database)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Les champs od:TABLE.CHAMP requièrent AutoCAD Map 3D.");
        return CreateOdLookupCore(database);
    }

    /// <summary>Toutes les données d'objet d'un objet : table -> champ -> valeur (export, publication).</summary>
    internal static Func<ObjectId, Dictionary<string, Dictionary<string, object?>>> CreateOdRecordReader(Database database)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Les données d'objet requièrent AutoCAD Map 3D.");
        return CreateOdRecordReaderCore(database);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<ObjectId, Dictionary<string, Dictionary<string, object?>>> CreateOdRecordReaderCore(Database database)
    {
        var tables = MapInfo.RequireProject(database).ODTables;
        return id =>
        {
            var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var attached = OpenRecords(tables, id, OdOpenMode.OpenForRead);
                // L'énumérateur de l'API Map lève une exception si la collection est vide.
                foreach (Record record in attached.Count > 0 ? attached.Cast<Record>() : [])
                {
                    if (!result.ContainsKey(record.TableName))
                        result[record.TableName] = ReadValues(tables[record.TableName], record);
                }
            }
            catch (MapException ex)
            {
                throw Translate(ex, $"lecture des données d'objet de {id.Handle}");
            }

            return result;
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<ObjectId, string, string, object?> CreateOdLookupCore(Database database)
    {
        var tables = MapInfo.RequireProject(database).ODTables;
        var cachedId = ObjectId.Null;
        var cached = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        return (id, table, field) =>
        {
            if (id != cachedId)
            {
                cached.Clear();
                try
                {
                    using var attached = OpenRecords(tables, id, OdOpenMode.OpenForRead);
                    // L'énumérateur de l'API Map lève une exception si la collection est vide.
                    foreach (Record record in attached.Count > 0 ? attached.Cast<Record>() : [])
                    {
                        if (!cached.ContainsKey(record.TableName))
                            cached[record.TableName] = new Dictionary<string, object?>(ReadValues(tables[record.TableName], record), StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (MapException ex)
                {
                    throw Translate(ex, $"lecture des données d'objet de {id.Handle}");
                }

                cachedId = id;
            }

            return cached.TryGetValue(table, out var values) && values.TryGetValue(field, out var value) ? value : null;
        };
    }

    /// <summary>Les erreurs de l'API Map arrivent sans message : on les traduit systématiquement.</summary>
    private static object Guard(Func<object> action, string operation)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, $"« {operation} » requiert AutoCAD Map 3D.");
        return GuardCore(action, operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object GuardCore(Func<object> action, string operation)
    {
        try
        {
            return action();
        }
        catch (MapException ex)
        {
            throw Translate(ex, operation);
        }
    }

    private static object ListOdTablesClassic(ToolContext context, JsonElement? args)
    {
        var database = context.Database;
        var transaction = context.RequireTransaction();
        var regTable = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
        var tables = new List<object>();
        foreach (var id in regTable)
        {
            var regApp = (RegAppTableRecord)transaction.GetObject(id, OpenMode.ForRead);
            if (regApp.Name.Equals("ACAD", StringComparison.OrdinalIgnoreCase))
                continue;

            tables.Add(new
            {
                Name = regApp.Name,
                Description = "Application XData (AutoCAD classique)",
                StoredAsXData = true,
                FieldCount = 0,
            });
        }

        return new
        {
            Count = tables.Count,
            Tables = tables,
            MapAvailable = false,
            Message = "AutoCAD classique : les tables de données d'objet Map 3D ne sont pas disponibles. Les attributs sont stockés en XData."
        };
    }

    private static object GetOdRecordsClassic(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var tableFilter = NameFilter.Create(reader.GetStrings("tables"));
        var transaction = context.RequireTransaction();
        var objects = new List<object>();
        var recordCount = 0;

        foreach (var id in Handles.Resolve(context, reader))
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
            var records = new List<object>();

            using var rb = entity.XData;
            if (rb is not null)
            {
                string? currentApp = null;
                var currentValues = new List<TypedValue>();

                void FlushApp()
                {
                    if (currentApp is null || currentApp.Equals("ACAD", StringComparison.OrdinalIgnoreCase))
                        return;

                    if (tableFilter is not null && !tableFilter.IsMatch(currentApp))
                        return;

                    var fields = FormatXDataFields(currentApp, currentValues);
                    records.Add(new
                    {
                        Table = currentApp,
                        Values = fields,
                    });
                    recordCount++;
                }

                foreach (var tv in rb)
                {
                    if (tv.TypeCode == (short)DxfCode.ExtendedDataRegAppName)
                    {
                        FlushApp();
                        currentApp = tv.Value as string;
                        currentValues.Clear();
                    }
                    else
                    {
                        currentValues.Add(tv);
                    }
                }
                FlushApp();
            }

            objects.Add(new { Handle = id.Handle.ToString(), RecordCount = records.Count, Records = records });
        }

        return new { RecordCount = recordCount, Objects = objects };
    }

    private static Dictionary<string, object?> FormatXDataFields(string appName, List<TypedValue> values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (appName.EndsWith("_SECTIONS", StringComparison.OrdinalIgnoreCase))
        {
            string[] sectionFieldNames = ["ID", "COMMUNE", "CODE_INSEE", "SECTION", "FEUILLE"];
            for (var i = 0; i < values.Count; i++)
            {
                var name = i < sectionFieldNames.Length ? sectionFieldNames[i] : $"FIELD_{i + 1}";
                dict[name] = values[i].Value;
            }
        }
        else if (appName.Contains("CADASTRE", StringComparison.OrdinalIgnoreCase))
        {
            string[] parcelFieldNames = ["ID", "COMMUNE", "CODE_INSEE", "SECTION", "NUMERO", "CONTENANCE"];
            for (var i = 0; i < values.Count; i++)
            {
                var name = i < parcelFieldNames.Length ? parcelFieldNames[i] : $"FIELD_{i + 1}";
                dict[name] = values[i].Value;
            }
        }
        else
        {
            for (var i = 0; i < values.Count; i++)
                dict[$"FIELD_{i + 1}"] = values[i].Value;
        }

        return dict;
    }

    private static object ListOdTablesCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var includeFields = reader.GetBool("includeFields", true);
        var tables = MapInfo.RequireProject(context.Database).ODTables;

        var result = new List<object>();
        foreach (string? name in tables.GetTableNames())
        {
            if (name is null)
                continue;

            var table = tables[name];
            var definitions = table.FieldDefinitions;
            result.Add(new
            {
                table.Name,
                Description = string.IsNullOrEmpty(table.Description) ? null : table.Description,
                StoredAsXData = table.StoreAsXData,
                FieldCount = definitions.Count,
                Fields = includeFields ? DescribeFields(definitions) : null,
            });
        }

        return new { Count = result.Count, Tables = result };
    }

    private static object GetOdRecordsCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var tableFilter = NameFilter.Create(reader.GetStrings("tables"));
        var project = MapInfo.RequireProject(context.Database);
        var tables = project.ODTables;

        var objects = new List<object>();
        var recordCount = 0;
        foreach (var id in Handles.Resolve(context, reader))
        {
            var records = new List<object>();
            using (var attached = OpenRecords(tables, id, OdOpenMode.OpenForRead))
            {
                // L'énumérateur de l'API Map lève une exception si la collection est vide.
                foreach (Record record in attached.Count > 0 ? attached.Cast<Record>() : [])
                {
                    if (tableFilter is not null && !tableFilter.IsMatch(record.TableName))
                        continue;

                    records.Add(new
                    {
                        Table = record.TableName,
                        Values = ReadValues(tables[record.TableName], record),
                    });
                    recordCount++;
                }
            }

            objects.Add(new { Handle = id.Handle.ToString(), RecordCount = records.Count, Records = records });
        }

        return new { RecordCount = recordCount, Objects = objects };
    }

    private static object SetOdValueCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var tableName = reader.RequireString("table");
        var fieldName = reader.RequireString("field");
        var createRecord = reader.GetBool("createRecord", true);
        if (!reader.Has("value"))
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « value » est obligatoire.");

        var project = MapInfo.RequireProject(context.Database);
        var tables = project.ODTables;
        if (!tables.IsTableDefined(tableName))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"La table de données d'objet « {tableName} » n'existe pas. Utilisez list_od_tables pour voir les tables du dessin.");

        var table = tables[tableName];
        var definitions = table.FieldDefinitions;
        var column = definitions.GetColumnIndex(fieldName);
        if (column < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"Le champ « {fieldName} » n'existe pas dans la table « {tableName} » : {string.Join(", ", FieldNames(definitions))}.");

        var type = definitions[column].Type;
        var updated = new List<string>();
        var created = new List<string>();

        foreach (var id in Handles.Resolve(context, reader))
        {
            var found = false;
            using (var attached = OpenRecords(tables, id, OdOpenMode.OpenForWrite))
            {
                // L'énumérateur de l'API Map lève une exception si la collection est vide.
                foreach (Record existing in attached.Count > 0 ? attached.Cast<Record>() : [])
                {
                    if (!string.Equals(existing.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Assign(existing[column], type, reader, fieldName);
                    try
                    {
                        attached.UpdateRecord(existing);
                    }
                    catch (MapException ex)
                    {
                        throw Translate(ex, $"mise à jour de {tableName} sur {id.Handle}");
                    }

                    found = true;
                    updated.Add(id.Handle.ToString());
                    break;
                }
            }

            if (found)
                continue;

            if (!createRecord)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"L'objet {id.Handle} n'a pas d'enregistrement dans la table « {tableName} ». " +
                    "Passez createRecord=true pour en attacher un.");

            var record = Record.Create();
            table.InitRecord(record);
            Assign(record[column], type, reader, fieldName);
            try
            {
                table.AddRecord(record, id);
            }
            catch (MapException ex)
            {
                throw Translate(ex, $"attachement d'un enregistrement {tableName} à {id.Handle}");
            }

            created.Add(id.Handle.ToString());
        }

        return new
        {
            Table = tableName,
            Field = fieldName,
            Updated = updated.Count,
            Created = created.Count,
            Handles = updated.Concat(created).ToArray(),
        };
    }

    /// <summary>
    /// Données d'objet des points importés : table créée si elle manque (NUMERO, CODE, ALTITUDE), sinon ses champs
    /// de ces noms sont remplis. À appeler après validation de la géométrie (AfterCommit).
    /// </summary>
    internal static void AttachPointData(Database database, string tableName, IReadOnlyList<(ObjectId Id, string? Number, string? Code, double Z)> points)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Les données d'objet (odTable) requièrent AutoCAD Map 3D.");
        GuardCore(() =>
        {
            AttachPointDataCore(database, tableName, points);
            return true;
        }, "rattachement des données d'objet des points");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AttachPointDataCore(Database database, string tableName, IReadOnlyList<(ObjectId Id, string? Number, string? Code, double Z)> points)
    {
        var tables = MapInfo.RequireProject(database).ODTables;
        if (!tables.IsTableDefined(tableName))
        {
            var definitions = FieldDefinitions.Create();
            definitions.AddColumn(FieldDefinition.Create("NUMERO", "Numéro du point", ""), 0);
            definitions.AddColumn(FieldDefinition.Create("CODE", "Code topographique", ""), 1);
            definitions.AddColumn(FieldDefinition.Create("ALTITUDE", "Altitude", 0.0), 2);
            tables.Add(tableName, definitions, "Points topographiques importés", false);
        }

        var table = tables[tableName];
        var fields = table.FieldDefinitions;
        var (number, code, altitude) = (fields.GetColumnIndex("NUMERO"), fields.GetColumnIndex("CODE"), fields.GetColumnIndex("ALTITUDE"));
        if (number < 0 && code < 0 && altitude < 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"La table « {tableName} » n'a aucun des champs NUMERO, CODE ou ALTITUDE : choisissez une autre table, ou une nouvelle.");

        foreach (var point in points)
        {
            var record = Record.Create();
            table.InitRecord(record);
            if (number >= 0 && fields[number].Type == DataType.Character)
                record[number].Assign(point.Number ?? "");
            if (code >= 0 && fields[code].Type == DataType.Character)
                record[code].Assign(point.Code ?? "");
            if (altitude >= 0 && fields[altitude].Type == DataType.Real)
                record[altitude].Assign(point.Z);
            table.AddRecord(record, point.Id);
        }
    }

    private static object CreateOdTableIfMissingCore(ToolContext context, JsonElement? args)
    {
        var name = new ArgReader(args).RequireString("name");
        return MapInfo.RequireProject(context.Database).ODTables.IsTableDefined(name)
            ? new { Created = false, Name = name }
            : CreateOdTableCore(context, args);
    }

    /// <summary>Nouvelle table : name, description, fields = [{ name, type, description, default }].</summary>
    private static object CreateOdTableCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name").Trim();
        var tables = MapInfo.RequireProject(context.Database).ODTables;
        if (tables.IsTableDefined(name))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"La table « {name} » existe déjà. Pour la modifier, utilisez edit_od_table.");

        var definitions = FieldDefinitions.Create();
        var fields = ReadFieldSpecs(reader, "fields");
        if (fields.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Une table demande au moins un champ dans « fields ».");
        foreach (var field in fields)
            definitions.AddColumn(field, definitions.Count);

        tables.Add(name, definitions, reader.GetString("description") ?? "", false);
        return new { Created = true, Name = name, Fields = DescribeFields(tables[name].FieldDefinitions) };
    }

    /// <summary>Renomme une table, lui ajoute ou lui retire des champs. Retirer un champ efface ses valeurs.</summary>
    private static object EditOdTableCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("table").Trim();
        var tables = MapInfo.RequireProject(context.Database).ODTables;
        if (!tables.IsTableDefined(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"La table de données d'objet « {name} » n'existe pas (voir list_od_tables).");

        var newName = reader.GetString("newName")?.Trim();
        var remove = reader.GetStrings("removeFields");
        var add = ReadFieldSpecs(reader, "addFields");
        if (string.IsNullOrEmpty(newName) && remove.Count == 0 && add.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Indiquez newName, addFields ou removeFields.");

        if (remove.Count > 0 || add.Count > 0)
        {
            var definitions = tables[name].FieldDefinitions.Clone();
            foreach (var field in remove)
            {
                var index = definitions.GetColumnIndex(field);
                if (index < 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams,
                        $"Le champ « {field} » n'existe pas dans la table « {name} » : {string.Join(", ", FieldNames(definitions))}.");
                definitions.RemoveColumn(index);
            }

            foreach (var field in add)
            {
                if (definitions.GetColumnIndex(field.Name) >= 0)
                    throw new PipeException(PipeErrorCodes.InvalidParams, $"Le champ « {field.Name} » existe déjà dans la table « {name} ».");
                definitions.AddColumn(field, definitions.Count);
            }

            if (definitions.Count == 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, "Une table doit garder au moins un champ : utilisez delete_od_table pour la supprimer.");
            tables.UpdateTable(name, definitions);
        }

        if (!string.IsNullOrEmpty(newName) && !newName.Equals(name, StringComparison.Ordinal))
        {
            if (tables.IsTableDefined(newName))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Une table « {newName} » existe déjà.");
            tables.RenameTable(name, newName);
            name = newName;
        }

        return new { Name = name, Fields = DescribeFields(tables[name].FieldDefinitions) };
    }

    /// <summary>
    /// Supprime une table et, avec elle, les enregistrements attachés aux objets. Sans force, refuse si des objets
    /// des espaces objet et papier en portent encore.
    /// </summary>
    private static object DeleteOdTableCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("table").Trim();
        var tables = MapInfo.RequireProject(context.Database).ODTables;
        if (!tables.IsTableDefined(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"La table de données d'objet « {name} » n'existe pas (voir list_od_tables).");

        var table = tables[name];
        var transaction = context.RequireTransaction();
        var layouts = (DBDictionary)transaction.GetObject(context.Database.LayoutDictionaryId, OpenMode.ForRead);
        var attached = 0;
        foreach (System.Collections.DictionaryEntry entry in layouts)
        {
            var layout = (Layout)transaction.GetObject((ObjectId)entry.Value!, OpenMode.ForRead);
            foreach (ObjectId id in (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead))
            {
                using var records = table.GetObjectTableRecords(CurrentDrawing, id, OdOpenMode.OpenForRead, false);
                if (records.Count > 0)
                    attached++;
            }
        }

        if (attached > 0 && !reader.GetBool("force", false))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"{attached} objet(s) portent encore des données de la table « {name} » : leur suppression serait définitive. " +
                "force=true supprime quand même, seulement si l'utilisateur le demande.");

        tables.RemoveTable(name);
        return new { Deleted = name, ObjectsWithRecords = attached };
    }

    /// <summary>Champs décrits en JSON : [{ name, type, description, default }].</summary>
    private static List<FieldDefinition> ReadFieldSpecs(ArgReader reader, string parameter)
    {
        var result = new List<FieldDefinition>();
        if (!reader.TryGet(parameter, out var fields))
            return result;
        if (fields.ValueKind != JsonValueKind.Array)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"« {parameter} » attend un tableau de champs, par exemple [{{\"name\":\"DIAMETRE\",\"type\":\"Integer\"}}].");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.EnumerateArray())
        {
            var fieldReader = new ArgReader(field);
            var name = fieldReader.RequireString("name").Trim();
            if (!names.Add(name))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Le champ « {name} » figure deux fois.");
            var description = fieldReader.GetString("description") ?? "";
            var type = ParseDataType(fieldReader.GetString("type") ?? "Character");
            result.Add(!fieldReader.Has("default") ? FieldDefinition.Create(name, description, type) : type switch
            {
                DataType.Character => FieldDefinition.Create(name, description, fieldReader.GetString("default") ?? ""),
                DataType.Integer => FieldDefinition.Create(name, description, fieldReader.GetInt("default", 0)),
                DataType.Real => FieldDefinition.Create(name, description, fieldReader.GetDouble("default", 0)),
                _ => FieldDefinition.Create(name, description, fieldReader.RequirePoint("default")),
            });
        }

        return result;
    }

    private static Records OpenRecords(Tables tables, ObjectId id, OdOpenMode mode)
    {
        try
        {
            return tables.GetObjectRecords(CurrentDrawing, id, mode, false);
        }
        catch (MapException ex)
        {
            throw Translate(ex, $"accès aux données d'objet de {id.Handle}");
        }
    }

    private static object[] DescribeFields(FieldDefinitions definitions)
    {
        var fields = new List<object>(definitions.Count);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            fields.Add(new
            {
                definition.Name,
                Type = definition.Type.ToString(),
                Description = string.IsNullOrEmpty(definition.Description) ? null : definition.Description,
                Default = Read(definition.DefaultMapValue),
            });
        }

        return [.. fields];
    }

    private static string[] FieldNames(FieldDefinitions definitions)
    {
        var names = new string[definitions.Count];
        for (var index = 0; index < definitions.Count; index++)
            names[index] = definitions[index].Name;

        return names;
    }

    private static Dictionary<string, object?> ReadValues(OdTable table, Record record)
    {
        var definitions = table.FieldDefinitions;
        var values = new Dictionary<string, object?>(record.Count);
        for (var index = 0; index < record.Count && index < definitions.Count; index++)
            values[definitions[index].Name] = Read(record[index]);

        return values;
    }

    private static object? Read(MapValue value) => value.Type switch
    {
        DataType.Character => value.StrValue,
        DataType.Integer => value.Int32Value,
        DataType.Real => Format.Number(value.DoubleValue),
        DataType.Point => Format.Point(value.Point),
        _ => null,
    };

    private static void Assign(MapValue target, DataType type, ArgReader reader, string fieldName)
    {
        switch (type)
        {
            case DataType.Character:
                target.Assign(reader.GetString("value") ?? throw Expected(fieldName, "un texte"));
                break;

            case DataType.Integer:
                target.Assign(reader.GetInt("value", 0));
                break;

            case DataType.Real:
                target.Assign(reader.GetDouble("value", 0));
                break;

            case DataType.Point:
                target.Assign(reader.RequirePoint("value"));
                break;

            default:
                throw new PipeException(PipeErrorCodes.MapUnavailable,
                    $"Le champ « {fieldName} » est d'un type non pris en charge ({type}).");
        }
    }

    private static PipeException Expected(string fieldName, string expected) =>
        new(PipeErrorCodes.InvalidParams, $"Le champ « {fieldName} » attend {expected}.");

    /// <summary>Traduit une erreur Map 3D, dont le message .NET est vide, en texte exploitable.</summary>
    internal static PipeException Translate(MapException exception, string operation)
    {
        var code = (Autodesk.Gis.Map.Constants.ErrorCode)exception.ErrorCode;
        var details = new List<string>();
        try
        {
            var stack = Autodesk.Gis.Map.HostMapApplicationServices.Application.ErrorStack;
            for (var index = 0; index < stack.EntriesCount; index++)
            {
                var entry = stack[index];
                if (!string.IsNullOrWhiteSpace(entry.ErrorMessage))
                    details.Add(entry.ErrorMessage.Trim());
            }

            stack.Clear();
        }
        catch
        {
            // La pile d'erreurs n'est qu'un complément d'information.
        }

        var explanation = details.Count > 0 ? $" : {string.Join(" ; ", details)}" : "";
        return new PipeException(PipeErrorCodes.MapUnavailable,
            $"Map 3D a refusé l'opération « {operation} » (code {code}){explanation}.");
    }

    private static DataType ParseDataType(string? name) => name?.ToLowerInvariant() switch
    {
        "character" or "text" or "string" => DataType.Character,
        "integer" or "int" => DataType.Integer,
        "real" or "double" => DataType.Real,
        "point" => DataType.Point,
        _ => throw new PipeException(PipeErrorCodes.InvalidParams,
            $"Type de champ « {name} » inconnu : Character, Integer, Real ou Point."),
    };
}

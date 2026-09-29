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

    public static object CreateOdTable(ToolContext context, JsonElement? args) =>
        Guard(() => CreateOdTableCore(context, args), "création d'une table de données d'objet");

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
    /// Diagnostic : crée une table de données d'objet pour les tests. Non exposé comme outil MCP.
    /// Paramètres : name, description, fields = [{ name, type, description }].
    /// </summary>
    private static object CreateOdTableCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name");
        var tables = MapInfo.RequireProject(context.Database).ODTables;
        if (tables.IsTableDefined(name))
            return new { Created = false, Name = name };

        if (!reader.TryGet("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Le paramètre « fields » attend un tableau de champs.");

        var definitions = FieldDefinitions.Create();
        var position = 0;
        foreach (var field in fields.EnumerateArray())
        {
            var fieldName = field.GetProperty("name").GetString()!;
            var typeName = field.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "Character";
            var description = field.TryGetProperty("description", out var descriptionValue) ? descriptionValue.GetString() : "";
            definitions.Add(fieldName, description ?? "", ParseDataType(typeName), position++);
        }

        tables.Add(name, definitions, reader.GetString("description") ?? "", false);
        return new { Created = true, Name = name, FieldCount = position };
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

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Données d'objet (OD) de Map 3D : tables attachées aux objets du dessin.</summary>
[McpServerToolType]
public sealed class MapTools(PluginClient plugin)
{
    [McpServerTool(Name = "list_od_tables", Title = "Lister les tables de données d'objet",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les tables de données d'objet (OD) de Map 3D définies dans le dessin, avec leurs champs " +
                 "(nom, type, description, valeur par défaut).")]
    public Task<string> ListOdTables(
        [Description("Inclure la définition des champs de chaque table.")] bool includeFields = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_od_tables", new { includeFields }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_od_records", Title = "Lire les données d'objet attachées",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lit les enregistrements de données d'objet attachés à des objets du dessin, désignés par leur " +
                 "handle. Renvoie, pour chaque objet, la table et la valeur de chaque champ.")]
    public Task<string> GetOdRecords(
        [Description("Handles des objets, tels que renvoyés par list_entities.")] string[] handles,
        [Description("Tables à retenir ; les jokers * et ? sont acceptés. Toutes si omis.")] string[]? tables = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_od_records", new { handles, tables }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_od_value", Title = "Écrire une valeur de donnée d'objet",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Écrit la valeur d'un champ de données d'objet sur un ou plusieurs objets. Si un objet n'a pas " +
                 "encore d'enregistrement dans la table, un enregistrement est attaché avec les valeurs par défaut, " +
                 "sauf si createRecord vaut false. La table et le champ doivent exister (voir list_od_tables).")]
    public Task<string> SetOdValue(
        [Description("Handles des objets, tels que renvoyés par list_entities.")] string[] handles,
        [Description("Nom de la table de données d'objet.")] string table,
        [Description("Nom du champ à écrire.")] string field,
        [Description("Valeur à écrire : texte, nombre, ou point [x, y, z] selon le type du champ.")] JsonElement value,
        [Description("Attacher un enregistrement aux objets qui n'en ont pas encore dans cette table.")] bool createRecord = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_od_value",
            new { handles, table, field, value, createRecord }, cancellationToken: cancellationToken);
}

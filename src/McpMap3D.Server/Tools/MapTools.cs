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

    [McpServerTool(Name = "create_od_table", Title = "Créer une table de données d'objet",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une table de données d'objet (OD) Map 3D, avec ses champs. set_od_value attache ensuite les " +
                 "valeurs aux objets. Refusé si la table existe déjà (edit_od_table la modifie).")]
    public Task<string> CreateOdTable(
        [Description("Nom de la table, par exemple CONDUITES (lettres, chiffres et _).")] string name,
        [Description("Champs de la table, un au moins.")] OdFieldInput[] fields,
        [Description("Description de la table.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_od_table", new { name, fields, description }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "edit_od_table", Title = "Modifier une table de données d'objet",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Renomme une table de données d'objet, lui ajoute des champs (valeur par défaut sur les objets " +
                 "existants) ou lui en retire. Retirer un champ efface ses valeurs sur tous les objets.")]
    public Task<string> EditOdTable(
        [Description("Nom de la table.")] string table,
        [Description("Nouveau nom.")] string? newName = null,
        [Description("Champs à ajouter en fin de table.")] OdFieldInput[]? addFields = null,
        [Description("Noms des champs à retirer.")] string[]? removeFields = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("edit_od_table", new { table, newName, addFields, removeFields }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_od_table", Title = "Supprimer une table de données d'objet",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime une table de données d'objet et, avec elle, ses enregistrements sur les objets (les objets " +
                 "restent). Refusé si des objets en portent encore, sauf avec force.")]
    public Task<string> DeleteOdTable(
        [Description("Nom de la table.")] string table,
        [Description("Supprimer même si des objets portent des données de la table : seulement si l'utilisateur le demande.")] bool force = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_od_table", new { table, force = force ? true : (bool?)null },
            busyTimeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
}

/// <summary>Champ de table de données d'objet, pour create_od_table et edit_od_table.</summary>
public sealed record OdFieldInput(
    [property: Description("Nom du champ, par exemple DIAMETRE.")] string Name,
    [property: Description("Type : Character (texte, défaut), Integer, Real ou Point.")] string? Type = null,
    [property: Description("Description du champ.")] string? Description = null,
    [property: Description("Valeur par défaut, du type du champ (point : [x, y, z]).")] JsonElement? Default = null);

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Sélection courante de l'utilisateur, filtres par valeur et métrés.</summary>
[McpServerToolType]
public sealed class QueryTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_selection", Title = "Objets sélectionnés dans AutoCAD",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Décrit les objets que l'utilisateur a sélectionnés dans AutoCAD (sélection en cours, hors " +
                 "commande), comme list_entities : handle, type, calque et résumé. Sert quand l'utilisateur parle de " +
                 "« ces objets » ou de « ma sélection ». Si rien n'est sélectionné, une note dit quoi lui demander.")]
    public Task<string> GetSelection(
        [Description("Nombre maximal d'objets décrits : 200 par défaut, 1000 au plus. count donne le total sélectionné.")] int limit = 200,
        [Description("Ajoute les détails géométriques : points, sommets, angles, échelles et limites.")] bool includeGeometry = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_selection", new { limit, includeGeometry }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "summarize_entities", Title = "Métré et synthèse des objets",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Métré des objets du dessin, sans les lister : nombre d'objets, longueur totale des courbes, aire " +
                 "des contours fermés, hachures et régions, et sommes de champs numériques, au total et par groupe " +
                 "de valeurs (groupBy). Par exemple, la longueur de conduites par diamètre : groupBy " +
                 "[\"od:CONDUITES.DIAMETRE\"], ou le nombre de blocs par nom : types [\"INSERT\"], groupBy [\"block\"]. " +
                 "Mêmes filtres que list_entities (calques, types, where), sur un espace, la sélection courante ou " +
                 "des handles. Longueurs et aires sont en unités du dessin (units).")]
    public Task<string> SummarizeEntities(
        [Description("Calques à retenir ; les jokers * et ? sont acceptés. Tous si omis.")] string[]? layers = null,
        [Description("Types DXF à retenir, par exemple LINE, LWPOLYLINE, INSERT, HATCH. Tous si omis.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Champs de regroupement, trois au plus : " + ToolHelp.EntityFields)] string[]? groupBy = null,
        [Description("Champs numériques à additionner, cinq au plus, par exemple [\"od:CONDUITES.PRIX\"].")] string[]? sum = null,
        [Description("Espace à parcourir : model (défaut), paper, current, ou le nom d'une présentation.")] string? space = null,
        [Description("Porter sur les objets sélectionnés par l'utilisateur dans AutoCAD, au lieu d'un espace.")] bool selection = false,
        [Description("Handles d'objets précis, au lieu d'un espace.")] string[]? handles = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("summarize_entities",
            new { layers, types, where, groupBy, sum, space, selection = selection ? true : (bool?)null, handles },
            busyTimeout: TimeSpan.FromSeconds(60), cancellationToken: cancellationToken);
}

/// <summary>Condition de filtre sur un champ d'objet, pour where.</summary>
public sealed record EntityCondition(
    [property: Description(ToolHelp.EntityFields)] string Field,
    [property: Description("Opérateur : = (défaut), !=, <, <=, >, >=, like (jokers * et ?), notlike, exists, missing. " +
                           "Avec = ou !=, value peut être une liste de valeurs.")] string? Op = null,
    [property: Description("Valeur comparée : nombre, texte, ou liste de valeurs. Inutile avec exists et missing.")] JsonElement? Value = null);

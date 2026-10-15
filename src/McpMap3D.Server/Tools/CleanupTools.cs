using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Remise à plat des objets et nettoyage de dessin de Map 3D (MAPCLEAN).</summary>
[McpServerToolType]
public sealed class CleanupTools(PluginClient plugin)
{
    [McpServerTool(Name = "flatten_entities", Title = "Mettre des objets à plat",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ramène des objets à une altitude unique (0 par défaut), comme la commande FLATTEN mais sans changer " +
                 "leur type : handles et données d'objet sont conservés. Lignes, polylignes 3D, splines, faces 3D et " +
                 "lignes de repère ont chacun de leurs sommets mis à l'altitude ; les objets plans parallèles au plan " +
                 "XY (polylignes, cercles, arcs, textes, hachures, cotes) sont déplacés verticalement ; une référence " +
                 "de bloc a son point d'insertion mis à l'altitude, sans toucher au contenu du bloc. Solides, surfaces, " +
                 "maillages et objets dans un plan incliné sont ignorés et comptés par raison, comme ceux des calques " +
                 "verrouillés. Mêmes filtres que list_entities ; all=true prend tout l'espace objet.")]
    public Task<string> FlattenEntities(
        [Description("Altitude Z à donner aux objets.")] double elevation = 0,
        [Description("Handles des objets.")] string[]? handles = null,
        [Description("Calques à retenir ; les jokers * et ? sont acceptés.")] string[]? layers = null,
        [Description("Types DXF à retenir, par exemple LINE, LWPOLYLINE, POLYLINE, INSERT, TEXT.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Prendre les objets sélectionnés dans AutoCAD.")] bool selection = false,
        [Description("Tout l'espace objet, sans filtre : à demander explicitement.")] bool all = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("flatten_entities",
            new { elevation, handles, layers, types, where, selection, all }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "clean_drawing", Title = "Nettoyer le dessin (MAPCLEAN)",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Nettoyage de dessin de Map 3D, comme MAPCLEAN en mode automatique, avant une topologie, une analyse " +
                 "ou un export : enchaîne les actions demandées, dans l'ordre (simplify et weed passent toujours en " +
                 "premier). Actions : deleteDuplicates (doublons), eraseShort (objets plus courts que la tolérance), " +
                 "zeroLength (longueur nulle), breakCrossing (coupe aux croisements), extendUndershoots (prolonge les " +
                 "extrémités qui n'atteignent pas un objet à moins de la tolérance), apparentIntersection (relie deux " +
                 "extrémités qui se rejoindraient en se prolongeant), snapClusteredNodes (fusionne les extrémités " +
                 "proches), dissolvePseudoNodes (joint les objets bout à bout), eraseDangling (efface les objets " +
                 "pendants), simplify (simplifie les objets), weed (élague les sommets des polylignes, la tolérance " +
                 "servant de distance). Objets traités : courbes, points, blocs et textes de l'espace objet, avec les " +
                 "mêmes filtres que list_entities (tout l'espace objet sans filtre). anchorLayers fixe des objets qui " +
                 "ne bougent pas. dryRun compte les erreurs de chaque action sans rien modifier : à faire d'abord. " +
                 "Les données d'objet suivent les objets modifiés.")]
    public Task<string> CleanDrawing(
        [Description("Actions à enchaîner, dans l'ordre : noms, ou objets { \"action\": \"eraseShort\", \"tolerance\": 0.05 } pour une tolérance propre.")] object[] actions,
        [Description("Tolérance de nettoyage, en unités du dessin (0,01 par défaut).")] double tolerance = 0.01,
        [Description("Compter les erreurs sans rien corriger.")] bool dryRun = false,
        [Description("Calques des objets d'ancrage, qui restent en place ; jokers acceptés.")] string[]? anchorLayers = null,
        [Description("Doublons et nœuds proches : inclure les points (vrai par défaut).")] bool includePoints = true,
        [Description("Doublons et nœuds proches : inclure les blocs.")] bool includeBlocks = false,
        [Description("Doublons : inclure textes et textes multilignes.")] bool includeText = false,
        [Description("Doublons : tenir compte de l'altitude (sinon deux objets superposés en plan sont des doublons).")] bool compareZ = false,
        [Description("Handles des objets à nettoyer.")] string[]? handles = null,
        [Description("Calques à nettoyer ; les jokers * et ? sont acceptés.")] string[]? layers = null,
        [Description("Types DXF à nettoyer, par exemple LINE, LWPOLYLINE.")] string[]? types = null,
        [Description(ToolHelp.Where)] EntityCondition[]? where = null,
        [Description("Nettoyer les objets sélectionnés dans AutoCAD.")] bool selection = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("clean_drawing",
            new { actions, tolerance, dryRun, anchorLayers, includePoints, includeBlocks, includeText, compareZ, handles, layers, types, where, selection },
            cancellationToken: cancellationToken);
}

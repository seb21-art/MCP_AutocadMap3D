using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Réglages et entretien du dessin : échelles des types de ligne et d'annotation, style des points, propriétés du
/// dessin, gestion des calques, purge et contrôle d'intégrité.
/// </summary>
[McpServerToolType]
public sealed class SettingsTools(PluginClient plugin)
{
    [McpServerTool(Name = "set_linetype_scale", Title = "Échelle des types de ligne",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle l'échelle des types de ligne (pointillés, tirets) : échelle globale (LTSCALE), échelle donnée " +
                 "aux nouveaux objets (CELTSCALE), mise à l'échelle dans les fenêtres de présentation (PSLTSCALE) et " +
                 "selon l'échelle d'annotation dans l'espace objet (MSLTSCALE). Des tirets qui paraissent continus " +
                 "demandent souvent une échelle globale plus grande, par exemple après un changement d'unités. " +
                 "L'échelle propre d'un objet se règle avec set_entity_properties (linetypeScale). Réglages actuels " +
                 "dans get_drawing_info.")]
    public Task<string> SetLinetypeScale(
        [Description("Échelle globale de tous les types de ligne (LTSCALE), strictement positive.")] double? globalScale = null,
        [Description("Échelle donnée aux objets créés ensuite (CELTSCALE), strictement positive.")] double? newObjectScale = null,
        [Description("Dans les fenêtres de présentation, tirets à la même longueur sur la feuille quelle que soit l'échelle de la fenêtre (PSLTSCALE).")] bool? paperSpaceScaling = null,
        [Description("Dans l'espace objet, tirets mis à l'échelle d'annotation courante (MSLTSCALE).")] bool? annotationScaling = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_linetype_scale",
            new { globalScale, newObjectScale, paperSpaceScaling, annotationScaling }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_annotation_scales", Title = "Lister les échelles d'annotation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les échelles d'annotation du dessin (unités papier pour unités du dessin, ratio réel compte " +
                 "tenu de l'unité du dessin) et l'échelle courante, qui fixe la taille des textes, cotes et hachures " +
                 "annotatifs dans l'espace objet.")]
    public Task<string> ListAnnotationScales(CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_annotation_scales", cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_annotation_scale", Title = "Échelle d'annotation courante",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rend courante une échelle d'annotation (CANNOSCALE), désignée par son nom ou par son ratio. Une " +
                 "échelle absente de la liste est créée : avec scale \"1:500\", 1 mm sur le papier vaut 500 mm réels, " +
                 "convertis dans l'unité du dessin (0,5 dans un dessin en mètres) ; name lui donne un nom, sinon « 1:500 ».")]
    public Task<string> SetAnnotationScale(
        [Description("Nom d'une échelle existante (list_annotation_scales), ou nom de l'échelle à créer avec scale.")] string? name = null,
        [Description("Ratio : \"1:500\", \"1/200\" ou un nombre (unités papier par unité du dessin). Une échelle de même ratio est réutilisée.")] object? scale = null,
        [Description("Rendre l'échelle courante (vrai par défaut) ; faux la crée seulement.")] bool current = true,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_annotation_scale", new { name, scale, current }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_point_style", Title = "Style des points",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle l'affichage de tous les objets point du dessin (PDMODE, PDSIZE), par exemple après " +
                 "import_points : forme (dot, none, plus, cross, tick), cercle et carré autour, taille en unités du " +
                 "dessin ou en pourcentage de la hauteur de l'écran. Les réglages non fournis sont conservés.")]
    public Task<string> SetPointStyle(
        [Description("Forme : dot (point), none (rien), plus (+), cross (x), tick (trait vertical).")] string? shape = null,
        [Description("Cercle autour de la forme.")] bool? circle = null,
        [Description("Carré autour de la forme.")] bool? square = null,
        [Description("Valeur PDMODE directe (0 à 4, plus 32 pour le cercle, 64 pour le carré), à la place de shape, circle et square.")] int? mode = null,
        [Description("Taille absolue, en unités du dessin.")] double? size = null,
        [Description("Taille relative, en pourcentage de la hauteur de l'écran (à la place de size).")] double? percentOfScreen = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_point_style",
            new { shape, circle, square, mode, size, percentOfScreen }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_drawing_properties", Title = "Propriétés du dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Modifie les propriétés du dessin, comme la commande PROPDESSIN : titre, sujet, auteur, mots-clés, " +
                 "commentaires, numéro de révision, base des liens hypertexte et propriétés personnalisées (utilisables " +
                 "dans les champs des cartouches). Seules les valeurs fournies changent ; get_drawing_info les affiche.")]
    public Task<string> SetDrawingProperties(
        [Description("Titre.")] string? title = null,
        [Description("Sujet.")] string? subject = null,
        [Description("Auteur.")] string? author = null,
        [Description("Mots-clés.")] string? keywords = null,
        [Description("Commentaires.")] string? comments = null,
        [Description("Numéro de révision.")] string? revisionNumber = null,
        [Description("Base des liens hypertexte relatifs.")] string? hyperlinkBase = null,
        [Description("Propriétés personnalisées { \"nom\": \"valeur\" } ; une valeur null retire la propriété.")] Dictionary<string, string?>? customProperties = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_drawing_properties",
            new { title, subject, author, keywords, comments, revisionNumber, hyperlinkBase, customProperties },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "rename_layer", Title = "Renommer un calque",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Renomme un calque ; ses objets le suivent. Les calques 0 et Defpoints, et ceux d'une référence " +
                 "externe, ne se renomment pas. Pour réunir deux calques, utilisez delete_layers avec mergeInto.")]
    public Task<string> RenameLayer(
        [Description("Nom actuel du calque.")] string name,
        [Description("Nouveau nom.")] string newName,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("rename_layer", new { name, newName }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_layers", Title = "Supprimer ou fusionner des calques",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime des calques. Sans mergeInto, un calque qui porte encore des objets est refusé (le nombre " +
                 "d'objets est indiqué). Avec mergeInto, comme FUSIONCALQUES : les objets des calques passent d'abord " +
                 "sur le calque cible, y compris dans les définitions de blocs et les attributs, et sur les calques " +
                 "verrouillés. Les calques 0, Defpoints, courant (sauf fusion) et de références externes sont ignorés, " +
                 "comme ceux encore référencés ailleurs (signalés).")]
    public Task<string> DeleteLayers(
        [Description("Calques à supprimer ; les jokers * et ? sont acceptés.")] string[] names,
        [Description("Calque existant qui reçoit les objets des calques supprimés (fusion).")] string? mergeInto = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_layers", new { names, mergeInto }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "purge_drawing", Title = "Purger le dessin",
        ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Supprime, comme PURGE, les éléments nommés que rien n'utilise : calques, blocs, types de ligne, " +
                 "styles de texte, de cote, de tableau, de ligne de repère multiple et de multiligne. Plusieurs passes, " +
                 "car purger un bloc libère ce qu'il utilisait. Les éléments des références externes, les calques 0 et " +
                 "Defpoints et les éléments courants sont conservés. dryRun liste sans rien supprimer.")]
    public Task<string> PurgeDrawing(
        [Description("Types à purger : layers, blocks, linetypes, textStyles, dimensionStyles, tableStyles, multileaderStyles, multilineStyles. Tous si omis.")] string[]? types = null,
        [Description("Noms à purger, avec les jokers * et ? ; tous les éléments inutilisés si omis.")] string[]? names = null,
        [Description("Lister ce qui serait purgé, sans rien supprimer.")] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("purge_drawing", new { types, names, dryRun }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "audit_drawing", Title = "Contrôler l'intégrité du dessin",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Contrôle l'intégrité du dessin, comme la commande AUDIT, et renvoie son rapport (fichier .adt écrit " +
                 "à côté du dessin). Avec fixErrors, les erreurs trouvées sont corrigées ; à faire sur un dessin " +
                 "enregistré, car ces corrections peuvent ne pas s'annuler avec U.")]
    public Task<string> AuditDrawing(
        [Description("Corriger les erreurs trouvées (faux par défaut : rapport seulement).")] bool fixErrors = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("audit_drawing", new { fixErrors }, cancellationToken: cancellationToken);
}

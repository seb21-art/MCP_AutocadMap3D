using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Matériaux, lumières, soleil calé sur le système de coordonnées, et ambiance de la vue.
/// </summary>
[McpServerToolType]
public sealed class SceneTools(PluginClient plugin)
{
    [McpServerTool(Name = "list_materials", Title = "Lister les matériaux",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les matériaux du dessin : couleur diffuse, opacité (1 = opaque), brillance, réflexion, texture, " +
                 "et s'ils sont recto-verso. Les matériaux anonymes (créés par un import) sont signalés.")]
    public Task<string> ListMaterials(
        [Description("Filtre sur les noms, avec les jokers * et ?.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_materials", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_material", Title = "Créer ou modifier un matériau",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Crée un matériau (copie de basedOn, ou un matériau neutre) ou modifie un matériau existant : " +
                 "couleur diffuse, opacité de 0 à 1 (1 = opaque), brillance et réflexion de 0 à 1, texture image, recto-verso. " +
                 "ByLayer, ByBlock et leurs noms français sont réservés. " +
                 "Le nom s'utilise ensuite avec assign_material.")]
    public Task<string> SetMaterial(
        [Description("Nom du matériau.")] string name,
        [Description("Création : matériau à copier.")] string? basedOn = null,
        [Description("Couleur diffuse : index 1-255, « R,G,B » ou nom (rouge, bleu, vert…).")] string? color = null,
        [Description("Opacité de 0 (invisible) à 1 (opaque).")] double? opacity = null,
        [Description("Brillance (reflet spéculaire) de 0 (mat) à 1 (très brillant).")] double? shininess = null,
        [Description("Réflexion (effet miroir) de 0 (aucune) à 1 (miroir).")] double? reflectivity = null,
        [Description("Fichier image de la texture diffuse ; chaîne vide pour la retirer.")] string? texture = null,
        [Description("Recto-verso : le matériau s'affiche aussi sur l'envers des faces.")] bool? twoSided = null,
        [Description("Description libre.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_material",
            new { name, basedOn, color, opacity, shininess, reflectivity, texture, twoSided, description },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "assign_material", Title = "Affecter un matériau",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Affecte un matériau à des objets (handles), à des calques, ou à une face de solide " +
                 "(handle + face, indice de get_solid_topology). ByLayer et ByBlock ne s'appliquent qu'aux objets ; " +
                 "ByLayer sur une face retire le matériau propre à cette face.")]
    public Task<string> AssignMaterial(
        [Description("Nom du matériau, ou ByLayer / ByBlock pour un objet.")] string material,
        [Description("Handles des objets.")] string[]? handles = null,
        [Description("Noms des calques.")] string[]? layers = null,
        [Description("Handle du solide dont une face reçoit le matériau.")] string? handle = null,
        [Description("Indice de la face (voir get_solid_topology).")] int? face = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("assign_material",
            new { material, handles, layers, handle, face }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_lights", Title = "Lister les lumières",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les lumières du dessin (espace objet et présentations, pas celles imbriquées dans un bloc) : " +
                 "type, position, cible, intensité, couleur, ombres, état. lightingUnits indique l'unité " +
                 "(generic, si ou american).")]
    public Task<string> ListLights(
        [Description("Filtre sur les noms, avec les jokers * et ?.")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_lights", new { names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_light", Title = "Créer ou modifier une lumière",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée une lumière (sans handle) ou modifie celle désignée par handle. Types : point, spot, distant, " +
                 "web (fichier IES). Position et cible en coordonnées générales. Angles du projecteur en degrés " +
                 "(falloff plus large que hotspot). L'intensité suit LIGHTINGUNITS : sans unité, ou intensité de la lampe " +
                 "en candelas en photométrique (le facteur d'intensité reste à 1).")]
    public Task<string> SetLight(
        [Description("Handle d'une lumière existante. Omis : création.")] string? handle = null,
        [Description("point, spot, distant ou web. Obligatoire à la création.")] string? type = null,
        [Description("Nom unique. « Lumière N » si omis à la création.")] string? name = null,
        [Description("Position [x,y,z]. Obligatoire pour point et spot à la création.")] double[]? position = null,
        [Description("Cible [x,y,z], pour un projecteur ou une lumière lointaine.")] double[]? target = null,
        [Description("Direction [x,y,z] d'une lumière lointaine, à la place de target.")] double[]? direction = null,
        [Description("Intensité, positive. 1 par défaut à la création.")] double? intensity = null,
        [Description("Couleur de la lumière.")] string? color = null,
        [Description("Allumée.")] bool? on = null,
        [Description("Ombres portées.")] bool? shadows = null,
        [Description("Angle du faisceau vif, en degrés (projecteur).")] double? hotspot = null,
        [Description("Angle du faisceau total, en degrés, supérieur à hotspot.")] double? falloff = null,
        [Description("Atténuation : none, inverseLinear ou inverseSquare.")] string? attenuation = null,
        [Description("Fichier IES, pour type web.")] string? webFile = null,
        [Description("Calque, qui doit exister. Calque courant si omis à la création.")] string? layer = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_light",
            new { handle, type, name, position, target, direction, intensity, color, on, shadows, hotspot, falloff, attenuation, webFile, layer },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "delete_lights", Title = "Supprimer des lumières",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Supprime les lumières désignées par leur handle. Confirmez la liste avec l'utilisateur avant " +
                 "d'appeler cet outil.")]
    public Task<string> DeleteLights(
        [Description("Handles des lumières, tels que renvoyés par list_lights.")] string[] handles,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("delete_lights", new { handles }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_sun", Title = "Lire le soleil",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Date, heure, ombres et direction du soleil de la vue active, plus la position géographique du " +
                 "dessin (latitude, longitude, nord, fuseau). calibrated indique si cette position correspond au " +
                 "système de coordonnées du dessin, manualPlace si elle a été donnée à la main, et " +
                 "assumedCoordinateSystem signale un Lambert-93 supposé d'après l'emprise.")]
    public Task<string> GetSun(CancellationToken cancellationToken) =>
        plugin.CallForTextAsync("get_sun", cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_sun", Title = "Régler le soleil",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle le soleil de la vue active : allumage, date (AAAA-MM-JJ), heure (HH:mm ou 14h30), ombres, " +
                 "intensité. Le lieu est calé sur le système de coordonnées du dessin (centre de l'emprise, ou at) : " +
                 "latitude, longitude, nord de la projection et heure légale (Paris pour un système français ou un lieu " +
                 "en France, quel que soit le système ; ailleurs fuseau solaire sans heure d'été, à forcer avec " +
                 "utcOffset et daylightSaving). Un lieu donné par latitude et longitude est conservé par les appels suivants, " +
                 "jusqu'à calibrate vrai. north, utcOffset (heures) et daylightSaving s'appliquent même sans lieu " +
                 "trouvé. Le marqueur géographique du dessin n'est pas modifié.")]
    public Task<string> SetSun(
        [Description("Allumer le soleil.")] bool? on = null,
        [Description("Date civile AAAA-MM-JJ.")] string? date = null,
        [Description("Heure civile HH:mm, HH:mm:ss ou 14h30.")] string? time = null,
        [Description("Date et heure AAAA-MM-JJTHH:mm, à la place de date et time.")] string? dateTime = null,
        [Description("Ombres du soleil.")] bool? shadows = null,
        [Description("Intensité, 1 par défaut.")] double? intensity = null,
        [Description("Couleur du soleil.")] string? color = null,
        [Description("Point du dessin [x,y] ou [x,y,z] dont la position géographique cale le soleil. Centre de l'emprise si omis.")] double[]? at = null,
        [Description("Latitude en degrés, à la place du calcul par le système de coordonnées.")] double? latitude = null,
        [Description("Longitude en degrés, avec latitude.")] double? longitude = null,
        [Description("Nord géographique, en degrés dans le sens horaire depuis +Y. Calculé par la projection si omis.")] double? north = null,
        [Description("Décalage UTC en heures, de -12 à 14. L'heure de Paris est choisie d'office pour un système français.")] double? utcOffset = null,
        [Description("Forcer l'heure d'été. Déduite de la date pour Paris si omis.")] bool? daylightSaving = null,
        [Description("Recaler le lieu sur le système de coordonnées. Par défaut : oui, sauf si un lieu a été donné à la main.")] bool? calibrate = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_sun",
            new { on, date, time, dateTime, shadows, intensity, color, at, latitude, longitude, north, utcOffset, daylightSaving, calibrate },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "get_view_ambiance", Title = "Lire l'ambiance de la vue",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Arrière-plan, luminosité, contraste, éclairage par défaut et style visuel de la vue active, ou de " +
                 "la fenêtre désignée par viewport. Un avertissement signale un arrière-plan masqué par le style Filaire 2D.")]
    public Task<string> GetViewAmbiance(
        [Description("Handle d'une fenêtre de présentation ou de l'espace objet. Vue active si omis.")] string? viewport = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_view_ambiance", new { viewport }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_view_ambiance", Title = "Régler l'ambiance de la vue",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle l'arrière-plan (none, solid, gradient, image, sky), la luminosité, le contraste et " +
                 "l'éclairage par défaut (off, one, two) et le style visuel de la vue active ou d'une fenêtre. Le ciel " +
                 "utilise le soleil de cette vue. Le style Filaire 2D n'affiche pas l'arrière-plan : passez visualStyle " +
                 "à realistic, shaded ou conceptual pour le voir.")]
    public Task<string> SetViewAmbiance(
        [Description("Handle d'une fenêtre. Vue active si omis.")] string? viewport = null,
        [Description("Arrière-plan : none, solid, gradient, image ou sky.")] string? background = null,
        [Description("Couleur d'un fond uni.")] string? color = null,
        [Description("Couleur haute d'un dégradé.")] string? colorTop = null,
        [Description("Couleur médiane d'un dégradé.")] string? colorMiddle = null,
        [Description("Couleur basse d'un dégradé.")] string? colorBottom = null,
        [Description("Hauteur de l'horizon du dégradé, de 0 à 1.")] double? horizon = null,
        [Description("Hauteur du dégradé, de 0 à 1.")] double? height = null,
        [Description("Rotation du dégradé, en degrés.")] double? rotation = null,
        [Description("Fichier image du fond.")] string? image = null,
        [Description("Luminosité de la vue, comme la propriété Brightness d'AutoCAD.")] double? brightness = null,
        [Description("Contraste de la vue.")] double? contrast = null,
        [Description("Éclairage par défaut : off, one (une lumière lointaine) ou two.")] string? defaultLighting = null,
        [Description("Style visuel : 2dwireframe, wireframe, hidden, realistic, conceptual, shaded, shaded_edges, shades_of_gray, sketchy, xray.")] string? visualStyle = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_view_ambiance",
            new { viewport, background, color, colorTop, colorMiddle, colorBottom, horizon, height, rotation, image, brightness, contrast, defaultLighting, visualStyle },
            cancellationToken: cancellationToken);
}

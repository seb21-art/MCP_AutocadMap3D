using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>
/// Analyse bas niveau du dessin : données DXF brutes des objets (entget, entmod), export DXF, classes enregistrées,
/// modules chargés et dictionnaires. Utile pour comprendre des objets personnalisés (Covadis…).
/// </summary>
[McpServerToolType]
public sealed class DxfTools(PluginClient plugin)
{
    [McpServerTool(Name = "get_entity_dxf", Title = "Lire les données DXF d'objets",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Donne les données DXF brutes d'objets, comme (entget (handent h) '(\"*\")) : entités ou objets non " +
                 "graphiques (dictionnaires, Xrecords, objets personnalisés). La lecture passe par acdbEntGet, si bien que " +
                 "le dxfOutFields d'un objet personnalisé (COVAMANHOLE, COVAPIPE…) est réellement appelé. Pour chaque " +
                 "handle : type DXF, classe C++ (et classe d'origine d'un proxy), paires ordonnées { code, type, valeur } : " +
                 "doubles en pleine précision, points [x, y, z], noms d'entité en handles, binaire (310, 1004) en " +
                 "hexadécimal tronqué à maxBinaryBytes. followReferences ajoute les objets désignés par les codes 330 à 369 " +
                 "et 1005, sur 1 ou 2 niveaux (200 objets au plus) ; includeExtensionDictionary, le contenu du dictionnaire " +
                 "d'extension. 50 handles au plus.")]
    public Task<string> GetEntityDxf(
        [Description("Handles des objets (entités ou objets non graphiques, par exemple ceux de list_dictionary).")] string[] handles,
        [Description("Données étendues (XDATA) : \"*\" pour toutes (défaut), un nom d'application, une liste de noms, ou false pour aucune.")] object? includeXdata = null,
        [Description("Ajouter le contenu du dictionnaire d'extension de chaque objet.")] bool includeExtensionDictionary = false,
        [Description("Profondeur de suivi des références (codes 330 à 369 et 1005) : 0 (défaut), 1 ou 2.")] int followReferences = 0,
        [Description("Octets affichés par valeur binaire avant troncature (256 par défaut).")] int maxBinaryBytes = 256,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("get_entity_dxf",
            new { handles, includeXdata, includeExtensionDictionary, followReferences, maxBinaryBytes }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "set_entity_dxf", Title = "Modifier des données DXF d'un objet",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Remplace des valeurs DXF d'un objet, comme (entmod) puis (entupd), en une seule étape d'annulation " +
                 "(l'outil undo revient en arrière). Désactivé par défaut : l'utilisateur doit créer " +
                 "%USERPROFILE%\\.mcpmap3d\\settings.json avec {\"enableSetEntityDxf\": true}, ou lancer AutoCAD avec " +
                 "MCPMAP3D_ENABLE_SET_ENTITY_DXF=1. Simulation par défaut (dryRun) : rien n'est écrit, la réponse montre " +
                 "les valeurs avant et après ; avec dryRun=false, la réponse ajoute une relecture complète pour voir ce que " +
                 "l'objet a recalculé. Chaque modification vise la n-ième occurrence d'un code déjà présent, avec une " +
                 "valeur du même type. Refusé si le handle n'existe pas, si l'objet est sur un calque verrouillé, si le code " +
                 "est absent, ou pour les codes -1, 0, 5, 100, 102, 105, 1001. Un objet personnalisé peut refuser ou " +
                 "corriger une valeur incohérente : à utiliser sur des copies de dessins.")]
    public Task<string> SetEntityDxf(
        [Description("Handle de l'objet.")] string handle,
        [Description("Modifications : { code, occurrence (n-ième occurrence du code, 1 par défaut), value }. Points [x, y, z], handles en texte, binaire en hexadécimal.")] object[] changes,
        [Description("Simulation sans écriture (vrai par défaut).")] bool dryRun = true,
        [Description("Données étendues lues et réécrites : \"*\" (défaut), un nom d'application, une liste, ou false.")] object? includeXdata = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("set_entity_dxf", new { handle, changes, dryRun, includeXdata }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "export_dxf", Title = "Exporter le dessin en DXF",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Écrit une copie DXF ASCII du dessin actif (Database.DxfOut) : le dessin garde son nom, son fichier et " +
                 "son état « modifié », qui sont renvoyés pour contrôle. Versions R2018 (défaut), R2013, R2010, R2007, " +
                 "R2004, R2000 ; précision de 0 à 16 décimales (16 par défaut). Le DXF binaire n'est pas proposé par " +
                 "l'API. Un chemin relatif part du dossier du dessin actif. AutoCAD enregistre l'export comme une étape " +
                 "d'annulation vide, comptée parmi celles du connecteur (outil undo).")]
    public Task<string> ExportDxf(
        [Description("Chemin du fichier .dxf (accents acceptés).")] string filePath,
        [Description("Version DXF : R2018 (défaut), R2013, R2010, R2007, R2004 ou R2000.")] string? version = null,
        [Description("Nombre de décimales, 0 à 16 (16 par défaut).")] int precision = 16,
        [Description("DXF ASCII (seul format possible, vrai par défaut).")] bool ascii = true,
        [Description("Remplacer un fichier existant.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("export_dxf", new { filePath, version, precision, ascii, overwrite }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_classes", Title = "Lister les classes d'objets",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Classes enregistrées dans AutoCAD (dictionnaire des classes) : nom C++, nom DXF, application, classe " +
                 "parente, drapeaux de proxy, et nombre d'objets de chaque classe dans le dessin actif. Les objets proxy " +
                 "comptent pour leur classe d'origine (proxyInstances) ; une classe connue seulement par ses proxys est " +
                 "listée à part (application absente). Filtre par préfixe du nom C++ ou DXF, par exemple COVA ou Cv.")]
    public Task<string> ListClasses(
        [Description("Préfixes de nom (C++ ou DXF), par exemple [\"COVA\", \"Cv\"]. Toutes les classes si omis.")] string[]? prefix = null,
        [Description("Seulement les classes qui ont des objets dans le dessin.")] bool usedOnly = false,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_classes", new { prefix, usedOnly }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_loaded_modules", Title = "Lister les modules chargés",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Modules chargés dans AutoCAD : applications ARX et DBX (DynamicLinker) et assemblages .NET, avec chemin " +
                 "complet, version de fichier, éditeur et produit.")]
    public Task<string> ListLoadedModules(
        [Description("arx, dotnet ou all (défaut).")] string? kind = null,
        [Description("Filtre sur les noms de fichiers, avec les jokers * et ?, par exemple [\"Cova*\"].")] string[]? names = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_loaded_modules", new { kind, names }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "list_dictionary", Title = "Parcourir un dictionnaire",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Liste les entrées d'un dictionnaire du dessin : clé, handle, type DXF, classe et nombre d'entrées des " +
                 "sous-dictionnaires, pour parcourir les objets non graphiques (catalogues, réseaux, réglages). Le " +
                 "dictionnaire se désigne par un chemin de clés depuis le dictionnaire nommé (\"\" pour la racine, " +
                 "\"COVADIS/RESEAUX\"), ou par un handle (dictionnaire, ou objet dont on veut le dictionnaire d'extension). " +
                 "get_entity_dxf décrit ensuite chaque entrée par son handle.")]
    public Task<string> ListDictionary(
        [Description("Chemin de clés séparées par /, depuis le dictionnaire nommé ; racine si vide.")] string? path = null,
        [Description("Handle d'un dictionnaire, ou d'un objet pour son dictionnaire d'extension (à la place de path).")] string? handle = null,
        [Description("Index de la première entrée.")] int offset = 0,
        [Description("Nombre maximal d'entrées : 200 par défaut, 1000 au plus.")] int limit = 200,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("list_dictionary", new { path, handle, offset, limit }, cancellationToken: cancellationToken);
}

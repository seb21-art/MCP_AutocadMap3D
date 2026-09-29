using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpMap3D.Server.Tools;

/// <summary>Maillages : création, lissage, conversions et maillage de terrain.</summary>
public sealed partial class ModelingTools
{
    [McpServerTool(Name = "create_mesh", Title = "Créer un maillage",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un maillage 3D (objet MAILLAGE d'AutoCAD) à partir de sommets et de faces. Chaque face liste " +
                 "ses sommets, trois ou plus, et les faces voisines tournent dans le même sens. Un maillage fermé se " +
                 "convertit en solide avec convert_mesh.")]
    public Task<string> CreateMesh(
        [Description("Sommets [x, y, z], par exemple [[0,0,0],[10,0,0],[10,10,0],[0,10,0],[5,5,8]].")] double[][] vertices,
        [Description("Faces, chacune la liste des indices de ses sommets (à partir de 0), par exemple " +
                     "[[0,1,4],[1,2,4],[2,3,4],[3,0,4],[3,2,1,0]].")] int[][] faces,
        [Description("Niveau de lissage, de 0 (facettes planes) à 4.")] int smoothLevel = 0,
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_mesh",
            new { vertices, faces, smoothLevel, layer, color }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "smooth_mesh", Title = "Lisser des maillages",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Règle le niveau de lissage de maillages, comme LISSERPLUS et LISSERMOINS : 0 garde les facettes " +
                 "planes, chaque niveau subdivise les faces et arrondit la forme.")]
    public Task<string> SmoothMesh(
        [Description("Handles des maillages.")] string[] handles,
        [Description("Niveau de lissage visé, de 0 à 4.")] int level,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("smooth_mesh", new { handles, level }, cancellationToken: cancellationToken);

    [McpServerTool(Name = "convert_mesh", Title = "Convertir maillages, solides et surfaces",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Convertit des maillages en solides (to = solid, comme CONVSOLIDE) ou en surfaces (to = surface, " +
                 "comme CONVSURF), ou des solides, surfaces et régions en maillages (to = mesh, comme MAILLAGELISSE). " +
                 "Un maillage doit être fermé pour devenir un solide. Par défaut l'objet converti remplace l'original.")]
    public Task<string> ConvertMesh(
        [Description("Handles des objets à convertir.")] string[] handles,
        [Description("Conversion : solid, surface ou mesh.")] string to,
        [Description("solid et surface : résultat lissé (courbe) plutôt qu'à facettes planes. Faux par défaut.")] bool? smooth = null,
        [Description("solid et surface : fusionner les facettes coplanaires. Vrai par défaut.")] bool? optimize = null,
        [Description("mesh : forme des faces, optimized (défaut), quads (surtout des quadrilatères) ou triangles.")] string? meshType = null,
        [Description("mesh : écart maximal entre le maillage et la forme d'origine ; 1 % de la diagonale de l'objet " +
                     "par défaut.")] double? maxDeviation = null,
        [Description("mesh : angle maximal entre deux faces voisines, en degrés (40 par défaut).")] double? maxAngle = null,
        [Description("mesh : longueur maximale des arêtes, 0 (défaut) pour aucune limite.")] double? maxEdgeLength = null,
        [Description("Garder les objets d'origine à côté des objets convertis.")] bool keepOriginals = false,
        [Description(LayerHelp + " Par défaut : calque de l'objet d'origine.")] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("convert_mesh",
            new { handles, to, smooth, optimize, meshType, maxDeviation, maxAngle, maxEdgeLength, keepOriginals, layer, color },
            cancellationToken: cancellationToken);

    [McpServerTool(Name = "create_terrain_mesh", Title = "Créer un maillage de terrain",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crée un maillage de terrain (MNT) par triangulation de Delaunay en plan de points cotés, dont les " +
                 "altitudes sont conservées : levé topographique, sommets de courbes de niveau, semis de points. Les " +
                 "points se donnent en tableau, ou sont lus dans des objets du dessin (points, blocs par leur point " +
                 "d'insertion, lignes, polylignes 2D et 3D) désignés par handles ou par calques. Les points confondus " +
                 "en plan ne comptent qu'une fois. Renvoie les triangles, l'altitude mini et maxi, l'aire en plan et " +
                 "l'aire réelle. 100 000 points au plus.")]
    public Task<string> CreateTerrainMesh(
        [Description("Points cotés [[x, y, z], …].")] double[][]? points = null,
        [Description("Handles d'objets portant des points cotés : points, blocs, lignes, polylignes.")] string[]? handles = null,
        [Description("Calques à parcourir dans l'espace objet, jokers acceptés, par exemple [\"TOPO_PTS\", " +
                     "\"*COURBES*\"].")] string[]? layers = null,
        [Description("Longueur maximale en plan d'un côté de triangle : les triangles plus longs sont retirés, pour " +
                     "ne pas combler les creux d'un contour concave. 0 (défaut) garde toute l'enveloppe convexe.")] double maxEdgeLength = 0,
        [Description("Objet créé : mesh (maillage AutoCAD, défaut) ou polyface (maillage polyface, lu par les " +
                     "anciennes versions et la plupart des logiciels, 32 767 sommets au plus).")] string meshType = "mesh",
        [Description(LayerHelp)] string? layer = null,
        [Description(ColorHelp)] string? color = null,
        CancellationToken cancellationToken = default) =>
        plugin.CallForTextAsync("create_terrain_mesh",
            new { points, handles, layers, maxEdgeLength, meshType, layer, color }, cancellationToken: cancellationToken);
}

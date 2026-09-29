namespace McpMap3D.Server.Tools;

/// <summary>Descriptions de paramètres communes à plusieurs outils.</summary>
internal static class ToolHelp
{
    // Aides courtes, répétées sur des dizaines de paramètres : le détail complet (noms de couleurs, types de ligne
    // courants, épaisseurs normalisées) est donné une seule fois dans Instructions.
    public const string Layer = "Calque de destination, qui doit exister. Calque courant si omis.";

    public const string Color = "Couleur : index 1-255, « R,G,B », nom, ByLayer ou ByBlock.";

    public const string Linetype =
        "Type de ligne (voir list_linetypes) ou ByLayer, chargé depuis le .lin d'AutoCAD s'il manque au dessin.";

    public const string LineWeight = "Épaisseur de ligne en mm, valeur normalisée (0.25, 0.35, 0.50…), ByLayer, ByBlock ou Default.";

    /// <summary>
    /// Instructions du serveur, lues une fois par conversation : règles communes à tous les outils et détail des
    /// paramètres partagés.
    /// </summary>
    public const string Instructions =
        "Ces outils agissent sur le dessin ouvert dans AutoCAD Map 3D, sur le poste de l'utilisateur. " +
        "Appelez ping pour vérifier la liaison. Si un outil répond qu'AutoCAD est occupé, demandez à l'utilisateur " +
        "de terminer la commande en cours (Échap) ou de fermer la boîte de dialogue ouverte, puis réessayez. " +
        "Chaque appel d'un outil de modification s'annule en une seule étape avec la commande U d'AutoCAD, sauf " +
        "mention contraire dans sa description. Pour enchaîner plusieurs opérations indépendantes en un seul appel, " +
        "utilisez run_batch.\n\n" +
        "Paramètres communs :\n" +
        "- color : index AutoCAD 1-255, « R,G,B », nom (rouge, jaune, vert, cyan, bleu, magenta, blanc, noir, gris), " +
        "ByLayer ou ByBlock.\n" +
        "- linetype : par exemple CACHE, AXES, INTERROMPU, POINTILLE, TIRETPT, DIVISE, FANTOME, BORDURE, Continuous " +
        "ou ByLayer (list_linetypes donne la liste). Chargé automatiquement depuis le fichier .lin d'AutoCAD s'il " +
        "manque au dessin.\n" +
        "- lineWeight : épaisseur de ligne (trait tracé) en mm, valeur normalisée : 0.00, 0.05, 0.09, 0.13, 0.15, " +
        "0.18, 0.20, 0.25, 0.30, 0.35, 0.40, 0.50, 0.53, 0.60, 0.70, 0.80, 0.90, 1.00, 1.06, 1.20, 1.40, 1.58, 2.00, " +
        "2.11 ; ou ByLayer, ByBlock, Default.";

    public const string PlanarPoints =
        "Sommets, deux au minimum. [[x,y],…] ou points 3D de même Z : polyligne horizontale à ce Z. Points 3D " +
        "coplanaires, par exemple [[4,0,-1],[5,0,-1.5],[5,0,0]] : polyligne dans leur plan, normale calculée " +
        "(tournée pour que sa plus grande composante soit positive). Avec plane : coordonnées locales [u,v].";

    public const string Plane =
        "Plan des sommets donnés en [u,v] : xy (u = X, v = Y), xz (u = X, v = Z, normale +Y) ou yz (u = Y, v = Z, " +
        "normale +X). Pratique pour un profil dans un plan vertical.";

    public const string PlaneOffset =
        "Avec plane : position du plan sur le troisième axe (Z pour xy, Y pour xz, X pour yz), 0 par défaut.";

    public const string Normal =
        "Normale imposée [nx,ny,nz] pour des points 3D, qui doivent être dans un plan perpendiculaire. Utile si les " +
        "sommets sont alignés ou pour choisir le sens de la normale.";
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using TopologyClean = Autodesk.Gis.Map.Topology.TopologyClean;
using McpMap3D.Shared;
using AcException = Autodesk.AutoCAD.Runtime.Exception;
using MapVariable = Autodesk.Gis.Map.Topology.Variable;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Remise à plat (comme la commande FLATTEN, mais sans changer le type des objets, ce qui garde handles et données
/// d'objet) et nettoyage de dessin de Map 3D (comme MAPCLEAN). Une étape de U chacun.
/// </summary>
internal static class CleanupTools
{
    private const double Tolerance = 1e-9;

    private sealed record CleanGroup(string Action, string? Label, int Errors);

    /// <summary>
    /// Met les objets à une altitude unique (0 par défaut). Les objets plans parallèles au plan XY sont déplacés
    /// verticalement ; lignes, polylignes 3D, splines, faces 3D et lignes de repère ont chacun de leurs sommets ramené à
    /// cette altitude. Solides, surfaces, maillages et objets dans un plan incliné sont laissés tels quels et signalés.
    /// </summary>
    public static object FlattenEntities(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var elevation = reader.GetDouble("elevation", 0);
        if (!reader.Has("handles") && !reader.Has("layers") && !reader.Has("types") && !reader.Has("where") && !reader.GetBool("selection", false) && !reader.GetBool("all", false))
            throw new PipeException(PipeErrorCodes.InvalidParams,
                "Désignez les objets (handles, layers, types, where ou selection), ou all=true pour tout l'espace objet.");

        var filter = EntityFilter.Create(context, reader, transaction);
        var layers = (LayerTable)transaction.GetObject(context.Database.LayerTableId, OpenMode.ForRead);
        int flattened = 0, unchanged = 0;
        var skipped = new Dictionary<string, int>();
        void Skip(string reason) => skipped[reason] = skipped.TryGetValue(reason, out var count) ? count + 1 : 1;

        foreach (var entity in QueryTools.Source(context, reader, transaction).ToList())
        {
            if (!filter.IsMatch(entity))
                continue;
            if (((LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead)).IsLocked)
            {
                Skip("calque verrouillé");
                continue;
            }

            var result = Flatten(entity, elevation, transaction);
            if (result is null)
                flattened++;
            else if (result.Length == 0)
                unchanged++;
            else
                Skip(result);
        }

        return new
        {
            Elevation = Format.Number(elevation),
            Flattened = flattened,
            AlreadyFlat = unchanged > 0 ? unchanged : (int?)null,
            Skipped = skipped.Count > 0 ? skipped.Select(item => new { Reason = item.Key, Count = item.Value }).ToList() : null,
        };
    }

    /// <summary>null : mis à plat ; chaîne vide : déjà à l'altitude ; sinon la raison pour laquelle l'objet est ignoré.</summary>
    private static string? Flatten(Entity entity, double z, Transaction transaction)
    {
        switch (entity)
        {
            case Line line:
                if (Same(line.StartPoint.Z, z) && Same(line.EndPoint.Z, z))
                    return "";
                Write(line);
                line.StartPoint = At(line.StartPoint, z);
                line.EndPoint = At(line.EndPoint, z);
                return null;

            case DBPoint point:
                if (Same(point.Position.Z, z))
                    return "";
                Write(point);
                point.Position = At(point.Position, z);
                return null;

            case Polyline3d polyline:
            {
                var vertices = new List<PolylineVertex3d>();
                foreach (ObjectId id in polyline)
                    vertices.Add((PolylineVertex3d)transaction.GetObject(id, OpenMode.ForRead));
                if (vertices.All(vertex => Same(vertex.Position.Z, z)))
                    return "";
                Write(polyline);
                foreach (var vertex in vertices)
                {
                    vertex.UpgradeOpen();
                    vertex.Position = At(vertex.Position, z);
                }

                return null;
            }

            case Spline spline:
            {
                var count = spline.NumControlPoints;
                if (Enumerable.Range(0, count).All(i => Same(spline.GetControlPointAt(i).Z, z)))
                    return "";
                Write(spline);
                for (var i = 0; i < count; i++)
                    spline.SetControlPointAt(i, At(spline.GetControlPointAt(i), z));
                return null;
            }

            case Face face:
            {
                var corners = Enumerable.Range(0, 4).Select(i => face.GetVertexAt((short)i)).ToList();
                if (corners.All(corner => Same(corner.Z, z)))
                    return "";
                Write(face);
                for (short i = 0; i < 4; i++)
                    face.SetVertexAt(i, At(corners[i], z));
                return null;
            }

            case Leader leader:
            {
                var count = leader.NumVertices;
                if (Enumerable.Range(0, count).All(i => Same(leader.VertexAt(i).Z, z)))
                    return "";
                Write(leader);
                for (var i = 0; i < count; i++)
                    leader.SetVertexAt(i, At(leader.VertexAt(i), z));
                return null;
            }

            case Solid3d or Autodesk.AutoCAD.DatabaseServices.Surface or SubDMesh or PolyFaceMesh or PolygonMesh or Autodesk.AutoCAD.DatabaseServices.Body:
                return "solide, surface ou maillage";

            case BlockReference reference:
                // Point d'insertion à l'altitude ; le contenu du bloc n'est pas modifié.
                return Displace(reference, z - reference.Position.Z);

            case Xline or Ray:
                return "droite infinie";
        }

        // Objet plan (polyligne, cercle, arc, texte, hachure, cote…) : déplacement vertical s'il est parallèle à XY.
        Extents3d extents;
        try
        {
            extents = entity.GeometricExtents;
        }
        catch (AcException)
        {
            return "sans étendue";
        }

        if (!Same(extents.MinPoint.Z, extents.MaxPoint.Z, 1e-6 * Math.Max(1, (extents.MaxPoint - extents.MinPoint).Length)))
            return "plan incliné (pas parallèle au plan XY)";
        return Displace(entity, z - extents.MinPoint.Z);
    }

    private static string? Displace(Entity entity, double dz)
    {
        if (Same(dz, 0))
            return "";
        Write(entity);
        entity.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, dz)));
        return null;
    }

    private static void Write(DBObject entity)
    {
        if (!entity.IsWriteEnabled)
            entity.UpgradeOpen();
    }

    private static Point3d At(Point3d point, double z) => new(point.X, point.Y, z);

    private static bool Same(double a, double b, double tolerance = Tolerance) => Math.Abs(a - b) <= tolerance;

    // ---------- Nettoyage de dessin (MAPCLEAN) ----------

    /// <summary>Actions de nettoyage de Map 3D et leur numéro dans la liste d'actions.</summary>
    private static readonly (string Name, int Code, string Label)[] Actions =
    [
        ("simplify", 128, "simplifier les objets"),
        ("weed", 1024, "élaguer les polylignes"),
        ("deleteDuplicates", 8, "supprimer les doublons"),
        ("eraseShort", 1, "effacer les objets courts"),
        ("zeroLength", 256, "objets de longueur nulle"),
        ("breakCrossing", 2, "couper les objets qui se croisent"),
        ("extendUndershoots", 4, "prolonger les dépassements insuffisants"),
        ("apparentIntersection", 512, "intersection apparente"),
        ("snapClusteredNodes", 16, "fusionner les nœuds proches"),
        ("dissolvePseudoNodes", 32, "dissoudre les pseudo-nœuds"),
        ("eraseDangling", 64, "effacer les objets pendants"),
    ];

    public static object CleanDrawing(ToolContext context, JsonElement? args)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Le nettoyage de dessin demande AutoCAD Map 3D (ou Civil 3D).");
        return CleanDrawingMap3D(context, args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object CleanDrawingMap3D(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var transaction = context.RequireTransaction();
        var tolerance = reader.GetDouble("tolerance", 0.01);
        if (tolerance <= 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "tolerance doit être strictement positive.");
        var dryRun = reader.GetBool("dryRun", false);

        // Actions, dans l'ordre donné ; tolérance propre à une action possible.
        if (!reader.TryGet("actions", out var actionsElement) || actionsElement.ValueKind != JsonValueKind.Array || actionsElement.GetArrayLength() == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams,
                $"actions : liste des actions à enchaîner, parmi {string.Join(", ", Actions.Select(action => action.Name))}.");
        var plan = new List<((string Name, int Code, string Label) Action, double Tolerance)>();
        foreach (var item in actionsElement.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("action", out var named) ? named.GetString() : null;
            var action = Actions.FirstOrDefault(entry => string.Equals(entry.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (action.Name is null)
                throw new PipeException(PipeErrorCodes.InvalidParams,
                    $"Action « {name} » inconnue : {string.Join(", ", Actions.Select(entry => entry.Name))}.");
            var own = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("tolerance", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble() : tolerance;
            if (own <= 0)
                throw new PipeException(PipeErrorCodes.InvalidParams, $"Tolérance de {action.Name} : strictement positive.");
            if (action.Code == 128 && plan.Any(entry => entry.Action.Code == 128))
                throw new PipeException(PipeErrorCodes.InvalidParams, "simplify ne peut figurer qu'une fois.");
            plan.Add((action, own));
        }

        // Objets à nettoyer : mêmes filtres que list_entities, courbes, points, blocs et textes de l'espace objet.
        var filter = EntityFilter.Create(context, reader, transaction);
        var include = new ObjectIdCollection();
        foreach (var entity in QueryTools.Source(context, reader, transaction))
            if (entity is (Curve and not (Xline or Ray)) or DBPoint or BlockReference or DBText or MText && filter.IsMatch(entity))
                include.Add(entity.ObjectId);
        if (include.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun objet à nettoyer ne correspond aux filtres.");

        var anchor = new ObjectIdCollection();
        var anchorLayers = NameFilter.Create(reader.GetStrings("anchorLayers"));
        if (anchorLayers is not null)
            foreach (ObjectId id in ReadTools.OpenSpace(context, "model", transaction))
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && anchorLayers.IsMatch(entity.Layer))
                    anchor.Add(id);

        var duplicates = new Dictionary<string, short>
        {
            ["INCLUDE_LINEAROBJS"] = 1,
            ["INCLUDE_POINTS"] = (short)(reader.GetBool("includePoints", true) ? 1 : 0),
            ["INCLUDE_BLOCKS"] = (short)(reader.GetBool("includeBlocks", false) ? 1 : 0),
            ["INCLUDE_TEXT"] = (short)(reader.GetBool("includeText", false) ? 1 : 0),
            ["INCLUDE_MTEXT"] = (short)(reader.GetBool("includeText", false) ? 1 : 0),
            ["INCLUDE_ZVALUES"] = (short)(reader.GetBool("compareZ", false) ? 1 : 0),
        };

        var groups = new List<CleanGroup>();
        object? sets = null;
        var before = include.Count;

        // Le moteur de nettoyage ouvre et modifie lui-même les objets : il travaille hors de la transaction de l'outil,
        // toujours en contexte commande (même étape de U).
        context.AfterCommit(() =>
        {
            using var variables = new MapVariable();
            Set(variables, "INCLUDEOBJS_AUTOSELECT", (short)0);
            Set(variables, "MAINTAIN_MARKERS", (short)0);
            Set(variables, "DEL_MARKER", (short)1);
            foreach (var (action, actionTolerance) in plan)
            {
                using var settings = new MapVariable();
                Set(settings, "CLEAN_TOL", actionTolerance);
                if (action.Code is 8)
                    foreach (var (name, value) in duplicates)
                        Set(settings, name, value);
                if (action.Code is 16)
                {
                    Set(settings, "INCLUDE_POINTS", duplicates["INCLUDE_POINTS"]);
                    Set(settings, "INCLUDE_BLOCKS", duplicates["INCLUDE_BLOCKS"]);
                }

                if (action.Code is 1024)
                    Set(settings, "WEED_DISTANCE", actionTolerance);
                variables.InsertActionToList(-1, action.Code, settings);
            }

            using var clean = new TopologyClean();
            if (anchor.Count > 0)
                clean.InitAnchorSet(variables, anchor);
            clean.Init(variables, include);
            clean.Start();
            clean.GroupNext();
            while (!clean.Completed)
            {
                var action = Actions.FirstOrDefault(entry => entry.Code == clean.GroupType);
                // Une action peut se découper en plusieurs groupes successifs (par type d'objet) : un seul compte par action.
                var name = action.Name ?? clean.GroupType.ToString();
                if (groups.Count > 0 && groups[^1].Action == name)
                    groups[^1] = groups[^1] with { Errors = groups[^1].Errors + clean.GroupErrorCount };
                else
                    groups.Add(new CleanGroup(name, action.Label, clean.GroupErrorCount));
                if (!dryRun)
                    clean.GroupFix();
                clean.GroupNext();
            }

            if (dryRun)
            {
                clean.Cancel();
                return;
            }

            clean.End();
            sets = new
            {
                Created = Count(() => clean.CreatedSelectionSet),
                Modified = Count(() => clean.ModifiedSelectionSet),
                Unchanged = Count(() => clean.UnchangedSelectionSet),
            };
        });

        context.ResultAfterCommit = () => new
        {
            DryRun = dryRun ? true : (bool?)null,
            Objects = before,
            Anchored = anchor.Count > 0 ? anchor.Count : (int?)null,
            Tolerance = Format.Number(tolerance),
            Groups = groups,
            Errors = groups.Sum(group => group.Errors),
            Result = sets,
        };
        return new { };
    }

    /// <summary>Taille d'un jeu d'objets du nettoyage : Map 3D lève une exception quand le jeu est vide.</summary>
    private static int Count(Func<ObjectIdCollection?> set)
    {
        try
        {
            return set()?.Count ?? 0;
        }
        catch (Autodesk.Gis.Map.MapTopologyException)
        {
            return 0;
        }
    }

    private static void Set(MapVariable variable, string name, short value)
    {
        using var buffer = new ResultBuffer(new TypedValue((int)Autodesk.AutoCAD.Runtime.LispDataType.Int16, value));
        variable.Set(name, buffer);
    }

    private static void Set(MapVariable variable, string name, double value)
    {
        using var buffer = new ResultBuffer(new TypedValue((int)Autodesk.AutoCAD.Runtime.LispDataType.Double, value));
        variable.Set(name, buffer);
    }
}

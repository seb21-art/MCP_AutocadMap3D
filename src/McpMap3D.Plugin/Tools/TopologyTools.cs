using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Gis.Map.Topology;
using McpMap3D.Shared;
using AcOpenMode = Autodesk.AutoCAD.DatabaseServices.OpenMode;
using TopoOpenMode = Autodesk.Gis.Map.Topology.OpenMode;

namespace McpMap3D.Plugin.Tools;

/// <summary>
/// Topologies de réseau de Map 3D : création à partir des courbes (liens) et des points ou blocs (nœuds) du dessin,
/// puis analyses sur le graphe qu'elles décrivent : plus court chemin, propagation, amont vers une source, aval
/// (ce qui n'est plus alimenté si un point est coupé), en tenant compte des vannes fermées.
/// </summary>
internal static class TopologyTools
{
    private const int MaxReturned = 2000;

    public static object CreateNetworkTopology(ToolContext context, JsonElement? args) =>
        Require(() => CreateCore(context, args));

    public static object TraceNetwork(ToolContext context, JsonElement? args) =>
        Require(() => TraceCore(context, args));

    public static object DeleteTopology(ToolContext context, JsonElement? args) =>
        Require(() => DeleteCore(context, args));

    private static object Require(Func<object> action)
    {
        if (!MapInfo.IsAvailable)
            throw new PipeException(PipeErrorCodes.MapUnavailable, "Les topologies requièrent AutoCAD Map 3D.");
        return Guard(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Guard(Func<object> action)
    {
        try
        {
            return action();
        }
        catch (Autodesk.Gis.Map.MapException ex)
        {
            throw MapTools.Translate(ex, "topologie");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object CreateCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("name").Trim();
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Nom de topologie « {name} » : lettres, chiffres, _ et - seulement.");

        var transaction = context.RequireTransaction();
        var filter = EntityFilter.Create(context, reader, transaction);
        var edges = new ObjectIdCollection();
        foreach (var entity in QueryTools.Source(context, reader, transaction))
        {
            if (entity is Curve and not (Xline or Ray) && filter.IsMatch(entity))
                edges.Add(entity.ObjectId);
        }

        if (edges.Count == 0)
            throw new PipeException(PipeErrorCodes.InvalidParams, "Aucune courbe (ligne, polyligne, arc) ne correspond aux filtres : rien à relier.");

        var nodes = new ObjectIdCollection();
        var nodeLayers = NameFilter.Create(reader.GetStrings("nodeLayers"));
        if (nodeLayers is not null)
        {
            var model = ReadTools.OpenSpace(context, "model", transaction);
            foreach (ObjectId id in model)
            {
                if (transaction.GetObject(id, AcOpenMode.ForRead) is Entity { } candidate and (DBPoint or BlockReference) && nodeLayers.IsMatch(candidate.Layer))
                    nodes.Add(id);
            }
        }

        var topologies = MapInfo.RequireProject(context.Database).Topologies;
        if (topologies.Exists(name))
        {
            if (!reader.GetBool("replace", false))
                throw new PipeException(PipeErrorCodes.InvalidParams, $"La topologie « {name} » existe déjà : replace=true la remplace.");
            topologies.Delete(name, false);
        }

        var tolerance = reader.GetDouble("tolerance", 0.01);
        topologies.Create(name, edges, nodes, new ObjectIdCollection(), TopologyTypes.Linear, 0, tolerance);

        var created = topologies[name];
        created.Open(TopoOpenMode.ForRead);
        try
        {
            return new
            {
                Name = name,
                Links = created.GetFullEdges().Count,
                Nodes = created.GetNodes().Count,
                SourceCurves = edges.Count,
                SourcePoints = nodes.Count,
                Tolerance = tolerance,
            };
        }
        finally
        {
            created.Close();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object DeleteCore(ToolContext context, JsonElement? args)
    {
        var name = new ArgReader(args).RequireString("name").Trim();
        var topologies = MapInfo.RequireProject(context.Database).Topologies;
        if (!topologies.Exists(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucune topologie « {name} » dans ce dessin.");
        topologies.Delete(name, false);
        return new { Deleted = name };
    }

    /// <summary>Graphe d'une topologie : nœuds (position, objet) et liens (longueur, objet, extrémités).</summary>
    private sealed class Graph
    {
        public readonly Dictionary<int, (Point3d Location, ObjectId Entity)> Nodes = [];
        public readonly List<(int Id, int From, int To, double Length, ObjectId Entity)> Edges = [];
        public readonly Dictionary<int, List<int>> Adjacent = [];

        public int Other(int edge, int node) => Edges[edge].From == node ? Edges[edge].To : Edges[edge].From;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object TraceCore(ToolContext context, JsonElement? args)
    {
        var reader = new ArgReader(args);
        var name = reader.RequireString("topology").Trim();
        var mode = reader.RequireString("mode").Trim().ToLowerInvariant();
        var topologies = MapInfo.RequireProject(context.Database).Topologies;
        if (!topologies.Exists(name))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Aucune topologie « {name} » dans ce dessin (voir create_network_topology).");

        var model = topologies[name];
        model.Open(TopoOpenMode.ForRead);
        Graph graph;
        try
        {
            graph = Load(model);
        }
        finally
        {
            model.Close();
        }

        var closed = new HashSet<int>(reader.TryGet("closed", out var closedPoints)
            ? Locations(context, closedPoints, "closed").Select(point => Nearest(graph, point).Node)
            : []);
        var (start, startDistance) = reader.Has("start") ? Nearest(graph, Location(context, reader, "start")) : (-1, 0);
        HashSet<int> edges;
        HashSet<int> nodes;
        string description;
        switch (mode)
        {
            case "path":
            {
                var (end, _) = Nearest(graph, Location(context, reader, "end"));
                var tree = Dijkstra(graph, Require(start), closed);
                if (!tree.Distance.ContainsKey(end))
                    throw new PipeException(PipeErrorCodes.InvalidParams, "Aucun chemin entre start et end (réseau coupé, ou vanne fermée).");
                (nodes, edges) = PathTo(graph, tree, end);
                description = "plus court chemin de start à end";
                break;
            }

            case "flood":
            {
                var max = reader.GetDouble("maxDistance", double.PositiveInfinity);
                var tree = Dijkstra(graph, Require(start), closed);
                nodes = tree.Distance.Where(pair => pair.Value <= max).Select(pair => pair.Key).ToHashSet();
                edges = Enumerable.Range(0, graph.Edges.Count)
                    .Where(i => nodes.Contains(graph.Edges[i].From) || nodes.Contains(graph.Edges[i].To))
                    .Where(i => !closed.Contains(graph.Edges[i].From) && !closed.Contains(graph.Edges[i].To) || nodes.Contains(graph.Edges[i].From) && nodes.Contains(graph.Edges[i].To))
                    .ToHashSet();
                description = double.IsInfinity(max) ? "tout ce qui est relié à start" : $"ce qui est relié à start à moins de {Format.Number(max)}";
                break;
            }

            case "upstream":
            {
                var (source, _) = Nearest(graph, Location(context, reader, "source"));
                var tree = Dijkstra(graph, Require(start), closed);
                if (!tree.Distance.ContainsKey(source))
                    throw new PipeException(PipeErrorCodes.InvalidParams, "start n'est pas relié à la source (réseau coupé, ou vanne fermée).");
                (nodes, edges) = PathTo(graph, tree, source);
                description = "chemin de start vers la source (amont)";
                break;
            }

            case "downstream":
            case "isolated":
            {
                var (source, _) = Nearest(graph, Location(context, reader, "source"));
                var cut = new HashSet<int>(closed);
                if (mode == "downstream")
                    cut.Add(Require(start));
                if (cut.Contains(source))
                    throw new PipeException(PipeErrorCodes.InvalidParams, "La source elle-même est coupée.");
                var fed = Dijkstra(graph, source, cut).Distance.Keys.ToHashSet();
                nodes = graph.Nodes.Keys.Where(node => !fed.Contains(node) && !cut.Contains(node)).ToHashSet();
                if (mode == "downstream")
                    nodes.Add(start);
                edges = Enumerable.Range(0, graph.Edges.Count)
                    .Where(i => !(fed.Contains(graph.Edges[i].From) && fed.Contains(graph.Edges[i].To)))
                    .Where(i => nodes.Contains(graph.Edges[i].From) || nodes.Contains(graph.Edges[i].To))
                    .ToHashSet();
                description = mode == "downstream"
                    ? "ce qui n'est plus alimenté depuis la source si start est coupé (aval)"
                    : "ce qui n'est plus alimenté depuis la source avec les vannes fermées";
                break;
            }

            default:
                throw new PipeException(PipeErrorCodes.InvalidParams, $"mode « {mode} » : path, flood, upstream, downstream ou isolated.");
        }

        var edgeHandles = edges.Select(i => graph.Edges[i].Entity).Where(id => !id.IsNull).Distinct().ToList();
        var nodeHandles = nodes.Select(node => graph.Nodes[node].Entity).Where(id => !id.IsNull).Distinct().ToList();
        if (reader.GetBool("select", false))
            context.Editor.SetImpliedSelection(edgeHandles.Concat(nodeHandles).ToArray());

        return new
        {
            Topology = name,
            Mode = mode,
            Description = description,
            StartSnapDistance = start >= 0 ? Format.Number(startDistance) : (double?)null,
            Links = edges.Count,
            Nodes = nodes.Count,
            Length = Format.Number(edges.Sum(i => graph.Edges[i].Length)),
            ClosedNodes = closed.Count > 0 ? closed.Count : (int?)null,
            LinkHandles = edgeHandles.Take(MaxReturned).Select(id => id.Handle.ToString()).ToArray(),
            NodeHandles = nodeHandles.Count > 0 ? nodeHandles.Take(MaxReturned).Select(id => id.Handle.ToString()).ToArray() : null,
            Truncated = edgeHandles.Count > MaxReturned || nodeHandles.Count > MaxReturned ? true : (bool?)null,
        };

        int Require(int node) => node >= 0 ? node : throw new PipeException(PipeErrorCodes.InvalidParams, $"Le mode « {mode} » demande start.");
    }

    private static Graph Load(TopologyModel model)
    {
        var graph = new Graph();
        foreach (Node node in model.GetNodes())
        {
            graph.Nodes[node.ID] = (node.Location, EntityOf(node));
            graph.Adjacent[node.ID] = [];
        }

        foreach (FullEdge edge in model.GetFullEdges())
        {
            var (from, to) = (edge.GetNextNode(false).ID, edge.GetNextNode(true).ID);
            graph.Edges.Add((edge.ID, from, to, edge.Length, edge.Entity));
            graph.Adjacent[from].Add(graph.Edges.Count - 1);
            if (to != from)
                graph.Adjacent[to].Add(graph.Edges.Count - 1);
        }

        return graph;
    }

    /// <summary>
    /// Objet du dessin d'un nœud. Un nœud implicite, créé par Map 3D à l'extrémité d'une courbe, n'en a pas : la
    /// lecture lève ErrorTopologyImplicitNode.
    /// </summary>
    private static ObjectId EntityOf(Node node)
    {
        try
        {
            return node.Entity;
        }
        catch (Autodesk.Gis.Map.MapException)
        {
            return ObjectId.Null;
        }
    }

    /// <summary>Plus courtes distances depuis un nœud, sans traverser les nœuds coupés.</summary>
    private static (Dictionary<int, double> Distance, Dictionary<int, int> Via) Dijkstra(Graph graph, int start, HashSet<int> cut)
    {
        var distance = new Dictionary<int, double> { [start] = 0 };
        var via = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0);
        while (queue.TryDequeue(out var node, out var reached))
        {
            if (reached > distance[node] || (cut.Contains(node) && node != start))
                continue;
            foreach (var edge in graph.Adjacent[node])
            {
                var next = graph.Other(edge, node);
                var candidate = reached + graph.Edges[edge].Length;
                if (distance.TryGetValue(next, out var known) && known <= candidate)
                    continue;
                distance[next] = candidate;
                via[next] = edge;
                queue.Enqueue(next, candidate);
            }
        }

        // Un nœud coupé est atteint mais ne transmet rien : il reste hors du réseau alimenté.
        foreach (var node in cut.Where(node => node != start))
            distance.Remove(node);
        return (distance, via);
    }

    private static (HashSet<int> Nodes, HashSet<int> Edges) PathTo(Graph graph, (Dictionary<int, double> Distance, Dictionary<int, int> Via) tree, int end)
    {
        var (nodes, edges) = (new HashSet<int> { end }, new HashSet<int>());
        for (var node = end; tree.Via.TryGetValue(node, out var edge); node = graph.Other(edge, node))
        {
            edges.Add(edge);
            nodes.Add(graph.Other(edge, node));
        }

        return (nodes, edges);
    }

    private static (int Node, double Distance) Nearest(Graph graph, Point3d point)
    {
        var best = graph.Nodes.MinBy(pair => pair.Value.Location.DistanceTo(point));
        return (best.Key, best.Value.Location.DistanceTo(point));
    }

    /// <summary>Point [x, y] ou handle d'un objet (point, bloc : sa position ; courbe : son point de départ).</summary>
    private static Point3d Location(ToolContext context, ArgReader reader, string name)
    {
        if (!reader.TryGet(name, out var value))
            throw new PipeException(PipeErrorCodes.InvalidParams, $"Le paramètre « {name} » est obligatoire : un point [x, y] ou un handle.");
        return Locations(context, JsonSerializer.SerializeToElement(new[] { value }), name)[0];
    }

    private static List<Point3d> Locations(ToolContext context, JsonElement values, string name)
    {
        if (values.ValueKind != JsonValueKind.Array)
            throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » attend une liste de points [x, y] ou de handles.");
        var transaction = context.RequireTransaction();
        return values.EnumerateArray().Select(value => value.ValueKind switch
        {
            JsonValueKind.String => transaction.GetObject(Handles.ResolveOne(context, value.GetString()!), AcOpenMode.ForRead) switch
            {
                DBPoint point => point.Position,
                BlockReference block => block.Position,
                Curve curve => curve.StartPoint,
                _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » : l'objet {value.GetString()} n'est ni un point, ni un bloc, ni une courbe."),
            },
            JsonValueKind.Array => new ArgReader(JsonSerializer.SerializeToElement(new { p = value })).RequirePoint("p"),
            _ => throw new PipeException(PipeErrorCodes.InvalidParams, $"« {name} » : point [x, y] ou handle attendu."),
        }).ToList();
    }
}

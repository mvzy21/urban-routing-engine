namespace UrbanRouting;

/// <summary>Which cost a route minimises: kilometres or minutes.</summary>
public enum Metric { Distance, Time }

/// <summary>A location (road junction): id, position x/y in km, and a name.</summary>
public sealed class Node
{
    public Node(int id, double x, double y, string name)
    {
        Id = id;
        X = x;
        Y = y;
        Name = name;
    }

    public int Id { get; }
    public double X { get; }
    public double Y { get; }
    public string Name { get; set; }
}

/// <summary>A connection between locations A and B, with its distance, speed limit and travel time.</summary>
public sealed class Road
{
    public required int Id { get; init; }
    public required int A { get; init; }
    public required int B { get; init; }
    public required bool TwoWay { get; init; }
    public required double DistanceKm { get; init; }
    public required double SpeedLimitKmh { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Travel time in minutes = distance / speed limit.</summary>
    public double TravelTimeMin => DistanceKm / SpeedLimitKmh * 60.0;
}

/// <summary>One direction of travel along a road: From -> To.</summary>
public sealed record Edge(int From, int To, Road Road)
{
    public double Cost(Metric metric) => metric == Metric.Distance ? Road.DistanceKm : Road.TravelTimeMin;
}

/// <summary>
/// The network stored as an ADJACENCY LIST: for every location, the list of edges leaving it.
///
/// Why a list and not a matrix (see AdjacencyMatrix below)?
///  - Streets are sparse (about 2 edges per location), so a list uses O(V + E) memory; a matrix uses O(V^2).
///  - Every algorithm asks "what are u's neighbours?": O(deg u) for a list, O(V) for a matrix.
/// Location ids are 0..V-1, so per-location data (dist[], visited[]) can live in plain arrays.
/// </summary>
public sealed class RoadNetwork
{
    private readonly List<Node> nodes = new();
    private readonly List<Road> roads = new();
    private readonly List<List<Edge>> outEdges = new();   // outEdges[u] = edges leaving u    (the adjacency list)
    private readonly List<List<Edge>> inEdges = new();    // inEdges[v]  = edges arriving at v (the same list, reversed)

    public int NodeCount => nodes.Count;
    public int RoadCount => roads.Count;
    public int EdgeCount { get; private set; }
    public IReadOnlyList<Node> Nodes => nodes;
    public IReadOnlyList<Road> Roads => roads;

    public int AddNode(double x, double y, string name = "")
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException($"Location '{name}' has invalid coordinates.");
        nodes.Add(new Node(nodes.Count, x, y, name));
        outEdges.Add(new List<Edge>());
        inEdges.Add(new List<Edge>());
        return nodes.Count - 1;
    }

    /// <summary>Adds a road (one edge, or two if it is two-way). Rejects data that would break Dijkstra.</summary>
    public Road AddRoad(int a, int b, double distanceKm, double speedLimitKmh, bool twoWay = true, string name = "")
    {
        CheckNode(a);
        CheckNode(b);
        if (a == b) throw new ArgumentException($"A road from location {a} back to itself is not allowed.");
        if (!(distanceKm > 0) || !double.IsFinite(distanceKm)) throw new ArgumentException($"Road {a}-{b}: distance must be > 0.");
        if (!(speedLimitKmh > 0) || !double.IsFinite(speedLimitKmh)) throw new ArgumentException($"Road {a}-{b}: speed limit must be > 0.");

        var road = new Road { Id = roads.Count, A = a, B = b, TwoWay = twoWay, DistanceKm = distanceKm, SpeedLimitKmh = speedLimitKmh, Name = name };
        roads.Add(road);
        AddEdge(new Edge(a, b, road));
        if (twoWay) AddEdge(new Edge(b, a, road));
        return road;
    }

    private void AddEdge(Edge e)
    {
        outEdges[e.From].Add(e);
        inEdges[e.To].Add(e);
        EdgeCount++;
    }

    public Node GetNode(int id)
    {
        CheckNode(id);
        return nodes[id];
    }

    /// <summary>The adjacency list of u: O(deg u) to walk.</summary>
    public IReadOnlyList<Edge> OutEdges(int u)
    {
        CheckNode(u);
        return outEdges[u];
    }

    public IReadOnlyList<Edge> InEdges(int v)
    {
        CheckNode(v);
        return inEdges[v];
    }

    /// <summary>Neighbours ignoring direction (used for "is the network in one piece?").</summary>
    public IEnumerable<(int Neighbour, Road Road)> NeighboursAnyDirection(int u)
    {
        foreach (var e in OutEdges(u)) yield return (e.To, e.Road);
        foreach (var e in InEdges(u)) yield return (e.From, e.Road);
    }

    /// <summary>Is there a road u -> v? O(deg u): the one question a matrix answers faster.</summary>
    public Edge? FindEdge(int u, int v) => OutEdges(u).FirstOrDefault(e => e.To == v);

    public double StraightLineKm(int a, int b)
    {
        var p = GetNode(a);
        var q = GetNode(b);
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    }

    private void CheckNode(int id)
    {
        if (id < 0 || id >= nodes.Count)
            throw new ArgumentOutOfRangeException(nameof(id), $"Location {id} does not exist (network has {nodes.Count}).");
    }
}

/// <summary>
/// Constrained routing: locations and roads that are blocked. The algorithms simply SKIP them.
/// Each check is an O(1) HashSet lookup, so the algorithms' Big-O does not change.
/// </summary>
public sealed class RouteConstraints
{
    public HashSet<int> BlockedNodes { get; } = new();          // e.g. an accident at a junction
    public HashSet<int> BlockedRoads { get; } = new();          // e.g. a street closed for a parade

    public static RouteConstraints None() => new();

    public bool AllowsNode(int id) => !BlockedNodes.Contains(id);

    public bool AllowsRoad(Road r) => !BlockedRoads.Contains(r.Id);

    public bool AllowsEdge(Edge e) => AllowsRoad(e.Road) && AllowsNode(e.To);
}

/// <summary>
/// The REJECTED alternative, kept only so the benchmark can compare it: a V x V table where
/// cost[u, v] = distance of the road u -> v, or infinity if there is none.
///   Memory O(V^2) | all neighbours of u O(V) | "is there a road u -> v?" O(1)
/// </summary>
public sealed class AdjacencyMatrix
{
    private readonly double[,] cost;

    public AdjacencyMatrix(RoadNetwork g)
    {
        int n = g.NodeCount;
        cost = new double[n, n];
        for (int u = 0; u < n; u++)
            for (int v = 0; v < n; v++)
                cost[u, v] = double.PositiveInfinity;
        foreach (var node in g.Nodes)
            foreach (var e in g.OutEdges(node.Id))
                cost[e.From, e.To] = Math.Min(cost[e.From, e.To], e.Road.DistanceKm);
    }

    public bool HasEdge(int u, int v) => !double.IsPositiveInfinity(cost[u, v]);

    public int CountNeighbours(int u)
    {
        int count = 0;
        for (int v = 0; v < cost.GetLength(1); v++)
            if (HasEdge(u, v)) count++;
        return count;
    }
}

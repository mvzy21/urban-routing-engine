namespace UrbanRouting;

/// <summary>Result of a route search.</summary>
public sealed class PathResult
{
    public bool Found { get; init; }
    public IReadOnlyList<int> Path { get; init; } = Array.Empty<int>();       // locations, in order
    public IReadOnlyList<Edge> Edges { get; init; } = Array.Empty<Edge>();    // roads driven, in order
    public double Cost { get; init; } = double.PositiveInfinity;
    public int Explored { get; init; }                                        // how much work was done
    public double DistanceKm => Edges.Sum(e => e.Road.DistanceKm);
    public double TimeMin => Edges.Sum(e => e.Road.TravelTimeMin);
}

/// <summary>
/// Shortest paths. Three methods are compared:
///   Brute force              O(V!) worst case   try every route; always correct, tiny graphs only
///   Dijkstra + array scan    O(V^2 + E)         the priority queue is a linear search over all locations
///   Dijkstra + binary heap   O((V + E) log V)   the priority queue is our MinHeap   <- chosen (streets are sparse)
/// Dijkstra is GREEDY: it always settles the closest unsettled location next. It needs costs >= 0.
/// </summary>
public static class ShortestPath
{
    public static PathResult Dijkstra(RoadNetwork g, int source, int target, Metric metric, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        g.GetNode(source);                                     // throws if an id is invalid
        g.GetNode(target);
        if (!rules.AllowsNode(source) || !rules.AllowsNode(target)) return new PathResult();

        var dist = new double[g.NodeCount];                    // best known cost from the source
        Array.Fill(dist, double.PositiveInfinity);
        var cameBy = new Edge?[g.NodeCount];                   // the road used to reach each location
        var settled = new bool[g.NodeCount];                   // true once a location's cost is final
        var heap = new MinHeap<int>();

        dist[source] = 0;
        heap.Push(source, 0);
        int explored = 0;

        while (heap.TryPop(out int u, out _))
        {
            if (settled[u]) continue;                          // stale copy (lazy deletion: the heap has no decrease-key)
            settled[u] = true;
            explored++;
            if (u == target) return BuildPath(source, target, dist[target], cameBy, explored);

            foreach (var e in g.OutEdges(u))
            {
                if (settled[e.To] || !rules.AllowsEdge(e)) continue;   // constrained routing = skip forbidden roads
                double newDist = dist[u] + e.Cost(metric);
                if (newDist < dist[e.To])                              // relaxation
                {
                    dist[e.To] = newDist;
                    cameBy[e.To] = e;
                    heap.Push(e.To, newDist);
                }
            }
        }
        return new PathResult { Explored = explored };         // no route
    }

    /// <summary>Same Dijkstra, but the priority queue is an array: scan every location for the smallest. O(V^2 + E).</summary>
    public static PathResult DijkstraArray(RoadNetwork g, int source, int target, Metric metric, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        g.GetNode(source);
        g.GetNode(target);
        if (!rules.AllowsNode(source) || !rules.AllowsNode(target)) return new PathResult();

        var dist = new double[g.NodeCount];
        Array.Fill(dist, double.PositiveInfinity);
        var cameBy = new Edge?[g.NodeCount];
        var settled = new bool[g.NodeCount];
        dist[source] = 0;
        int explored = 0;

        while (true)
        {
            int u = -1;
            for (int v = 0; v < g.NodeCount; v++)              // linear search: O(V) every step
                if (!settled[v] && dist[v] < double.PositiveInfinity && (u == -1 || dist[v] < dist[u])) u = v;
            if (u == -1) return new PathResult { Explored = explored };
            settled[u] = true;
            explored++;
            if (u == target) return BuildPath(source, target, dist[target], cameBy, explored);

            foreach (var e in g.OutEdges(u))
            {
                if (settled[e.To] || !rules.AllowsEdge(e)) continue;
                double newDist = dist[u] + e.Cost(metric);
                if (newDist < dist[e.To])
                {
                    dist[e.To] = newDist;
                    cameBy[e.To] = e;
                }
            }
        }
    }

    /// <summary>
    /// BRUTE FORCE: depth-first search with backtracking tries EVERY route that never revisits a location,
    /// and keeps the cheapest. Always correct, but exponential, so it refuses big graphs. Explored = routes tried.
    /// </summary>
    public static PathResult BruteForce(RoadNetwork g, int source, int target, Metric metric, int maxJunctions = 60)
    {
        g.GetNode(source);
        g.GetNode(target);
        if (g.NodeCount > maxJunctions) throw new InvalidOperationException($"Brute force is limited to {maxJunctions} locations.");

        var onRoute = new bool[g.NodeCount];
        var route = new List<Edge>();
        List<Edge>? best = null;
        double bestCost = double.PositiveInfinity;
        int routesTried = 0;

        void Explore(int u, double costSoFar)
        {
            if (u == target)                                   // base case: a complete route
            {
                routesTried++;
                if (costSoFar < bestCost) { bestCost = costSoFar; best = new List<Edge>(route); }
                return;
            }
            onRoute[u] = true;
            foreach (var e in g.OutEdges(u))
            {
                if (onRoute[e.To]) continue;                   // never visit a location twice
                route.Add(e);
                Explore(e.To, costSoFar + e.Cost(metric));
                route.RemoveAt(route.Count - 1);               // backtrack
            }
            onRoute[u] = false;
        }

        Explore(source, 0);
        if (best == null) return new PathResult { Explored = routesTried };
        var path = new List<int> { source };
        path.AddRange(best.Select(e => e.To));
        return new PathResult { Found = true, Path = path, Edges = best, Cost = bestCost, Explored = routesTried };
    }

    private static PathResult BuildPath(int source, int target, double cost, Edge?[] cameBy, int explored)
    {
        var edges = new List<Edge>();
        for (int v = target; v != source; v = cameBy[v]!.From) edges.Add(cameBy[v]!);   // walk back to the source
        edges.Reverse();
        var path = new List<int> { source };
        path.AddRange(edges.Select(e => e.To));
        return new PathResult { Found = true, Path = path, Edges = edges, Cost = cost, Explored = explored };
    }
}

/// <summary>"Which locations can I reach within a budget?"</summary>
public static class Reachability
{
    /// <summary>
    /// Bounded Dijkstra: the heap gives locations out cheapest first, so as soon as the cheapest one left is
    /// over the budget, everything else is too and we stop. Cost grows with the answer, not the city.
    /// Returns (location, cost) pairs, cheapest first.
    /// </summary>
    public static List<(int Node, double Cost)> WithinBudget(RoadNetwork g, int source, double budget, Metric metric, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        g.GetNode(source);
        if (double.IsNaN(budget) || budget < 0) throw new ArgumentOutOfRangeException(nameof(budget), "Budget must be >= 0.");
        var result = new List<(int, double)>();
        if (!rules.AllowsNode(source)) return result;

        var dist = new Dictionary<int, double> { [source] = 0 };   // only locations we touch
        var settled = new HashSet<int>();
        var heap = new MinHeap<int>();
        heap.Push(source, 0);

        while (heap.TryPop(out int u, out double d))
        {
            if (!settled.Add(u)) continue;                     // stale copy
            if (d > budget) break;                             // everything left costs even more
            result.Add((u, d));
            foreach (var e in g.OutEdges(u))
            {
                if (settled.Contains(e.To) || !rules.AllowsEdge(e)) continue;
                double nd = d + e.Cost(metric);
                if (nd <= budget && (!dist.TryGetValue(e.To, out double old) || nd < old))
                {
                    dist[e.To] = nd;
                    heap.Push(e.To, nd);
                }
            }
        }
        return result;
    }

    /// <summary>BFS by NUMBER OF ROADS. Shown in the demo to prove hop count is the wrong measure for a time budget.</summary>
    public static List<int> WithinHops(RoadNetwork g, int source, int maxHops, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        var hops = new Dictionary<int, int> { [source] = 0 };
        var queue = new Queue<int>();
        var order = new List<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            order.Add(u);
            if (hops[u] == maxHops) continue;
            foreach (var e in g.OutEdges(u))
                if (!hops.ContainsKey(e.To) && rules.AllowsEdge(e))
                {
                    hops[e.To] = hops[u] + 1;
                    queue.Enqueue(e.To);
                }
        }
        return order;
    }
}

/// <summary>Connectivity: traversal problems, O(V + E). BFS uses a queue, DFS a stack.</summary>
public static class Connectivity
{
    /// <summary>Breadth-first search, ignoring direction. Returns how many locations it reached and its largest queue.</summary>
    public static (int Visited, int PeakFrontier) Bfs(RoadNetwork g, int start, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        var visited = new bool[g.NodeCount];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        int count = 0, peak = 1;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            count++;
            foreach (var (v, road) in g.NeighboursAnyDirection(u))
                if (!visited[v] && rules.AllowsRoad(road) && rules.AllowsNode(v))
                {
                    visited[v] = true;
                    queue.Enqueue(v);
                }
            peak = Math.Max(peak, queue.Count);
        }
        return (count, peak);
    }

    /// <summary>Depth-first search with an explicit stack (no recursion, so a very long chain cannot overflow).</summary>
    public static (int Visited, int PeakFrontier) Dfs(RoadNetwork g, int start, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        var visited = new bool[g.NodeCount];
        var stack = new Stack<int>();
        stack.Push(start);
        int count = 0, peak = 1;
        while (stack.Count > 0)
        {
            int u = stack.Pop();
            if (visited[u]) continue;
            visited[u] = true;
            count++;
            foreach (var (v, road) in g.NeighboursAnyDirection(u))
                if (!visited[v] && rules.AllowsRoad(road) && rules.AllowsNode(v)) stack.Push(v);
            peak = Math.Max(peak, stack.Count);
        }
        return (count, peak);
    }

    /// <summary>
    /// Splits the network into its connected pieces (direction ignored): BFS from every location not yet visited.
    /// skipNode / skipRoad pretend one location or road is closed (used to find critical ones).
    /// </summary>
    public static List<List<int>> Components(RoadNetwork g, RouteConstraints? rules = null, int skipNode = -1, int skipRoad = -1)
    {
        rules ??= RouteConstraints.None();
        var visited = new bool[g.NodeCount];
        var pieces = new List<List<int>>();
        var queue = new Queue<int>();
        for (int s = 0; s < g.NodeCount; s++)
        {
            if (visited[s] || s == skipNode || !rules.AllowsNode(s)) continue;
            var piece = new List<int>();
            visited[s] = true;
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                piece.Add(u);
                foreach (var (v, road) in g.NeighboursAnyDirection(u))
                {
                    if (visited[v] || v == skipNode || road.Id == skipRoad || !rules.AllowsRoad(road) || !rules.AllowsNode(v)) continue;
                    visited[v] = true;
                    queue.Enqueue(v);
                }
            }
            pieces.Add(piece);
        }
        return pieces;
    }

    public static bool IsConnected(RoadNetwork g, RouteConstraints? rules = null) => Components(g, rules).Count <= 1;

    /// <summary>
    /// Can every location DRIVE to every other, obeying one-way streets? Pick any location s:
    /// true only if s reaches everyone AND everyone reaches s (= BFS on the reversed graph). Two BFS, O(V + E).
    /// </summary>
    public static bool IsStronglyConnected(RoadNetwork g, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        var allowed = Enumerable.Range(0, g.NodeCount).Where(rules.AllowsNode).ToList();
        if (allowed.Count == 0) return true;
        return Reach(g, allowed[0], rules, reversed: false) == allowed.Count
            && Reach(g, allowed[0], rules, reversed: true) == allowed.Count;
    }

    private static int Reach(RoadNetwork g, int start, RouteConstraints rules, bool reversed)
    {
        var visited = new bool[g.NodeCount];
        var queue = new Queue<int>();
        visited[start] = true;
        queue.Enqueue(start);
        int count = 0;
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            count++;
            foreach (var e in reversed ? g.InEdges(u) : g.OutEdges(u))
            {
                int v = reversed ? e.From : e.To;
                if (visited[v] || !rules.AllowsRoad(e.Road) || !rules.AllowsNode(v)) continue;
                visited[v] = true;
                queue.Enqueue(v);
            }
        }
        return count;
    }
}

/// <summary>
/// Critical infrastructure: locations and roads whose removal splits the city.
/// Brute force + BFS: count the pieces, then for every location and every road, pretend it is closed and count
/// again. More pieces = critical. One BFS is O(V + E), done V + E times, so O((V + E)^2).
/// </summary>
public static class Bottlenecks
{
    public static (List<(int Node, int CutOff)> Junctions, List<(int RoadId, int CutOff)> Roads) Find(RoadNetwork g, RouteConstraints? rules = null)
    {
        rules ??= RouteConstraints.None();
        int basePieces = Connectivity.Components(g, rules).Count;

        var locations = new List<(int, int)>();
        for (int v = 0; v < g.NodeCount; v++)
        {
            if (!rules.AllowsNode(v)) continue;
            var pieces = Connectivity.Components(g, rules, skipNode: v);
            if (pieces.Count <= basePieces) continue;
            // The pieces touching v used to be one piece: all but the biggest of them are now cut off.
            var neighbours = g.NeighboursAnyDirection(v).Select(n => n.Neighbour).ToHashSet();
            var sizes = pieces.Where(p => p.Any(neighbours.Contains)).Select(p => p.Count).ToList();
            locations.Add((v, sizes.Sum() - sizes.Max()));
        }

        var roads = new List<(int, int)>();
        foreach (var road in g.Roads)
        {
            if (!rules.AllowsRoad(road) || !rules.AllowsNode(road.A) || !rules.AllowsNode(road.B)) continue;
            var pieces = Connectivity.Components(g, rules, skipRoad: road.Id);
            if (pieces.Count <= basePieces) continue;
            // Removing one road splits one piece into two: the smaller side is cut off.
            int sideA = pieces.First(p => p.Contains(road.A)).Count;
            int sideB = pieces.First(p => p.Contains(road.B)).Count;
            roads.Add((road.Id, Math.Min(sideA, sideB)));
        }

        locations.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        roads.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        return (locations, roads);
    }
}

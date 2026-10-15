namespace UrbanRouting;

/// <summary>
/// Automated tests. Two kinds: small hand-made graphs where the answer is known, and cross-checks where a
/// fast method must agree with a slow, obviously correct one (e.g. brute force vs Dijkstra).
/// </summary>
public static class Tests
{
    private static int passed, failed;

    public static int Run(string dataDir)
    {
        passed = failed = 0;
        var city = DataLoader.Load(Path.Combine(dataDir, "kaggle", "manhatten.graphml"));
        DataLoader.LoadLandmarks(city, Path.Combine(dataDir, "landmarks.csv"));
        var g = city.Network;

        Section("Known answers on small graphs");
        // A --10 km motorway-- D, and A -1- B -1- C -1- D on slow streets.
        var t = new RoadNetwork();
        for (int i = 0; i < 5; i++) t.AddNode(0, 0, $"T{i}");        // location 4 is isolated
        var motorway = t.AddRoad(0, 3, 10, 100);                     // 10 km at 100 km/h = 6 min
        t.AddRoad(0, 1, 1, 10);                                      // 3 km at 10 km/h = 18 min in total
        t.AddRoad(1, 2, 1, 10);
        t.AddRoad(2, 3, 1, 10);
        Check(ShortestPath.Dijkstra(t, 0, 3, Metric.Distance).Path.SequenceEqual(new[] { 0, 1, 2, 3 }), "shortest distance takes the 3 short streets");
        Check(ShortestPath.Dijkstra(t, 0, 3, Metric.Time).Path.SequenceEqual(new[] { 0, 3 }), "fastest time takes the motorway");
        Check(Close(ShortestPath.DijkstraArray(t, 0, 3, Metric.Time).Cost, 6) && Close(ShortestPath.BruteForce(t, 0, 3, Metric.Time).Cost, 6),
              "array-scan Dijkstra and brute force give the same 6 minutes");
        var same = ShortestPath.Dijkstra(t, 1, 1, Metric.Time);
        Check(same.Found && same.Cost == 0 && same.Path.Count == 1, "start = destination -> a route of one location, cost 0");
        Check(!ShortestPath.Dijkstra(t, 0, 4, Metric.Time).Found, "isolated destination -> 'no route', no crash");
        Check(Close(motorway.TravelTimeMin, 6), "travel time = distance / speed limit (10 km at 100 km/h = 6 min)");
        var blocked = new RouteConstraints();
        blocked.BlockedRoads.Add(motorway.Id);
        Check(ShortestPath.Dijkstra(t, 0, 3, Metric.Time, blocked).Path.Count == 4, "blocking the motorway re-routes the trip onto the streets");

        var oneWay = new RoadNetwork();
        oneWay.AddNode(0, 0, "X");
        oneWay.AddNode(1, 0, "Y");
        oneWay.AddRoad(0, 1, 1, 50, twoWay: false);
        Check(ShortestPath.Dijkstra(oneWay, 0, 1, Metric.Distance).Found && !ShortestPath.Dijkstra(oneWay, 1, 0, Metric.Distance).Found,
              "one-way street: allowed one way only");
        Check(Connectivity.IsConnected(oneWay) && !Connectivity.IsStronglyConnected(oneWay), "one-way pair: connected but not strongly connected");

        var parallel = new RoadNetwork();
        parallel.AddNode(0, 0, "P");
        parallel.AddNode(1, 0, "Q");
        parallel.AddRoad(0, 1, 5, 50);
        parallel.AddRoad(0, 1, 2, 50);
        Check(Close(ShortestPath.Dijkstra(parallel, 0, 1, Metric.Distance).Cost, 2), "two parallel roads: the shorter one is used");
        Check(Bottlenecks.Find(parallel).Roads.Count == 0, "two parallel roads back each other up: neither is critical");

        var line = new RoadNetwork();                                // 0-1-2-3-4
        for (int i = 0; i < 5; i++) line.AddNode(i, 0, $"L{i}");
        for (int i = 0; i < 4; i++) line.AddRoad(i, i + 1, 1, 50);
        var (lineJunctions, lineRoads) = Bottlenecks.Find(line);
        Check(lineJunctions.Select(j => j.Node).OrderBy(v => v).SequenceEqual(new[] { 1, 2, 3 }) && lineRoads.Count == 4,
              "line of 5 locations: locations 1, 2, 3 and all 4 roads are critical");
        Check(lineJunctions.First(j => j.Node == 2).CutOff == 2, "removing the middle location cuts off 2");

        var square = new RoadNetwork();
        for (int i = 0; i < 4; i++) square.AddNode(0, 0, $"S{i}");
        for (int i = 0; i < 4; i++) square.AddRoad(i, (i + 1) % 4, 1, 50);
        var (sqJunctions, sqRoads) = Bottlenecks.Find(square);
        Check(sqJunctions.Count == 0 && sqRoads.Count == 0, "a square (a cycle) has nothing critical");

        var islands = new RoadNetwork();
        for (int i = 0; i < 4; i++) islands.AddNode(0, 0, $"I{i}");
        islands.AddRoad(0, 1, 1, 50);
        islands.AddRoad(2, 3, 1, 50);
        Check(Connectivity.Components(islands).Count == 2 && !Connectivity.IsConnected(islands), "two islands -> 2 pieces, not connected");

        Section("Our min-heap");
        var rng = new Random(3);
        var heap = new MinHeap<int>();
        var values = Enumerable.Range(0, 5000).Select(_ => rng.NextDouble()).ToList();
        foreach (var v in values) heap.Push(0, v);
        bool validHeap = heap.IsValid();
        var popped = new List<double>();
        while (heap.TryPop(out _, out double p)) popped.Add(p);
        Check(validHeap && popped.SequenceEqual(values.OrderBy(v => v)), "5,000 random numbers come out in sorted order");

        Section("Bad input is rejected");
        var bad = new RoadNetwork();
        bad.AddNode(0, 0, "A");
        bad.AddNode(1, 0, "B");
        Check(Throws<ArgumentException>(() => bad.AddRoad(0, 1, -1, 50)), "negative distance");
        Check(Throws<ArgumentException>(() => bad.AddRoad(0, 1, double.NaN, 50)), "missing (NaN) distance");
        Check(Throws<ArgumentException>(() => bad.AddRoad(0, 1, 1, 0)), "zero speed");
        Check(Throws<ArgumentException>(() => bad.AddRoad(0, 0, 1, 50)), "road from a location to itself");
        Check(Throws<ArgumentException>(() => bad.AddNode(double.NaN, 0, "C")), "location with a missing (NaN) x coordinate");
        Check(Throws<ArgumentOutOfRangeException>(() => ShortestPath.Dijkstra(bad, 0, 9, Metric.Time)), "route to a location that does not exist");
        Check(Throws<ArgumentOutOfRangeException>(() => Reachability.WithinBudget(bad, 0, -1, Metric.Time)), "negative budget");

        Section("Loading and cleaning the GraphML data");
        const string xml = """
            <graphml>
              <key attr.name="y" for="node" id="d3" /><key attr.name="x" for="node" id="d4" />
              <key attr.name="length" for="edge" id="d9" /><key attr.name="highway" for="edge" id="d11" />
              <key attr.name="oneway" for="edge" id="d12" /><key attr.name="maxspeed" for="edge" id="d15" />
              <key attr.name="osmid" for="edge" id="d10" /><key attr.name="bridge" for="edge" id="d18" />
              <graph>
                <node id="A"><data key="d3">40.700</data><data key="d4">-74.000</data></node>
                <node id="B"><data key="d3">40.701</data><data key="d4">-74.000</data></node>
                <node id="C"><data key="d3">40.702</data><data key="d4">-74.000</data></node>
                <node id="D"><data key="d3">40.703</data><data key="d4">-74.000</data></node>
                <edge source="A" target="B"><data key="d9">111.2</data><data key="d10">7</data><data key="d11">primary</data><data key="d12">False</data></edge>
                <edge source="B" target="A"><data key="d9">111.2</data><data key="d10">7</data><data key="d11">primary</data><data key="d12">False</data></edge>
                <edge source="B" target="C"><data key="d9">111.2</data><data key="d11">secondary</data><data key="d12">True</data><data key="d15">120 mph</data></edge>
                <edge source="C" target="D"><data key="d9">111.2</data><data key="d11">[u'residential', u'secondary']</data><data key="d12">True</data></edge>
                <edge source="D" target="D"><data key="d9">5</data><data key="d11">residential</data></edge>
                <edge source="C" target="A"><data key="d9">222</data><data key="d11">elevator</data></edge>
                <edge source="A" target="C"><data key="d9">0</data><data key="d11">motorway</data><data key="d12">True</data><data key="d18">yes</data></edge>
              </graph>
            </graphml>
            """;
        var small = DataLoader.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
        var s = small.Network;
        Check(small.TwoWayMerged == 1 && s.FindEdge(0, 1)!.Road == s.FindEdge(1, 0)!.Road, "the two directions of a two-way street become one road");
        Check(small.SelfLoops == 1 && small.NotDrivable == 1, "self-loop and elevator removed");
        Check(small.SpeedsRejected == 1 && Close(s.FindEdge(1, 2)!.Road.SpeedLimitKmh, 25 * 1.609344), "impossible '120 mph' replaced by 25 mph");
        Check(s.FindEdge(2, 3) != null, "list value '[u'residential', ...]' is read (first entry)");
        Check(s.FindEdge(0, 2)!.Road.DistanceKm > 0 && small.ZeroLengths == 1, "zero distance replaced by the straight-line distance");
        Check(Math.Abs(s.FindEdge(0, 1)!.Road.DistanceKm - 0.1112) < 1e-9 && Math.Abs(s.StraightLineKm(0, 1) - 0.1106) < 0.001, "metres -> km; lat/lon -> km");

        Section("The real Manhattan data");
        Check(g.NodeCount == 4426 && g.RoadCount == 7920, $"{g.NodeCount:N0} locations and {g.RoadCount:N0} roads loaded");
        Check(g.Roads.All(r => r.DistanceKm > 0 && r.SpeedLimitKmh >= 8 && r.SpeedLimitKmh <= 121), "every road has a positive distance and a sensible speed limit");
        Check(g.GetNode(city.Landmarks["Times Square"]).Name == "Times Square" && g.Nodes.Count(n => n.Name.Contains(" & ")) > 3000,
              "every location has a name (landmark, or the streets that meet there)");
        Check(Connectivity.IsConnected(g) && !Connectivity.IsStronglyConnected(g), "connected, but not strongly connected (one-way dead ends)");
        Check(city.Landmarks.Count == 10, "all 10 landmarks snapped to a location");

        var trips = Enumerable.Range(0, 200).Select(_ => (rng.Next(g.NodeCount), rng.Next(g.NodeCount))).ToList();
        Check(trips.All(p => SameCost(ShortestPath.Dijkstra(g, p.Item1, p.Item2, Metric.Time), ShortestPath.DijkstraArray(g, p.Item1, p.Item2, Metric.Time))),
              "heap Dijkstra = array Dijkstra on 200 random trips");

        var rules = new RouteConstraints();
        foreach (var r in g.Roads.Where(r => r.Name == "Broadway")) rules.BlockedRoads.Add(r.Id);
        for (int i = 0; i < 40; i++) rules.BlockedNodes.Add(rng.Next(g.NodeCount));
        var constrained = trips.Take(60).Select(p => ShortestPath.Dijkstra(g, p.Item1, p.Item2, Metric.Time, rules)).ToList();
        Check(constrained.All(r => r.Edges.All(rules.AllowsEdge)) && constrained.Count(r => r.Found) > 0,
              "constrained routes never use a blocked location or Broadway");

        var walk = DataLoader.WalkingVersion(DataLoader.Neighbourhood(g, city.Landmarks["Times Square"], 20));
        bool bruteAgrees = true;
        for (int a = 0; a < walk.NodeCount; a++)
            for (int b = 0; b < walk.NodeCount; b++)
                if (!SameCost(ShortestPath.BruteForce(walk, a, b, Metric.Distance), ShortestPath.Dijkstra(walk, a, b, Metric.Distance))) bruteAgrees = false;
        Check(bruteAgrees, "brute force = Dijkstra on every trip in a 20-location area of Times Square");

        var area = DataLoader.Neighbourhood(g, city.Landmarks["Bellevue Hospital"], 400);
        var reach = Reachability.WithinBudget(area, 0, 2.0, Metric.Time).Select(r => r.Node).ToHashSet();
        var expected = Enumerable.Range(0, area.NodeCount).Where(v => ShortestPath.Dijkstra(area, 0, v, Metric.Time).Cost <= 2.0).ToHashSet();
        Check(reach.SetEquals(expected), "reachability = every location whose shortest time is within the budget");

        var (locations, criticalRoads) = Bottlenecks.Find(g);
        Check(locations.Count == 99 && criticalRoads.Count == 103, "Manhattan: 99 critical locations and 103 critical roads");

        Section("Worst cases");
        var chain = new RoadNetwork();                               // the longest possible route and deepest DFS
        for (int i = 0; i < 200_000; i++) chain.AddNode(i, 0, $"C{i}");
        for (int i = 0; i + 1 < 200_000; i++) chain.AddRoad(i, i + 1, 0.1, 50);
        Check(ShortestPath.Dijkstra(chain, 0, 199_999, Metric.Distance).Path.Count == 200_000, "a route through 200,000 locations");
        Check(Connectivity.Dfs(chain, 0).Visited == 200_000, "DFS on a 200,000-long chain without a stack overflow");
        int island = g.AddNode(-50, -50, "island");
        var none = ShortestPath.Dijkstra(g, 0, island, Metric.Time);
        Check(!none.Found && none.Explored > 4000, "unreachable destination: searches everything, then says 'no route'");

        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static bool SameCost(PathResult a, PathResult b) => a.Found == b.Found && (!a.Found || Close(a.Cost, b.Cost));

    private static bool Close(double a, double b) => Math.Abs(a - b) < 1e-9 * Math.Max(1, Math.Abs(a));

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Section(string name) => Console.WriteLine($"\n{name}");

    private static void Check(bool ok, string name)
    {
        if (ok) passed++; else failed++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    }
}

using UrbanRouting;

// How to run (from the repository root):
//   dotnet run --project src                                -> interactive routing engine (menu) on Manhattan
//   dotnet run --project src -- demo                        -> scripted demo of every feature
//   dotnet run --project src -- test                        -> automated correctness tests
//   dotnet run -c Release --project src -- bench [--quick]  -> performance experiments (CSV files in results/)

string root = FindProjectRoot();
string dataDir = Path.Combine(root, "data");
string command = args.Length > 0 ? args[0].ToLowerInvariant() : "menu";

try
{
    switch (command)
    {
        case "menu": RunMenu(dataDir); return 0;
        case "demo": RunDemo(dataDir); return 0;
        case "test": return Tests.Run(dataDir);
        case "bench": Benchmarks.Run(Path.Combine(root, "results"), dataDir, args.Contains("--quick")); return 0;
        default: Console.Error.WriteLine("Use: menu | demo | test | bench [--quick]"); return 2;
    }
}
catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
{
    Console.Error.WriteLine("Data error: " + ex.Message);
    return 1;
}

// ------------------------------------------------------------------------------------------------ the demo
static void RunDemo(string dataDir)
{
    var city = DataLoader.Load(Path.Combine(dataDir, "kaggle", "manhatten.graphml"));
    DataLoader.LoadLandmarks(city, Path.Combine(dataDir, "landmarks.csv"));
    var g = city.Network;
    int Place(string name) => city.Landmarks[name];
    string Name(int node) => g.GetNode(node).Name;
    int times = Place("Times Square"), union = Place("Union Square"), columbus = Place("Columbus Circle");

    Title($"1. DATASET + NETWORK REPRESENTATION  ({city.Area}, Kaggle / OpenStreetMap)");
    Console.WriteLine($"   Read {city.NodesRead:N0} locations and {city.EdgesRead:N0} road segments from manhatten.graphml");
    Console.WriteLine($"   Cleaning: {city.SelfLoops} self-loops and {city.NotDrivable} non-drivable segments removed; {city.ZeroLengths} zero distances fixed;");
    Console.WriteLine($"   speed limits: {city.SpeedsFromTag:N0} from tags, {city.SpeedsDefaulted:N0} defaulted to 25 mph, {city.SpeedsRejected} impossible values rejected");
    Console.WriteLine($"   {city.TwoWayMerged:N0} two-way streets merged -> {g.RoadCount:N0} roads = {g.EdgeCount:N0} directed edges; {g.Roads.Count(r => !r.TwoWay):N0} roads are one-way");
    Console.WriteLine($"   Density E/(V(V-1)) = {(double)g.EdgeCount / ((double)g.NodeCount * (g.NodeCount - 1)):P3} -> very sparse, so an adjacency list");
    Console.WriteLine($"   Example location: id {times}, x = {g.GetNode(times).X:0.00} km, y = {g.GetNode(times).Y:0.00} km, name \"{Name(times)}\"");

    Title("2. SHORTEST PATH  (Battery Park -> Harlem 125th Street)");
    int from = Place("Battery Park"), to = Place("Harlem 125th Street");
    Console.WriteLine("   Shortest DISTANCE (Dijkstra + heap):\n      " + Route(ShortestPath.Dijkstra(g, from, to, Metric.Distance)));
    Console.WriteLine("   Shortest TRAVEL TIME (Dijkstra + heap):\n      " + Route(ShortestPath.Dijkstra(g, from, to, Metric.Time)));
    Console.WriteLine("   Shortest TRAVEL TIME (Dijkstra + array scan):\n      " + Route(ShortestPath.DijkstraArray(g, from, to, Metric.Time)));
    var walk = DataLoader.WalkingVersion(DataLoader.Neighbourhood(g, times, 35));
    int furthest = Reachability.WithinBudget(walk, 0, double.MaxValue, Metric.Distance)[^1].Node;
    var brute = ShortestPath.BruteForce(walk, 0, furthest, Metric.Distance);
    var greedy = ShortestPath.Dijkstra(walk, 0, furthest, Metric.Distance);
    Console.WriteLine($"   Brute force vs Dijkstra, walking, 35 locations around Times Square: brute force tried {brute.Explored:N0} routes"
                      + $" -> {brute.Cost:0.000} km; Dijkstra settled {greedy.Explored} locations -> {greedy.Cost:0.000} km");

    Title("3. CONSTRAINED ROUTING  (blocked roads and locations)");
    var normal = ShortestPath.Dijkstra(g, columbus, union, Metric.Time);
    Console.WriteLine("   (a) Columbus Circle -> Union Square, nothing blocked:\n      " + Route(normal));
    var parade = new RouteConstraints();
    foreach (var road in g.Roads.Where(r => r.Name == "Broadway")) parade.BlockedRoads.Add(road.Id);
    Console.WriteLine($"   (b) same trip, ALL OF BROADWAY BLOCKED for a parade ({parade.BlockedRoads.Count} roads):\n      "
                      + Route(ShortestPath.Dijkstra(g, columbus, union, Metric.Time, parade)));
    var accident = new RouteConstraints();
    int incident = normal.Path[normal.Path.Count / 2];
    accident.BlockedNodes.Add(incident);
    Console.WriteLine($"   (c) same trip, accident blocks the location {Name(incident)} (on the normal route):\n      "
                      + Route(ShortestPath.Dijkstra(g, columbus, union, Metric.Time, accident)));

    Title("4. REACHABILITY  (from Bellevue Hospital, by travel time and by distance)");
    int bellevue = Place("Bellevue Hospital");
    foreach (double minutes in new[] { 1.0, 2.0, 4.0, 6.0 })
    {
        var reach = Reachability.WithinBudget(g, bellevue, minutes, Metric.Time);
        Console.WriteLine($"   within {minutes} min:  {reach.Count,5:N0} locations (furthest: {Name(reach[^1].Node)}, {reach[^1].Cost:0.0} min)");
    }
    foreach (double km in new[] { 0.5, 1.0, 2.0 })
    {
        var reach = Reachability.WithinBudget(g, bellevue, km, Metric.Distance);
        Console.WriteLine($"   within {km} km:  {reach.Count,5:N0} locations (furthest: {Name(reach[^1].Node)}, {reach[^1].Cost:0.00} km)");
    }
    var byHops = Reachability.WithinHops(g, bellevue, 15);
    var fourMin = Reachability.WithinBudget(g, bellevue, 4.0, Metric.Time).Select(r => r.Node).ToHashSet();
    Console.WriteLine($"   BFS \"within 15 roads\" gives {byHops.Count} locations, but {byHops.Count(v => !fourMin.Contains(v))} of them are more than 4 min away"
                      + " -> counting roads is the wrong measure for a time budget.");

    Title("5. CONNECTIVITY");
    Console.WriteLine($"   All locations connected (direction ignored): {Connectivity.IsConnected(g)}");
    Console.WriteLine($"   Strongly connected (obeying one-way streets): {Connectivity.IsStronglyConnected(g)}");
    var bfs = Connectivity.Bfs(g, times);
    var dfs = Connectivity.Dfs(g, times);
    Console.WriteLine($"   BFS visited {bfs.Visited:N0} (largest queue {bfs.PeakFrontier:N0}); DFS visited {dfs.Visited:N0} (largest stack {dfs.PeakFrontier:N0})");
    var bothBlocked = new RouteConstraints();
    foreach (var road in g.Roads.Where(r => r.Name == "Lenox Terrace Place")) bothBlocked.BlockedRoads.Add(road.Id);
    Console.WriteLine($"   With Lenox Terrace Place blocked: connected = {Connectivity.IsConnected(g, bothBlocked)}, pieces = {Connectivity.Components(g, bothBlocked).Count}");

    Title("6. BOTTLENECKS  (critical locations and roads: remove each one, BFS again)");
    var (locations, roads) = Bottlenecks.Find(g);
    Console.WriteLine($"   {locations.Count} critical locations and {roads.Count} critical roads. Top 5 of each:");
    foreach (var (node, cutOff) in locations.Take(5)) Console.WriteLine($"      location {Name(node),-50} cuts off {cutOff} location(s)");
    foreach (var (roadId, cutOff) in roads.Take(5)) Console.WriteLine($"      road {g.Roads[roadId].Name,-40} cuts off {cutOff} location(s)");
    var closed = new RouteConstraints();
    closed.BlockedRoads.Add(roads[0].RoadId);
    int stranded = Connectivity.Components(g, closed).OrderBy(p => p.Count).First()[0];
    Console.WriteLine($"   What-if: block {g.Roads[roads[0].RoadId].Name} -> Times Square to {Name(stranded)}:\n      "
                      + Route(ShortestPath.Dijkstra(g, times, stranded, Metric.Time, closed)));

    Title("7. EDGE CASES");
    var same = ShortestPath.Dijkstra(g, times, times, Metric.Time);
    Console.WriteLine($"   Start = destination: found={same.Found}, {same.Path.Count} location, cost {same.Cost}");
    try { ShortestPath.Dijkstra(g, 0, 999_999, Metric.Time); }
    catch (ArgumentOutOfRangeException ex) { Console.WriteLine($"   Invalid location 999999 -> rejected: {ex.Message.Split('(')[0].Trim()}"); }
    try { Reachability.WithinBudget(g, 0, -5, Metric.Time); }
    catch (ArgumentOutOfRangeException) { Console.WriteLine("   Negative budget -> rejected"); }
    try { g.AddRoad(0, 1, -2, 50); }
    catch (ArgumentException ex) { Console.WriteLine($"   Negative road distance -> rejected: {ex.Message}"); }
    Console.WriteLine();
}

// ------------------------------------------------------------------------------------------------ the interactive menu
static void RunMenu(string dataDir)
{
    var city = DataLoader.Load(Path.Combine(dataDir, "kaggle", "manhatten.graphml"));
    DataLoader.LoadLandmarks(city, Path.Combine(dataDir, "landmarks.csv"));
    var g = city.Network;
    var places = city.Landmarks.ToList();                             // the numbered list of well-known places
    (List<(int Node, int CutOff)> Locations, List<(int RoadId, int CutOff)> Roads)? bottlenecks = null;

    Title($"MANHATTAN ROUTING ENGINE  ({g.NodeCount:N0} locations, {g.RoadCount:N0} roads, Kaggle / OpenStreetMap)");
    while (true)
    {
        Console.WriteLine("\n  1. List places              5. Reachable within a budget");
        Console.WriteLine("  2. Search a street          6. Is the network connected?");
        Console.WriteLine("  3. Shortest route           7. Critical locations and roads");
        Console.WriteLine("  4. Route with blocked roads 0. Exit");
        string? choice = Ask("\nChoose an option");
        if (choice is null or "0") return;
        try
        {
            switch (choice)
            {
                case "1": ListPlaces(); break;
                case "2": Pick("Street name to search"); break;
                case "3": ShortestRoute(blocked: false); break;
                case "4": ShortestRoute(blocked: true); break;
                case "5": Reachable(); break;
                case "6": Connected(); break;
                case "7": Critical(); break;
                default: Console.WriteLine("   Please type a number from 0 to 7."); break;
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("   Cancelled."); }
    }

    void ListPlaces()
    {
        for (int i = 0; i < places.Count; i++)
            Console.WriteLine($"   {i + 1,2}. {places[i].Key,-24} (location {places[i].Value}, x = {g.GetNode(places[i].Value).X:0.00} km, y = {g.GetNode(places[i].Value).Y:0.00} km)");
        Console.WriteLine("   Any other location can be found by street name (option 2), e.g. \"Broadway\".");
    }

    // A place = a number from the list, or part of a street name (then choose from the matches).
    int Pick(string prompt)
    {
        string text = Ask($"{prompt} (1-{places.Count} from the list, or part of a street name)") ?? throw new OperationCanceledException();
        if (int.TryParse(text, out int n) && n >= 1 && n <= places.Count) return Chosen(places[n - 1].Value);
        var matches = g.Nodes.Where(v => v.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(10).ToList();
        if (text.Length == 0 || matches.Count == 0) { Console.WriteLine("   No location matches that."); throw new OperationCanceledException(); }
        for (int i = 0; i < matches.Count; i++) Console.WriteLine($"   {i + 1,2}. {matches[i].Name} (location {matches[i].Id})");
        if (int.TryParse(Ask("   Which one"), out int m) && m >= 1 && m <= matches.Count) return Chosen(matches[m - 1].Id);
        Console.WriteLine("   Not a number from the list.");
        throw new OperationCanceledException();
    }

    int Chosen(int node)
    {
        Console.WriteLine($"   -> {g.GetNode(node).Name}");
        return node;
    }

    Metric AskMetric() => Ask("Optimise for 1 = distance, 2 = travel time") == "1" ? Metric.Distance : Metric.Time;

    void ShortestRoute(bool blocked)
    {
        int from = Pick("From"), to = Pick("To");
        var metric = AskMetric();
        var rules = new RouteConstraints();
        if (blocked)
        {
            string street = Ask("Street to block, e.g. Broadway (Enter for none)") ?? "";
            foreach (var r in g.Roads.Where(r => street != "" && r.Name.Equals(street, StringComparison.OrdinalIgnoreCase))) rules.BlockedRoads.Add(r.Id);
            if (street != "") Console.WriteLine($"   {rules.BlockedRoads.Count} road(s) named \"{street}\" blocked.");
            if (Ask("Also block a location, e.g. an accident? (y/n)") == "y") rules.BlockedNodes.Add(Pick("Blocked location"));
            Console.WriteLine("\n   Without blocks:\n      " + Route(ShortestPath.Dijkstra(g, from, to, metric)));
        }
        Console.WriteLine($"\n   {(blocked ? "With blocks" : $"Best route by {metric.ToString().ToLower()}")}:\n      " + Route(ShortestPath.Dijkstra(g, from, to, metric, rules)));
    }

    void Reachable()
    {
        int from = Pick("Start");
        var metric = AskMetric();
        string unit = metric == Metric.Time ? "min" : "km";
        if (!double.TryParse(Ask($"Budget in {unit}"), out double budget) || budget < 0) { Console.WriteLine("   The budget must be a number >= 0."); return; }
        var reach = Reachability.WithinBudget(g, from, budget, metric);
        Console.WriteLine($"\n   {reach.Count:N0} locations reachable within {budget} {unit}. The 5 furthest:");
        foreach (var (node, cost) in reach.TakeLast(5)) Console.WriteLine($"      {g.GetNode(node).Name,-50} {cost:0.00} {unit}");
    }

    void Connected()
    {
        Console.WriteLine($"   All locations connected (ignoring direction): {Connectivity.IsConnected(g)}");
        Console.WriteLine($"   Strongly connected (obeying one-way streets): {Connectivity.IsStronglyConnected(g)}");
        Console.WriteLine($"   Number of separate pieces: {Connectivity.Components(g).Count}");
    }

    void Critical()
    {
        if (bottlenecks == null) Console.WriteLine("   Removing each location and road in turn (about 10 seconds)...");
        bottlenecks ??= Bottlenecks.Find(g);
        var (locations, roads) = bottlenecks.Value;
        Console.WriteLine($"   {locations.Count} critical locations and {roads.Count} critical roads. Top 5 of each:");
        foreach (var (node, cutOff) in locations.Take(5)) Console.WriteLine($"      location {g.GetNode(node).Name,-50} cuts off {cutOff}");
        foreach (var (roadId, cutOff) in roads.Take(5)) Console.WriteLine($"      road     {g.Roads[roadId].Name,-50} cuts off {cutOff}");
    }
}

static string? Ask(string prompt)
{
    Console.Write(prompt + ": ");
    return Console.ReadLine()?.Trim();
}

// A route printed as the streets driven, repeats collapsed: "Broadway -> 7th Avenue -> ...".
static string Route(PathResult r)
{
    if (!r.Found) return $"NO ROUTE (locations explored: {r.Explored:N0})";
    var streets = new List<string>();
    foreach (var e in r.Edges)
    {
        string name = e.Road.Name == "" ? "(unnamed)" : e.Road.Name;
        if (streets.Count == 0 || streets[^1] != name) streets.Add(name);
    }
    string text = string.Join(" -> ", streets.Take(9)) + (streets.Count > 9 ? $" -> ... ({streets.Count} streets)" : "");
    return $"{text}\n      {r.DistanceKm:0.00} km, {r.TimeMin:0.0} min, locations explored: {r.Explored:N0}";
}

static void Title(string text) => Console.WriteLine($"\n{new string('=', 78)}\n {text}\n{new string('=', 78)}");

// Walk up from the current folder until we find the project root (the folder holding src/UrbanRouting.csproj).
static string FindProjectRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "UrbanRouting.csproj"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

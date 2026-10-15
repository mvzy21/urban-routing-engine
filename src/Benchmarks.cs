using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace UrbanRouting;

/// <summary>
/// Empirical evaluation on REAL data: the Kaggle New York street network (54,128 junctions). To test at many sizes,
/// each experiment cuts real neighbourhoods of growing size out of it (BFS outwards from Times Square).
/// Method: fixed random seeds for the trips, one warm-up run (so JIT compilation isn't timed), memory cleaned
/// before timing, then the MEDIAN of several repetitions (ignores one-off spikes from the OS or garbage collector).
/// Results go to results/*.csv and results/charts/*.svg.
/// </summary>
public static class Benchmarks
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string outDir = "";
    private static RoadNetwork newYork = null!;
    private static int centre;

    public static void Run(string resultsDir, string dataDir, bool quick)
    {
        outDir = resultsDir;
        Directory.CreateDirectory(Path.Combine(outDir, "charts"));
#if DEBUG
        Console.WriteLine("WARNING: Debug build - timings are not representative. Use: dotnet run -c Release --project src -- bench\n");
#endif
        var loaded = DataLoader.Load(Path.Combine(dataDir, "kaggle", "newyork.graphml"));
        newYork = loaded.Network;
        var (x, y) = loaded.ToKm(40.7580, -73.9855);                      // Times Square
        centre = DataLoader.NearestNode(newYork, x, y);

        string env = $".NET {Environment.Version}, {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs, " +
                     $"{(quick ? "quick" : "full")} run, {DateTime.Now:yyyy-MM-dd HH:mm}; data: {loaded.Area} " +
                     $"({newYork.NodeCount:N0} junctions, {newYork.RoadCount:N0} roads, {newYork.EdgeCount:N0} directed edges)";
        Console.WriteLine("Environment: " + env);
        File.WriteAllText(Path.Combine(outDir, "environment.txt"), env + Environment.NewLine);

        Representation(quick);
        PriorityQueueChoice(quick);
        BruteForceVersusDijkstra();
        ReachabilityBudget();
        TraversalScaling(quick);
        BottleneckScaling(quick);
        Console.WriteLine($"\nDone. CSV files written to {outDir}");
    }

    /// <summary>A real neighbourhood of n junctions around Times Square (the whole of New York for the largest size).</summary>
    private static RoadNetwork Area(int n) => n >= newYork.NodeCount ? newYork : DataLoader.Neighbourhood(newYork, centre, n);

    private static int[] Sizes(bool quick, params int[] full) =>
        (quick ? full.Where((_, i) => i % 2 == 0 || i == full.Length - 1) : full)
            .Select(n => Math.Min(n, newYork.NodeCount)).Distinct().ToArray();

    // ---------------------------------------------------------------- 1. list vs matrix
    private static void Representation(bool quick)
    {
        Header("1. Data structure for the graph: adjacency list vs adjacency matrix (real streets)");
        var rows = new List<double[]>();
        Console.WriteLine($"{"V",7} {"E",8} {"list MB",9} {"matrix MB",10} {"list scan ms",13} {"matrix scan ms",15} {"list lookup ns",15} {"matrix lookup ns",17}");

        foreach (int n in Sizes(quick, 250, 500, 1000, 2000, 4000))
        {
            long before = Memory();
            var g = Area(n);
            double listMb = (Memory() - before) / 1048576.0;

            before = Memory();
            var m = new AdjacencyMatrix(g);
            double matrixMb = (Memory() - before) / 1048576.0;

            long sink = 0;
            double listScan = TimeMs(() => { for (int u = 0; u < g.NodeCount; u++) foreach (var e in g.OutEdges(u)) sink += e.To; });
            double matrixScan = TimeMs(() => { for (int u = 0; u < g.NodeCount; u++) sink += m.CountNeighbours(u); });

            var rng = new Random(5);
            const int lookups = 200_000;
            var pairs = Enumerable.Range(0, lookups).Select(_ => (rng.Next(g.NodeCount), rng.Next(g.NodeCount))).ToArray();
            double listLookup = TimeMs(() => { foreach (var (u, v) in pairs) if (g.FindEdge(u, v) != null) sink++; }) * 1e6 / lookups;
            double matrixLookup = TimeMs(() => { foreach (var (u, v) in pairs) if (m.HasEdge(u, v)) sink++; }) * 1e6 / lookups;
            GC.KeepAlive(m);
            GC.KeepAlive(sink);

            rows.Add(new[] { n, g.EdgeCount, listMb, matrixMb, listScan, matrixScan, listLookup, matrixLookup });
            Console.WriteLine($"{n,7} {g.EdgeCount,8} {listMb,9:0.00} {matrixMb,10:0.00} {listScan,13:0.000} {matrixScan,15:0.000} {listLookup,15:0.0} {matrixLookup,17:0.0}");
        }

        Csv("representation.csv", "nodes,directed_edges,list_mb,matrix_mb,list_neighbour_scan_ms,matrix_neighbour_scan_ms,list_edge_lookup_ns,matrix_edge_lookup_ns", rows);
    }

    // ---------------------------------------------------------------- 2. heap vs array priority queue
    private static void PriorityQueueChoice(bool quick)
    {
        Header("2a. Dijkstra's priority queue: binary heap vs array scan, real streets (sparse)");
        var rows = new List<double[]>();
        Console.WriteLine($"{"V",7} {"E",8} {"heap ms",10} {"array ms",10} {"junctions settled",18}");
        var warm = Area(2000);                                       // discarded warm-up
        foreach (var (s, t) in RandomPairs(warm.NodeCount, 30, 99)) { ShortestPath.Dijkstra(warm, s, t, Metric.Time); ShortestPath.DijkstraArray(warm, s, t, Metric.Time); }

        foreach (int n in Sizes(quick, 1000, 2000, 5000, 10000, 20000, 54128))
        {
            var g = Area(n);
            var pairs = RandomPairs(g.NodeCount, 20, 13);
            double heap = TimeMs(() => { foreach (var (s, t) in pairs) ShortestPath.Dijkstra(g, s, t, Metric.Time); }, reps: 3) / pairs.Length;
            double settled = pairs.Average(p => ShortestPath.Dijkstra(g, p.Item1, p.Item2, Metric.Time).Explored);
            double array = double.NaN;
            if (g.NodeCount <= 20000)                                // O(V^2): beyond this one query takes seconds
            {
                var few = pairs.Take(5).ToArray();
                array = TimeMs(() => { foreach (var (s, t) in few) ShortestPath.DijkstraArray(g, s, t, Metric.Time); }, reps: 1) / few.Length;
            }
            rows.Add(new[] { g.NodeCount, g.EdgeCount, heap, array, settled });
            Console.WriteLine($"{g.NodeCount,7} {g.EdgeCount,8} {heap,10:0.000} {(double.IsNaN(array) ? "-" : array.ToString("0.000", Inv)),10} {settled,18:0}");
        }
        Csv("dijkstra_heap_vs_array.csv", "nodes,directed_edges,heap_ms,array_ms,junctions_settled", rows);

        Header("2b. The same comparison on a DENSE network (each real junction linked to its nearest 25%)");
        var dense = new List<double[]>();
        Console.WriteLine($"{"V",6} {"E",9} {"heap ms",10} {"array ms",10}");
        foreach (int n in Sizes(quick, 250, 500, 1000, 2000))
        {
            var g = DataLoader.DenseVersion(newYork, centre, n, 0.25);
            var pairs = RandomPairs(n, 10, 11);
            double heap = TimeMs(() => { foreach (var (s, t) in pairs) ShortestPath.Dijkstra(g, s, t, Metric.Distance); }) / pairs.Length;
            double array = TimeMs(() => { foreach (var (s, t) in pairs) ShortestPath.DijkstraArray(g, s, t, Metric.Distance); }) / pairs.Length;
            dense.Add(new[] { n, g.EdgeCount, heap, array });
            Console.WriteLine($"{n,6} {g.EdgeCount,9} {heap,10:0.000} {array,10:0.000}");
        }
        Csv("dijkstra_dense.csv", "nodes,directed_edges,heap_ms,array_ms", dense);
    }

    // ---------------------------------------------------------------- 3. brute force vs Dijkstra
    private static void BruteForceVersusDijkstra()
    {
        Header("3. Brute force (every route) vs greedy Dijkstra, walking, real neighbourhoods of Times Square");
        var rows = new List<double[]>();
        Console.WriteLine($"{"V",5} {"routes tried",13} {"brute ms",10} {"Dijkstra ms",12}");
        var warm = DataLoader.WalkingVersion(Area(20));        // discarded warm-up so the first row does not include JIT compilation
        ShortestPath.BruteForce(warm, 0, warm.NodeCount - 1, Metric.Distance, maxJunctions: 100);
        for (int n = 10; n <= 60; n += 5)
        {
            var g = DataLoader.WalkingVersion(Area(n));
            int target = Reachability.WithinBudget(g, 0, double.MaxValue, Metric.Distance)[^1].Node;   // furthest junction
            var sw = Stopwatch.StartNew();
            var brute = ShortestPath.BruteForce(g, 0, target, Metric.Distance, maxJunctions: 100);
            double bruteMs = sw.Elapsed.TotalMilliseconds;
            double dMs = TimeMs(() => ShortestPath.Dijkstra(g, 0, target, Metric.Distance), reps: 5);
            rows.Add(new double[] { g.NodeCount, brute.Explored, bruteMs, dMs });
            Console.WriteLine($"{g.NodeCount,5} {brute.Explored,13:N0} {bruteMs,10:0.00} {dMs,12:0.000}");
            if (bruteMs > 3000) break;                                  // exponential: stop before it takes minutes
        }
        Csv("brute_force.csv", "nodes,routes_tried,brute_force_ms,dijkstra_ms", rows);
    }

    // ---------------------------------------------------------------- 4. reachability
    private static void ReachabilityBudget()
    {
        Header($"4. Reachability: bounded Dijkstra from Times Square on all of New York ({newYork.NodeCount:N0} junctions)");
        double fullMs = TimeMs(() => Reachability.WithinBudget(newYork, centre, double.MaxValue, Metric.Time), reps: 3);
        var rows = new List<double[]>();
        Console.WriteLine($"{"budget min",11} {"reached",9} {"ms",9}   (unbounded search over all junctions: {fullMs:0.00} ms)");
        foreach (double budget in new[] { 0.5, 1, 2, 4, 8, 16, 32 })
        {
            int reached = Reachability.WithinBudget(newYork, centre, budget, Metric.Time).Count;
            double ms = TimeMs(() => Reachability.WithinBudget(newYork, centre, budget, Metric.Time), reps: 5);
            rows.Add(new[] { budget, reached, ms, fullMs });
            Console.WriteLine($"{budget,11} {reached,9} {ms,9:0.000}");
        }
        Csv("reachability.csv", "budget_min,nodes_reached,bounded_ms,unbounded_ms", rows);
    }

    // ---------------------------------------------------------------- 5. BFS vs DFS
    private static void TraversalScaling(bool quick)
    {
        Header("5. Connectivity check: BFS (queue) vs DFS (stack)");
        var rows = new List<double[]>();
        Console.WriteLine($"{"V",7} {"E",9} {"BFS ms",9} {"DFS ms",9} {"BFS peak queue",15} {"DFS peak stack",15}");
        foreach (int n in Sizes(quick, 2000, 5000, 10000, 20000, 54128))
        {
            var g = Area(n);
            int start = g == newYork ? centre : 0;
            double bfs = TimeMs(() => Connectivity.Bfs(g, start), reps: 5);
            double dfs = TimeMs(() => Connectivity.Dfs(g, start), reps: 5);
            var b = Connectivity.Bfs(g, start);
            var d = Connectivity.Dfs(g, start);
            rows.Add(new[] { g.NodeCount, g.EdgeCount, bfs, dfs, b.PeakFrontier, d.PeakFrontier });
            Console.WriteLine($"{g.NodeCount,7} {g.EdgeCount,9} {bfs,9:0.00} {dfs,9:0.00} {b.PeakFrontier,15} {d.PeakFrontier,15}");
        }
        Csv("traversal.csv", "nodes,directed_edges,bfs_ms,dfs_ms,bfs_peak_frontier,dfs_peak_frontier", rows);
    }

    // ---------------------------------------------------------------- 6. bottleneck detection
    private static void BottleneckScaling(bool quick)
    {
        Header("6. Critical junctions and roads: remove each one, BFS again (O((V + E)^2))");
        var rows = new List<double[]>();
        Console.WriteLine($"{"V",7} {"E",9} {"ms",11} {"critical junctions",19} {"critical roads",15}");
        foreach (int n in Sizes(quick, 250, 500, 1000, 2000, 4000, 8000))
        {
            var g = Area(n);
            var sw = Stopwatch.StartNew();
            var report = Bottlenecks.Find(g);
            double ms = sw.Elapsed.TotalMilliseconds;
            rows.Add(new double[] { g.NodeCount, g.EdgeCount, ms, report.Junctions.Count, report.Roads.Count });
            Console.WriteLine($"{g.NodeCount,7} {g.EdgeCount,9} {ms,11:0.0} {report.Junctions.Count,19} {report.Roads.Count,15}");
        }
        Csv("bottlenecks.csv", "nodes,directed_edges,ms,critical_nodes,critical_roads", rows);
    }

    // ---------------------------------------------------------------- helpers
    private static double TimeMs(Action action, int reps = 5)
    {
        action();                                   // warm-up: JIT-compile before timing
        GC.Collect();                               // start clean so leftover garbage from earlier work is not collected mid-timing
        GC.WaitForPendingFinalizers();
        var times = new double[reps];
        var sw = new Stopwatch();
        for (int i = 0; i < reps; i++)
        {
            sw.Restart();
            action();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        return times[reps / 2];                     // median
    }

    private static long Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static (int, int)[] RandomPairs(int n, int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (rng.Next(n), rng.Next(n))).ToArray();
    }

    private static void Header(string text)
    {
        Console.WriteLine();
        Console.WriteLine(text);
        Console.WriteLine(new string('-', text.Length));
    }

    private static void Csv(string file, string header, List<double[]> rows)
    {
        var sb = new StringBuilder(header).AppendLine();
        foreach (var r in rows)
            sb.AppendLine(string.Join(',', r.Select(v => double.IsNaN(v) ? "" : v.ToString("0.######", Inv))));
        File.WriteAllText(Path.Combine(outDir, file), sb.ToString());
    }

}

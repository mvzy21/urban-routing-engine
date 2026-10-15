using System.Globalization;
using System.Xml;

namespace UrbanRouting;

/// <summary>A loaded city: the network, where its map starts (for lat/lon -> km), landmarks and a cleaning report.</summary>
public sealed class CityData
{
    public RoadNetwork Network { get; } = new();
    public string Area { get; set; } = "";
    public double Lat0 { get; set; }
    public double Lon0 { get; set; }
    public Dictionary<string, int> Landmarks { get; } = new(StringComparer.OrdinalIgnoreCase);

    // What the cleaning did (printed by the demo).
    public int NodesRead { get; set; }
    public int EdgesRead { get; set; }
    public int SelfLoops { get; set; }
    public int NotDrivable { get; set; }
    public int ZeroLengths { get; set; }
    public int SpeedsFromTag { get; set; }
    public int SpeedsDefaulted { get; set; }
    public int SpeedsRejected { get; set; }
    public int TwoWayMerged { get; set; }

    /// <summary>Latitude/longitude -> km east and north of the map's south-west corner (under 1% error across a city).</summary>
    public (double X, double Y) ToKm(double lat, double lon) =>
        ((lon - Lon0) * 111.320 * Math.Cos(Lat0 * Math.PI / 180), (lat - Lat0) * 110.574);
}

/// <summary>
/// Reads the Kaggle "Street Network of New York in GraphML" files (OpenStreetMap data) and cleans them.
/// GraphML is XML: &lt;key&gt; lines say what each code means (d9 = length), then &lt;node&gt; and &lt;edge&gt; elements hold &lt;data&gt; values.
/// </summary>
public static class DataLoader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static CityData Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Data file not found: {path}");
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static CityData Load(Stream stream)
    {
        var city = new CityData();
        var keyNames = new Dictionary<string, string>();                       // "d9" -> "length"
        var rawNodes = new List<(string Id, Dictionary<string, string> Data)>();
        var rawEdges = new List<(string From, string To, Dictionary<string, string> Data)>();

        // 1. Read the XML one tag at a time.
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true }))
        {
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.LocalName)
                {
                    case "key":
                        keyNames[reader.GetAttribute("id") ?? ""] = reader.GetAttribute("attr.name") ?? "";
                        reader.Read();
                        break;
                    case "node":
                        rawNodes.Add((reader.GetAttribute("id") ?? "", ReadData(reader, keyNames)));
                        reader.Read();
                        break;
                    case "edge":
                        rawEdges.Add((reader.GetAttribute("source") ?? "", reader.GetAttribute("target") ?? "", ReadData(reader, keyNames)));
                        reader.Read();
                        break;
                    case "data":                                                // the area's name
                        string key = reader.GetAttribute("key") ?? "";
                        string value = reader.ReadElementContentAsString();
                        if (keyNames.GetValueOrDefault(key) == "name") city.Area = value;
                        break;
                    default:
                        reader.Read();
                        break;
                }
            }
        }
        city.NodesRead = rawNodes.Count;
        city.EdgesRead = rawEdges.Count;
        if (rawNodes.Count == 0) throw new InvalidDataException("The GraphML file contains no locations.");

        // 2. Junctions: lat/lon -> km, and OSM ids -> 0, 1, 2 ... (so arrays can be indexed by location).
        var lats = rawNodes.Select(n => Number(n.Data, "y")).ToList();
        var lons = rawNodes.Select(n => Number(n.Data, "x")).ToList();
        if (lats.Any(double.IsNaN) || lons.Any(double.IsNaN)) throw new InvalidDataException("A location is missing its coordinates.");
        city.Lat0 = lats.Min();
        city.Lon0 = lons.Min();
        var g = city.Network;
        var idOf = new Dictionary<string, int>();
        for (int i = 0; i < rawNodes.Count; i++)
        {
            var (x, y) = city.ToKm(lats[i], lons[i]);
            idOf[rawNodes[i].Id] = g.AddNode(x, y, $"junction {rawNodes[i].Id}");
        }

        // 3. Road segments: clean each one. A two-way street is stored as TWO opposite segments;
        //    hold the first until its partner arrives, then add ONE two-way road.
        var waiting = new Dictionary<(int, int, string, long), (int From, int To, CleanRoad Road)>();
        foreach (var (fromId, toId, data) in rawEdges)
        {
            if (!idOf.TryGetValue(fromId, out int from) || !idOf.TryGetValue(toId, out int to))
                throw new InvalidDataException($"Road {fromId}->{toId} refers to a missing location.");
            if (from == to) { city.SelfLoops++; continue; }
            var road = Clean(city, from, to, data);
            if (road == null) { city.NotDrivable++; continue; }

            if (road.OneWay) { g.AddRoad(from, to, road.DistanceKm, road.SpeedKmh, twoWay: false, road.Name); continue; }

            var pairKey = (Math.Min(from, to), Math.Max(from, to), road.OsmId, (long)Math.Round(road.DistanceKm * 10000));
            if (waiting.TryGetValue(pairKey, out var partner) && partner.From == to && partner.To == from)
            {
                waiting.Remove(pairKey);
                g.AddRoad(partner.From, partner.To, road.DistanceKm, road.SpeedKmh, twoWay: true, road.Name);
                city.TwoWayMerged++;
            }
            else if (!waiting.TryAdd(pairKey, (from, to, road)))
            {
                g.AddRoad(from, to, road.DistanceKm, road.SpeedKmh, twoWay: false, road.Name);
            }
        }
        // A "two-way" segment whose partner is missing from the file can only be driven the way it is given.
        foreach (var (from, to, road) in waiting.Values)
            g.AddRoad(from, to, road.DistanceKm, road.SpeedKmh, twoWay: false, road.Name);

        // 4. Names: OpenStreetMap locations have none, so name each one after the streets that meet there.
        foreach (var node in g.Nodes)
        {
            var streets = g.NeighboursAnyDirection(node.Id).Select(n => n.Road.Name).Where(s => s != "").Distinct().Take(2).ToList();
            if (streets.Count > 0) node.Name = string.Join(" & ", streets);
        }
        return city;
    }

    private sealed record CleanRoad(double DistanceKm, double SpeedKmh, bool OneWay, string Name, string OsmId);

    /// <summary>The cleaning rules for one road segment. Returns null for segments cars cannot use.</summary>
    private static CleanRoad? Clean(CityData city, int from, int to, Dictionary<string, string> d)
    {
        // Only roads cars can drive on (the data also contains e.g. "elevator" segments).
        string highway = First(d.GetValueOrDefault("highway", ""));
        string[] drivable = { "motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link",
                              "tertiary", "tertiary_link", "residential", "unclassified", "living_street", "road", "service" };
        if (!drivable.Contains(highway)) return null;

        // Distance: metres -> km. A zero or missing length becomes the straight-line distance.
        double distanceKm = Number(d, "length") / 1000.0;
        if (!(distanceKm > 0))
        {
            distanceKm = Math.Max(city.Network.StraightLineKm(from, to), 0.001);
            city.ZeroLengths++;
        }

        // Speed: "25 mph" -> km/h. Missing -> New York's 25 mph default. Impossible values -> default.
        double speed = ParseSpeedKmh(d.GetValueOrDefault("maxspeed", ""));
        if (speed >= 8 && speed <= 121) city.SpeedsFromTag++;
        else
        {
            if (double.IsNaN(speed)) city.SpeedsDefaulted++;
            else city.SpeedsRejected++;                                      // e.g. "120 mph" in Manhattan
            speed = DefaultSpeedKmh(highway);
        }

        bool oneWay = First(d.GetValueOrDefault("oneway", "False")) is "True" or "yes" or "1" or "true";
        return new CleanRoad(distanceKm, speed, oneWay, First(d.GetValueOrDefault("name", "")), First(d.GetValueOrDefault("osmid", "")));
    }

    /// <summary>"25 mph" -> 40.2 km/h; "60 km/h" -> 60; missing -> NaN. US data with no unit means mph.</summary>
    public static double ParseSpeedKmh(string raw)
    {
        string s = First(raw).Trim().ToLowerInvariant();
        string digits = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!double.TryParse(digits, NumberStyles.Float, Inv, out double value)) return double.NaN;
        return s.Contains("km") ? value : value * 1.609344;
    }

    /// <summary>Assumed limits when a road has no speed tag (New York City's default is 25 mph).</summary>
    public static double DefaultSpeedKmh(string highway) => 1.609344 * highway switch
    {
        "motorway" => 50,
        "trunk" => 40,
        "motorway_link" or "trunk_link" or "primary_link" or "secondary_link" or "tertiary_link" => 30,
        "living_street" => 10,
        _ => 25
    };

    /// <summary>Some values are Python lists, e.g. "[u'residential', u'secondary']": keep the first.</summary>
    public static string First(string raw)
    {
        string s = raw.Trim();
        if (!s.StartsWith('[')) return s;
        string first = s.Trim('[', ']').Split(',')[0].Trim();
        if (first.StartsWith('u')) first = first[1..];
        return first.Trim('\'', '"', ' ');
    }

    private static double Number(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && double.TryParse(First(v), NumberStyles.Float, Inv, out double x) ? x : double.NaN;

    /// <summary>Reads the &lt;data key="..."&gt;value&lt;/data&gt; children of a node or edge into a dictionary of name -> value.</summary>
    private static Dictionary<string, string> ReadData(XmlReader reader, Dictionary<string, string> keyNames)
    {
        var result = new Dictionary<string, string>();
        if (reader.IsEmptyElement) return result;
        using var sub = reader.ReadSubtree();
        sub.Read();
        sub.Read();
        while (!sub.EOF)
        {
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "data")
            {
                string key = sub.GetAttribute("key") ?? "";
                string value = sub.ReadElementContentAsString();
                if (keyNames.TryGetValue(key, out var name)) result[name] = value;
            }
            else sub.Read();
        }
        return result;
    }

    // ---------------------------------------------------------------- helpers for the demo and the experiments

    /// <summary>Snaps each place in landmarks.csv (name,lat,lon) to its nearest location.</summary>
    public static void LoadLandmarks(CityData city, string csvPath)
    {
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var c = line.Split(',');
            var (x, y) = city.ToKm(double.Parse(c[1], Inv), double.Parse(c[2], Inv));
            int node = NearestNode(city.Network, x, y);
            city.Landmarks[c[0]] = node;
            city.Network.GetNode(node).Name = c[0];                           // e.g. "Times Square"
        }
    }

    /// <summary>The location closest to a point: a linear search, O(V).</summary>
    public static int NearestNode(RoadNetwork g, double x, double y)
    {
        int best = -1;
        double bestDist = double.PositiveInfinity;
        foreach (var n in g.Nodes)
        {
            double d = (n.X - x) * (n.X - x) + (n.Y - y) * (n.Y - y);
            if (d < bestDist) { bestDist = d; best = n.Id; }
        }
        return best;
    }

    /// <summary>A real neighbourhood of 'size' locations: BFS outwards from 'centre', keep them and the roads between them.</summary>
    public static RoadNetwork Neighbourhood(RoadNetwork g, int centre, int size)
    {
        var order = new List<int> { centre };
        var newId = new Dictionary<int, int>();
        var sub = new RoadNetwork();
        newId[centre] = sub.AddNode(g.GetNode(centre).X, g.GetNode(centre).Y, g.GetNode(centre).Name);
        for (int i = 0; i < order.Count && order.Count < size; i++)
            foreach (var (v, _) in g.NeighboursAnyDirection(order[i]))
                if (order.Count < size && !newId.ContainsKey(v))
                {
                    order.Add(v);
                    newId[v] = sub.AddNode(g.GetNode(v).X, g.GetNode(v).Y, g.GetNode(v).Name);
                }
        foreach (var r in g.Roads)
            if (newId.TryGetValue(r.A, out int a) && newId.TryGetValue(r.B, out int b))
                sub.AddRoad(a, b, r.DistanceKm, r.SpeedLimitKmh, r.TwoWay, r.Name);
        return sub;
    }

    /// <summary>The same streets for someone walking: no one-way rules, 5 km/h. Used to show brute force exploding.</summary>
    public static RoadNetwork WalkingVersion(RoadNetwork g)
    {
        var walk = new RoadNetwork();
        foreach (var n in g.Nodes) walk.AddNode(n.X, n.Y, n.Name);
        foreach (var r in g.Roads) walk.AddRoad(r.A, r.B, r.DistanceKm, 5, twoWay: true, r.Name);
        return walk;
    }

    /// <summary>A DENSE network on real location positions: each location linked to its nearest 'fraction' of the others.</summary>
    public static RoadNetwork DenseVersion(RoadNetwork g, int centre, int size, double fraction)
    {
        var area = Neighbourhood(g, centre, size);
        var dense = new RoadNetwork();
        foreach (var n in area.Nodes) dense.AddNode(n.X, n.Y, n.Name);
        int links = Math.Max(1, (int)(dense.NodeCount * fraction));
        var done = new HashSet<(int, int)>();
        for (int u = 0; u < dense.NodeCount; u++)
            foreach (int v in Enumerable.Range(0, dense.NodeCount).Where(v => v != u).OrderBy(v => dense.StraightLineKm(u, v)).Take(links))
                if (done.Add((Math.Min(u, v), Math.Max(u, v))))
                    dense.AddRoad(u, v, dense.StraightLineKm(u, v) * 1.2 + 0.001, 40);
        return dense;
    }
}

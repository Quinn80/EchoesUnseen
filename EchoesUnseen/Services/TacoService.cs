using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace EchoesUnseen.Services;

/// <summary>
/// Imports GW2 marker packs (TacO / BlishHUD ".taco" archives or plain ".xml") and
/// exposes their POI coordinates so the audio sonar can guide a blind player to
/// achievement / collection / path points the GW2 API doesn't know about.
///
/// POIs carry MapID + xpos/ypos/zpos in GW2 world-space metres — the SAME space as
/// MumbleLink's fAvatarPosition (AvatarX/Y/Z) — so navigation is a direct 3-D
/// distance, no coordinate transform. Markers are cached to app data so a pack is
/// imported once. The app never ships packs; the user imports their own.
/// </summary>
public sealed class TacoService
{
    public record Marker(int MapId, double X, double Y, double Z, string Category, string Type = "");

    // Words that mark a POI as something you physically interact with — a chest,
    // a gathering node, a collectible — as opposed to a path point or waypoint.
    // Matched against the raw type path AND the friendly category, so it still
    // works on packs imported before the type was stored.
    private static readonly string[] InteractiveWords =
    {
        "chest", "treasure", "cache", "lootable", "loot", "bag",
        "node", "ore", "mining", "mithril", "orichalcum", "platinum",
        "wood", "log", "sapling", "plant", "herb", "harvest", "gather",
        "collect", "collectible", "quartz", "bloodstone",
    };

    /// <summary>True when this marker is a chest / node / collectible you'd walk up to
    /// and interact with — the things the proximity chime announces.</summary>
    public static bool IsInteractive(Marker m)
    {
        var hay = ((m.Type ?? "") + " " + (m.Category ?? "")).ToLowerInvariant();
        foreach (var w in InteractiveWords) if (hay.Contains(w)) return true;
        return false;
    }

    /// <summary>A painted TacO path (.trl): one or more line segments of 3-D world
    /// points, in the same world-space frame as the POI markers and MumbleLink.
    ///
    /// <para><b>Category</b> is the pack's own readable menu path (e.g. "Tekkit's Guides
    /// › [-CORE GAME-] › Jumping Puzzles") and <b>Name</b> the leaf label. These come
    /// from the pack's MarkerCategory tree — the same organisation TacO shows — which
    /// is what lets the Guides list be grouped and readable instead of a wall of
    /// filenames.</para></summary>
    public sealed record Trail(int MapId, List<List<double[]>> Segments, string Category, string Name = "");

    private List<Marker> _markers = new();
    private List<Trail> _trails = new();
    private string StorePath => Path.Combine(App.Settings.AppDataDirectory, "markers.json");
    private string TrailStorePath => Path.Combine(App.Settings.AppDataDirectory, "trails.json");

    public IReadOnlyList<Marker> All => _markers;

    public void Load()
    {
        try { if (File.Exists(StorePath)) _markers = JsonSerializer.Deserialize<List<Marker>>(File.ReadAllText(StorePath)) ?? new(); }
        catch (Exception ex) { CrashLogger.Log("TacoService.Load", ex); }
        try { if (File.Exists(TrailStorePath)) _trails = JsonSerializer.Deserialize<List<Trail>>(File.ReadAllText(TrailStorePath)) ?? new(); }
        catch (Exception ex) { CrashLogger.Log("TacoService.LoadTrails", ex); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_markers));
            File.WriteAllText(TrailStorePath, JsonSerializer.Serialize(_trails));
        }
        catch (Exception ex) { CrashLogger.Log("TacoService.Save", ex); }
    }

    /// <summary>Markers on the given map, sorted into their categories.</summary>
    public List<Marker> ForMap(int mapId) => _markers.Where(m => m.MapId == mapId).ToList();

    /// <summary>Painted trails on the given map.</summary>
    public List<Trail> TrailsForMap(int mapId) => _trails.Where(t => t.MapId == mapId).ToList();

    /// <summary>Distinct category names present on a map (for the picker).</summary>
    public List<string> CategoriesForMap(int mapId) =>
        _markers.Where(m => m.MapId == mapId).Select(m => m.Category)
                .Distinct().OrderBy(c => c).ToList();

    /// <summary>Import a .taco (zip), .xml, or .trl pack. Returns how many NEW markers
    /// were added (painted trails are imported alongside; see <see cref="LastTrailsAdded"/>).</summary>
    public int Import(string path)
    {
        var found = new List<Marker>();
        var foundTrails = new List<Trail>();
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".taco" or ".zip")
            {
                using var zip = ZipFile.OpenRead(path);

                // Index every entry by a normalised path so "Data\Foo\bar.trl" from the
                // XML resolves regardless of slash direction or casing.
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) entries[NormPath(e.FullName)] = e;

                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    string xml;
                    using (var s = e.Open())
                    using (var r = new StreamReader(s)) xml = r.ReadToEnd();
                    ParseXml(xml, found);                                  // POI markers
                    ParseTrailRefs(xml, entries, claimed, foundTrails);    // painted trails, named
                }

                // Any .trl the XML didn't reference still gets imported, named after its
                // file, so nothing is silently lost.
                foreach (var e in zip.Entries.Where(x => x.FullName.EndsWith(".trl", StringComparison.OrdinalIgnoreCase)))
                {
                    if (claimed.Contains(NormPath(e.FullName))) continue;
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var nm = Pretty(Path.GetFileNameWithoutExtension(e.Name));
                    var t = ParseTrl(ms.ToArray(), nm, nm);
                    if (t != null) foundTrails.Add(t);
                }
            }
            else if (ext == ".trl")
            {
                var nm = Pretty(Path.GetFileNameWithoutExtension(path));
                var t = ParseTrl(File.ReadAllBytes(path), nm, nm);
                if (t != null) foundTrails.Add(t);
            }
            else ParseXml(File.ReadAllText(path), found);
        }
        catch (Exception ex) { CrashLogger.Log("TacoService.Import", ex); }

        var seen = _markers.Select(Key).ToHashSet();
        int added = 0;
        foreach (var m in found)
            if (seen.Add(Key(m))) { _markers.Add(m); added++; }

        var seenT = _trails.Select(TrailKey).ToHashSet();
        LastTrailsAdded = 0;
        foreach (var t in foundTrails)
            if (seenT.Add(TrailKey(t))) { _trails.Add(t); LastTrailsAdded++; }

        Save();
        return added;
    }

    /// <summary>Painted-trail count added by the most recent <see cref="Import"/>.</summary>
    public int LastTrailsAdded { get; private set; }

    public void Clear() { _markers = new(); _trails = new(); Save(); }

    private static string TrailKey(Trail t)
    {
        var p = t.Segments.FirstOrDefault()?.FirstOrDefault();
        return $"{t.MapId}|{t.Category}|{t.Segments.Count}|{(p != null ? $"{p[0]:F0},{p[2]:F0}" : "")}";
    }

    /// <summary>Parse a TacO .trl binary: int32 version, int32 mapId, then float32
    /// (x,y,z) triplets in world inches. An all-zero triplet breaks the path into a
    /// new segment (TacO's convention for lifting the pen).</summary>
    private static Trail? ParseTrl(byte[] data, string category, string name)
    {
        try
        {
            if (data.Length < 8 + 12) return null;
            int mapId = BitConverter.ToInt32(data, 4);
            var segs = new List<List<double[]>>();
            var cur = new List<double[]>();
            for (int off = 8; off + 12 <= data.Length; off += 12)
            {
                float x = BitConverter.ToSingle(data, off);
                float y = BitConverter.ToSingle(data, off + 4);
                float z = BitConverter.ToSingle(data, off + 8);
                if (x == 0f && y == 0f && z == 0f)
                {
                    if (cur.Count > 1) segs.Add(cur);
                    cur = new List<double[]>();
                    continue;
                }
                cur.Add(new double[] { x, y, z });
            }
            if (cur.Count > 1) segs.Add(cur);
            return segs.Count == 0 ? null : new Trail(mapId, segs, category, name);
        }
        catch (Exception ex) { CrashLogger.Log("TacoService.ParseTrl", ex); return null; }
    }

    private static string Key(Marker m) => $"{m.MapId}|{m.X:F0}|{m.Z:F0}|{m.Category}";

    /// <summary>Normalise a pack-internal path so "Data\Foo\bar.trl" and "data/foo/BAR.TRL"
    /// resolve to the same zip entry.</summary>
    private static string NormPath(string p) => p.Replace('\\', '/').TrimStart('/');

    /// <summary>Walk a pack's MarkerCategory tree, mapping each dotted "type" path to
    /// its leaf DisplayName and its full readable menu path. This tree is the pack
    /// author's own organisation — reusing it is what makes the Guides list tidy.</summary>
    private static Dictionary<string, (string Leaf, string Path)> BuildCategoryMap(XDocument doc)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        void Walk(XElement el, string typePrefix, string dispPrefix)
        {
            foreach (var mc in el.Elements().Where(e => e.Name.LocalName.Equals("MarkerCategory", StringComparison.OrdinalIgnoreCase)))
            {
                var name = (string?)mc.Attribute("name") ?? "";
                var full = string.IsNullOrEmpty(typePrefix) ? name : typePrefix + "." + name;
                var dn = (string?)mc.Attribute("DisplayName") ?? name;
                var dpath = string.IsNullOrEmpty(dispPrefix) ? dn : dispPrefix + " › " + dn;
                if (!string.IsNullOrEmpty(full)) map[full] = (dn, dpath);
                Walk(mc, full, dpath);
            }
        }
        foreach (var root in doc.Elements()) Walk(root, "", "");
        return map;
    }

    /// <summary>Find every &lt;Trail type=… trailData=…/&gt;, load the .trl it points at,
    /// and label it with the pack's own readable category path and name.</summary>
    private static void ParseTrailRefs(string xml, Dictionary<string, ZipArchiveEntry> entries,
                                       HashSet<string> claimed, List<Trail> outList)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var cats = BuildCategoryMap(doc);

            foreach (var tr in doc.Descendants().Where(e => e.Name.LocalName.Equals("Trail", StringComparison.OrdinalIgnoreCase)))
            {
                var data = (string?)tr.Attribute("trailData");
                if (string.IsNullOrWhiteSpace(data)) continue;
                var key = NormPath(data);
                if (!entries.TryGetValue(key, out var entry)) continue;

                var type = (string?)tr.Attribute("type") ?? "";
                string catPath = cats.TryGetValue(type, out var c) ? c.Path : Pretty(type);
                string leaf = cats.TryGetValue(type, out var c2) ? c2.Leaf : Pretty(type);

                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var t = ParseTrl(ms.ToArray(), catPath, leaf);
                if (t != null) { outList.Add(t); claimed.Add(key); }
            }
        }
        catch (Exception ex) { CrashLogger.Log("TacoService.ParseTrailRefs", ex); }
    }

    private static void ParseXml(string xml, List<Marker> outList)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var cats = BuildCategoryMap(doc);
            var display = cats.ToDictionary(kv => kv.Key, kv => kv.Value.Leaf, StringComparer.OrdinalIgnoreCase);

            foreach (var poi in doc.Descendants().Where(e => e.Name.LocalName.Equals("POI", StringComparison.OrdinalIgnoreCase)))
            {
                if (!int.TryParse((string?)poi.Attribute("MapID"), out var mapId)) continue;
                double x = Num(poi, "xpos"), y = Num(poi, "ypos"), z = Num(poi, "zpos");
                if (x == 0 && y == 0 && z == 0) continue;
                var type = (string?)poi.Attribute("type") ?? "";
                var cat = display.TryGetValue(type, out var d) ? d : Pretty(type);
                outList.Add(new Marker(mapId, x, y, z, cat, type));
            }
        }
        catch (Exception ex) { CrashLogger.Log("TacoService.ParseXml", ex); }
    }

    private static double Num(XElement e, string attr) =>
        double.TryParse((string?)e.Attribute(attr), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Turn a "tw_guides.season4.node" type path into a readable label.</summary>
    private static string Pretty(string type)
    {
        var last = type.Split('.').LastOrDefault(s => s.Length > 0) ?? type;
        last = last.Replace('_', ' ').Trim();
        return last.Length == 0 ? "Markers" : char.ToUpperInvariant(last[0]) + last[1..];
    }
}

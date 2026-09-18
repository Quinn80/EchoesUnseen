using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services;

/// <summary>
/// Fetches a searchable catalog of community GW2 songs from the public GW2 Wiki
/// song lists (accessible plain wikitext — no laggy site). Each song is a
/// "===Title===" header followed by number-notation lines that our importer
/// already understands (chords 1/3/6, octaves [6 7 8], runs (3 2 1), pauses ~).
///
/// Songs are fetched live and added to the USER's personal library on demand —
/// the app never bundles or rehosts them, so it's the user browsing a public wiki,
/// not us redistributing arrangements.
/// </summary>
public sealed class SongCatalogService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Public GW2 Wiki community song lists (raw wikitext).
    private static readonly (string url, string label)[] Sources =
    {
        ("https://wiki.guildwars2.com/index.php?title=User:Vella/Musical_Harp_Songs&action=raw", "GW2 Wiki · Vella"),
    };

    public record CatalogSong(string Name, string Notation, string Source)
    {
        public override string ToString() => Name;   // screen-reader name in a list
    }

    private List<CatalogSong>? _cache;

    /// <summary>Fetch (once, then cached) and return the whole catalog.</summary>
    public async Task<List<CatalogSong>> GetAsync(CancellationToken ct = default)
    {
        if (_cache is { Count: > 0 }) return _cache;
        var all = new List<CatalogSong>();
        foreach (var (url, label) in Sources)
        {
            try
            {
                var text = await Http.GetStringAsync(url, ct);
                all.AddRange(Parse(text, label));
            }
            catch (Exception ex) { DiagLog.Log("CATALOG", $"fetch failed {url}: {ex.Message}"); }
        }
        _cache = all;
        DiagLog.Log("CATALOG", $"loaded {all.Count} songs");
        return all;
    }

    private static readonly Regex Header = new(@"^=+\s*(.+?)\s*=+\s*$");

    private static IEnumerable<CatalogSong> Parse(string wikitext, string source)
    {
        var results = new List<CatalogSong>();
        string? name = null;
        var notation = new StringBuilder();

        void Flush()
        {
            if (name != null && notation.Length > 0)
            {
                var note = notation.ToString().Trim();
                if (note.Count(char.IsDigit) >= 4)          // needs real content
                    results.Add(new CatalogSong(name, note, source));
            }
            notation.Clear();
        }

        foreach (var raw in wikitext.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = Header.Match(line);
            if (m.Success)
            {
                Flush();
                name = CleanTitle(m.Groups[1].Value);
                continue;
            }
            if (name == null || line.Length == 0) continue;
            line = Regex.Replace(line, @"<[^>]+>", " ").Trim();   // drop <br /> and other tags
            if (LooksLikeNotation(line)) notation.Append(line).Append(' ');
        }
        Flush();
        return results;
    }

    /// <summary>Strip wiki/HTML markup from a section title.</summary>
    private static string CleanTitle(string t)
    {
        t = Regex.Replace(t, @"<[^>]+>", "");           // <i>...</i>
        t = Regex.Replace(t, @"'''?|\[\[|\]\]", "");    // bold/italic/link markup
        return t.Trim();
    }

    /// <summary>A line of playable notation: has digits and is almost entirely made
    /// of note/notation characters (not prose).</summary>
    private static bool LooksLikeNotation(string line)
    {
        if (!line.Any(char.IsDigit)) return false;
        int ok = line.Count(c => char.IsDigit(c) || " ()[]/~-.,".IndexOf(c) >= 0);
        return ok >= line.Length * 0.85;
    }
}

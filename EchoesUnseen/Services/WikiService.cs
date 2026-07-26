using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace EchoesUnseen.Services;

/// <summary>
/// Guild Wars 2 Wiki MediaWiki API client.
/// Base: https://wiki.guildwars2.com/api.php
///
/// Used exclusively by the Voice Assistant panel (which is TEXT-INPUT ONLY,
/// no voice input).
///
/// CRITICAL BUG FIX FROM PREVIOUS BUILD:
///   MediaWiki's list=search sorts results by fulltext relevance score, not by
///   title match. Using results[0] directly returned wrong-subject articles:
///   searching "Charr" might return a random zone article that mentions charr,
///   not the Charr race page.
///
///   <see cref="PickBestResult"/> scores each candidate by title match quality
///   (exact match = +1000, starts-with = +500, all-query-words-in-title = +200)
///   with tiebreakers for shorter titles and original rank. This produces the
///   intuitively correct result the user expects.
///
///   srlimit was also bumped from 5 to 10 to give us more candidates to score.
/// </summary>
public class WikiService
{
    private const string ApiBase = "https://wiki.guildwars2.com/api.php";
    private readonly HttpClient _http;

    public WikiService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "EchoesUnseen/0.1 (Accessibility overlay for GW2)");
    }

    /// <summary>
    /// Full search-and-fetch pipeline. Returns the chosen article's title and
    /// plaintext extract, or null if no results matched.
    /// </summary>
    public async Task<WikiArticle?> SearchAndFetchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var results = await SearchAsync(query, ct);
        if (results == null || results.Count == 0) return null;

        var best = PickBestResult(query, results);
        if (best == null) return null;

        var body = await FetchExtractAsync(best.Title, ct);
        return new WikiArticle
        {
            Title = best.Title,
            Snippet = best.Snippet ?? "",
            Extract = body ?? "(article has no plaintext extract available)",
            Url = $"https://wiki.guildwars2.com/wiki/{Uri.EscapeDataString(best.Title.Replace(' ', '_'))}",
        };
    }

    /// <summary>Run the MediaWiki search. Returns up to 10 candidate pages.</summary>
    public async Task<List<WikiSearchResult>?> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{ApiBase}?action=query&list=search" +
                  $"&srsearch={Uri.EscapeDataString(query)}" +
                  $"&srlimit=10&srnamespace=0&format=json&origin=*";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonSerializer.Deserialize<WikiSearchResponse>(json, JsonOpts);
            return doc?.Query?.Search;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WikiService.SearchAsync", ex);
            return null;
        }
    }

    /// <summary>Fetch the plaintext extract for a given article title.</summary>
    public async Task<string?> FetchExtractAsync(string title, CancellationToken ct = default)
    {
        var url = $"{ApiBase}?action=query&prop=extracts&explaintext=true" +
                  $"&titles={Uri.EscapeDataString(title)}" +
                  $"&format=json&origin=*";
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            // Parse the first (and only) page in the query.pages object
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("extract", out var ext))
                    return ext.GetString();
            }
            return null;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WikiService.FetchExtractAsync", ex);
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // THE BUG FIX — PickBestResult replaces naive results[0]
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Score each candidate against the query and return the best title match.
    ///
    /// Scoring (higher = better):
    ///   Exact title match:              +1000
    ///   Title starts with query:        +500
    ///   All query words appear in title: +200
    ///   Per character of title length:   -0.5  (prefer shorter = more specific)
    ///   Per rank position from top:      -2    (original relevance as tiebreaker)
    /// </summary>
    public static WikiSearchResult? PickBestResult(string query, List<WikiSearchResult> results)
    {
        if (results.Count == 0) return null;
        if (results.Count == 1) return results[0];

        var q = query.Trim().ToLowerInvariant();
        var scored = new List<(WikiSearchResult Result, double Score)>(results.Count);

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var title = (r.Title ?? "").ToLowerInvariant();
            double score = 0;

            if (title == q) score += 1000;
            else if (title.StartsWith(q)) score += 500;
            else
            {
                var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var titleWords = title.Split(' ');
                if (qWords.Length > 0 && qWords.All(qw => titleWords.Any(tw => tw.Contains(qw))))
                    score += 200;
            }

            score -= title.Length * 0.5;
            score -= i * 2;

            scored.Add((r, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored[0].Result;
    }

    // ── Response types ───────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private class WikiSearchResponse
    {
        [JsonPropertyName("query")]
        public WikiSearchQuery? Query { get; set; }
    }

    private class WikiSearchQuery
    {
        [JsonPropertyName("search")]
        public List<WikiSearchResult>? Search { get; set; }
    }
}

public class WikiSearchResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("pageid")]
    public long PageId { get; set; }
}

public class WikiArticle
{
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Extract { get; set; } = "";
    public string Url { get; set; } = "";
}

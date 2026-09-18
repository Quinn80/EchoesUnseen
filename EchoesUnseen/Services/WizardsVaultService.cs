using System.Net.Http;
using System.Text.Json;

namespace EchoesUnseen.Services;

/// <summary>
/// Reads the Wizard's Vault from the Guild Wars 2 API.
///
/// WHY THIS EXISTS. Quinn wanted to know what the Vault rewards actually are — "I would
/// if I could read what they were." The hover reader can only OCR whatever is drawn on
/// screen, and the Vault draws item names in a stylised face over an animated background,
/// which is exactly the case OCR is worst at: a correct read of "Tropical Leaf Cape Set"
/// still came back at 42% confidence.
///
/// None of that is necessary. ArenaNet publishes the whole thing: every listing, its cost
/// in Astral Acclaim, and the item behind it. So this asks, and gets the real names —
/// spelled correctly, every time, with no picture of a name in the middle.
/// </summary>
public sealed class WizardsVaultService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Api = "https://api.guildwars2.com/v2/";

    public sealed record Reward(
        int ListingId, int ItemId, int Count, string Type, int Cost,
        string Name, string Rarity, string Description)
    {
        /// <summary>How this reads aloud: what it is, then what it costs.</summary>
        public string Spoken =>
            (Count > 1 ? $"{Count} {Name}" : Name)
            + (string.IsNullOrEmpty(Rarity) ? "" : $", {Rarity}")
            + $", {Cost} Astral Acclaim";

        /// <summary>Which part of the Vault this sits in, named the way the game names
        /// it on screen.
        ///
        /// Knowing what a reward IS only half-answers the question when you cannot see;
        /// you still have to find it. The API's "type" maps exactly onto the Vault's
        /// three tabs, so it can say where to look.</summary>
        public string Section => Type switch
        {
            "Featured" => "Featured rewards",
            "Normal"   => "Astral Rewards",
            "Legacy"   => "Legacy rewards",
            _          => string.IsNullOrWhiteSpace(Type) ? "the Vault" : Type,
        };
    }

    public sealed record Season(string Title, DateTime? Ends, List<Reward> Rewards);

    private Season? _cache;
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>The current season's rewards, with real item names.
    ///
    /// Cached for an hour: a season runs for months, so re-fetching a few hundred item
    /// names every time the panel opens would be rude to ArenaNet's servers and slow for
    /// no benefit.</summary>
    public async Task<Season?> GetSeasonAsync(CancellationToken ct = default)
    {
        if (_cache != null && (DateTime.UtcNow - _cachedAt).TotalHours < 1) return _cache;

        try
        {
            var root = await GetJsonAsync(Api + "wizardsvault", ct);
            if (root is null) return _cache;

            var title = root.Value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            DateTime? ends = root.Value.TryGetProperty("end", out var e)
                             && DateTime.TryParse(e.GetString(), out var dt) ? dt : null;

            var ids = new List<int>();
            if (root.Value.TryGetProperty("listings", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var v in ls.EnumerateArray()) ids.Add(v.GetInt32());
            if (ids.Count == 0) return _cache;

            // Listings, then the items they point at. Both endpoints take batches of ids,
            // so this is a handful of requests rather than one per reward.
            var listings = await GetManyAsync(Api + "wizardsvault/listings?ids=", ids, ct);
            var itemIds = listings
                .Where(l => l.TryGetProperty("item_id", out _))
                .Select(l => l.GetProperty("item_id").GetInt32())
                .Distinct().ToList();
            var items = await GetManyAsync(Api + "items?ids=", itemIds, ct);

            var byId = new Dictionary<int, JsonElement>();
            foreach (var it in items)
                if (it.TryGetProperty("id", out var idp)) byId[idp.GetInt32()] = it;

            var rewards = new List<Reward>();
            foreach (var l in listings)
            {
                int itemId = l.TryGetProperty("item_id", out var ii) ? ii.GetInt32() : 0;
                byId.TryGetValue(itemId, out var item);

                rewards.Add(new Reward(
                    ListingId: l.TryGetProperty("id", out var li) ? li.GetInt32() : 0,
                    ItemId: itemId,
                    Count: l.TryGetProperty("item_count", out var ic) ? ic.GetInt32() : 1,
                    Type: l.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
                    Cost: l.TryGetProperty("cost", out var co) ? co.GetInt32() : 0,
                    Name: item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nm)
                          ? nm.GetString() ?? $"Item {itemId}" : $"Item {itemId}",
                    Rarity: item.ValueKind == JsonValueKind.Object && item.TryGetProperty("rarity", out var ra)
                            ? ra.GetString() ?? "" : "",
                    Description: item.ValueKind == JsonValueKind.Object && item.TryGetProperty("description", out var de)
                            ? StripMarkup(de.GetString() ?? "") : ""));
            }

            _cache = new Season(title, ends, rewards.OrderBy(r => r.Cost).ToList());
            _cachedAt = DateTime.UtcNow;
            DiagLog.Log("VAULT", $"season \"{title}\", {rewards.Count} rewards");
            return _cache;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WizardsVaultService.GetSeasonAsync", ex);
            return _cache;
        }
    }

    /// <summary>Astral Acclaim on hand, or null if there's no API key or the call failed.
    /// Uses the app's own API service so the key is handled in one place.</summary>
    public static async Task<int?> GetBalanceAsync(Gw2ApiService api, CancellationToken ct = default)
    {
        try
        {
            var raw = await api.GetRawAuthAsync("account/wizardsvault", ct);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("astral_acclaim", out var a) ? a.GetInt32() : null;
        }
        catch (Exception ex) { CrashLogger.Log("WizardsVaultService.GetBalanceAsync", ex); return null; }
    }

    private static async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        var s = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    /// <summary>Fetch ids in batches. The API caps a batch at 200.</summary>
    private static async Task<List<JsonElement>> GetManyAsync(string urlPrefix, List<int> ids, CancellationToken ct)
    {
        var outp = new List<JsonElement>();
        for (int i = 0; i < ids.Count; i += 180)
        {
            var batch = ids.Skip(i).Take(180);
            var s = await Http.GetStringAsync(urlPrefix + string.Join(",", batch), ct);
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray()) outp.Add(el.Clone());
        }
        return outp;
    }

    /// <summary>Item descriptions carry colour and formatting tags. Spoken aloud they are
    /// gibberish, so they come out.</summary>
    private static string StripMarkup(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "<[^>]{0,40}>", " ")
              .Replace("  ", " ").Trim();
}

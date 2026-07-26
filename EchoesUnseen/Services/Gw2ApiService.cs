using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoesUnseen.Services;

/// <summary>
/// Guild Wars 2 REST API v2 client (https://api.guildwars2.com/v2/).
///
/// PUBLIC ENDPOINTS (no key required):
///   /maps/{id}, /continents/1/floors/1/regions/{r}/maps/{m},
///   /items/{id}, /itemstats/{id}, /commerce/prices/{id}, /commerce/listings/{id}
///
/// AUTHENTICATED ENDPOINTS (require API key with appropriate scopes):
///   /account, /account/achievements, /account/bank, /account/inventory,
///   /account/materials, /account/wallet, /characters, /characters/{name},
///   /characters/{name}/equipment, /characters/{name}/inventory
///
/// CACHING:
///   Map objectives responses are cached per-map-ID so panning around the
///   same map doesn't re-fetch the same objective list every poll. Cache
///   is invalidated only when the user explicitly refreshes.
///
/// COORDINATES:
///   The REST API returns positions in continent coordinates, which is
///   the SAME system the MumbleLink reader gives us. No conversion needed —
///   Euclidean distance is directly meaningful.
/// </summary>
public class Gw2ApiService
{
    private const string BaseUrl = "https://api.guildwars2.com/v2/";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly Dictionary<int, MapObjectives> _mapObjectivesCache = new();
    private readonly Dictionary<int, Map> _mapCache = new();
    private readonly Dictionary<int, Item> _itemCache = new();

    public Gw2ApiService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    // ─── Public helpers ──────────────────────────────────────────────────────

    /// <summary>GET /maps/{id}. Cached.</summary>
    public async Task<Map?> GetMapAsync(int mapId, CancellationToken ct = default)
    {
        if (_mapCache.TryGetValue(mapId, out var cached)) return cached;
        var map = await GetJsonAsync<Map>($"maps/{mapId}", ct);
        if (map != null) _mapCache[mapId] = map;
        return map;
    }

    /// <summary>
    /// Fetch all objectives (waypoints, POIs, vistas, hero challenges, tasks) for
    /// a given map. Requires us to first fetch the map to learn its region ID.
    /// Cached per map.
    /// </summary>
    public async Task<MapObjectives?> GetMapObjectivesAsync(int mapId, CancellationToken ct = default)
    {
        if (_mapObjectivesCache.TryGetValue(mapId, out var cached)) return cached;

        // Step 1: fetch the map to get its region ID
        var map = await GetMapAsync(mapId, ct);
        if (map == null || map.RegionId <= 0) return null;

        // Step 2: fetch the full map details from continents endpoint
        var details = await GetJsonAsync<MapObjectives>(
            $"continents/1/floors/1/regions/{map.RegionId}/maps/{mapId}", ct);
        if (details != null)
        {
            details.MapId = mapId;
            details.MapName = map.Name ?? "";
            _mapObjectivesCache[mapId] = details;
        }
        return details;
    }

    /// <summary>Force refetch of a map's objectives (user hit Refresh).</summary>
    public void InvalidateMap(int mapId)
    {
        _mapObjectivesCache.Remove(mapId);
        _mapCache.Remove(mapId);
    }

    // ─── Authenticated endpoints ─────────────────────────────────────────────

    public Task<List<string>?> GetCharactersAsync(CancellationToken ct = default)
        => GetJsonAsync<List<string>>("characters", ct, authenticated: true);

    public Task<Character?> GetCharacterAsync(string name, CancellationToken ct = default)
        => GetJsonAsync<Character>($"characters/{Uri.EscapeDataString(name)}", ct, authenticated: true);

    public Task<Account?> GetAccountAsync(CancellationToken ct = default)
        => GetJsonAsync<Account>("account", ct, authenticated: true);

    public Task<List<AccountAchievement>?> GetAccountAchievementsAsync(CancellationToken ct = default)
        => GetJsonAsync<List<AccountAchievement>>("account/achievements", ct, authenticated: true);

    public Task<List<WalletCurrency>?> GetWalletAsync(CancellationToken ct = default)
        => GetJsonAsync<List<WalletCurrency>>("account/wallet", ct, authenticated: true);

    /// <summary>
    /// Account bank. The array is positional: 30 slots per tab, and empty slots
    /// come back as null — so an item's index tells you exactly which tab and
    /// slot it sits in, which is what makes "where is it?" answerable.
    /// </summary>
    public Task<List<BankSlot?>?> GetBankAsync(CancellationToken ct = default)
        => GetJsonAsync<List<BankSlot?>>("account/bank", ct, authenticated: true);

    /// <summary>Material storage. Each entry carries its category id.</summary>
    public Task<List<MaterialSlot>?> GetMaterialsAsync(CancellationToken ct = default)
        => GetJsonAsync<List<MaterialSlot>>("account/materials", ct, authenticated: true);

    /// <summary>Material category names ("Basic Materials", "Fine Materials", …).</summary>
    public Task<List<MaterialCategory>?> GetMaterialCategoriesAsync(CancellationToken ct = default)
        => GetJsonAsync<List<MaterialCategory>>("materials?ids=all", ct);

    /// <summary>
    /// A character's bags. Bags are in the order they appear in-game, and each
    /// bag's slots are positional too, so we can say "bag 2, slot 7".
    /// </summary>
    public Task<CharacterInventory?> GetCharacterInventoryAsync(string name, CancellationToken ct = default)
        => GetJsonAsync<CharacterInventory>(
            $"characters/{Uri.EscapeDataString(name)}/inventory", ct, authenticated: true);

    // ─── Item lookups (public, batchable) ────────────────────────────────────

    public async Task<Item?> GetItemAsync(int id, CancellationToken ct = default)
    {
        if (_itemCache.TryGetValue(id, out var cached)) return cached;
        var item = await GetJsonAsync<Item>($"items/{id}", ct);
        if (item != null) _itemCache[id] = item;
        return item;
    }

    /// <summary>Batch fetch: /items?ids=1,2,3,... (up to 200 per call per API limits).</summary>
    public async Task<List<Item>> GetItemsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var missing = ids.Distinct().Where(id => !_itemCache.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            // Chunk into batches of 200 to respect API limits
            for (int i = 0; i < missing.Count; i += 200)
            {
                var batch = missing.Skip(i).Take(200);
                var idsParam = string.Join(",", batch);
                var items = await GetJsonAsync<List<Item>>($"items?ids={idsParam}", ct);
                if (items != null) foreach (var it in items) _itemCache[it.Id] = it;
            }
        }
        return ids.Select(id => _itemCache.TryGetValue(id, out var v) ? v : null!).Where(v => v != null).ToList();
    }

    // ─── Core GET helper ─────────────────────────────────────────────────────

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct, bool authenticated = false) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (authenticated)
            {
                var key = _settings.Current.Gw2ApiKey;
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("GW2 API key is not set. Enter one in Settings > API Keys.");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                CrashLogger.Log($"Gw2ApiService {res.StatusCode} {path}", new Exception(body));
                return null;
            }
            var json = await res.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CrashLogger.Log($"Gw2ApiService GET {path}", ex);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

// ═════════════════════════════════════════════════════════════════════════════
// RESPONSE MODELS — minimal fields needed by the MVP panels. Add more as needed.
// ═════════════════════════════════════════════════════════════════════════════

public class Map
{
    public int Id { get; set; }
    public string? Name { get; set; }
    [JsonPropertyName("region_id")]
    public int RegionId { get; set; }
    [JsonPropertyName("region_name")]
    public string? RegionName { get; set; }
    [JsonPropertyName("min_level")]
    public int MinLevel { get; set; }
    [JsonPropertyName("max_level")]
    public int MaxLevel { get; set; }
    [JsonPropertyName("continent_id")]
    public int ContinentId { get; set; }
}

public class MapObjectives
{
    public int MapId { get; set; }
    public string MapName { get; set; } = "";

    [JsonPropertyName("points_of_interest")]
    public Dictionary<string, PointOfInterest>? PointsOfInterest { get; set; }

    public Dictionary<string, HeartTask>? Tasks { get; set; }

    [JsonPropertyName("skill_challenges")]
    public List<SkillChallenge>? SkillChallenges { get; set; }
}

public class PointOfInterest
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; } // "waypoint" | "landmark" | "vista"
    public float[]? Coord { get; set; } // continent coords [x, y]

    public float X => Coord is { Length: >= 1 } ? Coord[0] : 0;
    public float Y => Coord is { Length: >= 2 } ? Coord[1] : 0;
}

public class HeartTask
{
    public int Id { get; set; }
    public string? Objective { get; set; }
    public int Level { get; set; }
    public float[]? Coord { get; set; }

    public float X => Coord is { Length: >= 1 } ? Coord[0] : 0;
    public float Y => Coord is { Length: >= 2 } ? Coord[1] : 0;
}

public class SkillChallenge
{
    public string? Id { get; set; }
    public float[]? Coord { get; set; }

    public float X => Coord is { Length: >= 1 } ? Coord[0] : 0;
    public float Y => Coord is { Length: >= 2 } ? Coord[1] : 0;
}

public class Character
{
    public string? Name { get; set; }
    public string? Race { get; set; }
    public string? Gender { get; set; }
    public string? Profession { get; set; }
    public int Level { get; set; }
    public List<Equipment>? Equipment { get; set; }
    [JsonPropertyName("specializations")]
    public Specializations? Specializations { get; set; }
}

public class Equipment
{
    public int Id { get; set; }
    public string? Slot { get; set; }
    public List<int>? Infusions { get; set; }
    public List<int>? Upgrades { get; set; }
    public int? Skin { get; set; }
    public EquipmentStats? Stats { get; set; }
}

public class EquipmentStats
{
    public int Id { get; set; }
    public Dictionary<string, float>? Attributes { get; set; }
}

public class Specializations
{
    public List<Specialization>? Pve { get; set; }
    public List<Specialization>? Pvp { get; set; }
    public List<Specialization>? Wvw { get; set; }
}

public class Specialization
{
    public int? Id { get; set; }
    public List<int>? Traits { get; set; }
}

public class Account
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public int World { get; set; }
    public List<string>? Access { get; set; }
}

public class AccountAchievement
{
    public int Id { get; set; }
    public bool Done { get; set; }
    public int Current { get; set; }
    public int Max { get; set; }
}

public class WalletCurrency
{
    public int Id { get; set; }
    public long Value { get; set; }
}

/// <summary>One occupied bank slot. Null entries in the array mean empty.</summary>
public class BankSlot
{
    public int Id { get; set; }
    public int Count { get; set; }
}

/// <summary>One stack in material storage, tagged with its category.</summary>
public class MaterialSlot
{
    public int Id { get; set; }
    public int Category { get; set; }
    public int Count { get; set; }
}

/// <summary>A material storage category, e.g. "Basic Materials".</summary>
public class MaterialCategory
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

/// <summary>A character's equipped bags.</summary>
public class CharacterInventory
{
    public List<Bag?>? Bags { get; set; }
}

/// <summary>One bag and its positional slots (null = empty slot).</summary>
public class Bag
{
    public int Id { get; set; }
    public int Size { get; set; }
    public List<BankSlot?>? Inventory { get; set; }
}

public class Item
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Rarity { get; set; }
    public string? Icon { get; set; }
    public int Level { get; set; }
    public string? Description { get; set; }
}

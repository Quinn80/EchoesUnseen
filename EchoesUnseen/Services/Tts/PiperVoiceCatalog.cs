namespace EchoesUnseen.Services.Tts;

/// <summary>
/// One downloadable Piper voice from the official rhasspy/piper-voices catalog.
/// </summary>
public sealed record PiperCatalogVoice(
    string Locale,      // e.g. "en_US"
    string Name,        // e.g. "amy"
    string Quality,     // "low" | "medium" | "high"
    string Gender,      // "Female" | "Male"
    string SizeLabel)   // human-readable, e.g. "63 MB"
{
    /// <summary>Piper model id / filename stem, e.g. "en_US-amy-medium".</summary>
    public string Id => $"{Locale}-{Name}-{Quality}";

    /// <summary>Friendly display name, e.g. "Amy — US English Female, Medium".</summary>
    public string Display =>
        $"{PrettyName} — {LocaleLabel} {Gender}, {Capitalize(Quality)}";

    /// <summary>
    /// Turn a model name into something readable: underscores become spaces and
    /// each word is capitalised, so "northern_english_male" reads as "Northern
    /// English Male" rather than "Northern_english_male".
    /// </summary>
    public string PrettyName => Name switch
    {
        "hfc_female" => "HFC Female",
        "hfc_male"   => "HFC Male",
        "ljspeech"   => "LJSpeech",
        _ => string.Join(" ", Name.Split('_').Select(Capitalize)),
    };

    private string LocaleLabel => Locale switch
    {
        "en_US" => "US English",
        "en_GB" => "British English",
        _        => Locale
    };

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // rhasspy/piper-voices layout: <lang>/<locale>/<name>/<quality>/<id>.onnx
    private string RepoPath => $"{Locale[..2]}/{Locale}/{Name}/{Quality}/{Id}";

    public string OnnxUrl =>
        $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{RepoPath}.onnx";
    public string JsonUrl =>
        $"https://huggingface.co/rhasspy/piper-voices/resolve/main/{RepoPath}.onnx.json";
}

/// <summary>
/// A small, curated set of high-quality English Piper voices the user can
/// download and preview from Settings. Deliberately short — the full catalog
/// has hundreds; these are the ones worth offering. Lessac (high) ships bundled.
/// </summary>
public static class PiperVoiceCatalog
{
    // English only, medium and high quality. Every entry below was verified to
    // resolve on the rhasspy/piper-voices repo, so no Download button can 404.
    //
    // Multi-speaker datasets (libritts, libritts_r, vctk, arctic, aru, semaine)
    // are deliberately excluded: the engine doesn't pass a --speaker id, so they
    // would silently fall back to speaker 0 and sound nothing like their name.
    public static readonly IReadOnlyList<PiperCatalogVoice> Voices = new[]
    {
        // ── US English · female ──────────────────────────────────────────────
        new PiperCatalogVoice("en_US", "lessac",     "high",   "Female", "119 MB"),
        new PiperCatalogVoice("en_US", "lessac",     "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_US", "amy",        "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_US", "kristin",    "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_US", "hfc_female", "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_US", "ljspeech",   "high",   "Female", "119 MB"),
        new PiperCatalogVoice("en_US", "ljspeech",   "medium", "Female", "63 MB"),

        // ── US English · male ────────────────────────────────────────────────
        new PiperCatalogVoice("en_US", "ryan",       "high",   "Male",   "119 MB"),
        new PiperCatalogVoice("en_US", "ryan",       "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "joe",        "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "john",       "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "hfc_male",   "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "bryce",      "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "norman",     "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "kusal",      "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_US", "sam",        "medium", "Male",   "63 MB"),

        // ── British English ──────────────────────────────────────────────────
        new PiperCatalogVoice("en_GB", "cori",       "high",   "Female", "119 MB"),
        new PiperCatalogVoice("en_GB", "cori",       "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_GB", "jenny_dioco","medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_GB", "alba",       "medium", "Female", "63 MB"),
        new PiperCatalogVoice("en_GB", "alan",       "medium", "Male",   "63 MB"),
        new PiperCatalogVoice("en_GB", "northern_english_male", "medium", "Male", "63 MB"),
    };

    /// <summary>
    /// The six voices fetched automatically on first run, so a new user has a
    /// real choice without hunting through Settings. Deliberately a spread —
    /// two US female, two US male, one British female, one British male — and
    /// all MEDIUM quality except the default, to keep the first-run download
    /// as small as a useful selection allows.
    ///
    /// en_US-lessac-high is the app default and is fetched by PiperInstaller
    /// before these, so it isn't repeated here.
    /// </summary>
    public static readonly IReadOnlyList<string> StarterPack = new[]
    {
        "en_US-amy-medium",         // US female, warm
        "en_US-hfc_female-medium",  // US female, brighter
        "en_US-ryan-medium",        // US male
        "en_US-joe-medium",         // US male, deeper
        "en_GB-cori-medium",        // British female
        "en_GB-alan-medium",        // British male
    };

    public static PiperCatalogVoice? Find(string id) =>
        Voices.FirstOrDefault(v => string.Equals(v.Id, id, System.StringComparison.OrdinalIgnoreCase));
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Loads, saves, and exposes the global <see cref="AppSettings"/>.
///
/// Persistence strategy:
///   - Plain settings (voices, colors, layout) → JSON at %APPDATA%\EchoesUnseen\settings.json
///   - API keys → same file but wrapped with DPAPI (ProtectedData.Protect) so that
///     even if the file is copied to another machine, the keys cannot be read.
///
/// Thread safety: saves are debounced 500ms so rapid slider adjustments don't
/// hammer the disk. Load is idempotent; if the file is missing or corrupt,
/// defaults are used and a fresh file is written on next save.
/// </summary>
public class SettingsService
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private System.Threading.Timer? _saveDebounce;

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public SettingsService()
    {
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EchoesUnseen");
        _settingsPath = Path.Combine(_settingsDir, "settings.json");
        Directory.CreateDirectory(_settingsDir);
    }

    /// <summary>Full path to the settings file — expose in Settings > About for support.</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>Root AppData directory — voices, songs, crash logs all live here.</summary>
    public string AppDataDirectory => _settingsDir;

    /// <summary>
    /// Reads settings.json if present. On any error, falls back to defaults
    /// and does not throw — the app must always start even with corrupt settings.
    /// </summary>

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) { Current = new AppSettings(); return; }
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded != null)
            {
                // Decrypt API keys AFTER deserialization (they were stored encrypted).
                loaded.Gw2ApiKey = Decrypt(loaded.Gw2ApiKey);
                loaded.ElevenLabsApiKey = Decrypt(loaded.ElevenLabsApiKey);
                Migrate(loaded);
                Current = loaded;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsService.Load", ex);
            Current = new AppSettings();
        }
    }

    /// <summary>
    /// Carry an older settings file forward.
    ///
    /// WHY THIS IS NEEDED AT ALL: a saved settings file always wins over the default in
    /// the code. So changing a default only ever helps a brand-new install - for anyone
    /// who has already run the app, the old value is pinned in their file and the change
    /// silently does nothing. That bit us twice in one day: a shortcut was moved off a
    /// combination that could not register on this machine, and the trail's reach was
    /// shortened after testing, and neither would have taken effect.
    ///
    /// So each entry here moves a value ON ONLY IF it is still the exact old default -
    /// i.e. the user never chose it. Anything deliberately set is left alone.
    /// </summary>
    private static void Migrate(AppSettings s)
    {
        // Ctrl+Alt+B was already claimed by other software and never registered, so it
        // was a shortcut that silently did nothing. F9 is free and needs one finger.
        if (s.Keybinds.RecordBug is "Ctrl+Alt+B") s.Keybinds.RecordBug = "F9";

        // Same story: Ctrl+Shift+O was taken, so "read my objective" silently did nothing.
        if (s.Keybinds.ReadObjective is "Ctrl+Shift+O") s.Keybinds.ReadObjective = "F8";

        // 150 m of route in a walled city put most of the far half of the ribbon across
        // rooftops you cannot walk to from here, which reads as clutter.
        if (Math.Abs(s.TrailDrawAheadM - 150.0) < 0.01) s.TrailDrawAheadM = 80.0;

        // Hover targeting follows the build's default until the player chooses. A file saved
        // by the first candidate build holds "false" that nobody chose.
        if (!s.HoverTargetingChosen) s.HoverTargetingFusion = AppSettings.HoverTargetingDefault;
    }

    /// <summary>
    /// Immediate, synchronous save. Called on app exit and when a debounced save
    /// is overridden by an explicit request.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                // Clone so we can encrypt keys without mutating the live object.
                var snapshot = Clone(Current);
                snapshot.Gw2ApiKey = Encrypt(snapshot.Gw2ApiKey);
                snapshot.ElevenLabsApiKey = Encrypt(snapshot.ElevenLabsApiKey);
                var json = JsonSerializer.Serialize(snapshot, JsonOpts);

                // Write atomically via temp file + move, so a crash during write
                // can't leave a half-written settings.json that fails to parse.
                var tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("SettingsService.Save", ex);
            }
        }
    }

    /// <summary>
    /// Restore all settings to their defaults, then save and notify. Two things
    /// are deliberately PRESERVED so a reset isn't destructive in a way the user
    /// wouldn't expect: the API keys (re-entering them is a hassle) and the
    /// first-run-intro flag (so a reset doesn't replay the whole tutorial).
    /// Downloaded Piper/Whisper files on disk are untouched.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            var keepGw2 = Current.Gw2ApiKey;
            var keepEleven = Current.ElevenLabsApiKey;
            var keepFirstRun = Current.FirstRunIntroSpoken;

            Current = new AppSettings
            {
                Gw2ApiKey = keepGw2,
                ElevenLabsApiKey = keepEleven,
                FirstRunIntroSpoken = keepFirstRun,
            };
        }
        Save();
        NotifyChanged();
    }

    /// <summary>
    /// Signal that settings changed. Debounces saves by 500ms so rapid slider
    /// drags don't cause disk thrashing, and raises the Changed event so any
    /// observer can update their UI/behavior.
    /// </summary>
    public void NotifyChanged()
    {
        Changed?.Invoke(this, Current);
        _saveDebounce?.Dispose();
        _saveDebounce = new System.Threading.Timer(_ => Save(), null, 500, Timeout.Infinite);
    }

    // ── DPAPI encryption for API keys (Windows-user-scoped) ──────────────────
    // These keys are only usable on the machine+user they were encrypted on.
    // If the settings file is copied elsewhere, the keys return empty strings.

    private static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return "enc:" + Convert.ToBase64String(encrypted);
        }
        catch { return ""; }
    }

    private static string Decrypt(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("enc:")) return stored; // backwards compat for unencrypted older files
        try
        {
            var encrypted = Convert.FromBase64String(stored[4..]);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static AppSettings Clone(AppSettings s)
    {
        // JSON round-trip is simplest deep clone for a POCO settings class.
        var json = JsonSerializer.Serialize(s, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Plays a song by generating a real AutoHotkey v2 script (Send/Sleep) and running
/// it with the genuine, bundled AutoHotkey engine. This is deliberately NOT our own
/// SendInput replay — it hands playback to AutoHotkey itself, which is the proven,
/// reliable way GW2 music macros work. We only ever generate note-key presses
/// (digits 1-8 plus the octave keys 9/0), matching ArenaNet's music-macro allowance.
///
/// The bundled AutoHotkey64.exe (GPLv2, unmodified) is extracted to app data on
/// first use. We generate the script from the song's notes — never run an imported
/// script blindly — so there's no arbitrary-code risk.
/// </summary>
public sealed class AhkPlayer : IDisposable
{
    private Process? _proc;
    private static string? _exePath;

    public bool IsAvailable => EnsureExe() != null;

    /// <summary>Extract the bundled AutoHotkey engine and return its path (or null).</summary>
    private static string? EnsureExe()
    {
        if (_exePath != null && File.Exists(_exePath)) return _exePath;
        try
        {
            var dir = Path.Combine(App.Settings.AppDataDirectory, "ahk");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "AutoHotkey64.exe");
            if (!File.Exists(dest))
            {
                var asm = typeof(AhkPlayer).Assembly;
                var res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("AutoHotkey64.exe", StringComparison.OrdinalIgnoreCase));
                if (res == null) return null;
                using var s = asm.GetManifestResourceStream(res);
                if (s == null) return null;
                using var f = File.Create(dest);
                s.CopyTo(f);
            }
            _exePath = dest;
            return dest;
        }
        catch (Exception ex) { CrashLogger.Log("AhkPlayer.EnsureExe", ex); return null; }
    }

    /// <summary>Turn the parsed notes into an AutoHotkey v2 script.</summary>
    public static string BuildScript(List<ParsedNote> notes, float tempo, int leadMs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#Requires AutoHotkey v2.0");
        sb.AppendLine("#SingleInstance Off");
        sb.AppendLine("SendMode 'Input'");
        sb.AppendLine("SetKeyDelay 0, 0");
        sb.AppendLine($"Sleep {Math.Clamp(leadMs, 0, 10000)}");   // time to focus GW2
        foreach (var n in notes)
        {
            int ms = Math.Clamp((int)(n.BeatMs / Math.Max(0.25f, tempo)), 20, 4000);
            if (n.IsRest) { sb.AppendLine($"Sleep {ms}"); continue; }
            char k = n.Key;
            if (k < '0' || k > '9') continue;
            if (k == '9' || k == '0')            // octave shift — a quick tap
            {
                sb.AppendLine($"Send '{k}'");
                sb.AppendLine("Sleep 40");
                continue;
            }
            int hold = Math.Clamp((int)(ms * 0.6), 40, 150);
            int gap = Math.Max(ms - hold, 25);
            sb.AppendLine($"Send '{{{k} down}}'");
            sb.AppendLine($"Sleep {hold}");
            sb.AppendLine($"Send '{{{k} up}}'");
            sb.AppendLine($"Sleep {gap}");
        }
        sb.AppendLine("ExitApp");
        return sb.ToString();
    }

    /// <summary>Start playing. Returns false if AutoHotkey couldn't be launched.</summary>
    public bool Play(List<ParsedNote> notes, float tempo, int leadMs = 2500)
    {
        Stop();
        var exe = EnsureExe();
        if (exe == null) return false;
        try
        {
            var script = BuildScript(notes, tempo, leadMs);
            var scriptPath = Path.Combine(App.Settings.AppDataDirectory, "ahk", "current-song.ahk");
            File.WriteAllText(scriptPath, script);
            _proc = Process.Start(new ProcessStartInfo(exe, $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            DiagLog.Log("AUTOPLAY", $"AutoHotkey started ({notes.Count} notes) pid={_proc?.Id}");
            return _proc != null;
        }
        catch (Exception ex) { CrashLogger.Log("AhkPlayer.Play", ex); return false; }
    }

    /// <summary>True while the AutoHotkey process is still playing.</summary>
    public bool IsPlaying { get { try { return _proc is { HasExited: false }; } catch { return false; } } }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;
        // If we killed it between a key's down and up, release every note key so
        // nothing is left held down in the game.
        KeyPressService.ReleaseNoteKeys();
    }

    public void Dispose() => Stop();
}

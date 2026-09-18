using System.Text;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services;

/// <summary>
/// Converts community song files into the app's KEY notation (digits 1-8 for the
/// notes, 9/0 for octave down/up, z for a rest, ".N" for a held note) so they can
/// join the library and play across the full three-octave instrument:
///   • AutoHotkey (.ahk) scripts — the GitHub/gw2mb ecosystem. Reads the Send/Sleep
///     sequence, INCLUDING {Numpad3 down}/{up} hold pairs and the octave keys 9/0.
///   • MIDI (.mid) files — the melody is extracted and mapped across three octaves
///     using 9/0 shifts (far more faithful than the old single-octave fold).
///
/// COPYRIGHT: the app ships no copyrighted songs; the USER imports their own files
/// into their personal library. ArenaNet permits the music macros themselves.
/// </summary>
public static class SongImportService
{
    // ── AutoHotkey ────────────────────────────────────────────────────────────
    /// <summary>
    /// Parse an AHK script into KEY notation. Handles `Send 1`, `Send, 1`,
    /// `SendInput {Numpad3 down}` / `{Numpad3 up}`, bare `{Numpad9}` octave keys,
    /// and Sleep timings. A note lasts until the next note/octave event.
    /// </summary>
    public static string FromAhk(string ahk, out int bpm, out int noteCount)
    {
        bpm = 100; noteCount = 0;
        if (string.IsNullOrWhiteSpace(ahk)) return "";

        // key press (optionally braced, with optional "Numpad" and up/down), or a Sleep.
        var rx = new Regex(
            @"Send(?:Input|Play|Event|Raw)?\s*,?\s*\{?\s*(?:Numpad)?(?<key>[0-9])\s*(?<ud>up|down)?\s*\}?" +
            @"|(?<sleep>Sleep)\s*,?\s*(?<ms>\d+)",
            RegexOptions.IgnoreCase);

        // Ordered stream: note-press (1-8), octave command (9/0), or sleep.
        var stream = new List<(char kind, int val)>(); // 'n'=note,'o'=octave,'s'=sleep
        foreach (Match m in rx.Matches(ahk))
        {
            if (m.Groups["sleep"].Success)
            {
                if (int.TryParse(m.Groups["ms"].Value, out var ms)) stream.Add(('s', ms));
                continue;
            }
            if (m.Groups["ud"].Value.Equals("up", StringComparison.OrdinalIgnoreCase)) continue; // release
            if (!int.TryParse(m.Groups["key"].Value, out var k)) continue;
            if (k == 9 || k == 0) stream.Add(('o', k));
            else if (k >= 1 && k <= 8) stream.Add(('n', k));
        }

        // Collapse: a note's length = sleeps until the next note/octave event.
        var seq = new List<(char kind, int key, int ms)>();
        int curKey = -1, acc = 0;
        void Flush() { if (curKey != -1) { seq.Add(('n', curKey, Math.Max(acc, 1))); curKey = -1; acc = 0; } }
        foreach (var (kind, val) in stream)
        {
            if (kind == 'n') { Flush(); curKey = val; acc = 0; }
            else if (kind == 'o') { Flush(); seq.Add(('o', val, 0)); }
            else if (curKey != -1) acc += val;
        }
        Flush();
        if (seq.Count(s => s.kind == 'n') == 0) return "";

        // Keep the EXACT millisecond timing (key=ms) instead of rounding to beats,
        // so playback matches the original AutoHotkey script rather than a flattened
        // approximation. bpm is nominal — each note carries its own duration.
        bpm = 100;
        var sb = new StringBuilder();
        foreach (var (kind, key, ms) in seq)
        {
            if (kind == 'o') { sb.Append((char)('0' + key)).Append(' '); continue; }   // octave shift
            sb.Append((char)('0' + key)).Append('=').Append(Math.Clamp(ms, 1, 60000)).Append(' ');
            noteCount++;
        }
        return sb.ToString().Trim();
    }

    // ── MIDI ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// Extract a melody from a MIDI file and render it across three octaves using
    /// 9/0 octave shifts. Picks the highest note when several sound together, snaps
    /// to the C-major scale, and clamps to the instrument's low/mid/high registers.
    /// </summary>
    public static string FromMidi(string path, out int bpm, out int noteCount)
    {
        bpm = 100; noteCount = 0;
        var midi = new NAudio.Midi.MidiFile(path, false);
        int tpq = Math.Max(1, midi.DeltaTicksPerQuarterNote);

        for (int t = 0; t < midi.Tracks; t++)
            foreach (var ev in midi.Events[t])
                if (ev is NAudio.Midi.TempoEvent te) { bpm = Math.Clamp((int)Math.Round(te.Tempo), 20, 400); goto gotTempo; }
        gotTempo:;

        var ons = new List<(long time, int note)>();
        for (int t = 0; t < midi.Tracks; t++)
            foreach (var ev in midi.Events[t])
                if (ev is NAudio.Midi.NoteOnEvent on && on.Velocity > 0)
                    ons.Add((on.AbsoluteTime, on.NoteNumber));
        if (ons.Count == 0) return "";
        ons.Sort((a, b) => a.time.CompareTo(b.time));

        long window = Math.Max(1, tpq / 8);
        var melody = new List<(long time, int note)>();
        int i = 0;
        while (i < ons.Count)
        {
            long t0 = ons[i].time; int hi = ons[i].note; int j = i + 1;
            while (j < ons.Count && ons[j].time - t0 <= window) { if (ons[j].note > hi) hi = ons[j].note; j++; }
            melody.Add((t0, hi));
            i = j;
        }

        // Reference C = the C nearest the median note → that register is "mid" (0).
        var sorted = melody.Select(m => m.note).OrderBy(n => n).ToList();
        int median = sorted[sorted.Count / 2];
        int refC = (int)Math.Round((median - 60) / 12.0) * 12 + 60;   // a C, near middle C

        var sb = new StringBuilder();
        int curOct = 0;
        for (int k = 0; k < melody.Count; k++)
        {
            int note = melody[k].note;
            long dur = (k + 1 < melody.Count ? melody[k + 1].time : melody[k].time + tpq) - melody[k].time;
            int beats = Math.Clamp((int)Math.Round((double)dur / tpq), 1, 16);

            int targetOct = Math.Clamp((int)Math.Round((double)(note - refC) / 12.0), -1, 1);
            while (curOct < targetOct) { sb.Append("0 "); curOct++; }   // 0 = octave up
            while (curOct > targetOct) { sb.Append("9 "); curOct--; }   // 9 = octave down

            char key = PitchToKey(((note % 12) + 12) % 12);
            sb.Append(key);
            if (beats > 1) sb.Append('.').Append(beats);
            sb.Append(' ');
            noteCount++;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Pitch class (0=C..11=B) → C-major scale key 1-7, snapping accidentals.</summary>
    private static char PitchToKey(int pc)
    {
        int deg = pc switch
        {
            0 => 1, 1 => 1, 2 => 2, 3 => 2, 4 => 3, 5 => 4,
            6 => 4, 7 => 5, 8 => 5, 9 => 6, 10 => 6, _ => 7,
        };
        return (char)('0' + deg);
    }
}

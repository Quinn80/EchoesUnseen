namespace EchoesUnseen.Models;

/// <summary>
/// A playable song for the Music Player panel.
///
/// ABC NOTATION:
///   Space-separated note tokens. Each token is a letter A-G (case sensitive
///   in ABC format, but we normalize). Tokens can have a trailing integer
///   duration in beats (e.g. "E2" = E held for 2 beats). Rest tokens are "z".
///   Bar separators "|" are allowed and ignored by the parser.
///
///   Example: "C D E F | E D C z | G F E D"
///
/// GW2 INSTRUMENT KEY MAPPING (all instruments use the same keys):
///   C = 1, D = 2, E = 3, F = 4, G = 5, A = 6, B = 7, high-C = 8
///
/// Our 8-key mapping matches how GW2 instruments work in-game. Octave marks
/// (',' and '\'') in ABC are ignored — we collapse everything to a single
/// octave and map to the 8 keys the instruments provide.
/// </summary>
public class Song
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Instrument { get; set; } = "Harp";
    public int Bpm { get; set; } = 100;
    public string Abc { get; set; } = "";
    public string Source { get; set; } = "bundled";
    public string Uploader { get; set; } = "";
    public int Rating { get; set; }
}

/// <summary>
/// One parsed note, ready to play.
/// <see cref="BeatMs"/> is the milliseconds to hold this note, derived from the
/// song's BPM. For a rest, <see cref="IsRest"/> is true and <see cref="Key"/> is ' '.
/// </summary>
public record ParsedNote(char Key, int BeatMs, bool IsRest);

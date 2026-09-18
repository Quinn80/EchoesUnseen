using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services.Hover;

/// <summary>
/// The text rules the hover reader applies between OCR and speech.
///
/// These lived as private methods inside CursorReader, which meant the replay harness
/// could run the TARGETING code but not the filters that decide what is finally said -
/// so a harness pass could still be a spoken failure. Moved here unchanged, free of WPF,
/// so HoverReplay runs exactly what the app runs from OCR box to spoken string.
/// </summary>
public static class HoverText
{
    /// <summary>
    /// Join lines into something a voice reads smoothly.
    ///
    /// Sticking ". " between every line gave "Double-click to consume.. Double-click to
    /// access your account bank from" - most tooltip lines already end in a full stop,
    /// so the join added a second one and the voice paused twice. Lines that already
    /// end in punctuation are joined with a space; the rest get their full stop.
    /// </summary>
    public static string JoinForSpeech(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0)
                sb.Append(".,:;!?".Contains(sb[^1]) ? " " : ". ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Fixed furniture of the interface, which is never the answer to "what am I
    /// pointing at". The inventory search box sits at the top of the panel and the
    /// bigger grab now reaches it, so "Search...." was being read out in front of real
    /// tooltips.
    /// </summary>
    public static bool IsPanelChrome(string line)
    {
        var t = line.Trim().TrimEnd('.', ':').Trim();
        return t.Length == 0
            || t.Equals("Search", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Inventory", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Coins", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(t, @"^\d+\s*/\s*\d+$");  // "77/116"
    }

    /// <summary>
    /// Say money as money.
    ///
    /// Guild Wars 2 writes a price as three numbers with a gold, silver and copper coin
    /// between them, and OCR has no idea what the coins are - it reads the little discs
    /// as letters. The bottom of Quinn's inventory came back as
    ///
    ///     "1,026el 10,0400 135e 98 e 97 e.-"
    ///
    /// which is 135 gold, 98 silver and 97 copper with debris around it. She asked for
    /// the gold in her inventory to be readable weeks ago and it has been coming out as
    /// that ever since. Three numbers in a row, each followed by a stray letter or two,
    /// is a coin amount and nothing else in the interface looks like it.
    /// </summary>
    public static string SpeakMoney(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;

        // number, junk, number, junk, number - the junk being whatever OCR made of a coin
        var m = Regex.Match(line,
            @"(\d[\d,]*)\s*[^\d\s]{0,3}\s+(\d[\d,]*)\s*[^\d\s]{0,3}\s+(\d[\d,]*)\s*[^\d\s]{0,3}\s*$");
        if (!m.Success) return line;

        static string N(string v) => v.Replace(",", "");
        var gold = N(m.Groups[1].Value);
        var silver = N(m.Groups[2].Value);
        var copper = N(m.Groups[3].Value);

        // Silver and copper never exceed 99; if they do this is three of something else.
        if (!int.TryParse(silver, out var sv) || sv > 99) return line;
        if (!int.TryParse(copper, out var cv) || cv > 99) return line;

        // Whatever sat in front of the coins is the gem and karma counters with their
        // own icons misread into it - "1,026el 10,0400". Keep it only if it contains an
        // actual word; otherwise it is more debris to read out.
        var head = line[..m.Index].Trim();
        var headHasWords = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .Any(t => new string(t.Where(char.IsLetter).ToArray()) is { Length: >= 3 } w
                                         && w.Any(c => "aeiouyAEIOUY".Contains(c)));

        var money = $"{gold} gold, {silver} silver, {copper} copper";
        return headHasWords ? $"{head}. {money}" : money;
    }

    /// <summary>
    /// Is this the thing we just said, give or take OCR?
    ///
    /// Resting on one object produces slightly different letters every second -
    /// "Fortified Gate" then "Fortifled Gate" - and comparing them exactly meant the
    /// same object was announced again and again. Compared on letters alone, lower case,
    /// punctuation and spacing thrown away, they are plainly the same answer.
    /// </summary>
    public static bool SameReading(string text, string previous)
    {
        static string Bare(string s) =>
            new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        var a = Bare(text);
        var b = Bare(previous);
        if (a.Length == 0 || b.Length == 0) return a == b;
        if (a == b) return true;

        // Allow a few characters of drift on a long read.
        var shorter = a.Length < b.Length ? a : b;
        var longer = a.Length < b.Length ? b : a;
        if (longer.Length - shorter.Length > longer.Length * 0.2) return false;

        int same = 0;
        for (int i = 0; i < shorter.Length; i++) if (shorter[i] == longer[i]) same++;
        return same >= longer.Length * 0.85;
    }

    /// <summary>
    /// Tidy text read off the WORLD rather than off a panel.
    ///
    /// The Tesseract path restricts itself to the characters that actually appear in
    /// Guild Wars 2, which is why its output never sprouts accents. Windows OCR has no
    /// such list, and a nameplate seen against moving scenery comes back wearing
    /// marks nobody typed - "-te_rael-Alchemst" for Alchemist, "+011ei Queen" for
    /// Killer Queen. Folding the accents back onto plain letters and dropping the
    /// symbols that are never in a name leaves something a voice can pronounce.
    /// </summary>
    public static string CleanSceneText(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;  // the accent itself
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || "[]()'-.,:!?/".Contains(ch))
                sb.Append(ch);
            else
                sb.Append(' ');                                   // bullets, tildes, stray maths
        }

        var cleaned = Regex.Replace(sb.ToString().Normalize(NormalizationForm.FormC), @"\s{2,}", " ").Trim();

        // A run of punctuation with no letters in it is not a name.
        return cleaned.Any(char.IsLetter) ? cleaned : "";
    }

    /// <summary>
    /// Does this ONE LINE read like language, or like a picture of some rocks?
    ///
    /// The test is whether a line carries more real words than gibberish. Interface text
    /// is pronounceable words, often with figures beside them - "Tailor (450),
    /// Weaponsmith (400)". Scene noise is short consonant clusters and stray capitals -
    /// "rd FEZ IINNOM", "Ee TEA SEN.". Counting words against junk separates those with
    /// no dictionary needed.
    ///
    /// A line of pure quantity passes too, so long as nothing substantial sits beside the
    /// figures: the gold at the bottom of the inventory is "208g 14s 32c" and holds no
    /// words at all, which is why hovering it used to say nothing.
    ///
    /// Checked against nineteen real lines from Quinn's logs: eighteen land correctly.
    /// The miss is "o aid   ae soll", where OCR happened to make two pronounceable words
    /// out of scenery - not separable without a dictionary, and one stray line rather
    /// than a paragraph.
    /// </summary>
    public static bool LooksLikeWords(string line)
    {
        // No confidence gate here any more, and there should never have been this one.
        // It read TesseractOcrService.LastConfidence - a value now set only by the CHAT
        // reader, on a different picture, at some other moment. Hover was silently
        // judging its own text by how well the chat box had been read seconds earlier.
        // RapidOCR reports confidence per line, which is the honest place for it.

        line = line.Trim();
        if (line.Length < 3) return false;

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        int wordy = 0, junk = 0, numeric = 0;
        foreach (var tok in tokens)
        {
            if (IsQuantity(tok)) { numeric++; continue; }

            var w = new string(tok.Where(char.IsLetter).ToArray());
            if (w.Length < 3) continue;                       // punctuation, initials

            // ALL CAPS usually means scene noise - "NOR", "EEE", "TEA SEN" - which is
            // why it counts against a line. A guild tag is the exception and it is
            // always written the same way, in square brackets: [HALO], [DRIP], [SIAM].
            // Treating those as shouting threw away every tag Quinn pointed at, which
            // is most of what a name IS in Guild Wars 2.
            var bracketed = tok.StartsWith('[') && tok.TrimEnd('.', ',', ':').EndsWith(']');

            if (w.Any(c => "aeiouyAEIOUY".Contains(c)) && (!w.All(char.IsUpper) || bracketed)) wordy++;
            else junk++;                                      // no vowel, or shouting
        }

        if (wordy >= 1 && wordy >= junk) return true;

        // Three numbers in a row with coin-shaped debris between them is money, and it
        // contains no words at all - which is why the gold at the bottom of the
        // inventory used to read as silence.
        if (Regex.IsMatch(line, @"\d[\d,]*\s*[^\d\s]{0,3}\s+\d[\d,]*\s*[^\d\s]{0,3}\s+\d[\d,]*"))
            return true;

        // Nothing but figures, with nothing of substance beside them.
        return numeric > 0 && tokens.All(t => IsQuantity(t) || t.Length <= 2);
    }

    /// <summary>A number as the interface writes one: a count, a fraction, a bonus, or
    /// coins.</summary>
    public static bool IsQuantity(string t) =>
        Regex.IsMatch(t, @"^([\d,]+[gsc]?|\d+/\d+|\+\d+)$");

    /// <summary>
    /// Drop OCR debris and cap the length. Lines that are mostly punctuation or
    /// stray glyphs get spelled out letter-by-letter by a screen reader, which
    /// is exactly the jumbled result we're trying to avoid.
    /// </summary>
    public static string Tidy(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var kept = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l =>
            {
                if (l.Length < 3) return false;
                int letters = l.Count(char.IsLetter);
                return letters >= 2 && letters >= l.Length * 0.35;
            })
            .ToList();

        if (kept.Count == 0) return "";

        var text = string.Join(". ", kept);
        return text.Length <= 600 ? text : text[..600] + "…";
    }

    /// <summary>
    /// The whole spoken-line filter the current reader applies to what the selector
    /// kept, in the order CursorReader applies it.
    /// </summary>
    public static List<string> FilterForSpeech(IEnumerable<string> lines) =>
        lines.Select(Tidy)
             .Select(CleanSceneText)
             .Where(l => !string.IsNullOrWhiteSpace(l) && !IsPanelChrome(l) && LooksLikeWords(l))
             .Select(SpeakMoney)
             .ToList();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>A run of text lines set against one left margin: a tooltip body, a paragraph.</summary>
public sealed class LineBlock
{
    public List<TextLine> Lines { get; } = new();
    public Box Bounds => Box.Bounding(Lines.Select(l => l.Box));
    public double Left => Lines.Count == 0 ? 0 : Lines.Skip(Lines.Count > 1 ? 1 : 0).Min(l => l.Box.X);
    public TextLine First => Lines[0];
}

/// <summary>A vertical list of items with left-aligned names at a regular pitch: merchant
/// stock, trading post results, category menus, objectives.</summary>
public sealed class TextList
{
    public List<TextLine> Names { get; } = new();
    public double Pitch { get; set; }
    public double Left { get; set; }
    public double Right { get; set; }
    public double Top => Names[0].Box.CentreY - Pitch / 2;
    public double Bottom => Names[^1].Box.CentreY + Pitch / 2;
}

/// <summary>
/// Text geometry: RapidOCR's lines turned into the shapes the interface is made of.
/// Every threshold is a multiple of the line height, never a pixel count.
/// </summary>
public static class TextLines
{
    private static readonly Regex PriceToken = new(@"^[\s\[\(]*\d[\d,\.]*\s*[^\d\s]{0,3}\s*(\d[\d,\.]*\s*[^\d\s]{0,3}\s*){0,2}[\]\)]*\s*$",
                                                   RegexOptions.Compiled);
    private static readonly Regex Fraction = new(@"^\s*\d[\d,]*\s*/\s*\d[\d,]*\s*$", RegexOptions.Compiled);

    public static int Letters(string s) => s.Count(char.IsLetter);

    /// <summary>A price, a count or a progress figure: digits with at most coin debris.</summary>
    public static bool IsNumeric(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || !t.Any(char.IsDigit)) return false;
        return PriceToken.IsMatch(t) || Fraction.IsMatch(t) || Letters(t) <= 2 && t.Count(char.IsDigit) >= 1;
    }

    public static bool IsFraction(string s) => Fraction.IsMatch(s);

    /// <summary>Prose - a description sentence rather than a name.</summary>
    public static bool IsSentence(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 6 || (words.Length >= 3 && s.TrimEnd().EndsWith('.'));
    }

    /// <summary>
    /// One box per line. RapidOCR often splits a name into pieces ("Pyre" "Boots" "Skin");
    /// pieces on the same baseline, at the same size, a word's gap apart, are one line.
    /// Anything further apart is a separate column - an item and its price stay separate.
    /// </summary>
    public static List<TextLine> MergeSplit(IEnumerable<TextLine> input)
    {
        var rows = new List<List<TextLine>>();
        foreach (var b in input.OrderBy(b => b.Box.CentreY).ThenBy(b => b.Box.X))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var last = r[^1].Box;
                var h = Math.Max(4, Math.Min(last.H, b.Box.H));
                if (last.VerticalOverlap(b.Box) < 0.6) return false;
                // A finished sentence is not continued by a bare figure: that is a price from
                // whatever is behind the panel.
                if (r[^1].Text.TrimEnd().EndsWith('.') && IsNumeric(b.Text) && Letters(b.Text) == 0) return false;
                // Two pieces SplitAtGaps has just cut apart share the box they came from exactly;
                // they were cut for a reason and are not joined back together.
                if (last.Y == b.Box.Y && last.H == b.Box.H) return false;
                // "Defense:" and a green "229" are one line even though the figure's box is smaller.
                var tol = IsNumeric(r[^1].Text) || IsNumeric(b.Text) ? 0.7 : 0.5;
                if (Math.Abs(last.H - b.Box.H) > h * tol) return false;
                var gap = b.Box.X - last.Right;
                return gap >= -h * 0.3 && gap <= h * 0.6;
            });
            if (row == null) rows.Add(new List<TextLine> { b });
            else row.Add(b);
        }
        return rows.Select(r => r.Count == 1 ? r[0]
                : new TextLine(string.Join(" ", r.OrderBy(x => x.Box.X).Select(x => x.Text.Trim())),
                               Box.Bounding(r.Select(x => x.Box)), r.Average(x => x.Confidence),
                               JoinedCharX(r.OrderBy(x => x.Box.X).ToList())))
                   .ToList();
    }

    /// <summary>Character positions of pieces joined with single spaces, when every piece has them.</summary>
    private static double[]? JoinedCharX(List<TextLine> parts)
    {
        var xs = new List<double>();
        for (int k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            if (part.CharX == null || part.CharX.Length != part.Text.Length || part.Text != part.Text.Trim()) return null;
            if (k > 0) xs.Add((xs[^1] + part.CharX[0]) / 2);
            xs.AddRange(part.CharX);
        }
        return xs.ToArray();
    }

    /// <summary>
    /// Split a line where the recogniser's own character positions show a gap no word space is
    /// that wide. The detector sometimes runs two separate things into one box - a card's price
    /// showing through a translucent tooltip and the tooltip's sentence ("600This will unlock
    /// the glider skin"), or a button label and the name beside it - and every rule after this
    /// assumes one box is one thing.
    /// </summary>
    public static IEnumerable<TextLine> SplitAtGaps(TextLine line)
    {
        var xs = line.CharX;
        var text = line.Text;
        if (text.Length < 4) { yield return line; yield break; }
        bool approx = xs == null || xs.Length != text.Length;
        if (approx)
        {
            // No character positions: only the glued-figure rule can be trusted, on even spacing.
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\p{Ll}\d")) { yield return line; yield break; }
            xs = Enumerable.Range(0, text.Length).Select(i => line.Box.X + (i + 0.5) / text.Length * line.Box.W).ToArray();
        }

        var steps = new List<double>();
        for (int i = 1; i < xs.Length; i++)
            if (text[i] != ' ' && text[i - 1] != ' ') steps.Add(xs[i] - xs[i - 1]);
        steps.Sort();
        var pitch = steps.Count > 0 ? steps[steps.Count / 2] : line.Box.H * 0.5;
        var h = line.Box.H;

        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            bool cut = false;
            if (i < text.Length)
            {
                // the previous visible character
                int p = i - 1;
                while (p > start && text[p] == ' ') p--;
                if (text[i] == ' ' || text[p] == ' ') continue;
                var gap = xs[i] - xs[p];
                bool digitThenWord = char.IsDigit(text[p]) && char.IsLetter(text[i]);
                // "wardrobe.150": a sentence ends and a price from behind the panel begins
                bool wordThenDigit = (char.IsLetter(text[p]) || text[p] == '.' || text[p] == '!') && char.IsDigit(text[i]);
                bool gluedFigure = char.IsLower(text[p]) && p == i - 1 && char.IsDigit(text[i])
                                   && System.Text.RegularExpressions.Regex.IsMatch(text[i..], @"^(\d[\d,]*\s+\p{L}|\d{2,}[\d,]*\s*$)");
                cut = gluedFigure
                      || (!approx && gap > Math.Max(h * 1.6, pitch * 3.2))
                      || (!approx && (digitThenWord || wordThenDigit) && gap > Math.Max(h * 0.9, pitch * 2.0))
                      || (text[p] == '.' && char.IsDigit(text[i]) && text[i..].All(c => char.IsDigit(c) || c == ','));
            }
            if (!cut && i < text.Length) continue;

            var piece = text[start..i].Trim();
            if (piece.Length > 0)
            {
                int a = start; while (a < i && text[a] == ' ') a++;
                int b = i - 1; while (b > a && text[b] == ' ') b--;
                if (start == 0 && i == text.Length) { yield return line; yield break; }
                var left = Math.Max(line.Box.X, xs[a] - pitch * 0.6);
                var right = Math.Min(line.Box.Right, xs[b] + pitch * 0.6);
                if (right > left)
                    yield return new TextLine(piece, Box.FromLTRB(left, line.Box.Y, right, line.Box.Bottom), line.Confidence, xs[a..(b + 1)]);
            }
            start = i;
        }
    }

    /// <summary>Typical line height near a point, ignoring headings and slivers.</summary>
    public static double LineHeight(IReadOnlyList<TextLine> lines, double x, double y, double fallback)
    {
        var near = lines.Where(l => l.Box.DistanceTo(x, y) < fallback * 25 && Letters(l.Text) >= 3)
                        .Select(l => l.Box.H).OrderBy(v => v).ToList();
        if (near.Count < 3) near = lines.Where(l => Letters(l.Text) >= 3).Select(l => l.Box.H).OrderBy(v => v).ToList();
        if (near.Count == 0) return fallback;
        var med = near[near.Count / 2];
        return Math.Clamp(med, fallback * 0.5, fallback * 2.2);
    }

    /// <summary>
    /// Blocks of left-aligned lines. A title that is indented by its icon joins the block
    /// below it; a blank line inside a tooltip does not break it.
    /// </summary>
    public static List<LineBlock> Blocks(IReadOnlyList<TextLine> lines, double h)
    {
        // Slivers - a stray "10:" half the height of the text around it - are detector debris;
        // letting one start a block splits a tooltip in two.
        var ordered = lines.Where(l => l.Box.H >= h * 0.6 || Letters(l.Text) >= 4).OrderBy(l => l.Box.Y).ToList();
        var blocks = new List<LineBlock>();
        foreach (var l in ordered)
        {
            LineBlock? home = null;
            double bestGap = double.MaxValue;
            foreach (var b in blocks)
            {
                var last = b.Lines[^1].Box;
                var gap = l.Box.Y - last.Bottom;
                // A tooltip's title is set in larger type a little above its body.
                var titleGap = b.Lines.Count == 1 && b.First.Box.H > l.Box.H * 1.1 ? h * 2.4 : h * 1.9;
                if (gap < -h * 0.6 || gap > titleGap) continue;
                if (Math.Abs(l.Box.H - last.H) > Math.Max(l.Box.H, last.H) * 0.55) continue;
                var aligned = Math.Abs(l.Box.X - b.Left) <= h * 0.7;
                // the first body line under an icon-indented title
                // ("Research Kit" over "Fine": the body line is short and does not reach under the title)
                var underTitle = b.Lines.Count == 1 && l.Box.X < b.First.Box.X - h * 0.8
                                 && l.Box.X > b.First.Box.X - h * 3.2 && gap < h * 1.2;
                // a line indented by its own icon (skill facts, bullet points), close under the last
                var iconIndented = l.Box.X > b.Left + h * 0.7 && l.Box.X < b.Left + h * 3.2 && gap < h * 0.9
                                   && l.Box.HorizontalOverlap(b.Bounds) > 0;
                if (!aligned && !underTitle && !iconIndented) continue;
                // A line on a block's own margin belongs there before it belongs to a block it
                // merely sits indented inside.
                var cost = aligned ? gap : gap + h;
                if (cost < bestGap) { bestGap = cost; home = b; }
            }
            if (home == null) { home = new LineBlock(); blocks.Add(home); }
            home.Lines.Add(l);
        }
        return blocks;
    }

    /// <summary>
    /// Vertical lists: names sharing a left edge at a steady pitch. Figures that sit under a
    /// name ("1/3", "0/500000") belong to that name's row and do not count as rows.
    /// </summary>
    public static List<TextList> Lists(IReadOnlyList<TextLine> lines, double h)
    {
        // Objective titles are long ("Escort 6 Allied Supply Caravans to Their Destinations in
        // World vs. World") but they do not end in a full stop the way description prose does.
        var names = lines.Where(l => Letters(l.Text) >= 3 && !l.Text.TrimEnd().EndsWith('.') && l.Box.W < h * 32)
                         .OrderBy(l => l.Box.X).ToList();
        var lists = new List<TextList>();
        var used = new HashSet<TextLine>();
        foreach (var seed in names)
        {
            if (used.Contains(seed)) continue;
            // Sub-items are indented a little (Skins > Armor Skins): still the same list.
            var column = names.Where(n => Math.Abs(n.Box.X - seed.Box.X) <= h * 1.3)
                              .OrderBy(n => n.Box.CentreY).ToList();
            if (column.Count < 3) continue;

            // The pitch is the step between neighbouring rows. From each neighbouring pair, walk
            // both ways at that step. Rows may be missing - hidden under a tooltip, unread - so a
            // step of two or three rows is still the list, as long as it lands on the grid. The
            // run that explains most names wins; on a tie the finer pitch, because a list read at
            // twice its pitch also "explains" every other row.
            var best = new List<TextLine>();
            double bestPitch = double.MaxValue;
            for (int i = 0; i + 1 < column.Count; i++)
            {
                var pitch = column[i + 1].Box.CentreY - column[i].Box.CentreY;
                if (pitch < h * 1.2 || pitch > h * 6.5) continue;

                bool OnGrid(double d) { var k = Math.Round(d / pitch); return k >= 1 && k <= 3 && Math.Abs(d / pitch - k) <= 0.28; }

                var run = new List<TextLine> { column[i], column[i + 1] };
                var cursor = column[i + 1];
                foreach (var n in column.Skip(i + 2))
                {
                    var d = n.Box.CentreY - cursor.Box.CentreY;
                    if (d < pitch * 0.72) continue;
                    if (!OnGrid(d)) break;
                    run.Add(n); cursor = n;
                }
                cursor = column[i];
                foreach (var n in column.Take(i).Reverse())
                {
                    var d = cursor.Box.CentreY - n.Box.CentreY;
                    if (d < pitch * 0.72) continue;
                    if (!OnGrid(d)) break;
                    run.Insert(0, n); cursor = n;
                }
                if (run.Count > best.Count || (run.Count == best.Count && pitch < bestPitch)) { best = run; bestPitch = pitch; }
            }
            if (best.Count < 3) continue;
            // A list is mostly rows, not mostly gaps: scattered captions that happen to share a
            // left edge line up on some coarse grid too.
            var slots = (int)Math.Round((best[^1].Box.CentreY - best[0].Box.CentreY) / bestPitch) + 1;
            if (best.Count < slots * 0.6) continue;
            var list = new TextList { Pitch = bestPitch, Left = best.Min(n => n.Box.X) };
            list.Names.AddRange(best);
            foreach (var n in best) used.Add(n);
            lists.Add(list);
        }
        return lists;
    }

    /// <summary>Everything drawn on one row of a list, left to right.</summary>
    public static List<TextLine> RowContents(IReadOnlyList<TextLine> lines, TextLine name, double top, double bottom,
                                             double left, double right)
    {
        return lines.Where(l => l.Box.CentreY >= top && l.Box.CentreY <= bottom
                                && l.Box.Right >= left && l.Box.X <= right)
                    .OrderBy(l => l.Box.X).ToList();
    }

    /// <summary>Letters-only similarity, for matching a tooltip's title to a row or a card.</summary>
    public static double Similarity(string? a, string? b)
    {
        static string N(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var x = N(a); var y = N(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        if (x.Contains(y)) return (double)y.Length / x.Length + 0.2;
        if (y.Contains(x)) return (double)x.Length / y.Length + 0.2;
        int[,] d = new int[x.Length + 1, y.Length + 1];
        for (int i = 0; i <= x.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= y.Length; j++) d[0, j] = j;
        for (int i = 1; i <= x.Length; i++)
            for (int j = 1; j <= y.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
        return 1 - (double)d[x.Length, y.Length] / Math.Max(x.Length, y.Length);
    }

    /// <summary>"8 Mithril Ore" -> ("Mithril Ore", 8). Tooltip titles carry the stack count.</summary>
    public static (string Name, int? Count) SplitCount(string title)
    {
        var m = Regex.Match(title.Trim(), @"^(\d{1,4})\s*[-\s]\s*(\D.*)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 1)
            return (m.Groups[2].Value.Trim(), n);
        return (title.Trim(), null);
    }

    /// <summary>Tidy a line for speech: stray bullets and doubled spaces out.</summary>
    public static string Clean(string s)
    {
        var t = Regex.Replace(s ?? "", @"[•●◆·�]+", " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        return t;
    }
}

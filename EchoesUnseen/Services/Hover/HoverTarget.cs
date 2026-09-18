using System;
using System.Collections.Generic;
using System.Linq;

namespace EchoesUnseen.Services.Hover;

/// <summary>One line of text OCR found, and where it was, in the grab's own pixels.</summary>
public readonly record struct TextBox(string Text, double X, double Y, double W, double H, double Confidence)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CentreX => X + W / 2;
    public double CentreY => Y + H / 2;
}

/// <summary>What kind of thing the pointer turned out to be on.</summary>
public enum HoverMode { None, Label, Row, Tooltip }

/// <summary>The decision, with the reasoning kept so a test harness can print it.</summary>
public sealed record HoverPick(
    HoverMode Mode,
    IReadOnlyList<TextBox> Chosen,
    IReadOnlyList<TextBox> Rejected,
    string Reason)
{
    public IEnumerable<string> Lines => Chosen.Select(c => c.Text);
}

/// <summary>
/// WHICH TEXT IS THE POINTER ON?
///
/// This used to be answered by taking every line within a distance of the pointer and
/// reading the lot, which is how a player's name ended up welded to a stranger's title
/// and a match timer:
///
///     "-anTArChet. Fortified 'Y. Drop). Tactivator ,tDra. Vegetable Synt'hesizer"
///
/// Distance alone cannot tell those apart, because in a crowd everything is roughly as
/// near as everything else. Layout can. A tooltip is a COLUMN - lines stacked with
/// aligned edges, even gaps and a common text size. A label is a single line, sometimes
/// with one subtitle tucked directly beneath it. Two different shapes, needing two
/// different rules, and neither of them is "the closest four lines".
///
/// Deliberately free of WPF and of the rest of the app, so the regression harness can
/// run exactly this code against saved captures with no game and no window.
/// </summary>
public static class HoverTarget
{
    // Everything below is a proportion of the anchor's own line height, so none of it
    // depends on the player's interface size or display scaling.
    private const double SubtitleGap = 1.4;    // how far under a label its subtitle may sit
    private const double TooltipGap = 2.2;     // a blank line inside a tooltip is still a tooltip
    private const double AlignTol = 0.6;       // flush-left means flush left
    private const double SizeTol = 0.45;       // lines of a block are about the same height
    private const double CentreTol = 0.5;      // a subtitle is centred under its label

    /// <summary>
    /// Put split lines back together before anything else looks at them.
    ///
    /// RapidOCR's detector does not always return one box per line. It frequently splits
    /// a name into pieces - "Pyre" "Boots" "Skin", "yman's" "Salvage Kit", "Tickets"
    /// "for" "New" - and every rule downstream assumes one box is one line. So a name
    /// arrived in fragments, got re-sorted into the wrong order ("Kit. Salvage"), or had
    /// half of it dropped by the word filter.
    ///
    /// Two boxes are the same line when they sit on the same baseline, stand the same
    /// height, and have only a word's worth of gap between them. That is a far safer test
    /// than it sounds: two different labels on one screen row - an item and its price -
    /// are separated by a column of empty space many times wider than a space character,
    /// which is exactly what GrowRow is for and is left alone here.
    /// </summary>
    private static List<TextBox> MergeSplitLines(IReadOnlyList<TextBox> boxes)
    {
        var rows = new List<List<TextBox>>();

        foreach (var b in boxes.OrderBy(b => b.CentreY).ThenBy(b => b.X))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var last = r[^1];
                var h = Math.Max(6, Math.Min(last.H, b.H));

                var share = Math.Min(last.Bottom, b.Bottom) - Math.Max(last.Y, b.Y);
                if (share < Math.Min(last.H, b.H) * 0.55) return false;    // not the same line
                if (Math.Abs(last.H - b.H) > h * 0.5) return false;        // not the same size

                var gap = b.X - last.Right;
                return gap >= -h * 0.3 && gap <= h * 1.2;                  // a space, not a column
            });

            if (row == null) rows.Add(new List<TextBox> { b });
            else row.Add(b);
        }

        var merged = new List<TextBox>();
        foreach (var r in rows)
        {
            if (r.Count == 1) { merged.Add(r[0]); continue; }

            var ordered = r.OrderBy(x => x.X).ToList();
            var left = ordered.Min(x => x.X);
            var right = ordered.Max(x => x.Right);
            var top = ordered.Min(x => x.Y);
            var bottom = ordered.Max(x => x.Bottom);
            merged.Add(new TextBox(string.Join(" ", ordered.Select(x => x.Text.Trim())),
                                   left, top, right - left, bottom - top,
                                   ordered.Average(x => x.Confidence)));
        }
        return merged;
    }

    public static HoverPick Select(IReadOnlyList<TextBox> input, double px, double py)
    {
        var boxes = input == null ? null : MergeSplitLines(input);

        if (boxes == null || boxes.Count == 0)
            return new HoverPick(HoverMode.None, Array.Empty<TextBox>(), Array.Empty<TextBox>(),
                                 "nothing found");

        var anchor = FindAnchor(boxes, px, py, out var why);
        var others = boxes.Where(b => !Same(b, anchor)).ToList();

        // A tooltip announces itself by shape: something else aligned underneath the
        // anchor, close by, at the same text size.
        var column = GrowColumn(anchor, others);
        if (column.Count > 1)
        {
            var rejected = boxes.Where(b => !column.Any(c => Same(c, b))).ToList();
            return new HoverPick(HoverMode.Tooltip, column, rejected,
                $"{why}; {column.Count} aligned lines beneath it");
        }

        // A MENU ROW READS ACROSS, NOT DOWN.
        //
        // Quinn's point: pointing at an item in a vendor list or the Wizard's Vault
        // should say what it costs too, without having to find the price and hover that
        // separately. The name and the price are one row - same baseline, same text
        // size, a gap of empty column between them - so they are one answer.
        //
        // Safe to do only because the test is BASELINE, not nearness: two labels have to
        // sit on the same line of the screen to be joined. Names floating over different
        // players never do.
        var row = GrowRow(anchor, others);
        if (row.Count > 1)
        {
            var notRow = boxes.Where(b => !row.Any(c => Same(c, b))).ToList();
            return new HoverPick(HoverMode.Row, row, notRow,
                $"{why}; {row.Count - 1} more on the same line");
        }

        // Otherwise a single label - with at most one subtitle, and only when nothing
        // else is competing for it. Two players standing together each have a name and
        // a title, and pairing the wrong ones is worse than reading one name.
        var chosen = new List<TextBox> { anchor };
        var subtitle = FindSubtitle(anchor, others);
        if (subtitle is TextBox sub) chosen.Add(sub);

        var rest = boxes.Where(b => !chosen.Any(c => Same(c, b))).ToList();
        return new HoverPick(HoverMode.Label, chosen, rest,
            subtitle is null ? $"{why}; no attached subtitle"
                             : $"{why}; subtitle attached beneath");
    }

    /// <summary>
    /// The one line the pointer is on. Inside beats beside, and beside beats near;
    /// among equals, the one the game would have drawn for this pointer - down and to
    /// the right, where tooltips go.
    /// </summary>
    private static TextBox FindAnchor(IReadOnlyList<TextBox> boxes, double px, double py, out string why)
    {
        var inside = boxes.Where(b => px >= b.X && px <= b.Right && py >= b.Y && py <= b.Bottom).ToList();
        if (inside.Count > 0)
        {
            var hit = inside.OrderBy(b => Math.Abs(b.CentreY - py)).First();
            why = "pointer inside this line";
            return hit;
        }

        // Distance to the edge of the box, not to its centre - a long line is not
        // further away just because it is long.
        TextBox best = boxes[0];
        double bestScore = double.MaxValue;
        foreach (var b in boxes)
        {
            var dx = px < b.X ? b.X - px : px > b.Right ? px - b.Right : 0;
            var dy = py < b.Y ? b.Y - py : py > b.Bottom ? py - b.Bottom : 0;
            var d = Math.Sqrt(dx * dx + dy * dy);

            // Mild preference for what lies down-and-right of the pointer, because that
            // is where the game puts a tooltip for this cursor.
            if (b.Y >= py && b.Right >= px) d *= 0.85;

            if (d < bestScore) { bestScore = d; best = b; }
        }
        why = "nearest line to the pointer";
        return best;
    }

    /// <summary>
    /// Walk downward from the anchor while the lines still look like the same block.
    ///
    /// Stops at the first line that breaks the pattern rather than skipping it, which is
    /// what keeps a tooltip from swallowing the next entry in the list behind it: the
    /// upgrades panel would otherwise return "Build Pots of Oil / Fortifies the objective
    /// with pots of burning oil. / Reinforced Walls / Build Mortars", three quarters of
    /// which the player did not point at.
    /// </summary>
    private static List<TextBox> GrowColumn(TextBox anchor, List<TextBox> others)
    {
        var column = new List<TextBox> { anchor };

        // ORDER BY CENTRE, NOT BY EDGE.
        //
        // Different engines draw their boxes differently. Windows OCR hugs the glyphs;
        // RapidOCR returns a taller box that OVERLAPS its neighbours - "Guards gain Iron
        // Hide..." runs to y=50 while the "50%." beneath it starts at y=38. Asking for
        // lines strictly BELOW an edge then hides the very line that continues the
        // sentence, and the tooltip reads as "50%." on its own. Centres cannot overlap
        // that way, so the reading order survives whichever engine produced the boxes.
        var below = others.Where(b => b.CentreY > anchor.CentreY).OrderBy(b => b.CentreY).ToList();
        var cursor = anchor;
        foreach (var b in below)
        {
            if (!Continues(cursor, b, TooltipGap)) break;
            column.Add(b);
            cursor = b;
        }

        // Then upward, in case the pointer landed in the middle of the tooltip rather
        // than on its first line.
        //
        // THE TITLE IS ALLOWED TO BE INDENTED. A Guild Wars 2 tooltip puts an icon beside
        // its name, so the name starts further right than the prose underneath it - 285
        // against 228, 335 against 296. Judged by the same flush-left rule as the body,
        // the title is dropped, and the title is the single most useful line in the whole
        // tooltip: "Slab of Red Meat" became "77/1,000 in Material Storage". So the first
        // step upward may be indented, provided it sits directly above and reads at the
        // same size. Everything above THAT is held to the strict rule again, which keeps
        // the line belonging to whatever is drawn behind the tooltip out of it.
        var above = others.Where(b => b.CentreY < anchor.CentreY).OrderByDescending(b => b.CentreY).ToList();
        cursor = column[0];
        bool titleAllowed = true;
        foreach (var b in above)
        {
            var joins = Continues(b, cursor, TooltipGap)
                     || (titleAllowed && IsTitleOf(b, cursor));
            if (!joins) break;

            column.Insert(0, b);
            cursor = b;
            titleAllowed = false;
        }

        return column;
    }

    /// <summary>
    /// Everything sharing the anchor's line of the screen, left to right.
    ///
    /// A row in a list - "Catapult Blueprint ..... 2 silver 40 copper" - is one thought
    /// split across the width of a panel. The lines must genuinely share a baseline
    /// (most of their heights overlapping) and be the same size, which is what stops
    /// this reaching across to something that merely happens to be at a similar height
    /// on the far side of the screen.
    /// </summary>
    private static List<TextBox> GrowRow(TextBox anchor, List<TextBox> others)
    {
        var h = Math.Max(6, anchor.H);

        var sameLine = others.Where(b =>
        {
            if (Math.Abs(b.H - anchor.H) > h * SizeTol) return false;

            var overlap = Math.Min(b.Bottom, anchor.Bottom) - Math.Max(b.Y, anchor.Y);
            if (overlap < Math.Min(b.H, anchor.H) * 0.6) return false;      // not the same line

            // Across a gap, but not across the whole panel.
            var gap = b.X > anchor.Right ? b.X - anchor.Right
                    : anchor.X > b.Right ? anchor.X - b.Right : 0;
            return gap <= h * 14;
        }).ToList();

        var row = new List<TextBox> { anchor };
        row.AddRange(sameLine);
        return row.OrderBy(b => b.X).ToList();
    }

    /// <summary>
    /// Is <paramref name="title"/> the heading of the block starting at <paramref name="body"/>?
    ///
    /// Indented, because of the icon beside it, but otherwise clearly attached: directly
    /// above, no further left than the body, overlapping it horizontally, and at a
    /// comparable size. Titles are often a little larger, so the size test is looser here
    /// than between body lines.
    /// </summary>
    private static bool IsTitleOf(TextBox title, TextBox body)
    {
        var h = Math.Max(6, Math.Min(title.H, body.H));

        var gap = body.Y - title.Bottom;
        if (gap < -h * 1.1 || gap > h * TooltipGap) return false;

        if (title.X < body.X - h * AlignTol) return false;          // a heading is not to the LEFT
        if (Math.Abs(title.H - body.H) > h * 0.8) return false;     // not a different kind of text

        var overlap = Math.Min(title.Right, body.Right) - Math.Max(title.X, body.X);
        return overlap > 0;
    }

    /// <summary>Does <paramref name="lower"/> read as the next line of <paramref name="upper"/>?</summary>
    private static bool Continues(TextBox upper, TextBox lower, double gapLines)
    {
        var h = Math.Max(6, Math.Min(upper.H, lower.H));

        var gap = lower.Y - upper.Bottom;
        if (gap < -h * 1.1 || gap > h * gapLines) return false;   // boxes may overlap            // too far, or overlapping

        if (Math.Abs(upper.H - lower.H) > h * SizeTol) return false;       // different text size

        // FLUSH LEFT, OR IT IS NOT THE SAME PARAGRAPH.
        //
        // Merely overlapping side to side is not enough, and letting that count is how a
        // tooltip swallowed the list behind it - the castle upgrades panel returned
        // "Castle Tactic. Airship Defense. Fortified Improvement. Auto Turrets. Hardened
        // Gates. Hardened Siege", six separate entries the player never pointed at, while
        // the actual tooltip was thrown away.
        //
        // The coordinates separate them cleanly. A tooltip's lines are set flush against
        // the same left margin - 282 then 281, 260 then 259. Entries in a list are each
        // indented by their own icon and wander: 148, 184, 149, 174, 154, 143. Requiring
        // the left edges to agree keeps every continuation line and refuses every
        // neighbour.
        return Math.Abs(upper.X - lower.X) <= h * AlignTol;
    }

    /// <summary>
    /// A title under a name - "Legendary Quinn [HALO]" then "Long-Term Commitment".
    ///
    /// Only when it is unmistakably that character's: centred under the name, one line
    /// down, the same size, and with nothing else nearby that could equally claim it.
    /// In a crowd the safe answer is the name alone.
    /// </summary>
    private static TextBox? FindSubtitle(TextBox anchor, List<TextBox> others)
    {
        var h = Math.Max(6, anchor.H);

        var candidates = others.Where(b =>
        {
            var gap = b.Y - anchor.Bottom;
            if (gap < -h * 1.1 || gap > h * SubtitleGap) return false;
            if (Math.Abs(b.H - anchor.H) > h * SizeTol) return false;
            return Math.Abs(b.CentreX - anchor.CentreX) <= Math.Max(anchor.W, b.W) * CentreTol;
        }).ToList();

        if (candidates.Count != 1) return null;   // none, or an ambiguous crowd

        // And nobody ELSE's name sits between them.
        var sub = candidates[0];
        var intruder = others.Any(b =>
            !Same(b, sub) &&
            b.CentreY > anchor.Bottom && b.CentreY < sub.Y &&
            Math.Min(b.Right, anchor.Right) - Math.Max(b.X, anchor.X) > 0);

        return intruder ? null : sub;
    }

    private static bool Same(TextBox a, TextBox b) =>
        a.Text == b.Text && Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Y - b.Y) < 0.001;
}

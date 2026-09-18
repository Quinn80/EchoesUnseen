using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenCvSharp;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>Which currency a window deals in, when the icon alone does not say.</summary>
public enum PriceContext { Unknown, Coins, Gems, AstralAcclaim }

/// <summary>
/// SAY MONEY AS MONEY - from the colours the game paints it in.
///
/// Guild Wars 2 writes "15 [silver coin] 36 [copper coin]", and OCR has no idea what a coin
/// is: it returns "1536", "15 36", "24@", "137e 38@ 95". But the game colours the figures
/// themselves by denomination - gold figures are yellow, silver figures grey-white, copper
/// figures orange - and draws a gem price beside a blue gem and an Astral Acclaim price
/// beside a teal crystal. So the figures are split where the recogniser's own character
/// positions show a gap wide enough to hold a coin, and each group is named by its colour.
/// </summary>
public static class PriceReader
{
    private enum Tint { Unknown, Gold, Silver, Copper, Gem, Teal }

    public sealed record Reading(string Spoken, string Currency, IReadOnlyList<long> Amounts);

    public static Reading? Read(Mat picture, TextLine line, double h, PriceContext context)
    {
        var groups = DigitGroups(line, h);
        if (groups.Count == 0) return null;

        // Colour of each group's figures, and of whatever is drawn just after it.
        var tints = groups.Select(g => GlyphTint(picture, g.X0 - h * 0.08, g.X1 + h * 0.08, line.Box, h)).ToList();
        var icon = GlyphTint(picture, groups[^1].X1 + h * 0.18, groups[^1].X1 + h * 1.05, line.Box, h);

        bool coinish = tints.Any(t => t is Tint.Gold or Tint.Copper) || icon is Tint.Gold or Tint.Copper
                       || (context == PriceContext.Coins && groups.Count <= 3);
        // In the Gem Store a single figure is gems, whatever colour its button is painted.
        if (context == PriceContext.Gems && groups.Count == 1) coinish = false;
        if (icon == Tint.Gem || (context == PriceContext.Gems && !coinish))
            return new Reading($"{Words(groups[0].Value)} gems", "gems", new[] { groups[0].Value });
        if (icon == Tint.Teal || (context == PriceContext.AstralAcclaim && !coinish))
            return new Reading($"{Words(groups[0].Value)} Astral Acclaim", "astral", new[] { groups[0].Value });

        if (coinish && groups.Count <= 3 && groups.All(g => g.Value >= 0))
        {
            // Name each group by its colour; where the colour is unclear, by position -
            // coins are always written gold, silver, copper from left to right, and the game
            // leaves out the larger denominations that are zero.
            var names = new string?[groups.Count];
            for (int i = 0; i < groups.Count; i++)
                names[i] = tints[i] switch { Tint.Gold => "gold", Tint.Silver => "silver", Tint.Copper => "copper", _ => null };
            // A coin price always runs down to copper ("8 [silver] 0 [copper]", "1 [gold] 0 0"),
            // so a single figure is copper - or it is not coins at all: badges, karma, claim
            // tickets and map currencies all sit beside a single figure. Naming such a price
            // "silver" would be a confident wrong answer, so a lone figure that is not painted
            // copper is said as the number alone.
            if (groups.Count == 1 && tints[0] != Tint.Copper && icon != Tint.Copper)
                return new Reading(Words(groups[0].Value), "unknown", new[] { groups[0].Value });
            var byPosition = groups.Count switch
            {
                1 => new[] { "copper" },
                2 => new[] { "silver", "copper" },
                _ => new[] { "gold", "silver", "copper" },
            };
            // If the colours disagree with each other's order, trust position.
            var order = new[] { "gold", "silver", "copper" };
            var named = names.Select((n, i) => n ?? byPosition[i]).ToArray();
            bool ordered = named.Select(n => Array.IndexOf(order, n)).Zip(named.Skip(1).Select(n => Array.IndexOf(order, n)), (a, b) => a < b).All(v => v);
            if (!ordered) named = byPosition;
            // Silver and copper never reach 100. A figure that would have to be "175 silver" is no
            // coin at all: a vendor's tickets and badges ("175 [ticket] + 250 [memory]") are said
            // as the figures, never as money they are not.
            if (groups.Where((g, i) => g.Value >= 100 && named[i] is "silver" or "copper").Any())
                return new Reading(string.Join(" plus ", groups.Select(g => Words(g.Value))), "unknown",
                                   groups.Select(g => g.Value).ToList());

            var parts = new List<string>();
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Value != 0 || groups.All(g => g.Value == 0))
                    parts.Add($"{groups[i].Value} {named[i]}");
            if (parts.Count == 0) parts.Add($"0 {named[^1]}");
            return new Reading(string.Join(", ", parts), "coins", groups.Select(g => g.Value).ToList());
        }

        return new Reading(Words(groups[0].Value), "unknown", new[] { groups[0].Value });
    }

    private static string Words(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private readonly record struct Group(long Value, double X0, double X1);

    /// <summary>Runs of figures, split where the characters are far enough apart to have a
    /// coin between them.</summary>
    private static List<Group> DigitGroups(TextLine line, double h)
    {
        var text = line.Text ?? "";
        var xs = line.CharX;
        var groups = new List<Group>();
        var digits = new List<(char C, double X)>();

        void Flush()
        {
            if (digits.Count == 0) return;
            var s = new string(digits.Select(d => d.C).ToArray());
            if (long.TryParse(s, out var v)) groups.Add(new Group(v, digits[0].X, digits[^1].X));
            digits.Clear();
        }

        double approx(int i) => xs != null && i < xs.Length ? xs[i]
            : line.Box.X + (i + 0.5) / Math.Max(1, text.Length) * line.Box.W;
        // A coin is about as wide as the figures are tall. Prices are often set larger than the
        // text around them (the Vault's "600"), and their digits are then further apart than a
        // body-text gap: the gap is measured against the figures' own size, not the body text's.
        var coinGap = Math.Max(h * 0.62, Math.Min(line.Box.H, h * 1.6) * 0.72);

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsDigit(c))
            {
                var x = approx(i);
                bool afterSeparator = i > 0 && (text[i - 1] == ',' || text[i - 1] == '.') && digits.Count > 0;
                if (digits.Count > 0 && !afterSeparator && x - digits[^1].X > coinGap) Flush();   // a coin's width apart
                digits.Add((c, x));
            }
            else if ((c == ',' || c == '.') && digits.Count > 0 && i + 3 < text.Length
                     && text.Substring(i + 1, 3).All(char.IsDigit))
            {
                // thousands separator: stay in the number
            }
            else Flush();
        }
        Flush();
        return groups;
    }

    /// <summary>The colour of the bright or saturated strokes in a strip of the line.</summary>
    private static Tint GlyphTint(Mat picture, double x0, double x1, Box lineBox, double h)
    {
        var box = Box.FromLTRB(x0, lineBox.Y + lineBox.H * 0.12, x1, lineBox.Bottom - lineBox.H * 0.12)
                     .Clip(new Box(0, 0, picture.Width, picture.Height));
        if (box.W < 2 || box.H < 2) return Tint.Unknown;
        using var roi = new Mat(picture, new OpenCvSharp.Rect((int)box.X, (int)box.Y, (int)Math.Max(1, box.W), (int)Math.Max(1, box.H)));
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);

        // Background: the median brightness; strokes: clearly brighter than that, or vivid.
        var vs = new List<byte>(hsv.Rows * hsv.Cols);
        for (int r = 0; r < hsv.Rows; r++)
            for (int c = 0; c < hsv.Cols; c++) vs.Add(hsv.At<Vec3b>(r, c).Item2);
        vs.Sort();
        var bg = vs[vs.Count / 2];

        double sx = 0, cx = 0, sat = 0, n = 0;
        for (int r = 0; r < hsv.Rows; r++)
            for (int c = 0; c < hsv.Cols; c++)
            {
                var p = hsv.At<Vec3b>(r, c);
                bool stroke = p.Item2 >= Math.Max(80, bg + 45) || (p.Item1 >= 110 && p.Item2 >= 90);
                if (!stroke) continue;
                var ang = p.Item0 * 2 * Math.PI / 180.0;   // OpenCV hue is 0..180
                sx += Math.Cos(ang) * p.Item1; cx += Math.Sin(ang) * p.Item1;
                sat += p.Item1; n++;
            }
        if (n < Math.Max(3, h * 0.2)) return Tint.Unknown;
        var meanSat = sat / n;
        if (meanSat < 70) return Tint.Silver;
        var hue = Math.Atan2(cx, sx) * 180 / Math.PI;       // degrees of the 0..180 scale x2
        if (hue < 0) hue += 360;
        hue /= 2;                                             // back to OpenCV units
        if (hue >= 5 && hue < 17) return Tint.Copper;
        if (hue >= 17 && hue < 36) return Tint.Gold;
        if (hue >= 78 && hue < 98) return Tint.Teal;
        if (hue >= 98 && hue < 130) return Tint.Gem;
        return Tint.Unknown;
    }
}

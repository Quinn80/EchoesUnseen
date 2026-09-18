using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services.Hover;

/// <summary>One reward card in the Wizard's Vault, as far as the screen shows it.</summary>
public sealed record VaultCard(
    string Name,
    int Price,
    string? Availability,
    double Left,
    double Right,
    double TextTop)
{
    /// <summary>Something a voice can say. Only the parts actually found.</summary>
    public string Spoken
    {
        get
        {
            var parts = new List<string> { Name };
            if (Price > 0) parts.Add($"{Price} Astral Acclaim");
            if (!string.IsNullOrWhiteSpace(Availability)) parts.Add(Availability!);
            return string.Join(". ", parts) + ".";
        }
    }

    /// <summary>Is this the same card as <paramref name="other"/>? Used to stay quiet
    /// while the pointer wanders around inside one reward.</summary>
    public bool SameAs(VaultCard? other) =>
        other != null &&
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        Price == other.Price;
}

/// <summary>
/// POINT AT THE REWARD, NOT AT ITS CAPTION.
///
/// A Wizard's Vault card is a big picture with a price and a name in small type
/// underneath it. Quinn can find the picture - it is three hundred pixels of artwork.
/// She cannot reliably land on a twelve-pixel caption, and asking her to is the whole
/// problem: hovering the artwork produced "th weapon skin", "etle mount skin",
/// "Mystic. 1 for 60" - fragments of whichever caption happened to clip the edge of the
/// capture.
///
/// So the pointer picks the CARD and the card answers for itself. Cards are laid out in
/// a regular grid with a price and a name at the foot of each one, so the card the
/// pointer is in is the caption whose horizontal span contains it - not the caption
/// nearest the pointer, which in a grid is frequently the neighbour's.
///
/// AND THE NAMES DO NOT HAVE TO BE GUESSED. The app already fetches the season's real
/// listings from the account API, with exact names and exact costs. A caption read as
/// "th weapon skin" resolves against that list to the item it actually is. This is not
/// inventing a description - it is recognising which of a known handful of rewards is on
/// screen, which is the one job OCR is bad at and a list of candidates is perfect for.
///
/// No WPF, no app state: the replay harness runs this against saved captures.
/// </summary>
public static class VaultCards
{
    /// <summary>A price in the vault: a bare number, optionally with the acclaim glyph
    /// misread into a letter or two beside it.</summary>
    private static readonly Regex PriceLike = new(@"^\s*([1-9][\d,]{0,5})\s*[^\d\s]{0,3}\s*$",
                                                  RegexOptions.Compiled);

    private static readonly Regex AvailabilityLike =
        new(@"^\s*(sold\s*out|\d+\s*available|\d+\s*for\s*\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Find the card the pointer is inside, if this looks like a vault grid at all.
    ///
    /// <paramref name="knownNames"/> is the season's real reward names from the API; pass
    /// an empty list and the caption is used as read.
    /// </summary>
    public static VaultCard? Find(IReadOnlyList<TextBox> boxes, double px, double py,
                                  IReadOnlyList<string> knownNames)
    {
        if (boxes == null || boxes.Count < 2) return null;

        // A price with a name close underneath it is the foot of a card.
        var captions = new List<(TextBox Price, TextBox Name)>();
        foreach (var price in boxes)
        {
            var m = PriceLike.Match(price.Text);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value.Replace(",", ""), out var cost) || cost <= 0) continue;

            var h = Math.Max(6, price.H);
            var name = boxes
                .Where(b => !ReferenceEquals(b.Text, price.Text))
                .Where(b => b.CentreY > price.CentreY && b.CentreY - price.CentreY < h * 3.5)
                .Where(b => Overlaps(b, price, 0.25))
                .Where(b => b.Text.Count(char.IsLetter) >= 4)
                .OrderBy(b => b.CentreY)
                .FirstOrDefault();

            if (name.Text != null) captions.Add((price, name));
        }

        if (captions.Count == 0) return null;

        // WHICH CARD? The one whose caption straddles the pointer's column. In a grid the
        // NEAREST caption is often the card next door - the gap between cards is smaller
        // than the height of the artwork above them.
        var hit = captions
            .Select(c => (c, Span: Span(c.Price, c.Name)))
            .OrderBy(t => Distance(px, t.Span))
            .First();

        // Too far sideways to be the card the pointer is in.
        var cardWidth = t_Width(hit.Span);
        if (Distance(px, hit.Span) > cardWidth * 0.6) return null;

        var priceText = PriceLike.Match(hit.c.Price.Text).Groups[1].Value.Replace(",", "");
        int.TryParse(priceText, out var priceValue);

        var caption = Clean(hit.c.Name.Text);
        var resolved = Resolve(caption, knownNames);

        // IS THIS ACTUALLY THE VAULT? A number above a name is a common enough shape -
        // the trading post produced "PRIES" over "97" and this happily called it 97
        // Astral Acclaim, which is the wrong currency in the wrong window. Claim it only
        // on evidence: either the caption resolved to a real reward from this season's
        // listings, or there are several captions side by side, which is a grid of cards
        // and not a price label on its own.
        if (resolved == null && captions.Count < 2) return null;

        // "Sold out" or "3 available" sits with the same card, in its column.
        var status = boxes
            .Where(b => AvailabilityLike.IsMatch(b.Text))
            .Where(b => Distance(px, (b.X, b.Right)) <= cardWidth * 0.6)
            .Select(b => b.Text.Trim())
            .FirstOrDefault();

        return new VaultCard(resolved ?? caption, priceValue, status,
                             hit.Span.Left, hit.Span.Right, hit.c.Price.Y);
    }

    /// <summary>
    /// Which of the season's rewards is this caption?
    ///
    /// OCR of eleven-pixel text loses characters, so the caption arrives as "th weapon
    /// skin" or "etle mount skin". Against a known list of maybe forty names that is
    /// still plenty to identify: the fragment has to be a run of the real name, or share
    /// most of its letters. A weak match is left alone rather than forced - saying the
    /// wrong reward's name confidently would be worse than saying a broken one.
    /// </summary>
    public static string? Resolve(string caption, IReadOnlyList<string> knownNames)
    {
        if (knownNames == null || knownNames.Count == 0) return null;

        var c = Normalise(caption);
        if (c.Length < 4) return null;

        string? best = null;
        double bestScore = 0;

        foreach (var name in knownNames)
        {
            var n = Normalise(name);
            if (n.Length == 0) continue;

            double score;
            if (n.Contains(c)) score = (double)c.Length / n.Length;     // a clean fragment
            else if (c.Contains(n)) score = (double)n.Length / c.Length;
            else score = Similarity(c, n);

            if (score > bestScore) { bestScore = score; best = name; }
        }

        return bestScore >= 0.55 ? best : null;
    }

    /// <summary>Letters in common, in order, over the longer string.</summary>
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        int i = 0, j = 0, same = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { same++; i++; j++; }
            else if (a.Length - i > b.Length - j) i++;
            else j++;
        }
        return (double)same / Math.Max(a.Length, b.Length);
    }

    private static string Normalise(string s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Clean(string s) =>
        Regex.Replace((s ?? "").Trim(), @"\s{2,}", " ");

    private static (double Left, double Right) Span(TextBox a, TextBox b) =>
        (Math.Min(a.X, b.X), Math.Max(a.Right, b.Right));

    private static double t_Width((double Left, double Right) s) => Math.Max(1, s.Right - s.Left);

    private static double Distance(double x, (double Left, double Right) s) =>
        x < s.Left ? s.Left - x : x > s.Right ? x - s.Right : 0;

    private static bool Overlaps(TextBox a, TextBox b, double minShare)
    {
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        return overlap > 0 && overlap / Math.Max(1, Math.Min(a.W, b.W)) >= minShare;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using OpenCvSharp;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>
/// POINT AT ONE THING. HEAR THAT ONE THING.
///
/// One picture of the screen and the pointer go in; one accessible object comes out:
///
///   1. TEXT GEOMETRY (RapidOCR detector) over a wide area around the pointer - where every
///      line of text is. Read sharply afterwards, line by line, from the full picture.
///   2. VISUAL GEOMETRY (OpenCV) over the same area - the long drawn edges, the rectangles
///      they make, the brightness of bands.
///   3. THE TOOLTIP, if one belongs to this hover: a block of text on a bordered panel,
///      anchored at the pointer the way the game anchors tooltips, and - when there is an
///      earlier picture - freshly changed. It may be far from the pointer; nearness to the
///      pointer is never used to trim what it says.
///   4. THE DIRECT TARGET, topmost first: a button the pointer is inside; the row of a list
///      (merchant, trading post, menu) the pointer's band crosses; the card whose caption
///      sits under or beside the pointer; the icon the anchored tooltip belongs to; the label
///      under the pointer. Nothing inside the tooltip panel is ever part of the target.
///   5. THE CONTRACT: name first, price from the same object, tooltip without repeating it.
///
/// No WPF, no app state: the replay harness runs exactly this against the recorded corpus.
/// </summary>
public sealed class HoverFusion
{
    private readonly MatOcr _ocr;
    private List<TextLine> _allLines = new();
    private List<TextLine> _nearPointer = new();
    private double _detScale = 1.0;
    private string _context = "";

    // What was said last, so moving inside one object stays quiet.
    private string? _lastKey;
    private readonly List<Tooltip> _untitled = new();

    /// <summary>
    /// The season's Wizard's Vault reward names from the account API, when the app has them.
    /// A proven name beats a read one: a card whose caption was read as "Enchanted Rler
    /// Beetle Skin" is said as the listing's own name. Only a close match is taken (the
    /// same rule the classic reader uses); a weak one leaves the read name alone.
    /// HoverReplay has no API, so the corpus is scored on what was read.
    /// </summary>
    public IReadOnlyList<string>? KnownVaultNames { get; set; }
    private string? _lastTooltip;
    private string? _lastPrice;
    private List<string> _lastTooltipLines = new();

    /// <summary>Were (nearly) all of these tooltip lines heard already? Recognition differs a
    /// little between two pictures of one tooltip - a stray "c" before a line, a line misread -
    /// so it is judged line by line: three in four lines like one already heard.</summary>
    private static bool LinesHeard(IReadOnlyList<string> lines, IReadOnlyList<string> heard)
    {
        var worded = lines.Where(l => TextLines.Letters(l) >= 4).ToList();
        if (worded.Count == 0 || heard.Count == 0) return false;
        var known = worded.Count(l => heard.Any(o => TextLines.Similarity(o, l) >= 0.75));
        return known >= worded.Count * 0.75;
    }
    private Box _lastBounds;

    public HoverFusion(MatOcr ocr) { _ocr = ocr; }

    /// <summary>Line box height at Quinn's reference setup (RapidOCR boxes, physical px) -
    /// only a starting guess; the measured height replaces it on every picture.</summary>
    public const double ReferenceLineHeight = 30;

    public AccessibleTarget Analyse(Mat picture, Mat? previous, ScreenPicture sp, double physX, double physY,
                                    CancellationToken ct = default)
    {
        var t = new AccessibleTarget();
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var (x, y) = sp.ToPicture(physX, physY);
        double s = sp.Scale;
        double guessH = ReferenceLineHeight * s;

        // ---- 1. text geometry -----------------------------------------------------------
        // Wider above the pointer than below: the window's title, which names the interface
        // (Gem Store, Wizard's Vault, Vendor), sits at its top.
        var region = Box.FromLTRB(x - 1050 * s, y - 1000 * s, x + 1050 * s, y + 700 * s).Clip(sp.Bounds);
        var detScale = Math.Clamp(0.8 / s, 0.5, 1.6);
        _detScale = detScale;
        var rawBoxes = _ocr.Detect(picture, region, detScale, ct);
        t.TimingsMs["detect"] = sw.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        sw.Restart();
        var boxes = MergeBoxes(rawBoxes);
        var read = _ocr.Recognize(picture, boxes, guessH, ct);
        var lines = TextLines.MergeSplit(read.Where(l => l.Text.Trim().Length > 0 && l.Confidence >= 45)
                                             .SelectMany(TextLines.SplitAtGaps))
                             .Select(l => l with { Text = TextLines.Clean(l.Text) })
                             .Where(l => l.Text.Length > 0).ToList();
        t.TimingsMs["recognise"] = sw.Elapsed.TotalMilliseconds;
        ct.ThrowIfCancellationRequested();

        double h = TextLines.LineHeight(lines, x, y, guessH);

        _nearPointer = new List<TextLine>();
        // NOTHING READ AT THE POINTER: look again, closer. A dimmed dialog's button label - grey on
        // grey - is too faint for the detector at the scale it scans the whole region. Only the
        // small area at the pointer is read again, with its contrast evened out.
        if (!lines.Any(l => l.Box.Inflate(h * 4, h * 1.6).Contains(x, y)))
        {
            sw.Restart();
            var near = ReadNearPointer(picture, region, x, y, h, ct);
            t.TimingsMs["reread"] = sw.Elapsed.TotalMilliseconds;
            if (near.Count > 0)
            {
                // only ever a button's label: faint text found this way is not trusted for anything else
                _nearPointer = near;
                t.Reasons.Add($"read again at the pointer: {string.Join(" | ", near.Select(l => l.Text))}");
            }
        }
        foreach (var l in lines) t.Debug.Add((l.Box, "ocr", l.Text));

        // CHAT IS NEVER A HOVER TARGET. The chat panel is dense, left-aligned prose right beside
        // most of the interface, and it looks like a tooltip and like a list to anything that
        // only measures geometry. It is found by what chat lines look like and cut out whole.
        var chat = ChatPanel(lines, h);
        if (!chat.IsEmpty)
        {
            t.Debug.Add((chat, "rejected", "chat panel"));
            if (chat.Inflate(h * 0.3, h * 0.3).Contains(x, y))
            {
                t.Type = TargetType.None;
                t.Reasons.Add("pointer is on the chat panel");
                t.TimingsMs["total"] = total.Elapsed.TotalMilliseconds;
                return t;
            }
            lines = lines.Where(l => !chat.Contains(l.Box.CentreX, l.Box.CentreY)).ToList();
        }

        // World event banners ("Objective captured! Cathedral of Blood has captured Speldan
        // Clearcut!") are drawn over whatever window is open. They are announced elsewhere and
        // are never what the pointer is on.
        lines = lines.Where(l => !EventBanner.IsMatch(l.Text)).ToList();

        // ---- 2. visual geometry ---------------------------------------------------------
        sw.Restart();
        using var geo = new UiGeometry(picture, region, h);
        t.TimingsMs["opencv"] = sw.Elapsed.TotalMilliseconds;
        lines = SplitPieceHeights(lines, geo, h);

        sw.Restart();
        var context = WindowContext(lines, x, y, h);
        _context = context;
        t.Reasons.Add($"line height {h / s:F0} px, context {context}");

        _allLines = lines;

        // An NPC's dialog choices are window text, never a tooltip about something else.
        var choices = DialogChoices(lines, h);
        foreach (var c in choices) t.Debug.Add((c.Box, "row", "choice"));

        // ---- 3. the tooltip -------------------------------------------------------------
        var tip = FindTooltip(picture, previous, geo, choices.Count > 0 ? lines.Except(choices).ToList() : lines, x, y, h, t);
        List<TextLine> window = lines;
        if (tip != null)
        {
            window = OutsidePanel(picture, lines, tip.Panel, h);
            t.TooltipBounds = tip.Panel;
            t.TooltipTitle = tip.Title;
            t.TooltipLines.AddRange(tip.Lines.Select(l => l.Text));
        }

        // ---- 4. the direct target -------------------------------------------------------
        var resolved = TryDialogChoice(choices, x, y, h, t)
                       || TryButton(geo, window, _nearPointer, x, y, h, t)
                       || TryList(picture, geo, window, x, y, h, context, tip, t)
                       || TryCard(picture, window, x, y, h, context, tip, t)
                       || TryCardByTooltip(picture, window, x, y, h, context, tip, t)
                       || TryIcon(tip, context, t)
                       || TryLabel(window, x, y, h, context, t)
                       || TryTab(window, x, y, h, t)
                       || TryBanner(window, x, y, h, context, t)
                       || TryHoverLabel(window, x, y, h, t);
        if (!resolved)
        {
            t.Type = TargetType.None;
            t.Reasons.Add("nothing under the pointer");
            t.TooltipLines.Clear();
            t.TooltipBounds = default;
        }

        // A tooltip only belongs to the object when it is about that object.
        if (tip != null && resolved && t.Type is not (TargetType.InventorySlot or TargetType.IconTooltip))
        {
            // The strongest candidate may be a list of names under a translucent tooltip; another
            // candidate, near the pointer, may be the tooltip that is about this object.
            if (!Belongs(tip, t, x, y, h))
            {
                var other = _tipCandidates.FirstOrDefault(c => !ReferenceEquals(c, tip) && c.Title != null
                                                               && c.Nearest <= 8 && Belongs(c, t, x, y, h));
                if (other != null)
                {
                    t.Reasons.Add($"tooltip '{other.Title}' is about '{t.Name}' - used instead of '{tip.Title}'");
                    tip = other;
                    t.TooltipBounds = tip.Panel;
                    t.TooltipTitle = tip.Title;
                    t.TooltipLines.Clear();
                    t.TooltipLines.AddRange(tip.Lines.Select(l => l.Text));
                }
            }
            if (!Belongs(tip, t, x, y, h))
            {
                t.Reasons.Add($"tooltip '{tip.Title}' is not about '{t.Name}' - not read");
                t.TooltipLines.Clear();
                t.TooltipBounds = default;
                t.TooltipTitle = null;
            }
        }

        // No tooltip read: an untitled one set aside may still name this object on a line of its own.
        if (resolved && t.TooltipLines.Count == 0 && t.Type is TargetType.MenuRow or TargetType.MerchantHorizontalItem
                                                              or TargetType.TradingPostCard
            && t.Name is { Length: >= 8 } rowName)
        {
            // the row's own line inside a "tooltip" means it is the list itself, not a tooltip
            var own = t.TargetBounds;
            var named = _untitled.Where(c => c.Nearest <= 8 && c.Panel.Intersect(own).Area < own.Area * 0.2
                                             && c.Lines.Any(l => TextLines.Similarity(l.Text, rowName) >= 0.85
                                                                 && l.Box.Intersect(own).Area <= 0))
                                 .OrderBy(c => c.Nearest).FirstOrDefault();
            if (named != null)
            {
                t.TooltipBounds = named.Panel;
                t.TooltipTitle = rowName;
                t.TooltipLines.AddRange(named.Lines.Select(l => l.Text));
                t.Reasons.Add($"untitled tooltip names '{rowName}' on a line of its own - read");
            }
        }

        // ---- 5. the contract ------------------------------------------------------------
        // A Gem Store card whose own price is covered by its tooltip: the tooltip states it, "[400 Gems]".
        if ((t.Type == TargetType.GemStoreCard || t.Type == TargetType.IconTooltip && context == "gemstore")
            && string.IsNullOrWhiteSpace(t.Price))
            foreach (var line in t.TooltipLines)
            {
                var m = TooltipGems.Match(line);
                if (!m.Success || !int.TryParse(Regex.Replace(m.Groups[1].Value, "[,.]", ""), out var gems) || gems <= 0)
                    continue;
                t.Price = gems.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " gems";
                t.Type = TargetType.GemStoreCard;   // a product's picture in the Gem Store is its card
                t.Reasons.Add($"price from its tooltip: '{line}'");
                break;
            }
        if (t.Type == TargetType.WizardVaultCard && KnownVaultNames is { Count: > 0 } && !string.IsNullOrWhiteSpace(t.Name))
        {
            var listed = VaultCards.Resolve(t.Name, KnownVaultNames);
            if (listed != null && listed != t.Name)
            {
                t.Reasons.Add($"vault listing: read '{t.Name}', listed as '{listed}'");
                t.Name = listed;
            }
        }
        t.Speech = SpeechComposer.Compose(t);
        var key = $"{t.Type}|{Norm(t.Name)}";
        var samePlace = _lastBounds.IsEmpty || t.TargetBounds.IsEmpty || _lastBounds.IoU(t.TargetBounds) > 0.25;
        // The same name, or - for a banner or a card whose name ran over several lines - one name
        // read shorter than the other because a line of it was covered ("Currency Exchange 50 Gold
        // for" under the View Details label), in the same place.
        var lastName = _lastKey?.Split('|', 2) is { Length: 2 } kp && kp[0] == t.Type.ToString() ? kp[1] : null;
        var name = Norm(t.Name);
        var sameObject = t.Type != TargetType.None && lastName != null
                         && (lastName == name && (samePlace || name.Length > 3)
                             || samePlace && Math.Min(name.Length, lastName.Length) >= 12
                                     && (name.StartsWith(lastName) || lastName.StartsWith(name)));
        // SAME OBJECT, NOTHING NEW: QUIET. Resting again inside what was just announced says it again
        // only if something about it was not heard yet - its tooltip appeared, or a price the
        // tooltip had covered is now visible. A tooltip disappearing adds nothing, and neither does
        // the same tooltip read a little differently from one picture to the next.
        var tipHeard = string.IsNullOrEmpty(t.TooltipText)
                       || TextLines.Similarity(_lastTooltip, t.TooltipText) >= 0.85
                       || LinesHeard(t.TooltipLines, _lastTooltipLines);
        var priceHeard = string.IsNullOrWhiteSpace(t.Price) || Norm(t.Price) == Norm(_lastPrice);
        t.ShouldSpeak = t.Speech.Length > 0 && !(sameObject && tipHeard && priceHeard);
        if (t.Speech.Length > 0)
        {
            if (!sameObject)
            {
                _lastTooltip = t.TooltipText;
                _lastTooltipLines = new List<string>(t.TooltipLines);
                _lastPrice = t.Price;
            }
            else
            {
                if (!string.IsNullOrEmpty(t.TooltipText)) _lastTooltip = t.TooltipText;
                _lastTooltipLines.AddRange(t.TooltipLines);
                if (!string.IsNullOrWhiteSpace(t.Price)) _lastPrice = t.Price;
            }
            // the longer name stands for the object
            _lastKey = sameObject && lastName != null && lastName.Length > name.Length ? _lastKey : key;
            _lastBounds = t.TargetBounds;
        }

        t.TimingsMs["fusion"] = sw.Elapsed.TotalMilliseconds;
        t.TimingsMs["total"] = total.Elapsed.TotalMilliseconds;
        return t;
    }

    private static readonly Regex TooltipGems = new(@"^\W*\[\s*(\d{1,3}(?:[,.]\d{3})+|\d+)\s*Gems?\s*\]?\W*$",
                                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // =====================================================================================
    // Text boxes
    // =====================================================================================

    /// <summary>Detector boxes on one baseline a word's gap apart are one line; read them as one.</summary>
    /// <summary>
    /// ONE LINE, ITS OWN HEIGHT.
    ///
    /// The detector sometimes draws one box around a tooltip's sentence AND a larger price set a
    /// little lower beside it ("... Alabaster Spider's weapon" + "180"). SplitAtGaps cuts the two
    /// apart, but both pieces keep the tall box - and a sentence box reaching into the next line
    /// of the tooltip stops the tooltip's lines from stacking into one block. Each piece gets the
    /// height of its own letters back, measured from the picture.
    /// </summary>
    private static List<TextLine> SplitPieceHeights(List<TextLine> lines, UiGeometry geo, double h) =>
        lines.GroupBy(l => (l.Box.Y, l.Box.H))
             .SelectMany(g => g.Count() > 1 && g.Key.H > h * 1.35
                              ? g.Select(l => l with { Box = geo.TightenVertical(l.Box) })
                              : g)
             .ToList();

    private List<TextLine> ReadNearPointer(Mat picture, Box region, double x, double y, double h, CancellationToken ct)
    {
        var roi = Box.FromLTRB(x - h * 8, y - h * 2.2, x + h * 8, y + h * 1.6).Clip(region);
        if (roi.W < h * 2 || roi.H < h) return new List<TextLine>();
        var r = new OpenCvSharp.Rect((int)roi.X, (int)roi.Y, (int)roi.W, (int)roi.H);
        using var crop = new Mat(picture, r);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var clahe = Cv2.CreateCLAHE(3.0, new OpenCvSharp.Size(8, 8));
        using var even = new Mat();
        clahe.Apply(gray, even);
        using var bgr = new Mat();
        Cv2.CvtColor(even, bgr, ColorConversionCodes.GRAY2BGR);
        var up = Math.Clamp(28.0 / Math.Max(8, h), 1.0, 2.5);
        // found on the evened picture, read from the real one: evening thins the letters
        var boxes = MergeBoxes(_ocr.Detect(bgr, new Box(0, 0, bgr.Width, bgr.Height), up, ct))
                        // faint letters at a line's ends go unfound: reach a little further
                        .Select(b => b.Offset(r.X, r.Y).Inflate(h * 0.8, 0).Clip(roi)).ToList();
        return _ocr.Recognize(picture, boxes, h, ct)
                   .Where(l => l.Confidence >= 60 && TextLines.Letters(l.Text) >= 3 && HoverText.LooksLikeWords(l.Text))
                   .SelectMany(TextLines.SplitAtGaps)
                   .Select(l => l with { Text = TextLines.Clean(l.Text), Box = ToLetters(l) })
                   .Where(l => l.Text.Length > 0)
                   .ToList();
    }

    /// <summary>A line's box narrowed to its letters, from the recogniser's character positions.</summary>
    private static Box ToLetters(TextLine l)
    {
        if (l.CharX is not { Length: > 1 } xs) return l.Box;
        var step = (xs[^1] - xs[0]) / (xs.Length - 1);
        return Box.FromLTRB(Math.Max(l.Box.X, xs[0] - step * 0.7), l.Box.Y, Math.Min(l.Box.Right, xs[^1] + step * 0.7), l.Box.Bottom);
    }

    private static List<Box> MergeBoxes(IReadOnlyList<Box> raw)
    {
        var rows = new List<List<Box>>();
        foreach (var b in raw.OrderBy(b => b.CentreY).ThenBy(b => b.X))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var last = r[^1];
                var hh = Math.Max(4, Math.Min(last.H, b.H));
                if (last.VerticalOverlap(b) < 0.6) return false;
                if (Math.Abs(last.H - b.H) > hh * 0.5) return false;
                // Pieces of one line share its centre line; a price set lower and larger beside a
                // tooltip's sentence does not.
                if (Math.Abs(last.CentreY - b.CentreY) > hh * 0.3) return false;
                var gap = b.X - last.Right;
                // A word space, never the gap between two buttons or two labels side by side.
                return gap >= -hh * 0.3 && gap <= hh * 0.55;
            });
            if (row == null) rows.Add(new List<Box> { b }); else row.Add(b);
        }
        return rows.Select(r => Box.Bounding(r)).ToList();
    }

    /// <summary>
    /// What window is the pointer in? The title nearest above the pointer says so - and a
    /// title is only a hint for naming the currency and the contract, never for targeting.
    /// </summary>
    private static string WindowContext(IReadOnlyList<TextLine> lines, double x, double y, double h)
    {
        (string Key, string Word)[] keys =
        {
            ("vault", "Wizard's Vault"), ("vault", "Astral Rewards"), ("vault", "Legacy Rewards"),
            ("gemstore", "Gem Store"), ("tradingpost", "Trading Post"), ("vendor", "Vendor"),
            ("inventory", "Inventory"), ("bank", "Account Vault"),
        };
        string best = "none"; double bestD = double.MaxValue;
        foreach (var l in lines)
        {
            if (l.Box.CentreY > y + h) continue;
            foreach (var (key, word) in keys)
            {
                var at = l.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                // "Black Lion Trader [Trading Post]" is a nameplate in the world, not a window.
                var open = l.Text.LastIndexOf('[', at);
                if (open >= 0 && l.Text.IndexOf(']', at) > open) continue;
                var d = Math.Abs(l.Box.CentreX - x) * 0.5 + (y - l.Box.CentreY);
                if (d < bestD) { bestD = d; best = key; }
            }
        }
        return best;
    }

    private static readonly Regex EventBanner = new(@"(Objective captured|has captured|is under attack|has claimed)",
                                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChatLine = new(@"^\W{0,3}\[[A-Za-z0-9]{1,8}\]\s*\S", RegexOptions.Compiled);

    /// <summary>The chat panel: two or more channel-tagged lines ("[G] [HALO] Name: ...",
    /// "[M] ...", "[S] ...") or the "(press Enter to chat)" prompt, stacked on one margin.</summary>
    private static Box ChatPanel(IReadOnlyList<TextLine> lines, double h)
    {
        var tagged = lines.Where(l => (ChatLine.IsMatch(l.Text) && l.Text.Contains(':'))
                                      || l.Text.Contains("to chat)", StringComparison.OrdinalIgnoreCase)
                                      || l.Text.Contains("Enter to chat", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tagged.Count < 2) return default;
        var column = tagged.GroupBy(l => (int)Math.Round(l.Box.X / (h * 3))).OrderByDescending(g => g.Count()).First().ToList();
        if (column.Count < 2) return default;
        var box = Box.Bounding(column.Select(l => l.Box));
        // untagged continuation lines in the same column belong to the chat too
        var cont = lines.Where(l => l.Box.X >= box.X - h * 2 && l.Box.X <= box.X + h * 4
                                    && l.Box.CentreY >= box.Y - h * 2 && l.Box.CentreY <= box.Bottom + h * 1.2);
        return Box.Bounding(cont.Select(l => l.Box).Append(box)).Inflate(h * 0.2, h * 0.2);
    }

    private static PriceContext PriceContextFor(string context) => context switch
    {
        "vault" => PriceContext.AstralAcclaim,
        "gemstore" => PriceContext.Gems,
        "tradingpost" or "vendor" => PriceContext.Coins,
        _ => PriceContext.Unknown,
    };

    // =====================================================================================
    // Tooltip
    // =====================================================================================

    private sealed class Tooltip
    {
        public required Box Panel { get; init; }
        public required List<TextLine> Lines { get; init; }
        public string? Title { get; init; }
        public double AnchorDistance { get; init; }   // in line heights
        public double Nearest { get; init; }          // panel edge to pointer, in line heights

        /// <summary>Close enough to the pointer that it can only be this hover's tooltip.</summary>
        public bool AtPointer => AnchorDistance <= 3.5 || Nearest <= 3.0;
        public double Score { get; init; }
        /// <summary>Its panel is a drawn frame found around it, not the extent of its text.</summary>
        public bool Framed { get; init; }
    }

    private static readonly string[] TooltipMarkers =
    {
        "double-click", "consumable", "account bound", "soulbound", "trophy", "crafting material",
        "in bank", "material storage", "required level", "defense", "salvage", "skin locked", "skin unlocked",
        "unused upgrade", "range", "requires", "use to", "damage", "novelty", "limited time", "outfit",
        "on acquire", "contains", "can be", "rare", "exotic", "ascended", "masterwork", "fine", "event item",
    };

    /// <summary>Every block that passed as a possible tooltip in the last search, best first.</summary>
    private readonly List<Tooltip> _tipCandidates = new();

    private Tooltip? FindTooltip(Mat picture, Mat? previous, UiGeometry geo, List<TextLine> lines,
                                 double x, double y, double h, AccessibleTarget t)
    {
        _tipCandidates.Clear();
        _untitled.Clear();
        // The pointer's own line is never part of a tooltip about it.
        var candidates = lines.Where(l => !l.Box.Inflate(h * 0.15, h * 0.15).Contains(x, y)).ToList();
        var blocks = TextLines.Blocks(candidates, h);

        Tooltip? best = null;
        foreach (var b in blocks)
        {
            // A figure standing well above a block (the WvW score and timer at the top of the screen,
            // over a tooltip that reaches up to it) is not the block's first line.
            while (b.Lines.Count >= 3 && TextLines.IsNumeric(b.First.Text) && !TextLines.IsFraction(b.First.Text)
                   && b.Lines[1].Box.Y - b.First.Box.Bottom > h * 1.5)
                b.Lines.RemoveAt(0);
            var n = b.Lines.Count;
            var bounds = b.Bounds;
            var prose = b.Lines.Count(l => TextLines.IsSentence(l.Text) && !Chrome.IsMatch(l.Text));
            var markers = b.Lines.Count(l => TooltipMarkers.Any(m => l.Text.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (n < 2 && !(n == 1 && bounds.W >= h * 7 && prose == 1)) continue;
            if (n >= 2 && prose == 0 && markers == 0) continue;           // a list of names is not a tooltip
            // A card's own caption - "400 / Wizard's Ascended / Armor Chest / 3 Available", or a
            // "View Details" button over a name - is text of the window, not a tooltip.
            if (prose == 0 && (TextLines.IsNumeric(b.First.Text) || Chrome.IsMatch(b.First.Text) || ViewDetails.IsMatch(b.First.Text))) continue;
            // The Gem Store's "View Details" label is drawn over a card, never inside a tooltip - and the
            // "..." of a card's shortened name reads as a full stop, so the caption passes for prose.
            if (b.Lines.Any(l => ViewDetails.IsMatch(l.Text))) continue;
            if (prose == 0 && markers < 2) continue;

            var (found, sides) = geo.PanelAround(bounds);
            // An edge found INSIDE the text (the title's icon, a divider) is not the border: the
            // panel always contains every line of its own block.
            var panel = found.Union(bounds.Inflate(h * 0.3, h * 0.2));

            // A TRANSLUCENT TOOLTIP OVER A LIST. The rows under it show through and poke out past
            // its edge, so the detector runs a row's name into a line of the tooltip ("Triumph" +
            // "Defense: 338") and the block takes in the rows around it - and every row name is
            // read as tooltip text. The tooltip's own frame, a top and a bottom border of the same
            // width, says exactly where it is: when the block spills out of such a frame, only
            // what is inside the frame is read, and read afresh.
            List<TextLine>? framedLines = null;
            if (FramedPanel(geo, b, lines, h) is Box frame && !frame.Inflate(h * 0.3, h * 0.3).Contains(x, y))
            {
                framedLines = ReadFramed(picture, frame, h)
                    // on its margin, or set in by an icon; text further in is the window showing through
                    .Where(l => l.Box.X <= frame.X + h * 3.6 && l.Box.CoveredBy(frame.Inflate(2, 2)) >= 0.9)
                    .Where(l => TextLines.Letters(l.Text) >= 2 || l.Text.Count(char.IsDigit) >= 2)
                    .OrderBy(l => l.Box.CentreY).ThenBy(l => l.Box.X)
                    .ToList();
                if (framedLines.Count > 0)
                {
                    t.Debug.Add((frame, "panel", $"framed tooltip, its block spilled out of it ({bounds})"));
                    panel = frame;
                    sides = 4;
                }
                else framedLines = null;
            }
            // pointer on its text: a window, not a tooltip (unless the text around the pointer is a
            // list the tooltip's own frame has been told apart from)
            if (framedLines == null && bounds.Inflate(h * 0.3, h * 0.3).Contains(x, y)) { t.Debug.Add((bounds, "panel", "not a tooltip: pointer on its text")); continue; }

            // Where the game puts a tooltip for this pointer: a corner of the panel beside it.
            double gapH = h * 1.8;
            var anchors = new[]
            {
                Dist(panel.X, panel.Bottom, x, y - gapH),       // above-right (inventory, merchants, skills)
                Dist(panel.Right, panel.Y, x, y + gapH),        // below-left (Wizard's Vault)
                Dist(panel.X, panel.Y, x, y + gapH),            // below-right
                Dist(panel.Right, panel.Bottom, x, y - gapH),   // above-left
            };
            var anchor = anchors.Min() / h;
            var nearest = panel.DistanceTo(x, y) / h;

            double changed = previous != null ? UiGeometry.ChangedFraction(picture, previous, panel) : -1;

            if (anchor > 9 && nearest > 6 && changed < 0.08) continue;     // far away and nothing new: somebody else's text
            // Right against the pointer, yet at none of the corners a tooltip is drawn at: a menu
            // opened under the pointer (the Gem Store's Style drop-down, under its tab).
            if (nearest < 1.0 && anchor > 5.5) { t.Debug.Add((panel, "panel", $"not a tooltip: against the pointer, anchor {anchor:F1}h")); continue; }

            var score = 3.0 * Math.Exp(-anchor / 3.0)
                      + 1.5 * Math.Exp(-nearest / 4.0)
                      + 0.9 * sides / 4.0
                      + 1.0 * Math.Min(1.0, markers / 2.0)
                      + 0.6 * Math.Min(1.0, prose / 2.0)
                      + (n >= 3 ? 0.4 : 0)
                      + (changed >= 0.08 ? 0.7 : 0);

            t.Debug.Add((panel, "panel", $"tooltip? score {score:F1} anchor {anchor:F1}h sides {sides}"));
            if (score < 2.6) continue;

            var inside = framedLines ?? ReadPanel(picture, lines, b, bounds, panel, h);
            // Lettering seen through the panel from the window behind is a fraction as bright as the
            // tooltip's own - a row's name under a vendor's tooltip, a card's caption under a Gem
            // Store tooltip - even when the detector has run it into a line of the tooltip.
            inside = WithoutSeeThrough(geo, inside, h, t);
            if (framedLines == null && _context == "gemstore") inside = WithinSideBorder(geo, inside, h, t);
            if (inside.Count == 0) continue;

            var title = TitleOf(inside, h);
            // A Gem Store tooltip reads name, then "20% Off!", "[560 Gems]", "Limited time left at
            // this price!". When the panel's top edge was found below the name, the tooltip starts
            // at a badge: its name is the nearest line above (up to those three lines up), on its
            // margin, with no price under it (a card's caption has its price beneath it).
            var head = inside[0];
            if (Chrome.IsMatch(head.Text) || Notice.IsMatch(head.Text) || BracketPrice.IsMatch(head.Text))
            {
                var above = lines.Where(l => !inside.Contains(l) && TextLines.Letters(l.Text) >= 3
                                             && !Chrome.IsMatch(l.Text) && !Notice.IsMatch(l.Text) && !BracketPrice.IsMatch(l.Text)
                                             && !TextLines.IsNumeric(l.Text) && !TextLines.IsSentence(l.Text)
                                             && l.Box.Bottom <= head.Box.Y + h * 0.3 && l.Box.Y >= head.Box.Y - h * 3.8
                                             && l.Box.X >= b.Left - h * 0.7 && l.Box.X <= b.Left + h * 3.2
                                             && !lines.Any(p => TextLines.IsNumeric(p.Text) && !TextLines.IsFraction(p.Text)
                                                                && p.Box.Y >= l.Box.Bottom - h * 0.2 && p.Box.Y - l.Box.Bottom < h * 1.3
                                                                && p.Box.HorizontalOverlap(l.Box) > 0))
                                 .OrderByDescending(l => l.Box.Y).FirstOrDefault();
                if (above.Text != null && TitleOf(new List<TextLine> { above }, h) != null)
                {
                    title = above.Text;
                    inside.Insert(0, above);
                    panel = panel.Union(above.Box);
                    t.Reasons.Add($"tooltip title '{above.Text}' found above its badges");
                }
            }
            // A tooltip split in two: text showing through the panel ran into a line of its body,
            // and the detector broke the panel apart. Its title - set in by its icon - is then the
            // first line of the block directly above, on the same margin.
            // (Only when this block does not start with an icon-indented title of its own.)
            if (b.First.Box.X - b.Left < h * 0.8)
            {
                var upper = blocks.Where(o => !ReferenceEquals(o, b) && o.Lines.Count <= 4
                                              && Math.Abs(o.Left - b.Left) <= h * 0.8
                                              && o.Bounds.Bottom >= panel.Y - h * 1.2 && o.Bounds.Bottom <= panel.Y + h * 0.8
                                              && o.Bounds.HorizontalOverlap(panel) > 0.3
                                              && o.First.Box.X - b.Left >= h * 0.8 && o.First.Box.X - b.Left <= h * 3.2)
                                  .OrderByDescending(o => o.Bounds.Bottom).FirstOrDefault();
                var upperTitle = upper == null ? null : TitleOf(upper.Lines.ToList(), h);
                if (upper != null && upperTitle != null && !Chrome.IsMatch(upperTitle))
                {
                    title = upperTitle;
                    inside.InsertRange(0, upper.Lines.Where(l => !inside.Contains(l)));
                    panel = panel.Union(upper.Bounds);
                    t.Reasons.Add($"tooltip title '{upperTitle}' found in the block above its body");
                }
            }
            // A tooltip with no name, not beside the pointer, is text of the window: objective
            // descriptions, a page of prose. The game's untitled tooltips (objective upgrades)
            // sit right at the pointer. Kept aside: one that names the object again on a line of
            // its own can still be proved to be about it.
            if (title == null && anchor > 3.5 && nearest > 2.0)
            {
                _untitled.Add(new Tooltip { Panel = panel, Lines = inside, Title = null, AnchorDistance = anchor, Nearest = nearest, Score = score, Framed = framedLines != null });
                continue;
            }
            var cand = new Tooltip { Panel = panel, Lines = inside, Title = title, AnchorDistance = anchor, Nearest = nearest, Score = score, Framed = framedLines != null };
            _tipCandidates.Add(cand);
            if (best == null || score > best.Score) best = cand;
        }
        // A block inside a tooltip's drawn frame is a piece of that tooltip, not a tooltip of its own.
        var frames = _tipCandidates.Concat(_untitled).Where(c => c.Framed).Select(c => c.Panel.Inflate(h * 0.5, h * 0.5)).ToList();
        if (frames.Count > 0)
        {
            bool Piece(Tooltip c) => !c.Framed && frames.Any(f => c.Panel.CoveredBy(f) >= 0.8);
            _tipCandidates.RemoveAll(Piece);
            _untitled.RemoveAll(Piece);
            if (best != null && Piece(best)) best = _tipCandidates.OrderByDescending(c => c.Score).FirstOrDefault();
        }
        _tipCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (best != null)
            t.Reasons.Add($"tooltip '{best.Title}' {best.Lines.Count} lines, anchor {best.AnchorDistance:F1}h, score {best.Score:F1}");
        return best;
    }

    /// <summary>
    /// A tooltip's lines without the window showing through it. The tooltip's own lettering sets
    /// the level (the median line's 95th-percentile brightness); a line, or a run of characters
    /// within a line, at under 45% of it is text behind the panel. Characters are judged one by
    /// one where the recogniser gave their positions, so "20% Off!" keeps its place when the
    /// detector ran "Baggy Cargo Pants Skin" from the card beneath into it.
    /// </summary>
    private static List<TextLine> WithoutSeeThrough(UiGeometry geo, List<TextLine> lines, double h, AccessibleTarget t)
    {
        if (lines.Count < 3) return lines;
        var bright = lines.Select(l => geo.Lettering(l.Box)).ToList();
        var typical = bright.OrderBy(v => v).ElementAt(bright.Count / 2);
        if (typical < 90) return lines;   // a dim tooltip altogether: nothing to measure against
        var cut = typical * 0.45;
        var kept = new List<TextLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var xs = l.CharX;
            if (xs == null || xs.Length != l.Text.Length || l.Text.Length < 4)
            {
                if (bright[i] < cut) t.Debug.Add((l.Box, "rejected", $"seen through the tooltip: {l.Text}"));
                else kept.Add(l);
                continue;
            }
            // each character's own brightness; a space takes its neighbours' side
            double cw = Math.Max(2, l.Box.H * 0.45);
            var dim = new bool?[l.Text.Length];
            var level = new double[l.Text.Length];
            for (int k = 0; k < l.Text.Length; k++)
            {
                if (char.IsWhiteSpace(l.Text[k])) continue;
                var cb = Box.FromLTRB(xs[k] - cw * 0.5, l.Box.Y, xs[k] + cw * 0.5, l.Box.Bottom);
                level[k] = geo.Lettering(cb);
                dim[k] = level[k] < cut;
            }
            // A character or three measured dim is a letter's thin strokes or a misplaced position;
            // only a run of dim characters - a word or more - is text behind the panel.
            for (int k = 0; k < dim.Length;)
            {
                if (dim[k] != true) { k++; continue; }
                int j = k, letters = 0;
                while (j < dim.Length && dim[j] != false) { if (dim[j] == true) letters++; j++; }
                if (letters < 4)
                    for (int q = k; q < j; q++) if (dim[q] == true) dim[q] = false;
                k = j;
            }
            if (!dim.Any(d => d == true))
            {
                kept.Add(l);
                continue;
            }
            int start = -1;
            void Flush(int end)   // characters start..end inclusive
            {
                if (start < 0) return;
                while (start <= end && char.IsWhiteSpace(l.Text[start])) start++;
                while (end >= start && char.IsWhiteSpace(l.Text[end])) end--;
                // a run is the tooltip's only if it is clearly bright as a whole: see-through text
                // has the odd bright pixel, never a bright word
                var run = Enumerable.Range(start, Math.Max(0, end - start + 1)).Where(k => dim[k] != null)
                                    .Select(k => level[k]).OrderBy(v => v).ToList();
                if (end >= start && l.Text.Substring(start, end - start + 1).Count(char.IsLetterOrDigit) >= 2
                    && run.Count > 0 && run[run.Count / 2] >= cut * 1.15)
                {
                    var seg = Box.FromLTRB(xs[start] - cw * 0.5, l.Box.Y, xs[end] + cw * 0.5, l.Box.Bottom);
                    kept.Add(new TextLine(l.Text.Substring(start, end - start + 1), seg, l.Confidence,
                                          xs[start..(end + 1)]));
                }
                start = -1;
            }
            for (int k = 0; k < l.Text.Length; k++)
            {
                if (dim[k] == true) { Flush(k - 1); continue; }
                if (start < 0 && dim[k] == false) start = k;
            }
            Flush(l.Text.Length - 1);
            t.Debug.Add((l.Box, "rejected", $"partly seen through the tooltip: {l.Text}"));
        }
        return kept;
    }

    /// <summary>
    /// A tooltip's lines within the reach of its side border. The detector often finds a tooltip's
    /// right border as one long edge even where its top and bottom are broken by text; lines past
    /// either end of it - the caption of the card beneath, set on the same margin - are the window
    /// around the tooltip. Only when the border holds most of the lines.
    /// </summary>
    private static List<TextLine> WithinSideBorder(UiGeometry geo, List<TextLine> lines, double h, AccessibleTarget t)
    {
        if (lines.Count < 4) return lines;
        var rights = lines.Select(l => l.Box.Right).OrderBy(v => v).ToList();
        double from = rights[(int)(rights.Count * 0.8)] - h * 0.5, to = rights[^1] + h * 4;
        double top = lines.Min(l => l.Box.Y), bottom = lines.Max(l => l.Box.Bottom);
        // the border is broken where bright lettering meets it: pieces in one column, close together, are one edge
        var pieces = geo.Vertical.Where(v => v.Pos >= from && v.Pos <= to && v.From < bottom && v.To > top)
                                 .OrderBy(v => v.Pos).ThenBy(v => v.From).ToList();
        var merged = new List<Segment>();
        foreach (var v in pieces)
        {
            var i = merged.FindIndex(m => Math.Abs(m.Pos - v.Pos) <= 2 && v.From - m.To <= h * 6 && m.From - v.To <= h * 6);
            if (i < 0) merged.Add(v);
            else merged[i] = merged[i] with { From = Math.Min(merged[i].From, v.From), To = Math.Max(merged[i].To, v.To) };
        }
        // (right of every line: an edge the text crosses is the card behind, not the tooltip's border)
        var side = merged.Where(v => v.Length >= h * 4 && v.Pos >= rights[^1] - h * 0.3)
                         .OrderByDescending(v => v.Length).FirstOrDefault();
        if (side.Length <= 0) return lines;
        // only below it: a product card's caption under a Gem Store tooltip (the border is often
        // found only from below the title, so nothing is cut above it)
        bool Within(TextLine l) => l.Box.CentreY <= side.To + h * 0.3;
        var held = lines.Count(Within);
        if (held == lines.Count || held < lines.Count * 0.6) return lines;
        foreach (var l in lines.Where(l => !Within(l)))
            t.Debug.Add((l.Box, "rejected", $"beyond the tooltip's side border: {l.Text}"));
        return lines.Where(Within).ToList();
    }

    /// <summary>A tooltip block's panel read cleanly: the block's lines inside it, anything else
    /// wholly inside it on its margin, and its lines that run out past its border re-read inside it.</summary>
    private List<TextLine> ReadPanel(Mat picture, List<TextLine> lines, LineBlock b, Box bounds, Box panel, double h)
    {
        var inside = new List<TextLine>(b.Lines.Where(l => l.Box.CoveredBy(panel.Inflate(h * 0.2, h * 0.1)) >= 0.92));
        var redo = new List<Box>();
        foreach (var l in b.Lines.Where(l => !inside.Contains(l)))
            redo.Add(l.Box.Intersect(panel));
        foreach (var l in lines)
        {
            if (b.Lines.Contains(l)) continue;
            var cov = l.Box.CoveredBy(panel);
            // Tooltips are translucent: a neighbouring card's price or caption shows through
            // them. Only a line set on the tooltip's own margin (or indented by an icon) is
            // the tooltip's; anything else inside the panel is the window behind it.
            var onMargin = l.Box.X >= b.Left - h * 0.7 && l.Box.X <= b.Left + h * 3.2
                           // a bare figure inside the panel is a price behind it, not tooltip text
                           && !(TextLines.IsNumeric(l.Text) && !TextLines.IsFraction(l.Text) && TextLines.Letters(l.Text) == 0);
            // Lines of the tooltip's own title sit above its body block (the title is set
            // apart by its icon and larger type).
            var above = TextLines.Letters(l.Text) >= 3 ? h * 2.6 : h * 0.3;
            var withinHeight = l.Box.CentreY >= bounds.Y - above && l.Box.CentreY <= panel.Bottom + h * 0.2;
            if (cov >= 0.95 && onMargin && withinHeight) inside.Add(l);
            // A line the detector ran in from underneath the panel ("Abyss Stalker Tor" +
            // "Skin Locked"): its part inside the panel, on the panel's margin, is tooltip text.
            else if (cov >= 0.25 && cov < 0.95 && l.Box.X < b.Left - h * 0.3
                     && l.Box.CentreY > bounds.Y && l.Box.CentreY < bounds.Bottom + h)
            {
                var part = l.Box.Intersect(Box.FromLTRB(b.Left - h * 0.3, panel.Y, panel.Right, panel.Bottom));
                if (!part.IsEmpty && part.W > h * 1.5) redo.Add(part);
            }
        }
        if (redo.Count > 0)
            inside.AddRange(_ocr.Recognize(picture, redo.Where(r => !r.IsEmpty && r.W > h * 0.6).ToList(), h)
                                .Where(l => l.Text.Length > 0 && l.Confidence >= 45));
        inside = TextLines.MergeSplit(inside)
                          // debris: a lone letter read off an icon, a single stray digit
                          .Where(l => TextLines.Letters(l.Text) >= 2 || l.Text.Count(char.IsDigit) >= 2)
                          .OrderBy(l => l.Box.CentreY).ThenBy(l => l.Box.X).ToList();
        return inside;
    }

    /// <summary>
    /// The frame of a tooltip whose text block spills out of it: a top and a bottom border of
    /// the same extent (within 0.6 line heights at each end), holding most of the block's
    /// tooltip-worded lines, with the block reaching well past it. Null when there is no such
    /// frame, or when the block already fits inside its frame.
    /// </summary>
    private static Box? FramedPanel(UiGeometry geo, LineBlock b, IReadOnlyList<TextLine> all, double h)
    {
        var markers = b.Lines.Where(l => TooltipMarkers.Any(m => l.Text.Contains(m, StringComparison.OrdinalIgnoreCase))).ToList();
        if (markers.Count < 2) return null;
        double top = markers.Min(l => l.Box.Y), bottom = markers.Max(l => l.Box.Bottom);
        var edges = geo.Horizontal.Where(e => e.Length >= h * 5).ToList();
        Box? best = null;
        int bestHeld = 0;
        foreach (var a in edges)
        {
            if (a.Pos > top + h * 0.2 || a.Pos < top - h * 5) continue;
            foreach (var z in edges)
            {
                if (z.Pos < bottom - h * 0.2 || z.Pos > bottom + h * 3) continue;
                // Both borders span the same width. Text drawn over one of them can break it short
                // at one end; that end is then taken from the other border, if a side edge stands there.
                var leftMatch = Math.Abs(a.From - z.From) <= h * 0.6;
                var rightMatch = Math.Abs(a.To - z.To) <= h * 0.6;
                if (!leftMatch && !rightMatch) continue;
                double l0 = leftMatch ? (a.From + z.From) / 2 : Math.Min(a.From, z.From);
                double r0 = rightMatch ? (a.To + z.To) / 2 : Math.Max(a.To, z.To);
                if (!leftMatch && !SideEdge(geo, l0, a.Pos, z.Pos, h)) continue;
                if (!rightMatch && !SideEdge(geo, r0, a.Pos, z.Pos, h)) continue;
                var f = Box.FromLTRB(l0, a.Pos, r0, z.Pos);
                if (f.H < h * 2 || f.W < h * 5) continue;
                var held = markers.Count(l => l.Box.CentreY > f.Y && l.Box.CentreY < f.Bottom
                                              && Math.Min(l.Box.Right, f.Right) - Math.Max(l.Box.X, f.X)
                                                 >= Math.Min(l.Box.W, h * 3) * 0.6);
                if (held < Math.Max(2, markers.Count * 0.7)) continue;
                // A tooltip's frame hugs its text: lines of it start just inside its left border
                // (a window's panel around the tooltip and the list beside it is no tooltip frame),
                // and none of the lines set on that margin runs out past its right border.
                var onMargin = all.Where(l => l.Box.X >= f.X - h * 0.3 && l.Box.X <= f.X + h * 1.2
                                              && l.Box.CentreY > f.Y && l.Box.CentreY < f.Bottom
                                              && TextLines.Letters(l.Text) >= 3).ToList();
                if (onMargin.Count < 2 || onMargin.Any(l => l.Box.Right > f.Right + h * 0.5)) continue;
                if (best == null || held > bestHeld || held == bestHeld && f.Area < best.Value.Area)
                {
                    best = f;
                    bestHeld = held;
                }
            }
        }
        if (best is not Box frame) return null;
        var block = b.Bounds;
        var spills = block.X < frame.X - h * 0.8 || block.Right > frame.Right + h * 0.8
                     || block.Y < frame.Y - h * 0.8 || block.Bottom > frame.Bottom + h * 0.8;
        // ...or the frame holds a line of its own the block does not: its title, set apart from the
        // body by the icon beside it, which text showing through has pushed into a block of its own.
        var missing = all.Any(l => !b.Lines.Contains(l) && TextLines.Letters(l.Text) >= 3
                                   && l.Box.CoveredBy(frame) >= 0.9 && l.Box.Y < block.Y
                                   && l.Box.X >= frame.X - h * 0.3 && l.Box.X <= frame.X + h * 3.6);
        return spills || missing ? frame : null;
    }

    /// <summary>Is there a vertical edge at <paramref name="x"/> along at least 40% of the span?</summary>
    private static bool SideEdge(UiGeometry geo, double x, double top, double bottom, double h)
    {
        var cover = geo.Vertical.Where(v => Math.Abs(v.Pos - x) <= h * 0.4)
                                .Sum(v => Math.Max(0, Math.Min(v.To, bottom) - Math.Max(v.From, top)));
        return cover >= (bottom - top) * 0.4;
    }

    /// <summary>The text inside a tooltip's frame, read on its own: the frame is copied onto a pad
    /// of its own tone, so nothing outside it can run into a line.</summary>
    private List<TextLine> ReadFramed(Mat picture, Box frame, double h)
    {
        var inner = frame.Inflate(-2, -2).Clip(new Box(0, 0, picture.Width, picture.Height));
        int ix = (int)Math.Ceiling(inner.X), iy = (int)Math.Ceiling(inner.Y);
        int iw = (int)Math.Floor(inner.Right) - ix, ih = (int)Math.Floor(inner.Bottom) - iy;
        if (iw < h * 2 || ih < h) return new List<TextLine>();
        int pad = (int)Math.Ceiling(h * 0.6);
        using var src = new Mat(picture, new OpenCvSharp.Rect(ix, iy, iw, ih));
        using var pane = new Mat(ih + 2 * pad, iw + 2 * pad, picture.Type(), Cv2.Mean(src));
        using (var dst = new Mat(pane, new OpenCvSharp.Rect(pad, pad, iw, ih))) src.CopyTo(dst);
        var boxes = MergeBoxes(_ocr.Detect(pane, new Box(0, 0, pane.Width, pane.Height), _detScale));
        double dx = ix - pad, dy = iy - pad;
        return TextLines.MergeSplit(_ocr.Recognize(pane, boxes, h)
                                        .Where(l => l.Text.Trim().Length > 0 && l.Confidence >= 45)
                                        .SelectMany(TextLines.SplitAtGaps))
                        .Select(l => l with
                        {
                            Text = TextLines.Clean(l.Text),
                            Box = l.Box.Offset(dx, dy),
                            CharX = l.CharX?.Select(v => v + dx).ToArray(),
                        })
                        .Where(l => l.Text.Length > 0)
                        .ToList();
    }

    private static double Dist(double ax, double ay, double bx, double by) =>
        Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    /// <summary>The tooltip's first line is its title unless it is already prose.</summary>
    private static string? TitleOf(List<TextLine> lines, double h)
    {
        // A sale badge or a figure may be read before the name ("20% Off!", a price seen through
        // the panel); the title is the first line among the top ones that is a name.
        var top = lines[0].Box.Y;
        var tops = lines.Where(l => l.Box.Y <= top + h * 1.6 && TextLines.Letters(l.Text) >= 3
                                    && !Chrome.IsMatch(l.Text) && !Notice.IsMatch(l.Text) && !TextLines.IsNumeric(l.Text)
                                    && !BracketPrice.IsMatch(l.Text))
                        .OrderBy(l => l.Box.CentreY).ToList();
        var first = tops.FirstOrDefault();
        if (first.Text == null) return null;
        // "2 Hound of Balthazar Loot Boxes" is six words and still a name. A title does not end
        // in a full stop and is not a long run of words.
        var t = first.Text.TrimEnd();
        if (t.EndsWith('.') || t.Contains(". ") || t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 9) return null;
        // a line that starts mid-sentence, or whose sentence carries on into the next line, is prose
        if (t.Length > 0 && char.IsLower(t[0])) return null;
        if (lines.Count > 1 && lines[1].Text.TrimStart() is { Length: > 0 } next && char.IsLower(next[0])
            && lines[1].Box.Y - first.Box.Bottom < h * 0.6) return null;
        if (first.Text.StartsWith("Double-click", StringComparison.OrdinalIgnoreCase)) return null;
        return first.Text;
    }

    /// <summary>Lines of the window with the tooltip's panel cut out of them.</summary>
    private List<TextLine> OutsidePanel(Mat picture, List<TextLine> lines, Box panel, double h)
    {
        var keep = new List<TextLine>();
        var redo = new List<Box>();
        var cover = panel.Inflate(h * 0.15, h * 0.05);
        foreach (var l in lines)
        {
            var share = l.Box.CoveredBy(cover);
            // A tooltip is translucent: text only partly under its edge is usually still legible.
            if (share <= 0.45) { keep.Add(l); continue; }
            if (share >= 0.85) continue;
            // Straddles the border: keep only the part outside, read again.
            var left = Box.FromLTRB(l.Box.X, l.Box.Y, Math.Min(l.Box.Right, cover.X), l.Box.Bottom);
            var right = Box.FromLTRB(Math.Max(l.Box.X, cover.Right), l.Box.Y, l.Box.Right, l.Box.Bottom);
            if (left.W > h * 1.2) redo.Add(left);
            else if (right.W > h * 1.2) redo.Add(right);
        }
        if (redo.Count > 0)
            keep.AddRange(_ocr.Recognize(picture, redo, h).Where(l => l.Text.Length > 0 && l.Confidence >= 45));
        return keep;
    }

    /// <summary>Is this tooltip about this object? Titles decide when both have a name.</summary>
    private static bool Belongs(Tooltip tip, AccessibleTarget t, double x, double y, double h)
    {
        if (tip.Title == null || string.IsNullOrWhiteSpace(t.Name))
            return tip.AnchorDistance <= 3.5;
        var (bare, _) = TextLines.SplitCount(tip.Title);
        if (TextLines.Similarity(bare, t.Name) >= 0.7) return true;
        // The title can be lost - run together with window text showing through the panel - but
        // a skin's tooltip names the item again on a line of its own ("Skin Unlocked" /
        // "Unbound Magic Mining Beam"). A tooltip right at the pointer that does is about it.
        return tip.AtPointer && t.Name.Length >= 8
               && tip.Lines.Any(l => TextLines.Similarity(l.Text, t.Name) >= 0.85);
    }

    // =====================================================================================
    // Direct target
    // =====================================================================================

    private static bool TryButton(UiGeometry geo, List<TextLine> window, List<TextLine> nearPointer, double x, double y, double h, AccessibleTarget t)
    {
        var rects = geo.RectanglesAround(x, y, h * 1.4, h * 0.8, h * 14, h * 2.6);
        foreach (var r in rects)
        {
            var inside = window.Where(l => r.Box.Contains(l.Box.CentreX, l.Box.CentreY)).ToList();
            if (inside.Count != 1) continue;
            var l = inside[0];
            var words = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 4 || TextLines.Letters(l.Text) < 2) continue;
            if (Math.Abs(l.Box.CentreX - r.Box.CentreX) > r.Box.W * 0.22) continue;
            if (Math.Abs(l.Box.CentreY - r.Box.CentreY) > r.Box.H * 0.35) continue;
            if (r.Box.W > l.Box.W + h * 7) continue;
            t.Type = TargetType.Button;
            t.Name = l.Text;
            t.TargetBounds = r.Box;
            t.Confidence = 0.8;
            t.Reasons.Add($"button {r.Box} ({r.Why})");
            return true;
        }

        // A filled button: a short label with the edges of its box close around it on every side.
        foreach (var l in window.Concat(nearPointer).Where(l => l.Box.Inflate(h * 3.5, h * 0.9).Contains(x, y)))
        {
            var words = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 3 || TextLines.Letters(l.Text) < 3 || TextLines.IsNumeric(l.Text) || l.Text.TrimEnd().EndsWith('.')) continue;
            // A card's caption plate is a box around a name too - but a caption has its price
            // beside it, and a button does not.
            var priced = window.Any(p => TextLines.IsNumeric(p.Text) && !TextLines.IsFraction(p.Text)
                                         && Math.Abs(p.Box.CentreY - l.Box.CentreY) < h * 2.4
                                         && Math.Abs(p.Box.CentreX - l.Box.CentreX) < Math.Max(l.Box.W, h * 3));
            if (priced) continue;
            var (box, sides) = geo.PanelAround(l.Box);
            // Faint text read again at the pointer, or a box found only by the close search, has
            // to look like a button on its own: wide (an inventory slot or a card's picture has
            // four edges too, but is square), with words on it rather than artwork.
            bool mustProve = nearPointer.Contains(l);
            double slackY = h * 0.15;
            if (sides < 4 || box.H > l.Box.H * 2.4)
            {
                // The box's top and bottom edges can lie INSIDE the detector's box for the label,
                // and the general search then finds the next edges out (a row of icons above).
                var tight = geo.ButtonAround(l.Box);
                if (tight == null) continue;
                box = tight.Value;
                // letters' own strokes are not the box: it must clear most of their height
                if (box.H < Math.Min(l.Box.H, h * 1.2) * 0.7) continue;
                mustProve = true;
                // the game answers a hover a few pixels outside a button's drawn edge
                slackY = h * 0.3;
            }
            if (mustProve && (box.W < box.H * 2.2 || !HoverText.LooksLikeWords(l.Text))) continue;
            if (box.H > l.Box.H * 2.4 || box.W > l.Box.W + h * 7) continue;
            if (!box.Inflate(h * 0.15, slackY).Contains(x, y)) continue;
            // A button holds its one label. Other lines running into the box ("Tickets for New" over
            // "Weapons from the" on a banner) make it a paragraph set on artwork, not a button.
            var label = l;
            if (window.Any(o => o != label && TextLines.Letters(o.Text) >= 2 && o.Box.CoveredBy(box) > 0.25)) continue;
            t.Type = TargetType.Button;
            t.Name = l.Text;
            t.TargetBounds = box;
            t.Confidence = 0.7;
            t.Reasons.Add($"filled button {box} around '{l.Text}'");
            return true;
        }
        return false;
    }

    private bool TryList(Mat picture, UiGeometry geo, List<TextLine> window, double x, double y, double h,
                         string context, Tooltip? tip, AccessibleTarget t)
    {
        // Figures under a name belong to the name's row; they are not rows themselves.
        var lists = TextLines.Lists(window.Where(l => !TextLines.IsNumeric(l.Text) && !Chrome.IsMatch(l.Text)
                                                      && !Availability.IsMatch(l.Text) && !WindowTitle.IsMatch(l.Text)
                                                      && l.Box.H < h * 1.5).ToList(), h);
        // Two lists side by side (trading post categories and results): the one whose names
        // are nearest the pointer across the screen, bounded on the right by the next list.
        // Of lists that could hold the pointer, the one with most rows first (a real list is long;
        // a coincidental alignment is short), then the nearest across the screen.
        foreach (var list in lists.OrderByDescending(l => l.Names.Count >= 5 ? 5 : l.Names.Count)
                                  .ThenBy(l => Math.Max(0, l.Left - x) + Math.Max(0, x - l.Names.Max(n => n.Box.Right)) * 0.5))
        {
            t.Reasons.Add($"list? {list.Names.Count} names from '{list.Names[0].Text}' pitch {list.Pitch / h:F1}h left {list.Left:F0} top {list.Top:F0} bottom {list.Bottom:F0}");
            if (y < list.Top || y > list.Bottom) continue;
            var left = list.Left - h * 3.4;
            // (only a list beside the pointer's own row: a tooltip beside the list - an equipped item's
            // comparison panel - that ends above the row is not the next list, and the row's price
            // may sit underneath it)
            var nextList = lists.Where(o => o != list && o.Left > list.Left + h * 2 && o.Top < list.Bottom && o.Bottom > list.Top
                                            && o.Top - list.Pitch <= y && o.Bottom + list.Pitch >= y
                                            && !o.Names.Any(n => n.Text.Contains("Currently Equipped", StringComparison.OrdinalIgnoreCase)))
                                .Select(o => o.Left - h * 3.4).DefaultIfEmpty(double.MaxValue).Min();
            // Left of the names, beside a small label of its own (a tab icon's "Novelties"): the
            // pointer is on that icon, not on the row across from it.
            if (x < list.Left - h * 1.2 && HoverLabelNear(window, x, y, h).Text is string own
                && list.Names.All(n => Math.Abs(n.Box.X - list.Left) > h * 0.8 || n.Text != own))
            {
                t.Reasons.Add($"pointer left of the list, beside its own label '{own}'");
                continue;
            }
            var right = window.Where(l => l.Box.CentreY >= list.Top && l.Box.CentreY <= list.Bottom
                                          && l.Box.X >= list.Left - h && l.Box.X < Math.Min(list.Left + h * 42, nextList))
                              .Select(l => l.Box.Right).DefaultIfEmpty(list.Left + h * 8).Max() + h * 1.5;
            right = Math.Min(right, nextList);
            if (x < left || x > right) continue;

            // The row whose band holds the pointer. A row is centred on its content, and some rows'
            // content hangs below the name - an objective's title with its progress bar and "1/3"
            // underneath - so the band is centred on the name plus whatever sits under it.
            var ordered = list.Names.OrderBy(n => n.Box.CentreY).ToList();
            // Only progress figures ("1/3") hang under a name; a stack count on the next row's
            // icon ("45") is the next row's, not this one's.
            var hangs = ordered.Select(n => window.Where(l => TextLines.IsFraction(l.Text) && l.Box.Y > n.Box.CentreY
                                                             && l.Box.CentreY < n.Box.CentreY + list.Pitch * 0.62
                                                             && l.Box.X >= n.Box.X - h && l.Box.X <= n.Box.X + h * 3)
                                                  .Select(l => l.Box.Bottom).DefaultIfEmpty(n.Box.Bottom).Max() - n.Box.Bottom)
                               .OrderBy(v => v).ToList();
            var drop = hangs[hangs.Count / 2] / 2;          // how far the row's centre sits below its name's centre
            double Centre(TextLine n) => n.Box.CentreY + drop;
            int idx = 0; double bestD = double.MaxValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                var d = Math.Abs(Centre(ordered[i]) - y);
                if (d < bestD) { bestD = d; idx = i; }
            }

            // Near a boundary, the game has already told us: the hovered row is drawn lighter.
            var bandTop = Centre(ordered[idx]) - list.Pitch / 2;
            var bandBottom = Centre(ordered[idx]) + list.Pitch / 2;
            var toBoundary = Math.Min(y - bandTop, bandBottom - y);
            if (toBoundary < list.Pitch * 0.22)
            {
                int other = y < Centre(ordered[idx]) ? idx - 1 : idx + 1;
                if (other >= 0 && other < ordered.Count)
                {
                    // Text and the (lighter, translucent) tooltip panel are masked out: only the
                    // row's own background says which row the game has lit.
                    var textBoxes = window.Select(l => l.Box).ToList();
                    if (tip != null) textBoxes.Add(tip.Panel);
                    // Compare the plain background between the names and the prices: icons at
                    // the left edge look alike in every row and drown out a subtle highlight.
                    var namesRight = ordered.Max(n => n.Box.Right) + h;
                    var figuresLeft = window.Where(l => TextLines.IsNumeric(l.Text) && l.Box.X > namesRight
                                                        && l.Box.CentreY >= list.Top && l.Box.CentreY <= list.Bottom)
                                            .Select(l => l.Box.X - h).DefaultIfEmpty(right - h).Min();
                    // (Measured: comparing only the plain middle made k1-e16 worse and fixed nothing,
                    // so the whole row is compared.)
                    double bx0 = left + h, bx1 = right - h;
                    double Bright(int i) => geo.BandBrightness(Centre(ordered[i]) - list.Pitch * 0.42,
                                                               Centre(ordered[i]) + list.Pitch * 0.42,
                                                               bx0, bx1, textBoxes);
                    var bi = Bright(idx); var bo = Bright(other);
                    t.Reasons.Add($"row boundary: band {bi:F0} vs neighbour {bo:F0}");
                    if (bo > bi + 5) idx = other;
                }
            }

            // A tooltip anchored right here names its row: trust it over the arithmetic.
            if (tip is { Title: not null, AtPointer: true })
            {
                var (bare, _) = TextLines.SplitCount(tip.Title);
                if (TextLines.Similarity(bare, ordered[idx].Text) < 0.7)
                    for (int d = -1; d <= 1; d += 2)
                    {
                        int j = idx + d;
                        if (j >= 0 && j < ordered.Count && TextLines.Similarity(bare, ordered[j].Text) >= 0.8)
                        {
                            t.Reasons.Add($"tooltip names the neighbouring row '{ordered[j].Text}'");
                            idx = j;
                            break;
                        }
                    }
            }

            var name = ordered[idx];
            var top = Centre(name) - list.Pitch / 2;
            var bottom = Centre(name) + list.Pitch / 2;

            // A GRID of labelled icons is not a list: another label on the same line further
            // right is the next column, and the row ends before it.
            var nextColumn = window.Where(l => TextLines.Letters(l.Text) >= 4 && !TextLines.IsNumeric(l.Text)
                                               && l.Box.VerticalOverlap(name.Box) > 0.5 && l.Box.X > name.Box.Right + h * 2)
                                   .Select(l => l.Box.X - h).DefaultIfEmpty(double.MaxValue).Min();
            if (nextColumn < right)
            {
                if (x > nextColumn) { t.Reasons.Add($"'{name.Text}' is one column of a grid - the pointer is in another"); continue; }
                right = nextColumn;
            }

            // A grid of product cards looks like a list of names, but a card's price sits on its
            // own line under the name, where a merchant row's price shares the name's line.
            var sameLinePrice = window.Any(l => TextLines.IsNumeric(l.Text) && !TextLines.IsFraction(l.Text)
                                               && l.Box.X > name.Box.Right && l.Box.VerticalOverlap(name.Box) > 0.5);
            var priceBelow = window.Any(l => TextLines.IsNumeric(l.Text) && !TextLines.IsFraction(l.Text)
                                            && l.Box.Y > name.Box.CentreY && l.Box.Y - name.Box.Bottom < h * 1.2
                                            && Math.Abs(l.Box.X - name.Box.X) < h * 3);
            if (!sameLinePrice && priceBelow)
            {
                t.Reasons.Add($"'{name.Text}' has its price beneath it - a card, not a list row");
                continue;
            }
            var row = TextLines.RowContents(window, name, top, bottom, left, right);
            t.TargetBounds = Box.FromLTRB(left, top, right, bottom);
            t.Name = name.Text;
            t.Debug.Add((t.TargetBounds, "row", "row"));

            // Price: the figures at the right end of the row.
            var figures = row.Where(l => l.Box.X > name.Box.Right && TextLines.IsNumeric(l.Text)).ToList();
            // The Trading Post's Level column ("0") is a bare figure too: a figure under the Level
            // heading is the item's level, never its price.
            if (context == "tradingpost"
                && window.FirstOrDefault(l => LevelHeading.IsMatch(l.Text) && l.Box.Bottom < name.Box.Y) is { Text: not null } level)
                figures = figures.Where(f => Math.Abs(f.Box.CentreX - level.Box.CentreX) > h * 2).ToList();
            var listHasPrices = window.Count(l => TextLines.IsNumeric(l.Text) && !TextLines.IsFraction(l.Text)
                                                  && l.Box.CentreY >= list.Top && l.Box.CentreY <= list.Bottom
                                                  && l.Box.X > list.Left + h * 4 && l.Box.X < right) >= 2;
            if (figures.Count == 0 && (listHasPrices || context is "vendor" or "tradingpost"))
            {
                // The detector passes over a lone "2" or "20" beside a coin more often than over
                // a word. The row is known now, so look at just its price column, closely.
                var zone = Box.FromLTRB(Math.Max(name.Box.Right + h, right - h * 12), top, right, bottom);
                var again = _ocr.Read(picture, zone, Math.Clamp(60.0 / Math.Max(8, h), 1.5, 4.0))
                                .Where(l => TextLines.IsNumeric(l.Text)).ToList();
                figures = again.Count == 0 ? figures
                    : _ocr.Recognize(picture, again.Select(a => a.Box).ToList(), h)
                          .Where(l => TextLines.IsNumeric(l.Text))
                          // a figure is followed by its coin or currency; an icon read as a digit is not
                          .Where(l => IconAfter(geo, l, name, h))
                          .ToList();
                t.Reasons.Add($"price column re-read: {string.Join(" | ", figures.Select(f => f.Text))}");
            }
            if (figures.Count == 0)
            {
                // Still nothing: a lone figure beside an icon at the end of the row ("1" and the
                // picture of the tool it costs, "2" and a coin) is below what the detector will box.
                var price = FigureBesideIcon(picture, geo, name, Math.Min(nextList, name.Box.Right + h * 36), tip, h);
                if (price.Text != null)
                {
                    figures = new List<TextLine> { price };
                    t.Reasons.Add($"price beside its icon at the end of the row: {price.Text}");
                }
            }
            // All the figures in the price column are one price: "15 [silver] 36 [copper]" may
            // arrive as one line or as two.
            // Only figures on the name's own line: a price never borrows a figure from the row
            // below, however close the pointer is to the boundary.
            var priceParts = figures.Where(f => !TextLines.IsFraction(f.Text)).OrderBy(f => f.Box.X).ToList();
            var onLine = priceParts.Where(f => f.Box.VerticalOverlap(name.Box) >= 0.4).ToList();
            if (onLine.Count > 0) priceParts = onLine;
            var priceLine = priceParts.Count == 0 ? default : JoinFigures(priceParts);
            if (priceLine.Text != null && !TextLines.IsFraction(priceLine.Text))
            {
                var reading = PriceReader.Read(picture, priceLine, h, PriceContextFor(context));
                // A Wizard's Vault objective is not for sale: the Astral Acclaim at the end of its
                // row is what completing it earns.
                if (reading != null && context == "vault" && reading.Currency is "astral" or "unknown")
                    t.Details.Add($"Reward: {reading.Spoken}");
                else if (reading != null) t.Price = reading.Spoken;
            }
            foreach (var l in row.Where(l => TextLines.IsFraction(l.Text))) t.Details.Add(l.Text);
            // a progress line directly under the name ("1/3") belongs to the row too
            foreach (var l in window.Where(l => TextLines.IsFraction(l.Text) && l.Box.CentreY > name.Box.Bottom
                                               && l.Box.CentreY < bottom + list.Pitch * 0.2
                                               && Math.Abs(l.Box.X - name.Box.X) < h * 1.5
                                               && !t.Details.Contains(l.Text)))
                t.Details.Add(l.Text);

            t.Type = t.Price != null
                ? (context == "tradingpost" ? TargetType.TradingPostCard : TargetType.MerchantHorizontalItem)
                : context == "tradingpost" && figures.Count > 0 ? TargetType.TradingPostCard : TargetType.MenuRow;
            t.Confidence = 0.75;
            t.Reasons.Add($"list row {idx + 1}/{ordered.Count} '{name.Text}' pitch {list.Pitch / h:F1}h price '{t.Price}'");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Read a figure again, closely. A single-currency price ("180" beside a crystal, "560"
    /// beside a gem) loses a digit to the icon next to it about one time in ten when read from
    /// its detector box alone - "180" becomes "18", a wrong price said with confidence. The
    /// full detector run over a small area around it, enlarged, reads it whole.
    /// </summary>
    private TextLine ReadFigure(Mat picture, TextLine line, double h)
    {
        var zone = line.Box.Inflate(h * 0.9, h * 0.45);
        var got = _ocr.Read(picture, zone, Math.Clamp(64.0 / Math.Max(8, line.Box.H), 1.5, 4.0))
                      .Where(l => TextLines.IsNumeric(l.Text))
                      .OrderByDescending(l => l.Box.Intersect(line.Box).Area)
                      .FirstOrDefault();
        if (got.Text == null || got.Box.Intersect(line.Box).Area <= 0) return line;
        int Digits(string s) => s.Count(char.IsDigit);
        // keep the longer reading of the same number; never swap in a different number
        var a = new string(line.Text.Where(char.IsDigit).ToArray());
        var b = new string(got.Text.Where(char.IsDigit).ToArray());
        if (b.Length > a.Length && b.StartsWith(a, StringComparison.Ordinal)) return got with { CharX = null };
        if (b.Length == a.Length) return line;
        return Digits(got.Text) > Digits(line.Text) && a.Length == 0 ? got : line;
    }

    /// <summary>Several figure boxes on one row as one line, keeping each character's position.</summary>
    private static TextLine JoinFigures(List<TextLine> parts)
    {
        if (parts.Count == 1) return parts[0];
        var text = new System.Text.StringBuilder();
        var xs = new List<double>();
        bool haveX = parts.All(p => p.CharX != null && p.CharX.Length == p.Text.Length);
        foreach (var p in parts)
        {
            if (text.Length > 0) { text.Append(' '); xs.Add(p.Box.X); }
            text.Append(p.Text);
            if (haveX) xs.AddRange(p.CharX!);
            else for (int i = 0; i < p.Text.Length; i++) xs.Add(p.Box.X + (i + 0.5) / p.Text.Length * p.Box.W);
        }
        return new TextLine(text.ToString(), Box.Bounding(parts.Select(p => p.Box)), parts.Average(p => p.Confidence), xs.ToArray());
    }

    private sealed record Caption(TextLine Price, List<TextLine> Names, List<TextLine> Extra, Box Bounds)
    {
        public string Name => StripChrome(string.Join(" ", Names.Select(n => n.Text)));
    }

    private static readonly Regex LevelHeading = new(@"^\W*Level\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ViewDetailsPart = new(@"V\s*i?\s*e\s*w\s+D\s*e\s*t", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ViewDetails = new(@"V\s*i\s*e?\s*w\s*D\s*e\s*t\s*a?\s*i?\s*l\s*s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Chrome = new(@"^\W*(View Details|Buy Gems|Redeem Code|New!?|Sale Ending Soon!?|Limited ti\S* left at this price!?|\d+\s*%\s*Off\s*!?|Featured Items|New Items|Promotions|Search\.*)\W*$",
                                               RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripChrome(string s) =>
        // (and the lone full stop left where the label covered the words: "Code of Creation . Drop")
        Regex.Replace(Regex.Replace(ViewDetails.Replace(s, " "), @"\s\.(?=\s)", " "), @"\s{2,}", " ").Trim();

    /// <summary>A name, not a name run together with a notice or a price seen through the panel.</summary>
    private static bool IsCleanName(string s) =>
        !Regex.IsMatch(s, @"(Limited ti|% ?Off|View Det|Available|Sold Out|\d{2,})", RegexOptions.IgnoreCase)
        && TextLines.Letters(s) >= 4 && !s.Contains("! ");

    /// <summary>The Gem Store's notices, however badly read: never an item's name.</summary>
    private static readonly Regex Notice = new(@"^\W*(Limited\s*ti|Sale\s*End|\d+\s*%\s*O\w{1,2}\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NewBadge = new(@"^\W*New\W*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SaleBadge = new(@"\d+\s*%\s*Off", RegexOptions.IgnoreCase);

    private static readonly Regex BracketPrice = new(@"^\W*\d[\d,\.]*\s*(gems?|astral acclaim|karma)?\W*$",
                                                     RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Availability = new(@"^\s*(\d+\s*Avail|Sold\s*Out|\d+\s*for\s*\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool TryCard(Mat picture, List<TextLine> window, double x, double y, double h, string context,
                         Tooltip? tip, AccessibleTarget t)
    {
        // A tooltip at the pointer that is not itself a caption names the card under the pointer
        // better than the nearest caption does - the card's own caption is often hidden under it.
        if (tip is { Title: not null, AtPointer: true } && TryCardByTooltip(picture, window, x, y, h, context, tip, t))
            return true;
        var captions = FindCaptions(window, h);
        if (captions.Count == 0) return false;
        foreach (var c in captions) t.Debug.Add((c.Bounds, "row", "caption " + c.Name));

        Caption? chosen = null; double bestCost = double.MaxValue;
        foreach (var c in captions)
        {
            // Cards in a row share the space between their captions: each card reaches halfway
            // to its neighbours, so pointing at the edge of a card's picture still finds it.
            var row = captions.Where(o => Math.Abs(o.Price.Box.CentreY - c.Price.Box.CentreY) < h).OrderBy(o => o.Bounds.CentreX).ToList();
            int k = row.IndexOf(c);
            // The grid's pitch is the SMALLEST step between neighbouring captions: a larger step
            // means a card between them whose caption is hidden (usually under the tooltip).
            double halfPitch = row.Count > 1
                ? row.Zip(row.Skip(1), (a, b) => b.Bounds.CentreX - a.Bounds.CentreX).Min() / 2 : c.Bounds.W / 2 + h * 2.2;
            double leftEdge = k > 0 ? Math.Max((row[k - 1].Bounds.CentreX + c.Bounds.CentreX) / 2, c.Bounds.CentreX - halfPitch)
                                    : c.Bounds.CentreX - halfPitch;
            double rightEdge = k < row.Count - 1 ? Math.Min((row[k + 1].Bounds.CentreX + c.Bounds.CentreX) / 2, c.Bounds.CentreX + halfPitch)
                                                 : c.Bounds.CentreX + halfPitch;
            var span = Box.FromLTRB(Math.Min(leftEdge, c.Bounds.X - h * 2.2), c.Bounds.Y, Math.Max(rightEdge, c.Bounds.Right + h * 2.2), c.Bounds.Bottom);
            double cost;
            if (span.X <= x && x <= span.Right)
            {
                if (y <= c.Bounds.Bottom + h * 2.5 && y >= c.Bounds.Y - h * 15)
                    cost = Math.Max(0, c.Bounds.Y - y) + (y > c.Bounds.Bottom ? (y - c.Bounds.Bottom) * 2 : 0);
                else continue;
                // A card's artwork above its caption holds no words. Other text between the
                // pointer and the caption (a tab's label, a filter, a heading) means the pointer
                // is on something else. The card's own badges just above its name do not count.
                if (y < c.Bounds.Y - h * 2.4
                    && window.Any(l => TextLines.Letters(l.Text) >= 2 && !Notice.IsMatch(l.Text) && !NewBadge.IsMatch(l.Text)
                                       && l.Box.CentreY > y - h * 0.5 && l.Box.Bottom < c.Bounds.Y - h * 2.4
                                       && l.Box.Right > c.Bounds.X - h && l.Box.X < c.Bounds.Right + h))
                    continue;
            }
            // Artwork to the left of its caption (the Gem Store's lists). Never in the Wizard's
            // Vault, whose artwork is above its captions: there, a caption to the right of the
            // pointer is the next card's, and its name and price are not this card's.
            else if (context != "vault" && x < c.Bounds.X && c.Bounds.X - x <= h * 11
                     && y >= c.Bounds.Y - h * 3 && y <= c.Bounds.Bottom + h * 3)
            {
                cost = (c.Bounds.X - x) * 1.2 + h * 2;
            }
            else continue;

            // No other caption between the pointer and this one.
            var between = captions.Any(o => !ReferenceEquals(o, c) && o.Bounds.HorizontalOverlap(c.Bounds) > 0.3
                                            && o.Bounds.CentreY > y && o.Bounds.CentreY < c.Bounds.CentreY);
            if (between) continue;
            if (cost < bestCost) { bestCost = cost; chosen = c; }
        }
        if (chosen == null) return false;

        // A tooltip at the pointer that names a different card: the tooltip is right, the caption
        // chosen is the neighbour's (the pointer's own caption is often hidden behind it).
        var name = chosen.Name;
        var price = chosen.Price;
        if (tip is { Title: not null, AtPointer: true } && IsCleanName(tip.Title))
        {
            var (bare, _) = TextLines.SplitCount(tip.Title);
            if (TextLines.Similarity(bare, name) < 0.7)
            {
                var match = captions.FirstOrDefault(c => TextLines.Similarity(bare, c.Name) >= 0.75);
                if (match != null) { chosen = match; name = match.Name; price = match.Price; }
                else { name = bare; price = default; }
                t.Reasons.Add($"tooltip title '{bare}' overrides caption");
            }
            else if (Norm(bare).Length > Norm(name).Length + 2 && Norm(name).Length >= 6 && Norm(bare).Contains(Norm(name)))
            {
                // the caption is partly hidden ("Spider's Selection Box"); the title is all of it
                t.Reasons.Add($"tooltip title '{bare}' completes the caption '{name}'");
                name = bare;
            }
        }

        t.Type = context switch
        {
            "gemstore" => TargetType.GemStoreCard,
            "tradingpost" => TargetType.TradingPostCard,
            _ => TargetType.WizardVaultCard,
        };
        t.Name = name;
        t.TargetBounds = chosen.Bounds;
        if (price.Text != null)
        {
            price = ReadFigure(picture, price, h);
            var reading = PriceReader.Read(picture, price, h, PriceContextFor(context == "none" ? "vault" : context));
            // The currency is the surest sign of which window this is.
            if (reading?.Currency == "gems") t.Type = TargetType.GemStoreCard;
            else if (reading?.Currency == "astral") t.Type = TargetType.WizardVaultCard;
            if (reading != null) t.Price = reading.Spoken;
            // a smaller, dimmer figure right under the price is the pre-sale price
            var old = window.Where(l => TextLines.IsNumeric(l.Text) && l.Box.Y > price.Box.CentreY
                                        && l.Box.Y - price.Box.Bottom < h * 0.9
                                        && Math.Abs(l.Box.CentreX - price.Box.CentreX) < h * 1.5)
                            .OrderBy(l => l.Box.Y).FirstOrDefault();
            // On sale ("20% Off!") the pre-sale price is small, dim and struck through, and the
            // detector often misses it: read the place it is printed.
            if (old.Text == null && window.Any(l => SaleBadge.IsMatch(l.Text) && l.Box.Intersect(chosen.Bounds.Inflate(h * 1.5, h * 2.5)).Area > 0))
            {
                // Too dim and small for the detector: read the spot with the recogniser alone.
                var spot = Box.FromLTRB(price.Box.X - h * 0.1, price.Box.Bottom, price.Box.Right + h * 0.1, price.Box.Bottom + h * 0.8)
                              .Clip(new Box(0, 0, picture.Width, picture.Height));
                var read = spot.IsEmpty ? default : _ocr.Recognize(picture, new[] { spot }, h).FirstOrDefault();
                var digits = read.Text == null ? "" : new string(read.Text.Where(char.IsDigit).ToArray());
                if (digits.Length >= 2 && read.Text!.Count(char.IsLetter) == 0)
                    old = new TextLine(digits, spot, read.Confidence);
                if (old.Text != null) t.Reasons.Add($"pre-sale price read under the price: '{old.Text}'");
            }
            if (old.Text != null && reading != null && reading.Amounts.Count > 0)
            {
                old = ReadFigure(picture, old, h);
                if (reading.Currency is "gems" or "astral")
                {
                    // gems and Astral Acclaim have no denominations: the figure is the whole price
                    var digits = new string(old.Text.Where(char.IsDigit).ToArray());
                    if (long.TryParse(digits, out var was) && was > reading.Amounts[0])
                        t.NormalPrice = was.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                                        + (reading.Currency == "gems" ? " gems" : " Astral Acclaim");
                }
                else
                {
                    var r2 = PriceReader.Read(picture, old, h, PriceContextFor(context == "none" ? "vault" : context));
                    if (r2 != null && r2.Amounts.Count > 0 && r2.Amounts[0] > reading.Amounts[0]) t.NormalPrice = r2.Spoken;
                }
            }
        }
        foreach (var e in chosen.Extra.Where(e => Availability.IsMatch(e.Text))) t.Details.Add(e.Text);
        t.Confidence = 0.7;
        t.Reasons.Add($"card caption '{name}' price '{t.Price}'");
        return true;
    }

    /// <summary>
    /// Captions: a price with a name beside it (above, below or to its right), plus the small
    /// lines that go with it ("3 Available", "Sold Out!", a second line of the name).
    /// </summary>
    private static List<Caption> FindCaptions(List<TextLine> window, double h)
    {
        var captions = new List<Caption>();
        var prices = window.Where(l => TextLines.IsNumeric(l.Text) && !TextLines.IsFraction(l.Text)
                                       && l.Text.Any(char.IsDigit) && l.Box.W < h * 6).ToList();
        foreach (var p in prices)
        {
            var name = window.Where(l => !TextLines.IsNumeric(l.Text) && TextLines.Letters(StripChrome(l.Text)) >= 4
                                         && !Chrome.IsMatch(l.Text)
                                         && !TextLines.IsSentence(l.Text) && !Availability.IsMatch(l.Text)
                                         && Math.Abs(l.Box.CentreY - p.Box.CentreY) <= h * 2.2
                                         && Math.Abs(l.Box.CentreY - p.Box.CentreY) >= h * 0.5
                                         && (l.Box.HorizontalOverlap(p.Box) > 0 || Math.Abs(l.Box.CentreX - p.Box.CentreX) < h * 3.5
                                             || Math.Abs(l.Box.X - p.Box.X) < h * 1.8))
                             .OrderBy(l => Math.Abs(l.Box.CentreY - p.Box.CentreY) + Math.Abs(l.Box.CentreX - p.Box.CentreX) * 0.3)
                             .FirstOrDefault();
            if (name.Text == null) continue;
            if (captions.Any(c => c.Names.Contains(name))) continue;

            var names = new List<TextLine> { name };
            // a second line of the name, directly under it and centred with it
            var second = window.FirstOrDefault(l => !TextLines.IsNumeric(l.Text) && TextLines.Letters(l.Text) >= 3
                                                   && !Availability.IsMatch(l.Text)
                                                   && l.Box.Y > name.Box.CentreY && l.Box.Y - name.Box.Bottom < h * 0.5
                                                   && Math.Abs(l.Box.CentreX - name.Box.CentreX) < h * 1.5
                                                   && !TextLines.IsSentence(l.Text));
            if (second.Text != null && name.Box.Y > p.Box.Y) names.Add(second);
            // ...or the name read so far is the SECOND line, and its first line is above it
            // (price beneath the name, as on Gem Store cards).
            if (name.Box.Y < p.Box.Y)
            {
                var firstLine = window.FirstOrDefault(l => !TextLines.IsNumeric(l.Text) && TextLines.Letters(StripChrome(l.Text)) >= 3
                                                           && !Chrome.IsMatch(l.Text) && !Availability.IsMatch(l.Text)
                                                           && name.Box.Y - l.Box.Bottom < h * 0.5
                                                           && Math.Abs(l.Box.CentreX - name.Box.CentreX) < h * 2
                                                           // (the View Details label run into the line makes its box taller)
                                                           && (l.Box.Bottom <= name.Box.CentreY && Math.Abs(l.Box.H - name.Box.H) < h * 0.4
                                                               || ViewDetails.IsMatch(l.Text) && l.Box.Y < name.Box.Y - h * 0.5));
                if (firstLine.Text != null) names.Insert(0, firstLine);
            }

            var bounds = Box.Bounding(names.Select(n => n.Box).Append(p.Box));
            var extra = window.Where(l => Availability.IsMatch(l.Text)
                                          && Math.Abs(l.Box.CentreX - bounds.CentreX) < h * 3
                                          && l.Box.CentreY > bounds.Y - h * 3.5 && l.Box.CentreY < bounds.Bottom + h * 1.6).ToList();
            bounds = Box.Bounding(extra.Select(e => e.Box).Append(bounds));
            captions.Add(new Caption(p, names, extra, bounds));
        }
        return captions;
    }

    /// <summary>
    /// The pointer is on a reward card's picture and the card's tooltip is showing - often
    /// drawn over the card's own caption. The tooltip names the card; the caption on screen with
    /// that same name, near the pointer, carries its price.
    /// </summary>
    private bool TryCardByTooltip(Mat picture, List<TextLine> window, double x, double y, double h, string context,
                                  Tooltip? tip, AccessibleTarget t)
    {
        if (tip is not { Title: not null, AtPointer: true } || !IsCleanName(tip.Title)) return false;
        var (bare, _) = TextLines.SplitCount(tip.Title!);
        // The card's caption is often partly under the (translucent) tooltip: look for it among
        // all the lines on screen that are not the tooltip's own.
        var tipLines = new HashSet<TextLine>(tip.Lines);
        // Including lines under the panel: it is translucent, and a card's price often shows
        // through its corner.
        var candidates = window.Concat(_allLines.Where(l => !tipLines.Contains(l)
                                                            && !tip.Lines.Any(tl => tl.Box.IoU(l.Box) > 0.6
                                                                                    && TextLines.Similarity(tl.Text, l.Text) >= 0.9)))
                               .Distinct().ToList();
        var captions = FindCaptions(candidates, h);
        var match = captions.Where(c => TextLines.Similarity(bare, c.Name) >= 0.6 && c.Bounds.DistanceTo(x, y) < h * 16)
                            .OrderBy(c => c.Bounds.DistanceTo(x, y)).FirstOrDefault();
        if (match == null) return false;
        var price = ReadFigure(picture, match.Price, h);
        var reading = PriceReader.Read(picture, price, h, PriceContextFor(context == "none" ? "vault" : context));
        t.Type = reading?.Currency == "gems" || context == "gemstore" ? TargetType.GemStoreCard : TargetType.WizardVaultCard;
        t.Name = bare;
        t.Price = reading?.Spoken;
        t.TargetBounds = match.Bounds;
        foreach (var e in match.Extra.Where(e => Availability.IsMatch(e.Text))) t.Details.Add(e.Text);
        t.Confidence = 0.65;
        t.Reasons.Add($"card named by its tooltip '{bare}', price from its caption '{t.Price}'");
        return true;
    }

    private static bool TryIcon(Tooltip? tip, string context, AccessibleTarget t)
    {
        if (tip == null || tip.AnchorDistance > 4.5) return false;
        var title = tip.Title;
        if (title == null)
        {
            // A one-sentence tooltip with no title (objective upgrades): the icon's own label
            // is found by TryLabel; here there is nothing to name it by.
            return false;
        }
        var (bare, count) = TextLines.SplitCount(title);
        t.Type = context == "inventory" || count != null ? TargetType.InventorySlot : TargetType.IconTooltip;
        t.Name = bare;
        t.StackCount = count;
        t.TargetBounds = default;
        t.Confidence = 0.7;
        t.Reasons.Add($"icon under the pointer, named by its tooltip '{title}'");
        return true;
    }

    private static readonly Regex WindowTitle = new(@"(Trading Company|Gem Store|Trading Post|Wizard's Vault|Inventory|Vendor|Season$)",
                                                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingChrome = new(@"^\W*(New!?|\d+% Off!?|Sale Ending Soon!?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A banner: artwork with a few lines of LARGE text laid over it ("Refreshing Seasonal /
    /// Selection of Outfits!"). The pointer is on the picture, not the words; the words are the
    /// banner's name.
    /// </summary>
    private static bool TryBanner(List<TextLine> window, double x, double y, double h, string context, AccessibleTarget t)
    {
        var big = window.Where(l => l.Box.H >= h * 1.6 && TextLines.Letters(l.Text) >= 3 && !Chrome.IsMatch(l.Text)
                                    && !WindowTitle.IsMatch(l.Text) && !ViewDetailsPart.IsMatch(l.Text)).ToList();
        if (big.Count == 0) return false;
        var blocks = TextLines.Blocks(big, big.Average(l => l.Box.H));
        foreach (var b in blocks.OrderBy(b => b.Bounds.DistanceTo(x, y)))
        {
            var bounds = b.Bounds;
            // the banner's artwork spreads well beyond its words
            var area = bounds.Inflate(h * 14, h * 4);
            if (!area.Contains(x, y)) continue;
            // A banner's second line is often set a size smaller ("Refreshing Seasonal" over
            // "Selection of Outfits!"): lines nearly as large, directly under or over it, are its name too.
            var lines = b.Lines.ToList();
            var lineH = lines.Average(l => l.Box.H);
            bool grew = true;
            while (grew)
            {
                grew = false;
                var span = Box.Bounding(lines.Select(l => l.Box));
                // (figures are set without ascenders or descenders: "178 Gems" measures shorter than its words)
                foreach (var l in window.Where(l => !lines.Contains(l) && l.Box.H >= Math.Max(h * 1.25, lineH * 0.7)
                                                    && TextLines.Letters(l.Text) >= 3 && !Chrome.IsMatch(l.Text)
                                                    // the View Details label that follows the pointer, run into a line under it
                                                    && !ViewDetailsPart.IsMatch(l.Text)
                                                    && !WindowTitle.IsMatch(l.Text)
                                                    && l.Box.HorizontalOverlap(span) > 0.5))
                {
                    var below = l.Box.Y - span.Bottom;
                    var above = span.Y - l.Box.Bottom;
                    // (or between two of its lines: a line a shade smaller than the rest can be in the middle)
                    var between = l.Box.CentreY > span.Y && l.Box.CentreY < span.Bottom;
                    if (between || below > -lineH * 0.3 && below < lineH * 0.6 || above > -lineH * 0.3 && above < lineH * 0.6)
                    {
                        lines.Add(l);
                        grew = true;
                        break;
                    }
                }
            }
            bounds = Box.Bounding(lines.Select(l => l.Box));
            area = bounds.Inflate(h * 14, h * 4);
            var name = LeadingChrome.Replace(string.Join(" ", lines.OrderBy(l => l.Box.Y).Select(l => l.Text)), "").Trim();
            if (TextLines.Letters(name) < 6) continue;
            t.Type = context == "vault" ? TargetType.WizardVaultCard : TargetType.GemStoreCard;
            t.Name = name;
            t.TargetBounds = area;
            t.Confidence = 0.5;
            t.Reasons.Add($"banner with large text '{name}'");
            return true;
        }
        return false;
    }

    private static readonly Regex NpcTitle = new(@"^\s*\p{Lu}[\p{L}' -]{2,}\s\[\p{L}[\p{L}' -]{2,}\]\s*$", RegexOptions.Compiled);

    /// <summary>A navigation tab: an icon with a one-word label directly beneath it.</summary>
    private static bool TryTab(List<TextLine> window, double x, double y, double h, AccessibleTarget t)
    {
        // Only the nearest line beneath the pointer can be its label: a short line further down
        // (a player's own "[HALO]" tag under an NPC's nameplate) is somebody else's.
        var beneath = window.Where(l => l.Box.Y >= y && l.Box.Y - y <= h * 1.8
                                        && l.Box.X - h * 0.8 <= x && x <= l.Box.Right + h * 0.8)
                            .OrderBy(l => l.Box.Y - y).FirstOrDefault();
        if (beneath.Text == null) return false;
        // An NPC's nameplate with its title, "Skirmish Supervisor [Skirmish Merchant]": the pointer is
        // on the vendor icon floating just above it.
        if (NpcTitle.IsMatch(beneath.Text) && beneath.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 7
            && !TextLines.IsSentence(beneath.Text))
        {
            t.Type = TargetType.WorldLabel;
            t.Name = beneath.Text;
            t.TargetBounds = Box.FromLTRB(beneath.Box.X, y - h, beneath.Box.Right, beneath.Box.Bottom);
            t.Confidence = 0.5;
            t.Reasons.Add($"nameplate '{beneath.Text}' beneath the icon at the pointer");
            return true;
        }
        var label = window.Where(l => l == beneath
                                      && l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
                                      && TextLines.Letters(l.Text) >= 3 && !TextLines.IsSentence(l.Text)
                                      && !ViewDetails.IsMatch(l.Text)
                                      // a card's caption has its price beside it; a tab does not
                                      && !window.Any(p => TextLines.IsNumeric(p.Text) && !TextLines.IsFraction(p.Text)
                                                          && Math.Abs(p.Box.CentreY - l.Box.CentreY) < h * 2.4
                                                          && Math.Abs(p.Box.CentreX - l.Box.CentreX) < Math.Max(l.Box.W, h * 3)))
                          .OrderBy(l => l.Box.Y - y).FirstOrDefault();
        if (label.Text == null) return false;
        t.Type = TargetType.Button;
        t.Name = label.Text;
        t.TargetBounds = Box.FromLTRB(label.Box.X - h * 0.5, y - h, label.Box.Right + h * 0.5, label.Box.Bottom);
        t.Confidence = 0.5;
        t.Reasons.Add($"tab '{label.Text}' labelled beneath its icon");
        return true;
    }

    /// <summary>
    /// AN NPC'S DIALOG CHOICES: a short column of lines on one margin, spaced wider than the
    /// lines of a paragraph (each has its own icon and a band of highlight), most of them
    /// finished sentences ("I have items to exchange.", "What do you exchange?", "Not right now,
    /// thanks."), with no price beside any of them. A vendor's list has names and prices; a
    /// tooltip's lines sit a line apart.
    /// </summary>
    private static List<TextLine> DialogChoices(List<TextLine> lines, double h)
    {
        var words = lines.Where(l => TextLines.Letters(l.Text) >= 3 && !TextLines.IsNumeric(l.Text)
                                     && l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 12
                                     && l.Box.H < h * 1.6)
                         .OrderBy(l => l.Box.CentreY).ToList();
        var best = new List<TextLine>();
        foreach (var seed in words)
        {
            var column = new List<TextLine> { seed };
            double pitch = 0;
            foreach (var l in words.Where(l => l.Box.CentreY > seed.Box.CentreY))
            {
                var last = column[^1];
                if (Math.Abs(l.Box.X - seed.Box.X) > h * 0.8 || Math.Abs(l.Box.H - seed.Box.H) > h * 0.45) continue;
                var step = l.Box.CentreY - last.Box.CentreY;
                if (step < h * 1.5) continue;
                if (step > h * 3.2) break;
                if (pitch > 0 && Math.Abs(step - pitch) > pitch * 0.25) break;
                if (pitch == 0) pitch = step;
                column.Add(l);
            }
            if (column.Count < 2 || column.Count > 8 || column.Count <= best.Count) continue;
            // mostly finished sentences
            if (column.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text.TrimEnd(), @"[.?!]$")) * 2 < column.Count + 1) continue;
            // nothing between the choices on their margin (a paragraph's lines, a list's rows)
            var top = column[0].Box.Y; var bottom = column[^1].Box.Bottom;
            if (words.Any(l => !column.Contains(l) && Math.Abs(l.Box.X - seed.Box.X) <= h * 0.8
                               && l.Box.CentreY > top && l.Box.CentreY < bottom)) continue;
            // no prices on their rows
            if (lines.Any(p => TextLines.IsNumeric(p.Text) && p.Box.X > seed.Box.X
                               && column.Any(c => Math.Abs(p.Box.CentreY - c.Box.CentreY) < h * 0.6))) continue;
            best = column;
        }
        return best;
    }

    /// <summary>
    /// The figure at the end of a row, found by its shape: runs of strokes on the name's line,
    /// tried left to right, each read closely on its own. Its leading digits are a price only
    /// when something icon-sized is drawn right after them (a coin, a currency, the item it
    /// costs). A row that ends in an icon with no figure (a lock) has no price - the recogniser
    /// reads a lock as "8", and nothing is drawn after it.
    /// </summary>
    private TextLine FigureBesideIcon(Mat picture, UiGeometry geo, TextLine name, double limit, Tooltip? tip, double h)
    {
        var nameH = name.Box.H;
        var band = Box.FromLTRB(name.Box.Right + h, name.Box.CentreY - nameH * 0.45, limit, name.Box.CentreY + nameH * 0.45);
        var runs = geo.InkRuns(band)
                      .Where(r => r.X1 - r.X0 >= 2 && (tip == null || !tip.Panel.Contains((r.X0 + r.X1) / 2, name.Box.CentreY)))
                      .ToList();
        int tries = 0;
        for (int i = 0; i < runs.Count && tries < 4; i++)
        {
            var (x0, x1) = runs[i];
            if (x1 - x0 > h * 4.5) continue;
            tries++;
            // The recogniser pads every crop sideways. Read once with the crop ending at these
            // strokes (so the icon beside them is not read as another character, "1e"), and once
            // a little wider (a lone thin "1" read too tightly loses its confidence).
            var pad = Math.Max(2, nameH * MatOcr.CropPadX);
            var cells = new[]
            {
                Box.FromLTRB(x0 - h * 0.3 + pad, name.Box.CentreY - nameH * 0.5, Math.Max(x0 - h * 0.3 + pad + 2, x1 + 1 - pad), name.Box.CentreY + nameH * 0.5),
                Box.FromLTRB(x0 - h * 0.2, name.Box.CentreY - nameH * 0.5, x1 + h * 0.1, name.Box.CentreY + nameH * 0.5),
            };
            var reads = _ocr.Recognize(picture, cells, h);
            foreach (var read in reads)
            {
                var figure = LeadingFigure(read);
                if (figure.Text != null && IconAfter(geo, figure, name, h))
                    return figure;
            }
        }
        return default;
    }

    /// <summary>The digits a reading starts with ("2@" -> "2"), with their positions; nothing if
    /// the reading is words.</summary>
    private static TextLine LeadingFigure(TextLine read)
    {
        var text = (read.Text ?? "").Trim();
        int n = 0;
        while (n < text.Length && (char.IsDigit(text[n]) || (text[n] == ',' && n > 0))) n++;
        if (n == 0 || read.Confidence < 70 || TextLines.Letters(text) >= 2) return default;
        var digits = text[..n].TrimEnd(',');
        double[]? xs = read.CharX != null && read.CharX.Length == read.Text!.Length ? read.CharX[..digits.Length] : null;
        return new TextLine(digits, read.Box, read.Confidence, xs);
    }

    /// <summary>Is something icon-sized drawn just after a figure's last digit?</summary>
    private static bool IconAfter(UiGeometry geo, TextLine figure, TextLine name, double h)
    {
        var lastX = figure.CharX is { Length: > 0 } xs ? xs[^1] : figure.Box.Right - figure.Box.H * 0.3;
        var band = Box.FromLTRB(lastX + h * 0.25, name.Box.CentreY - name.Box.H * 0.45, lastX + h * 1.7, name.Box.CentreY + name.Box.H * 0.45);
        var ink = geo.InkRuns(band).Sum(r => r.X1 - r.X0);
        return ink >= h * 0.5;
    }

    private static bool TryDialogChoice(List<TextLine> choices, double x, double y, double h, AccessibleTarget t)
    {
        if (choices.Count < 2) return false;
        var pitch = (choices[^1].Box.CentreY - choices[0].Box.CentreY) / (choices.Count - 1);
        var left = choices.Min(c => c.Box.X) - h * 3;                 // the choice's icon
        var right = choices.Max(c => c.Box.Right) + h * 6;            // its highlight runs past the words
        if (x < left || x > right) return false;
        var hit = choices.OrderBy(c => Math.Abs(c.Box.CentreY - y)).First();
        if (Math.Abs(hit.Box.CentreY - y) > pitch * 0.5) return false;
        t.Type = TargetType.DialogOption;
        t.Name = hit.Text.TrimEnd();
        t.TargetBounds = Box.FromLTRB(left, hit.Box.CentreY - pitch / 2, right, hit.Box.CentreY + pitch / 2);
        t.Confidence = 0.7;
        t.TooltipLines.Clear();
        t.TooltipBounds = default;
        t.TooltipTitle = null;
        t.Reasons.Add($"dialog choice {choices.IndexOf(hit) + 1} of {choices.Count}");
        return true;
    }

    /// <summary>
    /// A small hover label drawn beside the pointer ("Novelties" beside a vendor's tab icon):
    /// one to three words, on its own - no other text on its margin just above or below, so it
    /// is not a row of a list or a line of a paragraph.
    /// </summary>
    private static TextLine HoverLabelNear(List<TextLine> window, double x, double y, double h)
    {
        return window.Where(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
                                 && TextLines.Letters(l.Text) >= 4 && !TextLines.IsNumeric(l.Text) && l.Box.H < h * 1.5
                                 && !Chrome.IsMatch(l.Text) && !ViewDetails.IsMatch(l.Text)
                                 && l.Box.X - x >= -h * 1.0 && l.Box.X - x <= h * 4
                                 && l.Box.Bottom >= y - h * 3 && l.Box.Y <= y + h * 1.5
                                 && !window.Any(o => !o.Equals(l) && Math.Abs(o.Box.X - l.Box.X) < h * 0.8
                                                     && Math.Abs(o.Box.CentreY - l.Box.CentreY) < h * 3))
                     .OrderBy(l => l.Box.DistanceTo(x, y)).FirstOrDefault();
    }

    private static bool TryHoverLabel(List<TextLine> window, double x, double y, double h, AccessibleTarget t)
    {
        var label = HoverLabelNear(window, x, y, h);
        if (label.Text == null) return false;
        // a letter of the text behind the label, run on ("Novelties U", "NoveltiesE")
        var name = System.Text.RegularExpressions.Regex.Replace(label.Text, @"(\s+\p{L}|(?<=\p{Ll}{3})\p{Lu})$", "").Trim();
        t.Type = TargetType.IconTooltip;
        t.Name = name;
        t.TargetBounds = Box.FromLTRB(x - h, y - h, x + h, y + h);
        t.Confidence = 0.5;
        t.Reasons.Add($"hover label '{name}' beside the pointer");
        return true;
    }

    private static bool TryLabel(List<TextLine> window, double x, double y, double h, string context, AccessibleTarget t)
    {
        var hit = window.Where(l => l.Box.Inflate(h * 0.35, h * 0.25).Contains(x, y))
                        .OrderBy(l => Math.Abs(l.Box.CentreY - y)).FirstOrDefault();
        // Large type in the Gem Store or the Vault is a banner's: read as the banner, whole.
        if (hit.Text != null && context is "gemstore" or "vault" && hit.Box.H >= h * 1.6) return false;
        if (hit.Text == null)
        {
            // The label above an icon (objective upgrades): the pointer is on the icon, the
            // name sits directly above it.
            hit = window.Where(l => l.Box.Bottom <= y && y - l.Box.Bottom <= h * 4.5
                                    && l.Box.X - h * 0.8 <= x && x <= l.Box.Right + h * 0.8
                                    && !TextLines.IsSentence(l.Text) && TextLines.Letters(l.Text) >= 4
                                    && !WindowTitle.IsMatch(l.Text) && !Chrome.IsMatch(l.Text))   // the window's title and section headings are nobody's icon label
                        .OrderBy(l => y - l.Box.Bottom).FirstOrDefault();
            if (hit.Text == null || t.TooltipLines.Count == 0) return false;
            // a label directly beneath the icon, nearer than this one, is the icon's own (a tab's)
            var under = window.Where(l => l.Box.Y >= y && l.Box.Y - y <= h * 1.8
                                          && l.Box.X - h * 0.8 <= x && x <= l.Box.Right + h * 0.8)
                              .Select(l => l.Box.Y - y).DefaultIfEmpty(double.MaxValue).Min();
            if (under < y - hit.Box.Bottom) return false;
            t.Type = TargetType.IconTooltip;
            t.Name = hit.Text;
            t.TargetBounds = hit.Box;
            t.Reasons.Add($"icon labelled '{hit.Text}' above the pointer");
            return true;
        }

        // A paragraph: lines on the same margin directly above and below.
        var para = new List<TextLine> { hit };
        foreach (var dir in new[] { -1, 1 })
        {
            var cur = hit;
            for (int i = 0; i < 3; i++)
            {
                // (large banner type: its detector boxes overlap more and vary more in height, in
                // proportion to its size)
                var next = window.Where(l => !para.Contains(l) && Math.Abs(l.Box.X - cur.Box.X) < h * 0.7
                                             && !ViewDetailsPart.IsMatch(l.Text)
                                             && Math.Abs(l.Box.H - cur.Box.H) < Math.Max(h * 0.4, Math.Max(l.Box.H, cur.Box.H) * 0.3)
                                             && (dir > 0 ? l.Box.Y - cur.Box.Bottom : cur.Box.Y - l.Box.Bottom) is var g
                                             && g > -Math.Max(h * 0.5, cur.Box.H * 0.35) && g < h * 0.45)
                                 .OrderBy(l => Math.Abs(l.Box.CentreY - cur.Box.CentreY)).FirstOrDefault();
                if (next.Text == null) break;
                if (dir < 0) para.Insert(0, next); else para.Add(next);
                cur = next;
            }
        }

        // A nameplate's title, centred directly under a single-line name.
        if (para.Count == 1)
        {
            var sub = window.Where(l => l.Box.Y > hit.Box.CentreY && l.Box.Y - hit.Box.Bottom < h * 0.5
                                        && Math.Abs(l.Box.CentreX - hit.Box.CentreX) < Math.Max(hit.Box.W, l.Box.W) * 0.3).ToList();
            if (sub.Count == 1 && TextLines.Letters(sub[0].Text) >= 4 && !TextLines.IsNumeric(sub[0].Text)) t.Details.Add(sub[0].Text);
        }

        t.Type = TargetType.Label;
        t.Name = string.Join(" ", para.Select(p => p.Text));
        t.TargetBounds = Box.Bounding(para.Select(p => p.Box));
        t.Confidence = 0.6;
        t.Reasons.Add($"label under the pointer ({para.Count} line(s))");
        return true;
    }
}

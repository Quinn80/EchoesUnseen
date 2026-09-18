using System.Runtime.InteropServices;
using System.Windows;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Services;

/// <summary>
/// Reads aloud whatever is on screen around the mouse pointer — item tooltips,
/// inventory slots, menu buttons, achievement rows.
///
/// WHY A REGION AROUND THE CURSOR, NOT THE WHOLE SCREEN
///   Guild Wars 2 draws its tooltip next to whatever you're pointing at, so the
///   text you want is almost always within a few hundred pixels of the cursor.
///   Grabbing just that area means OCR has a small, dense patch to read instead
///   of an entire 3D scene, which is both far more accurate and much faster.
///
/// NO AI: this is Windows' built-in OCR plus the contrast/upscale conditioning
/// in <see cref="ImagePrep"/>. Nothing is sent anywhere; nothing is trained.
///
/// LIMITS, HONESTLY
///   The game exposes no accessibility information, so reading its interface
///   means reading pixels. Tooltips (large, high-contrast, on a dark panel) read
///   well. Tiny dim labels over a bright scene are still hard. For inventory
///   contents and gold there is a far better route than pixels — the official
///   GW2 API — which returns exact data (see Gw2ApiService).
/// </summary>
public sealed class CursorReader
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // The tooltip box GW2 draws is roughly this size; generous enough to catch a
    // full item description, tight enough to keep OCR fast and focused.
    /// <summary>
    /// The capture box around the pointer, in physical pixels — SIZED FROM THE TEXT ON
    /// SCREEN, not from any one machine.
    ///
    /// 880x640 was measured against Quinn's tooltips, and she plays at Larger interface
    /// size with display scaling on. Someone on Small at 100% would get a box mostly
    /// full of the things around the tooltip; someone on Larger at 150% would get the
    /// truncation this box was widened to cure, with lines cut off mid-word. A constant
    /// tuned on one screen is a bug on everyone else's.
    ///
    /// Interface size and display scaling both show up as one thing: how tall a line of
    /// text is. RapidOCR reports that on every read, so the box is
    /// a multiple of it. 880x640 is right when a line is about 22 pixels tall, which is
    /// what Quinn's grabs measure, so that is the reference point.
    ///
    /// Before the first measurement there is nothing to go on but the display scaling,
    /// which is the larger half of the variance and is knowable immediately. One hover
    /// replaces the guess with the real thing.
    /// </summary>
    // CAPTURE AREA AND OCR RESOLUTION ARE NOT THE SAME QUESTION.
    //
    // They were being answered with one number, so "the text is small" made the reader
    // sample MORE OF THE GAME - grabs of 880x640, 1077x783, 1339x974 - and then try to
    // work out which of the dozen labels in shot belonged to the pointer. That is
    // backwards. Small text means enlarge the PICTURE, not the AREA.
    //
    // 460x340 around the pointer is about one tooltip's worth of screen. If the thing
    // being read turns out to run past the edge, the grab is retaken once, wider, aimed
    // at where the text actually went - rather than everything being made bigger for
    // everyone all the time.
    private const int BaseWidth = 460, BaseHeight = 340;
    /// <summary>
    /// The line height 460x340 was tuned for - IN RAPIDOCR'S UNITS.
    ///
    /// This was 22, measured from Windows OCR boxes, which hug the glyphs. RapidOCR
    /// returns a box a good half taller for exactly the same text; it is the same
    /// generosity that makes its boxes overlap vertically, which the reading order
    /// already had to be rewritten around. Feeding one engine's number into a reference
    /// calibrated on the other inflated every ratio by about 1.5, and since the ratio is
    /// clamped at 2.0 the capture simply pinned there: Quinn's log shows 920x680 on 119
    /// of about 140 reads, which is the maximum, every time.
    ///
    /// A capture that size is most of a game panel. It reaches the chat box - the last
    /// session spoke "Kalus Ist Krieg: what if we flipped blue keep" while she was
    /// hovering a menu - and it reads down whatever list it lands in, which is exactly
    /// what she described. The engine swap did not break sizing loudly; it just moved the
    /// goalposts and nothing noticed.
    ///
    /// Her own measurements: 26 to 36 px, median around 32, for text 460x340 was right
    /// for. So 32 is the reference in these units.
    /// </summary>
    private const double ReferenceTextHeight = 32.0;   // RapidOCR box heights, from the log

    /// <summary>
    /// How tall a line of text is in HOVER's grabs. Hover's own, deliberately.
    ///
    /// This used to live on OcrService as a shared static, and the CHAT READER was
    /// writing to it. Chat calls ReadLinesAsync every couple of seconds on the chat
    /// panel, which runs the Windows OCR path, which measured that picture and stored
    /// the result in the same field hover sizes its capture from. So hover was sizing
    /// itself from a measurement of the chat box - a different picture, at a different
    /// scale, in a different engine's box units.
    ///
    /// It is why changing hover's reference from 22 to 32 did nothing at all: the value
    /// being divided was not the one hover had measured, and was overwritten again
    /// seconds later. The capture stayed pinned at 920x680 across two builds and the
    /// smoothed maximum was identically 152 in both, which is not something two different
    /// smoothing algorithms do unless neither of them is running.
    ///
    /// Two subsystems, one mutable static. The same shape as DIPs against physical
    /// pixels and Windows box heights against RapidOCR's: a number crossing a boundary
    /// it was never measured for.
    /// </summary>
    private static readonly List<double> _textHeights = new();
    private static double _textHeight;

    /// <summary>Note a line height from a HOVER grab. A running median rather than an
    /// average, because single detections reach 300 px and an average absorbs a share of
    /// an outlier where a median ignores it.</summary>
    private static void NoteHoverTextHeight(double px)
    {
        // A LINE OF TEXT IS A LINE OF TEXT. REFUSE ANYTHING ELSE.
        //
        // Smoothing the output was treating the symptom. The input is what is wrong:
        // per-read measurements sit at a median of 31 px with a ninetieth percentile of
        // 46, and then reach 328. Nothing in the Guild Wars 2 interface has a 328-pixel
        // line of text in a 460-pixel grab - that is one OCR box thrown across a slab of
        // artwork or an icon block, and it is not a measurement of anything.
        //
        // Letting it in and then averaging or medianing it away never worked: only 5% of
        // reads are large enough to justify the ceiling, yet 14% of captures reached it,
        // because a handful of absurd values enter the window and sit in it for several
        // hovers afterwards. Rejected at the door instead, where a wrong number belongs.
        const double MinLine = 8, MaxLine = 80;
        if (px < MinLine || px > MaxLine)
        {
            DiagLog.Log("HOVER", $"ignoring implausible line height {px:F0} px");
            return;
        }

        _textHeights.Add(px);
        if (_textHeights.Count > 9) _textHeights.RemoveAt(0);
        var sorted = _textHeights.OrderBy(v => v).ToList();
        _textHeight = sorted[sorted.Count / 2];
    }
    private const double ReferenceScale = 1.25;        // her display scaling

    /// <summary>Grab geometry for a pointer at this physical point: the box, and where
    /// the pointer sits inside it.</summary>
    public static (int W, int H, int InsetX, int InsetY) BoxFor(int px, int py)
    {
        double k = _textHeight > 4
            ? _textHeight / ReferenceTextHeight
            : ScreenMetrics.ScaleAt(px, py) / ReferenceScale;

        k = Math.Clamp(k, 0.6, 2.0);
        int w = (int)(BaseWidth * k), h = (int)(BaseHeight * k);

        // Biased right and down, because that is where the game draws a tooltip - but a
        // third of the way in rather than a quarter, because the coin row at the bottom
        // of the inventory runs LEFT from the pointer and a thinner margin cut the first
        // digit off, turning 135 gold into 35 gold.
        return (w, h, w / 3, h / 5);
    }

    /// <summary>How hard to upscale before OCR, for text of the size last seen.
    ///
    /// The engines want letters around 60 pixels tall. Multiplying by a fixed number
    /// instead means a player with a small interface gets text too small to read and one
    /// with a large interface gets it blown up until the interpolation invents edges -
    /// which is measurably worse, not merely wasteful: at 4x a nameplate read
    /// "Warsongt", at 2x it read "Warsong".</summary>
    public static int UpscaleForText()
    {
        var t = _textHeight;
        if (t <= 4) return 3;

        // Target in the same units as the measurement. 60 was chosen against Windows
        // box heights; the equivalent for RapidOCR's taller boxes is about 90, which
        // lands on the 2x-3x that measured best on her captures rather than pushing
        // everything to the floor of the range.
        return Math.Clamp((int)Math.Round(90.0 / t), 2, 6);
    }


    private readonly TtsService _tts;
    private string _lastSpoken = "";


    public CursorReader(TtsService tts) { _tts = tts; }

    /// <summary>Read the story/event objective from the top-right of the screen, so a
    /// player who can't see the tracker knows their current goal and next step.</summary>
    public async Task ReadObjectiveAsync()
    {
        try
        {
            // Physical pixels, and the monitor the game is on - the DIP width used to
            // put this band 512 px left of the actual top-right corner, so the tracker
            // was only ever half in shot.
            if (!GetCursorPos(out var cp)) { await SayHover("I couldn't find the mouse pointer."); return; }
            var mon = ScreenMetrics.MonitorAt(cp.X, cp.Y);
            int w = (int)(mon.Width * 0.38), h = (int)(mon.Height * 0.45);   // top-right tracker area
            var png = ScreenCaptureService.CapturePng((int)mon.Right - w, (int)mon.Top, w, h);
            if (png == null) { await SayHover("I couldn't capture the objective area."); return; }

            var text = Hover.HoverText.Tidy(await OcrService.ReadAsync(png));
            await SayHover(string.IsNullOrWhiteSpace(text) ? "I couldn't read an objective." : text);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("CursorReader.ReadObjectiveAsync", ex);
            await SayHover("Something went wrong reading the objective.");
        }
    }





    /// <summary>
    /// Run the tooltip finder for the record only.
    ///
    /// Everything it learns goes to the log and to the bug report; nothing it finds
    /// reaches the voice. What the log needs to answer before this can be trusted: does
    /// a panel appear when a tooltip really is on screen, does one appear when there is
    /// none, and is what it reads better than what the pointer-based reader said.
    /// </summary>
    private async Task ShadowTooltipAsync(Rect mon, int upscale, CancellationToken ct)
    {
        try
        {
            var age = Hover.TooltipFinder.BaselineAgeMs;
            var panel = Hover.TooltipFinder.FindNewPanel(mon);
            if (panel == Rect.Empty)
            {
                // THE FAILURES ARE THE DATA.
                //
                // The last three bug reports contained no tooltip folder at all, because
                // a candidate was only ever saved when one was ACCEPTED - and across 264
                // attempts exactly one was. Every rejection, which is the entire thing
                // worth studying, was thrown away. So a rejection is now recorded too,
                // with the reason, and the replay corpus finally gets the cases that
                // matter.
                DiagLog.Log("SHADOW", $"no panel (baseline {age:F0} ms old) - " +
                                      Hover.TooltipFinder.LastReason);
                BugRecorderService.NoteTooltipCandidate(
                    Hover.TooltipFinder.LastAfterPng, Hover.TooltipFinder.LastChangedBox,
                    "REJECTED: " + Hover.TooltipFinder.LastReason, age);
                return;
            }

            var png = ScreenCaptureService.CapturePng(
                (int)panel.Left, (int)panel.Top, (int)panel.Width, (int)panel.Height);
            if (png == null) return;

            var tipBoxes = await ReadBoxesAsync(png, upscale, (int)panel.Width, (int)panel.Height, ct);
            var text = Hover.HoverText.JoinForSpeech(
                tipBoxes.OrderBy(b => Math.Round(b.Y, 1)).ThenBy(b => b.X)
                        .Select(b => b.Text).Select(Hover.HoverText.Tidy).Select(Hover.HoverText.CleanSceneText)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !Hover.HoverText.IsPanelChrome(l)
                                    && Hover.HoverText.LooksLikeWords(l)));

            DiagLog.Log("SHADOW", $"panel {(int)panel.Width}x{(int)panel.Height} at " +
                                  $"({(int)panel.Left},{(int)panel.Top}), baseline {age:F0} ms old, " +
                                  $"{tipBoxes.Count} line(s) -> would say: " +
                                  (text.Length > 90 ? text[..90] : text));

            BugRecorderService.NoteTooltipCandidate(png, panel, text, age);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogger.Log("CursorReader.ShadowTooltip", ex); }
    }

    /// <summary>
    /// One hover through the OpenCV + RapidOCR targeting. Returns null when it answered the hover,
    /// or the reason it could not - the classic reader then takes over, and the reason is logged.
    /// </summary>
    private async Task<string?> ReadWithFusionAsync(int px, int py, bool auto, CancellationToken ct)
    {
        // The vault's own reward names (cached for hours; an empty list when there is no key).
        var vaultNames = await VaultNamesAsync(ct).ConfigureAwait(false);
        Hover.FusionHoverReader.Result? result;
        try
        {
            result = await Hover.FusionHoverReader.ReadAsync(px, py, vaultNames, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A fault in the new targeting costs this one hover its answer from it, not the
            // answer: the classic reader reads it instead.
            CrashLogger.Log("CursorReader.Fusion", ex);
            return $"analysis failed: {ex.GetType().Name}: {ex.Message}";
        }
        if (result == null)
            return Hover.FusionHoverReader.Unavailable
                ? "the OpenCV + RapidOCR engine could not load on this machine: " + Hover.FusionHoverReader.UnavailableReason
                : "the screen around the pointer could not be captured";
        ct.ThrowIfCancellationRequested();

        var t = result.Target;
        var r = result.Captured;
        // Same shape as the classic line, so the replay tools read both.
        DiagLog.Log("HOVER", $"pointer {px},{py}  physical capture {(int)r.Width}x{(int)r.Height} at ({(int)r.X},{(int)r.Y})  targeting fusion");
        Hover.HoverTargeting.AnsweredByNew();
        string Ms(string k) => t.TimingsMs.TryGetValue(k, out var v) ? $"{v:F0}" : "-";
        DiagLog.Log("FUSION", $"{t.Type} \"{t.Name}\" price=\"{t.Price}\" tooltip=\"{t.TooltipTitle}\" ({t.TooltipLines.Count} lines) " +
                              $"speak={t.ShouldSpeak}  ms: capture {result.CaptureMs:F0} detect {Ms("detect")} read {Ms("recognise")} " +
                              $"opencv {Ms("opencv")} fusion {Ms("fusion")} total {Ms("total")}");
        foreach (var reason in t.Reasons.Where(x => !x.StartsWith("list?")).Take(10))
            DiagLog.Log("FUSION", "  " + reason);

        if (string.IsNullOrWhiteSpace(t.Speech))
        {
            DiagLog.Log("HOVER", "nothing readable (fusion)");
            if (!auto) await SayHover("Nothing readable under the pointer.");
            return null;
        }
        if (auto && !t.ShouldSpeak)
        {
            DiagLog.Log("HOVER", $"same object, staying quiet: {t.Name}");
            return null;
        }

        DiagLog.Log("HOVER", "fusion says: " + (t.Speech.Length > 160 ? t.Speech[..160] : t.Speech));
        _lastSpoken = t.Speech;
        await SayHover(t.Speech);
        return null;
    }

    /// <summary>The reward last spoken, so wandering inside it stays quiet.</summary>
    private Hover.VaultCard? _lastCard;

    /// <summary>The season's real reward names, fetched once and kept.</summary>
    private static List<string>? _vaultNames;
    private static DateTime _vaultNamesAt;

    /// <summary>
    /// Is the pointer inside a Wizard's Vault reward card, and if so which one?
    ///
    /// Two things make this work that would not work from pixels alone.
    ///
    /// The grab is sized for a tooltip, and a reward card is taller than that - the
    /// caption sits at the FOOT of the card, below three hundred pixels of picture, so
    /// hovering the artwork cannot see it. When the first read shows a price with no name
    /// attached, the grab is retaken once, taller, reaching down to where the caption
    /// must be. One retry, only when there is a reason for it.
    ///
    /// And the names do not have to be guessed. The season's listings are already fetched
    /// from the account API with exact names and costs, so a caption read as "th weapon
    /// skin" is resolved to the reward it actually is. That is recognition against a known
    /// set, not invention.
    /// </summary>
    private async Task<Hover.VaultCard?> FindVaultCardAsync(
        List<Hover.TextBox> boxes, int insetX, int insetY,
        int grabX, int grabY, int boxW, int boxH, int upscale, CancellationToken ct)
    {
        var names = await VaultNamesAsync(ct).ConfigureAwait(false);

        var card = Hover.VaultCards.Find(boxes, insetX, insetY, names);
        if (card != null) return card;

        // NO TALL RETAKE UNLESS THIS REALLY IS THE VAULT.
        //
        // This started as a fix for one layout and turned into general behaviour: the
        // log shows "retaking 460x680" firing in the inventory, at merchants, in the gem
        // store and down ordinary lists, because ANY bare number looked like a reason.
        // That is the "OCR half the screen and sort it out afterwards" mistake coming
        // back in through a side door, and it is the thing this whole architecture
        // exists to avoid.
        //
        // The season's costs are known - they came from the same API as the names - so a
        // number that is not a price in this season's vault is not a reason to go
        // looking for a caption. Everywhere else, an unexplained number stays an
        // unexplained number, and the grab stays small.
        var costs = await VaultCostsAsync(ct).ConfigureAwait(false);
        if (costs.Count == 0) return null;

        var looksLikeVaultPrice = boxes.Any(b =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                b.Text.Trim(), @"^([1-9][\d,]{1,5})\s*[^\d\s]{0,3}$");
            return m.Success
                && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var v)
                && costs.Contains(v);
        });
        if (!looksLikeVaultPrice) return null;

        var tallH = Math.Min(boxH * 2, 1100);
        var mon = ScreenMetrics.MonitorAt(grabX + insetX, grabY + insetY);
        var tallY = Math.Clamp(grabY, (int)mon.Top, Math.Max((int)mon.Top, (int)mon.Bottom - tallH));

        var tallPng = ScreenCaptureService.CapturePng(grabX, tallY, boxW, tallH);
        if (tallPng == null) return null;

        DiagLog.Log("HOVER", $"price with no caption - retaking {boxW}x{tallH} to reach it");
        var tallBoxes = await ReadBoxesAsync(tallPng, upscale, boxW, tallH, ct).ConfigureAwait(false);
        return Hover.VaultCards.Find(tallBoxes, insetX, insetY + (grabY - tallY), names);
    }

    /// <summary>Every Astral Acclaim cost in this season, so a number can be recognised
    /// as a vault price rather than merely as a number.</summary>
    private static HashSet<int>? _vaultCosts;

    private static async Task<HashSet<int>> VaultCostsAsync(CancellationToken ct)
    {
        if (_vaultCosts != null && (DateTime.UtcNow - _vaultNamesAt).TotalHours < 6) return _vaultCosts;
        try
        {
            var season = await new WizardsVaultService().GetSeasonAsync(ct).ConfigureAwait(false);
            _vaultCosts = season?.Rewards.Select(r => r.Cost).Where(c => c > 0).ToHashSet()
                          ?? new HashSet<int>();
        }
        catch { _vaultCosts = new HashSet<int>(); }
        return _vaultCosts;
    }

    private static async Task<List<string>> VaultNamesAsync(CancellationToken ct)
    {
        if (_vaultNames != null && (DateTime.UtcNow - _vaultNamesAt).TotalHours < 6) return _vaultNames;
        try
        {
            var season = await new WizardsVaultService().GetSeasonAsync(ct).ConfigureAwait(false);
            _vaultNames = season?.Rewards.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n))
                                 .Distinct().ToList() ?? new List<string>();
            _vaultNamesAt = DateTime.UtcNow;
            DiagLog.Log("HOVER", $"vault listings cached: {_vaultNames.Count} reward name(s)");
        }
        catch { _vaultNames = new List<string>(); _vaultNamesAt = DateTime.UtcNow; }
        return _vaultNames;
    }

    /// <summary>
    /// Read the grab and hand back every line with its box, in the GRAB's own pixels.
    ///
    /// Both engines are asked for the same thing and their answers come back in the same
    /// shape, so everything downstream - targeting, filtering, speech - is written once
    /// and does not know or care which engine produced it.
    /// </summary>
    private static async Task<List<Hover.TextBox>> ReadBoxesAsync(
        byte[] png, int upscale, int boxW, int boxH, CancellationToken ct)
    {
        var list = new List<Hover.TextBox>();

        // The engines want a bigger picture than the grab; the SELECTOR wants the grab's
        // own coordinates, so every box is divided back down on the way out.
        var enlarged = ImagePrep.EnhanceForOcr(png, upscaleOverride: upscale);

        var rapid = await Ocr.RapidOcrService.ReadAsync(enlarged, ct).ConfigureAwait(false);
        if (rapid.Count > 0)
        {
            DiagLog.Log("HOVER", $"RapidOCR: {rapid.Count} line(s) in {Ocr.RapidOcrService.LastMs:F0} ms");

            // HOW TALL IS THE TEXT? Measured here now, because it used to be measured
            // inside the Windows OCR path - and hover stopped going through that path
            // when RapidOCR took over. Quinn's log has said "text last measured 0 px" on
            // every single read since, which means the grab size and the upscale have
            // both been frozen at their fallback ever since the engine changed. Adaptive
            // sizing did not break loudly; it simply stopped happening.
            // A TRIMMED MIDDLE, NOT AN EXTREME. One giant heading or one clipped sliver
            // would otherwise set the size for the next read, and the grab would lurch.
            var heights = rapid.Select(b => b.H / upscale).Where(h => h > 2).OrderBy(h => h).ToList();
            if (heights.Count > 0)
            {
                var trim = heights.Count >= 5 ? heights.Count / 5 : 0;      // drop the outer fifth
                var middle = heights.Skip(trim).Take(Math.Max(1, heights.Count - trim * 2)).ToList();
                var measured = middle[middle.Count / 2];
                NoteHoverTextHeight(measured);
                DiagLog.Log("HOVER", $"RapidOCR measured text height: {measured:F0} px " +
                                     $"(from {heights.Count} line(s))");
            }

            foreach (var b in rapid)
                list.Add(new Hover.TextBox(b.Text, b.X / upscale, b.Y / upscale,
                                           b.W / upscale, b.H / upscale, b.Confidence));
            return list;
        }

        var winLines = await OcrService.ReadLinesAsync(png, forceWindows: true, upscaleOverride: upscale);
        var winBoxes = OcrService.LastLineBoxes;
        DiagLog.Log("HOVER", $"Windows OCR fallback: {winLines.Count} line(s)");

        if (winBoxes == null || winBoxes.Count != winLines.Count)
        {
            foreach (var l in winLines)
                list.Add(new Hover.TextBox(l, 0, 0, boxW, boxH / 4.0, 0));
            return list;
        }

        for (int i = 0; i < winLines.Count; i++)
        {
            var b = winBoxes[i];   // normalised 0..1 by OcrService
            list.Add(new Hover.TextBox(winLines[i], b.X * boxW, b.Y * boxH,
                                       b.Width * boxW, b.Height * boxH, 0));
        }
        return list;
    }

    /// <summary>OCR's normalised boxes, in the grab's own pixels, for the selector.</summary>
    private static List<Hover.TextBox> ToBoxes(List<string> lines, List<Rect>? boxes, int w, int h)
    {
        var list = new List<Hover.TextBox>();
        if (boxes == null || boxes.Count != lines.Count)
        {
            // No geometry (the page engine does not report any): every line is a
            // candidate sitting nowhere in particular, so the selector will fall back to
            // treating the first as a label.
            foreach (var l in lines) list.Add(new Hover.TextBox(l, 0, 0, w, h / 4.0, 0));
            return list;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var b = boxes[i];
            list.Add(new Hover.TextBox(lines[i], b.X * w, b.Y * h, b.Width * w, b.Height * h, 0));
        }
        return list;
    }

    /// <summary>
    /// Keep the lines the pointer is actually ON, and drop the rest of the picture.
    ///
    /// The grab is 620x420 - far bigger than a name floating over someone's head - so
    /// a read came back with the name welded to whatever else happened to be in shot.
    /// One of Quinn's was "86 minute:. ooo-. Crae.at Ad,v.ernturaer", which is a match
    /// timer from the top of the screen, a scrap, and a title. Only the third thing
    /// was under her pointer.
    ///
    /// Windows OCR reports where every line sat, so the answer is already there. The
    /// pointer is at a known spot inside the grab (the box is offset around it by
    /// design), lines are ranked by distance from it, and anything much further away
    /// than the closest is somewhere else on the screen and not what was asked about.
    /// A tooltip survives whole, because its lines sit together.
    /// </summary>
    private static List<string> NearestToPointer(List<string> lines, List<Rect>? boxes,
                                                 double px, double py)
    {
        if (boxes == null || boxes.Count != lines.Count || lines.Count <= 1) return lines;

        static double Gap(double v, double lo, double hi) =>
            v < lo ? lo - v : v > hi ? v - hi : 0;   // 0 while inside

        var ranked = new List<(string Text, double Dist, Rect Box)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var b = boxes[i];
            var dx = Gap(px, b.Left, b.Right);
            var dy = Gap(py, b.Top, b.Bottom);
            ranked.Add((lines[i], Math.Sqrt(dx * dx + dy * dy), b));
        }

        var nearest = ranked.Min(r => r.Dist);
        const double Band = 0.20;      // a fifth of the grab past the closest line

        // KEEP THE COLUMN, NOT THE NEIGHBOURHOOD.
        //
        // Distance alone let a read collect eight unrelated things:
        //
        //     "-anTArChet. Fortified 'Y. Drop). Tactivator ,tDra. Vegetable
        //      Synt'hesizer. [HALO]. [HALO]. Berry 'Synthesizer"
        //
        // which is a handful of nameplates and item labels scattered across the grab,
        // all of them roughly as near the pointer as each other. A tooltip does not
        // look like that. A tooltip is a COLUMN: its lines stack vertically and share
        // the same horizontal span. Nameplates dotted around the world do not overlap
        // each other side to side.
        //
        // So the nearest line anchors a column, and only lines that sit within its
        // horizontal span join it.
        var anchor = ranked.OrderBy(r => r.Dist).First().Box;
        double left = anchor.Left - 0.10, right = anchor.Right + 0.10;

        // CHOOSE by distance. SPEAK in reading order.
        //
        // Those are different questions and answering both with distance got the
        // tooltip backwards. "Account Bound" sits at the BOTTOM of a tooltip but often
        // lands nearer the pointer than the title does, so Quinn was hearing
        // "Account Bound. Consumable" and "Account Bound. Double-click to open the
        // Mystic Forge" - the footnote first and the name last, when the name is the
        // thing she asked for. Nearness picks which lines belong to what she is
        // pointing at; the page then reads down it the way it is written.
        var kept = ranked.Where(r => r.Dist <= nearest + Band)
                         .Where(r => r.Box.Right > left && r.Box.Left < right)   // same column
                         .OrderBy(r => Math.Round(r.Box.Top, 3))
                         .ThenBy(r => r.Box.Left)
                         .Select(r => r.Text)
                         .ToList();

        // The same guild tag over three people's heads is one answer, not three.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        kept = kept.Where(t => seen.Add(t.Trim())).ToList();

        if (kept.Count < lines.Count)
            DiagLog.Log("HOVER", $"nearest {kept.Count} of {lines.Count} line(s) to the pointer");
        return kept;
    }


    /// <summary>Every hover utterance is labelled, so moving the pointer can stop THIS
    /// and nothing else.</summary>
    private Task SayHover(string text) =>
        _tts.SpeakAsync(text, tag: HoverTag);

    /// <summary>The label on hover speech.</summary>
    public const string HoverTag = "hover";

    /// <summary>
    /// The read in flight. Each new hover cancels the one before it.
    ///
    /// Recognition takes a few hundred milliseconds and the pointer moves far more often
    /// than that. Without this, the reader would finish a picture of whatever Quinn was
    /// pointing at half a second ago and say it aloud with complete confidence - which
    /// is worse than silence, because it sounds like an answer to the question she is
    /// asking now.
    /// </summary>
    private CancellationTokenSource? _inFlight;

    public async Task ReadAsync(bool enhance = false)
    {
        var previous = Interlocked.Exchange(ref _inFlight, new CancellationTokenSource());
        try { previous?.Cancel(); previous?.Dispose(); } catch { }
        var ct = _inFlight!.Token;

        try
        {
            if (!GetCursorPos(out var p))
            {
                await SayHover("I couldn't find the mouse pointer.");
                return;
            }

            // HOVER TARGETING: OPENCV + RAPIDOCR, when it is switched on. One object under the
            // pointer, with its own tooltip and price, decided by the same code HoverReplay
            // scores against the recorded corpus. If it cannot run on this machine it says so
            // in the log once and the classic reader below carries on.
            //
            // Every hover logs which path answered it (TARGETING=new / classic), and a fallback
            // from the new targeting to the classic reader always logs its reason.
            if (App.Settings.Current.HoverTargetingFusion)
            {
                var fallback = Hover.FusionHoverReader.Unavailable
                    ? "the OpenCV + RapidOCR engine could not load on this machine: " + Hover.FusionHoverReader.UnavailableReason
                    : await ReadWithFusionAsync(p.X, p.Y, auto: enhance, ct);
                if (fallback == null) return;
                Hover.HoverTargeting.FellBack(fallback);
            }
            else Hover.HoverTargeting.AnsweredByClassic();

            // Bias the box DOWN-RIGHT of the cursor, because that's where the
            // game puts tooltips, but keep some margin above/left so a tooltip
            // flipped to the other side is still caught.
            var (boxW, boxH, insetX, insetY) = BoxFor(p.X, p.Y);
            int x = p.X - insetX;
            int y = p.Y - insetY;

            // CLAMP IN THE SAME UNITS THE POINTER IS MEASURED IN.
            //
            // GetCursorPos gives physical pixels; SystemParameters gives DIPs. Clamping
            // one against the other capped the box at the DIP width, so on Quinn's
            // 2560x1440 screen at 125% it could never be placed past x=2048 or y=1152.
            // The outer fifth of the screen - the inventory, the gold counter - was
            // invisible to it, and pointing there pinned the box to a fixed spot and
            // read out whatever was underneath. See ScreenMetrics.
            //
            // Clamped to the monitor under the pointer rather than the whole desktop,
            // so on two screens the box never straddles the gap between them.
            var mon = ScreenMetrics.MonitorAt(p.X, p.Y);
            int mx = (int)mon.Left, my = (int)mon.Top;

            x = Math.Clamp(x, mx, Math.Max(mx, mx + (int)mon.Width - boxW));
            y = Math.Clamp(y, my, Math.Max(my, my + (int)mon.Height - boxH));

            var png = ScreenCaptureService.CapturePng(x, y, boxW, boxH);
            BugRecorderService.NoteHoverGrab(png!);   // full size, for tuning off real data
            if (png == null)
            {
                await SayHover("I couldn't capture that part of the screen.");
                return;
            }

            // KEEP THE GOOD LINES; DROP THE BAD ONES.
            //
            // Judging the whole read at once was wrong, and it is why hover kept saying
            // nothing. A tooltip is drawn OVER the 3-D world and the box round the cursor
            // catches both, so a read looks like this:
            //
            //     al NOR Sed NS
            //     Double-click to view his location.
            //     w 7 7 [ag
            //
            // One real line and two of hillside. Scored as one blob the noise drags it
            // under the bar and the whole thing is binned - including the sentence Quinn
            // actually wanted. Every one of the thirteen reads in her last recording went
            // that way, and four of them held real tooltip text.
            //
            // OCR already returns lines, so they are judged one at a time and whatever
            // survives is read out.
            // WINDOWS OCR FIRST FOR HOVER. THIS ORDER IS MEASURED, NOT ASSUMED.
            //
            // Frame 97 of Quinn's recording holds a tooltip legible enough to
            // transcribe by hand, so both engines could be scored against the real
            // words for once. Reading the same grab:
            //
            //     620x420 box   windows 47%   tesseract 56%
            //     880x640 box   windows 72%   tesseract  0%
            //
            // Enlarging the box to fit a whole tooltip - which it had to be, because
            // lines were coming back cut off mid-word - rescued Windows OCR and
            // destroyed Tesseract. With more of the inventory beside the tooltip in
            // shot, Tesseract's layout analysis picks the ICON GRID as the page and
            // reads that: "5 2 BE RE 151 5 it : Pills 3 sc 858". Windows OCR, trained
            // on photographs where text sits among other things, finds the tooltip.
            //
            // And only Windows OCR ever returned the item's NAME -
            // "Runecrafter's Salvage-o-Matic" - at either size. The name is the single
            // most useful thing in a tooltip.
            //
            // Tesseract still reads a clean block of prose more accurately, so it stays
            // as the fallback for when this finds nothing. It is also still the engine
            // for the chat panel, which IS a clean block.
            var upscale = UpscaleForText();
            DiagLog.Log("HOVER", $"pointer {p.X},{p.Y}  physical capture {boxW}x{boxH} at ({x},{y})  " +
                                 $"adaptive upscale {upscale}x  " +
                                 $"(measured text height {_textHeight:F0} px)");

            // ONE READER, ONE FALLBACK.
            //
            // RapidOCR first, because on Quinn's own captures it found half again as
            // many lines as Windows OCR with the lowest junk rate of the three engines
            // tried, and returned "[HALO]" correctly every single time after a week of
            // "[HBLO]" and ".1KAL0]". Windows OCR only when RapidOCR came back with
            // nothing at all - which on a machine that cannot run ONNX is every time, so
            // the reader still works there.
            //
            // Tesseract is gone from this path. It came last in every column of the
            // comparison and its habit of returning confident nonsense for a patch of
            // scenery is what several rounds of filtering were trying to survive.
            var boxes = await ReadBoxesAsync(png, upscale, boxW, boxH, ct);
            var lines = boxes.Select(b => b.Text).ToList();

            // SHADOW MODE. THIS WATCHES; IT DOES NOT SPEAK.
            //
            // I put this into the live path having written, in the same commit, that I
            // could not verify it - the replay corpus is single captures and this needs
            // pairs. It then replaced results that had been working. That was the wrong
            // call twice over: shipping an unverified mechanism ahead of a verified one,
            // and letting a thing I could not test take the microphone.
            //
            // So it runs, it logs what it would have said, and the bug recorder saves
            // the before and after frames that make it testable offline. It goes back
            // into the live path when - and only when - replay cases pass.
            _ = ShadowTooltipAsync(mon, upscale, ct);

            // IS THE POINTER IN A REWARD CARD?            // IS THE POINTER IN A REWARD CARD?
            //
            // Asked before anything else, because a card answers as a whole. Quinn can
            // find three hundred pixels of artwork; she cannot land on the twelve-pixel
            // caption under it, and every "th weapon skin" and "etle mount skin" in her
            // reports is the caption of whichever card happened to clip the edge of the
            // grab. The card the pointer is INSIDE is the one that speaks.
            var card = await FindVaultCardAsync(boxes, insetX, insetY, x, y, boxW, boxH, upscale, ct);
            if (card != null)
            {
                if (card.SameAs(_lastCard))
                {
                    // Still inside the same reward. Moving from the picture to the name
                    // to the price is one person looking at one thing, not three
                    // questions, and repeating it is how a reader becomes exhausting.
                    DiagLog.Log("HOVER", $"same card, staying quiet: {card.Name}");
                    return;
                }

                _lastCard = card;
                DiagLog.Log("HOVER", $"vault card: {card.Spoken}");
                _lastSpoken = card.Spoken;
                await SayHover(card.Spoken);
                return;
            }
            _lastCard = null;

            // WHICH of those lines is the pointer on? Answered by layout, in one place,
            // by code the replay harness runs too - see Services/Hover/HoverTarget.cs.
            var pick = Hover.HoverTarget.Select(boxes, insetX, insetY);
            DiagLog.Log("HOVER", $"{pick.Mode}: {pick.Reason}; " +
                                 $"kept {pick.Chosen.Count}, dropped {pick.Rejected.Count}");

            // The filters live in Services/Hover/HoverText.cs so the replay harness runs them too.
            var kept = Hover.HoverText.FilterForSpeech(pick.Lines);
            var raw = string.Join(Environment.NewLine, lines);
            var text = Hover.HoverText.JoinForSpeech(kept);

            // A SCRAP IS NOT A READING.
            //
            // Layout analysis mostly goes quiet on artwork, but a grid of item icons
            // can still leave one torn syllable behind - a real read of Quinn's
            // inventory upgrades panel came back as the single word "Recru". Saying
            // that aloud is worse than saying nothing: it sounds like an answer.
            // Ten characters clears every genuine short read there is, including the
            // gold at the bottom of the inventory ("208g 14s 32c") and a bare item
            // name, while a torn fragment falls short.
            // Ten characters was meant to catch a torn syllable - a read of the
            // upgrades panel once came back as the single word "Recru". It also caught
            // "[HALO]" twenty times in one session: Quinn's own guild tag, read
            // perfectly, binned for being six letters long. Along with "-Build",
            // "/Turret" and "Day". With the pointer-distance ranking and per-line word
            // test both in place, a stray fragment is rare and an unread landmark is
            // not - so this is now only a floor against single stray letters.
            const int MinReadable = 4;
            if (kept.Count == 0 || text.Length < MinReadable)
            {
                DiagLog.Log("HOVER", $"nothing readable in {lines.Count} line(s) " +
                                     "" +
                                     (kept.Count > 0 ? $" - only a scrap: \"{text}\"" : ""));
                if (!enhance) await SayHover("Nothing readable under the pointer.");
                return;
            }
            DiagLog.Log("HOVER", $"kept {kept.Count} of {lines.Count} line(s): " +
                                 (text.Length > 80 ? text[..80] : text));

            // NO WIKI LOOKUP.
            //
            // The idea was decent - read the item's name, fetch the real description,
            // speak that instead of raw OCR. It never once worked. Across Quinn's last
            // session: 49 lookups, 0 matches, a median of 101 ms each and 8.1 seconds
            // of waiting in total, on a reader whose whole point is answering
            // immediately. It failed because the name came from the first line of a
            // box that was in the wrong place, so it was searching the wiki for
            // "ll lh WSU" and "oe WB".
            //
            // With the box aimed properly the OCR text IS the item description, which
            // is what the wiki was being asked to supply. Nothing to add, so nothing
            // to wait for.
            // NO COHERENCE TEST. A GAME INTERFACE NAMES THINGS.
            //
            // LooksCoherent asked for at least three well-formed words before it would
            // let anything be spoken, which is a fair description of prose and a
            // terrible description of a game. Everything Quinn most needs to hear in
            // World versus World is one or two words, and the log shows every one of
            // them being read perfectly and then thrown away:
            //
            //     Stonemist Castle        Eternal Battlegrounds
            //     Fortified Gate          Valley Waypoint
            //     Consumable              Handiworker
            //
            // It was written to suppress the dense-panel jumble, back when the reader
            // grabbed a fixed box and handed the whole thing to one OCR call. That job
            // now belongs to layout analysis, to ranking lines by distance from the
            // pointer, and to judging each line for words on its own. Three filters
            // doing the same work, and this is the one that cannot tell a proper noun
            // from noise.

            if (Hover.HoverText.SameReading(text, _lastSpoken))
            {
                if (!enhance) await SayHover(text);  // manual repeat; auto stays quiet
                return;
            }

            _lastSpoken = text;
            await SayHover(text);
        }
        catch (OperationCanceledException)
        {
            // The pointer moved on. Nothing to say, and nothing went wrong.
        }
        catch (Exception ex)
        {
            CrashLogger.Log("CursorReader.ReadAsync", ex);
            await SayHover("Something went wrong reading the screen.");
        }
    }



    private static double Overlap(string x, string y)
    {
        var wx = x.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wy = y.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (wx.Count == 0) return 0;
        return (double)wx.Intersect(wy).Count() / wx.Count;
    }

}

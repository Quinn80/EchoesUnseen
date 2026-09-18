using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>
/// What Quinn hears, per contract.
///
///   Wizard's Vault   Name. [description]. Price: 1,200 Astral Acclaim. 1 Available.
///   Gem Store        Name. [description]. Price: 320 gems. Normally 400 gems.
///   Trading Post     Name. [description]. Price: 38 gold, 67 silver, 53 copper.
///   Merchant         Item Name. [tooltip description]. Price: 15 silver, 36 copper.
///   Inventory        Name. 19 in stack. [tooltip].
///   Button           Accept. Button.
///   Dialog option    I need some armor. Dialog option.
///
/// Name first, always. Said once: a tooltip title that repeats the name is dropped, and so
/// are tooltip lines that only repeat the price the card already announced.
/// </summary>
public static class SpeechComposer
{
    public static string Compose(AccessibleTarget t)
    {
        if (t.Type == TargetType.None || string.IsNullOrWhiteSpace(t.Name) && t.TooltipLines.Count == 0)
            return "";

        var name = TextLines.Clean(t.Name ?? "");
        if (t.Type == TargetType.Button) return Join(new[] { name, "Button." });
        if (t.Type == TargetType.DialogOption) return Join(new[] { name, "Dialog option." });

        var parts = new List<string>();
        if (name.Length > 0) parts.Add(name);

        if (t.StackCount is int n && n > 1) parts.Add($"{n} in stack");

        var body = TooltipBody(t, name);

        switch (t.Type)
        {
            case TargetType.MenuRow:
            case TargetType.Label:
            case TargetType.WorldLabel:
                parts.AddRange(t.Details);
                parts.AddRange(body);
                break;

            case TargetType.InventorySlot:
            case TargetType.IconTooltip:
                parts.AddRange(body);
                parts.AddRange(t.Details);
                break;

            default: // cards and merchant rows: description, then price, then availability
                parts.AddRange(body);
                if (!string.IsNullOrWhiteSpace(t.Price)) parts.Add($"Price: {t.Price}");
                if (!string.IsNullOrWhiteSpace(t.NormalPrice)) parts.Add($"Normally {t.NormalPrice}");
                parts.AddRange(t.Details);
                break;
        }
        return Join(parts);
    }

    private static readonly Regex PriceOnly = new(@"^\[?\s*[\d,\.]+\s*(gems?|gem)?\s*\]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<string> TooltipBody(AccessibleTarget t, string name)
    {
        var said = new List<string> { name };
        said.AddRange(t.Details);
        foreach (var raw in t.TooltipLines)
        {
            var line = TextLines.Clean(raw);
            if (line.Length == 0) continue;
            var (bare, _) = TextLines.SplitCount(line);
            if (TextLines.Similarity(bare, name) >= 0.82) continue;           // the title again
            if (PriceOnly.IsMatch(line)) continue;                             // "[560 Gems]"
            if (t.Price != null && Regex.IsMatch(line, @"^\s*\d") && TextLines.Letters(line) <= 5) continue;
            if (said.Any(s => TextLines.Similarity(s, line) >= 0.9)) continue;
            said.Add(line);
            yield return line;
        }
    }

    private static string Join(IEnumerable<string> parts) =>
        HoverText.JoinForSpeech(parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

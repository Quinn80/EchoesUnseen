using System.Collections.Generic;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>The accessibility contract the object under the pointer is read under.</summary>
public enum TargetType
{
    None,
    Label,
    WorldLabel,
    MenuRow,
    MerchantHorizontalItem,
    TradingPostCard,
    InventorySlot,
    IconTooltip,
    WizardVaultCard,
    GemStoreCard,
    DialogOption,
    Button,
}

/// <summary>
/// ONE HOVER = ONE ACCESSIBLE OBJECT.
///
/// The object physically under the pointer (its bounds, name, price, stack count) and,
/// separately, the tooltip that object produced - which may be anywhere on screen - joined
/// into one thing to say. Coordinates are in the picture's pixels; ScreenPicture converts.
/// </summary>
public sealed class AccessibleTarget
{
    public TargetType Type { get; set; }

    /// <summary>What the object is called - always spoken first.</summary>
    public string? Name { get; set; }

    public Box TargetBounds { get; set; }

    /// <summary>Other text belonging to the object itself (a row's progress, a card's availability).</summary>
    public List<string> Details { get; } = new();

    /// <summary>Spoken price, already in words: "15 silver, 36 copper", "560 gems".</summary>
    public string? Price { get; set; }

    /// <summary>The price before a sale, when the card shows one.</summary>
    public string? NormalPrice { get; set; }

    public int? StackCount { get; set; }

    public Box TooltipBounds { get; set; }
    public string? TooltipTitle { get; set; }
    public List<string> TooltipLines { get; } = new();
    public string? TooltipText => TooltipLines.Count == 0 ? null : string.Join(" ", TooltipLines);

    public double Confidence { get; set; }

    /// <summary>The whole thing to say, composed per contract.</summary>
    public string Speech { get; set; } = "";

    /// <summary>False when this is the same object as last time and nothing about it changed:
    /// moving around inside one card must not repeat it.</summary>
    public bool ShouldSpeak { get; set; } = true;

    public Dictionary<string, double> TimingsMs { get; } = new();
    public List<string> Reasons { get; } = new();
    public List<(Box Box, string Kind, string Label)> Debug { get; } = new();
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>
/// An axis-aligned rectangle in doubles. The fusion code works in the pixels of whatever
/// picture it was handed; <see cref="ScreenPicture"/> converts to physical screen pixels
/// at the edges, so a number never crosses a boundary it was not measured for.
/// </summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CentreX => X + W / 2;
    public double CentreY => Y + H / 2;
    public double Area => Math.Max(0, W) * Math.Max(0, H);
    public bool IsEmpty => W <= 0 || H <= 0;

    public static Box FromLTRB(double l, double t, double r, double b) => new(l, t, r - l, b - t);

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Contains(Box o, double slack = 0) =>
        o.X >= X - slack && o.Y >= Y - slack && o.Right <= Right + slack && o.Bottom <= Bottom + slack;

    public Box Intersect(Box o)
    {
        var l = Math.Max(X, o.X); var t = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right); var b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? FromLTRB(l, t, r, b) : default;
    }

    public Box Union(Box o) => IsEmpty ? o : o.IsEmpty ? this
        : FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    public Box Inflate(double dx, double dy) => new(X - dx, Y - dy, W + 2 * dx, H + 2 * dy);

    public Box Scale(double k) => new(X * k, Y * k, W * k, H * k);

    public Box Offset(double dx, double dy) => new(X + dx, Y + dy, W, H);

    public Box Clip(Box bounds) => Intersect(bounds);

    public double IoU(Box o)
    {
        var i = Intersect(o).Area;
        var u = Area + o.Area - i;
        return u <= 0 ? 0 : i / u;
    }

    /// <summary>Share of THIS box covered by <paramref name="o"/>.</summary>
    public double CoveredBy(Box o) => Area <= 0 ? 0 : Intersect(o).Area / Area;

    /// <summary>Horizontal overlap as a share of the narrower box.</summary>
    public double HorizontalOverlap(Box o)
    {
        var ov = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        return ov <= 0 ? 0 : ov / Math.Max(1, Math.Min(W, o.W));
    }

    /// <summary>Vertical overlap as a share of the shorter box.</summary>
    public double VerticalOverlap(Box o)
    {
        var ov = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return ov <= 0 ? 0 : ov / Math.Max(1, Math.Min(H, o.H));
    }

    /// <summary>Distance from a point to the nearest edge; zero inside.</summary>
    public double DistanceTo(double x, double y)
    {
        var dx = x < X ? X - x : x > Right ? x - Right : 0;
        var dy = y < Y ? Y - y : y > Bottom ? y - Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"[{X:F0},{Y:F0} {W:F0}x{H:F0}]";

    public static Box Bounding(IEnumerable<Box> boxes)
    {
        Box acc = default;
        foreach (var b in boxes) acc = acc.IsEmpty ? b : acc.Union(b);
        return acc;
    }
}

/// <summary>A line of text with its rectangle, in picture pixels.</summary>
public readonly record struct TextLine(string Text, Box Box, double Confidence, double[]? CharX = null)
{
    public double H => Box.H;
}

/// <summary>
/// A picture of (part of) the screen and how it maps onto physical screen pixels.
///
/// A live capture is 1 picture pixel per physical pixel. A recorded bug-report frame is
/// 1600x900 of a 2560x1440 screen, so 0.625. Everything the targeting code measures is
/// in picture pixels and proportional to the height of the text it finds, so the same
/// rules hold for both - and for any player's interface size and display scaling.
/// </summary>
public sealed class ScreenPicture
{
    public ScreenPicture(double originX, double originY, double scale, int width, int height)
    {
        OriginX = originX; OriginY = originY; Scale = scale; Width = width; Height = height;
    }

    /// <summary>Physical screen position of the picture's top-left pixel.</summary>
    public double OriginX { get; }
    public double OriginY { get; }

    /// <summary>Picture pixels per physical pixel.</summary>
    public double Scale { get; }

    public int Width { get; }
    public int Height { get; }

    public Box Bounds => new(0, 0, Width, Height);

    public (double X, double Y) ToPicture(double physX, double physY) =>
        ((physX - OriginX) * Scale, (physY - OriginY) * Scale);

    public Box ToPicture(Box phys) =>
        new((phys.X - OriginX) * Scale, (phys.Y - OriginY) * Scale, phys.W * Scale, phys.H * Scale);

    public Box ToPhysical(Box pic) =>
        new(pic.X / Scale + OriginX, pic.Y / Scale + OriginY, pic.W / Scale, pic.H / Scale);
}

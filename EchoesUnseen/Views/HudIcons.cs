using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EchoesUnseen.Views;

/// <summary>
/// Crisp vector icons for the 12 HUD gems, drawn to match the thin single-weight
/// line style used on the Echoes of Tyria website (Lucide/Feather-family look).
///
/// WHY VECTOR INSTEAD OF EMOJI:
///   * Emoji glyphs render differently on every machine, can't be recolored,
///     and don't scale crisply. Vector Paths look identical everywhere, take
///     any brush (so the animated fire glow works), and stay sharp at any size.
///   * Placement is pure geometry, so every icon sits dead-center on its gem.
///
/// STYLE:
///   * 24×24 design grid (same as the website icon set), stroked not filled.
///   * Round line caps/joins, ~2px stroke — the "outline" aesthetic.
///   * The caller supplies the Stroke brush (an animated fire gradient) and the
///     glow, so this file only defines SHAPE.
/// </summary>
public static class HudIcons
{
    /// <summary>
    /// Build the icon shape for a panel id as a WPF element sized to <paramref name="box"/>.
    /// The returned element is stroked with <paramref name="stroke"/> and has no fill,
    /// matching the website's line-icon look. Geometry is authored on a 24×24 grid
    /// and scaled to the requested box.
    /// </summary>
    public static FrameworkElement Build(string panelId, double box, Brush stroke)
    {
        double s = box / 24.0;             // scale factor from the 24-grid
        double sw = 2.0;                    // stroke width on the 24-grid

        var canvas = new Canvas
        {
            Width = box,
            Height = box,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        void Add(Geometry g, bool filled = false)
        {
            var p = new Path
            {
                Data = g,
                Stroke = stroke,
                StrokeThickness = sw,   // stroke authored on the 24-grid; scaled by RenderTransform below
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = filled ? stroke : null,
                IsHitTestVisible = false,
                // Scale the whole Path (geometry + stroke) from the origin up to
                // the requested box. We scale the ELEMENT, not the geometry:
                // Geometry.Parse returns a frozen StreamGeometry whose .Transform
                // is immutable, so assigning geometry.Transform silently fails.
                // RenderTransform on the Path always works.
                RenderTransform = new ScaleTransform(s, s),
            };
            canvas.Children.Add(p);
        }

        switch (panelId)
        {
            case "screen-reader": // eye outline + pupil
                Add(Geometry.Parse("M2,12 C4.5,6.5 9,4 12,4 C15,4 19.5,6.5 22,12 C19.5,17.5 15,20 12,20 C9,20 4.5,17.5 2,12 Z"));
                Add(new EllipseGeometry(new Point(12, 12), 3, 3));
                break;

            case "heart-quest": // heart
                Add(Geometry.Parse("M12,21 C12,21 3,14.5 3,8.5 C3,5.5 5.2,3.5 7.8,3.5 C9.6,3.5 11.1,4.6 12,6 C12.9,4.6 14.4,3.5 16.2,3.5 C18.8,3.5 21,5.5 21,8.5 C21,14.5 12,21 12,21 Z"));
                break;

            case "trail-nav": // folded map
                Add(Geometry.Parse("M9,4 L3,6.5 L3,20 L9,17.5 L15,20 L21,17.5 L21,4 L15,6.5 L9,4 Z"));
                Add(Geometry.Parse("M9,4 L9,17.5"));
                Add(Geometry.Parse("M15,6.5 L15,20"));
                break;

            case "chat-reader": // speech bubble
                Add(Geometry.Parse("M4,4 L20,4 C21,4 21,5 21,5 L21,15 C21,16 20,16 20,16 L9,16 L5,20 L5,16 L4,16 C3,16 3,15 3,15 L3,5 C3,4 4,4 4,4 Z"));
                break;

            case "voice-chat": // waveform bars
                Add(Geometry.Parse("M4,10 L4,14"));
                Add(Geometry.Parse("M8,7 L8,17"));
                Add(Geometry.Parse("M12,4 L12,20"));
                Add(Geometry.Parse("M16,7 L16,17"));
                Add(Geometry.Parse("M20,10 L20,14"));
                break;

            case "music": // double music note
                Add(Geometry.Parse("M9,18 L9,6 L20,4 L20,16"));
                Add(new EllipseGeometry(new Point(6.5, 18), 2.5, 2.5));
                Add(new EllipseGeometry(new Point(17.5, 16), 2.5, 2.5));
                break;

            case "assistant": // microphone
                Add(Geometry.Parse("M12,3 C10.3,3 9,4.3 9,6 L9,11 C9,12.7 10.3,14 12,14 C13.7,14 15,12.7 15,11 L15,6 C15,4.3 13.7,3 12,3 Z"));
                Add(Geometry.Parse("M6,11 C6,14.3 8.7,17 12,17 C15.3,17 18,14.3 18,11"));
                Add(Geometry.Parse("M12,17 L12,21"));
                Add(Geometry.Parse("M9,21 L15,21"));
                break;

            case "account": // magnifying glass
                Add(new EllipseGeometry(new Point(10.5, 10.5), 6.5, 6.5));
                Add(Geometry.Parse("M15.5,15.5 L21,21"));
                break;

            case "trading": // upward trend line + arrow head
                Add(Geometry.Parse("M3,17 L9,11 L13,15 L21,7"));
                Add(Geometry.Parse("M15,7 L21,7 L21,13"));
                break;

            case "build": // stacked layers (shield-alternative from site: layers)
                Add(Geometry.Parse("M12,3 L21,8 L12,13 L3,8 L12,3 Z"));
                Add(Geometry.Parse("M3,12 L12,17 L21,12"));
                Add(Geometry.Parse("M3,16 L12,21 L21,16"));
                break;

            case "map": // map-completion: sparkles
                Add(Geometry.Parse("M12,3 L13.8,9.2 L20,11 L13.8,12.8 L12,19 L10.2,12.8 L4,11 L10.2,9.2 L12,3 Z"));
                Add(Geometry.Parse("M18.5,3.5 L19.3,5.7 L21.5,6.5 L19.3,7.3 L18.5,9.5 L17.7,7.3 L15.5,6.5 L17.7,5.7 L18.5,3.5 Z"));
                break;

            case "settings": // gear
                Add(Geometry.Parse("M19.4,13 C19.5,12.7 19.5,12.3 19.5,12 C19.5,11.7 19.5,11.3 19.4,11 L21.2,9.6 C21.4,9.5 21.4,9.2 21.3,9 L19.6,6 C19.5,5.8 19.2,5.7 19,5.8 L16.9,6.6 C16.4,6.3 15.9,6 15.4,5.8 L15.1,3.6 C15,3.4 14.8,3.2 14.6,3.2 L11,3.2 C10.8,3.2 10.6,3.4 10.5,3.6 L10.2,5.8 C9.7,6 9.2,6.3 8.7,6.6 L6.6,5.8 C6.4,5.7 6.1,5.8 6,6 L4.3,9 C4.2,9.2 4.2,9.5 4.4,9.6 L6.2,11 C6.1,11.3 6.1,11.7 6.1,12 C6.1,12.3 6.1,12.7 6.2,13 L4.4,14.4 C4.2,14.5 4.2,14.8 4.3,15 L6,18 C6.1,18.2 6.4,18.3 6.6,18.2 L8.7,17.4 C9.2,17.7 9.7,18 10.2,18.2 L10.5,20.4 C10.6,20.6 10.8,20.8 11,20.8 L14.6,20.8 C14.8,20.8 15,20.6 15.1,20.4 L15.4,18.2 C15.9,18 16.4,17.7 16.9,17.4 L19,18.2 C19.2,18.3 19.5,18.2 19.6,18 L21.3,15 C21.4,14.8 21.4,14.5 21.2,14.4 L19.4,13 Z"));
                Add(new EllipseGeometry(new Point(12.8, 12), 3, 3));
                break;

            default: // fallback dot
                Add(new EllipseGeometry(new Point(12, 12), 4, 4));
                break;
        }

        return canvas;
    }
}

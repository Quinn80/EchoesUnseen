using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using EchoesUnseen.Models;
using EchoesUnseen.Services;

namespace EchoesUnseen.Views;

/// <summary>
/// Draws imported marker-pack points on screen as big, high-contrast, animated
/// markers — projecting each point's 3-D world position onto the screen using
/// MumbleLink's camera. This is the accessible visual overlay TacO/Blish don't do
/// well: fully colour / size / animation customisable, and a marker vanishes once
/// you reach (unlock) it.
/// </summary>
public partial class MarkerOverlay : UserControl
{
    /// <summary>20 vivid, high-contrast marker colours.</summary>
    public static readonly (string Name, Color Color)[] Colors =
    {
        ("Magenta", Color.FromRgb(0xFF,0x1A,0x8A)), ("Neon Cyan", Color.FromRgb(0x00,0xE5,0xFF)),
        ("Electric Lime", Color.FromRgb(0x76,0xFF,0x03)), ("Gold", Color.FromRgb(0xFF,0xC4,0x00)),
        ("Vivid Red", Color.FromRgb(0xFF,0x17,0x44)), ("Neon Orange", Color.FromRgb(0xFF,0x6D,0x00)),
        ("Violet", Color.FromRgb(0xD5,0x00,0xF9)), ("Electric Blue", Color.FromRgb(0x29,0x79,0xFF)),
        ("Spring Green", Color.FromRgb(0x00,0xE6,0x76)), ("Hot Pink", Color.FromRgb(0xFF,0x4D,0xD2)),
        ("White", Color.FromRgb(0xFF,0xFF,0xFF)), ("Aqua", Color.FromRgb(0x18,0xFF,0xD0)),
        ("Yellow", Color.FromRgb(0xFF,0xF0,0x30)), ("Coral", Color.FromRgb(0xFF,0x5C,0x5C)),
        ("Purple", Color.FromRgb(0x9C,0x27,0xFF)), ("Sky", Color.FromRgb(0x5C,0xC8,0xFF)),
        ("Mint", Color.FromRgb(0x6B,0xFF,0xB0)), ("Amber", Color.FromRgb(0xFF,0xA7,0x26)),
        ("Rose", Color.FromRgb(0xFF,0x2D,0x6F)), ("Chartreuse", Color.FromRgb(0xC6,0xFF,0x00)),
    };

    /// <summary>20 animation styles (the index maps to a behaviour in ApplyAnimation).</summary>
    public static readonly string[] Animations =
    {
        "Steady", "Pulse", "Fast Pulse", "Slow Breathe", "Blink", "Fast Blink", "Glow",
        "Spin Ring", "Expand Ring", "Sonar", "Bounce", "Wobble", "Heartbeat", "Flash",
        "Shimmer", "Grow", "Ping", "Twinkle", "Throb", "Beacon",
    };

    private MumbleLinkReader? _mumble;
    private readonly TacoService _taco = new();
    private DispatcherTimer? _timer;

    public MarkerOverlay() { InitializeComponent(); }

    public void AttachServices(MumbleLinkReader? mumble)
    {
        _mumble = mumble;
        _taco.Load();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
    }

    /// <summary>Re-read markers.json after a new pack was imported.</summary>
    public void ReloadMarkers() => _taco.Load();

    private static string VisitKey(int mapId, TacoService.Marker m) => $"{mapId}|{m.X:F0}|{m.Z:F0}|{m.Category}";

    private void Render()
    {
        Root.Children.Clear();
        var s = App.Settings.Current;
        if (_mumble == null) return;

        // If you've picked a guide, it draws. Full stop.
        //
        // This used to need TWO separate toggles switched on as well, in different
        // places — which is exactly how Quinn ended up staring at an empty screen with
        // the drawing silently disabled. Choosing a guide IS the intent to see it.
        bool wantTrail = TrailFollower.Current != null;
        if (!s.MarkerVisualEnabled && !wantTrail) return;

        var data = _mumble.Read();
        if (data == null) return;

        double sw = ActualWidth, sh = ActualHeight;
        if (sw < 10 || sh < 10) return;

        var cat = string.IsNullOrEmpty(s.MarkerVisualCategory) ? null : s.MarkerVisualCategory;
        var color0 = Colors[Math.Clamp(s.MarkerColorIndex, 0, Colors.Length - 1)].Color;

        // The painted TacO trail (.trl) you're actually following — drawn ON THE GROUND,
        // anchored to the world, so it stays put as you walk past. Only the chosen guide
        // is drawn: a map can hold dozens, and painting them all would be unreadable.
        if (wantTrail && TrailFollower.Current is { } activeTrail && activeTrail.MapId == data.MapId)
            DrawWorldTrails(new List<TacoService.Trail> { activeTrail }, data, sw, sh, color0);

        if (!s.MarkerVisualEnabled) return;   // trail-only mode: don't draw POI markers

        var markers = _taco.ForMap(data.MapId)
            .Where(m => cat == null || m.Category == cat)
            .Where(m => !s.VisitedMarkers.Contains(VisitKey(data.MapId, m)))
            .ToList();
        if (markers.Count == 0) return;

        var color = Colors[Math.Clamp(s.MarkerColorIndex, 0, Colors.Length - 1)].Color;
        double size = Math.Clamp(s.MarkerSize, 16, 240);
        int anim = Math.Clamp(s.MarkerAnimIndex, 0, Animations.Length - 1);

        // Decide the pack's units ONCE, from the largest coordinate on this map, not
        // per-marker. GW2 world metres for a whole map stay in the low thousands;
        // inch-based packs run to tens of thousands or more. Guessing per-marker (as
        // an earlier build did) scaled some points and not others, so markers didn't
        // sit where they belong — the "stuck / scattered" look. One decision fixes it.
        double maxCoord = 0;
        foreach (var m in markers) maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(m.X), Math.Abs(m.Z)));
        double unit = maxCoord > 15000 ? 39.3701 : 1.0;   // divisor: inches→metres, or 1

        var pts = new List<(Point p, double dist)>();
        foreach (var m in markers)
        {
            double dist = ProjectXYZ(m.X, m.Y, m.Z, data, unit, sw, sh, out double sx, out double sy, out bool vis);
            if (dist < 0 || !vis) continue;              // behind camera, or not in view
            if (dist < 8) { MarkVisited(data.MapId, m); continue; }   // reached → unlock/hide
            pts.Add((new Point(sx, sy), dist));
            DrawMarker(sx, sy, size, color, anim, dist);
        }

        // A trail line reads as a ROUTE only if it's ordered — nearest-first from you,
        // then hop to the next-closest — rather than a web drawn in file order.
        if (s.MarkerTrailLine && pts.Count >= 2) DrawTrail(OrderPath(pts), color);
    }

    /// <summary>Project a marker's world position to screen. <paramref name="unit"/> is
    /// the pack's coordinate divisor (39.3701 for inch packs, 1 for metre packs),
    /// decided once by the caller so every marker scales the same way. Returns the
    /// distance in metres, or -1 if it's behind the camera / off screen.</summary>
    private static double Project(TacoService.Marker m, MumbleLinkData d, double unit, double sw, double sh, out double sx, out double sy)
        => ProjectXYZ(m.X, m.Y, m.Z, d, unit, sw, sh, out sx, out sy);

    /// <summary>Project a raw world point (before unit scaling) to screen. Shared by
    /// markers and painted trails so both anchor to the ground identically.</summary>
    private static double ProjectXYZ(double wx, double wy, double wz, MumbleLinkData d, double unit, double sw, double sh, out double sx, out double sy)
        => ProjectXYZ(wx, wy, wz, d, unit, sw, sh, out sx, out sy, out _);

    /// <summary>
    /// Project a world point, reporting BEHIND-CAMERA and OFF-SCREEN separately.
    ///
    /// These are not the same thing and conflating them was a real bug. A point behind
    /// the camera has no valid projection — the maths inverts and must be rejected. A
    /// point merely outside the screen bounds is perfectly valid data that simply isn't
    /// in view; rejecting it made whole trail segments vanish whenever one corner
    /// crossed the screen edge, and made them flicker as the view turned.
    ///
    /// Returns the distance in metres, or -1 ONLY when the point is behind the camera.
    /// <paramref name="onScreen"/> reports visibility separately, so markers can cull on
    /// it while the trail ribbon ignores it and lets WPF clip.
    /// </summary>
    private static double ProjectXYZ(double wx, double wy, double wz, MumbleLinkData d, double unit, double sw, double sh, out double sx, out double sy, out bool onScreen)
    {
        onScreen = false;
        sx = sy = 0;
        double mx = wx / unit, my = wy / unit, mz = wz / unit;
        double tox = mx - d.CameraX, toy = my - d.CameraY, toz = mz - d.CameraZ;
        double len = Math.Sqrt(tox * tox + toy * toy + toz * toz);

        // Camera basis (GW2 MumbleLink is left-handed).
        double fx = d.CameraFrontX, fy = d.CameraFrontY, fz = d.CameraFrontZ;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl < 1e-4) return -1;
        fx /= fl; fy /= fl; fz /= fl;
        // right = up × front ; up = front × right
        double rx = 1 * fz - 0 * fy, ry = 0 * fx - 0 * fz, rz = 0 * fy - 1 * fx;   // (0,1,0)×F
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz); if (rl < 1e-4) return -1;
        rx /= rl; ry /= rl; rz /= rl;
        double ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;

        double depth = tox * fx + toy * fy + toz * fz;
        if (depth <= 0.3) return -1;                    // behind the camera
        double xc = tox * rx + toy * ry + toz * rz;
        double yc = tox * ux + toy * uy + toz * uz;

        double tanHalf = Math.Tan(Math.Clamp(d.Fov, 0.3f, 2.5f) / 2.0);
        double aspect = sw / sh;
        double ndcX = (xc / depth) / (tanHalf * aspect);
        double ndcY = (yc / depth) / tanHalf;
        onScreen = Math.Abs(ndcX) <= 1.15 && Math.Abs(ndcY) <= 1.15;

        // Clamp well outside the viewport rather than rejecting: off-screen points are
        // valid geometry, but a point close to the near plane can project to a colossal
        // coordinate, and handing WPF a polygon spanning millions of pixels is what
        // produced the giant diagonal wedges.
        sx = Math.Clamp((ndcX * 0.5 + 0.5) * sw, -4 * sw, 5 * sw);
        sy = Math.Clamp((0.5 - ndcY * 0.5) * sh, -4 * sh, 5 * sh);
        return len;
    }

    private void MarkVisited(int mapId, TacoService.Marker m)
    {
        var key = VisitKey(mapId, m);
        if (App.Settings.Current.VisitedMarkers.Contains(key)) return;
        App.Settings.Current.VisitedMarkers.Add(key);
        App.Settings.NotifyChanged();
    }

    // ── Drawing ───────────────────────────────────────────────────────────────
    private void DrawMarker(double sx, double sy, double size, Color color, int anim, double dist)
    {
        // Markers further away render a touch smaller so depth reads naturally.
        double scale = Math.Clamp(1.0 - dist / 4000.0, 0.45, 1.0);
        double sz = size * scale;

        var grid = new Grid { Width = sz, Height = sz, RenderTransformOrigin = new Point(0.5, 0.5) };
        var ring = new Ellipse
        {
            Stroke = new SolidColorBrush(color), StrokeThickness = Math.Max(2, sz * 0.10),
            Fill = Brushes.Transparent,
            Effect = new DropShadowEffect { Color = color, BlurRadius = sz * 0.6, ShadowDepth = 0, Opacity = 0.9 },
        };
        var dot = new Ellipse
        {
            Width = sz * 0.42, Height = sz * 0.42, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(color),
        };
        grid.Children.Add(ring);
        grid.Children.Add(dot);
        grid.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(1, 1), new RotateTransform(0), new TranslateTransform(0, 0) }
        };

        Canvas.SetLeft(grid, sx - sz / 2);
        Canvas.SetTop(grid, sy - sz / 2);
        Root.Children.Add(grid);

        ApplyAnimation(grid, ring, dot, anim, color, sz);
    }

    private void ApplyAnimation(Grid g, Ellipse ring, Ellipse dot, int anim, Color color, double sz)
    {
        var tg = (TransformGroup)g.RenderTransform;
        var scale = (ScaleTransform)tg.Children[0];
        var rot = (RotateTransform)tg.Children[1];
        var trans = (TranslateTransform)tg.Children[2];

        DoubleAnimation A(double from, double to, double ms, bool auto = true) => new(from, to, TimeSpan.FromMilliseconds(ms))
        { AutoReverse = auto, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        void Scale(double lo, double hi, double ms) { scale.BeginAnimation(ScaleTransform.ScaleXProperty, A(lo, hi, ms)); scale.BeginAnimation(ScaleTransform.ScaleYProperty, A(lo, hi, ms)); }
        void Fade(FrameworkElement el, double lo, double hi, double ms) => el.BeginAnimation(OpacityProperty, A(lo, hi, ms));
        void Spin(double ms) => rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(ms)) { RepeatBehavior = RepeatBehavior.Forever });

        switch (anim)
        {
            case 0: break;                                             // Steady
            case 1: Scale(0.85, 1.15, 900); break;                     // Pulse
            case 2: Scale(0.8, 1.2, 450); break;                       // Fast Pulse
            case 3: Scale(0.92, 1.08, 1800); break;                    // Slow Breathe
            case 4: Fade(g, 1, 0.25, 700); break;                      // Blink
            case 5: Fade(g, 1, 0.15, 300); break;                      // Fast Blink
            case 6: ring.Effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = sz, Opacity = 1 };
                    var glow = A(sz * 0.4, sz * 1.1, 800); ((DropShadowEffect)ring.Effect).BeginAnimation(DropShadowEffect.BlurRadiusProperty, glow); break; // Glow
            case 7: Spin(2600); break;                                 // Spin Ring
            case 8: AddExpandingRing(g, color, sz, 1400, 0); break;    // Expand Ring
            case 9: AddExpandingRing(g, color, sz, 1600, 0); AddExpandingRing(g, color, sz, 1600, 800); break; // Sonar
            case 10: trans.BeginAnimation(TranslateTransform.YProperty, A(-sz * 0.2, sz * 0.05, 600)); break;  // Bounce
            case 11: rot.BeginAnimation(RotateTransform.AngleProperty, A(-18, 18, 500)); break;                // Wobble
            case 12: HeartbeatScale(scale); break;                     // Heartbeat
            case 13: Fade(g, 1, 0.5, 180); Scale(0.95, 1.25, 180); break; // Flash
            case 14: Fade(dot, 1, 0.3, 900); Fade(ring, 0.6, 1, 900); break; // Shimmer
            case 15: Scale(0.6, 1.15, 1200); break;                    // Grow
            case 16: Scale(1.0, 1.35, 350); break;                     // Ping
            case 17: Fade(g, 1, 0.4, 250); break;                      // Twinkle
            case 18: Scale(0.9, 1.1, 700); Fade(g, 0.8, 1, 700); break; // Throb
            case 19: Fade(ring, 0.3, 1, 500); Scale(0.95, 1.1, 1000); AddExpandingRing(g, color, sz, 1800, 0); break; // Beacon
        }
    }

    private static void HeartbeatScale(ScaleTransform s)
    {
        var kf = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromMilliseconds(1100) };
        kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.0)));
        kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromPercent(0.12)));
        kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.24)));
        kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromPercent(0.36)));
        kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0.5)));
        var kf2 = kf.Clone();
        s.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, kf2);
    }

    private void AddExpandingRing(Grid g, Color color, double sz, double ms, double delayMs)
    {
        var r = new Ellipse
        {
            Width = sz * 0.5, Height = sz * 0.5, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Stroke = new SolidColorBrush(color),
            StrokeThickness = Math.Max(2, sz * 0.06), Fill = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var st = new ScaleTransform(0.4, 0.4);
        r.RenderTransform = st;
        g.Children.Add(r);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var grow = new DoubleAnimation(0.4, 2.2, TimeSpan.FromMilliseconds(ms)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        var fade = new DoubleAnimation(0.9, 0.0, TimeSpan.FromMilliseconds(ms)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        r.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Order projected points into a route: start at the one nearest you (in
    /// world metres), then repeatedly hop to the closest remaining point on screen.
    /// Turns a bag of markers into a line you can follow instead of a cat's cradle.</summary>
    private static List<Point> OrderPath(List<(Point p, double dist)> pts)
    {
        var remaining = new List<(Point p, double dist)>(pts);
        var route = new List<Point>();
        int startIdx = 0;
        for (int i = 1; i < remaining.Count; i++)
            if (remaining[i].dist < remaining[startIdx].dist) startIdx = i;
        var cur = remaining[startIdx].p;
        route.Add(cur);
        remaining.RemoveAt(startIdx);
        while (remaining.Count > 0)
        {
            int best = 0; double bestD = double.MaxValue;
            for (int i = 0; i < remaining.Count; i++)
            {
                double dx = remaining[i].p.X - cur.X, dy = remaining[i].p.Y - cur.Y;
                double d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
            cur = remaining[best].p;
            route.Add(cur);
            remaining.RemoveAt(best);
        }
        return route;
    }

    /// <summary>Draw painted .trl trails on the ground: project every vertex through the
    /// camera and connect consecutive on-screen points, breaking the line whenever a
    /// point goes behind the camera or off screen. World-anchored, so it lies on the
    /// dirt and stays put as you move — unlike the character-locked compass.</summary>
    private void DrawWorldTrails(List<TacoService.Trail> trails, MumbleLinkData data, double sw, double sh, Color color)
    {
        double maxCoord = 0;
        foreach (var t in trails)
            foreach (var seg in t.Segments)
                foreach (var p in seg)
                    maxCoord = Math.Max(maxCoord, Math.Max(Math.Abs(p[0]), Math.Abs(p[2])));
        double unit = maxCoord > 15000 ? 39.3701 : 1.0;

        double elevTol = Math.Clamp(App.Settings.Current.TrailElevationToleranceM, 2, 200);
        _lastFov = data.Fov; _lastScreenH = sh;

        // WHAT WE DRAW: the connected stretch of route in front of you.
        //
        // Not the whole trail. A guide can be four kilometres long, and handing all of
        // it to the renderer meant deciding point-by-point what was worth showing - a
        // job it did by comparing heights, which in a stacked city deleted 27% of the
        // path and punched a hole through the ribbon roughly once a frame. The follower
        // already knows where on the route you are, so it can hand over the stretch
        // that is genuinely walkable from where you stand. See TrailFollower.VisibleRun.
        var live = TrailFollower.Live;
        if (live is { HasTrail: true })
        {
            var route = live.VisibleRun(
                Math.Clamp(App.Settings.Current.TrailDrawAheadM, 25, 600),
                TrailFollower.DrawBehindM, out int youAt);

            if (route.Count >= 2)
            {
                if (App.Settings.Current.TrailUse3D)
                    Draw3DRibbon(route, data, unit, sw, sh, color);
                else
                {
                    ClearScene();
                    DrawGroundRibbonRaw(route, data, unit, sw, sh, color);
                }
                LogTrailDraw(data, route, unit, youAt);
                return;
            }
        }

        // We have not found our place on the route yet (just picked a guide, or standing
        // far off it). Fall back to the old whole-trail draw so something shows.
        ClearScene();
        foreach (var t in trails)
            foreach (var seg in t.Segments)
                DrawGroundRibbon(seg, data, unit, sw, sh, color, elevTol);
    }

    // -- The trail as real 3-D geometry ---------------------------------------
    //
    // The flat renderer projects every corner by hand, which forces two compromises it
    // cannot escape: a point behind the camera has no projection and must be dropped,
    // and a point just past the near plane projects to a colossal coordinate that has
    // to be clamped. Dropping is what made the ribbon pop in and out while turning;
    // clamping is what produced the giant diagonal wedges.
    //
    // Handing the geometry to WPF as an actual mesh removes both. WPF clips triangles at
    // the near plane properly - a segment running off behind you is CUT at the right
    // place rather than dropped - and depth-sorts the ribbon against itself.

    private readonly Dictionary<int, ImageBrush> _chevronCache = new();
    private Color _chevronColor;

    private void ClearScene()
    {
        if (Scene.Children.Count > 0) Scene.Children.Clear();
    }

    /// <summary>The camera axes in GW2 left-handed world space. Computed once a frame
    /// rather than per point, and deliberately the same construction the flat renderer
    /// uses, so the two can never disagree about where something is.</summary>
    private readonly record struct CamBasis(
        double Cx, double Cy, double Cz,
        double Fx, double Fy, double Fz,
        double Rx, double Ry, double Rz,
        double Ux, double Uy, double Uz);

    private static bool TryBasis(MumbleLinkData d, out CamBasis b)
    {
        b = default;
        double fx = d.CameraFrontX, fy = d.CameraFrontY, fz = d.CameraFrontZ;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl < 1e-4) return false;
        fx /= fl; fy /= fl; fz /= fl;

        double rx = fz, ry = 0, rz = -fx;                 // (0,1,0) x front
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-4) return false;                      // looking straight up or down
        rx /= rl; rz /= rl;

        double ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;
        b = new CamBasis(d.CameraX, d.CameraY, d.CameraZ, fx, fy, fz, rx, ry, rz, ux, uy, uz);
        return true;
    }

    /// <summary>World point to the frame WPF expects: +x right, +y up, -z receding.
    /// No rejection here: a point behind you simply gets a positive z and WPF clips the
    /// triangle at the near plane, which is the whole point of using a Viewport3D.</summary>
    private static Point3D ToCam(in CamBasis b, double wx, double wy, double wz)
    {
        double tx = wx - b.Cx, ty = wy - b.Cy, tz = wz - b.Cz;
        return new Point3D(tx * b.Rx + ty * b.Ry + tz * b.Rz,
                           tx * b.Ux + ty * b.Uy + tz * b.Uz,
                        -( tx * b.Fx + ty * b.Fy + tz * b.Fz));
    }

    // -- How much of the trail you can see through ----------------------------
    //
    // TacO fades its trail near the camera so the ribbon never covers your own
    // character, and thins it out with distance. Without that, the band is equally
    // solid at your feet and a hundred metres away - and since no overlay can be told
    // what the game has in front of it, a distant stretch running behind a building is
    // painted just as brightly as the piece you are standing on. That is what reads as
    // "clipping": not a drawing fault, but everything shouting at the same volume.

    /// <summary>Nearer than this, the ribbon thins out so it does not sit on top of
    /// your character. Deliberately mild - you still want to see where the path goes
    /// from under your own feet.</summary>
    private const double NearFadeM = 5.0, NearFadeEndM = 12.0, NearFadeFloor = 0.30;

    /// <summary>
    /// Beyond this the ribbon fades away with DISTANCE FROM THE CAMERA, until it is all
    /// but gone at the far end.
    ///
    /// This is the answer to the trail appearing to climb the side of a house. The route
    /// carries on past the building; we cannot be told the building is there, so we paint
    /// straight over it. What we CAN do is stop the far half being as loud as the piece
    /// under your feet, so it reads as a hint about where the path goes rather than a
    /// solid band up a wall. It also clears up the scribble at the very end, where a
    /// ribbon seen almost edge-on 90 m away collapsed into a bright zigzag.
    ///
    /// Measured from the camera rather than along the route - which is what TacO's own
    /// fadeNear/fadeFar do, and what actually matters here: an 80 m route that doubles
    /// back on itself may never be more than 20 m away, and should stay fully visible.
    /// </summary>
    private const double FarFadeStartM = 25.0, FarFadeEndM = 85.0, FarFadeFloor = 0.06;

    /// <summary>Far sections narrow as well, so what remains reads as a thread showing
    /// the way rather than a wide band lying across the scenery.</summary>
    private const double FarThinFloor = 0.45;

    /// <summary>Route further above or below you than this belongs to another storey.
    /// It is drawn as a GHOST rather than removed: hiding it is what used to chop the
    /// path in half on every staircase, but painting it solid makes a walkway two
    /// floors up look like a bridge hanging in the sky.</summary>
    private const double OffLevelM = 4.0, OffLevelAlpha = 0.38;

    /// <summary>0 close up, 1 at the far limit - shared by the fade and the narrowing so
    /// the two always agree about how distant something is.</summary>
    private static double FarNess(double distM) =>
        Math.Clamp((distM - FarFadeStartM) / (FarFadeEndM - FarFadeStartM), 0, 1);

    private static double AlphaAt(double distM, double dyM)
    {
        double a = 1.0;

        if (distM < NearFadeEndM)
        {
            double t = (distM - NearFadeM) / (NearFadeEndM - NearFadeM);
            a *= NearFadeFloor + (1.0 - NearFadeFloor) * Math.Clamp(t, 0, 1);
        }

        a *= 1.0 - (1.0 - FarFadeFloor) * FarNess(distM);

        if (Math.Abs(dyM) > OffLevelM) a *= OffLevelAlpha;

        return Math.Clamp(a, 0.0, 1.0);
    }

    private static double WidthScaleAt(double distM) =>
        1.0 - (1.0 - FarThinFloor) * FarNess(distM);

    /// <summary>
    /// Walk the route at a fixed spacing, keeping every original vertex as well.
    ///
    /// Two reasons. The ribbon is cut into short pieces so each can be drawn at its own
    /// transparency - WPF meshes have no per-vertex alpha, so a fade has to be built out
    /// of pieces - and short pieces make that fade smooth instead of stepped. Keeping the
    /// original vertices means corners stay exactly where the pack put them rather than
    /// being rounded off by the resampling.
    /// </summary>
    private static List<double[]> Resample(List<double[]> route, double unit, double stepM)
    {
        var outp = new List<double[]>();
        if (route.Count == 0) return outp;

        double carry = 0;
        outp.Add(new[] { route[0][0] / unit, route[0][1] / unit, route[0][2] / unit });

        for (int i = 0; i < route.Count - 1; i++)
        {
            double ax = route[i][0] / unit, ay = route[i][1] / unit, az = route[i][2] / unit;
            double bx = route[i + 1][0] / unit, by = route[i + 1][1] / unit, bz = route[i + 1][2] / unit;
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            if (len < 1e-6) continue;

            double d = stepM - carry;
            while (d < len)
            {
                double t = d / len;
                outp.Add(new[] { ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t });
                d += stepM;
            }
            carry = (carry + len) % stepM;
            outp.Add(new[] { bx, by, bz });          // never lose a real corner
        }
        return outp;
    }

    private void Draw3DRibbon(List<double[]> route, MumbleLinkData data, double unit,
                              double sw, double sh, Color color)
    {
        if (!TryBasis(data, out var b)) { ClearScene(); return; }

        // Short pieces: fine enough for a smooth fade, coarse enough to stay cheap.
        var pts = Resample(route, unit, 4.0);
        int n = pts.Count;
        if (n < 2) { ClearScene(); return; }

        double halfW = EffectiveTrailWidthM() * 0.5;
        // One chevron per this much route. It was 2.5 m, which looked right underfoot and
        // then vanished: at any distance a 2.5 m tile is a couple of pixels tall, the
        // filtering averages the arrow away, and the ribbon goes flat. Bigger tiles keep
        // an arrow readable much further down the path - which is the whole point of it.
        double tileM = Math.Max(3.0, halfW * 11.0);

        // TacO paints its trail ON the ground. The 0.35 m lift here was guarding against
        // the ribbon sinking into the floor, which cannot happen: we have no depth test
        // against the game at all, so nothing can ever be drawn over us. All the lift did
        // was hold the band a foot in the air, which is part of why it read as floating.
        const double Lift = 0.05;

        var cum = new double[n];
        for (int i = 1; i < n; i++)
            cum[i] = cum[i - 1] + Math.Sqrt(
                (pts[i][0] - pts[i - 1][0]) * (pts[i][0] - pts[i - 1][0]) +
                (pts[i][1] - pts[i - 1][1]) * (pts[i][1] - pts[i - 1][1]) +
                (pts[i][2] - pts[i - 1][2]) * (pts[i][2] - pts[i - 1][2]));
        double total = cum[n - 1];
        if (total < 0.5) { ClearScene(); return; }

        double phase = (Environment.TickCount64 % 600000) / 1000.0 * 1.6 / tileM;

        // Per-point ribbon edges in camera space, plus the transparency each point wants.
        var left = new Point3D[n];
        var right = new Point3D[n];
        var alpha = new double[n];

        for (int i = 0; i < n; i++)
        {
            double x = pts[i][0], y = pts[i][1] + Lift, z = pts[i][2];

            double[] pa = pts[Math.Max(i - 1, 0)], pc = pts[Math.Min(i + 1, n - 1)];
            double dx = pc[0] - pa[0], dz = pc[2] - pa[2];
            double dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1e-6) { dx = 1; dz = 0; dl = 1; }
            dx /= dl; dz /= dl;

            double nx = -dz, nz = dx, scale = 1.0;
            if (i > 0 && i < n - 1)
            {
                double ix = pts[i][0] - pts[i - 1][0], iz = pts[i][2] - pts[i - 1][2];
                double il = Math.Sqrt(ix * ix + iz * iz);
                if (il > 1e-6)
                {
                    ix /= il; iz /= il;
                    double cosT = Math.Clamp(ix * dx + iz * dz, -1, 1);
                    double half = Math.Cos(Math.Acos(cosT) * 0.5);
                    scale = half > 0.25 ? 1.0 / half : 4.0;
                }
            }

            double distCam = Math.Sqrt(
                (x - data.CameraX) * (x - data.CameraX) +
                (y - data.CameraY) * (y - data.CameraY) +
                (z - data.CameraZ) * (z - data.CameraZ));

            double w = halfW * scale * WidthScaleAt(distCam);
            left[i] = ToCam(b, x + nx * w, y, z + nz * w);
            right[i] = ToCam(b, x - nx * w, y, z - nz * w);

            alpha[i] = AlphaAt(distCam, pts[i][1] - data.AvatarY);
        }

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(System.Windows.Media.Colors.White));

        // One quad per step, each at its own transparency. Neighbours share their edge
        // vertices exactly, so the band still reads as one continuous ribbon.
        for (int i = 0; i < n - 1; i++)
        {
            double a = (alpha[i] + alpha[i + 1]) * 0.5;
            if (a < 0.06) continue;

            var mesh = new MeshGeometry3D();
            mesh.Positions.Add(left[i]); mesh.Positions.Add(right[i]);
            mesh.Positions.Add(left[i + 1]); mesh.Positions.Add(right[i + 1]);

            double v0 = cum[i] / tileM - phase, v1 = cum[i + 1] / tileM - phase;
            mesh.TextureCoordinates.Add(new System.Windows.Point(0, v0));
            mesh.TextureCoordinates.Add(new System.Windows.Point(1, v0));
            mesh.TextureCoordinates.Add(new System.Windows.Point(0, v1));
            mesh.TextureCoordinates.Add(new System.Windows.Point(1, v1));

            mesh.TriangleIndices.Add(0); mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(2);
            mesh.TriangleIndices.Add(1); mesh.TriangleIndices.Add(3); mesh.TriangleIndices.Add(2);
            mesh.Freeze();

            var mat = new DiffuseMaterial(ChevronBrush(color, a));
            group.Children.Add(new GeometryModel3D(mesh, mat) { BackMaterial = mat });
        }

        double vFov = Math.Clamp(data.Fov, 0.3f, 2.5f);
        double aspect = sw / Math.Max(sh, 1);
        double hFov = 2.0 * Math.Atan(Math.Tan(vFov / 2.0) * aspect) * 180.0 / Math.PI;

        Scene.Camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, 0),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = Math.Clamp(hFov, 10, 150),
            NearPlaneDistance = 0.15,
            FarPlaneDistance = 2000,
        };
        Scene.Children.Clear();
        Scene.Children.Add(new ModelVisual3D { Content = group });
    }

    /// <summary>A single repeating tile: a translucent band in your chosen colour with a
    /// bright chevron pointing the way you should walk. One tiled texture replaces the
    /// hundred-odd separate arrow polygons the flat renderer added every frame.</summary>
    private ImageBrush ChevronBrush(Color c, double opacity)
    {
        // Cached in twentieths: a fade needs many opacities per frame, and building a
        // bitmap for each one would be far more expensive than the drawing itself.
        int bucket = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 20);
        if (_chevronColor != c) { _chevronCache.Clear(); _chevronColor = c; }
        if (_chevronCache.TryGetValue(bucket, out var cached)) return cached;

        var made = BuildChevronBrush(c);
        made.Opacity = bucket / 20.0;
        made.Freeze();
        _chevronCache[bucket] = made;
        return made;
    }

    private ImageBrush BuildChevronBrush(Color c)
    {
        const int W = 64, H = 128;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x88, c.R, c.G, c.B)), null,
                             new Rect(0, 0, W, H));
            // Bright rails down both edges so the band still reads when seen edge-on.
            var rail = new SolidColorBrush(Color.FromArgb(0xF0, c.R, c.G, c.B));
            dc.DrawRectangle(rail, null, new Rect(0, 0, 5, H));
            dc.DrawRectangle(rail, null, new Rect(W - 5, 0, 5, H));

            // Chevron pointing toward decreasing V - the direction of travel. Drawn fat
            // and near-full-width: a thin arrow is the first thing to disappear once the
            // texture is being minified down the length of the path.
            var g = new StreamGeometry();
            using (var sc = g.Open())
            {
                sc.BeginFigure(new System.Windows.Point(W * 0.50, H * 0.08), true, true);
                sc.LineTo(new System.Windows.Point(W * 0.97, H * 0.62), true, false);
                sc.LineTo(new System.Windows.Point(W * 0.50, H * 0.38), true, false);
                sc.LineTo(new System.Windows.Point(W * 0.03, H * 0.62), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), null, g);
        }

        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        return new ImageBrush(rtb)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
        };
    }

    /// <summary>Flat-renderer path for a run already chosen for us - no elevation
    /// filtering, because the follower only hands over connected route.</summary>
    private void DrawGroundRibbonRaw(List<double[]> run, MumbleLinkData data, double unit,
                                     double sw, double sh, Color color)
    {
        const double Lift = 0.05;          // on the ground, like TacO
        var pts = new List<double[]>(run.Count);
        foreach (var p in run)
        {
            double wx = p[0] / unit, wy = p[1] / unit, wz = p[2] / unit;
            double dx = wx - data.AvatarX, dz = wz - data.AvatarZ;
            pts.Add(new[] { wx, wy + Lift, wz, Math.Sqrt(dx * dx + dz * dz) });
        }
        if (pts.Count >= 2) EmitRibbon(pts, data, unit, sw, sh, color, EffectiveTrailWidthM() * 0.5);
    }

    /// <summary>One line a second saying exactly what was drawn. "Nothing drew" is
    /// otherwise indistinguishable from "drew but invisible", which has cost days.</summary>
    private void LogTrailDraw(MumbleLinkData data, List<double[]> route, double unit, int youAt)
    {
        if ((DateTime.UtcNow - _diagAt).TotalMilliseconds < 1000) return;
        _diagAt = DateTime.UtcNow;

        double span = 0, nearest = double.MaxValue;
        for (int i = 0; i < route.Count; i++)
        {
            if (i > 0)
            {
                double ax = (route[i][0] - route[i - 1][0]) / unit;
                double az = (route[i][2] - route[i - 1][2]) / unit;
                span += Math.Sqrt(ax * ax + az * az);
            }
            double dx = route[i][0] / unit - data.AvatarX, dz = route[i][2] / unit - data.AvatarZ;
            nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dz * dz));
        }
        Services.DiagLog.Log("TRAILDRAW",
            $"mode={(App.Settings.Current.TrailUse3D ? "3d" : "flat")} pts={route.Count} " +
            $"span={span:F0}m youAt={youAt} nearest={nearest:F1}m playerY={data.AvatarY:F1} " +
            $"meshes={Scene.Children.Count} canvas={Root.Children.Count} unit={unit:F2}");
    }

    /// <summary>Trail is solid out to FadeNear, then fades to nothing by FadeFar — the
    /// same idea as the pack's own fadeNear/fadeFar.
    ///
    /// These are deliberately generous. An earlier 35/55 m pair made a chosen guide
    /// vanish entirely whenever you weren't already standing on it — many pack trails
    /// are short fragments sitting elsewhere on the map, so the whole thing got culled
    /// and nothing drew at all. Since only the ONE guide you picked is painted now, a
    /// long range costs little and lets you see the route you're walking toward.</summary>
    private const double FadeNearM = 80.0;
    private const double FadeFarM = 300.0;

    private static double FadeAt(double metres) =>
        metres <= FadeNearM ? 1.0
        : Math.Clamp(1.0 - (metres - FadeNearM) / (FadeFarM - FadeNearM), 0.0, 1.0);

    /// <summary>Draw one visible stretch of path TacO-style: a faint ribbon with a
    /// procession of arrowheads marching along it toward where you should walk. TacO
    /// paints a repeating texture and scrolls it; we place chevrons at a fixed spacing
    /// and slide them forward over time, which gives the same "flowing arrows" read
    /// while keeping YOUR colour and size — the customisation TacO never offered.</summary>

    // ── World-space trail width ───────────────────────────────────────────────
    private double _lastFov = 0.873, _lastScreenH = 1080;

    /// <summary>TacO's base trail width (metres). Documented default.</summary>
    private const double BaseTrailWidthM = 1.016;

    /// <summary>Pack trailScale (1.0 until we read it from the XML) times the user's
    /// accessibility multiplier. The user's setting is applied LAST so it always wins.</summary>
    private static double EffectiveTrailWidthM()
    {
        double packScale = 1.0;   // TODO: read trailScale from the pack XML
        double userMult = Math.Clamp(App.Settings.Current.TrailWidthMultiplier, 0.25, 6.0);
        return BaseTrailWidthM * packScale * userMult;
    }

    /// <summary>
    /// Draw one visible stretch as a ribbon lying FLAT ON THE GROUND, the way TacO does.
    ///
    /// This is the difference between a trail that looks painted on the floor and one
    /// that looks like a wall hovering in the air. Previously each stretch was a 2-D
    /// stroke of fixed screen thickness drawn perpendicular to the VIEW — effectively a
    /// billboard standing upright — so a one-metre trail became a 260-pixel vertical
    /// band that appeared to float overhead.
    ///
    /// Now the two edges of the ribbon are offset horizontally in WORLD space, both
    /// corners are projected, and the quad between them is filled. The ribbon therefore
    /// lies in the ground plane and foreshortens naturally: seen edge-on from standing
    /// height it reads as a thin painted line, and it narrows with distance for free.
    /// A small height offset lifts it just clear of the floor so it doesn't sink in.
    /// </summary>
    /// <summary>How far above or below you a trail point may sit before it is treated as
    /// a different storey. Grows with distance: strict underfoot (nothing may float
    /// overhead nearby), generous far off (a distant staircase is legitimately higher).</summary>
    private static double ElevationToleranceAt(double distanceM)
    {
        var s = App.Settings.Current;
        double tol = s.TrailElevBaseM + distanceM * s.TrailElevPerMetre;
        return Math.Clamp(tol, 1.0, Math.Max(s.TrailElevBaseM, s.TrailElevMaxM));
    }

    private void DrawGroundRibbon(List<double[]> seg, MumbleLinkData data, double unit,
                                  double sw, double sh, Color color, double elevTolUnused)
    {
        double halfW = EffectiveTrailWidthM() * 0.5;
        const double Lift = 0.05;                 // on the ground, like TacO

        // Gather contiguous runs of visible centreline points. Contiguity matters: a run
        // must BREAK where the path leaves view, or the ribbon would stitch a straight
        // line across the gap.
        var runs = new List<List<double[]>>();
        var cur = new List<double[]>();
        foreach (var p in seg)
        {
            double wx = p[0] / unit, wy = p[1] / unit, wz = p[2] / unit;
            double dx = wx - data.AvatarX, dz = wz - data.AvatarZ;
            double dist = Math.Sqrt(dx * dx + dz * dz);
            double dy = Math.Abs(wy - data.AvatarY);

            bool tooFar = dist > FadeFarM;
            bool tooTall = dy > ElevationToleranceAt(dist);
            if (tooFar) _diagFar++;
            else if (tooTall) { _diagElev++; _diagWorstDy = Math.Max(_diagWorstDy, dy); }
            else _diagKept++;
            if (dist < _diagNearestDist) { _diagNearestDist = dist; _diagNearestDy = dy; }

            bool keep = !tooFar && !tooTall;
            if (!keep) { if (cur.Count > 1) runs.Add(cur); cur = new List<double[]>(); continue; }
            cur.Add(new[] { wx, wy + Lift, wz, dist });
        }
        if (cur.Count > 1) runs.Add(cur);

        _diagRuns += runs.Count;
        foreach (var run in runs) EmitRibbon(run, data, unit, sw, sh, color, halfW);

        // One line a second saying exactly what the renderer decided. "Nothing drew" is
        // otherwise indistinguishable from "drew but invisible", which has cost days.
        if ((DateTime.UtcNow - _diagAt).TotalMilliseconds > 1000)
        {
            _diagAt = DateTime.UtcNow;
            Services.DiagLog.Log("TRAILDRAW",
                $"playerY={data.AvatarY:F1} kept={_diagKept} culledFar={_diagFar} " +
                $"culledElev={_diagElev} runs={_diagRuns} polys={Root.Children.Count} " +
                $"nearestPt={_diagNearestDist:F1}m dY={_diagNearestDy:F1}m worstDy={_diagWorstDy:F1}m " +
                $"tolAtNearest={ElevationToleranceAt(_diagNearestDist):F1}m unit={unit:F2}");
            _diagKept = _diagFar = _diagElev = _diagRuns = 0;
            _diagNearestDist = double.MaxValue; _diagNearestDy = 0; _diagWorstDy = 0;
        }
    }

    // Renderer diagnostics — counts per frame, reported once a second.
    private int _diagKept, _diagFar, _diagElev, _diagRuns;
    private double _diagNearestDist = double.MaxValue, _diagNearestDy, _diagWorstDy;
    private DateTime _diagAt = DateTime.MinValue;

    /// <summary>
    /// Build ONE continuous ribbon for a run of centreline points, with mitered joins.
    ///
    /// Previously each segment was its own quad, so neighbours met at hard angles with
    /// notches and slivers between them. Here every vertex offset follows the bisector of
    /// its two adjacent directions, so consecutive segments SHARE their edge vertices
    /// exactly and the band reads as one smooth ribbon. Same construction Blish HUD
    /// Pathing uses for its triangle strip (MIT; referenced for architecture only, no
    /// code copied).
    ///
    /// The miter is length-limited: at a hairpin the bisector shrinks toward zero and an
    /// unclamped miter would shoot out as a long spike.
    /// </summary>
    private void EmitRibbon(List<double[]> run, MumbleLinkData data, double unit,
                            double sw, double sh, Color color, double halfW)
    {
        int n = run.Count;
        if (n < 2) return;

        var left = new List<Point>(n);
        var right = new List<Point>(n);
        double nearest = double.MaxValue;

        void Flush()
        {
            if (left.Count >= 2)
            {
                var pts = new PointCollection(left.Count * 2);
                foreach (var q in left) pts.Add(q);
                for (int k = right.Count - 1; k >= 0; k--) pts.Add(right[k]);

                double fadeAmt = FadeAt(nearest);
                if (fadeAmt > 0.02)
                {
                    Root.Children.Add(new Polygon
                    {
                        Points = pts,
                        Fill = new SolidColorBrush(Color.FromArgb(0x9E, color.R, color.G, color.B)),
                        Stroke = new SolidColorBrush(Color.FromArgb(0xE0, color.R, color.G, color.B)),
                        StrokeThickness = 1.5,
                        StrokeLineJoin = PenLineJoin.Round,
                        Opacity = fadeAmt,
                        IsHitTestVisible = false,
                    });
                    DrawRibbonArrows(left, right, fadeAmt);
                }
            }
            left.Clear(); right.Clear(); nearest = double.MaxValue;
        }

        for (int i = 0; i < n; i++)
        {
            double[] a = run[Math.Max(i - 1, 0)], b = run[Math.Min(i + 1, n - 1)];
            double dx = b[0] - a[0], dz = b[2] - a[2];
            double dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1e-6) continue;
            dx /= dl; dz /= dl;

            // Ground-plane perpendicular, widened at corners so the outer edge keeps up
            // with the turn. That widening is the miter.
            double nx = -dz, nz = dx;
            double scale = 1.0;
            if (i > 0 && i < n - 1)
            {
                double ix = run[i][0] - run[i - 1][0], iz = run[i][2] - run[i - 1][2];
                double il = Math.Sqrt(ix * ix + iz * iz);
                if (il > 1e-6)
                {
                    ix /= il; iz /= il;
                    double cosT = Math.Clamp(ix * dx + iz * dz, -1, 1);
                    double half = Math.Cos(Math.Acos(cosT) * 0.5);
                    scale = half > 0.25 ? 1.0 / half : 4.0;      // limit the spike
                }
            }
            double ox = nx * halfW * scale, oz = nz * halfW * scale;

            if (ProjectXYZ((run[i][0] + ox) * unit, run[i][1] * unit, (run[i][2] + oz) * unit,
                           data, unit, sw, sh, out double lx, out double ly) < 0) { Flush(); continue; }
            if (ProjectXYZ((run[i][0] - ox) * unit, run[i][1] * unit, (run[i][2] - oz) * unit,
                           data, unit, sw, sh, out double rx, out double ry) < 0) { Flush(); continue; }

            left.Add(new Point(lx, ly));
            right.Add(new Point(rx, ry));
            nearest = Math.Min(nearest, run[i][3]);
        }
        Flush();
    }

    /// <summary>Direction arrows down the centre of the ribbon, sliding forward over time.
    /// Built from the ribbon edges themselves, so they always lie flat on it.</summary>
    private void DrawRibbonArrows(List<Point> left, List<Point> right, double fade)
    {
        const double SpacingPx = 90;
        double phase = (Environment.TickCount / 22.0) % SpacingPx;
        double travelled = -phase;

        for (int i = 0; i < left.Count - 1; i++)
        {
            Point c0 = new((left[i].X + right[i].X) * 0.5, (left[i].Y + right[i].Y) * 0.5);
            Point c1 = new((left[i + 1].X + right[i + 1].X) * 0.5, (left[i + 1].Y + right[i + 1].Y) * 0.5);
            double dx = c1.X - c0.X, dy = c1.Y - c0.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01) { continue; }

            double next = Math.Ceiling((travelled < 0 ? 0 : travelled) / SpacingPx) * SpacingPx;
            while (next < travelled + len)
            {
                if (next >= 0)
                {
                    double t = (next - travelled) / len;
                    Point c = new(c0.X + dx * t, c0.Y + dy * t);
                    Point l = new(left[i].X + (left[i + 1].X - left[i].X) * t,
                                  left[i].Y + (left[i + 1].Y - left[i].Y) * t);
                    Point r = new(right[i].X + (right[i + 1].X - right[i].X) * t,
                                  right[i].Y + (right[i + 1].Y - right[i].Y) * t);
                    double ux = dx / len, uy = dy / len;
                    double half = Math.Sqrt((l.X - r.X) * (l.X - r.X) + (l.Y - r.Y) * (l.Y - r.Y)) * 0.5;
                    if (half > 3)
                    {
                        Root.Children.Add(new Polygon
                        {
                            Points = new PointCollection
                            {
                                new(c.X + ux * half, c.Y + uy * half),
                                new(l.X - ux * half * 0.3, l.Y - uy * half * 0.3),
                                new(r.X - ux * half * 0.3, r.Y - uy * half * 0.3),
                            },
                            Fill = Brushes.White,
                            Opacity = fade * 0.85,
                            IsHitTestVisible = false,
                        });
                    }
                }
                next += SpacingPx;
            }
            travelled += len;
        }
    }

    private void DrawTrail(List<Point> pts, Color color)
    {
        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xAA, color.R, color.G, color.B)),
            StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 },
        };
        foreach (var p in pts) poly.Points.Add(p);
        Root.Children.Add(poly);
    }
}

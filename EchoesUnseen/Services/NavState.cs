namespace EchoesUnseen.Services;

/// <summary>
/// A tiny shared hand-off between the Trail Navigator (which does the expensive
/// API work every 2 s to pick the nearest objective) and the always-on compass
/// overlay (which polls MumbleLink fast to rotate an arrow toward that target).
///
/// WHY A STATIC:
///   The compass overlay lives in MainWindow and must keep pointing even after
///   the Trail Navigator panel is closed — navigation is meant to guide you WHILE
///   you play. A static, thread-safe snapshot avoids wiring a live reference
///   between a panel that comes and goes and a permanent overlay.
///
/// The target coordinates are in the same continent/map plane as MumbleLink's
/// PlayerX/PlayerY, so the compass can compute a bearing directly.
/// </summary>
public static class NavState
{
    private static readonly object _lock = new();
    private static float _x, _y;
    private static string _name = "";
    private static float _distance;
    private static bool _has;

    /// <summary>
    /// Trail Navigator publishes the current target here, in GW2 WORLD METRES —
    /// the same frame MumbleLink reports your position and facing in.
    ///
    /// This used to carry continent coordinates, which the compass then compared
    /// against a world-space facing vector. Those are different coordinate systems
    /// with the north/south axis flipped between them, so the arrow pointed behind
    /// you and the voice called the opposite turn. Everything is world metres now,
    /// so the compass, the spoken guide and the sonar cannot disagree.
    /// </summary>
    public static void SetTarget(float worldX, float worldZ, float distanceMetres, string name)
    {
        lock (_lock) { _x = worldX; _y = worldZ; _distance = distanceMetres; _name = name ?? ""; _has = true; }
    }

    public static void Clear()
    {
        lock (_lock) { _has = false; _name = ""; }
    }

    /// <summary>Current target snapshot. Returns false when there is nothing to
    /// point at (no map, sonar off, or no objectives match the filters).</summary>
    public static bool TryGetTarget(out float x, out float y, out float distance, out string name)
    {
        lock (_lock)
        {
            x = _x; y = _y; distance = _distance; name = _name;
            return _has;
        }
    }
}

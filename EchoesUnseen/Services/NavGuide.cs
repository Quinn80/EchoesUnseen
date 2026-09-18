namespace EchoesUnseen.Services;

/// <summary>
/// Turn-by-turn spoken navigation — the sightless compass.
///
/// Everything here works in GW2 WORLD space (metres), the SAME space MumbleLink
/// reports the live player position and camera facing in. That is the whole trick:
/// a bearing is only meaningful if the target and your facing live in one space.
/// The objective list from the API is in CONTINENT coordinates, so callers convert
/// those to world metres first (see ContinentToWorld) before asking for guidance.
///
/// Heading = the way the CAMERA faces, because that's the way you walk when you
/// press forward. 12 o'clock is dead ahead; 3 o'clock is a right angle to your
/// right; 6 o'clock is directly behind you.
/// </summary>
public static class NavGuide
{
    public readonly record struct Guidance(
        double DistanceMetres, double AngleDeg, int ClockHour, string Turn, bool Ahead);

    /// <summary>
    /// Direction and distance from the player to a target, in world metres.
    ///   px,pz  – player world position (X east, Z south) — MumbleLink AvatarX/AvatarZ
    ///   fx,fz  – camera facing on the ground plane — MumbleLink CameraFrontX/CameraFrontZ
    ///   tx,tz  – target world position
    /// Positive angle = target is to your RIGHT. `flip` mirrors left/right for maps
    /// whose handedness comes out reversed (user-settable, distance-independent).
    /// </summary>
    public static Guidance Compute(double px, double pz, double fx, double fz,
                                   double tx, double tz, bool flip)
    {
        double dx = tx - px, dz = tz - pz;
        double dist = Math.Sqrt(dx * dx + dz * dz);

        double fl = Math.Sqrt(fx * fx + fz * fz);
        if (fl < 1e-6) fl = 1;
        fx /= fl; fz /= fl;

        // dot = how much the target is ahead (cos); cross = which side (sin).
        double dot   = dist < 1e-6 ? 1 : (dx * fx + dz * fz) / dist;
        double cross = dist < 1e-6 ? 0 : (fx * dz - fz * dx) / dist;
        if (flip) cross = -cross;

        double ang = Math.Atan2(cross, dot) * 180.0 / Math.PI;   // -180..180, + = right

        int hour = (int)Math.Round(ang / 30.0);
        hour = ((hour % 12) + 12) % 12;
        if (hour == 0) hour = 12;

        return new Guidance(dist, ang, hour, TurnPhrase(ang), Math.Abs(ang) <= AlignedDeg);
    }

    /// <summary>Within this many degrees of dead-ahead counts as "straight" — a
    /// forgiving cone so the guide says "go forward" without demanding pixel aim.</summary>
    public const double AlignedDeg = 22;

    private static string TurnPhrase(double a)
    {
        double m = Math.Abs(a);
        string side = a > 0 ? "right" : "left";
        if (m <= AlignedDeg) return "straight ahead";
        if (m >= 150)        return "turn around";
        if (m <= 55)         return "bear " + side;
        if (m <= 115)        return "turn " + side;
        return "turn hard " + side;   // 115–150: nearly behind
    }

    /// <summary>A short spoken "which way" line for the on-demand clock check.</summary>
    public static string ClockLine(Guidance g, string? name)
    {
        string who = string.IsNullOrWhiteSpace(name) ? "Objective" : name;
        int m = (int)Math.Round(g.DistanceMetres);
        if (g.Ahead)
            return $"{who} straight ahead, {m} metres.";
        return $"{who} at {g.ClockHour} o'clock, {m} metres.";
    }

    /// <summary>
    /// Convert a continent-coordinate point (what the objective list uses) into GW2
    /// world metres (what MumbleLink's live facing/position use), using the map's
    /// map_rect (game inches) and continent_rect from the API.
    /// Returns false when the map lacks rect data (rare) so the caller can fall back.
    /// </summary>
    public static bool ContinentToWorld(double cx, double cy,
                                        float[][]? mapRect, float[][]? contRect,
                                        out double wx, out double wz)
    {
        wx = wz = 0;
        if (mapRect is not { Length: >= 2 } || contRect is not { Length: >= 2 }) return false;
        if (mapRect[0].Length < 2 || mapRect[1].Length < 2) return false;
        if (contRect[0].Length < 2 || contRect[1].Length < 2) return false;

        double crW = contRect[1][0] - contRect[0][0];
        double crH = contRect[1][1] - contRect[0][1];
        if (Math.Abs(crW) < 1e-6 || Math.Abs(crH) < 1e-6) return false;

        double fracX = (cx - contRect[0][0]) / crW;
        double fracY = (cy - contRect[0][1]) / crH;

        // Continent Y runs top-to-bottom; game Y is flipped relative to it.
        double gameX = mapRect[0][0] + fracX * (mapRect[1][0] - mapRect[0][0]);
        double gameY = mapRect[0][1] + (1 - fracY) * (mapRect[1][1] - mapRect[0][1]);

        // map_rect is in inches; MumbleLink positions are in metres.
        const double InchesPerMetre = 39.3701;
        wx = gameX / InchesPerMetre;
        wz = gameY / InchesPerMetre;
        return true;
    }
}

namespace EchoesUnseen.Services;

/// <summary>
/// Follows a TacO painted trail as an ORDERED ROUTE rather than homing on a blob.
///
/// This is what makes the guidance walk you around walls instead of into them: a trail
/// is a hand-authored walkable path, so we find where you are along it and aim at a
/// point a little way AHEAD on the route. As you walk, that aim point slides forward,
/// giving smooth turn-by-turn along the real path.
///
/// Everything is in GW2 world metres — the same frame as MumbleLink's position and
/// facing — so no coordinate conversion is involved and the direction maths is exact.
/// (The old compass mixed continent coords with a world-space facing, which is why it
/// drifted; following trail data removes that whole class of bug by construction.)
/// </summary>
public sealed class TrailFollower
{
    /// <summary>How far ahead along the path to aim, in METRES — roughly ten paces.
    /// Measured as distance rather than a count of points, because vertex spacing
    /// differs between packs; counting points would aim ten metres ahead in one pack
    /// and thirty in another. This keeps the guidance calm instead of twitchy.</summary>
    public const double LookaheadM = 10.0;

    /// <summary>Beyond this many metres from the path you're treated as off-route and
    /// guided back to it rather than onward along it.</summary>
    public const double OffPathM = 15.0;

    /// <summary>Within this of the final point, the trail is done.</summary>
    public const double ArriveM = 8.0;

    /// <summary>Beyond this you're not on the route at all — you get led to its START
    /// instead of being told you've "drifted", which is nonsense when the guide you
    /// picked begins on the far side of the map.</summary>
    public const double FarAwayM = 60.0;

    public TacoService.Trail? Active { get; private set; }
    public string Name => Active?.Name is { Length: > 0 } n ? n : (Active?.Category ?? "trail");

    /// <summary>The trail currently being followed, app-wide. The on-screen overlay
    /// draws ONLY this one: a map can hold dozens of trails, and painting them all at
    /// once is both unreadable spaghetti and needless work every frame.</summary>
    public static TacoService.Trail? Current { get; private set; }

    /// <summary>The follower that owns <see cref="Current"/>. The overlay needs more
    /// than the raw trail: it needs to know WHERE ON IT you are, so it can paint the
    /// connected stretch in front of you instead of the whole 4 km route.</summary>
    public static TrailFollower? Live { get; private set; }

    private List<double[]> _pts = new();      // flattened world points, in order
    private List<double> _cum = new();        // cumulative distance to the END from each index

    public void SetTrail(TacoService.Trail t)
    {
        Active = t;
        Current = t;
        Live = this;
        _cursor = -1;                 // re-acquire our place on the new route
        _state = TrailState.OffTrail; _pending = TrailState.OffTrail; _pendingCount = 0;
        _pts = new List<double[]>();
        foreach (var seg in t.Segments) _pts.AddRange(seg);

        // Distance remaining along the path from each point, so "how far to go" follows
        // the winding route instead of cutting straight through the scenery.
        _cum = new List<double>(new double[_pts.Count]);
        for (int i = _pts.Count - 2; i >= 0; i--)
        {
            double dx = _pts[i + 1][0] - _pts[i][0], dz = _pts[i + 1][2] - _pts[i][2];
            _cum[i] = _cum[i + 1] + Math.Sqrt(dx * dx + dz * dz);
        }
    }

    public void Clear()
    {
        Active = null; Current = null; _pts = new(); _cum = new(); _cursor = -1;
        if (ReferenceEquals(Live, this)) Live = null;
        _state = TrailState.OffTrail; _pending = TrailState.OffTrail; _pendingCount = 0;
    }
    public bool HasTrail => Active != null && _pts.Count > 1;

    // Progress along the route. Kept between ticks so guidance follows the stretch
    // you're on rather than snapping to whichever lap of the trail happens to be
    // closest. -1 = not yet acquired.
    private int _cursor = -1;
    private const int SearchBack = 20;        // allow a little backtracking
    private const int SearchForward = 80;     // and a decent stride forward
    private const double ReacquireM = 40.0;   // this far from the window → re-find the path

    /// <summary>
    /// Nearest point on the route within an index range, measured to the SEGMENTS rather
    /// than to the stored vertices.
    ///
    /// Vertex distance systematically over-reports how far off you are: stand exactly on
    /// the path midway between two points 2 m apart and vertex-distance says you're 1 m
    /// off. That inflation is what pushes a state machine over its thresholds when you
    /// haven't actually strayed, so drift is measured perpendicular to the segment, which
    /// is what "distance from the trail" actually means.
    /// </summary>
    private (int Index, double DistSq) NearestIn(double px, double pz, int lo, int hi)
    {
        int best = lo; double bestD = double.MaxValue;
        for (int i = lo; i <= hi; i++)
        {
            double d;
            if (i < hi)
            {
                double ax = _pts[i][0], az = _pts[i][2];
                double bx = _pts[i + 1][0], bz = _pts[i + 1][2];
                double ex = bx - ax, ez = bz - az;
                double len2 = ex * ex + ez * ez;
                double t = len2 < 1e-9 ? 0 : ((px - ax) * ex + (pz - az) * ez) / len2;
                t = Math.Clamp(t, 0, 1);                       // stay within the segment
                double cx = ax + ex * t, cz = az + ez * t;
                double dx = cx - px, dz = cz - pz;
                d = dx * dx + dz * dz;
            }
            else
            {
                double dx = _pts[i][0] - px, dz = _pts[i][2] - pz;
                d = dx * dx + dz * dz;
            }
            if (d < bestD) { bestD = d; best = i; }
        }
        return (best, bestD);
    }

    // ── On Trail / Near Trail / Off Trail ─────────────────────────────────────
    //
    // A single threshold makes the state chatter: hover either side of it and every
    // announcement fires again. So each transition has a DIFFERENT enter and exit
    // distance (hysteresis), and a candidate state must also persist for a few ticks
    // (debounce) before it counts. Together those mean small wobbles, or the odd
    // imprecise position sample, can never flip the state on their own.
    public enum TrailState { OnTrail, NearTrail, OffTrail }

    public const double OnEnterM  = 6.0;    // become "on" only when genuinely on it
    public const double OnExitM   = 10.0;   // but don't lose "on" until clearly past it
    public const double OffEnterM = 22.0;   // become "off" only when well away
    public const double OffExitM  = 16.0;   // and recover before that gap closes fully
    private const int DebounceTicks = 3;    // ~1.8 s at the 600 ms guide tick

    private TrailState _state = TrailState.OffTrail;
    private TrailState _pending = TrailState.OffTrail;
    private int _pendingCount;

    private TrailState StepState(double drift)
    {
        // Where would we like to be, given only the distance?
        TrailState want = _state;
        switch (_state)
        {
            case TrailState.OnTrail:
                if (drift > OffEnterM) want = TrailState.OffTrail;
                else if (drift > OnExitM) want = TrailState.NearTrail;
                break;
            case TrailState.NearTrail:
                if (drift <= OnEnterM) want = TrailState.OnTrail;
                else if (drift > OffEnterM) want = TrailState.OffTrail;
                break;
            case TrailState.OffTrail:
                if (drift <= OnEnterM) want = TrailState.OnTrail;
                else if (drift <= OffExitM) want = TrailState.NearTrail;
                break;
        }

        if (want == _state) { _pending = _state; _pendingCount = 0; return _state; }
        if (want != _pending) { _pending = want; _pendingCount = 1; return _state; }
        if (++_pendingCount < DebounceTicks) return _state;

        _state = want; _pendingCount = 0;
        return _state;
    }

    /// <summary>Index of the point roughly <paramref name="metres"/> further along the
    /// route from <paramref name="from"/>. Uses the precomputed distance-to-end, so it
    /// costs nothing and is independent of how densely the pack stores its points.</summary>
    private int AheadFrom(int from, double metres)
    {
        if (from >= _pts.Count - 1) return _pts.Count - 1;
        double targetRemaining = _cum[from] - metres;
        for (int i = from + 1; i < _pts.Count; i++)
            if (_cum[i] <= targetRemaining) return i;
        return _pts.Count - 1;
    }

    // ── What the overlay actually paints ──────────────────────────────────────
    //
    // The renderer used to be handed the WHOLE route — every point of a 4 km trail —
    // and left to work out for itself what was worth drawing. It did that with an
    // elevation filter: hide anything sitting too far above or below you, on the theory
    // that such points belong to a different storey.
    //
    // Measured against a real walk through Divinity's Reach, that filter was deleting
    // 27% of the nearby path: it punched a hole through the middle of the ribbon about
    // once per frame, and roughly one frame in twenty-five it removed the path at the
    // player's own feet. In a stacked city every staircase and terrace looks like
    // "a different storey" to a rule that only knows your height.
    //
    // The fix is to stop guessing from height and use what we already know: the route is
    // an ORDERED path and we know where on it you are. So walk outward from your cursor
    // and take the stretch that is genuinely connected to where you stand. A staircase
    // stays, because you can walk up it. A balcony stacked overhead never appears,
    // because the route doesn't reach it from here without a break.

    /// <summary>Metres of route painted ahead of you, and the short tail behind so you
    /// can see you're standing on it rather than beside it.</summary>
    public const double DrawAheadM = 150.0;
    public const double DrawBehindM = 15.0;

    /// <summary>Steeper than this between neighbouring route points and the path is
    /// changing storey rather than climbing. Measured on Tekkit's Divinity's Reach route:
    /// the median step rises 0.03 of its length and nine in ten stay under 0.43, so 1.2
    /// (about 50°) clears every real staircase and catches only the 8 genuine jumps in
    /// 510 steps.</summary>
    private const double MaxWalkSlope = 1.2;

    /// <summary>A horizontal gap this big between neighbours isn't a step, it's the
    /// pack stitching two separate stretches together (the same route has one 110 m
    /// "step"). Drawing across it would paint a line through the scenery.</summary>
    private const double MaxStepM = 40.0;

    private static bool Walkable(double[] a, double[] b)
    {
        double h = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[2] - a[2]) * (b[2] - a[2]));
        if (h > MaxStepM) return false;
        return Math.Abs(b[1] - a[1]) <= MaxWalkSlope * Math.Max(h, 0.05);
    }

    private static double Step3(double[] a, double[] b)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// The connected stretch of route to draw: from your place on the path, forward
    /// <paramref name="aheadM"/> metres and back <paramref name="behindM"/>, stopping
    /// early at either end if the route stops being walkable from here.
    ///
    /// Returns the points in walking order. Empty if we haven't found our place yet, in
    /// which case the caller falls back to drawing the raw trail.
    /// </summary>
    public List<double[]> VisibleRun(double aheadM, double behindM, out int youAt)
    {
        youAt = 0;
        var res = new List<double[]>();
        if (_pts.Count < 2 || _cursor < 0 || _cursor >= _pts.Count) return res;

        int lo = _cursor, hi = _cursor;

        double walked = 0;
        for (int i = _cursor; i < _pts.Count - 1; i++)
        {
            if (!Walkable(_pts[i], _pts[i + 1])) break;
            walked += Step3(_pts[i], _pts[i + 1]);
            hi = i + 1;
            if (walked >= aheadM) break;
        }

        walked = 0;
        for (int i = _cursor; i > 0; i--)
        {
            if (!Walkable(_pts[i], _pts[i - 1])) break;
            walked += Step3(_pts[i], _pts[i - 1]);
            lo = i - 1;
            if (walked >= behindM) break;
        }

        for (int i = lo; i <= hi; i++) res.Add(_pts[i]);
        youAt = _cursor - lo;          // where in the returned list you are standing
        return res;
    }

    public readonly record struct Status(
        bool Valid,
        double DriftM,          // how far you are from the path
        int DriftSide,          // -1 = path is to your left, +1 = right, 0 = on it
        double RemainingM,      // distance still to walk along the route
        NavGuide.Guidance Guide,// direction/turn to the aim point (or back to the path)
        bool OffPath,
        bool Arrived,
        bool FarAway,           // not on the route at all yet — being led to its start
        double AimX,            // the world point being steered toward — the compass
        double AimZ,            // uses this too, so arrow and voice always agree
        TrailState State,       // debounced On / Near / Off
        bool StateChanged);     // true only on the tick the state actually flips

    /// <summary>
    /// Work out where you are on the route and which way to go.
    ///   px,pz  – your world position   fx,fz – your facing (see hybrid choice in caller)
    /// When you're on the path we aim ahead along it; when you've strayed we aim at the
    /// nearest point on the path instead, so the same guidance walks you back on.
    /// </summary>
    public Status Update(double px, double pz, double fx, double fz, bool flip)
    {
        if (!HasTrail) return new Status(false, 0, 0, 0, default, false, false, false, 0, 0, TrailState.OffTrail, false);

        // WHERE ARE WE ON THE ROUTE?
        //
        // Naively taking the globally-nearest point breaks badly on real trails, which
        // loop back and cross over themselves: as you walk, a completely different lap
        // of the route passes close by, the "nearest" point jumps to it, and the voice
        // flip-flops "turn right… turn left…". So once we've found our place we only
        // look in a WINDOW around it and creep forward — the guidance then follows the
        // stretch you're actually walking. We only re-acquire globally if we've clearly
        // lost the path altogether.
        int near; double best;
        if (_cursor < 0)
        {
            (near, best) = NearestIn(px, pz, 0, _pts.Count - 1);
        }
        else
        {
            int lo = Math.Max(0, _cursor - SearchBack);
            int hi = Math.Min(_pts.Count - 1, _cursor + SearchForward);
            (near, best) = NearestIn(px, pz, lo, hi);
            if (Math.Sqrt(best) > ReacquireM)          // lost it — find the path again
                (near, best) = NearestIn(px, pz, 0, _pts.Count - 1);
        }
        _cursor = near;
        double drift = Math.Sqrt(best);

        // Which side of the path are you on? Cross the path's heading with the vector
        // from the path to you: the sign says whether to step left or right to rejoin.
        int side = 0;
        if (near < _pts.Count - 1)
        {
            double hx = _pts[near + 1][0] - _pts[near][0], hz = _pts[near + 1][2] - _pts[near][2];
            double tx = px - _pts[near][0], tz = pz - _pts[near][2];
            double cross = hx * tz - hz * tx;
            if (Math.Abs(cross) > 1e-6) side = cross > 0 ? -1 : +1;   // where the PATH lies, from you
        }

        bool off = drift > OffPathM;
        // Well clear of the whole route — you haven't started it yet. Lead to the START
        // rather than the nearest point, so a guide picked from the list makes sense from
        // wherever you happen to be standing.
        bool far = drift > FarAwayM;

        // ALWAYS aim at the nearest part of the route (ahead along it when you're on it).
        // An earlier version jumped the target to the trail's START once you were 60 m
        // off — the log showed the aim point teleporting 370 m as you crossed that line
        // back and forth, so the voice kept reversing itself. The "far" flag now only
        // changes the WORDS, never the target.
        int aim = off ? near : AheadFrom(near, LookaheadM);
        var target = _pts[aim];
        var guide = NavGuide.Compute(px, pz, fx, fz, target[0], target[2], flip);

        // When we're heading for the start, "remaining" is the distance to it, not the
        // length of a route we haven't joined.
        double remaining = _cum.Count > near ? _cum[near] : 0;

        bool arrived = !off && near >= _pts.Count - 2 && drift < ArriveM;

        var before = _state;
        var state = StepState(drift);

        return new Status(true, drift, side, remaining, guide, off, arrived, far,
                          target[0], target[2], state, state != before);
    }
}

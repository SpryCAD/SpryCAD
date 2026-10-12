namespace SpryCad.Kernel.Native;

/// <summary>
/// Exact sweep of planar profile regions along a tangent-continuous path of lines and circular arcs. Along a
/// line the profile is extruded, around an arc it is revolved about the arc's axis (which lies in the profile's
/// plane, the profile being perpendicular to the path): every face is a plane, cylinder, cone, sphere or torus.
/// The pieces are stitched where they meet (the end cap of one is the start cap of the next: both go, their edges
/// become one), no booleans. The profile keeps its orientation relative to the path (no twist).
/// </summary>
public static class NSweep
{
    /// <summary>
    /// A path segment: a line from A to B, an arc from A about Center, turning Angle (radians, positive) about Axis,
    /// or a spline (Curve, from A at its parameter 0 to B at 1).
    /// </summary>
    public sealed record Seg(Vec3 A, Vec3 B, Vec3? Center = null, Vec3? Axis = null, double Angle = 0, BSplineCurve? Curve = null)
    {
        public bool IsArc => Center != null;
        public bool IsSpline => Curve != null;
        static Vec3 D(BSplineCurve c, double t) { c.Derivs(t, out _, out var d, out _); return d.Normalized; }
        /// <summary>Unit tangent at the start (direction of travel).</summary>
        public Vec3 StartTangent => IsSpline ? D(Curve!, 0) : IsArc ? Vec3.Cross(Axis!.Value, A - Center!.Value).Normalized : (B - A).Normalized;
        /// <summary>Unit tangent at the end.</summary>
        public Vec3 EndTangent => IsSpline ? D(Curve!, 1) : IsArc ? Vec3.Cross(Axis!.Value, B - Center!.Value).Normalized : (B - A).Normalized;
        /// <summary>The motion taking the start of the segment (and the profile there) to its end (not a spline's: see <see cref="Sweep"/>).</summary>
        public Transform Motion => IsArc ? Transform.Rotation(Center!.Value, Axis!.Value, Angle) : Transform.Translation(B - A);
        public double Length
        {
            get
            {
                if (!IsSpline) return IsArc ? Angle * (A - Center!.Value).Length : (B - A).Length;
                double l = 0; var p = A;
                for (int i = 1; i <= 256; i++) { var q = Curve!.Eval(i / 256.0); l += Vec3.Distance(p, q); p = q; }
                return l;
            }
        }

        /// <summary>The segment run the other way.</summary>
        public Seg Reversed() => IsSpline ? new Seg(B, A, Curve: Reverse(Curve!)) : IsArc ? new Seg(B, A, Center, -Axis!.Value, Angle) : new Seg(B, A);

        /// <summary>A curve run backwards (still over 0..1).</summary>
        public static BSplineCurve Reverse(BSplineCurve c)
        {
            double a = c.Knots[0], b = c.Knots[^1];
            return new BSplineCurve(c.Degree, c.Poles.Reverse().ToArray(), c.Knots.Select(k => a + b - k).Reverse().ToArray());
        }
    }

    /// <summary>
    /// Sweeps the regions (in <paramref name="frame"/>'s plane, whose normal is the path's start tangent and which
    /// contains the path's start point) along the path. Faces are named prefix.k.… for segment k; the caps
    /// prefix.Start and prefix.End (none on a closed path).
    /// </summary>
    public static NBody Sweep(IReadOnlyList<ProfileRegion> regions, Frame frame, IReadOnlyList<Seg> path, string prefix)
    {
        if (path.Count == 0) throw new ArgumentException("the path is empty");
        var scale = Math.Max(1, path.Sum(s => s.Length));
        var tol = 1e-9 * scale;
        bool closed = path.Count > 1 && Vec3.Distance(path[^1].B, path[0].A) < tol * 1e3
            || path.Count == 1 && path[0].IsArc && path[0].Angle >= 2 * Math.PI - 1e-9
            || path.Count == 1 && path[0].IsSpline && Vec3.Distance(path[0].B, path[0].A) < tol * 1e3;
        // a single full turn: a revolution all the way round (no caps, nothing to stitch)
        // a spline closing on itself: two halves (no face of the sweep runs all the way round)
        if (path.Count == 1 && closed && path[0].IsSpline)
        {
            var hc = new HCurve(path[0].Curve!.Degree, path[0].Curve!.Knots, path[0].Curve!.Poles.Select(q => H.Of(q)).ToArray());
            var (h0, h1) = hc.Split(0.5 * (hc.T0 + hc.T1));
            var c0 = h0.Reparam(0, 1).ToBSpline(); var c1 = h1.Reparam(0, 1).ToBSpline();
            path = new[] { new Seg(c0.Eval(0), c0.Eval(1), Curve: c0), new Seg(c1.Eval(0), c1.Eval(1), Curve: c1) };
        }
        if (path.Count == 1 && closed && path[0].IsArc)
        {
            var s = path[0];
            var body = NBuild.Revolve(regions, frame, frame.ToLocal(s.Center!.Value), AxisIn(frame, s.Axis!.Value), 2 * Math.PI, prefix + ".0");
            return body;
        }
        // a closed path through splines: their turning frames need not come back round to the start's (the frame
        // turns about the path as it goes); the difference spread over the splines by length, as a twist
        var twists = new double[path.Count];
        if (closed && path.Any(s => s.IsSpline))
        {
            var g = frame;
            foreach (var s in path) g = s.IsSpline ? Frames(g, s.Curve!, 0, 1).Last() : g.Transformed(s.Motion);
            // (about the path's tangent there: both frames' parts square to it)
            var tz = path[0].StartTangent;
            Vec3 Sq(Vec3 v) => (v - tz * Vec3.Dot(v, tz)).Normalized;
            var (gx, fx) = (Sq(g.XDir), Sq(frame.XDir));
            var miss = Math.Atan2(Vec3.Dot(tz, Vec3.Cross(gx, fx)), Vec3.Dot(gx, fx));
            var total = path.Where(s => s.IsSpline).Sum(s => s.Length);
            for (int k = 0; k < path.Count; k++) if (path[k].IsSpline) twists[k] = miss * path[k].Length / total;
        }
        var pieces = new List<NBody>();
        var f = frame;
        for (int k = 0; k < path.Count; k++)
        {
            var s = path[k];
            var name = prefix + "." + k;
            // (a spline carries the profile at whatever angle it meets the path: from the start, it need only leave
            // the profile's plane; further on, run on smoothly from the piece before. Lines and arcs take it square.)
            if (s.IsSpline ? (k == 0 ? Vec3.Dot(s.StartTangent, f.Normal) < 0.05 : Vec3.Dot(s.StartTangent, path[k - 1].EndTangent) < 1 - 1e-6)
                : Vec3.Dot(s.StartTangent, f.Normal) < 1 - 1e-6)
                throw new ArgumentException(k == 0 && s.IsSpline ? "the path must leave the profile's plane" : $"the path turns sharply at segment {k + 1} (sweeps need a tangent-continuous path)");
            NBody piece;
            if (s.IsSpline)
            {
                var frames = Frames(f, s.Curve!, twists[k], Math.Max(12, 8 * Math.Max(1, s.Curve!.Poles.Length - s.Curve.Degree)));
                pieces.Add(NLoft.Loft(frames.Select(fr => new NLoft.Section(fr, regions)).ToList(), null, null, name));
                f = frames[^1];
                continue;
            }
            if (!s.IsArc) piece = NBuild.Extrude(regions, f, 0, s.Length, name);
            else
            {
                // the profile must stay on one side of the axis (it would sweep through itself)
                var c2 = f.ToLocal(s.Center!.Value);
                var a2 = AxisIn(f, s.Axis!.Value);
                var nrm = new Vec2(-a2.Y, a2.X);
                double side = 0;
                foreach (var r in regions) foreach (var l in r.AllLoops()) foreach (var v in l.Verts)
                        {
                            var d = Vec2.Dot(v.P - c2, nrm);
                            if (side == 0) side = Math.Sign(d);
                            else if (Math.Sign(d) != 0 && Math.Sign(d) != side)
                                throw new ArgumentException($"the profile crosses the axis of path arc {k + 1}: the arc's radius is smaller than the profile");
                        }
                piece = NBuild.Revolve(regions, f, c2, a2, s.Angle, name);
            }
            pieces.Add(piece);
            f = f.Transformed(s.Motion);
        }
        // stitch piece k's end to piece k+1's start (and the last to the first on a closed path)
        int joints = closed ? path.Count : path.Count - 1;
        for (int k = 0; k < joints; k++)
        {
            int a = k, b = (k + 1) % path.Count;
            Stitch(pieces[a], prefix + "." + a + ".End", pieces[b], prefix + "." + b + ".Start", tol * 1e3);
        }
        var res = new NBody();
        foreach (var p in pieces) res.Faces.AddRange(p.Faces);
        if (!closed)
        {
            foreach (var face in res.Faces)
            {
                if (face.Tag.Name == prefix + ".0.Start") face.Tag = new FaceTag(prefix + ".Start", face.Tag.Surface);
                else if (face.Tag.Name == prefix + "." + (path.Count - 1) + ".End") face.Tag = new FaceTag(prefix + ".End", face.Tag.Surface);
            }
        }
        res.Link();
        return res;
    }

    /// <summary>
    /// The profile's frame carried along a spline, at stations (the first f, the last at the curve's end): rotation-
    /// minimizing (turned with the tangent, no twist: double reflection, Wang et al. 2008), and turned about the tangent
    /// by <paramref name="twist"/> in all by the end, evenly. The profile placed on each and skinned through them is the
    /// sweep along the spline (an exact loft through every station; within a small fraction of their spacing between).
    /// </summary>
    static List<Frame> Frames(Frame f, BSplineCurve c, double twist, int stations)
    {
        const int sub = 16;
        var p0 = c.Eval(0);
        Vec3 T(double u) { c.Derivs(u, out _, out var dd, out _); return dd.Normalized; }
        // a basis along the path (its tangent, r, t × r) carried round; the profile's frame (at whatever angle to the
        // path) turned as the basis turns, its offset from the path point with it
        var t0 = T(0);
        var r0 = f.XDir - t0 * Vec3.Dot(f.XDir, t0);
        if (r0.Length < 1e-6) r0 = f.YDir - t0 * Vec3.Dot(f.YDir, t0);
        r0 = r0.Normalized; var b0 = Vec3.Cross(t0, r0);
        Vec3 Local(Vec3 v) => new(Vec3.Dot(v, t0), Vec3.Dot(v, r0), Vec3.Dot(v, b0));
        Vec3 lo = Local(f.Origin - p0), lx = Local(f.XDir), ly = Local(f.YDir), ln = Local(f.Normal);
        Frame At(Vec3 p, Vec3 t, Vec3 r)
        {
            var bb = Vec3.Cross(t, r);
            Vec3 W(Vec3 l) => t * l.X + r * l.Y + bb * l.Z;
            return new Frame(p + W(lo), W(lx), W(ly), W(ln));
        }
        var frames = new List<Frame> { f };
        Vec3 xi = p0, ti = t0, ri = r0;
        int steps = stations * sub;
        for (int k = 1; k <= steps; k++)
        {
            double u = (double)k / steps;
            var x1 = c.Eval(u); var t1 = T(u);
            var v1 = x1 - xi; var c1 = Vec3.Dot(v1, v1);
            if (c1 > 1e-300)
            {
                var rL = ri - v1 * (2 / c1 * Vec3.Dot(v1, ri));
                var tL = ti - v1 * (2 / c1 * Vec3.Dot(v1, ti));
                var v2 = t1 - tL; var c2 = Vec3.Dot(v2, v2);
                ri = c2 > 1e-300 ? rL - v2 * (2 / c2 * Vec3.Dot(v2, rL)) : rL;
            }
            // (kept square to the tangent and of unit length against drift)
            ri = (ri - t1 * Vec3.Dot(ri, t1)).Normalized;
            xi = x1; ti = t1;
            if (k % sub == 0)
            {
                var th = twist * k / steps;
                frames.Add(At(x1, t1, ri * Math.Cos(th) + Vec3.Cross(t1, ri) * Math.Sin(th)));
            }
        }
        return frames;
    }

    /// <summary>The direction of a 3D axis lying in the frame's plane, in the frame's coordinates.</summary>
    static Vec2 AxisIn(Frame f, Vec3 axis) => new Vec2(Vec3.Dot(axis, f.XDir), Vec3.Dot(axis, f.YDir)).Normalized;

    /// <summary>
    /// Joins two pieces where they meet: <paramref name="a"/>'s end cap and <paramref name="b"/>'s start cap (the same
    /// loops, facing each other) are removed, and b's edges and vertices there are replaced by a's.
    /// </summary>
    static void Stitch(NBody a, string endName, NBody b, string startName, double tol)
    {
        var endFaces = a.Faces.Where(x => x.Tag.Name == endName).ToList();
        var startFaces = b.Faces.Where(x => x.Tag.Name == startName).ToList();
        if (endFaces.Count == 0 || startFaces.Count == 0) throw new InvalidOperationException("sweep: a piece has no cap to join");
        var edgesA = endFaces.SelectMany(x => x.Coedges).Select(c => c.Edge).Distinct().ToList();
        var edgesB = startFaces.SelectMany(x => x.Coedges).Select(c => c.Edge).Distinct().ToList();
        var vertsA = edgesA.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().ToList();
        var vmap = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
        foreach (var vb in edgesB.SelectMany(e => new[] { e.V0, e.V1 }).Distinct())
        {
            var va = vertsA.MinBy(v => Vec3.Distance(v.P, vb.P))!;
            if (Vec3.Distance(va.P, vb.P) > tol) throw new InvalidOperationException("sweep: the pieces do not meet");
            vmap[vb] = va;
        }
        // b's cap edges: the a edge with the same ends and the same middle (two arcs of a circle share their ends)
        var emap = new Dictionary<NEdge, (NEdge e, bool flip)>(ReferenceEqualityComparer.Instance);
        foreach (var eb in edgesB)
        {
            NVertex v0 = vmap[eb.V0], v1 = vmap[eb.V1];
            var mid = eb.Mid;
            var match = edgesA.Where(ea => (ReferenceEquals(ea.V0, v0) && ReferenceEquals(ea.V1, v1)) || (ReferenceEquals(ea.V0, v1) && ReferenceEquals(ea.V1, v0)))
                .MinBy(ea => Vec3.Distance(ea.Mid, mid));
            if (match == null || Vec3.Distance(match.Mid, mid) > tol) throw new InvalidOperationException("sweep: the pieces' edges do not meet");
            emap[eb] = (match, !ReferenceEquals(match.V0, v0));
        }
        a.Faces.RemoveAll(x => endFaces.Contains(x));
        b.Faces.RemoveAll(x => startFaces.Contains(x));
        foreach (var face in b.Faces)
            foreach (var l in face.Loops)
                for (int i = 0; i < l.Coedges.Count; i++)
                {
                    var c = l.Coedges[i];
                    if (emap.TryGetValue(c.Edge, out var m)) l.Coedges[i] = new NCoedge(m.e, m.flip ? !c.Forward : c.Forward);
                    else
                    {
                        // edges leaving the joint (along the path): their joint vertex becomes a's
                        if (vmap.TryGetValue(c.Edge.V0, out var n0)) c.Edge.V0 = n0;
                        if (vmap.TryGetValue(c.Edge.V1, out var n1)) c.Edge.V1 = n1;
                    }
                }
    }
}

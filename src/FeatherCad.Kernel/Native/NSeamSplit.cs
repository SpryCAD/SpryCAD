namespace FeatherCad.Kernel.Native;

/// <summary>
/// Splits faces that wrap around a periodic surface (e.g. a full cylinder bounded by a seam edge, as STEP files
/// store them) into pieces spanning at most half a turn, the form native bodies use. Each face is unwrapped
/// into its parameter plane, cut along iso-lines (exact lines or circles in 3D), boundary edges are split where
/// they cross a cut, and the pieces are traced as the faces of the resulting planar graph. Poles (sphere poles,
/// cone apexes) are degenerate boundary segments in the parameter plane.
/// </summary>
public static class NSeamSplit
{
    const double Tol = 1e-3;

    /// <summary>Diagnostics (tests/tools).</summary>
    public static Action<string>? Debug;

    static void Dump(string what, NFace f, List<List<Seg>> loops)
    {
        if (Debug == null) return;
        Debug($"{what} {f.Tag.Name} {f.Surface.Kind} sense={f.Sense} loops={loops.Count}");
        foreach (var l in loops)
            Debug("   loop: " + string.Join(" ", l.Select(x => $"[{(x.Edge == null ? "pole" : x.Edge.Curve.GetType().Name[1..])}{(x.Forward ? "+" : "-")} ({x.Uv[0].X:0.###},{x.Uv[0].Y:0.###})->({x.Uv[^1].X:0.###},{x.Uv[^1].Y:0.###})]")));
    }

    public static void Apply(NBody body)
    {
        body.Link();
        foreach (var axis in new[] { 0, 1 })
        {
            var faces = body.Faces.ToList();
            body.Faces.Clear();
            foreach (var f in faces)
            {
                var pieces = Split(body, faces, f, axis);
                body.Faces.AddRange(pieces);
            }
            body.Link();
        }
    }

    sealed class Seg
    {
        public NEdge? Edge;          // null: segment along a pole
        public bool Forward;
        public NVertex V0 = null!, V1 = null!;
        public List<Vec2> Uv = new();
        public int From, To;
        public bool Used;
    }

    /// <summary>Parameter-plane coordinates (u, v·sense), oriented so outer loops run counter-clockwise.</summary>
    static List<List<Seg>> UvLoops(NFace f, double anchorU, double anchorV)
    {
        var s = f.Surface;
        double sg = f.Sense ? 1 : -1;
        var loops = new List<List<Seg>>();
        foreach (var l in f.Loops)
        {
            var segs = new List<Seg>();
            double hu = anchorU, hv = anchorV;
            double firstU = double.NaN;
            foreach (var c in l.Coedges)
            {
                var pts = c.Points(Tol);
                // leaving a pole (apex) the angle is free: such a loop is on its way back to where it started
                // (e.g. down the other side of a seam), so continue from the loop's starting angle
                if (segs.Count > 0 && s.IsPole(c.Start.P) && !double.IsNaN(firstU)) hu = firstU;
                var uv = new List<Vec2>(pts.Count);
                foreach (var p in pts)
                {
                    var q = s.UV(p, hu, s.PeriodicV ? hv : double.NaN);
                    uv.Add(new Vec2(q.X, q.Y * sg));
                    if (!s.IsPole(p)) hu = q.X;
                    hv = q.Y;
                }
                // poles: θ is undefined there, take it from the neighbouring sample
                for (int i = 0; i < pts.Count; i++)
                    if (s.IsPole(pts[i]) && pts.Count > 1)
                    {
                        var j = i == 0 ? 1 : i - 1;
                        uv[i] = new Vec2(uv[j].X, uv[i].Y);
                    }
                var seg = new Seg { Edge = c.Edge, Forward = c.Forward, V0 = c.Start, V1 = c.End, Uv = uv };
                if (double.IsNaN(firstU)) firstU = uv.FirstOrDefault(q => true).X;
                // arriving at a pole with a different θ: the gap is a segment along the pole
                if (segs.Count > 0 && Vec2.Distance(segs[^1].Uv[^1], uv[0]) > 1e-9)
                    segs.Add(new Seg { V0 = c.Start, V1 = c.Start, Uv = new() { segs[^1].Uv[^1], uv[0] } });
                segs.Add(seg);
            }
            if (segs.Count > 0 && Vec2.Distance(segs[^1].Uv[^1], segs[0].Uv[0]) > 1e-9)
            {
                // closing gap: along a pole, or a whole turn (seamless band / closed iso-loop): shift handled by caller
                if (ReferenceEquals(segs[^1].V1, segs[0].V0) && f.Surface.IsPole(segs[0].V0.P))
                    segs.Add(new Seg { V0 = segs[0].V0, V1 = segs[0].V0, Uv = new() { segs[^1].Uv[^1], segs[0].Uv[0] } });
            }
            loops.Add(segs);
        }
        return loops;
    }

    /// <summary>
    /// Adds the boundary of a whole torus face (its holes stay): a circle round the axis and one round the tube
    /// through a common vertex, placed clear of the holes, each in two halves; the loop runs counter-clockwise.
    /// </summary>
    static void WholeTorus(NFace f, List<List<Vec2>> holes)
    {
        var s = f.Surface;
        double sg = f.Sense ? 1 : -1;
        // seam positions: the parameter values farthest inside the gaps between the holes
        double Clear(IEnumerable<(double lo, double hi)> ranges)
        {
            double best = 0, bestGap = -1;
            for (int k = 0; k < 144; k++)
            {
                var c = 2 * Math.PI * k / 144;
                double gap = Math.PI;
                foreach (var (lo, hi) in ranges)
                {
                    var m = 0.5 * (lo + hi); var w = 0.5 * (hi - lo);
                    gap = Math.Min(gap, Math.Abs(NCurve.Wrap(c, m) - m) - w);
                }
                if (gap > bestGap) { bestGap = gap; best = c; }
            }
            if (bestGap <= 0) throw new NotSupportedException($"face {f.Tag.Name}: holes all the way round the torus");
            return best;
        }
        var u0 = Clear(holes.Select(h => (h.Min(p => p.X), h.Max(p => p.X))));
        var v0 = Clear(holes.Select(h => (h.Min(p => p.Y * sg), h.Max(p => p.Y * sg))));  // natural v
        var V = new NVertex(s.Eval(u0, v0));
        var radial = s.F.XDir * Math.Cos(u0) + s.F.YDir * Math.Sin(u0);
        // round the axis at v0, in the direction of increasing u
        var ringC = s.Origin + s.Axis * (s.R2 * Math.Sin(v0));
        var ring = new NCircle(ringC, s.Axis, V.P - ringC, Vec3.Distance(V.P, ringC));
        // round the tube at u0, in the direction of increasing v
        var tubeC = s.Origin + radial * s.R;
        var tube = new NCircle(tubeC, Vec3.Cross(radial, s.Axis), V.P - tubeC, s.R2);
        if (Vec3.Dot(tube.Deriv(0), s.Eval(u0, v0 + 1e-6) - V.P) < 0) tube = new NCircle(tubeC, -Vec3.Cross(radial, s.Axis), V.P - tubeC, s.R2);
        var rm = new NVertex(ring.Eval(Math.PI)); var tm = new NVertex(tube.Eval(Math.PI));
        NEdge r1 = new(ring, 0, Math.PI, V, rm), r2 = new(ring, Math.PI, 2 * Math.PI, rm, V);
        NEdge t1 = new(tube, 0, Math.PI, V, tm), t2 = new(tube, Math.PI, 2 * Math.PI, tm, V);
        // (u0 → u0+2π along v0, then up the tube, back, and down): counter-clockwise in (u, v); reversed when
        // the face's sense flips v
        var cs = new List<NCoedge> { new(r1, true), new(r2, true), new(t1, true), new(t2, true), new(r2, false), new(r1, false), new(t2, false), new(t1, false) };
        if (sg < 0) { cs.Reverse(); cs = cs.Select(c => new NCoedge(c.Edge, !c.Forward)).ToList(); }
        var loop = new NLoop();
        loop.Coedges.AddRange(cs);
        f.Loops.Insert(0, loop);
        f.Anchor = s.Eval(u0 + Math.PI, v0 + Math.PI);
        foreach (var c in cs) { c.Face = f; c.Edge.Uses.Add(c); }
    }

    static bool Closes(List<Seg> loop, int axis) => loop.Count > 0 && Math.Abs(C(loop[^1].Uv[^1], axis) - C(loop[0].Uv[0], axis)) < Math.PI;

    /// <summary>
    /// Turns a band into a face with a seam: every loop that runs all the way round is split at one parameter
    /// value, and the pieces are joined by a seam edge (two loops: across the band; one loop: to the pole or apex).
    /// </summary>
    static void MakeSeam(NFace f, int axis, List<List<Seg>> loops, List<int> open)
    {
        var s = f.Surface;
        double sg = f.Sense ? 1 : -1;
        if (open.Count > 2) throw new NotSupportedException($"face {f.Tag.Name}: more than two loops around the surface");
        // seam position: clear of the holes (it must not cross them) and as far as possible from the vertices of
        // the loops it joins
        var vcoords = open.SelectMany(i => loops[i]).Select(x => C(x.Uv[0], axis)).ToList();
        var holes = Enumerable.Range(0, loops.Count).Where(i => !open.Contains(i) && loops[i].Count > 0)
            .Select(i => { var pts = loops[i].SelectMany(x => x.Uv).Select(q => C(q, axis)).ToList(); return (lo: pts.Min(), hi: pts.Max()); }).ToList();
        double best = 0, bestGap = double.NegativeInfinity;
        for (int k = 0; k < 360; k++)
        {
            var c = 2 * Math.PI * k / 360;
            var gap = vcoords.Count == 0 ? Math.PI : vcoords.Min(v => Math.Abs(NCurve.Wrap(v, c) - c));
            foreach (var (lo, hi) in holes)
            {
                var m = 0.5 * (lo + hi);
                gap = Math.Min(gap, Math.Abs(NCurve.Wrap(c, m) - m) - 0.5 * (hi - lo)); // negative inside a hole's range
            }
            if (gap > bestGap) { bestGap = gap; best = c; }
        }
        if (bestGap <= 0) throw new NotSupportedException($"face {f.Tag.Name}: holes all the way round, no room for a seam");
        double Coord(Vec3 p, double near)
        {
            var q = s.UV(p, axis == 0 ? near : double.NaN, axis == 1 ? near * sg : double.NaN);
            return axis == 0 ? NCurve.Wrap(q.X, near) : NCurve.Wrap(q.Y * sg, near);
        }
        // split each open loop where it crosses the seam position; the new vertex starts the loop
        var starts = new List<(NLoop loop, NVertex v, bool increasing, double other)>();
        foreach (var i in open)
        {
            var segs = loops[i];
            var increasing = C(segs[^1].Uv[^1], axis) > C(segs[0].Uv[0], axis);
            NVertex? v = null; double other = 0;
            foreach (var sgm in segs)
            {
                if (sgm.Edge == null || v != null) continue;
                var e = sgm.Edge;
                var pts = sgm.Forward ? e.Samples(Tol) : Enumerable.Reverse(e.Samples(Tol)).ToList();
                for (int k = 0; k + 1 < sgm.Uv.Count && v == null; k++)
                {
                    double a = C(sgm.Uv[k], axis), b = C(sgm.Uv[k + 1], axis);
                    var c = best + Math.Round((0.5 * (a + b) - best) / (2 * Math.PI)) * 2 * Math.PI;
                    if ((a - c) * (b - c) > 0) continue;
                    double ta = ParamIn(e, pts[k]), tb = ParamIn(e, pts[k + 1]);
                    var t = Roots.Bisect(x => Coord(e.Curve.Eval(x), c) - c, ta, tb);
                    if (t <= e.T0 + 1e-12) v = e.V0;
                    else if (t >= e.T1 - 1e-12) v = e.V1;
                    else { SplitEdge(e, t); v = e.V1; }
                    other = O(Vec2.Lerp(sgm.Uv[k], sgm.Uv[k + 1], 0.5), axis);
                }
            }
            if (v == null) throw new InvalidOperationException($"face {f.Tag.Name}: no seam position on a loop");
            var loop = f.Loops.First(l => l.Coedges.Any(c => ReferenceEquals(c.Edge, segs.First(x => x.Edge != null).Edge) || ReferenceEquals(c.Start, v)));
            loop = f.Loops.First(l => l.Coedges.Any(c => ReferenceEquals(c.Start, v)));
            starts.Add((loop, v, increasing, other));
        }
        List<NCoedge> From(NLoop l, NVertex v)
        {
            var i = l.Coedges.FindIndex(c => ReferenceEquals(c.Start, v));
            return l.Coedges.Skip(i).Concat(l.Coedges.Take(i)).ToList();
        }
        NEdge Seam(NVertex a, NVertex b, Vec3 mid)
        {
            if (s.Kind == SurfaceKind.BSpline) return IsoEdge(s, axis, a, b, mid);
            if (axis == 0 && s.Kind is SurfaceKind.Cylinder or SurfaceKind.Cone)
            {
                var d = b.P - a.P;
                return new NEdge(new NLine(a.P, d), 0, d.Length, a, b);
            }
            var (circle, t0, t1) = CircleThrough(a.P, mid, b.P);
            return new NEdge(circle, t0, t1, a, b);
        }
        Vec3 At(double c, double other) => axis == 0 ? s.Eval(c, other * sg) : s.Eval(other, c * sg);
        var merged = new NLoop();
        if (starts.Count == 2)
        {
            // the loop running towards increasing parameter is the lower side of the band
            var lower = starts.First(x => x.increasing); var upper = starts.First(x => !x.increasing);
            // the face lies above the lower side; when the other direction is periodic too (a torus), the upper
            // side is the next one going up, not the nearest
            var upperOther = (axis == 0 ? s.PeriodicV : s.PeriodicU)
                ? lower.other + ((upper.other - lower.other) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI)
                : upper.other;
            var seam = Seam(lower.v, upper.v, At(best, 0.5 * (lower.other + upperOther)));
            merged.Coedges.AddRange(From(lower.loop, lower.v));
            merged.Coedges.Add(new NCoedge(seam, true));
            merged.Coedges.AddRange(From(upper.loop, upper.v));
            merged.Coedges.Add(new NCoedge(seam, false));
            f.Loops.Remove(lower.loop); f.Loops.Remove(upper.loop);
        }
        else
        {
            // one loop round a pole or apex: the seam runs to it
            var (loop, v, increasing, other) = starts[0];
            double poleOther;
            switch (s.Kind)
            {
                case SurfaceKind.Sphere: poleOther = (increasing ? 1 : -1) * Math.PI / 2; break;
                case SurfaceKind.Cone: poleOther = -s.R / Math.Tan(s.R2) * sg; break;
                default: throw new NotSupportedException($"face {f.Tag.Name}: a single loop around a {s.Kind.ToString().ToLowerInvariant()}");
            }
            var pole = new NVertex(At(best, poleOther));
            var seam = Seam(v, pole, At(best, 0.5 * (other + poleOther)));
            merged.Coedges.AddRange(From(loop, v));
            merged.Coedges.Add(new NCoedge(seam, true));
            merged.Coedges.Add(new NCoedge(seam, false));
            f.Loops.Remove(loop);
        }
        f.Loops.Insert(0, merged);
        foreach (var c in merged.Coedges) c.Face = f;
        // the seam is used twice by this face
        foreach (var c in merged.Coedges.Where(c => c.Edge.Uses.Count < 2 && !c.Edge.Uses.Contains(c))) c.Edge.Uses.Add(c);
        var seamEdges = merged.Coedges.Select(c => c.Edge).Where(e => e.Uses.Count == 0).Distinct();
        foreach (var c in merged.Coedges) if (!c.Edge.Uses.Contains(c)) c.Edge.Uses.Add(c);
    }

    /// <summary>Holes may come out a full turn away from the outer loop: shift them into its range.</summary>
    static void ShiftHoles(List<List<Seg>> loops, int outer, int axis)
    {
        var op = Points(loops[outer]).ToList();
        var mid = 0.5 * (op.Min(p => C(p, axis)) + op.Max(p => C(p, axis)));
        for (int i = 0; i < loops.Count; i++)
        {
            if (i == outer || loops[i].Count == 0) continue;
            var shift = Math.Round((mid - C(loops[i][0].Uv[0], axis)) / (2 * Math.PI)) * 2 * Math.PI;
            if (shift == 0) continue;
            foreach (var sg in loops[i])
                for (int k = 0; k < sg.Uv.Count; k++)
                    sg.Uv[k] = axis == 0 ? new Vec2(sg.Uv[k].X + shift, sg.Uv[k].Y) : new Vec2(sg.Uv[k].X, sg.Uv[k].Y + shift);
        }
    }

    static IEnumerable<Vec2> Points(List<Seg> loop)
    {
        foreach (var s in loop) for (int i = 0; i < s.Uv.Count - 1; i++) yield return s.Uv[i];
    }

    static double Area(IEnumerable<Vec2> poly)
    {
        var p = poly.ToList(); double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return a / 2;
    }

    static bool Inside(List<List<Vec2>> polys, Vec2 q)
    {
        bool inside = false;
        foreach (var poly in polys)
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    static double C(Vec2 p, int axis) => axis == 0 ? p.X : p.Y;
    static double O(Vec2 p, int axis) => axis == 0 ? p.Y : p.X;

    static List<NFace> Split(NBody body, List<NFace> all, NFace f, int axis)
    {
        var s = f.Surface;
        if (axis == 0 ? !s.PeriodicU : !s.PeriodicV) return new() { f };
        double sg = f.Sense ? 1 : -1;
        double anchorU = double.NaN, anchorV = double.NaN;
        if (f.Anchor is { } an) { var q = s.UV(an); anchorU = q.X; anchorV = q.Y; }
        var loops = UvLoops(f, anchorU, anchorV);
        if (loops.Count == 0) return new() { f };
        Dump($"split axis {axis}:", f, loops);
        var polys = loops.Select(l => Points(l).ToList()).ToList();
        // a whole torus with only holes in it (no boundary around it): give it the usual two seam circles
        if (axis == 0 && s.Kind == SurfaceKind.Torus && loops.All(l => Closes(l, 0) && Closes(l, 1)) && polys.All(p => Area(p) < 0))
        {
            WholeTorus(f, polys);
            loops = UvLoops(f, anchorU, anchorV);
            polys = loops.Select(l => Points(l).ToList()).ToList();
            Dump("  whole torus:", f, loops);
        }
        int outer = Enumerable.Range(0, polys.Count).OrderByDescending(i => Math.Abs(Area(polys[i]))).First();
        double lo = polys[outer].Min(p => C(p, axis)), hi = polys[outer].Max(p => C(p, axis));
        // a band without a seam (two parallel circles, no edge across) closes on itself after a full turn
        // a band (loops running all the way round, no seam edge across) first gets a seam
        var open = Enumerable.Range(0, loops.Count).Where(i => !Closes(loops[i], axis)).ToList();
        if (open.Count > 0)
        {
            MakeSeam(f, axis, loops, open);
            loops = UvLoops(f, anchorU, anchorV);
            Dump("  after seam:", f, loops);
            polys = loops.Select(l => Points(l).ToList()).ToList();
            outer = Enumerable.Range(0, polys.Count).OrderByDescending(i => Math.Abs(Area(polys[i]))).First();
            lo = polys[outer].Min(p => C(p, axis)); hi = polys[outer].Max(p => C(p, axis));
            if (loops.Any(l => !Closes(l, axis))) throw new InvalidOperationException($"face {f.Tag.Name}: could not add a seam");
        }
        Debug?.Invoke($"  {f.Tag.Name}: range {lo:0.###}..{hi:0.###}");
        if (hi - lo <= Math.PI + 1e-6) return new() { f };

        ShiftHoles(loops, outer, axis);

        // cut positions: at most half a turn per piece, kept clear of vertices and of edges along an iso-line
        int m = (int)Math.Ceiling((hi - lo) / Math.PI - 1e-9);
        var cuts = new List<double>();
        var nodesC = loops.SelectMany(l => l).SelectMany(x => new[] { C(x.Uv[0], axis), C(x.Uv[^1], axis) }).ToList();
        for (int k = 1; k < m; k++)
        {
            var c = lo + (hi - lo) * k / m;
            for (int tries = 0; tries < 50 && nodesC.Any(n => Math.Abs(n - c) < 1e-4); tries++) c += 1e-3 * (tries % 2 == 0 ? tries + 1 : -(tries + 1));
            cuts.Add(c);
        }

        // split boundary edges where they cross a cut (edges are shared: the neighbours get the vertex too)
        var splits = new Dictionary<NEdge, List<double>>(ReferenceEqualityComparer.Instance);
        double Coord(Vec3 p, double near)
        {
            var q = s.UV(p, axis == 0 ? near : double.NaN, axis == 1 ? near * sg : double.NaN);
            return axis == 0 ? NCurve.Wrap(q.X, near) : NCurve.Wrap(q.Y * sg, near);
        }
        foreach (var loop in loops)
            foreach (var sgm in loop)
            {
                if (sgm.Edge == null) continue;
                var e = sgm.Edge;
                var pts = sgm.Forward ? e.Samples(Tol) : Enumerable.Reverse(e.Samples(Tol)).ToList();
                foreach (var c in cuts)
                    for (int i = 0; i + 1 < sgm.Uv.Count; i++)
                    {
                        double a = C(sgm.Uv[i], axis) - c, b = C(sgm.Uv[i + 1], axis) - c;
                        double t;
                        if (Math.Abs(b) < 1e-13 && i + 1 < sgm.Uv.Count - 1) t = ParamIn(e, pts[i + 1]); // a sample right on the cut
                        else if (a * b < 0 && Math.Abs(a) >= 1e-13) t = Roots.Bisect(x => Coord(e.Curve.Eval(x), c) - c, ParamIn(e, pts[i]), ParamIn(e, pts[i + 1]));
                        else continue;
                        if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                        if (!l.Any(x => Math.Abs(x - t) < 1e-12)) l.Add(t);
                    }
            }
        Debug?.Invoke($"  {f.Tag.Name}: edge splits {string.Join(" ", splits.Select(kv => $"[{kv.Key.T0:0.###}..{kv.Key.T1:0.###}: {string.Join(",", kv.Value.Select(t => t.ToString("0.###")))}]"))}");
        foreach (var (e, ts) in splits)
            foreach (var t in ts.OrderByDescending(x => x))
            {
                if (t <= e.T0 + 1e-12 || t >= e.T1 - 1e-12) continue;
                SplitEdge(e, t);
            }

        // graph of the boundary pieces and the cuts (loops rebuilt with the split edges)
        loops = UvLoops(f, anchorU, anchorV);
        ShiftHoles(loops, outer, axis);
        var nodes = new List<(NVertex v, Vec2 uv)>();
        int Node(NVertex v, Vec2 uv)
        {
            for (int i = 0; i < nodes.Count; i++) if (ReferenceEquals(nodes[i].v, v) && Vec2.Distance(nodes[i].uv, uv) < 1e-6) return i;
            nodes.Add((v, uv)); return nodes.Count - 1;
        }
        var segs = new List<Seg>();
        foreach (var loop in loops)
            foreach (var sgm in loop)
            {
                if (sgm.Edge == null)
                {
                    // pole segment: split where it crosses a cut
                    var a = sgm.Uv[0]; var b = sgm.Uv[^1];
                    var stops = cuts.Where(c => (C(a, axis) - c) * (C(b, axis) - c) < 0)
                        .Select(c => (C(a, axis) - c) / (C(a, axis) - C(b, axis))).OrderBy(x => x).ToList();
                    var prev = a;
                    foreach (var w in stops.Append(1.0))
                    {
                        var q = Vec2.Lerp(a, b, w);
                        segs.Add(new Seg { V0 = sgm.V0, V1 = sgm.V0, Uv = new() { prev, q } });
                        prev = q;
                    }
                    continue;
                }
                segs.Add(sgm);
            }
        foreach (var sgm in segs) { sgm.From = Node(sgm.V0, sgm.Uv[0]); sgm.To = Node(sgm.V1, sgm.Uv[^1]); }
        var polysNow = loops.Select(l => Points(l).ToList()).ToList();
        Dump("  graph:", f, loops);
        foreach (var c in cuts)
        {
            var on = Enumerable.Range(0, nodes.Count).Where(i => Math.Abs(C(nodes[i].uv, axis) - c) < 1e-7).OrderBy(i => O(nodes[i].uv, axis)).ToList();
            for (int k = 0; k + 1 < on.Count; k++)
            {
                var (va, ua) = nodes[on[k]]; var (vb, ub) = nodes[on[k + 1]];
                var mid = Vec2.Lerp(ua, ub, 0.5);
                if (!Inside(polysNow, mid) || ReferenceEquals(va, vb) && s.IsPole(va.P)) continue;
                Vec3 At(Vec2 q) => s.Eval(q.X, q.Y * sg);
                void Add(NVertex a, Vec2 qa, int na, NVertex b, Vec2 qb, int nb)
                {
                    var edge = CutEdge(s, axis, a, b, At(Vec2.Lerp(qa, qb, 0.5)));
                    segs.Add(new Seg { Edge = edge, Forward = true, V0 = a, V1 = b, Uv = new() { qa, qb }, From = na, To = nb });
                    segs.Add(new Seg { Edge = edge, Forward = false, V0 = b, V1 = a, Uv = new() { qb, qa }, From = nb, To = na });
                }
                if (ReferenceEquals(va, vb))
                {
                    // the cut closes on itself (a full circle, e.g. across a whole torus): two halves
                    var vm = new NVertex(At(mid));
                    nodes.Add((vm, mid)); var nm = nodes.Count - 1;
                    Add(va, ua, on[k], vm, mid, nm);
                    Add(vm, mid, nm, vb, ub, on[k + 1]);
                }
                else Add(va, ua, on[k], vb, ub, on[k + 1]);
            }
        }

        // trace the pieces: at each node take the outgoing segment turning most to the left
        var outgoing = segs.GroupBy(x => x.From).ToDictionary(g => g.Key, g => g.ToList());
        var cycles = new List<List<Seg>>();
        foreach (var start in segs)
        {
            if (start.Used) continue;
            var cyc = new List<Seg>();
            var cur = start;
            for (int guard = 0; guard < segs.Count + 2; guard++)
            {
                cur.Used = true; cyc.Add(cur);
                var din = (cur.Uv[^1] - cur.Uv[^2]).Normalized;
                var back = -din;
                Seg? best = null; double bestA = double.MaxValue;
                foreach (var o in outgoing.GetValueOrDefault(cur.To) ?? new())
                {
                    if (o.Used && !ReferenceEquals(o, start)) continue;
                    var dout = (o.Uv[1] - o.Uv[0]).Normalized;
                    var a = -Math.Atan2(Vec2.Cross(back, dout), Vec2.Dot(back, dout));
                    if (a <= 1e-12) a += 2 * Math.PI;
                    if (a < bestA) { bestA = a; best = o; }
                }
                if (best == null) throw new InvalidOperationException($"face {f.Tag.Name}: could not split along its seam (open boundary)");
                if (ReferenceEquals(best, start)) break;
                cur = best;
            }
            cycles.Add(cyc);
        }

        var outerCycles = new List<(List<Seg> cyc, List<Vec2> poly)>();
        var holeCycles = new List<(List<Seg> cyc, List<Vec2> poly)>();
        foreach (var cyc in cycles)
        {
            if (cyc.All(x => x.Edge == null)) continue;
            var poly = Points(cyc).ToList();
            (Area(poly) > 0 ? outerCycles : holeCycles).Add((cyc, poly));
        }
        Debug?.Invoke($"  {f.Tag.Name}: cuts {string.Join(",", cuts.Select(c => c.ToString("0.###")))}, cycles {cycles.Count}, outer {outerCycles.Count}, holes {holeCycles.Count}");
        var result = new List<NFace>();
        var faceOf = new List<NFace>();
        foreach (var (cyc, poly) in outerCycles)
        {
            double umin = poly.Min(p => p.X), umax = poly.Max(p => p.X), vmin = poly.Min(p => p.Y), vmax = poly.Max(p => p.Y);
            var anchor = s.Eval(0.5 * (umin + umax), 0.5 * (vmin + vmax) * sg);
            var nf = new NFace(s, f.Sense, f.Tag, anchor);
            nf.Loops.Add(Loop(cyc));
            result.Add(nf); faceOf.Add(nf);
        }
        foreach (var (cyc, poly) in holeCycles)
        {
            int owner = -1; double bestArea = double.MaxValue;
            for (int i = 0; i < outerCycles.Count; i++)
                if (Inside(new() { outerCycles[i].poly }, poly[0]))
                {
                    var a = Area(outerCycles[i].poly);
                    if (a < bestArea) { bestArea = a; owner = i; }
                }
            if (owner < 0) throw new InvalidOperationException($"face {f.Tag.Name}: a hole lies outside every piece after splitting");
            faceOf[owner].Loops.Add(Loop(cyc));
        }
        // the edges now belong to the pieces (later splits by neighbouring faces must update them)
        foreach (var c in f.Coedges) c.Edge.Uses.Remove(c);
        foreach (var nf in result)
            foreach (var c in nf.Coedges) { c.Face = nf; c.Edge.Uses.Add(c); }
        return result;
    }

    static NLoop Loop(List<Seg> cyc)
    {
        var l = new NLoop();
        foreach (var x in cyc) if (x.Edge != null) l.Coedges.Add(new NCoedge(x.Edge, x.Forward));
        return l;
    }

    /// <summary>The iso-curve of the cut between two points (mid point pm on it): a line on cylinders and cones, a circle otherwise.</summary>
    static NEdge CutEdge(NSurface s, int axis, NVertex a, NVertex b, Vec3 pm)
    {
        if (s.Kind == SurfaceKind.BSpline) return IsoEdge(s, axis, a, b, pm);
        if (axis == 0 && s.Kind is SurfaceKind.Cylinder or SurfaceKind.Cone)
        {
            var d = b.P - a.P;
            return new NEdge(new NLine(a.P, d), 0, d.Length, a, b);
        }
        var (circle, t0, t1) = CircleThrough(a.P, pm, b.P);
        return new NEdge(circle, t0, t1, a, b);
    }

    /// <summary>
    /// The iso-parameter curve of a B-spline face from a to b (through pm): the cut coordinate fixed (u for axis 0,
    /// v for axis 1), the other one running from a's value to b's, the way that passes through pm.
    /// </summary>
    static NEdge IsoEdge(NSurface s, int axis, NVertex a, NVertex b, Vec3 pm)
    {
        var qa = s.UV(a.P); var qm = s.UV(pm, qa.X, qa.Y); var qb = s.UV(b.P, qm.X, qm.Y);
        double fixedC = axis == 0 ? qm.X : qm.Y;
        double t0 = axis == 0 ? qa.Y : qa.X, tm = axis == 0 ? qm.Y : qm.X, t1 = axis == 0 ? qb.Y : qb.X;
        // the running parameter must pass through pm's value between the two ends
        bool rising = t1 > t0;
        if ((tm - t0) * (t1 - tm) < 0) { if (rising) t1 -= 2 * Math.PI; else t1 += 2 * Math.PI; rising = t1 > t0; }
        var curve = new NIso(s, axis, fixedC, rising ? 1 : -1);
        return new NEdge(curve, rising ? t0 : -t0, rising ? t1 : -t1, a, b);
    }

    /// <summary>Circle through p0, pm, p1, oriented from p0 through pm to p1; parameters of p0 and p1.</summary>
    public static (NCircle c, double t0, double t1) CircleThrough(Vec3 p0, Vec3 pm, Vec3 p1)
    {
        var a = pm - p0; var b = p1 - p0;
        var n = Vec3.Cross(a, b);
        var n2 = n.LengthSq;
        if (n2 < 1e-24) throw new InvalidOperationException("cut curve: points are collinear");
        var center = p0 + (Vec3.Cross(n, a) * b.LengthSq + Vec3.Cross(b, n) * a.LengthSq) / (2 * n2);
        var r = Vec3.Distance(center, p0);
        var c = new NCircle(center, n, p0 - center, r);   // counter-clockwise about n: p0 → pm → p1
        var t1 = c.Param(p1, Math.PI);
        while (t1 <= 1e-12) t1 += 2 * Math.PI;
        return (c, 0, t1);
    }

    /// <summary>Curve parameter of a point of the edge, within the edge's range.</summary>
    static double ParamIn(NEdge e, Vec3 p)
    {
        var mid = 0.5 * (e.T0 + e.T1);
        var t = e.Curve.Param(p, e.Curve.Periodic ? mid : null);
        if (e.Curve.Periodic)
        {
            while (t < e.T0 - 1e-9) t += e.Curve.Period;
            while (t > e.T1 + 1e-9) t -= e.Curve.Period;
        }
        return Math.Clamp(t, e.T0, e.T1);
    }

    /// <summary>Splits an edge at parameter t: the edge keeps [T0, t], a new edge takes [t, T1]; every loop using it gets both.</summary>
    public static NEdge SplitEdge(NEdge e, double t)
    {
        var v = new NVertex(e.Curve.Eval(t));
        var e2 = new NEdge(e.Curve, t, e.T1, v, e.V1);
        e.T1 = t; e.V1 = v;
        e.InvalidateSamples();
        foreach (var u in e.Uses.ToList())
        {
            var loop = u.Face.Loops.First(l => l.Coedges.Contains(u));
            var i = loop.Coedges.IndexOf(u);
            var nc = new NCoedge(e2, u.Forward) { Face = u.Face };
            loop.Coedges.Insert(u.Forward ? i + 1 : i, nc);
            e2.Uses.Add(nc);
        }
        return e2;
    }
}

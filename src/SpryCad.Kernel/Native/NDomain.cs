namespace SpryCad.Kernel.Native;

/// <summary>
/// The trimmed parameter domain of a face: its loops mapped to UV' = (u, ±v) so that loops run
/// counter-clockwise (outer) / clockwise (holes) as seen along the outward normal. Faces never wrap
/// around a periodic direction (bodies are seam-split), so the domain is an ordinary planar region.
/// </summary>
public sealed class NDomain
{
    public NFace Face { get; }
    public List<Vec2[]> Loops { get; } = new();
    public List<Vec3[]> Loops3 { get; } = new();
    public double VSign { get; }
    public double URef { get; private set; }
    public double VRef { get; private set; }
    public double UMin { get; private set; } = double.MaxValue;
    public double UMax { get; private set; } = double.MinValue;
    public double VMin { get; private set; } = double.MaxValue;
    public double VMax { get; private set; } = double.MinValue;
    readonly double _tol;
    /// <summary>Per loop, each coedge's run of points: segments Start..End-1 (segment k from point k to k + 1).</summary>
    readonly List<(NCoedge c, int start, int end)[]> _runs = new();
    // near-boundary classification: coedges resampled finely (UV, 3D), by (loop, run)
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), (Vec2[] uv, Vec3[] p3)> _fine = new();

    NDomain(NFace f, double tol)
    {
        Face = f; _tol = tol;
        VSign = f.Sense ? 1 : -1;
        var s = f.Surface;
        bool first = true;
        double anchorU = double.NaN, anchorV = double.NaN;
        if (f.Anchor is { } an && s.PeriodicU)
        {
            var q = s.UV(an);
            anchorU = q.X; if (s.PeriodicV) anchorV = q.Y;
        }
        foreach (var loop in f.Loops)
        {
            var pts3 = new List<Vec3>(); var uv = new List<Vec2>();
            double hint = first ? anchorU : URef;
            (Vec3 p, Vec2 uv)? pendingPole = null;
            double vhint = first ? anchorV : VRef;
            var runs = new List<(NCoedge, int, int)>();
            foreach (var c in loop.Coedges)
            {
                var p = c.Points(tol);
                var cuv = new Vec2[p.Count];
                for (int i = 0; i < p.Count; i++)
                {
                    // faces are patches within ±π of their anchor: wrap against it (continuous unwrapping
                    // fails at poles, where θ legitimately jumps)
                    var q = double.IsNaN(anchorU) ? s.UV(p[i], hint, vhint) : s.UV(p[i], anchorU, double.IsNaN(anchorV) ? vhint : anchorV);
                    cuv[i] = new Vec2(q.X, q.Y * VSign);
                    if (!double.IsNaN(q.X)) hint = q.X;
                    vhint = q.Y;
                }
                // poles / apexes: θ is undefined there, take it from the neighbouring sample of the same edge
                for (int i = 0; i < p.Count; i++)
                    if (s.IsPole(p[i]) && p.Count > 1)
                    {
                        var j = i == 0 ? 1 : i - 1;
                        cuv[i] = new Vec2(cuv[j].X, cuv[i].Y);
                    }
                if (pendingPole is { } pp && cuv.Length > 0 && s.IsPole(p[0]))
                {
                    // previous coedge ended at the pole at u0, this one leaves it at u1: insert pole samples between
                    var u0 = pp.uv.X; var u1 = cuv[0].X;
                    int steps = (int)Math.Ceiling(Math.Abs(u1 - u0) / (Math.PI / 24));
                    for (int k = 1; k < steps; k++) { pts3.Add(pp.p); uv.Add(new Vec2(u0 + (u1 - u0) * k / steps, pp.uv.Y)); }
                }
                pendingPole = null;
                int start = uv.Count;
                for (int i = 0; i < p.Count - 1; i++) { pts3.Add(p[i]); uv.Add(cuv[i]); }
                runs.Add((c, start, uv.Count));
                // the end point of this coedge (start of the next) arrives at a pole with a different u:
                // the pole is a segment in UV, so walk along it in small steps (all mapping to the pole)
                if (p.Count > 0 && s.IsPole(p[^1]))
                {
                    pendingPole = (p[^1], cuv[^1]);
                }
                if (cuv.Length > 0) hint = cuv[^1].X;
            }
            if (pendingPole is { } lp && uv.Count > 0 && s.IsPole(pts3[0]))
            {
                var u0 = lp.uv.X; var u1 = uv[0].X;
                int steps = (int)Math.Ceiling(Math.Abs(u1 - u0) / (Math.PI / 24));
                for (int k = 1; k < steps; k++) { pts3.Add(lp.p); uv.Add(new Vec2(u0 + (u1 - u0) * k / steps, lp.uv.Y)); }
            }
            if (first && uv.Count > 0) { URef = uv[0].X; VRef = uv[0].Y * VSign; }
            if (!first && s.PeriodicU && uv.Count > 0)
            {
                // shift the hole into the range of the outer loop
                var mid = (UMin + UMax) / 2;
                var shift = Math.Round((mid - uv[0].X) / (2 * Math.PI)) * 2 * Math.PI;
                if (shift != 0) for (int i = 0; i < uv.Count; i++) uv[i] = new Vec2(uv[i].X + shift, uv[i].Y);
                if (s.PeriodicV)
                {
                    // a torus is periodic in v too: a hole may come out a full turn away from the face
                    var vmid = (VMin + VMax) / 2;
                    var vshift = Math.Round((vmid - uv[0].Y) / (2 * Math.PI)) * 2 * Math.PI;
                    if (vshift != 0) for (int i = 0; i < uv.Count; i++) uv[i] = new Vec2(uv[i].X, uv[i].Y + vshift);
                }
            }
            foreach (var q in uv)
            {
                UMin = Math.Min(UMin, q.X); UMax = Math.Max(UMax, q.X);
                VMin = Math.Min(VMin, q.Y); VMax = Math.Max(VMax, q.Y);
            }
            Loops.Add(uv.ToArray()); Loops3.Add(pts3.ToArray()); _runs.Add(runs.ToArray());
            first = false;
        }
        if (s.PeriodicU)
        {
            // a face that wraps around (a band) shows up as a jump of more than π between neighbours
            bool wraps = UMax - UMin > 2 * Math.PI - 1e-9;
            foreach (var l in Loops)
                for (int i = 0; i < l.Length && !wraps; i++)
                    if (Math.Abs(l[(i + 1) % l.Length].X - l[i].X) > Math.PI) wraps = true;
            if (!wraps && s.PeriodicV)
                foreach (var l in Loops)
                    for (int i = 0; i < l.Length && !wraps; i++)
                        if (Math.Abs(l[(i + 1) % l.Length].Y - l[i].Y) > Math.PI) wraps = true;
            if (wraps) throw new InvalidOperationException($"face {f.Tag.Name} wraps around its surface");
        }
        else if (s.Kind == SurfaceKind.Screw && s.Screw!.Gen is NCircle)
        {
            // swept by a circle (round wire): v is the circle's angle, periodic although the surface is not marked
            // so; a face all the way round the wire (a tube) wraps
            bool wraps = VMax - VMin > 2 * Math.PI - 1e-9;
            foreach (var l in Loops)
                for (int i = 0; i < l.Length && !wraps; i++)
                    if (Math.Abs(l[(i + 1) % l.Length].Y - l[i].Y) > Math.PI) wraps = true;
            if (wraps) throw new InvalidOperationException($"face {f.Tag.Name} wraps around its surface");
        }
    }

    public static NDomain Of(NFace f, double tol = 1e-3) => new(f, tol);

    /// <summary>
    /// The face's domain at <paramref name="tol"/> or finer, kept on the face (it survives cloning, and is rebuilt
    /// when the loops' geometry changed): repeated point tests on the same faces build it once.
    /// </summary>
    public static NDomain Cached(NFace f, double tol)
    {
        var sig = f.LoopSignature();
        if (f.DomainCache is { } dc && dc.Tol <= tol * 1.0001 && dc.Sig == sig) return dc.Dom;
        var d = new NDomain(f, tol);
        f.DomainCache = new(d, tol, sig);
        return d;
    }

    /// <summary>UV' coordinates of a 3D point (u wrapped into this domain's range).</summary>
    public Vec2 Map(Vec3 p)
    {
        var q = Face.Surface.UV(p, (UMin + UMax) / 2, (VMin + VMax) / 2 * VSign);
        return new Vec2(q.X, q.Y * VSign);
    }

    /// <summary>
    /// UV' coordinates of points along a curve (an edge's samples): on a screw surface each point is found from the
    /// one before it (from the domain's middle, a long helical face's points are turns away: a full search each).
    /// </summary>
    public Vec2[] MapAlong(IReadOnlyList<Vec3> pts)
    {
        var res = new Vec2[pts.Count];
        if (Face.Surface.Kind != SurfaceKind.Screw)
        {
            for (int i = 0; i < res.Length; i++) res[i] = Map(pts[i]);
            return res;
        }
        double hu = (UMin + UMax) / 2, hv = (VMin + VMax) / 2 * VSign;
        for (int i = 0; i < res.Length; i++)
        {
            var q = Face.Surface.UV(pts[i], hu, hv);
            res[i] = new Vec2(q.X, q.Y * VSign);
            hu = q.X; hv = q.Y;
        }
        return res;
    }

    public Vec3 Eval(Vec2 uv) => Face.Surface.Eval(uv.X, uv.Y * VSign);

    // boxes of runs of boundary segments (segment k of a loop runs from point k to point k + 1, wrapping), built on
    // first use: UV boxes of Loops, 3D boxes of Loops3. One reference each: safe to publish across threads.
    const int Run = 16;
    readonly record struct Box2(int Start, int End, double MinX, double MaxX, double MinY, double MaxY);
    readonly record struct Box3(int Start, int End, Vec3 Min, Vec3 Max);
    Box2[][]? _boxes2;
    Box3[][]? _boxes3;

    Box2[][] Boxes2()
    {
        if (_boxes2 is { } bx) return bx;
        var res = new Box2[Loops.Count][];
        for (int li = 0; li < Loops.Count; li++)
        {
            var l = Loops[li]; int n = l.Length;
            var r = new Box2[(n + Run - 1) / Run];
            for (int k = 0; k < r.Length; k++)
            {
                int s = k * Run, e = Math.Min(n, s + Run);
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                for (int i = s; i <= e; i++)
                {
                    var q = l[i % n];
                    // NaN coordinates: a box that is never skipped
                    if (double.IsNaN(q.X) || double.IsNaN(q.Y)) { x0 = y0 = double.NegativeInfinity; x1 = y1 = double.PositiveInfinity; break; }
                    x0 = Math.Min(x0, q.X); x1 = Math.Max(x1, q.X); y0 = Math.Min(y0, q.Y); y1 = Math.Max(y1, q.Y);
                }
                r[k] = new Box2(s, e, x0, x1, y0, y1);
            }
            res[li] = r;
        }
        return _boxes2 = res;
    }

    Box3[][] Boxes3()
    {
        if (_boxes3 is { } bx) return bx;
        var res = new Box3[Loops3.Count][];
        for (int li = 0; li < Loops3.Count; li++)
        {
            var l = Loops3[li]; int n = l.Length;
            var r = new Box3[(n + Run - 1) / Run];
            for (int k = 0; k < r.Length; k++)
            {
                int s = k * Run, e = Math.Min(n, s + Run);
                Vec3 lo = l[s], hi = l[s];
                for (int i = s; i <= e; i++) { var q = l[i % n]; lo = Vec3.Min(lo, q); hi = Vec3.Max(hi, q); }
                r[k] = new Box3(s, e, lo, hi);
            }
            res[li] = r;
        }
        return _boxes3 = res;
    }

    public bool ContainsUV(Vec2 p)
    {
        // crossings of the ray from p towards +u; a run of segments all above or all below p (or all to its left,
        // with a margin for the rounding of the crossing) has none. The order of the segments does not matter.
        bool inside = false;
        var boxes = Boxes2();
        for (int li = 0; li < Loops.Count; li++)
        {
            var poly = Loops[li]; int n = poly.Length;
            foreach (var bx in boxes[li])
            {
                if (bx.MinY > p.Y || bx.MaxY <= p.Y) continue;
                if (p.X > bx.MaxX + 1e-9 * (Math.Abs(bx.MaxX) + Math.Abs(bx.MinX) + 1)) continue;
                for (int k = bx.Start; k < bx.End; k++)
                {
                    // as (i, j) = (k + 1, k): the crossing computed from the same end
                    var a = poly[(k + 1) % n]; var b = poly[k];
                    if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
                }
            }
        }
        return inside;
    }

    /// <summary>Distance (3D) from p to the face boundary polyline.</summary>
    public double BoundaryDistance(Vec3 p) => BoundaryDistanceBelow(p, double.MaxValue);

    /// <summary>
    /// <see cref="BoundaryDistance"/> where it is below <paramref name="limit"/>; otherwise some value not below it.
    /// Runs of segments whose box is no nearer than the best so far are skipped.
    /// </summary>
    double BoundaryDistanceBelow(Vec3 p, double limit)
    {
        // squared distances (the root is monotonic: the same minimum); NaN propagates as before. A limit: segments a
        // little beyond it are still measured, so a distance not measured has a root above the limit
        double init = limit == double.MaxValue ? double.MaxValue : limit * limit * (1 + 1e-9), best = init;
        var boxes = Boxes3();
        for (int li = 0; li < Loops3.Count; li++)
        {
            var l = Loops3[li]; int n = l.Length;
            foreach (var bx in boxes[li])
            {
                // distance to the box, less a margin for the rounding of the segment points
                var e = Vec3.Max(Vec3.Max(bx.Min - p, p - bx.Max), Vec3.Zero);
                var pad = 1e-9 * ((bx.Max - bx.Min).Length + bx.Min.Length + bx.Max.Length + p.Length);
                var lb = Math.Max(0, e.Length - pad);
                if (lb * lb > best) continue;
                for (int i = bx.Start; i < bx.End; i++)
                {
                    var a = l[i]; var b = l[(i + 1) % n];
                    var d = b - a; var l2 = d.LengthSq;
                    var t = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
                    best = Math.Min(best, (a + d * t - p).LengthSq);
                }
            }
        }
        return best == init ? limit : Math.Sqrt(best);
    }

    /// <summary>
    /// +1 inside, 0 on the boundary (within onTol), -1 outside. p is assumed to lie on the surface.
    /// Farther than twice the sampling tolerance from the sampled boundary, the polygon decides (a ray from p only
    /// enters and leaves the slivers between chords and curves). Nearer, p may lie in such a sliver: the coedges
    /// near it are resampled finely (<see cref="FineTol"/>) and the tests run on that boundary.
    /// </summary>
    public int Classify(Vec3 p, double onTol)
    {
        var near = Math.Max(onTol, _tol * 2 + onTol);
        // only compared with this limit: runs farther than it need not be measured
        var bd = BoundaryDistanceBelow(p, near);
        if (!(bd < near)) return ContainsUV(Map(p)) ? 1 : -1;
        var refined = Refined(p, near);
        bd = refined.dist;
        if (bd < onTol) return 0;
        // within the chord error of the fine samples: decide on the exact edge curves (a point on a true arc can lie
        // outside its polyline)
        if (bd < FineTol * 2 + onTol && ExactBoundaryDistance(p) < onTol) return 0;
        return refined.inside(Map(p)) ? 1 : -1;
    }

    /// <summary>The chord tolerance of the boundary next to a point being classified.</summary>
    double FineTol => _tol / 100;

    /// <summary>
    /// The boundary with the coedges within <paramref name="near"/> of p resampled at <see cref="FineTol"/> (the finest
    /// sampling the edge has, if finer): the distance from p to it, and its crossing test.
    /// </summary>
    (double dist, Func<Vec2, bool> inside) Refined(Vec3 p, double near)
    {
        var swapped = new List<(int loop, int run)>();
        double best = double.MaxValue;
        for (int li = 0; li < Loops3.Count; li++)
        {
            var l = Loops3[li]; int n = l.Length;
            var runs = _runs[li];
            var covered = new bool[n];
            for (int ri = 0; ri < runs.Length; ri++)
            {
                var (_, a, b) = runs[ri];
                for (int k = a; k < b; k++) covered[k] = true;
                double d = double.MaxValue;
                for (int k = a; k < b; k++) d = Math.Min(d, SegDist(l[k], l[(k + 1) % n], p));
                if (!(d >= near)) swapped.Add((li, ri));
                else best = Math.Min(best, d);
            }
            // segments of no coedge (along a pole): kept
            for (int k = 0; k < n; k++) if (!covered[k]) best = Math.Min(best, SegDist(l[k], l[(k + 1) % n], p));
        }
        var fine = swapped.Select(x => Fine(x.loop, x.run)).ToList();
        foreach (var (_, f3) in fine)
            for (int k = 0; k + 1 < f3.Length; k++) best = Math.Min(best, SegDist(f3[k], f3[k + 1], p));
        bool Inside(Vec2 q)
        {
            bool inside = false;
            // (b, a) as the polygon test's (j, i): the crossing computed from the later end
            void Seg(Vec2 b, Vec2 a)
            {
                if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            for (int li = 0; li < Loops.Count; li++)
            {
                var poly = Loops[li]; int n = poly.Length;
                var runs = _runs[li];
                var swappedHere = new bool[n];
                for (int w = 0; w < swapped.Count; w++)
                {
                    if (swapped[w].loop != li) continue;
                    var (_, a, b) = runs[swapped[w].run];
                    for (int k = a; k < b; k++) swappedHere[k] = true;
                    var fu = fine[w].uv;
                    for (int k = 0; k + 1 < fu.Length; k++) Seg(fu[k], fu[k + 1]);
                }
                for (int k = 0; k < n; k++) if (!swappedHere[k]) Seg(poly[k], poly[(k + 1) % n]);
            }
            return inside;
        }
        return (best, Inside);

        static double SegDist(Vec3 a, Vec3 b, Vec3 p)
        {
            var d = b - a; var l2 = d.LengthSq;
            var t = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
            return (a + d * t - p).Length;
        }
    }

    /// <summary>
    /// A coedge's points at <see cref="FineTol"/> (or its edge's finest sampling, if finer) in this domain's UV: mapped
    /// from the run's first point onward, and ending on the run's first point and the next run's exactly, so the
    /// boundary stays closed where the run is swapped in.
    /// </summary>
    (Vec2[] uv, Vec3[] p3) Fine(int li, int ri) => _fine.GetOrAdd((li, ri), _ =>
    {
        var s = Face.Surface;
        var (c, a, b) = _runs[li][ri];
        var poly = Loops[li]; var l3 = Loops3[li]; int n = poly.Length;
        var pts = c.Points(FineTol, finest: true);
        var uv = new Vec2[pts.Count];
        double hu = poly[a].X, hv = poly[a].Y * VSign;
        for (int i = 0; i < pts.Count; i++)
        {
            var q = s.UV(pts[i], hu, hv);
            uv[i] = new Vec2(q.X, q.Y * VSign);
            if (!double.IsNaN(q.X)) hu = q.X;
            hv = q.Y;
        }
        for (int i = 0; i < pts.Count; i++)
            if (s.IsPole(pts[i]) && pts.Count > 1) { var j = i == 0 ? 1 : i - 1; uv[i] = new Vec2(uv[j].X, uv[i].Y); }
        var p3 = pts.ToArray();
        uv[0] = poly[a]; p3[0] = l3[a];
        uv[^1] = poly[b % n]; p3[^1] = l3[b % n];
        return (uv, p3);
    });

    /// <summary>Distance from p to the face's exact boundary curves.</summary>
    public double ExactBoundaryDistance(Vec3 p)
    {
        double best = double.MaxValue;
        foreach (var c in Face.Coedges)
        {
            var e = c.Edge;
            double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
            var t = e.Curve.Param(p, e.Curve.Periodic ? 0.5 * (lo + hi) : null);
            var q = t < lo ? e.Curve.Eval(lo) : t > hi ? e.Curve.Eval(hi) : e.Curve.Eval(t);
            best = Math.Min(best, Math.Min(Vec3.Distance(p, q), Math.Min(Vec3.Distance(p, e.V0.P), Vec3.Distance(p, e.V1.P))));
        }
        return best;
    }

    public double SignedArea()
    {
        double a = 0;
        foreach (var l in Loops)
            for (int i = 0, j = l.Length - 1; i < l.Length; j = i++) a += l[j].X * l[i].Y - l[i].X * l[j].Y;
        return a / 2;
    }

    System.Runtime.CompilerServices.StrongBox<Vec3>? _interior; // one reference: safe to publish across threads

    /// <summary>
    /// A point deep inside the face: among triangle centroids and their sub-points, the one farthest
    /// from the boundary (so classification is not confused by nearby or tangent faces).
    /// </summary>
    public Vec3 InteriorPoint()
    {
        if (_interior is { } cached) return cached.Value;
        var all = Loops.SelectMany(l => l).ToArray();
        var cands = new List<Vec2>();
        // a coarse grid first (cheap even for faces with many holes)
        for (int i = 1; i < 7; i++)
            for (int j = 1; j < 7; j++)
            {
                var q = new Vec2(UMin + (UMax - UMin) * i / 7, VMin + (VMax - VMin) * j / 7);
                if (ContainsUV(q)) cands.Add(q);
            }
        // a regular grid can land in the holes of a regular hole pattern: try a low-discrepancy (R2) point set
        // before triangulating, which is slow for faces with hundreds of holes
        for (int k = 0; k < 64 && cands.Count < 6; k++)
        {
            double fu = (0.5 + k * 0.7548776662466927) % 1, fv = (0.5 + k * 0.5698402909980532) % 1;
            var q = new Vec2(UMin + (UMax - UMin) * fu, VMin + (VMax - VMin) * fv);
            if (ContainsUV(q)) cands.Add(q);
        }
        if (cands.Count < 6)
        {
            // thin or oddly shaped domain: use triangle centroids
            var idx = Triangulator.Triangulate(Loops);
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                var a = all[idx[i]]; var b = all[idx[i + 1]]; var d = all[idx[i + 2]];
                var c = (a + b + d) / 3;
                cands.Add(c);
                cands.Add((c + a) / 2); cands.Add((c + b) / 2); cands.Add((c + d) / 2);
            }
        }
        if (cands.Count > 48) cands = cands.Where((_, k) => k % (cands.Count / 48 + 1) == 0).ToList();
        Vec3 best = all.Length > 0 ? Eval(all[0]) : Vec3.Zero; double bestD = -1;
        foreach (var c in cands)
        {
            if (!ContainsUV(c)) continue;
            var p = Eval(c);
            var d = BoundaryDistance(p);
            if (d > bestD) { bestD = d; best = p; }
        }
        _interior = new System.Runtime.CompilerServices.StrongBox<Vec3>(best);
        return best;
    }

    /// <summary>Points on the surface inside the domain's UV box (for bounds).</summary>
    public IEnumerable<Vec3> InteriorSamples(int n)
    {
        if (UMax < UMin) yield break;
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                var uv = new Vec2(UMin + (UMax - UMin) * i / n, VMin + (VMax - VMin) * j / n);
                yield return Eval(uv);
            }
    }
}

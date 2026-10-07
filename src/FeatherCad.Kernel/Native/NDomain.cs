namespace FeatherCad.Kernel.Native;

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
                for (int i = 0; i < p.Count - 1; i++) { pts3.Add(p[i]); uv.Add(cuv[i]); }
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
            Loops.Add(uv.ToArray()); Loops3.Add(pts3.ToArray());
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

    public Vec3 Eval(Vec2 uv) => Face.Surface.Eval(uv.X, uv.Y * VSign);

    public bool ContainsUV(Vec2 p)
    {
        bool inside = false;
        foreach (var poly in Loops)
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
        return inside;
    }

    /// <summary>Distance (3D) from p to the face boundary polyline.</summary>
    public double BoundaryDistance(Vec3 p)
    {
        double best = double.MaxValue;
        foreach (var l in Loops3)
            for (int i = 0; i < l.Length; i++)
            {
                var a = l[i]; var b = l[(i + 1) % l.Length];
                var d = b - a; var l2 = d.LengthSq;
                var t = l2 > 0 ? Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1) : 0;
                best = Math.Min(best, (a + d * t - p).Length);
            }
        return best;
    }

    /// <summary>+1 inside, 0 on the boundary (within onTol), -1 outside. p is assumed to lie on the surface.</summary>
    public int Classify(Vec3 p, double onTol)
    {
        var bd = BoundaryDistance(p);
        if (bd < onTol) return 0;
        // within the chord error of the sampled boundary: decide on the exact edge curves (a point on a
        // true arc can lie outside its polyline)
        if (bd < _tol * 2 + onTol && ExactBoundaryDistance(p) < onTol) return 0;
        return ContainsUV(Map(p)) ? 1 : -1;
    }

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

    Vec3? _interior;

    /// <summary>
    /// A point deep inside the face: among triangle centroids and their sub-points, the one farthest
    /// from the boundary (so classification is not confused by nearby or tangent faces).
    /// </summary>
    public Vec3 InteriorPoint()
    {
        if (_interior is { } cached) return cached;
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
        _interior = best;
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

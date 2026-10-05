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
        foreach (var loop in f.Loops)
        {
            var pts3 = new List<Vec3>(); var uv = new List<Vec2>();
            double hint = first ? double.NaN : URef;
            foreach (var c in loop.Coedges)
            {
                var p = c.Points(tol);
                var cuv = new Vec2[p.Count];
                for (int i = 0; i < p.Count; i++)
                {
                    var q = s.UV(p[i], hint);
                    cuv[i] = new Vec2(q.X, q.Y * VSign);
                    if (!double.IsNaN(q.X)) hint = q.X;
                }
                // poles / apexes: θ is undefined there, take it from the neighbouring sample of the same edge
                for (int i = 0; i < p.Count; i++)
                    if (s.IsPole(p[i]) && p.Count > 1)
                    {
                        var j = i == 0 ? 1 : i - 1;
                        cuv[i] = new Vec2(cuv[j].X, cuv[i].Y);
                    }
                for (int i = 0; i < p.Count - 1; i++) { pts3.Add(p[i]); uv.Add(cuv[i]); }
                if (cuv.Length > 0) hint = cuv[^1].X;
            }
            if (first && uv.Count > 0) URef = uv[0].X;
            if (!first && s.PeriodicU && uv.Count > 0)
            {
                // shift the hole into the range of the outer loop
                var mid = (UMin + UMax) / 2;
                var shift = Math.Round((mid - uv[0].X) / (2 * Math.PI)) * 2 * Math.PI;
                if (shift != 0) for (int i = 0; i < uv.Count; i++) uv[i] = new Vec2(uv[i].X + shift, uv[i].Y);
            }
            foreach (var q in uv)
            {
                UMin = Math.Min(UMin, q.X); UMax = Math.Max(UMax, q.X);
                VMin = Math.Min(VMin, q.Y); VMax = Math.Max(VMax, q.Y);
            }
            Loops.Add(uv.ToArray()); Loops3.Add(pts3.ToArray());
            first = false;
        }
        if (s.PeriodicU && UMax - UMin > 2 * Math.PI + 1e-6)
            throw new InvalidOperationException($"face {f.Tag.Name} wraps around its surface");
    }

    public static NDomain Of(NFace f, double tol = 1e-3) => new(f, tol);

    /// <summary>UV' coordinates of a 3D point (u wrapped into this domain's range).</summary>
    public Vec2 Map(Vec3 p)
    {
        var q = Face.Surface.UV(p, (UMin + UMax) / 2);
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
        if (BoundaryDistance(p) < onTol) return 0;
        return ContainsUV(Map(p)) ? 1 : -1;
    }

    public double SignedArea()
    {
        double a = 0;
        foreach (var l in Loops)
            for (int i = 0, j = l.Length - 1; i < l.Length; j = i++) a += l[j].X * l[i].Y - l[i].X * l[j].Y;
        return a / 2;
    }

    /// <summary>A point well inside the face (centroid of the largest triangle of its domain).</summary>
    public Vec3 InteriorPoint()
    {
        var idx = Triangulator.Triangulate(Loops);
        var all = Loops.SelectMany(l => l).ToArray();
        double best = -1; Vec2 c = all.Length > 0 ? all[0] : default;
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            var a = all[idx[i]]; var b = all[idx[i + 1]]; var d = all[idx[i + 2]];
            var area = Math.Abs(Vec2.Cross(b - a, d - a));
            // prefer large, well-shaped triangles
            var per = (b - a).Length + (d - b).Length + (a - d).Length;
            var q = per > 0 ? area / (per * per) : 0;
            var score = area * (0.25 + q);
            if (score > best) { best = score; c = (a + b + d) / 3; }
        }
        return Eval(c);
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

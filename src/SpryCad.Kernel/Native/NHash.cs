namespace SpryCad.Kernel.Native;

/// <summary>64-bit content hash of a native body: geometry (exact bits) and face names, in build order.</summary>
public static class NHash
{
    public static ulong Of(NBody b)
    {
        var h = new H();
        h.Add(b.Faces.Count);
        foreach (var f in b.Faces) { h.Add(f.Tag.Name); AddFace(ref h, f); }
        return h.Value;
    }

    /// <summary>
    /// Each UI face's geometry (its faces by that name, in build order): the exact surface and boundary, not the
    /// name. A face kept by name whose hash differs between two builds was changed (the visual diff).
    /// </summary>
    public static Dictionary<string, ulong> Faces(NBody b)
    {
        var hs = new Dictionary<string, H>();
        foreach (var f in b.Faces)
        {
            var h = hs.TryGetValue(f.Tag.Name, out var had) ? had : new H();
            AddFace(ref h, f);
            hs[f.Tag.Name] = h;
        }
        return hs.ToDictionary(kv => kv.Key, kv => kv.Value.Value);
    }

    static void AddFace(ref H h, NFace f)
    {
        h.Add(f.Sense ? 1 : 0);
        var s = f.Surface;
        h.Add((int)s.Kind);
        if (s.Kind == SurfaceKind.Plane) { h.Add(s.Plane!.Normal); h.Add(s.Plane.D); }
        else if (s.Kind == SurfaceKind.Screw)
        {
            var sc = s.Screw!; var m = sc.Motion;
            h.Add(m.O); h.Add(m.X); h.Add(m.Y); h.Add(m.A); h.Add(m.K); h.Add(sc.U0); h.Add(sc.U1);
            for (int k = 0; k <= 4; k++) h.Add(sc.Gen.Eval(sc.V0 + (sc.V1 - sc.V0) * k / 4));
        }
        else if (s.Kind == SurfaceKind.Pipe) { h.Add(s.R); foreach (var p in s.Spine!.P) h.Add(p); foreach (var d in s.Spine.D) h.Add(d); }
        else if (s.Kind == SurfaceKind.Offset) { h.Add(s.R); var bn = s.Base!.Nurbs!; foreach (var p in bn.P) h.Add(p); foreach (var k in bn.U) h.Add(k); foreach (var k in bn.V) h.Add(k); }
        else if (s.Kind == SurfaceKind.BSpline) { foreach (var p in s.Nurbs!.P) h.Add(p); foreach (var k in s.Nurbs.U) h.Add(k); foreach (var k in s.Nurbs.V) h.Add(k); if (s.Nurbs.W != null) foreach (var w in s.Nurbs.W) h.Add(w); }
        else { h.Add(s.Origin); h.Add(s.Axis); h.Add(s.F.XDir); h.Add(s.R); h.Add(s.R2); }
        h.Add(f.Loops.Count);
        foreach (var l in f.Loops)
        {
            h.Add(l.Coedges.Count);
            foreach (var c in l.Coedges)
            {
                var e = c.Edge;
                h.Add(c.Forward ? 1 : 0); h.Add(e.T0); h.Add(e.T1); h.Add(e.V0.P); h.Add(e.V1.P);
                switch (e.Curve)
                {
                    case NLine ln: h.Add(1); h.Add(ln.P); h.Add(ln.D); break;
                    case NCircle ci: h.Add(2); h.Add(ci.C); h.Add(ci.X); h.Add(ci.Y); h.Add(ci.R); break;
                    case NEllipse el: h.Add(3); h.Add(el.C); h.Add(el.X); h.Add(el.Y); h.Add(el.A); h.Add(el.B); break;
                    case NHyperbola hy: h.Add(8); h.Add(hy.C); h.Add(hy.X); h.Add(hy.Y); h.Add(hy.A); h.Add(hy.B); break;
                    case NParabola pa: h.Add(9); h.Add(pa.C); h.Add(pa.X); h.Add(pa.Y); h.Add(pa.F); break;
                    case NSpline sp: h.Add(4); h.Add(sp.S.Degree); foreach (var p in sp.S.Poles) h.Add(p); foreach (var k in sp.S.Knots) h.Add(k); break;
                    case NPolyline pl: h.Add(5); foreach (var p in pl.Pts) h.Add(p); break;
                    case NContact ct: h.Add(6); h.Add(ct.OnB ? 1 : 0); foreach (var p in ct.Spine.P) h.Add(p); break;
                    case NSection sc: h.Add(7); h.Add(sc.P0); h.Add(sc.P1); break;
                    default: h.Add(e.Curve.GetType().Name); break;
                }
            }
        }
    }

    struct H
    {
        ulong _h;
        public H() { _h = 0x9E3779B97F4A7C15UL; }
        public readonly ulong Value => Mix(_h);
        public void Add(ulong v) => _h = Mix(_h ^ (v + 0x9E3779B97F4A7C15UL + (_h << 6) + (_h >> 2)));
        public void Add(int v) => Add((ulong)(uint)v);
        public void Add(double v) => Add((ulong)BitConverter.DoubleToInt64Bits(v == 0 ? 0.0 : v));
        public void Add(Vec3 v) { Add(v.X); Add(v.Y); Add(v.Z); }
        public void Add(string s) { Add(s.Length); foreach (var ch in s) Add((ulong)ch); }
        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

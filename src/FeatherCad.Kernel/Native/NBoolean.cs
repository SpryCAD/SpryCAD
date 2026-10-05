namespace FeatherCad.Kernel.Native;

public sealed class NBooleanException(string msg) : Exception(msg);

/// <summary>
/// Exact boolean operations on native B-reps (planes, cylinders, cones, spheres, tori).
/// Edges are split where they cross the other body, section curves are computed from the exact
/// surfaces (analytically, or traced and projected onto both surfaces), faces are split in their UV
/// domains, pieces are classified by exact ray casting, and the result is sewn and simplified.
/// Throws <see cref="NBooleanException"/> for configurations it cannot handle; callers fall back.
/// </summary>
public sealed class NBoolean
{
    public static Action<string>? Trace;
    static void T(string m) => Trace?.Invoke(m);
    static string Dump(IEnumerable<NFace> fs) => string.Join(", ", fs.GroupBy(f => f.Tag.Name).Select(g => $"{g.Key}x{g.Count()}[{string.Join("/", g.Select(f => f.Loops.Count))}]"));
    readonly double _tol;      // geometric coincidence
    readonly double _onTol;    // "on the boundary" for UV/domain decisions
    readonly double _sampleTol;
    readonly Dictionary<NFace, NDomain> _domains = new(ReferenceEqualityComparer.Instance);
    readonly List<NVertex> _pool = new();

    NBoolean(double scale)
    {
        _tol = 1e-7 * Math.Max(10, scale);
        _onTol = 5e-6 * Math.Max(10, scale);
        _sampleTol = 2e-6 * Math.Max(10, scale);
    }

    enum State { Out, In, OnSame, OnOpposite }

    public static NBody Run(NBody a, NBody b, BoolOp op)
    {
        var ba = a.Bounds; var bb = b.Bounds;
        // tolerances follow the region where the operands meet: a long through-all tool must not
        // loosen them for the (small) part it actually cuts
        var lo = new Vec3(Math.Max(ba.Min.X, bb.Min.X), Math.Max(ba.Min.Y, bb.Min.Y), Math.Max(ba.Min.Z, bb.Min.Z));
        var hi = new Vec3(Math.Min(ba.Max.X, bb.Max.X), Math.Min(ba.Max.Y, bb.Max.Y), Math.Min(ba.Max.Z, bb.Max.Z));
        var scale = (hi - lo).Length;
        if (!(scale > 0) || hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z) scale = Math.Min(ba.Diagonal, bb.Diagonal);
        var nb = new NBoolean(scale);
        return nb.Execute(a.Clone(), b.Clone(), op);
    }

    NDomain Dom(NFace f)
    {
        if (!_domains.TryGetValue(f, out var d)) _domains[f] = d = NDomain.Of(f, _sampleTol * 500);
        return d;
    }

    NBody Execute(NBody A, NBody B, BoolOp op)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what) { if (Prof.Enabled) Prof.Add("bool." + what, sw.Elapsed.TotalMilliseconds); T($"[{sw.ElapsedMilliseconds} ms] {what}"); sw.Restart(); }
        MergeVertices(A, B);
        A.Link(); B.Link();

        // 1-2. split edges where they meet the other body's faces
        var onSurface = new List<(NEdge e, NFace f)>();
        var splits = new Dictionary<NEdge, List<(double t, NVertex v)>>(ReferenceEqualityComparer.Instance);
        FindEdgeSplits(A, B, splits, onSurface);
        FindEdgeSplits(B, A, splits, onSurface);
        Lap("edge splits");
        ApplySplits(A, splits); ApplySplits(B, splits);
        _domains.Clear();
        A.Link(); B.Link();

        // 3-4. section edges per face pair, imprints for coincident faces
        var sections = new Dictionary<NFace, List<NEdge>>(ReferenceEqualityComparer.Instance);
        void AddSec(NFace f, NEdge e) { if (!sections.TryGetValue(f, out var l)) sections[f] = l = new(); if (!l.Contains(e)) l.Add(e); }
        var faceBoxA = new Dictionary<NFace, Box3>(ReferenceEqualityComparer.Instance);
        var faceBoxB = new Dictionary<NFace, Box3>(ReferenceEqualityComparer.Instance);
        using (Prof.Time("sec.boxes"))
        {
            foreach (var f in A.Faces) faceBoxA[f] = f.Bounds(_sampleTol * 500);
            foreach (var f in B.Faces) faceBoxB[f] = f.Bounds(_sampleTol * 500);
        }
        foreach (var fa in A.Faces)
            foreach (var fb in B.Faces)
            {
                if (!faceBoxA[fa].Overlaps(faceBoxB[fb], _onTol)) continue;
                if (fa.Surface.CoincidentWith(fb.Surface, _tol * 10))
                {
                    using var _i = Prof.Time("sec.imprint");
                    Imprint(fb, fa, AddSec);
                    Imprint(fa, fb, AddSec);
                    continue;
                }
                using (Prof.Time("sec.curves"))
                    foreach (var e in SectionEdges(fa, fb)) { AddSec(fa, e); AddSec(fb, e); }
                // boundary edges of one face lying on the other face's surface split that face
                using (Prof.Time("sec.onsurface"))
                {
                    ImprintOnSurfaceEdges(fa, fb, AddSec);
                    ImprintOnSurfaceEdges(fb, fa, AddSec);
                }
            }

        Lap("sections");
        // 5. split faces
        if (Trace != null) T($"sections: {string.Join(", ", sections.Select(kv => kv.Key.Tag.Name + ":" + kv.Value.Count))}");
        var piecesA = SplitFaces(A, sections);
        var piecesB = SplitFaces(B, sections);
        Lap("split faces");
        if (Trace != null) T($"pieces A: {Dump(piecesA)}");
        if (Trace != null) T($"pieces B: {Dump(piecesB)}");
        _domains.Clear();

        // 6-7. classify and select
        var boxA = A.Bounds; var boxB = B.Bounds;
        var result = new NBody();
        var statesA = ClassifyAll(piecesA, piecesB, boxB, sections);
        var statesB = ClassifyAll(piecesB, piecesA, boxA, sections);
        foreach (var f in piecesA)
        {
            var st = statesA[f];
            if (Trace != null) T($"A {f.Tag.Name} loops={f.Loops.Count} pt={Dom(f).InteriorPoint()} -> {st}");
            bool keep = op switch
            {
                BoolOp.Union => st is State.Out or State.OnSame,
                BoolOp.Intersect => st is State.In or State.OnSame,
                _ => st is State.Out or State.OnOpposite,
            };
            if (keep) result.Faces.Add(f);
        }
        foreach (var f in piecesB)
        {
            var st = statesB[f];
            if (Trace != null) T($"B {f.Tag.Name} loops={f.Loops.Count} pt={Dom(f).InteriorPoint()} -> {st}");
            bool keep = op switch
            {
                BoolOp.Union => st == State.Out,
                BoolOp.Intersect => st == State.In,
                _ => st == State.In,
            };
            if (!keep) continue;
            if (op == BoolOp.Subtract) Reverse(f);
            result.Faces.Add(f);
        }

        Lap("classify");
        if (Trace != null) T($"kept: {Dump(result.Faces)}");
        // 8. sew coincident edges, simplify
        Sew(result);
        result.Link();
        Unify(result);
        if (Trace != null) T($"unified: {Dump(result.Faces)}");
        result.Link();
        MergeEdges(result);
        result.Link();
        Lap("sew/unify/merge");
        var err = result.Validate();
        if (err != null && Trace != null)
            foreach (var f in result.Faces)
                foreach (var l in f.Loops)
                    if (Trace != null) T($"  {f.Tag.Name} sense={f.Sense}: " + string.Join(" ", l.Coedges.Select(c => $"[{c.Start.P}->{c.End.P} {c.Edge.Curve.GetType().Name[1..]}{(c.Forward ? "+" : "-")}]")));
        if (err != null) throw new NBooleanException("result invalid: " + err);
        return result;
    }

    // ------------------------------------------------------------------------------------------
    // vertices

    void MergeVertices(NBody A, NBody B)
    {
        _pool.Clear();
        _pool.AddRange(A.Vertices);
        foreach (var v in B.Vertices)
        {
            var m = _pool.FirstOrDefault(p => Vec3.Distance(p.P, v.P) < _tol * 10);
            if (m == null) { _pool.Add(v); continue; }
            foreach (var e in B.Edges)
            {
                if (ReferenceEquals(e.V0, v)) e.V0 = m;
                if (ReferenceEquals(e.V1, v)) e.V1 = m;
            }
        }
    }

    NVertex Snap(Vec3 p)
    {
        foreach (var v in _pool) if (Vec3.Distance(v.P, p) < _tol * 10) return v;
        var nv = new NVertex(p);
        _pool.Add(nv);
        return nv;
    }

    // ------------------------------------------------------------------------------------------
    // edge splitting

    void FindEdgeSplits(NBody X, NBody Y, Dictionary<NEdge, List<(double, NVertex)>> splits, List<(NEdge, NFace)> onSurface)
    {
        var fboxes = Y.Faces.Select(f => (f, box: f.Bounds(_sampleTol * 500))).ToList();
        foreach (var e in X.Edges)
        {
            var eb = e.Bounds(_sampleTol * 500);
            foreach (var (f, box) in fboxes)
            {
                if (!eb.Overlaps(box, _onTol)) continue;
                var hits = NIntersect.EdgeSurface(e, f.Surface, _tol);
                if (hits.OnSurface) { onSurface.Add((e, f)); continue; }
                foreach (var t in hits.Params)
                {
                    var p = e.Curve.Eval(t);
                    if (Dom(f).Classify(f.Surface.Project(p), _onTol) < 0) continue;
                    if (Vec3.Distance(p, e.V0.P) < _tol * 10 || Vec3.Distance(p, e.V1.P) < _tol * 10) continue;
                    var v = Snap(p);
                    if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                    if (!l.Any(x => ReferenceEquals(x.Item2, v))) l.Add((t, v));
                }
            }
        }
    }

    static void ApplySplits(NBody body, Dictionary<NEdge, List<(double t, NVertex v)>> splits)
    {
        var pieces = new Dictionary<NEdge, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges.ToList())
        {
            if (!splits.TryGetValue(e, out var sp) || sp.Count == 0) continue;
            var ordered = sp.OrderBy(x => x.t).ToList();
            var list = new List<NEdge>();
            double t0 = e.T0; var v0 = e.V0;
            foreach (var (t, v) in ordered)
            {
                if (t <= t0 + 1e-12 || ReferenceEquals(v, v0)) continue;
                list.Add(new NEdge(e.Curve, t0, t, v0, v));
                t0 = t; v0 = v;
            }
            list.Add(new NEdge(e.Curve, t0, e.T1, v0, e.V1));
            pieces[e] = list;
        }
        if (pieces.Count == 0) return;
        foreach (var f in body.Faces)
            foreach (var l in f.Loops)
            {
                var nc = new List<NCoedge>();
                foreach (var c in l.Coedges)
                {
                    if (!pieces.TryGetValue(c.Edge, out var ps)) { nc.Add(c); continue; }
                    if (c.Forward) foreach (var p in ps) nc.Add(new NCoedge(p, true));
                    else for (int i = ps.Count - 1; i >= 0; i--) nc.Add(new NCoedge(ps[i], false));
                }
                l.Coedges.Clear(); l.Coedges.AddRange(nc);
            }
    }

    // ------------------------------------------------------------------------------------------
    // section edges

    IEnumerable<NEdge> SectionEdges(NFace fa, NFace fb)
    {
        var sa = fa.Surface; var sb = fb.Surface;
        // breakpoints: vertices of either face lying on both surfaces
        var bps = fa.Coedges.Concat(fb.Coedges).SelectMany(c => new[] { c.Start, c.End })
            .Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>()
            .Where(v => Math.Abs(sa.F_(v.P)) < _onTol && Math.Abs(sb.F_(v.P)) < _onTol).ToList();
        List<NCurve>? analytic; List<NCurve> curves;
        using (Prof.Time("sec.analytic")) analytic = NIntersect.Analytic(sa, sb, _tol * 10);
        var tsw = System.Diagnostics.Stopwatch.StartNew();
        using (Prof.Time("sec.trace"))
        {
            curves = analytic ?? NIntersect.Trace(fa, sb, _sampleTol * 50).Cast<NCurve>().ToList();
            if (analytic == null && curves.Count == 0)
            {
                // traced from the other side too (small faces)
                curves = NIntersect.Trace(fb, sa, _sampleTol * 50).Cast<NCurve>().ToList();
            }
        }
        if (Prof.Enabled && analytic == null) Prof.Add($"trace {sa.Kind}x{sb.Kind} {(curves.Count > 0 ? "hit" : "miss")}", tsw.Elapsed.TotalMilliseconds);
        NDomain domA, domB;
        using (Prof.Time("sec.domains")) { domA = Dom(fa); domB = Dom(fb); }
        foreach (var c in curves)
        {
            var on = new List<(double t, NVertex v)>();
            foreach (var v in bps)
            {
                var t = c.Param(v.P);
                if (Vec3.Distance(c.Eval(t), v.P) < _onTol * 4) on.Add((t, v));
            }
            if (on.Count == 0)
            {
                // a closed curve entirely inside both faces (e.g. a small hole grazing a bigger one): it touches
                // no existing edge, so give it two vertices of its own; it becomes an inner loop / a new face
                var probe = c is NPolyline pl ? pl.Eval(pl.Total * 0.37) : c.Eval(0);
                if (!c.Periodic || domA.Classify(sa.Project(probe), _onTol) <= 0 || domB.Classify(sb.Project(probe), _onTol) <= 0) continue;
                on.Add((0, Snap(c.Eval(0))));
                on.Add((c.Period / 2, Snap(c.Eval(c.Period / 2))));
            }
            bool periodic = c.Periodic;
            double period = c.Period;
            if (periodic) on = on.Select(x => (Mod(x.t, period), x.v)).ToList();
            on = on.OrderBy(x => x.t).ToList();
            // merge duplicates (same vertex or same parameter)
            var uniq = new List<(double t, NVertex v)>();
            foreach (var x in on) if (uniq.Count == 0 || (!ReferenceEquals(uniq[^1].v, x.v) && Math.Abs(uniq[^1].t - x.t) > 1e-12)) uniq.Add(x);
            if (periodic && uniq.Count > 1 && ReferenceEquals(uniq[0].v, uniq[^1].v)) uniq.RemoveAt(uniq.Count - 1);
            int n = uniq.Count;
            int intervals = periodic ? n : n - 1;
            if (periodic && n == 1) intervals = 1;
            for (int i = 0; i < intervals; i++)
            {
                var (t0, v0) = uniq[i];
                var (t1, v1) = periodic ? uniq[(i + 1) % n] : uniq[i + 1];
                if (periodic && t1 <= t0) t1 += period;
                if (t1 - t0 < 1e-12) continue;
                var mid = c.Eval(0.5 * (t0 + t1));
                int ca, cb;
                using (Prof.Time("sec.midclass")) { ca = domA.Classify(sa.Project(mid), _onTol); cb = domB.Classify(sb.Project(mid), _onTol); }
                if (ca <= 0 || cb <= 0) continue;
                NEdge ne;
                using (Prof.Time("sec.makeedge")) ne = MakeEdge(c, t0, t1, v0, v1, sa, sb);
                yield return ne;
            }
        }
    }

    static double Mod(double t, double p) { t %= p; return t < 0 ? t + p : t; }

    NEdge MakeEdge(NCurve c, double t0, double t1, NVertex v0, NVertex v1, NSurface sa, NSurface sb)
    {
        if (c is NPolyline pl)
        {
            var pts = pl.Between(t0, t1);
            pts[0] = v0.P; pts[^1] = v1.P;
            var spline = FitSpline(pts, _sampleTol * 5);
            if (spline == null) return new NEdge(new NLine(v0.P, v1.P - v0.P), 0, Vec3.Distance(v0.P, v1.P), v0, v1);
            return new NEdge(new NSpline(spline), 0, 1, v0, v1);
        }
        return new NEdge(c, t0, t1, v0, v1);
    }

    /// <summary>
    /// Cubic B-spline through a minimal subset of the exact points (Douglas-Peucker seed), refined until
    /// every exact point lies within tol of the spline.
    /// </summary>
    static BSplineCurve? FitSpline(List<Vec3> pts, double tol)
    {
        int n = pts.Count;
        if (n < 2) return null;
        var keep = new SortedSet<int> { 0, n - 1 };
        void DP(int a, int b)
        {
            if (b - a < 2) return;
            var pa = pts[a]; var d = pts[b] - pa; var l2 = d.LengthSq;
            double best = -1; int bi = -1;
            for (int i = a + 1; i < b; i++)
            {
                var t = l2 > 0 ? Math.Clamp(Vec3.Dot(pts[i] - pa, d) / l2, 0, 1) : 0;
                var dist = (pa + d * t - pts[i]).Length;
                if (dist > best) { best = dist; bi = i; }
            }
            if (best > tol * 20) { keep.Add(bi); DP(a, bi); DP(bi, b); }
        }
        DP(0, n - 1);
        // at least a few interior points so the cubic follows curved sections
        if (keep.Count < 4 && n >= 4) { keep.Add(n / 3); keep.Add(2 * n / 3); }
        if (keep.Count == 2) return null;
        for (int iter = 0; iter < 40; iter++)
        {
            var sel = keep.Select(i => pts[i]).ToList();
            var sp = BSplineCurve.Interpolate(sel, 3);
            var ns = new NSpline(sp);
            double worst = 0; int wi = -1;
            for (int i = 1; i < n - 1; i++)
            {
                if (keep.Contains(i)) continue;
                var dist = Vec3.Distance(ns.Eval(ns.Param(pts[i])), pts[i]);
                if (dist > worst) { worst = dist; wi = i; }
            }
            if (worst <= tol || wi < 0) return sp;
            keep.Add(wi);
        }
        return BSplineCurve.Interpolate(keep.Select(i => pts[i]).ToList(), 3);
    }

    /// <summary>Edges of 'src' that lie on dst's surface and inside dst become section edges of dst.</summary>
    void ImprintOnSurfaceEdges(NFace src, NFace dst, Action<NFace, NEdge> add)
    {
        NDomain? dom = null;
        foreach (var c in src.Coedges)
        {
            var e = c.Edge;
            bool on = true;
            for (int i = 0; i <= 8 && on; i++) if (Math.Abs(dst.Surface.F_(e.At(i / 8.0))) > _tol * 10) on = false;
            if (!on) continue;
            _blockers.Add(e);
            dom ??= Dom(dst);
            if (dom.Classify(dst.Surface.Project(e.Mid), _onTol) > 0) add(dst, e);
        }
    }

    /// <summary>Edges of 'src' lying inside the coincident face 'dst' become section edges of dst.</summary>
    void Imprint(NFace src, NFace dst, Action<NFace, NEdge> add)
    {
        var dom = Dom(dst);
        foreach (var c in src.Coedges)
        {
            var mid = c.Edge.Mid;
            if (dom.Classify(dst.Surface.Project(mid), _onTol) > 0) add(dst, c.Edge);
        }
    }

    // ------------------------------------------------------------------------------------------
    // face splitting (cycle tracing in UV)

    sealed class HalfEdge
    {
        public NEdge Edge = null!;
        public bool Forward;
        public NVertex From = null!, To = null!;
        public Vec2[] Uv = null!;
        public bool Used;
        public double OutAngle, InAngle;
    }

    List<NFace> SplitFaces(NBody body, Dictionary<NFace, List<NEdge>> sections)
    {
        var res = new List<NFace>();
        foreach (var f in body.Faces)
        {
            if (!sections.TryGetValue(f, out var secs) || secs.Count == 0) { res.Add(f); continue; }
            res.AddRange(SplitFace(f, secs));
        }
        return res;
    }

    IEnumerable<NFace> SplitFace(NFace f, List<NEdge> secs)
    {
        var dom = Dom(f);
        var boundaryEdges = new HashSet<NEdge>(f.Coedges.Select(c => c.Edge), ReferenceEqualityComparer.Instance);
        secs = secs.Where(e => !boundaryEdges.Contains(e)).ToList();
        // prune dangling section edges (an end not connected to anything else)
        var vertexDeg = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        void Inc(NVertex v) => vertexDeg[v] = vertexDeg.GetValueOrDefault(v) + 1;
        foreach (var c in f.Coedges) { Inc(c.Start); Inc(c.End); }
        foreach (var e in secs) { Inc(e.V0); Inc(e.V1); }
        bool pruned = true;
        while (pruned)
        {
            pruned = false;
            foreach (var e in secs.ToList())
                if (vertexDeg[e.V0] < 2 || vertexDeg[e.V1] < 2)
                {
                    secs.Remove(e); vertexDeg[e.V0]--; vertexDeg[e.V1]--; pruned = true;
                }
        }
        if (secs.Count == 0) { yield return f; yield break; }

        var hes = new List<HalfEdge>();
        HalfEdge Make(NEdge e, bool fwd)
        {
            var pts = e.Samples(_sampleTol * 500);
            var uv = pts.Select(dom.Map).ToArray();
            if (!fwd) Array.Reverse(uv);
            var h = new HalfEdge { Edge = e, Forward = fwd, From = fwd ? e.V0 : e.V1, To = fwd ? e.V1 : e.V0, Uv = uv };
            h.OutAngle = Dir(uv, true).Angle;
            h.InAngle = Dir(uv, false).Angle;
            return h;
        }
        foreach (var c in f.Coedges) hes.Add(Make(c.Edge, c.Forward));
        foreach (var e in secs) { hes.Add(Make(e, true)); hes.Add(Make(e, false)); }
        var outgoing = hes.GroupBy(h => h.From, ReferenceEqualityComparer.Instance).ToDictionary(g => (NVertex)g.Key!, g => g.ToList(), ReferenceEqualityComparer.Instance);

        HalfEdge? Next(HalfEdge h)
        {
            if (!outgoing.TryGetValue(h.To, out var outs)) return null;
            // reversed arrival direction; take the first outgoing edge clockwise from it (interior on the left)
            var back = h.InAngle + Math.PI;
            HalfEdge? best = null; double bestDelta = double.MaxValue;
            foreach (var o in outs)
            {
                if (o.Used) continue;
                if (ReferenceEquals(o.Edge, h.Edge) && o.Forward != h.Forward && outs.Count > 1) continue; // no immediate U-turn
                var delta = Mod(back - o.OutAngle, 2 * Math.PI);
                if (delta < 1e-9) delta += 2 * Math.PI;
                if (delta < bestDelta) { bestDelta = delta; best = o; }
            }
            return best;
        }

        var cycles = new List<List<HalfEdge>>();
        foreach (var start in hes)
        {
            if (start.Used) continue;
            var cyc = new List<HalfEdge>();
            var h = start;
            int guard = 0;
            while (h != null && !h.Used && guard++ < 100000)
            {
                h.Used = true; cyc.Add(h);
                if (ReferenceEquals(h.To, start.From)) break;
                h = Next(h);
            }
            if (cyc.Count == 0 || !ReferenceEquals(cyc[^1].To, cyc[0].From)) throw new NBooleanException("face split: open cycle");
            cycles.Add(cyc);
        }

        var outers = new List<(List<HalfEdge> cyc, Vec2[] poly, double area)>();
        var holes = new List<(List<HalfEdge> cyc, Vec2[] poly)>();
        foreach (var cyc in cycles)
        {
            var poly = cyc.SelectMany(h => h.Uv.Take(h.Uv.Length - 1)).ToArray();
            var area = Area(poly);
            if (Math.Abs(area) < 1e-14) continue;
            if (area > 0) outers.Add((cyc, poly, area)); else holes.Add((cyc, poly));
        }
        var faces = outers.OrderBy(o => o.area).Select(o => (o, face: NewFace(f, o.cyc))).ToList();
        foreach (var (cyc, poly) in holes)
        {
            // probe just to the left of the hole loop: that is where the owning face's material is
            int k = 0; double best = -1;
            for (int i = 0; i < poly.Length; i++) { var l = (poly[(i + 1) % poly.Length] - poly[i]).Length; if (l > best) { best = l; k = i; } }
            var a0 = poly[k]; var a1 = poly[(k + 1) % poly.Length];
            var probe = Vec2.Lerp(a0, a1, 0.5) + (a1 - a0).Perp * 1e-3;
            var owner = faces.FirstOrDefault(x => PointIn(x.o.poly, probe));
            if (owner.face == null) throw new NBooleanException("face split: hole without owner");
            owner.face.Loops.Add(ToLoop(cyc));
        }
        foreach (var (_, face) in faces) yield return face;
    }

    static NFace NewFace(NFace template, List<HalfEdge> cyc)
    {
        var f = new NFace(template.Surface, template.Sense, template.Tag, template.Anchor);
        f.Loops.Add(ToLoop(cyc));
        return f;
    }

    static NLoop ToLoop(List<HalfEdge> cyc)
    {
        var l = new NLoop();
        foreach (var h in cyc) l.Coedges.Add(new NCoedge(h.Edge, h.Forward));
        return l;
    }

    static Vec2 Dir(Vec2[] uv, bool atStart)
    {
        // direction a short distance along the polyline (robust to tiny first segments)
        if (atStart)
        {
            for (int i = 1; i < uv.Length; i++) { var d = uv[i] - uv[0]; if (d.Length > 1e-12) return d.Normalized; }
        }
        else
        {
            for (int i = uv.Length - 2; i >= 0; i--) { var d = uv[^1] - uv[i]; if (d.Length > 1e-12) return d.Normalized; }
        }
        return new Vec2(1, 0);
    }

    static double Area(Vec2[] p)
    {
        double a = 0;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++) a += p[j].X * p[i].Y - p[i].X * p[j].Y;
        return a / 2;
    }

    static bool PointIn(Vec2[] poly, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    // ------------------------------------------------------------------------------------------
    // classification

    static readonly Vec3[] RayDirs = MakeRayDirs(32);

    /// <summary>Well spread, irrational directions (golden spiral) so rays rarely hit edges or graze faces.</summary>
    static Vec3[] MakeRayDirs(int n)
    {
        var res = new Vec3[n];
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            var y = 1 - (i + 0.5) / n * 2;
            var r = Math.Sqrt(1 - y * y);
            var th = golden * i + 0.1234567;
            res[i] = new Vec3(Math.Cos(th) * r, y, Math.Sin(th) * r).Normalized;
        }
        return res;
    }

    readonly Dictionary<NFace, Box3> _fbox = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<NEdge> _blockers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Classifies pieces by connected regions: pieces sharing an edge that does not touch the other body
    /// (not a section/imprint edge, not lying on its surfaces) have the same state, so only one ray test
    /// per region is needed. Pieces on surfaces coincident with the other body are classified alone.
    /// </summary>
    Dictionary<NFace, State> ClassifyAll(List<NFace> pieces, List<NFace> other, Box3 otherBox, Dictionary<NFace, List<NEdge>> sections)
    {
        foreach (var l in sections.Values) foreach (var e in l) _blockers.Add(e);
        var res = new Dictionary<NFace, State>(ReferenceEqualityComparer.Instance);
        var idx = new Dictionary<NFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pieces.Count; i++) idx[pieces[i]] = i;
        var lone = new bool[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
            foreach (var g in other)
                if (pieces[i].Surface.CoincidentWith(g.Surface, _tol * 10) && FBox(pieces[i]).Overlaps(FBox(g), _onTol)) { lone[i] = true; break; }
        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var users = new Dictionary<NEdge, List<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < pieces.Count; i++)
            foreach (var c in pieces[i].Coedges)
            {
                if (!users.TryGetValue(c.Edge, out var l)) users[c.Edge] = l = new();
                l.Add(i);
            }
        foreach (var (e, us) in users)
        {
            if (us.Count != 2 || _blockers.Contains(e)) continue;
            if (lone[us[0]] || lone[us[1]]) continue;
            // the other body must not pass through the edge
            bool touches = false;
            foreach (var g in other)
            {
                if (!FBox(g).Overlaps(e.Bounds(_sampleTol * 500), _onTol)) continue;
                if (Math.Abs(g.Surface.F_(e.Mid)) < _onTol || Math.Abs(g.Surface.F_(e.V0.P)) < _onTol && Math.Abs(g.Surface.F_(e.V1.P)) < _onTol) { touches = true; break; }
            }
            if (touches) continue;
            parent[Find(us[0])] = Find(us[1]);
        }
        var compState = new Dictionary<int, State>();
        for (int i = 0; i < pieces.Count; i++)
        {
            var r = Find(i);
            if (!compState.TryGetValue(r, out var st)) compState[r] = st = Classify(pieces[i], other, otherBox);
            res[pieces[i]] = st;
        }
        return res;
    }
    Box3 FBox(NFace f)
    {
        if (_fbox.TryGetValue(f, out var b)) return b;
        var d = Dom(f);
        b = Box3.Empty;
        foreach (var l in d.Loops3) foreach (var p in l) b.Add(p);
        if (f.Surface.Kind != SurfaceKind.Plane) foreach (var p in d.InteriorSamples(4)) b.Add(p);
        var pad = _sampleTol * 500;
        b.Add(b.Min - new Vec3(pad, pad, pad)); b.Add(b.Max + new Vec3(pad, pad, pad));
        _fbox[f] = b;
        return b;
    }

    static bool RayHitsBox(Vec3 o, Vec3 d, Box3 b, double pad)
    {
        double tmin = 0, tmax = double.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            var oi = o[i]; var di = d[i]; var mn = b.Min[i] - pad; var mx = b.Max[i] + pad;
            if (Math.Abs(di) < 1e-15) { if (oi < mn || oi > mx) return false; continue; }
            var t1 = (mn - oi) / di; var t2 = (mx - oi) / di;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Math.Max(tmin, t1); tmax = Math.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        return true;
    }

    State Classify(NFace f, List<NFace> other, Box3 otherBox)
    {
        var fb = FBox(f);
        if (!fb.Overlaps(otherBox, _onTol)) return State.Out;
        var p = Dom(f).InteriorPoint();
        return ClassifyPoint(f, p, other, otherBox);
    }

    State ClassifyPoint(NFace f, Vec3 p, List<NFace> other, Box3 otherBox)
    {
        if (!otherBox.Overlaps(new Box3 { Min = p, Max = p }, _onTol)) return State.Out;
        var nf = f.Surface.Normal(p) * (f.Sense ? 1 : -1);
        // on another face?
        foreach (var g in other)
        {
            if (!FBox(g).Overlaps(new Box3 { Min = p, Max = p }, _onTol)) continue;
            if (Math.Abs(g.Surface.F_(p)) > _onTol) continue;
            if (Dom(g).Classify(g.Surface.Project(p), _onTol) < 0) continue;
            var ng = g.Surface.Normal(p) * (g.Sense ? 1 : -1);
            return Vec3.Dot(nf, ng) > 0 ? State.OnSame : State.OnOpposite;
        }
        foreach (var d in RayDirs)
        {
            int count = 0; bool ok = true;
            foreach (var g in other)
            {
                if (!RayHitsBox(p, d, FBox(g), _onTol * 4)) continue;
                foreach (var t in g.Surface.RayHits(p, d))
                {
                    if (t < -_onTol) continue;
                    var q = p + d * t;
                    var c = Dom(g).Classify(q, _onTol * 4);
                    if (c < 0) continue;                 // the surface, but outside this face
                    if (c == 0 || t <= _onTol) { ok = false; break; } // on an edge, or starting on the face
                    // grazing hits are ambiguous
                    var n = g.Surface.Normal(q);
                    if (Math.Abs(Vec3.Dot(n, d)) < 1e-3) { ok = false; break; }
                    count++;
                }
                if (!ok) break;
            }
            if (ok) return count % 2 == 1 ? State.In : State.Out;
        }
        throw new NBooleanException("classification failed");
    }

    static void Reverse(NFace f)
    {
        f.Sense = !f.Sense;
        foreach (var l in f.Loops)
        {
            var rev = l.Coedges.Select(c => new NCoedge(c.Edge, !c.Forward)).Reverse().ToList();
            l.Coedges.Clear(); l.Coedges.AddRange(rev);
        }
    }

    // ------------------------------------------------------------------------------------------
    // sewing and simplification

    /// <summary>Replaces geometrically identical edges (from the two operands) by one edge.</summary>
    void Sew(NBody body)
    {
        WeldVertices(body);
        body.Link();
        var single = body.Edges.Where(e => e.Uses.Count == 1).ToList();
        // partially overlapping open edges (one split where the other is not): split at each other's vertices
        var openVerts = single.SelectMany(e => new[] { e.V0, e.V1 }).Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>().ToList();
        var splits = new Dictionary<NEdge, List<(double t, NVertex v)>>(ReferenceEqualityComparer.Instance);
        foreach (var e in single)
        {
            var box = e.Bounds(_sampleTol * 500);
            foreach (var v in openVerts)
            {
                if (ReferenceEquals(v, e.V0) || ReferenceEquals(v, e.V1) || !box.Contains(v.P, _onTol)) continue;
                var t = e.Curve.Param(v.P, e.Curve.Periodic ? 0.5 * (e.T0 + e.T1) : null);
                if (t <= Math.Min(e.T0, e.T1) + 1e-9 || t >= Math.Max(e.T0, e.T1) - 1e-9) continue;
                if (Vec3.Distance(e.Curve.Eval(t), v.P) > _tol * 10) continue;
                if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                l.Add((t, v));
            }
        }
        if (splits.Count > 0)
        {
            ApplySplits(body, splits);
            body.Link();
            single = body.Edges.Where(e => e.Uses.Count == 1).ToList();
        }
        var byEnds = new Dictionary<(NVertex, NVertex), List<NEdge>>();
        (NVertex, NVertex) Key(NEdge e) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.V0) <= System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.V1) ? (e.V0, e.V1) : (e.V1, e.V0);
        foreach (var e in single)
        {
            var k = Key(e);
            if (!byEnds.TryGetValue(k, out var l)) byEnds[k] = l = new();
            l.Add(e);
        }
        var replace = new Dictionary<NEdge, (NEdge with_, bool flip)>(ReferenceEqualityComparer.Instance);
        foreach (var l in byEnds.Values)
        {
            for (int i = 0; i < l.Count; i++)
            {
                if (replace.ContainsKey(l[i])) continue;
                for (int j = i + 1; j < l.Count; j++)
                {
                    if (replace.ContainsKey(l[j])) continue;
                    if (Vec3.Distance(l[i].Mid, l[j].Mid) > _onTol * 4) continue;
                    var flip = !ReferenceEquals(l[i].V0, l[j].V0) || (l[i].Closed && Vec3.Dot(l[i].Curve.Deriv(l[i].T0), l[j].Curve.Deriv(l[j].T0)) < 0);
                    replace[l[j]] = (l[i], flip);
                    break;
                }
            }
        }
        if (replace.Count == 0) return;
        foreach (var f in body.Faces)
            foreach (var lp in f.Loops)
                for (int i = 0; i < lp.Coedges.Count; i++)
                {
                    var c = lp.Coedges[i];
                    if (replace.TryGetValue(c.Edge, out var r)) lp.Coedges[i] = new NCoedge(r.with_, r.flip ? !c.Forward : c.Forward);
                }
    }

    /// <summary>Replaces coincident vertex objects by one representative (grid hashed).</summary>
    void WeldVertices(NBody body)
    {
        var cell = _tol * 20;
        var grid = new Dictionary<(long, long, long), List<NVertex>>();
        var rep = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
        (long, long, long) K(Vec3 p, int dx, int dy, int dz) => ((long)Math.Floor(p.X / cell) + dx, (long)Math.Floor(p.Y / cell) + dy, (long)Math.Floor(p.Z / cell) + dz);
        foreach (var e in body.Edges)
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (rep.ContainsKey(v)) continue;
                NVertex? found = null;
                for (int dx = -1; dx <= 1 && found == null; dx++)
                    for (int dy = -1; dy <= 1 && found == null; dy++)
                        for (int dz = -1; dz <= 1 && found == null; dz++)
                            if (grid.TryGetValue(K(v.P, dx, dy, dz), out var l))
                                found = l.FirstOrDefault(w => Vec3.Distance(w.P, v.P) < _tol * 10);
                if (found != null) { rep[v] = found; continue; }
                rep[v] = v;
                var k = K(v.P, 0, 0, 0);
                if (!grid.TryGetValue(k, out var lst)) grid[k] = lst = new();
                lst.Add(v);
            }
        foreach (var e in body.Edges.ToList())
        {
            e.V0 = rep[e.V0]; e.V1 = rep[e.V1];
        }
    }

    /// <summary>Merges adjacent faces lying on the same surface with the same orientation.</summary>
    void Unify(NBody body)
    {
        body.Link();
        var faces = body.Faces;
        var idx = new Dictionary<NFace, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < faces.Count; i++) idx[faces[i]] = i;
        var parent = Enumerable.Range(0, faces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var e in body.Edges)
        {
            if (e.Uses.Count != 2) continue;
            var f = e.Uses[0].Face; var g = e.Uses[1].Face;
            if (ReferenceEquals(f, g) || f.Sense != g.Sense) continue;
            if (!ReferenceEquals(f.Surface, g.Surface) && !f.Surface.CoincidentWith(g.Surface, _tol * 10)) continue;
            if (f.Surface.Kind == SurfaceKind.Plane && Vec3.Dot(f.Surface.Plane!.Normal, g.Surface.Plane!.Normal) < 0) continue;
            parent[Find(idx[f])] = Find(idx[g]);
        }
        var groups = Enumerable.Range(0, faces.Count).GroupBy(Find).Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0) return;
        var remove = new HashSet<NFace>(ReferenceEqualityComparer.Instance);
        var add = new List<NFace>();
        foreach (var g in groups)
        {
            var members = g.Select(i => faces[i]).ToList();
            var set = new HashSet<NFace>(members, ReferenceEqualityComparer.Instance);
            // boundary coedges of the group (edges whose other use is outside the group)
            var bnd = members.SelectMany(m => m.Coedges).Where(c => !c.Edge.Uses.All(u => set.Contains(u.Face))).ToList();
            var template = members.OrderBy(m => m.Tag.Seq).First();
            var merged = new NFace(template.Surface, template.Sense, template.Tag, template.Anchor);
            // chain coedges into loops by vertices (ambiguous junctions keep the original order)
            var byStart = bnd.GroupBy(c => c.Start, ReferenceEqualityComparer.Instance).ToDictionary(x => (NVertex)x.Key!, x => x.ToList(), ReferenceEqualityComparer.Instance);
            var used = new HashSet<NCoedge>(ReferenceEqualityComparer.Instance);
            bool ok = true;
            foreach (var c0 in bnd)
            {
                if (used.Contains(c0)) continue;
                var loop = new NLoop();
                var c = c0;
                int guard = 0;
                while (c != null && used.Add(c) && guard++ < 100000)
                {
                    loop.Coedges.Add(new NCoedge(c.Edge, c.Forward));
                    if (ReferenceEquals(c.End, c0.Start)) break;
                    c = byStart.TryGetValue(c.End, out var nx) ? nx.FirstOrDefault(x => !used.Contains(x)) : null;
                }
                if (!ReferenceEquals(loop.Coedges[^1].End, loop.Coedges[0].Start)) { ok = false; break; }
                merged.Loops.Add(loop);
            }
            if (!ok) continue;
            // the merged face must still be a non-wrapping patch on periodic surfaces
            try { NDomain.Of(merged, _sampleTol * 500); }
            catch (InvalidOperationException) { continue; }
            foreach (var m in members) remove.Add(m);
            add.Add(merged);
        }
        body.Faces.RemoveAll(remove.Contains);
        body.Faces.AddRange(add);
    }

    /// <summary>Joins consecutive edges at vertices where nothing else meets and the curve continues.</summary>
    void MergeEdges(NBody body)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            body.Link();
            var edgesAt = new Dictionary<NVertex, List<NEdge>>(ReferenceEqualityComparer.Instance);
            foreach (var e in body.Edges)
                foreach (var v in new[] { e.V0, e.V1 })
                {
                    if (!edgesAt.TryGetValue(v, out var l)) edgesAt[v] = l = new();
                    if (!l.Contains(e)) l.Add(e);
                }
            foreach (var (v, list) in edgesAt)
            {
                if (list.Count != 2) continue;
                var (e1, e2) = (list[0], list[1]);
                if (e1.Closed || e2.Closed || ReferenceEquals(e1, e2)) continue;
                if (!SameCurve(e1.Curve, e2.Curve)) continue;
                // both edges must be used by the same two faces
                var f1 = e1.Uses.Select(u => u.Face).ToHashSet(ReferenceEqualityComparer.Instance);
                if (!e2.Uses.All(u => f1.Contains(u.Face))) continue;
                // order so that e1 ends at v and e2 starts at v along the curve parameter
                NEdge first, second;
                if (ReferenceEquals(e1.V1, v) && ReferenceEquals(e2.V0, v)) { first = e1; second = e2; }
                else if (ReferenceEquals(e2.V1, v) && ReferenceEquals(e1.V0, v)) { first = e2; second = e1; }
                else continue;
                if (!ReferenceEquals(first.Curve, second.Curve) || Math.Abs(first.T1 - second.T0) > 1e-9 * Math.Max(1, Math.Abs(first.T1))) continue;
                if (ReferenceEquals(first.V0, second.V1)) continue; // would create a closed edge
                var joined = new NEdge(first.Curve, first.T0, second.T1, first.V0, second.V1);
                foreach (var f in body.Faces)
                    foreach (var lp in f.Loops)
                    {
                        int n = lp.Coedges.Count;
                        int i1 = lp.Coedges.FindIndex(c => ReferenceEquals(c.Edge, first));
                        int i2 = lp.Coedges.FindIndex(c => ReferenceEquals(c.Edge, second));
                        if (i1 < 0 || i2 < 0) continue;
                        bool fwd = lp.Coedges[i1].Forward;
                        // forward traversal: first then second; reversed: second then first
                        int lead = fwd ? i1 : i2;
                        var rotated = Enumerable.Range(0, n).Select(k => lp.Coedges[(lead + k) % n]).ToList();
                        rotated.RemoveRange(0, 2);
                        rotated.Insert(0, new NCoedge(joined, fwd));
                        lp.Coedges.Clear(); lp.Coedges.AddRange(rotated);
                    }
                changed = true;
                break;
            }
        }
    }

    static bool SameCurve(NCurve a, NCurve b) => ReferenceEquals(a, b);
}

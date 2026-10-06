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
    static readonly object _traceLock = new();
    static void T(string m) { if (Trace != null) lock (_traceLock) Trace?.Invoke(m); }
    static string Dump(IEnumerable<NFace> fs) => string.Join(", ", fs.GroupBy(f => f.Tag.Name).Select(g => $"{g.Key}x{g.Count()}[{string.Join("/", g.Select(f => f.Loops.Count))}]"));
    readonly double _tol;      // geometric coincidence
    readonly double _onTol;    // "on the boundary" for UV/domain decisions
    readonly double _sampleTol;
    readonly Dictionary<NFace, NDomain> _domains = new(ReferenceEqualityComparer.Instance);
    readonly List<NVertex> _pool = new();
    // faces of either operand whose box does not reach the other operand: the boolean leaves them as they are
    // (never split, outside the other body), so every stage skips them and its cost follows the size of the change
    readonly HashSet<NFace> _far = new(ReferenceEqualityComparer.Instance);
    public static bool Local = Environment.GetEnvironmentVariable("FC_NOLOCAL") == null;
    bool Active(NFace f) => !_far.Contains(f);
    IEnumerable<NFace> ActiveFaces(NBody b) => _far.Count == 0 ? b.Faces : b.Faces.Where(Active);
    static List<NEdge> EdgesOf(IEnumerable<NFace> faces)
    {
        var seen = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        var res = new List<NEdge>();
        foreach (var f in faces) foreach (var l in f.Loops) foreach (var c in l.Coedges) if (seen.Add(c.Edge)) res.Add(c.Edge);
        return res;
    }
    static List<NVertex> VerticesOf(IEnumerable<NEdge> edges)
    {
        var seen = new HashSet<NVertex>(ReferenceEqualityComparer.Instance);
        var res = new List<NVertex>();
        foreach (var e in edges) { if (seen.Add(e.V0)) res.Add(e.V0); if (seen.Add(e.V1)) res.Add(e.V1); }
        return res;
    }

    /// <summary>Marks the faces of each operand that cannot reach the other one (see <see cref="_far"/>).</summary>
    void FindFar(NBody A, NBody B)
    {
        _far.Clear();
        var big = 1e300;
        _region = new Box3 { Min = new Vec3(-big, -big, -big), Max = new Vec3(big, big, big) };
        if (!Local) return;
        var pad = _onTol * 10;
        var ba = Box3.Empty; foreach (var f in A.Faces) ba.Add(f.Bounds(_sampleTol * 500));
        var bb = Box3.Empty; foreach (var f in B.Faces) bb.Add(f.Bounds(_sampleTol * 500));
        foreach (var f in A.Faces) if (!f.Bounds(_sampleTol * 500).Overlaps(bb, pad)) _far.Add(f);
        foreach (var f in B.Faces) if (!f.Bounds(_sampleTol * 500).Overlaps(ba, pad)) _far.Add(f);
        // where the operands can meet (both boxes), padded
        var lo = new Vec3(Math.Max(ba.Min.X, bb.Min.X), Math.Max(ba.Min.Y, bb.Min.Y), Math.Max(ba.Min.Z, bb.Min.Z));
        var hi = new Vec3(Math.Min(ba.Max.X, bb.Max.X), Math.Min(ba.Max.Y, bb.Max.Y), Math.Min(ba.Max.Z, bb.Max.Z));
        var pv = new Vec3(pad, pad, pad);
        _region = new Box3 { Min = lo - pv, Max = hi + pv };
    }

    Box3 _region;

    /// <summary>
    /// Edges of the active faces that reach the region where the operands meet. The others keep their
    /// vertices, neighbours and uses: an edge outside both boxes cannot touch the other operand.
    /// </summary>
    List<NEdge> NearEdges(IEnumerable<NFace> faces)
    {
        var res = new List<NEdge>();
        foreach (var e in EdgesOf(faces))
            if (_far.Count == 0 && !Local || e.Bounds(_sampleTol * 500).Overlaps(_region, 0)) res.Add(e);
        return res;
    }

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
        NBody ca, cb; using (Prof.Time("bool.clone")) { ca = a.Clone(); cb = b.Clone(); }
        return nb.Execute(ca, cb, op);
    }

    /// <summary>Builds the missing face domains in parallel (all at the same sampling tolerance, so concurrent
    /// edge sampling produces identical results), then caches them.</summary>
    void PrepareDomains(IEnumerable<NFace> faces, bool interior = true)
    {
        var todo = faces.Where(f => !_domains.ContainsKey(f)).Distinct(ReferenceEqualityComparer.Instance).Cast<NFace>().ToList();
        var built = new NDomain[todo.Count];
        Parallel.For(0, todo.Count, i => { built[i] = Build(todo[i], _sampleTol * 500); if (interior) built[i].InteriorPoint(); });
        for (int i = 0; i < todo.Count; i++) _domains[todo[i]] = built[i];
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<NFace, NDomain> _farDomains = new(ReferenceEqualityComparer.Instance);

    /// <summary>Domains during the parallel ray casts: far faces are built on first hit (most rays never reach them).</summary>
    NDomain RayDom(NFace g) => _far.Contains(g) ? _farDomains.GetOrAdd(g, f => Build(f, _sampleTol * 500)) : Dom(g);

    NDomain Dom(NFace f)
    {
        if (!_domains.TryGetValue(f, out var d)) _domains[f] = d = Build(f, _sampleTol * 500);
        return d;
    }

    /// <summary>
    /// A face's domain, kept on the face for the next operation (it survives cloning, and is rebuilt when the
    /// loops' geometry changed): a big face with many holes is not resampled by every small cut on the part.
    /// </summary>
    NDomain Build(NFace f, double tol)
    {
        if (!Local) return NDomain.Of(f, tol);
        var sig = f.LoopSignature();
        if (f.DomainCache is { } dc && dc.Tol <= tol * 1.0001 && dc.Sig == sig) return dc.Dom;
        var d = NDomain.Of(f, tol);
        f.DomainCache = new(d, tol, sig);
        return d;
    }

    NBody Execute(NBody A, NBody B, BoolOp op)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what) { if (Prof.Enabled) Prof.Add("bool." + what, sw.Elapsed.TotalMilliseconds); T($"[{sw.ElapsedMilliseconds} ms] {what}"); sw.Restart(); }
        FindFar(A, B);
        MergeVertices(A, B);
        A.Link(); B.Link();
        Lap("merge vertices");

        // 1-2. split edges where they meet the other body's faces
        var onSurface = new List<(NEdge e, NFace f)>();
        var splits = new Dictionary<NEdge, List<(double t, NVertex v)>>(ReferenceEqualityComparer.Instance);
        FindEdgeSplits(A, B, splits, onSurface);
        FindEdgeSplits(B, A, splits, onSurface);
        using (Prof.Time("split.crossings")) CrossOnSurfaceEdges(onSurface, splits);
        ImprintSplitVertices(A, B, splits);
        Lap("edge splits");
        ApplySplits(ActiveFaces(A).ToList(), splits); ApplySplits(ActiveFaces(B).ToList(), splits);
        _domains.Clear();
        A.Link(); B.Link();

        // 3-4. section edges per face pair, imprints for coincident faces
        var sections = new Dictionary<NFace, List<NEdge>>(ReferenceEqualityComparer.Instance);
        void AddSec(NFace f, NEdge e) { if (!sections.TryGetValue(f, out var l)) sections[f] = l = new(); if (!l.Contains(e)) l.Add(e); }
        var faceBoxA = new Dictionary<NFace, Box3>(ReferenceEqualityComparer.Instance);
        var faceBoxB = new Dictionary<NFace, Box3>(ReferenceEqualityComparer.Instance);
        using (Prof.Time("sec.boxes"))
        {
            foreach (var f in ActiveFaces(A)) faceBoxA[f] = f.Bounds(_sampleTol * 500);
            foreach (var f in ActiveFaces(B)) faceBoxB[f] = f.Bounds(_sampleTol * 500);
        }
        // candidate face pairs; intersection curves are computed up front (traced ones in parallel: they are
        // pure functions of the two faces), then section edges are assembled sequentially in pair order
        var pairs = new List<(NFace fa, NFace fb, bool coincident)>();
        foreach (var fa in ActiveFaces(A))
            foreach (var fb in ActiveFaces(B))
                if (faceBoxA[fa].Overlaps(faceBoxB[fb], _onTol))
                    pairs.Add((fa, fb, fa.Surface.CoincidentWith(fb.Surface, _tol * 10)));
        var pairCurves = new List<NCurve>[pairs.Count];
        var toTrace = new List<int>();
        using (Prof.Time("sec.analytic"))
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].coincident) continue;
                var an = NIntersect.Analytic(pairs[i].fa.Surface, pairs[i].fb.Surface, _tol * 10);
                if (an != null) pairCurves[i] = an; else toTrace.Add(i);
            }
        using (Prof.Time("sec.trace"))
        {
            var traceTol = _sampleTol * 50;
            // sample the traced faces' edges first, single-threaded: parallel tracing then only reads the caches
            foreach (var i in toTrace) { NDomain.Of(pairs[i].fa, Math.Max(traceTol, 1e-3)); NDomain.Of(pairs[i].fb, Math.Max(traceTol, 1e-3)); }
            Parallel.ForEach(toTrace, i =>
            {
                var (fa, fb, _) = pairs[i];
                List<NCurve> c; using (Prof.Time(Prof.Enabled ? $"tr.{fa.Surface.Kind}x{fb.Surface.Kind}" : "")) c = NIntersect.Trace(fa, fb.Surface, traceTol).Cast<NCurve>().ToList();
                if (Prof.Enabled && c.Count == 0) Prof.Add($"tr.empty.{fa.Surface.Kind}x{fb.Surface.Kind}", 0);
                // traced from the other side too (small faces)
                if (c.Count == 0) c = NIntersect.Trace(fb, fa.Surface, traceTol).Cast<NCurve>().ToList();
                pairCurves[i] = c;
            });
        }
        for (int i = 0; i < pairs.Count; i++)
        {
            var (fa, fb, coincident) = pairs[i];
            if (coincident)
            {
                using var _i = Prof.Time("sec.imprint");
                Imprint(fb, fa, AddSec);
                Imprint(fa, fb, AddSec);
                continue;
            }
            using (Prof.Time("sec.curves"))
                foreach (var e in SectionEdges(fa, fb, pairCurves[i])) { AddSec(fa, e); AddSec(fb, e); }
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
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_FACE") is { } tf)
            foreach (var (f, l) in sections)
                if (f.Tag.Name.Contains(tf))
                {
                    T($"  face {f.Tag.Name} boundary: " + string.Join(" ", f.Coedges.Select(c => $"[{c.Start.P}->{c.End.P} {c.Edge.Curve.GetType().Name[1..]}]")));
                    foreach (var e in l) T($"    sec {e.V0.P}->{e.V1.P} {e.Curve.GetType().Name[1..]} mid {e.Mid}");
                }
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
        Lap("sew");
        Unify(result);
        if (Trace != null) T($"unified: {Dump(result.Faces)}");
        result.Link();
        Lap("unify");
        MergeEdges(result);
        RemoveSpurs(result);
        result.Link();
        Lap("merge edges");
        var err = _far.Count == 0 ? result.Validate() : result.Validate(ActiveFaces(result).ToList(), NearEdges(ActiveFaces(result)));
        Lap("validate");
        if (err != null && Trace != null)
            foreach (var f in result.Faces)
                foreach (var l in f.Loops)
                    if (Trace != null) T($"  {f.Tag.Name} sense={f.Sense}: " + string.Join(" ", l.Coedges.Select(c => $"[{c.Start.P}->{c.End.P} {c.Edge.Curve.GetType().Name[1..]}{(c.Forward ? "+" : "-")}]")));
        if (err != null) throw new NBooleanException("result invalid: " + err);
        return result;
    }

    /// <summary>
    /// Removes spurs: edges a loop runs out along and straight back (e.g. a section edge left dangling inside a
    /// face). They bound nothing, but leave the face's tessellation with a slit.
    /// </summary>
    static void RemoveSpurs(NBody body)
    {
        foreach (var f in body.Faces)
        {
            foreach (var l in f.Loops)
            {
                if (l.Coedges.Count < 2) continue;
                var stack = new List<NCoedge>(l.Coedges.Count);
                bool Cancels(NCoedge a, NCoedge b) => ReferenceEquals(a.Edge, b.Edge) && a.Forward != b.Forward;
                foreach (var c in l.Coedges)
                {
                    if (stack.Count > 0 && Cancels(stack[^1], c)) stack.RemoveAt(stack.Count - 1);
                    else stack.Add(c);
                }
                // the loop's start can sit inside a spur too
                while (stack.Count >= 2 && Cancels(stack[0], stack[^1])) { stack.RemoveAt(stack.Count - 1); stack.RemoveAt(0); }
                if (stack.Count != l.Coedges.Count) { l.Coedges.Clear(); l.Coedges.AddRange(stack); }
            }
            f.Loops.RemoveAll(l => l.Coedges.Count == 0);
        }
        body.Faces.RemoveAll(f => f.Loops.Count == 0);
    }

    // ------------------------------------------------------------------------------------------
    // vertices

    void MergeVertices(NBody A, NBody B)
    {
        _pool.Clear();
        _pool.AddRange(VerticesOf(NearEdges(ActiveFaces(A))));
        var edgesB = NearEdges(ActiveFaces(B));
        foreach (var v in VerticesOf(edgesB))
        {
            var m = _pool.FirstOrDefault(p => Vec3.Distance(p.P, v.P) < _tol * 10);
            if (m == null) { _pool.Add(v); continue; }
            foreach (var e in edgesB)
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
        List<(NFace f, Box3 box)> fboxes; using (Prof.Time("split.fboxes")) fboxes = ActiveFaces(Y).Select(f => (f, box: f.Bounds(_sampleTol * 500))).ToList();
        foreach (var e in NearEdges(ActiveFaces(X)))
        {
            var eb = e.Bounds(_sampleTol * 500);
            foreach (var (f, box) in fboxes)
            {
                if (!eb.Overlaps(box, _onTol)) continue;
                NIntersect.EdgeHits hits; using (Prof.Time(Prof.Enabled ? $"split.{e.Curve.GetType().Name}x{f.Surface.Kind}" : "")) hits = NIntersect.EdgeSurface(e, f.Surface, _tol);
                if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_EDGE") is { } te && e.Mid.ToString().Contains(te))
                    T($"    edge {e.V0.P}->{e.V1.P} x {f.Tag.Name} ({f.Surface.Kind}): on={hits.OnSurface} hits {string.Join(" ", hits.Params.Select(t => $"{e.Curve.Eval(t)}/cls{Dom(f).Classify(f.Surface.Project(e.Curve.Eval(t)), _onTol)}"))}");
                if (hits.OnSurface) { onSurface.Add((e, f)); continue; }
                foreach (var t in hits.Params)
                {
                    var p = e.Curve.Eval(t);
                    // traced boundaries are splines fitted within _sampleTol * 5 of the exact curve: a point on
                    // the true boundary may sit that far outside the spline
                    if (Dom(f).Classify(f.Surface.Project(p), Math.Max(_onTol, _sampleTol * 12)) < 0) continue;
                    if (Vec3.Distance(p, e.V0.P) < _tol * 10 || Vec3.Distance(p, e.V1.P) < _tol * 10) continue;
                    var v = Snap(p);
                    if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                    if (!l.Any(x => ReferenceEquals(x.Item2, v))) l.Add((t, v));
                    // the edge runs along the surface here (e.g. a seam crossing a fillet's contact line): the hit is a
                    // double root, accurate only to about the square root of the solver tolerance
                    if (Math.Abs(Vec3.Dot(e.Curve.Deriv(t).Normalized, f.Surface.Normal(p))) < 0.05) _grazing.Add(v);
                }
            }
        }
    }

    // split vertices from grazing edge/surface hits
    readonly HashSet<NVertex> _grazing = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// An edge lying on a face of the other body (a fillet's contact line on the face it rolls on) crosses that
    /// face's boundary edges inside the surface, transversally: those crossings are found curve against curve and
    /// split both edges. Edge/surface hits there are grazing (the boundary edge touches the fillet's surface)
    /// and land up to a millimetre off; a grazing split near a crossing is replaced by it.
    /// </summary>
    void CrossOnSurfaceEdges(List<(NEdge e, NFace f)> onSurface, Dictionary<NEdge, List<(double t, NVertex v)>> splits)
    {
        var near = Math.Max(_onTol * 200, 1e3 * _tol);
        foreach (var (e, f) in onSurface)
        {
            var eb = e.Bounds(_sampleTol * 500);
            if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_CROSS") is { } tcf && e.Mid.ToString().Contains(tcf))
                T($"   pair {e.Curve.GetType().Name}{e.V0.P}->{e.V1.P} on {f.Tag.Name}: " + string.Join(" ", f.Coedges.Select(c => $"[{c.Edge.Curve.GetType().Name}{c.Edge.V0.P}->{c.Edge.V1.P}]")));
            foreach (var c in f.Coedges)
            {
                var g = c.Edge;
                if (ReferenceEquals(g, e) || !eb.Overlaps(g.Bounds(_sampleTol * 500), _onTol)) continue;
                foreach (var (te, tg, p, reach) in Crossings(e, g))
                {
                    if (Near(p, e) || Near(p, g)) continue;
                    var v = Snap(p);
                    foreach (var (edge, t) in new[] { (e, te), (g, tg) })
                    {
                        if (!splits.TryGetValue(edge, out var l)) splits[edge] = l = new();
                        // grazing splits of this edge close to the crossing were the same point, computed badly
                        l.RemoveAll(x => !ReferenceEquals(x.v, v) && _grazing.Contains(x.v) && Vec3.Distance(x.v.P, p) < near + reach);
                        if (!l.Any(x => ReferenceEquals(x.v, v))) l.Add((t, v));
                    }
                    if (Trace != null) T($"  on-surface crossing {p} reach {reach:g3}: {e.Curve.GetType().Name}{e.Mid} x {g.Curve.GetType().Name}{g.Mid} on {f.Tag.Name}; splits now {string.Join(" ", splits[e].Select(x => x.v.P))} | {string.Join(" ", splits[g].Select(x => x.v.P))}");
                }
            }
        }
        bool Near(Vec3 p, NEdge x) => Vec3.Distance(p, x.V0.P) < _tol * 10 || Vec3.Distance(p, x.V1.P) < _tol * 10;
    }

    readonly Dictionary<(NEdge, NEdge), List<(double, double, Vec3, double)>> _crossings = new();

    /// <summary>Crossings of an unordered pair of edges, computed once (the same answer whichever edge is on the face).</summary>
    List<(double ta, double tb, Vec3 p, double reach)> Crossings(NEdge e, NEdge g)
    {
        bool swap = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e) > System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g);
        var key = swap ? (g, e) : (e, g);
        if (!_crossings.TryGetValue(key, out var r))
        {
            // the distance profile is read along the more finely sampled edge (a line has a single segment)
            var (x, y) = key;
            bool flip = x.Samples(_sampleTol * 500).Count < y.Samples(_sampleTol * 500).Count;
            r = flip ? CurveCrossings(y, x).Select(c => (c.tb, c.ta, c.p, c.reach)).ToList() : CurveCrossings(x, y).Select(c => (c.ta, c.tb, c.p, c.reach)).ToList();
            _crossings[key] = r;
        }
        return swap ? r.Select(x => (x.Item2, x.Item1, x.Item3, x.Item4)).ToList() : r;
    }

    /// <summary>Points where two edges cross (closer than the coincidence tolerance), from polyline seeds refined by alternating projection.</summary>
    List<(double ta, double tb, Vec3 p, double reach)> CurveCrossings(NEdge a, NEdge b)
    {
        var res = new List<(double, double, Vec3, double)>();
        var pa = a.Samples(_sampleTol * 500); var pb = b.Samples(_sampleTol * 500);
        double lo(NEdge x) => Math.Min(x.T0, x.T1); double hi(NEdge x) => Math.Max(x.T0, x.T1);
        double Par(NEdge x, Vec3 p, double hint)
        {
            var t = x.Curve.Param(p, hint);
            if (x.Curve.Periodic) { while (t < lo(x) - 1e-9) t += x.Curve.Period; while (t > hi(x) + 1e-9) t -= x.Curve.Period; }
            return Math.Clamp(t, lo(x), hi(x));
        }
        var gap = _sampleTol * 500 * 4 + _onTol;
        // only segments inside the other edge's box can be close (long edges such as full circles have many)
        List<int> Segs(List<Vec3> pts, Box3 other)
        {
            var r = new List<int>();
            for (int i = 1; i < pts.Count; i++)
            {
                var bx = Box3.Empty; bx.Add(pts[i - 1]); bx.Add(pts[i]);
                if (bx.Overlaps(other, gap)) r.Add(i);
            }
            return r;
        }
        var sa = Segs(pa, b.Bounds(_sampleTol * 500)); var sb = Segs(pb, a.Bounds(_sampleTol * 500));
        // closest segment of b for each segment of a; seeds are the local minima of that distance along a (one per
        // crossing, two for a shallow double crossing), and stretches where the edges coincide are skipped
        var best = new Dictionary<int, (int j, double s, double u, double d)>();
        foreach (var i in sa)
            foreach (var j in sb)
            {
                var (s0, u0, d0) = SegSeg(pa[i - 1], pa[i], pb[j - 1], pb[j]);
                if (d0 <= gap && (!best.TryGetValue(i, out var cur) || d0 < cur.d)) best[i] = (j, s0, u0, d0);
            }
        var coincident = _tol * 100;
        var seeds = new List<int>();
        var keys = best.Keys.OrderBy(k => k).ToList();
        for (int k = 0; k < keys.Count; k++)
        {
            int i = keys[k];
            var bi = best[i];
            if (bi.d < coincident)
            {
                // a stretch within the coincidence tolerance: an overlap if it runs to an end of either edge's seeds,
                // otherwise the edges touch (a shallow double crossing narrower than the tolerance): one crossing at
                // the closest point
                int k1 = k;
                while (k1 + 1 < keys.Count && keys[k1 + 1] == keys[k1] + 1 && best[keys[k1 + 1]].d < coincident) k1++;
                bool local = best.TryGetValue(i - 1, out var before) && before.d >= coincident
                    && best.TryGetValue(keys[k1] + 1, out var after) && after.d >= coincident;
                if (local)
                    for (int x = k; x <= k1; x++)
                    {
                        var dx = best[keys[x]].d;
                        if ((x == k || best[keys[x - 1]].d >= dx) && (x == k1 || best[keys[x + 1]].d >= dx)) seeds.Add(keys[x]);
                    }
                k = k1;
                continue;
            }
            if (best.TryGetValue(i - 1, out var pv) && pv.d < bi.d) continue;
            if (best.TryGetValue(i + 1, out var nx) && nx.d < bi.d) continue;
            seeds.Add(i);
        }
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_CROSS") is { } tc && (a.Mid.ToString().Contains(tc) || b.Mid.ToString().Contains(tc)))
            T($"    cross {a.Curve.GetType().Name}{a.V0.P}->{a.V1.P} x {b.Curve.GetType().Name}{b.V0.P}->{b.V1.P}: segs {sa.Count}x{sb.Count} coinc {coincident:g3} tol {_tol:g3} seeds {string.Join(",", seeds)}");
        foreach (var i in seeds)
            {
                var (j, s, u, d) = best[i];
                var q = pa[i - 1] + (pa[i] - pa[i - 1]) * s;
                var ta = Par(a, q, a.T0 + (a.T1 - a.T0) * (i - 1 + s) / (pa.Count - 1));
                var tb = Par(b, q, b.T0 + (b.T1 - b.T0) * (j - 1 + u) / (pb.Count - 1));
                // Gauss-Newton on |a(ta) - b(tb)|^2: converges fast even for curves crossing at a shallow angle
                Vec3 xa = a.Curve.Eval(ta), xb = b.Curve.Eval(tb);
                for (int k = 0; k < 30 && Vec3.Distance(xa, xb) > _tol * 0.1; k++)
                {
                    Vec3 da = a.Curve.Deriv(ta), db = b.Curve.Deriv(tb), r = xa - xb;
                    double aa = Vec3.Dot(da, da), bb = Vec3.Dot(db, db), ab = Vec3.Dot(da, db);
                    double det = aa * bb - ab * ab;
                    if (!(det > 1e-12 * aa * bb)) break; // parallel
                    double ga = Vec3.Dot(da, r), gb = -Vec3.Dot(db, r);
                    // solve [aa -ab; -ab bb] [dta dtb] = -[ga gb]
                    double dta = -(bb * ga + ab * gb) / det, dtb = -(ab * ga + aa * gb) / det;
                    ta = Math.Clamp(ta + dta, lo(a), hi(a)); tb = Math.Clamp(tb + dtb, lo(b), hi(b));
                    xa = a.Curve.Eval(ta); xb = b.Curve.Eval(tb);
                }
                if (Vec3.Distance(xa, xb) > _tol * 10) continue;
                // running along each other is an overlap, not a crossing (sewing handles it)
                if (Math.Abs(Vec3.Dot(a.Curve.Deriv(ta).Normalized, b.Curve.Deriv(tb).Normalized)) > 0.99995) continue;
                var p = (xa + xb) * 0.5;
                if (res.Any(r => Vec3.Distance(r.Item3, p) < _tol * 10)) continue;
                res.Add((ta, tb, p, 0.0));
            }
        // two crossings enclosing a sliver narrower than the boundary tolerance (curves touching at a shallow angle):
        // one touch point, so no face piece is a few microns wide
        res.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        for (int k = 0; k + 1 < res.Count; k++)
        {
            var (ta0, tb0, p0, r0) = res[k]; var (ta1, tb1, p1, r1) = res[k + 1];
            double width = 0;
            for (int m = 1; m < 8; m++)
            {
                var t = ta0 + (ta1 - ta0) * m / 8;
                var x = a.Curve.Eval(t);
                width = Math.Max(width, Vec3.Distance(x, b.Curve.Eval(Par(b, x, tb0 + (tb1 - tb0) * m / 8))));
            }
            if (width > _onTol * 10) continue;
            var tm = 0.5 * (ta0 + ta1);
            var xa = a.Curve.Eval(tm);
            var tbm = Par(b, xa, 0.5 * (tb0 + tb1));
            var pm = (xa + b.Curve.Eval(tbm)) * 0.5;
            res[k] = (tm, tbm, pm, Math.Max(Vec3.Distance(pm, p0) + r0, Vec3.Distance(pm, p1) + r1));
            res.RemoveAt(k + 1);
            k--;
        }
        return res;
    }

    static (double s, double u, double d) SegSeg(Vec3 p0, Vec3 p1, Vec3 q0, Vec3 q1)
    {
        Vec3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        double a = Vec3.Dot(d1, d1), e = Vec3.Dot(d2, d2), f = Vec3.Dot(d2, r);
        double s, u;
        if (a < 1e-300 && e < 1e-300) { s = u = 0; }
        else if (a < 1e-300) { s = 0; u = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = Vec3.Dot(d1, r);
            if (e < 1e-300) { u = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = Vec3.Dot(d1, d2), den = a * e - b * b;
                s = den > 1e-300 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                u = (b * s + f) / e;
                if (u < 0) { u = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (u > 1) { u = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (s, u, Vec3.Distance(p0 + d1 * s, q0 + d2 * u));
    }

    /// <summary>
    /// A split vertex also splits every other edge it lies on (e.g. a tool edge crossing a wall plane right
    /// where the wall's own edge only touches the tool tangentially, which reports no crossing).
    /// </summary>
    void ImprintSplitVertices(NBody A, NBody B, Dictionary<NEdge, List<(double t, NVertex v)>> splits)
    {
        var verts = splits.Values.SelectMany(l => l.Select(x => x.v)).Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>().ToList();
        // vertices of one body lying inside an edge of the other split it too: e.g. a blend's tangent point on an
        // edge it only touches (no crossing is reported for a touch)
        var ba = A.Bounds; var bb = B.Bounds;
        var olo = new Vec3(Math.Max(ba.Min.X, bb.Min.X), Math.Max(ba.Min.Y, bb.Min.Y), Math.Max(ba.Min.Z, bb.Min.Z));
        var ohi = new Vec3(Math.Min(ba.Max.X, bb.Max.X), Math.Min(ba.Max.Y, bb.Max.Y), Math.Min(ba.Max.Z, bb.Max.Z));
        var overlap = Box3.Empty; overlap.Add(olo); overlap.Add(ohi);
        List<NVertex> Near(NBody b) => ohi.X < olo.X || ohi.Y < olo.Y || ohi.Z < olo.Z ? new() : VerticesOf(NearEdges(ActiveFaces(b))).Where(v => overlap.Contains(v.P, _onTol)).ToList();
        var forA = verts.Concat(Near(B)).Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>().ToList();
        var forB = verts.Concat(Near(A)).Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>().ToList();
        foreach (var (e, cand) in NearEdges(ActiveFaces(A)).Select(e => (e, forA)).Concat(NearEdges(ActiveFaces(B)).Select(e => (e, forB))))
        {
            if (cand.Count == 0) continue;
            var box = e.Bounds(_sampleTol * 500);
            foreach (var v in cand)
            {
                if (ReferenceEquals(v, e.V0) || ReferenceEquals(v, e.V1) || !box.Contains(v.P, _onTol)) continue;
                if (Vec3.Distance(v.P, e.V0.P) < _tol * 10 || Vec3.Distance(v.P, e.V1.P) < _tol * 10) continue;
                double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
                var t = e.Curve.Param(v.P, e.Curve.Periodic ? 0.5 * (lo + hi) : null);
                if (t <= lo || t >= hi || Vec3.Distance(e.Curve.Eval(t), v.P) > _tol * 10) continue;
                if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                if (!l.Any(x => ReferenceEquals(x.v, v))) l.Add((t, v));
            }
        }
    }

    static void ApplySplits(List<NFace> faces, Dictionary<NEdge, List<(double t, NVertex v)>> splits)
    {
        var pieces = new Dictionary<NEdge, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in EdgesOf(faces))
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
        foreach (var f in faces)
            foreach (var l in f.Loops)
            {
                if (!l.Coedges.Any(c => pieces.ContainsKey(c.Edge))) continue;
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

    IEnumerable<NEdge> SectionEdges(NFace fa, NFace fb, List<NCurve> curves)
    {
        var sa = fa.Surface; var sb = fb.Surface;
        // breakpoints: vertices of either face lying on both surfaces
        var bps = fa.Coedges.Concat(fb.Coedges).SelectMany(c => new[] { c.Start, c.End })
            .Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>()
            .Where(v => Math.Abs(sa.F_(v.P)) < _onTol && Math.Abs(sb.F_(v.P)) < _onTol).ToList();
        NDomain domA, domB;
        using (Prof.Time("sec.domains")) { domA = Dom(fa); domB = Dom(fb); }
        var dbgPair = Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_PAIR") is { } tp && (fa.Tag.Name + "|" + fb.Tag.Name).Contains(tp);
        if (dbgPair) T($"  pair {fa.Tag.Name} x {fb.Tag.Name}: {curves.Count} curves ({string.Join(",", curves.Select(c => c.GetType().Name))}), breakpoints {string.Join(" ", bps.Select(v => v.P))}");
        if (dbgPair) foreach (var c in curves.OfType<NPolyline>()) T($"    polyline {c.Pts[0]} .. {c.Pts[^1]} ({c.Pts.Length} pts, closed={c.IsClosed}), onTol={_onTol}, breakpoint distances {string.Join(" ", bps.Select(v => Vec3.Distance(c.Eval(c.Param(v.P)), v.P).ToString("0.######")))}");
        foreach (var c in curves)
        {
            var on = new List<(double t, NVertex v)>();
            // traced curves are exact at their samples but can pass a singular point (two surfaces tangent
            // there, e.g. crossing fillets) at up to the trace tolerance; the edge is pinned to the vertex anyway
            // (breakpoints lie exactly on both surfaces, so a traced curve passing this close does go through them)
            var onTol = c is NPolyline ? Math.Max(_onTol * 4, _sampleTol * 500) : _onTol * 4;
            foreach (var v in bps)
            {
                var t = c.Param(v.P);
                if (Vec3.Distance(c.Eval(t), v.P) < onTol) on.Add((t, v));
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
                // a tight tolerance: only curves really on a face boundary are dropped, thin slivers next to one are kept
                using (Prof.Time("sec.midclass")) { ca = domA.Classify(sa.Project(mid), _tol * 10); cb = domB.Classify(sb.Project(mid), _tol * 10); }
                if (dbgPair) T($"    interval {v0.P}->{v1.P} mid {mid} inA={ca} inB={cb}");
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
            if (cyc.Count == 0 || !ReferenceEquals(cyc[^1].To, cyc[0].From)) throw new NBooleanException($"face split: open cycle on {f.Tag.Name} near {cyc.LastOrDefault()?.To.P}");
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
            if (owner.face == null) throw new NBooleanException($"face split: hole without owner on {f.Tag.Name}");
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
        var plone = Prof.Time("cls.lone");
        var lone = new bool[pieces.Count];
        // only faces of the other body near this one can touch it
        var near = other.Where(Active).ToList();
        for (int i = 0; i < pieces.Count; i++)
            if (Active(pieces[i]))
            foreach (var g in near)
                if (pieces[i].Surface.CoincidentWith(g.Surface, _tol * 10) && FBox(pieces[i]).Overlaps(FBox(g), _onTol)) { lone[i] = true; break; }
        plone.Dispose();
        var punion = Prof.Time("cls.union");
        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var users = new Dictionary<NEdge, List<int>>(ReferenceEqualityComparer.Instance);
        // far pieces are outside; an edge of an active piece used once among the active pieces is shared with a
        // far face (shells are closed and section edges block): that region is outside too
        int outsideRoot = -1;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (Active(pieces[i])) continue;
            if (outsideRoot < 0) outsideRoot = i; else parent[Find(i)] = Find(outsideRoot);
        }
        for (int i = 0; i < pieces.Count; i++)
            if (Active(pieces[i]))
            foreach (var c in pieces[i].Coedges)
            {
                if (!users.TryGetValue(c.Edge, out var l)) users[c.Edge] = l = new();
                l.Add(i);
            }
        foreach (var (e, us) in users)
        {
            if (us.Count == 1 && outsideRoot >= 0 && !_blockers.Contains(e))
            {
                if (!lone[us[0]]) parent[Find(us[0])] = Find(outsideRoot);
                continue;
            }
            if (us.Count != 2 || _blockers.Contains(e)) continue;
            if (lone[us[0]] || lone[us[1]]) continue;
            // the other body must not pass through the edge (an edge of a far face cannot reach it)
            bool touches = false;
            var eb = e.Bounds(_sampleTol * 500);
            if (eb.Overlaps(_region, 0))
            foreach (var g in near)
            {
                if (!FBox(g).Overlaps(eb, _onTol)) continue;
                if (Math.Abs(g.Surface.F_(e.Mid)) < _onTol || Math.Abs(g.Surface.F_(e.V0.P)) < _onTol && Math.Abs(g.Surface.F_(e.V1.P)) < _onTol) { touches = true; break; }
            }
            if (touches) continue;
            if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_COMP") != null)
            {
                var s0 = Classify(pieces[us[0]], other, otherBox); var s1 = Classify(pieces[us[1]], other, otherBox);
                if (s0 != s1) T($"  LEAK via edge {e.Curve.GetType().Name} {e.V0.P}->{e.V1.P} mid {e.Mid}: {pieces[us[0]].Tag.Name}={s0} / {pieces[us[1]].Tag.Name}={s1}");
            }
            parent[Find(us[0])] = Find(us[1]);
        }
        // one representative per region; domains and boxes are built first (single-threaded), then the
        // representatives are ray-classified in parallel (read-only from here on)
        punion.Dispose();
        var pprep = Prof.Time("cls.prep");
        // a region with a far face is outside the other body: no ray test
        var outside = new HashSet<int>();
        if (outsideRoot >= 0) outside.Add(Find(outsideRoot));
        var reps = new Dictionary<int, int>();
        for (int i = 0; i < pieces.Count; i++) if (Active(pieces[i]) && !outside.Contains(Find(i))) reps.TryAdd(Find(i), i);
        PrepareDomains(reps.Values.Select(i => pieces[i]));
        PrepareDomains(near, interior: false);
        foreach (var f in near) FBox(f);
        foreach (var i in reps.Values) { FBox(pieces[i]); Dom(pieces[i]).InteriorPoint(); }
        pprep.Dispose();
        using var prays = Prof.Time("cls.rays");
        var repList = reps.ToList();
        var states = new State[repList.Count];
        Exception? failure = null;
        Parallel.For(0, repList.Count, k =>
        {
            try { states[k] = Classify(pieces[repList[k].Value], other, otherBox); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure != null) throw failure;
        var compState = new Dictionary<int, State>();
        foreach (var c in outside) compState[c] = State.Out;
        for (int k = 0; k < repList.Count; k++) compState[repList[k].Key] = states[k];
        for (int i = 0; i < pieces.Count; i++) res[pieces[i]] = Active(pieces[i]) ? compState[Find(i)] : State.Out;
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_COMP") != null)
            foreach (var g in Enumerable.Range(0, pieces.Count).GroupBy(Find).Where(g => g.Count() > 1))
                T($"  component {compState[g.Key]} rep={pieces[g.Key].Tag.Name}@{Dom(pieces[g.Key]).InteriorPoint()}: " + string.Join(", ", g.Select(i => $"{pieces[i].Tag.Name}@{Dom(pieces[i]).InteriorPoint()}")));
        return res;
    }
    Box3 FBox(NFace f)
    {
        if (_far.Contains(f))
        {
            // cached on the face (computed when the far faces were found): safe from the parallel ray casts
            var fb = f.Bounds(_sampleTol * 500);
            var pd = _sampleTol * 500;
            fb.Add(fb.Min - new Vec3(pd, pd, pd)); fb.Add(fb.Max + new Vec3(pd, pd, pd));
            return fb;
        }
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
        var dbg = Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_RAY") is { } tr && f.Tag.Name.Contains(tr);
        if (!fb.Overlaps(otherBox, _onTol)) { if (dbg) T($"    {f.Tag.Name} box {fb.Min}-{fb.Max} outside other {otherBox.Min}-{otherBox.Max}"); return State.Out; }
        var p = Dom(f).InteriorPoint();
        if (dbg) T($"    {f.Tag.Name} interior point {p} box {fb.Min}-{fb.Max}");
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
            if (RayDom(g).Classify(g.Surface.Project(p), _onTol) < 0) continue;
            var ng = g.Surface.Normal(p) * (g.Sense ? 1 : -1);
            return Vec3.Dot(nf, ng) > 0 ? State.OnSame : State.OnOpposite;
        }
        var dbg = Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_RAY") is { } tr && f.Tag.Name.Contains(tr);
        foreach (var d in RayDirs)
        {
            int count = 0; bool ok = true;
            List<string>? hits = dbg ? new() : null;
            foreach (var g in other)
            {
                if (!RayHitsBox(p, d, FBox(g), _onTol * 4)) continue;
                foreach (var t in g.Surface.RayHits(p, d))
                {
                    if (t < -_onTol) continue;
                    var q = p + d * t;
                    var c = RayDom(g).Classify(q, _onTol * 4);
                    if (c < 0) continue;                 // the surface, but outside this face
                    if (c == 0 || t <= _onTol) { ok = false; break; } // on an edge, or starting on the face
                    // grazing hits are ambiguous
                    var n = g.Surface.Normal(q);
                    if (Math.Abs(Vec3.Dot(n, d)) < 1e-3) { ok = false; break; }
                    count++;
                    hits?.Add($"{g.Tag.Name}@{t:0.###}{q}");
                }
                if (!ok) break;
            }
            if (ok)
            {
                if (dbg) T($"    ray from {p} dir {d}: {count} hits {string.Join(" ", hits!)}");
                return count % 2 == 1 ? State.In : State.Out;
            }
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
        var single = NearEdges(ActiveFaces(body)).Where(e => e.Uses.Count == 1).ToList();
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
            ApplySplits(ActiveFaces(body).ToList(), splits);
            body.Link();
            single = NearEdges(ActiveFaces(body)).Where(e => e.Uses.Count == 1).ToList();
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
        foreach (var f in ActiveFaces(body))
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
        var edges = NearEdges(ActiveFaces(body));
        if (_far.Count > 0) edges = edges.OrderBy(e => e.Uses.Any(u => _far.Contains(u.Face)) ? 0 : 1).ToList();
        foreach (var e in edges)
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
        foreach (var e in edges)
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
        foreach (var e in NearEdges(ActiveFaces(body)))
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
            // the merged face must still be a non-wrapping patch on periodic surfaces, with an outer loop: merging
            // all pieces of e.g. a torus around a hole would leave only the hole's loop, whose inside is the
            // opposite region of the face
            try
            {
                var dom = NDomain.Of(merged, _sampleTol * 500);
                if (dom.Loops.All(l => SignedArea(l) <= 0)) continue;
            }
            catch (InvalidOperationException) { continue; }
            foreach (var m in members) remove.Add(m);
            add.Add(merged);
        }
        body.Faces.RemoveAll(remove.Contains);
        body.Faces.AddRange(add);
    }

    static double SignedArea(Vec2[] l)
    {
        double a = 0;
        for (int i = 0, j = l.Length - 1; i < l.Length; j = i++) a += l[j].X * l[i].Y - l[i].X * l[j].Y;
        return a / 2;
    }

    /// <summary>Joins consecutive edges at vertices where nothing else meets and the curve continues.</summary>
    void MergeEdges(NBody body)
    {
        body.Link();
        var edgesAt = new Dictionary<NVertex, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in NearEdges(ActiveFaces(body)))
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!edgesAt.TryGetValue(v, out var l)) edgesAt[v] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
        // a merge only changes the edges at the two outer vertices: those are revisited, nothing is relinked
        var work = new Queue<NVertex>(edgesAt.Keys);
        while (work.Count > 0)
        {
            var v = work.Dequeue();
            if (!edgesAt.TryGetValue(v, out var list) || list.Count != 2) continue;
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
            foreach (NFace f in f1)
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
                    var nc = new NCoedge(joined, fwd) { Face = f };
                    rotated.Insert(0, nc);
                    joined.Uses.Add(nc);
                    lp.Coedges.Clear(); lp.Coedges.AddRange(rotated);
                }
            edgesAt.Remove(v);
            foreach (var end in new[] { first.V0, second.V1 })
                if (edgesAt.TryGetValue(end, out var el))
                {
                    el.RemoveAll(x => ReferenceEquals(x, first) || ReferenceEquals(x, second));
                    el.Add(joined);
                    work.Enqueue(end);
                }
        }
    }

    static bool SameCurve(NCurve a, NCurve b) => ReferenceEquals(a, b);
}

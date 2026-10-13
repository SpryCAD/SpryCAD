namespace SpryCad.Kernel.Native;

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

    /// <summary>How narrow an analytic face is: a round one's diameter (its smaller one for a torus), else its box's
    /// middle extent (a flat face's narrower side).</summary>
    static double Width(NFace f)
    {
        switch (f.Surface.Kind)
        {
            case SurfaceKind.Cylinder: case SurfaceKind.Cone: case SurfaceKind.Sphere: return Math.Min(2 * f.Surface.R, f.Bounds().Diagonal);
            case SurfaceKind.Torus: return 2 * f.Surface.R2;
        }
        var b = f.Bounds(); var d = b.Max - b.Min;
        var e = new[] { d.X, d.Y, d.Z }.OrderBy(x => x).ToArray();
        return e[1];
    }
    bool RollsOn(NFace blendFace, NSurface s) => blendFace.Surface.Spine is { } sp
        && (sp.SupportA is { } a && Same(a, s) || sp.SupportB is { } b && Same(b, s) || sp.Rolls.Any(r => Same(r, s)) && !Crosses(blendFace, s));

    /// <summary>
    /// Whether a face's boundary has points on both sides of a surface (by more than the tolerance). A composite
    /// blend's piece touches the other pieces' surfaces only where it switches onto them and lies on their open side
    /// elsewhere, unless it reaches past (a tool run on past its end, to be cut back): then it crosses them.
    /// </summary>
    bool Crosses(NFace f, NSurface s)
    {
        bool pos = false, neg = false;
        foreach (var c in f.Coedges)
            foreach (var p in c.Edge.Samples(_sampleTol * 50))
            {
                var d = s.F_(p);
                if (d > _tol * 100) pos = true; else if (d < -_tol * 100) neg = true;
                if (pos && neg) return true;
            }
        return false;
    }

    // the same surface, or a piece of the same fillet (a blend rolling over a fillet rolls on that fillet merged into
    // one pipe, the body has its pieces)
    bool Same(NSurface support, NSurface s) => ReferenceEquals(support, s) || support.CoincidentWith(s, _tol * 10) || Continues(support, s);

    /// <summary>
    /// Two pieces of one fillet (pipes of the same radius whose spines join at a node with the same tangent): they
    /// meet tangentially along the arc at the joint and nearly coincide around it, so there is nothing to trace.
    /// </summary>
    bool Continues(NSurface a, NSurface b)
    {
        if (a.Spine is not { } sa || b.Spine is not { } sb || Math.Abs(a.R - b.R) > _tol * 10) return false;
        // the joint is a node of both spines (spines run a little past their joints)
        for (int i = 0; i < sa.P.Length; i++)
            for (int j = 0; j < sb.P.Length; j++)
                if (Vec3.Distance(sa.P[i], sb.P[j]) < _tol * 10 && Math.Abs(Vec3.Dot(sa.D[i], sb.D[j])) > 1 - 1e-8) return true;
        return false;
    }

    /// <summary>How far a fitted curve may be from the exact one (0 for exact curves).</summary>
    static double FitTol(NCurve c) => c is NSpline ns ? ns.FitTol : 0;

    /// <summary>
    /// Two pipes that cannot meet: every point of a pipe is its radius from its spine, so spines further apart than
    /// the two radii (with a margin for the spine polylines' chord error) keep the surfaces apart. Proving that by
    /// tracing samples the whole overlap of the faces' boxes.
    /// </summary>
    /// <summary>
    /// Whether the surfaces are offsets of one another (an offset surface and its base, or two offsets of one base) at
    /// distances more than a tolerance apart: parallel, they never meet (a shell's inner wall against the loft it is
    /// offset from: traced, a long fruitless search).
    /// </summary>
    static bool OffsetsApart(NSurface a, NSurface b, double tol)
    {
        static (NSurface baseS, double d) Of(NSurface s) => s.Kind == SurfaceKind.Offset && s.Base is { } bs ? (bs, s.R) : (s, 0);
        var (ba, da) = Of(a); var (bb, db) = Of(b);
        if (Math.Abs(da - db) <= tol || (da == 0 && db == 0)) return false;
        if (ba.Kind != SurfaceKind.BSpline || bb.Kind != SurfaceKind.BSpline) return false;
        return ReferenceEquals(ba, bb) || ReferenceEquals(ba.Nurbs, bb.Nurbs) || ba.CoincidentWith(bb, tol);
    }

    /// <summary>
    /// Whether face f's surface patch (its parameter rectangle) stays clear of surface s: a grid of points on it, spaced in
    /// each direction by its length there, all on one side of s and each farther from it than the spacing (s's distance
    /// grows no faster than one moves, so nothing between the points reaches it). The spacing set from the nearest
    /// point of a coarse grid first; false when that would take too many points, or a point is that near.
    /// </summary>
    static bool Apart(NFace f, NSurface s, double tol, NDomain dom)
    {
        if (dom.UMax < dom.UMin || dom.VMax < dom.VMin) return false;
        Vec3 P(double a, double b) => dom.Eval(new Vec2(dom.UMin + (dom.UMax - dom.UMin) * a, dom.VMin + (dom.VMax - dom.VMin) * b));
        // the patch's lengths across its two directions (along lines through its middle)
        double Len(bool u) { double l = 0; var p = P(u ? 0 : 0.5, u ? 0.5 : 0); for (int i = 1; i <= 16; i++) { var q = u ? P(i / 16.0, 0.5) : P(0.5, i / 16.0); l += Vec3.Distance(p, q); p = q; } return l; }
        double lu = Len(true), lv = Len(false);
        int sign = 0;
        double Check(int nu, int nv, out double h)
        {
            double minF = double.MaxValue; h = 0;
            var prev = new Vec3[nv + 1];
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++)
                {
                    var p = P((double)i / nu, (double)j / nv);
                    var fv = s.F_(p);
                    if (!double.IsFinite(fv) || Math.Abs(fv) <= tol) return -1;
                    var sg = Math.Sign(fv);
                    if (sign == 0) sign = sg; else if (sg != sign) return -1;
                    minF = Math.Min(minF, Math.Abs(fv));
                    if (i > 0) h = Math.Max(h, Vec3.Distance(p, prev[j]));
                    if (j > 0) h = Math.Max(h, Vec3.Distance(p, P((double)i / nu, (j - 1.0) / nv)));
                    prev[j] = p;
                }
            return minF;
        }
        var m0 = Check(8, 8, out var h0);
        if (m0 < 0) return false;
        if (m0 > h0) return true;
        // spaced at most about the nearest distance found
        var step = 0.7 * m0;
        int nu2 = Math.Max(2, (int)Math.Ceiling(lu / step)), nv2 = Math.Max(2, (int)Math.Ceiling(lv / step));
        if ((long)(nu2 + 1) * (nv2 + 1) > 3000) return false;
        var m1 = Check(nu2, nv2, out var h1);
        return m1 > 0 && m1 > h1;
    }

    static bool PipesApart(NSurface a, NSurface b)
    {
        if (a.Spine is not { } sa || b.Spine is not { } sb) return false;
        double Chord(PipeSpine sp)
        {
            // a Hermite segment strays from its chord by at most about h^2 / (8 R) (R: smallest curvature radius)
            double worst = 0;
            for (int i = 1; i < sp.P.Length; i++) worst = Math.Max(worst, Vec3.Distance(sp.P[i - 1], sp.P[i]));
            var r = double.IsFinite(sp.MinCurvatureRadius) ? sp.MinCurvatureRadius : 1e300;
            return Math.Min(worst * worst / (8 * r), worst);
        }
        var margin = 2 * (Chord(sa) + Chord(sb)) + 1e-6 * (a.R + b.R + 1);
        var need = a.R + b.R + margin;
        if (!sa.Box.Overlaps(sb.Box, need)) return true;
        for (int i = 1; i < sa.P.Length; i++)
        {
            var bi = Box3.Empty; bi.Add(sa.P[i - 1]); bi.Add(sa.P[i]);
            if (!bi.Overlaps(sb.Box, need)) continue;
            for (int j = 1; j < sb.P.Length; j++)
                if (SegSeg(sa.P[i - 1], sa.P[i], sb.P[j - 1], sb.P[j]).d <= need) return false;
        }
        return true;
    }

    static int Id(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o) % 100000;
    static string Dump(IEnumerable<NFace> fs) => string.Join(", ", fs.GroupBy(f => f.Tag.Name).Select(g => $"{g.Key}x{g.Count()}[{string.Join("/", g.Select(f => f.Loops.Count))}]"));
    readonly double _tol;      // geometric coincidence
    readonly double _onTol;    // "on the boundary" for UV/domain decisions
    readonly double _sampleTol;
    readonly Dictionary<NFace, NDomain> _domains = new(ReferenceEqualityComparer.Instance);
    readonly VertexPool _pool;

    /// <summary>
    /// Vertices in the order they were added, hashed on a grid: <see cref="Find"/> gives the first one (lowest index)
    /// within the radius (_tol·10), as a scan of the list would.
    /// </summary>
    sealed class VertexPool
    {
        readonly List<NVertex> _items = new();
        readonly Dictionary<(long, long, long), List<int>> _grid = new();
        readonly double _cell, _r;
        // cells four times the radius: a point within it of p lies in p's cell, or in the next one on a side p is
        // within a quarter cell of (0.3 here: a margin for the rounding of the division)
        public VertexPool(double r) { _r = r; _cell = r * 4; }
        public IReadOnlyList<NVertex> Items => _items;
        public void Clear() { _items.Clear(); _grid.Clear(); }
        (long, long, long) K(Vec3 p) => ((long)Math.Floor(p.X / _cell), (long)Math.Floor(p.Y / _cell), (long)Math.Floor(p.Z / _cell));
        void Hash(int i)
        {
            var k = K(_items[i].P);
            if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new();
            l.Add(i);
        }
        public void Add(NVertex v) { _items.Add(v); Hash(_items.Count - 1); }
        public void AddRange(IEnumerable<NVertex> vs) { foreach (var v in vs) Add(v); }
        /// <summary>The first vertex nearer p than r (more than the pool's radius): the cells within reach scanned.</summary>
        public NVertex? Find(Vec3 p, double r)
        {
            if (r <= _r) return Find(p);
            var (x, y, z) = K(p);
            long n = (long)Math.Ceiling(r / _cell);
            int best = int.MaxValue;
            for (long dx = -n; dx <= n; dx++)
                for (long dy = -n; dy <= n; dy++)
                    for (long dz = -n; dz <= n; dz++)
                        if (_grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                            foreach (var i in l)
                            {
                                if (i >= best) break;
                                if (Vec3.Distance(_items[i].P, p) < r) best = i;
                            }
            return best == int.MaxValue ? null : _items[best];
        }

        /// <summary>The first vertex nearer p than the pool's radius.</summary>
        public NVertex? Find(Vec3 p)
        {
            // a few vertices: scanned (the same answer)
            if (_items.Count <= 32)
            {
                foreach (var v in _items) if (Vec3.Distance(v.P, p) < _r) return v;
                return null;
            }
            var (x, y, z) = K(p);
            static (int, int) Reach(double c, long k)
            {
                var f = c - k; // the position in the cell, 0..1
                return (f < 0.3 ? -1 : 0, f > 0.7 ? 1 : 0);
            }
            var (x0, x1) = Reach(p.X / _cell, x); var (y0, y1) = Reach(p.Y / _cell, y); var (z0, z1) = Reach(p.Z / _cell, z);
            int best = int.MaxValue;
            for (long dx = x0; dx <= x1; dx++)
                for (long dy = y0; dy <= y1; dy++)
                    for (long dz = z0; dz <= z1; dz++)
                        if (_grid.TryGetValue((x + dx, y + dy, z + dz), out var l))
                            foreach (var i in l)
                            {
                                if (i >= best) break; // indices ascend within a cell
                                if (Vec3.Distance(_items[i].P, p) < _r) best = i;
                            }
            return best == int.MaxValue ? null : _items[best];
        }
    }
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

    Func<string, string, bool>? _mayMeet;

    NBoolean(double scale)
    {
        _tol = 1e-7 * Math.Max(10, scale);
        _onTol = 5e-6 * Math.Max(10, scale);
        _sampleTol = 2e-6 * Math.Max(10, scale);
        _pool = new VertexPool(_tol * 10);
    }

    enum State { Out, In, OnSame, OnOpposite }

    public static NBody Run(NBody a, NBody b, BoolOp op) => Run(a, b, op, null);

    /// <summary>
    /// The boolean, told which faces can meet at all (by their names; a face of a and a face of b): a shell's inner
    /// body runs beside the outer one at the wall's thickness and meets it only at the open faces, and tracing a
    /// face against its own offset (two surfaces that never cross, a wall apart) takes minutes.
    /// </summary>
    public static NBody Run(NBody a, NBody b, BoolOp op, Func<string, string, bool>? mayMeet)
    {
        var ba = a.Bounds; var bb = b.Bounds;
        // tolerances follow the region where the operands meet: a long through-all tool must not
        // loosen them for the (small) part it actually cuts
        var lo = new Vec3(Math.Max(ba.Min.X, bb.Min.X), Math.Max(ba.Min.Y, bb.Min.Y), Math.Max(ba.Min.Z, bb.Min.Z));
        var hi = new Vec3(Math.Min(ba.Max.X, bb.Max.X), Math.Min(ba.Max.Y, bb.Max.Y), Math.Min(ba.Max.Z, bb.Max.Z));
        var scale = (hi - lo).Length;
        if (!(scale > 0) || hi.X < lo.X || hi.Y < lo.Y || hi.Z < lo.Z) scale = Math.Min(ba.Diagonal, bb.Diagonal);
        var nb = new NBoolean(scale) { _mayMeet = mayMeet };
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
        return NDomain.Cached(f, tol);
    }

    NBody Execute(NBody A, NBody B, BoolOp op)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what) { if (Prof.Enabled) Prof.Add("bool." + what, sw.Elapsed.TotalMilliseconds); T($"[{sw.ElapsedMilliseconds} ms] {what}"); sw.Restart(); Cancellation.Check(); }
        FindFar(A, B);
        MergeVertices(A, B);
        A.Link(); B.Link();
        Lap("merge vertices");

        // 1-2. split edges where they meet the other body's faces
        var onSurface = new List<(NEdge e, NFace f)>();
        var splits = new Dictionary<NEdge, List<(double t, NVertex v)>>(ReferenceEqualityComparer.Instance);
        _fittedEdges.Clear();
        foreach (var f in ActiveFaces(A).Concat(ActiveFaces(B)))
            if (f.Surface.Kind is SurfaceKind.BSpline or SurfaceKind.Offset)
                foreach (var c in f.Coedges) _fittedEdges.Add(c.Edge);
            // (a fillet's contact line is fitted too: marched along the rolling ball)
            else foreach (var c in f.Coedges) if (c.Edge.Curve is NContact) _fittedEdges.Add(c.Edge);
        FindEdgeSplits(A, B, splits, onSurface);
        FindEdgeSplits(B, A, splits, onSurface);
        using (Prof.Time("split.crossings")) CrossOnSurfaceEdges(onSurface, splits);
        using (Prof.Time("split.imprint")) ImprintSplitVertices(A, B, splits);
        Lap("edge splits");
        ApplySplits(ActiveFaces(A).ToList(), splits); ApplySplits(ActiveFaces(B).ToList(), splits);
        _domains.Clear();
        A.Link(); B.Link();

        // 3-4. section edges per face pair, imprints for coincident faces
        var sections = new Dictionary<NFace, List<NEdge>>(ReferenceEqualityComparer.Instance);
        var secSets = new Dictionary<NFace, HashSet<NEdge>>(ReferenceEqualityComparer.Instance);
        void AddSec(NFace f, NEdge e)
        {
            if (!sections.TryGetValue(f, out var l)) { sections[f] = l = new(); secSets[f] = new(ReferenceEqualityComparer.Instance); }
            if (secSets[f].Add(e)) l.Add(e);
        }
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
                if (faceBoxA[fa].Overlaps(faceBoxB[fb], _onTol) && (_mayMeet == null || _mayMeet(fa.Tag.Name, fb.Tag.Name)))
                    pairs.Add((fa, fb, fa.Surface.CoincidentWith(fb.Surface, _tol * 10)));
        var pairCurves = new List<NCurve>[pairs.Count];
        var toTrace = new List<int>();
        using (Prof.Time("sec.analytic"))
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].coincident) continue;
                // a blend and the surface its ball rolls on touch along the contact curve, which is an edge of the
                // blend face (imprinted as an edge lying on the other face): tracing that tangency is slow and noisy
                if (RollsOn(pairs[i].fa, pairs[i].fb.Surface) || RollsOn(pairs[i].fb, pairs[i].fa.Surface)
                    || Continues(pairs[i].fa.Surface, pairs[i].fb.Surface) || PipesApart(pairs[i].fa.Surface, pairs[i].fb.Surface)
                    || OffsetsApart(pairs[i].fa.Surface, pairs[i].fb.Surface, _onTol)) { pairCurves[i] = new(); continue; }
                var an = NIntersect.Analytic(pairs[i].fa.Surface, pairs[i].fb.Surface, _tol * 10, _sampleTol * 50 * 0.25);
                if (an != null) pairCurves[i] = an; else toTrace.Add(i);
            }
        using (Prof.Time("sec.trace"))
        {
            var traceTol = _sampleTol * 50;
            // the traced faces' domains, built first and single-threaded (sampling their edges): parallel tracing
            // then only reads them; a face is in many pairs
            var traceDoms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
            foreach (var i in toTrace)
                foreach (var f in new[] { pairs[i].fa, pairs[i].fb })
                    if (!traceDoms.ContainsKey(f)) traceDoms[f] = NDomain.Of(f, Math.Max(traceTol, 1e-3));
            // the planes B-spline faces here are mirrored in (a body and its mirror image, met along the plane)
            var mirrors = new List<Plane>();
            foreach (var i in toTrace)
                if (MirrorPlane(pairs[i].fa, pairs[i].fb) is { } mpl && !mirrors.Any(q => Same(q, mpl))) mirrors.Add(mpl);
            static bool Same(Plane a, Plane b) => Vec3.Dot(a.Normal, b.Normal) > 1 - 1e-12 && Math.Abs(a.D - b.D) < 1e-9 * Math.Max(1, Math.Abs(a.D));
            // a face traced against a surface once: the pieces of a seam-split face (a thread's half turns of one
            // cylinder) share their surface, and the trace runs over the whole other face, whichever piece it is
            var memo = new System.Collections.Concurrent.ConcurrentDictionary<(NFace, NSurface), Lazy<List<NCurve>>>();
            List<NCurve> Traced(NFace f, NSurface s)
                => new(memo.GetOrAdd((f, s), k => new Lazy<List<NCurve>>(() => NIntersect.Trace(k.Item1, k.Item2, traceTol, traceDoms[k.Item1]).Cast<NCurve>().ToList())).Value);
            Parallel.ForEach(toTrace, i =>
            {
                var (fa, fb, _) = pairs[i];
                // a screw surface's signed distance jumps between its turns: trace on the screw face instead, with the
                // other surface's (smooth) distance
                if (fb.Surface.Kind == SurfaceKind.Screw && fa.Surface.Kind != SurfaceKind.Screw) (fa, fb) = (fb, fa);
                // a B-spline's distance is a closest-point search: trace on the B-spline face, with the analytic
                // surface's distance (exact and cheap)
                bool onSpline = false;
                // (not where the analytic face is narrower than a few of the B-spline's grid cells, a thin hole through it:
                // there the B-spline's grid sees only scraps of the crossing, and the analytic face's own grid finds it)
                // (an offset of a B-spline too: its distance is the B-spline's search, which past a narrow face's edges
                // runs onto its extension and draws curves that are not there)
                if (fb.Surface.Kind is SurfaceKind.BSpline or SurfaceKind.Offset && fa.Surface.Kind is not (SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw)
                    && Width(fa) > 4 * fb.Bounds().Diagonal / 96) { (fa, fb) = (fb, fa); onSpline = true; }
                else if (fa.Surface.Kind is SurfaceKind.BSpline or SurfaceKind.Offset && fb.Surface.Kind is not (SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw or SurfaceKind.Pipe)
                    && Width(fb) <= 4 * fa.Bounds().Diagonal / 96) (fa, fb) = (fb, fa);
                var swt = System.Diagnostics.Stopwatch.StartNew();
                // a B-spline face against its own mirror image (a mirror or a pattern's mirrored copy, meeting its seed),
                // the face on one side of the mirror plane: they meet only in that plane, so the face is traced against
                // the plane instead (exact, and the plane's distance is cheap where the B-spline's is a closest-point
                // search). Two faces on opposite sides of such a plane (the seed's and another of the copy's, touching
                // along it) likewise meet only in it: the first's section by the plane, where it lies on the second.
                var mp = MirrorPlane(fa, fb);
                bool apart = false;
                if (mp == null && fa.Surface.Kind == SurfaceKind.BSpline && fb.Surface.Kind == SurfaceKind.BSpline)
                    foreach (var q in mirrors) if (Apart(fa, fb, q)) { mp = q; apart = true; break; }
                var against = mp is { } mpp ? NSurface.FromPlane(mpp) : fb.Surface;
                List<NCurve> c; using (Prof.Time(Prof.Enabled ? $"tr.{fa.Surface.Kind}x{against.Kind}" : "")) c = mp == null ? Traced(fa, against) : NIntersect.Trace(fa, against, traceTol, traceDoms[fa]).Cast<NCurve>().ToList();
                if (apart) c = OnSurface(c, fb.Surface, traceTol);
                if (Prof.Enabled && c.Count == 0) Prof.Add($"tr.empty.{fa.Surface.Kind}x{fb.Surface.Kind}", 0);
                // traced from the other side too (small faces)
                // (from the analytic face only when it is much smaller: a curve on it may fall between the B-spline's grid
                // points. A narrow one, a thin hole, was traced on its own face to begin with. Elsewhere the B-spline's
                // distance is slow, and past the patch's edge its sign flips with nothing crossing)
                // (meeting only in a mirror plane: the other face's section by the plane, where it lies on the first)
                if (c.Count == 0 && mp is { } mq)
                {
                    c = NIntersect.Trace(fb, NSurface.FromPlane(mq), traceTol, traceDoms[fb]).Cast<NCurve>().ToList();
                    if (apart) c = OnSurface(c, fa.Surface, traceTol);
                }
                else if (c.Count == 0 && fa.Surface.Kind != SurfaceKind.Screw && (!onSpline || fb.Bounds().Diagonal < 0.25 * fa.Bounds().Diagonal))
                {
                    // (a slow distance, an offset's or a B-spline's, against a face proved clear of it first: tracing it
                    // is a long fruitless search, a shell's inner wall against a fillet outside it)
                    bool clear; using (Prof.Time("tr2.apart")) clear = fa.Surface.Kind is SurfaceKind.Offset or SurfaceKind.BSpline && Apart(fb, fa.Surface, _onTol, traceDoms[fb]);
                    if (!clear) using (Prof.Time(Prof.Enabled ? $"tr2.{fb.Surface.Kind}x{fa.Surface.Kind}" : "")) c = Traced(fb, fa.Surface);
                }
                pairCurves[i] = c;
                if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_SLOW") != null)
                    T($"  trace {fa.Tag.Name}({fa.Surface.Kind}) x {fb.Tag.Name}({fb.Surface.Kind}): {c.Count} curves, {string.Join(" ", c.Select(x => x is NPolyline pl ? pl.Pts.Length + "pts" : x.GetType().Name))} in {swt.ElapsedMilliseconds} ms");
            });
        }
        // each pair's section edges and imprints worked out in parallel (pure geometry: the faces' domains are built
        // first and only read), then registered in pair order as before. A closed section touching no vertex makes
        // vertices of its own (the shared pool): such a pair is done in that ordered pass.
        using (Prof.Time("sec.domains")) PrepareDomains(pairs.SelectMany(p => new[] { p.fa, p.fb }), interior: false);
        var secEdges = new List<NEdge>?[pairs.Count];
        var imprints = new List<(NFace dst, NEdge e, bool add)>[pairs.Count];
        using (Prof.Time("sec.pairs"))
            Parallel.For(0, pairs.Count, i =>
            {
                var (fa, fb, coincident) = pairs[i];
                var l = new List<(NFace, NEdge, bool)>();
                if (coincident)
                {
                    Imprint(fb, fa, l);
                    Imprint(fa, fb, l);
                }
                else
                {
                    secEdges[i] = SectionEdges(fa, fb, pairCurves[i], allowSnap: false);
                    // boundary edges of one face lying on the other face's surface split that face
                    ImprintOnSurfaceEdges(fa, fb, l);
                    ImprintOnSurfaceEdges(fb, fa, l);
                }
                imprints[i] = l;
            });
        for (int i = 0; i < pairs.Count; i++)
        {
            var (fa, fb, coincident) = pairs[i];
            if (!coincident)
                foreach (var e in secEdges[i] ?? SectionEdges(fa, fb, pairCurves[i], allowSnap: true)!) { AddSec(fa, e); AddSec(fb, e); }
            foreach (var (dst, e, add) in imprints[i])
            {
                if (!coincident) _blockers.Add(e);
                if (add) AddSec(dst, e);
            }
        }

        Lap("sections");
        // 5. split faces
        if (Trace != null) T($"sections: {string.Join(", ", sections.Select(kv => kv.Key.Tag.Name + ":" + kv.Value.Count))}");
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_FACE") is { } tf)
            foreach (var (f, l) in sections)
                if (f.Tag.Name.Contains(tf))
                {
                    T($"  face {f.Tag.Name} boundary: " + string.Join(" ", f.Coedges.Select(c => $"[{c.Start.P}#{Id(c.Start)}->{c.End.P}#{Id(c.End)} {c.Edge.Curve.GetType().Name[1..]}]")));
                    foreach (var e in l) T($"    sec {e.V0.P}#{Id(e.V0)}->{e.V1.P}#{Id(e.V1)} {e.Curve.GetType().Name[1..]} mid {e.Mid}");
                }
        var piecesA = SplitFaces(A, sections);
        var piecesB = SplitFaces(B, sections);
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_FACE") is { } tf2)
            foreach (var pc in piecesA.Concat(piecesB).Where(f => f.Tag.Name.Contains(tf2)))
                T($"  piece {pc.Tag.Name}: " + string.Join(" | ", pc.Loops.Select(l => string.Join(" ", l.Coedges.Select(c => $"{c.Start.P}")))));
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
        // each vertex of B is replaced by the first pool vertex near it, in turn: a replacement made later applies
        // to the ends already moved onto that vertex, so an end follows the replacements in the order they were made
        var moved = new Dictionary<NVertex, (NVertex to, int step)>(ReferenceEqualityComparer.Instance);
        int step = 0;
        foreach (var v in VerticesOf(edgesB))
        {
            var m = _pool.Find(v.P);
            if (m == null) { _pool.Add(v); continue; }
            moved[v] = (m, step++);
        }
        if (moved.Count == 0) return;
        NVertex Final(NVertex x)
        {
            int after = -1;
            while (moved.TryGetValue(x, out var mv) && mv.step > after) { x = mv.to; after = mv.step; }
            return x;
        }
        foreach (var e in edgesB) { e.V0 = Final(e.V0); e.V1 = Final(e.V1); }
    }

    NVertex Snap(Vec3 p, double r = 0)
    {
        if ((r > 0 ? _pool.Find(p, r) : _pool.Find(p)) is { } found) return found;
        var nv = new NVertex(p);
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_SNAP") is { } ts && p.ToString().Contains(ts))
            T($"  new vertex {p}#{Id(nv)}: nearest pool vertex at {_pool.Items.Min(v => Vec3.Distance(v.P, p)):g3}; " + string.Join(" / ", Environment.StackTrace.Split((char)10).Skip(2).Take(3).Select(x => x.Trim())));
        _pool.Add(nv);
        return nv;
    }

    /// <summary>
    /// A list of vertices sorted by x: <see cref="InRange"/> gives the list positions of those with lo ≤ x ≤ hi, in
    /// list order (a scan of the list testing a box would meet them in that order).
    /// </summary>
    sealed class ByX
    {
        readonly double[] _x;
        readonly int[] _at;
        public ByX(IReadOnlyList<NVertex> vs)
        {
            // NaN never lies in a range: left out
            var at = new List<int>(vs.Count);
            for (int i = 0; i < vs.Count; i++) if (!double.IsNaN(vs[i].P.X)) at.Add(i);
            _at = at.ToArray();
            _x = _at.Select(i => vs[i].P.X).ToArray();
            Array.Sort(_x, _at);
        }
        public void InRange(double lo, double hi, List<int> res)
        {
            res.Clear();
            int a = 0, b = _x.Length;
            while (a < b) { int m = (a + b) >> 1; if (_x[m] < lo) a = m + 1; else b = m; }
            for (int k = a; k < _x.Length && _x[k] <= hi; k++) res.Add(_at[k]);
            res.Sort();
        }
    }

    // ------------------------------------------------------------------------------------------
    // edge splitting

    void FindEdgeSplits(NBody X, NBody Y, Dictionary<NEdge, List<(double, NVertex)>> splits, List<(NEdge, NFace)> onSurface)
    {
        List<(NFace f, Box3 box)> fboxes; using (Prof.Time("split.fboxes")) fboxes = ActiveFaces(Y).Select(f => (f, box: f.Bounds(_sampleTol * 500))).ToList();
        List<NVertex>? yVerts = null;
        var grazeNear = Math.Max(_onTol * 200, 1e3 * _tol);
        // the edge/surface roots of every candidate pair, found in parallel (pure geometry); the splits are then
        // made in order, as before
        var cand = new List<(NEdge e, NFace f)>();
        var pc = Prof.Time("split.cand");
        foreach (var e in NearEdges(ActiveFaces(X)))
        {
            var eb = e.Bounds(_sampleTol * 500);
            foreach (var (f, box) in fboxes)
                if (eb.Overlaps(box, _onTol)) cand.Add((e, f));
        }
        pc.Dispose();
        var found = new NIntersect.EdgeHits[cand.Count];
        using (Prof.Time("split.roots"))
            Parallel.For(0, cand.Count, k =>
            {
                var (e, f) = cand[k];
                // an edge of a face on that very surface lies on it (a fitted surface, a blend's pipe, is only as exact as
                // its fit: solved against it the edge strays off by its noise, and a spurious root near an end splits it on
                // one side only, as where a fillet rolls onto another fillet's blend)
                if (f.Surface.Kind is SurfaceKind.Pipe or SurfaceKind.BSpline or SurfaceKind.Offset
                    && e.Uses.Any(u => ReferenceEquals(u.Face.Surface, f.Surface) || u.Face.Surface.Kind == f.Surface.Kind && u.Face.Surface.CoincidentWith(f.Surface, _onTol)))
                {
                    found[k] = new NIntersect.EdgeHits(new List<double>(), true);
                    return;
                }
                using (Prof.Time(Prof.Enabled ? $"split.{e.Curve.GetType().Name}x{f.Surface.Kind}" : "")) found[k] = NIntersect.EdgeSurface(e, f.Surface, _tol);
            });
        using var pa = Prof.Time("split.apply");
        for (int k = 0; k < cand.Count; k++)
        {
            var (e, f) = cand[k];
            var hits = found[k];
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
                // the edge runs along the surface here (e.g. a seam crossing a fillet's contact line): the hit is a
                // double root, accurate only to about the square root of the solver tolerance. Where a vertex of the
                // other body lies on the edge close by (the end of a blend's contact line on it), that is the point.
                bool grazing = Math.Abs(Vec3.Dot(e.Curve.Deriv(t).Normalized, f.Surface.Normal(p))) < 0.05;
                NVertex? w = null; double tw = t;
                // a grazing touch this close to an end of the edge is that end
                if (grazing && (Vec3.Distance(p, e.V0.P) < grazeNear || Vec3.Distance(p, e.V1.P) < grazeNear)) continue;
                if (grazing)
                {
                    yVerts ??= VerticesOf(NearEdges(ActiveFaces(Y)));
                    foreach (var y in yVerts)
                    {
                        if (Vec3.Distance(y.P, p) > grazeNear || ReferenceEquals(y, e.V0) || ReferenceEquals(y, e.V1)) continue;
                        var ty = Refine(e.Curve, y.P, t);
                        if (Vec3.Distance(e.Curve.Eval(ty), y.P) > _tol * 10) continue;
                        if (w == null || Vec3.Distance(y.P, p) < Vec3.Distance(w.P, p)) { w = y; tw = ty; }
                    }
                }
                var v = w ?? Snap(p, _fittedEdges.Contains(e) || f.Surface.Kind is SurfaceKind.BSpline or SurfaceKind.Offset ? Noise : 0);
                var ts = w != null ? tw : t;
                if (Vec3.Distance(v.P, e.V0.P) < _tol * 10 || Vec3.Distance(v.P, e.V1.P) < _tol * 10) continue;
                if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                if (!l.Any(x => ReferenceEquals(x.Item2, v))) l.Add((ts, v));
                if (grazing && w == null) _grazing.Add(v);
            }
        }
    }

    // edges of B-spline patches (and of their offsets): fitted geometry. Where one of them, or a patch, is involved,
    // the same point found from two pairs lands up to the fitting noise apart (an edge of each body ending on the
    // other's coincident cylinder a few microns apart): a split there is snapped to a vertex within Noise
    readonly HashSet<NEdge> _fittedEdges = new(ReferenceEqualityComparer.Instance);
    double Noise => _sampleTol * 50;

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
        // the crossings of every pair first, in parallel (pure geometry; the pairs keyed in the order met, as below);
        // the splits are then made in order from them
        var todo = new List<(NEdge, NEdge)>();
        var seen = new HashSet<(NEdge, NEdge)>();
        foreach (var (e, f) in onSurface)
        {
            var eb = e.Bounds(_sampleTol * 500);
            foreach (var c in f.Coedges)
            {
                var g = c.Edge;
                if (ReferenceEquals(g, e) || !eb.Overlaps(g.Bounds(_sampleTol * 500), _onTol)) continue;
                int oe = Order(e), og = Order(g);
                var key = oe > og ? (g, e) : (e, g);
                if (!_crossings.ContainsKey(key) && seen.Add(key)) todo.Add(key);
            }
        }
        if (todo.Count > 1)
        {
            var res = new List<(double, double, Vec3, double)>[todo.Count];
            Parallel.For(0, todo.Count, k =>
            {
                var (x, y) = todo[k];
                bool flip = x.FinestSamples(_tol * 50).Count < y.FinestSamples(_tol * 50).Count;
                res[k] = flip ? CurveCrossings(y, x).Select(c => (c.tb, c.ta, c.p, c.reach)).ToList() : CurveCrossings(x, y).Select(c => (c.ta, c.tb, c.p, c.reach)).ToList();
            });
            for (int k = 0; k < todo.Count; k++) _crossings[todo[k]] = res[k];
        }
        foreach (var (e, f) in onSurface)
        {
            var eb = e.Bounds(_sampleTol * 500);
            if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_CROSS") is { } tcf && e.Mid.ToString().Contains(tcf))
                T($"   pair {e.Curve.GetType().Name}{e.V0.P}->{e.V1.P} on {f.Tag.Name}: " + string.Join(" ", f.Coedges.Select(c => $"[{c.Edge.Curve.GetType().Name}{c.Edge.V0.P}->{c.Edge.V1.P}]")));
            foreach (var c in f.Coedges)
            {
                var g = c.Edge;
                if (ReferenceEquals(g, e) || !eb.Overlaps(g.Bounds(_sampleTol * 500), _onTol)) continue;
                foreach (var (te0, tg0, p, reach) in Crossings(e, g))
                {
                    if (Near(p, e) && Near(p, g)) continue;
                    // a crossing at (or within the boundary tolerance of) an end of one edge: that end vertex splits the
                    // other edge (a fresh vertex a few microns away would leave the two bodies' boundaries apart)
                    var r = _fittedEdges.Contains(e) || _fittedEdges.Contains(g) ? Noise : 0;
                    var end = new[] { e.V0, e.V1, g.V0, g.V1 }.Where(w => Vec3.Distance(w.P, p) < Math.Max(_onTol, r)).MinBy(w => Vec3.Distance(w.P, p));
                    // a split either edge already has there (an edge/surface hit at the same point: the contact line
                    // crossing a plane on the boundary it also crosses) is the same vertex: two would leave a
                    // zero-length edge between them
                    end ??= new[] { e, g }.SelectMany(x => splits.GetValueOrDefault(x) ?? new())
                        .Where(x => !_grazing.Contains(x.v) && Vec3.Distance(x.v.P, p) < Math.Max(_onTol, r)).Select(x => x.v).MinBy(w => Vec3.Distance(w.P, p));
                    var v = end ?? Snap(p, r);
                    var targets = new List<(NEdge edge, double t)>();
                    foreach (var (edge, t) in new[] { (e, te0), (g, tg0) })
                        if (!ReferenceEquals(edge.V0, v) && !ReferenceEquals(edge.V1, v)) targets.Add((edge, end == null ? t : ParamOn(edge, v.P, t)));
                    foreach (var (edge, t) in targets)
                    {
                        if (!splits.TryGetValue(edge, out var l)) splits[edge] = l = new();
                        // grazing splits of this edge close to the crossing were the same point, computed badly
                        l.RemoveAll(x => !ReferenceEquals(x.v, v) && _grazing.Contains(x.v) && Vec3.Distance(x.v.P, p) < near + reach);
                        if (!l.Any(x => ReferenceEquals(x.v, v))) l.Add((t, v));
                    }
                    if (Trace != null) T($"  on-surface crossing {p} reach {reach:g3}: {e.Curve.GetType().Name}{e.Mid} x {g.Curve.GetType().Name}{g.Mid} on {f.Tag.Name}; splits now {string.Join(" ", splits.GetValueOrDefault(e)?.Select(x => x.v.P) ?? [])} | {string.Join(" ", splits.GetValueOrDefault(g)?.Select(x => x.v.P) ?? [])}");
                }
            }
        }
        bool Near(Vec3 p, NEdge x) => Vec3.Distance(p, x.V0.P) < _tol * 10 || Vec3.Distance(p, x.V1.P) < _tol * 10;
        static double ParamOn(NEdge x, Vec3 p, double hint)
        {
            double lo = Math.Min(x.T0, x.T1), hi = Math.Max(x.T0, x.T1);
            var t = Refine(x.Curve, p, x.Curve.Param(p, hint));
            if (x.Curve.Periodic) { while (t < lo - 1e-9) t += x.Curve.Period; while (t > hi + 1e-9) t -= x.Curve.Period; }
            return Math.Clamp(t, lo, hi);
        }
    }

    readonly Dictionary<(NEdge, NEdge), List<(double, double, Vec3, double)>> _crossings = new();

    // edges in the order this boolean first met them: a deterministic order for unordered pairs (object hash codes
    // differ from run to run, and the crossing computation is not symmetric in its last bits)
    readonly Dictionary<NEdge, int> _edgeOrder = new(ReferenceEqualityComparer.Instance);
    int Order(NEdge e)
    {
        if (!_edgeOrder.TryGetValue(e, out var o)) _edgeOrder[e] = o = _edgeOrder.Count;
        return o;
    }

    /// <summary>Crossings of an unordered pair of edges, computed once (the same answer whichever edge is on the face).</summary>
    List<(double ta, double tb, Vec3 p, double reach)> Crossings(NEdge e, NEdge g)
    {
        int oe = Order(e), og = Order(g);
        bool swap = oe > og;
        var key = swap ? (g, e) : (e, g);
        if (!_crossings.TryGetValue(key, out var r))
        {
            // the distance profile is read along the more finely sampled edge (a line has a single segment); seeds from
            // fine samples (the crossings of contact lines with face boundaries are shallow)
            var (x, y) = key;
            bool flip = x.FinestSamples(_tol * 50).Count < y.FinestSamples(_tol * 50).Count;
            r = flip ? CurveCrossings(y, x).Select(c => (c.tb, c.ta, c.p, c.reach)).ToList() : CurveCrossings(x, y).Select(c => (c.ta, c.tb, c.p, c.reach)).ToList();
            _crossings[key] = r;
        }
        return swap ? r.Select(x => (x.Item2, x.Item1, x.Item3, x.Item4)).ToList() : r;
    }

    /// <summary>Points where two edges cross (closer than the coincidence tolerance), from polyline seeds refined by alternating projection.</summary>
    List<(double ta, double tb, Vec3 p, double reach)> CurveCrossings(NEdge a, NEdge b)
    {
        var res = new List<(double, double, Vec3, double)>();
        // two helices of the same screw motion (a multi-start thread's grooves on one root cylinder) never cross: they
        // are the same helix (an overlap, found below) or run beside each other all the way
        if (a.Curve is NHelix ha && b.Curve is NHelix hb && ha.Motion.SameHelices(hb.Motion) && ha.Motion is var hm && Math.Abs(hm.K) > 1e-12)
        {
            var lead = Math.Abs(2 * Math.PI * hm.K);
            Vec2 Rz(Vec3 p) { var l = hm.Local(p); return new Vec2(Math.Sqrt(l.X * l.X + l.Y * l.Y), l.Z - hm.K * Math.Atan2(l.Y, l.X)); }
            var qa = Rz(ha.P0); var qb = Rz(hb.P0);
            var coincidentTol = _tol * 100;
            if (Math.Abs(qa.X - qb.X) > coincidentTol || Math.Abs(Math.IEEERemainder(qa.Y - qb.Y, lead)) > coincidentTol) return res;
        }
        var pa = a.FinestSamples(_tol * 50); var pb = b.FinestSamples(_tol * 50);
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
                // at an end of the edge the run is an overlap only if that end lies on the other edge (else the edges
                // cross in the last segment)
                bool Away(int seg, bool atStart) => atStart ? seg == 1 && DistTo(pa[0], pb) >= coincident : seg == pa.Count - 1 && DistTo(pa[^1], pb) >= coincident;
                bool local = (best.TryGetValue(i - 1, out var before) ? before.d >= coincident : Away(i, true))
                    && (best.TryGetValue(keys[k1] + 1, out var after) ? after.d >= coincident : Away(keys[k1], false));
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
            T($"    cross {a.Curve.GetType().Name}{a.V0.P}->{a.V1.P} x {b.Curve.GetType().Name}{b.V0.P}->{b.V1.P}: segs {sa.Count}x{sb.Count} coinc {coincident:g3} gap {gap:g3} best {string.Join(" ", best.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value.d:g3}"))} seeds {string.Join(",", seeds)}");
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

    static double DistTo(Vec3 p, List<Vec3> poly)
    {
        double best = double.MaxValue;
        for (int i = 1; i < poly.Count; i++) best = Math.Min(best, SegSeg(p, p, poly[i - 1], poly[i]).d);
        return best;
    }

    /// <summary>Newton steps on |c(t) - p|^2 from t (curve parameters from projections can be a little off).</summary>
    static double Refine(NCurve c, Vec3 p, double t)
    {
        for (int i = 0; i < 8; i++)
        {
            var d = c.Deriv(t); var dd = Vec3.Dot(d, d);
            if (!(dd > 1e-300)) break;
            var step = Vec3.Dot(c.Eval(t) - p, d) / dd;
            t -= step;
            if (Math.Abs(step) < 1e-15 * Math.Max(1, Math.Abs(t))) break;
        }
        return t;
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
        var xA = new ByX(forA); var xB = new ByX(forB);
        var inBox = new List<int>();
        foreach (var (e, cand, byX) in NearEdges(ActiveFaces(A)).Select(e => (e, forA, xA)).Concat(NearEdges(ActiveFaces(B)).Select(e => (e, forB, xB))))
        {
            if (cand.Count == 0) continue;
            var box = e.Bounds(_sampleTol * 500);
            // only vertices within the box's x range can pass its test below
            byX.InRange(box.Min.X - _onTol, box.Max.X + _onTol, inBox);
            foreach (var iv in inBox)
            {
                var v = cand[iv];
                if (ReferenceEquals(v, e.V0) || ReferenceEquals(v, e.V1) || !box.Contains(v.P, _onTol)) continue;
                if (Vec3.Distance(v.P, e.V0.P) < _tol * 10 || Vec3.Distance(v.P, e.V1.P) < _tol * 10) continue;
                double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
                var t = Refine(e.Curve, v.P, e.Curve.Param(v.P, e.Curve.Periodic ? 0.5 * (lo + hi) : null));
                if (t <= lo || t >= hi || Vec3.Distance(e.Curve.Eval(t), v.P) > _tol * 10) continue;
                if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
                // an exact vertex of the other body supersedes grazing hits of this edge around it (an edge lying on a
                // face, touching the blend next to it where the blend's boundary ends)
                if (!_grazing.Contains(v))
                {
                    var near = Math.Max(_onTol * 200, 1e3 * _tol);
                    l.RemoveAll(x => !ReferenceEquals(x.v, v) && _grazing.Contains(x.v) && Vec3.Distance(x.v.P, v.P) < near);
                }
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

    /// <summary>
    /// The section edges of a face pair (its intersection curves cut at the vertices on both surfaces, the pieces
    /// inside both faces). Without <paramref name="allowSnap"/> nothing shared is changed: null where a closed curve
    /// touching no vertex would need vertices of its own.
    /// </summary>
    List<NEdge>? SectionEdges(NFace fa, NFace fb, List<NCurve> curves, bool allowSnap)
    {
        var res = new List<NEdge>();
        var sa = fa.Surface; var sb = fb.Surface;
        // breakpoints: vertices of either face lying on both surfaces (as closely as a fitted edge through them is
        // known to lie on its surfaces: a shell's edge between nearly tangent offsets is off them by its fit)
        var vtol = new Dictionary<NVertex, double>(ReferenceEqualityComparer.Instance);
        foreach (var c in fa.Coedges.Concat(fb.Coedges))
            foreach (var v in new[] { c.Start, c.End })
                vtol[v] = Math.Max(vtol.GetValueOrDefault(v, _onTol), c.Edge.Curve is NSpline sp ? 2 * sp.FitTol : 0);
        var bps = vtol.Keys.Where(v => Math.Abs(sa.F_(v.P)) < vtol[v] && Math.Abs(sb.F_(v.P)) < vtol[v]).ToList();
        NDomain domA, domB;
        using (Prof.Time("sec.domains")) { domA = Dom(fa); domB = Dom(fb); }
        var dbgPair = Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_PAIR") is { } tp && (fa.Tag.Name + "|" + fb.Tag.Name).Contains(tp);
        if (dbgPair) T($"  pair {fa.Tag.Name} x {fb.Tag.Name}: {curves.Count} curves ({string.Join(",", curves.Select(c => c.GetType().Name))}), breakpoints {string.Join(" ", bps.Select(v => v.P))}");
        if (dbgPair) foreach (var c in curves.OfType<NPolyline>()) T($"    polyline {c.Pts[0]} .. {c.Pts[^1]} ({c.Pts.Length} pts, closed={c.IsClosed}), breakpoint tolerance {PolylineOnTol}, breakpoint distances {string.Join(" ", bps.Select(v => Vec3.Distance(c.Eval(c.Param(v.P)), v.P).ToString("0.######")))}");
        foreach (var c in curves)
        {
            var on = new List<(double t, NVertex v)>();
            // traced curves are exact at their samples but can pass a singular point (two surfaces tangent
            // there, e.g. crossing fillets) at up to the trace tolerance; the edge is pinned to the vertex anyway
            // (breakpoints lie exactly on both surfaces, so a traced curve passing this close does go through them)
            var onTol = c is NPolyline ? PolylineOnTol : _onTol * 4;
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
                if (!allowSnap) return null;
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
                Vec3 qa, qb; using (Prof.Time("sec.midproj")) { qa = sa.Project(mid); qb = sb.Project(mid); }
                using (Prof.Time("sec.midclass")) { ca = domA.Classify(qa, _tol * 10); cb = domB.Classify(qb, _tol * 10); }
                if (dbgPair) T($"    interval {v0.P}->{v1.P} mid {mid} inA={ca} inB={cb}");
                if (ca <= 0 || cb <= 0) continue;
                NEdge ne;
                using (Prof.Time("sec.makeedge")) ne = MakeEdge(c, t0, t1, v0, v1, sa, sb);
                res.Add(ne);
            }
        }
        return res;
    }

    /// <summary>How far a traced section's polyline may pass from a breakpoint that it goes through.</summary>
    double PolylineOnTol => Math.Max(_onTol * 4, _sampleTol * 500);

    static double Mod(double t, double p) { t %= p; return t < 0 ? t + p : t; }

    NEdge MakeEdge(NCurve c, double t0, double t1, NVertex v0, NVertex v1, NSurface sa, NSurface sb)
    {
        if (c is NPolyline pl)
        {
            var pts = pl.Between(t0, t1);
            pts[0] = v0.P; pts[^1] = v1.P;
            var spline = FitSpline(pts, _sampleTol * 5);
            if (spline == null) return new NEdge(new NLine(v0.P, v1.P - v0.P), 0, Vec3.Distance(v0.P, v1.P), v0, v1);
            // the fit is checked at the exact points; between them it may stray a little further
            return new NEdge(new NSpline(spline) { FitTol = _sampleTol * 5 * 2 }, 0, 1, v0, v1);
        }
        return new NEdge(c, t0, t1, v0, v1);
    }

    /// <summary>
    /// Cubic B-spline through a minimal subset of the exact points (Douglas-Peucker seed), refined until
    /// every exact point lies within tol of the spline. A point is measured against the stretch of spline between
    /// the kept points either side of it (never nearer than the whole spline, so the fit is never looser); each
    /// pass adds the worst point of every stretch that is out.
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
        var add = new List<int>();
        for (int iter = 0; iter < 40; iter++)
        {
            var ki = keep.ToArray();
            var sel = ki.Select(i => pts[i]).ToList();
            var sp = BSplineCurve.Interpolate(sel, 3);
            // the kept points' parameters (Interpolate's chord lengths)
            var uk = new double[ki.Length];
            double total = 0;
            for (int k = 1; k < ki.Length; k++) total += Vec3.Distance(sel[k], sel[k - 1]);
            if (total < 1e-15) total = 1;
            for (int k = 1; k < ki.Length; k++) uk[k] = uk[k - 1] + Vec3.Distance(sel[k], sel[k - 1]) / total;
            uk[^1] = 1;
            add.Clear();
            for (int k = 0; k + 1 < ki.Length; k++)
            {
                int a = ki[k], b = ki[k + 1];
                if (b - a < 2) continue;
                // chord length along the exact points for each one's starting parameter
                double run = 0, len = 0;
                for (int i = a + 1; i <= b; i++) len += Vec3.Distance(pts[i], pts[i - 1]);
                double worst = 0; int wi = -1;
                for (int i = a + 1; i < b; i++)
                {
                    run += Vec3.Distance(pts[i], pts[i - 1]);
                    var t0 = uk[k] + (uk[k + 1] - uk[k]) * (len > 0 ? run / len : 0.5);
                    var t = sp.ClosestParam(pts[i], uk[k], uk[k + 1], t0);
                    if (double.IsNaN(t)) t = t0;
                    var dist = Vec3.Distance(sp.Eval(t), pts[i]);
                    if (dist > worst) { worst = dist; wi = i; }
                }
                if (worst > tol) add.Add(wi);
            }
            if (add.Count == 0) return sp;
            foreach (var i in add) keep.Add(i);
        }
        return BSplineCurve.Interpolate(keep.Select(i => pts[i]).ToList(), 3);
    }

    /// <summary>
    /// Edges of 'src' that lie on dst's surface (each a blocker) and, those inside dst, section edges of dst: listed
    /// as (dst, edge, inside).
    /// </summary>
    void ImprintOnSurfaceEdges(NFace src, NFace dst, List<(NFace, NEdge, bool)> found)
    {
        NDomain? dom = null;
        foreach (var c in src.Coedges)
        {
            var e = c.Edge;
            bool on = true;
            var onTol = Math.Max(_tol * 10, e.Curve is NSpline ns ? ns.FitTol * 2 : 0);
            for (int i = 0; i <= 8 && on; i++) if (Math.Abs(dst.Surface.F_(e.At(i / 8.0))) > onTol) on = false;
            if (!on) continue;
            dom ??= Dom(dst);
            found.Add((dst, e, dom.Classify(dst.Surface.Project(e.Mid), _onTol) > 0));
        }
    }

    /// <summary>Edges of 'src' lying inside the coincident face 'dst' become section edges of dst: listed as (dst, edge, true).</summary>
    void Imprint(NFace src, NFace dst, List<(NFace, NEdge, bool)> found)
    {
        var dom = Dom(dst);
        foreach (var c in src.Coedges)
        {
            var mid = c.Edge.Mid;
            if (dom.Classify(dst.Surface.Project(mid), _onTol) > 0) found.Add((dst, c.Edge, true));
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
        // a section that is a boundary edge, or a copy of one from the other body (same ends, same curve; sewn to it
        // later), does not split the face
        bool Copy(NEdge e) => f.Coedges.Any(c => (ReferenceEquals(c.Edge.V0, e.V0) && ReferenceEquals(c.Edge.V1, e.V1) || ReferenceEquals(c.Edge.V0, e.V1) && ReferenceEquals(c.Edge.V1, e.V0))
            && !e.Closed && Vec3.Distance(c.Edge.Mid, e.Mid) < Math.Max(_onTol * 4, Math.Max(FitTol(e.Curve), FitTol(c.Edge.Curve)) * 4));
        secs = secs.Where(e => !boundaryEdges.Contains(e) && !Copy(e)).ToList();
        // the same curve twice among the sections (an edge of the other body and the traced intersection with one of
        // its faces): one splits the face, sewing joins the other to it
        for (int i = secs.Count - 1; i > 0; i--)
        {
            var e = secs[i];
            for (int j = 0; j < i; j++)
            {
                var o = secs[j];
                if (e.Closed || !(ReferenceEquals(o.V0, e.V0) && ReferenceEquals(o.V1, e.V1) || ReferenceEquals(o.V0, e.V1) && ReferenceEquals(o.V1, e.V0))) continue;
                if (Vec3.Distance(o.Mid, e.Mid) < Math.Max(_onTol * 4, Math.Max(FitTol(e.Curve), FitTol(o.Curve)) * 4)) { secs.RemoveAt(i); break; }
            }
        }
        // prune dangling section edges (an end not connected to anything else)
        var vertexDeg = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        void Inc(NVertex v) => vertexDeg[v] = vertexDeg.GetValueOrDefault(v) + 1;
        foreach (var c in f.Coedges) { Inc(c.Start); Inc(c.End); }
        foreach (var e in secs) { Inc(e.V0); Inc(e.V1); }
        // (the edges left are the same whatever the order of removal: a worklist of edges at each touched vertex)
        var atVertex = new Dictionary<NVertex, List<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < secs.Count; i++)
            foreach (var v in new[] { secs[i].V0, secs[i].V1 })
            {
                if (!atVertex.TryGetValue(v, out var l)) atVertex[v] = l = new();
                l.Add(i);
            }
        var removed = new bool[secs.Count];
        var work = new Stack<int>(Enumerable.Range(0, secs.Count).Reverse());
        while (work.Count > 0)
        {
            int i = work.Pop();
            if (removed[i]) continue;
            var e = secs[i];
            if (vertexDeg[e.V0] >= 2 && vertexDeg[e.V1] >= 2) continue;
            removed[i] = true; vertexDeg[e.V0]--; vertexDeg[e.V1]--;
            foreach (var j in atVertex[e.V0]) if (!removed[j]) work.Push(j);
            foreach (var j in atVertex[e.V1]) if (!removed[j]) work.Push(j);
        }
        if (removed.Any(x => x)) secs = secs.Where((_, i) => !removed[i]).ToList();
        if (Trace != null && Environment.GetEnvironmentVariable("FC_TRACE_FACE") is { } tfs && f.Tag.Name.Contains(tfs))
            T($"  split {f.Tag.Name}: {secs.Count} sections after filtering: " + string.Join(" ", secs.Select(e => $"[{e.V0.P}#{Id(e.V0)}->{e.V1.P}#{Id(e.V1)} {e.Curve.GetType().Name[1..]} mid-gap {f.Coedges.Where(c => ReferenceEquals(c.Edge.V0, e.V0) || ReferenceEquals(c.Edge.V1, e.V0)).Select(c => Vec3.Distance(c.Edge.Mid, e.Mid)).DefaultIfEmpty(-1).Min():g3} fit {(e.Curve is NSpline ns2 ? ns2.FitTol : 0):g3} vs {f.Coedges.Select(c => c.Edge.Curve).OfType<NSpline>().Select(x => x.FitTol).DefaultIfEmpty(0).Max():g3} same curve {f.Coedges.Any(c => ReferenceEquals(c.Edge.Curve, e.Curve))}]")));
        if (secs.Count == 0) { yield return f; yield break; }

        var hes = new List<HalfEdge>();
        HalfEdge Make(NEdge e, bool fwd, Vec2[]? mapped = null)
        {
            // (a section's two sides: its samples mapped once, the copy reversed)
            var uv = mapped != null ? (Vec2[])mapped.Clone() : dom.MapAlong(e.FinestSamples(_sampleTol * 500));
            if (!fwd) Array.Reverse(uv);
            var h = new HalfEdge { Edge = e, Forward = fwd, From = fwd ? e.V0 : e.V1, To = fwd ? e.V1 : e.V0, Uv = uv };
            h.OutAngle = Dir(uv, true).Angle;
            h.InAngle = Dir(uv, false).Angle;
            return h;
        }
        foreach (var c in f.Coedges) hes.Add(Make(c.Edge, c.Forward));
        foreach (var e in secs) { var h = Make(e, true); hes.Add(h); hes.Add(Make(e, false, h.Uv)); }
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
                // (the surface test first: it rejects most pairs at once, where a face's box may first have to be computed)
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
        try { return ClassifyPoint(f, p, other, otherBox); }
        catch (NBooleanException)
        {
            // the face's point on an edge of the other body (every ray starts there): other points inside the face,
            // the farthest from its boundary first
            var dom = Dom(f);
            foreach (var q in dom.InteriorSamples(31).Where(q => dom.Classify(q, _onTol * 4) > 0).OrderByDescending(dom.BoundaryDistance).Take(8))
            {
                try { return ClassifyPoint(f, q, other, otherBox); }
                catch (NBooleanException) { }
            }
            throw;
        }
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
        // the faces of a thread share one screw surface: its crossings (a long march) are found once per ray
        Dictionary<ScrewSurface, List<double>>? screwHits = null;
        foreach (var d in RayDirs)
        {
            int count = 0; bool ok = true;
            List<string>? hits = dbg ? new() : null;
            screwHits?.Clear();
            foreach (var g in other)
            {
                if (!RayHitsBox(p, d, FBox(g), _onTol * 4)) continue;
                List<double> gHits;
                if (g.Surface.Kind == SurfaceKind.Screw)
                {
                    screwHits ??= new(ReferenceEqualityComparer.Instance);
                    if (!screwHits.TryGetValue(g.Surface.Screw!, out gHits!)) screwHits[g.Surface.Screw!] = gHits = g.Surface.RayHits(p, d);
                }
                else gHits = g.Surface.RayHits(p, d);
                foreach (var t in gHits)
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
        var byX = new ByX(openVerts);
        var inBox = new List<int>();
        foreach (var e in single)
        {
            var box = e.Bounds(_sampleTol * 500);
            byX.InRange(box.Min.X - _onTol, box.Max.X + _onTol, inBox);
            foreach (var iv in inBox)
            {
                var v = openVerts[iv];
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
                    if (Vec3.Distance(l[i].Mid, l[j].Mid) > Math.Max(_onTol * 4, Math.Max(FitTol(l[i].Curve), FitTol(l[j].Curve)) * 4)) continue;
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
            if (ReferenceEquals(f, g)) continue;
            if (f.Surface.Kind == SurfaceKind.Plane && g.Surface.Kind == SurfaceKind.Plane)
            {
                // one plane facing the same way out, however each stores it (a tool's cap, flipped by the boolean, on
                // the face it lies on: its normal reversed and its sense too)
                var nf = f.Surface.Plane!.Normal * (f.Sense ? 1 : -1); var ng = g.Surface.Plane!.Normal * (g.Sense ? 1 : -1);
                if (Vec3.Dot(nf, ng) < 1 - 1e-9 || Math.Abs(f.Surface.Plane.Distance(g.Surface.Plane.Project(f.Surface.Plane.Project(Vec3.Zero)))) > _tol * 10) continue;
            }
            else
            {
                if (f.Sense != g.Sense) continue;
                if (!ReferenceEquals(f.Surface, g.Surface) && !f.Surface.CoincidentWith(g.Surface, _tol * 10)) continue;
            }
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

    /// <summary>Whether the faces lie on opposite sides of the plane (their B-spline poles do), touching it at most.</summary>
    static bool Apart(NFace fa, NFace fb, Plane pl)
    {
        var a = fa.Surface.Nurbs!; var b = fb.Surface.Nurbs!;
        var tol = 1e-9 * Math.Max(1, Math.Max(a.Box.Diagonal, b.Box.Diagonal));
        (double lo, double hi) Side(NurbsSurface s)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var p in s.P) { var d = pl.Distance(p); lo = Math.Min(lo, d); hi = Math.Max(hi, d); }
            return (lo, hi);
        }
        var (la, ha) = Side(a); var (lb, hb) = Side(b);
        return la >= -tol && hb <= tol || ha <= tol && lb >= -tol;
    }

    /// <summary>The parts of the curves (polylines) lying on the surface: runs of their points within tol of it.</summary>
    static List<NCurve> OnSurface(List<NCurve> curves, NSurface s, double tol)
    {
        var res = new List<NCurve>();
        foreach (var c in curves)
        {
            if (c is not NPolyline pl) { res.Add(c); continue; }
            var run = new List<Vec3>();
            void Flush() { if (run.Count >= 2) res.Add(new NPolyline(run.ToList(), false)); run.Clear(); }
            var pts = pl.IsClosed ? pl.Pts.Take(pl.Pts.Length - 1).ToArray() : pl.Pts;
            var on = pts.Select(p => Math.Abs(s.F_(p)) <= tol).ToArray();
            // a closed curve all on the surface stays closed
            if (pl.IsClosed && on.All(x => x)) { res.Add(c); continue; }
            for (int i = 0; i < pts.Length; i++)
            {
                if (on[i]) run.Add(pts[i]);
                else Flush();
            }
            Flush();
        }
        return res;
    }

    /// <summary>
    /// The plane two B-spline faces are mirror images in (poles reflected one for one, the same knots and weights),
    /// when the first lies on one side of it (its poles do, so the surface does): then the faces meet only in that
    /// plane. Null otherwise.
    /// </summary>
    static Plane? MirrorPlane(NFace fa, NFace fb)
    {
        if (fa.Surface.Nurbs is not { } a || fb.Surface.Nurbs is not { } b || ReferenceEquals(a, b)) return null;
        if (a.Pu != b.Pu || a.Pv != b.Pv || a.P.GetLength(0) != b.P.GetLength(0) || a.P.GetLength(1) != b.P.GetLength(1)) return null;
        if (!a.U.SequenceEqual(b.U) || !a.V.SequenceEqual(b.V) || (a.W == null) != (b.W == null)) return null;
        var tol = 1e-9 * Math.Max(1, a.Box.Diagonal);
        // the pair of poles farthest apart fixes the plane (their perpendicular bisector)
        int ni = a.P.GetLength(0), nj = a.P.GetLength(1);
        double far = 0; Vec3 pa = default, pb = default;
        for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
            {
                if (a.W != null && Math.Abs(a.W[i, j] - b.W![i, j]) > 1e-12 * Math.Abs(a.W[i, j])) return null;
                var d = Vec3.Distance(a.P[i, j], b.P[i, j]);
                if (d > far) { far = d; pa = a.P[i, j]; pb = b.P[i, j]; }
            }
        if (far < 100 * tol) return null; // the same surface
        var pl = Plane.FromPointNormal(0.5 * (pa + pb), (pb - pa).Normalized);
        double lo = 0, hi = 0;
        for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
            {
                var p = a.P[i, j]; var dp = pl.Distance(p);
                if (Vec3.Distance(p - pl.Normal * (2 * dp), b.P[i, j]) > tol) return null;
                lo = Math.Min(lo, dp); hi = Math.Max(hi, dp);
            }
        return lo >= -tol || hi <= tol ? pl : null;
    }
}

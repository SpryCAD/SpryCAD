using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Blends applied straight to the body's faces instead of by a boolean. A blend's tool here is the region between
/// the blended edge, the blend's contact curves and the blend surface: its other faces lie on the faces it blends
/// (the strips the blend replaces) and on the faces its square ends meet. On each face of the body the result is
/// the face's boundary with the tool's boundary there added (a concave blend adds material) or taken away (a convex
/// one removes it), as oriented edges: the edges they share cancel, the contact curves remain. The blend faces are
/// put in between. Nothing is intersected or classified, so it is fast, and exact by construction (the contact
/// curve is one edge of both faces).
/// <para>
/// A tool it cannot apply so is left to the boolean (<see cref="Apply"/> says which): a tool face on no face of the
/// body (an end meeting a face at an angle), other geometry near the blend, blends meeting each other (corners), or
/// a result that is not a valid shell.
/// </para>
/// </summary>
public static class DirectBlend
{
    /// <summary>Off: every blend by boolean (FC_BLEND=boolean; tests compare the two).</summary>
    public static bool Enabled = Environment.GetEnvironmentVariable("FC_BLEND") != "boolean";

    /// <summary>Diagnostics: why a tool was left to the boolean (tests/tools).</summary>
    public static Action<string>? Debug;

    static int _applied, _declined;
    /// <summary>Diagnostics: tools applied directly, and left to the boolean, since the start.</summary>
    public static (int applied, int declined) Counts => (_applied, _declined);

    /// <summary>A tool: <paramref name="Strip"/>'s non-blend faces lie on the faces of the body it blends.</summary>
    public sealed record Tool(Solid Strip, bool Convex, string Name, double Size);

    static readonly ReferenceEqualityComparer Ref = ReferenceEqualityComparer.Instance;

    /// <summary>
    /// Applies the tools it can to a copy of the body; null when it applied none. The others (by index) are added to
    /// <paramref name="left"/>, for the boolean.
    /// </summary>
    public static NBody? Apply(NBody body, IReadOnlyList<Tool> tools, List<int> left)
    {
        // tools whose boxes overlap form clusters: tools that only meet at their ends (an edge's blend running on
        // into the next one's tangentially, both ending square on the plane through the joint) are glued into one
        // tool
        var boxes = tools.Select(t => t.Strip.Bounds).ToList();
        var parent = Enumerable.Range(0, tools.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < tools.Count; i++)
            for (int j = i + 1; j < tools.Count; j++)
                if (boxes[i].Overlaps(boxes[j], 1e-9 * Math.Max(1, Math.Max(boxes[i].Diagonal, boxes[j].Diagonal)))) parent[Find(i)] = Find(j);
        NBody? work = null;
        bool any = false;
        foreach (var cluster in Enumerable.Range(0, tools.Count).GroupBy(Find).Select(g => g.ToList()))
        {
            // glued into one where they meet end to end; else one at a time (each checked against the body as the
            // ones before left it: a blend running into another's is left to the boolean)
            var attempts = new List<(Tool tool, List<int> members)>();
            string? glueWhy = null;
            if (cluster.Count > 1 && Glue(cluster.Select(k => tools[k]).ToList(), out glueWhy) is { } glued) attempts.Add((glued, cluster));
            else attempts.AddRange(cluster.Select(k => (tools[k], new List<int> { k })));
            foreach (var (tool, members) in attempts)
            {
                work ??= body.Clone();
                NBody? next;
                string? why;
                using (Prof.Time("blend.direct")) next = Try(work, tool, out why);
                if (next != null) { work = next; any = true; }
                if (why != null && glueWhy != null) why += $" (not glued: {glueWhy})";
                foreach (var k in members)
                    if (why == null) Interlocked.Increment(ref _applied);
                    else
                    {
                        left.Add(k);
                        Interlocked.Increment(ref _declined);
                        Debug?.Invoke($"{tools[k].Name}: by boolean ({why})");
                    }
            }
        }
        return any ? work : null;
    }

    /// <summary>
    /// The tools as one, where they meet only at end faces: a plane face of one with the same boundary as a plane
    /// face of another, facing the other way (the two ends cancel, their edges become one). Null (and why) when
    /// they meet otherwise, or are of different kinds (adding and removing material).
    /// </summary>
    internal static Tool? Glue(List<Tool> tools, out string? why)
    {
        why = null;
        if (tools.Any(t => t.Strip.Native == null || t.Convex != tools[0].Convex)) { why = "mixed tools"; return null; }
        var bodies = tools.Select(t => t.Strip.Native!.Clone()).ToList();
        var box = Box3.Empty;
        foreach (var b in bodies) box.Add(b.Bounds);
        var tol = 1e-7 * Math.Max(1, box.Diagonal);
        static Vec3 Out(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);
        var drop = new HashSet<NFace>(Ref);
        // vertices joined across all the ends glued (a corner piece's foot is on two ends, glued to two tools)
        var vpar = new Dictionary<NVertex, NVertex>(Ref);
        NVertex FindV(NVertex x) { while (vpar.TryGetValue(x, out var p) && !ReferenceEquals(p, x)) x = p; return x; }
        void JoinV(NVertex x, NVertex y) { x = FindV(x); y = FindV(y); if (!ReferenceEquals(x, y)) vpar[x] = y; }
        var emap = new Dictionary<NEdge, (NEdge e, bool flip)>(Ref);
        var parent = Enumerable.Range(0, bodies.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
                foreach (var a in bodies[i].Faces.Where(f => f.Surface.Kind == SurfaceKind.Plane && !drop.Contains(f)))
                    foreach (var b in bodies[j].Faces.Where(f => f.Surface.Kind == SurfaceKind.Plane && !drop.Contains(f)))
                    {
                        if (drop.Contains(a)) break;
                        if (a.Loops.Count != 1 || b.Loops.Count != 1) continue;
                        if (!a.Surface.CoincidentWith(b.Surface, tol)) continue;
                        var pa = a.Coedges.First().Start.P;
                        if (Vec3.Dot(Out(a, pa), Out(b, pa)) > 0) continue;
                        // (one end with a straight edge in two where the other's has it in one, a vertex on it: split so)
                        if (a.Loops[0].Coedges.Count != b.Loops[0].Coedges.Count && (SplitAt(bodies[i], a, b, tol) | SplitAt(bodies[j], b, a, tol))) { }
                        if (a.Loops[0].Coedges.Count != b.Loops[0].Coedges.Count) continue;
                        // the same boundary: each edge of b lies along one of a's with the same end points
                        var pairs = new List<(NEdge eb, NEdge ea, bool flip)>();
                        foreach (var cb in b.Coedges)
                        {
                            var eb = cb.Edge;
                            var ea = a.Coedges.Select(c => c.Edge).FirstOrDefault(x =>
                                (Vec3.Distance(x.V0.P, eb.V0.P) < tol && Vec3.Distance(x.V1.P, eb.V1.P) < tol || Vec3.Distance(x.V0.P, eb.V1.P) < tol && Vec3.Distance(x.V1.P, eb.V0.P) < tol)
                                && Vec3.Distance(x.Mid, eb.Mid) < 10 * tol);
                            if (ea == null) break;
                            pairs.Add((eb, ea, Vec3.Distance(ea.V0.P, eb.V0.P) >= tol));
                        }
                        if (pairs.Count != b.Loops[0].Coedges.Count) continue;
                        drop.Add(a); drop.Add(b);
                        foreach (var (eb, ea, flip) in pairs)
                        {
                            emap[eb] = (ea, flip);
                            JoinV(eb.V0, flip ? ea.V1 : ea.V0);
                            JoinV(eb.V1, flip ? ea.V0 : ea.V1);
                        }
                        parent[Find(i)] = Find(j);
                    }
        if (Enumerable.Range(0, bodies.Count).Select(Find).Distinct().Count() > 1) { why = "they do not meet at their ends"; return null; }
        var merged = new NBody();
        foreach (var b in bodies)
            foreach (var f in b.Faces)
            {
                if (drop.Contains(f)) continue;
                foreach (var l in f.Loops)
                    for (int k = 0; k < l.Coedges.Count; k++)
                    {
                        var c = l.Coedges[k];
                        if (emap.TryGetValue(c.Edge, out var m)) l.Coedges[k] = c = new NCoedge(m.e, m.flip ? !c.Forward : c.Forward);
                        c.Edge.V0 = FindV(c.Edge.V0);
                        c.Edge.V1 = FindV(c.Edge.V1);
                    }
                merged.Faces.Add(f);
            }
        var err = merged.Validate();
        if (err != null) { why = "glued: " + err; return null; }
        return new Tool(NTess.ToSolid(merged), tools[0].Convex, tools[0].Name, tools.Min(t => t.Size));
    }

    /// <summary>
    /// The straight edges of face x split at the vertices of face y lying inside them (two ends meeting, one with the
    /// edge in two pieces): in x's body, on both faces using them. Whether any was.
    /// </summary>
    static bool SplitAt(NBody body, NFace x, NFace y, double tol)
    {
        bool any = false;
        foreach (var p in y.Coedges.Select(c => c.Start.P).ToList())
        {
            if (x.Coedges.Any(c => Vec3.Distance(c.Start.P, p) < tol)) continue;
            var c = x.Coedges.FirstOrDefault(c => c.Edge.Curve is NLine l && Vec3.Distance(l.Eval(l.Param(p)), p) < tol
                && l.Param(p) > Math.Min(c.Edge.T0, c.Edge.T1) + tol && l.Param(p) < Math.Max(c.Edge.T0, c.Edge.T1) - tol);
            if (c == null) continue;
            var e = c.Edge; var tp = e.Curve.Param(p);
            var nv = new NVertex(p);
            var e1 = new NEdge(e.Curve, e.T0, tp, e.V0, nv); var e2 = new NEdge(e.Curve, tp, e.T1, nv, e.V1);
            foreach (var f in body.Faces)
                foreach (var l in f.Loops)
                    for (int k = 0; k < l.Coedges.Count; k++)
                    {
                        var u = l.Coedges[k];
                        if (!ReferenceEquals(u.Edge, e)) continue;
                        l.Coedges.RemoveAt(k);
                        l.Coedges.InsertRange(k, u.Forward ? new[] { new NCoedge(e1, true), new NCoedge(e2, true) } : new[] { new NCoedge(e2, false), new NCoedge(e1, false) });
                        k++;
                    }
            any = true;
        }
        if (any) body.Link();
        return any;
    }

    static bool IsBlendFace(NFace t)
    {
        var n = t.Tag.Name;
        return n.EndsWith(".Blend", StringComparison.Ordinal) || n.EndsWith(".S.F", StringComparison.Ordinal) || n.EndsWith(".R.F", StringComparison.Ordinal);
    }

    /// <summary>A planar end of a blend's tool (its square ends, a line tool's start and end).</summary>
    static bool IsCap(NFace t)
    {
        var n = t.Tag.Name;
        return t.Surface.Kind == SurfaceKind.Plane && (n.EndsWith(".End", StringComparison.Ordinal) || n.EndsWith(".Start", StringComparison.Ordinal)
            || n.EndsWith(".End0", StringComparison.Ordinal) || n.EndsWith(".End1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tool applied to the body (edited in place, or a new body after splitting faces at their seams); null and
    /// the reason when it cannot be (the body is then as it was).
    /// </summary>
    static NBody? Try(NBody body, Tool tool, out string? why)
    {
        why = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Lap(string what) { if (Prof.Enabled) Prof.Add("direct." + what, sw.Elapsed.TotalMilliseconds); sw.Restart(); }
        var tb = tool.Strip.Native;
        if (tb == null || tool.Strip.IsEmpty) { why = "no exact tool"; return null; }
        tb.Link();
        var box = tb.Bounds;
        var scale = Math.Max(1, box.Diagonal);
        var tol = 1e-7 * scale;
        // reaching into a box: by more than the padding of sampled bounds (1e-3)
        var pad = 2e-3 + tol;
        bool convex = tool.Convex;

        // ---- the tool's faces: on a face of the body near it (the strips, square ends), or blend faces
        var near = body.Faces.Where(f => f.Bounds().Overlaps(box, tol)).ToList();
        var onBody = new Dictionary<NFace, List<NFace>>(Ref);
        var blends = new List<NFace>();
        // (an end of the tool on no face of the body (a fillet stopping short, fading out, or squared off where it meets
        // nothing): a face of the result, as the boolean leaves it, put in between as the blend faces are)
        var caps = new List<NFace>();
        foreach (var t in tb.Faces)
        {
            var on = near.Where(f => f.Surface.CoincidentWith(t.Surface, 1e-9)).ToList();
            if (on.Count > 0) onBody[t] = on;
            else if (IsBlendFace(t)) blends.Add(t);
            else if (IsCap(t)) caps.Add(t);
            else { why = $"its face {t.Tag.Name} lies on no face of the body"; return null; }
        }
        if (blends.Count == 0 || onBody.Count == 0) { why = "not a blend's tool"; return null; }
        foreach (var t in caps)
            if (t.Coedges.Any(c => c.Edge.Uses.Count != 2 || c.Edge.Uses.Select(u => u.Face).Where(f => !ReferenceEquals(f, t)).Any(f => !onBody.ContainsKey(f) && !blends.Contains(f))))
            { why = $"its face {t.Tag.Name} lies on no face of the body"; return null; }
        var support = tb.Faces.Where(onBody.ContainsKey).ToList();
        var candidates = onBody.Values.SelectMany(x => x).Distinct(Ref).Cast<NFace>().ToList();

        Lap("faces");
        // ---- the tool's vertices on the body's (its chain vertices are the edge's own points)
        var bodyVerts = candidates.SelectMany(f => f.Coedges).Select(c => c.Start).Distinct(Ref).Cast<NVertex>().ToList();
        var toolVerts = tb.Faces.SelectMany(f => f.Coedges).Select(c => c.Start).Distinct(Ref).Cast<NVertex>().ToList();
        var canon = new Dictionary<NVertex, NVertex>(Ref);
        var taken = new HashSet<NVertex>(Ref);
        foreach (var v in toolVerts)
        {
            NVertex? best = null; double bd = tol;
            foreach (var w in bodyVerts) { var d = Vec3.Distance(v.P, w.P); if (d < bd) { bd = d; best = w; } }
            if (best != null && !taken.Add(best)) { why = "two of its vertices on one of the body's"; return null; }
            canon[v] = best ?? v;
        }
        NVertex Cv(NVertex v) => canon.TryGetValue(v, out var c) ? c : v;

        Lap("vertices");
        // ---- each side's vertices inside the other's edges split them: the body's edges where the contact curves
        // meet them (a switch's foot point, a square end's contact points), the tool's where it passes the body's
        // vertices (seam vertices of a rim the tool goes all the way round)
        var bodyEdges = candidates.SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct(Ref).Cast<NEdge>().ToList();
        var toolEdges = support.SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct(Ref).Cast<NEdge>().ToList();
        var splits = new Dictionary<NEdge, List<(double t, NVertex v)>>(Ref);
        void SplitAt(NEdge e, NVertex v)
        {
            if (!Inside(e, v.P, tol, out var t)) return;
            if (!splits.TryGetValue(e, out var l)) splits[e] = l = new();
            if (!l.Any(x => ReferenceEquals(x.v, v))) l.Add((t, v));
        }
        foreach (var v in toolVerts)
            if (ReferenceEquals(Cv(v), v))
                foreach (var e in bodyEdges) SplitAt(e, v);
        foreach (var w in bodyVerts)
            if (!taken.Contains(w))
                foreach (var e in toolEdges) SplitAt(e, w);

        var pieces = new Dictionary<NEdge, List<NEdge>>(Ref);
        List<NEdge> PiecesOf(NEdge e)
        {
            if (pieces.TryGetValue(e, out var l)) return l;
            var v0 = Cv(e.V0); var v1 = Cv(e.V1);
            l = new List<NEdge>();
            if (!splits.TryGetValue(e, out var sp))
                l.Add(ReferenceEquals(v0, e.V0) && ReferenceEquals(v1, e.V1) ? e : new NEdge(e.Curve, e.T0, e.T1, v0, v1));
            else
            {
                double pt = e.T0; var pv = v0;
                foreach (var (t, v) in sp.OrderBy(x => x.t))
                {
                    l.Add(new NEdge(e.Curve, pt, t, pv, v));
                    pt = t; pv = v;
                }
                l.Add(new NEdge(e.Curve, pt, e.T1, pv, v1));
            }
            return pieces[e] = l;
        }
        IEnumerable<(NEdge e, bool fwd)> Expand(NCoedge c)
        {
            var l = PiecesOf(c.Edge);
            if (c.Forward) foreach (var p in l) yield return (p, true);
            else for (int i = l.Count - 1; i >= 0; i--) yield return (l[i], false);
        }

        Lap("splits");
        var bodyPieceFaces = new Dictionary<NEdge, List<NFace>>(Ref);
        var same = new Dictionary<NEdge, (NEdge e, bool flip)>(Ref);
        (NEdge e, bool fwd) Map((NEdge e, bool fwd) x) => same.TryGetValue(x.e, out var m) ? (m.e, m.flip ? !x.fwd : x.fwd) : x;
        List<List<NFace>> groups = new();
        // twice at most: an edge between two of the body's faces on one surface (a seam) that the tool's strip
        // covers part of is split where it leaves the strip, then everything again with those pieces
        for (int pass = 0; ; pass++)
        {
            pieces.Clear(); bodyPieceFaces.Clear(); same.Clear();
            // ---- the tool's pieces along the body's edges become those edges (where they cancel)
            var byEnds = new Dictionary<(NVertex, NVertex), List<NEdge>>();
            (NVertex, NVertex) Key(NEdge e) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.V0) <= System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.V1) ? (e.V0, e.V1) : (e.V1, e.V0);
            foreach (var f in candidates)
                foreach (var c in f.Coedges)
                    foreach (var (p, _) in Expand(c))
                    {
                        if (!bodyPieceFaces.TryGetValue(p, out var fl))
                        {
                            bodyPieceFaces[p] = fl = new();
                            var k = Key(p);
                            if (!byEnds.TryGetValue(k, out var bl)) byEnds[k] = bl = new();
                            bl.Add(p);
                        }
                        if (!fl.Contains(f)) fl.Add(f);
                    }
            foreach (var e in toolEdges)
                foreach (var p in PiecesOf(e))
                    if (byEnds.TryGetValue(Key(p), out var bl))
                        foreach (var q in bl)
                            if (Coincide(p, q, tol))
                            {
                                bool flip = p.Closed ? Vec3.Dot(p.Curve.Deriv(p.T0), q.Curve.Deriv(q.T0)) < 0 : !ReferenceEquals(p.V0, q.V0);
                                same[p] = (q, flip);
                                break;
                            }

            // ---- groups: the tool's faces on a surface and the body's faces they share edges with
            var nodes = new List<NFace>(support); nodes.AddRange(candidates);
            var index = new Dictionary<NFace, int>(Ref);
            for (int i = 0; i < nodes.Count; i++) index[nodes[i]] = i;
            var parent = Enumerable.Range(0, nodes.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Join(NFace a, NFace b) => parent[Find(index[a])] = Find(index[b]);
            var toolEdgeFaces = new Dictionary<NEdge, NFace>(Ref);
            foreach (var t in support)
                foreach (var c in t.Coedges)
                {
                    if (toolEdgeFaces.TryGetValue(c.Edge, out var o) && o.Surface.CoincidentWith(t.Surface, 1e-9)) Join(t, o);
                    toolEdgeFaces[c.Edge] = t;
                    foreach (var x in Expand(c).Select(Map))
                        if (bodyPieceFaces.TryGetValue(x.e, out var fl))
                            foreach (var f in fl)
                                if (onBody[t].Contains(f)) Join(t, f);
                }
            groups = nodes.GroupBy(n => Find(index[n])).Select(g => g.ToList()).Where(g => g.Any(onBody.ContainsKey)).ToList();
            if (pass > 0) break;

            // ---- edges between two of a group's faces (a seam across the strip): split where they leave it
            bool more = false;
            foreach (var g in groups)
            {
                var gt = g.Where(onBody.ContainsKey).ToList();
                var gb = g.Where(n => !onBody.ContainsKey(n)).ToList();
                if (gb.Count < 2) continue;
                var bound = gt.SelectMany(t => t.Coedges).Select(c => c.Edge).Distinct(Ref).Cast<NEdge>()
                    .Where(e => !PiecesOf(e).Any(same.ContainsKey)).ToList();
                foreach (var e in gb.SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct(Ref).Cast<NEdge>().ToList())
                {
                    if (e.Uses.Count != 2 || !gb.Contains(e.Uses[0].Face) || !gb.Contains(e.Uses[1].Face)) continue;
                    foreach (var x in Leaves(e, gt, bound, tool.Size / 8, tol))
                    {
                        var v = new NVertex(x.p);
                        SplitAt(e, v);
                        SplitAt(x.c, v);
                        more = true;
                    }
                }
            }
            if (!more) break;
        }

        Lap("groups");
        // ---- each group: the body's faces' boundary and the tool's (reversed for a convex blend), shared edges
        // cancelling, traced into loops; with several faces of the body, each keeps its own (the parts of the edges
        // between them that the strip covers dropped, the tool's edges given to the face they lie in)
        var newFaces = new List<NFace>();
        var removed = new HashSet<NFace>(Ref);
        foreach (var g in groups)
        {
            var gt = g.Where(onBody.ContainsKey).ToList();
            var gb = g.Where(n => !onBody.ContainsKey(n)).ToList();
            if (gb.Count == 0) { why = $"its face {gt[0].Tag.Name} shares no edge with the body"; return null; }
            var toolCo = new List<(NEdge e, bool fwd)>();
            foreach (var t in gt)
                foreach (var c in t.Coedges)
                    foreach (var x in Expand(c).Select(Map)) toolCo.Add((x.e, convex ? !x.fwd : x.fwd));
            var gtDoms = gt.Select(t => (t, d: NDomain.Cached(t, 1e-3))).ToList();
            // several faces of the body: each edge decided by the side of it the face and the tool lie on (a point
            // just beside its middle), not by cancelling (the tool's own seams may lie along the body's)
            bool InT(Vec3 p) => gtDoms.Any(x => x.d.Classify(x.t.Surface.Project(p), tol) > 0);
            // well clear of the boundary sampling (1e-3: nearer, a domain resamples the boundary finely), well inside
            // the strip (the blend's size wide)
            var delta = Math.Min(Math.Max(4e-3, 0.05 * tool.Size), 0.25 * tool.Size);
            Vec3 Beside(NEdge e, bool fwd, NFace face, bool left)
            {
                var tm = 0.5 * (e.T0 + e.T1);
                var m = e.Curve.Eval(tm);
                var tan = (e.Curve.Deriv(tm) * (fwd ? 1 : -1)).Normalized;
                var d = Vec3.Cross(face.Surface.Normal(m) * (face.Sense ? 1 : -1), tan).Normalized;
                return m + d * (left ? delta : -delta);
            }
            // a concave blend's end on a face it meets (the same side out) adds to the face; everything else takes away
            static Vec3 Out(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);
            var probe = gt[0].Coedges.First().Edge.Mid;
            bool union = !convex && Vec3.Dot(Out(gt[0], probe), Out(gb[0], probe)) > 0;
            foreach (var F in gb)
            {
                var chain = new List<(NEdge e, bool fwd, bool tool)>();
                if (gb.Count == 1)
                {
                    foreach (var c in F.Coedges) foreach (var x in Expand(c)) chain.Add((x.e, x.fwd, false));
                    foreach (var x in toolCo) chain.Add((x.e, x.fwd, true));
                }
                else
                {
                    var fd = NDomain.Cached(F, 1e-3);
                    bool InF(Vec3 p) => fd.Classify(F.Surface.Project(p), tol) > 0;
                    // the face's own edges (the face on their left): kept where the other side is not the tool
                    // (subtracting: where the face's side is not the tool)
                    foreach (var c in F.Coedges)
                        foreach (var x in Expand(c))
                            if (!InT(Beside(x.e, x.fwd, F, left: !union))) chain.Add((x.e, x.fwd, false));
                    // the tool's edges, oriented with the tool on their left as seen from the face's side
                    foreach (var t in gt)
                    {
                        foreach (var c in t.Coedges)
                            foreach (var x0 in Expand(c).Select(Map))
                            {
                                var agree = Vec3.Dot(Out(t, x0.e.Mid), Out(F, x0.e.Mid)) > 0;
                                (NEdge e, bool fwd) x = agree ? x0 : (x0.e, !x0.fwd);
                                var right = Beside(x.e, x.fwd, F, left: false);
                                if (InT(right)) continue;
                                if (union) { if (!InF(right)) chain.Add((x.e, x.fwd, true)); }
                                else if (InF(right)) chain.Add((x.e, !x.fwd, true));
                            }
                    }
                }
                var net = new Dictionary<NEdge, int>(Ref);
                foreach (var x in chain) net[x.e] = net.GetValueOrDefault(x.e) + (x.fwd ? 1 : -1);
                var kept = new List<(NEdge e, bool fwd, bool tool)>();
                var seen = new HashSet<NEdge>(Ref);
                foreach (var x in chain)
                {
                    var n = net[x.e];
                    if (n == 0 || !seen.Add(x.e)) continue;
                    if (Math.Abs(n) > 1) { why = $"an edge of {F.Tag.Name} twice in one direction"; return null; }
                    kept.Add((x.e, n > 0, x.tool));
                }
                var loops = Trace(kept.Select(x => (x.e, x.fwd)).ToList());
                if (loops == null || loops.Count == 0) { why = $"the boundary of {F.Tag.Name} does not close into loops"; return null; }

                // other geometry of this face close to the blend: the boolean decides
                var toolBox = Box3.Empty;
                foreach (var t in gt) toolBox.Add(t.Bounds());
                // (one ending on the tool's boundary too: a barrel coming out through a face the blended corner is on, its
                // curve through the strip. Inside by more than the boundary's sampling: an edge the strip was cut back
                // to lies along it)
                var margin = Math.Max(10 * pad, 0.02 * tool.Size);
                foreach (var x in kept)
                {
                    // (not shrunk: a face square to an axis has no thickness along it)
                    if (x.tool || !x.e.Bounds().Overlaps(toolBox, tol)) continue;
                    if (gt.Any(t => Crosses(x.e, t, tool.Size / 4, margin)))
                    {
                        why = $"other edges of {F.Tag.Name} run into the blend"; return null;
                    }
                }

                var nf = new NFace(F.Surface, F.Sense, F.Tag, F.Anchor);
                foreach (var l in loops)
                {
                    var nl = new NLoop();
                    foreach (var (e, fwd) in l) nl.Coedges.Add(new NCoedge(e, fwd));
                    nf.Loops.Add(nl);
                }
                newFaces.Add(nf);
                removed.Add(F);
            }
        }

        Lap("loops");
        // ---- other faces of the body reaching into the blend: the boolean decides. A face can only get into the
        // region the tool replaces through the blend faces (through the strips it would meet the blended faces along
        // an edge, which the check above finds) or by lying wholly inside it
        foreach (var f in near)
            if (!removed.Contains(f) && f.Bounds().Overlaps(box, -pad) && Intrudes(f, tb, blends.Concat(caps).ToList(), tol, pad))
            {
                why = $"the face {f.Tag.Name} runs into the blend"; return null;
            }

        Lap("intrude");
        // ---- faces using a split edge of the body: the pieces instead
        var touched = new List<NFace>();
        foreach (var e in splits.Keys)
            foreach (var u in e.Uses)
                if (!removed.Contains(u.Face) && !touched.Contains(u.Face) && body.Faces.Contains(u.Face)) touched.Add(u.Face);
        foreach (var f in touched)
        {
            var nf = new NFace(f.Surface, f.Sense, f.Tag, f.Anchor);
            foreach (var l in f.Loops)
            {
                var nl = new NLoop();
                foreach (var c in l.Coedges) foreach (var (e, fwd) in Expand(c)) nl.Coedges.Add(new NCoedge(e, fwd));
                nf.Loops.Add(nl);
            }
            newFaces.Add(nf);
            removed.Add(f);
        }

        Lap("touched");
        // ---- the blend faces (turned inside out for a convex blend: the tool's outside is the body's)
        foreach (var t in blends.Concat(caps))
        {
            var nf = new NFace(t.Surface, convex ? !t.Sense : t.Sense, t.Tag, t.Anchor);
            foreach (var l in t.Loops)
            {
                var co = l.Coedges.SelectMany(Expand).Select(Map).ToList();
                if (convex) { co.Reverse(); co = co.Select(x => (x.e, !x.fwd)).ToList(); }
                var nl = new NLoop();
                foreach (var (e, fwd) in co) nl.Coedges.Add(new NCoedge(e, fwd));
                nf.Loops.Add(nl);
            }
            newFaces.Add(nf);
        }

        Lap("blendfaces");
        // ---- in place, checked; undone if invalid
        var before = body.Faces.ToList();
        body.Faces.RemoveAll(removed.Contains);
        body.Faces.AddRange(newFaces);
        var err = body.Validate(newFaces, null);
        Lap("validate");
        if (err != null)
        {
            body.Faces.Clear(); body.Faces.AddRange(before); body.Link();
            why = "result invalid: " + err;
            return null;
        }
        return body;
    }

    /// <summary>Whether p lies on e strictly between its ends (the curve parameter there in t).</summary>
    static bool Inside(NEdge e, Vec3 p, double tol, out double t)
    {
        t = 0;
        // (a fitted curve: within its fit tolerance, as Coincide)
        if (e.Curve is NSpline ns) tol = Math.Max(tol, 10 * ns.FitTol);
        if (!e.Bounds().Contains(p, tol)) return false;
        if (Vec3.Distance(p, e.V0.P) <= tol || Vec3.Distance(p, e.V1.P) <= tol) return false;
        t = e.Curve.Param(p, e.Curve.Periodic ? 0.5 * (e.T0 + e.T1) : null);
        if (e.Curve.Periodic)
        {
            var per = e.Curve.Period;
            while (t < e.T0) t += per;
            while (t > e.T1) t -= per;
        }
        if (t <= e.T0 || t >= e.T1) return false;
        return Vec3.Distance(e.Curve.Eval(t), p) <= tol;
    }

    /// <summary>
    /// Where edge e (on the surface of the faces <paramref name="strips"/>) passes from inside one of them to outside,
    /// and the edge of theirs (of <paramref name="bound"/>) it crosses there: sampled every step, the crossing found by
    /// bisection, then on both curves by alternate projection.
    /// </summary>
    static List<(Vec3 p, NEdge c)> Leaves(NEdge e, List<NFace> strips, List<NEdge> bound, double step, double tol)
    {
        var res = new List<(Vec3, NEdge)>();
        if (!strips.Any(t => e.Bounds().Overlaps(t.Bounds(), tol))) return res;
        var doms = strips.Select(t => (t, d: NDomain.Cached(t, 1e-3), b: t.Bounds())).ToList();
        bool In(double s) { var p = e.At(s); return doms.Any(x => x.b.Contains(p, tol) && x.d.Classify(x.t.Surface.Project(p), tol) > 0); }
        int n = (int)Math.Clamp(Math.Ceiling(e.Length / Math.Max(step, 1e-9)), 8, 4096);
        bool prev = In(0);
        for (int i = 1; i <= n; i++)
        {
            double s1 = (double)i / n;
            bool cur = In(s1);
            if (cur != prev)
            {
                double lo = (double)(i - 1) / n, hi = s1;
                for (int k = 0; k < 50; k++) { var m = 0.5 * (lo + hi); if (In(m) == prev) lo = m; else hi = m; }
                var p = e.At(0.5 * (lo + hi));
                NEdge? best = null; double bd = double.MaxValue;
                foreach (var c in bound)
                {
                    var q = c.Curve.Eval(c.Curve.Param(p, c.Curve.Periodic ? 0.5 * (c.T0 + c.T1) : null));
                    var d = Vec3.Distance(p, q);
                    if (d < bd) { bd = d; best = c; }
                }
                if (best != null)
                {
                    for (int k = 0; k < 20; k++)
                    {
                        var q = best.Curve.Eval(best.Curve.Param(p, best.Curve.Periodic ? 0.5 * (best.T0 + best.T1) : null));
                        var p2 = e.Curve.Eval(e.Curve.Param(q, e.Curve.Periodic ? 0.5 * (e.T0 + e.T1) : null));
                        bool done = Vec3.Distance(p2, p) < 1e-15 * Math.Max(1, p.Length);
                        p = p2;
                        if (done) break;
                    }
                    res.Add((p, best));
                }
            }
            prev = cur;
        }
        return res;
    }

    /// <summary>Whether the edge passes through the inside of face t (on the same surface), sampled every step.</summary>
    static bool Crosses(NEdge e, NFace t, double step, double tol)
    {
        if (!e.Bounds().Overlaps(t.Bounds(), tol)) return false;
        var dom = NDomain.Cached(t, 1e-3);
        int n = (int)Math.Clamp(Math.Ceiling(e.Length / Math.Max(step, 1e-9)), 8, 4096);
        for (int i = 0; i <= n; i++)
        {
            var p = e.At((double)i / n);
            if (dom.Classify(t.Surface.Project(p), tol) > 0) return true;
        }
        return false;
    }

    /// <summary>Whether face f crosses one of the tool's blend faces (inside both), or lies inside the tool.</summary>
    static bool Intrudes(NFace f, NBody tool, List<NFace> blends, double tol, double pad)
    {
        var fd = NDomain.Cached(f, 1e-3);
        foreach (var bf in blends)
        {
            if (!bf.Bounds().Overlaps(f.Bounds(), -pad)) continue;
            var bd = NDomain.Cached(bf, 1e-3);
            var bb = bf.Bounds();
            foreach (var p in Meet(bf, f.Surface, bb, tol, bd))
                if (bd.Classify(bf.Surface.Project(p), tol) > 0 && fd.Classify(f.Surface.Project(p), tol) >= 0) return true;
        }
        var ip = fd.InteriorPoint();
        return tool.Bounds.Contains(ip, -pad) && NativeBlend.PointInside(tool, ip);
    }

    /// <summary>
    /// Points of the intersection of face bf's surface with surface s near bf (box bb): the closed form where there is
    /// one (lines sampled across the box, circles and ellipses all round), else traced on bf.
    /// </summary>
    static IEnumerable<Vec3> Meet(NFace bf, NSurface s, Box3 bb, double tol, NDomain bd)
    {
        List<NCurve>? exact = null;
        try { exact = NIntersect.Analytic(bf.Surface, s, tol); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        if (exact != null && exact.All(c => c is NLine or NCircle or NEllipse or NPolyline))
        {
            foreach (var c in exact)
            {
                if (c is NPolyline pl) { foreach (var p in pl.Pts) yield return p; continue; }
                double t0, t1;
                if (c.Periodic) { t0 = 0; t1 = c.Period; }
                else
                {
                    t0 = double.MaxValue; t1 = double.MinValue;
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vec3((k & 1) == 0 ? bb.Min.X : bb.Max.X, (k & 2) == 0 ? bb.Min.Y : bb.Max.Y, (k & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                        var t = c.Param(corner);
                        t0 = Math.Min(t0, t); t1 = Math.Max(t1, t);
                    }
                }
                for (int i = 0; i <= 128; i++)
                {
                    var p = c.Eval(t0 + (t1 - t0) * i / 128);
                    if (bb.Contains(p, tol)) yield return p;
                }
            }
            yield break;
        }
        foreach (var pl in NIntersect.Trace(bf, s, Math.Max(tol, 1e-4), bd))
            foreach (var p in pl.Pts) yield return p;
    }

    /// <summary>
    /// Whether two edges with the same end vertices are the same curve between them (within the fit tolerance of
    /// fitted curves: a pattern copy's edge and the instance's own are fitted apart).
    /// </summary>
    static bool Coincide(NEdge a, NEdge b, double tol)
    {
        static double Fit(NCurve c) => c is NSpline ns ? ns.FitTol : 0;
        var near = Math.Max(10 * tol, 10 * (Fit(a.Curve) + Fit(b.Curve)));
        foreach (var s in new[] { 0.25, 0.5, 0.75 })
        {
            var p = a.At(s);
            var t = b.Curve.Param(p, b.Curve.Periodic ? 0.5 * (b.T0 + b.T1) : null);
            if (b.Curve.Periodic)
            {
                var per = b.Curve.Period;
                while (t < b.T0) t += per;
                while (t > b.T1) t -= per;
            }
            if (t < b.T0 - 1e-9 || t > b.T1 + 1e-9 || Vec3.Distance(b.Curve.Eval(t), p) > near) return false;
        }
        return true;
    }

    /// <summary>
    /// Closed loops of oriented edges, each continuing at the vertex the last one ends at; null when an end has no
    /// continuation or more than one (an ambiguous touch).
    /// </summary>
    static List<List<(NEdge e, bool fwd)>>? Trace(List<(NEdge e, bool fwd)> co)
    {
        static NVertex S((NEdge e, bool fwd) x) => x.fwd ? x.e.V0 : x.e.V1;
        static NVertex E((NEdge e, bool fwd) x) => x.fwd ? x.e.V1 : x.e.V0;
        var from = new Dictionary<NVertex, List<int>>(Ref);
        for (int i = 0; i < co.Count; i++)
        {
            if (!from.TryGetValue(S(co[i]), out var l)) from[S(co[i])] = l = new();
            l.Add(i);
        }
        if (from.Values.Any(l => l.Count > 1)) return null;
        var used = new bool[co.Count];
        var loops = new List<List<(NEdge, bool)>>();
        for (int i = 0; i < co.Count; i++)
        {
            if (used[i]) continue;
            var loop = new List<(NEdge, bool)>();
            int k = i;
            while (!used[k])
            {
                used[k] = true; loop.Add(co[k]);
                if (!from.TryGetValue(E(co[k]), out var nx)) return null;
                k = nx[0];
            }
            if (k != i) return null;
            loops.Add(loop);
        }
        return loops;
    }
}

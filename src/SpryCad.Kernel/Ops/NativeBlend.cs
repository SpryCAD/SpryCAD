using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Fillets and chamfers on native B-rep edges: tools are built from the exact edge curve and the adjacent
/// faces' surfaces (no tessellation), then applied straight to the faces they blend (<see cref="DirectBlend"/>), or
/// where that cannot be (corners where blends meet, ends at an angle, blends trimmed by neighbouring geometry)
/// subtracted (convex) or added (concave) with native booleans.
/// Lines between planes and arcs between a plane and a perpendicular cylinder use extruded/revolved profiles;
/// fillets on any other pair of faces use rolling-ball tools (<see cref="RollingBall"/>), chamfers the chamfer's
/// cross-section swept along the edges' tangent chain (<see cref="SweptChamfer"/>).
/// </summary>
public static class NativeBlend
{
    /// <summary>
    /// Applies the blend. Edges whose blend runs past the faces it should end on are, with
    /// <paramref name="overflow"/>, trimmed where they meet the neighbouring geometry ("keep edge" overflow);
    /// otherwise they fail. Failures are reported in <paramref name="errors"/>, their edges (as polylines) in
    /// <paramref name="problemEdges"/>.
    /// </summary>
    public static Solid Apply(Solid body, IReadOnlyList<NEdge> edges, double size, bool fillet, string prefix, List<string> errors,
        bool overflow = true, List<Vec3[]>? problemEdges = null, List<(Solid tool, bool cut)>? applied = null, ChamferSize? chamfer = null,
        bool buildOnly = false, System.Runtime.CompilerServices.StrongBox<bool>? trimmedOut = null)
    {
        if (size <= 0) { errors.Add("Size must be greater than zero."); return body; }
        // blends of both kinds meeting at a vertex (a hex socket's inside corners, concave, and its rim, convex): the odd
        // one out at each such vertex first, then the others on the body it leaves, running on round what it blended
        // (as two features would). At once, their tools can collide where they meet
        // (only where all at once fails: where it works, a corner of three blends gets its patch, as before)
        if (!buildOnly && Mixed(body.Native!, edges) is { } first)
        {
            var errs = new List<string>(); var probs = problemEdges == null ? null : new List<Vec3[]>(); var app = applied == null ? null : new List<(Solid, bool)>();
            // (all at once only where it may do better: three or more blends of one kind at a vertex get a corner of
            // their own there; else it fails where the kinds meet, and only costs time)
            var once = Corner3(edges) ? ApplyOne(body, edges, size, fillet, prefix, errs, overflow, probs, app, chamfer, false, trimmedOut) : body;
            if (!Corner3(edges)) errs.Add("staged");
            if (errs.Count == 0)
            {
                if (probs != null) problemEdges!.AddRange(probs);
                if (app != null) applied!.AddRange(app);
                return once;
            }
            var nb1 = Apply(body, first, size, fillet, prefix + ".c", errors, overflow, problemEdges, applied, chamfer, false, trimmedOut);
            if (ReferenceEquals(nb1, body) || nb1.Native == null) return nb1;
            var rest = Rest(body.Native!, nb1.Native, edges.Except(first).ToList(), prefix + ".c", size);
            Debug?.Invoke($"mixed blends: {first.Count} first, then {rest.Count} edges");
            return rest.Count == 0 ? nb1 : Apply(nb1, rest, size, fillet, prefix, errors, overflow, problemEdges, applied, chamfer, false, trimmedOut);
        }
        // (the ball pivoting round convex corners: where that leaves errors, the fillets mitred there instead, as before,
        // when that does better)
        var errs1 = new List<string>(); var probs1 = problemEdges == null ? null : new List<Vec3[]>(); var app1 = applied == null ? null : new List<(Solid, bool)>();
        int used = _pivotsUsed;
        var failedBefore = _pivotFailed;
        _pivotFailed = false;
        Solid r1;
        bool pivotFailed;
        try { r1 = ApplyOne(body, edges, size, fillet, prefix, errs1, overflow, probs1, app1, chamfer, buildOnly, trimmedOut); }
        finally { pivotFailed = _pivotFailed; _pivotFailed = failedBefore; }
        if ((errs1.Count > 0 || pivotFailed) && _pivotsUsed > used && !_noPivots)
        {
            var errs2 = new List<string>(); var probs2 = problemEdges == null ? null : new List<Vec3[]>(); var app2 = applied == null ? null : new List<(Solid, bool)>();
            _noPivots = true;
            Solid r2;
            try { r2 = ApplyOne(body, edges, size, fillet, prefix, errs2, overflow, probs2, app2, chamfer, buildOnly, trimmedOut); }
            finally { _noPivots = false; }
            Debug?.Invoke($"pivots: {errs1.Count} error(s) ({string.Join("; ", errs1)}); mitred: {errs2.Count}");
            // (a pivot not glued: that run is incomplete)
            if (pivotFailed || errs2.Count < errs1.Count) (r1, errs1, probs1, app1) = (r2, errs2, probs2, app2);
        }
        errors.AddRange(errs1);
        if (probs1 != null) problemEdges!.AddRange(probs1);
        if (app1 != null) applied!.AddRange(app1);
        return r1;
    }

    // (per thread: pattern instances are built in parallel)
    [ThreadStatic] static int _pivotsUsed;
    [ThreadStatic] static bool _pivotFailed;
    [ThreadStatic] static bool _noPivots;

    /// <summary>Whether three or more of the edges, of one kind (convex or concave), meet at a vertex.</summary>
    static bool Corner3(IReadOnlyList<NEdge> edges)
    {
        var at = new Dictionary<(NVertex, bool?), int>();
        foreach (var e in edges)
        {
            var k = Convex(e, e.Curve.Eval(0.5 * (e.T0 + e.T1)));
            foreach (var v in ReferenceEquals(e.V0, e.V1) ? new[] { e.V0 } : new[] { e.V0, e.V1 })
                if ((at[(v, k)] = at.GetValueOrDefault((v, k)) + 1) >= 3) return true;
        }
        return false;
    }

    /// <summary>
    /// The edges to blend first where selected edges of both kinds (convex, concave) meet at a vertex: at each such
    /// vertex those of the kind fewer edges there have (concave on a tie). Null when there is no such vertex.
    /// </summary>
    static List<NEdge>? Mixed(NBody body, IReadOnlyList<NEdge> edges)
    {
        var kind = new Dictionary<NEdge, bool?>(ReferenceEqualityComparer.Instance);
        foreach (var e in edges) kind[e] = e.Curve.Periodic && ReferenceEquals(e.V0, e.V1) ? null : Convex(e, e.Curve.Eval(0.5 * (e.T0 + e.T1)));
        var at = new Dictionary<NVertex, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in edges)
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!at.TryGetValue(v, out var l)) at[v] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
        var first = new List<NEdge>();
        foreach (var l in at.Values)
        {
            var cv = l.Where(e => kind[e] == true).ToList(); var cc = l.Where(e => kind[e] == false).ToList();
            if (cv.Count == 0 || cc.Count == 0) continue;
            foreach (var e in cv.Count < cc.Count ? cv : cc) if (!first.Contains(e)) first.Add(e);
        }
        return first.Count == 0 || first.Count == edges.Count ? null : first;
    }

    /// <summary>
    /// The edges still to blend on the body the first blends left: each one's remains (between the same faces,
    /// nearest its middle), and the new edges between their faces and the first blends' faces, near where they
    /// met (a fillet's arcs round a rounded corner, a chamfer's short lines across it).
    /// </summary>
    static List<NEdge> Rest(NBody before, NBody after, List<NEdge> edges, string firstPrefix, double size)
    {
        after.Link();
        var res = new List<NEdge>();
        static bool Between(NEdge e, string a, string b)
        {
            if (e.Uses.Count != 2) return false;
            var x = e.Uses[0].Face.Tag.Name; var y = e.Uses[1].Face.Tag.Name;
            return x == a && y == b || x == b && y == a;
        }
        foreach (var e in edges)
        {
            var a = e.Uses[0].Face.Tag.Name; var b = e.Uses[1].Face.Tag.Name;
            var mid = e.Curve.Eval(0.5 * (e.T0 + e.T1));
            var best = after.Edges.Where(x => Between(x, a, b)).OrderBy(x => x.Samples(1e-3).Min(q => Vec3.Distance(q, mid))).FirstOrDefault();
            if (best != null && best.Samples(1e-3).Min(q => Vec3.Distance(q, mid)) < Math.Max(1e-3, 0.5 * Vec3.Distance(e.V0.P, e.V1.P)) && !res.Contains(best)) res.Add(best);
        }
        // the remains' faces against the first blends' faces, near the vertices they shared
        var names = new HashSet<string>(edges.SelectMany(e => e.Uses.Select(u => u.Face.Tag.Name)));
        var ends = edges.SelectMany(e => new[] { e.V0.P, e.V1.P }).ToList();
        foreach (var x in after.Edges)
        {
            if (x.Uses.Count != 2 || res.Contains(x)) continue;
            var n0 = x.Uses[0].Face.Tag.Name; var n1 = x.Uses[1].Face.Tag.Name;
            bool blend0 = n0.StartsWith(firstPrefix + ".", StringComparison.Ordinal), blend1 = n1.StartsWith(firstPrefix + ".", StringComparison.Ordinal);
            if (!(blend0 && names.Contains(n1) || blend1 && names.Contains(n0))) continue;
            var m = x.Curve.Eval(0.5 * (x.T0 + x.T1));
            if (ends.Any(p => Vec3.Distance(p, m) < 3 * size)) res.Add(x);
        }
        return res;
    }

    static Solid ApplyOne(Solid body, IReadOnlyList<NEdge> edges, double size, bool fillet, string prefix, List<string> errors,
        bool overflow, List<Vec3[]>? problemEdges, List<(Solid tool, bool cut)>? applied, ChamferSize? chamfer,
        bool buildOnly, System.Runtime.CompilerServices.StrongBox<bool>? trimmedOut)
    {
        // (the corners' pivots, and which tool carries each, for this run only)
        var saved = (_pivoted, _patches);
        _pivoted = new HashSet<NVertex>(ReferenceEqualityComparer.Instance);
        _patches = new Dictionary<NVertex, RoundCornerCut.Built>(ReferenceEqualityComparer.Instance);
        try { return ApplyRun(body, edges, size, fillet, prefix, errors, overflow, problemEdges, applied, chamfer, buildOnly, trimmedOut); }
        finally { (_pivoted, _patches) = saved; }
    }

    [ThreadStatic] static HashSet<NVertex>? _pivoted;
    [ThreadStatic] static Dictionary<NVertex, RoundCornerCut.Built>? _patches;

    static Solid ApplyRun(Solid body, IReadOnlyList<NEdge> edges, double size, bool fillet, string prefix, List<string> errors,
        bool overflow, List<Vec3[]>? problemEdges, List<(Solid tool, bool cut)>? applied, ChamferSize? chamfer,
        bool buildOnly, System.Runtime.CompilerServices.StrongBox<bool>? trimmedOut)
    {
        body.Native!.Link();
        var ch = chamfer ?? ChamferSize.Equal(size);
        // an unequal chamfer is measured on the same face all along a chain (e.g. the top face around a box):
        // the reference is the first edge's face (the other one when flipped), carried to edges sharing it
        NFace? refA = null, refB = null;
        if (edges.Count > 0) { var (f0, f1) = Faces(edges[0]); (refA, refB) = ch.Flip ? (f1, f0) : (f0, f1); }
        static bool Like(NFace f, NFace? g) => g != null && (ReferenceEquals(f, g) || f.Surface.CoincidentWith(g.Surface, 1e-9));
        bool RefIsFirst(NFace x, NFace y) => Like(x, refA) || (!Like(y, refA) && (Like(y, refB) || !Like(x, refB) && !ch.Flip));
        // chamfer legs along faces x and y, running along ux / uy from the edge
        (double, double) Legs(NFace x, NFace y, Vec3 ux, Vec3 uy)
        {
            var (r, o) = ch.Legs(Math.Acos(Math.Clamp(Vec3.Dot(ux, uy), -1, 1)));
            return RefIsFirst(x, y) ? (r, o) : (o, r);
        }
        var cutTools = new List<Solid>();
        var addTools = new List<Solid>();
        var done = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        bool anyTrimmed = false;
        var trimmedEdges = new List<NEdge>();
        var failedChains = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        // per edge (group): its tools for the boolean, and the tool a direct blend applies (null: none)
        var entries = new List<(List<Solid> tools, bool convex, Solid? strip, string name, List<NEdge> group, bool trimmed)>();
        // failures, reported once the chains below have had their second try
        var fails = new List<(List<NEdge> group, string msg)>();
        bool direct = DirectBlend.Enabled && !buildOnly;
        int i = 0;
        foreach (var e in edges)
        {
            if (!done.Add(e)) continue;
            var name = $"{prefix}.{i++}";
            var (fa, fb) = Faces(e);
            var group = new List<NEdge> { e };
            if (e.Curve is NCircle c)
                // arcs of one circle between the same faces form one rim (bodies split full circles)
                foreach (var o in edges)
                    if (!done.Contains(o) && o.Curve is NCircle oc && SameCircle(c, oc) && SameFaces(e, o)) { group.Add(o); done.Add(o); }
            void Fail(string msg) => fails.Add((group, $"Edge {fa.Tag.Name} / {fb.Tag.Name}: {msg}"));
            try
            {
                List<Solid>? tools; bool convex, fits; Solid? strip;
                using (Prof.Time("blend.tools")) (tools, convex, fits, strip) = ForceRollingBall ? (null, false, false, null)
                    : e.Curve is NCircle ? ArcTools(group, size, fillet, name, ch, RefIsFirst, direct, body.Native!, edges) : LineTool(e, size, fillet, name, ch, RefIsFirst, direct, body.Native!, edges);
                if (tools == null && fillet)
                {
                    // any other pair of faces: rolling ball along the chain of edges this edge belongs to; first across
                    // faces meeting tangentially (one tool, the ball rolls from face to face), else between the same
                    // two surfaces only (separate tools, trimmed where they run past their faces)
                    RollingBall.Result? rb = null;
                    var tangent = TangentChain(e, edges, done);
                    // a tangent chain that failed as one tool is not tried again from each of its other edges
                    if (tangent.Count > Chain(e, edges).Count && !tangent.Any(failedChains.Contains))
                    {
                        var r2 = Ball(tangent, size, name, edges);
                        Debug?.Invoke($"tangent chain {name}: {tangent.Count} edges -> {r2.Error ?? (r2.Fits ? "fits" : "overflows")}");
                        if (r2.Error == null) { rb = r2; foreach (var o in tangent) if (!group.Contains(o)) { group.Add(o); done.Add(o); } }
                        else foreach (var o in tangent) failedChains.Add(o);
                    }
                    if (rb == null)
                    {
                        foreach (var o in Chain(e, edges)) if (!group.Contains(o)) { group.Add(o); done.Add(o); }
                        rb = Ball(group, size, name, edges);
                    }
                    if (rb.Error != null) { Fail(rb.Error); continue; }
                    // the rolling-ball tool's sides lie on the faces it blends: a direct blend applies it as it is
                    (tools, convex, fits, strip) = (new List<Solid> { rb.Tool! }, rb.Convex, rb.Fits, rb.Tool);
                }
                if (tools == null && !fillet)
                {
                    // any other pair of faces: the chamfer swept along the tangent chain this edge belongs to, its
                    // lines and arcs with exact tools of their own included (one tool: no joints meeting face to face)
                    var chain = TangentChain(e, edges, new HashSet<NEdge>(ReferenceEqualityComparer.Instance));
                    var set = new HashSet<NEdge>(chain, ReferenceEqualityComparer.Instance);
                    bool Part(List<NEdge> g) => g.Any(set.Contains) && !g.All(set.Contains);
                    if (entries.Any(x => Part(x.group)) || fails.Any(x => Part(x.group))) chain = TangentChain(e, edges, done);
                    var sc = SweptChamfer.Build(chain, name, Legs, OnFaces);
                    if (sc.Error != null && chain.Count > 1)
                    {
                        Debug?.Invoke($"swept chamfer {name}: {chain.Count} edges -> {sc.Error}");
                        sc = SweptChamfer.Build(chain = new List<NEdge>(group), name, Legs, OnFaces);
                    }
                    Debug?.Invoke($"swept chamfer {name}: {chain.Count} edges -> {sc.Error ?? (sc.Fits ? "fits" : "overflows")}");
                    if (sc.Error != null) { Fail(sc.Error); continue; }
                    set = new HashSet<NEdge>(chain, ReferenceEqualityComparer.Instance);
                    entries.RemoveAll(x => x.group.All(set.Contains));
                    fails.RemoveAll(x => x.group.All(set.Contains));
                    foreach (var o in chain) if (!group.Contains(o)) { group.Add(o); done.Add(o); }
                    // its reach past the corner and the legs' ends, as the line and arc tools': where that space is taken (a
                    // wall across a narrow gap), the strip bounded by the faces for the boolean too
                    var tool = sc.Strip != null && sc.Reach != null && Taken(body.Native!, fa, fb, sc.Reach, sc.Convex) ? sc.Strip : sc.Tool!;
                    (tools, convex, fits, strip) = (new List<Solid> { tool }, sc.Convex, sc.Fits, sc.Strip);
                }
                if (Debug != null)
                    Debug($"blend {name}: {string.Join(", ", group.Select(g => $"{g.Curve.GetType().Name}{g.V0.P}->{g.V1.P} [{string.Join("|", g.Uses.Select(u => u.Face.Tag.Name + ":" + u.Face.Surface.Kind))}]"))} -> tools={tools?.Count} fits={fits}");
                if (tools == null) { Fail("unsupported edge geometry."); continue; }
                // a tool running past the faces it blends is cut back to the neighbouring geometry below, once the
                // chains are settled (trimmed: still to cut)
                if (!fits && !overflow)
                {
                    Fail($"the {(fillet ? "fillet" : "chamfer")} does not fit: it runs past the faces it blends. Use a smaller size.");
                    continue;
                }
                // (the tool bounded by the faces: applied directly, and the one cut back where a tool runs past its faces)
                entries.Add((tools, convex, strip, name, group, !fits));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Fail(ex.Message); }
        }
        // a tangent chain whose pieces did not all fit (trimmed, or failed): the whole chain once more as one
        // rolling-ball tool, which has no ends at the joints to run past (a spoke's foot round on a rim: its straight
        // sides by the exact tools, the rest by the rolling ball, overflowing where they meet)
        if (fillet && !ForceRollingBall)
        {
            var bad = new HashSet<NEdge>(entries.Where(x => x.trimmed).SelectMany(x => x.group).Concat(fails.SelectMany(x => x.group)), ReferenceEqualityComparer.Instance);
            foreach (var seed in bad.ToList())
            {
                if (!bad.Contains(seed)) continue;
                var chain = TangentChain(seed, edges, new HashSet<NEdge>(ReferenceEqualityComparer.Instance));
                var set = new HashSet<NEdge>(chain, ReferenceEqualityComparer.Instance);
                var mine = Enumerable.Range(0, entries.Count).Where(k => entries[k].group.Any(set.Contains)).ToList();
                var mineF = Enumerable.Range(0, fails.Count).Where(k => fails[k].group.Any(set.Contains)).ToList();
                if (mine.Count + mineF.Count < 2) continue;
                // the chain is exactly those tools' edges
                if (mine.SelectMany(k => entries[k].group).Concat(mineF.SelectMany(k => fails[k].group)).Any(x => !set.Contains(x))) continue;
                var name0 = mine.Count > 0 ? entries[mine[0]].name : $"{prefix}.{i++}";
                RollingBall.Result rb;
                try { using (Prof.Time("blend.tools")) rb = Ball(chain, size, name0, edges); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                Debug?.Invoke($"whole tangent chain {name0}: {chain.Count} edges -> {rb.Error ?? (rb.Fits ? "fits" : "overflows")}");
                if (rb.Error != null || !rb.Fits) continue;
                foreach (var k in mine.OrderByDescending(k => k)) entries.RemoveAt(k);
                foreach (var k in mineF.OrderByDescending(k => k)) fails.RemoveAt(k);
                entries.Add((new List<Solid> { rb.Tool! }, rb.Convex, rb.Tool, name0, chain, false));
                bad.ExceptWith(chain);
            }
        }
        // "keep edge": the tools running past their faces cut back where they meet the neighbouring geometry. The
        // pieces of one tangent chain (lines split where a face beside them changes, running on into an arc) are
        // glued first and cut as one: cut apart, they meet face to face at their joints, where applying them one by
        // one fails
        var pending = Enumerable.Range(0, entries.Count).Where(k => entries[k].trimmed).ToList();
        // (the blends meeting those at a corner, on the plane bisecting it, go with them: glued first and cut back as
        // one, the whole run is applied directly; cut back alone, its end no longer matches theirs, and none of them is)
        var along = new HashSet<int>();
        if (pending.Count > 0)
            for (bool more = true; more;)
            {
                more = false;
                for (int j = 0; j < entries.Count; j++)
                {
                    if (pending.Contains(j) || entries[j].strip == null) continue;
                    if (pending.Any(k => entries[k].strip != null && entries[k].convex == entries[j].convex && MeetAtCorner(entries[k].group, entries[j].group)))
                    {
                        pending.Add(j); along.Add(j); more = true;
                    }
                }
            }
        if (pending.Count > 0)
        {
            using var _tr = Prof.Time("blend.trim");
            var par = pending.ToDictionary(k => k, k => k);
            int Find(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
            var none = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
            foreach (var k in pending)
            {
                var chain = new HashSet<NEdge>(entries[k].group.SelectMany(e => TangentChain(e, edges, none)), ReferenceEqualityComparer.Instance);
                foreach (var j in pending)
                    if (j != k && entries[j].convex == entries[k].convex && (entries[j].group.Any(chain.Contains) || MeetAtCorner(entries[k].group, entries[j].group)))
                        par[Find(j)] = Find(k);
            }
            var cut = new List<(List<Solid> tools, bool convex, Solid? strip, string name, List<NEdge> group, bool trimmed)>();
            List<NFace> FacesOf(IEnumerable<NEdge> es) => es.SelectMany(e => e.Uses.Select(u => u.Face)).Distinct(ReferenceEqualityComparer.Instance).Cast<NFace>().ToList();
            foreach (var cluster in pending.GroupBy(Find).Select(g => g.ToList()))
            {
                var all = cluster.SelectMany(k => entries[k].group).ToList();
                if (cluster.Count > 1 && cluster.All(k => entries[k].strip != null)
                    && DirectBlend.Glue(cluster.Select(k => new DirectBlend.Tool(entries[k].strip!, entries[k].convex, entries[k].name, size)).ToList(), out _) is { } glued
                    && TrimToEdge(body, new List<Solid> { glued.Strip }, glued.Convex, all, FacesOf(all)) is { } one)
                {
                    cut.Add((new List<Solid> { one }, glued.Convex, one, entries[cluster[0]].name, all, true));
                    continue;
                }
                foreach (var k in cluster)
                {
                    var x = entries[k];
                    // (one only along: not cut back, as it was)
                    if (along.Contains(k)) { cut.Add(x); continue; }
                    var faces = FacesOf(x.group);
                    // the tool bounded by the faces where there is one: the boolean's reaches past the corner, and cut
                    // back here that reach can be left exposed (material added, or taken, beyond the blend)
                    var t = TrimToEdge(body, x.strip != null ? new List<Solid> { x.strip } : x.tools, x.convex, x.group, faces);
                    if (t == null)
                    {
                        var (fa, fb) = Faces(x.group[0]);
                        fails.Add((x.group, $"Edge {fa.Tag.Name} / {fb.Tag.Name}: the {(fillet ? "fillet" : "chamfer")} does not fit: it runs past the faces it blends. Use a smaller size."));
                        continue;
                    }
                    // (applied directly too: its new faces lie on the neighbouring geometry's)
                    cut.Add((new List<Solid> { t }, x.convex, x.strip != null ? t : null, x.name, x.group, true));
                }
            }
            foreach (var k in pending.OrderByDescending(k => k)) entries.RemoveAt(k);
            entries.AddRange(cut);
        }
        foreach (var (group, msg) in fails)
        {
            errors.Add(msg);
            problemEdges?.AddRange(group.Select(g => g.Samples(1e-3).ToArray()));
        }
        foreach (var x in entries)
        {
            applied?.AddRange(x.tools.Select(t => (t, x.convex)));
            if (x.trimmed) { anyTrimmed = true; trimmedEdges.AddRange(x.group); }
        }
        // the tools only (in applied): the caller applies them (e.g. pattern instances built in parallel)
        // (trimmedOut: whether they must be applied one at a time, as below)
        if (trimmedOut != null) trimmedOut.Value = anyTrimmed;
        if (buildOnly) return body;
        var result = body;
        // direct first: the tools whose sides lie on the faces they blend change those faces (no boolean); the others
        // (and all when some were trimmed: those go one at a time) by boolean
        var byBoolean = Enumerable.Range(0, entries.Count).ToList();
        // corners where three fillets meet (a box's corner rounded all round): the ball's patch there, the edges'
        // tools shortened to it (the direct blend glues them into one)
        var corners = new List<DirectBlend.Tool>();
        var shortened = new Dictionary<NEdge, Solid>(ReferenceEqualityComparer.Instance);
        // (built for the boolean too: the edges' tools there, glued with the corner pieces, give the same corners)
        if (fillet && !anyTrimmed && !buildOnly)
        {
            var lines = entries.Where(x => x.group.Count == 1 && x.group[0].Curve is NLine && x.group[0].Uses.Count == 2
                && x.group[0].Uses.All(u => u.Face.Surface.Kind == SurfaceKind.Plane)).ToList();
            if (lines.Count >= 3)
            {
                List<(Solid piece, bool convex)> pieces;
                using (Prof.Time("blend.corners"))
                    (shortened, pieces) = CornerBlend.Build(lines.Select(x => new CornerBlend.Line(x.group[0], x.convex, x.name)).ToList(), size, prefix);
                for (int k = 0; k < entries.Count; k++)
                    if (entries[k].group.Count == 1 && shortened.TryGetValue(entries[k].group[0], out var sh)) entries[k] = entries[k] with { strip = sh };
                // (by boolean the edges with no corner keep their own tools; with direct blends off, as before)
                if (!direct)
                    for (int k = 0; k < entries.Count; k++)
                        if (!(entries[k].group.Count == 1 && shortened.ContainsKey(entries[k].group[0]))) entries[k] = entries[k] with { strip = null };
                corners.AddRange(pieces.Select((c, j) => new DirectBlend.Tool(c.piece, c.convex, $"{prefix}.C{j}", size)));
            }
        }
        if (direct && entries.Any(x => x.strip != null))
        {
            var idx = Enumerable.Range(0, entries.Count).Where(k => entries[k].strip != null).ToList();
            var left = new List<int>();
            var offered = idx.Select(k => new DirectBlend.Tool(entries[k].strip!, entries[k].convex, entries[k].name, size)).Concat(corners).ToList();
            NBody? nb;
            // (a tool the direct blend cannot take after all: all by boolean)
            try { nb = DirectBlend.Apply(body.Native!, offered, left); }
            catch (InvalidOperationException ex) { Debug?.Invoke($"direct blend failed: {ex.Message}"); nb = null; }
            if (nb != null)
            {
                result = NTess.ToSolid(nb);
                // (the corner pieces after the edges' tools: applied with them or not at all)
                var directDone = idx.Where((k, j) => !left.Contains(j)).ToHashSet();
                byBoolean = byBoolean.Where(k => !directDone.Contains(k)).ToList();
            }
        }
        // the edges into corners left to the boolean (direct blends off, or not taken): their tools glued with the corner
        // pieces they meet, as the direct blend glues them, so the corners come out the same either way
        var cornered = byBoolean.Where(k => entries[k].group.Count == 1 && shortened.ContainsKey(entries[k].group[0])).ToList();
        if (cornered.Count > 0 && corners.Count > 0
            && GlueAll(cornered.Select(k => new DirectBlend.Tool(entries[k].strip!, entries[k].convex, entries[k].name, size)).Concat(corners).ToList()) is { } joined)
        {
            foreach (var g in joined) (g.Convex ? cutTools : addTools).Add(g.Strip);
            byBoolean = byBoolean.Except(cornered).ToList();
        }
        foreach (var k in byBoolean) (entries[k].convex ? cutTools : addTools).AddRange(entries[k].tools);
        if (cutTools.Count == 0 && addTools.Count == 0) return result;
        if (byBoolean.Any(k => entries[k].trimmed))
        {
            // trimmed blends of neighbouring edges can touch each other along tangent curves; merged with each
            // other first that contact is non-manifold, so they are applied to the body one at a time instead
            // no slow facet fallback here: a failure is reported right away with the edges involved
            var old = Csg.NoFallback;
            Csg.NoFallback = true;
            int appliedOk = 0;
            try
            {
                using (Prof.Time("blend.apply"))
                {
                    foreach (var t in cutTools) { result = Csg.Subtract(result, t); appliedOk++; }
                    foreach (var t in addTools) { result = Csg.Union(result, t); appliedOk++; }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NBooleanException)
            {
                // (the boolean's own reason too: the first tool failing alone is no collision of blends)
                var what = fillet ? "fillet" : "chamfer";
                errors.Add(appliedOk == 0
                    ? $"the {what}, cut back where it runs into neighbouring geometry, cannot be joined to the body ({ex.Message}). Try a smaller size."
                    : $"the {what}s that run into neighbouring geometry also run into each other ({ex.Message}). Remove one of these edges or use a smaller size.");
                problemEdges?.AddRange(trimmedEdges.Select(g => g.Samples(1e-3).ToArray()));
                return body;
            }
            finally { Csg.NoFallback = old; }
            return result;
        }
        // exact or failed, never the faceted fallback: it stops at a wall-clock deadline, so whether it finishes
        // (and leaves a faceted body that every later boolean must then cut without a deadline) varied from run to run
        var oldExact = Csg.NoFallback;
        Csg.NoFallback = true;
        try
        {
            Solid? cu = null, au = null;
            using (Prof.Time("blend.toolunion"))
            {
                if (cutTools.Count > 0) cu = Csg.UnionAll(cutTools);
                if (addTools.Count > 0) au = Csg.UnionAll(addTools);
            }
            using (Prof.Time("blend.apply"))
            {
                if (cu != null) result = Csg.Subtract(result, cu);
                if (au != null) result = Csg.Union(result, au);
            }
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"the {(fillet ? "fillet" : "chamfer")} cannot be joined to the body ({ex.Message}). Try a smaller size.");
            problemEdges?.AddRange(edges.Select(e => e.Samples(1e-3).ToArray()));
            return body;
        }
        finally { Csg.NoFallback = oldExact; }
        return result;
    }

    static (NFace a, NFace b) Faces(NEdge e) => (e.Uses[0].Face, e.Uses[1].Face);

    /// <summary>
    /// The rolling-ball tool for a chain; where it runs past its faces and an open end meets a face at an angle (a
    /// boss's foot running into a wall), or the next blended edge at a corner (mitred, as the line and arc tools'),
    /// the tool run on past that end and cut back to that plane near it. Ended square there, it leans past on one
    /// side and falls short on the other. As it was where cutting back fails.
    /// </summary>
    static RollingBall.Result Ball(List<NEdge> chain, double size, string name, IReadOnlyList<NEdge> selected)
    {
        var rb = RollingBall.Build(chain, size, name, OnFaces);
        if (rb.Error != null || rb.Tool?.Native == null) return rb;
        // (only a tool running past its faces: one that fits ends where it should, but for the ball pivoting round a
        // corner on a round there)
        if (rb.Fits) return PivotEnds(rb, chain, size, name, selected);
        var cuts = new List<(Vec3 v, Plane pl)>();
        var corners = new List<RoundCornerCut>();
        var sel = selected as IReadOnlyCollection<NEdge> ?? selected.ToList();
        var run = 3 * size;
        var on = RollingBall.Build(chain, size, name, OnFaces, (v, dirOut, fa, fb) =>
        {
            if (EndPlane(v, dirOut, fa, fb, chain, sel) is not { } pl) return 0;
            if (RoundCorner(v, fa, fb, chain, sel, pl, size, name) is { } rc) corners.Add(rc);
            else cuts.Add((v.P, pl));
            return run;
        }, p => cuts.Any(c => c.pl.Distance(p) < 1e-9 && Vec3.Distance(p, c.v) < run + 2 * size) || corners.Any(c => c.Beyond(p)));
        if (cuts.Count + corners.Count == 0 || on.Error != null || on.Tool?.Native == null) return rb;
        var old = Csg.NoFallback;
        Csg.NoFallback = true;
        try
        {
            var t = on.Tool;
            // (only near the end: the plane may cut the tool elsewhere too, as a wall's underside in the plane of a
            // top the chain runs on along)
            foreach (var (v, pl) in cuts) t = TrimNear(t, pl, v, run + 2 * size);
            // (a run-on still running past its faces is cut back again, or given up for its whole tangent chain's:
            // cut on the mitre plane, as cheaper)
            // a fitting one: the ball pivoting round the corner edge (the fillet ended where it starts to, the first
            // of the two carrying the pivot's tool), else cut to the other fillet
            double added = 0; var carried = new List<NVertex>();
            foreach (var c in corners)
                if (!on.Fits) t = c.TrimOnMiter(t);
                else if (c.Patch() is { } patch) (t, added) = Pivot(t, c, patch, run + 2 * size, added, carried);
                else t = c.Trim(t);
            // (less the pivots' tools joined to it)
            double v0 = rb.Tool.Volume(), v1 = t.Volume() - added, vOn = on.Tool.Volume();
            // (cut back at all: an intersection the boolean misses leaves the run-on whole, as if nothing were there)
            if (t.IsEmpty || t.Native == null || t.Native.Validate() != null || Math.Abs(v1 - v0) > 0.25 * Math.Abs(v0)
                || Math.Abs(vOn - v1) < 1e-3 * Math.Abs(vOn - v0))
            {
                Debug?.Invoke($"rolling ball {name}: not cut back ({(t.Native == null ? "faceted" : t.Native.Validate() ?? $"volume {v1:g4} for {v0:g4}")})");
                return rb;
            }
            Debug?.Invoke($"rolling ball {name}: {cuts.Count + corners.Count} end(s) cut back ({(rb.Fits ? "fits" : "overflows")} -> {(on.Fits ? "fits" : "overflows")})");
            // (fitting but for what was cut off)
            Carry(carried);
            return rb with { Tool = t, Fits = on.Fits };
        }
        catch (Exception ex) when (ex is InvalidOperationException or NBooleanException)
        {
            Debug?.Invoke($"rolling ball {name}: not cut back ({ex.Message})");
            return rb;
        }
        finally { Csg.NoFallback = old; }
    }

    /// <summary>
    /// A fillet's end where it turns round a corner onto the next selected edge G, on a round face both run along, the
    /// corner between two planes (its own other face, and G's): a pedestal standing out of a casting's corner round,
    /// blended into it along its foot and up its side. No plane matches the two fillets' ends there: the one bisecting
    /// the corner leaves a sliver running on along the round past the other's end, the edges' bisector leans off it.
    /// Beyond the next face only what lies under the other fillet too is kept (its run-on past the corner): the two
    /// meet where their surfaces cross.
    /// </summary>
    sealed class RoundCornerCut
    {
        readonly NVertex _v; readonly NEdge _g; readonly Plane _next, _miter;
        readonly double _size; readonly IReadOnlyList<NEdge> _selected; readonly string _name;
        readonly NEdge _own, _corner; readonly NFace _shared, _ownFace, _nextFace;
        public RoundCornerCut(NVertex v, NEdge g, Plane next, Plane miter, double size, IReadOnlyList<NEdge> selected, string name,
            NEdge own, NEdge corner, NFace shared, NFace ownFace, NFace nextFace)
        {
            _v = v; _g = g; _next = next; _miter = miter; _size = size; _selected = selected; _name = name;
            _own = own; _corner = corner; _shared = shared; _ownFace = ownFace; _nextFace = nextFace;
        }
        double Reach => 5 * _size;
        public NVertex V => _v;

        /// <summary>The ball pivoting round the corner edge (<see cref="CornerPatch"/>); null where it does not.</summary>
        public CornerPatch.Result? Patch()
        {
            // (built once for the corner, by the first of its two fillets: the other ends on the same plane)
            if (_patches != null && _patches.TryGetValue(_v, out var known))
                    return known.Result is { } k ? ReferenceEquals(known.Own, _own) ? k : k.Swapped : null;
            var along = ReferenceEquals(_corner.V0, _v) ? _corner.Curve.Deriv(_corner.T0) : -_corner.Curve.Deriv(_corner.T1);
            CornerPatch.Result? p;
            using (Prof.Time("blend.corner.patch"))
                p = CornerPatch.Build(_v, along, Vec3.Distance(_corner.V0.P, _corner.V1.P), _own, _g, _shared, _ownFace, _nextFace, _size, _name + ".C");
            if (_patches != null) _patches[_v] = new Built(p, _own);
            if (p != null) _pivotsUsed++;
            return p;
        }

        public sealed record Built(CornerPatch.Result? Result, NEdge Own);

        /// <summary>Whether p lies beyond the next face, near the corner.</summary>
        public bool Beyond(Vec3 p) => _next.Distance(p) > -1e-9 && Vec3.Distance(p, _v.P) < Reach;

        /// <summary>The tool less what lies past the mitre plane near the corner.</summary>
        public Solid TrimOnMiter(Solid tool) => TrimNear(tool, _miter, _v.P, Reach);

        /// <summary>The tool less what lies beyond the next face and not under the other fillet; cut on the mitre plane
        /// near the corner where the other fillet cannot be built.</summary>
        public Solid Trim(Solid tool)
        {
            RollingBall.Result other;
            using (Prof.Time("blend.corner.other"))
                other = RollingBall.Build(TangentChain(_g, _selected, new HashSet<NEdge>(ReferenceEqualityComparer.Instance)), _size, _name, OnFaces,
                    (w, _, _, _) => ReferenceEquals(w, _v) ? 3 * _size : 0);
            if (other.Error != null || other.Tool?.Native == null) return TrimOnMiter(tool);
            var n = _next.Normal; var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
            var rect = ProfileLoop.Polygon(new[] { new Vec2(-Reach, -Reach), new Vec2(Reach, -Reach), new Vec2(Reach, Reach), new Vec2(-Reach, Reach) }, "corner");
            var beyond = Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(_next.Project(_v.P), x, y, n), 0, Reach, "corner");
            // (a boolean that fails or leaves the tool invalid: on the mitre plane, as without the other fillet; the
            // cut back at the chain's other end, if any, kept)
            using (Prof.Time("blend.corner.cut"))
                try
                {
                    var cut = Csg.Subtract(tool, Csg.Subtract(beyond, other.Tool));
                    if (!cut.IsEmpty && cut.Native != null && cut.Native.Validate() == null) return cut;
                    Debug?.Invoke($"round corner at {_v.P}: cut to the other fillet {(cut.Native == null ? "faceted" : cut.Native.Validate() ?? "empty")}, on the mitre plane instead");
                }
                catch (Exception ex) when (ex is InvalidOperationException or NBooleanException)
                {
                    Debug?.Invoke($"round corner at {_v.P}: cut to the other fillet failed ({ex.Message}), on the mitre plane instead");
                }
            return TrimOnMiter(tool);
        }
    }

    /// <summary>
    /// The cut at a fillet's end v turning round a corner onto the next selected edge on a round face (see
    /// <see cref="RoundCornerCut"/>; on a plane the mitre plane matches both ends), the corner between two planes.
    /// Null otherwise.
    /// </summary>
    static RoundCornerCut? RoundCorner(NVertex v, NFace fa, NFace fb, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge> selected, Plane miter, double size, string name)
    {
        if (_noPivots || _pivotFailed) return null;
        NEdge? g = null; int count = 0;
        foreach (var face in new[] { fa, fb })
            foreach (var c in face.Coedges)
            {
                var e = c.Edge;
                if (!ReferenceEquals(e.V0, v) && !ReferenceEquals(e.V1, v) || blended.Contains(e) || !selected.Contains(e) || IsSeam(e) || ReferenceEquals(e, g)) continue;
                g = e; count++;
            }
        if (count != 1 || g!.Uses.Count != 2) return null;
        var shared = g.Uses.Any(u => ReferenceEquals(u.Face, fa)) ? fa : fb;
        var own = ReferenceEquals(shared, fa) ? fb : fa;
        var next = g.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, shared));
        if (own.Surface.Kind != SurfaceKind.Plane || next.Surface.Kind != SurfaceKind.Plane || ReferenceEquals(own, next)) return null;
        if (CornerEdgeOf(v, shared, new[] { fa, fb, next }, blended, selected) is not { } corner || corner.Curve is not NLine || corner.Uses.Count != 2) return null;
        // (a convex corner edge, what stands on the shared face sticking out: the ball pivots round it; at a concave one,
        // a socket's inside corner, the fillets meet in a mitre)
        {
            var co = corner.Uses.FirstOrDefault(u => ReferenceEquals(u.Face, own)); var cn = corner.Uses.FirstOrDefault(u => ReferenceEquals(u.Face, next));
            if (co == null || cn == null) return null;
            var tm = 0.5 * (corner.T0 + corner.T1); var pm = corner.Curve.Eval(tm);
            var uO = Vec3.Cross(Outward(own, pm), Tangent(co, tm));
            if (Vec3.Dot(uO, Outward(next, pm)) >= 0) return null;
        }
        if (Math.Abs(Vec3.Dot(Outward(own, v.P), Outward(next, v.P))) > 0.999) return null;
        // (concave: the ball outside the faces, rolling round what stands on the shared one)
        if (Wedge(v, shared, own, blended) is not { } w || Vec3.Dot(w, Outward(shared, v.P) + Outward(own, v.P)) <= 0) return null;
        var ownEdge = blended.FirstOrDefault(e => (ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)) && e.Uses.Any(u => ReferenceEquals(u.Face, own)) && e.Uses.Any(u => ReferenceEquals(u.Face, shared)));
        if (ownEdge == null) return null;
        return new RoundCornerCut(v, g, Plane.FromPointNormal(v.P, Outward(next, v.P)), miter, size, selected as IReadOnlyList<NEdge> ?? selected.ToList(), name,
            ownEdge, corner, shared, own, next);
    }

    /// <summary>
    /// A fitting rolling-ball tool, its ends where the ball pivots round a corner on a round (<see cref="Pivot"/>):
    /// as it is where none does or the pivot cannot be built.
    /// </summary>
    static RollingBall.Result PivotEnds(RollingBall.Result rb, List<NEdge> chain, double size, string name, IReadOnlyList<NEdge> selected)
    {
        var sel = selected as IReadOnlyCollection<NEdge> ?? selected.ToList();
        var t = rb.Tool!;
        var old = Csg.NoFallback;
        Csg.NoFallback = true;
        try
        {
            foreach (var e in chain)
                foreach (var v in new[] { e.V0, e.V1 })
                {
                    // (an end of the chain: no other edge of it there)
                    if (chain.Count(x => ReferenceEquals(x.V0, v) || ReferenceEquals(x.V1, v)) != 1 || e.Uses.Count != 2) continue;
                    var dirOut = (ReferenceEquals(v, e.V1) ? e.Curve.Deriv(e.T1) : -e.Curve.Deriv(e.T0)).Normalized;
                    var (fa, fb) = Faces(e);
                    if (EndPlane(v, dirOut, fa, fb, chain, sel) is not { } pl || RoundCorner(v, fa, fb, chain, sel, pl, size, name) is not { } rc || rc.Patch() is not { } patch) continue;
                    var carried = new List<NVertex>();
                    var (cut, _) = Pivot(t, rc, patch, 5 * size, 0, carried, runOn: false);
                    if (cut.IsEmpty || cut.Native == null || cut.Native.Validate() != null) { Debug?.Invoke($"rolling ball {name}: no pivot at {v.P} ({cut.Native?.Validate() ?? "faceted"})"); continue; }
                    t = cut;
                    Carry(carried);
                }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NBooleanException) { Debug?.Invoke($"rolling ball {name}: no pivot ({ex.Message})"); return rb; }
        finally { Csg.NoFallback = old; }
        return ReferenceEquals(t, rb.Tool) ? rb : rb with { Tool = t };
    }

    /// <summary>
    /// A fillet's tool ended where the ball starts to pivot round the corner (<see cref="CornerPatch"/>), the pivot's
    /// tool joined to it unless the other fillet's carries it.
    /// </summary>
    static (Solid tool, double added) Pivot(Solid tool, RoundCornerCut c, CornerPatch.Result patch, double reach, double added, List<NVertex> carried, bool runOn = true)
    {
        // (a tool not run on that the corner takes on from v's joint: as it is)
        Solid t;
        using (Prof.Time("blend.pivot.cut")) t = !runOn && patch.OwnAtJoint ? tool : TrimNear(tool, patch.EndOwn, c.V.P, reach);
        bool taken;
        taken = _pivoted?.Contains(c.V) == true;
        if (taken) return (t, added);
        // (a union that fails leaves the pivot to the other fillet's tool)
        try
        {
            Solid u;
            using (Prof.Time("blend.pivot.join")) u = JoinPivot(t, patch.Tool);
            if (!u.IsEmpty && u.Native != null && u.Native.Validate() == null)
            {
                carried.Add(c.V);
                return (u, added + patch.Tool.Volume());
            }
            Debug?.Invoke($"pivot at {c.V.P}: not joined to this tool ({u.Native?.Validate() ?? "faceted"})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NBooleanException) { Debug?.Invoke($"pivot at {c.V.P}: not joined to this tool ({ex.Message})"); }
        return (t, added);
    }

    /// <summary>A fillet's tool and a pivot's joined: glued where their ends match (as built), else by boolean.</summary>
    static Solid JoinPivot(Solid tool, Solid pivot)
    {
        if (DirectBlend.Glue(new List<DirectBlend.Tool> { new(tool, false, "tool", 0), new(pivot, false, "pivot", 0) }, out var why) is { } one) return one.Strip;
        // (not glued, the booleans joining them and the body are slow and fail often: the feature is built mitred,
        // no pivot built from here on in this run, which is thrown away)
        Debug?.Invoke($"pivot not glued ({why}): the feature mitred");
        _pivotFailed = true;
        return tool;
    }

    /// <summary>The corners whose pivot a tool now kept carries.</summary>
    static void Carry(List<NVertex> carried)
    {
        if (_pivoted != null) foreach (var v in carried) _pivoted.Add(v);
    }

    /// <summary>The tool less what lies on the negative side of the plane within reach of p (a box about p).</summary>
    static Solid TrimNear(Solid tool, Plane pl, Vec3 p, double reach)
    {
        var n = pl.Normal; var origin = pl.Project(p);
        var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
        var rect = ProfileLoop.Polygon(new[] { new Vec2(-reach, -reach), new Vec2(reach, -reach), new Vec2(reach, reach), new Vec2(-reach, reach) }, "trim");
        var slab = Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(origin, x, y, n), -reach, 0, "trim");
        return Csg.Subtract(tool, slab);
    }

    /// <summary>The tools glued into one per group of them that touch (boxes overlapping); null if any group does not glue.</summary>
    static List<DirectBlend.Tool>? GlueAll(List<DirectBlend.Tool> tools)
    {
        var boxes = tools.Select(t => t.Strip.Bounds).ToList();
        var par = Enumerable.Range(0, tools.Count).ToArray();
        int Find(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
        for (int i = 0; i < tools.Count; i++)
            for (int j = i + 1; j < tools.Count; j++)
                if (boxes[i].Overlaps(boxes[j], 1e-9 * Math.Max(1, Math.Max(boxes[i].Diagonal, boxes[j].Diagonal)))) par[Find(i)] = Find(j);
        var res = new List<DirectBlend.Tool>();
        foreach (var g in Enumerable.Range(0, tools.Count).GroupBy(Find))
        {
            var members = g.Select(k => tools[k]).ToList();
            if (members.Count == 1) { res.Add(members[0]); continue; }
            if (DirectBlend.Glue(members, out var why) is not { } one) { Debug?.Invoke($"corners by boolean not glued: {why}"); return null; }
            res.Add(one);
        }
        return res;
    }

    /// <summary>Whether two blends' edges meet at a vertex at an angle, on a face both run along (a corner they meet at,
    /// mitred).</summary>
    static bool MeetAtCorner(List<NEdge> a, List<NEdge> b)
    {
        foreach (var x in a)
            foreach (var y in b)
            {
                if (ReferenceEquals(x, y)) continue;
                NVertex? v = ReferenceEquals(x.V0, y.V0) || ReferenceEquals(x.V0, y.V1) ? x.V0 : ReferenceEquals(x.V1, y.V0) || ReferenceEquals(x.V1, y.V1) ? x.V1 : null;
                if (v == null) continue;
                if (x.Uses.Any(u => y.Uses.Any(w => ReferenceEquals(u.Face, w.Face)))) return true;
            }
        return false;
    }

    /// <summary>The selected edges connected to e end to end through edges between the same two surfaces.</summary>
    static List<NEdge> Chain(NEdge e, IReadOnlyList<NEdge> selected)
    {
        var res = new List<NEdge> { e };
        for (int i = 0; i < res.Count; i++)
        {
            var c = res[i];
            foreach (var o in selected)
                if (!res.Contains(o) && (ReferenceEquals(o.V0, c.V0) || ReferenceEquals(o.V0, c.V1) || ReferenceEquals(o.V1, c.V0) || ReferenceEquals(o.V1, c.V1))
                    && RollingBall.SameSurfaces(e, o))
                    res.Add(o);
        }
        return res;
    }

    /// <summary>
    /// The selected edges connected to e end to end where one side stays on the same surface and the faces on the
    /// other side meet tangentially at the shared vertex (or are the same surface).
    /// </summary>
    static List<NEdge> TangentChain(NEdge e, IReadOnlyList<NEdge> selected, HashSet<NEdge> done)
    {
        static bool Same(NSurface x, NSurface y) => ReferenceEquals(x, y) || x.CoincidentWith(y, 1e-9);
        var res = new List<NEdge> { e };
        for (int i = 0; i < res.Count; i++)
        {
            var c = res[i];
            foreach (var o in selected)
            {
                if (res.Contains(o) || done.Contains(o) || o.Uses.Count != 2 || c.Uses.Count != 2) continue;
                NVertex? v = ReferenceEquals(o.V0, c.V0) || ReferenceEquals(o.V0, c.V1) ? o.V0 : ReferenceEquals(o.V1, c.V0) || ReferenceEquals(o.V1, c.V1) ? o.V1 : null;
                if (v == null) continue;
                if (RollingBall.SameSurfaces(c, o)) { res.Add(o); continue; }
                bool joined = false;
                foreach (var ca in c.Uses.Select(u => u.Face))
                    foreach (var oa in o.Uses.Select(u => u.Face))
                    {
                        if (joined || !Same(ca.Surface, oa.Surface) || ca.Sense != oa.Sense) continue;
                        var cb = c.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, ca));
                        var ob = o.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, oa));
                        if (Same(cb.Surface, ca.Surface) || Same(ob.Surface, oa.Surface)) continue;
                        joined = Vec3.Dot(Outward(cb, v.P), Outward(ob, v.P)) > 1 - 1e-4;
                    }
                if (joined) res.Add(o);
            }
        }
        return res;
    }

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    /// <summary>Direction of the edge as traversed by the coedge, at curve parameter t.</summary>
    static Vec3 Tangent(NCoedge c, double t) => (c.Edge.Curve.Deriv(t) * (c.Forward ? 1 : -1)).Normalized;

    static (List<Solid>? tools, bool convex, bool fits, Solid? strip) LineTool(NEdge e, double size, bool fillet, string name, ChamferSize ch, Func<NFace, NFace, bool> refIsFirst,
        bool direct, NBody body, IReadOnlyList<NEdge> selected)
    {
        var cA = e.Uses[0]; var cB = e.Uses[1];
        if (e.Curve is not NLine || cA.Face.Surface.Kind != SurfaceKind.Plane || cB.Face.Surface.Kind != SurfaceKind.Plane) return (null, false, false, null);
        var E = cA.Start.P; var dir = cA.End.P - E; var L = dir.Length; var t = dir / L;
        var nA = Outward(cA.Face, E); var nB = Outward(cB.Face, E);
        // faces lie to the left of their loops (seen from outside): n × direction points into the face
        var uA = Vec3.Cross(nA, t).Normalized;
        var uB = Vec3.Cross(nB, -t).Normalized;
        bool convex = Vec3.Dot(uB, nA) < 0;
        var x = uA; var y = Vec3.Cross(t, x).Normalized;
        var lframe = new Frame(E, x, y, t);
        Vec2 P(Vec3 v) => new(Vec3.Dot(v, x), Vec3.Dot(v, y));
        var (dA, dB) = fillet ? (size, size) : EdgeBlend.ChamferLegs(P(uA), P(uB), ch, refIsFirst(cA.Face, cB.Face));
        var loop = EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB);
        if (loop == null) return (null, convex, false, null);
        // the profile reaches past the corner (half the size, outside a convex edge, inside a concave one): where
        // that space is taken (a wall across a narrow gap, a void behind), the profile bounded by the faces instead
        {
            var o2 = (P(nA) + P(nB)).Normalized * ((convex ? 1 : -1) * Math.Max(dA, dB) * 0.5);
            var off = x * o2.X + y * o2.Y;
            var pts = new List<Vec3>();
            foreach (var s0 in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
                foreach (var f in new[] { 0.5, 0.95 })
                    pts.Add(E + t * (L * s0) + off * f);
            if (Taken(body, cA.Face, cB.Face, pts, convex) && EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB, atCorner: true) is { } bounded)
                loop = bounded;
        }
        var (tlA, tlB) = TangentLengths(P(uA), P(uB), size, fillet, dA, dB);
        // ends meeting a face at an angle (an edge of a drafted boss on the plate): the tool's square end leans past
        // that face on one side, into the material beyond it, and falls short on the other. The tool is cut back to
        // the face, and an extension past the end, also cut back to it, fills the other side
        var ext = 3 * Math.Max(size, Math.Max(tlA, tlB));
        // (a fillet turning round a corner onto the next: ended where the ball starts to pivot round it)
        var pivots = new List<(RoundCornerCut c, CornerPatch.Result patch)>();
        EndCut? End(NVertex v, Vec3 dirOut)
        {
            if (fillet && EndPlane(v, dirOut, cA.Face, cB.Face, new[] { e }, selected) is { } pl
                && RoundCorner(v, cA.Face, cB.Face, new[] { e }, selected, pl, size, name) is { } rc && rc.Patch() is { } patch)
            {
                pivots.Add((rc, patch));
                return new EndCut(patch.EndOwn);
            }
            return EndCutAt(v, dirOut, cA.Face, cB.Face, new[] { e }, selected);
        }
        var p0 = End(cA.Start, -t);
        var p1 = End(cA.End, t);
        // (contact points the ends' faces cut off are not the blend's)
        bool Cut(Vec3 x) => p0 != null && p0.Distance(x) < 0 || p1 != null && p1.Distance(x) < 0;
        bool fits = true;
        for (int i = 0; i <= 16 && fits; i++)
        {
            var q = E + t * (L * i / 16);
            Vec3 xa = q + uA * tlA, xb = q + uB * tlB;
            fits = (Cut(xa) || OnFaces(cA.Face, xa)) && (Cut(xb) || OnFaces(cB.Face, xb));
        }
        // the profile from end to end, its ends on those planes: built so (a prism with leaning ends), else extruded
        // past them and cut back
        Solid Between(ProfileLoop prof, bool extend)
        {
            if ((p0 != null || p1 != null) && p0?.Plane is not null == (p0 != null) && p1?.Plane is not null == (p1 != null)
                && NBuild.ExtrudeBetween(new[] { new ProfileRegion(prof) }, lframe, p0?.Plane, 0, p1?.Plane, L, name) is { } nb)
            {
                var sol0 = NTess.ToSolid(nb);
                if (p0 != null) sol0 = p0.TrimOthers(sol0);
                if (p1 != null) sol0 = p1.TrimOthers(sol0);
                return sol0;
            }
            var sol = Builders.Extrude(new[] { new ProfileRegion(prof) }, lframe, p0 != null && extend ? -ext : 0, p1 != null && extend ? L + ext : L, name);
            if (p0 != null) sol = p0.Trim(sol);
            if (p1 != null) sol = p1.Trim(sol);
            return sol;
        }
        // (ending where the ball pivots round a corner: bounded by the faces, its end the pivot's own section; reaching
        // past them, its end stands beside the pivot's in slivers (along the other edge, on a drafted face) and the union
        // fails)
        ProfileLoop? sl0 = null;
        if (pivots.Count > 0 && EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB, atCorner: true) is { } atJoint)
            loop = sl0 = atJoint;
        var tools = new List<Solid> { Between(loop, true) };
        tools.RemoveAll(x => x.IsEmpty);
        // the tool bounded by the faces (applied directly, or cut back where it runs past them), its ends at an angle on
        // the faces they meet (on those faces then, as its other faces are on the blended ones)
        Solid? strip = null;
        if ((direct || !fits) && EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB, atCorner: true) is { } sl)
        {
            strip = Between(sl, true);
            if (strip.IsEmpty || strip.Native == null) strip = null;
        }
        // the pivots' tools, unless the other fillets' carry them
        foreach (var (c, patch) in pivots)
        {
            bool taken;
            taken = _pivoted?.Contains(c.V) == true;
            if (taken) continue;
            // (the tool bounded by the faces at a pivot: the strip itself, joined to the pivot as one)
            if (tools.Count == 1 && strip != null && ReferenceEquals(loop, sl0))
                try { using (Prof.Time("blend.pivot.strip")) tools[0] = JoinPivot(tools[0], patch.Tool); }
                catch (Exception ex) when (ex is InvalidOperationException or NBooleanException) { tools.Add(patch.Tool); }
            else tools.Add(patch.Tool);
            if (strip != null)
                try { using (Prof.Time("blend.pivot.strip")) strip = JoinPivot(strip, patch.Tool); if (strip.Native == null || strip.Native.Validate() != null) strip = null; }
                catch (Exception ex) when (ex is InvalidOperationException or NBooleanException) { strip = null; }
            Carry(new List<NVertex> { c.V });
        }
        return (tools, convex, fits, strip);
    }

    static (List<Solid>? tools, bool convex, bool fits, Solid? strip) ArcTools(List<NEdge> arcs, double size, bool fillet, string name, ChamferSize ch, Func<NFace, NFace, bool> refIsFirst,
        bool direct, NBody body, IReadOnlyList<NEdge> selected)
    {
        var e = arcs[0];
        var cP = e.Uses[0]; var cC = e.Uses[1];
        if (cP.Face.Surface.Kind != SurfaceKind.Plane) (cP, cC) = (cC, cP);
        // a plane square to a cylinder (a hole's or a boss's rim) or to a cone about the same axis (a drafted boss's or
        // a countersink's rim): the profile revolved about the axis
        if (cP.Face.Surface.Kind != SurfaceKind.Plane || cC.Face.Surface.Kind is not (SurfaceKind.Cylinder or SurfaceKind.Cone)) return (null, false, false, null);
        bool cone = cC.Face.Surface.Kind == SurfaceKind.Cone;
        var cs = cC.Face.Surface;
        var ax = cs.Axis;
        var tm = 0.5 * (e.T0 + e.T1);
        var M = e.Curve.Eval(tm);
        var nP = Outward(cP.Face, M); var nC = Outward(cC.Face, M);
        if (Math.Abs(Vec3.Dot(ax, nP)) < 0.999) return (null, false, false, null);
        var uP = Vec3.Cross(nP, Tangent(cP, tm)).Normalized;
        var uC = Vec3.Cross(nC, Tangent(cC, tm)).Normalized;
        bool convex = Vec3.Dot(uC, nP) < 0;

        var rel = M - cs.Origin;
        var h0 = Vec3.Dot(rel, ax);
        var rho = (rel - ax * h0).Normalized;
        var uP2 = new Vec2(Math.Sign(Vec3.Dot(uP, rho)), 0);
        var uC2 = new Vec2(0, Math.Sign(Vec3.Dot(uC, ax)));
        var nP2 = new Vec2(0, Math.Sign(Vec3.Dot(nP, ax)));
        var nC2 = new Vec2(Math.Sign(Vec3.Dot(nC, rho)), 0);
        // (radius, height) of the edge; a cone's side leans in the (radius, height) plane
        var rE = cs.R;
        if (cone)
        {
            rE = (rel - ax * h0).Length;
            uC2 = new Vec2(Vec3.Dot(uC, rho), Vec3.Dot(uC, ax)).Normalized;
            nC2 = new Vec2(Vec3.Dot(nC, rho), Vec3.Dot(nC, ax)).Normalized;
        }
        var (dP, dC) = fillet ? (size, size) : EdgeBlend.ChamferLegs(uP2, uC2, ch, refIsFirst(cP.Face, cC.Face));
        var prof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(rE, h0), dP, fillet, convex, dC);
        if (prof == null) return (null, convex, false, null);
        // the tool bounded by the faces (applied directly, or cut back where it runs past them), revolved over the same arc (none for an
        // arc whose ends meet faces at an angle: those need the extensions)
        var sprof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(rE, h0), dP, fillet, convex, dC, atCorner: true);
        // (the blend reaching across the axis: larger than the round it runs along, as a fillet in a hole's rounded corner)
        if (prof.Verts.Any(v => v.P.X < -1e-9))
            throw new InvalidOperationException($"{(fillet ? $"R{size:0.###}" : $"a {size:0.###} chamfer")} does not fit inside the R{rE:0.###} round it runs along: use a size smaller than {rE:0.###}.");
        var (tlP, tlC) = TangentLengths(uP2, uC2, size, fillet, dP, dC);
        var tlc = Math.Max(tlP, tlC);
        var onPlane = new Vec2(rE, h0) + uP2 * tlP;   // (radius, height) of the tangent circle on the plane
        var onCyl = new Vec2(rE, h0) + uC2 * tlC;     // ... and on the cylinder

        var refDir = cs.F.XDir; var yDir = Vec3.Cross(ax, refDir);
        double Ang(Vec3 p) { var r = p - cs.Origin; return Math.Atan2(Vec3.Dot(r, yDir), Vec3.Dot(r, refDir)); }
        static double Wrap(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
        // counter-clockwise (about the axis) range of each arc
        var ranges = new List<(double a0, double sweep)>();
        foreach (var arc in arcs)
        {
            var a0 = Ang(arc.V0.P); var sweep = Wrap(Ang(arc.V1.P) - a0);
            if (Wrap(Ang(arc.Mid) - a0) > sweep) { a0 = Ang(arc.V1.P); sweep = Wrap(Ang(arc.V0.P) - a0); }
            ranges.Add((a0, sweep));
        }
        // the profile's reach past the corner, as the line tool's: where its space is taken, the profile bounded by the
        // faces instead
        {
            var o2 = (nP2 + nC2).Normalized * ((convex ? 1 : -1) * Math.Max(dP, dC) * 0.5);
            var pts = new List<Vec3>();
            foreach (var (a0, sweep) in ranges)
                foreach (var s0 in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
                {
                    var a = a0 + sweep * s0;
                    var dir = refDir * Math.Cos(a) + yDir * Math.Sin(a);
                    foreach (var f in new[] { 0.5, 0.95 }) pts.Add(cs.Origin + dir * (rE + o2.X * f) + ax * (h0 + o2.Y * f));
                }
            if (Taken(body, cP.Face, cC.Face, pts, convex)
                && EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(rE, h0), dP, fillet, convex, dC, atCorner: true) is { } bounded)
                prof = bounded;
        }
        // chain ends meeting a face at an angle (e.g. a keyway wall across a bore): extensions past the ends, each
        // cut back to its face
        var extensions = new List<(double a0, double sweep, EndCut plane, int arc)>();
        if (ranges.Sum(r => r.sweep) < 2 * Math.PI - 1e-6)
        {
            var delta = Math.Min(Math.Min(3 * Math.Max(size, tlc) / rE, Math.PI / 4), (2 * Math.PI - ranges.Sum(r => r.sweep)) / 2);
            static double Wrap2(double a) { a %= 2 * Math.PI; return a > Math.PI ? a - 2 * Math.PI : a < -Math.PI ? a + 2 * Math.PI : a; }
            Vec3 DirAt(double a) => -refDir * Math.Sin(a) + yDir * Math.Cos(a);
            for (int i = 0; i < arcs.Count; i++)
            {
                var arc = arcs[i]; var (a0, sweep) = ranges[i];
                var startV = Math.Abs(Wrap2(Ang(arc.V0.P) - a0)) < 1e-6 ? arc.V0 : arc.V1;
                var endV = ReferenceEquals(startV, arc.V0) ? arc.V1 : arc.V0;
                foreach (var atStart in new[] { true, false })
                {
                    var v = atStart ? startV : endV;
                    if (arcs.Count(o => ReferenceEquals(o.V0, v) || ReferenceEquals(o.V1, v)) > 1) continue; // joint inside the chain
                    var dirOut = atStart ? -DirAt(a0) : DirAt(a0 + sweep);
                    if (delta > 1e-9 && EndCutAt(v, dirOut, cP.Face, cC.Face, arcs, selected) is { } pl)
                    {
                        // (turning round a corner on the round face onto the next fillet: no plane matches both ends,
                        // the rolling ball's tool is cut back to the other fillet instead)
                        if (fillet && pl.Plane != null && RoundCorner(v, cP.Face, cC.Face, arcs, selected, pl.Plane, size, name) != null) return (null, false, false, null);
                        extensions.Add((atStart ? a0 - delta : a0 + sweep, delta, pl, i));
                    }
                }
            }
        }
        // (contact points the ends' faces cut off are not the blend's)
        bool Cut(Vec3 x) => extensions.Any(e => e.plane.Distance(x) < 0);
        bool fits = true;
        foreach (var (a0, sweep) in ranges)
            for (int i = 0; i <= 24 && fits; i++)
            {
                var a = a0 + sweep * i / 24;
                var dir = refDir * Math.Cos(a) + yDir * Math.Sin(a);
                Vec3 xp = cs.Origin + ax * onPlane.Y + dir * onPlane.X, xc = cs.Origin + ax * onCyl.Y + dir * onCyl.X;
                fits = (Cut(xp) || OnFaces(cP.Face, xp)) && (Cut(xc) || OnFaces(cC.Face, xc));
            }
        var tools = new List<Solid>();
        foreach (var (xa, xs, pl, _) in extensions)
        {
            var startDir = refDir * Math.Cos(xa) + yDir * Math.Sin(xa);
            var xf = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
            var piece = pl.Trim(Builders.Revolve(new[] { new ProfileRegion(prof) }, xf, Vec2.Zero, new Vec2(0, 1), xs, name));
            if (!piece.IsEmpty) tools.Add(piece);
        }
        if (ranges.Sum(r => r.sweep) > 2 * Math.PI - 1e-6)
        {
            var frame = new Frame(cs.Origin, refDir, ax, Vec3.Cross(refDir, ax));
            tools.Add(Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name));
            var full = sprof != null && (direct || !fits) && extensions.Count == 0
                ? Builders.Revolve(new[] { new ProfileRegion(sprof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, name) : null;
            return (tools, convex, fits, full);
        }
        Solid Piece(ProfileLoop pr, double from, double sweep)
        {
            var startDir = refDir * Math.Cos(from) + yDir * Math.Sin(from);
            return Builders.Revolve(new[] { new ProfileRegion(pr) }, new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax)), Vec2.Zero, new Vec2(0, 1), sweep, name);
        }
        // whether an end's plane cuts the profile revolved over (from, to): a long arc curves back across the plane of
        // the face its end meets (a slot's wall across a hole's rim), where the plane is no end of it
        bool Reaches(EndCut pl, double from, double to)
        {
            for (int i = 0; i <= 16; i++)
            {
                var a = from + (to - from) * i / 16;
                var dir = refDir * Math.Cos(a) + yDir * Math.Sin(a);
                foreach (var v in prof.Verts) if (pl.Distance(cs.Origin + dir * v.P.X + ax * v.P.Y) < -1e-9) return true;
            }
            return false;
        }
        // (the end lengths next to the ends cut back, and whether a plane reaches past them)
        (double s, double e, bool split) Ends(List<(double a0, double sweep, EndCut plane, int arc)> mine, double a0, double sweep)
        {
            double s = a0, e = a0 + sweep;
            foreach (var x in mine)
            {
                if (Math.Abs(x.a0 + x.sweep - a0) < 1e-9) s = a0 + Math.Min(x.sweep, sweep / 2);
                else e = a0 + sweep - Math.Min(x.sweep, sweep / 2);
            }
            return (s, e, e - s > 1e-9 && mine.Any(x => Reaches(x.plane, s, e)));
        }
        for (int k = 0; k < ranges.Count; k++)
        {
            var (a0, sweep) = ranges[k];
            if (sweep < 1e-9) continue;
            var mine = extensions.Where(x => x.arc == k).ToList();
            var (ms, me, split) = Ends(mine, a0, sweep);
            if (!split)
            {
                var piece = Piece(prof, a0, sweep);
                // cut back to the faces its ends meet at an angle (as the line tool's)
                foreach (var x in mine) piece = x.plane.Trim(piece);
                if (!piece.IsEmpty) tools.Add(piece);
                continue;
            }
            // the plane would cut the middle too: only the ends cut back, the middle whole
            foreach (var x in mine)
            {
                var atStart = Math.Abs(x.a0 + x.sweep - a0) < 1e-9;
                var end = x.plane.Trim(atStart ? Piece(prof, a0, ms - a0) : Piece(prof, me, a0 + sweep - me));
                if (!end.IsEmpty) tools.Add(end);
            }
            tools.Add(Piece(prof, ms, me - ms));
        }
        Solid? strip = null;
        if (sprof != null && (direct || !fits) && ranges.Count(r => r.sweep >= 1e-9) == 1)
        {
            // one arc: revolved over it and over its extensions, cut back to the faces its ends meet at an angle
            int k = ranges.FindIndex(r => r.sweep >= 1e-9);
            var (a0, sweep) = ranges[k];
            var mine = extensions.Where(x => x.arc == k).ToList();
            double from = a0, to = a0 + sweep;

            // (a plane cutting its middle too: left to the boolean)
            if (!Ends(mine, a0, sweep).split)
            {
                foreach (var x in mine)
                {
                    if (Math.Abs(x.a0 + x.sweep - a0) < 1e-9) from = x.a0;   // before the start
                    else to = x.a0 + x.sweep;                                // after the end
                }
                strip = Piece(sprof, from, to - from);
                foreach (var x in mine) strip = x.plane.Trim(strip);
                if (strip.IsEmpty || strip.Native == null) strip = null;
            }
        }
        return (tools, convex, fits, strip);
    }

    /// <summary>
    /// What a blend's end is cut back to where it meets a face at an angle: that face's plane, or the face itself where it
    /// is round (a cylinder: a hole breaking into a rim). Distance is positive on the side kept (the blend's).
    /// </summary>
    internal sealed class EndCut
    {
        public Plane? Plane { get; }
        readonly NFace? _face; readonly double _sign;
        /// <summary>The other faces' planes at the end, past Plane (the blend's reach into the material beside it, cut
        /// on Plane alone, can stand out past one of them).</summary>
        public IReadOnlyList<Plane> Others { get; }
        public EndCut(Plane pl, IReadOnlyList<Plane>? others = null) { Plane = pl; Others = others ?? Array.Empty<Plane>(); }
        public EndCut(NFace face, double sign) { _face = face; _sign = sign; Others = Array.Empty<Plane>(); }
        public double Distance(Vec3 p) => Plane != null ? Others.Aggregate(Plane.Distance(p), (d, o) => Math.Min(d, o.Distance(p))) : _face!.Surface.F_(p) * _sign;
        /// <summary>Whether two cuts are by the same round face (or coincident planes).</summary>
        public bool SameFace(EndCut o) => Plane != null ? o.Plane != null && Math.Abs(Vec3.Dot(Plane.Normal, o.Plane.Normal) - 1) < 1e-9 && Math.Abs(Plane.Distance(o.Plane.Project(Vec3.Zero))) < 1e-9
            : o._face != null && (ReferenceEquals(_face, o._face) || _face!.Surface.CoincidentWith(o._face.Surface, 1e-9)) && _sign == o._sign;
        /// <summary>Unit normal towards the kept side.</summary>
        public Vec3 NormalAt(Vec3 p) => Plane?.Normal ?? (_face!.Surface.Grad(p) * _sign).Normalized;
        /// <summary>The part of the tool on the kept side.</summary>
        public Solid Trim(Solid tool)
        {
            if (Plane != null) return TrimOthers(NativeBlend.Trim(tool, Plane));
            // the cylinder over the tool's span along its axis: the blend kept outside it (a hole) or inside it
            var s = _face!.Surface; var ax = s.Axis;
            double h0 = double.MaxValue, h1 = double.MinValue;
            var box = tool.Bounds;
            for (int c = 0; c < 8; c++)
            {
                var p = new Vec3((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
                var h = Vec3.Dot(p - s.Origin, ax);
                h0 = Math.Min(h0, h); h1 = Math.Max(h1, h);
            }
            var cyl = Builders.Cylinder(s.Origin + ax * (h0 - 1), ax, s.R, h1 - h0 + 2, "trim", s.F.XDir);
            return _sign > 0 ? Csg.Subtract(tool, cyl) : Csg.Intersect(tool, cyl);
        }

        /// <summary>The tool less what lies past the other planes.</summary>
        public Solid TrimOthers(Solid tool) => Others.Aggregate(tool, (t, o) => NativeBlend.Trim(t, o));
    }

    /// <summary>
    /// <see cref="EndPlane"/>, else the round face (a cylinder) a blend's end vertex runs into at an angle, the only
    /// face there other than the blended ones. Null when there is neither.
    /// </summary>
    internal static EndCut? EndCutAt(NVertex v, Vec3 dirOut, NFace fa, NFace fb, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge>? selected = null)
    {
        if (EndPlane(v, dirOut, fa, fb, blended, selected, out var past) is { } pl) return new EndCut(pl, past);
        var others = new List<NFace>();
        foreach (var face in new[] { fa, fb })
            foreach (var c in face.Coedges)
            {
                var e = c.Edge;
                if (!ReferenceEquals(e.V0, v) && !ReferenceEquals(e.V1, v) || blended.Contains(e) || IsSeam(e)) continue;
                foreach (var u in e.Uses)
                {
                    var f = u.Face;
                    if (ReferenceEquals(f, fa) || ReferenceEquals(f, fb) || f.Surface.CoincidentWith(fa.Surface, 1e-9) || f.Surface.CoincidentWith(fb.Surface, 1e-9)) continue;
                    if (!others.Any(o => o.Surface.CoincidentWith(f.Surface, 1e-9))) others.Add(f);
                }
            }
        if (others.Count != 1 || others[0].Surface.Kind != SurfaceKind.Cylinder) return null;
        var fc = others[0];
        var cos = Math.Abs(Vec3.Dot(fc.Surface.Normal(v.P), dirOut.Normalized));
        if (cos > 0.9999 || cos < 0.05) return null;
        // the side the edge is on, a little back from its end
        var side = fc.Surface.F_(v.P - dirOut.Normalized * 1e-3);
        return Math.Abs(side) < 1e-12 ? null : new EndCut(fc, Math.Sign(side));
    }

    /// <summary>
    /// The plane of the face a blend's end vertex runs into, when that face is planar and meets the edge at an
    /// angle (a face square to the edge already matches the tool's square end). Oriented with the edge next to
    /// the vertex on its positive side. Of several faces there, the one that cuts the blend off first. Null when there
    /// is none.
    /// </summary>
    internal static Plane? EndPlane(NVertex v, Vec3 dirOut, NFace fa, NFace fb, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge>? selected = null)
        => EndPlane(v, dirOut, fa, fb, blended, selected, out _);

    /// <summary><see cref="EndPlane(NVertex, Vec3, NFace, NFace, IEnumerable{NEdge}, IReadOnlyCollection{NEdge}?)"/>,
    /// with the other faces' planes there (oriented the same way).</summary>
    internal static Plane? EndPlane(NVertex v, Vec3 dirOut, NFace fa, NFace fb, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge>? selected, out List<Plane>? others)
    {
        others = null;
        var inside = v.P - dirOut.Normalized * 1e-3;
        // the next edge on one of the faces blended too (round a hole's rim, a box's top): the two meet on the plane
        // bisecting the corner (the next face's plane cuts a hole's corner off, leaving it unblended)
        if (selected != null)
        {
            Plane? miter = null; int count = 0;
            foreach (var face in new[] { fa, fb })
                foreach (var c in face.Coedges)
                {
                    var g = c.Edge;
                    if (!ReferenceEquals(g.V0, v) && !ReferenceEquals(g.V1, v) || blended.Contains(g) || !selected.Contains(g) || IsSeam(g)) continue;
                    var a = -dirOut.Normalized;
                    var b = (ReferenceEquals(g.V0, v) ? g.Curve.Deriv(g.T0) : -g.Curve.Deriv(g.T1)).Normalized;
                    var n = a - b;
                    if (n.Length < 1e-6) continue;
                    // the corner edge the two blends turn round (between the faces they do not share): the plane holds
                    // it, so each ends on it. Square to both edges (a box on a face) that is the bisector's plane;
                    // standing out of a drafted or round face, the bisector's leaves one blend's edge past the corner
                    // (edges running on tangentially: square to them, as before)
                    var shared = g.Uses.Any(u => ReferenceEquals(u.Face, fa)) ? fa : fb;
                    if (CornerEdge(v, shared, new[] { fa, fb }.Concat(g.Uses.Select(w => w.Face)), blended, selected) is { } cd && (a + b).Length > 1e-3 && Vec3.Cross((a + b).Normalized, cd) is var m && m.Length > 1e-3)
                        n = Vec3.Dot(m, a) < 0 ? -m : m;
                    count++;
                    miter = Plane.FromPointNormal(v.P, n.Normalized);
                }
            if (count == 1 && miter != null)
            {
                var cos = Math.Abs(Vec3.Dot(miter.Normal, dirOut.Normalized));
                return cos > 0.9999 ? null : miter;
            }
        }
        var planes = new List<(Plane pl, bool square)>();
        foreach (var face in new[] { fa, fb })
            foreach (var c in face.Coedges)
            {
                var e = c.Edge;
                if (!ReferenceEquals(e.V0, v) && !ReferenceEquals(e.V1, v)) continue;
                if (blended.Contains(e) || IsSeam(e)) continue;
                foreach (var u in e.Uses)
                {
                    var f = u.Face;
                    if (ReferenceEquals(f, fa) || ReferenceEquals(f, fb)) continue;
                    if (f.Surface.CoincidentWith(fa.Surface, 1e-9) || f.Surface.CoincidentWith(fb.Surface, 1e-9)) continue;
                    if (f.Surface.Kind != SurfaceKind.Plane) return null;
                    var n = f.Surface.Plane!.Normal;
                    var cos = Math.Abs(Vec3.Dot(n, dirOut.Normalized));
                    if (cos < 0.05) return null; // a face along the edge
                    var pl = Plane.FromPointNormal(v.P, n);
                    if (pl.Distance(inside) < 0) pl = Plane.FromPointNormal(v.P, -n);
                    if (!planes.Any(q => Math.Abs(Vec3.Dot(q.pl.Normal, pl.Normal) - 1) < 1e-9)) planes.Add((pl, cos > 0.9999));
                }
            }
        if (planes.Count > 1)
        {
            // several faces at the end (a rib's sloping back reaching the underside it stands under, where that ends
            // too): the blend lies in the wedge between its faces, and ends where the first of them cuts that off
            if (Wedge(v, fa, fb, blended) is not { } w) return null;
            planes = planes.OrderBy(q => Vec3.Dot(q.pl.Normal, w)).ToList();
            if (Vec3.Dot(planes[1].pl.Normal, w) - Vec3.Dot(planes[0].pl.Normal, w) < 1e-6) return null;
        }
        // (a face square to the edge: the tool's square end already matches it)
        if (planes.Count == 0 || planes[0].square) return null;
        others = planes.Skip(1).Select(q => q.pl).ToList();
        return planes[0].pl;
    }

    /// <summary>
    /// The direction from the blended edge at v (between fa and fb) into the wedge its blend fills: along both faces,
    /// away from the edge (into the material at a convex edge, out of it at a concave one). Null if no such edge.
    /// </summary>
    static Vec3? Wedge(NVertex v, NFace fa, NFace fb, IEnumerable<NEdge> blended)
    {
        foreach (var c in fa.Coedges)
        {
            var e = c.Edge;
            if (!ReferenceEquals(e.V0, v) && !ReferenceEquals(e.V1, v) || !blended.Contains(e)) continue;
            var cb = e.Uses.FirstOrDefault(u => ReferenceEquals(u.Face, fb) && !ReferenceEquals(u, c));
            if (cb == null) continue;
            var t = ReferenceEquals(e.V0, v) ? e.T0 : e.T1;
            // faces lie to the left of their loops (seen from outside): n × direction points into the face
            var uA = Vec3.Cross(Outward(fa, v.P), Tangent(c, t)).Normalized;
            var uB = Vec3.Cross(Outward(fb, v.P), Tangent(cb, t)).Normalized;
            return uA + uB;
        }
        return null;
    }

    /// <summary>
    /// The direction (away from v) of the one edge at v on the given faces, not on the shared one and neither blended
    /// nor selected: the corner two blends on the shared face turn round. Null when there is not exactly one.
    /// </summary>
    static Vec3? CornerEdge(NVertex v, NFace shared, IEnumerable<NFace> faces, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge> selected)
        => CornerEdgeOf(v, shared, faces, blended, selected) is { } u ? (ReferenceEquals(u.V0, v) ? u.Curve.Deriv(u.T0) : -u.Curve.Deriv(u.T1)).Normalized : null;

    /// <summary>The edge <see cref="CornerEdge"/> gives the direction of.</summary>
    static NEdge? CornerEdgeOf(NVertex v, NFace shared, IEnumerable<NFace> faces, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge> selected)
    {
        NEdge? found = null; int count = 0;
        var seen = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        foreach (var u in faces.SelectMany(f => f.Coedges).Select(c => c.Edge).Where(x => ReferenceEquals(x.V0, v) || ReferenceEquals(x.V1, v)))
        {
            if (!seen.Add(u) || blended.Contains(u) || selected.Contains(u) || IsSeam(u) || u.Uses.Any(w => ReferenceEquals(w.Face, shared))) continue;
            count++;
            found = u;
        }
        return count == 1 ? found : null;
    }

    /// <summary>The part of the tool on the positive side of the plane.</summary>
    internal static Solid Trim(Solid tool, Plane pl)
    {
        var box = tool.Bounds; var pad = box.Diagonal + 1;
        var n = pl.Normal; var origin = pl.Project(box.Center);
        var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
        var rect = ProfileLoop.Polygon(new[] { new Vec2(-pad, -pad), new Vec2(pad, -pad), new Vec2(pad, pad), new Vec2(-pad, pad) }, "trim");
        var slab = Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(origin, x, y, n), -pad, 0, "trim");
        return Csg.Subtract(tool, slab);
    }

    /// <summary>Distances from the edge to where the fillet/chamfer meets face A and face B (as in the profile).</summary>
    static (double a, double b) TangentLengths(Vec2 uA, Vec2 uB, double size, bool fillet, double chamferA, double chamferB)
    {
        if (!fillet) return (chamferA, chamferB);
        var theta = Math.Acos(Math.Clamp(Vec2.Dot(uA.Normalized, uB.Normalized), -1, 1));
        var tl = size / Math.Tan(theta / 2);
        return (tl, tl);
    }

    /// <summary>
    /// The blend must end on the faces it blends (SolidWorks reports such fillets as not fitting): the tangent
    /// point has to lie on the face, or on another face of the body on the same surface (seam-split halves).
    /// </summary>
    /// <summary>Diagnostics of the overflow trimming (tests/tools).</summary>
    public static Action<string>? Debug;

    /// <summary>Use rolling-ball tools for every fillet (tests compare them with the exact profile tools).</summary>
    [ThreadStatic] public static bool ForceRollingBall;

    /// <summary>Verify that blends fit on their faces (off only for tests of the failure path).</summary>
    public static bool CheckFit = true;

    /// <summary>
    /// Whether a tangent point lies on the face it should (or on another face of the same surface, e.g. the
    /// other half of a seam-split cylinder). False means the blend runs past the face: it does not fit.
    /// </summary>
    static bool OnFaces(NFace face, Vec3 p)
    {
        if (!CheckFit) return true;
        const double tol = 1e-6;
        if (Fits(face, p, tol)) return true;
        foreach (var c in face.Coedges)
            foreach (var u in c.Edge.Uses)
                if (!ReferenceEquals(u.Face, face) && u.Face.Surface.CoincidentWith(face.Surface, 1e-9) && Fits(u.Face, p, tol)) return true;
        return false;
    }

    /// <summary>
    /// "Keep edge" overflow: a blend that runs into neighbouring geometry is trimmed by it. The tool is cut to
    /// where it acts (the open space next to a concave edge, the material at a convex edge); of the pieces left,
    /// only those touching the blended edge are kept (pieces beyond an obstacle are not part of the blend).
    /// Null if nothing sensible remains.
    /// </summary>
    static Solid? TrimToEdge(Solid body, List<Solid> tools, bool convex, List<NEdge> edges, IReadOnlyCollection<NFace> blended)
    {
        var tool = tools.Count == 1 ? tools[0] : Csg.UnionAll(tools);
        if (tool.Native == null) return null;
        // obstacles: faces (other than the two blended ones) the tool reaches through. The tool is trimmed by
        // the far side of each, never by the blended faces themselves: the blend surface is tangent to those,
        // and a boolean against them would be degenerate exactly where the obstacle cuts that tangency.
        var cutters = new List<Solid>();
        var box = tool.Bounds;
        var pad = box.Diagonal + 1;
        var samples = tool.Faces.SelectMany(f => f.Loops.SelectMany(l => l)).Distinct().ToList();
        var pts = edges.SelectMany(e => e.Samples(1e-3)).ToList();
        foreach (var f in body.Native!.Faces)
        {
            if (blended.Any(b => ReferenceEquals(f, b) || f.Surface.CoincidentWith(b.Surface, 1e-9))) continue;
            if (!f.Bounds().Overlaps(box, 1e-6)) continue;
            // the side the tool must not reach: the material behind the face (concave blends add material in
            // the open space) or the open space in front of it (convex blends remove material)
            double Side(Vec3 p) => f.Surface.F_(p) * (f.Sense ? 1 : -1) * (convex ? 1 : -1);
            var dom = NDomain.Of(f, 1e-3);
            // an obstacle stands in front of the edge (the edge is on its open side) and the tool reaches behind it
            if (pts.Any(p => Side(p) > 1e-6)) continue;
            if (!samples.Any(p => Side(p) > 1e-4 && dom.Classify(f.Surface.Project(p), 1e-4) >= 0)) continue;
            var cutter = FarSide(f, convex, box, pad);
            Debug?.Invoke($"obstacle {f.Tag.Name} {f.Surface.Kind} sense={f.Sense} cutter={(cutter == null ? "unsupported" : "ok")}");
            if (cutter == null) return null; // obstacle shape not supported: no overflow
            cutters.Add(cutter);
        }
        Solid part = tool;
        if (cutters.Count > 0) part = Csg.Subtract(tool, cutters.Count == 1 ? cutters[0] : Csg.UnionAll(cutters));
        Debug?.Invoke($"trimmed tool: native={part.Native != null} empty={part.IsEmpty} ({Csg.LastFallbackReason})");
        if (part.Native == null || part.IsEmpty) return null;
        var lumps = part.Native.Lumps();
        var kept = lumps.Where(l => Touches(l, pts)).ToList();
        Debug?.Invoke($"lumps {lumps.Count}, touching the edge {kept.Count}");
        if (kept.Count == 0) return null;
        var nb = new NBody();
        foreach (var l in kept) nb.Faces.AddRange(l.Faces);
        nb.Link();
        return NTess.ToSolid(nb);
    }

    /// <summary>
    /// Solid covering the side of face f the blend must not reach, around the tool's box: a slab behind/in front
    /// of a plane, or the solid cylinder of a boss (material inside) or hole (open space inside). Null otherwise.
    /// </summary>
    static Solid? FarSide(NFace f, bool convex, Box3 box, double pad)
    {
        var s = f.Surface;
        // outward normal times (concave: material side = behind, convex: open side = in front)
        var sign = (f.Sense ? 1 : -1) * (convex ? 1 : -1);
        if (s.Kind == SurfaceKind.Plane)
        {
            var n = s.Plane!.Normal;
            var origin = s.Plane.Project(box.Center);
            var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
            var frame = new Frame(origin, x, y, n);
            var rect = ProfileLoop.Polygon(new[] { new Vec2(-pad, -pad), new Vec2(pad, -pad), new Vec2(pad, pad), new Vec2(-pad, pad) }, "ov");
            // the slab lies on the side where F_ * sign > 0, i.e. along n * sign
            return sign > 0
                ? Builders.Extrude(new[] { new ProfileRegion(rect) }, frame, 0, pad, "overflow")
                : Builders.Extrude(new[] { new ProfileRegion(rect) }, frame, -pad, 0, "overflow");
        }
        if (s.Kind == SurfaceKind.Cylinder && sign < 0)
        {
            // the side to remove is inside the cylinder (F_ < 0 there)
            var ax = s.Axis;
            double h0 = double.MaxValue, h1 = double.MinValue;
            for (int c = 0; c < 8; c++)
            {
                var p = new Vec3((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
                var h = Vec3.Dot(p - s.Origin, ax);
                h0 = Math.Min(h0, h); h1 = Math.Max(h1, h);
            }
            return Builders.Cylinder(s.Origin + ax * (h0 - 1), ax, s.R, h1 - h0 + 2, "overflow", s.F.XDir);
        }
        return null;
    }

    /// <summary>Whether a piece of the tool belongs to the edge: some edge sample lies on or inside it (the
    /// tool of a concave blend reaches past the corner, so the edge runs through its interior).</summary>
    static bool Touches(NBody lump, List<Vec3> pts)
    {
        var box = lump.Bounds;
        var doms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
        foreach (var f in lump.Faces) doms[f] = NDomain.Of(f, 1e-3);
        foreach (var p in pts)
        {
            if (!box.Contains(p, 1e-4)) continue;
            foreach (var f in lump.Faces)
                if (Math.Abs(f.Surface.F_(p)) < 1e-5 && doms[f].Classify(f.Surface.Project(p), 1e-4) >= 0) return true;
            if (Inside(lump, doms, p)) return true;
        }
        return false;
    }

    static readonly Vec3[] ProbeDirs =
    {
        new Vec3(0.5773, 0.5774, 0.5775).Normalized, new Vec3(-0.3121, 0.8410, 0.4419).Normalized, new Vec3(0.7071, -0.1213, -0.6965).Normalized,
    };

    /// <summary>
    /// Whether the space a tool reaches into past an edge's corner is taken: material beside a convex edge (the tool,
    /// subtracted, would cut it), open space beside a concave one (added, it would fill it). Only the exact test where
    /// a face other than the edge's two reaches into that space's box (otherwise it is as the two faces leave it).
    /// </summary>
    static bool Taken(NBody body, NFace fa, NFace fb, List<Vec3> pts, bool convex)
    {
        var box = Box3.Empty;
        foreach (var p in pts) box.Add(p);
        if (!body.Faces.Any(f => !ReferenceEquals(f, fa) && !ReferenceEquals(f, fb) && f.Bounds().Overlaps(box, 1e-6))) return false;
        using var _ = Prof.Time("blend.reach");
        body.Link();
        var doms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces) doms[f] = NDomain.Cached(f, 1e-3);
        return pts.Any(p => Inside(body, doms, p) == convex);
    }

    /// <summary>Whether p lies inside the body (ray parity on the exact faces).</summary>
    public static bool PointInside(NBody body, Vec3 p)
    {
        body.Link();
        var doms = new Dictionary<NFace, NDomain>(ReferenceEqualityComparer.Instance);
        foreach (var f in body.Faces) doms[f] = NDomain.Of(f, 1e-3);
        return Inside(body, doms, p);
    }

    /// <summary>Point-in-solid by ray parity; directions that graze a face boundary are skipped.</summary>
    static bool Inside(NBody body, Dictionary<NFace, NDomain> doms, Vec3 p)
    {
        foreach (var d in ProbeDirs)
        {
            int count = 0; bool ok = true;
            foreach (var f in body.Faces)
            {
                foreach (var t in f.Surface.RayHits(p, d))
                {
                    if (t <= 1e-9) continue;
                    var c = doms[f].Classify(p + d * t, 1e-6);
                    if (c == 0) { ok = false; break; }
                    if (c > 0) count++;
                }
                if (!ok) break;
            }
            if (ok) return count % 2 == 1;
        }
        return false;
    }

    static bool Fits(NFace f, Vec3 p, double tol)
    {
        var dom = NDomain.Cached(f, 1e-3);
        return dom.Classify(f.Surface.Project(p), Math.Max(tol, 1e-4)) >= 0;
    }

    static bool SameCircle(NCircle a, NCircle b)
        => Vec3.Distance(a.C, b.C) < 1e-9 && Math.Abs(a.R - b.R) < 1e-9 && Math.Abs(Math.Abs(Vec3.Dot(a.Axis, b.Axis)) - 1) < 1e-12;

    static bool SameFaces(NEdge a, NEdge b)
    {
        var x = new[] { a.Uses[0].Face.Tag.Name, a.Uses[1].Face.Tag.Name }.OrderBy(s => s, StringComparer.Ordinal);
        var y = new[] { b.Uses[0].Face.Tag.Name, b.Uses[1].Face.Tag.Name }.OrderBy(s => s, StringComparer.Ordinal);
        return x.SequenceEqual(y);
    }

    /// <summary>
    /// Whether the edge is convex (the material's angle between its faces under 180°) near point p on it (null: the
    /// faces tangent there, or the edge not between two faces).
    /// </summary>
    static bool? Convex(NEdge e, Vec3 p)
    {
        if (e.Uses.Count != 2) return null;
        var t = e.Curve.Param(p, e.Curve.Periodic ? 0.5 * (e.T0 + e.T1) : null);
        // a little inside the edge (its end may be a vertex where faces are tangent)
        t = Math.Clamp(t, e.T0, e.T1) * 0.999 + 0.0005 * (e.T0 + e.T1);
        var q = e.Curve.Eval(t);
        var cA = e.Uses[0]; var cB = e.Uses[1];
        var nA = Outward(cA.Face, q); var nB = Outward(cB.Face, q);
        var uB = Vec3.Cross(nB, e.Curve.Deriv(t) * (cB.Forward ? 1 : -1));
        if (uB.Length < 1e-12 || Math.Abs(Vec3.Dot(nA, nB)) > 1 - 1e-9) return null;
        return Vec3.Dot(uB.Normalized, nA) < 0;
    }

    /// <summary>
    /// Whether the edge is convex (the material's angle between its faces under 180°) at its middle; null where the
    /// faces are tangent or it is not between two faces.
    /// </summary>
    public static bool? IsConvex(NEdge e) => Convex(e, e.Curve.Eval(0.5 * (e.T0 + e.T1)));

    /// <summary>A seam: both sides are the same named face on the same surface (an artifact of face splitting).</summary>
    public static bool IsSeam(NEdge e)
        => e.Uses.Count == 2 && e.Uses[0].Face.Tag.Name == e.Uses[1].Face.Tag.Name
           && e.Uses[0].Face.Surface.CoincidentWith(e.Uses[1].Face.Surface, 1e-9);

    /// <summary>
    /// Edges reachable from the seeds through vertices where the curves continue tangentially, plus the
    /// other arcs of seam-split circles (always: they are the same model edge).
    /// </summary>
    public static List<NEdge> Expand(NBody body, IEnumerable<NEdge> seeds, bool tangent)
    {
        body.Link();
        var at = new Dictionary<NVertex, List<NEdge>>(ReferenceEqualityComparer.Instance);
        foreach (var e in body.Edges)
        {
            if (IsSeam(e)) continue;
            foreach (var v in new[] { e.V0, e.V1 })
            {
                if (!at.TryGetValue(v, out var l)) at[v] = l = new();
                l.Add(e);
            }
        }
        var result = new List<NEdge>();
        var seen = new HashSet<NEdge>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<NEdge>();
        foreach (var s in seeds) if (seen.Add(s)) { result.Add(s); queue.Enqueue(s); }
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            foreach (var atStart in new[] { true, false })
            {
                var v = atStart ? e.V0 : e.V1;
                var dirOut = atStart ? -e.Curve.Deriv(e.T0).Normalized : e.Curve.Deriv(e.T1).Normalized;
                if (!at.TryGetValue(v, out var others)) continue;
                foreach (var o in others)
                {
                    if (seen.Contains(o)) continue;
                    // the other pieces of a seam-split edge: same named faces on the same surfaces
                    bool sibling = SameFaces(e, o) && (e.Curve is NCircle ec && o.Curve is NCircle oc ? SameCircle(ec, oc) : RollingBall.SameSurfaces(e, o));
                    bool cont = false;
                    // on along a tangent edge of the same kind: not from a convex edge onto a concave one (a drafted
                    // side's top edge running on under an overhang level with the top)
                    if (tangent && !sibling && Convex(e, v.P) == Convex(o, v.P))
                        foreach (var oStart in new[] { true, false })
                        {
                            if (!ReferenceEquals(oStart ? o.V0 : o.V1, v)) continue;
                            var dirIn = oStart ? o.Curve.Deriv(o.T0).Normalized : -o.Curve.Deriv(o.T1).Normalized;
                            if (Vec3.Dot(dirOut, dirIn) > 0.9999) cont = true;
                        }
                    if (sibling || cont) { seen.Add(o); result.Add(o); queue.Enqueue(o); }
                }
            }
        }
        return result;
    }
}

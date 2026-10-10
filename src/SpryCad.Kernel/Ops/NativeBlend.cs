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
            var once = ApplyOne(body, edges, size, fillet, prefix, errs, overflow, probs, app, chamfer, false, trimmedOut);
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
        return ApplyOne(body, edges, size, fillet, prefix, errors, overflow, problemEdges, applied, chamfer, buildOnly, trimmedOut);
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
                        var r2 = RollingBall.Build(tangent, size, name, OnFaces);
                        Debug?.Invoke($"tangent chain {name}: {tangent.Count} edges -> {r2.Error ?? (r2.Fits ? "fits" : "overflows")}");
                        if (r2.Error == null) { rb = r2; foreach (var o in tangent) if (!group.Contains(o)) { group.Add(o); done.Add(o); } }
                        else foreach (var o in tangent) failedChains.Add(o);
                    }
                    if (rb == null)
                    {
                        foreach (var o in Chain(e, edges)) if (!group.Contains(o)) { group.Add(o); done.Add(o); }
                        rb = RollingBall.Build(group, size, name, OnFaces);
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
                try { using (Prof.Time("blend.tools")) rb = RollingBall.Build(chain, size, name0, OnFaces); }
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
                    if (j != k && entries[j].convex == entries[k].convex && entries[j].group.Any(chain.Contains)) par[Find(j)] = Find(k);
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
        if (direct && fillet && !anyTrimmed)
        {
            var lines = entries.Where(x => x.group.Count == 1 && x.group[0].Curve is NLine && x.group[0].Uses.Count == 2
                && x.group[0].Uses.All(u => u.Face.Surface.Kind == SurfaceKind.Plane)).ToList();
            if (lines.Count >= 3)
            {
                List<(Solid piece, bool convex)> pieces;
                Dictionary<NEdge, Solid> shortened;
                using (Prof.Time("blend.corners"))
                    (shortened, pieces) = CornerBlend.Build(lines.Select(x => new CornerBlend.Line(x.group[0], x.convex, x.name)).ToList(), size, prefix);
                for (int k = 0; k < entries.Count; k++)
                    if (entries[k].group.Count == 1 && shortened.TryGetValue(entries[k].group[0], out var sh)) entries[k] = entries[k] with { strip = sh };
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
        foreach (var k in byBoolean) (entries[k].convex ? cutTools : addTools).AddRange(entries[k].tools);
        if (cutTools.Count == 0 && addTools.Count == 0) return result;
        if (byBoolean.Any(k => entries[k].trimmed))
        {
            // trimmed blends of neighbouring edges can touch each other along tangent curves; merged with each
            // other first that contact is non-manifold, so they are applied to the body one at a time instead
            // no slow facet fallback here: a failure is reported right away with the edges involved
            var old = Csg.NoFallback;
            Csg.NoFallback = true;
            try
            {
                using (Prof.Time("blend.apply"))
                {
                    foreach (var t in cutTools) result = Csg.Subtract(result, t);
                    foreach (var t in addTools) result = Csg.Union(result, t);
                }
            }
            catch (InvalidOperationException)
            {
                errors.Add($"the {(fillet ? "fillets" : "chamfers")} that run into neighbouring geometry also run into each other where both are " +
                           "tangent to the same face; that corner cannot be blended yet. Remove one of these edges or use a smaller size.");
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
        var p0 = EndPlane(cA.Start, -t, cA.Face, cB.Face, new[] { e }, selected);
        var p1 = EndPlane(cA.End, t, cA.Face, cB.Face, new[] { e }, selected);
        // (contact points the ends' faces cut off are not the blend's)
        bool Cut(Vec3 x) => p0 != null && p0.Distance(x) < 0 || p1 != null && p1.Distance(x) < 0;
        bool fits = true;
        for (int i = 0; i <= 16 && fits; i++)
        {
            var q = E + t * (L * i / 16);
            Vec3 xa = q + uA * tlA, xb = q + uB * tlB;
            fits = (Cut(xa) || OnFaces(cA.Face, xa)) && (Cut(xb) || OnFaces(cB.Face, xb));
        }
        var main = Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, 0, L, name);
        if (p0 != null) main = Trim(main, p0);
        if (p1 != null) main = Trim(main, p1);
        var tools = new List<Solid> { main };
        if (p0 != null) tools.Add(Trim(Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, -ext, 0, name), p0));
        if (p1 != null) tools.Add(Trim(Builders.Extrude(new[] { new ProfileRegion(loop) }, lframe, L, L + ext, name), p1));
        tools.RemoveAll(x => x.IsEmpty);
        // the tool bounded by the faces (applied directly, or cut back where it runs past them), its ends at an angle extended and cut back
        // to the faces they meet (on those faces then, as its other faces are on the blended ones)
        Solid? strip = null;
        if ((direct || !fits) && EdgeBlend.Profile2D(P(uA), P(uB), P(nA), P(nB), Vec2.Zero, dA, fillet, convex, dB, atCorner: true) is { } sl)
        {
            strip = Builders.Extrude(new[] { new ProfileRegion(sl) }, lframe, p0 != null ? -ext : 0, p1 != null ? L + ext : L, name);
            if (p0 != null) strip = Trim(strip, p0);
            if (p1 != null) strip = Trim(strip, p1);
            if (strip.IsEmpty || strip.Native == null) strip = null;
        }
        return (tools, convex, fits, strip);
    }

    static (List<Solid>? tools, bool convex, bool fits, Solid? strip) ArcTools(List<NEdge> arcs, double size, bool fillet, string name, ChamferSize ch, Func<NFace, NFace, bool> refIsFirst,
        bool direct, NBody body, IReadOnlyList<NEdge> selected)
    {
        var e = arcs[0];
        var cP = e.Uses[0]; var cC = e.Uses[1];
        if (cP.Face.Surface.Kind != SurfaceKind.Plane) (cP, cC) = (cC, cP);
        if (cP.Face.Surface.Kind != SurfaceKind.Plane || cC.Face.Surface.Kind != SurfaceKind.Cylinder) return (null, false, false, null);
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
        var (dP, dC) = fillet ? (size, size) : EdgeBlend.ChamferLegs(uP2, uC2, ch, refIsFirst(cP.Face, cC.Face));
        var prof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(cs.R, h0), dP, fillet, convex, dC);
        if (prof == null) return (null, convex, false, null);
        // the tool bounded by the faces (applied directly, or cut back where it runs past them), revolved over the same arc (none for an
        // arc whose ends meet faces at an angle: those need the extensions)
        var sprof = EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(cs.R, h0), dP, fillet, convex, dC, atCorner: true);
        if (prof.Verts.Any(v => v.P.X < -1e-9)) throw new InvalidOperationException("size too large for this edge");
        var (tlP, tlC) = TangentLengths(uP2, uC2, size, fillet, dP, dC);
        var tlc = Math.Max(tlP, tlC);
        var onPlane = new Vec2(cs.R, h0) + uP2 * tlP;   // (radius, height) of the tangent circle on the plane
        var onCyl = new Vec2(cs.R, h0) + uC2 * tlC;     // ... and on the cylinder

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
                    foreach (var f in new[] { 0.5, 0.95 }) pts.Add(cs.Origin + dir * (cs.R + o2.X * f) + ax * (h0 + o2.Y * f));
                }
            if (Taken(body, cP.Face, cC.Face, pts, convex)
                && EdgeBlend.Profile2D(uP2, uC2, nP2, nC2, new Vec2(cs.R, h0), dP, fillet, convex, dC, atCorner: true) is { } bounded)
                prof = bounded;
        }
        // chain ends meeting a face at an angle (e.g. a keyway wall across a bore): extensions past the ends, each
        // cut back to its face
        var extensions = new List<(double a0, double sweep, Plane plane, int arc)>();
        if (ranges.Sum(r => r.sweep) < 2 * Math.PI - 1e-6)
        {
            var delta = Math.Min(Math.Min(3 * Math.Max(size, tlc) / cs.R, Math.PI / 4), (2 * Math.PI - ranges.Sum(r => r.sweep)) / 2);
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
                    if (delta > 1e-9 && EndPlane(v, dirOut, cP.Face, cC.Face, arcs, selected) is { } pl)
                        extensions.Add((atStart ? a0 - delta : a0 + sweep, delta, pl, i));
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
            var piece = Trim(Builders.Revolve(new[] { new ProfileRegion(prof) }, xf, Vec2.Zero, new Vec2(0, 1), xs, name), pl);
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
        for (int k = 0; k < ranges.Count; k++)
        {
            var (a0, sweep) = ranges[k];
            if (sweep < 1e-9) continue;
            var startDir = refDir * Math.Cos(a0) + yDir * Math.Sin(a0);
            var pframe = new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax));
            var piece = Builders.Revolve(new[] { new ProfileRegion(prof) }, pframe, Vec2.Zero, new Vec2(0, 1), sweep, name);
            // cut back to the faces its ends meet at an angle (as the line tool's)
            foreach (var x in extensions) if (x.arc == k) piece = Trim(piece, x.plane);
            if (!piece.IsEmpty) tools.Add(piece);
        }
        Solid? strip = null;
        if (sprof != null && (direct || !fits) && ranges.Count(r => r.sweep >= 1e-9) == 1)
        {
            // one arc: revolved over it and over its extensions, cut back to the faces its ends meet at an angle
            int k = ranges.FindIndex(r => r.sweep >= 1e-9);
            var (a0, sweep) = ranges[k];
            var mine = extensions.Where(x => x.arc == k).ToList();
            double from = a0, to = a0 + sweep;
            foreach (var x in mine)
            {
                if (Math.Abs(x.a0 + x.sweep - a0) < 1e-9) from = x.a0;   // before the start
                else to = x.a0 + x.sweep;                                // after the end
            }
            var startDir = refDir * Math.Cos(from) + yDir * Math.Sin(from);
            strip = Builders.Revolve(new[] { new ProfileRegion(sprof) }, new Frame(cs.Origin, startDir, ax, Vec3.Cross(startDir, ax)), Vec2.Zero, new Vec2(0, 1), to - from, name);
            foreach (var x in mine) strip = Trim(strip, x.plane);
            if (strip.IsEmpty || strip.Native == null) strip = null;
        }
        return (tools, convex, fits, strip);
    }

    /// <summary>
    /// The plane of the face a blend's end vertex runs into, when that face is planar and meets the edge at an
    /// angle (a face square to the edge already matches the tool's square end). Oriented with the edge next to
    /// the vertex on its positive side. Null when there is no single such face.
    /// </summary>
    internal static Plane? EndPlane(NVertex v, Vec3 dirOut, NFace fa, NFace fb, IEnumerable<NEdge> blended, IReadOnlyCollection<NEdge>? selected = null)
    {
        var inside = v.P - dirOut.Normalized * 1e-3;
        Plane? found = null;
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
                    count++;
                    miter = Plane.FromPointNormal(v.P, n.Normalized);
                }
            if (count == 1 && miter != null)
            {
                var cos = Math.Abs(Vec3.Dot(miter.Normal, dirOut.Normalized));
                return cos > 0.9999 ? null : miter;
            }
        }
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
                    if (cos > 0.9999 || cos < 0.05) return null; // square end, or a face along the edge
                    var pl = Plane.FromPointNormal(v.P, n);
                    if (pl.Distance(inside) < 0) pl = Plane.FromPointNormal(v.P, -n);
                    if (found != null && Math.Abs(Vec3.Dot(found.Normal, pl.Normal) - 1) > 1e-9) return null;
                    found = pl;
                }
            }
        return found;
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

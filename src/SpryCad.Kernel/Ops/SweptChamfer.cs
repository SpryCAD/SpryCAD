using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Chamfer tools for edges the exact tools do not cover (a drafted boss's rounded corners meeting the plate along
/// ellipses, rims on cones or tori): the chamfer's cross-section swept along a chain of edges, built directly as a
/// closed B-rep. At each point of the edge, in the plane normal to it, the legs run along the faces' sections, each
/// ending on the face of the body it lies on (a leg on a side running round a rounded corner crosses from the side
/// onto the corner where it meets the seam between them, a little before or after the edge does). The sweep is cut
/// where the edge changes and where a leg's end crosses a seam (the breakpoints): between them its curves are smooth,
/// planes along straight edges between planes, B-splines through the sections elsewhere.
/// <para>
/// Two tools from the same curves: for the boolean, the triangle of the chamfer line (reaching a little past both
/// legs' ends, so it crosses the faces there, never just touches them) and a point beyond the corner, so only the
/// chamfer face meets the body; for a direct blend (<see cref="DirectBlend"/>), the strip between the edge, the legs'
/// end curves and the chamfer face, its other faces on the body's faces (cut at the seams the legs cross).
/// </para>
/// </summary>
public static class SweptChamfer
{
    /// <summary>The boolean's tool, and the direct blend's (null where it cannot be built).</summary>
    /// <param name="Reach">Points of the space the boolean's tool reaches into past the edge's corner and past the legs'
    /// ends (where that is taken, the strip, bounded by the faces, is the safer tool).</param>
    public sealed record Result(Solid? Tool, bool Convex, bool Fits, string? Error, Solid? Strip = null, List<Vec3>? Reach = null);

    readonly record struct Piece(NEdge E, double Ta, double Tb, NFace A, NFace B);

    /// <summary>The section at a point of the edge: its foot, the legs' ends (on the faces in F), and where.</summary>
    sealed record Sec(Vec3 P, Vec3 D, Vec3[] X, NFace[] F, Vec3 Xc, bool On);

    /// <summary>A breakpoint: a vertex of the chain, or where a leg's end (side) crosses a seam g onto another face.</summary>
    sealed record Brk(int K, double Tau, int Side = -1, NEdge? G = null)
    {
        public Sec S = null!;
    }

    /// <summary>
    /// The tools for a chain of edges (connected end to end; a tangent chain may change faces on either side where
    /// they meet tangentially). legs(faceA, faceB, uA, uB) gives the leg lengths along the two faces for faces
    /// running along uA / uB (in the section plane) from the edge; onFaces(face, p) tells whether a leg's end lies on
    /// the face it should (fit check).
    /// </summary>
    public static Result Build(IReadOnlyList<NEdge> chain, string name, Func<NFace, NFace, Vec3, Vec3, (double a, double b)> legs,
        Func<NFace, Vec3, bool> onFaces)
    {
        static Result Fail(string m) => new(null, false, false, m);
        var order = Order(chain, out var closed);
        if (order == null) return Fail("the edges do not form a single chain.");
        if (order.Any(p => p.e.Uses.Count != 2)) return Fail("the edge is not between two faces.");
        if (closed && order.Count == 1)
        {
            // one closed edge: two pieces (the tool's faces must not wrap around)
            var p = order[0]; var m = 0.5 * (p.ta + p.tb);
            order = new List<(NEdge e, double ta, double tb)> { (p.e, p.ta, m), (p.e, m, p.tb) };
        }
        // side A: a face of the first edge, carried on through the chain (the same surface, else the face beside the
        // previous piece's A face across the joint)
        var pieces = new List<Piece>();
        NFace? prevA = null;
        foreach (var (e, ta, tb) in order)
        {
            var f0 = e.Uses[0].Face; var f1 = e.Uses[1].Face;
            NFace a;
            if (prevA == null) a = f0;
            else if (Same(f0, prevA) || !Same(f1, prevA) && Adjacent(f0, prevA)) a = f0;
            else a = f1;
            pieces.Add(new Piece(e, ta, tb, a, ReferenceEquals(a, f0) ? f1 : f0));
            prevA = a;
        }
        int n = pieces.Count;
        // the faces a leg's end may lie on: the chain's faces on that side
        var sideFaces = new[] { pieces.Select(p => p.A).Distinct().ToList(), pieces.Select(p => p.B).Distinct().ToList() };

        var box = Box3.Empty;
        foreach (var p in pieces) foreach (var q in p.E.Samples(1e-3)) box.Add(q);
        var scale = Math.Max(box.Diagonal, 1);
        var tol = 1e-7 * scale;
        bool fits = true, strippable = true;
        // (the face's plane, the end's vertex, the reach of the extension around it)
        var endPlanes = new List<(Plane pl, Vec3 at, double r)>();
        bool Cut(Vec3 x) => endPlanes.Any(e => e.pl.Distance(x) < 0 && Vec3.Distance(x, e.at) < e.r);
        bool? convex = null;
        string? error = null;
        bool In(NFace f, Vec3 p) => NDomain.Cached(f, 1e-3).Classify(f.Surface.Project(p), 1e-6 * scale) >= 0;

        // ---- the section at piece k, parameter tau (0..1 along it); prefer: the faces to try first
        (Vec3 P, Vec3 D, Vec3[] u, double[] d, Vec3 n2, bool cv, NSurface plane)? Frame(int k, double tau)
        {
            var pc = pieces[k];
            var t = pc.Ta + (pc.Tb - pc.Ta) * tau;
            var P = tau == 0 ? Start(pc) : tau == 1 ? End(pc) : pc.E.Curve.Eval(t);
            var cA = pc.E.Uses.First(u => ReferenceEquals(u.Face, pc.A)); var cB = pc.E.Uses.First(u => ReferenceEquals(u.Face, pc.B));
            var d = (pc.E.Curve.Deriv(t) * (pc.Tb > pc.Ta ? 1 : -1)).Normalized;
            var nA = Outward(pc.A, P); var nB = Outward(pc.B, P);
            // faces lie to the left of their loops (seen from outside): n × direction points into the face
            var uA = Vec3.Cross(nA, d * ((cA.Forward == pc.Tb > pc.Ta) ? 1 : -1)); var uB = Vec3.Cross(nB, d * ((cB.Forward == pc.Tb > pc.Ta) ? 1 : -1));
            if (uA.Length < 1e-9 || uB.Length < 1e-9 || Math.Abs(Vec3.Dot(nA, nB)) > 1 - 1e-9) { error = "the faces meet tangentially."; return null; }
            uA = uA.Normalized; uB = uB.Normalized;
            bool cv = Vec3.Dot(uB, nA) < 0;
            if (convex == null) convex = cv;
            else if (convex != cv) { error = "the edges are convex in places and concave in others: chamfer them separately."; return null; }
            double dA, dB;
            try { (dA, dB) = legs(pc.A, pc.B, uA, uB); }
            catch (InvalidOperationException ex) { error = ex.Message; return null; }
            return (P, d, new[] { uA, uB }, new[] { dA, dB }, (nA + nB).Normalized, cv, NSurface.FromPlane(Plane.FromPointNormal(P, d)));
        }
        Vec3? LegOn(int k, double tau, int side, NFace f)
        {
            var fr = Frame(k, tau);
            return fr == null ? null : Leg(f.Surface, fr.Value.plane, fr.Value.P, fr.Value.u[side], fr.Value.d[side]);
        }
        Sec? Section(int k, double tau, NFace?[]? prefer = null, Vec3?[]? force = null)
        {
            var fr = Frame(k, tau);
            if (fr == null) return null;
            var (P, D, u, d, n2, cv, plane) = fr.Value;
            var X = new Vec3[2]; var F = new NFace[2]; bool on = true;
            for (int s = 0; s < 2; s++)
            {
                var own = s == 0 ? pieces[k].A : pieces[k].B;
                if (force?[s] is { } fp) { X[s] = fp; F[s] = prefer?[s] ?? own; continue; }
                var tryFaces = new List<NFace>();
                if (prefer?[s] is { } pf) tryFaces.Add(pf);
                if (!tryFaces.Contains(own)) tryFaces.Add(own);
                tryFaces.AddRange(sideFaces[s].Where(f => !tryFaces.Contains(f)));
                Vec3? first = null; NFace? found = null;
                foreach (var f in tryFaces)
                {
                    var x = Leg(f.Surface, plane, P, u[s], d[s]);
                    if (x == null) continue;
                    first ??= x;
                    if (In(f, x.Value)) { X[s] = x.Value; F[s] = found = f; break; }
                }
                if (found == null)
                {
                    // on none of them: past the faces (the extended face's point; it does not fit, unless the face an
                    // end of the chain meets cuts it off)
                    var x = Leg(own.Surface, plane, P, u[s], d[s]) ?? first;
                    if (x == null) { error = "a leg runs off the face's curvature: use a smaller size."; return null; }
                    X[s] = x.Value; F[s] = own;
                    if (!Cut(x.Value)) on = false;
                }
                else if (!onFaces(F[s], X[s]) && !Cut(X[s])) on = false;
            }
            if (Vec3.Distance(X[0], X[1]) < 1e-9) { error = "the chamfer is too small."; return null; }
            var Xc = P + n2 * ((cv ? 1 : -1) * Math.Max(d[0], d[1]) * 0.5);
            return new Sec(P, D, X, F, Xc, on);
        }

        // ---- ends of an open chain meeting a face at an angle (a slot's wall across a rim): the tool's square end leans
        // past that face on one side and falls short on the other. The chain goes on a little along its curve (a
        // piece of its own, the faces' surfaces extended), and the tools are cut back to the face
        var virt = new HashSet<int>();
        var ends = new List<(Plane pl, bool atStart, double back)>();
        if (!closed)
            foreach (var atStart in new[] { true, false })
            {
                int k = atStart ? 0 : n - 1;
                var pc = pieces[k];
                var v = atStart ? StartV(pc) : EndV(pc);
                if (v == null || pc.E.Curve is not (NLine or NCircle or NEllipse)) continue;
                var fr = Frame(k, atStart ? 0 : 1);
                if (fr == null) return Fail(error!);
                var dirOut = fr.Value.D * (atStart ? -1 : 1);
                if (NativeBlend.EndPlane(v, dirOut, pc.A, pc.B, chain) is not { } pl) continue;
                var leg = Math.Max(fr.Value.d[0], fr.Value.d[1]);
                endPlanes.Add((pl, v.P, 5 * leg));
                // how far back along the edge the face reaches into the section (it leans by the angle between them)
                var cos = Math.Abs(Vec3.Dot(pl.Normal, dirOut));
                var back = Math.Min(2 * leg * Math.Sqrt(Math.Max(0, 1 - cos * cos)) / cos, 6 * leg);
                ends.Add((pl, atStart, back));
                // far enough past the end for the face to cut the whole section off
                var t = atStart ? pc.Ta : pc.Tb;
                var dt = 3 * Math.Max(fr.Value.d[0], fr.Value.d[1]) / Math.Max(pc.E.Curve.Deriv(t).Length, 1e-12) * Math.Sign(pc.Tb - pc.Ta);
                if (pc.E.Curve is NCircle or NEllipse) dt = Math.Sign(dt) * Math.Min(Math.Abs(dt), Math.PI / 4);
                if (atStart) { pieces.Insert(0, pc with { Ta = t - dt, Tb = t }); virt = virt.Select(x => x + 1).ToHashSet(); virt.Add(0); }
                else { pieces.Add(pc with { Ta = t, Tb = t + dt }); virt.Add(pieces.Count - 1); }
                n = pieces.Count;
            }

        // ---- breakpoints: the chain's vertices, and where a leg's end crosses from one face onto the next
        var brks = new List<Brk>();
        const int scan = 32;
        for (int k = 0; k < n; k++)
        {
            brks.Add(new Brk(k, 0));
            var labels = new NFace[scan + 1][];
            for (int i = 0; i <= scan; i++)
            {
                var s = Section(k, (double)i / scan);
                if (s == null) return Fail(error!);
                labels[i] = s.F;
                if (!s.On && !virt.Contains(k)) fits = false;
            }
            for (int side = 0; side < 2 && !virt.Contains(k); side++)
                for (int i = 0; i < scan; i++)
                {
                    NFace f0 = labels[i][side], f1 = labels[i + 1][side];
                    if (ReferenceEquals(f0, f1)) continue;
                    // the seam between them, and where the leg's end (on f0's surface, extended) crosses it
                    // (the seam may be split into several edges: the one where the leg's end crosses)
                    var xi = LegOn(k, (double)i / scan, side, f0) ?? labels[i][side].Surface.Project(Frame(k, (double)i / scan)!.Value.P);
                    var g = f0.Coedges.Select(c => c.Edge).Where(e => e.Uses.Any(x => ReferenceEquals(x.Face, f1)))
                        .OrderBy(e => e.Samples(1e-3).Min(q => Vec3.Distance(q, xi))).FirstOrDefault();
                    if (g == null) { strippable = false; continue; }
                    double Sigma(double tau)
                    {
                        var x = LegOn(k, tau, side, f0) ?? Vec3.Zero;
                        var gt = g.Curve.Param(x, 0.5 * (g.T0 + g.T1));
                        var gp = g.Curve.Eval(gt);
                        var w = Vec3.Cross(Outward(f0, gp), g.Curve.Deriv(gt));
                        return Vec3.Dot(x - gp, w);
                    }
                    double lo = (double)i / scan, hi = (double)(i + 1) / scan;
                    double slo = Sigma(lo), shi = Sigma(hi);
                    if (Math.Sign(slo) == Math.Sign(shi)) { strippable = false; continue; }
                    for (int it = 0; it < 80 && hi - lo > 1e-15; it++)
                    {
                        var mid = 0.5 * (lo + hi); var sm = Sigma(mid);
                        if (Math.Sign(sm) == Math.Sign(slo)) { lo = mid; slo = sm; } else hi = mid;
                    }
                    brks.Add(new Brk(k, 0.5 * (lo + hi), side, g));
                }
        }
        if (!closed) brks.Add(new Brk(n - 1, 1));
        brks = brks.OrderBy(b => b.K).ThenBy(b => b.Tau).ToList();
        // (a crossing at a vertex of the chain: the vertex)
        for (int i = brks.Count - 1; i > 0; i--)
            if (brks[i].K == brks[i - 1].K && brks[i].Tau - brks[i - 1].Tau < 1e-9) { if (brks[i].Side >= 0) strippable = false; brks.RemoveAt(i); }
        int nb = brks.Count, ni = closed ? nb : nb - 1;
        Debug?.Invoke($"swept {name}: {n} pieces ({string.Join(", ", pieces.Select(p => $"{p.E.Curve.GetType().Name} {p.Ta:g4}..{p.Tb:g4}"))}), closed={closed}, breakpoints {string.Join(" ", brks.Select(b => $"{b.K}:{b.Tau:g4}{(b.Side >= 0 ? "s" + b.Side : "")}"))}");
        int Nx(int b) => closed ? (b + 1) % nb : b + 1;

        // ---- the intervals' faces (at their middles), the sections at the breakpoints (a crossing's on its seam)
        var faces = new NFace[ni][];
        var footMid = new Vec3[ni];
        var reach = new List<Vec3>();
        (int k, double t0, double t1) Span(int i)
        {
            var b0 = brks[i]; var b1 = brks[Nx(i)];
            return (b0.K, b0.Tau, b1.K == b0.K ? b1.Tau : 1);
        }
        for (int i = 0; i < ni; i++)
        {
            var (k, t0, t1) = Span(i);
            var s = Section(k, 0.5 * (t0 + t1));
            if (s == null) return Fail(error!);
            faces[i] = s.F; footMid[i] = s.P;
            foreach (var f in new[] { 0.5, 0.95 }) reach.Add(s.P + (s.Xc - s.P) * f);
            reach.Add(Pt(s, 2)); reach.Add(Pt(s, 3));
        }
        for (int b = 0; b < nb; b++)
        {
            var br = brks[b];
            var after = b < ni ? faces[b] : faces[ni - 1];
            Sec? s;
            if (br.Side >= 0)
            {
                // on the seam: the leg's end on the face before (the same point on the one after)
                var before = faces[closed ? (b + ni - 1) % ni : Math.Max(0, b - 1)];
                var x = LegOn(br.K, br.Tau, br.Side, before[br.Side]);
                if (x == null) return Fail(error!);
                var force = new Vec3?[2]; force[br.Side] = x;
                s = Section(br.K, br.Tau, after, force);
            }
            else s = Section(br.K == n ? n - 1 : br.K, br.Tau, after);
            if (s == null) return Fail(error!);
            br.S = s;
        }

        // ---- per interval: the legs' end curves and the boolean tool's (lines between straight edges and planes,
        // else B-splines through the sections, refined until within tol of them between their points)
        var cur = new (NCurve[] c, BSplineCurve[]? sp, double fit)[ni];
        for (int i = 0; i < ni; i++)
        {
            var (k, t0, t1) = Span(i);
            var s0 = brks[i].S; var s1 = brks[Nx(i)].S;
            var pc = pieces[k];
            static Vec3[] Pts(Sec s) => Enumerable.Range(0, 5).Select(q => Pt(s, q)).ToArray();
            if (pc.E.Curve is NLine && faces[i].All(f => f.Surface.Kind == SurfaceKind.Plane) && pc.A.Surface.Kind == SurfaceKind.Plane && pc.B.Surface.Kind == SurfaceKind.Plane)
            {
                var a = Pts(s0); var b = Pts(s1);
                cur[i] = (Enumerable.Range(0, 5).Select(j => (NCurve)Line(a[j], b[j])).ToArray(), null, 0);
                continue;
            }
            Vec3[]? At(double u, bool keep)
            {
                var s = Section(k, t0 + (t1 - t0) * u, faces[i]);
                if (s == null) return null;
                if (keep && !s.On && !virt.Contains(k)) fits = false;
                return Pts(s);
            }
            BSplineCurve[]? sp = null;
            double fit = 0;
            for (int m = 8; m <= 1024 && sp == null; m *= 2)
            {
                var rows = new List<Vec3[]> { Pts(s0) };
                for (int j = 1; j < m; j++)
                {
                    var r = At((double)j / m, true);
                    if (r == null) return Fail(error!);
                    rows.Add(r);
                }
                rows.Add(Pts(s1));
                var uk = Enumerable.Range(0, m + 1).Select(j => (double)j / m).ToArray();
                var c = Enumerable.Range(0, 5).Select(j => BSplineCurve.Interpolate(rows.Select(r => r[j]).ToList(), uk)).ToArray();
                bool ok = true; fit = 0;
                for (int j = 0; j < m && ok; j++)
                {
                    var u = (j + 0.5) / m;
                    var r = At(u, false);
                    if (r == null) return Fail(error!);
                    for (int q = 0; q < 5; q++) ok &= Vec3.Distance(c[q].Eval(u), r[q]) < tol;
                    // the legs' end curves on their faces
                    for (int q = 0; q < 2; q++) fit = Math.Max(fit, Math.Abs(faces[i][q].Surface.F_(c[q].Eval(u))));
                }
                if (ok) sp = c;
            }
            if (sp == null) return Fail("could not follow the chamfer along the edge.");
            cur[i] = (sp.Select((c, q) => (NCurve)new NSpline(c) { FitTol = q < 2 ? Math.Max(2 * fit, 1e-12 * scale) : 0 }).ToArray(), sp, fit);
        }

        var tool = Assemble(name, brks, cur, closed, ni, Nx, convex!.Value, strip: false, pieces, faces, footMid, out var terr);
        if (tool == null) return Fail("internal: " + terr);
        Solid? stripSolid = null;
        if (strippable && fits)
        {
            stripSolid = Assemble(name, brks, cur, closed, ni, Nx, convex.Value, strip: true, pieces, faces, footMid, out var serr);
            if (stripSolid == null) Debug?.Invoke($"swept chamfer {name}: no strip ({serr})");
        }
        // cut back to the faces the ends meet: what lies behind the face of the extension, and of the chain as far back
        // as the face leans into it (not further: a chain running round a hole comes back past the plane further on)
        foreach (var (pl, atStart, back) in ends)
        {
            var near = new List<Vec3>();
            int kv = atStart ? 0 : n - 1, kr = atStart ? 1 : n - 2;
            foreach (var k in new[] { kv, kr })
            {
                if (k < 0 || k >= n) continue;
                var pc = pieces[k];
                // the real piece: only its end next to the vertex (back along the edge)
                double from = 0, to = 1;
                if (k == kr)
                {
                    var tv = atStart ? pc.Ta : pc.Tb;
                    var frac = Math.Min(1, back / Math.Max(pc.E.Curve.Deriv(tv).Length * Math.Abs(pc.Tb - pc.Ta), 1e-12));
                    (from, to) = atStart ? (0.0, frac) : (1 - frac, 1.0);
                }
                for (int j = 0; j <= 8; j++)
                {
                    var sc = Section(k, from + (to - from) * j / 8, null);
                    if (sc != null) for (int q = 0; q < 5; q++) near.Add(Pt(sc, q));
                }
            }
            tool = TrimNear(tool, pl, near);
            if (tool.IsEmpty) return Fail("the chamfer is cut away by the faces its ends meet.");
            try { if (stripSolid != null) stripSolid = TrimNear(stripSolid, pl, near); }
            catch (InvalidOperationException) { stripSolid = null; }
            if (stripSolid is { IsEmpty: true } or { Native: null }) stripSolid = null;
        }
        return new Result(tool, convex.Value, fits, null, stripSolid, reach);
    }

    /// <summary>Diagnostics (tests/tools).</summary>
    public static Action<string>? Debug;

    /// <summary>
    /// The B-rep: for the boolean, per interval the three ruled faces of the triangle (curves 2, 3, 4: the chamfer
    /// line's ends past the legs, the point beyond the corner), caps at open ends; for the direct blend, per interval
    /// the chamfer face (curves 0, 1), per run of one face of the body on each side the face between the edge, the
    /// seams the legs cross and the leg's end curve, caps at open ends.
    /// </summary>
    static Solid? Assemble(string name, List<Brk> brks, (NCurve[] c, BSplineCurve[]? sp, double fit)[] cur, bool closed, int ni, Func<int, int> Nx, bool convex,
        bool strip, List<Piece> pieces, NFace[][] faces, Vec3[] footMid, out string? err)
    {
        err = null;
        int nb = brks.Count, n = pieces.Count;
        var body = new NBody();
        // vertices at the breakpoints, per curve
        int nc = strip ? 2 : 3;
        int Ci(int q) => strip ? q : q + 2;   // the curve index of the q-th corner
        var V = new NVertex[nb, nc];
        for (int b = 0; b < nb; b++)
            for (int q = 0; q < nc; q++) V[b, q] = new NVertex(Pt(brks[b].S, Ci(q)));
        // the curves along each interval, and the lines across at each breakpoint
        var L = new NEdge[ni, nc];
        for (int i = 0; i < ni; i++)
            for (int q = 0; q < nc; q++)
            {
                var c = cur[i].c[Ci(q)]; var v0 = V[i, q]; var v1 = V[Nx(i), q];
                L[i, q] = c is NLine l ? new NEdge(l, 0, Vec3.Distance(v0.P, v1.P), v0, v1) : new NEdge(c, 0, 1, v0, v1);
            }
        var X = new NEdge[nb, nc];   // across: q -> q + 1 (mod nc)
        for (int b = 0; b < nb; b++)
            for (int q = 0; q < (strip ? 1 : 3); q++) X[b, q] = Seg(V[b, q], V[b, (q + 1) % nc]);

        // the ruled faces: the chamfer (both tools) and, for the boolean's, its two other sides
        for (int i = 0; i < ni; i++)
            for (int q = 0; q < (strip ? 1 : 3); q++)
            {
                int q1 = (q + 1) % nc;
                var e0 = L[i, q]; var e1 = L[i, q1];
                NSurface surf;
                if (cur[i].sp == null)
                {
                    var nrm = Vec3.Cross(e0.V1.P - e0.V0.P, e1.Curve.Eval(0.5 * (e1.T0 + e1.T1)) - e0.Curve.Eval(0.5 * (e0.T0 + e0.T1)));
                    if (nrm.Length < 1e-12) { err = "degenerate chamfer face"; return null; }
                    surf = NSurface.FromPlane(Plane.FromPointNormal(e0.V0.P, nrm.Normalized));
                }
                else
                {
                    var r0 = cur[i].sp![Ci(q)]; var r1 = cur[i].sp![Ci(q1)];
                    var poles = new Vec3[r0.Poles.Length, 2];
                    for (int j = 0; j < r0.Poles.Length; j++) { poles[j, 0] = r0.Poles[j]; poles[j, 1] = r1.Poles[j]; }
                    surf = NSurface.BSpline(new NurbsSurface(r0.Degree, 1, poles, null, r0.Knots, new double[] { 0, 0, 1, 1 }));
                }
                var label = strip || q == 0 ? "F" : q == 1 ? "b" : "a";
                var loop = new List<(NEdge, bool)> { (e0, true), (X[Nx(i), q], true), (e1, false), (X[i, q], false) };
                body.Faces.Add(Oriented(surf, null, new FaceTag($"{name}.S.{label}", surf.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : surf.Def), new() { loop }, Inside(i)));
            }

        // inside the tool, at interval i's middle (orients its faces): the centroid of its section there
        Vec3 Inside(int i)
        {
            Vec3 M(int q) { var c = cur[i].c[q]; return c is NLine ? 0.5 * (Pt(brks[i].S, q) + Pt(brks[Nx(i)].S, q)) : c.Eval(0.5); }
            return strip ? (M(0) + M(1) + footMid[i]) / 3 : (M(2) + M(3) + M(4)) / 3;
        }

        if (!strip)
        {
            if (!closed)
                foreach (var b in new[] { 0, nb - 1 })
                {
                    var loop = new List<(NEdge, bool)> { (X[b, 0], true), (X[b, 1], true), (X[b, 2], true) };
                    var s = brks[b].S;
                    body.Faces.Add(Oriented(NSurface.FromPlane(Plane.FromPointNormal(V[b, 0].P, s.D)), null, new FaceTag($"{name}.End{(b == 0 ? 0 : 1)}", SurfaceDef.PlaneDef), new() { loop },
                        b == 0 ? Inside(0) : Inside(ni - 1)));
                }
            return Finish(body, out err);
        }

        // ---- the strip's other faces, on the body's
        // the chain's vertices (at its breakpoints of the vertices) and edges
        var chainV = new Dictionary<int, NVertex>();   // breakpoint index -> vertex at the foot
        for (int b = 0; b < nb; b++) if (brks[b].Side < 0) chainV[b] = new NVertex(brks[b].S.P);
        var vertexB = new int[n + 1];                     // piece k starts at breakpoint vertexB[k]
        for (int k = 0, b = 0; k < n; k++) { while (!(brks[b].Side < 0 && brks[b].K == k && brks[b].Tau == 0)) b++; vertexB[k] = b; }
        vertexB[n] = closed ? vertexB[0] : nb - 1;
        var chainE = new NEdge[n];
        for (int k = 0; k < n; k++)
        {
            var pc = pieces[k];
            NVertex v0 = chainV[vertexB[k]], v1 = chainV[vertexB[k + 1]];
            chainE[k] = pc.Ta < pc.Tb ? new NEdge(pc.E.Curve, pc.Ta, pc.Tb, v0, v1) : new NEdge(pc.E.Curve, pc.Tb, pc.Ta, v1, v0);
        }
        // a chain vertex by the body's vertex it is at
        int ChainIndex(NVertex bodyV)
        {
            for (int k = 0; k <= n; k++)
            {
                var pc = pieces[Math.Min(k, n - 1)];
                var v = k < n ? StartV(pc) : EndV(pieces[n - 1]);
                if (ReferenceEquals(v, bodyV)) return k % (closed ? n : n + 1);
            }
            return -1;
        }
        // the seams a leg's end crosses: from the chain's vertex to the crossing, along the edges between the two faces
        // (a seam split into several: the whole ones up to the one crossed, and that one's part)
        var seam = new Dictionary<int, (List<(NEdge e, bool up)> path, int k)>();
        for (int b = 0; b < nb; b++)
        {
            var br = brks[b];
            if (br.Side < 0) continue;
            var g = br.G!;
            var common = g.Uses[0].Face.Coedges.Select(c => c.Edge).Where(e => e.Uses.Any(x => ReferenceEquals(x.Face, g.Uses[1].Face))).ToList();
            // from g down to the chain: through its ends, edge by edge
            List<NEdge>? down = null; NVertex? join = null; int k = -1;
            foreach (var start in new[] { g.V0, g.V1 })
            {
                var path = new List<NEdge>(); var w = start; var at = g;
                for (int guard = 0; guard < 64; guard++)
                {
                    if ((k = ChainIndex(w)) >= 0) break;
                    var next = common.FirstOrDefault(e => !ReferenceEquals(e, at) && !path.Contains(e) && (ReferenceEquals(e.V0, w) || ReferenceEquals(e.V1, w)));
                    if (next == null) break;
                    path.Add(next); at = next;
                    w = ReferenceEquals(next.V0, w) ? next.V1 : next.V0;
                }
                if (k >= 0) { down = path; join = start; break; }
            }
            if (down == null || join == null) { err = "a seam the chamfer crosses does not reach the chain"; return null; }
            // up from the chain's vertex: the whole edges (new vertices at their joints), then g to the crossing
            var list = new List<(NEdge e, bool up)>();
            var vFoot = chainV[vertexB[k]];
            var map = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
            NVertex Mv(NVertex x) => map.TryGetValue(x, out var y) ? y : map[x] = new NVertex(x.P);
            down.Reverse();
            NVertex? lower = null;
            for (int j = 0; j < down.Count; j++)
            {
                var e = down[j];
                // its end toward the chain: the one it shares with the next edge down (or the chain's vertex)
                var below = j == 0 ? (ChainIndex(e.V0) == k ? e.V0 : e.V1) : lower!;
                var above = ReferenceEquals(e.V0, below) ? e.V1 : e.V0;
                NVertex nb0 = j == 0 ? vFoot : Mv(below), nb1 = Mv(above);
                bool upFwd = ReferenceEquals(e.V0, below);
                list.Add((upFwd ? new NEdge(e.Curve, e.T0, e.T1, nb0, nb1) : new NEdge(e.Curve, e.T0, e.T1, nb1, nb0), upFwd));
                lower = above;
            }
            var vJoin = down.Count == 0 ? vFoot : Mv(join);
            var tq = g.Curve.Param(V[b, br.Side].P, 0.5 * (g.T0 + g.T1));
            list.Add(ReferenceEquals(g.V0, join) ? (new NEdge(g.Curve, g.T0, tq, vJoin, V[b, br.Side]), true) : (new NEdge(g.Curve, tq, g.T1, V[b, br.Side], vJoin), false));
            seam[b] = (list, k);
        }
        // across at a breakpoint of the chain's vertices (an open end, or where a band on a surface that closes on
        // itself is cut): from the foot to the leg's end, in the section plane
        var across = new Dictionary<(int b, int side), NEdge>();
        NEdge Across(int b, int side)
        {
            if (across.TryGetValue((b, side), out var e)) return e;
            var s = brks[b].S; var f = faces[b < ni ? b : ni - 1][side];
            var foot = chainV[b];
            return across[(b, side)] = f.Surface.Kind == SurfaceKind.Plane ? Seg(foot, V[b, side])
                : new NEdge(new NSection(f.Surface, NSurface.FromPlane(Plane.FromPointNormal(s.P, s.D)), s.P, s.X[side]), 0, 1, foot, V[b, side]);
        }
        // the chain's vertex a bound comes down to
        int Foot(int b) => seam.TryGetValue(b, out var se) ? se.k : !closed && b == nb - 1 ? n : Array.IndexOf(vertexB, b, 0, n);

        for (int side = 0; side < 2; side++)
        {
            // runs of intervals on one face, between the seams the leg's end crosses
            var bounds = Enumerable.Range(0, nb).Where(b => brks[b].Side == side).ToList();
            if (closed)
            {
                bool periodic = Enumerable.Range(0, ni).Any(i => faces[i][side].Surface.PeriodicU || faces[i][side].Surface.PeriodicV);
                if (bounds.Count == 0 && !periodic)
                {
                    // all round on one face: a band between the chain and the leg's end curve
                    var f = faces[0][side];
                    var inner = Enumerable.Range(0, n).Select(k => (chainE[k], pieces[k].Ta < pieces[k].Tb)).ToList();
                    var outer = Enumerable.Range(0, ni).Select(i => (L[i, side], true)).ToList();
                    body.Faces.Add(Oriented(f.Surface, f, new FaceTag($"{name}.S.{(side == 0 ? "a" : "b")}", f.Surface.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : f.Surface.Def),
                        new() { inner, outer }, null, convex));
                    continue;
                }
                // (a band on a surface closing on itself would wrap round it: cut across at the chain's vertices too)
                if (bounds.Count < 2) bounds = bounds.Concat(Enumerable.Range(0, nb).Where(b => brks[b].Side < 0)).Distinct().OrderBy(b => b).ToList();
            }
            else bounds = new List<int> { 0 }.Concat(bounds).Append(nb - 1).ToList();
            for (int r = 0; r < (closed ? bounds.Count : bounds.Count - 1); r++)
            {
                int bs = bounds[r], be = bounds[(r + 1) % bounds.Count];
                var loop = new List<(NEdge, bool)>();
                // along the leg's end curve, from bs to be
                for (int i = bs; i != be; i = Nx(i)) loop.Add((L[i, side], true));
                // down to the chain at be, back along it to bs's foot, up at bs
                if (seam.TryGetValue(be, out var se)) for (int j = se.path.Count - 1; j >= 0; j--) loop.Add((se.path[j].e, !se.path[j].up));
                else loop.Add((Across(be, side), false));
                int ke = Foot(be), ks = Foot(bs);
                if (ke < 0 || ks < 0) { err = "a bound off the chain"; return null; }
                for (int k = ke; k != ks;)
                {
                    k = closed ? (k + n - 1) % n : k - 1;
                    loop.Add((chainE[k], !(pieces[k].Ta < pieces[k].Tb)));
                }
                if (seam.TryGetValue(bs, out var sb)) foreach (var x in sb.path) loop.Add((x.e, x.up));
                else loop.Add((Across(bs, side), true));
                var f = faces[bs < ni ? bs : ni - 1][side];
                body.Faces.Add(Oriented(f.Surface, f, new FaceTag($"{name}.S.{(side == 0 ? "a" : "b")}", f.Surface.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : f.Surface.Def),
                    new() { loop }, null, convex));
            }
        }
        if (!closed)
            foreach (var b in new[] { 0, nb - 1 })
            {
                var loop = new List<(NEdge, bool)> { (Across(b, 0), true), (X[b, 0], true), (Across(b, 1), false) };
                var s = brks[b].S;
                body.Faces.Add(Oriented(NSurface.FromPlane(Plane.FromPointNormal(s.P, s.D)), null, new FaceTag($"{name}.End{(b == 0 ? 0 : 1)}", SurfaceDef.PlaneDef), new() { loop },
                    b == 0 ? Inside(0) : Inside(ni - 1)));
            }
        return Finish(body, out err);
    }

    /// <summary>A section's corner q: the legs' ends (0, 1), the chamfer line's ends past them (2, 3), the point beyond the corner (4).</summary>
    static Vec3 Pt(Sec s, int q)
    {
        var ab = (s.X[1] - s.X[0]).Normalized; var ext = 0.2 * Vec3.Distance(s.X[0], s.X[1]);
        return q switch { 0 => s.X[0], 1 => s.X[1], 2 => s.X[0] - ab * ext, 3 => s.X[1] + ab * ext, _ => s.Xc };
    }

    /// <summary>The tool less what lies behind the plane over the points (a prism on the plane round them).</summary>
    static Solid TrimNear(Solid tool, Plane pl, List<Vec3> near)
    {
        var n = pl.Normal; var origin = pl.Project(near[0]);
        var x = n.AnyPerpendicular(); var y = Vec3.Cross(n, x);
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue, z0 = 0;
        foreach (var p in near)
        {
            var r = p - origin;
            x0 = Math.Min(x0, Vec3.Dot(r, x)); x1 = Math.Max(x1, Vec3.Dot(r, x));
            y0 = Math.Min(y0, Vec3.Dot(r, y)); y1 = Math.Max(y1, Vec3.Dot(r, y));
            z0 = Math.Min(z0, pl.Distance(p));
        }
        var pad = 1e-3 * Math.Max(1, Math.Max(x1 - x0, y1 - y0));
        var rect = ProfileLoop.Polygon(new[] { new Vec2(x0 - pad, y0 - pad), new Vec2(x1 + pad, y0 - pad), new Vec2(x1 + pad, y1 + pad), new Vec2(x0 - pad, y1 + pad) }, "trim");
        var block = Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(origin, x, y, n), z0 - pad, 0, "trim");
        return Csg.Subtract(tool, block);
    }

    static Solid? Finish(NBody body, out string? err)
    {
        body.Link();
        err = body.Validate();
        return err == null ? NTess.ToSolid(body) : null;
    }

    /// <summary>
    /// A face of the tool on surface s, its loops oriented counter-clockwise about its outward normal (the outer one;
    /// inner ones clockwise). Outward: away from the point inside the tool; on a face of the body (on), as the body's
    /// face for a convex blend (the tool is material) and against it for a concave one.
    /// </summary>
    static NFace Oriented(NSurface s, NFace? on, FaceTag tag, List<List<(NEdge e, bool fwd)>> loops, Vec3? inside, bool convex = false)
    {
        var pts = loops.Select(l => l.SelectMany(x => Pts(x.e, x.fwd)).ToList()).ToList();
        var mid = pts[0][pts[0].Count / 2];
        bool sense;
        if (on != null) sense = convex ? on.Sense : !on.Sense;
        else
        {
            var c = pts.SelectMany(p => p).Aggregate(Vec3.Zero, (a, b) => a + b) / pts.Sum(p => p.Count);
            var q = s.Project(c);
            sense = Vec3.Dot(s.Normal(q), q - inside!.Value) >= 0;
        }
        var areas = pts.Select(p => Newell(p)).ToList();
        var nT = s.Normal(s.Project(mid)) * (sense ? 1 : -1);
        // the outer loop: the largest
        int outer = Enumerable.Range(0, loops.Count).OrderByDescending(i => areas[i].Length).First();
        var f = new NFace(s, sense, tag, s.Project(mid));
        for (int i = 0; i < loops.Count; i++)
        {
            var l = loops[i];
            var nl = Vec3.Dot(areas[i], nT) >= 0 == (i == outer) ? l : Reverse(l);
            var loop = new NLoop();
            foreach (var (e, fwd) in nl) loop.Coedges.Add(new NCoedge(e, fwd));
            f.Loops.Add(loop);
        }
        return f;
    }

    static IEnumerable<Vec3> Pts(NEdge e, bool fwd)
    {
        const int m = 8;
        for (int j = 0; j < m; j++)
        {
            var u = fwd ? (double)j / m : 1 - (double)j / m;
            yield return e.Curve.Eval(e.T0 + (e.T1 - e.T0) * u);
        }
    }

    static Vec3 Newell(List<Vec3> p)
    {
        var a = Vec3.Zero;
        for (int i = 0; i < p.Count; i++) a += Vec3.Cross(p[i], p[(i + 1) % p.Count]);
        return a * 0.5;
    }

    static Vec3 Start(Piece p) => p.Ta == p.E.T0 ? p.E.V0.P : p.Ta == p.E.T1 ? p.E.V1.P : p.E.Curve.Eval(p.Ta);
    static Vec3 End(Piece p) => p.Tb == p.E.T0 ? p.E.V0.P : p.Tb == p.E.T1 ? p.E.V1.P : p.E.Curve.Eval(p.Tb);
    static NVertex? StartV(Piece p) => p.Ta == p.E.T0 ? p.E.V0 : p.Ta == p.E.T1 ? p.E.V1 : null;
    static NVertex? EndV(Piece p) => p.Tb == p.E.T0 ? p.E.V0 : p.Tb == p.E.T1 ? p.E.V1 : null;

    static NEdge Seg(NVertex a, NVertex b) => new(new NLine(a.P, b.P - a.P), 0, Vec3.Distance(a.P, b.P), a, b);

    static NLine Line(Vec3 a, Vec3 b) => new(a, b - a);

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    static bool Same(NFace f, NFace g) => ReferenceEquals(f, g) || f.Surface.CoincidentWith(g.Surface, 1e-9) && f.Sense == g.Sense;

    static bool Adjacent(NFace f, NFace g) => f.Coedges.Any(c => c.Edge.Uses.Any(u => ReferenceEquals(u.Face, g)));

    /// <summary>
    /// The point of surface s in the section plane at (chord) distance d from P, starting along u: exact on a plane,
    /// by iteration on a curved face. Null when there is none near (the face curves away within d).
    /// </summary>
    static Vec3? Leg(NSurface s, NSurface section, Vec3 P, Vec3 u, double d)
    {
        var q = P + u * d;
        if (s.Kind == SurfaceKind.Plane)
        {
            // on the plane through P (a face on the edge's own plane, or a face beside it tangent there)
            if (Math.Abs(s.F_(P)) < 1e-9 * Math.Max(1, d)) return q;
        }
        for (int it = 0; it < 100; it++)
        {
            var p = NSurface.ProjectToIntersection(s, section, q);
            var r = p - P;
            if (r.Length < 1e-12 || Vec3.Dot(r, u) <= 0) return null;
            var nq = P + r.Normalized * d;
            var moved = Vec3.Distance(nq, q);
            q = nq;
            if (moved < 1e-13 * Math.Max(1, d)) break;
        }
        var res = NSurface.ProjectToIntersection(s, section, q);
        return Math.Abs(Vec3.Distance(res, P) - d) < 1e-7 * Math.Max(1, d) ? res : null;
    }

    static List<(NEdge, bool)> Reverse(List<(NEdge e, bool f)> loop)
    {
        var r = loop.Select(x => (x.e, !x.f)).ToList();
        r.Reverse();
        return r;
    }

    /// <summary>Orders the edges into a chain (traversed head to tail); null if they branch or are disjoint.</summary>
    static List<(NEdge e, double ta, double tb)>? Order(IReadOnlyList<NEdge> edges, out bool closed)
    {
        closed = false;
        var deg = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        foreach (var e in edges)
        {
            deg[e.V0] = deg.GetValueOrDefault(e.V0) + 1;
            deg[e.V1] = deg.GetValueOrDefault(e.V1) + 1;
        }
        if (deg.Values.Any(d => d > 2)) return null;
        var ends = deg.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
        var remaining = edges.ToList();
        NVertex cur;
        if (ends.Count == 0) { closed = true; cur = edges[0].V0; }
        else if (ends.Count == 2) cur = ends[0];
        else return null;
        var res = new List<(NEdge, double, double)>();
        var e1 = closed ? edges[0] : remaining.First(x => ReferenceEquals(x.V0, cur) || ReferenceEquals(x.V1, cur));
        while (e1 != null)
        {
            remaining.Remove(e1);
            bool fwd = ReferenceEquals(e1.V0, cur);
            res.Add(fwd ? (e1, e1.T0, e1.T1) : (e1, e1.T1, e1.T0));
            cur = fwd ? e1.V1 : e1.V0;
            e1 = remaining.FirstOrDefault(x => ReferenceEquals(x.V0, cur) || ReferenceEquals(x.V1, cur));
        }
        return remaining.Count > 0 ? null : res;
    }
}

using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Chamfer tools for edges the exact tools do not cover (a drafted boss's rounded corners meeting the plate along
/// ellipses, rims on cones or tori): the chamfer's cross-section swept along a chain of edges, built directly as a
/// closed B-rep. At each point of the edge, in the plane normal to it, the legs run along the two faces' sections; the
/// tool's cross-section is the triangle of the chamfer line, reaching a little past both legs' ends, and a point beyond
/// the corner (as the line tool's), so only the chamfer face meets the body, crossing its faces. Its faces are ruled:
/// planes along straight edges between planes, B-splines through the sections elsewhere.
/// </summary>
public static class SweptChamfer
{
    public sealed record Result(Solid? Tool, bool Convex, bool Fits, string? Error);

    readonly record struct Piece(NEdge E, double Ta, double Tb, NFace A, NFace B);

    /// <summary>
    /// The tool for a chain of edges (connected end to end; a tangent chain may change faces on either side where
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
        var facesA = pieces.Select(p => p.A).Distinct().ToList();
        var facesB = pieces.Select(p => p.B).Distinct().ToList();

        var box = Box3.Empty;
        foreach (var p in pieces) foreach (var q in p.E.Samples(1e-3)) box.Add(q);
        var scale = Math.Max(box.Diagonal, 1);
        var tol = 1e-7 * scale;

        // the section at edge parameter t of a piece: the leg ends on A and B, the point beyond the corner
        bool fits = true;
        bool? convex = null;
        string? error = null;
        (Vec3 a, Vec3 b, Vec3 x)? Section(Piece pc, double t, bool check)
        {
            var P = pc.E.Curve.Eval(t);
            var cA = pc.E.Uses.First(u => ReferenceEquals(u.Face, pc.A)); var cB = pc.E.Uses.First(u => ReferenceEquals(u.Face, pc.B));
            var d = pc.E.Curve.Deriv(t).Normalized;
            var nA = Outward(pc.A, P); var nB = Outward(pc.B, P);
            // faces lie to the left of their loops (seen from outside): n × direction points into the face
            var uA = Vec3.Cross(nA, d * (cA.Forward ? 1 : -1)); var uB = Vec3.Cross(nB, d * (cB.Forward ? 1 : -1));
            if (uA.Length < 1e-9 || uB.Length < 1e-9 || Math.Abs(Vec3.Dot(nA, nB)) > 1 - 1e-9) { error = "the faces meet tangentially."; return null; }
            uA = uA.Normalized; uB = uB.Normalized;
            bool cv = Vec3.Dot(uB, nA) < 0;
            if (convex == null) convex = cv;
            else if (convex != cv) { error = "the edges are convex in places and concave in others: chamfer them separately."; return null; }
            double dA, dB;
            try { (dA, dB) = legs(pc.A, pc.B, uA, uB); }
            catch (InvalidOperationException ex) { error = ex.Message; return null; }
            var section = NSurface.FromPlane(Plane.FromPointNormal(P, d));
            var a = Leg(pc.A.Surface, section, P, uA, dA); var b = Leg(pc.B.Surface, section, P, uB, dB);
            if (a == null || b == null) { error = "a leg runs off the face's curvature: use a smaller size."; return null; }
            if (check && fits) fits = facesA.Any(f => onFaces(f, a.Value)) && facesB.Any(f => onFaces(f, b.Value));
            // the chamfer line reaching past both ends (it crosses the faces there, never just touches them), and a
            // point beyond the corner: outside for cuts, inside for additions (only the chamfer face acts on the body)
            var ab = b.Value - a.Value; var len = ab.Length;
            if (len < 1e-9) { error = "the chamfer is too small."; return null; }
            ab /= len;
            var ext = 0.2 * Math.Max(dA, dB);
            var outDir = (nA + nB).Normalized;
            var X = P + outDir * ((cv ? 1 : -1) * Math.Max(dA, dB) * 0.5);
            return (a.Value - ab * ext, b.Value + ab * ext, X);
        }

        // per piece: the section curves (exact lines along a straight edge between planes, else B-splines through
        // sections, refined until they stay within tol of the exact sections between their points)
        int n = pieces.Count, nj = closed ? n : n + 1;
        bool Flat(Piece pc) => pc.E.Curve is NLine && pc.A.Surface.Kind == SurfaceKind.Plane && pc.B.Surface.Kind == SurfaceKind.Plane;
        // the sections at the joints, shared by the pieces on both sides: a straight piece's (on its planes) if
        // there is one, as its section curves are the lines between its ends
        var ends = new (Vec3 a, Vec3 b, Vec3 x)[nj];
        for (int j = 0; j < nj; j++)
        {
            Piece? before = j > 0 ? pieces[j - 1] : closed ? pieces[n - 1] : null;
            Piece? after = j < n ? pieces[j] : null;
            var s = before is { } pb && (after == null || Flat(pb)) ? Section(pb, pb.Tb, true) : Section(after!.Value, after.Value.Ta, true);
            if (s == null) return Fail(error!);
            ends[j] = s.Value;
        }
        var curves = new (NCurve a, NCurve b, NCurve x, BSplineCurve[]? splines)[n];
        for (int k = 0; k < n; k++)
        {
            var pc = pieces[k];
            var e0 = ends[k]; var e1 = ends[closed ? (k + 1) % n : k + 1];
            if (Flat(pc))
            {
                if (Section(pc, 0.5 * (pc.Ta + pc.Tb), true) == null) return Fail(error!);
                curves[k] = (Line(e0.a, e1.a), Line(e0.b, e1.b), Line(e0.x, e1.x), null);
                continue;
            }
            // its own sections at its ends: where a face beside it changes at a joint (a side running on round a
            // rounded corner), a leg's end on the extended face differs a little from the neighbour's; the difference
            // is spread along the piece, so its curves meet the joint's section and stay smooth
            var o0 = Section(pc, pc.Ta, true); var o1 = Section(pc, pc.Tb, true);
            if (o0 == null || o1 == null) return Fail(error!);
            (Vec3 a, Vec3 b, Vec3 x) d0 = (e0.a - o0.Value.a, e0.b - o0.Value.b, e0.x - o0.Value.x);
            (Vec3 a, Vec3 b, Vec3 x) d1 = (e1.a - o1.Value.a, e1.b - o1.Value.b, e1.x - o1.Value.x);
            (Vec3 a, Vec3 b, Vec3 x)? At(double u, bool check)
            {
                var s = Section(pc, pc.Ta + (pc.Tb - pc.Ta) * u, check);
                if (s == null) return null;
                return (s.Value.a + d0.a * (1 - u) + d1.a * u, s.Value.b + d0.b * (1 - u) + d1.b * u, s.Value.x + d0.x * (1 - u) + d1.x * u);
            }
            BSplineCurve[]? sp = null;
            for (int m = 16; m <= 1024 && sp == null; m *= 2)
            {
                var sec = new List<(Vec3 a, Vec3 b, Vec3 x)> { e0 };
                for (int i = 1; i < m; i++)
                {
                    var s = At((double)i / m, true);
                    if (s == null) return Fail(error!);
                    sec.Add(s.Value);
                }
                sec.Add(e1);
                var uk = Enumerable.Range(0, m + 1).Select(i => (double)i / m).ToArray();
                var c = new[]
                {
                    BSplineCurve.Interpolate(sec.Select(s => s.a).ToList(), uk), BSplineCurve.Interpolate(sec.Select(s => s.b).ToList(), uk),
                    BSplineCurve.Interpolate(sec.Select(s => s.x).ToList(), uk),
                };
                bool ok = true;
                for (int i = 0; i < m && ok; i++)
                {
                    var u = (i + 0.5) / m;
                    var s = At(u, false);
                    if (s == null) return Fail(error!);
                    ok = Vec3.Distance(c[0].Eval(u), s.Value.a) < tol && Vec3.Distance(c[1].Eval(u), s.Value.b) < tol && Vec3.Distance(c[2].Eval(u), s.Value.x) < tol;
                }
                if (ok) sp = c;
            }
            if (sp == null) return Fail("could not follow the chamfer along the edge.");
            curves[k] = (new NSpline(sp[0]), new NSpline(sp[1]), new NSpline(sp[2]), sp);
        }

        // ---- topology: per joint the section's vertices and edges, per piece three ruled faces
        var VA = new NVertex[nj]; var VB = new NVertex[nj]; var VX = new NVertex[nj];
        var AB = new NEdge[nj]; var BX = new NEdge[nj]; var XA = new NEdge[nj];
        for (int j = 0; j < nj; j++)
        {
            var (a, b, x) = ends[j];
            VA[j] = new NVertex(a); VB[j] = new NVertex(b); VX[j] = new NVertex(x);
            AB[j] = Seg(VA[j], VB[j]); BX[j] = Seg(VB[j], VX[j]); XA[j] = Seg(VX[j], VA[j]);
        }
        int W(int j) => closed ? j % n : j;
        var body = new NBody();
        for (int k = 0; k < n; k++)
        {
            int j0 = W(k), j1 = W(k + 1);
            var (ca, cb, cx, sp) = curves[k];
            NEdge Long(NCurve c, NVertex v0, NVertex v1) => c is NLine l
                ? new NEdge(l, 0, Vec3.Distance(v0.P, v1.P), v0, v1)
                : new NEdge(c, 0, 1, v0, v1);
            var ea = Long(ca, VA[j0], VA[j1]); var eb = Long(cb, VB[j0], VB[j1]); var ex = Long(cx, VX[j0], VX[j1]);
            // the tool's inside, at the middle of the piece: the section triangle's centroid
            var cm = (ca.Eval(Mid(ca, ea)) + cb.Eval(Mid(cb, eb)) + cx.Eval(Mid(cx, ex))) / 3;
            foreach (var (e0, e1, s0, s1, c0, c1, label) in new[]
                     {
                         (ea, eb, AB[j0], AB[j1], 0, 1, "F"), (eb, ex, BX[j0], BX[j1], 1, 2, "b"), (ex, ea, XA[j0], XA[j1], 2, 0, "a"),
                     })
            {
                // ruled between the two section curves: a plane, or a B-spline (degree 1 across)
                NSurface surf;
                var q0 = e0.Curve.Eval(Mid(e0.Curve, e0)); var q1 = e1.Curve.Eval(Mid(e1.Curve, e1));
                if (sp == null)
                {
                    var nrm = Vec3.Cross(e0.V1.P - e0.V0.P, q1 - q0);
                    if (nrm.Length < 1e-12) return Fail("internal: degenerate chamfer face.");
                    surf = NSurface.FromPlane(Plane.FromPointNormal(q0, nrm.Normalized));
                }
                else
                {
                    var r0 = sp[c0]; var r1 = sp[c1];
                    var poles = new Vec3[r0.Poles.Length, 2];
                    for (int i = 0; i < r0.Poles.Length; i++) { poles[i, 0] = r0.Poles[i]; poles[i, 1] = r1.Poles[i]; }
                    surf = NSurface.BSpline(new NurbsSurface(r0.Degree, 1, poles, null, r0.Knots, new double[] { 0, 0, 1, 1 }));
                }
                var qm = (q0 + q1) * 0.5;
                // outward: away from the tool's inside
                var nOut = Vec3.Dot(surf.Normal(qm), qm - cm) >= 0;
                var nT = surf.Normal(qm) * (nOut ? 1 : -1);
                var loop = new List<(NEdge, bool)> { (e0, true), (s1, true), (e1, false), (s0, false) };
                // faces lie to the left of their loops seen from outside
                if (Vec3.Dot(Vec3.Cross(nT, e0.V1.P - e0.V0.P), q1 - q0) < 0) loop = Reverse(loop);
                body.Faces.Add(MakeFace(surf, nOut, new FaceTag($"{name}.S.{label}", surf.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : surf.Def), loop, qm));
            }
        }
        if (!closed)
            foreach (var (j, sign) in new[] { (0, -1.0), (n, 1.0) })
            {
                var (a, b, x) = ends[j];
                var nrm = Vec3.Cross(b - a, x - a).Normalized;
                var pc = j == 0 ? pieces[0] : pieces[n - 1];
                var tdir = (pc.E.Curve.Deriv(j == 0 ? pc.Ta : pc.Tb) * (pc.Tb > pc.Ta ? 1 : -1)).Normalized * sign;
                var loop = new List<(NEdge, bool)> { (AB[j], true), (BX[j], true), (XA[j], true) };
                if (Vec3.Dot(nrm, tdir) < 0) loop = Reverse(loop);
                body.Faces.Add(MakeFace(NSurface.FromPlane(Plane.FromPointNormal(a, tdir)), true, new FaceTag($"{name}.End{(j == 0 ? 0 : 1)}", SurfaceDef.PlaneDef), loop, (a + b + x) / 3));
            }
        body.Link();
        var err = body.Validate();
        if (err != null) return Fail("internal: " + err);
        var solid = NTess.ToSolid(body);
        return new Result(solid, convex!.Value, fits, null);
    }

    static double Mid(NCurve c, NEdge e) => 0.5 * (e.T0 + e.T1);

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
        if (s.Kind == SurfaceKind.Plane) return q;
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

    static NFace MakeFace(NSurface s, bool sense, FaceTag tag, List<(NEdge e, bool fwd)> loop, Vec3? anchor)
    {
        var f = new NFace(s, sense, tag, anchor);
        var l = new NLoop();
        foreach (var (e, fwd) in loop) l.Coedges.Add(new NCoedge(e, fwd));
        f.Loops.Add(l);
        return f;
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

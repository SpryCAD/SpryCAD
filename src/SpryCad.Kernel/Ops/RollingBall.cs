using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Rolling-ball fillet tools for edges between any two surfaces. The ball centre runs along the intersection of
/// the two faces' offset surfaces (the spine, solved exactly at nodes and joined by a C1 Hermite curve); the
/// blend face is the pipe around the spine, touching each face along the foot points of the spine. The tool is
/// built directly as a closed B-rep: the cross-section region between the edge, the two contact curves and
/// the pipe, closed by planar caps normal to the spine at open ends. Concave tools are added to the body,
/// convex ones subtracted.
/// </summary>
public static class RollingBall
{
    public static Action<string>? Debug;

    public sealed record Result(Solid? Tool, bool Convex, bool Fits, string? Error);

    /// <summary>A piece of an edge, traversed from Ta to Tb.</summary>
    /// <summary>
    /// A chain's end piece on a curve that cannot be evaluated past its end (an intersection spline), run on by
    /// <paramref name="len"/> at one end: a new curve through the old one and points marched on along the intersection
    /// of its two faces' surfaces, in the edge's direction, standing in for the edge (its faces, the same uses). Null
    /// when the surfaces cannot be followed that far.
    /// </summary>
    static Piece? RunOn(Piece pc, bool atStart, double len)
    {
        var e = pc.E;
        NSurface sa = e.Uses[0].Face.Surface, sb = e.Uses[1].Face.Surface;
        // the piece's points in its own direction
        var pts = Enumerable.Range(0, 25).Select(i => e.Curve.Eval(pc.Ta + (pc.Tb - pc.Ta) * i / 24.0)).ToList();
        pts[0] = pc.Start; pts[^1] = pc.End;
        var cur = atStart ? pc.Start : pc.End;
        var d = pc.Dir(atStart ? pc.Ta : pc.Tb) * (atStart ? -1 : 1);
        var tol = 1e-9 * Math.Max(1, len);
        const int steps = 12;
        var more = new List<Vec3>();
        for (int i = 0; i < steps; i++)
        {
            var nx = NSurface.ProjectToIntersection(sa, sb, cur + d * (len / steps));
            if (!(Math.Abs(sa.F_(nx)) < tol && Math.Abs(sb.F_(nx)) < tol) || Vec3.Dot(nx - cur, d) <= 0.5 * len / steps)
            {
                // (as far as they can be followed, if that is some way: past the face cutting the end back, usually)
                Debug?.Invoke($"  run on {len:g3} from {cur}: lost the surfaces after {i} steps");
                if (i < 2) return null;
                break;
            }
            d = (nx - cur).Normalized;
            more.Add(nx); cur = nx;
        }
        if (atStart) { more.Reverse(); pts.InsertRange(0, more); } else pts.AddRange(more);
        // (the new curve runs as the edge does)
        bool rev = pc.Tb < pc.Ta;
        if (rev) pts.Reverse();
        var c = new NSpline(BSplineCurve.Interpolate(pts, 3));
        NVertex At(Vec3 p) => Vec3.Distance(p, e.V0.P) < 1e-9 ? e.V0 : Vec3.Distance(p, e.V1.P) < 1e-9 ? e.V1 : new NVertex(p);
        var ne = new NEdge(c, 0, 1, At(pts[0]), At(pts[^1]));
        ne.Uses.AddRange(e.Uses);
        return rev ? new Piece(ne, 1, 0) : new Piece(ne, 0, 1);
    }

    readonly record struct Piece(NEdge E, double Ta, double Tb)
    {
        public Vec3 Start => Ta == E.T0 ? E.V0.P : Ta == E.T1 ? E.V1.P : E.Curve.Eval(Ta);
        public Vec3 End => Tb == E.T0 ? E.V0.P : Tb == E.T1 ? E.V1.P : E.Curve.Eval(Tb);
        public Vec3 Dir(double t) => (E.Curve.Deriv(t) * (Tb > Ta ? 1 : -1)).Normalized;
        public double Mid => 0.5 * (Ta + Tb);
        public double Length
        {
            get
            {
                double l = 0; var p = Start;
                for (int i = 1; i <= 32; i++) { var q = E.Curve.Eval(Ta + (Tb - Ta) * i / 32); l += Vec3.Distance(p, q); p = q; }
                return l;
            }
        }
    }

    sealed record Joint(Vec3 V, Vec3 S, Vec3 T, Vec3 A, Vec3 B);

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    static bool OnSurface(NFace f, NSurface s) => ReferenceEquals(f.Surface, s) || f.Surface.CoincidentWith(s, 1e-9);

    /// <summary>Whether two edges join the same pair of surfaces.</summary>
    public static bool SameSurfaces(NEdge a, NEdge b)
    {
        if (a.Uses.Count != 2 || b.Uses.Count != 2) return false;
        NSurface a0 = a.Uses[0].Face.Surface, a1 = a.Uses[1].Face.Surface;
        NSurface b0 = b.Uses[0].Face.Surface, b1 = b.Uses[1].Face.Surface;
        bool Same(NSurface x, NSurface y) => ReferenceEquals(x, y) || x.CoincidentWith(y, 1e-9);
        return (Same(a0, b0) && Same(a1, b1)) || (Same(a0, b1) && Same(a1, b0));
    }

    /// <summary>
    /// Builds the fillet tool of radius r for a chain of edges (connected end to end, all between the same two
    /// surfaces). onFaces(face, p) tells whether a contact point lies on the face it should (fit check).
    /// runOn(vertex, outward direction, face, face), at an open chain's end: how far the tool runs on past it, along
    /// the edge's curve and the faces' surfaces (0: it ends square there), to be cut back to the face the end runs
    /// into at an angle. Contact points <paramref name="cutOff"/> says are cut off with it are not checked for fit.
    /// </summary>
    public static Result Build(IReadOnlyList<NEdge> chain, double r, string name, Func<NFace, Vec3, bool> onFaces,
        Func<NVertex, Vec3, NFace, NFace, double>? runOn = null, Func<Vec3, bool>? cutOff = null, Func<NVertex, Vec3?>? endAt = null)
    {
        if (cutOff != null) { var of = onFaces; onFaces = (f, p) => cutOff(p) || of(f, p); }
        var pieces = Order(chain, out var closed);
        if (pieces == null) return Fail("the edges do not form a single chain.");
        if (!closed && (runOn != null || endAt != null))
            foreach (var atStart in new[] { true, false })
            {
                int k = atStart ? 0 : pieces.Count - 1;
                var pc = pieces[k];
                var t = atStart ? pc.Ta : pc.Tb;
                var v = Vec3.Distance(pc.E.V0.P, pc.E.Curve.Eval(t)) <= Vec3.Distance(pc.E.V1.P, pc.E.Curve.Eval(t)) ? pc.E.V0 : pc.E.V1;
                // (ending at a given point of the edge, its curve run on or cut short to it: where the ball starts to
                // pivot round a corner)
                if (endAt?.Invoke(v) is { } q)
                {
                    if (Vec3.Distance(q, v.P) < 1e-12) continue;
                    var tq = pc.E.Curve.Param(q, t);
                    if (Vec3.Distance(pc.E.Curve.Eval(tq), q) > 1e-6 * Math.Max(1, r)) return Fail("internal: the end is not on the edge.");
                    pieces[k] = atStart ? pc with { Ta = tq } : pc with { Tb = tq };
                    continue;
                }
                if (runOn == null || pc.E.Uses.Count != 2) continue;
                if (pc.E.Curve is not (NLine or NCircle or NEllipse))
                {
                    // another curve (an intersection spline): made again, run on along its two faces' surfaces
                    var lenS = runOn(v, pc.Dir(t) * (atStart ? -1 : 1), pc.E.Uses[0].Face, pc.E.Uses[1].Face);
                    if (lenS > 0 && RunOn(pc, atStart, lenS) is { } longer) pieces[k] = longer;
                    continue;
                }
                var len = runOn(v, pc.Dir(t) * (atStart ? -1 : 1), pc.E.Uses[0].Face, pc.E.Uses[1].Face);
                if (!(len > 0)) continue;
                var dt = len / Math.Max(pc.E.Curve.Deriv(t).Length, 1e-12) * Math.Sign(pc.Tb - pc.Ta);
                if (pc.E.Curve is NCircle or NEllipse) dt = Math.Sign(dt) * Math.Min(Math.Abs(dt), Math.PI / 4);
                pieces[k] = atStart ? pc with { Ta = t - dt } : pc with { Tb = t + dt };
            }
        if (closed && pieces.Count == 1)
        {
            // one closed edge: two pieces (faces must not wrap around)
            var p = pieces[0];
            pieces = new List<Piece> { new(p.E, p.Ta, p.Mid), new(p.E, p.Mid, p.Tb) };
        }
        var e0 = pieces[0].E;
        if (pieces.Any(p => p.E.Uses.Count != 2)) return Fail("the edge is not between two faces.");
        var fA = e0.Uses[0].Face; var fB = e0.Uses[1].Face;
        NSurface SA = fA.Surface, SB = fB.Surface;
        NFace? FaceOn(NEdge e, NSurface s) => e.Uses.Select(u => u.Face).FirstOrDefault(f => OnSurface(f, s));
        foreach (var p in pieces)
        {
            var a = FaceOn(p.E, SA); var b = FaceOn(p.E, SB);
            if (a == null || b == null || ReferenceEquals(a, b) || a.Sense != fA.Sense || b.Sense != fB.Sense)
                return BuildComposite(pieces, closed, r, name, onFaces);
        }

        // convex (material removed) or concave (material added), at the middle of the first piece
        var tm = pieces[0].Mid; var M = e0.Curve.Eval(tm);
        var cA = e0.Uses.First(u => ReferenceEquals(u.Face, fA)); var cB = e0.Uses.First(u => ReferenceEquals(u.Face, fB));
        var nA = Outward(fA, M); var nB = Outward(fB, M);
        var uA = Vec3.Cross(nA, e0.Curve.Deriv(tm) * (cA.Forward ? 1 : -1)).Normalized;
        var uB = Vec3.Cross(nB, e0.Curve.Deriv(tm) * (cB.Forward ? 1 : -1)).Normalized;
        if (Math.Abs(Vec3.Dot(nA, nB)) > 1 - 1e-9) return Fail("the faces are tangent along the edge: there is nothing to fillet.");
        bool convex = Vec3.Dot(uB, nA) < 0;
        var rho = convex ? -r : r;

        // offset surfaces: the ball centre stays r away from both faces, on the side the ball rolls
        NSurface A2, B2;
        try
        {
            A2 = NShell.OffsetSurface(SA, rho * (fA.Sense ? 1 : -1));
            B2 = NShell.OffsetSurface(SB, rho * (fB.Sense ? 1 : -1));
        }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }

        var box = Box3.Empty;
        foreach (var p in pieces) foreach (var q in p.E.Samples(1e-3)) box.Add(q);
        var scale = Math.Max(box.Diagonal, r);
        var tolS = 1e-9 * Math.Max(10, scale);

        // joints: where the cross-section plane (normal to the spine) passes through a chain vertex
        int n = pieces.Count, nj = closed ? n : n + 1;
        var joints = new Joint[nj];
        for (int k = 0; k < nj; k++)
        {
            var pc = k < n ? pieces[k] : pieces[n - 1];
            var V = k < n ? pc.Start : pc.End;
            var dir = pc.Dir(k < n ? pc.Ta : pc.Tb);
            var fa = FaceOn(pc.E, SA)!; var fb = FaceOn(pc.E, SB)!;
            var na = Outward(fa, V); var nb = Outward(fb, V);
            var guess = V + (na + nb) * (rho / (1 + Vec3.Dot(na, nb)));
            var j = SolveJoint(A2, B2, V, guess, dir, scale);
            if (j == null) return Fail("the ball does not fit at an end of the edge (no centre at that distance from both faces).");
            var (S, T) = j.Value;
            var a = SA.Project(S); var b = SB.Project(S);
            if (Vec3.Distance(a, V) < 1e-7 * scale || Vec3.Distance(b, V) < 1e-7 * scale) return Fail("the faces meet tangentially.");
            joints[k] = new Joint(V, S, T, a, b);
        }
        Joint J(int k) => joints[closed ? k % n : k];

        // spines
        var spines = new PipeSpine[n]; var range = new (double s0, double s1)[n];
        for (int k = 0; k < n; k++)
        {
            var ja = J(k); var jb = J(k + 1);
            var m = March(A2, B2, ja, jb, r, Math.Max(pieces[k].Length, Vec3.Distance(ja.S, jb.S)), tolS, scale);
            if (m == null)
            {
                Debug?.Invoke($"march failed on piece {k}/{n} ({pieces[k].E.Curve.GetType().Name} {pieces[k].Start}->{pieces[k].End}, {SA.Kind}/{SB.Kind}) from {ja.S} to {jb.S}");
                return Fail("could not follow the rolling ball along the edge (the faces turn too sharply for this radius).");
            }
            var (P, D, i0, i1) = m.Value;
            var sp = new PipeSpine(P, D, SA, SB);
            if (Folds(sp, r, SA.Project, SB.Project)) return Fail("the radius is too large: the fillet would fold over itself.");
            spines[k] = sp; range[k] = (sp.S[i0], sp.S[i1]);
        }

        // ---- topology
        var VV = new NVertex[nj]; var AV = new NVertex[nj]; var BV = new NVertex[nj];
        var secA = new NEdge[nj]; var secB = new NEdge[nj]; var arc = new NEdge[nj];
        for (int k = 0; k < nj; k++)
        {
            var j = joints[k];
            VV[k] = new NVertex(j.V); AV[k] = new NVertex(j.A); BV[k] = new NVertex(j.B);
            var plane = NSurface.FromPlane(Plane.FromPointNormal(j.S, j.T));
            secA[k] = new NEdge(new NSection(SA, plane, j.V, j.A), 0, 1, VV[k], AV[k]);
            secB[k] = new NEdge(new NSection(SB, plane, j.V, j.B), 0, 1, VV[k], BV[k]);
            var circle = new NCircle(j.S, j.T, j.A - j.S, r);
            var tb = circle.Param(j.B);
            if (tb < 0) { circle = new NCircle(j.S, -j.T, j.A - j.S, r); tb = circle.Param(j.B); }
            arc[k] = new NEdge(circle, 0, tb, AV[k], BV[k]);
        }
        int W(int k) => closed ? k % n : k;
        var body = new NBody();
        var tagA = new FaceTag(name + ".A", SA.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : SA.Def);
        var tagB = new FaceTag(name + ".B", SB.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : SB.Def);
        bool fits = true;
        for (int k = 0; k < n; k++)
        {
            var pc = pieces[k]; var sp = spines[k]; var (s0, s1) = range[k];
            int k0 = W(k), k1 = W(k + 1);
            var fwd = pc.Tb > pc.Ta;
            var E = fwd ? new NEdge(pc.E.Curve, pc.Ta, pc.Tb, VV[k0], VV[k1]) : new NEdge(pc.E.Curve, pc.Tb, pc.Ta, VV[k1], VV[k0]);
            var cAe = new NEdge(new NContact(sp, false), s0, s1, AV[k0], AV[k1]);
            var cBe = new NEdge(new NContact(sp, true), s0, s1, BV[k0], BV[k1]);
            var pipe = NSurface.Pipe(sp, r);
            var faA = FaceOn(pc.E, SA)!; var faB = FaceOn(pc.E, SB)!;

            var Mp = pc.E.Curve.Eval(pc.Mid); var t = pc.Dir(pc.Mid);
            var sm = 0.5 * (s0 + s1); var C = sp.Eval(sm);
            var qa = SA.Project(C); var qb = SB.Project(C);
            // faces on A and B: between the edge and the contact curve; the tool's outward normal there points
            // into the body (concave: the tool fills open space) or out of it (convex: the tool is material)
            foreach (var (fsurf, face, sec, cont, q, tag) in new[] { (SA, faA, secA, cAe, qa, tagA), (SB, faB, secB, cBe, qb, tagB) })
            {
                var nT = Outward(face, Mp) * (convex ? 1 : -1);
                var loop = new List<(NEdge, bool)> { (E, fwd), (sec[k1], true), (cont, false), (sec[k0], false) };
                if (Vec3.Dot(Vec3.Cross(nT, t), q - Mp) < 0) loop = Reverse(loop);
                body.Faces.Add(MakeFace(fsurf, convex ? face.Sense : !face.Sense, tag, loop, fsurf.Project((Mp + q) * 0.5)));
            }
            // blend face: outward towards the spine (the tool lies outside the ball)
            {
                var nT = -pipe.Normal(qa);
                var loop = new List<(NEdge, bool)> { (cAe, true), (arc[k1], true), (cBe, false), (arc[k0], false) };
                if (Vec3.Dot(Vec3.Cross(nT, sp.Deriv(sm).Normalized), qb - qa) < 0) loop = Reverse(loop);
                body.Faces.Add(MakeFace(pipe, false, new FaceTag(name + ".Blend", pipe.Def), loop, pipe.Project((qa + qb) * 0.5)));
            }
            for (int i = 0; i <= 8 && fits; i++)
            {
                var c = sp.Eval(s0 + (s1 - s0) * i / 8);
                fits = onFaces(faA, SA.Project(c)) && onFaces(faB, SB.Project(c));
            }
        }
        if (!closed)
            foreach (var (k, sign) in new[] { (0, -1.0), (n, 1.0) })
            {
                var j = joints[k];
                var outward = j.T * sign;
                var loop = new List<(NEdge, bool)> { (secA[k], true), (arc[k], true), (secB[k], false) };
                if (Vec3.Dot(Vec3.Cross(j.A - j.V, j.B - j.V), outward) < 0) loop = Reverse(loop);
                var pl = NSurface.FromPlane(Plane.FromPointNormal(j.S, outward));
                body.Faces.Add(MakeFace(pl, true, new FaceTag($"{name}.End{(k == 0 ? 0 : 1)}", SurfaceDef.PlaneDef), loop, null));
            }
        body.Link();
        var err = body.Validate();
        if (err != null) return Fail("internal: " + err);
        return new Result(NTess.ToSolid(body), convex, fits, null);

        static Result Fail(string m) => new(null, false, false, m);
    }

    /// <summary>
    /// Whether the blend face folds over itself. Where the spine bends tighter than the ball's radius the pipe folds,
    /// but only on the side towards the bend's centre: the blend is the arc between the ball's two contacts, which
    /// round a convex corner (an edge's round running round a rounded corner) faces away from it.
    /// </summary>
    static bool Folds(PipeSpine sp, double r, Func<Vec3, Vec3> contactA, Func<Vec3, Vec3> contactB)
    {
        if (!(sp.MinCurvatureRadius < r * 1.02)) return false;
        int n = Math.Max(64, (sp.P.Length - 1) * 8);
        for (int i = 0; i <= n; i++)
        {
            var s = sp.S0 + (sp.S1 - sp.S0) * i / n;
            var d1 = sp.Deriv(s); var d2 = sp.Deriv2(s);
            var l2 = Vec3.Dot(d1, d1);
            if (!(l2 > 1e-300)) continue;
            var T = d1 / Math.Sqrt(l2);
            var kv = (d2 - T * Vec3.Dot(T, d2)) / l2;
            var k = kv.Length;
            if (k * r < 0.98) continue;
            var N = kv / k;
            var c = sp.Eval(s);
            var ua = (contactA(c) - c).Normalized; var ub = (contactB(c) - c).Normalized;
            // (the shorter arc between the contacts: a fillet's)
            var ang = Math.Acos(Math.Clamp(Vec3.Dot(ua, ub), -1, 1));
            for (int j = 0; j <= 8; j++)
            {
                var t = j / 8.0;
                var u = ang < 1e-9 ? ua : (ua * Math.Sin((1 - t) * ang) + ub * Math.Sin(t * ang)) / Math.Sin(ang);
                if (k * r * Vec3.Dot(u, N) > 0.98) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A chain whose edges stay on one surface on one side (A) while the faces on the other side (B) change where
    /// they meet tangentially (a groove's rim edge running from a torus onto a fillet and on onto a cylinder).
    /// The ball rolls from one B face onto the next where its foot point crosses the boundary curve between them
    /// (the switch): there the spine moves from one offset intersection to the next (both meet the offset of the
    /// boundary curve, so the spine stays C1), and the tool's B side is split along the boundary curve from the
    /// chain vertex to the foot point. The A side is one face between the chain ends (a band for a closed chain).
    /// </summary>
    static Result BuildComposite(List<Piece> pieces, bool closed, double r, string name, Func<NFace, Vec3, bool> onFaces)
    {
        static Result Fail(string m) => new(null, false, false, m);
        int n = pieces.Count;
        // the constant side: a surface every piece has a face on
        NSurface? SA = null;
        foreach (var cand in pieces[0].E.Uses.Select(u => u.Face.Surface))
            if (pieces.All(p => p.E.Uses.Any(u => OnSurface(u.Face, cand)))) { SA = cand; break; }
        if (SA == null) return Fail("the edges are not all between the same two faces.");
        var faA = new NFace[n]; var faB = new NFace[n];
        for (int k = 0; k < n; k++)
        {
            var a = pieces[k].E.Uses.Select(u => u.Face).FirstOrDefault(f => OnSurface(f, SA));
            var b = pieces[k].E.Uses.Select(u => u.Face).FirstOrDefault(f => !ReferenceEquals(f, a));
            if (a == null || b == null || OnSurface(b, SA)) return Fail("the edges are not all between the same two faces.");
            faA[k] = a; faB[k] = b;
        }
        if (faA.Any(f => f.Sense != faA[0].Sense)) return Fail("the edges are not all between the same two faces.");
        bool SameB(int k, int m) => Family(faB[k].Surface, faB[m].Surface) && faB[k].Sense == faB[m].Sense;
        // the surface a face's ball rolls on: a fillet's pieces merged into one pipe (the foot crosses from piece to
        // piece anywhere, not where the chain does)
        var merged = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        NSurface Sup(NFace f)
        {
            if (f.Surface.Kind != SurfaceKind.Pipe) return f.Surface;
            if (merged.TryGetValue(f, out var m)) return m;
            // the pieces: pipe faces of the same family reachable through shared edges
            var fam = new List<NFace> { f };
            for (int i = 0; i < fam.Count && fam.Count < 64; i++)
                foreach (var c in fam[i].Coedges)
                    foreach (var u in c.Edge.Uses)
                        if (!fam.Contains(u.Face) && u.Face.Surface.Kind == SurfaceKind.Pipe && Family(u.Face.Surface, f.Surface)) fam.Add(u.Face);
            var spines = fam.Select(x => x.Surface.Spine!).Distinct().ToList();
            var sp = spines.Count == 1 ? spines[0] : MergeSpines(spines, f.Surface.Spine!);
            m = sp == null ? f.Surface : NSurface.Pipe(sp, f.Surface.R);
            Debug?.Invoke($"  merged {fam.Count} faces, {spines.Count} spines ({string.Join(",", spines.Select(x => x.MinCurvatureRadius.ToString("g3")))}) -> {(sp == null ? "none" : sp.MinCurvatureRadius.ToString("g3") + " over " + sp.P.Length + " nodes")}");
            foreach (var x in fam) merged[x] = m;
            return m;
        }

        // a closed chain starts at a switch
        if (closed)
        {
            int s0 = Enumerable.Range(0, n).FirstOrDefault(k => !SameB((k + n - 1) % n, k), -1);
            if (s0 < 0) return Fail("the edges are not all between the same two faces.");
            pieces = pieces.Skip(s0).Concat(pieces.Take(s0)).ToList();
            faA = faA.Skip(s0).Concat(faA.Take(s0)).ToArray(); faB = faB.Skip(s0).Concat(faB.Take(s0)).ToArray();
        }
        int nj = closed ? n : n + 1;
        int Prev(int k) => closed ? (k + n - 1) % n : k - 1;     // piece before vertex k
        bool RawSwitch(int k) => (closed || k > 0 && k < n) && !SameB(Prev(k), k % n);
        // (the edge's vertex nearest: an end run on past its vertex has a parameter of its own)
        static NVertex Nearest(NEdge e, double t) => Math.Abs(t - e.T0) <= Math.Abs(t - e.T1) ? e.V0 : e.V1;
        NVertex ChainVertex(int k) => k < n ? Nearest(pieces[k].E, pieces[k].Ta) : Nearest(pieces[n - 1].E, pieces[n - 1].Tb);
        var vertexIndex = new Dictionary<NVertex, int>(ReferenceEqualityComparer.Instance);
        for (int k = 0; k < nj; k++) vertexIndex.TryAdd(ChainVertex(k), k);

        // convex or concave, at the middle of the first piece
        var e0 = pieces[0].E;
        var tm = pieces[0].Mid; var M = e0.Curve.Eval(tm);
        var cA0 = e0.Uses.First(u => ReferenceEquals(u.Face, faA[0])); var cB0 = e0.Uses.First(u => ReferenceEquals(u.Face, faB[0]));
        var nA0 = Outward(faA[0], M); var nB0 = Outward(faB[0], M);
        var uB0 = Vec3.Cross(nB0, e0.Curve.Deriv(tm) * (cB0.Forward ? 1 : -1)).Normalized;
        if (Math.Abs(Vec3.Dot(nA0, nB0)) > 1 - 1e-9) return Fail("the faces are tangent along the edge: there is nothing to fillet.");
        bool convex = Vec3.Dot(uB0, nA0) < 0;
        var rho = convex ? -r : r;

        NSurface A2;
        try { A2 = NShell.OffsetSurface(SA, rho * (faA[0].Sense ? 1 : -1)); }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }

        // the boundary curve between the B faces before and after a switch vertex, followed (over several edges if the
        // faces are split further on) until the ball's foot reaches it, or until it comes back to the chain at another
        // vertex: then the chain dips into the next face and out again while the ball rolls on (an island under the blend)
        var walked = new List<NFace>();
        var footEdge = new Dictionary<int, NEdge>();
        (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err) Walk(int k)
        {
            var fb0 = faB[Prev(k)]; var fb1 = faB[k % n];
            var vtx = ChainVertex(k % n);
            var V = vtx.P;
            var segs = new List<(NCurve c, double ta, double tb)>();
            var g = fb0.Coedges.Select(c => c.Edge).FirstOrDefault(x => (ReferenceEquals(x.V0, vtx) || ReferenceEquals(x.V1, vtx))
                       && x.Uses.Any(u => ReferenceEquals(u.Face, fb1)) && !pieces.Any(p => ReferenceEquals(p.E, x)));
            if (g == null) return (null, segs, null, "the edges change faces where the faces do not meet along an edge.");
            if (Vec3.Dot(Outward(fb0, V), Outward(fb1, V)) < 1 - 1e-4) return (null, segs, null, "the edges turn a corner between two faces: blend them separately.");
            var nsurf = Sup(fb0); bool sense = fb0.Sense;
            Vec3 C(NCurve gc, double t) { var G = gc.Eval(t); return G + nsurf.Normal(G) * ((sense ? 1 : -1) * rho); }
            var from = vtx; var cur = g;
            for (int step = 0; step < 8; step++)
            {
                foreach (var u in cur.Uses) if (!walked.Contains(u.Face)) walked.Add(u.Face);
                // ball centres touching the boundary curve: C(t) = G(t) + rho n(G(t)); the switch is where C(t) is on A's offset
                var tv = ReferenceEquals(cur.V0, from) ? cur.T0 : cur.T1; var tfar = ReferenceEquals(cur.V0, from) ? cur.T1 : cur.T0;
                var gc = cur.Curve;
                double F(double t) => A2.F_(C(gc, t));
                const int N = 64;
                double tPrev = tv, fPrev = F(tv);
                for (int i = 1; i <= N; i++)
                {
                    var t = tv + (tfar - tv) * i / N; var f = F(t);
                    if (Math.Sign(f) != Math.Sign(fPrev) || f == 0)
                    {
                        double lo = tPrev, hi = t, flo = fPrev;
                        for (int it = 0; it < 100 && Math.Abs(hi - lo) > 1e-15 * Math.Max(1, Math.Abs(hi)); it++)
                        {
                            var mid = 0.5 * (lo + hi); var fm = F(mid);
                            if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; } else hi = mid;
                        }
                        var root = 0.5 * (lo + hi);
                        segs.Add((gc, tv, root));
                        footEdge[k] = cur;
                        return (root, segs, null, null);
                    }
                    tPrev = t; fPrev = f;
                }
                segs.Add((gc, tv, tfar));
                var w = ReferenceEquals(cur.V0, from) ? cur.V1 : cur.V0;
                if (vertexIndex.TryGetValue(w, out var m)) return (null, segs, m, null);
                // on along the boundary: the next edge at the far vertex between faces of the same two surfaces
                var next = EdgesAt(w, cur).FirstOrDefault(x => !ReferenceEquals(x, cur) && x.Uses.Count == 2
                    && (Side(x.Uses[0].Face, fb0) && Side(x.Uses[1].Face, fb1) || Side(x.Uses[0].Face, fb1) && Side(x.Uses[1].Face, fb0)));
                if (next == null) break;
                from = w; cur = next;
            }
            Debug?.Invoke($"switch at {V}: no centre along the boundary from {g.V0.P}->{g.V1.P} ({fb0.Tag.Name}/{fb1.Tag.Name}, {segs.Count} edges)");
            return (null, segs, null, "the ball leaves the faces it blends before it reaches the next face.");
        }
        var walks = new Dictionary<int, (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err)>();
        var islandEnd = new HashSet<int>();
        var islands = new Dictionary<int, (int m, List<(NCurve c, double ta, double tb)> segs)>();   // start vertex -> end vertex, boundary path
        // pieces a..m-1 lie in the island starting at vertex a; vertices strictly between a and m are inside it
        bool InIsland(int k)
        {
            foreach (var (a, (m, _)) in islands)
                if (closed ? ((k - a + n) % n) < ((m - a + n) % n) : k >= a && k < m) return true;
            return false;
        }
        bool InsideIsland(int k)
        {
            foreach (var (a, (m, _)) in islands)
                if (closed ? ((k - a + n) % n) > 0 && ((k - a + n) % n) < ((m - a + n) % n) : k > a && k < m) return true;
            return false;
        }
        int IslandStartOf(int k)
        {
            foreach (var (a, (m, _)) in islands)
                if (closed ? ((k - a + n) % n) < ((m - a + n) % n) : k >= a && k < m) return a;
            return -1;
        }
        for (int k = 0; k < nj; k++)
        {
            if (!RawSwitch(k) || islandEnd.Contains(k) || InsideIsland(k)) continue;
            var wk = Walk(k);
            if (wk.err != null) return Fail(wk.err);
            walks[k] = wk;
            if (wk.back is int m)
            {
                // the island ends where the chain comes back onto the face it left
                if (!RawSwitch(m) || !Side(faB[m % n], faB[Prev(k)]) || m == k) return Fail("the ball leaves the faces it blends before it reaches the next face.");
                islands[k] = (m, wk.segs);
                islandEnd.Add(k); islandEnd.Add(m);
            }
        }
        bool IsSwitch(int k) => RawSwitch(k) && !islandEnd.Contains(k) && !InsideIsland(k);

        var box = Box3.Empty;
        foreach (var p in pieces) foreach (var q in p.E.Samples(1e-3)) box.Add(q);
        var scale = Math.Max(box.Diagonal, r);
        var tolS = 1e-9 * Math.Max(10, scale);

        // ---- events along the chain, in order: joints (cross-sections through chain vertices) and switches (where
        // the ball's foot reaches the boundary curve). A switch need not lie between the vertices it belongs to: the
        // foot can cross onto the next face after the chain has passed further vertices.
        var cum = new double[n + 1];
        for (int k = 0; k < n; k++) cum[k + 1] = cum[k] + pieces[k].Length;
        double total = cum[n];
        // where the plane through S normal to T crosses the chain, near vertex k
        double ChainPos(Vec3 S, Vec3 T, int k)
        {
            double best = double.NaN, bestD = double.MaxValue;
            for (int dq = -3; dq <= 2; dq++)
            {
                int q = k + dq;
                if (closed) q = (q % n + n) % n; else if (q < 0 || q >= n) continue;
                var pc = pieces[q];
                const int N = 32;
                double fPrev = Vec3.Dot(pc.E.Curve.Eval(pc.Ta) - S, T);
                for (int i = 1; i <= N; i++)
                {
                    var t = pc.Ta + (pc.Tb - pc.Ta) * i / N;
                    var f = Vec3.Dot(pc.E.Curve.Eval(t) - S, T);
                    if (Math.Sign(f) != Math.Sign(fPrev) || f == 0)
                    {
                        double lo = pc.Ta + (pc.Tb - pc.Ta) * (i - 1) / N, hi = t, flo = fPrev;
                        for (int it = 0; it < 60; it++)
                        {
                            var mid = 0.5 * (lo + hi); var fm = Vec3.Dot(pc.E.Curve.Eval(mid) - S, T);
                            if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; } else hi = mid;
                        }
                        var tt = 0.5 * (lo + hi);
                        var d = Vec3.Distance(pc.E.Curve.Eval(tt), S);
                        if (d < bestD) { bestD = d; best = cum[q] + pieces[q].Length * (tt - pc.Ta) / (pc.Tb - pc.Ta); }
                    }
                    fPrev = f;
                }
            }
            // (crossing at the vertex itself, between two pieces, the samples' signs can miss it: then a far crossing, at
            // the chain's other end where it comes back to the same height, is all there is; the vertex is nearer)
            var vk = k < n ? pieces[k].Start : pieces[n - 1].End;
            if (double.IsNaN(best) || bestD > Vec3.Distance(vk, S) + 1e-9 * scale) return cum[Math.Min(k, n)];
            return best;
        }

        // switches first (they set the face the ball rolls on after them)
        var events = new List<(double pos, int k, bool sw)>();
        var swJoint = new Dictionary<int, Joint>();
        var swAfter = new Dictionary<int, NFace>();
        var swBefore = new Dictionary<int, NFace>();
        for (int k = 0; k < nj; k++)
        {
            if (!IsSwitch(k)) continue;
            var (root, segs, _, _) = walks[k];
            var fb = faB[Prev(k)];
            var last = segs[^1];
            var G = last.c.Eval(root!.Value);
            var Ss = G + Sup(fb).Normal(G) * ((fb.Sense ? 1 : -1) * rho);
            NSurface Bb;
            try { Bb = NShell.OffsetSurface(Sup(fb), rho * (fb.Sense ? 1 : -1)); }
            catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }
            var Ts = Vec3.Cross(A2.Grad(Ss), Bb.Grad(Ss));
            if (Ts.Length < 1e-9) return Fail("the faces meet tangentially.");
            var dir = pieces[k % n].Dir(pieces[k % n].Ta);
            Ts = Ts.Normalized; if (Vec3.Dot(Ts, dir) < 0) Ts = -Ts;
            swJoint[k] = new Joint(ChainVertex(k % n).P, Ss, Ts, SA.Project(Ss), G);
            // the face the foot moves onto: across the boundary edge it was found on
            var fe = footEdge[k];
            swAfter[k] = fe.Uses.Select(u => u.Face).FirstOrDefault(f => !Side(f, fb)) ?? faB[k % n];
            swBefore[k] = fe.Uses.Select(u => u.Face).FirstOrDefault(f => Side(f, fb)) ?? fb;
            events.Add((ChainPos(Ss, Ts, k), k, true));
        }
        for (int k = 0; k < nj; k++)
            if (!IsSwitch(k)) events.Add((k < n ? cum[k] : total, k, false));
        // order along the chain; a closed chain starts at its first switch
        double origin = closed && events.Any(e => e.sw) ? events.Where(e => e.sw).Min(e => e.pos) : 0;
        double Key(double pos) => closed ? ((pos - origin) % total + total) % total : pos;
        events = events.OrderBy(e => Key(e.pos)).ThenBy(e => e.sw ? 1 : 0).ToList();
        int ne = events.Count;
        int nseg = closed ? ne : ne - 1;
        Debug?.Invoke("  events: " + string.Join(" ", events.Select(e => $"{(e.sw ? "S" : "J")}{e.k}@{Key(e.pos):0.###}" + (e.sw ? $"[{swBefore[e.k].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(swBefore[e.k].Surface) % 1000}->{swAfter[e.k].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(swAfter[e.k].Surface) % 1000} edge {faB[Prev(e.k)].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(faB[Prev(e.k)].Surface) % 1000}->{faB[e.k % n].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(faB[e.k % n].Surface) % 1000}]" : ""))) + $" (chain {total:0.###})");

        // the face the ball rolls on before the first event, then changed by each switch
        var support = new NFace[ne];
        {
            NFace cur = closed ? (events.Where(e => e.sw).Select(e => swAfter[e.k]).LastOrDefault() ?? faB[0]) : faB[0];
            if (!closed && IslandStartOf(0) >= 0) cur = faB[0];
            for (int i = 0; i < ne; i++)
            {
                if (events[i].sw) cur = swAfter[events[i].k];
                support[i] = cur;   // the face after event i
            }
        }
        var offsets = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        NSurface Off(NFace f)
        {
            if (!offsets.TryGetValue(f, out var o)) offsets[f] = o = NShell.OffsetSurface(Sup(f), rho * (f.Sense ? 1 : -1));
            return o;
        }
        // joints, with the face the ball rolls on there
        var ev = new Joint[ne];
        try
        {
            for (int i = 0; i < ne; i++)
            {
                var (pos, k, sw) = events[i];
                if (sw) { ev[i] = swJoint[k]; continue; }
                var fbk = support[i];
                int kp = k < n ? k : n - 1;
                var pc = pieces[kp];
                var V = k < n ? pc.Start : pc.End;
                var dir = pc.Dir(k < n ? pc.Ta : pc.Tb);
                var na = Outward(faA[kp], V); var nb = Outward(fbk, V);
                var guess = V + (na + nb) * (rho / (1 + Vec3.Dot(na, nb)));
                var j = SolveJoint(A2, Off(fbk), V, guess, dir, scale);
                if (j == null) return Fail("the ball does not fit at an end of the edge (no centre at that distance from both faces).");
                var (S, T) = j.Value;
                var a = SA.Project(S); var b = Sup(fbk).Project(S);
                if (Vec3.Distance(a, V) < 1e-7 * scale || Vec3.Distance(b, V) < 1e-7 * scale) return Fail("the faces meet tangentially.");
                ev[i] = new Joint(V, S, T, a, b);
            }
        }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }

        // every surface the ball rolls on along the chain (the boolean does not trace a segment's blend against them)
        var rolls = support.Select(Sup).Distinct().ToList();
        // spine segments between consecutive events, on A's offset and the offset of the face the ball rolls on
        var spines = new PipeSpine[nseg]; var range = new (double s0, double s1)[nseg];
        for (int i = 0; i < nseg; i++)
        {
            var ja = ev[i]; var jb = ev[(i + 1) % ne];
            var len = Key(events[(i + 1) % ne].pos) - Key(events[i].pos);
            if (closed && i == ne - 1) len += total;
            var m = March(A2, Off(support[i]), ja, jb, r, Math.Max(Math.Max(len, 0), Vec3.Distance(ja.S, jb.S)), tolS, scale);
            if (m == null)
            {
                Debug?.Invoke($"march failed on segment {i}/{nseg} ({support[i].Tag.Name}) from {ja.S} to {jb.S}");
                return Fail("could not follow the rolling ball along the edge (the faces turn too sharply for this radius).");
            }
            var (P, D, i0, i1) = m.Value;
            var sp = new PipeSpine(P, D, SA, Sup(support[i])) { Rolls = rolls };
            if (Folds(sp, r, SA.Project, Sup(support[i]).Project)) { if (Debug != null) { var fam = walked.Concat(faB).Distinct().Where(f => Side(f, support[i])).ToList(); for (int q = 0; q <= 10; q++) { var c = sp.Eval(sp.S0 + (sp.S1 - sp.S0) * q / 10); var pb = Sup(support[i]).Project(c); Debug($"    foot {q}: {pb} on fillet faces {fam.Any(f => onFaces(f, pb))}, A {faA.Any(f => onFaces(f, SA.Project(c)))}"); } } Debug?.Invoke($"fold on segment {i}/{nseg} ({support[i].Tag.Name}:{support[i].Surface.Kind}) {ev[i].S} -> {ev[(i + 1) % ne].S}: min curvature radius {sp.MinCurvatureRadius:g3}, {P.Count} nodes"); return Fail("the radius is too large: the fillet would fold over itself."); }
            spines[i] = sp; range[i] = (sp.S[i0], sp.S[i1]);
        }

        // ---- topology
        var VV = new NVertex[nj];
        // (an open chain's last vertex is ChainVertex(n), its far end: not k % n, the start)
        for (int k = 0; k < nj; k++) VV[k] = new NVertex(!closed && k == 0 ? pieces[0].Start : !closed && k == n ? pieces[n - 1].End : ChainVertex(k).P);
        var AV = new NVertex[ne]; var BV = new NVertex[ne]; var arc = new NEdge[ne];
        for (int i = 0; i < ne; i++)
        {
            var j = ev[i];
            AV[i] = new NVertex(j.A); BV[i] = new NVertex(j.B);
            var circle = new NCircle(j.S, j.T, j.A - j.S, r);
            var tb = circle.Param(j.B);
            if (tb < 0) { circle = new NCircle(j.S, -j.T, j.A - j.S, r); tb = circle.Param(j.B); }
            arc[i] = new NEdge(circle, 0, tb, AV[i], BV[i]);
        }
        int EvOf(int k, bool sw) => events.FindIndex(e => e.k == k && e.sw == sw);
        // sections at the open ends, boundary paths at switches (chain vertex to foot point)
        NEdge? secA0 = null, secA1 = null;
        var bnd = new Dictionary<int, List<(NEdge e, bool fwd)>>();     // by chain vertex
        if (!closed)
            foreach (var k in new[] { 0, n })
            {
                int i = EvOf(k, false);
                var j = ev[i];
                var plane = NSurface.FromPlane(Plane.FromPointNormal(j.S, j.T));
                var sa = new NEdge(new NSection(SA, plane, j.V, j.A), 0, 1, VV[k], AV[i]);
                if (k == 0) secA0 = sa; else secA1 = sa;
                bnd[k] = new() { (new NEdge(new NSection(Sup(support[i]), plane, j.V, j.B), 0, 1, VV[k], BV[i]), true) };
            }
        foreach (var (k, _) in swJoint)
        {
            var path = walks[k].segs;
            var list = new List<(NEdge, bool)>();
            var v0 = VV[k];
            for (int q = 0; q < path.Count; q++)
            {
                var (gc, ga, gb) = path[q];
                var v1 = q == path.Count - 1 ? BV[EvOf(k, true)] : new NVertex(gc.Eval(gb));
                list.Add(ga < gb ? (new NEdge(gc, ga, gb, v0, v1), true) : (new NEdge(gc, gb, ga, v1, v0), false));
                v0 = v1;
            }
            bnd[k] = list;
        }
        IEnumerable<(NEdge, bool)> Out(int k) => bnd[k];
        IEnumerable<(NEdge, bool)> In(int k) => Enumerable.Reverse(bnd[k]).Select(x => (x.e, !x.fwd));

        var body = new NBody();
        var tagA = new FaceTag(name + ".A", SA.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : SA.Def);
        bool fits = true;
        var E = new NEdge[n]; var fwdE = new bool[n];
        for (int k = 0; k < n; k++)
        {
            var pc = pieces[k];
            int k0 = k, k1 = closed ? (k + 1) % n : k + 1;
            fwdE[k] = pc.Tb > pc.Ta;
            E[k] = fwdE[k] ? new NEdge(pc.E.Curve, pc.Ta, pc.Tb, VV[k0], VV[k1]) : new NEdge(pc.E.Curve, pc.Tb, pc.Ta, VV[k1], VV[k0]);
        }
        // the contact point next to chain point p (orientation of the faces between the chain and the contact)
        Vec3 Foot(Vec3 p, NSurface surf)
        {
            Vec3 best = p; double bd = double.MaxValue;
            for (int i = 0; i < nseg; i++)
                for (int q = 0; q <= 16; q++)
                {
                    var c = spines[i].Eval(range[i].s0 + (range[i].s1 - range[i].s0) * q / 16);
                    var d = Vec3.Distance(c, p);
                    if (d < bd) { bd = d; best = c; }
                }
            return surf.Project(best);
        }
        var cA = new NEdge[nseg]; var cB = new NEdge[nseg];
        var bFaces = walked.Concat(faB).Distinct().ToList();
        for (int i = 0; i < nseg; i++)
        {
            var sp = spines[i]; var (s0, s1) = range[i];
            int i1 = (i + 1) % ne;
            cA[i] = new NEdge(new NContact(sp, false), s0, s1, AV[i], AV[i1]);
            cB[i] = new NEdge(new NContact(sp, true), s0, s1, BV[i], BV[i1]);
            // blend face
            var sm = 0.5 * (s0 + s1);
            var qa = SA.Project(sp.Eval(sm)); var qb = Sup(support[i]).Project(sp.Eval(sm));
            var pipe = NSurface.Pipe(sp, r);
            var nT = -pipe.Normal(qa);
            var loop = new List<(NEdge, bool)> { (cA[i], true), (arc[i1], true), (cB[i], false), (arc[i], false) };
            if (Vec3.Dot(Vec3.Cross(nT, sp.Deriv(sm).Normalized), qb - qa) < 0) loop = Reverse(loop);
            body.Faces.Add(MakeFace(pipe, false, new FaceTag(name + ".Blend", pipe.Def), loop, pipe.Project((qa + qb) * 0.5)));
            for (int q = 0; q <= 8 && fits; q++)
            {
                var c = sp.Eval(s0 + (s1 - s0) * q / 8);
                var pa = SA.Project(c); var pb = Sup(support[i]).Project(c);
                fits = faA.Any(f => onFaces(f, pa)) && bFaces.Any(f => Side(f, support[i]) && onFaces(f, pb));
            }
        }

        // islands: the boundary path from the island's first chain vertex to its last, and the face between it and the chain
        var islandPath = new Dictionary<int, (int m, List<(NEdge, bool)> path)>();
        foreach (var (k, (m, segs)) in islands)
        {
            var path = new List<(NEdge, bool)>();
            var v0 = VV[k];
            for (int i = 0; i < segs.Count; i++)
            {
                var (gc, ga, gb) = segs[i];
                var v1 = i == segs.Count - 1 ? VV[m % nj] : new NVertex(gc.Eval(gb));
                path.Add(ga < gb ? (new NEdge(gc, ga, gb, v0, v1), true) : (new NEdge(gc, gb, ga, v1, v0), false));
                v0 = v1;
            }
            islandPath[k] = (m, path);
            var steps = (m - k + n) % n;
            var loop = new List<(NEdge, bool)>();
            for (int i = 0; i < steps; i++) { int q = (k + i) % n; loop.Add((E[q], fwdE[q])); }
            loop.AddRange(Enumerable.Reverse(path).Select(x => (x.Item1, !x.Item2)));
            var fI = faB[k % n];
            var sI = Sup(fI);
            var Mp = pieces[k % n].E.Curve.Eval(pieces[k % n].Mid); var t = pieces[k % n].Dir(pieces[k % n].Mid);
            var (gm, gma, gmb) = segs[segs.Count / 2];
            var qI = gm.Eval(0.5 * (gma + gmb));
            var nT = Outward(fI, Mp) * (convex ? 1 : -1);
            if (Vec3.Dot(Vec3.Cross(nT, t), qI - Mp) < 0) loop = Reverse(loop);
            var tagI = new FaceTag($"{name}.I{k}", sI.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : sI.Def);
            body.Faces.Add(MakeFace(sI, convex ? fI.Sense : !fI.Sense, tagI, loop, sI.Project((Mp + qI) * 0.5)));
        }

        // B side: one face per run between switches (or open ends): the chain edges from the run's first vertex to its
        // last (the boundary curve across islands), the boundary at its end, the contact back, the boundary at its start
        {
            var cuts = events.Select((e, i) => (e, i)).Where(x => x.e.sw || !closed && (x.e.k == 0 || x.e.k == n)).ToList();
            for (int c = 0; c + (closed ? 0 : 1) < cuts.Count; c++)
            {
                var (ea, ia) = cuts[c]; var (eb, ib) = cuts[(c + 1) % cuts.Count];
                int ka = ea.k, kb = eb.k;
                int vsteps = closed ? ((kb - ka + n) % n == 0 && cuts.Count == 1 ? n : (kb - ka + n) % n) : kb - ka;
                int esteps = closed ? ((ib - ia + ne) % ne == 0 ? ne : (ib - ia + ne) % ne) : ib - ia;
                var fS = support[ia];
                var loop = new List<(NEdge, bool)>();
                for (int q = 0; q < vsteps; q++)
                {
                    int k = (ka + q) % n;
                    if (islandPath.TryGetValue(k, out var ip))
                    {
                        // across an island the face follows the boundary curve, not the chain
                        loop.AddRange(ip.path);
                        q += (ip.m - k + n) % n - 1;
                        continue;
                    }
                    loop.Add((E[k], fwdE[k]));
                }
                loop.AddRange(Out(kb));
                for (int q = esteps - 1; q >= 0; q--) loop.Add((cB[(ia + q) % ne], false));
                loop.AddRange(In(ka));
                // orientation: the face lies on the side of the chain where the contact is
                int kp = ka % n;
                var Mp = pieces[kp].E.Curve.Eval(pieces[kp].Mid); var t = pieces[kp].Dir(pieces[kp].Mid);
                var sS = Sup(fS);
                var qb = Foot(Mp, sS);
                var nT = Outward(faB[kp], Mp) * (convex ? 1 : -1);
                if (Vec3.Dot(Vec3.Cross(nT, t), qb - Mp) < 0) loop = Reverse(loop);
                var tagB = new FaceTag($"{name}.B{c}", sS.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : sS.Def);
                body.Faces.Add(MakeFace(sS, convex ? fS.Sense : !fS.Sense, tagB, loop, sS.Project((Mp + qb) * 0.5)));
            }
        }
        // A side: one face (a band around a closed chain): the chain edges, and the contact back
        {
            var Mp = pieces[0].E.Curve.Eval(pieces[0].Mid); var t = pieces[0].Dir(pieces[0].Mid);
            var qa = Foot(Mp, SA);
            var nT = Outward(faA[0], Mp) * (convex ? 1 : -1);
            bool flip = Vec3.Dot(Vec3.Cross(nT, t), qa - Mp) < 0;
            bool sense = convex ? faA[0].Sense : !faA[0].Sense;
            var lo = Enumerable.Range(0, n).Select(k => (E[k], fwdE[k])).ToList();
            var li = Enumerable.Range(0, nseg).Select(i => (cA[i], true)).ToList();
            li = Reverse(li);
            var face = new NFace(SA, sense, tagA, SA.Project((Mp + qa) * 0.5));
            if (closed)
            {
                if (flip) { lo = Reverse(lo); li = Reverse(li); }
                var outer = new NLoop(); var inner = new NLoop();
                foreach (var (e, f) in lo) outer.Coedges.Add(new NCoedge(e, f));
                foreach (var (e, f) in li) inner.Coedges.Add(new NCoedge(e, f));
                face.Loops.Add(outer); face.Loops.Add(inner);
            }
            else
            {
                var loop = new List<(NEdge, bool)>(lo) { (secA1!, true) };
                loop.AddRange(li);
                loop.Add((secA0!, false));
                if (flip) loop = Reverse(loop);
                var l = new NLoop();
                foreach (var (e, f) in loop) l.Coedges.Add(new NCoedge(e, f));
                face.Loops.Add(l);
            }
            body.Faces.Add(face);
        }
        if (!closed)
            foreach (var (k, sign) in new[] { (0, -1.0), (n, 1.0) })
            {
                int i = EvOf(k, false);
                var j = ev[i];
                var outward = j.T * sign;
                var loop = new List<(NEdge, bool)> { (k == 0 ? secA0! : secA1!, true), (arc[i], true) };
                loop.AddRange(In(k));
                if (Vec3.Dot(Vec3.Cross(j.A - j.V, j.B - j.V), outward) < 0) loop = Reverse(loop);
                var pl = NSurface.FromPlane(Plane.FromPointNormal(j.S, outward));
                body.Faces.Add(MakeFace(pl, true, new FaceTag($"{name}.End{(k == 0 ? 0 : 1)}", SurfaceDef.PlaneDef), loop, null));
            }
        body.Link();
        // a band around a cylinder (a groove through a thicker spoke) wraps its surface: split at the seams
        try { NSeamSplit.Apply(body); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return Fail("internal: " + ex.Message); }
        body.Link();
        var err = body.Validate();
        if (Debug != null)
            foreach (var f in body.Faces)
                Debug($"  tool {f.Tag.Name} {f.Surface.Kind}: " + string.Join(" | ", f.Loops.Select(l => string.Join(" ", l.Coedges.Select(c => $"{c.Start.P}{c.Edge.Curve.GetType().Name[1..3]}")))));
        if (err != null) return Fail("internal: " + err);
        return new Result(NTess.ToSolid(body), convex, fits, null);
    }

    /// <summary>One surface for the ball: coincident, or pieces of one fillet (pipes of a radius whose spines join).</summary>
    static bool Family(NSurface a, NSurface b)
    {
        if (ReferenceEquals(a, b) || a.CoincidentWith(b, 1e-9)) return true;
        if (a.Spine is not { } sa || b.Spine is not { } sb || Math.Abs(a.R - b.R) > 1e-9 * Math.Max(1, a.R)) return false;
        var tol = 1e-7 * Math.Max(1, a.R);
        for (int i = 0; i < sa.P.Length; i++)
            for (int j = 0; j < sb.P.Length; j++)
                if (Vec3.Distance(sa.P[i], sb.P[j]) < tol && Math.Abs(Vec3.Dot(sa.D[i], sb.D[j])) > 1 - 1e-8) return true;
        return false;
    }

    /// <summary>
    /// One spine through the spines of a fillet's pieces (they share their joint nodes exactly), starting from
    /// <paramref name="seed"/>; null if they do not chain up.
    /// </summary>
    static PipeSpine? MergeSpines(List<PipeSpine> spines, PipeSpine seed)
    {
        var P = seed.P.ToList(); var D = seed.D.ToList();
        var rest = spines.Where(x => !ReferenceEquals(x, seed)).ToList();
        var tol = 1e-9 * Math.Max(1, seed.Length);
        bool Join(PipeSpine o)
        {
            // a node near the end of the chain shared with o: continue along o from there
            for (int i = P.Count - 1; i >= Math.Max(0, P.Count - 40); i--)
                for (int j = 0; j < o.P.Length; j++)
                {
                    if (Vec3.Distance(P[i], o.P[j]) > tol) continue;
                    var dd = Vec3.Dot(D[i], o.D[j]);
                    if (dd > 1 - 1e-8 && j + 1 < o.P.Length)
                    {
                        P.RemoveRange(i + 1, P.Count - i - 1); D.RemoveRange(i + 1, D.Count - i - 1);
                        for (int q = j + 1; q < o.P.Length; q++) { P.Add(o.P[q]); D.Add(o.D[q]); }
                        return true;
                    }
                    if (dd < -(1 - 1e-8) && j > 0)
                    {
                        P.RemoveRange(i + 1, P.Count - i - 1); D.RemoveRange(i + 1, D.Count - i - 1);
                        for (int q = j - 1; q >= 0; q--) { P.Add(o.P[q]); D.Add(-o.D[q]); }
                        return true;
                    }
                }
            // or one near its start: o leads into it
            for (int i = 0; i < Math.Min(P.Count, 40); i++)
                for (int j = 0; j < o.P.Length; j++)
                {
                    if (Vec3.Distance(P[i], o.P[j]) > tol) continue;
                    var dd = Vec3.Dot(D[i], o.D[j]);
                    if (dd > 1 - 1e-8 && j > 0)
                    {
                        P.RemoveRange(0, i); D.RemoveRange(0, i);
                        P.InsertRange(0, o.P.Take(j)); D.InsertRange(0, o.D.Take(j));
                        return true;
                    }
                    if (dd < -(1 - 1e-8) && j + 1 < o.P.Length)
                    {
                        P.RemoveRange(0, i); D.RemoveRange(0, i);
                        P.InsertRange(0, o.P.Skip(j + 1).Reverse()); D.InsertRange(0, o.D.Skip(j + 1).Reverse().Select(x => -x));
                        return true;
                    }
                }
            return false;
        }
        bool progress = true;
        while (rest.Count > 0 && progress)
        {
            progress = false;
            foreach (var o in rest.ToList())
                if (Join(o)) { rest.Remove(o); progress = true; }
        }
        // a closed fillet loop would join onto itself: keep the open chain (the ball does not go all the way round)
        if (P.Count < 2) return null;
        try { return new PipeSpine(P, D, seed.SupportA, seed.SupportB) { Rolls = seed.Rolls }; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    static bool Side(NFace f, NFace like) => Family(f.Surface, like.Surface) && f.Sense == like.Sense;

    /// <summary>Edges at vertex w of the faces around it (reached from edge e's faces through edges at w).</summary>
    static List<NEdge> EdgesAt(NVertex w, NEdge e)
    {
        var faces = new List<NFace>(e.Uses.Select(u => u.Face));
        var edges = new List<NEdge>();
        for (int i = 0; i < faces.Count && i < 16; i++)
            foreach (var c in faces[i].Coedges)
            {
                var x = c.Edge;
                if (!ReferenceEquals(x.V0, w) && !ReferenceEquals(x.V1, w) || edges.Contains(x)) continue;
                edges.Add(x);
                foreach (var u in x.Uses) if (!faces.Contains(u.Face)) faces.Add(u.Face);
            }
        return edges;
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

    /// <summary>Orders the edges into a chain (pieces traversed head to tail); null if they branch or are disjoint.</summary>
    static List<Piece>? Order(IReadOnlyList<NEdge> edges, out bool closed)
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
        var res = new List<Piece>();
        var e1 = closed ? edges[0] : remaining.First(x => ReferenceEquals(x.V0, cur) || ReferenceEquals(x.V1, cur));
        while (e1 != null)
        {
            remaining.Remove(e1);
            bool fwd = ReferenceEquals(e1.V0, cur);
            res.Add(fwd ? new Piece(e1, e1.T0, e1.T1) : new Piece(e1, e1.T1, e1.T0));
            cur = fwd ? e1.V1 : e1.V0;
            e1 = remaining.FirstOrDefault(x => ReferenceEquals(x.V0, cur) || ReferenceEquals(x.V1, cur));
        }
        return remaining.Count > 0 ? null : res;
    }

    /// <summary>
    /// Ball centre S on both offset surfaces whose cross-section plane (normal to the spine at S) passes through V,
    /// with the spine tangent oriented along dir.
    /// </summary>
    static (Vec3 S, Vec3 T)? SolveJoint(NSurface A2, NSurface B2, Vec3 V, Vec3 guess, Vec3 dir, double scale)
    {
        var p = guess;
        Vec3 T = dir;
        for (int it = 0; it < 60; it++)
        {
            var gA = A2.Grad(p); var gB = B2.Grad(p);
            var tt = Vec3.Cross(gA, gB);
            if (tt.Length < 1e-9) return null;
            T = tt.Normalized;
            if (Vec3.Dot(T, dir) < 0) T = -T;
            var f = new Vec3(A2.F_(p), B2.F_(p), Vec3.Dot(T, p - V));
            // Newton step with rows gA, gB, T (T treated as constant)
            var det = Vec3.Dot(gA, Vec3.Cross(gB, T));
            if (Math.Abs(det) < 1e-14) return null;
            var delta = (Vec3.Cross(gB, T) * f.X + Vec3.Cross(T, gA) * f.Y + Vec3.Cross(gA, gB) * f.Z) / det;
            p -= delta;
            if (delta.Length < 1e-14 * Math.Max(1, scale)) break;
        }
        if (Math.Abs(A2.F_(p)) > 1e-9 * scale || Math.Abs(B2.F_(p)) > 1e-9 * scale) return null;
        var gT = Vec3.Cross(A2.Grad(p), B2.Grad(p)).Normalized;
        if (Vec3.Dot(gT, dir) < 0) gT = -gT;
        if (Math.Abs(Vec3.Dot(gT, p - V)) > 1e-8 * scale) return null;
        return (p, gT);
    }

    /// <summary>
    /// Spine nodes from joint a to joint b along the intersection of the offset surfaces (steps adapted so the
    /// Hermite curve stays within tol of the exact spine), extended a little past both ends. Returns the nodes,
    /// their unit tangents, and the indices of the two joints.
    /// </summary>
    static (List<Vec3> P, List<Vec3> D, int i0, int i1)? March(NSurface A2, NSurface B2, Joint a, Joint b, double r, double length, double tol, double scale)
    {
        (Vec3 p, Vec3 t)? Node(Vec3 guess, Vec3 tref)
        {
            var p = NSurface.ProjectToIntersection(A2, B2, guess);
            if (Math.Abs(A2.F_(p)) > 1e-9 * scale || Math.Abs(B2.F_(p)) > 1e-9 * scale) return null;
            var t = Vec3.Cross(A2.Grad(p), B2.Grad(p));
            if (t.Length < 1e-9) return null;
            t = t.Normalized;
            return (p, Vec3.Dot(t, tref) < 0 ? -t : t);
        }
        double Err(Vec3 p0, Vec3 t0, Vec3 p1, Vec3 t1)
        {
            var h = Vec3.Distance(p0, p1);
            double worst = 0;
            foreach (var s in new[] { 0.25, 0.5, 0.75 })
            {
                double s2 = s * s, s3 = s2 * s;
                var q = p0 * (2 * s3 - 3 * s2 + 1) + t0 * (h * (s3 - 2 * s2 + s)) + p1 * (-2 * s3 + 3 * s2) + t1 * (h * (s3 - s2));
                var exact = NSurface.ProjectToIntersection(A2, B2, q);
                worst = Math.Max(worst, Vec3.Distance(q, exact));
            }
            return worst;
        }
        var hmax = Math.Max(Math.Min(r, length / 6), 1e-6);
        var hmin = hmax * 1e-5;

        // forward from a to b
        var P = new List<Vec3> { a.S }; var D = new List<Vec3> { a.T };
        Vec3 cur = a.S, ct = a.T;
        double h = hmax, travelled = 0;
        for (int iter = 0; ; iter++)
        {
            if (iter > 200000 || travelled > length * 4 + 10 * hmax) return null;
            var toEnd = Vec3.Distance(cur, b.S);
            if (toEnd <= h * 1.5)
            {
                if (Err(cur, ct, b.S, b.T) <= tol) { P.Add(b.S); D.Add(b.T); break; }
                h = toEnd / 2;
                if (h < hmin) return null;
            }
            var nx = Node(cur + ct * h, ct);
            if (nx == null || Vec3.Dot(nx.Value.p - cur, ct) <= 0) { h /= 2; if (h < hmin) return null; continue; }
            var e = Err(cur, ct, nx.Value.p, nx.Value.t);
            if (e > tol) { h /= 2; if (h < hmin) return null; continue; }
            P.Add(nx.Value.p); D.Add(nx.Value.t);
            travelled += Vec3.Distance(cur, nx.Value.p);
            (cur, ct) = nx.Value;
            if (e < tol / 20) h = Math.Min(h * 1.5, hmax);
        }
        int i1 = P.Count - 1;

        // short extensions past both ends, so points of the blend near its ends project inside the spine
        List<(Vec3 p, Vec3 t)> Extend(Vec3 p0, Vec3 t0)
        {
            var res = new List<(Vec3, Vec3)>();
            var want = Math.Max(0.1 * r, Math.Min(r, 0.5 * length));
            double done = 0, step = want / 2;
            while (done < want * 0.99 && step > want * 1e-3)
            {
                var nx = Node(p0 + t0 * step, t0);
                if (nx == null || Vec3.Dot(nx.Value.p - p0, t0) <= 0 || Err(p0, t0, nx.Value.p, nx.Value.t) > tol) { step /= 2; continue; }
                res.Add(nx.Value); done += Vec3.Distance(p0, nx.Value.p);
                (p0, t0) = nx.Value;
            }
            return res;
        }
        foreach (var x in Extend(b.S, b.T)) { P.Add(x.p); D.Add(x.t); }
        var back = Extend(a.S, -a.T);
        P.InsertRange(0, back.Select(x => x.p).Reverse());
        D.InsertRange(0, back.Select(x => -x.t).Reverse());
        return (P, D, back.Count, i1 + back.Count);
    }
}

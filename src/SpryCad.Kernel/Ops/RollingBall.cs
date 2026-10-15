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
        var d0 = d;
        var tol = 1e-9 * Math.Max(1, len);
        // (the old stretch as the edge is, fit noise and all: the body's edge, the tool's lying on it. But the end it
        // runs on from, no longer a vertex, on both surfaces, as the points marched on from it: past it the curve
        // follows them, not the noise)
        {
            int k = atStart ? 0 : pts.Count - 1;
            var q = NSurface.ProjectToIntersection(sa, sb, pts[k]);
            if (Math.Abs(sa.F_(q)) < 1e-7 * Math.Max(1, len) && Math.Abs(sb.F_(q)) < 1e-7 * Math.Max(1, len) && Vec3.Distance(q, pts[k]) < 1e-3 * Math.Max(1, len))
                pts[k] = cur = q;
        }
        // (spaced about as the old stretch's points: a spline through points far apart after close ones swings off)
        double spacing = 0;
        for (int i = 1; i < pts.Count; i++) spacing += Vec3.Distance(pts[i - 1], pts[i]);
        spacing /= pts.Count - 1;
        int steps = (int)Math.Clamp(Math.Ceiling(len / Math.Max(spacing, 1e-9)), 12, 96);
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
            // (on along the way the end was going only: a curve turning back, over a round and down its far side,
            // would carry the tool back past the face it is cut to)
            if (Vec3.Dot(d, d0) < 0.5) break;
        }
        if (atStart) { more.Reverse(); pts.InsertRange(0, more); } else pts.AddRange(more);
        // (the new curve runs as the edge does)
        bool rev = pc.Tb < pc.Ta;
        if (rev) pts.Reverse();
        var bs = BSplineCurve.Interpolate(pts, 3);
        var raw = new NSpline(bs);
        // (how far it strays from the surfaces, the old stretch's noise included, as fitted curves carry: points on it a
        // boolean splits it at are taken as on them within that)
        double off = 0;
        for (int i = 0, n = 4 * pts.Count; i <= n; i++)
        {
            var m = raw.Eval((double)i / n);
            off = Math.Max(off, Vec3.Distance(m, NSurface.ProjectToIntersection(sa, sb, m)));
        }
        Debug?.Invoke($"  run on {len:g3}: {more.Count} steps, the curve within {off:g3} of the surfaces");
        var c = new NSpline(bs) { FitTol = Math.Max(off, (e.Curve as NSpline)?.FitTol ?? 0) };
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
            if (j == null) { Debug?.Invoke($"no joint at {V} along {dir} ({SA.Kind}/{SB.Kind}), guess {guess}"); return Fail("the ball does not fit at an end of the edge (no centre at that distance from both faces)."); }
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
        // (one surface for the blend of the whole chain: see Joined)
        var oneBlend = !closed && n > 1 && Joined(spines, range) is { } js ? NSurface.Pipe(js, r) : null;
        for (int k = 0; k < n; k++)
        {
            var pc = pieces[k]; var sp = spines[k]; var (s0, s1) = range[k];
            int k0 = W(k), k1 = W(k + 1);
            var fwd = pc.Tb > pc.Ta;
            var E = fwd ? new NEdge(pc.E.Curve, pc.Ta, pc.Tb, VV[k0], VV[k1]) : new NEdge(pc.E.Curve, pc.Tb, pc.Ta, VV[k1], VV[k0]);
            var cAe = new NEdge(new NContact(sp, false), s0, s1, AV[k0], AV[k1]);
            var cBe = new NEdge(new NContact(sp, true), s0, s1, BV[k0], BV[k1]);
            var pipe = oneBlend ?? NSurface.Pipe(sp, r);
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
                if (Vec3.Dot(LoopNormal(loop), outward) < 0) loop = Reverse(loop);
                // (the way round the faces beside it already go: the A face's use of the section edge, the other way)
                if (body.Faces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges).FirstOrDefault(c => ReferenceEquals(c.Edge, secA[k])) is { } useA
                    && loop.First(x => ReferenceEquals(x.Item1, secA[k])).Item2 == useA.Forward) loop = Reverse(loop);
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
    /// A fillet fading out along an edge: from radius r at <paramref name="ta"/> to nothing at <paramref name="tb"/>,
    /// ρ(u) = r (1 − 3u² + 2u³) over the edge's length (no slope at either end: its start section is a constant
    /// fillet's square end there exactly, and it closes onto the edge smoothly). The ball at each station solved
    /// exactly at its own radius, the blend the envelope of balls along their centres (a pipe of varying radius), the
    /// tool closed by a square start and its tip on the edge. One edge between two faces.
    /// </summary>
    public static Result Fade(NEdge e, double ta, double tb, double r, string name, Func<NFace, Vec3, bool> onFaces, bool? kind = null, Vec3? startCenter = null)
    {
        static Result Fail(string m) => new(null, false, false, m);
        if (e.Uses.Count != 2) return Fail("the edge is not between two faces.");
        var fA = e.Uses[0].Face; var fB = e.Uses[1].Face;
        NSurface SA = fA.Surface, SB = fB.Surface;
        var pc = new Piece(e, ta, tb);
        var V0 = pc.Start; var t0d = pc.Dir(ta);
        var cA = e.Uses[0]; var cB = e.Uses[1];
        var nA = Outward(fA, V0); var nB = Outward(fB, V0);
        var uB = Vec3.Cross(nB, e.Curve.Deriv(ta) * (cB.Forward ? 1 : -1)).Normalized;
        if (Math.Abs(Vec3.Dot(nA, nB)) > 1 - 1e-9) return Fail("the faces are tangent along the edge: there is nothing to fillet.");
        // (the chain's kind where given: near tangency the faces' own turn at the fade's start can read the other way)
        bool convex = kind ?? Vec3.Dot(uB, nA) < 0;
        var len = pc.Length;
        var scale = Math.Max(len, r);

        // the edge's parameter at a fraction of its length
        const int M = 1024;
        var cum = new double[M + 1]; var tq = new double[M + 1];
        {
            var prev = pc.Start;
            for (int i = 0; i <= M; i++)
            {
                tq[i] = ta + (tb - ta) * i / M;
                var q = e.Curve.Eval(tq[i]);
                cum[i] = i == 0 ? 0 : cum[i - 1] + Vec3.Distance(prev, q);
                prev = q;
            }
        }
        double TAt(double u)
        {
            if (u <= 0) return ta;
            if (u >= 1) return tb;
            var want = cum[M] * u;
            int lo = 0, hi = M;
            while (hi - lo > 1) { int m = (lo + hi) / 2; if (cum[m] < want) lo = m; else hi = m; }
            var f = cum[hi] > cum[lo] ? (want - cum[lo]) / (cum[hi] - cum[lo]) : 0;
            return tq[lo] + (tq[hi] - tq[lo]) * f;
        }
        static double Rho(double u, double r) => r * (1 - 3 * u * u + 2 * u * u * u);
        // the ball at a fraction u of the way: its centre and the direction of the section there (the tip: on the edge)
        // (each ball rolled on from the one before it, as the chain's own end joint is: near tangency the offsets meet along
        // two branches, and a ball solved in its section alone can sit on the other one, or on none; the first the chain's
        // end ball where given)
        var memo = new SortedDictionary<double, (Vec3 S, Vec3 T)?>();
        (Vec3 S, Vec3 T)? Solve(double u, Vec3? from)
        {
            var t = TAt(u);
            var V = u <= 0 ? pc.Start : u >= 1 ? pc.End : e.Curve.Eval(t);
            var dir = pc.Dir(t);
            if (u >= 1) return (V, dir);
            var rho = Rho(u, r) * (convex ? -1 : 1);
            try
            {
                var A2 = NShell.OffsetSurface(SA, rho * (fA.Sense ? 1 : -1)); var B2 = NShell.OffsetSurface(SB, rho * (fB.Sense ? 1 : -1));
                if (from is { } g && SolveJoint(A2, B2, V, g, dir, scale) is { } j) return j;
                var na = Outward(fA, V); var nb = Outward(fB, V);
                return SolveJoint(A2, B2, V, V + (na + nb) * (rho / (1 + Vec3.Dot(na, nb))), dir, scale);
            }
            catch (InvalidOperationException) { return null; }
        }
        // the guess for u from a ball solved at u0: moved along with the edge, scaled to u's radius
        Vec3 From(double u0, Vec3 S0, double u)
        {
            var V0 = u0 <= 0 ? pc.Start : e.Curve.Eval(TAt(u0));
            var V = e.Curve.Eval(TAt(u));
            var r0 = Rho(u0, r);
            return V + (S0 - V0) * (r0 > 1e-300 ? Rho(u, r) / r0 : 1);
        }
        (Vec3 S, Vec3 T)? Station(double u)
        {
            if (memo.TryGetValue(u, out var got)) return got;
            (Vec3 S, Vec3 T)? res;
            if (u <= 0) res = Solve(0, startCenter);
            else if (u >= 1) res = Solve(1, null);
            else
            {
                // from the nearest solved before it; where that fails, crept up on in smaller steps
                var below = memo.LastOrDefault(x => x.Key < u && x.Value != null);
                if (below.Value is not { } b) { var s0 = Station(0); below = new(0, s0); b = s0 ?? default; if (s0 == null) return memo[u] = null; }
                res = Solve(u, From(below.Key, b.S, u));
                if (res == null)
                {
                    var (uc, sc) = (below.Key, b.S);
                    for (int k = 1; k <= 8; k++)
                    {
                        var uk = below.Key + (u - below.Key) * k / 8;
                        var jk = Solve(uk, From(uc, sc, uk));
                        if (jk == null) break;
                        (uc, sc) = (uk, jk.Value.S);
                        if (k == 8) res = jk;
                    }
                }
                if (res == null) Debug?.Invoke($"fade {name}: no ball at u {u:g3}, radius {Rho(u, r):g4}, the faces {Math.Acos(Math.Clamp(Vec3.Dot(Outward(fA, e.Curve.Eval(TAt(u))).Normalized, Outward(fB, e.Curve.Eval(TAt(u))).Normalized), -1, 1)) * 180 / Math.PI:g3}° apart ({SA.Kind}/{SB.Kind})");
            }
            return memo[u] = res;
        }
        // the spine through the stations: the tangents across each node's neighbours (weighted by their spacing; the start
        // the joint's own, square to the start; the tip the edge's), the radius's change and slope along it
        PipeSpine? Spine(List<double> us)
        {
            int n = us.Count - 1;
            var S = new Vec3[n + 1]; var Tn = new Vec3[n + 1];
            for (int k = 0; k <= n; k++) { var st = Station(us[k])!.Value; S[k] = st.S; Tn[k] = st.T; }
            var sArc = new double[n + 1];
            for (int k = 1; k <= n; k++) sArc[k] = sArc[k - 1] + Vec3.Distance(S[k], S[k - 1]);
            var D = new Vec3[n + 1]; var dr = new double[n + 1]; var drs = new double[n + 1];
            for (int k = 0; k <= n; k++)
            {
                dr[k] = Rho(us[k], r) - r;
                if (k == 0 || k == n) { D[k] = Tn[k]; continue; }
                double h0 = sArc[k] - sArc[k - 1], h1 = sArc[k + 1] - sArc[k];
                D[k] = ((S[k + 1] - S[k]) / h1 * h0 + (S[k] - S[k - 1]) / h0 * h1).Normalized;
                // dρ/du over ds/du (Bessel's weights again)
                var dRdu = r * (-6 * us[k] + 6 * us[k] * us[k]);
                double g0 = us[k] - us[k - 1], g1 = us[k + 1] - us[k];
                var dsdu = (h1 / g1 * g0 + h0 / g0 * g1) / (g0 + g1);
                drs[k] = dsdu > 1e-12 ? dRdu / dsdu : 0;
            }
            try { return new PipeSpine(S, D, SA, SB) { Dr = dr, DrS = drs }; }
            catch (ArgumentException) { return null; }
        }
        // stations added midway where the spine strays from the ball solved there (its centre or its radius)
        var tolS = 1e-7 * Math.Max(1, scale);
        var stations = Enumerable.Range(0, 17).Select(k => k / 16.0).ToList();
        if (stations.Any(u => Station(u) == null)) return Fail("the fillet cannot fade out along the edge.");
        // (its contacts off its faces already at these (the ball rolling onto a face beside them): not refined further)
        for (int k = 1; k < 16; k++)
        {
            var st = Station(stations[k])!.Value;
            if (!onFaces(fA, SA.Project(st.S)) || !onFaces(fB, SB.Project(st.S)))
            { Debug?.Invoke($"fade {name}: at u {stations[k]:g3} a contact off its face"); return Fail("the fade does not fit on the faces."); }
        }
        PipeSpine? sp = null;
        for (int round = 0; round < 10; round++)
        {
            sp = Spine(stations);
            if (sp == null) return Fail("internal: the fade's spine");
            var add = new List<double>();
            for (int k = 0; k + 1 < stations.Count; k++)
            {
                var um = 0.5 * (stations[k] + stations[k + 1]);
                if (Station(um) is not { } st) return Fail("the fillet cannot fade out along the edge.");
                var (s, c, _) = sp.Ball(st.S, r);
                var off = Vec3.Distance(st.S, sp.Eval(s)) + Math.Abs(Rho(um, r) - sp.Rad(s, r));
                if (off > tolS) add.Add(um);
            }
            if (add.Count == 0 || stations.Count + add.Count > 600) break;
            stations = stations.Concat(add).OrderBy(x => x).ToList();
        }
        Debug?.Invoke($"fade {name}: {stations.Count} stations");
        var S0p = Station(0)!.Value;
        var S = new[] { S0p.S }; var Tn = new[] { S0p.T };
        var pipe = NSurface.Pipe(sp!, r);

        // ---- topology: start section (as a constant fillet's end), the tip on the edge
        var A0 = SA.Project(S[0]); var B0 = SB.Project(S[0]);
        var VV = new NVertex(V0); var AV = new NVertex(A0); var BV = new NVertex(B0); var tip = new NVertex(pc.End);
        var plane0 = NSurface.FromPlane(Plane.FromPointNormal(S[0], Tn[0]));
        var secA = new NEdge(new NSection(SA, plane0, V0, A0), 0, 1, VV, AV);
        var secB = new NEdge(new NSection(SB, plane0, V0, B0), 0, 1, VV, BV);
        var circle = new NCircle(S[0], Tn[0], A0 - S[0], r);
        var tb0 = circle.Param(B0);
        if (tb0 < 0) { circle = new NCircle(S[0], -Tn[0], A0 - S[0], r); tb0 = circle.Param(B0); }
        var arc = new NEdge(circle, 0, tb0, AV, BV);
        var fwd = tb > ta;
        var E = fwd ? new NEdge(e.Curve, ta, tb, VV, tip) : new NEdge(e.Curve, tb, ta, tip, VV);
        var cAe = new NEdge(new NContact(sp, false), sp.S0, sp.S1, AV, tip);
        var cBe = new NEdge(new NContact(sp, true), sp.S0, sp.S1, BV, tip);
        var body = new NBody();
        var Mp = e.Curve.Eval(pc.Mid); var tm = pc.Dir(pc.Mid);
        var sm = 0.5 * (sp.S0 + sp.S1); var C = sp.Eval(sm);
        var qa = SA.Project(C); var qb = SB.Project(C);
        foreach (var (fsurf, face, sec, cont, q, suffix) in new[] { (SA, fA, secA, cAe, qa, "A"), (SB, fB, secB, cBe, qb, "B") })
        {
            var nT = Outward(face, Mp) * (convex ? 1 : -1);
            var loop = new List<(NEdge, bool)> { (E, fwd), (cont, false), (sec, false) };
            if (Vec3.Dot(Vec3.Cross(nT, tm), q - Mp) < 0) loop = Reverse(loop);
            body.Faces.Add(MakeFace(fsurf, convex ? face.Sense : !face.Sense, new FaceTag(name + "." + suffix, fsurf.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : fsurf.Def), loop, fsurf.Project((Mp + q) * 0.5)));
        }
        {
            var nT = -pipe.Normal(qa);
            var loop = new List<(NEdge, bool)> { (cAe, true), (cBe, false), (arc, false) };
            if (Vec3.Dot(Vec3.Cross(nT, sp.Deriv(sm).Normalized), qb - qa) < 0) loop = Reverse(loop);
            body.Faces.Add(MakeFace(pipe, false, new FaceTag(name + ".Blend", pipe.Def), loop, pipe.Project((qa + qb) * 0.5)));
        }
        {
            var outward = -Tn[0];
            var loop = new List<(NEdge, bool)> { (secA, true), (arc, true), (secB, false) };
            if (Vec3.Dot(LoopNormal(loop), outward) < 0) loop = Reverse(loop);
            if (body.Faces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges).FirstOrDefault(c => ReferenceEquals(c.Edge, secA)) is { } useA
                && loop.First(x => ReferenceEquals(x.Item1, secA)).Item2 == useA.Forward) loop = Reverse(loop);
            body.Faces.Add(MakeFace(NSurface.FromPlane(Plane.FromPointNormal(S[0], outward)), true, new FaceTag(name + ".End0", SurfaceDef.PlaneDef), loop, null));
        }
        bool fits = true;
        // (its start the chain's own end ball, checked with the chain)
        for (int i = 1; i <= 8 && fits; i++)
        {
            var c = sp.Eval(sp.S0 + (sp.S1 - sp.S0) * i / 8);
            fits = onFaces(fA, SA.Project(c)) && onFaces(fB, SB.Project(c));
            if (!fits) Debug?.Invoke($"fade {name}: at {i}/8 a contact off its face ({(onFaces(fA, SA.Project(c)) ? "" : fA.Tag.Name + " at " + SA.Project(c))} {(onFaces(fB, SB.Project(c)) ? "" : fB.Tag.Name + " at " + SB.Project(c))})");
        }
        body.Link();
        if (body.Validate() is { } err) return Fail("internal: " + err);
        return new Result(NTess.ToSolid(body), convex, fits, null);
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
            // (the turn over a quarter of the ball either way: what folds the blend is the spine bending at the ball's
            // scale; its second derivative at a point also has the wiggles of tangents solved on a fitted surface, between
            // nodes a few thousandths apart)
            var dl = 0.25 * r;
            double sa = Math.Max(sp.S0, s - dl), sb = Math.Min(sp.S1, s + dl);
            if (sb - sa < 1e-12) continue;
            var ta = sp.Deriv(sa).Normalized; var tb = sp.Deriv(sb).Normalized;
            var turn = Math.Acos(Math.Clamp(Vec3.Dot(ta, tb), -1, 1));
            var k = turn / Vec3.Distance(sp.Eval(sa), sp.Eval(sb));
            if (k * r < 0.98 || turn < 1e-12) continue;
            var N = (tb - ta).Normalized;
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
    /// A chain whose faces change along it where they meet tangentially, on one side at a time: a groove's rim edge
    /// running from a torus onto a fillet and on onto a cylinder (B changing), or a fillet rolling round earlier ones,
    /// the top of a tube along the round it leaves the part by and on up a cut's side along a round there (each side
    /// in turn). The ball rolls from one face onto the next where its foot point crosses the boundary curve between
    /// them (the switch): there the spine moves from one offset intersection to the next (both meet the offset of the
    /// boundary curve, so the spine stays C1), and the tool's side is split along the boundary curve from the chain
    /// vertex to the foot point. A side with no switch is one face between the chain ends (a band for a closed chain).
    /// </summary>
    static Result BuildComposite(List<Piece> pieces, bool closed, double r, string name, Func<NFace, Vec3, bool> onFaces)
    {
        static Result Fail(string m) => new(null, false, false, m);
        int n = pieces.Count;
        var faA = new NFace[n]; var faB = new NFace[n];
        // the faces on each side, followed along the chain: at each vertex the side whose face carries on keeps it, the
        // other may switch. A: a surface every piece has a face on, where there is one (only B switching, as before)
        NSurface? SA0 = null;
        {
            foreach (var cand in pieces[0].E.Uses.Select(u => u.Face.Surface))
                if (pieces.All(p => p.E.Uses.Any(u => OnSurface(u.Face, cand)))) { SA0 = cand; break; }
            for (int k = 0; k < n; k++)
            {
                var fs = pieces[k].E.Uses.Select(u => u.Face).ToList();
                if (fs.Count != 2 || ReferenceEquals(fs[0], fs[1])) return Fail("the edges are not all between the same two faces.");
                NFace a, b;
                if (SA0 != null)
                {
                    a = fs.FirstOrDefault(f => OnSurface(f, SA0))!;
                    if (a == null) return Fail("the edges are not all between the same two faces.");
                }
                else if (k == 0) a = fs[0];
                else if (fs.FirstOrDefault(f => Side(f, faA[k - 1])) is { } ka) a = ka;
                else if (fs.FirstOrDefault(f => Side(f, faB[k - 1])) is { } kb) a = fs.First(f => !ReferenceEquals(f, kb));
                else return Fail("the edges change faces on both sides at once: blend them separately.");
                b = fs.First(f => !ReferenceEquals(f, a));
                if (OnSurface(b, a.Surface)) return Fail("the edges are not all between the same two faces.");
                faA[k] = a; faB[k] = b;
            }
            if (SA0 != null && faA.Any(f => f.Sense != faA[0].Sense)) return Fail("the edges are not all between the same two faces.");
            if (closed && n > 1 && !Side(faA[0], faA[n - 1]) && !Side(faB[0], faB[n - 1])) return Fail("the edges change faces on both sides at once: blend them separately.");
        }
        bool SameA(int k, int m) => Side(faA[k], faA[m]);
        bool SameB(int k, int m) => Side(faB[k], faB[m]);
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
            m = sp == null || spines.Count == 1 ? f.Surface : NSurface.Pipe(sp, f.Surface.R);
            Debug?.Invoke($"  merged {fam.Count} faces, {spines.Count} spines ({string.Join(",", spines.Select(x => x.MinCurvatureRadius.ToString("g3")))}) -> {(sp == null ? "none" : sp.MinCurvatureRadius.ToString("g3") + " over " + sp.P.Length + " nodes")}");
            foreach (var x in fam) merged[x] = m;
            return m;
        }

        // a closed chain starts at a switch (on B: a closed chain switching only on A taken the other way round)
        if (closed)
        {
            bool anyB = Enumerable.Range(0, n).Any(k => !SameB((k + n - 1) % n, k));
            if (!anyB && Enumerable.Range(0, n).Any(k => !SameA((k + n - 1) % n, k))) (faA, faB) = (faB, faA);
            int s0 = Enumerable.Range(0, n).FirstOrDefault(k => !SameB((k + n - 1) % n, k), -1);
            if (s0 < 0) return Fail("the edges are not all between the same two faces.");
            pieces = pieces.Skip(s0).Concat(pieces.Take(s0)).ToList();
            faA = faA.Skip(s0).Concat(faA.Take(s0)).ToArray(); faB = faB.Skip(s0).Concat(faB.Take(s0)).ToArray();
        }
        int nj = closed ? n : n + 1;
        int Prev(int k) => closed ? (k + n - 1) % n : k - 1;     // piece before vertex k
        bool RawSwitch(int k) => (closed || k > 0 && k < n) && !SameB(Prev(k), k % n);
        bool SwitchA(int k) => (closed || k > 0 && k < n) && !SameA(Prev(k), k % n);
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

        var offsets = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        NSurface Off(NFace f)
        {
            if (!offsets.TryGetValue(f, out var o)) offsets[f] = o = NShell.OffsetSurface(Sup(f), rho * (f.Sense ? 1 : -1));
            return o;
        }
        // (A, where it is one surface along the whole chain: that surface itself, as before)
        NSurface SupA(NFace f) => SA0 != null ? f.Surface : Sup(f);
        var offsetsA = new Dictionary<NFace, NSurface>(ReferenceEqualityComparer.Instance);
        NSurface OffA(NFace f)
        {
            if (!offsetsA.TryGetValue(f, out var o)) offsetsA[f] = o = NShell.OffsetSurface(SupA(f), rho * (f.Sense ? 1 : -1));
            return o;
        }

        // the boundary curve between one side's faces before and after a switch vertex, followed (over several edges if
        // the faces are split further on) until the ball's foot reaches it, or until it comes back to the chain at another
        // vertex: then the chain dips into the next face and out again while the ball rolls on (an island under the
        // blend, on B only). The other side keeps its face there
        var walkedA = new List<NFace>(); var walkedB = new List<NFace>();
        var footEdge = new Dictionary<int, NEdge>();
        var walkOther = new Dictionary<int, NFace>();
        var walkOther2 = new Dictionary<int, NFace>();   // a second switch's, where not the first's
        // (a second switch at a vertex, through a face beside the boundary's end: its root on the edge from there, the path
        // past the boundary's end, the face it rolls through, the edge its foot reaches the next face on)
        var second = new Dictionary<int, (double root, List<(NCurve c, double ta, double tb)> segs, NFace mid, NEdge foot)>();
        // (the other side's face there: the one at the vertex, or, where the ball's foot on that side is not on it when this
        // side's foot reaches the boundary, a neighbour it is on (a narrow face the chain crosses: the foot on it before or
        // after the chain is))
        (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err) Walk(int k, bool onB)
        {
            var fo = onB ? faA : faB; var fx = onB ? faB : faA;
            var cands = new List<NFace> { fo[k % n] };
            foreach (var q in new[] { Prev(k), closed ? (k + 1) % n : k + 1, closed ? (k + n - 2) % n : k - 2 })
                if (q >= 0 && q < n && !cands.Any(c => Side(c, fo[q]))) cands.Add(fo[q]);
            // (further along, and the faces the other side's switches roll through: where the sides switch out of the
            // chain's order the other side may be a few pieces behind or ahead, or between two of its faces)
            foreach (var q in new[] { k + 2, k - 3, k + 3, k - 4 })
                if (!closed && q >= 0 && q < n && !cands.Any(c => Side(c, fo[q]))) cands.Add(fo[q]);
            foreach (var f in onB ? walkedA : walkedB) if (!cands.Any(c => Side(c, f))) cands.Add(f);
            bool OnIt(NFace c, (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err) w)
            {
                var G = w.segs[^1].c.Eval(w.root!.Value); var fb0 = fx[Prev(k)];
                var S = G + (onB ? Sup(fb0) : SupA(fb0)).Normal(G) * ((fb0.Sense ? 1 : -1) * rho);
                return onFaces(c, (onB ? SupA(c) : Sup(c)).Project(S));
            }
            var w0 = WalkWith(k, onB, cands[0]);
            walkOther[k] = cands[0];
            if (w0.root != null && OnIt(cands[0], w0)) return w0;
            foreach (var c in cands.Skip(1))
            {
                var w = WalkWith(k, onB, c);
                if (w.root != null && OnIt(c, w)) { walkOther[k] = c; return w; }
            }
            walkOther[k] = cands[0];
            return WalkWith(k, onB, cands[0]);
        }
        (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err) WalkWith(int k, bool onB, NFace otherFace, NFace? otherFace2 = null)
        {
            second.Remove(k);
            var fx = onB ? faB : faA;
            var fb0 = fx[Prev(k)]; var fb1 = fx[k % n];
            var other = onB ? OffA(otherFace) : Off(otherFace);
            // (a second switch here: against the other side's face by then, where that has switched in between)
            var other2 = otherFace2 == null ? other : onB ? OffA(otherFace2) : Off(otherFace2);
            var walked = onB ? walkedB : walkedA;
            var vtx = ChainVertex(k % n);
            var V = vtx.P;
            var segs = new List<(NCurve c, double ta, double tb)>();
            var g = fb0.Coedges.Select(c => c.Edge).FirstOrDefault(x => (ReferenceEquals(x.V0, vtx) || ReferenceEquals(x.V1, vtx))
                       && x.Uses.Any(u => ReferenceEquals(u.Face, fb1)) && !pieces.Any(p => ReferenceEquals(p.E, x)));
            if (g == null) return (null, segs, null, "the edges change faces where the faces do not meet along an edge.");
            // (smooth along the edge between them: at its middle where the corner itself is a solved point a few degrees
            // off, as the inside of a shell on a small round)
            if (Vec3.Dot(Outward(fb0, V), Outward(fb1, V)) < 1 - 1e-4 && Vec3.Dot(Outward(fb0, g.Mid), Outward(fb1, g.Mid)) < Math.Cos(2 * Math.PI / 180))
                return (null, segs, null, "the edges turn a corner between two faces: blend them separately.");
            var nsurf = onB ? Sup(fb0) : SupA(fb0); bool sense = fb0.Sense;
            Vec3 C(NCurve gc, double t) { var G = gc.Eval(t); return G + nsurf.Normal(G) * ((sense ? 1 : -1) * rho); }
            var from = vtx; var cur = g;
            for (int step = 0; step < 8; step++)
            {
                foreach (var u in cur.Uses) if (!walked.Contains(u.Face)) walked.Add(u.Face);
                // ball centres touching the boundary curve: C(t) = G(t) + rho n(G(t)); the switch is where C(t) is on the
                // other side's offset
                var tv = ReferenceEquals(cur.V0, from) ? cur.T0 : cur.T1; var tfar = ReferenceEquals(cur.V0, from) ? cur.T1 : cur.T0;
                var gc = cur.Curve;
                double F(double t) => other.F_(C(gc, t));
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
                if (next == null)
                {
                    // (the boundary ends where a third face meets both, smoothly (a flat under the round the ball
                    // rolls onto, both tangent to the fillet it leaves): the ball, too wide to touch the boundary here,
                    // rolls onto that face first and on from it onto the next. Two switches at this vertex)
                    foreach (var e1 in EdgesAt(w, cur))
                    {
                        if (ReferenceEquals(e1, cur) || e1.Uses.Count != 2 || !e1.Uses.Any(u => Side(u.Face, fb0))) continue;
                        var mid = e1.Uses.Select(u => u.Face).FirstOrDefault(f => !Side(f, fb0));
                        if (mid == null || Side(mid, fb1) || !Smooth(fb0, mid, e1)) continue;
                        var e2 = EdgesAt(w, cur).FirstOrDefault(x => x.Uses.Count == 2 && x.Uses.Any(u => ReferenceEquals(u.Face, mid))
                            && x.Uses.Any(u => Side(u.Face, fb1)) && Smooth(mid, fb1, x));
                        if (e2 == null) continue;
                        var t1v = ReferenceEquals(e1.V0, w) ? e1.T0 : e1.T1; var t1f = ReferenceEquals(e1.V0, w) ? e1.T1 : e1.T0;
                        var r1 = Root(t1v, t1f, t => other.F_(C(e1.Curve, t)));
                        if (r1 == null) continue;
                        var ms = onB ? Sup(mid) : SupA(mid); bool msense = mid.Sense;
                        Vec3 C2(double t) { var G = e2.Curve.Eval(t); return G + ms.Normal(G) * ((msense ? 1 : -1) * rho); }
                        var t2v = ReferenceEquals(e2.V0, w) ? e2.T0 : e2.T1; var t2f = ReferenceEquals(e2.V0, w) ? e2.T1 : e2.T0;
                        var r2 = Root(t2v, t2f, t => other2.F_(C2(t)));
                        if (r2 == null) continue;
                        if (!walked.Contains(mid)) walked.Add(mid);
                        footEdge[k] = e1;
                        second[k] = (r2.Value, new() { (e2.Curve, t2v, r2.Value) }, mid, e2);
                        Debug?.Invoke($"  switch at {V}: through {mid.Tag.Name} beside the boundary's end {w.P}");
                        segs.Add((e1.Curve, t1v, r1.Value));
                        return (r1, segs, null, null);
                    }
                    break;
                }
                from = w; cur = next;
            }
            Debug?.Invoke($"switch at {V}: no centre along the boundary from {g.V0.P}->{g.V1.P} ({fb0.Tag.Name}/{fb1.Tag.Name}, {segs.Count} edges)");
            return (null, segs, null, "the ball leaves the faces it blends before it reaches the next face.");
        }
        // (two faces meeting smoothly along an edge, at its middle)
        static bool Smooth(NFace a, NFace b, NEdge e) => Vec3.Dot(Outward(a, e.Mid), Outward(b, e.Mid)) > Math.Cos(2 * Math.PI / 180);
        // (where f changes sign on [t0, t1], sampled then bisected)
        static double? Root(double t0, double t1, Func<double, double> f)
        {
            const int N = 64;
            double tPrev = t0, fPrev = f(t0);
            for (int i = 1; i <= N; i++)
            {
                var t = t0 + (t1 - t0) * i / N; var fv = f(t);
                if (Math.Sign(fv) != Math.Sign(fPrev) || fv == 0)
                {
                    double lo = tPrev, hi = t, flo = fPrev;
                    for (int it = 0; it < 100 && Math.Abs(hi - lo) > 1e-15 * Math.Max(1, Math.Abs(hi)); it++)
                    {
                        var m = 0.5 * (lo + hi); var fm = f(m);
                        if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; } else hi = m;
                    }
                    return 0.5 * (lo + hi);
                }
                tPrev = t; fPrev = fv;
            }
            return null;
        }
        var walks = new Dictionary<int, (double? root, List<(NCurve c, double ta, double tb)> segs, int? back, string? err)>();
        var onA = new HashSet<int>();   // switch vertices on A
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
        var box = Box3.Empty;
        foreach (var p in pieces) foreach (var q in p.E.Samples(1e-3)) box.Add(q);
        var scale = Math.Max(box.Diagonal, r);
        var tolS = 1e-9 * Math.Max(10, scale);

        // (an island open at the chain's end: the chain's last pieces on the next face, the ball still on the face before
        // it where the chain ends (its foot reaching the boundary only past the end's section, as a big ball beside a small
        // round the chain runs up onto at its end). No switch: the ball rolls on the face before to the end; between the
        // chain, the boundary up to the end's section and that section, the next face's own piece of the tool)
        (int k, List<(NCurve c, double ta, double tb)> segs, NFace face)? endIsland = null;
        (int k, List<(NCurve c, double ta, double tb)> segs, NFace face)? EndIsland(int k, List<(NCurve c, double ta, double tb)> segs, double root)
        {
            if (closed || k <= 0 || k >= n) return null;
            for (int q = k + 1; q < n; q++) if (RawSwitch(q) || SwitchA(q)) return null;
            var fb = faB[Prev(k)];
            var G = segs[^1].c.Eval(root);
            var Ss = G + Sup(fb).Normal(G) * ((fb.Sense ? 1 : -1) * rho);
            var pc = pieces[n - 1];
            var V = pc.End; var dir = pc.Dir(pc.Tb);
            var na = Outward(faA[n - 1], V); var nb = Outward(fb, V);
            var j = SolveJoint(OffA(faA[n - 1]), Off(fb), V, V + (na + nb) * (rho / (1 + Vec3.Dot(na, nb))), dir, scale);
            if (j == null || Vec3.Dot(Ss - j.Value.S, j.Value.T) <= 1e-9 * scale) return null;
            // the boundary from vertex k as far as the end's section
            var (S, T) = j.Value;
            var cut = new List<(NCurve c, double ta, double tb)>();
            foreach (var (c, ta, tb) in segs)
            {
                if (Root(ta, tb, t => Vec3.Dot(c.Eval(t) - S, T)) is { } x) { cut.Add((c, ta, x)); return (k, cut, faB[k]); }
                cut.Add((c, ta, tb));
            }
            return null;
        }
        try
        {
            for (int k = 0; k < nj; k++)
            {
                if (SwitchA(k))
                {
                    var wa = Walk(k, false);
                    if (wa.err != null) return Fail(wa.err);
                    if (wa.back != null) return Fail("the ball leaves the faces it blends before it reaches the next face.");
                    walks[k] = wa; onA.Add(k);
                    continue;
                }
                if (!RawSwitch(k) || islandEnd.Contains(k) || InsideIsland(k)) continue;
                var wk = Walk(k, true);
                if (wk.err != null) return Fail(wk.err);
                if (wk.back == null && wk.root is { } wr && second.ContainsKey(k) == false && EndIsland(k, wk.segs, wr) is { } ei)
                {
                    Debug?.Invoke($"  island at the end from {ChainVertex(k).P}: the ball reaches {ei.face.Tag.Name} only past the end");
                    endIsland = ei; islandEnd.Add(k);
                    continue;
                }
                walks[k] = wk;
                if (wk.back is int m)
                {
                    // the island ends where the chain comes back onto the face it left
                    if (!RawSwitch(m) || !Side(faB[m % n], faB[Prev(k)]) || m == k) return Fail("the ball leaves the faces it blends before it reaches the next face.");
                    islands[k] = (m, wk.segs);
                    islandEnd.Add(k); islandEnd.Add(m);
                }
            }
        }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }
        bool IsSwitch(int k) => onA.Contains(k) || RawSwitch(k) && !islandEnd.Contains(k) && !InsideIsland(k);

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
        var shared = new Dictionary<int, int>();   // a second switch: how many pieces of its path are the first's
        int VK(int key) => key >= nj ? key - nj : key;   // the chain vertex of a switch's key
        NFace Other2(int k) => walkOther2.TryGetValue(k, out var o2) ? o2 : walkOther[k];
        double origin = 0;
        double Key(double pos) => closed ? ((pos - origin) % total + total) % total : pos;
        // (each switch's centre is found against the face the other side is on there: taken at first as the one at its
        // vertex; where the sides switch out of the chain's order (one side's foot reaching its next face before the
        // other's, though the chain meets them the other way round) that is not the one it is on by then. Found again
        // against that one and put in order again, until each is against the face it is beside)
        for (int pass = 0; ; pass++)
        {
        events.Clear(); swJoint.Clear(); swAfter.Clear(); swBefore.Clear(); shared.Clear();
        onA.RemoveWhere(x => x >= nj);
        foreach (var key in walks.Keys.Where(x => x >= nj).ToList()) walks.Remove(key);
        try
        {
            for (int k = 0; k < nj; k++)
            {
                if (!IsSwitch(k)) continue;
                bool a = onA.Contains(k);
                var (root, segs, _, _) = walks[k];
                var fb = (a ? faA : faB)[Prev(k)];
                var last = segs[^1];
                var G = last.c.Eval(root!.Value);
                var Ss = G + (a ? SupA(fb) : Sup(fb)).Normal(G) * ((fb.Sense ? 1 : -1) * rho);
                var other = a ? Off(walkOther[k]) : OffA(walkOther[k]);
                var Ts = Vec3.Cross(other.Grad(Ss), (a ? OffA(fb) : Off(fb)).Grad(Ss));
                if (Ts.Length < 1e-9) return Fail("the faces meet tangentially.");
                var dir = pieces[k % n].Dir(pieces[k % n].Ta);
                Ts = Ts.Normalized; if (Vec3.Dot(Ts, dir) < 0) Ts = -Ts;
                var foot = (a ? Sup(walkOther[k]) : SupA(walkOther[k])).Project(Ss);
                swJoint[k] = a ? new Joint(ChainVertex(k % n).P, Ss, Ts, G, foot) : new Joint(ChainVertex(k % n).P, Ss, Ts, foot, G);
                // the face the foot moves onto: across the boundary edge it was found on
                var fe = footEdge[k];
                swAfter[k] = fe.Uses.Select(u => u.Face).FirstOrDefault(f => !Side(f, fb)) ?? (a ? faA : faB)[k % n];
                swBefore[k] = fe.Uses.Select(u => u.Face).FirstOrDefault(f => Side(f, fb)) ?? fb;
                events.Add((ChainPos(Ss, Ts, k), k, true));
                // (and on from the face beside the boundary's end, a second switch (keyed past the vertices), its path
                // the first's but for its last piece)
                if (second.TryGetValue(k, out var s2))
                {
                    int k2 = k + nj;
                    var mid = s2.mid;
                    var G2 = s2.segs[^1].c.Eval(s2.root);
                    var S2 = G2 + (a ? SupA(mid) : Sup(mid)).Normal(G2) * ((mid.Sense ? 1 : -1) * rho);
                    var other2 = a ? Off(Other2(k)) : OffA(Other2(k));
                    var T2 = Vec3.Cross(other2.Grad(S2), (a ? OffA(mid) : Off(mid)).Grad(S2));
                    if (T2.Length < 1e-9) return Fail("the faces meet tangentially.");
                    T2 = T2.Normalized; if (Vec3.Dot(T2, dir) < 0) T2 = -T2;
                    var foot2 = (a ? Sup(Other2(k)) : SupA(Other2(k))).Project(S2);
                    swJoint[k2] = a ? new Joint(ChainVertex(k % n).P, S2, T2, G2, foot2) : new Joint(ChainVertex(k % n).P, S2, T2, foot2, G2);
                    swAfter[k2] = s2.foot.Uses.Select(u => u.Face).FirstOrDefault(f => !Side(f, mid)) ?? (a ? faA : faB)[k % n];
                    swBefore[k2] = mid;
                    if (a) onA.Add(k2);
                    walks[k2] = (s2.root, segs.Take(segs.Count - 1).Concat(s2.segs).ToList(), null, null);
                    shared[k2] = segs.Count - 1;
                    events.Add((ChainPos(S2, T2, k), k2, true));
                }
            }
        }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }
        for (int k = 0; k < nj; k++)
            if (!IsSwitch(k)) events.Add((k < n ? cum[k] : total, k, false));
        // order along the chain; a closed chain starts at its first switch
        origin = closed && events.Any(e => e.sw) ? events.Where(e => e.sw).Min(e => e.pos) : 0;
        events = events.OrderBy(e => Key(e.pos)).ThenBy(e => e.sw ? 1 : 0).ToList();
        if (closed || pass >= 4) break;
        var redo = new HashSet<int>();
        {
            NFace curA = faA[0], curB = faB[0];
            foreach (var (_, k, sw) in events)
            {
                if (!sw) continue;
                bool a = onA.Contains(k);
                var now = a ? curB : curA;
                var used = k >= nj ? Other2(VK(k)) : walkOther[k];
                if (!Side(now, used))
                {
                    Debug?.Invoke($"  switch at {ChainVertex(VK(k) % n).P}{(k >= nj ? " (second)" : "")}: against {now.Tag.Name} by then, not {used.Tag.Name}");
                    if (k >= nj) walkOther2[VK(k)] = now; else walkOther[k] = now;
                    redo.Add(VK(k));
                }
                if (a) curA = swAfter[k]; else curB = swAfter[k];
            }
        }
        if (redo.Count == 0) break;
        foreach (var k in redo)
        {
            var w = WalkWith(k, !onA.Contains(k), walkOther[k], walkOther2.GetValueOrDefault(k));
            if (w.err != null) return Fail(w.err);
            if (w.back != null) return Fail("the ball leaves the faces it blends before it reaches the next face.");
            walks[k] = w;
        }
        }
        int ne = events.Count;
        int nseg = closed ? ne : ne - 1;
        Debug?.Invoke("  events: " + string.Join(" ", events.Select(e => $"{(e.sw ? onA.Contains(e.k) ? "SA" : "S" : "J")}{e.k}@{Key(e.pos):0.###}" + (e.sw ? $"[{swBefore[e.k].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(swBefore[e.k].Surface) % 1000}->{swAfter[e.k].Surface.Kind}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(swAfter[e.k].Surface) % 1000}]" : ""))) + $" (chain {total:0.###})");

        // the faces the ball rolls on before the first event, then changed by each switch
        var support = new NFace[ne]; var supportA = new NFace[ne];
        {
            NFace cur = closed ? (events.Where(e => e.sw && !onA.Contains(e.k)).Select(e => swAfter[e.k]).LastOrDefault() ?? faB[0]) : faB[0];
            NFace curA = closed ? (events.Where(e => e.sw && onA.Contains(e.k)).Select(e => swAfter[e.k]).LastOrDefault() ?? faA[0]) : faA[0];
            if (!closed && IslandStartOf(0) >= 0) cur = faB[0];
            for (int i = 0; i < ne; i++)
            {
                if (events[i].sw) { if (onA.Contains(events[i].k)) curA = swAfter[events[i].k]; else cur = swAfter[events[i].k]; }
                support[i] = cur;   // the face after event i
                supportA[i] = curA;
            }
        }
        // joints, with the faces the ball rolls on there
        var ev = new Joint[ne];
        try
        {
            for (int i = 0; i < ne; i++)
            {
                var (pos, k, sw) = events[i];
                if (sw) { ev[i] = swJoint[k]; continue; }
                var fbk = support[i]; var fak = supportA[i];
                int kp = k < n ? k : n - 1;
                var pc = pieces[kp];
                var V = k < n ? pc.Start : pc.End;
                var dir = pc.Dir(k < n ? pc.Ta : pc.Tb);
                var na = Outward(fak, V); var nb = Outward(fbk, V);
                var guess = V + (na + nb) * (rho / (1 + Vec3.Dot(na, nb)));
                var j = SolveJoint(OffA(fak), Off(fbk), V, guess, dir, scale);
                if (j == null) { Debug?.Invoke($"no joint at {V} along {dir} ({SupA(fak).Kind} {fak.Tag.Name}/{Sup(fbk).Kind} {fbk.Tag.Name}), guess {guess}"); return Fail("the ball does not fit at an end of the edge (no centre at that distance from both faces)."); }
                var (S, T) = j.Value;
                var a = SupA(fak).Project(S); var b = Sup(fbk).Project(S);
                if (Vec3.Distance(a, V) < 1e-7 * scale || Vec3.Distance(b, V) < 1e-7 * scale) return Fail("the faces meet tangentially.");
                ev[i] = new Joint(V, S, T, a, b);
            }
        }
        catch (InvalidOperationException) { return Fail("the radius is larger than the curvature of a face it blends."); }

        // an open chain's end joints: the ball rolled on to them from the joint next to them, on its own branch of the
        // offsets' intersection (solved in the end's section alone, it can sit on another where two offsets touch near
        // the end, the march then going round to it)
        if (!closed && ne >= 2)
            foreach (var (iEnd, iFrom, seg, sign) in new[] { (ne - 1, ne - 2, ne - 2, 1.0), (0, 1, 0, -1.0) })
            {
                if (events[iEnd].sw) continue;
                var len = Math.Abs(Key(events[iEnd].pos) - Key(events[iFrom].pos));
                var len2 = Math.Max(len, Vec3.Distance(ev[iEnd].S, ev[iFrom].S));
                if (Reach(OffA(supportA[seg]), Off(support[seg]), ev[iFrom], ev[iEnd].V, sign, len2, r, scale) is not { } rj) continue;
                if (Vec3.Distance(rj.S, ev[iEnd].S) <= 1e-7 * scale) continue;
                var T = Vec3.Dot(rj.T, ev[iEnd].T) < 0 ? -rj.T : rj.T;
                var fak = supportA[iEnd == 0 ? 0 : seg]; var fbk = support[iEnd == 0 ? 0 : seg];
                Debug?.Invoke($"  end joint at {ev[iEnd].V}: rolled on to {rj.S}, not {ev[iEnd].S} ({Vec3.Distance(rj.S, ev[iEnd].S):g3} apart)");
                ev[iEnd] = new Joint(ev[iEnd].V, rj.S, T, SupA(fak).Project(rj.S), Sup(fbk).Project(rj.S));
            }

        // every surface the ball rolls on along the chain (the boolean does not trace a segment's blend against them)
        var rolls = support.Select(Sup).Concat(supportA.Select(SupA)).Distinct().ToList();
        // spine segments between consecutive events, on the offsets of the faces the ball rolls on
        var spines = new PipeSpine[nseg]; var range = new (double s0, double s1)[nseg];
        for (int i = 0; i < nseg; i++)
        {
            var ja = ev[i]; var jb = ev[(i + 1) % ne];
            var len = Key(events[(i + 1) % ne].pos) - Key(events[i].pos);
            if (closed && i == ne - 1) len += total;
            var SAi = SupA(supportA[i]);
            var m = March(OffA(supportA[i]), Off(support[i]), ja, jb, r, Math.Max(Math.Max(len, 0), Vec3.Distance(ja.S, jb.S)), tolS, scale);
            if (m == null)
            {
                Debug?.Invoke($"march failed on segment {i}/{nseg} ({supportA[i].Tag.Name}/{support[i].Tag.Name}) from {ja.S} to {jb.S}");
                return Fail("could not follow the rolling ball along the edge (the faces turn too sharply for this radius).");
            }
            var (P, D, i0, i1) = m.Value;
            var sp = new PipeSpine(P, D, SAi, Sup(support[i])) { Rolls = rolls };
            if (Folds(sp, r, SAi.Project, Sup(support[i]).Project)) { if (Debug != null) { var fam = walkedB.Concat(faB).Distinct().Where(f => Side(f, support[i])).ToList(); for (int q = 0; q <= 10; q++) { var c = sp.Eval(sp.S0 + (sp.S1 - sp.S0) * q / 10); var pb = Sup(support[i]).Project(c); Debug($"    foot {q}: {pb} on fillet faces {fam.Any(f => onFaces(f, pb))}, A {faA.Any(f => onFaces(f, SAi.Project(c)))}"); } } Debug?.Invoke($"  sharpest at {string.Join(" ", Enumerable.Range(1, P.Count - 2).OrderByDescending(q => Math.Acos(Math.Clamp(Vec3.Dot(D[q - 1].Normalized, D[q + 1].Normalized), -1, 1)) / Math.Max(1e-12, Vec3.Distance(P[q - 1], P[q + 1]))).Take(3).Select(q => $"#{q} {P[q]} gap {Vec3.Distance(P[q - 1], P[q]):g2}/{Vec3.Distance(P[q], P[q + 1]):g2} turn {Math.Acos(Math.Clamp(Vec3.Dot(D[q - 1].Normalized, D[q + 1].Normalized), -1, 1)) * 180 / Math.PI:g3}°"))}"); Debug?.Invoke($"fold on segment {i}/{nseg} ({supportA[i].Tag.Name}:{supportA[i].Surface.Kind} / {support[i].Tag.Name}:{support[i].Surface.Kind}) {ev[i].S} -> {ev[(i + 1) % ne].S}: min curvature radius {sp.MinCurvatureRadius:g3}, {P.Count} nodes"); return Fail("the radius is too large: the fillet would fold over itself."); }
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
        // sections at the open ends, boundary paths at switches (chain vertex to foot point), on each side
        NVertex? endX = null;   // an island at the end: where the boundary crosses the end's section
        var bndA = new Dictionary<int, List<(NEdge e, bool fwd)>>();     // by chain vertex
        var bnd = new Dictionary<int, List<(NEdge e, bool fwd)>>();
        if (!closed)
            foreach (var k in new[] { 0, n })
            {
                int i = EvOf(k, false);
                var j = ev[i];
                var plane = NSurface.FromPlane(Plane.FromPointNormal(j.S, j.T));
                bndA[k] = new() { (new NEdge(new NSection(SupA(supportA[i]), plane, j.V, j.A), 0, 1, VV[k], AV[i]), true) };
                if (k == n && endIsland is { } ei)
                {
                    // (across the island's face from the chain's end to the boundary, then on the face the ball is on)
                    var (gc, _, gb) = ei.segs[^1];
                    var X = gc.Eval(gb);
                    endX = new NVertex(X);
                    bnd[k] = new()
                    {
                        (new NEdge(new NSection(Sup(ei.face), plane, j.V, X), 0, 1, VV[k], endX), true),
                        (new NEdge(new NSection(Sup(support[i]), plane, X, j.B), 0, 1, endX, BV[i]), true),
                    };
                }
                else bnd[k] = new() { (new NEdge(new NSection(Sup(support[i]), plane, j.V, j.B), 0, 1, VV[k], BV[i]), true) };
            }
        foreach (var (k, _) in swJoint)
        {
            bool a = onA.Contains(k);
            var path = walks[k].segs;
            var list = new List<(NEdge, bool)>();
            var v0 = VV[VK(k)];
            // (a second switch's path: the first's edges as far as they are the same)
            int q0 = shared.GetValueOrDefault(k);
            if (q0 > 0)
            {
                var firstPath = (a ? bndA : bnd)[VK(k)];
                for (int q = 0; q < q0; q++) list.Add(firstPath[q]);
                var (le, lf) = firstPath[q0 - 1];
                v0 = lf ? le.V1 : le.V0;
            }
            for (int q = q0; q < path.Count; q++)
            {
                var (gc, ga, gb) = path[q];
                var v1 = q == path.Count - 1 ? (a ? AV : BV)[EvOf(k, true)] : new NVertex(gc.Eval(gb));
                list.Add(ga < gb ? (new NEdge(gc, ga, gb, v0, v1), true) : (new NEdge(gc, gb, ga, v1, v0), false));
                v0 = v1;
            }
            (a ? bndA : bnd)[k] = list;
        }
        IEnumerable<(NEdge, bool)> Out(int k) => bnd[k];
        IEnumerable<(NEdge, bool)> In(int k) => Enumerable.Reverse(bnd[k]).Select(x => (x.e, !x.fwd));
        IEnumerable<(NEdge, bool)> OutA(int k) => bndA[k];
        IEnumerable<(NEdge, bool)> InA(int k) => Enumerable.Reverse(bndA[k]).Select(x => (x.e, !x.fwd));

        var body = new NBody();
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
        var bFaces = walkedB.Concat(faB).Distinct().ToList();
        var aFaces = walkedA.Concat(faA).Distinct().ToList();
        var oneBlendC = !closed && nseg > 1 && Joined(spines, range) is { } jsc ? NSurface.Pipe(jsc, r) : null;
        for (int i = 0; i < nseg; i++)
        {
            var sp = spines[i]; var (s0, s1) = range[i];
            int i1 = (i + 1) % ne;
            cA[i] = new NEdge(new NContact(sp, false), s0, s1, AV[i], AV[i1]);
            cB[i] = new NEdge(new NContact(sp, true), s0, s1, BV[i], BV[i1]);
            // blend face
            var sm = 0.5 * (s0 + s1);
            var SAi = SupA(supportA[i]);
            // (the contacts themselves: projected onto the supports, a centre near a round's axis (a ball nearly its size)
            // can land on the far side)
            var qa = cA[i].Curve.Eval(sm); var qb = cB[i].Curve.Eval(sm);
            var pipe = oneBlendC ?? NSurface.Pipe(sp, r);
            var nT = (sp.Eval(sm) - qa).Normalized;
            var loop = new List<(NEdge, bool)> { (cA[i], true), (arc[i1], true), (cB[i], false), (arc[i], false) };
            if (Vec3.Dot(Vec3.Cross(nT, sp.Deriv(sm).Normalized), qb - qa) < 0) loop = Reverse(loop);
            body.Faces.Add(MakeFace(pipe, false, new FaceTag(name + ".Blend", pipe.Def), loop, pipe.Project((qa + qb) * 0.5)));
            for (int q = 0; q <= 8 && fits; q++)
            {
                // (not at an end where the ball switches faces: its foot there is on the seam between them, and taken as on
                // the one it rolls onto only within the tolerance, so a hair off it reads as running past)
                if (q == 0 && events[i].sw || q == 8 && events[(i + 1) % ne].sw) continue;
                var c = sp.Eval(s0 + (s1 - s0) * q / 8);
                var pa = SAi.Project(c); var pb = Sup(support[i]).Project(c);
                bool onAf = aFaces.Any(f => Side(f, supportA[i]) && onFaces(f, pa)), onBf = bFaces.Any(f => Side(f, support[i]) && onFaces(f, pb));
                fits = onAf && onBf;
                if (!fits) Debug?.Invoke($"  overflows on segment {i} at {q}/8: A foot {pa} on A {onAf}, B foot {pb} on B {onBf} ({supportA[i].Tag.Name}/{support[i].Tag.Name})");
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

        // the island at the end: the chain from its vertex to the end, the end's section to the boundary, the boundary back
        List<(NEdge, bool)>? endPath = null;
        if (endIsland is { } eis)
        {
            int k = eis.k;
            endPath = new List<(NEdge, bool)>();
            var v0 = VV[k];
            for (int i = 0; i < eis.segs.Count; i++)
            {
                var (gc, ga, gb) = eis.segs[i];
                var v1 = i == eis.segs.Count - 1 ? endX! : new NVertex(gc.Eval(gb));
                endPath.Add(ga < gb ? (new NEdge(gc, ga, gb, v0, v1), true) : (new NEdge(gc, gb, ga, v1, v0), false));
                v0 = v1;
            }
            var loop = new List<(NEdge, bool)>();
            for (int q = k; q < n; q++) loop.Add((E[q], fwdE[q]));
            loop.Add(bnd[n][0]);
            loop.AddRange(Enumerable.Reverse(endPath).Select(x => (x.Item1, !x.Item2)));
            var fI = eis.face;
            var sI = Sup(fI);
            var Mp = pieces[k].E.Curve.Eval(pieces[k].Mid); var t = pieces[k].Dir(pieces[k].Mid);
            var (gm, gma, gmb) = eis.segs[eis.segs.Count / 2];
            var qI = gm.Eval(0.5 * (gma + gmb));
            var nT = Outward(fI, Mp) * (convex ? 1 : -1);
            if (Vec3.Dot(Vec3.Cross(nT, t), qI - Mp) < 0) loop = Reverse(loop);
            var tagI = new FaceTag($"{name}.I{k}", sI.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : sI.Def);
            body.Faces.Add(MakeFace(sI, convex ? fI.Sense : !fI.Sense, tagI, loop, sI.Project((Mp + qI) * 0.5)));
        }

        // each side: one face per run between its switches (or open ends): the chain edges from the run's first vertex
        // to its last (on B, the boundary curve across islands), the boundary at its end, the contact back, the boundary
        // at its start
        foreach (var sideB in new[] { true, false })
        {
            var cuts = events.Select((e, i) => (e, i)).Where(x => x.e.sw && onA.Contains(x.e.k) != sideB || !closed && !x.e.sw && (x.e.k == 0 || x.e.k == n)).ToList();
            // (a closed chain's side with no switch: one band, below)
            if (cuts.Count == 0) continue;
            var sup = sideB ? support : supportA; var fx = sideB ? faB : faA; var cont = sideB ? cB : cA;
            for (int c = 0; c + (closed ? 0 : 1) < cuts.Count; c++)
            {
                var (ea, ia) = cuts[c]; var (eb, ib) = cuts[(c + 1) % cuts.Count];
                int ka = VK(ea.k), kb = VK(eb.k);
                int vsteps = closed ? ((kb - ka + n) % n == 0 && cuts.Count == 1 ? n : (kb - ka + n) % n) : kb - ka;
                int esteps = closed ? ((ib - ia + ne) % ne == 0 ? ne : (ib - ia + ne) % ne) : ib - ia;
                var fS = sup[ia];
                var loop = new List<(NEdge, bool)>();
                // (the face the ball rolls through between a vertex's two switches: beside the chain, between the two
                // paths where they part)
                if (ka == kb && eb.k == ea.k + nj && shared.TryGetValue(eb.k, out var sh))
                {
                    loop.AddRange((sideB ? Out(eb.k) : OutA(eb.k)).Skip(sh));
                    for (int q = esteps - 1; q >= 0; q--) loop.Add((cont[(ia + q) % ne], false));
                    var back = (sideB ? In(ea.k) : InA(ea.k)).ToList();
                    loop.AddRange(back.Take(back.Count - sh));
                    var sM = sideB ? Sup(fS) : SupA(fS);
                    bool mSense = convex ? fS.Sense : !fS.Sense;
                    var ln = LoopNormal(loop);
                    var anchor = loop[0].Item1.Mid;
                    if (Vec3.Dot(ln, sM.Normal(sM.Project(anchor)) * (mSense ? 1 : -1)) < 0) loop = Reverse(loop);
                    var tagM = new FaceTag(sideB ? $"{name}.B{c}" : $"{name}.A{c}", sM.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : sM.Def);
                    body.Faces.Add(MakeFace(sM, mSense, tagM, loop, sM.Project(anchor)));
                    continue;
                }
                bool toEndIsland = false;
                for (int q = 0; q < vsteps; q++)
                {
                    int k = (ka + q) % n;
                    if (sideB && islandPath.TryGetValue(k, out var ip))
                    {
                        // across an island the face follows the boundary curve, not the chain
                        loop.AddRange(ip.path);
                        q += (ip.m - k + n) % n - 1;
                        continue;
                    }
                    if (sideB && endIsland?.k == k) { loop.AddRange(endPath!); toEndIsland = true; break; }
                    loop.Add((E[k], fwdE[k]));
                }
                // (past an island at the end: the end's section from where the boundary crosses it)
                loop.AddRange(sideB ? Out(eb.k).Skip(toEndIsland ? 1 : 0) : OutA(eb.k));
                for (int q = esteps - 1; q >= 0; q--) loop.Add((cont[(ia + q) % ne], false));
                loop.AddRange(sideB ? In(ea.k) : InA(ea.k));
                // orientation: the face lies on the side of the chain where the contact is
                int kp = ka % n;
                var Mp = pieces[kp].E.Curve.Eval(pieces[kp].Mid); var t = pieces[kp].Dir(pieces[kp].Mid);
                var sS = sideB ? Sup(fS) : SupA(fS);
                var qb = Foot(Mp, sS);
                var nT = Outward(fx[kp], Mp) * (convex ? 1 : -1);
                if (Vec3.Dot(Vec3.Cross(nT, t), qb - Mp) < 0) loop = Reverse(loop);
                var runs = cuts.Count - (closed ? 0 : 1);
                var tag = new FaceTag(sideB ? $"{name}.B{c}" : runs == 1 ? name + ".A" : $"{name}.A{c}", sS.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : sS.Def);
                body.Faces.Add(MakeFace(sS, convex ? fS.Sense : !fS.Sense, tag, loop, sS.Project((Mp + qb) * 0.5)));
            }
        }
        // a closed chain's A side with no switch: one band, the chain edges and the contact back
        if (closed && onA.Count == 0)
        {
            var SA = SupA(faA[0]);
            var Mp = pieces[0].E.Curve.Eval(pieces[0].Mid); var t = pieces[0].Dir(pieces[0].Mid);
            var qa = Foot(Mp, SA);
            var nT = Outward(faA[0], Mp) * (convex ? 1 : -1);
            bool flip = Vec3.Dot(Vec3.Cross(nT, t), qa - Mp) < 0;
            bool sense = convex ? faA[0].Sense : !faA[0].Sense;
            var lo = Enumerable.Range(0, n).Select(k => (E[k], fwdE[k])).ToList();
            var li = Enumerable.Range(0, nseg).Select(i => (cA[i], true)).ToList();
            li = Reverse(li);
            var face = new NFace(SA, sense, new FaceTag(name + ".A", SA.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : SA.Def), SA.Project((Mp + qa) * 0.5));
            if (flip) { lo = Reverse(lo); li = Reverse(li); }
            var outer = new NLoop(); var inner = new NLoop();
            foreach (var (e, f) in lo) outer.Coedges.Add(new NCoedge(e, f));
            foreach (var (e, f) in li) inner.Coedges.Add(new NCoedge(e, f));
            face.Loops.Add(outer); face.Loops.Add(inner);
            body.Faces.Add(face);
        }
        if (!closed)
            foreach (var (k, sign) in new[] { (0, -1.0), (n, 1.0) })
            {
                int i = EvOf(k, false);
                var j = ev[i];
                var outward = j.T * sign;
                var loop = new List<(NEdge, bool)>(OutA(k)) { (arc[i], true) };
                loop.AddRange(In(k));
                if (Vec3.Dot(LoopNormal(loop), outward) < 0) loop = Reverse(loop);
                // (the way round the faces beside it already go: the A face's use of the section edge, the other way)
                var secAk = bndA[k][0].e;
                if (body.Faces.SelectMany(f => f.Loops).SelectMany(l => l.Coedges).FirstOrDefault(c => ReferenceEquals(c.Edge, secAk)) is { } useA
                    && loop.First(x => ReferenceEquals(x.Item1, secAk)).Item2 == useA.Forward) loop = Reverse(loop);
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

    /// <summary>
    /// The tool with its end cap at a run out replaced by the fillet carried on to a point: where the blended edge's
    /// faces turn tangent at its end vertex v (a round fading out where a cut's side runs up into its top), the tool
    /// stops short at stop point q on edge e (its faces a few degrees apart there, as far as the ball marches) and is
    /// capped. From q to v the ball's centre is solved in each section across the edge directly (sliding its contact
    /// along one face until it is the radius from the other: well posed however near tangent the faces are), and is
    /// on the faces' common normal at v itself, where it touches both at v. The tip's blend surface narrows to
    /// nothing there, its contact edges meeting at v; it takes the cap's edges, so the tool runs on through without a
    /// seam. Null where it cannot be made (the capped tool stands).
    /// </summary>
    internal static Solid? Tip(Solid tool, NEdge e, NVertex v, Vec3 q, double r, bool convex)
    {
        if (tool.Native == null) return null;
        var nb = tool.Native.Clone(out var emap);
        nb.Link();
        var scale = Math.Max(1, nb.Bounds.Diagonal);
        var tol = 1e-7 * scale;
        // the cap: a planar end with a corner at q
        var cap = nb.Faces.FirstOrDefault(f => f.Surface.Kind == SurfaceKind.Plane && (f.Tag.Name.EndsWith(".End0", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".End1", StringComparison.Ordinal))
            && f.Coedges.Count() == 3 && f.Coedges.Any(c => Vec3.Distance(c.Start.P, q) < 1e-6 * scale));
        if (cap == null) return null;
        var arcCo = cap.Coedges.FirstOrDefault(c => c.Edge.Curve is NCircle);
        if (arcCo == null) return null;
        var arc = arcCo.Edge; var circle = (NCircle)arc.Curve;
        NFace Other(NEdge x, NFace not) => x.Uses.Select(u => u.Face).First(f => !ReferenceEquals(f, not));
        var blendT = Other(arc, cap);
        if (blendT.Surface.Spine is not { } mainSpine) return null;
        var secs = cap.Coedges.Where(c => !ReferenceEquals(c.Edge, arc)).Select(c => c.Edge).ToList();
        var vq = cap.Coedges.Select(c => c.Start).First(x => Vec3.Distance(x.P, q) < 1e-6 * scale);
        // which section is on which support: the face beside it, and the body face of e on that surface
        var (fa, fb) = (e.Uses[0].Face, e.Uses[1].Face);
        var sideA = Other(secs[0], cap); var sideB = Other(secs[1], cap);
        if (!sideA.Surface.CoincidentWith(fa.Surface, 1e-9) && !Family(sideA.Surface, fa.Surface)) { (sideA, sideB) = (sideB, sideA); (secs[0], secs[1]) = (secs[1], secs[0]); }
        if (!sideA.Surface.CoincidentWith(fa.Surface, 1e-9) && !Family(sideA.Surface, fa.Surface)) return null;
        var (secA, secB) = (secs[0], secs[1]);
        NVertex Far(NEdge x) => ReferenceEquals(x.V0, vq) ? x.V1 : x.V0;
        var vA0 = Far(secA); var vB0 = Far(secB);
        NSurface SA = sideA.Surface, SB = sideB.Surface;
        double rho = convex ? -r : r;
        Vec3 OutA(Vec3 p) => SA.Normal(p).Normalized * (fa.Sense ? 1 : -1);
        double DistB(Vec3 p) => SB.F_(p) * (fb.Sense ? 1 : -1);

        // the edge from q to v, and the ball's centres along it
        double tq = e.Curve.Param(q, e.Curve.Periodic ? 0.5 * (e.T0 + e.T1) : null);
        double tv = ReferenceEquals(e.V0, v) ? e.T0 : e.T1;
        if (Math.Abs(tv - tq) < 1e-12) return null;
        var S0 = circle.C;
        var T0 = circle.Axis.Normalized;
        if (Vec3.Dot(T0, v.P - q) < 0) T0 = -T0;
        var P = new List<Vec3> { S0 };
        var dPrev = (vA0.P - q).Normalized;
        const int N = 24;
        for (int k = 1; k < N; k++)
        {
            // (closer together towards v, where the blend narrows fastest)
            var f = 1 - Math.Pow(1 - (double)k / N, 2);
            var ek = e.Curve.Eval(tq + (tv - tq) * f);
            var tan = e.Curve.Deriv(tq + (tv - tq) * f).Normalized;
            var nA = OutA(ek);
            var dA = Vec3.Cross(tan, nA).Normalized;
            if (Vec3.Dot(dA, dPrev) < 0) dA = -dA;
            dPrev = dA;
            Vec3 Centre(double lam) { var qa = SA.Project(ek + dA * lam); return qa + OutA(qa) * rho; }
            double G(double lam) => DistB(Centre(lam)) - rho;
            double lo = 0, glo = G(0), hi = 1e-4 * r, ghi = G(hi);
            while (Math.Sign(ghi) == Math.Sign(glo) && hi < 4 * r) { hi *= 2; ghi = G(hi); }
            if (Math.Sign(ghi) == Math.Sign(glo)) return null;
            for (int it = 0; it < 100; it++)
            {
                var m = 0.5 * (lo + hi); var gm = G(m);
                if (Math.Sign(gm) == Math.Sign(glo)) { lo = m; glo = gm; } else hi = m;
            }
            // (on the main blend's curve of centres: sections across the edge are not across that curve, and near the cap
            // one can fall a little behind the last, on what the main blend covers. Only centres going on)
            var ck = Centre(0.5 * (lo + hi));
            if (Vec3.Dot(ck - P[^1], P.Count == 1 ? T0 : P[^1] - P[^2]) > 1e-9 * scale) P.Add(ck);
        }
        // at v: touching both faces there
        P.Add(v.P + OutA(v.P) * rho);
        // tangents: the cap's at the start (the main blend's section there), differences between, the edge's at v
        var D = new List<Vec3> { T0 };
        for (int k = 1; k < P.Count - 1; k++) D.Add((P[k + 1] - P[k - 1]).Normalized);
        // (at v the way the centres come in: the edge's own direction there where they come in along it, but where the
        // centres' curve meets the edge at an angle (two offsets touching there) that turns the spine through it at its
        // very end, a kink no fit follows. From the last three, a parabola's)
        if (P.Count < 3) D.Add((P[^1] - P[^2]).Normalized);
        else
        {
            Vec3 a0 = P[^3], a1 = P[^2], a2 = P[^1];
            double h1 = Vec3.Distance(a1, a0), h2 = Vec3.Distance(a2, a1);
            var tEnd = (a2 - a1) * ((2 * h2 + h1) / (h2 * (h1 + h2))) - (a1 - a0) * (h2 / (h1 * (h1 + h2)));
            D.Add(tEnd.Normalized);
        }
        for (int k = 1; k < P.Count; k++) if (Vec3.Distance(P[k], P[k - 1]) < 1e-9 * scale) return null;
        PipeSpine sp;
        try { sp = new PipeSpine(P, D, SA, SB) { Rolls = mainSpine.Rolls.Concat(new[] { SA, SB }).Distinct().ToList() }; }
        catch (ArgumentException) { return null; }
        var pipe = NSurface.Pipe(sp, r);
        double s0 = sp.S[0], s1 = sp.S[^1];

        // the tip's edges and faces: the edge's piece q..v, the contacts from the cap's corners to v
        var vV = new NVertex(v.P);
        var eTip = tq < tv ? new NEdge(e.Curve, tq, tv, vq, vV) : new NEdge(e.Curve, tv, tq, vV, vq);
        var cA = new NEdge(new NContact(sp, false), s0, s1, vA0, vV);
        var cB = new NEdge(new NContact(sp, true), s0, s1, vB0, vV);
        // each face's loop: from a cap edge, the way round opposite to the main tool's face's use of it, then on by
        // the vertex it ends at
        List<(NEdge, bool)>? Loop(NEdge first, NFace mainUser, params NEdge[] rest)
        {
            var use = mainUser.Coedges.First(c => ReferenceEquals(c.Edge, first));
            var loop = new List<(NEdge, bool)> { (first, !use.Forward) };
            var end = use.Forward ? first.V0 : first.V1;
            var left = rest.ToList();
            while (left.Count > 0)
            {
                var nx = left.FirstOrDefault(x => ReferenceEquals(x.V0, end) || ReferenceEquals(x.V1, end));
                if (nx == null) return null;
                var fwd = ReferenceEquals(nx.V0, end);
                loop.Add((nx, fwd));
                end = fwd ? nx.V1 : nx.V0;
                left.Remove(nx);
            }
            return ReferenceEquals(end, loop[0].Item2 ? loop[0].Item1.V0 : loop[0].Item1.V1) ? loop : null;
        }
        var la = Loop(secA, sideA, eTip, cA); var lb = Loop(secB, sideB, eTip, cB); var lt = Loop(arc, blendT, cB, cA);
        if (la == null || lb == null || lt == null) return null;
        var tipA = MakeFace(SA, sideA.Sense, sideA.Tag, la, null);
        var tipB = MakeFace(SB, sideB.Sense, sideB.Tag, lb, null);
        var tipT = MakeFace(pipe, blendT.Sense, blendT.Tag, lt, null);
        nb.Faces.Remove(cap);
        nb.Faces.Add(tipA); nb.Faces.Add(tipB); nb.Faces.Add(tipT);
        nb.Link();
        var err = nb.Validate();
        if (err != null) { Debug?.Invoke($"tip at {v.P}: {err}"); return null; }
        return NTess.ToSolid(nb);
    }

    /// <summary>One surface for the ball: coincident, or pieces of one fillet (pipes of a radius whose spines join).</summary>
    internal static bool Family(NSurface a, NSurface b)
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
    /// <summary>
    /// The spines of a chain's segments (each between its range, with the run-on past the chain's two ends) as one
    /// spine: the same curve (each piece is Hermite between the same nodes), so the blend of the whole chain is one
    /// surface. Its pieces as separate surfaces part by their run-ons' noise where they meet, and a fillet rolling on
    /// that blend later (on the pieces merged) sat a few hundred-thousandths off them. Null where they do not join.
    /// </summary>
    static PipeSpine? Joined(PipeSpine[] spines, (double s0, double s1)[] range)
    {
        var P = new List<Vec3>(); var D = new List<Vec3>();
        for (int i = 0; i < spines.Length; i++)
        {
            var sp = spines[i]; var (s0, s1) = range[i];
            for (int j = 0; j < sp.P.Length; j++)
            {
                var s = sp.S[j];
                // (inside the range; the first segment's run-on before it, the last's after it)
                if (i > 0 && s < s0 - 1e-12 || i < spines.Length - 1 && s > s1 + 1e-12) continue;
                if (P.Count > 0)
                {
                    var d = Vec3.Distance(P[^1], sp.P[j]);
                    if (d < 1e-9) continue;
                    // (consecutive pieces meet at the same joint, along the same tangent)
                    if (i > 0 && s <= s0 + 1e-12) { if (d > 1e-6 || Vec3.Dot(D[^1], sp.D[j]) < 1 - 1e-6) { Debug?.Invoke($"  not joined at segment {i}: {d:g3} apart, tangents {Vec3.Dot(D[^1], sp.D[j]):R}"); return null; } }
                }
                P.Add(sp.P[j]); D.Add(sp.D[j]);
            }
        }
        Debug?.Invoke($"  joined {spines.Length} spines into {P.Count} nodes");
        // (every surface the pieces' balls roll on: the boolean does not trace the blend against them, where it only
        // touches them, tangentially, along its contact)
        var rolls = spines.SelectMany(s => new[] { s.SupportA, s.SupportB }.Concat(s.Rolls)).OfType<NSurface>().Distinct().ToList();
        try { return P.Count >= 2 ? new PipeSpine(P, D, spines[0].SupportA, null) { Rolls = rolls } : null; }
        catch (ArgumentException) { return null; }
    }

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

    /// <summary>
    /// The loop's area normal (Newell's, over points along every edge, in its direction): an end face's outline, whose
    /// corner and contact points alone make a sliver of a triangle where the faces are nearly tangent and its sign
    /// unreliable, the arc between the contacts giving it its area.
    /// </summary>
    static Vec3 LoopNormal(List<(NEdge e, bool f)> loop)
    {
        var pts = new List<Vec3>();
        foreach (var (e, f) in loop)
            for (int i = 0; i < 8; i++) pts.Add(e.At(f ? i / 8.0 : 1 - i / 8.0));
        var n = Vec3.Zero;
        for (int i = 0; i < pts.Count; i++) n += Vec3.Cross(pts[i], pts[(i + 1) % pts.Count]);
        return n;
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
    /// <summary>
    /// The ball's centre in the section through chain point V, reached by rolling on from joint a (along the offsets'
    /// intersection the way a's tangent points, sign -1 backwards) until that section: on a's branch of the
    /// intersection, where solving in the section alone can find another (two offsets touching near V, as two rounds
    /// both tangent to a top where an edge between them ends). Null where it does not get there within reach.
    /// </summary>
    static (Vec3 S, Vec3 T)? Reach(NSurface A2, NSurface B2, Joint a, Vec3 V, double sign, double length, double r, double scale)
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
        // (the section through V: where the curve's normal plane holds it, ahead of the ball)
        static double G(Vec3 p, Vec3 t, Vec3 V) => Vec3.Dot(t, V - p);
        Vec3 cur = a.S, ct = a.T * sign;
        if (G(cur, ct, V) <= 0) return null;
        var hmax = Math.Max(Math.Min(r, length / 6), 1e-6) / 2;
        double h = hmax, travelled = 0;
        while (travelled < 2 * length + 4 * hmax)
        {
            var nx = Node(cur + ct * h, ct);
            if (nx == null || Vec3.Dot(nx.Value.p - cur, ct) <= 0) { h /= 2; if (h < hmax * 1e-6) return null; continue; }
            // (a step turning sharply has jumped across to another branch: shorter)
            if (Vec3.Dot(nx.Value.t, ct) < Math.Cos(10 * Math.PI / 180)) { h /= 2; if (h < hmax * 1e-6) return null; continue; }
            if (G(nx.Value.p, nx.Value.t, V) <= 0)
            {
                (Vec3 p, Vec3 t) lo = (cur, ct), hi = nx.Value;
                for (int it = 0; it < 80 && Vec3.Distance(lo.p, hi.p) > 1e-13 * Math.Max(1, scale); it++)
                {
                    if (Node((lo.p + hi.p) * 0.5, lo.t) is not { } mid) return null;
                    if (G(mid.p, mid.t, V) > 0) lo = mid; else hi = mid;
                }
                var t = hi.t * sign;
                return (hi.p, t);
            }
            travelled += Vec3.Distance(cur, nx.Value.p);
            (cur, ct) = nx.Value;
            h = Math.Min(h * 1.5, hmax);
        }
        return null;
    }

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
        // (no more exact than its ends: joints solved on a fitted surface, an offset of a shell's inside, sit up to
        // ~1e-7 off the intersection, and a step from one is off by that however short)
        double JointOff(Vec3 p) => Math.Max(Math.Abs(A2.F_(p)), Math.Abs(B2.F_(p)));
        tol = Math.Max(tol, 4 * Math.Max(JointOff(a.S), JointOff(b.S)));
        // (off the intersection itself, where the surfaces meet at a shallow angle, a few degrees, many times as far as
        // off either surface: a step from such a joint, however short, is off by that. Only a short step's noise)
        double JointAway(Vec3 p) => Vec3.Distance(p, NSurface.ProjectToIntersection(A2, B2, p));
        var noise = Math.Max(1e-6 * scale, 4 * Math.Max(JointAway(a.S), JointAway(b.S)));

        // forward from a to b
        var P = new List<Vec3> { a.S }; var D = new List<Vec3> { a.T };
        Vec3 cur = a.S, ct = a.T;
        double h = hmax, travelled = 0;
        for (int iter = 0; ; iter++)
        {
            if (iter > 200000 || travelled > length * 4 + 10 * hmax) { Debug?.Invoke($"    march lost: {travelled:g3} travelled for {length:g3}, at {cur}; from {a.S} along {a.T} to {b.S} along {b.T}; path {string.Join(" ", P.Where((_, i) => i % 4 == 0).Take(30))}"); return null; }
            var toEnd = Vec3.Distance(cur, b.S);
            if (toEnd <= h * 1.5)
            {
                var eEnd = Err(cur, ct, b.S, b.T);
                // (right at the end, an error above the tolerance is the surfaces' noise, an offset of a fitted one such
                // as another fillet's blend: stepping on would pass the end by and follow the curve on round)
                if (eEnd <= tol || toEnd <= hmax / 64 && eEnd <= 1e-5 * scale) { P.Add(b.S); D.Add(b.T); break; }
                h = toEnd / 2;
                if (h < hmin) { Debug?.Invoke($"    march stuck at {cur} short of the end {b.S}: error {eEnd:g3}"); return null; }
            }
            var nx = Node(cur + ct * h, ct);
            if (nx == null || Vec3.Dot(nx.Value.p - cur, ct) <= 0)
            {
                h /= 2;
                if (h < hmin)
                {
                    var pr = NSurface.ProjectToIntersection(A2, B2, cur + ct * h * 2);
                    Debug?.Invoke($"    march stuck at {cur} after {travelled:g3} of {length:g3}: {(nx == null ? $"no node (off {A2.F_(pr):g3}/{B2.F_(pr):g3}, tangent {Vec3.Cross(A2.Grad(pr), B2.Grad(pr)).Length:g3})" : "backwards")}");
                    return null;
                }
                continue;
            }
            var e = Err(cur, ct, nx.Value.p, nx.Value.t);
            // (a step already short, off by more than the tolerance only by the surfaces' noise, not by the curve bending:
            // an offset of a fitted surface, a loft's, is only as exact as its fit, and halving the step does not help)
            if (e > tol && !(h <= hmax / 64 && e <= noise)) { h /= 2; if (h < hmin) { Debug?.Invoke($"    march stuck at {cur} after {travelled:g3} of {length:g3}: error {e:g3}"); return null; } continue; }
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

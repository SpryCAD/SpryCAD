using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

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
    public sealed record Result(Solid? Tool, bool Convex, bool Fits, string? Error);

    /// <summary>A piece of an edge, traversed from Ta to Tb.</summary>
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
    /// </summary>
    public static Result Build(IReadOnlyList<NEdge> chain, double r, string name, Func<NFace, Vec3, bool> onFaces)
    {
        var pieces = Order(chain, out var closed);
        if (pieces == null) return Fail("the edges do not form a single chain.");
        if (closed && pieces.Count == 1)
        {
            // one closed edge: two pieces (faces must not wrap around)
            var p = pieces[0];
            pieces = new List<Piece> { new(p.E, p.Ta, p.Mid), new(p.E, p.Mid, p.Tb) };
        }
        var e0 = pieces[0].E;
        if (e0.Uses.Count != 2) return Fail("the edge is not between two faces.");
        var fA = e0.Uses[0].Face; var fB = e0.Uses[1].Face;
        NSurface SA = fA.Surface, SB = fB.Surface;
        NFace? FaceOn(NEdge e, NSurface s) => e.Uses.Select(u => u.Face).FirstOrDefault(f => OnSurface(f, s));
        foreach (var p in pieces)
        {
            var a = FaceOn(p.E, SA); var b = FaceOn(p.E, SB);
            if (a == null || b == null || ReferenceEquals(a, b) || a.Sense != fA.Sense || b.Sense != fB.Sense)
                return Fail("the edges are not all between the same two faces.");
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
            if (m == null) return Fail("could not follow the rolling ball along the edge (the faces turn too sharply for this radius).");
            var (P, D, i0, i1) = m.Value;
            var sp = new PipeSpine(P, D, SA, SB);
            if (sp.MinCurvatureRadius < r * 1.02) return Fail("the radius is too large: the fillet would fold over itself.");
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

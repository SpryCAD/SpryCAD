using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// The rolling ball's corner where two concave fillets on one face S (a casting's side, or its corner round) turn
/// round a convex corner edge L of what stands on it (a pedestal: its underside O blended into S along one edge, its
/// side N along the next). Rolling along the first edge the ball touches O and S; where it reaches L it pivots about
/// L, touching L and S, until it touches N: then it rolls on along the second edge. The pivot's blend is the pipe
/// about the ball's centre as it pivots (r from L, r off S), touching S along a curve and L along a piece of it; the
/// fillets end where the pivot starts and ends, their end sections the pipe's (the ball's circle through its two
/// contacts). Where the fillets meet in a mitre instead, one stands out past the other (the two reach the corner edge
/// at different heights when O and N meet S at different angles) and the mitre's crease is sharp.
/// <para>
/// The tool: the region between the pipe, S, O and N, from the first fillet's end section to the second's (closed by
/// those sections, the first edge from its end to the vertex v where L stands on S, L from v up to the pipe, the
/// second edge back to its end).
/// </para>
/// </summary>
internal static class CornerPatch
{

    /// <summary>The corner's tool and where each fillet ends (their tools are cut on these planes, keeping the side
    /// along their edge).</summary>
    public sealed record Result(Solid Tool, Plane EndOwn, Plane EndNext);

    static Vec3 Outward(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1);

    /// <summary>
    /// The corner at v: the fillet of radius r along edge eOwn (between S and O) turning round the corner edge
    /// (from v along <paramref name="along"/>, <paramref name="cornerLength"/> long) onto the next one along eNext
    /// (between S and N). Null when the ball does not pivot there (the fillets are not concave, the faces not planes,
    /// the ball reaching past the corner edge's end).
    /// </summary>
    public static Result? Build(NVertex v, Vec3 along, double cornerLength, NEdge eOwn, NEdge eNext, NFace s, NFace o, NFace nf, double r, string name)
    {
        if (o.Surface.Kind != SurfaceKind.Plane || nf.Surface.Kind != SurfaceKind.Plane) return Fail("the corner's faces are not planes");
        var L = along.Normalized;
        Vec3 nO = Outward(o, v.P), nN = Outward(nf, v.P);
        if (Math.Abs(Vec3.Dot(nO, L)) > 1e-6 || Math.Abs(Vec3.Dot(nN, L)) > 1e-6) return Fail("the corner edge is not between the corner's faces");
        var cosPhi = Math.Clamp(Vec3.Dot(nO, nN), -1, 1);
        var Phi = Math.Acos(cosPhi);
        if (Phi < 0.1 || Phi > Math.PI - 0.1) return Fail("the corner is too flat or too sharp");
        var w = (nN - nO * cosPhi).Normalized;
        double sS = s.Sense ? 1 : -1;
        var surf = s.Surface;
        // (signed distance outside S; the ball's centre is r out)
        double Out(Vec3 p) => surf.F_(p) * sS;
        Vec3 OutGrad(Vec3 p) => surf.Grad(p).Normalized * sS;
        Vec3 U(double phi) => nO * Math.Cos(phi) + w * Math.Sin(phi);
        Vec3 dU(double phi) => nO * -Math.Sin(phi) + w * Math.Cos(phi);
        // the pivot: centre v + z L + r u(φ), r out from S; z the nearest root up the corner edge
        double? Z(double phi)
        {
            var u = U(phi);
            double F(double z) => Out(v.P + L * z + u * r) - r;
            const int N = 96;
            double lo = -0.5 * r, hi = 8 * r, zPrev = lo, fPrev = F(lo);
            for (int i = 1; i <= N; i++)
            {
                var z = lo + (hi - lo) * i / N; var f = F(z);
                if (Math.Sign(f) != Math.Sign(fPrev) || f == 0)
                    return Roots.Bisect(F, zPrev, z);
                zPrev = z; fPrev = f;
            }
            return null;
        }
        const int n = 16;
        var P = new Vec3[n + 1]; var D = new Vec3[n + 1]; var zs = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var phi = Phi * i / n;
            if (Z(phi) is not { } z) return Fail($"no pivot centre at {phi:0.###} rad");
            if (z < 1e-9 * Math.Max(1, r) || z > cornerLength) return Fail($"the ball pivots off the corner edge (at {z:g4} of {cornerLength:g4})");
            var c = v.P + L * z + U(phi) * r;
            // d/dφ of the centre: z' from the distance staying r
            var g = OutGrad(c); var gl = Vec3.Dot(g, L);
            if (Math.Abs(gl) < 1e-9) return Fail("the corner edge runs along the face");
            var dz = -r * Vec3.Dot(g, dU(phi)) / gl;
            var d = L * dz + dU(phi) * r;
            if (d.Length < 1e-12) return Fail("the pivot does not move");
            P[i] = c; D[i] = d.Normalized; zs[i] = z;
        }
        PipeSpine sp;
        try { sp = new PipeSpine(P, D, null, surf); }
        catch (ArgumentException ex) { return Fail(ex.Message); }

        // the fillets' ends: the planes of the ball's circles there, kept towards their edges
        var endOwn = Plane.FromPointNormal(P[0], -D[0]);
        var endNext = Plane.FromPointNormal(P[n], D[n]);
        Vec3 k0 = v.P + L * zs[0], kn = v.P + L * zs[n];
        Vec3 s0 = surf.Project(P[0]), sn = surf.Project(P[n]);
        // where each end meets its edge: on the edge's plane face, the end plane and S
        Vec3? EdgeEnd(Plane end, Vec3 nFace, Vec3 k)
        {
            var dl = Vec3.Cross(nFace, end.Normal).Normalized;
            double F(double t) => Out(k + dl * t);
            // (towards S from the corner edge: the side the distance falls on)
            if (F(r) > F(-r)) dl = -dl;
            // (the first crossing out from the corner edge)
            const int N = 64;
            double tPrev = 0, fPrev = F(0);
            for (int i = 1; i <= N; i++)
            {
                var t = 8 * r * i / N; var f = F(t);
                if (Math.Sign(f) != Math.Sign(fPrev) || f == 0) return k + dl * Roots.Bisect(F, tPrev, t);
                tPrev = t; fPrev = f;
            }
            return null;
        }
        if (EdgeEnd(endOwn, nO, k0) is not { } q0 || EdgeEnd(endNext, nN, kn) is not { } qn) return Fail("an end does not reach its edge");
        // an end past v (on a drafted face a fillet runs on a hair past the corner before its ball reaches the corner
        // edge, its edge ending first: its sections there reach under what stands on S): not built (the fillets mitred)
        var tolV = 1e-7 * Math.Max(1, r);
        if (endOwn.Distance(v.P) > tolV || endNext.Distance(v.P) > tolV) return Fail("a fillet reaches the corner edge past the corner");

        // ---- topology
        // an end whose plane holds the corner edge (the fillet along an edge square to it: ending in the plane of the
        // other face) meets its edge at v and goes back along the corner edge, the corner having no face on that side
        bool flat0 = Vec3.Distance(q0, v.P) < tolV, flatN = Vec3.Distance(qn, v.P) < tolV;
        // the corner edge from v: up to the lower of the ball's contacts, on to the higher (one, the fillets reaching it
        // at one height: a box's foot on a plane), the pipe beside it between them
        bool level = Math.Abs(zs[0] - zs[n]) < tolV, ownHigher = !level && zs[0] > zs[n];
        var V = new NVertex(v.P);
        var K0 = new NVertex(k0); var KN = level ? K0 : new NVertex(kn);
        var S0 = new NVertex(s0); var SN = new NVertex(sn); var Q0 = flat0 ? V : new NVertex(q0); var QN = flatN ? V : new NVertex(qn);
        NEdge Line(NVertex a, NVertex b) => new(new NLine(a.P, b.P - a.P), 0, Vec3.Distance(a.P, b.P), a, b);
        NEdge Arc(Vec3 c, Vec3 axis, NVertex from, NVertex to)
        {
            var circle = new NCircle(c, axis, from.P - c, r);
            var t = circle.Param(to.P);
            if (t < 0) { circle = new NCircle(c, -axis, from.P - c, r); t = circle.Param(to.P); }
            return new NEdge(circle, 0, t, from, to);
        }
        // a piece of a blended edge's curve, from v to the fillet's end
        NEdge? Piece(NEdge e, NVertex from, NVertex to)
        {
            var c = e.Curve;
            double ta = c.Param(from.P, ReferenceEquals(e.V0, v) ? e.T0 : e.T1), tb = c.Param(to.P, ta);
            if (Vec3.Distance(c.Eval(ta), from.P) > 1e-6 * Math.Max(1, r) || Vec3.Distance(c.Eval(tb), to.P) > 1e-6 * Math.Max(1, r)) return null;
            return ta < tb ? new NEdge(c, ta, tb, from, to) : new NEdge(c, tb, ta, to, from);
        }
        NEdge? pOwn = flat0 ? null : Piece(eOwn, V, Q0), pNext = flatN ? null : Piece(eNext, V, QN);
        if (pOwn == null && !flat0 || pNext == null && !flatN) return Fail("the fillets' ends are not on their edges");
        var arc0 = Arc(P[0], D[0], S0, K0); var arcN = Arc(P[n], D[n], SN, KN);
        var contact = new NEdge(new NContact(sp, true), sp.S0, sp.S1, S0, SN);
        var planeOwn = NSurface.FromPlane(endOwn); var planeNext = NSurface.FromPlane(endNext);
        var trace0 = new NEdge(new NSection(surf, planeOwn, q0, s0), 0, 1, Q0, S0);
        var traceN = new NEdge(new NSection(surf, planeNext, qn, sn), 0, 1, QN, SN);
        var (KLo, KHi) = ownHigher ? (KN, K0) : (K0, KN);
        var lLo = Line(V, KLo); var lHi = level ? null : Line(KLo, KHi);
        // down the corner edge from a contact to v; along it from N's contact to O's (the pipe's side)
        List<(NEdge, bool)> Down(NVertex k) => !level && ReferenceEquals(k, KHi) ? new() { (lHi!, false), (lLo, false) } : new() { (lLo, false) };
        List<(NEdge, bool)> Across() => level ? new() : new() { (lHi!, ownHigher) };
        // a side's way back from its end to v: along the corner edge (flat), else its line to the corner edge and down
        List<(NEdge, bool)> Back(bool flat, NVertex q, NVertex k, NEdge? line) => flat ? Down(k) : new List<(NEdge, bool)> { (line!, false) };
        var line0 = flat0 ? null : Line(Q0, K0); var lineN = flatN ? null : Line(QN, KN);

        var body = new NBody();
        // faces: the planes' loops oriented by the tool's outward normal (their loops' normal), the curved faces' to
        // match a neighbour's along an edge they share (each edge used once each way)
        var faces = new List<(NSurface surface, bool sense, string tag, List<(NEdge e, bool fwd)> loop, Vec3? outward)>();
        void Face(NSurface surface, bool sense, string tag, List<(NEdge e, bool fwd)> loop, Vec3? outward) => faces.Add((surface, sense, tag, loop, outward));
        // the ends: outward along the fillets
        Face(planeOwn, true, "End0", new List<(NEdge, bool)> { (trace0, true), (arc0, true) }.Concat(Back(flat0, Q0, K0, line0)).ToList(), endOwn.Normal);
        Face(planeNext, true, "End1", new List<(NEdge, bool)> { (traceN, true), (arcN, true) }.Concat(Back(flatN, QN, KN, lineN)).ToList(), endNext.Normal);
        // the blend: outward towards the ball's centre
        var pipe = NSurface.Pipe(sp, r);
        Face(pipe, false, "Blend", new List<(NEdge, bool)> { (contact, true), (arcN, true) }.Concat(Across()).Append((arc0, false)).ToList(), null);
        // on S, O and N: outward into the material
        var sLoop = new List<(NEdge, bool)>();
        if (pOwn != null) sLoop.Add((pOwn, ReferenceEquals(pOwn.V0, V)));
        sLoop.Add((trace0, true)); sLoop.Add((contact, true)); sLoop.Add((traceN, false));
        if (pNext != null) sLoop.Add((pNext, !ReferenceEquals(pNext.V0, V)));
        Face(surf, !s.Sense, "S", sLoop, null);
        // O and N: the edge up to the fillet's end, its line to the corner edge, down that to v
        if (!flat0) Face(o.Surface, !o.Sense, "O", new List<(NEdge, bool)> { (pOwn!, ReferenceEquals(pOwn!.V0, V)), (line0!, true) }.Concat(Down(K0)).ToList(), -nO);
        if (!flatN) Face(nf.Surface, !nf.Sense, "N", new List<(NEdge, bool)> { (pNext!, ReferenceEquals(pNext!.V0, V)), (lineN!, true) }.Concat(Down(KN)).ToList(), -nN);
        static List<(NEdge e, bool fwd)> Reversed(List<(NEdge e, bool fwd)> l) => l.Select(x => (x.e, !x.fwd)).Reverse().ToList();
        // (the plane whose loop's normal is surest: the largest; the others from it)
        var done = new bool[faces.Count];
        int seed = -1; double best = 0; var seedFlip = false;
        for (int k = 0; k < faces.Count; k++)
        {
            if (faces[k].outward is not { } outward) continue;
            var pts = new List<Vec3>();
            foreach (var (e, fwd) in faces[k].loop)
                for (int i = 0; i < 8; i++) pts.Add(e.Curve.Eval(fwd ? e.T0 + (e.T1 - e.T0) * i / 8 : e.T1 - (e.T1 - e.T0) * i / 8));
            var nl = Vec3.Zero;
            for (int i = 0; i < pts.Count; i++) nl += Vec3.Cross(pts[i], pts[(i + 1) % pts.Count]);
            var a = Math.Abs(Vec3.Dot(nl, outward.Normalized));
            if (a > best) { best = a; seed = k; seedFlip = Vec3.Dot(nl, outward) < 0; }
        }
        if (seed < 0) return Fail("internal: no face to orient by");
        if (seedFlip) faces[seed] = faces[seed] with { loop = Reversed(faces[seed].loop) };
        done[seed] = true;
        for (bool more = true; more;)
        {
            more = false;
            for (int k = 0; k < faces.Count; k++)
            {
                if (done[k]) continue;
                foreach (var (e, fwd) in faces[k].loop)
                {
                    int j = Enumerable.Range(0, faces.Count).FirstOrDefault(j => j != k && done[j] && faces[j].loop.Any(x => ReferenceEquals(x.e, e)), -1);
                    if (j < 0) continue;
                    // (used the other way round there)
                    if (faces[j].loop.First(x => ReferenceEquals(x.e, e)).fwd == fwd) faces[k] = faces[k] with { loop = Reversed(faces[k].loop) };
                    done[k] = more = true;
                    break;
                }
            }
        }
        if (done.Any(d => !d)) return Fail("internal: a face not joined to the others");
        foreach (var (surface, sense, tag, loop, _) in faces)
        {
            var f = new NFace(surface, sense, new FaceTag(name + "." + tag, surface.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : surface.Def));
            var l = new NLoop();
            foreach (var (e, fwd) in loop) l.Coedges.Add(new NCoedge(e, fwd));
            f.Loops.Add(l);
            body.Faces.Add(f);
        }
        body.Link();
        if (body.Validate() is { } err) return Fail("internal: " + err);
        var tool = NTess.ToSolid(body);
        NativeBlend.Debug?.Invoke($"corner at {v.P}: pivot {Phi * 180 / Math.PI:0.#}°, the corner edge from {Math.Min(zs[0], zs[n]):g4} to {Math.Max(zs[0], zs[n]):g4}, volume {tool.Volume():g4}");
        return new Result(tool, endOwn, endNext);

        static Result? Fail(string why) { NativeBlend.Debug?.Invoke("corner patch: " + why); return null; }
    }
}

using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>
/// Fillets fading out where they stop: a fillet whose edge runs on tangentially into one not picked (a boss's foot
/// fillet, wrapped round its side fillet, reaching the next face) ends square, its section standing. There the
/// fillet is carried on along that edge, its radius falling to nothing: between two planes, along a straight edge,
/// the section shrinks onto the edge, and the tool is the cone over the end face with its apex on the edge (its
/// sides on the two planes, its round a ruled rational B-spline, tangent to both).
/// </summary>
public static class FadeOut
{
    /// <summary>How far a fillet fades out, in radii (at most most of the edge it fades along).</summary>
    public const double Radii = 4;

    /// <summary>On (FC_FADE=1): not yet by default.</summary>
    public static bool Enabled = Environment.GetEnvironmentVariable("FC_FADE") == "1";

    static readonly ReferenceEqualityComparer Ref = ReferenceEqualityComparer.Instance;

    /// <summary>
    /// The body with each of the blend's square ends faded out where it can be (unchanged where none can); the tools
    /// added to <paramref name="applied"/>.
    /// </summary>
    public static Solid Apply(Solid body, string prefix, double size, List<(Solid tool, bool cut)>? applied)
    {
        if (body.Native is not { } nb) return body;
        nb.Link();
        var tools = new List<(Solid tool, bool convex, string name)>();
        foreach (var cap in nb.Faces.Where(f => IsCap(f, prefix)).ToList())
            if (Cone(nb, cap, size) is { } t) tools.Add(t);
        if (tools.Count == 0) return body;
        var result = body;
        foreach (var (tool, convex, name) in tools)
        {
            var left = new List<int>();
            Solid next;
            if (result.Native is { } rn && DirectBlend.Apply(rn, new[] { new DirectBlend.Tool(tool, convex, name, size) }, left) is { } direct && left.Count == 0)
                next = NTess.ToSolid(direct);
            else
            {
                try { next = convex ? Csg.Subtract(result, tool) : Csg.Union(result, tool); }
                catch (Exception ex) when (ex is InvalidOperationException or NBooleanException)
                {
                    NativeBlend.Debug?.Invoke($"fade {name}: not applied ({ex.Message})");
                    continue;
                }
            }
            if (next.Native == null || next.Native.Validate() != null) { NativeBlend.Debug?.Invoke($"fade {name}: result invalid"); continue; }
            NativeBlend.Debug?.Invoke($"fade {name}: applied");
            result = next;
            applied?.Add((tool, convex));
        }
        return result;
    }

    static bool IsCap(NFace f, string prefix)
    {
        var n = f.Tag.Name;
        return f.Surface.Kind == SurfaceKind.Plane && n.StartsWith(prefix + ".", StringComparison.Ordinal)
            && (n.EndsWith(".End", StringComparison.Ordinal) || n.EndsWith(".Start", StringComparison.Ordinal)
                || n.EndsWith(".End0", StringComparison.Ordinal) || n.EndsWith(".End1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The cone fading the fillet out past a square end: where the end is a fillet's section (a line on each of two
    /// planes, an arc between), the edge between those planes runs on from its corner square to it, straight.
    /// </summary>
    static (Solid tool, bool convex, string name)? Cone(NBody body, NFace cap, double size)
    {
        if (cap.Loops.Count != 1 || cap.Loops[0].Coedges.Count != 3) return No($"{cap.Loops[0].Coedges.Count} edges round the end");
        var capN = cap.Surface.Plane!.Normal;
        var scale = Math.Max(1, body.Bounds.Diagonal);
        var tol = 1e-7 * scale;
        NFace? Other(NEdge e) => e.Uses.Count == 2 ? e.Uses.Select(u => u.Face).FirstOrDefault(g => !ReferenceEquals(g, cap)) : null;
        var cos = cap.Loops[0].Coedges;
        var arcC = cos.FirstOrDefault(c => c.Edge.Curve is NCircle);
        var legs = cos.Where(c => Straight(c.Edge)).ToList();
        if (arcC == null || legs.Count != 2) return No("edges " + string.Join(", ", cos.Select(c => $"{c.Edge.Curve.GetType().Name} off {Dev(c.Edge):G3}")));
        var fa = Other(legs[0].Edge); var fb = Other(legs[1].Edge);
        if (fa == null || fb == null || fa.Surface.Kind != SurfaceKind.Plane || fb.Surface.Kind != SurfaceKind.Plane) return No($"beside it {fa?.Surface.Kind} and {fb?.Surface.Kind}");
        // the corner: the two legs' shared vertex
        var corner = new[] { legs[0].Edge.V0, legs[0].Edge.V1 }.FirstOrDefault(v => ReferenceEquals(v, legs[1].Edge.V0) || ReferenceEquals(v, legs[1].Edge.V1));
        if (corner == null) return No("no corner");
        // the edge between the two planes running on from it, square to the end
        var g = fa.Coedges.Select(c => c.Edge).FirstOrDefault(e => e.Uses.Any(u => ReferenceEquals(u.Face, fb)) && (ReferenceEquals(e.V0, corner) || ReferenceEquals(e.V1, corner)));
        if (g == null || !Straight(g)) return No($"the edge on from the corner: {g?.Curve.GetType().Name ?? "none"}");
        var far = ReferenceEquals(g.V0, corner) ? g.V1 : g.V0;
        var len = Vec3.Distance(far.P, corner.P);
        if (len < 1e-6 * scale) return null;
        var d = (far.P - corner.P) / len;
        if (Math.Abs(Vec3.Dot(d, capN)) < 1 - 1e-6) return No($"the edge leaves the end at {Math.Acos(Math.Min(1, Math.Abs(Vec3.Dot(d, capN)))) * 180 / Math.PI:0.#}°");
        var circle = (NCircle)arcC.Edge.Curve;
        var r = circle.R;
        var L = Math.Min(Radii * Math.Max(r, size), 0.8 * len);
        if (L < 1e-6 * scale) return null;
        var convex = NativeBlend.IsConvex(g) == true;
        // the arc's ends: on face a's leg and on b's
        var a = legs[0].Edge.V0 == corner ? legs[0].Edge.V1 : legs[0].Edge.V0;
        var b = legs[1].Edge.V0 == corner ? legs[1].Edge.V1 : legs[1].Edge.V0;
        var arc = arcC.Edge;
        bool arcFromA = Vec3.Distance(arc.V0.P, a.P) < Vec3.Distance(arc.V1.P, a.P);
        var apexP = corner.P + d * L;

        // the tool's own vertices and edges
        var vC = new NVertex(corner.P); var vA = new NVertex(a.P); var vB = new NVertex(b.P); var vP = new NVertex(apexP);
        var eCA = new NEdge(new NLine(corner.P, a.P - corner.P), 0, Vec3.Distance(a.P, corner.P), vC, vA);
        var eCB = new NEdge(new NLine(corner.P, b.P - corner.P), 0, Vec3.Distance(b.P, corner.P), vC, vB);
        var eAP = new NEdge(new NLine(a.P, apexP - a.P), 0, Vec3.Distance(apexP, a.P), vA, vP);
        var eBP = new NEdge(new NLine(b.P, apexP - b.P), 0, Vec3.Distance(apexP, b.P), vB, vP);
        var eCP = new NEdge(new NLine(corner.P, d), 0, L, vC, vP);
        // (the arc from a to b, as the end's)
        var eAB = arcFromA ? new NEdge(circle, arc.T0, arc.T1, vA, vB) : new NEdge(circle, arc.T0, arc.T1, vB, vA);
        bool abFwd = arcFromA;

        // the cone's round: u along the arc from a to b, v to the apex
        var t0 = abFwd ? arc.T0 : arc.T1; var t1 = abFwd ? arc.T1 : arc.T0;
        var (pts, ws, knots) = ArcNurbs(circle, t0, t1);
        var poles = new Vec3[pts.Length, 2];
        var wts = new double[pts.Length, 2];
        for (int i = 0; i < pts.Length; i++) { poles[i, 0] = pts[i]; poles[i, 1] = apexP; wts[i, 0] = ws[i]; wts[i, 1] = ws[i]; }
        var round = NSurface.BSpline(new NurbsSurface(2, 1, poles, wts, knots, new double[] { 0, 0, 1, 1 }));

        // orientation: the base faces away from the apex; its loop C -> u -> w -> C, u and w the arc's ends in the
        // order that makes it so
        var baseOut = -d;
        bool cab = Vec3.Dot(Vec3.Cross(a.P - corner.P, b.P - a.P), baseOut) > 0;
        var name = cap.Tag.Name;
        name = name[..name.LastIndexOf('.')] + ".Fade";
        var tb = new NBody();
        void Face(NSurface s, Vec3 outward, Vec3 at, string tag, params (NEdge e, bool fwd)[] loop)
        {
            var f = new NFace(s, Vec3.Dot(s.Normal(at), outward) > 0, new FaceTag(name + "." + tag, s.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : s.Def));
            var l = new NLoop();
            foreach (var (e, fwd) in loop) l.Coedges.Add(new NCoedge(e, fwd));
            f.Loops.Add(l);
            tb.Faces.Add(f);
        }
        var centroidBase = (corner.P + a.P + b.P) / 3;
        if (cab)
        {
            Face(NSurface.FromPlane(cap.Surface.Plane!), baseOut, centroidBase, "Base", (eCA, true), (eAB, abFwd), (eCB, false));
            Face(NSurface.FromPlane(fa.Surface.Plane!), Out(fa, corner.P), corner.P, "A", (eCA, false), (eCP, true), (eAP, false));
            Face(NSurface.FromPlane(fb.Surface.Plane!), Out(fb, corner.P), corner.P, "B", (eCB, true), (eBP, true), (eCP, false));
            Face(round, RoundOut(), RoundAt(), "Blend", (eAB, !abFwd), (eAP, true), (eBP, false));
        }
        else
        {
            Face(NSurface.FromPlane(cap.Surface.Plane!), baseOut, centroidBase, "Base", (eCB, true), (eAB, !abFwd), (eCA, false));
            Face(NSurface.FromPlane(fb.Surface.Plane!), Out(fb, corner.P), corner.P, "B", (eCB, false), (eCP, true), (eBP, false));
            Face(NSurface.FromPlane(fa.Surface.Plane!), Out(fa, corner.P), corner.P, "A", (eCA, true), (eAP, true), (eCP, false));
            Face(round, RoundOut(), RoundAt(), "Blend", (eAB, abFwd), (eBP, true), (eAP, false));
        }
        tb.Link();
        if (tb.Validate() is { } err) { NativeBlend.Debug?.Invoke($"fade {name}: tool invalid ({err})"); return null; }
        var tool = NTess.ToSolid(tb);
        return (tool, convex, name);

        // (a line, or a section of two planes, stored as a general curve)
        bool Straight(NEdge e) => e.Curve is NLine || Dev(e) < tol;
        // (how far the curve strays from the line between its ends)
        double Dev(NEdge e)
        {
            var p0 = e.V0.P; var p1 = e.V1.P;
            var len = Vec3.Distance(p0, p1);
            if (len < 1e-9) return double.MaxValue;
            double dev = 0;
            foreach (var f in new[] { 0.25, 0.5, 0.75 })
            {
                var q = e.Curve.Eval(e.T0 + (e.T1 - e.T0) * f);
                var t = Vec3.Dot(q - p0, (p1 - p0) / len);
                dev = Math.Max(dev, Vec3.Distance(q, p0 + (p1 - p0) / len * t));
            }
            return dev;
        }
        (Solid, bool, string)? No(string why) { NativeBlend.Debug?.Invoke($"fade {cap.Tag.Name}: none ({why})"); return null; }
        Vec3 RoundAt() { var m = circle.Eval(0.5 * (arc.T0 + arc.T1)); return (m + apexP) * 0.5; }
        // (away from the corner's line at that section: the tool lies between the corner and the round)
        Vec3 RoundOut() { var m = circle.Eval(0.5 * (arc.T0 + arc.T1)); return m - corner.P; }
        // a side face of the tool, on face f's plane: facing out of the material where the tool lies in it (convex),
        // into it where the tool lies outside (concave)
        Vec3 Out(NFace f, Vec3 p) => f.Surface.Normal(p) * (f.Sense ? 1 : -1) * (convex ? 1 : -1);
    }

    /// <summary>A circle's arc from t0 to t1 as a rational quadratic B-spline (pieces of 90° at most).</summary>
    static (Vec3[] poles, double[] w, double[] knots) ArcNurbs(NCircle c, double t0, double t1)
    {
        var span = t1 - t0;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(span) / (Math.PI / 2) - 1e-9));
        var poles = new Vec3[2 * n + 1]; var w = new double[2 * n + 1];
        var h = span / n / 2;
        for (int k = 0; k < n; k++)
        {
            var a = t0 + 2 * h * k;
            poles[2 * k] = c.Eval(a); w[2 * k] = 1;
            var m = a + h;
            poles[2 * k + 1] = c.C + (c.X * Math.Cos(m) + c.Y * Math.Sin(m)) * (c.R / Math.Cos(h));
            w[2 * k + 1] = Math.Cos(h);
        }
        poles[2 * n] = c.Eval(t1); w[2 * n] = 1;
        var knots = new List<double> { 0, 0, 0 };
        for (int k = 1; k < n; k++) { knots.Add((double)k / n); knots.Add((double)k / n); }
        knots.AddRange(new[] { 1.0, 1, 1 });
        return (poles, w, knots.ToArray());
    }
}

namespace SpryCad.Kernel.Native;

/// <summary>Builds exact native bodies from 2D profiles.</summary>
public static class NBuild
{
    /// <summary>
    /// One exact profile segment (line, circular arc, or a piece of a spline from its T0 to its T1) from A to B,
    /// interior on the left.
    /// </summary>
    public sealed record Seg(Vec2 A, Vec2 B, string Name, ArcInfo? Arc, bool Ccw, SplineInfo? Spline = null);

    /// <summary>
    /// Converts a (faceted) profile loop back into exact lines, arcs and splines. Full circles become two arcs, a
    /// spline closing on itself two halves (seam rule).
    /// </summary>
    public static List<Seg> Segments(ProfileLoop loop)
    {
        var v = loop.Verts;
        int n = v.Count;
        var segs = new List<Seg>();
        if (n == 0) return segs;
        // pieces of one spline in a row (each starting where the one before ended)
        static bool Follows(ProfileVertex a, ProfileVertex b) => a.Spline != null && b.Spline != null && a.Name == b.Name
            && ReferenceEquals(a.Spline.Curve, b.Spline.Curve) && Math.Abs(a.Spline.T1 - b.Spline.T0) < 1e-9;
        // a spline all the way round: two halves
        if (v.All(x => x.Spline != null && ReferenceEquals(x.Spline.Curve, v[0].Spline!.Curve) && x.Name == v[0].Name))
        {
            double ta = v[0].Spline!.T0, tb = v[^1].Spline!.T1, tm = 0.5 * (ta + tb);
            var c = v[0].Spline!.Curve; var pm = c.Eval(tm); var mid = new Vec2(pm.X, pm.Y);
            segs.Add(new Seg(v[0].P, mid, v[0].Name, null, true, new SplineInfo(c, ta, tm)));
            segs.Add(new Seg(mid, v[0].P, v[0].Name, null, true, new SplineInfo(c, tm, tb)));
            return segs;
        }
        // full circle: one arc entity around the whole loop
        if (v.All(x => x.Arc != null && x.Name == v[0].Name && SameArc(x.Arc, v[0].Arc)))
        {
            var arc = v[0].Arc!;
            var a = v[0].P; var opposite = arc.Center * 2 - a;
            var ccw = loop.SignedArea() > 0;
            segs.Add(new Seg(a, opposite, v[0].Name, arc, ccw));
            segs.Add(new Seg(opposite, a, v[0].Name, arc, ccw));
            return segs;
        }
        // start at a run boundary
        int start = 0;
        for (int i = 0; i < n; i++)
        {
            var prev = v[(i - 1 + n) % n];
            if (!(prev.Name == v[i].Name && SameArc(prev.Arc, v[i].Arc) && v[i].Arc != null) && !Follows(prev, v[i])) { start = i; break; }
        }
        int k = 0;
        while (k < n)
        {
            var i = (start + k) % n;
            var cur = v[i];
            if (cur.Spline != null)
            {
                int run = 1;
                while (k + run < n && Follows(v[(start + k + run - 1) % n], v[(start + k + run) % n])) run++;
                var last = v[(start + k + run - 1) % n];
                segs.Add(new Seg(cur.P, v[(start + k + run) % n].P, cur.Name, null, true, new SplineInfo(cur.Spline.Curve, cur.Spline.T0, last.Spline!.T1)));
                k += run;
                continue;
            }
            if (cur.Arc == null)
            {
                segs.Add(new Seg(cur.P, v[(i + 1) % n].P, cur.Name, null, true));
                k++;
                continue;
            }
            int len = 1;
            while (k + len < n)
            {
                var nx = v[(start + k + len) % n];
                if (nx.Name != cur.Name || !SameArc(nx.Arc, cur.Arc)) break;
                len++;
            }
            var end = v[(start + k + len) % n].P;
            bool ccw;
            if (len >= 2) ccw = Vec2.Cross(v[(i + 1) % n].P - cur.Arc.Center, cur.P - cur.Arc.Center) < 0;
            else
            {
                // short arc: the shorter sweep
                var a0 = (cur.P - cur.Arc.Center).Angle; var a1 = (end - cur.Arc.Center).Angle;
                var d = a1 - a0; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI;
                ccw = d > 0;
            }
            segs.Add(new Seg(cur.P, end, cur.Name, cur.Arc, ccw));
            k += len;
        }
        return segs;
    }

    static bool SameArc(ArcInfo? a, ArcInfo? b) =>
        a == null ? b == null : b != null && Vec2.Distance(a.Center, b.Center) < 1e-9 && Math.Abs(a.Radius - b.Radius) < 1e-9;

    /// <summary>Builds a circle edge between two vertices on the circle (ccw about axis if ccw).</summary>
    internal static (NEdge edge, bool forward) ArcEdge(NCircle circle, NVertex a, NVertex b, bool ccw)
    {
        // canonical edge runs counter-clockwise about the circle axis
        var (from, to) = ccw ? (a, b) : (b, a);
        var t0 = circle.Param(from.P);
        var t1 = circle.Param(to.P, t0 + Math.PI);
        while (t1 <= t0 + 1e-12) t1 += 2 * Math.PI;
        return (new NEdge(circle, t0, t1, from, to), ccw);
    }

    /// <summary>A sketch spline in space: its poles mapped (an affine map from the sketch's x–y: the same curve).</summary>
    static BSplineCurve Mapped(BSplineCurve c, Func<double, double, Vec3> map) => c with { Poles = c.Poles.Select(q => map(q.X, q.Y)).ToArray() };

    /// <summary>
    /// An edge along a spline piece from a to b (its parameter t0 to t1): the edge's parameter rises, so a piece run
    /// backwards is the edge from b to a, used reversed.
    /// </summary>
    static (NEdge edge, bool forward) SplineEdge(BSplineCurve c, double t0, double t1, NVertex a, NVertex b)
    {
        var curve = new NSpline(c);
        return t0 <= t1 ? (new NEdge(curve, t0, t1, a, b), true) : (new NEdge(curve, t1, t0, b, a), false);
    }

    /// <summary>The surface ruled between two curves with the same knots (a spline's side between two caps): degree (p, 1).</summary>
    static NSurface Ruled(BSplineCurve bottom, BSplineCurve top)
    {
        int m = bottom.Poles.Length;
        var poles = new Vec3[m, 2];
        for (int i = 0; i < m; i++) { poles[i, 0] = bottom.Poles[i]; poles[i, 1] = top.Poles[i]; }
        return NSurface.BSpline(new NurbsSurface(bottom.Degree, 1, poles, null, bottom.Knots, new double[] { 0, 0, 1, 1 }));
    }

    /// <summary>
    /// The surface a spline sweeps turning about the axis (O, A) through the angle: rational, the curve's degree along
    /// it, quadratic around (spans of at most a quarter turn: the middle poles out by 1 / cos of half the span, weighted
    /// by it). One surface for the whole angle: every face of it (half turns at most) lies on it.
    /// </summary>
    static NSurface Revolved(BSplineCurve c, Vec3 O, Vec3 A, double angle)
    {
        int arcs = Math.Max(1, (int)Math.Ceiling(angle / (Math.PI / 2) - 1e-9));
        double span = angle / arcs, w = Math.Cos(span / 2);
        int m = c.Poles.Length, nv = 2 * arcs + 1;
        var poles = new Vec3[m, nv]; var weights = new double[m, nv];
        for (int i = 0; i < m; i++)
        {
            var q = c.Poles[i];
            var h = Vec3.Dot(q - O, A);
            var rho = (q - O) - A * h; var r = rho.Length;
            var X = r > 1e-12 ? rho / r : A.AnyPerpendicular(); var Y = Vec3.Cross(A, X);
            for (int k = 0; k < nv; k++)
            {
                var phi = span * k / 2;
                var rr = k % 2 == 1 ? r / w : r;
                poles[i, k] = O + A * h + (X * Math.Cos(phi) + Y * Math.Sin(phi)) * rr;
                weights[i, k] = k % 2 == 1 ? w : 1;
            }
        }
        var kv = new List<double> { 0, 0, 0 };
        for (int a = 1; a < arcs; a++) { kv.Add((double)a / arcs); kv.Add((double)a / arcs); }
        kv.AddRange(new double[] { 1, 1, 1 });
        return NSurface.BSpline(new NurbsSurface(c.Degree, 2, poles, weights, c.Knots, kv.ToArray()));
    }

    /// <summary>A spline piece's direction of travel at its middle, in the sketch.</summary>
    static Vec2 SplineTangent(Seg s)
    {
        s.Spline!.Curve.Derivs(0.5 * (s.Spline.T0 + s.Spline.T1), out _, out var d1, out _);
        var t = new Vec2(d1.X, d1.Y).Normalized;
        return s.Spline.T1 >= s.Spline.T0 ? t : -t;
    }

    /// <summary>A spline piece's middle point in the sketch.</summary>
    static Vec2 MidOfSpline(Seg s) { var p = s.Spline!.Curve.Eval(0.5 * (s.Spline.T0 + s.Spline.T1)); return new Vec2(p.X, p.Y); }

    /// <summary>Exact extrusion of profile regions along the frame normal from z0 to z1.</summary>
    public static NBody Extrude(IEnumerable<ProfileRegion> regions, Frame frame, double z0, double z1, string prefix)
    {
        if (z1 < z0) (z0, z1) = (z1, z0);
        var body = new NBody();
        var u = frame.Normal;
        var h = z1 - z0;
        var startTag = new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef);
        var endTag = new FaceTag(prefix + ".End", SurfaceDef.PlaneDef);
        var sideTags = new Dictionary<string, FaceTag>();

        foreach (var reg0 in regions)
        {
            var reg = reg0.Normalized();
            var startFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(frame.ToWorld(0, 0, z0), -u)), true, startTag);
            var endFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(frame.ToWorld(0, 0, z1), u)), true, endTag);
            foreach (var loop in reg.AllLoops())
            {
                var segs = Segments(loop);
                if (segs.Count == 0) continue;
                int m = segs.Count;
                var vb = new NVertex[m]; var vt = new NVertex[m];
                for (int i = 0; i < m; i++)
                {
                    vb[i] = new NVertex(frame.ToWorld(segs[i].A.X, segs[i].A.Y, z0));
                    vt[i] = new NVertex(frame.ToWorld(segs[i].A.X, segs[i].A.Y, z1));
                }
                var vert = new NEdge[m];
                for (int i = 0; i < m; i++) vert[i] = new NEdge(new NLine(vb[i].P, u), 0, h, vb[i], vt[i]);
                var bottom = new (NEdge e, bool fwd)[m]; var top = new (NEdge e, bool fwd)[m];
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    var s = segs[i];
                    if (s.Spline is { } sp)
                    {
                        bottom[i] = SplineEdge(Mapped(sp.Curve, (x, y) => frame.ToWorld(x, y, z0)), sp.T0, sp.T1, vb[i], vb[j]);
                        top[i] = SplineEdge(Mapped(sp.Curve, (x, y) => frame.ToWorld(x, y, z1)), sp.T0, sp.T1, vt[i], vt[j]);
                    }
                    else if (s.Arc == null)
                    {
                        var db = vb[j].P - vb[i].P;
                        bottom[i] = (new NEdge(new NLine(vb[i].P, db), 0, db.Length, vb[i], vb[j]), true);
                        top[i] = (new NEdge(new NLine(vt[i].P, db), 0, db.Length, vt[i], vt[j]), true);
                    }
                    else
                    {
                        var cb = new NCircle(frame.ToWorld(s.Arc.Center.X, s.Arc.Center.Y, z0), u, frame.XDir, s.Arc.Radius);
                        var ct = new NCircle(frame.ToWorld(s.Arc.Center.X, s.Arc.Center.Y, z1), u, frame.XDir, s.Arc.Radius);
                        bottom[i] = ArcEdge(cb, vb[i], vb[j], s.Ccw);
                        top[i] = ArcEdge(ct, vt[i], vt[j], s.Ccw);
                    }
                }
                // side faces
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    var s = segs[i];
                    var name = prefix + ".S." + s.Name;
                    NSurface surf; bool sense;
                    if (s.Spline is { } sp)
                    {
                        // (the natural normal is the curve's tangent × the extrusion: outward where the loop runs with the curve)
                        surf = Ruled(((NSpline)bottom[i].e.Curve).S, ((NSpline)top[i].e.Curve).S);
                        sense = sp.T1 > sp.T0;
                    }
                    else if (s.Arc == null)
                    {
                        var d = (vb[j].P - vb[i].P).Normalized;
                        surf = NSurface.FromPlane(Plane.FromPointNormal(vb[i].P, Vec3.Cross(d, u)));
                        sense = true;
                    }
                    else
                    {
                        var def = new SurfaceDef
                        {
                            Kind = SurfaceKind.Cylinder, Origin = frame.ToWorld(s.Arc.Center.X, s.Arc.Center.Y, z0), Axis = u,
                            RefDir = frame.XDir, Radius = s.Arc.Radius, Segments = s.Arc.Segments,
                        };
                        surf = new NSurface(def);
                        sense = s.Ccw; // convex arcs (ccw with interior on the left) face away from the axis
                    }
                    if (!sideTags.TryGetValue(name, out var tag))
                        sideTags[name] = tag = new FaceTag(name, s.Arc == null && s.Spline == null ? SurfaceDef.PlaneDef : surf.Def);
                    Vec3? anchor = null;
                    if (s.Spline != null) { var mid = MidOfSpline(s); anchor = frame.ToWorld(mid.X, mid.Y, (z0 + z1) / 2); }
                    if (s.Arc != null)
                    {
                        var c2 = s.Arc.Center; var a0 = (s.A - c2).Angle; var a1 = (s.B - c2).Angle;
                        var d = a1 - a0;
                        if (s.Ccw) { while (d <= 0) d += 2 * Math.PI; } else { while (d >= 0) d -= 2 * Math.PI; }
                        var mid = c2 + Vec2.FromAngle(a0 + d / 2, s.Arc.Radius);
                        anchor = frame.ToWorld(mid.X, mid.Y, (z0 + z1) / 2);
                    }
                    var f = new NFace(surf, sense, tag, anchor);
                    var l = new NLoop();
                    l.Coedges.Add(new NCoedge(bottom[i].e, bottom[i].fwd));
                    l.Coedges.Add(new NCoedge(vert[j], true));
                    l.Coedges.Add(new NCoedge(top[i].e, !top[i].fwd));
                    l.Coedges.Add(new NCoedge(vert[i], false));
                    f.Loops.Add(l);
                    body.Faces.Add(f);
                }
                // caps
                var endLoop = new NLoop();
                for (int i = 0; i < m; i++) endLoop.Coedges.Add(new NCoedge(top[i].e, top[i].fwd));
                endFace.Loops.Add(endLoop);
                var startLoop = new NLoop();
                for (int i = m - 1; i >= 0; i--) startLoop.Coedges.Add(new NCoedge(bottom[i].e, !bottom[i].fwd));
                startFace.Loops.Add(startLoop);
            }
            body.Faces.Add(startFace);
            body.Faces.Add(endFace);
        }
        body.Link();
        return body;
    }
    /// <summary>
    /// Exact extrusion of profile regions along the frame normal from the plane p0 to the plane p1 (null: z0 / z1, square
    /// to the normal): a prism whose ends lean (a blend's tool ending on the face its end meets, or on the plane
    /// bisecting a corner). Lines end in lines on the planes, arcs in ellipses. The planes keep the prism on their
    /// positive side. Null where a plane is (nearly) along the normal or the ends cross inside the profile.
    /// </summary>
    public static NBody? ExtrudeBetween(IEnumerable<ProfileRegion> regions, Frame frame, Plane? p0, double z0, Plane? p1, double z1, string prefix)
    {
        var u = frame.Normal;
        foreach (var pl in new[] { p0, p1 })
            if (pl != null && Math.Abs(Vec3.Dot(pl.Normal, u)) < 0.05) return null;
        var start = p0 ?? Plane.FromPointNormal(frame.ToWorld(0, 0, z0), u);
        var end = p1 ?? Plane.FromPointNormal(frame.ToWorld(0, 0, z1), -u);
        // (the start keeps the prism towards +u, the end towards -u)
        if (Vec3.Dot(start.Normal, u) < 0 || Vec3.Dot(end.Normal, u) > 0) return null;
        double Z(Plane pl, double x, double y) => -pl.Distance(frame.ToWorld(x, y, 0)) / Vec3.Dot(pl.Normal, u);
        var body = new NBody();
        var startTag = new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef);
        var endTag = new FaceTag(prefix + ".End", SurfaceDef.PlaneDef);
        var sideTags = new Dictionary<string, FaceTag>();
        // a cap's edge along a side: a line between its ends, or the ellipse where the plane crosses the arc's cylinder
        (NEdge e, bool fwd) CapEdge(Plane pl, Seg s, NVertex a, NVertex b)
        {
            // a spline carried along the normal onto the plane: its poles so (an affine map), the same knots
            if (s.Spline is { } sp) return SplineEdge(Mapped(sp.Curve, (x, y) => frame.ToWorld(x, y, Z(pl, x, y))), sp.T0, sp.T1, a, b);
            if (s.Arc == null)
            {
                var d = b.P - a.P;
                return (new NEdge(new NLine(a.P, d), 0, d.Length, a, b), true);
            }
            var c0 = frame.ToWorld(s.Arc.Center.X, s.Arc.Center.Y, 0);
            var cc = c0 + u * (-pl.Distance(c0) / Vec3.Dot(pl.Normal, u));
            var r = s.Arc.Radius;
            NCurve curve;
            var minor = Vec3.Cross(u, pl.Normal);
            if (minor.Length < 1e-9) curve = new NCircle(cc, u, frame.XDir, r);
            else
            {
                var y = minor.Normalized; var x = Vec3.Cross(y, pl.Normal).Normalized;
                curve = new NEllipse(cc, x, y, r / Math.Abs(Vec3.Dot(pl.Normal, u)), r);
            }
            // the part of it over the profile's arc: through the arc's middle, carried along the normal onto the plane
            var c2 = s.Arc.Center; var a0 = (s.A - c2).Angle; var a1 = (s.B - c2).Angle;
            var dd = a1 - a0;
            if (s.Ccw) { while (dd <= 0) dd += 2 * Math.PI; } else { while (dd >= 0) dd -= 2 * Math.PI; }
            var m2 = c2 + Vec2.FromAngle(a0 + dd / 2, r);
            var mid = frame.ToWorld(m2.X, m2.Y, Z(pl, m2.X, m2.Y));
            static double Up(double t, double from) { while (t <= from) t += 2 * Math.PI; while (t > from + 2 * Math.PI) t -= 2 * Math.PI; return t; }
            double ta = curve.Param(a.P), tb = Up(curve.Param(b.P), ta), tm = Up(curve.Param(mid), ta);
            // a to b with the parameter increasing through the middle, else b to a
            return tm < tb ? (new NEdge(curve, ta, tb, a, b), true) : (new NEdge(curve, tb - 2 * Math.PI, ta, b, a), false);
        }
        foreach (var reg0 in regions)
        {
            var reg = reg0.Normalized();
            // (the caps on the planes as given, facing out: as a prism cut back to them by a boolean leaves them, so they
            // merge with the faces they meet when the tool is applied)
            var startFace = new NFace(NSurface.FromPlane(start), false, startTag);
            var endFace = new NFace(NSurface.FromPlane(end), false, endTag);
            foreach (var loop in reg.AllLoops())
            {
                var segs = Segments(loop);
                if (segs.Count == 0) continue;
                int m = segs.Count;
                var vb = new NVertex[m]; var vt = new NVertex[m];
                for (int i = 0; i < m; i++)
                {
                    double x = segs[i].A.X, y = segs[i].A.Y, zb = Z(start, x, y), zt = Z(end, x, y);
                    if (zt - zb < 1e-9) return null;
                    vb[i] = new NVertex(frame.ToWorld(x, y, zb));
                    vt[i] = new NVertex(frame.ToWorld(x, y, zt));
                }
                var vert = new NEdge[m];
                for (int i = 0; i < m; i++) vert[i] = new NEdge(new NLine(vb[i].P, u), 0, Vec3.Distance(vb[i].P, vt[i].P), vb[i], vt[i]);
                var bottom = new (NEdge e, bool fwd)[m]; var top = new (NEdge e, bool fwd)[m];
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    bottom[i] = CapEdge(start, segs[i], vb[i], vb[j]);
                    top[i] = CapEdge(end, segs[i], vt[i], vt[j]);
                }
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    var s = segs[i];
                    var name = prefix + ".S." + s.Name;
                    NSurface surf; bool sense;
                    if (s.Spline is { } sp)
                    {
                        surf = Ruled(((NSpline)bottom[i].e.Curve).S, ((NSpline)top[i].e.Curve).S);
                        sense = sp.T1 > sp.T0;
                    }
                    else if (s.Arc == null)
                    {
                        var d = (frame.ToWorld(segs[j].A.X, segs[j].A.Y, 0) - frame.ToWorld(s.A.X, s.A.Y, 0)).Normalized;
                        surf = NSurface.FromPlane(Plane.FromPointNormal(vb[i].P, Vec3.Cross(d, u)));
                        sense = true;
                    }
                    else
                    {
                        var def = new SurfaceDef
                        {
                            Kind = SurfaceKind.Cylinder, Origin = frame.ToWorld(s.Arc.Center.X, s.Arc.Center.Y, 0), Axis = u,
                            RefDir = frame.XDir, Radius = s.Arc.Radius, Segments = s.Arc.Segments,
                        };
                        surf = new NSurface(def);
                        sense = s.Ccw;
                    }
                    if (!sideTags.TryGetValue(name, out var tag))
                        sideTags[name] = tag = new FaceTag(name, s.Arc == null && s.Spline == null ? SurfaceDef.PlaneDef : surf.Def);
                    Vec3? anchor = null;
                    if (s.Spline != null) { var mid = MidOfSpline(s); anchor = frame.ToWorld(mid.X, mid.Y, 0.5 * (Z(start, mid.X, mid.Y) + Z(end, mid.X, mid.Y))); }
                    if (s.Arc != null)
                    {
                        var c2 = s.Arc.Center; var a0 = (s.A - c2).Angle; var a1 = (s.B - c2).Angle;
                        var d = a1 - a0;
                        if (s.Ccw) { while (d <= 0) d += 2 * Math.PI; } else { while (d >= 0) d -= 2 * Math.PI; }
                        var mid = c2 + Vec2.FromAngle(a0 + d / 2, s.Arc.Radius);
                        anchor = frame.ToWorld(mid.X, mid.Y, 0.5 * (Z(start, mid.X, mid.Y) + Z(end, mid.X, mid.Y)));
                    }
                    var f = new NFace(surf, sense, tag, anchor);
                    var l = new NLoop();
                    l.Coedges.Add(new NCoedge(bottom[i].e, bottom[i].fwd));
                    l.Coedges.Add(new NCoedge(vert[j], true));
                    l.Coedges.Add(new NCoedge(top[i].e, !top[i].fwd));
                    l.Coedges.Add(new NCoedge(vert[i], false));
                    f.Loops.Add(l);
                    body.Faces.Add(f);
                }
                var endLoop = new NLoop();
                for (int i = 0; i < m; i++) endLoop.Coedges.Add(new NCoedge(top[i].e, top[i].fwd));
                endFace.Loops.Add(endLoop);
                var startLoop = new NLoop();
                for (int i = m - 1; i >= 0; i--) startLoop.Coedges.Add(new NCoedge(bottom[i].e, !bottom[i].fwd));
                startFace.Loops.Add(startLoop);
            }
            body.Faces.Add(startFace);
            body.Faces.Add(endFace);
        }
        body.Link();
        return body.Validate() == null ? body : null;
    }

    /// <summary>
    /// Exact revolution of profile regions about an axis in the sketch plane (angle in radians, ≤ 2π).
    /// Surfaces: planes, cylinders, cones (lines) and spheres / tori (arcs). Revolutions are split into
    /// pieces of at most 180° so every face is a non-wrapping patch (seam rule).
    /// </summary>
    public static NBody Revolve(IEnumerable<ProfileRegion> regions, Frame frame, Vec2 axisPoint, Vec2 axisDir, double angle, string prefix)
    {
        var regs = regions.Select(r => r.Normalized()).ToList();
        bool full = angle >= 2 * Math.PI - 1e-9;
        if (full) angle = 2 * Math.PI;
        var O = frame.ToWorld(axisPoint);
        var A = (frame.ToWorld(axisPoint + axisDir.Normalized) - O).Normalized;
        var n = frame.Normal;
        const double onAxis = 1e-6;
        double R(Vec3 p) { var d = p - O; return (d - A * Vec3.Dot(d, A)).Length; }
        double H(Vec3 p) => Vec3.Dot(p - O, A);

        // the side of the axis the profile lies on fixes the rotation direction relative to the sketch normal
        Vec3 radial0 = Vec3.Zero;
        foreach (var r in regs) foreach (var l in r.AllLoops()) foreach (var v in l.Verts)
                {
                    var w = frame.ToWorld(v.P); var d = (w - O) - A * Vec3.Dot(w - O, A);
                    if (d.Length > radial0.Length) radial0 = d;
                }
        if (radial0.Length < 1e-12) throw new ArgumentException("profile lies on the axis");
        radial0 = radial0.Normalized;
        var rotDir = Vec3.Cross(A, radial0);
        bool loopsCcwAboutRot = Vec3.Dot(rotDir, n) > 0;

        int pieces = (int)Math.Ceiling(angle / Math.PI - 1e-9);
        var angles = Enumerable.Range(0, pieces + 1).Select(k => angle * k / pieces).ToArray();
        int AngleIndex(int k) => full && k == pieces ? 0 : k;
        var rots = angles.Select(a => Transform.Rotation(O, A, a)).ToArray();

        var body = new NBody();
        var startTag = new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef);
        var endTag = new FaceTag(prefix + ".End", SurfaceDef.PlaneDef);
        var tags = new Dictionary<string, FaceTag>();

        foreach (var reg in regs)
        {
            var loops = reg.AllLoops().Select(l => Segments(l)).Where(l => l.Count > 0).ToList();
            var verts = new Dictionary<(int, int, int), NVertex>();
            var axisVerts = new Dictionary<(int, int), NVertex>();
            bool OnAxis(int li, int vi) => R(frame.ToWorld(loops[li][vi].A)) < onAxis;
            NVertex V(int li, int vi, int ai)
            {
                var p0 = frame.ToWorld(loops[li][vi].A);
                if (R(p0) < onAxis)
                {
                    if (!axisVerts.TryGetValue((li, vi), out var av)) axisVerts[(li, vi)] = av = new NVertex(O + A * H(p0));
                    return av;
                }
                var aik = AngleIndex(ai);
                if (!verts.TryGetValue((li, vi, aik), out var v)) verts[(li, vi, aik)] = v = new NVertex(rots[aik].Point(p0));
                return v;
            }
            var profEdges = new Dictionary<(int, int, int), (NEdge e, bool fwd)>();
            (NEdge e, bool fwd) ProfEdge(int li, int si, int ai)
            {
                var segs = loops[li];
                // segments lying on the axis are the same edge at every angle
                if (OnAxis(li, si) && OnAxis(li, (si + 1) % segs.Count) && segs[si].Arc == null && segs[si].Spline == null) ai = 0;
                var aik = AngleIndex(ai);
                if (profEdges.TryGetValue((li, si, aik), out var pe)) return pe;
                var s = segs[si];
                var va = V(li, si, aik); var vb = V(li, (si + 1) % segs.Count, aik);
                var rot = rots[aik];
                (NEdge, bool) res;
                if (s.Spline is { } sp) res = SplineEdge(Mapped(sp.Curve, (x, y) => rot.Point(frame.ToWorld(x, y, 0))), sp.T0, sp.T1, va, vb);
                else if (s.Arc == null)
                {
                    var d = vb.P - va.P;
                    res = (new NEdge(new NLine(va.P, d), 0, d.Length, va, vb), true);
                }
                else
                {
                    var circ = new NCircle(rot.Point(frame.ToWorld(s.Arc.Center)), rot.Vector(n), rot.Vector(frame.XDir), s.Arc.Radius);
                    res = ArcEdge(circ, va, vb, s.Ccw);
                }
                profEdges[(li, si, aik)] = res;
                return res;
            }
            var circEdges = new Dictionary<(int, int, int), NEdge>();
            NEdge CircEdge(int li, int vi, int k)
            {
                if (circEdges.TryGetValue((li, vi, k), out var ce)) return ce;
                var p0 = frame.ToWorld(loops[li][vi].A);
                var c = new NCircle(O + A * H(p0), A, (p0 - O) - A * H(p0), R(p0));
                ce = new NEdge(c, angles[k], angles[k + 1], V(li, vi, k), V(li, vi, k + 1));
                circEdges[(li, vi, k)] = ce;
                return ce;
            }

            for (int li = 0; li < loops.Count; li++)
            {
                var segs = loops[li];
                for (int si = 0; si < segs.Count; si++)
                {
                    var s = segs[si];
                    var pa = frame.ToWorld(s.A); var pb = frame.ToWorld(s.B);
                    double ra = R(pa), rb = R(pb), ha = H(pa), hb = H(pb);
                    bool aOn = ra < onAxis, bOn = rb < onAxis;
                    if (aOn && bOn && s.Arc == null && s.Spline == null) continue; // segment on the axis: no surface
                    var name = prefix + ".R." + s.Name;
                    NSurface surf;
                    var refDir = radial0;
                    if (s.Spline is { } sp) surf = Revolved(Mapped(sp.Curve, (x, y) => frame.ToWorld(x, y, 0)), O, A, angle);
                    else if (s.Arc != null)
                    {
                        var c = frame.ToWorld(s.Arc.Center);
                        var rc = R(c); var hc = H(c);
                        var def = rc < 1e-9
                            ? new SurfaceDef { Kind = SurfaceKind.Sphere, Origin = O + A * hc, Axis = A, RefDir = refDir, Radius = s.Arc.Radius }
                            : new SurfaceDef { Kind = SurfaceKind.Torus, Origin = O + A * hc, Axis = A, RefDir = refDir, Radius = rc, Param2 = s.Arc.Radius };
                        surf = new NSurface(def with { Segments = SurfaceDef.SegmentsFor(Math.Max(ra, rb)) });
                    }
                    else if (Math.Abs(ra - rb) < 1e-9)
                        surf = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cylinder, Origin = O, Axis = A, RefDir = refDir, Radius = ra, Segments = SurfaceDef.SegmentsFor(ra) });
                    else if (Math.Abs(ha - hb) < 1e-9)
                    {
                        var outw = Vec3.Cross((pb - pa).Normalized, n);
                        var pn = Vec3.Dot(outw, A) >= 0 ? A : -A;
                        surf = NSurface.FromPlane(Plane.FromPointNormal(pa, pn));
                    }
                    else
                    {
                        var sign = (rb - ra) * (hb - ha) > 0 ? 1 : -1;
                        var half = Math.Atan2(Math.Abs(rb - ra), Math.Abs(hb - ha));
                        surf = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cone, Origin = O + A * ha, Axis = A * sign, RefDir = refDir, Radius = ra, Param2 = half, Segments = SurfaceDef.SegmentsFor(Math.Max(ra, rb)) });
                    }
                    if (!tags.TryGetValue(name, out var tag)) tags[name] = tag = new FaceTag(name, surf.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : surf.Def);
                    var midP = s.Spline != null ? frame.ToWorld(MidOfSpline(s)) : s.Arc == null ? (pa + pb) * 0.5 : frame.ToWorld(MidOfArc(s));
                    var tangent = s.Spline != null ? (frame.ToWorld(MidOfSpline(s) + SplineTangent(s)) - frame.ToWorld(MidOfSpline(s))).Normalized
                        : s.Arc == null ? (pb - pa).Normalized : ArcTangent(s, frame);
                    var outward = Vec3.Cross(tangent, n);
                    bool sense = surf.Kind == SurfaceKind.Plane || Vec3.Dot(surf.Normal(midP), outward) > 0;
                    for (int k = 0; k < pieces; k++)
                    {
                        var face = new NFace(surf, sense, tag, Transform.Rotation(O, A, 0.5 * (angles[k] + angles[k + 1])).Point(midP));
                        var loop = new NLoop();
                        var e0 = ProfEdge(li, si, k);
                        loop.Coedges.Add(new NCoedge(e0.e, e0.fwd));
                        if (!bOn) loop.Coedges.Add(new NCoedge(CircEdge(li, (si + 1) % segs.Count, k), true));
                        var e1 = ProfEdge(li, si, k + 1);
                        loop.Coedges.Add(new NCoedge(e1.e, !e1.fwd));
                        if (!aOn) loop.Coedges.Add(new NCoedge(CircEdge(li, si, k), false));
                        if (!loopsCcwAboutRot) RevLoop(loop);
                        face.Loops.Add(loop);
                        body.Faces.Add(face);
                    }
                }
            }
            if (!full)
            {
                var startPlane = Plane.FromPointNormal(O, -rotDir);
                var endPlane = Plane.FromPointNormal(O, rots[pieces].Normal(rotDir));
                var sf = new NFace(NSurface.FromPlane(startPlane), true, startTag);
                var ef = new NFace(NSurface.FromPlane(endPlane), true, endTag);
                for (int li = 0; li < loops.Count; li++)
                {
                    var l0 = new NLoop(); var l1 = new NLoop();
                    for (int si = 0; si < loops[li].Count; si++)
                    {
                        var e0 = ProfEdge(li, si, 0); var e1 = ProfEdge(li, si, pieces);
                        l0.Coedges.Add(new NCoedge(e0.e, e0.fwd));
                        l1.Coedges.Add(new NCoedge(e1.e, e1.fwd));
                    }
                    // the start cap faces -rotDir, the end cap +rotDir; profile loops are CCW about n
                    if (loopsCcwAboutRot) RevLoop(l0); else RevLoop(l1);
                    sf.Loops.Add(l0); ef.Loops.Add(l1);
                }
                body.Faces.Add(sf); body.Faces.Add(ef);
            }
        }
        body.Link();
        return body;
    }

    static void RevLoop(NLoop l)
    {
        var r = l.Coedges.Select(c => new NCoedge(c.Edge, !c.Forward)).Reverse().ToList();
        l.Coedges.Clear(); l.Coedges.AddRange(r);
    }

    static Vec2 MidOfArc(Seg s)
    {
        var c = s.Arc!.Center;
        var a0 = (s.A - c).Angle; var a1 = (s.B - c).Angle;
        var d = a1 - a0;
        if (s.Ccw) { while (d <= 0) d += 2 * Math.PI; } else { while (d >= 0) d -= 2 * Math.PI; }
        return c + Vec2.FromAngle(a0 + d / 2, s.Arc.Radius);
    }

    static Vec3 ArcTangent(Seg s, Frame frame)
    {
        var m = MidOfArc(s);
        var r = m - s.Arc!.Center;
        var t = s.Ccw ? r.Perp : -r.Perp;
        return (frame.ToWorld(m + t.Normalized) - frame.ToWorld(m)).Normalized;
    }
}

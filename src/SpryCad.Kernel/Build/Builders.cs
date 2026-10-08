namespace SpryCad.Kernel;

/// <summary>Constructs closed solids from 2D profiles.</summary>
public static class Builders
{
    /// <summary>
    /// Extrudes profile regions (in <paramref name="frame"/> coordinates) along the frame normal from
    /// offset z0 to z1 (z0 &lt; z1). Faces are named prefix.Start, prefix.End and prefix.S.&lt;segment name&gt;.
    /// </summary>
    /// <summary>Use the exact native kernel for builders (falls back to facets if it fails).</summary>
    public static bool UseNative = true;

    public static Solid Extrude(IEnumerable<ProfileRegion> regions, Frame frame, double z0, double z1, string prefix)
    {
        var regs = regions.ToList();
        if (UseNative)
        {
            try
            {
                var nb = Native.NBuild.Extrude(regs, frame, z0, z1, prefix);
                if (nb.Validate() == null) return Native.NTess.ToSolid(nb);
            }
            catch (Exception) { /* fall back to the facet builder */ }
        }
        return ExtrudeFacets(regs, frame, z0, z1, prefix);
    }

    public static Solid ExtrudeFacets(IEnumerable<ProfileRegion> regions, Frame frame, double z0, double z1, string prefix)
    {
        if (z1 < z0) (z0, z1) = (z1, z0);
        if (z1 - z0 < 1e-9) return Solid.Empty;
        var faces = new List<Face>();
        var u = frame.Normal;
        var startTag = new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef);
        var endTag = new FaceTag(prefix + ".End", SurfaceDef.PlaneDef);
        var sideTags = new Dictionary<string, FaceTag>();
        var startPlane = Plane.FromPointNormal(frame.ToWorld(0, 0, z0), -u);
        var endPlane = Plane.FromPointNormal(frame.ToWorld(0, 0, z1), u);

        foreach (var reg0 in regions)
        {
            var reg = reg0.Normalized();
            var startLoops = new List<Vec3[]>();
            var endLoops = new List<Vec3[]>();
            foreach (var loop in reg.AllLoops())
            {
                var n = loop.Verts.Count;
                var bot = new Vec3[n]; var top = new Vec3[n];
                for (int i = 0; i < n; i++)
                {
                    bot[i] = frame.ToWorld(loop.Verts[i].P.X, loop.Verts[i].P.Y, z0);
                    top[i] = frame.ToWorld(loop.Verts[i].P.X, loop.Verts[i].P.Y, z1);
                }
                endLoops.Add(top);
                var rev = (Vec3[])bot.Clone(); Array.Reverse(rev);
                startLoops.Add(rev);
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    var v = loop.Verts[i];
                    var d = bot[j] - bot[i];
                    if (d.Length < 1e-12) continue;
                    var normal = Vec3.Cross(d, u).Normalized;
                    var tag = SideTag(sideTags, prefix + ".S." + v.Name, v.Arc, frame, u);
                    var plane = Plane.FromPointNormal(bot[i], normal);
                    faces.Add(new Face(plane, tag, bot[i], bot[j], top[j], top[i]));
                }
            }
            faces.Add(new Face(startPlane, startTag, startLoops));
            faces.Add(new Face(endPlane, endTag, endLoops));
        }
        return new Solid(faces);
    }

    static FaceTag SideTag(Dictionary<string, FaceTag> cache, string name, ArcInfo? arc, Frame frame, Vec3 axis)
    {
        if (cache.TryGetValue(name, out var t)) return t;
        SurfaceDef surf = SurfaceDef.PlaneDef;
        if (arc != null)
            surf = new SurfaceDef
            {
                Kind = SurfaceKind.Cylinder,
                Origin = frame.ToWorld(arc.Center),
                Axis = axis,
                RefDir = frame.XDir,
                Radius = arc.Radius,
                Segments = arc.Segments,
            };
        t = new FaceTag(name, surf);
        cache[name] = t;
        return t;
    }

    /// <summary>
    /// Revolves profile regions about an axis lying in the sketch plane.
    /// The axis passes through <paramref name="axisPoint"/> with direction <paramref name="axisDir"/> (sketch coords).
    /// Angle in radians (full revolution if &gt;= 2π). Faces named prefix.R.&lt;segment&gt;, prefix.Start/End.
    /// </summary>
    public static Solid Revolve(IEnumerable<ProfileRegion> regions, Frame frame, Vec2 axisPoint, Vec2 axisDir, double angle, string prefix, int segmentsOverride = 0,
        IReadOnlyList<double>? stepAngles = null)
    {
        var regs = regions.ToList();
        if (UseNative)
        {
            try
            {
                // a negative sweep is the same rotation about the reversed axis; the native builder
                // orients the body outward for positive sweeps, so no volume check is needed
                var nb = angle < 0
                    ? Native.NBuild.Revolve(regs, frame, axisPoint, -axisDir, -angle, prefix)
                    : Native.NBuild.Revolve(regs, frame, axisPoint, axisDir, angle, prefix);
                if (nb.Validate() == null) return Native.NTess.ToSolid(nb);
            }
            catch (Exception) { /* fall back */ }
        }
        return RevolveFacets(regs, frame, axisPoint, axisDir, angle, prefix, segmentsOverride, stepAngles);
    }

    public static Solid RevolveFacets(IEnumerable<ProfileRegion> regions, Frame frame, Vec2 axisPoint, Vec2 axisDir, double angle, string prefix, int segmentsOverride = 0,
        IReadOnlyList<double>? stepAngles = null)
    {
        var regs = regions.Select(r => r.Normalized()).ToList();
        if (regs.Count == 0) return Solid.Empty;
        bool full = angle >= 2 * Math.PI - 1e-9;
        if (full) angle = 2 * Math.PI;
        if (angle < 1e-9) return Solid.Empty;

        var O = frame.ToWorld(axisPoint);
        var A = (frame.ToWorld(axisPoint + axisDir.Normalized) - O).Normalized;
        var ad = axisDir.Normalized;

        double maxR = 0;
        foreach (var r in regs) foreach (var l in r.AllLoops()) foreach (var v in l.Verts)
                    maxR = Math.Max(maxR, Math.Abs(Vec2.Cross(ad, v.P - axisPoint)));
        int fullSegs = segmentsOverride > 0 ? segmentsOverride : SurfaceDef.SegmentsFor(maxR);
        int steps = full ? fullSegs : Math.Max(1, (int)Math.Ceiling(fullSegs * angle / (2 * Math.PI)));
        Transform[] rots;
        if (stepAngles != null && !full && stepAngles.Count >= 2)
        {
            // explicit rotation angles (0 .. angle), used to line facets up with an existing surface
            steps = stepAngles.Count - 1;
            rots = stepAngles.Select(a => Transform.Rotation(O, A, a)).ToArray();
        }
        else
        {
            rots = new Transform[steps + 1];
            for (int k = 0; k <= steps; k++) rots[k] = Transform.Rotation(O, A, angle * k / steps);
        }
        // the side of the axis the profile is on determines how the sketch normal relates to rotation
        var sampleP = frame.ToWorld(regs[0].Outer.Verts[0].P);
        foreach (var r in regs) foreach (var v in r.Outer.Verts)
            {
                var w = frame.ToWorld(v.P);
                var rr = (w - O) - A * Vec3.Dot(w - O, A);
                if (rr.Length > 1e-9) { sampleP = w; break; }
            }
        var radial0 = ((sampleP - O) - A * Vec3.Dot(sampleP - O, A)).Normalized;
        var rotDir = Vec3.Cross(A, radial0); // direction points move when rotating positively
        // loops are CCW about the frame normal: side quads (a0,b0,b1,a1) face outward when the
        // rotation moves points along +normal; otherwise they must be flipped
        bool flipSides = Vec3.Dot(rotDir, frame.Normal) < 0;

        var faces = new List<Face>();
        var tags = new Dictionary<string, FaceTag>();
        var startTag = new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef);
        var endTag = new FaceTag(prefix + ".End", SurfaceDef.PlaneDef);

        foreach (var reg in regs)
        {
            foreach (var loop in reg.AllLoops())
            {
                int n = loop.Verts.Count;
                var p3 = loop.Verts.Select(v => frame.ToWorld(v.P)).ToArray();
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    var a = p3[i]; var b = p3[j];
                    if ((b - a).Length < 1e-12) continue;
                    double ha = Vec3.Dot(a - O, A), hb = Vec3.Dot(b - O, A);
                    double ra = ((a - O) - A * ha).Length, rb = ((b - O) - A * hb).Length;
                    if (ra < 1e-9 && rb < 1e-9) continue; // segment on the axis
                    var v = loop.Verts[i];
                    var name = prefix + ".R." + v.Name;
                    var tag = RevolveTag(tags, name, v, frame, O, A, ha, hb, ra, rb);

                    if (Math.Abs(ha - hb) < 1e-9 && !(v.Arc != null))
                    {
                        // planar annulus / sector, built as one face
                        faces.Add(Annulus(a, b, ra, rb, rots, full, flipSides, tag, A));
                        continue;
                    }
                    for (int k = 0; k < steps; k++)
                    {
                        var a0 = rots[k].Point(a); var b0 = rots[k].Point(b);
                        var a1 = rots[k + 1].Point(a); var b1 = rots[k + 1].Point(b);
                        var pts = new List<Vec3>(4) { a0, b0 };
                        if (rb > 1e-9) pts.Add(b1);
                        if (ra > 1e-9) pts.Add(a1);
                        if (pts.Count < 3) continue;
                        if (flipSides) pts.Reverse();
                        var pl = Plane.FromPolygon(pts);
                        if (pl == null) continue;
                        faces.Add(new Face(pl, tag, pts.ToArray()));
                    }
                }
            }
            if (!full)
            {
                // caps
                var capLoops0 = new List<Vec3[]>(); var capLoops1 = new List<Vec3[]>();
                foreach (var loop in reg.AllLoops())
                {
                    var p3 = loop.Verts.Select(v => frame.ToWorld(v.P)).ToArray();
                    var l0 = (Vec3[])p3.Clone(); var l1 = p3.Select(p => rots[steps].Point(p)).ToArray();
                    // start cap normal = -rotDir, end cap normal = +rotDir(rotated)
                    // start cap normal is -rotDir, end cap normal is +rotDir (rotated)
                    if (!flipSides) Array.Reverse(l0); else Array.Reverse(l1);
                    capLoops0.Add(l0); capLoops1.Add(l1);
                }
                var n0 = -rotDir;
                faces.Add(new Face(Plane.FromPointNormal(capLoops0[0][0], n0), startTag, capLoops0));
                var n1 = rots[steps].Normal(rotDir);
                faces.Add(new Face(Plane.FromPointNormal(capLoops1[0][0], n1), endTag, capLoops1));
            }
        }
        return new Solid(faces);
    }

    static Face Annulus(Vec3 a, Vec3 b, double ra, double rb, Transform[] rots, bool full, bool flipSides, FaceTag tag, Vec3 axis)
    {
        // outward normal of the annulus: +/- axis; determine from rotating segment orientation
        var a1 = rots[1].Point(a); var b1 = rots[1].Point(b);
        var quad = new List<Vec3> { a, b, b1, a1 };
        if (flipSides) quad.Reverse();
        var pl = Plane.FromPolygon(quad) ?? Plane.FromPointNormal(a, axis);
        var n = Vec3.Dot(pl.Normal, axis) >= 0 ? axis : -axis;
        var plane = Plane.FromPointNormal(a, n);
        int steps = rots.Length - 1;
        Vec3[] Ring(Vec3 p) => Enumerable.Range(0, full ? steps : steps + 1).Select(k => rots[k].Point(p)).ToArray();
        var outerP = ra > rb ? a : b; var innerP = ra > rb ? b : a; var rin = Math.Min(ra, rb);
        if (full)
        {
            var outer = Ring(outerP);
            if (Vec3.Dot(LoopNormal(outer), n) < 0) Array.Reverse(outer);
            var loops = new List<Vec3[]> { outer };
            if (rin > 1e-9)
            {
                var inner = Ring(innerP);
                if (Vec3.Dot(LoopNormal(inner), n) > 0) Array.Reverse(inner);
                loops.Add(inner);
            }
            return new Face(plane, tag, loops);
        }
        else
        {
            var o = Ring(outerP); var i = Ring(innerP);
            var pts = new List<Vec3>(o);
            if (rin > 1e-9) pts.AddRange(i.Reverse()); else pts.Add(innerP);
            var arr = pts.ToArray();
            if (Vec3.Dot(LoopNormal(arr), n) < 0) Array.Reverse(arr);
            return new Face(plane, tag, arr);
        }
    }

    static Vec3 LoopNormal(Vec3[] l)
    {
        var s = Vec3.Zero;
        for (int i = 0; i < l.Length; i++) s += Vec3.Cross(l[i], l[(i + 1) % l.Length]);
        return s;
    }

    static FaceTag RevolveTag(Dictionary<string, FaceTag> cache, string name, ProfileVertex v, Frame frame, Vec3 O, Vec3 A, double ha, double hb, double ra, double rb)
    {
        if (cache.TryGetValue(name, out var t)) return t;
        var refDir = frame.XDir - A * Vec3.Dot(frame.XDir, A);
        if (refDir.Length < 1e-9) refDir = A.AnyPerpendicular();
        refDir = refDir.Normalized;
        SurfaceDef s;
        if (v.Arc != null)
        {
            var c = frame.ToWorld(v.Arc.Center);
            var hc = Vec3.Dot(c - O, A);
            var rc = ((c - O) - A * hc).Length;
            s = rc < 1e-9
                ? new SurfaceDef { Kind = SurfaceKind.Sphere, Origin = c, Axis = A, RefDir = refDir, Radius = v.Arc.Radius }
                : new SurfaceDef { Kind = SurfaceKind.Torus, Origin = O + A * hc, Axis = A, RefDir = refDir, Radius = rc, Param2 = v.Arc.Radius };
        }
        else if (Math.Abs(ra - rb) < 1e-9)
            s = new SurfaceDef { Kind = SurfaceKind.Cylinder, Origin = O, Axis = A, RefDir = refDir, Radius = ra };
        else if (Math.Abs(ha - hb) < 1e-9)
            s = SurfaceDef.PlaneDef;
        else
        {
            // cone: axis oriented toward increasing radius
            var dirSign = (rb - ra) * (hb - ha) > 0 ? 1 : -1;
            var half = Math.Atan2(Math.Abs(rb - ra), Math.Abs(hb - ha));
            s = new SurfaceDef { Kind = SurfaceKind.Cone, Origin = O + A * ha, Axis = A * dirSign, RefDir = refDir, Radius = ra, Param2 = half };
        }
        s = s with { Segments = SurfaceDef.SegmentsFor(Math.Max(ra, rb)) };
        t = new FaceTag(name, s);
        cache[name] = t;
        return t;
    }

    /// <summary>Axis aligned box (used by tests and primitives).</summary>
    public static Solid Box(Vec3 min, Vec3 max, string prefix = "Box")
    {
        var size = max - min;
        var frame = new Frame(new Vec3(0, 0, min.Z), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var loop = ProfileLoop.Polygon(new[] { new Vec2(min.X, min.Y), new Vec2(max.X, min.Y), new Vec2(max.X, max.Y), new Vec2(min.X, max.Y) }, "E");
        return Extrude(new[] { new ProfileRegion(loop) }, frame, 0, size.Z, prefix);
    }

    /// <summary>Cylinder along an axis.</summary>
    public static Solid Cylinder(Vec3 baseCenter, Vec3 axis, double radius, double height, string prefix, Vec3? refDir = null, int segments = 0)
    {
        var n = axis.Normalized;
        var x = (refDir ?? n.AnyPerpendicular());
        x = (x - n * Vec3.Dot(x, n)).Normalized;
        var frame = new Frame(baseCenter, x, Vec3.Cross(n, x), n);
        var loop = ProfileLoop.Circle(Vec2.Zero, radius, "C", segments);
        return Extrude(new[] { new ProfileRegion(loop) }, frame, 0, height, prefix);
    }
}

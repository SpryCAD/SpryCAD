using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

/// <summary>A solid read from a STEP file.</summary>
public sealed record StepSolid(string Name, NBody Body);

/// <summary>What was read from a STEP file.</summary>
public sealed class StepImportResult
{
    public List<StepSolid> Solids { get; } = new();
    public List<string> Warnings { get; } = new();
    /// <summary>Millimetres per file length unit.</summary>
    public double UnitMm { get; set; } = 1;
}

/// <summary>
/// Imports the exact B-rep solids of a STEP AP203/AP214/AP242 file as native bodies: planes, cylinders, cones,
/// spheres and tori bounded by lines, circles, ellipses and B-spline curves. Faces that wrap around their
/// surface are split at half turns (<see cref="NSeamSplit"/>), full-circle edges in two. Face names are
/// <c>prefix + "I" + id</c> of the STEP face, stable for a given file.
/// </summary>
public static class StepImport
{
    /// <param name="mmToUnits">Scale from millimetres to the document's length unit.</param>
    public static StepImportResult Read(byte[] data, string prefix, double mmToUnits = 1)
    {
        var f = StepFile.Parse(data);
        var r = new StepImportResult();
        r.UnitMm = LengthUnitMm(f);
        double angleScale = AngleUnitRadians(f);
        var ctx = new Ctx(f, r.UnitMm * mmToUnits, angleScale, prefix, r.Warnings);

        var solids = f.OfType("MANIFOLD_SOLID_BREP").Concat(f.OfType("BREP_WITH_VOIDS")).Distinct().ToList();
        if (solids.Count == 0)
        {
            var shells = f.OfType("CLOSED_SHELL").ToList();
            if (shells.Count == 0)
            {
                var free = f.OfType("ADVANCED_FACE").Count() + f.OfType("B_SPLINE_SURFACE_WITH_KNOTS").Count();
                throw new InvalidDataException(free > 0
                    ? "The STEP file has surfaces but no solid (an open surface model); only solids can be imported."
                    : "The STEP file contains no solid geometry.");
            }
            foreach (var sh in shells) ctx.AddSolid(r, "Solid", new[] { (sh, false) });
        }
        foreach (var s in solids)
        {
            var name = s.Args.Count > 0 && s.Args[0] is string n && n.Length > 0 ? n : "Solid";
            var parts = new List<(StepInstance shell, bool reversed)> { (f.Get(s.Args[1]), false) };
            if (s.Is("BREP_WITH_VOIDS") && s.Args.Count > 2 && s.Args[2] is List<object?> voids)
                foreach (var v in voids)
                {
                    var oc = f.Get(v); // ORIENTED_CLOSED_SHELL(name, *, shell, orientation)
                    parts.Add((f.Get(oc.Args[2]), !(oc.Args[3] is StepEnum { Value: "T" })));
                }
            ctx.AddSolid(r, name, parts);
        }
        if (r.Solids.Count == 0) throw new InvalidDataException("No solid of the STEP file could be imported: " + string.Join("; ", r.Warnings.Take(3)));
        return r;
    }

    // ------------------------------------------------------------------------------------------
    // units

    static double LengthUnitMm(StepFile f)
    {
        foreach (var u in f.Instances.Values.OrderBy(i => i.Id))
            if (u.Is("LENGTH_UNIT")) return UnitFactor(f, u, length: true);
        return 1;
    }

    static double AngleUnitRadians(StepFile f)
    {
        foreach (var u in f.Instances.Values.OrderBy(i => i.Id))
            if (u.Is("PLANE_ANGLE_UNIT")) return UnitFactor(f, u, length: false);
        return 1;
    }

    /// <summary>Millimetres (length) or radians (angle) per unit.</summary>
    static double UnitFactor(StepFile f, StepInstance u, bool length)
    {
        if (u.Part("SI_UNIT") is { } si)
        {
            var prefix = si.Count > 0 && si[0] is StepEnum p ? p.Value : null;
            double pf = prefix switch
            {
                "MILLI" => 1e-3, "CENTI" => 1e-2, "DECI" => 1e-1, "KILO" => 1e3, "MICRO" => 1e-6, "NANO" => 1e-9, _ => 1,
            };
            return length ? pf * 1000 : pf;
        }
        if (u.Part("CONVERSION_BASED_UNIT") is { } cb && cb.Count > 1 && cb[1] is StepRef mr)
        {
            // (LENGTH|PLANE_ANGLE)_MEASURE_WITH_UNIT(value, unit)
            var m = f[mr];
            var v = m.Args[0] switch { StepTyped t => Convert.ToDouble(t.Value), double d => d, _ => 1.0 };
            var baseUnit = m.Args[1] is StepRef br ? UnitFactor(f, f[br], length) : 1;
            return v * baseUnit;
        }
        return 1;
    }

    // ------------------------------------------------------------------------------------------

    sealed class Ctx
    {
        readonly StepFile _f;
        readonly double _s, _ang;
        readonly string _prefix;
        readonly List<string> _warn;
        readonly Dictionary<int, NVertex> _vertices = new();
        readonly Dictionary<int, List<NEdge>> _edges = new();
        readonly Dictionary<int, NSurface?> _surfaces = new();

        public Ctx(StepFile f, double scale, double angle, string prefix, List<string> warnings)
        {
            _f = f; _s = scale; _ang = angle; _prefix = prefix; _warn = warnings;
        }

        public void AddSolid(StepImportResult r, string name, IEnumerable<(StepInstance shell, bool reversed)> shells)
        {
            var body = new NBody();
            int skipped = 0; var reasons = new SortedSet<string>();
            foreach (var (shell, reversed) in shells)
            {
                var faces = shell.Args[1] as List<object?> ?? throw new InvalidDataException($"STEP: shell #{shell.Id} has no faces.");
                foreach (var fr in faces)
                {
                    try
                    {
                        foreach (var nf in Face(_f.Get(fr)))
                        {
                            if (reversed) { nf.Sense = !nf.Sense; foreach (var l in nf.Loops) { l.Coedges.Reverse(); for (int i = 0; i < l.Coedges.Count; i++) l.Coedges[i] = new NCoedge(l.Coedges[i].Edge, !l.Coedges[i].Forward); } }
                            body.Faces.Add(nf);
                        }
                    }
                    catch (NotSupportedException ex) { skipped++; reasons.Add(ex.Message); }
                }
            }
            if (skipped > 0)
            {
                _warn.Add($"{name}: {skipped} face(s) use geometry that cannot be imported yet ({string.Join(", ", reasons)}); the solid was skipped.");
                return;
            }
            body.Link();
            try { NSeamSplit.Apply(body); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { _warn.Add($"{name}: {ex.Message}; the solid was skipped."); return; }
            body.Link();
            var err = body.Validate();
            if (err != null) { _warn.Add($"{name}: not a closed solid ({err}); skipped."); return; }
            r.Solids.Add(new StepSolid(name, body));
        }

        Vec3 Point(object? a)
        {
            var p = _f.Get(a); // CARTESIAN_POINT(name, (x, y, z))
            var c = (List<object?>)p.Args[1];
            return new Vec3(D(c[0]), c.Count > 1 ? D(c[1]) : 0, c.Count > 2 ? D(c[2]) : 0) * _s;
        }

        static double D(object? o) => o switch { double d => d, StepTyped t => D(t.Value), _ => throw new InvalidDataException("STEP: expected a number.") };

        Vec3 Direction(object? a)
        {
            var d = _f.Get(a); // DIRECTION(name, (x, y, z))
            var c = (List<object?>)d.Args[1];
            return new Vec3(D(c[0]), c.Count > 1 ? D(c[1]) : 0, c.Count > 2 ? D(c[2]) : 0).Normalized;
        }

        /// <summary>AXIS2_PLACEMENT_3D(name, location, axis, ref_direction): origin, Z and X.</summary>
        (Vec3 o, Vec3 z, Vec3 x) Placement(object? a)
        {
            var p = _f.Get(a);
            var o = Point(p.Args[1]);
            var z = p.Args.Count > 2 && p.Args[2] is StepRef ? Direction(p.Args[2]) : Vec3.UnitZ;
            var x = p.Args.Count > 3 && p.Args[3] is StepRef ? Direction(p.Args[3]) : z.AnyPerpendicular();
            x = (x - z * Vec3.Dot(x, z)).Normalized;
            return (o, z, x);
        }

        NVertex Vertex(object? a)
        {
            var v = _f.Get(a);
            if (_vertices.TryGetValue(v.Id, out var nv)) return nv;
            return _vertices[v.Id] = new NVertex(Point(v.Args[1])); // VERTEX_POINT(name, point)
        }

        IEnumerable<NFace> Face(StepInstance face)
        {
            var nf = FaceLoops(face);
            if (nf.Loops.Count > 0) return new[] { nf };
            // no edges: a whole closed surface (a full sphere is one face with only a vertex loop)
            if (nf.Surface.Kind == SurfaceKind.Sphere) return WholeSphere(nf);
            throw new NotSupportedException($"closed {nf.Surface.Kind.ToString().ToLowerInvariant()} faces without edges");
        }

        /// <summary>A full sphere as two half spheres between two meridians.</summary>
        IEnumerable<NFace> WholeSphere(NFace f)
        {
            var s = f.Surface;
            var south = new NVertex(s.Origin - s.Axis * s.R); var north = new NVertex(s.Origin + s.Axis * s.R);
            NEdge Meridian(Vec3 through)
            {
                var (c, t0, t1) = NSeamSplit.CircleThrough(south.P, through, north.P);
                return new NEdge(c, t0, t1, south, north);
            }
            var m0 = Meridian(s.Origin + s.F.XDir * s.R); var m1 = Meridian(s.Origin - s.F.XDir * s.R);
            foreach (var (a, b, anchor) in new[] { (m0, m1, s.F.YDir), (m1, m0, -s.F.YDir) })
            {
                var h = new NFace(s, f.Sense, f.Tag, s.Origin + anchor * s.R);
                var l = new NLoop();
                l.Coedges.Add(new NCoedge(a, true)); l.Coedges.Add(new NCoedge(b, false));
                h.Loops.Add(l);
                // loops run counter-clockwise seen from outside
                if (NDomain.Of(h).SignedArea() < 0)
                {
                    l.Coedges.Clear();
                    l.Coedges.Add(new NCoedge(b, true)); l.Coedges.Add(new NCoedge(a, false));
                }
                yield return h;
            }
        }

        NFace FaceLoops(StepInstance face)
        {
            // ADVANCED_FACE / FACE_SURFACE (name, bounds, surface, same_sense)
            var surf = Surface(face.Args[2]);
            var sense = face.Args[3] is StepEnum { Value: "T" };
            var tag = new FaceTag($"{_prefix}I{face.Id}", surf.Kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : surf.Def);
            var nf = new NFace(surf, sense, tag);
            foreach (var b in (List<object?>)face.Args[1])
            {
                var bound = _f.Get(b); // FACE_(OUTER_)BOUND(name, loop, orientation)
                var loop = _f.Get(bound.Args[1]);
                if (loop.Is("VERTEX_LOOP")) continue; // a pole or apex: a point, not a boundary
                if (!loop.Is("EDGE_LOOP")) throw new NotSupportedException(loop.Type.ToLowerInvariant().Replace('_', ' '));
                var nl = new NLoop();
                foreach (var oe in (List<object?>)loop.Args[1])
                {
                    var o = _f.Get(oe); // ORIENTED_EDGE(name, *, *, edge, orientation)
                    var fwd = o.Args[4] is StepEnum { Value: "T" };
                    var pieces = Edge(_f.Get(o.Args[3]));
                    if (fwd) foreach (var e in pieces) nl.Coedges.Add(new NCoedge(e, true));
                    else for (int i = pieces.Count - 1; i >= 0; i--) nl.Coedges.Add(new NCoedge(pieces[i], false));
                }
                if (!(bound.Args[2] is StepEnum { Value: "T" }))
                {
                    nl.Coedges.Reverse();
                    for (int i = 0; i < nl.Coedges.Count; i++) nl.Coedges[i] = new NCoedge(nl.Coedges[i].Edge, !nl.Coedges[i].Forward);
                }
                if (nl.Coedges.Count > 0) nf.Loops.Add(nl);
            }
            return nf;
        }

        NSurface Surface(object? a)
        {
            var s = _f.Get(a);
            if (_surfaces.TryGetValue(s.Id, out var cached)) return cached ?? throw new NotSupportedException(s.Type.ToLowerInvariant());
            NSurface? res = null;
            switch (s.Type)
            {
                case "PLANE":
                    {
                        var (o, z, _) = Placement(s.Args[1]);
                        res = NSurface.FromPlane(Plane.FromPointNormal(o, z));
                        break;
                    }
                case "CYLINDRICAL_SURFACE":
                    {
                        var (o, z, x) = Placement(s.Args[1]);
                        var r = D(s.Args[2]) * _s;
                        res = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cylinder, Origin = o, Axis = z, RefDir = x, Radius = r, Segments = SurfaceDef.SegmentsFor(r) });
                        break;
                    }
                case "CONICAL_SURFACE":
                    {
                        var (o, z, x) = Placement(s.Args[1]);
                        var r = D(s.Args[2]) * _s; var half = D(s.Args[3]) * _ang;
                        res = new NSurface(new SurfaceDef { Kind = SurfaceKind.Cone, Origin = o, Axis = z, RefDir = x, Radius = r, Param2 = half, Segments = SurfaceDef.SegmentsFor(Math.Max(r, 1)) });
                        break;
                    }
                case "SPHERICAL_SURFACE":
                    {
                        var (o, z, x) = Placement(s.Args[1]);
                        var r = D(s.Args[2]) * _s;
                        res = new NSurface(new SurfaceDef { Kind = SurfaceKind.Sphere, Origin = o, Axis = z, RefDir = x, Radius = r, Segments = SurfaceDef.SegmentsFor(r) });
                        break;
                    }
                case "TOROIDAL_SURFACE":
                    {
                        var (o, z, x) = Placement(s.Args[1]);
                        var R = D(s.Args[2]) * _s; var r = D(s.Args[3]) * _s;
                        if (r >= R) { _surfaces[s.Id] = null; throw new NotSupportedException("self-intersecting torus"); }
                        res = new NSurface(new SurfaceDef { Kind = SurfaceKind.Torus, Origin = o, Axis = z, RefDir = x, Radius = R, Param2 = r, Segments = SurfaceDef.SegmentsFor(R + r) });
                        break;
                    }
            }
            _surfaces[s.Id] = res;
            return res ?? throw new NotSupportedException(Friendly(s.Type));
        }

        static string Friendly(string type) => type switch
        {
            "B_SPLINE_SURFACE_WITH_KNOTS" or "(BOUNDED_SURFACE B_SPLINE_SURFACE B_SPLINE_SURFACE_WITH_KNOTS GEOMETRIC_REPRESENTATION_ITEM RATIONAL_B_SPLINE_SURFACE REPRESENTATION_ITEM SURFACE)" => "free-form (B-spline) surfaces",
            "SURFACE_OF_REVOLUTION" => "surfaces of revolution",
            "SURFACE_OF_LINEAR_EXTRUSION" => "extruded surfaces",
            "OFFSET_SURFACE" => "offset surfaces",
            _ => type.StartsWith("(") ? "free-form (B-spline) surfaces" : type.ToLowerInvariant().Replace('_', ' '),
        };

        /// <summary>EDGE_CURVE(name, start, end, curve, same_sense) as native edges from start to end (a closed edge in two halves).</summary>
        List<NEdge> Edge(StepInstance ec)
        {
            if (_edges.TryGetValue(ec.Id, out var known)) return known;
            var v0 = Vertex(ec.Args[1]); var v1 = Vertex(ec.Args[2]);
            var same = ec.Args[4] is StepEnum { Value: "T" };
            var curve = Curve(_f.Get(ec.Args[3]), same);
            double t0 = Param(curve, v0.P, null), t1;
            bool closed = ReferenceEquals(v0, v1);
            if (curve.Periodic)
            {
                t1 = curve.Param(v1.P, t0 + Math.PI);
                while (t1 <= t0 + 1e-12) t1 += curve.Period;
                if (closed) t1 = t0 + curve.Period;
            }
            else
            {
                t1 = Param(curve, v1.P, null);
                if (closed && curve is NSpline) (t0, t1) = (0, 1);
            }
            if (t1 <= t0) throw new InvalidDataException($"STEP: edge #{ec.Id} runs backwards along its curve.");
            List<NEdge> res;
            if (closed)
            {
                var tm = 0.5 * (t0 + t1);
                var vm = new NVertex(curve.Eval(tm));
                res = new() { new NEdge(curve, t0, tm, v0, vm), new NEdge(curve, tm, t1, vm, v1) };
            }
            else res = new() { new NEdge(curve, t0, t1, v0, v1) };
            return _edges[ec.Id] = res;
        }

        static double Param(NCurve c, Vec3 p, double? hint) => c.Param(p, hint);

        /// <summary>The 3D curve, parameterized in the direction of the edge (reversed when same_sense is false).</summary>
        NCurve Curve(StepInstance c, bool same)
        {
            // SURFACE_CURVE / SEAM_CURVE / INTERSECTION_CURVE (name, curve_3d, pcurves, representation)
            if (c.Is("SURFACE_CURVE") || c.Is("SEAM_CURVE") || c.Is("INTERSECTION_CURVE"))
                return Curve(_f.Get((c.Part("SURFACE_CURVE") ?? c.Part("SEAM_CURVE") ?? c.Part("INTERSECTION_CURVE") ?? c.Args)[1]), same);
            if (c.Is("TRIMMED_CURVE")) return Curve(_f.Get(c.Args[1]), same);
            switch (c.Type)
            {
                case "LINE":
                    {
                        var p = Point(c.Args[1]);
                        var vec = _f.Get(c.Args[2]); // VECTOR(name, direction, magnitude)
                        var d = Direction(vec.Args[1]);
                        return new NLine(p, same ? d : -d);
                    }
                case "CIRCLE":
                    {
                        var (o, z, x) = Placement(c.Args[1]);
                        var r = D(c.Args[2]) * _s;
                        return new NCircle(o, same ? z : -z, x, r);
                    }
                case "ELLIPSE":
                    {
                        var (o, z, x) = Placement(c.Args[1]);
                        var a = D(c.Args[2]) * _s; var b = D(c.Args[3]) * _s;
                        var y = Vec3.Cross(z, x);
                        return new NEllipse(o, x, same ? y : -y, a, b);
                    }
            }
            if (c.Is("B_SPLINE_CURVE_WITH_KNOTS") || c.Is("B_SPLINE_CURVE")) return Spline(c, same);
            throw new NotSupportedException(c.Type.ToLowerInvariant().Replace('_', ' ') + " edges");
        }

        NCurve Spline(StepInstance c, bool same)
        {
            // simple: B_SPLINE_CURVE_WITH_KNOTS(name, degree, poles, form, closed, self, mults, knots, spec)
            // complex: (B_SPLINE_CURVE(degree, poles, form, closed, self) B_SPLINE_CURVE_WITH_KNOTS(mults, knots, spec)
            //           RATIONAL_B_SPLINE_CURVE(weights) ...)
            List<object?> bs, kn;
            if (c.Parts.Count == 1) { bs = c.Args.Skip(1).Take(5).ToList(); kn = c.Args.Skip(6).ToList(); }
            else { bs = c.Part("B_SPLINE_CURVE")!; kn = c.Part("B_SPLINE_CURVE_WITH_KNOTS")!; }
            int degree = (int)D(bs[0]);
            var poles = ((List<object?>)bs[1]).Select(Point).ToArray();
            var mults = ((List<object?>)kn[0]).Select(m => (int)D(m)).ToList();
            var knotVals = ((List<object?>)kn[1]).Select(D).ToList();
            var knots = new List<double>();
            for (int i = 0; i < mults.Count; i++) for (int k = 0; k < mults[i]; k++) knots.Add(knotVals[i]);
            if (knots.Count != poles.Length + degree + 1) throw new InvalidDataException($"STEP: B-spline #{c.Id} has inconsistent knots.");
            // normalized to [0, 1] (native splines are parameterized on the unit interval)
            double k0 = knots[degree], k1 = knots[^(degree + 1)];
            var U = knots.Select(k => (k - k0) / (k1 - k0)).ToArray();
            double[]? w = c.Part("RATIONAL_B_SPLINE_CURVE") is { } rw ? ((List<object?>)rw[0]).Select(D).ToArray() : null;
            if (w != null && w.All(x => Math.Abs(x - w[0]) < 1e-12 * Math.Abs(w[0]))) w = null;
            if (!same)
            {
                Array.Reverse(poles);
                U = U.Reverse().Select(k => 1 - k).ToArray();
                if (w != null) Array.Reverse(w);
            }
            if (w == null) return new NSpline(new BSplineCurve(degree, poles, U));
            // rational: interpolated by a cubic through enough exact points (within 1e-8 of the size)
            var size = poles.Max(p => Vec3.Distance(p, poles[0])) + 1e-9;
            for (int n = Math.Max(16, poles.Length * 4); ; n *= 2)
            {
                var pts = Enumerable.Range(0, n + 1).Select(i => Rational(degree, poles, w, U, (double)i / n)).ToList();
                var sp = BSplineCurve.Interpolate(pts, 3);
                var ns = new NSpline(sp);
                double err = 0;
                for (int i = 0; i < n; i++) { var q = Rational(degree, poles, w, U, (i + 0.5) / n); err = Math.Max(err, Vec3.Distance(ns.Eval(ns.Param(q)), q)); }
                if (err < 1e-8 * size || n > 4096) return ns;
            }
        }

        static Vec3 Rational(int p, Vec3[] P, double[] w, double[] U, double u)
        {
            int n = P.Length - 1;
            u = Math.Clamp(u, U[p], U[n + 1]);
            int span = BSplineCurve.FindSpan(n, p, u, U);
            var N = BSplineCurve.Basis(span, u, p, U);
            Vec3 num = Vec3.Zero; double den = 0;
            for (int i = 0; i <= p; i++) { var k = span - p + i; num += P[k] * (N[i] * w[k]); den += N[i] * w[k]; }
            return num / den;
        }
    }
}

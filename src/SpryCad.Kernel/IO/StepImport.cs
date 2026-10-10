using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>A solid read from a STEP file.</summary>
public sealed record StepSolid(string Name, NBody Body);

/// <summary>A part of a STEP assembly: its name and its solids, in its own coordinates.</summary>
public sealed record StepPart(string Name, List<StepSolid> Solids);

/// <summary>A placed use of a part in a STEP assembly (sub-assemblies' placements composed).</summary>
public sealed record StepOccurrence(int Part, string Name, Transform Placement);

/// <summary>A STEP assembly: its parts and where each is used.</summary>
public sealed class StepAssemblyResult
{
    public string Name { get; set; } = "Assembly";
    public List<StepPart> Parts { get; } = new();
    public List<StepOccurrence> Occurrences { get; } = new();
    public List<string> Warnings { get; } = new();
    public double UnitMm { get; set; } = 1;
}

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
    /// <summary>Read B-spline surfaces and curves that are exactly planes, cylinders, lines or circles as those.</summary>
    public static bool RecognizeAnalytic = true;

    public static StepImportResult Read(byte[] data, string prefix, double mmToUnits = 1)
    {
        var f = StepFile.Parse(data);
        var r = new StepImportResult();
        (r.UnitMm, var angleScale) = Units(f);
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
    // assemblies

    /// <summary>Whether a STEP file is an assembly (placed uses of parts), from its text alone.</summary>
    public static bool IsAssembly(byte[] data)
    {
        var text = System.Text.Encoding.Latin1.GetString(data);
        return text.Contains("NEXT_ASSEMBLY_USAGE_OCCURRENCE", StringComparison.Ordinal) || text.Contains("MAPPED_ITEM", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a STEP assembly: each product with solids becomes a part (its solids in its own coordinates), each use
    /// of it an occurrence placed where the assembly puts it (through sub-assemblies). Placements come from
    /// CONTEXT_DEPENDENT_SHAPE_REPRESENTATIONs (a REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION between the part's
    /// shape and its parent's, either way round, by an ITEM_DEFINED_TRANSFORMATION) or MAPPED_ITEMs. A product's
    /// shape may be split over representations related without a transformation (as SolidWorks and CATIA write).
    /// Null when the file has no assembly structure.
    /// </summary>
    public static StepAssemblyResult? ReadAssembly(byte[] data, string prefix, double mmToUnits = 1)
    {
        if (!IsAssembly(data)) return null;
        var f = StepFile.Parse(data);
        var res = new StepAssemblyResult();
        (res.UnitMm, var angleUnit) = Units(f);
        var ctx = new Ctx(f, res.UnitMm * mmToUnits, angleUnit, prefix, res.Warnings);
        bool IsBrep(StepInstance i) => i.Is("MANIFOLD_SOLID_BREP") || i.Is("BREP_WITH_VOIDS");
        StepInstance? Inst(object? a) => a is StepRef r && f.Instances.TryGetValue(r.Id, out var i) ? i : null;
        List<object?>? RepArgs(StepInstance i) => i.Parts.FirstOrDefault(p => p.Type.EndsWith("REPRESENTATION", StringComparison.Ordinal) && p.Args.Count >= 2 && p.Args[1] is List<object?>).Args;

        // representations related without a transformation: one shape split over them
        var plain = new Dictionary<int, List<int>>();
        void Link(int x, int y)
        {
            if (!plain.TryGetValue(x, out var l)) plain[x] = l = new(); l.Add(y);
            if (!plain.TryGetValue(y, out var m)) plain[y] = m = new(); m.Add(x);
        }
        foreach (var i in f.Instances.Values)
            if (i.Part("REPRESENTATION_RELATIONSHIP") is { } rr && !i.Is("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION") && rr.Count >= 4 && rr[2] is StepRef r1 && rr[3] is StepRef r2)
                Link(r1.Id, r2.Id);
            else if (i.Is("SHAPE_REPRESENTATION_RELATIONSHIP") && !i.Is("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION") && i.Args.Count >= 4 && i.Args[2] is StepRef s1 && i.Args[3] is StepRef s2)
                Link(s1.Id, s2.Id);
        HashSet<int> Closure(int rep)
        {
            var seen = new HashSet<int> { rep }; var stack = new Stack<int>(); stack.Push(rep);
            while (stack.Count > 0) foreach (var n in plain.GetValueOrDefault(stack.Pop()) ?? new()) if (seen.Add(n)) stack.Push(n);
            return seen;
        }

        // product definitions: their shapes and names
        var repsOf = new Dictionary<int, HashSet<int>>();
        foreach (var sdr in f.OfType("SHAPE_DEFINITION_REPRESENTATION"))
        {
            if (Inst(sdr.Args[0]) is not { } pds || pds.Args.Count < 3 || Inst(pds.Args[2]) is not { } pd || !pd.Is("PRODUCT_DEFINITION")) continue;
            if (Inst(sdr.Args[1]) is not { } rep) continue;
            if (!repsOf.TryGetValue(pd.Id, out var set)) repsOf[pd.Id] = set = new();
            set.UnionWith(Closure(rep.Id));
        }
        string NameOf(int pdId)
        {
            var pd = f.Instances[pdId];
            if (Inst(pd.Args.Count > 2 ? pd.Args[2] : null) is { } pdf && Inst(pdf.Args.Count > 2 ? pdf.Args[2] : null) is { } prod)
                foreach (var a in new[] { 1, 0 })
                    if (prod.Args.Count > a && prod.Args[a] is string n && n.Trim().Length > 0) return n.Trim();
            return "Part";
        }
        Transform Frame(object? a)
        {
            var (o, z, x) = ctx.Placement(a);
            var y = Vec3.Cross(z, x);
            return new Transform(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z, o);
        }

        // the assembly's links: parent product definition -> (child product definition, child-to-parent placement, name)
        var children = new Dictionary<int, List<(int child, Transform t, string name)>>();
        var placed = new HashSet<int>();
        foreach (var cdsr in f.OfType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
        {
            if (Inst(cdsr.Args[0]) is not { } rel || Inst(cdsr.Args[1]) is not { } ipds || ipds.Args.Count < 3 || Inst(ipds.Args[2]) is not { } nauo) continue;
            if (!nauo.Is("NEXT_ASSEMBLY_USAGE_OCCURRENCE") || nauo.Args.Count < 5 || Inst(nauo.Args[3]) is not { } parent || Inst(nauo.Args[4]) is not { } child) continue;
            var rr = rel.Part("REPRESENTATION_RELATIONSHIP");
            var rwt = rel.Part("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION");
            if (rr == null || rwt == null || rr.Count < 4 || Inst(rwt[0]) is not { } idt || !idt.Is("ITEM_DEFINED_TRANSFORMATION")) continue;
            var f1 = Frame(idt.Args[2]); var f2 = Frame(idt.Args[3]);
            // item 1 belongs to rep 1: the part's shape is rep 1 (child to parent: frame 2 after frame 1 undone), or
            // the writer put it the other way round
            var childReps = repsOf.GetValueOrDefault(child.Id) ?? new();
            bool childIsRep1 = rr[2] is StepRef c1 && childReps.Contains(c1.Id) || !(rr[3] is StepRef c2 && childReps.Contains(c2.Id));
            var t = childIsRep1 ? f2 * f1.RigidInverse() : f1 * f2.RigidInverse();
            var name = nauo.Args[1] is string nn && nn.Length > 0 ? nn : NameOf(child.Id);
            if (!children.TryGetValue(parent.Id, out var l)) children[parent.Id] = l = new();
            l.Add((child.Id, t, name));
            placed.Add(nauo.Id);
        }
        // uses without a placement: where the part is
        foreach (var nauo in f.OfType("NEXT_ASSEMBLY_USAGE_OCCURRENCE"))
        {
            if (placed.Contains(nauo.Id) || Inst(nauo.Args[3]) is not { } parent || Inst(nauo.Args[4]) is not { } child) continue;
            if (!children.TryGetValue(parent.Id, out var l)) children[parent.Id] = l = new();
            l.Add((child.Id, Transform.Identity, nauo.Args[1] is string nn && nn.Length > 0 ? nn : NameOf(child.Id)));
        }

        // a product's own solids, and mapped items in its shapes (a representation used again, placed)
        var partIndex = new Dictionary<string, int>();
        int PartOf(string key, string name, IEnumerable<StepInstance> breps)
        {
            if (partIndex.TryGetValue(key, out var i)) return i;
            var r = new StepImportResult();
            foreach (var b in breps.OrderBy(x => x.Id))
            {
                var parts = new List<(StepInstance shell, bool reversed)> { (f.Get(b.Args[1]), false) };
                if (b.Is("BREP_WITH_VOIDS") && b.Args.Count > 2 && b.Args[2] is List<object?> voids)
                    foreach (var v in voids) { var oc = f.Get(v); parts.Add((f.Get(oc.Args[2]), !(oc.Args[3] is StepEnum { Value: "T" }))); }
                ctx.AddSolid(r, b.Args.Count > 0 && b.Args[0] is string bn && bn.Length > 0 ? bn : name, parts);
            }
            res.Warnings.AddRange(r.Warnings);
            if (r.Solids.Count == 0) return partIndex[key] = -1;
            res.Parts.Add(new StepPart(name, r.Solids));
            return partIndex[key] = res.Parts.Count - 1;
        }
        IEnumerable<StepInstance> Items(IEnumerable<int> reps) =>
            reps.Select(id => f.Instances.GetValueOrDefault(id)).OfType<StepInstance>().SelectMany(rep => RepArgs(rep) is { } ra ? ((List<object?>)ra[1]).Select(Inst).OfType<StepInstance>() : Enumerable.Empty<StepInstance>());

        void Walk(IEnumerable<int> reps, string key, string name, Transform t, int depth)
        {
            if (depth > 64) { res.Warnings.Add("The assembly is nested too deeply (a loop?); its deeper levels were left out."); return; }
            var repSet = reps.ToList();
            var items = Items(repSet).ToList();
            var breps = items.Where(IsBrep).ToList();
            if (breps.Count > 0 && PartOf(key, name, breps) is var pi and >= 0) res.Occurrences.Add(new StepOccurrence(pi, name, t));
            // mapped items: another representation placed in this one
            foreach (var mi in items.Where(i => i.Is("MAPPED_ITEM")))
            {
                if (Inst(mi.Args[1]) is not { } map || map.Args.Count < 2 || Inst(map.Args[1]) is not { } mrep) continue;
                var mt = Frame(mi.Args[2]) * Frame(map.Args[0]).RigidInverse();
                // the product owning that representation names it, if any
                var owner = repsOf.FirstOrDefault(kv => kv.Value.Contains(mrep.Id)).Key;
                Walk(Closure(mrep.Id), "rep:" + mrep.Id, owner != 0 ? NameOf(owner) : (mi.Args[0] is string mn && mn.Length > 0 ? mn : name), t * mt, depth + 1);
            }
        }
        void WalkProduct(int pd, string name, Transform t, int depth)
        {
            Walk(repsOf.GetValueOrDefault(pd) ?? new HashSet<int>(), "pd:" + pd, name, t, depth);
            foreach (var (child, ct, cname) in children.GetValueOrDefault(pd) ?? new())
                WalkProduct(child, cname, t * ct, depth + 1);
        }

        // the roots: product definitions used by no other
        var childIds = new HashSet<int>(children.Values.SelectMany(l => l.Select(x => x.child)));
        var roots = children.Keys.Where(k => !childIds.Contains(k)).ToList();
        if (roots.Count == 0) roots = repsOf.Keys.Where(k => !childIds.Contains(k)).ToList();
        foreach (var root in roots.OrderBy(x => x)) WalkProduct(root, NameOf(root), Transform.Identity, 0);
        if (roots.Count == 1) res.Name = NameOf(roots[0]);
        if (res.Occurrences.Count == 0) throw new InvalidDataException("No part of the STEP assembly could be imported: " + string.Join("; ", res.Warnings.Take(3)));
        return res;
    }

    // ------------------------------------------------------------------------------------------
    // units

    /// <summary>
    /// The units of the solids' geometry (millimetres per length unit, radians per angle unit): from the context of
    /// the shape representation holding them (files often carry other units too, e.g. a metre unit for presentation
    /// data); else from any unit context; else the first such unit in the file. The file is scanned once for both.
    /// </summary>
    static (double mm, double rad) Units(StepFile f)
    {
        var solids = new HashSet<int>(f.OfType("MANIFOLD_SOLID_BREP").Concat(f.OfType("BREP_WITH_VOIDS")).Select(s => s.Id));
        var sorted = f.Instances.Values.OrderBy(i => i.Id).ToList();
        // representations listing a solid among their items, then every unit context
        var contexts = new List<StepInstance>();
        foreach (var r in sorted)
        {
            var args = r.Parts.FirstOrDefault(p => p.Type.EndsWith("SHAPE_REPRESENTATION", StringComparison.Ordinal)).Args;
            if (args == null || args.Count < 3 || args[1] is not List<object?> items) continue;
            if (items.Any(x => x is StepRef sr && solids.Contains(sr.Id)) && args[2] is StepRef cr && f.Instances.TryGetValue(cr.Id, out var ctx))
                contexts.Add(ctx);
        }
        foreach (var c in sorted) if (c.Is("GLOBAL_UNIT_ASSIGNED_CONTEXT")) contexts.Add(c);
        double Unit(string kind, bool length)
        {
            foreach (var ctx in contexts)
            {
                if (ctx.Part("GLOBAL_UNIT_ASSIGNED_CONTEXT") is not { } ua || ua.Count == 0 || ua[0] is not List<object?> units) continue;
                foreach (var u in units)
                    if (u is StepRef ur && f.Instances.TryGetValue(ur.Id, out var ui) && ui.Is(kind)) return UnitFactor(f, ui, length);
            }
            foreach (var u in sorted)
                if (u.Is(kind)) return UnitFactor(f, u, length);
            return 1;
        }
        return (Unit("LENGTH_UNIT", true), Unit("PLANE_ANGLE_UNIT", false));
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
        /// <summary>B-spline surfaces read as analytic ones whose natural normal is the other way.</summary>
        readonly HashSet<int> _flipped = new();

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
            // the loops, which agree with the neighbouring faces through the shared edges, decide which side is
            // outside; some writers get a face's same_sense flag wrong (seen on rational fillet faces): such a face's
            // outer loop runs clockwise in its parameter plane, so its sense is flipped
            int flipped = 0;
            foreach (var f in body.Faces)
            {
                if (f.Surface.PeriodicU || f.Surface.PeriodicV) continue; // (wrapping faces are oriented after splitting)
                try { if (NDomain.Of(f).SignedArea() < 0) { f.Sense = !f.Sense; flipped++; } }
                catch (InvalidOperationException) { }
            }
            if (flipped > 0) _warn.Add($"{name}: {flipped} face(s) had their orientation flag reversed in the file; corrected from their edges.");
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
        public (Vec3 o, Vec3 z, Vec3 x) Placement(object? a)
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
            if (nf.Surface.Kind == SurfaceKind.BSpline && WholeBSpline(nf) is { } pieces) return pieces;
            throw new NotSupportedException($"closed {nf.Surface.Kind.ToString().ToLowerInvariant()} faces without edges");
        }

        /// <summary>
        /// A closed B-spline face without edges: closed round with a pole at each end (sphere-like) becomes two halves
        /// between iso-curves from pole to pole; closed both ways (torus-like) gets the two seam iso-curves.
        /// Null for other shapes.
        /// </summary>
        IEnumerable<NFace>? WholeBSpline(NFace f)
        {
            var s = f.Surface; var nb = s.Nurbs!;
            NFace Oriented(Vec3 anchor, params (NEdge e, bool fwd)[] cs)
            {
                var h = new NFace(s, f.Sense, f.Tag, anchor);
                var l = new NLoop();
                foreach (var (e, fwd) in cs) l.Coedges.Add(new NCoedge(e, fwd));
                h.Loops.Add(l);
                if (NDomain.Of(h).SignedArea() < 0)
                {
                    l.Coedges.Reverse();
                    for (int i = 0; i < l.Coedges.Count; i++) l.Coedges[i] = new NCoedge(l.Coedges[i].Edge, !l.Coedges[i].Forward);
                }
                return h;
            }
            if (nb.ClosedU && !nb.ClosedV && nb.Poles.Count == 2)
            {
                double v0 = nb.V0, v1 = nb.V1, vm = 0.5 * (v0 + v1);
                var south = new NVertex(s.Eval(0, v0)); var north = new NVertex(s.Eval(0, v1));
                NEdge Meridian(double u) => new(new NIso(s, 0, u, 1), v0, v1, south, north);
                var m0 = Meridian(0); var m1 = Meridian(Math.PI);
                return new[]
                {
                    Oriented(s.Eval(0.5 * Math.PI, vm), (m0, true), (m1, false)),
                    Oriented(s.Eval(1.5 * Math.PI, vm), (m1, true), (m0, false)),
                };
            }
            if (nb.ClosedU && nb.ClosedV)
            {
                var V = new NVertex(s.Eval(0, 0));
                var ru = new NVertex(s.Eval(Math.PI, 0)); var tv = new NVertex(s.Eval(0, Math.PI));
                NEdge Iso(int axis, double t0, double t1, NVertex a, NVertex b) => new(new NIso(s, axis, 0, 1), t0, t1, a, b);
                var r1 = Iso(1, 0, Math.PI, V, ru); var r2 = Iso(1, Math.PI, 2 * Math.PI, ru, V);
                var t1 = Iso(0, 0, Math.PI, V, tv); var t2 = Iso(0, Math.PI, 2 * Math.PI, tv, V);
                return new[] { Oriented(s.Eval(Math.PI, Math.PI), (r1, true), (r2, true), (t1, true), (t2, true), (r2, false), (r1, false), (t2, false), (t1, false)) };
            }
            return null;
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
            var sense = face.Args[3] is StepEnum { Value: "T" } ^ _flipped.Contains(_f.Get(face.Args[2]).Id);
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
            if (res == null && (s.Is("B_SPLINE_SURFACE_WITH_KNOTS") || s.Is("B_SPLINE_SURFACE")))
            {
                // planes and cylinders written as B-splines are read as what they are
                var nb = SplineSurface(s);
                bool flip = false;
                res = RecognizeAnalytic ? NRecognize.Surface(nb, 1e-7 * Math.Max(1, nb.Box.Diagonal), out flip) : null;
                if (res != null && flip) _flipped.Add(s.Id);
                res ??= NSurface.BSpline(nb);
            }
            if (res == null && s.Type == "OFFSET_SURFACE")
            {
                // OFFSET_SURFACE(name, basis, distance, self_intersect): along the basis' normal
                var basis = Surface(s.Args[1]);
                res = NShell.OffsetSurface(basis, D(s.Args[2]) * _s);
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
            _ => type.StartsWith("(", StringComparison.Ordinal) ? "free-form (B-spline) surfaces" : type.ToLowerInvariant().Replace('_', ' '),
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
                case "HYPERBOLA":
                    {
                        var (o, z, x) = Placement(c.Args[1]);
                        var a = D(c.Args[2]) * _s; var b = D(c.Args[3]) * _s;
                        var y = Vec3.Cross(z, x);
                        return new NHyperbola(o, x, same ? y : -y, a, b);
                    }
                case "PARABOLA":
                    {
                        var (o, z, x) = Placement(c.Args[1]);
                        var f = D(c.Args[2]) * _s;
                        var y = Vec3.Cross(z, x);
                        return new NParabola(o, x, same ? y : -y, f);
                    }
            }
            if (c.Is("B_SPLINE_CURVE_WITH_KNOTS") || c.Is("B_SPLINE_CURVE")) return Spline(c, same);
            throw new NotSupportedException(c.Type.ToLowerInvariant().Replace('_', ' ') + " edges");
        }

        /// <summary>
        /// B_SPLINE_SURFACE_WITH_KNOTS(name, u_degree, v_degree, poles, form, u_closed, v_closed, self, u_mults, v_mults,
        /// u_knots, v_knots, spec), or the complex (rational) form with B_SPLINE_SURFACE / B_SPLINE_SURFACE_WITH_KNOTS /
        /// RATIONAL_B_SPLINE_SURFACE parts.
        /// </summary>
        NurbsSurface SplineSurface(StepInstance s)
        {
            List<object?> bs, kn;
            if (s.Parts.Count == 1) { bs = s.Args.Skip(1).Take(7).ToList(); kn = s.Args.Skip(8).ToList(); }
            else { bs = s.Part("B_SPLINE_SURFACE")!; kn = s.Part("B_SPLINE_SURFACE_WITH_KNOTS")!; }
            int pu = (int)D(bs[0]), pv = (int)D(bs[1]);
            var rows = (List<object?>)bs[2];
            int nu = rows.Count, nv = ((List<object?>)rows[0]!).Count;
            var poles = new Vec3[nu, nv];
            for (int i = 0; i < nu; i++)
            {
                var row = (List<object?>)rows[i]!;
                for (int j = 0; j < nv; j++) poles[i, j] = Point(row[j]);
            }
            double[] Knots(List<object?> mults, List<object?> vals)
            {
                var k = new List<double>();
                for (int i = 0; i < mults.Count; i++) for (int m = 0; m < (int)D(mults[i]); m++) k.Add(D(vals[i]));
                return k.ToArray();
            }
            var U = Knots((List<object?>)kn[0]!, (List<object?>)kn[2]!);
            var V = Knots((List<object?>)kn[1]!, (List<object?>)kn[3]!);
            if (U.Length != nu + pu + 1 || V.Length != nv + pv + 1) throw new InvalidDataException($"STEP: B-spline surface #{s.Id} has inconsistent knots.");
            double[,]? w = null;
            if (s.Part("RATIONAL_B_SPLINE_SURFACE") is { } rw)
            {
                var wrows = (List<object?>)rw[0]!;
                w = new double[nu, nv];
                for (int i = 0; i < nu; i++) { var row = (List<object?>)wrows[i]!; for (int j = 0; j < nv; j++) w[i, j] = D(row[j]); }
            }
            return new NurbsSurface(pu, pv, poles, w, U, V);
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
            if (RecognizeAnalytic)
            {
                // lines and circles written as B-splines
                var span = poles.Max(p => Vec3.Distance(p, poles[0]));
                var bc = new BSplineCurve(degree, poles, U);
                var exact = NRecognize.Curve(w == null ? bc.Eval : t => Rational(degree, poles, w, U, t), 1e-7 * Math.Max(1, span));
                if (exact != null) return exact;
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

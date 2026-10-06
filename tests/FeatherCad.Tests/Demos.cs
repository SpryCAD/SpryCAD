using FeatherCad.Core;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary>
/// Parametric demo parts built through the document API (what the UI produces): every dimension is an
/// expression of the part's global variables, so changing an equation regenerates the whole part.
/// <c>-- demos [outDir]</c> writes them as .fcad files.
/// </summary>
static class Demos
{
    public static IEnumerable<(string file, Func<CadDocument> build)> All => new (string, Func<CadDocument>)[]
    {
        ("pipe-flange.fcad", Flange),
        ("enclosure.fcad", Enclosure),
        ("heat-sink.fcad", HeatSink),
        ("stepped-shaft.fcad", Shaft),
        ("l-bracket.fcad", Bracket),
    };

    /// <summary>Equation changes each demo must survive (shows what the parameters do).</summary>
    public static readonly Dictionary<string, (string name, string expr)[][]> Variants = new()
    {
        ["pipe-flange.fcad"] = new[] { new[] { ("N", "12"), ("PCD", "125") }, new[] { ("OD", "180"), ("HubH", "45"), ("N", "6") }, new[] { ("BoltD", "18"), ("Rfil", "8") } },
        ["enclosure.fcad"] = new[] { new[] { ("L", "160"), ("W", "100") }, new[] { ("VentN", "9"), ("VentP", "5") }, new[] { ("H", "60"), ("BossH", "20"), ("Wall", "3") } },
        ["heat-sink.fcad"] = new[] { new[] { ("N", "20"), ("T", "1.2") }, new[] { ("L", "120"), ("FinH", "40") }, new[] { ("N", "8"), ("T", "3") } },
        ["stepped-shaft.fcad"] = new[] { new[] { ("D2", "50"), ("KeyW", "14") }, new[] { ("L2", "100"), ("KeyL", "60") }, new[] { ("D1", "35"), ("Rs", "2"), ("C", "2") } },
        ["l-bracket.fcad"] = new[] { new[] { ("Wb", "90"), ("HoleSp", "60") }, new[] { ("A", "100"), ("Bh", "80"), ("Gs", "40") }, new[] { ("T", "10"), ("Rb", "4") } },
    };

    /// <summary>Rebuilds every demo and its variants; writes the exact STEP of each to <paramref name="dir"/>.</summary>
    public static int Verify(string dir, bool volumes = true)
    {
        Directory.CreateDirectory(dir);
        int bad = 0;
        foreach (var (file, build) in All)
        {
            var doc = build();
            foreach (var variant in new[] { Array.Empty<(string, string)>() }.Concat(Variants[file]))
            {
                var d = CadDocument.FromJson(doc.ToJson());
                foreach (var (n, e) in variant) d.Equations.First(q => q.Name == n).Expr = e;
                Csg.ClearCache();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = new Regenerator().Rebuild(d);
                var ms = sw.Elapsed.TotalMilliseconds;
                var problems = d.Features.Where(f => !r.Status[f.Id].Ok || r.Status[f.Id].Warnings.Count > 0)
                    .Select(f => $"{f.Name}: {r.Status[f.Id].Error}{string.Join(";", r.Status[f.Id].Warnings)}").ToList();
                var body = r.State.Bodies.Count == 1 ? r.State.Bodies[0].Solid : null;
                var ok = problems.Count == 0 && body?.Native != null && body.Topology.OpenHalfEdges == 0;
                var label = variant.Length == 0 ? "as designed" : string.Join(", ", variant.Select(v => $"{v.Item1}={v.Item2}"));
                Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {file,-20} {label,-32} {ms,6:0} ms  vol {(body != null && volumes ? KernelTests.Vex(body).ToString("0") : "-"),8}  {string.Join(" | ", problems)}");
                if (!ok) bad++;
                if (variant.Length == 0 && body != null)
                {
                    using var fs = File.Create(Path.Combine(dir, Path.ChangeExtension(file, ".step")));
                    StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), Path.GetFileNameWithoutExtension(file));
                    if (StepWriter.LastError != null) { Console.WriteLine("     STEP exact export failed: " + StepWriter.LastError.Message); bad++; }
                }
            }
        }
        return bad;
    }

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var (file, build) in All)
        {
            var doc = build();
            File.WriteAllText(Path.Combine(dir, file), doc.ToJson());
            var r = new Regenerator().Rebuild(doc);
            Console.WriteLine($"{file}: {doc.Features.Count} features, {r.Milliseconds:0} ms");
            foreach (var f in doc.Features)
                if (!r.Status[f.Id].Ok || r.Status[f.Id].Warnings.Count > 0)
                    Console.WriteLine($"  {f.Name}: {r.Status[f.Id].Error} {string.Join("; ", r.Status[f.Id].Warnings)}");
            foreach (var b in r.State.Bodies)
                Console.WriteLine($"  body: native={b.Solid.Native != null} open={b.Solid.Topology.OpenHalfEdges} faces: " +
                    string.Join(" ", b.Solid.Native?.Faces.Select(f => f.Tag.Name).Distinct() ?? Array.Empty<string>()));
        }
        return 0;
    }

    // ------------------------------------------------------------------------------------------
    // 1. Pipe flange: revolved weld-neck flange, bolt holes on a pitch circle (count is a variable)

    static CadDocument Flange()
    {
        var d = Doc(("OD", "150"), ("ID", "60"), ("T", "18"), ("HubD", "88"), ("HubH", "32"),
                    ("PCD", "118"), ("BoltD", "14"), ("N", "8"), ("Rfil", "5"), ("C", "1.5"));
        // half profile on the Front plane (XY), revolved about the Y axis
        var s = new Sk(d);
        int a = s.P("ID/2", "0"), b = s.P("OD/2", "0"), c = s.P("OD/2", "T"), e = s.P("HubD/2", "T"),
            f = s.P("HubD/2", "T+HubH"), g = s.P("ID/2", "T+HubH");
        var L = s.Poly(a, b, c, e, f, g); // bottom, rim, flange top, hub, hub top, bore
        s.Centerline(Sketch.OriginId, s.P("0", "T+HubH"));
        var prof = d.AddFeature(new SketchFeature { Name = "Profile", Plane = PlaneRef.Front, Sketch = s.S });
        var rev = d.AddFeature(new RevolveFeature { Name = "Flange", SketchId = prof.Id, Angle = "360" });

        // one bolt hole through the flange plate, patterned around the axis
        var h = new Sk(d);
        h.Circle(h.P("PCD/2", "0"), "BoltD");
        var hs = d.AddFeature(new SketchFeature { Name = "Bolt hole", Plane = PlaneRef.Top, Sketch = h.S });
        var cut = d.AddFeature(new ExtrudeFeature { Name = "Bolt hole cut", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.AddFeature(new CircularPatternFeature { Name = "Bolt circle", Seeds = { cut.Id }, Axis = new DirRef { Axis = "Y" }, Count = "N" });

        // blend the hub into the plate, break the outer and bore edges
        string F(int i) => $"{rev.Id}.R.L{L[i]}";
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature { Name = "Hub fillet", Radius = "Rfil", Edges = { E(F(2), F(3), new Vec3(V("HubD/2"), V("T"), 0)) } });
        d.AddFeature(new ChamferFeature
        {
            Name = "Edge breaks", Distance = "C",
            Edges =
            {
                E(F(1), F(2), new Vec3(V("OD/2"), V("T"), 0)), E(F(0), F(1), new Vec3(V("OD/2"), 0, 0)),
                E(F(3), F(4), new Vec3(V("HubD/2"), V("T+HubH"), 0)),
                E(F(4), F(5), new Vec3(V("ID/2"), V("T+HubH"), 0)), E(F(5), F(0), new Vec3(V("ID/2"), 0, 0)),
            },
        });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 2. Electronics enclosure: rounded box, shell, screw bosses (2D pattern), vent slots (pattern + mirror)

    static CadDocument Enclosure()
    {
        var d = Doc(("L", "120"), ("W", "80"), ("H", "40"), ("Wall", "2.5"), ("Rc", "8"), ("Rrim", "1"),
                    ("BossD", "9"), ("BossH", "12"), ("ScrewD", "3.2"), ("Inset", "10"),
                    ("VentN", "6"), ("VentW", "3"), ("VentH", "18"), ("VentP", "6"));
        var s = new Sk(d);
        var L = s.Rect("-L/2", "-W/2", "L/2", "W/2");
        var sk = d.AddFeature(new SketchFeature { Name = "Footprint", Plane = PlaneRef.Front, Sketch = s.S });
        var box = d.AddFeature(new ExtrudeFeature { Name = "Box", SketchId = sk.Id, Depth = "H" });
        string F(int i) => $"{box.Id}.S.L{L[i]}";
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Corner rounds", Radius = "Rc",
            Edges =
            {
                E(F(0), F(1), new Vec3(V("L/2"), V("-W/2"), V("H/2"))), E(F(1), F(2), new Vec3(V("L/2"), V("W/2"), V("H/2"))),
                E(F(2), F(3), new Vec3(V("-L/2"), V("W/2"), V("H/2"))), E(F(3), F(0), new Vec3(V("-L/2"), V("-W/2"), V("H/2"))),
            },
        });
        d.AddFeature(new ShellFeature { Name = "Shell", Thickness = "Wall", Faces = { new FaceRef { Name = box.Id + ".End", Hint = new Vec3(0, 0, V("H")), Normal = Vec3.UnitZ } } });
        // rounded rim: the outer top edge, tangent-propagated around the corners
        d.AddFeature(new FilletFeature { Name = "Rim round", Radius = "Rrim", Edges = { E(box.Id + ".End", F(0), new Vec3(0, V("-W/2"), V("H"))) } });

        // screw bosses: one on the floor, patterned in X and Y
        var floor = d.AddFeature(new PlaneFeature { Name = "Floor plane", Base = PlaneRef.Front, Offset = "Wall" });
        var b = new Sk(d);
        b.Circle(b.P("-(L/2-Inset)", "-(W/2-Inset)"), "BossD");
        var bs = d.AddFeature(new SketchFeature { Name = "Boss", Plane = new PlaneRef { Feature = floor.Id }, Sketch = b.S });
        var boss = d.AddFeature(new ExtrudeFeature { Name = "Boss", SketchId = bs.Id, Depth = "BossH" });
        d.AddFeature(new LinearPatternFeature
        {
            Name = "Boss pattern", Seeds = { boss.Id },
            Direction = new DirRef { Axis = "X" }, Spacing = "L-2*Inset", Count = "2",
            Direction2 = new DirRef { Axis = "Y" }, Spacing2 = "W-2*Inset", Count2 = "2",
        });
        // screw holes drilled into the boss tops
        var top = d.AddFeature(new PlaneFeature { Name = "Boss top plane", Base = PlaneRef.Front, Offset = "Wall+BossH" });
        var hp = new Sk(d);
        foreach (var (x, y) in new[] { ("-(L/2-Inset)", "-(W/2-Inset)"), ("L/2-Inset", "-(W/2-Inset)"), ("L/2-Inset", "W/2-Inset"), ("-(L/2-Inset)", "W/2-Inset") })
            hp.Point(hp.P(x, y));
        var hs = d.AddFeature(new SketchFeature { Name = "Screw positions", Plane = new PlaneRef { Feature = top.Id }, Sketch = hp.S });
        d.AddFeature(new HoleFeature { Name = "Screw holes", SketchId = hs.Id, Type = HoleType.Simple, Diameter = "ScrewD", ThroughAll = false, Depth = "BossH-1" });

        // vent slots in the right wall (Right plane: sketch X = -Z, sketch Y = Y), patterned and mirrored
        var side = d.AddFeature(new PlaneFeature { Name = "Right wall plane", Base = PlaneRef.Right, Offset = "L/2" });
        var v = new Sk(d);
        v.Rect("-(H/2+VentH/2)", "-((VentN-1)*VentP/2+VentW/2)", "-(H/2-VentH/2)", "-((VentN-1)*VentP/2-VentW/2)");
        var vs = d.AddFeature(new SketchFeature { Name = "Vent slot", Plane = new PlaneRef { Feature = side.Id }, Sketch = v.S });
        var vent = d.AddFeature(new ExtrudeFeature { Name = "Vent", SketchId = vs.Id, IsCut = true, Reverse = true, Depth = "Wall+1" });
        var vp = d.AddFeature(new LinearPatternFeature { Name = "Vent pattern", Seeds = { vent.Id }, Direction = new DirRef { Axis = "Y" }, Spacing = "VentP", Count = "VentN" });
        d.AddFeature(new MirrorFeature { Name = "Vents, left wall", Seeds = { vp.Id }, Plane = PlaneRef.Right });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 3. Heat sink: base plate, N fins (pitch derived from N), counterbored mounting holes

    static CadDocument HeatSink()
    {
        var d = Doc(("L", "80"), ("W", "60"), ("B", "5"), ("FinH", "25"), ("T", "1.6"), ("N", "14"),
                    ("Margin", "8"), ("HoleD", "3.4"), ("CbD", "6.5"), ("CbH", "2.5"), ("Rc", "4"));
        var s = new Sk(d);
        var L = s.Rect("-L/2", "-W/2", "L/2", "W/2");
        var sk = d.AddFeature(new SketchFeature { Name = "Base", Plane = PlaneRef.Front, Sketch = s.S });
        var basePlate = d.AddFeature(new ExtrudeFeature { Name = "Base plate", SketchId = sk.Id, Depth = "B" });
        string F(int i) => $"{basePlate.Id}.S.L{L[i]}";
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Base corners", Radius = "Rc",
            Edges =
            {
                E(F(0), F(1), new Vec3(V("L/2"), V("-W/2"), V("B/2"))), E(F(1), F(2), new Vec3(V("L/2"), V("W/2"), V("B/2"))),
                E(F(2), F(3), new Vec3(V("-L/2"), V("W/2"), V("B/2"))), E(F(3), F(0), new Vec3(V("-L/2"), V("-W/2"), V("B/2"))),
            },
        });
        var top = d.AddFeature(new PlaneFeature { Name = "Base top", Base = PlaneRef.Front, Offset = "B" });
        var f = new Sk(d);
        f.Rect("-L/2", "-(W/2-Margin)", "-(L/2-T)", "W/2-Margin");
        var fs = d.AddFeature(new SketchFeature { Name = "Fin", Plane = new PlaneRef { Feature = top.Id }, Sketch = f.S });
        var fin = d.AddFeature(new ExtrudeFeature { Name = "Fin", SketchId = fs.Id, Depth = "FinH" });
        d.AddFeature(new LinearPatternFeature { Name = "Fins", Seeds = { fin.Id }, Direction = new DirRef { Axis = "X" }, Spacing = "(L-T)/(N-1)", Count = "N" });
        // mounting holes in the margins, counterbored from the top
        var h = new Sk(d);
        foreach (var (x, y) in new[] { ("-(L/2-Margin)", "-(W/2-Margin/2)"), ("L/2-Margin", "-(W/2-Margin/2)"), ("L/2-Margin", "W/2-Margin/2"), ("-(L/2-Margin)", "W/2-Margin/2") })
            h.Point(h.P(x, y));
        var hs = d.AddFeature(new SketchFeature { Name = "Hole positions", Plane = new PlaneRef { Feature = top.Id }, Sketch = h.S });
        d.AddFeature(new HoleFeature { Name = "Mounting holes", SketchId = hs.Id, Type = HoleType.Counterbore, Diameter = "HoleD", CbDiameter = "CbD", CbDepth = "CbH", ThroughAll = true });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 4. Stepped shaft: revolved steps, shoulder fillets, edge chamfers, keyway, cross hole

    static CadDocument Shaft()
    {
        var d = Doc(("D1", "30"), ("L1", "40"), ("D2", "42"), ("L2", "70"), ("D3", "25"), ("L3", "35"),
                    ("C", "1.5"), ("Rs", "1"), ("KeyW", "12"), ("KeyL", "40"), ("KeyT", "5"), ("CrossD", "6"));
        // half profile on the Front plane, revolved about its bottom edge (the X axis)
        var s = new Sk(d);
        int p0 = Sketch.OriginId, p1 = s.P("0", "D1/2"), p2 = s.P("L1", "D1/2"), p3 = s.P("L1", "D2/2"), p4 = s.P("L1+L2", "D2/2"),
            p5 = s.P("L1+L2", "D3/2"), p6 = s.P("L1+L2+L3", "D3/2"), p7 = s.P("L1+L2+L3", "0");
        var L = s.Poly(p0, p1, p2, p3, p4, p5, p6, p7); // left end, D1, shoulder 1, D2, shoulder 2, D3, right end, axis
        var sk = d.AddFeature(new SketchFeature { Name = "Profile", Plane = PlaneRef.Front, Sketch = s.S });
        var rev = d.AddFeature(new RevolveFeature { Name = "Shaft", SketchId = sk.Id, AxisEntity = L[7], Angle = "360" });
        string F(int i) => $"{rev.Id}.R.L{L[i]}";
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Shoulder fillets", Radius = "Rs",
            Edges = { E(F(1), F(2), new Vec3(V("L1"), V("D1/2"), 0)), E(F(4), F(5), new Vec3(V("L1+L2"), V("D3/2"), 0)) },
        });
        d.AddFeature(new ChamferFeature
        {
            Name = "Chamfers", Distance = "C",
            Edges =
            {
                E(F(0), F(1), new Vec3(0, V("D1/2"), 0)), E(F(5), F(6), new Vec3(V("L1+L2+L3"), V("D3/2"), 0)),
                E(F(2), F(3), new Vec3(V("L1"), V("D2/2"), 0)), E(F(3), F(4), new Vec3(V("L1+L2"), V("D2/2"), 0)),
            },
        });
        // keyway on the D2 section: a slot (two arcs) on a plane tangent to the top of the shaft (Top: sketch Y = -Z)
        var kp = d.AddFeature(new PlaneFeature { Name = "Keyway plane", Base = PlaneRef.Top, Offset = "D2/2" });
        var k = new Sk(d);
        int c1 = k.P("L1+L2/2-(KeyL-KeyW)/2", "0"), c2 = k.P("L1+L2/2+(KeyL-KeyW)/2", "0");
        int a1 = k.P("L1+L2/2-(KeyL-KeyW)/2", "KeyW/2"), a2 = k.P("L1+L2/2+(KeyL-KeyW)/2", "KeyW/2");
        int b2 = k.P("L1+L2/2+(KeyL-KeyW)/2", "-KeyW/2"), b1 = k.P("L1+L2/2-(KeyL-KeyW)/2", "-KeyW/2");
        k.Line(b1, b2); k.Arc(c2, b2, a2); k.Line(a2, a1); k.Arc(c1, a1, b1);
        var ks = d.AddFeature(new SketchFeature { Name = "Keyway", Plane = new PlaneRef { Feature = kp.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyway", SketchId = ks.Id, IsCut = true, Reverse = true, Depth = "KeyT" });
        // cross hole through the D1 section
        var x = new Sk(d);
        x.Circle(x.P("L1/2", "0"), "CrossD");
        var xs = d.AddFeature(new SketchFeature { Name = "Cross hole", Plane = PlaneRef.Top, Sketch = x.S });
        d.AddFeature(new ExtrudeFeature { Name = "Cross hole", SketchId = xs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 5. L-bracket: extruded L profile, bend fillets, gusset mirrored to the other side, counterbored holes

    static CadDocument Bracket()
    {
        var d = Doc(("A", "70"), ("Bh", "50"), ("Wb", "60"), ("T", "6"), ("Rb", "6"), ("Gt", "5"), ("Gs", "28"),
                    ("HoleD", "6.6"), ("CbD", "11"), ("CbH", "4"), ("HoleSp", "34"));
        var s = new Sk(d);
        int q0 = Sketch.OriginId, q1 = s.P("A", "0"), q2 = s.P("A", "T"), q3 = s.P("T", "T"), q4 = s.P("T", "Bh"), q5 = s.P("0", "Bh");
        var L = s.Poly(q0, q1, q2, q3, q4, q5); // bottom, leg end, leg top, inner wall, top, back
        var sk = d.AddFeature(new SketchFeature { Name = "L profile", Plane = PlaneRef.Front, Sketch = s.S });
        var ex = d.AddFeature(new ExtrudeFeature { Name = "Bracket", SketchId = sk.Id, End = EndCondition.MidPlane, Depth = "Wb" });
        string F(int i) => $"{ex.Id}.S.L{L[i]}";
        // gusset in the inner corner, flush with the front side, mirrored to the back side
        var gp = d.AddFeature(new PlaneFeature { Name = "Gusset plane", Base = PlaneRef.Front, Offset = "Wb/2-Gt" });
        var g = new Sk(d);
        g.Poly(g.P("T", "T"), g.P("T+Gs", "T"), g.P("T", "T+Gs"));
        var gs = d.AddFeature(new SketchFeature { Name = "Gusset", Plane = new PlaneRef { Feature = gp.Id }, Sketch = g.S });
        var gusset = d.AddFeature(new ExtrudeFeature { Name = "Gusset", SketchId = gs.Id, Depth = "Gt" });
        d.AddFeature(new MirrorFeature { Name = "Gusset mirror", Seeds = { gusset.Id }, Plane = PlaneRef.Front });
        d.AddFeature(new FilletFeature { Name = "Outer bend", Radius = "Rb+T", Edges = { E(F(5), F(0), new Vec3(0, 0, 0)) } });
        // counterbored holes in the base leg (Top plane offset T: sketch Y = -Z) and plain holes in the upright
        var bp = d.AddFeature(new PlaneFeature { Name = "Leg top", Base = PlaneRef.Top, Offset = "T" });
        var h = new Sk(d);
        h.Point(h.P("(A+T)/2+Gs/4", "HoleSp/2")); h.Point(h.P("(A+T)/2+Gs/4", "-HoleSp/2"));
        var hs = d.AddFeature(new SketchFeature { Name = "Base holes", Plane = new PlaneRef { Feature = bp.Id }, Sketch = h.S });
        d.AddFeature(new HoleFeature { Name = "Base holes", SketchId = hs.Id, Type = HoleType.Counterbore, Diameter = "HoleD", CbDiameter = "CbD", CbDepth = "CbH", ThroughAll = true });
        var up = d.AddFeature(new PlaneFeature { Name = "Upright face", Base = PlaneRef.Right, Offset = "T" });
        var u = new Sk(d);
        u.Point(u.P("HoleSp/2", "(Bh+T)/2+Gs/4")); u.Point(u.P("-HoleSp/2", "(Bh+T)/2+Gs/4"));
        var us = d.AddFeature(new SketchFeature { Name = "Upright holes", Plane = new PlaneRef { Feature = up.Id }, Sketch = u.S });
        d.AddFeature(new HoleFeature { Name = "Upright holes", SketchId = us.Id, Type = HoleType.Simple, Diameter = "HoleD", ThroughAll = true });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // helpers

    static EdgeRef E(string a, string b, Vec3 hint) => new() { A = a, B = b, Hint = hint };

    static CadDocument Doc(params (string name, string expr)[] eqs)
    {
        var d = new CadDocument();
        foreach (var (n, e) in eqs) d.Equations.Add(new Equation { Name = n, Expr = e });
        return d;
    }

    /// <summary>Sketch builder: points fully dimensioned from the origin by expressions of the variables.</summary>
    sealed class Sk
    {
        public readonly Sketch S = new();
        readonly Func<string, double?> _lookup;

        public Sk(CadDocument d)
        {
            var (vals, _) = Regenerator.EvaluateEquations(d);
            _lookup = n => vals.TryGetValue(n, out var v) ? v : null;
        }

        public double Eval(string e) => Expr.Eval(e, _lookup);

        /// <summary>Point at (x, y); a leading '-' means the negative side (the dimension is the magnitude).</summary>
        public int P(string x, string y)
        {
            var p = S.AddPoint(new Vec2(Eval(x), Eval(y)));
            Dim(CType.HDistance, x, Sketch.OriginId, p.Id);
            Dim(CType.VDistance, y, Sketch.OriginId, p.Id);
            return p.Id;
        }

        void Dim(CType t, string expr, params int[] refs)
        {
            var neg = expr.StartsWith('-');
            var mag = neg ? expr[1..] : expr;
            var c = S.AddConstraint(t, refs);
            c.Expr = mag; c.Value = Math.Abs(Eval(mag)); c.Sign = neg ? -1 : 1;
        }

        public int Line(int a, int b) => S.Add(new SkLine { P1 = a, P2 = b }).Id;
        public int Centerline(int a, int b) => S.Add(new SkLine { P1 = a, P2 = b, Construction = true }).Id;

        /// <summary>Axis-aligned rectangle from corner expressions; lines: bottom, right, top, left.</summary>
        public List<int> Rect(string x0, string y0, string x1, string y1)
            => Poly(P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1));

        /// <summary>Closed polygon through the points.</summary>
        public List<int> Poly(params int[] pts)
        {
            var ids = new List<int>();
            for (int i = 0; i < pts.Length; i++) ids.Add(Line(pts[i], pts[(i + 1) % pts.Length]));
            return ids;
        }

        public int Circle(int center, string diameter)
        {
            var c = S.Add(new SkCircle { Center = center, R = Eval(diameter) / 2 });
            var k = S.AddConstraint(CType.Diameter, c.Id);
            k.Expr = diameter; k.Value = Eval(diameter);
            return c.Id;
        }

        /// <summary>Arc from start to end (counter-clockwise) about center, all points already dimensioned.</summary>
        public int Arc(int center, int start, int end) => S.Add(new SkArc { Center = center, Start = start, End = end }).Id;

        public void Point(int p) => S.Add(new SkPointEntity { P = p });
    }
}

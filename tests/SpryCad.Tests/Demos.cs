using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Parametric demo parts built through the document API (what the UI produces): every dimension is an
/// expression of the part's global variables, so changing an equation regenerates the whole part.
/// <c>-- demos [outDir]</c> writes them as .sprt files.
/// </summary>
static partial class Demos
{
    public static IEnumerable<(string file, Func<CadDocument> build)> All => new (string, Func<CadDocument>)[]
    {
        ("pipe-flange.sprt", Flange),
        ("enclosure.sprt", Enclosure),
        ("heat-sink.sprt", HeatSink),
        ("stepped-shaft.sprt", Shaft),
        ("l-bracket.sprt", Bracket),
        ("spur-gear.sprt", Gear),
        ("hand-wheel.sprt", HandWheel),
        ("spoked-handwheel.sprt", SpokedWheel),
        ("bent-tube.sprt", BentTube),
        ("bolt-and-nut.sprt", BoltAndNut),
        ("acme-lead-screw.sprt", AcmeLeadScrew),
        ("configurable-gear.sprt", ConfigGear),
        ("socket-screw.sprt", SocketScrew),
    };

    /// <summary>The demo assemblies (written with the parts by <see cref="Write"/>).</summary>
    public static IEnumerable<(string file, Func<SpryCad.Core.Assembly.AssemblyModel> build)> Assemblies => new (string, Func<SpryCad.Core.Assembly.AssemblyModel>)[]
    {
        ("hinge.sasm", Hinge),
        ("lead-screw.sasm", LeadScrewAssembly),
        ("gears.sasm", GearPair),
        ("screw-sizes.sasm", ScrewSizes),
        ("linkage.sasm", Linkage),
    };

    /// <summary>
    /// A hinge in one file (its parts embedded): two of the same leaf (a 40 x 30 x 2 plate with a knuckle along half
    /// of one edge, bored for the pin, and the plate notched along the other half for the other leaf's knuckle), the
    /// second turned half a turn about the vertical line through the middle of the knuckle's axis (its plate on the
    /// other side, its knuckle beside the first), and the pin through both. Printable: Clr between the knuckles,
    /// between each knuckle and the other leaf's notch, and round the pin.
    /// </summary>
    /// <summary>An assembly written as a STEP assembly (each part's solids once, each component placed).</summary>
    public static byte[] AssemblyStep(SpryCad.Core.Assembly.AssemblyModel m, string name)
    {
        // (each configuration of a part as a part of its own)
        var keys = m.Doc.Components.Select(SpryCad.Core.Assembly.AssemblyModel.DisplayKey).Distinct().ToList();
        var parts = keys.Select(k =>
        {
            var bodies = new Regenerator { ThreadOverride = ThreadDisplay.Modeled }.Rebuild(m.Parts[k].Doc!).State.Bodies.Select(b => (b.Id, b.Solid)).ToList();
            var first = m.ComponentsShowing(k).First();
            var config = m.ConfigLabel(first);
            return (first.Name.Split('<')[0] + (config.Length > 0 ? $" ({config})" : ""), (IReadOnlyList<(string, Solid)>)bodies);
        }).ToList();
        var comps = m.Doc.Components.Select(c => (keys.IndexOf(SpryCad.Core.Assembly.AssemblyModel.DisplayKey(c)), c.Name, c.Placement.ToTransform())).ToList();
        using var ms = new MemoryStream();
        StepWriter.WriteAssembly(ms, name, parts, comps);
        return ms.ToArray();
    }

    /// <summary>Parts saved to a temporary folder, inserted embedded by <paramref name="build"/>; the folder deleted after.</summary>
    static SpryCad.Core.Assembly.AssemblyModel Embedded(IEnumerable<(string name, CadDocument doc)> parts,
        Action<SpryCad.Core.Assembly.AssemblyModel, Dictionary<string, string>> build)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "fc-asm-demo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var paths = new Dictionary<string, string>();
            foreach (var (name, doc) in parts) { var pp = Path.Combine(tmp, name + ".sprt"); doc.Save(pp); paths[name] = pp; }
            var m = SpryCad.Core.Assembly.AssemblyModel.New();
            build(m, paths);
            // embedded: nothing points back at the temporary files
            foreach (var comp in m.Doc.Components) { comp.Part.Path = null; comp.Part.AbsolutePath = null; }
            return m;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>A face of a component's part picked by its geometry.</summary>
    static SpryCad.Core.Assembly.MateRef FaceRef(SpryCad.Core.Assembly.AssemblyModel m, SpryCad.Core.Assembly.Component c, Func<FaceRange, bool> which) =>
        m.RefTo(c.Id, m.DisplayOf(c)!.Mesh.Faces.First(which).Name)!;

    /// <summary>
    /// The three-start Acme lead screw and its nut as an assembly: the two parts of acme-lead-screw.sprt each a part
    /// of its own (embedded), modelled about its origin, the nut on the screw where it fits, held by a Screw mate of
    /// the thread's lead (N·P): drag the nut and it turns as it travels.
    /// </summary>
    static SpryCad.Core.Assembly.AssemblyModel LeadScrewAssembly()
    {
        var screw = Doc(("D", "20"), ("P", "4"), ("N", "3"), ("L", "80"), ("J", "12"), ("JL", "20"));
        AcmeScrew(screw);
        var nut = Doc(("D", "20"), ("P", "4"), ("N", "3"), ("S", "30"), ("NutL", "24"), ("Clr", "0.2"));
        AcmeNut(nut, "0");
        screw.Color = new[] { 0.62, 0.64, 0.68 }; nut.Color = new[] { 0.80, 0.62, 0.30 };
        var r = new Regenerator().Rebuild(screw);
        double lead = r.EquationValues["P"] * r.EquationValues["N"], j = r.EquationValues["J"];
        return Embedded(new[] { ("Screw", screw), ("Nut", nut) }, (m, paths) =>
        {
            var s = m.Insert(paths["Screw"], Transform.Identity, embed: true);
            // both parts on their origins: the nut, where it is, is on the screw where it fits
            var n = m.Insert(paths["Nut"], Transform.Identity, embed: true);
            var journal = FaceRef(m, s, f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - j / 2) < 1e-6);
            var bore = FaceRef(m, n, f => f.Surface.Kind == SurfaceKind.Cylinder && f.Surface.Radius < r.EquationValues["D"] / 2);
            m.AddMate(SpryCad.Core.Assembly.MateType.Screw, journal, bore, lead);
        });
    }

    /// <summary>
    /// Two of the spur gear (embedded) meshing: each on an axis along Z (the first on the assembly's, the second the
    /// pitch diameter and 0.1 mm of backlash beside it along X, turned half a tooth to mesh), their faces on the Front plane, and a Gear mate
    /// between their bores (1:1, the other way round): drag either and both turn.
    /// </summary>
    static SpryCad.Core.Assembly.AssemblyModel GearPair()
    {
        var gear = Gear();
        gear.Color = new[] { 0.55, 0.62, 0.72 };
        var r = new Regenerator().Rebuild(gear);
        // the pitch diameter apart plus 0.1 mm of backlash (printable: the teeth do not rub)
        double z = r.EquationValues["z"], centre = 2 * r.EquationValues["r"] + 0.1, bore = r.EquationValues["Bore"];
        return Embedded(new[] { ("Gear", gear) }, (m, paths) =>
        {
            var g1 = m.Insert(paths["Gear"], Transform.Identity, embed: true);
            var g2 = m.Insert(paths["Gear"], Transform.Translation(new Vec3(centre, 0, 0)) * Transform.Rotation(Vec3.Zero, Vec3.UnitZ, Math.PI / z), embed: true);
            g1.Fixed = false;
            bool Bore(FaceRange f) => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - bore / 2) < 1e-6;
            bool Back(FaceRange f) => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z < -0.9 && Math.Abs(f.FirstPlane.Origin.Z) < 1e-6;
            var T = SpryCad.Core.Assembly.MateType.Coincident;
            m.AddMate(T, FaceRef(m, g1, Bore), m.RefTo(null, "Top")!);
            m.AddMate(T, FaceRef(m, g1, Bore), m.RefTo(null, "Right")!);
            m.AddMate(T, FaceRef(m, g1, Back), m.RefTo(null, "Front")!);
            m.AddMate(T, FaceRef(m, g2, Bore), m.RefTo(null, "Top")!);
            m.AddMate(SpryCad.Core.Assembly.MateType.Coincident, FaceRef(m, g2, Bore), m.RefTo(null, "Right")!, centre);
            m.AddMate(T, FaceRef(m, g2, Back), m.RefTo(null, "Front")!);
            m.AddMate(SpryCad.Core.Assembly.MateType.Gear, FaceRef(m, g1, Bore), FaceRef(m, g2, Bore));
        });
    }

    static SpryCad.Core.Assembly.AssemblyModel Hinge()
    {
        // the leaf: the plate on Front (x 0..40, y 0..30, z 0..T), the knuckle and its bore along X on Right
        // (sketch x = -z, y = y) about (y 32, z T/2)
        var leaf = Doc(("L", "40"), ("W", "30"), ("T", "2"), ("KD", "6"), ("PinD", "3"), ("Clr", "0.2"));
        var p = new Sk(leaf);
        p.Rect("0", "0", "L", "W");
        var ps = leaf.AddFeature(new SketchFeature { Name = "Plate", Plane = PlaneRef.Front, Sketch = p.S });
        leaf.AddFeature(new ExtrudeFeature { Name = "Plate", SketchId = ps.Id, Depth = "T" });
        var k = new Sk(leaf);
        k.Circle(k.P("-T/2", "W+KD/2-1"), "KD");
        var ks = leaf.AddFeature(new SketchFeature { Name = "Knuckle", Plane = PlaneRef.Right, Sketch = k.S });
        leaf.AddFeature(new ExtrudeFeature { Name = "Knuckle", SketchId = ks.Id, Depth = "L/2-Clr" });
        // the notch for the other leaf's knuckle (the same circle, from the middle on): Clr clear of it
        var n = new Sk(leaf);
        n.Rect("L/2-Clr", "W-1-Clr", "L+1", "W+1");
        var ns = leaf.AddFeature(new SketchFeature { Name = "Notch", Plane = PlaneRef.Front, Sketch = n.S });
        leaf.AddFeature(new ExtrudeFeature { Name = "Notch", SketchId = ns.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        var b = new Sk(leaf);
        b.Circle(b.P("-T/2", "W+KD/2-1"), "PinD+2*Clr");
        var bs = leaf.AddFeature(new SketchFeature { Name = "Bore", Plane = PlaneRef.Right, Sketch = b.S });
        leaf.AddFeature(new ExtrudeFeature { Name = "Bore", SketchId = bs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        leaf.Color = new[] { 0.80, 0.66, 0.36 };
        // the pin along X
        var pin = Doc(("PinD", "3"), ("PinL", "44"));
        var c = new Sk(pin);
        c.Circle(Sketch.OriginId, "PinD");
        var cs = pin.AddFeature(new SketchFeature { Name = "Pin", Plane = PlaneRef.Right, Sketch = c.S });
        pin.AddFeature(new ExtrudeFeature { Name = "Pin", SketchId = cs.Id, Depth = "PinL" });
        pin.Color = new[] { 0.55, 0.57, 0.62 };
        var tmp = Path.Combine(Path.GetTempPath(), "fc-hinge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            string lp = Path.Combine(tmp, "Leaf.sprt"), pp = Path.Combine(tmp, "Pin.sprt");
            leaf.Save(lp); pin.Save(pp);
            var m = SpryCad.Core.Assembly.AssemblyModel.New();
            double ky = 32, kz = 1;
            var leaf1 = m.Insert(lp, Transform.Identity, embed: true);
            var leaf2 = m.Insert(lp, Transform.Rotation(new Vec3(20, ky, kz), Vec3.UnitZ, Math.PI), embed: true);
            var pinC = m.Insert(pp, Transform.Translation(new Vec3(-2, ky, kz)), embed: true);
            // the mates: the second leaf on the first's knuckle axis, its knuckle Clr*2 from the first's (it swings
            // about the axis: drag it); the pin in the bore, sticking out 2 mm past the first knuckle's start
            string Face(SpryCad.Core.Assembly.Component c, Func<FaceRange, bool> which) => m.DisplayOf(c)!.Mesh.Faces.First(which).Name;
            bool Bore(FaceRange f) => f.Surface.Kind == SurfaceKind.Cylinder && f.Surface.Radius < 2;
            bool KnuckleEnd(FaceRange f) => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.X > 0.9 && Math.Abs(f.FirstPlane.Origin.X - 19.8) < 1e-6;
            bool KnuckleStart(FaceRange f) => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.X < -0.9 && Math.Abs(f.FirstPlane.Origin.X) < 1e-6;
            bool PinSide(FaceRange f) => f.Surface.Kind == SurfaceKind.Cylinder;
            bool PinStart(FaceRange f) => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.X < -0.9;
            SpryCad.Core.Assembly.MateRef R(SpryCad.Core.Assembly.Component c, Func<FaceRange, bool> which) => m.RefTo(c.Id, Face(c, which))!;
            m.AddMate(SpryCad.Core.Assembly.MateType.Concentric, R(leaf2, Bore), R(leaf1, Bore));
            m.AddMate(SpryCad.Core.Assembly.MateType.Coincident, R(leaf2, KnuckleEnd), R(leaf1, KnuckleEnd), 0.4);
            m.AddMate(SpryCad.Core.Assembly.MateType.Concentric, R(pinC, PinSide), R(leaf1, Bore));
            m.AddMate(SpryCad.Core.Assembly.MateType.Coincident, R(pinC, PinStart), R(leaf1, KnuckleStart), 2);
            // embedded: nothing points back at the temporary files
            foreach (var comp in m.Doc.Components) { comp.Part.Path = null; comp.Part.AbsolutePath = null; }
            return m;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Equation changes each demo must survive (shows what the parameters do).</summary>
    public static readonly Dictionary<string, (string name, string expr)[][]> Variants = new()
    {
        ["pipe-flange.sprt"] = new[] { new[] { ("N", "12"), ("PCD", "125") }, new[] { ("OD", "180"), ("HubH", "45"), ("N", "6") }, new[] { ("BoltD", "18"), ("Rfil", "8") } },
        ["enclosure.sprt"] = new[] { new[] { ("L", "160"), ("W", "100") }, new[] { ("VentN", "9"), ("VentP", "5") }, new[] { ("H", "60"), ("BossH", "20"), ("Wall", "3") } },
        ["heat-sink.sprt"] = new[] { new[] { ("N", "20"), ("T", "1.2") }, new[] { ("L", "120"), ("FinH", "40") }, new[] { ("N", "8"), ("T", "3") } },
        ["stepped-shaft.sprt"] = new[] { new[] { ("D2", "50"), ("KeyW", "14") }, new[] { ("L2", "100"), ("KeyL", "60") }, new[] { ("D1", "35"), ("Rs", "2"), ("C", "2") } },
        ["hand-wheel.sprt"] = new[] { new[] { ("NS", "3") }, new[] { ("Dish", "45"), ("SpokeD", "16") }, new[] { ("D", "260"), ("NS", "6"), ("KnobL", "60") } },
        ["bolt-and-nut.sprt"] = new[] { new[] { ("D", "12"), ("P", "1.75"), ("S", "19") }, new[] { ("L", "45"), ("Clr", "0.3") }, new[] { ("NutH", "10"), ("Clr", "0") } },
        ["acme-lead-screw.sprt"] = new[] { new[] { ("N", "2") }, new[] { ("N", "2"), ("L", "60") }, new[] { ("D", "16"), ("P", "3"), ("N", "4") } },
        ["bent-tube.sprt"] = new[] { new[] { ("R", "80"), ("L1", "120") }, new[] { ("OD", "40"), ("ID", "34"), ("FlangeD", "100"), ("PCD", "75") }, new[] { ("N", "6"), ("L2", "100") } },
        ["spoked-handwheel.sprt"] = new[] { new[] { ("NS", "6") }, new[] { ("D", "300"), ("RimT", "26"), ("NG", "4") }, new[] { ("NS", "4"), ("SpokeD", "20"), ("Rf", "3") } },
        ["spur-gear.sprt"] = new[] { new[] { ("z", "48") }, new[] { ("m", "2.5"), ("z", "30") }, new[] { ("PA", "25"), ("b", "20") } },
        // small: solid (the web recesses and lightening holes suppressed); large: both
        ["configurable-gear.sprt"] = new[] { new[] { ("z", "20") }, new[] { ("z", "26") }, new[] { ("z", "60") } },
        ["socket-screw.sprt"] = new[] { new[] { ("L", "60") }, new[] { ("Threaded", "0") } },
        ["l-bracket.sprt"] = new[] { new[] { ("Wb", "90"), ("HoleSp", "60") }, new[] { ("A", "100"), ("Bh", "80"), ("Gs", "40") }, new[] { ("T", "10"), ("Rb", "4") } },
    };

    /// <summary>Rebuilds every demo and its variants; writes the exact STEP of each to <paramref name="dir"/>.</summary>
    public static int Verify(string dir, bool volumes = true, string? filter = null)
    {
        Directory.CreateDirectory(dir);
        int bad = 0;
        foreach (var (file, build) in All.Where(x => filter == null || x.file.Contains(filter)))
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
                // demo sketches are fully defined (black), by relations and dimensions
                foreach (var sf in d.Features.OfType<SketchFeature>())
                    if (r.State.Sketches.GetValueOrDefault(sf.Id)?.Solve is { } sv && sv.Status != SketchStatus.FullyDefined)
                        problems.Add($"{sf.Name}: sketch {sv.Status}");
                var body = r.State.Bodies.Count >= 1 ? r.State.Bodies[0].Solid : null;
                var ok = problems.Count == 0 && r.State.Bodies.Count >= 1 && r.State.Bodies.All(bb => bb.Solid.Native != null && bb.Solid.Topology.OpenHalfEdges == 0);
                var label = variant.Length == 0 ? "as designed" : string.Join(", ", variant.Select(v => $"{v.Item1}={v.Item2}"));
                Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {file,-20} {label,-32} {ms,6:0} ms  vol {(body != null && volumes ? KernelTests.Vex(body).ToString("0") : "-"),8}  {string.Join(" | ", problems)}");
                if (!ok)
                {
                    bad++;
                    foreach (var bb in r.State.Bodies)
                        Console.WriteLine($"     body {bb.Id}: native={bb.Solid.Native != null} open={bb.Solid.Topology.OpenHalfEdges} ({Csg.LastFallbackReason})");
                }
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

    /// <summary><c>-- demo-sketches name</c>: solve status of each sketch of one demo (debugging).</summary>
    public static int SketchReport(string name)
    {
        var doc = All.First(a => a.file.StartsWith(name)).build();
        var r = new Regenerator().Rebuild(doc);
        foreach (var f in doc.Features.OfType<SketchFeature>())
        {
            var sr = r.State.Sketches.GetValueOrDefault(f.Id);
            var sv = sr?.Solve;
            Console.WriteLine($"{f.Name}: {(sv == null ? "no result" : $"status={sv.Status} converged={sv.Converged} residual={sv.Residual:g3} free points={sv.FreePoints.Count}")} {r.Status[f.Id].Error}");
            if (sv != null && f.Sketch.Constraints.Count < 200)
            {
                var dofs = SketchSolver.Analyze(sr!.Sketch);
                Console.WriteLine($"   analyze: {dofs.Status}, free points {string.Join(",", dofs.FreePoints)}");
                var sk = sr.Sketch;
                foreach (var c in sk.Constraints.Where(c => c.IsDimension))
                {
                    var m = SketchSolver.Measure(sk, c);
                    if (Math.Abs(m - c.Value) > 1e-6) Console.WriteLine($"   dim {c.Type} {string.Join(",", c.Refs)} target {c.Value:0.######} measured {m:0.######} expr {c.Expr}");
                }
                foreach (var c in sk.Constraints.Where(c => c.Type == CType.PointOnCircle))
                {
                    if (sk.Entity(c.Refs[1]) is not SkArc a) continue;
                    var p = sk.P(c.Refs[0]);
                    var dev = (p - sk.P(a.Center)).Length - (sk.P(a.Start) - sk.P(a.Center)).Length;
                    if (Math.Abs(dev) > 1e-6) Console.WriteLine($"   on-arc {c.Refs[0]} -> arc {c.Refs[1]} deviation {dev:g3}");
                }
                foreach (var a in sk.Entities.OfType<SkArc>())
                {
                    var dev = (sk.P(a.End) - sk.P(a.Center)).Length - (sk.P(a.Start) - sk.P(a.Center)).Length;
                    if (Math.Abs(dev) > 1e-6) Console.WriteLine($"   arc {a.Id} end/start radius mismatch {dev:g3}");
                }
            }
        }
        for (int i = 1; i <= doc.Features.Count; i++)
        {
            var ri = new Regenerator().Rebuild(doc, i);
            Console.WriteLine($"  after {doc.Features[i - 1].Name}: " + string.Join(" ", ri.State.Bodies.Select(bb => $"{bb.Id}:open={bb.Solid.Topology.OpenHalfEdges}")));
            foreach (var bb in ri.State.Bodies.Where(bb => bb.Solid.Topology.OpenHalfEdges > 0))
            {
                var topo = bb.Solid.Topology;
                var owner = new HashSet<(int, int)>();
                foreach (var fl in topo.FaceLoops) foreach (var l in fl) for (int j = 0; j < l.Length; j++) owner.Add((l[j], l[(j + 1) % l.Length]));
                var counts = new Dictionary<string, int>();
                for (int fi = 0; fi < topo.FaceLoops.Count; fi++)
                    foreach (var l in topo.FaceLoops[fi])
                        for (int j = 0; j < l.Length; j++)
                            if (!owner.Contains((l[(j + 1) % l.Length], l[j])))
                            {
                                var n = bb.Solid.Faces[fi].Tag.Name;
                                counts[n] = counts.GetValueOrDefault(n) + 1;
                                if (counts[n] <= 3) Console.WriteLine($"     open {n}: {topo.Vertices[l[j]]} -> {topo.Vertices[l[(j + 1) % l.Length]]}");
                            }
                Console.WriteLine("     open by face: " + string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
                // facet orientation vs the exact outward normal, per native face
                var nb = bb.Solid.Native!;
                foreach (var nf in nb.Faces.Where(x => counts.ContainsKey(x.Tag.Name)))
                {
                    var one = new SpryCad.Kernel.Native.NBody(); one.Faces.Add(nf);
                    var facets = SpryCad.Kernel.Native.NTess.Facets(one);
                    int good = 0, flipped = 0;
                    foreach (var fc in facets)
                    {
                        var c = fc.Centroid();
                        var n = nf.Surface.Normal(nf.Surface.Project(c)) * (nf.Sense ? 1 : -1);
                        if (Vec3.Dot(n, fc.Plane.Normal) > 0) good++; else flipped++;
                    }
                    var dom = SpryCad.Kernel.Native.NDomain.Of(nf, 0.02);
                    Console.WriteLine($"     face {nf.Tag.Name} loops={nf.Loops.Count} facets ok={good} flipped={flipped} uv=[{dom.UMin:0.###},{dom.UMax:0.###}]x[{dom.VMin:0.###},{dom.VMax:0.###}] loop areas={string.Join(",", dom.Loops.Select(l => { double a = 0; for (int q = 0, w = l.Length - 1; q < l.Length; w = q++) a += l[w].X * l[q].Y - l[q].X * l[w].Y; return (a / 2).ToString("0.####"); }))}");
                }
                break;
            }
            if (ri.State.Bodies.Any(bb => bb.Solid.Topology.OpenHalfEdges > 0)) break;
        }
        if (name.StartsWith("spur")) Console.WriteLine($"involute flank deviation: {FlankDeviation(doc, r):0.######} mm");
        return 0;
    }

    /// <summary>Largest distance from the exact involute (sampled densely) to the nearest flank arc of the gap sketch.</summary>
    public static double FlankDeviation(CadDocument doc, RegenResult r)
    {
        var (v, _) = Regenerator.EvaluateEquations(doc);
        double rb = v["rb"], rs = v["rs"], re = v["re"], z = v["z"], inva = v["inva"];
        var gap = doc.Features.OfType<SketchFeature>().First(f => f.Name == "Tooth gap");
        var sk = r.State.Sketches[gap.Id].Sketch;
        var arcs = sk.Entities.OfType<SkArc>().Where(a => a.Center != Sketch.OriginId).ToList();
        double worst = 0;
        for (int i = 0; i <= 400; i++)
        {
            var rho = rs + (re - rs) * i / 400.0;
            var a = Math.Acos(rb / rho);
            var phi = Math.PI / (2 * z) - inva + (Math.Tan(a) - a);
            foreach (var sgn in new[] { 1, -1 })
            {
                var p = new Vec2(rho * Math.Cos(phi), sgn * rho * Math.Sin(phi));
                worst = Math.Max(worst, arcs.Min(ar => Math.Abs((p - sk.P(ar.Center)).Length - (sk.P(ar.Start) - sk.P(ar.Center)).Length)));
            }
        }
        return worst;
    }

    /// <summary>
    /// <c>-- containerize &lt;file or folder&gt;...</c>: rebuilds parts and saves each in place as a container with
    /// its preview image (the content is unchanged). Folders: their *.sprt files, not subfolders.
    /// </summary>
    public static int Containerize(IEnumerable<string> paths)
    {
        int bad = 0;
        foreach (var path in paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.sprt").Order() : new[] { p }.AsEnumerable()))
        {
            var doc = CadDocument.Load(path);
            var r = new Regenerator().Rebuild(doc);
            doc.Thumbnail = DocumentContainer.RenderThumbnail(doc, r) ?? doc.Thumbnail;
            doc.Save(path, container: true);
            Console.WriteLine($"{path}: container{(doc.Thumbnail != null ? " with preview" : ", NO PREVIEW (nothing solid)")}");
            if (doc.Thumbnail == null) bad++;
        }
        return bad == 0 ? 0 : 1;
    }

    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var broken = new List<string>();
        foreach (var (file, build) in All)
        {
            var doc = build();
            var r = new Regenerator().Rebuild(doc);
            // a container with its preview, as the app saves parts (Explorer shows it as the thumbnail)
            doc.Thumbnail = DocumentContainer.RenderThumbnail(doc, r);
            doc.Save(Path.Combine(dir, file), container: true);
            Console.WriteLine($"{file}: {doc.Features.Count} features, {r.Milliseconds:0} ms");
            foreach (var f in doc.Features)
                if (!r.Status[f.Id].Ok || r.Status[f.Id].Warnings.Count > 0)
                    Console.WriteLine($"  {f.Name}: {r.Status[f.Id].Error} {string.Join("; ", r.Status[f.Id].Warnings)}");
            if (doc.Features.Any(f => !r.Status[f.Id].Ok)) broken.Add(file);
            foreach (var b in r.State.Bodies)
                Console.WriteLine($"  body: native={b.Solid.Native != null} open={b.Solid.Topology.OpenHalfEdges} faces: " +
                    string.Join(" ", b.Solid.Native?.Faces.Select(f => f.Tag.Name).Distinct() ?? Array.Empty<string>()));
        }
        foreach (var (file, build) in Assemblies)
        {
            var m = build();
            m.Save(Path.Combine(dir, file));
            // and as a STEP assembly (for other CAD systems, and to try opening one)
            File.WriteAllBytes(Path.Combine(dir, Path.ChangeExtension(file, ".step")), AssemblyStep(m, Path.GetFileNameWithoutExtension(file)));
            Console.WriteLine($"{file}: {m.Doc.Components.Count} components ({string.Join(", ", m.Doc.Components.Select(c => c.Name))})");
            if (m.Parts.Values.Any(p => p.Problem != null || p.Mesh.Indices.Length == 0)) broken.Add(file);
        }
        // a failed feature fails the run, so a broken demo can't ship in the installer (build/make-installer.ps1)
        if (broken.Count == 0) return 0;
        Console.Error.WriteLine($"Broken demos (a feature failed to rebuild): {string.Join(", ", broken)}");
        return 1;
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
    // 6. Spur gear with involute teeth: module, tooth count and pressure angle drive everything

    /// <summary>Largest distance between the arc-approximated flanks and the true involute (last built gear).</summary>
    public static double GearFlankError;

    static CadDocument Gear()
    {
        var d = Doc(("m", "2"), ("z", "36"), ("PA", "20"), ("b", "14"), ("Bore", "14"), ("KeyW", "5"), ("KeyT", "2.3"),
                    ("Web", "6"), ("NLH", "6"),
                    // derived geometry (standard full-depth teeth)
                    ("r", "m*z/2"), ("rb", "r*cos(PA)"), ("ra", "r+m"), ("rf", "r-1.25*m"), ("inva", "tan(PA)-PA*pi/180"),
                    ("rs", "max(rb, rf+0.1*m)"), ("re", "ra+0.3*m"),
                    ("HubD", "2.2*Bore"), ("RimD", "2*rf-4*m"), ("Pocket", "(b-Web)/2"),
                    ("LH", "0.7*(RimD-HubD)/2"), ("LHR", "(RimD+HubD)/4"));

        // blank with bore
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "2*ra");
        s.Circle(Sketch.OriginId, "Bore");
        var bs = d.AddFeature(new SketchFeature { Name = "Blank", Plane = PlaneRef.Front, Sketch = s.S });
        var blank = d.AddFeature(new ExtrudeFeature { Name = "Blank", SketchId = bs.Id, Depth = "b" });

        // one tooth gap, centred on +X: root arc, radial lines below the base circle, involute flanks (three arcs
        // each through exact involute points), closed outside the tip circle
        var g = new Sk(d);
        // polar angle (degrees) of the upper flank at radius rho: half gap at the base circle + involute function
        static string Phi(string rho) => $"(90/z - inva*180/pi + (tan(acos(rb/({rho}))) - acos(rb/({rho}))*pi/180)*180/pi)";
        static string Rho(double frac) => $"(rs+(re-rs)*{frac * frac:0.######})"; // denser near the base circle
        string X(string rho) => $"{rho}*cos({Phi(rho)})";
        string Y(string rho) => $"{rho}*sin({Phi(rho)})";
        int[] Flank(bool upper)
        {
            var pts = new List<int>();
            // the lower flank's outer end only gets its height: the closing arc (centred on the axis, through the
            // upper end) fixes its radius, a second full dimension would be redundant
            for (int k = 0; k <= 3; k++) pts.Add(g.P(X(Rho(k / 3.0)), (upper ? "" : "-") + "(" + Y(Rho(k / 3.0)) + ")", dimX: upper || k < 3));
            for (int k = 0; k < 3; k++)
            {
                // the arc through flank points k and k+1 also passes through the involute point between them
                var mid = g.P(X(Rho((k + 0.5) / 3.0)), (upper ? "" : "-") + "(" + Y(Rho((k + 0.5) / 3.0)) + ")");
                g.Point(mid);
                g.ArcThrough(pts[k], mid, pts[k + 1]);
            }
            return pts.ToArray();
        }
        var up = Flank(true); var lo = Flank(false);
        var rootU = g.P($"rf*cos({Phi("rs")})", $"rf*sin({Phi("rs")})");
        var rootL = g.P($"rf*cos({Phi("rs")})", $"-(rf*sin({Phi("rs")}))", dimX: false); // on the root arc
        g.Arc(Sketch.OriginId, rootL, rootU);
        g.Line(rootU, up[0]); g.Line(rootL, lo[0]);
        g.Arc(Sketch.OriginId, lo[3], up[3]);
        var gs = d.AddFeature(new SketchFeature { Name = "Tooth gap", Plane = PlaneRef.Front, Sketch = g.S });
        var gap = d.AddFeature(new ExtrudeFeature { Name = "Tooth gap", SketchId = gs.Id, IsCut = true, End = EndCondition.ThroughAll });
        d.AddFeature(new CircularPatternFeature { Name = "Teeth", Seeds = { gap.Id }, Axis = new DirRef { Axis = "Z" }, Count = "z" });

        // keyway in the bore
        var k = new Sk(d);
        k.Rect("0", "-KeyW/2", "Bore/2+KeyT", "KeyW/2");
        var ks = d.AddFeature(new SketchFeature { Name = "Keyway", Plane = PlaneRef.Front, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyway", SketchId = ks.Id, IsCut = true, End = EndCondition.ThroughAll });

        // web: recessed on both faces between hub and rim, with lightening holes
        var w = new Sk(d);
        w.Circle(Sketch.OriginId, "RimD"); w.Circle(Sketch.OriginId, "HubD");
        var ws = d.AddFeature(new SketchFeature { Name = "Web recess", Plane = PlaneRef.Front, Sketch = w.S });
        var rec = d.AddFeature(new ExtrudeFeature { Name = "Web recess", SketchId = ws.Id, IsCut = true, Depth = "Pocket" });
        var back = d.AddFeature(new PlaneFeature { Name = "Back face", Base = PlaneRef.Front, Offset = "b" });
        var w2 = new Sk(d);
        w2.Circle(Sketch.OriginId, "RimD"); w2.Circle(Sketch.OriginId, "HubD");
        var ws2 = d.AddFeature(new SketchFeature { Name = "Web recess (back)", Plane = new PlaneRef { Feature = back.Id }, Sketch = w2.S });
        d.AddFeature(new ExtrudeFeature { Name = "Web recess (back)", SketchId = ws2.Id, IsCut = true, Reverse = true, Depth = "Pocket" });
        var l = new Sk(d);
        l.Circle(l.P("LHR", "0"), "LH");
        var ls = d.AddFeature(new SketchFeature { Name = "Lightening hole", Plane = PlaneRef.Front, Sketch = l.S });
        var lh = d.AddFeature(new ExtrudeFeature { Name = "Lightening hole", SketchId = ls.Id, IsCut = true, End = EndCondition.ThroughAll });
        d.AddFeature(new CircularPatternFeature { Name = "Lightening holes", Seeds = { lh.Id }, Axis = new DirRef { Axis = "Z" }, Count = "NLH" });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 7. Cast-iron hand wheel: round rim, dished (raised) hub, slanted spokes on planes normal to their
    //    centrelines, keyed bore, and a spinner knob on the rim (separate body: it turns on its pin)

    static CadDocument HandWheel()
    {
        var d = Doc(("D", "200"), ("RimT", "18"), ("HubD", "46"), ("HubH", "40"), ("Bore", "16"), ("KeyW", "5"), ("KeyT", "2.3"),
                    ("Dish", "30"), ("NS", "5"), ("SpokeD", "13"), ("C", "1.5"),
                    ("KnobNeck", "6"), ("KnobR", "11"), ("KnobL", "45"), ("PinD", "8"),
                    // spoke centreline: from inside the hub to the rim centre
                    ("SpokeIn", "HubD/2*0.5"), ("SpokeL", "sqrt((D/2-SpokeIn)^2+Dish^2)"));

        // rim: a circle revolved about the wheel axis (Y)
        var rs = new Sk(d);
        rs.Circle(rs.P("D/2", "0"), "RimT");
        rs.Centerline(Sketch.OriginId, rs.P("0", "Dish"));
        var rimSk = d.AddFeature(new SketchFeature { Name = "Rim section", Plane = PlaneRef.Front, Sketch = rs.S });
        d.AddFeature(new RevolveFeature { Name = "Rim", SketchId = rimSk.Id, Angle = "360" });

        // hub: raised by Dish above the rim plane (the dish), bored
        var hs = new Sk(d);
        var hl = hs.Poly(hs.P("Bore/2", "Dish-HubH/2"), hs.P("HubD/2", "Dish-HubH/2"), hs.P("HubD/2", "Dish+HubH/2"), hs.P("Bore/2", "Dish+HubH/2"));
        hs.Centerline(Sketch.OriginId, hs.P("0", "Dish+HubH/2"));
        var hubSk = d.AddFeature(new SketchFeature { Name = "Hub section", Plane = PlaneRef.Front, Sketch = hs.S });
        var hub = d.AddFeature(new RevolveFeature { Name = "Hub", SketchId = hubSk.Id, Angle = "360" });

        // one spoke: centreline sketch, plane normal to it at the hub end, circle, extrude along the line
        var cl = new Sk(d);
        int a = cl.P("SpokeIn", "Dish"), b = cl.P("D/2", "0");
        var line = cl.Centerline(a, b);
        var clSk = d.AddFeature(new SketchFeature { Name = "Spoke centreline", Plane = PlaneRef.Front, Sketch = cl.S });
        var sp = d.AddFeature(new PlaneFeature
        {
            Name = "Spoke plane", Mode = PlaneMode.NormalToCurve,
            Curve = new CurveRef { Sketch = clSk.Id, SketchEntity = line }, Point = new PointRef { Sketch = clSk.Id, SketchPoint = a },
        });
        var sc = new Sk(d);
        sc.Circle(Sketch.OriginId, "SpokeD");
        var scSk = d.AddFeature(new SketchFeature { Name = "Spoke section", Plane = new PlaneRef { Feature = sp.Id }, Sketch = sc.S });
        var spoke = d.AddFeature(new ExtrudeFeature { Name = "Spoke", SketchId = scSk.Id, Depth = "SpokeL" });
        d.AddFeature(new CircularPatternFeature { Name = "Spokes", Seeds = { spoke.Id }, Axis = new DirRef { Axis = "Y" }, Count = "NS" });

        // hub edges broken
        string H(int i) => $"{hub.Id}.R.L{hl[i]}";
        double V(string e) => hs.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Hub chamfers", Distance = "C",
            Edges =
            {
                E(H(1), H(2), new Vec3(V("HubD/2"), V("Dish+HubH/2"), 0)), E(H(0), H(1), new Vec3(V("HubD/2"), V("Dish-HubH/2"), 0)),
                E(H(2), H(3), new Vec3(V("Bore/2"), V("Dish+HubH/2"), 0)), E(H(3), H(0), new Vec3(V("Bore/2"), V("Dish-HubH/2"), 0)),
            },
        });
        // keyway along the bore (Top plane: sketch Y = -Z)
        var k = new Sk(d);
        k.Rect("0", "-KeyW/2", "Bore/2+KeyT", "KeyW/2");
        var ks = d.AddFeature(new SketchFeature { Name = "Keyway", Plane = PlaneRef.Top, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyway", SketchId = ks.Id, IsCut = true, End = EndCondition.ThroughAllBoth });

        // spinner knob on the operator's side (away from the dished hub, i.e. towards -Y): pin into the rim,
        // collar, grip with a domed end, revolved about its own axis
        var kn = new Sk(d);
        int k0 = kn.P("D/2", "0"), k1 = kn.P("D/2+PinD/2", "0"), k2 = kn.P("D/2+PinD/2", "-(RimT/2+2)"),
            k3 = kn.P("D/2+KnobNeck", "-(RimT/2+2)"), k4 = kn.P("D/2+KnobNeck", "-(RimT/2+5)"), k5 = kn.P("D/2+KnobR", "-(RimT/2+8)"),
            k6 = kn.P("D/2+KnobR", "-(RimT/2+KnobL-KnobR)"), kc = kn.P("D/2", "-(RimT/2+KnobL-KnobR)"), k7 = kn.P("D/2", "-(RimT/2+KnobL)");
        kn.Line(k0, k1); kn.Line(k1, k2); kn.Line(k2, k3); kn.Line(k3, k4); kn.Line(k4, k5); kn.Line(k5, k6);
        kn.Arc(kc, k7, k6); // dome: counter-clockwise from the tip round to the side
        var axisLine = kn.Line(k7, k0);
        var knSk = d.AddFeature(new SketchFeature { Name = "Knob profile", Plane = PlaneRef.Front, Sketch = kn.S });
        d.AddFeature(new RevolveFeature { Name = "Spinner knob", SketchId = knSk.Id, AxisEntity = axisLine, Angle = "360", Merge = false });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // 8. Spoked hand wheel with filleted joints: round rim with finger scallops on its inner side, straight
    //    spokes, keyed hub. Every junction is a rolling-ball fillet between curved faces: spoke/rim
    //    (cylinder/torus), spoke/hub (cylinder/cylinder) and the scallop edges (torus/cylinder, convex). The
    //    fillets are made once on the seed spoke and scallop and patterned with them.

    static CadDocument SpokedWheel()
    {
        var d = Doc(("D", "250"), ("RimT", "22"), ("HubD", "50"), ("HubL", "46"), ("Bore", "20"), ("KeyW", "6"), ("KeyT", "2.8"),
                    ("NS", "5"), ("SpokeD", "16"), ("NG", "3"), ("GripD", "14"), ("GripDepth", "3.5"),
                    ("Rf", "4"), ("Rh", "4"), ("Rg", "1.5"), ("Re", "2"), ("C", "1"),
                    // spokes start inside the hub wall; grips sit between the spokes (half a step off them)
                    ("SpokeIn", "HubD/2-4"), ("GripR", "D/2-RimT/2-GripD/2+GripDepth"), ("GripA", "180/(NS*NG)"));

        // rim: round section revolved about the wheel axis (Y)
        var rs = new Sk(d);
        var rimC = rs.Circle(rs.P("D/2", "0"), "RimT");
        rs.Centerline(Sketch.OriginId, rs.P("0", "RimT"));
        var rimSk = d.AddFeature(new SketchFeature { Name = "Rim section", Plane = PlaneRef.Front, Sketch = rs.S });
        var rim = d.AddFeature(new RevolveFeature { Name = "Rim", SketchId = rimSk.Id, Angle = "360" });

        // finger grips first: scallops cut into the inner side of the rim, edges rounded, patterned between the
        // spokes; the spokes are added afterwards, so a grip next to a spoke never cuts into it
        // (Top plane: sketch Y = -Z, so a positive sketch angle is a positive rotation about Y)
        var g = new Sk(d);
        var gripC = g.Circle(g.P("GripR*cos(GripA)", "GripR*sin(GripA)"), "GripD");
        var gSk = d.AddFeature(new SketchFeature { Name = "Grip", Plane = PlaneRef.Top, Sketch = g.S });
        var grip = d.AddFeature(new ExtrudeFeature { Name = "Grip", SketchId = gSk.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        var ga = g.Eval("GripA") * Math.PI / 180; var gr = g.Eval("D/2-RimT/2");
        var gripFil = d.AddFeature(new FilletFeature
        {
            Name = "Grip edges", Radius = "Rg",
            Edges = { E($"{rim.Id}.R.C{rimC}", $"{grip.Id}.S.C{gripC}", new Vec3(gr * Math.Cos(ga), g.Eval("RimT/4"), -gr * Math.Sin(ga))) },
        });
        d.AddFeature(new CircularPatternFeature { Name = "Grips", Seeds = { grip.Id, gripFil.Id }, Axis = new DirRef { Axis = "Y" }, Count = "NS*NG" });

        // hub
        var hs = new Sk(d);
        var hl = hs.Poly(hs.P("Bore/2", "-HubL/2"), hs.P("HubD/2", "-HubL/2"), hs.P("HubD/2", "HubL/2"), hs.P("Bore/2", "HubL/2"));
        hs.Centerline(Sketch.OriginId, hs.P("0", "HubL/2"));
        var hubSk = d.AddFeature(new SketchFeature { Name = "Hub section", Plane = PlaneRef.Front, Sketch = hs.S });
        var hub = d.AddFeature(new RevolveFeature { Name = "Hub", SketchId = hubSk.Id, Angle = "360" });
        string H(int i) => $"{hub.Id}.R.L{hl[i]}";
        double V(string e) => hs.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Hub ends", Radius = "Re",
            Edges = { E(H(0), H(1), new Vec3(V("HubD/2"), V("-HubL/2"), 0)), E(H(1), H(2), new Vec3(V("HubD/2"), V("HubL/2"), 0)) },
        });
        d.AddFeature(new ChamferFeature
        {
            Name = "Bore chamfers", Distance = "C",
            Edges = { E(H(2), H(3), new Vec3(V("Bore/2"), V("HubL/2"), 0)), E(H(3), H(0), new Vec3(V("Bore/2"), V("-HubL/2"), 0)) },
        });

        // one spoke along +X from inside the hub wall to the rim centre
        var sp = d.AddFeature(new PlaneFeature { Name = "Spoke plane", Base = PlaneRef.Right, Offset = "SpokeIn" });
        var sc = new Sk(d);
        var spokeC = sc.Circle(Sketch.OriginId, "SpokeD");
        var scSk = d.AddFeature(new SketchFeature { Name = "Spoke section", Plane = new PlaneRef { Feature = sp.Id }, Sketch = sc.S });
        var spoke = d.AddFeature(new ExtrudeFeature { Name = "Spoke", SketchId = scSk.Id, Depth = "D/2-SpokeIn" });
        string rimFace = $"{rim.Id}.R.C{rimC}", spokeFace = $"{spoke.Id}.S.C{spokeC}";
        var rimFil = d.AddFeature(new FilletFeature
        {
            Name = "Spoke to rim", Radius = "Rf",
            Edges = { E(rimFace, spokeFace, new Vec3(V("D/2-sqrt(RimT^2-SpokeD^2)/2"), V("SpokeD/2"), 0)) },
        });
        var hubFil = d.AddFeature(new FilletFeature
        {
            Name = "Spoke to hub", Radius = "Rh",
            Edges = { E(H(1), spokeFace, new Vec3(V("HubD/2"), V("SpokeD/2"), 0)) },
        });
        d.AddFeature(new CircularPatternFeature { Name = "Spokes", Seeds = { spoke.Id, rimFil.Id, hubFil.Id }, Axis = new DirRef { Axis = "Y" }, Count = "NS" });

        // keyway along the bore
        var k = new Sk(d);
        k.Rect("0", "-KeyW/2", "Bore/2+KeyT", "KeyW/2");
        var ks = d.AddFeature(new SketchFeature { Name = "Keyway", Plane = PlaneRef.Top, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyway", SketchId = ks.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // helpers

    static EdgeRef E(string a, string b, Vec3 hint) => new() { A = a, B = b, Hint = hint };

    /// <summary>
    /// A tube swept along a straight leg, a 90° bend and a second leg, with a bolted flange at its start: the
    /// profile (two circles) on Right, the path on Top starting at the origin.
    /// </summary>
    static CadDocument BentTube()
    {
        var d = Doc(("OD", "30"), ("ID", "24"), ("L1", "80"), ("R", "50"), ("L2", "60"),
                    ("FlangeD", "80"), ("FlangeT", "8"), ("PCD", "60"), ("BoltD", "8"), ("N", "4"));
        var p = new Sk(d);
        p.Circle(Sketch.OriginId, "OD"); p.Circle(Sketch.OriginId, "ID");
        var prof = d.AddFeature(new SketchFeature { Name = "Tube section", Plane = PlaneRef.Right, Sketch = p.S });
        // the path: along +x, a quarter turn about (L1, R), then along +y (Top: sketch y is world -z)
        var w = new Sk(d);
        int a = w.P("L1", "0"), c = w.P("L1", "R"), b = w.P("L1+R", "R"), e = w.P("L1+R", "R+L2");
        w.Line(Sketch.OriginId, a); w.Arc(c, a, b); w.Line(b, e);
        var path = d.AddFeature(new SketchFeature { Name = "Centreline", Plane = PlaneRef.Top, Sketch = w.S });
        d.AddFeature(new SweepFeature { Name = "Tube", ProfileSketchId = prof.Id, PathSketchId = path.Id });
        // the flange behind the tube's start, and its bolt holes around the tube's axis (X)
        var f = new Sk(d);
        f.Circle(Sketch.OriginId, "FlangeD"); f.Circle(Sketch.OriginId, "ID");
        var fs = d.AddFeature(new SketchFeature { Name = "Flange", Plane = PlaneRef.Right, Sketch = f.S });
        d.AddFeature(new ExtrudeFeature { Name = "Flange", SketchId = fs.Id, Depth = "FlangeT", Reverse = true });
        var h = new Sk(d);
        h.Circle(h.P("0", "PCD/2"), "BoltD");
        var hs = d.AddFeature(new SketchFeature { Name = "Bolt hole", Plane = PlaneRef.Right, Sketch = h.S });
        var cut = d.AddFeature(new ExtrudeFeature { Name = "Bolt hole", SketchId = hs.Id, IsCut = true, Depth = "FlangeT", Reverse = true });
        d.AddFeature(new CircularPatternFeature { Name = "Bolt circle", Seeds = { cut.Id }, Axis = new DirRef { Axis = "X" }, Count = "N" });
        return d;
    }

    /// <summary>
    /// A printable M10 bolt and nut: hex head and threaded shank (up to the head, lead-in chamfer), and a hex nut (a
    /// separate body) threaded through its hole of the minor diameter (countersunk both sides); both threads ISO (size
    /// fitted to the faces) with a fit clearance Clr.
    /// </summary>
    static CadDocument BoltAndNut()
    {
        var d = Doc(("D", "10"), ("P", "1.5"), ("L", "30"), ("S", "16"), ("HeadH", "6.4"), ("NutH", "8"), ("Gap", "30"), ("Clr", "0.15"));
        // bolt: head (a hexagon about the origin) on Front, shank on a plane at the head's top
        var h = new Sk(d);
        h.Polygon(Sketch.OriginId, 6, "S");
        var hs = d.AddFeature(new SketchFeature { Name = "Head", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Head", SketchId = hs.Id, Depth = "HeadH" });
        var top = d.AddFeature(new PlaneFeature { Name = "Head top", Base = PlaneRef.Front, Offset = "HeadH" });
        var sh = new Sk(d);
        var shc = sh.Circle(Sketch.OriginId, "D");
        var shs = d.AddFeature(new SketchFeature { Name = "Shank", Plane = new PlaneRef { Feature = top.Id }, Sketch = sh.S });
        var shank = d.AddFeature(new ExtrudeFeature { Name = "Shank", SketchId = shs.Id, Depth = "L" });
        double V(string e) => sh.Eval(e);
        d.AddFeature(new ThreadFeature
        {
            Name = "Bolt thread", End = ThreadEnd.UpToNext, Clearance = "Clr",
            Face = new FaceRef { Name = $"{shank.Id}.S.C{shc}", Hint = new Vec3(V("D/2"), 0, V("HeadH+L-1")) },
        });
        // nut: a separate body beside the bolt (a hexagon about a point Gap along X), its hole the thread's minor
        // diameter about the same point
        var n = new Sk(d);
        var nc = n.P("Gap", "0");
        n.Polygon(nc, 6, "S");
        var hole = n.Circle(nc, "D-1.0825*P");
        var ns = d.AddFeature(new SketchFeature { Name = "Nut", Plane = PlaneRef.Front, Sketch = n.S });
        var nut = d.AddFeature(new ExtrudeFeature { Name = "Nut", SketchId = ns.Id, Depth = "NutH", Merge = false });
        d.AddFeature(new ThreadFeature
        {
            Name = "Nut thread", End = ThreadEnd.ThroughAll, Clearance = "Clr",
            Face = new FaceRef { Name = $"{nut.Id}.S.C{hole}", Hint = new Vec3(V("Gap+(D-1.0825*P)/2"), 0, V("NutH-0.5")) },
        });
        return d;
    }

    /// <summary>
    /// A multi-start Acme lead screw: a rod of diameter D with an N-start Acme thread of pitch P (29° included angle,
    /// basic depth P/2, flat at the root 0.3707 P) over its length L, a journal of diameter J at one end and a 45°
    /// lead-in chamfer at the other. One helical cut of lead N·P, patterned N times about the axis. Beside it, its
    /// nut (a separate body, <see cref="AcmeNut"/>) about a point Gap along X.
    /// </summary>
    static CadDocument AcmeLeadScrew()
    {
        var d = Doc(("D", "20"), ("P", "4"), ("N", "3"), ("L", "80"), ("J", "12"), ("JL", "20"),
                    ("S", "30"), ("NutL", "24"), ("Clr", "0.2"), ("Gap", "D+S"));
        AcmeScrew(d);
        AcmeNut(d, "Gap");
        return d;
    }

    /// <summary>The screw of <see cref="AcmeLeadScrew"/> (D, P, N, L, J, JL), on the origin along +Z.</summary>
    static void AcmeScrew(CadDocument d)
    {
        // the rod on Front (along +Z), the journal on its far end
        var r = new Sk(d);
        var rc = r.Circle(Sketch.OriginId, "D");
        var rs = d.AddFeature(new SketchFeature { Name = "Rod", Plane = PlaneRef.Front, Sketch = r.S });
        var rod = d.AddFeature(new ExtrudeFeature { Name = "Rod", SketchId = rs.Id, Depth = "L" });
        var end = d.AddFeature(new PlaneFeature { Name = "Rod end", Base = PlaneRef.Front, Offset = "L" });
        var j = new Sk(d);
        j.Circle(Sketch.OriginId, "J");
        var js = d.AddFeature(new SketchFeature { Name = "Journal", Plane = new PlaneRef { Feature = end.Id }, Sketch = j.S });
        d.AddFeature(new ExtrudeFeature { Name = "Journal", SketchId = js.Id, Depth = "JL" });
        double V(string e) => r.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Lead-in", Distance = "P/2",
            Edges = { E($"{rod.Id}.S.C{rc}", $"{rod.Id}.Start", new Vec3(V("D/2"), 0, 0)) },
        });
        // the space between two teeth on Top (sketch y = -z), a pitch below the rod's free end: flat 0.3707 P at the
        // root (radius D/2 - P/2), flanks at 14.5° out past the surface; the centerline (the rod's axis) runs up +Z
        var g = new Sk(d);
        string Ro = "D/2+0.5", Rr = "D/2-P/2", Hr = "0.18535*P", Ho = "0.18535*P+(P/2+0.5)*tan(14.5)";
        g.Poly(g.P(Rr, $"P-{Hr}"), g.P(Ro, $"P-({Ho})"), g.P(Ro, $"P+{Ho}"), g.P(Rr, $"P+{Hr}"));
        g.Centerline(Sketch.OriginId, g.P("0", "-L"));
        var gs = d.AddFeature(new SketchFeature { Name = "Thread space", Plane = PlaneRef.Top, Sketch = g.S });
        var groove = d.AddFeature(new HelicalSweepFeature { Name = "Thread", SketchId = gs.Id, IsCut = true, Pitch = "P*N", Revolutions = "(L+2*P)/(P*N)" });
        d.AddFeature(new CircularPatternFeature { Name = "Starts", Seeds = { groove.Id }, Axis = new DirRef { Axis = "Z" }, Count = "N" });
    }

    /// <summary>
    /// The nut of <see cref="AcmeLeadScrew"/> (D, P, N, S, NutL, Clr) about a point x0 along X (the origin when "0"),
    /// along +Z: a hex across flats S, NutL long, its bore the minor diameter, threaded by the screw's tooth with a
    /// clearance Clr all round, swept and patterned the same way about the bore's axis.
    /// </summary>
    static void AcmeNut(CadDocument d, string x0)
    {
        string X(string e) => x0 == "0" ? e : $"{x0}+{e}";
        // a hex about the centre with a bore of the minor diameter (just clear of the screw's root) about the same
        // point, its ends chamfered into the bore
        var n = new Sk(d);
        double V(string e) => n.Eval(e);
        var centre = x0 == "0" ? Sketch.OriginId : n.P(x0, "0");
        n.Polygon(centre, 6, "S");
        var bore = n.Circle(centre, "D-P+2*Clr");
        var ns = d.AddFeature(new SketchFeature { Name = "Nut", Plane = PlaneRef.Front, Sketch = n.S });
        var nut = d.AddFeature(new ExtrudeFeature { Name = "Nut", SketchId = ns.Id, Depth = "NutL", Merge = false });
        string boreFace = $"{nut.Id}.S.C{bore}";
        d.AddFeature(new ChamferFeature
        {
            Name = "Nut lead-ins", Distance = "P/2",
            Edges =
            {
                E(boreFace, $"{nut.Id}.Start", new Vec3(V(X("D/2-P/2+Clr")), 0, 0)),
                E(boreFace, $"{nut.Id}.End", new Vec3(V(X("D/2-P/2+Clr")), 0, V("NutL"))),
            },
        });
        // the nut's thread space is the screw's tooth (P - 0.3707 P wide at the screw's root, narrowing at 14.5° a
        // side) with Clr all round: from inside the bore out to the screw's crest + Clr, centred half a pitch below
        // the nut (where the screw's tooth is: the nut moved onto the screw's axis fits it as it stands)
        var ng = new Sk(d);
        string Ri = "D/2-P/2+Clr-0.5", Rn = "D/2+Clr";
        string Hi = "0.31465*P-(Clr-0.5)*tan(14.5)+Clr", Hn = "0.31465*P-(P/2+Clr)*tan(14.5)+Clr";
        ng.Poly(ng.P(X(Ri), $"P/2-({Hi})"), ng.P(X(Rn), $"P/2-({Hn})"), ng.P(X(Rn), $"P/2+{Hn}"), ng.P(X(Ri), $"P/2+{Hi}"));
        ng.Centerline(x0 == "0" ? Sketch.OriginId : ng.P(x0, "0"), ng.P(x0, "-NutL"));
        var ngs = d.AddFeature(new SketchFeature { Name = "Nut thread space", Plane = PlaneRef.Top, Sketch = ng.S });
        var nutGroove = d.AddFeature(new HelicalSweepFeature { Name = "Nut thread", SketchId = ngs.Id, IsCut = true, Pitch = "P*N", Revolutions = "(NutL+2*P)/(P*N)" });
        d.AddFeature(new CircularPatternFeature
        {
            Name = "Nut starts", Seeds = { nutGroove.Id }, Count = "N",
            Axis = new DirRef { Face = new FaceRef { Name = boreFace, Hint = new Vec3(V(X("D/2-P/2+Clr")), 0, V("NutL/2")) } },
        });
    }

    static CadDocument Doc(params (string name, string expr)[] eqs)
    {
        var d = new CadDocument();
        foreach (var (n, e) in eqs) d.Equations.Add(new Equation { Name = n, Expr = e });
        return d;
    }

    /// <summary>
    /// Sketch builder that sketches like a SolidWorks user: lines whose ends share an X (Y) get a Vertical
    /// (Horizontal) relation, points on an axis get a relation to the origin, centred rectangles use a
    /// construction diagonal with the origin at its midpoint plus width/height dimensions, and only the
    /// remaining coordinates are dimensioned (by expressions of the variables). Relations and dimensions are
    /// added when the sketch is taken (<see cref="S"/>).
    /// </summary>
    sealed class Sk
    {
        readonly Sketch _s = new();
        readonly Func<string, double?> _lookup;
        bool _done;
        // coordinate expressions per point (null: determined otherwise, e.g. by an arc), lines between points
        readonly Dictionary<int, (string? x, string? y)> _coord = new() { [Sketch.OriginId] = ("0", "0") };
        readonly List<(int a, int b, int id)> _lines = new();

        public Sk(CadDocument d)
        {
            var (vals, _) = Regenerator.EvaluateEquations(d);
            _lookup = n => vals.TryGetValue(n, out var v) ? v : null;
        }

        /// <summary>The finished sketch (relations and dimensions added on first access).</summary>
        public Sketch S { get { Finish(); return _s; } }

        public double Eval(string e) => Expr.Eval(e, _lookup);

        /// <summary>Point at (x, y); a leading '-' means the negative side (the dimension is the magnitude).</summary>
        public int P(string x, string y, bool dimX = true)
        {
            var p = _s.AddPoint(new Vec2(Eval(x), Eval(y)));
            _coord[p.Id] = (dimX ? Norm(x) : null, Norm(y));
            return p.Id;
        }

        static string Norm(string e) => e.Replace(" ", "");

        public int Line(int a, int b) { var id = _s.Add(new SkLine { P1 = a, P2 = b }).Id; _lines.Add((a, b, id)); return id; }
        public int Centerline(int a, int b) { var id = _s.Add(new SkLine { P1 = a, P2 = b, Construction = true }).Id; _lines.Add((a, b, id)); return id; }

        /// <summary>
        /// Axis-aligned rectangle; lines: bottom, right, top, left. Centred on the origin (x0 = -x1, y0 = -y1) it is
        /// drawn as a centre rectangle: diagonal with the origin at its midpoint, width and height dimensions.
        /// </summary>
        public List<int> Rect(string x0, string y0, string x1, string y1)
        {
            bool Centred(string lo, string hi) => Norm(lo) == "-" + Norm(hi) || Norm(lo) == "-(" + Norm(hi) + ")";
            if (Centred(x0, x1) && Centred(y0, y1))
            {
                double w = Eval(x1), h = Eval(y1);
                int p0 = Free(-w, -h), p1 = Free(w, -h), p2 = Free(w, h), p3 = Free(-w, h);
                var L = Poly(p0, p1, p2, p3);
                _s.AddConstraint(CType.Horizontal, L[0]); _s.AddConstraint(CType.Horizontal, L[2]);
                _s.AddConstraint(CType.Vertical, L[1]); _s.AddConstraint(CType.Vertical, L[3]);
                var diag = _s.Add(new SkLine { P1 = p0, P2 = p2, Construction = true });
                _s.AddConstraint(CType.Midpoint, Sketch.OriginId, diag.Id);
                Size(L[0], $"2*({x1})"); Size(L[1], $"2*({y1})");
                return L;
            }
            return Poly(P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1));
        }

        /// <summary>Point whose position comes from relations only (no coordinate dimensions).</summary>
        int Free(double x, double y)
        {
            var p = _s.AddPoint(new Vec2(x, y));
            _coord[p.Id] = (null, null);
            return p.Id;
        }

        /// <summary>Length dimension of a line.</summary>
        void Size(int line, string expr)
        {
            var c = _s.AddConstraint(CType.Distance, line);
            c.Expr = Simplify(expr); c.Value = Math.Abs(Eval(expr));
        }

        static string Simplify(string e) => e.StartsWith("2*(") && e.EndsWith("/2)") && !e[3..^3].Contains('(') ? e[3..^3] : e;

        /// <summary>Closed polygon through the points.</summary>
        public List<int> Poly(params int[] pts)
        {
            var ids = new List<int>();
            for (int i = 0; i < pts.Length; i++) ids.Add(Line(pts[i], pts[(i + 1) % pts.Length]));
            return ids;
        }

        /// <summary>
        /// Regular polygon of an even number of sides about a centre point, as the Polygon tool draws it (corners on a
        /// construction circle about the centre, the sides equal), then one side Vertical and the size dimensioned
        /// across the flats (between that side and the one opposite). Lines from the corner just above the right
        /// flat, counter-clockwise.
        /// </summary>
        public List<int> Polygon(int center, int sides, string acrossFlats)
        {
            var c = _s.P(center);
            double af = Eval(acrossFlats), r = af / 2 / Math.Cos(Math.PI / sides);
            var circle = _s.Add(new SkCircle { Center = center, R = r, Construction = true });
            var corners = Enumerable.Range(0, sides).Select(i => Free(c.X + r * Math.Cos(Math.PI * (2 * i + 1) / sides), c.Y + r * Math.Sin(Math.PI * (2 * i + 1) / sides))).ToArray();
            var L = Poly(corners);
            foreach (var p in corners) OnArc(p, circle.Id);
            for (int i = 1; i < sides; i++) _s.AddConstraint(CType.Equal, L[0], L[i]);
            _s.AddConstraint(CType.Vertical, L[^1]);
            var k = _s.AddConstraint(CType.Distance, L[^1], L[sides / 2 - 1]);
            k.Expr = acrossFlats; k.Value = af;
            SketchSolver.CaptureSign(_s, k);
            return L;
        }

        public int Circle(int center, string diameter)
        {
            var c = _s.Add(new SkCircle { Center = center, R = Eval(diameter) / 2 });
            var k = _s.AddConstraint(CType.Diameter, c.Id);
            k.Expr = diameter; k.Value = Eval(diameter);
            return c.Id;
        }

        /// <summary>Arc from start to end (counter-clockwise) about center.</summary>
        public int Arc(int center, int start, int end) => _s.Add(new SkArc { Center = center, Start = start, End = end }).Id;

        public void Point(int p) => _s.Add(new SkPointEntity { P = p });

        /// <summary>Point lies on an arc/circle.</summary>
        public void OnArc(int point, int arc) => _s.AddConstraint(CType.PointOnCircle, point, arc);

        /// <summary>
        /// Arc from a to b through m (counter-clockwise order chosen automatically); its centre is a free point
        /// placed at the circumcentre, fixed by the solver through a PointOnCircle relation on m.
        /// </summary>
        public int ArcThrough(int a, int m, int b)
        {
            Vec2 A = _s.P(a), M = _s.P(m), B = _s.P(b);
            var ab = B - A; var am = M - A;
            var dd = 2 * Vec2.Cross(ab, am);
            var c = A + (new Vec2(am.Y, -am.X) * ab.LengthSq + new Vec2(-ab.Y, ab.X) * am.LengthSq) / dd; // circumcentre
            var cp = Free(c.X, c.Y);
            bool ccw = Vec2.Cross(A - c, M - c) > 0;
            var arc = ccw ? Arc(cp, a, b) : Arc(cp, b, a);
            OnArc(m, arc);
            return arc;
        }

        /// <summary>Relations for shared coordinates, then dimensions for what is left.</summary>
        void Finish()
        {
            if (_done) return;
            _done = true;
            foreach (var horizontal in new[] { false, true })
            {
                string? C(int p) => _coord.TryGetValue(p, out var c) ? (horizontal ? c.y : c.x) : null;
                // points joined by a line with the same coordinate expression share that coordinate by a relation
                var parent = _coord.Keys.ToDictionary(k => k, k => k);
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                foreach (var (a, b, id) in _lines)
                {
                    var ca = C(a); var cb = C(b);
                    if (ca == null || cb == null || ca != cb || Find(a) == Find(b)) continue;
                    _s.AddConstraint(horizontal ? CType.Horizontal : CType.Vertical, id);
                    parent[Find(a)] = Find(b);
                }
                foreach (var g in _coord.Keys.Where(k => C(k) != null).GroupBy(Find))
                {
                    var members = g.ToList();
                    var expr = C(members[0])!;
                    if (members.Contains(Sketch.OriginId)) continue; // already tied to the origin by relations
                    var rep = members[0];
                    if (expr == "0")
                    {
                        // on an axis: Horizontal/Vertical with the origin rather than a zero dimension
                        _s.AddConstraint(horizontal ? CType.Horizontal : CType.Vertical, Sketch.OriginId, rep);
                        continue;
                    }
                    var neg = expr.StartsWith('-');
                    var mag = neg ? expr[1..] : expr;
                    var c = _s.AddConstraint(horizontal ? CType.VDistance : CType.HDistance, Sketch.OriginId, rep);
                    c.Expr = mag; c.Value = Math.Abs(Eval(mag)); c.Sign = neg ? -1 : 1;
                }
            }
        }
    }
}

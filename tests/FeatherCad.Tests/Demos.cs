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
        ("spur-gear.fcad", Gear),
        ("hand-wheel.fcad", HandWheel),
        ("spoked-handwheel.fcad", SpokedWheel),
    };

    /// <summary>Equation changes each demo must survive (shows what the parameters do).</summary>
    public static readonly Dictionary<string, (string name, string expr)[][]> Variants = new()
    {
        ["pipe-flange.fcad"] = new[] { new[] { ("N", "12"), ("PCD", "125") }, new[] { ("OD", "180"), ("HubH", "45"), ("N", "6") }, new[] { ("BoltD", "18"), ("Rfil", "8") } },
        ["enclosure.fcad"] = new[] { new[] { ("L", "160"), ("W", "100") }, new[] { ("VentN", "9"), ("VentP", "5") }, new[] { ("H", "60"), ("BossH", "20"), ("Wall", "3") } },
        ["heat-sink.fcad"] = new[] { new[] { ("N", "20"), ("T", "1.2") }, new[] { ("L", "120"), ("FinH", "40") }, new[] { ("N", "8"), ("T", "3") } },
        ["stepped-shaft.fcad"] = new[] { new[] { ("D2", "50"), ("KeyW", "14") }, new[] { ("L2", "100"), ("KeyL", "60") }, new[] { ("D1", "35"), ("Rs", "2"), ("C", "2") } },
        ["hand-wheel.fcad"] = new[] { new[] { ("NS", "3") }, new[] { ("Dish", "45"), ("SpokeD", "16") }, new[] { ("D", "260"), ("NS", "6"), ("KnobL", "60") } },
        ["spoked-handwheel.fcad"] = new[] { new[] { ("NS", "6") }, new[] { ("D", "300"), ("RimT", "26"), ("NG", "4") }, new[] { ("NS", "4"), ("SpokeD", "20"), ("Rf", "3") } },
        ["spur-gear.fcad"] = new[] { new[] { ("z", "48") }, new[] { ("m", "2.5"), ("z", "30") }, new[] { ("PA", "25"), ("b", "20") } },
        ["l-bracket.fcad"] = new[] { new[] { ("Wb", "90"), ("HoleSp", "60") }, new[] { ("A", "100"), ("Bh", "80"), ("Gs", "40") }, new[] { ("T", "10"), ("Rb", "4") } },
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
                    var p = sk.P(c.Refs[0]); var a = (SkArc)sk.Entity(c.Refs[1])!;
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
                    var one = new FeatherCad.Kernel.Native.NBody(); one.Faces.Add(nf);
                    var facets = FeatherCad.Kernel.Native.NTess.Facets(one);
                    int good = 0, flipped = 0;
                    foreach (var fc in facets)
                    {
                        var c = fc.Centroid();
                        var n = nf.Surface.Normal(nf.Surface.Project(c)) * (nf.Sense ? 1 : -1);
                        if (Vec3.Dot(n, fc.Plane.Normal) > 0) good++; else flipped++;
                    }
                    var dom = FeatherCad.Kernel.Native.NDomain.Of(nf, 0.02);
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
    /// its preview image (the content is unchanged). Folders: their *.fcad files, not subfolders.
    /// </summary>
    public static int Containerize(IEnumerable<string> paths)
    {
        int bad = 0;
        foreach (var path in paths.SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.fcad").Order() : new[] { p }.AsEnumerable()))
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

using System.Diagnostics;

namespace SpryCad.Tests;

sealed class TestRunner
{
    public int Passed, Failed;
    readonly string? _filter;
    public TestRunner(string? filter) { _filter = filter; }

    public void Test(string name, Action body)
    {
        if (_filter != null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return;
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            Passed++;
            Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
            if (ex is not AssertException) Console.WriteLine(ex.StackTrace);
        }
    }

    public void True(bool cond, string msg) { if (!cond) throw new AssertException(msg); }
    public void Eq<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new AssertException($"expected {expected}, got {actual}"); }
    public void Near(double actual, double expected, double tol) { if (Math.Abs(actual - expected) > tol) throw new AssertException($"expected {expected} ±{tol}, got {actual}"); }
}

sealed class AssertException(string msg) : Exception(msg);

static class Program
{
    static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("FC_TOPO") != null) SpryCad.Kernel.Topology.Debug = Console.WriteLine;
        // (FC_BLEND=boolean only turns direct blends off)
        if (Environment.GetEnvironmentVariable("FC_BLEND") is { } fcBlend && fcBlend != "boolean") { SpryCad.Kernel.NativeBlend.Debug = Console.WriteLine; SpryCad.Kernel.RollingBall.Debug = Console.WriteLine; SpryCad.Kernel.SweptChamfer.Debug = Console.WriteLine; }
        if (Environment.GetEnvironmentVariable("FC_BOOL") != null) SpryCad.Kernel.Native.NBoolean.Trace = Console.WriteLine;
        if (Environment.GetEnvironmentVariable("FC_SHELL") != null) SpryCad.Kernel.Native.NShell.Debug = Console.WriteLine;
        if (args.Length > 7 && args[0] == "fillet") return Hang.Run(args);
        if (args.Length > 2 && args[0] == "rb") return RollingBallTool.Run(args);
        if (args.Length > 1 && args[0] == "import") return ImportTool.Run(args);
        if (args.Length > 1 && args[0] == "savebench")
        {
            // what an autosave holds the UI for: a part with an imported model of about the given MB
            var step = File.ReadAllBytes(args[1]);
            int mb = args.Length > 2 ? int.Parse(args[2]) : 20;
            var big = new byte[mb * 1024 * 1024];
            for (int o = 0; o < big.Length; o += step.Length) Array.Copy(step, 0, big, o, Math.Min(step.Length, big.Length - o));
            var doc = new SpryCad.Core.Model.CadDocument();
            doc.AddFile("imports/m.step", big);
            doc.Equations.Add(new SpryCad.Core.Model.Equation { Name = "W", Expr = "10" });
            var tmp = Path.Combine(Path.GetTempPath(), "sprycad-savebench.sprt");
            for (int r = 0; r < 3; r++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                doc.Save(tmp);
                var save = sw.Elapsed.TotalMilliseconds; sw.Restart();
                var copy = doc.Clone();
                var clone = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"{mb} MB attachment, run {r}: save {save:0} ms, clone (what the UI waits for now) {clone:0.0} ms");
            }
            File.Delete(tmp);
            return 0;
        }
        if (args.Length > 1 && args[0] == "unfold") return UnfoldTool.Run(args);
        if (args.Length > 2 && args[0] == "gridpart")
        {
            // a part whose one sketch is sketchbench's k×k grid of rectangles (a large sketch to edit in the app)
            var doc = new SpryCad.Core.Model.CadDocument();
            doc.AddFeature(new SpryCad.Core.Model.SketchFeature { Plane = SpryCad.Core.Model.PlaneRef.Front, Sketch = SketchBench.Grid(int.Parse(args[1])) });
            doc.Save(args[2], container: true);
            return 0;
        }
        if (args.Length > 1 && args[0] == "asmsolve")
        {
            // what solving each assembly's mates costs once it is open (best of ten, after a first solve)
            foreach (var file in args.Skip(1))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = SpryCad.Core.Assembly.AssemblyModel.Open(SpryCad.Core.Assembly.AssemblyDocument.Load(file), file);
                var open = sw.Elapsed.TotalMilliseconds;
                var first = m.SolveMates();
                double best = double.MaxValue;
                for (int i = 0; i < 10; i++) { sw.Restart(); m.SolveMates(); best = Math.Min(best, sw.Elapsed.TotalMilliseconds); }
                Console.WriteLine($"{Path.GetFileName(file),-22} open {open,7:0} ms  components {m.Doc.Components.Count,4}  mates {m.Doc.Mates.Count,4}  solve {best,8:0.00} ms");
            }
            return 0;
        }
        if (args.Length > 1 && args[0] == "loadtime")
        {
            // what opening a STEP file in the app costs: rebuild of the importing part, then the display mesh
            foreach (var file in args.Skip(1))
                for (int run = 0; run < 3; run++)
                {
                    var data = File.ReadAllBytes(file);
                    var doc = new SpryCad.Core.Model.CadDocument();
                    doc.AddFile("imports/m.step", data);
                    doc.AddFeature(new SpryCad.Core.Model.ImportFeature { File = "imports/m.step", Sha256 = SpryCad.Core.Model.DocumentContainer.Hash(data) + run });
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = new SpryCad.Core.Regen.Regenerator().Rebuild(doc);
                    var t1 = sw.ElapsedMilliseconds;
                    var tris = 0;
                    SpryCad.Kernel.Prof.Reset(); SpryCad.Kernel.Prof.Enabled = run == 2;
                    foreach (var b in r.State.Bodies) tris += b.Mesh.Indices.Length / 3;
                    if (run == 2) foreach (var (pn, pms, calls) in SpryCad.Kernel.Prof.Results().OrderByDescending(x => x.ms).Take(10)) Console.WriteLine($"    {pn,-26} {pms,8:0} ms x{calls}");
                    SpryCad.Kernel.Prof.Enabled = false;
                    Console.WriteLine($"{Path.GetFileName(file),-24} run {run}: rebuild {t1} ms, display mesh {sw.ElapsedMilliseconds - t1} ms ({tris} triangles)");
                }
            return 0;
        }
        if (args.Length > 1 && args[0] == "memload")
        {
            // what keeping STEP parts open costs: each imported, rebuilt and meshed as the app does, all kept
            static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var kept = new List<(SpryCad.Core.Regen.Regenerator regen, SpryCad.Core.Regen.RegenResult result)>();
            var files = args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a).Where(f => f.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)) : new[] { a }).ToList();
            var before = Managed();
            foreach (var file in files)
            {
                var data = File.ReadAllBytes(file);
                var doc = new SpryCad.Core.Model.CadDocument();
                doc.AddFile("imports/m.step", data);
                doc.AddFeature(new SpryCad.Core.Model.ImportFeature { File = "imports/m.step", Sha256 = SpryCad.Core.Model.DocumentContainer.Hash(data) });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var regen = new SpryCad.Core.Regen.Regenerator();
                var r = regen.Rebuild(doc);
                foreach (var b in r.State.Bodies) _ = b.Mesh;
                var ms = sw.ElapsedMilliseconds;
                var m0 = Managed();
                kept.Add((regen, r));
                Console.WriteLine($"{Path.GetFileName(file),-28} {ms,5} ms  managed {(m0 - before) / 1048576.0,8:0.0} MB total, {(m0 - before) / 1048576.0 / kept.Count,6:0.0} MB/part");
            }
            GC.KeepAlive(kept);
            return 0;
        }
        if (args.Length > 1 && args[0] == "nurbsprobe")
        {
            var res = SpryCad.Kernel.StepImport.Read(File.ReadAllBytes(args[1]), "F1.");
            var f = res.Solids[0].Body.Faces.First(x => x.Surface.Kind == SpryCad.Kernel.SurfaceKind.BSpline);
            var s = f.Surface; var nb = s.Nurbs!;
            Console.WriteLine($"U [{nb.U0},{nb.U1}] V [{nb.V0},{nb.V1}] closedU {nb.ClosedU} closedV {nb.ClosedV} poles {nb.Poles.Count}");
            foreach (var v in new[] { 1.4, 1.5, 1.55, 1.565, 1.57 })
            {
                var p = s.Eval(Math.PI, v);
                var c = nb.Closest(p);
                var ch = nb.Closest(p, nb.U0 + (nb.U1 - nb.U0) / 2, v - 0.01);
                Console.WriteLine($"face (pi,{v}) -> {p}: closest u={c.u:0.####} v={c.v:0.####} d={c.dist:g3} | hinted u={ch.u:0.####} v={ch.v:0.####} d={ch.dist:g3} | UV {s.UV(p, Math.PI, v - 0.01)}");
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "fitcheck")
        {
            SpryCad.Kernel.BSplineSurface.Debug = Console.WriteLine;
            SpryCad.Kernel.BSplineSurface.Fit((u, v) => new SpryCad.Kernel.Vec3(4 * Math.Cos(u), 4 * Math.Sin(u), 10 * v), 0, 1.5, 0, 2, 1e-9, 4, 4);
            return 0;
        }
        if (args.Length > 1 && args[0] == "verify-demos") return Demos.Verify(args[1], filter: args.Length > 2 ? args[2] : null);
        if (args.Length > 1 && args[0] == "demo-sketches") return Demos.SketchReport(args[1]);
        if (args.Length > 0 && args[0] == "blendab") return BlendAB.Run(args.Length > 1 ? args[1] : null);
        if (args.Length > 0 && args[0] == "matebench") return MateBench.Run(args[1..]);
        if (args.Length > 0 && args[0] == "sketchfuzz") return SketchFuzz.Cli(args[1..]);
        if (args.Length > 0 && args[0] == "sketchbench") return SketchBench.Run(args[1..]);
        if (args.Length > 0 && args[0] == "demos") return Demos.Write(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "fc-demos"));
        if (args.Length > 1 && args[0] == "containerize") return Demos.Containerize(args.Skip(1));
        if (args.Length > 2 && args[0] == "validate") return Validate.Run(args[1], args[2]);
        if (args.Length > 4 && args[0] == "repro") { Bench.Repro(args[1], args[2..]); return 0; }
        if (args.Length > 1 && args[0] == "markertime") { Bench.MarkerTime(args[1]); return 0; }
        if (args.Length > 1 && args[0] == "cases") { Bench.Cases(args[1]); return 0; }
        if (args.Length > 2 && args[0] is "tessrepeat" or "tessrepeat-fresh") return Bench.TessRepeat(args[1], int.Parse(args[2]), args[3..], args[0] == "tessrepeat-fresh");
        if (args.Length > 2 && args[0] == "edgedir") return Bench.EdgeDirections(args[1], args[2]);
        if (args.Length > 1 && args[0] == "massprops") return Bench.MassPropsTool(args[1], args[2..]);
        if (args.Length > 1 && args[0] == "demoprints") return Bench.DemoPrints(args[1]);
        if (args.Length > 2 && args[0] == "determinism") return Bench.Determinism(args[1], int.Parse(args[2]), args[3..]);
        if (args.Length > 2 && args[0] == "featprof") { Bench.FeatureProfile(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 3); return 0; }
        if (args.Length > 1 && args[0] == "localbench") return LocalBench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
        if (args.Length > 0 && args[0] == "loftbench") return LoftBench.Run();
        if (args.Length > 0 && args[0] == "loftmirror") return LoftMirrorBench.Run();
        if (args.Length > 0 && args[0] == "loftstep") return LoftProbe.Step();
        if (args.Length > 0 && args[0] == "loftprobe") return LoftProbe.Run(args.Length > 1 ? args[1] : null);
        if (args.Length > 1 && args[0] == "bench")
        {
            Bench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
            return 0;
        }
        var filter = args.Length > 0 ? args[0] : null;
        var t = new TestRunner(filter);
        int result = 0;
        var thread = new Thread(() =>
        {
            Console.WriteLine("Kernel");
            KernelTests.Run(t);
            Console.WriteLine("Exact");
            ExactTests.Run(t);
            Console.WriteLine("Native");
            NativeTests.Run(t);
            RollingBallTool.Tests(t);
            ContainerTests.Run(t);
            ImportTests.Run(t);
            NativeBoolTests.Run(t);
            NativeRevolveTests.Run(t);
            SheetMetalTests.Run(t);
            DraftTests.Run(t);
            ExactMassTests.Run(t);
            DeterminismTests.Run(t);
            PatternUnionTests.Run(t);
            SketchOverlapTests.Run(t);
            SketchLookupTests.Run(t);
            IdTests.Run(t);
            FeatureColorTests.Run(t);
            DirectBlendTests.Run(t);
            SweepTests.Run(t);
            SweepFeatureTests.Run(t);
            LoftTests.Run(t);
            LoftFeatureTests.Run(t);
            HelicalTests.Run(t);
            ThreadFeatureTests.Run(t);
            AssemblyTests.Run(t);
            MateDerivativeTests.Run(t);
            NestedAssemblyTests.Run(t);
            AssemblyToolTests.Run(t);
            SketchVisibilityTests.Run(t);
            SketchConvertTests.Run(t);
            RevolveAxisTests.Run(t);
            ConfigTests.Run(t);
            AxisCenterTests.Run(t);
            PlaneSectionTests.Run(t);
            SlotDragTests.Run(t);
            SketchFuzz.Check(t);
            Console.WriteLine("Core");
            CoreTests.Run(t);
            Console.WriteLine($"\n{t.Passed} passed, {t.Failed} failed");
            result = t.Failed == 0 ? 0 : 1;
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }
}

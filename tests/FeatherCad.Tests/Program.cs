using System.Diagnostics;

namespace FeatherCad.Tests;

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
        if (Environment.GetEnvironmentVariable("FC_TOPO") != null) FeatherCad.Kernel.Topology.Debug = Console.WriteLine;
        if (Environment.GetEnvironmentVariable("FC_BLEND") != null) { FeatherCad.Kernel.NativeBlend.Debug = Console.WriteLine; FeatherCad.Kernel.RollingBall.Debug = Console.WriteLine; }
        if (Environment.GetEnvironmentVariable("FC_BOOL") != null) FeatherCad.Kernel.Native.NBoolean.Trace = Console.WriteLine;
        if (Environment.GetEnvironmentVariable("FC_SHELL") != null) FeatherCad.Kernel.Native.NShell.Debug = Console.WriteLine;
        if (args.Length > 7 && args[0] == "fillet") return Hang.Run(args);
        if (args.Length > 2 && args[0] == "rb") return RollingBallTool.Run(args);
        if (args.Length > 1 && args[0] == "import") return ImportTool.Run(args);
        if (args.Length > 1 && args[0] == "unfold") return UnfoldTool.Run(args);
        if (args.Length > 1 && args[0] == "loadtime")
        {
            // what opening a STEP file in the app costs: rebuild of the importing part, then the display mesh
            foreach (var file in args.Skip(1))
                for (int run = 0; run < 3; run++)
                {
                    var data = File.ReadAllBytes(file);
                    var doc = new FeatherCad.Core.Model.CadDocument();
                    doc.AddFile("imports/m.step", data);
                    doc.AddFeature(new FeatherCad.Core.Model.ImportFeature { File = "imports/m.step", Sha256 = FeatherCad.Core.Model.DocumentContainer.Hash(data) + run });
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = new FeatherCad.Core.Regen.Regenerator().Rebuild(doc);
                    var t1 = sw.ElapsedMilliseconds;
                    var tris = 0;
                    FeatherCad.Kernel.Prof.Reset(); FeatherCad.Kernel.Prof.Enabled = run == 2;
                    foreach (var b in r.State.Bodies) tris += b.Mesh.Indices.Length / 3;
                    if (run == 2) foreach (var (pn, pms, calls) in FeatherCad.Kernel.Prof.Results().OrderByDescending(x => x.ms).Take(10)) Console.WriteLine($"    {pn,-26} {pms,8:0} ms x{calls}");
                    FeatherCad.Kernel.Prof.Enabled = false;
                    Console.WriteLine($"{Path.GetFileName(file),-24} run {run}: rebuild {t1} ms, display mesh {sw.ElapsedMilliseconds - t1} ms ({tris} triangles)");
                }
            return 0;
        }
        if (args.Length > 1 && args[0] == "memload")
        {
            // what keeping STEP parts open costs: each imported, rebuilt and meshed as the app does, all kept
            static long Managed() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }
            var kept = new List<(FeatherCad.Core.Regen.Regenerator regen, FeatherCad.Core.Regen.RegenResult result)>();
            var files = args.Skip(1).SelectMany(a => Directory.Exists(a) ? Directory.GetFiles(a).Where(f => f.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)) : new[] { a }).ToList();
            var before = Managed();
            foreach (var file in files)
            {
                var data = File.ReadAllBytes(file);
                var doc = new FeatherCad.Core.Model.CadDocument();
                doc.AddFile("imports/m.step", data);
                doc.AddFeature(new FeatherCad.Core.Model.ImportFeature { File = "imports/m.step", Sha256 = FeatherCad.Core.Model.DocumentContainer.Hash(data) });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var regen = new FeatherCad.Core.Regen.Regenerator();
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
            var res = FeatherCad.Kernel.StepImport.Read(File.ReadAllBytes(args[1]), "F1.");
            var f = res.Solids[0].Body.Faces.First(x => x.Surface.Kind == FeatherCad.Kernel.SurfaceKind.BSpline);
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
            FeatherCad.Kernel.BSplineSurface.Debug = Console.WriteLine;
            FeatherCad.Kernel.BSplineSurface.Fit((u, v) => new FeatherCad.Kernel.Vec3(4 * Math.Cos(u), 4 * Math.Sin(u), 10 * v), 0, 1.5, 0, 2, 1e-9, 4, 4);
            return 0;
        }
        if (args.Length > 1 && args[0] == "verify-demos") return Demos.Verify(args[1], filter: args.Length > 2 ? args[2] : null);
        if (args.Length > 1 && args[0] == "demo-sketches") return Demos.SketchReport(args[1]);
        if (args.Length > 0 && args[0] == "demos") return Demos.Write(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "fc-demos"));
        if (args.Length > 1 && args[0] == "containerize") return Demos.Containerize(args.Skip(1));
        if (args.Length > 2 && args[0] == "validate") return Validate.Run(args[1], args[2]);
        if (args.Length > 4 && args[0] == "repro") { Bench.Repro(args[1], args[2..]); return 0; }
        if (args.Length > 1 && args[0] == "markertime") { Bench.MarkerTime(args[1]); return 0; }
        if (args.Length > 1 && args[0] == "cases") { Bench.Cases(args[1]); return 0; }
        if (args.Length > 2 && args[0] == "featprof") { Bench.FeatureProfile(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 3); return 0; }
        if (args.Length > 1 && args[0] == "localbench") return LocalBench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
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

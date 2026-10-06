using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary>STEP import: exact round trips, the Imported feature, features on top of imported geometry.</summary>
static class ImportTests
{
    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "fc-import-tests", name);

    /// <summary>A demo part exported to STEP (bytes) and its exact volume.</summary>
    static (byte[] step, double volume) Exported(string demo, bool volume = true)
    {
        var doc = Demos.All.First(d => d.file == demo).build();
        var r = new Regenerator().Rebuild(doc);
        using var ms = new MemoryStream();
        StepWriter.Write(ms, r.State.Bodies.Select(b => (b.Id, b.Solid)), demo);
        return (ms.ToArray(), volume ? r.State.Bodies.Sum(b => Vol(b.Solid)) : 0);
    }

    /// <summary>Volume of the exact body tessellated at 1e-3 (both sides of a comparison are, so the chord errors nearly cancel).</summary>
    static double Vol(Solid s) => NTess.ToSolid(s.Native!, 1e-3).Volume();

    static CadDocument ImportDoc(byte[] step, string name = "imports/part.step")
    {
        var d = new CadDocument();
        d.AddFile(name, step);
        d.AddFeature(new ImportFeature { File = name, Sha256 = DocumentContainer.Hash(step) });
        return d;
    }

    public static void Run(TestRunner t)
    {
        Directory.CreateDirectory(Temp(""));

        foreach (var demo in new[] { "pipe-flange.fcad", "stepped-shaft.fcad", "hand-wheel.fcad", "enclosure.fcad" })
            t.Test($"STEP import: {demo} exports and imports back exactly (seams, bands, apexes, whole torus)", () =>
            {
                var (step, volume) = Exported(demo);
                var r = new Regenerator().Rebuild(ImportDoc(step));
                var f = r.Status.Values.First();
                t.True(f.Ok, f.Error ?? "");
                t.True(r.State.Bodies.All(b => b.Solid.Native != null && b.Solid.Topology.OpenHalfEdges == 0), "imported body not closed");
                t.Near(r.State.Bodies.Sum(b => Vol(b.Solid)), volume, volume * 2e-5);
            });

        t.Test("STEP import: OCCT-style seams, apex vertex loops and a whole sphere", () =>
        {
            // a sphere written as one face with a single vertex loop, a cylinder with a seam edge
            var step = """
                ISO-10303-21;
                HEADER; FILE_DESCRIPTION((''),'2;1'); FILE_NAME('s','',(''),(''),'','',''); FILE_SCHEMA(('AUTOMOTIVE_DESIGN')); ENDSEC;
                DATA;
                #1=CARTESIAN_POINT('',(0.,0.,0.));
                #2=DIRECTION('',(0.,0.,1.));
                #3=DIRECTION('',(1.,0.,0.));
                #4=AXIS2_PLACEMENT_3D('',#1,#2,#3);
                #5=SPHERICAL_SURFACE('',#4,10.);
                #6=CARTESIAN_POINT('',(0.,0.,10.));
                #7=VERTEX_POINT('',#6);
                #8=VERTEX_LOOP('',#7);
                #9=FACE_BOUND('',#8,.T.);
                #10=ADVANCED_FACE('',(#9),#5,.T.);
                #11=CLOSED_SHELL('',(#10));
                #12=MANIFOLD_SOLID_BREP('ball',#11);
                ENDSEC;
                END-ISO-10303-21;
                """;
            var res = StepImport.Read(System.Text.Encoding.ASCII.GetBytes(step), "F1.");
            var body = res.Solids.Single().Body;
            t.Eq(body.Validate(), null);
            t.Near(NTess.ToSolid(body, 1e-3).Volume(), 4.0 / 3 * Math.PI * 1000, 2);
        });

        t.Test("STEP import: a fillet on an imported edge, kept through save and reload", () =>
        {
            var (step, volume) = Exported("l-bracket.fcad");
            var d = ImportDoc(step);
            var r0 = new Regenerator().Rebuild(d);
            // a straight convex edge between two imported planar faces
            var nb = r0.State.Bodies[0].Solid.Native!; nb.Link();
            var e = nb.Edges.First(x => x.Curve is NLine && x.Uses.Count == 2 && x.Uses.All(u => u.Face.Surface.Kind == SurfaceKind.Plane)
                && Vec3.Dot(x.Uses[0].Face.Surface.Plane!.Normal * (x.Uses[0].Face.Sense ? 1 : -1), x.Uses[1].Face.Surface.Plane!.Normal * (x.Uses[1].Face.Sense ? 1 : -1)) > -0.5
                && IsConvex(x));
            t.True(e.Uses[0].Face.Tag.Name.StartsWith(d.Features[0].Id + ".I"), "imported faces are not named after the STEP faces");
            d.AddFeature(new FilletFeature { Radius = "1", Edges = { new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid } } });
            var r1 = new Regenerator().Rebuild(d);
            t.True(r1.Status.Values.All(s => s.Ok), string.Join("; ", r1.Status.Values.Select(s => s.Error)));
            var v1 = Vol(r1.State.Bodies[0].Solid);
            t.True(v1 < volume - 1e-3, "fillet removed nothing");
            var path = Temp("imported.fcad");
            d.Save(path);
            t.True(CadDocument.IsContainer(path), "a part with an imported file must be a container");
            var r2 = new Regenerator().Rebuild(CadDocument.Load(path));
            t.True(r2.Status.Values.All(s => s.Ok), "reloaded part does not rebuild");
            t.Near(Vol(r2.State.Bodies[0].Solid), v1, 1e-6);
        });

        t.Test("Shell of an exact body it cannot offset fails fast with a reason (no broken fallback)", () =>
        {
            // user's part: fillet (rolling ball, trimmed by a wall) then shell whose inner fillet no longer reaches the wall
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var path = Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "failed-shell.fcad");
            var d = CadDocument.Load(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(d);
            t.True(sw.ElapsedMilliseconds < 8000, $"took {sw.ElapsedMilliseconds} ms");
            var shell = d.Features.OfType<ShellFeature>().Single();
            var st = r.Status[shell.Id];
            t.True(!st.Ok && st.Error!.Contains("shell cannot be made"), "expected a shell error, got: " + (st.Error ?? "success"));
            t.True(r.State.Bodies.All(b => b.Solid.Native != null && b.Solid.Topology.OpenHalfEdges == 0), "the part before the shell must stay intact");
            // shelled before the fillet and chamfer (thinner wall), it works
            var fs = d.Features;
            fs.Remove(shell);
            fs.Insert(fs.FindIndex(f => f is FilletFeature), shell);
            shell.Thickness = "1";
            var r2 = new Regenerator().Rebuild(d);
            t.True(r2.Status.Values.All(s => s.Ok), string.Join("; ", r2.Status.Values.Where(s => !s.Ok).Select(s => s.Error)));
            t.True(r2.State.Bodies.All(b => b.Solid.Native != null && b.Solid.Topology.OpenHalfEdges == 0), "shelled part not closed");
        });

        t.Test("STEP import: inch documents get the geometry in inches", () =>
        {
            var (step, volume) = Exported("heat-sink.fcad");
            var d = ImportDoc(step); d.Units = UnitSystem.IPS;
            var r = new Regenerator().Rebuild(d);
            t.Near(Vol(r.State.Bodies[0].Solid), volume / Math.Pow(25.4, 3), volume / Math.Pow(25.4, 3) * 1e-4);
        });

        t.Test("STEP import: unsupported geometry is reported, not half-imported", () =>
        {
            var (step, _) = Exported("spoked-handwheel.fcad", volume: false);
            var r = new Regenerator().Rebuild(ImportDoc(step));
            var s = r.Status.Values.First();
            t.True(!s.Ok && s.Error!.Contains("B-spline"), "expected a clear error, got: " + s.Error);
        });
    }

    static bool IsConvex(NEdge e)
    {
        var (fa, fb) = (e.Uses[0].Face, e.Uses[1].Face);
        var nA = fa.Surface.Plane!.Normal * (fa.Sense ? 1 : -1); var nB = fb.Surface.Plane!.Normal * (fb.Sense ? 1 : -1);
        var t = (e.Curve.Deriv(e.T0) * (e.Uses[0].Forward ? 1 : -1)).Normalized;
        var uB = Vec3.Cross(nB, -t);
        return Vec3.Dot(uB, nA) < 0 && Math.Abs(Vec3.Dot(nA, nB)) < 0.99;
    }
}

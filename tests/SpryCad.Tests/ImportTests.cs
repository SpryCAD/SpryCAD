using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

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

        foreach (var demo in new[] { "pipe-flange.sprt", "stepped-shaft.sprt", "hand-wheel.sprt", "enclosure.sprt" })
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

        t.Test("STEP import: instance numbers past 32 bits (SolidWorks' #18446744073709551615 for a missing reference)", () =>
        {
            // a point numbered 2^32 (any size is valid), and SolidWorks 2024's null reference: -1 as an unsigned 64-bit number
            var step = """
                ISO-10303-21;
                HEADER; FILE_DESCRIPTION((''),'2;1'); FILE_NAME('s','',(''),(''),'','',''); FILE_SCHEMA(('CONFIG_CONTROL_DESIGN')); ENDSEC;
                DATA;
                #4294967296=CARTESIAN_POINT('',(0.,0.,0.));
                #2=DIRECTION('',(0.,0.,1.));
                #3=DIRECTION('',(1.,0.,0.));
                #4=AXIS2_PLACEMENT_3D('',#4294967296,#2,#3);
                #5=SPHERICAL_SURFACE('',#4,10.);
                #6=CARTESIAN_POINT('',(0.,0.,10.));
                #7=VERTEX_POINT('',#6);
                #8=VERTEX_LOOP('',#7);
                #9=FACE_BOUND('',#8,.T.);
                #10=ADVANCED_FACE('',(#9),#5,.T.);
                #11=CLOSED_SHELL('',(#10));
                #12=MANIFOLD_SOLID_BREP('ball',#11);
                #13=PERSON_AND_ORGANIZATION(#18446744073709551615,#18446744073709551615);
                #14=PERSON_AND_ORGANIZATION(#18446744073709551615,#18446744073709551615);
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
            var (step, volume) = Exported("l-bracket.sprt");
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
            var path = Temp("imported.sprt");
            d.Save(path);
            t.True(SpryCad.Core.Storage.PartDatabase.IsFile(path), "not saved as a database");
            t.True(CadDocument.Load(path).Files.Count == d.Files.Count && d.Files.Count > 0, "the imported file is not kept in the part");
            var r2 = new Regenerator().Rebuild(CadDocument.Load(path));
            t.True(r2.Status.Values.All(s => s.Ok), "reloaded part does not rebuild");
            t.Near(Vol(r2.State.Bodies[0].Solid), v1, 1e-6);
        });

        t.Test("Shell of an exact body it cannot offset fails fast with a reason (no broken fallback)", () =>
        {
            // user's part: fillet (rolling ball, trimmed by a wall) then shell whose inner fillet no longer reaches the wall
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var path = Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "failed-shell.sprt");
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

        t.Test("STEP import: the solid's own unit context wins over other units in the file; flipped face flags are corrected", () =>
        {
            var (step, volume) = Exported("l-bracket.sprt");
            var text = System.Text.Encoding.ASCII.GetString(step);
            // an unused metre unit ahead of everything (presentation data in real files)
            text = text.Replace("DATA;\n", "DATA;\n#0=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT($,.METRE.));\n");
            // one planar face written with its same_sense flag reversed (its loops still say outward)
            var m = System.Text.RegularExpressions.Regex.Match(text, @"ADVANCED_FACE\('[^']*',\([^)]*\),#(\d+),\.T\.\)");
            t.True(m.Success, "no face to flip");
            text = text[..m.Index] + m.Value.Replace(",.T.)", ",.F.)") + text[(m.Index + m.Length)..];
            var res = StepImport.Read(System.Text.Encoding.ASCII.GetBytes(text), "F1.");
            t.True(res.Warnings.Any(w => w.Contains("orientation")), "flipped flag not reported");
            var body = res.Solids.Single().Body;
            t.Eq(body.Validate(), null);
            var solid = NTess.ToSolid(body);
            t.True(solid.Topology.OpenHalfEdges == 0, "display mesh not closed");
            t.Near(NTess.ToSolid(body, 1e-3).Volume(), volume, volume * 2e-5);
        });

        t.Test("STEP import: inch documents get the geometry in inches", () =>
        {
            var (step, volume) = Exported("heat-sink.sprt");
            var d = ImportDoc(step); d.Units = UnitSystem.IPS;
            var r = new Regenerator().Rebuild(d);
            t.Near(Vol(r.State.Bodies[0].Solid), volume / Math.Pow(25.4, 3), volume / Math.Pow(25.4, 3) * 1e-4);
        });

        t.Test("STEP round trip: a round-wire spring keeps its half-round helical faces apart (each its own B-spline patch)", () =>
        {
            // the two halves of the wire share a name and a screw surface; joined across their seams for STEP they
            // made one face all the way round the wire on a B-spline that does not close (Inventor: flared sheets)
            var nb = NHelical.Sweep(new[] { new ProfileRegion(ProfileLoop.Circle(new Vec2(5, 0), 1, "C")) }, Frame.Front, Vec3.Zero, Vec3.UnitY, 5, 3 * 2 * Math.PI, "S", exitLength: 10);
            var solid = NTess.ToSolid(nb);
            using var ms = new MemoryStream();
            StepWriter.Write(ms, new[] { ("spring", solid) }, "spring");
            t.Eq(StepWriter.LastError, null);
            var text = System.Text.Encoding.ASCII.GetString(ms.ToArray());
            t.Eq(System.Text.RegularExpressions.Regex.Matches(text, "B_SPLINE_SURFACE").Count, 2);
            var r = new Regenerator().Rebuild(ImportDoc(ms.ToArray()));
            var s = r.Status.Values.First();
            t.True(s.Ok, s.Error ?? "");
            var b = r.State.Bodies.Single().Solid;
            t.True(b.Native != null && b.Topology.OpenHalfEdges == 0, "not closed");
            // both sides tessellated alike (the chord errors nearly cancel); exactly: Pappus and π r² · 10
            var volume = NTess.ToSolid(nb, 5e-3).Volume();
            t.Near(volume, 3 * 2 * Math.PI * Math.PI * 5 + Math.PI * 10, 2.0);
            t.Near(NTess.ToSolid(b.Native!, 5e-3).Volume(), volume, volume * 1e-3);
        });

        t.Test("STEP round trip: a modeled thread's flanks (B-splines in the file) read back as screw surfaces", () =>
        {
            var (step, volume) = Exported("acme-lead-screw.sprt");
            var res = StepImport.Read(step, "F1.");
            var bodies = res.Solids.Select(x => x.Body).ToList();
            foreach (var nb in bodies) t.Eq(nb.Validate(), null);
            var faces = bodies.SelectMany(nb => nb.Faces).ToList();
            t.Eq(faces.Count(f => f.Surface.Kind == SurfaceKind.BSpline), 0);
            t.True(faces.Count(f => f.Surface.Kind == SurfaceKind.Screw) >= 4, "screw faces");
            // on the round lead about each part's axis: one motion for the flanks of a thread
            foreach (var screws in bodies.Select(nb => nb.Faces.Where(f => f.Surface.Kind == SurfaceKind.Screw).ToList()).Where(l => l.Count > 0))
            {
                t.True(screws.All(f => f.Surface.Screw!.Motion.SameHelices(screws[0].Surface.Screw!.Motion)), "one motion");
                // their long edges exact helices of it too
                t.True(screws.SelectMany(f => f.Coedges).Any(c => c.Edge.Curve is NHelix h && h.Motion.SameHelices(screws[0].Surface.Screw!.Motion)), "helical edges");
            }
            t.Near(bodies.Sum(nb => NTess.ToSolid(nb, 1e-3).Volume()), volume, volume * 1e-4);
        });

        t.Test("STEP import: B-spline blend faces (spoked wheel round trip)", () =>
        {
            var original = new Regenerator().Rebuild(Demos.All.First(d => d.file == "spoked-handwheel.sprt").build()).State.Bodies.Single().Solid;
            using var ms = new MemoryStream();
            StepWriter.Write(ms, new[] { ("wheel", original) }, "wheel");
            var step = ms.ToArray();
            var volume = NTess.ToSolid(original.Native!, 5e-3).Volume();
            var r = new Regenerator().Rebuild(ImportDoc(step));
            var s = r.Status.Values.First();
            t.True(s.Ok, s.Error ?? "");
            var b = r.State.Bodies.Single().Solid;
            t.True(b.Native != null && b.Topology.OpenHalfEdges == 0, "not closed");
            t.True(b.Native!.Faces.Count(f => f.Surface.Kind == SurfaceKind.BSpline) == 100, "blend faces are not B-splines");
            // the blends were exported as fitted B-splines (within 1e-5 mm); both tessellated at 5e-3
            t.Near(NTess.ToSolid(b.Native, 5e-3).Volume(), volume, volume * 2e-4);
        });

        // NURBS-converted solids and a rolling-ball fillet written by OCCT: rational, closed and polar B-spline surfaces
        foreach (var (file, exact) in new[]
        {
            ("n-cyl.step", Math.PI * 100 * 30), ("n-cone.step", Math.PI * 100 * 20 / 3), ("n-sphere.step", 4.0 / 3 * Math.PI * 1000),
            ("n-torus.step", 2 * Math.PI * Math.PI * 20 * 25), ("n-boxhole.step", 40.0 * 40 * 10 - Math.PI * 36 * 10), ("occ-fillet.step", 53076.38),
        })
            foreach (var recognize in new[] { false, true })
                t.Test($"STEP import: B-spline surfaces from OCCT ({file}{(recognize ? ", recognized" : "")})", () =>
                {
                    var res = ReadModel(file, recognize);
                    var body = res.Solids.Single().Body;
                    t.Eq(body.Validate(), null);
                    var solid = NTess.ToSolid(body);
                    t.True(solid.Topology.OpenHalfEdges == 0, "display mesh not closed");
                    t.Near(NTess.ToSolid(body, 1e-3).Volume(), exact, exact * 3e-4);
                    if (recognize) Console.WriteLine($"        {string.Join(", ", body.Faces.GroupBy(f => f.Surface.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
                });

        // the same part as exact analytic geometry and as OCCT's NURBS conversion: shell and fillet must agree
        (Solid nurbs, Solid analytic) BoxHole()
        {
            var res = ReadModel("n-boxhole.step", recognize: false);
            var box = Builders.Box(new Vec3(-20, -20, 0), new Vec3(20, 20, 10), "Box");
            var an = Csg.Subtract(box, Builders.Cylinder(new Vec3(0, 0, -1), Vec3.UnitZ, 6, 12, "Hole"));
            return (NTess.ToSolid(res.Solids.Single().Body), an);
        }
        static string TopFace(Solid s) => s.Native!.Faces.First(f => f.Coedges.All(c => Math.Abs(c.Start.P.Z - 10) < 1e-6) && f.Coedges.Any(c => Math.Abs(c.Start.P.X) > 19)).Tag.Name;
        static List<NEdge> HoleTopEdges(Solid s)
        {
            s.Native!.Link();
            return s.Native.Edges.Where(e => !NativeBlend.IsSeam(e) && Math.Abs(e.V0.P.Z - 10) < 1e-6 && Math.Abs(e.Mid.Z - 10) < 1e-6
                && Math.Abs(Math.Sqrt(e.Mid.X * e.Mid.X + e.Mid.Y * e.Mid.Y) - 6) < 1e-5).ToList();
        }

        t.Test("Shell of B-spline faces (offset surfaces) matches the analytic part", () =>
        {
            var (nurbs, an) = BoxHole();
            Solid sn;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { sn = ShellOp.Apply(nurbs, new HashSet<string> { TopFace(nurbs) }, 1, "S"); }
            catch (ShellFailedException ex) { throw new AssertException(ex.Message + " / " + Csg.LastFallbackReason); }
            Console.WriteLine($"        B-spline shell {sw.ElapsedMilliseconds} ms");
            var sa = ShellOp.Apply(an, new HashSet<string> { TopFace(an) }, 1, "S");
            if (Environment.GetEnvironmentVariable("FC_DUMP") is { } dumpDir)
                using (var fs = File.Create(Path.Combine(dumpDir, "nurbs-shell.step"))) StepWriter.Write(fs, new[] { ("shell", sn) }, "shell");
            t.True(sn.Native != null && sn.Topology.OpenHalfEdges == 0, "B-spline shell not closed");
            t.True(sn.Native!.Faces.Any(f => f.Surface.Kind == SurfaceKind.Offset), "no offset surfaces in the shell");
            var exact = 14869.03 - (38.0 * 38 * 9 - Math.PI * 49 * 9);
            t.Near(NTess.ToSolid(sa.Native!, 1e-3).Volume(), exact, exact * 3e-4);
            t.Near(NTess.ToSolid(sn.Native!, 1e-3).Volume(), exact, exact * 3e-4);
        });

        t.Test("Fillet between B-spline faces (rolling ball on offset surfaces) matches the analytic torus fillet", () =>
        {
            var (nurbs, an) = BoxHole();
            var errors = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Prof.Reset(); Prof.Enabled = Environment.GetEnvironmentVariable("FC_PROF") != null;
            var fn = NativeBlend.Apply(nurbs, NativeBlend.Expand(nurbs.Native!, HoleTopEdges(nurbs), true), 1, true, "Fil", errors);
            Console.WriteLine($"        B-spline fillet {sw.ElapsedMilliseconds} ms");
            if (Prof.Enabled) foreach (var (pn, pms, calls) in Prof.Results().OrderByDescending(x => x.ms).Take(12)) Console.WriteLine($"          {pn,-30} {pms,8:0} ms x{calls}");
            Prof.Enabled = false;
            if (Environment.GetEnvironmentVariable("FC_DUMP") is { } dumpDir)
                using (var fs = File.Create(Path.Combine(dumpDir, "nurbs-fillet.step"))) StepWriter.Write(fs, new[] { ("fillet", fn) }, "fillet");
            var fa = NativeBlend.Apply(an, NativeBlend.Expand(an.Native!, HoleTopEdges(an), true), 1, true, "Fil", errors);
            t.True(errors.Count == 0, string.Join("; ", errors));
            t.True(fn.Native != null && fn.Topology.OpenHalfEdges == 0, "B-spline fillet not closed");
            t.True(fn.Native!.Faces.Any(f => f.Surface.Kind == SurfaceKind.Pipe), "no blend face");
            // removed: corner region (1 - π/4) r² swept round the hole at its centroid radius
            var removed = 2 * Math.PI * (6 + (10 - 3 * Math.PI) / (3 * (4 - Math.PI))) * (1 - Math.PI / 4);
            var vn = NTess.ToSolid(fn.Native, 1e-3).Volume(); var va = NTess.ToSolid(fa.Native!, 1e-3).Volume();
            t.Near(va, 14869.03 - removed, 0.3);
            t.Near(vn, va, 0.3);
        });

        t.Test("Thin holes through an imported B-spline cylinder: small crossings in the middle of its face found", () =>
        {
            // a hole narrower than a few of the B-spline face's grid cells is traced on the hole's face (on the
            // B-spline's grid it was only scraps, and r 0.05 came out with no hole at all)
            var res = ReadModel("n-cyl.step", recognize: false);
            var cyl = NTess.ToSolid(res.Solids.Single().Body);
            var v0 = KernelTests.Vex(cyl);
            foreach (var r in new[] { 0.5, 0.2, 0.05, 0.02, 0.01 })
            {
                Csg.ClearCache();
                var cut = Csg.Subtract(cyl, Builders.Cylinder(new Vec3(-15, 0, 15), Vec3.UnitX, r, 30, "Hole"));
                t.True(cut.Native != null && cut.Native.Validate() == null, $"r {r}: exact and valid ({Csg.LastFallbackReason})");
                // a hole across the diameter: pi r^2 20. (Found, not missed: r 0.05 was. The crossings are traced to the
                // boolean's own tolerance, a few per cent of the tiniest holes' size, so within 5 %)
                var dv = v0 - KernelTests.Vex(cut);
                var e = Math.PI * r * r * 20;
                t.True(Math.Abs(dv - e) < 0.05 * e, $"r {r}: removed {dv:R}, about {e:R}");
            }
        });

        t.Test("Boolean through imported B-spline geometry", () =>
        {
            var res = ReadModel("n-cyl.step", recognize: false);
            var cyl = NTess.ToSolid(res.Solids.Single().Body);
            var cut = Csg.Subtract(cyl, Builders.Box(new Vec3(0, -20, 10), new Vec3(20, 20, 20), "Cut"));
            t.True(cut.Native != null && cut.Topology.OpenHalfEdges == 0, $"cut not exact/closed ({Csg.LastFallbackReason})");
            t.Near(NTess.ToSolid(cut.Native!, 1e-3).Volume(), Math.PI * 100 * 30 - Math.PI * 100 / 2 * 10, 5);
        });
    }

    /// <summary>A model file read as written (B-splines kept as B-splines) or with planes, cylinders, ... recognized.</summary>
    static StepImportResult ReadModel(string file, bool recognize)
    {
        var was = StepImport.RecognizeAnalytic;
        StepImport.RecognizeAnalytic = recognize;
        try { return StepImport.Read(File.ReadAllBytes(Path.Combine(Models(), "step", file)), "F1."); }
        finally { StepImport.RecognizeAnalytic = was; }
    }

    static string Models()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "tests", "SpryCad.Tests", "models");
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

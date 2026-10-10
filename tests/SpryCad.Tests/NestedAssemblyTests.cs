using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Sub-assemblies: rigid (moved as one body) and flexible (their mates solved with the assembly's), saved, found, nested.</summary>
static class NestedAssemblyTests
{
    internal static string NewDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "fc-asm-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A cylinder part (radius r, height h, from z = 0 up) saved at path.</summary>
    internal static void Cylinder(string path, double r, double h)
    {
        var d = new CadDocument();
        var sk = new SpryCad.Core.Sketching.Sketch();
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = SpryCad.Core.Sketching.Sketch.OriginId, R = r });
        var s = d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        d.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = h.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        d.Save(path);
    }

    /// <summary>A face of the part at a path: its cylinder, or the planar end facing +Z in the part (end) / -Z (start).</summary>
    internal static string FaceAt(AssemblyModel m, string path, string which)
    {
        var faces = m.DisplayAt(path)!.Mesh.Faces;
        return which switch
        {
            "side" => faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder).Name,
            "end" => faces.First(f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z > 0.9).Name,
            _ => faces.First(f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z < -0.9).Name,
        };
    }

    /// <summary>
    /// A sub-assembly saved in <paramref name="dir"/>/sub: a pin (fixed) and a sleeve on it (concentric: free to slide
    /// along and turn about the pin's axis, Z), the sleeve's start at z = 0.
    /// </summary>
    internal static string PinSleeve(string dir)
    {
        var sd = Path.Combine(dir, "sub");
        string pp = Path.Combine(sd, "Pin.sprt"), sp = Path.Combine(sd, "Sleeve.sprt");
        Cylinder(pp, 5, 10); Cylinder(sp, 5, 10);
        var m = AssemblyModel.New();
        var pin = m.Insert(pp, Transform.Identity);
        var sleeve = m.Insert(sp, Transform.Translation(new Vec3(20, 0, 0)));
        m.AddMate(MateType.Concentric, m.RefTo(sleeve.Id, FaceAt(m, sleeve.Id, "side"))!, m.RefTo(pin.Id, FaceAt(m, pin.Id, "side"))!);
        var path = Path.Combine(sd, "PinSleeve.sasm");
        m.Save(path);
        return path;
    }

    /// <summary>An assembly in a new folder: a base cylinder (fixed at the origin), and the pin and sleeve sub-assembly at x = 100.</summary>
    internal static (AssemblyModel m, Component sub, string dir, string subPath) WithSub(string name, bool flexible)
    {
        var dir = NewDir(name);
        var subPath = PinSleeve(dir);
        var bp = Path.Combine(dir, "Base.sprt");
        Cylinder(bp, 20, 5);
        var m = AssemblyModel.New();
        m.Insert(bp, Transform.Identity);
        var sub = m.InsertAssembly(subPath, Transform.Translation(new Vec3(100, 0, 0)), flexible);
        return (m, sub, dir, subPath);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Sub-assemblies: inserted, their parts placed through them (paths, world placements, previews)", () =>
        {
            var (m, sub, _, _) = WithSub("nested-insert", flexible: false);
            t.True(sub.IsAssembly && !sub.Fixed && sub.Name == "PinSleeve<1>", "a sub-assembly component, floating");
            var paths = m.Occurrences().Select(o => o.Path).ToList();
            t.Eq(string.Join(",", paths), "C1,C2,C2/C1,C2/C2");
            t.Eq(m.PlacedParts().Count(), 3);
            var pin = m.Find("C2/C1")!;
            t.True(Vec3.Distance(pin.World.T, new Vec3(100, 0, 0)) < 1e-9 && pin.Top == sub && pin.Rel == "C1" && !pin.Driven, $"the pin through the sub-assembly ({pin.World.T})");
            t.True(m.Find("C2/C9") == null && m.Find("C1/C1") == null && m.DisplayAt("C2") == null, "no such components");
            t.True(m.RenderThumbnail() != null, "a preview of all three");
        });

        t.Test("Sub-assemblies, rigid: a mate on one of its parts moves it whole, as its file has it", () =>
        {
            var (m, sub, _, _) = WithSub("nested-rigid", flexible: false);
            t.True(!m.HasMates, "a rigid sub-assembly's own mates are not this assembly's");
            var sleeveWas = m.WorldOf("C2/C2").RigidInverse() * m.WorldOf("C2/C1");
            var start = m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!;
            m.AddMate(MateType.Coincident, start, m.RefTo(null, "Front")!, 7);
            t.True(m.LastSolve!.Satisfied && Math.Abs(Math.Abs(m.World(start).P.Z) - 7) < 1e-6, $"7 mm from Front ({m.World(start).P})");
            t.True(Math.Abs(Math.Abs(sub.Placement.T.Z) - 7) < 1e-6, $"the sub-assembly moved ({sub.Placement.T})");
            var sleeveNow = m.WorldOf("C2/C2").RigidInverse() * m.WorldOf("C2/C1");
            t.True(Vec3.Distance(sleeveNow.T, sleeveWas.T) < 1e-9 && sub.Poses == null, "the sleeve stayed where it is on the pin");
            t.Eq(m.Movable("C2/C2"), "C2");
            t.Eq(m.Movable("C2/C1"), "C2");

            // fixed, it cannot move: the mate is reported
            var (m2, sub2, _, _) = WithSub("nested-rigid-fixed", flexible: false);
            sub2.Fixed = true;
            m2.AddMate(MateType.Coincident, m2.RefTo("C2/C2", FaceAt(m2, "C2/C2", "start"))!, m2.RefTo(null, "Front")!, 7);
            t.True(!m2.LastSolve!.Satisfied, "a fixed rigid sub-assembly: the sleeve cannot slide");
            t.True(m2.Movable("C2/C2") == null, "nothing to drag");
        });

        t.Test("Sub-assemblies, flexible: their mates solved with the assembly's, their parts posed for this instance", () =>
        {
            var (m, sub, dir, subPath) = WithSub("nested-flexible", flexible: true);
            sub.Fixed = true;
            t.True(m.HasMates, "its mate counts");
            var start = m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!;
            m.AddMate(MateType.Coincident, start, m.RefTo(null, "Front")!, 7);
            var p = m.World(start).P;
            t.True(m.LastSolve!.Satisfied && Math.Abs(Math.Abs(p.Z) - 7) < 1e-6, $"the sleeve slid 7 mm along the pin ({p})");
            t.True(Math.Abs(p.X - 100) < 1e-6 && Math.Abs(p.Y) < 1e-6, "still on the pin (its own mate holds)");
            t.True(m.LastSolve.Errors.ContainsKey("C2/M1") && m.LastSolve.Errors["C2/M1"] < 1e-6, "the sub-assembly's mate reported under its path");
            t.True(sub.Placement.T == new Vec3(100, 0, 0) && sub.Poses?.ContainsKey("C2") == true && sub.Poses.Count == 1, "the sleeve posed for this instance; nothing else moved");
            t.True(Vec3.Distance(m.WorldOf("C2/C1").T, new Vec3(100, 0, 0)) < 1e-9, "the pin (fixed in it) stays");
            t.Eq(m.Movable("C2/C2"), "C2/C2");
            t.True(m.Movable("C2/C1") == null, "its fixed pin goes with it (fixed)");

            // the sub-assembly's file is untouched
            var own = AssemblyModel.Open(AssemblyDocument.Load(subPath), subPath);
            t.True(Math.Abs(own.Doc.Components[1].Placement.T.Z) < 1e-6, "the sub-assembly's own file keeps the sleeve where it was");

            // saved and opened again: the pose kept, the mates hold where they are
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            var s2 = back.Doc.Components[1];
            t.True(s2.IsAssembly && s2.Flexible && s2.Poses?.Count == 1 && back.Subs.Values.Single().Model != null, "a flexible sub-assembly, found and opened");
            back.SolveMates();
            var was = m.WorldOf("C2/C2");
            t.True(back.LastSolve!.Satisfied && Vec3.Distance(back.WorldOf("C2/C2").T, was.T) < 1e-6 && Math.Abs(back.WorldOf("C2/C2").M00 - was.M00) < 1e-9,
                $"holds as saved ({back.WorldOf("C2/C2").T} vs {was.T})");
            t.True(Math.Abs(Math.Abs(back.World(back.Doc.Mates[0].A).P.Z) - 7) < 1e-6, "the sleeve where it was left");

            // dragged away along the pin and off it: the mates pull it back onto the pin, 7 mm up
            back.SetWorld("C2/C2", Transform.Translation(new Vec3(3, 4, 20)) * back.WorldOf("C2/C2"));
            back.SolveMates();
            var q = back.World(back.Doc.Mates[0].A).P;
            t.True(back.LastSolve.Satisfied && Math.Abs(Math.Abs(q.Z) - 7) < 1e-6 && Math.Abs(q.X - 100) < 1e-6 && Math.Abs(q.Y) < 1e-6, $"back ({q})");

            // made rigid: as its file has it (the pose dropped): the mate can no longer hold (it is fixed)
            back.SetFlexible(s2, false);
            back.SolveMates();
            t.True(s2.Poses == null && !back.LastSolve.Satisfied, "rigid: the sleeve back where the file has it; the mate not satisfied");
        });

        t.Test("Sub-assemblies, flexible: two instances posed apart; a sub-assembly moves its parts with it; locks through it", () =>
        {
            var (m, a, _, subPath) = WithSub("nested-two", flexible: true);
            a.Fixed = true;
            var b = m.InsertAssembly(subPath, Transform.Translation(new Vec3(-100, 0, 0)), flexible: true);
            b.Fixed = true;
            t.True(m.Subs.Count == 1 && b.Part.PartId == a.Part.PartId, "one sub-assembly model for both");
            var sa = m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!;
            var sb = m.RefTo("C3/C2", FaceAt(m, "C3/C2", "start"))!;
            m.AddMate(MateType.Coincident, sa, m.RefTo(null, "Front")!, 4);
            m.AddMate(MateType.Coincident, sb, m.RefTo(null, "Front")!, 9);
            t.True(m.LastSolve!.Satisfied && Math.Abs(Math.Abs(m.World(sa).P.Z) - 4) < 1e-6 && Math.Abs(Math.Abs(m.World(sb).P.Z) - 9) < 1e-6,
                $"each where its own mate puts it ({m.World(sa).P.Z:0.###}, {m.World(sb).P.Z:0.###})");

            // the second, floating and moved: its sleeve goes with it (relative to it), and slides back to its mate
            b.Fixed = false;
            m.Doc.Mates.RemoveAt(1);
            var rel = m.WorldOf("C3").RigidInverse() * m.WorldOf("C3/C2");
            m.SetWorld("C3", Transform.Translation(new Vec3(0, 50, 0)) * m.WorldOf("C3"));
            var rel2 = m.WorldOf("C3").RigidInverse() * m.WorldOf("C3/C2");
            t.True(Vec3.Distance(rel.T, rel2.T) < 1e-9 && Math.Abs(m.WorldOf("C3/C2").T.Y - 50) < 1e-9, "carried with the sub-assembly");

            // a lock between the base and the second sub-assembly's pin (fixed in it): knocked away, it comes back
            var lockA = m.RefTo("C1", FaceAt(m, "C1", "side"))!;
            var lockB = m.RefTo("C3/C1", FaceAt(m, "C3/C1", "side"))!;
            m.AddMate(MateType.Lock, lockB, lockA);
            var held = m.WorldOf("C3/C1");
            m.SetWorld("C3", Transform.Translation(new Vec3(7, -3, 2)) * Transform.Rotation(Vec3.Zero, Vec3.UnitX, 0.3) * m.WorldOf("C3"));
            m.SolveMates();
            var pt = new Vec3(3, 4, 5);
            t.True(m.LastSolve!.Satisfied && Vec3.Distance(m.WorldOf("C3/C1").Point(pt), held.Point(pt)) < 1e-6, "locked through the sub-assembly");
        });

        t.Test("Sub-assemblies, flexible: motion through them (a gear pair turned as a motor turns it, the turns kept with the assembly)", () =>
        {
            // the sub-assembly: a 10 mm and a 5 mm gear (cylinders) on axes along Z, 15 apart, held on them by its origin planes
            var dir = NewDir("nested-gears");
            string ga = Path.Combine(dir, "gears", "Big.sprt"), gb = Path.Combine(dir, "gears", "Small.sprt");
            Cylinder(ga, 10, 5); Cylinder(gb, 5, 5);
            var g = AssemblyModel.New();
            var big = g.Insert(ga, Transform.Identity); var small = g.Insert(gb, Transform.Translation(new Vec3(15, 0, 0)));
            big.Fixed = false;
            MateRef G(Component c, string w) => g.RefTo(c.Id, FaceAt(g, c.Id, w))!;
            g.AddMate(MateType.Coincident, G(big, "side"), g.RefTo(null, "Top")!);
            g.AddMate(MateType.Coincident, G(big, "side"), g.RefTo(null, "Right")!);
            g.AddMate(MateType.Coincident, G(big, "start"), g.RefTo(null, "Front")!);
            g.AddMate(MateType.Coincident, G(small, "side"), g.RefTo(null, "Top")!);
            g.AddMate(MateType.Coincident, G(small, "side"), g.RefTo(null, "Right")!, 15);
            g.AddMate(MateType.Coincident, G(small, "start"), g.RefTo(null, "Front")!);
            g.AddMate(MateType.Gear, G(big, "side"), G(small, "side"));
            var gearsPath = Path.Combine(dir, "gears", "Gears.sasm");
            g.Save(gearsPath);

            // used flexible, fixed somewhere askew in the assembly
            var m = AssemblyModel.New();
            var at = Transform.Translation(new Vec3(50, 20, -5)) * Transform.Rotation(Vec3.Zero, new Vec3(1, 1, 0).Normalized, 0.7);
            var sub = m.InsertAssembly(gearsPath, at, flexible: true);
            t.True(sub.Fixed, "the first component: fixed");
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied, $"its mates hold where it is ({string.Join(", ", m.LastSolve.Failed())})");
            // how far a gear has turned about its axis, in the sub-assembly's frame
            double Turn(string path)
            {
                var rel = m.WorldOf("C1").RigidInverse() * m.WorldOf(path);
                var x = rel.Vector(Vec3.UnitX);
                return Math.Atan2(x.Y, x.X);
            }
            var bigAxis = m.World(m.RefTo("C1/C1", FaceAt(m, "C1/C1", "side"))!);
            double total = 0;
            void Step(double a)
            {
                // the motor's step: the big gear turned about its axis, the rest moved by the mates
                m.SetWorld("C1/C1", Transform.Rotation(bigAxis.P, bigAxis.D, a) * m.WorldOf("C1/C1"));
                var res = m.SolveMates(m.MovableComponents().Where(x => x != "C1/C1").ToHashSet());
                total += a;
                t.True(res.Satisfied, $"geared after {total:0.##} rad");
            }
            var small0 = Turn("C1/C2");
            foreach (var a in new[] { 0.6, 0.9, 1.0, 1.1 }) Step(a);
            // turned 3.6 rad (past half a turn): the small one twice as far the other way, counted on, not wrapped
            double Expected() => Math.IEEERemainder(small0 - 2 * total, 2 * Math.PI);
            t.True(Math.Abs(Math.IEEERemainder(Turn("C1/C2") - Expected(), 2 * Math.PI)) < 1e-4, $"the small gear turned twice as far, back ({Turn("C1/C2"):0.####} vs {Expected():0.####})");
            t.True(sub.Turns?.ContainsKey("M7") == true && sub.Poses?.Count == 2, "the gear's turns and both gears' poses kept with this instance");

            // saved and opened again: it goes on turning from where it was
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            m = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied, "holds as saved");
            Step(0.8); Step(0.7);
            t.True(Math.Abs(Math.IEEERemainder(Turn("C1/C2") - Expected(), 2 * Math.PI)) < 1e-4, $"after reopening too ({Turn("C1/C2"):0.####} vs {Expected():0.####})");
        });

        t.Test("Sub-assemblies: nested two deep, flexible all the way down; not inside themselves", () =>
        {
            var dir = NewDir("nested-deep");
            var subPath = PinSleeve(dir);
            // the middle assembly: the pin and sleeve flexible in it, fixed, with nothing else
            var mid = AssemblyModel.New();
            var inner = mid.InsertAssembly(subPath, Transform.Translation(new Vec3(0, 30, 0)), flexible: true);
            var midPath = Path.Combine(dir, "Mid.sasm");
            mid.Save(midPath);

            var bp = Path.Combine(dir, "Base.sprt");
            Cylinder(bp, 20, 5);
            var top = AssemblyModel.New();
            top.Insert(bp, Transform.Identity);
            var c = top.InsertAssembly(midPath, Transform.Translation(new Vec3(100, 0, 0)), flexible: true);
            c.Fixed = true;
            t.Eq(string.Join(",", top.Occurrences().Select(o => o.Path)), "C1,C2,C2/C1,C2/C1/C1,C2/C1/C2");
            t.True(Vec3.Distance(top.WorldOf("C2/C1/C1").T, new Vec3(100, 30, 0)) < 1e-9, "placed through both");
            t.True(top.HasMates && top.Find("C2/C1/C2")!.Driven, "flexible all the way down: the sleeve is this assembly's to place");
            var start = top.RefTo("C2/C1/C2", FaceAt(top, "C2/C1/C2", "start"))!;
            top.AddMate(MateType.Coincident, start, top.RefTo(null, "Front")!, 6);
            var p = top.World(start).P;
            t.True(top.LastSolve!.Satisfied && Math.Abs(Math.Abs(p.Z) - 6) < 1e-6 && Math.Abs(p.X - 100) < 1e-6 && Math.Abs(p.Y - 30) < 1e-6, $"slid on its pin, two levels down ({p})");
            t.True(c.Poses?.Keys.SequenceEqual(new[] { "C1/C2" }) == true, $"posed by its path in the sub-assembly ({string.Join(", ", c.Poses?.Keys.ToArray() ?? Array.Empty<string>())})");

            // the middle one rigid in the top: the inner one is placed as the middle file has it (rigid too)
            top.SetFlexible(c, false);
            t.True(!top.Find("C2/C1/C2")!.Driven, "rigid: nothing below it is this assembly's to place");

            // an assembly cannot hold itself, nor one that holds it
            var topPath = Path.Combine(dir, "Top.sasm");
            top.Save(topPath);
            var reopenedMid = AssemblyModel.Open(AssemblyDocument.Load(midPath), midPath);
            bool threw = false;
            try { reopenedMid.InsertAssembly(topPath, Transform.Identity); } catch (InvalidOperationException) { threw = true; }
            t.True(threw && reopenedMid.Doc.Components.Count == 1, "the top (which holds the middle) refused in the middle");
            threw = false;
            try { reopenedMid.InsertAssembly(midPath, Transform.Identity); } catch (InvalidOperationException) { threw = true; }
            t.True(threw, "the middle refused in itself");
        });

        t.Test("Sub-assemblies: kept up with their files (a sub-assembly or a part in one saved with changes shown again, nothing else read)", () =>
        {
            var (m, sub, dir, subPath) = WithSub("nested-reload", flexible: false);
            var files = m.SourceFiles().Select(Path.GetFileName).OrderBy(x => x).ToList();
            t.Eq(string.Join(",", files), "Base.sprt,Pin.sprt,PinSleeve.sasm,Sleeve.sprt");
            var pinPath = Path.Combine(dir, "sub", "Pin.sprt");

            // touched, or another file: nothing
            File.SetLastWriteTimeUtc(subPath, DateTime.UtcNow);
            t.True(m.ReloadChanged(new[] { subPath, Path.Combine(dir, "Other.sprt") }, defer: false).Count == 0, "touched, unchanged: not reloaded");
            var oldModel = m.Subs.Values.Single().Model;

            // the sub-assembly saved with a third component: shown with it
            var own = AssemblyModel.Open(AssemblyDocument.Load(subPath), subPath);
            own.Insert(pinPath, Transform.Translation(new Vec3(0, 40, 0)));
            own.Save(subPath);
            var changed = m.ReloadChanged(new[] { subPath }, defer: false);
            t.True(changed.SequenceEqual(new[] { "PinSleeve<1>" }) && m.Subs.Values.Single().Model != oldModel, $"reloaded ({string.Join(", ", changed)})");
            t.Eq(m.PlacedParts().Count(), 4);
            t.True(Vec3.Distance(m.WorldOf("C2/C3").T, new Vec3(100, 40, 0)) < 1e-9, "the new pin where the sub-assembly put it");
            t.True(m.ReloadChanged(new[] { subPath }, defer: false).Count == 0, "again: already as saved");

            // a part inside it saved bigger: its display there rebuilt, the sub-assembly not opened again
            var subModel = m.Subs.Values.Single().Model;
            var before = m.DisplayAt("C2/C1")!.Mesh.Bounds;
            var pin = CadDocument.Load(pinPath);
            var sketch = pin.Features.OfType<SketchFeature>().Single();
            ((SpryCad.Core.Sketching.SkCircle)sketch.Sketch.Entities.Single(e => e is SpryCad.Core.Sketching.SkCircle)).R = 8;
            pin.Save(pinPath);
            changed = m.ReloadChanged(new[] { pinPath }, defer: false);
            var after = m.DisplayAt("C2/C1")!.Mesh.Bounds;
            t.True(changed.SequenceEqual(new[] { "PinSleeve<1>/Pin<1>" }) && m.Subs.Values.Single().Model == subModel, $"the part reloaded in place ({string.Join(", ", changed)})");
            t.True(Math.Abs(after.Max.X - 8) < 1e-3 && Math.Abs(before.Max.X - 5) < 1e-3 && m.DisplayAt("C2/C3")!.Mesh.Bounds.Max.X > 7.99, "both pins bigger");

            // deferred (as the app does): shown as it was meanwhile, built by the pending jobs
            pin.Save(pinPath);
            changed = m.ReloadChanged(new[] { pinPath });
            var jobs = m.PendingJobs();
            t.True(changed.Count == 1 && jobs.Count == 1 && jobs[0].Owner == subModel, "a job for the sub-assembly's part");
            t.True(m.Install(jobs[0], jobs[0].Build()) && !m.AnyPending, "built and shown");
        });

        t.Test("Sub-assemblies: found again when moved (by identity), shown missing when not; removed with their mates", () =>
        {
            var (m, sub, dir, subPath) = WithSub("nested-find", flexible: true);
            m.AddMate(MateType.Coincident, m.RefTo("C2/C2", FaceAt(m, "C2/C2", "start"))!, m.RefTo(null, "Front")!, 3);
            var path = Path.Combine(dir, "Top.sasm");
            m.Save(path);
            t.True(sub.Part.Assembly && sub.Part.Path == "sub/PinSleeve.sasm", $"linked relative ({sub.Part.Path})");
            t.Eq(DocumentContainer.ReadIdentity(subPath).id, sub.Part.PartId);

            // the sub-assembly's folder renamed: found by identity in the assembly's folder
            Directory.Move(Path.Combine(dir, "sub"), Path.Combine(dir, "moved"));
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            var res = back.Resolution.Of(back.Doc.Components[1]);
            t.True(res.Status == PartStatus.Moved && back.Subs.Values.Single().Model is { } sm && sm.Parts.Count == 2, $"found, its parts too ({res.Status})");
            back.SolveMates();
            t.True(back.LastSolve!.Satisfied, "its mates hold");

            // removed: its mates go with it
            back.Remove(back.Doc.Components[1]);
            t.True(back.Doc.Mates.Count == 0 && back.Subs.Count == 0, "the mate on its sleeve went with it");
            // undone (the document as it was): shown again
            var before = AssemblyDocument.FromJson(m.Doc.ToJson());
            back.Doc.Components = before.Components; back.Doc.Mates = before.Mates;
            back.Sync();
            t.True(back.Subs.Count == 1 && back.PlacedParts().Count() == 3, "shown again after undo");

            // gone: shown from the copy of it the assembly kept when saved
            Directory.Delete(Path.Combine(dir, "moved"), true);
            var gone = AssemblyModel.Open(AssemblyDocument.Load(path), path);
            t.True(gone.Subs.Values.Single() is { Source: SubSource.Cache, Problem: not null } && gone.PlacedParts().Count(p => p.pd.Mesh.Indices.Length > 0) == 3, "missing: shown from the copy the assembly kept");
        });
    }
}

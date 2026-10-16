using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Assemblies: part identity, the assembly document and its caches, finding parts again (renamed, moved, missing).</summary>
static class AssemblyTests
{
    static string NewDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "fc-asm-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A part saved at <paramref name="path"/> (folders made as needed); its identity.</summary>
    static CadDocument SavePart(string path, CadDocument? doc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        doc ??= new CadDocument();
        doc.Save(path);
        return doc;
    }

    static PartRef Ref(CadDocument part, string asmDir, string partPath) => new()
    {
        PartId = part.Id, Revision = part.Revision, AbsolutePath = Path.GetFullPath(partPath),
        Path = ComponentResolver.Relative(asmDir, Path.GetFullPath(partPath)),
    };

    /// <summary>A cylinder part (radius r, height h) saved at path.</summary>
    static CadDocument Cylinder(string path, double r, double h, bool identity = true)
    {
        var d = new CadDocument();
        var sk = new SpryCad.Core.Sketching.Sketch();
        sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = SpryCad.Core.Sketching.Sketch.OriginId, R = r });
        var s = d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        d.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = h.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (identity) d.Save(path);
        else File.WriteAllText(path, d.ToJson());
        return d;
    }

    /// <summary>A pin (cylinder r 5, 10 long along +Z) fixed, and a sleeve of the same size floating, placed askew.</summary>
    static (AssemblyModel m, Component pin, Component sleeve, string dir) PinAndSleeve(string name)
    {
        var dir = NewDir(name);
        string pp = Path.Combine(dir, "Pin.sprt"), sp = Path.Combine(dir, "Sleeve.sprt");
        Cylinder(pp, 5, 10); Cylinder(sp, 5, 10);
        var m = AssemblyModel.New();
        var pin = m.Insert(pp, Transform.Identity);
        var askew = Transform.Translation(new Vec3(40, 7, 3)) * Transform.Rotation(Vec3.Zero, new Vec3(1, 2, 0.5), 0.6);
        var sleeve = m.Insert(sp, askew);
        return (m, pin, sleeve, dir);
    }

    /// <summary>A component's face of a kind: its cylinder, or the planar end whose normal points along +Z (end) / -Z (start) in the part.</summary>
    static string FaceOf(AssemblyModel m, Component c, string which)
    {
        var faces = m.DisplayOf(c)!.Mesh.Faces;
        return which switch
        {
            "side" => faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder).Name,
            "end" => faces.First(f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z > 0.9).Name,
            _ => faces.First(f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z < -0.9).Name,
        };
    }

    /// <summary>The angle a component has turned about the world Z axis through c, from a point of its part on +X.</summary>
    static double TurnedAboutZ(Component comp, Vec3 c)
    {
        var p = comp.Placement.ToTransform().Point(new Vec3(10, 0, 0)) - comp.Placement.ToTransform().Point(Vec3.Zero);
        return Math.Atan2(p.Y, p.X);
    }

    /// <summary>How much two placed components' solids overlap (mm³).</summary>
    static double Overlap(AssemblyModel m, Component a, Component b)
    {
        NBody Solid(Component c) => new SpryCad.Core.Regen.Regenerator().Rebuild(m.DisplayOf(c)!.Doc!).State.Bodies.Single().Solid.Native!.Transformed(c.Placement.ToTransform());
        var common = NBoolean.Run(Solid(a), Solid(b), BoolOp.Intersect);
        return common.Faces.Count == 0 ? 0 : NMass.Of(common)!.Volume;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Assembly previews: saved in the file (a PNG, stored as Explorer's handler reads it), every component as placed", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "hinge.sasm").build();
            var path = Path.Combine(NewDir("thumbs"), "Hinge.sasm");
            m.Save(path);
            var png = AssemblyDocument.Load(path).Thumbnail;
            t.True(png != null && png.Length > 1000 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G', "a PNG preview");
            // as the handler reads it: one query, plain SQLite
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Png FROM Thumbnail ORDER BY Size DESC LIMIT 1";
                t.True(cmd.ExecuteScalar() is byte[] stored && stored.SequenceEqual(png!), "stored as a plain PNG (the handler reads it with one query)");
            }
            // the placed meshes: each component where it is (the second leaf turned half round: its box mirrored about x = 20)
            var leaf2 = m.Doc.Components[1];
            var placed = AssemblyModel.PlacedMesh(m.DisplayOf(leaf2)!.Mesh, leaf2.Placement.ToTransform());
            var own = m.DisplayOf(leaf2)!.Mesh.Bounds;
            t.True(Math.Abs(placed.Bounds.Max.X - (40 - own.Min.X)) < 1e-4 && Math.Abs(placed.Bounds.Min.Y - (64 - own.Max.Y)) < 1e-4, $"placed ({placed.Bounds.Min} .. {placed.Bounds.Max})");
            // without anything to show: no preview
            t.True(AssemblyModel.New().RenderThumbnail() == null, "an empty assembly has none");
        });

        t.Test("STEP assemblies: the hinge written and read back (parts once, placed); as an assembly: embedded parts, each fixed where it was", () =>
        {
            var hinge = Demos.Assemblies.First(x => x.file == "hinge.sasm").build();
            var step = Demos.AssemblyStep(hinge, "Hinge");
            t.True(StepImport.IsAssembly(step), "an assembly");
            var res = StepImport.ReadAssembly(step, "S.")!;
            t.True(res.Parts.Count == 2 && res.Occurrences.Count == 3 && res.Name == "Hinge", $"{res.Parts.Count} parts, {res.Occurrences.Count} uses, '{res.Name}'");
            var q = new Vec3(3, 4, 5);
            for (int i = 0; i < 3; i++)
                t.True(Vec3.Distance(res.Occurrences[i].Placement.Point(q), hinge.Doc.Components[i].Placement.ToTransform().Point(q)) < 1e-6, $"use {i + 1} placed as it was");

            var m = AssemblyModel.FromStep(step, "Hinge")!;
            t.Eq(m.Doc.Components.Count, 3);
            t.True(m.Doc.Components.All(c => c.Part.Embedded != null) && m.Parts.Count == 2, "embedded, a part each");
            t.True(m.Doc.Components.All(c => c.Fixed) && m.Doc.Mates.Count == 0, "every component fixed where the file puts it, no mates");
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied && m.LastSolve.Moved.Count == 0, "nothing moves");
            for (int i = 0; i < 3; i++)
            {
                t.True(Vec3.Distance(m.Doc.Components[i].Placement.ToTransform().Point(q), hinge.Doc.Components[i].Placement.ToTransform().Point(q)) < 1e-6, $"component {i + 1} where it was");
                var v0 = hinge.DisplayOf(hinge.Doc.Components[i])!.Mesh.Volume; var v1 = m.DisplayOf(m.Doc.Components[i])!.Mesh.Volume;
                t.True(Math.Abs(v1 - v0) < 1e-3 * v0, $"component {i + 1}'s volume {v1:0.###} vs {v0:0.###}");
            }
            // saved and opened again, from itself alone
            var path = Path.Combine(NewDir("step-asm"), "Hinge.sasm");
            m.Save(path);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path, new PartIndex());
            t.True(back.Doc.Components.All(c => back.DisplayOf(c)!.Source == PartSource.Embedded && back.DisplayOf(c)!.Problem == null), "reopened");

            // written the other way round (the parent's shape as the relationship's first representation, the
            // transformation's items swapped to match): the same placements
            var text = System.Text.Encoding.ASCII.GetString(step);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"REPRESENTATION_RELATIONSHIP\('',\s*'',\s*(#\d+),\s*(#\d+)\)", "REPRESENTATION_RELATIONSHIP('','',$2,$1)");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"ITEM_DEFINED_TRANSFORMATION\('',\s*'',\s*(#\d+),\s*(#\d+)\)", "ITEM_DEFINED_TRANSFORMATION('','',$2,$1)");
            var swapped = StepImport.ReadAssembly(System.Text.Encoding.ASCII.GetBytes(text), "S.")!;
            for (int i = 0; i < 3; i++)
                t.True(Vec3.Distance(swapped.Occurrences[i].Placement.Point(q), hinge.Doc.Components[i].Placement.ToTransform().Point(q)) < 1e-6, $"swapped: use {i + 1} placed as it was");

            // a part's STEP is not an assembly
            using var ms = new MemoryStream();
            StepWriter.Write(ms, new[] { ("Pin", new SpryCad.Core.Regen.Regenerator().Rebuild(hinge.Parts.Values.First().Doc!).State.Bodies[0].Solid) }, "Pin");
            t.True(!StepImport.IsAssembly(ms.ToArray()) && AssemblyModel.FromStep(ms.ToArray(), "Pin") == null, "a part's STEP is not an assembly");
        });

        t.Test("A part embedded in an embedded sub-assembly saved back: into the sub-assembly's file, that into the assembly's, shown as it now is", () =>
        {
            var m = AssemblyModel.FromStep(Demos.AssemblyStep(Demos.Assemblies.First(x => x.file == "hinge.sasm").build(), "Hinge"), "Hinge")!;
            var sub = m.FormSubAssembly(m.Doc.Components.Skip(1).ToList(), Path.Combine(NewDir("nested-embed"), "Leaves.sasm"), flexible: false, embed: true);
            var inner = m.Occurrences().First(o => o.Path.StartsWith(sub.Id + "/", StringComparison.Ordinal) && !o.Component.IsAssembly);
            t.True(m.EmbeddedAllTheWay(sub.Id), "the sub-assembly is embedded");
            var name = inner.Component.Part.Embedded!;
            var doc = DocumentContainer.Load(m.NestedEmbedded(sub.Id, name)!, name);
            doc.Color = new[] { 0.9, 0.1, 0.1 };
            var subFile = m.Doc.Files[sub.Part.Embedded!];
            m.UpdateNestedEmbedded(sub.Id, name, DocumentContainer.ToNewBytes(doc, "x"));
            t.True(DocumentContainer.Load(m.NestedEmbedded(sub.Id, name)!, name).Color[0] == 0.9, "saved into the sub-assembly");
            t.True(!ReferenceEquals(m.Doc.Files[sub.Part.Embedded!], subFile) && AssemblyContainer.Load(m.Doc.Files[sub.Part.Embedded!], "s.sasm").Revision == sub.Part.Revision,
                "the sub-assembly's file saved into the assembly, its revision noted");
            t.True(m.Find(inner.Path)!.Display!.Color[0] == 0.9, "shown as it now is");
        });

        t.Test("Lead screw assembly demo: the nut on the screw (a Screw mate of the thread's lead) fits; turned, it travels and still fits", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "lead-screw.sasm").build();
            var (screw, nut) = (m.Doc.Components[0], m.Doc.Components[1]);
            t.True(m.LastSolve!.Satisfied && m.LastSolve.Moved.Count == 0, "the nut fits where it was put: the mate holds as placed");
            t.True(Overlap(m, screw, nut) < 1e-6, $"fits ({Overlap(m, screw, nut):0.######} mm3)");
            // turned 100 degrees (a solve places it on the thread): it has travelled along, the screw's own way round
            var mate = m.Doc.Mates.Single();
            var axis = m.World(mate.A);
            var z0 = Vec3.Dot(nut.Placement.ToTransform().T, axis.D);
            nut.Placement = Placement.From(Transform.Rotation(axis.P, axis.D, 100 * Math.PI / 180) * nut.Placement.ToTransform());
            m.SolveMates();
            var travel = Vec3.Dot(nut.Placement.ToTransform().T, axis.D) - z0;
            t.True(m.LastSolve!.Satisfied && Math.Abs(travel) > 0.5, $"travelled {travel:0.###} mm");
            var v = Overlap(m, screw, nut);
            t.True(v < 1e-6, $"still fits after turning ({v:0.######} mm3): the mate's lead and hand are the thread's");
        });

        t.Test("Gear pair demo: meshing, turning together the other way round, the teeth clear of each other", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "gears.sasm").build();
            var (g1, g2) = (m.Doc.Components[0], m.Doc.Components[1]);
            t.True(m.LastSolve!.Satisfied, $"the mates hold ({string.Join(", ", m.LastSolve.Failed())})");
            t.True(Overlap(m, g1, g2) < 1e-6, $"meshing clear ({Overlap(m, g1, g2):0.######} mm3)");
            double Angle(Component c) { var tr = c.Placement.ToTransform(); var x = tr.Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
            var (a1, a2) = (Angle(g1), Angle(g2));
            g1.Placement = Placement.From(Transform.Rotation(Vec3.Zero, Vec3.UnitZ, 0.13) * g1.Placement.ToTransform());
            m.SolveMates(new HashSet<string> { g2.Id });
            var d1 = Math.IEEERemainder(Angle(g1) - a1, 2 * Math.PI); var d2 = Math.IEEERemainder(Angle(g2) - a2, 2 * Math.PI);
            t.True(m.LastSolve!.Satisfied && Math.Abs(d1 - 0.13) < 1e-9 && Math.Abs(d2 + 0.13) < 1e-6, $"turned {d1:0.####}, the other {d2:0.####}");
            t.True(Overlap(m, g1, g2) < 1e-6, $"still clear ({Overlap(m, g1, g2):0.######} mm3)");
        });

        t.Test("Linkage demo: the mates hold as placed; the drive gear turned (dragged), the crank, rocker and slider follow the kinematics", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "linkage.sasm").build();
            var bad = m.Parts.Values.Where(p => p.Problem != null || p.Mesh.Indices.Length == 0).Select(p => p.Problem ?? "empty").ToList();
            t.True(bad.Count == 0, $"the parts build ({string.Join("; ", bad)})");
            var warned = m.Parts.Values.SelectMany(p =>
            {
                var r = new SpryCad.Core.Regen.Regenerator().Rebuild(p.Doc!);
                return p.Doc!.Features.Where(f => !r.Status[f.Id].Ok || r.Status[f.Id].Warnings.Count > 0)
                    .Select(f => $"{f.Name}: {r.Status[f.Id].Error} {string.Join("; ", r.Status[f.Id].Warnings)}");
            }).ToList();
            t.True(warned.Count == 0, $"no feature fails or warns ({string.Join(" | ", warned)})");
            t.True(m.LastSolve!.Satisfied && m.LastSolve.Moved.Count == 0,
                $"the mates hold where the parts were put ({m.Doc.Mates.Count} mates; failed: {string.Join(", ", m.LastSolve.Failed().Select(id => m.Doc.Mates.First(x => x.Id == id).Type))}; moved {m.LastSolve.Moved.Count})");
            Component C(string name) => m.Doc.Components.First(c => c.Name.StartsWith(name));
            var (crank, slider, g2) = (C("Crank"), C("Slider"), m.Doc.Components.Last(c => c.Name.StartsWith("Gear")));
            double CrankAngle() { var x = crank.Placement.ToTransform().Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
            // the second gear's rim dragged round (seen down Z) by steps: the crank turns the other way, 1:1
            var g2c = g2.Placement.ToTransform().Point(Vec3.Zero);
            var rim = new Vec3(30, 0, 5);
            double before = CrankAngle(), turned = 0;
            for (int i = 0; i < 12; i++)
            {
                var w = g2.Placement.ToTransform().Point(rim) - g2c;
                var to = Math.Atan2(w.Y, w.X) + 0.12;
                m.SolveDrag(new MateSolver.Pull(g2.Id, rim, g2c + new Vec3(30 * Math.Cos(to), 30 * Math.Sin(to), 50), -Vec3.UnitZ));
                turned += 0.12;
                t.True(m.LastSolve!.Satisfied, $"step {i}: the mates hold ({string.Join(", ", m.LastSolve.Failed().Select(id => m.Doc.Mates.First(x => x.Id == id).Type))})");
            }
            var crankTurn = Math.IEEERemainder(CrankAngle() - before, 2 * Math.PI);
            t.True(Math.Abs(crankTurn + turned) < 1e-3, $"the crank turned {crankTurn:0.####} for the gear's {turned:0.####}");
            var (_, b, c) = Demos.LinkageJoints(CrankAngle());
            var sx = slider.Placement.ToTransform().Point(Vec3.Zero).X;
            t.True(Math.Abs(sx - c.X) < 1e-4, $"the slider at {sx:0.####} (kinematics: {c.X:0.####})");
            var rocker = C("Rocker").Placement.ToTransform();
            var bAt = rocker.Point(new Vec3(70, 0, 0));
            t.True(Math.Abs(bAt.X - b.X) < 1e-4 && Math.Abs(bAt.Y - b.Y) < 1e-4, $"the rocker's end at ({bAt.X:0.###}, {bAt.Y:0.###}) (kinematics: ({b.X:0.###}, {b.Y:0.###}))");

            // a motor on the second gear (as the Motor page steps it: turned about its bore's axis and held while the
            // rest is solved), two whole turns: the mechanism goes right round and comes back where it was
            var bore = m.Doc.Mates.First(x => x.Type == MateType.Gear).B;
            var others = m.Doc.Components.Where(x => !x.Fixed && x != g2).Select(x => x.Id).ToHashSet();
            var start = (CrankAngle(), slider.Placement.ToTransform().Point(Vec3.Zero).X);
            double worst = 0;
            int steps = 0;
            for (double turned2 = 0; turned2 < 4 * Math.PI - 1e-9; turned2 += Math.PI / 30, steps++)
            {
                var ax = m.World(bore);
                g2.Placement = Placement.From(Transform.Rotation(ax.P, ax.D, Math.PI / 30) * g2.Placement.ToTransform());
                var res = m.SolveMates(others);
                if (!res.Satisfied) { t.True(false, $"motor step {steps}: the mates hold ({string.Join(", ", res.Failed())})"); break; }
                var (_, _, cc) = Demos.LinkageJoints(CrankAngle());
                worst = Math.Max(worst, Math.Abs(slider.Placement.ToTransform().Point(Vec3.Zero).X - cc.X));
            }
            t.True(worst < 1e-4, $"{steps} motor steps: the slider where the kinematics put it throughout (off by {worst:0.######})");
            var end = (CrankAngle(), slider.Placement.ToTransform().Point(Vec3.Zero).X);
            t.True(Math.Abs(Math.IEEERemainder(end.Item1 - start.Item1, 2 * Math.PI)) < 1e-4 && Math.Abs(end.Item2 - start.Item2) < 1e-4,
                $"two turns: back where it started (crank {start.Item1:0.####} -> {end.Item1:0.####}, slider {start.Item2:0.###} -> {end.Item2:0.###})");
        });

        t.Test("Mates: tangent, lock, distance limits, gear and screw", () =>
        {
            // tangent: the sleeve lying on the Top plane (its axis parallel to it, a radius above)
            var (m, pin, sleeve, dir) = PinAndSleeve("more-mates");
            var side = m.RefTo(sleeve.Id, FaceOf(m, sleeve, "side"))!;
            m.AddMate(MateType.Tangent, side, m.RefTo(null, "Top")!);
            var ax = m.World(side);
            t.True(m.LastSolve!.Satisfied && Math.Abs(ax.D.Y) < 1e-9 && Math.Abs(Math.Abs(ax.P.Y) - 5) < 1e-6, $"on the plane ({ax.P}, {ax.D})");

            // lock: knocked away, it comes back to where it was locked
            var (m2, p2, s2, _) = PinAndSleeve("lock");
            var locked = s2.Placement.ToTransform();
            m2.AddMate(MateType.Lock, m2.RefTo(s2.Id, FaceOf(m2, s2, "side"))!, m2.RefTo(p2.Id, FaceOf(m2, p2, "side"))!);
            s2.Placement = Placement.From(Transform.Translation(new Vec3(5, -3, 2)) * Transform.Rotation(Vec3.Zero, Vec3.UnitX, 0.4) * locked);
            m2.SolveMates();
            var q = new Vec3(3, 4, 5);
            t.True(m2.LastSolve!.Satisfied && Vec3.Distance(s2.Placement.ToTransform().Point(q), locked.Point(q)) < 1e-6, "locked");

            // distance limits: 2 to 8 mm along the pin's axis above its end
            var (m3, p3, s3, _) = PinAndSleeve("limits");
            MateRef R3(Component c, string w) => m3.RefTo(c.Id, FaceOf(m3, c, w))!;
            m3.AddMate(MateType.Concentric, R3(s3, "side"), R3(p3, "side"));
            // (above the pin, facing it: the side and alignment given, not taken from where the sleeve happens to be)
            m3.AddMate(MateType.Coincident, R3(s3, "start"), R3(p3, "end"), 5, mm => { mm.Min = 2; mm.Max = 8; mm.Side = 1; mm.Aligned = false; });
            double Gap() => m3.World(R3(s3, "start")).P.Z - 10;
            t.True(m3.LastSolve!.Satisfied && Gap() > 2 - 1e-6 && Gap() < 8 + 1e-6, $"within the limits ({Gap():0.###})");
            void Lift(double dz) => s3.Placement = Placement.From(Transform.Translation(new Vec3(0, 0, dz)) * s3.Placement.ToTransform());
            Lift(5 - Gap()); m3.SolveMates();
            t.True(m3.LastSolve!.Satisfied && Math.Abs(Gap() - 5) < 1e-6, $"inside: stays ({Gap():0.######})");
            Lift(12 - Gap()); m3.SolveMates();
            t.True(m3.LastSolve!.Satisfied && Math.Abs(Gap() - 8) < 1e-5, $"past the top: held at 8 ({Gap():0.######})");

            // gear: a 10 mm and a 5 mm cylinder on axes along Z (15 apart); the small one turns twice as fast, the other way
            var gd = NewDir("gear");
            string ga = Path.Combine(gd, "Big.sprt"), gb = Path.Combine(gd, "Small.sprt");
            Cylinder(ga, 10, 5); Cylinder(gb, 5, 5);
            var mg = AssemblyModel.New();
            var big = mg.Insert(ga, Transform.Identity); var small = mg.Insert(gb, Transform.Translation(new Vec3(15, 0, 0)));
            big.Fixed = false;
            MateRef G(Component c, string w) => mg.RefTo(c.Id, FaceOf(mg, c, w))!;
            mg.AddMate(MateType.Coincident, G(big, "side"), mg.RefTo(null, "Top")!);
            mg.AddMate(MateType.Coincident, G(big, "side"), mg.RefTo(null, "Right")!);
            mg.AddMate(MateType.Coincident, G(big, "start"), mg.RefTo(null, "Front")!);
            mg.AddMate(MateType.Coincident, G(small, "side"), mg.RefTo(null, "Top")!);
            mg.AddMate(MateType.Coincident, G(small, "side"), mg.RefTo(null, "Right")!, 15);
            mg.AddMate(MateType.Coincident, G(small, "start"), mg.RefTo(null, "Front")!);
            var gear = mg.AddMate(MateType.Gear, G(big, "side"), G(small, "side"));
            t.True(mg.LastSolve!.Satisfied && Math.Abs(gear.RatioA - 10) < 1e-9 && Math.Abs(gear.RatioB - 5) < 1e-9, "the ratio from the radii");
            double total = 0;
            foreach (var step in new[] { 0.5, 0.8, 1.1, 1.3, -0.4 })
            {
                big.Placement = Placement.From(Transform.Rotation(Vec3.Zero, Vec3.UnitZ, step) * big.Placement.ToTransform());
                mg.SolveMates(new HashSet<string> { small.Id });
                total += step;
                t.True(mg.LastSolve!.Satisfied, $"geared after {total:0.##} rad");
            }
            var expect = Math.IEEERemainder(-2 * total, 2 * Math.PI);
            var got = TurnedAboutZ(small, new Vec3(15, 0, 0));
            t.True(Math.Abs(Math.IEEERemainder(got - expect, 2 * Math.PI)) < 1e-5 && Math.Abs(gear.AngleB + 2 * total) < 1e-5, $"the small one turned {gear.AngleB:0.####} for {total:0.####} (counted past a turn)");

            // dragged: a point on the big one's rim pulled round by the cursor (seen down Z) follows it either way, the
            // small one turning with it at the ratio
            var rim = big.Placement.ToTransform().RigidInverse().Point(new Vec3(10 * Math.Cos(total), 10 * Math.Sin(total), 2.5));
            double At() { var w = big.Placement.ToTransform().Point(rim); return Math.Atan2(w.Y, w.X); }
            foreach (var step in new[] { 0.15, 0.15, 0.15, -0.1, -0.1, -0.1, -0.1, -0.1 })
            {
                var to = At() + step;
                // the cursor a little off the rim (the ray need not pass through the point)
                var cursor = new Vec3(12 * Math.Cos(to), 12 * Math.Sin(to), 50);
                var a0 = gear.AngleA;
                mg.SolveDrag(new MateSolver.Pull(big.Id, rim, cursor, -Vec3.UnitZ));
                var turned = gear.AngleA - a0;
                t.True(mg.LastSolve!.Satisfied && Math.Abs(Math.IEEERemainder(At() - to, 2 * Math.PI)) < 1e-4
                    && Math.Abs(turned - step) < 1e-4 && Math.Abs(10 * gear.AngleA + 5 * gear.AngleB) < 1e-6,
                    $"dragged {step:0.##}: turned {turned:0.######}, the small one at the ratio ({gear.AngleA:0.####}, {gear.AngleB:0.####})");
            }

            // screw: a nut (5 mm cylinder, 10 long) on a pin, lead 6: turned by hand and let go, it settles on the
            // thread (least motion: part of the turn kept, the advance to match), along the axis the lead per turn;
            // a left-hand thread the other way
            foreach (var left in new[] { false, true })
            {
                var (ms, ps, ns, _) = PinAndSleeve(left ? "screw-lh" : "screw");
                var screw = ms.AddMate(MateType.Screw, ms.RefTo(ps.Id, FaceOf(ms, ps, "side"))!, ms.RefTo(ns.Id, FaceOf(ms, ns, "side"))!, 6, mm => mm.Reverse = left);
                t.True(ms.LastSolve!.Satisfied, "on the axis");
                var d = ms.World(screw.A).D;
                (double turn, double along) State()
                {
                    var tr = ns.Placement.ToTransform();
                    return (Math.Atan2(Vec3.Dot(Vec3.Cross(Vec3.UnitX, tr.Vector(Vec3.UnitX)), d), Vec3.Dot(Vec3.UnitX, tr.Vector(Vec3.UnitX))), Vec3.Dot(tr.Point(Vec3.Zero), d));
                }
                var (th0, s0) = State();
                ns.Placement = Placement.From(Transform.Rotation(Vec3.Zero, d, Math.PI / 2) * ns.Placement.ToTransform());
                ms.SolveMates();
                var (th1, s1) = State();
                double dth = Math.IEEERemainder(th1 - th0, 2 * Math.PI);
                double advance = (left ? -1 : 1) * 6 * dth / (2 * Math.PI);
                t.True(ms.LastSolve!.Satisfied && dth > 0.1 && Math.Abs((s1 - s0) - advance) < 1e-6,
                    $"{(left ? "left" : "right")} hand: turned {dth:0.####} rad, advanced {s1 - s0:0.######} (expected {advance:0.######})");
            }
        });

        t.Test("Mates edited: a coincident's offset changed in place (same id, name and place in the list), then removed; old Distance mates load as offsets", () =>
        {
            var (m, pin, sleeve, _) = PinAndSleeve("mateedit");
            MateRef R(Component c, string which) => m.RefTo(c.Id, FaceOf(m, c, which))!;
            m.AddMate(MateType.Concentric, R(sleeve, "side"), R(pin, "side"));
            var dist = m.AddMate(MateType.Coincident, R(sleeve, "start"), R(pin, "end"), 5);
            var lock0 = m.AddMate(MateType.Parallel, R(sleeve, "start"), m.RefTo(null, "Front")!);
            double Z() => m.World(R(sleeve, "start")).P.Z;
            t.True(m.LastSolve!.Satisfied && Math.Abs(Math.Abs(Z() - 10) - 5) < 1e-6, $"5 mm from the end ({Z():0.######})");
            var edited = m.ReplaceMate(dist, MateType.Coincident, dist.A.Clone(), dist.B.Clone(), 12);
            t.True(m.LastSolve!.Satisfied && Math.Abs(Math.Abs(Z() - 10) - 12) < 1e-6, $"12 mm from it after the edit ({Z():0.######})");
            t.True(edited.Id == dist.Id && edited.Name == dist.Name && m.Doc.Mates.IndexOf(edited) == 1 && m.Doc.Mates.Count == 3 && m.Doc.Mates[2] == lock0,
                "the same mate: id, name and place kept, nothing added");
            // an assembly saved when Distance was a type of its own: read as the Coincident with an offset it is
            var json = m.Doc.ToJson();
            var i = m.Doc.Mates.IndexOf(edited);
            var legacy = AssemblyDocument.FromJson(json.Replace("\"Coincident\"", "\"Distance\""));
            t.True(legacy.Mates.All(x => x.Type != MateType.Distance) && legacy.Mates[i].Value == 12, "Distance mates load as Coincidents with their offset");
            var coin = m.ReplaceMate(edited, MateType.Coincident, edited.A.Clone(), edited.B.Clone(), 0);
            t.True(m.LastSolve!.Satisfied && Math.Abs(Z() - 10) < 1e-6 && coin.Name == dist.Name && coin.Id == dist.Id, $"no offset: touching ({Z():0.######}, {coin.Name})");
        });

        t.Test("Mate solver (sparse): a tree of stacked sleeves and a separate pair solve, knocked askew, back onto their mates", () =>
        {
            // a fixed pin; on it a stack of 12 sleeves; from the 4th a branch of 6 more beside it (a Distance between
            // axes); and a pair mated only to each other and an origin plane (a piece of the graph on its own)
            var dir = NewDir("sparse");
            string pp = Path.Combine(dir, "Pin.sprt");
            Cylinder(pp, 5, 10);
            var m = AssemblyModel.New();
            var pin = m.Insert(pp, Transform.Identity);
            MateRef R(Component c, string which) => m.RefTo(c.Id, FaceOf(m, c, which))!;
            Component Stack(Component below, Vec3 at)
            {
                var c = m.Insert(pp, Transform.Translation(at));
                c.Fixed = false;
                m.AddMate(MateType.Concentric, R(c, "side"), R(below, "side"));
                m.AddMate(MateType.Coincident, R(c, "start"), R(below, "end"));
                return c;
            }
            var stack = new List<Component> { pin };
            for (int i = 1; i <= 12; i++) stack.Add(Stack(stack[^1], new Vec3(0, 0, 10 * i)));
            var branchBase = m.Insert(pp, Transform.Translation(new Vec3(30, 0, 30)));
            branchBase.Fixed = false;
            m.AddMate(MateType.Coincident, R(branchBase, "side"), R(stack[4], "side"), 30, x => x.Aligned = true);
            m.AddMate(MateType.Coincident, R(branchBase, "start"), R(stack[3], "end"));
            var branch = new List<Component> { branchBase };
            for (int i = 1; i <= 6; i++) branch.Add(Stack(branch[^1], new Vec3(30, 0, 30 + 10 * i)));
            var lone = m.Insert(pp, Transform.Translation(new Vec3(-50, 0, 0))); lone.Fixed = false;
            var mate = m.Insert(pp, Transform.Translation(new Vec3(-50, 0, 10))); mate.Fixed = false;
            m.AddMate(MateType.Coincident, R(lone, "start"), m.RefTo(null, "Front")!);
            m.AddMate(MateType.Concentric, R(mate, "side"), R(lone, "side"));
            m.AddMate(MateType.Coincident, R(mate, "start"), R(lone, "end"));
            t.True(m.LastSolve!.Satisfied, "built satisfied");
            var where = m.Doc.Components.ToDictionary(c => c.Id, c => c.Placement.ToTransform());
            var rnd = new Random(7);
            foreach (var c in m.Doc.Components.Where(c => !c.Fixed))
                c.Placement = Placement.From(Transform.Translation(new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5))
                    * Transform.Rotation(c.Placement.ToTransform().T, new Vec3(rnd.NextDouble(), rnd.NextDouble(), 1), 0.05 * rnd.NextDouble()) * c.Placement.ToTransform());
            var solve = m.SolveMates();
            t.True(solve.Satisfied, $"solved back ({solve.Iterations} iterations, worst {solve.Errors.Values.Max():E1})");
            // the stack's axes back where they were (each may turn about its axis: nothing holds that; the branch may
            // swing round the stack and the pair slide on the plane: they are only held to their mates)
            var axisOff = stack.Max(c =>
            {
                var a = c.Placement.ToTransform(); var b = where[c.Id];
                return Math.Max(Vec3.Distance(a.Point(Vec3.Zero), b.Point(Vec3.Zero)), Vec3.Distance(a.Point(new Vec3(0, 0, 10)), b.Point(new Vec3(0, 0, 10))));
            });
            t.True(axisOff < 1e-6, $"the stack's axes where they were (off by {axisOff:E1})");
        });

        t.Test("Mates: concentric and coincident stack a sleeve on a pin; a distance lifts it; contradictions are reported", () =>
        {
            var (m, pin, sleeve, dir) = PinAndSleeve("mates");
            MateRef R(Component c, string which) => m.RefTo(c.Id, FaceOf(m, c, which))!;
            var conc = m.AddMate(MateType.Concentric, R(sleeve, "side"), R(pin, "side"));
            var coin = m.AddMate(MateType.Coincident, R(sleeve, "start"), R(pin, "end"));
            t.True(m.LastSolve!.Satisfied, $"satisfied ({string.Join(", ", m.LastSolve.Errors.Select(e => $"{e.Key} {e.Value:E1}"))})");
            t.True(!coin.Aligned, "face to face: anti-aligned");
            // the sleeve's start face on the pin's end (z = 10), its axis the pin's
            var start = m.World(coin.A); var axis = m.World(conc.A);
            t.True(Math.Abs(start.P.Z - 10) < 1e-6 && Math.Abs(Math.Abs(start.D.Z) - 1) < 1e-9, $"stacked ({start.P}, {start.D})");
            t.True(Math.Abs(Math.Abs(axis.D.Z) - 1) < 1e-9 && new Vec2(axis.P.X, axis.P.Y).Length < 1e-6, $"on the axis ({axis.P}, {axis.D})");
            t.True(pin.Placement.ToTransform().T.Length < 1e-12, "the fixed pin did not move");

            // a distance instead: 5 mm apart
            m.Doc.Mates.Remove(coin);
            var dist = m.AddMate(MateType.Coincident, R(sleeve, "start"), R(pin, "end"), 5);
            t.True(m.LastSolve!.Satisfied && Math.Abs(m.World(dist.A).P.Z - 15) < 1e-6, $"5 mm up ({m.World(dist.A).P}, normal {m.World(dist.A).D}; side {dist.Side}, aligned {dist.Aligned}; errors {string.Join(", ", m.LastSolve.Errors.Select(e => $"{e.Key} {e.Value:E1}"))}; iterations {m.LastSolve.Iterations})");

            // contradicting it (coincident with the same faces): reported, nothing thrown
            var clash = m.AddMate(MateType.Coincident, R(sleeve, "start"), R(pin, "end"));
            t.True(!m.LastSolve!.Satisfied && m.LastSolve.Failed().Any(), "the conflict is reported");
            m.Doc.Mates.Remove(clash);
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied, "satisfied again without it");

            // parallel and angle against the assembly's origin planes: the sleeve's end parallel to Front, its axis at 30° to Top's normal
            var (m2, _, s2, _) = PinAndSleeve("mates2");
            MateRef R2(string which) => m2.RefTo(s2.Id, FaceOf(m2, s2, which))!;
            m2.AddMate(MateType.Parallel, R2("end"), m2.RefTo(null, "Front")!);
            t.True(m2.LastSolve!.Satisfied && Math.Abs(Math.Abs(m2.World(R2("end")).D.Z) - 1) < 1e-9, "parallel to Front");
            var (m3, _, s3, _) = PinAndSleeve("mates3");
            var side = m3.RefTo(s3.Id, FaceOf(m3, s3, "side"))!;
            m3.AddMate(MateType.Angle, side, m3.RefTo(null, "Top")!, 30);
            var ang = Math.Acos(Math.Clamp(Vec3.Dot(m3.World(side).D, Vec3.UnitY), -1, 1)) * 180 / Math.PI;
            t.True(m3.LastSolve!.Satisfied && Math.Abs(ang - 30) < 1e-5, $"30° ({ang:0.######})");

            // saved; the parts' files gone: the mates still solve from the geometry they keep
            var asmPath = Path.Combine(dir, "Stack.sasm");
            m.Save(asmPath);
            var placed = sleeve.Placement.ToTransform().T;
            File.Delete(Path.Combine(dir, "Pin.sprt")); File.Delete(Path.Combine(dir, "Sleeve.sprt"));
            var back = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            t.True(back.Doc.Components.All(c => back.DisplayOf(c)!.Source == PartSource.Cache), "from the caches");
            var sb = back.Doc.Find(sleeve.Id)!;
            sb.Placement = Placement.From(Transform.Translation(new Vec3(3, -2, 9)) * sb.Placement.ToTransform());
            back.SolveMates();
            t.True(back.LastSolve!.Satisfied && Vec3.Distance(sb.Placement.ToTransform().T, placed) < 1e-5, $"back in place ({sb.Placement.ToTransform().T} vs {placed})");
        });

        t.Test("Hinge demo: one file, its parts embedded; reopened from it alone; no two components overlap", () =>
        {
            var dir = NewDir("hinge");
            var path = Path.Combine(dir, "hinge.sasm");
            Demos.Assemblies.First(a => a.file == "hinge.sasm").build().Save(path);
            var m = AssemblyModel.Open(AssemblyDocument.Load(path), path, new PartIndex());
            t.Eq(m.Doc.Components.Count, 3);
            t.True(m.Doc.Components.All(c => m.DisplayOf(c)!.Source == PartSource.Embedded && m.DisplayOf(c)!.Problem == null), "all from the file");
            t.Eq(m.Parts.Count, 2);
            t.True(m.Notes.Count == 0, string.Join("; ", m.Notes));
            // its mates hold as placed
            t.Eq(m.Doc.Mates.Count, 4);
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied && m.LastSolve.Moved.Count == 0, $"the mates hold as placed (moved: {string.Join(", ", m.LastSolve.Moved)})");
            // the placed solids, pairwise
            var solids = m.Doc.Components.Select(c => (c.Name, body: new SpryCad.Core.Regen.Regenerator().Rebuild(m.DisplayOf(c)!.Doc!).State.Bodies.Single().Solid.Native!
                .Transformed(c.Placement.ToTransform()))).ToList();
            for (int i = 0; i < solids.Count; i++)
                for (int j = i + 1; j < solids.Count; j++)
                {
                    var common = NBoolean.Run(solids[i].body, solids[j].body, BoolOp.Intersect);
                    var v = common.Faces.Count == 0 ? 0 : NMass.Of(common)!.Volume;
                    t.True(v < 1e-6, $"{solids[i].Name} and {solids[j].Name} overlap by {v:0.######} mm3");
                }
            // the second leaf swings about the hinge's axis (its one freedom): turned there it stays; moved along the
            // axis or off it, the mates bring it back onto the axis, its knuckle where it was along it (how far round it
            // ends up is free: the least motion may turn it a little)
            var leaf2 = m.Doc.Components[1];
            var home = leaf2.Placement.ToTransform();
            var axisP = new Vec3(0, 32, 1);
            var swung = Transform.Rotation(axisP, Vec3.UnitX, 0.6) * home;
            leaf2.Placement = Placement.From(swung);
            m.SolveMates();
            t.True(m.LastSolve!.Satisfied && !m.LastSolve.Moved.Contains(leaf2.Id), "turned about the axis: it stays");
            leaf2.Placement = Placement.From(Transform.Translation(new Vec3(3, 0.5, -0.4)) * swung);
            m.SolveMates();
            var q = new Vec3(30, 50, 1);
            var back = leaf2.Placement.ToTransform().Point(q); var was = swung.Point(q);
            double R(Vec3 p) => new Vec2(p.Y - 32, p.Z - 1).Length;
            t.True(m.LastSolve!.Satisfied && Math.Abs(back.X - was.X) < 1e-6 && Math.Abs(R(back) - R(was)) < 1e-6,
                $"moved off: back on the axis, as far along it ({back} vs {was})");
        });

        t.Test("Assembly model: components linked and embedded, displays built, caches saved; missing, lightweight and changed parts", () =>
        {
            var dir = NewDir("model");
            string pa = Path.Combine(dir, "parts", "Pin.sprt"), pb = Path.Combine(dir, "parts", "Leaf.sprt");
            Cylinder(pa, 5, 10); Cylinder(pb, 3, 20);
            var m = AssemblyModel.New();
            var c1 = m.Insert(pa, Transform.Identity);
            var c2 = m.Insert(pa, Transform.Translation(new Vec3(30, 0, 0)));
            var c3 = m.Insert(pb, Transform.Translation(new Vec3(0, 30, 0)), embed: true);
            t.True(c1.Name == "Pin<1>" && c2.Name == "Pin<2>" && c3.Name == "Leaf<1>", "names");
            t.Eq(m.Parts.Count, 2);
            double V(Component c) => m.DisplayOf(c)!.Mesh.Volume;
            t.True(Math.Abs(V(c1) - Math.PI * 25 * 10) < 0.01 * Math.PI * 250 && Math.Abs(V(c3) - Math.PI * 9 * 20) < 0.01 * Math.PI * 180, "displays");
            t.True(c3.Part.Embedded != null && m.Doc.Files.ContainsKey(c3.Part.Embedded), "the leaf embedded");
            var asmPath = Path.Combine(dir, "Hinge.sasm");
            m.Save(asmPath);
            t.Eq(m.Doc.Caches.Count, 2);
            t.Eq(c1.Part.Path, "parts/Pin.sprt");
            int tris = m.DisplayOf(c1)!.Mesh.Indices.Length;

            // reopened: the pin from its file, the leaf from the assembly
            var m2 = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath);
            t.True(m2.DisplayOf(m2.Doc.Components[0])!.Source == PartSource.File && m2.DisplayOf(m2.Doc.Components[2])!.Source == PartSource.Embedded, "sources");

            // the pin's file moved out of reach: shown from the cache, as it was
            var away = Path.Combine(NewDir("away"), "Pin.sprt");
            File.Move(pa, away);
            var m3 = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath, new PartIndex());
            var pd = m3.DisplayOf(m3.Doc.Components[0])!;
            t.True(pd.Source == PartSource.Cache && pd.Mesh.Indices.Length == tris && pd.Problem != null, "missing: from the cache");
            t.True(m3.Notes.Any(n => n.Contains("not found")), "a note");
            File.Move(away, pa);

            // lightweight: from the cache although the file is there
            var a4 = AssemblyDocument.Load(asmPath);
            foreach (var c in a4.Components.Where(c => c.Name.StartsWith("Pin"))) c.Lightweight = true;
            var m4 = AssemblyModel.Open(a4, asmPath);
            t.True(m4.DisplayOf(a4.Components[0])!.Source == PartSource.Cache && m4.DisplayOf(a4.Components[2])!.Source == PartSource.Embedded, "lightweight");

            // the pin made longer (a new revision): rebuilt, and on saving its cache replaces the old one
            var pin = CadDocument.Load(pa);
            ((ExtrudeFeature)pin.Features[1]).Depth = "40";
            pin.Save(pa);
            var m5 = AssemblyModel.Open(AssemblyDocument.Load(asmPath), asmPath);
            t.True(m5.Resolution.Of(m5.Doc.Components[0]).NewRevision, "a new revision");
            t.True(Math.Abs(m5.DisplayOf(m5.Doc.Components[0])!.Mesh.Volume - Math.PI * 25 * 40) < 0.01 * Math.PI * 1000, "rebuilt");
            m5.Save(asmPath);
            t.Eq(m5.Doc.Caches.Count, 2);
            t.True(m5.Doc.Caches.ContainsKey(AssemblyDocument.CacheKey(pin.Id!, pin.Revision)), "the new revision cached, the old one dropped");
            t.Eq(m5.Doc.Files.Keys.Count(k => k.StartsWith("cache/")), 2);

            // a part saved before identities: given one when inserted
            var legacy = Path.Combine(dir, "parts", "Old.sprt");
            Cylinder(legacy, 2, 5, identity: false);
            t.True(DocumentContainer.ReadIdentity(legacy).id == null, "no identity yet");
            m5.Insert(legacy, Transform.Identity);
            t.True(DocumentContainer.ReadIdentity(legacy).id != null && m5.Notes.Any(n => n.Contains("identity")), "given one");
        });

        t.Test("Part identity: an id on the first save (kept), a new revision on every save that changes it, a new id for a copy", () =>
        {
            var dir = NewDir("identity");
            var doc = new CadDocument();
            t.True(doc.Id == null, "no id before saving");
            var p = Path.Combine(dir, "a.sprt");
            doc.Save(p);
            var (id, rev) = (doc.Id, doc.Revision);
            t.True(id != null && rev != null, "an id and a revision");
            doc.Save(p);
            t.True(doc.Id == id && doc.Revision == rev, "nothing changed: same revision");
            doc.Equations.Add(new Equation { Name = "W", Expr = "10" });
            doc.Save(p);
            t.True(doc.Id == id && doc.Revision != rev, "same id, new revision");
            t.Eq(DocumentContainer.ReadIdentity(p), (doc.Id, doc.Revision));
            // another file of the same part
            var pc = Path.Combine(dir, "c.sprt");
            doc.Save(pc);
            t.Eq(DocumentContainer.ReadIdentity(pc), (doc.Id, doc.Revision));
            t.Eq(CadDocument.Load(pc).Id, doc.Id);
            // a copy saved under another name: its own identity, remembering where it came from
            doc.ForkIdentity();
            doc.Save(Path.Combine(dir, "copy.sprt"));
            t.True(doc.Id != id && doc.DerivedFrom == id, "the copy's identity");
            t.Eq(DocumentContainer.ReadIdentity(p).id, id);
            // not a part
            File.WriteAllText(Path.Combine(dir, "x.sprt"), "not json");
            t.Eq(DocumentContainer.ReadIdentity(Path.Combine(dir, "x.sprt")), ((string?)null, (string?)null));
        });

        t.Test("Assembly document: components (names, placements, fixed first), embedded files and caches saved and loaded", () =>
        {
            var dir = NewDir("doc");
            var asm = new AssemblyDocument();
            var bolt = new PartRef { PartId = "B", Path = "parts/Bolt.sprt" };
            var place = Transform.Rotation(Vec3.Zero, Vec3.UnitZ, 0.7) * Transform.Translation(new Vec3(10, 20, 30));
            var c1 = asm.AddComponent(bolt, Transform.Identity);
            var c2 = asm.AddComponent(new PartRef { PartId = "B", Path = "parts/Bolt.sprt" }, place);
            var c3 = asm.AddComponent(new PartRef { PartId = "N", Path = "Nut.sprt" }, Transform.Translation(new Vec3(0, 0, 50)));
            t.True(c1.Name == "Bolt<1>" && c2.Name == "Bolt<2>" && c3.Name == "Nut<1>", $"names {c1.Name} {c2.Name} {c3.Name}");
            t.True(c1.Fixed && !c2.Fixed && !c3.Fixed, "the first component is fixed");
            asm.Files["cache/B.mesh"] = new byte[] { 1, 2, 3 };
            asm.Caches[AssemblyDocument.CacheKey("B", "r1")] = "cache/B.mesh";
            var p = Path.Combine(dir, "a.sasm");
            asm.Save(p);
            var back = AssemblyDocument.Load(p);
            t.Eq(back.Components.Count, 3);
            t.True(back.Id == asm.Id && back.Id != null, "identity");
            var q = new Vec3(1, 2, 3);
            t.True(Vec3.Distance(back.Find(c2.Id)!.Placement.ToTransform().Point(q), place.Point(q)) < 1e-9, "placement");
            t.True(back.Files["cache/B.mesh"].SequenceEqual(new byte[] { 1, 2, 3 }) && back.Caches.Count == 1, "embedded files and caches");
            // a damaged cache is refused (its blob no longer matches its hash)
            var damaged = Path.Combine(dir, "bad.sasm");
            File.Copy(p, damaged);
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = damaged, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE Blobs SET Codec = 0, RawSize = 3, Data = X'090909' WHERE Hash = $h";
                cmd.Parameters.AddWithValue("$h", SpryCad.Core.Storage.Hash.Of(new byte[] { 1, 2, 3 }).ToBytes());
                t.Eq(cmd.ExecuteNonQuery(), 1);
            }
            bool refused = false;
            try { AssemblyDocument.Load(damaged); } catch (InvalidDataException) { refused = true; }
            t.True(refused, "a damaged cache is refused");
        });

        t.Test("Display cache: a part's mesh, faces, bounds, volume and color written and read back", () =>
        {
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            var box = NBuild.Extrude(new[] { new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 20), new Vec2(0, 20) }, "B")) }, frame, 0, 5, "F1");
            var mesh = DisplayMesh.Build(NTess.ToSolid(box));
            var back = MeshCache.Read(MeshCache.Write(mesh, new[] { 0.2, 0.4, 0.6 }));
            t.True(back.Mesh.Positions.SequenceEqual(mesh.Positions) && back.Mesh.Normals.SequenceEqual(mesh.Normals) && back.Mesh.Indices.SequenceEqual(mesh.Indices), "triangles");
            t.True(back.Mesh.Faces.Select(f => (f.Name, f.IndexStart, f.IndexCount)).SequenceEqual(mesh.Faces.Select(f => (f.Name, f.IndexStart, f.IndexCount))), "faces");
            t.True(back.Mesh.EdgeLines.SequenceEqual(mesh.EdgeLines) && back.Mesh.Edges.Count == mesh.Edges.Count, "edges");
            t.True(Math.Abs(back.Mesh.Volume - 1000) < 1e-6 && Vec3.Distance(back.Mesh.Bounds.Max, new Vec3(10, 20, 5)) < 1e-9, "volume and bounds");
            t.True(Math.Abs(back.Color[1] - 0.4) < 1e-6, "color");
        });

        t.Test("Finding parts: where last seen; the assembly moved; renamed (index, folder scan); a folder moved; a different part at the old name; missing; copies", () =>
        {
            // a project: assembly and two parts in parts/
            var root = NewDir("resolve");
            var proj = Path.Combine(root, "Proj");
            string bp = Path.Combine(proj, "parts", "Bolt.sprt"), np = Path.Combine(proj, "parts", "std", "Nut.sprt");
            var bolt = SavePart(bp); var nut = SavePart(np);
            var asmPath = Path.Combine(proj, "Top.sasm");
            var asm = new AssemblyDocument();
            asm.AddComponent(Ref(bolt, proj, bp), Transform.Identity);
            asm.AddComponent(Ref(nut, proj, np), Transform.Identity);
            asm.Save(asmPath);
            AssemblyDocument Fresh() => AssemblyDocument.Load(asmPath);

            // 1. where last seen
            var r = ComponentResolver.Resolve(Fresh(), asmPath);
            t.True(r.Parts.Values.All(p => p.Status == PartStatus.Found) && !r.Changed && r.Notes.Count == 0, "found in place");

            // 2. the assembly moved (copied elsewhere), the parts not: found at their absolute paths, relative paths updated
            var elsewhere = Path.Combine(root, "Other", "Top.sasm");
            Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
            File.Copy(asmPath, elsewhere);
            var moved = AssemblyDocument.Load(elsewhere);
            r = ComponentResolver.Resolve(moved, elsewhere);
            t.True(r.Parts.Values.All(p => p.Status == PartStatus.Found) && r.Changed, "found by absolute path");
            t.Eq(moved.Components[0].Part.Path, "../Proj/parts/Bolt.sprt");

            // 3. the bolt renamed: the index knows (it was opened there)
            var bp2 = Path.Combine(proj, "parts", "Bolt-M10.sprt");
            File.Move(bp, bp2);
            var index = new PartIndex();
            index.RecordFile(bp2);
            var a3 = Fresh();
            r = ComponentResolver.Resolve(a3, asmPath, index);
            t.True(r.Of(a3.Components[0]).Status == PartStatus.Moved && PartIndex.SamePath(r.Of(a3.Components[0]).Path!, bp2), "renamed: from the index");
            t.True(r.Notes.Count == 1 && r.Notes[0].Contains("new place"), "one note");
            t.Eq(a3.Components[0].Part.Path, "parts/Bolt-M10.sprt");

            // 4. renamed and nothing indexed: found by scanning the assembly's folder
            var a4 = Fresh();
            r = ComponentResolver.Resolve(a4, asmPath, new PartIndex());
            t.True(r.Of(a4.Components[0]).Status == PartStatus.Moved && PartIndex.SamePath(r.Of(a4.Components[0]).Path!, bp2), "renamed: from the folder");
            File.Move(bp2, bp);

            // 5. the parts folder moved away from the assembly (to another project); the index knows the bolt's new
            // place only: the nut is found where the bolt's folder went
            var newParts = Path.Combine(root, "Library", "parts");
            Directory.CreateDirectory(Path.GetDirectoryName(newParts)!);
            Directory.Move(Path.Combine(proj, "parts"), newParts);
            index = new PartIndex();
            index.RecordFile(Path.Combine(newParts, "Bolt.sprt"));
            var a5 = Fresh();
            r = ComponentResolver.Resolve(a5, asmPath, index);
            t.True(r.Parts.Values.All(p => p.Status == PartStatus.Moved), "both found");
            t.True(PartIndex.SamePath(r.Of(a5.Components[1]).Path!, Path.Combine(newParts, "std", "Nut.sprt")) && r.Notes.Any(n => n.Contains("moved with the other parts")), "the nut where the bolt's folder went");

            // 6. a different part saved under the bolt's old name: not taken; the real one is found in the library
            SavePart(bp);
            var a6 = Fresh();
            r = ComponentResolver.Resolve(a6, asmPath, index);
            t.True(r.Of(a6.Components[0]).Status == PartStatus.Moved && r.Notes.Any(n => n.Contains("different part")), "a different part at the old name is not taken");

            // 7. missing: the library deleted
            Directory.Delete(Path.Combine(root, "Library"), true);
            var a7 = Fresh();
            r = ComponentResolver.Resolve(a7, asmPath, new PartIndex());
            t.True(r.Of(a7.Components[1]).Status == PartStatus.Missing && r.Notes.Any(n => n.Contains("not found")), "missing");

            // 8. copies of a part (copied in the file manager: same id): the revision last used wins
            var dir8 = NewDir("copies");
            var p8 = Path.Combine(dir8, "Plate.sprt");
            var plate = SavePart(p8);
            var asm8 = new AssemblyDocument();
            asm8.AddComponent(Ref(plate, dir8, p8), Transform.Identity);
            var older = Path.Combine(dir8, "old", "Plate.sprt");
            Directory.CreateDirectory(Path.GetDirectoryName(older)!);
            File.Copy(p8, older);
            plate.Equations.Add(new Equation { Name = "T", Expr = "1" });
            plate.Save(p8); // a newer revision in place...
            File.Move(p8, Path.Combine(dir8, "Plate-new.sprt"));
            var ix8 = new PartIndex();
            ix8.Scan(dir8);
            r = ComponentResolver.Resolve(asm8, Path.Combine(dir8, "A.sasm"), ix8);
            t.True(PartIndex.SamePath(r.Of(asm8.Components[0]).Path!, older), $"the copy of the revision last used ({r.Of(asm8.Components[0]).Path})");

            // 9. a part saved before identities: by path only
            var dir9 = NewDir("legacy");
            var p9 = Path.Combine(dir9, "Old.sprt");
            File.WriteAllText(p9, new CadDocument().ToJson());
            var asm9 = new AssemblyDocument();
            asm9.AddComponent(new PartRef { Path = "Old.sprt", AbsolutePath = p9 }, Transform.Identity);
            r = ComponentResolver.Resolve(asm9, Path.Combine(dir9, "A.sasm"));
            t.True(r.Of(asm9.Components[0]).Status == PartStatus.Found, "by path");
        });

        DragSessions(t);
    }

    /// <summary>
    /// A sleeve turning on a fixed pin, dragged round by its rim (as the viewport does: one drag session, mouse move by
    /// mouse move): a part locked to the sleeve goes round with it, one locked to the pin stays, one locked out of place
    /// is pulled onto its lock; the same as solving each move afresh; exact on release.
    /// </summary>
    static void DragSessions(TestRunner t)
    {
        t.Test("Drag sessions: locked parts carried with what they are locked to, the same as solving each move afresh, exact on release", () =>
        {
            (AssemblyModel m, Component sleeve, Component flag, Component still, Component loose) Build()
            {
                var (m, pin, sleeve, dir) = PinAndSleeve("drag-session");
                m.AddMate(MateType.Concentric, m.RefTo(sleeve.Id, FaceOf(m, sleeve, "side"))!, m.RefTo(pin.Id, FaceOf(m, pin, "side"))!);
                m.AddMate(MateType.Coincident, m.RefTo(sleeve.Id, FaceOf(m, sleeve, "start"))!, m.RefTo(pin.Id, FaceOf(m, pin, "end"))!);
                var fp = Path.Combine(dir, "Flag.sprt");
                Cylinder(fp, 2, 5);
                Component Locked(Component to, Vec3 at)
                {
                    var c = m.Insert(fp, Transform.Translation(at));
                    m.AddMate(MateType.Lock, new MateRef { Component = c.Id, D = Vec3.UnitZ }, new MateRef { Component = to.Id, D = Vec3.UnitZ });
                    return c;
                }
                var flag = Locked(sleeve, new Vec3(20, 0, 15));
                var still = Locked(pin, new Vec3(-20, 0, 0));
                var loose = Locked(sleeve, new Vec3(0, 20, 15));
                // knocked off its lock: it holds no longer, and the solve puts it back
                loose.Placement = Placement.From(Transform.Translation(new Vec3(3, 1, 2)) * loose.Placement.ToTransform());
                return (m, sleeve, flag, still, loose);
            }
            Transform T(Component c) => c.Placement.ToTransform();
            MateSolver.Pull Pull(Component sleeve, int i)
            {
                var a = 0.1 * (i + 1);
                return new MateSolver.Pull(sleeve.Id, new Vec3(5, 0, 5), new Vec3(5 * Math.Cos(a), 5 * Math.Sin(a), 100), -Vec3.UnitZ);
            }

            var (m, sleeve, flag, still, loose) = Build();
            var flagOn = T(sleeve).RigidInverse() * T(flag);
            var stillAt = T(still);
            m.BeginDrag(sleeve.Id);
            for (int i = 0; i < 20; i++)
            {
                var res = m.SolveDrag(Pull(sleeve, i));
                t.True(res.Satisfied, $"move {i}: the mates hold ({string.Join(", ", res.Failed())})");
                t.True(res.Solved.Contains(flag.Id), $"move {i}: the part locked to the sleeve is solved with it");
                var off = T(sleeve).RigidInverse() * T(flag);
                t.True(Vec3.Distance(off.T, flagOn.T) < 1e-9 && Vec3.Distance(off.Vector(Vec3.UnitX), flagOn.Vector(Vec3.UnitX)) < 1e-9, $"move {i}: the locked part stays where it is on the sleeve");
                t.True(Vec3.Distance(T(still).T, stillAt.T) < 1e-12, $"move {i}: the part locked to the pin stays");
            }
            var end = m.EndDrag()!;
            t.True(end.Satisfied && end.Errors.Values.Max() < 1e-9, $"exact on release (worst {end.Errors.Values.Max():E1})");
            var turned = Math.Atan2(T(sleeve).Vector(Vec3.UnitX).Y, T(sleeve).Vector(Vec3.UnitX).X);

            // each move solved afresh
            var (m2, sleeve2, _, _, _) = Build();
            for (int i = 0; i < 20; i++) m2.SolveDrag(Pull(sleeve2, i));
            m2.SolveMates();
            var turned2 = Math.Atan2(T(sleeve2).Vector(Vec3.UnitX).Y, T(sleeve2).Vector(Vec3.UnitX).X);
            t.True(Math.Abs(turned - 2.0) < 1e-6 && Math.Abs(turned - turned2) < 1e-6, $"turned {turned:0.######} with the session, {turned2:0.######} afresh (the cursor: 2)");
        });

        t.Test("Drag sessions: the linkage's slider dragged past both ends of its travel and back follows the cursor wherever it can reach, off its dead points too", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "linkage.sasm").build();
            var slider = m.Doc.Components.First(c => c.Name.StartsWith("Slider"));
            // its travel: where the kinematics put it as the crank goes round
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 3600; i++) { var x = Demos.LinkageJoints(2 * Math.PI * i / 3600).C.X; lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            double At() => slider.Placement.ToTransform().T.X;
            // the cursor along the rail: on past the far end, back past the near one, then into the middle
            var path = new List<double>();
            for (double x = At(); x < hi + 30; x += 4) path.Add(x);
            for (double x = hi + 30; x > lo - 30; x -= 4) path.Add(x);
            for (double x = lo - 30; x < (lo + hi) / 2; x += 4) path.Add(x);
            m.BeginDrag(slider.Id);
            double worst = 0; string where = "";
            foreach (var x in path)
            {
                var res = m.SolveDrag(new MateSolver.Pull(slider.Id, Vec3.Zero, new Vec3(x, 0, 100), -Vec3.UnitZ));
                t.True(res.Satisfied, $"cursor at {x:0.#}: the mates hold");
                // within its reach it is under the cursor; beyond, at that end of its travel
                var off = Math.Abs(At() - Math.Clamp(x, lo, hi));
                if (off > worst) { worst = off; where = $"cursor at {x:0.#}, slider at {At():0.###} (travel {lo:0.###} to {hi:0.###})"; }
            }
            m.EndDrag();
            // (a few mm behind where it leaves an end of its travel: there the crank turns a long way for a little of it)
            t.True(worst < 3, $"the slider follows the cursor: worst {worst:0.###} mm off, {where}");
        });

        t.Test("Drag sessions: a turn by hand (Shift+drag) turns a sleeve on its pin as far as its mates let it; a part locked to a fixed one stays; one no mate holds turns freely", () =>
        {
            var (m, pin, sleeve, dir) = PinAndSleeve("turn-drag");
            m.AddMate(MateType.Concentric, m.RefTo(sleeve.Id, FaceOf(m, sleeve, "side"))!, m.RefTo(pin.Id, FaceOf(m, pin, "side"))!);
            m.AddMate(MateType.Coincident, m.RefTo(sleeve.Id, FaceOf(m, sleeve, "start"))!, m.RefTo(pin.Id, FaceOf(m, pin, "end"))!);
            Transform T(Component c) => c.Placement.ToTransform();
            double AboutZ() { var x = T(sleeve).Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
            var a0 = AboutZ();
            var centre = new Vec3(0, 0, 15);
            var grab = new Vec3(5, 0, 5); // on the sleeve's rim (in its part)
            m.BeginDrag(sleeve.Id);
            for (int i = 0; i < 20; i++)
            {
                var r = Transform.Rotation(centre, Vec3.UnitZ, 0.1);
                var at = T(sleeve).Point(grab);
                var res = m.SolveDrag(new MateSolver.Pull(sleeve.Id, grab, r.Point(at), Vec3.Zero, default, r));
                t.True(res.Satisfied, $"turn {i}: the mates hold");
            }
            var turned = Math.IEEERemainder(AboutZ() - a0, 2 * Math.PI);
            t.True(Math.Abs(turned - 2.0) < 1e-3, $"turned about the pin {turned:0.#####} (the hand: 2)");
            // turned about a line across the pin: the mates do not let it tilt
            for (int i = 0; i < 5; i++)
            {
                var r = Transform.Rotation(centre, Vec3.UnitX, 0.1);
                var at = T(sleeve).Point(grab);
                var res = m.SolveDrag(new MateSolver.Pull(sleeve.Id, grab, r.Point(at), Vec3.Zero, default, r));
                t.True(res.Satisfied, $"tilt {i}: the mates hold");
            }
            t.True(Vec3.Cross(T(sleeve).Vector(Vec3.UnitZ), Vec3.UnitZ).Length < 1e-6, "still on the pin's axis");
            var end = m.EndDrag()!;
            t.True(end.Satisfied && end.Errors.Values.Max() < 1e-9, "exact on release");

            // a part locked to the fixed pin: dragged or turned, its lock keeps it where it is (the solve holds it still;
            // the hand does not move it as it does a part no mate holds)
            var locked = m.Insert(Path.Combine(dir, "Pin.sprt"), Transform.Translation(new Vec3(-40, 0, 0)));
            m.AddMate(MateType.Lock, new MateRef { Component = locked.Id, D = Vec3.UnitZ }, new MateRef { Component = pin.Id, D = Vec3.UnitZ });
            var lockedAt = T(locked);
            m.BeginDrag(locked.Id);
            var lr = m.SolveDrag(new MateSolver.Pull(locked.Id, Vec3.Zero, new Vec3(-20, 15, 100), -Vec3.UnitZ, new Vec3(20, 15, 0)));
            t.True(lr.Held.Contains(locked.Id), "the locked part is held still by its lock");
            m.SolveDrag(new MateSolver.Pull(locked.Id, Vec3.Zero, new Vec3(-40, 0, 20), Vec3.Zero, default, Transform.Rotation(new Vec3(-40, 0, 5), Vec3.UnitY, 0.3)));
            m.EndDrag();
            t.True(Vec3.Distance(T(locked).T, lockedAt.T) < 1e-9 && Vec3.Distance(T(locked).Vector(Vec3.UnitZ), lockedAt.Vector(Vec3.UnitZ)) < 1e-9 && m.LastSolve!.Satisfied,
                "dragged and turned, the locked part stays");

            // a part no mate holds: turned as the hand turns it
            var loose = m.Insert(Path.Combine(dir, "Pin.sprt"), Transform.Translation(new Vec3(60, 0, 0)));
            var turn = Transform.Rotation(new Vec3(60, 0, 5), Vec3.UnitY, 0.3);
            var expect = turn * T(loose);
            m.BeginDrag(loose.Id);
            m.SolveDrag(new MateSolver.Pull(loose.Id, Vec3.Zero, Vec3.Zero, Vec3.Zero, default, turn));
            m.EndDrag();
            t.True(Vec3.Distance(T(loose).T, expect.T) < 1e-9 && Vec3.Distance(T(loose).Vector(Vec3.UnitZ), expect.Vector(Vec3.UnitZ)) < 1e-9, "turned freely");
        });

        t.Test("Motion sessions: the linkage run by a motor on its gear, set up once, follows the kinematics two turns round and ends exact", () =>
        {
            var m = Demos.Assemblies.First(x => x.file == "linkage.sasm").build();
            Component C(string name) => m.Doc.Components.First(c => c.Name.StartsWith(name));
            var (crank, slider, g2) = (C("Crank"), C("Slider"), m.Doc.Components.Last(c => c.Name.StartsWith("Gear")));
            double CrankAngle() { var x = crank.Placement.ToTransform().Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
            var bore = m.Doc.Mates.First(x => x.Type == MateType.Gear).B;
            var start = (CrankAngle(), slider.Placement.ToTransform().T.X);
            m.BeginMotion(g2.Id);
            double worst = 0, worstGear = 0;
            int steps = 0;
            for (double turned = 0; turned < 4 * Math.PI - 1e-9; turned += Math.PI / 30, steps++)
            {
                var ax = m.World(bore);
                var was = CrankAngle();
                m.SetWorld(g2.Id, Transform.Rotation(ax.P, ax.D, Math.PI / 30) * m.WorldOf(g2.Id));
                var res = m.SolveMotion();
                if (!res.Satisfied) { t.True(false, $"step {steps}: the mates hold ({string.Join(", ", res.Failed())})"); break; }
                worst = Math.Max(worst, Math.Abs(slider.Placement.ToTransform().T.X - Demos.LinkageJoints(CrankAngle()).C.X));
                // the gears 1:1: the crank turned back as far as the gear turned on, each step
                worstGear = Math.Max(worstGear, Math.Abs(Math.IEEERemainder(CrankAngle() - was, 2 * Math.PI) + Math.PI / 30));
            }
            t.True(worstGear < 1e-6, $"each step the crank turned with the gear (off by {worstGear:0.######} rad)");
            var end = m.EndMotion()!;
            t.True(worst < 1e-4, $"{steps} steps: the slider where the kinematics put it throughout (off by {worst:0.######})");
            t.True(end.Satisfied && end.Errors.Values.Max() < 1e-9, $"exact at the end ({end.Errors.Values.Max():E1})");
            t.True(Math.Abs(Math.IEEERemainder(CrankAngle() - start.Item1, 2 * Math.PI)) < 1e-4 && Math.Abs(slider.Placement.ToTransform().T.X - start.Item2) < 1e-4,
                "two turns: back where it started");
        });
    }
}

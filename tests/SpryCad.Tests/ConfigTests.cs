using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Conditions in equations, features suppressed by them, user-facing parameters and design tables, configured parts.</summary>
static class ConfigTests
{
    /// <summary>
    /// A 100 × 60 × 10 plate (width W) with a 10 mm hole cut at (20, 30) and a linear pattern of it (3 along X,
    /// 30 apart); the cut is suppressed when <paramref name="cutWhen"/> holds.
    /// </summary>
    static (CadDocument doc, Feature cut, Feature pattern) Plate(string? cutWhen)
    {
        var doc = new CadDocument();
        doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60, "W") });
        doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(20, 30));
        sk.Add(new SkCircle { Center = c.Id, R = 5 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAllBoth, SuppressWhen = cutWhen });
        var lp = doc.AddFeature(new LinearPatternFeature { Seeds = { cut.Id }, Direction = new DirRef { Axis = "X" }, Spacing = "30", Count = "3" });
        return (doc, cut, lp);
    }

    static double Volume(RegenResult r) => r.State.Bodies.Sum(b => b.Mesh.Volume);

    /// <summary>Plate sizes S, M (in use), L: width W and whether it has holes.</summary>
    static DesignTable SizeTable() => new()
    {
        Name = "Size", Columns = { "W", "Holes" }, Selected = "M",
        Rows =
        {
            new DesignRow { Key = "S", Values = { "80", "0" } },
            new DesignRow { Key = "M", Values = { "100", "1" } },
            new DesignRow { Key = "L", Values = { "120", "1" } },
        },
    };

    public static void Run(TestRunner t)
    {
        t.Test("Expressions: comparisons and logic", () =>
        {
            Func<string, double?> v = n => n switch { "Teeth" => 24, "Bore" => 12, "order" => 3, _ => null };
            t.Eq(Expr.Eval("Teeth < 30", v), 1.0);
            t.Eq(Expr.Eval("Teeth >= 30", v), 0.0);
            t.Eq(Expr.Eval("Teeth < 30 and Bore > 20", v), 0.0);
            t.Eq(Expr.Eval("Teeth < 30 or Bore > 20", v), 1.0);
            t.Eq(Expr.Eval("not (Teeth < 30)", v), 0.0);
            t.Eq(Expr.Eval("!(Teeth == 24) || Bore != 12", v), 0.0);
            t.Eq(Expr.Eval("Teeth = 24 && Bore <> 13", v), 1.0);
            t.Eq(Expr.Eval("0.1 + 0.2 == 0.3"), 1.0);
            t.Eq(Expr.Eval("1in == 25.4"), 1.0);
            t.Eq(Expr.Eval("1 + 2 * 3 > 6"), 1.0);       // arithmetic binds tighter
            t.Eq(Expr.Eval("-Bore < 0", v), 1.0);
            t.Eq(Expr.Eval("order + 1", v), 4.0);        // "or" only as a whole word
            t.Eq(Expr.Eval("if(Teeth > 20, 5, 2)", v), 5.0);
            t.Eq(Expr.Eval("choose(2, 10, 20, 30)"), 20.0);
            t.True(!Expr.TryEval("choose(4, 10, 20, 30)", null, out _, out _), "choose out of range fails");
            t.True(Expr.Identifiers("Teeth < 30 and not Bore").SequenceEqual(new[] { "Teeth", "Bore" }), "logic words are not variables");
        });

        t.Test("Suppress When: a feature and the features using it suppressed while the condition holds", () =>
        {
            var (doc, cut, lp) = Plate("W < 90");
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            t.True(r.Status.Values.All(s => s.Ok), string.Join("; ", r.Status.Values.Select(s => s.Error).OfType<string>()));
            t.True(!r.Status[cut.Id].Suppressed && !r.Status[lp.Id].Suppressed, "condition does not hold: built");
            var holes = Volume(r);
            t.Near(holes, 100 * 60 * 10 - 3 * Math.PI * 25 * 10, 100 * 60 * 10 * 0.01);

            doc.Equations[0].Expr = "80";
            r = regen.Rebuild(doc);
            t.True(r.Status.Values.All(s => s.Ok), "no errors when suppressed by a condition");
            t.True(r.Status[cut.Id] is { Suppressed: true, SuppressedBy: "condition" }, "cut suppressed by its condition");
            t.True(r.Status[lp.Id].Suppressed && r.Status[lp.Id].SuppressedBy == cut.Name, "pattern suppressed with its seed");
            t.Near(Volume(r), 80 * 60 * 10, 1e-6 * 80 * 60 * 10);
            var off = Regenerator.SuppressedFeatures(doc);
            t.True(off.Count == 2 && off[cut.Id] == "condition" && off[lp.Id] == cut.Name, "suppressed features known without a rebuild");

            // back again: rebuilt, with the holes
            doc.Equations[0].Expr = "100";
            r = regen.Rebuild(doc);
            t.True(!r.Status[cut.Id].Suppressed && !r.Status[lp.Id].Suppressed, "built again");
            t.Near(Volume(r), holes, 1e-6);

            // suppressed by hand: the pattern follows too (no rebuild error)
            cut.Suppressed = true;
            r = regen.Rebuild(doc);
            t.True(r.Status[lp.Id] is { Suppressed: true, Ok: true }, "pattern follows a hand-suppressed seed");
            t.True(r.Status[cut.Id] is { Suppressed: true, SuppressedBy: null }, "suppressed by the user");
        });

        t.Test("Suppress When: a condition that cannot be evaluated is an error", () =>
        {
            var (doc, cut, lp) = Plate("Nope > 1");
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[cut.Id].Error?.Contains("Nope") == true, r.Status[cut.Id].Error ?? "no error");
            t.True(!r.Status[lp.Id].Ok, "the pattern of a failed seed fails as before");
            // and saved and read again
            var back = CadDocument.FromJson(doc.ToJson());
            t.Eq(back.Find(cut.Id)!.SuppressWhen, "Nope > 1");
        });

        t.Test("Design tables: the row in use sets its columns; the table's name is the row number", () =>
        {
            var (doc, cut, lp) = Plate("not Holes");
            doc.Equations.Clear();
            doc.Equations.Add(new Equation { Name = "Area", Expr = "W * 60" });
            doc.Tables = new() { SizeTable() };
            var (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.True(errs.Count == 0, string.Join("; ", errs.Values));
            t.Eq(vals["Size"], 2.0); t.Eq(vals["W"], 100.0); t.Eq(vals["Holes"], 1.0); t.Eq(vals["Area"], 6000.0);
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            t.True(!r.Status[cut.Id].Suppressed, "holes in M");

            doc.Tables[0].Selected = "S";
            r = regen.Rebuild(doc);
            t.True(r.Status[cut.Id].Suppressed && r.Status[lp.Id].Suppressed, "no holes in S");
            t.Near(Volume(r), 80 * 60 * 10, 1e-3);

            // a key no row has: the first row
            doc.Tables[0].Selected = "XL";
            t.Eq(Regenerator.EvaluateEquations(doc).values["W"], 80.0);

            // a variable set twice: the first (the table) wins, the second is an error
            doc.Equations.Add(new Equation { Name = "W", Expr = "5" });
            (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.Eq(vals["W"], 80.0);
            t.True(errs.ContainsKey("W"), "W set twice is an error");

            // saved and read again
            doc.Equations.RemoveAt(1);
            var back = CadDocument.FromJson(doc.ToJson());
            t.Eq(back.Tables![0].Rows[2].Values[0], "120");
            t.Eq(back.Tables[0].Selected, "XL");
        });

        t.Test("Configurations: user-facing parameters, checked, normalized, applied to a copy", () =>
        {
            var (doc, cut, _) = Plate("not Holes");
            doc.Equations.Clear();
            doc.Equations.Add(new Equation { Name = "Thick", Expr = "10", Exposed = true, Description = "Plate thickness", Min = 2, Max = 40 });
            doc.Equations.Add(new Equation { Name = "Slots", Expr = "0", Exposed = true, Min = 0, Max = 1 });
            doc.Equations.Add(new Equation { Name = "Internal", Expr = "Thick * 2" });
            doc.Tables = new() { SizeTable() };
            ((ExtrudeFeature)doc.Features[1]).Depth = "Thick";

            var ps = doc.UserParameters();
            t.True(ps.Select(p => p.Name).SequenceEqual(new[] { "Size", "Thick", "Slots" }), string.Join(",", ps.Select(p => p.Name)));
            t.True(ps[0].IsChoice && ps[0].Choices!.SequenceEqual(new[] { "S", "M", "L" }) && ps[0].Default == "M", "table as a choice");
            t.True(!ps[1].IsToggle && ps[2].IsToggle, "0..1 is a toggle");

            t.Eq(doc.Check("Size", "l"), null);
            t.True(doc.Check("Size", "XL") != null, "not a row");
            t.True(doc.Check("Thick", "50") != null, "above the limit");
            t.True(doc.Check("Thick", "1") != null, "below the limit");
            t.Eq(doc.Check("Thick", "0.5in"), null);
            t.True(doc.Check("Thick", "Internal") != null, "only numbers, not the part's variables");
            t.True(doc.Check("Internal", "3") != null, "internal variables cannot be set");

            // only what differs, as the part spells the names; what it cannot take left out
            var n = doc.Normalize(new Dictionary<string, string> { ["size"] = "l", ["THICK"] = "10", ["Internal"] = "4", ["Slots"] = "1", ["Nope"] = "2" })!;
            t.True(n.Count == 2 && n["Size"] == "L" && n["Slots"] == "1", string.Join(",", n.Select(kv => kv.Key + "=" + kv.Value)));
            t.True(doc.Normalize(new Dictionary<string, string> { ["Size"] = "M", ["Thick"] = "10.0" }) == null, "the defaults are nothing to set");

            // a copy configured; the part itself unchanged
            var big = doc.Configured(new Dictionary<string, string> { ["Size"] = "L", ["Thick"] = "20" });
            t.True(!ReferenceEquals(big, doc), "a copy");
            t.Eq(doc.Tables[0].Selected, "M");
            t.Eq(doc.Equations[0].Expr, "10");
            var r = new Regenerator().Rebuild(big);
            t.Near(Volume(r), 120 * 60 * 20 - 3 * Math.PI * 25 * 20, 120 * 60 * 20 * 0.01);
            t.True(ReferenceEquals(doc.Configured(null), doc) && ReferenceEquals(doc.Configured(new Dictionary<string, string> { ["Size"] = "M" }), doc), "nothing to change: the part");
            var small = new Regenerator().Rebuild(doc.Configured(new Dictionary<string, string> { ["Size"] = "S" }));
            t.True(small.Status[cut.Id].Suppressed, "S has no holes");

            t.Eq(doc.Label(null), "M, Thick 10");
            t.Eq(doc.Label(new Dictionary<string, string> { ["Size"] = "S", ["Thick"] = "0.5in", ["Slots"] = "1" }), "S, Thick 12.7, Slots");
            t.Eq(Configurations.KeyOf(new Dictionary<string, string> { ["Thick"] = "12.70", ["size"] = "S" }), Configurations.KeyOf(new Dictionary<string, string> { ["Size"] = "s", ["thick"] = "12.7" }));

            // the part's own values set from a configuration
            t.True(doc.Apply(new Dictionary<string, string> { ["Size"] = "S", ["Thick"] = "8" }), "changed");
            t.Eq(doc.Tables[0].Selected, "S"); t.Eq(doc.Equations[0].Expr, "8");
            t.True(!doc.Apply(new Dictionary<string, string> { ["Size"] = "S" }), "nothing to change");
        });

        t.Test("Design tables: pasted from a spreadsheet", () =>
        {
            var tab = DesignTable.Parse("Size\tD\tPitch\tHeadD\r\nM3\t3\t0,5\t5.5\r\nM4\t4\t0.7\r\n\r\nM5\t5\t0.8\t8.5\r\n")!;
            t.True(tab.Columns.SequenceEqual(new[] { "D", "Pitch", "HeadD" }), string.Join(",", tab.Columns));
            t.True(tab.Rows.Select(r => r.Key).SequenceEqual(new[] { "M3", "M4", "M5" }), "rows");
            t.Eq(tab.Rows[0].Values[1], "0.5");   // decimal comma
            t.Eq(tab.Rows[1].Values[2], "0");     // blank cell
            var csv = DesignTable.Parse("Key,A,B\nx,1,2\n")!;
            t.True(csv.Rows.Single().Values.SequenceEqual(new[] { "1", "2" }), "comma separated");
            t.True(DesignTable.Parse("just one line") == null && DesignTable.Parse("") == null, "no table");
        });

        t.Test("Assemblies: one part file, components configured each their own way; caches per configuration", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-config-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var (part, _, _) = Plate("not Holes");
            part.Equations.Clear();
            part.Equations.Add(new Equation { Name = "Thick", Expr = "10", Exposed = true, Min = 1 });
            ((ExtrudeFeature)part.Features[1]).Depth = "Thick";
            part.Tables = new() { SizeTable() };
            var pp = Path.Combine(dir, "Plate.sprt");
            part.Save(pp);

            var m = Core.Assembly.AssemblyModel.New();
            var a = m.Insert(pp, Transform.Identity);
            var b = m.Insert(pp, Transform.Translation(new Vec3(0, 100, 0)));
            var c = m.Insert(pp, Transform.Translation(new Vec3(0, 200, 0)));
            static double Width(Core.Assembly.PartDisplay d) => d.Mesh.Bounds.Max.X - d.Mesh.Bounds.Min.X;
            static double Height(Core.Assembly.PartDisplay d) => d.Mesh.Bounds.Max.Z - d.Mesh.Bounds.Min.Z;
            t.True(ReferenceEquals(m.DisplayOf(a), m.DisplayOf(b)), "the part as it is: one display");

            var doc = m.PartDocument(b)!;
            m.Configure(new[] { b }, doc, new Dictionary<string, string> { ["Size"] = "L", ["Thick"] = "20" });
            m.Configure(new[] { c }, doc, new Dictionary<string, string> { ["Size"] = "M", ["Thick"] = "10" });
            t.True(c.Parameters == null, "the part's own values: nothing set");
            t.True(b.Parameters!.Count == 2 && b.Parameters["Size"] == "L", "only what differs is kept");
            t.Near(Width(m.DisplayOf(a)!), 100, 1e-6); t.Near(Height(m.DisplayOf(a)!), 10, 1e-6);
            t.Near(Width(m.DisplayOf(b)!), 120, 1e-6); t.Near(Height(m.DisplayOf(b)!), 20, 1e-6);
            t.True(ReferenceEquals(m.DisplayOf(a), m.DisplayOf(c)), "same configuration, same display");
            t.Eq(m.ConfigLabel(b), "L, Thick 20");
            t.Eq(m.Parts.Count, 2);
            // a configured face is mated as it is in that configuration
            var top = m.DisplayOf(b)!.Mesh.Faces.First(f => f.Name.EndsWith(".End") && f.FirstPlane.Normal.Z > 0.5);
            t.Near(m.FaceGeom(b.Id, top.Name)!.P.Z, 20, 1e-6);

            // saved, then opened without the part: each configuration from its own cache
            var ap = Path.Combine(dir, "Plates.sasm");
            m.Save(ap);
            t.Eq(m.Doc.Caches.Count, 2);
            File.Move(pp, pp + ".gone");
            var back = Core.Assembly.AssemblyModel.Open(Core.Assembly.AssemblyDocument.Load(ap), ap);
            var bb = back.Doc.Find(b.Id)!;
            t.True(bb.Parameters?["Size"] == "L", "parameters saved");
            t.Eq(back.DisplayOf(bb)!.Source, Core.Assembly.PartSource.Cache);
            t.Near(Width(back.DisplayOf(bb)!), 120, 1e-3);
            t.Near(Width(back.DisplayOf(back.Doc.Find(a.Id)!)!), 100, 1e-3);
            t.Eq(back.ConfigLabel(bb), "L, 20");   // (the part not loaded: the values as set)

            // back as it was: the configuration's display and cache dropped
            File.Move(pp + ".gone", pp);
            back = Core.Assembly.AssemblyModel.Open(Core.Assembly.AssemblyDocument.Load(ap), ap);
            bb = back.Doc.Find(b.Id)!;
            back.Configure(new[] { bb }, back.PartDocument(bb)!, null);
            t.True(bb.Parameters == null && back.Parts.Count == 1, "one display again");
            back.RefreshCaches();
            t.Eq(back.Doc.Caches.Count, 1);

            // lightweight: a configuration with no cache yet is built; with one, shown from it
            back.Configure(new[] { bb }, back.PartDocument(bb)!, new Dictionary<string, string> { ["Size"] = "S" });
            bb.Lightweight = true;
            back.LoadPart(Core.Assembly.ComponentResolver.Key(bb.Part));
            t.True(back.DisplayOf(bb)!.Source != Core.Assembly.PartSource.Cache, "no cache: built");
            back.RefreshCaches();
            back.LoadPart(Core.Assembly.ComponentResolver.Key(bb.Part));
            t.Eq(back.DisplayOf(bb)!.Source, Core.Assembly.PartSource.Cache);
            t.Near(Width(back.DisplayOf(bb)!), 80, 1e-3);
            t.True(back.PartDocument(bb) != null, "a lightweight component's part read to configure it");
        });

        t.Test("Demos: the configurable gear solid when small; the screw sizes from one part file", () =>
        {
            var gear = Demos.ConfigGear();
            string Id(string name) => gear.Features.First(f => f.Name == name).Id;
            var regen = new Regenerator();
            foreach (var (z, recess, holes) in new[] { ("20", false, false), ("26", true, false), ("36", true, true) })
            {
                var r = regen.Rebuild(gear.Configured(new Dictionary<string, string> { ["z"] = z }));
                t.True(r.Status.Values.All(s => s.Ok), $"z={z}: " + string.Join("; ", r.Status.Values.Select(s => s.Error).OfType<string>()));
                t.True(r.Status[Id("Web recess")].Suppressed != recess && r.Status[Id("Web recess (back)")].Suppressed != recess, $"z={z}: recess {recess}");
                t.True(r.Status[Id("Lightening hole")].Suppressed != holes && r.Status[Id("Lightening holes")].Suppressed != holes, $"z={z}: holes {holes}");
            }

            var m = Demos.ScrewSizes();
            t.Eq(m.Doc.Components.Count, 7);
            t.Eq(m.Parts.Count, 7);
            t.True(m.Parts.Values.All(p => p.Problem == null && p.Mesh.Indices.Length > 0), "every size built");
            var m12 = m.Doc.Components[^1];
            t.Eq(m.ConfigLabel(m12), "M12, L 50, Threaded");
            // head 12 high, 50 under it
            var d = m.DisplayOf(m12)!;
            t.Near(d.Mesh.Bounds.Max.Z - d.Mesh.Bounds.Min.Z, 62, 0.05);
            t.Near(d.Mesh.Bounds.Max.X - d.Mesh.Bounds.Min.X, 18, 0.05);
            // the thread modeled (no helix to draw); unthreaded: a plain shank, more material
            t.True(d.Threads.Count == 0, "modeled thread");
            var plain = Core.Assembly.AssemblyModel.Build(m.PartDocument(m12)!.Configured(new Dictionary<string, string> { ["Size"] = "M12", ["L"] = "50", ["Threaded"] = "0" }));
            t.True(plain.mesh.Volume > d.Mesh.Volume + 1, $"thread cut: {d.Mesh.Volume:0} < {plain.mesh.Volume:0}");
            // a cosmetic thread reaches the assembly's display (drawn as a helix where the component is)
            var cos = m.PartDocument(m12)!.Clone();
            cos.Features.OfType<ThreadFeature>().Single().Cosmetic = true;
            var built = Core.Assembly.AssemblyModel.Build(cos);
            t.True(built.threads.Count == 1 && built.threads[0].External, "cosmetic thread kept for display");
        });

        t.Test("Rebuild cache: equations seen by their values (a description or same value rebuilds nothing)", () =>
        {
            var (doc, _, _) = Plate(null);
            var regen = new Regenerator();
            regen.Rebuild(doc);
            doc.Equations[0].Description = "Width"; doc.Equations[0].Exposed = true;
            t.Eq(regen.Rebuild(doc).Rebuilt, 0);
            doc.Equations[0].Expr = "50 * 2";
            t.Eq(regen.Rebuild(doc).Rebuilt, 0);
            doc.Equations[0].Expr = "90";
            t.True(regen.Rebuild(doc).Rebuilt > 0, "a new value rebuilds");
        });
    }
}

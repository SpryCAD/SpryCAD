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
            t.Eq(Expr.Eval("1in == 25.4mm"), 1.0);
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

        t.Test("Hole Wizard: a hole on the plane the part stands on drills into the part (it drilled away, cutting nothing); Reverse turns it", () =>
        {
            // (the plate extruded from the Front plane along its normal, the holes' sketch on that plane, as a flange on the Top plane)
            foreach (var reverse in new[] { false, true })
            {
                var (doc, cut, lp) = Plate(null);
                var before = Volume(new Regenerator().Rebuild(doc));
                var sk = new Sketch();
                sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(60, 30)).Id });
                var s3 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                var h = doc.AddFeature(new HoleFeature { SketchId = s3.Id, ThroughAll = true, Diameter = "6", Reverse = reverse });
                var r = new Regenerator().Rebuild(doc);
                t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
                var cutAway = before - Volume(r);
                if (reverse) t.Near(cutAway, 0, 3);
                else t.Near(cutAway, Math.PI * 3 * 3 * 10, 3); // (the faceted cylinder of the mesh)
            }
        });

        t.Test("Fillet whose foot just touches the bolt holes' rims (the pipe flange, 20 holes): joined as the exact boolean joins it (it came out wrong, the body open)", () =>
        {
            // (the hub fillet's foot at HubD/2 + Rfil = 49, the holes' rims reaching in to PCD/2 - BoltD/2 = 49)
            static double Build(bool direct)
            {
                var old = SpryCad.Kernel.DirectBlend.Enabled;
                SpryCad.Kernel.DirectBlend.Enabled = direct;
                try
                {
                    var doc = Demos.Flange();
                    doc.Equations.First(e => e.Name == "BoltD").Expr = "20";
                    return Volume(new Regenerator().Rebuild(doc));
                }
                finally { SpryCad.Kernel.DirectBlend.Enabled = old; }
            }
            t.Near(Build(direct: true), Build(direct: false), 1e-3);
        });

        t.Test("Hole Wizard: Clear material above cuts the hole's top diameter back out through the part above the sketch's plane (a fillet over the holes)", () =>
        {
            // (a plate 10 thick extruded both ways from the Front plane, the hole drilled 2 deep from it: 5 of plate above)
            foreach (var (type, clear, removed) in new[]
            {
                (HoleType.Simple, false, Math.PI * 9 * 2), (HoleType.Simple, true, Math.PI * 9 * 7),
                (HoleType.Counterbore, false, Math.PI * 25 * 1 + Math.PI * 9 * 1), (HoleType.Counterbore, true, Math.PI * 25 * 6 + Math.PI * 9 * 1),
            })
            {
                var doc = new CadDocument();
                var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60) });
                doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10", End = EndCondition.MidPlane });
                var before = Volume(new Regenerator().Rebuild(doc));
                var sk = new Sketch();
                sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(20, 10)).Id });
                var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                var h = doc.AddFeature(new HoleFeature
                {
                    SketchId = s2.Id, Type = type, Diameter = "6", ThroughAll = false, Depth = "2", DrillPoint = false,
                    CbDiameter = "10", CbDepth = "1", ClearAbove = clear,
                });
                var r = new Regenerator().Rebuild(doc);
                t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
                t.Near(before - Volume(r), removed, removed * 0.02); // (the faceted cylinders of the mesh)
            }
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

        t.Test("Threads start at the free end whatever the length: the socket screw's at its tip, running to the head (the point picked stays put)", () =>
        {
            foreach (var len in new[] { "12", "20", "60" })
            {
                var doc = Demos.SocketScrew();
                doc.Equations.First(e => e.Name == "L").Expr = len;
                var r = new Regenerator().Rebuild(doc);
                var th = doc.Features.First(f => f is ThreadFeature);
                t.True(r.Status[th.Id].Ok, $"L {len}: {r.Status[th.Id].Error}");
                var c = r.State.Cosmetics[th.Id];
                // (the head on the Front plane, the shank from its underside at k = 6 to the tip at k + L, M6 the size in use)
                var tip = 6 + double.Parse(len, System.Globalization.CultureInfo.InvariantCulture);
                t.Near(Math.Abs(c.Origin.Z), tip, 1e-6);
                t.True(Math.Abs(c.Origin.Z + c.Axis.Z * c.Length) < tip, $"L {len}: the thread runs from the tip toward the head");
            }
        });

        t.Test("Design tables: one named after an equation lists its standard values (each row's key one), user-facing as a choice in its place", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "L", Expr = "10mm", Exposed = true });
            doc.Tables = new()
            {
                new DesignTable
                {
                    Name = "L", Selected = "20mm",
                    Rows = { new DesignRow { Key = "12mm" }, new DesignRow { Key = "16mm" }, new DesignRow { Key = "20mm" } },
                },
            };
            var (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.True(errs.Count == 0, string.Join("; ", errs.Values));
            t.Eq(vals["L"].Value, 20.0);
            var ps = doc.UserParameters();
            t.True(ps.Count == 1 && ps[0].IsChoice && ps[0].Choices!.Count == 3, "L offered as its standard values, once");
            var configured = doc.Configured(new Dictionary<string, string> { ["L"] = "12mm" });
            t.Eq(Regenerator.EvaluateEquations(configured).values["L"].Value, 12.0);
        });

        t.Test("Design tables as configurations: a row picks another table's row and sets an equation, and gives the part number and description", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "L", Expr = "10mm" });
            doc.Tables = new()
            {
                new DesignTable
                {
                    Name = "Config", Columns = { "Size", "L", TableConfig.PartNumber, TableConfig.Description }, Selected = "M6x20",
                    Rows =
                    {
                        new DesignRow { Key = "M5x12", Values = { "M5", "12mm", "SC-0512", "Cap screw M5x12" } },
                        new DesignRow { Key = "M6x20", Values = { "M6", "20mm", "SC-0620", "Cap screw M6x20" } },
                    },
                },
                new DesignTable { Name = "Size", Columns = { "D" }, Rows = { new DesignRow { Key = "M5", Values = { "5mm" } }, new DesignRow { Key = "M6", Values = { "6mm" } } } },
            };
            var (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.True(errs.Count == 0, string.Join("; ", errs.Values));
            t.Eq(vals["D"].Value, 6.0); t.Eq(vals["L"].Value, 20.0); t.Eq(vals["Size"].Value, 2.0);
            t.True(!vals.ContainsKey(TableConfig.PartNumber), "a property is not a variable");
            var props = TableConfig.PropertiesOf(doc);
            t.Eq(props[TableConfig.PartNumber], "SC-0620"); t.Eq(props[TableConfig.Description], "Cap screw M6x20");
            doc.Tables[0].Selected = "M5x12";
            (vals, _) = Regenerator.EvaluateEquations(doc);
            t.Eq(vals["D"].Value, 5.0); t.Eq(vals["L"].Value, 12.0);
            t.Eq(TableConfig.PropertiesOf(doc)[TableConfig.PartNumber], "SC-0512");
            // the table picked from listed first: the same
            doc.Tables.Reverse();
            t.Eq(Regenerator.EvaluateEquations(doc).values["D"].Value, 5.0);
        });

        t.Test("Design tables: the row in use sets its columns; the table's name is the row number", () =>
        {
            var (doc, cut, lp) = Plate("not Holes");
            doc.Equations.Clear();
            doc.Equations.Add(new Equation { Name = "Area", Expr = "W * 60" });
            doc.Tables = new() { SizeTable() };
            var (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.True(errs.Count == 0, string.Join("; ", errs.Values));
            t.Eq(vals["Size"].Value, 2.0); t.Eq(vals["W"].Value, 100.0); t.Eq(vals["Holes"].Value, 1.0); t.Eq(vals["Area"].Value, 6000.0);
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            t.True(!r.Status[cut.Id].Suppressed, "holes in M");

            doc.Tables[0].Selected = "S";
            r = regen.Rebuild(doc);
            t.True(r.Status[cut.Id].Suppressed && r.Status[lp.Id].Suppressed, "no holes in S");
            t.Near(Volume(r), 80 * 60 * 10, 1e-3);

            // a key no row has: the first row
            doc.Tables[0].Selected = "XL";
            t.Eq(Regenerator.EvaluateEquations(doc).values["W"].Value, 80.0);

            // an equation a table's column names: the table sets it, the row's value in place of its own (TableConfig)
            doc.Equations.Add(new Equation { Name = "W", Expr = "5" });
            (vals, errs) = Regenerator.EvaluateEquations(doc);
            t.Eq(vals["W"].Value, 80.0);
            t.True(!errs.ContainsKey("W"), "W set by the table, not an error");

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
            // the cosmetic thread reaches the assembly's display (drawn as a helix where the component is); unthreaded: none
            t.True(d.Threads.Count == 1 && d.Threads[0].External, "cosmetic thread kept for display");
            var plain = Core.Assembly.AssemblyModel.Build(m.PartDocument(m12)!.Configured(new Dictionary<string, string> { ["Size"] = "M12", ["L"] = "50", ["Threaded"] = "0" }));
            t.Eq(plain.threads.Count, 0);
            // exports model it: the thread cut, less material
            var modeled = new Regenerator { ThreadOverride = ThreadDisplay.Modeled }.Rebuild(m.DisplayOf(m12)!.Doc!).State.Bodies.Sum(b => b.Mesh.Volume);
            t.True(modeled < d.Mesh.Volume - 1, $"thread cut on export: {modeled:0} < {d.Mesh.Volume:0}");
        });

        t.Test("Assemblies: threads shown all modeled or as set (View > Threads), the caches kept as set", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-config-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var ap = Path.Combine(dir, "Screws.sasm");
            Demos.ScrewSizes().Save(ap);
            var m = Core.Assembly.AssemblyModel.Open(Core.Assembly.AssemblyDocument.Load(ap), ap, defer: true);
            var m12 = m.Doc.Components[^1];
            var cosmetic = m.DisplayOf(m12)!;
            t.True(cosmetic.Cached && cosmetic.Threads.Count == 1, "as set: cosmetic, from the cache");
            var caches = m.Doc.Caches.ToDictionary(kv => kv.Key, kv => m.Doc.Files[kv.Value]);

            m.SetThreads(ThreadDisplay.Modeled);
            t.Eq(m.Pending.Count, 7);
            foreach (var job in m.PendingJobs()) t.True(m.Install(job, job.Build()), "installed");
            var modeled = m.DisplayOf(m12)!;
            t.True(modeled.Threads.Count == 0 && !modeled.Cached, "modeled: no cosmetic thread");
            t.True(modeled.Mesh.Volume < cosmetic.Mesh.Volume - 1 && modeled.Mesh.Indices.Length > cosmetic.Mesh.Indices.Length, "the thread cut");
            m.RefreshCaches();
            t.True(m.Doc.Caches.All(kv => ReferenceEquals(m.Doc.Files[kv.Value], caches[kv.Key])), "the caches as set kept");

            m.SetThreads(ThreadDisplay.AsSet);
            t.True(m.Pending.Count == 0 && m.DisplayOf(m12)!.Cached && m.DisplayOf(m12)!.Threads.Count == 1, "as set again: from the caches");
        });

        t.Test("Assemblies open from their caches when current; others built afterwards (geometry version, part revision)", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-config-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var (part, _, _) = Plate(null);
            part.Tables = new() { SizeTable() };
            var pp = Path.Combine(dir, "Plate.sprt");
            part.Save(pp);
            var m = Core.Assembly.AssemblyModel.New();
            var a = m.Insert(pp, Transform.Identity);
            var b = m.Insert(pp, Transform.Translation(new Vec3(0, 100, 0)));
            m.Configure(new[] { b }, m.PartDocument(b)!, new Dictionary<string, string> { ["Size"] = "L" });
            var ap = Path.Combine(dir, "Plates.sasm");
            m.Save(ap);
            static double Width(Core.Assembly.PartDisplay d) => d.Mesh.Bounds.Max.X - d.Mesh.Bounds.Min.X;

            // the cache: version 3, with the geometry version and the threads
            var thread = new CosmeticThread(new Vec3(1, 2, 3), Vec3.UnitZ, Vec3.UnitX, 5, -1.5, 20, true);
            var e = Core.Assembly.MeshCache.Read(Core.Assembly.MeshCache.Write(m.DisplayOf(a)!.Mesh, part.Color, new[] { thread }));
            t.True(e.Current && e.Version == GeometryVersion.Current && e.Threads.Single() == thread, "round trip");

            // opened again: every display from its cache, nothing to build
            var doc = Core.Assembly.AssemblyDocument.Load(ap);
            var back = Core.Assembly.AssemblyModel.Open(doc, ap, defer: true);
            t.True(back.Pending.Count == 0 && back.Parts.Values.All(p => p.Cached && p.Source == Core.Assembly.PartSource.File && p.Part != null), "all from current caches");
            t.Near(Width(back.DisplayOf(back.Doc.Find(b.Id)!)!), 120, 1e-3);

            // caches made by older code: shown, and built afterwards
            doc = Core.Assembly.AssemblyDocument.Load(ap);
            foreach (var name in doc.Caches.Values) BitConverter.GetBytes(0).CopyTo(doc.Files[name], 7);
            back = Core.Assembly.AssemblyModel.Open(doc, ap, defer: true);
            t.Eq(back.Pending.Count, 2);
            var bb = back.Doc.Find(b.Id)!;
            t.True(back.DisplayOf(bb)!.Building && Width(back.DisplayOf(bb)!) > 119, "the old cache shown meanwhile");
            foreach (var job in back.PendingJobs()) t.True(back.Install(job, job.Build()), "installed");
            t.True(back.Pending.Count == 0 && back.Parts.Values.All(p => !p.Building && !p.Cached), "built");
            back.RefreshCaches();
            t.True(back.Doc.Caches.Values.All(n => Core.Assembly.MeshCache.VersionOf(back.Doc.Files[n]) == GeometryVersion.Current), "caches made again");

            // the part changed since: its old display shown until built; a job made stale by loading it again is dropped
            part.Tables![0].Rows[1].Values[0] = "90";   // M: 90 wide
            part.Save(pp);
            back = Core.Assembly.AssemblyModel.Open(Core.Assembly.AssemblyDocument.Load(ap), ap, defer: true);
            var aa = back.Doc.Find(a.Id)!;
            t.Near(Width(back.DisplayOf(aa)!), 100, 1e-3);
            var jobs = back.PendingJobs();
            t.Eq(jobs.Count, 2);
            t.True(back.Install(jobs[0], jobs[0].Build()), "first built");
            t.Near(Width(back.DisplayOf(aa)!), 90, 1e-3);
            back.LoadPart(Core.Assembly.ComponentResolver.Key(aa.Part));
            t.True(!back.Install(jobs[1], jobs[1].Build()) && back.Pending.Count == 0, "stale job dropped");
            t.Near(Width(back.DisplayOf(back.Doc.Find(b.Id)!)!), 120, 1e-3);
        });

        t.Test("Parts keep their display in the file: read back for its content and geometry version only", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-config-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var (part, _, _) = Plate(null);
            var r = new Regenerator().Rebuild(part);
            t.True(Core.Assembly.MeshCache.BuildSeconds(r) > 0, "build time known");
            part.Display = Core.Assembly.MeshCache.Of(r, part.Color);
            var pp = Path.Combine(dir, "Plate.sprt");
            part.Save(pp);
            var back = CadDocument.Load(pp);
            t.True(back.Display != null, "kept");
            var e = Core.Assembly.MeshCache.Read(back.Display!);
            t.Near(e.Mesh.Volume, r.State.Bodies.Sum(b => b.Mesh.Volume), 1e-6);
            t.True(back.Clone().Display == null, "not copied with the history");

            // a display for other content (the model changed elsewhere): not read
            static void Sql(string path, string sql)
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                c.Open();
                using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
            }
            var other = Path.Combine(dir, "Other.sprt");
            File.Copy(pp, other);
            Sql(other, "UPDATE Cache SET CacheKey = randomblob(32)");
            t.True(CadDocument.Load(other).Display == null, "another content's display dropped");
            t.True(CadDocument.Load(other).ToJson() == back.ToJson(), "the part itself unchanged");

            // made by older geometry code: not read
            var mesh = back.Display!.ToArray();
            BitConverter.GetBytes(0).CopyTo(mesh, 7);
            back.Display = mesh;
            back.Save(pp);
            t.True(CadDocument.Load(pp).Display == null, "older geometry code's display dropped");
            // a display saved again with an unchanged part replaces it
            back.Display = Core.Assembly.MeshCache.Of(r, part.Color);
            back.Save(pp);
            t.True(CadDocument.Load(pp).Display != null, "the file itself still good");
            t.Eq(SpryCad.Core.Storage.PartFile.History(pp).Count, 1);
            // a database's bytes (as an assembly embeds them) read the same
            t.True(DocumentContainer.Load(File.ReadAllBytes(pp), "x").Display != null, "read from bytes");
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
            // back to the value before (undo): what was built for it is still there
            doc.Equations[0].Expr = "100";
            t.Eq(regen.Rebuild(doc).Rebuilt, 0);
            doc.Equations[0].Expr = "90";
            t.Eq(regen.Rebuild(doc).Rebuilt, 0);
            regen.Invalidate();
            t.Eq(regen.Rebuild(doc).Rebuilt, doc.Features.Count);
        });
    }
}

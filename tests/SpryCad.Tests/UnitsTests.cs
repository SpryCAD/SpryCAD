using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Units (docs/units.md): a part shown in other units keeps its size; which variables are lengths; values given units.</summary>
static class UnitsTests
{
    // a block W wide, H high (W - 2*Gap, from equations without units) and T deep, its draft Ang
    static CadDocument Block(UnitSystem units)
    {
        var doc = new CadDocument { Units = units };
        doc.Equations.AddRange(new[]
        {
            new Equation { Name = "W", Expr = "40" }, new Equation { Name = "Gap", Expr = "5" },
            new Equation { Name = "H", Expr = "W - 2*Gap" }, new Equation { Name = "T", Expr = "8" },
            new Equation { Name = "N", Expr = "4" }, new Equation { Name = "Ang", Expr = "0" },
        });
        var sk = CoreTests.Rect(40, 30, "W");
        var hDim = sk.Constraints.Last(c => c.Type == SpryCad.Core.Sketching.CType.Distance);
        hDim.Expr = "H";
        var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "T + 2", Draft = "Ang" });
        return doc;
    }

    static Vec3 Size(CadDocument doc)
    {
        var r = new Regenerator().Rebuild(doc);
        var b = r.State.Bounds;
        return b.Max - b.Min;
    }

    // (the display mesh's bounds: a little past the solid's)
    static bool Same(Vec3 a, Vec3 b) => (a - b).Length < 0.01;

    public static void Run(TestRunner t)
    {
        t.Test("Units: a part shown in inches keeps its size (its values without units in the units they were written in), and keeps them saved", () =>
        {
            var doc = Block(UnitSystem.MMGS);
            var mm = Size(doc);
            t.True(Same(mm, new Vec3(40, 30, 10)), $"{mm}");
            doc.ShowIn(UnitSystem.IPS);
            t.Eq(doc.Units, UnitSystem.IPS); t.Eq(doc.ValueUnits, UnitSystem.MMGS);
            t.True(Same(Size(doc), mm), $"{Size(doc)}");
            var path = Path.Combine(Path.GetTempPath(), "sprycad-tests", $"units-{Guid.NewGuid():N}.sprt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PartFile.Save(doc, path);
            var back = PartFile.Load(path);
            t.Eq(back.ValueUnits, UnitSystem.MMGS);
            t.True(Same(Size(back), mm), "the same read back");
            // a part made in inches: its values without units are inches
            var inch = Block(UnitSystem.IPS);
            t.True(Same(Size(inch), new Vec3(40, 30, 10) * 25.4), $"{Size(inch)}");
        });

        t.Test("Units: which variables are lengths, worked out from where they are used (a dimension, a depth, a count), through equations and functions", () =>
        {
            var doc = new CadDocument();
            doc.Equations.AddRange(new[]
            {
                new Equation { Name = "OD", Expr = "150" }, new Equation { Name = "PCD", Expr = "OD - 32" },
                new Equation { Name = "N", Expr = "8" }, new Equation { Name = "Unused", Expr = "3" },
                new Equation { Name = "Taper", Expr = "5" }, new Equation { Name = "Both", Expr = "2" },
            });
            var uses = new[]
            {
                new UnitInference.ValueUse("OD", ValueKind.Length),
                new UnitInference.ValueUse("PCD*sin(180/N)", ValueKind.Length),
                new UnitInference.ValueUse("N", ValueKind.Number),
                new UnitInference.ValueUse("Taper", ValueKind.Angle),
                new UnitInference.ValueUse("Both", ValueKind.Length),
                new UnitInference.ValueUse("Both", ValueKind.Number),
            };
            var r = UnitInference.Infer(doc, uses);
            t.Eq(r.Kinds["OD"], UnitInference.VarKind.Length);
            t.Eq(r.Kinds["PCD"], UnitInference.VarKind.Length);
            t.Eq(r.Kinds["N"], UnitInference.VarKind.Number);
            t.Eq(r.Kinds["Taper"], UnitInference.VarKind.Angle);
            t.Eq(r.Kinds["Unused"], UnitInference.VarKind.Unknown);
            t.Eq(r.Kinds["Both"], UnitInference.VarKind.Conflict);
            t.Eq(UnitInference.WithUnits(r, "OD - 32", "mm"), "OD - 32mm");
            t.Eq(UnitInference.ConstantWithUnit(r, "OD", "150", "mm"), "150mm");
            t.Eq(UnitInference.ConstantWithUnit(r, "N", "8", "mm"), "8");
            t.Eq(UnitInference.WithUnits(r, "PCD*sin(180/N)", "mm"), "PCD*sin(180/N)");
            // a number written as a factor is plain: 2*Gap used as a length makes Gap the length
            var g = new CadDocument();
            g.Equations.AddRange(new[] { new Equation { Name = "Gap", Expr = "5" }, new Equation { Name = "K", Expr = "3" } });
            var rg = UnitInference.Infer(g, new[] { new UnitInference.ValueUse("2*Gap + 1", ValueKind.Length), new UnitInference.ValueUse("K*Gap", ValueKind.Length) });
            t.Eq(rg.Kinds["Gap"], UnitInference.VarKind.Length);
            t.Eq(UnitInference.WithUnits(rg, "2*Gap + 1", "mm"), "2*Gap + 1mm");
            // K: a length times K a length, so K is a plain number
            t.Eq(rg.Kinds["K"], UnitInference.VarKind.Number);
        });

        t.Test("Units: a part's values given units (checked against what they were), then shown in inches, the same size; a value typed in the part's units after", () =>
        {
            var doc = Block(UnitSystem.MMGS);
            var mm = Size(doc);
            var uses = Regenerator.ReadValues(doc);
            t.True(uses.Any(u => u.Text == "W" && u.Kind == ValueKind.Length) && uses.Any(u => u.Text == "Ang" && u.Kind == ValueKind.Angle), string.Join(", ", uses));
            var (n, why) = UnitInference.AddUnits(doc, uses, Regenerator.ValueOf);
            t.True(why == null && n > 0, why ?? "nothing changed");
            string E(string name) => doc.Equations.Single(e => e.Name == name).Expr;
            t.Eq($"{E("W")}|{E("Gap")}|{E("H")}|{E("T")}|{E("N")}|{E("Ang")}", "40mm|5mm|W - 2*Gap|8mm|4|0");
            t.Eq(((ExtrudeFeature)doc.Features[1]).Depth, "T + 2mm");
            t.True(Same(Size(doc), mm), $"{Size(doc)}");
            doc.ShowIn(UnitSystem.IPS);
            t.True(Same(Size(doc), mm), "shown in inches, the same");
            // nothing more to give units to
            t.Eq(UnitInference.AddUnits(doc, Regenerator.ReadValues(doc), Regenerator.ValueOf).changed, 0);
            // a configuration's number for a length given units: in its unit, so H = W - 2*Gap still works
            doc.Equations.Single(e => e.Name == "W").Exposed = true;
            var wide = doc.Configured(new Dictionary<string, string> { ["W"] = "60" });
            t.Eq(wide.Equations.Single(e => e.Name == "W").Expr, "60mm");
            t.True(Same(Size(wide), new Vec3(60, 50, 10)), $"{Size(wide)}");
        });
    }
}

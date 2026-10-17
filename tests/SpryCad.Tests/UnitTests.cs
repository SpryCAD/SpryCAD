using System.Text.RegularExpressions;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// Units: the model is in millimetres whatever the part's units, values carry their own units (typed without one, they
/// get the part's), and the part's units only change what values are shown in.
/// </summary>
static class UnitTests
{
    const double In = 25.4;

    /// <summary>A 2 × 1 in block 0.5 in deep, its values typed as they would be in an inch part (no units).</summary>
    static CadDocument InchBlock()
    {
        var doc = new CadDocument { Units = UnitSystem.IPS };
        string Typed(string text, ValueKind kind = ValueKind.Length)
        {
            if (!doc.Units.TryEnter(text, kind, null, out var kept, out _, out var err)) throw new Exception(err);
            return kept;
        }
        var sk = CoreTests.Rect(2 * In, 1 * In);
        var dims = sk.Constraints.Where(c => c.IsDimension).ToList();
        dims[0].Expr = Typed("2"); dims[1].Expr = Typed("1");
        var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = Typed("0.5") });
        return doc;
    }

    static ModelState Build(CadDocument doc, TestRunner t)
    {
        var r = new Regenerator().Rebuild(doc);
        t.True(r.Status.Values.All(x => x.Ok), "rebuild: " + string.Join("; ", r.Status.Values.Select(x => x.Error)));
        return r.State;
    }

    static double Volume(ModelState st) => st.Bodies.Sum(b => NTess.ToSolid(b.Solid.Native!, 1e-3).Volume());

    static string StepOf(ModelState st)
    {
        using var ms = new MemoryStream();
        StepWriter.Write(ms, st.Bodies.Select(b => (b.Id, b.Solid)), "block");
        // (but the time it was written)
        return Regex.Replace(System.Text.Encoding.ASCII.GetString(ms.ToArray()), @"FILE_NAME\(.*\);", "");
    }

    /// <summary>The same STEP text, or where they first differ.</summary>
    static void SameStep(TestRunner t, string a, string b, string what)
    {
        var la = a.Split('\n'); var lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            if (i >= la.Length || i >= lb.Length || la[i] != lb[i])
                t.True(false, $"{what}: line {i + 1}: {(i < la.Length ? la[i] : "(none)")} / {(i < lb.Length ? lb[i] : "(none)")}");
    }

    // (the model's bounds are its display mesh's, a hair outside the exact size)
    const double BoundsTol = 0.01;

    public static void Run(TestRunner t)
    {
        t.Test("Units: values carry units and mix freely; a plain number and a length do not add", () =>
        {
            var q = Expr.Evaluate("1in + 2mm");
            t.True(q.IsLength, "a length"); t.Near(q.Value, 27.4, 1e-12);
            t.Near(Expr.Evaluate("1ft").Value, 304.8, 1e-12);
            t.Near(Expr.Evaluate("2\"").Value, 50.8, 1e-12);
            t.Near(Expr.Evaluate("1.5 cm").Value, 15, 1e-12);
            t.Near(Expr.Evaluate("(1 + 1)in").Value, 50.8, 1e-12);
            var area = Expr.Evaluate("2in * 3mm");
            t.True(area.Length == 2, "length × length is an area"); t.Near(area.Value, 152.4, 1e-12);
            t.True(Expr.Evaluate("sqrt(area)", n => n == "area" ? area : null).IsLength, "an area's root is a length");
            t.True(Expr.Evaluate("10mm / 2mm").IsNumber, "a ratio has no units");
            t.True(Expr.Evaluate("30deg").IsAngle && Expr.Evaluate("45°").IsAngle, "angles");
            t.Near(Expr.Evaluate("0.5rad").Value, 0.5 * 180 / Math.PI, 1e-12);
            t.Near(Expr.Evaluate("sin(30deg)").Value, 0.5, 1e-12);
            t.Near(Expr.Evaluate("sin(30)").Value, 0.5, 1e-12);
            t.True(Expr.Evaluate("atan(1)") is { IsNumber: true, Value: 45 }, "an inverse trig function gives degrees, a plain number");
            t.True(!Expr.TryEvaluate("10 + 1in", null, out _, out var err) && err!.Contains("give 10 its units"), "number + length: " + err);
            t.True(!Expr.TryEvaluate("1in > 30deg", null, out _, out _), "length compared with an angle");
            t.Eq(Expr.Evaluate("W > 0", n => n == "W" ? Quantity.Mm(5) : null).Value, 1.0); // (a plain 0 takes any units)
            t.Eq(Expr.Evaluate("1in == 25.4mm").Value, 1.0);
            t.True(Expr.Evaluate("max(1in, 30mm)").Value == 30, "max of lengths");
            t.True(Expr.Identifiers("10mm + (W/2)in + 3\" + \"D1@Sketch1\"").SequenceEqual(new[] { "W", "D1@Sketch1" }), "units are not names");
            t.True(Expr.IsLiteral("2.5 in", out var u) && u == "in" && Expr.IsLiteral("10", out var none) && none == "" && !Expr.IsLiteral("W", out _), "literals");
        });

        t.Test("Units: a value typed without a unit gets the part's; one with keeps its own", () =>
        {
            void Entered(UnitSystem us, string typed, ValueKind kind, string kept, double value, Func<string, Quantity?>? lookup = null)
            {
                t.True(us.TryEnter(typed, kind, lookup, out var k, out var v, out var err), $"{typed}: {err}");
                t.Eq(k, kept);
                t.Near(v.Value, value, 1e-9);
            }
            Entered(UnitSystem.IPS, "10", ValueKind.Length, "10in", 254);
            Entered(UnitSystem.MMGS, "10", ValueKind.Length, "10mm", 10);
            Entered(UnitSystem.MMGS, "10in", ValueKind.Length, "10in", 254);
            Entered(UnitSystem.IPS, "5mm", ValueKind.Length, "5mm", 5);
            Entered(UnitSystem.IPS, "W/2", ValueKind.Length, "(W/2)in", 25.4, n => n == "W" ? new Quantity(2) : null);
            Entered(UnitSystem.IPS, "W/2", ValueKind.Length, "W/2", 25.4, n => n == "W" ? Quantity.Mm(50.8) : null);
            Entered(UnitSystem.IPS, "30", ValueKind.Angle, "30deg", 30);
            Entered(UnitSystem.IPS, "6", ValueKind.Number, "6", 6);
            t.True(!UnitSystem.IPS.TryEnter("6mm", ValueKind.Number, null, out _, out _, out _), "a count with units");
            t.True(!UnitSystem.IPS.TryEnter("2in * 3in", ValueKind.Length, null, out _, out _, out _), "an area for a length");
            t.True(!UnitSystem.IPS.TryEnter("2in", ValueKind.Angle, null, out _, out _, out _), "a length for an angle");
            // shown in the part's units (docs/units.md): worked-out values, dimensions whatever they were typed in, and a
            // field's value that is a number alone; a formula as written
            t.Eq(UnitSystem.IPS.Length(12.7), "0.5 in");
            t.Eq(UnitSystem.IPS.Entry(12.7), "0.5in");
            t.Eq(UnitSystem.MMGS.Label(254, "10in"), "254");
            t.Eq(UnitSystem.IPS.Label(254, "10in"), "10");
            t.Eq(UnitSystem.IPS.Label(10, "10mm"), "0.3937");
            t.Eq(UnitFormat.Show("10", ValueKind.Length), "10mm");
            t.Eq(UnitSystem.IPS.Show("12.7mm", ValueKind.Length), "0.5in");
            t.Eq(UnitSystem.IPS.Show("12.7", ValueKind.Length), "0.5in");
            t.Eq(UnitSystem.MMGS.Show("0.5in", ValueKind.Length), "12.7mm");
            t.Eq(UnitSystem.IPS.Show("0.5in", ValueKind.Length), "0.5in");
            t.Eq(UnitSystem.IPS.Show("(W/2)mm", ValueKind.Length), "(W/2)mm");
            t.Eq(UnitSystem.IPS.Show("30deg", ValueKind.Angle), "30deg");
        });

        t.Test("Units: an inch part's values typed in inches build it to size, in millimetres", () =>
        {
            var doc = InchBlock();
            var st = Build(doc, t);
            var b = st.Bounds;
            t.Near(b.Size.X, 2 * In, BoundsTol); t.Near(b.Size.Y, 1 * In, BoundsTol); t.Near(b.Size.Z, 0.5 * In, BoundsTol);
            t.Near(Volume(st), In * In * In, 1e-3);
            // its file keeps the values with their units, and opens the same size
            var back = CadDocument.FromJson(doc.ToJson());
            t.True(back.ToJson().Contains("\"0.5in\""), "the depth kept with its unit");
            t.Near(Volume(Build(back, t)), In * In * In, 1e-3);
        });

        t.Test("Units: switching a part's units changes nothing in the model", () =>
        {
            var doc = InchBlock();
            var before = Build(doc, t);
            var stepBefore = StepOf(before);
            doc.Units = UnitSystem.MMGS;
            var after = Build(doc, t);
            t.Near(Volume(after), Volume(before), 1e-9);
            t.Near(after.Bounds.Diagonal, before.Bounds.Diagonal, 1e-9);
            SameStep(t, StepOf(after), stepBefore, "switched to millimetres");
            // a millimetre part made the same in millimetres: the same block
            var mm = new CadDocument();
            var s = mm.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(50.8, 25.4) });
            mm.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "12.7" });
            // (but the names, made of the parts' own random ids)
            static string Unnamed(string step) => Regex.Replace(step, "'[^']*'", "''");
            SameStep(t, Unnamed(StepOf(Build(mm, t))), Unnamed(stepBefore), "made in millimetres");
        });

        t.Test("Units: STEP of an inch part is written and read back in millimetres, to size", () =>
        {
            var st = Build(InchBlock(), t);
            using var ms = new MemoryStream();
            StepWriter.Write(ms, st.Bodies.Select(b => (b.Id, b.Solid)), "block");
            var back = StepImport.Read(ms.ToArray(), "I.");
            var solid = NTess.ToSolid(back.Solids.Single().Body, 1e-3);
            t.Near(solid.Volume(), In * In * In, 1e-2);
            t.Near(solid.Bounds.Size.X, 2 * In, 1e-6);
        });

        t.Test("Units: inches in a millimetre part, lengths from equations, and a wrong kind of value is the feature's error", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "2in" });
            doc.Equations.Add(new Equation { Name = "N", Expr = "4" });
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(50.8, 20, "W") });
            var e = doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "1in" });
            var st = Build(doc, t);
            t.Near(st.Bounds.Size.X, 50.8, BoundsTol);
            t.Near(st.Bounds.Size.Z, 25.4, BoundsTol);
            t.True(new Regenerator().Rebuild(doc).EquationValues["W"].IsLength, "W is a length");
            e.Depth = "W / N";
            t.Near(Build(doc, t).Bounds.Size.Z, 12.7, BoundsTol);
            e.Depth = "N";   // a plain number: millimetres
            t.Near(Build(doc, t).Bounds.Size.Z, 4, BoundsTol);
            e.Depth = "W * W";
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[e.Id].Error is { } err && err.Contains("Expected a length"), "an area for a depth: " + r.Status[e.Id].Error);
            e.Depth = "N + 1in";
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[e.Id].Error is { } err2 && err2.Contains("Units do not match"), "a number plus a length: " + r.Status[e.Id].Error);
        });
    }
}

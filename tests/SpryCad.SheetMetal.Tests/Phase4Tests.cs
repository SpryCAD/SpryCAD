using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel.Native;
using SpryCad.SheetMetal.Export;
using SpryCad.SheetMetal.Features;
using KVec2 = SpryCad.Kernel.Vec2;

namespace SpryCad.SheetMetal.Tests;

/// <summary>Arcs, cuts, hems and the distance, flange, grain and hem checks (sheet metal spec sections 6, 8 and 9).</summary>
static class Phase4Tests
{
    const string Steel = "Steel CRS", Gauge = "14 ga";

    static Sketch Rect(double w, double h, params (double x, double y, double r)[] circles)
    {
        var sk = new Sketch();
        var ps = new[] { (0.0, 0.0), (w, 0.0), (w, h), (0.0, h) }.Select(p => sk.AddPoint(new KVec2(p.Item1, p.Item2))).ToList();
        for (int i = 0; i < 4; i++) sk.Add(new SkLine { P1 = ps[i].Id, P2 = ps[(i + 1) % 4].Id });
        foreach (var (x, y, r) in circles) sk.Add(new SkCircle { Center = sk.AddPoint(new KVec2(x, y)).Id, R = r });
        return sk;
    }

    static Sketch Circles(params (double x, double y, double r)[] circles)
    {
        var sk = new Sketch();
        foreach (var (x, y, r) in circles) sk.Add(new SkCircle { Center = sk.AddPoint(new KVec2(x, y)).Id, R = r });
        return sk;
    }

    /// <summary>A part with a plate of the sketch (on the front plane) as its base flange.</summary>
    static (CadDocument doc, SheetMetalBody body, BaseFlangeFeature bf) Part(Sketch sketch, string material = Steel, string gauge = Gauge)
    {
        var doc = new CadDocument();
        var body = SheetMetalModule.AddBody(doc, material, gauge);
        var s = doc.AddFeature(new SketchFeature { Sketch = sketch });
        var bf = doc.AddFeature(new BaseFlangeFeature { Body = body.Id, SketchId = s.Id });
        SheetProfiles.Sync(doc);
        return (doc, body, bf);
    }

    /// <summary>The plate's edge on a side of its 100 × 60 rectangle (edges numbered from where the outline starts).</summary>
    static string Side(RegenResult r, string body, BaseFlangeFeature bf, string side)
    {
        var o = r.State.Sheets[body].Graph.Panel(new PanelId(bf.Id + ".P0")).FlatOutline.Outer;
        bool On(Vec2 p) => side switch { "bottom" => Math.Abs(p.Y) < 1e-9, "top" => Math.Abs(p.Y - 60) < 1e-9, "left" => Math.Abs(p.X) < 1e-9, _ => Math.Abs(p.X - 100) < 1e-9 };
        return $"{bf.Id}.P0.Edge({Enumerable.Range(0, o.Count).Single(k => On(o[k]) && On(o[(k + 1) % o.Count]))})";
    }

    static RegenResult Build(TestRunner t, CadDocument doc, bool warningsOk = false)
    {
        var r = new Regenerator().Rebuild(doc);
        foreach (var (id, s) in r.Status)
        {
            t.True(s.Ok, $"{id}: {s.Error}");
            if (!warningsOk) t.True(s.Warnings.Count == 0, $"{id}: {string.Join("; ", s.Warnings)}");
        }
        return r;
    }

    static NBody Solid(RegenResult r) => r.State.Bodies.Single().Solid.Native!;
    static double Volume(NBody b) => NMass.Of(b)!.Volume;
    static IEnumerable<string> Warnings(RegenResult r) => r.Status.Values.SelectMany(s => s.Warnings);

    public static void Run(TestRunner t)
    {
        double th = SheetRules.Resolve(StarterProfile.Profile, Steel, Gauge).Thickness;

        t.Test("Arcs: a plate with a round hole: exact volume, the hole a cylinder, true arcs in the flat and the DXF", () =>
        {
            var (doc, body, _) = Part(Rect(100, 60, (30, 30, 8)));
            var r = Build(t, doc);
            var solid = Solid(r);
            t.Eq(solid.Validate(), null);
            t.Near(Volume(solid), (100 * 60 - Math.PI * 64) * th, 1e-6);
            t.True(solid.Faces.Any(f => f.Surface.Kind == Kernel.SurfaceKind.Cylinder && f.Tag.Name.Contains(".Hole0.Edge(")), "the hole's wall is a cylinder");
            var flat = SheetMetalModule.FlatOf(r.State, body.Id)!;
            t.Eq(flat.Loops.Count, 2);
            t.Near(flat.Area, 100 * 60 - Math.PI * 64, 1e-9);
            t.Near(flat.CutLength, 320 + 2 * Math.PI * 8, 1e-9);
            using var ms = new MemoryStream();
            FlatExport.WriteDxf(ms, flat, OutputStyle.Default);
            var dxf = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            t.True(dxf.Contains("\r\n 42\r\n") || dxf.Contains("\r\n42\r\n"), "arcs as bulges");
        });

        t.Test("Cut: a round hole in a flanged plate; one through both walls of a channel; a profile across an edge is said", () =>
        {
            var (doc, body, bf) = Part(Rect(100, 60));
            var r0 = Build(t, doc);
            doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "bottom"), Side(r0, body.Id, bf, "top") }, Length = "30" });
            var plateHole = doc.AddFeature(new SketchFeature { Sketch = Circles((50, 30, 6)) });
            var c1 = doc.AddFeature(new SheetCutFeature { Body = body.Id, SketchId = plateHole.Id });
            // on the top plane (normal y): through both flanges, 15 up
            var wallHole = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Std = "Top" }, Sketch = Circles((50, -15, 4)) });
            var c2 = doc.AddFeature(new SheetCutFeature { Body = body.Id, SketchId = wallHole.Id });
            var before = Volume(Solid(Build(t, doc.Clone().Also(d => { d.Features.RemoveAll(x => x.Id == c1.Id || x.Id == c2.Id); }))));
            var r = Build(t, doc);
            var solid = Solid(r);
            t.Eq(solid.Validate(), null);
            t.Near(Volume(solid), before - Math.PI * 36 * th - 2 * Math.PI * 16 * th, 1e-6);
            var g = r.State.Sheets[body.Id].Graph;
            t.Eq(g.Panels.Sum(p => p.FlatOutline.Holes.Count), 3);
            // across the plate's edge
            var across = doc.AddFeature(new SketchFeature { Sketch = Circles((0, 30, 5)) });
            var c3 = doc.AddFeature(new SheetCutFeature { Body = body.Id, SketchId = across.Id });
            var bad = new Regenerator().Rebuild(doc);
            t.True(bad.Status[c3.Id].Error?.Contains("crosses") == true, bad.Status[c3.Id].Error ?? "no error");
        });

        t.Test("Cut: a hole too near a bend fails SM-DIST-01; too near a free edge SM-DIST-02", () =>
        {
            var (doc, body, bf) = Part(Rect(100, 60));
            var r0 = Build(t, doc);
            doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "bottom") }, Length = "20" });
            // the plate's bottom edge moved up by the setback: a hole 2 mm above it is far too near the bend
            var ossb = BendMath.Ossb(Math.PI / 2, th, th);
            var near = doc.AddFeature(new SketchFeature { Sketch = Circles((50, ossb + 2 + 3, 3), (98, 30, 1)) });
            var cut = doc.AddFeature(new SheetCutFeature { Body = body.Id, SketchId = near.Id });
            var r = Build(t, doc, warningsOk: true);
            var w = r.Status[cut.Id].Warnings;
            t.True(w.Any(x => x.StartsWith("SM-DIST-01") && x.Contains("from the bend Edge-Flange1")), string.Join("\n", w));
            t.True(w.Any(x => x.StartsWith("SM-DIST-02")), string.Join("\n", w));
        });

        t.Test("Hems: closed (in contact for the flat, the modelling gap in the solid), open and teardrop are valid solids", () =>
        {
            foreach (var type in new[] { HemType.Closed, HemType.Open, HemType.Teardrop })
            {
                var (doc, body, bf) = Part(Rect(100, 60));
                var r0 = Build(t, doc);
                var hem = doc.AddFeature(new HemFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "bottom") }, Type = type, Length = "12", Angle = "20" });
                var r = Build(t, doc);
                var solid = Solid(r);
                t.Eq(solid.Validate(), null);
                var g = r.State.Sheets[body.Id].Graph;
                var b = g.Bends.Single();
                t.True(b.IsHem, "a hem's bend");
                if (type != HemType.Teardrop) t.Near(b.Angle, Math.PI, 1e-12); else t.Near(b.Angle, Math.PI + 20 * Math.PI / 180, 1e-12);
                if (type == HemType.Closed) t.Near(b.InsideRadius, 0.127 / 2, 1e-12);
                // the return lies over the plate, the hem's outside at the plate's edge plus its fold
                var box = solid.TightBounds();
                t.Near(box.Min.Y, -(b.InsideRadius + th), 1e-6);
                t.True(box.Max.Z <= 2 * b.InsideRadius + th + 1e-6, $"{type}: the return stays low ({box.Max.Z})");
            }
        });

        t.Test("Hems: a teardrop too long runs into its panel (SM-HEM-01, with the longest that fits); a hem meeting a flange (SM-HEM-02); limits (SM-HEM-03)", () =>
        {
            var (doc, body, bf) = Part(Rect(100, 60));
            var r0 = Build(t, doc);
            var hem = doc.AddFeature(new HemFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "bottom") }, Type = HemType.Teardrop, Length = "40", Angle = "30" });
            var bad = new Regenerator().Rebuild(doc);
            t.True(bad.Status[hem.Id].Error?.StartsWith("SM-HEM-01") == true && bad.Status[hem.Id].Error!.Contains("at most"), "teardrop: " + (bad.Status[hem.Id].Error ?? "no error"));
            hem.Length = "10"; // (under the 10.9 the check gives)
            var ef = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "right") }, Length = "20" });
            var meet = new Regenerator().Rebuild(doc);
            t.True(meet.Status[ef.Id].Error?.Contains("SM-HEM-02") == true, "flange: " + (meet.Status[ef.Id].Error ?? "no error") + " / hem: " + meet.Status[hem.Id].Error);
            doc.Features.Remove(ef);
            hem.Type = HemType.Closed; hem.Length = "200";
            var r = Build(t, doc, warningsOk: true);
            t.True(r.Status[hem.Id].Warnings.Any(w => w.StartsWith("SM-HEM-03")), string.Join("\n", r.Status[hem.Id].Warnings));
        });

        t.Test("Checks: a flange shorter than its tooling's minimum (SM-FLG-01); bends along the grain of grain-sensitive aluminum (SM-GRAIN-01)", () =>
        {
            var (doc, body, bf) = Part(Rect(100, 60));
            var r0 = Build(t, doc);
            var ef = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { Side(r0, body.Id, bf, "bottom") }, Length = "6" });
            var r = Build(t, doc, warningsOk: true);
            t.True(r.Status[ef.Id].Warnings.Any(w => w.StartsWith("SM-FLG-01")), string.Join("\n", r.Status[ef.Id].Warnings));

            var (al, ab, abf) = Part(Rect(100, 60), "Aluminum 5052", "0.063");
            var a0 = Build(t, al);
            ab.GrainAngle = 0; // along x: a bend on the bottom edge runs with it
            var aef = al.AddFeature(new EdgeFlangeFeature { Body = ab.Id, Edges = { Side(a0, ab.Id, abf, "bottom") }, Length = "20" });
            var ar = Build(t, al, warningsOk: true);
            t.True(ar.Status[aef.Id].Warnings.Any(w => w.StartsWith("SM-GRAIN-01")), string.Join("\n", ar.Status[aef.Id].Warnings));
            ab.GrainAngle = 90;
            t.False(Warnings(Build(t, al, warningsOk: true)).Any(w => w.StartsWith("SM-GRAIN-01")), "across the grain: fine");
        });
    }

    static T Also<T>(this T v, Action<T> a) { a(v); return v; }
}

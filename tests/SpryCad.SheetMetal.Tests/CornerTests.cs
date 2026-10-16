using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel.Native;
using SpryCad.SheetMetal.Features;
using KVec2 = SpryCad.Kernel.Vec2;

namespace SpryCad.SheetMetal.Tests;

/// <summary>Corners, reliefs and the checks (sheet metal spec sections 7 and 9).</summary>
static class CornerTests
{
    const string Steel = "Steel CRS", Gauge = "14 ga";

    /// <summary>A 100 × 60 tray of 14 ga steel: the plate and 30 mm flanges up on all four edges (two features).</summary>
    static (CadDocument doc, SheetMetalBody body, EdgeFlangeFeature second) Tray(string length = "30", BendDirection secondDirection = BendDirection.Up)
    {
        var doc = new CadDocument();
        var body = SheetMetalModule.AddBody(doc, Steel, Gauge);
        var sk = new Sketch();
        var ps = new[] { (0.0, 0.0), (100.0, 0.0), (100.0, 60.0), (0.0, 60.0) }.Select(p => sk.AddPoint(new KVec2(p.Item1, p.Item2))).ToList();
        for (int i = 0; i < 4; i++) sk.Add(new SkLine { P1 = ps[i].Id, P2 = ps[(i + 1) % 4].Id });
        var s = doc.AddFeature(new SketchFeature { Sketch = sk });
        var bf = doc.AddFeature(new BaseFlangeFeature { Body = body.Id, SketchId = s.Id });
        SheetProfiles.Sync(doc);
        // the plate's edges as numbered: 0..3 from where the outline starts (all four used, so which is which does not matter)
        doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { $"{bf.Id}.P0.Edge(0)", $"{bf.Id}.P0.Edge(2)" }, Length = length });
        var second = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { $"{bf.Id}.P0.Edge(1)", $"{bf.Id}.P0.Edge(3)" }, Length = length, Direction = secondDirection });
        return (doc, body, second);
    }

    static double Volume(NBody b) => NMass.Of(b)!.Volume;

    public static void Run(TestRunner t)
    {
        double th = SheetRules.Resolve(StarterProfile.Profile, Steel, Gauge).Thickness;
        double gap = Math.Max(0.1, 0.2 * th);

        t.Test("Corners: a four-flange tray: open corners at the profile's gap, the outside flush, the flat one piece with relieved corners", () =>
        {
            var (doc, body, second) = Tray();
            var r = new Regenerator().Rebuild(doc);
            foreach (var (id, s) in r.Status) t.True(s.Ok && s.Warnings.Count == 0, $"{id}: {s.Error} {string.Join("; ", s.Warnings)}");
            var solid = r.State.Bodies.Single().Solid.Native!;
            t.Eq(solid.Validate(), null);
            var st = r.State.Sheets[body.Id];
            t.Eq(st.Graph.Corners.Count, 4);
            foreach (var c in st.Graph.Corners)
            {
                t.Eq(c.Treatment, CornerTreatment.Open);
                t.Near(c.Gap, gap, 1e-9);
            }
            t.True(st.Checks.Where(c => c.Rule == "SM-CORN-01").All(c => c.Passed), string.Join("\n", st.Checks.Select(c => c.Message)));
            t.False(st.Checks.Any(c => !c.Passed), string.Join("\n", st.Checks.Where(c => !c.Passed).Select(c => c.Message)));
            // material inside: the flanges' outsides on the plate's outline, 30 high to the outside mold line
            var box = solid.TightBounds();
            t.Near(box.Min.X, 0, 1e-9); t.Near(box.Max.X, 100, 1e-9);
            t.Near(box.Min.Y, 0, 1e-9); t.Near(box.Max.Y, 60, 1e-9);
            t.Near(box.Max.Z, 30 - th, 1e-9);
            // the volume: the panels (flanges lengthened toward the corners) and the bend shells
            double v = st.Graph.Panels.Sum(p => p.FlatOutline.Area) * th;
            foreach (var b in st.Graph.Bends)
            {
                var w = FlatPattern2D.Strip(st.Graph, b).Outer;
                v += b.Angle / 2 * ((b.InsideRadius + th) * (b.InsideRadius + th) - b.InsideRadius * b.InsideRadius) * (w[0] - w[1]).Length;
            }
            t.Near(Volume(solid), v, 1e-6);
            // the flat: one contour; at each corner a stepped notch where the bend zones end and the flanges run on past them
            // (each corner: a flange's far corner, its step, the bend zone's end, the plate's corner, the other zone's end, its step, its far corner)
            var flat = FlatPattern2D.From(st.Graph);
            t.Eq(flat.Loops.Count, 1);
            t.Eq(flat.Loops[0].Count, 4 * 7);
            t.Near(flat.Loops.Sum(l => Polygon2.LoopArea(l)), FlatPattern2D.Pieces(st.Graph).Sum(p => p.Outline.Area), 1e-6);
        });

        t.Test("Corners: overlap with no gap fails SM-CORN-01 naming the corner; an overlap at the rule's gap passes", () =>
        {
            var (doc, body, second) = Tray();
            var corner = new Regenerator().Rebuild(doc).State.Sheets[body.Id].Graph.Corners[0].Id.Value;
            doc.SheetOverrides = new() { new SheetMetalOverride { Target = corner, Values = { ["sm.corners/treatment"] = "OverlapA", ["sm.corners/gap"] = "0" } } };
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status.Values.All(s => s.Ok), "builds");
            t.Eq(r.State.Bodies.Single().Solid.Native!.Validate(), null);
            var warnings = r.Status.Values.SelectMany(s => s.Warnings).ToList();
            t.True(warnings.Any(w => w.StartsWith("SM-CORN-01") && w.Contains("Corner Edge-Flange") && w.Contains("gap rule requires")), string.Join("\n", warnings));
            var c = r.State.Sheets[body.Id].Graph.Corners.Single(x => x.Id.Value == corner);
            t.Eq(c.Treatment, CornerTreatment.OverlapA);
            t.Near(c.Gap, 0, 1e-9);
            doc.SheetOverrides[0].Values["sm.corners/gap"] = "1";
            var ok = new Regenerator().Rebuild(doc);
            t.False(ok.Status.Values.SelectMany(s => s.Warnings).Any(), string.Join("\n", ok.Status.Values.SelectMany(s => s.Warnings)));
            t.Eq(ok.State.Bodies.Single().Solid.Native!.Validate(), null);
        });

        t.Test("Corners: a treatment the profile does not allow is said; flanges up and down part without a corner", () =>
        {
            var (doc, body, _) = Tray();
            var corner = new Regenerator().Rebuild(doc).State.Sheets[body.Id].Graph.Corners[0].Id.Value;
            doc.SheetBodies![0].Overrides = new(StringComparer.Ordinal) { ["sm.corners/allowed"] = "Open" };
            doc.SheetOverrides = new() { new SheetMetalOverride { Target = corner, Values = { ["sm.corners/treatment"] = "Closed" } } };
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status.Values.SelectMany(s => s.Warnings).Any(w => w.Contains("does not allow a Closed corner")), string.Join("\n", r.Status.Values.SelectMany(s => s.Warnings)));

            var (mixed, mb, _) = Tray(secondDirection: BendDirection.Down);
            var rm = new Regenerator().Rebuild(mixed);
            t.True(rm.Status.Values.All(s => s.Ok), string.Join("; ", rm.Status.Values.Select(s => s.Error)));
            t.Eq(rm.State.Bodies.Single().Solid.Native!.Validate(), null);
            t.True(rm.State.Sheets[mb.Id].Graph.Corners.All(c => double.IsPositiveInfinity(c.Gap)), "no corner to make");
        });

        t.Test("Checks: the flat overlapping itself (SM-FLAT-01); a bend zone ending in material (SM-REL-01)", () =>
        {
            var a = Polygon2.Rectangle(new Vec2(0, 0), new Vec2(10, 10));
            t.True(SheetChecks.Overlap(a, Polygon2.Rectangle(new Vec2(5, 5), new Vec2(15, 15))), "crossing");
            t.True(SheetChecks.Overlap(a, Polygon2.Rectangle(new Vec2(2, 2), new Vec2(8, 8))), "inside");
            t.True(SheetChecks.Overlap(a, a), "the same");
            t.False(SheetChecks.Overlap(a, Polygon2.Rectangle(new Vec2(10, 0), new Vec2(20, 10))), "touching along an edge");
            t.False(SheetChecks.Overlap(a, Polygon2.Rectangle(new Vec2(10, 10), new Vec2(20, 20))), "touching at a point");
            // a plate with a flange on its bottom edge, and a stray panel lying over the end of the bend zone
            var rules = SheetRules.Resolve(StarterProfile.Profile, Steel, Gauge);
            var (ba, src) = rules.Allowance(Math.PI / 2);
            var plate = new Panel(new PanelId("A"), new Polygon2(new[] { new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, 60), new Vec2(0, 60) }), Frame3.Identity);
            var flange = new Panel(new PanelId("B"), new Polygon2(new[] { new Vec2(100, -ba), new Vec2(0, -ba), new Vec2(0, -ba - 20), new Vec2(100, -ba - 20) }), Frame3.Identity);
            var bend = new Bend(new BendId("AB"), plate.Id, flange.Id, new Line2(new Vec2(0, -ba / 2), new Vec2(-1, 0)), Math.PI / 2, BendDirection.Up,
                rules.InsideRadius, ba, rules.Toolset, src) { EdgeA = 0, EdgeB = 0 };
            var stray = new Panel(new PanelId("C"), Polygon2.Rectangle(new Vec2(95, -ba - 5), new Vec2(110, 5)), Frame3.Identity);
            var results = SheetChecks.Flat(new BendGraph(th, new[] { plate, flange, stray }, new[] { bend }, plate.Id));
            t.True(results.Any(x => x.Rule == "SM-REL-01" && x.Message.Contains("AB ends inside material (C)")), string.Join("\n", results.Select(x => x.Message)));
            t.True(results.Any(x => x.Rule == "SM-FLAT-01" && x.Entity.Contains('C')), string.Join("\n", results.Select(x => x.Message)));
            t.Eq(SheetChecks.Flat(new BendGraph(th, new[] { plate, flange }, new[] { bend }, plate.Id)).Count, 0);
        });
    }
}

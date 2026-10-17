using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel.Native;
using SpryCad.SheetMetal.Export;
using SpryCad.SheetMetal.Features;
using KVec2 = SpryCad.Kernel.Vec2;

namespace SpryCad.SheetMetal.Tests;

/// <summary>Sheet metal features through the real rebuild (sheet metal spec sections 6 and 10).</summary>
static class FeatureTests
{
    const string Steel = "Steel CRS", Gauge = "14 ga";

    static Sketch Polyline(bool closed, params (double x, double y)[] pts)
    {
        var sk = new Sketch();
        var ps = pts.Select(p => sk.AddPoint(new KVec2(p.x, p.y))).ToList();
        for (int i = 0; i < ps.Count - (closed ? 0 : 1); i++) sk.Add(new SkLine { P1 = ps[i].Id, P2 = ps[(i + 1) % ps.Count].Id });
        return sk;
    }

    /// <summary>A part with a 100 × 60 plate of 14 ga steel (the base flange on the front plane).</summary>
    static (CadDocument doc, SheetMetalBody body, BaseFlangeFeature bf) Plate()
    {
        var doc = new CadDocument();
        var body = SheetMetalModule.AddBody(doc, Steel, Gauge);
        var sk = doc.AddFeature(new SketchFeature { Sketch = Polyline(true, (0, 0), (100, 0), (100, 60), (0, 60)) });
        var bf = doc.AddFeature(new BaseFlangeFeature { Body = body.Id, SketchId = sk.Id });
        SheetProfiles.Sync(doc);
        return (doc, body, bf);
    }

    /// <summary>
    /// The face name of the 100 × 60 plate's edge on a side (bottom y = 0, top y = 60, left x = 0, right x = 100):
    /// edges are numbered from where the sketch's outline starts.
    /// </summary>
    static string E(BaseFlangeFeature bf, string side)
    {
        var o = Polyline(true, (0, 0), (100, 0), (100, 60), (0, 60));
        var doc = new CadDocument();
        var body = SheetMetalModule.AddBody(doc, Steel, Gauge);
        var sk = doc.AddFeature(new SketchFeature { Sketch = o });
        doc.AddFeature(new BaseFlangeFeature { Id = bf.Id, Body = body.Id, SketchId = sk.Id });
        var outer = new Regenerator().Rebuild(doc).State.Sheets[body.Id].Graph.Panels[0].FlatOutline.Outer;
        bool On(Vec2 p) => side switch { "bottom" => Math.Abs(p.Y) < 1e-9, "top" => Math.Abs(p.Y - 60) < 1e-9, "left" => Math.Abs(p.X) < 1e-9, _ => Math.Abs(p.X - 100) < 1e-9 };
        var i = Enumerable.Range(0, outer.Count).Single(k => On(outer[k]) && On(outer[(k + 1) % outer.Count]));
        return $"{bf.Id}.P0.Edge({i})";
    }

    static RegenResult Build(TestRunner t, CadDocument doc)
    {
        var r = new Regenerator().Rebuild(doc);
        foreach (var (id, s) in r.Status) t.True(s.Ok, $"{id}: {s.Error}");
        return r;
    }

    static NBody Solid(RegenResult r) => r.State.Bodies.Single().Solid.Native!;
    static double Volume(NBody b) => NMass.Of(b)!.Volume;

    /// <summary>The folded volume: panels' areas × t, and each bend's shell θ/2 ((R + t)² − R²) × its width.</summary>
    static double FoldedVolume(SheetState s)
    {
        double t = s.Graph.Thickness;
        var v = s.Graph.Panels.Sum(p => p.FlatOutline.Area) * t;
        foreach (var b in s.Graph.Bends)
        {
            var w = FlatPattern2D.Strip(s.Graph, b).Outer;
            v += b.Angle / 2 * ((b.InsideRadius + t) * (b.InsideRadius + t) - b.InsideRadius * b.InsideRadius) * (w[0] - w[1]).Length;
        }
        return v;
    }

    public static void Run(TestRunner t)
    {
        SheetMetalModule.Register();
        double th = SheetRules.Resolve(StarterProfile.Profile, Steel, Gauge).Thickness;

        t.Test("Features: a plate base flange is the sketch's outline, the gauge thick, below the sketch plane", () =>
        {
            var (doc, body, bf) = Plate();
            var r = Build(t, doc);
            var solid = Solid(r);
            t.Eq(solid.Validate(), null);
            t.Near(Volume(solid), 100 * 60 * th, 1e-6);
            var box = solid.TightBounds();
            t.Near(box.Max.Z, 0, 1e-9);
            t.Near(box.Min.Z, -th, 1e-9);
            t.True(solid.Faces.Any(f => f.Tag.Name == bf.Id + ".P0.Top"), "the top face is named by its panel");
            t.Eq(solid.Faces.Count(f => f.Tag.Name.StartsWith(bf.Id + ".P0.Edge(")), 4);
            t.Eq(r.State.Bodies.Single().Id, body.Id);
        });

        t.Test("Features: an edge flange, material inside: its outside face at the edge, its length to the outside mold line", () =>
        {
            var (doc, body, bf) = Plate();
            var ef = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "bottom") }, Length = "30" });
            var r = Build(t, doc);
            var solid = Solid(r);
            t.Eq(solid.Validate(), null);
            var st = r.State.Sheets[body.Id];
            t.Near(Volume(solid), FoldedVolume(st), 1e-6);
            var box = solid.TightBounds();
            // the edge was y = 0; the flange stands up (+z) from the top side, its outside face at y = 0
            t.Near(box.Min.Y, 0, 1e-9);
            t.Near(box.Max.Z, 30 - th, 1e-9);
            t.True(solid.Faces.Any(f => f.Tag.Name == ef.Id + ".0.B.Inside" && f.Surface.Kind == Kernel.SurfaceKind.Cylinder), "the bend's inside is a cylinder named by the bend");
            // bend outside: the plate keeps its size, the bend starts at the edge
            ef.Position = FlangePosition.BendOutside;
            var box2 = Solid(Build(t, doc)).TightBounds();
            t.Near(box2.Min.Y, -(st.Rules.InsideRadius + th), 1e-9);
        });

        t.Test("Features: flanges on opposite edges; a flange on a flange's far edge; on the side of a flange: refused", () =>
        {
            var (doc, body, bf) = Plate();
            var ef = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "bottom"), E(bf, "top") }, Length = "25", Direction = BendDirection.Down });
            var r = Build(t, doc);
            t.Eq(Solid(r).Validate(), null);
            t.Near(Volume(Solid(r)), FoldedVolume(r.State.Sheets[body.Id]), 1e-6);
            t.Near(Solid(r).TightBounds().Min.Z, -(25 - 0) + 0, 1e-9); // down: the outside is the top side; 25 to the OML at z = 0
            var ef2 = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { ef.Id + ".0.P.Edge(2)" }, Length = "15", Angle = "45" });
            var r2 = Build(t, doc);
            t.Eq(Solid(r2).Validate(), null);
            t.Near(Volume(Solid(r2)), FoldedVolume(r2.State.Sheets[body.Id]), 1e-6);
            // beside the bend a flange hangs from: refused, said
            var side = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { ef.Id + ".0.P.Edge(1)" }, Length = "10" });
            var bad = new Regenerator().Rebuild(doc);
            t.True(bad.Status[side.Id].Error?.Contains("beside the bend") == true, bad.Status[side.Id].Error ?? "no error");
        });

        t.Test("Features: a bent profile from an open sketch: the lines are the top side, each corner a bend", () =>
        {
            var doc = new CadDocument();
            var body = SheetMetalModule.AddBody(doc, Steel, Gauge);
            var sk = doc.AddFeature(new SketchFeature { Sketch = Polyline(false, (0, 0), (50, 0), (50, 30), (90, 30)) });
            doc.AddFeature(new BaseFlangeFeature { Body = body.Id, SketchId = sk.Id, Width = "40" });
            SheetProfiles.Sync(doc);
            var r = Build(t, doc);
            var solid = Solid(r);
            t.Eq(solid.Validate(), null);
            var st = r.State.Sheets[body.Id];
            t.Eq(st.Graph.Bends.Count, 2);
            t.Eq(st.Graph.Bends[0].Direction, BendDirection.Down); // a left turn
            t.Eq(st.Graph.Bends[1].Direction, BendDirection.Up);   // a right turn
            t.Near(Volume(solid), FoldedVolume(st), 1e-6);
            var box = solid.TightBounds();
            // the top side on the lines; the material on their left
            t.Near(box.Min.X, 0, 1e-9);
            t.Near(box.Min.Y, 0, 1e-9);
            t.Near(box.Max.X, 90, 1e-9);
            t.Near(box.Max.Y, 30 + th, 1e-9);
            t.Near(box.Max.Z - box.Min.Z, 40, 1e-9);
        });

        t.Test("Features: saved and read back (bodies, overrides, the profile copy) the part rebuilds the same", () =>
        {
            var (doc, body, bf) = Plate();
            var ef = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "left") }, Length = "20" });
            doc.SheetOverrides = new() { new SheetMetalOverride { Target = ef.Id + ".0.B", Values = { ["sm.tooling/inside radius"] = "3" } } };
            var r = Build(t, doc);
            t.Near(r.State.Sheets[body.Id].Graph.Bends[0].InsideRadius, 3, 0);
            var back = PartObjects.ToDocument(PartObjects.From(doc));
            t.Eq(back.SheetBodies!.Single().Gauge, Gauge);
            t.Eq(back.SheetOverrides!.Single().Values["sm.tooling/inside radius"], "3");
            t.Eq(back.SheetProfile!.ProfileName, StarterProfile.Name);
            t.True(back.Features.OfType<EdgeFlangeFeature>().Single().Edges.Single() == E(bf, "left"), "the edge flange reads back");
            var r2 = Build(t, back);
            t.Near(Volume(Solid(r2)), Volume(Solid(r)), 1e-9);
            // the bytes are the same written again
            var a = PartObjects.From(doc); var b = PartObjects.From(back);
            t.Eq(a.Count, b.Count);
            foreach (var (k, v) in a) t.True(b.TryGetValue(k, out var w) && v.AsSpan().SequenceEqual(w), $"object {k} differs");
        });

        t.Test("Features: ids and the flat's layout stay put when an unrelated flange changes", () =>
        {
            var (doc, body, bf) = Plate();
            var e1 = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "bottom") }, Length = "20" });
            var e2 = doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "top") }, Length = "20" });
            var g1 = Build(t, doc).State.Sheets[body.Id].Graph;
            e2.Length = "35";
            var g2 = Build(t, doc).State.Sheets[body.Id].Graph;
            t.Eq(string.Join(",", g1.Panels.Select(p => p.Id)), string.Join(",", g2.Panels.Select(p => p.Id)));
            t.Eq(string.Join(",", g1.Bends.Select(b => b.Id)), string.Join(",", g2.Bends.Select(b => b.Id)));
            foreach (var id in new[] { bf.Id + ".P0", e1.Id + ".0.P" })
                t.Eq(string.Join(" ", g1.Panel(new PanelId(id)).FlatOutline.Outer), string.Join(" ", g2.Panel(new PanelId(id)).FlatOutline.Outer));
        });

        t.Test("Features: the flat pattern: one closed contour, bends on their layers, the same bytes every time; shown flat", () =>
        {
            var (doc, body, bf) = Plate();
            doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "bottom") }, Length = "20" });
            doc.AddFeature(new EdgeFlangeFeature { Body = body.Id, Edges = { E(bf, "top") }, Length = "20", Direction = BendDirection.Down });
            var r = Build(t, doc);
            var st = r.State.Sheets[body.Id];
            var flat = SheetMetalModule.FlatOf(r.State, body.Id)!;
            t.Eq(flat.Loops.Count, 1);
            t.Eq(flat.Loops[0].Count, 4); // a rectangle: the edges along the bends run straight on
            t.Eq(flat.Bends.Count, 2);
            var flatLength = 60 + st.Graph.Bends.Sum(b => b.Allowance) + st.Graph.Panels.Where(p => p.Id.Value != bf.Id + ".P0").Sum(p => p.FlatOutline.Area / 100);
            var (min, max) = flat.Bounds;
            t.Near(max.Y - min.Y, flatLength - 2 * BendMath.Ossb(Math.PI / 2, st.Rules.InsideRadius, th), 1e-9);
            t.Near(flat.CutLength, 2 * (100 + (max.Y - min.Y)), 1e-9);
            var style = OutputStyle.Of(st.Rules);
            string Dxf() { using var ms = new MemoryStream(); FlatExport.WriteDxf(ms, flat, style); return System.Text.Encoding.UTF8.GetString(ms.ToArray()); }
            var dxf = Dxf();
            t.Eq(dxf, Dxf());
            t.True(dxf.Contains("\r\nBEND_UP\r\n") && dxf.Contains("\r\nBEND_DOWN\r\n") && dxf.Contains("\r\nOUTLINE\r\n"), "the profile's layers");
            t.True(dxf.Contains("UP 90°"), "a bend note");
            using (var ms = new MemoryStream()) { FlatExport.WriteSvg(ms, flat, style); t.True(ms.Length > 200, "an SVG"); }
            // shown flat: the flat pattern as the body, the base panel where it was
            body.Flattened = true;
            var fr = Build(t, doc);
            var fs = Solid(fr);
            t.Eq(fs.Validate(), null);
            var fb = fs.TightBounds();
            t.Near(fb.Max.Z - fb.Min.Z, th, 1e-9);
            t.Near(Volume(fs), flat.Loops.Sum(l => Polygon2.LoopArea(l)) * th, 1e-6);
        });

        t.Test("Features: the part's profile copy follows its sheets; a changed profile is found on opening", () =>
        {
            var (doc, body, _) = Plate();
            t.True(doc.SheetProfile!.Tables.Single(x => x.Key == ProfileTables.Gauges).Rows.All(r => r.Keys[0] == Steel && r.Keys[1] == Gauge), "only the part's material and gauge");
            t.Eq(SheetProfiles.Update(doc), null);
            // the shop changes the inside radius for steel
            var tables = StarterProfile.Tables.Select(x => x.Clone()).ToList();
            var tooling = tables.Single(x => x.Key == ProfileTables.Tooling);
            tooling.Rows.Add(new ProfileRow(new[] { Steel, "*", "Standard" }, "1", "t", "88", "8*t", "88", "1", "2*t", "4*t", ""));
            var lib = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null, Array.Empty<LibraryTable>(), tables);
            var set = new LibrarySet(new[] { lib });
            var u = SheetProfiles.Update(doc, set);
            t.True(u != null, "an update");
            t.True(u!.Value.Changes.Any(c => c.Contains("Shop profile")), string.Join("\n", u.Value.Changes));
            t.True(u.Value.Changes.Any(c => c.Contains("Steel CRS|*|Standard: added")), string.Join("\n", u.Value.Changes));
            doc.SheetProfile = u.Value.Fresh;
            t.Near(SheetProfiles.RulesOf(doc, body).InsideRadius, 2 * th, 1e-12);
            // another gauge: the copy is made again for it
            body.Gauge = "16 ga";
            t.True(SheetProfiles.Sync(doc, set), "synced");
            t.True(doc.SheetProfile!.Tables.Single(x => x.Key == ProfileTables.Gauges).Rows.Single().Keys[1] == "16 ga", "the new gauge");
            doc.SheetBodies!.Clear();
            t.True(SheetProfiles.Sync(doc) && doc.SheetProfile == null, "no sheets, no copy");
        });
    }
}

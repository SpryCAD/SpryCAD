using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Holes that break out through the side of the part (a corner fillet), at several sizes.</summary>
static class HoleBreakoutTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Hole breaking out through a corner fillet: exact and closed at every size", () =>
        {
            // a part the size of an engine casting (300 mm): the boolean's tolerances grow with the part, and a hole tool
            // that started 0.001 above its face then lay within them
            const double k = 3;
            string S(double v) => (v * k).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var drafted in new[] { false, true })
                foreach (var d in new[] { 8, 12, 13, 13.002, 14, 16 })
                {
                    var doc = new CadDocument();
                    var sk = new Sketch();
                    var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(100 * k, 0));
                    var c = sk.AddPoint(new Vec2(100 * k, 60 * k)); var e = sk.AddPoint(new Vec2(0, 60 * k));
                    sk.Add(new SkLine { P1 = a.Id, P2 = b.Id }); sk.Add(new SkLine { P1 = b.Id, P2 = c.Id });
                    sk.Add(new SkLine { P1 = c.Id, P2 = e.Id }); sk.Add(new SkLine { P1 = e.Id, P2 = a.Id });
                    var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                    // drafted: the sides lean in, so the corner fillet is a cone and the hole meets it in a traced curve
                    var block = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = S(20), Draft = drafted ? "3" : "0" });
                    var regen = new Regenerator();
                    var r = regen.Rebuild(doc);
                    var edge = r.State.Bodies[0].Solid.Topology.Edges.First(x => Math.Abs(x.Start.X - x.End.X) < 3 * k && x.Start.X > 90 * k && x.Start.Y > 50 * k
                        && Math.Abs(x.Start.Z - x.End.Z) > 10 * k);
                    doc.AddFeature(new FilletFeature { Radius = S(10), Edges = { new EdgeRef { A = edge.FaceA, B = edge.FaceB, Hint = edge.Midpoint } } });
                    // from the top face, near the filleted corner: past ~11.5 mm (Ã— k) it breaks out through the fillet
                    var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(93 * k, 53 * k)); hs.Add(new SkPointEntity { P = hp.Id });
                    var top = new FaceRef { Name = block.Id + ".End", Hint = new Vec3(50 * k, 30 * k, 20 * k), Normal = Vec3.UnitZ };
                    var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
                    var hole = doc.AddFeature(new HoleFeature { SketchId = s2.Id, Type = HoleType.Simple, Diameter = S(d), ThroughAll = true });
                    int before = Csg.NativeFallback;
                    r = regen.Rebuild(doc);
                    var tag = $"{(drafted ? "drafted" : "straight")} sides, hole {d}";
                    foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok && r.Status[f.Id].Warnings.Count == 0, $"{tag}: {f.Name}: {r.Status[f.Id].Error} {string.Join("; ", r.Status[f.Id].Warnings)}");
                    t.True(Csg.NativeFallback == before, $"{tag}: an exact boolean fell back ({Csg.LastFallbackReason})");
                    var nb = r.State.Bodies[0].Solid.Native;
                    t.True(nb != null && nb.Validate() == null, $"{tag}: exact and closed: {nb?.Validate()}");
                    // the hole starts on the face it is sketched on: nothing of it is left above (a tool started 0.001
                    // above the face lay within the boolean's tolerance of it on large parts: a hole breaking out
                    // through the engine casting's corner fillet then left its wall uncut and the boolean failed)
                    if (nb != null)
                    {
                        var topZ = nb.Edges.Max(x => Math.Max(x.V0.P.Z, x.V1.P.Z));
                        t.True(topZ <= 20 * k + 1e-9, $"{tag}: highest vertex at z = {topZ:R}, above the top face (z = {20 * k})");
                    }
                }
        });
    }
}

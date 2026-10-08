using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

/// <summary>
/// Rebuilds a small throw-away part on a background thread at start-up so the JIT has compiled the
/// modeling code paths (extrude, cut, fillet, hole, pattern, shell, tessellation, exact B-rep) before the
/// user's first rebuild. Failures are ignored: this only affects timing.
/// </summary>
public static class Warmup
{
    public static Task Start() => Task.Run(() =>
    {
        try { Run(); } catch { /* timing only */ }
    });

    public static RegenResult Run()
    {
        var doc = new CadDocument();
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60) });
        var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
        var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ };

        var sk2 = new Sketch();
        var cp = sk2.AddPoint(new Vec2(30, 30));
        sk2.Add(new SkCircle { Center = cp.Id, R = 8 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk2 });
        doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true });

        doc.AddFeature(new FilletFeature { Radius = "5", Edges = { new EdgeRef { A = e1.Id + ".S.L5", B = e1.Id + ".S.L6", Hint = new Vec3(100, 60, 10) } } });

        var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(70, 30)); hs.Add(new SkPointEntity { P = hp.Id });
        var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
        var hole = doc.AddFeature(new HoleFeature { SketchId = s3.Id, Type = HoleType.Counterbore, Diameter = "6.6", ThroughAll = true });
        doc.AddFeature(new LinearPatternFeature { Seeds = { hole.Id }, Direction = new DirRef { Axis = "Y" }, Spacing = "-20", Count = "2" });
        doc.AddFeature(new ShellFeature { Thickness = "2", Faces = { new FaceRef { Name = e1.Id + ".Start", Hint = new Vec3(50, 30, 0), Normal = -Vec3.UnitZ } } });
        doc.AddFeature(new ChamferFeature { Distance = "1", Edges = { new EdgeRef { A = e1.Id + ".End", B = e1.Id + ".S.L6", Hint = new Vec3(50, 60, 20) } } });

        var r = new Regenerator().Rebuild(doc);
        foreach (var b in r.State.Bodies)
        {
            _ = b.Solid.Faces.Count;      // display tessellation
            _ = b.Solid.Topology.Edges;   // picking topology
            ExactBrep.Build(b.Solid);     // STEP export path
        }
        return r;
    }

    static Sketch Rect(double w, double h)
    {
        var sk = new Sketch();
        var p1 = sk.Point(Sketch.OriginId)!;
        var p2 = sk.AddPoint(new Vec2(w, 0)); var p3 = sk.AddPoint(new Vec2(w, h)); var p4 = sk.AddPoint(new Vec2(0, h));
        var l1 = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
        var l2 = sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
        var l3 = sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id });
        var l4 = sk.Add(new SkLine { P1 = p4.Id, P2 = p1.Id });
        sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
        sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
        var d1 = sk.AddConstraint(CType.Distance, l1.Id); d1.Expr = "100"; d1.Value = w;
        var d2 = sk.AddConstraint(CType.Distance, l2.Id); d2.Expr = "60"; d2.Value = h;
        return sk;
    }
}

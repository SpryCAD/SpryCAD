using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Small parts whose blends exercise the direct blend (and the boolean it falls back to).</summary>
static partial class Demos
{
    public static IEnumerable<(string name, Func<CadDocument> build)> BlendCases => new (string, Func<CadDocument>)[]
    {
        ("casting foot, drafted", () => CastingFoot("2")),
        ("casting foot, undrafted", () => CastingFoot("0")),
        ("casting foot, plate drafted and rounded", () => CastingFoot("2", "5")),
        ("casting foot, drafted, chamfered", () => CastingFoot("2", chamfer: _ => { })),
        ("casting foot, drafted, chamfered 6 x 3", () => CastingFoot("2", chamfer: c => { c.Type = ChamferType.DistanceDistance; c.Distance2 = "3"; })),
        ("casting foot, drafted, chamfered 6 x 3 flipped", () => CastingFoot("2", chamfer: c => { c.Type = ChamferType.DistanceDistance; c.Distance2 = "3"; c.Flip = true; })),
        ("casting foot, drafted, chamfered 6 at 30°", () => CastingFoot("2", chamfer: c => { c.Type = ChamferType.AngleDistance; c.Angle = "30"; })),
        ("boss top, drafted, chamfered", () => CastingFoot("2", chamfer: _ => { }, atTop: true)),
        ("casting foot, drafted, chamfered, hollow under", () => CastingFoot("2", chamfer: _ => { }, hollow: true)),
        ("plate, drafted hole across a slot, rim chamfered", SlotHole),
        ("box, three edges at a corner", BoxCorner),
        ("plate, hole rims", PlateHoles),
        ("box, all edges", BoxAll),
        ("box, top edges", () => BoxAll(topOnly: true)),
        ("pocket, floor and corners", Pocket),
        ("drafted block, top edge under an overhang", Overhang),
        ("wall beside a taller one", NarrowGap),
        ("rib against a wall, filleted and mirrored", MirroredRib),
    };

    /// <summary>
    /// A base plate with a boss on it (drafted, its vertical edges rounded R20), the boss's foot blended R6 into the
    /// plate's top: one edge picked, the tangent edges following (a closed loop of lines and arcs). A casting's
    /// commonest blend.
    /// </summary>
    static CadDocument CastingFoot(string draft, string? baseDraft = null, Action<ChamferFeature>? chamfer = null, bool atTop = false, bool hollow = false)
    {
        var d = Doc(("BaseX", "100"), ("BaseY", "75"), ("BaseT", "20"), ("BossX", "60"), ("BossY", "40"), ("BossH", "60"), ("Draft", draft),
            ("Rc", "20"), ("Rf", "6"), ("BaseDraft", baseDraft ?? "0"), ("Rb", "15"));
        var b = new Sk(d);
        var bl = b.Rect("-BaseX", "-BaseY", "BaseX", "BaseY");
        var bs = d.AddFeature(new SketchFeature { Name = "Base", Plane = PlaneRef.Front, Sketch = b.S });
        var basePlate = d.AddFeature(new ExtrudeFeature { Name = "Base", SketchId = bs.Id, Depth = "BaseT", Draft = baseDraft == null ? null : "BaseDraft" });
        if (baseDraft != null)
        {
            // the plate's corners rounded too (as the engine's casting)
            string BF(int i) => $"{basePlate.Id}.S.L{bl[i]}";
            double W(string e) => b.Eval(e);
            double zb = W("BaseT/2");
            d.AddFeature(new FilletFeature
            {
                Name = "Base corners", Radius = "Rb",
                Edges =
                {
                    E(BF(0), BF(1), new Vec3(W("BaseX"), -W("BaseY"), zb)), E(BF(1), BF(2), new Vec3(W("BaseX"), W("BaseY"), zb)),
                    E(BF(2), BF(3), new Vec3(-W("BaseX"), W("BaseY"), zb)), E(BF(3), BF(0), new Vec3(-W("BaseX"), -W("BaseY"), zb)),
                },
            });
        }
        var top = d.AddFeature(new PlaneFeature { Name = "Base top", Base = PlaneRef.Front, Offset = "BaseT" });
        var y = new Sk(d);
        var yl = y.Rect("-BossX", "-BossY", "BossX", "BossY");
        var ys = d.AddFeature(new SketchFeature { Name = "Boss", Plane = new PlaneRef { Feature = top.Id }, Sketch = y.S });
        var boss = d.AddFeature(new ExtrudeFeature { Name = "Boss", SketchId = ys.Id, Depth = "BossH", Draft = draft == "0" ? null : "Draft" });
        double V(string e) => y.Eval(e);
        string F(int i) => $"{boss.Id}.S.L{yl[i]}";
        double zm = V("BaseT+BossH/2");
        d.AddFeature(new FilletFeature
        {
            Name = "Boss corners", Radius = "Rc",
            Edges =
            {
                E(F(0), F(1), new Vec3(V("BossX"), -V("BossY"), zm)), E(F(1), F(2), new Vec3(V("BossX"), V("BossY"), zm)),
                E(F(2), F(3), new Vec3(-V("BossX"), V("BossY"), zm)), E(F(3), F(0), new Vec3(-V("BossX"), -V("BossY"), zm)),
            },
        });
        if (hollow)
        {
            // a pocket from below under the boss's front side, leaving the plate 1 thick there: the space a concave
            // blend's tool reaches into behind the corner is open
            var u = new Sk(d);
            u.Rect("-70", "-55", "70", "-25");
            var us = d.AddFeature(new SketchFeature { Name = "Hollow", Plane = PlaneRef.Front, Sketch = u.S });
            d.AddFeature(new ExtrudeFeature { Name = "Hollow", SketchId = us.Id, IsCut = true, Depth = "BaseT - 1" });
        }
        // (top: the boss's top edge instead, convex, its side drafted in by BossH tan(Draft))
        var foot = atTop ? E($"{boss.Id}.End", F(0), new Vec3(0, -V("BossY - BossH * tan(Draft)"), V("BaseT + BossH")))
            : E($"{basePlate.Id}.End", F(0), new Vec3(0, -V("BossY"), V("BaseT")));
        // chamfered: drafted, the rounded corners meet the plate (and the top) along ellipses (their axes lean)
        if (chamfer != null)
        {
            var ch = new ChamferFeature { Name = "Foot", Distance = "Rf", Edges = { foot } };
            chamfer(ch);
            d.AddFeature(ch);
        }
        else d.AddFeature(new FilletFeature { Name = "Foot", Radius = "Rf", Edges = { foot } });
        return d;
    }

    /// <summary>A box with the three edges at one corner filleted (they meet at the corner: a vertex blend).</summary>
    static CadDocument BoxCorner()
    {
        var d = Doc(("X", "40"), ("Y", "30"), ("Z", "20"), ("R", "4"));
        var s = new Sk(d);
        var l = s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Box", Plane = PlaneRef.Front, Sketch = s.S });
        var box = d.AddFeature(new ExtrudeFeature { Name = "Box", SketchId = ss.Id, Depth = "Z" });
        double V(string e) => s.Eval(e);
        string F(int i) => $"{box.Id}.S.L{l[i]}";
        d.AddFeature(new FilletFeature
        {
            Name = "Corner", Radius = "R", TangentPropagation = false,
            Edges =
            {
                E(F(0), F(1), new Vec3(V("X"), -V("Y"), V("Z/2"))),
                E($"{box.Id}.End", F(0), new Vec3(0, -V("Y"), V("Z"))),
                E($"{box.Id}.End", F(1), new Vec3(V("X"), 0, V("Z"))),
            },
        });
        return d;
    }

    /// <summary>A box with all twelve edges filleted (eight corners where three fillets meet).</summary>
    static CadDocument BoxAll() => BoxAll(false);

    /// <summary>The box with all twelve edges filleted, or the four round its top only (two fillets at each corner).</summary>
    static CadDocument BoxAll(bool topOnly)
    {
        var d = Doc(("X", "40"), ("Y", "30"), ("Z", "20"), ("R", "4"));
        var s = new Sk(d);
        var l = s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Box", Plane = PlaneRef.Front, Sketch = s.S });
        var box = d.AddFeature(new ExtrudeFeature { Name = "Box", SketchId = ss.Id, Depth = "Z" });
        double V(string e) => s.Eval(e);
        string F(int i) => $"{box.Id}.S.L{l[i]}";
        var corners = new[] { (V("X"), -V("Y")), (V("X"), V("Y")), (-V("X"), V("Y")), (-V("X"), -V("Y")) };
        var mids = new[] { (0.0, -V("Y")), (V("X"), 0.0), (0.0, V("Y")), (-V("X"), 0.0) };
        var fillet = new FilletFeature { Name = "All edges", Radius = "R", TangentPropagation = false };
        for (int i = 0; i < 4; i++)
        {
            fillet.Edges.Add(E($"{box.Id}.End", F(i), new Vec3(mids[i].Item1, mids[i].Item2, V("Z"))));
            if (topOnly) continue;
            fillet.Edges.Add(E(F(i), F((i + 1) % 4), new Vec3(corners[i].Item1, corners[i].Item2, V("Z/2"))));
            fillet.Edges.Add(E($"{box.Id}.Start", F(i), new Vec3(mids[i].Item1, mids[i].Item2, 0)));
        }
        d.AddFeature(fillet);
        return d;
    }

    /// <summary>A block with a rectangular pocket, the pocket's floor edges and vertical edges filleted (concave corners).</summary>
    static CadDocument Pocket()
    {
        var d = Doc(("X", "40"), ("Y", "30"), ("Z", "20"), ("PX", "25"), ("PY", "15"), ("PD", "12"), ("R", "3"));
        var s = new Sk(d);
        s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Block", Plane = PlaneRef.Front, Sketch = s.S });
        var block = d.AddFeature(new ExtrudeFeature { Name = "Block", SketchId = ss.Id, Depth = "Z" });
        var top = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Front, Offset = "Z" });
        var p = new Sk(d);
        var pl = p.Rect("-PX", "-PY", "PX", "PY");
        var ps = d.AddFeature(new SketchFeature { Name = "Pocket", Plane = new PlaneRef { Feature = top.Id }, Sketch = p.S });
        var pocket = d.AddFeature(new ExtrudeFeature { Name = "Pocket", SketchId = ps.Id, IsCut = true, Reverse = true, Depth = "PD" });
        double V(string e) => s.Eval(e);
        string F(int i) => $"{pocket.Id}.S.L{pl[i]}";
        double floor = V("Z-PD");
        var corners = new[] { (V("PX"), -V("PY")), (V("PX"), V("PY")), (-V("PX"), V("PY")), (-V("PX"), -V("PY")) };
        var mids = new[] { (0.0, -V("PY")), (V("PX"), 0.0), (0.0, V("PY")), (-V("PX"), 0.0) };
        var fillet = new FilletFeature { Name = "Pocket edges", Radius = "R", TangentPropagation = false };
        for (int i = 0; i < 4; i++)
        {
            fillet.Edges.Add(E(F(i), F((i + 1) % 4), new Vec3(corners[i].Item1, corners[i].Item2, floor + V("PD/2"))));
            fillet.Edges.Add(E($"{pocket.Id}.End", F(i), new Vec3(mids[i].Item1, mids[i].Item2, floor)));
        }
        d.AddFeature(fillet);
        return d;
    }

    /// <summary>
    /// A drafted block with a box on its top as wide as the block's foot (so it overhangs the narrower top, level with
    /// it, like a casting's hopper), the block's side top edge filleted with the tangent edges following: convex
    /// along the block's own top, concave on under the overhang, running into the box's wall.
    /// </summary>
    static CadDocument Overhang()
    {
        var d = Doc(("X", "60"), ("Y", "40"), ("H", "60"), ("Draft", "2"), ("B0", "50"), ("B1", "10"), ("BH", "30"), ("R", "3"));
        var s = new Sk(d);
        var l = s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Block", Plane = PlaneRef.Front, Sketch = s.S });
        var block = d.AddFeature(new ExtrudeFeature { Name = "Block", SketchId = ss.Id, Depth = "H", Draft = "Draft" });
        var top = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Front, Offset = "H" });
        var b = new Sk(d);
        b.Rect("-B0", "-Y", "-B1", "Y");
        var bs = d.AddFeature(new SketchFeature { Name = "Box", Plane = new PlaneRef { Feature = top.Id }, Sketch = b.S });
        d.AddFeature(new ExtrudeFeature { Name = "Box", SketchId = bs.Id, Depth = "BH" });
        double V(string e) => s.Eval(e);
        var y = -V("Y") + V("H") * Math.Tan(V("Draft") * Math.PI / 180);
        d.AddFeature(new FilletFeature
        {
            Name = "Top edge", Radius = "R",
            Edges = { E($"{block.Id}.S.L{l[0]}", $"{block.Id}.End", new Vec3(V("X/2"), y, V("H"))) },
        });
        return d;
    }

    /// <summary>
    /// Two walls on a base with a 2 mm gap between them, the left one 30 high, the right one 40: the left wall's top
    /// edge at the gap filleted R8 (the space beyond that edge's corner, across the gap, is the taller wall).
    /// </summary>
    static CadDocument NarrowGap()
    {
        var d = Doc(("X", "30"), ("Y", "20"), ("G", "1"), ("H1", "30"), ("H2", "40"), ("R", "8"));
        var b = new Sk(d);
        b.Rect("-X", "-Y", "X", "Y");
        var bs = d.AddFeature(new SketchFeature { Name = "Base", Plane = PlaneRef.Front, Sketch = b.S });
        d.AddFeature(new ExtrudeFeature { Name = "Base", SketchId = bs.Id, Depth = "10" });
        var top = d.AddFeature(new PlaneFeature { Name = "Base top", Base = PlaneRef.Front, Offset = "10" });
        var w1 = new Sk(d);
        var l1 = w1.Rect("-X", "-Y", "-G", "Y");
        var w1s = d.AddFeature(new SketchFeature { Name = "Low wall", Plane = new PlaneRef { Feature = top.Id }, Sketch = w1.S });
        var low = d.AddFeature(new ExtrudeFeature { Name = "Low wall", SketchId = w1s.Id, Depth = "H1-10" });
        var w2 = new Sk(d);
        w2.Rect("G", "-Y", "X", "Y");
        var w2s = d.AddFeature(new SketchFeature { Name = "High wall", Plane = new PlaneRef { Feature = top.Id }, Sketch = w2.S });
        d.AddFeature(new ExtrudeFeature { Name = "High wall", SketchId = w2s.Id, Depth = "H2-10" });
        double V(string e) => b.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Gap edge", Radius = "R", TangentPropagation = false,
            Edges = { E($"{low.Id}.End", $"{low.Id}.S.L{l1[1]}", new Vec3(-V("G"), 0, V("H1"))) },
        });
        return d;
    }

    /// <summary>
    /// A plate with a wall at either end, a rib against the left wall standing on the plate 2 mm from its edge, its
    /// sides filleted R3 into the plate and the wall (the outer one runs past the plate's edge: cut back to it), then
    /// the rib and its fillets mirrored.
    /// </summary>
    static CadDocument MirroredRib()
    {
        var d = Doc(("X", "50"), ("Y", "30"), ("W", "10"), ("H", "50"), ("RL", "30"), ("RT", "3"), ("RH", "40"), ("R", "3"));
        var b = new Sk(d);
        b.Rect("-X", "-Y", "X", "Y");
        var bs = d.AddFeature(new SketchFeature { Name = "Plate", Plane = PlaneRef.Front, Sketch = b.S });
        var plate = d.AddFeature(new ExtrudeFeature { Name = "Plate", SketchId = bs.Id, Depth = "10" });
        var top = d.AddFeature(new PlaneFeature { Name = "Plate top", Base = PlaneRef.Front, Offset = "10" });
        var w = new Sk(d);
        var wl = w.Rect("-X", "-Y", "-(X-W)", "Y");
        w.Rect("X-W", "-Y", "X", "Y");
        var ws = d.AddFeature(new SketchFeature { Name = "Walls", Plane = new PlaneRef { Feature = top.Id }, Sketch = w.S });
        var walls = d.AddFeature(new ExtrudeFeature { Name = "Walls", SketchId = ws.Id, Depth = "H-10" });
        var r = new Sk(d);
        var rl = r.Rect("-(X-W)", "Y-2-2*RT", "-(X-W-RL)", "Y-2");
        var rs = d.AddFeature(new SketchFeature { Name = "Rib", Plane = new PlaneRef { Feature = top.Id }, Sketch = r.S });
        var rib = d.AddFeature(new ExtrudeFeature { Name = "Rib", SketchId = rs.Id, Depth = "RH-10" });
        double V(string e) => b.Eval(e);
        double xm = -V("X-W-RL/2"), xw = -V("X-W"), zm = V("(RH+10)/2"), y0 = V("Y-2-2*RT"), y1 = V("Y-2");
        var fil = d.AddFeature(new FilletFeature
        {
            Name = "Rib fillets", Radius = "R", TangentPropagation = false,
            Edges =
            {
                E($"{rib.Id}.S.L{rl[0]}", $"{plate.Id}.End", new Vec3(xm, y0, 10)), E($"{rib.Id}.S.L{rl[2]}", $"{plate.Id}.End", new Vec3(xm, y1, 10)),
                E($"{rib.Id}.S.L{rl[0]}", $"{walls.Id}.S.L{wl[1]}", new Vec3(xw, y0, zm)), E($"{rib.Id}.S.L{rl[2]}", $"{walls.Id}.S.L{wl[1]}", new Vec3(xw, y1, zm)),
            },
        });
        d.AddFeature(new MirrorFeature { Name = "Rib, other end", Seeds = { rib.Id, fil.Id }, Plane = PlaneRef.Right });
        return d;
    }

    /// <summary>A plate with through holes, their top rims filleted and bottom rims chamfered.</summary>
    /// <summary>
    /// A plate with a drafted hole (a cone: no exact chamfer tool), a slot off its centre line cutting across the
    /// hole's rim: the rim's top edge, chamfered, is an open arc whose ends meet the slot's walls at an angle.
    /// </summary>
    static CadDocument SlotHole()
    {
        var d = Doc(("X", "50"), ("Y", "30"), ("T", "12"), ("D", "20"), ("Draft", "5"), ("C", "1.5"));
        var s = new Sk(d);
        s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Plate", Plane = PlaneRef.Front, Sketch = s.S });
        var plate = d.AddFeature(new ExtrudeFeature { Name = "Plate", SketchId = ss.Id, Depth = "T" });
        var top = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Front, Offset = "T" });
        var h = new Sk(d);
        var c = h.Circle(h.P("0", "0"), "D");
        var hs = d.AddFeature(new SketchFeature { Name = "Hole", Plane = new PlaneRef { Feature = top.Id }, Sketch = h.S });
        var hole = d.AddFeature(new ExtrudeFeature { Name = "Hole", SketchId = hs.Id, IsCut = true, Depth = "T", Reverse = true, Draft = "Draft" });
        var g = new Sk(d);
        g.Rect("0", "2", "20", "8");
        var gs = d.AddFeature(new SketchFeature { Name = "Slot", Plane = PlaneRef.Front, Sketch = g.S });
        d.AddFeature(new ExtrudeFeature { Name = "Slot", SketchId = gs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.AddFeature(new ChamferFeature
        {
            Name = "Rim", Distance = "C",
            Edges = { E($"{plate.Id}.End", $"{hole.Id}.S.C{c}", new Vec3(-s.Eval("D/2"), 0, s.Eval("T"))) },
        });
        return d;
    }

    static CadDocument PlateHoles()
    {
        var d = Doc(("X", "50"), ("Y", "30"), ("T", "12"), ("D", "16"), ("P", "25"), ("R", "2"), ("C", "1"));
        var s = new Sk(d);
        s.Rect("-X", "-Y", "X", "Y");
        var ss = d.AddFeature(new SketchFeature { Name = "Plate", Plane = PlaneRef.Front, Sketch = s.S });
        var plate = d.AddFeature(new ExtrudeFeature { Name = "Plate", SketchId = ss.Id, Depth = "T" });
        var h = new Sk(d);
        var c1 = h.Circle(h.P("-P", "0"), "D");
        var c2 = h.Circle(h.P("P", "0"), "D");
        var hs = d.AddFeature(new SketchFeature { Name = "Holes", Plane = PlaneRef.Front, Sketch = h.S });
        var holes = d.AddFeature(new ExtrudeFeature { Name = "Holes", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Top rims", Radius = "R",
            Edges =
            {
                E($"{plate.Id}.End", $"{holes.Id}.S.C{c1}", new Vec3(-V("P") + V("D/2"), 0, V("T"))),
                E($"{plate.Id}.End", $"{holes.Id}.S.C{c2}", new Vec3(V("P") + V("D/2"), 0, V("T"))),
            },
        });
        d.AddFeature(new ChamferFeature
        {
            Name = "Bottom rims", Distance = "C",
            Edges =
            {
                E($"{plate.Id}.Start", $"{holes.Id}.S.C{c1}", new Vec3(-V("P") - V("D/2"), 0, 0)),
                E($"{plate.Id}.Start", $"{holes.Id}.S.C{c2}", new Vec3(V("P") - V("D/2"), 0, 0)),
            },
        });
        return d;
    }
}

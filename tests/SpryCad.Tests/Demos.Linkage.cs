using SpryCad.Core;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

static partial class Demos
{
    // the linkage's layout (mm): the plate's top at BT; the crank's pivot at the origin, the rocker's at O4 along X;
    // a four-bar (crank, coupler, rocker: Grashof, so the crank turns right round), a rod from the rocker's end to a
    // slider on a rail along y = 0, and two gears above the linkage, the first locked to the crank
    const double LkBT = 6, LkT = 5, LkO4 = 100, LkCrank = 35, LkCoupler = 110, LkRocker = 70, LkRod = 100;
    const double LkPinD = 8, LkHoleD = 8.2, LkRailY = -10;

    /// <summary>
    /// The linkage's joints for a crank angle (radians): A the crank's end, B the rocker's (above the line of the
    /// pivots), C the slider's pin on y = 0 beyond B.
    /// </summary>
    public static (Vec3 A, Vec3 B, Vec3 C) LinkageJoints(double crank)
    {
        var a = new Vec3(LkCrank * Math.Cos(crank), LkCrank * Math.Sin(crank), 0);
        var o4 = new Vec3(LkO4, 0, 0);
        // B: on the circle of the coupler about A and the rocker's about O4, the upper of the two
        var d = (o4 - a).Length;
        var u = (o4 - a) / d;
        var along = (LkCoupler * LkCoupler - LkRocker * LkRocker + d * d) / (2 * d);
        var h = Math.Sqrt(LkCoupler * LkCoupler - along * along);
        var n = new Vec3(-u.Y, u.X, 0);
        var b = a + u * along + n * h;
        if (b.Y < 0) b = a + u * along - n * h;
        var c = new Vec3(b.X + Math.Sqrt(LkRod * LkRod - b.Y * b.Y), 0, 0);
        return (a, b, c);
    }

    /// <summary>
    /// A link: a bar with round ends, Len between the centres of its two holes (the first on the origin, the other
    /// along X), on the Front plane, T thick.
    /// </summary>
    static CadDocument LinkPart(double len, double[] color)
    {
        var d = Doc(("Len", $"{len}"), ("W", "16"), ("T", $"{LkT}"), ("Hole", $"{LkHoleD}"));
        var s = new Sk(d);
        long end = s.P("Len", "0");
        long a = s.P("0", "-W/2"), b = s.P("Len", "-W/2"), c = s.P("Len", "W/2"), e = s.P("0", "W/2");
        s.Line(a, b); s.Arc(end, b, c); s.Line(c, e); s.Arc(Sketch.OriginId, e, a);
        s.Circle(Sketch.OriginId, "Hole"); s.Circle(end, "Hole");
        var sk = d.AddFeature(new SketchFeature { Name = "Link", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Link", SketchId = sk.Id, Depth = "T" });
        d.Color = color;
        return d;
    }

    /// <summary>A joint pin: a shaft Len long under a head.</summary>
    static CadDocument PinPart(double len)
    {
        var d = Doc(("D", $"{LkPinD}"), ("Len", $"{len}"), ("HeadD", "13"), ("HeadT", "2.5"));
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "D");
        var sk = d.AddFeature(new SketchFeature { Name = "Shaft", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Shaft", SketchId = sk.Id, Depth = "Len" });
        var top = d.AddFeature(new PlaneFeature { Name = "Under head", Base = PlaneRef.Front, Offset = "Len" });
        var hs = new Sk(d);
        hs.Circle(Sketch.OriginId, "HeadD");
        var hk = d.AddFeature(new SketchFeature { Name = "Head", Plane = new PlaneRef { Feature = top.Id }, Sketch = hs.S });
        d.AddFeature(new ExtrudeFeature { Name = "Head", SketchId = hk.Id, Depth = "HeadT" });
        d.Color = new[] { 0.72, 0.72, 0.74 };
        return d;
    }

    /// <summary>
    /// The base: a plate (its top at BT) with the crank's and rocker's pivot pins standing on it, a rail for the slider
    /// along X (its face at y = RailY), and the gears' shafts up to ShaftTop: above the crank's pivot pin, and on its
    /// own below it.
    /// </summary>
    static CadDocument LinkageBase(double gearCentre, double gearBore, double shaftTop)
    {
        var d = Doc(("BT", $"{LkBT}"), ("O4", $"{LkO4}"), ("PinD", $"{LkPinD}"), ("PinH", "8"), ("RailY", $"{-LkRailY}"),
            ("G", $"{gearCentre}"), ("ShaftD", $"{gearBore}-0.2"), ("ShaftTop", $"{shaftTop}"));
        var p = new Sk(d);
        p.Rect("-60", "-125", "250", "100");
        var ps = d.AddFeature(new SketchFeature { Name = "Plate", Plane = PlaneRef.Front, Sketch = p.S });
        d.AddFeature(new ExtrudeFeature { Name = "Plate", SketchId = ps.Id, Depth = "BT" });
        var pins = new Sk(d);
        pins.Circle(Sketch.OriginId, "PinD");
        pins.Circle(pins.P("O4", "0"), "PinD");
        var pk = d.AddFeature(new SketchFeature { Name = "Pivots", Plane = PlaneRef.Front, Sketch = pins.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pivots", SketchId = pk.Id, Depth = "BT+PinH" });
        var r = new Sk(d);
        r.Rect("115", "-(RailY+8)", "245", "-RailY");
        var rk = d.AddFeature(new SketchFeature { Name = "Rail", Plane = PlaneRef.Front, Sketch = r.S });
        d.AddFeature(new ExtrudeFeature { Name = "Rail", SketchId = rk.Id, Depth = "BT+8" });
        // the gears' shafts: the drive gear's on top of the crank's pivot pin, the other's from the plate
        var top = d.AddFeature(new PlaneFeature { Name = "Pivot top", Base = PlaneRef.Front, Offset = "BT+PinH" });
        var g1 = new Sk(d);
        g1.Circle(Sketch.OriginId, "ShaftD");
        var g1k = d.AddFeature(new SketchFeature { Name = "Drive gear shaft", Plane = new PlaneRef { Feature = top.Id }, Sketch = g1.S });
        d.AddFeature(new ExtrudeFeature { Name = "Drive gear shaft", SketchId = g1k.Id, Depth = "ShaftTop-BT-PinH" });
        var g = new Sk(d);
        g.Circle(g.P("0", "-G"), "ShaftD");
        var gk = d.AddFeature(new SketchFeature { Name = "Gear shaft", Plane = PlaneRef.Front, Sketch = g.S });
        d.AddFeature(new ExtrudeFeature { Name = "Gear shaft", SketchId = gk.Id, Depth = "ShaftTop" });
        d.Color = new[] { 0.42, 0.45, 0.50 };
        return d;
    }

    /// <summary>The slider: a block 30 x 20 x 10 on the plate against the rail, bored for its pin in the middle.</summary>
    static CadDocument SliderPart()
    {
        var d = Doc(("L", "30"), ("W", "20"), ("H", "10"), ("Hole", $"{LkHoleD}"));
        var s = new Sk(d);
        s.Rect("-L/2", "-W/2", "L/2", "W/2");
        s.Circle(Sketch.OriginId, "Hole");
        var sk = d.AddFeature(new SketchFeature { Name = "Block", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Block", SketchId = sk.Id, Depth = "H" });
        d.Color = new[] { 0.30, 0.55, 0.75 };
        return d;
    }

    /// <summary>
    /// A linkage with many mates to drag about: on a fixed base plate, a four-bar (crank, coupler, rocker) on the
    /// plate's pivot pins drives a rod and a slider along a rail; pins through the moving joints; above it all a
    /// gear locked to the crank meshes with a second gear on its own shaft. Every joint is concentric, every layer
    /// coincident on the one below; the gears add a Gear mate and a Lock. Drag the crank, the rocker, the slider or
    /// either gear and everything follows.
    /// </summary>
    static AssemblyModel Linkage()
    {
        var gear = Gear();
        gear.Color = new[] { 0.80, 0.62, 0.30 };
        var gr = new Regenerator().Rebuild(gear);
        double z = gr.EquationValues["z"], gb = gr.EquationValues["b"], gBore = gr.EquationValues["Bore"];
        double centre = 2 * gr.EquationValues["r"] + 0.1;
        // the gears above the linkage (clear of the coupler and the pins' heads), their shafts to the gears' tops
        const double gearLift = 13;
        double gearZ = LkBT + gearLift;
        var parts = new (string, CadDocument)[]
        {
            ("Base", LinkageBase(centre, gBore, gearZ + gb)),
            ("Crank", LinkPart(LkCrank, new[] { 0.80, 0.30, 0.25 })),
            ("Coupler", LinkPart(LkCoupler, new[] { 0.35, 0.65, 0.35 })),
            ("Rocker", LinkPart(LkRocker, new[] { 0.85, 0.70, 0.25 })),
            ("Rod", LinkPart(LkRod, new[] { 0.55, 0.40, 0.70 })),
            ("Slider", SliderPart()),
            ("Pin 10", PinPart(2 * LkT)),
            ("Pin 15", PinPart(3 * LkT)),
            ("Gear", gear),
        };
        return Embedded(parts, (m, paths) =>
        {
            var (A, B, C) = LinkageJoints(60 * Math.PI / 180);
            Transform At(Vec3 p, double zz, double angle = 0) =>
                Transform.Translation(new Vec3(p.X, p.Y, zz)) * Transform.Rotation(Vec3.Zero, Vec3.UnitZ, angle);
            double Dir(Vec3 from, Vec3 to) => Math.Atan2(to.Y - from.Y, to.X - from.X);
            var o4 = new Vec3(LkO4, 0, 0);
            var gearAt = new Vec3(0, -centre, 0);

            var basePlate = m.Insert(paths["Base"], Transform.Identity, embed: true);
            var crank = m.Insert(paths["Crank"], At(Vec3.Zero, LkBT, Dir(Vec3.Zero, A)), embed: true);
            var rocker = m.Insert(paths["Rocker"], At(o4, LkBT, Dir(o4, B)), embed: true);
            var coupler = m.Insert(paths["Coupler"], At(A, LkBT + LkT, Dir(A, B)), embed: true);
            var rod = m.Insert(paths["Rod"], At(B, LkBT + 2 * LkT, Dir(B, C)), embed: true);
            var slider = m.Insert(paths["Slider"], At(C, LkBT), embed: true);
            var pinA = m.Insert(paths["Pin 10"], At(A, LkBT), embed: true);
            var pinB = m.Insert(paths["Pin 15"], At(B, LkBT), embed: true);
            var pinC = m.Insert(paths["Pin 15"], At(C, LkBT), embed: true);
            // the gears (z 0 to b in the part) on their shafts above the linkage; the second turned half a tooth to mesh
            var g1 = m.Insert(paths["Gear"], At(Vec3.Zero, gearZ), embed: true);
            var g2 = m.Insert(paths["Gear"], At(gearAt, gearZ, Math.PI / z), embed: true);
            basePlate.Fixed = true;
            foreach (var c in m.Doc.Components) if (c != basePlate) c.Fixed = false;

            // faces picked by their geometry (in the part)
            MateRef Face(Component c, Func<FaceRange, bool> which) => FaceRef(m, c, which);
            static bool Plane(FaceRange f, double nz, double at) =>
                f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z * Math.Sign(nz) > 0.9 && Math.Abs(f.FirstPlane.Origin.Z - at) < 1e-6;
            static bool Hole(FaceRange f, double x, double y, double r) =>
                f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - r) < 1e-6
                && Math.Abs(f.Bounds.Center.X - x) < 1e-3 && Math.Abs(f.Bounds.Center.Y - y) < 1e-3;
            MateRef Bottom(Component c) => Face(c, f => Plane(f, -1, 0));
            MateRef Top(Component c, double h) => Face(c, f => Plane(f, 1, h));
            MateRef End0(Component c) => Face(c, f => Hole(f, 0, 0, LkHoleD / 2));
            MateRef End1(Component c, double len) => Face(c, f => Hole(f, len, 0, LkHoleD / 2));
            MateRef Shaft(Component c) => Face(c, f => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - LkPinD / 2) < 1e-6);
            MateRef UnderHead(Component c, double len) => Face(c, f => Plane(f, -1, len));
            var plateTop = Face(basePlate, f => Plane(f, 1, LkBT));
            var pivot2 = Face(basePlate, f => Hole(f, 0, 0, LkPinD / 2));
            var pivot4 = Face(basePlate, f => Hole(f, LkO4, 0, LkPinD / 2));
            var rail = Face(basePlate, f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Y > 0.9 && Math.Abs(f.FirstPlane.Origin.Y - LkRailY) < 1e-6);
            var driveShaft = Face(basePlate, f => Hole(f, 0, 0, gBore / 2 - 0.1));
            var gearShaft = Face(basePlate, f => Hole(f, 0, -centre, gBore / 2 - 0.1));
            bool GearBore(FaceRange f) => f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - gBore / 2) < 1e-6;
            bool GearBack(FaceRange f) => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Z < -0.9 && Math.Abs(f.FirstPlane.Origin.Z) < 1e-6;

            const MateType Co = MateType.Coincident, Cc = MateType.Concentric;
            // the four-bar on the plate's pivots
            m.AddMate(Cc, End0(crank), pivot2);
            m.AddMate(Co, Bottom(crank), plateTop);
            m.AddMate(Cc, End0(rocker), pivot4);
            m.AddMate(Co, Bottom(rocker), plateTop);
            m.AddMate(Cc, End0(coupler), End1(crank, LkCrank));
            m.AddMate(Co, Bottom(coupler), Top(crank, LkT));
            m.AddMate(Cc, End1(coupler, LkCoupler), End1(rocker, LkRocker));
            // the rod from the rocker's end (on the coupler) to the slider on the rail
            m.AddMate(Cc, End0(rod), End1(coupler, LkCoupler));
            m.AddMate(Co, Bottom(rod), Top(coupler, LkT));
            m.AddMate(Co, Bottom(slider), plateTop);
            m.AddMate(Co, Face(slider, f => f.Surface.Kind == SurfaceKind.Plane && f.FirstPlane.Normal.Y < -0.9), rail);
            m.AddMate(Cc, End1(rod, LkRod), Face(slider, f => Hole(f, 0, 0, LkHoleD / 2)));
            // the pins through the joints, their heads on the top layer
            m.AddMate(Cc, Shaft(pinA), End1(crank, LkCrank));
            m.AddMate(Co, UnderHead(pinA, 2 * LkT), Top(coupler, LkT));
            m.AddMate(Cc, Shaft(pinB), End1(rocker, LkRocker));
            m.AddMate(Co, UnderHead(pinB, 3 * LkT), Top(rod, LkT));
            m.AddMate(Cc, Shaft(pinC), Face(slider, f => Hole(f, 0, 0, LkHoleD / 2)));
            m.AddMate(Co, UnderHead(pinC, 3 * LkT), Top(rod, LkT));
            // the gears above: the first on the shaft over the crank's pivot and locked to the crank, the second on its
            // own shaft, both held gearLift above the plate, meshing (the other way round, 1:1)
            m.AddMate(Cc, Face(g1, GearBore), driveShaft);
            m.AddMate(Co, Face(g1, GearBack), plateTop, gearLift);
            m.AddMate(Cc, Face(g2, GearBore), gearShaft);
            m.AddMate(Co, Face(g2, GearBack), plateTop, gearLift);
            m.AddMate(MateType.Lock, Face(g1, GearBore), End0(crank));
            m.AddMate(MateType.Gear, Face(g1, GearBore), Face(g2, GearBore));
        });
    }
}

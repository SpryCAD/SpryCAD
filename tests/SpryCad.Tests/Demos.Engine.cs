using SpryCad.Core;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// A hopper-cooled hit and miss engine: a horizontal single cylinder with two spoked flywheels, cast iron frame,
/// cylinder and water hopper in one casting, its exhaust valve worked from the crankshaft through a 2:1 cam gear, a
/// push rod along the side and a rocker on the head. Each part is modeled as a designer would (sketches with
/// relations and dimensions from the part's variables, then features), the assembly mated so turning a flywheel runs
/// the piston, the cam gear and the valve.
/// </summary>
/// <remarks>
/// The layout (mm): the crankshaft along Z through the origin, the cylinder along X towards -X at y = 0, the base
/// below; +Z is the cam gear's side. Parts that move are modeled about their own joint (the crankshaft about its
/// axis, the rod about its big end, the piston about its wrist pin); the castings in the engine's coordinates.
/// </remarks>
static partial class Demos
{
    // the running gear
    const double HmR = 30, HmRod = 130, HmBore = 50, HmJ = 25, HmPin = 22, HmWp = 12;
    // the crank's webs (inner faces at ±WebIn), the main bearings (z from BrgIn to BrgOut either side)
    const double HmWebIn = 13, HmWebT = 12, HmBrgIn = 30, HmBrgOut = 70, HmShaftL = 143;
    // the gears' back faces, their width, the eccentric on the cam gear (its offset, diameter, thickness)
    const double HmGearZ = 73, HmGearB = 12, HmModule = 2, HmZCrank = 24, HmZCam = 48;
    const double HmEcc = 3, HmEccD = 36, HmEccT = 10;
    // the flywheels' spoke planes (±), the cylinder head's inner face, its thickness, the valves (z ±ValveZ)
    const double HmFwZ = 118, HmHeadX = -195, HmHeadT = 36, HmValveZ = 13;

    static double HmCamX => -(HmModule * (HmZCrank + HmZCam) / 2 + 0.1);
    static double HmPushZ => HmGearZ + HmGearB + HmEccT / 2;

    /// <summary>A part's material, for mass properties (the densities the app's Mass Properties offers; rubber and glass typical).</summary>
    static void HmMaterial(CadDocument d, string material)
    {
        d.Material = material;
        d.Density = material switch
        {
            "Cast iron" => 7200, "Stainless steel" => 8000, "Brass" => 8500, "Copper" => 8940, "Rubber" => 1100, "Glass" => 2500,
            _ => 7850,
        };
    }

    /// <summary>The piston's wrist pin (x) for a crank angle (radians, 0: the crank pin on +X).</summary>
    public static double HmPistonX(double crank) =>
        HmR * Math.Cos(crank) - Math.Sqrt(HmRod * HmRod - HmR * HmR * Math.Sin(crank) * Math.Sin(crank));

    // ------------------------------------------------------------------------------------------
    // running gear

    /// <summary>
    /// The crankshaft, about its axis (Z), the crank pin on +X: a web each side of the pin (an arc about the shaft
    /// and one about the pin, joined by lines tangent to both), the pin between them, the main journal out from each
    /// web to the ends, filleted into the webs; keyways for the gear and flywheels, the ends chamfered.
    /// </summary>
    static CadDocument HmCrankshaft()
    {
        var d = Doc(("R", $"{HmR}"), ("J", $"{HmJ}"), ("Pin", $"{HmPin}"), ("WebIn", $"{HmWebIn}"), ("WebT", $"{HmWebT}"),
            ("L", $"{HmShaftL}"), ("Rw", "24"), ("Rp", "18"), ("Rf", "2"), ("C", "1.5"),
            ("KeyW", "6"), ("KeyT", "3"), ("KeyIn", $"{HmGearZ}"), ("KeyIn2", $"{HmFwZ - 30}"),
            ("ca", "(Rw-Rp)/R"), ("sa", "sqrt(1-ca^2)"));
        var webPlane = d.AddFeature(new PlaneFeature { Name = "Web face", Base = PlaneRef.Front, Offset = "WebIn" });
        var w = new Sk(d);
        int pc = w.P("R", "0");
        int p1 = w.Q("Rw*ca", "Rw*sa"), p3 = w.Q("Rw*ca", "-Rw*sa"), p2 = w.Q("R+Rp*ca", "Rp*sa"), p4 = w.Q("R+Rp*ca", "-Rp*sa");
        var big = w.Arc(Sketch.OriginId, p1, p3);
        var small = w.Arc(pc, p4, p2);
        var top = w.Line(p2, p1); var bot = w.Line(p3, p4);
        w.Dim(CType.Radius, "Rw", big); w.Dim(CType.Radius, "Rp", small);
        foreach (var l in new[] { top, bot }) { w.Rel(CType.Tangent, l, big); w.Rel(CType.Tangent, l, small); }
        var ws = d.AddFeature(new SketchFeature { Name = "Web", Plane = new PlaneRef { Feature = webPlane.Id }, Sketch = w.S });
        var web = d.AddFeature(new ExtrudeFeature { Name = "Web", SketchId = ws.Id, Depth = "WebT" });
        // the journal from the web's outer face to the end
        var outer = d.AddFeature(new PlaneFeature { Name = "Web outer face", Base = PlaneRef.Front, Offset = "WebIn+WebT" });
        var j = new Sk(d);
        var jc = j.Circle(Sketch.OriginId, "J");
        var js = d.AddFeature(new SketchFeature { Name = "Journal", Plane = new PlaneRef { Feature = outer.Id }, Sketch = j.S });
        var journal = d.AddFeature(new ExtrudeFeature { Name = "Journal", SketchId = js.Id, Depth = "L-WebIn-WebT" });
        double V(string e) => w.Eval(e);
        var fil = d.AddFeature(new FilletFeature
        {
            Name = "Journal fillet", Radius = "Rf",
            Edges = { E($"{journal.Id}.S.C{jc}", $"{web.Id}.End", new Vec3(0, V("J/2"), V("WebIn+WebT"))) },
        });
        d.AddFeature(new MirrorFeature { Name = "Web and journal, back", Seeds = { web.Id, journal.Id, fil.Id }, Plane = PlaneRef.Front });
        // the crank pin between the webs
        var p = new Sk(d);
        var pcirc = p.Circle(p.P("R", "0"), "Pin");
        var ps = d.AddFeature(new SketchFeature { Name = "Crank pin", Plane = PlaneRef.Front, Sketch = p.S });
        var pin = d.AddFeature(new ExtrudeFeature { Name = "Crank pin", SketchId = ps.Id, End = EndCondition.MidPlane, Depth = "2*WebIn" });
        d.AddFeature(new FilletFeature
        {
            Name = "Pin fillets", Radius = "Rf",
            Edges =
            {
                E($"{pin.Id}.S.C{pcirc}", $"{web.Id}.Start", new Vec3(V("R"), V("Pin/2"), V("WebIn"))),
                E($"{pin.Id}.S.C{pcirc}", $"{web.Id}.Start", new Vec3(V("R"), V("Pin/2"), -V("WebIn"))),
            },
        });
        // keyways (on top of the journal: Top plane at J/2, sketch y = -z): the gear's and flywheel's on +Z, the
        // flywheel's on -Z
        var kp = d.AddFeature(new PlaneFeature { Name = "Keyway plane", Base = PlaneRef.Top, Offset = "J/2" });
        var k = new Sk(d);
        k.Rect("-KeyW/2", "-(L+1)", "KeyW/2", "-KeyIn");
        k.Rect("-KeyW/2", "KeyIn2", "KeyW/2", "L+1");
        var ks = d.AddFeature(new SketchFeature { Name = "Keyways", Plane = new PlaneRef { Feature = kp.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyways", SketchId = ks.Id, IsCut = true, Reverse = true, Depth = "KeyT" });
        d.Color = new[] { 0.66, 0.67, 0.70 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// The connecting rod, its big end on the origin and its small end Len along -X: a tapered I-section shank between
    /// a round small end (bushed for the wrist pin) and a block big end split on the crank pin's centre (the cap is a
    /// part of its own), recessed on both faces, its bolt holes along X.
    /// </summary>
    static CadDocument HmConRod()
    {
        var d = Doc(("Len", $"{HmRod}"), ("Pin", $"{HmPin}"), ("Wp", $"{HmWp}"), ("BigW", "22"), ("SmallW", "20"), ("SmallD", "26"),
            ("BlockH", "56"), ("BlockL", "16"), ("W1", "16"), ("W2", "22"), ("ShankT", "14"), ("Web", "6"),
            ("BoltY", "20"), ("BoltD", "6.4"), ("Rf", "3"), ("Rr", "2"));
        // big end block (x -BlockL..0), centred on the axis
        var b = new Sk(d);
        var bl = b.Rect("-BlockL", "-BlockH/2", "0", "BlockH/2");
        var bs = d.AddFeature(new SketchFeature { Name = "Big end", Plane = PlaneRef.Front, Sketch = b.S });
        var block = d.AddFeature(new ExtrudeFeature { Name = "Big end", SketchId = bs.Id, End = EndCondition.MidPlane, Depth = "BigW" });
        // small end boss
        var s = new Sk(d);
        var se = s.Circle(s.P("-Len", "0"), "SmallD");
        var ss = d.AddFeature(new SketchFeature { Name = "Small end", Plane = PlaneRef.Front, Sketch = s.S });
        var small = d.AddFeature(new ExtrudeFeature { Name = "Small end", SketchId = ss.Id, End = EndCondition.MidPlane, Depth = "SmallW" });
        // the shank: tapering from W1 at the small end to W2 at the block
        var t = new Sk(d);
        var sh = t.Poly(t.P("-Len", "-W1/2"), t.P("-BlockL", "-W2/2"), t.P("-BlockL", "W2/2"), t.P("-Len", "W1/2"));
        var ts = d.AddFeature(new SketchFeature { Name = "Shank", Plane = PlaneRef.Front, Sketch = t.S });
        var shank = d.AddFeature(new ExtrudeFeature { Name = "Shank", SketchId = ts.Id, End = EndCondition.MidPlane, Depth = "ShankT" });
        double V(string e) => t.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Shank fillets", Radius = "Rf",
            Edges =
            {
                E($"{shank.Id}.S.L{sh[0]}", $"{small.Id}.S.C{se}", new Vec3(-V("Len-10"), -V("W1/2"), 0)),
                E($"{shank.Id}.S.L{sh[2]}", $"{small.Id}.S.C{se}", new Vec3(-V("Len-10"), V("W1/2"), 0)),
                E($"{shank.Id}.S.L{sh[0]}", $"{block.Id}.S.L{bl[3]}", new Vec3(-V("BlockL"), -V("W2/2"), 0)),
                E($"{shank.Id}.S.L{sh[2]}", $"{block.Id}.S.L{bl[3]}", new Vec3(-V("BlockL"), V("W2/2"), 0)),
            },
        });
        // the I-section: a recess in each face of the shank, Web left between them
        var r = new Sk(d);
        r.Poly(r.P("-(Len-SmallD/2-2)", "-(W1/2-3)"), r.P("-(BlockL+3)", "-(W2/2-3)"), r.P("-(BlockL+3)", "W2/2-3"), r.P("-(Len-SmallD/2-2)", "W1/2-3"));
        var face = d.AddFeature(new PlaneFeature { Name = "Shank face", Base = PlaneRef.Front, Offset = "ShankT/2" });
        var rs = d.AddFeature(new SketchFeature { Name = "Recess", Plane = new PlaneRef { Feature = face.Id }, Sketch = r.S });
        var rec = d.AddFeature(new ExtrudeFeature { Name = "Recess", SketchId = rs.Id, IsCut = true, Reverse = true, Depth = "(ShankT-Web)/2" });
        d.AddFeature(new MirrorFeature { Name = "Recess, back", Seeds = { rec.Id }, Plane = PlaneRef.Front });
        // bores: the crank pin's (half of it here), the wrist pin's
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Pin");
        h.Circle(h.P("-Len", "0"), "Wp");
        var hs = d.AddFeature(new SketchFeature { Name = "Bores", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bores", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        // the cap bolts' holes, from the split face (Right plane: sketch x = -z, y = y) into the block
        var bo = new Sk(d);
        bo.Circle(bo.P("0", "BoltY"), "BoltD");
        bo.Circle(bo.P("0", "-BoltY"), "BoltD");
        var bos = d.AddFeature(new SketchFeature { Name = "Bolt holes", Plane = PlaneRef.Right, Sketch = bo.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bolt holes", SketchId = bos.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = new[] { 0.62, 0.63, 0.66 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>The rod's cap: the other half of the big end (x 0..CapL), bored, its bolt holes along X.</summary>
    static CadDocument HmRodCap()
    {
        var d = Doc(("Pin", $"{HmPin}"), ("BigW", "22"), ("BlockH", "56"), ("CapL", "14"), ("BoltY", "20"), ("BoltD", "6.4"), ("Rc", "8"));
        var c = new Sk(d);
        var L = c.Rect("0", "-BlockH/2", "CapL", "BlockH/2");
        var cs = d.AddFeature(new SketchFeature { Name = "Cap", Plane = PlaneRef.Front, Sketch = c.S });
        var cap = d.AddFeature(new ExtrudeFeature { Name = "Cap", SketchId = cs.Id, End = EndCondition.MidPlane, Depth = "BigW" });
        double V(string e) => c.Eval(e);
        d.AddFeature(new FilletFeature
        {
            Name = "Cap corners", Radius = "Rc",
            Edges =
            {
                E($"{cap.Id}.S.L{L[0]}", $"{cap.Id}.S.L{L[1]}", new Vec3(V("CapL"), -V("BlockH/2"), 0)),
                E($"{cap.Id}.S.L{L[1]}", $"{cap.Id}.S.L{L[2]}", new Vec3(V("CapL"), V("BlockH/2"), 0)),
            },
        });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Pin");
        var hs = d.AddFeature(new SketchFeature { Name = "Bore", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bore", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        var bo = new Sk(d);
        bo.Circle(bo.P("0", "BoltY"), "BoltD");
        bo.Circle(bo.P("0", "-BoltY"), "BoltD");
        var bos = d.AddFeature(new SketchFeature { Name = "Bolt holes", Plane = PlaneRef.Right, Sketch = bo.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bolt holes", SketchId = bos.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = new[] { 0.62, 0.63, 0.66 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// The piston about its wrist pin (on the origin, along Z), its crown towards -X: a revolved half section with
    /// three ring grooves and a hollow skirt, the wrist pin's bosses inside, bored through.
    /// </summary>
    static CadDocument HmPiston()
    {
        var d = Doc(("D", $"{HmBore}-0.2"), ("Crown", "25"), ("Skirt", "30"), ("CrownT", "8"), ("Wall", "4"),
            ("Gw", "2"), ("Gd", "2.5"), ("G1", "4"), ("Gp", "5"), ("BossD", "22"), ("Wp", $"{HmWp}"), ("C", "0.8"), ("Slot", "32"), ("SlotW", "22"));
        // the half section on Front (x along the axis, y the radius), revolved about its edge on the axis
        var s = new Sk(d);
        var pts = new List<int> { s.P("-Crown", "0"), s.P("-Crown", "D/2") };
        for (int g = 0; g < 3; g++)
        {
            string x0 = $"-(Crown-G1-{g}*Gp)", x1 = $"-(Crown-G1-{g}*Gp-Gw)";
            pts.Add(s.P(x0, "D/2")); pts.Add(s.P(x0, "D/2-Gd")); pts.Add(s.P(x1, "D/2-Gd")); pts.Add(s.P(x1, "D/2"));
        }
        pts.Add(s.P("Skirt", "D/2")); pts.Add(s.P("Skirt", "D/2-Wall"));
        pts.Add(s.P("-(Crown-CrownT)", "D/2-Wall")); pts.Add(s.P("-(Crown-CrownT)", "0"));
        var L = s.Poly(pts.ToArray());
        var sk = d.AddFeature(new SketchFeature { Name = "Section", Plane = PlaneRef.Front, Sketch = s.S });
        var rev = d.AddFeature(new RevolveFeature { Name = "Piston", SketchId = sk.Id, AxisEntity = L[^1], Angle = "360" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Crown edge", Distance = "C",
            Edges = { E($"{rev.Id}.R.L{L[0]}", $"{rev.Id}.R.L{L[1]}", new Vec3(-V("Crown"), V("D/2"), 0)) },
        });
        // the wrist pin's bosses, across inside the skirt, and the bore through them and the skirt
        var b = new Sk(d);
        b.Circle(Sketch.OriginId, "BossD");
        var bs = d.AddFeature(new SketchFeature { Name = "Pin bosses", Plane = PlaneRef.Front, Sketch = b.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pin bosses", SketchId = bs.Id, End = EndCondition.MidPlane, Depth = "D-1.5*Wall" });
        // room between the bosses for the rod's small end to swing (round the pin's axis)
        var sl = new Sk(d);
        sl.Circle(Sketch.OriginId, "Slot");
        var sls = d.AddFeature(new SketchFeature { Name = "Rod slot", Plane = PlaneRef.Front, Sketch = sl.S });
        d.AddFeature(new ExtrudeFeature { Name = "Rod slot", SketchId = sls.Id, IsCut = true, End = EndCondition.MidPlane, Depth = "SlotW" });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Wp");
        var hs = d.AddFeature(new SketchFeature { Name = "Pin bore", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pin bore", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = new[] { 0.78, 0.79, 0.80 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    /// <summary>The wrist pin: a plain pin along Z, centred on the origin, its ends chamfered.</summary>
    static CadDocument HmWristPin()
    {
        var d = Doc(("Wp", $"{HmWp}"), ("L", $"{HmBore}-6"), ("C", "0.6"));
        var s = new Sk(d);
        var c = s.Circle(Sketch.OriginId, "Wp");
        var sk = d.AddFeature(new SketchFeature { Name = "Pin", Plane = PlaneRef.Front, Sketch = s.S });
        var pin = d.AddFeature(new ExtrudeFeature { Name = "Pin", SketchId = sk.Id, End = EndCondition.MidPlane, Depth = "L" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Ends", Distance = "C",
            Edges = { E($"{pin.Id}.S.C{c}", $"{pin.Id}.Start", new Vec3(V("Wp/2"), 0, -V("L/2"))), E($"{pin.Id}.S.C{c}", $"{pin.Id}.End", new Vec3(V("Wp/2"), 0, V("L/2"))) },
        });
        d.Color = new[] { 0.70, 0.71, 0.74 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// A flywheel about Z, its spokes on the Front plane: a crowned rim (revolved, its inner edges rounded), a hub
    /// standing further in (towards -Z) than out, NS spokes of an oval section tapering (drafted) from the hub to the
    /// rim, filleted into both, and a keyed bore.
    /// </summary>
    static CadDocument HmFlywheel()
    {
        var d = Doc(("D", "280"), ("RimW", "40"), ("RimT", "20"), ("Crown", "1.5"), ("HubD", "72"), ("HubIn", "30"), ("HubOut", "20"),
            ("Bore", $"{HmJ}"), ("KeyW", "6"), ("KeyT", "3"), ("NS", "6"), ("SpW", "30"), ("SpT", "16"), ("SpDraft", "3"),
            ("Rf", "5"), ("Rh", "4"), ("Rr", "3"), ("C", "1.5"), ("CapPCD", "38"));
        // the rim's section on Right (sketch x = -z, y = y), revolved about the axis (the sketch's x axis)
        var r = new Sk(d);
        int a = r.P("-RimW/2", "D/2-RimT"), b = r.P("RimW/2", "D/2-RimT"), c = r.P("RimW/2", "D/2-Crown"), e = r.P("-RimW/2", "D/2-Crown");
        int m = r.P("0", "D/2");
        var rl = new[] { r.Line(a, b), r.Line(b, c) };
        var crown = r.ArcThrough(c, m, e);
        var rl3 = r.Line(e, a);
        r.Centerline(Sketch.OriginId, r.P("RimW/2", "0"));
        var rs = d.AddFeature(new SketchFeature { Name = "Rim section", Plane = PlaneRef.Right, Sketch = r.S });
        var rim = d.AddFeature(new RevolveFeature { Name = "Rim", SketchId = rs.Id, Angle = "360" });
        // the hub (HubIn towards -z, HubOut towards +z: sketch x = -z)
        var h = new Sk(d);
        var hl = h.Poly(h.P("-HubOut", "Bore/2"), h.P("HubIn", "Bore/2"), h.P("HubIn", "HubD/2"), h.P("-HubOut", "HubD/2"));
        h.Centerline(Sketch.OriginId, h.P("HubIn", "0"));
        var hs = d.AddFeature(new SketchFeature { Name = "Hub section", Plane = PlaneRef.Right, Sketch = h.S });
        var hub = d.AddFeature(new RevolveFeature { Name = "Hub", SketchId = hs.Id, Angle = "360" });
        double V(string ex) => r.Eval(ex);
        // one spoke along +X: an oval (two half circles joined by lines) on a plane inside the hub, drafted out to
        // inside the rim
        var sp = d.AddFeature(new PlaneFeature { Name = "Spoke plane", Base = PlaneRef.Right, Offset = "HubD/2-4" });
        var o = new Sk(d);
        // sketch x = -z (the thickness), y = y (the width)
        int o1 = o.P("SpT/2", "-(SpW/2-SpT/2)"), o2 = o.P("SpT/2", "SpW/2-SpT/2"), o3 = o.P("-SpT/2", "SpW/2-SpT/2"), o4 = o.P("-SpT/2", "-(SpW/2-SpT/2)");
        int oc1 = o.P("0", "SpW/2-SpT/2"), oc2 = o.P("0", "-(SpW/2-SpT/2)");
        var side1 = o.Line(o1, o2); var end1 = o.Arc(oc1, o2, o3); var side2 = o.Line(o3, o4); var end2 = o.Arc(oc2, o4, o1);
        var os = d.AddFeature(new SketchFeature { Name = "Spoke section", Plane = new PlaneRef { Feature = sp.Id }, Sketch = o.S });
        var spoke = d.AddFeature(new ExtrudeFeature { Name = "Spoke", SketchId = os.Id, Depth = "D/2-RimT/2-HubD/2+4", Draft = "SpDraft" });
        var toRim = d.AddFeature(new FilletFeature
        {
            Name = "Spoke to rim", Radius = "Rf",
            Edges = { E($"{rim.Id}.R.L{rl[0]}", $"{spoke.Id}.S.L{side1}", new Vec3(V("D/2-RimT"), 0, -V("SpT/2") + 0.4)) },
        });
        var toHub = d.AddFeature(new FilletFeature
        {
            Name = "Spoke to hub", Radius = "Rh",
            Edges = { E($"{hub.Id}.R.L{hl[2]}", $"{spoke.Id}.S.L{side1}", new Vec3(V("HubD/2"), 0, -V("SpT/2"))) },
        });
        d.AddFeature(new CircularPatternFeature { Name = "Spokes", Seeds = { spoke.Id, toRim.Id, toHub.Id }, Axis = new DirRef { Axis = "Z" }, Count = "NS" });
        // the rim's inner edges rounded, the hub's ends chamfered
        d.AddFeature(new FilletFeature
        {
            Name = "Rim inner edges", Radius = "Rr",
            Edges =
            {
                E($"{rim.Id}.R.L{rl[0]}", $"{rim.Id}.R.L{rl[1]}", new Vec3(V("D/2-RimT"), 0, -V("RimW/2"))),
                E($"{rim.Id}.R.L{rl[0]}", $"{rim.Id}.R.L{rl3}", new Vec3(V("D/2-RimT"), 0, V("RimW/2"))),
            },
        });
        d.AddFeature(new ChamferFeature
        {
            Name = "Hub ends", Distance = "C",
            Edges =
            {
                E($"{hub.Id}.R.L{hl[2]}", $"{hub.Id}.R.L{hl[1]}", new Vec3(V("HubD/2"), 0, -V("HubIn"))),
                E($"{hub.Id}.R.L{hl[2]}", $"{hub.Id}.R.L{hl[3]}", new Vec3(V("HubD/2"), 0, V("HubOut"))),
            },
        });
        // tapped holes for the hub cap's screws in the hub's outer face, clear of the keyway
        var ho = d.AddFeature(new PlaneFeature { Name = "Hub outer face", Base = PlaneRef.Front, Offset = "HubOut" });
        var cs = new Sk(d);
        cs.Point(cs.P("CapPCD/2*cos(45)", "CapPCD/2*sin(45)"));
        var css = d.AddFeature(new SketchFeature { Name = "Cap screw", Plane = new PlaneRef { Feature = ho.Id }, Sketch = cs.S });
        var csh = d.AddFeature(new HoleFeature { Name = "Cap screw", SketchId = css.Id, Diameter = "4.2", ThroughAll = false, Depth = "10" });
        d.AddFeature(new CircularPatternFeature { Name = "Cap screws", Seeds = { csh.Id }, Axis = new DirRef { Axis = "Z" }, Count = "4" });
        // keyway along the bore (Top plane: sketch y = -z)
        var k = new Sk(d);
        k.Rect("-KeyW/2", "-(HubOut+1)", "KeyW/2", "HubIn+1");
        var kp = d.AddFeature(new PlaneFeature { Name = "Keyway plane", Base = PlaneRef.Top, Offset = "Bore/2+KeyT" });
        var ks = d.AddFeature(new SketchFeature { Name = "Keyway", Plane = new PlaneRef { Feature = kp.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Keyway", SketchId = ks.Id, IsCut = true, Reverse = true, Depth = "KeyT+1" });
        d.Color = new[] { 0.72, 0.08, 0.07 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // castings

    /// <summary>
    /// The engine's main casting, in the engine's coordinates: a base plate (drafted, its corners rounded, bolt
    /// holes), the body standing on it to the crank's height, the main bearings' lower halves either side, the
    /// cylinder barrel along -X ending in a square flange for the head, the water hopper on top (drafted, cored out
    /// round a liner that carries the bore), the crank pit opened through the body, a boss for the cam gear's stud,
    /// lugs guiding the push rod, and an oil boss on the barrel.
    /// </summary>
    static CadDocument HmCasting()
    {
        var d = Doc(("BaseY", "150"), ("BaseT", "20"), ("BaseX0", "215"), ("BaseX1", "60"), ("BaseW", "170"), ("BaseR", "15"), ("BaseDraft", "5"),
            ("BodyX0", "170"), ("BodyX1", "40"), ("BodyW", "116"), ("BodyDraft", "2"), ("BodyR", "20"), ("Fl", "8"), ("FlH", "16"),
            ("WallR", "4"), ("RibR", "3"), ("JointR", "5"), ("RibT", "12"), ("RibIn", "50"), ("PedRibY", "110"), ("BaseRibX", "150"), ("BaseRibP", "90"), ("BaseRibOut", "80"), ("BaseRibH", "45"),
            ("BrgIn", $"{HmBrgIn}"), ("BrgOut", $"{HmBrgOut}"), ("BrgW", "64"), ("BrgDrop", "40"), ("J", $"{HmJ}"), ("CapStud", "25"), ("PedR", "6"),
            ("HeadX", $"{-HmHeadX}"), ("BarrelD", "104"), ("BarrelEnd", "70"), ("FlangeD", "116"), ("FlangeT", "12"), ("StudPCD", "96"), ("NStud", "6"),
            ("HopX0", "175"), ("HopX1", "85"), ("HopW", "116"), ("HopH", "82"), ("HopWall", "7"), ("HopDraft", "2"), ("HopR", "12"), ("HopTopR", "4"), ("Jacket", "20"), ("FillD", "44"),
            ("LinerD", "66"), ("Bore", $"{HmBore}"), ("Relief", "58"), ("ReliefL", "8"),
            ("PitX1", "60"), ("PitW", "2*BrgIn"), ("PitBot", "110"),
            ("CamX", $"{-HmCamX}"), ("CamD", "36"), ("Stud", "16"), ("GearZ", $"{HmGearZ}"), ("CamIn", "40"),
            ("LugX", "172"), ("LugP", "24"), ("LugT", "10"), ("LugIn", "48"), ("PushZ", $"{HmPushZ}"), ("PushD", "8.4"),
            ("OilX", "77"), ("Tap8", "6.8"));
        // the base plate (Top: sketch y = -z), drafted, its corners rounded
        var bb = d.AddFeature(new PlaneFeature { Name = "Base bottom", Base = PlaneRef.Top, Offset = "-BaseY" });
        var b = new Sk(d);
        var bl = b.Rect("-BaseX0", "-BaseW/2", "BaseX1", "BaseW/2");
        var bs = d.AddFeature(new SketchFeature { Name = "Base", Plane = new PlaneRef { Feature = bb.Id }, Sketch = b.S });
        var basePlate = d.AddFeature(new ExtrudeFeature { Name = "Base", SketchId = bs.Id, Depth = "BaseT", Draft = "BaseDraft" });
        double V(string e) => b.Eval(e);
        string BF(int i) => $"{basePlate.Id}.S.L{bl[i]}";
        double by = -V("BaseY-BaseT/2");
        d.AddFeature(new FilletFeature
        {
            Name = "Base corners", Radius = "BaseR",
            Edges =
            {
                E(BF(0), BF(1), new Vec3(V("BaseX1"), by, V("BaseW/2"))), E(BF(1), BF(2), new Vec3(V("BaseX1"), by, -V("BaseW/2"))),
                E(BF(2), BF(3), new Vec3(-V("BaseX0"), by, -V("BaseW/2"))), E(BF(3), BF(0), new Vec3(-V("BaseX0"), by, V("BaseW/2"))),
            },
        });
        // the body up to the crank's height
        var bt = d.AddFeature(new PlaneFeature { Name = "Base top", Base = PlaneRef.Top, Offset = "-(BaseY-BaseT)" });
        var y = new Sk(d);
        var yl = y.Rect("-BodyX0", "-BodyW/2", "BodyX1", "BodyW/2");
        var ys = d.AddFeature(new SketchFeature { Name = "Body", Plane = new PlaneRef { Feature = bt.Id }, Sketch = y.S });
        var body = d.AddFeature(new ExtrudeFeature { Name = "Body", SketchId = ys.Id, Depth = "BaseY-BaseT", Draft = "BodyDraft" });
        string YF(int i) => $"{body.Id}.S.L{yl[i]}";
        double yy = -V("BaseY/2");
        d.AddFeature(new FilletFeature
        {
            Name = "Body corners", Radius = "BodyR",
            Edges =
            {
                E(YF(0), YF(1), new Vec3(V("BodyX1"), yy, V("BodyW/2"))), E(YF(1), YF(2), new Vec3(V("BodyX1"), yy, -V("BodyW/2"))),
                E(YF(2), YF(3), new Vec3(-V("BodyX0"), yy, -V("BodyW/2"))), E(YF(3), YF(0), new Vec3(-V("BodyX0"), yy, V("BodyW/2"))),
            },
        });
        // the body's foot flared into the base all round: a chamfer FlH up its side and Fl out over the base (one edge
        // picked, the rest follow round the corners)
        d.AddFeature(new ChamferFeature
        {
            Name = "Foot", Type = ChamferType.DistanceDistance, Distance = "FlH", Distance2 = "Fl",
            Edges = { E(YF(0), $"{basePlate.Id}.End", new Vec3(-V("(BodyX0-BodyX1)/2"), -V("BaseY-BaseT"), V("BodyW/2"))) },
        });
        // the main bearings' lower halves, from BrgIn out to BrgOut each side
        var bp = d.AddFeature(new PlaneFeature { Name = "Bearing inner face", Base = PlaneRef.Front, Offset = "BrgIn" });
        var p = new Sk(d);
        var pl = p.Rect("-BrgW/2", "-BrgDrop", "BrgW/2", "0");
        var ps = d.AddFeature(new SketchFeature { Name = "Bearing pedestal", Plane = new PlaneRef { Feature = bp.Id }, Sketch = p.S });
        var ped = d.AddFeature(new ExtrudeFeature { Name = "Bearing pedestal", SketchId = ps.Id, Depth = "BrgOut-BrgIn" });
        // its outer corners rounded (the caps' are too)
        var pedR = d.AddFeature(new FilletFeature
        {
            Name = "Pedestal corners", Radius = "PedR",
            Edges =
            {
                E($"{ped.Id}.S.L{pl[1]}", $"{ped.Id}.End", new Vec3(V("BrgW/2"), -V("BrgDrop/2"), V("BrgOut"))),
                E($"{ped.Id}.S.L{pl[3]}", $"{ped.Id}.End", new Vec3(-V("BrgW/2"), -V("BrgDrop/2"), V("BrgOut"))),
            },
        });
        // blended into the body's side where it stands out of it (its back side runs onto the body's corner round)
        double SideZ0(double yAt) => V("BodyW/2") - (yAt + V("BaseY-BaseT")) * Math.Tan(V("BodyDraft") * Math.PI / 180);
        var pedJ = d.AddFeature(new FilletFeature
        {
            Name = "Pedestal into the body", Radius = "JointR",
            Edges =
            {
                E($"{ped.Id}.S.L{pl[1]}", YF(0), new Vec3(V("BrgW/2"), -V("BrgDrop/2"), SideZ0(-V("BrgDrop/2")))),
                E($"{ped.Id}.S.L{pl[3]}", YF(0), new Vec3(-V("BrgW/2"), -V("BrgDrop/2"), SideZ0(-V("BrgDrop/2")))),
                E($"{ped.Id}.S.L{pl[0]}", YF(0), new Vec3(0, -V("BrgDrop"), SideZ0(-V("BrgDrop")))),
            },
        });
        d.AddFeature(new MirrorFeature { Name = "Bearing pedestal, back", Seeds = { ped.Id, pedR.Id, pedJ.Id }, Plane = PlaneRef.Front });
        // a rib under each pedestal, down the body's side (Right: sketch x = -z)
        var pr = new Sk(d);
        pr.Poly(pr.P("-RibIn", "-BrgDrop"), pr.P("-BrgOut", "-BrgDrop"), pr.P("-RibIn", "-PedRibY"));
        var prs = d.AddFeature(new SketchFeature { Name = "Pedestal rib", Plane = PlaneRef.Right, Sketch = pr.S });
        var pedRib = d.AddFeature(new ExtrudeFeature { Name = "Pedestal rib", SketchId = prs.Id, End = EndCondition.MidPlane, Depth = "RibT" });
        // filleted into the body's side and under the pedestal
        double SideZ(double yAt) => V("BodyW/2") - (yAt + V("BaseY-BaseT")) * Math.Tan(V("BodyDraft") * Math.PI / 180);
        double rt = V("RibT/2"), ry = -V("(BrgDrop+PedRibY)/2");
        var pedRibR = d.AddFeature(new FilletFeature
        {
            Name = "Pedestal rib fillets", Radius = "RibR",
            Edges =
            {
                E($"{pedRib.Id}.Start", YF(0), new Vec3(-rt, ry, SideZ(ry))), E($"{pedRib.Id}.End", YF(0), new Vec3(rt, ry, SideZ(ry))),
                E($"{pedRib.Id}.Start", $"{ped.Id}.S.L{pl[0]}", new Vec3(-rt, -V("BrgDrop"), V("BrgOut") - 6)),
                E($"{pedRib.Id}.End", $"{ped.Id}.S.L{pl[0]}", new Vec3(rt, -V("BrgDrop"), V("BrgOut") - 6)),
            },
        });
        d.AddFeature(new MirrorFeature { Name = "Pedestal rib, back", Seeds = { pedRib.Id, pedRibR.Id }, Plane = PlaneRef.Front });
        // ribs from the body's side out over the base, two each side
        var rp = d.AddFeature(new PlaneFeature { Name = "Base rib plane", Base = PlaneRef.Right, Offset = "-BaseRibX" });
        var br = new Sk(d);
        br.Poly(br.P("-RibIn", "-(BaseY-BaseT)"), br.P("-BaseRibOut", "-(BaseY-BaseT)"), br.P("-RibIn", "-(BaseY-BaseT-BaseRibH)"));
        var brs = d.AddFeature(new SketchFeature { Name = "Base rib", Plane = new PlaneRef { Feature = rp.Id }, Sketch = br.S });
        var baseRib = d.AddFeature(new ExtrudeFeature { Name = "Base rib", SketchId = brs.Id, End = EndCondition.MidPlane, Depth = "RibT-2" });
        var baseRibs = d.AddFeature(new LinearPatternFeature { Name = "Base ribs", Seeds = { baseRib.Id }, Direction = new DirRef { Axis = "X" }, Spacing = "BaseRibP", Count = "2" });
        d.AddFeature(new MirrorFeature { Name = "Base ribs, back", Seeds = { baseRib.Id, baseRibs.Id }, Plane = PlaneRef.Front });
        // the cylinder: the barrel from the head's face along +X, the flange the head bolts to
        var cf = d.AddFeature(new PlaneFeature { Name = "Cylinder face", Base = PlaneRef.Right, Offset = "-HeadX" });
        var c = new Sk(d);
        var barrelC = c.Circle(Sketch.OriginId, "BarrelD");
        var cs = d.AddFeature(new SketchFeature { Name = "Barrel", Plane = new PlaneRef { Feature = cf.Id }, Sketch = c.S });
        var barrel = d.AddFeature(new ExtrudeFeature { Name = "Barrel", SketchId = cs.Id, Depth = "HeadX-BarrelEnd" });
        var f = new Sk(d);
        f.Circle(Sketch.OriginId, "FlangeD");
        var fs = d.AddFeature(new SketchFeature { Name = "Flange", Plane = new PlaneRef { Feature = cf.Id }, Sketch = f.S });
        var flange = d.AddFeature(new ExtrudeFeature { Name = "Flange", SketchId = fs.Id, Depth = "FlangeT" });
        // the water hopper on the barrel, drafted, its corners rounded, cored out down past the bore
        var h = new Sk(d);
        var hl = h.Rect("-HopX0", "-HopW/2", "-HopX1", "HopW/2");
        var hs = d.AddFeature(new SketchFeature { Name = "Hopper", Plane = PlaneRef.Top, Sketch = h.S });
        var hop = d.AddFeature(new ExtrudeFeature { Name = "Hopper", SketchId = hs.Id, Depth = "HopH", Draft = "HopDraft" });
        string HF(int i) => $"{hop.Id}.S.L{hl[i]}";
        double hy = V("HopH/2");
        d.AddFeature(new FilletFeature
        {
            Name = "Hopper corners", Radius = "HopR",
            Edges =
            {
                E(HF(0), HF(1), new Vec3(-V("HopX1"), hy, V("HopW/2"))), E(HF(1), HF(2), new Vec3(-V("HopX1"), hy, -V("HopW/2"))),
                E(HF(2), HF(3), new Vec3(-V("HopX0"), hy, -V("HopW/2"))), E(HF(3), HF(0), new Vec3(-V("HopX0"), hy, V("HopW/2"))),
            },
        });
        // the top's edge rounded all round (one edge picked, the rest follow tangent): suppressed until the kernel
        // blends tangent neighbours (docs/hit-and-miss-notes.md)
        d.AddFeature(new FilletFeature
        {
            Name = "Hopper top edge", Radius = "HopTopR",
            Edges = { E(HF(0), $"{hop.Id}.End", new Vec3(-V("(HopX0+HopX1)/2"), V("HopH"), V("HopW/2-HopH*tan(HopDraft)"))) },
        });
        var ht = d.AddFeature(new PlaneFeature { Name = "Under the hopper's top", Base = PlaneRef.Top, Offset = "HopH-HopWall" });
        var w = new Sk(d);
        w.Rect("-(HopX0-HopWall)", "-(HopW/2-HopWall)", "-(HopX1+HopWall)", "HopW/2-HopWall");
        var ws = d.AddFeature(new SketchFeature { Name = "Water space", Plane = new PlaneRef { Feature = ht.Id }, Sketch = w.S });
        d.AddFeature(new ExtrudeFeature { Name = "Water space", SketchId = ws.Id, IsCut = true, Reverse = true, Depth = "HopH-HopWall+Jacket", Draft = "HopDraft" });
        var hf = d.AddFeature(new PlaneFeature { Name = "Hopper top", Base = PlaneRef.Top, Offset = "HopH" });
        var fo = new Sk(d);
        fo.Circle(fo.P("-(HopX0+HopX1)/2", "0"), "FillD");
        var fos = d.AddFeature(new SketchFeature { Name = "Filler", Plane = new PlaneRef { Feature = hf.Id }, Sketch = fo.S });
        d.AddFeature(new ExtrudeFeature { Name = "Filler", SketchId = fos.Id, IsCut = true, Reverse = true, Depth = "HopWall+1" });
        // the liner through the water, and the bore
        var l = new Sk(d);
        l.Circle(Sketch.OriginId, "LinerD");
        var ls = d.AddFeature(new SketchFeature { Name = "Liner", Plane = new PlaneRef { Feature = cf.Id }, Sketch = l.S });
        d.AddFeature(new ExtrudeFeature { Name = "Liner", SketchId = ls.Id, Depth = "HeadX-BarrelEnd" });
        var o = new Sk(d);
        o.Circle(Sketch.OriginId, "Bore");
        var os = d.AddFeature(new SketchFeature { Name = "Bore", Plane = new PlaneRef { Feature = cf.Id }, Sketch = o.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bore", SketchId = os.Id, IsCut = true, End = EndCondition.ThroughAll });
        // a relief at the bore's open end, where the rod swings past it
        var me = d.AddFeature(new PlaneFeature { Name = "Barrel end", Base = PlaneRef.Right, Offset = "-BarrelEnd" });
        var rl = new Sk(d);
        rl.Circle(Sketch.OriginId, "Relief");
        var rls = d.AddFeature(new SketchFeature { Name = "Mouth relief", Plane = new PlaneRef { Feature = me.Id }, Sketch = rl.S });
        d.AddFeature(new ExtrudeFeature { Name = "Mouth relief", SketchId = rls.Id, IsCut = true, Reverse = true, Depth = "ReliefL" });
        // the crank pit, through the body from above, open at the back
        var pt = d.AddFeature(new PlaneFeature { Name = "Pit top", Base = PlaneRef.Top, Offset = "BarrelD/2" });
        var k = new Sk(d);
        var kl = k.Rect("-BarrelEnd", "-PitW/2", "PitX1", "PitW/2");
        var ks = d.AddFeature(new SketchFeature { Name = "Crank pit", Plane = new PlaneRef { Feature = pt.Id }, Sketch = k.S });
        var pit = d.AddFeature(new ExtrudeFeature { Name = "Crank pit", SketchId = ks.Id, IsCut = true, Reverse = true, Depth = "BarrelD/2+PitBot" });
        // the walls' tops rounded either side of the pedestals: inside along the pit, outside along the body's sides
        // (Top: sketch y = -z, so the pit's first line is its +Z wall)
        double side = V("BodyW/2-(BaseY-BaseT)*tan(BodyDraft)"), pw = V("PitW/2");
        string Body(int i) => $"{body.Id}.S.L{yl[i]}";
        // (behind the pedestals the body's sides are its corner rounds already)
        d.AddFeature(new FilletFeature
        {
            Name = "Wall tops", Radius = "WallR",
            Edges =
            {
                E($"{pit.Id}.S.L{kl[0]}", $"{body.Id}.End", new Vec3(-50, 0, pw)), E($"{pit.Id}.S.L{kl[2]}", $"{body.Id}.End", new Vec3(-50, 0, -pw)),
                E(Body(0), $"{body.Id}.End", new Vec3(-43, 0, side)), E(Body(2), $"{body.Id}.End", new Vec3(-60, 0, -side)),
            },
        });
        // the main bearings' bore, the cap studs' tapped holes
        var j = new Sk(d);
        j.Circle(Sketch.OriginId, "J");
        var js = d.AddFeature(new SketchFeature { Name = "Main bearing bore", Plane = PlaneRef.Front, Sketch = j.S });
        d.AddFeature(new ExtrudeFeature { Name = "Main bearing bore", SketchId = js.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        var cp = new Sk(d);
        foreach (var (sx, sz) in new[] { ("-CapStud", "-(BrgIn+BrgOut)/2"), ("CapStud", "-(BrgIn+BrgOut)/2"), ("-CapStud", "(BrgIn+BrgOut)/2"), ("CapStud", "(BrgIn+BrgOut)/2") })
            cp.Point(cp.P(sx, sz));
        var cps = d.AddFeature(new SketchFeature { Name = "Cap studs", Plane = PlaneRef.Top, Sketch = cp.S });
        d.AddFeature(new HoleFeature { Name = "Cap studs", SketchId = cps.Id, Diameter = "Tap8", ThroughAll = false, Depth = "BrgDrop-8" });
        // the head's studs, drilled into the flange (the face's plane turned round: drilling along +X, sketch x = z)
        var cfo = d.AddFeature(new PlaneFeature { Name = "Cylinder face (out)", Base = PlaneRef.Right, Offset = "-HeadX", Flip = true });
        var hd = new Sk(d);
        hd.Point(hd.P("0", "StudPCD/2"));
        var hds = d.AddFeature(new SketchFeature { Name = "Head stud", Plane = new PlaneRef { Feature = cfo.Id }, Sketch = hd.S });
        var stud = d.AddFeature(new HoleFeature { Name = "Head stud", SketchId = hds.Id, Diameter = "Tap8", ThroughAll = false, Depth = "FlangeT+6" });
        d.AddFeature(new CircularPatternFeature { Name = "Head studs", Seeds = { stud.Id }, Axis = new DirRef { Axis = "X" }, Count = "NStud" });
        // the cam gear's stud boss on the +Z side, bored for the stud
        var ci = d.AddFeature(new PlaneFeature { Name = "Cam boss start", Base = PlaneRef.Front, Offset = "CamIn" });
        var cb = new Sk(d);
        var camC = cb.Circle(cb.P("-CamX", "0"), "CamD");
        var cbs = d.AddFeature(new SketchFeature { Name = "Cam boss", Plane = new PlaneRef { Feature = ci.Id }, Sketch = cb.S });
        var camBoss = d.AddFeature(new ExtrudeFeature { Name = "Cam boss", SketchId = cbs.Id, Depth = "GearZ-CamIn-1" });
        var ce = d.AddFeature(new PlaneFeature { Name = "Cam boss face", Base = PlaneRef.Front, Offset = "GearZ-1" });
        var ch = new Sk(d);
        ch.Point(ch.P("-CamX", "0"));
        var chs = d.AddFeature(new SketchFeature { Name = "Stud hole", Plane = new PlaneRef { Feature = ce.Id }, Sketch = ch.S });
        d.AddFeature(new HoleFeature { Name = "Stud hole", SketchId = chs.Id, Diameter = "Stud", ThroughAll = false, Depth = "GearZ-CamIn-6" });
        // the push rod's guide lugs on the +Z side (Right: sketch x = -z), a round end round the rod's hole
        var lp = d.AddFeature(new PlaneFeature { Name = "Lug plane", Base = PlaneRef.Right, Offset = "-LugX" });
        var g = new Sk(d);
        int g0 = g.P("-LugIn", "-8"), g1 = g.P("-PushZ", "-8"), g2 = g.P("-PushZ", "8"), g3 = g.P("-LugIn", "8"), gc = g.P("-PushZ", "0");
        g.Line(g0, g1); g.Arc(gc, g2, g1); g.Line(g2, g3); g.Line(g3, g0);
        g.Circle(gc, "PushD");
        var gs = d.AddFeature(new SketchFeature { Name = "Push rod lug", Plane = new PlaneRef { Feature = lp.Id }, Sketch = g.S });
        var lug = d.AddFeature(new ExtrudeFeature { Name = "Push rod lug", SketchId = gs.Id, End = EndCondition.MidPlane, Depth = "LugT" });
        d.AddFeature(new LinearPatternFeature { Name = "Push rod lugs", Seeds = { lug.Id }, Direction = new DirRef { Axis = "X" }, Spacing = "LugP", Count = "2" });
        // the oil boss on the barrel's open end, drilled into the bore
        var op = d.AddFeature(new PlaneFeature { Name = "Oil boss top", Base = PlaneRef.Top, Offset = "BarrelD/2+4" });
        var ob = new Sk(d);
        ob.Circle(ob.P("-OilX", "0"), "14");
        var obs = d.AddFeature(new SketchFeature { Name = "Oil boss", Plane = new PlaneRef { Feature = op.Id }, Sketch = ob.S });
        d.AddFeature(new ExtrudeFeature { Name = "Oil boss", SketchId = obs.Id, Reverse = true, Depth = "12" });
        var oh = new Sk(d);
        oh.Point(oh.P("-OilX", "0"));
        var ohs = d.AddFeature(new SketchFeature { Name = "Oil hole", Plane = new PlaneRef { Feature = op.Id }, Sketch = oh.S });
        d.AddFeature(new HoleFeature { Name = "Oil hole", SketchId = ohs.Id, Diameter = "Tap8", ThroughAll = false, Depth = "BarrelD/2+4-Bore/2+1" });
        // the base's hold-down holes
        var bh = new Sk(d);
        foreach (var (sx, sz) in new[] { ("-(BaseX0-8)", "-(BaseW/2-15)"), ("-(BaseX0-8)", "BaseW/2-15"), ("BaseX1-8", "-(BaseW/2-15)"), ("BaseX1-8", "BaseW/2-15") })
            bh.Point(bh.P(sx, sz));
        var bhs = d.AddFeature(new SketchFeature { Name = "Hold-down holes", Plane = new PlaneRef { Feature = bt.Id }, Sketch = bh.S });
        d.AddFeature(new HoleFeature { Name = "Hold-down holes", SketchId = bhs.Id, Diameter = "11", ThroughAll = true });
        // the joints blended, as cast: the hopper into the barrel before and behind it, the head's flange into the
        // barrel, the cam gear's boss into the body's side
        string barrelFace = $"{barrel.Id}.S.C{barrelC}";
        double up = V("BarrelD/2"), lean = V("BarrelD/2*tan(HopDraft)");
        d.AddFeature(new FilletFeature
        {
            Name = "Hopper into the barrel", Radius = "JointR",
            Edges = { E(HF(3), barrelFace, new Vec3(-V("HopX0") + lean, up, 0)), E(HF(1), barrelFace, new Vec3(-V("HopX1") - lean, up, 0)) },
        });
        d.AddFeature(new FilletFeature
        {
            Name = "Flange into the barrel", Radius = "JointR",
            Edges = { E($"{flange.Id}.End", barrelFace, new Vec3(-V("HeadX-FlangeT"), 0, -up)) },
        });
        // (suppressed: fails in the kernel, docs/hit-and-miss-notes.md)
        d.AddFeature(new FilletFeature
        {
            Name = "Cam boss into the body", Radius = "JointR", Suppressed = true,
            Edges = { E($"{camBoss.Id}.S.C{camC}", Body(0), new Vec3(-V("CamX"), -V("CamD/2"), SideZ(-V("CamD/2")))) },
        });
        d.Color = new[] { 0.72, 0.08, 0.07 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    /// <summary>
    /// A main bearing's cap, about the bearing's axis (Z) from z = 0 to its length, its split face on y = 0: flat
    /// shoulders for the studs' nuts either side of a round hump over the bore, a boss on top for the oil cup.
    /// </summary>
    static CadDocument HmBearingCap()
    {
        var d = Doc(("J", $"{HmJ}"), ("W", "64"), ("L", $"{HmBrgOut - HmBrgIn}"), ("T", "14"), ("Hump", "17"), ("Stud", "25"), ("StudD", "8.5"),
            ("CupD", "14"), ("CupH", "4"), ("Tap", "6.8"), ("C", "1"), ("R", "6"));
        var s = new Sk(d);
        int a = s.P("-W/2", "0"), b = s.P("W/2", "0"), c = s.P("W/2", "T"), e = s.P("Hump", "T"), g = s.P("-Hump", "T", dimX: false), h = s.P("-W/2", "T");
        int hc = s.P("0", "T");
        s.Line(a, b); var right = s.Line(b, c); s.Line(c, e); s.Arc(hc, e, g); s.Line(g, h); var left = s.Line(h, a);
        var sk = d.AddFeature(new SketchFeature { Name = "Cap", Plane = PlaneRef.Front, Sketch = s.S });
        var cap = d.AddFeature(new ExtrudeFeature { Name = "Cap", SketchId = sk.Id, Depth = "L" });
        // the outer end's corners rounded, as the pedestal's under it
        double V(string ex) => s.Eval(ex);
        d.AddFeature(new FilletFeature
        {
            Name = "Outer corners", Radius = "R",
            Edges =
            {
                E($"{cap.Id}.S.L{right}", $"{cap.Id}.End", new Vec3(V("W/2"), V("T/2"), V("L"))),
                E($"{cap.Id}.S.L{left}", $"{cap.Id}.End", new Vec3(-V("W/2"), V("T/2"), V("L"))),
            },
        });
        var j = new Sk(d);
        j.Circle(Sketch.OriginId, "J");
        var js = d.AddFeature(new SketchFeature { Name = "Bore", Plane = PlaneRef.Front, Sketch = j.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bore", SketchId = js.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        // the studs' holes down through the shoulders (Top: sketch y = -z)
        var tp = d.AddFeature(new PlaneFeature { Name = "Shoulders", Base = PlaneRef.Top, Offset = "T" });
        var st = new Sk(d);
        st.Point(st.P("-Stud", "-L/2")); st.Point(st.P("Stud", "-L/2"));
        var sts = d.AddFeature(new SketchFeature { Name = "Stud holes", Plane = new PlaneRef { Feature = tp.Id }, Sketch = st.S });
        d.AddFeature(new HoleFeature { Name = "Stud holes", SketchId = sts.Id, Diameter = "StudD", ThroughAll = true });
        // the oil cup's boss on the hump, tapped down into the bore
        var top = d.AddFeature(new PlaneFeature { Name = "Cup boss top", Base = PlaneRef.Top, Offset = "T+Hump+CupH" });
        var cb = new Sk(d);
        cb.Circle(cb.P("0", "-L/2"), "CupD");
        var cbs = d.AddFeature(new SketchFeature { Name = "Cup boss", Plane = new PlaneRef { Feature = top.Id }, Sketch = cb.S });
        d.AddFeature(new ExtrudeFeature { Name = "Cup boss", SketchId = cbs.Id, Reverse = true, Depth = "CupH+2" });
        var ch = new Sk(d);
        ch.Point(ch.P("0", "-L/2"));
        var chs = d.AddFeature(new SketchFeature { Name = "Oil hole", Plane = new PlaneRef { Feature = top.Id }, Sketch = ch.S });
        d.AddFeature(new HoleFeature { Name = "Oil hole", SketchId = chs.Id, Diameter = "Tap", ThroughAll = false, Depth = "T+Hump+CupH-J/2+1" });
        d.Color = new[] { 0.72, 0.08, 0.07 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    /// <summary>
    /// The cylinder head, in the engine's coordinates (its inner face on the cylinder's flange, x = HeadX, and
    /// HeadT thick towards -X): a round plate studded to the flange, the valves' seats (countersunk) and stems'
    /// holes, bosses on top (the exhaust) and on the -Z side (the intake) ported into the valves' pockets, guide
    /// bosses for the valve springs, the spark plug's hole, and cheeks for the rocker's pivot.
    /// </summary>
    static CadDocument HmHead()
    {
        var d = Doc(("HeadX", $"{-HmHeadX}"), ("T", $"{HmHeadT}"), ("D", "116"), ("PCD", "96"), ("NStud", "6"), ("StudD", "8.5"), ("SpotD", "15"),
            ("Vz", $"{HmValveZ}"), ("Stem", "6.2"), ("Seat", "22"), ("PortD", "18"), ("PortX", "210"), ("BossD", "28"), ("BossOut", "62"),
            ("GuideD", "14"), ("GuideH", "6"), ("PlugY", "18"), ("PlugD", "12.5"),
            ("PivX", "266"), ("PivZ", $"{(HmValveZ + HmPushZ) / 2}"), ("PinD", "8.2"), ("Gap", "5.5"), ("CheekT", "6"), ("CheekX", "278"), ("CheekZ0", "42"), ("CheekZ1", "62"),
            ("C", "1.5"));
        var inner = d.AddFeature(new PlaneFeature { Name = "Inner face", Base = PlaneRef.Right, Offset = "-HeadX" });
        var p = new Sk(d);
        var pc = p.Circle(Sketch.OriginId, "D");
        var ps = d.AddFeature(new SketchFeature { Name = "Head", Plane = new PlaneRef { Feature = inner.Id }, Sketch = p.S });
        var head = d.AddFeature(new ExtrudeFeature { Name = "Head", SketchId = ps.Id, Reverse = true, Depth = "T" });
        double V(string e) => p.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Outer edge", Distance = "C",
            Edges = { E($"{head.Id}.S.C{pc}", $"{head.Id}.End", new Vec3(-V("HeadX+T"), V("D/2"), 0)) },
        });
        // the exhaust boss on top and the intake boss on the -Z side, over the valves' pockets
        var et = d.AddFeature(new PlaneFeature { Name = "Exhaust boss top", Base = PlaneRef.Top, Offset = "BossOut" });
        var eb = new Sk(d);
        eb.Circle(eb.P("-PortX", "-Vz"), "BossD");
        var ebs = d.AddFeature(new SketchFeature { Name = "Exhaust boss", Plane = new PlaneRef { Feature = et.Id }, Sketch = eb.S });
        d.AddFeature(new ExtrudeFeature { Name = "Exhaust boss", SketchId = ebs.Id, Reverse = true, Depth = "BossOut-D/2+8" });
        var ib = d.AddFeature(new PlaneFeature { Name = "Intake boss face", Base = PlaneRef.Front, Offset = "-BossOut" });
        var isk = new Sk(d);
        isk.Circle(isk.P("-PortX", "0"), "BossD");
        var ibs = d.AddFeature(new SketchFeature { Name = "Intake boss", Plane = new PlaneRef { Feature = ib.Id }, Sketch = isk.S });
        d.AddFeature(new ExtrudeFeature { Name = "Intake boss", SketchId = ibs.Id, Depth = "BossOut-D/2+8" });
        // the valves' guide bosses on the outer face (Right: sketch x = -z)
        var outer = d.AddFeature(new PlaneFeature { Name = "Outer face", Base = PlaneRef.Right, Offset = "-(HeadX+T)" });
        var gb = new Sk(d);
        gb.Circle(gb.P("-Vz", "0"), "GuideD"); gb.Circle(gb.P("Vz", "0"), "GuideD");
        var gbs = d.AddFeature(new SketchFeature { Name = "Guide bosses", Plane = new PlaneRef { Feature = outer.Id }, Sketch = gb.S });
        d.AddFeature(new ExtrudeFeature { Name = "Guide bosses", SketchId = gbs.Id, Reverse = true, Depth = "GuideH" });
        // the rocker's cheeks either side of y = 0 (Top offset: sketch y = -z), its pivot bored through them
        var cp = d.AddFeature(new PlaneFeature { Name = "Cheek face", Base = PlaneRef.Top, Offset = "Gap" });
        var ck = new Sk(d);
        int k0 = ck.P("-CheekX", "-CheekZ1"), k1 = ck.P("-(HeadX+T-1)", "-CheekZ1"), k2 = ck.P("-(HeadX+T-1)", "-CheekZ0"), k3 = ck.P("-CheekX", "-CheekZ0");
        ck.Poly(k0, k1, k2, k3);
        var cks = d.AddFeature(new SketchFeature { Name = "Rocker cheek", Plane = new PlaneRef { Feature = cp.Id }, Sketch = ck.S });
        var cheek = d.AddFeature(new ExtrudeFeature { Name = "Rocker cheek", SketchId = cks.Id, Depth = "CheekT" });
        d.AddFeature(new MirrorFeature { Name = "Rocker cheek, below", Seeds = { cheek.Id }, Plane = PlaneRef.Top });
        var pv = new Sk(d);
        pv.Circle(pv.P("-PivX", "-PivZ"), "PinD");
        var pvs = d.AddFeature(new SketchFeature { Name = "Pivot", Plane = PlaneRef.Top, Sketch = pv.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pivot", SketchId = pvs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        // the valves: seats countersunk into the inner face, the stems' holes on through (drilled -X)
        var vs = new Sk(d);
        vs.Point(vs.P("-Vz", "0")); vs.Point(vs.P("Vz", "0"));
        var vss = d.AddFeature(new SketchFeature { Name = "Valve seats", Plane = new PlaneRef { Feature = inner.Id }, Sketch = vs.S });
        d.AddFeature(new HoleFeature { Name = "Valve seats", SketchId = vss.Id, Type = HoleType.Countersink, Diameter = "Stem", CsDiameter = "Seat", CsAngle = "90", ThroughAll = true });
        // the ports: down from the exhaust boss, in from the intake boss, into the valves' pockets
        var ep = new Sk(d);
        ep.Point(ep.P("-PortX", "-Vz"));
        var eps = d.AddFeature(new SketchFeature { Name = "Exhaust port", Plane = new PlaneRef { Feature = et.Id }, Sketch = ep.S });
        d.AddFeature(new HoleFeature { Name = "Exhaust port", SketchId = eps.Id, Diameter = "PortD", ThroughAll = false, Depth = "BossOut", DrillPoint = false });
        var ipp = d.AddFeature(new PlaneFeature { Name = "Intake boss face (in)", Base = PlaneRef.Front, Offset = "-BossOut", Flip = true });
        var ip = new Sk(d);
        // the turned plane: sketch x = -x
        ip.Point(ip.P("PortX", "0"));
        var ips = d.AddFeature(new SketchFeature { Name = "Intake port", Plane = new PlaneRef { Feature = ipp.Id }, Sketch = ip.S });
        d.AddFeature(new HoleFeature { Name = "Intake port", SketchId = ips.Id, Diameter = "PortD", ThroughAll = false, Depth = "BossOut-Vz", DrillPoint = false });
        // the studs' holes, spot faced on the outer face, drilled along +X (the outer face turned round: sketch x = z)
        var ot = d.AddFeature(new PlaneFeature { Name = "Outer face (in)", Base = PlaneRef.Right, Offset = "-(HeadX+T)", Flip = true });
        var sh = new Sk(d);
        sh.Point(sh.P("0", "-PCD/2"));
        var shs = d.AddFeature(new SketchFeature { Name = "Stud hole", Plane = new PlaneRef { Feature = ot.Id }, Sketch = sh.S });
        var stud = d.AddFeature(new HoleFeature { Name = "Stud hole", SketchId = shs.Id, Type = HoleType.Counterbore, Diameter = "StudD", CbDiameter = "SpotD", CbDepth = "1", ThroughAll = true });
        d.AddFeature(new CircularPatternFeature { Name = "Stud holes", Seeds = { stud.Id }, Axis = new DirRef { Axis = "X" }, Count = "NStud" });
        // the spark plug's hole, below the valves
        var pl = new Sk(d);
        pl.Point(pl.P("0", "-PlugY"));
        var pls = d.AddFeature(new SketchFeature { Name = "Plug hole", Plane = new PlaneRef { Feature = ot.Id }, Sketch = pl.S });
        d.AddFeature(new HoleFeature { Name = "Plug hole", SketchId = pls.Id, Diameter = "PlugD", ThroughAll = true });
        d.Color = new[] { 0.72, 0.08, 0.07 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // the gears and the valve train

    /// <summary>The spur gear (<see cref="Gear"/>) of z teeth on a bore, brass, its web recesses and lightening holes only where they fit.</summary>
    static CadDocument HmGear(double z, double bore)
    {
        var d = ConfigGear();
        foreach (var (n, e) in new[] { ("m", $"{HmModule}"), ("z", $"{z}"), ("b", $"{HmGearB}"), ("Bore", $"{bore}"), ("KeyW", bore > 20 ? "6" : "5"), ("KeyT", bore > 20 ? "3" : "2.3") })
            d.Equations.First(q => q.Name == n).Expr = e;
        foreach (var q in d.Equations) q.Exposed = false;
        d.Color = new[] { 0.85, 0.66, 0.28 };
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>The cam gear: twice the crank gear's teeth, free on its stud, the exhaust's eccentric on its outer face.</summary>
    static CadDocument HmCamGear()
    {
        var d = HmGear(HmZCam, 16);
        d.Equations.Add(new Equation { Name = "Ecc", Expr = $"{HmEcc}" });
        d.Equations.Add(new Equation { Name = "EccD", Expr = $"{HmEccD}" });
        d.Equations.Add(new Equation { Name = "EccT", Expr = $"{HmEccT}" });
        var face = d.AddFeature(new PlaneFeature { Name = "Outer face", Base = PlaneRef.Front, Offset = "b" });
        var s = new Sk(d);
        s.Circle(s.P("Ecc", "0"), "EccD");
        var ss = d.AddFeature(new SketchFeature { Name = "Eccentric", Plane = new PlaneRef { Feature = face.Id }, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Eccentric", SketchId = ss.Id, Depth = "EccT" });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Bore");
        var hs = d.AddFeature(new SketchFeature { Name = "Bore through", Plane = new PlaneRef { Feature = face.Id }, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bore through", SketchId = hs.Id, IsCut = true, Depth = "EccT" });
        return d;
    }

    /// <summary>The cam gear's stud: pressed into the casting's boss, a collar holding the gear on, along Z from z = 0.</summary>
    static CadDocument HmCamStud()
    {
        var d = Doc(("D", "16"), ("In", "26"), ("Out", $"{HmGearB + HmEccT + 1}"), ("CollarD", "24"), ("CollarT", "4"), ("C", "0.8"));
        var s = new Sk(d);
        var c = s.Circle(Sketch.OriginId, "D");
        var ss = d.AddFeature(new SketchFeature { Name = "Stud", Plane = PlaneRef.Front, Sketch = s.S });
        var stud = d.AddFeature(new ExtrudeFeature { Name = "Stud", SketchId = ss.Id, Depth = "Out", Depth2 = "In" });
        var top = d.AddFeature(new PlaneFeature { Name = "Collar face", Base = PlaneRef.Front, Offset = "Out" });
        var k = new Sk(d);
        k.Circle(Sketch.OriginId, "CollarD");
        var ks = d.AddFeature(new SketchFeature { Name = "Collar", Plane = new PlaneRef { Feature = top.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Collar", SketchId = ks.Id, Depth = "CollarT" });
        _ = c; _ = stud;
        d.Color = new[] { 0.66, 0.67, 0.70 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>The push rod along X from x = 0 to Len: plain, its ends faced square for the eccentric and the rocker.</summary>
    static CadDocument HmPushRod()
    {
        double len = -(HmHeadX - HmHeadT - 29) + (HmCamX + HmEcc - HmEccD / 2);
        var d = Doc(("D", "8"), ("Len", $"{len:0.###}"), ("C", "0.5"));
        var s = new Sk(d);
        var c = s.Circle(Sketch.OriginId, "D");
        var ss = d.AddFeature(new SketchFeature { Name = "Rod", Plane = PlaneRef.Right, Sketch = s.S });
        var rod = d.AddFeature(new ExtrudeFeature { Name = "Rod", SketchId = ss.Id, Depth = "Len" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new ChamferFeature
        {
            Name = "Ends", Distance = "C",
            Edges = { E($"{rod.Id}.S.C{c}", $"{rod.Id}.Start", new Vec3(0, V("D/2"), 0)), E($"{rod.Id}.S.C{c}", $"{rod.Id}.End", new Vec3(V("Len"), V("D/2"), 0)) },
        });
        d.Color = new[] { 0.80, 0.81, 0.83 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// The rocker on its pivot (the origin, along Y): an arm along Z, a round tip at each end towards +X (the valve's
    /// stem at -Z, the push rod at +Z), a boss round the pivot.
    /// </summary>
    static CadDocument HmRocker()
    {
        var d = Doc(("Arm", $"{(HmPushZ - HmValveZ) / 2}"), ("T", "10"), ("Back", "8"), ("Tip", "4"), ("TipX", "2"), ("BossD", "16"), ("PinD", "8.2"));
        // Top: sketch y = -z
        var s = new Sk(d);
        s.Rect("-Back", "-Arm", "TipX", "Arm");
        var ss = d.AddFeature(new SketchFeature { Name = "Arm", Plane = PlaneRef.Top, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Arm", SketchId = ss.Id, End = EndCondition.MidPlane, Depth = "T" });
        var t = new Sk(d);
        t.Circle(t.P("TipX", "Arm"), "2*Tip"); t.Circle(t.P("TipX", "-Arm"), "2*Tip");
        var ts = d.AddFeature(new SketchFeature { Name = "Tips", Plane = PlaneRef.Top, Sketch = t.S });
        d.AddFeature(new ExtrudeFeature { Name = "Tips", SketchId = ts.Id, End = EndCondition.MidPlane, Depth = "T" });
        var o = new Sk(d);
        o.Circle(Sketch.OriginId, "BossD");
        var os = d.AddFeature(new SketchFeature { Name = "Boss", Plane = PlaneRef.Top, Sketch = o.S });
        d.AddFeature(new ExtrudeFeature { Name = "Boss", SketchId = os.Id, End = EndCondition.MidPlane, Depth = "T" });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "PinD");
        var hs = d.AddFeature(new SketchFeature { Name = "Pivot", Plane = PlaneRef.Top, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pivot", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = new[] { 0.72, 0.08, 0.07 };
        HmMaterial(d, "Cast iron");
        return d;
    }

    /// <summary>A headed pin along Y from y = 0 (under the head) to -Len.</summary>
    static CadDocument HmPivotPin()
    {
        var d = Doc(("D", "8"), ("Len", "26"), ("HeadD", "12"), ("HeadT", "3"));
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "D");
        var ss = d.AddFeature(new SketchFeature { Name = "Pin", Plane = PlaneRef.Top, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pin", SketchId = ss.Id, Reverse = true, Depth = "Len" });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "HeadD");
        var hs = d.AddFeature(new SketchFeature { Name = "Head", Plane = PlaneRef.Top, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Head", SketchId = hs.Id, Depth = "HeadT" });
        d.Color = new[] { 0.80, 0.81, 0.83 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// A poppet valve along X: its face on x = 0 looking +X, the 45° seat behind it, the stem to -Len with a groove
    /// for the spring's retainer near its end.
    /// </summary>
    static CadDocument HmValve()
    {
        var d = Doc(("Head", "20"), ("Margin", "1"), ("Stem", "6"), ("Len", "65"), ("Groove", "4"), ("Gw", "1.2"), ("Gd", "0.6"));
        var s = new Sk(d);
        var L = s.Poly(Sketch.OriginId, s.P("0", "Head/2"), s.P("-Margin", "Head/2"), s.P("-(Margin+Head/2-Stem/2)", "Stem/2"),
            s.P("-(Len-Groove-Gw)", "Stem/2"), s.P("-(Len-Groove-Gw)", "Stem/2-Gd"), s.P("-(Len-Groove)", "Stem/2-Gd"), s.P("-(Len-Groove)", "Stem/2"),
            s.P("-Len", "Stem/2"), s.P("-Len", "0"));
        var sk = d.AddFeature(new SketchFeature { Name = "Section", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new RevolveFeature { Name = "Valve", SketchId = sk.Id, AxisEntity = L[^1], Angle = "360" });
        d.Color = new[] { 0.62, 0.63, 0.66 };
        HmMaterial(d, "Stainless steel");
        return d;
    }

    /// <summary>A valve spring: a helix of round wire along -X from x = 0, Len long.</summary>
    static CadDocument HmValveSpring()
    {
        var d = Doc(("Coil", "12"), ("Wire", "1.6"), ("Len", "16"), ("N", "5"));
        var s = new Sk(d);
        s.Circle(s.P("-Wire/2", "Coil/2"), "Wire");
        s.Centerline(Sketch.OriginId, s.P("-Len", "0"));
        var sk = d.AddFeature(new SketchFeature { Name = "Wire", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new HelicalSweepFeature { Name = "Coils", SketchId = sk.Id, Pitch = "(Len-Wire)/N", Revolutions = "N" });
        d.Color = new[] { 0.25, 0.25, 0.27 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>The spring's retainer: a washer along X from x = 0 to -T.</summary>
    static CadDocument HmRetainer()
    {
        var d = Doc(("D", "15"), ("Hole", "6.2"), ("T", "3"));
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "D"); s.Circle(Sketch.OriginId, "Hole");
        var sk = d.AddFeature(new SketchFeature { Name = "Washer", Plane = PlaneRef.Right, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Washer", SketchId = sk.Id, Reverse = true, Depth = "T" });
        d.Color = new[] { 0.80, 0.81, 0.83 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // fittings

    static readonly double[] HmBrass = { 0.85, 0.66, 0.28 }, HmBlack = { 0.13, 0.13, 0.14 }, HmSteel = { 0.80, 0.81, 0.83 }, HmRed = { 0.72, 0.08, 0.07 };

    /// <summary>A revolved part about Y: the half section through the points (x the radius, y up), closed on the axis.</summary>
    static (CadDocument d, Feature rev, List<int> lines) HmTurned(string name, (string name, string expr)[] eqs, params (string r, string y)[] pts)
    {
        var d = Doc(eqs);
        var s = new Sk(d);
        var ids = new List<int> { s.P("0", pts[0].y) };
        ids.AddRange(pts.Select(p => s.P(p.r, p.y)));
        ids.Add(s.P("0", pts[^1].y));
        var L = s.Poly(ids.ToArray());
        var sk = d.AddFeature(new SketchFeature { Name = name + " section", Plane = PlaneRef.Front, Sketch = s.S });
        var rev = d.AddFeature(new RevolveFeature { Name = name, SketchId = sk.Id, AxisEntity = L[^1], Angle = "360" });
        return (d, rev, L);
    }

    /// <summary>A revolved ring about Y: the closed section through the points (x the radius, y up), clear of the axis.</summary>
    static (CadDocument d, Feature rev, List<int> lines) HmRing(string name, (string name, string expr)[] eqs, params (string r, string y)[] pts)
    {
        var d = Doc(eqs);
        var s = new Sk(d);
        var L = s.Poly(pts.Select(p => s.P(p.r, p.y)).ToArray());
        s.Centerline(Sketch.OriginId, s.P("0", "10"));
        var sk = d.AddFeature(new SketchFeature { Name = name + " section", Plane = PlaneRef.Front, Sketch = s.S });
        var rev = d.AddFeature(new RevolveFeature { Name = name, SketchId = sk.Id, Angle = "360" });
        return (d, rev, L);
    }

    /// <summary>The hopper's filler: a funnel flaring up from a spigot in the hopper's opening (from y = 0 on the hopper's top).</summary>
    static CadDocument HmFunnel()
    {
        var (d, _, _) = HmRing("Funnel", new[] { ("Spig", "21.8"), ("In", "19"), ("Foot", "27"), ("Top", "40"), ("H", "40"), ("T", "3"), ("SpigL", "8") },
            ("In", "-SpigL"), ("Spig", "-SpigL"), ("Spig", "0"), ("Foot", "0"), ("Foot", "4"), ("Top", "H"), ("Top-T", "H"), ("In", "6"));
        d.Color = HmRed;
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>
    /// The exhaust: a spigot into the head's port, a flange on its boss, the pipe up and the muffler on it (a double
    /// cone with a band at its widest, blended), its outlet bored down from the top.
    /// </summary>
    static CadDocument HmMuffler()
    {
        var (d, rev, L) = HmTurned("Muffler", new[] { ("Spig", "9"), ("SpigL", "10"), ("Fl", "16"), ("FlT", "5"), ("Pipe", "10"), ("PipeH", "75"),
                ("Neck", "16"), ("Big", "38"), ("Lo", "125"), ("Band", "6"), ("Top", "17"), ("Hi", "170"), ("H", "190"), ("Out", "16"), ("Rb", "10") },
            ("Spig", "-SpigL"), ("Spig", "0"), ("Fl", "0"), ("Fl", "FlT"), ("Pipe", "FlT"), ("Pipe", "PipeH"), ("Neck", "PipeH"), ("Neck", "PipeH+8"),
            ("Big", "Lo"), ("Big", "Lo+Band"), ("Top", "Hi"), ("Top", "H"));
        string F(int i) => $"{rev.Id}.R.L{L[i]}";
        // the double cone blended into its band and the neck (lines: 0 the axis to the spigot, 8 the lower cone, 9 the band, 10 the upper cone)
        d.AddFeature(new FilletFeature
        {
            Name = "Band", Radius = "Rb",
            Edges = { E(F(8), F(9), new Vec3(38, 125, 0)), E(F(9), F(10), new Vec3(38, 131, 0)) },
        });
        d.AddFeature(new FilletFeature { Name = "Neck", Radius = "4", Edges = { E(F(7), F(8), new Vec3(16, 83, 0)), E(F(10), F(11), new Vec3(17, 170, 0)) } });
        var top = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Top, Offset = "H" });
        var o = new Sk(d);
        o.Point(Sketch.OriginId);
        var os = d.AddFeature(new SketchFeature { Name = "Outlet", Plane = new PlaneRef { Feature = top.Id }, Sketch = o.S });
        d.AddFeature(new HoleFeature { Name = "Outlet", SketchId = os.Id, Diameter = "Out", ThroughAll = false, Depth = "30" });
        d.Color = HmBlack;
        HmMaterial(d, "Cast iron");
        return d;
    }

    /// <summary>The muffler's brass top ring, from y = 0.</summary>
    static CadDocument HmMufflerCap()
    {
        var (d, rev, L) = HmRing("Ring", new[] { ("In", "8"), ("Out", "18.5"), ("H", "10"), ("Lip", "17"), ("C", "1.5") },
            ("In", "0"), ("Out", "0"), ("Out", "H-3"), ("Lip", "H"), ("In", "H"));
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>
    /// The mixer (carburettor), brass, along Z from the head's intake boss (z = 0) out: a spigot into the port, a
    /// flange, the body, the air horn standing up with its bell, the fuel nipple below and the needle valve's bore.
    /// </summary>
    static CadDocument HmMixer()
    {
        var d = Doc(("Spig", "18"), ("SpigL", "10"), ("Fl", "28"), ("FlT", "4"), ("Body", "22"), ("L", "40"), ("HornZ", "28"), ("Horn", "18"), ("HornH", "30"),
            ("Bell", "26"), ("BellH", "8"), ("Air", "12"), ("NipZ", "16"), ("Nip", "8"), ("NipL", "24"), ("Fuel", "4"), ("Needle", "6"), ("C", "1"));
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "Spig");
        var ss = d.AddFeature(new SketchFeature { Name = "Spigot", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Spigot", SketchId = ss.Id, Reverse = true, Depth = "SpigL" });
        var f = new Sk(d);
        f.Circle(Sketch.OriginId, "Fl");
        var fs = d.AddFeature(new SketchFeature { Name = "Flange", Plane = PlaneRef.Front, Sketch = f.S });
        d.AddFeature(new ExtrudeFeature { Name = "Flange", SketchId = fs.Id, Depth = "FlT" });
        var b = new Sk(d);
        var bc = b.Circle(Sketch.OriginId, "Body");
        var bs = d.AddFeature(new SketchFeature { Name = "Body", Plane = PlaneRef.Front, Sketch = b.S });
        var body = d.AddFeature(new ExtrudeFeature { Name = "Body", SketchId = bs.Id, Depth = "L" });
        // the air horn up from the body's axis (Top: sketch y = -z), its bell, bored down into the body
        var h = new Sk(d);
        h.Circle(h.P("0", "-HornZ"), "Horn");
        var hs = d.AddFeature(new SketchFeature { Name = "Air horn", Plane = PlaneRef.Top, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Air horn", SketchId = hs.Id, Depth = "HornH" });
        var bp = d.AddFeature(new PlaneFeature { Name = "Bell base", Base = PlaneRef.Top, Offset = "HornH" });
        var be = new Sk(d);
        be.Circle(be.P("0", "-HornZ"), "Bell");
        var bes = d.AddFeature(new SketchFeature { Name = "Bell", Plane = new PlaneRef { Feature = bp.Id }, Sketch = be.S });
        d.AddFeature(new ExtrudeFeature { Name = "Bell", SketchId = bes.Id, Depth = "BellH", Draft = "20", DraftOutward = true });
        var at = d.AddFeature(new PlaneFeature { Name = "Bell top", Base = PlaneRef.Top, Offset = "HornH+BellH" });
        var ah = new Sk(d);
        ah.Point(ah.P("0", "-HornZ"));
        var ahs = d.AddFeature(new SketchFeature { Name = "Air", Plane = new PlaneRef { Feature = at.Id }, Sketch = ah.S });
        d.AddFeature(new HoleFeature { Name = "Air", SketchId = ahs.Id, Diameter = "Air", ThroughAll = false, Depth = "HornH+BellH" });
        // the fuel nipple below, bored
        var n = new Sk(d);
        n.Circle(n.P("0", "-NipZ"), "Nip");
        var ns = d.AddFeature(new SketchFeature { Name = "Nipple", Plane = PlaneRef.Top, Sketch = n.S });
        d.AddFeature(new ExtrudeFeature { Name = "Nipple", SketchId = ns.Id, Reverse = true, Depth = "NipL" });
        // the needle valve's bore in the body's end
        var ep = d.AddFeature(new PlaneFeature { Name = "Body end", Base = PlaneRef.Front, Offset = "L" });
        var nv = new Sk(d);
        nv.Point(Sketch.OriginId);
        var nvs = d.AddFeature(new SketchFeature { Name = "Needle bore", Plane = new PlaneRef { Feature = ep.Id }, Sketch = nv.S });
        d.AddFeature(new HoleFeature { Name = "Needle bore", SketchId = nvs.Id, Diameter = "Needle", ThroughAll = false, Depth = "L/2" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new ChamferFeature { Name = "Body end", Distance = "C", Edges = { E($"{body.Id}.S.C{bc}", $"{body.Id}.End", new Vec3(0, -V("Body/2"), V("L"))) } });
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>The needle valve: a stem into the mixer (along -Z from z = 0) and a knurled knob (a groove patterned round it).</summary>
    static CadDocument HmNeedleValve()
    {
        var d = Doc(("Stem", "6"), ("StemL", "14"), ("Knob", "28"), ("KnobT", "8"), ("Neck", "10"), ("NeckL", "4"), ("Knurl", "2"), ("N", "30"));
        var s = new Sk(d);
        s.Circle(Sketch.OriginId, "Stem");
        var ss = d.AddFeature(new SketchFeature { Name = "Stem", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Stem", SketchId = ss.Id, Reverse = true, Depth = "StemL" });
        var n = new Sk(d);
        n.Circle(Sketch.OriginId, "Neck");
        var ns = d.AddFeature(new SketchFeature { Name = "Neck", Plane = PlaneRef.Front, Sketch = n.S });
        d.AddFeature(new ExtrudeFeature { Name = "Neck", SketchId = ns.Id, Depth = "NeckL" });
        var kp = d.AddFeature(new PlaneFeature { Name = "Knob face", Base = PlaneRef.Front, Offset = "NeckL" });
        var k = new Sk(d);
        k.Circle(Sketch.OriginId, "Knob");
        var ks = d.AddFeature(new SketchFeature { Name = "Knob", Plane = new PlaneRef { Feature = kp.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Knob", SketchId = ks.Id, Depth = "KnobT" });
        var g = new Sk(d);
        g.Circle(g.P("Knob/2", "0"), "Knurl");
        var gs = d.AddFeature(new SketchFeature { Name = "Knurl", Plane = new PlaneRef { Feature = kp.Id }, Sketch = g.S });
        var groove = d.AddFeature(new ExtrudeFeature { Name = "Knurl", SketchId = gs.Id, IsCut = true, Depth = "KnobT" });
        d.AddFeature(new CircularPatternFeature { Name = "Knurling", Seeds = { groove.Id }, Axis = new DirRef { Axis = "Z" }, Count = "N" });
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>
    /// The fuel line, in the engine's coordinates: a tube swept from the mixer's nipple down beside the casting and
    /// along to the tank in the base, on the plane z = Z (its section on a plane across the start).
    /// </summary>
    static CadDocument HmFuelLine(Vec3 start)
    {
        var d = Doc(("X0", $"{-start.X}"), ("Y0", $"{-start.Y}"), ("Z", $"{-start.Z}"), ("OD", "7"), ("ID", "4"), ("R", "15"),
            ("Drop", "60"), ("Run", "70"), ("Y1", "130"));
        var sp = d.AddFeature(new PlaneFeature { Name = "Start", Base = PlaneRef.Top, Offset = "-Y0" });
        var p = new Sk(d);
        // Top: sketch y = -z
        p.Circle(p.P("-X0", "Z"), "OD"); p.Circle(p.P("-X0", "Z"), "ID");
        var ps = d.AddFeature(new SketchFeature { Name = "Tube section", Plane = new PlaneRef { Feature = sp.Id }, Sketch = p.S });
        // the path on the plane z = -Z (Front offset: sketch x = x, y = y): down, a bend, along +X, a bend down into the base
        var pp = d.AddFeature(new PlaneFeature { Name = "Path plane", Base = PlaneRef.Front, Offset = "-Z" });
        var w = new Sk(d);
        int a = w.P("-X0", "-Y0"), b = w.P("-X0", "-(Y0+Drop)"), c = w.P("-(X0-R)", "-(Y0+Drop)"), e = w.P("-(X0-R)", "-(Y0+Drop+R)"),
            f = w.P("-(X0-R-Run)", "-(Y0+Drop+R)"), g = w.P("-(X0-R-Run)", "-(Y0+Drop+2*R)"), h = w.P("-(X0-2*R-Run)", "-(Y0+Drop+2*R)"), k = w.P("-(X0-2*R-Run)", "-Y1");
        w.Line(a, b); w.Arc(c, b, e); w.Line(e, f); w.Arc(g, h, f); w.Line(h, k);
        var ws = d.AddFeature(new SketchFeature { Name = "Route", Plane = new PlaneRef { Feature = pp.Id }, Sketch = w.S });
        d.AddFeature(new SweepFeature { Name = "Fuel line", ProfileSketchId = ps.Id, PathSketchId = ws.Id });
        d.Color = HmRed;
        HmMaterial(d, "Copper");
        return d;
    }

    /// <summary>The spark plug along X: its thread into the head (0..+Thread), the hex seated on the head, the insulator and terminal out along -X.</summary>
    static CadDocument HmSparkPlug()
    {
        var d = Doc(("Thread", "12.5"), ("ThreadL", "12"), ("S", "21"), ("HexT", "9"), ("Ins", "12"), ("InsL", "26"), ("Term", "5"), ("TermL", "8"));
        var t = new Sk(d);
        t.Circle(Sketch.OriginId, "Thread");
        var ts = d.AddFeature(new SketchFeature { Name = "Thread", Plane = PlaneRef.Right, Sketch = t.S });
        d.AddFeature(new ExtrudeFeature { Name = "Thread", SketchId = ts.Id, Depth = "ThreadL" });
        var h = new Sk(d);
        h.Poly(h.P("S/2", "S/(2*sqrt(3))"), h.P("0", "S/sqrt(3)"), h.P("-S/2", "S/(2*sqrt(3))"), h.P("-S/2", "-S/(2*sqrt(3))"), h.P("0", "-S/sqrt(3)"), h.P("S/2", "-S/(2*sqrt(3))"));
        var hs = d.AddFeature(new SketchFeature { Name = "Hex", Plane = PlaneRef.Right, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Hex", SketchId = hs.Id, Reverse = true, Depth = "HexT" });
        var ip = d.AddFeature(new PlaneFeature { Name = "Hex top", Base = PlaneRef.Right, Offset = "-HexT" });
        var i = new Sk(d);
        i.Circle(Sketch.OriginId, "Ins");
        var iss = d.AddFeature(new SketchFeature { Name = "Insulator", Plane = new PlaneRef { Feature = ip.Id }, Sketch = i.S });
        d.AddFeature(new ExtrudeFeature { Name = "Insulator", SketchId = iss.Id, Reverse = true, Depth = "InsL", Draft = "3" });
        var tp = d.AddFeature(new PlaneFeature { Name = "Insulator top", Base = PlaneRef.Right, Offset = "-(HexT+InsL)" });
        var e = new Sk(d);
        e.Circle(Sketch.OriginId, "Term");
        var es = d.AddFeature(new SketchFeature { Name = "Terminal", Plane = new PlaneRef { Feature = tp.Id }, Sketch = e.S });
        d.AddFeature(new ExtrudeFeature { Name = "Terminal", SketchId = es.Id, Reverse = true, Depth = "TermL" });
        d.Color = new[] { 0.90, 0.90, 0.88 };
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>The plug's boot: a rubber cap over the insulator (along -X from x = 0), its lead swept off at right angles.</summary>
    static CadDocument HmPlugBoot()
    {
        var d = Doc(("D", "18"), ("L", "26"), ("Lead", "7"), ("R", "14"), ("Run", "30"));
        var s = new Sk(d);
        var c = s.Circle(Sketch.OriginId, "D");
        var ss = d.AddFeature(new SketchFeature { Name = "Boot", Plane = PlaneRef.Right, Sketch = s.S });
        var boot = d.AddFeature(new ExtrudeFeature { Name = "Boot", SketchId = ss.Id, Reverse = true, Depth = "L" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new FilletFeature { Name = "End", Radius = "4", Edges = { E($"{boot.Id}.S.C{c}", $"{boot.Id}.End", new Vec3(-V("L"), V("D/2"), 0)) } });
        // open at the plug's end, over the insulator and terminal
        var o = new Sk(d);
        o.Point(Sketch.OriginId);
        var os = d.AddFeature(new SketchFeature { Name = "Socket", Plane = PlaneRef.Right, Sketch = o.S });
        d.AddFeature(new HoleFeature { Name = "Socket", SketchId = os.Id, Diameter = "13", ThroughAll = false, Depth = "L-3" });
        // the lead: from the boot's end along -X, a bend, then down (-Y)
        var lp = d.AddFeature(new PlaneFeature { Name = "Boot end", Base = PlaneRef.Right, Offset = "-(L-2)" });
        var l = new Sk(d);
        l.Circle(Sketch.OriginId, "Lead");
        var ls = d.AddFeature(new SketchFeature { Name = "Lead section", Plane = new PlaneRef { Feature = lp.Id }, Sketch = l.S });
        var w = new Sk(d);
        int a = w.P("-(L-2)", "0"), b = w.P("-(L+6)", "0"), cc = w.P("-(L+6)", "-R"), e = w.P("-(L+6+R)", "-R"), f = w.P("-(L+6+R)", "-(R+Run)");
        w.Line(a, b); w.Arc(cc, b, e); w.Line(e, f);
        var ws = d.AddFeature(new SketchFeature { Name = "Lead route", Plane = PlaneRef.Front, Sketch = w.S });
        d.AddFeature(new SweepFeature { Name = "Lead", ProfileSketchId = ls.Id, PathSketchId = ws.Id });
        d.Color = HmBlack;
        HmMaterial(d, "Rubber");
        return d;
    }

    /// <summary>An oil cup, brass, up from y = 0: its thread into the tapped hole, a hex, the cup, a domed lid.</summary>
    static CadDocument HmOilCup()
    {
        var (d, rev, L) = HmTurned("Oil cup", new[] { ("Tap", "6.8"), ("TapL", "6"), ("Hex", "8"), ("HexH", "5"), ("Cup", "7"), ("CupH", "20"), ("Lid", "8.5"), ("LidH", "6") },
            ("Tap/2", "-TapL"), ("Tap/2", "0"), ("Hex", "0"), ("Hex", "HexH"), ("Cup", "HexH"), ("Cup", "HexH+CupH"), ("Lid", "HexH+CupH"), ("Lid", "HexH+CupH+LidH"));
        d.AddFeature(new FilletFeature { Name = "Lid", Radius = "3", Edges = { E($"{rev.Id}.R.L{L[7]}", $"{rev.Id}.R.L{L[8]}", new Vec3(8.5, 31, 0)) } });
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>The cylinder's drip oiler, brass, up from y = 0: thread, base, a post through the glass, the top cap and its knob.</summary>
    static CadDocument HmOiler()
    {
        var (d, _, _) = HmTurned("Oiler", new[] { ("Tap", "6.8"), ("TapL", "6"), ("Base", "9"), ("BaseH", "8"), ("Post", "3"), ("Glass", "22"), ("Top", "11.5"), ("TopH", "6"), ("Knob", "4"), ("KnobH", "8") },
            ("Tap/2", "-TapL"), ("Tap/2", "0"), ("Base", "0"), ("Base", "BaseH"), ("Post", "BaseH"), ("Post", "BaseH+Glass"), ("Top", "BaseH+Glass"), ("Top", "BaseH+Glass+TopH"),
            ("Knob", "BaseH+Glass+TopH"), ("Knob", "BaseH+Glass+TopH+KnobH"));
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>The oiler's sight glass: a tube from y = 0, Glass high.</summary>
    static CadDocument HmOilerGlass()
    {
        var (d, _, _) = HmRing("Glass", new[] { ("In", "8"), ("Out", "10"), ("H", "22") }, ("In", "0"), ("Out", "0"), ("Out", "H"), ("In", "H"));
        d.Color = new[] { 0.55, 0.62, 0.90 };
        HmMaterial(d, "Glass");
        return d;
    }

    /// <summary>A flywheel's hub cap, brass, on the hub's outer face (z = 0 up): a ring round the shaft's end, four counterbored screw holes.</summary>
    static CadDocument HmHubCap()
    {
        var d = Doc(("D", "50"), ("T", "6"), ("Hole", "27"), ("PCD", "38"), ("Screw", "5.5"), ("Cb", "9"), ("CbH", "3"), ("C", "1"));
        var s = new Sk(d);
        var oc = s.Circle(Sketch.OriginId, "D"); s.Circle(Sketch.OriginId, "Hole");
        var ss = d.AddFeature(new SketchFeature { Name = "Cap", Plane = PlaneRef.Front, Sketch = s.S });
        var cap = d.AddFeature(new ExtrudeFeature { Name = "Cap", SketchId = ss.Id, Depth = "T" });
        double V(string e) => s.Eval(e);
        d.AddFeature(new ChamferFeature { Name = "Edge", Distance = "C", Edges = { E($"{cap.Id}.S.C{oc}", $"{cap.Id}.End", new Vec3(V("D/2"), 0, V("T"))) } });
        var tp = d.AddFeature(new PlaneFeature { Name = "Top", Base = PlaneRef.Front, Offset = "T" });
        var h = new Sk(d);
        h.Point(h.P("PCD/2*cos(45)", "PCD/2*sin(45)"));
        var hs = d.AddFeature(new SketchFeature { Name = "Screw hole", Plane = new PlaneRef { Feature = tp.Id }, Sketch = h.S });
        var hole = d.AddFeature(new HoleFeature { Name = "Screw hole", SketchId = hs.Id, Type = HoleType.Counterbore, Diameter = "Screw", CbDiameter = "Cb", CbDepth = "CbH", ThroughAll = true });
        d.AddFeature(new CircularPatternFeature { Name = "Screw holes", Seeds = { hole.Id }, Axis = new DirRef { Axis = "Z" }, Count = "4" });
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>The belt pulley bolted to the outer flywheel's hub (from z = 0 out): a web with the hub's screw holes and a crowned drum.</summary>
    static CadDocument HmPulley()
    {
        var (d, rev, L) = HmRing("Pulley", new[] { ("Bore", "13.5"), ("Web", "6"), ("Drum", "55"), ("Wall", "4"), ("W", "40"), ("PCD", "38"), ("Screw", "5.5") },
            ("Bore", "0"), ("Drum", "0"), ("Drum", "W"), ("Drum-Wall", "W"), ("Drum-Wall", "Web"), ("Bore", "Web"));
        // the section was drawn about Y: turned so the axis is Z by sketching on Front and revolving about the y axis, the
        // part lies along Y; the assembly turns it. The web's screw holes (Top: sketch y = -z) drilled down through the web.
        var tp = d.AddFeature(new PlaneFeature { Name = "Web face", Base = PlaneRef.Top, Offset = "Web" });
        var h = new Sk(d);
        h.Point(h.P("PCD/2*cos(45)", "PCD/2*sin(45)"));
        var hs = d.AddFeature(new SketchFeature { Name = "Screw hole", Plane = new PlaneRef { Feature = tp.Id }, Sketch = h.S });
        var hole = d.AddFeature(new HoleFeature { Name = "Screw hole", SketchId = hs.Id, Diameter = "Screw", ThroughAll = true });
        d.AddFeature(new CircularPatternFeature { Name = "Screw holes", Seeds = { hole.Id }, Axis = new DirRef { Axis = "Y" }, Count = "4" });
        _ = rev; _ = L;
        d.Color = HmSteel;
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    /// <summary>A governor weight, brass: a boss on its pivot (the origin, along Z), an arm along +X, a ball on its end.</summary>
    static CadDocument HmGovernorWeight()
    {
        var d = Doc(("Arm", "40"), ("Rod", "6"), ("Ball", "20"), ("Boss", "12"), ("BossT", "8"), ("Pin", "5"));
        var b = new Sk(d);
        b.Circle(Sketch.OriginId, "Boss");
        var bs = d.AddFeature(new SketchFeature { Name = "Boss", Plane = PlaneRef.Front, Sketch = b.S });
        d.AddFeature(new ExtrudeFeature { Name = "Boss", SketchId = bs.Id, End = EndCondition.MidPlane, Depth = "BossT" });
        var r = new Sk(d);
        r.Circle(Sketch.OriginId, "Rod");
        var rs = d.AddFeature(new SketchFeature { Name = "Arm", Plane = PlaneRef.Right, Sketch = r.S });
        d.AddFeature(new ExtrudeFeature { Name = "Arm", SketchId = rs.Id, Depth = "Arm" });
        var s = new Sk(d);
        int a = s.P("Arm-Ball/2", "0", dimX: false), c = s.P("Arm", "0"), e = s.P("Arm+Ball/2", "0");
        s.Arc(c, e, a);
        var ax = s.Line(a, e);
        var ss = d.AddFeature(new SketchFeature { Name = "Ball", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new RevolveFeature { Name = "Ball", SketchId = ss.Id, AxisEntity = ax, Angle = "360" });
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Pin");
        var hs = d.AddFeature(new SketchFeature { Name = "Pivot", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pivot", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = HmBrass;
        HmMaterial(d, "Brass");
        return d;
    }

    /// <summary>A rubber foot under the base, from y = 0 down, tapped for its hold-down screw.</summary>
    static CadDocument HmFoot()
    {
        var (d, rev, L) = HmRing("Foot", new[] { ("D", "12"), ("H", "10"), ("Tap", "4.25") }, ("Tap", "0"), ("D", "0"), ("D", "-H"), ("Tap", "-H"));
        _ = rev; _ = L;
        d.Color = HmBlack;
        HmMaterial(d, "Rubber");
        return d;
    }

    /// <summary>A hex nut along Z from z = 0, AF across flats, H thick, bored.</summary>
    static CadDocument HmHexNut(double af, double h, double bore)
    {
        var d = Doc(("S", $"{af}"), ("H", $"{h}"), ("Bore", $"{bore}"));
        var s = new Sk(d);
        s.Poly(s.P("S/2", "S/(2*sqrt(3))"), s.P("0", "S/sqrt(3)"), s.P("-S/2", "S/(2*sqrt(3))"), s.P("-S/2", "-S/(2*sqrt(3))"), s.P("0", "-S/sqrt(3)"), s.P("S/2", "-S/(2*sqrt(3))"));
        s.Circle(Sketch.OriginId, "Bore");
        var ss = d.AddFeature(new SketchFeature { Name = "Nut", Plane = PlaneRef.Front, Sketch = s.S });
        d.AddFeature(new ExtrudeFeature { Name = "Nut", SketchId = ss.Id, Depth = "H" });
        d.Color = HmBlack;
        HmMaterial(d, "Steel (plain carbon)");
        return d;
    }

    // ------------------------------------------------------------------------------------------
    // the assembly

    /// <summary>The crank angle the engine is put together at (radians): the crank gear's teeth then mesh with the cam gear's.</summary>
    const double HmCrank0 = 37.5 * Math.PI / 180;

    // faces picked by their geometry, in the part's coordinates
    static bool HmCyl(FaceRange f, double r, Vec3 axis, Func<Vec3, bool>? at = null) =>
        f.Surface.Kind == SurfaceKind.Cylinder && Math.Abs(f.Surface.Radius - r) < 1e-6
        && Math.Abs(Vec3.Dot(f.Surface.Axis.Normalized, axis)) > 0.999 && (at == null || at(f.Bounds.Center));
    static bool HmPlane(FaceRange f, Vec3 normal, double at, Func<Vec3, bool>? where = null) =>
        f.Surface.Kind == SurfaceKind.Plane && Vec3.Dot(f.FirstPlane.Normal, normal) > 0.999
        && Math.Abs(Vec3.Dot(f.FirstPlane.Origin, normal) - at) < 1e-6 && (where == null || where(f.Bounds.Center));

    // ------------------------------------------------------------------------------------------
    // checking

    /// <summary>The engine's parts by name (each built fresh).</summary>
    public static IEnumerable<(string name, Func<CadDocument> build)> HmParts => new (string, Func<CadDocument>)[]
    {
        ("Crankshaft", HmCrankshaft),
        ("Connecting rod", HmConRod),
        ("Rod cap", HmRodCap),
        ("Piston", HmPiston),
        ("Wrist pin", HmWristPin),
        ("Flywheel", HmFlywheel),
        ("Casting", HmCasting),
        ("Bearing cap", HmBearingCap),
        ("Cylinder head", HmHead),
        ("Crank gear", () => HmGear(HmZCrank, HmJ)),
        ("Cam gear", HmCamGear),
        ("Cam stud", HmCamStud),
        ("Push rod", HmPushRod),
        ("Rocker", HmRocker),
        ("Pivot pin", HmPivotPin),
        ("Valve", HmValve),
        ("Valve spring", HmValveSpring),
        ("Retainer", HmRetainer),
        ("Funnel", HmFunnel),
        ("Muffler", HmMuffler),
        ("Muffler cap", HmMufflerCap),
        ("Mixer", HmMixer),
        ("Needle valve", HmNeedleValve),
        ("Fuel line", () => HmFuelLine(new Vec3(-210, -24, -78))),
        ("Spark plug", HmSparkPlug),
        ("Plug boot", HmPlugBoot),
        ("Oil cup", HmOilCup),
        ("Oiler", HmOiler),
        ("Oiler glass", HmOilerGlass),
        ("Hub cap", HmHubCap),
        ("Pulley", HmPulley),
        ("Governor weight", HmGovernorWeight),
        ("Foot", HmFoot),
        ("Hex nut M6", () => HmHexNut(10, 5, 6.4)),
    };

    /// <summary><c>-- engine-steps name</c>: one engine part rebuilt a feature at a time, each step's time (finding a slow or hanging feature).</summary>
    public static int EngineSteps(string name)
    {
        var doc = HmParts.First(p => p.name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).build();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "fc-engine-step.json"), doc.ToJson());
        for (int i = 1; i <= doc.Features.Count; i++)
        {
            Csg.ClearCache();
            Console.Write($"{i,3} {doc.Features[i - 1].Name,-28} ");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(doc, i);
            var st = r.Status[doc.Features[i - 1].Id];
            var bodies = string.Join(" ", r.State.Bodies.Select(b => $"{(b.Solid.Native != null ? "exact" : "FACETED")}{(b.Solid.Topology.OpenHalfEdges > 0 ? $" OPEN({b.Solid.Topology.OpenHalfEdges})" : "")}"));
            Console.WriteLine($"{sw.ElapsedMilliseconds,7} ms {bodies} {(st.Ok ? "" : st.Error)} {string.Join("; ", st.Warnings)}");
            if (r.State.Bodies.Any(b => b.Solid.Native == null || b.Solid.Topology.OpenHalfEdges > 0)) { Console.WriteLine("  (stopped: the body is no longer exact and closed)"); break; }
        }
        return 0;
    }

    /// <summary>
    /// <c>-- engine-faces part feature x y z [r]</c>: the faces of an engine part near a point (within r, 3 mm by default)
    /// after the named feature, by name, kind and box (finding the names to pick edges by).
    /// </summary>
    public static int EngineFaces(string name, string feature, Vec3 at, double r = 3)
    {
        var doc = HmParts.First(p => p.name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).build();
        var upTo = doc.Features.FindIndex(f => f.Name == feature) + 1;
        var res = new Regenerator().Rebuild(doc, upTo > 0 ? upTo : doc.Features.Count);
        var near = new Box3 { Min = at - new Vec3(r, r, r), Max = at + new Vec3(r, r, r) };
        foreach (var b in res.State.Bodies)
            foreach (var f in b.Mesh.Faces.Where(f => f.Bounds.Overlaps(near)))
                Console.WriteLine($"{f.Name,-28} {f.Surface.Kind,-9} box {f.Bounds.Min} .. {f.Bounds.Max}");
        return 0;
    }

    /// <summary><c>-- verify-engine [outDir] [filter]</c>:rebuilds each part of the engine, reports what fails, warns or is not fully defined.</summary>
    public static int VerifyEngine(string? dir, string? filter)
    {
        int bad = 0;
        if (dir != null) Directory.CreateDirectory(dir);
        foreach (var (name, build) in HmParts.Where(p => filter == null || p.name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            var doc = build();
            Csg.ClearCache();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = new Regenerator().Rebuild(doc);
            var ms = sw.Elapsed.TotalMilliseconds;
            var problems = doc.Features.Where(f => !r.Status[f.Id].Ok || r.Status[f.Id].Warnings.Count > 0)
                .Select(f => $"{f.Name}: {r.Status[f.Id].Error}{string.Join(";", r.Status[f.Id].Warnings)}").ToList();
            foreach (var sf in doc.Features.OfType<SketchFeature>())
                if (r.State.Sketches.GetValueOrDefault(sf.Id)?.Solve is { } sv && sv.Status != SketchStatus.FullyDefined)
                    problems.Add($"{sf.Name}: sketch {sv.Status}");
            var ok = problems.Count == 0 && r.State.Bodies.Count >= 1 && r.State.Bodies.All(bb => bb.Solid.Native != null && bb.Solid.Topology.OpenHalfEdges == 0);
            var vol = r.State.Bodies.Sum(bb => KernelTests.Vex(bb.Solid));
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name,-22} {doc.Features.Count,3} features {ms,7:0} ms  bodies {r.State.Bodies.Count}  vol {vol,10:0}  {string.Join(" | ", problems)}");
            if (!ok) bad++;
            if (dir != null)
            {
                doc.Thumbnail = DocumentContainer.RenderThumbnail(doc, r);
                File.WriteAllBytes(Path.Combine(dir, name + ".png"), Thumbnail.Render(r.State.Bodies.Select(bb => (bb.Mesh, doc.Color)), 700));
                doc.Save(Path.Combine(dir, name + ".sprt"), container: true);
            }
        }
        return bad;
    }
}

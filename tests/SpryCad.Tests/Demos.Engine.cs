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
        return d;
    }

    /// <summary>
    /// The piston about its wrist pin (on the origin, along Z), its crown towards -X: a revolved half section with
    /// three ring grooves and a hollow skirt, the wrist pin's bosses inside, bored through.
    /// </summary>
    static CadDocument HmPiston()
    {
        var d = Doc(("D", $"{HmBore}-0.2"), ("Crown", "25"), ("Skirt", "30"), ("CrownT", "8"), ("Wall", "4"),
            ("Gw", "2"), ("Gd", "2.5"), ("G1", "4"), ("Gp", "5"), ("BossD", "22"), ("Wp", $"{HmWp}"), ("C", "0.8"));
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
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "Wp");
        var hs = d.AddFeature(new SketchFeature { Name = "Pin bore", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Pin bore", SketchId = hs.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        d.Color = new[] { 0.78, 0.79, 0.80 };
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
        return d;
    }

    /// <summary>
    /// A flywheel about Z, its spokes on the Front plane: a crowned rim (revolved, its inner edges rounded), a hub
    /// standing further in (towards -Z) than out, NS spokes of an oval section tapering (drafted) from the hub to the
    /// rim, filleted into both, and a keyed bore.
    /// </summary>
    static CadDocument HmFlywheel()
    {
        var d = Doc(("D", "280"), ("RimW", "40"), ("RimT", "20"), ("Crown", "1.5"), ("HubD", "56"), ("HubIn", "30"), ("HubOut", "20"),
            ("Bore", $"{HmJ}"), ("KeyW", "6"), ("KeyT", "3"), ("NS", "6"), ("SpW", "26"), ("SpT", "14"), ("SpDraft", "1.5"),
            ("Rf", "5"), ("Rh", "3"), ("Rr", "3"), ("C", "1.5"), ("CapPCD", "38"));
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
            ("BodyX0", "170"), ("BodyX1", "40"), ("BodyW", "116"), ("BodyDraft", "2"), ("BodyR", "20"), ("Rb", "6"),
            ("BrgIn", $"{HmBrgIn}"), ("BrgOut", $"{HmBrgOut}"), ("BrgW", "60"), ("BrgDrop", "40"), ("J", $"{HmJ}"), ("CapStud", "22"),
            ("HeadX", $"{-HmHeadX}"), ("BarrelD", "104"), ("BarrelEnd", "70"), ("FlangeD", "116"), ("FlangeT", "12"), ("StudPCD", "96"), ("NStud", "6"),
            ("HopX0", "175"), ("HopX1", "85"), ("HopW", "116"), ("HopH", "82"), ("HopWall", "7"), ("HopDraft", "2"), ("HopR", "12"), ("Jacket", "20"), ("FillD", "44"),
            ("LinerD", "66"), ("Bore", $"{HmBore}"),
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
        // (blending the body into the base all round fails: docs/hit-and-miss-notes.md)
        // the main bearings' lower halves, from BrgIn out to BrgOut each side
        var bp = d.AddFeature(new PlaneFeature { Name = "Bearing inner face", Base = PlaneRef.Front, Offset = "BrgIn" });
        var p = new Sk(d);
        p.Rect("-BrgW/2", "-BrgDrop", "BrgW/2", "0");
        var ps = d.AddFeature(new SketchFeature { Name = "Bearing pedestal", Plane = new PlaneRef { Feature = bp.Id }, Sketch = p.S });
        var ped = d.AddFeature(new ExtrudeFeature { Name = "Bearing pedestal", SketchId = ps.Id, Depth = "BrgOut-BrgIn" });
        d.AddFeature(new MirrorFeature { Name = "Bearing pedestal, back", Seeds = { ped.Id }, Plane = PlaneRef.Front });
        // the cylinder: the barrel from the head's face along +X, the flange the head bolts to
        var cf = d.AddFeature(new PlaneFeature { Name = "Cylinder face", Base = PlaneRef.Right, Offset = "-HeadX" });
        var c = new Sk(d);
        c.Circle(Sketch.OriginId, "BarrelD");
        var cs = d.AddFeature(new SketchFeature { Name = "Barrel", Plane = new PlaneRef { Feature = cf.Id }, Sketch = c.S });
        d.AddFeature(new ExtrudeFeature { Name = "Barrel", SketchId = cs.Id, Depth = "HeadX-BarrelEnd" });
        var f = new Sk(d);
        f.Circle(Sketch.OriginId, "FlangeD");
        var fs = d.AddFeature(new SketchFeature { Name = "Flange", Plane = new PlaneRef { Feature = cf.Id }, Sketch = f.S });
        d.AddFeature(new ExtrudeFeature { Name = "Flange", SketchId = fs.Id, Depth = "FlangeT" });
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
        // the crank pit, through the body from above, open at the back
        var pt = d.AddFeature(new PlaneFeature { Name = "Pit top", Base = PlaneRef.Top, Offset = "BarrelD/2" });
        var k = new Sk(d);
        k.Rect("-BarrelEnd", "-PitW/2", "PitX1", "PitW/2");
        var ks = d.AddFeature(new SketchFeature { Name = "Crank pit", Plane = new PlaneRef { Feature = pt.Id }, Sketch = k.S });
        d.AddFeature(new ExtrudeFeature { Name = "Crank pit", SketchId = ks.Id, IsCut = true, Reverse = true, Depth = "BarrelD/2+PitBot" });
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
        cb.Circle(cb.P("-CamX", "0"), "CamD");
        var cbs = d.AddFeature(new SketchFeature { Name = "Cam boss", Plane = new PlaneRef { Feature = ci.Id }, Sketch = cb.S });
        d.AddFeature(new ExtrudeFeature { Name = "Cam boss", SketchId = cbs.Id, Depth = "GearZ-CamIn-1" });
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
        d.Color = new[] { 0.72, 0.08, 0.07 };
        return d;
    }

    /// <summary>
    /// A main bearing's cap, about the bearing's axis (Z) from z = 0 to its length, its split face on y = 0: flat
    /// shoulders for the studs' nuts either side of a round hump over the bore, a boss on top for the oil cup.
    /// </summary>
    static CadDocument HmBearingCap()
    {
        var d = Doc(("J", $"{HmJ}"), ("W", "60"), ("L", $"{HmBrgOut - HmBrgIn}"), ("T", "14"), ("Hump", "18"), ("Stud", "22"), ("StudD", "8.5"),
            ("CupD", "14"), ("CupH", "4"), ("Tap", "6.8"), ("C", "1"));
        var s = new Sk(d);
        int a = s.P("-W/2", "0"), b = s.P("W/2", "0"), c = s.P("W/2", "T"), e = s.P("Hump", "T"), g = s.P("-Hump", "T", dimX: false), h = s.P("-W/2", "T");
        int hc = s.P("0", "T");
        s.Line(a, b); s.Line(b, c); s.Line(c, e); var hump = s.Arc(hc, e, g); s.Line(g, h); s.Line(h, a);
        var sk = d.AddFeature(new SketchFeature { Name = "Cap", Plane = PlaneRef.Front, Sketch = s.S });
        var cap = d.AddFeature(new ExtrudeFeature { Name = "Cap", SketchId = sk.Id, Depth = "L" });
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
        _ = cap; _ = hump;
        d.Color = new[] { 0.72, 0.08, 0.07 };
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
            ("Vz", $"{HmValveZ}"), ("Stem", "6.2"), ("Seat", "20"), ("PortD", "18"), ("PortX", "210"), ("BossD", "28"), ("BossOut", "62"),
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
        return d;
    }

    /// <summary>The muffler's brass top ring, from y = 0.</summary>
    static CadDocument HmMufflerCap()
    {
        var (d, rev, L) = HmRing("Ring", new[] { ("In", "8"), ("Out", "18.5"), ("H", "10"), ("Lip", "17"), ("C", "1.5") },
            ("In", "0"), ("Out", "0"), ("Out", "H-3"), ("Lip", "H"), ("In", "H"));
        d.Color = HmBrass;
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
        return d;
    }

    /// <summary>
    /// The fuel line, in the engine's coordinates: a tube swept from the mixer's nipple down beside the casting and
    /// along to the tank in the base, on the plane z = Z (its section on a plane across the start).
    /// </summary>
    static CadDocument HmFuelLine(Vec3 start)
    {
        var d = Doc(("X0", $"{-start.X}"), ("Y0", $"{-start.Y}"), ("Z", $"{-start.Z}"), ("OD", "7"), ("ID", "4"), ("R", "15"),
            ("Drop", "60"), ("Run", "70"), ("Y1", "132"));
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
        // the lead: from the boot's end along -X, a bend, then down (-Y)
        var lp = d.AddFeature(new PlaneFeature { Name = "Boot end", Base = PlaneRef.Right, Offset = "-(L-6)" });
        var l = new Sk(d);
        l.Circle(Sketch.OriginId, "Lead");
        var ls = d.AddFeature(new SketchFeature { Name = "Lead section", Plane = new PlaneRef { Feature = lp.Id }, Sketch = l.S });
        var w = new Sk(d);
        int a = w.P("-(L-6)", "0"), b = w.P("-(L+6)", "0"), cc = w.P("-(L+6)", "-R"), e = w.P("-(L+6+R)", "-R"), f = w.P("-(L+6+R)", "-(R+Run)");
        w.Line(a, b); w.Arc(cc, b, e); w.Line(e, f);
        var ws = d.AddFeature(new SketchFeature { Name = "Lead route", Plane = PlaneRef.Front, Sketch = w.S });
        d.AddFeature(new SweepFeature { Name = "Lead", ProfileSketchId = ls.Id, PathSketchId = ws.Id });
        d.Color = HmBlack;
        return d;
    }

    /// <summary>An oil cup, brass, up from y = 0: its thread into the tapped hole, a hex, the cup, a domed lid.</summary>
    static CadDocument HmOilCup()
    {
        var (d, rev, L) = HmTurned("Oil cup", new[] { ("Tap", "6.8"), ("TapL", "6"), ("Hex", "8"), ("HexH", "5"), ("Cup", "7"), ("CupH", "20"), ("Lid", "8.5"), ("LidH", "6") },
            ("Tap/2", "-TapL"), ("Tap/2", "0"), ("Hex", "0"), ("Hex", "HexH"), ("Cup", "HexH"), ("Cup", "HexH+CupH"), ("Lid", "HexH+CupH"), ("Lid", "HexH+CupH+LidH"));
        d.AddFeature(new FilletFeature { Name = "Lid", Radius = "3", Edges = { E($"{rev.Id}.R.L{L[7]}", $"{rev.Id}.R.L{L[8]}", new Vec3(8.5, 31, 0)) } });
        d.Color = HmBrass;
        return d;
    }

    /// <summary>The cylinder's drip oiler, brass, up from y = 0: thread, base, a post through the glass, the top cap and its knob.</summary>
    static CadDocument HmOiler()
    {
        var (d, _, _) = HmTurned("Oiler", new[] { ("Tap", "6.8"), ("TapL", "6"), ("Base", "11"), ("BaseH", "8"), ("Post", "3"), ("Glass", "22"), ("Top", "11.5"), ("TopH", "6"), ("Knob", "4"), ("KnobH", "8") },
            ("Tap/2", "-TapL"), ("Tap/2", "0"), ("Base", "0"), ("Base", "BaseH"), ("Post", "BaseH"), ("Post", "BaseH+Glass"), ("Top", "BaseH+Glass"), ("Top", "BaseH+Glass+TopH"),
            ("Knob", "BaseH+Glass+TopH"), ("Knob", "BaseH+Glass+TopH+KnobH"));
        d.Color = HmBrass;
        return d;
    }

    /// <summary>The oiler's sight glass: a tube from y = 0, Glass high.</summary>
    static CadDocument HmOilerGlass()
    {
        var (d, _, _) = HmRing("Glass", new[] { ("In", "8"), ("Out", "10"), ("H", "22") }, ("In", "0"), ("Out", "0"), ("Out", "H"), ("In", "H"));
        d.Color = new[] { 0.55, 0.62, 0.90 };
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
        return d;
    }

    /// <summary>A rubber foot under the base, from y = 0 down, tapped for its hold-down screw.</summary>
    static CadDocument HmFoot()
    {
        var (d, rev, L) = HmRing("Foot", new[] { ("D", "12"), ("H", "10"), ("Tap", "4.25") }, ("Tap", "0"), ("D", "0"), ("D", "-H"), ("Tap", "-H"));
        _ = rev; _ = L;
        d.Color = HmBlack;
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

    /// <summary>
    /// The engine put together (its parts embedded): placed where they belong at the crank angle <see cref="HmCrank0"/>
    /// and mated as they work: the crankshaft in its bearings, the flywheels and crank gear keyed to it (locks), the
    /// rod on the crank pin and the wrist pin, the piston in the bore, the cam gear on its stud meshing 2:1 with the
    /// crank gear, the push rod in its lugs riding on the cam gear's eccentric (tangent), the rocker on its pivot
    /// between the push rod and the exhaust valve's stem (tangent each). Turn a flywheel and it all runs.
    /// </summary>
    static AssemblyModel HitAndMiss()
    {
        var m = AssemblyModel.New();
        Component Add(CadDocument doc, string name, Transform t) => m.InsertEmbedded(doc, name, t);
        static Transform T(double x, double y, double z) => Transform.Translation(new Vec3(x, y, z));
        static Transform Rz(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitZ, a);
        static Transform Ry(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitY, a);
        double th = HmCrank0;
        var pinAt = new Vec3(HmR * Math.Cos(th), HmR * Math.Sin(th), 0);
        var px = HmPistonX(th);
        var rodDir = Math.Atan2(-pinAt.Y, px - pinAt.X);

        var casting = Add(HmCasting(), "Casting", Transform.Identity);
        casting.Fixed = true;
        var capDoc = HmBearingCap();
        var cap1 = Add(capDoc, "Bearing cap", T(0, 0, HmBrgIn));
        var cap2 = Add(capDoc, "Bearing cap", T(0, 0, -HmBrgIn) * Ry(Math.PI));
        var crank = Add(HmCrankshaft(), "Crankshaft", Rz(th));
        var fwDoc = HmFlywheel();
        var fw1 = Add(fwDoc, "Flywheel", T(0, 0, HmFwZ) * Rz(th));
        var fw2 = Add(fwDoc, "Flywheel", Rz(th) * T(0, 0, -HmFwZ) * Ry(Math.PI));
        var crankGear = Add(HmGear(HmZCrank, HmJ), "Crank gear", T(0, 0, HmGearZ) * Rz(th + Math.PI / 2));
        var rod = Add(HmConRod(), "Connecting rod", T(pinAt.X, pinAt.Y, 0) * Rz(rodDir + Math.PI));
        var rodCap = Add(HmRodCap(), "Rod cap", T(pinAt.X, pinAt.Y, 0) * Rz(rodDir + Math.PI));
        var piston = Add(HmPiston(), "Piston", T(px, 0, 0));
        var wristPin = Add(HmWristPin(), "Wrist pin", T(px, 0, 0));
        var head = Add(HmHead(), "Cylinder head", Transform.Identity);
        var stud = Add(HmCamStud(), "Cam stud", T(HmCamX, 0, HmGearZ));
        var camGear = Add(HmCamGear(), "Cam gear", T(HmCamX, 0, HmGearZ));
        double rockerX = HmHeadX - HmHeadT - 35, stemEnd = HmHeadX - 65;
        var pushRod = Add(HmPushRod(), "Push rod", T(stemEnd, 0, HmPushZ));
        var rocker = Add(HmRocker(), "Rocker", T(rockerX, 0, (HmValveZ + HmPushZ) / 2));
        var pivot = Add(HmPivotPin(), "Rocker pin", T(rockerX, 11.5, (HmValveZ + HmPushZ) / 2));
        var valveDoc = HmValve(); var springDoc = HmValveSpring(); var retDoc = HmRetainer();
        var exhaust = Add(valveDoc, "Valve", T(HmHeadX, 0, HmValveZ));
        var intake = Add(valveDoc, "Valve", T(HmHeadX, 0, -HmValveZ));
        double springAt = HmHeadX - HmHeadT - 6;
        var exSpring = Add(springDoc, "Valve spring", T(springAt, 0, HmValveZ));
        var inSpring = Add(springDoc, "Valve spring", T(springAt, 0, -HmValveZ));
        var exRet = Add(retDoc, "Retainer", T(springAt - 16, 0, HmValveZ));
        var inRet = Add(retDoc, "Retainer", T(springAt - 16, 0, -HmValveZ));
        foreach (var c in m.Doc.Components) if (c != casting) c.Fixed = false;

        MateRef F(Component c, Func<FaceRange, bool> which)
        {
            var faces = m.DisplayOf(c)!.Mesh.Faces;
            if (faces.Any(which)) return FaceRef(m, c, which);
            throw new InvalidOperationException($"no such face on {c.Name}: " + string.Join("; ", faces.Select(f =>
                $"{f.Name} {f.Surface.Kind} r={f.Surface.Radius:0.###} ax={f.Surface.Axis} n={f.FirstPlane.Normal} o={f.FirstPlane.Origin} c={f.Bounds.Center}")));
        }
        MateRef Any(Component c) => m.RefTo(c.Id, m.DisplayOf(c)!.Mesh.Faces[0].Name)!;
        var (X, Y, Z) = (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        const MateType Co = MateType.Coincident, Cc = MateType.Concentric, Lk = MateType.Lock, Tg = MateType.Tangent;
        var top = m.RefTo(null, "Top")!;
        double rj = HmJ / 2;

        // the bearing caps on the pedestals
        foreach (var cap in new[] { cap1, cap2 })
        {
            m.AddMate(Co, F(cap, f => HmPlane(f, -Y, 0)), top);
            m.AddMate(Cc, F(cap, f => HmCyl(f, rj, Z)), F(casting, f => HmCyl(f, rj, Z)));
            var side = cap == cap1 ? 1 : -1;
            m.AddMate(Co, F(cap, f => HmPlane(f, -Z, 0)), F(casting, f => HmPlane(f, -side * Z, -HmBrgIn)));
        }
        // the crankshaft in its bearings, a gap to the bearing at the +Z web
        var journal = F(crank, f => HmCyl(f, rj, Z, p => p.Z > HmBrgOut));
        m.AddMate(Cc, journal, F(casting, f => HmCyl(f, rj, Z)));
        m.AddMate(Co, F(crank, f => HmPlane(f, Z, HmWebIn + HmWebT)), F(casting, f => HmPlane(f, -Z, -HmBrgIn)), HmBrgIn - HmWebIn - HmWebT);
        // the flywheels and the crank gear keyed to it
        foreach (var fw in new[] { fw1, fw2 })
        {
            m.AddMate(Cc, F(fw, f => HmCyl(f, rj, Z)), journal);
            m.AddMate(Lk, Any(crank), Any(fw));
        }
        m.AddMate(Cc, F(crankGear, f => HmCyl(f, rj, Z)), journal);
        m.AddMate(Lk, Any(crank), Any(crankGear));
        // the rod on the crank pin beside the +Z web, its cap bolted on; the piston in the bore on its wrist pin
        m.AddMate(Cc, F(rod, f => HmCyl(f, HmPin / 2, Z)), F(crank, f => HmCyl(f, HmPin / 2, Z)));
        m.AddMate(Co, F(rod, f => HmPlane(f, Z, 11, p => p.X > -20)), F(crank, f => HmPlane(f, -Z, -HmWebIn)), HmWebIn - 11);
        m.AddMate(Lk, Any(rod), Any(rodCap));
        m.AddMate(Cc, F(piston, f => HmCyl(f, (HmBore - 0.2) / 2, X)), F(casting, f => HmCyl(f, HmBore / 2, X)));
        m.AddMate(Cc, F(wristPin, f => HmCyl(f, HmWp / 2, Z)), F(piston, f => HmCyl(f, HmWp / 2, Z)));
        m.AddMate(Lk, Any(piston), Any(wristPin));
        m.AddMate(Cc, F(rod, f => HmCyl(f, HmWp / 2, Z)), F(wristPin, f => HmCyl(f, HmWp / 2, Z)));
        // the head on the cylinder's flange, two of its studs' holes on the flange's
        m.AddMate(Co, F(head, f => HmPlane(f, X, HmHeadX)), F(casting, f => HmPlane(f, -X, -HmHeadX)));
        foreach (var yy in new[] { 48.0, -48.0 })
            m.AddMate(Cc, F(head, f => HmCyl(f, 4.25, X, p => Math.Abs(p.Y - yy) < 1)), F(casting, f => HmCyl(f, 3.4, X, p => Math.Abs(p.Y - yy) < 1 && Math.Abs(p.Z) < 1)));
        // the cam gear's stud pressed into its boss; the gear on it beside the crank gear, meshing 2:1
        m.AddMate(Cc, F(stud, f => HmCyl(f, 8, Z)), F(casting, f => HmCyl(f, 8, Z)));
        m.AddMate(Lk, Any(casting), Any(stud));
        var camBore = F(camGear, f => HmCyl(f, 8, Z));
        m.AddMate(Cc, camBore, F(stud, f => HmCyl(f, 8, Z)));
        m.AddMate(Co, F(camGear, f => HmPlane(f, -Z, 0)), F(crankGear, f => HmPlane(f, -Z, 0)));
        m.AddMate(MateType.Gear, F(crankGear, f => HmCyl(f, rj, Z)), camBore, setup: g => { g.RatioA = HmZCrank; g.RatioB = HmZCam; });
        // the push rod in its lugs, on the eccentric; the rocker on its pivot between the push rod and the exhaust valve
        var rodEnd = HmPushRod().Equations.First(q => q.Name == "Len").Expr;
        double len = double.Parse(rodEnd, System.Globalization.CultureInfo.InvariantCulture);
        m.AddMate(Cc, F(pushRod, f => HmCyl(f, 4, X)), F(casting, f => HmCyl(f, 4.2, X)));
        m.AddMate(Tg, F(pushRod, f => HmPlane(f, X, len)), F(camGear, f => HmCyl(f, HmEccD / 2, Z)));
        m.AddMate(Cc, F(rocker, f => HmCyl(f, 4.1, Y)), F(head, f => HmCyl(f, 4.1, Y)));
        m.AddMate(Co, F(rocker, f => HmPlane(f, Y, 5)), F(head, f => HmPlane(f, -Y, -5.5)), 0.5);
        m.AddMate(Tg, F(rocker, f => HmCyl(f, 4, Y, p => p.Z > 0)), F(pushRod, f => HmPlane(f, -X, 0)));
        m.AddMate(Cc, F(pivot, f => HmCyl(f, 4, Y)), F(head, f => HmCyl(f, 4.1, Y)));
        m.AddMate(Co, F(pivot, f => HmPlane(f, -Y, 0)), F(head, f => HmPlane(f, Y, 11.5)));
        // the valves in their guides: the exhaust held on the rocker, the intake (opened by the suction) closed
        foreach (var (v, z) in new[] { (exhaust, HmValveZ), (intake, -HmValveZ) })
            m.AddMate(Cc, F(v, f => HmCyl(f, 3, X, p => p.X < -20)), F(head, f => HmCyl(f, 3.1, X, p => Math.Abs(p.Z - z) < 1)));
        m.AddMate(Tg, F(rocker, f => HmCyl(f, 4, Y, p => p.Z < 0)), F(exhaust, f => HmPlane(f, -X, 65)));
        m.AddMate(Co, F(intake, f => HmPlane(f, X, 0)), F(head, f => HmPlane(f, X, HmHeadX)));
        // the springs on the guide bosses, the retainers on the valves (the springs do not compress: notes)
        foreach (var s in new[] { exSpring, inSpring }) m.AddMate(Lk, Any(head), Any(s));
        m.AddMate(Lk, Any(exhaust), Any(exRet));
        m.AddMate(Lk, Any(intake), Any(inRet));

        // ---- the fittings
        Component Fitting(CadDocument doc, string name, Transform t) { var c = Add(doc, name, t); c.Fixed = false; return c; }
        // the hopper's funnel, the exhaust and its muffler, the mixer and its needle valve, the fuel line
        var funnel = Fitting(HmFunnel(), "Funnel", T(-130, 82, 0));
        m.AddMate(Cc, F(funnel, f => HmCyl(f, 21.8, Y)), F(casting, f => HmCyl(f, 22, Y)));
        m.AddMate(Co, F(funnel, f => HmPlane(f, -Y, 0)), F(casting, f => HmPlane(f, Y, 82)));
        var muffler = Fitting(HmMuffler(), "Muffler", T(-210, 62, HmValveZ));
        m.AddMate(Cc, F(muffler, f => HmCyl(f, 9, Y)), F(head, f => HmCyl(f, 9, Y)));
        m.AddMate(Co, F(muffler, f => HmPlane(f, -Y, 0)), F(head, f => HmPlane(f, Y, 62)));
        var mufflerCap = Fitting(HmMufflerCap(), "Muffler cap", T(-210, 62 + 190, HmValveZ));
        m.AddMate(Cc, F(mufflerCap, f => HmCyl(f, 8, Y)), F(muffler, f => HmCyl(f, 8, Y)));
        m.AddMate(Co, F(mufflerCap, f => HmPlane(f, -Y, 0)), F(muffler, f => HmPlane(f, Y, 190)));
        var mixerAt = T(-210, 0, -62) * Ry(Math.PI);
        var mixer = Fitting(HmMixer(), "Mixer", mixerAt);
        m.AddMate(Cc, F(mixer, f => HmCyl(f, 9, Z)), F(head, f => HmCyl(f, 9, Z)));
        m.AddMate(Co, F(mixer, f => HmPlane(f, -Z, 0)), F(head, f => HmPlane(f, -Z, 62)));
        m.AddMate(MateType.Parallel, F(mixer, f => HmPlane(f, Y, 38)), top);
        var needle = Fitting(HmNeedleValve(), "Needle valve", mixerAt * T(0, 0, 40));
        m.AddMate(Cc, F(needle, f => HmCyl(f, 3, Z)), F(mixer, f => HmCyl(f, 3, Z)));
        m.AddMate(Co, F(needle, f => HmPlane(f, -Z, 0)), F(mixer, f => HmPlane(f, Z, 40)));
        var fuel = Fitting(HmFuelLine(new Vec3(-210, -24, -78)), "Fuel line", Transform.Identity);
        m.AddMate(Lk, Any(casting), Any(fuel));
        // the spark plug and its boot
        var plug = Fitting(HmSparkPlug(), "Spark plug", T(HmHeadX - HmHeadT, -18, 0));
        m.AddMate(Cc, F(plug, f => HmCyl(f, 6.25, X)), F(head, f => HmCyl(f, 6.25, X)));
        m.AddMate(Co, F(plug, f => HmPlane(f, X, 0)), F(head, f => HmPlane(f, -X, -(HmHeadX - HmHeadT))));
        var boot = Fitting(HmPlugBoot(), "Plug boot", T(HmHeadX - HmHeadT - 22, -18, 0));
        m.AddMate(Lk, Any(plug), Any(boot));
        // the oil cups on the bearing caps, the drip oiler (and its glass) on the cylinder
        var cupDoc = HmOilCup();
        foreach (var (cap, z) in new[] { (cap1, 50.0), (cap2, -50.0) })
        {
            var cup = Fitting(cupDoc, "Oil cup", T(0, 36, z));
            m.AddMate(Cc, F(cup, f => HmCyl(f, 3.4, Y)), F(cap, f => HmCyl(f, 3.4, Y)));
            m.AddMate(Co, F(cup, f => HmPlane(f, -Y, 0)), F(cap, f => HmPlane(f, Y, 36)));
        }
        var oiler = Fitting(HmOiler(), "Oiler", T(-77, 56, 0));
        m.AddMate(Cc, F(oiler, f => HmCyl(f, 3.4, Y)), F(casting, f => HmCyl(f, 3.4, Y, p => Math.Abs(p.X + 77) < 1)));
        m.AddMate(Co, F(oiler, f => HmPlane(f, -Y, 0)), F(casting, f => HmPlane(f, Y, 56)));
        var glass = Fitting(HmOilerGlass(), "Oiler glass", T(-77, 64, 0));
        m.AddMate(Lk, Any(oiler), Any(glass));
        // on the flywheels: the brass hub caps (the outer one over the belt pulley), the governor's weights
        var capDocB = HmHubCap();
        var fw1At = fw1.Placement.ToTransform(); var fw2At = fw2.Placement.ToTransform();
        var hubCap1 = Fitting(capDocB, "Hub cap", fw1At * T(0, 0, 20));
        var pulley = Fitting(HmPulley(), "Pulley", fw2At * T(0, 0, 20) * Transform.Rotation(Vec3.Zero, X, Math.PI / 2));
        var hubCap2 = Fitting(capDocB, "Hub cap", fw2At * T(0, 0, 26));
        m.AddMate(Lk, Any(fw1), Any(hubCap1));
        m.AddMate(Lk, Any(fw2), Any(pulley));
        m.AddMate(Lk, Any(fw2), Any(hubCap2));
        var weightDoc = HmGovernorWeight();
        foreach (var a in new[] { 0.0, Math.PI })
        {
            var wt = Fitting(weightDoc, "Governor weight", fw1At * Rz(a) * T(55, 0, 11) * Rz(Math.PI / 2));
            m.AddMate(Lk, Any(fw1), Any(wt));
        }
        // the feet under the base
        var footDoc = HmFoot();
        var feet = new List<(Component foot, double x, double z)>();
        foreach (var (x, z) in new[] { (-207.0, -70.0), (-207.0, 70.0), (52.0, -70.0), (52.0, 70.0) })
        {
            var foot = Fitting(footDoc, "Foot", T(x, -150, z));
            m.AddMate(Cc, F(foot, f => HmCyl(f, 4.25, Y)), F(casting, f => HmCyl(f, 5.5, Y, p => Math.Abs(p.X - x) < 1 && Math.Abs(p.Z - z) < 1)));
            m.AddMate(Co, F(foot, f => HmPlane(f, Y, 0)), F(casting, f => HmPlane(f, -Y, 150)));
            feet.Add((foot, x, z));
        }

        // ---- the screws: one socket head cap screw part, configured for each size and length
        var screwDoc = SocketScrew();
        Component Screw(string size, string length, Transform t)
        {
            var c = Fitting(screwDoc, "Socket screw", t);
            m.Configure(new[] { c }, screwDoc, new Dictionary<string, string> { ["Size"] = size, ["L"] = length });
            return c;
        }
        // a screw on its hole: the shank on the hole's axis, the head's underside on the face (k: the head's height)
        void Seat(Component screw, double d, double k, MateRef hole, MateRef face)
        {
            m.AddMate(Cc, F(screw, f => HmCyl(f, d / 2, Z)), hole);
            m.AddMate(Co, F(screw, f => HmPlane(f, Z, k)), face);
        }
        static Transform Rx(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitX, a);
        // the head's six, through its spot faces into the flange
        for (int i = 0; i < 6; i++)
        {
            double t = i * Math.PI / 3, sy = 48 * Math.Cos(t), sz = 48 * Math.Sin(t);
            var sc = Screw("M8", "50", T(HmHeadX - HmHeadT + 1 - 8, sy, sz) * Ry(Math.PI / 2));
            Seat(sc, 8, 8, F(head, f => HmCyl(f, 4.25, X, p => Math.Abs(p.Y - sy) < 1 && Math.Abs(p.Z - sz) < 1)),
                F(head, f => HmPlane(f, -X, -(HmHeadX - HmHeadT + 1), p => Math.Abs(p.Y - sy) < 8 && Math.Abs(p.Z - sz) < 8)));
        }
        // the bearing caps' four, down through the shoulders into the pedestals
        foreach (var (cap, z) in new[] { (cap1, 50.0), (cap2, -50.0) })
            foreach (var x in new[] { -22.0, 22.0 })
            {
                var sc = Screw("M8", "40", T(x, 22, z) * Rx(Math.PI / 2));
                // (the cap's own coordinates: its length along +Z from its inner face, the second cap turned round)
                Seat(sc, 8, 8, F(cap, f => HmCyl(f, 4.25, Y, p => Math.Abs(Math.Abs(p.X) - 22) < 1 && Math.Sign(p.X) == Math.Sign(cap == cap1 ? x : -x))),
                    F(cap, f => HmPlane(f, Y, 14, p => Math.Sign(p.X) == Math.Sign(cap == cap1 ? x : -x))));
            }
        // the rod cap's two, through the cap into the rod's block, nutted
        var rodAt = rod.Placement.ToTransform();
        var nutDoc = HmHexNut(10, 5, 6.4);
        foreach (var by in new[] { -20.0, 20.0 })
        {
            var sc = Screw("M6", "35", rodAt * T(20, by, 0) * Ry(-Math.PI / 2));
            Seat(sc, 6, 6, F(rodCap, f => HmCyl(f, 3.2, X, p => Math.Abs(p.Y - by) < 1)), F(rodCap, f => HmPlane(f, X, 14)));
            var nut = Fitting(nutDoc, "Hex nut", rodAt * T(-16, by, 0) * Ry(-Math.PI / 2));
            m.AddMate(Cc, F(nut, f => HmCyl(f, 3.2, Z)), F(rod, f => HmCyl(f, 3.2, X, p => Math.Abs(p.Y - by) < 1)));
            m.AddMate(Co, F(nut, f => HmPlane(f, -Z, 0)), F(rod, f => HmPlane(f, -X, 16)));
        }
        // the hub caps' four each (through the pulley's web too on the outer flywheel)
        foreach (var (fw, cap, under, screwL) in new[] { (fw1, hubCap1, 23.0, "12"), (fw2, hubCap2, 29.0, "16") })
            for (int i = 0; i < 4; i++)
            {
                double t = Math.PI / 4 + i * Math.PI / 2, hx = 19 * Math.Cos(t), hy = 19 * Math.Sin(t);
                var sc = Screw("M5", screwL, fw.Placement.ToTransform() * T(hx, hy, under + 5) * Rx(Math.PI));
                Seat(sc, 5, 5, F(cap, f => HmCyl(f, 2.75, Z, p => Math.Abs(p.X - hx) < 1 && Math.Abs(p.Y - hy) < 1)),
                    F(cap, f => HmPlane(f, Z, 3, p => Math.Abs(p.X - hx) < 5 && Math.Abs(p.Y - hy) < 5)));
            }
        // the base's hold-down screws into the feet
        foreach (var (foot, x, z) in feet)
        {
            var sc = Screw("M10", "25", T(x, -120, z) * Rx(Math.PI / 2));
            Seat(sc, 10, 10, F(casting, f => HmCyl(f, 5.5, Y, p => Math.Abs(p.X - x) < 1 && Math.Abs(p.Z - z) < 1)),
                F(casting, f => HmPlane(f, Y, -130)));
        }
        return m;
    }

    /// <summary>The assembly as a picture: every component where it is, in its part's colour.</summary>
    static byte[] HmPicture(AssemblyModel m, int size = 900, double turn = 0)
    {
        // the whole engine turned about the vertical (the thumbnail's camera is fixed)
        var view = Transform.Rotation(Vec3.Zero, Vec3.UnitY, turn * Math.PI / 180);
        return Thumbnail.Render(m.Doc.Components.Select(c => (AssemblyModel.PlacedMesh(m.DisplayOf(c)!.Mesh, view * c.Placement.ToTransform()), m.DisplayOf(c)!.Color ?? new[] { 0.7, 0.7, 0.7 })), size);
    }

    /// <summary>
    /// <c>-- engine-asm [outDir]</c>: the engine assembled; the mates checked to hold as placed, then a flywheel turned
    /// two whole turns (as a motor would) checking the piston and the cam gear follow; the assembly and pictures written.
    /// </summary>
    public static int EngineAssembly(string? dir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = HitAndMiss();
        Console.WriteLine($"{m.Doc.Components.Count} components, {m.Doc.Mates.Count} mates, built in {sw.ElapsedMilliseconds} ms");
        int bad = 0;
        foreach (var p in m.Parts.Values.Where(p => p.Problem != null || p.Mesh.Indices.Length == 0)) { Console.WriteLine($"part problem: {p.Problem ?? "empty"}"); bad++; }
        var first = m.SolveMates();
        var failed = first.Failed().Select(id => m.Doc.Mates.First(x => x.Id == id)).Select(x => $"{x.Name} ({x.Type})").ToList();
        Console.WriteLine($"as placed: {(first.Satisfied ? "satisfied" : "NOT satisfied: " + string.Join(", ", failed))}, moved {first.Moved.Count}: "
            + string.Join(", ", first.Moved.Select(id => m.Doc.Find(id)!.Name)));
        if (!first.Satisfied || first.Moved.Count > 0) bad++;
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            foreach (var turn in new[] { 0, 90, 180, 270 })
                File.WriteAllBytes(Path.Combine(dir, $"engine-view{turn}.png"), HmPicture(m, 900, turn));
        }
        // the motor: the +Z flywheel turned about its axis, the rest solved each step
        var fw = m.Doc.Components.First(c => c.Name.StartsWith("Flywheel"));
        var crank = m.Doc.Components.First(c => c.Name.StartsWith("Crankshaft"));
        var piston = m.Doc.Components.First(c => c.Name.StartsWith("Piston"));
        var cam = m.Doc.Components.First(c => c.Name.StartsWith("Cam gear"));
        var valve = m.Doc.Components.First(c => c.Name.StartsWith("Valve") && c.Placement.ToTransform().Point(Vec3.Zero).Z > 0);
        var others = m.Doc.Components.Where(x => !x.Fixed && x != fw).Select(x => x.Id).ToHashSet();
        double Angle(Component c) { var x = c.Placement.ToTransform().Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
        double crank0 = Angle(crank), cam0 = Angle(cam);
        double worstPiston = 0, worstCam = 0, liftMin = double.MaxValue, liftMax = double.MinValue;
        int steps = 0;
        sw.Restart();
        const int perTurn = 36;
        for (int i = 1; i <= 2 * perTurn; i++, steps++)
        {
            fw.Placement = Placement.From(Transform.Rotation(Vec3.Zero, Vec3.UnitZ, 2 * Math.PI / perTurn) * fw.Placement.ToTransform());
            var res = m.SolveMates(others);
            if (!res.Satisfied) { Console.WriteLine($"motor step {i}: mates fail ({string.Join(", ", res.Failed().Select(id => m.Doc.Mates.First(x => x.Id == id).Type))})"); bad++; break; }
            var turned = 2 * Math.PI * i / perTurn;
            var a = Angle(crank);
            worstPiston = Math.Max(worstPiston, Math.Abs(piston.Placement.ToTransform().Point(Vec3.Zero).X - HmPistonX(a)));
            worstCam = Math.Max(worstCam, Math.Abs(Math.IEEERemainder(Angle(cam) - cam0 + turned / 2, 2 * Math.PI)));
            var lift = valve.Placement.ToTransform().Point(Vec3.Zero).X - HmHeadX;
            liftMin = Math.Min(liftMin, lift); liftMax = Math.Max(liftMax, lift);
            if (dir != null && i % 9 == 0) File.WriteAllBytes(Path.Combine(dir, $"engine-{i:00}.png"), HmPicture(m, 500));
        }
        Console.WriteLine($"{steps} motor steps in {sw.ElapsedMilliseconds} ms: piston off the kinematics by {worstPiston:0.######}, cam gear off half the crank's turn by {worstCam:0.######} rad, exhaust valve lift {liftMin:0.###} to {liftMax:0.###} mm");
        Console.WriteLine($"crank back to {Math.IEEERemainder(Angle(crank) - crank0, 2 * Math.PI):0.######} rad of where it started");
        if (worstPiston > 1e-4 || worstCam > 1e-4 || liftMin < -1e-4) bad++;
        if (dir != null) m.Save(Path.Combine(dir, "hit-and-miss.sasm"));
        return bad;
    }

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
            Console.WriteLine($"{sw.ElapsedMilliseconds,7} ms {(st.Ok ? "" : st.Error)} {string.Join("; ", st.Warnings)}");
        }
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

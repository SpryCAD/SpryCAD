using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// The hit and miss engine put together as a designer would: sub-assemblies for what is built (and moves) as a unit,
/// each mated in its own coordinates, and the engine mating them to the casting.
/// </summary>
/// <remarks>
/// Sub-assemblies, all embedded: a flywheel with its governor's weights, hub cap and screws; the other flywheel with
/// the belt pulley; the crankshaft with its gear and both flywheels (rigid: they turn as one); the connecting rod with
/// its cap, screws and nuts; the piston with its wrist pin; a main bearing's cap with its oil cup and screws; the drip
/// oiler; and the cylinder head with its valves, springs, rocker, spark plug, muffler, mixer and screws (flexible: the
/// rocker and the exhaust valve move in it).
/// </remarks>
static partial class Demos
{
    /// <summary>A face of the part at a path in an assembly, picked by its geometry (in the part's coordinates).</summary>
    static MateRef HmFace(AssemblyModel m, string path, Func<FaceRange, bool> which)
    {
        var faces = m.DisplayAt(path)!.Mesh.Faces;
        var f = faces.FirstOrDefault(which) ?? throw new InvalidOperationException($"no such face on {m.NameAt(path)}: " + string.Join("; ", faces.Select(x =>
            $"{x.Name} {x.Surface.Kind} r={x.Surface.Radius:0.###} ax={x.Surface.Axis} n={x.FirstPlane.Normal} o={x.FirstPlane.Origin} c={x.Bounds.Center}")));
        return m.RefTo(path, f.Name)!;
    }

    /// <summary>An assembly being built: its components (parts and sub-assemblies embedded), faces picked, screws seated.</summary>
    sealed class HmAsm
    {
        public readonly AssemblyModel M = AssemblyModel.New();
        static readonly Vec3 Z = Vec3.UnitZ;

        /// <summary>A part (the first component fixed, the others floating).</summary>
        public Component Add(CadDocument doc, string name, Transform t)
        {
            var c = M.InsertEmbedded(doc, name, t);
            c.Fixed = M.Doc.Components.Count == 1;
            return c;
        }

        /// <summary>A sub-assembly saved at <paramref name="file"/>, embedded (nothing points back at the file).</summary>
        public Component Sub(string file, Transform t, bool flexible = false)
        {
            var c = M.InsertAssembly(file, t, flexible, embed: true);
            c.Fixed = M.Doc.Components.Count == 1;
            c.Part.Path = null; c.Part.AbsolutePath = null;
            return c;
        }

        public MateRef F(Component c, Func<FaceRange, bool> which) => HmFace(M, c.Id, which);
        /// <summary>A face of a component of a sub-assembly.</summary>
        public MateRef F(Component sub, Component inner, Func<FaceRange, bool> which) => HmFace(M, sub.Id + "/" + inner.Id, which);
        public MateRef Any(Component c) => M.RefTo(c.Id, M.DisplayOf(c)!.Mesh.Faces[0].Name)!;
        public MateRef Any(Component sub, Component inner) => M.RefTo(sub.Id + "/" + inner.Id, M.DisplayAt(sub.Id + "/" + inner.Id)!.Mesh.Faces[0].Name)!;
        public MateRef Plane(string std) => M.RefTo(null, std)!;
        public Mate Mate(MateType type, MateRef a, MateRef b, double value = 0, Action<Mate>? setup = null) => M.AddMate(type, a, b, value, setup);

        /// <summary>A socket head cap screw of the configurable part, configured to a size and length.</summary>
        public Component Screw(CadDocument screwDoc, string size, string length, Transform t)
        {
            var c = Add(screwDoc, "Socket screw", t);
            M.Configure(new[] { c }, screwDoc, new Dictionary<string, string> { ["Size"] = size, ["L"] = length });
            return c;
        }

        /// <summary>A screw on its hole: the shank on the hole's axis, the head's underside (k up its axis) on the face.</summary>
        public void Seat(Component screw, double d, double k, MateRef hole, MateRef face)
        {
            Mate(MateType.Concentric, F(screw, f => HmCyl(f, d / 2, Z)), hole);
            Mate(MateType.Coincident, F(screw, f => HmPlane(f, Z, k)), face);
        }

        public string Save(string dir, string name)
        {
            var p = Path.Combine(dir, name + AssemblyDocument.FileExtension);
            M.Save(p);
            return p;
        }
    }

    static Transform HmT(double x, double y, double z) => Transform.Translation(new Vec3(x, y, z));
    static Transform HmRx(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitX, a);
    static Transform HmRy(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitY, a);
    static Transform HmRz(double a) => Transform.Rotation(Vec3.Zero, Vec3.UnitZ, a);

    /// <summary>
    /// A flywheel (keyed bore along Z, its spokes on z = 0) with its brass hub cap held by four screws (a circular
    /// pattern of one), and either the governor's two weights on the spokes or the belt pulley under the hub cap.
    /// </summary>
    static string HmFlywheelAssembly(string dir, CadDocument screwDoc, bool pulley)
    {
        var a = new HmAsm();
        var (X, Y, Z) = (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        var fw = a.Add(HmFlywheel(), "Flywheel", Transform.Identity);
        var bore = a.F(fw, f => HmCyl(f, HmJ / 2, Z));
        var hubFace = a.F(fw, f => HmPlane(f, Z, 20, p => new Vec3(p.X, p.Y, 0).Length < 40));
        bool At45(Vec3 p) => p.X > 0 && p.Y > 0 && Math.Abs(p.X - p.Y) < 1;
        var tapped = a.F(fw, f => HmCyl(f, 2.1, Z, At45));
        double capAt = 20;
        if (pulley)
        {
            // the pulley's web on the hub (the pulley is turned along Y in its part)
            var pu = a.Add(HmPulley(), "Pulley", HmT(0, 0, 20) * HmRx(Math.PI / 2));
            a.Mate(MateType.Concentric, a.F(pu, f => HmCyl(f, 13.5, Y)), bore);
            a.Mate(MateType.Coincident, a.F(pu, f => HmPlane(f, -Y, 0)), hubFace);
            a.Mate(MateType.Concentric, a.F(pu, f => HmCyl(f, 2.75, Y, p => p.X > 0 && p.Z < 0)), tapped);
            hubFace = a.F(pu, f => HmPlane(f, Y, 6));
            tapped = a.F(pu, f => HmCyl(f, 2.75, Y, p => p.X > 0 && p.Z < 0));
            capAt = 26;
        }
        var cap = a.Add(HmHubCap(), "Hub cap", HmT(0, 0, capAt));
        a.Mate(MateType.Concentric, a.F(cap, f => HmCyl(f, 13.5, Z)), bore);
        a.Mate(MateType.Coincident, a.F(cap, f => HmPlane(f, -Z, 0)), hubFace);
        a.Mate(MateType.Concentric, a.F(cap, f => HmCyl(f, 2.75, Z, At45)), tapped);
        double h = 19 * Math.Cos(Math.PI / 4);
        var screw = a.Screw(screwDoc, "M5", pulley ? "16" : "12", HmT(h, h, capAt + 3 + 5) * HmRx(Math.PI));
        a.Seat(screw, 5, 5, a.F(cap, f => HmCyl(f, 2.75, Z, At45)), a.F(cap, f => HmPlane(f, Z, 3, At45)));
        a.M.AddPattern(PatternKind.Circular, new[] { screw }, bore, 4, 90);
        if (!pulley)
        {
            // the governor's weights on two opposite spokes (pinned to them: they do not swing, see the notes)
            var wt = a.Add(HmGovernorWeight(), "Governor weight", HmT(55, 0, 13) * HmRz(Math.PI / 2));
            a.Mate(MateType.Lock, a.Any(fw), a.Any(wt));
            a.M.AddPattern(PatternKind.Circular, new[] { wt }, bore, 2, 180);
        }
        return a.Save(dir, pulley ? "Flywheel, pulley side" : "Flywheel, governor side");
    }

    /// <summary>
    /// The crankshaft (its axis Z, the crank pin on +X) with the crank gear and both flywheels keyed to it (locks), each
    /// flywheel a sub-assembly of its own.
    /// </summary>
    static (string file, Component crank, Component gear) HmCrankAssembly(string dir, CadDocument screwDoc)
    {
        var a = new HmAsm();
        var Z = Vec3.UnitZ;
        var crank = a.Add(HmCrankshaft(), "Crankshaft", Transform.Identity);
        var journal = a.F(crank, f => HmCyl(f, HmJ / 2, Z, p => p.Z > HmBrgOut));
        var gear = a.Add(HmGear(HmZCrank, HmJ), "Crank gear", HmT(0, 0, HmGearZ) * HmRz(Math.PI / 2));
        a.Mate(MateType.Concentric, a.F(gear, f => HmCyl(f, HmJ / 2, Z)), journal);
        a.Mate(MateType.Lock, a.Any(crank), a.Any(gear));
        var governor = a.Sub(HmFlywheelAssembly(dir, screwDoc, pulley: false), HmT(0, 0, HmFwZ));
        var pulley = a.Sub(HmFlywheelAssembly(dir, screwDoc, pulley: true), HmT(0, 0, -HmFwZ) * HmRy(Math.PI));
        foreach (var fw in new[] { governor, pulley })
        {
            // the flywheel itself is the sub-assembly's first component
            var flywheel = a.M.SubOf(fw)!.Model!.Doc.Components[0];
            a.Mate(MateType.Concentric, a.F(fw, flywheel, f => HmCyl(f, HmJ / 2, Z)), journal);
            a.Mate(MateType.Lock, a.Any(crank), a.Any(fw, flywheel));
        }
        return (a.Save(dir, "Crankshaft assembly"), crank, gear);
    }

    /// <summary>The connecting rod (its big end on the origin, the small end along -X) with its cap, two screws and nuts (a pattern of one pair).</summary>
    static string HmRodAssembly(string dir, CadDocument screwDoc)
    {
        var a = new HmAsm();
        var (X, Z) = (Vec3.UnitX, Vec3.UnitZ);
        var rod = a.Add(HmConRod(), "Connecting rod", Transform.Identity);
        var cap = a.Add(HmRodCap(), "Rod cap", Transform.Identity);
        bool Lo(Vec3 p) => p.Y < -10;
        bool Hi(Vec3 p) => p.Y > 10;
        a.Mate(MateType.Coincident, a.F(cap, f => HmPlane(f, -X, 0)), a.F(rod, f => HmPlane(f, X, 0)));
        a.Mate(MateType.Concentric, a.F(cap, f => HmCyl(f, 3.2, X, Lo)), a.F(rod, f => HmCyl(f, 3.2, X, Lo)));
        a.Mate(MateType.Concentric, a.F(cap, f => HmCyl(f, 3.2, X, Hi)), a.F(rod, f => HmCyl(f, 3.2, X, Hi)));
        var screw = a.Screw(screwDoc, "M6", "35", HmT(20, -20, 0) * HmRy(-Math.PI / 2));
        a.Seat(screw, 6, 6, a.F(cap, f => HmCyl(f, 3.2, X, Lo)), a.F(cap, f => HmPlane(f, X, 14)));
        var nut = a.Add(HmHexNut(10, 5, 6.4), "Hex nut", HmT(-16, -20, 0) * HmRy(-Math.PI / 2));
        a.Mate(MateType.Concentric, a.F(nut, f => HmCyl(f, 3.2, Z)), a.F(rod, f => HmCyl(f, 3.2, X, Lo)));
        a.Mate(MateType.Coincident, a.F(nut, f => HmPlane(f, -Z, 0)), a.F(rod, f => HmPlane(f, -X, 16)));
        a.M.AddPattern(PatternKind.Linear, new[] { screw, nut }, a.Plane("Top"), 2, 40);
        return a.Save(dir, "Connecting rod assembly");
    }

    /// <summary>The piston (its wrist pin's axis Z through the origin) with the wrist pin, centred.</summary>
    static string HmPistonAssembly(string dir)
    {
        var a = new HmAsm();
        var Z = Vec3.UnitZ;
        var piston = a.Add(HmPiston(), "Piston", Transform.Identity);
        var pin = a.Add(HmWristPin(), "Wrist pin", Transform.Identity);
        a.Mate(MateType.Concentric, a.F(pin, f => HmCyl(f, HmWp / 2, Z)), a.F(piston, f => HmCyl(f, HmWp / 2, Z)));
        a.Mate(MateType.Coincident, a.F(pin, f => HmPlane(f, -Z, (HmBore - 6) / 2)), a.Plane("Front"), (HmBore - 6) / 2);
        return a.Save(dir, "Piston assembly");
    }

    /// <summary>A main bearing's cap (its split face on y = 0, its length along +Z) with its oil cup and two screws (a pattern of one).</summary>
    static string HmCapAssembly(string dir, CadDocument screwDoc)
    {
        var a = new HmAsm();
        var Y = Vec3.UnitY;
        var cap = a.Add(HmBearingCap(), "Bearing cap", Transform.Identity);
        var cup = a.Add(HmOilCup(), "Oil cup", HmT(0, 35, 20));
        a.Mate(MateType.Concentric, a.F(cup, f => HmCyl(f, 3.4, Y)), a.F(cap, f => HmCyl(f, 3.4, Y)));
        a.Mate(MateType.Coincident, a.F(cup, f => HmPlane(f, -Y, 0)), a.F(cap, f => HmPlane(f, Y, 35)));
        var screw = a.Screw(screwDoc, "M8", "40", HmT(-25, 22, 20) * HmRx(Math.PI / 2));
        a.Seat(screw, 8, 8, a.F(cap, f => HmCyl(f, 4.25, Y, p => p.X < 0)), a.F(cap, f => HmPlane(f, Y, 14, p => p.X < 0)));
        a.M.AddPattern(PatternKind.Linear, new[] { screw }, a.Plane("Right"), 2, 50);
        return a.Save(dir, "Bearing cap assembly");
    }

    /// <summary>The drip oiler with its sight glass, up from y = 0.</summary>
    static string HmOilerAssembly(string dir)
    {
        var a = new HmAsm();
        var Y = Vec3.UnitY;
        var oiler = a.Add(HmOiler(), "Oiler", Transform.Identity);
        var glass = a.Add(HmOilerGlass(), "Sight glass", HmT(0, 8, 0));
        a.Mate(MateType.Concentric, a.F(glass, f => HmCyl(f, 8, Y)), a.F(oiler, f => HmCyl(f, 3, Y)));
        a.Mate(MateType.Coincident, a.F(glass, f => HmPlane(f, -Y, 0)), a.F(oiler, f => HmPlane(f, Y, 8)));
        return a.Save(dir, "Drip oiler");
    }

    /// <summary>
    /// The cylinder head in the engine's coordinates (it is modeled there), with what it carries: the valves in their
    /// guides (the exhaust on the rocker, the intake closed), springs and retainers, the rocker on its pin, the spark
    /// plug and boot, the muffler and its ring, the mixer and its needle valve, and its six screws (a circular pattern
    /// of one). Flexible in the engine: the rocker and the exhaust valve move.
    /// </summary>
    static (string file, Component head, Component rocker, Component exhaust) HmHeadAssembly(string dir, CadDocument screwDoc)
    {
        var a = new HmAsm();
        var (X, Y, Z) = (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        const MateType Co = MateType.Coincident, Cc = MateType.Concentric, Lk = MateType.Lock, Tg = MateType.Tangent;
        var head = a.Add(HmHead(), "Cylinder head", Transform.Identity);
        double rockerX = HmHeadX - HmHeadT - 35, pivotZ = (HmValveZ + HmPushZ) / 2;
        var rocker = a.Add(HmRocker(), "Rocker", HmT(rockerX, 0, pivotZ));
        var pin = a.Add(HmPivotPin(), "Rocker pin", HmT(rockerX, 11.5, pivotZ));
        var valveDoc = HmValve(); var springDoc = HmValveSpring(); var retDoc = HmRetainer();
        var exhaust = a.Add(valveDoc, "Valve", HmT(HmHeadX, 0, HmValveZ));
        var intake = a.Add(valveDoc, "Valve", HmT(HmHeadX, 0, -HmValveZ));
        double springAt = HmHeadX - HmHeadT - 6;
        // the rocker on its pin between the cheeks, the pin's head on the upper cheek
        a.Mate(Cc, a.F(rocker, f => HmCyl(f, 4.1, Y)), a.F(head, f => HmCyl(f, 4.1, Y)));
        a.Mate(Co, a.F(rocker, f => HmPlane(f, Y, 5)), a.F(head, f => HmPlane(f, -Y, -5.5)), 0.5);
        a.Mate(Cc, a.F(pin, f => HmCyl(f, 4, Y)), a.F(head, f => HmCyl(f, 4.1, Y)));
        a.Mate(Co, a.F(pin, f => HmPlane(f, -Y, 0)), a.F(head, f => HmPlane(f, Y, 11.5)));
        // the valves in their guides: the exhaust on the rocker's tip, the intake (opened by the suction) closed
        foreach (var (v, z) in new[] { (exhaust, HmValveZ), (intake, -HmValveZ) })
        {
            a.Mate(Cc, a.F(v, f => HmCyl(f, 3, X, p => p.X < -20)), a.F(head, f => HmCyl(f, 3.1, X, p => Math.Abs(p.Z - z) < 1)));
            var spring = a.Add(springDoc, "Valve spring", HmT(springAt, 0, z));
            a.Mate(Lk, a.Any(head), a.Any(spring));
            var ret = a.Add(retDoc, "Retainer", HmT(springAt - 16, 0, z));
            a.Mate(Lk, a.Any(v), a.Any(ret));
        }
        a.Mate(Tg, a.F(rocker, f => HmCyl(f, 4, Y, p => p.Z < 0)), a.F(exhaust, f => HmPlane(f, -X, 65)));
        a.Mate(Co, a.F(intake, f => HmPlane(f, X, 0)), a.F(head, f => HmPlane(f, X, HmHeadX)));
        // the spark plug and its boot
        var plug = a.Add(HmSparkPlug(), "Spark plug", HmT(HmHeadX - HmHeadT, -18, 0));
        a.Mate(Cc, a.F(plug, f => HmCyl(f, 6.25, X)), a.F(head, f => HmCyl(f, 6.25, X)));
        a.Mate(Co, a.F(plug, f => HmPlane(f, X, 0)), a.F(head, f => HmPlane(f, -X, -(HmHeadX - HmHeadT))));
        var boot = a.Add(HmPlugBoot(), "Plug boot", HmT(HmHeadX - HmHeadT - 22, -18, 0));
        a.Mate(Lk, a.Any(plug), a.Any(boot));
        // the exhaust: the muffler on its boss, its ring on top
        var muffler = a.Add(HmMuffler(), "Muffler", HmT(-210, 62, HmValveZ));
        a.Mate(Cc, a.F(muffler, f => HmCyl(f, 9, Y)), a.F(head, f => HmCyl(f, 9, Y)));
        a.Mate(Co, a.F(muffler, f => HmPlane(f, -Y, 0)), a.F(head, f => HmPlane(f, Y, 62)));
        var ring = a.Add(HmMufflerCap(), "Muffler ring", HmT(-210, 62 + 190, HmValveZ));
        a.Mate(Cc, a.F(ring, f => HmCyl(f, 8, Y)), a.F(muffler, f => HmCyl(f, 8, Y)));
        a.Mate(Co, a.F(ring, f => HmPlane(f, -Y, 0)), a.F(muffler, f => HmPlane(f, Y, 190)));
        // the mixer on the intake's boss, upright, its needle valve in its end
        var mixerAt = HmT(-210, 0, -62) * HmRy(Math.PI);
        var mixer = a.Add(HmMixer(), "Mixer", mixerAt);
        a.Mate(Cc, a.F(mixer, f => HmCyl(f, 9, Z)), a.F(head, f => HmCyl(f, 9, Z)));
        a.Mate(Co, a.F(mixer, f => HmPlane(f, -Z, 0)), a.F(head, f => HmPlane(f, -Z, 62)));
        a.Mate(MateType.Parallel, a.F(mixer, f => HmPlane(f, Y, 38)), a.Plane("Top"));
        var needle = a.Add(HmNeedleValve(), "Needle valve", mixerAt * HmT(0, 0, 40));
        a.Mate(Cc, a.F(needle, f => HmCyl(f, 3, Z)), a.F(mixer, f => HmCyl(f, 3, Z)));
        a.Mate(Co, a.F(needle, f => HmPlane(f, -Z, 0)), a.F(mixer, f => HmPlane(f, Z, 40)));
        // the six screws through its spot faces into the cylinder's flange: one, patterned round the head's axis
        var under = HmHeadX - HmHeadT + 1;
        var screw = a.Screw(screwDoc, "M8", "50", HmT(under - 8, 48, 0) * HmRy(Math.PI / 2));
        a.Seat(screw, 8, 8, a.F(head, f => HmCyl(f, 4.25, X, p => p.Y > 40)), a.F(head, f => HmPlane(f, -X, -under, p => p.Y > 40)));
        a.M.AddPattern(PatternKind.Circular, new[] { screw }, a.F(head, f => HmCyl(f, 58, X)), 6, 60);
        return (a.Save(dir, "Cylinder head assembly"), head, rocker, exhaust);
    }

    /// <summary>
    /// The engine: the casting (fixed), the crankshaft assembly in its bearings, the bearing caps, the rod and piston,
    /// the cylinder head (flexible), the cam gear on its stud meshing 2:1 with the crank gear, the push rod between the
    /// cam gear's eccentric and the rocker, and the fittings on the casting. Placed where they belong at the crank
    /// angle <see cref="HmCrank0"/> (the gears' teeth mesh there); turn the crankshaft and it all runs.
    /// </summary>
    static AssemblyModel HitAndMiss()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try { return HitAndMiss(dir); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static AssemblyModel HitAndMiss(string dir)
    {
        var (X, Y, Z) = (Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
        const MateType Co = MateType.Coincident, Cc = MateType.Concentric, Lk = MateType.Lock, Tg = MateType.Tangent;
        var screwDoc = SocketScrew();
        double th = HmCrank0, rj = HmJ / 2;
        var pinAt = new Vec3(HmR * Math.Cos(th), HmR * Math.Sin(th), 0);
        var px = HmPistonX(th);
        var rodDir = Math.Atan2(-pinAt.Y, px - pinAt.X);

        // the sub-assemblies, each saved (and embedded below)
        var (crankFile, crankPart, crankGear) = HmCrankAssembly(dir, screwDoc);
        var rodFile = HmRodAssembly(dir, screwDoc);
        var pistonFile = HmPistonAssembly(dir);
        var capFile = HmCapAssembly(dir, screwDoc);
        var oilerFile = HmOilerAssembly(dir);
        var (headFile, headPart, rockerPart, exhaustPart) = HmHeadAssembly(dir, screwDoc);

        var a = new HmAsm();
        var casting = a.Add(HmCasting(), "Casting", Transform.Identity);
        var crank = a.Sub(crankFile, HmRz(th));
        var cap1 = a.Sub(capFile, HmT(0, 0, HmBrgIn));
        var cap2 = a.Sub(capFile, HmT(0, 0, -HmBrgIn) * HmRy(Math.PI));
        var rod = a.Sub(rodFile, HmT(pinAt.X, pinAt.Y, 0) * HmRz(rodDir + Math.PI));
        var piston = a.Sub(pistonFile, HmT(px, 0, 0));
        var head = a.Sub(headFile, Transform.Identity, flexible: true);
        var stud = a.Add(HmCamStud(), "Cam stud", HmT(HmCamX, 0, HmGearZ));
        var camGear = a.Add(HmCamGear(), "Cam gear", HmT(HmCamX, 0, HmGearZ));
        var pushRod = a.Add(HmPushRod(), "Push rod", HmT(HmHeadX - 65, 0, HmPushZ));
        var top = a.Plane("Top");
        Component First(Component sub) => a.M.SubOf(sub)!.Model!.Doc.Components[0];

        // the bearing caps on the pedestals
        foreach (var cap in new[] { cap1, cap2 })
        {
            var part = First(cap);
            var side = cap == cap1 ? 1 : -1;
            a.Mate(Co, a.F(cap, part, f => HmPlane(f, -Y, 0)), top);
            a.Mate(Cc, a.F(cap, part, f => HmCyl(f, rj, Z)), a.F(casting, f => HmCyl(f, rj, Z)));
            a.Mate(Co, a.F(cap, part, f => HmPlane(f, -Z, 0)), a.F(casting, f => HmPlane(f, -side * Z, -HmBrgIn)));
        }
        // the crankshaft in its bearings, a gap to the bearing at the +Z web
        var journal = a.F(crank, crankPart, f => HmCyl(f, rj, Z, p => p.Z > HmBrgOut));
        a.Mate(Cc, journal, a.F(casting, f => HmCyl(f, rj, Z)));
        a.Mate(Co, a.F(crank, crankPart, f => HmPlane(f, Z, HmWebIn + HmWebT)), a.F(casting, f => HmPlane(f, -Z, -HmBrgIn)), HmBrgIn - HmWebIn - HmWebT);
        // the rod on the crank pin beside the +Z web; the piston in the bore on the rod's small end
        var rodPart = First(rod);
        a.Mate(Cc, a.F(rod, rodPart, f => HmCyl(f, HmPin / 2, Z)), a.F(crank, crankPart, f => HmCyl(f, HmPin / 2, Z)));
        a.Mate(Co, a.F(rod, rodPart, f => HmPlane(f, Z, 11, p => p.X > -20)), a.F(crank, crankPart, f => HmPlane(f, -Z, -HmWebIn)), HmWebIn - 11);
        var pistonPart = First(piston);
        var wristPin = a.M.SubOf(piston)!.Model!.Doc.Components[1];
        a.Mate(Cc, a.F(piston, pistonPart, f => HmCyl(f, (HmBore - 0.2) / 2, X)), a.F(casting, f => HmCyl(f, HmBore / 2, X)));
        a.Mate(Cc, a.F(rod, rodPart, f => HmCyl(f, HmWp / 2, Z)), a.F(piston, wristPin, f => HmCyl(f, HmWp / 2, Z)));
        // the head on the cylinder's flange, two of its screw holes on the flange's tapped holes
        a.Mate(Co, a.F(head, headPart, f => HmPlane(f, X, HmHeadX)), a.F(casting, f => HmPlane(f, -X, -HmHeadX)));
        foreach (var yy in new[] { 48.0, -48.0 })
            a.Mate(Cc, a.F(head, headPart, f => HmCyl(f, 4.25, X, p => Math.Abs(p.Y - yy) < 1)),
                a.F(casting, f => HmCyl(f, 3.4, X, p => Math.Abs(p.Y - yy) < 1 && Math.Abs(p.Z) < 1)));
        // the cam gear's stud pressed into its boss; the gear on it beside the crank gear, meshing 2:1
        a.Mate(Cc, a.F(stud, f => HmCyl(f, 8, Z)), a.F(casting, f => HmCyl(f, 8, Z)));
        a.Mate(Lk, a.Any(casting), a.Any(stud));
        var camBore = a.F(camGear, f => HmCyl(f, 8, Z));
        a.Mate(Cc, camBore, a.F(stud, f => HmCyl(f, 8, Z)));
        a.Mate(Co, a.F(camGear, f => HmPlane(f, -Z, 0)), a.F(crank, crankGear, f => HmPlane(f, -Z, 0)));
        a.Mate(MateType.Gear, a.F(crank, crankGear, f => HmCyl(f, rj, Z)), camBore, setup: g => { g.RatioA = HmZCrank; g.RatioB = HmZCam; });
        // the push rod in its lugs, between the eccentric and the rocker
        double len = double.Parse(HmPushRod().Equations.First(q => q.Name == "Len").Expr, System.Globalization.CultureInfo.InvariantCulture);
        a.Mate(Cc, a.F(pushRod, f => HmCyl(f, 4, X)), a.F(casting, f => HmCyl(f, 4.2, X)));
        a.Mate(Tg, a.F(pushRod, f => HmPlane(f, X, len)), a.F(camGear, f => HmCyl(f, HmEccD / 2, Z)));
        a.Mate(Tg, a.F(head, rockerPart, f => HmCyl(f, 4, Y, p => p.Z > 0)), a.F(pushRod, f => HmPlane(f, -X, 0)));

        // the fittings on the casting: the hopper's funnel, the drip oiler, the fuel line, the feet and their screws
        var funnel = a.Add(HmFunnel(), "Funnel", HmT(-130, 82, 0));
        a.Mate(Cc, a.F(funnel, f => HmCyl(f, 21.8, Y)), a.F(casting, f => HmCyl(f, 22, Y)));
        a.Mate(Co, a.F(funnel, f => HmPlane(f, -Y, 0)), a.F(casting, f => HmPlane(f, Y, 82)));
        var oiler = a.Sub(oilerFile, HmT(-77, 56, 0));
        var oilerPart = First(oiler);
        a.Mate(Cc, a.F(oiler, oilerPart, f => HmCyl(f, 3.4, Y)), a.F(casting, f => HmCyl(f, 3.4, Y, p => Math.Abs(p.X + 77) < 1)));
        a.Mate(Co, a.F(oiler, oilerPart, f => HmPlane(f, -Y, 0)), a.F(casting, f => HmPlane(f, Y, 56)));
        var fuel = a.Add(HmFuelLine(new Vec3(-210, -24, -78)), "Fuel line", Transform.Identity);
        a.Mate(Lk, a.Any(casting), a.Any(fuel));
        var footDoc = HmFoot();
        var seeds = new List<Component>();
        foreach (var z in new[] { -70.0, 70.0 })
        {
            const double x = -207;
            var foot = a.Add(footDoc, "Foot", HmT(x, -150, z));
            a.Mate(Cc, a.F(foot, f => HmCyl(f, 4.25, Y)), a.F(casting, f => HmCyl(f, 5.5, Y, p => Math.Abs(p.X - x) < 1 && Math.Abs(p.Z - z) < 1)));
            a.Mate(Co, a.F(foot, f => HmPlane(f, Y, 0)), a.F(casting, f => HmPlane(f, -Y, 150)));
            var screw = a.Screw(screwDoc, "M10", "25", HmT(x, -120, z) * HmRx(Math.PI / 2));
            a.Seat(screw, 10, 10, a.F(casting, f => HmCyl(f, 5.5, Y, p => Math.Abs(p.X - x) < 1 && Math.Abs(p.Z - z) < 1)), a.F(casting, f => HmPlane(f, Y, -130)));
            seeds.Add(foot); seeds.Add(screw);
        }
        a.M.AddPattern(PatternKind.Linear, seeds, a.Plane("Right"), 2, 259);
        return a.M;
    }

    /// <summary>The assembly as a picture: every part where it is, in its colour (the whole turned about the vertical: the camera is fixed).</summary>
    static byte[] HmPicture(AssemblyModel m, int size = 900, double turn = 0)
    {
        var view = HmRy(turn * Math.PI / 180);
        return Thumbnail.Render(m.PlacedParts().Select(x => (AssemblyModel.PlacedMesh(x.pd.Mesh, view * x.o.World), x.o.Color ?? x.pd.Color ?? new[] { 0.7, 0.7, 0.7 })), size);
    }

    /// <summary>
    /// <c>-- engine-asm [outDir]</c>: the engine assembled; the mates checked to hold as placed, then the crankshaft
    /// turned two whole turns (as a motor would) checking the piston, the cam gear and the exhaust valve follow; the
    /// assembly, its bill of materials and pictures written.
    /// </summary>
    public static int EngineAssembly(string? dir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = HitAndMiss();
        var parts = m.PlacedParts().Count();
        Console.WriteLine($"{parts} parts in {m.Doc.Components.Count} top level components, {m.Occurrences().Count(o => o.Component.IsAssembly)} sub-assemblies, built in {sw.ElapsedMilliseconds} ms");
        int bad = 0;
        foreach (var o in m.PlacedParts().Where(x => x.pd.Problem != null || x.pd.Mesh.Indices.Length == 0)) { Console.WriteLine($"part problem: {m.NameAt(o.o.Path)}: {o.pd.Problem ?? "empty"}"); bad++; }
        var first = m.SolveMates();
        Console.WriteLine($"as placed: {(first.Satisfied ? "satisfied" : "NOT satisfied: " + string.Join(", ", first.Failed()))}, {first.Errors.Count} mates, moved {first.Moved.Count}: {string.Join(", ", first.Moved)}");
        if (!first.Satisfied || first.Moved.Count > 0) bad++;
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            foreach (var turn in new[] { 0, 90, 180, 270 })
                File.WriteAllBytes(Path.Combine(dir, $"engine-view{turn}.png"), HmPicture(m, 900, turn));
            File.WriteAllText(Path.Combine(dir, "bom.csv"), Bom.ToCsv(Bom.Build(m, BomKind.Indented)));
            // what overlaps (a screw in its tapped hole does: the hole is the thread's minor diameter)
            var notes = new List<string>();
            var isw = System.Diagnostics.Stopwatch.StartNew();
            var clashes = Interference.Find(Interference.Items(m), notes);
            Console.WriteLine($"interference ({isw.ElapsedMilliseconds} ms): {clashes.Count} clashes{(notes.Count > 0 ? "; " + string.Join("; ", notes) : "")}");
            foreach (var c in clashes) Console.WriteLine($"  {c.Volume,9:0.0} mm3  {m.NameAt(c.A)}  x  {m.NameAt(c.B)}  at {c.Bounds.Center}");
            var mass = AssemblyMass.Of(m);
            Console.WriteLine($"mass {mass.Mass / 1000:0.00} kg, centre of mass {mass.Center}");
        }
        // the motor: the crankshaft assembly turned about its axis, the rest solved each step
        var crank = m.Doc.Components.First(c => c.Name.StartsWith("Crankshaft assembly")).Id;
        var piston = m.Doc.Components.First(c => c.Name.StartsWith("Piston assembly")).Id;
        var cam = m.Doc.Components.First(c => c.Name.StartsWith("Cam gear")).Id;
        var head = m.Doc.Components.First(c => c.Name.StartsWith("Cylinder head assembly"));
        var valve = head.Id + "/" + m.SubOf(head)!.Model!.Doc.Components.First(c => c.Name.StartsWith("Valve") && c.Placement.T.Z > 0).Id;
        var others = m.MovableComponents().Where(p => p != crank).ToHashSet();
        double Angle(string path) { var x = m.WorldOf(path).Vector(Vec3.UnitX); return Math.Atan2(x.Y, x.X); }
        double crank0 = Angle(crank), cam0 = Angle(cam);
        double worstPiston = 0, worstCam = 0, liftMin = double.MaxValue, liftMax = double.MinValue;
        int steps = 0;
        sw.Restart();
        const int perTurn = 36;
        for (int i = 1; i <= 2 * perTurn; i++, steps++)
        {
            m.SetWorld(crank, HmRz(2 * Math.PI / perTurn) * m.WorldOf(crank));
            var res = m.SolveMates(others);
            if (!res.Satisfied) { Console.WriteLine($"motor step {i}: mates fail ({string.Join(", ", res.Failed())})"); bad++; break; }
            var turned = 2 * Math.PI * i / perTurn;
            worstPiston = Math.Max(worstPiston, Math.Abs(m.WorldOf(piston).T.X - HmPistonX(Angle(crank))));
            worstCam = Math.Max(worstCam, Math.Abs(Math.IEEERemainder(Angle(cam) - cam0 + turned / 2, 2 * Math.PI)));
            var lift = m.WorldOf(valve).T.X - HmHeadX;
            liftMin = Math.Min(liftMin, lift); liftMax = Math.Max(liftMax, lift);
            if (dir != null && i % 9 == 0)
            {
                File.WriteAllBytes(Path.Combine(dir, $"engine-{i:00}.png"), HmPicture(m, 500, 90));
                // anything running into anything as it turns (screws in their tapped holes aside); a part whose placed
                // body cannot even be bounded is named (the check itself throws on it)
                var items = Interference.Items(m);
                foreach (var it in items)
                    foreach (var s in AssemblySolids.Of(it.Display))
                        try { _ = s.Native?.Transformed(it.World).Bounds; }
                        catch (Exception ex) { Console.WriteLine($"  step {i}: {m.NameAt(it.Path)} placed at {it.World.T}: {ex.Message}"); bad++; }
                List<Clash> clashes;
                try { clashes = Interference.Find(items, new List<string>()); }
                catch (Exception ex) { Console.WriteLine($"  step {i}: interference check failed: {ex.Message}"); bad++; continue; }
                var moving = clashes
                    .Where(c => !new[] { c.A, c.B }.Any(p => m.NameAt(p).Contains("Socket screw") || m.NameAt(p).Contains("Valve spring"))).ToList();
                foreach (var c in moving) Console.WriteLine($"  step {i}: {c.Volume:0.0} mm3  {m.NameAt(c.A)}  x  {m.NameAt(c.B)}  at {c.Bounds.Center}");
                if (moving.Count > 0) bad++;
            }
        }
        Console.WriteLine($"{steps} motor steps in {sw.ElapsedMilliseconds} ms: piston off the kinematics by {worstPiston:0.######}, cam gear off half the crank's turn by {worstCam:0.######} rad, exhaust valve lift {liftMin:0.###} to {liftMax:0.###} mm");
        Console.WriteLine($"crank back to {Math.IEEERemainder(Angle(crank) - crank0, 2 * Math.PI):0.######} rad of where it started");
        if (worstPiston > 1e-4 || worstCam > 1e-4 || liftMin < -1e-4 || liftMax < 5) bad++;
        if (dir != null) m.Save(Path.Combine(dir, "hit-and-miss.sasm"));
        return bad;
    }
}

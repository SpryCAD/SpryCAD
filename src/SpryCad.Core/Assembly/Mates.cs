using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// The mates: SolidWorks' standard ones (Coincident, Parallel, Perpendicular, Tangent, Concentric, Lock, Distance,
/// Angle — Distance and Angle with optional limits) and the mechanical Gear and Screw.
/// </summary>
/// <remarks>
/// Distance is not a type of its own any more: a Coincident with an offset (its <see cref="Mate.Value"/>) or limits
/// holds the references that far apart. Distance stays for files saved before; they load as such Coincidents.
/// </remarks>
public enum MateType { Coincident, Concentric, Parallel, Perpendicular, Distance, Angle, Tangent, Lock, Gear, Screw }

/// <summary>What a mate reference is, geometrically.</summary>
public enum MateGeomKind { Plane, Axis, Point }

/// <summary>
/// A mate reference's geometry in its part's coordinates: a plane (a point on it and its normal, out of the
/// material for a face), an axis (a point on it and its direction: cylinders, cones; R the cylinder's radius) or a
/// point (sphere centres; R the sphere's radius).
/// </summary>
public sealed record MateGeom(MateGeomKind Kind, Vec3 P, Vec3 D, double R = 0)
{
    /// <summary>The geometry of a display face: planes and the axes of cylinders and cones; points for spheres.</summary>
    public static MateGeom? Of(FaceRange f)
    {
        var s = f.Surface;
        return s.Kind switch
        {
            SurfaceKind.Plane => new MateGeom(MateGeomKind.Plane, f.FirstPlane.Origin, f.FirstPlane.Normal),
            SurfaceKind.Cylinder => new MateGeom(MateGeomKind.Axis, s.Origin, s.Axis.Normalized, s.Radius),
            SurfaceKind.Cone => new MateGeom(MateGeomKind.Axis, s.Origin, s.Axis.Normalized),
            SurfaceKind.Sphere => new MateGeom(MateGeomKind.Point, s.Origin, Vec3.UnitZ, s.Radius),
            _ => null,
        };
    }

    /// <summary>Carried by a placement.</summary>
    public MateGeom Placed(in Transform t) => this with { P = t.Point(P), D = Kind == MateGeomKind.Point ? D : t.Vector(D).Normalized };

    /// <summary>As a value (the solver's: placed many times a step, never kept).</summary>
    internal Geom Value => new(Kind, P, D, R);
}

/// <summary>A <see cref="MateGeom"/> as a value: what the mates' residuals and their derivatives work on.</summary>
internal readonly record struct Geom(MateGeomKind Kind, Vec3 P, Vec3 D, double R)
{
    public Geom Placed(in Transform t) => new(Kind, t.Point(P), Kind == MateGeomKind.Point ? D : t.Vector(D).Normalized, R);
}

/// <summary>
/// One side of a mate: a face of a component's part (its name, as the part names its faces), or an origin plane of
/// the assembly (Component null, Face "Front" / "Top" / "Right"). The geometry as it was when last seen is kept
/// (in the part's coordinates): the mate still solves when the part is lightweight, missing, or lost that face.
/// </summary>
public sealed class MateRef
{
    public string? Component { get; set; }
    public string Face { get; set; } = "";
    public MateGeomKind Kind { get; set; }
    public Vec3 P { get; set; }
    public Vec3 D { get; set; }
    /// <summary>The radius of a cylinder or sphere (0: none).</summary>
    public double R { get; set; }
    /// <summary>
    /// The center of the round face's circle at one end of it along its axis (+1: the far end, -1: the near one), as a
    /// point: a hole's or a boss's center. Null: the face itself.
    /// </summary>
    public int? End { get; set; }

    public MateGeom Geom => new(Kind, P, D, R);
    public MateRef Clone() => (MateRef)MemberwiseClone();
}

/// <summary>A mate: two references held in a relation (and its values).</summary>
public sealed class Mate
{
    public string Id { get; set; } = "";
    /// <summary>Shown in the tree ("Coincident1").</summary>
    public string Name { get; set; } = "";
    public MateType Type { get; set; }
    public MateRef A { get; set; } = new();
    public MateRef B { get; set; } = new();
    /// <summary>The offset (mm) of a Coincident (0: touching), the angle (degrees) of an Angle, the lead (mm per turn) of a Screw.</summary>
    public double Value { get; set; }

    /// <summary>A Distance (from files saved before it was folded in) made the Coincident with an offset it is.</summary>
    public static void Normalize(Mate m)
    {
        if (m.Type == MateType.Distance) m.Type = MateType.Coincident;
    }
    /// <summary>
    /// The directions the same way (aligned) or opposite (anti-aligned: faces against each other). Chosen when the
    /// mate is made from how the components lie (the nearer), flipped by the user.
    /// </summary>
    public bool Aligned { get; set; }
    /// <summary>For distances (and tangency to a plane): which side of A's plane B is (+1 along A's normal, -1 against it).</summary>
    public int Side { get; set; } = 1;
    public bool Suppressed { get; set; }

    /// <summary>Distance / Angle with limits: the value may be anywhere from Min to Max (the mate holds it in that range).</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>Tangent between two cylinders or spheres: one inside the other (else outside each other).</summary>
    public bool Inside { get; set; }
    /// <summary>Gear: A turns RatioB / RatioA times as B (teeth or pitch diameters: the turns go the other way round).</summary>
    public double RatioA { get; set; } = 1;
    public double RatioB { get; set; } = 1;
    /// <summary>Gear: both turning the same way (an internal gear, a belt); Screw: a left-hand thread.</summary>
    public bool Reverse { get; set; }
    /// <summary>Lock: where B is relative to A.</summary>
    public Placement? Rel { get; set; }
    /// <summary>Gear and Screw: a direction across each axis, in its part (where each turn is measured from).</summary>
    public Vec3 UA { get; set; }
    public Vec3 UB { get; set; }
    /// <summary>Gear: where those directions pointed (world) when the mate was made.</summary>
    public Vec3 W0A { get; set; }
    public Vec3 W0B { get; set; }
    /// <summary>Screw: B's offset along A's axis and its turn relative to A when the mate was made.</summary>
    public double S0 { get; set; }
    public double Theta0 { get; set; }
    /// <summary>
    /// Gear: how far A and B have turned since (radians, whole turns counted); Screw: B's turn relative to A. Kept
    /// as the components turn, so a turn past half a revolution is not mistaken for one the other way.
    /// </summary>
    public double AngleA { get; set; }
    public double AngleB { get; set; }

    public Mate Clone()
    {
        var m = (Mate)MemberwiseClone();
        m.A = A.Clone(); m.B = B.Clone();
        return m;
    }

    /// <summary>Whether this type can relate these kinds of references (in either order).</summary>
    public static bool Supports(MateType t, MateGeomKind a, MateGeomKind b) => Supports(t, new MateGeom(a, default, default), new MateGeom(b, default, default), radii: false);

    /// <summary>Whether this type can relate these references (tangency needs their radii).</summary>
    public static bool Supports(MateType t, MateGeom a, MateGeom b, bool radii = true)
    {
        bool Pair(MateGeomKind x, MateGeomKind y) => (a.Kind == x && b.Kind == y) || (a.Kind == y && b.Kind == x);
        bool Round(MateGeom g) => !radii || g.Kind == MateGeomKind.Plane || g.R > 0;
        return t switch
        {
            MateType.Coincident or MateType.Distance or MateType.Lock => true,
            MateType.Concentric => Pair(MateGeomKind.Axis, MateGeomKind.Axis) || Pair(MateGeomKind.Axis, MateGeomKind.Point) || Pair(MateGeomKind.Point, MateGeomKind.Point),
            MateType.Parallel or MateType.Perpendicular or MateType.Angle => a.Kind != MateGeomKind.Point && b.Kind != MateGeomKind.Point,
            MateType.Tangent => !Pair(MateGeomKind.Plane, MateGeomKind.Plane) && Round(a) && Round(b),
            MateType.Gear or MateType.Screw => Pair(MateGeomKind.Axis, MateGeomKind.Axis),
            _ => false,
        };
    }

    static double Wrap(double a) => Math.IEEERemainder(a, 2 * Math.PI);

    /// <summary>Lock: three points of B's part held where A's placement and the locked relation put them.</summary>
    static readonly Vec3[] LockPoints = { Vec3.Zero, new(10, 0, 0), new(0, 10, 0) };

    /// <summary>The signed angle (about axis d) from direction u to direction v, both taken across d.</summary>
    static double AngleAbout(Vec3 u, Vec3 v, Vec3 d)
    {
        u -= d * Vec3.Dot(u, d); v -= d * Vec3.Dot(v, d);
        return Math.Atan2(Vec3.Dot(Vec3.Cross(u, v), d), Vec3.Dot(u, v));
    }

    /// <summary>A direction across an axis (in its part), where turns about it are measured from.</summary>
    static Vec3 Across(Vec3 d) => d.AnyPerpendicular().Normalized;

    /// <summary>Gear: how far each component has turned about its axis since the mate was made (continuous).</summary>
    (double a, double b) GearAngles(in Geom a, in Geom b, in Transform ta, in Transform tb) =>
        (AngleA + Wrap(AngleAbout(W0A, ta.Vector(UA), a.D) - AngleA), AngleB + Wrap(AngleAbout(W0B, tb.Vector(UB), b.D) - AngleB));

    /// <summary>Screw: B's turn relative to A about A's axis (continuous).</summary>
    double ScrewAngle(in Geom a, in Transform ta, in Transform tb) => AngleB + Wrap(AngleAbout(ta.Vector(UA), tb.Vector(UB), a.D) - AngleB);

    /// <summary>
    /// Records what a new mate holds to from how its references lie now (world geometry and the components'
    /// placements): the alignment and side nearest the present, Lock's relative placement, the turns' references.
    /// </summary>
    public void Capture(MateGeom a, MateGeom b, in Transform ta, in Transform tb)
    {
        switch (Type)
        {
            case MateType.Lock:
                Rel = Placement.From(ta.RigidInverse() * tb);
                return;
            case MateType.Gear:
                UA = Across(ta.RigidInverse().Vector(a.D)); UB = Across(tb.RigidInverse().Vector(b.D));
                W0A = ta.Vector(UA); W0B = tb.Vector(UB);
                AngleA = AngleB = 0;
                if (a.R > 0 && b.R > 0) { RatioA = a.R; RatioB = b.R; }
                return;
            case MateType.Screw:
                UA = Across(ta.RigidInverse().Vector(a.D)); UB = Across(tb.RigidInverse().Vector(b.D));
                S0 = Vec3.Dot(b.P - a.P, a.D);
                Theta0 = AngleB = AngleAbout(ta.Vector(UA), tb.Vector(UB), a.D);
                Aligned = Vec3.Dot(a.D, b.D) >= 0;
                return;
        }
        ChooseAlignment(a, b);
    }

    /// <summary>After a solve: the turns as they are now (so the next solve counts on from here).</summary>
    public void Settle(MateGeom a, MateGeom b, in Transform ta, in Transform tb) => Settle(a.Value, b.Value, ta, tb);

    internal void Settle(Geom a, Geom b, in Transform ta, in Transform tb)
    {
        if (Type == MateType.Gear) (AngleA, AngleB) = GearAngles(a, b, ta, tb);
        else if (Type == MateType.Screw) AngleB = ScrewAngle(a, ta, tb);
    }

    /// <summary>
    /// How far the placed references are from the relation: the residuals (zero when it holds). Directions as
    /// vector differences (no ambiguity near a whole turn), positions in mm. <paramref name="ta"/>, <paramref name="tb"/>:
    /// the placements of A's and B's components (Lock, Gear and Screw measure the components themselves).
    /// </summary>
    public void Residuals(MateGeom a, MateGeom b, in Transform ta, in Transform tb, List<double> r) => Residuals(a.Value, b.Value, ta, tb, r);

    internal void Residuals(Geom a, Geom b, in Transform ta, in Transform tb, List<double> r)
    {
        void Vec(Vec3 v) { r.Add(v.X); r.Add(v.Y); r.Add(v.Z); }
        Vec3 Perp(Vec3 v, Vec3 axis) => v - axis * Vec3.Dot(v, axis);
        // a value held to a limit range (no limits: exactly the value)
        void Range(double v, double target)
        {
            if (Min is { } lo && Max is { } hi) r.Add(Math.Max(0, lo - v) + Math.Max(0, v - hi));
            else r.Add(v - target);
        }
        switch (Type)
        {
            case MateType.Lock:
            {
                // three points of B's part where A's placement and the locked relation put them
                var rel = (Rel ?? Placement.From(Transform.Identity)).ToTransform();
                foreach (var q in LockPoints)
                    Vec(tb.Point(q) - ta.Point(rel.Point(q)));
                return;
            }
            case MateType.Gear:
            {
                // turning together at the ratio (opposite ways unless reversed): RatioA * dA + RatioB * dB = 0
                var (da, db) = GearAngles(a, b, ta, tb);
                r.Add((RatioA * da + (Reverse ? -1 : 1) * RatioB * db) / Math.Max(1e-9, Math.Max(RatioA, RatioB)));
                return;
            }
            case MateType.Screw:
            {
                // on one axis; along it as far as turned: (s - s0) = lead * (theta - theta0) / 2pi (left hand: the other way)
                Vec(Vec3.Cross(a.D, b.D));
                Vec(Perp(b.P - a.P, a.D));
                var s = Vec3.Dot(b.P - a.P, a.D);
                var th = ScrewAngle(a, ta, tb);
                r.Add((s - S0) - (Reverse ? -1 : 1) * Value * (th - Theta0) / (2 * Math.PI));
                return;
            }
        }
        // order the pair: plane before axis before point
        if ((int)a.Kind > (int)b.Kind) (a, b) = (b, a);
        double sgn = Aligned ? 1 : -1;
        var k = (a.Kind, b.Kind);
        // a coincident held apart (an offset, or limits) is measured as a distance
        var type = Type == MateType.Coincident && (Value != 0 || Min != null) ? MateType.Distance : Type;
        switch (type)
        {
            case MateType.Coincident when k == (MateGeomKind.Axis, MateGeomKind.Axis):
            case MateType.Concentric when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                // the same line (directions either way: concentric)
                Vec(Vec3.Cross(a.D, b.D));
                Vec(Perp(b.P - a.P, a.D));
                break;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(b.D - a.D * sgn);
                r.Add(Vec3.Dot(b.P - a.P, a.D));
                break;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                // the axis in the plane
                r.Add(Vec3.Dot(b.D, a.D));
                r.Add(Vec3.Dot(b.P - a.P, a.D));
                break;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Point):
                r.Add(Vec3.Dot(b.P - a.P, a.D));
                break;
            case MateType.Coincident when k == (MateGeomKind.Axis, MateGeomKind.Point):
            case MateType.Concentric when k == (MateGeomKind.Axis, MateGeomKind.Point):
                Vec(Perp(b.P - a.P, a.D));
                break;
            case MateType.Coincident or MateType.Concentric when k == (MateGeomKind.Point, MateGeomKind.Point):
                Vec(b.P - a.P);
                break;
            case MateType.Parallel when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(Vec3.Dot(b.D, a.D));
                break;
            case MateType.Parallel:
                Vec(Vec3.Cross(a.D, b.D));
                break;
            case MateType.Perpendicular when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                Vec(Vec3.Cross(a.D, b.D));
                break;
            case MateType.Perpendicular:
                r.Add(Vec3.Dot(a.D, b.D));
                break;
            case MateType.Tangent when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                // a cylinder lying on the plane: its axis parallel to it, a radius away on its side
                r.Add(Vec3.Dot(b.D, a.D));
                r.Add(Vec3.Dot(b.P - a.P, a.D) - Side * b.R);
                break;
            case MateType.Tangent when k == (MateGeomKind.Plane, MateGeomKind.Point):
                r.Add(Vec3.Dot(b.P - a.P, a.D) - Side * b.R);
                break;
            case MateType.Tangent when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                // two cylinders touching along a line: parallel, their axes the radii apart (summed, or differing inside)
                Vec(Vec3.Cross(a.D, b.D));
                r.Add(Perp(b.P - a.P, a.D).Length - (Inside ? Math.Abs(a.R - b.R) : a.R + b.R));
                break;
            case MateType.Tangent when k == (MateGeomKind.Axis, MateGeomKind.Point):
                r.Add(Perp(b.P - a.P, a.D).Length - (Inside ? Math.Abs(a.R - b.R) : a.R + b.R));
                break;
            case MateType.Tangent:
                r.Add((b.P - a.P).Length - (Inside ? Math.Abs(a.R - b.R) : a.R + b.R));
                break;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(b.D - a.D * sgn);
                Range(Side * Vec3.Dot(b.P - a.P, a.D), Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(Vec3.Dot(b.D, a.D));
                Range(Side * Vec3.Dot(b.P - a.P, a.D), Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Point):
                Range(Side * Vec3.Dot(b.P - a.P, a.D), Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                Vec(Vec3.Cross(a.D, b.D));
                Range(Perp(b.P - a.P, a.D).Length, Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Point):
                Range(Perp(b.P - a.P, a.D).Length, Value);
                break;
            case MateType.Distance:
                Range((b.P - a.P).Length, Value);
                break;
            case MateType.Angle:
            {
                // the angle between the directions (planes' normals, axes), in degrees for the limits' sake
                var c = Math.Clamp(Vec3.Dot(a.D, b.D), -1, 1);
                var deg = Math.Acos(c) * 180 / Math.PI;
                if (Min is { } lo && Max is { } hi) r.Add((Math.Max(0, lo - deg) + Math.Max(0, deg - hi)) * Math.PI / 180);
                else r.Add((deg - Value) * Math.PI / 180);
                break;
            }
        }
    }

    /// <summary>
    /// A small rigid motion of one side of a mate: a turn <c>W</c> about <c>Pivot</c> and a move <c>M</c> (the solver's
    /// six numbers for a component, at zero). Zero: that side still.
    /// </summary>
    public readonly record struct Twist(Vec3 W, Vec3 M, Vec3 Pivot)
    {
        public Vec3 Point(Vec3 x) => Vec3.Cross(W, x - Pivot) + M;
        public Vec3 Dir(Vec3 d) => Vec3.Cross(W, d);
    }

    /// <summary>
    /// How <see cref="Residuals"/> change (row for row, per unit) as the sides move by <paramref name="da"/> and
    /// <paramref name="db"/>: the residuals' derivatives, exactly, for the solver's Jacobian. False (with
    /// <paramref name="r"/> to be ignored) where it is not worked out here and the solver differences instead: Gear and
    /// Screw (turns counted from where they were), a length or an Angle where it is not differentiable (zero, 0 or 180
    /// degrees).
    /// </summary>
    public bool Directional(MateGeom a, MateGeom b, in Transform ta, in Transform tb, in Twist da, in Twist db, List<double> r)
        => Directional(a.Value, b.Value, ta, tb, da, db, r);

    internal bool Directional(Geom a, Geom b, in Transform ta, in Transform tb, in Twist da, in Twist db, List<double> r)
    {
        void Vec(Vec3 v) { r.Add(v.X); r.Add(v.Y); r.Add(v.Z); }
        // v - n (v.n), and its change
        static Vec3 Perp(Vec3 v, Vec3 n) => v - n * Vec3.Dot(v, n);
        static Vec3 DPerp(Vec3 v, Vec3 n, Vec3 dv, Vec3 dn) => dv - dn * Vec3.Dot(v, n) - n * (Vec3.Dot(dv, n) + Vec3.Dot(v, dn));
        // the change of a value held to a limit range (a penalty outside it), or held exactly
        void Range(double v, double dv)
        {
            if (Min is { } lo && Max is { } hi) r.Add(v < lo ? -dv : v > hi ? dv : 0);
            else r.Add(dv);
        }
        switch (Type)
        {
            case MateType.Lock:
            {
                var rel = (Rel ?? Placement.From(Transform.Identity)).ToTransform();
                foreach (var q in LockPoints)
                    Vec(db.Point(tb.Point(q)) - da.Point(ta.Point(rel.Point(q))));
                return true;
            }
            case MateType.Gear:
            {
                // each side's turn about its own axis, from a direction fixed in the assembly
                if (DAngleAbout(W0A, ta.Vector(UA), a.D, Vec3.Zero, da.Dir(ta.Vector(UA)), da.Dir(a.D)) is not { } dA
                    || DAngleAbout(W0B, tb.Vector(UB), b.D, Vec3.Zero, db.Dir(tb.Vector(UB)), db.Dir(b.D)) is not { } dB) return false;
                r.Add((RatioA * dA + (Reverse ? -1 : 1) * RatioB * dB) / Math.Max(1e-9, Math.Max(RatioA, RatioB)));
                return true;
            }
            case MateType.Screw:
            {
                var u = ta.Vector(UA); var w = tb.Vector(UB);
                if (DAngleAbout(u, w, a.D, da.Dir(u), db.Dir(w), da.Dir(a.D)) is not { } dTh) return false;
                var dDa0 = da.Dir(a.D); var dDb0 = db.Dir(b.D);
                var v0 = b.P - a.P; var dv0 = db.Point(b.P) - da.Point(a.P);
                Vec(Vec3.Cross(dDa0, b.D) + Vec3.Cross(a.D, dDb0));
                Vec(DPerp(v0, a.D, dv0, dDa0));
                r.Add(Vec3.Dot(dv0, a.D) + Vec3.Dot(v0, dDa0) - (Reverse ? -1 : 1) * Value * dTh / (2 * Math.PI));
                return true;
            }
        }
        var (sa, sb) = (da, db);
        if ((int)a.Kind > (int)b.Kind) { (a, b) = (b, a); (sa, sb) = (db, da); }
        var dPa = sa.Point(a.P); var dDa = a.Kind == MateGeomKind.Point ? Vec3.Zero : sa.Dir(a.D);
        var dPb = sb.Point(b.P); var dDb = b.Kind == MateGeomKind.Point ? Vec3.Zero : sb.Dir(b.D);
        double sgn = Aligned ? 1 : -1;
        var k = (a.Kind, b.Kind);
        var type = Type == MateType.Coincident && (Value != 0 || Min != null) ? MateType.Distance : Type;
        var v = b.P - a.P; var dv = dPb - dPa;
        Vec3 DCross() => Vec3.Cross(dDa, b.D) + Vec3.Cross(a.D, dDb);
        double DDot() => Vec3.Dot(dDb, a.D) + Vec3.Dot(b.D, dDa);
        // the offset along A's normal, and its change
        double Off() => Vec3.Dot(v, a.D);
        double DOff() => Vec3.Dot(dv, a.D) + Vec3.Dot(v, dDa);
        // a length and its change (false at zero, where it has none)
        static bool DLen(Vec3 x, Vec3 dx, out double len, out double dlen)
        {
            len = x.Length;
            dlen = len > 1e-9 ? Vec3.Dot(x, dx) / len : 0;
            return len > 1e-9;
        }
        double l, dl;
        switch (type)
        {
            case MateType.Coincident when k == (MateGeomKind.Axis, MateGeomKind.Axis):
            case MateType.Concentric when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                Vec(DCross());
                Vec(DPerp(v, a.D, dv, dDa));
                return true;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(dDb - dDa * sgn);
                r.Add(DOff());
                return true;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(DDot());
                r.Add(DOff());
                return true;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Point):
                r.Add(DOff());
                return true;
            case MateType.Coincident when k == (MateGeomKind.Axis, MateGeomKind.Point):
            case MateType.Concentric when k == (MateGeomKind.Axis, MateGeomKind.Point):
                Vec(DPerp(v, a.D, dv, dDa));
                return true;
            case MateType.Coincident or MateType.Concentric when k == (MateGeomKind.Point, MateGeomKind.Point):
                Vec(dv);
                return true;
            case MateType.Parallel when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(DDot());
                return true;
            case MateType.Parallel:
                Vec(DCross());
                return true;
            case MateType.Perpendicular when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                Vec(DCross());
                return true;
            case MateType.Perpendicular:
                r.Add(DDot());
                return true;
            case MateType.Tangent when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(DDot());
                r.Add(DOff());
                return true;
            case MateType.Tangent when k == (MateGeomKind.Plane, MateGeomKind.Point):
                r.Add(DOff());
                return true;
            case MateType.Tangent when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                if (!DLen(Perp(v, a.D), DPerp(v, a.D, dv, dDa), out _, out dl)) return false;
                Vec(DCross());
                r.Add(dl);
                return true;
            case MateType.Tangent when k == (MateGeomKind.Axis, MateGeomKind.Point):
                if (!DLen(Perp(v, a.D), DPerp(v, a.D, dv, dDa), out _, out dl)) return false;
                r.Add(dl);
                return true;
            case MateType.Tangent:
                if (!DLen(v, dv, out _, out dl)) return false;
                r.Add(dl);
                return true;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(dDb - dDa * sgn);
                Range(Side * Off(), Side * DOff());
                return true;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(DDot());
                Range(Side * Off(), Side * DOff());
                return true;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Point):
                Range(Side * Off(), Side * DOff());
                return true;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                if (!DLen(Perp(v, a.D), DPerp(v, a.D, dv, dDa), out l, out dl)) return false;
                Vec(DCross());
                Range(l, dl);
                return true;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Point):
                if (!DLen(Perp(v, a.D), DPerp(v, a.D, dv, dDa), out l, out dl)) return false;
                Range(l, dl);
                return true;
            case MateType.Distance:
                if (!DLen(v, dv, out l, out dl)) return false;
                Range(l, dl);
                return true;
            case MateType.Angle:
            {
                var c = Vec3.Dot(a.D, b.D);
                if (Math.Abs(c) > 1 - 1e-9) return false;
                // in radians, as the residual: the arccosine's change (or the penalty's, outside the limits)
                var dAngle = -DDot() / Math.Sqrt(1 - c * c);
                var deg = Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI;
                if (Min is { } lo && Max is { } hi) r.Add(deg < lo ? -dAngle : deg > hi ? dAngle : 0);
                else r.Add(dAngle);
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// How <see cref="AngleAbout"/>(u, v, d) changes as u, v and d change by du, dv and dd; null where it has no
    /// direction to measure (u or v along d).
    /// </summary>
    static double? DAngleAbout(Vec3 u, Vec3 v, Vec3 d, Vec3 du, Vec3 dv, Vec3 dd)
    {
        // the directions across d, and their changes
        var up = u - d * Vec3.Dot(u, d); var vp = v - d * Vec3.Dot(v, d);
        var dup = du - dd * Vec3.Dot(u, d) - d * (Vec3.Dot(du, d) + Vec3.Dot(u, dd));
        var dvp = dv - dd * Vec3.Dot(v, d) - d * (Vec3.Dot(dv, d) + Vec3.Dot(v, dd));
        // atan2(y, x) with x = up.vp, y = (up x vp).d
        double x = Vec3.Dot(up, vp), y = Vec3.Dot(Vec3.Cross(up, vp), d);
        double q = x * x + y * y;
        if (q < 1e-24) return null;
        double dx = Vec3.Dot(dup, vp) + Vec3.Dot(up, dvp);
        double dy = Vec3.Dot(Vec3.Cross(dup, vp) + Vec3.Cross(up, dvp), d) + Vec3.Dot(Vec3.Cross(up, vp), dd);
        return (x * dy - y * dx) / q;
    }

    /// <summary>The alignment nearest how two references lie now (for a new mate), the side, and inside / outside.</summary>
    public void ChooseAlignment(MateGeom a, MateGeom b)
    {
        if ((int)a.Kind > (int)b.Kind) (a, b) = (b, a);
        Aligned = Vec3.Dot(a.D, b.D) >= 0;
        // the side B is on; touching (no side yet): out of A's face, along its normal (a distance then parts them)
        if (a.Kind == MateGeomKind.Plane) Side = Vec3.Dot(b.P - a.P, a.D) < -1e-9 * (1 + a.P.Length) ? -1 : 1;
        // round against round: inside if the centres are nearer the radii's difference than their sum
        if (a.Kind != MateGeomKind.Plane)
        {
            var d = a.Kind == MateGeomKind.Axis ? (b.P - a.P - a.D * Vec3.Dot(b.P - a.P, a.D)).Length : (b.P - a.P).Length;
            Inside = Math.Abs(d - Math.Abs(a.R - b.R)) < Math.Abs(d - (a.R + b.R));
        }
    }
}

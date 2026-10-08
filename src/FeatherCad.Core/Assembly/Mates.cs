using FeatherCad.Kernel;

namespace FeatherCad.Core.Assembly;

/// <summary>The mates of the first milestone (SolidWorks' standard mates).</summary>
public enum MateType { Coincident, Concentric, Parallel, Perpendicular, Distance, Angle }

/// <summary>What a mate reference is, geometrically.</summary>
public enum MateGeomKind { Plane, Axis, Point }

/// <summary>
/// A mate reference's geometry in its part's coordinates: a plane (a point on it and its normal, out of the
/// material for a face), an axis (a point on it and its direction: cylinders, cones) or a point (sphere centres).
/// </summary>
public sealed record MateGeom(MateGeomKind Kind, Vec3 P, Vec3 D)
{
    /// <summary>The geometry of a display face: planes and the axes of cylinders and cones; points for spheres.</summary>
    public static MateGeom? Of(FaceRange f)
    {
        var s = f.Surface;
        return s.Kind switch
        {
            SurfaceKind.Plane => new MateGeom(MateGeomKind.Plane, f.FirstPlane.Origin, f.FirstPlane.Normal),
            SurfaceKind.Cylinder or SurfaceKind.Cone => new MateGeom(MateGeomKind.Axis, s.Origin, s.Axis.Normalized),
            SurfaceKind.Sphere => new MateGeom(MateGeomKind.Point, s.Origin, Vec3.UnitZ),
            _ => null,
        };
    }

    /// <summary>Carried by a placement.</summary>
    public MateGeom Placed(in Transform t) => this with { P = t.Point(P), D = Kind == MateGeomKind.Point ? D : t.Vector(D).Normalized };
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

    public MateGeom Geom => new(Kind, P, D);
    public MateRef Clone() => (MateRef)MemberwiseClone();
}

/// <summary>A mate: two references held in a relation (and a value for distances and angles).</summary>
public sealed class Mate
{
    public string Id { get; set; } = "";
    /// <summary>Shown in the tree ("Coincident1").</summary>
    public string Name { get; set; } = "";
    public MateType Type { get; set; }
    public MateRef A { get; set; } = new();
    public MateRef B { get; set; } = new();
    /// <summary>Distance (mm) or angle (degrees), for Distance and Angle mates.</summary>
    public double Value { get; set; }
    /// <summary>
    /// The directions the same way (aligned) or opposite (anti-aligned: faces against each other). Chosen when the
    /// mate is made from how the components lie (the nearer), flipped by the user.
    /// </summary>
    public bool Aligned { get; set; }
    /// <summary>For distances: which side of A's plane B is (+1 along A's normal, -1 against it).</summary>
    public int Side { get; set; } = 1;
    public bool Suppressed { get; set; }

    public Mate Clone()
    {
        var m = (Mate)MemberwiseClone();
        m.A = A.Clone(); m.B = B.Clone();
        return m;
    }

    /// <summary>Whether this type can relate these kinds of references (in either order).</summary>
    public static bool Supports(MateType t, MateGeomKind a, MateGeomKind b)
    {
        bool Pair(MateGeomKind x, MateGeomKind y) => (a == x && b == y) || (a == y && b == x);
        return t switch
        {
            MateType.Coincident => true,
            MateType.Concentric => Pair(MateGeomKind.Axis, MateGeomKind.Axis) || Pair(MateGeomKind.Axis, MateGeomKind.Point) || Pair(MateGeomKind.Point, MateGeomKind.Point),
            MateType.Parallel or MateType.Perpendicular => a != MateGeomKind.Point && b != MateGeomKind.Point,
            MateType.Distance => true,
            MateType.Angle => a != MateGeomKind.Point && b != MateGeomKind.Point,
            _ => false,
        };
    }

    /// <summary>
    /// How far the placed references are from the relation: the residuals (zero when it holds). Directions as
    /// vector differences (no ambiguity near a whole turn), positions in mm.
    /// </summary>
    public void Residuals(MateGeom a, MateGeom b, List<double> r)
    {
        // order the pair: plane before axis before point
        bool swap = (int)a.Kind > (int)b.Kind;
        if (swap) (a, b) = (b, a);
        double s = Aligned ? 1 : -1;
        void Vec(Vec3 v) { r.Add(v.X); r.Add(v.Y); r.Add(v.Z); }
        Vec3 Perp(Vec3 v, Vec3 axis) => v - axis * Vec3.Dot(v, axis);
        var k = (a.Kind, b.Kind);
        switch (Type)
        {
            case MateType.Coincident when k == (MateGeomKind.Axis, MateGeomKind.Axis):
            case MateType.Concentric when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                // the same line (directions either way: concentric)
                Vec(Vec3.Cross(a.D, b.D));
                Vec(Perp(b.P - a.P, a.D));
                break;
            case MateType.Coincident when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(b.D - a.D * s);
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
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Plane):
                Vec(b.D - a.D * s);
                r.Add(Vec3.Dot(b.P - a.P, a.D) - Side * Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Axis):
                r.Add(Vec3.Dot(b.D, a.D));
                r.Add(Vec3.Dot(b.P - a.P, a.D) - Side * Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Plane, MateGeomKind.Point):
                r.Add(Vec3.Dot(b.P - a.P, a.D) - Side * Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Axis):
                Vec(Vec3.Cross(a.D, b.D));
                r.Add(Perp(b.P - a.P, a.D).Length - Value);
                break;
            case MateType.Distance when k == (MateGeomKind.Axis, MateGeomKind.Point):
                r.Add(Perp(b.P - a.P, a.D).Length - Value);
                break;
            case MateType.Distance:
                r.Add((b.P - a.P).Length - Value);
                break;
            case MateType.Angle:
            {
                // the angle between the directions (planes' normals, axes)
                var c = Math.Clamp(Vec3.Dot(a.D, b.D), -1, 1);
                r.Add(Math.Acos(c) - Value * Math.PI / 180);
                break;
            }
        }
    }

    /// <summary>The alignment nearest how two references lie now (for a new mate), and the side for distances.</summary>
    public void ChooseAlignment(MateGeom a, MateGeom b)
    {
        bool swap = (int)a.Kind > (int)b.Kind;
        if (swap) (a, b) = (b, a);
        Aligned = Vec3.Dot(a.D, b.D) >= 0;
        // the side B is on; touching (no side yet): out of A's face, along its normal (a distance then parts them)
        if (a.Kind == MateGeomKind.Plane) Side = Vec3.Dot(b.P - a.P, a.D) < -1e-9 * (1 + a.P.Length) ? -1 : 1;
    }
}

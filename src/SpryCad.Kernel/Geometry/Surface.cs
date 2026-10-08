using SpryCad.Kernel.Native;

namespace SpryCad.Kernel;

/// <summary>Pipe: the canal surface of a ball rolling along a spine curve (rolling-ball blends).</summary>
/// <summary>Offset: the base surface moved by Radius along its normal (offsets of B-spline surfaces).</summary>
/// <summary>Screw: a line or arc carried by a screw motion (helical sweeps: threads, springs).</summary>
public enum SurfaceKind { Plane, Cylinder, Cone, Sphere, Torus, Pipe, BSpline, Offset, Screw }

/// <summary>
/// The exact ("true") geometry a face was created from. Faces are stored as exact planar
/// facets; curved faces remember their analytic definition here so that display normals,
/// edge blends and future exact-surface export/booleans can use it.
/// </summary>
public sealed record SurfaceDef
{
    public SurfaceKind Kind { get; init; }
    /// <summary>Point on axis (cylinder/cone/torus/sphere center).</summary>
    public Vec3 Origin { get; init; }
    /// <summary>Axis direction (unit).</summary>
    public Vec3 Axis { get; init; }
    /// <summary>Reference direction perpendicular to Axis; facet angles are measured from it.</summary>
    public Vec3 RefDir { get; init; }
    /// <summary>Cylinder/sphere radius, torus major radius, cone radius at Origin.</summary>
    public double Radius { get; init; }
    /// <summary>Torus minor radius, cone half angle (radians).</summary>
    public double Param2 { get; init; }
    /// <summary>Pipe spine (Radius is the pipe radius).</summary>
    public PipeSpine? Spine { get; init; }
    /// <summary>B-spline surface geometry.</summary>
    public NurbsSurface? Nurbs { get; init; }
    /// <summary>Base surface of an offset surface (Radius is the distance along its normal).</summary>
    public NSurface? Base { get; init; }
    /// <summary>Screw surface geometry.</summary>
    public ScrewSurface? Screw { get; init; }
    /// <summary>Facet count used for a full revolution.</summary>
    public int Segments { get; init; }

    public static readonly SurfaceDef PlaneDef = new() { Kind = SurfaceKind.Plane };

    public bool IsCurved => Kind != SurfaceKind.Plane;

    public SurfaceDef Transformed(in Transform t)
    {
        if (Kind == SurfaceKind.Plane) return this;
        var axis = t.Normal(Axis);
        var refd = t.Normal(RefDir);
        return this with { Origin = t.Point(Origin), Axis = axis, RefDir = refd, Spine = Spine?.Transformed(t), Nurbs = Nurbs?.Transformed(t), Base = Base?.Transformed(t), Screw = Screw?.Transformed(t) };
    }

    /// <summary>Analytic outward normal at p, given the facet normal (used to pick the side).</summary>
    public Vec3 NormalAt(Vec3 p, Vec3 facetNormal) => Orient(RawNormal(p), facetNormal);

    /// <summary>Whether a normal is a closest-point search (worth computing once per point).</summary>
    public bool IsFreeForm => Kind is SurfaceKind.Pipe or SurfaceKind.BSpline or SurfaceKind.Offset or SurfaceKind.Screw;

    /// <summary>The surface's normal direction at p, either side, not normalized (null for a plane).</summary>
    public Vec3? RawNormal(Vec3 p)
    {
        Vec3 n;
        switch (Kind)
        {
            case SurfaceKind.Cylinder:
                {
                    var d = p - Origin;
                    n = d - Axis * Vec3.Dot(d, Axis);
                    break;
                }
            case SurfaceKind.Sphere:
                n = p - Origin;
                break;
            case SurfaceKind.Cone:
                {
                    var d = p - Origin;
                    var h = Vec3.Dot(d, Axis);
                    var radial = (d - Axis * h).Normalized;
                    // surface normal of cone with half angle a: radial*cos(a) - axis*sin(a)
                    var a = Param2;
                    n = radial * Math.Cos(a) - Axis * Math.Sin(a);
                    break;
                }
            case SurfaceKind.Torus:
                {
                    var d = p - Origin;
                    var h = Vec3.Dot(d, Axis);
                    var radial = (d - Axis * h).Normalized;
                    var c = Origin + radial * Radius;
                    n = p - c;
                    break;
                }
            case SurfaceKind.Pipe:
                n = Spine!.Normal(p);
                break;
            case SurfaceKind.BSpline:
                {
                    var c = Nurbs!.Closest(p);
                    n = Nurbs.Normal(c.u, c.v);
                    break;
                }
            case SurfaceKind.Offset:
                n = Base!.Grad(p);
                break;
            case SurfaceKind.Screw:
                n = Screw!.Normal(p);
                break;
            default:
                return null;
        }
        return n;
    }

    /// <summary>A <see cref="RawNormal"/> turned to the facet's side (the facet normal where it is unusable).</summary>
    public static Vec3 Orient(Vec3? raw, Vec3 facetNormal)
    {
        if (raw is not { } n) return facetNormal;
        n = n.Normalized;
        if (n.LengthSq < 0.5) return facetNormal;
        if (Vec3.Dot(n, facetNormal) < 0) n = -n;
        // guard against facets whose analytic normal deviates a lot (e.g. degenerate tips)
        if (Vec3.Dot(n, facetNormal) < 0.3) return facetNormal;
        return n;
    }

    /// <summary>Number of facets for a full circle of radius r.</summary>
    public static int SegmentsFor(double r)
    {
        // chord deviation target ~ 0.4% of radius, clamped. Multiples of 4 keep quadrant points.
        var n = (int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - Math.Min(0.02, 0.05 / Math.Max(r, 1e-6)))));
        n = Math.Clamp(n, 24, 96);
        return (n + 3) / 4 * 4;
    }
}

/// <summary>
/// Persistent identity of a face. Names are generated from the creating feature and the
/// sketch entity / role that produced the face, so they survive parameter changes
/// (a practical answer to the topological naming problem).
/// </summary>
public sealed class FaceTag
{
    public string Name { get; }
    public SurfaceDef Surface { get; }
    /// <summary>Creation order, used to keep the oldest name when coplanar faces merge.</summary>
    public long Seq { get; }

    static long _seq;

    public FaceTag(string name, SurfaceDef surface)
    {
        Name = name; Surface = surface; Seq = Interlocked.Increment(ref _seq);
    }

    internal FaceTag(string name, SurfaceDef surface, long seq) { Name = name; Surface = surface; Seq = seq; }

    public FaceTag WithName(string name) => new(name, Surface, Seq);
    public FaceTag Transformed(in Transform t, string? prefix)
        => new(prefix == null ? Name : prefix + Name, Surface.Transformed(t), Seq);

    public override string ToString() => Name;
}

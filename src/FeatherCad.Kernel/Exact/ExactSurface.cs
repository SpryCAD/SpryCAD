namespace FeatherCad.Kernel;

/// <summary>
/// An analytic surface with evaluation helpers: closest point, natural normal and the
/// definition needed for exact export. Planes carry their <see cref="Plane"/>.
/// </summary>
public sealed class ExactSurface
{
    public SurfaceDef Def { get; }
    public Plane? Plane { get; }

    public ExactSurface(SurfaceDef def, Plane? plane = null)
    {
        Def = def; Plane = plane;
        if (def.Kind == SurfaceKind.Plane && plane == null) throw new ArgumentException("plane required");
    }

    public SurfaceKind Kind => Def.Kind;

    Vec3 Axis => Def.Axis.Normalized;

    /// <summary>Orthogonal projection of p onto the surface (nearest point).</summary>
    public Vec3 Project(Vec3 p)
    {
        switch (Def.Kind)
        {
            case SurfaceKind.Plane:
                return Plane!.Project(p);
            case SurfaceKind.Cylinder:
                {
                    var (h, radial, _) = Decompose(p);
                    return Def.Origin + Axis * h + radial * Def.Radius;
                }
            case SurfaceKind.Sphere:
                {
                    var d = (p - Def.Origin).Normalized;
                    if (d.LengthSq < 0.5) d = Axis.AnyPerpendicular();
                    return Def.Origin + d * Def.Radius;
                }
            case SurfaceKind.Cone:
                {
                    // generator in the (h, r) half plane: (t, Ra + t tan a)
                    var (h, radial, r) = Decompose(p);
                    var dir = new Vec2(Math.Cos(Def.Param2), Math.Sin(Def.Param2));
                    var p0 = new Vec2(0, Def.Radius);
                    var q = new Vec2(h, r);
                    var t = Vec2.Dot(q - p0, dir);
                    var c = p0 + dir * t;
                    if (c.Y < 0) c = new Vec2(-Def.Radius / Math.Tan(Def.Param2), 0); // apex
                    return Def.Origin + Axis * c.X + radial * c.Y;
                }
            case SurfaceKind.Torus:
                {
                    var (h, radial, _) = Decompose(p);
                    var center = Def.Origin + radial * Def.Radius;
                    var d = (p - center).Normalized;
                    if (d.LengthSq < 0.5) d = radial;
                    return center + d * Def.Param2;
                }
            case SurfaceKind.Pipe:
                return Def.Spine!.Project(p, Def.Radius);
            case SurfaceKind.BSpline:
                return Def.Nurbs!.Closest(p).c;
            case SurfaceKind.Offset:
                {
                    var c = Def.Base!.Project(p);
                    return c + Def.Base.Normal(c) * Def.Radius;
                }
        }
        return p;
    }

    /// <summary>Axial height, unit radial direction and radial distance of p relative to the axis.</summary>
    (double h, Vec3 radial, double r) Decompose(Vec3 p)
    {
        var a = Axis;
        var rel = p - Def.Origin;
        var h = Vec3.Dot(rel, a);
        var rv = rel - a * h;
        var r = rv.Length;
        var radial = r > 1e-12 ? rv / r : (Def.RefDir.LengthSq > 0.5 ? Def.RefDir.Normalized : a.AnyPerpendicular());
        return (h, radial, r);
    }

    /// <summary>Natural (parametric) normal of the surface at a point on it, as defined by STEP.</summary>
    public Vec3 NaturalNormal(Vec3 p)
    {
        switch (Def.Kind)
        {
            case SurfaceKind.Plane: return Plane!.Normal;
            case SurfaceKind.Cylinder: { var (_, radial, _) = Decompose(p); return radial; }
            case SurfaceKind.Sphere: return (p - Def.Origin).Normalized;
            case SurfaceKind.Cone:
                {
                    var (_, radial, _) = Decompose(p);
                    var a = Def.Param2;
                    return (radial * Math.Cos(a) - Axis * Math.Sin(a)).Normalized;
                }
            case SurfaceKind.Torus:
                {
                    var (_, radial, _) = Decompose(p);
                    var c = Def.Origin + radial * Def.Radius;
                    return (p - c).Normalized;
                }
            case SurfaceKind.Pipe:
                return Def.Spine!.Normal(p);
            case SurfaceKind.BSpline:
                {
                    var c = Def.Nurbs!.Closest(p);
                    return Def.Nurbs.Normal(c.u, c.v);
                }
            case SurfaceKind.Offset:
                return Def.Base!.Grad(p);
        }
        return Vec3.UnitZ;
    }

    public double Distance(Vec3 p) => Vec3.Distance(p, Project(p));

    /// <summary>Finds a point on the intersection of two surfaces near p (alternating projections).</summary>
    public static Vec3 ProjectToIntersection(ExactSurface a, ExactSurface b, Vec3 p, int iterations = 200, double tol = 1e-11)
    {
        var q = p;
        for (int i = 0; i < iterations; i++)
        {
            var next = b.Project(a.Project(q));
            if ((next - q).LengthSq < tol * tol) return next;
            q = next;
        }
        return q;
    }

    public string Describe()
    {
        return Def.Kind switch
        {
            SurfaceKind.Plane => "Plane",
            SurfaceKind.Cylinder => $"Cylinder Ø{2 * Def.Radius:0.###}",
            SurfaceKind.Sphere => $"Sphere R{Def.Radius:0.###}",
            SurfaceKind.Cone => $"Cone {2 * Def.Param2 * 180 / Math.PI:0.##}°",
            SurfaceKind.Torus => $"Torus R{Def.Radius:0.###} r{Def.Param2:0.###}",
            SurfaceKind.Pipe => $"Fillet R{Def.Radius:0.###}",
            SurfaceKind.BSpline => "B-spline surface",
            SurfaceKind.Offset => $"Offset surface {Def.Radius:0.###}",
            _ => Def.Kind.ToString(),
        };
    }
}

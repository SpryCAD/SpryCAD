namespace FeatherCad.Kernel.Native;

/// <summary>
/// Analytic surface for the native B-rep: UV parameterization, implicit signed distance (positive on
/// the natural-normal side), projection and exact ray intersection.
/// UV: plane (x, y); cylinder/cone (θ, h); sphere/torus (θ, φ). θ is measured about the axis from RefDir.
/// </summary>
public sealed class NSurface
{
    public SurfaceDef Def { get; }
    public SurfaceKind Kind => Def.Kind;
    /// <summary>Plane: in-plane frame (Normal = plane normal). Revolution: X = RefDir, Y = Axis × X, Normal = Axis.</summary>
    public Frame F { get; }
    public ExactSurface Exact { get; }

    public NSurface(SurfaceDef def, Plane? plane = null)
    {
        Def = def;
        if (def.Kind == SurfaceKind.Plane)
        {
            if (plane == null) throw new ArgumentException("plane required");
            F = Frame.FromPlane(plane);
            Exact = new ExactSurface(def, plane);
        }
        else
        {
            var a = def.Axis.Normalized;
            var x = def.RefDir - a * Vec3.Dot(def.RefDir, a);
            if (x.Length < 1e-9) x = a.AnyPerpendicular();
            x = x.Normalized;
            F = new Frame(def.Origin, x, Vec3.Cross(a, x), a);
            Exact = new ExactSurface(def with { Axis = a, RefDir = x });
        }
    }

    public static NSurface FromPlane(Plane p) => new(SurfaceDef.PlaneDef, p);

    public Plane? Plane => Kind == SurfaceKind.Plane ? F.Plane : null;
    public Vec3 Axis => F.Normal;
    public Vec3 Origin => F.Origin;
    public double R => Def.Radius;
    public double R2 => Def.Param2;
    public bool PeriodicU => Kind != SurfaceKind.Plane;

    // ---------------------------------------------------------------------------------------
    // parameterization

    public bool PeriodicV => Kind == SurfaceKind.Torus;

    public Vec2 UV(Vec3 p, double uHint = double.NaN, double vHint = double.NaN)
    {
        var l = F.ToLocal3(p);
        switch (Kind)
        {
            case SurfaceKind.Plane: return new Vec2(l.X, l.Y);
            case SurfaceKind.Cylinder:
            case SurfaceKind.Cone:
                return new Vec2(Theta(l, uHint), l.Z);
            case SurfaceKind.Sphere:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    return new Vec2(Theta(l, uHint), Math.Atan2(l.Z, r));
                }
            case SurfaceKind.Torus:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    var phi = Math.Atan2(l.Z, r - R);
                    return new Vec2(Theta(l, uHint), double.IsNaN(vHint) ? phi : NCurve.Wrap(phi, vHint));
                }
        }
        return default;
    }

    static double Theta(Vec3 l, double hint)
    {
        if (l.X * l.X + l.Y * l.Y < 1e-24) return double.IsNaN(hint) ? 0 : hint; // on the axis (pole/apex)
        var a = Math.Atan2(l.Y, l.X);
        return double.IsNaN(hint) ? a : NCurve.Wrap(a, hint);
    }

    /// <summary>True when the point is on the axis, where θ is undefined (sphere pole, cone apex).</summary>
    public bool IsPole(Vec3 p)
    {
        if (Kind is not (SurfaceKind.Sphere or SurfaceKind.Cone)) return false;
        var l = F.ToLocal3(p);
        return l.X * l.X + l.Y * l.Y < 1e-14 * Math.Max(1, R * R);
    }

    public Vec3 Eval(double u, double v)
    {
        switch (Kind)
        {
            case SurfaceKind.Plane: return F.ToWorld(u, v);
            case SurfaceKind.Cylinder: return F.ToWorld(R * Math.Cos(u), R * Math.Sin(u), v);
            case SurfaceKind.Cone:
                {
                    var r = R + v * Math.Tan(R2);
                    return F.ToWorld(r * Math.Cos(u), r * Math.Sin(u), v);
                }
            case SurfaceKind.Sphere:
                return F.ToWorld(R * Math.Cos(v) * Math.Cos(u), R * Math.Cos(v) * Math.Sin(u), R * Math.Sin(v));
            case SurfaceKind.Torus:
                {
                    var r = R + R2 * Math.Cos(v);
                    return F.ToWorld(r * Math.Cos(u), r * Math.Sin(u), R2 * Math.Sin(v));
                }
        }
        return default;
    }

    /// <summary>Natural (parametric) normal at a point on the surface.</summary>
    public Vec3 Normal(Vec3 p) => Exact.NaturalNormal(p);

    public Vec3 Project(Vec3 p) => Exact.Project(p);

    /// <summary>Signed distance, positive on the side of the natural normal (exact for all kinds but the cone, which is scaled).</summary>
    public double F_(Vec3 p)
    {
        var l = F.ToLocal3(p);
        switch (Kind)
        {
            case SurfaceKind.Plane: return l.Z;
            case SurfaceKind.Cylinder: return Math.Sqrt(l.X * l.X + l.Y * l.Y) - R;
            case SurfaceKind.Cone:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y);
                    return (r - (R + l.Z * Math.Tan(R2))) * Math.Cos(R2);
                }
            case SurfaceKind.Sphere: return Math.Sqrt(l.X * l.X + l.Y * l.Y + l.Z * l.Z) - R;
            case SurfaceKind.Torus:
                {
                    var r = Math.Sqrt(l.X * l.X + l.Y * l.Y) - R;
                    return Math.Sqrt(r * r + l.Z * l.Z) - R2;
                }
        }
        return 0;
    }

    public Vec3 Grad(Vec3 p)
    {
        const double h = 1e-7;
        return new Vec3(
            F_(p + new Vec3(h, 0, 0)) - F_(p - new Vec3(h, 0, 0)),
            F_(p + new Vec3(0, h, 0)) - F_(p - new Vec3(0, h, 0)),
            F_(p + new Vec3(0, 0, h)) - F_(p - new Vec3(0, 0, h))) / (2 * h);
    }

    /// <summary>(∂u × ∂v) orientation vs. natural normal: +1 for every kind (by construction of the parameterization).</summary>
    public const int UvSign = 1;

    // ---------------------------------------------------------------------------------------
    // relations

    public bool IsRevolution => Kind != SurfaceKind.Plane;

    /// <summary>Same geometric surface (orientation ignored).</summary>
    public bool CoincidentWith(NSurface o, double tol)
    {
        if (Kind != o.Kind) return false;
        if (Kind == SurfaceKind.Plane)
        {
            var a = Plane!; var b = o.Plane!;
            var d = Vec3.Dot(a.Normal, b.Normal);
            return Math.Abs(Math.Abs(d) - 1) < 1e-10 && Math.Abs(a.Distance(b.Origin)) < tol;
        }
        if (!SameAxisLine(o, tol)) return false;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: return Math.Abs(R - o.R) < tol;
            case SurfaceKind.Sphere: return Vec3.Distance(Origin, o.Origin) < tol && Math.Abs(R - o.R) < tol;
            case SurfaceKind.Torus: return Vec3.Distance(Origin, o.Origin) < tol && Math.Abs(R - o.R) < tol && Math.Abs(R2 - o.R2) < tol;
            case SurfaceKind.Cone:
                {
                    // compare radius at a common height
                    var dir = Vec3.Dot(Axis, o.Axis) > 0 ? 1 : -1;
                    if (dir < 0) return false;
                    if (Math.Abs(R2 - o.R2) > 1e-10) return false;
                    var h = Vec3.Dot(o.Origin - Origin, Axis);
                    return Math.Abs(R + h * Math.Tan(R2) - o.R) < tol;
                }
        }
        return false;
    }

    public bool SameAxisLine(NSurface o, double tol)
    {
        if (Math.Abs(Math.Abs(Vec3.Dot(Axis, o.Axis)) - 1) > 1e-10) return false;
        var d = o.Origin - Origin;
        return (d - Axis * Vec3.Dot(d, Axis)).Length < tol;
    }

    // ---------------------------------------------------------------------------------------
    // ray intersection (all crossings, t > 0 not enforced)

    public List<double> RayHits(Vec3 o, Vec3 d)
    {
        var res = new List<double>();
        var lo = F.ToLocal3(o);
        var ld = new Vec3(Vec3.Dot(d, F.XDir), Vec3.Dot(d, F.YDir), Vec3.Dot(d, F.Normal));
        void Quad(double a, double b, double c)
        {
            if (Math.Abs(a) < 1e-14)
            {
                if (Math.Abs(b) > 1e-14) res.Add(-c / b);
                return;
            }
            var disc = b * b - 4 * a * c;
            if (disc < 0) return;
            var sq = Math.Sqrt(disc);
            var q = -0.5 * (b + Math.Sign(b == 0 ? 1 : b) * sq);
            res.Add(q / a);
            if (Math.Abs(q) > 1e-300) res.Add(c / q);
        }
        switch (Kind)
        {
            case SurfaceKind.Plane:
                if (Math.Abs(ld.Z) > 1e-15) res.Add(-lo.Z / ld.Z);
                break;
            case SurfaceKind.Cylinder:
                Quad(ld.X * ld.X + ld.Y * ld.Y, 2 * (lo.X * ld.X + lo.Y * ld.Y), lo.X * lo.X + lo.Y * lo.Y - R * R);
                break;
            case SurfaceKind.Sphere:
                Quad(ld.LengthSq, 2 * Vec3.Dot(lo, ld), lo.LengthSq - R * R);
                break;
            case SurfaceKind.Cone:
                {
                    var k = Math.Tan(R2);
                    // x² + y² = (R + k z)²
                    var a = ld.X * ld.X + ld.Y * ld.Y - k * k * ld.Z * ld.Z;
                    var b = 2 * (lo.X * ld.X + lo.Y * ld.Y) - 2 * k * ld.Z * (R + k * lo.Z);
                    var c = lo.X * lo.X + lo.Y * lo.Y - (R + k * lo.Z) * (R + k * lo.Z);
                    Quad(a, b, c);
                    res.RemoveAll(t => R + k * (lo.Z + t * ld.Z) < -1e-12); // other nappe
                    break;
                }
            case SurfaceKind.Torus:
                {
                    // bounding sphere interval, then sign changes of the implicit function
                    var big = R + R2;
                    var bb = Vec3.Dot(lo, ld); var cc = lo.LengthSq - big * big; var aa = ld.LengthSq;
                    var disc = bb * bb - aa * cc;
                    if (disc < 0) break;
                    var sq = Math.Sqrt(disc);
                    double t0 = (-bb - sq) / aa, t1 = (-bb + sq) / aa;
                    int n = 96;
                    double prevT = t0, prevF = F_(o + d * t0);
                    for (int i = 1; i <= n; i++)
                    {
                        var t = t0 + (t1 - t0) * i / n;
                        var f = F_(o + d * t);
                        if (prevF == 0) res.Add(prevT);
                        else if (Math.Sign(f) != Math.Sign(prevF)) res.Add(Roots.Bisect(x => F_(o + d * x), prevT, t));
                        prevT = t; prevF = f;
                    }
                    break;
                }
        }
        return res;
    }

    // ---------------------------------------------------------------------------------------
    // profile in the (r, h) half plane of an axis (for coaxial intersections)

    /// <summary>Profile primitive of a surface of revolution about the given axis.</summary>
    public abstract record Profile;
    public sealed record HLine(double H) : Profile;                  // plane ⟂ axis
    public sealed record VLine(double R) : Profile;                  // cylinder
    public sealed record SLine(Vec2 P, Vec2 Dir) : Profile;          // cone generator (r, h)
    public sealed record PCircle(Vec2 C, double Rad) : Profile;      // sphere / torus section

    /// <summary>The surface's profile about (origin, axis) if it is a surface of revolution about that axis.</summary>
    public Profile? ProfileAbout(Vec3 origin, Vec3 axis, double tol)
    {
        axis = axis.Normalized;
        double H(Vec3 p) => Vec3.Dot(p - origin, axis);
        bool OnAxis(Vec3 p) { var d = p - origin; return (d - axis * Vec3.Dot(d, axis)).Length < tol; }
        switch (Kind)
        {
            case SurfaceKind.Plane:
                return Math.Abs(Math.Abs(Vec3.Dot(Plane!.Normal, axis)) - 1) < 1e-10 ? new HLine(H(Plane.Origin)) : null;
            case SurfaceKind.Sphere:
                return OnAxis(Origin) ? new PCircle(new Vec2(0, H(Origin)), R) : null;
        }
        if (Math.Abs(Math.Abs(Vec3.Dot(Axis, axis)) - 1) > 1e-10 || !OnAxis(Origin)) return null;
        var s = Vec3.Dot(Axis, axis) > 0 ? 1.0 : -1.0;
        switch (Kind)
        {
            case SurfaceKind.Cylinder: return new VLine(R);
            case SurfaceKind.Cone:
                {
                    // r = R + k * h_local, h_local = s * (h - h0)
                    var h0 = H(Origin);
                    var k = Math.Tan(R2);
                    // direction of increasing local h in (r, h): (k, s) normalized
                    return new SLine(new Vec2(R, h0), new Vec2(k, s).Normalized);
                }
            case SurfaceKind.Torus: return new PCircle(new Vec2(R, H(Origin)), R2);
        }
        return null;
    }

    public NSurface Transformed(in Transform t)
    {
        if (Kind == SurfaceKind.Plane) return FromPlane(Plane!.Transformed(t));
        var d = Def.Transformed(t);
        if (Math.Abs(t.Determinant - 1) > 1e-9 && Math.Abs(t.Determinant + 1) > 1e-9)
        {
            var s = Math.Cbrt(Math.Abs(t.Determinant));
            d = d with { Radius = d.Radius * s, Param2 = Kind == SurfaceKind.Torus ? d.Param2 * s : d.Param2 };
        }
        return new NSurface(d);
    }

    public string Describe() => Exact.Describe();
}

public static class Roots
{
    /// <summary>Root of f in [a,b] given a sign change (bisection + secant polish).</summary>
    public static double Bisect(Func<double, double> f, double a, double b, int iters = 100)
    {
        double fa = f(a), fb = f(b);
        if (fa == 0) return a;
        if (fb == 0) return b;
        for (int i = 0; i < iters; i++)
        {
            var m = 0.5 * (a + b);
            var fm = f(m);
            if (fm == 0 || Math.Abs(b - a) < 1e-15 * Math.Max(1, Math.Abs(m))) return m;
            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; } else { b = m; fb = fm; }
        }
        return 0.5 * (a + b);
    }

    /// <summary>Minimum of |f| on [a,b] by golden section (for tangential touches).</summary>
    public static double MinAbs(Func<double, double> f, double a, double b)
    {
        for (int i = 0; i < 80; i++)
        {
            var m1 = a + (b - a) * 0.382; var m2 = a + (b - a) * 0.618;
            if (Math.Abs(f(m1)) < Math.Abs(f(m2))) b = m2; else a = m1;
        }
        return 0.5 * (a + b);
    }
}

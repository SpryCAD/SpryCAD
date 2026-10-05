namespace FeatherCad.Kernel;

/// <summary>Oriented plane: points p with Dot(Normal, p) == D. Normal is unit length.</summary>
public sealed class Plane
{
    public readonly Vec3 Normal;
    public readonly double D;

    public Plane(Vec3 normal, double d) { Normal = normal; D = d; }

    public static Plane FromPointNormal(Vec3 p, Vec3 n)
    {
        n = n.Normalized;
        return new Plane(n, Vec3.Dot(n, p));
    }

    /// <summary>Plane through three points (CCW gives normal by right hand rule). Null if degenerate.</summary>
    public static Plane? FromPoints(Vec3 a, Vec3 b, Vec3 c)
    {
        var n = Vec3.Cross(b - a, c - a);
        var l = n.Length;
        if (l < 1e-14) return null;
        n /= l;
        return new Plane(n, Vec3.Dot(n, a));
    }

    /// <summary>Best fit plane of a polygon using Newell's method.</summary>
    public static Plane? FromPolygon(IReadOnlyList<Vec3> pts)
    {
        double nx = 0, ny = 0, nz = 0;
        var c = Vec3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
            c += a;
        }
        var n = new Vec3(nx, ny, nz);
        var l = n.Length;
        if (l < 1e-14) return null;
        n /= l;
        c /= pts.Count;
        return new Plane(n, Vec3.Dot(n, c));
    }

    public double Distance(Vec3 p) => Vec3.Dot(Normal, p) - D;
    public Vec3 Project(Vec3 p) => p - Normal * Distance(p);
    public Plane Flipped() => new(-Normal, -D);
    public Vec3 Origin => Normal * D;
    public Plane Offset(double d) => new(Normal, D + d);

    public Plane Transformed(in Transform t)
    {
        var p = t.Point(Origin);
        var n = t.Normal(Normal);
        return FromPointNormal(p, n);
    }

    /// <summary>Intersects a line p + t*dir. Returns t or NaN.</summary>
    public double IntersectRay(Vec3 p, Vec3 dir)
    {
        var den = Vec3.Dot(Normal, dir);
        if (Math.Abs(den) < 1e-15) return double.NaN;
        return (D - Vec3.Dot(Normal, p)) / den;
    }

    public bool IsCoplanar(Plane o, double angTol = 1e-9, double distTol = 1e-6) =>
        Vec3.Dot(Normal, o.Normal) > 1 - angTol && Math.Abs(D - o.D) < distTol;

    public override string ToString() => $"Plane(n={Normal}, d={D:0.####})";
}

/// <summary>Right-handed orthonormal coordinate frame. Used for sketch planes.</summary>
public readonly record struct Frame(Vec3 Origin, Vec3 XDir, Vec3 YDir, Vec3 Normal)
{
    public static readonly Frame World = new(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);

    /// <summary>SolidWorks "Front" plane: XY, normal +Z.</summary>
    public static readonly Frame Front = new(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
    /// <summary>SolidWorks "Top" plane: XZ, normal +Y (sketch X = world X, sketch Y = world -Z).</summary>
    public static readonly Frame Top = new(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
    /// <summary>SolidWorks "Right" plane: YZ, normal +X (sketch X = world -Z, sketch Y = world Y).</summary>
    public static readonly Frame Right = new(Vec3.Zero, -Vec3.UnitZ, Vec3.UnitY, Vec3.UnitX);

    public Vec3 ToWorld(Vec2 p) => Origin + XDir * p.X + YDir * p.Y;
    public Vec3 ToWorld(double x, double y) => Origin + XDir * x + YDir * y;
    public Vec3 ToWorld(double x, double y, double z) => Origin + XDir * x + YDir * y + Normal * z;
    public Vec2 ToLocal(Vec3 p) { var d = p - Origin; return new Vec2(Vec3.Dot(d, XDir), Vec3.Dot(d, YDir)); }
    public Vec3 ToLocal3(Vec3 p) { var d = p - Origin; return new Vec3(Vec3.Dot(d, XDir), Vec3.Dot(d, YDir), Vec3.Dot(d, Normal)); }
    public Plane Plane => Plane.FromPointNormal(Origin, Normal);

    /// <summary>
    /// Build a frame on an arbitrary plane the way SolidWorks orients sketches on faces:
    /// the origin is the world origin projected on the plane and the X axis follows the
    /// most closely aligned standard plane.
    /// </summary>
    public static Frame FromPlane(Plane plane)
    {
        var n = plane.Normal;
        var origin = plane.Project(Vec3.Zero);
        Frame best = Front; double bestDot = -1;
        foreach (var f in new[] { Front, Top, Right })
        {
            var d = Math.Abs(Vec3.Dot(f.Normal, n));
            if (d > bestDot + 1e-9) { bestDot = d; best = f; }
        }
        var x = best.XDir;
        if (Vec3.Dot(best.Normal, n) < 0) x = -x; // keep handedness sensible when looking from the back
        x = (x - n * Vec3.Dot(x, n));
        if (x.Length < 1e-9) x = n.AnyPerpendicular();
        x = x.Normalized;
        var y = Vec3.Cross(n, x).Normalized;
        return new Frame(origin, x, y, n);
    }

    public Frame Transformed(in Transform t)
    {
        var o = t.Point(Origin);
        var x = t.Vector(XDir).Normalized;
        var nn = t.Normal(Normal);
        var y = Vec3.Cross(nn, x).Normalized;
        return new Frame(o, x, y, nn);
    }

    public Frame Offset(double d) => this with { Origin = Origin + Normal * d };
    public Frame Flipped() => new(Origin, -XDir, YDir, -Normal);
}

/// <summary>Affine transform: 3x3 linear part (row major) + translation.</summary>
public readonly struct Transform
{
    public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;
    public readonly Vec3 T;

    public Transform(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22, Vec3 t)
    {
        M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22; T = t;
    }

    public static readonly Transform Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1, Vec3.Zero);

    public static Transform Translation(Vec3 t) => new(1, 0, 0, 0, 1, 0, 0, 0, 1, t);
    public static Transform Scale(double s) => new(s, 0, 0, 0, s, 0, 0, 0, s, Vec3.Zero);

    /// <summary>Rotation about an axis through origin by angle (radians, right hand rule).</summary>
    public static Transform Rotation(Vec3 origin, Vec3 axis, double angle)
    {
        axis = axis.Normalized;
        double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
        double x = axis.X, y = axis.Y, z = axis.Z;
        var r = new Transform(
            t * x * x + c, t * x * y - s * z, t * x * z + s * y,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c, Vec3.Zero);
        // p' = R(p - o) + o
        var to = r.Vector(origin);
        return new Transform(r.M00, r.M01, r.M02, r.M10, r.M11, r.M12, r.M20, r.M21, r.M22, origin - to);
    }

    /// <summary>Reflection about a plane.</summary>
    public static Transform Mirror(Plane p)
    {
        var n = p.Normal;
        var m = new Transform(
            1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Z,
            -2 * n.Y * n.X, 1 - 2 * n.Y * n.Y, -2 * n.Y * n.Z,
            -2 * n.Z * n.X, -2 * n.Z * n.Y, 1 - 2 * n.Z * n.Z,
            n * (2 * p.D));
        return m;
    }

    public Vec3 Vector(Vec3 v) => new(M00 * v.X + M01 * v.Y + M02 * v.Z, M10 * v.X + M11 * v.Y + M12 * v.Z, M20 * v.X + M21 * v.Y + M22 * v.Z);
    public Vec3 Point(Vec3 p) => Vector(p) + T;
    /// <summary>Transforms a normal. Valid for rigid motions, reflections and uniform scale.</summary>
    public Vec3 Normal(Vec3 n) => Vector(n).Normalized;
    public double Determinant => M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);
    public bool IsMirror => Determinant < 0;
    public bool IsIdentity => M00 == 1 && M11 == 1 && M22 == 1 && M01 == 0 && M02 == 0 && M10 == 0 && M12 == 0 && M20 == 0 && M21 == 0 && T == Vec3.Zero;

    /// <summary>Returns a * b (apply b first, then a).</summary>
    public static Transform operator *(Transform a, Transform b)
    {
        return new Transform(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22,
            a.Point(b.T));
    }
}

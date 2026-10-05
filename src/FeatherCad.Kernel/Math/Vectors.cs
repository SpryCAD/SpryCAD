using System.Runtime.CompilerServices;

namespace FeatherCad.Kernel;

/// <summary>Immutable 2D vector (double precision).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSq => X * X + Y * Y;
    public Vec2 Normalized { get { var l = Length; return l > 1e-300 ? this / l : Zero; } }
    public Vec2 Perp => new(-Y, X);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;
    public double Angle => Math.Atan2(Y, X);
    public static Vec2 FromAngle(double a, double r = 1) => new(Math.Cos(a) * r, Math.Sin(a) * r);
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>Immutable 3D vector (double precision).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double LengthSq => X * X + Y * Y + Z * Z;
    public Vec3 Normalized { get { var l = Length; return l > 1e-300 ? this / l : Zero; } }
    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
    public static Vec3 Min(Vec3 a, Vec3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    public static Vec3 Max(Vec3 a, Vec3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    /// <summary>Any unit vector perpendicular to this one.</summary>
    public Vec3 AnyPerpendicular()
    {
        var a = Math.Abs(X) < 0.9 ? UnitX : UnitY;
        return Cross(this, a).Normalized;
    }

    public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;
    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

/// <summary>Axis aligned bounding box.</summary>
public struct Box3
{
    public Vec3 Min, Max;
    public static Box3 Empty => new() { Min = new(double.MaxValue, double.MaxValue, double.MaxValue), Max = new(double.MinValue, double.MinValue, double.MinValue) };
    public readonly bool IsEmpty => Min.X > Max.X;
    public void Add(Vec3 p) { Min = Vec3.Min(Min, p); Max = Vec3.Max(Max, p); }
    public void Add(Box3 b) { if (!b.IsEmpty) { Add(b.Min); Add(b.Max); } }
    public readonly Vec3 Center => (Min + Max) * 0.5;
    public readonly Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;
    public readonly double Diagonal => IsEmpty ? 0 : (Max - Min).Length;
    public readonly bool Overlaps(Box3 o, double tol = 0) =>
        !(o.Min.X > Max.X + tol || o.Max.X < Min.X - tol ||
          o.Min.Y > Max.Y + tol || o.Max.Y < Min.Y - tol ||
          o.Min.Z > Max.Z + tol || o.Max.Z < Min.Z - tol);
}

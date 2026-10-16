namespace SpryCad.SheetMetal;

// The few geometric types the bend graph needs (sheet metal spec section 2: Core references nothing but its own math).
// Lengths in millimetres, angles in radians, as in the rest of SpryCAD.

public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;
    /// <summary>The z of a × b.</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public Vec2 Normalized() { var l = Length; return l > 0 ? new Vec2(X / l, Y / l) : this; }
    /// <summary>Turned a quarter turn counterclockwise: the left of this direction.</summary>
    public Vec2 Left => new(-Y, X);
    public override string ToString() => FormattableString.Invariant($"({X:0.######}, {Y:0.######})");
}

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3 Normalized() { var l = Length; return l > 0 ? new Vec3(X / l, Y / l, Z / l) : this; }
    public static Vec3 From(Vec2 p, double z = 0) => new(p.X, p.Y, z);
    public override string ToString() => FormattableString.Invariant($"({X:0.######}, {Y:0.######}, {Z:0.######})");
}

/// <summary>A line in the flat: a point on it and its unit direction. Its left (<see cref="Normal"/>) is a side of it.</summary>
public readonly record struct Line2(Vec2 Point, Vec2 Dir)
{
    public static Line2 Through(Vec2 a, Vec2 b) => new(a, (b - a).Normalized());
    public Vec2 Normal => Dir.Left;
    /// <summary>How far <paramref name="p"/> is to the left of the line (negative: to the right).</summary>
    public double Side(Vec2 p) => Vec2.Dot(p - Point, Normal);
    public Line2 Offset(double d) => new(Point + Normal * d, Dir);
}

/// <summary>
/// A rigid transform (rotation, then translation): where a panel's flat coordinates go in the folded part. The flat
/// is the plane z = 0 at the sheet's top side, the material from z = -t to 0.
/// </summary>
public readonly record struct Frame3(Vec3 X, Vec3 Y, Vec3 Z, Vec3 Origin)
{
    public static readonly Frame3 Identity = new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0, 0, 0));

    public Vec3 Apply(Vec3 p) => X * p.X + Y * p.Y + Z * p.Z + Origin;
    public Vec3 ApplyDir(Vec3 d) => X * d.X + Y * d.Y + Z * d.Z;
    public Vec3 Apply(Vec2 p) => Apply(Vec3.From(p));

    /// <summary>This after <paramref name="inner"/>: p → this(inner(p)).</summary>
    public Frame3 Compose(Frame3 inner) => new(ApplyDir(inner.X), ApplyDir(inner.Y), ApplyDir(inner.Z), Apply(inner.Origin));

    public static Frame3 Translation(Vec3 v) => Identity with { Origin = v };

    /// <summary>A rotation by <paramref name="angle"/> (right-handed about <paramref name="axis"/>) about the line through <paramref name="point"/>.</summary>
    public static Frame3 Rotation(Vec3 point, Vec3 axis, double angle)
    {
        var k = axis.Normalized();
        double c = Math.Cos(angle), s = Math.Sin(angle);
        Vec3 Rot(Vec3 v) => v * c + Vec3.Cross(k, v) * s + k * (Vec3.Dot(k, v) * (1 - c));
        var r = new Frame3(Rot(new(1, 0, 0)), Rot(new(0, 1, 0)), Rot(new(0, 0, 1)), default);
        return r with { Origin = point - r.ApplyDir(point) };
    }
}

/// <summary>A planar region in the flat: an outer loop (counterclockwise) and holes (clockwise), straight segments.</summary>
public sealed record Polygon2(IReadOnlyList<Vec2> Outer, IReadOnlyList<IReadOnlyList<Vec2>> Holes)
{
    public Polygon2(IReadOnlyList<Vec2> outer) : this(outer, Array.Empty<IReadOnlyList<Vec2>>()) { }

    public static Polygon2 Rectangle(Vec2 min, Vec2 max) =>
        new(new[] { min, new Vec2(max.X, min.Y), max, new Vec2(min.X, max.Y) });

    /// <summary>The signed area of a loop (positive counterclockwise).</summary>
    public static double LoopArea(IReadOnlyList<Vec2> loop)
    {
        double a = 0;
        for (int i = 0; i < loop.Count; i++) a += Vec2.Cross(loop[i], loop[(i + 1) % loop.Count]);
        return a / 2;
    }

    public double Area => Math.Abs(LoopArea(Outer)) - Holes.Sum(h => Math.Abs(LoopArea(h)));

    public IEnumerable<Vec2> Points => Outer.Concat(Holes.SelectMany(h => h));
}

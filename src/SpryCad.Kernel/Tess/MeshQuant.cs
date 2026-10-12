namespace SpryCad.Kernel;

/// <summary>
/// Unit normals in two 16-bit numbers (octahedral encoding: the sphere folded onto a square), for saved display
/// meshes. Each is rounded to the nearest of the four neighbouring codes that decodes closest to it: under
/// 0.005° from the normal it was given.
/// </summary>
public static class OctNormal
{
    const float Max = 32767;

    public static (short a, short b) Encode(float x, float y, float z)
    {
        var l1 = MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z);
        if (l1 < 1e-30f) return (0, (short)Max); // (no normal: +Z, as a zero vector has no direction)
        float u = x / l1, v = y / l1;
        if (z < 0) (u, v) = ((1 - MathF.Abs(v)) * Sign(u), (1 - MathF.Abs(u)) * Sign(v));
        // the best of the codes around (u, v)
        float fu = MathF.Floor(u * Max), fv = MathF.Floor(v * Max);
        (short, short) best = default; double bestDot = double.NegativeInfinity;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                var a = (short)Math.Clamp(fu + i, -Max, Max); var b = (short)Math.Clamp(fv + j, -Max, Max);
                var (dx, dy, dz) = Decode(a, b);
                var dot = ((double)dx * x + (double)dy * y + (double)dz * z) / Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);
                if (dot > bestDot) { bestDot = dot; best = (a, b); }
            }
        return best;
    }

    public static (float x, float y, float z) Decode(short a, short b)
    {
        float x = a / Max, y = b / Max, z = 1 - MathF.Abs(x) - MathF.Abs(y);
        var t = MathF.Max(-z, 0);
        x += x >= 0 ? -t : t;
        y += y >= 0 ? -t : t;
        var l = MathF.Sqrt(x * x + y * y + z * z);
        return (x / l, y / l, z / l);
    }

    static float Sign(float v) => v >= 0 ? 1 : -1;
}

/// <summary>
/// Points in three 16-bit numbers each: their place in a box, in 65535 steps per axis (an axis with no extent
/// takes the box's minimum). For display only: at most a 65535th of the box's size off on each axis.
/// </summary>
public readonly struct PointQuantizer
{
    public readonly Vec3 Min, Max;
    readonly double _sx, _sy, _sz; // steps per unit (0: no extent)

    public PointQuantizer(Vec3 min, Vec3 max)
    {
        Min = min; Max = max;
        static double S(double lo, double hi) => hi - lo > 0 ? 65535 / (hi - lo) : 0;
        _sx = S(min.X, max.X); _sy = S(min.Y, max.Y); _sz = S(min.Z, max.Z);
    }

    /// <summary>The box around the given xyz triples (0, 0, 0 for none).</summary>
    public static PointQuantizer Around(params float[][] coords)
    {
        var b = Box3.Empty;
        foreach (var c in coords) for (int i = 0; i + 2 < c.Length; i += 3) b.Add(new Vec3(c[i], c[i + 1], c[i + 2]));
        return b.IsEmpty ? new(Vec3.Zero, Vec3.Zero) : new(b.Min, b.Max);
    }

    public ushort X(double v) => Q(v, Min.X, _sx);
    public ushort Y(double v) => Q(v, Min.Y, _sy);
    public ushort Z(double v) => Q(v, Min.Z, _sz);

    static ushort Q(double v, double min, double s) => (ushort)Math.Clamp(Math.Round((v - min) * s), 0, 65535);

    public float DX(ushort q) => (float)(Min.X + q / 65535.0 * (Max.X - Min.X));
    public float DY(ushort q) => (float)(Min.Y + q / 65535.0 * (Max.Y - Min.Y));
    public float DZ(ushort q) => (float)(Min.Z + q / 65535.0 * (Max.Z - Min.Z));
}

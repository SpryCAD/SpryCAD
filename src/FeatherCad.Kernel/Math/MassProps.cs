namespace FeatherCad.Kernel;

/// <summary>
/// Mass properties of closed triangle meshes, at unit density (multiply masses and moments by the density):
/// volume, center of mass, the inertia tensor about the center of mass and about the origin, and the principal
/// moments and axes. Exact for the mesh (Eberly, "Polyhedral Mass Properties (Revisited)", by the divergence
/// theorem); a mesh following the exact surfaces closely gives values close to the exact part.
/// </summary>
public sealed class MassProps
{
    public double Volume;
    public double SurfaceArea;
    public Vec3 Center;
    /// <summary>Inertia tensor about the center of mass, world axes: [0..2][0..2], products negated (Ixy = -∫xy dm).</summary>
    public double[,] AtCenter = new double[3, 3];
    /// <summary>Inertia tensor about the origin, world axes (same convention).</summary>
    public double[,] AtOrigin = new double[3, 3];
    /// <summary>Principal moments about the center of mass, smallest first, and their axes (unit, right-handed).</summary>
    public double[] Principal = new double[3];
    public Vec3[] Axes = { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1) };

    readonly double[] _sum = new double[10];

    /// <summary>Adds a closed mesh's triangles (outward facing; a mesh facing inward counts negative).</summary>
    public void Add(float[] positions, int[] indices) => Add(positions, indices, 0, indices.Length);

    public void Add(float[] positions, int[] indices, int start, int count)
    {
        Vec3 P(int i) => new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        for (int k = start; k + 2 < start + count; k += 3) AddTriangle(P(indices[k]), P(indices[k + 1]), P(indices[k + 2]));
    }

    public void AddTriangle(Vec3 p0, Vec3 p1, Vec3 p2)
    {
        var d = Vec3.Cross(p1 - p0, p2 - p0);
        SurfaceArea += d.Length / 2;
        Sub(p0.X, p1.X, p2.X, out var f1x, out var f2x, out var f3x, out var g0x, out var g1x, out var g2x);
        Sub(p0.Y, p1.Y, p2.Y, out _, out var f2y, out var f3y, out var g0y, out var g1y, out var g2y);
        Sub(p0.Z, p1.Z, p2.Z, out _, out var f2z, out var f3z, out var g0z, out var g1z, out var g2z);
        _sum[0] += d.X * f1x;
        _sum[1] += d.X * f2x; _sum[2] += d.Y * f2y; _sum[3] += d.Z * f2z;
        _sum[4] += d.X * f3x; _sum[5] += d.Y * f3y; _sum[6] += d.Z * f3z;
        _sum[7] += d.X * (p0.Y * g0x + p1.Y * g1x + p2.Y * g2x);
        _sum[8] += d.Y * (p0.Z * g0y + p1.Z * g1y + p2.Z * g2y);
        _sum[9] += d.Z * (p0.X * g0z + p1.X * g1z + p2.X * g2z);
    }

    static void Sub(double w0, double w1, double w2, out double f1, out double f2, out double f3, out double g0, out double g1, out double g2)
    {
        double t0 = w0 + w1, t1 = w0 * w0, t2 = t1 + w1 * t0;
        f1 = t0 + w2; f2 = t2 + w2 * f1; f3 = w0 * t1 + w1 * t2 + w2 * f2;
        g0 = f2 + w0 * (f1 + w0); g1 = f2 + w1 * (f1 + w1); g2 = f2 + w2 * (f1 + w2);
    }

    /// <summary>Works out the results from the triangles added.</summary>
    public MassProps Finish()
    {
        double[] mult = { 1 / 6.0, 1 / 24.0, 1 / 24.0, 1 / 24.0, 1 / 60.0, 1 / 60.0, 1 / 60.0, 1 / 120.0, 1 / 120.0, 1 / 120.0 };
        var s = new double[10];
        for (int i = 0; i < 10; i++) s[i] = _sum[i] * mult[i];
        if (s[0] < 0) for (int i = 0; i < 10; i++) s[i] = -s[i]; // inward-facing mesh
        Volume = s[0];
        if (Volume < 1e-300) return this;
        Center = new Vec3(s[1], s[2], s[3]) / Volume;
        // about the origin: x² etc. integrals s4..s6, products s7 (xy), s8 (yz), s9 (zx)
        AtOrigin = Tensor(s[5] + s[6], s[4] + s[6], s[4] + s[5], s[7], s[8], s[9]);
        // about the center of mass (parallel axis)
        double m = Volume, cx = Center.X, cy = Center.Y, cz = Center.Z;
        AtCenter = Tensor(
            s[5] + s[6] - m * (cy * cy + cz * cz), s[4] + s[6] - m * (cz * cz + cx * cx), s[4] + s[5] - m * (cx * cx + cy * cy),
            s[7] - m * cx * cy, s[8] - m * cy * cz, s[9] - m * cz * cx);
        (Principal, Axes) = Eigen(AtCenter);
        return this;
    }

    static double[,] Tensor(double ixx, double iyy, double izz, double pxy, double pyz, double pzx) => new double[,]
    {
        { ixx, -pxy, -pzx },
        { -pxy, iyy, -pyz },
        { -pzx, -pyz, izz },
    };

    /// <summary>Eigenvalues (ascending) and unit eigenvectors (right-handed) of a symmetric 3×3 matrix (Jacobi).</summary>
    public static (double[] values, Vec3[] vectors) Eigen(double[,] m)
    {
        var a = (double[,])m.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            double scale = Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]);
            if (off <= 1e-15 * Math.Max(scale, 1e-300)) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300) continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var order = new[] { 0, 1, 2 }.OrderBy(i => a[i, i]).ToArray();
        var values = order.Select(i => a[i, i]).ToArray();
        var vecs = order.Select(i => new Vec3(v[0, i], v[1, i], v[2, i]).Normalized).ToArray();
        if (Vec3.Dot(Vec3.Cross(vecs[0], vecs[1]), vecs[2]) < 0) vecs[2] = -vecs[2];
        return (values, vecs);
    }
}

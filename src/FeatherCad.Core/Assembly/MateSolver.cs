using FeatherCad.Kernel;

namespace FeatherCad.Core.Assembly;

/// <summary>
/// Places floating components so their mates hold: each floating component that takes part in a mate is a rigid body
/// (a turn about a point of it and a move: six unknowns), fixed components stay; the mates' residuals are driven to
/// zero by Levenberg-Marquardt. Its damping keeps the motion small: components settle near where they were (or were
/// dragged to), and of two ways to satisfy the mates the nearer is taken. Mates left unsatisfied (conflicting, or
/// over-defined differently) are reported with their error.
/// </summary>
public static class MateSolver
{
    public sealed class Result
    {
        /// <summary>By mate id: the largest residual left (mm, or radians for directions).</summary>
        public Dictionary<string, double> Errors { get; } = new();
        /// <summary>Components moved.</summary>
        public HashSet<string> Moved { get; } = new();
        public bool Satisfied { get; set; }
        public int Iterations { get; set; }
        public IEnumerable<string> Failed(double tol = 1e-6) => Errors.Where(e => e.Value > tol).Select(e => e.Key);
    }

    /// <summary>
    /// Solves the assembly's unsuppressed mates, moving its floating components (only <paramref name="onlyMove"/>
    /// when given: a new mate moves its first component). <paramref name="local"/>: a reference's geometry in its
    /// part's coordinates.
    /// </summary>
    public static Result Solve(AssemblyDocument doc, Func<MateRef, MateGeom> local, ISet<string>? onlyMove = null, int maxIter = 80)
    {
        var res = new Result();
        bool Live(MateRef r) => r.Component == null || doc.Find(r.Component) is { Suppressed: false };
        var mates = doc.Mates.Where(m => !m.Suppressed && Live(m.A) && Live(m.B) && Mate.Supports(m.Type, local(m.A).Kind, local(m.B).Kind)).ToList();
        var free = new List<Component>();
        foreach (var m in mates)
            foreach (var r in new[] { m.A, m.B })
                if (r.Component != null && doc.Find(r.Component) is { Fixed: false } c && (onlyMove == null || onlyMove.Contains(c.Id)) && !free.Contains(c))
                    free.Add(c);
        var placements = doc.Components.ToDictionary(c => c.Id, c => c.Placement.ToTransform());
        Transform PlacementOf(string? id, Transform[]? trial) =>
            id == null ? Transform.Identity : trial != null && free.FindIndex(c => c.Id == id) is var i and >= 0 ? trial[i] : placements[id];
        MateGeom World(MateRef r, Transform[]? trial) => local(r).Placed(PlacementOf(r.Component, trial));

        // each free component turns about the middle of its references (well conditioned), in world coordinates
        var pivots = free.Select(c =>
        {
            var pts = mates.SelectMany(m => new[] { m.A, m.B }).Where(r => r.Component == c.Id).Select(r => World(r, null).P).ToList();
            var sum = Vec3.Zero; foreach (var p in pts) sum += p;
            return pts.Count > 0 ? sum / pts.Count : placements[c.Id].T;
        }).ToArray();

        // a face to be turned half round onto its mate: turned first (from exactly opposite the solver cannot tell
        // which way to turn)
        foreach (var m in mates.Where(m => m.Type is MateType.Coincident or MateType.Distance))
        {
            var a = World(m.A, null); var b = World(m.B, null);
            if (a.Kind != MateGeomKind.Plane || b.Kind != MateGeomKind.Plane) continue;
            double s = m.Aligned ? 1 : -1;
            if (Vec3.Dot(b.D, a.D) * s > -0.5) continue;
            var (r, i) = free.FindIndex(c => c.Id == m.B.Component) is var ib and >= 0 ? (m.B, ib) : (m.A, free.FindIndex(c => c.Id == m.A.Component));
            if (i < 0) continue;
            var d = World(r, null).D;
            var turnAxis = d.AnyPerpendicular();
            var id = free[i].Id;
            placements[id] = Transform.Rotation(World(r, null).P, turnAxis, Math.PI) * placements[id];
        }

        int n = free.Count * 6;
        var x = new double[n];
        Transform[] Trial(double[] dx)
        {
            var t = new Transform[free.Count];
            for (int i = 0; i < free.Count; i++)
            {
                var w = new Vec3(dx[6 * i], dx[6 * i + 1], dx[6 * i + 2]);
                var mv = new Vec3(dx[6 * i + 3], dx[6 * i + 4], dx[6 * i + 5]);
                var rot = w.Length > 1e-15 ? Transform.Rotation(pivots[i], w / w.Length, w.Length) : Transform.Identity;
                t[i] = Transform.Translation(mv) * rot * placements[free[i].Id];
            }
            return t;
        }
        double[] Res(Transform[]? trial)
        {
            var r = new List<double>();
            foreach (var m in mates) m.Residuals(World(m.A, trial), World(m.B, trial), r);
            return r.ToArray();
        }
        static double Sq(double[] v) { double s = 0; foreach (var a in v) s += a * a; return s; }

        const double tol = 1e-10;
        double lambda = 1e-3;
        int it = 0;
        var r0 = Res(null);
        for (; it < maxIter && n > 0 && r0.Length > 0 && r0.Max(Math.Abs) > tol; it++)
        {
            // Jacobian by differences about the current placements
            var zero = new double[n];
            var J = new double[r0.Length, n];
            const double h = 1e-7;
            for (int j = 0; j < n; j++)
            {
                zero[j] = h;
                var rj = Res(Trial(zero));
                zero[j] = 0;
                for (int k = 0; k < r0.Length; k++) J[k, j] = (rj[k] - r0[k]) / h;
            }
            var A = new double[n, n]; var g = new double[n];
            for (int a = 0; a < n; a++)
            {
                for (int b = a; b < n; b++)
                {
                    double s = 0;
                    for (int k = 0; k < r0.Length; k++) s += J[k, a] * J[k, b];
                    A[a, b] = A[b, a] = s;
                }
                double sg = 0;
                for (int k = 0; k < r0.Length; k++) sg += J[k, a] * r0[k];
                g[a] = -sg;
            }
            double f0 = Sq(r0);
            bool stepped = false;
            for (int tries = 0; tries < 12 && !stepped; tries++)
            {
                var M = (double[,])A.Clone();
                for (int a = 0; a < n; a++) M[a, a] += lambda * (1 + A[a, a]);
                var dx = SolveLinear(M, (double[])g.Clone());
                if (dx == null) { lambda *= 10; continue; }
                var trial = Trial(dx);
                var r1 = Res(trial);
                if (Sq(r1) < f0)
                {
                    for (int i = 0; i < free.Count; i++) placements[free[i].Id] = trial[i];
                    r0 = r1;
                    lambda = Math.Max(lambda / 4, 1e-12);
                    stepped = true;
                }
                else lambda *= 6;
            }
            if (!stepped) break;
        }
        res.Iterations = it;

        // the components moved, and what each mate is left with
        foreach (var c in free)
        {
            var before = c.Placement.ToTransform(); var after = placements[c.Id];
            if (Differs(before, after)) { c.Placement = Placement.From(after); res.Moved.Add(c.Id); }
        }
        foreach (var m in doc.Mates)
        {
            if (m.Suppressed) continue;
            var r = new List<double>();
            if (Live(m.A) && Live(m.B) && Mate.Supports(m.Type, local(m.A).Kind, local(m.B).Kind)) m.Residuals(World(m.A, null), World(m.B, null), r);
            else r.Add(double.PositiveInfinity);
            res.Errors[m.Id] = r.Count == 0 ? 0 : r.Max(Math.Abs);
        }
        res.Satisfied = res.Errors.Values.All(e => e <= 1e-6);
        return res;
    }

    static bool Differs(Transform a, Transform b) =>
        Vec3.Distance(a.T, b.T) > 1e-12 || Math.Abs(a.M00 - b.M00) + Math.Abs(a.M01 - b.M01) + Math.Abs(a.M02 - b.M02) + Math.Abs(a.M10 - b.M10)
            + Math.Abs(a.M11 - b.M11) + Math.Abs(a.M12 - b.M12) + Math.Abs(a.M20 - b.M20) + Math.Abs(a.M21 - b.M21) + Math.Abs(a.M22 - b.M22) > 1e-12;

    /// <summary>Gaussian elimination with partial pivoting; null when singular.</summary>
    static double[]? SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int c = 0; c < n; c++)
        {
            int p = c; double best = Math.Abs(a[c, c]);
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > best) { best = Math.Abs(a[r, c]); p = r; }
            if (best < 1e-300) return null;
            if (p != c)
            {
                for (int k = 0; k < n; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
                (b[c], b[p]) = (b[p], b[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                var f = a[r, c] / a[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) a[r, k] -= f * a[c, k];
                b[r] -= f * b[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = b[r];
            for (int k = r + 1; k < n; k++) s -= a[r, k] * x[k];
            x[r] = s / a[r, r];
        }
        return x;
    }
}

using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Places floating components so their mates hold: each floating component that takes part in a mate is a rigid body
/// (a turn about a point of it and a move: six unknowns), fixed components stay; the mates' residuals are driven to
/// zero by Levenberg-Marquardt. Its damping keeps the motion small: components settle near where they were (or were
/// dragged to), and of two ways to satisfy the mates the nearer is taken. Mates left unsatisfied (conflicting, or
/// over-defined differently) are reported with their error.
/// </summary>
/// <remarks>
/// Sparse throughout, as assemblies are: a mate depends on its two components only, so the Jacobian is made a
/// component at a time from that component's mates, and the normal equations (a 6x6 block for each component and
/// each pair mated together) are ordered so mated components sit close (reverse Cuthill-McKee) and factored within
/// their envelope (skyline Cholesky). A chain or a tree of components then costs about its size, not its cube.
/// </remarks>
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
        var byId = new Dictionary<string, Component>();
        foreach (var c in doc.Components) byId.TryAdd(c.Id, c);
        // each reference's geometry looked up once (finding a face among a part's faces is not free)
        var geo = new Dictionary<MateRef, MateGeom>(ReferenceEqualityComparer.Instance);
        MateGeom L(MateRef r)
        {
            if (!geo.TryGetValue(r, out var g)) geo[r] = g = local(r);
            return g;
        }
        bool Live(MateRef r) => r.Component == null || byId.TryGetValue(r.Component, out var c) && !c.Suppressed;
        var mates = doc.Mates.Where(m => !m.Suppressed && Live(m.A) && Live(m.B) && Mate.Supports(m.Type, L(m.A), L(m.B))).ToList();
        var free = new List<Component>();
        var freeIdx = new Dictionary<string, int>();
        foreach (var m in mates)
            foreach (var r in new[] { m.A, m.B })
                if (r.Component != null && byId[r.Component] is { Fixed: false } c && (onlyMove == null || onlyMove.Contains(c.Id)) && !freeIdx.ContainsKey(c.Id))
                {
                    freeIdx[c.Id] = free.Count;
                    free.Add(c);
                }
        var placements = doc.Components.ToDictionary(c => c.Id, c => c.Placement.ToTransform());
        Transform PlacementOf(string? id, Transform[]? trial) =>
            id == null ? Transform.Identity : trial != null && freeIdx.TryGetValue(id, out var i) ? trial[i] : placements[id];
        MateGeom World(MateRef r, Transform[]? trial) => L(r).Placed(PlacementOf(r.Component, trial));
        int FreeOf(MateRef r) => r.Component != null && freeIdx.TryGetValue(r.Component, out var i) ? i : -1;

        // each free component turns about the middle of its references (well conditioned), in world coordinates
        var refPoints = new Vec3[free.Count]; var refCount = new int[free.Count];
        foreach (var m in mates)
            foreach (var r in new[] { m.A, m.B })
                if (FreeOf(r) is var i and >= 0) { refPoints[i] += World(r, null).P; refCount[i]++; }
        var pivots = free.Select((c, i) => refCount[i] > 0 ? refPoints[i] / refCount[i] : placements[c.Id].T).ToArray();

        // a face to be turned half round onto its mate: turned first (from exactly opposite the solver cannot tell
        // which way to turn)
        foreach (var m in mates.Where(m => m.Type is MateType.Coincident or MateType.Distance && m.Min == null))
        {
            var a = World(m.A, null); var b = World(m.B, null);
            if (a.Kind != MateGeomKind.Plane || b.Kind != MateGeomKind.Plane) continue;
            double s = m.Aligned ? 1 : -1;
            if (Vec3.Dot(b.D, a.D) * s > -0.5) continue;
            var (r, i) = FreeOf(m.B) is var ib and >= 0 ? (m.B, ib) : (m.A, FreeOf(m.A));
            if (i < 0) continue;
            var d = World(r, null).D;
            var id = free[i].Id;
            placements[id] = Transform.Rotation(World(r, null).P, d.AnyPerpendicular(), Math.PI) * placements[id];
        }

        int nf = free.Count, n = nf * 6;
        // a component moved by six numbers: a turn (about its pivot) and a move
        Transform Moved(int i, double w0, double w1, double w2, double m0, double m1, double m2)
        {
            var w = new Vec3(w0, w1, w2);
            var rot = w.Length > 1e-15 ? Transform.Rotation(pivots[i], w / w.Length, w.Length) : Transform.Identity;
            return Transform.Translation(new Vec3(m0, m1, m2)) * rot * placements[free[i].Id];
        }
        Transform[] Trial(double[] dx)
        {
            var t = new Transform[nf];
            for (int i = 0; i < nf; i++) t[i] = Moved(i, dx[6 * i], dx[6 * i + 1], dx[6 * i + 2], dx[6 * i + 3], dx[6 * i + 4], dx[6 * i + 5]);
            return t;
        }

        // the mates' rows: each mate's residuals at a fixed place in the vector, and which free components it moves with
        var buf = new List<double>();
        void MateRes(Mate m, in Transform ta, in Transform tb)
        {
            buf.Clear();
            m.Residuals(L(m.A).Placed(ta), L(m.B).Placed(tb), ta, tb, buf);
        }
        var rowOf = new int[mates.Count + 1];
        var ca = new int[mates.Count]; var cb = new int[mates.Count];
        for (int k = 0; k < mates.Count; k++)
        {
            var m = mates[k];
            MateRes(m, PlacementOf(m.A.Component, null), PlacementOf(m.B.Component, null));
            rowOf[k + 1] = rowOf[k] + buf.Count;
            ca[k] = FreeOf(m.A); cb[k] = FreeOf(m.B);
        }
        int rows = rowOf[mates.Count];
        double[] Res(Transform[]? trial)
        {
            var r = new double[rows];
            for (int k = 0; k < mates.Count; k++)
            {
                var m = mates[k];
                MateRes(m, PlacementOf(m.A.Component, trial), PlacementOf(m.B.Component, trial));
                for (int q = 0; q < buf.Count && rowOf[k] + q < rowOf[k + 1]; q++) r[rowOf[k] + q] = buf[q];
            }
            return r;
        }
        var matesOf = new List<int>[nf];
        for (int i = 0; i < nf; i++) matesOf[i] = new List<int>();
        for (int k = 0; k < mates.Count; k++)
        {
            if (ca[k] >= 0) matesOf[ca[k]].Add(k);
            if (cb[k] >= 0 && cb[k] != ca[k]) matesOf[cb[k]].Add(k);
        }
        var sky = Skyline.For(nf, Enumerable.Range(0, mates.Count).Where(k => ca[k] >= 0 && cb[k] >= 0 && ca[k] != cb[k]).Select(k => (ca[k], cb[k])));
        static double Sq(double[] v) { double s = 0; foreach (var a in v) s += a * a; return s; }

        const double tol = 1e-10, h = 1e-6;
        double lambda = 1e-3;
        int it = 0;
        var r0 = Res(null);
        // the Jacobian: for each mate, its rows against each of its (at most two) free components' six numbers
        var Ja = new double[mates.Count][]; var Jb = new double[mates.Count][];
        for (int k = 0; k < mates.Count; k++)
        {
            int nr = rowOf[k + 1] - rowOf[k];
            Ja[k] = new double[nr * 6]; Jb[k] = new double[nr * 6];
        }
        for (; it < maxIter && n > 0 && rows > 0 && r0.Max(Math.Abs) > tol; it++)
        {
            for (int i = 0; i < nf; i++)
                for (int j = 0; j < 6; j++)
                {
                    // central differences: exact to h squared (deep chains of components, far from the origin, need it)
                    Transform Step(double d) => Moved(i, j == 0 ? d : 0, j == 1 ? d : 0, j == 2 ? d : 0, j == 3 ? d : 0, j == 4 ? d : 0, j == 5 ? d : 0);
                    var tp = Step(h); var tm = Step(-h);
                    foreach (var k in matesOf[i])
                    {
                        var m = mates[k];
                        int nr = rowOf[k + 1] - rowOf[k];
                        var J = ca[k] == i ? Ja[k] : Jb[k];
                        MateRes(m, ca[k] == i ? tp : PlacementOf(m.A.Component, null), cb[k] == i ? tp : PlacementOf(m.B.Component, null));
                        for (int q = 0; q < nr; q++) J[q * 6 + j] = q < buf.Count ? buf[q] : 0;
                        MateRes(m, ca[k] == i ? tm : PlacementOf(m.A.Component, null), cb[k] == i ? tm : PlacementOf(m.B.Component, null));
                        for (int q = 0; q < nr; q++) J[q * 6 + j] = (J[q * 6 + j] - (q < buf.Count ? buf[q] : 0)) / (2 * h);
                    }
                }
            // the normal equations JtJ dx = -Jt r, a 6x6 block at a time
            sky.Clear();
            var g = new double[n];
            for (int k = 0; k < mates.Count; k++)
            {
                int nr = rowOf[k + 1] - rowOf[k], r = rowOf[k];
                // (a mate between two references of one component: its rows count once, against that component)
                int a = ca[k], b = cb[k] == ca[k] ? -1 : cb[k];
                for (int p = 0; p < 6; p++)
                {
                    for (int q = 0; q < nr; q++)
                    {
                        if (a >= 0) g[6 * a + p] -= Ja[k][q * 6 + p] * r0[r + q];
                        if (b >= 0) g[6 * b + p] -= Jb[k][q * 6 + p] * r0[r + q];
                    }
                    for (int s = 0; s < 6; s++)
                    {
                        double aa = 0, bb = 0, ab = 0;
                        for (int q = 0; q < nr; q++)
                        {
                            double ja = Ja[k][q * 6 + p], jb = Jb[k][q * 6 + p];
                            aa += ja * Ja[k][q * 6 + s]; bb += jb * Jb[k][q * 6 + s]; ab += ja * Jb[k][q * 6 + s];
                        }
                        if (a >= 0) sky.Add(6 * a + p, 6 * a + s, aa);
                        if (b >= 0) sky.Add(6 * b + p, 6 * b + s, bb);
                        if (a >= 0 && b >= 0) { sky.Add(6 * a + p, 6 * b + s, ab); sky.Add(6 * b + s, 6 * a + p, ab); }
                    }
                }
            }
            double f0 = Sq(r0);
            bool stepped = false;
            for (int tries = 0; tries < 12 && !stepped; tries++)
            {
                var dx = sky.SolveDamped(lambda, g);
                if (dx == null) { lambda *= 10; continue; }
                var trial = Trial(dx);
                var r1 = Res(trial);
                if (Sq(r1) < f0)
                {
                    for (int i = 0; i < nf; i++) placements[free[i].Id] = trial[i];
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
            if (Live(m.A) && Live(m.B) && Mate.Supports(m.Type, L(m.A), L(m.B)))
            {
                var (ga, gb) = (World(m.A, null), World(m.B, null));
                var (ta, tb) = (PlacementOf(m.A.Component, null), PlacementOf(m.B.Component, null));
                m.Residuals(ga, gb, ta, tb, r);
                // the turns as they are now: the next solve counts on from here
                m.Settle(ga, gb, ta, tb);
            }
            else r.Add(double.PositiveInfinity);
            res.Errors[m.Id] = r.Count == 0 ? 0 : r.Max(Math.Abs);
        }
        res.Satisfied = res.Errors.Values.All(e => e <= 1e-6);
        return res;
    }

    static bool Differs(Transform a, Transform b) =>
        Vec3.Distance(a.T, b.T) > 1e-12 || Math.Abs(a.M00 - b.M00) + Math.Abs(a.M01 - b.M01) + Math.Abs(a.M02 - b.M02) + Math.Abs(a.M10 - b.M10)
            + Math.Abs(a.M11 - b.M11) + Math.Abs(a.M12 - b.M12) + Math.Abs(a.M20 - b.M20) + Math.Abs(a.M21 - b.M21) + Math.Abs(a.M22 - b.M22) > 1e-12;

    /// <summary>
    /// A symmetric matrix of 6x6 blocks (one per free component), kept within its envelope: the components put in
    /// reverse Cuthill-McKee order (those mated together close), each row stored from its first non-zero column.
    /// </summary>
    sealed class Skyline
    {
        readonly int _n;
        readonly int[] _pos;      // component -> its place in the order
        readonly int[] _first;    // row (in order) -> its first stored column
        readonly double[][] _row; // row -> columns first..row (the lower triangle)

        Skyline(int components, int[] pos, int[] firstComp)
        {
            _n = components * 6; _pos = pos;
            _first = new int[_n]; _row = new double[_n][];
            for (int i = 0; i < _n; i++)
            {
                _first[i] = 6 * firstComp[i / 6];
                _row[i] = new double[i - _first[i] + 1];
            }
        }

        public static Skyline For(int components, IEnumerable<(int a, int b)> pairs)
        {
            var adj = new HashSet<int>[components];
            for (int i = 0; i < components; i++) adj[i] = new HashSet<int>();
            foreach (var (a, b) in pairs) { adj[a].Add(b); adj[b].Add(a); }
            // reverse Cuthill-McKee: breadth first from a least connected component, each piece in turn
            var order = new List<int>(components);
            var seen = new bool[components];
            foreach (var start in Enumerable.Range(0, components).OrderBy(i => adj[i].Count))
            {
                if (seen[start]) continue;
                seen[start] = true;
                var q = new Queue<int>(); q.Enqueue(start);
                while (q.Count > 0)
                {
                    var c = q.Dequeue(); order.Add(c);
                    foreach (var nb in adj[c].Where(x => !seen[x]).OrderBy(x => adj[x].Count)) { seen[nb] = true; q.Enqueue(nb); }
                }
            }
            order.Reverse();
            var pos = new int[components];
            for (int i = 0; i < components; i++) pos[order[i]] = i;
            // each component's first column: the earliest of itself and those mated to it
            var firstComp = new int[components];
            for (int i = 0; i < components; i++)
            {
                var c = order[i];
                firstComp[i] = adj[c].Select(x => pos[x]).Append(i).Min();
            }
            return new Skyline(components, pos, firstComp);
        }

        public void Clear() { foreach (var r in _row) Array.Clear(r); }

        int Index(int dof) => 6 * _pos[dof / 6] + dof % 6;

        /// <summary>Adds to the entry at (r, c) of the matrix (component-numbered dofs); the upper triangle is ignored.</summary>
        public void Add(int r, int c, double v)
        {
            int i = Index(r), j = Index(c);
            if (j > i) return;
            _row[i][j - _first[i]] += v;
        }

        /// <summary>
        /// Solves (A + lambda (1 + diag A)) x = g by Cholesky within the envelope (component-numbered dofs); null when
        /// it is not positive definite.
        /// </summary>
        public double[]? SolveDamped(double lambda, double[] g)
        {
            var L = new double[_n][];
            for (int i = 0; i < _n; i++)
            {
                var a = _row[i]; var l = L[i] = new double[a.Length];
                int fi = _first[i];
                for (int j = fi; j <= i; j++)
                {
                    double s = a[j - fi];
                    if (j == i) s += lambda * (1 + a[j - fi]);
                    int fj = _first[j], from = Math.Max(fi, fj);
                    var lj = L[j];
                    for (int k = from; k < j; k++) s -= l[k - fi] * lj[k - fj];
                    if (j < i) l[j - fi] = s / lj[j - fj];
                    else
                    {
                        if (!(s > 1e-300)) return null;
                        l[j - fi] = Math.Sqrt(s);
                    }
                }
            }
            // L y = g, then Lt x = y (in order), then back to the components' numbering
            var y = new double[_n];
            for (int dof = 0; dof < _n; dof++) y[Index(dof)] = g[dof];
            for (int i = 0; i < _n; i++)
            {
                double s = y[i]; int fi = _first[i];
                for (int k = fi; k < i; k++) s -= L[i][k - fi] * y[k];
                y[i] = s / L[i][i - fi];
            }
            for (int i = _n - 1; i >= 0; i--)
            {
                y[i] /= L[i][i - _first[i]];
                int fi = _first[i];
                for (int k = fi; k < i; k++) y[k] -= L[i][k - fi] * y[i];
            }
            var x = new double[_n];
            for (int dof = 0; dof < _n; dof++) x[dof] = y[Index(dof)];
            return x;
        }
    }
}

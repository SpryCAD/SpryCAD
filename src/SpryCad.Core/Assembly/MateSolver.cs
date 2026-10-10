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
        /// <summary>The components solved for (floating ones that take part in a mate), moved or not.</summary>
        public HashSet<string> Solved { get; } = new();
        public bool Satisfied { get; set; }
        public int Iterations { get; set; }
        public IEnumerable<string> Failed(double tol = 1e-6) => Errors.Where(e => e.Value > tol).Select(e => e.Key);
    }

    /// <summary>
    /// A component being dragged: its point <c>Local</c> (in its part) pulled toward the line from <c>Origin</c> along
    /// <c>Dir</c> (the ray under the cursor). <c>Move</c>: how far the cursor moved it (in the view's plane), for a
    /// component no mate moves (<see cref="AssemblyModel.SolveDrag"/>); the solve itself starts from where it is.
    /// </summary>
    public sealed record Pull(string Component, Vec3 Local, Vec3 Origin, Vec3 Dir, Vec3 Move = default);

    /// <summary>
    /// A component carried by another (<c>Body</c>): placed at the body's placement times <c>Offset</c>, never solved
    /// for itself (the components of a rigid sub-assembly, the fixed ones of a flexible one). Mates on it move its body.
    /// </summary>
    public readonly record struct Attachment(string Body, Transform Offset);

    /// <summary>
    /// A drag: the mates solved with the grabbed point pulled toward the cursor's ray (gently, so the mates win:
    /// a component turns or slides only as they let it, and what it is mated to follows), then solved exactly. A gear
    /// on its axis turns so the point grabbed follows the cursor round, its mate turning the other. Only what is mated
    /// to the dragged component, through floating components, moves: the rest has nothing pulling it.
    /// </summary>
    public static Result SolveDrag(AssemblyDocument doc, Func<MateRef, MateGeom> local, Pull pull, IReadOnlyDictionary<string, Attachment>? attached = null)
        => PrepareDrag(doc, local, pull.Component, attached).Run(pull);

    /// <summary>
    /// A drag's solve set up once, for every mouse move of it (<see cref="Session.Run"/> with the pull): the dragged
    /// component and what is mated to it through floating components.
    /// </summary>
    public static Session PrepareDrag(AssemblyDocument doc, Func<MateRef, MateGeom> local, string component, IReadOnlyDictionary<string, Attachment>? attached = null)
        => Prepare(doc, local, Linked(doc, component, attached), attached, component);

    /// <summary>
    /// A solve set up (the mates and references looked up, the components to move, the rows, the order of the normal
    /// equations), to be run any number of times while nothing but the solves themselves moves the components.
    /// </summary>
    public sealed class Session
    {
        readonly Func<Pull?, double, int, Result> _run;
        internal Session(Func<Pull?, double, int, Result> run) => _run = run;
        /// <summary>
        /// Solves from where the components are: with a pull (on the component it was set up for), pulled then exactly;
        /// to <paramref name="tol"/> (the largest residual left, mm or radians).
        /// </summary>
        public Result Run(Pull? pull = null, double tol = 1e-10, int maxIter = 80) => _run(pull, tol, maxIter);
    }

    /// <summary>
    /// The floating components mated to <paramref name="start"/>, directly or through other floating ones (a fixed
    /// component holds what is mated to it apart from the rest), <paramref name="start"/> among them.
    /// </summary>
    static HashSet<string> Linked(AssemblyDocument doc, string start, IReadOnlyDictionary<string, Attachment>? attached)
    {
        var byId = new Dictionary<string, Component>();
        foreach (var c in doc.Components) byId.TryAdd(c.Id, c);
        string? Body(string? id) => id != null && attached != null && attached.TryGetValue(id, out var at) ? at.Body : id;
        bool Floats(string? id) => id != null && byId.TryGetValue(id, out var c) && !c.Fixed && !c.Suppressed;
        var adj = new Dictionary<string, List<string>>();
        foreach (var m in doc.Mates)
        {
            if (m.Suppressed) continue;
            var (a, b) = (Body(m.A.Component), Body(m.B.Component));
            if (!Floats(a) || !Floats(b) || a == b) continue;
            (adj.TryGetValue(a!, out var la) ? la : adj[a!] = new()).Add(b!);
            (adj.TryGetValue(b!, out var lb) ? lb : adj[b!] = new()).Add(a!);
        }
        var seen = new HashSet<string> { start };
        var q = new Queue<string>(); q.Enqueue(start);
        while (q.Count > 0)
            if (adj.TryGetValue(q.Dequeue(), out var nb))
                foreach (var x in nb) if (seen.Add(x)) q.Enqueue(x);
        return seen;
    }

    /// <summary>
    /// Solves the assembly's unsuppressed mates, moving its floating components (only <paramref name="onlyMove"/>
    /// when given: a new mate moves its first component). <paramref name="local"/>: a reference's geometry in its
    /// part's coordinates. <paramref name="attached"/>: components carried by others (<see cref="Attachment"/>), their
    /// placements set from their bodies' after the solve.
    /// </summary>
    public static Result Solve(AssemblyDocument doc, Func<MateRef, MateGeom> local, ISet<string>? onlyMove = null, int maxIter = 80,
        IReadOnlyDictionary<string, Attachment>? attached = null)
        => Prepare(doc, local, onlyMove, attached, null).Run(null, 1e-10, maxIter);

    /// <summary>
    /// The solve set up; <paramref name="pullComponent"/>: the component a drag pulls (a row for the pull). Each run, with
    /// a pull, is in two stages: the mates with the pull (until it stops getting smaller), then the mates alone.
    /// </summary>
    static Session Prepare(AssemblyDocument doc, Func<MateRef, MateGeom> local, ISet<string>? onlyMove,
        IReadOnlyDictionary<string, Attachment>? attached, string? pullComponent)
    {
        Pull? pull = null;
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
        // a reference on a carried component moves its body
        string? BodyOf(string? id) => id != null && attached != null && attached.TryGetValue(id, out var at) ? at.Body : id;
        // components locked together (a Lock that holds as they are) move as one, carried by one of them (a still one
        // if any is: the group is still), the Lock between them left out: fewer to solve for
        var groupOf = new Dictionary<string, string>();
        string Root(string b) { while (groupOf.TryGetValue(b, out var up) && up != b) b = up; return b; }
        bool Moves(string b) => byId[b] is { Fixed: false } c && (onlyMove == null || onlyMove.Contains(c.Id));
        Transform AsPlaced(string? id) =>
            id == null ? Transform.Identity
            : attached != null && attached.TryGetValue(id, out var at) ? byId[at.Body].Placement.ToTransform() * at.Offset
            : byId[id].Placement.ToTransform();
        var locked = new HashSet<Mate>();
        foreach (var m in mates)
        {
            if (m.Type != MateType.Lock || BodyOf(m.A.Component) is not { } ba || BodyOf(m.B.Component) is not { } bb || ba == bb) continue;
            var (ta, tb) = (AsPlaced(m.A.Component), AsPlaced(m.B.Component));
            var lr = new List<double>();
            m.Residuals(L(m.A).Placed(ta), L(m.B).Placed(tb), ta, tb, lr);
            if (lr.Max(Math.Abs) > 1e-9) continue;
            locked.Add(m);
            var (ra, rb) = (Root(ba), Root(bb));
            if (ra == rb) continue;
            if (!Moves(rb) && Moves(ra)) (ra, rb) = (rb, ra);
            groupOf[rb] = ra;
        }
        if (groupOf.Count > 0)
        {
            var grouped = attached != null ? new Dictionary<string, Attachment>(attached) : new Dictionary<string, Attachment>();
            Transform Body(string b) => byId[b].Placement.ToTransform();
            foreach (var b in groupOf.Keys) grouped[b] = new Attachment(Root(b), Body(Root(b)).RigidInverse() * Body(b));
            if (attached != null)
                foreach (var (id, at) in attached)
                    if (Root(at.Body) is var root && root != at.Body) grouped[id] = new Attachment(root, Body(root).RigidInverse() * Body(at.Body) * at.Offset);
            attached = grouped;
            mates.RemoveAll(locked.Contains);
        }
        // a pull on a component locked to another pulls the group's
        var pullOff = Transform.Identity;
        var pulled = pullComponent;
        if (pullComponent != null && groupOf.ContainsKey(pullComponent)) { pullOff = attached![pullComponent].Offset; pullComponent = attached[pullComponent].Body; }
        foreach (var m in mates)
            foreach (var r in new[] { m.A, m.B })
                if (BodyOf(r.Component) is { } b && byId[b] is { Fixed: false } c && (onlyMove == null || onlyMove.Contains(c.Id)) && !freeIdx.ContainsKey(c.Id))
                {
                    freeIdx[c.Id] = free.Count;
                    free.Add(c);
                }
        // a pull on a component no mate moves: nothing to solve (the drag moved it)
        if (pullComponent != null && !freeIdx.ContainsKey(pullComponent)) pullComponent = null;
        var placements = doc.Components.ToDictionary(c => c.Id, c => c.Placement.ToTransform());
        Transform BodyPlacement(string id, Transform[]? trial) => trial != null && freeIdx.TryGetValue(id, out var i) ? trial[i] : placements[id];
        Transform PlacementOf(string? id, Transform[]? trial) =>
            id == null ? Transform.Identity
            : attached != null && attached.TryGetValue(id, out var at) ? BodyPlacement(at.Body, trial) * at.Offset
            : BodyPlacement(id, trial);
        MateGeom World(MateRef r, Transform[]? trial) => L(r).Placed(PlacementOf(r.Component, trial));
        int FreeOf(MateRef r) => BodyOf(r.Component) is { } b && freeIdx.TryGetValue(b, out var i) ? i : -1;

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
        bool pulling = false;
        // the free components' placements (by index), as the solve has them
        var place = free.Select(c => placements[c.Id]).ToArray();
        // a component moved by six numbers, a turn w and a move m about its pivot. Pulled, as a screw motion (the
        // exponential map): a step along a turn about any line, through the pivot or not, follows that turn exactly (a
        // turn about the pivot and then a move would leave a crank turned on its journal off it by the turn squared
        // times the pivot's distance from the journal, for the mates to pull back each step). Not otherwise: there the
        // steps are small corrections, and free turns (a gear spinning on its axis) left costing nothing let them
        // wander (a stack of gears knocked out of place was not solved back in 80 steps).
        Transform Moved(int i, double w0, double w1, double w2, double m0, double m1, double m2)
        {
            var w = new Vec3(w0, w1, w2); var m = new Vec3(m0, m1, m2);
            double th = w.Length;
            if (th <= 1e-15) return Transform.Translation(m) * place[i];
            var k = w / th;
            var km = Vec3.Cross(k, m);
            if (pulling) m += km * ((1 - Math.Cos(th)) / th) + Vec3.Cross(k, km) * ((th - Math.Sin(th)) / th);
            return Transform.Translation(m) * Transform.Rotation(pivots[i], k, th) * place[i];
        }
        Transform[] Trial(double[] dx)
        {
            var t = new Transform[nf];
            for (int i = 0; i < nf; i++) t[i] = Moved(i, dx[6 * i], dx[6 * i + 1], dx[6 * i + 2], dx[6 * i + 3], dx[6 * i + 4], dx[6 * i + 5]);
            return t;
        }

        // the mates' rows: each mate's residuals at a fixed place in the vector, and which free components it moves with;
        // a pull's rows after them (its component's grabbed point off the cursor's ray, weighted well below the mates)
        int K = mates.Count + (pullComponent != null ? 1 : 0);
        string? CompA(int k) => k < mates.Count ? mates[k].A.Component : pullComponent;
        string? CompB(int k) => k < mates.Count ? mates[k].B.Component : null;
        var pullDir = Vec3.Zero;
        // (well below: where the mates do not let the point reach the cursor, they give by the weight squared times the
        // distance, and strained over mates that hold the same thing twice, a crank's tilt by its journal and its gear's
        // face, they come back only slowly)
        double pullWeight = 1e-3;
        // each mate's references' geometry, and where each side is: on a free component (ca, cb: its index, offA, offB:
        // where the reference's component sits on it) or still (fixA, fixB: its placement)
        var geoA = new MateGeom[K]; var geoB = new MateGeom[K];
        var offA = new Transform[K]; var offB = new Transform[K]; var fixA = new Transform[K]; var fixB = new Transform[K];
        for (int k = 0; k < mates.Count; k++) { geoA[k] = L(mates[k].A); geoB[k] = L(mates[k].B); }
        var buf = new List<double>();
        void MateRes(int k, in Transform ta, in Transform tb)
        {
            buf.Clear();
            if (k < mates.Count)
            {
                mates[k].Residuals(geoA[k].Placed(ta), geoB[k].Placed(tb), ta, tb, buf);
                return;
            }
            if (pull == null) { buf.Add(0); buf.Add(0); buf.Add(0); return; }
            var v = ta.Point(pull.Local) - pull.Origin;
            v -= pullDir * Vec3.Dot(v, pullDir);
            buf.Add(pullWeight * v.X); buf.Add(pullWeight * v.Y); buf.Add(pullWeight * v.Z);
        }
        var rowOf = new int[K + 1];
        var ca = new int[K]; var cb = new int[K];
        Transform Offset(string? id) => id != null && attached != null && attached.TryGetValue(id, out var at) ? at.Offset : Transform.Identity;
        for (int k = 0; k < K; k++)
        {
            ca[k] = k < mates.Count ? FreeOf(mates[k].A) : freeIdx[pullComponent!];
            cb[k] = k < mates.Count ? FreeOf(mates[k].B) : -1;
            offA[k] = Offset(CompA(k)); offB[k] = Offset(CompB(k));
            fixA[k] = PlacementOf(CompA(k), null); fixB[k] = PlacementOf(CompB(k), null);
            MateRes(k, fixA[k], fixB[k]);
            rowOf[k + 1] = rowOf[k] + buf.Count;
        }
        Transform SideA(int k, Transform[]? trial) => ca[k] < 0 ? fixA[k] : (trial ?? place)[ca[k]] * offA[k];
        Transform SideB(int k, Transform[]? trial) => cb[k] < 0 ? fixB[k] : (trial ?? place)[cb[k]] * offB[k];
        int rows = rowOf[K];
        double[] Res(Transform[]? trial)
        {
            var r = new double[rows];
            for (int k = 0; k < K; k++)
            {
                MateRes(k, SideA(k, trial), SideB(k, trial));
                for (int q = 0; q < buf.Count && rowOf[k] + q < rowOf[k + 1]; q++) r[rowOf[k] + q] = buf[q];
            }
            return r;
        }
        // the mates each free component's differences are taken for: a mate between two free components moves the same
        // when both move alike (it holds the references relative to each other), so its rows against B's six numbers
        // follow from those against A's, and only A is differenced; a gear counts its turns from directions fixed in the
        // assembly, so both are
        var matesOf = new List<int>[nf];
        for (int i = 0; i < nf; i++) matesOf[i] = new List<int>();
        var derived = new bool[K];
        for (int k = 0; k < K; k++)
        {
            derived[k] = k < mates.Count && ca[k] >= 0 && cb[k] >= 0 && ca[k] != cb[k] && mates[k].Type != MateType.Gear;
            if (ca[k] >= 0) matesOf[ca[k]].Add(k);
            if (cb[k] >= 0 && cb[k] != ca[k] && !derived[k]) matesOf[cb[k]].Add(k);
        }
        var sky = Skyline.For(nf, Enumerable.Range(0, K).Where(k => ca[k] >= 0 && cb[k] >= 0 && ca[k] != cb[k]).Select(k => (ca[k], cb[k])));
        static double Sq(double[] v) { double s = 0; foreach (var a in v) s += a * a; return s; }

        const double h = 1e-6, maxTurn = 0.2;
        var r0 = Array.Empty<double>();
        // the damping: light where the steps are small and the mates nearly linear (a drag's pull from where they
        // hold, a solve starting nearly satisfied), lightened fast as steps succeed
        double Damping() => pulling ? 1e-6 : r0.Length > 0 && r0.Max(Math.Abs) < 1e-6 ? 1e-9 : 1e-3;
        double lambda = 0;
        // the Jacobian: for each mate, its rows against each of its (at most two) free components' six numbers
        var Ja = new double[K][]; var Jb = new double[K][];
        for (int k = 0; k < K; k++)
        {
            int nr = rowOf[k + 1] - rowOf[k];
            Ja[k] = new double[nr * 6]; Jb[k] = new double[nr * 6];
        }
        var curA = new Transform[K]; var curB = new Transform[K];
        var fd = new bool[K];
        // a mate's (or the pull's) rows against its components' six numbers exactly, into Ja / Jb; false where the mate
        // does not work them out (they are differenced)
        bool Exact(int k)
        {
            int nr = rowOf[k + 1] - rowOf[k];
            static Mate.Twist Unit(int j, Vec3 pivot) =>
                j < 3 ? new(j == 0 ? Vec3.UnitX : j == 1 ? Vec3.UnitY : Vec3.UnitZ, Vec3.Zero, pivot)
                      : new(Vec3.Zero, j == 3 ? Vec3.UnitX : j == 4 ? Vec3.UnitY : Vec3.UnitZ, pivot);
            if (k == mates.Count)
            {
                // the grabbed point's offset from the ray: its motion less the part along the ray, weighted (none
                // without a pull: the row is there for the drag's other moves)
                if (pull == null) { Array.Clear(Ja[k]); return true; }
                var x = curA[k].Point(pull.Local);
                for (int j = 0; j < 6; j++)
                {
                    var dx = Unit(j, pivots[ca[k]]).Point(x);
                    var dv = (dx - pullDir * Vec3.Dot(dx, pullDir)) * pullWeight;
                    Ja[k][j] = dv.X; Ja[k][6 + j] = dv.Y; Ja[k][12 + j] = dv.Z;
                }
                return true;
            }
            var m = mates[k];
            var ga = geoA[k].Placed(curA[k]); var gb = geoB[k].Placed(curB[k]);
            var still = new Mate.Twist(Vec3.Zero, Vec3.Zero, Vec3.Zero);
            // (a mate between two references of one component: its rows against that component, both sides moving)
            bool one = ca[k] == cb[k];
            for (int side = 0; side < 2; side++)
            {
                int c = side == 0 ? ca[k] : cb[k];
                if (c < 0 || side == 1 && one) continue;
                var J = side == 0 ? Ja[k] : Jb[k];
                for (int j = 0; j < 6; j++)
                {
                    var u = Unit(j, pivots[c]);
                    buf.Clear();
                    if (!m.Directional(ga, gb, curA[k], curB[k], side == 0 || one ? u : still, side == 1 || one ? u : still, buf) || buf.Count != nr)
                        return false;
                    for (int q = 0; q < nr; q++) J[q * 6 + j] = buf[q];
                }
            }
            return true;
        }
        var blkAA = new double[36]; var blkBB = new double[36]; var blkAB = new double[36]; var blkBA = new double[36];
        // JtJ's 6x6 block of one side's rows (into blk) and its share of -Jt r (into g at component c)
        void Gram(double[] J, double[] J2, int nr, int r, int c, double[] g, double[] blk)
        {
            Array.Clear(blk);
            for (int q = 0; q < nr; q++)
            {
                double rq = r0[r + q];
                for (int p = 0; p < 6; p++)
                {
                    double jp = J[q * 6 + p];
                    if (jp == 0) continue;
                    g[6 * c + p] -= jp * rq;
                    for (int t = 0; t < 6; t++) blk[p * 6 + t] += jp * J2[q * 6 + t];
                }
            }
        }
        bool ran = false;
        Result Run(Pull? drag, double tol, int maxIter)
        {
            if (drag != null && drag.Component != pulled) throw new ArgumentException("The pull is on a component the solve was not set up for.");
            // (none where nothing it pulls is solved for: a component locked to a still one)
            pull = drag == null || pullComponent == null ? null : pulled == pullComponent ? drag : drag with { Component = pullComponent, Local = pullOff.Point(drag.Local) };
            pulling = pull != null; pullWeight = 1e-3; pullDir = pull?.Dir.Normalized ?? Vec3.Zero;
            var res = new Result();
            foreach (var c in free) res.Solved.Add(c.Id);
            foreach (var b in groupOf.Keys) if (freeIdx.ContainsKey(Root(b))) res.Solved.Add(b);
            // run again: from where the components are now (as the last run left them, or put back since)
            if (ran) for (int i = 0; i < nf; i++) placements[free[i].Id] = place[i] = free[i].Placement.ToTransform();
            ran = true;
            r0 = Res(null);
            lambda = Damping();
            int it = 0, end = pulling ? 2 * maxIter : maxIter;
            for (int stage = 0; stage < end && n > 0 && rows > 0; stage++, it++)
            {
                // pulled as far as it goes: the pull let go, the mates solved exactly from there
                if (pulling && (stage == maxIter || r0.Max(Math.Abs) <= tol))
                {
                    pulling = false; pullWeight = 0; stage = maxIter;
                    r0 = Res(null); lambda = Damping();
                }
                if (!pulling && r0.Max(Math.Abs) <= tol) break;
                for (int k = 0; k < K; k++) { curA[k] = SideA(k, null); curB[k] = SideB(k, null); }
                // the Jacobian exactly where the mate works its derivatives out (and the pull's), by central differences
                // for the rest
                for (int k = 0; k < K; k++) fd[k] = !Exact(k);
                for (int i = 0; i < nf; i++)
                {
                    bool any = false;
                    foreach (var k in matesOf[i]) any |= fd[k];
                    if (!any) continue;
                    for (int j = 0; j < 6; j++)
                    {
                        // central differences: exact to h squared (deep chains of components, far from the origin, need it)
                        Transform Step(double d) => Moved(i, j == 0 ? d : 0, j == 1 ? d : 0, j == 2 ? d : 0, j == 3 ? d : 0, j == 4 ? d : 0, j == 5 ? d : 0);
                        var tp = Step(h); var tm = Step(-h);
                        foreach (var k in matesOf[i])
                        {
                            if (!fd[k]) continue;
                            int nr = rowOf[k + 1] - rowOf[k];
                            var J = ca[k] == i ? Ja[k] : Jb[k];
                            MateRes(k, ca[k] == i ? tp * offA[k] : curA[k], cb[k] == i ? tp * offB[k] : curB[k]);
                            for (int q = 0; q < nr; q++) J[q * 6 + j] = q < buf.Count ? buf[q] : 0;
                            MateRes(k, ca[k] == i ? tm * offA[k] : curA[k], cb[k] == i ? tm * offB[k] : curB[k]);
                            for (int q = 0; q < nr; q++) J[q * 6 + j] = (J[q * 6 + j] - (q < buf.Count ? buf[q] : 0)) / (2 * h);
                        }
                    }
                }
                // B's rows from A's: a motion of A by a turn w about A's pivot pa and a move m is the motion of B by w about B's
                // pivot pb and m + w x (pb - pa); both together leave the mate as it is, so
                // Jb.m = -Ja.m and Jb.w_j = -Ja.w_j + Ja.m (e_j x (pb - pa))
                for (int k = 0; k < K; k++)
                {
                    if (!derived[k] || !fd[k]) continue;
                    int nr = rowOf[k + 1] - rowOf[k];
                    var d = pivots[cb[k]] - pivots[ca[k]];
                    var A = Ja[k]; var B = Jb[k];
                    for (int q = 0; q < nr; q++)
                    {
                        int o = q * 6;
                        double m0 = A[o + 3], m1 = A[o + 4], m2 = A[o + 5];
                        // e_0 x d = (0, -d.z, d.y), e_1 x d = (d.z, 0, -d.x), e_2 x d = (-d.y, d.x, 0)
                        B[o] = -A[o] + (-m1 * d.Z + m2 * d.Y);
                        B[o + 1] = -A[o + 1] + (m0 * d.Z - m2 * d.X);
                        B[o + 2] = -A[o + 2] + (-m0 * d.Y + m1 * d.X);
                        B[o + 3] = -m0; B[o + 4] = -m1; B[o + 5] = -m2;
                    }
                }
                // the normal equations JtJ dx = -Jt r, a 6x6 block at a time
                sky.Clear();
                var g = new double[n];
                for (int k = 0; k < K; k++)
                {
                    int nr = rowOf[k + 1] - rowOf[k], r = rowOf[k];
                    // (a mate between two references of one component: its rows count once, against that component)
                    int a = ca[k], b = cb[k] == ca[k] ? -1 : cb[k];
                    var A = Ja[k]; var B = Jb[k];
                    if (a >= 0) Gram(A, A, nr, r, a, g, blkAA);
                    if (b >= 0) Gram(B, B, nr, r, b, g, blkBB);
                    if (a >= 0) sky.AddBlock(a, a, blkAA);
                    if (b >= 0) sky.AddBlock(b, b, blkBB);
                    if (a >= 0 && b >= 0)
                    {
                        // the block across: AtB at (a, b), its transpose at (b, a) (the one below the diagonal is kept)
                        Array.Clear(blkAB);
                        for (int q = 0; q < nr; q++)
                            for (int p = 0; p < 6; p++)
                            {
                                double ja = A[q * 6 + p];
                                if (ja == 0) continue;
                                for (int t = 0; t < 6; t++) blkAB[p * 6 + t] += ja * B[q * 6 + t];
                            }
                        sky.AddBlock(a, b, blkAB);
                        for (int p = 0; p < 6; p++) for (int t = 0; t < 6; t++) blkBA[t * 6 + p] = blkAB[p * 6 + t];
                        sky.AddBlock(b, a, blkBA);
                    }
                }
                double f0 = Sq(r0);
                bool stepped = false;
                for (int tries = 0; tries < 12 && !stepped; tries++)
                {
                    var dx = sky.SolveDamped(lambda, g);
                    if (dx == null) { lambda *= 10; continue; }
                    // no component turned more than maxTurn in one step (the whole step shortened alike): the mates are
                    // linearised about here, and a long turn taken on them at once can land what hangs on it on the
                    // other way of satisfying them (a piston on the far side of its crank)
                    double turn = 0;
                    for (int i = 0; i < nf; i++) turn = Math.Max(turn, Math.Sqrt(dx[6 * i] * dx[6 * i] + dx[6 * i + 1] * dx[6 * i + 1] + dx[6 * i + 2] * dx[6 * i + 2]));
                    if (turn > maxTurn) for (int j = 0; j < n; j++) dx[j] *= maxTurn / turn;
                    var trial = Trial(dx);
                    var r1 = Res(trial);
                    // too far: half the way first (one more residual), before damping and factoring again
                    if (!(Sq(r1) < f0))
                    {
                        for (int j = 0; j < n; j++) dx[j] *= 0.5;
                        trial = Trial(dx); r1 = Res(trial);
                    }
                    if (Sq(r1) < f0)
                    {
                        for (int i = 0; i < nf; i++) placements[free[i].Id] = place[i] = trial[i];
                        // pulled, the residual stays (the cursor is where the mates do not let the point go): done when
                        // it stops getting smaller (by a millionth: the exact solve follows)
                        if (pulling && f0 - Sq(r1) < 1e-6 * f0) { r0 = r1; stage = maxIter - 1; break; }
                        r0 = r1;
                        lambda = Math.Max(lambda / 10, 1e-12);
                        stepped = true;
                    }
                    else lambda *= 6;
                }
                if (!stepped && !pulling) break;
                if (!stepped) stage = maxIter - 1;
            }
            res.Iterations = it;

            // the components moved, and what each mate is left with
            foreach (var c in free)
            {
                var before = c.Placement.ToTransform(); var after = placements[c.Id];
                if (Differs(before, after)) { c.Placement = Placement.From(after); res.Moved.Add(c.Id); }
            }
            // carried components go with their bodies
            if (attached != null)
                foreach (var (id, at) in attached)
                    if (res.Moved.Contains(at.Body) && byId.TryGetValue(id, out var c))
                    {
                        c.Placement = Placement.From(placements[at.Body] * at.Offset);
                        res.Moved.Add(id);
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
        return new Session(Run);
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
        readonly double[][] _l;   // its factor, the same shape (made again for each damping tried)

        Skyline(int components, int[] pos, int[] firstComp)
        {
            _n = components * 6; _pos = pos;
            _first = new int[_n]; _row = new double[_n][]; _l = new double[_n][];
            for (int i = 0; i < _n; i++)
            {
                _first[i] = 6 * firstComp[i / 6];
                _row[i] = new double[i - _first[i] + 1];
                _l[i] = new double[i - _first[i] + 1];
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
        /// Adds a 6x6 block (row-major) at components <paramref name="a"/> (rows) and <paramref name="b"/> (columns);
        /// as <see cref="Add"/>, what falls above the diagonal is ignored (the caller adds a block across both ways).
        /// </summary>
        public void AddBlock(int a, int b, double[] blk)
        {
            int ri = 6 * _pos[a], ci = 6 * _pos[b];
            if (ci > ri) return;
            for (int p = 0; p < 6; p++)
            {
                var row = _row[ri + p]; int f = _first[ri + p];
                for (int q = 0; q < 6 && (ci + q) <= ri + p; q++) row[ci + q - f] += blk[p * 6 + q];
            }
        }

        /// <summary>
        /// Solves (A + lambda (1 + diag A)) x = g by Cholesky within the envelope (component-numbered dofs); null when
        /// it is not positive definite.
        /// </summary>
        public double[]? SolveDamped(double lambda, double[] g)
        {
            var L = _l;
            for (int i = 0; i < _n; i++)
            {
                var a = _row[i]; var l = L[i];
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

using System.Diagnostics;
using System.Reflection;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// <c>-- blendstress [filter]</c>: fillets and chamfers on many parts (the stress shapes, the test models and demo
/// parts) and many selections of their edges (each face's, each corner's, single edges with their tangent chains,
/// pairs meeting at a corner, all of them), small, medium and large, each scored:
/// <list type="bullet">
/// <item>right: built, valid, its display mesh closed, no tool's end left standing (a wing), the volume moved the
/// right way (down where the edges are all convex, up where all concave), written as STEP with each fillet's edges on
/// its written surface;</item>
/// <item>refused: the feature failed, saying why (a size that does not fit: what a CAD system says too);</item>
/// <item>wrong: built but one of the above not so (the worst: a body that looks done and is not).</item>
/// </list>
/// The counts by kind, selection and shape, and how many tools were applied directly and by boolean. FC_STRESS_ALL:
/// every case printed, not only the ones not right; FC_STRESS_CSV=path: every case to a CSV file.
/// </summary>
static class BlendStress
{
    sealed record Case(string Shape, string Selection, string Kind, string Size, string Outcome, string Detail, double Ms, int Direct, int Boolean);

    public static int Run(string? filter)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        var shapes = new List<(string name, Func<CadDocument> load, bool small)>();
        foreach (var (n, b) in Demos.StressShapes) shapes.Add((n, b, true));
        foreach (var f in Directory.GetFiles(Path.Combine(dir!, "tests", "SpryCad.Tests", "models"), "*.sprt", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var path = f;
            shapes.Add((Path.GetFileNameWithoutExtension(f), () => CadDocument.Load(path), false));
        }
        bool all = Environment.GetEnvironmentVariable("FC_STRESS_ALL") != null;
        // (why the direct blend left tools to the boolean, the face names taken out)
        var why = new Dictionary<string, int>();
        bool printWhy = Environment.GetEnvironmentVariable("FC_STRESS_WHY") != null;
        DirectBlend.Debug = m =>
        {
            if (printWhy) Console.WriteLine("      why: " + m);
            var k = m[(m.IndexOf('(') + 1)..].TrimEnd(')');
            k = System.Text.RegularExpressions.Regex.Replace(k, @"F(?:[0-9a-z]{12}|[0-9])[0-9A-Za-z.]*", "F");
            k = System.Text.RegularExpressions.Regex.Replace(k, @"\(not glued:.*$", "").Trim();
            lock (why) why[k] = why.GetValueOrDefault(k) + 1;
        };
        var cases = new List<Case>();
        var sw = Stopwatch.StartNew();
        foreach (var (shape, load, small) in shapes)
        {
            if (filter != null && !shape.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            CadDocument doc0;
            NBody body;
            double minDim;
            try
            {
                doc0 = load();
                var r0 = new Regenerator().Rebuild(doc0);
                if (r0.State.Bodies.Count != 1 || r0.State.Bodies[0].Solid.Native is not { } nb) { Console.WriteLine($"skip {shape}: not one exact body"); continue; }
                body = nb; body.Link();
                // (sizes against the part's smallest straight edge, of those not tiny beside the part (a sliver's edge
                // would make every size meaningless): small, medium and large blends for it)
                var diag = body.Bounds.Diagonal;
                var lengths = body.Edges.Where(e => e.Curve is NLine).Select(e => e.Length).Where(l => l > 0.03 * diag).ToList();
                minDim = lengths.Count > 0 ? lengths.Min() : body.Bounds.Diagonal / 10;
            }
            catch (Exception ex) { Console.WriteLine($"skip {shape}: {ex.Message}"); continue; }
            foreach (var (selName, edges) in Selections(body, small))
                foreach (var kind in new[] { "fillet", "chamfer" })
                    foreach (var (sizeName, f) in new[] { ("small", 0.08), ("medium", 0.2), ("large", 0.4) })
                    {
                        var c = Score(doc0, body, edges, kind, f * minDim, out var detail, out var ms, out var direct, out var boolean);
                        var row = new Case(shape, selName, kind, sizeName, c, detail, ms, direct, boolean);
                        cases.Add(row);
                        if ((c == "wrong" || Environment.GetEnvironmentVariable("FC_STRESS_SAVEALL") != null) && Environment.GetEnvironmentVariable("FC_STRESS_SAVE") is { } saveDir) Save(saveDir, doc0, edges, kind, f * minDim, $"{shape} {selName} {kind} {sizeName}");
                        if (all || c != "right") Console.WriteLine($"{c,-7} {shape} | {selName} | {kind} {sizeName} ({f * minDim:0.###}) | {detail} [{ms:0} ms]");
                    }
        }
        Console.WriteLine();
        Console.WriteLine($"{cases.Count} cases in {sw.Elapsed.TotalSeconds:0} s: {cases.Count(c => c.Outcome == "right")} right, {cases.Count(c => c.Outcome == "refused")} refused, {cases.Count(c => c.Outcome == "wrong")} wrong");
        Console.WriteLine($"tools {cases.Sum(c => c.Direct)} direct, {cases.Sum(c => c.Boolean)} by boolean; cases using the boolean at all: {cases.Count(c => c.Boolean > 0)} of {cases.Count}");
        void By(string title, Func<Case, string> key)
        {
            Console.WriteLine($"by {title}:");
            foreach (var g in cases.GroupBy(key).OrderBy(g => g.Key))
                Console.WriteLine($"  {g.Key,-40} {g.Count(c => c.Outcome == "right"),4} right {g.Count(c => c.Outcome == "refused"),4} refused {g.Count(c => c.Outcome == "wrong"),4} wrong");
        }
        By("kind", c => c.Kind);
        By("size", c => c.Size);
        By("selection", c => c.Selection.Split(' ')[0]);
        By("shape", c => c.Shape);
        Console.WriteLine("tools left to the boolean, why:");
        foreach (var (k, n) in why.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {n,5}  {k}");
        DirectBlend.Debug = null;
        Console.WriteLine("wrong, by what:");
        foreach (var g in cases.Where(c => c.Outcome == "wrong").GroupBy(c => c.Detail.Split(':')[0]).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Count(),4}  {g.Key}");
        if (Environment.GetEnvironmentVariable("FC_STRESS_CSV") is { } csv)
            File.WriteAllLines(csv, new[] { "shape,selection,kind,size,outcome,detail,ms,direct,boolean" }
                .Concat(cases.Select(c => string.Join(",", new[] { c.Shape, c.Selection, c.Kind, c.Size, c.Outcome, c.Detail, c.Ms.ToString("0"), c.Direct.ToString(), c.Boolean.ToString() }
                    .Select(x => "\"" + x.Replace("\"", "'") + "\"")))));
        return 0;
    }

    /// <summary>
    /// <c>-- blendtime [reps]</c>: every demo part, blend case and test model rebuilt cold reps times (5); each fillet
    /// and chamfer feature's best time, and their sum per part and in all. To compare builds: run each, compare.
    /// </summary>
    public static int Time(int reps, string? filter = null)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        var docs = new List<(string name, Func<CadDocument> load)>();
        foreach (var (file, build) in Demos.All)
            if (file.EndsWith(".sprt", StringComparison.Ordinal)) docs.Add(("demo " + file, build));
        foreach (var (name, build) in Demos.BlendCases) docs.Add(("case " + name, build));
        foreach (var f in Directory.GetFiles(Path.Combine(dir!, "tests", "SpryCad.Tests", "models"), "*.sprt", SearchOption.AllDirectories).OrderBy(x => x))
        {
            var path = f;
            docs.Add((Path.GetFileNameWithoutExtension(f), () => CadDocument.Load(path)));
        }
        double total = 0;
        foreach (var (name, load) in docs)
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var part = load();
            var blends = part.Features.Where(f => f is FilletFeature or ChamferFeature).Select(f => f.Id).ToList();
            if (blends.Count == 0) continue;
            var best = blends.ToDictionary(id => id, _ => double.MaxValue);
            for (int i = 0; i < reps; i++)
            {
                Csg.ClearCache();
                // (FC_TIME_PROF: the last rebuild profiled)
                bool prof = i == reps - 1 && Environment.GetEnvironmentVariable("FC_TIME_PROF") != null;
                if (prof) { Prof.Reset(); Prof.Enabled = true; }
                var r = new Regenerator().Rebuild(part.Clone());
                if (prof)
                {
                    Prof.Enabled = false;
                    foreach (var (pn, pms, calls) in Prof.Results().Where(x => x.name.StartsWith("blend", StringComparison.Ordinal) || x.name.StartsWith("direct", StringComparison.Ordinal) || x.name.StartsWith("csg", StringComparison.Ordinal)).OrderByDescending(x => x.ms).Take(14))
                        Console.WriteLine($"      {pn,-28} {pms,8:0.0} ms x{calls}");
                }
                foreach (var id in blends) best[id] = Math.Min(best[id], r.Status[id].Milliseconds);
            }
            var sum = best.Values.Sum();
            total += sum;
            Console.WriteLine($"{sum,9:0.0} ms  {name} ({blends.Count} blend features)");
        }
        Console.WriteLine($"{total,9:0.0} ms  all blend features");
        return 0;
    }

    /// <summary>
    /// The cases of the stress shapes named (all their selections, kinds and sizes), each scored: for a regression test.
    /// </summary>
    public static List<(string name, string outcome, string detail)> Cases(IEnumerable<string> shapeNames)
    {
        var res = new List<(string, string, string)>();
        foreach (var (shape, build) in Demos.StressShapes.Where(s => shapeNames.Contains(s.name)))
        {
            var doc0 = build();
            var r0 = new Regenerator().Rebuild(doc0);
            var body = r0.State.Bodies.Single().Solid.Native!;
            body.Link();
            var diag = body.Bounds.Diagonal;
            var minDim = body.Edges.Where(e => e.Curve is NLine).Select(e => e.Length).Where(l => l > 0.03 * diag).Min();
            foreach (var (selName, edges) in Selections(body, true))
                foreach (var kind in new[] { "fillet", "chamfer" })
                    foreach (var (sizeName, f) in new[] { ("small", 0.08), ("medium", 0.2), ("large", 0.4) })
                    {
                        var c = Score(doc0, body, edges, kind, f * minDim, out var detail, out _, out _, out _);
                        res.Add(($"{shape} | {selName} | {kind} {sizeName}", c, detail));
                    }
        }
        return res;
    }

    /// <summary>
    /// The edge selections tried on a part: each planar face's edges (at most 12), each corner's (three or more
    /// edges), the first few edges alone (tangent chains followed), pairs meeting at a corner, and (a small part) all
    /// of them.
    /// </summary>
    static IEnumerable<(string name, List<NEdge> edges)> Selections(NBody body, bool small)
    {
        // (sharp edges: a smooth one, between faces meeting tangentially, has nothing to blend; the tangent chains of
        // the sharp ones still follow them)
        var edges = body.Edges.Where(e => e.Uses.Count == 2 && !IsSeam(e) && Sharp(e)).OrderBy(e => Key(e.Mid)).ToList();
        var faces = body.Faces.OrderBy(f => Key(Centroid(f))).ToList();
        int fi = 0;
        foreach (var f in faces)
        {
            var fe = f.Coedges.Select(c => c.Edge).Distinct(ReferenceEqualityComparer.Instance).Cast<NEdge>().Where(edges.Contains).ToList();
            if (fe.Count is >= 2 and <= 12 && f.Surface.Kind == SurfaceKind.Plane) yield return ($"face {fi} ({fe.Count} edges)", fe);
            fi++;
            if (!small && fi > 6) break;
        }
        var verts = edges.SelectMany(e => new[] { e.V0, e.V1 }).Distinct(ReferenceEqualityComparer.Instance).Cast<NVertex>().OrderBy(v => Key(v.P)).ToList();
        int vi = 0, corners = 0;
        foreach (var v in verts)
        {
            var at = edges.Where(e => ReferenceEquals(e.V0, v) || ReferenceEquals(e.V1, v)).ToList();
            if (at.Count >= 3 && corners++ < (small ? 4 : 2)) yield return ($"corner {vi} ({at.Count} edges)", at);
            if (at.Count >= 2 && vi % 3 == 0 && vi < (small ? 18 : 6)) yield return ($"pair at {vi}", at.Take(2).ToList());
            vi++;
        }
        for (int i = 0; i < edges.Count && i < (small ? 6 : 3); i++) yield return ($"edge {i}", new List<NEdge> { edges[i * Math.Max(1, edges.Count / 6) % edges.Count] });
        if (small) yield return ($"all ({edges.Count} edges)", edges);
    }

    /// <summary>A case as a part file (FC_STRESS_SAVE=dir: the wrong ones), to look at.</summary>
    /// <summary>
    /// <c>-- bossfillet out.sprt radius [shape]</c>: a stress shape's boss (block with boss by default) with every edge of
    /// the boss filleted in one feature (its top, its sides, its foot), saved to look at.
    /// </summary>
    public static int BossFillet(string path, double radius, string shape = "block with boss")
    {
        var doc0 = Demos.StressShapes.First(s => s.name == shape).build();
        var r0 = new Regenerator().Rebuild(doc0);
        var body = r0.State.Bodies.Single().Solid.Native!;
        body.Link();
        var boss = doc0.Features.First(f => f.Name == "Boss").Id;
        var edges = body.Edges.Where(e => e.Uses.Count == 2 && e.Uses.Any(u => u.Face.Tag.Name.StartsWith(boss + ".", StringComparison.Ordinal)) && Sharp(e)).ToList();
        Console.WriteLine($"{edges.Count} edges");
        Save(Path.GetDirectoryName(Path.GetFullPath(path))!, doc0, edges, "fillet", radius, Path.GetFileNameWithoutExtension(path));
        // (FC_BOSS_ORDERS=n: the edges in n orders, each built, its corner patches counted: the result should not depend on it)
        if (Environment.GetEnvironmentVariable("FC_BOSS_ORDERS") is { } no)
            for (int seed = 0; seed < int.Parse(no); seed++)
            {
                var order = new List<NEdge>(edges);
                var rng = new Random(seed);
                for (int i = order.Count - 1; i > 0 && seed > 1; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
                if (seed == 1) order.Reverse();
                var doc = doc0.Clone();
                var refs = order.Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid }).ToList();
                var fi = doc.AddFeature(new FilletFeature { Name = "Stress", Radius = radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture), Edges = refs });
                var rr = new Regenerator().Rebuild(doc);
                var faces = rr.State.Bodies.Single().Solid.Native!.Faces.Where(f => f.Tag.Name.StartsWith(fi.Id + ".", StringComparison.Ordinal)).ToList();
                var spheres = faces.Count(f => f.Surface.Kind == SurfaceKind.Sphere);
                var order12 = string.Join(",", order.Select(e => edges.IndexOf(e)));
                Console.WriteLine($"order {seed} [{order12}]: ok={rr.Status[fi.Id].Ok} spheres {spheres}, faces {faces.Count}");
                if (spheres < 4) doc.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"boss-order{seed}.sprt"));
            }
        return 0;
    }

    static void Save(string dir, CadDocument doc0, List<NEdge> edges, string kind, double size, string name)
    {
        Directory.CreateDirectory(dir);
        var doc = doc0.Clone();
        var refs = edges.Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid }).ToList();
        var sz = size.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        doc.AddFeature<Feature>(kind == "fillet" ? new FilletFeature { Name = "Stress", Radius = sz, Edges = refs } : new ChamferFeature { Name = "Stress", Distance = sz, Edges = refs });
        doc.Save(Path.Combine(dir, string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + ".sprt"));
    }

    static bool Sharp(NEdge e)
    {
        var (fa, fb) = (e.Uses[0].Face, e.Uses[1].Face);
        var p = e.Mid;
        var na = fa.Surface.Normal(p) * (fa.Sense ? 1 : -1); var nb = fb.Surface.Normal(p) * (fb.Sense ? 1 : -1);
        return Vec3.Dot(na.Normalized, nb.Normalized) < Math.Cos(2 * Math.PI / 180);
    }

    static bool IsSeam(NEdge e) => e.Uses.Count == 2 && ReferenceEquals(e.Uses[0].Face, e.Uses[1].Face);
    static (double, double, double) Key(Vec3 p) => (Math.Round(p.X, 4), Math.Round(p.Y, 4), Math.Round(p.Z, 4));
    static Vec3 Centroid(NFace f) { var ps = f.Coedges.Select(c => c.Start.P).ToList(); return ps.Count == 0 ? Vec3.Zero : ps.Aggregate(Vec3.Zero, (a, b) => a + b) / ps.Count; }

    static string Score(CadDocument doc0, NBody body, List<NEdge> edges, string kind, double size, out string detail, out double ms, out int direct, out int boolean)
    {
        detail = ""; ms = 0; direct = 0; boolean = 0;
        var doc = doc0.Clone();
        var refs = edges.Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid }).ToList();
        var sz = size.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Feature blend = kind == "fillet" ? new FilletFeature { Name = "Stress", Radius = sz, Edges = refs, TangentPropagation = true }
            : new ChamferFeature { Name = "Stress", Distance = sz, Edges = refs, TangentPropagation = true };
        doc.AddFeature(blend);
        var convex = edges.Select(NativeBlend.IsConvex).ToList();
        var (d0, b0) = DirectBlend.Counts;
        var sw = Stopwatch.StartNew();
        RegenResult r;
        // (a minute at most: where the rebuild heeds it)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { r = new Regenerator().Rebuild(doc, null, cts.Token); }
        catch (OperationCanceledException) { ms = sw.Elapsed.TotalMilliseconds; detail = "slow: cancelled after a minute"; return "wrong"; }
        catch (Exception ex) { ms = sw.Elapsed.TotalMilliseconds; detail = "exception: " + ex.Message; return "wrong"; }
        ms = sw.Elapsed.TotalMilliseconds;
        // (a blend taking more than ten seconds is a failure too, whatever it makes)
        if (ms > 10000) { detail = $"slow: {ms / 1000:0} s"; return "wrong"; }
        var (d1, b1) = DirectBlend.Counts;
        direct = d1 - d0; boolean = b1 - b0;
        var st = r.Status[blend.Id];
        if (!st.Ok) { detail = "refused: " + Short(st.Error); return "refused"; }
        if (r.State.Bodies.Count != 1) { detail = $"bodies: {r.State.Bodies.Count}"; return "wrong"; }
        var s = r.State.Bodies[0].Solid;
        if (s.Native is not { } nb) { detail = "faceted: no exact body"; return "wrong"; }
        if (nb.Validate() is { } bad) { detail = "invalid: " + Short(bad); return "wrong"; }
        if (s.Topology.OpenHalfEdges != 0) { detail = $"open mesh: {s.Topology.OpenHalfEdges} half-edges"; return "wrong"; }
        // (a tool's end standing: not one flush with a face beside it, in its plane, as a concave blend ending on a face
        // it runs out onto leaves)
        nb.Link();
        var ftol = 1e-7 * Math.Max(1, nb.Bounds.Diagonal);
        var caps = nb.Faces.Where(f => f.Tag.Name.StartsWith(blend.Id + ".", StringComparison.Ordinal)
            && (f.Tag.Name.EndsWith(".End", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".Start", StringComparison.Ordinal)
                || f.Tag.Name.EndsWith(".End0", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".End1", StringComparison.Ordinal))
            && !f.Coedges.SelectMany(c => c.Edge.Uses).Select(u => u.Face).Any(g => !ReferenceEquals(g, f) && g.Surface.Kind == SurfaceKind.Plane
                && f.Surface.Kind == SurfaceKind.Plane && g.Surface.CoincidentWith(f.Surface, ftol))).ToList();
        if (caps.Count > 0) { detail = $"tool end left: {caps.Count} face(s)"; return "wrong"; }
        // (each edge picked blended: none of the result's sharp edges still runs through its middle; a blend left out
        // silently leaves a valid body)
        {
            var tol = 1e-6 * Math.Max(1, body.Bounds.Diagonal);
            nb.Link();
            bool Through(NEdge x, Vec3 p)
            {
                var t = x.Curve.Param(p, 0.5 * (x.T0 + x.T1));
                return t >= Math.Min(x.T0, x.T1) - 1e-9 && t <= Math.Max(x.T0, x.T1) + 1e-9 && Vec3.Distance(x.Curve.Eval(t), p) < tol;
            }
            var left = edges.Count(e => nb.Edges.Any(x => x.Uses.Count == 2 && x.Bounds().Contains(e.Mid, tol) && Sharp(x) && Through(x, e.Mid)));
            if (left > 0) { detail = $"edge not blended: {left} of {edges.Count}"; return "wrong"; }
        }
        var v0 = NTess.ToSolid(body).Volume(); var v1 = s.Volume();
        if (convex.All(c => c == true) && v1 >= v0 - 1e-9 * Math.Abs(v0)) { detail = $"volume: not less ({v0:0.###} -> {v1:0.###})"; return "wrong"; }
        if (convex.All(c => c == false) && v1 <= v0 + 1e-9 * Math.Abs(v0)) { detail = $"volume: not more ({v0:0.###} -> {v1:0.###})"; return "wrong"; }
        if (ExportProblem(s, blend.Id) is { } ep) { detail = "export: " + ep; return "wrong"; }
        detail = st.Warnings.Count > 0 ? "warning: " + Short(st.Warnings[0]) : "ok";
        return "right";
    }

    static string Short(string? m) => m == null ? "" : m.Length > 140 ? m[..140] + "…" : m;

    static readonly MethodInfo PipeFit = typeof(StepWriter).GetMethod("PipeFit", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly Type ParamSurfaceType = typeof(StepWriter).GetNestedType("ParamSurface", BindingFlags.NonPublic)!;
    static readonly MethodInfo Of = ParamSurfaceType.GetMethod("Of")!;
    static readonly MethodInfo Find = ParamSurfaceType.GetMethod("Find")!;

    /// <summary>Written as STEP: an error, or a fillet face whose edges are not on its surface as written (no parameter curves for them).</summary>
    static string? ExportProblem(Solid s, string blendId)
    {
        try
        {
            using var ms = new MemoryStream();
            StepWriter.LastError = null;
            StepWriter.Write(ms, new[] { ("part", s) }, "part");
            if (StepWriter.LastError is { } ex) return "write: " + Short(ex.Message);
            var x = ExactBrepFactory.FromNative(s.Native!);
            if (x.Warnings.Count > 0) return "exact: " + Short(x.Warnings[0]);
            foreach (var f in x.Faces.Where(f => f.Surface.Kind == SurfaceKind.Pipe && f.Name.StartsWith(blendId + ".", StringComparison.Ordinal)))
            {
                var fit = (BSplineSurface)PipeFit.Invoke(null, new object[] { f, x })!;
                var ps = Of.Invoke(null, new object[] { fit })!;
                foreach (var (ei, _) in f.Loops.SelectMany(l => l.Edges))
                {
                    object? hint = null;
                    foreach (var p in x.Edges[ei].Points)
                    {
                        if (Find.Invoke(ps, new object?[] { p, hint, 1e-4, false }) is not { } uv)
                        {
                            // (FC_STRESS_WHY: where along the spine, and how far off, by a grid of the fitted surface)
                            if (Environment.GetEnvironmentVariable("FC_STRESS_WHY") != null && f.Surface.Def.Spine is { } sp)
                            {
                                double best = double.MaxValue;
                                for (int i = 0; i <= 200; i++)
                                    for (int j = 0; j <= 200; j++)
                                        best = Math.Min(best, Vec3.Distance(p, fit.Eval(fit.KnotsU[0] + (fit.KnotsU[^1] - fit.KnotsU[0]) * i / 200, fit.KnotsV[0] + (fit.KnotsV[^1] - fit.KnotsV[0]) * j / 200)));
                                var bs = sp.Ball(p, f.Surface.Def.Radius).s;
                                // (the lookup itself, from the grid's nearest: whether the point is on the fit and only the
                                // lookup along the edge lost it)
                                {
                                    double bu = 0, bv = 0, bdd = double.MaxValue;
                                    for (int i = 0; i <= 200; i++)
                                        for (int j = 0; j <= 200; j++)
                                        {
                                            double uu = fit.KnotsU[0] + (fit.KnotsU[^1] - fit.KnotsU[0]) * i / 200, vv = fit.KnotsV[0] + (fit.KnotsV[^1] - fit.KnotsV[0]) * j / 200;
                                            var dd = Vec3.Distance(p, fit.Eval(uu, vv));
                                            if (dd < bdd) { bdd = dd; bu = uu; bv = vv; }
                                        }
                                    var again = Find.Invoke(ps, new object?[] { p, new Vec2(bu, bv), 1e-4, false });
                                    Console.WriteLine($"    off the exact pipe itself by {Vec3.Distance(p, f.Surface.Project(p)):g3}");
                                    Console.WriteLine($"    from the grid's nearest the lookup {(again == null ? "fails too" : "finds it")}; the edge {x.Edges[ei].Curve.GetType().Name} {x.Edges[ei].Points[0]} -> {x.Edges[ei].Points[^1]}, its other face {string.Join(",", x.Faces.Where(g => g != f && g.Loops.Any(l => l.Edges.Any(q => q.Edge == ei))).Select(g => g.Name + ":" + g.Surface.Kind))}");
                                }
                                // (densely near the point's place along the spine)
                                double vlo = Math.Max(fit.KnotsV[0], bs - 0.05 * (sp.S1 - sp.S0)), vhi = Math.Min(fit.KnotsV[^1], bs + 0.05 * (sp.S1 - sp.S0));
                                for (int i = 0; i <= 400; i++)
                                    for (int j = 0; j <= 400; j++)
                                        best = Math.Min(best, Vec3.Distance(p, fit.Eval(fit.KnotsU[0] + (fit.KnotsU[^1] - fit.KnotsU[0]) * i / 400, vlo + (vhi - vlo) * j / 400)));
                                Console.WriteLine($"    off: {p} at s {bs:g4} of {sp.S0:g4}..{sp.S1:g4}, ~{best:g3} from the fit; fit v {fit.KnotsV[0]:g4}..{fit.KnotsV[^1]:g4}");
                            }
                            return $"an edge off its written surface ({f.Name})";
                        }
                        hint = uv;
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "exception: " + Short(ex.InnerException?.Message ?? ex.Message); }
    }
}

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
                // (sizes against the part's smallest straight edge: small, medium and large blends for it)
                var lengths = body.Edges.Where(e => e.Curve is NLine).Select(e => e.Length).Where(l => l > 1e-6).ToList();
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
                        if (c == "wrong" && Environment.GetEnvironmentVariable("FC_STRESS_SAVE") is { } saveDir) Save(saveDir, doc0, edges, kind, f * minDim, $"{shape} {selName} {kind} {sizeName}");
                        if (all || c != "right") Console.WriteLine($"{c,-7} {shape} | {selName} | {kind} {sizeName} ({f * minDim:0.###}) | {detail} [{ms:0} ms]");
                    }
        }
        Console.WriteLine();
        Console.WriteLine($"{cases.Count} cases in {sw.Elapsed.TotalSeconds:0} s: {cases.Count(c => c.Outcome == "right")} right, {cases.Count(c => c.Outcome == "refused")} refused, {cases.Count(c => c.Outcome == "wrong")} wrong");
        Console.WriteLine($"tools {cases.Sum(c => c.Direct)} direct, {cases.Sum(c => c.Boolean)} by boolean");
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
    /// The edge selections tried on a part: each planar face's edges (at most 12), each corner's (three or more
    /// edges), the first few edges alone (tangent chains followed), pairs meeting at a corner, and (a small part) all
    /// of them.
    /// </summary>
    static IEnumerable<(string name, List<NEdge> edges)> Selections(NBody body, bool small)
    {
        var edges = body.Edges.Where(e => e.Uses.Count == 2 && !IsSeam(e)).OrderBy(e => Key(e.Mid)).ToList();
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
    static void Save(string dir, CadDocument doc0, List<NEdge> edges, string kind, double size, string name)
    {
        Directory.CreateDirectory(dir);
        var doc = doc0.Clone();
        var refs = edges.Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Mid }).ToList();
        var sz = size.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        doc.AddFeature<Feature>(kind == "fillet" ? new FilletFeature { Name = "Stress", Radius = sz, Edges = refs } : new ChamferFeature { Name = "Stress", Distance = sz, Edges = refs });
        doc.Save(Path.Combine(dir, string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + ".sprt"));
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
        try { r = new Regenerator().Rebuild(doc); }
        catch (Exception ex) { ms = sw.Elapsed.TotalMilliseconds; detail = "exception: " + ex.Message; return "wrong"; }
        ms = sw.Elapsed.TotalMilliseconds;
        var (d1, b1) = DirectBlend.Counts;
        direct = d1 - d0; boolean = b1 - b0;
        var st = r.Status[blend.Id];
        if (!st.Ok) { detail = "refused: " + Short(st.Error); return "refused"; }
        if (r.State.Bodies.Count != 1) { detail = $"bodies: {r.State.Bodies.Count}"; return "wrong"; }
        var s = r.State.Bodies[0].Solid;
        if (s.Native is not { } nb) { detail = "faceted: no exact body"; return "wrong"; }
        if (nb.Validate() is { } bad) { detail = "invalid: " + Short(bad); return "wrong"; }
        if (s.Topology.OpenHalfEdges != 0) { detail = $"open mesh: {s.Topology.OpenHalfEdges} half-edges"; return "wrong"; }
        var caps = nb.Faces.Where(f => f.Tag.Name.StartsWith(blend.Id + ".", StringComparison.Ordinal)
            && (f.Tag.Name.EndsWith(".End", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".Start", StringComparison.Ordinal)
                || f.Tag.Name.EndsWith(".End0", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".End1", StringComparison.Ordinal))).ToList();
        if (caps.Count > 0) { detail = $"tool end left: {caps.Count} face(s)"; return "wrong"; }
        var v0 = NTess.ToSolid(body).Volume(); var v1 = s.Volume();
        if (convex.All(c => c == true) && v1 >= v0 - 1e-9 * Math.Abs(v0)) { detail = $"volume: not less ({v0:0.###} -> {v1:0.###})"; return "wrong"; }
        if (convex.All(c => c == false) && v1 <= v0 + 1e-9 * Math.Abs(v0)) { detail = $"volume: not more ({v0:0.###} -> {v1:0.###})"; return "wrong"; }
        if (ExportProblem(s) is { } ep) { detail = "export: " + ep; return "wrong"; }
        detail = st.Warnings.Count > 0 ? "warning: " + Short(st.Warnings[0]) : "ok";
        return "right";
    }

    static string Short(string? m) => m == null ? "" : m.Length > 140 ? m[..140] + "…" : m;

    static readonly MethodInfo PipeFit = typeof(StepWriter).GetMethod("PipeFit", BindingFlags.NonPublic | BindingFlags.Static)!;
    static readonly Type ParamSurfaceType = typeof(StepWriter).GetNestedType("ParamSurface", BindingFlags.NonPublic)!;
    static readonly MethodInfo Of = ParamSurfaceType.GetMethod("Of")!;
    static readonly MethodInfo Find = ParamSurfaceType.GetMethod("Find")!;

    /// <summary>Written as STEP: an error, or a fillet face whose edges are not on its surface as written (no parameter curves for them).</summary>
    static string? ExportProblem(Solid s)
    {
        try
        {
            using var ms = new MemoryStream();
            StepWriter.LastError = null;
            StepWriter.Write(ms, new[] { ("part", s) }, "part");
            if (StepWriter.LastError is { } ex) return "write: " + Short(ex.Message);
            var x = ExactBrepFactory.FromNative(s.Native!);
            if (x.Warnings.Count > 0) return "exact: " + Short(x.Warnings[0]);
            foreach (var f in x.Faces.Where(f => f.Surface.Kind == SurfaceKind.Pipe))
            {
                var ps = Of.Invoke(null, new[] { PipeFit.Invoke(null, new object[] { f, x })! })!;
                foreach (var (ei, _) in f.Loops.SelectMany(l => l.Edges))
                {
                    object? hint = null;
                    foreach (var p in x.Edges[ei].Points)
                    {
                        if (Find.Invoke(ps, new object?[] { p, hint, 1e-4, false }) is not { } uv) return $"an edge off its written surface ({f.Name})";
                        hint = uv;
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "exception: " + Short(ex.InnerException?.Message ?? ex.Message); }
    }
}

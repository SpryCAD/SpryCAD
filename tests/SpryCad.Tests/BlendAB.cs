using System.Diagnostics;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Direct blends against booleans: every demo part and sample model rebuilt both ways, each fillet and chamfer
/// feature's result compared (exact volume, face names, validity), with the time the blend features took and why
/// tools were left to the boolean. `blendab [filter]`.
/// </summary>
static class BlendAB
{
    public static int Run(string? filter)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        var docs = new List<(string name, Func<CadDocument> load)>();
        foreach (var (file, build) in Demos.All)
            if (file.EndsWith(".sprt", StringComparison.Ordinal)) docs.Add(("demo " + file, build));
        foreach (var (name, build) in Demos.BlendCases) docs.Add(("case " + name, build));
        foreach (var f in Directory.GetFiles(Path.Combine(dir!, "samples"), "*.sprt", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(Path.Combine(dir!, "tests", "SpryCad.Tests", "models"), "*.sprt", SearchOption.AllDirectories)))
        {
            var path = f;
            docs.Add((Path.GetRelativePath(dir!, f), () => CadDocument.Load(path)));
        }
        var why = new Dictionary<string, int>();
        bool verbose = Environment.GetEnvironmentVariable("FC_BLENDAB_WHY") != null;
        DirectBlend.Debug = m =>
        {
            if (verbose) Console.WriteLine("      " + m);
            var k = m[(m.IndexOf('(') + 1)..].TrimEnd(')');
            // the reason without the face names
            k = System.Text.RegularExpressions.Regex.Replace(k, @"F(?:[0-9a-z]{12}|[0-9])[0-9A-Za-z.]*", "F");
            lock (why) why[k] = why.GetValueOrDefault(k) + 1;
        };
        if (Environment.GetEnvironmentVariable("FC_BLENDAB_NB") != null) NativeBlend.Debug = m => { if (m.StartsWith("blend ")) Console.WriteLine("      " + m); };
        // (FC_BLENDAB_PROF: the profile of the stages, at the end)
        bool prof = Environment.GetEnvironmentVariable("FC_BLENDAB_PROF") != null;
        if (prof) { Prof.Reset(); Prof.Enabled = true; }
        int bad = 0; double tDirect = 0, tBool = 0;
        foreach (var (name, load) in docs)
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            // one part for both (a part built by code gets new random ids each time: its face names would differ)
            var part = load();
            var b = Build(() => part.Clone(), false);
            var a = Build(() => part.Clone(), true);
            tDirect += a.ms; tBool += b.ms;
            var diffs = new List<string>();
            for (int i = 0; i < a.feats.Count; i++)
            {
                var (fa, fb) = (a.feats[i], b.feats[i]);
                if (fa.error != fb.error) diffs.Add($"{fa.name}: error '{fa.error}' vs '{fb.error}'");
                if (Math.Abs(fa.vol - fb.vol) > 1e-6 * Math.Max(1, Math.Abs(fb.vol))) diffs.Add($"{fa.name}: volume {fa.vol:R} vs {fb.vol:R}");
                if (fa.valid != null) diffs.Add($"{fa.name}: direct invalid: {fa.valid}");
                if (!fa.names.SetEquals(fb.names)) diffs.Add($"{fa.name}: faces +[{string.Join(",", fa.names.Except(fb.names))}] -[{string.Join(",", fb.names.Except(fa.names))}]");
            }
            if (diffs.Count > 0) bad++;
            Console.WriteLine($"{(diffs.Count == 0 ? "same" : "DIFF")} {name}: blends {a.ms:0} ms direct, {b.ms:0} ms boolean");
            foreach (var d in diffs.Take(6)) Console.WriteLine("    " + d);
            if (verbose) for (int i = 0; i < a.feats.Count; i++) Console.WriteLine($"    {a.feats[i].name}: {a.feats[i].ms:0.0} ms direct, {b.feats[i].ms:0.0} ms boolean{(a.feats[i].error != null ? " ERROR " + a.feats[i].error : "")}");
        }
        Console.WriteLine($"blend features: {tDirect:0} ms direct, {tBool:0} ms boolean; tools {DirectBlend.Counts.applied} direct, {DirectBlend.Counts.declined} by boolean");
        foreach (var (k, n) in why.OrderByDescending(x => x.Value)) Console.WriteLine($"  {n,5}  {k}");
        DirectBlend.Debug = null;
        if (prof) foreach (var (n, ms, c) in Prof.Results().Where(x => x.name.StartsWith("direct.", StringComparison.Ordinal) || x.name.StartsWith("blend.", StringComparison.Ordinal)).Take(20)) Console.WriteLine($"  {n,-28} {ms,8:0} ms {c,7}");
        return bad == 0 ? 0 : 1;
    }

    static readonly int Reps = int.TryParse(Environment.GetEnvironmentVariable("FC_BLENDAB_REPS"), out var n) ? n : 5;

    internal sealed record Feat(string name, string? error, double vol, HashSet<string> names, string? valid, double ms);

    internal static (List<Feat> feats, double ms) Build(Func<CadDocument> load, bool direct, int reps = 0)
    {
        var old = DirectBlend.Enabled;
        DirectBlend.Enabled = direct;
        try
        {
            var doc = load();
            // equations to change (FC_BLENDAB_EQ="SpW=26;SpDraft=0"), where the part has them
            foreach (var kv in (Environment.GetEnvironmentVariable("FC_BLENDAB_EQ") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)))
                if (doc.Equations.FirstOrDefault(q => q.Name == kv[0]) is { } eq) eq.Expr = kv[1];
            Csg.ClearCache();
            var feats = new List<Feat>();
            double ms = 0;
            // each blend feature's own time
            for (int i = 0; i < doc.Features.Count; i++)
            {
                var f = doc.Features[i];
                if (f is not (FilletFeature or ChamferFeature)) continue;
                // the median of a few fresh rebuilds of the feature (the earlier ones from the regenerator's cache)
                var times = new List<double>();
                RegenResult r = null!;
                for (int rep = 0; rep < (reps > 0 ? reps : Reps); rep++)
                {
                    var rg = new Regenerator();
                    rg.Rebuild(doc, i);
                    Csg.ClearCache();
                    var sw = Stopwatch.StartNew();
                    r = rg.Rebuild(doc, i + 1);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                var fms = times[times.Count / 2];
                ms += fms;
                var st = r.Status[f.Id];
                double vol = 0; var names = new HashSet<string>(); string? valid = null;
                foreach (var b in r.State.Bodies)
                {
                    vol += KernelTests.Vex(b.Solid);
                    if (b.Solid.Native is { } nb)
                    {
                        foreach (var face in nb.Faces) names.Add(face.Tag.Name);
                        valid ??= nb.Validate();
                    }
                }
                feats.Add(new Feat(f.Name, st.Error, vol, names, direct ? valid : null, fms));
            }
            return (feats, ms);
        }
        finally { DirectBlend.Enabled = old; }
    }
}

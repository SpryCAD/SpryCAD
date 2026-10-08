using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Rebuilding a model gives bit-identical results, whatever the threads do: booleans trace and split in parallel,
/// pattern instances are solved in parallel. Every feature's bodies are fingerprinted (counts, names, vertices,
/// edge curves, surfaces, sample caches) and the final exact volume compared; one rebuild runs while other threads
/// keep half the cores busy, so the parallel work is scheduled differently.
/// </summary>
static class DeterminismTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Determinism: rebuilds are bit-identical, also with the cores busy (handwheel, grips cut last, 8 spokes)", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            // pattern instances solved again in parallel, blends running across neighbouring fillets: the model where
            // the order of an edge pair once followed object hash codes (crossings differing in their last bits)
            var cases = new (string file, (string name, string expr)[] eqs)[]
            {
                (Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "spoked-handwheel-grips-last.sprt"), new[] { ("NS", "8") }),
            };
            foreach (var (file, eqs) in cases)
            {
                var json = CadDocument.Load(file).ToJson();
                (List<string> names, List<string> prints) Build(bool busy)
                {
                    var doc = CadDocument.FromJson(json);
                    foreach (var (n, e) in eqs) doc.Equations.First(q => q.Name == n).Expr = e;
                    Csg.ClearCache();
                    using var load = busy ? new CoreLoad() : null;
                    var r = new Regenerator().Rebuild(doc);
                    var prints = r.States.Select(Bench.Fingerprint).ToList();
                    prints.Add(string.Join(" ", r.State.Bodies.Select(b => KernelTests.Vex(b.Solid).ToString("R"))));
                    return (doc.Features.Select(f => f.Name).Append("(exact volume)").ToList(), prints);
                }
                var first = Build(false);
                foreach (var busy in new[] { false, true })
                {
                    var again = Build(busy);
                    var differ = Enumerable.Range(0, first.names.Count).Where(k => first.prints[k] != again.prints[k]).Select(k => first.names[k]).ToList();
                    t.True(differ.Count == 0, $"{Path.GetFileName(file)}{(busy ? " (cores busy)" : "")}: {string.Join(", ", differ)} differ between rebuilds");
                }
            }
        });
    }

    /// <summary>Threads spinning on half the cores while it lives (the parallel loops get fewer, later workers).</summary>
    sealed class CoreLoad : IDisposable
    {
        volatile bool _stop;
        readonly List<Thread> _threads = new();

        public CoreLoad()
        {
            for (int i = 0; i < Math.Max(1, Environment.ProcessorCount / 2); i++)
            {
                var th = new Thread(() => { double x = 0; while (!_stop) x += Math.Sqrt(x + 1); }) { IsBackground = true };
                th.Start();
                _threads.Add(th);
            }
        }

        public void Dispose()
        {
            _stop = true;
            foreach (var th in _threads) th.Join();
        }
    }
}

/// <summary>Unions of touching pattern copies reused by symmetry give the result of making them.</summary>
static class PatternUnionTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Pattern: unions of touching copies reused by symmetry match the unions made (8 spokes, fillets meeting at the hub)", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var json = CadDocument.Load(Path.Combine(dir!, "samples", "spoked-handwheel.sprt")).ToJson();
            (List<string> names, double volume, int faces, List<string> warnings) Build(bool reuse)
            {
                var doc = CadDocument.FromJson(json);
                doc.Equations.First(q => q.Name == "NS").Expr = "8";
                Csg.ClearCache();
                Regenerator.ReusePatternUnions = reuse;
                try
                {
                    var r = new Regenerator().Rebuild(doc);
                    var nb = r.State.Bodies[0].Solid.Native!;
                    return (nb.Faces.Select(f => f.Tag.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(), KernelTests.Vex(r.State.Bodies[0].Solid),
                        nb.Faces.Count, doc.Features.SelectMany(f => r.Status[f.Id].Warnings.Append(r.Status[f.Id].Error ?? "")).Where(w => w != "").ToList());
                }
                finally { Regenerator.ReusePatternUnions = true; }
            }
            var before = Regenerator.PatternUnionsReused;
            var reused = Build(true);
            t.True(Regenerator.PatternUnionsReused > before, "some unions were reused");
            var made = Build(false);
            t.Eq(reused.faces, made.faces);
            t.True(reused.names.SequenceEqual(made.names), "the same faces, named after the instances they belong to");
            t.True(Math.Abs(reused.volume - made.volume) < 1e-9 * made.volume, $"volume {reused.volume:R} vs {made.volume:R}");
            t.True(reused.warnings.SequenceEqual(made.warnings), $"warnings: {string.Join("; ", reused.warnings)} vs {string.Join("; ", made.warnings)}");
        });
    }
}

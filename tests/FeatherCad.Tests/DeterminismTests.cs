using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

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
                (Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "spoked-handwheel-grips-last.fcad"), new[] { ("NS", "8") }),
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

using System.Diagnostics;

namespace FeatherCad.Tests;

sealed class TestRunner
{
    public int Passed, Failed;
    readonly string? _filter;
    public TestRunner(string? filter) { _filter = filter; }

    public void Test(string name, Action body)
    {
        if (_filter != null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return;
        var sw = Stopwatch.StartNew();
        try
        {
            body();
            Passed++;
            Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
            if (ex is not AssertException) Console.WriteLine(ex.StackTrace);
        }
    }

    public void True(bool cond, string msg) { if (!cond) throw new AssertException(msg); }
    public void Eq<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new AssertException($"expected {expected}, got {actual}"); }
    public void Near(double actual, double expected, double tol) { if (Math.Abs(actual - expected) > tol) throw new AssertException($"expected {expected} ±{tol}, got {actual}"); }
}

sealed class AssertException(string msg) : Exception(msg);

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 4 && args[0] == "repro") { Bench.Repro(args[1], args[2], args[3], args[4]); return 0; }
        if (args.Length > 1 && args[0] == "bench")
        {
            Bench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 5);
            return 0;
        }
        var filter = args.Length > 0 ? args[0] : null;
        var t = new TestRunner(filter);
        int result = 0;
        var thread = new Thread(() =>
        {
            Console.WriteLine("Kernel");
            KernelTests.Run(t);
            Console.WriteLine("Exact");
            ExactTests.Run(t);
            Console.WriteLine("Native");
            NativeTests.Run(t);
            NativeBoolTests.Run(t);
            NativeRevolveTests.Run(t);
            Console.WriteLine("Core");
            CoreTests.Run(t);
            Console.WriteLine($"\n{t.Passed} passed, {t.Failed} failed");
            result = t.Failed == 0 ? 0 : 1;
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        return result;
    }
}

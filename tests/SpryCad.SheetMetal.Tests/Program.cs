using System.Diagnostics;

namespace SpryCad.SheetMetal.Tests;

sealed class TestRunner(string? filter)
{
    public int Passed, Failed;

    public void Test(string name, Action body)
    {
        if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
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
    public void False(bool cond, string msg) { if (cond) throw new AssertException(msg); }
    public void Eq<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new AssertException($"expected {expected}, got {actual}"); }
    public void Near(double actual, double expected, double tol) { if (!(Math.Abs(actual - expected) <= tol)) throw new AssertException($"expected {expected} ±{tol}, got {actual}"); }

    /// <summary>Asserts <paramref name="body"/> throws <typeparamref name="T"/> with a message containing <paramref name="words"/>.</summary>
    public void Throws<T>(Action body, string words) where T : Exception
    {
        try { body(); }
        catch (T ex)
        {
            if (!ex.Message.Contains(words, StringComparison.OrdinalIgnoreCase)) throw new AssertException($"the message \"{ex.Message}\" does not say \"{words}\"");
            return;
        }
        throw new AssertException($"no {typeof(T).Name} (\"{words}\")");
    }
}

sealed class AssertException(string msg) : Exception(msg);

static class Program
{
    static int Main(string[] args)
    {
        var t = new TestRunner(args.Length > 0 ? args[0] : null);
        Console.WriteLine("Sheet metal");
        BendMathTests.Run(t);
        ProfileTests.Run(t);
        GraphTests.Run(t);
        FeatureTests.Run(t);
        Console.WriteLine($"{t.Passed} passed, {t.Failed} failed");
        return t.Failed == 0 ? 0 : 1;
    }
}

using System.Diagnostics;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary>
/// <c>-- fillet file.fcad FaceA FaceB hx hy hz radius [timeoutSec]</c>: appends a fillet on the edge between two
/// named faces and rebuilds with a watchdog, logging boolean stages with timestamps (to find hangs).
/// </summary>
static class Hang
{
    public static int Run(string[] a)
    {
        var doc = CadDocument.FromJson(File.ReadAllText(a[1]));
        double D(int i) => double.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
        var timeout = a.Length > 8 ? D(8) : 30;
        doc.AddFeature(new FilletFeature { Radius = a[7], Edges = { new EdgeRef { A = a[2], B = a[3], Hint = new Vec3(D(4), D(5), D(6)) } } });
        var sw = Stopwatch.StartNew();
        NativeBlend.Debug = m => Console.WriteLine("    overflow: " + m);
        FeatherCad.Kernel.Native.NBoolean.Trace = m =>
        {
            if (m.StartsWith("[") || m.StartsWith("sections") || m.StartsWith("pieces")) Console.WriteLine($"{sw.ElapsedMilliseconds,7} ms  {(m.Length > 160 ? m[..160] : m)}");
        };
        RegenResult? r = null; Exception? err = null;
        var t = new Thread(() => { try { r = new Regenerator().Rebuild(doc); } catch (Exception ex) { err = ex; } }, 256 * 1024 * 1024) { IsBackground = true };
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(timeout)))
        {
            Console.WriteLine($"HANG: still running after {timeout} s");
            return 2;
        }
        FeatherCad.Kernel.Native.NBoolean.Trace = null;
        if (err != null) { Console.WriteLine("EXCEPTION " + err); return 1; }
        foreach (var f in doc.Features.TakeLast(3)) Console.WriteLine($"  {f.Name}: {r!.Status[f.Id].Milliseconds:0} ms {r.Status[f.Id].Error} {string.Join(";", r.Status[f.Id].Warnings)}");
        Console.WriteLine($"done in {sw.ElapsedMilliseconds} ms, fallbacks {Csg.NativeFallback} ({Csg.LastFallbackReason})");
        return 0;
    }
}

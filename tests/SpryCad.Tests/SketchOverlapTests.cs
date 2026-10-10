using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Sketches whose closed contours overlap.</summary>
static class SketchOverlapTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Extrude of a sketch whose closed contours cross: their union, one valid exact body", () =>
        {
            // a circle over a rectangle's end used to be taken as a hole in it (its first point inside), the
            // regions crossing in one body: open, no error
            var doc = Demos.OverlappingProfile();
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Eq(r.State.Bodies.Count, 1);
            var nb = r.State.Bodies[0].Solid.Native;
            t.True(nb != null && nb.Validate() == null, $"exact and closed: {nb?.Validate()}");
            // the union's area: the rectangle, the end circles' halves beyond its ends and their caps above and below
            // it, the middle circle's caps (segments beyond |y| = 4)
            double R = 6, d = 4, seg = R * R * Math.Acos(d / R) - d * Math.Sqrt(R * R - d * d);
            var area = 60 * 8 + 2 * (Math.PI * R * R / 2 + seg) + 2 * seg;
            var v = KernelTests.Vex(r.State.Bodies[0].Solid);
            t.True(Math.Abs(v - 10 * area) < 1e-6 * v, $"volume {v:R}, expected {10 * area:R}");
        });
    }
}

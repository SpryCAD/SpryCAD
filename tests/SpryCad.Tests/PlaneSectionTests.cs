using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>Planes across cones (exact conics) and tori (closed-form points) instead of traced sections.</summary>
static class PlaneSectionTests
{
    const double Tol = 1e-6;

    // r = 10 + z·tan(half) about Z: the apex below the origin for a positive half angle
    static NSurface Cone(double half) => new(new SurfaceDef { Kind = SurfaceKind.Cone, Origin = Vec3.Zero, Axis = Vec3.UnitZ, RefDir = Vec3.UnitX, Radius = 10, Param2 = half });
    static NSurface Torus() => new(new SurfaceDef { Kind = SurfaceKind.Torus, Origin = Vec3.Zero, Axis = Vec3.UnitZ, RefDir = Vec3.UnitX, Radius = 10, Param2 = 2 });
    static NSurface Plane(Vec3 p, Vec3 n) => NSurface.FromPlane(SpryCad.Kernel.Plane.FromPointNormal(p, n.Normalized));

    // every curve's points (over a range of parameters) on both surfaces
    static void OnBoth(TestRunner t, List<NCurve> cs, NSurface a, NSurface b)
    {
        foreach (var c in cs)
        {
            // a polyline's own (exact) points; an analytic curve's over a range of its parameter
            var pts = c is NPolyline pl ? pl.Pts
                : Enumerable.Range(0, 41).Select(i => c.Eval(c.Periodic ? 2 * Math.PI * i / 40 : -3 + 6.0 * i / 40)).ToArray();
            foreach (var p in pts)
                t.True(Math.Abs(a.F_(p)) < 1e-9 && Math.Abs(b.F_(p)) < 1e-9, $"{c.GetType().Name} point {p} off: {a.F_(p):e2} {b.F_(p):e2}");
        }
    }

    public static void Run(TestRunner t)
    {
        var half = Math.PI / 6;
        t.Test("Plane x cone: ellipse, parabola, hyperbola (one branch: the cone's nappe), exact", () =>
        {
            var cone = Cone(half);
            // tilted 20°: an ellipse
            var pl = Plane(new Vec3(0, 0, 5), new Vec3(Math.Sin(0.35), 0, Math.Cos(0.35)));
            var cs = NIntersect.Analytic(pl, cone, Tol)!;
            t.True(cs.Count == 1 && cs[0] is NEllipse, "ellipse");
            OnBoth(t, cs, pl, cone);
            // parallel to a side (normal at 90° − half from the axis): a parabola
            var pp = Plane(new Vec3(0, 0, 4), new Vec3(Math.Sin(Math.PI / 2 - half), 0, Math.Cos(Math.PI / 2 - half)));
            cs = NIntersect.Analytic(pp, cone, Tol)!;
            t.True(cs.Count == 1 && cs[0] is NParabola, "parabola");
            OnBoth(t, cs, pp, cone);
            // parallel to the axis: one branch of a hyperbola, above the apex (z > −10/tan 30°)
            foreach (var cn in new[] { cone, Cone(-half) })
            {
                var ph = Plane(new Vec3(4, 0, 0), Vec3.UnitX);
                cs = NIntersect.Analytic(ph, cn, Tol)!;
                t.True(cs.Count == 1 && cs[0] is NHyperbola, "one hyperbola branch");
                OnBoth(t, cs, ph, cn);
            }
            // through the apex: the two generators in the plane (from the apex up the nappe), or none
            var apex = new Vec3(0, 0, -10 / Math.Tan(half));
            var pa = Plane(apex, new Vec3(1, 0, 0.3));
            cs = NIntersect.Analytic(pa, cone, Tol)!;
            t.True(cs.Count == 2 && cs.All(c => c is NLine), "two generators");
            foreach (var c in cs)
                for (int i = 0; i <= 10; i++) { var p = c.Eval(i * 3.0); t.True(Math.Abs(pa.F_(p)) < 1e-9 && Math.Abs(cone.F_(p)) < 1e-9, $"line point {p} off"); }
            t.Eq(NIntersect.Analytic(Plane(apex, new Vec3(0.2, 0, 1)), cone, Tol)!.Count, 0);
        });

        t.Test("Plane x torus: meridian circles, one or two loops, two curves round the axis, none", () =>
        {
            var tor = Torus();
            var cs = NIntersect.Analytic(Plane(Vec3.Zero, Vec3.UnitX), tor, Tol)!;
            t.True(cs.Count == 2 && cs.All(c => c is NCircle ci && Math.Abs(ci.R - 2) < 1e-12), "through the axis: two circles");
            OnBoth(t, cs, Plane(Vec3.Zero, Vec3.UnitX), tor);
            foreach (var (x, loops) in new[] { (5.0, 2), (9.0, 1), (11.5, 1), (13.0, 0) })
            {
                var pl = Plane(new Vec3(x, 0, 0), Vec3.UnitX);
                cs = NIntersect.Analytic(pl, tor, Tol)!;
                t.Eq(cs.Count, loops);
                t.True(cs.All(c => c is NPolyline { IsClosed: true }), "closed loops");
                OnBoth(t, cs, pl, tor);
            }
            // nearly square to the axis, through the tube: two curves running round it
            var tilt = Plane(new Vec3(0, 0, 0.5), new Vec3(0.05, 0, 1));
            cs = NIntersect.Analytic(tilt, tor, Tol)!;
            t.Eq(cs.Count, 2);
            OnBoth(t, cs, tilt, tor);
            // tangent to the outside: left to the tracer
            t.True(NIntersect.Analytic(Plane(new Vec3(12, 0, 0), Vec3.UnitX), tor, Tol) is not { Count: > 0 }, "tangent: no loops");
        });

        t.Test("Plane x cone: hyperbola edges exported as STEP hyperbolas, read back as such", () =>
        {
            var doc = Demos.All.First(d => d.file == "hand-wheel.sprt").build();
            var r = new Regenerator().Rebuild(doc);
            var nb = r.State.Bodies[0].Solid.Native!;
            t.True(nb.Edges.Any(e => e.Curve is NHyperbola), "hyperbola edges in the wheel");
            using var ms = new MemoryStream();
            StepWriter.Write(ms, r.State.Bodies.Select(b => (b.Id, b.Solid)), "wheel");
            var text = System.Text.Encoding.ASCII.GetString(ms.ToArray());
            t.True(text.Contains("HYPERBOLA("), "written as HYPERBOLA");
            var back = StepImport.Read(ms.ToArray(), "F1.");
            t.True(back.Solids.Any(s => s.Body.Edges.Any(e => e.Curve is NHyperbola)), "read back as hyperbolas");
        });
    }
}

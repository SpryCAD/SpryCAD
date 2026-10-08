using FeatherCad.Kernel;
using FeatherCad.Kernel.Native;

namespace FeatherCad.Tests;

/// <summary>Exact mass properties (integrated on the B-rep) against closed forms.</summary>
static class ExactMassTests
{
    static void Rel(TestRunner t, double actual, double expected, double rel = 1e-9, string what = "")
    {
        var err = Math.Abs(actual - expected) / Math.Max(Math.Abs(expected), 1e-300);
        t.True(err <= rel, $"{what} {actual:R} vs {expected:R} (relative error {err:e2})");
    }

    static MassProps Exact(TestRunner t, NBody b)
    {
        var mp = NMass.Of(b);
        t.True(mp != null, "integrable");
        return mp!;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Exact mass properties: box, cylinder, plate with a hole (planes and cylinders)", () =>
        {
            // a 100 × 50 × 20 box at the origin: centre (50, 25, 10), Ixx about the centre m(b² + c²)/12
            var box = Exact(t, NativeTests.Box(Vec3.Zero, new Vec3(100, 50, 20)));
            double m = 100000;
            Rel(t, box.Volume, m, 1e-12, "box volume");
            Rel(t, box.SurfaceArea, 2 * (100 * 50 + 100 * 20 + 50 * 20), 1e-12, "box area");
            Rel(t, box.Center.X, 50, 1e-12); Rel(t, box.Center.Y, 25, 1e-12); Rel(t, box.Center.Z, 10, 1e-12);
            Rel(t, box.AtCenter[0, 0], m * (50 * 50 + 20 * 20) / 12.0, 1e-12, "box Ixx");
            Rel(t, box.AtCenter[2, 2], m * (100 * 100 + 50 * 50) / 12.0, 1e-12, "box Izz");
            t.True(Math.Abs(box.AtCenter[0, 1]) < 1e-6 * box.AtCenter[0, 0], "box: no products of inertia");

            // a cylinder r 10, h 20 along z from (10, 10, 0): V = π r² h, Izz = m r²/2, Ixx = m (3r² + h²)/12
            var cyl = Exact(t, NativeTests.Cyl(new Vec3(10, 10, 0), Vec3.UnitZ, 10, 20, "C"));
            double r = 10, h = 20, mc = Math.PI * r * r * h;
            Rel(t, cyl.Volume, mc, 1e-9, "cylinder volume");
            Rel(t, cyl.SurfaceArea, 2 * Math.PI * r * h + 2 * Math.PI * r * r, 1e-9, "cylinder area");
            Rel(t, cyl.Center.X, 10, 1e-9); Rel(t, cyl.Center.Z, 10, 1e-9);
            Rel(t, cyl.AtCenter[2, 2], mc * r * r / 2, 1e-9, "cylinder Izz");
            Rel(t, cyl.AtCenter[0, 0], mc * (3 * r * r + h * h) / 12, 1e-9, "cylinder Ixx");

            // a 60 × 40 × 10 plate with a round hole r 8 through it
            var outer = ProfileLoop.Polygon(new[] { new Vec2(0, 0), new Vec2(60, 0), new Vec2(60, 40), new Vec2(0, 40) }, "E");
            var hole = ProfileLoop.Circle(new Vec2(30, 20), 8, "H");
            var plate = NBuild.Extrude(new[] { new ProfileRegion(outer, new[] { hole }) }, Frame.Front, 0, 10, "P");
            var pm = Exact(t, plate);
            Rel(t, pm.Volume, 60 * 40 * 10 - Math.PI * 64 * 10, 1e-9, "plate volume");
            Rel(t, pm.SurfaceArea, 2 * (60 * 40 - Math.PI * 64) + 2 * (60 + 40) * 10 + 2 * Math.PI * 8 * 10, 1e-9, "plate area");
        });

        t.Test("Exact mass properties: cone, sphere, torus (revolved)", () =>
        {
            ProfileRegion Poly(params (double x, double y)[] p) => new(ProfileLoop.Polygon(p.Select(q => new Vec2(q.x, q.y)), "L"));
            // cone r 10, h 15: V = π r² h / 3, centroid h/4 above the base
            var cone = Exact(t, NBuild.Revolve(new[] { Poly((0, 0), (10, 0), (0, 15)) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "K"));
            Rel(t, cone.Volume, Math.PI * 100 * 15 / 3, 1e-9, "cone volume");
            Rel(t, cone.SurfaceArea, Math.PI * 100 + Math.PI * 10 * Math.Sqrt(100 + 225), 1e-9, "cone area");
            // sphere r 10: V = 4/3 π r³, A = 4 π r², I = 2/5 m r² about any axis through the centre
            var sph = Exact(t, NativeRevolveTests.Sphere(10));
            double ms = 4.0 / 3 * Math.PI * 1000;
            Rel(t, sph.Volume, ms, 1e-9, "sphere volume");
            Rel(t, sph.SurfaceArea, 4 * Math.PI * 100, 1e-9, "sphere area");
            Rel(t, sph.Principal[0], 0.4 * ms * 100, 1e-9, "sphere inertia");
            Rel(t, sph.Principal[2], 0.4 * ms * 100, 1e-9, "sphere inertia");
            // torus R 20, r 5 (about the y axis): V = 2π² R r², A = 4π² R r, about its axis m (R² + 3r²/4)
            var tor = Exact(t, NBuild.Revolve(new[] { new ProfileRegion(ProfileLoop.Circle(new Vec2(20, 0), 5, "T")) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "T"));
            double mt = 2 * Math.PI * Math.PI * 20 * 25;
            Rel(t, tor.Volume, mt, 1e-9, "torus volume");
            Rel(t, tor.SurfaceArea, 4 * Math.PI * Math.PI * 20 * 5, 1e-9, "torus area");
            Rel(t, tor.Principal[2], mt * (400 + 0.75 * 25), 1e-9, "torus inertia about its axis");
            // a quarter of a tube (partial revolve: planar ends)
            var quarter = Exact(t, NBuild.Revolve(new[] { Poly((5, 0), (10, 0), (10, 20), (5, 20)) }, Frame.Front, Vec2.Zero, new Vec2(0, 1), Math.PI / 2, "Q"));
            Rel(t, quarter.Volume, Math.PI * (100 - 25) * 20 / 4, 1e-9, "quarter tube volume");
        });

        t.Test("Exact mass properties: moved, turned and mirrored bodies; a boolean with fillets", () =>
        {
            var cyl = NativeTests.Cyl(new Vec3(10, 10, 0), Vec3.UnitZ, 10, 20, "C");
            double mc = Math.PI * 100 * 20;
            var turned = Exact(t, cyl.Transformed(Transform.Rotation(new Vec3(5, -3, 2), new Vec3(1, 2, 3).Normalized, 0.7)));
            Rel(t, turned.Volume, mc, 1e-9, "turned volume");
            // principal moments do not depend on the placement: about the axis m r²/2 (the smallest here), across it
            // m (3r² + h²)/12
            Rel(t, turned.Principal[0], mc * 100 / 2, 1e-9, "turned principal (axis)");
            Rel(t, turned.Principal[2], mc * (3 * 100 + 400) / 12, 1e-9, "turned principal (across)");
            var mirrored = Exact(t, cyl.Transformed(Transform.Mirror(Plane.FromPointNormal(Vec3.Zero, Vec3.UnitX))));
            Rel(t, mirrored.Volume, mc, 1e-9, "mirrored volume");
            Rel(t, mirrored.Center.X, -10, 1e-9, "mirrored centre");
            // a box with a cylinder cut out and the cylinder's sphere dimple: against a fine mesh
            var plate = NativeTests.Box(new Vec3(-30, -30, -10), new Vec3(30, 30, 0), "P");
            var ball = NativeRevolveTests.Sphere(8, "B").Transformed(Transform.Translation(new Vec3(10, 10, 0)));
            var dimple = NBoolean.Run(plate, ball, BoolOp.Subtract);
            var ex = Exact(t, dimple);
            Rel(t, ex.Volume, 60 * 60 * 10 - 0.5 * 4.0 / 3 * Math.PI * 512, 1e-9, "dimpled plate volume");
        });

        t.Test("Exact mass properties: a filleted part agrees with ever finer meshes", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var path = Path.Combine(dir!, "tests", "FeatherCad.Tests", "models", "spoked-handwheel-grips-last.fcprt");
            var doc = FeatherCad.Core.Model.CadDocument.Load(path);
            var r = new FeatherCad.Core.Regen.Regenerator().Rebuild(doc);
            var nb = r.State.Bodies[0].Solid.Native!;
            t.True(nb.Faces.Any(f => f.Surface.Kind == SurfaceKind.Pipe), "the part has rolling-ball fillets");
            var cachesBefore = nb.Edges.Select(e => e.SampledTol).ToList();
            var ex = Exact(t, nb);
            // measuring leaves the body as it was (no samples cached on its edges)
            t.True(nb.Edges.Select(e => e.SampledTol).SequenceEqual(cachesBefore), "edge sample caches untouched");
            double MeshVolume(double tol)
            {
                var mp = new MassProps();
                foreach (var f in NTess.Facets(nb.Clone(), tol)) foreach (var tr in Triangulator.TriangulateFace(f)) mp.AddTriangle(tr.A, tr.B, tr.C);
                return mp.Finish().Volume;
            }
            // the mesh's error shrinks with its tolerance, towards the exact value
            var e1 = Math.Abs(MeshVolume(2e-2) - ex.Volume);
            var e2 = Math.Abs(MeshVolume(5e-3) - ex.Volume);
            t.True(e2 < e1, $"finer mesh closer: {e1:g3} then {e2:g3}");
            t.True(e2 / ex.Volume < 5e-4, $"5e-3 mesh within 5e-4 of exact ({e2 / ex.Volume:e2})");
        });
    }
}

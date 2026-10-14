using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Tests;

/// <summary>
/// The brake duct (a part made in the app: an intake lofted off a round collar into a plenum, an outlet tube leaving
/// it at an angle under a drop cut in its top, filleted, then shelled): fillets rolling round earlier fillets, the
/// shell's inside made from the part before its fillets, filleted again.
/// </summary>
static class BrakeDuctTests
{
    static CadDocument Part()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "tests", "SpryCad.Tests", "models"))) dir = Path.GetDirectoryName(dir);
        return CadDocument.Load(Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "parts", "brake-duct-fillets.sprt"));
    }

    static T Feature<T>(CadDocument doc, string name) where T : Feature => doc.Features.OfType<T>().First(f => f.Name == name);

    // (the faces the drop's cut left on its sides, and the plenum's side rounds they meet)
    const string Drop = "Fc6ywqe5gz5hw.S.", SideRounds = "F9hmbac9dv0dw.", Flat = "Fc6ywqe5gz5hw.S.L3qkkja6fnr4";

    /// <summary>Every feature built, the one body valid and closed.</summary>
    static NBody Built(TestRunner t, CadDocument doc, RegenResult r, string what)
    {
        foreach (var f in doc.Features)
            t.True(r.Status[f.Id].Ok, $"{what}: {f.Name} failed: {r.Status[f.Id].Error}");
        var body = r.State.Bodies.Single();
        t.True(body.Solid.Native?.Validate() == null, $"{what}: body invalid: {body.Solid.Native?.Validate()}");
        t.Eq(body.Solid.Topology.OpenHalfEdges, 0);
        return body.Solid.Native!;
    }

    /// <summary>
    /// Sharp edges between a face named one way and a face named another: their faces more than 6 degrees apart at the
    /// middle (not a fillet's fading end, under 5).
    /// </summary>
    static int EdgesBetween(NBody b, Func<string, bool> a, Func<string, bool> c)
        => b.Edges.Count(e => e.Uses.Count == 2 && (a(e.Uses[0].Face.Tag.Name) && c(e.Uses[1].Face.Tag.Name) || c(e.Uses[0].Face.Tag.Name) && a(e.Uses[1].Face.Tag.Name))
            && Vec3.Dot(Out(e.Uses[0].Face, e.Mid), Out(e.Uses[1].Face, e.Mid)) < Math.Cos(6 * Math.PI / 180));
    static Vec3 Out(NFace f, Vec3 p) => (f.Surface.Normal(p) * (f.Sense ? 1 : -1)).Normalized;

    /// <summary>A fillet's end cap (a rolling-ball tool's square end), of the fillet feature with the id.</summary>
    static bool Capped(NFace f, string fillet) => f.Tag.Name.Contains(fillet + ".", StringComparison.Ordinal)
        && (f.Tag.Name.EndsWith(".End0", StringComparison.Ordinal) || f.Tag.Name.EndsWith(".End1", StringComparison.Ordinal));

    public static void Run(TestRunner t)
    {
        t.Test("Brake duct: the tube's rim rolls round the tube bend and up the drop's sides, shelled", () =>
        {
            // (R0.155 and up the ball is too wide for the seam between the tube bend and the side round: it rolls
            // through the flat side under the round; R0.142 to R0.15 that flat is a sliver the join fails on)
            foreach (var (radius, wall) in new[] { ("0.1", "0.03"), ("0.14", "0.03"), ("0.175", "0.03"), ("0.2", "0.03"), ("0.1", "0.125"), ("0.125", "0.125") })
            {
                var doc = Part();
                var rim = Feature<FilletFeature>(doc, "Tube rim");
                rim.Radius = radius; rim.TangentPropagation = true;
                Feature<ShellFeature>(doc, "Shell").Thickness = wall;
                var r = new Regenerator().Rebuild(doc);
                var what = $"R{radius} shelled at {wall}";
                var body = Built(t, doc, r, what);
                t.True(r.Status[rim.Id].Warnings.Count == 0, $"{what}: Tube rim: {string.Join("; ", r.Status[rim.Id].Warnings)}");
                t.True(r.Status[Feature<ShellFeature>(doc, "Shell").Id].Warnings.All(w => !w.Contains("Not inside")), $"{what}: a fillet left out inside");
                // round the corner and up the drop's sides: no sharp edge left between them and the side rounds
                t.Eq(EdgesBetween(body, n => n.StartsWith(Drop, StringComparison.Ordinal) && !n.Contains(".I."),
                    n => n.StartsWith(SideRounds, StringComparison.Ordinal) && !n.Contains(".I.")), 0);
                // and to a point where it fades out at the top of the drop, inside and out: no end cap left short of it
                t.True(!body.Faces.Any(f => Capped(f, rim.Id)), $"{what}: a fade-out end capped short of the point");
            }
        });

        t.Test("Brake duct: exported, every spline edge's curve ends at its vertices (no run on past them)", () =>
        {
            // the rim rolling round the tube bend: its edges split off longer curves (cut back, split where crossed)
            // were written as the whole curve, running on past their vertices, as Inventor drew them
            var doc = Part();
            var rim = Feature<FilletFeature>(doc, "Tube rim");
            rim.Radius = "0.2"; rim.TangentPropagation = true;
            Feature<ShellFeature>(doc, "Shell").Thickness = "0.03";
            var r = new Regenerator().Rebuild(doc);
            var b = ExactBrepFactory.FromNative(Built(t, doc, r, "R0.2"));
            int splines = 0;
            foreach (var e in b.Edges)
            {
                if (e.Curve is not BSplineCurve bs) continue;
                splines++;
                var (s, f) = (bs.Eval(bs.Knots[0]), bs.Eval(bs.Knots[^1]));
                var (v0, v1) = (b.Vertices[e.V0], b.Vertices[e.V1]);
                var off = Math.Min(Math.Max(Vec3.Distance(s, v0), Vec3.Distance(f, v1)), Math.Max(Vec3.Distance(s, v1), Vec3.Distance(f, v0)));
                t.True(off < 1e-4, $"an edge's curve ends {off:g3} from its vertices ({v0} .. {v1})");
            }
            t.True(splines > 0, "spline edges");
        });

        t.Test("Brake duct: the tube bend ends flush under the drop's ramp, no shelf beside it", () =>
        {
            var doc = Part();
            var bend = Feature<FilletFeature>(doc, "Tube bend");
            var r = new Regenerator().Rebuild(doc, doc.Features.IndexOf(bend) + 1);
            t.True(r.Status[bend.Id].Ok, $"Tube bend: {r.Status[bend.Id].Error}");
            var flat = r.State.Bodies.Single().Solid.Native!.Faces.Where(f => f.Tag.Name == Flat).ToList();
            t.True(flat.Count > 0, "the drop's flat");
            // (cut back on the flat's plane alone it reached past the ramp's seam at X 7.061, a shelf under the ramp)
            var minX = flat.SelectMany(f => f.Coedges).Min(c => c.Start.P.X);
            t.True(minX > 7.061 - 1e-3, $"the flat reaches X {minX:R}, under the ramp");
        });

        // (at 0.0625 the tube bend inside is left out: its tool, cut back on the inside of the drop's flat, does not join)
        t.Test("Brake duct: shells 0.03 and 0.125 make the tube bend inside, in under a few seconds", () =>
        {
            foreach (var wall in new[] { "0.03", "0.125" })
            {
                var doc = Part();
                var rim = Feature<FilletFeature>(doc, "Tube rim");
                rim.Radius = "0.3"; rim.TangentPropagation = false;
                var shell = Feature<ShellFeature>(doc, "Shell");
                shell.Thickness = wall;
                var r = new Regenerator().Rebuild(doc);
                Built(t, doc, r, $"shelled at {wall}");
                t.True(r.Status[shell.Id].Warnings.All(w => !w.Contains("Not inside")), $"{wall}: {string.Join("; ", r.Status[shell.Id].Warnings)}");
                // (the inside subtracted from the outside was traced face by face, 27 s at 0.125)
                t.True(r.Status[shell.Id].Milliseconds < 5000, $"{wall}: the shell took {r.Status[shell.Id].Milliseconds:0} ms");
            }
        });

        t.Test("Brake duct: a fillet round the intake collar's base goes all the way round, concave and convex", () =>
        {
            var doc = Part();
            var rim = Feature<FilletFeature>(doc, "Tube rim");
            rim.Radius = "0.3"; rim.TangentPropagation = false;
            var shell = Feature<ShellFeature>(doc, "Shell");
            // (round the seam where the loft leaves the collar: concave, but convex across the bottom, where the loft
            // turns down off it)
            var collar = new FilletFeature
            {
                Name = "Collar base", Radius = "0.1", TangentPropagation = true,
                Edges = { new EdgeRef { A = "F9ykg06njewzt.S.C63kc9mekgs5", B = "Fbqb9zpmy19f8.S.C63kc9mekgs5.5", Hint = new Vec3(0.5, 0.75, 0) } },
            };
            doc.AddFeature(collar, doc.Features.IndexOf(shell));
            shell.Thickness = "0.03";
            var r = new Regenerator().Rebuild(doc);
            var body = Built(t, doc, r, "collar base");
            t.True(r.Status[collar.Id].Warnings.Count == 0, string.Join("; ", r.Status[collar.Id].Warnings));
            // no sharp stretch of the seam left between the collar and the loft
            t.Eq(EdgesBetween(body, n => n == "F9ykg06njewzt.S.C63kc9mekgs5", n => n.StartsWith("Fbqb9zpmy19f8.", StringComparison.Ordinal)), 0);
            // each stretch fading out to a point where the seam turns tangent, outside and in: no end cap left short of it
            var caps = body.Faces.Where(f => Capped(f, collar.Id)).ToList();
            // and made inside too (the seam split where it turns before its two kinds were filleted at their own radii)
            t.True(r.Status[shell.Id].Warnings.All(w => !w.Contains("Not inside")), string.Join("; ", r.Status[shell.Id].Warnings));
            t.True(caps.Count == 0, "a fade-out end capped short of the point: " + string.Join(", ", caps.Select(f => f.Tag.Name + " at " + f.Coedges.First().Start.P)));
        });
    }
}

using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Blends applied straight to the body's faces (<see cref="DirectBlend"/>) against the boolean.</summary>
static class DirectBlendTests
{
    public static void Run(TestRunner t)
    {
        t.Test("Direct blend: the blend cases and sample parts as by boolean (volume, faces), valid, most tools direct", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var models = Demos.BlendCases.Select(c => (c.name, c.build)).ToList();
            foreach (var f in Directory.GetFiles(Path.Combine(dir!, "samples"), "*.sprt", SearchOption.AllDirectories))
            {
                var path = f;
                models.Add((Path.GetFileName(f), () => CadDocument.Load(path)));
            }
            var before = DirectBlend.Counts;
            foreach (var (name, load) in models)
            {
                var a = BlendAB.Build(load, direct: true, reps: 1).feats;
                var b = BlendAB.Build(load, direct: false, reps: 1).feats;
                for (int i = 0; i < a.Count; i++)
                {
                    var (fa, fb) = (a[i], b[i]);
                    t.True(fa.valid == null, $"{name}, {fa.name}: valid ({fa.valid})");
                    // the boolean failing where the direct blend works is fine (the casting's foot, below); a corner
                    // where three fillets meet is the ball's directly, crossing cylinders by boolean (tested below)
                    if (fb.error != null || fa.names.Any(n => n.Contains(".C", StringComparison.Ordinal) && n.EndsWith(".Blend", StringComparison.Ordinal))) continue;
                    t.True(fa.error == null, $"{name}, {fa.name}: {fa.error}");
                    t.True(Math.Abs(fa.vol - fb.vol) <= 1e-7 * Math.Max(1, fb.vol), $"{name}, {fa.name}: volume {fa.vol:R} vs {fb.vol:R}");
                    // the same faces (the boolean may leave a tool's end beside the face it lies on as a face of its own)
                    t.True(fa.names.IsSubsetOf(fb.names), $"{name}, {fa.name}: faces +[{string.Join(",", fa.names.Except(fb.names))}]");
                }
            }
            var after = DirectBlend.Counts;
            int applied = after.applied - before.applied, declined = after.declined - before.declined;
            Console.WriteLine($"      tools: {applied} direct, {declined} by boolean");
            t.True(applied > 4 * declined, "most tools direct");
        });

        t.Test("Direct blend: a boss's foot blended all round (its sides' and rounded corners' tools glued into one ring)", () =>
        {
            // a 120 x 80 boss, its vertical edges rounded R20, blended R6 into the plate (all square: no draft). The
            // boolean fails here (the corner fillets' tools leave their ends as faces of their own beside the plate's
            // top); directly, the foot's eight tools (lines and arcs) meet only at their ends and go on as one
            var a = BlendAB.Build(() => Demos.BlendCases.First(c => c.name == "casting foot, undrafted").build(), direct: true, reps: 1).feats;
            var corners = a.First(f => f.name == "Boss corners"); var foot = a.First(f => f.name == "Foot");
            t.True(foot.error == null, foot.error ?? "");
            t.True(foot.valid == null, foot.valid ?? "");
            // the blend's cross-section: the square r x r less the quarter disc, its centroid x from the wall; along
            // the straight sides, and round the corners at Rc + x (Pappus)
            double r = 6, rc = 20;
            var area = r * r * (1 - Math.PI / 4);
            var x = r * (5.0 / 6 - Math.PI / 4) / (1 - Math.PI / 4);
            var expected = area * (2 * (120 - 2 * rc) + 2 * (80 - 2 * rc) + 2 * Math.PI * (rc + x));
            t.True(Math.Abs(foot.vol - corners.vol - expected) < 1e-6 * expected, $"added {foot.vol - corners.vol:R}, expected {expected:R}");
        });

        t.Test("Fillet ends on a face at an angle: a drafted boss's rounded corners stop at the plate (no notch in it)", () =>
        {
            // the corner edges lean 2° (draft) to the plate's top: the tool's square end used to reach past the top on
            // one side, cutting a notch into the plate round each fillet's foot
            foreach (var direct in new[] { true, false })
            {
                var old = DirectBlend.Enabled;
                DirectBlend.Enabled = direct;
                try
                {
                    var doc = Demos.BlendCases.First(c => c.name == "casting foot, drafted").build();
                    var corners = doc.Features.First(f => f.Name == "Boss corners");
                    var r = new Regenerator().Rebuild(doc, doc.Features.IndexOf(corners) + 1);
                    t.True(r.Status[corners.Id].Ok, r.Status[corners.Id].Error ?? "");
                    // the plate (200 x 150 x 20) whole below its top
                    var below = Csg.Intersect(r.State.Bodies[0].Solid, Builders.Box(new Vec3(-150, -100, -10), new Vec3(150, 100, 20)));
                    var v = KernelTests.Vex(below);
                    t.True(Math.Abs(v - 200 * 150 * 20) < 1e-6 * v, $"{(direct ? "direct" : "boolean")}: the plate {v:R}, not {200 * 150 * 20}");
                }
                finally { DirectBlend.Enabled = old; }
            }
        });

        t.Test("Fillet round a spoke's foot on a rim (undrafted): the tangent chain as one tool when its pieces overflow", () =>
        {
            // the oval spoke's straight side meets the rim along a circle (the exact arc tool), its round ends and
            // other side along curves (the rolling ball, an open chain overflowing where it meets the arc tool's):
            // trimmed, the two collided ("cannot be blended yet"); the whole loop as one rolling-ball tool fits
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var file = Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "flywheel-wide-spokes.sprt");
            foreach (var rh in new[] { "3", "5" })
                foreach (var direct in new[] { true, false })
                {
                    var old = DirectBlend.Enabled;
                    DirectBlend.Enabled = direct;
                    try
                    {
                        var doc = CadDocument.Load(file);
                        foreach (var (n, e) in new[] { ("SpW", "26"), ("SpT", "14"), ("SpDraft", "0"), ("Rh", rh) }) doc.Equations.First(q => q.Name == n).Expr = e;
                        var rim = doc.Features.First(f => f.Name == "Spoke to rim");
                        var r = new Regenerator().Rebuild(doc, doc.Features.IndexOf(rim) + 1);
                        t.True(r.Status[rim.Id].Ok, $"Rh {rh}, {(direct ? "direct" : "boolean")}: {r.Status[rim.Id].Error}");
                        t.True(r.State.Bodies[0].Solid.Native?.Validate() == null, "valid");
                    }
                    finally { DirectBlend.Enabled = old; }
                }
        });

        t.Test("Direct blend: where three fillets meet, a patch of the ball (convex and concave corners, exact volumes)", () =>
        {
            // a fillet's cross-section is the square r x r less the quarter disc; each runs to the cross-section r
            // from a corner where three meet, and the corner is the cube r x r x r there less the ball's octant
            static double Area(double r) => r * r * (1 - Math.PI / 4);
            static double Cube(double r) => r * r * r * (1 - Math.PI / 6);
            var cases = new (string name, string feature, int corners, double expected)[]
            {
                // 80 x 60 x 20, the three edges at a top corner R4 (removed)
                ("box, three edges at a corner", "Corner", 1, 96000 - (Area(4) * ((80 - 4) + (60 - 4) + (20 - 4)) + Cube(4))),
                // all twelve edges R4: each between two corners
                ("box, all edges", "All edges", 8, 96000 - (Area(4) * 4 * ((80 - 8) + (60 - 8) + (20 - 8)) + 8 * Cube(4))),
                // a 50 x 30 x 12 pocket in it, its floor and vertical edges R3 (added; the vertical ones end at the top)
                ("pocket, floor and corners", "Pocket edges", 4, 96000 - 50 * 30 * 12 + Area(3) * (2 * (50 - 6) + 2 * (30 - 6) + 4 * (12 - 3)) + 4 * Cube(3)),
            };
            foreach (var (name, feature, n, expected) in cases)
            {
                var f = BlendAB.Build(() => Demos.BlendCases.First(c => c.name == name).build(), direct: true, reps: 1).feats.First(x => x.name == feature);
                t.True(f.error == null && f.valid == null, $"{name}: {f.error} {f.valid}");
                t.Eq(f.names.Count(x => x.Contains(".C", StringComparison.Ordinal) && x.EndsWith(".Blend", StringComparison.Ordinal)), n);
                t.True(Math.Abs(f.vol - expected) < 1e-6 * f.vol, $"{name}: volume {f.vol:R}, expected {expected:R}");
                // shown: the display mesh closed round the patches (its volume near the exact one)
                var doc = Demos.BlendCases.First(c => c.name == name).build();
                var mesh = new Regenerator().Rebuild(doc).State.Bodies[0].Mesh;
                t.True(mesh.Indices.Length > 0 && Math.Abs(mesh.Volume - expected) < 2e-3 * expected, $"{name}: mesh volume {mesh.Volume:R}");
            }
        });

        t.Test("Fillet tangent propagation keeps to edges of one kind (not on from a convex edge to a concave one)", () =>
        {
            // a drafted block's side top edge (convex) runs straight on under a box that overhangs the narrower top,
            // level with it (concave there): one edge picked on the block's own top rounds that edge only
            foreach (var direct in new[] { true, false })
            {
                var f = BlendAB.Build(() => Demos.BlendCases.First(c => c.name == "drafted block, top edge under an overhang").build(), direct, reps: 1).feats.First(x => x.name == "Top edge");
                t.True(f.error == null && f.valid == null, $"{f.error} {f.valid}");
                var blends = f.names.Where(n => n.EndsWith(".S.F", StringComparison.Ordinal) || n.EndsWith(".Blend", StringComparison.Ordinal)).ToList();
                t.True(blends.Count == 1 && blends[0].StartsWith("F6.0.", StringComparison.Ordinal), $"{(direct ? "direct" : "boolean")}: blend faces {string.Join(", ", blends)}");
            }
        });

        t.Test("Fillet beside other geometry: the tool reaches past the edge's corner only into empty space (a taller wall across a narrow gap left whole)", () =>
        {
            // the boolean's tool reaches past the corner (half the size) to keep clear of the faces it blends; with a
            // wall 2 mm across the gap, that reach cut a wedge out of it
            foreach (var direct in new[] { true, false })
            {
                var old = DirectBlend.Enabled;
                DirectBlend.Enabled = direct;
                try
                {
                    var doc = Demos.BlendCases.First(c => c.name == "wall beside a taller one").build();
                    var r = new Regenerator().Rebuild(doc);
                    foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
                    // the high wall and the base under it: 29 x 40 x 30 + 29 x 40 x 10
                    var right = Csg.Intersect(r.State.Bodies[0].Solid, Builders.Box(new Vec3(1, -30, -5), new Vec3(40, 30, 50)));
                    var v = KernelTests.Vex(right);
                    t.True(Math.Abs(v - 46400) < 1e-6 * v, $"{(direct ? "direct" : "boolean")}: the high wall {v:R}, not 46400");
                }
                finally { DirectBlend.Enabled = old; }
            }
        });

        t.Test("Direct blend: off (FC_BLEND=boolean) applies nothing directly", () =>
        {
            var before = DirectBlend.Counts;
            BlendAB.Build(() => Demos.BlendCases.First(c => c.name == "plate, hole rims").build(), direct: false, reps: 1);
            t.Eq(before.applied, DirectBlend.Counts.applied);
        });
    }
}

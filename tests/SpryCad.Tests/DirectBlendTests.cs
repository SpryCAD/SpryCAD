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

        t.Test("Chamfer round a drafted boss's foot: its rounded corners meet the plate along ellipses (one swept tool)", () =>
        {
            // the corner fillets' axes lean (draft 2°): the foot runs round lines and ellipses, which the exact tools
            // do not cover ("unsupported edge geometry"); swept along the loop, the chamfer's cross-section (legs 6
            // along the plate and the boss's side, 92° apart) adds its area along the loop
            var a = BlendAB.Build(() => Demos.BlendCases.First(c => c.name == "casting foot, drafted, chamfered").build(), direct: true, reps: 1).feats;
            var corners = a.First(f => f.name == "Boss corners"); var foot = a.First(f => f.name == "Foot");
            t.True(foot.error == null, foot.error ?? "");
            t.True(foot.valid == null, foot.valid ?? "");
            double d = 6, rc = 20;
            var area = 0.5 * d * d * Math.Sin(Math.PI / 2 + 2 * Math.PI / 180);
            // the triangle's centroid a third of the legs out from the wall; round the corners nearly circles of Rc
            var approx = area * (2 * (120 - 2 * rc) + 2 * (80 - 2 * rc) + 2 * Math.PI * (rc + d / 3));
            var added = foot.vol - corners.vol;
            t.True(Math.Abs(added - approx) < 0.01 * approx, $"added {added:R}, about {approx:R}");
        });

        t.Test("Chamfer types round a drafted boss (foot and top): the cross-section's area along the loop, the reference face kept all round", () =>
        {
            // the plate meets the boss's side at 92°, its top at 88° (draft 2°). Pappus: the triangle's area along the
            // straight sides, and round the corners (R20) at the centroid's distance from the side
            double dr = 2 * Math.PI / 180, rc = 20;
            double Est(double plate, double wall, bool top)
            {
                var theta = top ? Math.PI / 2 - dr : Math.PI / 2 + dr;
                var area = 0.5 * plate * wall * Math.Sin(theta);
                // the centroid's distance from the side (horizontally): out over the plate, in under the top (the side
                // leaning back towards it at the plate, away below the top)
                var x = (plate - wall * Math.Sin(dr)) / 3;
                var w = top ? 60 - 60 * Math.Tan(dr) : 60; var h = top ? 40 - 60 * Math.Tan(dr) : 40;
                return area * (2 * (2 * w - 2 * rc) + 2 * (2 * h - 2 * rc) + 2 * Math.PI * (rc + (top ? -x : x)));
            }
            double Changed(string name)
            {
                var a = BlendAB.Build(() => Demos.BlendCases.First(c => c.name == name).build(), direct: true, reps: 1).feats;
                var corners = a.First(f => f.name == "Boss corners"); var foot = a.First(f => f.name == "Foot");
                t.True(foot.error == null, $"{name}: {foot.error}");
                t.True(foot.valid == null, $"{name}: {foot.valid}");
                return Math.Abs(foot.vol - corners.vol);
            }
            void Near(string name, double v, double e) => t.True(Math.Abs(v - e) < 2e-3 * e, $"{name}: {v:R}, about {e:R}");
            // which face the distance is measured on: both are possible (the first edge's first face); flipped, the other
            var other = 6 * Math.Sin(Math.PI / 6) / Math.Sin(Math.PI / 2 + dr + Math.PI / 6);
            foreach (var (name, plate, wall, top) in new[]
                     {
                         ("casting foot, drafted, chamfered", 6.0, 6.0, false), ("boss top, drafted, chamfered", 6.0, 6.0, true),
                     })
                Near(name, Changed(name), Est(plate, wall, top));
            var dd = Changed("casting foot, drafted, chamfered 6 x 3"); var ddf = Changed("casting foot, drafted, chamfered 6 x 3 flipped");
            var (e63, e36) = (Est(6, 3, false), Est(3, 6, false));
            bool onPlate = Math.Abs(dd - e63) < Math.Abs(dd - e36);
            Near("6 x 3", dd, onPlate ? e63 : e36);
            Near("6 x 3 flipped", ddf, onPlate ? e36 : e63);
            // at 30° to the reference face: the same face as the distances'
            Near("6 at 30°", Changed("casting foot, drafted, chamfered 6 at 30°"), onPlate ? Est(6, other, false) : Est(other, 6, false));
        });

        t.Test("Swept chamfer over a hollow behind its corner: the boolean adds no more than the chamfer (the tool bounded by the faces)", () =>
        {
            // under the boss's front side the plate is 1 thick: the swept tool's point beyond the corner (inside, for
            // a concave edge) lies in the pocket below, which the boolean would fill
            foreach (var direct in new[] { false, true })
            {
                double Added(string name)
                {
                    var old = DirectBlend.Enabled;
                    DirectBlend.Enabled = direct;
                    try
                    {
                        var doc = Demos.BlendCases.First(c => c.name == name).build();
                        var foot = doc.Features.First(f => f.Name == "Foot");
                        Csg.ClearCache();
                        var r0 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(foot));
                        var r1 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(foot) + 1);
                        t.True(r1.Status[foot.Id].Ok, $"{name}: {r1.Status[foot.Id].Error}");
                        t.True(r1.State.Bodies[0].Solid.Native?.Validate() == null, $"{name}: valid");
                        return KernelTests.Vex(r1.State.Bodies[0].Solid) - KernelTests.Vex(r0.State.Bodies[0].Solid);
                    }
                    finally { DirectBlend.Enabled = old; }
                }
                var plain = Added("casting foot, drafted, chamfered"); var hollow = Added("casting foot, drafted, chamfered, hollow under");
                t.True(Math.Abs(hollow - plain) < 1e-6 * plain, $"{(direct ? "direct" : "boolean")}: added {hollow:R} over the hollow, {plain:R} without");
            }
        });

        t.Test("Shelled bracket's inside top edges: the side cut back at a counterbore goes with the sides it meets at the corners, applied directly", () =>
        {
            // the loop round the shell's inside (four lines, mitred at the corners, and an arc), one side cut back where
            // a counterbore reaches it: cut back alone, its ends no longer matched its neighbours' and the whole loop
            // went by boolean; glued with them first, the run is cut back as one and applied directly (the
            // counterbore's own rim fillet, cut back at the same wall, then by boolean)
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var file = Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "shelled-bracket-inner-fillet.sprt");
            double Vol(bool direct, out int declined)
            {
                var old = DirectBlend.Enabled;
                DirectBlend.Enabled = direct;
                try
                {
                    var doc = CadDocument.Load(file);
                    var f = doc.Features.First(x => x.Id == "F18");
                    var r0 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(f));
                    var before = DirectBlend.Counts.declined;
                    Csg.ClearCache();
                    var r = new Regenerator().Rebuild(doc, doc.Features.IndexOf(f) + 1);
                    declined = DirectBlend.Counts.declined - before;
                    t.True(r.Status[f.Id].Ok, r.Status[f.Id].Error ?? "");
                    t.True(r.State.Bodies[0].Solid.Native?.Validate() == null, "valid");
                    return KernelTests.Vex(r.State.Bodies[0].Solid);
                }
                finally { DirectBlend.Enabled = old; }
            }
            var vd = Vol(true, out var declined); var vb = Vol(false, out _);
            t.True(Math.Abs(vd - vb) < 1e-7 * vb, $"direct {vd:R}, boolean {vb:R}");
            t.True(declined <= 1, $"{declined} tools by boolean");
        });

        t.Test("Rim chamfer ending on a round hole that breaks into it: cut back to the hole's side, not ending square", () =>
        {
            // the drafted hole's rim (the revolved tool), a smaller hole breaking into it: the rim's arc ends on that
            // hole's cylinder at an angle. Square, its ends were left as faces of their own (a step at each end)
            foreach (var direct in new[] { true, false })
            {
                var old = DirectBlend.Enabled;
                DirectBlend.Enabled = direct;
                try
                {
                    var doc = Demos.BlendCases.First(c => c.name == "plate, drafted hole broken into by a round hole, rim chamfered").build();
                    var rim = doc.Features.First(f => f.Name == "Rim");
                    var r = new Regenerator().Rebuild(doc, doc.Features.IndexOf(rim) + 1);
                    var nb = r.State.Bodies[0].Solid.Native;
                    t.True(r.Status[rim.Id].Ok && nb != null && nb.Validate() == null, $"{(direct ? "direct" : "boolean")}: {r.Status[rim.Id].Error}");
                    var ends = nb!.Faces.Select(f => f.Tag.Name).Where(n => n.StartsWith(rim.Id) && (n.EndsWith(".Start") || n.EndsWith(".End"))).ToList();
                    t.True(ends.Count == 0, $"{(direct ? "direct" : "boolean")}: its ends left as faces ({string.Join(", ", ends)})");
                }
                finally { DirectBlend.Enabled = old; }
            }
        });

        t.Test("Holes beside and through a swept chamfer's foot at a rounded corner: traced quickly, exact", () =>
        {
            // a hole through the plate grazing the chamfer's B-spline face near its edge: the trace on the B-spline misses
            // it, the one on the hole's face (with the B-spline's slow distance) found the hole casting's curves in
            // 20 s each, and past the patch's edge curves that are not there (1-3 s here)
            var doc = Demos.BlendCases.First(c => c.name == "casting foot, drafted, chamfered 6 x 3").build();
            var body = new Regenerator().Rebuild(doc, doc.Features.Count).State.Bodies[0].Solid;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var r in new[] { 26.0, 27, 28, 29, 30 })
            {
                Csg.ClearCache();
                var cut = Csg.Subtract(body, Builders.Cylinder(new Vec3(40 + r * 0.7071, -20 - r * 0.7071, -1), Vec3.UnitZ, 3, 40, "H"));
                t.True(cut.Native != null && cut.Native.Validate() == null, $"r {r}: exact and valid");
                // (clear of the chamfer: the plate's 20 only)
                var dv = KernelTests.Vex(body) - KernelTests.Vex(cut);
                t.True(Math.Abs(dv - Math.PI * 9 * 20) < 1e-6 * dv, $"r {r}: removed {dv:R}");
            }
            t.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms for five holes");
            // through the chamfer: more than the plate's
            Csg.ClearCache();
            var through = Csg.Subtract(body, Builders.Cylinder(new Vec3(40 + 22 * 0.7071, -20 - 22 * 0.7071, -1), Vec3.UnitZ, 3, 40, "H"));
            t.True(through.Native != null && through.Native.Validate() == null, "through: exact and valid");
            t.True(KernelTests.Vex(body) - KernelTests.Vex(through) > Math.PI * 9 * 20 + 1, "through: the chamfer's material too");
        });

        t.Test("Chamfer on an open arc whose ends meet a slot's walls at an angle: cut back to the walls, not ending square", () =>
        {
            // a drafted hole's top rim (plate top / cone: the swept tool), a slot across it: the rim's arc ends on the
            // walls at 11.5° and 53° to its square end. Square, the chamfer stopped short of one wall at each end (a
            // step left) and leant past the other; extended and cut back, it reaches both. The removed volume is the
            // whole rim's less the part in the slot: the chamfer's triangle (corner r 10, legs 1.5 along the top and
            // down the wall, drafted 5°) at each radius over the slot's arc (y 2..8)
            double Removed(bool slot)
            {
                var doc = Demos.BlendCases.First(c => c.name == "plate, drafted hole across a slot, rim chamfered").build();
                if (!slot) doc.Features.First(f => f.Name == "Slot").Suppressed = true;
                var rim = doc.Features.First(f => f.Name == "Rim");
                var r0 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(rim));
                var r1 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(rim) + 1);
                t.True(r1.Status[rim.Id].Ok, r1.Status[rim.Id].Error ?? "");
                t.True(r1.State.Bodies[0].Solid.Native?.Validate() == null, "valid");
                return KernelTests.Vex(r0.State.Bodies[0].Solid) - KernelTests.Vex(r1.State.Bodies[0].Solid);
            }
            var dr = 5 * Math.PI / 180;
            (double r, double z) E = (10, 12), A = (11.5, 12), B = (10 - 1.5 * Math.Sin(dr), 12 - 1.5 * Math.Cos(dr));
            static double Side((double r, double z) p, (double r, double z) q, double r, double z) => (q.r - p.r) * (z - p.z) - (q.z - p.z) * (r - p.r);
            double full = 0, inSlot = 0;
            const int N = 600;
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double r = 9.8 + 1.8 * (i + 0.5) / N, z = 10.4 + 1.6 * (j + 0.5) / N;
                    double s1 = Side(E, A, r, z), s2 = Side(A, B, r, z), s3 = Side(B, E, r, z);
                    if ((s1 < 0 || s2 < 0 || s3 < 0) && (s1 > 0 || s2 > 0 || s3 > 0)) continue;
                    full += 2 * Math.PI * r; inSlot += r * (Math.Asin(8 / r) - Math.Asin(2 / r));
                }
            var expected = 1 - inSlot / full;
            var ratio = Removed(true) / Removed(false);
            t.True(Math.Abs(ratio - expected) < 5e-4, $"the slotted rim's chamfer {ratio:R} of the whole rim's, expected {expected:R}");
        });

        t.Test("Fillet on a swept chamfer's edge, picked on a straight side or round a corner: the same loop", () =>
        {
            // the chamfer's pieces share one face name; the edge is found nearest the hint, and its tangent chain is
            // the whole loop round the boss either way
            var dvs = new List<double>();
            foreach (var hint in new[] { new Vec3(0, -46, 20), new Vec3(40 + 26 * 0.7071, -20 - 26 * 0.7071, 20) })
            {
                var doc = Demos.BlendCases.First(c => c.name == "casting foot, drafted, chamfered").build();
                var ch = doc.Features.Last();
                var r0 = new Regenerator().Rebuild(doc, doc.Features.Count);
                var plate = doc.Features.First(f => f.Name == "Base" && f is ExtrudeFeature).Id;
                var fil = new FilletFeature { Name = "Chamfer edge", Radius = "1", Edges = { new EdgeRef { A = plate + ".End", B = ch.Id + ".1.S.F", Hint = hint } } };
                doc.AddFeature(fil);
                var r = new Regenerator().Rebuild(doc, doc.Features.Count);
                t.True(r.Status[fil.Id].Ok, r.Status[fil.Id].Error ?? "");
                t.True(r.State.Bodies[0].Solid.Native?.Validate() == null, "valid");
                dvs.Add(KernelTests.Vex(r.State.Bodies[0].Solid) - KernelTests.Vex(r0.State.Bodies[0].Solid));
            }
            t.True(dvs[0] > 0 && Math.Abs(dvs[0] - dvs[1]) < 1e-4 * dvs[0], $"added {dvs[0]:R} and {dvs[1]:R}");
        });

        t.Test("Prism with leaning ends (a blend's tool on the faces its ends meet): as extruded past them and cut back", () =>
        {
            // a half disc (a line and an arc: lines and an ellipse on each end) between two leaning planes, against the
            // same extruded long and cut back by booleans (what the tools did before)
            var loop = new ProfileLoop();
            loop.Verts.Add(new ProfileVertex(new Vec2(-1, 0), "a"));
            loop.AddArc(Vec2.Zero, 1, 0, Math.PI, true, "r");
            var frame = new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
            var p0 = Plane.FromPointNormal(Vec3.Zero, new Vec3(0.3, 0.2, 1).Normalized);
            var p1 = Plane.FromPointNormal(new Vec3(0, 0, 5), new Vec3(-0.2, 0.4, -1).Normalized);
            var nb = Kernel.Native.NBuild.ExtrudeBetween(new[] { new ProfileRegion(loop) }, frame, p0, 0, p1, 5, "T");
            t.True(nb != null && nb.Validate() == null, "built and valid");
            Solid Cut(Solid s0, Plane pl)
            {
                var x = pl.Normal.AnyPerpendicular(); var y = Vec3.Cross(pl.Normal, x);
                var rect = ProfileLoop.Polygon(new[] { new Vec2(-20, -20), new Vec2(20, -20), new Vec2(20, 20), new Vec2(-20, 20) }, "c");
                return Csg.Subtract(s0, Builders.Extrude(new[] { new ProfileRegion(rect) }, new Frame(pl.Project(Vec3.Zero), x, y, pl.Normal), -20, 0, "c"));
            }
            var cut = Cut(Cut(Builders.Extrude(new[] { new ProfileRegion(loop) }, frame, -3, 8, "T"), p0), p1);
            double v = KernelTests.Vex(Kernel.Native.NTess.ToSolid(nb!)), e = KernelTests.Vex(cut);
            t.True(Math.Abs(v - e) < 1e-9 * e, $"volume {v:R}, cut back {e:R}");
            t.True(nb!.Edges.Count(x => x.Curve is Kernel.Native.NEllipse) == 2, "an ellipse on each end");
        });

        t.Test("Hex socket (socket cap screw): rim and floor blends mitred at the corners; inside corners and rim in one feature", () =>
        {
            // the sample screw's socket: a hexagon, apothem 2.5, 3 deep. A blend round a polygon's edges, mitred at its
            // corners, is its cross-section along the sides plus, at each corner, twice tan(half the turn) times the
            // cross-section's first moment about the wall: added outside the hole (rim), taken off inside (floor)
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var path = Path.Combine(dir!, "samples", "demos", "socket-screw.sprt");
            double r = 0.5, P = 6 * 2 * 2.5 * Math.Tan(Math.PI / 6), corners = 6 * Math.Tan(Math.PI / 6);
            // per unit length: the fillet's (the square less the quarter disc) and the chamfer's triangle; their moments
            double af = r * r * (1 - Math.PI / 4), mf = r * r * r * (5.0 / 6 - Math.PI / 4), ac = r * r / 2, mc = r * r * r / 6;
            double Changed(Func<Kernel.Native.NEdge, bool> which, Func<List<EdgeRef>, Feature[]> feats)
            {
                var doc = CadDocument.Load(path);
                var sock = doc.Features.First(f => f.Name == "Socket" && f is ExtrudeFeature);
                var r0 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(sock) + 1);
                var nb = r0.State.Bodies[0].Solid.Native!;
                nb.Link();
                var refs = nb.Edges.Where(e => e.Uses.Count == 2 && e.Uses.Any(u => u.Face.Tag.Name.StartsWith(sock.Id + ".")) && which(e))
                    .Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Curve.Eval(0.5 * (e.T0 + e.T1)) }).ToList();
                var fs = feats(refs);
                for (int i = 0; i < fs.Length; i++) { fs[i].Name = $"Blend {i}"; doc.Features.Insert(doc.Features.IndexOf(sock) + 1 + i, fs[i]); }
                var r1 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(fs[^1]) + 1);
                foreach (var f in fs) t.True(r1.Status[f.Id].Ok, $"{f.Name}: {r1.Status[f.Id].Error}");
                t.True(r1.State.Bodies[0].Solid.Native?.Validate() == null, "valid");
                return KernelTests.Vex(r1.State.Bodies[0].Solid) - KernelTests.Vex(r0.State.Bodies[0].Solid);
            }
            var sockId = "F4";
            bool Vert(Kernel.Native.NEdge e) => e.Uses.All(u => u.Face.Tag.Name.StartsWith(sockId + ".S"));
            bool Floor(Kernel.Native.NEdge e) => e.Uses.Any(u => u.Face.Tag.Name == sockId + ".End");
            bool Rim(Kernel.Native.NEdge e) => !Vert(e) && !Floor(e);
            Feature Fil(List<EdgeRef> es) => new FilletFeature { Radius = "0.5", Edges = es };
            Feature Cha(List<EdgeRef> es) => new ChamferFeature { Distance = "0.5", Edges = es };
            void Near(string what, double v, double e) => t.True(Math.Abs(v - e) < 1e-6 + 1e-5 * Math.Abs(e), $"{what}: {v:R}, expected {e:R}");
            // the rim (convex, round the hole: outside it) used to stop short of each corner, leaving it unblended
            Near("rim chamfer", Changed(Rim, es => new[] { Cha(es) }), -(ac * P + 2 * corners * mc));
            Near("rim fillet", Changed(Rim, es => new[] { Fil(es) }), -(af * P + 2 * corners * mf));
            Near("floor fillet", Changed(Floor, es => new[] { Fil(es) }), af * P - 2 * corners * mf);
            // the inside corners (concave) and the rim (convex) in one feature: as the corners first, then the rim
            // running on round them, in two (at once their tools collided at the rim's corners)
            var two = Changed(e => Vert(e) || Rim(e), es => new[] { Fil(es.Where(x => x.A.StartsWith(sockId + ".S") && x.B.StartsWith(sockId + ".S")).ToList()), Fil(es.Where(x => !(x.A.StartsWith(sockId + ".S") && x.B.StartsWith(sockId + ".S"))).ToList()) });
            Near("corners and rim filleted at once", Changed(e => Vert(e) || Rim(e), es => new[] { Fil(es) }), two);
            // chamfered: the corners' triangles (120° apart) along the depth 3; the rim round the twelve-sided outline
            // they leave (sides 2.887 - 1 and 0.866, turns of 30°)
            double cornerTri = 0.5 * r * r * Math.Sin(2 * Math.PI / 3) * 3 * 6, p12 = 6 * (2.5 * 2 * Math.Tan(Math.PI / 6) - 1) + 6 * 2 * r * Math.Sin(Math.PI / 3);
            Near("corners and rim chamfered at once", Changed(e => Vert(e) || Rim(e), es => new[] { Cha(es) }), cornerTri - (ac * p12 + 2 * 12 * Math.Tan(Math.PI / 12) * mc));
            // the floor R1 after the corners rounded R0.5: the ball does not fit in them; said once, with the edges
            {
                var doc = CadDocument.Load(path);
                var sock = doc.Features.First(f => f.Name == "Socket" && f is ExtrudeFeature);
                var nb0 = new Regenerator().Rebuild(doc, doc.Features.IndexOf(sock) + 1).State.Bodies[0].Solid.Native!;
                nb0.Link();
                List<EdgeRef> Refs(Func<Kernel.Native.NEdge, bool> w) => nb0.Edges.Where(e => e.Uses.Count == 2 && e.Uses.Any(u => u.Face.Tag.Name.StartsWith(sockId + ".")) && w(e))
                    .Select(e => new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Hint = e.Curve.Eval(0.5 * (e.T0 + e.T1)) }).ToList();
                var rounds = new FilletFeature { Name = "Corners", Radius = "0.5", Edges = Refs(Vert) };
                var floor = new FilletFeature { Name = "Floor", Radius = "1", Edges = Refs(Floor).Take(1).ToList() };
                doc.Features.Insert(doc.Features.IndexOf(sock) + 1, rounds);
                doc.Features.Insert(doc.Features.IndexOf(rounds) + 1, floor);
                var rr = new Regenerator().Rebuild(doc, doc.Features.IndexOf(floor) + 1);
                var err = rr.Status[floor.Id].Error ?? "";
                t.True(!rr.Status[floor.Id].Ok && err.Contains("R1 does not fit inside the R0.5 round it runs along") && err.IndexOf("does not fit") == err.LastIndexOf("does not fit"), err);
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

        t.Test("Mirror of a fillet whose pieces go one at a time: the copy filleted the same, the body exact (not faceted)", () =>
        {
            // the rib's fillets into the plate and the wall meet where the rib meets both; their tools, joined to be
            // copied, made the whole body faceted (on a worker thread, the faceted fallback was not refused)
            var doc = Demos.BlendCases.First(c => c.name == "rib against a wall, filleted and mirrored").build();
            int Index(string name) => doc.Features.FindIndex(f => f.Name == name);
            var reg = new Regenerator();
            double Vol(int n) { var r = reg.Rebuild(doc, n); return KernelTests.Vex(r.State.Bodies[0].Solid); }
            var beforeRib = Vol(Index("Rib"));
            var filleted = Vol(Index("Rib fillets") + 1);
            var res = reg.Rebuild(doc);
            foreach (var f in doc.Features) t.True(res.Status[f.Id].Ok && res.Status[f.Id].Warnings.Count == 0, $"{f.Name}: {res.Status[f.Id].Error} {string.Join("; ", res.Status[f.Id].Warnings)}");
            var nb = res.State.Bodies[0].Solid.Native;
            t.True(nb != null && nb.Validate() == null, $"exact and closed: {nb?.Validate()}");
            var mirrored = KernelTests.Vex(res.State.Bodies[0].Solid);
            t.True(Math.Abs(mirrored - beforeRib - 2 * (filleted - beforeRib)) < 1e-6 * mirrored, $"the copy adds {mirrored - filleted:R}, the rib and its fillets {filleted - beforeRib:R}");
        });

        t.Test("Mirror with a seed suppressed: the others mirrored, said; with its seed failed: the seed's error", () =>
        {
            // the mirror used to be suppressed with its seed (it uses it), so the rib was silently not mirrored
            var doc = Demos.BlendCases.First(c => c.name == "rib against a wall, filleted and mirrored").build();
            var fil = doc.Features.First(f => f.Name == "Rib fillets");
            var mirror = doc.Features.First(f => f.Name == "Rib, other end");
            var reg = new Regenerator();
            double Vol(ModelState st) => KernelTests.Vex(st.Bodies[0].Solid);
            var beforeRib = Vol(reg.Rebuild(doc, doc.Features.FindIndex(f => f.Name == "Rib")).State);
            var rib = Vol(reg.Rebuild(doc, doc.Features.IndexOf(fil)).State);
            fil.Suppressed = true;
            var res = reg.Rebuild(doc);
            var st = res.Status[mirror.Id];
            t.True(!st.Suppressed && st.Ok, $"built: suppressed {st.Suppressed} by {st.SuppressedBy}, {st.Error}");
            t.True(st.Warnings.Any(w => w.Contains("Rib fillets") && w.Contains("suppressed")), string.Join("; ", st.Warnings));
            t.True(Math.Abs(Vol(res.State) - beforeRib - 2 * (rib - beforeRib)) < 1e-6 * rib, $"the rib mirrored: {Vol(res.State) - rib:R} added, the rib {rib - beforeRib:R}");
            // all its seeds suppressed: suppressed itself, as before
            doc.Features.First(f => f.Name == "Rib").Suppressed = true;
            t.True(reg.Rebuild(doc).Status[mirror.Id].Suppressed, "suppressed with all its seeds");
            doc.Features.First(f => f.Name == "Rib").Suppressed = false;
            // the seed failing: its error, not that it cannot be patterned
            fil.Suppressed = false;
            ((FilletFeature)fil).Radius = "NoSuchVariable";
            var err = reg.Rebuild(doc).Status[mirror.Id].Error ?? "";
            t.True(err.StartsWith("Rib fillets failed: ", StringComparison.Ordinal), err);
        });

        t.Test("Pattern of a filleted rib too close to the next: the overlapping fillets said", () =>
        {
            // the copies of the seed's tool are not solved against each other: R3 fillets either side of 6 thick ribs
            // 10 apart overlap in the 4 gap (8 apart, the next rib's foot is on the seed's fillet: copied, the same); 16
            // apart (the blend case) nothing is said
            foreach (var (spacing, overlap) in new[] { ("10", true), ("16", false), ("8", true) })
            {
                var doc = Demos.PatternedRibs(spacing);
                var p = doc.Features.First(f => f.Name == "Ribs");
                var st = new Regenerator().Rebuild(doc).Status[p.Id];
                t.True(st.Ok, st.Error ?? "");
                t.True(st.Warnings.Any(w => w.Contains("overlaps the seed's")) == overlap, $"spacing {spacing}: [{string.Join("; ", st.Warnings)}]");
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

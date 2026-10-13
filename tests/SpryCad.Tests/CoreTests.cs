using System.Diagnostics;
using SpryCad.Core;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

static class CoreTests
{
    /// <summary>Rectangle sketch with corner at origin, fully dimensioned.</summary>
    public static Sketch Rect(double w, double h, string? wExpr = null)
    {
        var sk = new Sketch();
        var p1 = sk.Point(Sketch.OriginId)!;
        var p2 = sk.AddPoint(new Vec2(w, 0)); var p3 = sk.AddPoint(new Vec2(w, h)); var p4 = sk.AddPoint(new Vec2(0, h));
        var l1 = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
        var l2 = sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
        var l3 = sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id });
        var l4 = sk.Add(new SkLine { P1 = p4.Id, P2 = p1.Id });
        sk.AddConstraint(CType.Horizontal, l1.Id); sk.AddConstraint(CType.Horizontal, l3.Id);
        sk.AddConstraint(CType.Vertical, l2.Id); sk.AddConstraint(CType.Vertical, l4.Id);
        var d1 = sk.AddConstraint(CType.Distance, l1.Id); d1.Expr = wExpr ?? w.ToString(System.Globalization.CultureInfo.InvariantCulture); d1.Value = w;
        var d2 = sk.AddConstraint(CType.Distance, l2.Id); d2.Expr = h.ToString(System.Globalization.CultureInfo.InvariantCulture); d2.Value = h;
        return sk;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Rebuild cancelled during a feature (a pattern of fillets) stops with a cancellation and leaves nothing behind in the cache", () =>
        {
            var root = AppContext.BaseDirectory;
            while (!Directory.Exists(Path.Combine(root, "samples"))) root = Path.GetDirectoryName(root)!;
            var file = Path.Combine(root, "samples", "spoked-handwheel.sprt");
            // the control: a second rebuild on a regenerator that was never cancelled (the sample caches of states taken
            // from its cache differ from a fresh rebuild's, so compare like with like)
            var control = new Regenerator();
            var docC = CadDocument.Load(file);
            control.Rebuild(docC);
            var reference = Bench.Fingerprints(control.Rebuild(docC), geometryOnly: true);
            var regen = new Regenerator();
            var doc = CadDocument.Load(file);
            // cancelled once the rebuild is under way (inside the grips' or spokes' booleans and fillets)
            using var cts = new CancellationTokenSource();
            bool cancelled = false;
            var sw = Stopwatch.StartNew();
            try
            {
                regen.Rebuild(doc, null, cts.Token, p => { if (p.Index >= 4) cts.Cancel(); });
            }
            catch (OperationCanceledException) { cancelled = true; }
            t.True(cancelled, "the rebuild stops with a cancellation, not an error");
            // the same regenerator (its cache) builds the part as a fresh one does
            var again = regen.Rebuild(doc);
            t.True(again.Status.Values.All(s => s.Error == null), "no feature failed: " + string.Join("; ", again.Status.Values.Where(s => s.Error != null).Select(s => s.Error)));
            t.True(Bench.Fingerprints(again, geometryOnly: true).SequenceEqual(reference), "the part after a cancelled rebuild is the part");
        });

        t.Test("Equations: waiting for the ones they use gives the values and errors of evaluating in passes, without the exceptions", () =>
        {
            // the plain passes, as they were: every pending equation tried each pass until a pass changes nothing
            static (Dictionary<string, double>, Dictionary<string, string>) Passes(List<Equation> eqs)
            {
                var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var errors = new Dictionary<string, string>();
                var pending = new List<Equation>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var twice = new Dictionary<string, string>();
                foreach (var e in eqs) if (names.Add(e.Name.Trim())) pending.Add(e); else twice[e.Name] = $"{e.Name.Trim()} is set twice (by a design table or another equation)";
                while (pending.Count > 0)
                {
                    var next = new List<Equation>();
                    foreach (var e in pending)
                    {
                        if (Expr.TryEval(e.Expr, n => values.TryGetValue(n, out var v) ? v : null, out var val, out var err)) values[e.Name.Trim()] = val;
                        else { next.Add(e); errors[e.Name] = err ?? "error"; }
                    }
                    foreach (var e in pending.Except(next)) errors.Remove(e.Name);
                    if (next.Count == pending.Count) break;
                    pending = next;
                }
                foreach (var (n, msg) in twice) errors[n] = msg;
                return (values, errors);
            }
            // equations listed after the ones they use, a chain of them: all evaluate
            {
                var doc = new CadDocument();
                for (int i = 0; i < 6; i++) doc.Equations.Add(new Equation { Name = $"c{i}", Expr = i == 5 ? "7" : $"c{i + 1} + 1" });
                var (v, e) = Regenerator.EvaluateEquations(doc);
                t.True(e.Count == 0 && v["c0"] == 12, $"reverse chain: c0 = {v.GetValueOrDefault("c0")}, errors {string.Join("; ", e)}");
            }
            var rnd = new Random(4);
            var templates = new[] { "{0} + 1", "{0} * {1}", "max({0}, {1}) / 2", "sqrt({0} * {0} + 4)", "{0} - unknown1", "7", "{1} + 0.5", "-{0}" };
            for (int trial = 0; trial < 300; trial++)
            {
                int n = rnd.Next(1, 25);
                var eqs = new List<Equation>();
                for (int i = 0; i < n; i++)
                {
                    string Ref() => rnd.Next(10) == 0 ? "ghost" : $"v{rnd.Next(n)}";
                    var expr = string.Format(templates[rnd.Next(templates.Length)], Ref(), Ref());
                    if (rnd.Next(15) == 0) expr = "";
                    if (rnd.Next(20) == 0) expr = "3 +";
                    eqs.Add(new Equation { Name = rnd.Next(30) == 0 && i > 0 ? $"V{rnd.Next(i)}" : $"v{i}", Expr = expr });
                }
                var doc = new CadDocument();
                doc.Equations.AddRange(eqs);
                var (va, ea) = Regenerator.EvaluateEquations(doc);
                var (vb, eb) = Passes(eqs);
                t.True(va.Count == vb.Count && va.All(kv => vb.TryGetValue(kv.Key, out var w) && BitConverter.DoubleToInt64Bits(w) == BitConverter.DoubleToInt64Bits(kv.Value)), $"trial {trial}: values");
                t.True(ea.Count == eb.Count && ea.All(kv => eb.TryGetValue(kv.Key, out var w) && w == kv.Value), $"trial {trial}: errors {string.Join("; ", ea)} vs {string.Join("; ", eb)}");
            }
        });

        t.Test("Expressions", () =>
        {
            t.Near(Expr.Eval("2 + 3 * 4"), 14, 1e-12);
            t.Near(Expr.Eval("(2 + 3) * 4 ^ 2"), 80, 1e-12);
            t.Near(Expr.Eval("sin(30) * 2"), 1, 1e-12);
            t.Near(Expr.Eval("1in + 1mm"), 26.4, 1e-12);
            t.Near(Expr.Eval("W / 2", n => n == "W" ? 50 : null), 25, 1e-12);
            t.Near(Expr.Eval("-2^2"), -4, 1e-12);
        });

        t.Test("Solver: rectangle fully defined", () =>
        {
            var sk = Rect(80, 40);
            // perturb the corners (w, h) and (0, h): the points after the origin's (w, 0)
            long p3 = sk.Points[2].Id, p4 = sk.Points[3].Id;
            sk.Point(p3)!.X = 50; sk.Point(p4)!.Y = 70;
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "converged");
            t.Eq(r.Status, SketchStatus.FullyDefined);
            t.Near(sk.Point(p3)!.X, 80, 1e-6); t.Near(sk.Point(p3)!.Y, 40, 1e-6); t.Near(sk.Point(p4)!.X, 0, 1e-6);
        });

        t.Test("Solver: under defined + drag", () =>
        {
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(10, 1));
            var l = sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            sk.AddConstraint(CType.Horizontal, l.Id);
            var r = SketchSolver.Solve(sk);
            t.Eq(r.Status, SketchStatus.UnderDefined);
            t.Near(sk.Point(a.Id)!.Y, sk.Point(b.Id)!.Y, 1e-9);
            SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [b.Id] = new Vec2(20, 5) });
            t.Near(sk.Point(b.Id)!.X, 20, 1e-6);
            t.Near(sk.Point(a.Id)!.Y, 5, 1e-6);
        });

        t.Test("Solver: circle tangent to line, radius", () =>
        {
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(-20, 0)); var b = sk.AddPoint(new Vec2(20, 0));
            var l = sk.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            var c = sk.AddPoint(new Vec2(0, 7));
            var ci = sk.Add(new SkCircle { Center = c.Id, R = 3 });
            var tg = sk.AddConstraint(CType.Tangent, l.Id, ci.Id); SketchSolver.CaptureSign(sk, tg);
            var rad = sk.AddConstraint(CType.Radius, ci.Id); rad.Value = 5;
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "converged");
            var lineY = sk.P(a.Id).Y;
            t.Near(Math.Abs(sk.P(c.Id).Y - lineY), 5, 1e-6);
        });

        t.Test("Solver: redundant constraint detection", () =>
        {
            var sk = Rect(80, 40);
            SketchSolver.Solve(sk);
            var extra = new SkConstraint { Id = sk.NewId(), Type = CType.Distance, Refs = new[] { sk.Entities[2].Id }, Value = 80 };
            t.True(SketchSolver.WouldOverDefine(sk, extra), "length of opposite side is redundant");
        });

        t.Test("Dimension labels: placed by the user or laid out (and files from before the flag)", () =>
        {
            var sk = Rect(80, 40);
            var d = sk.Constraints.First(c => c.IsDimension);
            t.True(!d.HasPlacedLabel, "a dimension made in code (no offset, no flag) is laid out");
            d.LabelX = 5; d.LabelY = -3;
            t.True(d.HasPlacedLabel, "a file from before the flag: a label off its default spot was placed by hand");
            d.LabelPlaced = false;
            t.True(!d.HasPlacedLabel, "Auto Arrange clears it, whatever the offset");
            d.LabelPlaced = true;
            var back = Serialization.Clone(sk).Constraints.First(c => c.Id == d.Id);
            t.True(back.HasPlacedLabel && back.LabelX == 5, "the flag and the position are saved");
            var json = System.Text.Json.JsonSerializer.Serialize(Rect(80, 40), Serialization.Options);
            t.True(!json.Contains("labelPlaced"), "no flag written for laid out labels of older sketches (unchanged files)");
        });

        t.Test("Solver: straight slot keeps its sides parallel to the centerline", () =>
        {
            // built as the Straight Slot tool does
            var sk = new Sketch();
            var ca = sk.AddPoint(new Vec2(0, 0)); var cb = sk.AddPoint(new Vec2(30, 0));
            var axis = sk.Add(new SkLine { P1 = ca.Id, P2 = cb.Id, Construction = true });
            var p1 = sk.AddPoint(new Vec2(0, 5)); var p2 = sk.AddPoint(new Vec2(30, 5));
            var p3 = sk.AddPoint(new Vec2(30, -5)); var p4 = sk.AddPoint(new Vec2(0, -5));
            var top = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
            var bot = sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id });
            var arcB = sk.Add(new SkArc { Center = cb.Id, Start = p3.Id, End = p2.Id });
            var arcA = sk.Add(new SkArc { Center = ca.Id, Start = p1.Id, End = p4.Id });
            foreach (var (l, arc) in new[] { (top, arcB), (bot, arcA), (top, arcA), (bot, arcB) })
                SketchSolver.CaptureSign(sk, sk.AddConstraint(CType.Tangent, l.Id, arc.Id));
            var par = new SkConstraint { Id = sk.NewId(), Type = CType.Parallel, Refs = new[] { top.Id, axis.Id } };
            t.True(!SketchSolver.WouldOverDefine(sk, par), "parallel side is not implied by the tangencies alone");
            var c = sk.AddConstraint(CType.Parallel, top.Id, axis.Id);
            var second = new SkConstraint { Id = sk.NewId(), Type = CType.Parallel, Refs = new[] { bot.Id, axis.Id } };
            t.True(SketchSolver.WouldOverDefine(sk, second), "the other side then follows");

            // drag one corner of a side: the slot must stay a slot (parallel sides, equal ends)
            var r = SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [p2.Id] = new Vec2(32, 9) });
            t.True(r.Converged, "converged");
            var u = (sk.P(cb.Id) - sk.P(ca.Id)).Normalized;
            t.Near(Vec2.Cross(u, (sk.P(p2.Id) - sk.P(p1.Id)).Normalized), 0, 1e-6);
            t.Near(Vec2.Cross(u, (sk.P(p4.Id) - sk.P(p3.Id)).Normalized), 0, 1e-6);
            t.Near(Vec2.Distance(sk.P(ca.Id), sk.P(p1.Id)), Vec2.Distance(sk.P(cb.Id), sk.P(p2.Id)), 1e-6);
            _ = c;
        });

        t.Test("Profile: rectangle with circle hole", () =>
        {
            var sk = Rect(80, 40);
            var c = sk.AddPoint(new Vec2(40, 20));
            sk.Add(new SkCircle { Center = c.Id, R = 10 });
            SketchSolver.Solve(sk);
            var regs = ProfileExtractor.Extract(sk, out _);
            t.Eq(regs.Count, 1);
            t.Eq(regs[0].Holes.Count, 1);
        });

        t.Test("Sketch fillet keeps the virtual sharp, follows the dimensions, and deleting it restores the corner", () =>
        {
            double Volume(Sketch sk)
            {
                var doc = new CadDocument();
                var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
                var r = new Regenerator().Rebuild(doc);
                t.True(r.Status.Values.All(x => x.Ok), "rebuild: " + string.Join("; ", r.Status.Values.Select(x => x.Error)));
                return r.State.Bodies.Single().Mesh.Volume;
            }
            var sk = Rect(80, 40);
            var corner = sk.Points.First(p => Math.Abs(p.X - 80) < 1e-9 && Math.Abs(p.Y) < 1e-9).Id;
            var widthDim = sk.Constraints.First(c => c.IsDimension && Math.Abs(c.Value - 80) < 1e-9);
            t.True(SketchOps.Fillet(sk, corner, 10) == null, "fillet made");
            var res = SketchSolver.Solve(sk);
            t.True(res.Status == SketchStatus.FullyDefined, "still fully defined, not over defined: " + res.Status);
            var f = sk.Fillets!.Single();
            var arc = (SkArc)sk.Entity(f.Arc)!;
            t.Near(sk.ArcRadius(arc), 10, 1e-6);
            // the originals are construction, still ending at the corner (dimensions to them unchanged)
            t.True(sk.Entity(f.LineA)!.Construction && sk.Entity(f.LineB)!.Construction, "originals as reference geometry");
            t.True(sk.Point(corner) != null && sk.EntitiesUsing(corner).Count() == 2, "the corner (virtual sharp) is kept");
            var tangents = new[] { arc.Start, arc.End }.Select(sk.P).OrderBy(p => p.X).ToList();
            t.Near(tangents[0].X, 70, 1e-6); t.Near(tangents[0].Y, 0, 1e-6); t.Near(tangents[1].X, 80, 1e-6); t.Near(tangents[1].Y, 10, 1e-6);
            // a 10 mm round off one corner: 20 deep
            t.Near(Volume(sk.Clone()), (80 * 40 - (100 - Math.PI * 100 / 4)) * 20, 10); // the mesh's arc is within ~0.01%
            // the width changes: the fillet follows the corner
            widthDim.Value = 100; widthDim.Expr = "100";
            SketchSolver.Solve(sk);
            var moved = new[] { arc.Start, arc.End }.Select(sk.P).OrderBy(p => p.X).ToList();
            t.Near(moved[0].X, 90, 1e-6); t.Near(moved[1].X, 100, 1e-6); t.Near(sk.ArcRadius(arc), 10, 1e-6);
            // deleting the arc: the lines are solid again, the corner sharp, nothing left of the fillet
            sk.DeleteEntity(f.Arc);
            t.True(sk.Fillets == null, "fillet record gone");
            t.Eq(sk.Entities.Count, 4);
            t.True(sk.Entities.All(e => !e.Construction), "lines solid again");
            t.True(SketchSolver.Solve(sk).Status == SketchStatus.FullyDefined, "fully defined again");
            t.Near(Volume(sk.Clone()), 100 * 40 * 20, 1);
            // a radius that does not fit is refused
            t.True(SketchOps.Fillet(sk, corner, 45) != null, "too big refused");

            // all four corners, one after the other (each next to a filleted one), with a construction diagonal at
            // two of them (as a center rectangle has): every corner takes its fillet
            var all = Rect(80, 40);
            var corners = all.Points.Where(p => p.Id != Sketch.OriginId || true).Select(p => p.Id).Where(id => all.EntitiesUsing(id).Count() == 2).ToList();
            var c0 = all.Points.First(p => p.X == 0 && p.Y == 0).Id; var c2 = all.Points.First(p => p.X == 80 && p.Y == 40).Id;
            all.Add(new SkLine { P1 = c0, P2 = c2, Construction = true });
            t.Eq(corners.Count, 4);
            foreach (var cid in corners) t.True(SketchOps.Fillet(all, cid, 5) is null, "corner filleted: " + SketchOps.Fillet(all.Clone(), cid, 5));
            t.Eq(all.Fillets!.Count, 4);
            var resAll = SketchSolver.Solve(all);
            t.True(resAll.Status == SketchStatus.FullyDefined, "four fillets: fully defined, not over: " + resAll.Status);
            // one solid line per side, between the two tangent points
            t.Eq(all.Entities.Count(e => e is SkLine { Construction: false }), 4);
            t.Near(Volume(all.Clone()), (80 * 40 - 4 * (25 - Math.PI * 25 / 4)) * 20, 10);
            // removing one fillet gives back that corner only; removing all gives the rectangle
            var first = all.Fillets[0];
            all.DeleteEntity(first.Arc);
            t.Eq(all.Fillets!.Count, 3);
            t.True(SketchSolver.Solve(all).Status == SketchStatus.FullyDefined, "three fillets: fully defined");
            t.Near(Volume(all.Clone()), (80 * 40 - 3 * (25 - Math.PI * 25 / 4)) * 20, 10);
            foreach (var fr in all.Fillets.ToList()) all.DeleteEntity(fr.Arc);
            t.True(all.Fillets == null, "all fillets gone");
            t.Eq(all.Entities.Count(e => e is SkLine { Construction: false }), 4);
            t.Eq(all.Entities.Count(e => e is SkLine { Construction: true }), 1); // just the diagonal
            t.Near(Volume(all.Clone()), 80 * 40 * 20, 1);
        });

        t.Test("Sketch: an angle dimension to the sketch's horizontal axis fully defines a line from the origin", () =>
        {
            var sk = new Sketch();
            var end = sk.AddPoint(new Vec2(3, 9));
            var line = sk.Add(new SkLine { P1 = Sketch.OriginId, P2 = end.Id, Construction = true });
            var axis = SketchReferences.Axis(sk, "H", 2);
            t.True(ReferenceEquals(SketchReferences.Axis(sk, "H", 5), axis), "reused");
            var len = sk.AddConstraint(CType.Distance, line.Id); len.Value = 1;
            var ang = sk.AddConstraint(CType.Angle, axis.Id, line.Id); ang.Value = 70;
            SketchSolver.CaptureSign(sk, len); SketchSolver.CaptureSign(sk, ang);
            var res = SketchSolver.Solve(sk);
            t.True(res.Converged, "solves");
            t.Eq(res.Dof, 0);
            t.Near(Vec2.Distance(sk.P(end.Id), new Vec2(Math.Cos(70 * Math.PI / 180), Math.Sin(70 * Math.PI / 180))), 0, 1e-9);
            // in a part: the rebuild leaves the axis alone (no model edge behind it), and it is saved
            var doc = new CadDocument();
            var sf = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[sf.Id].Ok && r.Status[sf.Id].Warnings.Count == 0, r.Status[sf.Id].Error ?? string.Join("; ", r.Status[sf.Id].Warnings));
            var copy = (SketchFeature)sf.Clone();
            t.Eq(copy.Sketch.Entities.OfType<SkLine>().Count(e => e.External?.Axis == "H"), 1);
        });

        t.Test("Shell thicker than the rounds before it: the inside from the part before them, rounded again (or sharp)", () =>
        {
            // a block (40 x 30 x 20) with its top edges rounded R1, shelled 2 open at the bottom: offset as it is, the
            // rounds would need a radius of -1; the inside is the block's, sharp
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(40, 30) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var lines = s1.Sketch.Entities;
            string Side(int i) => e1.Id + ".S." + ProfileExtractor.FaceName(lines[i]);
            var round = doc.AddFeature(new FilletFeature { Radius = "1", Edges = { new EdgeRef { A = e1.Id + ".End", B = Side(0), Hint = new Vec3(20, 0, 20) } } });
            var shell = doc.AddFeature(new ShellFeature { Thickness = "2", Faces = { new FaceRef { Name = e1.Id + ".Start", Hint = new Vec3(20, 15, 0), Normal = -Vec3.UnitZ } } });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.True(r.Status[shell.Id].Warnings.Any(w => w.Contains("before its last fillets")), "made from the part before the rounds");
            var b = r.State.Bodies.Single();
            t.Eq(b.Solid.Native!.Validate(), null);
            t.Eq(b.Solid.Topology.OpenHalfEdges, 0);
            var rounded = KernelTests.Vex(new Regenerator().Rebuild(doc, doc.Features.IndexOf(shell)).State.Bodies.Single().Solid);
            t.Near(KernelTests.Vex(b.Solid), rounded - 36 * 26 * 18, 1e-3 * rounded);
            // thinner than the rounds: the inside rounded R1 - 0.5 (made as it always was)
            shell.Thickness = "0.5";
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[shell.Id].Ok && r.Status[shell.Id].Warnings.Count == 0, r.Status[shell.Id].Error ?? string.Join("; ", r.Status[shell.Id].Warnings));
        });

        t.Test("Shell a pyramid on a rectangle: its apex's four faces offset meet in two corners, not one", () =>
        {
            // (a square base's would meet at one point; on a 40 x 20 base the four offset planes do not, and the inner
            // apex is a short ridge between the two long sides' planes)
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(40, 20) });
            var p = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "30" });
            var sk = new Sketch(); sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(20, 10)).Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = p.Id }, Sketch = sk });
            var loft = doc.AddFeature(new LoftFeature { SectionSketchIds = { s1.Id, s2.Id } });
            var shell = doc.AddFeature(new ShellFeature { Thickness = "2", Faces = { new FaceRef { Name = loft.Id + ".Start", Hint = new Vec3(20, 10, 0), Normal = -Vec3.UnitZ } } });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            var b = r.State.Bodies.Single();
            t.Eq(b.Solid.Native!.Validate(), null);
            t.Eq(b.Solid.Topology.OpenHalfEdges, 0);
            // the walls: less than the pyramid, more than its faces' area times the wall less the corners' overlap
            double outer = 40 * 20 * 30 / 3.0, v = KernelTests.Vex(b.Solid);
            t.True(v > 0.15 * outer && v < 0.6 * outer, $"walls {v} of {outer}");
        });

        t.Test("Fillet along a round boss ending on a slanted face", () =>
        {
            // a block with a slanted end (Front: x 0..40 at the bottom, 0..30 at the top y 10), a bar along X (radius 4,
            // its axis on the top face) running out through the slant; the concave edges where the bar meets the top
            // end on the slant at 45°
            var doc = new CadDocument();
            var sk = new Sketch();
            var pts = new[] { new Vec2(0, 0), new Vec2(40, 0), new Vec2(30, 10), new Vec2(0, 10) }.Select(p => sk.AddPoint(p).Id).ToArray();
            for (int i = 0; i < 4; i++) sk.Add(new SkLine { P1 = pts[i], P2 = pts[(i + 1) % 4] });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var block = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var right = Resolver.StdPlane("Right");
            var bs = new Sketch(); var bc = bs.AddPoint(right.ToLocal(new Vec3(0, 10, 10))); var circ = bs.Add(new SkCircle { Center = bc.Id, R = 4 });
            var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Right, Sketch = bs });
            var bar = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, Depth = "36", Reverse = right.Normal.X < 0 });
            string round = bar.Id + ".S." + ProfileExtractor.FaceName(circ), top = block.Id + ".S." + ProfileExtractor.FaceName(sk.Entities[2]);
            var fi = doc.AddFeature(new FilletFeature
            {
                Radius = "1",
                Edges = { new EdgeRef { A = round, B = top, Hint = new Vec3(15, 10, 14) }, new EdgeRef { A = round, B = top, Hint = new Vec3(15, 10, 6) } },
            });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            var b = r.State.Bodies.Single();
            t.Eq(b.Solid.Native!.Validate(), null);
            t.Eq(b.Solid.Topology.OpenHalfEdges, 0);
            // (two concave R1 fillets about 30 long: less than (1 - π/4) per unit length each, as the bar curves away)
            var before = new Regenerator().Rebuild(doc, doc.Features.IndexOf(fi)).State.Bodies.Single();
            var added = KernelTests.Vex(b.Solid) - KernelTests.Vex(before.Solid);
            t.True(added > 5 && added < 2 * 30 * (1 - Math.PI / 4), $"added {added}");
        });

        t.Test("Loft from a model face to a sketch", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var p = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "60" });
            var sk = new Sketch(); var c = sk.AddPoint(new Vec2(50, 30)); sk.Add(new SkCircle { Center = c.Id, R = 15 });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = p.Id }, Sketch = sk });
            var loft = doc.AddFeature(new LoftFeature
            {
                SectionSketchIds = { "", s2.Id },
                SectionFaces = { new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ }, null },
            });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            var b = r.State.Bodies.Single();
            t.Eq(b.Solid.Native!.Validate(), null);
            var v = KernelTests.Vex(b.Solid) - 100 * 60 * 20;
            t.True(v > 40 * Math.PI * 225 && v < 40 * 6000, $"the loft between its ends: {v}");
            t.Eq(string.Join(",", loft.Consumes), s2.Id);
            // saved and read back
            var back = CadDocument.FromJson(doc.ToJson());
            t.Eq(((LoftFeature)back.Find(loft.Id)!).SectionFaces[0]!.Name, e1.Id + ".End");
        });

        t.Test("Extrude Up To Surface (a face, a plane, a cylinder) and Up To Body", () =>
        {
            static Sketch CircleAt(Frame fr, Vec3 world, double r)
            {
                var sk = new Sketch(); var c = sk.AddPoint(fr.ToLocal(world));
                sk.Add(new SkCircle { Center = c.Id, R = r });
                return sk;
            }
            // a block (z 0..20); a post from a plane 50 up, back down to the block's top face
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var p50 = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "50" });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = p50.Id }, Sketch = CircleAt(Resolver.StdPlane("Front"), new Vec3(50, 30, 0), 5) });
            var post = doc.AddFeature(new ExtrudeFeature
            {
                SketchId = s2.Id, End = EndCondition.UpToSurface, Reverse = true,
                UpTo = new PlaneRef { Face = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ } },
            });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Near(KernelTests.Vex(r.State.Bodies.Single().Solid), 100 * 60 * 20 + Math.PI * 25 * 30, 0.5);
            // up to a reference plane (15 short of the block: a body of its own)
            var p35 = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "35" }, doc.Features.IndexOf(post));
            post.UpTo = new PlaneRef { Feature = p35.Id };
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[post.Id].Ok, r.Status[post.Id].Error ?? "");
            t.Near(r.State.Bodies.Sum(b => KernelTests.Vex(b.Solid)), 100 * 60 * 20 + Math.PI * 25 * 15, 0.5);

            // a round bar (radius 20 along Z); a peg from y 50 down to its surface, then to the body
            var doc2 = new CadDocument();
            var bar = new Sketch(); var bc = bar.AddPoint(new Vec2(0, 0)); var circ = bar.Add(new SkCircle { Center = bc.Id, R = 20 });
            var b1 = doc2.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = bar });
            var be = doc2.AddFeature(new ExtrudeFeature { SketchId = b1.Id, Depth = "100" });
            var top = Resolver.StdPlane("Top");
            var py = doc2.AddFeature(new PlaneFeature { Base = PlaneRef.Top, Offset = "50" });
            var s3 = doc2.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = py.Id }, Sketch = CircleAt(top, new Vec3(0, 0, 50), 5) });
            var round = new FaceRef { Name = be.Id + ".S." + ProfileExtractor.FaceName(circ), Hint = new Vec3(0, 20, 50) };
            var peg = doc2.AddFeature(new ExtrudeFeature { SketchId = s3.Id, End = EndCondition.UpToSurface, UpTo = new PlaneRef { Face = round }, Reverse = top.Normal.Y > 0 });
            double bar0 = Math.PI * 400 * 100;
            foreach (var end in new[] { EndCondition.UpToSurface, EndCondition.UpToBody })
            {
                peg.End = end;
                r = new Regenerator().Rebuild(doc2);
                t.True(r.Status[peg.Id].Ok, $"{end}: {r.Status[peg.Id].Error}");
                var v = KernelTests.Vex(r.State.Bodies.Single().Solid) - bar0;
                t.True(v > Math.PI * 25 * 30 && v < Math.PI * 25 * 30.7, $"{end}: the peg {v}");
            }
            // off the bar's side: part of it misses
            s3.Sketch = CircleAt(top, new Vec3(18, 0, 50), 5);
            r = new Regenerator().Rebuild(doc2);
            t.True(!r.Status[peg.Id].Ok && r.Status[peg.Id].Error!.Contains("misses"), r.Status[peg.Id].Error ?? "built");
        });

        t.Test("Sketch fillet where an arc meets a line, and where two arcs meet; deleting it restores the arc", () =>
        {
            bool Extrudes(Sketch sk)
            {
                var doc = new CadDocument();
                var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "5" });
                var r = new Regenerator().Rebuild(doc);
                return r.Status.Values.All(x => x.Ok) && r.State.Bodies.Single().Solid.Native?.Validate() == null;
            }
            // a block whose top is an arc about (20, 10) from (40, 30) round to (0, 30)
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(40, 0)); var c = sk.AddPoint(new Vec2(40, 30)); var d = sk.AddPoint(new Vec2(0, 30));
            var o = sk.AddPoint(new Vec2(20, 10));
            sk.Add(new SkLine { P1 = a.Id, P2 = b.Id }); sk.Add(new SkLine { P1 = b.Id, P2 = c.Id }); sk.Add(new SkLine { P1 = d.Id, P2 = a.Id });
            var top = sk.Add(new SkArc { Center = o.Id, Start = c.Id, End = d.Id });
            t.True(SketchOps.Fillet(sk, c.Id, 3) is null, "line to arc filleted: " + SketchOps.Fillet(sk.Clone(), c.Id, 3));
            var f = sk.Fillets!.Single();
            var fa = (SkArc)sk.Entity(f.Arc)!;
            t.Near(sk.ArcRadius(fa), 3, 1e-9);
            // inside the big arc, touching it: its centre 28.28 - 3 from the big one's
            t.Near(Vec2.Distance(sk.P(fa.Center), sk.P(o.Id)), Math.Sqrt(800) - 3, 1e-9);
            t.True(sk.Entity(top.Id)!.Construction, "the original arc as construction");
            var res = SketchSolver.Solve(sk);
            t.True(res.Status is not (SketchStatus.OverDefined or SketchStatus.Failed), $"solves: {res.Status}");
            t.True(Extrudes(sk.Clone()), "the profile extrudes");
            sk.DeleteEntity(f.Arc);
            t.True(sk.Fillets == null && !sk.Entity(top.Id)!.Construction, "the arc solid again");
            t.Eq(sk.Entities.Count, 4);

            // a lens of two arcs (about (0, -10) and (0, 10)) meeting at (-10, 0) and (10, 0)
            var lens = new Sketch();
            var p = lens.AddPoint(new Vec2(-10, 0)); var q = lens.AddPoint(new Vec2(10, 0));
            var c1 = lens.AddPoint(new Vec2(0, -10)); var c2 = lens.AddPoint(new Vec2(0, 10));
            lens.Add(new SkArc { Center = c1.Id, Start = q.Id, End = p.Id });
            lens.Add(new SkArc { Center = c2.Id, Start = p.Id, End = q.Id });
            t.True(SketchOps.Fillet(lens, q.Id, 1) is null, "arc to arc filleted: " + SketchOps.Fillet(lens.Clone(), q.Id, 1));
            var lf = (SkArc)lens.Entity(lens.Fillets!.Single().Arc)!;
            foreach (var cc in new[] { c1, c2 }) t.Near(Vec2.Distance(lens.P(lf.Center), lens.P(cc.Id)), Math.Sqrt(200) - 1, 1e-9);
            t.True(SketchSolver.Solve(lens).Status is not (SketchStatus.OverDefined or SketchStatus.Failed), "the lens solves");
            t.True(Extrudes(lens.Clone()), "the lens extrudes");
        });

        t.Test("Sketch offset: lines and arcs, outward and inward, follow the originals and the distance", () =>
        {
            double Ring(Sketch sk)
            {
                var doc = new CadDocument();
                var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk.Clone() });
                doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "10" });
                var r = new Regenerator().Rebuild(doc);
                t.True(r.Status.Values.All(x => x.Ok), "rebuild: " + string.Join("; ", r.Status.Values.Select(x => x.Error)));
                return r.State.Bodies.Sum(b => b.Mesh.Volume);
            }

            // a rectangle outward by 5: a 90 × 50 outline around the 80 × 40; fully defined; follows the width
            var sk = Rect(80, 40);
            var (off, err) = SketchOps.Offset(sk, SketchOps.Chain(sk, new[] { sk.Entities[0].Id }), 5);
            t.True(off != null, "offset: " + err);
            var res = SketchSolver.Solve(sk);
            t.True(res.Status == SketchStatus.FullyDefined, "rectangle offset: fully defined, not over: " + res.Status);
            t.Near(Ring(sk), (90 * 50 - 80 * 40) * 10, 1);
            t.True(sk.Points.Any(p => Math.Abs(p.X + 5) < 1e-6 && Math.Abs(p.Y + 5) < 1e-6), "outer corner at (-5, -5)");
            var width = sk.Constraints.First(c => c.Type == CType.Distance && Math.Abs(c.Value - 80) < 1e-9);
            width.Value = 100; width.Expr = "100";
            SketchSolver.Solve(sk);
            t.True(sk.Points.Any(p => Math.Abs(p.X - 105) < 1e-6 && Math.Abs(p.Y + 5) < 1e-6), "the offset follows the width");
            off!.Value = 8; off.Expr = "8";
            SketchSolver.Solve(sk);
            t.True(sk.Points.Any(p => Math.Abs(p.X - 108) < 1e-6 && Math.Abs(p.Y + 8) < 1e-6), "the offset distance drives it");
            t.Near(SketchSolver.Measure(sk, off), 8, 1e-9);
            // reversed: inside
            SketchOps.ReverseOffset(sk, off);
            t.True(SketchSolver.Solve(sk).Status == SketchStatus.FullyDefined, "reversed: fully defined");
            t.True(sk.Points.Any(p => Math.Abs(p.X - 8) < 1e-6 && Math.Abs(p.Y - 8) < 1e-6), "inner corner at (8, 8)");
            t.Near(Ring(sk), (100 * 40 - 84 * 24) * 10, 1);

            // a filleted rectangle (lines and tangent arcs) outward by 5: arcs concentric, radius 15
            var rr = Rect(80, 40);
            foreach (var cid in rr.Points.Select(p => p.Id).Where(id => rr.EntitiesUsing(id).Count() == 2).ToList()) SketchOps.Fillet(rr, cid, 10);
            var arcs = rr.Entities.OfType<SkArc>().Select(a => a.Id).ToList();
            var (off2, err2) = SketchOps.Offset(rr, SketchOps.Chain(rr, new[] { arcs[0] }), 5);
            t.True(off2 != null, "offset of a rounded rectangle: " + err2);
            var res2 = SketchSolver.Solve(rr);
            t.True(res2.Status == SketchStatus.FullyDefined, "rounded offset: fully defined, not over: " + res2.Status);
            t.True(rr.Entities.OfType<SkArc>().Count(a => !arcs.Contains(a.Id) && Math.Abs(rr.ArcRadius(a) - 15) < 1e-6) == 4, "four arcs of radius 15");
            double Rounded(double w, double h, double r) => w * h - (4 - Math.PI) * r * r;
            t.Near(Ring(rr), (Rounded(90, 50, 15) - Rounded(80, 40, 10)) * 10, 5);

            // an open chain (an L) to the left: square ends; and a circle outward
            var open = new Sketch();
            var a0 = open.AddPoint(new Vec2(0, 0), fix: true); var a1 = open.AddPoint(new Vec2(50, 0), fix: true); var a2 = open.AddPoint(new Vec2(50, 30), fix: true);
            var la = open.Add(new SkLine { P1 = a0.Id, P2 = a1.Id }); open.Add(new SkLine { P1 = a1.Id, P2 = a2.Id });
            var (off3, err3) = SketchOps.Offset(open, SketchOps.Chain(open, new[] { la.Id }), 4);
            t.True(off3 != null, "open chain: " + err3);
            t.True(SketchSolver.Solve(open).Status == SketchStatus.FullyDefined, "open offset fully defined");
            // left of travel from (0,0) toward (50,0) is up: the copy runs (0,4) → (46,4) → (46,30)
            foreach (var want in new[] { new Vec2(0, 4), new Vec2(46, 4), new Vec2(46, 30) })
                t.True(open.Points.Any(p => Vec2.Distance(p.P, want) < 1e-6), $"open offset point at {want.X}, {want.Y}");
            var circ = new Sketch();
            var cc = circ.Add(new SkCircle { Center = Sketch.OriginId, R = 10 });
            circ.AddConstraint(CType.Radius, cc.Id).Value = 10;
            var (off4, _) = SketchOps.Offset(circ, new[] { cc.Id }, 3);
            SketchSolver.Solve(circ);
            t.True(circ.Entities.OfType<SkCircle>().Any(c => c.Id != cc.Id && Math.Abs(c.R - 13) < 1e-9), "circle offset outward to R 13");
        });

        t.Test("Sketch mirror: a half profile about a centerline closes, and the copy follows the original", () =>
        {
            var sk = new Sketch();
            var o = Sketch.OriginId;
            var top = sk.AddPoint(new Vec2(0, 50));
            var axis = sk.Add(new SkLine { P1 = o, P2 = top.Id, Construction = true });
            sk.AddConstraint(CType.Vertical, axis.Id);
            var p1 = sk.AddPoint(new Vec2(30, 0)); var p2 = sk.AddPoint(new Vec2(30, 20)); var p3 = sk.AddPoint(new Vec2(0, 40));
            var l1 = sk.Add(new SkLine { P1 = o, P2 = p1.Id });
            var l2 = sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id });
            var l3 = sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
            var hole = sk.AddPoint(new Vec2(10, 15));
            var circle = sk.Add(new SkCircle { Center = hole.Id, R = 3 });
            var made = SketchOps.Mirror(sk, new[] { l1.Id, l2.Id, l3.Id, circle.Id }, axis.Id);
            t.Eq(made.Count, 4);
            var res = SketchSolver.Solve(sk);
            t.True(res.Status != SketchStatus.OverDefined && res.Status != SketchStatus.Failed, "solves: " + res.Status);
            // the points on the axis are shared: the outline closes (a 900 mm² half twice, less two holes)
            var doc = new CadDocument();
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk.Clone() });
            doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "10" });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status.Values.All(x => x.Ok), "rebuild: " + string.Join("; ", r.Status.Values.Select(x => x.Error)));
            t.Near(r.State.Bodies.Single().Mesh.Volume, (2 * 900 - 2 * Math.PI * 9) * 10, 5);
            // moving an original point moves its mirror image; the copied circle keeps the original's radius
            SketchSolver.Solve(sk, new Dictionary<long, Vec2> { [p2.Id] = new Vec2(35, 25) });
            var image = sk.Points.Single(p => Math.Abs(p.X + sk.P(p2.Id).X) < 1e-6 && Math.Abs(p.Y - sk.P(p2.Id).Y) < 1e-6 && p.Id != p2.Id);
            t.True(image != null, "mirror image followed");
            var copyCircle = sk.Entities.OfType<SkCircle>().Single(c => c.Id != circle.Id);
            t.Near(copyCircle.R, circle.R, 1e-9); t.Near(sk.P(copyCircle.Center).X, -sk.P(hole.Id).X, 1e-6);
        });

        t.Test("Sketch relations to model geometry are associative (midpoint of edge, model center)", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0];
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ };
            var frame = Resolver.ResolvePlane(new PlaneRef { Face = top }, r.State);

            // what the sketcher does when a point is placed on the midpoint of the model's back top edge
            var backEdge = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.Y - 60) < 1e-9 && Math.Abs(e.End.Y - 60) < 1e-9
                && Math.Abs(e.Start.Z - 20) < 1e-9 && Math.Abs(e.End.Z - 20) < 1e-9);
            var sk = new Sketch();
            var refLine = (SkLine)SketchReferences.Create(sk, frame, backEdge, body.Id, false, backEdge.Midpoint)!;
            var c = sk.AddPoint(frame.ToLocal(backEdge.Midpoint) + new Vec2(0.3, -0.2));
            sk.AddConstraint(CType.Midpoint, c.Id, refLine.Id);
            sk.Add(new SkCircle { Center = c.Id, R = 5 });
            sk.RemoveUnusedReferences();
            t.True(sk.Entities.Contains(refLine), "reference kept by its relation");
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk });
            r = regen.Rebuild(doc);
            t.True(r.Status[s2.Id].Ok, r.Status[s2.Id].Error ?? "");
            var res = r.State.Sketches[s2.Id];
            t.Near(Vec3.Distance(res.Frame.ToWorld(res.Sketch.P(c.Id)), new Vec3(50, 60, 20)), 0, 1e-6);

            // the model changes: the relation follows it
            doc.Equations[0].Expr = "120";
            r = regen.Rebuild(doc);
            res = r.State.Sketches[s2.Id];
            t.Near(Vec3.Distance(res.Frame.ToWorld(res.Sketch.P(c.Id)), new Vec3(60, 60, 20)), 0, 1e-6);

            // a reference whose point is shared by sketch geometry (snapped to a model center/vertex) is kept
            var sk2 = new Sketch();
            var vtx = (SkPointEntity)SketchReferences.Create(sk2, frame, backEdge, body.Id, true, backEdge.Start)!;
            var q = sk2.AddPoint(new Vec2(10, 10));
            sk2.Add(new SkLine { P1 = vtx.P, P2 = q.Id });
            sk2.RemoveUnusedReferences();
            t.True(sk2.Entities.Contains(vtx), "shared-point reference kept");
        });

        t.Test("Delete: dependents found through any reference", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var doc = CadDocument.FromJson(File.ReadAllText(Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "shelled-bracket.sprt")));
            string Id(string name) => doc.Features.First(f => f.Name == name).Id;
            var deps = doc.DependentsOf(new[] { Id("Hole1") }).Select(f => f.Name).ToList();
            t.True(deps.Contains("LPattern1"), "pattern of the hole: " + string.Join(",", deps));
            t.True(!deps.Contains("Hole2") && !deps.Contains("Fillet1"), "unrelated features: " + string.Join(",", deps));
            t.Eq(doc.DependentsOf(new[] { Id("Hole2") }).Count, 0);
            var all = doc.DependentsOf(new[] { Id("Boss-Extrude1") }).Select(f => f.Name).ToList();
            t.True(all.Contains("Fillet1") && all.Contains("Shell1") && all.Contains("Hole2"), "base feature: " + string.Join(",", all));
        });

        t.Test("Fillets running into neighbouring geometry: overflow, clear errors, never a hang", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var models = Path.Combine(dir!, "tests", "SpryCad.Tests", "models");
            var json = File.ReadAllText(Path.Combine(models, "shelled-bracket.sprt"));
            // inside the shell: a 4 mm fillet around the boss foot runs past the 0.5 mm gap to the wall
            FilletFeature AddFillet(CadDocument d, bool overflow) => (FilletFeature)d.AddFeature(new FilletFeature
            { Radius = "4", Overflow = overflow, Edges = { new EdgeRef { A = LegacyIds.Name(d.Id!, "F9.I.F2.End"), B = LegacyIds.Name(d.Id!, "F9.I.F8.1.F7.0.R.CB"), Hint = new Vec3(70, 2.5, 18) } } });

            // overflow (default): trimmed by the wall, adds material, stays exact and closed
            var doc = CadDocument.FromJson(json);
            var before = KernelTests.Vex(new Regenerator().Rebuild(doc).State.Bodies[0].Solid);
            var fil = AddFillet(doc, true);
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[fil.Id].Ok, "overflow fillet: " + r.Status[fil.Id].Error);
            var body = r.State.Bodies[0].Solid;
            t.True(body.Native != null && body.Topology.OpenHalfEdges == 0, "native, closed");
            t.True(KernelTests.Vex(body) > before + 1, "material added");

            // without overflow: a clear, fast error naming the edge (highlighted)
            doc = CadDocument.FromJson(json);
            fil = AddFillet(doc, false);
            var sw = Stopwatch.StartNew();
            r = new Regenerator().Rebuild(doc);
            t.True(!r.Status[fil.Id].Ok && r.Status[fil.Id].Error!.Contains("does not fit"), "fit error: " + r.Status[fil.Id].Error);
            t.True(r.Status[fil.Id].ProblemEdges.Count > 0, "problem edges reported");
            t.True(sw.ElapsedMilliseconds < 3000, $"fast failure ({sw.ElapsedMilliseconds} ms)");

            // the user's case: all inner floor edges (walls, corner, tube, both bosses) in one 2 mm fillet
            doc = CadDocument.FromJson(File.ReadAllText(Path.Combine(models, "shelled-bracket-inner-fillet.sprt")));
            r = new Regenerator().Rebuild(doc);
            var last = doc.Features[^1];
            t.True(r.Status[last.Id].Ok, "inner floor fillet: " + r.Status[last.Id].Error);
            body = r.State.Bodies[0].Solid;
            t.True(body.Native != null && body.Topology.OpenHalfEdges == 0, "inner floor fillet native, closed");
            t.True(ExactBrep.Build(body).Warnings.Count == 0, "exact B-rep");

            // and if a bad case reaches the boolean anyway, the fallback is time-boxed
            var (oldFit, oldBudget) = (NativeBlend.CheckFit, Csg.FallbackSeconds);
            NativeBlend.CheckFit = false; Csg.FallbackSeconds = 1;
            try
            {
                doc = CadDocument.FromJson(json);
                fil = AddFillet(doc, false);
                sw.Restart();
                r = new Regenerator().Rebuild(doc);
                t.True(sw.ElapsedMilliseconds < 8000, $"bounded failure ({sw.ElapsedMilliseconds} ms)");
            }
            finally { NativeBlend.CheckFit = oldFit; Csg.FallbackSeconds = oldBudget; }
        });

        t.Test("Demo models rebuild, also with changed parameters, and export exact STEP", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "sprycad-tests", "demos");
            t.Eq(Demos.Verify(dir, volumes: false), 0);
        });

        t.Test("Reference planes: offset, through a vertex or sketch point, at an angle", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            // side faces of the box: right (x = W) and top (y = 60) lines of the rectangle
            var s1Lines = s1.Sketch.Entities;
            string right = e1.Id + ".S." + ProfileExtractor.FaceName(s1Lines[1]), top = e1.Id + ".S." + ProfileExtractor.FaceName(s1Lines[2]);
            // parallel to Right through the box corner (W, 60, 20): the vertex is the end of an edge between named faces
            var corner = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.ThroughPoint, Base = PlaneRef.Right,
                Point = new PointRef { Vertex = new EdgeRef { A = e1.Id + ".End", B = top, Hint = new Vec3(100, 60, 20) } },
            });
            // through a sketch point
            var sk = new Sketch(); var sp = sk.AddPoint(new Vec2(30, 40));
            sk.Add(new SkPointEntity { P = sp.Id });
            var ps = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var thru = doc.AddFeature(new PlaneFeature { Mode = PlaneMode.ThroughPoint, Base = PlaneRef.Top, Point = new PointRef { Sketch = ps.Id, SketchPoint = sp.Id } });
            // Top plane tilted 30 degrees about X, then moved through the sketch point
            var tilt = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.Angle, Base = PlaneRef.Top, Axis = new DirRef { Axis = "X" }, Angle = "30",
                Point = new PointRef { Sketch = ps.Id, SketchPoint = sp.Id },
            });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            var pc = r.State.Planes[corner.Id];
            t.Near(Vec3.Dot(new Vec3(100, 60, 20) - pc.Origin, pc.Normal), 0, 1e-9);
            t.Near(Math.Abs(Vec3.Dot(pc.Normal, Vec3.UnitX)), 1, 1e-12);
            var pt = r.State.Planes[thru.Id];
            t.Near(Vec3.Dot(new Vec3(30, 40, 0) - pt.Origin, pt.Normal), 0, 1e-9);
            var pa = r.State.Planes[tilt.Id];
            t.Near(Vec3.Distance(pa.Normal, new Vec3(0, Math.Cos(Math.PI / 6), Math.Sin(Math.PI / 6))), 0, 1e-12);
            t.Near(Vec3.Dot(new Vec3(30, 40, 0) - pa.Origin, pa.Normal), 0, 1e-9);
            // normal to a sketch line at its end (the end is picked by the reference point)
            var ls = new Sketch(); var l0 = ls.AddPoint(new Vec2(10, 10)); var l1 = ls.AddPoint(new Vec2(40, 50));
            var line = ls.Add(new SkLine { P1 = l0.Id, P2 = l1.Id, Construction = true });
            var lsk = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = ls });
            var normal = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.NormalToCurve, Curve = new CurveRef { Sketch = lsk.Id, SketchEntity = line.Id },
                Point = new PointRef { Sketch = lsk.Id, SketchPoint = l1.Id },
            });
            r = regen.Rebuild(doc);
            t.True(r.Status[normal.Id].Ok, r.Status[normal.Id].Error ?? "");
            var pn = r.State.Planes[normal.Id];
            t.Near(Vec3.Distance(pn.Origin, new Vec3(40, 50, 0)), 0, 1e-9);
            t.Near(Vec3.Distance(pn.Normal, new Vec3(30, 40, 0).Normalized), 0, 1e-12);
            // the Front plane turned 90° about that sketch line: it holds the line, square to Front
            var about = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.Angle, Base = PlaneRef.Front, Axis = new DirRef { Curve = new CurveRef { Sketch = lsk.Id, SketchEntity = line.Id } }, Angle = "90",
            });
            r = regen.Rebuild(doc);
            t.True(r.Status[about.Id].Ok, r.Status[about.Id].Error ?? "");
            var pl = r.State.Planes[about.Id];
            t.Near(Vec3.Dot(new Vec3(10, 10, 0) - pl.Origin, pl.Normal), 0, 1e-9);
            t.Near(Vec3.Dot(new Vec3(40, 50, 0) - pl.Origin, pl.Normal), 0, 1e-9);
            t.Near(Vec3.Dot(pl.Normal, Vec3.UnitZ), 0, 1e-12);
            // normal to a model edge (the box's vertical edge at x=W, y=60) at its top end
            var en = doc.AddFeature(new PlaneFeature
            {
                Mode = PlaneMode.NormalToCurve, Curve = new CurveRef { Edge = new EdgeRef { A = right, B = top, Hint = new Vec3(100, 60, 10) } },
                Point = new PointRef { Vertex = new EdgeRef { A = e1.Id + ".End", B = top, Hint = new Vec3(100, 60, 20) } },
            });
            r = regen.Rebuild(doc);
            t.True(r.Status[en.Id].Ok, r.Status[en.Id].Error ?? "");
            var pe = r.State.Planes[en.Id];
            t.Near(Vec3.Distance(pe.Origin, new Vec3(100, 60, 20)), 0, 1e-9);
            t.Near(Math.Abs(Vec3.Dot(pe.Normal, Vec3.UnitZ)), 1, 1e-12);
            // the vertex plane follows the model
            doc.Equations[0].Expr = "130";
            r = regen.Rebuild(doc);
            pc = r.State.Planes[corner.Id];
            t.Near(Vec3.Dot(new Vec3(130, 60, 20) - pc.Origin, pc.Normal), 0, 1e-9);
        });

        t.Test("Start-up warm-up model rebuilds cleanly", () =>
        {
            // the warm-up part must exercise the real paths without errors (else it warms nothing)
            var r = Warmup.Run();
            foreach (var st in r.Status.Values) t.True(st.Ok && st.Warnings.Count == 0, $"warm-up feature: {st.Error} {string.Join(";", st.Warnings)}");
            t.True(r.State.Bodies.Count == 1 && r.State.Bodies[0].Solid.Native != null, "native warm-up body");
        });

        t.Test("Performance budget: model rebuilds", () =>
        {
            // budgets are ~3-4x the measured warm rebuild time: they catch regressions, not machine noise
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            var cases = new (string file, double budgetMs)[]
            {
                (Path.Combine(dir!, "samples", "bracket.sprt"), 120),
                (Path.Combine(dir!, "tests", "SpryCad.Tests", "models", "shelled-bracket.sprt"), 400),
            };
            foreach (var (file, budget) in cases)
            {
                var json = CadDocument.Load(file).ToJson(); // samples are containers
                double best = double.MaxValue;
                for (int i = 0; i < 6; i++)
                {
                    var doc = CadDocument.FromJson(json);
                    Csg.ClearCache(); // measure the computation, not boolean cache hits
                    var sw = Stopwatch.StartNew();
                    var r = new Regenerator().Rebuild(doc);
                    var ms = sw.Elapsed.TotalMilliseconds;
                    if (i >= 2) best = Math.Min(best, ms);
                    t.True(r.Status.Values.All(st => st.Ok), "rebuild ok");
                }
                Console.WriteLine($"      {Path.GetFileName(file)}: {best:0.0} ms (budget {budget} ms)");
                t.True(best < budget, $"{Path.GetFileName(file)} rebuild {best:0.0} ms exceeds budget {budget} ms");
            }
        });

        t.Test("Samples rebuild watertight", () =>
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
            t.True(dir != null, "samples folder");
            // (files named Failed* are reproductions of failures, tested on their own)
            foreach (var file in Directory.GetFiles(Path.Combine(dir!, "samples"), "*.sprt").Where(f => !Path.GetFileName(f).StartsWith("Failed")))
            {
                var doc = CadDocument.Load(file);
                if (Environment.GetEnvironmentVariable("FC_TRACE") != null) SpryCad.Kernel.Native.NBoolean.Trace = m => Console.WriteLine("T " + m);
                var r = new Regenerator().Rebuild(doc);
                SpryCad.Kernel.Native.NBoolean.Trace = null;
                for (int i = 0; i < r.States.Count; i++)
                    foreach (var b in r.States[i].Bodies)
                        Console.WriteLine($"        after {doc.Features[i].Name}: open={b.Solid.Topology.OpenHalfEdges} native={b.Solid.Native != null}");
                foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{Path.GetFileName(file)} {f.Name}: {r.Status[f.Id].Error}");
                foreach (var b in r.State.Bodies)
                {
                    Console.WriteLine($"      {Path.GetFileName(file)}: open={b.Solid.Topology.OpenHalfEdges} native={b.Solid.Native != null} vol={b.Solid.Volume():0.###} ({Csg.LastFallbackReason})");
                    t.Eq(b.Solid.Topology.OpenHalfEdges, 0);
                    // the display mesh (also used for mass properties) must enclose the same volume
                    var vMesh = b.Mesh.Volume; var vExact = KernelTests.Vex(b.Solid);
                    t.True(Math.Abs(vMesh - vExact) < 2e-3 * Math.Abs(vExact), $"display mesh volume {vMesh} vs {vExact}");
                }
                foreach (var b in r.State.Bodies)
                {
                    if (Environment.GetEnvironmentVariable("FC_DUMP") != null && b.Solid.Native != null)
                        foreach (var f in b.Solid.Native.Faces)
                        {
                            var bx = f.Bounds();
                            Console.WriteLine($"        {f.Tag.Name} {f.Surface.Kind} R={f.Surface.R:0.###} axis={f.Surface.Axis} o={f.Surface.Origin} sense={f.Sense} box={bx.Min}-{bx.Max} loops={string.Join("/", f.Loops.Select(l => l.Coedges.Count))}");
                        }
                    var brep = ExactBrep.Build(b.Solid);
                    t.True(brep.Warnings.Count == 0, $"{Path.GetFileName(file)} exact: {string.Join("; ", brep.Warnings)}");
                }
                var outDir = Path.Combine(Path.GetTempPath(), "sprycad-tests");
                Directory.CreateDirectory(outDir);
                using (var fs = File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".step")))
                    StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), Path.GetFileNameWithoutExtension(file), 1.0);
            }
        });

        t.Test("Part: extrude, cut on face, fillet, hole, pattern, shell", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            t.True(r.Status.Values.All(s => s.Ok), string.Join(";", r.Status.Values.Select(s => s.Error)));
            t.Near(r.State.Bodies[0].Solid.Volume(), 100 * 60 * 20, 1e-6);

            // sketch on the top (end) face: circle, cut through all
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 20), Normal = Vec3.UnitZ };
            var sk2 = new Sketch();
            var cp = sk2.AddPoint(new Vec2(30, 30));
            sk2.Add(new SkCircle { Center = cp.Id, R = 8 });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk2 });
            var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true });
            r = regen.Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            t.Near(KernelTests.Vex(r.State.Bodies[0].Solid), 100 * 60 * 20 - Math.PI * 64 * 20, 1.5);

            // fillet a vertical edge (both ends at x = 100, y = 60: which way round edges run follows the face names)
            var edge = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 100) < 1e-6 && Math.Abs(e.Start.Y - 60) < 1e-6
                && Math.Abs(e.End.X - 100) < 1e-6 && Math.Abs(e.End.Y - 60) < 1e-6);
            var fil = doc.AddFeature(new FilletFeature { Radius = "5", Edges = { new EdgeRef { A = edge.FaceA, B = edge.FaceB, Hint = edge.Midpoint } } });
            // hole on top face
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(70, 30)); hs.Add(new SkPointEntity { P = hp.Id });
            var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var hole = doc.AddFeature(new HoleFeature { SketchId = s3.Id, Type = HoleType.Counterbore, Diameter = "6.6", ThroughAll = true });
            // pattern the hole along -X
            var lp = doc.AddFeature(new LinearPatternFeature { Seeds = { hole.Id }, Direction = new DirRef { Axis = "Y" }, Spacing = "-20", Count = "2" });
            var sw = Stopwatch.StartNew();
            r = regen.Rebuild(doc);
            var ms1 = sw.ElapsedMilliseconds;
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            for (int i = 0; i < r.States.Count; i++)
                if (r.States[i].Bodies.Count > 0)
                    Console.WriteLine($"      after {doc.Features[i].Name}: open={r.States[i].Bodies[0].Solid.Topology.OpenHalfEdges} faces={r.States[i].Bodies[0].Solid.Faces.Count} warn={string.Join(";", r.Status[doc.Features[i].Id].Warnings)}");
            t.Eq(r.State.Bodies.Count, 1);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // shell from the bottom face
            var sh = doc.AddFeature(new ShellFeature { Thickness = "2", Faces = { new FaceRef { Name = e1.Id + ".Start", Hint = new Vec3(50, 30, 0), Normal = -Vec3.UnitZ } } });
            r = regen.Rebuild(doc);
            t.True(r.Status[sh.Id].Ok, r.Status[sh.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            t.True(r.State.Bodies[0].Solid.Native != null, "shell is native: " + Csg.LastFallbackReason);
            Console.WriteLine($"      full rebuild {ms1} ms, rebuilt {r.Rebuilt} feature(s) incrementally in {r.Milliseconds:0} ms");

            // change equation -> parametric update
            doc.Equations[0].Expr = "120";
            r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name} after W=120: {r.Status[f.Id].Error}");
            t.Near(r.State.Bodies[0].Solid.TightBounds().Size.X, 120, 1e-6);

            // save / load round trip
            var json = doc.ToJson();
            var doc2 = CadDocument.FromJson(json);
            t.Eq(doc2.Features.Count, doc.Features.Count);
            var r2 = new Regenerator().Rebuild(doc2);
            t.Near(r2.State.Bodies[0].Solid.Volume(), r.State.Bodies[0].Solid.Volume(), 1e-6);
            var dir = Path.Combine(Path.GetTempPath(), "sprycad-tests");
            Directory.CreateDirectory(dir);
            doc.Save(Path.Combine(dir, "demo.sprt"));
            using (var fs = File.Create(Path.Combine(dir, "demo.step"))) StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), "demo");
            using (var fs = File.Create(Path.Combine(dir, "demo.stl"))) StlWriter.Write(fs, r.State.Bodies.Select(b => b.Solid));
        });

        t.Test("Revolve feature", () =>
        {
            var doc = new CadDocument();
            var sk = new Sketch();
            var a = sk.AddPoint(new Vec2(0, -10)); var b = sk.AddPoint(new Vec2(0, 30));
            sk.Add(new SkLine { P1 = a.Id, P2 = b.Id, Construction = true });
            var p1 = sk.AddPoint(new Vec2(10, 0)); var p2 = sk.AddPoint(new Vec2(20, 0)); var p3 = sk.AddPoint(new Vec2(20, 20)); var p4 = sk.AddPoint(new Vec2(10, 20));
            sk.Add(new SkLine { P1 = p1.Id, P2 = p2.Id }); sk.Add(new SkLine { P1 = p2.Id, P2 = p3.Id });
            sk.Add(new SkLine { P1 = p3.Id, P2 = p4.Id }); sk.Add(new SkLine { P1 = p4.Id, P2 = p1.Id });
            var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
            var rv = doc.AddFeature(new RevolveFeature { SketchId = s.Id });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[rv.Id].Ok, r.Status[rv.Id].Error ?? "");
            var v = r.State.Bodies[0].Solid.Volume();
            t.Near(v, Math.PI * (400 - 100) * 20, 60);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
        });

        t.Test("Mirror, circular pattern, plane, combine", () =>
        {
            var doc = new CadDocument();
            // base plate 100x60x10
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            // boss: 10x10 square at (10,10) extruded 5 on top face
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 10), Normal = Vec3.UnitZ };
            var sk = Rect(10, 10);
            foreach (var p in sk.Points) if (p.Id != Sketch.OriginId) { p.X += 10; p.Y += 10; }
            // free the rectangle from the origin: rebuild it unattached
            var sq = new Sketch();
            var a = sq.AddPoint(new Vec2(10, 10)); var b = sq.AddPoint(new Vec2(20, 10)); var c = sq.AddPoint(new Vec2(20, 20)); var d = sq.AddPoint(new Vec2(10, 20));
            sq.Add(new SkLine { P1 = a.Id, P2 = b.Id }); sq.Add(new SkLine { P1 = b.Id, P2 = c.Id }); sq.Add(new SkLine { P1 = c.Id, P2 = d.Id }); sq.Add(new SkLine { P1 = d.Id, P2 = a.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sq });
            var boss = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, Depth = "5" });
            // mirror about a reference plane at x = 50 (Right plane offset 50)
            var pl = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Right, Offset = "50" });
            var mir = doc.AddFeature(new MirrorFeature { Seeds = { boss.Id }, Plane = new PlaneRef { Feature = pl.Id } });
            var r = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Near(r.State.Bodies[0].Solid.Volume(), 60000 + 2 * 500, 1e-6);
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // circular pattern of the boss around the Z axis through (50,30): use an axis via a cylinder hole
            var hs = new Sketch(); var hc = hs.AddPoint(new Vec2(50, 30)); hs.Add(new SkCircle { Center = hc.Id, R = 5 });
            var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s3.Id, IsCut = true, End = EndCondition.ThroughAll, Reverse = true });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[cut.Id].Ok, r.Status[cut.Id].Error ?? "");
            var cylName = r.State.Bodies[0].Solid.Faces.First(f => f.Tag.Surface.Kind == SurfaceKind.Cylinder).Tag.Name;
            var cp = doc.AddFeature(new CircularPatternFeature { Seeds = { boss.Id }, Axis = new DirRef { Face = new FaceRef { Name = cylName, Hint = new Vec3(55, 30, 5) } }, Count = "4" });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[cp.Id].Ok, r.Status[cp.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);

            // separate body + combine subtract
            var s4 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(20, 20) });
            var e4 = doc.AddFeature(new ExtrudeFeature { SketchId = s4.Id, Depth = "30", Merge = false });
            r = new Regenerator().Rebuild(doc);
            t.Eq(r.State.Bodies.Count, 2);
            var before = r.State.Bodies[0].Solid.Volume();
            var comb = doc.AddFeature(new CombineFeature { Op = CombineOp.Subtract, MainBody = r.State.Bodies[0].Id, ToolBodies = { e4.Id } });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[comb.Id].Ok, r.Status[comb.Id].Error ?? "");
            t.Eq(r.State.Bodies.Count, 1);
            t.Near(r.State.Bodies[0].Solid.Volume(), before - 20 * 20 * 10 - 500, 1e-6); // tool also removes the boss inside it
        });

        t.Test("Chamfer hole rim via document", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(50, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "12" });
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(25, 12, -25), Normal = Vec3.UnitY };
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(25, 25)); hs.Add(new SkPointEntity { P = hp.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            var h = doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "10", ThroughAll = true });
            var r = new Regenerator().Rebuild(doc);
            t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
            var rim = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.Closed && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End"));
            var ch = doc.AddFeature(new ChamferFeature { Distance = "1", Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } });
            r = new Regenerator().Rebuild(doc);
            t.True(r.Status[ch.Id].Ok, r.Status[ch.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
        });

        // a 100 x 50 x 20 block (x 0..100, y 0..20 up, z -50..0) chamfered on the given top edges
        (double removed, Kernel.Native.NBody body) Chamfered(ChamferFeature ch, Func<TopoEdge, bool> pick)
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(100, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "20" });
            var r = new Regenerator().Rebuild(doc);
            var before = r.State.Bodies[0].Solid.Volume();
            foreach (var e in r.State.Bodies[0].Solid.Topology.Edges.Where(e => e.IsLinear && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End")).Where(pick))
                ch.Edges.Add(new EdgeRef { A = e.FaceA, B = e.FaceB, Hint = e.Midpoint });
            doc.AddFeature(ch);
            r = new Regenerator().Rebuild(doc);
            if (!r.Status[ch.Id].Ok) throw new Exception(r.Status[ch.Id].Error);
            var solid = r.State.Bodies[0].Solid;
            t.Eq(solid.Topology.OpenHalfEdges, 0);
            return (before - solid.Volume(), solid.Native!);
        }
        static bool FrontEdge(TopoEdge e) => Math.Abs(e.Start.Z) < 1e-9 && Math.Abs(e.End.Z) < 1e-9;

        t.Test("Chamfer types: equal, distance-distance, angle-distance (volumes)", () =>
        {
            t.Near(Chamfered(new ChamferFeature { Distance = "2" }, FrontEdge).removed, 0.5 * 2 * 2 * 100, 1e-6);
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4" }, FrontEdge).removed, 0.5 * 2 * 4 * 100, 1e-6);
            // 90 degree edge: the other leg is d * tan(angle)
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "2", Angle = "30" }, FrontEdge).removed,
                0.5 * 2 * 2 * Math.Tan(Math.PI / 6) * 100, 1e-6);
            t.Near(Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "2", Angle = "45" }, FrontEdge).removed, 0.5 * 2 * 2 * 100, 1e-6);
        });

        t.Test("Chamfer flip puts the distances on the other faces", () =>
        {
            // on the top face 3 from the edge: inside the chamfer only when the 4 leg runs along the top
            var probe = new Vec3(50, 19.9, -3);
            var a = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4" }, FrontEdge).body;
            var b = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "2", Distance2 = "4", Flip = true }, FrontEdge).body;
            t.True(NativeBlend.PointInside(a, probe) != NativeBlend.PointInside(b, probe), "flip swaps the legs");
            // legs 4 and 4 tan 60 = 6.9: 5 along the top is inside only when the 6.9 leg runs along the top
            var probe2 = new Vec3(50, 19.9, -5);
            var c = Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "4", Angle = "60" }, FrontEdge).body;
            var d = Chamfered(new ChamferFeature { Type = ChamferType.AngleDistance, Distance = "4", Angle = "60", Flip = true }, FrontEdge).body;
            t.True(NativeBlend.PointInside(c, probe2) != NativeBlend.PointInside(d, probe2), "flip swaps the reference face");
        });

        t.Test("Unequal chamfer around the top keeps its long leg on the same face", () =>
        {
            foreach (var flip in new[] { false, true })
            {
                var (removed, body) = Chamfered(new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "1", Distance2 = "3", Flip = flip }, _ => true);
                // 2 from each edge on the top face: removed on all four sides, or kept on all four
                var probes = new[] { new Vec3(50, 19.95, -2), new Vec3(50, 19.95, -48), new Vec3(2, 19.95, -25), new Vec3(98, 19.95, -25) };
                var inside = probes.Select(p => NativeBlend.PointInside(body, p)).ToList();
                t.True(inside.All(x => x == inside[0]), $"flip={flip}: same leg on every edge ({string.Join(",", inside)})");
                t.True(removed > 1.5 * 290 && removed < 1.5 * 300, $"removed {removed}");
            }
        });

        t.Test("Unequal chamfer on a hole rim", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(50, 50) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "12" });
            var top = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(25, 12, -25), Normal = Vec3.UnitY };
            var hs = new Sketch(); var hp = hs.AddPoint(new Vec2(25, 25)); hs.Add(new SkPointEntity { P = hp.Id });
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
            doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "10", ThroughAll = true });
            var r = new Regenerator().Rebuild(doc);
            var before = r.State.Bodies[0].Solid.Volume();
            var rim = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.Closed && (e.FaceA == e1.Id + ".End" || e.FaceB == e1.Id + ".End"));
            var got = new List<double>();
            foreach (var flip in new[] { false, true, false })
            {
                var d = doc.Clone();
                var ch = d.AddFeature(got.Count == 2 ? new ChamferFeature { Distance = "1", Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } }
                    : new ChamferFeature { Type = ChamferType.DistanceDistance, Distance = "1", Distance2 = "2", Flip = flip, Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } });
                var rr = new Regenerator().Rebuild(d);
                t.True(rr.Status[ch.Id].Ok, rr.Status[ch.Id].Error ?? "");
                got.Add(before - rr.State.Bodies[0].Solid.Volume());
            }
            // Pappus: triangle with legs dr (radial, on the top) and dh (down the bore), centroid at R + dr/3
            double Ring(double dr, double dh) => 2 * Math.PI * (5 + dr / 3) * dr * dh / 2;
            // volumes are measured on the faceted bore, a few percent over the exact rings (the equal chamfer too)
            t.Near(got[2], Ring(1, 1), Ring(1, 1) * 0.08);
            got.RemoveAt(2);
            got.Sort();
            t.Near(got[0], Ring(1, 2), Ring(1, 2) * 0.05);
            t.Near(got[1], Ring(2, 1), Ring(2, 1) * 0.05);
            t.True(got[1] - got[0] > 1, "flip moves the long leg from the bore to the top face");
        });

        t.Test("Document: rounded corner then top fillets (user scenario)", () =>
        {
            var doc = new CadDocument();
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = Rect(80, 60) });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "30" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0].Solid;
            // vertical edge (parallel to Y) at the far corner
            var vert = body.Topology.Edges.First(e => e.IsLinear && Math.Abs((e.End - e.Start).Normalized.Y) > 0.99 && e.Start.X > 79);
            var f1 = doc.AddFeature(new FilletFeature { Radius = "10", Edges = { new EdgeRef { A = vert.FaceA, B = vert.FaceB, Hint = vert.Midpoint } } });
            r = regen.Rebuild(doc);
            t.True(r.Status[f1.Id].Ok, r.Status[f1.Id].Error ?? "");
            var top = e1.Id + ".End";
            var edges = r.State.Bodies[0].Solid.Topology.Edges.Where(e => e.FaceA == top || e.FaceB == top)
                .Select(e => new EdgeRef { A = e.FaceA, B = e.FaceB, Hint = e.Midpoint }).ToList();
            var preTopo = r.State.Bodies[0].Solid.Topology;
            var f2 = doc.AddFeature(new FilletFeature { Radius = "3", Edges = edges });
            r = regen.Rebuild(doc);
            t.True(r.Status[f2.Id].Ok, r.Status[f2.Id].Error ?? "");
            t.Eq(r.State.Bodies[0].Solid.Topology.OpenHalfEdges, 0);
            var full = r.State.Bodies[0].Solid.Volume();
            // tangent propagation: one straight edge next to the corner pulls in the arc and the next line
            // the 70 mm top edge shortened by the corner round
            var one = edges.First(er => preTopo.EdgesBetween(er.A, er.B).Any(te => te.IsLinear && Math.Abs(te.Length - 70) < 1e-6));
            var f3 = (FilletFeature)f2.Clone(); f3.Edges = new() { one };
            var doc2 = doc.Clone(); doc2.Features[^1] = f3;
            var r2 = new Regenerator().Rebuild(doc2);
            t.True(r2.Status[f3.Id].Ok, r2.Status[f3.Id].Error ?? "");
            t.True(r2.State.Bodies[0].Solid.Faces.Any(f => f.Tag.Surface.Kind == SurfaceKind.Torus), "propagated through the rounded corner");
            t.True(r2.State.Bodies[0].Solid.Volume() > full, "fewer edges than the full selection");
            var dir = Path.Combine(Path.GetTempPath(), "sprycad-tests");
            doc.Save(Path.Combine(dir, "corner.sprt"));
        });

        t.Test("Sketch dimensions to model edges are associative", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = Rect(100, 60, "W") });
            var e1 = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            var regen = new Regenerator();
            var r = regen.Rebuild(doc);
            var body = r.State.Bodies[0];
            var topFace = new FaceRef { Name = e1.Id + ".End", Hint = new Vec3(50, 30, 10), Normal = Vec3.UnitZ };
            var frame = Resolver.ResolvePlane(new PlaneRef { Face = topFace }, r.State);
            // right edge of the top face (x = W) and bottom edge (y = 0)
            // (both ends at z = 10: the vertical edge below also starts there when it runs down, as the face names decide)
            var right = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 100) < 1e-9 && Math.Abs(e.End.X - 100) < 1e-9 && Math.Abs(e.Start.Z - 10) < 1e-9 && Math.Abs(e.End.Z - 10) < 1e-9);
            var bottom = body.Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.Y) < 1e-9 && Math.Abs(e.End.Y) < 1e-9 && Math.Abs(e.Start.Z - 10) < 1e-9 && Math.Abs(e.End.Z - 10) < 1e-9);
            var sk = new Sketch();
            var hp = sk.AddPoint(new Vec2(80, 20)); sk.Add(new SkPointEntity { P = hp.Id });
            var rl = (SkLine)SketchReferences.Create(sk, frame, right, body.Id, false, right.Midpoint)!;
            var blE = SketchReferences.Create(sk, frame, bottom, body.Id, false, bottom.Midpoint);
            t.True(blE is SkLine, $"bottom ref: {blE?.GetType().Name ?? "null"} edge {bottom.Start}->{bottom.End} {bottom.FaceA}/{bottom.FaceB} vs right {right.FaceA}/{right.FaceB}");
            var bl = (SkLine)blE!;
            var d1 = sk.AddConstraint(CType.Distance, hp.Id, rl.Id); SketchSolver.CaptureSign(sk, d1); d1.Expr = "15"; d1.Value = 15;
            var d2 = sk.AddConstraint(CType.Distance, hp.Id, bl.Id); SketchSolver.CaptureSign(sk, d2); d2.Expr = "12"; d2.Value = 12;
            var res = SketchSolver.Solve(sk);
            t.Eq(res.Status, SketchStatus.FullyDefined);
            var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = topFace }, Sketch = sk });
            var h = doc.AddFeature(new HoleFeature { SketchId = s2.Id, Diameter = "6" });
            r = regen.Rebuild(doc);
            t.True(r.Status[h.Id].Ok, r.Status[h.Id].Error ?? "");
            Vec3 HoleCenter(RegenResult rr) => rr.State.Bodies[0].Solid.Faces.First(f => f.Tag.Surface.Kind == SurfaceKind.Cylinder).Tag.Surface.Origin;
            t.Near(HoleCenter(r).X, 85, 1e-6); t.Near(HoleCenter(r).Y, 12, 1e-6);
            // the part grows: the hole follows the right edge
            doc.Equations[0].Expr = "130";
            r = regen.Rebuild(doc);
            foreach (var f in doc.Features) t.True(r.Status[f.Id].Ok, $"{f.Name}: {r.Status[f.Id].Error}");
            t.Near(HoleCenter(r).X, 115, 1e-6); t.Near(HoleCenter(r).Y, 12, 1e-6);
            // round trip keeps the references
            var doc2 = CadDocument.FromJson(doc.ToJson());
            t.True(((SketchFeature)doc2.Find(s2.Id)!).Sketch.Entities.Count(e => e.IsReference) == 2, "references saved");
        });

        t.Test("Angle dimension measures the sector it is placed in", () =>
        {
            // like the reported case: top line runs right from the vertex, the diagonal is stored pointing back to it
            var sk = new Sketch();
            var v = sk.AddPoint(new Vec2(0, 10)); v.Fixed = true;
            var a = sk.AddPoint(new Vec2(40, 10)); a.Fixed = true;
            var d = sk.AddPoint(new Vec2(40, -14.6));
            var top = sk.Add(new SkLine { P1 = v.Id, P2 = a.Id });
            var diag = sk.Add(new SkLine { P1 = d.Id, P2 = v.Id });
            var c = new SkConstraint { Id = sk.NewId(), Type = CType.Angle, Refs = new[] { top.Id, diag.Id } };
            c.Flip = SketchSolver.ChooseAngleFlip(sk.P(a.Id) - sk.P(v.Id), sk.P(v.Id) - sk.P(d.Id), new Vec2(15, -3)); // label inside the acute sector
            var measured = SketchSolver.Measure(sk, c);
            t.Near(measured, Math.Atan2(24.6, 40) * 180 / Math.PI, 1e-6); // ~31.6, not ~148.4
            SketchSolver.CaptureSign(sk, c);
            c.Value = 45;
            sk.Constraints.Add(c);
            sk.AddConstraint(CType.Vertical, a.Id, d.Id);
            var r = SketchSolver.Solve(sk);
            t.True(r.Converged, "solved");
            t.Near(SketchSolver.Measure(sk, c), 45, 1e-6);
            t.Near(sk.P(d.Id).Y, 10 - 40, 1e-6); // diagonal now at 45 degrees below the top line
        });
    }
}

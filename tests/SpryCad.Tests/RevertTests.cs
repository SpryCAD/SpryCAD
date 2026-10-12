using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Changes in words (Core/Storage/PartDescribe) and going back (Core/Storage/PartRevert): full reverts, selective
/// reverts of one revision's changes (dependents, changes made since, restricted to some objects, removed and
/// reordered features, reverting a revert, embedded files), every result saved, loaded and rebuilt.
/// </summary>
static class RevertTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-revert-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The part's content as JSON: no identity or revision, name counters sorted.</summary>
    static string Json(CadDocument d)
    {
        var c = CadDocument.FromJson(d.ToJson());
        c.Id = null; c.Revision = null;
        c.NameCounters = c.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        return c.ToJson();
    }

    static string Names(IEnumerable<string> l) => "[" + string.Join(", ", l) + "]";

    static string Order(CadDocument d) => Names(d.Features.Select(f => f.Name));

    /// <summary>A part saved revision by revision to one file (revisions oldest first).</summary>
    sealed class Part
    {
        public readonly CadDocument Doc;
        public readonly string Path;
        public readonly List<Guid> Revs = new();
        public Part(CadDocument doc, string dir) { Doc = doc; Path = System.IO.Path.Combine(dir, "p" + CadDocument.FileExtension); }
        public Guid Save(string message = "")
        {
            PartFile.Save(Doc, Path, "a", message);
            var r = Guid.Parse(Doc.Revision!);
            if (Revs.Count == 0 || Revs[^1] != r) Revs.Add(r);
            return r;
        }
        public Guid PartId => Guid.Parse(Doc.Id!);
        /// <summary>A revert's result saved as the part's next revision (as the app does: kind Revert, with its message).</summary>
        public Guid SaveRevert(CadDocument result, string message)
        {
            result.Id = Doc.Id;
            PartFile.Save(result, Path, "r", message, RevisionKind.Revert);
            var r = Guid.Parse(result.Revision!);
            Revs.Add(r);
            return r;
        }
        public Hash Root(Guid rev) { using var db = PartDatabase.Open(Path); return db.GetRevision(rev)!.RootHash; }
        public RevisionInfo Head() { using var db = PartDatabase.Open(Path); return db.GetHead(PartId)!; }
        public RevertPlan Plan(CadDocument current, Guid rev, IReadOnlySet<ObjectId>? only = null, bool withDependents = false)
        {
            using var db = PartDatabase.Open(Path);
            return PartRevert.PlanSelective(db, current, rev, only, withDependents);
        }
    }

    /// <summary>A plate (width W): Sketch1, Boss-Extrude1 (depth 10), Sketch2 (a circle), Cut-Extrude1 (through all).</summary>
    static (CadDocument doc, SketchFeature s1, ExtrudeFeature plate, SketchFeature s2, ExtrudeFeature cut) Plate()
    {
        var doc = new CadDocument();
        doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
        var rect = CoreTests.Rect(100, 60, "W");
        rect.Constraints.Where(c => c.IsDimension).Last().Name = "D2"; // the height, named as the editor names them
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = rect });
        var plate = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
        var (s2, cut) = AddHole(doc);
        return (doc, s1, plate, s2, cut);
    }

    static (SketchFeature, ExtrudeFeature) AddHole(CadDocument doc, int? insertAt = null)
    {
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(20, 30));
        sk.Add(new SkCircle { Center = c.Id, R = 5 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk }, insertAt);
        var cut = doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAllBoth }, insertAt + 1);
        return (s2, cut);
    }

    /// <summary>A fillet on the plate's vertical edge at x 100, y 60 (picked from the part as it rebuilds now).</summary>
    static FilletFeature CornerFillet(CadDocument doc, string radius = "3")
    {
        var r = new Regenerator().Rebuild(doc);
        var edge = r.State.Bodies[0].Solid.Topology.Edges.First(e => e.IsLinear && Math.Abs(e.Start.X - 100) < 1e-6 && Math.Abs(e.Start.Y - 60) < 1e-6
            && Math.Abs(e.End.X - 100) < 1e-6 && Math.Abs(e.End.Y - 60) < 1e-6);
        return new FilletFeature { Radius = radius, Edges = { new EdgeRef { A = edge.FaceA, B = edge.FaceB, Hint = edge.Midpoint } } };
    }

    /// <summary>A sketch (a point) on the Top plane: a feature that builds on nothing.</summary>
    static SketchFeature PointSketch(CadDocument doc, int? insertAt = null)
    {
        var sk = new Sketch();
        sk.AddPoint(new Vec2(5, 5));
        return doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = sk }, insertAt);
    }

    /// <summary>
    /// A plan's result saves (to a file of its own) and loads back the same, and rebuilds without exceptions (with
    /// every feature built unless <paramref name="allowErrors"/>: a revert made anyway, its dependents failing).
    /// </summary>
    static void Check(TestRunner t, CadDocument result, string label, bool allowErrors = false)
    {
        var c = result.Clone();
        c.Id = null; c.Revision = null;
        var path = Path.Combine(NewDir("check"), "c" + CadDocument.FileExtension);
        PartFile.Save(c, path, "t");
        var back = PartFile.Load(path);
        t.True(Json(back) == Json(result), label + ": loaded back differs from the plan's result");
        t.True(back.Files.Keys.Order().SequenceEqual(result.Files.Keys.Order()) && back.Files.All(kv => kv.Value.AsSpan().SequenceEqual(result.Files[kv.Key])),
            label + ": embedded files differ after save and load");
        var r = new Regenerator().Rebuild(back);
        if (!allowErrors)
            foreach (var f in back.Features)
                t.True(r.Status.TryGetValue(f.Id, out var s) && (s.Ok || s.Suppressed), $"{label}: {f.Name} did not rebuild: {(r.Status.TryGetValue(f.Id, out var s2) ? s2.Error : "no status")}");
    }

    static byte[] Bytes(CadDocument d, string featureId) => PartObjects.From(d)[PartObjects.FeatureId(featureId)];

    static byte[] BoxStep()
    {
        var doc = new CadDocument();
        var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(20, 20) });
        doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
        var r = new Regenerator().Rebuild(doc);
        using var ms = new MemoryStream();
        StepWriter.Write(ms, r.State.Bodies.Select(b => (b.Id, b.Solid)), "box");
        return ms.ToArray();
    }

    public static void Run(TestRunner t)
    {
        t.Test("Describe: depth, sketch dimension, lines added, equation, feature added and removed, in words", () =>
        {
            var (doc, s1, plate, s2, cut) = Plate();
            var p = new Part(doc, NewDir("describe"));
            var r0 = p.Save();
            t.Eq(plate.Name, "Boss-Extrude1");

            // a feature's field
            var old = Bytes(doc, plate.Id);
            plate.Depth = "15";
            var text = PartDescribe.Describe(ObjectKind.Feature, old, Bytes(doc, plate.Id), plate.Name);
            t.Eq(text, "Boss-Extrude1: depth 10 → 15");
            var r1 = p.Save();

            // a sketch dimension: named, with both values
            old = Bytes(doc, s1.Id);
            var d2 = s1.Sketch.Constraints.Single(c => c.Name == "D2");
            d2.Expr = "70"; d2.Value = 70;
            text = PartDescribe.Describe(ObjectKind.Feature, old, Bytes(doc, s1.Id), s1.Name);
            t.True(text == $"{s1.Name}: D2 60 → 70", "dimension: " + text);
            var r2 = p.Save();

            // lines added to a sketch
            old = Bytes(doc, s2.Id);
            var a = s2.Sketch.AddPoint(new Vec2(50, 10)); var b = s2.Sketch.AddPoint(new Vec2(60, 10)); var c3 = s2.Sketch.AddPoint(new Vec2(60, 20));
            s2.Sketch.Add(new SkLine { P1 = a.Id, P2 = b.Id });
            s2.Sketch.Add(new SkLine { P1 = b.Id, P2 = c3.Id });
            text = PartDescribe.Describe(ObjectKind.Feature, old, Bytes(doc, s2.Id), s2.Name);
            t.True(text == $"{s2.Name}: 2 lines added", "lines: " + text);
            var r3 = p.Save();

            // an equation
            var varsId = PartObjects.Single(ObjectKind.Variables);
            var oldV = PartObjects.From(doc)[varsId];
            doc.Equations[0].Expr = "120";
            text = PartDescribe.Describe(ObjectKind.Variables, oldV, PartObjects.From(doc)[varsId], "Equations");
            t.True(text.Contains("W = 100 → 120"), "equation: " + text);
            var r4 = p.Save();
            // an equation added, and one removed
            oldV = PartObjects.From(doc)[varsId];
            doc.Equations.Add(new Equation { Name = "X", Expr = "5" });
            var midV = PartObjects.From(doc)[varsId];
            t.True(PartDescribe.Describe(ObjectKind.Variables, oldV, midV, "Equations").Contains("X = 5 added"), "equation added: " + PartDescribe.Describe(ObjectKind.Variables, oldV, midV, "Equations"));
            doc.Equations.RemoveAt(1);
            t.True(PartDescribe.Describe(ObjectKind.Variables, midV, PartObjects.From(doc)[varsId], "Equations").Contains("X removed"), "equation removed");

            // a feature added, then removed
            var fillet = doc.AddFeature(new FilletFeature { Radius = "2" });
            var r5 = p.Save();
            doc.Features.Remove(fillet);
            var r6 = p.Save();
            t.Eq(p.Revs.Count, 7);

            using var db = PartDatabase.Open(p.Path);
            string One(Guid from, Guid to)
            {
                var l = PartDescribe.Changes(db, from, to);
                t.True(l.All(x => x.Change.ObjectId.Kind != ObjectKind.NameCounters), "name counters listed");
                return string.Join(" | ", l.Select(x => x.Text));
            }
            t.Eq(One(r0, r1), "Boss-Extrude1: depth 10 → 15");
            t.Eq(One(r1, r2), $"{s1.Name}: D2 60 → 70");
            t.Eq(One(r2, r3), $"{s2.Name}: 2 lines added");
            t.True(One(r3, r4) == "Equations: W = 100 → 120", "via Changes: " + One(r3, r4));

            // from nothing: features first, in the part's order; no name counters
            var first = PartDescribe.Changes(db, null, r0);
            t.True(first.All(x => x.Change.ObjectId.Kind != ObjectKind.NameCounters), "name counters listed from nothing");
            var feats = first.TakeWhile(x => x.Change.ObjectId.Kind == ObjectKind.Feature).Select(x => x.Name).ToList();
            t.True(feats.SequenceEqual(new[] { s1.Name, plate.Name, s2.Name, cut.Name }), "features from nothing: " + Names(feats));
            t.True(first.Skip(feats.Count).All(x => x.Change.ObjectId.Kind != ObjectKind.Feature), "a feature after the other objects");
            t.True(first.Where(x => x.Change.ObjectId.Kind == ObjectKind.Feature).All(x => x.Text == x.Name + " added"), "added: " + Names(first.Select(x => x.Text)));

            // over several revisions: features in order (the last revision's), then the rest
            var many = PartDescribe.Changes(db, r0, r4);
            var mf = many.TakeWhile(x => x.Change.ObjectId.Kind == ObjectKind.Feature).Select(x => x.Name).ToList();
            t.True(mf.SequenceEqual(new[] { s1.Name, plate.Name, s2.Name }), "features 0→4: " + Names(mf));
            t.True(many.Skip(mf.Count).Select(x => x.Change.ObjectId.Kind).SequenceEqual(new[] { ObjectKind.Variables }), "0→4 others: " + Names(many.Select(x => x.Text)));

            // a feature added or removed (the order object changes too: not a reorder, as History says)
            var c45 = PartDescribe.Changes(db, r4, r5);
            t.True(c45.Any(x => x.Text == $"{fillet.Name} added" && x.Name == fillet.Name), "added: " + One(r4, r5));
            t.True(PartDescribe.Changes(db, r5, r6).Any(x => x.Text == $"{fillet.Name} removed"), "removed: " + One(r5, r6));
            t.Eq(One(r4, r5), $"{fillet.Name} added");
            t.Eq(One(r5, r6), $"{fillet.Name} removed");
        });

        t.Test("Revert: full revert to a revision saved as the next one has that revision's root hash, kind Revert and the message", () =>
        {
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("full"));
            p.Save();
            plate.Depth = "15";
            var r1 = p.Save();
            doc.AddFeature(CornerFillet(doc));
            p.Save();
            doc.Equations[0].Expr = "120";
            var r3 = p.Save();

            CadDocument back;
            using (var db = PartDatabase.Open(p.Path)) back = PartRevert.RevertTo(db, r1);
            t.Eq(Json(back), Json(PartFile.Load(p.Path, revision: r1)));
            Check(t, back, "full revert");
            var r4 = p.SaveRevert(back, "Reverted to revision 2");
            t.True(p.Root(r4) == p.Root(r1), "root hash after a full revert differs from the revision's");
            var head = p.Head();
            t.Eq(head.RevisionId, r4);
            t.Eq(head.Kind, RevisionKind.Revert);
            t.Eq(head.Message, "Reverted to revision 2");
            t.Eq(head.ParentId, r3);
            t.Eq(PartFile.History(p.Path).Count, 5);
        });

        t.Test("Revert: selective revert of a revision restores its change and keeps later unrelated changes and features", () =>
        {
            var (doc, _, plate, s2, cut) = Plate();
            var p = new Part(doc, NewDir("selective"));
            p.Save();
            plate.Depth = "15";
            var r = p.Save();
            cut.End = EndCondition.ThroughAll; cut.Reverse = true; // B: unrelated
            p.Save();
            var c = doc.AddFeature(CornerFillet(doc)); // C: new
            p.Save();

            var plan = p.Plan(doc, r);
            var res = plan.Result;
            t.Eq(((ExtrudeFeature)res.Find(plate.Id)!).Depth, "10");
            var rc = (ExtrudeFeature)res.Find(cut.Id)!;
            t.True(rc.End == EndCondition.ThroughAll && rc.Reverse, "the later change to the cut lost");
            t.True(res.Find(c.Id) is FilletFeature, "the fillet added since is gone");
            t.Eq(Order(res), Order(doc));
            t.True(plan.Items.Select(i => i.Name).SequenceEqual(new[] { plate.Name }), "items: " + Names(plan.Items.Select(i => i.Name)));
            t.Eq(plan.Items[0].Kind, ChangeKind.Modified);
            t.True(plan.ChangedSince.Count == 0, "changed since: " + Names(plan.ChangedSince));
            t.True(plan.Dependents.Count == 0, "dependents: " + Names(plan.Dependents));
            t.Eq(res.NameCounters.OrderBy(kv => kv.Key).Select(kv => kv.Key + kv.Value).Aggregate("", (x, y) => x + y),
                doc.NameCounters.OrderBy(kv => kv.Key).Select(kv => kv.Key + kv.Value).Aggregate("", (x, y) => x + y));
            Check(t, res, "selective");
            // the part itself is not changed by planning
            t.Eq(plate.Depth, "15");
        });

        t.Test("Revert: a revision that added a feature used by a later one: dependents listed, or removed with it", () =>
        {
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("dependents"));
            p.Save();
            // R: a boss on the plate's top face (its sketch and the extrude, D)
            var top = new FaceRef { Name = plate.Id + ".End", Hint = new Vec3(50, 30, 10), Normal = Vec3.UnitZ };
            var sk = new Sketch();
            var cp = sk.AddPoint(new Vec2(70, 30));
            sk.Add(new SkCircle { Center = cp.Id, R = 8 });
            var s3 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = sk });
            var d = doc.AddFeature(new ExtrudeFeature { SketchId = s3.Id, Depth = "5" });
            var r = p.Save();
            // later: E, a fillet on the boss's top rim
            var reb = new Regenerator().Rebuild(doc);
            t.True(reb.Status[d.Id].Ok, "the boss: " + reb.Status[d.Id].Error);
            var rim = reb.State.Bodies.SelectMany(b => b.Solid.Topology.Edges).First(e => !e.IsLinear && Math.Abs(e.Start.Z - 15) < 1e-6 && Math.Abs(e.End.Z - 15) < 1e-6);
            var e = doc.AddFeature(new FilletFeature { Radius = "1", Edges = { new EdgeRef { A = rim.FaceA, B = rim.FaceB, Hint = rim.Midpoint } } });
            t.True(new Regenerator().Rebuild(doc).Status[e.Id].Ok, "the rim fillet does not build");
            var other = PointSketch(doc); // and an unrelated feature
            p.Save();

            var plan = p.Plan(doc, r);
            t.True(plan.Dependents.SequenceEqual(new[] { e.Name }), "dependents: " + Names(plan.Dependents));
            t.True(plan.Result.Find(d.Id) == null && plan.Result.Find(s3.Id) == null, "the boss not removed");
            t.True(plan.Result.Find(e.Id) != null, "the dependent removed without withDependents");
            t.True(plan.Items.Where(i => i.Id.Kind == ObjectKind.Feature).Select(i => i.Name).Order().SequenceEqual(new[] { s3.Name, d.Name }.Order()), "items: " + Names(plan.Items.Select(i => i.Name)));
            Check(t, plan.Result, "revert anyway", allowErrors: true);

            var all = p.Plan(doc, r, withDependents: true);
            t.True(all.Dependents.Count == 0, "dependents with withDependents: " + Names(all.Dependents));
            t.True(all.Result.Find(e.Id) == null, "the dependent kept with withDependents");
            t.True(all.Result.Find(d.Id) == null && all.Result.Find(s3.Id) == null, "the boss kept with withDependents");
            t.True(all.Result.Find(other.Id) != null, "an unrelated feature removed");
            t.True(all.Items.Any(i => i.Name == e.Name && i.Kind == ChangeKind.Added), "the dependent not an item: " + Names(all.Items.Select(i => i.Name + " " + i.Kind)));
            t.Eq(Order(all.Result), Order(PartFile.Load(p.Path, revision: p.Revs[0])).TrimEnd(']') + ", " + other.Name + "]");
            Check(t, all.Result, "with dependents");
            // features only added: no reorder to put back
            t.True(!plan.Items.Any(i => i.Kind == ChangeKind.Reordered), "a reorder item for a revision that only added features: " + Names(plan.Items.Select(i => i.Name + " " + i.Kind)));
        });

        t.Test("Revert: a feature changed again since the revision is listed in ChangedSince and goes back as before it", () =>
        {
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("since"));
            p.Save();
            plate.Depth = "15";
            var r = p.Save();
            plate.Depth = "20";
            p.Save();
            var plan = p.Plan(doc, r);
            t.True(plan.ChangedSince.SequenceEqual(new[] { plate.Name }), "changed since: " + Names(plan.ChangedSince));
            t.True(plan.Items.Single().ChangedSince, "the item not marked changed since");
            t.Eq(((ExtrudeFeature)plan.Result.Find(plate.Id)!).Depth, "10");
            Check(t, plan.Result, "changed since");

            // and unsaved changes count as changed since too (the plan is against the part as it is)
            var p2 = new Part(Plate().doc, NewDir("since-unsaved"));
            p2.Save();
            var pl2 = (ExtrudeFeature)p2.Doc.Features[1];
            pl2.Depth = "15";
            var r2 = p2.Save();
            pl2.Depth = "25"; // not saved
            var plan2 = p2.Plan(p2.Doc, r2);
            t.True(plan2.ChangedSince.SequenceEqual(new[] { pl2.Name }), "unsaved change not in changed since: " + Names(plan2.ChangedSince));
            t.Eq(((ExtrudeFeature)plan2.Result.Find(pl2.Id)!).Depth, "10");
        });

        t.Test("Revert: restricted to one of a revision's two changes reverts just that one", () =>
        {
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("only"));
            p.Save();
            plate.Depth = "15";
            doc.Equations[0].Expr = "120";
            var r = p.Save();

            var onlyPlate = p.Plan(doc, r, new HashSet<ObjectId> { PartObjects.FeatureId(plate.Id) });
            t.Eq(((ExtrudeFeature)onlyPlate.Result.Find(plate.Id)!).Depth, "10");
            t.Eq(onlyPlate.Result.Equations[0].Expr, "120");
            t.True(onlyPlate.Items.Select(i => i.Name).SequenceEqual(new[] { plate.Name }), "items: " + Names(onlyPlate.Items.Select(i => i.Name)));
            Check(t, onlyPlate.Result, "only the plate");

            var onlyVars = p.Plan(doc, r, new HashSet<ObjectId> { PartObjects.Single(ObjectKind.Variables) });
            t.Eq(((ExtrudeFeature)onlyVars.Result.Find(plate.Id)!).Depth, "15");
            t.Eq(onlyVars.Result.Equations[0].Expr, "100");
            t.True(onlyVars.Items.Select(i => i.Name).SequenceEqual(new[] { "Equations" }), "items: " + Names(onlyVars.Items.Select(i => i.Name)));
            Check(t, onlyVars.Result, "only the equations");

            var both = p.Plan(doc, r);
            t.True(((ExtrudeFeature)both.Result.Find(plate.Id)!).Depth == "10" && both.Result.Equations[0].Expr == "100", "both not reverted");
            t.Eq(both.Items.Count, 2);
        });

        t.Test("Revert: a revision that removed a feature: it comes back after the feature that preceded it, later features kept", () =>
        {
            var doc = new CadDocument();
            doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
            var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60, "W") });
            var plate = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
            var f = doc.AddFeature(CornerFillet(doc));
            AddHole(doc);
            var p = new Part(doc, NewDir("removed"));
            p.Save();
            var fBytes = Bytes(doc, f.Id);
            var expectedBefore = Order(doc);
            doc.Features.Remove(f);
            var r = p.Save();
            var x = PointSketch(doc);
            p.Save();

            var plan = p.Plan(doc, r);
            t.Eq(Order(plan.Result), expectedBefore.TrimEnd(']') + ", " + x.Name + "]");
            t.Eq(plan.Result.IndexOf(f.Id), 2);
            t.True(Bytes(plan.Result, f.Id).AsSpan().SequenceEqual(fBytes), "the fillet not put back as it was");
            t.True(plan.Items.Any(i => i.Name == f.Name && i.Kind == ChangeKind.Removed), "items: " + Names(plan.Items.Select(i => i.Name + " " + i.Kind)));
            t.True(plan.Dependents.Count == 0 && plan.ChangedSince.Count == 0, "dependents " + Names(plan.Dependents) + " changed since " + Names(plan.ChangedSince));
            Check(t, plan.Result, "removed feature back");

            // with a feature inserted since right after its predecessor: it goes back right after the predecessor
            var y = PointSketch(doc, insertAt: 2);
            var plan2 = p.Plan(doc, r);
            t.True(plan2.Result.IndexOf(f.Id) == plan2.Result.IndexOf(plate.Id) + 1, "with a feature inserted after the predecessor: " + Order(plan2.Result));
            t.True(plan2.Result.Find(y.Id) != null, "the inserted feature lost");
        });

        t.Test("Revert: a revision that reordered features: the earlier order comes back, a feature added since keeps its position", () =>
        {
            var (doc, s1, plate, s2, cut) = Plate();
            var p = new Part(doc, NewDir("reorder"));
            p.Save();
            int a = doc.Features.IndexOf(plate), b = doc.Features.IndexOf(s2);
            (doc.Features[a], doc.Features[b]) = (doc.Features[b], doc.Features[a]);
            var r = p.Save();
            var x = PointSketch(doc, insertAt: 2);
            p.Save();
            t.Eq(Order(doc), Names(new[] { s1.Name, s2.Name, x.Name, plate.Name, cut.Name }));

            var plan = p.Plan(doc, r);
            t.Eq(Order(plan.Result), Names(new[] { s1.Name, plate.Name, x.Name, s2.Name, cut.Name }));
            t.True(plan.Items.Any(i => i.Kind == ChangeKind.Reordered), "no reorder item: " + Names(plan.Items.Select(i => i.Name + " " + i.Kind)));
            Check(t, plan.Result, "reorder back");

            // nothing added since: exactly the earlier order
            var p2 = new Part(Plate().doc, NewDir("reorder2"));
            p2.Save();
            var before2 = Order(p2.Doc);
            (p2.Doc.Features[1], p2.Doc.Features[2]) = (p2.Doc.Features[2], p2.Doc.Features[1]);
            var r2 = p2.Save();
            t.Eq(Order(p2.Plan(p2.Doc, r2).Result), before2);
        });

        t.Test("Revert: reverting a revert gives the state the revert was made from (same root hash)", () =>
        {
            // a change reverted
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("revrev"));
            p.Save();
            plate.Depth = "15";
            var r = p.Save();
            doc.AddFeature(CornerFillet(doc));
            var pre = p.Save();
            var plan = p.Plan(doc, r);
            var r2 = p.SaveRevert(plan.Result, plan.Message);
            t.True(p.Root(r2) != p.Root(pre), "the revert changed nothing");
            var cur = PartFile.Load(p.Path);
            var back = p.Plan(cur, r2);
            Check(t, back.Result, "revert of a revert");
            var r3 = p.SaveRevert(back.Result, back.Message);
            string d1;
            using (var db = PartDatabase.Open(p.Path)) d1 = string.Join(", ", PartHistory.Diff(db, pre, r3).Select(c => c.Kind + " " + c.ObjectId));
            t.True(p.Root(r3) == p.Root(pre), "reverting the revert does not give the state before it: " + d1);

            // an added feature reverted (removed), then put back
            var (doc2, _, plate2, _, _) = Plate();
            var q = new Part(doc2, NewDir("revrev-add"));
            q.Save();
            var fil = doc2.AddFeature(CornerFillet(doc2), insertAt: 2);
            var ra = q.Save();
            plate2.Depth = "12";
            var pre2 = q.Save();
            var pa = q.Plan(doc2, ra);
            t.True(pa.Result.Find(fil.Id) == null, "the added fillet not removed");
            var rb = q.SaveRevert(pa.Result, pa.Message);
            var pb = q.Plan(PartFile.Load(q.Path), rb);
            t.Eq(pb.Result.IndexOf(fil.Id), 2);
            Check(t, pb.Result, "revert of a removal revert");
            var rc = q.SaveRevert(pb.Result, pb.Message);
            string diff;
            using (var db = PartDatabase.Open(q.Path)) diff = string.Join(", ", PartHistory.Diff(db, pre2, rc).Select(c => c.Kind + " " + c.ObjectId));
            t.True(q.Root(rc) == q.Root(pre2), "reverting the revert of an addition differs: " + diff);
        });

        t.Test("Revert: embedded files: reverting an import removes the file and the feature; reverting their removal brings them back", () =>
        {
            var step = BoxStep();
            const string name = "imports/box.step";
            var (doc, _, plate, _, _) = Plate();
            var p = new Part(doc, NewDir("files"));
            p.Save();
            doc.AddFile(name, step);
            var imp = doc.AddFeature(new ImportFeature { File = name, Sha256 = DocumentContainer.Hash(step) });
            var rImport = p.Save();
            t.True(new Regenerator().Rebuild(doc).Status[imp.Id].Ok, "the import does not build");
            plate.Depth = "15";
            p.Save();

            var plan = p.Plan(doc, rImport);
            t.True(plan.Result.Find(imp.Id) == null, "the import feature kept");
            t.True(plan.Result.Files.Count == 0, "embedded files kept: " + Names(plan.Result.Files.Keys));
            t.Eq(((ExtrudeFeature)plan.Result.Find(plate.Id)!).Depth, "15");
            t.True(plan.Items.Any(i => i.Id.Kind == ObjectKind.File && i.Name == name), "the file not an item: " + Names(plan.Items.Select(i => i.Name)));
            Check(t, plan.Result, "import reverted");

            // remove them (a revision), then revert that
            doc.Features.Remove(imp);
            doc.Files.Remove(name);
            var rRemove = p.Save();
            plate.Depth = "18";
            p.Save();
            var back = p.Plan(doc, rRemove);
            t.True(back.Result.Find(imp.Id) is ImportFeature, "the import feature not back");
            t.True(back.Result.Files.TryGetValue(name, out var bytes) && bytes.AsSpan().SequenceEqual(step), "the embedded file not back");
            t.Eq(((ExtrudeFeature)back.Result.Find(plate.Id)!).Depth, "18");
            Check(t, back.Result, "import back");
            var saved = p.SaveRevert(back.Result, back.Message);
            var loaded = PartFile.Load(p.Path);
            t.True(loaded.Files.TryGetValue(name, out var lb) && lb.AsSpan().SequenceEqual(step), "the file not in the saved part");
            t.True(new Regenerator().Rebuild(loaded).Status[imp.Id].Ok, "the import back does not build");
            t.Eq(p.Head().RevisionId, saved);
        });
    }
}

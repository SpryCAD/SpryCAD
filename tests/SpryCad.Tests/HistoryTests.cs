using Microsoft.Data.Sqlite;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>A part file's history (Core/Storage/PartHistory): what each revision changed, diffs, tags, purge, compact, usage.</summary>
static class HistoryTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-history-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static long Scalar(string path, string sql, params (string, object)[] ps)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    static void Exec(string path, string sql, params (string, object)[] ps)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    static long Count(string path, string table) => Scalar(path, $"SELECT COUNT(*) FROM {table}");

    /// <summary>RevisionObjects or Cache rows whose blob is missing (must always be 0).</summary>
    static long MissingBlobs(string path) => Scalar(path, """
        SELECT (SELECT COUNT(*) FROM RevisionObjects o WHERE NOT EXISTS (SELECT 1 FROM Blobs b WHERE b.Hash = o.Hash))
             + (SELECT COUNT(*) FROM Cache c WHERE NOT EXISTS (SELECT 1 FROM Blobs b WHERE b.Hash = c.Hash))
        """);

    static long BlobExists(string path, Hash h) => Scalar(path, "SELECT COUNT(*) FROM Blobs WHERE Hash = $h", ("$h", h.ToBytes()));

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>The part's content as JSON: no identity or revision, name counters sorted.</summary>
    static string Json(CadDocument d)
    {
        var c = CadDocument.FromJson(d.ToJson());
        c.Id = null; c.Revision = null;
        c.NameCounters = c.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        return c.ToJson();
    }

    static string Names(IEnumerable<string> l) => "[" + string.Join(", ", l) + "]";

    /// <summary>A plate (width W) with a hole: Sketch1, Extrude1, Sketch2, Extrude2 (cut). It rebuilds.</summary>
    static (CadDocument doc, ExtrudeFeature plate, SketchFeature holeSketch) Plate()
    {
        var doc = new CadDocument();
        doc.Equations.Add(new Equation { Name = "W", Expr = "100" });
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60, "W") });
        var plate = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10" });
        var sk = new Sketch();
        var c = sk.AddPoint(new Vec2(20, 30));
        sk.Add(new SkCircle { Center = c.Id, R = 5 });
        var s2 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        doc.AddFeature(new ExtrudeFeature { SketchId = s2.Id, IsCut = true, End = EndCondition.ThroughAllBoth });
        return (doc, plate, s2);
    }

    /// <summary>
    /// A part saved six times, each by its own author: (0) the plate; (1) a fillet added; (2) the plate's depth changed;
    /// (3) the plate's extrude and the hole's sketch swapped; (4) the fillet deleted; (5) an equation changed.
    /// Revisions oldest first, and the part as each was saved (JSON).
    /// </summary>
    sealed record Saved(string Path, CadDocument Doc, List<Guid> Revs, List<string> Jsons, ExtrudeFeature Plate, SketchFeature Hole, FilletFeature Fillet);

    static readonly string[] Authors = { "alice", "bob", "carol", "dave", "erin", "frank" };

    static Saved SixSaves(string dir)
    {
        var (doc, plate, hole) = Plate();
        var path = Path.Combine(dir, "p" + CadDocument.FileExtension);
        var revs = new List<Guid>();
        var jsons = new List<string>();
        void Save(int i)
        {
            PartFile.Save(doc, path, Authors[i]);
            revs.Add(Guid.Parse(doc.Revision!));
            jsons.Add(Json(doc));
        }
        Save(0);
        var fillet = doc.AddFeature(new FilletFeature { Radius = "2", Edges = { new EdgeRef { A = plate.Id + ".Top", B = plate.Id + ".Side1", Hint = new Vec3(50, 0, 10) } } });
        Save(1);
        plate.Depth = "15";
        Save(2);
        int a = doc.Features.IndexOf(plate), b = doc.Features.IndexOf(hole);
        (doc.Features[a], doc.Features[b]) = (doc.Features[b], doc.Features[a]);
        Save(3);
        doc.Features.Remove(fillet);
        Save(4);
        doc.Equations[0].Expr = "120";
        Save(5);
        return new Saved(path, doc, revs, jsons, plate, hole, fillet);
    }

    public static void Run(TestRunner t)
    {
        t.Test("History: Read lists revisions newest first with authors and what each changed", () =>
        {
            var s = SixSaves(NewDir("read"));
            using var db = PartDatabase.Open(s.Path);
            var h = PartHistory.Read(db);
            t.Eq(h.Count, 6);
            t.True(h.Select(e => e.Revision.RevisionId).SequenceEqual(Enumerable.Reverse(s.Revs)), "not newest first");
            t.True(h.Select(e => e.Revision.Author).SequenceEqual(Authors.Reverse()), "authors: " + Names(h.Select(e => e.Revision.Author)));
            t.True(h.All(e => e.Tags.Count == 0), "tags on an untagged file");
            ChangeSummary C(int i) => h[5 - i].Changes;

            var c0 = C(0);
            var first = PartFile.Load(s.Path, revision: s.Revs[0]).Features.Select(f => f.Name).Order().ToList();
            t.Eq(first.Count, 4);
            t.True(c0.Added.Order().SequenceEqual(first), "first revision added " + Names(c0.Added) + ", expected " + Names(first));
            t.True(c0.Modified.Count == 0 && c0.Removed.Count == 0 && !c0.Reordered && c0.OtherChanges == 0, "first revision: more than added features");

            var c1 = C(1);
            t.True(s.Fillet.Name.StartsWith("Fillet"), "fillet name " + s.Fillet.Name);
            t.True(c1.Added.SequenceEqual(new[] { s.Fillet.Name }), "added " + Names(c1.Added));
            t.True(c1.Modified.Count == 0 && c1.Removed.Count == 0, "fillet: modified " + Names(c1.Modified) + " removed " + Names(c1.Removed));
            t.True(!c1.Reordered, "adding a feature reported as a reorder");
            t.Eq(c1.OtherChanges, 0); // (its name counter changed too: not listed as a change of its own)

            var c2 = C(2);
            t.True(c2.Modified.SequenceEqual(new[] { s.Plate.Name }), "modified " + Names(c2.Modified));
            t.True(c2.Added.Count == 0 && c2.Removed.Count == 0 && !c2.Reordered && c2.OtherChanges == 0, "depth change: more than one modified feature");

            var c3 = C(3);
            t.True(c3.Reordered, "reorder not reported");
            t.True(c3.Added.Count == 0 && c3.Removed.Count == 0 && c3.Modified.Count == 0 && c3.OtherChanges == 0,
                $"reorder: added {Names(c3.Added)} removed {Names(c3.Removed)} modified {Names(c3.Modified)} other {c3.OtherChanges}");

            var c4 = C(4);
            t.True(c4.Removed.SequenceEqual(new[] { s.Fillet.Name }), "removed " + Names(c4.Removed));
            t.True(c4.Added.Count == 0 && c4.Modified.Count == 0 && !c4.Reordered && c4.OtherChanges == 0,
                $"delete: added {Names(c4.Added)} modified {Names(c4.Modified)} reordered {c4.Reordered} other {c4.OtherChanges}");

            var c5 = C(5);
            t.Eq(c5.OtherChanges, 1);
            t.True(c5.Added.Count + c5.Modified.Count + c5.Removed.Count == 0 && !c5.Reordered, "equation change: feature changes listed");
            t.True(!c5.IsEmpty && h.All(e => !e.Changes.IsEmpty), "an empty summary");
        });

        t.Test("History: a feature added and existing ones reordered in one save reports both; added at the front is no reorder", () =>
        {
            var dir = NewDir("addreorder");
            var (doc, plate, hole) = Plate();
            var path = Path.Combine(dir, "p" + CadDocument.FileExtension);
            PartFile.Save(doc, path, "a");
            // a sketch inserted at the front: every index shifts, the relative order does not
            var sk = new Sketch();
            sk.AddPoint(new Vec2(0, 0));
            doc.AddFeature(new SketchFeature { Plane = PlaneRef.Top, Sketch = sk }, insertAt: 0);
            PartFile.Save(doc, path, "a");
            int a = doc.Features.IndexOf(plate), b = doc.Features.IndexOf(hole);
            (doc.Features[a], doc.Features[b]) = (doc.Features[b], doc.Features[a]);
            doc.AddFeature(new FilletFeature { Radius = "1" });
            PartFile.Save(doc, path, "a");
            using var db = PartDatabase.Open(path);
            var h = PartHistory.Read(db);
            t.True(h[1].Changes.Added.Count == 1 && !h[1].Changes.Reordered, "insert at front: reordered " + h[1].Changes.Reordered);
            t.True(h[0].Changes.Added.Count == 1 && h[0].Changes.Reordered, "add + swap: reordered " + h[0].Changes.Reordered);
        });

        t.Test("History: Diff between revisions (adjacent and not, from nothing, to itself)", () =>
        {
            var s = SixSaves(NewDir("diff"));
            using var db = PartDatabase.Open(s.Path);
            var plateId = PartObjects.FeatureId(s.Plate.Id);
            var filletId = PartObjects.FeatureId(s.Fillet.Id);
            var order = PartObjects.Single(ObjectKind.FeatureOrder);
            var counters = PartObjects.Single(ObjectKind.NameCounters);
            string Show(List<ObjectChange> l) => string.Join(", ", l.Select(c => $"{c.Kind} {c.ObjectId}"));

            var d0 = PartHistory.Diff(db, null, s.Revs[0]);
            t.True(d0.All(c => c.Kind == ChangeKind.Added && c.Old == null && c.New != null), "from nothing: " + Show(d0));
            t.True(!d0.Any(c => c.ObjectId.Kind == ObjectKind.FeatureOrder), "the feature order reported as added");
            t.Eq(d0.Count(c => c.ObjectId.Kind == ObjectKind.Feature), 4);

            // 0 → 2: fillet added, plate changed, name counters changed; the order only grew (not a reorder)
            var d02 = PartHistory.Diff(db, s.Revs[0], s.Revs[2]);
            t.True(d02.Any(c => c.Kind == ChangeKind.Added && c.ObjectId.Equals(filletId) && c.Old == null && c.New != null), "0→2 fillet added: " + Show(d02));
            t.True(d02.Any(c => c.Kind == ChangeKind.Modified && c.ObjectId.Equals(plateId) && c.Old != null && c.New != null && c.Old != c.New), "0→2 plate modified: " + Show(d02));
            t.True(d02.Any(c => c.Kind == ChangeKind.Modified && c.ObjectId.Equals(counters)), "0→2 name counters: " + Show(d02));
            t.True(!d02.Any(c => c.ObjectId.Equals(PartObjects.Single(ObjectKind.Settings))), "0→2 settings changed: " + Show(d02));
            t.True(!d02.Any(c => c.ObjectId.Equals(order)), "0→2 a feature added reported as a reorder: " + Show(d02));
            t.Eq(d02.Count, 3);

            // 1 → 4: the fillet removed, the plate changed, the order changed; name counters the same
            var d14 = PartHistory.Diff(db, s.Revs[1], s.Revs[4]);
            t.True(d14.Any(c => c.Kind == ChangeKind.Removed && c.ObjectId.Equals(filletId) && c.New == null && c.Old != null), "1→4 fillet removed: " + Show(d14));
            t.True(d14.Any(c => c.Kind == ChangeKind.Modified && c.ObjectId.Equals(plateId)), "1→4 plate: " + Show(d14));
            t.True(d14.Any(c => c.Kind == ChangeKind.Reordered && c.ObjectId.Equals(order)), "1→4 order: " + Show(d14));
            t.Eq(d14.Count, 3);

            // 0 → 5: plate and variables changed, order changed (a swap), name counters; the fillet came and went
            var d05 = PartHistory.Diff(db, s.Revs[0], s.Revs[5]);
            t.True(!d05.Any(c => c.ObjectId.Equals(filletId)), "0→5 lists the fillet: " + Show(d05));
            t.True(d05.Any(c => c.Kind == ChangeKind.Modified && c.ObjectId.Equals(PartObjects.Single(ObjectKind.Variables))), "0→5 variables: " + Show(d05));
            t.Eq(d05.Count, 4);

            // backwards: 2 → 0 is the mirror of 0 → 2
            var d20 = PartHistory.Diff(db, s.Revs[2], s.Revs[0]);
            t.True(d20.Any(c => c.Kind == ChangeKind.Removed && c.ObjectId.Equals(filletId)), "2→0 fillet removed: " + Show(d20));
            t.Eq(d20.Count, d02.Count);

            t.Eq(PartHistory.Diff(db, s.Revs[3], s.Revs[3]).Count, 0);
        });

        t.Test("History: PartFile.Load with a revision gives the part as saved then", () =>
        {
            var s = SixSaves(NewDir("loadrev"));
            for (int i = 0; i < s.Revs.Count; i++)
            {
                var d = PartFile.Load(s.Path, revision: s.Revs[i]);
                t.Eq(d.Revision, s.Revs[i].ToString("D"));
                t.Eq(d.Id, s.Doc.Id);
                t.True(Json(d) == s.Jsons[i], $"revision {i} differs from what was saved");
            }
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[0]).Features.Count, 4);
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[1]).Features.Count, 5);
            t.Eq(((ExtrudeFeature)PartFile.Load(s.Path, revision: s.Revs[1]).Find(s.Plate.Id)!).Depth, "10");
            t.Eq(((ExtrudeFeature)PartFile.Load(s.Path, revision: s.Revs[2]).Find(s.Plate.Id)!).Depth, "15");
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[2]).Features[1].Id, s.Plate.Id);
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[3]).Features[1].Id, s.Hole.Id);
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[4]).Equations[0].Expr, "100");
            t.Eq(PartFile.Load(s.Path, revision: s.Revs[5]).Equations[0].Expr, "120");
            t.Eq(Json(PartFile.Load(s.Path)), s.Jsons[5]);
            t.True(Catch(() => PartFile.Load(s.Path, revision: Guid.NewGuid())) is InvalidDataException, "an unknown revision loaded");
        });

        t.Test("History: Tag, Untag, RenameTag (moving, refusals); Read shows the tags on their revisions", () =>
        {
            var s = SixSaves(NewDir("tags"));
            using var db = PartDatabase.Open(s.Path, readOnly: false);
            Dictionary<Guid, List<string>> Tags() => PartHistory.Read(db).ToDictionary(e => e.Revision.RevisionId, e => e.Tags.Order().ToList());

            PartHistory.Tag(db, s.Revs[0], "v1");
            PartHistory.Tag(db, s.Revs[2], "  v2  ");
            PartHistory.Tag(db, s.Revs[2], "review");
            var tg = Tags();
            t.True(tg[s.Revs[0]].SequenceEqual(new[] { "v1" }), "rev 0: " + Names(tg[s.Revs[0]]));
            t.True(tg[s.Revs[2]].SequenceEqual(new[] { "review", "v2" }), "rev 2 (trimmed name): " + Names(tg[s.Revs[2]]));
            t.True(tg.Where(kv => kv.Key != s.Revs[0] && kv.Key != s.Revs[2]).All(kv => kv.Value.Count == 0), "tags on other revisions");

            // the same name again moves it
            PartHistory.Tag(db, s.Revs[4], "v1");
            tg = Tags();
            t.True(tg[s.Revs[0]].Count == 0 && tg[s.Revs[4]].SequenceEqual(new[] { "v1" }), "v1 not moved: " + Names(tg[s.Revs[0]]) + " / " + Names(tg[s.Revs[4]]));
            t.Eq(Count(s.Path, "Tags"), 3L);

            PartHistory.Untag(db, "review");
            t.True(Tags()[s.Revs[2]].SequenceEqual(new[] { "v2" }), "Untag");
            PartHistory.Untag(db, "no-such-tag"); // (nothing)
            t.Eq(Count(s.Path, "Tags"), 2L);

            PartHistory.RenameTag(db, "v2", "release-1");
            tg = Tags();
            t.True(tg[s.Revs[2]].SequenceEqual(new[] { "release-1" }), "renamed: " + Names(tg[s.Revs[2]]));

            // refusals, changing nothing
            t.True(Catch(() => PartHistory.RenameTag(db, "nope", "x")) is ArgumentException, "rename of an unknown tag");
            t.True(Catch(() => PartHistory.RenameTag(db, "release-1", "v1")) is ArgumentException, "rename onto an existing tag");
            t.True(Catch(() => PartHistory.RenameTag(db, "release-1", "   ")) is ArgumentException, "rename to an empty name");
            t.True(Catch(() => PartHistory.Tag(db, s.Revs[1], "")) is ArgumentException, "an empty tag");
            t.True(Catch(() => PartHistory.Tag(db, s.Revs[1], "  ")) is ArgumentException, "a blank tag");
            tg = Tags();
            t.True(tg[s.Revs[2]].SequenceEqual(new[] { "release-1" }) && tg[s.Revs[4]].SequenceEqual(new[] { "v1" }) && tg[s.Revs[1]].Count == 0, "a refusal changed the tags");
            t.Eq(Count(s.Path, "Tags"), 2L);
            // renaming to itself is no change
            PartHistory.RenameTag(db, "v1", "v1");
            t.True(Tags()[s.Revs[4]].SequenceEqual(new[] { "v1" }), "rename to itself lost the tag");
        });

        t.Test("History: Purge keeps the history from a revision: older revisions, their tags and blobs gone; the head unchanged", () =>
        {
            var s = SixSaves(NewDir("purge"));
            var keep = s.Revs[2];
            Hash Of(CadDocument d, string featureId) => Hash.Of(PartObjects.From(d)[PartObjects.FeatureId(featureId)]);
            var doc0 = PartFile.Load(s.Path, revision: s.Revs[0]);
            var plateDepth10 = Of(doc0, s.Plate.Id); // used by revisions 0 and 1 only
            var filletHash = Of(PartFile.Load(s.Path, revision: s.Revs[2]), s.Fillet.Id); // revisions 1-3: kept
            t.Eq(BlobExists(s.Path, plateDepth10), 1L);

            using (var db = PartDatabase.Open(s.Path, readOnly: false))
            {
                PartHistory.Tag(db, s.Revs[0], "first");
                PartHistory.Tag(db, s.Revs[1], "filleted");
                PartHistory.Tag(db, s.Revs[2], "kept");
                PartHistory.Tag(db, s.Revs[5], "head");
                var (revs, tags) = PartHistory.PurgePlan(db, keep);
                t.True(revs.Select(r => r.RevisionId).SequenceEqual(new[] { s.Revs[1], s.Revs[0] }), "plan: " + string.Join(", ", revs.Select(r => s.Revs.IndexOf(r.RevisionId))));
                t.True(tags.Order().SequenceEqual(new[] { "filleted", "first" }), "plan tags: " + Names(tags));
                t.True(Catch(() => PartHistory.PurgePlan(db, Guid.NewGuid())) is ArgumentException, "a plan for an unknown revision");
                var (none, noTags) = PartHistory.PurgePlan(db, s.Revs[0]);
                t.True(none.Count == 0 && noTags.Count == 0, "a plan from the first revision removes something");
            }
            var head = Json(PartFile.Load(s.Path));
            var blobs = Count(s.Path, "Blobs");
            var size = new FileInfo(s.Path).Length;

            using (var db = PartDatabase.Open(s.Path, readOnly: false)) PartHistory.Purge(db, keep);

            var history = PartFile.History(s.Path);
            t.Eq(history.Count, 4);
            t.True(history.Select(r => r.RevisionId).SequenceEqual(new[] { s.Revs[5], s.Revs[4], s.Revs[3], s.Revs[2] }), "history after purge");
            t.Eq(history[^1].RevisionId, keep);
            t.True(history[^1].ParentId == null, "the kept revision still has a parent");
            t.Eq(Count(s.Path, "Revisions"), 4L);
            t.Eq(Scalar(s.Path, "SELECT COUNT(*) FROM RevisionObjects WHERE RevisionId IN ($a, $b)", ("$a", s.Revs[0].ToByteArray()), ("$b", s.Revs[1].ToByteArray())), 0L);
            t.Eq(Scalar(s.Path, "SELECT COUNT(*) FROM Tags WHERE Name IN ('first', 'filleted')"), 0L);
            t.Eq(Scalar(s.Path, "SELECT COUNT(*) FROM Tags WHERE Name IN ('kept', 'head')"), 2L);
            t.Eq(Json(PartFile.Load(s.Path)), head);
            for (int i = 2; i < 6; i++) t.True(Json(PartFile.Load(s.Path, revision: s.Revs[i])) == s.Jsons[i], $"revision {i} changed by the purge");
            t.True(Catch(() => PartFile.Load(s.Path, revision: s.Revs[0])) is InvalidDataException, "a purged revision loads");
            // blobs: only what a remaining revision or cache uses
            t.Eq(MissingBlobs(s.Path), 0L);
            t.Eq(BlobExists(s.Path, plateDepth10), 0L);
            t.Eq(BlobExists(s.Path, filletHash), 1L);
            var used = Scalar(s.Path, "SELECT COUNT(*) FROM (SELECT Hash FROM RevisionObjects UNION SELECT Hash FROM Cache)");
            t.Eq(Count(s.Path, "Blobs"), used);
            t.True(Count(s.Path, "Blobs") < blobs, $"blobs {blobs} → {Count(s.Path, "Blobs")}");
            t.True(new FileInfo(s.Path).Length <= size, $"file grew: {size} → {new FileInfo(s.Path).Length}");
            // the oldest now lists its features as added (from nothing)
            using (var db = PartDatabase.Open(s.Path))
            {
                var h = PartHistory.Read(db);
                t.Eq(h.Count, 4);
                t.Eq(h[^1].Changes.Added.Count, 5);
                t.True(h[^1].Tags.SequenceEqual(new[] { "kept" }), "kept tag");
            }
            // and the file still saves on
            var d = PartFile.Load(s.Path);
            d.Features.OfType<ExtrudeFeature>().First().Depth = "33";
            PartFile.Save(d, s.Path, "zed");
            t.Eq(PartFile.History(s.Path).Count, 5);

            // purging up to the head leaves one revision
            using (var db = PartDatabase.Open(s.Path, readOnly: false)) PartHistory.Purge(db, PartFile.History(s.Path)[0].RevisionId);
            t.Eq(PartFile.History(s.Path).Count, 1);
            t.Eq(Count(s.Path, "Revisions"), 1L);
            t.Eq(MissingBlobs(s.Path), 0L);
            t.Eq(Count(s.Path, "Blobs"), Scalar(s.Path, "SELECT COUNT(*) FROM (SELECT Hash FROM RevisionObjects UNION SELECT Hash FROM Cache)"));
            t.Eq(((ExtrudeFeature)PartFile.Load(s.Path).Features.OfType<ExtrudeFeature>().First()).Depth, "33");
        });

        t.Test("History: Compact keeps every blob a revision or the display uses and removes orphans", () =>
        {
            var dir = NewDir("compact");
            var s = SixSaves(dir);
            // a display for the head
            var d = PartFile.Load(s.Path);
            var r = new Regenerator().Rebuild(d);
            t.True(r.State.Bodies.Count > 0, "the part rebuilds");
            d.Display = MeshCache.Of(r, d.Color);
            PartFile.Save(d, s.Path, "x"); // (same content: no revision, the display stored)
            t.Eq(PartFile.History(s.Path).Count, 6);
            t.Eq(Count(s.Path, "Cache"), 1L);
            var blobs = Count(s.Path, "Blobs");
            var orphan = new byte[32];
            Random.Shared.NextBytes(orphan);
            Exec(s.Path, "INSERT INTO Blobs (Hash, Codec, RawSize, Data) VALUES ($h, 0, 4, $d)", ("$h", orphan), ("$d", new byte[] { 1, 2, 3, 4 }));
            t.Eq(Count(s.Path, "Blobs"), blobs + 1);

            using (var db = PartDatabase.Open(s.Path, readOnly: false)) PartHistory.Compact(db);
            t.Eq(Count(s.Path, "Blobs"), blobs);
            t.Eq(Scalar(s.Path, "SELECT COUNT(*) FROM Blobs WHERE Hash = $h", ("$h", orphan)), 0L);
            t.Eq(MissingBlobs(s.Path), 0L);
            for (int i = 0; i < s.Revs.Count; i++) t.True(Json(PartFile.Load(s.Path, revision: s.Revs[i])) == s.Jsons[i], $"revision {i} after compact");
            t.True(PartFile.Load(s.Path).Display != null, "the display gone after compact");
            // twice: nothing more to do
            using (var db = PartDatabase.Open(s.Path, readOnly: false)) PartHistory.Compact(db);
            t.Eq(Count(s.Path, "Blobs"), blobs);
        });

        t.Test("History: ClearCache removes the display and its blob; the part reloads without one", () =>
        {
            var dir = NewDir("clearcache");
            var (doc, _, _) = Plate();
            var r = new Regenerator().Rebuild(doc);
            doc.Display = MeshCache.Of(r, doc.Color);
            var path = Path.Combine(dir, "p" + CadDocument.FileExtension);
            PartFile.Save(doc, path, "a");
            var loaded = PartFile.Load(path);
            t.True(loaded.Display != null && loaded.Display.SequenceEqual(doc.Display), "the display saved");
            t.Eq(Count(path, "Cache"), 1L);
            var displayHash = Hash.Of(doc.Display);
            t.Eq(BlobExists(path, displayHash), 1L);
            var blobs = Count(path, "Blobs");
            var json = Json(loaded);

            using (var db = PartDatabase.Open(path, readOnly: false)) PartHistory.ClearCache(db);
            t.Eq(Count(path, "Cache"), 0L);
            t.Eq(BlobExists(path, displayHash), 0L);
            t.Eq(Count(path, "Blobs"), blobs - 1);
            t.Eq(MissingBlobs(path), 0L);
            var back = PartFile.Load(path);
            t.True(back.Display == null, "a display after ClearCache");
            t.Eq(Json(back), json);
        });

        t.Test("History: Usage splits the file into model, embedded files, display, history and thumbnail", () =>
        {
            var dir = NewDir("usage");
            var (doc, plate, _) = Plate();
            var r = new Regenerator().Rebuild(doc);
            doc.Display = MeshCache.Of(r, doc.Color);
            doc.Thumbnail = DocumentContainer.RenderThumbnail(doc, r);
            var data = new byte[20000];
            new Random(7).NextBytes(data); // (random: does not compress)
            doc.AddFile("imports/data.bin", data);
            var path = Path.Combine(dir, "p" + CadDocument.FileExtension);
            PartFile.Save(doc, path, "a");

            FileUsage U() { using var db = PartDatabase.Open(path); return PartHistory.Usage(db, path); }
            long BlobBytes() => Scalar(path, "SELECT COALESCE(SUM(length(Data)), 0) FROM Blobs");
            void Sensible(FileUsage u)
            {
                t.Eq(u.FileBytes, new FileInfo(path).Length);
                t.True(u.ModelBytes > 0, "no model bytes");
                t.Eq(u.ModelBytes + u.EmbeddedFileBytes + u.DisplayBytes + u.HistoryBytes, BlobBytes());
                t.True(u.ModelBytes + u.EmbeddedFileBytes + u.DisplayBytes + u.HistoryBytes + u.ThumbnailBytes <= u.FileBytes, $"parts exceed the file: {u}");
            }

            var u1 = U();
            Sensible(u1);
            t.True(u1.EmbeddedFileBytes >= data.Length / 2, "embedded file bytes " + u1.EmbeddedFileBytes);
            t.True(u1.DisplayBytes > 0, "no display bytes");
            t.Eq(u1.DisplayBytes, Scalar(path, "SELECT length(b.Data) FROM Blobs b JOIN Cache c ON c.Hash = b.Hash"));
            t.Eq(u1.HistoryBytes, 0L);
            t.True(doc.Thumbnail == null || u1.ThumbnailBytes == doc.Thumbnail.Length, "thumbnail bytes");
            t.Eq(u1.Revisions, 1);

            for (int i = 0; i < 4; i++)
            {
                plate.Depth = (11 + i).ToString();
                PartFile.Save(doc, path, "a");
            }
            var u2 = U();
            Sensible(u2);
            t.Eq(u2.Revisions, 5);
            t.True(u2.HistoryBytes > 0, "no history bytes after several saves");
            t.Eq(u2.EmbeddedFileBytes, u1.EmbeddedFileBytes);

            // a removed embedded file is history
            doc.Files.Remove("imports/data.bin");
            PartFile.Save(doc, path, "a");
            var u3 = U();
            Sensible(u3);
            t.Eq(u3.EmbeddedFileBytes, 0L);
            t.True(u3.HistoryBytes >= u2.HistoryBytes + data.Length / 2, $"the removed file not in history: {u2.HistoryBytes} → {u3.HistoryBytes}");
            t.Eq(u3.Revisions, 6);
        });

        t.Test("History: through a WorkingCopy: Edit (a tag) is saved to the file; Read; LoadRevision", () =>
        {
            var folder = WorkingCopy.Folder;
            var root = NewDir("workingcopy");
            WorkingCopy.Folder = Path.Combine(root, "working");
            try
            {
                var (doc, plate, _) = Plate();
                var src = Path.Combine(root, "p" + CadDocument.FileExtension);
                PartFile.Save(doc, src, "a");
                var first = Guid.Parse(doc.Revision!);
                using var wc = WorkingCopy.Open(src);
                t.True(wc.Exists, "the copy not written");
                plate.Depth = "20";
                wc.Save(doc, "b");
                var second = Guid.Parse(doc.Revision!);

                var h = wc.Read(PartHistory.Read);
                t.True(h.Select(e => e.Revision.RevisionId).SequenceEqual(new[] { second, first }), "WorkingCopy.Read history");
                t.Eq(((ExtrudeFeature)wc.LoadRevision(first).Find(plate.Id)!).Depth, "10");
                t.Eq(((ExtrudeFeature)wc.LoadRevision(second).Find(plate.Id)!).Depth, "20");

                wc.Edit(db => PartHistory.Tag(db, first, "v1"));
                t.True(wc.Read(PartHistory.Read).Single(e => e.Revision.RevisionId == first).Tags.SequenceEqual(new[] { "v1" }), "the copy has no tag");
                using (var db = PartDatabase.Open(src)) t.True(PartHistory.Read(db).All(e => e.Tags.Count == 0), "Edit changed the file before a save");
                // saved with no change to the part: no revision, the tag goes to the file
                wc.Save(doc, "b");
                t.Eq(PartFile.History(src).Count, 2);
                using (var db = PartDatabase.Open(src))
                {
                    var fh = PartHistory.Read(db);
                    t.True(fh.Single(e => e.Revision.RevisionId == first).Tags.SequenceEqual(new[] { "v1" }), "the tag not saved to the file");
                }
                // and the next save of a change keeps it
                plate.Depth = "21";
                wc.Save(doc, "b");
                using (var db = PartDatabase.Open(src))
                    t.True(PartHistory.Read(db).Single(e => e.Revision.RevisionId == first).Tags.SequenceEqual(new[] { "v1" }), "the tag lost by a later save");
                t.Eq(Json(CadDocument.Load(src)), Json(doc));
                wc.Close();
                t.True(!wc.Exists, "the copy left after Close");
            }
            finally { WorkingCopy.Folder = folder; }
        });
    }
}

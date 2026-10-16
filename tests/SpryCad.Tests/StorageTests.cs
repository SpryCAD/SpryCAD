using Microsoft.Data.Sqlite;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Part files as databases (Core/Storage): objects, revisions, blobs, tags and the file format itself.</summary>
static class StorageTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-storage-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    /// <summary>The file opened with plain SQLite (no SpryCAD code).</summary>
    static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static object? Scalar(string path, string sql)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static long Count(string path, string table) => (long)Scalar(path, $"SELECT COUNT(*) FROM {table}")!;

    static Dictionary<ObjectId, Hash> HeadObjects(string path, CadDocument doc)
    {
        using var db = PartDatabase.Open(path);
        return db.GetObjects(Guid.Parse(doc.Revision!));
    }

    /// <summary>The objects whose hash differs between two revisions (added, removed or changed).</summary>
    static List<ObjectId> Changed(Dictionary<ObjectId, Hash> a, Dictionary<ObjectId, Hash> b) =>
        a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).ToList();

    /// <summary>The part's JSON, its name counters in the order the file keeps them (sorted: the order means nothing).</summary>
    static string Json(CadDocument d)
    {
        d.NameCounters = d.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        return d.ToJson();
    }

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>A plate (width W) with a hole: two independent sketches, an extrude and a cut. It rebuilds.</summary>
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
    /// A part with something in every object kind: sketches, extrudes, a fillet, a pattern, an import of an embedded
    /// file, equations, a design table, material, sheet metal settings; and what the manifest keeps (rollback bar,
    /// sketch visibility, where it came from).
    /// </summary>
    static CadDocument Rich()
    {
        var (doc, plate, hole) = Plate();
        doc.Equations.Add(new Equation { Name = "R", Expr = "W / 50", Description = "Fillet radius", Exposed = true, Min = 0.5, Max = 5 });
        doc.Tables = new()
        {
            new DesignTable
            {
                Name = "Size", Description = "Plate size", Columns = { "W" }, Selected = "M",
                Rows = { new DesignRow { Key = "S", Values = { "80" } }, new DesignRow { Key = "M", Values = { "100" } }, new DesignRow { Key = "L", Values = { "120" } } },
            },
        };
        doc.AddFeature(new FilletFeature { Radius = "R", Edges = { new EdgeRef { A = plate.Id + ".Top", B = plate.Id + ".Side1", Hint = new Vec3(50, 0, 10) } } });
        doc.AddFeature(new LinearPatternFeature { Seeds = { doc.Features[3].Id }, Direction = new DirRef { Axis = "X" }, Spacing = "30", Count = "3" });
        var step = System.Text.Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
        doc.AddFile("imports/block.step", step);
        doc.AddFeature(new ImportFeature { File = "imports/block.step", Sha256 = DocumentContainer.Hash(step) });
        doc.Color = new[] { 0.1, 0.2, 0.3 };
        doc.Material = "Aluminium 6061";
        doc.Density = 2.7;
        doc.Units = UnitSystem.MMGS;
        doc.SketchVisibility = new() { [hole.Id] = false };
        doc.RollbackIndex = doc.Features.Count - 1;
        doc.DerivedFrom = CadDocument.NewGuid();
        doc.Library = new(StringComparer.Ordinal) { ["ISO273/M8"] = SpryCad.Core.Library.LibrarySet.BuiltIn.Snapshot("ISO273/M8")! };
        // sheet metal: a body (no features: its objects alone), a bend override, the profile rows it can use
        doc.SheetBodies = new() { new SpryCad.SheetMetal.SheetMetalBody { Id = "Sheet1", Name = "Sheet1", Material = "Steel CRS", Gauge = "16 ga", Overrides = new(StringComparer.Ordinal) { ["sm.allowance/k"] = "0.4" } } };
        doc.SheetOverrides = new() { new SpryCad.SheetMetal.SheetMetalOverride { Target = "EdgeFlange1.0.B", Values = { ["sm.tooling/inside radius"] = "2" } } };
        doc.SheetProfile = SpryCad.SheetMetal.ProfileSnapshot.Of(SpryCad.SheetMetal.StarterProfile.Profile, new[] { ("Steel CRS", "16 ga") });
        return doc;
    }

    public static void Run(TestRunner t)
    {
        t.Test("Storage: every object kind serializes, reads back and serializes to the same bytes (demos, samples, a rich part)", () =>
        {
            var dir = NewDir("roundtrip");
            var docs = new List<(string name, CadDocument doc)> { ("rich", Rich()) };
            docs.AddRange(Demos.All.Select(d => ("demo " + d.file, d.build())));
            docs.AddRange(Directory.GetFiles(Path.Combine(Root(), "samples", "demos"), "*.sprt").Order().Select(f => ("sample " + Path.GetFileName(f), CadDocument.Load(f))));
            var kinds = new HashSet<ObjectKind>();
            int n = 0;
            foreach (var (name, doc) in docs)
            {
                var before = PartObjects.From(doc);
                var path = Path.Combine(dir, $"{n++}.sprt");
                doc.Save(path);
                var back = CadDocument.Load(path);
                var after = PartObjects.From(back);
                t.True(before.Keys.OrderBy(k => k).SequenceEqual(after.Keys.OrderBy(k => k)), $"{name}: the same objects");
                foreach (var (id, bytes) in before)
                {
                    t.True(after[id].AsSpan().SequenceEqual(bytes), $"{name}: object {id} ({id.Kind}) changed through save and open");
                    kinds.Add(id.Kind);
                }
                // what the file holds is those bytes, by hash
                var stored = HeadObjects(path, doc);
                t.True(stored.Count == before.Count && before.All(kv => stored[kv.Key] == Hash.Of(kv.Value)), $"{name}: stored hashes");
                // and saving the reopened part changes nothing
                var rev = back.Revision;
                back.Save(path);
                t.Eq(back.Revision, rev);
                t.Eq(PartFile.History(path).Count, 1);
            }
            // (a part's kinds: an assembly's, 20 and up, are AssemblyStorageTests')
            foreach (var k in Enum.GetValues<ObjectKind>().Where(k => (int)k < 20)) t.True(kinds.Contains(k), $"no {k} object tested");

            // the rich part as a whole, with what the manifest keeps
            var rich = Rich();
            var rp = Path.Combine(dir, "rich.sprt");
            rich.Save(rp);
            var rb = CadDocument.Load(rp);
            t.Eq(Json(rb), Json(rich));
            t.Eq(rb.RollbackIndex, rich.RollbackIndex);
            t.Eq(rb.DerivedFrom, rich.DerivedFrom);
            t.True(rb.SketchVisibility != null && rb.SketchVisibility.Count == 1 && !rb.SketchVisibility.Values.Single(), "sketch visibility");
            t.True(rb.Files.Single().Value.SequenceEqual(rich.Files.Single().Value), "embedded file");
        });

        t.Test("Storage: saving with no changes makes no new revision", () =>
        {
            var dir = NewDir("nochange");
            var doc = Rich();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var rev = doc.Revision;
            var (blobs, revs, rows) = (Count(path, "Blobs"), Count(path, "Revisions"), Count(path, "RevisionObjects"));
            doc.Save(path);
            t.Eq(doc.Revision, rev);
            var reopened = CadDocument.Load(path);
            reopened.Save(path);
            t.Eq(reopened.Revision, rev);
            t.Eq(PartFile.History(path).Count, 1);
            t.Eq(DocumentContainer.ReadIdentity(path), (doc.Id, rev));
            t.True(Count(path, "Blobs") == blobs && Count(path, "Revisions") == revs && Count(path, "RevisionObjects") == rows, "rows added by a save that changed nothing");
        });

        t.Test("Storage: changing one feature parameter adds one blob and one revision", () =>
        {
            var dir = NewDir("onechange");
            var (doc, plate, _) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var objects0 = HeadObjects(path, doc);
            var (blobs, revs, rows) = (Count(path, "Blobs"), Count(path, "Revisions"), Count(path, "RevisionObjects"));
            plate.Depth = "12";
            doc.Save(path);
            var objects1 = HeadObjects(path, doc);
            var changed = Changed(objects0, objects1);
            t.True(changed.Count == 1 && changed[0].Equals(PartObjects.FeatureId(plate.Id)), "changed: " + string.Join(", ", changed));
            t.Eq(Count(path, "Blobs"), blobs + 1);
            t.Eq(Count(path, "Revisions"), revs + 1);
            t.Eq(Count(path, "RevisionObjects"), rows + objects1.Count);
            t.Eq(PartFile.History(path).Count, 2);
        });

        t.Test("Storage: reordering two independent features changes only the feature order", () =>
        {
            var dir = NewDir("reorder");
            var (doc, plate, hole) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var objects0 = HeadObjects(path, doc);
            var blobs = Count(path, "Blobs");
            // the plate's extrude and the hole's sketch do not depend on each other
            int a = doc.Features.IndexOf(plate), b = doc.Features.IndexOf(hole);
            t.Eq(b, a + 1);
            (doc.Features[a], doc.Features[b]) = (doc.Features[b], doc.Features[a]);
            t.True(new Regenerator().Rebuild(doc).Status.Values.All(s => s.Ok), "the reordered part rebuilds");
            doc.Save(path);
            var changed = Changed(objects0, HeadObjects(path, doc));
            t.True(changed.Count == 1 && changed[0].Equals(PartObjects.Single(ObjectKind.FeatureOrder)), "changed: " + string.Join(", ", changed));
            t.Eq(Count(path, "Blobs"), blobs + 1);
            t.Eq(CadDocument.Load(path).Features[a].Id, hole.Id);
        });

        t.Test("Storage: a compressed blob is keyed by the hash of its uncompressed bytes", () =>
        {
            var dir = NewDir("blobs");
            var path = Path.Combine(dir, "b.sprt");
            var payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i % 7} of a compressible payload\n")));
            t.True(payload.Length >= 1024, "a payload of 1 KB or more");
            var small = new byte[] { 1, 2, 3 };
            var png = new byte[2048]; new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }.CopyTo(png, 0);
            Hash h, hs, hp;
            using (var db = PartDatabase.Create(path))
            {
                h = db.Blobs.Put(payload);
                t.Eq(h, Hash.Of(payload));
                t.Eq(db.Blobs.Put(payload), h);  // stored once
                hs = db.Blobs.Put(small);
                hp = db.Blobs.Put(png);
                t.True(db.Blobs.Contains(h) && db.Blobs.Contains(hs) && !db.Blobs.Contains(Hash.Of(new byte[] { 9 })), "contains");
            }
            using (var c = Raw(path))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Codec, RawSize, length(Data) FROM Blobs WHERE Hash = $h";
                var p = cmd.Parameters.Add("$h", SqliteType.Blob);
                (long codec, long raw, long len) Row(Hash x)
                {
                    p.Value = x.ToBytes();
                    using var r = cmd.ExecuteReader();
                    t.True(r.Read(), "blob row");
                    return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
                }
                var row = Row(h);
                t.True(row.codec == 1 && row.raw == payload.Length && row.len < payload.Length, $"compressed: codec {row.codec}, {row.raw} -> {row.len} bytes");
                t.Eq(Row(hs).codec, 0L);   // too small to be worth it
                t.Eq(Row(hp).codec, 0L);   // compressed already
                using var all = c.CreateCommand();
                all.CommandText = "SELECT COUNT(*) FROM Blobs";
                t.Eq((long)all.ExecuteScalar()!, 3L);
            }
            using (var db = PartDatabase.Open(path))
            {
                var back = db.Blobs.Get(h);
                t.True(back.SequenceEqual(payload), "read back as it was");
                t.Eq(Hash.Of(back), h);
                t.True(db.Blobs.Get(hs).SequenceEqual(small) && db.Blobs.Get(hp).SequenceEqual(png), "uncompressed blobs read back");
            }
        });

        t.Test("Storage: the same file embedded twice is stored once", () =>
        {
            var dir = NewDir("dedupe");
            var doc = new CadDocument();
            var data = new byte[4096]; new Random(7).NextBytes(data);
            doc.AddFile("imports/a.step", data);
            doc.AddFile("imports/b.step", data.ToArray());
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var objects = HeadObjects(path, doc);
            t.Eq(objects[PartObjects.FileId("imports/a.step")], Hash.Of(data));
            t.Eq(objects[PartObjects.FileId("imports/b.step")], Hash.Of(data));
            using (var c = Raw(path))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Blobs WHERE Hash = $h";
                cmd.Parameters.AddWithValue("$h", Hash.Of(data).ToBytes());
                t.Eq((long)cmd.ExecuteScalar()!, 1L);
            }
            // imported again later (a third name, another save): still the one blob
            var blobs = Count(path, "Blobs");
            doc.AddFile("imports/c.step", data.ToArray());
            doc.Save(path);
            t.Eq(Count(path, "Blobs"), blobs + 1); // the settings object (it lists the names), not the file
            var back = CadDocument.Load(path);
            t.True(back.Files.Count == 3 && back.Files.Values.All(f => f.SequenceEqual(data)), "all three read back");
        });

        t.Test("Storage: tags set, listed, moved and deleted; a tag survives later saves", () =>
        {
            var dir = NewDir("tags");
            var (doc, plate, _) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var partId = Guid.Parse(doc.Id!);
            var rev1 = Guid.Parse(doc.Revision!);
            using (var db = PartDatabase.Open(path, readOnly: false))
            {
                db.SetTag(partId, "Released A", rev1);
                db.SetTag(partId, "Prototype", rev1);
                var tags = db.GetTags(partId);
                t.True(tags.Count == 2 && tags[0] == ("Prototype", rev1) && tags[1] == ("Released A", rev1), "listed by name");
                db.DeleteTag(partId, "Prototype");
                t.True(db.GetTags(partId).Single() == ("Released A", rev1), "deleted");
            }
            plate.Depth = "15";
            doc.Save(path);
            plate.Depth = "20";
            doc.Save(path);
            var rev3 = Guid.Parse(doc.Revision!);
            t.True(rev3 != rev1, "new revisions");
            using (var db = PartDatabase.Open(path, readOnly: false))
            {
                t.True(db.GetTags(partId).Single() == ("Released A", rev1), "the tag survives later saves, on its revision");
                t.True(db.GetRevision(rev1) != null, "the tagged revision kept");
                db.SetTag(partId, "Released A", rev3);
                t.True(db.GetTags(partId).Single() == ("Released A", rev3), "moved");
            }
            doc.Save(path); // nothing changed
            using (var db = PartDatabase.Open(path))
                t.True(db.GetTags(partId).Single() == ("Released A", rev3), "kept by a save that changed nothing");
        });

        t.Test("Storage: history of three saves, head first, parents chained, authors recorded", () =>
        {
            var dir = NewDir("history");
            var (doc, plate, _) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            var revs = new List<string>();
            foreach (var (depth, author) in new[] { ("10", "alice"), ("11", "bob"), ("12", "carol") })
            {
                plate.Depth = depth;
                doc.Save(path, author);
                revs.Add(doc.Revision!);
            }
            var h = PartFile.History(path);
            t.Eq(h.Count, 3);
            t.True(h.Select(r => r.RevisionId.ToString("D")).SequenceEqual(revs.AsEnumerable().Reverse()), "head first");
            t.True(h.Select(r => r.Author).SequenceEqual(new[] { "carol", "bob", "alice" }), "authors: " + string.Join(", ", h.Select(r => r.Author)));
            t.True(h[0].ParentId == h[1].RevisionId && h[1].ParentId == h[2].RevisionId && h[2].ParentId == null, "parents chained");
            t.True(h.All(r => r.PartId == Guid.Parse(doc.Id!) && r.Kind == RevisionKind.Save), "part and kind");
            t.True(h[0].CreatedUtc >= h[1].CreatedUtc && h[1].CreatedUtc >= h[2].CreatedUtc, "times in order");
            t.True(h.Select(r => r.RootHash).Distinct().Count() == 3, "three contents");
            // without an author: the default one
            plate.Depth = "13";
            doc.Save(path);
            t.Eq(PartFile.History(path)[0].Author, PartFile.DefaultAuthor);
            // the file's head revision reads back as it was saved
            using var db = PartDatabase.Open(path);
            var objects = db.GetObjects(h[1].RevisionId);
            t.Eq(PartDatabase.RootHash(objects), h[1].RootHash);
            var bob = PartObjects.ToDocument(objects.ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value)));
            t.Eq(((ExtrudeFeature)bob.Find(plate.Id)!).Depth, "11");
        });

        t.Test("Storage: Save As to a new path makes a new file; another part saved over a file replaces it", () =>
        {
            var dir = NewDir("saveas");
            var (doc, plate, _) = Plate();
            var a = Path.Combine(dir, "a.sprt");
            doc.Save(a);
            plate.Depth = "12";
            doc.Save(a);
            var headA = doc.Revision;
            var b = Path.Combine(dir, "b.sprt");
            doc.Save(b);
            t.True(File.Exists(b) && PartDatabase.IsFile(b), "a new file");
            t.Eq(PartFile.History(b).Count, 1);
            t.Eq(PartFile.History(a).Count, 2);
            t.Eq(DocumentContainer.ReadIdentity(a), (doc.Id, headA));
            t.Eq(CadDocument.Load(b).ToJson().Replace(doc.Revision!, headA), CadDocument.Load(a).ToJson());

            // a different part saved where a was
            var (other, _, _) = Plate();
            other.Equations[0].Expr = "90";
            other.Save(a);
            t.True(other.Id != doc.Id, "a different part");
            t.Eq(PartFile.History(a).Count, 1);
            t.Eq(DocumentContainer.ReadIdentity(a), (other.Id, other.Revision));
            t.Eq(Count(a, "Parts"), 1L);
            t.Eq(Count(a, "Revisions"), 1L);
            using (var db = PartDatabase.Open(a))
                t.True(db.GetHistory(Guid.Parse(doc.Id!)).Count == 0 && db.GetHead(Guid.Parse(doc.Id!)) == null, "the old part's history not mixed in");
            t.Eq(CadDocument.Load(a).Equations[0].Expr, "90");

            // and saving the first part back over it replaces it again
            doc.Save(a);
            t.Eq(PartFile.History(a).Count, 1);
            t.Eq(CadDocument.Load(a).Id, doc.Id);
        });

        t.Test("Storage: a save that fails part way leaves the old file", () =>
        {
            var dir = NewDir("crash");
            var (doc, plate, _) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var before = File.ReadAllBytes(path);
            var rev = doc.Revision;
            plate.Depth = "25";
            void Unchanged(string how)
            {
                t.True(File.ReadAllBytes(path).SequenceEqual(before), $"{how}: the old file changed");
                t.Eq(doc.Revision, rev);
                t.Eq(PartFile.History(path).Count, 1);
                t.Eq(((ExtrudeFeature)CadDocument.Load(path).Find(plate.Id)!).Depth, "10");
            }
            // the working copy cannot be made (something else is in its place)
            Directory.CreateDirectory(path + ".saving");
            t.True(Catch(() => doc.Save(path)) != null, "saved over a blocked working copy");
            Unchanged("working copy blocked");
            Directory.Delete(path + ".saving");
            if (OperatingSystem.IsWindows())
            {
                // everything written, but the finished copy cannot replace the file (held open without delete sharing)
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    t.True(Catch(() => doc.Save(path)) != null, "replaced a file held open");
                Unchanged("final move refused");
                t.True(!File.Exists(path + ".saving"), "the working copy left behind");
            }
            // and the next save goes through
            doc.Save(path);
            t.Eq(PartFile.History(path).Count, 2);
            t.Eq(((ExtrudeFeature)CadDocument.Load(path).Find(plate.Id)!).Depth, "25");
        });

        t.Test("Storage: the file is SQLite with SpryCAD's application id; manifest and thumbnail readable with plain SQL", () =>
        {
            var dir = NewDir("format");
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var r = new Regenerator().Rebuild(doc);
            doc.Thumbnail = DocumentContainer.RenderThumbnail(doc, r);
            t.True(doc.Thumbnail != null, "a thumbnail");
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            var head = new byte[100];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
            t.True(head.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8), "SQLite header");
            t.Eq(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(68)), 0x53505259);
            t.Eq(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(60)), PartDatabase.SchemaVersion); // user_version
            t.True(PartDatabase.Is(head) && PartDatabase.IsFile(path), "recognized");
            t.True(Scalar(path, "SELECT Png FROM Thumbnail") is byte[] png && png.SequenceEqual(doc.Thumbnail!), "thumbnail by plain SQL");
            t.Eq((long)Scalar(path, "SELECT Size FROM Thumbnail")!, 256L);
            t.Eq(Scalar(path, "SELECT Value FROM Manifest WHERE Key = 'RootPartId'") as string, doc.Id);
            t.Eq(Scalar(path, "SELECT Value FROM Manifest WHERE Key = 'Units'") as string, doc.Units.ToString());
            t.Eq((long)Scalar(path, "PRAGMA application_id")!, (long)PartDatabase.ApplicationId);
            // not SpryCAD's: another SQLite database, a text file
            var other = Path.Combine(dir, "other.db");
            using (var c = Raw(other)) { using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE T (X INTEGER)"; cmd.ExecuteNonQuery(); }
            t.True(!PartDatabase.IsFile(other), "a plain SQLite database taken for a part");
            File.WriteAllText(Path.Combine(dir, "x.sprt"), "not a part");
            t.True(!PartDatabase.IsFile(Path.Combine(dir, "x.sprt")), "text taken for a part");
        });

        t.Test("Storage: a file from a newer SpryCAD (higher schema version) is refused with a clear message", () =>
        {
            var dir = NewDir("newer");
            var (doc, _, _) = Plate();
            var path = Path.Combine(dir, "p.sprt");
            doc.Save(path);
            using (var c = Raw(path)) { using var cmd = c.CreateCommand(); cmd.CommandText = $"PRAGMA user_version = {PartDatabase.SchemaVersion + 1}"; cmd.ExecuteNonQuery(); }
            var ex = Catch(() => CadDocument.Load(path));
            t.True(ex is InvalidDataException && ex.Message.Contains("newer SpryCAD") && ex.Message.Contains((PartDatabase.SchemaVersion + 1).ToString()), "not refused clearly: " + (ex?.Message ?? "it opened"));
            t.True(Catch(() => PartDatabase.Open(path)) is InvalidDataException, "the database opened");
            t.Eq(DocumentContainer.ReadIdentity(path), ((string?)null, (string?)null));
        });

        t.Test("Storage: the old-format test models (ZIP containers and plain JSON) still open and convert", () =>
        {
            var dir = NewDir("legacy");
            var files = Directory.GetFiles(Path.Combine(Root(), "tests", "SpryCad.Tests", "models"), "*.sprt").Order().ToList();
            int zips = 0, plain = 0;
            foreach (var f in files)
            {
                var b = File.ReadAllBytes(f);
                t.True(!PartDatabase.Is(b), $"{Path.GetFileName(f)} is not an old-format fixture");
                if (b[0] == 'P' && b[1] == 'K') zips++; else plain++;
                var doc = CadDocument.Load(f);
                t.True(doc.Features.Count > 0, $"{Path.GetFileName(f)}: features");
                var objects = PartObjects.From(doc);
                var path = Path.Combine(dir, Path.GetFileName(f));
                doc.Save(path);
                t.True(PartDatabase.IsFile(path), "converted to a database");
                var back = CadDocument.Load(path);
                var again = PartObjects.From(back);
                t.True(objects.Count == again.Count && objects.All(kv => again.TryGetValue(kv.Key, out var x) && x.AsSpan().SequenceEqual(kv.Value)), $"{Path.GetFileName(f)}: the same part after conversion");
                t.Eq(back.Id, doc.Id);
            }
            t.True(zips > 0 && plain > 0, $"fixtures of both old kinds ({zips} ZIP, {plain} JSON)");
        });

        t.Test("Storage: an assembly that embeds a part opens the embedded part (a database inside the assembly)", () =>
        {
            var dir = NewDir("embed");
            var (part, _, _) = Plate();
            var m = AssemblyModel.New();
            var c = m.InsertEmbedded(part, "Plate", Transform.Identity);
            var key = c.Part.Embedded!;
            t.True(PartDatabase.Is(m.Doc.Files[key]), "the embedded part is a database");
            t.True(part.Id != null && c.Part.PartId == part.Id && c.Part.Revision == part.Revision, "identity and revision given");
            var inner = DocumentContainer.Load(m.Doc.Files[key], "Plate");
            t.True(inner.Id == part.Id && inner.Revision == part.Revision && inner.Features.Count == part.Features.Count, "read from its bytes");
            var path = Path.Combine(dir, "A.sasm");
            m.Save(path);
            var back = AssemblyModel.Open(AssemblyDocument.Load(path), path, new PartIndex());
            var shown = back.DisplayOf(back.Doc.Components.Single())!;
            t.True(shown.Source == PartSource.Embedded && shown.Problem == null, $"opened from the assembly ({shown.Source}: {shown.Problem})");
            t.Eq(shown.Doc!.Id, part.Id);
            t.Eq(Json(shown.Doc), Json(part));
            t.Near(shown.Mesh.Volume, new Regenerator().Rebuild(part).State.Bodies.Sum(b => b.Mesh.Volume), 1e-6);
        });
    }
}

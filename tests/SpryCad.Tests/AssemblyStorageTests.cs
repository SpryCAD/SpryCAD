using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Assembly files as databases (Core/Storage/AssemblyFile): revisions of the assembly, its embedded parts and
/// sub-assemblies as documents of the same database with their own history, display caches, the state kept beside the
/// revisions, and old ZIP assemblies converted.
/// </summary>
static class AssemblyStorageTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-asm-storage-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
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
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    static long Count(string path, string sql, params (string name, object value)[] ps)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>The revisions (not autosaves) a database holds for one document.</summary>
    static long RevisionRows(string path, string id) =>
        Count(path, "SELECT COUNT(*) FROM Revisions WHERE PartId = $p AND Kind <> 1", ("$p", Guid.Parse(id).ToByteArray()));

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>Bytes written to a new file in <paramref name="dir"/> (a document's database, to open as a file).</summary>
    static string ToFile(string dir, byte[] bytes, string ext)
    {
        var p = Path.Combine(dir, Guid.NewGuid().ToString("N")[..8] + ext);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>
    /// An assembly's JSON as compared: its revision left out (a database's head id, not the old container's GUID), the
    /// old containers' list of attachments too, the dictionaries in one order (the order means nothing).
    /// </summary>
    static string AsmJson(AssemblyDocument d)
    {
        var c = AssemblyDocument.FromJson(d.ToJson(indented: false));
        c.Revision = null;
        c.Attachments = null;
        c.MateCounters = c.MateCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        c.Caches = c.Caches.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        if (c.Configurations is { Count: 0 }) c.Configurations = null;
        if (c.Explodes is { Count: 0 }) c.Explodes = null;
        return c.ToJson();
    }

    /// <summary>A part's JSON as compared (as <see cref="AsmJson"/>).</summary>
    static string PartJson(CadDocument d)
    {
        var c = CadDocument.FromJson(d.ToJson(indented: false));
        c.Revision = null;
        c.Attachments = null;
        c.NameCounters = c.NameCounters.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary();
        return c.ToJson();
    }

    static bool IsAssemblyName(string name) => name.EndsWith(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase);
    static bool IsDocumentName(string name) => !name.StartsWith("cache/", StringComparison.Ordinal)
        && (IsAssemblyName(name) || name.EndsWith(CadDocument.FileExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>An embedded document as compared: its model's JSON (its embedded files' too, for a sub-assembly).</summary>
    static string EmbeddedJson(string name, byte[] bytes)
    {
        if (!IsAssemblyName(name)) return PartJson(DocumentContainer.Load(bytes, name));
        var sd = AssemblyContainer.Load(bytes, name);
        return AsmJson(sd) + string.Concat(sd.Files.Where(f => IsDocumentName(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => "\n" + f.Key + ":\n" + EmbeddedJson(f.Key, f.Value)));
    }

    static string MatesJson(AssemblyDocument d) => JsonSerializer.Serialize(d.Mates, Serialization.Options);

    /// <summary>A small part (an equation and a sketch: saved without rebuilding), its width W.</summary>
    static CadDocument SmallPart(string w)
    {
        var d = new CadDocument();
        d.Equations.Add(new Equation { Name = "W", Expr = w });
        d.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60, "W") });
        return d;
    }

    /// <summary>A part saved <paramref name="revisions"/> times (W changed each time) to a file; its bytes and the part.</summary>
    static (byte[] bytes, CadDocument doc, string path) PartWithHistory(string dir, int revisions, string name = "X")
    {
        var path = Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N")[..6] + ".sprt");
        var d = SmallPart("100");
        for (int i = 0; i < revisions; i++)
        {
            d.Equations[0].Expr = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.Save(path);
        }
        return (File.ReadAllBytes(path), d, path);
    }

    /// <summary>A pattern, configurations, an exploded view, units and state given to an assembly (it has components).</summary>
    static void Enrich(AssemblyDocument doc)
    {
        var cs = doc.Components;
        var last = cs[^1];
        last.Hidden = true;
        cs[0].Lightweight = true;
        last.Part.AbsolutePath = "C:/elsewhere/x" + CadDocument.FileExtension;
        if (cs.Count > 1) cs[1].Suppressed = true;
        cs[0].Color = new[] { 0.25, 0.5, 0.75 };
        doc.Patterns ??= new();
        doc.Patterns.Add(new ComponentPattern
        {
            Id = "P" + doc.NextPatternId++, Name = "LinearPattern" + (doc.Patterns.Count + 1), Kind = PatternKind.Linear,
            Seeds = { last.Id },
            Reference = new MateRef { Component = cs[0].Id, Face = "Face1", Kind = MateGeomKind.Axis, P = new Vec3(1, 2, 3), D = Vec3.UnitZ, R = 2 },
            Count = 2, Spacing = 12.5,
            Instances = { new PatternInstance { Seed = last.Id, Index = 1, Component = last.Id } },
        });
        doc.Configurations ??= new() { new AssemblyConfiguration { Name = "Default" } };
        doc.Configurations.Add(new AssemblyConfiguration
        {
            Name = "Lite", Suppressed = new() { last.Id }, Hidden = new() { cs[0].Id },
            Parameters = new() { [cs[0].Id] = new() { ["W"] = "20" } },
        });
        doc.ActiveConfiguration = "Lite";
        doc.Explodes = new() { new ExplodeView { Name = "Exploded View1", Steps = { new ExplodeStep { Components = { cs[0].Id }, Direction = new Vec3(0, 0, 1), Distance = 30 } } } };
        doc.Units = UnitSystem.IPS;
    }

    static void CheckDatabase(TestRunner t, string path, string what)
    {
        var b = File.ReadAllBytes(path);
        t.True(b.Length > 100 && System.Text.Encoding.ASCII.GetString(b, 0, 16) == "SQLite format 3\0", $"{what}: SQLite's header");
        t.Eq(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(68)), PartDatabase.ApplicationId);
        using var db = PartDatabase.Open(path);
        t.True(db.Manifest("RootKind") == AssemblyFile.RootKindAssembly, $"{what}: RootKind {db.Manifest("RootKind") ?? "(none)"}");
    }

    /// <summary>An assembly written as assemblies were before databases: a ZIP container (mimetype, assembly.json, attachments, thumbnail).</summary>
    static void WriteLegacyZip(string path, AssemblyDocument asm)
    {
        asm.Attachments = asm.Files.Count == 0 ? null
            : asm.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new AttachmentInfo { Name = f.Key, Size = f.Value.LongLength, Sha256 = DocumentContainer.Hash(f.Value) }).ToList();
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        DocumentContainer.WriteMime(zip, DocumentContainer.AssemblyMime);
        void Entry(string name, byte[] data)
        {
            using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            s.Write(data);
        }
        Entry(AssemblyContainer.ModelEntry, System.Text.Encoding.UTF8.GetBytes(asm.ToJson()));
        foreach (var a in asm.Attachments ?? new()) Entry(DocumentContainer.AttachmentFolder + a.Name, asm.Files[a.Name]);
        if (asm.Thumbnail != null) Entry(DocumentContainer.ThumbnailEntry, asm.Thumbnail);
        asm.Attachments = null;
    }

    /// <summary>An assembly of two components of external parts (no mates).</summary>
    static AssemblyDocument TwoParts()
    {
        var asm = new AssemblyDocument();
        asm.AddComponent(new PartRef { PartId = CadDocument.NewGuid(), Revision = CadDocument.NewGuid(), Path = "Bolt.sprt" }, Transform.Identity);
        asm.AddComponent(new PartRef { PartId = CadDocument.NewGuid(), Revision = CadDocument.NewGuid(), Path = "Nut.sprt" }, Transform.Translation(new Vec3(0, 0, 50)));
        return asm;
    }

    static Mate NewMate(AssemblyDocument asm) => asm.AddMate(new Mate
    {
        Type = MateType.Concentric,
        A = new MateRef { Component = asm.Components[0].Id, Face = "F1.Side", Kind = MateGeomKind.Axis, D = Vec3.UnitZ, R = 5 },
        B = new MateRef { Component = asm.Components[1].Id, Face = "F2.Bore", Kind = MateGeomKind.Axis, D = Vec3.UnitZ, R = 5 },
        Aligned = true,
    });

    static List<ObjectChange> HeadDiff(string path, string from, string to)
    {
        using var db = PartDatabase.Open(path);
        return PartHistory.Diff(db, Guid.Parse(from), Guid.Parse(to));
    }

    static string Show(IEnumerable<ObjectChange> cs) => string.Join(", ", cs.Select(c => $"{c.Kind} {c.ObjectId.Kind}"));

    public static void Run(TestRunner t)
    {
        t.Test("Assembly files: the samples (ZIP) load, save as databases and load back the same: components, mates, patterns, configurations, explodes, state, caches, embedded documents, preview", () =>
        {
            var dir = NewDir("samples");
            var samples = Directory.GetFiles(Path.Combine(Root(), "samples", "demos"), "*.sasm").Order().ToList();
            t.True(samples.Count > 0, "no sample assemblies");
            foreach (var src in samples)
            {
                var name = Path.GetFileName(src);
                var copy = Path.Combine(dir, "old-" + name);
                File.Copy(src, copy);
                var doc = AssemblyDocument.Load(copy);
                Enrich(doc);
                var files = new Dictionary<string, byte[]>(doc.Files, StringComparer.Ordinal);
                var path = Path.Combine(dir, name);
                doc.Save(path);
                CheckDatabase(t, path, name);
                var back = AssemblyDocument.Load(path);
                t.True(back.Id == doc.Id && back.Revision == doc.Revision && back.Revision != null, $"{name}: identity and revision");
                t.True(back.Components.Select(c => (c.Id, c.Name, c.Fixed, c.Suppressed, c.Hidden, c.Lightweight, c.Part.PartId, c.Part.Revision, c.Part.Path, c.Part.AbsolutePath, c.Part.Embedded, c.Part.Assembly))
                    .SequenceEqual(doc.Components.Select(c => (c.Id, c.Name, c.Fixed, c.Suppressed, c.Hidden, c.Lightweight, c.Part.PartId, c.Part.Revision, c.Part.Path, c.Part.AbsolutePath, c.Part.Embedded, c.Part.Assembly))), $"{name}: components");
                t.True(back.Components.Zip(doc.Components).All(p => p.First.Placement.R.SequenceEqual(p.Second.Placement.R) && Vec3.Distance(p.First.Placement.T, p.Second.Placement.T) == 0), $"{name}: placements");
                t.Eq(MatesJson(back), MatesJson(doc));
                t.True(back.Units == UnitSystem.IPS && back.ActiveConfiguration == "Lite", $"{name}: units and active configuration");
                t.True((back.NextComponentId, back.NextMateId, back.NextPatternId) == (doc.NextComponentId, doc.NextMateId, doc.NextPatternId), $"{name}: counters");
                t.Eq(AsmJson(back), AsmJson(doc)); // (everything else: patterns, configurations, explodes, mate counters)
                // display caches: the same keys, names and bytes
                t.True(back.Caches.OrderBy(kv => kv.Key).SequenceEqual(doc.Caches.OrderBy(kv => kv.Key)), $"{name}: cache keys");
                foreach (var (k, f) in doc.Caches) t.True(back.Files[f].AsSpan().SequenceEqual(files[f]), $"{name}: cache {k}");
                // embedded documents: each a database now, the same model
                t.True(back.Files.Keys.Order(StringComparer.Ordinal).SequenceEqual(files.Keys.Order(StringComparer.Ordinal)), $"{name}: files {string.Join(", ", back.Files.Keys)}");
                foreach (var (f, bytes) in files.Where(f => IsDocumentName(f.Key)))
                {
                    t.True(PartDatabase.Is(back.Files[f]), $"{name}: {f} saved as a database");
                    t.Eq(EmbeddedJson(f, back.Files[f]), EmbeddedJson(f, bytes));
                }
                t.True(doc.Thumbnail != null && back.Thumbnail != null && back.Thumbnail.SequenceEqual(doc.Thumbnail), $"{name}: preview");
                // saved again as it was read: nothing new
                var revs = RevisionRows(path, doc.Id!);
                back.Save(path);
                t.True(back.Revision == doc.Revision && RevisionRows(path, doc.Id!) == revs && PartFile.History(path).Count == 1, $"{name}: saved again, a revision");
            }
        });

        t.Test("Assembly files: a save that changes nothing adds no revision; moving a component, adding a mate change those objects; hiding one only its state", () =>
        {
            var dir = NewDir("revisions");
            var path = Path.Combine(dir, "a.sasm");
            var asm = TwoParts();
            asm.Save(path);
            var r1 = asm.Revision!;
            t.True(r1 != null && PartFile.History(path).Count == 1, "a first revision");
            var blobs = Count(path, "SELECT COUNT(*) FROM Blobs");
            // unchanged: none, the revision kept (and reopened and saved: none)
            asm.Save(path);
            t.Eq(asm.Revision, r1);
            var reopened = AssemblyDocument.Load(path);
            reopened.Save(path);
            t.Eq(reopened.Revision, r1);
            t.True(PartFile.History(path).Count == 1 && Count(path, "SELECT COUNT(*) FROM Blobs") == blobs, "rows added by a save that changed nothing");

            // moved: one revision, its component changed
            asm.Components[1].Placement = Placement.From(Transform.Translation(new Vec3(5, 0, 50)));
            asm.Save(path);
            var r2 = asm.Revision!;
            t.True(r2 != r1 && PartFile.History(path).Count == 2, "moving a component: a revision");
            var d = HeadDiff(path, r1, r2);
            t.True(d.Count == 1 && d[0].Kind == ChangeKind.Modified && d[0].ObjectId.Equals(PartObjects.IdOf(ObjectKind.Component, asm.Components[1].Id)), "moved: " + Show(d));

            // a mate: added, the order and the counters it is numbered by changed
            var mate = NewMate(asm);
            asm.Save(path);
            var r3 = asm.Revision!;
            d = HeadDiff(path, r2, r3);
            t.True(d.Any(c => c.Kind == ChangeKind.Added && c.ObjectId.Equals(PartObjects.IdOf(ObjectKind.Mate, mate.Id))), "the mate added: " + Show(d));
            t.True(d.Any(c => c.Kind == ChangeKind.Modified && c.ObjectId.Equals(PartObjects.Single(ObjectKind.AssemblyOrder))), "the order changed: " + Show(d));
            t.True(d.All(c => c.ObjectId.Kind is ObjectKind.Mate or ObjectKind.AssemblyOrder or ObjectKind.AssemblyCounters) && d.Count <= 3, "nothing else: " + Show(d));

            // hidden, made lightweight, the part's place and revision seen: no revision, but kept
            var c2 = asm.Components[1];
            c2.Hidden = true; c2.Lightweight = true; c2.Part.Path = "sub/Nut.sprt"; c2.Part.AbsolutePath = "C:/p/sub/Nut.sprt"; c2.Part.Revision = CadDocument.NewGuid();
            asm.Save(path);
            t.Eq(asm.Revision, r3);
            t.Eq(PartFile.History(path).Count, 3);
            var back = AssemblyDocument.Load(path);
            var b2 = back.Find(c2.Id)!;
            t.True(b2.Hidden && b2.Lightweight && b2.Part.Path == c2.Part.Path && b2.Part.AbsolutePath == c2.Part.AbsolutePath && b2.Part.Revision == c2.Part.Revision, "the state kept");
            t.True(!back.Components[0].Hidden && !back.Components[0].Lightweight, "the other component's state as it was");
            t.Eq(back.Revision, r3);
            t.Eq(AsmJson(back), AsmJson(asm));
            using (var db = PartDatabase.Open(path))
                t.True(db.Manifest("AssemblyState") is { } s && s.Contains(c2.Part.Revision!), "the state in the manifest");
        });

        t.Test("Assembly files: every assembly object kind serializes, reads back and serializes to the same bytes, stored by hash", () =>
        {
            var dir = NewDir("objects");
            var asm = TwoParts();
            var (pb, part, _) = PartWithHistory(dir, 1, "Embedded");
            asm.Files["parts/Embedded.sprt"] = pb;
            asm.AddComponent(new PartRef { PartId = part.Id, Revision = part.Revision, Embedded = "parts/Embedded.sprt" }, Transform.Rotation(Vec3.Zero, Vec3.UnitX, 0.3), "Embedded");
            NewMate(asm);
            Enrich(asm);
            asm.MateCounters["Lock"] = 7;
            var embedded = new Dictionary<string, string> { ["parts/Embedded.sprt"] = part.Id! };
            var before = AssemblyObjects.From(asm, embedded);
            var kinds = before.Keys.Select(k => k.Kind).ToHashSet();
            foreach (var k in Enum.GetValues<ObjectKind>().Where(k => (int)k >= 20)) t.True(kinds.Contains(k), $"no {k} object tested");
            t.True(kinds.All(k => (int)k >= 20), "an assembly has only assembly objects");
            var path = Path.Combine(dir, "a.sasm");
            asm.Save(path);
            var back = AssemblyDocument.Load(path);
            var ids = back.Files.Where(f => IsDocumentName(f.Key)).ToDictionary(f => f.Key, f => DocumentContainer.Load(f.Value, f.Key).Id!);
            t.True(ids.Count == 1 && ids["parts/Embedded.sprt"] == part.Id, "the embedded part keeps its identity");
            var after = AssemblyObjects.From(back, ids);
            t.True(before.Keys.Order().SequenceEqual(after.Keys.Order()), "the same objects");
            foreach (var (id, bytes) in before)
                t.True(after[id].AsSpan().SequenceEqual(bytes), $"object {id} ({id.Kind}) changed through save and open:\n{System.Text.Encoding.UTF8.GetString(bytes)}\n{System.Text.Encoding.UTF8.GetString(after[id])}");
            using (var db = PartDatabase.Open(path))
            {
                var stored = db.GetObjects(Guid.Parse(back.Revision!));
                t.True(stored.Count == before.Count && before.All(kv => stored.TryGetValue(kv.Key, out var h) && h == Hash.Of(kv.Value)), "stored hashes");
            }
            // and read back as an assembly, they make the same document
            var (doc2, emb2) = AssemblyObjects.ToDocument(before);
            t.True(emb2.Count == 1 && emb2["parts/Embedded.sprt"] == part.Id, "the embedded names");
            AssemblyObjects.ApplyState(doc2, AssemblyObjects.State(asm));
            doc2.Id = asm.Id; doc2.DerivedFrom = asm.DerivedFrom;
            t.Eq(AsmJson(doc2), AsmJson(asm));
        });

        t.Test("Assembly files: an embedded part keeps its history (revisions, tags, state) in the assembly's database, and goes on with it", () =>
        {
            var dir = NewDir("embedded-history");
            var (pb, part, partPath) = PartWithHistory(dir, 3, "Bracket");
            var origin = CadDocument.NewGuid();
            part.DerivedFrom = origin;
            part.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 4, 5, 6 };
            part.Save(partPath); // (state only: no revision)
            using (var db = PartDatabase.Open(partPath, readOnly: false)) db.SetTag(Guid.Parse(part.Id!), "v1", db.GetHistory(Guid.Parse(part.Id!))[^1].RevisionId);
            pb = File.ReadAllBytes(partPath);
            var history = PartFile.History(partPath).Select(r => r.RevisionId).ToList();
            t.Eq(history.Count, 3);

            var asm = TwoParts();
            asm.Files["parts/Bracket.sprt"] = pb;
            asm.AddComponent(new PartRef { PartId = part.Id, Revision = part.Revision, Embedded = "parts/Bracket.sprt" }, Transform.Identity, "Bracket");
            var path = Path.Combine(dir, "a.sasm");
            asm.Save(path);
            t.Eq(RevisionRows(path, part.Id!), 3L);
            using (var db = PartDatabase.Open(path))
                t.True(db.Manifest($"doc/{part.Id}/DerivedFrom") == origin, "the part's state under its id");

            var back = AssemblyDocument.Load(path);
            var file = ToFile(dir, back.Files["parts/Bracket.sprt"], ".sprt");
            t.True(PartFile.History(file).Select(r => r.RevisionId).SequenceEqual(history), "the same three revisions");
            using (var db = PartDatabase.Open(file))
            {
                t.True(db.GetTags(Guid.Parse(part.Id!)).Select(x => x.Name).SequenceEqual(new[] { "v1" }), "its tag");
                t.Eq(db.Manifest("RootKind"), AssemblyFile.RootKindPart);
            }
            var embedded = CadDocument.Load(file);
            t.True(embedded.Id == part.Id && embedded.Revision == part.Revision && embedded.DerivedFrom == origin, "identity, revision and state");
            var preview = embedded.Thumbnail;
            t.Eq(PartJson(embedded), PartJson(part));

            // edited and saved where the assembly keeps it: a fourth revision, in the assembly's database
            embedded.Equations[0].Expr = "150";
            embedded.Save(file);
            t.Eq(PartFile.History(file).Count, 4);
            back.Files["parts/Bracket.sprt"] = File.ReadAllBytes(file);
            back.Find("C3")!.Part.Revision = embedded.Revision;
            var asmRev = back.Revision;
            back.Save(path);
            t.Eq(RevisionRows(path, part.Id!), 4L);
            t.Eq(back.Revision, asmRev); // (the assembly itself did not change: the component's revision seen is state)
            var again = AssemblyDocument.Load(path);
            var file2 = ToFile(dir, again.Files["parts/Bracket.sprt"], ".sprt");
            var h2 = PartFile.History(file2);
            t.True(h2.Count == 4 && h2.Skip(1).Select(r => r.RevisionId).SequenceEqual(history) && h2[0].RevisionId.ToString("D") == embedded.Revision, "four revisions, the first three as they were");
            t.Eq(CadDocument.Load(file2).Equations[0].Expr, "150");
            // its preview kept (the part as it was saved: copied out whole, as the old containers kept it)
            t.True(preview != null && preview.SequenceEqual(part.Thumbnail), "the embedded part's preview lost");
        });

        t.Test("Assembly files: an embedded sub-assembly with its own embedded part comes back with both histories; unchanged it keeps its revision", () =>
        {
            var dir = NewDir("embedded-sub");
            var (pb, part, _) = PartWithHistory(dir, 2, "Pin");
            var sub = new AssemblyDocument();
            sub.Files["parts/Pin.sprt"] = pb;
            sub.AddComponent(new PartRef { PartId = part.Id, Revision = part.Revision, Embedded = "parts/Pin.sprt" }, Transform.Identity, "Pin");
            sub.AddComponent(new PartRef { PartId = part.Id, Revision = part.Revision, Embedded = "parts/Pin.sprt" }, Transform.Translation(new Vec3(10, 0, 0)), "Pin");
            sub.Components[1].Hidden = true;
            sub.Files["cache/pin.mesh"] = new byte[] { 4, 5, 6 };
            sub.Caches[AssemblyDocument.CacheKey(part.Id!, part.Revision)] = "cache/pin.mesh";
            var subBytes = AssemblyContainer.ToBytes(sub);
            var subRev = sub.Revision!;
            t.True(PartDatabase.Is(subBytes) && sub.Id != null, "the sub-assembly as a database");

            var top = TwoParts();
            top.Files["subs/PinPair.sasm"] = subBytes;
            top.AddComponent(new PartRef { PartId = sub.Id, Revision = subRev, Embedded = "subs/PinPair.sasm", Assembly = true }, Transform.Identity, "PinPair");
            var path = Path.Combine(dir, "top.sasm");
            top.Save(path);
            t.True(RevisionRows(path, sub.Id!) == 1 && RevisionRows(path, part.Id!) == 2, "both in the top's database");

            var back = AssemblyDocument.Load(path);
            var sb = back.Files["subs/PinPair.sasm"];
            using (var db = PartDatabase.Open(ToFile(dir, sb, ".sasm")))
                t.True(db.Manifest("RootKind") == AssemblyFile.RootKindAssembly && db.GetHistory(Guid.Parse(sub.Id!)).Count == 1 && db.GetHistory(Guid.Parse(part.Id!)).Count == 2, "the sub-assembly's database: both histories");
            var sd = AssemblyContainer.Load(sb, "subs/PinPair.sasm");
            t.True(sd.Id == sub.Id && sd.Revision == subRev, "the sub-assembly's identity and revision");
            t.True(sd.Components[1].Hidden && !sd.Components[0].Hidden, "its state");
            t.True(sd.Caches.Count == 1 && sd.Files["cache/pin.mesh"].SequenceEqual(new byte[] { 4, 5, 6 }), "its caches");
            var pinFile = ToFile(dir, sd.Files["parts/Pin.sprt"], ".sprt");
            t.True(PartFile.History(pinFile).Count == 2 && CadDocument.Load(pinFile).Revision == part.Revision, "its part, with its history");
            t.Eq(EmbeddedJson("subs/PinPair.sasm", sb), EmbeddedJson("subs/PinPair.sasm", subBytes));

            // unchanged, made bytes again from the bytes it had: the same revision, no new one
            var again = AssemblyContainer.ToBytes(sd, sb);
            t.Eq(sd.Revision, subRev);
            using (var db = PartDatabase.Open(ToFile(dir, again, ".sasm"))) t.Eq(db.GetHistory(Guid.Parse(sub.Id!)).Count, 1);
            // changed: a second revision, the first kept
            sd.Components[1].Placement = Placement.From(Transform.Translation(new Vec3(20, 0, 0)));
            var changed = AssemblyContainer.ToBytes(sd, sb);
            t.True(sd.Revision != subRev, "changed: a new revision");
            using (var db = PartDatabase.Open(ToFile(dir, changed, ".sasm")))
            {
                var h = db.GetHistory(Guid.Parse(sub.Id!));
                t.True(h.Count == 2 && h[1].RevisionId.ToString("D") == subRev, "two revisions, the first as it was");
                t.Eq(db.GetHistory(Guid.Parse(part.Id!)).Count, 2);
            }
            // put back in the top and saved: the top's database has the sub-assembly's two revisions
            back.Files["subs/PinPair.sasm"] = changed;
            back.Save(path);
            t.True(RevisionRows(path, sub.Id!) == 2 && RevisionRows(path, part.Id!) == 2, "the top's database: the sub-assembly's two revisions, its part's two");
        });

        t.Test("Assembly files: display caches saved and loaded (keys, names, bytes); one dropped is gone after the next save, its blob too unless shared", () =>
        {
            var dir = NewDir("caches");
            var asm = TwoParts();
            byte[] a = { 1, 1, 1, 1 }, b = { 2, 2, 2 };
            string ka = AssemblyDocument.CacheKey("A", "r1"), kb = AssemblyDocument.CacheKey("B", "r1", "Long"), kc = AssemblyDocument.CacheKey("C", "r2");
            asm.Files["cache/a.mesh"] = a; asm.Files["cache/b.mesh"] = b; asm.Files["cache/c.mesh"] = (byte[])a.Clone();
            asm.Caches[ka] = "cache/a.mesh"; asm.Caches[kb] = "cache/b.mesh"; asm.Caches[kc] = "cache/c.mesh";
            var path = Path.Combine(dir, "a.sasm");
            asm.Save(path);
            var rev = asm.Revision;
            t.Eq(Count(path, "SELECT COUNT(*) FROM Cache WHERE Kind = 1"), 3L);
            using (var db = PartDatabase.Open(path)) t.True(db.Manifest("AssemblyCaches") != null, "listed in the manifest");
            var back = AssemblyDocument.Load(path);
            t.True(back.Caches.OrderBy(kv => kv.Key).SequenceEqual(asm.Caches.OrderBy(kv => kv.Key)), "the same keys and names");
            foreach (var (k, f) in asm.Caches) t.True(back.Files[f].SequenceEqual(asm.Files[f]), $"cache {k}'s bytes");
            t.Eq(back.Files.Count, 3);

            long BlobsOf(byte[] data) => Count(path, "SELECT COUNT(*) FROM Blobs WHERE Hash = $h", ("$h", Hash.Of(data).ToBytes()));
            // b dropped (as the assembly drops one: key and file): gone, its blob too
            back.Caches.Remove(kb); back.Files.Remove("cache/b.mesh");
            back.Save(path);
            t.Eq(back.Revision, rev); // (caches are not in revisions)
            var b2 = AssemblyDocument.Load(path);
            t.True(!b2.Caches.ContainsKey(kb) && !b2.Files.ContainsKey("cache/b.mesh") && b2.Caches.Count == 2, "dropped: " + string.Join(", ", b2.Caches.Keys));
            t.Eq(BlobsOf(b), 0L);
            // c dropped: its bytes are a's as well, kept for a
            b2.Caches.Remove(kc); b2.Files.Remove("cache/c.mesh");
            b2.Save(path);
            var b3 = AssemblyDocument.Load(path);
            t.True(b3.Caches.Keys.SequenceEqual(new[] { ka }) && b3.Files.Keys.SequenceEqual(new[] { "cache/a.mesh" }) && b3.Files["cache/a.mesh"].SequenceEqual(a), "only a left");
            t.Eq(BlobsOf(a), 1L);
            t.Eq(Count(path, "SELECT COUNT(*) FROM Cache WHERE Kind = 1"), 1L);
            // all dropped: none listed
            b3.Caches.Clear(); b3.Files.Clear();
            b3.Save(path);
            using (var db = PartDatabase.Open(path)) t.True(db.Manifest("AssemblyCaches") == null, "none listed");
            t.True(BlobsOf(a) == 0 && AssemblyDocument.Load(path).Files.Count == 0, "and none kept");
        });

        t.Test("Assembly files: an old ZIP assembly with an embedded old part converts on save, the part's history starting there", () =>
        {
            var dir = NewDir("convert");
            var part = SmallPart("80");
            part.Id = CadDocument.NewGuid(); part.Revision = CadDocument.NewGuid();
            var partJson = System.Text.Encoding.UTF8.GetBytes(part.ToJson()); // (a part saved before containers: its JSON)
            var asm = TwoParts();
            asm.Id = CadDocument.NewGuid(); asm.Revision = CadDocument.NewGuid();
            asm.Files["parts/Old.sprt"] = partJson;
            asm.AddComponent(new PartRef { PartId = part.Id, Revision = part.Revision, Embedded = "parts/Old.sprt" }, Transform.Identity, "Old");
            asm.Files["cache/old.mesh"] = new byte[] { 7, 8, 9 };
            asm.Caches[AssemblyDocument.CacheKey(part.Id, part.Revision)] = "cache/old.mesh";
            asm.Components[0].Hidden = true;
            asm.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var old = Path.Combine(dir, "old.sasm");
            WriteLegacyZip(old, asm);
            t.True(File.ReadAllBytes(old).AsSpan(0, 2).SequenceEqual("PK"u8), "a ZIP");

            var doc = AssemblyDocument.Load(old);
            t.True(doc.Id == asm.Id && doc.Components[0].Hidden && doc.Files["parts/Old.sprt"].SequenceEqual(partJson), "the old file as it was");
            doc.Save(old); // (in place)
            CheckDatabase(t, old, "converted");
            t.True(RevisionRows(old, part.Id) == 1 && RevisionRows(old, asm.Id) == 1, "the part's history (and the assembly's) starts here");
            var back = AssemblyDocument.Load(old);
            t.True(back.Id == asm.Id && back.Revision != asm.Revision && back.Revision == doc.Revision, "its identity, a revision of the database");
            t.True(back.Components[0].Hidden && back.Caches.Count == 1 && back.Files["cache/old.mesh"].SequenceEqual(new byte[] { 7, 8, 9 }) && back.Thumbnail!.SequenceEqual(asm.Thumbnail), "state, caches and preview");
            var pf = ToFile(dir, back.Files["parts/Old.sprt"], ".sprt");
            t.True(PartDatabase.IsFile(pf) && PartFile.History(pf).Count == 1, "the part a database, one revision");
            var p = CadDocument.Load(pf);
            t.True(p.Id == part.Id, "the part's identity");
            t.Eq(PartJson(p), PartJson(part));
            t.Eq(AsmJson(back), AsmJson(doc));
        });

        t.Test("Assembly files: a part file opened as an assembly, an assembly file opened as a part, refused with clear messages", () =>
        {
            var dir = NewDir("refused");
            var partPath = Path.Combine(dir, "p.sprt");
            SmallPart("10").Save(partPath);
            var asmPath = Path.Combine(dir, "a.sasm");
            TwoParts().Save(asmPath);
            var e1 = Catch(() => AssemblyDocument.Load(partPath));
            t.True(e1 is InvalidDataException && e1.Message.Contains("Not a SpryCAD assembly") && e1.Message.Contains("part"), "a part as an assembly: " + (e1?.Message ?? "it loaded"));
            var e2 = Catch(() => CadDocument.Load(asmPath));
            t.True(e2 is InvalidDataException && e2.Message.Contains("Not a SpryCAD part") && e2.Message.Contains("assembly"), "an assembly as a part: " + (e2?.Message ?? "it loaded"));
            // the same from bytes (an embedded document under the wrong name)
            var e3 = Catch(() => AssemblyContainer.Load(File.ReadAllBytes(partPath), "p.sasm"));
            t.True(e3 is InvalidDataException && e3.Message.Contains("Not a SpryCAD assembly"), "part bytes as an assembly: " + (e3?.Message ?? "it loaded"));
            var e4 = Catch(() => DocumentContainer.Load(File.ReadAllBytes(asmPath), "a.sprt"));
            t.True(e4 is InvalidDataException && e4.Message.Contains("Not a SpryCAD part"), "assembly bytes as a part: " + (e4?.Message ?? "it loaded"));
            // and the files themselves are left as they were
            t.True(!AssemblyFile.IsAssembly(partPath) && AssemblyFile.IsAssembly(asmPath), "IsAssembly");
        });
    }
}

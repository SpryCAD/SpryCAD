using System.Globalization;
using System.Text.Json;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;

namespace SpryCad.Core.Storage;

/// <summary>
/// An assembly file (.sasm) as a database, in the same format as parts (<see cref="PartDatabase"/>): saving records a
/// revision of the assembly (<see cref="AssemblyObjects"/>), opening reads the head.
/// <para>
/// The documents embedded in it (virtual parts, embedded sub-assemblies) are documents of the same database, each
/// with its own history: saving copies their rows in (their revisions, tags, blobs and display caches), opening copies
/// them out into a database of their own for the assembly to use as it always has (<see cref="AssemblyDocument.Files"/>).
/// Making one external later is the same copy out, its history kept.
/// </para>
/// <para>
/// Each document's state that is not in its revisions (a part's rollback bar, an assembly's hidden components) is kept
/// in the manifest: the file's own document under plain keys, as a part file has them, each embedded one's under
/// <c>doc/&lt;id&gt;/</c>.
/// </para>
/// </summary>
public static class AssemblyFile
{
    public const string RootKindAssembly = "assembly", RootKindPart = "part";
    const int CacheKind = 1;
    /// <summary>An embedded document's preview (a file's own is its Thumbnail table): a cache row of its own.</summary>
    const int PreviewKind = 2;

    /// <summary>Manifest keys of a document's state, kept per document (under <c>doc/&lt;id&gt;/</c> for an embedded one).</summary>
    static readonly string[] DocumentKeys = { "DerivedFrom", "RollbackIndex", "SketchVisibility", "AssemblyState", "AssemblyCaches" };

    /// <summary>Whether a database file's own document is an assembly.</summary>
    public static bool IsAssembly(string path)
    {
        if (!PartDatabase.IsFile(path)) return false;
        using var db = PartDatabase.Open(path);
        return db.Manifest("RootKind") == RootKindAssembly;
    }

    /// <summary>Saves the assembly as a revision of its history in the file (none when nothing changed); sets its identity and revision.</summary>
    public static void Save(AssemblyDocument doc, string path, string? author = null, string message = "")
    {
        foreach (var name in doc.Files.Keys) DocumentContainer.CheckName(name);
        doc.Id ??= CadDocument.NewGuid();
        var asmId = Guid.Parse(doc.Id);
        var tmp = path + ".saving";
        var scratch = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N"));
        RevisionInfo head;
        try
        {
            bool append = File.Exists(path) && PartDatabase.IsFile(path) && RootOf(path) == asmId;
            if (append) File.Copy(path, tmp, overwrite: true);
            using (var db = append ? PartDatabase.Open(tmp, readOnly: false) : PartDatabase.Create(tmp))
            {
                // the documents embedded in it: their rows (a part or sub-assembly saved before databases, converted first)
                Directory.CreateDirectory(scratch);
                var embedded = new Dictionary<string, (string id, string revision)>(StringComparer.Ordinal);
                foreach (var (name, bytes) in doc.Files.Where(f => IsEmbeddedDocument(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    var file = Path.Combine(scratch, embedded.Count + (name.EndsWith(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase) ? ".sasm" : ".sprt"));
                    AsDatabase(bytes, name, file);
                    embedded[name] = Import(db, file);
                }
                db.Begin();
                try
                {
                    db.SetPart(asmId, Path.GetFileNameWithoutExtension(path));
                    var objects = AssemblyObjects.From(doc, embedded).ToDictionary(kv => kv.Key, kv => db.Blobs.Put(kv.Value));
                    head = db.Commit(asmId, objects, author ?? PartFile.DefaultAuthor, message.Length > 0 ? message : doc.SaveMessage ?? "",
                        doc.SaveMessage != null ? doc.SaveKind : RevisionKind.Save) ?? db.GetHead(asmId)!;
                    var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    db.SetManifest("FormatVersion", CadDocument.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
                    db.SetManifest("AppVersion", typeof(AssemblyFile).Assembly.GetName().Version?.ToString() ?? "");
                    db.SetManifest("RootPartId", asmId.ToString("D"));
                    db.SetManifest("RootKind", RootKindAssembly);
                    db.SetManifest("Units", doc.Units.ToString());
                    if (db.Manifest("CreatedUtc") == null) db.SetManifest("CreatedUtc", now);
                    db.SetManifest("ModifiedUtc", now);
                    db.SetManifest("DerivedFrom", doc.DerivedFrom);
                    db.SetManifest("AssemblyState", AssemblyObjects.State(doc));
                    db.SetThumbnail(doc.Thumbnail);
                    SaveCaches(db, asmId, doc);
                    db.CommitTransaction();
                }
                catch { db.Rollback(); throw; }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        finally { try { Directory.Delete(scratch, true); } catch { /* best effort */ } }
        doc.Revision = head.RevisionId.ToString("D");
        doc.SaveMessage = null; doc.SaveKind = RevisionKind.Save;
    }

    /// <summary>The assembly at the head of the file's history, its embedded documents and display caches with it.</summary>
    public static AssemblyDocument Load(string path)
    {
        using var db = PartDatabase.Open(path);
        var asmId = RootOf(db);
        if (db.Manifest("RootKind") != RootKindAssembly) throw new InvalidDataException("Not a SpryCAD assembly (the file holds a part).");
        var head = db.GetHead(asmId) ?? throw new InvalidDataException("The file holds no saved assembly.");
        var (doc, embedded) = AssemblyObjects.ToDocument(db.GetObjects(head.RevisionId).ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value)));
        doc.Id = asmId.ToString("D");
        doc.Revision = head.RevisionId.ToString("D");
        doc.DerivedFrom = db.Manifest("DerivedFrom");
        AssemblyObjects.ApplyState(doc, db.Manifest("AssemblyState"));
        doc.Thumbnail = db.GetThumbnail();
        LoadCaches(db, doc, db.Manifest("AssemblyCaches"));
        foreach (var (name, id) in embedded) doc.Files[name] = Extract(db, path, Guid.Parse(id));
        return doc;
    }

    // ------------------------------------------------------------------------------------------
    // embedded documents: copied in and out as rows

    /// <summary>Names of embedded documents (the rest of an assembly's files are display caches).</summary>
    static bool IsEmbeddedDocument(string name) =>
        !name.StartsWith("cache/", StringComparison.Ordinal)
        && (name.EndsWith(CadDocument.FileExtension, StringComparison.OrdinalIgnoreCase) || name.EndsWith(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase));

    /// <summary>An embedded document's bytes as a database file (one saved before databases converted: its history starts here).</summary>
    static void AsDatabase(byte[] bytes, string name, string file)
    {
        if (PartDatabase.Is(bytes)) { File.WriteAllBytes(file, bytes); return; }
        if (name.EndsWith(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase)) Save(AssemblyContainer.Load(bytes, name), file);
        else PartFile.Save(DocumentContainer.Load(bytes, name), file);
    }

    /// <summary>
    /// Copies a database's documents into <paramref name="db"/>: their revisions (not autosaves), tags, blobs and display
    /// caches (in place of those they had), and their state. The id of its own document and its head revision.
    /// </summary>
    static (string id, string revision) Import(PartDatabase db, string file)
    {
        string root, head; byte[]? preview;
        using (var src = PartDatabase.Open(file))
        {
            var id = RootOf(src);
            root = id.ToString("D"); preview = src.GetThumbnail();
            head = src.GetHead(id)?.RevisionId.ToString("D") ?? "";
        }
        db.Execute("ATTACH DATABASE $f AS src", ("$f", file));
        try
        {
            db.Begin();
            try
            {
                db.Execute("INSERT OR IGNORE INTO Blobs SELECT * FROM src.Blobs");
                db.Execute("""
                    INSERT INTO Parts (PartId, Name, HeadRevisionId, WorkingRevisionId) SELECT PartId, Name, HeadRevisionId, NULL FROM src.Parts WHERE true
                    ON CONFLICT(PartId) DO UPDATE SET Name = excluded.Name, HeadRevisionId = excluded.HeadRevisionId
                    """);
                db.Execute("INSERT OR IGNORE INTO Revisions SELECT * FROM src.Revisions WHERE Kind <> 1");
                db.Execute("INSERT OR IGNORE INTO RevisionObjects SELECT o.* FROM src.RevisionObjects o JOIN src.Revisions r ON r.RevisionId = o.RevisionId WHERE r.Kind <> 1");
                db.Execute("DELETE FROM Tags WHERE PartId IN (SELECT PartId FROM src.Parts)");
                db.Execute("INSERT OR REPLACE INTO Tags SELECT * FROM src.Tags");
                db.Execute("DELETE FROM Cache WHERE PartId IN (SELECT PartId FROM src.Parts)");
                db.Execute("INSERT OR REPLACE INTO Cache SELECT * FROM src.Cache");
                // its own document's state under its id; the state of documents embedded in it as it is
                foreach (var key in DocumentKeys)
                {
                    db.Execute("DELETE FROM Manifest WHERE Key = $k", ("$k", $"doc/{root}/{key}"));
                    db.Execute("INSERT INTO Manifest (Key, Value) SELECT $k, Value FROM src.Manifest WHERE Key = $s", ("$k", $"doc/{root}/{key}"), ("$s", key));
                }
                db.Execute("INSERT OR REPLACE INTO Manifest SELECT Key, Value FROM src.Manifest WHERE Key LIKE 'doc/%'");
                // its preview, kept as its cache (the documents embedded in it brought theirs with their cache rows)
                if (preview != null)
                    db.Execute("INSERT OR REPLACE INTO Cache (CacheKey, PartId, Kind, Hash) VALUES ($c, $p, $k, $h)",
                        ("$c", Hash.Of(System.Text.Encoding.UTF8.GetBytes("preview/" + root)).ToBytes()), ("$p", Guid.Parse(root).ToByteArray()), ("$k", PreviewKind), ("$h", db.Blobs.Put(preview).ToBytes()));
                db.CommitTransaction();
            }
            catch { db.Rollback(); throw; }
        }
        finally { db.Execute("DETACH DATABASE src"); }
        return (root, head);
    }

    /// <summary>
    /// An embedded document copied out of the assembly's database into one of its own (its bytes): its history, tags,
    /// display caches and state, and those of the documents embedded in it.
    /// </summary>
    static byte[] Extract(PartDatabase db, string path, Guid id)
    {
        var ids = new List<Guid>();
        void Collect(Guid d)
        {
            if (ids.Contains(d)) return;
            ids.Add(d);
            if (db.GetHead(d) is { } h && db.GetObjects(h.RevisionId).TryGetValue(PartObjects.Single(ObjectKind.AssemblySettings), out var s))
                foreach (var sub in AssemblyObjects.EmbeddedOf(db.Blobs.Get(s)).Values) Collect(Guid.Parse(sub));
        }
        Collect(id);
        var kind = db.GetHead(id) is { } head && db.GetObjects(head.RevisionId).ContainsKey(PartObjects.Single(ObjectKind.AssemblySettings)) ? RootKindAssembly : RootKindPart;
        var tmp = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + (kind == RootKindAssembly ? ".sasm" : ".sprt"));
        try
        {
            using (var dst = PartDatabase.Create(tmp))
            {
                dst.Execute("ATTACH DATABASE $f AS src", ("$f", path));
                try
                {
                    dst.Begin();
                    try
                    {
                        foreach (var d in ids)
                        {
                            var p = ("$p", (object)d.ToByteArray());
                            dst.Execute("""
                                INSERT OR IGNORE INTO Blobs SELECT b.* FROM src.Blobs b WHERE b.Hash IN (
                                  SELECT o.Hash FROM src.RevisionObjects o JOIN src.Revisions r ON r.RevisionId = o.RevisionId WHERE r.PartId = $p AND r.Kind <> 1
                                  UNION SELECT Hash FROM src.Cache WHERE PartId = $p)
                                """, p);
                            dst.Execute("INSERT OR IGNORE INTO Parts (PartId, Name, HeadRevisionId, WorkingRevisionId) SELECT PartId, Name, HeadRevisionId, NULL FROM src.Parts WHERE PartId = $p", p);
                            dst.Execute("INSERT OR IGNORE INTO Revisions SELECT * FROM src.Revisions WHERE PartId = $p AND Kind <> 1", p);
                            dst.Execute("INSERT OR IGNORE INTO RevisionObjects SELECT o.* FROM src.RevisionObjects o JOIN src.Revisions r ON r.RevisionId = o.RevisionId WHERE r.PartId = $p AND r.Kind <> 1", p);
                            dst.Execute("INSERT OR IGNORE INTO Tags SELECT * FROM src.Tags WHERE PartId = $p", p);
                            dst.Execute("INSERT OR IGNORE INTO Cache SELECT * FROM src.Cache WHERE PartId = $p", p);
                            var prefix = $"doc/{d:D}/";
                            if (d == id)
                                foreach (var key in DocumentKeys)
                                    dst.Execute("INSERT OR REPLACE INTO Manifest (Key, Value) SELECT $k, Value FROM src.Manifest WHERE Key = $s", ("$k", key), ("$s", prefix + key));
                            else dst.Execute("INSERT OR REPLACE INTO Manifest SELECT Key, Value FROM src.Manifest WHERE substr(Key, 1, length($x)) = $x", ("$x", prefix));
                        }
                        dst.SetManifest("FormatVersion", CadDocument.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
                        dst.SetManifest("RootPartId", id.ToString("D"));
                        dst.SetManifest("RootKind", kind);
                        // its preview back where a file keeps its own
                        var p0 = ("$p", (object)id.ToByteArray()); var pk = ("$k", (object)PreviewKind);
                        if (dst.Query("SELECT Hash FROM Cache WHERE PartId = $p AND Kind = $k", r => (byte[])r[0], p0, pk).FirstOrDefault() is { } ph)
                        {
                            dst.SetThumbnail(dst.Blobs.Get(Hash.From(ph)));
                            dst.Execute("DELETE FROM Cache WHERE PartId = $p AND Kind = $k", p0, pk);
                            dst.DeleteBlobIfUnused(ph);
                        }
                        dst.CommitTransaction();
                    }
                    catch { dst.Rollback(); throw; }
                }
                finally { dst.Execute("DETACH DATABASE src"); }
            }
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    // ------------------------------------------------------------------------------------------
    // the assembly's display caches (its parts' meshes, its linked sub-assemblies' copies): not in revisions

    sealed record CacheEntry(string File, string Hash);

    static void SaveCaches(PartDatabase db, Guid asmId, AssemblyDocument doc)
    {
        var old = db.Query("SELECT Hash FROM Cache WHERE PartId = $p AND Kind = $k", r => (byte[])r[0], ("$p", asmId.ToByteArray()), ("$k", CacheKind));
        db.Execute("DELETE FROM Cache WHERE PartId = $p AND Kind = $k", ("$p", asmId.ToByteArray()), ("$k", CacheKind));
        var map = new SortedDictionary<string, CacheEntry>(StringComparer.Ordinal);
        // (a cache named by a key, and any other file the assembly holds that is not a document: kept by its name)
        var entries = doc.Caches.Where(kv => doc.Files.ContainsKey(kv.Value)).Select(kv => (key: kv.Key, file: kv.Value))
            .Concat(doc.Files.Keys.Where(n => !IsEmbeddedDocument(n) && !doc.Caches.ContainsValue(n)).Select(n => (key: "file:" + n, file: n)));
        foreach (var (key, file) in entries)
        {
            var h = db.Blobs.Put(doc.Files[file]);
            db.Execute("INSERT OR REPLACE INTO Cache (CacheKey, PartId, Kind, Hash) VALUES ($c, $p, $k, $h)",
                ("$c", Hash.Of(System.Text.Encoding.UTF8.GetBytes("assembly/" + asmId + "/" + key)).ToBytes()), ("$p", asmId.ToByteArray()), ("$k", CacheKind), ("$h", h.ToBytes()));
            map[key] = new CacheEntry(file, h.ToString());
        }
        db.SetManifest("AssemblyCaches", map.Count > 0 ? JsonSerializer.Serialize(map) : null);
        foreach (var h in old) db.DeleteBlobIfUnused(h);
    }

    static void LoadCaches(PartDatabase db, AssemblyDocument doc, string? json)
    {
        if (json == null) return;
        var map = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json) ?? new();
        foreach (var (key, e) in map)
        {
            var bytes = db.Blobs.Get(Hash.From(Convert.FromHexString(e.Hash)), verify: true);
            doc.Files[e.File] = bytes;
            if (!key.StartsWith("file:", StringComparison.Ordinal)) doc.Caches[key] = e.File;
        }
    }

    // ------------------------------------------------------------------------------------------

    static Guid RootOf(string path) { using var db = PartDatabase.Open(path); return RootOf(db); }

    static Guid RootOf(PartDatabase db) =>
        Guid.TryParse(db.Manifest("RootPartId"), out var id) ? id : throw new InvalidDataException("The file names no document (no RootPartId in its manifest).");
}

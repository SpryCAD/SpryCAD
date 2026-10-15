using System.Globalization;
using System.Text;
using System.Text.Json;
using SpryCad.Core.Model;

namespace SpryCad.Core.Storage;

/// <summary>
/// A part file (.sprt) as a database (<see cref="PartDatabase"/>): saving records a revision, opening reads the
/// head. Every save goes to a copy of the file, swapped in when complete, so a failed or interrupted save leaves the
/// file as it was; a file holding the same part keeps its history, any other file there is replaced.
/// </summary>
public static class PartFile
{
    const int DisplayCache = 0;

    /// <summary>
    /// Saves the part as a revision of its history in the file (no revision when nothing changed since the last).
    /// Sets the part's identity on its first save and its <see cref="CadDocument.Revision"/> to the head's id.
    /// </summary>
    public static void Save(CadDocument doc, string path, string? author = null, string message = "", RevisionKind kind = RevisionKind.Save, bool durable = true)
    {
        doc.Id ??= CadDocument.NewGuid();
        var partId = Guid.Parse(doc.Id);
        var tmp = path + ".saving";
        RevisionInfo head;
        try
        {
            bool append = File.Exists(path) && PartDatabase.IsFile(path) && RootPart(path) == partId;
            if (append) File.Copy(path, tmp, overwrite: true);
            using (var db = append ? PartDatabase.Open(tmp, readOnly: false) : PartDatabase.Create(tmp, durable))
            {
                db.Begin();
                try
                {
                    db.SetPart(partId, Path.GetFileNameWithoutExtension(path));
                    var objects = PartObjects.From(doc).ToDictionary(kv => kv.Key, kv => db.Blobs.Put(kv.Value));
                    var made = db.Commit(partId, objects, author ?? DefaultAuthor, message, kind);
                    head = made ?? db.GetHead(partId)!;
                    // a new revision: what it looks like, its changes shown (the Revisions pane)
                    if (made != null && PreviewMeshOf(doc) is { } mesh)
                        try { RevisionPreview.Record(db, made, mesh, doc.Color, doc); }
                        catch (Exception ex) when (ex is not Microsoft.Data.Sqlite.SqliteException) { /* a preview is never worth failing a save */ }
                    db.DropWorking(partId); // (saved: what autosave kept is in the head now)
                    WriteManifest(db, doc, partId);
                    db.SetThumbnail(doc.Thumbnail);
                    db.SetDisplay(partId, DisplayKey(head.RootHash), doc.Display);
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
        doc.Revision = head.RevisionId.ToString("D");
    }

    /// <summary>The part's display as saved: the one the app built (<see cref="CadDocument.PreviewMesh"/>), else its display cache's.</summary>
    static Kernel.DisplayMesh? PreviewMeshOf(CadDocument doc)
    {
        if (doc.PreviewMesh != null) return doc.PreviewMesh;
        if (doc.Display == null) return null;
        try { return Assembly.MeshCache.Read(doc.Display).Mesh; }
        catch (InvalidDataException) { return null; }
    }

    static void WriteManifest(PartDatabase db, CadDocument doc, Guid partId)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        db.SetManifest("FormatVersion", CadDocument.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
        db.SetManifest("AppVersion", typeof(PartFile).Assembly.GetName().Version?.ToString() ?? "");
        db.SetManifest("RootPartId", partId.ToString("D"));
        db.SetManifest("RootKind", AssemblyFile.RootKindPart);
        db.SetManifest("Units", doc.Units.ToString());
        if (db.Manifest("CreatedUtc") == null) db.SetManifest("CreatedUtc", now);
        db.SetManifest("ModifiedUtc", now);
        db.SetManifest("DerivedFrom", doc.DerivedFrom);
        // not the model: where the rollback bar is, which sketches are shown
        db.SetManifest("RollbackIndex", doc.RollbackIndex?.ToString(CultureInfo.InvariantCulture));
        db.SetManifest("SketchVisibility", doc.SketchVisibility == null ? null
            : JsonSerializer.Serialize(new SortedDictionary<string, bool>(doc.SketchVisibility, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Autosave: the part as the file's working revision (in place of the one it had), never in its history. Written in
    /// place, in one transaction (a working copy is the app's own, local file); the file is made if there is none.
    /// </summary>
    public static void SaveWorking(CadDocument doc, string path, string? author = null)
    {
        doc.Id ??= CadDocument.NewGuid();
        var partId = Guid.Parse(doc.Id);
        bool same = File.Exists(path) && PartDatabase.IsFile(path) && RootPart(path) == partId;
        using var db = same ? PartDatabase.Open(path, readOnly: false) : PartDatabase.Create(path);
        db.Begin();
        try
        {
            db.SetPart(partId, Path.GetFileNameWithoutExtension(path));
            var objects = PartObjects.From(doc).ToDictionary(kv => kv.Key, kv => db.Blobs.Put(kv.Value));
            db.CommitWorking(partId, objects, author ?? DefaultAuthor);
            WriteManifest(db, doc, partId);
            db.CommitTransaction();
        }
        catch { db.Rollback(); throw; }
    }

    /// <summary>The name revisions are made by when none is given: the user's (set by the app from its options).</summary>
    public static string DefaultAuthor { get; set; } = Environment.UserName;

    /// <summary>
    /// The part at the head of the file's history; with <paramref name="working"/>, its working revision if it has one
    /// (autosave); with <paramref name="revision"/>, as it was in that revision.
    /// </summary>
    public static CadDocument Load(string path, bool working = false, Guid? revision = null)
    {
        using var db = PartDatabase.Open(path);
        var partId = RootPart(db);
        if (db.Manifest("RootKind") == AssemblyFile.RootKindAssembly) throw new InvalidDataException("Not a SpryCAD part (the file holds an assembly).");
        var head = (revision is { } r ? db.GetRevision(r) ?? throw new InvalidDataException("The file has no such revision.") : null)
            ?? (working ? db.GetWorking(partId) : null) ?? db.GetHead(partId) ?? throw new InvalidDataException("The file holds no saved part.");
        var objects = db.GetObjects(head.RevisionId).ToDictionary(kv => kv.Key, kv => db.Blobs.Get(kv.Value, verify: kv.Key.Kind == ObjectKind.File));
        var doc = PartObjects.ToDocument(objects);
        doc.Id = partId.ToString("D");
        doc.Revision = head.RevisionId.ToString("D");
        doc.DerivedFrom = db.Manifest("DerivedFrom");
        if (int.TryParse(db.Manifest("RollbackIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rb)) doc.RollbackIndex = rb;
        if (db.Manifest("SketchVisibility") is { } sv) doc.SketchVisibility = JsonSerializer.Deserialize<Dictionary<string, bool>>(sv);
        doc.Thumbnail = db.GetThumbnail();
        // the display made for this very content by this geometry code, if one was saved
        if (db.GetCache(DisplayKey(head.RootHash)) is { } d && Assembly.MeshCache.VersionOf(d) == Kernel.GeometryVersion.Current) doc.Display = d;
        return doc;
    }

    /// <summary>The part's identity and head revision, without reading the part (nulls if the file has none).</summary>
    public static (string? id, string? revision) ReadIdentity(string path)
    {
        using var db = PartDatabase.Open(path);
        if (db.Manifest("RootPartId") is not { } p || !Guid.TryParse(p, out var partId)) return (null, null);
        return (partId.ToString("D"), db.GetHead(partId)?.RevisionId.ToString("D"));
    }

    /// <summary>The part's history in the file, head first.</summary>
    public static List<RevisionInfo> History(string path)
    {
        using var db = PartDatabase.Open(path);
        return db.GetHistory(RootPart(db));
    }

    static Guid RootPart(string path) { using var db = PartDatabase.Open(path); return RootPart(db); }

    static Guid RootPart(PartDatabase db) =>
        Guid.TryParse(db.Manifest("RootPartId"), out var id) ? id : throw new InvalidDataException("The file names no part (no RootPartId in its manifest).");

    /// <summary>The display cache's key: made for this content (root hash).</summary>
    static Hash DisplayKey(Hash root) => Hash.Of(Encoding.UTF8.GetBytes("display/" + root));
}

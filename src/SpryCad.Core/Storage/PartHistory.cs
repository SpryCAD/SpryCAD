using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SpryCad.Core.Storage;

public enum ChangeKind { Added, Removed, Modified, Reordered }

/// <summary>An object that differs between two revisions (by id): added, removed, or changed (its hash).</summary>
public sealed record ObjectChange(ObjectId ObjectId, ChangeKind Kind, Hash? Old, Hash? New);

/// <summary>What a revision changed from its parent, as the history lists it.</summary>
public sealed record ChangeSummary(IReadOnlyList<string> Added, IReadOnlyList<string> Modified, IReadOnlyList<string> Removed, bool Reordered, int OtherChanges)
{
    public bool IsEmpty => Added.Count + Modified.Count + Removed.Count + OtherChanges == 0 && !Reordered;
}

/// <summary>A revision as the history shows it: who saved it when, its tags, and what it changed.</summary>
public sealed record HistoryEntry(RevisionInfo Revision, IReadOnlyList<string> Tags, ChangeSummary Changes);

/// <summary>How a part file's size is spent (File info).</summary>
public sealed record FileUsage(long FileBytes, long ModelBytes, long EmbeddedFileBytes, long DisplayBytes, long HistoryBytes, long ThumbnailBytes, int Revisions);

/// <summary>
/// A part file's history (the file format spec, sections 6, 7 and 13): its revisions with what each changed, tags,
/// and keeping the file's size in check. Works on a database file directly: in the app, on the part's working
/// copy (<see cref="WorkingCopy.Edit"/>), saved to the part's file by the next save.
/// </summary>
public static class PartHistory
{
    /// <summary>The part's revisions, newest first, each with its tags and what it changed from the one before.</summary>
    public static List<HistoryEntry> Read(PartDatabase db)
    {
        var partId = RootPart(db);
        var tags = db.GetTags(partId).ToLookup(t => t.RevisionId, t => t.Name);
        var history = db.GetHistory(partId);
        var list = new List<HistoryEntry>();
        for (int i = 0; i < history.Count; i++)
        {
            var r = history[i];
            var parent = i + 1 < history.Count ? history[i + 1] : null;
            list.Add(new HistoryEntry(r, tags[r.RevisionId].ToList(), Summarize(db, parent?.RevisionId, r.RevisionId)));
        }
        return list;
    }

    /// <summary>
    /// The objects that differ from revision <paramref name="from"/> (null: nothing, the first revision) to
    /// <paramref name="to"/>: added, removed and changed; a changed feature order is one "reordered" change.
    /// </summary>
    public static List<ObjectChange> Diff(PartDatabase db, Guid? from, Guid to)
    {
        var a = from is { } f ? db.GetObjects(f) : new Dictionary<ObjectId, Hash>();
        var b = db.GetObjects(to);
        var changes = new List<ObjectChange>();
        foreach (var (id, h) in b)
        {
            if (!a.TryGetValue(id, out var old)) { if (id.Kind != ObjectKind.FeatureOrder) changes.Add(new(id, ChangeKind.Added, null, h)); }
            else if (old != h) changes.Add(new(id, id.Kind == ObjectKind.FeatureOrder ? ChangeKind.Reordered : ChangeKind.Modified, old, h));
        }
        foreach (var (id, h) in a)
            if (!b.ContainsKey(id)) changes.Add(new(id, ChangeKind.Removed, h, null));
        return changes;
    }

    static ChangeSummary Summarize(PartDatabase db, Guid? from, Guid to)
    {
        List<string> added = new(), modified = new(), removed = new();
        bool reordered = false; int other = 0;
        foreach (var c in Diff(db, from, to))
        {
            if (c.Kind == ChangeKind.Reordered) { reordered = true; continue; }
            if (c.ObjectId.Kind == ObjectKind.NameCounters) continue; // (follows added features: not a change of its own)
            if (c.ObjectId.Kind != ObjectKind.Feature) { if (from != null) other++; continue; }
            var name = FeatureName(db, (c.New ?? c.Old)!.Value) ?? c.ObjectId.ToString();
            (c.Kind == ChangeKind.Added ? added : c.Kind == ChangeKind.Removed ? removed : modified).Add(name);
        }
        // (adding a feature also changes the order: not a reorder)
        if (reordered && added.Count + removed.Count > 0) reordered = OrderChangedBeyond(db, from, to);
        return new ChangeSummary(added, modified, removed, reordered, other);
    }

    /// <summary>Whether the features both revisions have are in another order (not just features added or removed).</summary>
    static bool OrderChangedBeyond(PartDatabase db, Guid? from, Guid to)
    {
        if (from is not { } f) return false;
        List<string> Order(Guid rev) => db.GetObjects(rev).TryGetValue(PartObjects.Single(ObjectKind.FeatureOrder), out var h)
            ? JsonDocument.Parse(db.Blobs.Get(h)).RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetString()!).ToList() : new();
        var a = Order(f); var b = Order(to);
        var common = a.Intersect(b).ToHashSet();
        return !a.Where(common.Contains).SequenceEqual(b.Where(common.Contains));
    }

    static string? FeatureName(PartDatabase db, Hash h)
    {
        try { return JsonDocument.Parse(db.Blobs.Get(h)).RootElement.GetProperty("data").GetProperty("name").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------------------------------
    // tags

    /// <summary>Gives a revision a tag (unique in the part: an existing tag of that name is moved to it).</summary>
    public static void Tag(PartDatabase db, Guid revisionId, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A tag needs a name.");
        db.SetTag(RootPart(db), name, revisionId);
    }

    public static void Untag(PartDatabase db, string name) => db.DeleteTag(RootPart(db), name);

    public static void RenameTag(PartDatabase db, string name, string newName)
    {
        var partId = RootPart(db);
        newName = newName.Trim();
        if (newName.Length == 0) throw new ArgumentException("A tag needs a name.");
        var rev = db.GetTags(partId).FirstOrDefault(t => t.Name == name).RevisionId;
        if (rev == Guid.Empty) throw new ArgumentException($"There is no tag \"{name}\".");
        if (newName != name && db.GetTags(partId).Any(t => t.Name == newName)) throw new ArgumentException($"There is a tag \"{newName}\" already.");
        db.DeleteTag(partId, name);
        db.SetTag(partId, newName, rev);
    }

    // ------------------------------------------------------------------------------------------
    // keeping the file's size in check (section 13)

    /// <summary>
    /// The revisions <see cref="Purge"/> would remove (those before <paramref name="keepFrom"/>), and the tags they would
    /// take with them, for the user to confirm.
    /// </summary>
    public static (List<RevisionInfo> revisions, List<string> tags) PurgePlan(PartDatabase db, Guid keepFrom)
    {
        var partId = RootPart(db);
        var history = db.GetHistory(partId);
        int at = history.FindIndex(r => r.RevisionId == keepFrom);
        if (at < 0) throw new ArgumentException("The revision is not in the part's history.");
        var gone = history.Skip(at + 1).ToList();
        var ids = gone.Select(r => r.RevisionId).ToHashSet();
        return (gone, db.GetTags(partId).Where(t => ids.Contains(t.RevisionId)).Select(t => t.Name).ToList());
    }

    /// <summary>
    /// Forgets the history before <paramref name="keepFrom"/>: it becomes the first revision, the older ones and their
    /// tags are removed, and the file compacted. Cannot be undone.
    /// </summary>
    public static void Purge(PartDatabase db, Guid keepFrom)
    {
        var (gone, _) = PurgePlan(db, keepFrom);
        db.Begin();
        try
        {
            db.Execute("UPDATE Revisions SET ParentId = NULL WHERE RevisionId = $r", ("$r", keepFrom.ToByteArray()));
            foreach (var r in gone)
            {
                var id = r.RevisionId.ToByteArray();
                db.Execute("DELETE FROM Tags WHERE RevisionId = $r", ("$r", id));
                db.Execute("DELETE FROM RevisionObjects WHERE RevisionId = $r", ("$r", id));
                db.Execute("DELETE FROM Revisions WHERE RevisionId = $r", ("$r", id));
            }
            DeleteUnusedBlobs(db);
            db.CommitTransaction();
        }
        catch { db.Rollback(); throw; }
        db.Execute("VACUUM");
    }

    /// <summary>Removes what nothing uses any more (blobs no revision or cache holds) and gives the space back.</summary>
    public static void Compact(PartDatabase db)
    {
        db.Begin();
        try { DeleteUnusedBlobs(db); db.CommitTransaction(); }
        catch { db.Rollback(); throw; }
        db.Execute("VACUUM");
    }

    /// <summary>Removes the display caches (the part is rebuilt when opened), then compacts.</summary>
    public static void ClearCache(PartDatabase db)
    {
        db.Begin();
        try { db.Execute("DELETE FROM Cache"); DeleteUnusedBlobs(db); db.CommitTransaction(); }
        catch { db.Rollback(); throw; }
        db.Execute("VACUUM");
    }

    static void DeleteUnusedBlobs(PartDatabase db) => db.Execute("""
        DELETE FROM Blobs WHERE Hash NOT IN (SELECT Hash FROM RevisionObjects)
                            AND Hash NOT IN (SELECT Hash FROM Cache)
        """);

    /// <summary>
    /// How the file's size is spent: the part as it is now (model objects, embedded files), its display, what only
    /// older revisions use (history), and the preview.
    /// </summary>
    public static FileUsage Usage(PartDatabase db, string path)
    {
        var partId = RootPart(db);
        var head = db.GetHead(partId);
        var current = head != null ? db.GetObjects(head.RevisionId) : new Dictionary<ObjectId, Hash>();
        var model = current.Where(kv => kv.Key.Kind != ObjectKind.File).Select(kv => kv.Value).ToHashSet();
        var files = current.Where(kv => kv.Key.Kind == ObjectKind.File).Select(kv => kv.Value).ToHashSet();
        var cache = db.Query("SELECT Hash FROM Cache", r => Hash.From((byte[])r[0])).ToHashSet();
        long m = 0, f = 0, d = 0, h = 0;
        foreach (var (hash, size) in db.Query("SELECT Hash, length(Data) FROM Blobs", r => (Hash.From((byte[])r[0]), r.GetInt64(1))))
        {
            if (model.Contains(hash)) m += size;
            else if (files.Contains(hash)) f += size;
            else if (cache.Contains(hash)) d += size;
            else h += size;
        }
        var thumb = db.Query("SELECT length(Png) FROM Thumbnail", r => r.GetInt64(0)).Sum();
        return new FileUsage(new FileInfo(path).Length, m, f, d, h, thumb, db.GetHistory(partId).Count);
    }

    static Guid RootPart(PartDatabase db) =>
        Guid.TryParse(db.Manifest("RootPartId"), out var id) ? id : throw new InvalidDataException("The file names no part (no RootPartId in its manifest).");
}

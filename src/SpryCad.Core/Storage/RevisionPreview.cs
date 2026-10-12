using System.Text;
using System.Text.Json;
using SpryCad.Kernel;

namespace SpryCad.Core.Storage;

/// <summary>
/// What each revision of a part looks like, with what it changed shown (the Revisions pane): at a save, the part's
/// faces are recorded with the revision (name → geometry hash, a few kilobytes), and a small preview is made of the
/// part with the faces added since the revision before in green and those changed in amber, the others plain. Kept
/// as the revision's caches (never in it): purging a revision takes them with it, clearing the display caches does not.
/// Revisions saved before (or without a display) have none.
/// </summary>
public static class RevisionPreview
{
    internal const int FacesKind = 3, PreviewKind = 4;
    public const int Size = 128;

    static readonly double[] Added = { 0.20, 0.70, 0.30 }, Changed = { 0.95, 0.75, 0.10 }, Moved = { 0.93, 0.87, 0.70 }, Unchanged = { 0.80, 0.81, 0.83 };

    /// <summary>
    /// Records a revision's faces and preview from its display mesh (<paramref name="doc"/>: the part saved). Against
    /// the revision before: the faces of the features it added green, of those it changed amber (a sketch or plane
    /// changed: the faces of the features built on it), other faces whose geometry changed with them (a neighbour's
    /// edge moved) pale amber, the rest plain grey. The first revision: the plain part.
    /// </summary>
    public static void Record(PartDatabase db, RevisionInfo rev, DisplayMesh mesh, double[] color, Model.CadDocument doc)
    {
        if (mesh.Indices.Length == 0) return;
        var faces = VisualDiff.Hashes(mesh);
        var sorted = new SortedDictionary<string, string>(faces.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("x16")), StringComparer.Ordinal);
        Put(db, FacesKey(rev.RevisionId), rev.PartId, FacesKind, JsonSerializer.SerializeToUtf8Bytes(sorted));
        Func<string, double[]?>? faceColor = null;
        if (rev.ParentId is { } p)
        {
            var (added, changed) = ChangedFeatures(db, p, rev.RevisionId, doc, faces.Keys);
            var geometry = Faces(db, p) is { } before ? VisualDiff.Compare(before, faces) : null;
            faceColor = name =>
            {
                var f = VisualDiff.FeatureOf(name);
                if (added.Contains(f)) return Added;
                if (changed.Contains(f)) return Changed;
                return geometry != null && geometry.TryGetValue(name, out var k) && k is FaceChange.Added or FaceChange.Changed ? Moved : Unchanged;
            };
        }
        Put(db, PreviewKey(rev.RevisionId), rev.PartId, PreviewKind, Thumbnail.Render(new[] { (mesh, color) }, Size, faceColor));
    }

    /// <summary>
    /// The features a revision added and changed, as the faces show them: a changed feature that makes no faces (a
    /// sketch, a plane) stands for the features built on it (through others that make none, too).
    /// </summary>
    static (HashSet<string> added, HashSet<string> changed) ChangedFeatures(PartDatabase db, Guid from, Guid to, Model.CadDocument doc, IEnumerable<string> faceNames)
    {
        var added = new HashSet<string>(StringComparer.Ordinal); var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in PartHistory.Diff(db, from, to).Where(c => c.ObjectId.Kind == ObjectKind.Feature))
            (c.Kind == ChangeKind.Added ? added : changed).Add(c.ObjectId.ToString());
        var makesFaces = faceNames.Select(VisualDiff.FeatureOf).ToHashSet(StringComparer.Ordinal);
        var through = new Queue<string>(added.Concat(changed).Where(id => !makesFaces.Contains(id)));
        var seen = through.ToHashSet(StringComparer.Ordinal);
        var jsons = doc.Features.Select(f => (f.Id, json: Model.CadDocument.JsonOf(f))).ToList();
        while (through.Count > 0)
        {
            var id = through.Dequeue();
            var target = added.Contains(id) ? added : changed;
            foreach (var (fid, json) in jsons)
            {
                if (fid == id || !json.Contains($"\"{id}", StringComparison.Ordinal) || added.Contains(fid)) continue;
                target.Add(fid);
                if (!makesFaces.Contains(fid) && seen.Add(fid)) through.Enqueue(fid);
            }
        }
        return (added, changed);
    }

    /// <summary>The revision's preview (PNG), null when it has none.</summary>
    public static byte[]? Get(PartDatabase db, Guid revisionId) => db.GetCache(PreviewKey(revisionId));

    /// <summary>The revision's faces (name → geometry hash), null when they were not recorded.</summary>
    public static Dictionary<string, ulong>? Faces(PartDatabase db, Guid revisionId)
    {
        if (db.GetCache(FacesKey(revisionId)) is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!
                .ToDictionary(kv => kv.Key, kv => ulong.Parse(kv.Value, System.Globalization.NumberStyles.HexNumber), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return null; }
    }

    /// <summary>Removes a revision's faces and preview (it is purged).</summary>
    internal static void Forget(PartDatabase db, Guid revisionId)
    {
        foreach (var key in new[] { FacesKey(revisionId), PreviewKey(revisionId) })
            db.Execute("DELETE FROM Cache WHERE CacheKey = $k", ("$k", key.ToBytes()));
    }

    static void Put(PartDatabase db, Hash key, Guid partId, int kind, byte[] data) =>
        db.Execute("INSERT OR REPLACE INTO Cache (CacheKey, PartId, Kind, Hash) VALUES ($c, $p, $k, $h)",
            ("$c", key.ToBytes()), ("$p", partId.ToByteArray()), ("$k", kind), ("$h", db.Blobs.Put(data).ToBytes()));

    static Hash FacesKey(Guid rev) => Hash.Of(Encoding.UTF8.GetBytes("faces/" + rev.ToString("D")));
    static Hash PreviewKey(Guid rev) => Hash.Of(Encoding.UTF8.GetBytes("revision-preview/" + rev.ToString("D")));
}

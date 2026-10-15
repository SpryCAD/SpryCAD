using System.Globalization;
using SpryCad.Core.Storage;

namespace SpryCad.Core.Library;

/// <summary>The layers libraries stack in (section 34): later layers add to, override and restrict earlier ones.</summary>
public enum LibraryLayer { Standards = 0, PartFamilies = 1, Company = 2 }

/// <summary>A library as read: its identity, the revision read, and its tables and restrictions.</summary>
public sealed record LoadedLibrary(Guid LibraryId, string Name, LibraryLayer Layer, Guid RevisionId, string? Path,
    IReadOnlyList<LibraryTable> Tables, IReadOnlyList<LibraryRestriction> Restrictions);

/// <summary>A library file was published by someone else since it was read: publishing over it would lose their work.</summary>
public sealed class LibraryConflictException(string path) : IOException($"{System.IO.Path.GetFileName(path)} was published by someone else since it was read.");

/// <summary>
/// A library file (.sprlib, section 34): the same database as a part file (<see cref="PartDatabase"/>), its root a
/// library (manifest <c>RootKind = library</c>) whose objects are its tables and its restrictions. So libraries have
/// revisions, history, tags and diffs like parts. Users only ever read a copy (<see cref="LibrarySet"/>); publishing
/// writes a copy beside the file and swaps it in, refused if the file changed since it was read.
/// </summary>
public static class LibraryFile
{
    public const string Extension = ".sprlib";
    public const string RootKindLibrary = "library";

    /// <summary>Reads the library at its head revision.</summary>
    public static LoadedLibrary Read(string path)
    {
        using var db = PartDatabase.Open(path);
        if (db.Manifest("RootKind") != RootKindLibrary) throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a SpryCAD library.");
        var id = Guid.Parse(db.Manifest("RootPartId") ?? throw new InvalidDataException("The library names no id."));
        var head = db.GetHead(id) ?? throw new InvalidDataException("The library has nothing published.");
        var tables = new List<LibraryTable>();
        var restrictions = new List<LibraryRestriction>();
        foreach (var (oid, hash) in db.GetObjects(head.RevisionId).OrderBy(kv => kv.Key))
        {
            var bytes = db.Blobs.Get(hash);
            if (oid.Kind == ObjectKind.LibraryTable) tables.Add(PartObjects.Open<LibraryTable>(bytes, ObjectKind.LibraryTable));
            else if (oid.Kind == ObjectKind.LibraryRestrictions) restrictions.AddRange(PartObjects.Open<List<LibraryRestriction>>(bytes, ObjectKind.LibraryRestrictions));
            // (other kinds: from a newer SpryCAD, left alone)
        }
        tables.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var layer = Enum.TryParse<LibraryLayer>(db.Manifest("LibraryLayer"), out var l) ? l : LibraryLayer.Company;
        return new LoadedLibrary(id, db.Manifest("LibraryName") ?? System.IO.Path.GetFileNameWithoutExtension(path), layer, head.RevisionId, path, tables, restrictions);
    }

    /// <summary>Whether the file is a library (its manifest), without reading it.</summary>
    public static bool IsLibrary(string path)
    {
        if (!PartDatabase.IsFile(path)) return false;
        using var db = PartDatabase.Open(path);
        return db.Manifest("RootKind") == RootKindLibrary;
    }

    /// <summary>
    /// Publishes the tables and restrictions as the library's next revision (none when nothing changed), at
    /// <paramref name="path"/>: a new library when there is none. Refused (<see cref="LibraryConflictException"/>) when
    /// the file is not what <paramref name="readRevision"/> says was read (someone else published since), unless null.
    /// Returns the published revision.
    /// </summary>
    public static Guid Publish(string path, Guid libraryId, string name, LibraryLayer layer, IEnumerable<LibraryTable> tables,
        IEnumerable<LibraryRestriction>? restrictions, string author, string message = "", Guid? readRevision = null)
    {
        var tmp = path + ".saving";
        var local = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sprycad-lib-" + Guid.NewGuid().ToString("N") + Extension);
        try
        {
            bool exists = File.Exists(path);
            if (exists) File.Copy(path, local);
            Guid revision;
            using (var db = exists ? PartDatabase.Open(local, readOnly: false) : PartDatabase.Create(local))
            {
                if (exists)
                {
                    if (db.Manifest("RootKind") != RootKindLibrary || db.Manifest("RootPartId") != libraryId.ToString("D"))
                        throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} holds another library or a model.");
                    if (readRevision != null && db.GetHead(libraryId)?.RevisionId != readRevision) throw new LibraryConflictException(path);
                }
                db.Begin();
                try
                {
                    db.SetPart(libraryId, name);
                    var objects = Objects(tables, restrictions).ToDictionary(kv => kv.Key, kv => db.Blobs.Put(kv.Value));
                    revision = (db.Commit(libraryId, objects, author, message) ?? db.GetHead(libraryId)!).RevisionId;
                    var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    db.SetManifest("FormatVersion", "1");
                    db.SetManifest("AppVersion", typeof(LibraryFile).Assembly.GetName().Version?.ToString() ?? "");
                    db.SetManifest("RootPartId", libraryId.ToString("D"));
                    db.SetManifest("RootKind", RootKindLibrary);
                    db.SetManifest("LibraryName", name);
                    db.SetManifest("LibraryLayer", layer.ToString());
                    if (db.Manifest("CreatedUtc") == null) db.SetManifest("CreatedUtc", now);
                    db.SetManifest("ModifiedUtc", now);
                    db.CommitTransaction();
                }
                catch { db.Rollback(); throw; }
            }
            // swapped in only if nobody published while this was written
            if (readRevision != null && exists && HeadOf(path, libraryId) != readRevision) throw new LibraryConflictException(path);
            File.Copy(local, tmp, overwrite: true);
            File.Move(tmp, path, overwrite: true);
            return revision;
        }
        finally
        {
            try { File.Delete(local); } catch { /* best effort */ }
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    static Guid? HeadOf(string path, Guid libraryId) { using var db = PartDatabase.Open(path); return db.GetHead(libraryId)?.RevisionId; }

    /// <summary>The objects a library revision lists: one per table (by its key), and its restrictions (one, if any).</summary>
    internal static Dictionary<ObjectId, byte[]> Objects(IEnumerable<LibraryTable> tables, IEnumerable<LibraryRestriction>? restrictions)
    {
        var d = new Dictionary<ObjectId, byte[]>();
        foreach (var t in tables)
        {
            if (string.IsNullOrWhiteSpace(t.Key) || t.Key.Contains('/')) throw new ArgumentException($"A table key cannot be empty or hold '/': \"{t.Key}\".");
            if (t.Key == PartMaterial.CustomTable) throw new ArgumentException($"\"{t.Key}\" is kept for parts' own materials: a table cannot be keyed so.");
            foreach (var r in t.Rows)
            {
                if (r.Values.Count != t.Columns.Count) throw new ArgumentException($"{t.Key}/{r.Key} has {r.Values.Count} values for {t.Columns.Count} columns.");
                // (only a material's values may be missing, not known; never a size's, nor a material's density)
                for (int c = 0; c < r.Values.Count; c++)
                    if (!double.IsFinite(r.Values[c]) && (t.Kind != TableKinds.Material || t.Columns[c] == "density"))
                        throw new ArgumentException($"{t.Key}/{r.Key} has no {t.Columns[c]}.");
            }
            if (!d.TryAdd(PartObjects.IdOf(ObjectKind.LibraryTable, t.Key), PartObjects.Envelope(ObjectKind.LibraryTable, PartObjects.Json(t))))
                throw new ArgumentException($"Two tables are keyed \"{t.Key}\".");
        }
        var rs = restrictions?.OrderBy(r => r.Table, StringComparer.Ordinal).ToList();
        if (rs is { Count: > 0 }) d.Add(PartObjects.Single(ObjectKind.LibraryRestrictions), PartObjects.Envelope(ObjectKind.LibraryRestrictions, PartObjects.Json(rs)));
        return d;
    }

    /// <summary>A revision id standing for this content (the built-in library's: it has no file): the same data, the same id.</summary>
    public static Guid ContentRevision(IEnumerable<LibraryTable> tables, IEnumerable<LibraryRestriction>? restrictions)
    {
        var root = PartDatabase.RootHash(Objects(tables, restrictions).ToDictionary(kv => kv.Key, kv => Hash.Of(kv.Value)));
        return new Guid(root.ToBytes().AsSpan(0, 16));
    }

    /// <summary>The built-in standards written as a library file (the start of a shared standards library).</summary>
    public static Guid WriteBuiltIn(string path, string author) =>
        Publish(path, BuiltInStandards.LibraryId, BuiltInStandards.Name, LibraryLayer.Standards, BuiltInStandards.Tables, null, author, "SpryCAD built-in standards");
}

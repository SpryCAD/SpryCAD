using System.Globalization;
using SpryCad.Core.Storage;
using SpryCad.SheetMetal;

namespace SpryCad.Core.Library;

/// <summary>
/// What a library holds (docs/library-v2.md): standards (threads, holes, fasteners, a shop profile) or materials
/// (materials and appearances). The library in use for each is SpryCAD's defaults or a company's own, never both.
/// </summary>
public enum LibraryDomain { Standards, Materials }

public static class LibraryDomains
{
    /// <summary>The domain a table kind belongs in.</summary>
    public static LibraryDomain Of(string kind) => kind is TableKinds.Material or TableKinds.Appearance ? LibraryDomain.Materials : LibraryDomain.Standards;

    public static string Name(LibraryDomain d) => d == LibraryDomain.Materials ? "materials" : "standards";
}

/// <summary>
/// A library as read: its identity, what it holds, the revision read, its tables, and its shop profile tables (sheet
/// metal, in a standards library; null or empty: none).
/// </summary>
public sealed record LoadedLibrary(Guid LibraryId, string Name, LibraryDomain Domain, Guid RevisionId, string? Path,
    IReadOnlyList<LibraryTable> Tables, IReadOnlyList<ShopProfileTable>? ProfileTables = null);

/// <summary>A library file was published by someone else since it was read: publishing over it would lose their work.</summary>
public sealed class LibraryConflictException(string path) : IOException($"{System.IO.Path.GetFileName(path)} was published by someone else since it was read.");

/// <summary>
/// A library file (.sprlib, section 34): the same database as a part file (<see cref="PartDatabase"/>), its root a
/// library (manifest <c>RootKind = library</c>) whose objects are its tables (and a standards library's shop profile
/// tables), what it holds in the manifest (<c>LibraryDomain</c>). So libraries have
/// revisions, history, tags and diffs like parts. Users only ever read a copy (<see cref="LibrarySet"/>); publishing
/// writes a copy beside the file and swaps it in, refused if the file changed since it was read.
/// </summary>
public static class LibraryFile
{
    public const string Extension = ".sprlib";
    public const string RootKindLibrary = "library";
    /// <summary>The library format (docs/library-v2.md); a library of another one is refused.</summary>
    public const string FormatVersion = "2";

    static void CheckFormat(PartDatabase db, string path)
    {
        if (db.Manifest("FormatVersion") is var v && v != FormatVersion)
            throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is in an earlier library format ({v ?? "unknown"}) and cannot be read. "
                + "Make a new library from SpryCAD's defaults (Options > Libraries) and import its tables from Excel.");
    }

    /// <summary>Reads the library at its head revision.</summary>
    public static LoadedLibrary Read(string path)
    {
        using var db = PartDatabase.Open(path);
        if (db.Manifest("RootKind") != RootKindLibrary) throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not a SpryCAD library.");
        CheckFormat(db, path);
        var id = Guid.Parse(db.Manifest("RootPartId") ?? throw new InvalidDataException("The library names no id."));
        var head = db.GetHead(id) ?? throw new InvalidDataException("The library has nothing published.");
        var tables = new List<LibraryTable>();
        var profile = new List<ShopProfileTable>();
        foreach (var (oid, hash) in db.GetObjects(head.RevisionId).OrderBy(kv => kv.Key))
        {
            var bytes = db.Blobs.Get(hash);
            if (oid.Kind == ObjectKind.LibraryTable) tables.Add(PartObjects.Open<LibraryTable>(bytes, ObjectKind.LibraryTable));
            else if (oid.Kind == ObjectKind.ShopProfileTable) profile.Add(PartObjects.Open<ShopProfileTable>(bytes, ObjectKind.ShopProfileTable));
            // (other kinds: from a newer SpryCAD, left alone)
        }
        tables.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        profile.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var domain = Enum.TryParse<LibraryDomain>(db.Manifest("LibraryDomain"), out var d) ? d
            : tables.Any(t => LibraryDomains.Of(t.Kind) == LibraryDomain.Materials) ? LibraryDomain.Materials : LibraryDomain.Standards;
        return new LoadedLibrary(id, db.Manifest("LibraryName") ?? System.IO.Path.GetFileNameWithoutExtension(path), domain, head.RevisionId, path, tables, profile);
    }

    /// <summary>Whether the file is a library (its manifest), without reading it.</summary>
    public static bool IsLibrary(string path)
    {
        if (!PartDatabase.IsFile(path)) return false;
        using var db = PartDatabase.Open(path);
        return db.Manifest("RootKind") == RootKindLibrary;
    }

    /// <summary>
    /// Publishes the tables as the library's next revision (none when nothing changed), at
    /// <paramref name="path"/>: a new library when there is none. Refused (<see cref="LibraryConflictException"/>) when
    /// the file is not what <paramref name="readRevision"/> says was read (someone else published since), unless null.
    /// Returns the published revision. <paramref name="profileTables"/>: the library's shop profile tables (sheet metal).
    /// </summary>
    public static Guid Publish(string path, Guid libraryId, string name, LibraryDomain domain, IEnumerable<LibraryTable> tables,
        string author, string message = "", Guid? readRevision = null, IEnumerable<ShopProfileTable>? profileTables = null)
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
                    CheckFormat(db, path);
                    if (readRevision != null && db.GetHead(libraryId)?.RevisionId != readRevision) throw new LibraryConflictException(path);
                }
                db.Begin();
                try
                {
                    db.SetPart(libraryId, name);
                    var objects = Objects(tables, profileTables, domain).ToDictionary(kv => kv.Key, kv => db.Blobs.Put(kv.Value));
                    revision = (db.Commit(libraryId, objects, author, message) ?? db.GetHead(libraryId)!).RevisionId;
                    var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    db.SetManifest("FormatVersion", FormatVersion);
                    db.SetManifest("AppVersion", typeof(LibraryFile).Assembly.GetName().Version?.ToString() ?? "");
                    db.SetManifest("RootPartId", libraryId.ToString("D"));
                    db.SetManifest("RootKind", RootKindLibrary);
                    db.SetManifest("LibraryName", name);
                    db.SetManifest("LibraryDomain", domain.ToString());
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

    /// <summary>
    /// The objects a library revision lists: one per table (by its key) and one per shop profile table (by its key).
    /// A table of another domain's kind than <paramref name="domain"/> (when given), or a profile table with problems
    /// (<see cref="ShopProfileTable.Problems"/>) or outside a standards library, is refused.
    /// </summary>
    internal static Dictionary<ObjectId, byte[]> Objects(IEnumerable<LibraryTable> tables, IEnumerable<ShopProfileTable>? profileTables = null,
        LibraryDomain? domain = null)
    {
        var d = new Dictionary<ObjectId, byte[]>();
        foreach (var t in tables)
        {
            // (':' parts an item key, "ISO273:M8"; '/' parted it before library v2, so old keys read unambiguously)
            if (string.IsNullOrWhiteSpace(t.Key) || t.Key.IndexOfAny(new[] { ':', '/' }) >= 0) throw new ArgumentException($"A table key cannot be empty or hold ':' or '/': \"{t.Key}\".");
            if (t.Key == PartMaterial.CustomTable) throw new ArgumentException($"\"{t.Key}\" is kept for parts' own materials: a table cannot be keyed so.");
            if (Problem(t) is { } problem) throw new ArgumentException(problem);
            if (domain is { } dm && LibraryDomains.Of(t.Kind) is var td && td != dm)
                throw new ArgumentException($"{t.Key} ({t.Kind}) belongs in a {LibraryDomains.Name(td)} library, not a {LibraryDomains.Name(dm)} one.");
            if (!d.TryAdd(PartObjects.IdOf(ObjectKind.LibraryTable, t.Key), PartObjects.Envelope(ObjectKind.LibraryTable, PartObjects.Json(t))))
                throw new ArgumentException($"Two tables are keyed \"{t.Key}\".");
        }
        foreach (var t in profileTables ?? Array.Empty<ShopProfileTable>())
        {
            if (domain == LibraryDomain.Materials) throw new ArgumentException("A shop profile belongs in a standards library, not a materials one.");
            if (t.Problems().FirstOrDefault() is { } problem) throw new ArgumentException(problem);
            if (!d.TryAdd(PartObjects.IdOf(ObjectKind.ShopProfileTable, t.Key), PartObjects.Envelope(ObjectKind.ShopProfileTable, PartObjects.Json(t))))
                throw new ArgumentException($"Two shop profile tables are keyed \"{t.Key}\".");
        }
        return d;
    }

    /// <summary>
    /// What is wrong with a table by its kind's schema (null: nothing): a kind SpryCAD knows; value columns of the
    /// schema (an open kind's own too); every required column given, with a value in every row; rows keyed by the
    /// schema's key columns, each once.
    /// </summary>
    public static string? Problem(LibraryTable t) => Schemas.Check(t);

    /// <summary>A revision id standing for this content (the built-in library's: it has no file): the same data, the same id.</summary>
    public static Guid ContentRevision(IEnumerable<LibraryTable> tables, IEnumerable<ShopProfileTable>? profileTables = null)
    {
        var root = PartDatabase.RootHash(Objects(tables, profileTables).ToDictionary(kv => kv.Key, kv => Hash.Of(kv.Value)));
        return new Guid(root.ToBytes().AsSpan(0, 16));
    }

    /// <summary>
    /// A company's own library of a domain made from SpryCAD's defaults (New Company Library): a new library (its own
    /// id), its first revision the defaults' tables, to change from there.
    /// </summary>
    public static Guid WriteDefaults(string path, LibraryDomain domain, string name, string author) =>
        Publish(path, Guid.NewGuid(), name, domain, Defaults(domain), author, $"From SpryCAD's default {LibraryDomains.Name(domain)}");

    /// <summary>SpryCAD's default tables of a domain.</summary>
    public static IReadOnlyList<LibraryTable> Defaults(LibraryDomain domain) =>
        domain == LibraryDomain.Materials ? BuiltInMaterials.Tables : BuiltInStandards.Tables;
}

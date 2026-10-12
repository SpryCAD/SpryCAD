using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SpryCad.Core.Storage;

public enum RevisionKind { Save = 0, Working = 1, Revert = 2, Conversion = 3, Merge = 4 }

/// <summary>A revision: an immutable snapshot of one part (its objects), with its parent, author, time and message.</summary>
public sealed record RevisionInfo(Guid RevisionId, Guid PartId, Guid? ParentId, Hash RootHash, DateTime CreatedUtc, string Author, string Message, RevisionKind Kind);

/// <summary>
/// A SpryCAD file as an SQLite database (format: the file format spec, sections 2 and 3): a content-addressed blob
/// store, its parts' revisions (each part's history a parent chain) and tags, a manifest readable without the app,
/// and what is made from the model (thumbnail, display caches), which is never in a revision.
/// <para>
/// Ids are GUIDs (16 bytes), never counters, so rows copy between files without clashing. The file is marked as
/// SpryCAD's (<see cref="ApplicationId"/>) with its schema version (<see cref="SchemaVersion"/>); a newer schema is
/// refused. Opened without pooling: closing it lets go of the file at once (others may save over it).
/// </para>
/// </summary>
public sealed class PartDatabase : IDisposable
{
    /// <summary>"SPRY", at offset 68 of every SpryCAD database (file type rules read it there).</summary>
    public const int ApplicationId = 0x53505259;
    public const int SchemaVersion = 1;

    readonly SqliteConnection _db;
    public BlobStore Blobs { get; }

    PartDatabase(SqliteConnection db) { _db = db; Blobs = new BlobStore(db); }

    /// <summary>Whether the bytes start as a SpryCAD database (SQLite's header, then <see cref="ApplicationId"/> at offset 68).</summary>
    public static bool Is(ReadOnlySpan<byte> head) =>
        head.Length >= 72 && head[..16].SequenceEqual("SQLite format 3\0"u8)
        && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[68..]) == ApplicationId;

    public static bool IsFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[72];
            return fs.ReadAtLeast(head, 72, throwOnEndOfStream: false) == 72 && Is(head);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>A new, empty database at <paramref name="path"/> (replacing any file there).</summary>
    public static PartDatabase Create(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var db = Connect(path, SqliteOpenMode.ReadWriteCreate);
        // (the page size must be set before the first table)
        Exec(db, $"PRAGMA page_size = 4096; PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {SchemaVersion};");
        Exec(db, Schema);
        return new PartDatabase(db);
    }

    public static PartDatabase Open(string path, bool readOnly = true)
    {
        var db = Connect(path, readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite);
        try
        {
            if (Scalar<long>(db, "PRAGMA application_id") != ApplicationId) throw new InvalidDataException("Not a SpryCAD file.");
            var v = Scalar<long>(db, "PRAGMA user_version");
            if (v > SchemaVersion) throw new InvalidDataException($"The file was saved by a newer SpryCAD (file version {v}); update SpryCAD to open it.");
        }
        catch { db.Dispose(); throw; }
        return new PartDatabase(db);
    }

    static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        SqliteSetup.Ensure();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();
        var db = new SqliteConnection(cs);
        db.Open();
        Exec(db, "PRAGMA foreign_keys = ON;");
        return db;
    }

    public void Dispose() => _db.Dispose();

    // ------------------------------------------------------------------------------------------
    // schema

    const string Schema = """
        CREATE TABLE Manifest (
          Key   TEXT PRIMARY KEY,
          Value TEXT NOT NULL
        );
        CREATE TABLE Blobs (
          Hash    BLOB PRIMARY KEY,   -- SHA-256 of the uncompressed bytes
          Codec   INTEGER NOT NULL,   -- 0 none, 1 zstd
          RawSize INTEGER NOT NULL,
          Data    BLOB NOT NULL
        );
        CREATE TABLE Parts (
          PartId            BLOB PRIMARY KEY,
          Name              TEXT NOT NULL,
          HeadRevisionId    BLOB,
          WorkingRevisionId BLOB
        );
        CREATE TABLE Revisions (
          RevisionId    BLOB PRIMARY KEY,
          PartId        BLOB NOT NULL REFERENCES Parts(PartId),
          ParentId      BLOB,
          MergeParentId BLOB,
          RootHash      BLOB NOT NULL,
          CreatedUtc    TEXT NOT NULL,
          Author        TEXT NOT NULL,
          Message       TEXT NOT NULL,
          Kind          INTEGER NOT NULL
        );
        CREATE INDEX IX_Revisions_Part ON Revisions(PartId, CreatedUtc);
        CREATE TABLE RevisionObjects (
          RevisionId BLOB NOT NULL REFERENCES Revisions(RevisionId),
          ObjectId   BLOB NOT NULL,
          Hash       BLOB NOT NULL REFERENCES Blobs(Hash),
          PRIMARY KEY (RevisionId, ObjectId)
        ) WITHOUT ROWID;
        CREATE TABLE Tags (
          PartId     BLOB NOT NULL REFERENCES Parts(PartId),
          Name       TEXT NOT NULL,
          RevisionId BLOB NOT NULL REFERENCES Revisions(RevisionId),
          CreatedUtc TEXT NOT NULL,
          Kind       INTEGER NOT NULL DEFAULT 0,
          Release    TEXT,
          PRIMARY KEY (PartId, Name)
        ) WITHOUT ROWID;
        CREATE TABLE Cache (
          CacheKey BLOB PRIMARY KEY,
          PartId   BLOB NOT NULL,
          Kind     INTEGER NOT NULL,   -- 0 display mesh
          Hash     BLOB NOT NULL REFERENCES Blobs(Hash)
        ) WITHOUT ROWID;
        CREATE TABLE Thumbnail (
          Size INTEGER PRIMARY KEY,    -- longest edge in pixels
          Png  BLOB NOT NULL           -- a plain PNG, readable with one query and no SpryCAD code
        );
        """;

    // ------------------------------------------------------------------------------------------
    // manifest

    public string? Manifest(string key)
    {
        using var cmd = Cmd("SELECT Value FROM Manifest WHERE Key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetManifest(string key, string? value)
    {
        using var cmd = value == null ? Cmd("DELETE FROM Manifest WHERE Key = $k", ("$k", key))
            : Cmd("INSERT OR REPLACE INTO Manifest (Key, Value) VALUES ($k, $v)", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------------------------------
    // parts and revisions

    /// <summary>Adds the part (if new) or renames it.</summary>
    public void SetPart(Guid partId, string name)
    {
        using var cmd = Cmd("INSERT INTO Parts (PartId, Name) VALUES ($p, $n) ON CONFLICT(PartId) DO UPDATE SET Name = $n", ("$p", partId.ToByteArray()), ("$n", name));
        cmd.ExecuteNonQuery();
    }

    public RevisionInfo? GetHead(Guid partId)
    {
        using var cmd = Cmd("SELECT HeadRevisionId FROM Parts WHERE PartId = $p", ("$p", partId.ToByteArray()));
        return cmd.ExecuteScalar() is byte[] id ? GetRevision(new Guid(id)) : null;
    }

    public RevisionInfo? GetRevision(Guid revisionId)
    {
        using var cmd = Cmd("SELECT RevisionId, PartId, ParentId, RootHash, CreatedUtc, Author, Message, Kind FROM Revisions WHERE RevisionId = $r", ("$r", revisionId.ToByteArray()));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRevision(r) : null;
    }

    static RevisionInfo ReadRevision(SqliteDataReader r) => new(
        new Guid((byte[])r[0]), new Guid((byte[])r[1]), r.IsDBNull(2) ? null : new Guid((byte[])r[2]),
        Hash.From((byte[])r[3]), DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        r.GetString(5), r.GetString(6), (RevisionKind)r.GetInt32(7));

    /// <summary>A revision's objects (id → blob hash), in one query.</summary>
    public Dictionary<ObjectId, Hash> GetObjects(Guid revisionId)
    {
        var d = new Dictionary<ObjectId, Hash>();
        using var cmd = Cmd("SELECT ObjectId, Hash FROM RevisionObjects WHERE RevisionId = $r", ("$r", revisionId.ToByteArray()));
        using var r = cmd.ExecuteReader();
        while (r.Read()) d.Add(new ObjectId((byte[])r[0]), Hash.From((byte[])r[1]));
        return d;
    }

    /// <summary>
    /// The root hash of a set of objects (a Merkle root): each object's id and hash, in id order, hashed together.
    /// Two revisions with the same content have the same root hash.
    /// </summary>
    public static Hash RootHash(IReadOnlyDictionary<ObjectId, Hash> objects)
    {
        var buf = new byte[objects.Count * (PartObjects.IdSize + Hash.Size)];
        int at = 0;
        foreach (var (id, h) in objects.OrderBy(kv => kv.Key))
        {
            id.Bytes.CopyTo(buf.AsSpan(at)); at += PartObjects.IdSize;
            h.ToBytes().CopyTo(buf, at); at += Hash.Size;
        }
        return Hash.Of(buf);
    }

    /// <summary>
    /// Records a revision of the part with these objects (their blobs already stored), its parent the part's head, and
    /// makes it the head. Nothing when the content is the head's (null then). Within the caller's transaction.
    /// </summary>
    public RevisionInfo? Commit(Guid partId, IReadOnlyDictionary<ObjectId, Hash> objects, string author, string message, RevisionKind kind = RevisionKind.Save)
    {
        var root = RootHash(objects);
        var head = GetHead(partId);
        if (head != null && head.RootHash == root) return null;
        var rev = new RevisionInfo(Guid.NewGuid(), partId, head?.RevisionId, root, DateTime.UtcNow, author, message, kind);
        Insert(rev, objects);
        using (var cmd = Cmd("UPDATE Parts SET HeadRevisionId = $r WHERE PartId = $p", ("$r", rev.RevisionId.ToByteArray()), ("$p", partId.ToByteArray())))
            cmd.ExecuteNonQuery();
        return rev;
    }

    void Insert(RevisionInfo rev, IReadOnlyDictionary<ObjectId, Hash> objects)
    {
        using (var cmd = Cmd("""
            INSERT INTO Revisions (RevisionId, PartId, ParentId, RootHash, CreatedUtc, Author, Message, Kind)
            VALUES ($r, $p, $parent, $root, $t, $a, $m, $k)
            """, ("$r", rev.RevisionId.ToByteArray()), ("$p", rev.PartId.ToByteArray()), ("$parent", (object?)rev.ParentId?.ToByteArray() ?? DBNull.Value),
            ("$root", rev.RootHash.ToBytes()), ("$t", rev.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
            ("$a", rev.Author), ("$m", rev.Message), ("$k", (int)rev.Kind)))
            cmd.ExecuteNonQuery();
        using (var cmd = Cmd("INSERT INTO RevisionObjects (RevisionId, ObjectId, Hash) VALUES ($r, $o, $h)"))
        {
            var o = cmd.Parameters.Add("$o", SqliteType.Blob); var h = cmd.Parameters.Add("$h", SqliteType.Blob);
            cmd.Parameters.AddWithValue("$r", rev.RevisionId.ToByteArray());
            foreach (var (id, hash) in objects) { o.Value = id.ToArray(); h.Value = hash.ToBytes(); cmd.ExecuteNonQuery(); }
        }
    }

    /// <summary>
    /// Records the part's working revision (autosave: never in its history, never tagged) with these objects, its
    /// parent the head, in place of the one it had; none when the content is the head's. Within the caller's
    /// transaction.
    /// </summary>
    public RevisionInfo? CommitWorking(Guid partId, IReadOnlyDictionary<ObjectId, Hash> objects, string author)
    {
        DropWorking(partId);
        var root = RootHash(objects);
        var head = GetHead(partId);
        if (head != null && head.RootHash == root) return null;
        var rev = new RevisionInfo(Guid.NewGuid(), partId, head?.RevisionId, root, DateTime.UtcNow, author, "", RevisionKind.Working);
        Insert(rev, objects);
        using (var cmd = Cmd("UPDATE Parts SET WorkingRevisionId = $r WHERE PartId = $p", ("$r", rev.RevisionId.ToByteArray()), ("$p", partId.ToByteArray())))
            cmd.ExecuteNonQuery();
        return rev;
    }

    /// <summary>The part's working revision, if it has one.</summary>
    public RevisionInfo? GetWorking(Guid partId)
    {
        using var cmd = Cmd("SELECT WorkingRevisionId FROM Parts WHERE PartId = $p", ("$p", partId.ToByteArray()));
        return cmd.ExecuteScalar() is byte[] id ? GetRevision(new Guid(id)) : null;
    }

    /// <summary>Removes the part's working revision (saved, or discarded) and the blobs only it used.</summary>
    public void DropWorking(Guid partId)
    {
        if (GetWorking(partId) is not { } w) return;
        var hashes = GetObjects(w.RevisionId).Values.Select(h => h.ToBytes()).ToList();
        using (var cmd = Cmd("UPDATE Parts SET WorkingRevisionId = NULL WHERE PartId = $p", ("$p", partId.ToByteArray()))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd("DELETE FROM RevisionObjects WHERE RevisionId = $r", ("$r", w.RevisionId.ToByteArray()))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd("DELETE FROM Revisions WHERE RevisionId = $r", ("$r", w.RevisionId.ToByteArray()))) cmd.ExecuteNonQuery();
        foreach (var h in hashes) DeleteBlobIfUnused(h);
    }

    internal void DeleteBlobIfUnused(byte[] hash)
    {
        using var cmd = Cmd("""
            DELETE FROM Blobs WHERE Hash = $h
              AND NOT EXISTS (SELECT 1 FROM Cache WHERE Hash = $h)
              AND NOT EXISTS (SELECT 1 FROM RevisionObjects WHERE Hash = $h)
            """, ("$h", hash));
        cmd.ExecuteNonQuery();
    }

    /// <summary>The part's history, head first (following each revision's parent).</summary>
    public List<RevisionInfo> GetHistory(Guid partId)
    {
        var l = new List<RevisionInfo>();
        for (var r = GetHead(partId); r != null; r = r.ParentId is { } p ? GetRevision(p) : null) l.Add(r);
        return l;
    }

    // ------------------------------------------------------------------------------------------
    // tags: names for revisions, unique per part; moving one is always explicit

    public void SetTag(Guid partId, string name, Guid revisionId)
    {
        using var cmd = Cmd("""
            INSERT INTO Tags (PartId, Name, RevisionId, CreatedUtc) VALUES ($p, $n, $r, $t)
            ON CONFLICT(PartId, Name) DO UPDATE SET RevisionId = $r
            """, ("$p", partId.ToByteArray()), ("$n", name), ("$r", revisionId.ToByteArray()), ("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
        cmd.ExecuteNonQuery();
    }

    public void DeleteTag(Guid partId, string name)
    {
        using var cmd = Cmd("DELETE FROM Tags WHERE PartId = $p AND Name = $n", ("$p", partId.ToByteArray()), ("$n", name));
        cmd.ExecuteNonQuery();
    }

    public List<(string Name, Guid RevisionId)> GetTags(Guid partId)
    {
        var l = new List<(string, Guid)>();
        using var cmd = Cmd("SELECT Name, RevisionId FROM Tags WHERE PartId = $p ORDER BY Name", ("$p", partId.ToByteArray()));
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add((r.GetString(0), new Guid((byte[])r[1])));
        return l;
    }

    // ------------------------------------------------------------------------------------------
    // made from the model: thumbnail and display caches (never in a revision)

    public void SetThumbnail(byte[]? png)
    {
        Exec(_db, "DELETE FROM Thumbnail");
        if (png == null) return;
        using var cmd = Cmd("INSERT INTO Thumbnail (Size, Png) VALUES ($s, $p)", ("$s", PngSize(png)), ("$p", png));
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetThumbnail()
    {
        using var cmd = Cmd("SELECT Png FROM Thumbnail ORDER BY Size DESC LIMIT 1");
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>The longest edge of a PNG (its header), 0 if it is not one.</summary>
    static int PngSize(byte[] png) => png.Length >= 24 && png[12] == 'I' && png[13] == 'H' && png[14] == 'D' && png[15] == 'R'
        ? Math.Max(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)))
        : 0;

    const int DisplayKind = 0;

    /// <summary>
    /// The part's display (a display cache, <see cref="Assembly.MeshCache"/>) under <paramref name="key"/>, in place of
    /// the one it had (null: none). Only the latest is kept: a display is large, and older content can be rebuilt.
    /// </summary>
    public void SetDisplay(Guid partId, Hash key, byte[]? data)
    {
        var old = new List<byte[]>();
        using (var cmd = Cmd("SELECT Hash FROM Cache WHERE PartId = $p AND Kind = $k", ("$p", partId.ToByteArray()), ("$k", DisplayKind)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) old.Add((byte[])r[0]);
        using (var cmd = Cmd("DELETE FROM Cache WHERE PartId = $p AND Kind = $k", ("$p", partId.ToByteArray()), ("$k", DisplayKind)))
            cmd.ExecuteNonQuery();
        if (data != null)
        {
            var h = Blobs.Put(data);
            using var cmd = Cmd("INSERT INTO Cache (CacheKey, PartId, Kind, Hash) VALUES ($key, $p, $k, $h)",
                ("$key", key.ToBytes()), ("$p", partId.ToByteArray()), ("$k", DisplayKind), ("$h", h.ToBytes()));
            cmd.ExecuteNonQuery();
        }
        // the displays replaced, unless something else holds the same bytes
        foreach (var h in old) DeleteBlobIfUnused(h);
    }

    public byte[]? GetCache(Hash key)
    {
        using var cmd = Cmd("SELECT Hash FROM Cache WHERE CacheKey = $k", ("$k", key.ToBytes()));
        return cmd.ExecuteScalar() is byte[] h ? Blobs.Get(Hash.From(h)) : null;
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Runs a statement (maintenance: <see cref="PartHistory"/>).</summary>
    public void Execute(string sql, params (string name, object value)[] ps)
    {
        using var cmd = Cmd(sql, ps);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The rows a query gives, each read by <paramref name="read"/>.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string name, object value)[] ps)
    {
        var l = new List<T>();
        using var cmd = Cmd(sql, ps);
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add(read(r));
        return l;
    }

    public void Begin() => Exec(_db, "BEGIN IMMEDIATE");
    public void CommitTransaction() => Exec(_db, "COMMIT");
    public void Rollback() { try { Exec(_db, "ROLLBACK"); } catch (SqliteException) { } }

    SqliteCommand Cmd(string sql, params (string name, object value)[] ps)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static T Scalar<T>(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}

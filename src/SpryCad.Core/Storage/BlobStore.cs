using Microsoft.Data.Sqlite;

namespace SpryCad.Core.Storage;

/// <summary>
/// The content-addressed store (the Blobs table): each blob kept once, by the hash of its uncompressed bytes, so a
/// change of compression never changes what it is. Compressed with zstd (level 3) when that saves a tenth or more;
/// small blobs and already-compressed formats (PNG, JPEG, ZIP, gzip, zstd) are kept as they are.
/// </summary>
public sealed class BlobStore
{
    const int CodecNone = 0, CodecZstd = 1;
    const int MinCompress = 256;

    readonly SqliteConnection _db;

    public BlobStore(SqliteConnection db) { _db = db; }

    /// <summary>Stores the bytes (if not already there) and returns their hash.</summary>
    public Hash Put(ReadOnlySpan<byte> data)
    {
        var hash = Hash.Of(data);
        if (Contains(hash)) return hash;
        int codec = CodecNone;
        byte[] stored = data.ToArray();
        if (data.Length >= MinCompress && !Compressed(data))
        {
            using var z = new ZstdSharp.Compressor(3);
            var packed = z.Wrap(data).ToArray();
            if (packed.Length <= data.Length * 0.9) { stored = packed; codec = CodecZstd; }
        }
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO Blobs (Hash, Codec, RawSize, Data) VALUES ($h, $c, $n, $d)";
        cmd.Parameters.AddWithValue("$h", hash.ToBytes());
        cmd.Parameters.AddWithValue("$c", codec);
        cmd.Parameters.AddWithValue("$n", data.Length);
        cmd.Parameters.AddWithValue("$d", stored);
        cmd.ExecuteNonQuery();
        return hash;
    }

    public bool Contains(Hash hash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Blobs WHERE Hash = $h";
        cmd.Parameters.AddWithValue("$h", hash.ToBytes());
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// The blob's bytes. Checked against its hash when <paramref name="verify"/> (embedded files: as a ZIP's checksums
    /// did) and always in debug builds; model objects are not, for speed (damage shows as JSON that does not read).
    /// </summary>
    public byte[] Get(Hash hash, bool verify = false)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT Codec, RawSize, Data FROM Blobs WHERE Hash = $h";
        cmd.Parameters.AddWithValue("$h", hash.ToBytes());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidDataException($"The file is damaged: blob {hash} is missing.");
        var codec = r.GetInt32(0); var size = r.GetInt64(1); var data = (byte[])r[2];
        var raw = codec switch
        {
            CodecNone => data,
            CodecZstd => Unpack(data, size),
            _ => throw new InvalidDataException($"The file uses an unknown compression ({codec}): made by a newer SpryCAD?"),
        };
#if DEBUG
        verify = true;
#endif
        if (verify && Hash.Of(raw) != hash) throw new InvalidDataException($"The file is damaged: blob {hash} does not match its content.");
        return raw;
    }

    static byte[] Unpack(byte[] data, long size)
    {
        using var z = new ZstdSharp.Decompressor();
        var raw = z.Unwrap(data).ToArray();
        if (raw.Length != size) throw new InvalidDataException("The file is damaged: a blob does not unpack to its size.");
        return raw;
    }

    /// <summary>Formats that are compressed already (by their first bytes): compressing them again only costs time.</summary>
    static bool Compressed(ReadOnlySpan<byte> d) =>
        d.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' })
        || d.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })               // JPEG
        || d.StartsWith(new byte[] { (byte)'P', (byte)'K', 3, 4 })     // ZIP (and the formats built on it)
        || d.StartsWith(new byte[] { 0x1F, 0x8B })                     // gzip
        || d.StartsWith(new byte[] { 0x28, 0xB5, 0x2F, 0xFD });        // zstd
}

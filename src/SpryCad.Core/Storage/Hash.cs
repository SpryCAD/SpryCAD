using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SpryCad.Core.Storage;

/// <summary>
/// A SHA-256 hash: the identity of a blob (of its uncompressed bytes) and of a revision's content (its root hash).
/// Four ulongs rather than a byte array, so equal hashes are equal values (dictionary keys, comparisons).
/// </summary>
public readonly record struct Hash(ulong A, ulong B, ulong C, ulong D)
{
    public const int Size = 32;

    public static Hash Of(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[Size];
        SHA256.HashData(data, h);
        return From(h);
    }

    public static Hash From(ReadOnlySpan<byte> sha256) => new(
        BinaryPrimitives.ReadUInt64BigEndian(sha256),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[8..]),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[16..]),
        BinaryPrimitives.ReadUInt64BigEndian(sha256[24..]));

    /// <summary>The 32 bytes, as stored.</summary>
    public byte[] ToBytes()
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt64BigEndian(b, A);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), B);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(16), C);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(24), D);
        return b;
    }

    public override string ToString() => Convert.ToHexString(ToBytes()).ToLowerInvariant();
}

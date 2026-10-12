using System.Security.Cryptography;
using System.Text;

namespace SpryCad.Core.Model;

/// <summary>
/// Ids of features ("F" + 12 base-32 characters, 60 random bits) and of sketch points, entities, relations and
/// convert groups (random 53-bit numbers: exact in any JSON reader). Random rather than counted, so two copies of
/// a part edited apart never give two different things the same id (history, diffs and merging go by id).
/// Ids are never shown: the UI shows names (<see cref="Feature.Name"/>, "Line3").
/// <para>
/// Ids written in face names (sketch ids in base 32) always hold a letter, so they never look like the counted
/// ids of parts made before (<see cref="LegacyIds"/>), which are all digits.
/// </para>
/// </summary>
public static class Ids
{
    const string Digits = "0123456789abcdefghjkmnpqrstvwxyz"; // Crockford's base 32, lower case
    const long SketchMask = (1L << 53) - 1;

    public static string NewFeatureId() => FeatureId(Random64());

    public static long NewSketchId() => SketchId(Random64());

    /// <summary>A sketch id as written in face names ("L" + this for a line's side face).</summary>
    public static string Name(long id)
    {
        Span<char> s = stackalloc char[13];
        int n = s.Length;
        do { s[--n] = Digits[(int)(id & 31)]; id >>= 5; } while (id != 0);
        return new string(s[n..]);
    }

    /// <summary>A feature id made from 64 bits (the low 60 are used); one holding no letter is moved on until it does.</summary>
    internal static string FeatureId(ulong bits)
    {
        while (true)
        {
            Span<char> s = stackalloc char[13];
            s[0] = 'F';
            var v = bits;
            for (int i = 12; i >= 1; i--) { s[i] = Digits[(int)(v & 31)]; v >>= 5; }
            var id = new string(s);
            if (HasLetter(id.AsSpan(1))) return id;
            bits = Mix(bits);
        }
    }

    /// <summary>A sketch id made from 64 bits: positive (0 is the sketch origin), 53 bits, with a letter in its <see cref="Name"/>.</summary>
    internal static long SketchId(ulong bits)
    {
        while (true)
        {
            var id = (long)(bits & SketchMask);
            if (id != 0 && HasLetter(Name(id))) return id;
            bits = Mix(bits);
        }
    }

    /// <summary>64 bits from a hash of the parts, for ids derived from others (<see cref="LegacyIds"/>).</summary>
    internal static ulong Hash(params string[] parts)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(h);
    }

    static ulong Random64() => BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

    static ulong Mix(ulong x) { x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; return x + 0x9e3779b97f4a7c15UL; }

    static bool HasLetter(ReadOnlySpan<char> s) { foreach (var c in s) if (c > '9') return true; return false; }
}

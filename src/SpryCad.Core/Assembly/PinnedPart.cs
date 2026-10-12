using SpryCad.Core.Model;
using SpryCad.Core.Storage;

namespace SpryCad.Core.Assembly;

/// <summary>
/// A pinned component's part (the file format spec, section 8): the revision it is pinned to, from the part's history,
/// or the revision its tag names now. Refused, saying why, when the file no longer has it (or has no history: a part
/// saved before databases).
/// </summary>
public static class PinnedPart
{
    public static CadDocument Load(string file, PartRef pin)
    {
        if (!PartDatabase.IsFile(file)) throw new InvalidDataException("its file was saved by an older SpryCAD and has no history to pin to");
        Guid rev;
        using (var db = PartDatabase.Open(file)) rev = Resolve(db, pin);
        return PartFile.Load(file, revision: rev);
    }

    public static CadDocument Load(byte[] bytes, PartRef pin)
    {
        if (!PartDatabase.Is(bytes)) throw new InvalidDataException("it was saved by an older SpryCAD and has no history to pin to");
        var tmp = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + CadDocument.FileExtension);
        try { File.WriteAllBytes(tmp, bytes); return Load(tmp, pin); }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>The revision pinned to: the tag's now (kept as the pin last found), else the revision named.</summary>
    static Guid Resolve(PartDatabase db, PartRef pin)
    {
        var partId = Guid.Parse(db.Manifest("RootPartId") ?? throw new InvalidDataException("the part's file names no part"));
        if (pin.PinTag is { } tag)
        {
            var t = db.GetTags(partId).FirstOrDefault(x => x.Name == tag);
            if (t.Name == null) throw new InvalidDataException($"the tag \"{tag}\" it is pinned to is no longer in the part's history");
            pin.Pin = t.RevisionId.ToString("D");
            return t.RevisionId;
        }
        if (!Guid.TryParse(pin.Pin, out var rev) || db.GetRevision(rev) == null)
            throw new InvalidDataException("the revision it is pinned to is no longer in the part's history (purged?)");
        return rev;
    }
}

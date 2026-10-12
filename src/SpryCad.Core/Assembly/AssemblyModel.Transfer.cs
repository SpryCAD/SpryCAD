using SpryCad.Core.Model;
using SpryCad.Core.Storage;

namespace SpryCad.Core.Assembly;

/// <summary>What became of a part's original file when it was made virtual (for telling the user).</summary>
public sealed record MadeVirtual(string OriginalPath, bool Marked, IReadOnlyList<string> UsedBy);

/// <summary>How a virtual part and the file it was made from compare (the file format spec, section 8, divergence).</summary>
public enum Divergence
{
    /// <summary>The same, or only the virtual copy changed since.</summary>
    None,
    /// <summary>Only the file changed since: its changes can be pulled in as they are.</summary>
    FileAhead,
    /// <summary>Both changed: one of them has to be chosen (there is no merging yet).</summary>
    Both,
}

/// <summary>A virtual part whose original file changed after it was made virtual.</summary>
public sealed record OriginalChanged(string Embedded, string PartName, string File, Divergence Kind);

public sealed partial class AssemblyModel
{
    /// <summary>
    /// Makes an embedded (virtual) part or sub-assembly a file of its own at <paramref name="target"/> (Make External):
    /// its history goes with it (the same database copied out), and every component of it uses the file from now on.
    /// The assembly's earlier revisions keep the embedded copy.
    /// </summary>
    public void MakeExternal(Component c, string target)
    {
        if (c.Part.Embedded is not { } name || !Doc.Files.TryGetValue(name, out var bytes)) throw new InvalidOperationException($"{c.Name} is not embedded in the assembly.");
        target = System.IO.Path.GetFullPath(target);
        if (IsAssemblyFile(target) != c.IsAssembly) throw new ArgumentException(c.IsAssembly ? "A sub-assembly is made an assembly file (.sasm)." : "A part is made a part file (.sprt).");
        var oldKey = ComponentResolver.Key(c.Part);
        // the file written beside where it goes, then put in place (a part from before databases saved as one)
        var tmp = target + ".saving";
        try
        {
            if (PartDatabase.Is(bytes)) File.WriteAllBytes(tmp, bytes);
            else if (c.IsAssembly) AssemblyContainer.Load(bytes, name).Save(tmp);
            else DocumentContainer.Load(bytes, name).Save(tmp);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        var (id, rev) = DocumentContainer.ReadIdentity(target);
        var dir = Path != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) : null;
        foreach (var o in ComponentsOf(oldKey).ToList())
        {
            o.Part.Embedded = null;
            o.Part.AbsolutePath = target;
            o.Part.Path = dir != null ? ComponentResolver.Relative(dir, target) : System.IO.Path.GetFileName(target);
            o.Part.Revision = rev;
        }
        Doc.Files.Remove(name);
        if (id != null) Index?.Record(id, rev, target);
        Forget(oldKey);
        // (found where it was just written)
        Resolution.Parts[ComponentResolver.Key(c.Part)] = new PartResolution(PartStatus.Found, target, false, null);
        Doc.SaveMessage = $"Made {System.IO.Path.GetFileNameWithoutExtension(target)} external";
        Doc.SaveKind = RevisionKind.Conversion;
        Sync();
    }

    /// <summary>
    /// Makes a part or sub-assembly used from its file embedded in the assembly (Make Virtual): its history comes with it,
    /// and every component of it uses the embedded copy from now on. The file is never deleted or renamed here (other
    /// assemblies may use it); it is marked as made virtual in this assembly when it can be (<see cref="Supersede"/>).
    /// </summary>
    public MadeVirtual MakeVirtual(Component c, string user)
    {
        if (c.Part.Embedded != null) throw new InvalidOperationException($"{c.Name} is embedded in the assembly already.");
        var oldKey = ComponentResolver.Key(c.Part);
        if (Resolution.Parts.GetValueOrDefault(oldKey) is not { Status: PartStatus.Found or PartStatus.Moved, Path: { } file })
            throw new InvalidOperationException($"{c.Name}'s file was not found, so it cannot be embedded.");
        var bytes = File.ReadAllBytes(file);
        var name = UniqueName((c.IsAssembly ? "subs/" : "parts/") + System.IO.Path.GetFileName(file));
        Doc.Files[name] = bytes;
        // (the file's place stays known: where its original is, for changes made to it later)
        foreach (var o in ComponentsOf(oldKey).ToList()) o.Part.Embedded = name;
        Forget(oldKey);
        Doc.SaveMessage = $"Made {System.IO.Path.GetFileNameWithoutExtension(file)} virtual";
        Doc.SaveKind = RevisionKind.Conversion;
        Sync();
        var marked = Doc.Id != null && Supersede.Mark(file, Path, Doc.Id, user);
        return new MadeVirtual(file, marked, UsedBy(c.Part.PartId, file));
    }

    /// <summary>
    /// The other assembly files (in this assembly's folder and below) that use the part from its file: the original
    /// cannot be deleted while any does.
    /// </summary>
    public List<string> UsedBy(string? partId, string partFile)
    {
        var found = new List<string>();
        if (partId == null || Path == null || System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is not { } root) return found;
        foreach (var f in Directory.EnumerateFiles(root, "*" + AssemblyDocument.FileExtension, SearchOption.AllDirectories))
        {
            if (PartIndex.SamePath(f, Path)) continue;
            try
            {
                var asm = AssemblyContainer.Load(f);
                if (asm.Components.Any(o => o.Part.PartId == partId && o.Part.Embedded == null)) found.Add(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException) { }
        }
        return found;
    }

    /// <summary>
    /// The virtual parts whose original files changed after they were made virtual here (the file still there, marked as
    /// made virtual in this assembly): pulled in when only the file changed, chosen between when both did.
    /// </summary>
    public List<OriginalChanged> ChangedOriginals()
    {
        var list = new List<OriginalChanged>();
        if (Doc.Id == null) return list;
        foreach (var g in Doc.Components.Where(c => c.Part.Embedded != null && !c.IsAssembly && c.Part.PartId != null).GroupBy(c => c.Part.Embedded!))
        {
            var r = g.First().Part;
            var file = r.AbsolutePath != null && File.Exists(r.AbsolutePath) ? r.AbsolutePath
                : Path != null && r.Path != null && File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? "", r.Path))
                    ? System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? "", r.Path)) : null;
            if (file == null || !Doc.Files.TryGetValue(g.Key, out var bytes) || Supersede.Read(file) is not { } mark || mark.AssemblyId != Doc.Id) continue;
            try
            {
                if (DocumentContainer.ReadIdentity(file).id != r.PartId) continue;
                var mine = History(bytes); var theirs = PartFile.History(file).Select(h => h.RevisionId).ToList();
                if (mine.Count == 0 || theirs.Count == 0 || mine[0] == theirs[0]) continue;
                var kind = theirs.Contains(mine[0]) ? Divergence.FileAhead : mine.Contains(theirs[0]) ? Divergence.None : Divergence.Both;
                if (kind != Divergence.None) list.Add(new OriginalChanged(g.Key, System.IO.Path.GetFileNameWithoutExtension(file), file, kind));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { }
        }
        return list;
    }

    /// <summary>
    /// Takes the original file's part as the virtual one (Pull Changes In, or the file chosen when both changed): its
    /// history with it. The file keeps its mark.
    /// </summary>
    public void TakeOriginal(OriginalChanged o)
    {
        var key = Doc.Components.Where(c => c.Part.Embedded == o.Embedded).Select(c => ComponentResolver.Key(c.Part)).First();
        Doc.Files[o.Embedded] = File.ReadAllBytes(o.File);
        var rev = DocumentContainer.ReadIdentity(o.File).revision;
        foreach (var c in Doc.Components.Where(c => c.Part.Embedded == o.Embedded)) c.Part.Revision = rev;
        Doc.SaveMessage = o.Kind == Divergence.FileAhead ? $"Pulled in the changes made to {o.PartName}'s file" : $"Took {o.PartName} from its file";
        Doc.SaveKind = RevisionKind.Conversion;
        Forget(key);
        Sync();
    }

    // ------------------------------------------------------------------------------------------
    // pinned components

    /// <summary>
    /// Pins a component to a revision of its part (or, with <paramref name="tag"/>, to whatever revision that tag names):
    /// it shows that revision from now on, whatever the part becomes. Null revision and tag: back to the latest.
    /// </summary>
    public void Pin(Component c, Guid? revision, string? tag = null)
    {
        if (c.IsAssembly) throw new InvalidOperationException("Sub-assemblies are not pinned yet: only parts.");
        c.Part.Pin = revision?.ToString("D");
        c.Part.PinTag = tag;
        c.Part.Revision = revision?.ToString("D") ?? c.Part.Revision;
        LoadPart(ComponentResolver.Key(c.Part));
    }

    /// <summary>A component's part's saved revisions, newest first, with their tags (from its file or embedded copy).</summary>
    public List<HistoryEntry> HistoryOf(Component c)
    {
        var key = ComponentResolver.Key(c.Part);
        if (c.Part.Embedded is { } e && Doc.Files.TryGetValue(e, out var bytes))
        {
            if (!PartDatabase.Is(bytes)) return new();
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + CadDocument.FileExtension);
            try { File.WriteAllBytes(tmp, bytes); using var db = PartDatabase.Open(tmp); return PartHistory.Read(db); }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
        if (Resolution.Parts.GetValueOrDefault(key) is { Status: PartStatus.Found or PartStatus.Moved, Path: { } file } && PartDatabase.IsFile(file))
        {
            using var db = PartDatabase.Open(file);
            return PartHistory.Read(db);
        }
        return new();
    }

    /// <summary>A part database's revision ids, head first (from its bytes).</summary>
    static List<Guid> History(byte[] bytes)
    {
        if (!PartDatabase.Is(bytes)) return new();
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + CadDocument.FileExtension);
        try { File.WriteAllBytes(tmp, bytes); return PartFile.History(tmp).Select(h => h.RevisionId).ToList(); }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    /// <summary>Drops what was loaded for a part (or sub-assembly) by its old key: loaded again by its new one.</summary>
    void Forget(string key)
    {
        foreach (var k in Parts.Keys.Where(k => OfPart(k, key)).ToList()) Parts.Remove(k);
        foreach (var k in Subs.Keys.Where(k => OfPart(k, key)).ToList()) Subs.Remove(k);
    }
}

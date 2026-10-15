using System.IO.Compression;
using System.Text;
using SpryCad.Core.Model;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Reads and writes .sasm files. An assembly is saved as a database (<see cref="Storage.AssemblyFile"/>: its history,
/// the parts and sub-assemblies embedded in it with theirs, its display caches and preview). Assemblies saved before
/// are still read: ZIP containers
/// <code>
///   mimetype                "application/x-sprycad-assembly", the first entry, uncompressed (DocumentContainer.MimeEntry)
///   assembly.json           the assembly (components, references, mates)
///   attachments/parts/…     embedded parts (.sprt bytes)
///   attachments/cache/…     display caches of the parts' revisions (<see cref="MeshCache"/>)
///   thumbnail.png           preview image (optional)
/// </code>
/// whose attachments are listed with size and SHA-256 in assembly.json and checked on load.
/// </summary>
public static class AssemblyContainer
{
    public const string ModelEntry = "assembly.json";

    /// <summary>Saves the assembly (a revision of its history: <see cref="Storage.AssemblyFile.Save"/>).</summary>
    public static void Save(AssemblyDocument asm, string path, string? author = null) => Storage.AssemblyFile.Save(asm, path, author);

    /// <summary>
    /// The assembly as the bytes of its file (an assembly embedded in another): saved as a database (its identity given if
    /// it has none, a new revision when it changed). <paramref name="previous"/>: the bytes it had, so its history goes on
    /// (and it keeps its revision when nothing changed).
    /// </summary>
    public static byte[] ToBytes(AssemblyDocument asm, byte[]? previous = null, bool durable = true)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + AssemblyDocument.FileExtension);
        try
        {
            if (previous != null && Storage.PartDatabase.Is(previous)) File.WriteAllBytes(tmp, previous);
            Storage.AssemblyFile.Save(asm, tmp, durable: durable);
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    public static AssemblyDocument Load(string path) =>
        Storage.PartDatabase.IsFile(path) ? Storage.AssemblyFile.Load(path) : Load(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>An assembly from the bytes of its file (<paramref name="name"/>: what to call it in messages).</summary>
    public static AssemblyDocument Load(byte[] bytes, string name)
    {
        if (Storage.PartDatabase.Is(bytes))
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + AssemblyDocument.FileExtension);
            try { File.WriteAllBytes(tmp, bytes); return Storage.AssemblyFile.Load(tmp); }
            finally { try { File.Delete(tmp); } catch { /* best effort */ } }
        }
        var path = name;
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var model = zip.GetEntry(ModelEntry) ?? throw new InvalidDataException("Not a SpryCAD assembly (no assembly.json in the container).");
        var asm = AssemblyDocument.FromJson(Encoding.UTF8.GetString(Read(model)));
        var folder = DocumentContainer.AttachmentFolder;
        foreach (var e in zip.Entries)
        {
            if (e.FullName == DocumentContainer.ThumbnailEntry) asm.Thumbnail = Read(e);
            else if (e.FullName.StartsWith(folder, StringComparison.Ordinal) && e.FullName.Length > folder.Length)
                asm.Files[e.FullName[folder.Length..]] = Read(e);
        }
        foreach (var a in asm.Attachments ?? new())
        {
            if (!asm.Files.TryGetValue(a.Name, out var data))
                throw new InvalidDataException($"The embedded file \"{a.Name}\" is missing from {Path.GetFileName(path)}.");
            if (data.LongLength != a.Size || DocumentContainer.Hash(data) != a.Sha256)
                throw new InvalidDataException($"The embedded file \"{a.Name}\" in {Path.GetFileName(path)} is damaged (checksum mismatch).");
        }
        if (asm.LegacyIds) asm.RenewLegacyCaches();
        return asm;
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

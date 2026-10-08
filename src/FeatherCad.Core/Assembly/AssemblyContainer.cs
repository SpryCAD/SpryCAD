using System.IO.Compression;
using System.Text;
using FeatherCad.Core.Model;

namespace FeatherCad.Core.Assembly;

/// <summary>
/// Reads and writes .fcasm files: always a ZIP container
/// <code>
///   assembly.json           the assembly (components, references, mates)
///   attachments/parts/…     embedded parts (.fcad bytes)
///   attachments/cache/…     display caches of the parts' revisions (<see cref="MeshCache"/>)
///   thumbnail.png           preview image (optional)
/// </code>
/// Attachments are listed with size and SHA-256 in assembly.json and checked on load, as in part containers.
/// </summary>
public static class AssemblyContainer
{
    public const string ModelEntry = "assembly.json";

    public static void Save(AssemblyDocument asm, string path)
    {
        foreach (var name in asm.Files.Keys) DocumentContainer.CheckName(name);
        asm.Id ??= CadDocument.NewGuid();
        asm.Revision = CadDocument.NewGuid();
        asm.Attachments = asm.Files.Count == 0 ? null
            : asm.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new AttachmentInfo { Name = f.Key, Size = f.Value.LongLength, Sha256 = DocumentContainer.Hash(f.Value) }).ToList();
        var json = asm.ToJson();
        var tmp = path + ".saving";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Write(zip, ModelEntry, Encoding.UTF8.GetBytes(json), CompressionLevel.Optimal);
                foreach (var a in asm.Attachments ?? new())
                    Write(zip, DocumentContainer.AttachmentFolder + a.Name, asm.Files[a.Name], CompressionLevel.Optimal);
                if (asm.Thumbnail != null) Write(zip, DocumentContainer.ThumbnailEntry, asm.Thumbnail, CompressionLevel.NoCompression);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    public static AssemblyDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var model = zip.GetEntry(ModelEntry) ?? throw new InvalidDataException("Not a FeatherCAD assembly (no assembly.json in the container).");
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
        return asm;
    }

    static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data);
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

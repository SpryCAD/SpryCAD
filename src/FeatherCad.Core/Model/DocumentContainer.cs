using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FeatherCad.Core.Model;

/// <summary>
/// Reads and writes .fcad files. Plain files are the document's JSON. Containers are ZIP archives:
/// <code>
///   model.json              the document (the same JSON as a plain file)
///   attachments/&lt;name&gt;      embedded files, listed with size and SHA-256 in model.json
///   thumbnail.png           preview image (optional)
/// </code>
/// The kind is recognized from the first bytes ("PK" for ZIP), not the extension.
/// </summary>
public static class DocumentContainer
{
    public const string ModelEntry = "model.json";
    public const string AttachmentFolder = "attachments/";
    public const string ThumbnailEntry = "thumbnail.png";

    public static bool IsContainer(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && IsZip(head);
    }

    static bool IsZip(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    /// <summary>Attachment names: relative, forward slashes, no empty, "." or ".." segments.</summary>
    public static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')
            || name.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new ArgumentException($"Invalid attachment name \"{name}\" (use a relative path with forward slashes).");
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// The preview image saved in containers (and shown by Explorer as the file's thumbnail): the solid bodies of
    /// a rebuild, in the part's color. Null when there is nothing solid to show.
    /// </summary>
    public static byte[]? RenderThumbnail(CadDocument doc, Regen.RegenResult? result)
    {
        var bodies = result?.State.Bodies;
        if (bodies == null || bodies.Count == 0) return null;
        return Kernel.Thumbnail.Render(bodies.Select(b => (b.Mesh, doc.Color)));
    }

    public static void Save(CadDocument doc, string path, bool container)
    {
        foreach (var name in doc.Files.Keys) CheckName(name);
        // the index in model.json always matches the files written
        doc.Attachments = doc.Files.Count == 0 ? null
            : doc.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new AttachmentInfo { Name = f.Key, Size = f.Value.LongLength, Sha256 = Hash(f.Value) }).ToList();
        var json = doc.ToJson();
        var tmp = path + ".saving";
        try
        {
            if (!container && doc.Files.Count == 0) File.WriteAllText(tmp, json);
            else
            {
                using var fs = File.Create(tmp);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                Write(zip, ModelEntry, Encoding.UTF8.GetBytes(json), CompressionLevel.Optimal);
                foreach (var a in doc.Attachments ?? new())
                    Write(zip, AttachmentFolder + a.Name, doc.Files[a.Name], CompressionLevel.Optimal);
                if (doc.Thumbnail != null) Write(zip, ThumbnailEntry, doc.Thumbnail, CompressionLevel.NoCompression);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data);
    }

    public static CadDocument Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!IsZip(bytes)) return CadDocument.FromJson(Encoding.UTF8.GetString(bytes)); // (BOM is skipped by the decoder)
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var model = zip.GetEntry(ModelEntry) ?? throw new InvalidDataException("Not a FeatherCAD document (no model.json in the container).");
        var doc = CadDocument.FromJson(Encoding.UTF8.GetString(Read(model)));
        foreach (var e in zip.Entries)
        {
            if (e.FullName == ThumbnailEntry) doc.Thumbnail = Read(e);
            else if (e.FullName.StartsWith(AttachmentFolder, StringComparison.Ordinal) && e.FullName.Length > AttachmentFolder.Length)
                doc.Files[e.FullName[AttachmentFolder.Length..]] = Read(e);
        }
        // every listed file must be there, intact
        foreach (var a in doc.Attachments ?? new())
        {
            if (!doc.Files.TryGetValue(a.Name, out var data))
                throw new InvalidDataException($"The embedded file \"{a.Name}\" is missing from {Path.GetFileName(path)}.");
            if (data.LongLength != a.Size || Hash(data) != a.Sha256)
                throw new InvalidDataException($"The embedded file \"{a.Name}\" in {Path.GetFileName(path)} is damaged (checksum mismatch).");
        }
        return doc;
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

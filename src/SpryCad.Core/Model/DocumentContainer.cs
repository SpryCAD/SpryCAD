using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SpryCad.Core.Model;

/// <summary>
/// Reads and writes .sprt files. Plain files are the document's JSON. Containers are ZIP archives:
/// <code>
///   mimetype                "application/x-sprycad-part", the first entry, uncompressed (<see cref="MimeEntry"/>)
///   model.json              the document (the same JSON as a plain file)
///   attachments/&lt;name&gt;      embedded files, listed with size and SHA-256 in model.json
///   thumbnail.png           preview image (optional)
///   display.mesh            the display as last built (optional; a display cache, made for display.revision)
///   display.revision        the revision the display was made for
/// </code>
/// The kind is recognized from the first bytes ("PK" for ZIP), not the extension.
/// </summary>
public static class DocumentContainer
{
    public const string ModelEntry = "model.json";
    public const string AttachmentFolder = "attachments/";
    public const string ThumbnailEntry = "thumbnail.png";
    public const string DisplayEntry = "display.mesh", DisplayRevisionEntry = "display.revision";

    /// <summary>
    /// The first entry of every container (parts and assemblies), stored uncompressed: the file's media type, as
    /// ODF and EPUB do. Its text is then at a fixed place, <see cref="MimeOffset"/> (after the 30 byte local header
    /// and the 8 letter name), so tools that know files by their content (the Linux desktop's shared-mime-info,
    /// installer/linux/sprycad-mime.xml) tell a part from an assembly without unzipping. Readers ignore it.
    /// </summary>
    public const string MimeEntry = "mimetype";
    public const string PartMime = "application/x-sprycad-part", AssemblyMime = "application/x-sprycad-assembly";
    public const int MimeOffset = 38;

    /// <summary>Writes the <see cref="MimeEntry"/>: it must be the archive's first entry.</summary>
    public static void WriteMime(ZipArchive zip, string mime) => Write(zip, MimeEntry, Encoding.ASCII.GetBytes(mime), CompressionLevel.NoCompression);

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
        // the part's identity (kept), and this save's revision
        doc.Id ??= CadDocument.NewGuid();
        doc.Revision = CadDocument.NewGuid();
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
                WriteMime(zip, PartMime);
                Write(zip, ModelEntry, Encoding.UTF8.GetBytes(json), CompressionLevel.Optimal);
                foreach (var a in doc.Attachments ?? new())
                    Write(zip, AttachmentFolder + a.Name, doc.Files[a.Name], CompressionLevel.Optimal);
                if (doc.Thumbnail != null) Write(zip, ThumbnailEntry, doc.Thumbnail, CompressionLevel.NoCompression);
                if (doc.Display != null)
                {
                    Write(zip, DisplayEntry, doc.Display, CompressionLevel.Fastest);
                    Write(zip, DisplayRevisionEntry, Encoding.UTF8.GetBytes(doc.Revision!), CompressionLevel.NoCompression);
                }
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

    public static CadDocument Load(string path) => Load(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>A part from its file's bytes (e.g. a part embedded in an assembly); <paramref name="name"/> for messages.</summary>
    public static CadDocument Load(byte[] bytes, string name)
    {
        if (!IsZip(bytes)) return CadDocument.FromJson(Encoding.UTF8.GetString(bytes)); // (BOM is skipped by the decoder)
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var model = zip.GetEntry(ModelEntry) ?? throw new InvalidDataException("Not a SpryCAD document (no model.json in the container).");
        var doc = CadDocument.FromJson(Encoding.UTF8.GetString(Read(model)));
        foreach (var e in zip.Entries)
        {
            if (e.FullName == ThumbnailEntry) doc.Thumbnail = Read(e);
            else if (e.FullName == DisplayEntry) doc.Display = Read(e);
            else if (e.FullName.StartsWith(AttachmentFolder, StringComparison.Ordinal) && e.FullName.Length > AttachmentFolder.Length)
                doc.Files[e.FullName[AttachmentFolder.Length..]] = Read(e);
        }
        // a display made for another revision (the model edited elsewhere), or by other geometry code, is not this part's
        var displayRev = zip.GetEntry(DisplayRevisionEntry) is { } dr ? Encoding.UTF8.GetString(Read(dr)) : null;
        if (doc.Display != null && (displayRev != doc.Revision || Assembly.MeshCache.VersionOf(doc.Display) != Kernel.GeometryVersion.Current)) doc.Display = null;
        // every listed file must be there, intact
        foreach (var a in doc.Attachments ?? new())
        {
            if (!doc.Files.TryGetValue(a.Name, out var data))
                throw new InvalidDataException($"The embedded file \"{a.Name}\" is missing from {name}.");
            if (data.LongLength != a.Size || Hash(data) != a.Sha256)
                throw new InvalidDataException($"The embedded file \"{a.Name}\" in {name} is damaged (checksum mismatch).");
        }
        return doc;
    }

    /// <summary>A file's identity (Id, Revision) without building the document; nulls when it has none (a part saved
    /// before identities, or not a part).</summary>
    public static (string? id, string? revision) ReadIdentity(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            byte[] json;
            if (IsZip(bytes))
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                // (an assembly's identity is in its assembly.json)
                var model = zip.GetEntry(ModelEntry) ?? zip.GetEntry(Assembly.AssemblyContainer.ModelEntry);
                if (model == null) return (null, null);
                json = Read(model);
            }
            else json = bytes;
            using var d = System.Text.Json.JsonDocument.Parse(json);
            string? Str(string name) => d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            return (Str("id"), Str("revision"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

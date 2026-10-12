using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SpryCad.Core.Model;

/// <summary>
/// Reads and writes .sprt files. A part is saved as a database (<see cref="Storage.PartFile"/>: its history,
/// embedded files, preview and display). Parts saved before are still read: plain files are the document's JSON,
/// containers ZIP archives:
/// <code>
///   mimetype                "application/x-sprycad-part", the first entry, uncompressed (<see cref="MimeEntry"/>)
///   model.json              the document (the same JSON as a plain file)
///   attachments/&lt;name&gt;      embedded files, listed with size and SHA-256 in model.json
///   thumbnail.png           preview image (optional)
///   display.mesh            the display as last built (optional; a display cache, made for display.revision)
///   display.revision        the revision the display was made for
/// </code>
/// The kind is recognized from the first bytes (SQLite's header, "PK" for ZIP), not the extension. The ZIP layout's
/// names stay in use for assemblies (<see cref="Assembly.AssemblyContainer"/>).
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

    static bool IsZip(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    /// <summary>Attachment names: relative, forward slashes, no empty, "." or ".." segments.</summary>
    public static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')
            || name.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
            throw new ArgumentException($"Invalid attachment name \"{name}\" (use a relative path with forward slashes).");
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // each attachment's hash, kept with its byte array (attachments are replaced, never changed in place): a save
    // hashes only the files that are new since the last one
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> _hashes = new();
    static string HashOf(byte[] data) => _hashes.GetValue(data, Hash);

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

    /// <summary>Saves the part (a revision of its history: <see cref="Storage.PartFile.Save"/>).</summary>
    public static void Save(CadDocument doc, string path, string? author = null)
    {
        foreach (var name in doc.Files.Keys) CheckName(name);
        Storage.PartFile.Save(doc, path, author);
    }

    static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data);
    }

    public static CadDocument Load(string path) =>
        Storage.PartDatabase.IsFile(path) ? Storage.PartFile.Load(path) : Load(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>A part from its file's bytes (e.g. a part embedded in an assembly); <paramref name="name"/> for messages.</summary>
    public static CadDocument Load(byte[] bytes, string name)
    {
        if (Storage.PartDatabase.Is(bytes)) return OnDisk(bytes, Storage.PartFile.Load);
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
            if (Storage.PartDatabase.IsFile(path)) return Storage.PartFile.ReadIdentity(path);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (null, null);
        }
    }

    /// <summary>A database's bytes read as a file (SQLite opens files): written to a temporary one, removed after.</summary>
    static T OnDisk<T>(byte[] bytes, Func<string, T> read)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sprycad-" + Guid.NewGuid().ToString("N") + ".sprt");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            return read(tmp);
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    static byte[] Read(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

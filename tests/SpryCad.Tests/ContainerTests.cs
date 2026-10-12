using System.IO.Compression;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>.sprt files: databases with embedded files and a thumbnail; old plain JSON and ZIP container parts still open.</summary>
static class ContainerTests
{
    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "fc-container-tests", name);

    public static void Run(TestRunner t)
    {
        Directory.CreateDirectory(Temp(""));

        t.Test("Container: a part saves as a SpryCAD database and reads back the same", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var path = Temp("plain.sprt");
            doc.Save(path);
            t.True(PartDatabase.IsFile(path), "not saved as a database");
            t.Eq(CadDocument.Load(path).ToJson(), doc.ToJson());
            t.Eq(DocumentContainer.ReadIdentity(path), (doc.Id, doc.Revision));
        });

        t.Test("Container: embedded files and thumbnail round-trip, the revision lists them", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var step = System.Text.Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            doc.AddFile("imports/bracket.step", step);
            doc.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var path = Temp("container.sprt");
            doc.Save(path);
            t.True(PartDatabase.IsFile(path), "not saved as a database");
            using (var db = PartDatabase.Open(path))
            {
                var objects = db.GetObjects(Guid.Parse(doc.Revision!));
                t.True(objects.TryGetValue(PartObjects.FileId("imports/bracket.step"), out var h) && h == Hash.Of(step), "embedded file not in the revision");
                t.True(db.Blobs.Get(h).SequenceEqual(step), "embedded file content");
                t.True(db.GetThumbnail()!.SequenceEqual(doc.Thumbnail), "thumbnail missing");
            }
            var back = CadDocument.Load(path);
            t.True(back.Files["imports/bracket.step"].SequenceEqual(step), "attachment content changed");
            t.True(back.Thumbnail!.SequenceEqual(doc.Thumbnail), "thumbnail changed");
            // the history itself is the same and rebuilds the same
            var a = new Regenerator().Rebuild(doc); var b = new Regenerator().Rebuild(back);
            t.Near(KernelTests.Vex(b.State.Bodies[0].Solid), KernelTests.Vex(a.State.Bodies[0].Solid), 1e-6);
            // a clone (undo snapshots, rebuild requests) keeps the embedded files
            t.True(back.Clone().Files.ContainsKey("imports/bracket.step"), "clone lost the attachment");
        });

        t.Test("Container: parts and assemblies start with SQLite's header and SpryCAD's application id; the manifest names which", () =>
        {
            // what installer/linux/sprycad-mime.xml looks for: the database header and "SPRY" at byte 68 (the same for
            // both: the extension tells them apart, the manifest's RootKind for SpryCAD)
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            doc.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var part = Temp("mime.sprt");
            doc.Save(part);
            var asmPath = Temp("mime.sasm");
            SpryCad.Core.Assembly.AssemblyContainer.Save(new SpryCad.Core.Assembly.AssemblyDocument(), asmPath);
            foreach (var (path, kind) in new[] { (part, AssemblyFile.RootKindPart), (asmPath, AssemblyFile.RootKindAssembly) })
            {
                var b = File.ReadAllBytes(path);
                t.Eq(System.Text.Encoding.ASCII.GetString(b, 0, 16), "SQLite format 3\0");
                t.Eq(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(68)), PartDatabase.ApplicationId);
                t.Eq(System.Text.Encoding.ASCII.GetString(b, 68, 4), "SPRY");
                using var db = PartDatabase.Open(path);
                t.Eq(db.Manifest("RootKind"), kind);
            }
            t.True(!AssemblyFile.IsAssembly(part) && AssemblyFile.IsAssembly(asmPath), "IsAssembly");
            t.Eq(CadDocument.Load(part).ToJson(), doc.ToJson()); // and both read back
            SpryCad.Core.Assembly.AssemblyContainer.Load(asmPath);
        });

        t.Test("Container: a damaged or missing embedded file is reported, bad names are refused", () =>
        {
            var doc = new CadDocument();
            var data = new byte[] { 1, 2, 3, 4 };
            doc.AddFile("imports/a.step", data);
            var path = Temp("damaged.sprt");
            doc.Save(path);
            var key = Hash.Of(data).ToBytes();
            // change the stored content
            Sql(path, "UPDATE Blobs SET Data = X'01020305' WHERE Hash = $h", key);
            var ex = Catch(() => CadDocument.Load(path));
            t.True(ex is InvalidDataException && ex.Message.Contains("damaged"), "damage not detected: " + (ex?.Message ?? "it loaded"));
            // remove it
            Sql(path, "PRAGMA foreign_keys = OFF; DELETE FROM Blobs WHERE Hash = $h", key); // (damage the database would refuse)
            ex = Catch(() => CadDocument.Load(path));
            t.True(ex is InvalidDataException && ex.Message.Contains("missing"), "missing file not detected: " + (ex?.Message ?? "it loaded"));
            foreach (var bad in new[] { "", "/abs.step", "../up.step", "a\\b.step", "c:/x.step", "a//b" })
                t.True(Catch(() => doc.AddFile(bad, new byte[1])) is ArgumentException, $"accepted name \"{bad}\"");
        });

        t.Test("Container: a failed save keeps the old file", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var path = Temp("forced.sprt");
            doc.Save(path);
            t.Eq(CadDocument.Load(path).ToJson(), doc.ToJson());
            var before = File.ReadAllBytes(path);
            // an attachment that cannot be written (name checked at save) must not truncate the existing file
            doc.Files["../evil"] = new byte[1];
            t.True(Catch(() => doc.Save(path)) != null, "a bad name saved");
            t.True(File.ReadAllBytes(path).SequenceEqual(before), "old file damaged by a failed save");
            t.True(!File.Exists(path + ".saving"), "temporary file left behind");
        });

        t.Test("Container: old plain JSON and ZIP container parts still open (embedded files, thumbnail, display), and save as databases", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            // plain JSON, as parts without embedded files were saved
            var plain = Temp("legacy-plain.sprt");
            doc.Id = CadDocument.NewGuid(); doc.Revision = CadDocument.NewGuid();
            File.WriteAllText(plain, doc.ToJson());
            t.True(!PartDatabase.IsFile(plain), "not a legacy file");
            t.Eq(CadDocument.Load(plain).ToJson(), doc.ToJson());
            t.Eq(DocumentContainer.ReadIdentity(plain), (doc.Id, doc.Revision));

            // a ZIP container with an embedded file, a thumbnail and a display for its revision
            var step = System.Text.Encoding.ASCII.GetBytes("ISO-10303-21;\nEND-ISO-10303-21;\n");
            doc.AddFile("imports/bracket.step", step);
            doc.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var r = new Regenerator().Rebuild(doc);
            doc.Display = SpryCad.Core.Assembly.MeshCache.Of(r, doc.Color);
            var zipped = Temp("legacy-zip.sprt");
            WriteLegacyZip(zipped, doc);
            t.True(File.ReadAllBytes(zipped).AsSpan(0, 2).SequenceEqual("PK"u8), "not a ZIP");
            var z = CadDocument.Load(zipped);
            t.True(z.Files["imports/bracket.step"].SequenceEqual(step), "embedded file");
            t.True(z.Thumbnail!.SequenceEqual(doc.Thumbnail), "thumbnail");
            t.True(z.Display != null, "display for its revision");
            t.Eq(z.Id, doc.Id);
            t.Eq(z.Features.Count, doc.Features.Count);
            t.Eq(DocumentContainer.Load(File.ReadAllBytes(zipped), "x").Files.Count, 1);
            t.Eq(DocumentContainer.ReadIdentity(zipped), (doc.Id, doc.Revision));

            // saved again: a database, everything kept
            z.Save(zipped);
            t.True(PartDatabase.IsFile(zipped), "saved as a database");
            var again = CadDocument.Load(zipped);
            t.True(again.Files["imports/bracket.step"].SequenceEqual(step) && again.Thumbnail!.SequenceEqual(doc.Thumbnail) && again.Display != null, "kept through conversion");
            t.Eq(again.Id, doc.Id);
            t.True(PartObjects.From(again).OrderBy(kv => kv.Key).Select(kv => Hash.Of(kv.Value))
                .SequenceEqual(PartObjects.From(z).OrderBy(kv => kv.Key).Select(kv => Hash.Of(kv.Value))), "the same part");

            // an old container's damaged or missing attachment is still reported
            var damaged = Temp("legacy-damaged.sprt");
            WriteLegacyZip(damaged, doc, (name, data) => name == "imports/bracket.step" ? data.Select(x => (byte)(x ^ 1)).ToArray() : data);
            var ex = Catch(() => CadDocument.Load(damaged));
            t.True(ex is InvalidDataException && ex.Message.Contains("damaged"), "damage not detected: " + ex?.Message);
            WriteLegacyZip(damaged, doc, (name, data) => name == "imports/bracket.step" ? null : data);
            ex = Catch(() => CadDocument.Load(damaged));
            t.True(ex is InvalidDataException && ex.Message.Contains("missing"), "missing file not detected: " + ex?.Message);
        });

        t.Test("Thumbnail: PNG preview of a rebuilt part", () =>
        {
            var doc = Demos.All.First(d => d.file == "pipe-flange.sprt").build();
            var r = new Regenerator().Rebuild(doc);
            var png = Thumbnail.Render(r.State.Bodies.Select(b => (b.Mesh, doc.Color)));
            t.True(png.Take(8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "not a PNG");
            // decode the IDAT stream and check the part covers a reasonable part of the image
            var (w, h, rgba) = DecodePng(png);
            t.Eq(w, 256); t.Eq(h, 256);
            int covered = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] > 128) covered++;
            t.True(covered > w * h / 10 && covered < w * h * 9 / 10, $"part covers {covered} of {w * h} pixels");
            File.WriteAllBytes(Temp("flange.png"), png);
        });
    }

    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception ex) { return ex; } }

    /// <summary>Runs SQL on a file directly (no SpryCAD code), with an optional blob parameter $h.</summary>
    static void Sql(string path, string sql, byte[]? h = null)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (h != null) cmd.Parameters.AddWithValue("$h", h);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A part as the ZIP container parts were saved as before databases (mimetype, model.json listing the embedded
    /// files, attachments/, thumbnail.png, display.mesh and display.revision); <paramref name="tamper"/> may change or
    /// drop (null) an embedded file's stored bytes.
    /// </summary>
    internal static void WriteLegacyZip(string path, CadDocument doc, Func<string, byte[], byte[]?>? tamper = null)
    {
        doc.Id ??= CadDocument.NewGuid();
        doc.Revision ??= CadDocument.NewGuid();
        doc.Attachments = doc.Files.Count == 0 ? null
            : doc.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new AttachmentInfo { Name = f.Key, Size = f.Value.LongLength, Sha256 = DocumentContainer.Hash(f.Value) }).ToList();
        static void Put(ZipArchive zip, string name, byte[] data, CompressionLevel level)
        {
            using var s = zip.CreateEntry(name, level).Open();
            s.Write(data);
        }
        try
        {
            if (File.Exists(path)) File.Delete(path);
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            DocumentContainer.WriteMime(zip, DocumentContainer.PartMime);
            Put(zip, DocumentContainer.ModelEntry, System.Text.Encoding.UTF8.GetBytes(doc.ToJson()), CompressionLevel.Optimal);
            foreach (var (name, data) in doc.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                if ((tamper == null ? data : tamper(name, data)) is { } stored) Put(zip, DocumentContainer.AttachmentFolder + name, stored, CompressionLevel.Optimal);
            if (doc.Thumbnail != null) Put(zip, DocumentContainer.ThumbnailEntry, doc.Thumbnail, CompressionLevel.NoCompression);
            if (doc.Display != null)
            {
                Put(zip, DocumentContainer.DisplayEntry, doc.Display, CompressionLevel.Fastest);
                Put(zip, DocumentContainer.DisplayRevisionEntry, System.Text.Encoding.UTF8.GetBytes(doc.Revision), CompressionLevel.NoCompression);
            }
        }
        finally { doc.Attachments = null; }
    }

    /// <summary>Decoder for the PNGs <see cref="Png"/> writes (RGBA, filter 0).</summary>
    static (int w, int h, byte[] rgba) DecodePng(byte[] png)
    {
        int pos = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR") { w = (png[pos + 8] << 24) | (png[pos + 9] << 16) | (png[pos + 10] << 8) | png[pos + 11]; h = (png[pos + 12] << 24) | (png[pos + 13] << 16) | (png[pos + 14] << 8) | png[pos + 15]; }
            if (type == "IDAT") idat.Write(png, pos + 8, len);
            pos += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var data = raw.ToArray();
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++) Array.Copy(data, y * (w * 4 + 1) + 1, rgba, y * w * 4, w * 4);
        return (w, h, rgba);
    }
}

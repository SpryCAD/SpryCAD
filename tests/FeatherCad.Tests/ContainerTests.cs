using System.IO.Compression;
using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary>.sprt files: plain JSON and ZIP containers with embedded files and a thumbnail.</summary>
static class ContainerTests
{
    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "fc-container-tests", name);

    public static void Run(TestRunner t)
    {
        Directory.CreateDirectory(Temp(""));

        t.Test("Container: a part without embedded files still saves as plain JSON (unchanged format)", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var path = Temp("plain.sprt");
            doc.Save(path);
            t.True(!CadDocument.IsContainer(path), "saved as a container");
            t.Eq(File.ReadAllText(path), doc.ToJson());
            t.True(!File.ReadAllText(path).Contains("\"attachments\""), "attachment index written for a part without attachments");
            t.Eq(CadDocument.Load(path).ToJson(), doc.ToJson());
        });

        t.Test("Container: embedded files and thumbnail round-trip, model.json lists them", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var step = System.Text.Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            doc.AddFile("imports/bracket.step", step);
            doc.Thumbnail = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var path = Temp("container.sprt");
            doc.Save(path);
            t.True(CadDocument.IsContainer(path), "not saved as a container");
            using (var zip = ZipFile.OpenRead(path))
            {
                t.True(zip.GetEntry("model.json") != null, "model.json missing");
                t.True(zip.GetEntry("attachments/imports/bracket.step") != null, "attachment missing");
                t.True(zip.GetEntry("thumbnail.png") != null, "thumbnail missing");
            }
            var back = CadDocument.Load(path);
            t.True(back.Files["imports/bracket.step"].SequenceEqual(step), "attachment content changed");
            t.True(back.Thumbnail!.SequenceEqual(doc.Thumbnail), "thumbnail changed");
            t.Eq(back.Attachments!.Single().Sha256, DocumentContainer.Hash(step));
            // the history itself is the same and rebuilds the same
            var a = new Regenerator().Rebuild(doc); var b = new Regenerator().Rebuild(back);
            t.Near(KernelTests.Vex(b.State.Bodies[0].Solid), KernelTests.Vex(a.State.Bodies[0].Solid), 1e-6);
            // a clone (undo snapshots, rebuild requests) keeps the embedded files
            t.True(back.Clone().Files.ContainsKey("imports/bracket.step"), "clone lost the attachment");
        });

        t.Test("Container: a damaged or missing embedded file is reported, bad names are refused", () =>
        {
            var doc = new CadDocument();
            doc.AddFile("imports/a.step", new byte[] { 1, 2, 3, 4 });
            var path = Temp("damaged.sprt");
            doc.Save(path);
            // flip the stored attachment's content
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("attachments/imports/a.step")!.Delete();
                using var s = zip.CreateEntry("attachments/imports/a.step").Open();
                s.Write(new byte[] { 1, 2, 3, 5 });
            }
            var ex = Catch(() => CadDocument.Load(path));
            t.True(ex is InvalidDataException && ex.Message.Contains("damaged"), "damage not detected: " + ex?.Message);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.GetEntry("attachments/imports/a.step")!.Delete();
            ex = Catch(() => CadDocument.Load(path));
            t.True(ex is InvalidDataException && ex.Message.Contains("missing"), "missing file not detected: " + ex?.Message);
            foreach (var bad in new[] { "", "/abs.step", "../up.step", "a\\b.step", "c:/x.step", "a//b" })
                t.True(Catch(() => doc.AddFile(bad, new byte[1])) is ArgumentException, $"accepted name \"{bad}\"");
        });

        t.Test("Container: always-container save of a plain part, and a failed save keeps the old file", () =>
        {
            var doc = Demos.All.First(d => d.file == "l-bracket.sprt").build();
            var path = Temp("forced.sprt");
            doc.Save(path, container: true);
            t.True(CadDocument.IsContainer(path), "not a container");
            t.Eq(CadDocument.Load(path).ToJson(), doc.ToJson());
            var before = File.ReadAllBytes(path);
            // an attachment that cannot be written (name checked at save) must not truncate the existing file
            doc.Files["../evil"] = new byte[1];
            Catch(() => doc.Save(path));
            t.True(File.ReadAllBytes(path).SequenceEqual(before), "old file damaged by a failed save");
            t.True(!File.Exists(path + ".saving"), "temporary file left behind");
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

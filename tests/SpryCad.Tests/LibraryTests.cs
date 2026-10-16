using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Standards libraries (Core/Library, file format spec section 34): library files, the central set, snapshots in parts.</summary>
static class LibraryTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-library-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string J<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v, Serialization.Compact);

    static LibraryTable Clearance(params (string row, double close, double normal, double loose)[] rows) => new()
    {
        Key = "ISO273", Title = "Metric clearance holes", Kind = TableKinds.Clearance, Standard = "ISO 273",
        Columns = { "close", "normal", "loose" }, Rows = rows.Select(r => new LibraryRow(r.row, r.close, r.normal, r.loose)).ToList(),
    };

    /// <summary>A share folder holding a company library (and the library set listing it), with the cache in its own folder.</summary>
    static (string config, string lib) Share(string dir, IEnumerable<LibraryTable> tables, IEnumerable<LibraryRestriction>? restrictions = null)
    {
        var lib = Path.Combine(dir, "company.sprlib");
        LibraryFile.Publish(lib, Guid.NewGuid(), "Acme", LibraryLayer.Company, tables, restrictions, "admin");
        var config = Path.Combine(dir, LibraryConfig.FileName);
        File.WriteAllText(config, """{ "libraries": [ "company.sprlib" ] }""");
        return (config, lib);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Library: built-in tables well formed (a value per column, unique keys); written as a library file and read back unchanged", () =>
        {
            var keys = BuiltInStandards.Tables.Select(x => x.Key).ToList();
            t.True(keys.Distinct().Count() == keys.Count, "unique table keys");
            foreach (var tb in BuiltInStandards.Tables)
            {
                t.True(tb.Rows.All(r => r.Values.Count == tb.Columns.Count && r.Values.All(v => v > 0)), $"{tb.Key}: a positive value per column");
                t.True(tb.Rows.Select(r => r.Key).Distinct().Count() == tb.Rows.Count, $"{tb.Key}: unique rows");
                t.True(!tb.Verified, $"{tb.Key}: not marked verified (values from secondary sources)");
            }
            var unc = BuiltInStandards.Tables.Single(x => x.Key == "ASME-B1.1-UNC").Row("1/4-20 UNC")!;
            t.Near(unc.Values[0], 6.35, 1e-12); t.Near(unc.Values[1], 1.27, 1e-12);
            var path = Path.Combine(NewDir("builtin"), "standards.sprlib");
            var rev = LibraryFile.WriteBuiltIn(path, "test");
            var lib = LibraryFile.Read(path);
            t.Eq(lib.LibraryId, BuiltInStandards.LibraryId);
            t.Eq(lib.RevisionId, rev);
            t.Eq(lib.Layer, LibraryLayer.Standards);
            t.Eq(J(lib.Tables), J(BuiltInStandards.Tables.OrderBy(x => x.Key, StringComparer.Ordinal).ToList()));
            t.True(LibraryFile.IsLibrary(path) && PartDatabase.IsFile(path), "a SpryCAD database, its root a library");
        });

        t.Test("Library: publishing the same tables again makes no revision; a change does; a stale read is refused", () =>
        {
            var path = Path.Combine(NewDir("publish"), "company.sprlib");
            var id = Guid.NewGuid();
            var r1 = LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 8.4, 9, 10)) }, null, "admin");
            var r2 = LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 8.4, 9, 10)) }, null, "admin", readRevision: r1);
            t.Eq(r2, r1);
            var r3 = LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 8.4, 9.2, 10)) }, null, "admin", readRevision: r1);
            t.True(r3 != r1, "a new revision");
            t.Eq(PartFile.History(path).Count, 2);
            // someone read r1, someone else published r3 since: refused, the file untouched
            bool refused = false;
            try { LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 1, 2, 3)) }, null, "other", readRevision: r1); }
            catch (LibraryConflictException) { refused = true; }
            t.True(refused, "refused");
            t.Eq(LibraryFile.Read(path).RevisionId, r3);
        });

        t.Test("Library set: a company library overrides and adds rows; its restrictions hide sizes from pickers, not from parts", () =>
        {
            var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryLayer.Company, Guid.NewGuid(), null,
                new[] { Clearance(("M8", 8.4, 9.2, 10), ("M7", 7.4, 7.6, 8)) },
                new[] { new LibraryRestriction { Table = "ISO273", Allow = new() { "M3", "M4", "M5", "M6", "M7", "M8", "M10", "M12" } }, new LibraryRestriction { Table = "ASME-B1.1-UNF", Hidden = true } });
            var set = new LibrarySet(new[] { company });
            var iso = set.Table("ISO273")!;
            t.Near(iso.Row("M8")!.Values[1], 9.2, 1e-12);
            t.True(iso.Row("M7") != null && iso.Row("M48") != null, "added to the built-in rows, which stay");
            var offered = set.Offered(TableKinds.Clearance).Single(x => x.Key == "ISO273");
            t.Eq(string.Join(",", offered.Rows.Select(r => r.Key)), "M3,M4,M5,M6,M8,M10,M12,M7");
            t.True(!set.Offered(TableKinds.Thread).Any(x => x.Key == "ASME-B1.1-UNF"), "a hidden table is not offered");
            t.True(set.Snapshot("ISO273/M48") != null && set.Snapshot("ASME-B1.1-UNF/1/4-28 UNF") != null, "parts still resolve restricted items");
            var s = set.Snapshot("ISO273/M8")!;
            t.Eq(s.LibraryName, "Acme");
            t.Eq(set.Snapshot("ISO273/M6")!.LibraryId, BuiltInStandards.LibraryId);
            // different columns: the later table replaces the earlier
            var other = new LoadedLibrary(Guid.NewGuid(), "Other", LibraryLayer.Company, Guid.NewGuid(), null,
                new[] { new LibraryTable { Key = "ISO273", Kind = TableKinds.Clearance, Columns = { "normal" }, Rows = { new LibraryRow("M8", 9.5) } } }, Array.Empty<LibraryRestriction>());
            t.Eq(new LibrarySet(new[] { other }).Table("ISO273")!.Rows.Count, 1);
        });

        t.Test("Library sync: copied from the share once, re-read only when it changes; offline, the copies last read", () =>
        {
            var share = NewDir("share");
            var cacheBefore = LibrarySync.CacheFolder;
            LibrarySync.CacheFolder = NewDir("cache");
            try
            {
                var (config, lib) = Share(share, new[] { Clearance(("M8", 8.4, 9.2, 10)) });
                var r1 = LibrarySync.Load(config);
                t.Eq(r1.Problems.Count, 0);
                t.Near(r1.Set.Table("ISO273")!.Row("M8")!.Values[1], 9.2, 1e-12);
                var copies = Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib");
                t.Eq(copies.Length, 1);
                var copyTime = File.GetLastWriteTimeUtc(copies[0]);
                // unchanged: read from the copy (not copied again)
                var r2 = LibrarySync.Load(config);
                t.Eq(r2.Set.Libraries.Last().RevisionId, r1.Set.Libraries.Last().RevisionId);
                t.Eq(File.GetLastWriteTimeUtc(Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib").Single()), copyTime);
                // published again: the new revision copied, the old copy removed
                var read = LibraryFile.Read(lib);
                LibraryFile.Publish(lib, read.LibraryId, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 8.4, 9.4, 10)) }, null, "admin", readRevision: read.RevisionId);
                var r3 = LibrarySync.Load(config);
                t.Near(r3.Set.Table("ISO273")!.Row("M8")!.Values[1], 9.4, 1e-12);
                t.Eq(Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib").Length, 1);
                // the share gone (offline): the copies last read, with a note
                Directory.Delete(share, true);
                var r4 = LibrarySync.Load(config);
                t.Near(r4.Set.Table("ISO273")!.Row("M8")!.Values[1], 9.4, 1e-12);
                t.True(r4.Problems.Count > 0, "says it is using the copies");
                // no set configured: the built-in standards and part families
                t.Eq(LibrarySync.Load(null).Set.Libraries.Count, 2);
            }
            finally { LibrarySync.CacheFolder = cacheBefore; }
        });

        t.Test("Library sync: an admin's setting (environment variable) wins over the user's", () =>
        {
            var before = Environment.GetEnvironmentVariable(LibrarySync.EnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(LibrarySync.EnvironmentVariable, @"\\server\cad\libraries");
                var (p, src) = LibrarySync.Locate(@"C:\mine");
                t.Eq(src, LibraryConfigSource.Environment);
                t.Eq(p, Path.Combine(@"\\server\cad\libraries", LibraryConfig.FileName));
                Environment.SetEnvironmentVariable(LibrarySync.EnvironmentVariable, null);
                if (!File.Exists(LibrarySync.MachinePolicyFile))
                {
                    var (u, us) = LibrarySync.Locate(@"C:\mine\libraries.json");
                    t.Eq(us, LibraryConfigSource.User);
                    t.Eq(u, @"C:\mine\libraries.json");
                }
            }
            finally { Environment.SetEnvironmentVariable(LibrarySync.EnvironmentVariable, before); }
        });

        t.Test("Library in parts: snapshots of the items used, saved in the file; a part rebuilds from them with no library", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                // a 1/4" shaft, 20 long, threaded 1/4-20 UNC (cosmetic: the helix shows the pitch)
                var doc = new CadDocument();
                var sk = new SpryCad.Core.Sketching.Sketch();
                sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = sk.Point(SpryCad.Core.Sketching.Sketch.OriginId)!.Id, R = 3.175 });
                var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                var ex = doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
                var r0 = new Regenerator().Rebuild(doc);
                var cyl = r0.State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
                var th = doc.AddFeature(new ThreadFeature
                {
                    Face = new FaceRef { Name = cyl.Tag.Name, Hint = new Vec3(3.175, 0, 19) }, Size = "1/4-20 UNC", Standard = "ASME-B1.1-UNC",
                    End = ThreadEnd.ThroughAll, Cosmetic = true, Chamfer = false,
                });
                var hole = doc.AddFeature(new HoleFeature { SketchId = s.Id, Standard = "ISO273", Size = "M8", Type = HoleType.Counterbore });
                LibraryLink.Current = LibrarySet.BuiltIn;
                t.True(LibraryLink.Sync(doc), "snapshots added");
                t.Eq(string.Join(",", doc.Library!.Keys), "ASME-B1.1-UNC/1/4-20 UNC,ISO273/M8,ISO4762-cbore/M8");
                t.True(!LibraryLink.Sync(doc), "nothing more to do");
                // saved and read back: the snapshots are objects of the part, unchanged
                var path = Path.Combine(NewDir("part"), "shaft.sprt");
                PartFile.Save(doc, path);
                var loaded = PartFile.Load(path);
                t.Eq(J(loaded.Library), J(doc.Library));
                PartFile.Save(loaded, path);
                t.Eq(PartFile.History(path).Count, 1);
                // with libraries whose UNC row is different (and wrong): the part still uses its own
                var wrong = new LoadedLibrary(Guid.NewGuid(), "Wrong", LibraryLayer.Company, Guid.NewGuid(), null,
                    new[] { new LibraryTable { Key = "ASME-B1.1-UNC", Kind = TableKinds.Thread, Columns = { "major", "pitch" }, Rows = { new LibraryRow("1/4-20 UNC", 6.35, 2.0) } } }, Array.Empty<LibraryRestriction>());
                LibraryLink.Current = new LibrarySet(new[] { wrong });
                var r = new Regenerator().Rebuild(loaded);
                t.True(r.Status[th.Id].Ok, r.Status[th.Id].Error ?? "");
                t.Near(r.State.Cosmetics[th.Id].Lead, 1.27, 1e-12);
                // removing the hole drops its snapshots
                loaded.Features.RemoveAll(f => f.Id == hole.Id);
                LibraryLink.Sync(loaded);
                t.Eq(string.Join(",", loaded.Library!.Keys), "ASME-B1.1-UNC/1/4-20 UNC");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Library updates: a changed row is offered with its values; accepting sizes the hole again, in the part's history", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var doc = new CadDocument();
                var hole = doc.AddFeature(new HoleFeature { Standard = "ISO273", Size = "M8" });
                HoleSizes.Apply(hole, i => LibraryLink.Resolve(doc, i));
                t.Eq(hole.Diameter, "9mm");
                LibraryLink.Sync(doc);
                t.Eq(LibraryLink.Updates(doc).Count, 0);
                var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryLayer.Company, Guid.NewGuid(), null, new[] { Clearance(("M8", 8.4, 9.2, 10)) }, Array.Empty<LibraryRestriction>());
                LibraryLink.Current = new LibrarySet(new[] { company });
                var ups = LibraryLink.Updates(doc);
                t.Eq(ups.Count, 1);
                t.Eq(ups[0].Describe(), "M8 (ISO273): normal 9 → 9.2");
                var path = Path.Combine(NewDir("update"), "plate.sprt");
                PartFile.Save(doc, path);
                LibraryLink.Accept(doc, ups);
                t.Eq(hole.Diameter, "9.2mm");
                t.Eq(doc.Library!["ISO273/M8"].LibraryName, "Acme");
                PartFile.Save(doc, path);
                using var db = PartDatabase.Open(path);
                var h = db.GetHistory(Guid.Parse(doc.Id!));
                var changes = PartDescribe.Changes(db, h[1].RevisionId, h[0].RevisionId).Select(c => c.Text).ToList();
                t.True(changes.Any(c => c.StartsWith("Library data M8 (ISO273)")), string.Join(" | ", changes));
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Library CSV: a table's rows and text written and read back unchanged; quoting, tabs and bad cells", () =>
        {
            var tb = Clearance(("M8", 8.4, 9, 10), ("M10", 10.5, 11, 12), ("M1.6", 1.7, 1.8, 2.0000000000000004));
            tb.Rows[0].Text = new(StringComparer.Ordinal) { ["part number"] = "ACME-0008, \"special\"", ["supplier"] = "Bolt Co" };
            tb.Rows[1].Text = new(StringComparer.Ordinal) { ["supplier"] = "line one\nline two" };
            var csv = LibraryCsv.Write(tb);
            t.True(csv.StartsWith("row,close,normal,loose,text:part number,text:supplier\r\n"), csv);
            var (cols, rows) = LibraryCsv.Read(csv);
            t.Eq(J(cols), J(tb.Columns));
            t.Eq(J(rows), J(tb.Rows));
            t.True(rows[2].Text == null, "no text: none");
            // as pasted from a spreadsheet: tabs, a blank line, a BOM
            var (c2, r2) = LibraryCsv.Read("﻿size\tdiameter\tdepth\n\nM5\t9.5\t5.4\n");
            t.Eq(string.Join(",", c2), "diameter,depth");
            t.Eq(r2.Single().Key, "M5"); t.Near(r2[0].Values[1], 5.4, 0);
            foreach (var (bad, says) in new[] { ("row,a\nM8,x\n", "Line 2: M8's a is not a number"), ("row,a\nM8,1\nM8,2\n", "Line 3: M8 is listed twice"), ("row,a\n,1\n", "Line 2"), ("row,a\nM8,\"1\n", "not closed") })
            {
                string? message = null;
                try { LibraryCsv.Read(bad); } catch (FormatException ex) { message = ex.Message; }
                t.True(message?.Contains(says) == true, $"{bad.Replace("\n", "|")}: {message}");
            }
            // published as read: the same revision content
            var path = Path.Combine(NewDir("csv"), "company.sprlib");
            var id = Guid.NewGuid();
            var r1 = LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { tb }, null, "admin");
            var back = new LibraryTable { Key = tb.Key, Title = tb.Title, Kind = tb.Kind, Standard = tb.Standard, Columns = cols, Rows = rows };
            t.Eq(LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { back }, null, "admin", readRevision: r1), r1);
        });

        t.Test("Library editing: two admins read the same revision; the second to publish is refused, reloads and publishes on top", () =>
        {
            var dir = NewDir("edit");
            var path = Path.Combine(dir, "company.sprlib");
            var id = Guid.NewGuid();
            var deny = new[] { new LibraryRestriction { Table = "ISO273", Deny = new() { "M1.6" } } };
            LibraryFile.Publish(path, id, "Acme", LibraryLayer.Company, new[] { Clearance(("M8", 8.4, 9, 10)) }, deny, "admin", "first");
            var a = LibraryFile.Read(path);
            var b = LibraryFile.Read(path);
            var ra = LibraryFile.Publish(path, id, a.Name, a.Layer, new[] { Clearance(("M8", 8.4, 9, 10), ("M10", 10.5, 11, 12)) }, a.Restrictions, "alice", "M10 added", a.RevisionId);
            bool refused = false;
            try { LibraryFile.Publish(path, id, b.Name, b.Layer, b.Tables, new[] { new LibraryRestriction { Table = "ISO273", Allow = new() { "M8" } } }, "bob", "M8 only", b.RevisionId); }
            catch (LibraryConflictException) { refused = true; }
            t.True(refused, "bob's publish refused");
            t.True(!File.Exists(path + ".saving"), "nothing left beside the file");
            var reread = LibraryFile.Read(path);
            t.Eq(reread.RevisionId, ra);
            t.Eq(reread.Tables.Single().Rows.Count, 2);
            var rb = LibraryFile.Publish(path, id, reread.Name, reread.Layer, reread.Tables, new[] { new LibraryRestriction { Table = "ISO273", Allow = new() { "M8" } } }, "bob", "M8 only", reread.RevisionId);
            var history = PartFile.History(path);
            t.Eq(string.Join(" | ", history.Select(h => $"{h.Author}: {h.Message}")), "bob: M8 only | alice: M10 added | admin: first");
            t.Eq(history[0].RevisionId, rb);
            t.True(new LibrarySet(new[] { LibraryFile.Read(path) }).Offered(TableKinds.Clearance).Single(x => x.Key == "ISO273").Rows.Select(r => r.Key).SequenceEqual(new[] { "M8" }), "only M8 offered");
            // a new company library, added to the set once
            var config = Path.Combine(dir, LibraryConfig.FileName);
            File.WriteAllText(config, """{ "libraries": [ "standards.sprlib" ] }""");
            var sub = Path.Combine(dir, "shop", "fixtures.sprlib");
            Directory.CreateDirectory(Path.GetDirectoryName(sub)!);
            LibraryFile.Publish(sub, Guid.NewGuid(), "Fixtures", LibraryLayer.Company, new[] { Clearance(("M6", 6.4, 6.6, 7)) }, null, "admin");
            t.True(!LibraryConfig.Lists(config, sub), "not listed yet");
            t.True(LibraryConfig.Add(config, sub), "added");
            t.True(!LibraryConfig.Add(config, sub), "added once");
            t.True(LibraryConfig.Lists(config, sub), "listed");
            var listed = System.Text.Json.JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(config), Serialization.Options)!.Libraries;
            t.Eq(string.Join(",", listed), "standards.sprlib,shop/fixtures.sprlib");
        });

        t.Test("Library holes: countersunk M16 and M20 sized from ISO 10642; M18 (not in it) said, not sized from the size before", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var doc = new CadDocument();
                LibrarySnapshot? Resolve(string i) => LibraryLink.Resolve(doc, i);
                var hole = doc.AddFeature(new HoleFeature { Type = HoleType.Countersink, Standard = "ISO273", Size = "M16" });
                HoleSizes.Apply(hole, Resolve);
                t.Eq(hole.Diameter, "17.5mm"); t.Eq(hole.CsDiameter, "32.4mm");
                t.True(HoleSizes.Missing(hole, Resolve) == null, "M16 has a countersink");
                hole.Size = "M20"; HoleSizes.Apply(hole, Resolve);
                t.Eq(hole.Diameter, "22mm"); t.Eq(hole.CsDiameter, "40.4mm");
                hole.Size = "M18"; HoleSizes.Apply(hole, Resolve);
                t.Eq(hole.Diameter, "20mm");
                t.True(HoleSizes.Missing(hole, Resolve)?.Contains("no M18 countersink") == true, HoleSizes.Missing(hole, Resolve) ?? "(none)");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Library units: inch standards shown in inches (angles in degrees), stored in mm; the units kept through a publish and the set", () =>
        {
            var csink = BuiltInStandards.Tables.Single(x => x.Key == "ASME-B18.6.3-csink");
            t.Eq(csink.Units, LibraryTable.Inch);
            t.Eq(csink.Scale("diameter"), 25.4); t.Eq(csink.Scale("angle"), 1.0);
            t.Eq(BuiltInStandards.Tables.Single(x => x.Key == "ISO273").Scale("normal"), 1.0);
            var path = Path.Combine(NewDir("units"), "company.sprlib");
            var tb = Clearance(("#10", 5.1, 5.3, 5.8)); tb.Key = "ACME-inch"; tb.Units = LibraryTable.Inch;
            LibraryFile.Publish(path, Guid.NewGuid(), "Acme", LibraryLayer.Company, new[] { tb }, null, "admin");
            var lib = LibraryFile.Read(path);
            t.Eq(lib.Tables.Single().Units, LibraryTable.Inch);
            t.Eq(new LibrarySet(new[] { lib }).Offered(TableKinds.Clearance).Single(x => x.Key == "ACME-inch").Units, LibraryTable.Inch);
            t.True(!J(Clearance(("M8", 8.4, 9, 10))).Contains("units", StringComparison.OrdinalIgnoreCase), "no units (mm): not written, so metric tables are unchanged");
        });
    }
}

using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
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

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }

    const string Shcs = "Socket head cap screw";
    /// <summary>A socket head cap screw's row key in a fastener holes table ("Socket head cap screw:M8").</summary>
    static string Sh(string size) => LibraryRow.JoinKeys(new[] { Shcs, size });

    /// <summary>A company's fastener holes table (ISO-holes): socket head cap screw clearances by size.</summary>
    static LibraryTable Clearance(params (string row, double close, double normal, double loose)[] rows) => new()
    {
        Key = "ISO-holes", Title = "Metric holes for fasteners", Kind = TableKinds.FastenerHoles, Standard = "ISO 273",
        Columns = { "close", "normal", "loose" },
        Rows = rows.Select(r => new LibraryRow { Keys = { Shcs, r.row }, Values = { r.close, r.normal, r.loose } }).ToList(),
    };

    /// <summary>A share folder holding a company standards library (and the library set listing it), with the cache in its own folder.</summary>
    static (string config, string lib) Share(string dir, IEnumerable<LibraryTable> tables)
    {
        var lib = Path.Combine(dir, "company.sprlib");
        LibraryFile.Publish(lib, Guid.NewGuid(), "Acme", LibraryDomain.Standards, tables, "admin");
        var config = Path.Combine(dir, LibraryConfig.FileName);
        File.WriteAllText(config, """{ "libraries": [ "company.sprlib" ] }""");
        return (config, lib);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Library: SpryCAD's default standards well formed (a value per column, unique keys); written as a company's own library (an id of its own) and read back unchanged", () =>
        {
            var keys = BuiltInStandards.Tables.Select(x => x.Key).ToList();
            t.True(keys.Distinct().Count() == keys.Count, "unique table keys");
            foreach (var tb in BuiltInStandards.Tables)
            {
                t.True(tb.Rows.All(r => r.Values.Count == tb.Columns.Count && r.Values.Select((v, i) => v > 0 || double.IsNaN(v) && Schemas.Of(tb.Kind)?.Column(tb.Columns[i])?.Required == false).All(ok => ok)), $"{tb.Key}: a positive value per column (or none, where not required)");
                t.True(tb.Rows.Select(r => r.Key).Distinct().Count() == tb.Rows.Count, $"{tb.Key}: unique rows");
                t.True(!tb.Verified, $"{tb.Key}: not marked verified (values from secondary sources)");
            }
            var unc = BuiltInStandards.Tables.Single(x => x.Key == "ASME-B1.1-UNC").Row("1/4-20 UNC:2A")!;
            t.Near(unc.Values[0], 0.25, 1e-12); t.Near(unc.Values[1], 0.05, 1e-12); // (in inches, as published; read in mm below)
            t.Near(LibrarySet.BuiltIn.Snapshot("ASME-B1.1-UNC:1/4-20 UNC:2A")!.Base("major")!.Value, 6.35, 1e-12);
            var path = Path.Combine(NewDir("builtin"), "standards.sprlib");
            var rev = LibraryFile.WriteDefaults(path, LibraryDomain.Standards, "Acme standards", "test");
            var lib = LibraryFile.Read(path);
            t.True(lib.LibraryId != BuiltInStandards.LibraryId, "the company's own library");
            t.Eq(lib.Name, "Acme standards");
            t.Eq(lib.RevisionId, rev);
            t.Eq(lib.Domain, LibraryDomain.Standards);
            t.Eq(J(lib.Tables), J(LibraryFile.Defaults(LibraryDomain.Standards).OrderBy(x => x.Key, StringComparer.Ordinal).ToList()));
            t.True(LibraryFile.Defaults(LibraryDomain.Standards).Any(x => x.Key == "ISO4762"), "the fastener families with the standards");
            t.True(LibraryFile.IsLibrary(path) && PartDatabase.IsFile(path), "a SpryCAD database, its root a library");
        });

        t.Test("Library: publishing the same tables again makes no revision; a change does; a stale read is refused", () =>
        {
            var path = Path.Combine(NewDir("publish"), "company.sprlib");
            var id = Guid.NewGuid();
            var r1 = LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 8.4, 9, 10)) }, "admin");
            var r2 = LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 8.4, 9, 10)) }, "admin", readRevision: r1);
            t.Eq(r2, r1);
            var r3 = LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 8.4, 9.2, 10)) }, "admin", readRevision: r1);
            t.True(r3 != r1, "a new revision");
            t.Eq(PartFile.History(path).Count, 2);
            // someone read r1, someone else published r3 since: refused, the file untouched
            bool refused = false;
            try { LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 1, 2, 3)) }, "other", readRevision: r1); }
            catch (LibraryConflictException) { refused = true; }
            t.True(refused, "refused");
            t.Eq(LibraryFile.Read(path).RevisionId, r3);
        });

        t.Test("Library set: a company library is used in place of SpryCAD's defaults for its domain, whole (no merging); the other domain stays the defaults", () =>
        {
            var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null,
                new[] { Clearance(("M8", 8.4, 9.2, 10), ("M7", 7.4, 7.6, 8)) });
            var set = new LibrarySet(new[] { company });
            t.Eq(set.Standards.Name, "Acme");
            t.Eq(set.Materials.LibraryId, BuiltInMaterials.LibraryId);
            var iso = set.Table("ISO-holes")!;
            t.Eq(string.Join(",", iso.Rows.Select(r => r.Keys[1])), "M8,M7"); // (only the company's: its preferred sizes)
            t.Near(iso.Row(Sh("M8"))!.Values[1], 9.2, 1e-12);
            t.True(set.Table("ISO261-coarse") == null && set.Snapshot("ISO-holes:Socket head cap screw:M48") == null, "the defaults' tables and rows not in use");
            t.Eq(set.Offered(TableKinds.FastenerHoles).Single().Key, "ISO-holes");
            t.Eq(set.Snapshot("ISO-holes:Socket head cap screw:M8")!.LibraryName, "Acme");
            t.True(set.Table("Steel") != null, "the materials still SpryCAD's defaults");
            // two standards libraries given: the first is used
            var other = new LoadedLibrary(Guid.NewGuid(), "Other", LibraryDomain.Standards, Guid.NewGuid(), null, new[] { Clearance(("M8", 1, 2, 3)) });
            t.Eq(new LibrarySet(new[] { company, other }).Standards.Name, "Acme");
            // a table of the wrong domain refused on publishing
            t.True(Throws(() => LibraryFile.Publish(Path.Combine(NewDir("domain"), "m.sprlib"), Guid.NewGuid(), "M", LibraryDomain.Materials, new[] { Clearance(("M8", 8.4, 9, 10)) }, "admin")),
                "a clearance table in a materials library refused");
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
                t.Near(r1.Set.Table("ISO-holes")!.Row(Sh("M8"))!.Values[1], 9.2, 1e-12);
                var copies = Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib");
                t.Eq(copies.Length, 1);
                var copyTime = File.GetLastWriteTimeUtc(copies[0]);
                // unchanged: read from the copy (not copied again)
                var r2 = LibrarySync.Load(config);
                t.Eq(r2.Set.Standards.RevisionId, r1.Set.Standards.RevisionId);
                t.Eq(File.GetLastWriteTimeUtc(Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib").Single()), copyTime);
                // published again: the new revision copied, the old copy removed
                var read = LibraryFile.Read(lib);
                LibraryFile.Publish(lib, read.LibraryId, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 8.4, 9.4, 10)) }, "admin", readRevision: read.RevisionId);
                var r3 = LibrarySync.Load(config);
                t.Near(r3.Set.Table("ISO-holes")!.Row(Sh("M8"))!.Values[1], 9.4, 1e-12);
                t.Eq(Directory.GetFiles(LibrarySync.CacheFolder, "*.sprlib").Length, 1);
                // the share gone (offline): the copies last read, with a note
                Directory.Delete(share, true);
                var r4 = LibrarySync.Load(config);
                t.Near(r4.Set.Table("ISO-holes")!.Row(Sh("M8"))!.Values[1], 9.4, 1e-12);
                t.True(r4.Problems.Count > 0, "says it is using the copies");
                // no set configured: SpryCAD's default standards and materials
                var none = LibrarySync.Load(null).Set;
                t.Eq(none.Standards.LibraryId, BuiltInStandards.LibraryId); t.Eq(none.Materials.LibraryId, BuiltInMaterials.LibraryId);
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
                    Face = new FaceRef { Name = cyl.Tag.Name, Hint = new Vec3(3.175, 0, 19) }, Size = "1/4-20 UNC", Standard = "ASME-B1.1-UNC", Class = "2A",
                    End = ThreadEnd.ThroughAll, Cosmetic = true, Chamfer = false,
                });
                var hole = doc.AddFeature(new HoleFeature { SketchId = s.Id, Standard = "ISO-holes", Fastener = Shcs, Size = "M8", Type = HoleType.Counterbore });
                LibraryLink.Current = LibrarySet.BuiltIn;
                t.True(LibraryLink.Sync(doc), "snapshots added");
                t.Eq(string.Join(",", doc.Library!.Keys), "ASME-B1.1-UNC:1/4-20 UNC:2A,ISO-holes:Socket head cap screw:M8");
                t.True(!LibraryLink.Sync(doc), "nothing more to do");
                // saved and read back: the snapshots are objects of the part, unchanged
                var path = Path.Combine(NewDir("part"), "shaft.sprt");
                PartFile.Save(doc, path);
                var loaded = PartFile.Load(path);
                t.Eq(J(loaded.Library), J(doc.Library));
                PartFile.Save(loaded, path);
                t.Eq(PartFile.History(path).Count, 1);
                // with libraries whose UNC row is different (and wrong): the part still uses its own
                var wrong = new LoadedLibrary(Guid.NewGuid(), "Wrong", LibraryDomain.Standards, Guid.NewGuid(), null,
                    new[] { new LibraryTable { Key = "ASME-B1.1-UNC", Kind = TableKinds.Thread, Columns = { "major", "pitch" }, Rows = { new LibraryRow { Keys = { "1/4-20 UNC", "2A" }, Values = { 6.35, 2.0 } } } } });
                LibraryLink.Current = new LibrarySet(new[] { wrong });
                var r = new Regenerator().Rebuild(loaded);
                t.True(r.Status[th.Id].Ok, r.Status[th.Id].Error ?? "");
                t.Near(r.State.Cosmetics[th.Id].Lead, 1.27, 1e-12);
                // removing the hole drops its snapshots
                loaded.Features.RemoveAll(f => f.Id == hole.Id);
                LibraryLink.Sync(loaded);
                t.Eq(string.Join(",", loaded.Library!.Keys), "ASME-B1.1-UNC:1/4-20 UNC:2A");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Library updates: a changed row is offered with its values; accepting it sizes the hole again (but a value typed over), in the part's history", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var doc = new CadDocument();
                var hole = doc.AddFeature(new HoleFeature { Type = HoleType.Counterbore, Standard = "ISO-holes", Fastener = Shcs, Size = "M8" });
                HoleFeature Made() => HoleSizes.Effective(hole, i => LibraryLink.Resolve(doc, i));
                t.Eq(Made().Diameter, "9mm");
                t.Eq(Made().CbDiameter, "15mm");
                hole.Overrides = new() { ["CbDepth"] = "9mm" };
                LibraryLink.Sync(doc);
                t.Eq(LibraryLink.Updates(doc).Count, 0);
                // (the company's copy of the table, its M8 normal clearance 9.2)
                var acme = System.Text.Json.JsonSerializer.Deserialize<LibraryTable>(System.Text.Json.JsonSerializer.Serialize(LibrarySet.BuiltIn.Table("ISO-holes"), LibraryJson.Options), LibraryJson.Options)!;
                acme.Row(Sh("M8"))!.Values[acme.Column("normal")] = 9.2;
                var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null, new[] { acme });
                LibraryLink.Current = new LibrarySet(new[] { company });
                var ups = LibraryLink.Updates(doc);
                t.Eq(ups.Count, 1);
                t.Eq(ups[0].Describe(), "Socket head cap screw M8 (ISO-holes): normal 9 → 9.2");
                var path = Path.Combine(NewDir("update"), "plate.sprt");
                PartFile.Save(doc, path);
                LibraryLink.Accept(doc, ups);
                t.Eq(Made().Diameter, "9.2mm");
                t.Eq(Made().CbDepth, "9mm"); // (typed over: kept)
                t.Eq(Made().CbDiameter, "15mm");
                t.Eq(doc.Library!["ISO-holes:Socket head cap screw:M8"].LibraryName, "Acme");
                PartFile.Save(doc, path);
                using var db = PartDatabase.Open(path);
                var h = db.GetHistory(Guid.Parse(doc.Id!));
                var changes = PartDescribe.Changes(db, h[1].RevisionId, h[0].RevisionId).Select(c => c.Text).ToList();
                t.True(changes.Any(c => c.StartsWith("Library data Socket head cap screw M8 (ISO-holes)")), string.Join(" | ", changes));
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
            var r1 = LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { tb }, "admin");
            var back = new LibraryTable { Key = tb.Key, Title = tb.Title, Kind = tb.Kind, Standard = tb.Standard, Columns = cols, Rows = rows };
            t.Eq(LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { back }, "admin", readRevision: r1), r1);
        });

        t.Test("Library editing: two admins read the same revision; the second to publish is refused, reloads and publishes on top", () =>
        {
            var dir = NewDir("edit");
            var path = Path.Combine(dir, "company.sprlib");
            var id = Guid.NewGuid();
            LibraryFile.Publish(path, id, "Acme", LibraryDomain.Standards, new[] { Clearance(("M8", 8.4, 9, 10)) }, "admin", "first");
            var a = LibraryFile.Read(path);
            var b = LibraryFile.Read(path);
            var ra = LibraryFile.Publish(path, id, a.Name, a.Domain, new[] { Clearance(("M8", 8.4, 9, 10), ("M10", 10.5, 11, 12)) }, "alice", "M10 added", a.RevisionId);
            bool refused = false;
            try { LibraryFile.Publish(path, id, b.Name, b.Domain, new[] { Clearance(("M8", 8.4, 9, 10), ("M12", 13, 13.5, 14.5)) }, "bob", "M12 added", b.RevisionId); }
            catch (LibraryConflictException) { refused = true; }
            t.True(refused, "bob's publish refused");
            t.True(!File.Exists(path + ".saving"), "nothing left beside the file");
            var reread = LibraryFile.Read(path);
            t.Eq(reread.RevisionId, ra);
            t.Eq(reread.Tables.Single().Rows.Count, 2);
            var mine = reread.Tables.Single();
            mine.Rows.Add(new LibraryRow { Keys = { Shcs, "M12" }, Values = { 13, 13.5, 14.5 } });
            var rb = LibraryFile.Publish(path, id, reread.Name, reread.Domain, new[] { mine }, "bob", "M12 added", reread.RevisionId);
            var history = PartFile.History(path);
            t.Eq(string.Join(" | ", history.Select(h => $"{h.Author}: {h.Message}")), "bob: M12 added | alice: M10 added | admin: first");
            t.Eq(history[0].RevisionId, rb);
            t.True(new LibrarySet(new[] { LibraryFile.Read(path) }).Offered(TableKinds.FastenerHoles).Single(x => x.Key == "ISO-holes").Rows.Select(r => r.Keys[1]).SequenceEqual(new[] { "M8", "M10", "M12" }), "both admins' sizes");
            // a new company library, added to the set once
            var config = Path.Combine(dir, LibraryConfig.FileName);
            File.WriteAllText(config, """{ "libraries": [ "standards.sprlib" ] }""");
            var sub = Path.Combine(dir, "shop", "fixtures.sprlib");
            Directory.CreateDirectory(Path.GetDirectoryName(sub)!);
            LibraryFile.Publish(sub, Guid.NewGuid(), "Fixtures", LibraryDomain.Standards, new[] { Clearance(("M6", 6.4, 6.6, 7)) }, "admin");
            t.True(!LibraryConfig.Lists(config, sub), "not listed yet");
            t.True(LibraryConfig.Add(config, sub), "added");
            t.True(!LibraryConfig.Add(config, sub), "added once");
            t.True(LibraryConfig.Lists(config, sub), "listed");
            var listed = System.Text.Json.JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(config), Serialization.Options)!.Libraries;
            t.Eq(string.Join(",", listed), "standards.sprlib,shop/fixtures.sprlib");
        });

        t.Test("Library holes: a flat head screw's countersunk M16 and M20 from ISO-holes; no M18 row (its own values); holes sized before library v2 named by their row, values that differ kept as typed", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var doc = new CadDocument();
                LibrarySnapshot? Resolve(string i) => LibraryLink.Resolve(doc, i);
                var hole = doc.AddFeature(new HoleFeature { Type = HoleType.Countersink, Standard = "ISO-holes", Fastener = "Flat head screw", Size = "M16", CsDiameter = "1mm" });
                var made = HoleSizes.Effective(hole, Resolve);
                t.Eq(made.Diameter, "17.5mm"); t.Eq(made.CsDiameter, "32.4mm"); t.Eq(made.CsAngle, "90");
                hole.Fit = "close";
                t.Eq(HoleSizes.Effective(hole, Resolve).Diameter, "17mm");
                hole.Fit = null; hole.Size = "M20";
                made = HoleSizes.Effective(hole, Resolve);
                t.Eq(made.Diameter, "22mm"); t.Eq(made.CsDiameter, "40.4mm");
                hole.Size = "M18";
                t.True(Resolve(HoleSizes.ItemOf(hole)!) == null, "no M18 row for flat head screws");
                t.Eq(HoleSizes.Effective(hole, Resolve).CsDiameter, "1mm");
                var table = LibraryLink.Current.Table("ISO-holes")!;
                t.True(table.Rows.Where(r => HoleSizes.Suits(HoleType.Countersink, table, r)).All(r => r.Keys[0] == "Flat head screw"), "countersinks: flat head rows");
                t.True(table.Rows.Where(r => HoleSizes.Suits(HoleType.Counterbore, table, r)).All(r => r.Keys[0] == Shcs), "counterbores: socket head rows");
                // a hole sized before library v2 (ISO273 with ISO 10642's countersink): its fastener holes row; the countersink typed bigger kept
                var old = new HoleFeature { Type = HoleType.Countersink, Standard = "ISO273", Size = "M16", Diameter = "17.5mm", CsDiameter = "33mm", CsAngle = "90" };
                HoleSizes.Upgrade(old, LibrarySet.BuiltIn);
                t.Eq(old.Standard, "ISO-holes"); t.Eq(old.Fastener, "Flat head screw"); t.Eq(old.Size, "M16");
                t.Eq(string.Join(",", old.Overrides!.Select(kv => $"{kv.Key}={kv.Value}")), "CsDiameter=33mm");
                var plain = new HoleFeature { Standard = "ASME-B18.2.8", Size = "1/4", Diameter = HoleSizes.LibraryValue(new HoleFeature { Standard = "ASME-holes", Fastener = Shcs, Size = "1/4" }, "Diameter", LibrarySet.BuiltIn.Snapshot)! };
                HoleSizes.Upgrade(plain, LibrarySet.BuiltIn);
                t.Eq(plain.Standard + "|" + plain.Fastener, "ASME-holes|" + Shcs);
                t.True(plain.Overrides == null, "the same diameter: nothing typed over");
                // and when a part is read
                var part = new CadDocument();
                part.AddFeature(new HoleFeature { Standard = "ISO273", Size = "M8", Diameter = "9mm" });
                var back = CadDocument.FromJson(part.ToJson());
                var h = back.Features.OfType<HoleFeature>().Single();
                t.Eq($"{h.Standard}|{h.Fastener}|{h.Size}", "ISO-holes|" + Shcs + "|M8");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Parts from before library v2: snapshots re-keyed (ISO273/M8 is ISO273:M8) and given their kind, a material's SI values put in its table's units, the items the part names re-keyed; the part the same", () =>
        {
            // (a part as an earlier SpryCAD wrote it: '/' keys, snapshots without kind or units, a material's values in SI)
            var doc = new CadDocument { MaterialItem = "Materials-aluminum/Aluminum 6061" };
            var hole = new LibrarySnapshot { Table = "ISO273", Row = "M8", Columns = { "close", "normal", "loose" }, Values = { 8.4, 9, 10 } };
            var alu = LibrarySet.BuiltIn.Snapshot("Aluminum:Aluminum 6061")!;
            var old = new LibrarySnapshot
            {
                Table = "Materials-aluminum", Row = alu.Row, Columns = alu.Columns.ToList(), Text = alu.Text,
                Values = alu.Columns.Select((c, i) => Schemas.UnitOf(TableKinds.Material, c).ToBase(alu.Values[i], TableUnits.Metric)).ToList(),
            };
            doc.Library = new(StringComparer.Ordinal) { ["ISO273/M8"] = hole, ["Materials-aluminum/Aluminum 6061"] = old };
            doc.Features.Add(new FastenerFeature { Id = "F1", Item = "ISO4762/M8x30" });
            doc.Tables = new() { new DesignTable { Name = "Grade", Columns = { TableConfig.Material }, Rows = { new DesignRow { Key = "A", Values = { "Materials-aluminum/Aluminum 6061" } } } } };
            var back = CadDocument.FromJson(doc.ToJson());
            t.Eq(string.Join(",", back.Library!.Keys), "Aluminum:Aluminum 6061,ISO273:M8"); // (ISO273: a clearance table's, no kind now)
            t.Eq(back.MaterialItem, "Aluminum:Aluminum 6061");
            t.Eq(back.Library["Aluminum:Aluminum 6061"].Kind, TableKinds.Material);
            t.Near(back.EffectiveDensity, 2700, 1e-9);
            t.True(back.Library["Aluminum:Aluminum 6061"].SameData(alu), "the material as the library has it now");
            t.Eq(((FastenerFeature)back.Features.Single()).Item, "ISO4762:M8x30");
            t.Eq(back.Tables![0].Rows[0].Values[0], "Aluminum:Aluminum 6061");
            // (the material tables renamed: "Materials-aluminum" is "Aluminum"; but a company library that still has the
            // old key keeps it, and its parts too)
            var before = LibraryLink.Current;
            try
            {
                // (a copy: the defaults' tables are shared)
                var own = System.Text.Json.JsonSerializer.Deserialize<List<LibraryTable>>(LibraryJson.Write(LibraryFile.Defaults(LibraryDomain.Materials)), LibraryJson.Options)!;
                own.Single(x => x.Key == "Aluminum").Key = "Materials-aluminum";
                LibraryLink.Current = new LibrarySet(new[] { new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Materials, Guid.NewGuid(), null, own) });
                var kept = CadDocument.FromJson(new CadDocument { MaterialItem = "Materials-aluminum:Aluminum 6061" }.ToJson());
                t.Eq(kept.MaterialItem, "Materials-aluminum:Aluminum 6061");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Library units: an inch standard holds its values in inches as published (angles in degrees), read in mm; a snapshot keeps the units; kept through a publish and the set", () =>
        {
            var holes = BuiltInStandards.Tables.Single(x => x.Key == "ASME-holes");
            t.Eq(holes.Units, TableUnits.Inch);
            var flat = holes.Rows.First(r => r.Keys[0] == "Flat head screw" && r.Keys[1] == "#10");
            t.Eq(flat.Values[holes.Column("head.diameter")], 0.385);
            t.Near(holes.Base(flat, "head.diameter")!.Value, 0.385 * 25.4, 1e-12);
            t.Eq(holes.Base(flat, "head.angle"), 82.0);
            t.Eq(holes.Label("head.diameter"), "in"); t.Eq(holes.Label("head.angle"), "°");
            t.Eq(BuiltInStandards.Tables.Single(x => x.Key == "ISO-holes").Label("normal"), "mm");
            var snap = LibrarySet.BuiltIn.Snapshot("ASME-holes:Flat head screw:#10")!;
            t.Eq(snap.Units, TableUnits.Inch); t.Eq(snap.Kind, TableKinds.FastenerHoles);
            t.Eq(snap.Value("head.diameter"), 0.385); t.Near(snap.Base("head.diameter")!.Value, 0.385 * 25.4, 1e-12);
            var path = Path.Combine(NewDir("units"), "company.sprlib");
            var tb = Clearance(("#10", 0.201, 0.209, 0.228)); tb.Key = "ACME-inch"; tb.Units = TableUnits.Inch;
            LibraryFile.Publish(path, Guid.NewGuid(), "Acme", LibraryDomain.Standards, new[] { tb }, "admin");
            var lib = LibraryFile.Read(path);
            t.Eq(lib.Tables.Single().Units, TableUnits.Inch);
            t.Eq(lib.Tables.Single().Rows[0].Values[1], 0.209);
            t.Eq(new LibrarySet(new[] { lib }).Offered(TableKinds.FastenerHoles).Single(x => x.Key == "ACME-inch").Units, TableUnits.Inch);
        });

        t.Test("Library workbooks: SpryCAD's default standards and materials to Excel and back unchanged, a sheet each describing itself (key, kind, units, grouped headings with units); wrong cells named by sheet and cell", () =>
        {
            foreach (var domain in new[] { LibraryDomain.Standards, LibraryDomain.Materials })
            {
                var source = LibraryFile.Defaults(domain);
                var ms = new MemoryStream();
                LibraryWorkbook.Write(source, ms);
                ms.Position = 0;
                var (tables, problems) = LibraryWorkbook.Read(ms);
                t.True(problems.Count == 0, string.Join("; ", problems));
                t.Eq(tables.Count, source.Count);
                foreach (var (a, b) in source.Zip(tables))
                {
                    t.Eq(b.Key, a.Key); t.Eq(b.Title, a.Title); t.Eq(b.Kind, a.Kind); t.Eq(b.Standard, a.Standard); t.Eq(b.Units, a.Units); t.Eq(b.Verified, a.Verified);
                    t.Eq(string.Join(",", b.Columns), string.Join(",", a.Columns));
                    t.Eq(b.Rows.Count, a.Rows.Count);
                    foreach (var (ra, rb) in a.Rows.Zip(b.Rows))
                    {
                        t.Eq(rb.Key, ra.Key);
                        t.True(ra.Values.Zip(rb.Values).All(v => v.First.Equals(v.Second) || double.IsNaN(v.First) && double.IsNaN(v.Second)
                                || Math.Abs(v.First - v.Second) <= 1e-12 * Math.Max(1, Math.Abs(v.First))), // (a spreadsheet keeps 15 digits)
                             $"{a.Key} {ra.Key}: {string.Join(",", ra.Values)} vs {string.Join(",", rb.Values)}");
                        t.Eq(J(rb.Text), J(ra.Text));
                    }
                }
            }
            // the layout: what the table is in row 1, its title in row 2, then its headings with units, then its rows
            var buf = new MemoryStream();
            LibraryWorkbook.Write(LibraryFile.Defaults(LibraryDomain.Standards).Concat(BuiltInMaterials.Tables.Take(1)), buf);
            buf.Position = 0;
            using (var wb = new ClosedXML.Excel.XLWorkbook(buf))
            {
                var iso = wb.Worksheet("ISO-holes");
                t.Eq(iso.Cell(1, 1).GetString() + iso.Cell(1, 2).GetString() + iso.Cell(1, 3).GetString() + iso.Cell(1, 4).GetString(), "TableISO-holesKindfastener-holes");
                t.Eq(iso.Cell(4, 1).GetString() + "|" + iso.Cell(4, 2).GetString() + "|" + iso.Cell(3, 3).GetString() + "|" + iso.Cell(4, 4).GetString(), "Fastener|Size|Shaft Clearance|Normal (mm)");
                var unc = wb.Worksheet("ASME-B1.1-UNC");
                t.Eq(string.Join("|", Enumerable.Range(1, 3).Select(c => unc.Cell(5, c).GetString())) + "|" + unc.Cell(6, 2).GetString(), "Designation|Class|Major Diameter (in)|2A");
                var steel = wb.Worksheet("Steel");
                t.Eq(steel.Cell(3, 2).GetString() + "|" + steel.Cell(4, 2).GetString(), "Physical|Density (g/cm³)");
                t.True(steel.Range(3, 3, 3, 9).IsMerged() || steel.Cell(3, 3).IsMerged(), "a group merged over its columns");
                var color = Enumerable.Range(1, 30).First(c => steel.Cell(4, c).GetString() == "Color");
                t.True(steel.Cell(5, color).GetString().StartsWith("#"), "a color shown as hex");
                t.True(Enumerable.Range(1, 30).All(c => steel.Cell(4, c).GetString() is not ("Red" or "Green" or "Blue")), "one Color column, not r, g and b");

                // a value typed as words, a size listed twice, a column in the wrong units: each said with its sheet and cell; that table left out
                iso.Cell(6, 4).SetValue("about nine");
                iso.Cell(7, 1).SetValue(iso.Cell(5, 1).GetString()); iso.Cell(7, 2).SetValue(iso.Cell(5, 2).GetString());
                iso.Cell(4, 3).SetValue("Close (in)");
                // a color that is not hex: said
                steel.Cell(6, color).SetValue("gray");
                var edited = new MemoryStream();
                wb.SaveAs(edited);
                edited.Position = 0;
                var (t2, p2) = LibraryWorkbook.Read(edited);
                t.True(p2.Any(x => x.StartsWith("Sheet ISO-holes, cell D6") && x.Contains("not a number")), string.Join("; ", p2));
                t.True(p2.Any(x => x.StartsWith("Sheet ISO-holes, cell A7") && x.Contains("twice")), string.Join("; ", p2));
                t.True(p2.Any(x => x.StartsWith("Sheet ISO-holes, cell C4") && x.Contains("units")), string.Join("; ", p2));
                t.True(p2.Any(x => x.StartsWith($"Sheet Steel, cell {ClosedXML.Excel.XLHelper.GetColumnLetterFromNumber(color)}6") && x.Contains("not a hex color")), string.Join("; ", p2));
                t.True(t2.All(x => x.Key is not ("ISO-holes" or "Steel")) && t2.Count == LibraryFile.Defaults(LibraryDomain.Standards).Count - 1, "the tables with problems left out, the others read");
                // a color typed as hex: read into r, g and b
                steel.Cell(6, color).SetValue("#FF8000");
                var edited2 = new MemoryStream(); wb.SaveAs(edited2); edited2.Position = 0;
                var st = LibraryWorkbook.Read(edited2).tables.Single(x => x.Key == "Steel");
                var row6 = st.Rows[1];
                t.Eq($"{row6.Values[st.Column("r")]},{row6.Values[st.Column("g")]},{row6.Values[st.Column("b")]}", $"1,{128 / 255.0},0");
                t.True(st.Column("r") < st.Column("metallic"), "r, g and b where the table has them");
            }
        });

        t.Test("Library hole types: a counterbore's profile made a hole type (D, CbD, CbDepth its columns), M6 and M8 rows; an M8 hole of it cuts what the built-in M8 counterbore does, and follows a library update", () =>
        {
            // (a plate 10 thick both ways from the Front plane, the hole from its top face, through all)
            (CadDocument doc, HoleFeature h) Part(Action<HoleFeature> set)
            {
                var doc = new CadDocument();
                var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60) });
                doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10", End = EndCondition.MidPlane });
                var top = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "5" });
                var sk = new Sketch();
                sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(20, 10)).Id });
                var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = top.Id }, Sketch = sk });
                var h = new HoleFeature { SketchId = s2.Id, End = HoleEnd.ThroughAll, Type = HoleType.Counterbore };
                set(h);
                doc.AddFeature(h);
                return (doc, h);
            }
            static double Vol(CadDocument d) => new Regenerator().Rebuild(d).State.Bodies.Sum(b => b.Mesh.Volume);
            // the type, from a counterbore hole's profile: its names the columns, the hole's values its first row
            var proto = new HoleFeature { Type = HoleType.Counterbore, Diameter = "6.6", CbDiameter = "11", CbDepth = "6.5" };
            var look = HoleProfile.Lookup(proto, _ => null, 1, 10);
            var (type, problem) = HoleProfile.ToLibraryType(HoleProfile.Default(proto, look, 1), "ACME-cbore", "M6", look);
            t.True(type != null, problem ?? "");
            t.Eq(string.Join(",", type!.Columns), "CbD,CbDepth,D");
            type.Rows.Add(new LibraryRow { Key = "M8", Values = { 15, 8.6, 9 } });
            var lib = new LoadedLibrary(Guid.NewGuid(), "ACME", LibraryDomain.Standards, Guid.NewGuid(), null, new[] { type });
            var set = new LibrarySet(new[] { lib });
            t.Eq(set.Offered(TableKinds.HoleType).Single().Key, "ACME-cbore");
            t.True(set.Table("ACME-cbore")!.Profile != null, "the profile kept with the table");
            var (doc, h) = Part(x =>
            {
                x.LibraryType = "ACME-cbore"; x.Size = "M8"; x.Profile = HoleProfile.FromJson(set.Table("ACME-cbore")!.Profile);
            });
            LibraryLink.Sync(doc, set);
            t.Eq(HoleSizes.Effective(h, i => LibraryLink.Resolve(doc, i)).Diameter, "9mm");
            var (builtIn, _) = Part(x => { x.Diameter = "9"; x.CbDiameter = "15"; x.CbDepth = "8.6"; });
            t.Near(Vol(doc), Vol(builtIn), 1e-6);
            t.Eq(string.Join(",", h.LibraryItems()), "ACME-cbore:M8");
            // the library's M8 counterbore made deeper, the update accepted: the hole follows
            var before = Vol(doc);
            type.Rows[1].Values[1] = 9.6;
            var set2 = new LibrarySet(new[] { lib with { RevisionId = Guid.NewGuid() } });
            var updates = LibraryLink.Updates(doc, set2);
            t.Eq(updates.Count, 1);
            LibraryLink.Accept(doc, updates);
            t.Near(HoleSizes.Effective(h, i => LibraryLink.Resolve(doc, i)).TypeValues!["CbDepth"], 9.6, 1e-12);
            var ring = Math.PI * (7.5 * 7.5 - 4.5 * 4.5) * 1;
            t.Near(before - Vol(doc), ring, ring * 0.02);
        });

        t.Test("Design table workbooks: a part's equations and tables to Excel and back unchanged (numbers as numbers, expressions as text, the row in use)", () =>
        {
            var doc = Demos.SocketScrew();
            var ms = new MemoryStream();
            DesignTableWorkbook.Write(doc.Equations, doc.Tables ?? new(), ms);
            ms.Position = 0;
            var (eqs, tables, problems) = DesignTableWorkbook.Read(ms);
            t.True(problems.Count == 0, string.Join("; ", problems));
            t.Eq(J(eqs.Select(e => (e.Name, e.Expr, e.Exposed, e.Description))), J(doc.Equations.Select(e => (e.Name, e.Expr, e.Exposed, e.Description))));
            t.Eq(J(tables.Select(x => (x.Name, x.Columns, x.Selected, x.Exposed, x.Description))), J((doc.Tables ?? new()).Select(x => (x.Name, x.Columns, x.Selected, x.Exposed, x.Description))));
            t.Eq(J(tables.Select(x => x.Rows.Select(r => (r.Key, r.Values)))), J((doc.Tables ?? new()).Select(x => x.Rows.Select(r => (r.Key, r.Values)))));
            t.True((doc.Tables?.Count ?? 0) > 0, "the demo has design tables");
        });
    }
}

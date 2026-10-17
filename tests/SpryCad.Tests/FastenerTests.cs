using SpryCad.Core.Assembly;
using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Core.Storage;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Fasteners (file format spec section 25): part family tables, generated bodies, Insert Fastener, the BOM.</summary>
static class FastenerTests
{
    static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fc-fastener-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string S(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A plate 100 x 60, <paramref name="thickness"/> thick (z 0 up), with holes through it from its top at the points
    /// given: M8 clearance holes from the library, or (<paramref name="drill"/>) plain holes of that diameter.
    /// </summary>
    static (CadDocument doc, HoleFeature hole) Plate(string path, double thickness, IEnumerable<Vec2> holes, double? drill = null)
    {
        var doc = new CadDocument();
        var sk = new Sketch();
        var a = sk.AddPoint(new Vec2(0, 0)); var b = sk.AddPoint(new Vec2(100, 0));
        var c = sk.AddPoint(new Vec2(100, 60)); var e = sk.AddPoint(new Vec2(0, 60));
        sk.Add(new SkLine { P1 = a.Id, P2 = b.Id }); sk.Add(new SkLine { P1 = b.Id, P2 = c.Id });
        sk.Add(new SkLine { P1 = c.Id, P2 = e.Id }); sk.Add(new SkLine { P1 = e.Id, P2 = a.Id });
        var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
        var block = doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = S(thickness) });
        var hs = new Sketch();
        foreach (var p in holes) { var hp = hs.AddPoint(p); hs.Add(new SkPointEntity { P = hp.Id }); }
        var top = new FaceRef { Name = block.Id + ".End", Hint = new Vec3(50, 30, thickness), Normal = Vec3.UnitZ };
        var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Face = top }, Sketch = hs });
        var hole = doc.AddFeature(drill is { } dd
            ? new HoleFeature { SketchId = s2.Id, Diameter = S(dd), ThroughAll = true }
            : new HoleFeature { SketchId = s2.Id, Standard = "ISO-holes", Fastener = "Socket head cap screw", Size = "M8", Diameter = "9mm", ThroughAll = true });
        LibraryLink.Sync(doc);
        doc.Save(path);
        return (doc, hole);
    }

    /// <summary>The walls of a hole feature's holes in a component's display.</summary>
    static List<string> HoleWalls(AssemblyModel m, Component c, HoleFeature h) =>
        m.DisplayOf(c)!.Mesh.Faces.Where(f => f.Name.StartsWith(h.Id + ".", StringComparison.Ordinal) && f.Surface.Kind == SurfaceKind.Cylinder)
            .Select(f => f.Name).Distinct().ToList();

    /// <summary>A company standards library: SpryCAD's defaults with one table cut to some sizes (by the rows' size).</summary>
    static LoadedLibrary Only(string table, List<string> sizes) => new(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null,
        LibraryFile.Defaults(LibraryDomain.Standards).Select(x => x.Key != table ? x : new LibraryTable
        {
            Key = x.Key, Title = x.Title, Kind = x.Kind, Standard = x.Standard, Units = x.Units, Columns = x.Columns,
            Rows = x.Rows.Where(r => sizes.Contains(r.Text?.GetValueOrDefault(Schemas.SizeColumn) ?? r.Key)).ToList(),
        }).ToList());

    public static void Run(TestRunner t)
    {
        t.Test("Fasteners: family tables well formed (a value per column, each row its size), not marked verified", () =>
        {
            foreach (var tb in BuiltInStandards.Tables.Where(x => x.Kind is TableKinds.Screw or TableKinds.Nut or TableKinds.Washer))
            {
                t.True(!tb.Verified, $"{tb.Key}: not verified");
                t.True(Fasteners.KindOf(tb.Kind) != null, $"{tb.Key}: a fastener kind");
                t.True(tb.Rows.All(r => r.Values.Count == tb.Columns.Count && r.Values.All(v => v > 0) && Fasteners.SizeOf(r) != null), $"{tb.Key}: values and a size");
                t.True(tb.Rows.Select(r => r.Key).Distinct().Count() == tb.Rows.Count, $"{tb.Key}: unique designations");
            }
            var set = LibrarySet.BuiltIn;
            var m8 = set.Snapshot("ISO4762:M8x30")!;
            t.Eq(m8.LibraryName, BuiltInStandards.Name);
            t.Near(m8.Value("d")!.Value, 8, 1e-12); t.Near(m8.Value("l")!.Value, 30, 1e-12); t.Near(m8.Value("b")!.Value, 28, 1e-12);
            t.Eq(Fasteners.SizeOf(m8), "M8");
            t.Eq(Fasteners.Designation(m8), "ISO 4762 M8x30");
            var inch = set.Snapshot("ASME-B18.3-SHCS:1/4-20 x 1")!;
            t.Near(inch.Value("l")!.Value, 1, 1e-12); t.Near(inch.Base("l")!.Value, 25.4, 1e-12); t.Near(inch.Base("p")!.Value, 1.27, 1e-12); // (in inches, read in mm)
            t.Eq(Fasteners.SizeOf(inch), "1/4");
            // the families a fastener holes row names exist, with screws of its size where the family has the size (metric: nuts too)
            foreach (var holes in set.Offered(TableKinds.FastenerHoles))
                foreach (var r in holes.Rows.Where(r => r.Text?.GetValueOrDefault("screws") != null))
                {
                    var screws = r.Text!["screws"]; var nuts = r.Text.GetValueOrDefault("nuts"); var size = r.Keys[1];
                    t.True(set.Table(screws) != null && (nuts == null || set.Table(nuts) != null), $"{holes.Key} {r.Key}: its families");
                    if (!Fasteners.OfferedSizes(set, screws).Contains(size)) continue;
                    t.True(Fasteners.Offered(set, screws, size).Count > 0, $"{size} screws");
                    if (nuts != null) t.True(Fasteners.Offered(set, nuts, size).Count == 1, $"{size} nut");
                }
            var lengths = Fasteners.Offered(set, "ISO4762", "M8").Select(r => r.Values[7]).ToList();
            t.True(lengths.SequenceEqual(lengths.OrderBy(x => x)) && lengths[0] == 12 && lengths[^1] == 80, string.Join(",", lengths));
        });

        t.Test("Fasteners: a company library with the M3-M12 screws only offers those sizes; SpryCAD's defaults offer them all", () =>
        {
            var sizes = new List<string> { "M3", "M4", "M5", "M6", "M8", "M10", "M12" };
            var set = new LibrarySet(new[] { Only("ISO4762", sizes) });
            t.Eq(string.Join(",", Fasteners.OfferedSizes(set, "ISO4762")), string.Join(",", sizes));
            t.Eq(Fasteners.Offered(set, "ISO4762", "M16").Count, 0);
            t.Eq(Fasteners.Offered(set, "ISO4762", "M2").Count, 0);
            t.True(Fasteners.Offered(set, "ISO4762", "M8").Any(r => r.Key == "M8x30"), "M8x30 offered");
            t.True(set.Snapshot("ISO4762:M16x50") == null, "the sizes left out not offered (parts that use one keep its snapshot)");
            t.True(Fasteners.OfferedSizes(LibrarySet.BuiltIn, "ISO4762").Contains("M16"), "M16 in SpryCAD's defaults");
        });

        t.Test("Fasteners: generated bodies the right size, cached by family, values and kernel version", () =>
        {
            Fasteners.ClearCache();
            var set = LibrarySet.BuiltIn;
            var m8 = set.Snapshot("ISO4762:M8x30")!;
            var built = Fasteners.BodiesBuilt;
            var screw = Fasteners.Body(m8);
            t.True(ReferenceEquals(Fasteners.Body(m8), screw), "the same body again");
            t.Eq(Fasteners.BodiesBuilt, built + 1);
            var bb = screw.Bounds;
            t.Near(bb.Min.Z, -30, 0.02); t.Near(bb.Max.Z, 8, 0.02); t.Near(bb.Max.X, 6.5, 0.05);
            var vol = screw.Volume();
            double shank = Math.PI * 16 * 30, head = Math.PI * 6.5 * 6.5 * 8;
            t.True(vol < shank + head && vol > 0.9 * shank + 0.8 * head, $"volume {vol}");
            var names = screw.FaceNames().ToList();
            t.True(names.Contains(Fasteners.ScrewShank) && names.Contains(Fasteners.ScrewSeat), string.Join(",", names));
            var nut = Fasteners.Body(set.Snapshot("ISO4032:M8")!);
            t.Near(nut.Bounds.Max.Z, 6.8, 0.01); t.Near(nut.Bounds.Min.Z, 0, 0.01);
            t.True(nut.FaceNames().Contains(Fasteners.NutBore) && nut.FaceNames().Contains(Fasteners.NutBearing), string.Join(",", nut.FaceNames()));
            var washer = Fasteners.Body(set.Snapshot("ISO7089:M8")!);
            t.Near(washer.Volume(), Math.PI * (64 - 8.4 * 8.4 / 4) * 1.6, 0.03 * washer.Volume());
            // the same values from another library: the same body; other values: another
            var copy = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null,
                new[] { new LibraryTable { Key = "ACME-SHCS", Kind = TableKinds.Screw, Standard = "Acme 100", Columns = Fasteners.ScrewColumns.ToList(),
                    Rows = { new LibraryRow("A8-30", m8.Values.ToArray()), new LibraryRow("A8-31", m8.Values.Take(7).Append(31).ToArray()) } } });
            var acme = new LibrarySet(new[] { copy });
            t.Eq(Fasteners.BodyKey(acme.Snapshot("ACME-SHCS:A8-30")!), Fasteners.BodyKey(m8));
            t.True(ReferenceEquals(Fasteners.Body(acme.Snapshot("ACME-SHCS:A8-30")!), screw), "shared");
            t.True(Fasteners.BodyKey(acme.Snapshot("ACME-SHCS:A8-31")!) != Fasteners.BodyKey(m8), "another length, another body");
        });

        t.Test("Fasteners: a fastener part rebuilds from its snapshot, saved and read back", () =>
        {
            var m8 = LibrarySet.BuiltIn.Snapshot("ISO4762:M8x30")!;
            var part = Fasteners.Part(m8);
            t.Eq(part.Id, Fasteners.Part(m8).Id);
            t.True(!LibraryLink.Sync(part), "its snapshot is the one its feature uses");
            var r = new Regenerator().Rebuild(part);
            t.True(r.Status.Values.All(s => s.Ok), string.Join(" | ", r.Status.Values.Select(s => s.Error)));
            t.Eq(r.State.Bodies.Count, 1);
            var path = Path.Combine(NewDir("part"), "screw.sprt");
            part.Save(path);
            var loaded = CadDocument.Load(path);
            t.Eq(Fasteners.Of(loaded)!.Designation, "ISO 4762 M8x30");
            t.Eq(string.Join(",", loaded.Library!.Keys), "ISO4762:M8x30,Steel:Steel, property class 8.8"); // (its row and its grade's)
        });

        t.Test("Insert Fastener: a bolt through two plates, length rounded up to a standard one, nut and washer mated; BOM by designation", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var dir = NewDir("stack");
                var (topDoc, topHole) = Plate(Path.Combine(dir, "top.sprt"), 10, new[] { new Vec2(20, 20) });
                var (botDoc, _) = Plate(Path.Combine(dir, "bottom.sprt"), 5, new[] { new Vec2(20, 20) });
                var m = AssemblyModel.New();
                var bottom = m.Insert(Path.Combine(dir, "bottom.sprt"), Transform.Identity);
                var top = m.Insert(Path.Combine(dir, "top.sprt"), Transform.Translation(new Vec3(0, 0, 5)));
                top.Fixed = true;
                var wall = HoleWalls(m, top, topHole).Single();
                var plan = m.PlanFastener(top.Id, wall);
                t.Eq(plan.End, FastenerEnd.Nut);
                t.Near(plan.Clamped, 15, 0.01);
                t.Near(plan.Needed, 15 + 1.6 + 6.8 + 2 * 1.25, 0.01);
                t.Eq(plan.Screw?.Key, "M8x30");
                t.Near(plan.Seat.Z, 15, 0.01); t.Near(plan.Inward.Z, -1, 1e-6);
                t.Eq(plan.SeatFace.component, top.Id); t.Eq(plan.FarFace.component, bottom.Id);
                var ins = m.InsertFastener(plan);
                t.True(ins.Nut != null && ins.Washers.Count == 1, "a nut and a washer");
                t.Eq(ins.Mates.Count, 6);
                t.Eq(ins.Designation, "ISO 4762 M8x30");
                // the mates hold as placed: solving moves nothing
                var screwAt = ins.Screw.Placement.T;
                var solved = m.SolveMates();
                t.True(Vec3.Distance(ins.Screw.Placement.T, screwAt) < 1e-6, $"screw moved to {ins.Screw.Placement.T}");
                t.Near(ins.Screw.Placement.T.Z, 15, 1e-6);
                t.Near(ins.Nut!.Placement.T.Z, -1.6, 1e-6);
                // the screw's tip past the nut
                var tip = ins.Screw.Placement.ToTransform().Point(new Vec3(0, 0, -30));
                t.True(tip.Z < -1.6 - 6.8, $"tip at {tip.Z}");
                // the BOM names the fasteners by standard and designation
                var bom = Bom.Build(m, BomKind.PartsOnly);
                var names = bom.Select(r => r.Name + " x" + r.Quantity).ToList();
                t.True(names.Contains("ISO 4762 M8x30 x1") && names.Contains("ISO 4032 M8 x1") && names.Contains("ISO 7089 M8 x1"), string.Join(" | ", names));
                t.True(bom.Single(r => r.Name.StartsWith("ISO 4762")).File == null, "no file name");
                // the embedded screw keeps its row
                var screwPart = m.PartDocument(ins.Screw)!;
                t.Eq(screwPart.Library!["ISO4762:M8x30"].LibraryId, BuiltInStandards.LibraryId);
                t.True(bom.All(r => r.PartNumber == null), "SpryCAD's rows give no part numbers");
                // a company's screws with its part numbers: listed beside the designation
                var acme = LibraryFile.Defaults(LibraryDomain.Standards).ToList();
                foreach (var r in acme.Single(x => x.Key == "ISO4762").Rows) (r.Text ??= new(StringComparer.Ordinal))[Schemas.PartNumberColumn] = "ACME-" + r.Key;
                LibraryLink.Current = new LibrarySet(new[] { new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryDomain.Standards, Guid.NewGuid(), null, acme) });
                var m3 = AssemblyModel.New();
                m3.Insert(Path.Combine(dir, "bottom.sprt"), Transform.Identity);
                var top3 = m3.Insert(Path.Combine(dir, "top.sprt"), Transform.Translation(new Vec3(0, 0, 5)));
                top3.Fixed = true;
                m3.InsertFastener(m3.PlanFastener(top3.Id, HoleWalls(m3, top3, topHole).Single()));
                var bom3 = Bom.Build(m3, BomKind.PartsOnly);
                t.Eq(bom3.Single(r => r.Name == "ISO 4762 M8x30").PartNumber, "ACME-M8x30");
                t.True(bom3.Single(r => r.Name == "ISO 4032 M8").PartNumber == null, "a nut without one: none");
                LibraryLink.Current = LibrarySet.BuiltIn;

                // into a tapped plate instead: clamped is the top plate, plus 1.5 d of thread
                var (tapDoc, _) = Plate(Path.Combine(dir, "tapped.sprt"), 15, new[] { new Vec2(20, 20) }, drill: 6.8);
                var m2 = AssemblyModel.New();
                var tapped = m2.Insert(Path.Combine(dir, "tapped.sprt"), Transform.Identity);
                var top2 = m2.Insert(Path.Combine(dir, "top.sprt"), Transform.Translation(new Vec3(0, 0, 15)));
                var plan2 = m2.PlanFastener(top2.Id, HoleWalls(m2, top2, topHole).Single());
                t.Eq(plan2.End, FastenerEnd.Tapped);
                t.Eq(plan2.Tapped, tapped.Id);
                t.Near(plan2.Clamped, 10, 0.01);
                t.Eq(plan2.Screw?.Key, "M8x25");
                t.Eq(plan2.Notes.Count, 0);
                var ins2 = m2.InsertFastener(plan2);
                t.True(ins2.Nut == null && ins2.Washers.Count == 0, "no nut");
                t.Eq(ins2.Mates.Count, 2);

                // a company library with the M3-M6 screws only: the M8 hole has no screw to offer
                bool refused = false;
                try { m2.PlanFastener(top2.Id, HoleWalls(m2, top2, topHole).Single(), new FastenerOptions { Set = new LibrarySet(new[] { Only("ISO4762", new() { "M3", "M4", "M5", "M6" }) }) }); }
                catch (InvalidOperationException) { refused = true; }
                t.True(refused, "no M8 screws offered");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Insert Fastener: 200 M8x30 bolts across parts share one embedded part, one snapshot blob and one cached body", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                Fasteners.ClearCache();
                var dir = NewDir("many");
                var points = Enumerable.Range(0, 10).Select(i => new Vec2(10 + 20 * (i % 5), 15 + 30 * (i / 5))).ToList();
                var (_, topHole) = Plate(Path.Combine(dir, "top.sprt"), 10, points);
                Plate(Path.Combine(dir, "bottom.sprt"), 5, points);
                var m = AssemblyModel.New();
                var tops = new List<Component>();
                for (int i = 0; i < 20; i++)
                {
                    var at = new Vec3(150 * i, 0, 0);
                    m.Insert(Path.Combine(dir, "bottom.sprt"), Transform.Translation(at));
                    tops.Add(m.Insert(Path.Combine(dir, "top.sprt"), Transform.Translation(at + new Vec3(0, 0, 5))));
                }
                var built = Fasteners.BodiesBuilt;
                var screws = new List<Component>();
                foreach (var top in tops)
                    foreach (var wall in HoleWalls(m, top, topHole))
                        screws.Add(m.InsertFastener(top.Id, wall).Screw);
                t.Eq(screws.Count, 200);
                t.True(screws.All(c => c.Name.StartsWith("ISO 4762 M8x30<", StringComparison.Ordinal)), string.Join(",", screws.Select(c => c.Name).Distinct().Take(5)));
                t.Eq(screws.Select(c => c.Part.Embedded).Distinct().Count(), 1);
                t.True(screws.Select(m.DisplayOf).Distinct().Count() == 1, "one display");
                t.Eq(Fasteners.BodiesBuilt - built, 3);
                t.Eq(Fasteners.BodiesCached, 3);
                t.Eq(m.Doc.Files.Keys.Count(k => k.StartsWith("parts/", StringComparison.Ordinal)), 3);
                var bom = Bom.Build(m, BomKind.PartsOnly);
                t.Eq(bom.Single(r => r.Name == "ISO 4762 M8x30").Quantity, 200);
                // saved: the row's snapshot is one object of one embedded part, one blob
                var path = Path.Combine(dir, "frame.sasm");
                m.Save(path);
                using var db = PartDatabase.Open(path);
                var oid = PartObjects.IdOf(ObjectKind.LibrarySnapshot, "ISO4762:M8x30");
                var rows = db.Query("SELECT Hash FROM RevisionObjects WHERE ObjectId = $o", r => Convert.ToHexString((byte[])r[0]), ("$o", oid.ToArray()));
                t.Eq(rows.Count, 1);
                t.Eq(db.Query("SELECT COUNT(*) FROM Blobs WHERE Hash = $h", r => r.GetInt64(0), ("$h", Convert.FromHexString(rows[0])))[0], 1L);
                // and opened again, the bolts are still the one part
                var again = AssemblyModel.Open(AssemblyDocument.Load(path), path);
                var reopened = again.Doc.Components.Where(c => c.Name.StartsWith("ISO 4762 M8x30<", StringComparison.Ordinal)).ToList();
                t.Eq(reopened.Count, 200);
                t.Eq(reopened.Select(again.DisplayOf).Distinct().Count(), 1);
            }
            finally { LibraryLink.Current = before; }
        });
    }
}

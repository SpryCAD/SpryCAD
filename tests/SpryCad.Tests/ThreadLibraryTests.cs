using ClosedXML.Excel;
using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Thread tables (docs/library-v2.md): the formulas SpryCAD's are filled by, and Inventor's spreadsheets read as tables.</summary>
static class ThreadLibraryTests
{
    /// <summary>An installed Inventor's spreadsheet (thread.xlsx, clearance.xlsx); null where Inventor is not installed.</summary>
    static string? InventorFile(string name)
    {
        var root = @"C:\Users\Public\Documents\Autodesk";
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "Inventor*").OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "Design Data", "XLS", "en-US", name)).FirstOrDefault(File.Exists);
    }

    // (read while Excel has it open too)
    static InventorWorkbook.Result Import(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return InventorWorkbook.Read(fs);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Thread formulas: Unified 2A/2B limits and tap drills as ASME B1.1 tabulates them; ISO 6g/6H from the basic profile and the g deviation", () =>
        {
            void Check(string name, Dictionary<string, double> v, params (string path, double value)[] expected)
            {
                foreach (var (path, value) in expected)
                    t.True(v.TryGetValue(path, out var x) && Math.Abs(x - value) < 1e-9, $"{name} {path}: {(v.TryGetValue(path, out var y) ? y : double.NaN)}, expected {value}");
            }
            var (q, qt) = ThreadFormulas.Unified(0.25, 1 / 20.0);
            Check("1/4-20", q, ("external.major.max", 0.2489), ("external.major.min", 0.2408), ("external.pitch.max", 0.2164), ("external.pitch.min", 0.2127),
                ("external.minor.max", 0.1894), ("internal.minor.min", 0.196), ("internal.minor.max", 0.207), ("internal.pitch.min", 0.2175),
                ("internal.pitch.max", 0.2224), ("internal.major.min", 0.25), ("tap.diameter", 0.201));
            t.Eq(qt["tap.size"], "#7"); t.Eq(qt["internal.class"], "2B");
            var (h, ht) = ThreadFormulas.Unified(0.5, 1 / 13.0);
            Check("1/2-13", h, ("external.major.max", 0.4985), ("external.pitch.min", 0.4435), ("internal.minor.max", 0.434), ("internal.pitch.max", 0.4565), ("tap.diameter", 27 / 64.0));
            t.Eq(ht["tap.size"], "27/64");
            var (one, _) = ThreadFormulas.Unified(1, 1 / 8.0);
            Check("1-8", one, ("external.major.min", 0.983), ("external.pitch.max", 0.9168), ("internal.minor.min", 0.865), ("internal.pitch.max", 0.9276), ("tap.diameter", 0.875));
            var (m8, m8t) = ThreadFormulas.IsoMetric(8, 1.25);
            Check("M8", m8, ("external.major.max", 7.972), ("external.pitch.max", 7.16), ("external.minor.max", 6.619),
                ("internal.minor.min", 6.647), ("internal.pitch.min", 7.188), ("internal.major.min", 8), ("tap.diameter", 6.75));
            t.True(!m8.ContainsKey("external.major.min") && !m8.ContainsKey("internal.pitch.max"), "the limits ISO 965-1's tables give left out");
            t.Eq(m8t["internal.class"], "6H");
            // SpryCAD's own tables are filled by them
            var unc = LibrarySet.BuiltIn.Snapshot("ASME-B1.1-UNC:1/4-20 UNC:2A")!;
            t.Eq(unc.Value("internal.pitch.max"), 0.2224); t.Eq(unc.Text?["tap.size"], "#7");
            t.Near(unc.Base("tap.diameter")!.Value, 0.201 * 25.4, 1e-12);
            var iso = LibrarySet.BuiltIn.Snapshot("ISO261-coarse:M8:6g")!;
            t.Eq(iso.Value("external.pitch.max"), 7.16); t.Eq(iso.Value("tap.diameter"), 6.75); t.Eq(iso.Text?["internal.class"], "6H");
        });

        t.Test("Thread formulas against an installed Inventor's thread.xlsx: every UNC/UNF limit and tap drill to 1 inch, the ISO limits of SpryCAD's sizes (skipped without Inventor)", () =>
        {
            if (InventorFile("thread.xlsx") is not { } path) { Console.WriteLine("    (Inventor's thread.xlsx not found: skipped)"); return; }
            var tables = Import(path).Tables;
            var cells = 0; var differ = new List<string>();
            void Compare(LibraryTable tb, LibraryRow r, Dictionary<string, double> mine, double tol)
            {
                foreach (var (col, v) in mine)
                {
                    if (tb.Column(col) is var i && i < 0 || !double.IsFinite(r.Values[i])) continue;
                    cells++;
                    // (Inventor's cells hold more digits than they show: compared as shown, to one in the last place,
                    // where intermediate rounding differs: the UNR minor diameter, 5/8-11's allowance)
                    if (Math.Abs(r.Values[i] - v) > tol) differ.Add($"{r.Key} {col}: Inventor {r.Values[i]}, SpryCAD {v}");
                }
            }
            var un = tables.Single(x => x.Key == "ANSI-Unified-Screw-Threads");
            // (the UNC and UNF series to 1 in: Inventor also lists 13/16 and 15/16 as UNC, which B1.1's series do not have)
            foreach (var r in un.Rows.Where(r => (r.Keys[0].EndsWith(" UNC") || r.Keys[0].EndsWith(" UNF")) && r.Values[un.Column("major")] <= 1
                && !r.Keys[0].StartsWith("13/16") && !r.Keys[0].StartsWith("15/16") && r.Keys[1] is "2A" or "3A"))
                Compare(un, r, ThreadFormulas.Unified(r.Values[un.Column("major")], r.Values[un.Column("pitch")], r.Keys[1]).values, 1.5e-4);
            var ours = BuiltInStandards.Tables.Where(x => x.Key.StartsWith("ISO261", StringComparison.Ordinal))
                .SelectMany(x => x.Rows.Select(r => $"M{r.Values[x.Column("major")]}x{r.Values[x.Column("pitch")]}")).ToHashSet();
            var isoTable = tables.Single(x => x.Key == "ISO-Metric-profile");
            foreach (var r in isoTable.Rows.Where(r => ours.Contains(r.Keys[0]) && r.Keys[1] == "6g"))
                Compare(isoTable, r, ThreadFormulas.IsoMetric(r.Values[isoTable.Column("major")], r.Values[isoTable.Column("pitch")]).values, 1.5e-3);
            Console.WriteLine($"    {cells} cells compared, {differ.Count} differ" + (differ.Count > 0 ? ":\n      " + string.Join("\n      ", differ) : ""));
            // (what differs: Inventor's misplaced digits and M33x3.5's deviation (it has -32 µm for -53), 5/8-11's UNR
            // minor diameter and 7/8-14's 2B minor diameter a step apart)
            t.True(cells > 400 && differ.Count <= 10, $"{differ.Count} of {cells} differ");
        });

        t.Test("Auto threads from the library: a 1/4 in shaft takes 1/4-20 from UNC, a hole its size by the tap drill; the part keeps the table's rows and builds the same without the library", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                (CadDocument doc, ThreadFeature th) Shaft(double r, string? standard)
                {
                    var doc = new CadDocument();
                    var sk = new SpryCad.Core.Sketching.Sketch();
                    sk.Add(new SpryCad.Core.Sketching.SkCircle { Center = sk.Point(SpryCad.Core.Sketching.Sketch.OriginId)!.Id, R = r });
                    var s = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = sk });
                    doc.AddFeature(new ExtrudeFeature { SketchId = s.Id, Depth = "20" });
                    var cyl = new Regenerator().Rebuild(doc).State.Bodies[0].Solid.Native!.Faces.First(f => f.Surface.Kind == SurfaceKind.Cylinder);
                    var th = doc.AddFeature(new ThreadFeature { Face = new FaceRef { Name = cyl.Tag.Name, Hint = new Vec3(r, 0, 19) }, Standard = standard, End = ThreadEnd.ThroughAll, Cosmetic = true, Chamfer = false });
                    return (doc, th);
                }
                var (doc, th) = Shaft(3.175, "ASME-B1.1-UNC");
                t.True(LibraryLink.Sync(doc), "snapshots added");
                t.Eq(doc.Library!.Count, LibrarySet.BuiltIn.Table("ASME-B1.1-UNC")!.Rows.Count);
                var r1 = new Regenerator().Rebuild(doc);
                t.True(r1.Status[th.Id].Ok, r1.Status[th.Id].Error ?? "");
                t.Near(r1.State.Cosmetics[th.Id].Lead, 1.27, 1e-12);
                // its callout: the size it took and its class (external)
                t.Eq(r1.Status[th.Id].Callout, "1/4-20 UNC-2A");
                t.Eq(r1.State.Notes[th.Id].Face, th.Face.Name);
                // no thread tables in the libraries: the part's own rows; still kept by Sync
                var other = new LoadedLibrary(Guid.NewGuid(), "Other", LibraryDomain.Standards, Guid.NewGuid(), null,
                    new[] { new LibraryTable { Key = "ISO-metric-fine", Kind = TableKinds.Thread, Columns = { "major", "pitch" }, Rows = { new LibraryRow("M6x0.5", 6, 0.5) } } });
                LibraryLink.Current = new LibrarySet(new[] { other });
                t.True(!LibraryLink.Sync(doc), "the part's rows kept");
                t.Near(new Regenerator().Rebuild(doc).State.Cosmetics[th.Id].Lead, 1.27, 1e-12);
                // a metric shaft without a standard: ISO coarse, M8
                LibraryLink.Current = LibrarySet.BuiltIn;
                var (m, mt) = Shaft(4, null);
                t.Near(new Regenerator().Rebuild(m).State.Cosmetics[mt.Id].Lead, 1.25, 1e-12);
                // a hole by its tap drill: 6.75 is M8's (its basic minor diameter, 6.647, is nearer M8 too; 6.8 is not M10's 8.5)
                t.Eq(ThreadSizes.Fit(new CadDocument(), "ISO261-coarse", 6.8, external: false)?.name, "M8");
                t.Eq(ThreadSizes.Fit(new CadDocument(), "ASME-B1.1-UNC", 0.201 * 25.4, external: false)?.name, "1/4-20 UNC");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Tapped holes: drilled with the thread's tap drill (ISO M8: 6.75; 1/4-20: #7), threaded through the part or to a thread depth (shown cosmetic), the thread's row a snapshot of the part; a diameter typed over kept", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                // (a plate 10 thick both ways from the Front plane, two holes from its top face)
                (CadDocument doc, HoleFeature h) Part(Action<HoleFeature> set)
                {
                    var doc = new CadDocument();
                    var s1 = doc.AddFeature(new SketchFeature { Plane = PlaneRef.Front, Sketch = CoreTests.Rect(100, 60) });
                    doc.AddFeature(new ExtrudeFeature { SketchId = s1.Id, Depth = "10", End = EndCondition.MidPlane });
                    var top = doc.AddFeature(new PlaneFeature { Base = PlaneRef.Front, Offset = "5" });
                    var sk = new Sketch();
                    sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(20, 10)).Id });
                    sk.Add(new SkPointEntity { P = sk.AddPoint(new Vec2(40, 20)).Id });
                    var s2 = doc.AddFeature(new SketchFeature { Plane = new PlaneRef { Feature = top.Id }, Sketch = sk });
                    var h = new HoleFeature { SketchId = s2.Id, End = HoleEnd.ThroughAll, Type = HoleType.Tapped, ThreadSize = "M8" };
                    set(h);
                    doc.AddFeature(h);
                    LibraryLink.Sync(doc);
                    return (doc, h);
                }
                static double Vol(RegenResult r) => r.State.Bodies.Sum(b => b.Mesh.Volume);
                var (plain, _) = Part(h => h.Type = HoleType.Simple);
                plain.Features.RemoveAll(x => x is HoleFeature);
                var solid = Vol(new Regenerator().Rebuild(plain));
                var (doc, hole) = Part(_ => { });
                t.Eq(string.Join(",", hole.LibraryItems()), "ISO261-coarse:M8:6g");
                t.True(doc.Library!.ContainsKey("ISO261-coarse:M8:6g"), "the thread's row kept by the part");
                var r = new Regenerator().Rebuild(doc);
                t.True(r.Status[hole.Id].Ok, r.Status[hole.Id].Error ?? "");
                // its callout: the tap drill through, the thread (its internal class) through; its note the thread's row
                t.Eq(r.Status[hole.Id].Callout, "2X ⌀6.75 THRU ALL\nM8-6H THRU ALL");
                t.Eq(r.State.Notes[hole.Id].ThreadItem, "ISO261-coarse:M8:6g");
                var removed = solid - Vol(r);
                var drill = 2 * Math.PI * 6.75 * 6.75 / 4 * 10;
                t.Near(removed, drill, drill * 0.01);
                var threads = r.State.Cosmetics.Where(kv => kv.Key.StartsWith(hole.Id + ".T", StringComparison.Ordinal)).Select(kv => kv.Value).ToList();
                t.Eq(threads.Count, 2);
                t.True(threads.All(c => !c.External && Math.Abs(c.Lead - 1.25) < 1e-12 && Math.Abs(c.Radius - 3.375) < 1e-9 && Math.Abs(c.Length - 10) < 1e-6),
                    string.Join("; ", threads.Select(c => $"{c.Lead} {c.Radius} {c.Length}")));
                // to a thread depth; a blind hole threaded deeper than it goes: as deep as it goes, said
                hole.ThreadDepth = "6";
                var r6 = new Regenerator().Rebuild(doc);
                t.Near(r6.State.Cosmetics[hole.Id + ".T0"].Length, 6, 1e-9);
                t.Eq(r6.Status[hole.Id].Callout, "2X ⌀6.75 THRU ALL\nM8-6H ↧ 6");
                hole.End = HoleEnd.Blind; hole.Depth = "4"; hole.ThreadDepth = "6";
                var rb = new Regenerator().Rebuild(doc);
                t.Near(rb.State.Cosmetics[hole.Id + ".T0"].Length, 4, 1e-9);
                t.True(rb.Status[hole.Id].Warnings.Any(w => w.Contains("deeper than the hole")), "said");
                // the thread cut, not cosmetic: a groove from the drill out to the major diameter at each hole (through)
                hole.End = HoleEnd.ThroughAll; hole.ThreadDepth = ""; hole.ThreadCosmetic = false;
                var rc = new Regenerator().Rebuild(doc);
                t.True(rc.Status[hole.Id].Ok, rc.Status[hole.Id].Error ?? "");
                t.True(!rc.State.Cosmetics.Keys.Any(x => x.StartsWith(hole.Id + ".T", StringComparison.Ordinal)), "no helix drawn");
                var cut = solid - Vol(rc);
                var majorCyl = 2 * Math.PI * 8 * 8 / 4 * 10;
                t.True(cut > drill * 1.05 && cut < majorCyl, $"{cut} between {drill} and {majorCyl}");
                // blind, threaded short of the bottom: still a valid cut
                hole.End = HoleEnd.Blind; hole.Depth = "8"; hole.ThreadDepth = "6";
                var rb2 = new Regenerator().Rebuild(doc);
                t.True(rb2.Status[hole.Id].Ok, rb2.Status[hole.Id].Error ?? "");
                // View > Threads: all cosmetic draws it again
                var rv = new Regenerator { ThreadOverride = ThreadDisplay.Cosmetic }.Rebuild(doc);
                t.True(rv.State.Cosmetics.ContainsKey(hole.Id + ".T0"), "the view's cosmetic override");
                hole.ThreadCosmetic = true;
                // a diameter typed over the tap drill: kept
                hole.Overrides = new() { ["Diameter"] = "6.8mm" };
                t.Eq(HoleSizes.Effective(hole, i => LibraryLink.Resolve(doc, i)).Diameter, "6.8mm");
                // an inch thread
                var (inch, ih) = Part(h => { h.ThreadStandard = "ASME-B1.1-UNC"; h.ThreadSize = "1/4-20 UNC"; });
                t.Eq(HoleSizes.Effective(ih, i => LibraryLink.Resolve(inch, i)).Diameter, SpryCad.Core.Expr.Format(Math.Round(0.201 * 25.4, 4)) + "mm");
                var ri = new Regenerator().Rebuild(inch);
                t.Near(ri.State.Cosmetics[ih.Id + ".T1"].Lead, 1.27, 1e-12);
                t.True(ri.Status[ih.Id].Callout!.EndsWith("\n1/4-20 UNC-2B THRU ALL"), ri.Status[ih.Id].Callout!);
                ih.ThreadClass = "3A";
                t.True(new Regenerator().Rebuild(inch).Status[ih.Id].Callout!.EndsWith("\n1/4-20 UNC-3B THRU ALL"), "3B");
                // no size: said
                var (none, nh) = Part(h => h.ThreadSize = null);
                t.True(new Regenerator().Rebuild(none).Status[nh.Id].Error?.Contains("thread size") == true, "no size said");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Thread classes: a row per size and class (Inventor's way); SpryCAD's UNC/UNF 2A/2B and 3A/3B, ISO 6g/6H; a thread names its class (the size's first where it names none)", () =>
        {
            t.Eq(string.Join(",", ThreadSizes.Classes(LibrarySet.BuiltIn, "ASME-B1.1-UNC", "1/4-20 UNC").Select(c => c.Pair)), "2A / 2B,3A / 3B");
            t.Eq(string.Join(",", ThreadSizes.Classes(LibrarySet.BuiltIn, "ISO261-coarse", "M8").Select(c => c.Pair)), "6g / 6H");
            var three = LibrarySet.BuiltIn.Snapshot(ThreadSizes.ItemOf("ASME-B1.1-UNC", "1/4-20 UNC", "3A"))!;
            t.Eq($"{three.Value("external.major.max")} {three.Value("external.pitch.min")} {three.Value("internal.minor.max")} {three.Value("internal.pitch.max")}", "0.25 0.2147 0.2067 0.2211");
            t.Eq(three.Text!["internal.class"], "3B");
            var th = new ThreadFeature { Standard = "ASME-B1.1-UNC", Size = "1/4-20 UNC" };
            t.Eq(string.Join(",", th.LibraryItems()), "ASME-B1.1-UNC:1/4-20 UNC:2A");
            th.Class = "3A";
            t.Eq(string.Join(",", th.LibraryItems()), "ASME-B1.1-UNC:1/4-20 UNC:3A");
            // Auto: each size's first class, or the class it names
            t.Eq(ThreadSizes.Fit(new CadDocument(), "ASME-B1.1-UNC", 6.35, external: true)?.cls, "2A");
            t.Eq(ThreadSizes.Fit(new CadDocument(), "ASME-B1.1-UNC", 6.35, external: true, "3A")?.cls, "3A");
        });

        t.Test("Parts from before thread classes: a thread's snapshot (its row its designation alone) given its class, the thread and a tapped hole naming it", () =>
        {
            var before = LibraryLink.Current;
            try
            {
                LibraryLink.Current = LibrarySet.BuiltIn;
                var doc = new CadDocument();
                var unc = LibrarySet.BuiltIn.Snapshot(ThreadSizes.ItemOf("ASME-B1.1-UNC", "1/4-20 UNC", "2A"))!;
                var old = new LibrarySnapshot { Table = unc.Table, Row = "1/4-20 UNC", Kind = unc.Kind, Units = unc.Units, Columns = { "major", "pitch" }, Values = { 0.25, 0.05 } };
                doc.Library = new(StringComparer.Ordinal) { ["ASME-B1.1-UNC:1/4-20 UNC"] = old };
                doc.Features.Add(new ThreadFeature { Id = "T1", Standard = "ASME-B1.1-UNC", Size = "1/4-20 UNC" });
                doc.Features.Add(new HoleFeature { Id = "H1", Type = HoleType.Tapped, ThreadSize = "M8" });
                var back = CadDocument.FromJson(doc.ToJson());
                t.Eq(string.Join(",", back.Library!.Keys), "ASME-B1.1-UNC:1/4-20 UNC:2A");
                t.Eq(back.Features.OfType<ThreadFeature>().Single().Class, "2A");
                t.Eq(back.Features.OfType<HoleFeature>().Single().ThreadClass, "6g");
            }
            finally { LibraryLink.Current = before; }
        });

        t.Test("Inventor import: a thread sheet as a thread table, a row per designation and class (TPI as the pitch), a taper sheet, a clearance sheet as fastener holes (head type 1 a counterbore, 2 a countersink); other sheets said", () =>
        {
            var wb = new XLWorkbook();
            var th = wb.AddWorksheet("ANSI Unified Screw Threads");
            th.Cell(1, 1).SetValue("I Q"); th.Cell(1, 2).SetValue("ANSI Unified Screw Threads"); th.Cell(2, 21).SetValue("Tap Drill"); th.Cell(2, 24).SetValue("Inch");
            var heads = new[] { "Size", "Size Suffix", "Thread Designation", "Custom Thread Designation", "TPI", "Class", "Max", "Min", "Max", "Min", "Max", "Min", "", "Class", "Min", "Max", "Min", "Max", "Min", "Max", "Basic" };
            for (int i = 0; i < heads.Length; i++) th.Cell(3, i + 1).SetValue(heads[i]);
            void Row(int r, params object[] cells) { for (int i = 0; i < cells.Length; i++) if (cells[i] is double d) th.Cell(r, i + 1).SetValue(d); else if (cells[i] is string s && s.Length > 0) th.Cell(r, i + 1).SetValue(s); }
            Row(4, 0.25, "", "1/4-20 UNC", "1/4-20 UNC", 20.0, "2A", 0.2489, 0.2408, 0.2164, 0.2127, 0.1894, "", "", "2B", 0.196, 0.207, 0.2175, 0.2224, 0.25, "", 0.201);
            Row(5, 0.25, "", "1/4-20 UNC", "1/4-20 UNC", 20.0, "3A", 0.25, 0.2419, 0.2175, 0.2147, 0.1905, "", "", "3B", 0.196, 0.2067, 0.2175, 0.2211, 0.25, "", 0.201);
            Row(6, 0.3125, "", "5/16-18 UNC", "5/16-18 UNC", 18.0, "2A", 0.3113, 0.3026, 0.2752, 0.2712, 0.2464, "", "", "2B", 0.252, 0.265, 0.2764, 0.2817, 0.3125, "", 0.257);
            var tp = wb.AddWorksheet("NPT");
            tp.Cell(1, 1).SetValue("I T"); tp.Cell(1, 2).SetValue("NPT"); tp.Cell(1, 22).SetValue("inch-taper");
            tp.Cell(2, 1).SetValue("Thread Designation (Nominal Pipe Size)");
            var npt = new object[] { "1/8", "1/8 - 27 NPT", 0.405, 27.0, 1 / 27.0, 0.36351, 0.1615, 0.3736, 0.2639, 0.38, 0.1111, 0.35656, 0.1285, 0.3924, 0.1898, 0.37537, 0.02963, 0.3339, 0.313, 3.5798, 0.2726 };
            for (int i = 0; i < npt.Length; i++) if (npt[i] is double d) tp.Cell(4, i + 1).SetValue(d); else tp.Cell(4, i + 1).SetValue((string)npt[i]);
            var cl = wb.AddWorksheet("ISO");
            cl.Cell(1, 1).SetValue("M"); cl.Cell(2, 1).SetValue("Fastener Type");
            object[][] holes =
            {
                new object[] { "Socket Head Cap Screw ISO 4762", "M8", 8.4, 9.0, 10.0, 15.0, 8.6, 1.0 },
                new object[] { "Countersunk Flat Head Screw ISO 2009/7046", "M8", 8.4, 9.0, 10.0, 16.4, 90.0, 2.0 },
                new object[] { "Hex Head Bolt", "M8", 8.4, 9.0, 10.0, 0.0, 0.0, 0.0 },
            };
            for (int r = 0; r < holes.Length; r++)
                for (int i = 0; i < holes[r].Length; i++) if (holes[r][i] is double d) cl.Cell(4 + r, i + 1).SetValue(d); else cl.Cell(4 + r, i + 1).SetValue((string)holes[r][i]);
            wb.AddWorksheet("Notes").Cell(1, 1).SetValue("anything");
            var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;

            var (tables, notes) = InventorWorkbook.Read(ms);
            t.Eq(string.Join(",", tables.Select(x => $"{x.Key}:{x.Kind}:{x.Units}")),
                "ANSI-Unified-Screw-Threads:thread:Inch,NPT:taper-thread:Inch,ISO-holes:fastener-holes:Metric");
            var un = tables[0];
            t.Eq(string.Join(",", un.Rows.Select(r => r.Key)), "1/4-20 UNC:2A,1/4-20 UNC:3A,5/16-18 UNC:2A");
            var q = un.Rows[0];
            t.Eq(q.Values[un.Column("pitch")], 0.05); t.Eq(q.Values[un.Column("internal.pitch.max")], 0.2224); t.Eq(q.Values[un.Column("tap.diameter")], 0.201);
            t.Eq(q.Text!["internal.class"], "2B");
            t.True(un.Column("external.minor.min") < 0 && un.Column("internal.major.max") < 0, "columns no row gives left out");
            t.Eq(un.Rows[1].Text!["internal.class"], "3B");
            t.Near(tables[1].Rows.Single().Values[tables[1].Column("taper")], 3.5798, 1e-12);
            var iso = tables[2];
            t.Eq(string.Join(",", iso.Rows.Select(r => r.Text?.GetValueOrDefault("head") ?? "-")), "counterbore,countersink,-");
            t.Eq(iso.Rows[0].Values[iso.Column("head.depth")], 8.6); t.Eq(iso.Rows[1].Values[iso.Column("head.angle")], 90.0);
            t.True(double.IsNaN(iso.Rows[2].Values[iso.Column("head.diameter")]), "no head: no pocket");
            t.True(HoleSizes.Suits(SpryCad.Core.Model.HoleType.Counterbore, iso, iso.Rows[0]) && HoleSizes.Suits(SpryCad.Core.Model.HoleType.Countersink, iso, iso.Rows[1]), "suits the Hole Wizard's types");
            t.True(notes.Any(n => n.StartsWith("Sheet Notes")), string.Join(" | ", notes));
            foreach (var x in tables) t.True(Schemas.Check(x) == null, Schemas.Check(x) ?? "");
        });

        t.Test("Inventor import of an installed Inventor's thread.xlsx and clearance.xlsx: every sheet read, published and read back (skipped without Inventor)", () =>
        {
            if (InventorFile("thread.xlsx") is not { } threads || InventorFile("clearance.xlsx") is not { } clearance) { Console.WriteLine("    (Inventor's spreadsheets not found: skipped)"); return; }
            var a = Import(threads); var b = Import(clearance);
            var all = a.Tables.Concat(b.Tables).ToList();
            Console.WriteLine($"    {all.Count} tables, {all.Sum(x => x.Rows.Count)} rows; notes: {a.Notes.Count + b.Notes.Count}");
            t.True(a.Tables.Any(x => x.Key == "ISO-Metric-profile" && x.Row("M8x1.25:6g") != null), "ISO M8x1.25");
            t.True(a.Tables.Any(x => x.Kind == TableKinds.TaperThread && x.Key == "NPT"), "NPT");
            t.True(b.Tables.Any(x => x.Key == "ISO-holes" && x.Rows.Any(r => r.Keys[0].StartsWith("Socket Head Cap Screw") && r.Keys[1] == "M8")), "ISO socket head M8 holes");
            var path = Path.Combine(Path.GetTempPath(), "sprycad-tests", $"inventor-{Guid.NewGuid():N}.sprlib");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            LibraryFile.Publish(path, Guid.NewGuid(), "Inventor", LibraryDomain.Standards, all, "admin");
            var back = LibraryFile.Read(path);
            t.Eq(back.Tables.Count, all.Count);
            t.Eq(back.Tables.Sum(x => x.Rows.Count), all.Sum(x => x.Rows.Count));
        });
    }
}

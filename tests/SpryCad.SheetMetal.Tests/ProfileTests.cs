using SpryCad.Core.Library;

namespace SpryCad.SheetMetal.Tests;

/// <summary>Shop profiles (sheet metal spec section 3): lookups, explicit misses, the starter profile, CSV and libraries.</summary>
static class ProfileTests
{
    static ShopProfileTable Tooling(params ProfileRow[] rows)
    {
        var t = ProfileTables.Empty(ProfileTables.Tooling);
        t.Rows.AddRange(rows);
        return t;
    }

    static ProfileRow Tool(string m, string g, string set, string isDefault, string radius) =>
        new(new[] { m, g, set }, isDefault, "1", "88", "10", "88", "1", radius, "5", "");

    static ShopProfile With(params ShopProfileTable[] replace)
    {
        var tables = StarterProfile.Tables.ToDictionary(x => x.Key, x => x.Clone());
        foreach (var r in replace) tables[r.Key] = r;
        return new ShopProfile("Test", tables.Values);
    }

    public static void Run(TestRunner t)
    {
        t.Test("Profile: the starter profile is well formed and resolves its materials", () =>
        {
            t.Eq(string.Join("\n", StarterProfile.Profile.Problems()), "");
            var steel = SheetRules.Resolve(StarterProfile.Profile, "Steel CRS", "16 ga");
            t.Near(steel.Thickness, 0.0598 * 25.4, 1e-12);
            t.Near(steel.InsideRadius, steel.Thickness, 1e-12); // R = t
            t.Eq(steel.Toolset, "Standard");
            t.Eq(steel.Method, AllowanceMethod.KFactor);
            t.Near(steel.K, 0.44, 1e-12);
            t.Eq(steel.DefaultFlangeReference, FlangeReference.OutsideMoldLine);
            t.Near(steel.Tolerance, 0.001, 1e-15);
            // the closed hem's modelling gap: 0.005" (decided Oct 15)
            t.Near(steel.Rule(ProfileTables.Hems, "model gap", extraKey: "Closed"), 0.127, 1e-12);
            // a material's own row beats the "*" one
            t.Near(SheetRules.Resolve(StarterProfile.Profile, "Aluminum 5052", "0.063").K, 0.42, 1e-12);
            // rules are expressions of t and R
            t.Near(steel.Rule(ProfileTables.Relief, "bend relief depth"), steel.InsideRadius + steel.Thickness + 0.5, 1e-12);
            t.Near(steel.Rule(ProfileTables.Corners, "gap"), Math.Max(0.1, 0.2 * steel.Thickness), 1e-12);
            t.Eq(steel.RuleText(ProfileTables.Relief, "bend relief"), "Rectangular");
            t.True(steel.Items.Contains("sm.gauges/Steel CRS|16 ga"), string.Join(", ", steel.Items));
        });

        t.Test("Profile: the same material and gauge get the same rules; overrides change only that sheet", () =>
        {
            var a = SheetRules.Resolve(StarterProfile.Profile, "Stainless 304", "14 ga");
            var b = SheetRules.Resolve(StarterProfile.Profile, "Stainless 304", "14 ga");
            t.Eq(a.InsideRadius, b.InsideRadius);
            t.Eq(a.Allowance(Math.PI / 2), b.Allowance(Math.PI / 2));
            var o = a.WithOverrides(new Dictionary<string, string> { ["sm.tooling/inside radius"] = "3", ["sm.allowance/k"] = "0.33" });
            t.Near(o.InsideRadius, 3, 1e-12);
            t.Near(o.K, 0.33, 1e-12);
            t.True(o.Allowance(Math.PI / 2).Source.Overridden, "the allowance says it is overridden");
            t.False(a.Allowance(Math.PI / 2).Source.Overridden, "the plain rules are not overridden");
            t.Near(SheetRules.Resolve(StarterProfile.Profile, "Stainless 304", "14 ga").InsideRadius, a.InsideRadius, 0);
        });

        t.Test("Profile: misses are errors, never defaults (material, gauge, tool set, table)", () =>
        {
            var p = StarterProfile.Profile;
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(p, "Titanium", "1 mm"), "no materials row for material \"Titanium\"");
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(p, "Steel CRS", "7 ga"), "no gauges row");
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(p, "Steel CRS", "16 ga").WithToolset("Gooseneck"), "no tool set \"Gooseneck\"");
            var noHems = new ShopProfile("Bare", StarterProfile.Tables.Where(x => x.Key != ProfileTables.Hems));
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(noHems, "Steel CRS", "16 ga").Rule(ProfileTables.Hems, "gap", extraKey: "Closed"), "has no hem rules table");
            var noTeardrop = With(new ShopProfileTable
            {
                Key = ProfileTables.Hems, KeyColumns = ProfileTables.Empty(ProfileTables.Hems).KeyColumns, Columns = ProfileTables.Empty(ProfileTables.Hems).Columns,
                Rows = { new ProfileRow(new[] { "*", "*", "Closed" }, "1", "1", "2", "0", "0", "0") },
            });
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(noTeardrop, "Steel CRS", "16 ga").Rule(ProfileTables.Hems, "gap", extraKey: "Teardrop"), "hem \"Teardrop\"");
        });

        t.Test("Profile: tooling picks the default tool set of the most specific rows; another by name", () =>
        {
            var p = With(Tooling(
                Tool("*", "*", "Generic", "1", "t"),
                Tool("Steel CRS", "*", "V8", "1", "2"),
                Tool("Steel CRS", "*", "V16", "0", "4"),
                Tool("Steel CRS", "10 ga", "V24", "1", "6")));
            var s16 = SheetRules.Resolve(p, "Steel CRS", "16 ga");
            t.Eq(s16.Toolset, "V8");
            t.Near(s16.InsideRadius, 2, 0);
            t.Near(s16.WithToolset("V16").InsideRadius, 4, 0);
            t.Eq(SheetRules.Resolve(p, "Steel CRS", "10 ga").Toolset, "V24");
            t.Eq(SheetRules.Resolve(p, "Stainless 304", "16 ga").Toolset, "Generic");
            // two defaults, or none, at the same level: said, not guessed
            var two = With(Tooling(Tool("*", "*", "A", "1", "1"), Tool("*", "*", "B", "1", "1")));
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(two, "Steel CRS", "16 ga"), "2 default tool sets");
            var none = With(Tooling(Tool("*", "*", "A", "0", "1")));
            t.Throws<ProfileMissException>(() => SheetRules.Resolve(none, "Steel CRS", "16 ga"), "no default tool set");
        });

        t.Test("Profile: equally specific rows that both match are ambiguous", () =>
        {
            var corners = ProfileTables.Empty(ProfileTables.Corners);
            corners.Rows.Add(new ProfileRow(new[] { "Steel CRS", "*" }, "Open", "1", "Open", "0"));
            corners.Rows.Add(new ProfileRow(new[] { "*", "16 ga" }, "Open", "2", "Open", "0"));
            var s = SheetRules.Resolve(With(corners), "Steel CRS", "16 ga");
            t.Throws<ProfileMissException>(() => s.Rule(ProfileTables.Corners, "gap"), "ambiguous");
            t.Near(SheetRules.Resolve(With(corners), "Steel CRS", "14 ga").Rule(ProfileTables.Corners, "gap"), 1, 0);
        });

        t.Test("Profile: tables with problems are found (keys, cells, expressions)", () =>
        {
            var g = ProfileTables.Empty(ProfileTables.Gauges);
            g.Rows.Add(new ProfileRow(new[] { "Steel" }, "1", "0", "0"));
            g.Rows.Add(new ProfileRow(new[] { "Steel", "16" }, "max(t,", "0", "0"));
            g.Rows.Add(new ProfileRow(new[] { "Steel", "16" }, "1", "0", "0"));
            var problems = g.Problems().ToList();
            t.True(problems.Any(p => p.Contains("1 keys for 2")), string.Join("\n", problems));
            t.True(problems.Any(p => p.Contains("thickness")), string.Join("\n", problems));
            t.True(problems.Any(p => p.Contains("two rows are keyed")), string.Join("\n", problems));
        });

        t.Test("Profile CSV: tables round trip; inch tables read plain numbers as inches; bad files say why", () =>
        {
            foreach (var table in StarterProfile.Tables)
            {
                var back = ProfileCsv.Read(table.Key, ProfileCsv.Write(table), table.Units);
                t.Eq(ProfileCsv.Write(back), ProfileCsv.Write(table));
                t.Eq(string.Join(";", back.Rows.Select(r => r.KeyText + "=" + string.Join(",", r.Cells))),
                     string.Join(";", table.Rows.Select(r => r.KeyText + "=" + string.Join(",", r.Cells))));
            }
            var csv = "gauge,material,thickness,thickness tol plus,thickness tol minus\r\n16 ga,Steel,0.0598,0.003,0.003\r\n";
            var g = ProfileCsv.Read(ProfileTables.Gauges, csv, ShopProfileTable.Inch);
            t.Eq(g.Rows[0].Keys[0], "Steel"); // columns by name, in any order
            t.Eq(g.Rows[0].Cells[0], "0.0598in");
            // pasted from a spreadsheet (tabs)
            var tabs = "material\tgauge\tthickness\tthickness tol plus\tthickness tol minus\nSteel\t2 mm\t2\t0.1\t0.1\n";
            t.Eq(ProfileCsv.Read(ProfileTables.Gauges, tabs).Rows[0].Cells[0], "2");
            t.Throws<FormatException>(() => ProfileCsv.Read(ProfileTables.Gauges, "material,gauge\r\nSteel,1\r\n"), "no \"thickness\" column");
            t.Throws<FormatException>(() => ProfileCsv.Read(ProfileTables.Gauges, csv.Replace("tol minus\r\n", "tol minus,colour\r\n")), "\"colour\" is not a column");
            t.Throws<FormatException>(() => ProfileCsv.Read(ProfileTables.Gauges, csv.Replace("0.0598", "thin")), "\"thin\" is not known");
        });

        t.Test("Profile in libraries: published and read back; the set uses the last library's profile whole, else the starter", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-sheetmetal-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shop.sprlib");
            // a shop with steel only
            var tables = StarterProfile.Tables.Select(x => x.Clone()).ToList();
            var mats = tables.Single(x => x.Key == ProfileTables.Materials);
            mats.Rows.RemoveAll(r => r.Keys[0] != "Steel CRS");
            var id = Guid.NewGuid();
            LibraryFile.Publish(path, id, "Acme Shop", LibraryLayer.Company, Array.Empty<LibraryTable>(), null, "admin", profileTables: tables);
            var read = LibraryFile.Read(path);
            t.Eq(read.ProfileTables!.Count, tables.Count);
            t.Eq(ProfileCsv.Write(read.ProfileTables.Single(x => x.Key == ProfileTables.Gauges)), ProfileCsv.Write(tables.Single(x => x.Key == ProfileTables.Gauges)));

            t.Eq(LibrarySet.BuiltIn.Profile.Name, StarterProfile.Name);
            var set = new LibrarySet(new[] { read });
            t.Eq(set.Profile.Name, "Acme Shop");
            t.Near(Features.SheetMetalModule.RulesFor(set, "Steel CRS", "16 ga").Thickness, 0.0598 * 25.4, 1e-12);
            // the shop has no stainless: not found in the starter profile either
            t.Throws<ProfileMissException>(() => Features.SheetMetalModule.RulesFor(set, "Stainless 304", "16 ga"), "Acme Shop");

            // a library without a profile above it does not hide it
            var other = new LoadedLibrary(Guid.NewGuid(), "Fixtures", LibraryLayer.Company, Guid.NewGuid(), null, Array.Empty<LibraryTable>(), Array.Empty<LibraryRestriction>());
            t.Eq(new LibrarySet(new[] { read, other }).Profile.Name, "Acme Shop");

            // a table with problems is not published
            var bad = tables.Select(x => x.Clone()).ToList();
            bad.Single(x => x.Key == ProfileTables.Gauges).Rows[0].Cells[0] = "max(";
            t.Throws<ArgumentException>(() => LibraryFile.Publish(path, id, "Acme Shop", LibraryLayer.Company, Array.Empty<LibraryTable>(), null, "admin", profileTables: bad), "thickness");
            try { Directory.Delete(dir, true); } catch { }
        });
    }
}

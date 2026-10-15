using SpryCad.Core.Library;
using SpryCad.Core.Model;

namespace SpryCad.Tests;

/// <summary>Material libraries (Core/Library/Materials, BuiltInMaterials): material and appearance tables as standards-library data.</summary>
static class MaterialTests
{
    static string J<T>(T v) => System.Text.Json.JsonSerializer.Serialize(v, Serialization.Compact);

    public static void Run(TestRunner t)
    {
        t.Test("Materials: built-in tables well formed (the fixed columns, a density each, looks 0-1, unique names); the old twelve keep their names and densities", () =>
        {
            var keys = BuiltInStandards.Tables.Concat(BuiltInMaterials.Tables).Concat(FastenerFamilies.Tables).Select(x => x.Key).ToList();
            t.True(keys.Distinct().Count() == keys.Count, "table keys unique across the built-in libraries");
            var names = new HashSet<string>();
            foreach (var tb in BuiltInMaterials.Tables)
            {
                t.True(!tb.Verified, $"{tb.Key}: not marked verified");
                var cols = tb.Kind == TableKinds.Material ? Materials.Columns : Materials.AppearanceColumns;
                t.True(tb.Columns.SequenceEqual(cols), $"{tb.Key}: the {tb.Kind} columns");
                foreach (var r in tb.Rows)
                {
                    t.True(r.Values.Count == tb.Columns.Count, $"{tb.Key}/{r.Key}: a value per column");
                    foreach (var c in Materials.AppearanceColumns)
                        t.True(r.Values[tb.Column(c)] is >= 0 and <= 1, $"{tb.Key}/{r.Key}: {c} 0-1");
                    if (tb.Kind != TableKinds.Material) continue;
                    t.True(names.Add(r.Key), $"{r.Key}: one material of that name");
                    var m = Materials.Of(tb, r)!;
                    t.True(m.Value("density") is > 100 and < 25000, $"{r.Key}: a density");
                    t.True(r.Text?.ContainsKey(Materials.TypeKey) == true && Enum.TryParse<MaterialType>(r.Text[Materials.TypeKey], out _), $"{r.Key}: a type");
                    t.True(m.Modulus is null or > 1e6, $"{r.Key}: modulus in Pa");
                    t.True(m.Poisson is null or > 0 and < 0.5, $"{r.Key}: Poisson's ratio");
                    t.True(m.Yield is not { } y || m.Tensile is not { } u || y <= u, $"{r.Key}: yield at most tensile");
                    t.True(m.Appearance != null, $"{r.Key}: a look");
                }
            }
            var set = LibrarySet.BuiltIn;
            foreach (var (name, density) in new (string, double)[]
            {
                ("Steel (plain carbon)", 7850), ("Stainless steel", 8000), ("Cast iron", 7200), ("Aluminum 6061", 2700), ("Brass", 8500),
                ("Copper", 8940), ("Titanium Ti-6Al-4V", 4430), ("ABS", 1050), ("PLA", 1240), ("Nylon 6/6", 1140), ("Acetal (POM)", 1410),
                ("Polycarbonate", 1200),
            })
            {
                var m = set.Offered(TableKinds.Material).SelectMany(tb => tb.Rows.Select(r => Materials.Of(tb, r)!)).FirstOrDefault(x => x.Name == name);
                t.True(m != null, $"{name} offered");
                t.Near(m!.Density, density, 1e-9);
            }
            t.True(set.Offered(TableKinds.Appearance).Single().Rows.Count > 10, "appearance presets offered");
        });

        t.Test("Materials: written as a library file and read back unchanged (missing values kept as not known); a size table's missing value refused", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "fc-material-tests", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "materials.sprlib");
            LibraryFile.Publish(path, BuiltInMaterials.LibraryId, BuiltInMaterials.Name, LibraryLayer.Standards, BuiltInMaterials.Tables, null, "test");
            var lib = LibraryFile.Read(path);
            t.Eq(J(lib.Tables), J(BuiltInMaterials.Tables.OrderBy(x => x.Key, StringComparer.Ordinal).ToList()));
            t.Eq(lib.RevisionId == Guid.Empty, false);
            var oak = Materials.Of(lib.Tables.Single(x => x.Key == "Materials-wood"), lib.Tables.Single(x => x.Key == "Materials-wood").Row("Red oak")!)!;
            t.True(oak.Poisson == null && oak.Modulus is > 1e10, "a value not known stays not known");
            var thread = new LibraryTable { Key = "T", Kind = TableKinds.Thread, Columns = { "major", "pitch" }, Rows = { new LibraryRow("M8", 8, double.NaN) } };
            t.True(Throws(() => LibraryFile.Publish(Path.Combine(dir, "bad.sprlib"), Guid.NewGuid(), "Bad", LibraryLayer.Company, new[] { thread }, null, "test")), "a thread with no pitch refused");
            var noDensity = new LibraryTable { Key = "M", Kind = TableKinds.Material, Columns = Materials.Columns.ToList(), Rows = { new LibraryRow("X", Materials.Columns.Select(_ => double.NaN).ToArray()) } };
            t.True(Throws(() => LibraryFile.Publish(Path.Combine(dir, "bad2.sprlib"), Guid.NewGuid(), "Bad", LibraryLayer.Company, new[] { noDensity }, null, "test")), "a material with no density refused");
        });

        t.Test("Materials: a company library overrides a built-in material, adds its own and restricts what is offered; a snapshot reads as the company's", () =>
        {
            var builtIn = BuiltInMaterials.Tables.Single(x => x.Key == "Materials-aluminum");
            var mine = builtIn.Row("Aluminum 6061")!;
            var values = mine.Values.ToArray();
            values[builtIn.Column("density")] = 2710;
            var plate = (double[])values.Clone();
            plate[builtIn.Column("yield")] = 240e6;
            var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryLayer.Company, Guid.NewGuid(), null,
                new[]
                {
                    new LibraryTable
                    {
                        Key = "Materials-aluminum", Title = "Aluminum", Kind = TableKinds.Material, Columns = Materials.Columns.ToList(),
                        Rows =
                        {
                            new LibraryRow("Aluminum 6061", values) { Text = mine.Text },
                            new LibraryRow("Acme 6061-T651 plate", plate) { Text = new(StringComparer.Ordinal) { [Materials.TypeKey] = "Aluminum", ["partNumber"] = "AL-6061-PL" } },
                        },
                    },
                },
                new[]
                {
                    new LibraryRestriction { Table = "Materials-aluminum", Allow = new() { "Aluminum 6061", "Acme 6061-T651 plate" } },
                    new LibraryRestriction { Table = "Materials-wood", Hidden = true },
                });
            var set = new LibrarySet(new[] { company });
            var offered = set.Offered(TableKinds.Material).ToList();
            t.Eq(string.Join(",", offered.Single(x => x.Key == "Materials-aluminum").Rows.Select(r => r.Key)), "Aluminum 6061,Acme 6061-T651 plate");
            t.True(offered.All(x => x.Key != "Materials-wood"), "a hidden family not offered");
            t.True(offered.Any(x => x.Key == "Materials-steel"), "the other families still offered");
            var s = set.Snapshot("Materials-aluminum/Aluminum 6061")!;
            t.Eq(s.LibraryName, "Acme");
            t.Near(Materials.Of(s)!.Density, 2710, 1e-9);
            t.True(set.Snapshot("Materials-aluminum/Aluminum 7075-T6") != null, "a restricted material still resolves (parts that use it)");
            var p = Materials.Of(set.Snapshot("Materials-aluminum/Acme 6061-T651 plate")!)!;
            t.Eq(p.Type, MaterialType.Aluminum);
            t.Eq(p.Text("partNumber"), "AL-6061-PL");
            // the change described as Library Updates lists it (a value not known on both sides is no change)
            var update = new LibraryUpdate(s.Item, LibrarySet.BuiltIn.Snapshot(s.Item)!, s);
            t.Eq(update.Describe(), "Aluminum 6061 (Materials-aluminum): density 2700 → 2710");
        });

        t.Test("Materials: units (MMGS and IPS), shear from E and ν, an unknown type, CSV with values not known", () =>
        {
            t.Eq(Materials.Unit("density", UnitSystem.MMGS), (1000.0, "g/cm³"));
            t.Near(7850 / Materials.Unit("density", UnitSystem.IPS).scale, 0.2836, 1e-4);
            t.Near(250e6 / Materials.Unit("yield", UnitSystem.IPS).scale, 36.26, 0.01);
            t.Near(200e9 / Materials.Unit("modulus", UnitSystem.IPS).scale, 29.0, 0.01);
            t.Near(12e-6 / Materials.Unit("expansion", UnitSystem.IPS).scale, 6.667, 0.001);
            t.Eq(Materials.Unit("poisson", UnitSystem.IPS), (1.0, ""));
            var steel = Materials.Of(LibrarySet.BuiltIn.Snapshot("Materials-steel/Steel (plain carbon)")!)!;
            t.Near(steel.Shear!.Value, 200e9 / (2 * 1.29), 1);
            t.Eq(steel.Appearance!.Metallic, 1.0);
            t.Eq(Materials.TypeOf("Unobtainium"), MaterialType.Other);
            var wood = BuiltInMaterials.Tables.Single(x => x.Key == "Materials-wood");
            var csv = LibraryCsv.Write(wood);
            t.True(csv.Contains("Red oak,660,12500000000,,"), "a value not known written empty");
            var (cols, rows) = LibraryCsv.Read(csv);
            t.True(cols.SequenceEqual(wood.Columns), "columns read back");
            t.True(double.IsNaN(rows.Single(r => r.Key == "Red oak").Values[wood.Column("poisson")]), "an empty cell read as not known");
            t.Eq(J(rows), J(wood.Rows));
        });
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
}

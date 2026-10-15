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

    /// <summary>The part's material and appearance (plan phase 2): set, kept, saved, upgraded from older parts, updated from a library.</summary>
    public static void RunPart(TestRunner t)
    {
        const string Al = "Materials-aluminum/Aluminum 6061";

        t.Test("Part material: a library material copied in (name, density, look); a color of its own keeps the finish; cleared, its row goes", () =>
        {
            var doc = new CadDocument();
            t.Eq(doc.MaterialName, null);
            t.Eq(doc.EffectiveDensity, CadDocument.DefaultDensity);
            t.Eq(doc.EffectiveAppearance, Appearance.Default);
            PartMaterial.Set(doc, Al, LibrarySet.BuiltIn);
            t.Eq(doc.MaterialItem, Al);
            t.True(doc.Library?.ContainsKey(Al) == true, "the row copied into the part");
            t.Eq(doc.MaterialName, "Aluminum 6061");
            t.Near(doc.EffectiveDensity, 2700, 1e-9);
            t.Eq(doc.EffectiveAppearance.Metallic, 1.0);
            t.True(doc.Color.SequenceEqual(new[] { 0.78, 0.79, 0.80 }), "the color shown is the material's");
            doc.Color = new[] { 0.1, 0.2, 0.6 };
            t.True(doc.Appearance != null && doc.Appearance.Metallic == 1 && doc.Appearance.Roughness == 0.4, "its own color, the material's finish kept");
            PartMaterial.Set(doc, "Materials-copper/Brass", LibrarySet.BuiltIn);
            t.True(doc.Color.SequenceEqual(new[] { 0.1, 0.2, 0.6 }), "its own appearance kept through a change of material");
            t.True(doc.Library!.Keys.SequenceEqual(new[] { "Materials-copper/Brass" }), "the old material's row dropped");
            PartMaterial.Set(doc, null);
            t.True(doc.MaterialItem == null && doc.Library == null, "cleared");
            t.True(Throws(() => PartMaterial.Set(doc, "ISO273/M8", LibrarySet.BuiltIn)), "a size is not a material");
        });

        t.Test("Part material: kept through a copy (undo) and a part file's objects, its row with it; the older readers' color, name and density written", () =>
        {
            var doc = new CadDocument();
            PartMaterial.Set(doc, Al, LibrarySet.BuiltIn);
            doc.Appearance = new Appearance(new[] { 0.2, 0.3, 0.4 }, 0.5, 0.2, 0.8, "Mine");
            var copy = doc.Clone();
            t.Eq(copy.MaterialItem, Al);
            t.Eq(copy.Appearance, doc.Appearance);
            t.Eq(J(copy.Library), J(doc.Library));
            t.True(copy.LegacyColor == null && !doc.ToJson().Contains("\"color\":[0.2"), "no legacy color written");
            var objects = SpryCad.Core.Storage.PartObjects.From(doc);
            var material = System.Text.Encoding.UTF8.GetString(objects[SpryCad.Core.Storage.PartObjects.Single(SpryCad.Core.Storage.ObjectKind.Material)]);
            t.True(material.Contains("\"material\":\"Aluminum 6061\"") && material.Contains("\"density\":2700") && material.Contains("\"color\":[0.2,0.3,0.4]"), "what an older SpryCAD reads");
            // the part's copy read back with it (what it weighs by, library or none)
            var back = SpryCad.Core.Storage.PartObjects.ToDocument(objects);
            t.Eq(back.MaterialItem, Al);
            t.Eq(J(back.Library), J(doc.Library));
            t.Near(back.EffectiveDensity, 2700, 1e-9);
            t.Eq(back.Appearance, doc.Appearance);
        });

        t.Test("Part material: older parts (JSON and part files) keep their weight and color: a name matched to the library, a density that differs or an unknown name a custom material", () =>
        {
            static CadDocument FromJson(string material) =>
                CadDocument.FromJson($$"""{ "format": "SpryCAD", "formatVersion": 3, "features": [], {{material}} }""");
            var a = FromJson("""  "color": [0.74, 0.76, 0.80], "material": "Aluminum 6061", "density": 2700  """);
            t.Eq(a.MaterialItem, Al);
            t.Eq(a.Appearance, null);
            t.True(a.LegacyColor == null && a.LegacyMaterial == null && a.LegacyDensity == null, "the legacy values cleared");
            var b = FromJson("""  "color": [0.74, 0.76, 0.80], "material": "Brass", "density": 8400  """);
            t.True(PartMaterial.IsCustom(b.MaterialItem) && b.MaterialName == "Brass", "a changed density: a custom material of the old name");
            t.Near(b.EffectiveDensity, 8400, 1e-9);
            t.Eq(b.MaterialInfo!.Type, MaterialType.CopperAlloy);
            var c = FromJson("""  "color": [0.9, 0.1, 0.1], "material": "Custom", "density": 5000  """);
            t.True(PartMaterial.IsCustom(c.MaterialItem) && c.MaterialName == "Custom", "custom");
            t.Near(c.EffectiveDensity, 5000, 1e-9);
            t.True(c.Color.SequenceEqual(new[] { 0.9, 0.1, 0.1 }) && c.Appearance != null, "a color set: its own appearance");
            var d = FromJson("""  "color": [0.74, 0.76, 0.80]  """);
            t.True(d.MaterialItem == null && d.Appearance == null && d.Library == null, "nothing set: nothing");
            // a part file's objects from before (the material object holding only a color, a name and a density)
            var objects = SpryCad.Core.Storage.PartObjects.From(new CadDocument());
            objects[SpryCad.Core.Storage.PartObjects.Single(SpryCad.Core.Storage.ObjectKind.Material)] =
                System.Text.Encoding.UTF8.GetBytes("""{"kind":4,"v":1,"data":{"color":[0.5,0.5,0.5],"material":"Copper","density":8940}}""");
            var e = SpryCad.Core.Storage.PartObjects.ToDocument(objects);
            t.Eq(e.MaterialItem, "Materials-copper/Copper");
            t.True(e.Color.SequenceEqual(new[] { 0.5, 0.5, 0.5 }), "its color kept");
        });

        t.Test("Part material: a library's change offered by Library Updates and taken in (density); a custom material kept by Sync and never offered", () =>
        {
            var doc = new CadDocument();
            PartMaterial.Set(doc, Al, LibrarySet.BuiltIn);
            var table = BuiltInMaterials.Tables.Single(x => x.Key == "Materials-aluminum");
            var values = table.Row("Aluminum 6061")!.Values.ToArray();
            values[table.Column("density")] = 2710;
            var company = new LoadedLibrary(Guid.NewGuid(), "Acme", LibraryLayer.Company, Guid.NewGuid(), null,
                new[] { new LibraryTable { Key = table.Key, Kind = TableKinds.Material, Columns = table.Columns, Rows = { new LibraryRow("Aluminum 6061", values) { Text = table.Row("Aluminum 6061")!.Text } } } },
                Array.Empty<LibraryRestriction>());
            var set = new LibrarySet(new[] { company });
            t.True(!LibraryLink.Sync(doc, set), "nothing to add or drop");
            var updates = LibraryLink.Updates(doc, set);
            t.Eq(updates.Single().Item, Al);
            t.Near(doc.EffectiveDensity, 2700, 1e-9);
            LibraryLink.Accept(doc, updates);
            t.Near(doc.EffectiveDensity, 2710, 1e-9);
            PartMaterial.SetCustom(doc, "Aluminum 6061, custom density", 2650, doc.Library![Al]);
            t.True(doc.Library.Keys.SequenceEqual(new[] { "Part/Aluminum 6061, custom density" }), "the custom row only");
            t.True(!LibraryLink.Sync(doc, set), "Sync keeps it");
            t.Eq(LibraryLink.Updates(doc, set).Count, 0);
            t.True(doc.EffectiveAppearance.Metallic == 1, "the material's look kept");
            t.True(Throws(() => LibraryFile.Publish(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sprlib"), Guid.NewGuid(), "Bad", LibraryLayer.Company,
                new[] { new LibraryTable { Key = PartMaterial.CustomTable, Kind = TableKinds.Material, Columns = Materials.Columns.ToList() } }, null, "test")), "no library may key a table as parts' own");
        });

        t.Test("Part material: a fastener weighs and looks as its grade (class 8.8 steel, a washer plain steel; a row naming its own)", () =>
        {
            var set = LibrarySet.BuiltIn;
            var screw = Fasteners.Part(set.Snapshot("ISO4762/M8x30")!, set);
            t.Eq(screw.MaterialName, "Steel, property class 8.8");
            t.True(screw.EffectiveAppearance.Color[0] < 0.3, "black oxide");
            t.Eq(Fasteners.Part(set.Snapshot("ISO7089/M8")!, set).MaterialName, "Steel (plain carbon)");
            var a2 = set.Snapshot("ISO4762/M8x30")!;
            a2.Text![Fasteners.MaterialKey] = "Materials-stainless/Stainless steel, A2-70";
            t.Eq(Fasteners.Part(a2, set).MaterialName, "Stainless steel, A2-70");
            t.True(screw.Library!.Count == 2 && !LibraryLink.Sync(screw, set), "its row and its material's");
        });
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
}

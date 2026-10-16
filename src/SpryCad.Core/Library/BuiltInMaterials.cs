namespace SpryCad.Core.Library;

/// <summary>
/// The materials SpryCAD carries itself (section 34, layer 1, beside <see cref="BuiltInStandards"/>): a material table
/// per family (so a company library can override, restrict or hide a family) and the appearance presets. Typical
/// handbook values, not any one specification's minimums, and marked not verified (<see cref="LibraryTable.Verified"/>):
/// check them before relying on them. Table keys and row names never change meaning (parts' snapshots name them); the
/// twelve materials SpryCAD listed before libraries keep their names, so older parts find them.
/// </summary>
public static class BuiltInMaterials
{
    /// <summary>The built-in materials library's identity (fixed: parts' snapshots name it).</summary>
    public static readonly Guid LibraryId = new("2c8e5f1a-7d34-4b96-a0e2-91f6c3d84b27");
    public const string Name = "SpryCAD materials";
    public const string Source = "Typical values";
    /// <summary>The appearance presets' table.</summary>
    public const string AppearancesKey = "Appearances";

    const double N = double.NaN;

    static IReadOnlyList<LibraryTable>? _tables;
    public static IReadOnlyList<LibraryTable> Tables => _tables ??= Build();

    // looks shared by a family's rows: r, g, b, metallic, roughness, opacity
    static readonly double[] SteelLook = { 0.62, 0.63, 0.65, 1, 0.45, 1 }, StainlessLook = { 0.70, 0.71, 0.72, 1, 0.30, 1 },
        IronLook = { 0.35, 0.36, 0.37, 1, 0.70, 1 }, AluminumLook = { 0.78, 0.79, 0.80, 1, 0.40, 1 }, BlackOxide = { 0.20, 0.20, 0.21, 0.6, 0.50, 1 },
        BrassLook = { 0.78, 0.64, 0.32, 1, 0.30, 1 }, CopperLook = { 0.80, 0.50, 0.35, 1, 0.30, 1 }, BronzeLook = { 0.70, 0.50, 0.28, 1, 0.40, 1 },
        TitaniumLook = { 0.60, 0.59, 0.56, 1, 0.45, 1 }, NickelLook = { 0.66, 0.65, 0.62, 1, 0.40, 1 }, ZincLook = { 0.70, 0.72, 0.74, 1, 0.50, 1 };
    static double[] Plastic(double r, double g, double b, double rough = 0.5, double opacity = 1) => new[] { r, g, b, 0, rough, opacity };

    /// <summary>
    /// A material row: density (kg/m³), Young's modulus (GPa), Poisson's ratio, yield and tensile strength (MPa),
    /// elongation (%), hardness, expansion (µm/(m·K)), conductivity (W/(m·K)), specific heat (J/(kg·K)) and its look;
    /// stored in SI (shear modulus not given: E / 2(1 + ν)).
    /// </summary>
    static LibraryRow M(string name, MaterialType type, double density, double eGPa, double poisson, double yieldMPa, double tensileMPa,
        double elongationPct, double expansionMicro, double conductivity, double specificHeat, double[] look,
        double hardness = N, string? scale = null, string? spec = null, string? category = null, string? description = null)
    {
        var text = new SortedDictionary<string, string>(StringComparer.Ordinal) { [Materials.TypeKey] = type.ToString() };
        if (scale != null) text[Materials.HardnessScaleKey] = scale;
        if (spec != null) text[Materials.SpecKey] = spec;
        if (category != null) text[Materials.CategoryKey] = category;
        if (description != null) text[Materials.DescriptionKey] = description;
        return new LibraryRow(name, new[]
        {
            density, eGPa * 1e9, poisson, N, yieldMPa * 1e6, tensileMPa * 1e6, elongationPct / 100, hardness,
            expansionMicro * 1e-6, conductivity, specificHeat,
        }.Concat(look).ToArray()) { Text = text };
    }

    static LibraryTable Family(string key, string title, params LibraryRow[] rows) => new()
    {
        Key = key, Title = title, Kind = TableKinds.Material, Standard = Source, Columns = Materials.Columns.ToList(), Rows = rows.ToList(),
    };

    static List<LibraryTable> Build()
    {
        const MaterialType St = MaterialType.Steel, Ss = MaterialType.StainlessSteel, Fe = MaterialType.CastIron, Al = MaterialType.Aluminum,
            Cu = MaterialType.CopperAlloy, Ti = MaterialType.Titanium, Pl = MaterialType.Plastic, El = MaterialType.Elastomer,
            Co = MaterialType.Composite, Wd = MaterialType.Wood;
        return new List<LibraryTable>
        {
            Family("Materials-steel", "Carbon and alloy steels",
                M("Steel (plain carbon)", St, 7850, 200, 0.29, 250, 400, 25, 12, 50, 486, SteelLook),
                M("AISI 1018 (cold drawn)", St, 7870, 205, 0.29, 370, 440, 15, 11.5, 51.9, 486, SteelLook, 126, "HB", "ASTM A108"),
                M("AISI 1045 (cold drawn)", St, 7870, 205, 0.29, 530, 625, 12, 11.2, 49.8, 486, SteelLook, 179, "HB", "ASTM A108"),
                M("AISI 4140 (annealed)", St, 7850, 205, 0.29, 415, 655, 25.7, 12.2, 42.6, 473, SteelLook, 197, "HB", "ASTM A29"),
                M("ASTM A36", St, 7850, 200, 0.26, 250, 400, 23, 11.7, 51.9, 486, SteelLook, spec: "ASTM A36", category: "Structural"),
                M("Steel, property class 8.8", St, 7850, 205, 0.29, 640, 800, 12, 11.5, 50, 486, BlackOxide, spec: "ISO 898-1", category: "Fasteners")),
            Family("Materials-stainless", "Stainless steels",
                M("Stainless steel", Ss, 8000, 193, 0.29, 215, 505, 40, 17.3, 16.2, 500, StainlessLook, description: "Austenitic, as 304"),
                M("AISI 304 (annealed)", Ss, 8000, 193, 0.29, 215, 505, 70, 17.3, 16.2, 500, StainlessLook, 123, "HB", "ASTM A240"),
                M("AISI 316 (annealed)", Ss, 8000, 193, 0.30, 290, 580, 50, 16.0, 16.3, 500, StainlessLook, spec: "ASTM A240"),
                M("17-4 PH (H1025)", Ss, 7780, 197, 0.27, 1000, 1070, 12, 10.8, 18.3, 460, StainlessLook, spec: "ASTM A564"),
                M("Stainless steel, A2-70", Ss, 7900, 193, 0.29, 450, 700, N, 17.3, 16.2, 500, StainlessLook, spec: "ISO 3506-1", category: "Fasteners")),
            Family("Materials-iron", "Cast irons",
                M("Cast iron", Fe, 7200, 110, 0.26, N, 250, N, 11, 46, 490, IronLook, description: "Gray iron"),
                M("Gray iron, class 30", Fe, 7150, 100, 0.26, N, 214, N, 10.5, 46, 490, IronLook, 210, "HB", "ASTM A48"),
                M("Ductile iron 65-45-12", Fe, 7100, 169, 0.29, 310, 448, 12, 11.2, 36, 506, IronLook, spec: "ASTM A536")),
            Family("Materials-aluminum", "Aluminum alloys",
                M("Aluminum 6061", Al, 2700, 68.9, 0.33, 276, 310, 12, 23.6, 167, 896, AluminumLook, 95, "HB", "ASTM B209 / B221", description: "6061-T6"),
                M("Aluminum 6063-T5", Al, 2700, 68.9, 0.33, 145, 186, 12, 23.4, 209, 900, AluminumLook, 60, "HB", "ASTM B221", "Extrusions"),
                M("Aluminum 7075-T6", Al, 2810, 71.7, 0.33, 503, 572, 11, 23.6, 130, 960, AluminumLook, 150, "HB", "ASTM B209"),
                M("Aluminum 5052-H32", Al, 2680, 70.3, 0.33, 193, 228, 12, 23.8, 138, 880, AluminumLook, 60, "HB", "ASTM B209", "Sheet"),
                M("Aluminum A356-T6 (cast)", Al, 2670, 72.4, 0.33, 165, 228, 3.5, 21.4, 151, 963, AluminumLook, 70, "HB", "ASTM B26", "Castings")),
            Family("Materials-copper", "Copper alloys",
                M("Brass", Cu, 8500, 97, 0.31, 310, 385, 25, 20.5, 115, 380, BrassLook, spec: "ASTM B16", description: "Free-cutting brass C360, half hard"),
                M("Copper", Cu, 8940, 115, 0.31, 69, 220, 45, 17, 388, 385, CopperLook, spec: "ASTM B152", description: "C110, annealed"),
                M("Bronze C932 (bearing)", Cu, 8930, 100, 0.34, 125, 240, 20, 18, 58, 376, BronzeLook, 65, "HB", "ASTM B505")),
            Family("Materials-titanium", "Titanium alloys",
                M("Titanium Ti-6Al-4V", Ti, 4430, 113.8, 0.342, 880, 950, 14, 8.6, 6.7, 526, TitaniumLook, 334, "HB", "ASTM B348 grade 5"),
                M("Titanium grade 2", Ti, 4510, 105, 0.37, 275, 345, 20, 8.6, 16.4, 523, TitaniumLook, spec: "ASTM B348 grade 2")),
            Family("Materials-other-metals", "Other metals",
                M("Inconel 718", MaterialType.Nickel, 8190, 200, 0.29, 1034, 1241, 12, 13, 11.4, 435, NickelLook, spec: "AMS 5662"),
                M("Magnesium AZ31B", MaterialType.Magnesium, 1770, 45, 0.35, 200, 260, 15, 26, 96, 1000, AluminumLook, spec: "ASTM B90"),
                M("Zinc (Zamak 3)", MaterialType.Zinc, 6600, 96, 0.27, 221, 283, 10, 27.4, 113, 419, ZincLook, 82, "HB", "ASTM B86", "Die castings")),
            Family("Materials-plastics", "Plastics",
                M("ABS", Pl, 1050, 2.3, 0.35, 40, 43, 25, 90, 0.17, 1400, Plastic(0.15, 0.15, 0.16)),
                M("PLA", Pl, 1240, 3.5, 0.36, 60, 65, 6, 68, 0.13, 1800, Plastic(0.92, 0.92, 0.90)),
                M("Nylon 6/6", Pl, 1140, 2.8, 0.39, N, 80, 50, 80, 0.25, 1670, Plastic(0.92, 0.90, 0.84), description: "Dry as molded"),
                M("Nylon 12", Pl, 1010, 1.4, 0.40, N, 50, N, 100, 0.25, N, Plastic(0.90, 0.90, 0.88)),
                M("Acetal (POM)", Pl, 1410, 2.8, 0.35, 65, 70, 40, 110, 0.31, 1470, Plastic(0.95, 0.95, 0.93)),
                M("Polycarbonate", Pl, 1200, 2.3, 0.37, 62, 65, 110, 68, 0.20, 1200, Plastic(0.85, 0.88, 0.90, 0.1, 0.5)),
                M("Acrylic (PMMA)", Pl, 1190, 3.1, 0.37, N, 70, 5, 70, 0.19, 1470, Plastic(0.88, 0.92, 0.94, 0.05, 0.3)),
                M("HDPE", Pl, 950, 1.0, 0.42, 26, 30, N, 120, 0.48, 1900, Plastic(0.95, 0.95, 0.95, 0.6)),
                M("Polypropylene", Pl, 905, 1.5, 0.42, 32, 35, N, 100, 0.22, 1920, Plastic(0.90, 0.90, 0.88, 0.6)),
                M("PETG", Pl, 1270, 2.1, 0.38, 50, 53, N, 68, 0.20, N, Plastic(0.86, 0.90, 0.92, 0.1, 0.6)),
                M("PEEK", Pl, 1300, 3.6, 0.40, N, 100, N, 47, 0.25, 1340, Plastic(0.80, 0.70, 0.55)),
                M("PTFE", Pl, 2200, 0.5, 0.46, N, 25, 300, 135, 0.25, 1000, Plastic(0.97, 0.97, 0.96, 0.7))),
            Family("Materials-elastomers", "Elastomers",
                M("TPU (Shore 95A)", El, 1210, 0.026, 0.48, N, 40, 500, N, N, N, Plastic(0.10, 0.10, 0.10, 0.8), 95, "Shore A"),
                M("Natural rubber", El, 930, 0.003, 0.49, N, 20, 700, N, 0.13, N, Plastic(0.10, 0.10, 0.10, 0.85))),
            Family("Materials-composites", "Composites",
                M("Carbon fiber laminate (quasi-isotropic)", Co, 1550, 50, 0.30, N, 600, N, N, N, N, Plastic(0.12, 0.12, 0.13, 0.3), description: "In-plane"),
                M("FR-4 (glass epoxy)", Co, 1850, 22, 0.12, N, 310, N, 14, 0.30, N, Plastic(0.70, 0.75, 0.45), description: "In-plane")),
            Family("Materials-wood", "Woods",
                M("Red oak", Wd, 660, 12.5, N, N, N, N, N, N, N, Plastic(0.70, 0.52, 0.33, 0.7), description: "Along the grain, 12% moisture"),
                M("Eastern white pine", Wd, 400, 8.5, N, N, N, N, N, N, N, Plastic(0.86, 0.72, 0.52, 0.7), description: "Along the grain, 12% moisture")),
            Family("Materials-ceramics-glass", "Ceramics and glass",
                M("Glass (soda-lime)", MaterialType.Glass, 2500, 72, 0.22, N, N, N, 9, 1.0, 840, Plastic(0.85, 0.92, 0.92, 0.05, 0.25)),
                M("Alumina (96%)", MaterialType.Ceramic, 3720, 300, 0.21, N, N, N, 8.2, 24.7, 880, Plastic(0.96, 0.95, 0.92, 0.6))),
            new LibraryTable
            {
                Key = AppearancesKey, Title = "Appearances", Kind = TableKinds.Appearance, Standard = "SpryCAD", Columns = Materials.AppearanceColumns.ToList(),
                Rows = new (string name, double[] look)[]
                {
                    ("Polished steel", new[] { 0.66, 0.67, 0.69, 1, 0.15, 1 }), ("Brushed steel", SteelLook), ("Cast iron", IronLook),
                    ("Polished aluminum", new[] { 0.85, 0.86, 0.87, 1, 0.15, 1 }), ("Brushed aluminum", AluminumLook),
                    ("Brass", BrassLook), ("Copper", CopperLook), ("Bronze", BronzeLook), ("Chrome", new[] { 0.80, 0.81, 0.83, 1, 0.05, 1 }),
                    ("Black oxide", BlackOxide), ("Black anodize", new[] { 0.12, 0.12, 0.13, 0.5, 0.4, 1 }), ("Clear anodize", new[] { 0.80, 0.81, 0.82, 0.8, 0.45, 1 }),
                    ("Red anodize", new[] { 0.65, 0.10, 0.12, 0.6, 0.4, 1 }), ("Blue anodize", new[] { 0.12, 0.25, 0.60, 0.6, 0.4, 1 }),
                    ("Matte plastic, black", Plastic(0.10, 0.10, 0.11, 0.8)), ("Matte plastic, white", Plastic(0.92, 0.92, 0.91, 0.8)),
                    ("Matte plastic, grey", Plastic(0.50, 0.51, 0.53, 0.8)), ("Gloss plastic, black", Plastic(0.08, 0.08, 0.09, 0.15)),
                    ("Gloss plastic, white", Plastic(0.94, 0.94, 0.93, 0.15)), ("Gloss plastic, red", Plastic(0.75, 0.10, 0.10, 0.15)),
                    ("Gloss plastic, blue", Plastic(0.10, 0.25, 0.70, 0.15)), ("Gloss plastic, yellow", Plastic(0.95, 0.80, 0.10, 0.15)),
                    ("Rubber", Plastic(0.10, 0.10, 0.10, 0.85)), ("Clear plastic", Plastic(0.88, 0.92, 0.94, 0.05, 0.35)),
                    ("Clear glass", Plastic(0.85, 0.92, 0.92, 0.05, 0.25)), ("Smoked glass", Plastic(0.30, 0.32, 0.33, 0.05, 0.5)),
                }.Select(a => new LibraryRow(a.name, a.look)).ToList(),
            },
        };
    }

    /// <summary>The built-in materials as a loaded library (with the built-in standards, the bottom of every <see cref="LibrarySet"/>).</summary>
    public static LoadedLibrary AsLibrary() => new(LibraryId, Name, LibraryLayer.Standards, LibraryFile.ContentRevision(Tables, null), null, Tables, Array.Empty<LibraryRestriction>());
}

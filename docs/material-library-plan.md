# Material library: plan

A library of materials a part can be given: what it is (its type), its mechanical (and thermal) properties, and how it looks.
Assigning one sets the part's density for mass properties and, unless the part's appearance was set on its own, its appearance.

Materials are **standards-library data**: the same `.sprlib` files, layers, local copies, snapshots, Library Updates and
library editor as threads, holes and fasteners (`docs/standards-libraries.md`, branch `libraries-fasteners`). Nothing here
adds a second library mechanism; it adds table kinds, a built-in materials library, and the part/appearance plumbing.

> Depends on `libraries-fasteners`: build this branch on it (or after it merges to master).

## Where things stand

- A part has `Color` (RGB 0-1), `Material` (a name) and `Density` (kg/m³, null = steel 7850): `CadDocument.cs:48-54`.
  They are saved together as one `ObjectKind.Material` object (`MaterialData`, `PartObjects.cs:181`).
- The "library" is 12 `(name, density)` pairs hard-coded in the Mass Properties window (`MainWindow.MassProps.cs:32`),
  which is also the only place a material is chosen.
- Appearance is RGB only: Options > Document Properties > Appearance for a part (`OptionsDialog.cs:388`), a 12-swatch grid for an
  assembly component (`MainWindow.AsmTools.cs:154-200`, `Component.Color`, `ComponentOverride.Color`).
  The shader has fixed Blinn specular (exponent 60, strength 0.22) for every part; alpha works (a second, blended pass).
- The BOM has a Material column (name only); assembly mass uses each part's density (`AssemblyTools.cs:180`).
- Fastener parts are made with `Material = "Steel"` and a fixed dark grey (`Library/Fasteners.cs:207`, fasteners branch).
- STEP neither reads nor writes colors or materials.

## What the standards libraries already give us

| Standards libraries (fasteners branch) | Used for materials as |
| --- | --- |
| `LibraryTable` (key, title, kind, standard, verified, units, numeric columns, rows by designation, row text) | A materials table: a row per material, its properties as columns, its type/category/spec as text |
| `TableKinds` | Two new kinds: `material` and `appearance` |
| `BuiltInStandards` (C# tables, fixed `LibraryId`, `Verified = false`) | `BuiltInMaterials`, the same way: a fixed id, typical values marked unverified |
| Layers: built-in, standards, part families, company | Company libraries add in-house materials (with part numbers, specs) and correct values; a standards library can carry a verified set |
| `LibraryRestriction` (allow / deny / hidden) | A company's **approved materials** list: the material picker offers only those |
| `LibrarySnapshot` in `CadDocument.Library` (an `ObjectKind.LibrarySnapshot` object each) | The part's copy of its material row: it keeps its mass and look with no library installed |
| `LibraryLink.Updates` / `Accept`, Tools > Library Updates | A changed material row is offered like a changed thread size, taken in as one undoable edit |
| `LibrarySync` (central `libraries.json`, local copies, offline) | Nothing new: material tables travel in the same files |
| `LibraryEditor` (tables, rows, text, CSV in/out, restrictions, publish with conflict check) | Edits material tables; needs a column-units display and a color swatch for appearance columns |
| `PartDescribe` case for `LibrarySnapshot` | History reads "Library data Aluminum 6061-T6 (Materials-aluminum) updated: density 2700 → 2710" for free |

## Table kinds

Added to `TableKinds`, documented there the way the others are:

```csharp
/// <summary>
/// Materials, a row per material ("Aluminum 6061-T6"): <see cref="Materials.Columns"/> in SI units (not millimetres: see
/// <see cref="Materials.Unit"/>); a value not known is NaN. Text: "type" (<see cref="MaterialType"/>), and optionally
/// "category", "spec" ("ASTM B209"), "description", part numbers.
/// </summary>
public const string Material = "material";
/// <summary>Appearances, a row per finish ("Brushed aluminum"): <see cref="Materials.AppearanceColumns"/>.</summary>
public const string Appearance = "appearance";
```

Material columns (fixed names, so every materials table has the same columns and tables merge across layers by key):

| Column | Unit stored | Shown in MMGS / IPS |
| --- | --- | --- |
| `density` | kg/m³ | g/cm³ / lb/in³ |
| `modulus` (Young's) | Pa | GPa / Msi |
| `poisson` | — | — |
| `shear` | Pa | GPa / Msi |
| `yield`, `tensile` | Pa | MPa / ksi |
| `elongation` | fraction | % |
| `hardness` (scale in text, "hardnessScale": "HB") | — | — |
| `expansion` | 1/K | µm/(m·K) / µin/(in·°F) |
| `conductivity` | W/(m·K) | W/(m·K) / BTU/(h·ft·°F) |
| `specificHeat` | J/(kg·K) | J/(kg·K) / BTU/(lb·°F) |
| `r`, `g`, `b`, `metallic`, `roughness`, `opacity` | 0-1 | — (a swatch) |

- Only `density` is required; anything else may be NaN ("not given": `Serialization.Options` already allows named
  floating-point literals). The appearance columns are inline in a material row, so a snapshot carries its look too.
- `LibraryTable.Scale(column)` is length-only today. Materials need a unit per column: a `Materials.Unit(column, UnitSystem)`
  (factor and label) the editor and the dialogs use, instead of `Units = "in"`. A materials table's `Units` stays null
  (values are SI; the document's units decide how they are shown).
- `MaterialType` enum (Steel, StainlessSteel, CastIron, Aluminum, CopperAlloy, Titanium, Nickel, Magnesium, Zinc, Plastic,
  Elastomer, Composite, Wood, Ceramic, Glass, Other) is stored as the "type" text, so an unknown one from a newer
  library is shown as Other, not refused.
- `Materials` (Core/Library/Materials.cs) is the typed view over a row or snapshot: `Density`, `Modulus`, …, `Appearance`,
  `Type`, as `Fasteners` is over screw rows.

## Built-in materials (`BuiltInMaterials`)

- C# tables like `BuiltInStandards`, a fixed `LibraryId`, `Verified = false`, `Standard` = the source ("typical values").
  Added to `LibrarySet`'s bottom layer beside `BuiltInStandards` and `FastenerFamilies`; written into `standards.sprlib`
  when an admin creates a library folder (`LibraryFile.WriteBuiltIn`).
- One table per family so a company can override or hide a family: `Materials-steel`, `Materials-stainless`,
  `Materials-iron`, `Materials-aluminum`, `Materials-copper`, `Materials-titanium`, `Materials-other-metals`,
  `Materials-plastics`, `Materials-elastomers`, `Materials-composites`, `Materials-wood`, `Materials-ceramics-glass`.
- About 40 rows: the 12 there today (same names, so old parts match) plus 1018/1045/4140/A36 steel, 304/316/17-4 stainless,
  6061-T6/7075-T6/5052-H32/A356 aluminum, C360 brass, C110 copper, bronze, gray iron, Ti grade 2, Inconel 718, AZ31,
  zinc, HDPE, PP, PETG, PEEK, PTFE, acrylic, nylon 12, TPU, natural rubber, CFRP, FR-4, oak, pine, glass, alumina; a fastener
  grade or two (property class 8.8 steel, A2 stainless).
- `Appearances` table (kind `appearance`): the presets the Appearance dialog offers (polished/brushed steel and aluminum,
  brass, copper, chrome, black oxide, black anodize, matte/gloss plastic in several colors, clear and smoked glass).
  A company library can add its own (brand colors) or hide some, by the same restrictions.
- Row keys and table keys never change meaning (parts' snapshots name them), as for the standards.

## On the part

The material is a library item like a thread size: `CadDocument.MaterialItem` ("Materials-aluminum/Aluminum 6061-T6"),
its row a `LibrarySnapshot` in `CadDocument.Library`.

```csharp
/// <summary>The part's material: a library item ("Materials-aluminum/Aluminum 6061-T6") whose row is copied into
/// <see cref="Library"/> (null: none set; steel's density assumed).</summary>
public string? MaterialItem { get; set; }
/// <summary>The part's own appearance, set apart from its material's (null: the material's, or the default grey).</summary>
public Appearance? Appearance { get; set; }
```

- `LibraryLink.Sync` counts `MaterialItem` as used (beside the features' `LibraryItems()`), so its snapshot is added when
  a material is picked and removed when it is cleared. `LibraryLink.Updates` / `Accept` then cover materials with no change;
  `Accept` has nothing to resize for a material, only the snapshot (and the display: density and appearance).
- **A custom material** (a density typed in Mass Properties, or Edit Material > Custom) is a snapshot with no library:
  item `Part/<name>`, `LibraryId = Guid.Empty`, never offered for update, and not removed by `Sync` while `MaterialItem` names it.
- `Doc.EffectiveAppearance` = `Appearance ?? material's appearance columns ?? default grey`;
  `Doc.EffectiveDensity` = material's `density ?? 7850`. The ~10 readers of `doc.Color` / `doc.Density` (thumbnails,
  previews, revision previews, display cache, `PartDisplay`, assembly mass, BOM) move to these; `Color`, `Material` and
  `Density` go, kept only as legacy read/write in storage.
- **Storage:** `MaterialData` becomes `{ MaterialItem, Appearance }` plus the legacy `Color/Material/Density` still written
  (from the effective values) so an older SpryCAD opening a newer part sees the right color and density. The envelope stays
  `v: 1` (unknown members are ignored when read).
- **Old parts:** a `Material` name is matched to a built-in row by name (the 12 presets keep their names); a `Density`
  different from the match, or a name with no match, makes a custom snapshot with that name and density. A `Color` other
  than the default grey becomes the part's own `Appearance`. Done when the part is read, so it is saved the new way next time.
- **Fasteners:** `Fasteners.Part` sets `MaterialItem` from the family's text ("material": "Materials-steel/Steel, class 8.8"),
  and no `Color`, so fasteners weigh and look like their grade.
- Later (not in this plan): material per configuration / from a design table column.

## Appearance in the viewport

- `Appearance` record (Core/Model): `Color` (RGB 0-1), `Metallic`, `Roughness`, `Opacity`, optional `Name` (the preset's).
- The lit shader gets `uMetal` and `uRough` beside `uColor` (alpha = opacity):
  - specular exponent from roughness (e.g. `mix(200, 8, rough)`), strength from roughness and metallic;
  - a metal's specular tinted by its base color and its diffuse darkened (`mix(base*d, base*d*0.35, metal)`);
  - a simple fake environment for metals (sky/ground gradient sampled by the reflected view vector) so polished metal does not
    look like flat grey plastic. No textures, no environment maps.
- `MeshInstance` carries an `Appearance` instead of `Rgba`; the display cache (`FCMESH4`) gains two floats
  (`FCMESH5`; older caches are rebuilt, as they are today when the version differs).
- Opacity < 1 uses the existing blended pass, for parts as well as assembly components.
- Color by Part / Color by Feature still override the color only (metallic/roughness set to a neutral plastic).

## UI

- **Part tree:** a "Material" row under the part row: "Material <not specified>" or the material's name.
  Right-click it (or the part row): **Edit Material…**, **Remove Material**, recently used materials,
  and a mark when Library Updates has a change for it. Double-click opens Edit Material.
- **Edit Material dialog** (modeless `ToolWindow`, like Mass Properties): a picker over `LibrarySet.Offered(TableKinds.Material)`,
  so a company's restrictions apply as they do in the Hole Wizard:
  - left: search box, then a tree Type > Material (the library a row comes from shown greyed beside it);
  - right: the row's properties in the document's units (`Materials.Unit`), an appearance preview, spec and source,
    "unverified" when its table is;
  - **Apply** (one `Session.Change`, one undo), **Close**, **Custom…** (a custom snapshot, starting from the selected row),
    **Edit in Library…** (opens the library editor at that table, for library owners), "Use the material's appearance".
- **Mass Properties window:** the material combo is replaced by the material's name and an **Edit Material…** button;
  typing a density there still works and makes a custom material ("Steel (plain carbon), custom density").
- **Appearance dialog** (one dialog for a part, its Options page, and an assembly component): color (RGB/hex + swatch),
  Metallic, Roughness and Opacity sliders, the presets from `Offered(TableKinds.Appearance)` (replacing the 12 swatches),
  and **Material's appearance** to clear an override. Components: `Component.Color` / `ComponentOverride.Color` become
  `Appearance?` (old files read as an RGB appearance).
- **Library editor:** material and appearance tables use `Materials.Unit` for their headers and values, show a swatch for the
  `r,g,b` columns (click to pick), and a type drop-down for the "type" text. CSV import/export works as for other tables
  (header in the shown units), which is how a company brings in a materials list from a spreadsheet.
- **Command palette / menus:** Edit Material…, Appearance…; materials are reached in Tools > Libraries like the rest.

## Assemblies and BOM

- `PartDisplay` carries the effective appearance and density; assembly mass already sums per-part densities.
- BOM: Material column shows the material's name (or "<not specified>", steel assumed for mass); add **Mass** (each) and
  an optional **Total mass**, in the assembly's units, and in the CSV. Fasteners keep their designation names.
- Assembly Mass (`AssemblyMassCommand`): shown in the assembly's units, not always g/mm, and lists parts with no material.

## STEP (last)

- Export: each part's color as `STYLED_ITEM` / `COLOUR_RGB` (and transparency) so other CAD shows it; material name and
  density as `PRODUCT` properties where AP242 allows.
- Import: read colors into each part's `Appearance`; a material name, if present, matched to the library by name.

## Phases

1. **Table kinds and built-in materials**: `TableKinds.Material/Appearance`, `Materials` (columns, units, typed view),
   `BuiltInMaterials` in the bottom layer, the Mass Properties combo reading `Offered(material)`.
   Tests (in `LibraryTests` style): every built-in row has a positive density and the fixed columns; a company table
   overrides a row and restricts the offered list; `Materials.Unit` round trips MMGS/IPS.
2. **The part's material**: `MaterialItem`, `Appearance`, custom snapshots, `Sync` counting the material, effective
   density/appearance and their readers, storage + old-file mapping, fasteners' grades. Tests: round trip; an old part
   (name + density, color) opens with the same mass and color; a library change shows in `Updates` and `Accept` changes mass;
   a custom material survives `Sync`. Fix `StorageTests.cs:102` density 2.7 to a kg/m³ value.
3. **Material UI**: tree row, Edit Material dialog, Mass Properties change, library editor units and swatches.
4. **Appearance**: shader metallic/roughness, the Appearance dialog for parts and components, appearance presets table,
   display cache bump, component `Appearance` overrides.
5. **BOM mass, assembly mass units, STEP colors.**

Each phase builds and ships on its own; phase 1 changes nothing a user sees except the longer material list.

## Open questions

- **Merge order**: this needs `libraries-fasteners` (its `Library` namespace, snapshots, editor). Base the branch on it, or wait.
- **One table per family or one big table**: per family lets a company hide a family; one table is simpler to CSV.
  Plan: per family.
- **Appearance inline in material rows, or a reference to an appearance row**: inline keeps the snapshot self-contained
  (plan); a reference would let one change to "Brushed aluminum" restyle every aluminum.
- **Per body / per face appearance**: not covered; a part has one appearance.
- **Thermal columns**: included now (cheap, NaN when unknown) though nothing uses them until there is analysis.

# Sheet metal: gap analysis and plan

Status: draft for review, against the spec's "Sheet metal" tab (Oct 11, 2026; "tab §n" below) and the main spec sections it relies on.

## Progress (branch `sheet-metal`, cut from `libraries-fasteners`)

Phases 0 and 1 are done except where noted. None of it is committed yet.

- **Removed:** the Flat Pattern feature, Flatten, `ModelState.Flats`, `SheetMetalSettings`, the Sheet Metal ribbon tab, menu, Options page and DXF export command. `SheetUnfold` and `DxfWriter` stay in the kernel for phases 2 and 5.
- **Projects:** `SpryCad.SheetMetal.Core`, `.Brep`, `.Features`, `.Export` and `tests/SpryCad.SheetMetal.Tests` are in the solution. Brep and Export are empty so far.
- **Extension points:** `FeatureRegistry` in `SpryCad.Core/Model` registers a feature type's JSON name and its rebuild handler. The regenerator's switch falls through to it. `SheetMetalModule.Register()` runs at app startup and registers nothing yet.
- **Shop profile:** `ShopProfileTable` (object kind 42 in `.sprlib`), `ShopProfile.Find` (most specific row wins; misses and ties are errors), `SheetRules.Resolve(profile, material, gauge, overrides)`, `RuleExpr` (expressions in t and R with mm/in/deg), `ProfileCsv`, and `StarterProfile`. `LibrarySet.Profile` is the last library's profile, whole, or else the starter profile. The library editor keeps a library's profile tables when it publishes.
- **Core:** `BendMath` (K-factor, bend table, bend deduction, DIN 6935, OSSB, ISSB, BD, flange references), `BendGraph` (tree check and exact fold), and geometry types.
- **Tests:** 19 in `tests/SpryCad.SheetMetal.Tests`, all passing. They cover hand-calculated bends by every method and reference, interpolation limits, lookups and misses, CSV, a library round trip, and fold-and-remeasure for 1–10 bends.

Differences from the structure below:
- **Profile tables live in `SpryCad.SheetMetal.Core`**, not in `SpryCad.Core/Library`. `SpryCad.Core` references the sheet metal Core, which has no references of its own. This keeps lookup and resolution testable without SpryCad.Core.
- **Tables are keyed by gauge name** ("16 ga"), not by thickness. The gauge table gives the thickness.
- **The bend table's interpolate flag** is a property of the table and isn't in the CSV. The importer sets it.

### Phase 2 (done, not yet run in the app)

- **Part objects:** `SheetMetalBody` (kind 9), `SheetMetalOverride` (kind 10) and `ProfileSnapshot` (kind 11, one per part). History describes changes to them.
- **Profile snapshot:** holds every profile row whose material and gauge keys match the part's sheets (`*` included). A lookup over the snapshot therefore gives exactly what the full profile gives, and the part rebuilds the same with no library.
  - Synced on every edit (`DocumentSession.EndChange`): made when a material or gauge is new to the part, dropped with the last body. When it is remade, it takes the profile's current rows.
  - On open, a changed profile is taken in as one undoable change, with the differences in the status bar.
- **Features** (in `SpryCad.SheetMetal.Features`, registered at startup):
  - **Base Flange:** a plate from a closed sketch (straight edges for now, holes allowed), or a bent profile from open lines. The lines are the top side, with material on their left; a left turn bends down, a right turn up.
  - **Edge Flange:** one or more edges, each flange the whole edge long. Length measured to OML, IML or the tangent; bend angle; up or down. Position: material inside (trims the panel by OSSB), material outside (ISSB), or bend outside. Per-bend overrides come from `SheetMetalOverride`.
- **Folded solid** (`SheetMetal.Brep/FoldedSolid`): built face by face (planes, and cylinders for bend zones), stitched by construction, and valid. Faces are named `P.Top`, `P.Bottom`, `P.Edge(n)`, `B.Inside`, `B.Outside`, `B.End0/1`. **Flatten** (`SheetMetalBody.Flattened`) builds the flat solid in place, with the fixed panel staying put.
- **Flat pattern** (`FlatPattern2D`): outline loops, bend lines and notes, cut length and pierce count, and the bend table. **Export** (`SheetMetal.Export/FlatExport`) writes DXF and SVG with the `sm.output` layers and bend note format, the bend table under the part, and the same bytes every time.
- **App:**
  - **Ribbon:** a Sheet Metal tab with Base Flange, Edge Flange, Flatten and Export Flat; also in the Insert menu and the command palette.
  - **Property pages:** Base Flange picks material and gauge from the profile; Edge Flange picks edges by their side faces or by the edges themselves.
  - **Library editor:** the **Shop Profile** button imports or exports each profile table as CSV (mm or inches), or starts from the starter profile.
- **Tests:** 8 more in `tests/SpryCad.SheetMetal.Tests`, 27 in all, all passing. They cover the plate, flange positions and dimensions, opposite flanges and a flange on a flange, the bent profile's bounding box, a save/load round trip with byte-identical objects, stable ids and flat layout after an unrelated edit, flat export, and snapshot sync and update.

Limits for now (later phases):
- **Corners:** flanges on edges that meet are refused until phase 3 adds corners.
- **Base flange shape:** the outline is straight lines only (no arcs or splines).
- **Edge flange width:** always the whole edge (no width or offsets).
- **Inside radius:** must be more than 0.
- **Fixed panel:** `SheetMetalBody.FixedPanel` re-roots the flat but has no UI yet.
- **Snapshot granularity:** remaking the snapshot for a new gauge refreshes all its rows from the current profile.

## In short

The tab replaces today's approach rather than extending it. Today SpryCAD **recovers** sheet metal from a solid: it models ordinary features, then unfolds the result. The tab **generates** both the folded solid and the flat pattern from a stored bend graph, with every shop value coming from a shop profile library. Today's unfolder becomes the start of the tab's recognizer (tab §11), not the core of the module.

## What exists today

| Area | Where | What it does |
| --- | --- | --- |
| Unfold | `src/SpryCad.Kernel/Ops/SheetMetal.cs` (`SheetUnfold`) | Recognition-style: walls move rigidly and bends unroll about a fixed planar face. Finds the thickness from the geometry. Handles Z bends, holes across bends, notches, and shelled, filleted or imported bodies. |
| Bend allowance | `BendRule` in the same file | K-factor and DIN 6935. No bend table, bend deduction, OSSB/ISSB or flange references. |
| Flat output | `FlatPattern` | 2D outline regions, bends (centerline, angle, inner radius, up/down, allowance, tangent lines clipped to material) and a flat solid. |
| Profile helper | `SheetProfile.Thicken` | Thickens an open chain into a bent cross section. Only the tests use it. |
| Feature | `FlatPatternFeature`, `DoFlatPattern` (`Regenerator.cs`) | Optional fixed face, method and K-factor overrides, and Flatten. Works on **any** body. |
| Part setting | `SheetMetalSettings` on `CadDocument.SheetMetal` | Method and K-factor expression, stored in the single `Settings` object (`ObjectKind.Settings` = 5). |
| UI | `MainWindow.Commands.cs`, `OptionsDialog.cs` | Insert > Sheet Metal > Flat Pattern…, Flatten; a Sheet Metal ribbon tab; Export Flat Pattern DXF…; a document options page. |
| DXF | `src/SpryCad.Kernel/IO/DxfWriter.cs` | R12 ASCII with true arcs and circles. Fixed layers OUTLINE, BEND_UP and BEND_DOWN. No bend notes or bend table. |
| Tests | `tests/SpryCad.Tests/SheetMetalTests.cs` | Unfold cases, DIN 6935, the feature with overrides and save/load. |

Main spec dependencies the tab assumes:

| Main spec | State |
| --- | --- |
| §4 object store and revisions | Exists (`PartObjects`, `PartDatabase`). |
| §34 libraries, snapshots and `LibraryRef`s | On `libraries-fasteners`, not merged. Tables are numbers plus text; no expression values. |
| §11 cache keys | Regeneration cache keys exist (`Regenerator.cs`). No named cache kinds such as `FlatPattern`, and no per-module version salt. |
| §27 design rules, `RuleResults`, model CI, `check` CLI | **Not started.** |
| §40 unit-aware parameters | Expressions exist. The model is in **mm**, while tab §2 says Core works in SI metres. |
| §43 cost, §22 release package, §23 visual diff, §28 mass properties | §43 not started; check the others before phase 6. |
| §25 holes and fasteners | On `libraries-fasteners`. |
| Persistent face naming | `FaceTag` names in the kernel. Tab §10 wants names like `Panel(id).Top` and `Bend(id).Inside`. |

## Gap by tab section

| Tab § | Requires | Gap |
| --- | --- | --- |
| 2 Architecture | Four projects: `SheetMetal.Core` (no B-rep), `.Brep`, `.Features`, `.Export`. Deterministic; one tolerance per part; own tests and benchmarks. | No projects. There's no "Format" project for Export to reference: today export is in `SpryCad.Kernel/IO`. Needs a 2D polygon library in Core (offset, union, boolean) with exact arcs. |
| 3 Shop profiles | A `.sprlib` with Materials, Tooling, Allowance, Corner, Relief, Hem, Distance, Flat output and Conventions tables. Expression values; explicit misses; interpolation only when enabled; overrides; switch with a diff; a starter profile. | `LibraryTable` holds numbers and text only. **Decided:** sheet metal rules get their own table type (a `ShopProfileTable` in the same `.sprlib`) with expression cells, multi-key lookup (material × thickness × tooling) and range rules. `LibraryTable` stays as it is. The profile switch-and-diff and compliance report are new. |
| 4 Data model | `SheetMetalBody` objects, one object per feature, corner and hem override objects; a cached `BendGraph`; a tree rooted at the fixed panel; persistent ids. | All new. `SheetMetalSettings` in `Settings` becomes `SheetMetalBody`. |
| 5 Bend math | K-factor, bend table, bend deduction, and DIN 6935 from the standard itself; OML, IML and tangent references, chosen per flange (no >90° switch); deflection angles; `AllowanceSource`. | K-factor and DIN 6935 exist. DIN 6935 should be re-checked against the standard's own examples, as the tab asks. The rest is new. |
| 6 Features | Base, edge and miter flange, hem, jog, sketched bend, cut, hole, corner edit, convert. Flat-state sketching. | None. |
| 7 Corners and reliefs | Corner entities found from the graph; Open, Closed, Overlap A/B and Miter treatments; bend and corner relief types sized by rule. | None. |
| 8 Hems | Closed, open, teardrop and rolled, with hem allowance tables; a closed hem has a modelling gap. | None. |
| 9 Rules | 13 SM rule types as §27 design rules, incremental, with one-click fixes. | §27 doesn't exist. The SM checks can be written in Core against the graph first and plugged into §27 later. |
| 10 Outputs | Folded solid built face by face (no booleans) with graph-derived face ids; flat pattern oriented by the fixed panel and grain; bend table; DXF and SVG by profile mapping; visual diff and release callouts; cost inputs. | `DxfWriter` is the base (true arcs already). No SVG, no profile mapping, no bend table, and no direct B-rep builder from planes and cylinders. |
| 11 Recognition | "Looks like sheet metal" banner; thickness pairing and map; edge classification; per-face report; conversion with OML/IML/mid preservation, rips, and the original kept in history. | `SheetUnfold` already does thickness finding, wall/bend classification and unrolling. It needs to report per face instead of throwing on the first problem, handle sharp edges and rips, and output a graph instead of a flat. |
| 12 Storage, API, CLI | Cache kinds, `SheetMetalVersion`, `ISheetMetalBody`, `ISheetMetalEditor`, `ISheetMetalRecognizer`, `flat` CLI, 14 acceptance tests. | No CLI exists today. Check the test project before adding `flat` and `check`. |

## Conflicts with today's behaviour

**Decided: no backwards compatibility.** Old part files with sheet metal data don't need to open or migrate.

1. **Flat Pattern on any body.** Tab §11: flat pattern, bend table and DXF flat export "exist only for sheet metal bodies". **Remove** `FlatPatternFeature`, Flatten, `DoFlatPattern` and `ModelState.Flats`, along with their commands and the Options page. To flatten a shelled or imported solid, you convert it first (phase 5).
2. **Units.** Tab §2 says Core works in SI metres. The rest of SpryCAD (kernel, libraries) is in mm. Proposal: Core in mm like the rest, documented as a deviation. Mixing scales across the B-rep adapter invites tolerance bugs.
3. **Settings object.** **Remove** `SheetMetalSettings` and `CadDocument.SheetMetal` from `Settings`, with no migration. Their values live on `SheetMetalBody` and the shop profile.
4. **Module placement.** Today's sheet metal code lives in `SpryCad.Kernel`. The tab wants a Core project that doesn't reference the B-rep kernel. The 2D and bend math move out; the unfolder moves to `SheetMetal.Brep`.

## Recommendation

Follow the tab's architecture: a separate part type, sheet metal bodies and a bend graph. The tab settles the question my first draft left open. Two choices still keep the work down:

- **Same part file, new object kinds.** A part with a `SheetMetalBody` object is a sheet metal part, and the tree, commands and icons follow from that. No new file extension. This matches tab §12 ("uses the main file format unchanged").
- **The shop profile is a §34 library**, built on `libraries-fasteners`, with its own `ShopProfileTable` type beside `LibraryTable`. It uses the same layers, snapshots, update flow and editor. That gives a gauge/thickness table plus tooling and allowance tables per shop, and the library editor's CSV import covers the tab's "fast way to import each shop's tables". Choose the new object kind numbers away from the branch's known clash (kinds 40 and 41 fall in the range the assembly test reserves).

## Planned structure

### The consistency rule

> Sheet metal rules are the same for every part of the same material and thickness, unless the user has overridden them on a particular part (sheet body), bend, corner or hem.

The structure follows from that rule:

- The rules that apply to a sheet are a **pure function** `Resolve(profile revision, material, thickness)`. Bend angle is added only for bend table lookups. Nothing about the part, its history or the feature that made a bend enters the lookup.
- A part stores **only** its material, its thickness, which profile it uses, and its explicit overrides. It stores no copied rule values, apart from the §34 snapshot (below).
- Tooling is not a per-part choice. The profile names one default tool set for each material and thickness, so the inside radius is the same everywhere. Choosing another tool set on a bend is an override.
- The flange length reference (OML, IML or tangent) is a dimensioning option on each flange, not a rule. It changes where the dimension measures to, not the bend.
- Overrides are explicit objects, listed in the UI and in the compliance report. Anything not overridden comes from the profile.

### Shop profile (in a `.sprlib`)

One new library object kind, `ShopProfileTable` (one object per table, beside `LibraryTable` and `LibraryRestrictions`). The data types live in `SpryCad.Core/Library`, so `LibraryFile` reads and publishes them like the other tables.

```csharp
public sealed class ShopProfileTable
{
    public string Key { get; set; } = "";          // "sm.tooling"
    public string Title { get; set; } = "";
    public string? Units { get; set; }             // null = mm, "in": shown and typed in inches, stored in mm
    public List<string> KeyColumns { get; set; }   // e.g. ["material", "thickness"]
    public List<ProfileColumn> Columns { get; set; }
    public List<ProfileRow> Rows { get; set; }
    public bool Interpolate { get; set; }          // bend tables only: linear within range, never beyond
}
public sealed record ProfileColumn(string Name, ProfileValueKind Kind);   // Length, Angle, Ratio, Number, Text, Enum, Bool, Expr
public sealed class ProfileRow
{
    public List<string> Keys { get; set; }         // one per key column; "*" matches any value
    public List<string> Cells { get; set; }        // one per column; numbers stored in mm and degrees, invariant;
                                                   // Expr cells hold expressions in t, R, θ ("max(t, 0.5mm)")
}
```

Lookup: the most specific row wins. An exact material and thickness beats `material, *`, which beats `*, *`. Two rows equally specific is a publish error. No row is a miss, reported as SM-TOOL-01. There is no fallback to built-in values.

Tables, with key columns in brackets:

| Table key | Keys | Columns |
| --- | --- | --- |
| `sm.materials` | [material] | density, grain sensitive, notes |
| `sm.gauges` | [material, gauge] | thickness, thickness tol + / − (for section 20's LMC check). Gauge is a name ("16 ga", "2.0 mm"). |
| `sm.tooling` | [material, thickness, toolset] | default (bool: exactly one per material and thickness), punch tip radius, punch angle, die V-width, die angle, die shoulder radius, inside radius, min flange length, max tonnage per metre |
| `sm.allowance` | [material, thickness, toolset] | method (KFactor, BendTable, BendDeduction, Din6935), K |
| `sm.bendtable` | [material, thickness, toolset, angle] | BA or BD (method from `sm.allowance`) |
| `sm.corners` | [material, thickness] | default treatment, gap (Expr), allowed treatments, zero gap allowed |
| `sm.relief` | [material, thickness] | bend relief type, width, depth (Expr); corner relief type, size, min sliver (Expr) |
| `sm.hems` | [material, thickness, hem type] | allowed, min/max length, gap (Expr), hem allowance, closed-hem model gap (starter: 0.127 mm) |
| `sm.distances` | [material, thickness] | hole-to-bend, edge-to-bend, cut-to-bend, hole-to-edge (Expr) |
| `sm.output` | [ ] (one row) | DXF layer, colour and line type per entity; bend note format; up/down convention; etch layer |
| `sm.conventions` | [ ] (one row) | default flange reference, geometric tolerance (starter: 0.001 mm) |

The angle convention is not a column, because it is fixed as the deflection angle. Each table imports and exports as CSV, with key columns first, using the library editor's CSV code and a column template per table key. The starter profile is built in, like `BuiltInStandards`, and labelled as example values.

### Part objects (in a `.sprt`)

New part object kinds in the part range (after `LibrarySnapshot = 8`):

| Kind | One per | Holds |
| --- | --- | --- |
| `SheetMetalBody = 9` | Sheet body | Body id (GUID), name, profile (library id), material key, gauge key, fixed panel (persistent id), grain direction, body-level overrides |
| `SheetMetalOverride = 10` | Overridden bend, corner or hem | Target persistent id (`Bend(…)`, `Corner(…)`, `Hem(…)`), override values by column name (expressions allowed), optional note |
| `Feature = 1` (existing) | Feature | New feature types, each naming its `SheetMetalBody`: `BaseFlangeFeature`, `EdgeFlangeFeature`, `MiterFlangeFeature`, `HemFeature`, `JogFeature`, `SketchedBendFeature`, `SheetCutFeature`, `SheetHoleFeature`, `ConvertToSheetMetalFeature` |
| `LibrarySnapshot = 8` (existing) | Profile row used | The resolved rows (`sm.tooling/CRS|1.519|T1`, …), so the part rebuilds with no library installed |

An override on a feature itself (for example an edge flange's tool set) is a property of that feature. An override on something the graph generated (a bend, corner or hem) is a `SheetMetalOverride` keyed by its persistent id, as tab §4 says. Both appear in the same overrides list in the UI. `ObjectKind.Settings` loses `SheetMetal`; there is no migration.

### Code projects

| Project | References | Contents |
| --- | --- | --- |
| `SpryCad.SheetMetal.Core` | Math only | `BendGraph`, `Panel`, `Bend`, `Corner`, `Hem`, ids; `BendMath` (BA, OSSB, ISSB, BD, flange references); `SheetRules` (the resolved, evaluated values for one material and thickness, plus overrides applied); fold and unfold; corner and relief geometry; the SM checks returning `RuleResult`s; the 2D flat pattern. Its own 2D polygon library with exact arcs. |
| `SpryCad.SheetMetal.Brep` | Sheet metal Core, Kernel | Folded solid built face by face (planes and cylinders, no booleans), face names `Panel(id).Top`, `Bend(id).Inside`, …; the recognizer (from today's `SheetUnfold`). |
| `SpryCad.SheetMetal.Features` | Sheet metal Core and Brep, SpryCad.Core | Feature classes, the regeneration handler, the profile resolver (`ShopProfileTable` rows → `SheetRules`), snapshots, the profile diff and compliance report. |
| `SpryCad.SheetMetal.Export` | Sheet metal Core | DXF and SVG from the flat pattern, using the `sm.output` mapping (today's `DxfWriter` moves here). There's no "Format" project in SpryCAD to reference. |

`SpryCad.Core` can't reference `SheetMetal.Features`, so it needs two small extension points:
- **A feature handler registry in `Regenerator`.** Today it switches on the feature type.
- **A JSON polymorphism modifier,** so feature types from another assembly serialize. Today `[JsonDerivedType]` attributes on `Feature` list them.

The app references the sheet metal projects and registers them at startup. Tests go in a new `tests/SpryCad.SheetMetal.Tests`. Its Core tests run without the kernel.

### Rebuild

For each `SheetMetalBody`:
1. Resolve `SheetRules` from the snapshot, or from the library set for rows not yet snapshotted, then apply body-level overrides.
2. Run its features in tree order. Each one edits the `BendGraph`, with bend, corner and hem overrides applied as each entity is created.
3. Run the checks.
4. Build the folded solid into `ModelState.Bodies` and the flat pattern into the model state.

The cache key is the feature hashes + the snapshot hash + the overrides + `SheetMetalVersion`.

### Keeping parts consistent when the profile changes

A part's snapshot can fall behind the profile when someone publishes a change. For holes and threads, §34 makes updates opt-in. That would break the consistency rule: two 16 ga steel parts could bend differently. Decided: a sheet metal part **takes profile updates on open** as one undoable history entry ("Shop profile rows updated: inside radius 1.5 → 2.0 mm"). Released revisions keep their snapshots, since they're never rebuilt from the library. Overridden values don't change.

## Phased plan

Each phase ends with a usable result and its tab §12 acceptance tests.

0. **Prerequisites.** Merge `libraries-fasteners`. Add the `ShopProfileTable` type to `.sprlib` (its own object kind, snapshots and editor grid) with expression cells, multi-key lookup, explicit misses and in-range interpolation. Remove today's Flat Pattern, Flatten and `SheetMetalSettings`. The unfold and DXF code stays, to be reused by phases 2 and 5. Create the four projects and the test suite. Set up the 2D polygon library (with arcs), the tolerance per part, and a determinism test harness (byte-identical output).
1. **Graph and math (Core only).** `BendGraph`, panels, bends, fold/unfold, tree check (cycles rejected), all four allowance methods, OML/IML/tangent and OSSB/ISSB/BD. Shop profile tables, lookup and a starter profile. Tests: the hand-calculated single bend in all methods and references, fold-and-remeasure for 1–10 bends, cycle rejection, missing tooling.
2. **Base and edge flange, folded solid, flat output.** `SheetMetalBody` and feature objects; Base Flange (plate and bent profile); Edge Flange (length reference, angle, direction, position, width). The B-rep builder with graph face ids; flat pattern with profile DXF and SVG mapping and a bend table. Tests: stable ids and orientation after unrelated edits; DXF layers, arcs and closed contours.
3. **Corners and reliefs.** Corner detection, the treatments, bend and corner relief, the corner preview, corner edit objects. SM-FLAT-01, SM-FOLD-01, SM-CORN-01/02, SM-REL-01 as Core checks with messages. Tests: the four-flange box; three-bend relief.
4. **More features.** Hem (all four types, hem tables, SM-HEM-01..03), Jog, Sketched Bend, Miter Flange, Cut (folded and flat, mapped across bends), Hole (linked to §25), flat-state sketching. SM-FLG-01, SM-DIST-01/02, SM-GRAIN-01.
5. **Recognition and conversion.** Rebuild `SheetUnfold` as `ISheetMetalRecognizer`: thickness map, per-face report, sharp edges to bends, OML/IML/mid preservation, rips, banner.
6. **Integration.** Plug the SM rules into §27 once it exists (model CI, history, release gates, one-click fixes). Profile switch with diff and compliance report. `flat` and `check` CLI. Flat pattern in §23 visual diff and the §22 release package. §43 cost inputs.

The later items (lofted bends, formed tools, bend sequence, rolled parts, welded multi-body) are out of scope.

## Open questions

From the tab (§12), with a suggested answer:

1. ~~**Closed hems:**~~ **Decided:** a modelling gap of 0.005" (0.127 mm) for now. It's a Hem rules value in the shop profile, so it can change later.
2. ~~**Angle convention and >90° OML rule in the starter profile:**~~ **Decided:** angles are the bend (deflection) angle, so a shallow bend is 45°. The flange length reference (OML, IML or tangent) is an option on each flange; nothing switches it automatically above 90°.
3. ~~**Profile authoring:**~~ **Decided:** CSV import for now.
4. ~~**More than one profile per part:**~~ **Decided:** one profile, the library set's, for every part. Switching shops means switching the library set's profile. A per-part switch for quoting is deferred.
5. Bend sequence and press brake checks: deferred.

Raised by this review:

6. ~~**SI vs mm in Core:**~~ **Decided:** mm internally, like the rest of SpryCAD.
7. Section 27 design rules: deferred. The SM checks return results from Core for now.
8. Multi-body: deferred.
9. ~~**Profile updates:**~~ **Decided:** a sheet metal part takes changed profile rows automatically on open, as one undoable history entry. Released revisions and overridden values don't change.

Decided (Oct 15):

- Sheet metal rules are their own table type (`ShopProfileTable`), not an extension of `LibraryTable`.
- No backwards compatibility: today's Flat Pattern feature and sheet metal settings are removed, not migrated.
- Closed hems: a 0.005" (0.127 mm) modelling gap in the folded solid; the bend graph treats them as in contact.
- Angles are the bend (deflection) angle (a shallow bend is 45°).
- Flange length reference (OML, IML or tangent) is an option on each flange, with no automatic switch above 90°. The profile only gives new flanges their default.
- Internal units are mm.
- Profile tables are authored by CSV import for now.
- Rules are the same for every part of the same material and thickness unless overridden on a part, bend, corner or hem (see "The consistency rule").
- Deferred: bend sequence, the section 27 integration, multi-body.
- One shop profile (the library set's) for every part. Parts take profile changes automatically on open, as one undoable history entry.

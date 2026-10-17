# Standards library v2

A restructure of the standards library (threads, holes, fasteners, materials). SpryCAD is not in the field yet, so
this breaks the library file format, the item keys parts store and the built-in data freely. It replaces the design
in [standards-libraries.md](standards-libraries.md) and [material-library-plan.md](material-library-plan.md), and the
"file format spec section 34" the code cites (that spec is not in the repository; this document stands in for it).

## Why

Today every kind of data is one generic `LibraryTable`: a list of numeric columns, positional values in
millimeters, optional text. What a table means is known only from its `Kind` string and from names hard-coded in the
code that reads it. That works for a dozen tables, and gets in the way of everything past that:

- **The schema is implicit.** Column names (`major`, `pitch`, `close`, `normal`, `diameter`, `depth`, `density`) are
  matched by string; a column is in degrees if its name contains "angle"; a snapshot is a material if it has a
  `density` column and a screw if its columns are exactly `d, p, dk, k, s, t, b, l`. Pitch versus TPI, counts and
  ratios have no place.
- **Tables are joined by hard-coded switches.** `HoleSizes.Companions` maps a clearance table to its counterbore and
  countersink tables; `Fasteners.TablesFor` maps it to screw, nut and washer families; the hole page parses a table's
  title for " for " to name the screw type. Adding a standard means changing code.
- **Rows are keyed by designation alone.** Inventor's thread sheet lists M8x1.25 once per class (6g, 4g6g, ...); we
  cannot hold that.
- **Inch values are stored in millimeters** and converted back for display, so a value read back from a spreadsheet
  differs in its 16th digit (2.1843999999999997 for 0.086 in) and every edit risks a spurious change.
- **Holes copy library values into their own fields** and guess whether the user typed over them by comparing with a
  freshly sized copy.
- **Built-in standards are C# code** (`BuiltInStandards.cs`), not data a reviewer can check against the standard.
- **Duplicates and quirks:** `Threads.MetricCoarse` duplicates the ISO 261 table for Auto threads; the inch socket
  head cap screw family has no `Units`; fastener families are missing from the built-in library file.

Inventor's design data (`thread.xlsx`, 26 sheets; `clearance.xlsx`, 6 sheets) shows the shape of a better model:
self-describing sheets, grouped columns, one row per size and class, one clearance row holding the screw's clearance
diameters and its head pocket together.

## Goals

1. Every table kind has a **declared schema**: its key columns, its value columns (grouped, typed, with units), which
   are required.
2. Rows have **composite keys** (designation and class; fastener type and size).
3. Values are stored **in the standard's own units**, exactly as published, and converted only where used.
4. **One record per fastener size** for holes: clearance diameters and head pocket together, by fastener type.
5. **One hole mechanism**: the built-in hole types (simple, counterbore, countersink, tapped) are library hole types
   like the ones users add.
6. **One thread record** used by the Thread feature, tapped holes, fastener models and drawing callouts.
7. **Built-in standards as workbooks** in the repository (`standards.xlsx`, `materials.xlsx`), in exactly the
   layout users edit, generated into the app at build time.
8. **Libraries by domain**: standards (threads, holes, fasteners) and materials (materials, appearances) in separate
   files, exported and imported separately.
9. **Excel is the editing surface** (ClosedXML); the `.sprlib` file stays the source of truth (revisions, layers,
   conflict-checked publishing, snapshots in parts).
10. Read **Inventor's `thread.xlsx` and `clearance.xlsx`** directly, so their data (custom threads included) comes
    across.

11. **No layering.** A library in use is either SpryCAD's defaults or a company's own library, never a merge of
    the two (see [Libraries and files](#libraries-and-files)).

Unchanged: the `.sprlib` container (a part database with revisions, conflict-checked publishing), local cached
copies of a shared library, parts self-contained through snapshots, opt-in updates.

## The model

### Schemas

A schema is declared in code per table kind. It is what makes columns typed, grouped and validated, and it is what
the workbook headings are drawn from.

```csharp
sealed record ColumnDef(string Path, string[] Heading, Quantity Unit, bool Required = false);
// Path: stable id, "external.major.max"; Heading: the grouped headings, "External", "Major Dia", "Max"
enum Quantity { Length, Angle, Pitch, Count, Number, Text, Density, Stress, ... }   // materials' SI quantities too
sealed record TableSchema(string Kind, string Title, ColumnDef[] Key, ColumnDef[] Columns, bool OpenColumns = false);
```

- **Length** values are in the table's units (mm or in). **Angle** is always degrees. **Pitch** is a length per
  thread; an inch table may give it as TPI (a `tpi` column), the schema converting. Material quantities keep their
  SI storage and inch-pound display as today (`Materials.Unit`), now declared per column instead of looked up by name.
- **Required** columns must have a value in every row; optional ones may be blank (not known).
- **OpenColumns** kinds (hole types, user tables) take any extra columns, typed by a heading suffix (`(mm)`, `(°)`).

A table carries a `SchemaVersion`, so a later schema change can migrate stored tables.

### Keys

Each schema names its key columns. The item key a part stores is built from them, with a separator that cannot occur
in a designation:

| Kind | Key | Item |
|---|---|---|
| thread | designation, class | `ASME-B1.1-UNC:1/4-20 UNC:2A` |
| fastener-holes | fastener type, size | `ISO-holes:Socket head cap screw:M8` |
| hole-type | size | `ACME-cbore:M8` |
| screw, nut, washer | designation | `ISO4762:M8x30` |
| material | name | `Steel:AISI 1018 (cold drawn)` |

(`:` replaces today's `/`, which "1/4-20 UNC" already contains; a designation holding `:` is escaped.) Class is
optional where a standard has none.

### Native units

`LibraryTable.Units` is `mm` or `in` and every length value is stored in it, exactly as typed. Readers ask for
millimeters (`row.Mm("external.major.max")`); the conversion happens there, once. Parts snapshot the row as stored,
units included.

## The kinds

### thread

One row per designation and class, as Inventor's straight-thread sheets have them:

- **Key**: designation and class, the external class (6g, 2A, 3A, 4g6g).
- **Size**: major diameter and pitch (required).
- **External**: major and pitch diameters max and min, minor diameter max and min.
- **Internal**: its class (text: 6H, 2B, 3B; blank on a row with external limits only, as Inventor's 4g6g), minor
  and pitch diameters min and max, major diameter min and max.
- **Tap drill**: diameter, and its size's name (`#7`, `27/64`) where it is a named drill.

A thread names its class (`ThreadFeature.Class`, a tapped hole's `ThreadClass`); the Thread page and a tapped
hole's page pick it from the size's rows. A part from before classes is given its size's first class when read.
Inventor's thread depth and runout columns are left out (they are defaults of a hole, not of the thread). Taper threads (NPT, BSPT, ISO and DIN taper) have different columns (handtight engagement, wrench
makeup, vanish thread, taper angle) and are a kind of their own, **taper-thread**; nothing reads them yet.

Readers: the Thread feature (sizes; Auto picks the size of its table fitting the face, a shaft by its major
diameter, a hole by its tap drill), tapped holes (tap drill, pitch), fasteners (major diameter). Drawings
(designation and class: "1/4-20 UNC-2B") are to come.

**SpryCAD's own thread tables** are filled from the standards' formulas by `SpryCad.LibraryGen threads
data/library/standards.xlsx` (`ThreadFormulas`), from each row's designation, major diameter and pitch:

- **Unified (ASME B1.1), 2A/2B and 3A/3B**: every limit, by the standard's tolerance formulas (length of engagement
  the major diameter; 3A without allowance and with 0.75 times 2A's pitch diameter tolerance, 3B 1.3 times that),
  the tolerances rounded to 0.0001 in before the limits are worked out as its tables are; the 2B minor diameter's
  maximum from 1/4 in up only (below, the standard has a rule of its own, not reproduced), 3B's by its formula within
  its limits. The tap drill is the standard drill nearest a 75% thread. Against an installed Inventor's thread.xlsx,
  9 of 1105 cells to 1 in differ, by a step in the last place, below #2, or by Inventor's own errors (a test, skipped
  where Inventor is not installed).
- **ISO metric, 6g/6H**: the limits that follow from the basic profile and the g deviation (the external maxima,
  the internal minima). The others depend on ISO 965-1's tabulated tolerances, which its formulas (with R40
  rounding) reproduce for most pitches but not all: against Inventor's values, some tolerances at P = 0.5, 1.5,
  2.5, 4 and 5 differ by 8 to 25 µm. So they are left blank rather than near. The tap drill is D - P.

No Inventor data is copied into SpryCAD: a company that has Inventor imports its spreadsheets into its own library.

### fastener-holes

Replaces the clearance, counterbore and countersink kinds and `HoleSizes.Companions`. One table per standard (ISO,
ASME inch, DIN, JIS ...), one row per fastener type and size, as Inventor's clearance sheets:

- **Key**: fastener type (socket head cap screw, flat head screw, hex bolt ...), size.
- **Shaft**: close, normal, loose clearance diameters.
- **Head**: head type (none, counterbore, countersink), pocket diameter, depth or angle.
- **Parts**: the screw, nut and washer families it goes with (table keys), replacing `Fasteners.TablesFor`.

The Hole Wizard's Standard, Fastener and Size are this table, its fastener types and its rows, with no title
parsing; the fastener types offered are those whose rows suit the hole type (a counterbore needs a row with a
counterbore pocket, a countersink one with a countersink; a simple hole takes any row's clearance).

The built-in tables are `ISO-holes` (ISO 273 clearances; socket head cap screws with ISO 4762 counterbores and ISO
4032 nuts and ISO 7089 washers; flat head screws with ISO 10642 countersinks) and `ASME-holes` (ASME B18.2.8; socket
head cap screws with B18.3 counterbores; flat head screws with B18.6.3 countersinks). A size with no pocket in its
standard has its clearances only (the pocket cells blank).

### hole-type

A hole's shape: a profile sketch (half the hole in section, as `HoleProfile`) whose dimensions use named parameters,
and how those parameters are filled. The built-in types are library rows too:

| Hole type | Parameters from |
|---|---|
| Simple | D: a fastener-holes row's clearance (by fit), or typed |
| Counterbore | D, CbD, CbDepth: the row's clearance and head pocket |
| Countersink | D, CsD, CsAngle: the row's clearance and head pocket |
| Tapped | D: a thread row's tap drill; thread depth |
| (user types) | their own table's columns, a row per size (today's hole types) |

The near side, under head and far side countersinks and Clear material above stay options of the hole feature for
the built-in types. A user type draws what it needs.

(Phase 2 keeps the three built-in types in code, their parameters from the fastener-holes row as above; making them
library rows waits for tapped holes in phase 3, which add the fourth.)

### screw, nut, washer

The part families (today's `FastenerFamilies`) with declared columns (d, p, dk, k, s, t, b, l ...) instead of being
recognized by their column sequence. The inch family gets its units.

### material, appearance

Materials keep their SI columns and per-column display units, declared in the schema with groups (Physical,
Mechanical, Thermal, Appearance), so a material row's look is grouped under an Appearance heading rather than mixed
in. Appearances not tied to a material stay their own table. Colors are shown in workbooks as hex (`#B4B9C0`) with
the cell filled in that color.

## Libraries and files

- **Defaults or a company library, no merging.** SpryCAD ships its defaults built in. A company that customizes
  makes its own library, starting as a copy of the defaults (New Company Library), and uses it instead: the library
  in use is one or the other, never layered. This replaces today's layers (built-in, standards, part families,
  company), the row-by-row merge of tables with the same key, and restrictions: a company removes the sizes it does
  not want from its own copy (parts already using them keep their snapshots).
- **Domains.** A `.sprlib` names its domain in the manifest: `standards` (threads, holes, fasteners) or `materials`
  (materials, appearances). Each domain is chosen on its own (a company may keep SpryCAD's threads and use its own
  materials), set in Options or the shared `libraries.json` as now. The Library Editor opens one domain; exports are
  per domain.
- **Stored in our format, edited as spreadsheets.** A company library is a `.sprlib` (revisions, history,
  conflict-checked publishing); Excel is how it is edited: export, edit, import, publish, each publish a revision.
  The defaults are built from workbooks in the repository and compiled into the app; New Company Library writes
  them out as a company's first `.sprlib`. No `.sprlib` is committed (it is generated output, and binary).
- **Compare with SpryCAD defaults** (later): a company library does not take SpryCAD's corrections and additions on
  its own; this lists the rows the defaults added or changed since the copy was made, for the owner to take one by
  one.
- **FormatVersion 2.** Tables are stored as today (one object per table, JSON), now with their schema version and
  native units. A version 1 library is refused with a message saying to recreate it from the built-in data.
- **Built-in data** moves from C# into two workbooks in the repository, `data/library/standards.xlsx` and
  `data/library/materials.xlsx`, laid out exactly as an export (below), so the defaults and a company library are
  edited the same way. Not CSV: a CSV opened and saved in Excel loses its text cells ("1/4" becomes a date, decimal
  commas in some locales), and it has no merged headings, cell fills or sheets.
- **Generated at build.** A small tool, `tools/SpryCad.LibraryGen`, reads the workbooks before `SpryCad.Core` is
  compiled, checks every sheet against its schema (a bad cell fails the build, naming its sheet and cell), and writes
  the tables as JSON that Core embeds. The app never parses Excel at startup.
- **Readable diffs.** The same tool prints a workbook as text; a `.gitattributes` diff driver uses it, so `git diff`
  and reviews show the cells that changed rather than "binary files differ".
- **Projects.** The library's data model, schemas and workbook reading and writing move into their own project,
  `SpryCad.Library`, which depends on nothing else of SpryCAD (only ClosedXML), so both the generator and Core can use
  it without a cycle. Core keeps what uses the library (holes, threads, fasteners, materials, snapshots in parts). A
  hole type's profile is held there as its JSON text, read into a sketch by Core.

## Workbooks

One sheet per table, describing itself (no index sheet; a sheet copied to another workbook still reads):

```
row 1    Table | ISO-metric | Kind | thread | Units | mm | Standard | ISO 965-1 | Verified | no
row 2    ISO metric screw threads                                (title)
row 3+   grouped headings, merged across their columns:  External | Internal
                                                          Major Dia | Pitch Dia | ...
last     column headings with units:  Designation | Class | Max (mm) | Min (mm) | ...
then     a row per size and class
```

- Columns are matched by their heading path, so they can be reordered; a heading not in the schema is refused (or
  taken as an extra column, for open kinds).
- Reading checks every cell and refuses the whole workbook on any problem, naming its sheet and cell; otherwise it
  shows what changes before applying (as the `xlsx-tables` branch does now).
- Two exports: **Standards** (threads, taper threads, fastener holes, hole types, fastener families) and
  **Materials** (materials, appearances).
- Design tables keep their own workbook (equations, a sheet per table), unchanged by this.

### Inventor import

Read `thread.xlsx` and `clearance.xlsx` as Inventor lays them out (A1 units and type code, B1 title, three heading
rows, the family name and units in a cell far right) and map each sheet onto our schemas: straight-thread sheets to
thread tables, taper sheets to taper-thread tables, clearance sheets to fastener-holes tables. Tolerated: decimal
commas ("M1,6"), cells holding a space, blank limits, pitch given as a fraction of an inch.

Done (`InventorWorkbook`, Library Editor > Import from Inventor): a thread sheet is a table, a row per designation
and class as the sheet has them; TPI is read as the pitch in the sheet's units; clearance head type 1 is
a counterbore (its depth), 2 a countersink (its angle). Columns no row fills are left out; rows a table cannot take
(a designation listed twice) are noted. The fastener families of an imported fastener holes table are left for the
library's owner to fill in.

## Parts

- **Snapshots** keep the whole row as stored (all columns, native units, schema version), keyed by the new item key.
- **Holes resolve their values at rebuild** from their snapshot (fastener-holes row and fit, or thread row, and hole
  type), as threads and fasteners already do, instead of copying them into their diameter fields. A value the user
  types over is stored as an **override** of that parameter, explicit, not inferred by comparison. Library updates
  change every value not overridden. (`HoleFeature.Overrides`, by field: Diameter, CbDiameter, CbDepth, CsDiameter,
  CsAngle; `HoleSizes.Effective` makes the hole as built. The page says which values are typed over, and **Use
  Library Values** drops them; changing the size or fastener drops them too, the fit the diameter's.)
- **Material tables renamed**: SpryCAD's material tables are keyed `Steel`, `Aluminum`, `Plastics` and so on (they
  began with `Materials-` while materials shared a library with standards). A part naming an old key is re-keyed when
  read, unless the libraries in use still have that key (a company library made before keeps its keys).
- **Holes sized before v2** (`Standard` ISO273 or ASME-B18.2.8) are named by their fastener-holes row when read:
  the socket head cap screw's (the flat head screw's for a countersink), with the values they were made with kept
  as overrides where the row's differ, so they build the same.
- **Fastener parts**' identity hashes change with the item key format; that is fine before release.
- **Existing part files**: their snapshots of built-in items (`ISO273/M8`, `ISO261-coarse/M8`, ...) are upgraded on
  read by a small mapping of the old built-in keys to the new ones; snapshots of other libraries' items are kept as
  they are and resolve as before until the part is edited. The demo samples are regenerated.

## The built-in library

SpryCAD's defaults are `data/library/standards.xlsx` and `data/library/materials.xlsx`, in the export layout above.

- **To change them**, edit the workbook in Excel (or export from SpryCAD's Library Editor, edit, and save over it) and
  build. `SpryCad.Core`'s build runs `tools/SpryCad.LibraryGen` on the workbooks first: every sheet is checked against
  its schema, and a problem fails the build, named by its workbook, sheet and cell. A table's key and row keys never
  change meaning once released (parts name them).
- **Readable diffs.** `.gitattributes` marks `*.xlsx` with the `xlsx` diff driver; set it up once per clone (after a
  build, so the tool exists):

  ```
  git config diff.xlsx.textconv "dotnet tools/SpryCad.LibraryGen/bin/Debug/net10.0/SpryCad.LibraryGen.dll text"
  git config diff.xlsx.cachetextconv true
  ```

  `git diff` and `git log -p` then show the cells that changed, a line per row (tab-separated), headed by its sheet.

## Phases

Each phase is one branch, reviewed before it is merged.

1. **Foundation.** Schemas, typed columns, composite keys, native units; `LibraryTable` and storage at FormatVersion
   2; defaults or a company library per domain, no layers or restrictions; self-describing workbook sheets;
   built-in data as workbooks generated at build (the `SpryCad.Library` project, the generator, the diff driver).
   The existing kinds ported as they are (thread with major and pitch only; clearance,
   counterbore and countersink still separate), so nothing else changes yet.
2. **Holes.** The fastener-holes kind replacing clearance, counterbore, countersink and the two switches; holes
   resolving from snapshots with explicit overrides (library hole types too); the Hole Wizard reworked onto them;
   Insert Fastener's families from the row.
3. **Threads.** The full thread record and the taper-thread kind, SpryCAD's tables filled from the standards'
   formulas; Auto threads from the library (the part keeping the table's rows); tapped holes (the tap drill, a
   cosmetic thread to a depth); the Inventor importer.
4. **Domains.** Materials as their own library file and export, with grouped headings and colors. (Done with
   phase 1: standards and materials are separate libraries, chosen, edited and exported separately.)

## Open questions

- ~~Thread classes in one row or two.~~ Decided: Inventor's way, a row per designation and (external) class, the
  internal class that goes with it on the same row (see thread).
- **Tapped holes** show their thread cosmetic, or cut it (Cosmetic thread off, or View > Threads modeled; the Thread
  feature's groove). Exports model a Thread feature's cosmetic thread but not yet a tapped hole's, and Insert
  Fastener does not yet take a tapped hole as the screw's far end.
- ~~Merging layers.~~ Decided: no layering; defaults or a company library (see Libraries and files).
- **Verified data.** Built-in tables are all marked unverified today. Phase 1 carries their values over unchanged;
  checking them against the published standards (or taking Inventor's values, if licensing allows) is its own pass.
- **`LibraryLink.Current`** is a global read throughout Core. Out of scope here; worth replacing with the set passed
  in, later.

# Standards libraries

Thread sizes, holes for fasteners (clearances, counterbores, countersinks), fasteners and materials come from libraries. The design
is in [library-v2.md](library-v2.md); this page is how to set them up and use them.

## Defaults or a company library

SpryCAD carries its own defaults, so it works with nothing set up:

| Library | What |
| --- | --- |
| Standards | ISO 261/262 metric threads (coarse and fine), ASME B1.1 UNC/UNF, holes for fasteners (ISO 273 and ASME B18.2.8 clearances, with the counterbores of ISO 4762 and ASME B18.3 socket head cap screws and the countersinks of ISO 10642 and ASME B18.6.3 flat head screws), and the fastener families: ISO 4762 and ASME B18.3 socket head cap screws, ISO 4032 hex nuts, ISO 7089 washers |
| Materials | Typical metals, plastics, elastomers, composites, wood, ceramics and glass, and appearance presets |

A company that wants its own makes a company library, which starts as a copy of SpryCAD's defaults, and changes it:
adds in-house sizes and part numbers, corrects values, removes the sizes it does not use. Standards and materials are
separate libraries, chosen separately: a company can keep SpryCAD's threads and use its own materials.

The library in use is either SpryCAD's defaults or the company's library, never a mix. A size removed from the company
library is no longer offered; parts that already use it keep their copy of it.

> The default values come from secondary sources and are marked unverified. Check them against the published
> standards before you rely on them.

## Setting up a shared library set (admin)

1. In Tools > Options > Libraries, click **Create in a Folder...** and pick a folder on the file server. SpryCAD writes
   `standards.sprlib` and `materials.sprlib` (your company's own, copies of SpryCAD's defaults) and `libraries.json`:

   ```json
   { "libraries": [ "standards.sprlib", "materials.sprlib" ] }
   ```

   Paths are relative to `libraries.json`. Each library says whether it holds standards or materials; leave one out
   to use SpryCAD's defaults for it. If two of the same kind are listed, the first is used and Options says so.
2. Set the folder's permissions: everyone reads, and only the library owners write. SpryCAD does not sign company
   libraries; the share's permissions are what protect them.
3. Point each computer at the folder. The first of these that is set wins:
   - the `SPRYCAD_LIBRARIES` environment variable (by group policy or login script);
   - the first line of the machine policy file: `%ProgramData%\SpryCAD\libraries.path` on Windows,
     `/Library/Application Support/SpryCAD/libraries.path` on macOS, `/etc/sprycad/libraries.path` on Linux;
   - the user's own choice in Options > Libraries (grayed out when either of the above is set).

## How clients use it

- **Never opened on the share.** SpryCAD copies each library to `%LOCALAPPDATA%\SpryCAD\Libraries` and reads the copy.
  A copy is refreshed when the share's file changes (size, time and first 64 KB), checked at start-up and every 5
  minutes. Keeping a file open on the share would block the owner's publish on Windows, and SQLite locking cannot be
  trusted on network shares.
- **Offline:** when the share cannot be reached, SpryCAD uses the copies it last read and says so in Options.
- **Parts are self-contained.** A part keeps a copy (snapshot) of every library row its features and its material use,
  as ordinary versioned objects. It rebuilds the same with no library installed, for example at a supplier.
- **Updates are opt-in.** When a library changes a row that a part uses, opening the part says so in the status bar.
  **Tools > Library Updates** lists the changed values and, if accepted, takes them in as one undoable edit that shows
  in the part's history. Holes sized from a changed row are sized again, but for the values typed over the
  library's on the Hole Wizard page: those are kept as typed.

## Editing a library

**Tools > Libraries > Edit Standards Library** and **Edit Materials Library** open the library in use (with SpryCAD's
defaults in use, a copy of them: publishing saves your copy as your company's own library in a folder you choose, with
a `libraries.json`). **New Company Library** starts one from SpryCAD's defaults.

- **In Excel.** **Export to Excel** writes the library to a workbook, a sheet per table. Each sheet says what it is in
  its first row (table key, kind, units, standard, whether verified) and names each column with its unit
  ("Normal (mm)", "Density (g/cm³)"). Edit it in Excel, LibreOffice or Google Sheets, then **Import from Excel**: a
  workbook with a problem imports nothing and names each problem's sheet and cell; otherwise it shows what changes
  before taking it in. Tables not in the workbook stay as they are. Columns may be reordered; a column the table's kind
  does not have becomes a text column (part numbers, a supplier).
- **In SpryCAD.** The editor shows a table's rows, values and text and edits them directly (copy and paste work with
  spreadsheets).
- **Units.** Each table has the units of its standard: an inch standard holds inches, exactly as published (angles in
  degrees either way); a material table metric or inch-pound units. Changing a table's units converts its values.
- **Publishing** saves the library's next revision with a message. A library is the same database format as a part, so
  each publish is a revision with history (**History**). If someone else published since you opened the library, it
  says so and offers to reload their version.

## Threads

A thread table has a row per designation and class (`1/4-20 UNC` 2A, `1/4-20 UNC` 3A), as Inventor's thread sheets
do: its major diameter and pitch, the class's limits and those of the internal class that goes with it (2B, 3B), and
its tap drill. SpryCAD's are worked out from the standards' formulas: ASME B1.1 for the Unified tables (2A/2B and
3A/3B, every limit), ISO 965-1 for the metric ones (6g/6H, the limits that follow from the basic profile alone; the
others left blank).

- **Thread feature**: a size of a table and its class, or Auto: the size of the table fitting the face (a shaft by its
  major diameter, a hole by its tap drill) in the class picked. A part with an Auto thread keeps a copy of every row
  of its table.
- **Tapped holes** (Hole Wizard, Tapped): a size and its class (2B, 3B); the hole is drilled with the size's tap
  drill (typed over, kept) and threaded to the thread depth (blank: as deep as the hole goes): shown as a cosmetic
  thread, or cut (Cosmetic thread cleared) with the groove the Thread feature cuts, so a modeled screw fits it.
- **Callouts**: a hole and a thread say what they are as a drawing's callout would, their thread's class in it: "⌀9
  THRU ALL" over "⌴ ⌀15 ↧ 8.6"; "2X ⌀6.75 THRU ALL" over "M8-6H ↧ 12" for a tapped hole (its internal class);
  "1/4-20 UNC-2A ↧ 20" for a thread on a shaft. The feature tree shows it as the feature's tip; the measure tool shows
  it on a hole's or a threaded face, with the thread's data: its table, class, pitch (and TPI), its limits and its tap
  drill, in the table's units.
- **Import from Inventor** (Library Editor): reads Inventor's `thread.xlsx` (thread and taper sheets) and
  `clearance.xlsx` (fastener holes) into the library, showing what changes first.

## Holes for fasteners

A fastener holes table (`ISO-holes`, `ASME-holes`) has a row per fastener type and size (`Socket head cap screw`,
`M8`): its close, normal and loose clearances; its head's pocket (`counterbore` or `countersink`, its diameter, and
its depth or angle); and the screw, nut and washer families that go in it (table keys). The Hole Wizard's Standard is
the table, Fastener its fastener types that suit the hole type, then Size and Fit. A company adds its own fastener
types (a hex bolt, a button head) as rows.

## Fasteners

The fastener families have a row per size and length (`M8x30`, `1/4-20 x 1`), each naming its screw size (`size`:
`M8`, `1/4`).

- **Insert Fastener** (Assembly tab): click the wall of a hole that the Hole Wizard sized from a fastener holes table.
  The screw size and families come from the hole's row. SpryCAD measures the stack along the hole's axis, from the face the head bears on
  through the touching parts. The screw length is the clamped thickness plus the nut, washer and two pitches (or 1.5 d
  of thread in a tapped part), rounded up to the next length the library offers. The screw, its washers and its nut are
  mated to the hole (concentric and coincident).
- **One part per row.** A fastener is a generated part, embedded in the assembly once per row however many components
  use it. It keeps its own snapshot of the row. Its body is built once per family definition, row values and kernel
  version, and is shared.
- **BOM:** fasteners are listed by standard and designation (`ISO 4762 M8x30`), not by file name.

## Notes on the format

- **Item keys.** A part names a library row by its table and its key, joined by a colon: `ISO-holes:Socket head cap screw:M8`,
  `ASME-B1.1-UNC:1/4-20 UNC`. Parts written before library v2 (`ISO273/M8`) are upgraded when read; their holes sized from the old clearance tables are named by their fastener holes row.
- **Library format 2.** A library written by an earlier SpryCAD is refused, with what to do: make a new library from
  the defaults and import its tables from Excel.
- **Not opened in place**, no per-part table of references (snapshots are revision objects, so revert, diff and
  Compact handle them), and no signing for company libraries (the share's permissions protect them).

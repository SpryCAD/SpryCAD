# Standards libraries

Thread sizes, clearance holes, counterbores and countersinks come from standards libraries (file format spec, section 34). One set of libraries, kept in a shared folder, serves every SpryCAD user in a company.

## Layers

| Layer | What | Where |
| --- | --- | --- |
| Built-in standards | ISO 261/262 metric threads (coarse and fine), ASME B1.1 UNC/UNF, ISO 273 and ASME B18.2.8 clearance holes, counterbores (ISO 4762, ASME B18.3) and countersinks (ISO 10642, ASME B18.6.3) | Carried by SpryCAD itself |
| Standards library | Corrections or additions to the above | `standards.sprlib` on the share |
| Company library | Preferred sizes (restrictions), in-house rows, part numbers | `company.sprlib` on the share |

A table in a later library adds rows to a table with the same key, and overrides rows with the same designation. If its columns differ, it replaces the table. Restrictions hide rows or whole tables from the Thread and Hole Wizard pickers. Parts that already use a restricted size keep using it.

> The built-in values come from secondary sources and are marked unverified. Check them against the published standards before you rely on them, and record corrections in `standards.sprlib`. How standards data may be sourced and distributed still needs legal review (spec, section 18).

## Setting up a shared library set (admin)

1. In Tools > Options > Libraries, click **Create in a Folder...** and pick a folder on the file server. SpryCAD writes `standards.sprlib` (a copy of the built-in standards) and `libraries.json`:

   ```json
   { "libraries": [ "standards.sprlib", "company.sprlib" ] }
   ```

   Paths are relative to `libraries.json`, and libraries load in the order listed.
2. Set the folder's permissions: everyone reads, and only the library owners write. SpryCAD does not sign company libraries; the share's permissions are what protect them.
3. Point each computer at the folder. The first of these that is set wins:
   - the `SPRYCAD_LIBRARIES` environment variable (by group policy or login script);
   - the first line of the machine policy file: `%ProgramData%\SpryCAD\libraries.path` on Windows, `/Library/Application Support/SpryCAD/libraries.path` on macOS, `/etc/sprycad/libraries.path` on Linux;
   - the user's own choice in Options > Libraries (greyed out when either of the above is set).

## How clients use it

- **Never opened on the share.** SpryCAD copies each library to `%LOCALAPPDATA%\SpryCAD\Libraries` and reads the copy. A copy is refreshed when the share's file changes (size, time and first 64 KB), checked at start-up and every 5 minutes. Keeping a file open on the share would block the owner's publish on Windows, and SQLite locking cannot be trusted on network shares.
- **Offline:** when the share cannot be reached, SpryCAD uses the copies it last read and says so in Options.
- **Parts are self-contained.** A part keeps a snapshot of every library row its features use, as ordinary versioned objects. It rebuilds the same with no library installed, for example at a supplier.
- **Updates are opt-in.** When a library changes a row that a part uses, opening the part says so in the status bar. **Tools > Library Updates** lists the changed values and, if accepted, takes them in as one undoable edit that shows in the part's history. Holes sized from a changed row are sized again. A hole whose diameters were typed over is treated as custom and keeps its values.

## Publishing changes

A library is the same database format as a part, so each publish is a revision with history. `LibraryFile.Publish` writes a copy beside the file and swaps it in. It refuses (`LibraryConflictException`) if someone else published since the revision that was read.

**Tools > Libraries > Edit Library** edits a library's tables (rows, values, text such as part numbers, CSV import and export per table) and which rows the pickers offer (an Enabled check box per row, or hide the whole table), and publishes them with a message. If someone else published first, it says so and offers to reload. With no library set in use, it opens SpryCAD's built-in standards instead: publishing saves them, with your changes, as `standards.sprlib` and `libraries.json` in a folder you choose, and offers to use that folder. **New Company Library** starts one and, once published, offers to add it to `libraries.json`. **History** lists each publish. Each table has units: values are always stored in millimetres, and an inch table (the ASME ones) shows and takes lengths in inches (angle columns in degrees). CSV files use the table's units and have a header row (`row`, then the columns, then `text:name` for each text column) and one row per designation.

## Differences from the spec (section 34)

- **Not opened in place.** The spec opens shared libraries read-only, in place, with `immutable=1`. They are copied locally instead, for the reasons given in section 15.5.
- **No `LibraryRefs` table.** Snapshots are `LibrarySnapshot` objects in the revision, so revert, diff and Compact already handle them. A separate per-part table would point at the wrong snapshot after a revert.
- **Features hold only an item key.** A feature stores the item key (for example `ASME-B1.1-UNC/1/4-20 UNC`, or a hole's standard, size and fit). The library id and revision live in the snapshot, so accepting an update does not change every feature.
- **No signing for company libraries.** As decided, the share's permissions protect them. Signing remains planned for libraries that SpryCAD ships separately.

## Fasteners (spec section 25)

SpryCAD also carries part family tables (layer 2): ISO 4762 and ASME B18.3 socket head cap screws (a row per size and length, `M8x30`, `1/4-20 x 1`), ISO 4032 hex nuts and ISO 7089 washers. Like the standards, they are marked unverified. Each row names its screw size in its text (`size`: `M8`, `1/4`), so a company restriction can list sizes as well as rows: `{ "table": "ISO4762", "allow": [ "M3", "M4", "M5", "M6", "M8", "M10", "M12" ] }` offers those sizes in every length.

- **Insert Fastener** (Assembly tab): click the wall of a hole that the Hole Wizard sized from a clearance table. The screw size comes from the hole. SpryCAD measures the stack along the hole's axis, from the face the head bears on through the touching parts. The screw length is the clamped thickness plus the nut, washer and two pitches (or 1.5 d of thread in a tapped part), rounded up to the next length the libraries offer. The screw, its washers and its nut are mated to the hole (concentric and coincident).
- **One part per row.** A fastener is a generated part, embedded in the assembly once per row however many components use it. It keeps its own snapshot of the row. Its body is built once per family definition, row values and kernel version, and is shared.
- **BOM:** fasteners are listed by standard and designation (`ISO 4762 M8x30`), not by file name.

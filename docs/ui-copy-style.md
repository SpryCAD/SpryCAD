# UI copy style

SpryCAD is a professional CAD system. Its text should read that way: clear technical descriptions, short and
accurate. The reader is a mechanical engineer who knows CAD, so state results and required actions, and don't
explain how the program works inside.

This covers every string a user sees: status bar messages, tooltips, PropertyManager page text, selection-box
hints, dialog titles, bodies and buttons, menu items, tree rows, and the error messages Core raises
(`FeatureException` and the like), because they reach the user too.

## Rules

1. **Status messages** are one short sentence stating the result, plus at most one short instruction if the user
   has to act. No parenthetical asides and no chains of clauses joined by colons. End with a period.
   - "Configuration 'X' added and activated." (was: "Configuration X added and active: suppress, hide or
     configure components for it; the other configurations keep theirs.")
   - "Exploded view on. Components cannot be dragged while exploded."
2. **Tooltips** are the command name, then one sentence of about 20 words at most, starting with a verb.
   - "Insert Fastener\nInserts a library screw, sized and length-matched to a Hole Wizard hole, with optional nut
     and washers." (was 60 words)
3. **Page notes** (`page.Info`) are at most two short sentences. Keep required instructions and syntax hints; cut
   examples, asides and anything another field already says.
4. **Dropdown options** are plain names: "Material inside", "Teardrop", "Overlap A (first over second)". Add a
   parenthetical only when the name alone is ambiguous.
5. **Verbs:** use **Select** for choosing geometry, entities, features or rows, and **Click** only for buttons or
   for placing positions in the graphics area. Don't use "Pick". Use **Create** rather than "Make" for new
   features.
6. **Terms:**
   - Use **Under Defined**, **Fully Defined** and **Over Defined** as status labels; in a sentence write
     "over-defines the sketch".
   - Use **Dependents**, not "children".
   - Chamfer types are **Distance**, **Angle-Distance** and **Distance-Distance**.
   - Use **graphics area**, **design tree**, **PropertyManager**, **cylindrical face** (not "round face").
   - Keep the SolidWorks-aligned command names (Extruded Boss/Base, Hole Wizard, Smart Dimension, Pack and Go,
     Normal To, Base Flange).
7. **Errors:** dialog titles are in Title Case ("Save Failed", "Export Failed", "Publish Failed"). The body says
   what failed, then the reason: `$"Could not save {file}: {ex.Message}"`. Add how to fix it when that is known.
   Never show a bare `ex.Message`.
8. **Confirmations** ask a question that names the action ("Overwrite files in X?", "Permanently delete 3 older
   revisions?") and label the buttons with the action ("Overwrite", "Delete Revisions", "Keep Current"), never
   "OK" / "Go on". A save prompt is titled "Save Changes".
9. **Confirmation corner and page buttons** are "OK (Enter)" and "Cancel (Esc)" (or "Close (Esc)"). Don't add
   what OK does; the page already says it.
10. **Capitalization:** Title Case for buttons, menu items, dialog and page titles, and section headers. Sentence
    case for checkbox labels, page notes, dropdown options and field labels.
11. **Punctuation:**
    - Use the ellipsis character `…` (not `...`) on menu items and buttons that open a dialog or need more input.
    - Sentences end with a period; labels, buttons and titles don't.
    - Write keyboard hints as `Ctrl+Z`, `Esc`, `Enter`, and menu paths as `Tools > Options > Graphics`.
12. **Plurals:** don't write "(s)". Pluralize with the existing pattern: ``$"{n} part{(n == 1 ? "" : "s")}"``.
13. **US spelling:** center, neighboring, color, gray, millimeter, mold, miter, canceled.
14. **Units:** a value shown with a unit uses the document's units (`Units.Symbol()`, `Units.Entry(...)`), never a
    hard-coded "mm" (except where the value really is always millimeters, such as the internal model).

## Accuracy

Copy must say what the code does. Read the code before describing behavior, especially for anything optional or
conditional. Examples that were nearly wrong:

- **Fillet:** "tangent edges are included" was only true when Tangent propagation is on.
- **Mass Properties:** native bodies are integrated exactly on the B-rep, so only bodies without exact geometry
  are approximate. Saying "values are approximate" for all of them would have been wrong.

## Don't change

- **Strings used as keys:** `case "…"` labels, the keys of the tooltip dictionary in `MainWindow.Commands.cs`,
  command names that `UsualPart` / `UsualSketch` / `UsualAssembly` refer to, `StandardView("Front")` arguments,
  tab names compared in code, and Measure's `Kind` values.
- **Internal text:** `App.Log(...)` labels, `Debug?.Invoke(...)` traces and comments.
- **Text a test checks:** search `tests/` before rewording a Core message. For example,
  `PlaneReferenceTests` checks that an error starts with "Under defined".

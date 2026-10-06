# FeatherCAD

Fast, lightweight, open-source **parametric 3D CAD** with a SolidWorks-style workflow.
Pure C# — its own B-rep geometry kernel, an Avalonia UI and an OpenGL viewport — so it runs
natively on **Windows, Linux and macOS** with no browser, no Electron and no native CAD kernel to install.

![workflow](docs/mainwindow.png)

## Highlights

- **SolidWorks feel**: CommandManager tabs (Features / Sketch / Evaluate), FeatureManager design tree with
  rollback bar, PropertyManager pages with green-check/red-X and pink "active selection" boxes, confirmation
  corner, heads-up view toolbar, translucent yellow previews, blue/black/red sketch definition colors,
  and SolidWorks mouse/keyboard navigation.
- **Fully parametric**: every feature is regenerated from the history; dimensions and feature values accept
  expressions and global variables (`W = 100`, `W/2 + 5`, `1in`, `sin(30)`).
- **Fast**: incremental rebuilds (only features after the change are rebuilt), background rebuild thread so
  the UI never blocks, live preview while editing, ~1 s cold start for the ReadyToRun build. A 14-feature
  shelled part (fillets, counterbored holes, pattern, shell, chamfer) rebuilds in ~85 ms; editing one of its
  parameters takes 7–80 ms; tool solids and intermediate results are never meshed, and unchanged boolean
  operands are reused from a cache. A rebuild-time budget test guards against regressions.
- **Light**: ~3 MB of own code; the only dependencies are Avalonia and Silk.NET (OpenGL bindings).
- **Exact geometry**: faces are true planes, cylinders, cones, spheres and tori with exact line, circle and
  B-spline edges — exported to STEP as analytic surfaces and displayed with smooth silhouettes.
- **Rolling-ball fillets between any faces**: where no analytic blend exists (cylinder–cylinder, cylinder–torus,
  plane–tilted cone, …) the fillet is the pipe swept by the ball, its centre solved exactly on the faces'
  offset surfaces (within 1e-8 mm; it reproduces the exact torus of a plane/cylinder fillet to 2e-9 mm), and
  exported to STEP as a fitted B-spline surface. Fillets and chamfers can be patterned and mirrored like cuts.
- **Open formats**: native `.fcad` files are readable, diffable JSON (or a ZIP holding that JSON when a part embeds
  other files); export to **STEP AP214** and **binary STL**.

## Features

| Area | Commands |
|---|---|
| Sketch | Line, Centerline, Corner/Center Rectangle, Circle, Centerpoint/3-Point/Tangent Arc, Polygon, Straight Slot, Point, Trim, Convert Entities, Construction toggle |
| Relations | Automatic while drawing (coincident, horizontal/vertical, midpoint, on-entity) — also to the **existing model**: model vertices (coincident), model edges (on edge), edge midpoints and hole/boss centers, all associative (they follow model changes); dashed inference lines to sketch points and model vertices. Plus Horizontal, Vertical, Parallel, Perpendicular, Collinear, Equal, Tangent, Concentric, Coincident, Midpoint, Symmetric, Fix |
| Dimensions | Smart Dimension: length, horizontal/vertical/aligned distance, point–line, parallel lines, radius, diameter, angle; driven (reference) dims; expressions |
| Solver | Numerical constraint solver with minimum-change dragging and degrees-of-freedom analysis (under / fully / over defined) |
| Features | Extruded Boss/Base & Cut (Blind, Through All, Through All–Both, Mid Plane, two directions, merge result), Revolved Boss & Cut, Fillet, Chamfer, Hole Wizard (simple, counterbore, countersink, standard metric sizes, drill point), Shell, Linear Pattern (2 directions), Circular Pattern, Mirror, Reference Plane, Combine (add/subtract/common) |
| Booleans | Union, subtract, intersect between bodies; multi-body parts |
| Tools | Equations / global variables, Mass Properties (volume, area, mass, center of mass, bounding box), Options (units MMGS/IPS, part color, wheel direction, perspective) |
| Files | `.fcad` open/save, autosave + recovery, exact STEP (analytic surfaces) and STL export |
| View | Standard views (animated), Normal To, zoom to fit, Shaded With Edges / Shaded / Hidden Lines Visible / Wireframe, perspective, planes toggle |

## Controls (SolidWorks defaults)

| Action | Input |
|---|---|
| Rotate | Middle mouse drag (or Alt + left drag); middle double-click = zoom to fit |
| Pan | Ctrl + middle drag, or right mouse drag |
| Zoom | Mouse wheel (at cursor), Shift + middle drag |
| Roll | Alt + middle drag |
| Zoom to fit | `F` |
| View orientation | `Spacebar`, `Ctrl+1..7` (Front, Back, Left, Right, Top, Bottom, Isometric), `Ctrl+8` Normal To |
| Rotate view | Arrow keys (15°), Shift + arrows (90°) |
| Sketch tools | `L` Line, `R` Rectangle, `C` Circle, `A` Arc, `P` Point, `D` Smart Dimension, `T` Trim, `Esc` select/cancel |
| PropertyManager | `Enter` = OK (from any field), `Esc` = Cancel |
| Edit | `Ctrl+Z` / `Ctrl+Y`, `Delete` (confirms, lists dependent features), `F2` rename, `Ctrl+B` rebuild, `Ctrl+Q` force rebuild |

Typical workflow: select **Front Plane** → **Sketch** → draw → **Smart Dimension** (type values or `=expressions`)
→ **Exit Sketch** → **Extruded Boss/Base** (the sketch you just finished is picked up automatically) → OK.
Select a face → **Sketch** to sketch on it; select an edge → **Fillet**; select a face → **Hole Wizard**.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/FeatherCad.App -c Release
```

Open a sample: `dotnet run --project src/FeatherCad.App -c Release -- samples/bracket.fcad`

### Parametric demo models (`samples/demos`)

Every dimension in these parts is an expression of global variables: open **Tools > Equations**, change a
value and the whole part regenerates (sketches, patterns, fillets, holes). Each one is rebuilt by the test suite
as designed and with several variables changed, and exports to a valid exact STEP.

| File | What it shows | Try changing |
|---|---|---|
| `pipe-flange.fcad` | Revolved weld-neck flange, bolt hole circular-patterned on the pitch circle, hub fillet, edge chamfers | `N` (bolt count), `PCD`, `OD`, `HubH`, `BoltD` |
| `enclosure.fcad` | Rounded box, shell, rim fillet, screw bosses (2-D pattern) with holes, vent slots patterned and mirrored to the opposite wall | `L`, `W`, `H`, `Wall`, `VentN`, `VentP` |
| `heat-sink.fcad` | Base plate with N fins whose pitch is derived from N (`(L-T)/(N-1)`), counterbored mounting holes | `N`, `T`, `FinH`, `L` |
| `stepped-shaft.fcad` | Revolved stepped shaft, shoulder fillets, chamfers, rounded-end keyway, cross hole | `D1`, `D2`, `L2`, `KeyW`, `KeyL` |
| `l-bracket.fcad` | Mid-plane extruded L profile, gusset mirrored to both sides, counterbored and plain holes, outer bend fillet | `A`, `Bh`, `Wb`, `T`, `Gs`, `HoleSp` |
| `spur-gear.fcad` | Involute spur gear: one tooth gap (each flank three arcs through exact involute points, within ~2 µm of the true involute at module 2) patterned `z` times, keyed bore, recessed web with lightening holes; derived variables (`rb`, `ra`, `rf`, ...) in the equations | `m`, `z`, `PA`, `b`, `Bore` |
| `hand-wheel.fcad` | Cast-iron hand wheel: round rim, dished (raised) hub, slanted spokes extruded on a plane normal to their centreline, keyed bore, spinner knob on the rim as a separate body | `D`, `Dish`, `NS` (spokes), `SpokeD`, `KnobL` |
| `spoked-handwheel.fcad` | Hand wheel with filleted joints: round rim with finger scallops on its inner side, straight spokes, keyed hub. Every joint is a rolling-ball fillet between curved faces (spoke/rim cylinder–torus, spoke/hub cylinder–cylinder, convex scallop edges), made once on the seed spoke and scallop and patterned with them | `NS` (spokes), `NG` (grips per gap), `D`, `RimT`, `SpokeD`, `Rf` |

The demos are generated by `tests/FeatherCad.Tests/Demos.cs` through the same document API the UI uses
(`dotnet run --project tests/FeatherCad.Tests -c Release -- demos samples/demos`).

### Release builds (self-contained, ReadyToRun for fast startup)

```bash
./publish.sh linux-x64 osx-arm64      # macOS / Linux
```
```powershell
./publish.ps1 win-x64                  # Windows (PowerShell)
```
Output goes to `publish/<rid>/` — copy the folder anywhere and run `FeatherCAD(.exe)`.

### Installers

Both publish the app, regenerate the demo parts (failing on a broken one) and include the samples; the version
is `<Version>` in `src/FeatherCad.App/FeatherCad.App.csproj`, or pass one.

```powershell
build/make-installer.ps1               # Windows: publish/FeatherCAD-Setup-<version>.exe (needs Inno Setup 6)
```
```bash
build/make-linux-package.sh            # Linux: publish/FeatherCAD-<version>-linux-x64.tar.gz (also runs in Git Bash)
```
The Windows setup installs per user by default and opens `.fcad` files with FeatherCAD. On Linux, unpack the
tarball and run `./install.sh`: it installs into `~/.local/share/feathercad` with a menu entry, icon, `.fcad`
file type and a `feathercad` command (`./uninstall.sh` removes them).

### Tests

```bash
dotnet run --project tests/FeatherCad.Tests -c Release
```
The test runner checks kernel booleans (volumes, watertightness), blends, shell, revolve, exporters, the sketch
solver and complete parametric parts (sketch → extrude → cut on face → fillet → hole → pattern → shell →
equation change → save/load round trip), plus a rebuild-time budget on two sample models.

Performance tools:

```bash
# warm/cold rebuild, per-feature times, profile by stage, and the cost of editing each feature's parameter
dotnet run --project tests/FeatherCad.Tests -c Release -- bench path/to/model.fcad 10
# rebuild with one parameter changed, writing the boolean trace to %TEMP%/fc-repro-trace.txt
dotnet run --project tests/FeatherCad.Tests -c Release -- repro path/to/model.fcad Fillet2 Radius 2.5
```
`Kernel.Prof` is a lightweight stage profiler (`using (Prof.Time("stage")) ...`, off by default).

## Architecture

```
src/FeatherCad.Kernel   Geometry kernel (no dependencies)
  Math/                 Vec2/Vec3, planes, frames, transforms
  Brep/                 Solid / Face (planar loops + exact plane), stitched Topology (vertices, edges, adjacency)
  Csg/                  Plane-based BSP booleans, vertex welding, face merging / T-junction resolution
  Build/                Profiles, extrude, revolve, primitives
  Ops/                  Edge blends (fillet/chamfer), shell
  Tess/                 Ear-clipping triangulation (holes), GPU display meshes
  IO/                   STEP AP214 and STL writers
src/FeatherCad.Core     Parametric layer
  Sketch/               Sketch model, constraint solver, profile (region) extraction
  Model/                Document, features, references (faces/edges/planes), .fcad serialization
  Regen/                Cached incremental regeneration, reference resolution
  Expr.cs               Expression evaluator for dimensions and equations
src/FeatherCad.App      Avalonia desktop UI
  Viewport/             OpenGL renderer (MSAA), camera, picking, selection modes
  Sketcher/             Interactive sketch editor, dimensions, relation glyphs
  UI/                   PropertyManager framework, design tree, dialogs, icons
tests/FeatherCad.Tests  Kernel + core test runner
```

### The kernel

FeatherCAD uses its own **boundary-representation** kernel written for speed and robustness:

- **Exact planes.** Every face carries the exact plane it was built from; booleans split along those
  planes (plane-based geometry) rather than re-deriving planes from rounded vertices, which keeps
  results crack-free and watertight.
- **Topology vs. geometry are separate.** A solid is faces → loops → edges → vertices; each face points to a
  `SurfaceDef` (plane, cylinder, cone, sphere, torus) describing its **true** shape. Curved faces are currently
  stored as fine planar facets *that remember their exact surface* (axis, radius…). That exact data already
  drives smooth shading, hole/boss rim chamfers and fillets and pattern axes, and it is what a future exact
  NURBS/analytic surface back end will use — files will upgrade without changes.
- **Exact B-rep layer** (`Kernel/Exact`). After every operation the facets of each analytic surface are merged
  back into one exact face (`ExactBrep`). Boundaries are recognized as exact lines and circles; general
  intersection curves (e.g. two crossing pipes) become cubic B-splines interpolated through points projected
  onto *both* exact surfaces. Vertices are projected onto all their incident surfaces. This exact B-rep drives
  STEP export (PLANE, CYLINDRICAL/CONICAL/SPHERICAL/TOROIDAL_SURFACE, LINE, CIRCLE, B_SPLINE_CURVE_WITH_KNOTS),
  display refinement (curved faces are subdivided adaptively onto their true surface, crack-free) and the
  face/edge readouts in the status bar. Exported files are verified with OpenCascade's B-rep checker
  (valid closed solids, exact volumes).
- **Persistent naming.** Faces are named from the feature and sketch entity that created them
  (`F3.S.L7` = side face of extrude F3 from sketch line 7). References to faces/edges use these names plus a
  geometric hint, so fillets and sketches-on-faces survive parameter changes.

### The `.fcad` format

Plain JSON: units, equations, and the ordered feature list with sketches (points, entities, constraints and
dimension expressions). Example:

```json
{
  "format": "FeatherCAD", "formatVersion": 1, "units": "MMGS",
  "equations": [ { "name": "W", "expr": "120" } ],
  "features": [
    { "$type": "sketch", "id": "F1", "name": "Sketch1", "plane": { "std": "Front" }, "sketch": { "...": "..." } },
    { "$type": "extrude", "id": "F2", "name": "Boss-Extrude1", "sketchId": "F1", "end": "Blind", "depth": "20" },
    { "$type": "fillet", "id": "F5", "name": "Fillet1", "radius": "5", "edges": [ { "a": "F2.S.L3", "b": "F2.End", "hint": [100, 60, 10] } ] }
  ]
}
```

A part that embeds other files (e.g. an imported model) is saved as a **container**: a ZIP archive with the same
JSON as `model.json`, the embedded files under `attachments/` (listed with size and SHA-256 in `model.json`, and
verified on open) and a `thumbnail.png` preview. Options → "Always save parts as containers" saves every part
that way. Both kinds use the `.fcad` extension and are told apart by their first bytes. To diff containers in git:

```bash
git config diff.fcad.textconv 'sh -c "unzip -p \"$0\" model.json 2>/dev/null || cat \"$0\""'
# and in .gitattributes:  *.fcad diff=fcad
```

## Current limitations

- Shell keeps the faces and edges of the part and moves them: when the inner surface changes shape at the wall
  thickness (a fillet or hole closer to another face than the wall is thick) it fails with an explanation.
- Corners where three fillets meet are not blended yet; chamfers on curved face pairs are not supported.
- STEP import reads exact solids (analytic and B-spline faces); assemblies, surfaces of revolution/extrusion
  and open surface models are not imported yet.
- Sketch profiles use the union of closed regions; crossing lines must be split with Trim first.
- Developed and tested on Windows 11. Linux and macOS use the same code (Avalonia + OpenGL) but have not been
  tested yet.

## Roadmap

Check items off as they land.

### Kernel performance

- [x] Local booleans: work only on the faces near the tool instead of the whole body (rebuild time follows
      the size of the change, not of the part)
- [ ] Reuse face splits and edge samples of faces a boolean leaves untouched (face domains and edge
      bounds are reused)
- [ ] Analytic intersections for more pairs (plane/torus, cylinder/sphere, oblique cone/plane, ...)
- [ ] Pattern copies that do not touch: copy the topology instead of running booleans
- [ ] Recognise B-spline faces that are planes, cylinders or cones on import (sheet-metal STEP files)
- [ ] Faster B-spline closest point (better seeds, fewer Newton steps) and adaptive traces between B-splines
- [ ] Display normals from the tessellator's parameters; reuse unchanged faces' meshes; level of detail
- [ ] Fewer surviving allocations per operation (clone of a 3000-face body triggers gen-1 GCs)
- [ ] Rolling-ball spine at 1e-6 instead of 1e-8; independent fillet chains in parallel

### Modelling

- [ ] Sweep and loft (path, sections, guide curves)
- [ ] Draft (including parting line)
- [ ] Smooth corner blends where three fillets meet (setback / vertex blends)
- [x] Fillets along edges that run across tangent faces (the ball rolls from face to face in one tool)
- [x] Patterned fillets solved again on instances whose geometry differs from the seed
- [ ] Variable-radius and face fillets; chamfers on curved faces
- [ ] General shell / offset that survives topology changes; thicken; offset surface bodies
- [ ] Surface modelling: open surface bodies, knit, trim, extend, fill / patch, ruled and boundary surfaces
- [ ] Fit traced section curves to the size of the face pair, not of the whole operand (a big pattern cut
      leaves coarse curves that later local booleans must reconcile: handwheel spokes at 220-250 degrees)
- [ ] Boolean robustness for the remaining tangent and partial-overlap cases (fillets of neighbouring features that
      overlap each other, cuts with fillets running through other fillets)
- [ ] Body edits: split body, move / delete / replace face with healing
- [ ] Sheet metal: flanges, bends with K-factor, flat pattern

### Geometry and topology

- [ ] Swept / extruded / revolved surfaces (also in STEP import); true offset curves
- [ ] Tolerant modelling (per-edge and per-vertex tolerances) for imperfect imported geometry
- [ ] General curve/curve and curve/surface intersector
- [ ] Healing on import (gaps, slivers, orientation; orientation from loops is done)
- [ ] Non-manifold, sheet and wire bodies
- [ ] Persistent naming through all splits and merges

### Analysis

- [ ] Exact mass properties (integrated on the surfaces, not the mesh)
- [ ] Minimum distance, curvature analysis, interference detection
- [ ] Section views / exact planar slices

### Data exchange

- [x] STEP import of exact solids (analytic and B-spline faces)
- [x] Embedded files in .fcad containers
- [ ] STEP assemblies (placed instances), colours and layers; AP242 with PMI
- [ ] IGES and Parasolid (x_t)
- [ ] File-format plugins (import/export registry, plugins loaded on first use)

### Beyond the kernel

- [ ] Assemblies and mates
- [ ] Drawings with hidden-line removal
- [ ] Configurations
- [ ] Scripting API

## License

MIT — see [LICENSE](LICENSE).

# Performance notes

Open performance work, for whoever picks it up next. From a review of the whole code base (October 2026) and the
changes made after it. Line numbers drift: search for the names.

The aim is a faster app that stays robust and light: no new dependencies, and results that are either bit-identical
or checked against the old ones (see *Checking a change* below).

## Done already

| Commit | What |
|---|---|
| `ebcb470` | Display normals of free-form faces searched once per point; screw surfaces keep their last closest point and sweep box; the rebuild cache keyed by prefix hash (undo/redo/preview hits); compact JSON in memory; no DOF analysis per sketch-drag move; camera matrices cached |
| `4d0e6b2` | Ear clipping with a z-order index; `CdtMesh` in flat arrays; NTess interior grid by row crossings and boxed boundary runs (display tessellation ~1.7× faster) |
| `ab36f00` | Plane × cone (exact conics, apex lines) and plane × torus (closed-form points) instead of traced |
| `d7e143e` | `NIntersect.Trace` crossings, adjacency and chaining in flat stamped arrays; `NSurface.FGrad` gives `F_` and `Grad` from one evaluation for `ProjectToIntersection` (results bit for bit; traced time on the handwheel about halved, Torus × Cylinder ~310 to ~155 ms summed) |
| `d9974b7` | `BSplineCurve.Derivs` (exact first and second derivatives) and `ClosestParam` (bracketed Newton); `NSpline.Param` uses it after the sample scan instead of 60 golden-section steps; `FitSpline` measures each point on its own span and adds every bad span's worst point per pass, with no `NSpline` per pass (`sec.makeedge` 4–5× faster; results bit for bit on every sample and demo variant) |
| `bb6b89e` | `NDomain`: boxes over runs of 16 boundary segments (UV and 3D, built on first use); `ContainsUV` skips runs the ray cannot cross, `BoundaryDistance` compares squared distances and skips runs farther than the best, and `Classify` measures only up to its limit (results bit for bit; `sec.midclass` about 1.5–3× faster: gear 23 to 13 ms, acme screw 21 to 6 ms) |
| `7f68541` | `NBoolean` vertex matching: the snap pool hashed on a grid (the lowest-index match, as the scan; a scan below 33 vertices), `MergeVertices` rewriting edge ends once (replacements followed in the order made), and `ImprintSplitVertices` and `Sew` finding candidate vertices by x range, in list order; `split.cand`/`roots`/`apply`/`imprint` timers (results bit for bit; `split.imprint` about 3×: spur-gear-nice ~20 to ~7 ms) |
| `3f9f50d` | Edge samples per tolerance band: `NEdge.Samples` gives a sampling at most 4× finer than asked (a second cache beside the finest), `FinestSamples` the old behaviour for the readers that need it (face split, curve crossings, the boolean's domains). B-reps bit for bit; display meshes now follow their own tolerance at boundaries (display tessellation 2–5× faster, display mesh up to 2.5×: acme screw 145 to 32 ms and 205 to 95 ms) |
| `f87f849` | `NDomain.Classify` refines the boundary next to a point (within twice the sampling tolerance): the coedges near it are resampled at `tol/100` and the distance and crossing tests run on that; the boolean's domains then use coarse samples and `EdgeSurface` no longer samples every candidate edge at `_tol·50` (curve crossings ask for those samples themselves). Exact mass properties and every demo variant the same; geometry hashes (`FC_DET_GEOMETRY`) the same except the last bits of a crossing vertex on the acme screw. `sec.domains` 3–4×, `bool.classify` ~30% faster; bolt-and-nut ~20%, acme screw ~10% faster rebuilds, the heavy samples within noise |
| (next) | `Classify`'s refinement: near segments found through the 16-segment boxes, coedges no nearer than the band not measured, fine UV mapped only for the crossing test, crossing parity as the polygon's flipped by the swapped segments, the exact distance over the swapped coedges only (results bit for bit; `sec.midclass` 2–3× faster: handwheel 25 to 9 ms, spur-gear-nice 30 to 15 ms) |

## Where the time goes now

Measure with `dotnet run -c Release -- bench <file.sprt> 5` in `tests/SpryCad.Tests` (per-feature times plus a
profile; the profile's times are summed over threads). On the heavy samples (`samples/demos/spur-gear-nice.sprt`,
`samples/spoked-handwheel.sprt`) the leaders are:

- **`NIntersect.Trace`** (`trace.march`, `trace.project`, `trace.grid`). These are surface pairs with no closed form:
  Cone × Cylinder on the gear (~600 ms summed), Torus × Cylinder on the handwheel (~400 ms).
- **`sec.makeedge`**: `NBoolean.FitSpline` over traced polylines (~300 ms on the gear).
- After the October work, `spur-gear-nice` rebuilds in ~490 ms (from ~710); traced Cone × Cylinder is still the leader
  (`trace.project` ~400 ms, `trace.march` ~350 ms, `trace.grid` ~160 ms summed). `spur-gear-nice.sprt` is a personal
  part, ignored by git: ask for it.
- **Display mesh build**: `mesh.topology`, `topo.weld`, `topo.edges`, `mesh.emit`.

## Kernel: open items, roughly by expected gain

1. **`NIntersect.Trace` internals** (results must stay identical):
   - `Densify` always splits at depth 0 and then projects twice at depth 1. That is about 3 projections per
     segment; the extra points change results.
   - `ProjectToIntersection` starts every point from scratch; `trace.grid` (the block test and lazy `F_` values) is
     now about as large as the march and the projection.
2. **`NBoolean.FitSpline` and `NSpline.Param`**: done (see above). The `NSpline` constructor still evaluates 129
   samples for the scan; `Param` still ignores `hint`.
3. **`NDomain.Classify`, `BoundaryDistance` and `ContainsUV`**: done (see above). What is left of `sec.midclass` is probably
   mostly `Map` (the surface's `UV`) and `Project`; `ExactBoundaryDistance` is still linear in the coedges.
4. **Vertex matching in `NBoolean`**: done (see above). In `bool.edge splits` what is left is mostly
   `split.crossings` (curve × curve on faces) and the edge/surface roots.
5. **Edge sample cache**: done (see above).
6. **B-spline interpolation** (`Exact/Curves.cs` `Interpolate`/`SolveBanded`): builds a dense (n+1)² matrix and
   clones it. STEP rational curves push n into the thousands (hundreds of MB). Store the band only.
7. **NURBS core** (`NNurbs.DerivsIn`):
   - Six passes over `Vec3[,]`/`double[,]`; flat homogeneous poles in one pass should be 3–5× faster.
   - `PoleAt` uses LINQ inside Newton.
   - Newton stops only on a step below 1e-15; add a residual test.
8. **Screw-surface ray casts** (`ScrewSurface.RayHits`): up to 20,000 unhinted `Closest` calls per ray. Warm-start
   from the previous sample with the global search as fallback. Ray parity must not change.
9. **Mass properties**:
   - `NMass.Partials` uses finite differences for B-spline and offset surfaces; `Nurbs.Derivs` is exact.
   - `Breaks` samples more panels than 8-point Gauss needs.
10. **Display pipeline**:
    - `NTess` emits one `Face` object per triangle, then `Topology.Build` re-welds every corner through a dictionary.
      Indexed per-face output would skip the weld.
    - `DisplayMesh` emits unshared vertices (3× the GPU memory within a face).
11. **Small items**:
    - `NFace.ComputeBounds` builds a throwaway domain. Sharing the cached one changed results; see *Tried and rejected*.
    - `ClassifyAll` runs `CoincidentWith` before the cheap box test.
    - `NFace.Coedges` is a LINQ `SelectMany` used in hot loops.
    - `AddSec` uses `List.Contains`.
    - `SplitFace` pruning and dedup are O(s²) to O(s³).
    - STEP import uses culture-sensitive `EndsWith` (should be ordinal) and runs `Unit()` twice, each with sorts.
    - `Solid.Combine` has an unreachable duplicate clone block.

## Core and app: open items

- **Sketch solver** (`Sketch/Solver.cs`):
  - Dense m×m systems, allocated per damping attempt.
  - Split equations into independent clusters (union-find over `Eq.Vars`) and skip converged clusters.
  - Drop equations with no variables.
  - Analytic derivatives for the common constraints.
  - `WouldOverDefine` ranks the whole sketch twice.
- **Mate solver** (`Assembly/MateSolver.cs`): already sparse. Solve the connected pieces separately and skip those
  already within tolerance. `Settle` runs two full solves.
- **Regenerator**:
  - Cancellation is only checked between features, so pass the token into the boolean and blend loops.
  - `DocumentSession` builds display meshes before its stale check.
  - Equations are ordered by trial and catch (about n²/2 exceptions); sort them topologically.
  - `ResolveDir` from an edge tessellates the whole intermediate body; try `ResolveNativeEdge` first, as the other
    resolvers do.
- **Viewport** (`ViewportControl`, `SketchEditor`):
  - `Redraw()` rebuilds the sketch overlay synchronously on every input event, recreating every relation glyph
    (brushes, a native `Cursor` each). Coalesce to once per frame, pool glyphs by constraint id, and cache the label
    layout (a pan does not change it).
  - `PickVertex` allocates per edge per move with no cull.
  - Assembly hover ray-tests every triangle; cull by face boxes as `PickItem` does.
- **Sketch lookups**: `Sketch.Point`, `Entity` and `P` are linear scans used per entity per frame. Selection is a
  `List`.
- **Feature tree**: rebuilt fully 2–3 times per edit and on every preview tick. Icon SVG paths are re-parsed each
  time.
- **Dynamic geometry**: cosmetic thread helices and sketches are rebuilt and re-uploaded every frame.
- **Saving and startup**:
  - Autosave re-hashes and recompresses attachments every time; cache them per array instance.
  - Startup: measure with `TieredCompilationQuickJit` on, and start `Warmup` after the window opens.

## Tried and rejected

- **`Roots.Bisect` → Illinois/Brent**: about 2% on threads, but results changed on 136 of 358 feature hashes.
- **`ComputeBounds` sharing the face's cached domain**: changes results. `Solid.Bounds` caches a finer domain that
  booleans then reuse, which makes them order-dependent and slower to classify.
- **Anisotropic Morton scaling in the ear-clipping index**: slower on thread outlines; square scaling is kept.
- **Plane tangent to a cone along a generator, or tangent to a torus**: left traced on purpose (tangential contact).

## Checking a change

All of these run from `tests/SpryCad.Tests`, built with `dotnet build -c Release`.

- **Results bit for bit**: `dotnet run -c Release --no-build -- determinism <file.sprt> 1` prints a hash per feature.
  Run it before and after (another process, or a `git worktree` of the old commit) over `samples/*.sprt` and
  `samples/demos/*.sprt`, and diff the output.
- **Display bit for bit**: run the same with `FC_DET_MESH=1`, which adds a hash of the final facets and display mesh.
- **Geometry only**: `FC_DET_GEOMETRY=1` leaves the edge sample cache out of the hashes (which edges are sampled, at
  which tolerance); `FC_DET_PRINT=1` prints each feature's fingerprint after its hash, to see which part changed.
- **Within tolerance**, when results are allowed to change:
  - `-- massprops <file>` gives exact volume, area and inertia per body.
  - `-- verify-demos <dir>` rebuilds every demo and its variants, checks they are closed, writes STEP, and prints
    volumes.
  - Compare both against the old build.
- **Tests**: `dotnet run -c Release --no-build` runs them all. Pass a name fragment to filter.
- **Timing**: machines here are often busy (other agents build and test). Alternate old and new builds run for run
  (`bench <file> 5`, two rounds each) instead of trusting one pair of numbers.

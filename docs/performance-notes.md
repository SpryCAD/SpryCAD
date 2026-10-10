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
| `e323738` | `Classify`'s refinement: near segments found through the 16-segment boxes, coedges no nearer than the band not measured, fine UV mapped only for the crossing test, crossing parity as the polygon's flipped by the swapped segments, the exact distance over the swapped coedges only (results bit for bit; `sec.midclass` 2–3× faster: handwheel 25 to 9 ms, spur-gear-nice 30 to 15 ms) |
| `ff6ab13` | `BSplineCurve.Interpolate` stores only the band of its system (4p + 1 entries a row, every entry the elimination reads or writes) instead of a dense (n+1)² matrix and its clone; the dense matrix is rebuilt only for the general fallback (results bit for bit; 3,000 points: 144 MB to 0.3 MB) |
| `3f2183d` | Screw-surface ray casts: the crossings of a screw surface found once per ray when several faces share it (a thread's seam-split faces); steps of the march outside the generator's radial band about the axis (padded by its sample spacing and the hit tolerance) not sampled; the closest-point seeds' frame coordinates taken once (results bit for bit; `cls.rays` about 1.5–2× faster on the threaded parts) |
| `1a00a45` | Mass properties with exact surface derivatives: `NSurface.TryPartials` for B-splines, screw surfaces and offsets of B-splines (the normal's derivative from the base's second derivatives), `PipeSpine.SurfacePartials` along a pipe's spine (the frame's derivative from the spine's second derivative and the interpolated normal), instead of central differences (results within 3e-10 relative; spoked handwheel's exact integration ~450 to ~300 ms) |
| #8 | Mass properties: a contact curve's panels end at the spine's nodes (the curve is only C1 there) with 4 Gauss points each, instead of uniform panels across them with 8 (spoked handwheel 2.5e-9 nearer the converged volume, exact integration ~270 to ~155 ms). `Topology.Build` welding once for the quick and the full pass was tried: the quick pass already succeeds on every sample, nothing to gain |
| #8 | `DisplayMesh` shares vertices within a UI face (a corner reuses an earlier one at the same point with the same normal): the triangles as drawn are the same (`FC_DET_MESH_TRIS`), 3.5–5.5× fewer vertices (spoked handwheel 253k to 46k, 6.1 to 1.1 MB of positions and normals); `bench` prints the display mesh's triangles and vertices |
| #8 | Tessellator triangles name their corners in their face's point list (`Face.Pool`, `PoolA..C`): `Topology.Build` welds each point once (the welder gives a bit-identical point its earlier index, so the result is the same bit for bit; spoked handwheel `topo.weld` ~50 to ~33 ms, display mesh ~10–15% faster) |
| #8 | Small items: `SplitFace` prunes dangling sections from a worklist (the same edges remain in any order of removal), `AddSec` keeps a set beside each face's list, STEP import finds both units in one pass and compares type names ordinally, `Solid.Combine`'s unreachable block removed (results bit for bit) |

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
   - `ProjectToIntersection` starts every point from scratch (warm starts would change results).
   - Done: the block test's grid-node points are kept (blocks share their corners and edge middles) and reused by
     the grid values: `trace.grid` about 40% less (spur-gear-nice 58–68 to 33–38 ms, handwheel 76–93 to 52–58 ms
     summed), bit for bit. What is left of tracing is the projection (~250 ms summed on spur-gear-nice) and the march.
2. **`NBoolean.FitSpline` and `NSpline.Param`**: done (see above). The `NSpline` constructor still evaluates 129
   samples for the scan; `Param` still ignores `hint`.
3. **`NDomain.Classify`, `BoundaryDistance` and `ContainsUV`**: done (see above). What is left of `sec.midclass` is probably
   mostly `Map` (the surface's `UV`) and `Project`; `ExactBoundaryDistance` is still linear in the coedges.
4. **Vertex matching in `NBoolean`**: done (see above). In `bool.edge splits` what is left is mostly
   `split.crossings` (curve × curve on faces) and the edge/surface roots.
5. **Edge sample cache**: done (see above).
6. **B-spline interpolation**: done (see above).
7. **NURBS core** (`NNurbs`): `PoleAt` reads two flags instead of LINQ over `Poles` (in every Newton step). Summing
   the six derivative sums in one pass over flat poles was tried: bit for bit, but no measurable gain (a 4×4 cubic:
   `DersBasis` twice per call and the quotient rule dominate). Homogeneous poles in one pass would change rounding.
   Newton stops only on a step below 1e-15; a residual test would change results.
8. **Screw-surface ray casts**: done for what stays bit for bit (see above). Each step inside the band still runs an
   unhinted `Closest` (17 seeds, 3 Gauss-Newton solves). Warm-starting from the previous step would follow one turn
   where the nearest point jumps between turns; skipping by the step's distance is not safe either, since `Closest`
   can return a farther foot. Both would need parity checks against the full march.
9. **Mass properties**: done (see above). Against panels at the frame's samples too (the pipe's frame is
   interpolated linearly, 8 samples a spine segment), the result moves by 1e-12 at 7× the cost: the nodes are what
   matters. Other curves (helices, fitted splines) still converge slowly: 4× more panels moves the acme screw's Ixx by
   1e-6. The curves' `Deriv` is a central difference (two projections each for a contact curve).
10. **Display pipeline**:
    - `DisplayMesh` vertices shared: done (see above).
    - `NTess` triangles carry their point list's indices: each point welded once (see above). They are still one
      `Face` object (and a 3-point loop array) per triangle; `Solid.Faces` is read widely. What is left of
      `topo.weld` is the grid search for each distinct point (~33 ms on the spoked handwheel).
    - `Topology.Build` welding once for its quick and full passes was tried: the quick pass succeeds on every sample.
11. **Small items**: `SplitFace` pruning, `AddSec`, STEP units and ordinal names, `Solid.Combine` done (see above).
    Left:
    - `NFace.ComputeBounds` builds a throwaway domain. Sharing the cached one changed results; see *Tried and rejected*.
    - `NFace.Coedges` is a LINQ `SelectMany` used in hot loops.
    - `SplitFace`'s duplicate-section check is O(s²) (small s in practice).

## Core and app: open items

- **Sketch solver** (`Sketch/Solver.cs`): measure with `-- sketchbench [samples dir] [rounds] [grid sizes]` (solve,
  DOF analysis, ten drag steps and the over-define check on every sample sketch and on grids of dimensioned
  rectangles, with a hash of the results to compare builds).
  - Done: Gauss-Newton solves each independent cluster of equations as its own block (J Jᵀ is block diagonal, and a
    Cholesky of it gives exact zeros across blocks: bit for bit), from a Jacobian kept at each equation's own variables;
    a cluster with more equations than variables (a drag's pull and hold terms) is solved in its variables,
    (JᵀJ + cI) Δ = Jᵀ F (the same step mathematically; drags round differently, rebuilds of every sample are bit for
    bit). Ten drag steps: tooth-gap sketch 35 to 10 ms, 6×6 grid 5.7 s to 57 ms, 10×10 grid 135 s to 0.35 s.
  - Left: skipping converged clusters; equations with no variables; analytic derivatives (the Jacobian is central
    differences); `Analyze` and `WouldOverDefine` reduce the whole dense Jacobian (6–20 ms on the 10×10 grid).
- **Mate solver** (`Assembly/MateSolver.cs`): `matebench` on a stack of gears: 200 components solve in ~360 ms, a drag
  step ~60 ms (100: 64 and 28 ms); one connected piece, so splitting does not help it. Already sparse. Solve the connected pieces separately and skip those
  already within tolerance. `Settle` runs two full solves.
- **Regenerator**:
  - Cancellation: done. The rebuild's token is ambient in the kernel (`Kernel.Cancellation`), checked between a
    boolean's stages and a blend's edge groups; the kernel's catch-alls let `OperationCanceledException` through, so a
    cancellation is never cached as a failure or sent to the facet fallback. Further checks belong on the calling
    thread (inside a `Parallel` body the exception arrives wrapped unless the loop has the same token).
  - `DocumentSession`: done (no display meshes for a result a newer request has superseded).
  - Equations: done. One naming another still pending waits for it without being evaluated (no exception per pass);
    the pass limit that stopped a chain listed after the ones it uses (three or more) before its end is gone.
  - `ResolveDir` from an edge tessellates the whole intermediate body; try `ResolveNativeEdge` first, as the other
    resolvers do.
- **Viewport** (`ViewportControl`, `SketchEditor`):
  - Sketch overlay: relation, On Edge and anchor marks are kept by id and repositioned (no new controls, brushes or
    native cursors per update). Measured with `SPRYCAD_PROFILE_OVERLAY=1` (the log gets each 2 s's updates, average
    and worst time): on the gear's tooth-gap sketch (36 relations, mostly dimensions) hovering and zooming cost
    0.74–0.94 ms an update before and 0.32–0.41 ms after; entering the sketch 143 to 86 ms worst. Under a millisecond
    either way, so coalescing to once per frame and caching the label layout wait for a sketch where it shows.
  - `PickVertex` allocates per edge per move with no cull.
  - Assembly hover ray-tests every triangle; cull by face boxes as `PickItem` does.
- **Sketch lookups**: `Sketch.Point`, `Entity` and `P` are linear scans used per entity per frame. Selection is a
  `List`.
- **Feature tree**: no longer rebuilt for a preview's result (it shows the document and the real result, which a
  preview leaves alone); icon paths parsed once; one rollback-bar cursor. Measured with `SPRYCAD_PROFILE_TREE=1`
  editing Fillet1 of spur-gear-nice with four previews: one rebuild (the cancel) instead of five; a rebuild of the
  18-feature tree costs 3–11 ms either way. Still rebuilt in full on each document change and real result.
- **Dynamic geometry**: cosmetic thread helices and sketches are rebuilt and re-uploaded every frame.
- **Saving and startup**:
  - Autosave: done. It writes a copy of each part off the UI thread (one autosave at a time), and attachment hashes
    are kept per byte array. `-- savebench <step file> [MB]`: with a 20 MB imported model the UI waited 260–560 ms an
    autosave (hash and Optimal deflate on the UI thread), now ~0.1 ms; the save itself ~265 ms (hash cached).
  - Startup: done, measured from launch, measured from launch (with a part) to its first rebuild finished, on installer-style
    ReadyToRun self-contained builds, 6–8 launches each, medians. `TieredCompilationQuickJit` back on (the default):
    spur-gear 1498 to 1214 ms (rebuild 382 to 362 ms), hand-wheel 1271 to 1128 ms (rebuild 137 to 107 ms).
    Tried and rejected: starting `Warmup` once the window has opened made the first rebuild slower (spur-gear
    1874 ms, rebuild ~600 ms, with or without QuickJit) as the part's rebuild then raced the warm-up.

## Tried and rejected

- **`ClassifyAll` box test before `CoincidentWith`**: slower (`cls.lone` 8 to 50 ms on spur-gear-nice). `FBox` computes a
  face's bounds on first use, often for faces nothing else needs; `CoincidentWith` rejects most pairs at once (kinds).
- **`Topology.Build` welding once for the quick and the full pass**: the quick pass succeeds on every sample.

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
- **Display bit for bit**: run the same with `FC_DET_MESH=1`, which adds a hash of the final facets and display mesh. With
  `FC_DET_MESH_TRIS=1` as well, the display mesh is hashed as drawn (each index's position and normal), so a change of
  vertex layout alone does not show.
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

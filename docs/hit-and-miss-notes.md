# Hit and miss engine demo: what it needed

Notes made while modeling the hit and miss engine demo (`tests/SpryCad.Tests/Demos.Engine.cs`): things SpryCAD
could not do, did not do well, or that a designer would reach for. Each says what was done instead.

Commands (from the repository root):

- `dotnet run --project tests/SpryCad.Tests -c Release -- verify-engine [outDir|-] [part]`: rebuilds each part (or
  the ones whose name contains `part`), reports failures, warnings and sketches not fully defined; with `outDir`, saves
  each part and a picture of it there.
- `... -- engine-steps <part>`: one part rebuilt a feature at a time, each step's time.
- `... -- engine-asm [outDir]`: assembles the engine, checks the mates hold as placed, turns a flywheel two turns and
  checks the piston, cam gear and exhaust valve follow; with `outDir`, writes `hit-and-miss.sasm` and pictures.

## Kernel

- **Intermittent hang rebuilding the flywheel with wider spokes. FIXED (branch `blend-quick-fixes`).** `HmFlywheel`
  with `SpW = 32`, `SpT = 18` (instead of 26 / 14), saved as `repro/flywheel-wide-spokes.sprt`; reproduced 2 runs in 6
  with the saved file (`determinism <file> 1`, timeout 90 s). Cause: the spoke-to-hub fillet's exact boolean failed
  (next item), and `NativeBlend.Apply` (tools not trimmed) then took the faceted fallback, which stops at a 4 s
  wall-clock deadline. Mostly it timed out and the fillet failed; sometimes it finished just inside 4 s and left a
  faceted body, and every later boolean on a faceted body runs `Csg.RunFacets` with no deadline (minutes). Fix: that
  branch of `Apply` is exact or fails with a message, like the trimmed branch and the pattern code already were. The
  file now rebuilds in about 2.2 s every time (12 of 12), every run the same.
- **The same wide-spoke flywheel: the spoke-to-hub fillet fails. FIXED (same branch).** "result invalid: edge used 3
  times (NContact ... on F4.R.L7,F4.R.L7,F9.0.Blend)". Not the hub's seam: `RollingBall.BuildComposite` gave an open
  chain's far end the chain's start vertex (`VV[k]` built from `ChainVertex(k % n)`, and `n % n` is 0), so the tool's
  end cap and last chain edge ran back across the spoke foot to the start. Only open chains whose B face changes (an
  oval spoke's side and arcs) go that way. The seed fillet now builds. The pattern's other instances still fail on
  these wide spokes, rightly: each spoke's foot spans ±31° of the hub, 62° for spokes 60° apart, so neighbouring feet
  overlap and no fillet fits between them. *Instead:* spokes 26 x 14 (or a larger hub, or fewer spokes).
- **Patterned spoke-to-hub fillet fails on some instances. NOT A BUG.** Flywheel (26 x 14 spokes): filleted R5 into the
  hub's cylinder, then patterned 6 times: instances 2 and 6 (60° and 300°) fail with "the fillet does not fit". The
  feet are only about 4.6° (2.2 mm) apart on the hub, and the seed's R5 fillet reaches about 40° round the hub, past
  where instance 2's foot starts (32°): instance 2's edge chain is cut by the seed's fillet (`NativeBlend.Debug`
  shows 6 edges instead of 4). Instances 3 to 5 are copies of the seed's tool, applied without the fit check, which is
  why they "work". *Instead:* R3. To fix (messages): say "runs into the fillet of instance 1" instead of "does not
  fit"; and check the copied instances against each other too, or the pattern quietly overlaps them.
- **Undrafted spoke variant fails differently.** The same spoke with no draft: "the fillets that run into neighbouring
  geometry also run into each other where both are tangent to the same face; that corner cannot be blended yet"
  (raised for "Spoke to rim"). *Instead:* drafted spokes. To fix: as for the casting below.
- **A fillet round the foot of a box whose corners are filleted.** Casting: the body (a drafted rectangle, its four
  vertical edges filleted R20) blended R6 into the base plate's top, one edge picked, tangent edges following: with
  draft, "the fillets that run into neighbouring geometry also run into each other ... cannot be blended yet";
  without draft, "the edges are not all between the same two faces" on each corner fillet's edge. A very common
  casting operation. *Instead:* no blend. To fix:
  - "Run into each other" (`NativeBlend.Apply`, the trimmed branch) is a catch-all for a failed boolean, not a corner
    step that gives up. Each tool is trimmed against the body alone (`TrimToEdge`), then applied one at a time; two
    blends tangent to the same face meet tangentially and the second boolean fails. Cheaper: where neighbouring
    blends meet tangentially, build them as one rolling-ball chain (`RollingBall.BuildComposite`) instead. Proper:
    after building the tools, find pairs on the same support face whose lumps overlap, cut each along the curve where
    the two blend surfaces cross (`RollingBall.March` can follow it), and join the pair into one tool (~300-500
    lines); a smooth corner patch (setback vertex blend) is 1000+ lines and a new surface.
  - "Not all between the same two faces" (`RollingBall.BuildComposite`): it needs one surface (A) that every piece of
    the chain lies on, taken from `pieces[0]` only, and `LineTool`/`ArcTools` claim line and arc edges before the
    chain reaches it, so a mixed loop gets split. To fix: with tangent propagation, build the whole chain as one tool;
    choose A by vote across the pieces, either side; where B changes at an angle (not tangentially), the same corner
    join as above (~150-300 lines). Not yet clear why the undrafted R20 corners skip `ArcTools`: the
    `NativeBlend.Debug` line in `Apply` prints the surface kinds and would show it.
- **A sketch of overlapping closed loops extrudes to a broken body, silently.** Rocker: a rectangle with circles
  overlapping its ends and middle, in one sketch, extruded: no error or warning, but the body is open (not a valid
  solid). Either union the regions or say why. *Instead:* three sketches, three extrudes. To fix:
  `ProfileExtractor.Extract` only joins curves at shared sketch points (crossing curves are never split), and
  `ProfileRegion.FromLoops` (`Profile.cs`) tests nesting with one vertex, so the loops become overlapping regions;
  `Builders.Extrude` then falls back to facets without a word when `Validate()` fails. Smallest: detect overlapping
  regions and throw "The sketch's closed contours overlap" (~40 lines), and warn on the facet fallback. Expected
  result: extrude each region and `Csg.UnionAll` them (~15 lines). Full: split the curves at their crossings into
  planar regions in the extractor (~200 lines; also lets a user pick regions).

## Messages

- **A failed seed hidden by the pattern. FIXED (branch `blend-quick-fixes`).** When a seed fillet failed, the pattern
  said "Only extrude, revolve, ... can be patterned". The rebuild's feature statuses now reach the feature context
  (`Ctx.Earlier`), and a pattern or mirror whose seed was not built says "<seed> failed: <its error>" (or "<seed> is
  suppressed").

## Modeling features

- **Loft.** Casting transitions (hopper to barrel, body to bearing towers) and the muffler's bulb would be lofts.
  *Instead:* drafted extrudes, revolves. To add: a `LoftFeature` beside `SweepFeature` (`Features.cs`), regenerated
  like `DoSweep` (regions from each section sketch, the kernel tool, `Validate`, `ApplyTool`, `State.Tools` so it can
  be patterned); the kernel skinning between sections next to `NSweep.Sweep`, with the sections' `ProfileLoop`s
  matched vertex for vertex or resampled.
- **Fillet/blend of a whole cast body in one go** (all concave edges between two features): castings need dozens of
  blends, picked edge by edge here (and several fail, above). To add (~150-250 lines, useful once the corner cases
  above work): a scope on `FilletFeature` (features A and B, or a body, "all concave") beside `Edges`; in
  `TryNativeBlend`, before `NativeBlend.Expand`, the body's edges whose two faces' tags start with the two feature
  ids, kept when concave (the `LineTool`/`RollingBall` test); `BlendSpec` to carry the scope for patterns; chains that
  fail left out with a warning instead of all or nothing; the page in `OpenBlendPage`.

## Assembly

- **Subassemblies** (coming: worktree `nested-assemblies`): the engine would group as crank and flywheels, valve
  train, cylinder head with its fittings, mixer, and fasteners.
- **Cam / one-sided contact.** The exhaust valve should be worked by a cam (a lobe with a dwell) and close on its seat
  while the follower lifts off. Mates have no cam (a tangent to a non-round profile) and contact is always two-sided.
  *Instead:* a round eccentric on the cam gear and tangents all the way to the valve: the valve lifts as a sine over
  two crank turns (0 to 6 mm), never closed for long. To add:
  - One-sided contact (~30 lines): distance limits already work as penalties in `Residuals.Range` (`Mates.cs`); allow
    no upper limit, and a one-sided tangent whose residual is `max(0, -gap)`. Something must still hold the follower
    on (a spring, gravity, a drag), or it floats.
  - Cam mate (~400-800 lines): `MateGeomKind.Profile` (the extrude's sketch profile, or a section of the face, kept on
    the `MateRef` so it survives lightweight mode) and `MateType.Cam`, whose residual is the follower's distance to the
    profile's closest point (Newton on the profile's parameter, seeded from the last solve) minus its radius, plus a
    row keeping the axes parallel. `MateSolver`'s Jacobians are finite differences, so no derivatives to write; the
    profile must be smooth (lines and arcs tangent) to converge. `MateGeom.Of` today takes planes, cylinders, cones
    and spheres only.
- **Hit and miss governor.** The flyweights should swing out with speed and latch the exhaust valve open: no mate
  can express it. *Instead:* the weights locked to the flywheel. Needs one-sided contact (above), something driven by
  speed, and a latch: beyond a mate.
- **Flexible parts.** The valve springs should shorten as the valve opens. *Instead:* locked to the head. To add
  (~200-400 lines): `Component.Drivers` (a part parameter set from an expression over mate values), evaluated with
  `Expr` after `MateSolver.Solve`, the part rebuilt through `Configure`/`LoadPart`. Turning the flywheel would make a
  configuration per position, so quantise or skip the cache; or, for looks only, stretch the mesh along its axis in
  `PlacedMesh`.
- **Many fasteners.** 24 screws (6 head, 4 bearing caps, 2 rod cap with nuts, 8 hub caps, 4 hold-downs), each mated by
  hand (concentric + under the head on the face): a "smart fastener" (pick a hole, get a screw of the right size and
  length, mated) would save most of the mating. To add (~300-600 lines): `HoleFeature` and `ThreadFeature` know the
  diameter, depth, counterbore and thread, but the assembly only sees display faces. Hole records (axis, entry point,
  diameter, depth, thread, seat face) in the rebuild's state, kept on `PartDisplay` and in the mesh cache; then an
  "Insert fastener" command doing what the demo's `Screw`/`Seat` do.

## Appearance

- **Colour per face or per body.** The flywheels' rims are turned bright steel, the spokes red; the cylinder's barrel
  band is turned; the oiler is brass and glass. A part has one colour. *Instead:* one colour per part (the oiler's
  glass a part of its own).
- **Transparency** for the oiler's sight glass.
- To add both (~150 lines plus file formats): the renderer already takes alpha (`GlRenderer.DrawMesh`, `uColor` is a
  vec4), overdraws faces by name (`DrawInstances`, used for mate highlights) and has a blended pass. Missing: colours
  with alpha by feature id (face names change with topology) and by body, saved in the part, the assembly and the mesh
  cache; faces batched by colour, opaque first, then the transparent ones back to front with depth writes off;
  `Thumbnail.Render` taking per-face colours.

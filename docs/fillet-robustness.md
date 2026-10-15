# Fillet and chamfer robustness

A stress run (`SpryCad.Tests blendstress [filter]`) puts fillets and chamfers on many parts and many selections of
their edges, and scores each result. It is the measure for the work below: each step should raise the "right"
count and lower "wrong", and the run is the regression check afterwards.

## The stress run

Parts: prisms of many outlines (block, a corner cut off, trapezoid, triangle, L, a thin wedge, hexagon), several of
them drafted; a block with a boss (square, round, flush to a side, drafted), a pocket (square, round) or a hole;
and every part in `tests/SpryCad.Tests/models`.

Selections on each: the edges of each planar face, the edges at each corner, single edges (tangent chains
followed), pairs meeting at a corner, and (the generated parts) all edges. Fillet and chamfer, each small, medium
and large (0.08, 0.2 and 0.4 of the part's shortest straight edge).

Each case is scored:

- **right**: built; the body valid; its display mesh closed; no tool's end left standing (a "wing"); the volume
  moved the right way (down where the edges are all convex, up where all concave); written as STEP with every
  fillet face's edges on its surface as written (so each gets its parameter curve).
- **refused**: the feature failed, saying why. What a CAD system does for a size that does not fit; not a bug in
  itself, though some refusals are cases we should be able to do.
- **wrong**: built, but one of the above not so. The worst outcome: a body that looks done and is not.

Options: `FC_STRESS_ALL` prints every case (else only those not right); `FC_STRESS_CSV=path` writes every case to a
CSV file; `FC_STRESS_SAVE=dir` saves each wrong case as a part file to look at (open it, or `zoom` it).

## Baseline (before this work)

2712 cases: 2257 right (83%), 203 refused (7.5%), 252 wrong (9.3%). Tools: 8807 applied directly, 4146 by boolean.

| | right | refused | wrong |
|---|---|---|---|
| fillet | 1076 | 86 | 194 |
| chamfer | 1181 | 117 | 58 |
| corner selections | 381 | 24 | 87 |
| face selections | 729 | 82 | 71 |
| all edges | 48 | 18 | 36 |

Wrong, by what: a tool's end left standing 179, export 49, volume 24.

The harness was tightened after this baseline (sharp edges picked only, sizes from edges not tiny beside the part,
caps flush with a face not counted as wings, a blended edge checked actually blended, slow cases scored): the case
set is 2700 now, and a few hundred of the baseline's verdicts were the harness's own mistakes.

## After (branch fillet-robustness)

2700 cases: 2471 right (91.5%), 165 refused (6.1%), 64 wrong (2.4%). Tools: 13662 applied directly, 3256 by boolean;
305 cases use the boolean at all.

| | right | refused | wrong |
|---|---|---|---|
| fillet | 1234 | 69 | 47 |
| chamfer | 1237 | 96 | 17 |
| corner selections | 452 | 30 | 10 |
| face selections | 784 | 59 | 27 |
| all edges | 94 | 1 | 7 |

Wrong, by what: a tool's end left standing 35, export 14, slow 6, volume 5, edge not blended 3, open mesh 1.

Every plain prism (block, corner cut, trapezoid, triangle, hexagon, L, thin wedge, drafted or not), the blocks with
a round boss or pocket, and the block with a square pocket: no case wrong (the test suite's "Blend stress: prisms"
gate keeps the prisms so).

### Corners

Two blends of one kind on a shared face, meeting at a vertex, are classified by the corner edge between their other
faces:

- square to the shared face (a box, an L's inside corner): both end on one plane through the corner, exact there,
  and the tools glue end to end;
- leaning from square, convex (a drafted block) or concave (a drafted boss's side edge running down onto its foot's
  chamfer): their faces and the two beside them meet at no one point, and a plane leaves a sliver or wing. Each runs
  on to the face it meets, and the overlapping tools are joined by an exact union.

Blends of both kinds meeting at a vertex are staged, the odd kind out first. Fillets take with them the edges of
their kind they meet (a corner of three gets its patch, rather than one fillet later rolling over another's round
as big as itself); where the staging leaves a tool's end standing or fails, the other kind goes first (a boss's
side and top edges, then its foot rolling round their rounds), kept where it leaves fewer.

A fillet stopping where its edge runs on tangentially into one not picked (a boss's foot fillet, wrapped round the
side fillet, reaching the next face) ends square: correct, and counted as a tool's end standing here. A variable
radius fading the fillet out there is the better end, for later.

Still wrong: the drafted boss (14, mostly those square ends), the handwheel's grips (13), the brake duct (7, corner
chamfers slow on its lofted faces), the shelled parts (14), the drafted L (6). Per-edge tolerances showed no near
misses worth the change and were not done.

## Speed

`SpryCad.Tests blendtime [reps] [filter]`: the blend features of every test model, demo and case, each the best of
`reps` rebuilds (`FC_TIME_PROF=1`: where the time goes). Same machine, alternating runs, best of 7 each:

| | baseline (master) | this branch |
|---|---|---|
| all blend features, median of 3 runs | 1207 ms | 1211 ms |
| all blend features, best of 3 runs | 1176 ms | 1200 ms |

Within the run-to-run spread. Faster: a box's top edges (1.8 -> 0.5 ms), pipe flange (24 -> 22.5), a handwheel
chamfer that took twelve minutes before the booleans were made exact or failed (now under a second). A few
percent slower: drafted chamfers (their ends on the plane the faces cross on, no sliver: slightly different tools
for the boolean). Tools overlapping round a corner are joined by an exact union only where they are convex and
their edges come within a blend's size of each other (a check of 0.1 ms).

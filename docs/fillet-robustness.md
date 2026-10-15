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

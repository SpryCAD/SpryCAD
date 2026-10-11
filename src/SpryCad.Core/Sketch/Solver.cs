using SpryCad.Kernel;

namespace SpryCad.Core.Sketching;

public sealed class SolveResult
{
    public bool Converged { get; init; }
    public double Residual { get; init; }
    public SketchStatus Status { get; init; }
    /// <summary>Point ids whose position is not fully determined.</summary>
    public HashSet<int> FreePoints { get; init; } = new();
    /// <summary>Circle ids whose radius is not determined.</summary>
    public HashSet<int> FreeRadii { get; init; } = new();
    public int Dof { get; init; }
}

/// <summary>
/// Numerical geometric constraint solver. Unknowns are free point coordinates and circle radii.
/// Uses damped Gauss-Newton in its minimum-norm form, so under-constrained sketches move as
/// little as possible (the behaviour users expect while dragging).
/// </summary>
public static class SketchSolver
{
    const double Tol = 1e-9;

    readonly struct PRef
    {
        public readonly int Ix, Iy; public readonly double Fx, Fy;
        public PRef(int ix, int iy, double fx, double fy) { Ix = ix; Iy = iy; Fx = fx; Fy = fy; }
        public Vec2 Get(double[] x) => new(Ix >= 0 ? x[Ix] : Fx, Iy >= 0 ? x[Iy] : Fy);
    }

    readonly struct RRef
    {
        // circle: radius variable; arc: distance center-start
        public readonly int Ir; public readonly double Fr; public readonly bool IsArc; public readonly PRef C, S;
        public RRef(int ir, double fr) { Ir = ir; Fr = fr; IsArc = false; C = default; S = default; }
        public RRef(PRef c, PRef s) { Ir = -1; Fr = 0; IsArc = true; C = c; S = s; }
        public double Get(double[] x) => IsArc ? (S.Get(x) - C.Get(x)).Length : (Ir >= 0 ? x[Ir] : Fr);
    }

    sealed class Eq
    {
        public int[] Vars = Array.Empty<int>();
        public Func<double[], double> F = null!;
        public double Weight = 1;
        public int ConstraintId = -1;
    }

    sealed class System
    {
        public readonly List<double> X = new();
        public readonly Dictionary<int, (int ix, int iy)> PointVars = new();
        public readonly Dictionary<int, int> RadiusVars = new();
        public readonly List<Eq> Eqs = new();
        public Sketch Sketch = null!;
        public Dictionary<int, PRef> PRefs = new();
        public Dictionary<int, RRef> RRefs = new();
    }

    static System Build(Sketch sk, IReadOnlyDictionary<int, Vec2>? fixedOverride, bool includeDragAsFixed)
    {
        var s = new System { Sketch = sk };
        foreach (var p in sk.Points)
        {
            var pos = p.P;
            bool fixedPt = p.Fixed;
            if (fixedOverride != null && fixedOverride.TryGetValue(p.Id, out var target))
            {
                pos = target;
                if (includeDragAsFixed) fixedPt = true;
            }
            if (fixedPt) s.PRefs[p.Id] = new PRef(-1, -1, pos.X, pos.Y);
            else
            {
                int ix = s.X.Count; s.X.Add(pos.X); s.X.Add(pos.Y);
                s.PointVars[p.Id] = (ix, ix + 1);
                s.PRefs[p.Id] = new PRef(ix, ix + 1, 0, 0);
            }
        }
        foreach (var e in sk.Entities)
        {
            if (e is SkCircle rc && (rc.IsReference || rc.Link != null))
            {
                s.RRefs[rc.Id] = new RRef(-1, rc.R); // model circle (referenced or converted): radius is fixed
            }
            else if (e is SkCircle c)
            {
                int ir = s.X.Count; s.X.Add(c.R);
                s.RadiusVars[c.Id] = ir;
                s.RRefs[c.Id] = new RRef(ir, 0);
            }
            else if (e is SkArc a)
            {
                s.RRefs[a.Id] = new RRef(s.PRefs[a.Center], s.PRefs[a.Start]);
                var C = s.PRefs[a.Center]; var S = s.PRefs[a.Start]; var E = s.PRefs[a.End];
                AddEq(s, -1, x => (S.Get(x) - C.Get(x)).Length - (E.Get(x) - C.Get(x)).Length, C, S, E);
            }
        }
        foreach (var c in sk.Constraints)
        {
            if (c.Driven) continue;
            try { AddConstraint(s, c); } catch { /* malformed constraint: ignore */ }
        }
        return s;
    }

    static void AddEq(System s, int cid, Func<double[], double> f, params PRef[] pts) => AddEqV(s, cid, f, pts, Array.Empty<RRef>());

    static void AddEqV(System s, int cid, Func<double[], double> f, PRef[] pts, RRef[] rads)
    {
        var vars = new List<int>();
        foreach (var p in pts) { if (p.Ix >= 0) vars.Add(p.Ix); if (p.Iy >= 0) vars.Add(p.Iy); }
        foreach (var r in rads)
        {
            if (r.IsArc) { foreach (var p in new[] { r.C, r.S }) { if (p.Ix >= 0) vars.Add(p.Ix); if (p.Iy >= 0) vars.Add(p.Iy); } }
            else if (r.Ir >= 0) vars.Add(r.Ir);
        }
        s.Eqs.Add(new Eq { Vars = vars.Distinct().ToArray(), F = f, ConstraintId = cid });
    }

    static (PRef a, PRef b) Line(System s, int id)
    {
        var l = (SkLine)s.Sketch.Entity(id)!;
        return (s.PRefs[l.P1], s.PRefs[l.P2]);
    }

    static bool IsPoint(System s, int id) => s.PRefs.ContainsKey(id);
    static bool IsLine(System s, int id) => s.Sketch.Entity(id) is SkLine;
    static bool IsRound(System s, int id) => s.Sketch.Entity(id) is SkCircle or SkArc;

    static PRef CenterOf(System s, int id) => s.Sketch.Entity(id) switch
    {
        SkCircle c => s.PRefs[c.Center],
        SkArc a => s.PRefs[a.Center],
        _ => throw new InvalidOperationException(),
    };

    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>
    /// Offset: each copy at the distance from its original, on its side. Equations per copy point (two each, chosen
    /// so they are never tangent to each other, which would hide the point's position from the analysis):
    /// two lines meeting: the distance from each; a line and an arc meeting smoothly: the line distance and the
    /// arc's radial line; meeting at an angle: the line distance and the radius; an open end: across from the
    /// original's end (square to a line, on an arc's radial line). An arc copy shares its original's center, and
    /// its end point takes its radius from the arc's own equal-radius equation, so it gets one equation less.
    /// </summary>
    static void AddOffset(System s, SkConstraint c)
    {
        var sk = s.Sketch; var r = c.Refs; int id = c.Id; var d = c.Value;
        var sides = c.Sides ?? Array.Empty<int>();
        // per copy point: its original point and the pairs it belongs to
        var at = new Dictionary<int, (int orig, List<(SkEntity o, SkEntity q, int side)> pairs)>();
        void Map(int q, int o, SkEntity oe, SkEntity qe, int side)
        {
            if (!at.TryGetValue(q, out var v)) at[q] = v = (o, new());
            v.pairs.Add((oe, qe, side));
        }
        for (int k = 0; k + 1 < r.Length; k += 2)
        {
            int side = k / 2 < sides.Length ? sides[k / 2] : 1;
            var o = sk.Entity(r[k]); var q = sk.Entity(r[k + 1]);
            switch (o, q)
            {
                case (SkLine ol, SkLine ql): Map(ql.P1, ol.P1, ol, ql, side); Map(ql.P2, ol.P2, ol, ql, side); break;
                case (SkArc oa, SkArc qa): Map(qa.Start, oa.Start, oa, qa, side); Map(qa.End, oa.End, oa, qa, side); break;
                case (SkCircle oc, SkCircle qc):
                    {
                        var ro = s.RRefs[oc.Id]; var rq = s.RRefs[qc.Id]; var sd = side;
                        AddEqV(s, id, x => rq.Get(x) - (ro.Get(x) + sd * d), Array.Empty<PRef>(), new[] { ro, rq });
                        break;
                    }
            }
        }
        // the equations, by kind
        void LineDist(int qp, SkLine o, int side)
        {
            var a = s.PRefs[o.P1]; var b = s.PRefs[o.P2]; var q = s.PRefs[qp];
            AddEq(s, id, x => { var u = b.Get(x) - a.Get(x); return Vec2.Cross(u, q.Get(x) - a.Get(x)) / Math.Max(u.Length, 1e-12) - side * d; }, a, b, q);
        }
        void Square(int qp, int op, SkLine o)
        {
            var a = s.PRefs[o.P1]; var b = s.PRefs[o.P2]; var q = s.PRefs[qp]; var p = s.PRefs[op];
            AddEq(s, id, x => { var u = b.Get(x) - a.Get(x); return Vec2.Dot(u, q.Get(x) - p.Get(x)) / Math.Max(u.Length, 1e-12); }, a, b, q, p);
        }
        void Radius(int qp, SkArc o, int side)
        {
            var cen = s.PRefs[o.Center]; var st = s.PRefs[o.Start]; var q = s.PRefs[qp];
            AddEq(s, id, x => (q.Get(x) - cen.Get(x)).Length - ((st.Get(x) - cen.Get(x)).Length + side * d), cen, st, q);
        }
        void Radial(int qp, int op, SkArc o)
        {
            var cen = s.PRefs[o.Center]; var q = s.PRefs[qp]; var p = s.PRefs[op];
            AddEq(s, id, x => { var w = p.Get(x) - cen.Get(x); return Vec2.Cross(q.Get(x) - cen.Get(x), w) / Math.Max(w.Length, 1e-12); }, cen, q, p);
        }
        // smooth joins, judged on the originals as they are
        bool Smooth(SkEntity e1, SkEntity e2, int op)
        {
            var p = sk.P(op);
            Vec2 Normal(SkEntity e) => e switch
            {
                SkLine l => (sk.P(l.P2) - sk.P(l.P1)).Normalized.Perp,
                SkArc a => (p - sk.P(a.Center)).Normalized,
                _ => default,
            };
            return Math.Abs(Vec2.Cross(Normal(e1), Normal(e2))) < 1e-6;
        }
        foreach (var (qp, (op, pairs)) in at)
        {
            var lines = pairs.Where(x => x.o is SkLine).ToList();
            var arcEnds = pairs.Where(x => x.q is SkArc qa && qa.End == qp).ToList();
            var arcStarts = pairs.Where(x => x.q is SkArc qa && qa.Start == qp).ToList();
            if (arcEnds.Count > 0)
            {
                // the arc's own equation sets this end's radius: one more
                var a = (SkArc)arcEnds[0].o;
                if (lines.Count > 0) { if (Smooth(lines[0].o, a, op)) Radial(qp, op, a); else LineDist(qp, (SkLine)lines[0].o, lines[0].side); }
                else if (arcStarts.Count > 0) { var b = (SkArc)arcStarts[0].o; if (Smooth(a, b, op)) Radial(qp, op, a); else Radius(qp, b, arcStarts[0].side); }
                else if (arcEnds.Count == 1) Radial(qp, op, a);
            }
            else if (lines.Count >= 2)
            {
                LineDist(qp, (SkLine)lines[0].o, lines[0].side);
                // two lines in line: their distances agree, so square across instead
                if (Smooth(lines[0].o, lines[1].o, op)) Square(qp, op, (SkLine)lines[0].o);
                else LineDist(qp, (SkLine)lines[1].o, lines[1].side);
            }
            else if (lines.Count == 1 && arcStarts.Count >= 1)
            {
                var l = (SkLine)lines[0].o; var a = (SkArc)arcStarts[0].o;
                LineDist(qp, l, lines[0].side);
                if (Smooth(l, a, op)) Radial(qp, op, a); else Radius(qp, a, arcStarts[0].side);
            }
            else if (lines.Count == 1) { LineDist(qp, (SkLine)lines[0].o, lines[0].side); Square(qp, op, (SkLine)lines[0].o); }
            else if (arcStarts.Count >= 1)
            {
                var a = (SkArc)arcStarts[0].o;
                Radius(qp, a, arcStarts[0].side);
                if (arcStarts.Count >= 2 && !Smooth(a, arcStarts[1].o, op)) Radius(qp, (SkArc)arcStarts[1].o, arcStarts[1].side);
                else Radial(qp, op, a);
            }
        }
    }

    static void AddConstraint(System s, SkConstraint c)
    {
        var r = c.Refs; int id = c.Id;
        switch (c.Type)
        {
            case CType.Offset:
                AddOffset(s, c);
                break;
            case CType.Horizontal:
            case CType.Vertical:
                {
                    PRef a, b;
                    if (r.Length == 1) (a, b) = Line(s, r[0]); else { a = s.PRefs[r[0]]; b = s.PRefs[r[1]]; }
                    if (c.Type == CType.Horizontal) AddEq(s, id, x => a.Get(x).Y - b.Get(x).Y, a, b);
                    else AddEq(s, id, x => a.Get(x).X - b.Get(x).X, a, b);
                    break;
                }
            case CType.Parallel:
            case CType.Perpendicular:
                {
                    var (a1, b1) = Line(s, r[0]); var (a2, b2) = Line(s, r[1]);
                    bool par = c.Type == CType.Parallel;
                    AddEq(s, id, x =>
                    {
                        var d1 = b1.Get(x) - a1.Get(x); var d2 = b2.Get(x) - a2.Get(x);
                        var l = Math.Max(d1.Length * d2.Length, 1e-12);
                        return (par ? Vec2.Cross(d1, d2) : Vec2.Dot(d1, d2)) / l;
                    }, a1, b1, a2, b2);
                    break;
                }
            case CType.Collinear:
                {
                    var (a1, b1) = Line(s, r[0]); var (a2, b2) = Line(s, r[1]);
                    AddEq(s, id, x => { var d = b1.Get(x) - a1.Get(x); return Vec2.Cross(d, a2.Get(x) - a1.Get(x)) / Math.Max(d.Length, 1e-12); }, a1, b1, a2);
                    AddEq(s, id, x => { var d = b1.Get(x) - a1.Get(x); return Vec2.Cross(d, b2.Get(x) - a1.Get(x)) / Math.Max(d.Length, 1e-12); }, a1, b1, b2);
                    break;
                }
            case CType.Equal:
                {
                    if (IsLine(s, r[0]) && IsLine(s, r[1]))
                    {
                        var (a1, b1) = Line(s, r[0]); var (a2, b2) = Line(s, r[1]);
                        AddEq(s, id, x => (b1.Get(x) - a1.Get(x)).Length - (b2.Get(x) - a2.Get(x)).Length, a1, b1, a2, b2);
                    }
                    else
                    {
                        var r1 = s.RRefs[r[0]]; var r2 = s.RRefs[r[1]];
                        AddEqV(s, id, x => r1.Get(x) - r2.Get(x), Array.Empty<PRef>(), new[] { r1, r2 });
                    }
                    break;
                }
            case CType.Tangent:
                {
                    int li = IsLine(s, r[0]) ? 0 : IsLine(s, r[1]) ? 1 : -1;
                    if (li >= 0)
                    {
                        var (a, b) = Line(s, r[li]); var ci = r[1 - li];
                        var cen = CenterOf(s, ci); var rad = s.RRefs[ci];
                        var sign = c.Sign;
                        // the line and an arc meeting at an end (fillets, tangent arcs, slots): the radius at that
                        // point is perpendicular to the line. The same geometry as the distance form below, but it
                        // pins the touching point (to the distance form, a point on both the line and the circle can
                        // slide along the line to first order, and is reported free)
                        if (s.Sketch.Entity(r[li]) is SkLine sl && s.Sketch.Entity(ci) is SkArc sa)
                        {
                            var shared = new[] { sl.P1, sl.P2 }.FirstOrDefault(p => p == sa.Start || p == sa.End, -1);
                            if (shared >= 0)
                            {
                                var tp = s.PRefs[shared];
                                AddEq(s, id, x =>
                                {
                                    var d = b.Get(x) - a.Get(x);
                                    return Vec2.Dot(d, tp.Get(x) - cen.Get(x)) / Math.Max(d.Length, 1e-12);
                                }, a, b, cen, tp);
                                break;
                            }
                        }
                        AddEqV(s, id, x =>
                        {
                            var d = b.Get(x) - a.Get(x);
                            return sign * Vec2.Cross(d, cen.Get(x) - a.Get(x)) / Math.Max(d.Length, 1e-12) - rad.Get(x);
                        }, new[] { a, b, cen }, new[] { rad });
                    }
                    else
                    {
                        var c1 = CenterOf(s, r[0]); var c2 = CenterOf(s, r[1]);
                        var r1 = s.RRefs[r[0]]; var r2 = s.RRefs[r[1]];
                        var ext = c.Sign >= 0;
                        AddEqV(s, id, x =>
                        {
                            var d = (c2.Get(x) - c1.Get(x)).Length;
                            return ext ? d - (r1.Get(x) + r2.Get(x)) : d - Math.Abs(r1.Get(x) - r2.Get(x));
                        }, new[] { c1, c2 }, new[] { r1, r2 });
                    }
                    break;
                }
            case CType.PointOnLine:
                {
                    var p = s.PRefs[r[0]]; var (a, b) = Line(s, r[1]);
                    AddEq(s, id, x => { var d = b.Get(x) - a.Get(x); return Vec2.Cross(d, p.Get(x) - a.Get(x)) / Math.Max(d.Length, 1e-12); }, p, a, b);
                    break;
                }
            case CType.PointOnCircle:
                {
                    var p = s.PRefs[r[0]]; var cen = CenterOf(s, r[1]); var rad = s.RRefs[r[1]];
                    AddEqV(s, id, x => (p.Get(x) - cen.Get(x)).Length - rad.Get(x), new[] { p, cen }, new[] { rad });
                    break;
                }
            case CType.Midpoint:
                {
                    var p = s.PRefs[r[0]]; var (a, b) = Line(s, r[1]);
                    AddEq(s, id, x => p.Get(x).X - (a.Get(x).X + b.Get(x).X) / 2, p, a, b);
                    AddEq(s, id, x => p.Get(x).Y - (a.Get(x).Y + b.Get(x).Y) / 2, p, a, b);
                    break;
                }
            case CType.Concentric:
            case CType.Coincident:
                {
                    var p = c.Type == CType.Concentric ? CenterOf(s, r[0]) : s.PRefs[r[0]];
                    var q = c.Type == CType.Concentric ? CenterOf(s, r[1]) : s.PRefs[r[1]];
                    AddEq(s, id, x => p.Get(x).X - q.Get(x).X, p, q);
                    AddEq(s, id, x => p.Get(x).Y - q.Get(x).Y, p, q);
                    break;
                }
            case CType.Symmetric:
                {
                    var p = s.PRefs[r[0]]; var q = s.PRefs[r[1]]; var (a, b) = Line(s, r[2]);
                    AddEq(s, id, x => { var d = b.Get(x) - a.Get(x); var m = (p.Get(x) + q.Get(x)) * 0.5; return Vec2.Cross(d, m - a.Get(x)) / Math.Max(d.Length, 1e-12); }, p, q, a, b);
                    AddEq(s, id, x => { var d = b.Get(x) - a.Get(x); return Vec2.Dot(d, q.Get(x) - p.Get(x)) / Math.Max(d.Length, 1e-12); }, p, q, a, b);
                    break;
                }
            case CType.Distance:
                {
                    var v = c.Value;
                    if (r.Length == 1)
                    {
                        var (a, b) = Line(s, r[0]);
                        AddEq(s, id, x => (b.Get(x) - a.Get(x)).Length - v, a, b);
                    }
                    else if (IsPoint(s, r[0]) && IsPoint(s, r[1]))
                    {
                        var a = s.PRefs[r[0]]; var b = s.PRefs[r[1]];
                        AddEq(s, id, x => (b.Get(x) - a.Get(x)).Length - v, a, b);
                    }
                    else if (IsLine(s, r[0]) && IsLine(s, r[1]))
                    {
                        // parallel lines distance: from first point of line 2 to line 1
                        var (a, b) = Line(s, r[0]); var (p, _) = Line(s, r[1]);
                        var sign = c.Sign;
                        AddEq(s, id, x => { var d = b.Get(x) - a.Get(x); return sign * Vec2.Cross(d, p.Get(x) - a.Get(x)) / Math.Max(d.Length, 1e-12) - v; }, p, a, b);
                    }
                    else
                    {
                        int pi = IsPoint(s, r[0]) ? 0 : 1;
                        var other = r[1 - pi];
                        var p = s.PRefs[r[pi]];
                        if (IsLine(s, other))
                        {
                            var (a, b) = Line(s, other);
                            var sign = c.Sign;
                            AddEq(s, id, x => { var d = b.Get(x) - a.Get(x); return sign * Vec2.Cross(d, p.Get(x) - a.Get(x)) / Math.Max(d.Length, 1e-12) - v; }, p, a, b);
                        }
                        else if (IsRound(s, other))
                        {
                            var cen = CenterOf(s, other);
                            AddEq(s, id, x => (p.Get(x) - cen.Get(x)).Length - v, p, cen);
                        }
                    }
                    break;
                }
            case CType.HDistance:
            case CType.VDistance:
                {
                    PRef a, b;
                    if (r.Length == 1) (a, b) = Line(s, r[0]); else { a = s.PRefs[r[0]]; b = s.PRefs[r[1]]; }
                    var v = c.Value; var sign = c.Sign;
                    if (c.Type == CType.HDistance) AddEq(s, id, x => sign * (b.Get(x).X - a.Get(x).X) - v, a, b);
                    else AddEq(s, id, x => sign * (b.Get(x).Y - a.Get(x).Y) - v, a, b);
                    break;
                }
            case CType.Radius:
            case CType.Diameter:
                {
                    var rad = s.RRefs[r[0]];
                    var v = c.Type == CType.Diameter ? c.Value / 2 : c.Value;
                    AddEqV(s, id, x => rad.Get(x) - v, Array.Empty<PRef>(), new[] { rad });
                    break;
                }
            case CType.Angle:
                {
                    var (a1, b1) = Line(s, r[0]); var (a2, b2) = Line(s, r[1]);
                    var target = c.Sign * c.Value * Math.PI / 180;
                    double f1 = (c.Flip & 1) != 0 ? -1 : 1, f2 = (c.Flip & 2) != 0 ? -1 : 1;
                    AddEq(s, id, x =>
                    {
                        var d1 = (b1.Get(x) - a1.Get(x)) * f1; var d2 = (b2.Get(x) - a2.Get(x)) * f2;
                        return Wrap(Math.Atan2(Vec2.Cross(d1, d2), Vec2.Dot(d1, d2)) - target);
                    }, a1, b1, a2, b2);
                    break;
                }
        }
    }

    /// <summary>Solves the sketch in place. Optional drag targets move the given points.</summary>
    public static SolveResult Solve(Sketch sk, IReadOnlyDictionary<int, Vec2>? drag = null, bool analyze = true)
    {
        // fixed points (the origin, points on the model) stay where they are, dragged with the rest or not: held at
        // the pointer they would move the sketch about them, while they themselves are never moved
        if (drag != null && drag.Keys.Any(id => sk.Point(id) is { Fixed: true }))
            drag = drag.Where(kv => sk.Point(kv.Key) is not { Fixed: true }).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (drag != null && drag.Count > 0)
        {
            // the shape before the drag: a step that turns it inside out is not taken (see SameShape). A step refused
            // so is tried shorter (half, a quarter, an eighth, a thirty-second of the way): the drag goes as far as the shape allows
            // instead of stopping short of the limit
            var s0 = Build(sk, null, false);
            var x0 = s0.X.ToArray();
            var from = drag.ToDictionary(kv => kv.Key, kv => sk.P(kv.Key));
            foreach (var f in new[] { 1.0, 0.5, 0.25, 0.125, 1.0 / 32 })
            {
                var to = f == 1 ? drag : drag.ToDictionary(kv => kv.Key, kv => from[kv.Key] + (kv.Value - from[kv.Key]) * f);
                if (TryDrag(sk, s0, x0, to)) break;
            }
            return analyze ? Analyze(sk) : new SolveResult { Converged = true };
        }

        var s = Build(sk, null, false);
        var x = s.X.ToArray();
        var xBefore = x.ToArray();
        var res = Iterate(s, x, 100);
        // a solution that flips a tangency (or collapses its line) is not this sketch: not solved, nothing moved
        bool ok = res < 1e-7 && SameShape(s, xBefore, s, x);
        if (ok) Apply(s, x, null);
        if (!analyze) return new SolveResult { Converged = ok, Residual = res, Status = ok ? SketchStatus.UnderDefined : SketchStatus.OverDefined };
        var an = Analyze(sk);
        return ok ? an : new SolveResult { Converged = false, Residual = res, Status = SketchStatus.OverDefined, FreePoints = an.FreePoints, FreeRadii = an.FreeRadii, Dof = an.Dof };
    }

    /// <summary>
    /// One drag step: the dragged points held at the pointer, else pulled toward it as far as the relations let them
    /// (everything else held lightly where it is), then the relations met exactly. Applied when the shape is kept.
    /// </summary>
    static bool TryDrag(Sketch sk, System s0, double[] x0, IReadOnlyDictionary<int, Vec2> drag)
    {
        // 1) dragged points fixed at the cursor
        var s1 = Build(sk, drag, includeDragAsFixed: true);
        var x1 = s1.X.ToArray();
        if (Iterate(s1, x1, 40) < 1e-7 && SameShape(s0, x0, s1, x1))
        {
            Apply(s1, x1, drag);
            return true;
        }
        // 2) soft pull toward the cursor, then project back onto the constraints
        var s2 = Build(sk, null, false);
        var x2 = s2.X.ToArray();
        var pulled = new HashSet<int>();
        foreach (var (pid, target) in drag)
        {
            if (!s2.PointVars.TryGetValue(pid, out var v)) continue;
            var (ix, iy) = v;
            pulled.Add(ix); pulled.Add(iy);
            s2.Eqs.Add(new Eq { Vars = new[] { ix }, F = x => (x[ix] - target.X) * 0.01 });
            s2.Eqs.Add(new Eq { Vars = new[] { iy }, F = x => (x[iy] - target.Y) * 0.01 });
        }
        // everything else held lightly where it is (far weaker than the pull): the pointer moves what it can, and
        // geometry the drag does not need to move stays put instead of drifting along its own freedom (a slot
        // shrinking while its side is dragged)
        for (int i = 0; i < x2.Length; i++)
        {
            if (pulled.Contains(i)) continue;
            int vi = i; var at = x2[i];
            s2.Eqs.Add(new Eq { Vars = new[] { vi }, F = x => (x[vi] - at) * 1e-4 });
        }
        Iterate(s2, x2, 30);
        var s3 = Build(sk, null, false);
        if (Iterate(s3, x2, 40) < 1e-7 && SameShape(s0, x0, s3, x2))
        {
            Apply(s3, x2, null);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a solve keeps the sketch's shape: each line tangent to an arc or circle still has it on the same side,
    /// and is not shrunk to nothing. A tangency stays as it was applied: the arc running on past the line, or turned
    /// back beside it (for an arc meeting the line at an end, which side its center is on decides which). The equation
    /// of a tangency at a shared end holds on either side, and on a line shrunk to nothing, so the solver can land
    /// there (a slot whose side collapses, its arcs meeting at a point); such a solution is refused instead, and the
    /// sketch stays as it was. Compared with the sketch before the solve, which was kept so in turn.
    /// </summary>
    static bool SameShape(System before, double[] xb, System after, double[] xa)
    {
        foreach (var c in before.Sketch.Constraints)
        {
            if (c.Type != CType.Tangent || c.Refs.Length < 2) continue;
            int li = IsLine(before, c.Refs[0]) ? 0 : IsLine(before, c.Refs[1]) ? 1 : -1;
            if (li < 0 || !IsRound(before, c.Refs[1 - li])) continue;
            (double side, double len) Of(System s, double[] x)
            {
                var (a, b) = Line(s, c.Refs[li]);
                var d = b.Get(x) - a.Get(x);
                return (Vec2.Cross(d, CenterOf(s, c.Refs[1 - li]).Get(x) - a.Get(x)), d.Length);
            }
            var (s0, l0) = Of(before, xb);
            var (s1, l1) = Of(after, xa);
            if (l0 > 1e-9 && l1 < Math.Max(1e-6, l0 * 1e-3)) return false;
            if (Math.Abs(s0) > 1e-12 * Math.Max(1, l0 * l0) && Math.Sign(s0) != Math.Sign(s1)) return false;
        }
        // nothing shrinks to nothing (equal sides of a polygon all at once, a circle's radius through zero), and no
        // arc turns into its complement (an end swung past the other: a fillet of 90° becoming 270°)
        // (shrinking by 1000 in one step, or below a millionth of the sketch's size: halving steps toward nothing are
        // stopped too, before a tangency's side can no longer be told)
        double size = 1;
        foreach (var p in before.PRefs.Values) size = Math.Max(size, p.Get(xb).Length);
        bool Collapsed(double was, double now) => was > 1e-9 && now < Math.Max(Math.Min(was, size * 1e-6), was * 1e-3);
        foreach (var e in before.Sketch.Entities)
            switch (e)
            {
                case SkLine l:
                    {
                        var (a, b) = Line(before, l.Id); var (a2, b2) = Line(after, l.Id);
                        if (Collapsed((b.Get(xb) - a.Get(xb)).Length, (b2.Get(xa) - a2.Get(xa)).Length)) return false;
                        break;
                    }
                case SkCircle:
                    if (before.RRefs.TryGetValue(e.Id, out var r0) && after.RRefs.TryGetValue(e.Id, out var r1) && Collapsed(r0.Get(xb), r1.Get(xa))) return false;
                    break;
                case SkArc arc:
                    {
                        if (Collapsed(before.RRefs[arc.Id].Get(xb), after.RRefs[arc.Id].Get(xa))) return false;
                        if (Math.Abs(Sweep(before, xb, arc) - Sweep(after, xa, arc)) > Math.PI) return false;
                        break;
                    }
            }
        return true;
    }

    /// <summary>An arc's sweep, counterclockwise from its start to its end: 0 to 2π.</summary>
    static double Sweep(System s, double[] x, SkArc a)
    {
        var c = s.PRefs[a.Center].Get(x); var st = s.PRefs[a.Start].Get(x) - c; var en = s.PRefs[a.End].Get(x) - c;
        var w = Math.Atan2(en.Y, en.X) - Math.Atan2(st.Y, st.X);
        while (w < 0) w += 2 * Math.PI;
        while (w >= 2 * Math.PI) w -= 2 * Math.PI;
        return w;
    }

    static void Apply(System s, double[] x, IReadOnlyDictionary<int, Vec2>? drag)
    {
        foreach (var p in s.Sketch.Points)
        {
            if (s.PointVars.TryGetValue(p.Id, out var v)) { p.X = x[v.ix]; p.Y = x[v.iy]; }
            else if (drag != null && drag.TryGetValue(p.Id, out var t) && !p.Fixed) { p.X = t.X; p.Y = t.Y; }
        }
        foreach (var e in s.Sketch.Entities)
            if (e is SkCircle c && s.RadiusVars.TryGetValue(c.Id, out var ir)) c.R = Math.Abs(x[ir]);
    }

    /// <summary>
    /// The equations in independent clusters (equations sharing a variable, and so on), each in ascending order:
    /// J Jᵀ is block diagonal over them, and a Cholesky factorization of it yields exactly zero across blocks, so each
    /// block is factored and solved on its own (O(Σ k³) instead of O(m³)) with the same results.
    /// </summary>
    static int[][] Clusters(System s, int n)
    {
        int m = s.Eqs.Count;
        var parent = new int[m];
        for (int i = 0; i < m; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        var owner = new int[n];
        Array.Fill(owner, -1);
        for (int i = 0; i < m; i++)
            foreach (var v in s.Eqs[i].Vars)
            {
                if (owner[v] < 0) owner[v] = i;
                else { int a = Find(owner[v]), b = Find(i); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
            }
        var byRoot = new Dictionary<int, List<int>>();
        var order = new List<List<int>>();
        for (int i = 0; i < m; i++)
        {
            int r = Find(i);
            if (!byRoot.TryGetValue(r, out var l)) { byRoot[r] = l = new List<int>(); order.Add(l); }
            l.Add(i);
        }
        return order.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>Runs damped Gauss-Newton; returns final max residual.</summary>
    static double Iterate(System s, double[] x, int maxIter)
    {
        int m = s.Eqs.Count, n = x.Length;
        if (m == 0 || n == 0) return m == 0 ? 0 : MaxAbs(Eval(s, x));
        var F = Eval(s, x);
        double norm = Norm2(F), maxAbs = MaxAbs(F);
        double mu = 1e-9;
        // each equation's derivatives at its own variables only (Eqs[i].Vars order): a dense row would be zero elsewhere
        var J = new double[m][];
        for (int i = 0; i < m; i++) J[i] = new double[s.Eqs[i].Vars.Length];
        var xNew = new double[n];
        var blocks = Clusters(s, n);
        // a cluster with fewer variables than equations (a drag's pull and hold terms on top of the relations) is solved
        // in its variables: (JᵀJ + c I) Δ = Jᵀ F, the same step as Jᵀ (J Jᵀ + c I)⁻¹ F for any c > 0, from a smaller system
        var bvars = blocks.Select(b => b.SelectMany(i => s.Eqs[i].Vars).Distinct().Order().ToArray()).ToArray();
        var inVars = blocks.Select((b, bi) => bvars[bi].Length < b.Length).ToArray();
        var local = new int[n];
        foreach (var vs in bvars) for (int k = 0; k < vs.Length; k++) local[vs[k]] = k;
        var Ab = blocks.Select((b, bi) => inVars[bi] ? new double[bvars[bi].Length, bvars[bi].Length] : new double[b.Length, b.Length]).ToArray();
        var diag = new double[m];
        for (int it = 0; it < maxIter && maxAbs > Tol; it++)
        {
            SparseJacobian(s, x, J);
            // A = J J^T + mu I   (m x m), block by block (zero between clusters)
            for (int i = 0; i < m; i++) { double d = 0; foreach (var g in J[i]) d += g * g; diag[i] = d; }
            for (int bi = 0; bi < blocks.Length; bi++)
            {
                var b = blocks[bi]; var A = Ab[bi];
                if (inVars[bi])
                {
                    // JᵀJ over the cluster's variables
                    Array.Clear(A);
                    foreach (var i in b)
                    {
                        var ri = J[i]; var vi = s.Eqs[i].Vars;
                        for (int k1 = 0; k1 < vi.Length; k1++)
                            for (int k2 = 0; k2 < vi.Length; k2++) A[local[vi[k1]], local[vi[k2]]] += ri[k1] * ri[k2];
                    }
                    continue;
                }
                for (int li = 0; li < b.Length; li++)
                    for (int lj = li; lj < b.Length; lj++)
                    {
                        int i = b[li], j = b[lj];
                        double sum = 0;
                        var ri = J[i]; var rj = J[j];
                        var vi = s.Eqs[i].Vars; var vj = s.Eqs[j].Vars;
                        // (a variable of i's that j does not have adds a zero: skipped)
                        for (int k = 0; k < vi.Length; k++)
                        {
                            int at = Array.IndexOf(vj, vi[k]);
                            if (at >= 0) sum += ri[k] * rj[at];
                        }
                        A[li, lj] = sum; A[lj, li] = sum;
                    }
            }
            bool improved = false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double trace = 0; for (int i = 0; i < m; i++) trace += diag[i];
                var lam = mu * Math.Max(1, trace / m);
                Array.Copy(x, xNew, n);
                bool solved = true;
                for (int bi = 0; bi < blocks.Length && solved; bi++)
                {
                    var b = blocks[bi];
                    if (inVars[bi])
                    {
                        var vs = bvars[bi];
                        var g = new double[vs.Length];
                        foreach (var i in b)
                        {
                            var ri = J[i]; var vi = s.Eqs[i].Vars;
                            for (int k = 0; k < vi.Length; k++) g[local[vi[k]]] += ri[k] * F[i];
                        }
                        var d = SolveSym(Ab[bi], g, vs.Length, lam + 1e-14);
                        if (d == null) { solved = false; break; }
                        for (int k = 0; k < vs.Length; k++) xNew[vs[k]] -= d[k];
                    }
                    else
                    {
                        var rhs = new double[b.Length];
                        for (int k = 0; k < b.Length; k++) rhs[k] = F[b[k]];
                        var y = SolveSym(Ab[bi], rhs, b.Length, lam + 1e-14);
                        if (y == null) { solved = false; break; }
                        for (int k = 0; k < b.Length; k++)
                        {
                            var yi = y[k]; if (yi == 0) continue;
                            var ri = J[b[k]]; var vi = s.Eqs[b[k]].Vars;
                            for (int q = 0; q < vi.Length; q++) xNew[vi[q]] -= ri[q] * yi;
                        }
                    }
                }
                if (!solved) { mu *= 10; continue; }
                var Fn = Eval(s, xNew);
                var nn = Norm2(Fn);
                if (nn < norm || MaxAbs(Fn) < Tol)
                {
                    Array.Copy(xNew, x, n);
                    F = Fn; norm = nn; maxAbs = MaxAbs(Fn);
                    mu = Math.Max(mu / 4, 1e-12);
                    improved = true;
                    break;
                }
                mu *= 8;
            }
            if (!improved) break;
        }
        return maxAbs;
    }

    static double[] Eval(System s, double[] x)
    {
        var F = new double[s.Eqs.Count];
        for (int i = 0; i < F.Length; i++) F[i] = s.Eqs[i].F(x) * s.Eqs[i].Weight;
        return F;
    }

    /// <summary><see cref="Jacobian"/>'s values at each equation's own variables (row i in Eqs[i].Vars order).</summary>
    static void SparseJacobian(System s, double[] x, double[][] J)
    {
        for (int i = 0; i < s.Eqs.Count; i++)
        {
            var row = J[i];
            var eq = s.Eqs[i];
            for (int k = 0; k < eq.Vars.Length; k++)
            {
                int v = eq.Vars[k];
                var old = x[v];
                var h = 1e-7 * Math.Max(1, Math.Abs(old));
                x[v] = old + h; var fp = eq.F(x);
                x[v] = old - h; var fm = eq.F(x);
                x[v] = old;
                row[k] = (fp - fm) / (2 * h) * eq.Weight;
            }
        }
    }

    static double Norm2(double[] F) { double s = 0; foreach (var f in F) s += f * f; return s; }
    static double MaxAbs(double[] F) { double s = 0; foreach (var f in F) s = Math.Max(s, Math.Abs(f)); return s; }

    /// <summary>Cholesky solve of a symmetric positive (semi)definite system, shifted by shift on its diagonal.</summary>
    static double[]? SolveSym(double[,] A, double[] b, int n, double shift = 0)
    {
        var L = new double[n, n];
        for (int j = 0; j < n; j++)
        {
            double sum = shift == 0 ? A[j, j] : A[j, j] + shift;
            for (int k = 0; k < j; k++) sum -= L[j, k] * L[j, k];
            if (sum <= 1e-300) return null;
            var ljj = Math.Sqrt(sum);
            L[j, j] = ljj;
            for (int i = j + 1; i < n; i++)
            {
                double s2 = A[i, j];
                for (int k = 0; k < j; k++) s2 -= L[i, k] * L[j, k];
                L[i, j] = s2 / ljj;
            }
        }
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = b[i];
            for (int k = 0; k < i; k++) s -= L[i, k] * y[k];
            y[i] = s / L[i, i];
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = y[i];
            for (int k = i + 1; k < n; k++) s -= L[k, i] * y[k];
            y[i] = s / L[i, i];
        }
        return y;
    }

    // ---- degrees of freedom analysis --------------------------------------------------------

    /// <summary>Determines which points / radii are still free (under-defined).</summary>
    public static SolveResult Analyze(Sketch sk)
    {
        var s = Build(sk, null, false);
        var x = s.X.ToArray();
        int m = s.Eqs.Count, n = x.Length;
        var (rank, free) = Reduce(s, x, wantFree: true);
        var fp = new HashSet<int>(); var fr = new HashSet<int>();
        foreach (var (pid, (ix, iy)) in s.PointVars) if (free![ix] || free[iy]) fp.Add(pid);
        foreach (var (cid, ir) in s.RadiusVars) if (free[ir]) fr.Add(cid);
        var resid = m > 0 ? MaxAbs(Eval(s, x)) : 0;
        var status = resid > 1e-6 ? SketchStatus.OverDefined
            : (fp.Count == 0 && fr.Count == 0) ? SketchStatus.FullyDefined : SketchStatus.UnderDefined;
        return new SolveResult { Converged = resid <= 1e-6, Residual = resid, Status = status, FreePoints = fp, FreeRadii = fr, Dof = n - rank };
    }

    /// <summary>
    /// Rank of the Jacobian at x and, when asked, which variables it leaves free (a variable is free when it is no
    /// pivot, or its pivot row reaches a variable that is none). Gauss-Jordan elimination with partial pivoting,
    /// column by column, giving what elimination of the whole dense matrix gives, bit for bit, but on each group of
    /// equations sharing variables alone: a column only has entries in its group's rows, and the rows keep the
    /// places the whole matrix would give them (ties between equal pivots go to the earlier place).
    /// </summary>
    static (int rank, bool[]? free) Reduce(System s, double[] x, bool wantFree)
    {
        int m = s.Eqs.Count, n = x.Length;
        // groups: the variables equations share, joined
        var parent = new int[n];
        for (int v = 0; v < n; v++) parent[v] = v;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        var used = new bool[n];
        foreach (var eq in s.Eqs)
            for (int k = 0; k < eq.Vars.Length; k++)
            {
                used[eq.Vars[k]] = true;
                int a = Find(eq.Vars[0]), b = Find(eq.Vars[k]);
                if (a != b) parent[b] = a;
            }
        var groupOf = new int[n]; Array.Fill(groupOf, -1);
        var groupRows = new List<List<int>>(); var groupCols = new List<int>();
        var local = new int[n]; // a variable's column in its group
        int Group(int v)
        {
            int r = Find(v);
            if (groupOf[r] < 0) { groupOf[r] = groupRows.Count; groupRows.Add(new List<int>()); groupCols.Add(0); }
            return groupOf[r];
        }
        var colGroup = new int[n]; Array.Fill(colGroup, -1);
        for (int v = 0; v < n; v++)
            if (used[v]) { int g = Group(v); colGroup[v] = g; local[v] = groupCols[g]++; }
        // each equation's row over its group's columns, as Jacobian gives it
        var rows = new double[m][];
        for (int i = 0; i < m; i++)
        {
            var eq = s.Eqs[i];
            if (eq.Vars.Length == 0) continue;
            int g = colGroup[eq.Vars[0]];
            groupRows[g].Add(i);
            var row = rows[i] = new double[groupCols[g]];
            foreach (var v in eq.Vars)
            {
                var old = x[v];
                var h = 1e-7 * Math.Max(1, Math.Abs(old));
                x[v] = old + h; var fp = eq.F(x);
                x[v] = old - h; var fm = eq.F(x);
                x[v] = old;
                row[local[v]] = (fp - fm) / (2 * h) * eq.Weight;
            }
        }
        // the elimination, with each row's place in the whole matrix
        var rowAt = new int[m]; var place = new int[m];
        for (int i = 0; i < m; i++) rowAt[i] = place[i] = i;
        var pivotRow = new int[n]; Array.Fill(pivotRow, -1);
        int rank = 0;
        for (int col = 0; col < n && rank < m; col++)
        {
            int g = colGroup[col];
            if (g < 0) continue;
            int lc = local[col];
            int best = -1; double bestV = 1e-9;
            foreach (var i in groupRows[g])
            {
                if (place[i] < rank) continue;
                var a = Math.Abs(rows[i][lc]);
                if (a > bestV || (a == bestV && best >= 0 && place[i] < place[best])) { bestV = a; best = i; }
            }
            if (best < 0) continue;
            int was = place[best], moved = rowAt[rank];
            rowAt[rank] = best; place[best] = rank; rowAt[was] = moved; place[moved] = was;
            var pr = rows[best];
            var pv = pr[lc];
            for (int j = 0; j < pr.Length; j++) pr[j] /= pv;
            foreach (var i in groupRows[g])
            {
                if (i == best) continue;
                var ri = rows[i];
                var f = ri[lc];
                if (f == 0) continue;
                for (int j = 0; j < ri.Length; j++) ri[j] -= f * pr[j];
            }
            pivotRow[col] = best;
            rank++;
        }
        if (!wantFree) return (rank, null);
        var free = new bool[n];
        var groupVars = new List<int>[groupRows.Count];
        for (int v = 0; v < n; v++) if (colGroup[v] >= 0) (groupVars[colGroup[v]] ??= new()).Add(v);
        for (int j = 0; j < n; j++)
        {
            if (pivotRow[j] < 0) { free[j] = true; continue; }
            var row = rows[pivotRow[j]];
            foreach (var f in groupVars[colGroup[j]])
                if (pivotRow[f] < 0 && Math.Abs(row[local[f]]) > 1e-7) { free[j] = true; break; }
        }
        return (rank, free);
    }

    /// <summary>True when adding the constraint would be redundant or conflicting (over-define).</summary>
    public static bool WouldOverDefine(Sketch sk, SkConstraint c)
    {
        var before = RankOf(sk);
        var tmp = sk.Clone();
        tmp.Constraints.Add(c.Clone());
        var s = Build(tmp, null, false);
        var after = RankOf(tmp);
        var addedEqs = s.Eqs.Count(e => e.ConstraintId == c.Id);
        return after - before < addedEqs;
    }

    static int RankOf(Sketch sk)
    {
        var s = Build(sk, null, false);
        int m = s.Eqs.Count, n = s.X.Count;
        if (m == 0 || n == 0) return 0;
        return Reduce(s, s.X.ToArray(), wantFree: false).rank;
    }

    // ---- measurement helpers used when creating dimensions -------------------------------------

    /// <summary>Directions of the two lines of an angle dimension, after applying its flips.</summary>
    public static (Vec2 d1, Vec2 d2) AngleRays(Sketch sk, SkConstraint c)
    {
        var l1 = (SkLine)sk.Entity(c.Refs[0])!; var l2 = (SkLine)sk.Entity(c.Refs[1])!;
        var d1 = sk.P(l1.P2) - sk.P(l1.P1); var d2 = sk.P(l2.P2) - sk.P(l2.P1);
        if ((c.Flip & 1) != 0) d1 = -d1;
        if ((c.Flip & 2) != 0) d2 = -d2;
        return (d1, d2);
    }

    /// <summary>Chooses the flips so the angle measured is the sector containing <paramref name="towards"/> (seen from the vertex).</summary>
    public static int ChooseAngleFlip(Vec2 d1, Vec2 d2, Vec2 towards)
    {
        var u = towards.Normalized;
        int best = 0; double bestScore = double.NegativeInfinity;
        for (int flip = 0; flip < 4; flip++)
        {
            var r1 = ((flip & 1) != 0 ? -d1 : d1).Normalized; var r2 = ((flip & 2) != 0 ? -d2 : d2).Normalized;
            // u lies inside the sector when it is on the same side of each ray as the other ray
            var inside = Math.Sign(Vec2.Cross(r1, u)) == Math.Sign(Vec2.Cross(r1, r2)) && Math.Sign(Vec2.Cross(r2, u)) == Math.Sign(Vec2.Cross(r2, r1));
            var score = (inside ? 10 : 0) + Vec2.Dot(r1, u) + Vec2.Dot(r2, u);
            if (score > bestScore) { bestScore = score; best = flip; }
        }
        return best;
    }

    public static double Measure(Sketch sk, SkConstraint c)
    {
        var r = c.Refs;
        Vec2 P(int id) => sk.P(id);
        (Vec2 a, Vec2 b) L(int id) { var l = (SkLine)sk.Entity(id)!; return (P(l.P1), P(l.P2)); }
        bool IsPt(int id) => sk.Point(id) != null;
        switch (c.Type)
        {
            case CType.Distance:
                if (r.Length == 1) { var (a, b) = L(r[0]); return (b - a).Length; }
                if (IsPt(r[0]) && IsPt(r[1])) return (P(r[1]) - P(r[0])).Length;
                if (sk.Entity(r[0]) is SkLine && sk.Entity(r[1]) is SkLine)
                {
                    var (a, b) = L(r[0]); var (p, _) = L(r[1]); var d = b - a;
                    return Math.Abs(Vec2.Cross(d, p - a)) / d.Length;
                }
                {
                    int pi = IsPt(r[0]) ? 0 : 1; var p = P(r[pi]); var o = sk.Entity(r[1 - pi]);
                    if (o is SkLine) { var (a, b) = L(o.Id); var d = b - a; return Math.Abs(Vec2.Cross(d, p - a)) / d.Length; }
                    if (o is SkCircle ci) return (p - P(ci.Center)).Length;
                    if (o is SkArc ar) return (p - P(ar.Center)).Length;
                    return 0;
                }
            case CType.HDistance:
            case CType.VDistance:
                {
                    Vec2 a, b;
                    if (r.Length == 1) (a, b) = L(r[0]); else { a = P(r[0]); b = P(r[1]); }
                    return Math.Abs(c.Type == CType.HDistance ? b.X - a.X : b.Y - a.Y);
                }
            case CType.Offset:
                {
                    // the first pair: a line's copy from its original's line, a curve's radius difference
                    if (r.Length < 2) return 0;
                    var o = sk.Entity(r[0]); var q = sk.Entity(r[1]);
                    if (o is SkLine && q is SkLine ql) { var (a, b) = L(r[0]); var d = b - a; return Math.Abs(Vec2.Cross(d, P(ql.P1) - a)) / d.Length; }
                    if (o != null && q != null) return Math.Abs(sk.Radius(q) - sk.Radius(o));
                    return 0;
                }
            case CType.Radius: return sk.Radius(sk.Entity(r[0])!);
            case CType.Diameter: return 2 * sk.Radius(sk.Entity(r[0])!);
            case CType.Angle:
                {
                    var (d1, d2) = AngleRays(sk, c);
                    return Math.Abs(Math.Atan2(Vec2.Cross(d1, d2), Vec2.Dot(d1, d2))) * 180 / Math.PI;
                }
        }
        return 0;
    }

    /// <summary>Captures the orientation sign for signed constraints from the current geometry.</summary>
    public static void CaptureSign(Sketch sk, SkConstraint c)
    {
        var r = c.Refs;
        Vec2 P(int id) => sk.P(id);
        (Vec2 a, Vec2 b) L(int id) { var l = (SkLine)sk.Entity(id)!; return (P(l.P1), P(l.P2)); }
        bool IsPt(int id) => sk.Point(id) != null;
        int Sg(double v) => v < 0 ? -1 : 1;
        switch (c.Type)
        {
            case CType.HDistance:
            case CType.VDistance:
                {
                    Vec2 a, b;
                    if (r.Length == 1) (a, b) = L(r[0]); else { a = P(r[0]); b = P(r[1]); }
                    c.Sign = Sg(c.Type == CType.HDistance ? b.X - a.X : b.Y - a.Y);
                    break;
                }
            case CType.Distance when r.Length == 2 && !(IsPt(r[0]) && IsPt(r[1])):
                {
                    if (sk.Entity(r[0]) is SkLine && sk.Entity(r[1]) is SkLine)
                    {
                        var (a, b) = L(r[0]); var (p, _) = L(r[1]);
                        c.Sign = Sg(Vec2.Cross(b - a, p - a));
                    }
                    else
                    {
                        int pi = IsPt(r[0]) ? 0 : 1; var o = sk.Entity(r[1 - pi]);
                        if (o is SkLine) { var (a, b) = L(o.Id); c.Sign = Sg(Vec2.Cross(b - a, P(r[pi]) - a)); }
                    }
                    break;
                }
            case CType.Angle:
                {
                    var (d1, d2) = AngleRays(sk, c);
                    c.Sign = Sg(Vec2.Cross(d1, d2));
                    break;
                }
            case CType.Tangent:
                {
                    int li = sk.Entity(r[0]) is SkLine ? 0 : sk.Entity(r[1]) is SkLine ? 1 : -1;
                    if (li >= 0)
                    {
                        var (a, b) = L(r[li]); var ce = sk.Entity(r[1 - li]);
                        var cen = ce is SkCircle cc ? P(cc.Center) : P(((SkArc)ce!).Center);
                        c.Sign = Sg(Vec2.Cross(b - a, cen - a));
                    }
                    else
                    {
                        var e1 = sk.Entity(r[0])!; var e2 = sk.Entity(r[1])!;
                        Vec2 C(SkEntity e) => e is SkCircle cc ? P(cc.Center) : P(((SkArc)e).Center);
                        var d = (C(e2) - C(e1)).Length; var r1 = sk.Radius(e1); var r2 = sk.Radius(e2);
                        c.Sign = Math.Abs(d - (r1 + r2)) <= Math.Abs(d - Math.Abs(r1 - r2)) ? 1 : -1;
                    }
                    break;
                }
        }
    }
}

using SpryCad.Kernel;

namespace SpryCad.Core.Sketching;

/// <summary>Sketch editing operations: fillet and mirror.</summary>
public static class SketchOps
{
    /// <summary>
    /// The two solid lines meeting at a point (a corner that can be filleted): construction lines and model
    /// references there do not count. Null, with why, otherwise.
    /// </summary>
    public static (SkLine a, SkLine b)? CornerAt(Sketch sk, long point, out string? why)
    {
        if (CurveCornerAt(sk, point, out why) is not var (a, b)) return null;
        if (a is SkLine la && b is SkLine lb) return (la, lb);
        why = "Sketch Fillet: click a corner where two lines meet.";
        return null;
    }

    /// <summary>The ends of a line or arc (an arc's start and end, not its centre).</summary>
    public static long[] EndsOf(SkEntity e) => e switch { SkLine l => new[] { l.P1, l.P2 }, SkArc a => new[] { a.Start, a.End }, _ => Array.Empty<long>() };

    /// <summary>
    /// The two solid lines or arcs ending at a point (a corner that can be filleted). Null, with why, otherwise.
    /// </summary>
    public static (SkEntity a, SkEntity b)? CurveCornerAt(Sketch sk, long point, out string? why)
    {
        why = null;
        var solid = sk.EntitiesUsing(point).Where(e => !e.Construction && !e.IsReference && e is not SkPointEntity).ToList();
        if (solid.Any(e => e is not (SkLine or SkArc) || !EndsOf(e).Contains(point)))
        { why = "Sketch Fillet: click a corner where two lines or arcs end."; return null; }
        if (solid.Count != 2) { why = "Sketch Fillet: click a corner where two lines or arcs meet."; return null; }
        return (solid[0], solid[1]);
    }

    /// <summary>
    /// Fillets the corner at <paramref name="corner"/> (two solid lines ending there) with radius <paramref name="r"/>.
    /// The lines stay, ending at the corner, as construction geometry: the virtual sharp. The profile follows a
    /// trimmed copy of each, kept on it, from its far end (or the tangent point of a fillet there already) to this
    /// fillet's tangent point, and a tangent arc with a radius dimension. Returns null, or why it cannot be filleted.
    /// </summary>
    public static string? Fillet(Sketch sk, long corner, double r)
    {
        if (CurveCornerAt(sk, corner, out var why0) is not var (ea, eb)) return why0;
        if (!(r > 0)) return "Sketch Fillet: the radius must be more than 0.";
        if (ea is not SkLine || eb is not SkLine) return FilletCurves(sk, corner, ea, eb, r);
        if (CornerAt(sk, corner, out var why) is not var (xa, xb)) return why;
        // a line trimmed by a fillet at its other end already stands for that fillet's original line
        SkLine OriginalOf(SkLine x) =>
            sk.Fillets?.FirstOrDefault(f => f.TrimA == x.Id) is { } fa ? (SkLine)sk.Entity(fa.LineA)!
            : sk.Fillets?.FirstOrDefault(f => f.TrimB == x.Id) is { } fb ? (SkLine)sk.Entity(fb.LineB)!
            : x;
        bool IsTrim(SkLine x) => sk.Fillets?.Any(f => f.TrimA == x.Id || f.TrimB == x.Id) == true;
        long FarEnd(SkLine l) => l.P1 == corner ? l.P2 : l.P1;
        var c = sk.P(corner);
        var pa = sk.P(FarEnd(xa)); var pb = sk.P(FarEnd(xb));
        var da = pa - c; var db = pb - c;
        double lenA = da.Length, lenB = db.Length;
        if (lenA < 1e-12 || lenB < 1e-12) return "Sketch Fillet: a line has no length.";
        da = da / lenA; db = db / lenB;
        var angle = Math.Acos(Math.Clamp(Vec2.Dot(da, db), -1, 1));
        if (angle < 1e-6 || Math.PI - angle < 1e-6) return "Sketch Fillet: the lines are in line; there is no corner to round.";
        // tangent points this far from the corner, the center on the bisector
        var t = r / Math.Tan(angle / 2);
        if (t >= lenA - 1e-9 || t >= lenB - 1e-9) return $"Sketch Fillet: a radius of {Expr.Format(r)} does not fit; the lines are too short.";
        var ta = c + da * t; var tb = c + db * t;
        var center = c + (da + db).Normalized * (r / Math.Sin(angle / 2));

        var oa = OriginalOf(xa); var ob = OriginalOf(xb);
        var pTa = sk.AddPoint(ta); var pTb = sk.AddPoint(tb); var pC = sk.AddPoint(center);
        // each side's solid line: the existing trimmed line, or a new copy of the line (which turns construction)
        SkLine Trim(SkLine x, SkPoint tangent)
        {
            var trim = x;
            if (!IsTrim(x))
            {
                trim = sk.Add(new SkLine { P1 = x.P1, P2 = x.P2 });
                x.Construction = true;
            }
            if (trim.P1 == corner) trim.P1 = tangent.Id; else trim.P2 = tangent.Id;
            return trim;
        }
        var trimA = Trim(xa, pTa); var trimB = Trim(xb, pTb);
        // counter-clockwise from start to end, the short way round
        var ccw = Vec2.Cross(ta - center, tb - center) > 0;
        var arc = sk.Add(new SkArc { Center = pC.Id, Start = ccw ? pTa.Id : pTb.Id, End = ccw ? pTb.Id : pTa.Id });
        // the tangent points stay on the original lines; the arc meets the trimmed lines tangentially
        sk.AddConstraint(CType.PointOnLine, pTa.Id, oa.Id);
        sk.AddConstraint(CType.PointOnLine, pTb.Id, ob.Id);
        foreach (var l in new[] { trimA, trimB })
        {
            var tg = sk.AddConstraint(CType.Tangent, l.Id, arc.Id);
            SketchSolver.CaptureSign(sk, tg);
        }
        var dim = sk.AddConstraint(CType.Radius, arc.Id);
        dim.Value = r; dim.Expr = Expr.Format(r);
        dim.Name = "D" + sk.Constraints.Count(x => x.IsDimension);
        (sk.Fillets ??= new()).Add(new SkFillet { Arc = arc.Id, LineA = oa.Id, LineB = ob.Id, TrimA = trimA.Id, TrimB = trimB.Id });
        return null;
    }

    /// <summary>
    /// A fillet where an arc meets a line or another arc: as for two lines (<see cref="Fillet"/>), the originals stay
    /// as construction and trimmed copies of them carry the profile. The fillet's centre is where the two curves'
    /// offsets by the radius cross (a line moved either way, a circle grown or shrunk): of those, the one whose
    /// tangent points lie on the two pieces next to the corner, nearest it.
    /// </summary>
    static string? FilletCurves(Sketch sk, long corner, SkEntity xa, SkEntity xb, double r)
    {
        // a piece trimmed by a fillet at its other end already stands for that fillet's original
        SkEntity OriginalOf(SkEntity x) =>
            sk.Fillets?.FirstOrDefault(f => f.TrimA == x.Id) is { } fa ? sk.Entity(fa.LineA)!
            : sk.Fillets?.FirstOrDefault(f => f.TrimB == x.Id) is { } fb ? sk.Entity(fb.LineB)!
            : x;
        bool IsTrim(SkEntity x) => sk.Fillets?.Any(f => f.TrimA == x.Id || f.TrimB == x.Id) == true;
        var c = sk.P(corner);
        // each curve's offsets: lines (a point and direction) or circles (centre and radius)
        var offs = new List<(bool line, Vec2 p, Vec2 d, double R)>[2];
        var ents = new[] { xa, xb };
        for (int k = 0; k < 2; k++)
        {
            offs[k] = new();
            switch (ents[k])
            {
                case SkLine l:
                    {
                        var far = sk.P(l.P1 == corner ? l.P2 : l.P1); var d = (far - c).Normalized;
                        foreach (var s in new[] { 1, -1 }) offs[k].Add((true, c + d.Perp * (s * r), d, 0));
                        break;
                    }
                case SkArc a:
                    {
                        var o = sk.P(a.Center); var R = sk.ArcRadius(a);
                        offs[k].Add((false, o, default, R + r));
                        if (R - r > 1e-9) offs[k].Add((false, o, default, R - r));
                        break;
                    }
            }
        }
        static List<Vec2> Meet((bool line, Vec2 p, Vec2 d, double R) a, (bool line, Vec2 p, Vec2 d, double R) b)
        {
            var res = new List<Vec2>();
            if (a.line && b.line)
            {
                var den = Vec2.Cross(a.d, b.d);
                if (Math.Abs(den) > 1e-12) res.Add(a.p + a.d * (Vec2.Cross(b.p - a.p, b.d) / den));
                return res;
            }
            if (!a.line && b.line) (a, b) = (b, a);
            if (a.line)
            {
                // line a against circle b
                var t0 = Vec2.Dot(b.p - a.p, a.d); var foot = a.p + a.d * t0; var h = Vec2.Distance(foot, b.p);
                if (h <= b.R) { var w = Math.Sqrt(b.R * b.R - h * h); res.Add(foot - a.d * w); res.Add(foot + a.d * w); }
                return res;
            }
            var dd = Vec2.Distance(a.p, b.p);
            if (dd < 1e-12 || dd > a.R + b.R || dd < Math.Abs(a.R - b.R)) return res;
            var x = (a.R * a.R - b.R * b.R + dd * dd) / (2 * dd); var y = Math.Sqrt(Math.Max(0, a.R * a.R - x * x));
            var u = (b.p - a.p) / dd;
            res.Add(a.p + u * x + u.Perp * y); res.Add(a.p + u * x - u.Perp * y);
            return res;
        }
        // where a centre touches a curve, and whether that is on the piece of it next to the corner
        (Vec2 at, bool ok) Touch(SkEntity e, Vec2 center)
        {
            switch (e)
            {
                case SkLine l:
                    {
                        var far = sk.P(l.P1 == corner ? l.P2 : l.P1); var d = far - c; var len = d.Length; d /= len;
                        var t = Vec2.Dot(center - c, d);
                        return (c + d * t, t > 1e-9 && t < len - 1e-9);
                    }
                case SkArc a:
                    {
                        var o = sk.P(a.Center); var R = sk.ArcRadius(a);
                        var q = center - o; if (q.Length < 1e-12) return (o, false);
                        var at = o + q.Normalized * R;
                        double a0 = (sk.P(a.Start) - o).Angle, a1 = (sk.P(a.End) - o).Angle, aq = q.Angle;
                        double Ccw(double from, double to) { var x = to - from; while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
                        var sweep = Ccw(a0, a1); var at0 = Ccw(a0, aq);
                        return (at, at0 > 1e-9 && at0 < sweep - 1e-9 && Vec2.Distance(at, c) > 1e-9);
                    }
            }
            return (default, false);
        }
        (Vec2 center, Vec2 ta, Vec2 tb)? best = null; double bestScore = double.MaxValue;
        foreach (var oa in offs[0])
            foreach (var ob in offs[1])
                foreach (var cen in Meet(oa, ob))
                {
                    var (ta, okA) = Touch(xa, cen); var (tb, okB) = Touch(xb, cen);
                    if (!okA || !okB) continue;
                    var score = Vec2.Distance(ta, c) + Vec2.Distance(tb, c);
                    if (score < bestScore) { bestScore = score; best = (cen, ta, tb); }
                }
        if (best is not var (center, pa, pb)) return $"Sketch Fillet: a radius of {Expr.Format(r)} does not fit at that corner.";
        var origA = OriginalOf(xa); var origB = OriginalOf(xb);
        var pTa = sk.AddPoint(pa); var pTb = sk.AddPoint(pb); var pC = sk.AddPoint(center);
        // each side's solid piece: the existing trimmed one, or a new copy (the original turns construction), its
        // end at the corner moved to the tangent point
        SkEntity Trim(SkEntity x, SkPoint tangent)
        {
            var trim = x;
            if (!IsTrim(x))
            {
                trim = x switch
                {
                    SkLine l => sk.Add(new SkLine { P1 = l.P1, P2 = l.P2 }),
                    SkArc a => sk.Add(new SkArc { Center = a.Center, Start = a.Start, End = a.End }),
                    _ => x,
                };
                x.Construction = true;
            }
            switch (trim)
            {
                case SkLine tl: if (tl.P1 == corner) tl.P1 = tangent.Id; else tl.P2 = tangent.Id; break;
                case SkArc ta2: if (ta2.Start == corner) ta2.Start = tangent.Id; else ta2.End = tangent.Id; break;
            }
            return trim;
        }
        var trimA = Trim(xa, pTa); var trimB = Trim(xb, pTb);
        var ccw = Vec2.Cross(pa - center, pb - center) > 0;
        var arc = sk.Add(new SkArc { Center = pC.Id, Start = ccw ? pTa.Id : pTb.Id, End = ccw ? pTb.Id : pTa.Id });
        // the tangent points stay on the originals; the fillet meets the trimmed pieces tangentially
        foreach (var (pt, orig) in new[] { (pTa, origA), (pTb, origB) })
            sk.AddConstraint(orig is SkLine ? CType.PointOnLine : CType.PointOnCircle, pt.Id, orig.Id);
        foreach (var piece in new[] { trimA, trimB })
        {
            var tg = sk.AddConstraint(CType.Tangent, piece.Id, arc.Id);
            SketchSolver.CaptureSign(sk, tg);
        }
        var dim = sk.AddConstraint(CType.Radius, arc.Id);
        dim.Value = r; dim.Expr = Expr.Format(r);
        dim.Name = "D" + sk.Constraints.Count(x => x.IsDimension);
        (sk.Fillets ??= new()).Add(new SkFillet { Arc = arc.Id, LineA = origA.Id, LineB = origB.Id, TrimA = trimA.Id, TrimB = trimB.Id });
        return null;
    }

    /// <summary>
    /// The chain through the given entities (lines and arcs end to end, as SolidWorks' "select chain"): grown along
    /// shared end points while exactly two chain entities meet there. Construction lines are left out.
    /// </summary>
    public static List<long> Chain(Sketch sk, IEnumerable<long> start)
    {
        var set = new HashSet<long>(start.Where(id => sk.Entity(id) is SkLine or SkArc or SkSpline { Closed: false }));
        bool Joinable(SkEntity e) => e is SkLine or SkArc or SkSpline { Closed: false } && !e.Construction;
        IEnumerable<long> Ends(SkEntity e) => e switch
        {
            SkLine l => new[] { l.P1, l.P2 }, SkArc a => new[] { a.Start, a.End },
            SkSpline { Closed: false } sp when sp.Points.Count >= 2 => new[] { sp.Points[0], sp.Points[^1] },
            _ => Array.Empty<long>(),
        };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var id in set.ToList())
                foreach (var p in Ends(sk.Entity(id)!))
                {
                    var there = sk.EntitiesUsing(p).Where(e => Joinable(e) && Ends(e).Contains(p)).ToList();
                    if (there.Count == 2)
                        foreach (var e in there) if (set.Add(e.Id)) grew = true;
                }
        }
        return set.Concat(start.Where(id => sk.Entity(id) is SkCircle or SkSpline { Closed: true })).Distinct().ToList();
    }

    /// <summary>
    /// Offsets entities (lines and arcs end to end, and circles) by <paramref name="d"/>, associatively: the copies
    /// are tied to the originals by one Offset relation whose value is the distance (a dimension), so they follow
    /// the originals. A closed chain goes outward, an open one to the left of its direction from its first entity;
    /// <paramref name="reverse"/> takes the other side. Returns the Offset constraint, or why it cannot be done.
    /// </summary>
    public static (SkConstraint? offset, string? error) Offset(Sketch sk, IEnumerable<long> entityIds, double d, bool reverse = false)
    {
        if (!(d > 0)) return (null, "Offset: the distance must be more than 0.");
        var ents = entityIds.Distinct().Select(sk.Entity).OfType<SkEntity>().Where(e => e is SkLine or SkArc or SkCircle or SkSpline).ToList();
        if (ents.Count == 0) return (null, "Offset: select lines, arcs, circles or splines.");
        var circles = ents.OfType<SkCircle>().ToList();
        // closed splines go on their own, as circles do
        var loops = ents.OfType<SkSpline>().Where(x => x.Closed).ToList();
        var curves = ents.Where(e => e is not SkCircle && e is not SkSpline { Closed: true }).ToList();
        IEnumerable<long> Ends(SkEntity e) => e switch
        {
            SkLine l => new[] { l.P1, l.P2 }, SkArc a => new[] { a.Start, a.End },
            SkSpline sp when sp.Points.Count >= 2 => new[] { sp.Points[0], sp.Points[^1] },
            _ => Array.Empty<long>(),
        };
        // a spline's direction (of its curve) at one of its points, by index
        Vec2 SplineDir(SkSpline sp, int i)
        {
            if (sk.SplineCurve(sp) is not { } c) return new Vec2(1, 0);
            c.Derivs(sk.SplineFitParams(sp)[i], out _, out var dv, out _);
            return new Vec2(dv.X, dv.Y).Normalized;
        }
        // order the chain: start at an end (a point only one of them uses), else anywhere (closed)
        var use = curves.SelectMany(Ends).GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        if (use.Values.Any(n => n > 2)) return (null, "Offset: the selection branches; select a single chain of lines and arcs.");
        var order = new List<(SkEntity e, bool forward)>();
        if (curves.Count > 0)
        {
            var closed = use.Values.All(n => n == 2);
            var startEnt = closed ? curves[0] : curves.First(e => Ends(e).Any(p => use[p] == 1));
            var entry = closed ? Ends(startEnt).First() : Ends(startEnt).First(p => use[p] == 1);
            var left = new HashSet<SkEntity>(curves);
            var cur = startEnt; var from = entry;
            while (cur != null)
            {
                left.Remove(cur);
                var ends = Ends(cur).ToArray();
                order.Add((cur, ends[0] == from));
                var to = ends[0] == from ? ends[1] : ends[0];
                cur = left.FirstOrDefault(e => Ends(e).Contains(to));
                from = to;
            }
            if (left.Count > 0) return (null, "Offset: the selection is not one connected chain; select a single chain of lines and arcs.");
        }

        // the side: left of the direction of travel; a closed chain outward (to the right when it runs counter-clockwise)
        int global = 1;
        if (curves.Count > 0 && use.Values.All(n => n == 2))
        {
            double area = 0;
            foreach (var (e, fwd) in order)
            {
                var pts = Samples(sk, e);
                if (!fwd) pts.Reverse();
                for (int i = 0; i + 1 < pts.Count; i++) area += Vec2.Cross(pts[i], pts[i + 1]);
            }
            global = area > 0 ? -1 : 1;
        }
        if (reverse) global = -global;
        // per entity: lines and splines by their own direction (left +), arcs and circles by radius (outward +)
        int SideOf(SkEntity e, bool fwd) => e switch
        {
            SkLine or SkSpline => fwd ? global : -global,
            SkArc => fwd ? -global : global, // left of travel along a counter-clockwise arc is toward its center
            _ => reverse ? -1 : 1,
        };
        // the left normal of travel at a point of an entity, for the first guess of the copies' positions
        Vec2 LeftNormal(SkEntity e, bool fwd, Vec2 p) => e switch
        {
            SkLine l => (sk.P(l.P2) - sk.P(l.P1)).Normalized.Perp * (fwd ? 1 : -1),
            SkArc a => (sk.P(a.Center) - p).Normalized * (fwd ? 1 : -1),
            SkSpline sp => SplineDir(sp, Vec2.Distance(sk.P(sp.Points[0]), p) < Vec2.Distance(sk.P(sp.Points[^1]), p) ? 0 : sp.Points.Count - 1).Perp * (fwd ? 1 : -1),
            _ => default,
        };
        // copy points: each original end point once, placed near where it will settle (the solver finishes it)
        var copyOf = new Dictionary<long, long>();
        long CopyPoint(long p)
        {
            if (copyOf.TryGetValue(p, out var q)) return q;
            var pos = sk.P(p);
            var normals = order.Where(x => Ends(x.e).Contains(p)).Select(x => LeftNormal(x.e, x.forward, pos) * global).ToList();
            Vec2 at;
            if (normals.Count == 2)
            {
                var dot = Vec2.Dot(normals[0], normals[1]);
                at = dot > -0.95 ? pos + (normals[0] + normals[1]) * (d / (1 + dot)) : pos + normals[0] * d;
            }
            else at = pos + normals[0] * d;
            return copyOf[p] = sk.AddPoint(at).Id;
        }
        var refs = new List<long>(); var sides = new List<int>();
        foreach (var (e, fwd) in order)
        {
            SkEntity copy = e switch
            {
                SkLine l => new SkLine { P1 = CopyPoint(l.P1), P2 = CopyPoint(l.P2) },
                SkArc a => new SkArc { Center = a.Center, Start = CopyPoint(a.Start), End = CopyPoint(a.End) },
                // a spline: its ends shared with the neighbours' copies, its inner points across from the original's
                SkSpline sp => new SkSpline
                {
                    Points = sp.Points.Select((p, i) => i == 0 || i == sp.Points.Count - 1 ? CopyPoint(p)
                        : sk.AddPoint(sk.P(p) + SplineDir(sp, i).Perp * (SideOf(e, fwd) * d)).Id).ToList(),
                },
                _ => null!,
            };
            sk.Add(copy);
            refs.Add(e.Id); refs.Add(copy.Id); sides.Add(SideOf(e, fwd));
        }
        foreach (var ci in circles)
        {
            var side = SideOf(ci, true);
            if (ci.R + side * d <= 0) return (null, $"Offset: a circle of radius {Expr.Format(ci.R)} cannot be offset {Expr.Format(d)} inward.");
            var copy = sk.Add(new SkCircle { Center = ci.Center, R = ci.R + side * d });
            refs.Add(ci.Id); refs.Add(copy.Id); sides.Add(side);
        }
        foreach (var sp in loops)
        {
            // outward: to the right of a counter-clockwise loop
            var poly = Polyline(sk, sp);
            double area = 0;
            for (int i = 0; i + 1 < poly.Count; i++) area += Vec2.Cross(poly[i], poly[i + 1]);
            var side = (area > 0 ? -1 : 1) * (reverse ? -1 : 1);
            var copy = sk.Add(new SkSpline { Closed = true, Points = sp.Points.Select((p, i) => sk.AddPoint(sk.P(p) + SplineDir(sp, i).Perp * (side * d)).Id).ToList() });
            refs.Add(sp.Id); refs.Add(copy.Id); sides.Add(side);
        }
        var c = new SkConstraint { Id = sk.NewId(), Type = CType.Offset, Refs = refs.ToArray(), Sides = sides.ToArray(), Value = d, Expr = Expr.Format(d) };
        c.Name = "D" + (sk.Constraints.Count(x => x.IsDimension) + 1);
        sk.Constraints.Add(c);
        return (c, null);
    }

    /// <summary>Reverses an offset: its copies go to the other side.</summary>
    public static void ReverseOffset(Sketch sk, SkConstraint offset)
    {
        if (offset.Sides == null) return;
        for (int i = 0; i < offset.Sides.Length; i++) offset.Sides[i] = -offset.Sides[i];
        // move the copies across (each copy point once, mirrored through its original), so the solver starts near
        // the answer
        var flip = new Dictionary<long, long>();
        for (int k = 0; k + 1 < offset.Refs.Length; k += 2)
        {
            var o = sk.Entity(offset.Refs[k]); var q = sk.Entity(offset.Refs[k + 1]);
            switch (o, q)
            {
                case (SkCircle oc, SkCircle qc): qc.R = Math.Max(1e-9, 2 * oc.R - qc.R); break;
                case (SkLine ol, SkLine ql): flip[ql.P1] = ol.P1; flip[ql.P2] = ol.P2; break;
                case (SkArc oa, SkArc qa): flip[qa.Start] = oa.Start; flip[qa.End] = oa.End; break;
                case (SkSpline os, SkSpline qs) when os.Points.Count == qs.Points.Count:
                    for (int i = 0; i < os.Points.Count; i++) flip[qs.Points[i]] = os.Points[i];
                    break;
            }
        }
        foreach (var (qp, op) in flip) if (sk.Point(qp) is { } pt) pt.P = sk.P(op) * 2 - pt.P;
    }

    /// <summary>Points along a line or arc, start to end (for the area of a closed chain).</summary>
    static List<Vec2> Samples(Sketch sk, SkEntity e)
    {
        if (e is SkLine l) return new() { sk.P(l.P1), sk.P(l.P2) };
        if (e is SkSpline) return Polyline(sk, e);
        if (e is SkArc a)
        {
            var c = sk.P(a.Center); var s = sk.P(a.Start) - c; var en = sk.P(a.End) - c;
            var a0 = Math.Atan2(s.Y, s.X); var a1 = Math.Atan2(en.Y, en.X);
            while (a1 <= a0) a1 += 2 * Math.PI;
            var r = s.Length;
            return Enumerable.Range(0, 17).Select(i => c + Vec2.FromAngle(a0 + (a1 - a0) * i / 16, r)).ToList();
        }
        return new();
    }

    /// <summary>
    /// Mirrors entities about a line: copies of lines, arcs, circles, splines and points on the other side, each copied
    /// point tied to its original by a symmetric relation (and circles by equal radii), so they stay mirrored.
    /// Points on the mirror line are shared rather than copied (kept on it), so a half profile closes. Returns
    /// the new entities' ids.
    /// </summary>
    public static List<long> Mirror(Sketch sk, IEnumerable<long> entityIds, long axisLine)
    {
        var made = new List<long>();
        if (sk.Entity(axisLine) is not SkLine axis) return made;
        var a = sk.P(axis.P1); var b = sk.P(axis.P2);
        var d = b - a;
        if (d.Length < 1e-12) return made;
        var u = d / d.Length;
        Vec2 Reflect(Vec2 p) { var v = p - a; var along = u * Vec2.Dot(v, u); return a + along * 2 - v; }
        var scale = Math.Max(d.Length, 1);
        var map = new Dictionary<long, long>();
        long MapPoint(long id)
        {
            if (map.TryGetValue(id, out var m)) return m;
            var p = sk.P(id);
            var off = Math.Abs(Vec2.Cross(u, p - a));
            if (off < 1e-7 * scale)
            {
                // on the mirror line: shared, and kept on it (unless it is already, or that would over define)
                if (id != axis.P1 && id != axis.P2 && !sk.Constraints.Any(c => c.Type is CType.PointOnLine or CType.Midpoint && c.Refs[0] == id && c.Refs[1] == axisLine))
                {
                    var on = new SkConstraint { Id = sk.NewId(), Type = CType.PointOnLine, Refs = new[] { id, axisLine } };
                    if (!SketchSolver.WouldOverDefine(sk, on)) sk.Constraints.Add(on);
                }
                return map[id] = id;
            }
            var q = sk.AddPoint(Reflect(p));
            sk.AddConstraint(CType.Symmetric, id, q.Id, axisLine);
            return map[id] = q.Id;
        }
        foreach (var id in entityIds.Distinct())
        {
            if (id == axisLine || sk.Entity(id) is not { IsReference: false } e) continue;
            SkEntity copy = e switch
            {
                SkLine l => new SkLine { P1 = MapPoint(l.P1), P2 = MapPoint(l.P2) },
                // a reflection turns the direction round: counter-clockwise again with the ends swapped
                SkArc ar => new SkArc { Center = MapPoint(ar.Center), Start = MapPoint(ar.End), End = MapPoint(ar.Start) },
                SkCircle ci => new SkCircle { Center = MapPoint(ci.Center), R = ci.R },
                SkPointEntity pe => new SkPointEntity { P = MapPoint(pe.P) },
                // (its handles mirrored too: a direction reflects as a point about the line through the origin)
                SkSpline sp => new SkSpline
                {
                    Points = sp.Points.Select(MapPoint).ToList(), Closed = sp.Closed, Params = sp.Params == null ? null : new List<double>(sp.Params),
                    StartDir = sp.StartDir is { } sd ? u * (2 * Vec2.Dot(u, sd)) - sd : null, EndDir = sp.EndDir is { } ed ? u * (2 * Vec2.Dot(u, ed)) - ed : null,
                },
                _ => null!,
            };
            if (copy == null) continue;
            copy.Construction = e.Construction;
            sk.Add(copy);
            // a circle's center does not set its size: equal radii keep the copy the same
            if (copy is SkCircle) sk.AddConstraint(CType.Equal, e.Id, copy.Id);
            made.Add(copy.Id);
        }
        return made;
    }

    /// <summary>An entity as a polyline (lines, arcs, circles, splines), for crossings.</summary>
    public static List<Vec2> Polyline(Sketch sk, SkEntity e)
    {
        switch (e)
        {
            case SkLine l: return new() { sk.P(l.P1), sk.P(l.P2) };
            case SkCircle c: return Enumerable.Range(0, 97).Select(i => sk.P(c.Center) + Vec2.FromAngle(2 * Math.PI * i / 96, c.R)).ToList();
            case SkArc a:
                {
                    var c = sk.P(a.Center); var s = sk.P(a.Start) - c; var en = sk.P(a.End) - c;
                    double a0 = s.Angle, a1 = en.Angle;
                    while (a1 <= a0) a1 += 2 * Math.PI;
                    return Enumerable.Range(0, 65).Select(i => c + Vec2.FromAngle(a0 + (a1 - a0) * i / 64, s.Length)).ToList();
                }
            case SkSpline sp when sk.SplineCurve(sp) is { } curve:
                return Enumerable.Range(0, 401).Select(i => { var q = curve.Eval(i / 400.0); return new Vec2(q.X, q.Y); }).ToList();
        }
        return new();
    }

    /// <summary>
    /// Trims a spline: the piece around <paramref name="at"/> between its crossings with other (non-construction)
    /// entities, or its ends, goes. What is left is a spline (two, cut in the middle; a closed one opens) through the
    /// fit points it keeps and a new point at each cut, tangent relations at its kept ends kept. The new points and
    /// the entity each was cut at (to tie them there), or null with why not.
    /// </summary>
    public static (List<(long point, long by)> cuts, string? error) TrimSpline(Sketch sk, SkSpline s, Vec2 at)
    {
        var none = new List<(long, long)>();
        if (sk.SplineCurve(s) is not { } curve) return (none, "the spline has no curve");
        const int N = 1200;
        var P = Enumerable.Range(0, N + 1).Select(i => { var q = curve.Eval((double)i / N); return new Vec2(q.X, q.Y); }).ToArray();
        double Param(Vec2 q)
        {
            double best = double.MaxValue, bt = 0;
            for (int i = 0; i < N; i++)
            {
                var d = P[i + 1] - P[i]; var l2 = d.LengthSq;
                var f = l2 > 0 ? Math.Clamp(Vec2.Dot(q - P[i], d) / l2, 0, 1) : 0;
                var dist = Vec2.Distance(P[i] + d * f, q);
                if (dist < best) { best = dist; bt = (i + f) / N; }
            }
            return bt;
        }
        var tm = Param(at);
        // crossings with the other entities (and where they are)
        var cuts = new List<(double t, long by, Vec2 p)>();
        foreach (var e in sk.Entities)
        {
            if (e.Id == s.Id || e.Construction) continue;
            var poly = Polyline(sk, e);
            for (int k = 0; k + 1 < poly.Count; k++)
            {
                Vec2 c = poly[k], d = poly[k + 1];
                for (int i = 0; i < N; i++)
                {
                    Vec2 a = P[i], b = P[i + 1], dir = b - a;
                    var den = Vec2.Cross(dir, d - c);
                    if (Math.Abs(den) < 1e-15) continue;
                    var u = Vec2.Cross(c - a, d - c) / den; var v = Vec2.Cross(c - a, dir) / den;
                    if (u < 0 || u > 1 || v < -1e-9 || v > 1 + 1e-9) continue;
                    var t = (i + u) / N;
                    if (!s.Closed && (t < 1e-4 || t > 1 - 1e-4)) continue; // at the spline's own ends
                    cuts.Add((t, e.Id, a + dir * u));
                }
            }
        }
        cuts.Sort((x, y) => x.t.CompareTo(y.t));
        for (int i = cuts.Count - 1; i > 0; i--) if (cuts[i].t - cuts[i - 1].t < 1e-5) cuts.RemoveAt(i);
        // the fit points' parameters
        var fp = s.Points.Select(id => Param(sk.P(id))).ToList();
        fp[0] = 0;
        if (!s.Closed) fp[^1] = 1;
        var tangents = sk.Constraints.Where(c => sk.SplineTangent(c) is { } st && ReferenceEquals(st.spline, s)).Select(c => (c, sk.SplineTangent(c)!.Value.point)).ToList();
        var made = new List<SkSpline>();
        var res = new List<(long, long)>();
        long NewPoint((double t, long by, Vec2 p) cut) { var np = sk.AddPoint(cut.p); res.Add((np.Id, cut.by)); return np.Id; }
        // a piece keeps the old curve's directions at its ends (the cuts, and the old ends where nothing else sets them)
        // (the derivative per the piece's own parameter: the old one's times the share of it the piece spans)
        Vec2 Dir(double t, double span) { curve.Derivs(Math.Clamp(t, 0, 1), out _, out var d, out _); return new Vec2(d.X, d.Y) * span; }
        SkSpline? Piece(List<long> pts, double t0, double t1)
        {
            if (pts.Count < 2) return null;
            var span = t1 >= t0 ? t1 - t0 : t1 + 1 - t0;
            // the points' parameters on the old curve, as the piece's own (the same curve where they are)
            var prm = pts.Select((id, i) => i == 0 ? 0.0 : i == pts.Count - 1 ? 1.0 : ((Param(sk.P(id)) - t0 + 1) % 1) / span).ToList();
            var sp = sk.Add(new SkSpline { Points = pts, Construction = s.Construction, StartDir = Dir(t0, span), EndDir = Dir(t1, span), Params = prm });
            made.Add(sp);
            return sp;
        }
        if (s.Closed)
        {
            if (cuts.Count < 2) return (none, "a closed spline is trimmed between two crossings");
            // the piece round the click goes: from the crossing after it round to the one before
            var lo = cuts.LastOrDefault(x => x.t <= tm); if (lo.by == 0) lo = cuts[^1];
            var hi = cuts.FirstOrDefault(x => x.t > tm); if (hi.by == 0) hi = cuts[0];
            var pts = new List<long> { NewPoint(hi) };
            // fit points from hi on round to lo
            var order = Enumerable.Range(0, s.Points.Count).OrderBy(i => (fp[i] - hi.t + 1) % 1).Where(i => (fp[i] - hi.t + 1) % 1 < (lo.t - hi.t + 1) % 1 && (fp[i] - hi.t + 1) % 1 > 1e-6).ToList();
            pts.AddRange(order.Select(i => s.Points[i]));
            pts.Add(NewPoint(lo));
            Piece(pts, hi.t, lo.t);
        }
        else
        {
            (double t, long by, Vec2 p) lo = (0, -1, default), hi = (1, -1, default);
            foreach (var x in cuts) { if (x.t <= tm) lo = x; else { hi = x; break; } }
            if (lo.by < 0 && hi.by < 0) { sk.DeleteEntity(s.Id); return (res, null); }
            if (lo.by >= 0)
            {
                var pts = Enumerable.Range(0, s.Points.Count).Where(i => fp[i] < lo.t - 1e-6).Select(i => s.Points[i]).ToList();
                pts.Add(NewPoint(lo));
                Piece(pts, 0, lo.t);
            }
            if (hi.by >= 0)
            {
                var pts = new List<long> { NewPoint(hi) };
                pts.AddRange(Enumerable.Range(0, s.Points.Count).Where(i => fp[i] > hi.t + 1e-6).Select(i => s.Points[i]));
                Piece(pts, hi.t, 1);
            }
        }
        // tangent relations at the ends kept: on the piece that has the end
        foreach (var (c, pt) in tangents)
        {
            var piece = made.FirstOrDefault(m => m.Points[0] == pt || m.Points[^1] == pt);
            if (piece == null) continue;
            var other = c.Refs[0] == s.Id ? c.Refs[1] : c.Refs[0];
            sk.Constraints.Add(new SkConstraint { Id = sk.NewId(), Type = CType.Tangent, Refs = new[] { other, piece.Id } });
        }
        sk.DeleteEntity(s.Id);
        return (res, null);
    }
}

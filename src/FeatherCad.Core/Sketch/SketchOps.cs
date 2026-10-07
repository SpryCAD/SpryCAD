using FeatherCad.Kernel;

namespace FeatherCad.Core.Sketching;

/// <summary>Sketch editing operations: fillet and mirror.</summary>
public static class SketchOps
{
    /// <summary>
    /// The two solid lines meeting at a point (a corner that can be filleted): construction lines and model
    /// references there do not count. Null, with why, otherwise.
    /// </summary>
    public static (SkLine a, SkLine b)? CornerAt(Sketch sk, int point, out string? why)
    {
        why = null;
        var solid = sk.EntitiesUsing(point).Where(e => !e.Construction && !e.IsReference && e is not SkPointEntity).ToList();
        if (solid.Any(e => e is not SkLine)) { why = "Sketch Fillet: rounding a line to an arc is not supported yet; click a corner between two lines."; return null; }
        if (solid.Count != 2) { why = "Sketch Fillet: click a corner where two lines meet."; return null; }
        return ((SkLine)solid[0], (SkLine)solid[1]);
    }

    /// <summary>
    /// Fillets the corner at <paramref name="corner"/> (two solid lines ending there) with radius <paramref name="r"/>.
    /// The lines stay, ending at the corner, as construction geometry: the virtual sharp. The profile follows a
    /// trimmed copy of each, kept on it, from its far end (or the tangent point of a fillet there already) to this
    /// fillet's tangent point, and a tangent arc with a radius dimension. Returns null, or why it cannot be filleted.
    /// </summary>
    public static string? Fillet(Sketch sk, int corner, double r)
    {
        if (CornerAt(sk, corner, out var why) is not var (xa, xb)) return why;
        if (!(r > 0)) return "Sketch Fillet: the radius must be more than 0.";
        // a line trimmed by a fillet at its other end already stands for that fillet's original line
        SkLine OriginalOf(SkLine x) =>
            sk.Fillets?.FirstOrDefault(f => f.TrimA == x.Id) is { } fa ? (SkLine)sk.Entity(fa.LineA)!
            : sk.Fillets?.FirstOrDefault(f => f.TrimB == x.Id) is { } fb ? (SkLine)sk.Entity(fb.LineB)!
            : x;
        bool IsTrim(SkLine x) => sk.Fillets?.Any(f => f.TrimA == x.Id || f.TrimB == x.Id) == true;
        int FarEnd(SkLine l) => l.P1 == corner ? l.P2 : l.P1;
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
    /// Mirrors entities about a line: copies of lines, arcs, circles and points on the other side, each copied
    /// point tied to its original by a symmetric relation (and circles by equal radii), so they stay mirrored.
    /// Points on the mirror line are shared rather than copied (kept on it), so a half profile closes. Returns
    /// the new entities' ids.
    /// </summary>
    public static List<int> Mirror(Sketch sk, IEnumerable<int> entityIds, int axisLine)
    {
        var made = new List<int>();
        if (sk.Entity(axisLine) is not SkLine axis) return made;
        var a = sk.P(axis.P1); var b = sk.P(axis.P2);
        var d = b - a;
        if (d.Length < 1e-12) return made;
        var u = d / d.Length;
        Vec2 Reflect(Vec2 p) { var v = p - a; var along = u * Vec2.Dot(v, u); return a + along * 2 - v; }
        var scale = Math.Max(d.Length, 1);
        var map = new Dictionary<int, int>();
        int MapPoint(int id)
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
}

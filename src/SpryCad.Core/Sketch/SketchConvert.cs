using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Core.Sketching;

/// <summary>
/// Convert Entities: model edges projected into a sketch. Exact where the sketch has the curve: lines as lines,
/// circles and arcs facing the sketch as circles and arcs. Any other curve (a spline, a tilted circle) as arcs and
/// lines fitted to it, within <see cref="Tolerance"/>. Edges joined end to end share their points. Converted
/// geometry stays on its edge (<see cref="SkConvertLink"/>): every rebuild places it again where the edge now is
/// (<see cref="Update"/>), so the sketch follows the model.
/// </summary>
public static class SketchConvert
{
    /// <summary>How far a fitted arc or line may stray from a curve with no exact sketch equivalent.</summary>
    public const double Tolerance = 1e-3;

    public enum PieceKind { Line, Arc, Circle }

    /// <summary>
    /// A piece of an edge as seen in the sketch. Line: A to B. Arc: about C, counter-clockwise from A to B.
    /// Circle: about C, radius R.
    /// </summary>
    public readonly record struct Piece(PieceKind Kind, Vec2 A, Vec2 B, Vec2 C, double R);

    /// <summary>An edge as sketch pieces (none when it is seen end on).</summary>
    public static List<Piece> Pieces(Frame frame, NEdge e)
    {
        Vec2 L(Vec3 p) => frame.ToLocal(p);
        var (a, b) = (L(e.V0.P), L(e.V1.P));
        var res = new List<Piece>();
        switch (e.Curve)
        {
            case NLine:
                if (Vec2.Distance(a, b) >= 1e-9) res.Add(new Piece(PieceKind.Line, a, b, default, 0));
                return res;
            case NCircle c when Math.Abs(Vec3.Dot(c.Axis.Normalized, frame.Normal)) > 1 - 1e-9:
                res.Add(e.Closed ? new Piece(PieceKind.Circle, default, default, L(c.C), c.R) : ArcPiece(L(c.C), a, b, L(e.Mid)));
                return res;
        }
        // anything else, as it looks from the sketch
        var pts = e.Samples(Tolerance / 4).Select(L).ToList();
        if (pts.Count < 2) return res;
        pts[0] = a; pts[^1] = b;
        Fit(pts, 0, pts.Count - 1, res);
        return res;
    }

    /// <summary>
    /// Converts one edge; false when it adds nothing (seen end on, or there already). With the body it is on (and an
    /// edge between two named faces) the geometry is linked to it; otherwise it is fixed where the edge is now.
    /// </summary>
    public static bool Edge(Sketch sk, Frame frame, NEdge e, string? bodyId = null)
    {
        var pieces = Pieces(frame, e);
        var link = bodyId != null && e.Uses.Count == 2
            ? new EdgeRef { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Body = bodyId, Hint = e.Mid }
            : null;
        bool any = false;
        for (int i = 0; i < pieces.Count; i++)
            any |= Add(sk, pieces[i], link == null ? null : new SkConvertLink { Edge = link, Piece = i, Of = pieces.Count });
        return any;
    }

    /// <summary>A display polyline (no exact edge known) as lines, fixed.</summary>
    public static bool Polyline(Sketch sk, IReadOnlyList<Vec2> pts)
    {
        bool any = false;
        for (int i = 0; i + 1 < pts.Count; i++)
            if (Vec2.Distance(pts[i], pts[i + 1]) >= 1e-9) any |= Add(sk, new Piece(PieceKind.Line, pts[i], pts[i + 1], default, 0), null);
        return any;
    }

    /// <summary>
    /// Places converted geometry where its edges are now (a rebuild). An edge now seen as other pieces (a fitted
    /// curve that changed) gets its geometry made again; one no longer in the model leaves it where it was, with a warning.
    /// </summary>
    public static void Update(Sketch sk, Frame frame, ModelState state, List<string> warnings)
    {
        var groups = sk.Entities.Where(x => x.Link != null).GroupBy(x => x.Link!.Key).ToList();
        if (groups.Count == 0) return;
        var redo = new List<(List<SkEntity> old, List<Piece> pieces, EdgeRef edge)>();
        // the bodies' edges by the faces they join, gathered once (a sketch may hold hundreds of converted edges:
        // looked up one by one, each would scan every edge of the model)
        var index = new Dictionary<(string? body, string a, string b), List<NEdge>>();
        var names = groups.SelectMany(g => new[] { g.First().Link!.Edge.A, g.First().Link!.Edge.B }).ToHashSet();
        foreach (var body in state.Bodies)
        {
            if (body.Solid.Native is not { } nb) continue;
            nb.Link();
            var seen = new HashSet<NEdge>();
            foreach (var e in nb.Faces.Where(f => names.Contains(f.Tag.Name)).SelectMany(f => f.Coedges).Select(c => c.Edge))
            {
                if (e.Uses.Count != 2 || !seen.Add(e)) continue;
                var (fa, fb) = (e.Uses[0].Face.Tag.Name, e.Uses[1].Face.Tag.Name);
                var key = (body.Id, string.CompareOrdinal(fa, fb) <= 0 ? fa : fb, string.CompareOrdinal(fa, fb) <= 0 ? fb : fa);
                (index.TryGetValue(key, out var l) ? l : index[key] = new()).Add(e);
            }
        }
        NEdge? Find(EdgeRef r)
        {
            var key = (r.Body, string.CompareOrdinal(r.A, r.B) <= 0 ? r.A : r.B, string.CompareOrdinal(r.A, r.B) <= 0 ? r.B : r.A);
            if (index.TryGetValue(key, out var l)) return l.Count == 1 ? l[0] : l.MinBy(e => Vec3.Distance(e.Mid, r.Hint));
            // not between those faces any more (renamed, another body): the resolver's wider search
            try { return Resolver.ResolveNativeEdge(r, state)?.edge; } catch (FeatureException) { return null; }
        }
        foreach (var g in groups)
        {
            var link = g.First().Link!;
            var ne = Find(link.Edge);
            if (ne == null) { warnings.Add("Converted sketch geometry: its model edge was not found (it stays where it was)."); continue; }
            var pieces = Pieces(frame, ne);
            var ents = g.ToList();
            if (pieces.Count == link.Of && ents.All(x => x.Link!.Piece < pieces.Count && KindOf(x) == pieces[x.Link.Piece].Kind))
                foreach (var x in ents) Place(sk, x, pieces[x.Link!.Piece]);
            else redo.Add((ents, pieces, link.Edge));
        }
        // made again after the others are placed: their ends are found where the neighbours now are
        foreach (var (old, pieces, edge) in redo)
        {
            foreach (var x in old) sk.DeleteEntity(x.Id);
            for (int i = 0; i < pieces.Count; i++) Add(sk, pieces[i], new SkConvertLink { Edge = edge, Piece = i, Of = pieces.Count });
        }
    }

    static PieceKind? KindOf(SkEntity e) => e switch
    {
        SkLine => PieceKind.Line,
        SkArc => PieceKind.Arc,
        SkCircle => PieceKind.Circle,
        _ => null,
    };

    static void Place(Sketch sk, SkEntity x, Piece p)
    {
        switch (x)
        {
            case SkLine l: Set(sk, l.P1, p.A); Set(sk, l.P2, p.B); break;
            case SkArc a: Set(sk, a.Center, p.C); Set(sk, a.Start, p.A); Set(sk, a.End, p.B); break;
            case SkCircle c: Set(sk, c.Center, p.C); c.R = p.R; break;
        }
    }

    static void Set(Sketch sk, int id, Vec2 p)
    {
        if (id != Sketch.OriginId && sk.Point(id) is { } pt) { pt.X = p.X; pt.Y = p.Y; pt.Fixed = true; }
    }

    /// <summary>Adds a piece (its points shared with what is there); false when the same geometry is there already.</summary>
    static bool Add(Sketch sk, Piece p, SkConvertLink? link)
    {
        switch (p.Kind)
        {
            case PieceKind.Line:
                {
                    int pa = Point(sk, p.A), pb = Point(sk, p.B);
                    if (sk.Entities.OfType<SkLine>().Any(l => (l.P1 == pa && l.P2 == pb) || (l.P1 == pb && l.P2 == pa))) return false;
                    sk.Add(new SkLine { P1 = pa, P2 = pb, Link = link });
                    return true;
                }
            case PieceKind.Arc:
                {
                    int c = Point(sk, p.C), s = Point(sk, p.A), e = Point(sk, p.B);
                    if (sk.Entities.OfType<SkArc>().Any(x => x.Center == c && x.Start == s && x.End == e)) return false;
                    sk.Add(new SkArc { Center = c, Start = s, End = e, Link = link });
                    return true;
                }
            default:
                {
                    int c = Point(sk, p.C);
                    if (sk.Entities.OfType<SkCircle>().Any(o => o.Center == c && Math.Abs(o.R - p.R) < 1e-9)) return false;
                    var ci = sk.Add(new SkCircle { Center = c, R = p.R, Link = link });
                    // not linked: its radius held by a dimension (a linked one takes it from the model)
                    if (link == null)
                    {
                        var rc = sk.AddConstraint(CType.Radius, ci.Id); rc.Value = p.R; rc.Expr = Expr.Format(p.R);
                        rc.LabelPlaced = false; // made, not placed: laid out
                    }
                    return true;
                }
        }
    }

    /// <summary>Points pts[i0..i1] as one line, one arc, or split in two and each fitted again.</summary>
    static void Fit(List<Vec2> pts, int i0, int i1, List<Piece> res)
    {
        var (a, b) = (pts[i0], pts[i1]);
        if (i1 - i0 < 2) { if (Vec2.Distance(a, b) >= 1e-9) res.Add(new Piece(PieceKind.Line, a, b, default, 0)); return; }
        var closedSpan = Vec2.Distance(a, b) < 1e-9;
        bool Within(Func<Vec2, double> dev) { for (int k = i0 + 1; k < i1; k++) if (dev(pts[k]) > Tolerance) return false; return true; }
        if (!closedSpan && Within(p => SegmentDistance(p, a, b))) { res.Add(new Piece(PieceKind.Line, a, b, default, 0)); return; }
        var im = (i0 + i1) / 2;
        if (!closedSpan && Circumcenter(a, pts[im], b) is { } cc)
        {
            var r = Vec2.Distance(cc, a);
            if (r < 1e6 && Within(p => Math.Abs(Vec2.Distance(p, cc) - r))) { res.Add(ArcPiece(cc, a, b, pts[im])); return; }
        }
        Fit(pts, i0, im, res);
        Fit(pts, im, i1, res);
    }

    /// <summary>An arc about c from a to b the way through m, as sketch arcs run (counter-clockwise).</summary>
    static Piece ArcPiece(Vec2 c, Vec2 a, Vec2 b, Vec2 m)
    {
        bool ccw = Vec2.Cross(b - a, m - a) < 0;
        var r = Vec2.Distance(c, a);
        return ccw ? new Piece(PieceKind.Arc, a, b, c, r) : new Piece(PieceKind.Arc, b, a, c, r);
    }

    static double SegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var d = b - a; var l2 = d.LengthSq;
        var t = l2 > 0 ? Math.Clamp(Vec2.Dot(p - a, d) / l2, 0, 1) : 0;
        return Vec2.Distance(a + d * t, p);
    }

    static Vec2? Circumcenter(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return null;
        var ux = (a.LengthSq * (b.Y - c.Y) + b.LengthSq * (c.Y - a.Y) + c.LengthSq * (a.Y - b.Y)) / d;
        var uy = (a.LengthSq * (c.X - b.X) + b.LengthSq * (a.X - c.X) + c.LengthSq * (b.X - a.X)) / d;
        return new Vec2(ux, uy);
    }

    /// <summary>A fixed point at p: the one there already, else a new one.</summary>
    static int Point(Sketch sk, Vec2 p)
    {
        foreach (var q in sk.Points) if (Vec2.Distance(q.P, p) < 1e-6) return q.Id;
        return sk.AddPoint(p, fix: true).Id;
    }

    /// <summary>An edge and the edges joined to it end to end where they run on smoothly (tangent), both ways.</summary>
    public static List<NEdge> TangentChain(NBody body, NEdge start)
    {
        body.Link();
        var at = new Dictionary<NVertex, List<NEdge>>();
        foreach (var e in body.Edges)
            foreach (var v in new[] { e.V0, e.V1 }.Distinct())
                (at.TryGetValue(v, out var l) ? l : at[v] = new()).Add(e);
        // the direction an edge leaves a vertex by
        static Vec3 Away(NEdge e, NVertex v)
        {
            var d = ReferenceEquals(v, e.V0) ? e.Curve.Deriv(e.T0) : -e.Curve.Deriv(e.T1);
            if (e.T1 < e.T0) d = -d;
            return d.Length > 1e-12 ? d.Normalized : d;
        }
        var chain = new List<NEdge> { start };
        var seen = new HashSet<NEdge> { start };
        var todo = new Stack<(NEdge e, NVertex v)>();
        if (!start.Closed) { todo.Push((start, start.V0)); todo.Push((start, start.V1)); }
        const double cosTol = 0.99985; // 1°
        while (todo.Count > 0)
        {
            var (e, v) = todo.Pop();
            if (!at.TryGetValue(v, out var others)) continue;
            var dir = -Away(e, v); // arriving at v
            foreach (var o in others)
            {
                if (seen.Contains(o) || o.Closed) continue;
                if (Vec3.Dot(dir, Away(o, v)) < cosTol) continue;
                seen.Add(o); chain.Add(o);
                todo.Push((o, ReferenceEquals(o.V0, v) ? o.V1 : o.V0));
                break; // one way on
            }
        }
        return chain;
    }
}

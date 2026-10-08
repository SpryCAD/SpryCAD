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
    public static bool Edge(Sketch sk, Frame frame, NEdge e, string? bodyId = null, int? group = null) => Edge(new Index(sk), frame, e, bodyId, group);

    static bool Edge(Index ix, Frame frame, NEdge e, string? bodyId = null, int? group = null)
    {
        var pieces = Pieces(frame, e);
        var link = bodyId != null && e.Uses.Count == 2 ? RefOf(e, bodyId) : null;
        bool any = false;
        for (int i = 0; i < pieces.Count; i++)
            any |= ix.Add(pieces[i], link == null ? null : new SkConvertLink { Edge = link, Piece = i, Of = pieces.Count, Group = group });
        return any;
    }

    static EdgeRef RefOf(NEdge e, string bodyId) => new() { A = e.Uses[0].Face.Tag.Name, B = e.Uses[1].Face.Tag.Name, Body = bodyId, Hint = e.Mid };

    /// <summary>What was picked to convert: a face (all its edges), an edge's tangent chain, or one edge.</summary>
    public sealed record Request(NBody Body, string BodyId, string? Face = null, NEdge? Edge = null, bool Chain = false)
    {
        /// <summary>The edges it stands for now.</summary>
        public List<NEdge> Edges()
        {
            if (Face != null) { Body.Link(); return FaceEdges(Body, Face); }
            return Edge == null ? new() : Chain ? TangentChain(Body, Edge) : new() { Edge };
        }
    }

    /// <summary>Converts what was picked; how many edges were added.</summary>
    public static int Apply(Sketch sk, Frame frame, IEnumerable<Request> picks)
    {
        int n = 0;
        var ix = new Index(sk);
        foreach (var r in picks)
            n += r.Face != null ? Face(sk, frame, r.Body, r.BodyId, r.Face, ix)
                : r.Edge == null ? 0
                : r.Chain ? Chain(sk, frame, r.Body, r.BodyId, r.Edge, ix)
                : Edge(ix, frame, r.Edge, r.BodyId) ? 1 : 0;
        return n;
    }

    /// <summary>
    /// Converts every edge of a face (all the body's faces by its name), as a whole: when the face later has other
    /// edges (more teeth, fewer holes), they are converted again from what the face is then. Returns how many edges added.
    /// </summary>
    public static int Face(Sketch sk, Frame frame, NBody body, string bodyId, string faceName) => Face(sk, frame, body, bodyId, faceName, new Index(sk));

    static int Face(Sketch sk, Frame frame, NBody body, string bodyId, string faceName, Index ix)
    {
        if (sk.ConvertGroups?.Any(g => g.Face == faceName && g.Body == bodyId) == true) return 0; // converted already
        body.Link();
        var edges = FaceEdges(body, faceName);
        return AddGroup(ix, frame, new SkConvertGroup { Body = bodyId, Face = faceName, Edges = edges.Count }, edges);
    }

    /// <summary>Converts an edge's tangent chain as a whole (followed again from that edge as the model changes).</summary>
    public static int Chain(Sketch sk, Frame frame, NBody body, string bodyId, NEdge start) => Chain(sk, frame, body, bodyId, start, new Index(sk));

    static int Chain(Sketch sk, Frame frame, NBody body, string bodyId, NEdge start, Index ix)
    {
        var edges = TangentChain(body, start);
        if (start.Uses.Count != 2) return edges.Count(e => Edge(ix, frame, e, bodyId)); // no names to find it again by
        return AddGroup(ix, frame, new SkConvertGroup { Body = bodyId, Chain = RefOf(start, bodyId), Edges = edges.Count }, edges);
    }

    static int AddGroup(Index ix, Frame frame, SkConvertGroup g, List<NEdge> edges)
    {
        var sk = ix.Sketch;
        g.Id = (sk.ConvertGroups?.Max(x => x.Id) ?? 0) + 1;
        (sk.ConvertGroups ??= new()).Add(g);
        var n = edges.Count(e => Edge(ix, frame, e, g.Body, g.Id));
        if (!sk.Entities.Any(x => x.Link?.Group == g.Id)) { sk.ConvertGroups.Remove(g); if (sk.ConvertGroups.Count == 0) sk.ConvertGroups = null; }
        return n;
    }

    static List<NEdge> FaceEdges(NBody body, string faceName) =>
        body.Faces.Where(f => f.Tag.Name == faceName).SelectMany(f => f.Coedges).Select(c => c.Edge).Distinct().ToList();

    /// <summary>A display polyline (no exact edge known) as lines, fixed.</summary>
    public static bool Polyline(Sketch sk, IReadOnlyList<Vec2> pts)
    {
        var ix = new Index(sk);
        bool any = false;
        for (int i = 0; i + 1 < pts.Count; i++)
            if (Vec2.Distance(pts[i], pts[i + 1]) >= 1e-9) any |= ix.Add(new Piece(PieceKind.Line, pts[i], pts[i + 1], default, 0), null);
        return any;
    }

    /// <summary>
    /// Places converted geometry where its edges are now (a rebuild). A face or chain converted as a whole whose
    /// edges changed (more, fewer, other curves) is converted again from what it is now. An edge picked alone and no
    /// longer in the model leaves its geometry where it was, with a warning.
    /// </summary>
    public static void Update(Sketch sk, Frame frame, ModelState state, List<string> warnings)
    {
        // a group none of whose geometry is left was deleted
        if (sk.ConvertGroups != null)
        {
            sk.ConvertGroups.RemoveAll(g => !sk.Entities.Any(e => e.Link?.Group == g.Id));
            if (sk.ConvertGroups.Count == 0) sk.ConvertGroups = null;
        }
        var singles = sk.Entities.Where(x => x.Link is { Group: null }).GroupBy(x => x.Link!.Key).ToList();
        if (singles.Count == 0 && sk.ConvertGroups == null) return;

        // the bodies' edges by the faces they join, gathered once (a sketch may hold hundreds of converted edges:
        // looked up one by one, each would scan every edge of the model)
        var names = sk.Entities.Where(x => x.Link != null).SelectMany(x => new[] { x.Link!.Edge.A, x.Link.Edge.B })
            .Concat(sk.ConvertGroups?.Where(g => g.Chain != null).SelectMany(g => new[] { g.Chain!.A, g.Chain.B }) ?? Enumerable.Empty<string>())
            .ToHashSet();
        var index = new Dictionary<(string? body, string a, string b), List<NEdge>>();
        static (string, string) Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        foreach (var body in state.Bodies)
        {
            if (body.Solid.Native is not { } nb) continue;
            nb.Link();
            var seen = new HashSet<NEdge>();
            foreach (var e in nb.Faces.Where(f => names.Contains(f.Tag.Name)).SelectMany(f => f.Coedges).Select(c => c.Edge))
            {
                if (e.Uses.Count != 2 || !seen.Add(e)) continue;
                var (a, b) = Pair(e.Uses[0].Face.Tag.Name, e.Uses[1].Face.Tag.Name);
                (index.TryGetValue((body.Id, a, b), out var l) ? l : index[(body.Id, a, b)] = new()).Add(e);
            }
        }
        NEdge? Find(EdgeRef r, Func<NEdge, bool>? free = null)
        {
            var (a, b) = Pair(r.A, r.B);
            if (index.TryGetValue((r.Body, a, b), out var l))
                return l.Where(e => free?.Invoke(e) ?? true).MinBy(e => Vec3.Distance(e.Mid, r.Hint));
            if (free != null) return null;
            // not between those faces any more (renamed, another body): the resolver's wider search
            try { return Resolver.ResolveNativeEdge(r, state)?.edge; } catch (FeatureException) { return null; }
        }
        // placed on its edge, the link following the edge (where it is now: found by it next time)
        var pointsById = sk.Points.ToDictionary(p => p.Id);
        void PlaceAll(List<SkEntity> ents, NEdge e, List<Piece> pieces, string bodyId)
        {
            var r = RefOf(e, bodyId);
            foreach (var x in ents)
            {
                Place(pointsById, x, pieces[x.Link!.Piece]);
                x.Link = new SkConvertLink { Edge = r, Piece = x.Link.Piece, Of = x.Link.Of, Group = x.Link.Group };
            }
        }
        bool Fits(List<SkEntity> ents, List<Piece> pieces) =>
            pieces.Count == ents[0].Link!.Of && ents.All(x => x.Link!.Piece < pieces.Count && KindOf(x) == pieces[x.Link.Piece].Kind);

        // what goes, and the edges converted again in its place (after the others are placed: their ends are found
        // where the neighbours now are)
        var remove = new List<SkEntity>();
        var add = new List<(NEdge e, string? body, int? group)>();
        foreach (var g in singles)
        {
            var ents = g.ToList();
            var link = ents[0].Link!;
            var ne = Find(link.Edge);
            if (ne == null) { warnings.Add("Converted sketch geometry: its model edge was not found (it stays where it was)."); continue; }
            var pieces = Pieces(frame, ne);
            if (Fits(ents, pieces)) PlaceAll(ents, ne, pieces, link.Edge.Body ?? "");
            else { remove.AddRange(ents); add.Add((ne, link.Edge.Body, null)); }
        }
        foreach (var grp in sk.ConvertGroups?.ToList() ?? new())
        {
            if (state.FindBody(grp.Body)?.Solid.Native is not { } nb) { warnings.Add("Converted sketch geometry: its body was not found (it stays where it was)."); continue; }
            var now = grp.Face != null ? FaceEdges(nb, grp.Face)
                : Find(grp.Chain!) is { } start ? TangentChain(nb, start) : new List<NEdge>();
            if (now.Count == 0) { warnings.Add($"Converted sketch geometry: its {(grp.Face != null ? "face" : "edges")} were not found (it stays where it was)."); continue; }
            // the same edges as when converted: each placed on its own (geometry deleted since stays deleted)
            var mine = sk.Entities.Where(x => x.Link?.Group == grp.Id).GroupBy(x => x.Link!.Key).Select(x => x.ToList()).ToList();
            var taken = new HashSet<NEdge>();
            var nowSet = now.ToHashSet();
            var placed = new List<(List<SkEntity> ents, NEdge e, List<Piece> pieces)>();
            bool same = now.Count == grp.Edges;
            foreach (var ents in mine)
            {
                if (!same) break;
                var e = Find(ents[0].Link!.Edge, x => nowSet.Contains(x) && !taken.Contains(x));
                List<Piece>? pieces = e == null ? null : Pieces(frame, e);
                if (e == null || !Fits(ents, pieces!)) { same = false; break; }
                taken.Add(e);
                placed.Add((ents, e, pieces!));
            }
            if (same) { foreach (var (ents, e, pieces) in placed) PlaceAll(ents, e, pieces, grp.Body); continue; }
            // other edges now: converted again from the face or chain as it is
            remove.AddRange(mine.SelectMany(x => x));
            add.AddRange(now.Select(e => (e, (string?)grp.Body, (int?)grp.Id)));
            grp.Edges = now.Count;
        }
        if (remove.Count == 0 && add.Count == 0) return;
        Remove(sk, remove);
        var ix = new Index(sk);
        foreach (var (e, body, group) in add) Edge(ix, frame, e, body, group);
    }

    /// <summary>
    /// Removes entities at once (as <see cref="Sketch.DeleteEntity"/> one by one: their points no other entity uses,
    /// and the relations on them, go too); one by one, a converted face of hundreds of edges is slow to take apart.
    /// </summary>
    static void Remove(Sketch sk, List<SkEntity> ents)
    {
        if (ents.Count == 0) return;
        var gone = ents.Select(e => e.Id).ToHashSet();
        sk.Entities.RemoveAll(e => gone.Contains(e.Id));
        var used = sk.Entities.SelectMany(e => e.PointIds).ToHashSet();
        var points = ents.SelectMany(e => e.PointIds).Where(p => p != Sketch.OriginId && !used.Contains(p)).ToHashSet();
        sk.Points.RemoveAll(p => points.Contains(p.Id));
        gone.UnionWith(points);
        sk.Constraints.RemoveAll(c => c.Refs.Any(gone.Contains));
        if (sk.Fillets != null)
        {
            sk.Fillets.RemoveAll(f => gone.Contains(f.Arc) || gone.Contains(f.TrimA) || gone.Contains(f.TrimB) || gone.Contains(f.LineA) || gone.Contains(f.LineB));
            if (sk.Fillets.Count == 0) sk.Fillets = null;
        }
    }

    static PieceKind? KindOf(SkEntity e) => e switch
    {
        SkLine => PieceKind.Line,
        SkArc => PieceKind.Arc,
        SkCircle => PieceKind.Circle,
        _ => null,
    };

    static void Place(Dictionary<int, SkPoint> pts, SkEntity x, Piece p)
    {
        switch (x)
        {
            case SkLine l: Set(pts, l.P1, p.A); Set(pts, l.P2, p.B); break;
            case SkArc a: Set(pts, a.Center, p.C); Set(pts, a.Start, p.A); Set(pts, a.End, p.B); break;
            case SkCircle c: Set(pts, c.Center, p.C); c.R = p.R; break;
        }
    }

    static void Set(Dictionary<int, SkPoint> pts, int id, Vec2 p)
    {
        if (id != Sketch.OriginId && pts.TryGetValue(id, out var pt)) { pt.X = p.X; pt.Y = p.Y; pt.Fixed = true; }
    }

    /// <summary>
    /// Adds pieces to a sketch: its points found by where they are (a grid, not a search of every point), and the
    /// lines, arcs and circles there already (not added twice), so converting hundreds of edges stays quick.
    /// </summary>
    sealed class Index
    {
        public readonly Sketch Sketch;
        const double Tol = 1e-6;
        readonly Dictionary<(long, long), List<SkPoint>> _grid = new();
        readonly HashSet<(int, int)> _lines = new();
        readonly HashSet<(int, int, int)> _arcs = new();
        readonly HashSet<(int, long)> _circles = new();

        public Index(Sketch sk)
        {
            Sketch = sk;
            foreach (var p in sk.Points) Cell(p.P).Add(p);
            foreach (var e in sk.Entities) Note(e);
        }

        static (long, long) Key(Vec2 p) => ((long)Math.Floor(p.X / Tol), (long)Math.Floor(p.Y / Tol));
        List<SkPoint> Cell(Vec2 p) { var k = Key(p); return _grid.TryGetValue(k, out var l) ? l : _grid[k] = new(); }
        static long RKey(double r) => (long)Math.Round(r / 1e-9);

        void Note(SkEntity e)
        {
            switch (e)
            {
                case SkLine l: _lines.Add((Math.Min(l.P1, l.P2), Math.Max(l.P1, l.P2))); break;
                case SkArc a: _arcs.Add((a.Center, a.Start, a.End)); break;
                case SkCircle c: _circles.Add((c.Center, RKey(c.R))); break;
            }
        }

        /// <summary>A fixed point at p: the one there already, else a new one.</summary>
        public int Point(Vec2 p)
        {
            var (kx, ky) = Key(p);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (_grid.TryGetValue((kx + dx, ky + dy), out var l))
                        foreach (var q in l) if (Vec2.Distance(q.P, p) < Tol) return q.Id;
            var np = Sketch.AddPoint(p, fix: true);
            Cell(p).Add(np);
            return np.Id;
        }

        /// <summary>Adds a piece (its points shared with what is there); false when the same geometry is there already.</summary>
        public bool Add(Piece p, SkConvertLink? link)
        {
            SkEntity made;
            switch (p.Kind)
            {
                case PieceKind.Line:
                    {
                        int pa = Point(p.A), pb = Point(p.B);
                        if (_lines.Contains((Math.Min(pa, pb), Math.Max(pa, pb)))) return false;
                        made = Sketch.Add(new SkLine { P1 = pa, P2 = pb, Link = link });
                        break;
                    }
                case PieceKind.Arc:
                    {
                        int c = Point(p.C), s = Point(p.A), e = Point(p.B);
                        if (_arcs.Contains((c, s, e))) return false;
                        made = Sketch.Add(new SkArc { Center = c, Start = s, End = e, Link = link });
                        break;
                    }
                default:
                    {
                        int c = Point(p.C);
                        if (_circles.Contains((c, RKey(p.R)))) return false;
                        var ci = Sketch.Add(new SkCircle { Center = c, R = p.R, Link = link });
                        // not linked: its radius held by a dimension (a linked one takes it from the model)
                        if (link == null)
                        {
                            var rc = Sketch.AddConstraint(CType.Radius, ci.Id); rc.Value = p.R; rc.Expr = Expr.Format(p.R);
                            rc.LabelPlaced = false; // made, not placed: laid out
                        }
                        made = ci;
                        break;
                    }
            }
            Note(made);
            return true;
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

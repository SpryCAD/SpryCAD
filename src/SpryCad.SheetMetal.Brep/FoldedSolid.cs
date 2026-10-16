using SpryCad.Kernel;
using SpryCad.Kernel.Native;
using KV = SpryCad.Kernel.Vec3;

namespace SpryCad.SheetMetal.Brep;

/// <summary>
/// The solid of a sheet metal body, built face by face from its bend graph (sheet metal spec section 10): each panel
/// its flat outline thick t, placed by its folded frame; each bend zone a cylindrical shell between its panels. Faces
/// meet by construction (no booleans). Faces are named by the graph: "P.Top", "P.Bottom", "P.Edge(n)" for a panel P,
/// "B.Inside", "B.Outside", "B.End0", "B.End1" for a bend B; so other features, dimensions and comments stay on them.
/// </summary>
public static class FoldedSolid
{
    public static string Top(PanelId p) => p.Value + ".Top";
    public static string Bottom(PanelId p) => p.Value + ".Bottom";
    public static string Edge(PanelId p, int n) => $"{p.Value}.Edge({n})";

    /// <summary>The panel and edge index a face name "P.Edge(n)" names (null: not a panel's edge face).</summary>
    public static (PanelId panel, int edge)? ParseEdge(string faceName)
    {
        var i = faceName.LastIndexOf(".Edge(", StringComparison.Ordinal);
        if (i < 0 || !faceName.EndsWith(')')) return null;
        var n = faceName[(i + 6)..^1];
        return int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k)
            ? (new PanelId(faceName[..i]), k) : null;
    }

    /// <summary>
    /// The body folded (or flat, <paramref name="flat"/>: every piece where the flat pattern has it), its flat
    /// coordinates placed in the part by <paramref name="world"/>.
    /// </summary>
    public static NBody Build(BendGraph graph, Frame3 world, bool flat = false)
    {
        var g = flat ? graph : graph.Fold();
        if (flat) g.Walk(); // (the same checks)
        double t = g.Thickness;
        var pieces = FlatPattern2D.Pieces(g);
        foreach (var b in g.Bends)
            if (!flat && b.InsideRadius <= 0) throw new BendGraphException($"{b.Id}: a bend with no inside radius cannot be modelled yet.");

        var body = new NBody();
        var vertices = new Dictionary<string, NVertex>();
        var edges = new Dictionary<string, NEdge>();
        const double tol = 1e-7;
        static string PKey(Vec2 p) => FormattableString.Invariant($"{Math.Round(p.X / tol)}:{Math.Round(p.Y / tol)}");

        // the pieces' edges split where another piece's corner lies on them; every directed segment: a segment run
        // both ways is between two pieces (no side face)
        var split = FlatPattern2D.Split(pieces, tol);
        var directed = new HashSet<string>();
        foreach (var (_, ls) in split)
            foreach (var loop in ls)
                for (int i = 0; i < loop.Count; i++)
                    if (loop[i].bulge == 0) directed.Add(PKey(loop[i].p) + ">" + PKey(loop[(i + 1) % loop.Count].p));

        foreach (var (piece, splitLoops) in split)
        {
            var map = new PieceMap(g, piece, world, flat);
            NVertex V(Vec2 p, bool top)
            {
                var k = PKey(p) + (top ? "T" : "B");
                if (!vertices.TryGetValue(k, out var v)) vertices[k] = v = new NVertex(K(map.Point(p, top ? 0 : -t)));
                return v;
            }
            // an edge on the top or bottom: straight, or an arc (bulge for a→b; the two halves of a circle have the same
            // ends, so an arc is known by its middle too)
            NCoedge E(Vec2 a, Vec2 b, double bulge, bool top)
            {
                var (ka, kb) = (PKey(a), PKey(b));
                var key = string.CompareOrdinal(ka, kb) < 0 ? $"{ka}|{kb}" : $"{kb}|{ka}";
                if (bulge != 0) key += "|" + PKey(new Seg2(a, b, bulge).At(0.5));
                key += top ? "T" : "B";
                var va = V(a, top); var vb = V(b, top);
                if (!edges.TryGetValue(key, out var e)) edges[key] = e = map.Edge(a, b, bulge, top ? 0 : -t, va, vb);
                return new NCoedge(e, ReferenceEquals(e.V0, va));
            }
            NCoedge Up(Vec2 p, bool up)
            {
                var key = PKey(p) + "|V";
                var vb = V(p, false); var vt = V(p, true);
                if (!edges.TryGetValue(key, out var e)) edges[key] = e = new NEdge(new NLine(vb.P, vt.P - vb.P), 0, (vt.P - vb.P).Length, vb, vt);
                return new NCoedge(e, up);
            }

            // top and bottom
            var loops = splitLoops.Select(l => l.Select(x => x.p).ToList()).ToList();
            var topFace = map.Face(true);
            var bottomFace = map.Face(false);
            for (int li = 0; li < loops.Count; li++)
            {
                var loop = loops[li];
                double B(int i) => splitLoops[li][i].bulge;
                var lt = new NLoop();
                for (int i = 0; i < loop.Count; i++) lt.Coedges.Add(E(loop[i], loop[(i + 1) % loop.Count], B(i), true));
                topFace.Loops.Add(lt);
                var lb = new NLoop();
                for (int i = loop.Count - 1; i >= 0; i--) lb.Coedges.Add(E(loop[(i + 1) % loop.Count], loop[i], -B(i), false));
                bottomFace.Loops.Add(lb);
            }
            body.Faces.Add(topFace);
            body.Faces.Add(bottomFace);

            // the sides where no other piece continues the sheet
            for (int li = 0; li < loops.Count; li++)
            {
                var loop = loops[li];
                for (int i = 0; i < loop.Count; i++)
                {
                    Vec2 a = loop[i], b = loop[(i + 1) % loop.Count];
                    var bulge = splitLoops[li][i].bulge;
                    if (bulge == 0 && directed.Contains(PKey(b) + ">" + PKey(a))) continue;
                    var face = map.SideFace(a, b, bulge, -t, SideName(piece, li, splitLoops[li][i].edge));
                    var l = new NLoop();
                    l.Coedges.Add(E(a, b, bulge, false));
                    l.Coedges.Add(Up(b, true));
                    l.Coedges.Add(E(b, a, -bulge, true));
                    l.Coedges.Add(Up(a, false));
                    face.Loops.Add(l);
                    body.Faces.Add(face);
                }
            }
        }
        body.Link();
        if (body.Validate() is { } problem) throw new BendGraphException($"The sheet's solid is not valid: {problem}");
        return body;
    }

    static IEnumerable<IReadOnlyList<Vec2>> Loops(FlatPiece p) => new[] { p.Outline.Outer }.Concat(p.Outline.Holes);

    static string SideName(FlatPiece piece, int loop, int i)
    {
        if (piece.Bend is { } b) return $"{b.Id.Value}.End{(i == 1 ? 0 : 1)}";
        return loop == 0 ? Edge(piece.Panel!.Id, i) : $"{piece.Id}.Hole{loop - 1}.Edge({i})";
    }

    static KV K(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Where a piece's flat points go: a panel rigidly; a bend zone wrapped round its axis.</summary>
    sealed class PieceMap
    {
        readonly FlatPiece _piece;
        readonly Frame3 _place;   // world ∘ the panel's (or the bend's A panel's) folded frame
        readonly bool _wrap;
        readonly Bend? _bend;
        readonly double _t;

        public PieceMap(BendGraph g, FlatPiece piece, Frame3 world, bool flat)
        {
            _piece = piece;
            _t = g.Thickness;
            _bend = piece.Bend;
            var frame = piece.Panel?.FoldedFrame ?? g.Panel(piece.Bend!.A).FoldedFrame;
            _place = world.Compose(flat ? Frame3.Identity : frame);
            _wrap = !flat && _bend != null;
        }

        bool Up => _bend!.Direction == BendDirection.Up;
        /// <summary>The bend axis's height in A's flat: above the top for Up bends, below the bottom for Down.</summary>
        double AxisZ => Up ? _bend!.InsideRadius : -_t - _bend!.InsideRadius;
        /// <summary>The rotation's axis (right-handed) and its sign: Up turns B toward the top.</summary>
        Vec3 AxisDir => Vec3.From(_bend!.FlatAxis.Dir);
        double Sign => Up ? 1 : -1;
        /// <summary>The distance from the axis at flat height z.</summary>
        double Rho(double z) => Up ? _bend!.InsideRadius - z : _bend!.InsideRadius + _t + z;

        /// <summary>How far along the bend p is: its distance from A's tangent line, and its foot on that line.</summary>
        (double s, Vec2 foot) Along(Vec2 p)
        {
            var ta = _bend!.TangentA;
            var s = ta.Side(p);
            return (s, p - ta.Normal * s);
        }

        double Phi(double s) => _bend!.Allowance > 0 ? s / _bend.Allowance * _bend.Angle : 0;

        public Vec3 Point(Vec2 p, double z)
        {
            if (!_wrap) return _place.Apply(new Vec3(p.X, p.Y, z));
            var (s, foot) = Along(p);
            var axisPoint = new Vec3(foot.X, foot.Y, AxisZ);
            var rot = Frame3.Rotation(axisPoint, AxisDir, Sign * Phi(s));
            return _place.Apply(rot.Apply(new Vec3(foot.X, foot.Y, z)));
        }

        public NEdge Edge(Vec2 a, Vec2 b, double bulge, double z, NVertex va, NVertex vb)
        {
            if (bulge != 0)
            {
                if (_wrap) throw new BendGraphException($"{_bend!.Id}: a round edge across a bend zone cannot be modelled yet.");
                // an arc of a panel's outline: about the panel's normal, counterclockwise for a positive bulge
                var s = new Seg2(a, b, bulge);
                var c = s.Center;
                var center = _place.Apply(new Vec3(c.X, c.Y, z));
                var axis = _place.ApplyDir(new Vec3(0, 0, Math.Sign(s.Sweep)));
                return new NEdge(new NCircle(K(center), K(axis), va.P - K(center), s.Radius), 0, Math.Abs(s.Sweep), va, vb);
            }
            if (_wrap)
            {
                var d = (b - a).Normalized();
                var along = Math.Abs(Vec2.Dot(d, _bend!.FlatAxis.Dir));
                if (along < 1 - 1e-9)
                {
                    if (along > 1e-9) throw new BendGraphException($"{_bend.Id}: a cut across the bend zone at an angle cannot be modelled yet.");
                    // across the bend: an arc about the axis
                    var (sa, foot) = Along(a);
                    var (sb, _) = Along(b);
                    var center = _place.Apply(new Vec3(foot.X, foot.Y, AxisZ));
                    var dphi = Sign * (Phi(sb) - Phi(sa));
                    var axis = _place.ApplyDir(AxisDir) * Math.Sign(dphi);
                    var circle = new NCircle(K(center), K(axis), va.P - K(center), Rho(z));
                    return new NEdge(circle, 0, Math.Abs(dphi), va, vb);
                }
            }
            var dir = vb.P - va.P;
            return new NEdge(new NLine(va.P, dir), 0, dir.Length, va, vb);
        }

        /// <summary>The piece's top (z = 0) or bottom face, its normal out of the material.</summary>
        public NFace Face(bool top)
        {
            if (!_wrap)
            {
                var n = _place.ApplyDir(new Vec3(0, 0, top ? 1 : -1));
                var p = _place.Apply(new Vec3(0, 0, top ? 0 : -_t));
                var name = _piece.Panel != null ? (top ? Top(_piece.Panel.Id) : Bottom(_piece.Panel.Id))
                    : _bend!.Id.Value + (top == (_bend.Direction == BendDirection.Up) ? ".Inside" : ".Outside");
                return new NFace(NSurface.FromPlane(Plane.FromPointNormal(K(p), K(n))), true, new FaceTag(name, SurfaceDef.PlaneDef));
            }
            double z = top ? 0 : -_t;
            bool inside = Rho(z) < Rho(top ? -_t : 0);
            var ta = _bend!.TangentA;
            var origin = _place.Apply(new Vec3(ta.Point.X, ta.Point.Y, AxisZ));
            // the reference direction: from the axis to the bend's start
            var refDir = _place.ApplyDir(new Vec3(0, 0, Up ? -1 : 1));
            var def = new SurfaceDef
            {
                Kind = SurfaceKind.Cylinder, Origin = K(origin), Axis = K(_place.ApplyDir(AxisDir)), RefDir = K(refDir),
                Radius = Rho(z), Segments = 64,
            };
            var mid = Point(ta.Point + ta.Normal * (_bend.Allowance / 2) + ta.Dir * 0, z);
            // the face's normal out of the material: toward the axis on the inside of the bend, away on the outside
            return new NFace(new NSurface(def), !inside, new FaceTag(_bend.Id.Value + (inside ? ".Inside" : ".Outside"), def), K(mid));
        }

        /// <summary>A side face along the flat segment a→b (the piece on its left), its normal outward.</summary>
        public NFace SideFace(Vec2 a, Vec2 b, double bulge, double zBottom, string name)
        {
            if (bulge != 0)
            {
                // a round side: a cylinder about the panel's normal; facing away from its axis where the outline bulges
                // out (a counterclockwise arc on its left), toward it where it bulges in (a hole's, run clockwise)
                var s = new Seg2(a, b, bulge);
                var c = s.Center;
                var def = new SurfaceDef
                {
                    Kind = SurfaceKind.Cylinder, Origin = K(_place.Apply(new Vec3(c.X, c.Y, zBottom))), Axis = K(_place.ApplyDir(new Vec3(0, 0, 1))),
                    RefDir = K(_place.ApplyDir(Vec3.From(a - c))), Radius = s.Radius, Segments = 64,
                };
                var mid = s.At(0.5);
                return new NFace(new NSurface(def), s.Sweep > 0, new FaceTag(name, def), K(_place.Apply(new Vec3(mid.X, mid.Y, zBottom / 2))));
            }
            var d = (b - a).Normalized();
            var nFlat = new Vec3(d.Y, -d.X, 0);
            // a bend zone's end faces are across its axis: their normal is along the axis, which wrapping keeps
            var n = _place.ApplyDir(nFlat);
            var p = Point(a, zBottom);
            return new NFace(NSurface.FromPlane(Plane.FromPointNormal(K(p), K(n))), true, new FaceTag(name, SurfaceDef.PlaneDef));
        }
    }
}

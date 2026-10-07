using FeatherCad.Kernel.Native;

namespace FeatherCad.Kernel;

/// <summary>
/// A bend of a flat pattern: its centerline in the flat, the bend angle (radians), inner radius and length in the
/// flat (the bend allowance), and its tangent lines (where the bend meets the walls) as the pieces over material.
/// </summary>
/// <param name="Up">The bend folds toward the flat's normal (the unfolded side is the bend's inside).</param>
public sealed record FlatBend(Vec2 A, Vec2 B, double Angle, double InnerRadius, bool Up, double Allowance,
    IReadOnlyList<(Vec2 A, Vec2 B)> Tangents);

public enum BendAllowanceMethod
{
    /// <summary>The neutral fiber at K × thickness from the inside of the bend.</summary>
    KFactor,
    /// <summary>DIN 6935: k = 0.65 + ½ log₁₀(r/s) (1 from r/s = 5), neutral fiber at k × s/2; bends under 15° need none.</summary>
    Din6935,
}

/// <summary>How long a bend is in the flat.</summary>
public sealed record BendRule(BendAllowanceMethod Method, double K = 0.5)
{
    public static readonly BendRule Default = new(BendAllowanceMethod.KFactor, 0.5);

    /// <summary>The bend allowance: the length in the flat of a bend of <paramref name="angle"/> (radians), inner radius r, thickness t.</summary>
    public double Allowance(double angle, double r, double t)
    {
        if (Method == BendAllowanceMethod.KFactor) return angle * (r + K * t);
        // DIN 6935: the compensation v is 0 for opening angles over 165°: the flat is the outer lengths to the
        // sharp corner, 2 (r + s) tan(θ/2) for the bend
        if (angle < 15 * Math.PI / 180) return 2 * (r + t) * Math.Tan(angle / 2);
        return angle * (r + DinK(r, t) * t / 2);
    }

    /// <summary>DIN 6935's correction factor k (relative to half the thickness).</summary>
    public static double DinK(double r, double t) => r >= 5 * t ? 1 : Math.Clamp(0.65 + 0.5 * Math.Log10(r / t), 0, 1);

    public override string ToString() => Method == BendAllowanceMethod.KFactor ? $"K-factor {K:0.###}" : "DIN 6935";
}

/// <summary>
/// The flat pattern of a sheet metal body: the outline in the plane of the fixed face (2D in <see cref="Frame"/>,
/// the unfolded side at z = 0, the material on the -Normal side), the bend lines and the flat solid.
/// </summary>
public sealed class FlatPattern
{
    public required double Thickness { get; init; }
    public required BendRule Rule { get; init; }
    public required Frame Frame { get; init; }
    public required List<ProfileRegion> Regions { get; init; }
    public required List<FlatBend> Bends { get; init; }
    public required NBody Body { get; init; }
}

/// <summary>A body that cannot be unfolded, with a message for the user.</summary>
public sealed class UnfoldException(string message) : Exception(message);

/// <summary>
/// Unfolds a sheet metal body: a solid of uniform thickness whose faces on one side (the faces tangent-connected
/// to the fixed face) are planes (walls) and cylinders (bends). Walls move rigidly; a bend unrolls onto the plane
/// with the length of its neutral fiber, angle × (inner radius + K × thickness). Nothing is stored on the body:
/// any body of that shape unfolds, whatever features made it (shelled and filleted parts, imported sheet metal).
/// </summary>
public static class SheetUnfold
{
    /// <summary>Unfolds with a K-factor (see <see cref="Unfold(NBody, BendRule, NFace?, string)"/>).</summary>
    public static FlatPattern Unfold(NBody body, double kFactor = 0.5, NFace? fixedFace = null, string prefix = "Flat")
        => Unfold(body, new BendRule(BendAllowanceMethod.KFactor, kFactor), fixedFace, prefix);

    /// <summary>Unfolds <paramref name="body"/> about <paramref name="fixedFace"/> (a planar face; the largest one when null).</summary>
    public static FlatPattern Unfold(NBody body, BendRule rule, NFace? fixedFace = null, string prefix = "Flat")
    {
        if (rule.Method == BendAllowanceMethod.KFactor && (rule.K < 0 || rule.K > 1)) throw new UnfoldException("The K-factor must be between 0 and 1.");
        body.Link();
        var scale = Math.Max(body.Bounds.Diagonal, 1e-9);
        var tol = 1e-7 * scale;
        var f0 = fixedFace ?? body.Faces.Where(f => f.Surface.Kind == SurfaceKind.Plane).MaxBy(PlanarArea)
            ?? throw new UnfoldException("The body has no planar face to keep fixed.");
        if (f0.Surface.Kind != SurfaceKind.Plane) throw new UnfoldException("The fixed face must be planar.");

        var n0 = Outward(f0, f0.Coedges.First().Edge.V0.P);
        var o0 = f0.Coedges.First().Edge.V0.P;
        var t = Thickness(body, f0, n0, o0, tol);

        // the unfolded side: the faces tangent-connected to the fixed face, each with its map to the flat
        var u0 = f0.Surface.F.XDir; u0 = (u0 - n0 * Vec3.Dot(u0, n0)).Normalized; var v0 = Vec3.Cross(n0, u0);
        var maps = new Dictionary<NFace, FaceMap>(ReferenceEqualityComparer.Instance)
        {
            [f0] = new WallMap(o0, u0, v0, Vec2.Zero, new Vec2(1, 0), new Vec2(0, 1)),
        };
        var queue = new Queue<NFace>(); queue.Enqueue(f0);
        while (queue.Count > 0)
        {
            var f = queue.Dequeue();
            var m = maps[f];
            foreach (var c in f.Coedges)
            {
                var g = Other(c);
                if (g == null || maps.ContainsKey(g)) continue;
                var mid = c.Edge.Mid;
                if (Vec3.Dot(Outward(f, mid), Outward(g, mid)) < 1 - 1e-6) continue; // a sharp edge: the side of the sheet
                // a bend split in pieces (e.g. by a hole) and reached again from its other side is still one bend
                maps[g] = g.Surface.Kind == SurfaceKind.Cylinder && maps.Values.OfType<BendMap>().FirstOrDefault(b => Coaxial(g.Surface, b, tol)) is { } same
                    ? same : Continue(f, m, g, c.Edge, t, rule, tol);
                queue.Enqueue(g);
            }
        }

        foreach (var (f, m) in maps)
            if (!HasOpposite(body, f, t, tol))
                throw new UnfoldException($"Face {f.Tag.Name} has no opposite face at the sheet thickness ({t:0.###}): the body is not a sheet.");

        var loops = Outline(maps, tol, scale);
        var regions = ProfileRegion.FromLoops(loops).Select(r => r.Normalized()).ToList();
        var frame = new Frame(o0, u0, v0, n0);
        var flat = NBuild.Extrude(regions, frame, -t, 0, prefix + ".");

        var bends = maps.Values.OfType<BendMap>().Distinct()
            .Select(b => Clip(b.Line(maps.Where(kv => kv.Value == b).Select(kv => kv.Key)), loops)).ToList();
        return new FlatPattern { Thickness = t, Rule = rule, Frame = frame, Regions = regions, Bends = bends, Body = flat };
    }

    // ---- maps from the folded faces to the flat ----

    abstract class FaceMap
    {
        public abstract Vec2 Map(Vec3 p);
    }

    /// <summary>Rigid map of a plane: p3 → p2, the in-plane directions a3, d3 → a2, d2.</summary>
    sealed class WallMap(Vec3 p3, Vec3 a3, Vec3 d3, Vec2 p2, Vec2 a2, Vec2 d2) : FaceMap
    {
        public override Vec2 Map(Vec3 p) { var q = p - p3; return p2 + a2 * Vec3.Dot(q, a3) + d2 * Vec3.Dot(q, d3); }
        public Vec2 Dir(Vec3 v) => a2 * Vec3.Dot(v, a3) + d2 * Vec3.Dot(v, d3);
        /// <summary>The plane spanned by a3, d3 has normal n.</summary>
        public bool Contains(Vec3 n, double angTol) => Math.Abs(Vec3.Dot(n, a3)) < angTol && Math.Abs(Vec3.Dot(n, d3)) < angTol;
    }

    /// <summary>
    /// Unrolls a cylinder: the entry line (radial <c>R0</c> from the axis point <c>C</c>) maps to p2 along a2, and the
    /// angle θ from it (positive toward the tangent <c>W</c>) maps to θ × neutral radius along w2.
    /// </summary>
    sealed class BendMap(Vec3 c, Vec3 axis, Vec3 r0, Vec3 w, double radius, double innerRadius, double neutral, bool up, Vec2 p2, Vec2 a2, Vec2 w2) : FaceMap
    {
        public readonly Vec3 C = c, Axis = axis;
        public readonly double Radius = radius;
        public readonly Vec2 A2 = a2, W2 = w2;
        public double Theta(Vec3 p)
        {
            var q = p - C; q -= Axis * Vec3.Dot(q, Axis);
            var th = Math.Atan2(Vec3.Dot(q, w), Vec3.Dot(q, r0));
            return th < -0.5 ? th + 2 * Math.PI : th; // beyond 180° (coaxial faces of one bend share the map)
        }
        public override Vec2 Map(Vec3 p) => p2 + a2 * Vec3.Dot(p - C, Axis) + w2 * (Theta(p) * neutral);
        /// <summary>The tangent leaving the bend at angle θ (continuing in the direction of increasing θ).</summary>
        public Vec3 Tangent(double th) => w * Math.Cos(th) - r0 * Math.Sin(th);

        public FlatBend Line(IEnumerable<NFace> faces)
        {
            double th = 0, h0 = double.MaxValue, h1 = double.MinValue;
            foreach (var f in faces)
                foreach (var co in f.Coedges)
                    foreach (var p in co.Edge.Samples(1e-3))
                    {
                        th = Math.Max(th, Theta(p));
                        var h = Vec3.Dot(p - C, Axis);
                        h0 = Math.Min(h0, h); h1 = Math.Max(h1, h);
                    }
            (Vec2, Vec2) At(double s) => (p2 + a2 * h0 + w2 * s, p2 + a2 * h1 + w2 * s);
            var (a, b) = At(th * neutral / 2);
            return new FlatBend(a, b, th, innerRadius, up, th * neutral, new[] { At(0), At(th * neutral) });
        }

        /// <summary>The largest angle of the face's boundary from the entry line: the bend angle.</summary>
        public double Angle(NFace f)
        {
            double th = 0;
            foreach (var co in f.Coedges)
                foreach (var p in co.Edge.Samples(1e-3)) th = Math.Max(th, Theta(p));
            return th;
        }
    }

    /// <summary>The map of face g, tangent to the mapped face f across edge e.</summary>
    static FaceMap Continue(NFace f, FaceMap m, NFace g, NEdge e, double t, BendRule rule, double tol)
    {
        var p = e.V0.P;
        var gs = g.Surface;
        if (gs.Kind != SurfaceKind.Plane && gs.Kind != SurfaceKind.Cylinder)
            throw new UnfoldException($"Face {g.Tag.Name} is a {gs.Kind.ToString().ToLowerInvariant()}: only planes and cylinders unfold.");

        // the link: a point of the edge, the direction along the edge and the direction leaving f into g
        Vec3 a3, d3;
        if (m is BendMap bm) { a3 = bm.Axis; d3 = bm.Tangent(bm.Theta(p)); }
        else
        {
            var wm = (WallMap)m;
            if (gs.Kind == SurfaceKind.Plane) return wm; // coplanar walls (split faces)
            a3 = gs.Axis.Normalized;
            var nf = Outward(f, p);
            d3 = Vec3.Cross(nf, a3).Normalized;
            // leaving f: f's own vertices lie behind the edge
            double side = 0;
            foreach (var co in f.Coedges) side += Vec3.Dot(co.Edge.V0.P - p, d3);
            if (side > 0) d3 = -d3;
        }
        var p2 = m.Map(p);
        Vec2 a2, d2;
        if (m is WallMap w) { a2 = w.Dir(a3); d2 = w.Dir(d3); }
        else { var b = (BendMap)m; a2 = b.A2; d2 = b.W2; } // leaving a bend: the unrolled directions continue

        if (gs.Kind == SurfaceKind.Plane)
        {
            var wall = new WallMap(p, a3, d3, p2, a2, d2);
            if (!wall.Contains(Outward(g, p), 1e-6))
                throw new UnfoldException($"Face {g.Tag.Name} does not continue the bend tangentially.");
            return wall;
        }

        // a bend: its axis must run along the edge
        var axis = gs.Axis.Normalized;
        if (Vec3.Cross(axis, a3).Length > 1e-6) throw new UnfoldException($"Face {g.Tag.Name} is not a bend: its axis does not run along its edge.");
        if (Vec3.Dot(axis, a3) < 0) axis = -axis;
        var q = p - gs.Origin;
        var foot = gs.Origin + axis * Vec3.Dot(q, axis);
        var radial = p - foot;
        var radius = radial.Length;
        var r0 = radial * (1 / radius);
        if (Math.Abs(Vec3.Dot(r0, d3)) > 1e-6) throw new UnfoldException($"Face {g.Tag.Name} is not tangent to {f.Tag.Name}.");
        var convex = Vec3.Dot(Outward(g, p), r0) > 0;
        var inner = convex ? radius - t : radius;
        if (inner < -tol) throw new UnfoldException($"Bend {g.Tag.Name} is sharper than the sheet is thick.");
        inner = Math.Max(inner, 0);
        // the neutral radius makes the bend as long as the rule says (it depends on the angle for DIN 6935)
        var angle = new BendMap(foot, axis, r0, d3, radius, inner, 1, !convex, p2, a2, d2).Angle(g);
        var neutral = angle > 1e-9 ? rule.Allowance(angle, inner, t) / angle : inner + t / 2;
        return new BendMap(foot, axis, r0, d3, radius, inner, neutral, !convex, p2, a2, d2);
    }

    static bool Coaxial(NSurface s, BendMap b, double tol)
    {
        var a = s.Axis.Normalized;
        if (Vec3.Cross(a, b.Axis).Length > 1e-9) return false;
        var q = s.Origin - b.C;
        return (q - b.Axis * Vec3.Dot(q, b.Axis)).Length < tol && Math.Abs(s.R - b.Radius) < tol;
    }

    // ---- the outline ----

    /// <summary>The boundary of the mapped faces (the edges to faces off the unfolded side) as closed 2D loops.</summary>
    static List<ProfileLoop> Outline(Dictionary<NFace, FaceMap> maps, double tol, double scale)
    {
        var bound = new List<NCoedge>();
        foreach (var f in maps.Keys)
            foreach (var c in f.Coedges)
            {
                var g = Other(c);
                if (g == null || !maps.ContainsKey(g)) bound.Add(c);
            }
        var byStart = new Dictionary<NVertex, List<NCoedge>>(ReferenceEqualityComparer.Instance);
        foreach (var c in bound)
        {
            if (!byStart.TryGetValue(c.Start, out var l)) byStart[c.Start] = l = new();
            l.Add(c);
        }
        var used = new HashSet<NCoedge>(ReferenceEqualityComparer.Instance);
        var loops = new List<ProfileLoop>();
        var sampleTol = Math.Max(1e-4, 1e-5 * scale);
        foreach (var first in bound)
        {
            if (used.Contains(first)) continue;
            var loop = new ProfileLoop();
            NCoedge? c = first, last = first;
            while (c != null && used.Add(c))
            {
                AddCoedge(loop, c, maps[c.Face], sampleTol, tol);
                last = c;
                c = byStart.TryGetValue(c.End, out var next) ? next.FirstOrDefault(x => !used.Contains(x)) : null;
            }
            if (!ReferenceEquals(last.End, first.Start)) throw new UnfoldException("The flat outline does not close.");
            MergeCollinear(loop, tol);
            if (loop.Verts.Count >= 2) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// The bend's lines where there is material: from the first to the last crossing of the outline within the
    /// bend's width (reliefs and notches at the ends of a bend shorten them, as in other CAD systems' flat patterns).
    /// </summary>
    static FlatBend Clip(FlatBend b, List<ProfileLoop> loops)
    {
        var (a, c) = Clip((b.A, b.B), loops);
        // a tangent line is drawn only where there is material (it may leave and enter it again at a notch)
        return b with { A = a, B = c, Tangents = b.Tangents.SelectMany(x => Inside(x, loops)).ToList() };
    }

    /// <summary>A centerline from where it first enters the material to where it last leaves it (one line, as other systems' DXF).</summary>
    static (Vec2 A, Vec2 B) Clip((Vec2 A, Vec2 B) seg, List<ProfileLoop> loops)
    {
        var pieces = Inside(seg, loops);
        return pieces.Count == 0 ? seg : (pieces[0].A, pieces[^1].B);
    }

    /// <summary>
    /// The pieces of a segment inside the material: split where it meets the outline, each piece kept when its
    /// middle is inside (even-odd over all loops). Robust where the segment runs through outline vertices, as a
    /// tangent line does where a bend's end meets a wall (a crossing count would see such a vertex twice).
    /// </summary>
    static List<(Vec2 A, Vec2 B)> Inside((Vec2 A, Vec2 B) seg, List<ProfileLoop> loops)
    {
        var d = seg.B - seg.A; var len = d.Length;
        var res = new List<(Vec2 A, Vec2 B)>();
        if (len < 1e-12) return res;
        var u = d * (1 / len);
        var ts = new List<double> { 0, len };
        foreach (var l in loops)
        {
            var v = l.Verts;
            for (int i = 0; i < v.Count; i++)
            {
                var p = v[i].P; var q = v[(i + 1) % v.Count].P;
                var e = q - p;
                var den = Vec2.Cross(u, e);
                if (Math.Abs(den) < 1e-15) continue; // parallel: its ends are other segments' ends
                var w = p - seg.A;
                var s = Vec2.Cross(w, u) / den; // along the outline segment
                if (s < -1e-9 || s > 1 + 1e-9) continue;
                var t = Vec2.Cross(w, e) / den;  // along ours
                if (t > 0 && t < len) ts.Add(t);
            }
        }
        ts.Sort();
        bool In(Vec2 p) => loops.Count(l => l.Contains(p)) % 2 == 1;
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            double a = ts[i], c = ts[i + 1];
            if (c - a < 1e-9 * Math.Max(1, len) || !In(seg.A + u * (0.5 * (a + c)))) continue;
            // join with the previous piece when they touch
            if (res.Count > 0 && Vec2.Distance(res[^1].B, seg.A + u * a) < 1e-9 * Math.Max(1, len)) res[^1] = (res[^1].A, seg.A + u * c);
            else res.Add((seg.A + u * a, seg.A + u * c));
        }
        return res;
    }

    /// <summary>
    /// Joins straight segments that continue each other (a side of the sheet runs across walls and bends: one edge
    /// in the flat, named after its first piece).
    /// </summary>
    static void MergeCollinear(ProfileLoop loop, double tol)
    {
        var v = loop.Verts;
        for (bool changed = true; changed && v.Count > 3;)
        {
            changed = false;
            for (int i = 0; i < v.Count && v.Count > 3; i++)
            {
                var prev = v[(i - 1 + v.Count) % v.Count]; var cur = v[i]; var next = v[(i + 1) % v.Count];
                if (prev.Arc != null || cur.Arc != null) continue;
                var d = next.P - prev.P;
                if (d.Length < 1e-12 || Vec2.Dot(cur.P - prev.P, d) <= 0 || Vec2.Dot(next.P - cur.P, d) <= 0) continue;
                if (Math.Abs(Vec2.Cross(d, cur.P - prev.P)) / d.Length > Math.Max(tol, 1e-9)) continue;
                v.RemoveAt(i--);
                changed = true;
            }
        }
    }

    static void AddCoedge(ProfileLoop loop, NCoedge c, FaceMap m, double sampleTol, double tol)
    {
        var name = Other(c)?.Tag.Name ?? c.Face.Tag.Name;
        if (c.Edge.Curve is NLine) { loop.Verts.Add(new ProfileVertex(m.Map(c.Start.P), name)); return; }
        var pts = c.Points(sampleTol).Select(m.Map).ToList();
        pts.RemoveAt(pts.Count - 1); // the next coedge starts there
        if (c.Edge.Curve is NCircle ci && m is WallMap)
        {
            // rigid map: still a circle
            var arc = new ArcInfo(m.Map(ci.C), ci.R, SurfaceDef.SegmentsFor(ci.R));
            foreach (var p in pts) loop.Verts.Add(new ProfileVertex(p, name, arc));
            return;
        }
        // an unrolled curve: the end of a bend is straight in the flat
        var end = m.Map(c.End.P);
        var kept = new List<Vec2> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var nx = i + 1 < pts.Count ? pts[i + 1] : end;
            var a = kept[^1]; var d = nx - a;
            var dev = d.Length < 1e-15 ? 0 : Math.Abs(Vec2.Cross(d, pts[i] - a)) / d.Length;
            if (dev > Math.Max(tol, 1e-9)) kept.Add(pts[i]);
        }
        foreach (var p in kept) loop.Verts.Add(new ProfileVertex(p, name));
    }

    // ---- helpers ----

    static NFace? Other(NCoedge c)
    {
        foreach (var u in c.Edge.Uses)
            if (!ReferenceEquals(u, c)) return ReferenceEquals(u.Face, c.Face) ? null : u.Face;
        return null;
    }

    static Vec3 Outward(NFace f, Vec3 p) { var n = f.Surface.Normal(p); return f.Sense ? n : -n; }

    /// <summary>The sheet thickness: the distance to the nearest face facing away from the fixed face behind it.</summary>
    static double Thickness(NBody body, NFace f0, Vec3 n0, Vec3 o0, double tol)
    {
        double t = double.MaxValue;
        foreach (var f in body.Faces)
        {
            if (f.Surface.Kind != SurfaceKind.Plane || ReferenceEquals(f, f0)) continue;
            var p = f.Coedges.First().Edge.V0.P;
            if (Vec3.Dot(Outward(f, p), n0) > -1 + 1e-9) continue;
            var d = Vec3.Dot(o0 - p, n0);
            if (d > tol) t = Math.Min(t, d);
        }
        if (t == double.MaxValue) throw new UnfoldException("The fixed face has no opposite face: the body is not a sheet.");
        return t;
    }

    /// <summary>A wall has an antiparallel plane at distance t behind it; a bend a coaxial cylinder t from it.</summary>
    static bool HasOpposite(NBody body, NFace f, double t, double tol)
    {
        var s = f.Surface;
        var p = f.Coedges.First().Edge.V0.P;
        var n = Outward(f, p);
        foreach (var g in body.Faces)
        {
            var gs = g.Surface;
            if (gs.Kind != s.Kind || ReferenceEquals(g, f)) continue;
            if (s.Kind == SurfaceKind.Plane)
            {
                var q = g.Coedges.First().Edge.V0.P;
                if (Vec3.Dot(Outward(g, q), n) < -1 + 1e-9 && Math.Abs(Vec3.Dot(p - q, n) - t) < 1e3 * tol) return true;
            }
            else
            {
                var a = s.Axis.Normalized;
                if (Vec3.Cross(a, gs.Axis.Normalized).Length > 1e-9) continue;
                var d = gs.Origin - s.Origin;
                if ((d - a * Vec3.Dot(d, a)).Length < 1e3 * tol && Math.Abs(Math.Abs(gs.R - s.R) - t) < 1e3 * tol) return true;
            }
        }
        return false;
    }

    /// <summary>Area of a planar face (loops in its plane frame; holes run the other way and subtract).</summary>
    static double PlanarArea(NFace f)
    {
        var fr = f.Surface.F;
        double a = 0;
        foreach (var l in f.Loops)
        {
            var pts = l.Coedges.SelectMany(c => { var s = c.Points(1e-2); return s.Take(s.Count - 1); }).Select(fr.ToLocal).ToList();
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++) a += pts[j].X * pts[i].Y - pts[i].X * pts[j].Y;
        }
        return Math.Abs(a) / 2;
    }
}

/// <summary>2D profiles of sheet metal.</summary>
public static class SheetProfile
{
    /// <summary>
    /// The cross section of a sheet along an open chain of lines: material of thickness t on the left of the chain,
    /// every corner bent with inner radius r. The chain side faces are named A0.., the other side B0..,
    /// the bends BendA/BendB i (at chain vertex i), the ends E0 and E1.
    /// </summary>
    public static ProfileLoop Thicken(IReadOnlyList<Vec2> chain, double t, double r)
    {
        int n = chain.Count;
        if (n < 2) throw new ArgumentException("A chain needs two points.");
        var dir = new Vec2[n - 1]; var nrm = new Vec2[n - 1];
        for (int i = 0; i < n - 1; i++) { var d = chain[i + 1] - chain[i]; dir[i] = d * (1 / d.Length); nrm[i] = dir[i].Perp; }

        // per corner: the bend center, the turn side (+1 left) and the tangent points on both sides
        var corners = new List<(Vec2 c, int s, double rc, double ro, Vec2 t1, Vec2 t2, Vec2 u1, Vec2 u2)>();
        for (int i = 1; i < n - 1; i++)
        {
            var s = Vec2.Cross(dir[i - 1], dir[i]) > 0 ? 1 : -1;
            var rc = s > 0 ? r + t : r;   // radius on the chain side
            var ro = rc - s * t;          // on the offset side
            // center: on both chain lines offset by s·rc
            var p = chain[i - 1] + nrm[i - 1] * (s * rc); var q = chain[i] + nrm[i] * (s * rc);
            var den = Vec2.Cross(dir[i - 1], dir[i]);
            var c = p + dir[i - 1] * (Vec2.Cross(q - p, dir[i]) / den);
            corners.Add((c, s, rc, ro, c - nrm[i - 1] * (s * rc), c - nrm[i] * (s * rc), c - nrm[i - 1] * (s * ro), c - nrm[i] * (s * ro)));
        }

        var loop = new ProfileLoop();
        // chain side, forward
        loop.Verts.Add(new ProfileVertex(chain[0], "A0"));
        for (int i = 0; i < corners.Count; i++)
        {
            var k = corners[i];
            loop.AddArc(k.c, k.rc, (k.t1 - k.c).Angle, (k.t2 - k.c).Angle, k.s > 0, "BendA" + (i + 1));
            loop.Verts.Add(new ProfileVertex(k.t2, "A" + (i + 1)));
        }
        loop.Verts.Add(new ProfileVertex(chain[n - 1], "E1"));
        // offset side, backward
        loop.Verts.Add(new ProfileVertex(chain[n - 1] + nrm[n - 2] * t, "B" + (n - 2)));
        for (int i = corners.Count - 1; i >= 0; i--)
        {
            var k = corners[i];
            loop.AddArc(k.c, k.ro, (k.u2 - k.c).Angle, (k.u1 - k.c).Angle, k.s < 0, "BendB" + (i + 1));
            loop.Verts.Add(new ProfileVertex(k.u1, "B" + i));
        }
        loop.Verts.Add(new ProfileVertex(chain[0] + nrm[0] * t, "E0"));
        return loop;
    }
}

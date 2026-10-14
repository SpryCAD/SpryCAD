using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

/// <summary>
/// A plane from up to three references and how it relates to each, as SolidWorks' plane: parallel to a face and
/// through a point, through a line at an angle to a plane, tangent to a cylinder, normal to a curve at its end,
/// midway between two faces, through three points, and so on. A plane has three freedoms (two of direction, one of
/// position); each relation takes some, and the plane is fully defined when they are all taken.
/// </summary>
public static class PlaneSolver
{
    /// <summary>What the reference is, which decides the relations it offers.</summary>
    public enum RefKind { None, Plane, Line, Round, Curve, Point }

    public static RefKind KindOf(PlaneReference r) =>
        r.Plane != null ? RefKind.Plane : r.Round != null ? RefKind.Round : r.Line != null ? RefKind.Line
        : r.Curve != null ? RefKind.Curve : r.Point != null ? RefKind.Point : RefKind.None;

    /// <summary>The relations a reference offers, the usual one first.</summary>
    public static IReadOnlyList<PlaneRelation> Offered(PlaneReference r) => KindOf(r) switch
    {
        RefKind.Plane => [PlaneRelation.Offset, PlaneRelation.Parallel, PlaneRelation.Perpendicular, PlaneRelation.Coincident, PlaneRelation.Angle, PlaneRelation.MidPlane],
        RefKind.Line => r.Curve != null
            ? [PlaneRelation.Perpendicular, PlaneRelation.Coincident, PlaneRelation.Parallel]
            : [PlaneRelation.Coincident, PlaneRelation.Perpendicular, PlaneRelation.Parallel],
        RefKind.Round => [PlaneRelation.Tangent, PlaneRelation.Coincident, PlaneRelation.Perpendicular, PlaneRelation.Parallel],
        RefKind.Curve => [PlaneRelation.Perpendicular],
        RefKind.Point => [PlaneRelation.Coincident],
        _ => [],
    };

    /// <summary>
    /// The relation a reference takes when it is picked, given those picked before it (whose relations may change
    /// too, so the usual plane comes out at once): a face alone is offset from; then a point makes it parallel
    /// through the point, a line makes it perpendicular through the line; a line or curve alone is perpendicular at
    /// its end.
    /// </summary>
    public static void Suggest(IList<PlaneReference> refs, PlaneReference added)
    {
        var kind = KindOf(added);
        var others = refs.Where(r => r != added).ToList();
        added.Relation = Offered(added).FirstOrDefault();
        if (others.Count == 0) return;
        var planes = others.Where(o => KindOf(o) == RefKind.Plane).ToList();
        switch (kind)
        {
            case RefKind.Plane:
                if (planes.Count == 1 && others.Count == 1 && planes[0].Relation is PlaneRelation.Offset or PlaneRelation.MidPlane)
                { planes[0].Relation = PlaneRelation.MidPlane; added.Relation = PlaneRelation.MidPlane; }
                else if (others.Any(o => KindOf(o) is RefKind.Line or RefKind.Round && o.Relation == PlaneRelation.Coincident)) added.Relation = PlaneRelation.Perpendicular;
                else if (others.Any(o => KindOf(o) == RefKind.Round && o.Relation == PlaneRelation.Tangent)) added.Relation = PlaneRelation.Parallel;
                else if (others.Any(o => KindOf(o) == RefKind.Point)) added.Relation = PlaneRelation.Parallel;
                break;
            case RefKind.Point:
                foreach (var p in planes.Where(p => p.Relation == PlaneRelation.Offset)) p.Relation = PlaneRelation.Parallel;
                break;
            case RefKind.Line or RefKind.Round:
                if (planes.Count > 0)
                {
                    added.Relation = PlaneRelation.Coincident;
                    foreach (var p in planes.Where(p => p.Relation is PlaneRelation.Offset or PlaneRelation.Parallel)) p.Relation = PlaneRelation.Perpendicular;
                }
                else if (others.Any(o => KindOf(o) == RefKind.Point)) added.Relation = kind == RefKind.Round ? PlaneRelation.Tangent : PlaneRelation.Perpendicular;
                else if (others.Any(o => KindOf(o) is RefKind.Line or RefKind.Round && o.Relation == PlaneRelation.Perpendicular))
                {
                    // two lines: the plane through both
                    foreach (var o in others.Where(o => KindOf(o) is RefKind.Line or RefKind.Round)) o.Relation = PlaneRelation.Coincident;
                    added.Relation = PlaneRelation.Coincident;
                }
                break;
        }
    }

    /// <summary>How much of the plane the references define: below, at or above its three freedoms.</summary>
    public enum Definition { Under, Full, Over }

    /// <summary>Freedoms taken, counted in halves (a mid plane takes one and a half): six define the plane.</summary>
    static int Taken(IReadOnlyList<PlaneReference> refs, out bool atEnd)
    {
        int n = 0, along = 0; bool placed = false, perpCurve = false, normalFixed = false;
        foreach (var r in refs)
        {
            var (k, rel) = (KindOf(r), r.Relation);
            switch (k)
            {
                case RefKind.Plane:
                    n += rel switch { PlaneRelation.Coincident or PlaneRelation.Offset => 6, PlaneRelation.Parallel => 4, PlaneRelation.MidPlane => 3, _ => 2 };
                    placed |= rel is PlaneRelation.Coincident or PlaneRelation.Offset or PlaneRelation.MidPlane;
                    normalFixed |= rel is PlaneRelation.Coincident or PlaneRelation.Offset or PlaneRelation.Parallel;
                    if (rel == PlaneRelation.Perpendicular) along++;
                    break;
                case RefKind.Line or RefKind.Round:
                    n += rel == PlaneRelation.Parallel ? 2 : 4;
                    placed |= rel is PlaneRelation.Coincident or PlaneRelation.Tangent;
                    perpCurve |= rel == PlaneRelation.Perpendicular && r.Curve != null;
                    normalFixed |= rel == PlaneRelation.Perpendicular;
                    if (rel != PlaneRelation.Perpendicular) along++;
                    break;
                case RefKind.Curve: n += 4; perpCurve = true; normalFixed = true; break;
                case RefKind.Point: n += 2; placed = true; break;
            }
        }
        // a direction the plane runs along takes nothing more once its normal is set (it has to agree with it):
        // tangent to a cylinder and parallel to a face, through an edge and parallel to a face
        if (normalFixed) n -= 2 * along;
        // perpendicular to a line or curve with nothing else placing the plane: at its end
        atEnd = perpCurve && !placed && n < 6;
        return atEnd ? n + 2 : n;
    }

    public static Definition Defined(IReadOnlyList<PlaneReference> refs)
    {
        var n = Taken(refs, out _);
        return n < 6 ? Definition.Under : n == 6 ? Definition.Full : Definition.Over;
    }

    /// <summary>The plane the references define, or why they do not (a <see cref="FeatureException"/>).</summary>
    public static Frame Solve(IReadOnlyList<PlaneReference> refs, ModelState st, Func<string, string, double> length, Func<string, string, double> angle)
    {
        refs = refs.Where(r => KindOf(r) != RefKind.None).ToList();
        if (refs.Count == 0) throw new FeatureException("Select the references of the plane: a face or plane, an edge or axis, a curve, or a point.");
        var taken = Taken(refs, out var atEnd);
        if (taken < 6) throw new FeatureException("Under defined: " + UnderHint(refs));
        if (taken > 6) throw new FeatureException("Over defined: take a relation off, or a reference.");

        // what the references are, resolved
        var planes = new List<(PlaneReference r, Frame f)>();
        var lines = new List<(PlaneReference r, Vec3 o, Vec3 d)>();   // Coincident / Parallel / Perpendicular to a line (or a round face's axis)
        var rounds = new List<(PlaneReference r, Vec3 o, Vec3 d, double rad)>(); // Tangent
        var points = new List<Vec3>();
        (Vec3 p, Vec3 t)? normalTo = null; // perpendicular to a curve, at a point on it
        PlaneReference? curveRef = null;
        foreach (var r in refs)
            switch (KindOf(r))
            {
                case RefKind.Plane: planes.Add((r, Resolver.ResolvePlane(r.Plane!, st))); break;
                case RefKind.Round when r.Relation == PlaneRelation.Tangent:
                    {
                        var (o, d, rad) = ResolveRound(r.Round!, st);
                        rounds.Add((r, o, d, rad));
                        break;
                    }
                case RefKind.Line or RefKind.Round when r.Relation == PlaneRelation.Perpendicular && r.Curve != null:
                case RefKind.Curve:
                    curveRef = r;
                    break;
                case RefKind.Line or RefKind.Round:
                    {
                        var (o, d) = Resolver.ResolveDir(r.Line!, st);
                        lines.Add((r, o, d.Normalized));
                        break;
                    }
                case RefKind.Point: points.Add(Resolver.ResolvePoint(r.Point!, st)); break;
            }
        if (curveRef != null)
        {
            // at a picked point, else at the end of the curve nearest where it was picked
            var at = points.Count > 0 ? points[0] : atEnd ? NearestEnd(curveRef, st) : curveRef.Hint;
            var (p, t) = Resolver.CurveTangent(curveRef.Curve!, at, st);
            normalTo = (points.Count > 0 ? points[0] : p, t.Normalized);
        }

        Frame? basis = null;    // a plane whose in-plane axes the result keeps
        Vec3? n = null;         // the normal
        Vec3? through = null;   // a point the plane passes through

        // --- at an angle to a plane, about a line in it
        if (planes.FirstOrDefault(p => p.r.Relation == PlaneRelation.Angle) is { r: not null } ang)
        {
            if (lines.FirstOrDefault(l => l.r.Relation == PlaneRelation.Coincident) is not { r: not null } axis)
                throw new FeatureException("At an angle to a plane: also pick the line (an edge, sketch line or axis) it turns about.");
            var deg = angle(ang.r.Value, "Angle") * (ang.r.Flip ? -1 : 1);
            var rot = Transform.Rotation(axis.o, axis.d, deg * Math.PI / 180);
            var f = ang.f;
            // the plane through the line first: the base plane moved to contain it
            f = f.Offset(Vec3.Dot(axis.o - f.Origin, f.Normal));
            return new Frame(rot.Point(f.Origin), rot.Vector(f.XDir), rot.Vector(f.YDir), rot.Vector(f.Normal));
        }

        // --- midway between two planes
        var mids = planes.Where(p => p.r.Relation == PlaneRelation.MidPlane).ToList();
        if (mids.Count == 2)
        {
            var (a, b) = (mids[0].f, mids[1].f);
            double da = Vec3.Dot(a.Normal, a.Origin), db = Vec3.Dot(b.Normal, b.Origin);
            var dot = Vec3.Dot(a.Normal, b.Normal);
            // facing the same way: their average; otherwise the plane between them (where n1·x - d1 = n2·x - d2)
            var (nn, dd) = dot > 0.999 ? (a.Normal + b.Normal, da + db) : (a.Normal - b.Normal, da - db);
            var len = nn.Length;
            if (len < 1e-9) throw new FeatureException("The two planes are the same plane.");
            n = nn / len;
            through = n.Value * (dd / len);
            if (Math.Abs(Math.Abs(dot) - 1) < 1e-9) basis = a;
        }
        else if (mids.Count == 1) throw new FeatureException("Mid plane: pick the second face or plane, also Mid Plane.");

        // --- the normal fixed by one reference
        if (n == null)
        {
            if (planes.FirstOrDefault(p => p.r.Relation is PlaneRelation.Coincident or PlaneRelation.Offset or PlaneRelation.Parallel) is { r: not null } par)
            {
                basis = par.f; n = par.f.Normal;
                if (par.r.Relation == PlaneRelation.Coincident) through = par.f.Origin;
                if (par.r.Relation == PlaneRelation.Offset) through = par.f.Origin + par.f.Normal * length(par.r.Value, "Offset") * (par.r.Flip ? -1 : 1);
            }
            else if (lines.FirstOrDefault(l => l.r.Relation == PlaneRelation.Perpendicular) is { r: not null } perp) n = perp.d;
            else if (normalTo is { } nt) { n = nt.t; through = nt.p; }
        }
        if (normalTo is { } nt2 && through == null) through = nt2.p;

        // --- else from the directions in it: the lines it contains or runs along, the planes it is perpendicular to
        var inPlane = new List<Vec3>();
        foreach (var l in lines.Where(l => l.r.Relation is PlaneRelation.Coincident or PlaneRelation.Parallel)) inPlane.Add(l.d);
        foreach (var p in planes.Where(p => p.r.Relation == PlaneRelation.Perpendicular)) inPlane.Add(p.f.Normal);
        foreach (var ro in rounds) inPlane.Add(ro.d);
        // the points it passes through: picked, and on the lines it contains
        var on = new List<Vec3>(points);
        foreach (var l in lines.Where(l => l.r.Relation == PlaneRelation.Coincident)) on.Add(l.o);

        if (n != null && inPlane.Any(v => Math.Abs(Vec3.Dot(v.Normalized, n.Value)) > 1e-6))
            throw new FeatureException("The references disagree: a line or axis the plane runs along is not square to its normal.");
        if (n == null)
        {
            if (rounds.Count > 1) throw new FeatureException("Tangent to two round faces is not supported: pick one, and a point or a plane.");
            if (rounds.Count == 1 && inPlane.Count(v => Vec3.Cross(v, rounds[0].d).Length > 1e-6) == 0 && on.Count > 0)
            {
                // tangent to a cylinder through a point (or a line along its axis): one of the two tangents, on the side picked
                var ro = rounds[0];
                var w = on[0] - ro.o; w -= ro.d * Vec3.Dot(w, ro.d);
                var dist = w.Length;
                if (dist < ro.rad - 1e-9) throw new FeatureException("The point is inside the round face: no plane through it is tangent.");
                var phi = Math.Acos(Math.Clamp(ro.rad / dist, -1, 1));
                var side = Side(ro.r, ro.o, ro.d, w / dist);
                n = Transform.Rotation(Vec3.Zero, ro.d, side * phi).Vector(w / dist);
                through = on[0];
            }
            else
            {
                var dirs = Independent(inPlane);
                if (dirs.Count >= 2) n = Vec3.Cross(dirs[0], dirs[1]);
                else if (dirs.Count == 1)
                {
                    // one direction in it and two points on it (or a line and a point)
                    var across = on.Skip(1).Select(p => p - on[0]).FirstOrDefault(v => Vec3.Cross(dirs[0], v).Length > 1e-9);
                    if (across == default) throw new FeatureException("The references do not decide the plane: they line up.");
                    n = Vec3.Cross(dirs[0], across);
                }
                else if (on.Count >= 3)
                {
                    n = Vec3.Cross(on[1] - on[0], on[2] - on[0]);
                    if (n.Value.Length < 1e-9) throw new FeatureException("The three points are on a line.");
                }
                else throw new FeatureException("The references do not decide the plane's direction.");
                if (n.Value.Length < 1e-12) throw new FeatureException("The references do not decide the plane: they are parallel.");
                n = n.Value.Normalized;
            }
        }

        // --- where it is: tangent to the round face, else through a point it contains
        if (through == null && rounds.Count == 1)
        {
            var ro = rounds[0];
            var nn = n.Value - ro.d * Vec3.Dot(n.Value, ro.d);
            if (nn.Length < 1e-9) throw new FeatureException("A plane square to the round face's axis cannot be tangent to it.");
            nn = nn.Normalized;
            through = ro.o + nn * ro.rad * Side(ro.r, ro.o, ro.d, nn);
        }
        through ??= on.Count > 0 ? on[0] : null;
        if (through == null) throw new FeatureException("Under defined: " + UnderHint(refs));

        return Build(n.Value, through.Value, basis);
    }

    /// <summary>The side of a round face a tangent plane is on: where it was picked (+1 along <paramref name="toward"/>), or the other side when flipped.</summary>
    static int Side(PlaneReference r, Vec3 o, Vec3 axis, Vec3 toward)
    {
        var h = r.Hint - o; h -= axis * Vec3.Dot(h, axis);
        var s = h.Length < 1e-9 || Vec3.Dot(h, toward) >= 0 ? 1 : -1;
        return r.Flip ? -s : s;
    }

    static List<Vec3> Independent(List<Vec3> vs)
    {
        var res = new List<Vec3>();
        foreach (var v in vs.Select(v => v.Normalized))
        {
            if (v.Length < 0.5) continue;
            if (res.Count == 0 || res.Count == 1 && Vec3.Cross(res[0], v).Length > 1e-6) res.Add(v);
        }
        return res;
    }

    /// <summary>The plane with normal <paramref name="n"/> through <paramref name="p"/>: with the axes of <paramref name="basis"/> when parallel to it, else horizontal X (Y up, unless the plane is flat).</summary>
    static Frame Build(Vec3 n, Vec3 p, Frame? basis)
    {
        if (basis is { } b && Vec3.Cross(b.Normal, n).Length < 1e-9)
        {
            var moved = b.Offset(Vec3.Dot(p - b.Origin, b.Normal));
            return Vec3.Dot(b.Normal, n) > 0 ? moved : moved.Flipped();
        }
        // centred on the point it passes through, so it sits on the references
        var up = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitY;
        var x = Vec3.Cross(up, n).Normalized;
        return new Frame(p, x, Vec3.Cross(n, x), n);
    }

    static string UnderHint(IReadOnlyList<PlaneReference> refs)
    {
        if (refs.Count == 1)
            return KindOf(refs[0]) switch
            {
                RefKind.Plane => refs[0].Relation switch
                {
                    PlaneRelation.Parallel => "also pick a point it passes through, or choose Offset.",
                    PlaneRelation.Perpendicular => "also pick the line or point it passes through.",
                    PlaneRelation.MidPlane => "pick the second face or plane, also Mid Plane.",
                    _ => "also pick the line (an edge, sketch line or axis) it turns about.",
                },
                RefKind.Point => "also pick a face or plane it is parallel to, or a line it is perpendicular to.",
                RefKind.Round => "also pick a point it passes through, or a face or plane.",
                _ => "also pick a point, a face or another line.",
            };
        return "pick another reference (a face, edge, axis or point), or choose another relation.";
    }

    /// <summary>The end of a curve reference nearest where it was picked (a closed curve: the point on it there).</summary>
    static Vec3 NearestEnd(PlaneReference r, ModelState st)
    {
        var c = r.Curve!;
        (Vec3 a, Vec3 b)? ends = null;
        if (c.Sketch != null && st.Sketches.TryGetValue(c.Sketch, out var sr))
        {
            var sk = sr.Sketch;
            ends = sk.Entity(c.SketchEntity) switch
            {
                Sketching.SkLine l => (sr.Frame.ToWorld(sk.P(l.P1)), sr.Frame.ToWorld(sk.P(l.P2))),
                Sketching.SkArc arc => (sr.Frame.ToWorld(sk.P(arc.Start)), sr.Frame.ToWorld(sk.P(arc.End))),
                _ => null,
            };
        }
        else if (c.Edge is { } e)
        {
            if (Resolver.ResolveNativeEdge(e, st) is { } ne) { if (!ReferenceEquals(ne.edge.V0, ne.edge.V1)) ends = (ne.edge.V0.P, ne.edge.V1.P); }
            else { var (_, te) = Resolver.ResolveEdge(e, st); if (!te.Closed) ends = (te.Start, te.End); }
        }
        if (ends is not { } en) return r.Hint;
        return Vec3.Distance(en.a, r.Hint) <= Vec3.Distance(en.b, r.Hint) ? en.a : en.b;
    }

    /// <summary>The axis and radius of a cylindrical face.</summary>
    static (Vec3 o, Vec3 d, double r) ResolveRound(FaceRef f, ModelState st)
    {
        var s = Resolver.NativeFace(f, st) is { } hit ? hit.face.Tag.Surface : Resolver.ResolveFace(f, st).face.Tag.Surface;
        if (s.Kind != SurfaceKind.Cylinder) throw new FeatureException("Tangent: the round face must be a cylinder.");
        return (s.Origin, s.Axis.Normalized, s.Radius);
    }
}

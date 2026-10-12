using SpryCad.Core.Sketching;
using SpryCad.Kernel;
using SpryCad.Kernel.Native;

namespace SpryCad.Core.Regen;

/// <summary>
/// The path of a sweep from its sketch: the sketch's lines and arcs (construction geometry left out) chained end to
/// end, or one circle; in world space, starting where the profile's plane meets it and running away from it.
/// </summary>
static class SweepPath
{
    // an arc turns Angle (signed: + counter-clockwise) about Center; a spline runs along Curve (the sketch's x–y) from A at 0 to B at 1
    sealed record Piece(Vec2 A, Vec2 B, Vec2? Center, double Angle, BSplineCurve? Curve = null);

    /// <summary>
    /// The path segments, starting on <paramref name="plane"/> (the profile's) and leaving it along its normal or
    /// against it; throws a <see cref="FeatureException"/> saying what is wrong with the path.
    /// </summary>
    public static List<NSweep.Seg> Build(SketchResult path, Plane plane, Vec3 profileCentre)
    {
        var sk = path.Sketch; var fr = path.Frame;
        var ents = sk.Entities.Where(e => !e.Construction).ToList();
        var circles = ents.OfType<SkCircle>().ToList();
        var pieces = Pieces(sk, ents);
        if (circles.Count > 0 && (circles.Count > 1 || pieces.Count > 0))
            throw new FeatureException("The path must be one chain of lines, arcs and splines, or one circle.");
        if (circles.Count == 1)
        {
            // a circle: once round, from where the profile's plane cuts it
            var ci = circles[0];
            var c = fr.ToWorld(sk.P(ci.Center)); var r = ci.R;
            var axis = fr.Normal;
            // the circle's points in the profile's plane: along the plane's trace in the circle's plane
            var dir = Vec3.Cross(axis, plane.Normal);
            if (dir.Length < 1e-9) throw new FeatureException("The profile's sketch must cross the path circle.");
            var radial = Vec3.Cross(dir.Normalized, axis);
            if (Math.Abs(plane.Distance(c)) > 1e-6 * Math.Max(1, r)) throw new FeatureException("The profile's sketch plane must pass through the path circle's centre.");
            var p = Vec3.Dot(radial, profileCentre - c) >= 0 ? c + radial * r : c - radial * r;
            // turn so the profile leaves along its plane's normal
            if (Vec3.Dot(Vec3.Cross(axis, p - c), plane.Normal) < 0) axis = -axis;
            return new List<NSweep.Seg> { new(p, p, c, axis, 2 * Math.PI) };
        }
        if (pieces.Count == 0) throw new FeatureException("The path sketch has no lines, arcs or splines.");
        var scale = Math.Max(1, pieces.Max(x => Math.Max(x.A.Length, x.B.Length)));
        var tol = 1e-7 * scale;
        var order = Chain(pieces, tol, "path");
        // (one spline closing on itself is a closed path too)
        bool closed = order.Count > 1 && Vec2.Distance(End(order[^1]), Start(order[0])) < tol
            || order.Count == 1 && order[0].p.Curve != null && Vec2.Distance(order[0].p.A, order[0].p.B) < tol;
        // the start: an end of the path on the profile's plane (open), or a vertex on it (closed)
        double Dist(Vec2 q) => Math.Abs(plane.Distance(fr.ToWorld(q)));
        var ptol = 1e-6 * scale;
        if (!closed)
        {
            if (Dist(Start(order[0])) > ptol)
            {
                if (Dist(End(order[^1])) > ptol) throw new FeatureException("The path must start on the profile's sketch plane (at one of its ends).");
                order.Reverse();
                for (int i = 0; i < order.Count; i++) order[i] = (order[i].p, !order[i].fwd);
            }
        }
        else
        {
            // the vertex on the profile's plane nearest the profile
            var onPlane = Enumerable.Range(0, order.Count).Where(i => Dist(Start(order[i])) < ptol).ToList();
            if (onPlane.Count == 0) throw new FeatureException("The closed path must have a vertex on the profile's sketch plane.");
            var k = onPlane.MinBy(i => Vec3.Distance(fr.ToWorld(Start(order[i])), profileCentre));
            order = order.Skip(k).Concat(order.Take(k)).ToList();
        }
        var segs = ToWorld(order, fr);
        // a closed path walked the other way round leaves the profile against its normal: walk it forwards
        if (closed && Vec3.Dot(segs[0].StartTangent, plane.Normal) < 0)
        {
            var back = new List<NSweep.Seg>();
            for (int i = segs.Count - 1; i >= 0; i--) back.Add(segs[i].Reversed());
            segs = back;
        }
        return segs;
    }

    static Vec2 Start((Piece p, bool fwd) x) => x.fwd ? x.p.A : x.p.B;
    static Vec2 End((Piece p, bool fwd) x) => x.fwd ? x.p.B : x.p.A;

    /// <summary>The sketch's lines and arcs (of the entities given).</summary>
    static List<Piece> Pieces(Sketch sk, IEnumerable<SkEntity> ents)
    {
        var pieces = new List<Piece>();
        foreach (var e in ents)
        {
            switch (e)
            {
                case SkLine l: pieces.Add(new Piece(sk.P(l.P1), sk.P(l.P2), null, 0)); break;
                case SkSpline sp when sk.SplineCurve(sp) is { } curve:
                    {
                        var a = curve.Eval(0); var b = curve.Eval(1);
                        pieces.Add(new Piece(new Vec2(a.X, a.Y), new Vec2(b.X, b.Y), null, 0, curve));
                        break;
                    }
                case SkArc a:
                    {
                        var c = sk.P(a.Center); var s = sk.P(a.Start); var t = sk.P(a.End);
                        var ang = (t - c).Angle - (s - c).Angle;
                        while (ang <= 1e-12) ang += 2 * Math.PI;
                        pieces.Add(new Piece(s, t, c, ang));
                        break;
                    }
            }
        }
        return pieces;
    }

    /// <summary>The pieces end to end (from a free end if there is one); throws if they branch or are in pieces.</summary>
    static List<(Piece p, bool fwd)> Chain(List<Piece> pieces, double tol, string what)
    {
        var order = new List<(Piece p, bool fwd)>();
        var used = new bool[pieces.Count];
        int Count(Vec2 q) => pieces.Count(x => Vec2.Distance(x.A, q) < tol) + pieces.Count(x => Vec2.Distance(x.B, q) < tol);
        foreach (var x in pieces)
            if (Count(x.A) > 2 || Count(x.B) > 2) throw new FeatureException($"The {what} branches: it must be a single chain of lines and arcs.");
        // start at a free end if there is one (an open path), else anywhere (closed)
        int first = Enumerable.Range(0, pieces.Count).FirstOrDefault(i => Count(pieces[i].A) == 1 || Count(pieces[i].B) == 1, 0);
        bool firstFwd = Count(pieces[first].A) == 1 || Count(pieces[first].B) != 1;
        order.Add((pieces[first], firstFwd)); used[first] = true;
        while (true)
        {
            var (lp, lf) = order[^1];
            var end = lf ? lp.B : lp.A;
            int next = -1; bool nf = true;
            for (int i = 0; i < pieces.Count && next < 0; i++)
            {
                if (used[i]) continue;
                if (Vec2.Distance(pieces[i].A, end) < tol) { next = i; nf = true; }
                else if (Vec2.Distance(pieces[i].B, end) < tol) { next = i; nf = false; }
            }
            if (next < 0) break;
            order.Add((pieces[next], nf)); used[next] = true;
        }
        if (used.Any(u => !u)) throw new FeatureException($"The {what} is in pieces: it must be one connected chain of lines and arcs.");
        return order;
    }

    /// <summary>The chained pieces in world space.</summary>
    static List<NSweep.Seg> ToWorld(List<(Piece p, bool fwd)> order, Frame fr)
    {
        var segs = new List<NSweep.Seg>();
        foreach (var (p, fwd) in order)
        {
            var a = fr.ToWorld(fwd ? p.A : p.B); var b = fr.ToWorld(fwd ? p.B : p.A);
            if (p.Curve is { } curve)
            {
                var world = curve with { Poles = curve.Poles.Select(q => fr.ToWorld(q.X, q.Y, 0)).ToArray() };
                segs.Add(new NSweep.Seg(a, b, Curve: fwd ? world : NSweep.Seg.Reverse(world)));
                continue;
            }
            if (p.Center is not { } c) { segs.Add(new NSweep.Seg(a, b)); continue; }
            // counter-clockwise in the sketch is about its normal; walked backwards, about the reversed normal
            var axis = fwd ? fr.Normal : -fr.Normal;
            segs.Add(new NSweep.Seg(a, b, fr.ToWorld(c), axis, p.Angle));
        }
        return segs;
    }

    /// <summary>
    /// A loft's guide curves in a sketch: each connected chain of lines and arcs (construction geometry left out) is
    /// one guide, in world space; throws if a chain branches or closes on itself.
    /// </summary>
    public static List<List<NSweep.Seg>> Guides(SketchResult sketch, bool closed = false)
    {
        var sk = sketch.Sketch;
        var ents = sk.Entities.Where(e => !e.Construction && !e.Dangling).ToList();
        if (ents.OfType<SkCircle>().Any()) throw new FeatureException("A guide curve cannot be a circle: draw it as arcs (a chain of lines and arcs).");
        var pieces = Pieces(sk, ents);
        if (pieces.Count == 0) throw new FeatureException("The guide sketch has no lines, arcs or splines.");
        var scale = Math.Max(1, pieces.Max(x => Math.Max(x.A.Length, x.B.Length)));
        var tol = 1e-7 * scale;
        // connected components
        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
            {
                var (a, b) = (pieces[i], pieces[j]);
                if (Vec2.Distance(a.A, b.A) < tol || Vec2.Distance(a.A, b.B) < tol || Vec2.Distance(a.B, b.A) < tol || Vec2.Distance(a.B, b.B) < tol)
                    parent[Find(i)] = Find(j);
            }
        var res = new List<List<NSweep.Seg>>();
        foreach (var comp in Enumerable.Range(0, pieces.Count).GroupBy(Find))
        {
            var order = Chain(comp.Select(i => pieces[i]).ToList(), tol, "guide curve");
            bool ring = (order.Count > 1 || order[0].p.Curve != null) && Vec2.Distance(End(order[^1]), Start(order[0])) < tol;
            if (ring != closed)
                throw new FeatureException(closed ? "On a closed loft, guide curves and the centerline must be closed too." : "A guide curve or centerline cannot be closed unless the loft is: it runs from the first profile to the last.");
            res.Add(ToWorld(order, sketch.Frame));
        }
        return res;
    }
}

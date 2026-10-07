namespace FeatherCad.Kernel.Native;

/// <summary>
/// Exact sweep of planar profile regions along a tangent-continuous path of lines and circular arcs. Along a
/// line the profile is extruded, around an arc it is revolved about the arc's axis (which lies in the profile's
/// plane, the profile being perpendicular to the path): every face is a plane, cylinder, cone, sphere or torus.
/// The pieces are stitched where they meet (the end cap of one is the start cap of the next: both go, their edges
/// become one), no booleans. The profile keeps its orientation relative to the path (no twist).
/// </summary>
public static class NSweep
{
    /// <summary>A path segment: a line from A to B, or an arc from A about Center, turning Angle (radians, positive) about Axis.</summary>
    public sealed record Seg(Vec3 A, Vec3 B, Vec3? Center = null, Vec3? Axis = null, double Angle = 0)
    {
        public bool IsArc => Center != null;
        /// <summary>Unit tangent at the start (direction of travel).</summary>
        public Vec3 StartTangent => IsArc ? Vec3.Cross(Axis!.Value, A - Center!.Value).Normalized : (B - A).Normalized;
        /// <summary>Unit tangent at the end.</summary>
        public Vec3 EndTangent => IsArc ? Vec3.Cross(Axis!.Value, B - Center!.Value).Normalized : (B - A).Normalized;
        /// <summary>The motion taking the start of the segment (and the profile there) to its end.</summary>
        public Transform Motion => IsArc ? Transform.Rotation(Center!.Value, Axis!.Value, Angle) : Transform.Translation(B - A);
        public double Length => IsArc ? Angle * (A - Center!.Value).Length : (B - A).Length;
    }

    /// <summary>
    /// Sweeps the regions (in <paramref name="frame"/>'s plane, whose normal is the path's start tangent and which
    /// contains the path's start point) along the path. Faces are named prefix.k.… for segment k; the caps
    /// prefix.Start and prefix.End (none on a closed path).
    /// </summary>
    public static NBody Sweep(IReadOnlyList<ProfileRegion> regions, Frame frame, IReadOnlyList<Seg> path, string prefix)
    {
        if (path.Count == 0) throw new ArgumentException("the path is empty");
        var scale = Math.Max(1, path.Sum(s => s.Length));
        var tol = 1e-9 * scale;
        bool closed = path.Count > 1 && Vec3.Distance(path[^1].B, path[0].A) < tol * 1e3
            || path.Count == 1 && path[0].IsArc && path[0].Angle >= 2 * Math.PI - 1e-9;
        // a single full turn: a revolution all the way round (no caps, nothing to stitch)
        if (path.Count == 1 && closed)
        {
            var s = path[0];
            var body = NBuild.Revolve(regions, frame, frame.ToLocal(s.Center!.Value), AxisIn(frame, s.Axis!.Value), 2 * Math.PI, prefix + ".0");
            return body;
        }
        var pieces = new List<NBody>();
        var f = frame;
        for (int k = 0; k < path.Count; k++)
        {
            var s = path[k];
            var name = prefix + "." + k;
            if (Vec3.Dot(s.StartTangent, f.Normal) < 1 - 1e-6)
                throw new ArgumentException($"the path turns sharply at segment {k + 1} (sweeps need a tangent-continuous path)");
            NBody piece;
            if (!s.IsArc) piece = NBuild.Extrude(regions, f, 0, s.Length, name);
            else
            {
                // the profile must stay on one side of the axis (it would sweep through itself)
                var c2 = f.ToLocal(s.Center!.Value);
                var a2 = AxisIn(f, s.Axis!.Value);
                var nrm = new Vec2(-a2.Y, a2.X);
                double side = 0;
                foreach (var r in regions) foreach (var l in r.AllLoops()) foreach (var v in l.Verts)
                        {
                            var d = Vec2.Dot(v.P - c2, nrm);
                            if (side == 0) side = Math.Sign(d);
                            else if (Math.Sign(d) != 0 && Math.Sign(d) != side)
                                throw new ArgumentException($"the profile crosses the axis of path arc {k + 1}: the arc's radius is smaller than the profile");
                        }
                piece = NBuild.Revolve(regions, f, c2, a2, s.Angle, name);
            }
            pieces.Add(piece);
            f = f.Transformed(s.Motion);
        }
        // stitch piece k's end to piece k+1's start (and the last to the first on a closed path)
        int joints = closed ? path.Count : path.Count - 1;
        for (int k = 0; k < joints; k++)
        {
            int a = k, b = (k + 1) % path.Count;
            Stitch(pieces[a], prefix + "." + a + ".End", pieces[b], prefix + "." + b + ".Start", tol * 1e3);
        }
        var res = new NBody();
        foreach (var p in pieces) res.Faces.AddRange(p.Faces);
        if (!closed)
        {
            foreach (var face in res.Faces)
            {
                if (face.Tag.Name == prefix + ".0.Start") face.Tag = new FaceTag(prefix + ".Start", face.Tag.Surface);
                else if (face.Tag.Name == prefix + "." + (path.Count - 1) + ".End") face.Tag = new FaceTag(prefix + ".End", face.Tag.Surface);
            }
        }
        res.Link();
        return res;
    }

    /// <summary>The direction of a 3D axis lying in the frame's plane, in the frame's coordinates.</summary>
    static Vec2 AxisIn(Frame f, Vec3 axis) => new Vec2(Vec3.Dot(axis, f.XDir), Vec3.Dot(axis, f.YDir)).Normalized;

    /// <summary>
    /// Joins two pieces where they meet: <paramref name="a"/>'s end cap and <paramref name="b"/>'s start cap (the same
    /// loops, facing each other) are removed, and b's edges and vertices there are replaced by a's.
    /// </summary>
    static void Stitch(NBody a, string endName, NBody b, string startName, double tol)
    {
        var endFaces = a.Faces.Where(x => x.Tag.Name == endName).ToList();
        var startFaces = b.Faces.Where(x => x.Tag.Name == startName).ToList();
        if (endFaces.Count == 0 || startFaces.Count == 0) throw new InvalidOperationException("sweep: a piece has no cap to join");
        var edgesA = endFaces.SelectMany(x => x.Coedges).Select(c => c.Edge).Distinct().ToList();
        var edgesB = startFaces.SelectMany(x => x.Coedges).Select(c => c.Edge).Distinct().ToList();
        var vertsA = edgesA.SelectMany(e => new[] { e.V0, e.V1 }).Distinct().ToList();
        var vmap = new Dictionary<NVertex, NVertex>(ReferenceEqualityComparer.Instance);
        foreach (var vb in edgesB.SelectMany(e => new[] { e.V0, e.V1 }).Distinct())
        {
            var va = vertsA.MinBy(v => Vec3.Distance(v.P, vb.P))!;
            if (Vec3.Distance(va.P, vb.P) > tol) throw new InvalidOperationException("sweep: the pieces do not meet");
            vmap[vb] = va;
        }
        // b's cap edges: the a edge with the same ends and the same middle (two arcs of a circle share their ends)
        var emap = new Dictionary<NEdge, (NEdge e, bool flip)>(ReferenceEqualityComparer.Instance);
        foreach (var eb in edgesB)
        {
            NVertex v0 = vmap[eb.V0], v1 = vmap[eb.V1];
            var mid = eb.Mid;
            var match = edgesA.Where(ea => (ReferenceEquals(ea.V0, v0) && ReferenceEquals(ea.V1, v1)) || (ReferenceEquals(ea.V0, v1) && ReferenceEquals(ea.V1, v0)))
                .MinBy(ea => Vec3.Distance(ea.Mid, mid));
            if (match == null || Vec3.Distance(match.Mid, mid) > tol) throw new InvalidOperationException("sweep: the pieces' edges do not meet");
            emap[eb] = (match, !ReferenceEquals(match.V0, v0));
        }
        a.Faces.RemoveAll(x => endFaces.Contains(x));
        b.Faces.RemoveAll(x => startFaces.Contains(x));
        foreach (var face in b.Faces)
            foreach (var l in face.Loops)
                for (int i = 0; i < l.Coedges.Count; i++)
                {
                    var c = l.Coedges[i];
                    if (emap.TryGetValue(c.Edge, out var m)) l.Coedges[i] = new NCoedge(m.e, m.flip ? !c.Forward : c.Forward);
                    else
                    {
                        // edges leaving the joint (along the path): their joint vertex becomes a's
                        if (vmap.TryGetValue(c.Edge.V0, out var n0)) c.Edge.V0 = n0;
                        if (vmap.TryGetValue(c.Edge.V1, out var n1)) c.Edge.V1 = n1;
                    }
                }
    }
}

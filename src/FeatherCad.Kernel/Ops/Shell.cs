namespace FeatherCad.Kernel;

/// <summary>
/// Shell (hollow) operation: offsets every face plane inward by the wall thickness, moves each
/// vertex to the intersection of its offset planes, and subtracts that inner solid. Removed
/// (open) faces are offset outward instead so the inner solid breaks through them.
/// </summary>
public static class ShellOp
{
    public static Solid Apply(Solid body, ISet<string> openFaces, double thickness, string prefix)
    {
        if (thickness <= 0) throw new ArgumentException("Thickness must be greater than zero.");
        if (Csg.UseNative && body.Native != null)
        {
            try
            {
                var solid = Native.NTess.ToSolid(Native.NShell.Apply(body.Native, openFaces, thickness, prefix));
                if (!solid.IsEmpty && solid.Topology.OpenHalfEdges == 0) return solid;
                Csg.LastFallbackReason = "shell tessellation not watertight";
            }
            catch (Exception ex) { Csg.LastFallbackReason = ex.Message; }
        }
        var topo = body.Topology;
        int nv = topo.Vertices.Count;
        var offPlanes = new Plane[body.Faces.Count];
        for (int fi = 0; fi < body.Faces.Count; fi++)
        {
            var f = body.Faces[fi];
            offPlanes[fi] = openFaces.Contains(f.Tag.Name) ? f.Plane.Offset(thickness) : f.Plane.Offset(-thickness);
        }
        var incident = new List<int>[nv];
        for (int fi = 0; fi < topo.FaceLoops.Count; fi++)
            foreach (var l in topo.FaceLoops[fi])
                foreach (var v in l)
                    (incident[v] ??= new List<int>(4)).Add(fi);

        var moved = new Vec3[nv];
        for (int v = 0; v < nv; v++)
        {
            var p0 = topo.Vertices[v];
            if (incident[v] == null) { moved[v] = p0; continue; }
            // unique planes (by normal)
            var uniq = new List<Plane>();
            foreach (var fi in incident[v].Distinct())
            {
                var pl = offPlanes[fi];
                if (!uniq.Any(u => Vec3.Dot(u.Normal, pl.Normal) > 1 - 1e-9)) uniq.Add(pl);
            }
            moved[v] = SolveLeastSquares(uniq, p0, thickness);
        }

        var faces = new List<Face>();
        var tagMap = new Dictionary<FaceTag, FaceTag>();
        for (int fi = 0; fi < body.Faces.Count; fi++)
        {
            var f = body.Faces[fi];
            if (!tagMap.TryGetValue(f.Tag, out var tag))
            {
                var s = f.Tag.Surface;
                if (s.Kind == SurfaceKind.Cylinder || s.Kind == SurfaceKind.Sphere)
                {
                    // inner offset surface: radius shrinks for bosses, grows for holes
                    var c = f.Centroid();
                    var radial = (c - s.Origin) - s.Axis * Vec3.Dot(c - s.Origin, s.Axis);
                    var outwardAway = Vec3.Dot(radial, f.Plane.Normal) > 0;
                    s = s with { Radius = Math.Max(1e-6, s.Radius + (outwardAway ? -thickness : thickness)) };
                }
                tagMap[f.Tag] = tag = new FaceTag(prefix + ".I." + f.Tag.Name, s);
            }
            var loops = topo.FaceLoops[fi].Select(l => l.Select(v => moved[v]).ToArray()).ToList();
            faces.Add(new Face(offPlanes[fi], tag, loops));
        }
        var inner = new Solid(faces);
        return Csg.Subtract(body, inner);
    }

    static Vec3 SolveLeastSquares(List<Plane> planes, Vec3 p0, double t)
    {
        Vec3 r;
        if (planes.Count == 1)
        {
            var pl = planes[0];
            r = p0 + pl.Normal * (pl.D - Vec3.Dot(pl.Normal, p0));
        }
        else if (planes.Count == 2) r = TwoPlanes(planes[0], planes[1], p0);
        else
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
            var b = Vec3.Zero;
            foreach (var pl in planes)
            {
                var n = pl.Normal;
                a00 += n.X * n.X; a01 += n.X * n.Y; a02 += n.X * n.Z;
                a11 += n.Y * n.Y; a12 += n.Y * n.Z; a22 += n.Z * n.Z;
                b += n * pl.D;
            }
            var det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            if (Math.Abs(det) < 1e-6)
            {
                // nearly degenerate (e.g. smooth facet fan): use the two most different planes
                Plane best0 = planes[0], best1 = planes[1]; double bestDot = 2;
                for (int i = 0; i < planes.Count; i++)
                    for (int j = i + 1; j < planes.Count; j++)
                    {
                        var d = Math.Abs(Vec3.Dot(planes[i].Normal, planes[j].Normal));
                        if (d < bestDot) { bestDot = d; best0 = planes[i]; best1 = planes[j]; }
                    }
                r = TwoPlanes(best0, best1, p0);
            }
            else
            {
                double i00 = (a11 * a22 - a12 * a12) / det, i01 = (a02 * a12 - a01 * a22) / det, i02 = (a01 * a12 - a02 * a11) / det;
                double i11 = (a00 * a22 - a02 * a02) / det, i12 = (a02 * a01 - a00 * a12) / det, i22 = (a00 * a11 - a01 * a01) / det;
                r = new Vec3(i00 * b.X + i01 * b.Y + i02 * b.Z, i01 * b.X + i11 * b.Y + i12 * b.Z, i02 * b.X + i12 * b.Y + i22 * b.Z);
            }
        }
        if ((r - p0).Length > 20 * t) return p0; // ill-conditioned corner
        return r;
    }

    /// <summary>Closest point to p0 lying on both planes.</summary>
    static Vec3 TwoPlanes(Plane a, Plane b, Vec3 p0)
    {
        var n1 = a.Normal; var n2 = b.Normal;
        var d = Vec3.Dot(n1, n2);
        var den = 1 - d * d;
        var r1 = a.D - Vec3.Dot(n1, p0); var r2 = b.D - Vec3.Dot(n2, p0);
        if (den < 1e-12)
            return p0 + n1 * r1; // parallel planes
        // p = p0 + x n1 + y n2 with [1 d; d 1][x y] = [r1 r2]
        var x = (r1 - d * r2) / den; var y = (r2 - d * r1) / den;
        return p0 + n1 * x + n2 * y;
    }
}

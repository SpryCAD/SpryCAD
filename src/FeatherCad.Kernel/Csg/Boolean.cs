namespace FeatherCad.Kernel;

public enum BoolOp { Union, Subtract, Intersect }

/// <summary>Solid boolean operations.</summary>
public static class Csg
{
    public static Solid Union(Solid a, Solid b) => Run(a, b, BoolOp.Union);
    public static Solid Subtract(Solid a, Solid b) => Run(a, b, BoolOp.Subtract);
    public static Solid Intersect(Solid a, Solid b) => Run(a, b, BoolOp.Intersect);

    public static Solid Run(Solid a, Solid b, BoolOp op)
    {
        if (a.IsEmpty) return op == BoolOp.Union ? b : Solid.Empty;
        if (b.IsEmpty) return op == BoolOp.Intersect ? Solid.Empty : a;
        if (!a.Bounds.Overlaps(b.Bounds, 1e-6))
        {
            return op switch
            {
                BoolOp.Union => Solid.Combine(new[] { a, b }),
                BoolOp.Subtract => a,
                _ => Solid.Empty,
            };
        }

        var pa = ToPolys(a); var pb = ToPolys(b);
        var na = new BspNode(pa); var nb = new BspNode(pb);
        switch (op)
        {
            case BoolOp.Union:
                na.ClipTo(nb); nb.ClipTo(na); nb.Invert(); nb.ClipTo(na); nb.Invert();
                na.Build(nb.AllPolygons());
                break;
            case BoolOp.Subtract:
                na.Invert(); na.ClipTo(nb); nb.ClipTo(na); nb.Invert(); nb.ClipTo(na); nb.Invert();
                na.Build(nb.AllPolygons()); na.Invert();
                break;
            case BoolOp.Intersect:
                na.Invert(); nb.ClipTo(na); nb.Invert(); na.ClipTo(nb); nb.ClipTo(na);
                na.Build(nb.AllPolygons()); na.Invert();
                break;
        }
        return FaceMerger.Rebuild(na.AllPolygons());
    }

    /// <summary>Union of many solids (balanced pairwise to keep BSP sizes small).</summary>
    public static Solid UnionAll(IList<Solid> solids)
    {
        if (solids.Count == 0) return Solid.Empty;
        var list = solids.Where(s => !s.IsEmpty).ToList();
        if (list.Count == 0) return Solid.Empty;
        while (list.Count > 1)
        {
            var next = new List<Solid>((list.Count + 1) / 2);
            for (int i = 0; i < list.Count; i += 2)
                next.Add(i + 1 < list.Count ? Union(list[i], list[i + 1]) : list[i]);
            list = next;
        }
        return list[0];
    }

    /// <summary>Converts faces to convex polygons (convex faces pass through, others are triangulated).</summary>
    internal static List<CsgPoly> ToPolys(Solid s)
    {
        var res = new List<CsgPoly>(s.Faces.Count * 2);
        foreach (var f in s.Faces)
        {
            if (f.Loops.Count == 1 && IsConvex(f.Outer, f.Plane.Normal))
            {
                res.Add(new CsgPoly((Vec3[])f.Outer.Clone(), f.Plane, f.Tag));
                continue;
            }
            foreach (var t in Triangulator.TriangulateFace(f))
                res.Add(new CsgPoly(new[] { t.A, t.B, t.C }, f.Plane, f.Tag));
        }
        return res;
    }

    static bool IsConvex(Vec3[] pts, Vec3 n)
    {
        int c = pts.Length;
        if (c < 3) return false;
        for (int i = 0; i < c; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % c]; var d = pts[(i + 2) % c];
            if (Vec3.Dot(Vec3.Cross(b - a, d - b), n) < -1e-12) return false;
        }
        return true;
    }
}

namespace SpryCad.Kernel;

/// <summary>Convex polygon used inside the BSP boolean engine. Carries its exact plane and face tag.</summary>
internal sealed class CsgPoly
{
    public Vec3[] Pts;
    public Plane Plane;
    public FaceTag Tag;

    public CsgPoly(Vec3[] pts, Plane plane, FaceTag tag) { Pts = pts; Plane = plane; Tag = tag; }

    public void Flip()
    {
        Array.Reverse(Pts);
        Plane = Plane.Flipped();
    }
}

/// <summary>
/// Plane based BSP tree (after Thibault/Naylor, csg.js). Splitting planes are always the exact
/// planes of the input faces, never planes re-fitted from vertices, which keeps classification stable.
/// </summary>
internal sealed class BspNode
{
    public const double Eps = 1e-7;

    Plane? _plane;
    BspNode? _front, _back;
    List<CsgPoly> _polys = new();

    public BspNode(List<CsgPoly>? polys = null)
    {
        if (polys != null && polys.Count > 0) Build(polys);
    }

    public void Invert()
    {
        // iterative to avoid deep recursion
        var stack = new Stack<BspNode>(); stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (var p in n._polys) p.Flip();
            if (n._plane != null) n._plane = n._plane.Flipped();
            (n._front, n._back) = (n._back, n._front);
            if (n._front != null) stack.Push(n._front);
            if (n._back != null) stack.Push(n._back);
        }
    }

    /// <summary>Removes the parts of polys that are inside this BSP's solid.</summary>
    public List<CsgPoly> ClipPolygons(List<CsgPoly> polys)
    {
        var result = new List<CsgPoly>();
        var stack = new Stack<(BspNode node, List<CsgPoly> polys)>();
        stack.Push((this, polys));
        while (stack.Count > 0)
        {
            var (n, list) = stack.Pop();
            if (n._plane == null) { result.AddRange(list); continue; }
            var front = new List<CsgPoly>(); var back = new List<CsgPoly>();
            foreach (var p in list) Split(n._plane, p, front, back, front, back);
            if (n._front != null) stack.Push((n._front, front)); else result.AddRange(front);
            if (n._back != null) stack.Push((n._back, back)); // else: inside -> discarded
        }
        return result;
    }

    public void ClipTo(BspNode bsp)
    {
        var stack = new Stack<BspNode>(); stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            n._polys = bsp.ClipPolygons(n._polys);
            if (n._front != null) stack.Push(n._front);
            if (n._back != null) stack.Push(n._back);
        }
    }

    public List<CsgPoly> AllPolygons()
    {
        var res = new List<CsgPoly>();
        var stack = new Stack<BspNode>(); stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            res.AddRange(n._polys);
            if (n._front != null) stack.Push(n._front);
            if (n._back != null) stack.Push(n._back);
        }
        return res;
    }

    public void Build(List<CsgPoly> polys)
    {
        var stack = new Stack<(BspNode node, List<CsgPoly> polys)>();
        stack.Push((this, polys));
        while (stack.Count > 0)
        {
            var (n, list) = stack.Pop();
            if (list.Count == 0) continue;
            n._plane ??= ChooseSplitter(list);
            var front = new List<CsgPoly>(); var back = new List<CsgPoly>();
            foreach (var p in list) Split(n._plane, p, n._polys, n._polys, front, back);
            if (front.Count > 0) { n._front ??= new BspNode(); stack.Push((n._front, front)); }
            if (back.Count > 0) { n._back ??= new BspNode(); stack.Push((n._back, back)); }
        }
    }

    /// <summary>Pick a splitting plane: sample a few candidates and minimize splits (keeps trees small).</summary>
    static Plane ChooseSplitter(List<CsgPoly> list)
    {
        if (list.Count < 8) return list[0].Plane;
        int samples = Math.Min(5, list.Count);
        Plane best = list[0].Plane; int bestScore = int.MaxValue;
        int step = list.Count / samples;
        for (int s = 0; s < samples; s++)
        {
            var cand = list[s * step].Plane;
            int splits = 0, fr = 0, bk = 0;
            int lim = Math.Min(list.Count, 64);
            int st = Math.Max(1, list.Count / lim);
            for (int i = 0; i < list.Count; i += st)
            {
                int f = 0, b = 0;
                foreach (var v in list[i].Pts)
                {
                    var d = cand.Distance(v);
                    if (d > Eps) f++; else if (d < -Eps) b++;
                }
                if (f > 0 && b > 0) splits++; else if (f > 0) fr++; else if (b > 0) bk++;
            }
            var score = splits * 8 + Math.Abs(fr - bk);
            if (score < bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }

    const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

    [ThreadStatic] static int[]? _types;

    /// <summary>
    /// Deadline for the current thread's BSP work (Stopwatch ticks; 0 = none). BSP booleans on finely
    /// tessellated curved models can explode; past the deadline the boolean throws instead of hanging.
    /// </summary>
    [ThreadStatic] public static long Deadline;
    [ThreadStatic] static int _work;

    public static void Split(Plane plane, CsgPoly poly, List<CsgPoly> coFront, List<CsgPoly> coBack, List<CsgPoly> front, List<CsgPoly> back)
    {
        if (Deadline != 0 && (++_work & 4095) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() > Deadline)
            throw new TimeoutException("the boolean operation did not converge");
        // Same plane object or numerically identical -> coplanar without touching vertices.
        if (ReferenceEquals(plane, poly.Plane) || (Math.Abs(plane.D - poly.Plane.D) < Eps && Vec3.Dot(plane.Normal, poly.Plane.Normal) > 1 - 1e-12))
        { coFront.Add(poly); return; }
        if (Math.Abs(plane.D + poly.Plane.D) < Eps && Vec3.Dot(plane.Normal, poly.Plane.Normal) < -1 + 1e-12)
        { coBack.Add(poly); return; }

        var pts = poly.Pts;
        int n = pts.Length;
        if (_types == null || _types.Length < n) _types = new int[Math.Max(n, 64)];
        var types = _types;
        int polyType = 0;
        for (int i = 0; i < n; i++)
        {
            var t = plane.Distance(pts[i]);
            var type = t < -Eps ? Back : t > Eps ? Front : Coplanar;
            polyType |= type;
            types[i] = type;
        }
        switch (polyType)
        {
            case Coplanar:
                (Vec3.Dot(plane.Normal, poly.Plane.Normal) > 0 ? coFront : coBack).Add(poly);
                break;
            case Front: front.Add(poly); break;
            case Back: back.Add(poly); break;
            default:
                {
                    var f = new List<Vec3>(n + 1); var b = new List<Vec3>(n + 1);
                    for (int i = 0; i < n; i++)
                    {
                        int j = (i + 1) % n;
                        int ti = types[i], tj = types[j];
                        var vi = pts[i]; var vj = pts[j];
                        if (ti != Back) f.Add(vi);
                        if (ti != Front) b.Add(vi);
                        if ((ti | tj) == Spanning)
                        {
                            var di = plane.Distance(vi); var dj = plane.Distance(vj);
                            var t = di / (di - dj);
                            var v = vi + (vj - vi) * t;
                            f.Add(v); b.Add(v);
                        }
                    }
                    if (f.Count >= 3) front.Add(new CsgPoly(f.ToArray(), poly.Plane, poly.Tag));
                    if (b.Count >= 3) back.Add(new CsgPoly(b.ToArray(), poly.Plane, poly.Tag));
                    break;
                }
        }
    }
}

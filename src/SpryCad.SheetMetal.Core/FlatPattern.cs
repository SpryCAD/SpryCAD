using System.Globalization;

namespace SpryCad.SheetMetal;

/// <summary>A piece of the flat: a panel, or a bend's zone (the strip its allowance wide).</summary>
public sealed record FlatPiece(string Id, Polygon2 Outline, Panel? Panel, Bend? Bend)
{
    public bool IsBend => Bend != null;
}

/// <summary>A bend as the flat pattern shows it (sheet metal spec section 10): its line, notes and where it came from.</summary>
public sealed record FlatBendLine(BendId Id, Vec2 A, Vec2 B, BendDirection Direction, double Angle, double InsideRadius,
    double Allowance, string Tooling, AllowanceSource Source, Vec2 TangentA0, Vec2 TangentA1, Vec2 TangentB0, Vec2 TangentB1)
{
    /// <summary>The bend angle in degrees.</summary>
    public double AngleDeg => Angle * 180 / Math.PI;

    /// <summary>The bend note by the profile's format: {direction}, {angle}, {radius}, {id}, {tooling}.</summary>
    public string Note(string format) => format
        .Replace("{direction}", Direction == BendDirection.Up ? "UP" : "DOWN")
        .Replace("{angle}", AngleDeg.ToString("0.#", CultureInfo.InvariantCulture) + "°")
        .Replace("{radius}", InsideRadius.ToString("0.###", CultureInfo.InvariantCulture))
        .Replace("{id}", Id.Value)
        .Replace("{tooling}", Tooling);
}

/// <summary>
/// The flat pattern in 2D (sheet metal spec section 10): the outline (closed loops, outer counterclockwise, holes
/// clockwise; straight edges and true arcs) and the bends, in the flat's coordinates, laid out by the fixed panel so
/// it does not move between edits. Made from the bend graph alone.
/// </summary>
public sealed class FlatPattern2D
{
    public required double Thickness { get; init; }
    /// <summary>The outline's loops' points (outer counterclockwise, holes clockwise).</summary>
    public required IReadOnlyList<IReadOnlyList<Vec2>> Loops { get; init; }
    /// <summary>Each loop's edges' bulges (0: straight; see <see cref="Seg2"/>).</summary>
    public required IReadOnlyList<IReadOnlyList<double>> LoopBulges { get; init; }
    public required IReadOnlyList<FlatBendLine> Bends { get; init; }

    /// <summary>A loop's edges.</summary>
    public IEnumerable<Seg2> Segs(int loop)
    {
        var l = Loops[loop];
        for (int i = 0; i < l.Count; i++) yield return new Seg2(l[i], l[(i + 1) % l.Count], LoopBulges[loop][i]);
    }

    /// <summary>The length of the outline (cut length, for costing: main spec section 43).</summary>
    public double CutLength => Enumerable.Range(0, Loops.Count).Sum(i => Segs(i).Sum(s => s.Length));

    /// <summary>The number of closed contours (pierces, for costing).</summary>
    public int PierceCount => Loops.Count;

    /// <summary>The material's area in the flat (the outer loops less the holes).</summary>
    public double Area => Enumerable.Range(0, Loops.Count).Sum(i => Polygon2.LoopArea(Loops[i]) + Segs(i).Sum(s => s.SegmentArea));

    public (Vec2 Min, Vec2 Max) Bounds
    {
        get
        {
            var pts = Enumerable.Range(0, Loops.Count).SelectMany(i => Segs(i).SelectMany(s => s.Points(Math.PI / 720))).ToList();
            return (new Vec2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Vec2(pts.Max(p => p.X), pts.Max(p => p.Y)));
        }
    }

    /// <summary>The pieces of the flat: each panel, and each bend's zone between its tangent lines along A's edge.</summary>
    public static List<FlatPiece> Pieces(BendGraph g)
    {
        var pieces = g.Panels.Select(p => new FlatPiece(p.Id.Value, p.FlatOutline, p, null)).ToList();
        foreach (var b in g.Bends) pieces.Add(new FlatPiece(b.Id.Value, Strip(g, b), null, b));
        return pieces;
    }

    /// <summary>
    /// Each piece's loops with their straight edges split where another piece's corner lies on them (a flange running
    /// on past its bend zone: the zone's corner on the flange's edge), so pieces that meet share whole segments. Each
    /// point carries the index of the piece's own edge it starts a piece of (for naming the edge's faces) and the
    /// piece's bulge (arcs are never split).
    /// </summary>
    public static List<(FlatPiece piece, List<List<(Vec2 p, int edge, double bulge)>> loops)> Split(List<FlatPiece> pieces, double tol = 1e-7)
    {
        var all = pieces.SelectMany(x => x.Outline.Points).ToList();
        var result = new List<(FlatPiece, List<List<(Vec2, int, double)>>)>();
        foreach (var piece in pieces)
        {
            var loops = new List<List<(Vec2, int, double)>>();
            for (int li = 0; li < piece.Outline.LoopCount; li++)
            {
                var loop = piece.Outline.Loop(li);
                var split = new List<(Vec2, int, double)>();
                for (int i = 0; i < loop.Count; i++)
                {
                    var bulge = piece.Outline.Bulge(li, i);
                    Vec2 a = loop[i], b = loop[(i + 1) % loop.Count];
                    split.Add((a, i, bulge));
                    var d = b - a;
                    var len2 = Vec2.Dot(d, d);
                    if (bulge != 0 || len2 <= 0) continue;
                    var len = Math.Sqrt(len2);
                    var on = all.Select(v => (v, s: Vec2.Dot(v - a, d) / len2))
                        .Where(x => x.s * len > tol && (1 - x.s) * len > tol && Math.Abs(Vec2.Cross(d, x.v - a)) / len < tol)
                        .OrderBy(x => x.s).Select(x => x.v).ToList();
                    Vec2? last = null;
                    foreach (var v in on)
                    {
                        if (last is { } l && (v - l).Length < tol) continue;
                        split.Add((v, i, 0));
                        last = v;
                    }
                }
                loops.Add(split);
            }
            result.Add((piece, loops));
        }
        return result;
    }

    /// <summary>A bend's zone in the flat: A's edge it starts at, moved out by the allowance (counterclockwise).</summary>
    public static Polygon2 Strip(BendGraph g, Bend b)
    {
        var poly = g.Panel(b.A).FlatOutline;
        var a = poly.Outer;
        if (b.EdgeA < 0 || b.EdgeA >= a.Count) throw new BendGraphException($"{b.Id} is not on an edge of {b.A}.");
        if (poly.Bulge(0, b.EdgeA) != 0) throw new BendGraphException($"{b.Id} is on a round edge of {b.A}: bends are on straight edges.");
        var p = a[b.EdgeA];
        var q = a[(b.EdgeA + 1) % a.Count];
        var n = b.FlatAxis.Normal * b.Allowance;
        // the region beyond A's edge p→q (A on its left): counterclockwise it runs q→p along the edge
        return new Polygon2(new[] { q, p, p + n, q + n });
    }

    public static FlatPattern2D From(BendGraph g)
    {
        var pieces = Pieces(g);
        // the outline: every piece's edges but those two pieces share (run both ways), joined into loops
        var segs = new List<(Vec2 a, Vec2 b, double bulge)>();
        foreach (var (_, ls) in Split(pieces))
            foreach (var loop in ls)
                for (int i = 0; i < loop.Count; i++) segs.Add((loop[i].p, loop[(i + 1) % loop.Count].p, loop[i].bulge));
        var tol = 1e-6;
        string K(Vec2 p) => FormattableString.Invariant($"{Math.Round(p.X / tol)}:{Math.Round(p.Y / tol)}");
        var directed = segs.Where(s => s.bulge == 0).GroupBy(s => K(s.a) + ">" + K(s.b)).ToDictionary(x => x.Key, x => x.Count());
        var outer = segs.Where(s => s.bulge != 0 || !directed.ContainsKey(K(s.b) + ">" + K(s.a))).ToList();
        var used = new bool[outer.Count];
        var loops = new List<IReadOnlyList<Vec2>>();
        var bulges = new List<IReadOnlyList<double>>();
        for (int s0 = 0; s0 < outer.Count; s0++)
        {
            if (used[s0]) continue;
            var loop = new List<(Vec2 p, double bulge)>();
            int cur = s0;
            while (true)
            {
                used[cur] = true;
                loop.Add((outer[cur].a, outer[cur].bulge));
                var end = K(outer[cur].b);
                if (end == K(outer[s0].a)) break;
                cur = Enumerable.Range(0, outer.Count).FirstOrDefault(i => !used[i] && K(outer[i].a) == end, -1);
                if (cur < 0) throw new BendGraphException("The flat pattern's outline does not close.");
            }
            var simple = Simplify(loop);
            loops.Add(simple.Select(x => x.p).ToList());
            bulges.Add(simple.Select(x => x.bulge).ToList());
        }
        var bends = g.Bends.Select(b =>
        {
            var strip = Strip(g, b).Outer;
            Vec2 q = strip[0], p = strip[1];
            var half = b.FlatAxis.Normal * (b.Allowance / 2);
            return new FlatBendLine(b.Id, p + half, q + half, b.Direction, b.Angle, b.InsideRadius, b.Allowance, b.Tooling, b.Source,
                p, q, strip[2], strip[3]);
        }).ToList();
        return new FlatPattern2D { Thickness = g.Thickness, Loops = loops, LoopBulges = bulges, Bends = bends };
    }

    /// <summary>A loop without the points where it runs straight on (a panel's edge continuing along a bend zone's).</summary>
    static List<(Vec2 p, double bulge)> Simplify(List<(Vec2 p, double bulge)> loop)
    {
        var r = new List<(Vec2 p, double bulge)>(loop);
        bool changed = true;
        while (changed && r.Count > 3)
        {
            changed = false;
            for (int i = 0; i < r.Count; i++)
            {
                int h = (i + r.Count - 1) % r.Count;
                if (r[h].bulge != 0 || r[i].bulge != 0) continue;
                var a = r[h].p; var b = r[i].p; var c = r[(i + 1) % r.Count].p;
                var u = b - a; var v = c - b;
                if (Math.Abs(Vec2.Cross(u, v)) <= 1e-9 * u.Length * v.Length && Vec2.Dot(u, v) > 0) { r.RemoveAt(i); changed = true; break; }
            }
        }
        return r;
    }

    /// <summary>The bend table (sheet metal spec section 10): a row per bend, in the graph's order.</summary>
    public IEnumerable<string[]> BendTable() => Bends.Select(b => new[]
    {
        b.Id.Value, b.Direction == BendDirection.Up ? "Up" : "Down",
        b.AngleDeg.ToString("0.##", CultureInfo.InvariantCulture) + "°",
        b.InsideRadius.ToString("0.###", CultureInfo.InvariantCulture), b.Tooling, b.Source.ToString(),
    });

    public static readonly string[] BendTableHeader = { "Bend", "Direction", "Angle", "Inside radius", "Tooling", "Allowance from" };
}

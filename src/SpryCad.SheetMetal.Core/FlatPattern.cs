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
/// clockwise) and the bends, in the flat's coordinates, laid out by the fixed panel so it does not move between edits.
/// Made from the bend graph alone.
/// </summary>
public sealed class FlatPattern2D
{
    public required double Thickness { get; init; }
    public required IReadOnlyList<IReadOnlyList<Vec2>> Loops { get; init; }
    public required IReadOnlyList<FlatBendLine> Bends { get; init; }

    /// <summary>The length of the outline (cut length, for costing: main spec section 43).</summary>
    public double CutLength => Loops.Sum(l => l.Select((p, i) => (l[(i + 1) % l.Count] - p).Length).Sum());

    /// <summary>The number of closed contours (pierces, for costing).</summary>
    public int PierceCount => Loops.Count;

    public (Vec2 Min, Vec2 Max) Bounds
    {
        get
        {
            var pts = Loops.SelectMany(l => l).ToList();
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

    /// <summary>A bend's zone in the flat: A's edge it starts at, moved out by the allowance (counterclockwise).</summary>
    public static Polygon2 Strip(BendGraph g, Bend b)
    {
        var a = g.Panel(b.A).FlatOutline.Outer;
        if (b.EdgeA < 0 || b.EdgeA >= a.Count) throw new BendGraphException($"{b.Id} is not on an edge of {b.A}.");
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
        var segs = new List<(Vec2 a, Vec2 b)>();
        foreach (var piece in pieces)
            foreach (var loop in new[] { piece.Outline.Outer }.Concat(piece.Outline.Holes))
                for (int i = 0; i < loop.Count; i++) segs.Add((loop[i], loop[(i + 1) % loop.Count]));
        var tol = 1e-6;
        string K(Vec2 p) => FormattableString.Invariant($"{Math.Round(p.X / tol)}:{Math.Round(p.Y / tol)}");
        var directed = segs.GroupBy(s => K(s.a) + ">" + K(s.b)).ToDictionary(x => x.Key, x => x.Count());
        var outer = segs.Where(s => !directed.ContainsKey(K(s.b) + ">" + K(s.a))).ToList();
        var used = new bool[outer.Count];
        var loops = new List<IReadOnlyList<Vec2>>();
        for (int s0 = 0; s0 < outer.Count; s0++)
        {
            if (used[s0]) continue;
            var loop = new List<Vec2>();
            int cur = s0;
            while (true)
            {
                used[cur] = true;
                loop.Add(outer[cur].a);
                var end = K(outer[cur].b);
                if (end == K(outer[s0].a)) break;
                cur = Enumerable.Range(0, outer.Count).FirstOrDefault(i => !used[i] && K(outer[i].a) == end, -1);
                if (cur < 0) throw new BendGraphException("The flat pattern's outline does not close.");
            }
            loops.Add(Simplify(loop));
        }
        var bends = g.Bends.Select(b =>
        {
            var strip = Strip(g, b).Outer;
            Vec2 q = strip[0], p = strip[1];
            var half = b.FlatAxis.Normal * (b.Allowance / 2);
            return new FlatBendLine(b.Id, p + half, q + half, b.Direction, b.Angle, b.InsideRadius, b.Allowance, b.Tooling, b.Source,
                p, q, strip[2], strip[3]);
        }).ToList();
        return new FlatPattern2D { Thickness = g.Thickness, Loops = loops, Bends = bends };
    }

    /// <summary>A loop without the points where it runs straight on (a panel's edge continuing along a bend zone's).</summary>
    static List<Vec2> Simplify(List<Vec2> loop)
    {
        var r = new List<Vec2>(loop);
        bool changed = true;
        while (changed && r.Count > 3)
        {
            changed = false;
            for (int i = 0; i < r.Count; i++)
            {
                var a = r[(i + r.Count - 1) % r.Count]; var b = r[i]; var c = r[(i + 1) % r.Count];
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

using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel.Native;
using SpryCad.SheetMetal.Brep;
using KVec2 = SpryCad.Kernel.Vec2;

namespace SpryCad.SheetMetal.Features;

/// <summary>
/// How sheet metal features rebuild (sheet metal spec section 6): each edits the body's bend graph; the folded solid
/// (or the flat, when the body is shown flattened) is made again from the graph after each.
/// </summary>
static class SheetRebuild
{
    static FeatureException Error(string m) => new(m);

    static SheetMetalBody BodyOf(FeatureContext ctx, string id) =>
        ctx.Doc.SheetBody(id) ?? throw Error($"The part has no sheet metal body \"{id}\".");

    // ---- base flange ----

    public static void BaseFlange(BaseFlangeFeature f, FeatureContext ctx)
    {
        var sb = BodyOf(ctx, f.Body);
        if (ctx.State.Sheets.ContainsKey(sb.Id)) throw Error($"{Name(sb)} already has a base flange.");
        var rules = SheetProfiles.RulesOf(ctx.Doc, sb);
        if (!ctx.State.Sketches.TryGetValue(f.SketchId, out var sk)) throw Error("The base flange's sketch is missing.");
        var bendRules = new Dictionary<BendId, SheetRules>();
        var (graph, world) = sk.Regions.Count > 0 ? Plate(f, sk, rules) : Profile(f, sk, rules, ctx, bendRules);
        Finish(ctx, sb, graph, world, rules, bendRules);
    }

    static string Name(SheetMetalBody b) => string.IsNullOrEmpty(b.Name) ? b.Id : b.Name;

    /// <summary>A plate: the sketch's one closed profile, the sketch plane the top side, the material below it.</summary>
    static (BendGraph, Frame3) Plate(BaseFlangeFeature f, SketchResult sk, SheetRules rules)
    {
        if (sk.Regions.Count > 1) throw Error("The sketch has more than one closed profile: a base flange takes one.");
        var region = sk.Regions[0].Normalized();
        List<Vec2> Loop(Kernel.ProfileLoop l)
        {
            if (l.Verts.Any(v => v.Arc != null || v.Spline != null))
                throw Error("A base flange's outline is straight lines for now: arcs and splines come with cuts.");
            return l.Verts.Select(v => new Vec2(v.P.X, v.P.Y)).ToList();
        }
        var outline = new Polygon2(Loop(region.Outer), region.Holes.Select(h => (IReadOnlyList<Vec2>)Loop(h)).ToList());
        var panel = new Panel(new PanelId(f.Id + ".P0"), outline, Frame3.Identity);
        var fr = sk.Frame;
        var world = new Frame3(V(fr.XDir), V(fr.YDir), V(fr.Normal), V(fr.Origin));
        // the material above the sketch plane: the top side a thickness up
        if (f.ReverseSide) world = world with { Origin = world.Origin + world.Z * rules.Thickness };
        return (new BendGraph(rules.Thickness, new[] { panel }, Array.Empty<Bend>(), panel.Id), world);
    }

    /// <summary>
    /// A bent profile: the sketch's lines, end to end, as the sheet's top side; each corner a bend at the profile's
    /// inside radius (a left turn bends down, the top on the outside; a right turn up). Flat lengths from the lines'
    /// lengths less the setbacks to the top side's virtual sharps.
    /// </summary>
    static (BendGraph, Frame3) Profile(BaseFlangeFeature f, SketchResult sk, SheetRules rules, FeatureContext ctx, Dictionary<BendId, SheetRules> bendRules)
    {
        var chain = Chain(sk.Sketch);
        if (f.ReverseSide) chain.Reverse();
        double t = rules.Thickness;
        var width = ctx.Length(f.Width, "Width");
        if (width <= 0) throw Error("The width must be greater than zero.");
        double y0 = f.ReverseWidth ? -width : 0, y1 = y0 + width;

        int n = chain.Count - 1; // segments
        var dirs = Enumerable.Range(0, n).Select(i => (chain[i + 1] - chain[i]).Normalized()).ToArray();
        var lengths = Enumerable.Range(0, n).Select(i => (chain[i + 1] - chain[i]).Length).ToArray();
        var bends = new List<(double angle, BendDirection dir, SheetRules r, BendId id)>();
        for (int i = 1; i < n; i++)
        {
            var cross = Vec2.Cross(dirs[i - 1], dirs[i]);
            var angle = Math.Atan2(Math.Abs(cross), Vec2.Dot(dirs[i - 1], dirs[i]));
            if (angle < 1e-6) throw Error($"Lines {i} and {i + 1} run straight on: there is no bend between them.");
            if (angle > Math.PI - 1e-6) throw Error($"Lines {i} and {i + 1} turn back on themselves.");
            var id = new BendId($"{f.Id}.B{i - 1}");
            var r = SheetProfiles.RulesOf(ctx.Doc, rules, id);
            if (!ReferenceEquals(r, rules)) bendRules[id] = r;
            bends.Add((angle, cross > 0 ? BendDirection.Down : BendDirection.Up, r, id));
        }
        // the top side is outside a down bend (its sharp the outside one) and inside an up bend
        double Setback(int b) => bends[b].dir == BendDirection.Down
            ? BendMath.Ossb(bends[b].angle, bends[b].r.InsideRadius, t) : BendMath.Issb(bends[b].angle, bends[b].r.InsideRadius);

        var panels = new List<Panel>();
        var graphBends = new List<Bend>();
        double x = 0;
        for (int i = 0; i < n; i++)
        {
            var flat = lengths[i] - (i > 0 ? Setback(i - 1) : 0) - (i < n - 1 ? Setback(i) : 0);
            if (flat <= 1e-9) throw Error($"Line {i + 1} is too short for its bends (at least {lengths[i] - flat:0.###} mm).");
            var id = new PanelId($"{f.Id}.P{i}");
            panels.Add(new Panel(id, Polygon2.Rectangle(new Vec2(x, y0), new Vec2(x + flat, y1)), Frame3.Identity));
            x += flat;
            if (i < n - 1)
            {
                var b = bends[i];
                SheetRules r = b.r;
                var (ba, source) = Allowance(r, b.angle, b.id);
                graphBends.Add(new Bend(b.id, id, new PanelId($"{f.Id}.P{i + 1}"), new Line2(new Vec2(x + ba / 2, y0), new Vec2(0, -1)),
                    b.angle, b.dir, r.InsideRadius, ba, r.Toolset, source) { EdgeA = 1, EdgeB = 3 });
                x += ba;
            }
        }
        var fr = sk.Frame;
        var origin = V(fr.ToWorld(chain[0].X, chain[0].Y, 0));
        var xd = V(fr.ToWorld(dirs[0].X, dirs[0].Y, 0) - fr.Origin);
        var yd = V(fr.Normal);
        var world = new Frame3(xd, yd, Vec3.Cross(xd, yd), origin);
        return (new BendGraph(t, panels, graphBends, panels[0].Id), world);
    }

    /// <summary>The sketch's lines end to end, as points in the sketch (one open chain; nothing else in the sketch).</summary>
    static List<Vec2> Chain(Sketch s)
    {
        var lines = new List<(Vec2 a, Vec2 b)>();
        foreach (var e in s.Entities)
        {
            if (e.Construction || e.IsReference || e.Dangling) continue;
            if (e is not SkLine l) throw Error("A bent base flange is made of lines only (no arcs or splines).");
            var p = s.Point(l.P1); var q = s.Point(l.P2);
            if (p == null || q == null) continue;
            lines.Add((new Vec2(p.X, p.Y), new Vec2(q.X, q.Y)));
        }
        if (lines.Count == 0) throw Error("The sketch has neither a closed profile nor lines for a bent profile.");
        const double tol = 1e-6;
        bool Same(Vec2 a, Vec2 b) => (a - b).Length < tol;
        // the chain's ends: the points only one line has
        var ends = lines.SelectMany(l => new[] { l.a, l.b }).Where(p => lines.Count(l => Same(l.a, p) || Same(l.b, p)) == 1).ToList();
        if (ends.Count != 2) throw Error("The sketch's lines must make one open chain, end to end (with no branches).");
        var chain = new List<Vec2> { ends.OrderBy(p => p.X).ThenBy(p => p.Y).First() };
        var left = new List<(Vec2 a, Vec2 b)>(lines);
        while (left.Count > 0)
        {
            var last = chain[^1];
            var i = left.FindIndex(l => Same(l.a, last) || Same(l.b, last));
            if (i < 0) throw Error("The sketch's lines must make one open chain, end to end.");
            var l = left[i];
            left.RemoveAt(i);
            chain.Add(Same(l.a, last) ? l.b : l.a);
        }
        return chain;
    }

    // ---- edge flange ----

    public static void EdgeFlange(EdgeFlangeFeature f, FeatureContext ctx)
    {
        var sb = BodyOf(ctx, f.Body);
        if (!ctx.State.Sheets.TryGetValue(sb.Id, out var st)) throw Error($"{Name(sb)} has no base flange before this flange.");
        if (f.Edges.Count == 0) throw Error("Select the edges to put flanges on.");
        var g = st.Graph;
        var rules = st.Rules;
        double t = g.Thickness;
        var angle = ctx.Angle(f.Angle, "Angle") * Math.PI / 180;
        if (!(angle > 0 && angle < Math.PI)) throw Error("The angle must be more than 0° and less than 180°.");
        var length = ctx.Length(f.Length, "Length");
        if (length <= 0) throw Error("The length must be greater than zero.");

        var panels = g.Panels.ToList();
        var bends = g.Bends.ToList();
        var bendRules = new Dictionary<BendId, SheetRules>(st.BendRules);
        var edges = f.Edges.Select(e => FoldedSolid.ParseEdge(e) ?? throw Error($"\"{e}\" is not a sheet metal edge.")).ToList();
        bool Used(PanelId p, int i) => bends.Any(b => (b.A == p && b.EdgeA == i) || (b.B == p && b.EdgeB == i))
            || edges.Count(e => e.panel == p && e.edge == i) > 1;
        for (int k = 0; k < edges.Count; k++)
        {
            var (pid, i) = edges[k];
            int pi = panels.FindIndex(p => p.Id == pid);
            if (pi < 0) throw Error($"The edge {f.Edges[k]} is not in the part any more.");
            var outer = panels[pi].FlatOutline.Outer;
            int m = outer.Count;
            if (i < 0 || i >= m) throw Error($"The edge {f.Edges[k]} is not in the part any more.");
            if (Used(pid, i)) throw Error($"{f.Edges[k]} already has a bend.");
            // flanges on edges that meet need a corner between them (open, closed, overlapping): not yet
            int prev = (i + m - 1) % m, next = (i + 1) % m;
            if (bends.Any(b => (b.A == pid && (b.EdgeA == prev || b.EdgeA == next)) || (b.B == pid && (b.EdgeB == prev || b.EdgeB == next)))
                || edges.Any(e => e.panel == pid && (e.edge == prev || e.edge == next)))
                throw Error($"{f.Edges[k]} meets an edge that has a bend: flanges on edges that meet need a corner, which is not modelled yet.");

            var id = new BendId($"{f.Id}.{k}.B");
            var r = SheetProfiles.RulesOf(ctx.Doc, rules, id);
            if (!ReferenceEquals(r, rules)) bendRules[id] = r;
            double R = r.InsideRadius;
            var trim = f.Position switch
            {
                FlangePosition.MaterialInside => BendMath.Ossb(angle, R, t),
                FlangePosition.MaterialOutside => BendMath.Issb(angle, R),
                _ => 0,
            };
            if (trim > 0) panels[pi] = panels[pi] with { FlatOutline = MoveEdge(panels[pi].FlatOutline, i, trim, f.Edges[k]) };
            outer = panels[pi].FlatOutline.Outer;
            Vec2 a = outer[i], b = outer[next];
            var (ba, source) = Allowance(r, angle, id);
            var flat = BendMath.FlatContribution(length, f.Reference, angle, R, t);
            if (flat <= 1e-9) throw Error($"The flange is too short for its bend: at least {length - flat:0.###} mm measured {Words(f.Reference)}.");
            var n = new Vec2((b - a).Y, -(b - a).X).Normalized();
            var pnew = new PanelId($"{f.Id}.{k}.P");
            panels.Add(new Panel(pnew, new Polygon2(new[] { b + n * ba, a + n * ba, a + n * (ba + flat), b + n * (ba + flat) }), Frame3.Identity));
            bends.Add(new Bend(id, pid, pnew, new Line2(a + n * (ba / 2), (a - b).Normalized()), angle, f.Direction, R, ba, r.Toolset, source)
                { EdgeA = i, EdgeB = 0 });
        }
        Finish(ctx, sb, g with { Panels = panels, Bends = bends }, st.World, rules, bendRules);
    }

    static string Words(FlangeReference r) => r switch
    {
        FlangeReference.OutsideMoldLine => "to the outside mold line",
        FlangeReference.InsideMoldLine => "to the inside mold line",
        _ => "to the tangent line",
    };

    /// <summary>The outline with edge <paramref name="i"/> moved in by <paramref name="d"/> (its neighbours shortened to meet it).</summary>
    static Polygon2 MoveEdge(Polygon2 poly, int i, double d, string what)
    {
        var o = poly.Outer.ToList();
        int m = o.Count, prev = (i + m - 1) % m, next = (i + 1) % m, after = (i + 2) % m;
        var dir = (o[next] - o[i]).Normalized();
        var inward = dir.Left;
        var p0 = o[i] + inward * d;
        Vec2? Meet(Vec2 a, Vec2 b)
        {
            // the moved edge's line with the line a→b
            var e = b - a;
            var den = Vec2.Cross(dir, e);
            if (Math.Abs(den) < 1e-12) return null;
            var s = Vec2.Cross(a - p0, e) / den;
            return p0 + dir * s;
        }
        var na = Meet(o[prev], o[i]);
        var nb = Meet(o[next], o[after]);
        if (na == null || nb == null) throw Error($"{what}: its neighbouring edges run along it.");
        if (Vec2.Dot(nb.Value - na.Value, dir) <= 1e-9) throw Error($"{what}: the panel is too small for this flange's setback.");
        o[i] = na.Value; o[next] = nb.Value;
        return poly with { Outer = o };
    }

    static (double, AllowanceSource) Allowance(SheetRules r, double angle, BendId id)
    {
        try { return r.Allowance(angle); }
        catch (ProfileMissException ex) { throw Error($"{id}: {ex.Message}"); }
    }

    /// <summary>
    /// The body as the feature leaves it: re-rooted at its fixed panel (when it names one the graph has; the part does
    /// not move), its solid made again (folded, or flat when it is shown flattened).
    /// </summary>
    static void Finish(FeatureContext ctx, SheetMetalBody sb, BendGraph g, Frame3 world, SheetRules rules, Dictionary<BendId, SheetRules> bendRules)
    {
        try
        {
            if (sb.FixedPanel is { } fp && g.Panels.Any(p => p.Id.Value == fp) && g.FixedPanel.Value != fp)
            {
                var place = g.Fold().Panel(new PanelId(fp)).FoldedFrame;
                world = world.Compose(place);
                g = g with { FixedPanel = new PanelId(fp) };
            }
            var body = FoldedSolid.Build(g, world, sb.Flattened);
            var state = new SheetState(sb.Id, g, rules, world) { BendRules = bendRules };
            ctx.State = ctx.State.WithSheet(state, NTess.ToSolid(body));
        }
        catch (BendGraphException ex) { throw Error(ex.Message); }
    }

    static Vec3 V(Kernel.Vec3 v) => new(v.X, v.Y, v.Z);
}

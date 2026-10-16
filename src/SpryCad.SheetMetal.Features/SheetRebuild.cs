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
        Finish(ctx, f, sb, graph, world, rules, bendRules);
    }

    static string Name(SheetMetalBody b) => string.IsNullOrEmpty(b.Name) ? b.Id : b.Name;

    /// <summary>A plate: the sketch's one closed profile, the sketch plane the top side, the material below it.</summary>
    static (BendGraph, Frame3) Plate(BaseFlangeFeature f, SketchResult sk, SheetRules rules)
    {
        if (sk.Regions.Count > 1) throw Error("The sketch has more than one closed profile: a base flange takes one.");
        var region = sk.Regions[0].Normalized();
        var outer = Loop(region.Outer);
        var holes = region.Holes.Select(Loop).ToList();
        var outline = new Polygon2(outer.points, holes.Select(h => (IReadOnlyList<Vec2>)h.points).ToList())
        {
            OuterBulge = outer.bulges, HoleBulges = holes.Select(h => (IReadOnlyList<double>?)h.bulges).ToList(),
        };
        var panel = new Panel(new PanelId(f.Id + ".P0"), outline, Frame3.Identity);
        var fr = sk.Frame;
        var world = new Frame3(V(fr.XDir), V(fr.YDir), V(fr.Normal), V(fr.Origin));
        // the material above the sketch plane: the top side a thickness up
        if (f.ReverseSide) world = world with { Origin = world.Origin + world.Z * rules.Thickness };
        return (new BendGraph(rules.Thickness, new[] { panel }, Array.Empty<Bend>(), panel.Id), world);
    }

    /// <summary>
    /// A sketch profile loop as an outline loop: its straight edges, and its arcs as true arcs again (a profile carries
    /// an arc as facets marked with it: a run of them is one arc; a whole circle two halves). Splines are refused.
    /// </summary>
    public static (List<Vec2> points, List<double> bulges) Loop(Kernel.ProfileLoop l)
    {
        if (l.Verts.Any(v => v.Spline != null)) throw Error("A sheet's outline cannot have splines: use lines and arcs.");
        var v = l.Verts;
        int n = v.Count;
        Vec2 P(int i) => new(v[(i % n + n) % n].P.X, v[(i % n + n) % n].P.Y);
        var points = new List<Vec2>();
        var bulges = new List<double>();
        if (v.All(x => x.Arc != null && x.Arc == v[0].Arc))
        {
            // a whole circle: two half circles, the same way round as the loop
            var c = new Vec2(v[0].Arc!.Center.X, v[0].Arc!.Center.Y);
            var sign = Math.Sign(Polygon2.LoopArea(Enumerable.Range(0, n).Select(P).ToList()));
            var a = P(0);
            points.Add(a); points.Add(c * 2 - a);
            bulges.Add(sign); bulges.Add(sign);
            return (points, bulges);
        }
        // start where a run starts (a straight edge, or the first facet of an arc after another kind of edge)
        int start = Enumerable.Range(0, n).First(i => v[i].Arc == null || v[(i + n - 1) % n].Arc != v[i].Arc);
        for (int k = 0; k < n;)
        {
            int i = (start + k) % n;
            var arc = v[i].Arc;
            if (arc == null) { points.Add(P(i)); bulges.Add(0); k++; continue; }
            // the run of facets of this arc: its sweep summed facet by facet
            var c = new Vec2(arc.Center.X, arc.Center.Y);
            double sweep = 0;
            int j = k;
            while (j < n && v[(start + j) % n].Arc == arc)
            {
                Vec2 p = P(start + j) - c, q = P(start + j + 1) - c;
                sweep += Math.Atan2(Vec2.Cross(p, q), Vec2.Dot(p, q));
                j++;
            }
            points.Add(P(i));
            bulges.Add(Math.Tan(sweep / 4));
            k = j;
        }
        return (points, bulges);
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
        var edges = Edges(f.Edges);
        for (int k = 0; k < edges.Count; k++)
        {
            var (pid, i) = edges[k];
            var (pi, next) = TakeEdge(panels, bends, edges, k, f.Edges[k]);
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
            var (ba, source) = Allowance(r, angle, id);
            var flat = BendMath.FlatContribution(length, f.Reference, angle, R, t);
            if (flat <= 1e-9) throw Error($"The flange is too short for its bend: at least {length - flat:0.###} mm measured {Words(f.Reference)}.");
            AddFlange(panels, bends, pi, i, next, id, new PanelId($"{f.Id}.{k}.P"), angle, f.Direction, R, ba, flat, r.Toolset, source, hem: false);
        }
        Finish(ctx, f, sb, g with { Panels = panels, Bends = bends }, st.World, rules, bendRules);
    }

    static List<(PanelId panel, int edge)> Edges(List<string> names) =>
        names.Select(e => FoldedSolid.ParseEdge(e) ?? throw Error($"\"{e}\" is not a sheet metal edge.")).ToList();

    /// <summary>
    /// Edge <paramref name="k"/> of a feature checked as a place for a bend: in the part, straight, free, and not
    /// beside the bend its panel hangs from. The panel's index and the edge's next one.
    /// </summary>
    static (int pi, int next) TakeEdge(List<Panel> panels, List<Bend> bends, List<(PanelId panel, int edge)> edges, int k, string name)
    {
        var (pid, i) = edges[k];
        int pi = panels.FindIndex(p => p.Id == pid);
        if (pi < 0) throw Error($"The edge {name} is not in the part any more.");
        int m = panels[pi].FlatOutline.Outer.Count;
        if (i < 0 || i >= m) throw Error($"The edge {name} is not in the part any more.");
        if (bends.Any(b => (b.A == pid && b.EdgeA == i) || (b.B == pid && b.EdgeB == i)) || edges.Count(e => e.panel == pid && e.edge == i) > 1)
            throw Error($"{name} already has a bend.");
        if (panels[pi].FlatOutline.Bulge(0, i) != 0) throw Error($"{name} is round: a flange or hem goes on a straight edge.");
        // bends on a panel's edges that meet make a corner (SheetCorners); one beside the bend its panel hangs from cannot be made yet
        int prev = (i + m - 1) % m, next = (i + 1) % m;
        if (bends.Any(b => b.B == pid && (b.EdgeB == prev || b.EdgeB == next)))
            throw Error($"{name} is beside the bend its panel hangs from: a flange or hem there cannot be made yet.");
        return (pi, next);
    }

    /// <summary>A bend on panel <paramref name="pi"/>'s edge i→next and its panel beyond, <paramref name="flat"/> deep, the whole edge long.</summary>
    static void AddFlange(List<Panel> panels, List<Bend> bends, int pi, int i, int next, BendId id, PanelId pnew, double angle, BendDirection dir,
        double R, double ba, double flat, string toolset, AllowanceSource source, bool hem)
    {
        var outer = panels[pi].FlatOutline.Outer;
        Vec2 a = outer[i], b = outer[next];
        var n = new Vec2((b - a).Y, -(b - a).X).Normalized();
        panels.Add(new Panel(pnew, new Polygon2(new[] { b + n * ba, a + n * ba, a + n * (ba + flat), b + n * (ba + flat) }), Frame3.Identity));
        bends.Add(new Bend(id, panels[pi].Id, pnew, new Line2(a + n * (ba / 2), (a - b).Normalized()), angle, dir, R, ba, toolset, source)
            { EdgeA = i, EdgeB = 0, IsHem = hem });
    }

    // ---- hem ----

    public static void Hem(HemFeature f, FeatureContext ctx)
    {
        var sb = BodyOf(ctx, f.Body);
        if (!ctx.State.Sheets.TryGetValue(sb.Id, out var st)) throw Error($"{Name(sb)} has no base flange before this hem.");
        if (f.Edges.Count == 0) throw Error("Select the edges to hem.");
        if (f.Type == HemType.Rolled) throw Error("Rolled hems cannot be made yet: use closed, open or teardrop.");
        var g = st.Graph;
        var rules = st.Rules;
        double t = g.Thickness;
        var type = f.Type.ToString();
        double Rule(string col, double r)
        {
            try { return rules.Rule(ProfileTables.Hems, col, r, type); }
            catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { throw Error($"SM-HEM-03: {ex.Message}"); }
        }
        string RuleText(string col)
        {
            try { return rules.RuleText(ProfileTables.Hems, col, type); }
            catch (ProfileMissException ex) { throw Error($"SM-HEM-03: {ex.Message}"); }
        }
        if (RuleText("allowed") != "1") throw Error($"SM-HEM-03: the shop profile does not allow {type.ToLowerInvariant()} hems in {rules.Material} {rules.Gauge}.");
        var length = ctx.Length(f.Length, "Length");
        if (length <= 0) throw Error("The length must be greater than zero.");
        // the hem's inside radius in the folded solid and its angle: closed, the layers the modelling gap apart (in
        // contact for the flat and the checks); open, half the gap; a teardrop as given, past 180° by its angle
        double R, angle;
        switch (f.Type)
        {
            case HemType.Closed: R = Rule("model gap", 0) / 2; angle = Math.PI; break;
            case HemType.Open:
                var gap = string.IsNullOrWhiteSpace(f.Gap) ? Rule("gap", 0) : ctx.Length(f.Gap, "Gap");
                if (gap <= 0) throw Error("An open hem's gap must be greater than zero (a closed hem has none).");
                R = gap / 2; angle = Math.PI; break;
            default:
                R = string.IsNullOrWhiteSpace(f.Radius) ? t : ctx.Length(f.Radius, "Radius");
                var past = ctx.Angle(f.Angle, "Angle");
                if (!(past > 0 && past < 90)) throw Error("A teardrop's angle must be more than 0° and less than 90°.");
                R = Math.Max(R, 1e-3); angle = Math.PI + past * Math.PI / 180; break;
        }
        if (R <= 0) throw Error($"SM-HEM-03: the shop profile gives {type.ToLowerInvariant()} hems no modelling gap: the folded solid needs one.");
        var ba = Rule("allowance", f.Type == HemType.Closed ? 0 : R);
        var source = new AllowanceSource(AllowanceMethod.BendTable, $"Hem allowance ({type}, {rules.Material} {rules.Gauge})");
        // the return, from the fold's outside (R + t beyond the bend's start)
        var flat = length - (R + t);
        if (flat <= 1e-9) throw Error($"The hem is too short: at least {R + t:0.###} mm (its fold).");
        double min = Rule("min length", R), max = Rule("max length", R);
        var warnings = new List<string>();
        if (length < min - 1e-9 || length > max + 1e-9)
            warnings.Add($"SM-HEM-03: a {type.ToLowerInvariant()} hem in {rules.Material} {rules.Gauge} is {min:0.###} to {max:0.###} mm long by the shop's rule; this one is {length:0.###} mm.");

        var panels = g.Panels.ToList();
        var bends = g.Bends.ToList();
        var edges = Edges(f.Edges);
        for (int k = 0; k < edges.Count; k++)
        {
            var (_, i) = edges[k];
            var (pi, next) = TakeEdge(panels, bends, edges, k, f.Edges[k]);
            if (f.MaterialInside) panels[pi] = panels[pi] with { FlatOutline = MoveEdge(panels[pi].FlatOutline, i, R + t, f.Edges[k]) };
            AddFlange(panels, bends, pi, i, next, new BendId($"{f.Id}.{k}.B"), new PanelId($"{f.Id}.{k}.P"), angle, f.Direction, R, ba, flat, "hem", source, hem: true);
        }
        var graph = g with { Panels = panels, Bends = bends };
        // a teardrop's return must stay clear of the panel it folds back over (SM-HEM-01)
        try
        {
            var folded = graph.Fold();
            foreach (var b in bends.Where(b => b.Id.Value.StartsWith(f.Id + ".", StringComparison.Ordinal)))
                if (SheetChecks.HemClearance(folded, b) is { } clash)
                    throw Error("SM-HEM-01: " + Named(ctx.Doc, clash.message.Replace(" past the bend", $" past the bend (a length of {clash.maxFlat + R + t:0.###} mm)")));
        }
        catch (BendGraphException ex) { throw Error(Named(ctx.Doc, ex.Message)); }
        foreach (var w in warnings) ctx.Warn(w);
        Finish(ctx, f, sb, graph, st.World, rules, new Dictionary<BendId, SheetRules>(st.BendRules));
    }

    // ---- cut ----

    /// <summary>
    /// Holes from the sketch's closed profiles, through every panel parallel to the sketch that one lies inside (mapped
    /// into the panel's flat). A profile that crosses a panel's edge, or lies on no panel, is said.
    /// </summary>
    public static void Cut(SheetCutFeature f, FeatureContext ctx)
    {
        var sb = BodyOf(ctx, f.Body);
        if (!ctx.State.Sheets.TryGetValue(sb.Id, out var st)) throw Error($"{Name(sb)} has no base flange before this cut.");
        if (!ctx.State.Sketches.TryGetValue(f.SketchId, out var sk)) throw Error("The cut's sketch is missing.");
        if (sk.Regions.Count == 0) throw Error("The cut's sketch has no closed profile.");
        var g = st.Graph;
        BendGraph folded;
        try { folded = g.Fold(); } catch (BendGraphException ex) { throw Error(ex.Message); }
        var fr = sk.Frame;
        var skNormal = V(fr.Normal);
        var panels = g.Panels.ToList();
        var own = new List<string>();
        int profile = 0;
        foreach (var region in sk.Regions)
        {
            profile++;
            if (region.Holes.Count > 0) throw Error($"Profile {profile} has an island inside it: a cut cannot leave loose material.");
            var (pts, bulges) = Loop(region.Normalized().Outer);
            bool cutAny = false;
            for (int pi = 0; pi < panels.Count; pi++)
            {
                // the panel's place in the part, and the sketch's plane parallel to it
                var place = st.World.Compose(folded.Panel(panels[pi].Id).FoldedFrame);
                var dot = Vec3.Dot(place.Z, skNormal);
                if (Math.Abs(Math.Abs(dot) - 1) > 1e-9) continue;
                // the profile in the panel's flat (seen from the other side: mirrored, its arcs the other way round)
                var local = pts.Select(p => ToFlat(place, V(fr.ToWorld(p.X, p.Y, 0)))).ToList();
                var lb = bulges.Select(b => dot > 0 ? b : -b).ToList();
                var cut = new Polygon2(local) { OuterBulge = lb };
                var panel = panels[pi].FlatOutline;
                var straight = cut.Straightened();
                var shape = panel.Straightened();
                bool inside = straight.Outer.All(x => SheetChecks.Inside(shape, x));
                bool crosses = SheetChecks.Overlap(straight, shape) && !inside;
                if (!inside)
                {
                    if (crosses) throw Error($"Profile {profile} crosses {panels[pi].Id}'s edge: cuts across a panel's edge or a bend cannot be made yet.");
                    continue;
                }
                // a hole runs clockwise (the material on its left)
                if (cut.LoopArea(0) > 0) { local.Reverse(); lb = Enumerable.Range(0, lb.Count).Select(j => -lb[lb.Count - 1 - j]).ToList(); }
                var holes = panel.Holes.Append(local).ToList();
                var holeBulges = Enumerable.Range(1, panel.LoopCount - 1).Select(h => panel.Bulges(h)).Append(lb).ToList();
                panels[pi] = panels[pi] with { FlatOutline = panel with { Holes = holes, HoleBulges = holeBulges } };
                own.Add($"{panels[pi].Id}.Hole{holes.Count - 1}");
                cutAny = true;
            }
            if (!cutAny) throw Error($"Profile {profile} lies on no panel: sketch it on a plane parallel to a panel, inside it.");
        }
        Finish(ctx, f, sb, g with { Panels = panels }, st.World, st.Rules, new Dictionary<BendId, SheetRules>(st.BendRules), own);
    }

    /// <summary>A point of the part in a panel's flat (its place's inverse; the height dropped).</summary>
    static Vec2 ToFlat(Frame3 place, Vec3 p)
    {
        var d = p - place.Origin;
        return new Vec2(Vec3.Dot(d, place.X), Vec3.Dot(d, place.Y));
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
        if (poly.Bulge(0, (i + o.Count - 1) % o.Count) != 0 || poly.Bulge(0, (i + 1) % o.Count) != 0)
            throw Error($"{what}: a round edge meets it, which its setback cannot move yet (use Bend outside).");
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
        if (na == null || nb == null) throw Error($"{what}: its neighboring edges run along it.");
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
    /// The body as the feature leaves it: its flanges fitted to their edges and their corners made (by the profile's
    /// corner rules and the part's corner overrides), checked, re-rooted at its fixed panel (when it names one the
    /// graph has; the part does not move), its solid made again (folded, or flat when it is shown flattened). The
    /// checks that fail on what this feature made are its warnings.
    /// </summary>
    static void Finish(FeatureContext ctx, Feature f, SheetMetalBody sb, BendGraph g, Frame3 world, SheetRules rules, Dictionary<BendId, SheetRules> bendRules,
        IReadOnlyCollection<string>? own = null)
    {
        var checks = new List<RuleResult>();
        try
        {
            g = SheetCorners.Apply(g, (id, b1, b2) => CornerSpecOf(ctx.Doc, rules, id, b1), checks);
            checks.AddRange(SheetChecks.Flat(g));
            SheetRules Of(Bend b) => bendRules.TryGetValue(b.Id, out var r) ? r : rules;
            try
            {
                checks.AddRange(SheetChecks.Distances(g, b => Of(b).Rule(ProfileTables.Distances, "hole to bend", b.InsideRadius), rules.Rule(ProfileTables.Distances, "hole to edge")));
                checks.AddRange(SheetChecks.MinFlange(g, b => Of(b).MinFlange));
                if (rules.GrainSensitive && sb.GrainAngle is { } grain) checks.AddRange(SheetChecks.Grain(g, grain, rules.MinGrainAngle));
            }
            catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { ctx.Warn($"A check could not be made: {ex.Message}"); }
            foreach (var c in checks.Where(c => !c.Passed && (c.Entity.Contains(f.Id + ".", StringComparison.Ordinal) || own?.Contains(c.Entity) == true)))
                ctx.Warn($"{c.Rule}: {Named(ctx.Doc, c.Message)}");
            if (sb.FixedPanel is { } fp && g.Panels.Any(p => p.Id.Value == fp) && g.FixedPanel.Value != fp)
            {
                var place = g.Fold().Panel(new PanelId(fp)).FoldedFrame;
                world = world.Compose(place);
                g = g with { FixedPanel = new PanelId(fp) };
            }
            var body = FoldedSolid.Build(g, world, sb.Flattened);
            var state = new SheetState(sb.Id, g, rules, world) { BendRules = bendRules, Checks = checks };
            ctx.State = ctx.State.WithSheet(state, NTess.ToSolid(body));
        }
        catch (BendGraphException ex) { throw Error(Named(ctx.Doc, ex.Message)); }
    }

    /// <summary>A message with the features' ids in the entities' ids ("Fx3k….0.B") said by the features' names.</summary>
    public static string Named(CadDocument doc, string message)
    {
        foreach (var feature in doc.Features)
            if (feature.Id.Length > 0 && message.Contains(feature.Id, StringComparison.Ordinal)) message = message.Replace(feature.Id, feature.Name);
        return message;
    }

    /// <summary>
    /// How a corner is made: the profile's corner rule for the sheet (treatment, gap at the first bend's radius), or the
    /// part's override for the corner (sheet metal spec section 7). The profile's gap is what the check holds it to.
    /// </summary>
    public static CornerSpec CornerSpecOf(CadDocument doc, SheetRules rules, CornerId id, Bend first)
    {
        try
        {
            var o = doc.SheetOverrides?.FirstOrDefault(x => x.Target == id.Value);
            var r = o is { Values.Count: > 0 } ? rules.WithOverrides(o.Values) : rules;
            CornerTreatment Parse(string s) => Enum.TryParse<CornerTreatment>(s.Replace(" ", ""), true, out var c) ? c
                : throw new ProfileMissException(ProfileTables.Corners, $"\"{s}\" is not a corner treatment (Open, Closed, OverlapA, OverlapB).");
            var allowed = rules.RuleText(ProfileTables.Corners, "allowed").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();
            return new CornerSpec(Parse(r.RuleText(ProfileTables.Corners, "treatment")), r.Rule(ProfileTables.Corners, "gap", first.InsideRadius),
                rules.Rule(ProfileTables.Corners, "gap", first.InsideRadius), allowed, rules.RuleText(ProfileTables.Corners, "zero gap allowed") == "1", o != null);
        }
        catch (Exception ex) when (ex is ProfileMissException or RuleExprException) { throw Error($"Corner {id}: {ex.Message}"); }
    }

    static Vec3 V(Kernel.Vec3 v) => new(v.X, v.Y, v.Z);
}

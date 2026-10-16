using SpryCad.Core.Library;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>What holds a screw at its far end: a nut, or the thread of a tapped part; Auto: a tapped part if there is one.</summary>
public enum FastenerEnd { Auto, Nut, Tapped }

/// <summary>How Insert Fastener fastens a hole.</summary>
public sealed class FastenerOptions
{
    public FastenerEnd End { get; set; } = FastenerEnd.Auto;
    /// <summary>A washer under the nut (metric: ISO 7089).</summary>
    public bool NutWasher { get; set; } = true;
    /// <summary>A washer under the screw's head.</summary>
    public bool HeadWasher { get; set; }
    /// <summary>How much thread a tapped part takes, in screw diameters (1.5: safe in aluminium).</summary>
    public double Engagement { get; set; } = 1.5;
    /// <summary>The libraries to pick from (null: <see cref="LibraryLink.Current"/>).</summary>
    public LibrarySet? Set { get; set; }
}

/// <summary>
/// Where a fastener goes and what it takes (<see cref="AssemblyModel.PlanFastener"/>): the hole, the stack it clamps
/// along the hole's axis, and the screws offered for it with the one long enough picked.
/// </summary>
public sealed class FastenerPlan
{
    public required string Component { get; init; }
    public required string HoleFace { get; init; }
    public required HoleFeature Hole { get; init; }
    public required string Size { get; init; }
    public required string ScrewTable { get; init; }
    public string? NutTable { get; init; }
    public string? WasherTable { get; init; }
    /// <summary>Where the head bears (on the axis) and the direction the screw goes in (world).</summary>
    public required Vec3 Seat { get; init; }
    public required Vec3 Inward { get; init; }
    /// <summary>The face the head (or its washer) bears on, and the one the nut (or its washer) does.</summary>
    public required (string component, string face) SeatFace { get; init; }
    public required (string component, string face) FarFace { get; init; }
    /// <summary>The thickness clamped between the head and the nut, or the tapped part (mm).</summary>
    public required double Clamped { get; init; }
    /// <summary>The tapped part the screw threads into, if any.</summary>
    public string? Tapped { get; init; }
    public required FastenerEnd End { get; init; }
    public required FastenerOptions Options { get; init; }
    /// <summary>The length under the head the screw needs (mm), before rounding up to a standard length.</summary>
    public required double Needed { get; init; }
    /// <summary>The screws of the size the libraries offer (company restrictions applied), shortest first.</summary>
    public required List<LibraryRow> Screws { get; init; }
    /// <summary>The shortest offered screw long enough (null: none is).</summary>
    public LibraryRow? Screw { get; init; }
    public List<string> Notes { get; } = new();
}

/// <summary>What Insert Fastener added.</summary>
public sealed record InsertedFastener(Component Screw, Component? Nut, List<Component> Washers, List<Mate> Mates, string Designation);

public sealed partial class AssemblyModel
{
    /// <summary>
    /// Plans a fastener for a hole sized from the library (<see cref="HoleFeature.Standard"/>, <see cref="HoleFeature.Size"/>)
    /// in a component: the screw size from the hole; the stack it clamps measured along the hole's axis through the
    /// components (from the face the head bears on, through the touching parts, to the last one or to a tapped part);
    /// its length that stack plus the nut (and washers) or the thread engagement, rounded up to the next standard
    /// length the libraries offer. <paramref name="end"/>: the end of the hole face the head goes at (as
    /// <see cref="MateRef.End"/>); null: the end with nothing in the way.
    /// </summary>
    public FastenerPlan PlanFastener(string componentId, string holeFace, FastenerOptions? options = null, int? end = null)
    {
        options ??= new FastenerOptions();
        var set = options.Set ?? LibraryLink.Current;
        if (Find(componentId) is not { Display: { } pd } occ || occ.Rel != null) throw new InvalidOperationException("Pick a hole in a part of this assembly.");
        var doc = pd.Part ?? pd.Doc ?? throw new InvalidOperationException($"{occ.Component.Name} is shown from its cache; resolve it to fasten its holes.");
        var hole = doc.Features.OfType<HoleFeature>().FirstOrDefault(h => holeFace.StartsWith(h.Id + ".", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Pick the wall of a hole made with the Hole Wizard.");
        if (string.IsNullOrEmpty(hole.Standard) || string.IsNullOrEmpty(hole.Size) || Fasteners.TablesFor(hole.Standard) is not { } tables)
            throw new InvalidOperationException($"{(string.IsNullOrEmpty(hole.Name) ? "The hole" : hole.Name)} was not sized from a clearance hole table, so its screw is not known.");
        if (FaceGeom(componentId, holeFace) is not { Kind: MateGeomKind.Axis } axis) throw new InvalidOperationException("Pick the round wall of the hole.");
        var size = hole.Size;
        var screws = Fasteners.Offered(set, tables.screws, size);
        if (screws.Count == 0) throw new InvalidOperationException($"No {size} screws are offered ({tables.screws}): the company library leaves them out.");
        var nutRow = tables.nuts != null ? Fasteners.Offered(set, tables.nuts, size).FirstOrDefault() : null;
        var washerRow = tables.washers != null ? Fasteners.Offered(set, tables.washers, size).FirstOrDefault() : null;
        var screwTable = set.Table(tables.screws)!;
        double Col(LibraryRow r, LibraryTable t, string c) => t.Column(c) is var i and >= 0 ? r.Values[i] : 0;
        double d = Col(screws[0], screwTable, "d"), dk = Col(screws[0], screwTable, "dk"), pitch = Col(screws[0], screwTable, "p");

        // the hole's axis in the world, from the chosen end inward
        var t = occ.World;
        (Vec3 at, Vec3 inward) Axis(int e)
        {
            var a = FaceGeom(componentId, holeFace, e)!.P; var b = FaceGeom(componentId, holeFace, -e)!.P;
            return (t.Point(a), t.Vector(b - a).Normalized);
        }
        // probes parallel to the axis: one under the head (the stack it clamps), one between a tap drill's radius and the thread's (a tapped part has material there, a clearance hole none)
        double rHead = (axis.R + Math.Max(dk / 2, axis.R * 1.2)) / 2, rCore = 0.47 * d;
        if (end == null)
        {
            // the end with nothing in the way of the head (a tie: the counterbore's, else the upper one)
            int Blocked(int e)
            {
                var (at, inward) = Axis(e);
                return Probe(at, inward, rHead, 0.37).Count(iv => iv.b > -dk && iv.a < -0.05);
            }
            int bp = Blocked(1), bn = Blocked(-1);
            if (bp != bn) end = bp < bn ? 1 : -1;
            else if (FaceGeom(componentId, CounterboreFace(holeFace), null) is { } cb)
                end = Vec3.Distance(t.Point(cb.P), Axis(1).at) <= Vec3.Distance(t.Point(cb.P), Axis(-1).at) ? 1 : -1;
            else end = Axis(1).inward.Z <= Axis(-1).inward.Z ? 1 : -1;
        }
        var (start, dir) = Axis(end.Value);

        // the stack: from the material at the start of the hole, through the parts touching along the axis
        Stack? stack = null;
        foreach (var angle in new[] { 0.37, 2.46, 4.55 })
            if ((stack = Measure(start, dir, rHead, rCore, angle)) != null) break;
        if (stack == null) throw new InvalidOperationException("Nothing found around the hole for the screw's head to bear on.");
        var s = stack.Value;

        var kind = options.End switch
        {
            FastenerEnd.Auto => s.tapped != null || nutRow == null ? FastenerEnd.Tapped : FastenerEnd.Nut,
            var k => k,
        };
        if (kind == FastenerEnd.Nut && nutRow == null) throw new InvalidOperationException($"No {size} nuts are offered, so this screw must go into a tapped part.");
        double hw = washerRow != null ? Col(washerRow, set.Table(tables.washers!)!, "h") : 0;
        double headW = options.HeadWasher && washerRow != null ? hw : 0;
        double clamped, needed;
        (string, string) far;
        string? tapped = null;
        var notes = new List<string>();
        if (kind == FastenerEnd.Nut)
        {
            clamped = s.end - s.seat;
            far = s.endFace;
            var m = Col(nutRow!, set.Table(tables.nuts!)!, "m");
            needed = headW + clamped + (options.NutWasher && washerRow != null ? hw : 0) + m + 2 * pitch;
            if (s.tapped != null) notes.Add($"{Find(s.tapped)?.Component.Name} is threaded, yet a nut was asked for: the screw goes through it.");
        }
        else
        {
            tapped = s.tapped;
            clamped = (s.tapped != null ? s.tappedAt : s.end) - s.seat;
            far = s.tapped != null ? s.tappedFace : s.endFace;
            needed = headW + clamped + options.Engagement * d;
            if (s.tapped == null) notes.Add("No tapped part found past the stack: the length allows for one there.");
            else if (s.threadDepth < options.Engagement * d - 1e-6)
                notes.Add($"{Find(s.tapped)?.Component.Name} has only {Expr.Format(Math.Round(s.threadDepth, 2))} mm of thread for {Expr.Format(Math.Round(options.Engagement * d, 2))} mm of engagement.");
        }
        var lCol = screwTable.Column("l");
        var screw = screws.FirstOrDefault(r => r.Values[lCol] >= needed - 1e-6);
        if (screw == null) notes.Add($"No {size} screw offered is long enough: {Expr.Format(Math.Round(needed, 2))} mm needed, {Expr.Format(screws[^1].Values[lCol])} mm the longest.");
        var plan = new FastenerPlan
        {
            Component = componentId, HoleFace = holeFace, Hole = hole, Size = size,
            ScrewTable = tables.screws, NutTable = tables.nuts, WasherTable = tables.washers,
            Seat = start + dir * s.seat, Inward = dir, SeatFace = s.seatFace, FarFace = far, Clamped = clamped, Tapped = tapped,
            End = kind, Options = options, Needed = needed, Screws = screws, Screw = screw,
        };
        plan.Notes.AddRange(notes);
        return plan;
    }

    static string CounterboreFace(string holeFace) =>
        holeFace.EndsWith(".R.Hole", StringComparison.Ordinal) ? holeFace[..^".R.Hole".Length] + ".R.CB" : holeFace + ".none";

    /// <summary>
    /// Adds the fastener a plan found (or the screw picked from its <see cref="FastenerPlan.Screws"/>): the screw,
    /// its washers and nut, each a part made from its row and embedded in the assembly (one part per row, however many
    /// use it, keeping its snapshot of the row), placed on the hole and mated to it: concentric with the hole and
    /// coincident with the faces they bear on. The mates already hold, so nothing else moves.
    /// </summary>
    public InsertedFastener InsertFastener(FastenerPlan plan, string? screwRow = null)
    {
        var set = plan.Options.Set ?? LibraryLink.Current;
        var screwKey = screwRow ?? plan.Screw?.Key ?? throw new InvalidOperationException(plan.Notes.LastOrDefault() ?? "No screw is long enough.");
        LibrarySnapshot Snap(string table, string row) => set.Snapshot(LibrarySnapshot.ItemKey(table, row)) ?? throw new InvalidOperationException($"No library has {row} ({table}).");
        var screw = Snap(plan.ScrewTable, screwKey);
        var washerRow = plan.WasherTable != null ? Fasteners.Offered(set, plan.WasherTable, plan.Size).FirstOrDefault() : null;
        var washer = washerRow != null ? Snap(plan.WasherTable!, washerRow.Key) : null;
        var nutRow = plan.End == FastenerEnd.Nut && plan.NutTable != null ? Fasteners.Offered(set, plan.NutTable, plan.Size).FirstOrDefault() : null;
        var nut = nutRow != null ? Snap(plan.NutTable!, nutRow.Key) : null;
        double hw = washer?.Value("h") ?? 0;

        // placements: a fastener's axis is its Z; a screw's head is up (out of the hole), a nut's and a washer's bearing face down
        var outward = -plan.Inward;
        Transform Along(Vec3 origin, Vec3 z)
        {
            var x = Vec3.Cross(Math.Abs(z.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX, z).Normalized;
            var y = Vec3.Cross(z, x);
            return new Transform(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z, origin);
        }
        Component Add(LibrarySnapshot s, Transform at)
        {
            var part = Fasteners.Part(s, set);
            return InsertEmbedded(part, Fasteners.Of(part)!.Designation, at);
        }
        var mates = new List<Mate>();
        MateRef Ref(string component, string face) => RefTo(component, face) ?? throw new InvalidOperationException($"{Find(component)?.Component.Name} has no face {face} to mate to.");
        void Fix(MateType type, MateRef a, MateRef b)
        {
            var m = new Mate { Type = type, A = a, B = b };
            Mate.Normalize(m);
            m.Capture(World(a), World(b), PlacementOf(a), PlacementOf(b));
            mates.Add(Doc.AddMate(m));
        }
        var hole = Ref(plan.Component, plan.HoleFace);
        var washers = new List<Component>();

        // under the head
        var seat = Ref(plan.SeatFace.component, plan.SeatFace.face);
        double lift = 0;
        if (plan.Options.HeadWasher && washer != null)
        {
            var w = Add(washer, Along(plan.Seat, outward));
            washers.Add(w);
            Fix(MateType.Concentric, Ref(w.Id, Fasteners.WasherBore), hole);
            Fix(MateType.Coincident, Ref(w.Id, Fasteners.WasherBottom), seat);
            seat = Ref(w.Id, Fasteners.WasherTop);
            lift = hw;
        }
        var sc = Add(screw, Along(plan.Seat + outward * lift, outward));
        Fix(MateType.Concentric, Ref(sc.Id, Fasteners.ScrewShank), hole);
        Fix(MateType.Coincident, Ref(sc.Id, Fasteners.ScrewSeat), seat);

        // the far end
        Component? n = null;
        if (nut != null)
        {
            var at = plan.Seat + plan.Inward * plan.Clamped;
            var bearing = Ref(plan.FarFace.component, plan.FarFace.face);
            if (plan.Options.NutWasher && washer != null)
            {
                var w = Add(washer, Along(at, plan.Inward));
                washers.Add(w);
                Fix(MateType.Concentric, Ref(w.Id, Fasteners.WasherBore), hole);
                Fix(MateType.Coincident, Ref(w.Id, Fasteners.WasherBottom), bearing);
                bearing = Ref(w.Id, Fasteners.WasherTop);
                at += plan.Inward * hw;
            }
            n = Add(nut, Along(at, plan.Inward));
            Fix(MateType.Concentric, Ref(n.Id, Fasteners.NutBore), hole);
            Fix(MateType.Coincident, Ref(n.Id, Fasteners.NutBearing), bearing);
        }
        return new InsertedFastener(sc, n, washers, mates, Fasteners.Designation(screw, set));
    }

    /// <summary><see cref="PlanFastener"/> and <see cref="InsertFastener(FastenerPlan, string?)"/> in one.</summary>
    public InsertedFastener InsertFastener(string componentId, string holeFace, FastenerOptions? options = null, int? end = null) =>
        InsertFastener(PlanFastener(componentId, holeFace, options, end));

    // ------------------------------------------------------------------------------------------
    // measuring along a hole

    /// <summary>
    /// The stack along an axis: where the material the head bears on starts (<c>seat</c>) and the touching material
    /// ends, with the faces there; the first part past the seat with material inside the thread (a tapped part), where
    /// its material starts and how deep it goes. Distances along <paramref name="dir"/> from <paramref name="start"/>.
    /// </summary>
    record struct Stack(double seat, (string, string) seatFace, double end, (string, string) endFace,
        string? tapped, double tappedAt, (string, string) tappedFace, double threadDepth);

    Stack? Measure(Vec3 start, Vec3 dir, double rHead, double rCore, double angle)
    {
        const double gap = 0.05;
        var head = Probe(start, dir, rHead, angle);
        // the material the head bears on: at the start of the hole (a counterbore's floor, or the face around it)
        var first = head.FirstOrDefault(iv => iv.b > 0.01);
        if (first.comp == null || first.a < -0.5 || first.a > 0.5) return null;
        double end = first.b; var endFace = (first.comp, first.outFace);
        var run = new List<(double a, double b, string comp, string inFace, string outFace)> { first };
        foreach (var iv in head.Where(iv => iv.a >= first.a && !iv.Equals(first)))
        {
            if (iv.a > end + gap) break;
            run.Add(iv);
            if (iv.b > end) { end = iv.b; endFace = (iv.comp, iv.outFace); }
        }
        // a tapped part: material inside the thread's core where it lies in the stack
        var core = Probe(start, dir, rCore, angle);
        foreach (var iv in run.OrderBy(iv => iv.a))
        {
            var thread = core.Where(c => c.comp == iv.comp && c.b > iv.a + gap && c.a < iv.b - gap).ToList();
            if (thread.Count == 0) continue;
            var depth = thread.Sum(c => Math.Min(c.b, iv.b) - Math.Max(c.a, iv.a));
            return new Stack(first.a, (first.comp, first.inFace), end, endFace, iv.comp, iv.a, (iv.comp, iv.inFace), depth);
        }
        return new Stack(first.a, (first.comp, first.inFace), end, endFace, null, 0, default, 0);
    }

    /// <summary>
    /// Where a line parallel to an axis (at <paramref name="radius"/> from it, turned <paramref name="angle"/> about it)
    /// runs through the parts placed here (fasteners left out), as intervals along it with the faces it enters and
    /// leaves by, in order. Each part's mesh is closed, so its crossings pair up.
    /// </summary>
    List<(double a, double b, string comp, string inFace, string outFace)> Probe(Vec3 start, Vec3 dir, double radius, double angle)
    {
        var u = Vec3.Cross(Math.Abs(dir.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitX, dir).Normalized;
        var v = Vec3.Cross(dir, u);
        var origin = start + (u * Math.Cos(angle) + v * Math.Sin(angle)) * radius;
        var result = new List<(double, double, string, string, string)>();
        foreach (var (o, pd) in PlacedParts())
        {
            if (o.Rel != null || Fasteners.Of(pd.Part ?? pd.Doc) != null) continue;
            var inv = o.World.RigidInverse();
            var p = inv.Point(origin); var d = inv.Vector(dir);
            var mesh = pd.Mesh;
            if (!LineNearBox(mesh.Bounds, p, d)) continue;
            var hits = new List<(double t, string face)>();
            foreach (var f in mesh.Faces)
            {
                if (!LineNearBox(f.Bounds, p, d)) continue;
                for (int k = f.IndexStart; k + 2 < f.IndexStart + f.IndexCount + 0; k += 3)
                    if (Cross(mesh, k, p, d) is { } tt) hits.Add((tt, f.Name));
            }
            if (hits.Count < 2) continue;
            hits.Sort((x, y) => x.t.CompareTo(y.t));
            // (a crossing on an edge between triangles is found twice)
            var unique = new List<(double t, string face)>();
            foreach (var h in hits) if (unique.Count == 0 || h.t - unique[^1].t > 1e-5) unique.Add(h);
            for (int i = 0; i + 1 < unique.Count; i += 2)
                result.Add((unique[i].t, unique[i + 1].t, o.Path, unique[i].face, unique[i + 1].face));
        }
        result.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return result;
    }

    static bool LineNearBox(Box3 b, Vec3 p, Vec3 d)
    {
        if (b.IsEmpty) return false;
        const double pad = 1e-3;
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            double pi = i == 0 ? p.X : i == 1 ? p.Y : p.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            double mn = (i == 0 ? b.Min.X : i == 1 ? b.Min.Y : b.Min.Z) - pad, mx = (i == 0 ? b.Max.X : i == 1 ? b.Max.Y : b.Max.Z) + pad;
            if (Math.Abs(di) < 1e-12) { if (pi < mn || pi > mx) return false; continue; }
            double t0 = (mn - pi) / di, t1 = (mx - pi) / di;
            if (t0 > t1) (t0, t1) = (t1, t0);
            lo = Math.Max(lo, t0); hi = Math.Min(hi, t1);
            if (lo > hi) return false;
        }
        return true;
    }

    /// <summary>Where the line p + t d crosses the triangle at index k of the mesh (Möller–Trumbore; null: it does not).</summary>
    static double? Cross(DisplayMesh m, int k, Vec3 p, Vec3 d)
    {
        Vec3 V(int i) { var j = 3 * m.Indices[i]; return new Vec3(m.Positions[j], m.Positions[j + 1], m.Positions[j + 2]); }
        var a = V(k); var e1 = V(k + 1) - a; var e2 = V(k + 2) - a;
        var h = Vec3.Cross(d, e2);
        var det = Vec3.Dot(e1, h);
        if (Math.Abs(det) < 1e-12) return null;
        var f = 1 / det;
        var s = p - a;
        var uu = f * Vec3.Dot(s, h);
        if (uu < -1e-9 || uu > 1 + 1e-9) return null;
        var q = Vec3.Cross(s, e1);
        var vv = f * Vec3.Dot(d, q);
        if (vv < -1e-9 || uu + vv > 1 + 1e-9) return null;
        return f * Vec3.Dot(e2, q);
    }
}

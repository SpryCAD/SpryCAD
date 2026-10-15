using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SpryCad.Core.Model;
using SpryCad.Kernel;

namespace SpryCad.Core.Library;

public enum FastenerKind { Screw, Nut, Washer }

/// <summary>
/// Fasteners from the part family tables (section 25): a fastener is a part made from one library row, its body built
/// by a generator for the row's family (<see cref="Body"/>). Bodies are cached by the family's definition, the row's
/// values and the kernel's version (<see cref="BodyKey"/>), so every M8x30 screw, in any part or assembly, is one body.
/// <para>
/// Each body sits at its own origin with its axis along Z: a screw's under-head face at z = 0 with the head above it
/// and the shank below; a nut's and a washer's bearing face at z = 0 with the rest above.
/// </para>
/// </summary>
public static class Fasteners
{
    /// <summary>The row text naming the screw size a row is for ("M8", "1/4"), as clearance hole tables name their rows.</summary>
    public const string SizeKey = "size";

    /// <summary>
    /// Screw columns: thread major diameter "d" and pitch "p"; head diameter "dk" and height "k"; hex socket across
    /// flats "s" and depth "t"; thread length "b" and length under the head "l".
    /// </summary>
    public static readonly IReadOnlyList<string> ScrewColumns = new[] { "d", "p", "dk", "k", "s", "t", "b", "l" };
    /// <summary>Nut columns: thread "d" and pitch "p", width across flats "s", height "m".</summary>
    public static readonly IReadOnlyList<string> NutColumns = new[] { "d", "p", "s", "m" };
    /// <summary>Washer columns: bore "d1", outside diameter "d2", thickness "h".</summary>
    public static readonly IReadOnlyList<string> WasherColumns = new[] { "d1", "d2", "h" };

    /// <summary>Face names of the generated bodies (mates name these).</summary>
    public const string ScrewShank = "Screw.R.Shank", ScrewSeat = "Screw.R.Seat";
    public const string NutBore = "Nut.S.Bore", NutBearing = "Nut.Start", NutTop = "Nut.End";
    public const string WasherBore = "Washer.S.Bore", WasherBottom = "Washer.Start", WasherTop = "Washer.End";

    /// <summary>
    /// What each family's generator makes, versioned: part of every body's cache key, so a change to a generator
    /// (bump its version here) never shows a body it made before.
    /// </summary>
    static readonly IReadOnlyDictionary<FastenerKind, string> Definitions = new Dictionary<FastenerKind, string>
    {
        [FastenerKind.Screw] = "socket head cap screw v1: revolved shank (tip chamfer d/10) and head (top chamfer k/12), hex socket s x t cut",
        [FastenerKind.Nut] = "hex nut v1: hexagon s across flats, bore d, height m",
        [FastenerKind.Washer] = "plain washer v1: annulus d1..d2, thickness h",
    };

    public static FastenerKind? KindOf(string tableKind) => tableKind switch
    {
        TableKinds.Screw => FastenerKind.Screw,
        TableKinds.Nut => FastenerKind.Nut,
        TableKinds.Washer => FastenerKind.Washer,
        _ => null,
    };

    /// <summary>The kind of fastener a row is, by its columns (null: not a fastener family).</summary>
    public static FastenerKind? KindOf(LibrarySnapshot s) =>
        s.Columns.SequenceEqual(ScrewColumns) ? FastenerKind.Screw
        : s.Columns.SequenceEqual(NutColumns) ? FastenerKind.Nut
        : s.Columns.SequenceEqual(WasherColumns) ? FastenerKind.Washer
        : null;

    /// <summary>The screw size a row is for ("M8"), from its text.</summary>
    public static string? SizeOf(LibraryRow r) => r.Text?.GetValueOrDefault(SizeKey);
    public static string? SizeOf(LibrarySnapshot s) => s.Text?.GetValueOrDefault(SizeKey);

    // ------------------------------------------------------------------------------------------
    // which tables go with a clearance hole table

    /// <summary>The screw, nut and washer tables for screws in a clearance hole table's holes (null: none).</summary>
    public static (string screws, string? nuts, string? washers)? TablesFor(string clearanceTable) => clearanceTable switch
    {
        "ISO273" => ("ISO4762", "ISO4032", "ISO7089"),
        "ASME-B18.2.8" => ("ASME-B18.3-SHCS", null, null),
        _ => null,
    };

    /// <summary>
    /// The rows of a table of a size that the libraries offer (company restrictions applied), shortest first: what the
    /// fastener picker lists.
    /// </summary>
    public static List<LibraryRow> Offered(LibrarySet set, string table, string size)
    {
        var t = set.Offered(set.Table(table)?.Kind ?? "").FirstOrDefault(x => x.Key == table);
        if (t == null) return new();
        var l = t.Column("l");
        return t.Rows.Where(r => SizeOf(r) == size).OrderBy(r => l >= 0 ? r.Values[l] : 0).ToList();
    }

    /// <summary>The sizes of a table the libraries offer, in the table's order.</summary>
    public static List<string> OfferedSizes(LibrarySet set, string table) =>
        set.Offered(set.Table(table)?.Kind ?? "").FirstOrDefault(x => x.Key == table)?.Rows.Select(SizeOf).OfType<string>().Distinct().ToList() ?? new();

    /// <summary>How a fastener is named in a bill of materials: its standard and designation ("ISO 4762 M8x30").</summary>
    public static string Designation(LibrarySnapshot s, LibrarySet? set = null)
    {
        var standard = (set ?? LibraryLink.Current).Table(s.Table)?.Standard
            ?? FastenerFamilies.Tables.FirstOrDefault(t => t.Key == s.Table)?.Standard ?? s.Table;
        return standard + " " + s.Row;
    }

    // ------------------------------------------------------------------------------------------
    // bodies

    static readonly ConcurrentDictionary<string, Lazy<Solid>> Bodies = new(StringComparer.Ordinal);
    static int _built;

    /// <summary>How many bodies the generators have built (cache misses), for tests.</summary>
    public static int BodiesBuilt => _built;
    /// <summary>How many bodies are cached.</summary>
    public static int BodiesCached => Bodies.Count;

    /// <summary>
    /// The cache key of a row's body: the hash of its family's generator definition, the row's values (by column) and
    /// the kernel's version. Where the row came from (its library, table key, designation) is not part of it.
    /// </summary>
    public static string BodyKey(LibrarySnapshot s)
    {
        var kind = KindOf(s) ?? throw new ArgumentException($"{s.Item} is not a fastener row.");
        var sb = new StringBuilder();
        sb.Append(Definitions[kind]).Append('\n');
        for (int i = 0; i < s.Columns.Count; i++)
            sb.Append(s.Columns[i]).Append('=').Append(s.Values[i].ToString("R", CultureInfo.InvariantCulture)).Append(';');
        sb.Append("\ngeometry ").Append(GeometryVersion.Current);
        return Storage.Hash.Of(Encoding.UTF8.GetBytes(sb.ToString())).ToString();
    }

    /// <summary>The fastener's body (built once per <see cref="BodyKey"/>, then shared). Solids are not changed by their users.</summary>
    public static Solid Body(LibrarySnapshot s) => Bodies.GetOrAdd(BodyKey(s), _ => new Lazy<Solid>(() =>
    {
        Interlocked.Increment(ref _built);
        double V(string c) => s.Value(c) is { } v && v > 0 ? v : throw new Regen.FeatureException($"{s.Row} ({s.Table}) has no {c}.");
        return KindOf(s) switch
        {
            FastenerKind.Screw => Screw(V("d"), V("l"), V("dk"), V("k"), V("s"), V("t")),
            FastenerKind.Nut => Nut(V("d"), V("s"), V("m")),
            _ => Washer(V("d1"), V("d2"), V("h")),
        };
    })).Value;

    /// <summary>Empties the body cache (tests).</summary>
    public static void ClearCache() => Bodies.Clear();

    static Solid Screw(double d, double l, double dk, double k, double s, double t)
    {
        double r = d / 2, rk = dk / 2, c = d / 10, kc = k / 12;
        // (radius, height) with the under-head face at 0: tip, shank, under-head face, head, its top
        var prof = new ProfileLoop();
        void P(double x, double y, string n) => prof.Verts.Add(new ProfileVertex(new Vec2(x, y), n));
        P(0, -l, "Tip"); P(r - c, -l, "TipChamfer"); P(r, -l + c, "Shank"); P(r, 0, "Seat"); P(rk, 0, "Head");
        P(rk, k - kc, "HeadChamfer"); P(rk - kc, k, "Top"); P(0, k, "Axis");
        var frame = new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitZ, -Vec3.UnitY);
        var body = Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, "Screw");
        if (body.Native == null && body.Volume() < 0) body = body.Flipped();
        // the hex socket (left out if the cut fails: the screw is still the right size)
        if (t < k && s < dk)
        {
            var hex = Hexagon(s, "Socket.F");
            var tool = Builders.Extrude(new[] { new ProfileRegion(hex) }, Frame.World, k - t, k + 1, "Socket");
            try
            {
                var cut = Csg.Subtract(body, tool);
                if (!cut.IsEmpty) body = cut;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return body;
    }

    static Solid Nut(double d, double s, double m)
    {
        var region = new ProfileRegion(Hexagon(s, "Flat"), new[] { ProfileLoop.Circle(Vec2.Zero, d / 2, "Bore") });
        return Builders.Extrude(new[] { region }, Frame.World, 0, m, "Nut");
    }

    static Solid Washer(double d1, double d2, double h)
    {
        var region = new ProfileRegion(ProfileLoop.Circle(Vec2.Zero, d2 / 2, "Rim"), new[] { ProfileLoop.Circle(Vec2.Zero, d1 / 2, "Bore") });
        return Builders.Extrude(new[] { region }, Frame.World, 0, h, "Washer");
    }

    /// <summary>A hexagon of the width across flats, flats parallel to X.</summary>
    static ProfileLoop Hexagon(double acrossFlats, string name)
    {
        var rc = acrossFlats / Math.Sqrt(3);
        return ProfileLoop.Polygon(Enumerable.Range(0, 6).Select(i => Vec2.FromAngle(Math.PI / 6 + i * Math.PI / 3, rc)), name);
    }

    // ------------------------------------------------------------------------------------------
    // parts

    /// <summary>
    /// The part a fastener is: one <see cref="FastenerFeature"/> and the snapshot of its row (so it rebuilds with no
    /// library). Its identity comes from the row's data, so the same fastener made twice is the same part (an assembly
    /// embeds it once, however many components use it).
    /// </summary>
    public static CadDocument Part(LibrarySnapshot s, LibrarySet? set = null)
    {
        if (KindOf(s) == null) throw new ArgumentException($"{s.Item} is not a fastener row.");
        var doc = new CadDocument
        {
            Id = PartId(s).ToString("D"),
            Color = new[] { 0.30, 0.31, 0.33 },
            Material = "Steel",
        };
        var designation = Designation(s, set);
        doc.AddFeature(new FastenerFeature { Name = designation, Item = s.Item, Designation = designation });
        doc.Library = new(StringComparer.Ordinal) { [s.Item] = s };
        return doc;
    }

    /// <summary>A fastener part's identity: from its row's item and data (not where it came from).</summary>
    public static Guid PartId(LibrarySnapshot s)
    {
        var data = "fastener\n" + s.Item + "\n" + string.Join(",", s.Columns) + "\n"
            + string.Join(",", s.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "\n"
            + string.Join(";", (s.Text ?? new()).Select(kv => kv.Key + "=" + kv.Value));
        return new Guid(Storage.Hash.Of(Encoding.UTF8.GetBytes(data)).ToBytes().AsSpan(0, 16));
    }

    /// <summary>The fastener a part is (null: not one): its feature.</summary>
    public static FastenerFeature? Of(CadDocument? doc) => doc?.Features.Count == 1 ? doc.Features[0] as FastenerFeature : null;
}

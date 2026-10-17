using SpryCad.Core.Model;

namespace SpryCad.Core.Library;

/// <summary>An item whose library row differs from the part's snapshot of it (section 34, "Library update available").</summary>
public sealed record LibraryUpdate(string Item, LibrarySnapshot Old, LibrarySnapshot New)
{
    /// <summary>The changed values in words ("normal 9 → 9.2").</summary>
    public string Describe()
    {
        var items = new List<string>();
        foreach (var c in Old.Columns.Union(New.Columns))
        {
            var a = Old.Value(c); var b = New.Value(c);
            if (!a.Equals(b)) items.Add($"{c} {(a is { } x ? Expr.Format(x) : "—")} → {(b is { } y ? Expr.Format(y) : "—")}");
        }
        if (!(Old.Text ?? new()).SequenceEqual(New.Text ?? new())) items.Add("text changed");
        return $"{New.Name}: {string.Join(", ", items)}";
    }
}

/// <summary>
/// How a part uses library data (section 34): each feature names the items it uses (<see cref="Feature.LibraryItems"/>),
/// the part keeps a snapshot of each (<see cref="CadDocument.Library"/>), and rebuilds from the snapshot alone. An item
/// with no snapshot yet (a size just picked) comes from the libraries in use (<see cref="Current"/>).
/// </summary>
public static class LibraryLink
{
    /// <summary>The libraries in use (set by the app when it loads or reloads them; the built-in standards until then).</summary>
    public static LibrarySet Current { get; set; } = LibrarySet.BuiltIn;

    /// <summary>The item's row: the part's snapshot, else the libraries' (null: neither has it).</summary>
    public static LibrarySnapshot? Resolve(CadDocument doc, string item) =>
        doc.Library?.TryGetValue(item, out var s) == true ? s : Current.Snapshot(item);

    /// <summary>
    /// A part read from a file written before library v2 brought up to date: its snapshots re-keyed ("ISO273/M8" is
    /// "ISO273:M8") and given their kind (a material's values in its table's metric units), and the item keys it names
    /// (its material, its fasteners, design tables' $Material cells) re-keyed alike. Nothing for a present part.
    /// </summary>
    public static void Upgrade(CadDocument doc)
    {
        if (doc.Library != null)
        {
            var snaps = doc.Library.Values.ToList();
            foreach (var s in snaps) { s.UpgradeV1(); s.Table = RenamedTable(s.Table); ThreadSizes.UpgradeSnapshot(s); }
            doc.Library = new SortedDictionary<string, LibrarySnapshot>(snaps.ToDictionary(s => s.Item), StringComparer.Ordinal);
        }
        if (doc.MaterialItem != null) doc.MaterialItem = Renamed(LibrarySnapshot.Upgrade(doc.MaterialItem));
        foreach (var h in doc.Features.OfType<HoleFeature>()) HoleSizes.Upgrade(h, Current);
        // threads and tapped holes from before threads had classes: the class of the size they use
        foreach (var t in doc.Features.OfType<ThreadFeature>())
            if (t.Class == null && t.Size is not ("Auto" or "Custom") && !string.IsNullOrEmpty(t.Size)) t.Class = ThreadSizes.ClassFor(doc, ThreadSizes.TableOf(t), t.Size);
        foreach (var h in doc.Features.OfType<HoleFeature>())
            if (h.Type == HoleType.Tapped && h.ThreadClass == null && !string.IsNullOrEmpty(h.ThreadSize)) h.ThreadClass = ThreadSizes.ClassFor(doc, HoleSizes.ThreadTableOf(h), h.ThreadSize);
        foreach (var f in doc.Features.OfType<FastenerFeature>()) f.Item = LibrarySnapshot.Upgrade(f.Item);
        foreach (var t in doc.Tables ?? new())
            for (int c = 0; c < t.Columns.Count; c++)
                if (t.Columns[c] == TableConfig.Material)
                    foreach (var r in t.Rows)
                        if (c < r.Values.Count && r.Values[c].Length > 0) r.Values[c] = Renamed(LibrarySnapshot.Upgrade(r.Values[c]));
    }

    /// <summary>
    /// SpryCAD's tables renamed since parts named them: the material tables lost the "Materials-" their keys began with
    /// when materials became a library of their own.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RenamedTables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Materials-steel"] = "Steel", ["Materials-stainless"] = "Stainless", ["Materials-iron"] = "Iron", ["Materials-aluminum"] = "Aluminum",
        ["Materials-copper"] = "Copper", ["Materials-titanium"] = "Titanium", ["Materials-other-metals"] = "Other metals",
        ["Materials-plastics"] = "Plastics", ["Materials-elastomers"] = "Elastomers", ["Materials-composites"] = "Composites",
        ["Materials-wood"] = "Wood", ["Materials-ceramics-glass"] = "Ceramics and glass",
    };

    // a table's present key: renamed, unless the libraries in use still have it (a company library made before the
    // rename keeps its keys, and its parts with them)
    static string RenamedTable(string table) =>
        RenamedTables.TryGetValue(table, out var now) && Current.Table(table) == null ? now : table;

    static string Renamed(string item)
    {
        var (table, row) = LibrarySnapshot.Split(item);
        return RenamedTable(table) is var now && now != table ? LibrarySnapshot.ItemKey(now, row) : item;
    }

    /// <summary>
    /// Brings the part's snapshots in line with what its features use: a snapshot added for each item used that has
    /// none (from <paramref name="set"/>), removed for each no feature uses. True when anything changed.
    /// </summary>
    public static bool Sync(CadDocument doc, LibrarySet? set = null)
    {
        set ??= Current;
        var used = doc.Features.SelectMany(f => f.LibraryItems()).ToHashSet(StringComparer.Ordinal);
        if (doc.MaterialItem != null) used.Add(doc.MaterialItem);
        // a table a feature picks from when built: its rows in the libraries, and those the part already has (a size
        // the library no longer offers kept, as an item would be)
        foreach (var table in doc.Features.SelectMany(f => f.LibraryTables()).Distinct(StringComparer.Ordinal))
        {
            if (set.Table(table) is { } tb) foreach (var r in tb.Rows) used.Add(LibrarySnapshot.ItemKey(table, r.Key));
            foreach (var k in doc.Library?.Keys ?? Enumerable.Empty<string>()) if (LibrarySnapshot.Split(k).table == table) used.Add(k);
        }
        bool changed = false;
        foreach (var item in used)
            if (doc.Library?.ContainsKey(item) != true && set.Snapshot(item) is { } s)
            {
                (doc.Library ??= new(StringComparer.Ordinal))[item] = s;
                changed = true;
            }
        foreach (var item in doc.Library?.Keys.Where(k => !used.Contains(k)).ToList() ?? new())
        {
            doc.Library!.Remove(item);
            changed = true;
        }
        if (doc.Library is { Count: 0 }) doc.Library = null;
        return changed;
    }

    /// <summary>The part's snapshots whose rows are different in <paramref name="set"/> now (none for items it no longer has).</summary>
    public static List<LibraryUpdate> Updates(CadDocument doc, LibrarySet? set = null)
    {
        set ??= Current;
        var list = new List<LibraryUpdate>();
        foreach (var (item, old) in doc.Library ?? new())
            if (set.Snapshot(item) is { } now && !now.SameData(old)) list.Add(new LibraryUpdate(item, old, now));
        return list;
    }

    /// <summary>
    /// Takes the updates into the part (a model edit: the caller makes it undoable): the new snapshots, which the
    /// features sized from them read when the part is built (a hole's diameters, but those typed over).
    /// </summary>
    public static void Accept(CadDocument doc, IEnumerable<LibraryUpdate> updates)
    {
        foreach (var u in updates) (doc.Library ??= new(StringComparer.Ordinal))[u.Item] = u.New;
    }
}

/// <summary>
/// Hole sizes from the library (docs/library-v2.md): a hole sized from a fastener holes table names its row (table,
/// fastener, size) and fit; it is made with the row's clearance diameter for the fit and its head's pocket, read from
/// the part's snapshot of the row, but for the values typed over them (<see cref="HoleFeature.Overrides"/>).
/// </summary>
public static class HoleSizes
{
    /// <summary>The hole's values a fastener holes row gives, by field name (as <see cref="HoleFeature.Overrides"/> names them).</summary>
    public static readonly string[] Fields = { "Diameter", "CbDiameter", "CbDepth", "CsDiameter", "CsAngle" };

    /// <summary>The head types of a fastener holes row ("head" text): a counterbore's or a countersink's pocket.</summary>
    public const string Counterbore = "counterbore", Countersink = "countersink";

    /// <summary>The item of the hole's fastener holes row (null: not sized from the library, or tapped).</summary>
    public static string? ItemOf(HoleFeature f) =>
        f.Type != HoleType.Tapped && !string.IsNullOrEmpty(f.Standard) && !string.IsNullOrEmpty(f.Fastener) && !string.IsNullOrEmpty(f.Size)
            ? LibrarySnapshot.ItemKey(f.Standard, LibraryRow.JoinKeys(new[] { f.Fastener, f.Size })) : null;

    /// <summary>The thread table a tapped hole's size is from: its own, else ISO metric coarse.</summary>
    public static string ThreadTableOf(HoleFeature f) => string.IsNullOrEmpty(f.ThreadStandard) ? "ISO261-coarse" : f.ThreadStandard;

    /// <summary>The item of a tapped hole's thread size (null: not tapped, or no size).</summary>
    public static string? ThreadItemOf(HoleFeature f) =>
        f.Type == HoleType.Tapped && f.LibraryType == null && !string.IsNullOrEmpty(f.ThreadSize)
        && (f.ThreadClass ?? ThreadSizes.FirstClass(ThreadTableOf(f), f.ThreadSize)) is { } cls ? ThreadSizes.ItemOf(ThreadTableOf(f), f.ThreadSize, cls) : null;

    /// <summary>A tapped hole's thread: its major diameter, pitch and tap drill (mm; the tap drill major less pitch where the row gives none). Null: no thread row.</summary>
    public static (double major, double pitch, double tap)? Thread(HoleFeature f, Func<string, LibrarySnapshot?> resolve)
    {
        if (ThreadItemOf(f) is not { } item || resolve(item) is not { } s || s.Base("major") is not { } d || s.Base("pitch") is not { } p) return null;
        return (d, p, s.Base("tap.diameter") ?? d - p);
    }

    /// <summary>The items a hole sized from the library uses: its row (or its library hole type's size, or its thread's).</summary>
    public static IEnumerable<string> Items(HoleFeature f)
    {
        if (f.LibraryType != null) { if (!string.IsNullOrEmpty(f.Size)) yield return LibrarySnapshot.ItemKey(f.LibraryType, f.Size); yield break; }
        if (ItemOf(f) is { } item) yield return item;
        if (ThreadItemOf(f) is { } thread) yield return thread;
    }

    /// <summary>Whether a fastener holes row suits a hole type: a counterbore's row gives its pocket, a countersink's too; any row a simple hole.</summary>
    public static bool Suits(HoleType type, LibraryTable table, LibraryRow row)
    {
        var head = row.Text?.GetValueOrDefault("head");
        return type switch
        {
            HoleType.Counterbore => head == Counterbore && table.Base(row, "head.diameter") != null && table.Base(row, "head.depth") != null,
            HoleType.Countersink => head == Countersink && table.Base(row, "head.diameter") != null,
            _ => true,
        };
    }

    /// <summary>A hole's value of a field (as typed: an expression).</summary>
    public static string Get(HoleFeature f, string field) => field switch
    {
        "Diameter" => f.Diameter, "CbDiameter" => f.CbDiameter, "CbDepth" => f.CbDepth, "CsDiameter" => f.CsDiameter, "CsAngle" => f.CsAngle,
        _ => throw new ArgumentException(field),
    };

    public static void Set(HoleFeature f, string field, string value)
    {
        switch (field)
        {
            case "Diameter": f.Diameter = value; break;
            case "CbDiameter": f.CbDiameter = value; break;
            case "CbDepth": f.CbDepth = value; break;
            case "CsDiameter": f.CsDiameter = value; break;
            case "CsAngle": f.CsAngle = value; break;
            default: throw new ArgumentException(field);
        }
    }

    /// <summary>The value the hole's row gives a field (lengths in mm, the angle in degrees); null when it gives none (or the hole has no row).</summary>
    public static string? LibraryValue(HoleFeature f, string field, Func<string, LibrarySnapshot?> resolve)
    {
        static string Mm(double v) => Expr.Format(Math.Round(v, 4)) + "mm";
        // a tapped hole: its diameter the thread's tap drill
        if (f.Type == HoleType.Tapped) return field == "Diameter" && Thread(f, resolve) is { } th ? Mm(th.tap) : null;
        if (ItemOf(f) is not { } item || resolve(item) is not { } row) return null;
        var head = row.Text?.GetValueOrDefault("head");
        double? v = field switch
        {
            "Diameter" => (f.Fit != null ? row.Base(f.Fit) : null) ?? row.Base("normal"),
            "CbDiameter" when head == Counterbore => row.Base("head.diameter"),
            "CbDepth" when head == Counterbore => row.Base("head.depth"),
            "CsDiameter" when head == Countersink => row.Base("head.diameter"),
            "CsAngle" when head == Countersink => row.Base("head.angle"),
            _ => null,
        };
        return v is not { } x ? null : field == "CsAngle" ? Expr.Format(x) : Mm(x);
    }

    /// <summary>
    /// The hole as it is made: sized from the library, each value its row gives (a value typed over it in its place);
    /// a library hole type's size's values (<see cref="HoleFeature.TypeValues"/>); else the hole as it is.
    /// </summary>
    public static HoleFeature Effective(HoleFeature f, Func<string, LibrarySnapshot?> resolve)
    {
        if (f.LibraryType != null)
        {
            var e = (HoleFeature)f.Clone();
            if (Items(f).FirstOrDefault() is { } ti && resolve(ti) is { } row)
            {
                e.TypeValues = row.Columns.Zip(row.Values).Where(x => double.IsFinite(x.Second))
                    .ToDictionary(x => x.First, x => Schemas.UnitOf(row.Kind, x.First).ToBase(x.Second, row.Units));
                if (e.TypeValues.TryGetValue("D", out var d)) e.Diameter = Expr.Format(Math.Round(d, 6)) + "mm";
            }
            return e;
        }
        if (ItemOf(f) == null && ThreadItemOf(f) == null) return f;
        var h = (HoleFeature)f.Clone();
        foreach (var field in Fields)
            if (f.Overrides?.GetValueOrDefault(field) is { } typed) Set(h, field, typed);
            else if (LibraryValue(f, field, resolve) is { } lib) Set(h, field, lib);
        return h;
    }

    /// <summary>
    /// A hole sized from the clearance tables before library v2 ("ISO273", "ASME-B18.2.8", with its counterbore or
    /// countersink found from them) named by its fastener holes row instead: the same size, the fastener its type was
    /// for; the diameters it was made with kept as typed over where the row's (in <paramref name="set"/>) differ.
    /// Nothing for a hole sized otherwise.
    /// </summary>
    public static void Upgrade(HoleFeature f, LibrarySet set)
    {
        if (f.Fastener != null || f.Standard is not ("ISO273" or "ASME-B18.2.8")) return;
        f.Standard = f.Standard == "ISO273" ? "ISO-holes" : "ASME-holes";
        f.Fastener = f.Type == HoleType.Countersink ? "Flat head screw" : "Socket head cap screw";
        var relevant = f.Type switch
        {
            HoleType.Counterbore => new[] { "Diameter", "CbDiameter", "CbDepth" },
            HoleType.Countersink => new[] { "Diameter", "CsDiameter", "CsAngle" },
            _ => new[] { "Diameter" },
        };
        foreach (var field in relevant)
        {
            var mine = Get(f, field);
            var lib = LibraryValue(f, field, set.Snapshot);
            if (lib == null || !Same(mine, lib)) (f.Overrides ??= new())[field] = mine;
        }
    }

    // the same amount (a length in mm or degrees, as SpryCAD wrote them), the text aside
    static bool Same(string a, string b)
    {
        static double? V(string s) => double.TryParse(s.Replace("mm", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        return a == b || V(a) is { } x && V(b) is { } y && Math.Abs(x - y) < 1e-9;
    }
}

/// <summary>Thread sizes from the library.</summary>
public static class ThreadSizes
{
    /// <summary>The table a thread's size is from: its own, else ISO metric coarse (threads made before libraries).</summary>
    public static string TableOf(ThreadFeature t) => string.IsNullOrEmpty(t.Standard) ? "ISO261-coarse" : t.Standard;

    /// <summary>A thread row's item: its table, designation and class ("ASME-B1.1-UNC:1/4-20 UNC:2A").</summary>
    public static string ItemOf(string table, string size, string cls) => LibrarySnapshot.ItemKey(table, LibraryRow.JoinKeys(new[] { size, cls }));

    /// <summary>The first class a table gives a size (its row's order: the standard's usual one, 6g, 2A); null: it has no such size.</summary>
    public static string? FirstClass(string table, string size, LibrarySet? set = null) =>
        (set ?? LibraryLink.Current).Table(table)?.Rows.FirstOrDefault(r => r.Keys.Count == 2 && r.Keys[0] == size)?.Keys[1];

    /// <summary>The item a thread of a standard size uses (none for Auto and Custom): its size in its class (the table's first, named none).</summary>
    public static IEnumerable<string> Items(ThreadFeature t)
    {
        if (t.Size is "Auto" or "Custom" || string.IsNullOrEmpty(t.Size)) yield break;
        if ((t.Class ?? FirstClass(TableOf(t), t.Size)) is { } cls) yield return ItemOf(TableOf(t), t.Size, cls);
    }

    /// <summary>A size's class: its external class (the row's key: 2A) and the internal class that goes with it (2B; "" where the row gives none).</summary>
    public sealed record ThreadClass(string External, string Internal)
    {
        /// <summary>"2A / 2B"; the external alone where the row gives no internal class.</summary>
        public string Pair => Internal.Length > 0 ? $"{External} / {Internal}" : External;
    }

    /// <summary>The classes a table gives a size, in its rows' order (as Inventor's thread sheets: a row per class).</summary>
    public static List<ThreadClass> Classes(LibrarySet set, string table, string size) =>
        set.Table(table)?.Rows.Where(r => r.Keys.Count == 2 && r.Keys[0] == size)
            .Select(r => new ThreadClass(r.Keys[1], r.Text?.GetValueOrDefault("internal.class") ?? "")).ToList() ?? new();

    /// <summary>
    /// The size of a table fitting a face, for an Auto thread: a shaft by its major diameter, a hole by its tap drill
    /// (its basic minor diameter where the table gives none), of the class asked (each size's first where it is not
    /// given or the size lacks it), from the part's snapshots of the table's rows (the libraries' where it has none).
    /// Null: the table has no rows.
    /// </summary>
    public static (string name, string cls, double major, double pitch)? Fit(CadDocument doc, string table, double diameter, bool external, string? cls = null)
    {
        var rows = doc.Library?.Where(kv => LibrarySnapshot.Split(kv.Key).table == table).Select(kv => kv.Value).ToList() is { Count: > 0 } kept ? kept
            : LibraryLink.Current.Table(table) is { } tb ? tb.Rows.Select(r => LibraryLink.Current.Snapshot(LibrarySnapshot.ItemKey(table, r.Key))!).ToList()
            : new List<LibrarySnapshot>();
        // a row per size: its class asked, else its first
        var bySize = new Dictionary<string, (string cls, LibrarySnapshot s)>(StringComparer.Ordinal);
        foreach (var s in rows)
        {
            var keys = LibraryRow.SplitKeys(s.Row);
            var (size, c) = (keys[0], keys.Count > 1 ? keys[1] : "");
            if (!bySize.ContainsKey(size) || c == cls && bySize[size].cls != cls) bySize[size] = (c, s);
        }
        (string, string, double, double)? best = null; double bestErr = double.MaxValue;
        foreach (var (size, (c, s)) in bySize)
        {
            if (s.Base("major") is not { } d || s.Base("pitch") is not { } p) continue;
            var fit = external ? d : s.Base("tap.diameter") ?? Library.ThreadFormulas.MinorDiameter(d, p);
            var err = Math.Abs(fit - diameter);
            if (err < bestErr - 1e-12) { bestErr = err; best = (size, c, d, p); }
        }
        return best;
    }

    /// <summary>
    /// A thread snapshot from before threads had classes (its row its designation alone) given its class: the one it
    /// names (an "external.class" text), else the table's first for the size, else the standard's usual one (6g, 2A).
    /// </summary>
    internal static void UpgradeSnapshot(LibrarySnapshot s)
    {
        if (s.Kind != TableKinds.Thread || LibraryRow.SplitKeys(s.Row).Count != 1) return;
        var cls = s.Text?.GetValueOrDefault("external.class") ?? FirstClass(s.Table, s.Row) ?? (s.Units == TableUnits.Inch ? "2A" : "6g");
        s.Text?.Remove("external.class");
        if (s.Text is { Count: 0 }) s.Text = null;
        s.Row = LibraryRow.JoinKeys(new[] { s.Row, cls });
    }

    /// <summary>The class of a size a part already has a snapshot of (the first, where several), else the table's first.</summary>
    internal static string? ClassFor(CadDocument doc, string table, string size) =>
        doc.Library?.Values.Where(x => x.Table == table && LibraryRow.SplitKeys(x.Row) is { Count: 2 } k && k[0] == size)
            .Select(x => LibraryRow.SplitKeys(x.Row)[1]).FirstOrDefault() ?? FirstClass(table, size);

    /// <summary>The thread's major diameter and pitch (mm) by its size, from the part's snapshot or the libraries (null: unknown size).</summary>
    public static (double major, double pitch)? Resolve(CadDocument doc, ThreadFeature t)
    {
        var item = Items(t).FirstOrDefault();
        if (item == null || LibraryLink.Resolve(doc, item) is not { } s) return null;
        return s.Base("major") is { } d && s.Base("pitch") is { } p ? (d, p) : null;
    }
}

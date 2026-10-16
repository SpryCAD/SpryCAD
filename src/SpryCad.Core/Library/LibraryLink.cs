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
            if (a != b) items.Add($"{c} {(a is { } x ? Expr.Format(x) : "—")} → {(b is { } y ? Expr.Format(y) : "—")}");
        }
        if (!(Old.Text ?? new()).SequenceEqual(New.Text ?? new())) items.Add("text changed");
        return $"{New.Row} ({New.Table}): {string.Join(", ", items)}";
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
    /// Brings the part's snapshots in line with what its features use: a snapshot added for each item used that has
    /// none (from <paramref name="set"/>), removed for each no feature uses. True when anything changed.
    /// </summary>
    public static bool Sync(CadDocument doc, LibrarySet? set = null)
    {
        set ??= Current;
        var used = doc.Features.SelectMany(f => f.LibraryItems()).ToHashSet(StringComparer.Ordinal);
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
    /// Takes the updates into the part (a model edit: the caller makes it undoable): the new snapshots, and the
    /// features sized from those items sized again from them (a hole's diameters).
    /// </summary>
    public static void Accept(CadDocument doc, IEnumerable<LibraryUpdate> updates)
    {
        var items = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in updates)
        {
            (doc.Library ??= new(StringComparer.Ordinal))[u.Item] = u.New;
            items.Add(u.Item);
        }
        foreach (var f in doc.Features.OfType<HoleFeature>())
            if (f.LibraryItems().Any(items.Contains)) HoleSizes.Apply(f, item => Resolve(doc, item));
    }
}

/// <summary>Hole sizes from the library: clearance diameters by fit, with the counterbores and countersinks that go with them.</summary>
public static class HoleSizes
{
    /// <summary>The counterbore and countersink tables that go with a clearance table.</summary>
    public static (string cbore, string csink) Companions(string clearanceTable) => clearanceTable switch
    {
        "ASME-B18.2.8" => ("ASME-B18.3-cbore", "ASME-B18.6.3-csink"),
        _ => ("ISO4762-cbore", "ISO10642-csink"),
    };

    /// <summary>The items a hole sized from the library uses.</summary>
    public static IEnumerable<string> Items(HoleFeature f)
    {
        if (string.IsNullOrEmpty(f.Standard) || string.IsNullOrEmpty(f.Size)) yield break;
        yield return LibrarySnapshot.ItemKey(f.Standard, f.Size);
        var (cb, cs) = Companions(f.Standard);
        if (f.Type == HoleType.Counterbore) yield return LibrarySnapshot.ItemKey(cb, f.Size);
        if (f.Type == HoleType.Countersink) yield return LibrarySnapshot.ItemKey(cs, f.Size);
    }

    /// <summary>
    /// What the hole's standard and size has no row for, said for the user (null: none): a counterbore or countersink
    /// of a size its table lacks (ISO 10642 has no M18) keeps the values it had, so they must be typed.
    /// </summary>
    public static string? Missing(HoleFeature f, Func<string, LibrarySnapshot?> resolve)
    {
        if (string.IsNullOrEmpty(f.Standard) || string.IsNullOrEmpty(f.Size)) return null;
        if (resolve(LibrarySnapshot.ItemKey(f.Standard, f.Size)) == null) return $"The libraries have no {f.Size} in {f.Standard}: type the diameter.";
        var (cb, cs) = Companions(f.Standard);
        if (f.Type == HoleType.Counterbore && resolve(LibrarySnapshot.ItemKey(cb, f.Size)) == null)
            return $"The libraries have no {f.Size} counterbore ({cb}): type its diameter and depth.";
        if (f.Type == HoleType.Countersink && resolve(LibrarySnapshot.ItemKey(cs, f.Size)) == null)
            return $"The libraries have no {f.Size} countersink ({cs}): type its diameter and angle.";
        return null;
    }

    /// <summary>
    /// Sets the hole's diameters (and counterbore depth, countersink angle) from its standard, size and fit, as found by
    /// <paramref name="resolve"/>. Values a table has no row for are left as they are. Lengths are written in millimetres.
    /// </summary>
    public static void Apply(HoleFeature f, Func<string, LibrarySnapshot?> resolve)
    {
        if (string.IsNullOrEmpty(f.Standard) || string.IsNullOrEmpty(f.Size)) return;
        static string Mm(double v) => Expr.Format(Math.Round(v, 4)) + "mm";
        if (resolve(LibrarySnapshot.ItemKey(f.Standard, f.Size)) is { } c)
            if (((f.Fit != null ? c.Value(f.Fit) : null) ?? c.Value("normal") ?? c.Values.FirstOrDefault()) is var d && d > 0) f.Diameter = Mm(d);
        var (cb, cs) = Companions(f.Standard);
        if (f.Type == HoleType.Counterbore && resolve(LibrarySnapshot.ItemKey(cb, f.Size)) is { } b)
        {
            if (b.Value("diameter") is { } bd) f.CbDiameter = Mm(bd);
            if (b.Value("depth") is { } bh) f.CbDepth = Mm(bh);
        }
        if (f.Type == HoleType.Countersink && resolve(LibrarySnapshot.ItemKey(cs, f.Size)) is { } s)
        {
            if (s.Value("diameter") is { } sd) f.CsDiameter = Mm(sd);
            if (s.Value("angle") is { } sa) f.CsAngle = Expr.Format(sa);
        }
    }
}

/// <summary>Thread sizes from the library.</summary>
public static class ThreadSizes
{
    /// <summary>The table a thread's size is from: its own, else ISO metric coarse (threads made before libraries).</summary>
    public static string TableOf(ThreadFeature t) => string.IsNullOrEmpty(t.Standard) ? "ISO261-coarse" : t.Standard;

    /// <summary>The item a thread of a standard size uses (none for Auto and Custom).</summary>
    public static IEnumerable<string> Items(ThreadFeature t)
    {
        if (t.Size is "Auto" or "Custom" || string.IsNullOrEmpty(t.Size)) yield break;
        yield return LibrarySnapshot.ItemKey(TableOf(t), t.Size);
    }

    /// <summary>The thread's major diameter and pitch (mm) by its size, from the part's snapshot or the libraries (null: unknown size).</summary>
    public static (double major, double pitch)? Resolve(CadDocument doc, ThreadFeature t)
    {
        var item = Items(t).FirstOrDefault();
        if (item == null || LibraryLink.Resolve(doc, item) is not { } s) return null;
        return s.Value("major") is { } d && s.Value("pitch") is { } p ? (d, p) : null;
    }
}

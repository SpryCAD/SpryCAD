using SpryCad.Core.Model;

namespace SpryCad.Core.Library;

/// <summary>
/// A part's material (<see cref="CadDocument.MaterialItem"/>): a library item like a thread size, the part keeping a
/// copy of its row (<see cref="CadDocument.Library"/>), so it weighs and looks the same with no library installed and
/// takes a library's change only through Library Updates. A material of the part's own (a density typed in) is a row
/// no library has: table <see cref="CustomTable"/>, never offered for update.
/// </summary>
public static class PartMaterial
{
    /// <summary>The table a part's own materials are in ("Part/Custom"); reserved, so no library has it.</summary>
    public const string CustomTable = "Part";
    /// <summary>What a material of the part's own is called when nothing names it.</summary>
    public const string CustomName = "Custom";

    /// <summary>
    /// Gives the part a library material (null: none), its row copied in from <paramref name="set"/> (the libraries
    /// in use). The part's own appearance, if it has one, is kept. A model edit: the caller makes it undoable.
    /// </summary>
    public static void Set(CadDocument doc, string? item, LibrarySet? set = null)
    {
        if (item == doc.MaterialItem) return;
        LibrarySnapshot? s = null;
        if (item != null)
        {
            s = (set ?? LibraryLink.Current).Snapshot(item) ?? throw new ArgumentException($"No library has the material {item}.");
            if (Materials.Of(s) == null) throw new ArgumentException($"{item} is not a material.");
        }
        Put(doc, item, s);
    }

    /// <summary>
    /// Gives the part a material of its own: <paramref name="name"/> with <paramref name="density"/> (kg/m³), its other
    /// properties and look those of <paramref name="basedOn"/> (a material row), or none.
    /// </summary>
    public static void SetCustom(CadDocument doc, string name, double density, LibrarySnapshot? basedOn = null)
    {
        if (!(density > 0) || !double.IsFinite(density)) throw new ArgumentException("A density must be a positive number.");
        var b = basedOn != null ? Materials.Of(basedOn) : null;
        var s = new LibrarySnapshot
        {
            Table = CustomTable, Row = name.Trim().Length > 0 ? name.Trim() : CustomName, LibraryName = "",
            Kind = TableKinds.Material, Units = TableUnits.Metric, Columns = Materials.Columns.ToList(),
            // (in a metric material table's units, as the set's snapshots are)
            Values = Materials.Columns.Select(c => (c == "density" ? density : b?.Value(c)) is { } si
                ? Schemas.UnitOf(TableKinds.Material, c).FromBase(si, TableUnits.Metric) : double.NaN).ToList(),
            Text = basedOn?.Text != null ? new SortedDictionary<string, string>(basedOn.Text, StringComparer.Ordinal)
                : new SortedDictionary<string, string>(StringComparer.Ordinal) { [Materials.TypeKey] = MaterialType.Other.ToString() },
        };
        Put(doc, s.Item, s);
    }

    /// <summary>Whether the item is a material of the part's own (no library's).</summary>
    public static bool IsCustom(string? item) => item != null && LibrarySnapshot.Split(item).table == CustomTable;

    static void Put(CadDocument doc, string? item, LibrarySnapshot? s)
    {
        // the old material's row dropped, unless a feature uses it too
        if (doc.MaterialItem is { } old && doc.Library != null && !doc.Features.SelectMany(f => f.LibraryItems()).Contains(old))
            doc.Library.Remove(old);
        doc.MaterialItem = item;
        if (item != null && s != null) (doc.Library ??= new(StringComparer.Ordinal))[item] = s;
        if (doc.Library is { Count: 0 }) doc.Library = null;
    }

    /// <summary>The material row named <paramref name="name"/> in any material table of the set (restricted or not), first by layer order.</summary>
    public static string? FindByName(string name, LibrarySet? set = null)
    {
        set ??= LibraryLink.Current;
        return set.Tables(TableKinds.Material).Where(t => t.Row(name) != null).Select(t => LibrarySnapshot.ItemKey(t.Key, name)).FirstOrDefault();
    }

    /// <summary>
    /// A part from before material libraries (<see cref="CadDocument.LegacyColor"/> and the rest): its material name
    /// matched to a library row by name, a custom material when none has it or its density differs (the part weighs
    /// as it did), and a color other than the default grey its own appearance. The legacy values cleared.
    /// </summary>
    public static void Upgrade(CadDocument doc, LibrarySet? set = null)
    {
        var (color, name, density) = (doc.LegacyColor, doc.LegacyMaterial, doc.LegacyDensity);
        if (color == null && name == null && density == null) return;
        doc.LegacyColor = null; doc.LegacyMaterial = null; doc.LegacyDensity = null;
        set ??= LibraryLink.Current;
        if (doc.MaterialItem == null && (name != null || density != null))
        {
            var item = name != null ? FindByName(name, set) : null;
            var row = item != null ? set.Snapshot(item) : null;
            var d = density ?? (row != null ? Materials.Of(row)!.Density : CadDocument.DefaultDensity);
            if (row != null && Math.Abs(Materials.Of(row)!.Density - d) <= 1e-9 * d) Set(doc, item, set);
            else SetCustom(doc, name ?? CustomName, d, row);
        }
        if (doc.Appearance == null && color is { Length: 3 } && !color.SequenceEqual(Appearance.Default.Color))
            doc.Appearance = new Appearance(color);
    }
}

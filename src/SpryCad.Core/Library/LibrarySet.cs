namespace SpryCad.Core.Library;

/// <summary>
/// The libraries in use, stacked (section 34): the built-in standards first, then the configured libraries by layer
/// (standards, part families, company) and in the order configured. A table in a later library adds rows to one of
/// the same key and overrides rows of the same designation (or replaces the table when its columns differ); company
/// restrictions hide rows from the pickers. Existing parts resolve their items regardless of restrictions (they
/// carry snapshots anyway). Immutable: a new set is made when the libraries change.
/// </summary>
public sealed class LibrarySet
{
    public static readonly LibrarySet BuiltIn = new(Array.Empty<LoadedLibrary>());

    /// <summary>Every library in the set, bottom layer first (the built-in standards first of all).</summary>
    public IReadOnlyList<LoadedLibrary> Libraries { get; }

    readonly Dictionary<string, Merged> _tables = new(StringComparer.Ordinal);
    readonly List<LibraryRestriction> _restrictions = new();

    sealed record Merged(LibraryTable Table, LoadedLibrary[] From);

    public LibrarySet(IEnumerable<LoadedLibrary> libraries)
    {
        Libraries = new[] { BuiltInStandards.AsLibrary(), BuiltInMaterials.AsLibrary(), FastenerFamilies.AsLibrary() }.Concat(libraries.Where(l => l.LibraryId != Guid.Empty).OrderBy(l => l.Layer)).ToList();
        foreach (var lib in Libraries)
        {
            foreach (var t in lib.Tables) Add(lib, t);
            _restrictions.AddRange(lib.Restrictions);
        }
    }

    void Add(LoadedLibrary lib, LibraryTable t)
    {
        if (!_tables.TryGetValue(t.Key, out var m) || !m.Table.Columns.SequenceEqual(t.Columns))
        {
            _tables[t.Key] = new Merged(t, t.Rows.Select(_ => lib).ToArray());
            return;
        }
        // same columns: rows added or overridden by designation, the table's own text from the later library
        var rows = m.Table.Rows.ToList();
        var from = m.From.ToList();
        foreach (var r in t.Rows)
        {
            var i = rows.FindIndex(x => x.Key == r.Key);
            if (i >= 0) { rows[i] = r; from[i] = lib; }
            else { rows.Add(r); from.Add(lib); }
        }
        var merged = new LibraryTable { Key = t.Key, Title = t.Title, Kind = t.Kind, Standard = t.Standard, Units = t.Units, Verified = t.Verified && m.Table.Verified, Columns = t.Columns, Rows = rows };
        _tables[t.Key] = new Merged(merged, from.ToArray());
    }

    /// <summary>The table by key, every row (null if no library has it).</summary>
    public LibraryTable? Table(string key) => _tables.TryGetValue(key, out var m) ? m.Table : null;

    /// <summary>Every table of a kind, every row (restrictions aside: what parts that already use them resolve), in the order pickers list them.</summary>
    public IEnumerable<LibraryTable> Tables(string kind) =>
        _tables.Values.Where(m => m.Table.Kind == kind).OrderBy(m => Order(m.Table.Key)).ThenBy(m => m.Table.Key, StringComparer.Ordinal).Select(m => m.Table);

    /// <summary>Whether the company libraries offer this row (and its table) in pickers.</summary>
    public bool Allowed(string table, string row) => Allowed(table, row, Table(table)?.Row(row)?.Text?.GetValueOrDefault(Fasteners.SizeKey));

    bool Allowed(string table, string row, string? size) => _restrictions.Where(r => r.Table == table).All(r => r.Allows(row, size));

    bool TableShown(string table) => !_restrictions.Any(r => r.Table == table && r.Hidden);

    /// <summary>The tables of a kind (<see cref="TableKinds"/>) as pickers offer them: hidden tables and restricted rows left out.</summary>
    public IEnumerable<LibraryTable> Offered(string kind)
    {
        foreach (var m in _tables.Values.Where(m => m.Table.Kind == kind && TableShown(m.Table.Key)).OrderBy(m => Order(m.Table.Key)))
        {
            var rows = m.Table.Rows.Where(r => Allowed(m.Table.Key, r.Key, r.Text?.GetValueOrDefault(Fasteners.SizeKey))).ToList();
            if (rows.Count == 0) continue;
            yield return new LibraryTable { Key = m.Table.Key, Title = m.Table.Title, Kind = m.Table.Kind, Standard = m.Table.Standard, Units = m.Table.Units, Verified = m.Table.Verified, Columns = m.Table.Columns, Rows = rows };
        }
    }

    // built-in tables first in their own order, then the others by key
    static readonly List<string> BuiltInOrder = BuiltInStandards.Tables.Concat(BuiltInMaterials.Tables).Select(t => t.Key).ToList();
    static int Order(string key) { var i = BuiltInOrder.IndexOf(key); return i >= 0 ? i : 1000; }

    /// <summary>The row of an item ("ISO273/M8") as a snapshot to copy into a part (null: no library has it).</summary>
    public LibrarySnapshot? Snapshot(string item)
    {
        var (table, row) = LibrarySnapshot.Split(item);
        if (!_tables.TryGetValue(table, out var m)) return null;
        var i = m.Table.Rows.FindIndex(r => r.Key == row);
        if (i < 0) return null;
        var r = m.Table.Rows[i];
        var lib = m.From[i];
        return new LibrarySnapshot
        {
            Table = table, Row = row, LibraryId = lib.LibraryId, LibraryRevision = lib.RevisionId, LibraryName = lib.Name,
            Columns = m.Table.Columns.ToList(), Values = r.Values.ToList(),
            Text = r.Text == null ? null : new SortedDictionary<string, string>(r.Text, StringComparer.Ordinal),
        };
    }
}

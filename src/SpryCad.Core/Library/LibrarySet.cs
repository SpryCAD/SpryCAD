namespace SpryCad.Core.Library;

/// <summary>
/// The libraries in use (docs/library-v2.md): for standards and for materials each, SpryCAD's defaults or a company's
/// own library, never a mix (no layering). Parts resolve their items from their snapshots; this is what pickers offer
/// and what new snapshots are taken from. Immutable: a new set is made when the libraries change.
/// </summary>
public sealed class LibrarySet
{
    public static readonly LibrarySet BuiltIn = new(Array.Empty<LoadedLibrary>());

    /// <summary>The standards library in use (SpryCAD's defaults when no company library is set).</summary>
    public LoadedLibrary Standards { get; }
    /// <summary>The materials library in use (SpryCAD's defaults when no company library is set).</summary>
    public LoadedLibrary Materials { get; }

    /// <summary>The libraries in use: standards, then materials.</summary>
    public IReadOnlyList<LoadedLibrary> Libraries => new[] { Standards, Materials };

    readonly Dictionary<string, (LibraryTable table, LoadedLibrary lib)> _tables = new(StringComparer.Ordinal);

    /// <summary>
    /// The set of the given company libraries: the first of each domain (any other of the same domain is not used);
    /// SpryCAD's defaults for a domain none is given for.
    /// </summary>
    public LibrarySet(IEnumerable<LoadedLibrary> libraries)
    {
        var given = libraries.Where(l => l.LibraryId != Guid.Empty).ToList();
        Standards = given.FirstOrDefault(l => l.Domain == LibraryDomain.Standards) ?? BuiltInStandards.AsLibrary();
        Materials = given.FirstOrDefault(l => l.Domain == LibraryDomain.Materials) ?? BuiltInMaterials.AsLibrary();
        foreach (var lib in Libraries)
            foreach (var t in lib.Tables) _tables.TryAdd(t.Key, (t, lib));
        Profile = Standards.ProfileTables is { Count: > 0 } p ? new SheetMetal.ShopProfile(Standards.Name, p) : SheetMetal.StarterProfile.Profile;
    }

    /// <summary>
    /// The shop profile every sheet metal part uses (sheet metal spec section 3): the standards library's, whole;
    /// SpryCAD's starter profile when it has none.
    /// </summary>
    public SheetMetal.ShopProfile Profile { get; }

    /// <summary>The library the profile is from (null: the starter profile).</summary>
    public LoadedLibrary? ProfileLibrary => Standards.ProfileTables is { Count: > 0 } ? Standards : null;

    /// <summary>The table by key (null if the libraries in use have none).</summary>
    public LibraryTable? Table(string key) => _tables.TryGetValue(key, out var m) ? m.table : null;

    /// <summary>Every table of a kind, in the order its library lists them.</summary>
    public IEnumerable<LibraryTable> Tables(string kind) => _tables.Values.Where(m => m.table.Kind == kind).Select(m => m.table);

    /// <summary>The tables of a kind (<see cref="TableKinds"/>) as pickers offer them: those with rows.</summary>
    public IEnumerable<LibraryTable> Offered(string kind) => Tables(kind).Where(t => t.Rows.Count > 0);

    /// <summary>The row of an item ("ISO273:M8") as a snapshot to copy into a part (null: the libraries in use have none).</summary>
    public LibrarySnapshot? Snapshot(string item)
    {
        var (table, row) = LibrarySnapshot.Split(item);
        if (!_tables.TryGetValue(table, out var m) || m.table.Row(row) is not { } r) return null;
        return new LibrarySnapshot
        {
            Table = table, Row = row, Kind = m.table.Kind, Units = m.table.Units, LibraryId = m.lib.LibraryId, LibraryRevision = m.lib.RevisionId, LibraryName = m.lib.Name,
            Columns = m.table.Columns.ToList(), Values = r.Values.ToList(),
            Text = r.Text == null ? null : new SortedDictionary<string, string>(r.Text, StringComparer.Ordinal),
        };
    }
}

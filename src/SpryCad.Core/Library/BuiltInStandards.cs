namespace SpryCad.Core.Library;

/// <summary>
/// SpryCAD's default standards (threads, holes, fasteners): the standards in use when no company library is set, and
/// what a company's own standards library starts as. Authored in data/library/standards.xlsx and generated into this
/// assembly at build time (docs/library-v2.md). Every table is marked not yet verified against its published standard
/// (<see cref="LibraryTable.Verified"/>) until checked. Keys and row designations never change meaning (parts name them).
/// </summary>
public static class BuiltInStandards
{
    /// <summary>The default standards library's identity (fixed: parts' snapshots name it).</summary>
    public static readonly Guid LibraryId = new("6f1d3c52-8a4e-4b7f-9d21-0c5e8b7a2f14");
    public const string Name = "SpryCAD standards";

    public static IReadOnlyList<LibraryTable> Tables => Data.Value;

    static readonly Lazy<IReadOnlyList<LibraryTable>> Data = new(() => LibraryDefaults.Load("standards"));

    /// <summary>SpryCAD's default standards as a loaded library (the standards in use when no company library is set).</summary>
    public static LoadedLibrary AsLibrary() => Default.Value;

    static readonly Lazy<LoadedLibrary> Default = new(() => new LoadedLibrary(LibraryId, Name, LibraryDomain.Standards, LibraryFile.ContentRevision(Tables), null, Tables));
}

namespace SpryCad.Core.Library;

/// <summary>
/// SpryCAD's default materials: a material table per family and the appearance presets; the materials in use when no
/// company library is set, and what a company's own materials library starts as. Authored in
/// data/library/materials.xlsx and generated into this assembly at build time (docs/library-v2.md). Typical handbook
/// values, not any one specification's minimums, and marked not verified (<see cref="LibraryTable.Verified"/>): check
/// them before relying on them. Table keys and row names never change meaning (parts' snapshots name them); the
/// twelve materials SpryCAD listed before libraries keep their names, so older parts find them.
/// </summary>
public static class BuiltInMaterials
{
    /// <summary>The default materials library's identity (fixed: parts' snapshots name it).</summary>
    public static readonly Guid LibraryId = new("2c8e5f1a-7d34-4b96-a0e2-91f6c3d84b27");
    public const string Name = "SpryCAD materials";
    /// <summary>The appearance presets' table.</summary>
    public const string AppearancesKey = "Appearances";

    public static IReadOnlyList<LibraryTable> Tables => Data.Value;

    static readonly Lazy<IReadOnlyList<LibraryTable>> Data = new(() => LibraryDefaults.Load("materials"));

    /// <summary>SpryCAD's default materials as a loaded library (the materials in use when no company library is set).</summary>
    public static LoadedLibrary AsLibrary() => Default.Value;

    static readonly Lazy<LoadedLibrary> Default = new(() => new LoadedLibrary(LibraryId, Name, LibraryDomain.Materials, LibraryFile.ContentRevision(Tables), null, Tables));
}

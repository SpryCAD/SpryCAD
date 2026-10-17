namespace SpryCad.Core.Library;

/// <summary>
/// SpryCAD's built-in library as generated at build time from data/library/*.xlsx (tools/SpryCad.LibraryGen) and
/// embedded in this assembly: a domain's tables.
/// </summary>
static class LibraryDefaults
{
    public static IReadOnlyList<LibraryTable> Load(string name)
    {
        using var s = typeof(LibraryDefaults).Assembly.GetManifestResourceStream($"LibraryDefaults/{name}.json")
            ?? throw new InvalidOperationException($"The built-in library's {name} data is missing from the build (data/library/{name}.xlsx).");
        return LibraryJson.Read(s);
    }
}

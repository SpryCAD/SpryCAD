using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpryCad.Core.Library;

/// <summary>
/// The JSON the built-in library's tables are generated into at build time (tools/SpryCad.LibraryGen, from
/// data/library/*.xlsx) and read from in the app: a list of tables per domain.
/// </summary>
public static class LibraryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Write(IEnumerable<LibraryTable> tables) => JsonSerializer.Serialize(tables.ToList(), Options);

    public static List<LibraryTable> Read(Stream json) => JsonSerializer.Deserialize<List<LibraryTable>>(json, Options) ?? new();
}

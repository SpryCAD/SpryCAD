using System.Text.Json;
using SpryCad.Core.Storage;

namespace SpryCad.Core.Library;

/// <summary>
/// The central library set file (<c>libraries.json</c>, usually on a network share): the libraries everyone uses, in
/// order, as paths relative to it. Who may change them is the share's permissions: users only read.
/// <code>{ "libraries": [ "standards.sprlib", "company.sprlib" ] }</code>
/// </summary>
public sealed class LibraryConfig
{
    public const string FileName = "libraries.json";
    public List<string> Libraries { get; set; } = new();

    /// <summary>The library's path as the set file at <paramref name="configPath"/> lists it: relative to it, with '/'.</summary>
    public static string Entry(string configPath, string libraryPath) =>
        Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? "", Path.GetFullPath(libraryPath)).Replace('\\', '/');

    /// <summary>Whether the set file lists the library (false when it cannot be read).</summary>
    public static bool Lists(string configPath, string libraryPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? "";
        var full = Path.GetFullPath(libraryPath);
        try
        {
            var c = JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath), Model.Serialization.Options);
            return c?.Libraries.Any(e => string.Equals(Path.GetFullPath(Path.Combine(dir, e)), full, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Adds the library to the end of the set file (made if there is none), unless it lists it already. Returns whether it was added.</summary>
    public static bool Add(string configPath, string libraryPath)
    {
        if (File.Exists(configPath) && Lists(configPath, libraryPath)) return false;
        var c = File.Exists(configPath) ? JsonSerializer.Deserialize<LibraryConfig>(File.ReadAllText(configPath), Model.Serialization.Options) ?? new() : new();
        c.Libraries.Add(Entry(configPath, libraryPath));
        var tmp = configPath + ".saving";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c, Model.Serialization.Options));
        File.Move(tmp, configPath, overwrite: true);
        return true;
    }
}

/// <summary>Where the library set file was found, and what said so.</summary>
public enum LibraryConfigSource { None, Environment, MachinePolicy, User }

/// <summary>
/// Loads the central library set (section 34) the way model files are opened (section 12): never in place on the
/// share. Each library is copied into a local cache (<see cref="CacheFolder"/>) and read from there, copied again only
/// when the share's file changed (its size, time and first bytes); holding a share's file open would stop the owner's
/// atomic publish on Windows, and SQLite locking cannot be trusted there. When the share cannot be reached, the
/// copies last read are used, so modelling works offline.
/// </summary>
public static class LibrarySync
{
    /// <summary>The environment variable naming the library set file (or its folder): the admin's way per machine or login.</summary>
    public const string EnvironmentVariable = "SPRYCAD_LIBRARIES";

    public static string CacheFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpryCAD", "Libraries");

    /// <summary>
    /// The machine-wide file an installer or admin writes, holding the library set's path on its first line (Windows:
    /// %ProgramData%\SpryCAD\libraries.path; macOS: /Library/Application Support/SpryCAD/libraries.path; Linux:
    /// /etc/sprycad/libraries.path).
    /// </summary>
    public static string MachinePolicyFile =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpryCAD", "libraries.path")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/SpryCAD/libraries.path"
        : "/etc/sprycad/libraries.path";

    /// <summary>
    /// The library set file in use: the environment variable's, else the machine policy's, else the user's own choice
    /// (Options). An admin's setting wins, so everyone in the company uses the same libraries.
    /// </summary>
    public static (string? path, LibraryConfigSource source) Locate(string? userSetting)
    {
        if (Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable)) is { } env) return (env, LibraryConfigSource.Environment);
        try
        {
            if (File.Exists(MachinePolicyFile) && Normalize(File.ReadLines(MachinePolicyFile).FirstOrDefault()) is { } m) return (m, LibraryConfigSource.MachinePolicy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Normalize(userSetting) is { } u ? (u, LibraryConfigSource.User) : (null, LibraryConfigSource.None);
    }

    /// <summary>A path as given, or the set file in a folder given; null for none.</summary>
    static string? Normalize(string? p)
    {
        p = p?.Trim().Trim('"');
        if (string.IsNullOrEmpty(p)) return null;
        return p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, LibraryConfig.FileName);
    }

    /// <summary>The result of loading: the set, and what could not be read (shown to the user, never fatal).</summary>
    public sealed record Result(LibrarySet Set, IReadOnlyList<string> Problems);

    /// <summary>The library set the file names (null: the built-in standards alone).</summary>
    public static Result Load(string? configPath)
    {
        var problems = new List<string>();
        if (configPath == null) return new Result(LibrarySet.BuiltIn, problems);
        Directory.CreateDirectory(CacheFolder);
        var index = ReadIndex();
        var config = ReadConfig(configPath, index, problems);
        var libs = new List<LoadedLibrary>();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? "";
        foreach (var entry in config?.Libraries ?? new())
        {
            var source = Path.GetFullPath(Path.Combine(baseDir, entry.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            try { libs.Add(Fetch(source, index)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                if (index.Libraries.TryGetValue(source, out var cached) && File.Exists(cached.Copy))
                {
                    libs.Add(LibraryFile.Read(cached.Copy) with { Path = source });
                    problems.Add($"{Path.GetFileName(source)}: {ex.Message} Using the copy read {cached.ReadUtc.ToLocalTime():g}.");
                }
                else problems.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }
        WriteIndex(index);
        // (one library of each domain is used: a second is said, not mixed in)
        foreach (var g in libs.GroupBy(l => l.Domain).Where(g => g.Count() > 1))
            problems.Add($"{string.Join(", ", g.Skip(1).Select(l => Path.GetFileName(l.Path ?? l.Name)))} not used: {Path.GetFileName(g.First().Path ?? g.First().Name)} is the {LibraryDomains.Name(g.Key)} library in use.");
        return new Result(new LibrarySet(libs), problems);
    }

    static LibraryConfig? ReadConfig(string path, CacheIndex index, List<string> problems)
    {
        string? json = null;
        try
        {
            json = File.ReadAllText(path);
            index.Config = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            json = index.Config;
            problems.Add(json != null ? $"The library set ({path}) cannot be read: {ex.Message} Using the copy last read." : $"The library set ({path}) cannot be read: {ex.Message}");
        }
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<LibraryConfig>(json, Model.Serialization.Options); }
        catch (JsonException ex) { problems.Add($"The library set ({path}) is not valid: {ex.Message}"); return null; }
    }

    /// <summary>The library at <paramref name="source"/>, from the local copy when the share's file is unchanged since it was copied.</summary>
    static LoadedLibrary Fetch(string source, CacheIndex index)
    {
        var fp = WorkingCopy.Fingerprint(source) ?? throw new FileNotFoundException($"The library was not found at {source}.");
        if (index.Libraries.TryGetValue(source, out var c) && c.Fingerprint == fp && File.Exists(c.Copy))
            return LibraryFile.Read(c.Copy) with { Path = source };
        var tmp = Path.Combine(CacheFolder, Guid.NewGuid().ToString("N") + ".reading");
        try
        {
            File.Copy(source, tmp);
            // (the share's file was replaced while copying: read again next time)
            if (WorkingCopy.Fingerprint(source) != fp) fp = "";
            var lib = LibraryFile.Read(tmp);
            var copy = Path.Combine(CacheFolder, $"{lib.LibraryId:N}-{lib.RevisionId:N}{LibraryFile.Extension}");
            File.Move(tmp, copy, overwrite: true);
            index.Libraries[source] = new CachedLibrary(fp, copy, DateTime.UtcNow);
            return lib with { Path = source };
        }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }
    }

    // ---- the cache's index: which share file each copy is of

    sealed class CacheIndex
    {
        public string? Config { get; set; }
        public Dictionary<string, CachedLibrary> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed record CachedLibrary(string Fingerprint, string Copy, DateTime ReadUtc);

    static string IndexPath => Path.Combine(CacheFolder, "index.json");

    static CacheIndex ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath) && JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(IndexPath), Model.Serialization.Options) is { } i)
            {
                i.Libraries = new Dictionary<string, CachedLibrary>(i.Libraries, StringComparer.OrdinalIgnoreCase);
                return i;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new CacheIndex();
    }

    static void WriteIndex(CacheIndex index)
    {
        try
        {
            // copies no longer listed are removed (a library's older revisions)
            var keep = index.Libraries.Values.Select(c => Path.GetFullPath(c.Copy)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(CacheFolder, "*" + LibraryFile.Extension))
                if (!keep.Contains(Path.GetFullPath(f))) try { File.Delete(f); } catch (IOException) { }
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index, Model.Serialization.Options));
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

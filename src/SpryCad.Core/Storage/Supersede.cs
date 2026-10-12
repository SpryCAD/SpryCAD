using System.Text.Json;

namespace SpryCad.Core.Storage;

/// <summary>Which assembly a part file was made virtual in: where it is, which it is, when and by whom.</summary>
public sealed record SupersededBy(string? AssemblyPath, string AssemblyId, DateTime Utc, string User)
{
    /// <summary>The assembly's file from the part's folder (its path is kept relative to it), or null if not known.</summary>
    public string? AssemblyFile(string partPath) =>
        AssemblyPath == null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(partPath)) ?? "", AssemblyPath));
}

/// <summary>
/// The mark a part file gets when it is made virtual in an assembly (the file format spec, section 8): the file stays
/// as it is (other assemblies may use it), but whoever opens it learns that its changes no longer reach that assembly.
/// Kept in its manifest; nothing in its history changes.
/// </summary>
public static class Supersede
{
    const string Key = "SupersededBy";

    /// <summary>
    /// Marks the part file at <paramref name="partPath"/> as made virtual in the assembly. False when it cannot be (read-only,
    /// locked, or a part saved before databases): the user is told, nothing else changes.
    /// </summary>
    public static bool Mark(string partPath, string? assemblyPath, string assemblyId, string user)
    {
        try
        {
            if (!PartDatabase.IsFile(partPath)) return false;
            var rel = assemblyPath == null ? null
                : Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(partPath)) ?? "", Path.GetFullPath(assemblyPath)).Replace('\\', '/');
            using var db = PartDatabase.Open(partPath, readOnly: false);
            db.SetManifest(Key, JsonSerializer.Serialize(new SupersededBy(rel, assemblyId, DateTime.UtcNow, user)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    /// <summary>The mark a database has (null: none).</summary>
    public static SupersededBy? Read(PartDatabase db)
    {
        try { return db.Manifest(Key) is { } json ? JsonSerializer.Deserialize<SupersededBy>(json) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>The mark a part file has (null: none, or not a database).</summary>
    public static SupersededBy? Read(string path)
    {
        try
        {
            if (!PartDatabase.IsFile(path)) return null;
            using var db = PartDatabase.Open(path);
            return Read(db);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    /// <summary>Removes the mark (the part kept as a part of its own).</summary>
    public static void Clear(PartDatabase db) => db.SetManifest(Key, null);
}

using System.Text.Json;
using SpryCad.Core.Model;

namespace SpryCad.Core.Assembly;

/// <summary>
/// Where parts are, by identity: every part opened or saved is recorded (its GUID, revision and path), and project
/// folders can be scanned. Assemblies look a part up here when it is not where they last saw it (renamed, moved,
/// copied with its folder). Entries are checked when used (the file still there, still that part); stale ones are
/// dropped. Kept as a small JSON file in the user's application data.
/// </summary>
public sealed class PartIndex
{
    public sealed class Entry
    {
        public string Path { get; set; } = "";
        public string? Revision { get; set; }
        public DateTime Seen { get; set; }
    }

    /// <summary>Folders scanned for parts (a project's folders): searched when a part is not found otherwise.</summary>
    public List<string> Roots { get; set; } = new();
    public Dictionary<string, List<Entry>> Parts { get; set; } = new();

    // folders that are never part of a project (build output, version control, packages)
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { ".git", ".svn", ".hg", "bin", "obj", "node_modules", "$RECYCLE.BIN" };

    public static PartIndex Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<PartIndex>(File.ReadAllText(file), Serialization.Options) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
        var tmp = file + ".saving";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Serialization.Options));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>Notes that the part <paramref name="id"/> (at <paramref name="revision"/>) is at <paramref name="path"/>.</summary>
    public void Record(string id, string? revision, string path)
    {
        path = System.IO.Path.GetFullPath(path);
        // a path holds one part: whatever was recorded there before is gone
        foreach (var (k, l) in Parts) if (k != id) l.RemoveAll(e => SamePath(e.Path, path));
        if (!Parts.TryGetValue(id, out var list)) Parts[id] = list = new();
        var e = list.FirstOrDefault(x => SamePath(x.Path, path));
        if (e == null) list.Add(e = new Entry { Path = path });
        e.Revision = revision; e.Seen = DateTime.UtcNow;
        foreach (var k in Parts.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) Parts.Remove(k);
    }

    /// <summary>Records the file's identity (if it is a part or an assembly with one).</summary>
    public bool RecordFile(string path)
    {
        var (id, rev) = DocumentContainer.ReadIdentity(path);
        if (id == null) return false;
        Record(id, rev, path);
        return true;
    }

    /// <summary>The files holding part <paramref name="id"/>, checked (stale entries are dropped), most recently seen first.</summary>
    public List<(string path, string? revision)> Lookup(string id)
    {
        if (!Parts.TryGetValue(id, out var list)) return new();
        var res = new List<(string, string?)>();
        foreach (var e in list.OrderByDescending(e => e.Seen).ToList())
        {
            var (fid, rev) = File.Exists(e.Path) ? DocumentContainer.ReadIdentity(e.Path) : (null, null);
            if (fid != id) { list.Remove(e); continue; }
            e.Revision = rev;
            res.Add((e.Path, rev));
        }
        if (list.Count == 0) Parts.Remove(id);
        return res;
    }

    /// <summary>Records every part under <paramref name="root"/> (at most <paramref name="maxFiles"/> files read). The number recorded.</summary>
    public int Scan(string root, int maxFiles = 20000)
    {
        int n = 0, read = 0;
        foreach (var f in PartFiles(root))
        {
            if (read++ >= maxFiles) break;
            if (RecordFile(f)) n++;
        }
        return n;
    }

    /// <summary>
    /// The part and assembly files under a folder (assemblies too: they are components of other assemblies), skipping
    /// build, package and version-control folders.
    /// </summary>
    public static IEnumerable<string> PartFiles(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir, "*" + CadDocument.FileExtension).Concat(Directory.GetFiles(dir, "*" + AssemblyDocument.FileExtension)).ToArray(); dirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) yield return f;
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            for (int i = dirs.Length - 1; i >= 0; i--)
                if (!SkipDirs.Contains(System.IO.Path.GetFileName(dirs[i])) && !System.IO.Path.GetFileName(dirs[i]).StartsWith('.')) stack.Push(dirs[i]);
        }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(b).TrimEnd('\\', '/'),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

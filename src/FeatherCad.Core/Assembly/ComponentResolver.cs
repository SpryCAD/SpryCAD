using FeatherCad.Core.Model;

namespace FeatherCad.Core.Assembly;

/// <summary>Where a component's part was found.</summary>
public enum PartStatus
{
    /// <summary>Where the assembly last saw it (relative to the assembly, or at its absolute path).</summary>
    Found,
    /// <summary>Somewhere else (renamed, moved): the reference was updated.</summary>
    Moved,
    /// <summary>Embedded in the assembly.</summary>
    Embedded,
    /// <summary>Not found: the component shows its display cache until it is.</summary>
    Missing,
}

/// <summary>What became of one referenced part.</summary>
public sealed record PartResolution(PartStatus Status, string? Path, bool NewRevision, string? Note);

/// <summary>
/// Finds the files of an assembly's parts by identity, never asking: each part is looked for
/// <list type="number">
/// <item>where the assembly last saw it, relative to the assembly, then at its absolute path (a file there with
/// another identity is not taken: a different part now has that name);</item>
/// <item>in the parts index (every part opened or saved is recorded there: renamed or moved parts);</item>
/// <item>where the parts it was last seen with went (a folder moved or copied as a whole: the same relative place
/// under the new folder);</item>
/// <item>in the assembly's folder and the search roots (each file's identity read).</item>
/// </list>
/// Parts found elsewhere get their references updated (<see cref="ResolveResult.Changed"/>) and one line in
/// <see cref="ResolveResult.Notes"/>; parts not found are Missing (the assembly still opens, from its caches). A part
/// saved before identities (no id) is found by path only.
/// </summary>
public static class ComponentResolver
{
    public sealed class ResolveResult
    {
        /// <summary>By part (id, or path for a part without one); every component of a part shares its resolution.</summary>
        public Dictionary<string, PartResolution> Parts { get; } = new();
        /// <summary>One line per part found elsewhere, missing, or ambiguous.</summary>
        public List<string> Notes { get; } = new();
        /// <summary>References were updated (the assembly has unsaved changes).</summary>
        public bool Changed { get; set; }

        public PartResolution Of(Component c) => Parts[Key(c.Part)];
    }

    public static string Key(PartRef r) => r.PartId ?? ("path:" + (r.AbsolutePath ?? r.Path ?? r.Embedded ?? ""));

    public static ResolveResult Resolve(AssemblyDocument asm, string? asmPath, PartIndex? index = null, IEnumerable<string>? searchRoots = null)
    {
        var res = new ResolveResult();
        var asmDir = asmPath != null ? Path.GetDirectoryName(Path.GetFullPath(asmPath)) : null;
        // one reference per part (its components share it); references updated together
        var refs = asm.Components.GroupBy(c => Key(c.Part)).ToDictionary(g => g.Key, g => g.Select(c => c.Part).ToList());
        var pending = new List<string>();
        // folders that moved: (old folder, new folder), learned from parts found elsewhere
        var moves = new List<(string from, string to)>();

        string Name(PartRef r) => Path.GetFileNameWithoutExtension(r.Path ?? r.AbsolutePath ?? r.Embedded ?? "part");

        void Found(string key, PartStatus status, string path, string? fileRevision, string? note)
        {
            var first = refs[key][0];
            var full = Path.GetFullPath(path);
            var rel = asmDir != null ? Relative(asmDir, full) : null;
            bool changed = first.AbsolutePath == null || !PartIndex.SamePath(first.AbsolutePath, full) || first.Path != rel;
            if (changed)
            {
                foreach (var r in refs[key]) { r.AbsolutePath = full; r.Path = rel; }
                res.Changed = true;
            }
            var newRev = first.PartId != null && fileRevision != first.Revision;
            res.Parts[key] = new PartResolution(status, full, newRev, note);
            if (note != null) res.Notes.Add(note);
            if (first.PartId != null) index?.Record(first.PartId, fileRevision, full);
        }

        // 1. embedded, and where last seen
        foreach (var (key, list) in refs)
        {
            var r = list[0];
            if (r.Embedded != null && asm.Files.ContainsKey(r.Embedded)) { res.Parts[key] = new PartResolution(PartStatus.Embedded, null, false, null); continue; }
            string? wrong = null;
            foreach (var cand in new[] { asmDir != null && r.Path != null ? Path.Combine(asmDir, r.Path) : null, r.AbsolutePath })
            {
                if (cand == null || !File.Exists(cand)) continue;
                var (id, rev) = DocumentContainer.ReadIdentity(cand);
                if (r.PartId == null || id == r.PartId)
                {
                    // (found only at its absolute path: the assembly moved, not the part; its relative path is updated)
                    Found(key, PartStatus.Found, cand, rev, null);
                    goto next;
                }
                wrong ??= cand;
            }
            if (wrong != null) res.Notes.Add($"{Name(r)}: {wrong} is a different part now; looking for {Name(r)} elsewhere.");
            pending.Add(key);
        next:;
        }

        // 2. the parts index
        foreach (var key in pending.ToList())
        {
            var r = refs[key][0];
            if (r.PartId == null || index == null) continue;
            var hits = index.Lookup(r.PartId);
            if (hits.Count == 0) continue;
            var best = Best(hits, r);
            var note = $"{Name(r)} was found in a new place: {best.path}" + (hits.Count > 1 ? $" ({hits.Count} copies; the one {(best.revision == r.Revision ? "of the revision last used" : "nearest the old place")})" : "");
            Learn(r, best.path);
            Found(key, PartStatus.Moved, best.path, best.revision, note);
            pending.Remove(key);
        }

        // 3. where the parts it was seen with went (repeated: each find teaches more moves)
        bool progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            foreach (var key in pending.ToList())
            {
                var r = refs[key][0];
                if (r.AbsolutePath == null) continue;
                foreach (var (from, to) in moves)
                {
                    var old = Path.GetFullPath(r.AbsolutePath);
                    if (!old.StartsWith(from.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    var cand = Path.Combine(to, Path.GetRelativePath(from, old));
                    if (!File.Exists(cand)) continue;
                    var (id, rev) = DocumentContainer.ReadIdentity(cand);
                    if (r.PartId != null && id != r.PartId) continue;
                    Learn(r, cand);
                    Found(key, PartStatus.Moved, cand, rev, $"{Name(r)} was found in a new place: {cand} (moved with the other parts)");
                    pending.Remove(key); progress = true;
                    break;
                }
            }
        }

        // 4. the assembly's folder and the search roots, by identity
        if (pending.Count > 0)
        {
            var wanted = pending.Select(k => refs[k][0]).Where(r => r.PartId != null).ToDictionary(r => r.PartId!, r => r);
            var roots = new List<string>();
            if (asmDir != null) roots.Add(asmDir);
            if (searchRoots != null) roots.AddRange(searchRoots);
            if (index != null) roots.AddRange(index.Roots);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (wanted.Count == 0) break;
                foreach (var f in PartIndex.PartFiles(root))
                {
                    if (!seen.Add(f)) continue;
                    var (id, rev) = DocumentContainer.ReadIdentity(f);
                    if (id == null) continue;
                    index?.Record(id, rev, f);
                    if (!wanted.TryGetValue(id, out var r)) continue;
                    var key = Key(r);
                    Found(key, PartStatus.Moved, f, rev, $"{Name(r)} was found in a new place: {f}");
                    pending.Remove(key); wanted.Remove(id);
                    if (wanted.Count == 0) break;
                }
            }
        }

        foreach (var key in pending)
        {
            var r = refs[key][0];
            res.Parts[key] = new PartResolution(PartStatus.Missing, null, false, $"{Name(r)} was not found (last at {r.AbsolutePath ?? r.Path}); shown from the assembly's copy.");
            res.Notes.Add(res.Parts[key].Note!);
        }
        return res;

        // a part found elsewhere: its old folder went to its new one (others may have gone with it)
        void Learn(PartRef r, string newPath)
        {
            if (r.AbsolutePath == null) return;
            string? from = Path.GetDirectoryName(Path.GetFullPath(r.AbsolutePath)), to = Path.GetDirectoryName(Path.GetFullPath(newPath));
            // the deepest folders that differ: ...\Old\Sub\a.fcprt -> ...\New\Sub\a.fcprt teaches Old -> New
            while (from != null && to != null && string.Equals(Path.GetFileName(from), Path.GetFileName(to), StringComparison.OrdinalIgnoreCase)
                   && Path.GetDirectoryName(from) is { } pf && Path.GetDirectoryName(to) is { } pt)
            { from = pf; to = pt; }
            if (from != null && to != null && !moves.Contains((from, to))) moves.Add((from, to));
        }
    }

    /// <summary>Of several files of a part: the revision last used, else the one nearest the old place.</summary>
    static (string path, string? revision) Best(List<(string path, string? revision)> hits, PartRef r)
    {
        var same = hits.Where(h => h.revision == r.Revision).ToList();
        var pool = same.Count > 0 ? same : hits;
        if (r.AbsolutePath == null) return pool[0];
        var old = Path.GetFullPath(r.AbsolutePath);
        return pool.OrderByDescending(h => CommonPrefix(h.path, old)).ThenBy(h => h.path, StringComparer.OrdinalIgnoreCase).First();
    }

    static int CommonPrefix(string a, string b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
        return n;
    }

    /// <summary>A path relative to a folder, with forward slashes; null on another drive.</summary>
    public static string? Relative(string dir, string full)
    {
        if (!string.Equals(Path.GetPathRoot(dir), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetRelativePath(dir, full).Replace('\\', '/');
    }
}

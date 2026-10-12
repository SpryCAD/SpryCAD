using SpryCad.Kernel;

namespace SpryCad.Core.Storage;

public enum FaceChange { Unchanged, Changed, Added, Removed }

/// <summary>
/// Two states of a part compared face by face, for the visual diff (the file format spec, section 23, face mode):
/// faces are matched by their persistent names, and a face kept by name is unchanged when its exact geometry is the
/// same (the display mesh's <see cref="FaceRange.GeometryHash"/>), changed when not; a name in one state only is a
/// face added or removed.
/// </summary>
public static class VisualDiff
{
    /// <summary>Each face name of either mesh, and what happened to it from <paramref name="older"/> to <paramref name="newer"/>.</summary>
    public static Dictionary<string, FaceChange> Compare(DisplayMesh older, DisplayMesh newer)
    {
        var a = Hashes(older); var b = Hashes(newer);
        var d = new Dictionary<string, FaceChange>();
        foreach (var (name, h) in b)
            d[name] = !a.TryGetValue(name, out var old) ? FaceChange.Added
                // (a geometry hash of 0 is not known: taken as changed, never as the same)
                : h != 0 && h == old ? FaceChange.Unchanged : FaceChange.Changed;
        foreach (var name in a.Keys) if (!b.ContainsKey(name)) d[name] = FaceChange.Removed;
        return d;
    }

    // a face's facets share its name (and its hash): the first range's
    static Dictionary<string, ulong> Hashes(DisplayMesh m)
    {
        var d = new Dictionary<string, ulong>();
        foreach (var f in m.Faces) d.TryAdd(f.Name, f.GeometryHash);
        return d;
    }

    /// <summary>The feature a face name was made by: its first part (a feature id, or "id-k" for an imported body).</summary>
    public static string FeatureOf(string faceName)
    {
        var first = faceName.Split('.')[0];
        var dash = first.IndexOf('-');
        return dash > 0 ? first[..dash] : first;
    }
}

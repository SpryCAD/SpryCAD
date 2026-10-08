using FeatherCad.Core.Assembly;
using FeatherCad.Core.Model;

namespace FeatherCad.App.Session;

/// <summary>
/// The user's parts index (<see cref="PartIndex"/>): every part opened or saved in the app is recorded, so
/// assemblies find parts that were renamed or moved. Kept in the application data folder, saved on each change.
/// </summary>
public static class PartsIndexStore
{
    static readonly object _lock = new();
    static PartIndex? _index;

    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpryCAD", "parts-index.json");

    /// <summary>Runs <paramref name="use"/> on the index (loaded on first use) and saves it afterwards.</summary>
    public static T Use<T>(Func<PartIndex, T> use)
    {
        lock (_lock)
        {
            _index ??= PartIndex.Load(StorePath);
            var r = use(_index);
            try { _index.Save(StorePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the index is a convenience */ }
            return r;
        }
    }

    /// <summary>Records a part just opened or saved (nothing for a part without an identity).</summary>
    public static void Record(CadDocument doc, string path)
    {
        if (doc.Id == null) return;
        Use(ix => { ix.Record(doc.Id, doc.Revision, path); return 0; });
    }
}

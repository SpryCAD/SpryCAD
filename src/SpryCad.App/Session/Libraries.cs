using Avalonia.Threading;
using SpryCad.Core.Library;

namespace SpryCad.App.Session;

/// <summary>
/// The standards libraries in use (section 34): the central library set an admin configured (or the user chose), read
/// off the UI thread into <see cref="LibraryLink.Current"/>, again whenever <see cref="Reload"/> is called (a timer,
/// Options). Reading is cheap when nothing changed: only the share's files' fingerprints are checked.
/// </summary>
public static class Libraries
{
    static int _loading;

    /// <summary>What the last load could not read (shown in Options; never fatal: the copies last read, or the built-in standards, are used).</summary>
    public static IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();

    /// <summary>When the set in use changed (on the UI thread).</summary>
    public static event Action? Changed;

    public static (string? path, LibraryConfigSource source) Location => LibrarySync.Locate(UserSettings.LibrariesPath);

    /// <summary>Reads the library set again in the background (a load already running is not repeated).</summary>
    public static void Reload()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        var path = Location.path;
        Task.Run(() =>
        {
            LibrarySync.Result r;
            try { r = LibrarySync.Load(path); }
            catch (Exception ex)
            {
                App.Log(ex, "Reading the standards libraries");
                r = new LibrarySync.Result(LibraryLink.Current, new[] { ex.Message });
            }
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _loading, 0);
                var before = LibraryLink.Current;
                LibraryLink.Current = r.Set;
                Problems = r.Problems;
                if (!Same(before, r.Set)) Changed?.Invoke();
            });
        });
    }

    static bool Same(LibrarySet a, LibrarySet b) =>
        a.Libraries.Select(l => (l.LibraryId, l.RevisionId)).SequenceEqual(b.Libraries.Select(l => (l.LibraryId, l.RevisionId)));
}

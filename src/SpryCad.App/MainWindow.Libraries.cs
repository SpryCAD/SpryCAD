using Avalonia.Threading;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Library;

namespace SpryCad.App;

// Standards libraries (section 34): loading the central set, and a part's library updates.
public partial class MainWindow
{
    DispatcherTimer? _libraryTimer;

    /// <summary>Reads the library set now and every few minutes (cheap when nothing changed: the share's files' fingerprints).</summary>
    void InitLibraries()
    {
        Libraries.Changed += () => NoteLibraryUpdates();
        Libraries.Reload();
        _libraryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _libraryTimer.Tick += (_, _) => Libraries.Reload();
        _libraryTimer.Start();
    }

    /// <summary>Says in the status bar when the active part's library data has changed in the libraries (nothing otherwise).</summary>
    void NoteLibraryUpdates()
    {
        if (IsAssemblyActive) return;
        var n = LibraryLink.Updates(Session.Doc).Count;
        if (n > 0) Status($"Library update available for {n} item{(n == 1 ? "" : "s")} in this part. See Tools > Library Updates.");
    }

    /// <summary>Lists the part's library updates and, if the user accepts, takes them in (one undoable edit).</summary>
    async Task LibraryUpdatesCommand()
    {
        if (IsAssemblyActive) { Status("Library updates are available for parts only. Open the part."); return; }
        var updates = LibraryLink.Updates(Session.Doc);
        if (updates.Count == 0)
        {
            var from = string.Join(", ", LibraryLink.Current.Libraries.Select(l => l.Name));
            await Dialogs.Message(this, "Library Updates", $"The part's library data is up to date ({from}).");
            return;
        }
        var text = "These library values have changed since the part used them:\n\n"
            + string.Join("\n", updates.Select(u => "• " + u.Describe()))
            + "\n\nUpdate the part? Holes sized from these are sized again; the change is in the part's history and can be undone.";
        if (!await Dialogs.YesNo(this, "Library Updates", text, "Update", "Keep Current")) return;
        Session.Change(() => LibraryLink.Accept(Session.Doc, updates));
        Status($"Updated {updates.Count} library item{(updates.Count == 1 ? "" : "s")}.");
        RefreshScene();
    }
}

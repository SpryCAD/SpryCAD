using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Storage;

namespace SpryCad.App;

/// <summary>
/// The Revisions pane (task pane, as Open Parts): the part's saved revisions, newest first, each with a preview of the
/// part as it was then, what it changed in green (faces added) and amber (faces changed), when, by whom, its tags and
/// what it changed in words. Clicking one (or Enter) makes the part as it was then: a revert, one change that Undo
/// takes back, saved as a new revision. The revision the part is now is marked; unsaved changes are a row of their
/// own at the top. Right-click: open it read-only, its changes, compare it with now in 3D, tag it, the History window.
/// </summary>
public partial class MainWindow
{
    DockPanel? _revisionsView;
    ListBox _revisionsList = null!;
    TextBlock _revisionsNote = null!;
    List<HistoryEntry> _revisionEntries = new();
    // what the list was last filled from: the session, its head and changes to its history, the revision it shows
    object? _revisionsKey;
    readonly Dictionary<Guid, Bitmap?> _revisionPreviews = new();
    Image? _revisionsNowImage;

    bool RevisionsShown => _taskMode != TaskMode.Hidden && _taskCurrent == _revisionsPane;

    /// <summary>The pane's content, made once (filled again each time it is shown).</summary>
    Control RevisionsControl()
    {
        if (_revisionsView == null)
        {
            _revisionsList = new ListBox { Background = Palette.Surface, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
            _revisionsList.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());
            _revisionsList.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _revisionsList.SelectedItem is ListBoxItem { Tag: HistoryEntry h }) { RevertFromPane(h); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    if (_taskMode == TaskMode.Overlay) HideTaskPane();
                    else Viewport.Focus();
                    e.Handled = true;
                }
            };
            _revisionsList.ContextMenu = RevisionMenu();
            _revisionsNote = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted, FontSize = TypeScale.Small, Margin = new Thickness(10, 8),
            };
            DockPanel.SetDock(_revisionsNote, Dock.Top);
            _revisionsView = new DockPanel { Children = { _revisionsNote, _revisionsList } };
        }
        _revisionsKey = null; // (read again: tags or the file may have changed while it was hidden)
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshRevisions);
        return _revisionsView;
    }

    /// <summary>
    /// Fills the list again when what it shows has changed (another part, a save, the history edited, a revert, unsaved
    /// changes made): not on every edit, which reads nothing new.
    /// </summary>
    void RefreshRevisions()
    {
        if (_revisionsView == null || !RevisionsShown) return;
        var session = Session;
        var key = (Asm != null ? (object)Asm : session, session.FilePath, session.Doc.Revision, session.HistoryVersion, session.ShownRevision, session.Dirty);
        if (Equals(key, _revisionsKey)) { if (_revisionsNowImage != null) _revisionsNowImage.Source = _tab.Thumb; return; }
        _revisionsKey = key;
        _revisionsNowImage = null;
        _revisionsList.Items.Clear();
        _revisionEntries = new();

        if (Asm != null) { _revisionsNote.Text = "Revisions are listed for parts (assemblies come later)."; return; }
        if (session.FilePath == null) { _revisionsNote.Text = "Save the part to start its history: every save is kept, and shows here."; return; }
        Dictionary<Guid, byte[]?> previews;
        try
        {
            (_revisionEntries, previews) = session.ReadHistory(db =>
            {
                var entries = PartHistory.Read(db);
                return (entries, entries.Where(e => !_revisionPreviews.ContainsKey(e.Revision.RevisionId))
                    .ToDictionary(e => e.Revision.RevisionId, e => RevisionPreview.Get(db, e.Revision.RevisionId)));
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _revisionsNote.Text = "The part's history could not be read: " + ex.Message;
            return;
        }
        if (_revisionPreviews.Count > 2000) _revisionPreviews.Clear();
        foreach (var (id, png) in previews)
        {
            try { _revisionPreviews[id] = png != null ? new Bitmap(new MemoryStream(png)) : null; }
            catch (Exception) { _revisionPreviews[id] = null; } // (a broken preview: shown without)
        }

        var shown = session.ShownRevision;
        _revisionsNote.Text = _revisionEntries.Count == 0 ? "No revisions yet."
            : "Click a revision to go back to it (Undo returns). Green: faces it added; amber: faces it changed.";
        if (shown == null)
        {
            _revisionsNowImage = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, Source = _tab.Thumb };
            var item = RevisionItem(_revisionsNowImage, "Unsaved changes", null, "Save to keep them as a revision.", current: true, tags: Array.Empty<string>());
            item.Tag = null;
            _revisionsList.Items.Add(item);
        }
        foreach (var e in _revisionEntries)
        {
            var img = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, Source = _revisionPreviews.GetValueOrDefault(e.Revision.RevisionId) };
            var sub = e.Revision.Message.Length > 0 ? $"{e.Revision.Author} · {e.Revision.Message}" : e.Revision.Author;
            var current = e.Revision.RevisionId == shown;
            var item = RevisionItem(img, When(e), sub, Describe(e), current, e.Tags);
            item.Tag = e;
            ToolTip.SetTip(item, $"{When(e)}, {e.Revision.Author}" + (e.Revision.Message.Length > 0 ? $"\n{e.Revision.Message}" : "") + $"\n{Describe(e)}"
                + (current ? "\n\nThe part is as it was in this revision." : "\n\nClick to make the part as it was then (Undo returns)."));
            item.Tapped += (_, _) => RevertFromPane(e);
            _revisionsList.Items.Add(item);
            if (current) _revisionsList.SelectedItem = item;
        }
    }

    /// <summary>A row: the preview (or a part sign when there is none), the title (tags first), who and why, what changed.</summary>
    static ListBoxItem RevisionItem(Image img, string title, string? sub, string what, bool current, IReadOnlyList<string> tags)
    {
        var none = Icons.Make("part", 30, Palette.TextMuted);
        none.HorizontalAlignment = HorizontalAlignment.Center; none.VerticalAlignment = VerticalAlignment.Center;
        none.IsVisible = img.Source == null;
        img.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) none.IsVisible = img.Source == null; };
        var picture = new Panel { Width = 64, Height = 64, Children = { none, img } };
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var t in tags)
            head.Children.Add(new Border
            {
                Background = Palette.AccentLight, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock { Text = t, Foreground = Palette.AccentText, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center },
            });
        head.Children.Add(new TextBlock { Text = title, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
        if (current) head.Children.Add(new TextBlock { Text = "  ● now", Foreground = Palette.AccentText, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Margin = new Thickness(10, 0, 0, 0), Children = { head } };
        if (sub != null) text.Children.Add(new TextBlock { Text = sub, FontSize = TypeScale.Small, Foreground = Palette.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = what, FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var dock = new DockPanel { Children = { picture, text } };
        DockPanel.SetDock(picture, Dock.Left);
        return new ListBoxItem { Content = dock, Padding = new Thickness(6, 4) };
    }

    /// <summary>A revision clicked: the part made as it was then (nothing for the one it is now).</summary>
    void RevertFromPane(HistoryEntry e)
    {
        var session = Session;
        if (Asm != null || session.ShownRevision == e.Revision.RevisionId) return;
        if (session.ReadOnly) { Status("The part is read-only: it cannot be reverted here."); return; }
        try { RevertToRevision(session, e, session.Title.TrimEnd(' ', '*')); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            _ = Dialogs.Message(this, "Revert", ex.Message);
        }
    }

    /// <summary>The right-click menu of a revision.</summary>
    ContextMenu RevisionMenu()
    {
        HistoryEntry? Target() => _revisionsList.SelectedItem is ListBoxItem { Tag: HistoryEntry h } ? h : null;
        string Name() => Session.Title.TrimEnd(' ', '*');
        async Task Do(string title, Func<Task> act)
        {
            try { await act(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
            {
                await Dialogs.Message(this, title, ex.Message);
            }
        }
        var revert = new MenuItem { Header = "_Go Back to This" };
        revert.Click += (_, _) => { if (Target() is { } e) RevertFromPane(e); };
        var open = new MenuItem { Header = "_Open Read-Only" };
        open.Click += (_, _) => _ = Do("Open Revision", async () => { if (Target() is { } e) await OpenRevisionTab(Session, e, Name()); });
        var changes = new MenuItem { Header = "What It _Changed..." };
        changes.Click += (_, _) => { if (Target() is { } e) CompareCommand(Session, e, Name(), this); };
        var compare3d = new MenuItem { Header = "Compare with _Now in 3D..." };
        compare3d.Click += (_, _) => _ = Do("Compare", () =>
        {
            if (Target() is not { } e) return Task.CompletedTask;
            var then = Session.LoadRevision(e.Revision.RevisionId);
            var now = Session.Doc.Clone();
            VisualCompare(then, When(e), now, "now", PartDescribe.Changes(then, now), this);
            return Task.CompletedTask;
        });
        var tag = new MenuItem { Header = "_Tag..." };
        tag.Click += (_, _) => _ = Do("Tag Revision", async () =>
        {
            if (Target() is not { } e) return;
            if (await Dialogs.Input(this, "Tag Revision", "Tag (a name for this revision, e.g. Rev A, Sent to shop):", "") is not { } t || t.Trim().Length == 0) return;
            var moved = _revisionEntries.FirstOrDefault(x => x != e && x.Tags.Contains(t.Trim()));
            if (moved != null && !await Dialogs.YesNo(this, "Tag Revision", $"\"{t.Trim()}\" tags the revision of {When(moved)}. Move it to this one?", "Move", "Cancel")) return;
            Session.EditHistory(db => PartHistory.Tag(db, e.Revision.RevisionId, t));
        });
        var history = new MenuItem { Header = "_History Window..." };
        history.Click += (_, _) => HistoryCommand();
        var menu = new ContextMenu { Items = { revert, open, changes, compare3d, new Separator(), tag, new Separator(), history } };
        menu.Opening += (_, _) =>
        {
            var e = Target();
            revert.IsEnabled = e != null && e.Revision.RevisionId != Session.ShownRevision && !Session.ReadOnly;
            open.IsEnabled = changes.IsEnabled = compare3d.IsEnabled = tag.IsEnabled = e != null;
        };
        return menu;
    }
}

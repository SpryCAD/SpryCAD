using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.UI;
using SpryCad.Core.Storage;

namespace SpryCad.App;

/// <summary>
/// File > History: the part's saved revisions (newest first) with who saved each when, its tags and what it changed;
/// an old revision opened read-only; tags given, renamed and removed; and the file kept small (Compact, Clear Cache,
/// Purge Older). Changes to the history are saved to the part's file by the next save, like any edit.
/// </summary>
public partial class MainWindow
{
    void HistoryCommand()
    {
        if (Asm != null) { Status("History is kept for parts (assemblies come later)."); return; }
        var session = Session;
        if (session.History() == null) { _ = Dialogs.Message(this, "History", "The part has no history yet: every save of it is kept, from its first."); return; }
        var name = session.Title.TrimEnd(' ', '*');

        var list = new ListBox { SelectionMode = SelectionMode.Single };
        var usage = new TextBlock { FontSize = TypeScale.Small, Foreground = Palette.TextMuted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        List<HistoryEntry> entries = new();
        void Fill(Guid? select = null)
        {
            entries = session.History() ?? new();
            list.ItemsSource = entries.Select((e, i) => Row(e, i == 0)).ToList();
            var at = select is { } s ? entries.FindIndex(e => e.Revision.RevisionId == s) : -1;
            list.SelectedIndex = at >= 0 ? at : entries.Count > 0 ? 0 : -1;
            usage.Text = session.Usage() is { } u ? UsageText(u) : "";
        }
        HistoryEntry? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < entries.Count ? entries[list.SelectedIndex] : null;

        var open = new Button { Content = "Open Read-Only" };
        var tag = new Button { Content = "Tag..." };
        var rename = new Button { Content = "Rename Tag..." };
        var untag = new Button { Content = "Remove Tag" };
        var purge = new Button { Content = "Purge Older..." };
        var compact = new Button { Content = "Compact" };
        var clear = new Button { Content = "Clear Cache" };
        var close = new Button { Content = "Close", IsCancel = true };
        void Sync()
        {
            var e = Selected();
            open.IsEnabled = tag.IsEnabled = e != null;
            rename.IsEnabled = untag.IsEnabled = e is { Tags.Count: > 0 };
            purge.IsEnabled = e != null && list.SelectedIndex < entries.Count - 1;
        }
        list.SelectionChanged += (_, _) => Sync();

        var note = new TextBlock
        {
            Text = "Every save of the part, newest first. Tags, purging and compacting are saved to the file with the part's next save.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextSecondary, Margin = new Thickness(0, 0, 0, 8),
        };
        var side = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { open, tag, rename, untag, new Separator(), purge } };
        foreach (var b in side.Children.OfType<Button>()) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { compact, clear, close } };
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right); bottom.Children.Add(usage);
        var dock = new DockPanel();
        DockPanel.SetDock(note, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom); DockPanel.SetDock(side, Dock.Right);
        dock.Children.Add(note); dock.Children.Add(bottom); dock.Children.Add(side); dock.Children.Add(list);
        var w = ToolWindow($"History: {name}", dock, 720, 520);

        // what the history's changes need from the user: a name, a confirmation; errors said where they happen
        async Task Do(Func<Task> act)
        {
            try { await act(); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
            {
                await Dialogs.Message(w, "History", ex.Message);
            }
        }
        async Task<string?> TagOf(HistoryEntry e, string verb) =>
            e.Tags.Count == 1 ? e.Tags[0] : await Dialogs.Input(w, verb, $"Which tag ({string.Join(", ", e.Tags)}):", e.Tags[0]);

        async Task OpenSelected() => await Do(async () =>
        {
            if (Selected() is not { } e) return;
            var doc = session.LoadRevision(e.Revision.RevisionId);
            if (await NewTab() is null) return;
            var label = e.Tags.Count > 0 ? e.Tags[0] : e.Revision.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            Session.OpenRevision(doc, $"{name} ({label})");
            _tab.FitAfterLoad = Session.RequestedRevision;
            UpdateTabHeader(_tab);
            Status($"Opened {name} as it was at {label}, read-only: Save As keeps it as a part of its own.");
        });
        open.Click += (_, _) => _ = OpenSelected();
        tag.Click += (_, _) => _ = Do(async () =>
        {
            if (Selected() is not { } e) return;
            if (await Dialogs.Input(w, "Tag Revision", "Tag (a name for this revision, e.g. Rev A, Sent to shop):", "") is not { } t || t.Trim().Length == 0) return;
            var moved = entries.FirstOrDefault(x => x != e && x.Tags.Contains(t.Trim()));
            if (moved != null && !await Dialogs.YesNo(w, "Tag Revision", $"\"{t.Trim()}\" tags the revision of {When(moved)}. Move it to this one?", "Move", "Cancel")) return;
            session.EditHistory(db => PartHistory.Tag(db, e.Revision.RevisionId, t));
            Fill(e.Revision.RevisionId);
        });
        rename.Click += (_, _) => _ = Do(async () =>
        {
            if (Selected() is not { } e || e.Tags.Count == 0 || await TagOf(e, "Rename Tag") is not { } old) return;
            if (await Dialogs.Input(w, "Rename Tag", $"New name for \"{old}\":", old) is not { } t || t.Trim() == old) return;
            session.EditHistory(db => PartHistory.RenameTag(db, old, t));
            Fill(e.Revision.RevisionId);
        });
        untag.Click += (_, _) => _ = Do(async () =>
        {
            if (Selected() is not { } e || e.Tags.Count == 0 || await TagOf(e, "Remove Tag") is not { } t) return;
            session.EditHistory(db => PartHistory.Untag(db, t));
            Fill(e.Revision.RevisionId);
        });
        purge.Click += (_, _) => _ = Do(async () =>
        {
            if (Selected() is not { } e) return;
            var (gone, tags) = session.ReadHistory(db => PartHistory.PurgePlan(db, e.Revision.RevisionId));
            if (gone.Count == 0) return;
            var lost = tags.Count == 0 ? "" : $"\n\nThese tags go with them: {string.Join(", ", tags)}.";
            if (!await Dialogs.YesNo(w, "Purge Older Revisions",
                $"Forget the {gone.Count} revision{(gone.Count == 1 ? "" : "s")} before {When(e)}? This revision becomes the first; the older ones cannot be opened again once the part is saved.{lost}",
                "Purge", "Cancel")) return;
            session.EditHistory(db => PartHistory.Purge(db, e.Revision.RevisionId));
            Fill(e.Revision.RevisionId);
            Status($"Purged {gone.Count} older revision{(gone.Count == 1 ? "" : "s")} of {name}: saved with the part.");
        });
        compact.Click += (_, _) => _ = Do(() => { session.EditHistory(PartHistory.Compact); Fill(Selected()?.Revision.RevisionId); return Task.CompletedTask; });
        clear.Click += (_, _) => _ = Do(() => { session.EditHistory(PartHistory.ClearCache); Fill(Selected()?.Revision.RevisionId); return Task.CompletedTask; });
        close.Click += (_, _) => w.Close();
        list.DoubleTapped += (_, _) => _ = OpenSelected();
        Fill();
        Sync();
        w.Show(this);
    }

    static string When(HistoryEntry e) => e.Revision.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>One revision in the list: when, by whom, its tags, and what it changed.</summary>
    static Control Row(HistoryEntry e, bool latest)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = When(e), FontWeight = FontWeight.SemiBold });
        head.Children.Add(new TextBlock { Text = e.Revision.Author, Foreground = Palette.TextSecondary });
        foreach (var t in e.Tags)
            head.Children.Add(new Border
            {
                Background = Palette.AccentLight, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0),
                Child = new TextBlock { Text = t, Foreground = Palette.AccentText, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center },
            });
        if (latest) head.Children.Add(new TextBlock { Text = "(last saved)", Foreground = Palette.TextMuted, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center });
        var what = new TextBlock { Text = Describe(e), Foreground = Palette.TextSecondary, FontSize = TypeScale.Small, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 2, Margin = new Thickness(2, 3), Children = { head, what } };
        if (e.Revision.Message.Length > 0) body.Children.Insert(1, new TextBlock { Text = e.Revision.Message, TextWrapping = TextWrapping.Wrap });
        return body;
    }

    /// <summary>What a revision changed, in a line: "Added Fillet3 · Changed Boss-Extrude1 · Removed Cut1 · Reordered".</summary>
    static string Describe(HistoryEntry e)
    {
        var c = e.Changes;
        if (e.Revision.ParentId == null) return c.Added.Count == 0 ? "First save" : $"First save: {Names(c.Added)}";
        var parts = new List<string>();
        if (c.Added.Count > 0) parts.Add("Added " + Names(c.Added));
        if (c.Modified.Count > 0) parts.Add("Changed " + Names(c.Modified));
        if (c.Removed.Count > 0) parts.Add("Removed " + Names(c.Removed));
        if (c.Reordered) parts.Add("Reordered features");
        if (c.OtherChanges > 0) parts.Add(c.OtherChanges == 1 ? "1 other change (equations, material, settings or files)" : $"{c.OtherChanges} other changes (equations, material, settings or files)");
        return parts.Count == 0 ? "No change to the model" : string.Join(" · ", parts);
    }

    static string Names(IReadOnlyList<string> names) => names.Count <= 6 ? string.Join(", ", names) : string.Join(", ", names.Take(5)) + $" and {names.Count - 5} more";

    static string UsageText(FileUsage u)
    {
        static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0.#} KB" : $"{b / 1048576.0:0.#} MB";
        return $"{Size(u.FileBytes)} in all ({u.Revisions} revision{(u.Revisions == 1 ? "" : "s")}): model {Size(u.ModelBytes)}, embedded files {Size(u.EmbeddedFileBytes)}, " +
               $"display {Size(u.DisplayBytes)}, older revisions {Size(u.HistoryBytes)}, preview {Size(u.ThumbnailBytes)}";
    }
}

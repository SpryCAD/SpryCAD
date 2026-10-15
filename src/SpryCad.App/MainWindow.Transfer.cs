using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Storage;

namespace SpryCad.App;

/// <summary>
/// Make External and Make Virtual (the file format spec, section 8): an embedded part or sub-assembly made a file of
/// its own, a part used from its file embedded, each with its history; the original of a part made virtual marked
/// (a banner when it is opened), optionally moved to the Recycle Bin when nothing else uses it; and changes made to
/// it afterwards pulled in.
/// </summary>
public partial class MainWindow
{
    async Task MakeExternalCommand(string path)
    {
        if (Asm is not { } a || path.Contains('/') || a.Model.Doc.Find(path) is not { } c || c.Part.Embedded is not { } name) return;
        if (_parts.Any(t => t.EmbeddedIn == a && t.EmbeddedName == name)) { await Dialogs.Message(this, "Make External", $"{c.Name} is open in another tab. Save or close it first."); return; }
        var ext = c.IsAssembly ? AssemblyDocument.FileExtension : CadDocument.FileExtension;
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Make {c.Name.Split('<')[0]} External",
            DefaultExtension = ext.TrimStart('.'),
            SuggestedFileName = Path.GetFileName(name),
            FileTypeChoices = new[] { new FilePickerFileType(c.IsAssembly ? "SpryCAD Assembly" : "SpryCAD Part") { Patterns = new[] { "*" + ext } } },
        });
        if (f?.TryGetLocalPath() is not { } target) return;
        a.BeginChange();
        try
        {
            await Task.Run(() => PartsIndexStore.Use(_ => { a.Model.MakeExternal(c, target); return true; }));
            a.EndChange();
            Status($"{c.Name.Split('<')[0]} saved externally as {Path.GetFileName(target)}. Save the assembly to keep the change.");
        }
        catch (Exception ex) { App.Log(ex, "Make external"); a.AbandonChange(); await Dialogs.Message(this, "Make External", ex.Message); }
    }

    async Task MakeVirtualCommand(string path)
    {
        if (Asm is not { } a || path.Contains('/') || a.Model.Doc.Find(path) is not { } c || c.Part.Embedded != null) return;
        var what = c.Name.Split('<')[0];
        if (!await Dialogs.YesNo(this, "Make Virtual",
            $"Embed {what} in the assembly with its history? The original file is kept, but later changes to it will not update this assembly.",
            "Make Virtual", "Cancel")) return;
        a.BeginChange();
        MadeVirtual done;
        try
        {
            done = await Task.Run(() => a.Model.MakeVirtual(c, UserSettings.AuthorName));
            a.EndChange();
        }
        catch (Exception ex) { App.Log(ex, "Make virtual"); a.AbandonChange(); await Dialogs.Message(this, "Make Virtual", ex.Message); return; }
        var file = Path.GetFileName(done.OriginalPath);
        Status($"{what} made virtual." + (done.Marked ? $" {file} is marked as virtualized." : $" Could not mark {file}; it may be read-only, in use or from an older version."));
        // the original moved to the Recycle Bin: only when nothing else uses it, and once the assembly holding it is saved
        if (done.UsedBy.Count > 0)
        {
            await Dialogs.Message(this, "Make Virtual", $"{file} is kept because other assemblies use it: {string.Join(", ", done.UsedBy.Select(Path.GetFileName))}.");
            return;
        }
        if (a.FilePath == null) return;
        if (!await Dialogs.YesNo(this, "Make Virtual", $"No other assembly in this folder uses {file}. Save the assembly and move {file} to the {TrashName}?", "Save and Move", "Keep File")) return;
        if (!await Save()) return;
        Status(Trash(done.OriginalPath) ? $"Moved {file} to the {TrashName}." : $"Could not move {file} to the {TrashName}. The file is kept.");
    }

    static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    /// <summary>Moves a file to the Recycle Bin (Trash elsewhere), never deleting it outright. False if it could not.</summary>
    static bool Trash(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return !File.Exists(path);
            }
            var psi = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("osascript", new[] { "-e", $"tell application \"Finder\" to delete POSIX file \"{path.Replace("\"", "\\\"")}\"" })
                : new ProcessStartInfo("gio", new[] { "trash", path });
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
            return !File.Exists(path);
        }
        catch (Exception ex) { App.Log(ex, "Move to trash"); return false; }
    }

    /// <summary>
    /// After an assembly opens: its virtual parts whose original files changed since they were made virtual. Only the
    /// file changed: its changes pulled in, if the user wants. Both changed: the user chooses which to keep (what each
    /// changed is listed); there is no merging yet.
    /// </summary>
    async Task OfferChangedOriginals(AssemblySession a)
    {
        List<OriginalChanged> changed;
        try { changed = await Task.Run(() => a.Model.ChangedOriginals()); }
        catch (Exception ex) { App.Log(ex, "Changed originals"); return; }
        foreach (var o in changed)
        {
            bool take;
            if (o.Kind == Divergence.FileAhead)
                take = await Dialogs.YesNo(this, "Changes to a Virtual Part's File",
                    $"{o.PartName} is virtual in this assembly, and its original file {Path.GetFileName(o.File)} has changed. Update the virtual part from the file?", "Update", "Keep Current");
            else
            {
                string differences;
                try
                {
                    var mine = DocumentContainer.Load(a.Model.Doc.Files[o.Embedded], o.Embedded);
                    var theirs = CadDocument.Load(o.File);
                    var list = PartDescribe.Changes(mine, theirs);
                    differences = list.Count == 0 ? "" : "\n\nDifferences: " + string.Join("; ", list.Take(8).Select(c => c.Text)) + (list.Count > 8 ? $"; and {list.Count - 8} more" : "");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { differences = ""; }
                take = await Dialogs.YesNo(this, "A Virtual Part and Its File Both Changed",
                    $"{o.PartName} has changed both in this assembly and in its original file {Path.GetFileName(o.File)}. Use the file's version? Changes made in the assembly will be lost.{differences}",
                    "Use File Version", "Keep Assembly Version");
            }
            if (!take) continue;
            a.BeginChange();
            try { await Task.Run(() => a.Model.TakeOriginal(o)); a.EndChange(); Status($"{o.PartName} updated from its file. Save the assembly to keep the change."); }
            catch (Exception ex) { App.Log(ex, "Pull changes in"); a.AbandonChange(); await Dialogs.Message(this, "Pull Changes In", ex.Message); }
        }
    }

    // ------------------------------------------------------------------------------------------
    // pinned components: a revision (or tag) of the part, not its latest

    async Task PinCommand(string path)
    {
        if (Asm is not { } a || path.Contains('/') || a.Model.Doc.Find(path) is not { } c || c.IsAssembly) return;
        List<HistoryEntry> history;
        try { history = await Task.Run(() => a.Model.HistoryOf(c)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) { await Dialogs.Message(this, "Pin to Revision", ex.Message); return; }
        if (history.Count == 0) { await Dialogs.Message(this, "Pin to Revision", $"{c.Name.Split('<')[0]} has no revision history. The part file was not found or is from an older version."); return; }
        var list = new ListBox { ItemsSource = history.Select((e, i) => Row(e, i == 0)).ToList(), SelectedIndex = 0 };
        var pin = new Button { Content = "Pin", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var head = new TextBlock
        {
            Text = $"Shows {c.Name} at the selected revision regardless of later changes to the part. A tagged revision can follow its tag.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 8, 0, 0), Children = { pin, cancel } };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(head); dock.Children.Add(buttons); dock.Children.Add(list);
        var w = ToolWindow($"Pin {c.Name} to a Revision", dock, 560, 440);
        HistoryEntry? chosen = null;
        pin.Click += (_, _) => { if (list.SelectedIndex >= 0) chosen = history[list.SelectedIndex]; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        list.DoubleTapped += (_, _) => { if (list.SelectedIndex >= 0) chosen = history[list.SelectedIndex]; w.Close(); };
        await w.ShowDialog(this);
        if (chosen is not { } e) return;
        string? tag = null;
        if (e.Tags.Count > 0 && await Dialogs.YesNo(this, "Pin to Revision",
            $"The revision is tagged {e.Tags[0]}. Follow the tag if it moves to another revision, or keep this revision?",
            "Follow the Tag", "This Revision")) tag = e.Tags[0];
        a.BeginChange();
        try
        {
            await Task.Run(() => a.Model.Pin(c, e.Revision.RevisionId, tag));
            if (a.Model.HasMates) a.Model.SolveMates();
            a.EndChange();
            Status($"{c.Name} pinned to {(tag ?? When(e))}.");
        }
        catch (Exception ex) { App.Log(ex, "Pin component"); a.AbandonChange(); await Dialogs.Message(this, "Pin to Revision", ex.Message); }
    }

    void Unpin(string path)
    {
        if (Asm is not { } a || path.Contains('/') || a.Model.Doc.Find(path) is not { } c) return;
        a.BeginChange();
        try
        {
            a.Model.Pin(c, null);
            if (a.Model.HasMates) a.Model.SolveMates();
            a.EndChange();
            Status($"{c.Name} unpinned. It now uses the latest revision.");
        }
        catch (Exception ex) { App.Log(ex, "Unpin component"); a.AbandonChange(); Status(ex.Message); }
    }

    // ------------------------------------------------------------------------------------------
    // a part made virtual somewhere, opened from its file

    Control? _supersededBanner;
    TextBlock? _supersededTitle, _supersededText;

    /// <summary>
    /// The banner over a part whose file was made virtual in an assembly: it opens read-only, and says that changes made
    /// here do not reach that assembly. Open Assembly, Edit Anyway, or Keep as Separate Part (the mark removed).
    /// </summary>
    void UpdateSupersededBanner()
    {
        var s = _tab?.Asm == null ? _tab?.Session : null;
        if (s?.Superseded is not { } mark || s.FilePath is not { } path) { if (_supersededBanner != null) _supersededBanner.IsVisible = false; return; }
        if (_supersededBanner == null)
        {
            _supersededText = Card.Text("");
            var openAsm = new Button { Content = "Open Assembly" };
            var anyway = new Button { Content = "Edit Anyway" };
            var keep = new Button { Content = "Keep as Separate Part" };
            ToolTip.SetTip(keep, "Detaches this part from the assembly and removes the virtual-part mark when saved.");
            openAsm.Click += (_, _) =>
            {
                if (Session.Superseded?.AssemblyFile(Session.FilePath!) is { } f && File.Exists(f)) _ = OpenFileAsync(f);
                else Status("The assembly file was not found.");
            };
            anyway.Click += (_, _) =>
            {
                Session.ReadOnly = false; Session.Superseded = null;
                UpdateSupersededBanner();
                Status("Editing enabled. Changes saved here will not update the assembly.");
            };
            keep.Click += (_, _) =>
            {
                try
                {
                    Session.EditHistory(Supersede.Clear);
                    Session.ReadOnly = false; Session.Superseded = null;
                    UpdateSupersededBanner();
                    Status("Part detached. Save it to remove the virtual-part mark.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException) { Status(ex.Message); }
            };
            _supersededBanner = Card.Make(CardKind.Info, "", new Control[] { _supersededText }, new[] { openAsm, anyway, keep }, out var title);
            _supersededTitle = title;
            GraphicsArea.Children.Add(_supersededBanner);
        }
        var asmName = mark.AssemblyPath != null ? Path.GetFileNameWithoutExtension(mark.AssemblyPath) : "an assembly";
        _supersededTitle!.Text = $"{Path.GetFileNameWithoutExtension(path)} was made virtual in {asmName}";
        _supersededText!.Text = $"On {mark.Utc.ToLocalTime():g} by {mark.User}. Changes made here will not appear in that assembly. Opened read-only.";
        _supersededBanner.IsVisible = true;
    }
}

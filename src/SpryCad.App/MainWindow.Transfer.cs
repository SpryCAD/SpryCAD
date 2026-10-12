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
        if (_parts.Any(t => t.EmbeddedIn == a && t.EmbeddedName == name)) { await Dialogs.Message(this, "Make External", $"{c.Name} is open in a tab of its own: save or close it first."); return; }
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
            Status($"{c.Name.Split('<')[0]} is now {Path.GetFileName(target)}, with its history: saving the assembly records it.");
        }
        catch (Exception ex) { App.Log(ex, "Make external"); a.AbandonChange(); await Dialogs.Message(this, "Make External", ex.Message); }
    }

    async Task MakeVirtualCommand(string path)
    {
        if (Asm is not { } a || path.Contains('/') || a.Model.Doc.Find(path) is not { } c || c.Part.Embedded != null) return;
        var what = c.Name.Split('<')[0];
        if (!await Dialogs.YesNo(this, "Make Virtual",
            $"Embed {what} in the assembly, with its history? Its file stays where it is (other assemblies may use it); changes made to the file afterwards no longer reach this assembly, and the file says so when opened.",
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
        Status($"{what} is embedded now" + (done.Marked ? $"; {file} is marked as made virtual here." : $"; {file} could not be marked (read-only, in use, or saved by an older SpryCAD)."));
        // the original moved to the Recycle Bin: only when nothing else uses it, and once the assembly holding it is saved
        if (done.UsedBy.Count > 0)
        {
            await Dialogs.Message(this, "Make Virtual", $"{file} is kept: other assemblies use it ({string.Join(", ", done.UsedBy.Select(Path.GetFileName))}).");
            return;
        }
        if (a.FilePath == null) return;
        if (!await Dialogs.YesNo(this, "Make Virtual", $"No other assembly in this folder uses {file}. Save the assembly and move {file} to the {TrashName}?", "Save and Move", "Keep the File")) return;
        if (!await Save()) return;
        Status(Trash(done.OriginalPath) ? $"Moved {file} to the {TrashName}." : $"{file} could not be moved to the {TrashName}: it is kept.");
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
                    $"{o.PartName} was made virtual in this assembly, and its file ({Path.GetFileName(o.File)}) was changed since. Pull those changes in?", "Pull In", "Keep as It Is");
            else
            {
                string differences;
                try
                {
                    var mine = DocumentContainer.Load(a.Model.Doc.Files[o.Embedded], o.Embedded);
                    var theirs = CadDocument.Load(o.File);
                    var list = PartDescribe.Changes(mine, theirs);
                    differences = list.Count == 0 ? "" : "\n\nThe file differs from the virtual part in: " + string.Join("; ", list.Take(8).Select(c => c.Text)) + (list.Count > 8 ? $"; and {list.Count - 8} more" : "");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { differences = ""; }
                take = await Dialogs.YesNo(this, "A Virtual Part and Its File Both Changed",
                    $"{o.PartName} was changed in this assembly and in its file ({Path.GetFileName(o.File)}) since it was made virtual. Keep the assembly's, or take the file's (the assembly's changes are lost)?{differences}",
                    "Take the File's", "Keep the Assembly's");
            }
            if (!take) continue;
            a.BeginChange();
            try { await Task.Run(() => a.Model.TakeOriginal(o)); a.EndChange(); Status($"{o.PartName} taken from its file: saving the assembly records it."); }
            catch (Exception ex) { App.Log(ex, "Pull changes in"); a.AbandonChange(); await Dialogs.Message(this, "Pull Changes In", ex.Message); }
        }
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
            ToolTip.SetTip(keep, "This part goes its own way: the mark is removed (saved with the part).");
            openAsm.Click += (_, _) =>
            {
                if (Session.Superseded?.AssemblyFile(Session.FilePath!) is { } f && File.Exists(f)) _ = OpenFileAsync(f);
                else Status("The assembly is no longer where it was.");
            };
            anyway.Click += (_, _) =>
            {
                Session.ReadOnly = false; Session.Superseded = null;
                UpdateSupersededBanner();
                Status("Editing anyway: changes saved here do not reach the assembly the part was made virtual in.");
            };
            keep.Click += (_, _) =>
            {
                try
                {
                    Session.EditHistory(Supersede.Clear);
                    Session.ReadOnly = false; Session.Superseded = null;
                    UpdateSupersededBanner();
                    Status("Kept as a part of its own: saving it removes the mark.");
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

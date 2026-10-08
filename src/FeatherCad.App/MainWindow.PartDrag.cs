using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FeatherCad.App.Session;
using FeatherCad.App.UI;
using FeatherCad.App.Viewport;
using FeatherCad.Core.Assembly;
using FeatherCad.Kernel;
using Transform = FeatherCad.Kernel.Transform;

namespace FeatherCad.App;

/// <summary>
/// Parts dragged into an assembly (as from SolidWorks' open documents): a part's row in the Open Parts list dropped on
/// the graphics area is inserted where it is dropped, on the design tree beside the assembly. Part files dragged from
/// Explorer are inserted the same way.
/// </summary>
public partial class MainWindow
{
    /// <summary>An open part's file, dragged from the list of open parts.</summary>
    static readonly DataFormat<string> PartDragFormat = DataFormat.CreateStringApplicationFormat("SpryCAD.Part");

    /// <summary>The graphics area and the assembly's tree take parts while an assembly is the active document.</summary>
    void InitPartDrop()
    {
        foreach (var target in new Control[] { Viewport, _asmTree })
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DroppedParts(e) is { Count: > 0 } ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
            target.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                if (DroppedParts(e) is not { Count: > 0 } parts) return;
                e.Handled = true;
                Point? at = target == Viewport ? e.GetPosition(Viewport) : null;
                await InsertDropped(parts, at);
            });
        }
    }

    /// <summary>What is being dragged, as part files the active assembly can take (nothing while a part is active).</summary>
    List<string>? DroppedParts(DragEventArgs e)
    {
        if (Asm == null) return null;
        if (e.DataTransfer.TryGetValue(PartDragFormat) is { Length: > 0 } path) return new List<string> { path };
        return e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => Path.GetExtension(p).Equals(Core.Model.CadDocument.FileExtension, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    async Task InsertDropped(List<string> paths, Point? at)
    {
        if (Asm is not { } a) return;
        var open = paths.Select(p => _parts.FirstOrDefault(t => t.Asm == null && t.Session.FilePath is { } f && PartIndex.SamePath(f, p))).ToList();
        Activate();
        a.BeginChange();
        string? last = null;
        foreach (var p in paths)
        {
            try
            {
                var first = a.Model.Doc.Components.Count == 0;
                var anchor = AssemblyCenter(a.Model);
                var beside = PlaceBeside(a.Model);
                var c = await Task.Run(() => PartsIndexStore.Use(_ => a.Model.Insert(p, beside, embed: false)));
                // the first part stays at the origin; the others are centered where they were dropped (on the tree: beside)
                if (!first && at is { } pt && a.Model.DisplayOf(c) is { } pd && !pd.Mesh.Bounds.IsEmpty)
                    c.Placement = Placement.From(Transform.Translation(DropPoint(pt, anchor) - pd.Mesh.Bounds.Center));
                last = c.Id;
            }
            catch (Exception ex) { await Dialogs.Message(this, "Insert Component", $"{Path.GetFileName(p)} could not be inserted: {ex.Message}"); }
        }
        if (last == null) { a.AbandonChange(); return; }
        a.Selected = last;
        a.EndChange();
        Viewport.Focus();
        var unsaved = open.Where(t => t is { Session.Dirty: true }).Select(t => TitleOf(t!).TrimEnd(' ', '*')).ToList();
        Status($"Inserted {string.Join(", ", paths.Select(Path.GetFileNameWithoutExtension))}." +
               (unsaved.Count > 0 ? $" {string.Join(", ", unsaved)} has unsaved changes: inserted as last saved." : " Drag it to move it; Shift+drag turns it."));
    }

    /// <summary>The middle of what the assembly holds (the origin when empty): dropped parts go at its depth.</summary>
    static Vec3 AssemblyCenter(AssemblyModel m)
    {
        var b = Box3.Empty;
        foreach (var c in m.Doc.Components)
            if (m.DisplayOf(c) is { } pd && !pd.Mesh.Bounds.IsEmpty)
            {
                var t = c.Placement.ToTransform();
                b.Add(t.Point(pd.Mesh.Bounds.Min)); b.Add(t.Point(pd.Mesh.Bounds.Max));
            }
        return b.IsEmpty ? Vec3.Zero : b.Center;
    }

    /// <summary>Where the mouse points, on the plane facing the view through <paramref name="anchor"/>.</summary>
    Vec3 DropPoint(Point screen, Vec3 anchor)
    {
        var (o, d) = Viewport.Camera.Ray(screen.X, screen.Y);
        var n = Camera.V(-Viewport.Camera.Back);
        var den = Vec3.Dot(d, n);
        return Math.Abs(den) < 1e-9 ? anchor : o + d * (Vec3.Dot(anchor - o, n) / den);
    }

    /// <summary>
    /// A row of the Open Parts list dragged out: while an assembly is the active document a press on a part's row does
    /// not switch to the part (that would leave the assembly); it is dragged, or, released where it was, switched to.
    /// </summary>
    void AllowPartDrag(ListBox list, ListBoxItem item, PartTab t)
    {
        PointerPressedEventArgs? press = null;
        Point start = default;
        item.AddHandler(PointerPressedEvent, (_, e) =>
        {
            press = null;
            if (Asm == null || t.Asm != null || !e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            // the row's ✕ is a button of its own
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
            press = e; start = e.GetPosition(item);
            e.Pointer.Capture(item);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        item.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (press == null) return;
            var d = e.GetPosition(item) - start;
            if (d.X * d.X + d.Y * d.Y < 16) return;
            var p = press; press = null;
            e.Pointer.Capture(null);
            if (t.Session.FilePath == null) { Status($"Save {TitleOf(t).TrimEnd(' ', '*')} first: an assembly refers to parts by their files."); return; }
            Status($"Drop {TitleOf(t).TrimEnd(' ', '*')} in the graphics area to insert it where it is dropped.");
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(PartDragFormat, t.Session.FilePath));
            await DragDrop.DoDragDropAsync(p, data, DragDropEffects.Copy);
        }, RoutingStrategies.Tunnel);
        item.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (press == null) return;
            press = null;
            e.Pointer.Capture(null);
            // a click: the part is switched to, as a click on its row always does
            list.SelectedItem = item;
            item.Focus();
        }, RoutingStrategies.Tunnel);
    }
}

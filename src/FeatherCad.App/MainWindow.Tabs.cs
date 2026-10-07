using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FeatherCad.App.Session;

namespace FeatherCad.App;

/// <summary>
/// Document tabs: one tab per open part. Each tab owns its <see cref="DocumentSession"/> (document, undo, rebuilds,
/// unsaved state) and remembers its view; the feature tree, viewport and sketcher are shared and show the active
/// tab's part. Inactive parts keep rebuilding in the background and are shown as they are when switched to.
/// </summary>
public partial class MainWindow
{
    sealed class PartTab(DocumentSession session)
    {
        public readonly DocumentSession Session = session;
        public Border Header = null!;
        public TextBlock Label = null!;
        /// <summary>The camera when last left; null until then (the view is fitted to the part instead).</summary>
        public (System.Numerics.Quaternion rotation, System.Numerics.Vector3 target, float halfHeight)? View;
        /// <summary>
        /// Fit the view once this rebuild request's result arrives (just opened or imported); null for none. A
        /// request number, not a flag: an earlier, empty result can arrive first and must not use up the fit.
        /// </summary>
        public int? FitAfterLoad;
        /// <summary>Preview for the list of parts, and the rebuild it shows (re-rendered when the part rebuilds).</summary>
        public Avalonia.Media.Imaging.Bitmap? Thumb;
        public int ThumbRevision = -1;
    }

    readonly List<PartTab> _parts = new();
    PartTab _tab;
    int _untitledCount;

    static readonly IBrush TabActiveBrush = Brushes.White;
    static readonly IBrush TabInactiveBrush = new SolidColorBrush(Color.Parse("#FFDCE3EC"));
    static readonly IBrush TabHoverBrush = new SolidColorBrush(Color.Parse("#FFEEF2F7"));
    static readonly IBrush TabBorderBrush = new SolidColorBrush(Color.Parse("#FFC5CFDC"));

    /// <summary>Adds a tab for a session (not activated). Its events reach the window only while it is active.</summary>
    PartTab AddTab(DocumentSession s)
    {
        s.UntitledName = "Part" + ++_untitledCount;
        var t = new PartTab(s);
        s.Rebuilt += () => { if (s == Session) OnRebuilt(); };
        s.DocumentChanged += () => { UpdateTabHeader(t); if (s == Session) OnDocumentChanged(); };
        s.BusyChanged += busy => { if (s == Session) { BusyText.IsVisible = busy; BusyText.Text = "Rebuilding…"; } };
        s.Progress += p => { if (s == Session && s.Busy) BusyText.Text = p.Text; };

        t.Label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
        var close = new Button
        {
            Content = "✕", FontSize = 10, Padding = new Thickness(4, 0), MinHeight = 0, MinWidth = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Close (Ctrl+W)");
        close.Click += (_, _) => _ = CloseTab(t);
        t.Header = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { t.Label, close } },
            Padding = new Thickness(10, 4, 4, 4), CornerRadius = new CornerRadius(4, 4, 0, 0),
            BorderBrush = TabBorderBrush, BorderThickness = new Thickness(1, 1, 1, 0), Cursor = new Cursor(StandardCursorType.Hand),
        };
        t.Header.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(t.Header).Properties;
            if (props.IsMiddleButtonPressed) _ = CloseTab(t);
            else if (props.IsLeftButtonPressed) _ = SwitchTo(t);
        };
        t.Header.PointerEntered += (_, _) => { if (t != _tab) t.Header.Background = TabHoverBrush; };
        t.Header.PointerExited += (_, _) => UpdateTabHeader(t);

        _parts.Add(t);
        DocTabs.Children.Add(t.Header);
        UpdateTabHeader(t);
        UpdatePartsButton();
        return t;
    }

    // ------------------------------------------------------------------------------------------
    // the list of all open parts (the button at the right end of the tabs, Ctrl+E)

    void InitPartsList()
    {
        PartsButton.Click += (_, _) => ShowPartsList();
        ToolTip.SetTip(PartsButton, "All open parts (Ctrl+E): arrow keys switch between them");
        DocTabs.HiddenCountChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdatePartsButton);
        UpdatePartsButton();
    }

    void UpdatePartsButton()
    {
        var hidden = DocTabs.HiddenCount;
        PartsButton.Content = hidden > 0 ? $"+{hidden} more ▾" : $"{_parts.Count} part{(_parts.Count == 1 ? "" : "s")} ▾";
        PartsButton.FontWeight = hidden > 0 ? FontWeight.SemiBold : FontWeight.Normal;
    }

    /// <summary>
    /// A drop-down list of every open part with a preview. Selecting one (arrow keys, click) shows it right away, so
    /// a set of parts can be flipped through; Enter or Esc closes the list. Typing jumps to a part by name.
    /// </summary>
    void ShowPartsList()
    {
        var list = new ListBox { MaxHeight = 560, Width = 360, Background = Brushes.White };
        var images = new List<(PartTab tab, Image image)>();
        foreach (var t in _parts)
        {
            var img = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, Source = t.Thumb };
            var title = new TextBlock { Text = t.Session.Title, FontSize = 13, FontWeight = t == _tab ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
            var path = new TextBlock { Text = t.Session.FilePath ?? "Not saved yet", FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.PathSegmentEllipsis };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(10, 0, 0, 0), Children = { title, path } };
            var row = new DockPanel { Children = { img, text } };
            DockPanel.SetDock(img, Dock.Left);
            var item = new ListBoxItem { Content = row, Tag = t, Padding = new Thickness(6, 4) };
            Avalonia.Controls.Primitives.TextSearch.SetText(item, t.Session.Title);
            list.Items.Add(item);
            images.Add((t, img));
        }
        var flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
        list.SelectedIndex = _parts.IndexOf(_tab);
        bool reverting = false;
        list.SelectionChanged += async (_, _) =>
        {
            if (reverting || list.SelectedItem is not ListBoxItem { Tag: PartTab t } || t == _tab) return;
            if (!await SwitchTo(t))
            {
                // leaving the current part was cancelled (a pending edit): the selection goes back to it
                reverting = true; list.SelectedIndex = _parts.IndexOf(_tab); reverting = false;
            }
            foreach (var it in list.Items.OfType<ListBoxItem>())
                if (it.Content is DockPanel { Children: [_, StackPanel { Children: [TextBlock tb, ..] }] })
                    tb.FontWeight = it.Tag == _tab ? FontWeight.SemiBold : FontWeight.Normal;
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { flyout.Hide(); e.Handled = true; }
        };
        flyout.Opened += (_, _) =>
        {
            var i = Math.Max(0, list.SelectedIndex);
            list.ScrollIntoView(i);
            (list.ContainerFromIndex(i) ?? (Control)list).Focus(NavigationMethod.Directional);
        };
        flyout.ShowAt(PartsButton);
        foreach (var (t, img) in images) _ = LoadThumb(t, img);
    }

    /// <summary>The part's preview (rendered off the UI thread when the part has rebuilt since the last one).</summary>
    async Task LoadThumb(PartTab t, Image img)
    {
        var s = t.Session;
        var result = s.Result;
        var rev = s.ResultRevision;
        if (result == null || (t.Thumb != null && t.ThumbRevision == rev)) return;
        var color = s.Doc.Color;
        var png = await Task.Run(() =>
        {
            try { return result.State.Bodies.Count == 0 ? null : Kernel.Thumbnail.Render(result.State.Bodies.Select(b => (b.Mesh, color)), 128); }
            catch { return null; } // a preview is never worth an error
        });
        if (png == null) return;
        t.Thumb = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png));
        t.ThumbRevision = rev;
        img.Source = t.Thumb;
    }

    void UpdateTabHeader(PartTab t)
    {
        t.Label.Text = t.Session.Title;
        t.Label.FontWeight = t == _tab ? FontWeight.SemiBold : FontWeight.Normal;
        t.Header.Background = t == _tab ? TabActiveBrush : TabInactiveBrush;
        ToolTip.SetTip(t.Header, t.Session.FilePath ?? "Not saved yet");
    }

    /// <summary>An unsaved new part nobody has worked on: opening a file reuses its tab instead of adding one.</summary>
    bool IsUntouched(PartTab t) =>
        t.Session.FilePath == null && !t.Session.Dirty && !t.Session.CanUndo && t.Session.Doc.Features.Count == 0
        && !(t == _tab && HasPendingEdit);

    /// <summary>Opens an empty tab and makes it active (null if leaving the current one was cancelled).</summary>
    async Task<PartTab?> NewTab()
    {
        var t = AddTab(new DocumentSession());
        if (await SwitchTo(t)) return t;
        RemoveTab(t);
        return null;
    }

    /// <summary>
    /// Makes a tab active. The sketch or feature being edited in the current tab is kept or discarded first (as
    /// before a save); false if that was cancelled.
    /// </summary>
    async Task<bool> SwitchTo(PartTab t)
    {
        if (t == _tab) return true;
        if (!await ResolvePendingEdit()) return false;
        LeaveModes();
        _viewSelector.Close();
        StopArrowSpin();
        var cam = Viewport.Camera;
        _tab.View = (cam.Rotation, cam.Target, cam.HalfHeight);
        Selection.Clear();
        Viewport.ClearPivot();

        var old = _tab;
        _tab = t;
        Session = t.Session;
        UpdateTabHeader(old);
        UpdateTabHeader(t);
        DocTabs.Active = t.Header; // shown even when the tabs before it fill the strip

        StatusRebuild.Text = "";
        BusyText.IsVisible = Session.Busy;
        OnDocumentChanged();
        OnRebuilt();
        if (t.View is { } v) { cam.Rotation = v.rotation; cam.Target = v.target; cam.HalfHeight = v.halfHeight; Viewport.Redraw(); }
        else if (Session.Result != null) Viewport.StandardView("Trimetric", fit: true);
        else t.FitAfterLoad = t.Session.RequestedRevision;
        return true;
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous tab.</summary>
    async Task CycleTab(int step)
    {
        if (_parts.Count < 2) return;
        var i = _parts.IndexOf(_tab);
        await SwitchTo(_parts[((i + step) % _parts.Count + _parts.Count) % _parts.Count]);
    }

    /// <summary>
    /// Closes a tab, asking to save its changes first (it is shown while asked). Closing the last one leaves a new
    /// empty part, as the window always shows one. False if cancelled.
    /// </summary>
    async Task<bool> CloseTab(PartTab t)
    {
        if (!await SwitchTo(t)) return false;
        if (!await ConfirmDiscard()) return false;
        var i = _parts.IndexOf(t);
        PartTab next;
        if (_parts.Count > 1) next = _parts[i > 0 ? i - 1 : 1];
        else { next = AddTab(new DocumentSession()); next.Session.New(); }
        // the closed part's edits were resolved by ConfirmDiscard: switching away cannot be cancelled
        await SwitchTo(next);
        RemoveTab(t);
        return true;
    }

    void RemoveTab(PartTab t)
    {
        _parts.Remove(t);
        DocTabs.Children.Remove(t.Header);
        UpdatePartsButton();
    }
}

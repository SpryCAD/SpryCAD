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
        s.Rebuilt += () => { if (s == Session) OnRebuilt(); if (_partRows.TryGetValue(t, out var row)) _ = LoadThumb(t, row.Image); };
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
        ToolTip.SetTip(PartsButton, "All open parts (Ctrl+E), docked at the right or in a window: arrow keys switch between them");
        DocTabs.HiddenCountChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdatePartsButton);
        PartsDockFloat.Click += (_, _) => { UserSettings.PartsDocked = false; UserSettings.Save(); MovePartsList(docked: false); };
        PartsDockClose.Click += (_, _) => HidePartsList();
        PartsDockSplitter.DragCompleted += (_, _) => { UserSettings.PartsDockWidth = (int)MainGrid.ColumnDefinitions[4].ActualWidth; UserSettings.Save(); };
        UpdatePartsButton();
        // shown at start when it was docked when the app last closed
        if (UserSettings.PartsDocked && UserSettings.PartsDockOpen) Avalonia.Threading.Dispatcher.UIThread.Post(() => MovePartsList(docked: true, focus: false));
    }

    void UpdatePartsButton()
    {
        var hidden = DocTabs.HiddenCount;
        PartsButton.Content = hidden > 0 ? $"+{hidden} more ▾" : $"{_parts.Count} part{(_parts.Count == 1 ? "" : "s")} ▾";
        PartsButton.FontWeight = hidden > 0 ? FontWeight.SemiBold : FontWeight.Normal;
        RefreshPartsList();
    }

    sealed record PartRow(ListBoxItem Item, TextBlock Title, TextBlock Path, Image Image, Button Close);

    Window? _partsWindow;
    ListBox? _partsList;
    readonly Dictionary<PartTab, PartRow> _partRows = new();
    bool _partsSyncing;

    /// <summary>The list is docked at the right of the main window (else floating, or not shown).</summary>
    bool PartsDocked => PartsDock.IsVisible;

    /// <summary>
    /// The list of every open part with a preview: docked at the right of the main window (its width set by the
    /// splitter) or in a window of its own (moved and sized where it was last), as it was last. Selecting a part
    /// (arrow keys, click) shows it right away, so a set of parts can be flipped through; typing jumps to a part by
    /// name; Enter goes back to the part, Esc closes the window (or, docked, goes back to the part). A row's ✕ (on
    /// the row under the mouse and the selected one), Delete or Ctrl+W close a part. It follows the parts as they
    /// open, close, change and are switched to another way.
    /// </summary>
    void ShowPartsList()
    {
        if (_partsWindow != null) { _partsWindow.Activate(); FocusPartsList(); return; }
        if (PartsDocked) { FocusPartsList(); return; }
        MovePartsList(UserSettings.PartsDocked);
    }

    /// <summary>The list, made once and moved between the dock and the window.</summary>
    ListBox PartsListControl()
    {
        if (_partsList != null) return _partsList;
        var list = new ListBox { Background = Brushes.White, BorderThickness = new Thickness(0) };
        list.SelectionChanged += async (_, _) =>
        {
            if (_partsSyncing || list.SelectedItem is not ListBoxItem { Tag: PartTab t } || t == _tab) return;
            // leaving the current part may be cancelled (a pending edit): the list then shows it again
            await SwitchTo(t);
            RefreshPartsList();
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (_partsWindow != null) _partsWindow.Close(); else { Activate(); Viewport.Focus(); }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter) { Activate(); Viewport.Focus(); e.Handled = true; }
            else if (e.Key == Key.Delete || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            {
                if (list.SelectedItem is ListBoxItem { Tag: PartTab t }) ClosePartFromList(t);
                e.Handled = true;
            }
        };
        _partsList = list;
        RefreshPartsList();
        return list;
    }

    /// <summary>Shows the list docked at the right or in its window (taking it from where it is).</summary>
    void MovePartsList(bool docked, bool focus = true)
    {
        var list = PartsListControl();
        // take it out of where it is (the window closes without dropping the list)
        if (_partsWindow is { } old) { _partsMoving = true; old.Close(); _partsMoving = false; }
        if (PartsDocked) SetPartsDock(false);
        if (list.Parent is ContentControl host) host.Content = null;

        if (docked)
        {
            PartsDockBody.Content = list;
            SetPartsDock(true);
            if (focus) Avalonia.Threading.Dispatcher.UIThread.Post(FocusPartsList);
            return;
        }
        var dock = new Button { Content = "Dock Right", FontSize = 11, Padding = new Thickness(8, 1), MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4) };
        ToolTip.SetTip(dock, "Dock the list at the right of the main window");
        var body = new ContentControl { Content = list };
        var content = new DockPanel { Children = { dock, body } };
        DockPanel.SetDock(dock, Dock.Top);
        var w = new Window
        {
            Title = "Open Parts", Content = content, ShowInTaskbar = false, CanResize = true,
            CanMinimize = false, CanMaximize = false,
            MinWidth = 220, MinHeight = 120, Width = 360, Height = 520,
        };
        w.Opened += (_, _) => ToolWindowStyle(w);
        // where it was last, else beside the button, below the tabs
        if (UserSettings.PartsWindow is { } b && b.w > 0)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = new PixelPoint(b.x, b.y);
            w.Width = b.w; w.Height = b.h;
        }
        else if (PartsButton.PointToScreen(new Point(PartsButton.Bounds.Width, PartsButton.Bounds.Height)) is var corner)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = new PixelPoint(corner.X - (int)(w.Width * RenderScaling), corner.Y + 4);
        }
        dock.Click += (_, _) => { UserSettings.PartsDocked = true; UserSettings.Save(); MovePartsList(docked: true); };
        if (focus) w.Opened += (_, _) => FocusPartsList();
        w.Closing += (_, _) =>
        {
            UserSettings.PartsWindow = (w.Position.X, w.Position.Y, (int)w.Width, (int)w.Height);
            UserSettings.Save();
        };
        w.Closed += (_, _) =>
        {
            body.Content = null;
            _partsWindow = null;
            if (!_partsMoving) DropPartsList();
        };
        _partsWindow = w;
        w.Show(this);
    }

    bool _partsMoving;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Windows: a tool window's title bar (no app icon, only the close button), as floating palettes have; minimize
    /// and maximize are off on every platform (CanMinimize / CanMaximize).
    /// </summary>
    static void ToolWindowStyle(Window w)
    {
        if (!OperatingSystem.IsWindows() || w.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
        const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        var ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
        // the frame is drawn again with the new style
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Opens or closes the dock column (its width kept between showings).</summary>
    void SetPartsDock(bool open)
    {
        var cols = MainGrid.ColumnDefinitions;
        if (!open && PartsDock.IsVisible) UserSettings.PartsDockWidth = (int)cols[4].ActualWidth;
        cols[3].Width = new GridLength(open ? 4 : 0);
        cols[4].Width = new GridLength(open ? Math.Clamp(UserSettings.PartsDockWidth, 180, 900) : 0);
        cols[4].MinWidth = open ? 180 : 0;
        PartsDockSplitter.IsVisible = open;
        PartsDock.IsVisible = open;
        UserSettings.PartsDockOpen = open;
        UserSettings.Save();
    }

    /// <summary>Hides the list wherever it is.</summary>
    void HidePartsList()
    {
        if (_partsWindow != null) { _partsWindow.Close(); return; }
        if (!PartsDocked) return;
        SetPartsDock(false);
        PartsDockBody.Content = null;
        DropPartsList();
        Viewport.Focus();
    }

    void DropPartsList() { _partsList = null; _partRows.Clear(); }

    /// <summary>Closes a part from the list (asking to save its changes, as its tab's ✕), the list keeping the keyboard.</summary>
    async void ClosePartFromList(PartTab t)
    {
        await CloseTab(t);
        _partsWindow?.Activate();
        FocusPartsList();
    }

    void FocusPartsList()
    {
        if (_partsList is not { } list) return;
        var i = Math.Max(0, list.SelectedIndex);
        list.ScrollIntoView(i);
        (list.ContainerFromIndex(i) ?? (Control)list).Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// Brings the list up to date in place (rows added, removed, renamed, the active part selected): rebuilding it
    /// would lose the keyboard focus in the middle of flipping through parts.
    /// </summary>
    void RefreshPartsList()
    {
        if (_partsList is not { } list) return;
        _partsSyncing = true;
        try
        {
            foreach (var gone in _partRows.Keys.Where(t => !_parts.Contains(t)).ToList())
            {
                list.Items.Remove(_partRows[gone].Item);
                _partRows.Remove(gone);
            }
            for (int i = 0; i < _parts.Count; i++)
            {
                var t = _parts[i];
                if (!_partRows.TryGetValue(t, out var row))
                {
                    var img = new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform, Source = t.Thumb };
                    var title = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
                    var path = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.PathSegmentEllipsis };
                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(10, 0, 0, 0), Children = { title, path } };
                    var close = new Button
                    {
                        Content = "✕", FontSize = 12, Padding = new Thickness(6, 2), MinHeight = 0, MinWidth = 0, Margin = new Thickness(6, 0, 0, 0),
                        Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                        Opacity = 0, Focusable = false,
                    };
                    ToolTip.SetTip(close, "Close (Delete, Ctrl+W)");
                    close.Click += (_, _) => ClosePartFromList(t);
                    var dock = new DockPanel { Children = { img, close, text } };
                    DockPanel.SetDock(img, Dock.Left);
                    DockPanel.SetDock(close, Dock.Right);
                    var item = new ListBoxItem { Content = dock, Tag = t, Padding = new Thickness(6, 4) };
                    // the ✕ on the row under the mouse (and the selected one, see below)
                    item.PointerEntered += (_, _) => close.Opacity = 1;
                    item.PointerExited += (_, _) => close.Opacity = t == _tab ? 1 : 0;
                    row = new PartRow(item, title, path, img, close);
                    _partRows[t] = row;
                    list.Items.Insert(Math.Min(i, list.Items.Count), row.Item);
                    _ = LoadThumb(t, img);
                }
                else if (list.Items.IndexOf(row.Item) != i) { list.Items.Remove(row.Item); list.Items.Insert(i, row.Item); }
                row.Title.Text = t.Session.Title;
                row.Title.FontWeight = t == _tab ? FontWeight.SemiBold : FontWeight.Normal;
                if (!row.Item.IsPointerOver) row.Close.Opacity = t == _tab ? 1 : 0;
                row.Path.Text = t.Session.FilePath ?? "Not saved yet";
                Avalonia.Controls.Primitives.TextSearch.SetText(row.Item, t.Session.Title);
            }
            if (_partRows.TryGetValue(_tab, out var active) && !ReferenceEquals(list.SelectedItem, active.Item))
            {
                list.SelectedItem = active.Item;
                list.ScrollIntoView(active.Item);
            }
        }
        finally { _partsSyncing = false; }
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
        RefreshPartsList();
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

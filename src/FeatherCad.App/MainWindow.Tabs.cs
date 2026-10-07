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
        /// <summary>The file the part was opened from (its STEP model, which a new part does not keep as its path).</summary>
        public string? Source;
        /// <summary>No preview can be made (the part is empty or could not be read): the background pass skips it.</summary>
        public bool NoPreview;
        /// <summary>The part's file could not be read (missing, not a STEP file, a broken part file).</summary>
        public string? LoadError;
        /// <summary>The file was read but its model could not be imported (the Imported feature failed); kept when the model is dropped.</summary>
        public string? ImportError;
        /// <summary>Why the part could not be opened, if it could not: shown in red on its tab and row, and over the view.</summary>
        public string? Error => LoadError ?? ImportError;
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
        s.Rebuilt += () =>
        {
            // an import that failed (an unsupported STEP model): the part is marked, as one whose file could not be read
            if (s.Result is { } r)
            {
                var err = s.Doc.Features.OfType<Core.Model.ImportFeature>().Select(f => r.Status.GetValueOrDefault(f.Id)?.Error).FirstOrDefault(e => e != null);
                if (err != t.ImportError) { t.ImportError = err; UpdateTabHeader(t); if (t == _tab) UpdateLoadBanner(); }
            }
            if (s == Session) OnRebuilt();
            // the preview follows the part (new ones are the background pass's job while it runs)
            if (_partRows.TryGetValue(t, out var row) && (!_previewsRunning || t.Thumb != null)) _ = LoadThumb(t, row.Image);
        };
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
        ToolTip.SetTip(PartsButton, "All open parts (Ctrl+E), in the task pane at the right: arrow keys switch between them");
        DocTabs.HiddenCountChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdatePartsButton);
        UpdatePartsButton();
    }

    void UpdatePartsButton()
    {
        var hidden = DocTabs.HiddenCount;
        PartsButton.Content = hidden > 0 ? $"+{hidden} more ▾" : $"{_parts.Count} part{(_parts.Count == 1 ? "" : "s")} ▾";
        PartsButton.FontWeight = hidden > 0 ? FontWeight.SemiBold : FontWeight.Normal;
        RefreshPartsList();
    }

    sealed record PartRow(ListBoxItem Item, TextBlock Title, TextBlock Path, Image Image, Control Warning, Button Close);

    static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FFC62828"));

    Window? _partsWindow;
    ListBox? _partsList;
    readonly Dictionary<PartTab, PartRow> _partRows = new();
    bool _partsSyncing;

    /// <summary>
    /// The list of every open part with a preview: in the task pane at the right (pinned or sliding out), or popped
    /// out into a window of its own (moved and sized where it was last). Selecting a part (arrow keys, click) shows
    /// it right away, so a set of parts can be flipped through; typing jumps to a part by name; Enter goes back to the
    /// part, Esc too (tucking the sliding pane away, closing the window). A row's ✕ (on the row under the mouse and
    /// the selected one), Delete or Ctrl+W close a part; right-click closes others or all. It follows the parts as
    /// they open, close, change and are switched to another way.
    /// </summary>
    void ShowPartsList()
    {
        if (_partsWindow != null) { _partsWindow.Activate(); FocusPartsList(); return; }
        if (PartsInTaskPane) { FocusPartsList(); return; }
        ShowTaskPane(_partsPane, focus: true);
    }

    /// <summary>The list, made once and moved between the dock and the window.</summary>
    ListBox PartsListControl()
    {
        if (_partsList != null) return _partsList;
        var list = new ListBox { Background = Brushes.White, BorderThickness = new Thickness(0) };
        // right-click: close this part, the others, or all (the part under the mouse is selected, so shown, first)
        PartTab? Picked() => list.SelectedItem is ListBoxItem { Tag: PartTab t } ? t : null;
        var closeOne = new MenuItem { Header = "_Close", InputGesture = new KeyGesture(Key.Delete) };
        closeOne.Click += (_, _) => { if (Picked() is { } t) ClosePartFromList(t); };
        var closeOthers = new MenuItem { Header = "Close _Others" };
        closeOthers.Click += async (_, _) => { if (Picked() is { } t) { await CloseTabs(_parts.Where(p => p != t).ToList()); FocusPartsList(); } };
        var closeAll = new MenuItem { Header = "Close _All" };
        closeAll.Click += async (_, _) => { await CloseTabs(_parts.ToList()); FocusPartsList(); };
        // the part's file (or the STEP model it was made from), as text on the clipboard
        string? PathOf(PartTab t) => t.Session.SourcePath ?? t.Source;
        var copyPath = new MenuItem { Header = "Copy File _Path" };
        copyPath.Click += async (_, _) =>
        {
            if (Picked() is not { } t || PathOf(t) is not { } p || TopLevel.GetTopLevel(list)?.Clipboard is not { } clip) return;
            await clip.SetTextAsync(p);
            Status("Copied " + p);
        };
        var menu = new ContextMenu { Items = { copyPath, new Separator(), closeOne, closeOthers, new Separator(), closeAll } };
        menu.Opening += (_, _) =>
        {
            closeOne.IsEnabled = Picked() != null;
            closeOthers.IsEnabled = Picked() != null && _parts.Count > 1;
            copyPath.IsEnabled = Picked() is { } t && PathOf(t) != null; // a new part has no file yet
        };
        list.ContextMenu = menu;
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
                if (_partsWindow != null) _partsWindow.Close();
                else if (_taskMode == TaskMode.Overlay) HideTaskPane();
                else { Activate(); Viewport.Focus(); }
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

    /// <summary>Shows the list in the task pane or popped out in its window (taking it from where it is).</summary>
    void MovePartsList(bool docked, bool focus = true)
    {
        var list = PartsListControl();
        // take it out of where it is (the window closes without dropping the list)
        if (_partsWindow is { } old) { _partsMoving = true; old.Close(); _partsMoving = false; }
        if (PartsInTaskPane) PlaceTaskPane(TaskMode.Hidden);
        if (list.Parent is ContentControl host) host.Content = null;

        if (docked)
        {
            ShowTaskPane(_partsPane, focus);
            return;
        }
        var dock = new Button
        {
            Content = UI.Icons.Make("dockright", 14), Padding = new Thickness(4, 2), MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 6, 6, 4),
        };
        ToolTip.SetTip(dock, "Back to the task pane at the right of the main window");
        var body = new ContentControl { Content = list };
        var content = new DockPanel { Children = { dock, body } };
        // a little room around the parts in the floating window, inside the scrolling area: the scroll bar stays at
        // the window's edge (the docked list runs to the panel's edges)
        list.Padding = new Thickness(8, 0, 8, 8);
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
        dock.Click += (_, _) => MovePartsList(docked: true);
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
                    // a part that could not be opened: a red warning sign where its preview would be
                    var warning = UI.Icons.Make("warning", 36, ErrorBrush);
                    warning.HorizontalAlignment = HorizontalAlignment.Center; warning.VerticalAlignment = VerticalAlignment.Center;
                    warning.IsVisible = false;
                    var picture = new Panel { Width = 64, Height = 64, Children = { img, warning } };
                    var dock = new DockPanel { Children = { picture, close, text } };
                    DockPanel.SetDock(picture, Dock.Left);
                    DockPanel.SetDock(close, Dock.Right);
                    var item = new ListBoxItem { Content = dock, Tag = t, Padding = new Thickness(6, 4) };
                    // the ✕ on the row under the mouse (and the selected one, see below)
                    item.PointerEntered += (_, _) => close.Opacity = 1;
                    item.PointerExited += (_, _) => close.Opacity = t == _tab ? 1 : 0;
                    row = new PartRow(item, title, path, img, warning, close);
                    _partRows[t] = row;
                    list.Items.Insert(Math.Min(i, list.Items.Count), row.Item);
                    _ = LoadThumb(t, img);
                }
                else if (list.Items.IndexOf(row.Item) != i) { list.Items.Remove(row.Item); list.Items.Insert(i, row.Item); }
                row.Title.Text = t.Session.Title;
                row.Title.FontWeight = t == _tab ? FontWeight.SemiBold : FontWeight.Normal;
                if (!row.Item.IsPointerOver) row.Close.Opacity = t == _tab ? 1 : 0;
                // could not be opened: the name in red, the problem instead of the path (in full on the tooltip)
                var error = t.Error;
                row.Title.Foreground = error != null ? ErrorBrush : null;
                row.Path.Text = error ?? t.Session.FilePath ?? t.Source ?? "Not saved yet";
                row.Path.Foreground = error != null ? ErrorBrush : Brushes.Gray;
                row.Path.TextTrimming = error != null ? TextTrimming.CharacterEllipsis : TextTrimming.PathSegmentEllipsis;
                row.Warning.IsVisible = error != null && t.Thumb == null;
                ToolTip.SetTip(row.Item, error != null ? $"Could not be opened: {error}\n{t.Source ?? t.Session.FilePath}" : null);
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
    async Task LoadThumb(PartTab t, Image? img = null)
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
        // the row now (it may have been made while the preview was rendered)
        if (_partRows.TryGetValue(t, out var row)) row.Image.Source = t.Thumb;
        else if (img != null) img.Source = t.Thumb;
    }

    // ------------------------------------------------------------------------------------------
    // parts opened in a batch: read when first shown; previews made in the background; memory kept down

    /// <summary>How many parts keep their built model when not shown (the most recently shown): flipping back is instant.</summary>
    const int KeepLoaded = 6;
    readonly List<PartTab> _recent = new();
    bool _previewsRunning;

    /// <summary>Reads a part opened in a batch, the first time it is needed; the problem when it cannot be read.</summary>
    string? LoadPending(PartTab t)
    {
        if (t.Session.TakePending() is not { } path) return null;
        var s = t.Session;
        if (IsStep(path))
        {
            s.New();
            var err = ImportInto(t, path);
            s.MarkClean();
            return err;
        }
        try
        {
            s.Load(path);
            if (!IsAutosave(path)) RecentFiles.Add(path);
            t.FitAfterLoad = s.RequestedRevision;
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// A part was shown: it goes first among the recently shown; the parts shown longest ago drop their built model
    /// (not their document, changes or undo history) and are built again when shown.
    /// </summary>
    void TouchRecent(PartTab t)
    {
        _recent.Remove(t);
        _recent.Insert(0, t);
        foreach (var old in _recent.Skip(KeepLoaded).ToList())
            if (old != _tab && (old.Session.Unload() || old.Session.Result == null)) _recent.Remove(old);
    }

    /// <summary>
    /// Makes the previews of the list of open parts in the background, one part at a time (reading parts not read
    /// yet, building those whose model was dropped), dropping each built model again unless it was shown recently.
    /// Parts that cannot be read are closed, and named in the status bar.
    /// </summary>
    async void StartPreviews()
    {
        if (_previewsRunning) return;
        _previewsRunning = true;
        var failed = new List<string>();
        try
        {
            while (_parts.FirstOrDefault(p => p.Thumb == null && !p.NoPreview) is { } t)
            {
                if (LoadPending(t) is { } err)
                {
                    // kept, marked red (its error shows when it is selected)
                    failed.Add($"{Path.GetFileName(t.Source)} ({err})");
                    t.NoPreview = true;
                    t.LoadError = err;
                    UpdateTabHeader(t);
                    if (t == _tab) UpdateLoadBanner();
                    continue;
                }
                t.Session.EnsureBuilt();
                if (await WaitBuilt(t)) await LoadThumb(t);
                if (t.Thumb == null) t.NoPreview = true; // an empty part, or one that failed to rebuild
                if (t != _tab && !_recent.Contains(t)) t.Session.Unload();
                await Task.Yield();
            }
        }
        finally { _previewsRunning = false; }
        if (failed.Count > 0) Status($"Could not open {failed.Count} file{(failed.Count == 1 ? "" : "s")}: {string.Join("; ", failed)}");
    }

    Border? _loadBanner;
    TextBlock _bannerTitle = null!, _bannerError = null!, _bannerPath = null!;

    /// <summary>
    /// Over the view of a part that could not be opened: why, the file, and Retry (after fixing or replacing it).
    /// Hidden for parts that opened.
    /// </summary>
    void UpdateLoadBanner()
    {
        if (_tab.Error is not { } error) { if (_loadBanner != null) _loadBanner.IsVisible = false; return; }
        if (_loadBanner == null)
        {
            _bannerTitle = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = ErrorBrush, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _bannerError = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            _bannerPath = new TextBlock { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
            var retry = new Button { Content = "Retry", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 3), Margin = new Thickness(0, 6, 0, 0) };
            ToolTip.SetTip(retry, "Read the file again (e.g. after fixing or replacing it)");
            retry.Click += (_, _) => RetryLoad(_tab);
            var icon = UI.Icons.Make("warning", 22, ErrorBrush);
            var head = new DockPanel { Children = { icon, _bannerTitle } };
            DockPanel.SetDock(icon, Dock.Left);
            _bannerTitle.Margin = new Thickness(8, 0, 0, 0);
            _loadBanner = new Border
            {
                Background = Brushes.White, BorderBrush = ErrorBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12), MaxWidth = 520, Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 4 16 0 #30000000"),
                Child = new StackPanel { Spacing = 6, Children = { head, _bannerError, _bannerPath, retry } },
            };
            GraphicsArea.Children.Add(_loadBanner);
        }
        _bannerTitle.Text = $"{_tab.Session.Title.TrimEnd(' ', '*')} could not be opened";
        _bannerError.Text = error;
        _bannerPath.Text = _tab.Source ?? _tab.Session.FilePath ?? "";
        _loadBanner.IsVisible = true;
    }

    /// <summary>Reads a part that could not be opened again (from its file, now; or when it is next shown).</summary>
    void RetryLoad(PartTab t)
    {
        if ((t.Source ?? t.Session.FilePath) is not { } path) return;
        t.LoadError = null; t.ImportError = null; t.NoPreview = false; t.Thumb = null;
        t.Session.Defer(path);
        if (t == _tab)
        {
            if (LoadPending(t) is { } err) t.LoadError = err;
            t.Session.EnsureBuilt();
            t.FitAfterLoad = t.Session.RequestedRevision;
            UpdateLoadBanner();
        }
        UpdateTabHeader(t);
        StartPreviews();
    }

    /// <summary>Waits until the part's latest rebuild has a result (false if the tab closed or it takes over two minutes).</summary>
    async Task<bool> WaitBuilt(PartTab t)
    {
        var s = t.Session;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_parts.Contains(t) && (s.Result == null || s.ResultRevision < s.RequestedRevision || s.Busy))
        {
            if (sw.Elapsed.TotalMinutes > 2) return false;
            await Task.Delay(30);
        }
        return _parts.Contains(t) && s.Result != null;
    }

    void UpdateTabHeader(PartTab t)
    {
        t.Label.Text = t.Session.Title;
        t.Label.FontWeight = t == _tab ? FontWeight.SemiBold : FontWeight.Normal;
        t.Label.Foreground = t.Error != null ? ErrorBrush : null;
        t.Header.Background = t == _tab ? TabActiveBrush : TabInactiveBrush;
        ToolTip.SetTip(t.Header, t.Error is { } e ? $"Could not be opened: {e}" : t.Session.FilePath ?? t.Source ?? "Not saved yet");
        RefreshPartsList();
    }

    /// <summary>An unsaved new part nobody has worked on: opening a file reuses its tab instead of adding one.</summary>
    bool IsUntouched(PartTab t) =>
        t.Session.FilePath == null && !t.Session.Dirty && !t.Session.CanUndo && t.Session.Doc.Features.Count == 0
        && !t.Session.IsPending && t.Error == null && !(t == _tab && HasPendingEdit);

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
        // the view is the part's only once it was fitted to it: left before its model arrived (flipping through
        // parts quickly), it is still to be fitted when shown again. Left while the view animates, the view it was
        // going to (and the animation stops: it must not carry on into the next part's view)
        var settled = Viewport.SettleView();
        _tab.View = _tab.FitAfterLoad == null ? settled : null;
        Selection.Clear();
        Viewport.ClearPivot();

        var old = _tab;
        _tab = t;
        Session = t.Session;
        // opened in a batch and not read yet, or its built model dropped to save memory: read / built now
        if (LoadPending(t) is { } loadError) t.LoadError = loadError;
        t.Session.EnsureBuilt();
        TouchRecent(t);
        UpdateTabHeader(old);
        UpdateTabHeader(t);
        DocTabs.Active = t.Header; // shown even when the tabs before it fill the strip

        StatusRebuild.Text = "";
        BusyText.IsVisible = Session.Busy;
        OnDocumentChanged();
        OnRebuilt();
        if (t.View is { } v) { cam.Rotation = v.rotation; cam.Target = v.target; cam.HalfHeight = v.halfHeight; Viewport.Redraw(); }
        else if (Session.Result != null && (t.FitAfterLoad == null || Session.ResultRevision >= t.FitAfterLoad))
        {
            // built meanwhile (in the background): fitted now, at once, and not again at the next rebuild
            t.FitAfterLoad = null;
            Viewport.StandardView("Trimetric", fit: true, animate: false);
        }
        else t.FitAfterLoad = t.Session.RequestedRevision;
        UpdateLoadBanner(); // a part that could not be opened says why, over the view
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
    /// Closes several tabs: those with nothing to save at once (without showing each), then the others one by one,
    /// each shown while its changes are asked about; Cancel stops there (the rest stay open). False if cancelled.
    /// </summary>
    async Task<bool> CloseTabs(IReadOnlyCollection<PartTab> tabs)
    {
        // nothing to ask: removed straight away (the active one goes last, through CloseTab, as it is shown)
        foreach (var t in tabs.Where(t => t != _tab && !t.Session.Dirty).ToList()) RemoveTab(t);
        foreach (var t in tabs.Where(t => _parts.Contains(t) && t != _tab).ToList())
            if (!await CloseTab(t)) return false;
        if (tabs.Contains(_tab)) return await CloseTab(_tab);
        return true;
    }

    /// <summary>
    /// Closes a tab, asking to save its changes first (it is shown while asked). Closing the last one leaves a new
    /// empty part, as the window always shows one. False if cancelled.
    /// </summary>
    async Task<bool> CloseTab(PartTab t)
    {
        // a part not read yet has nothing to ask about (and need not be read to be closed)
        if (t != _tab && t.Session.IsPending) { RemoveTab(t); return true; }
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
        _recent.Remove(t);
        DocTabs.Children.Remove(t.Header);
        UpdatePartsButton();
    }
}

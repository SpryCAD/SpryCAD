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
        /// <summary>Fit the view once the part's next rebuild finishes (just opened or imported).</summary>
        public bool FitAfterLoad;
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
        s.BusyChanged += busy => { if (s == Session) BusyText.IsVisible = busy; };

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
        return t;
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
        t.Header.BringIntoView();

        StatusRebuild.Text = "";
        BusyText.IsVisible = Session.Busy;
        OnDocumentChanged();
        OnRebuilt();
        if (t.View is { } v) { cam.Rotation = v.rotation; cam.Target = v.target; cam.HalfHeight = v.halfHeight; Viewport.Redraw(); }
        else if (Session.Result != null) Viewport.StandardView("Trimetric", fit: true);
        else t.FitAfterLoad = true;
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
    }
}

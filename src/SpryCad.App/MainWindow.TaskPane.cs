using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using SpryCad.App.Session;

using SpryCad.App.UI;

namespace SpryCad.App;

/// <summary>
/// The task pane, as SolidWorks': a strip of tabs at the right edge of the window. A tab slides its pane out over the
/// graphics area; clicking the model (or Esc, or the tab again) tucks it away. Pinned, the pane stays open beside
/// the graphics area instead (its width set by the splitter). The header collapses it, pops it out into a window of
/// its own, or pins it. Panes: Open Parts, Revisions, Bill of Materials (more can be added with <see cref="AddTaskPane"/>).
/// </summary>
public partial class MainWindow
{
    sealed class TaskPane(string id, string title, string icon, Func<Control> content, Action? popOut)
    {
        public readonly string Id = id, Title = title, Icon = icon;
        public readonly Func<Control> Content = content;
        /// <summary>Shows the pane in a window of its own (null: it cannot be popped out).</summary>
        public readonly Action? PopOut = popOut;
        public Button Tab = null!;
    }

    enum TaskMode { Hidden, Overlay, Pinned }

    readonly List<TaskPane> _taskPanes = new();
    TaskPane? _taskCurrent;
    TaskMode _taskMode = TaskMode.Hidden;
    Border _taskPanel = null!;
    ContentControl _taskBody = null!;
    TextBlock _taskTitle = null!;
    Button _taskPin = null!, _taskPopOut = null!;
    TaskPane _partsPane = null!, _revisionsPane = null!;

    static IBrush TaskTabActive => Palette.SelectedBg;
    static readonly BoxShadows TaskShadow = BoxShadows.Parse("-4 0 14 0 #40000000");

    void BuildTaskPane()
    {
        Button HeaderButton(string icon, string tip)
        {
            var b = new Button
            {
                Content = UI.Icons.Make(icon, 14), Padding = new Thickness(4, 2), MinHeight = 22, MinWidth = 22, Classes = { "tb" },
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            return b;
        }
        var collapse = HeaderButton("collapse", "Collapse (Esc)");
        collapse.Click += (_, _) => HideTaskPane();
        _taskPopOut = HeaderButton("popout", "Open in a separate window");
        _taskPopOut.Click += (_, _) => { if (_taskCurrent?.PopOut is { } pop) { HideTaskPane(); pop(); } };
        _taskPin = HeaderButton("unpinned", "");
        _taskPin.Click += (_, _) =>
        {
            UserSettings.TaskPinned = _taskMode != TaskMode.Pinned;
            UserSettings.Save();
            PlaceTaskPane(UserSettings.TaskPinned ? TaskMode.Pinned : TaskMode.Overlay);
        };
        _taskTitle = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = UI.TypeScale.Body, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(collapse, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(_taskPin, Avalonia.Controls.Dock.Right);
        DockPanel.SetDock(_taskPopOut, Avalonia.Controls.Dock.Right);
        header.Children.Add(collapse);
        header.Children.Add(_taskPin);
        header.Children.Add(_taskPopOut);
        header.Children.Add(_taskTitle);
        var headerBar = new Border { Child = header, Background = Palette.Strip, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(headerBar, Avalonia.Controls.Dock.Top);
        _taskBody = new ContentControl();
        _taskPanel = new Border
        {
            Background = Palette.Panel,
            BorderBrush = Palette.Border, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new DockPanel { Children = { headerBar, _taskBody } },
        };

        _partsPane = AddTaskPane("OpenParts", "Open Parts", "parts", () => { var l = PartsListControl(); l.Padding = new Thickness(0); return l; },
            popOut: () => MovePartsList(docked: false));
        _revisionsPane = AddTaskPane("Revisions", "Revisions", "revisions", RevisionsControl);
        _bomPane = AddTaskPane("Bom", "Bill of Materials", "bom", BomControl);

        TaskSplitter.DragCompleted += (_, _) => { UserSettings.TaskWidth = (int)MainGrid.ColumnDefinitions[4].ActualWidth; UserSettings.Save(); };
        // unpinned, the pane tucks away when the model (or anything else outside it) is clicked
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_taskMode != TaskMode.Overlay || e.Source is not Visual v) return;
            if (_taskPanel.IsVisualAncestorOf(v) || ReferenceEquals(_taskPanel, v) || TaskStrip.IsVisualAncestorOf(v)) return;
            HideTaskPane(focusModel: false);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // pinned open when the app last closed: open again
        if (UserSettings.TaskOpen && UserSettings.TaskPinned)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowTaskPane(_partsPane, focus: false));
    }

    /// <summary>Adds a pane and its tab on the strip.</summary>
    TaskPane AddTaskPane(string id, string title, string icon, Func<Control> content, Action? popOut = null)
    {
        var p = new TaskPane(id, title, icon, content, popOut);
        p.Tab = new Button
        {
            Content = UI.Icons.Make(icon, 18), Padding = new Thickness(3), MinHeight = 0, MinWidth = 0, Classes = { "tb" },
        };
        ToolTip.SetTip(p.Tab, title);
        p.Tab.Click += (_, _) =>
        {
            // the open pane's tab tucks it away (unpinned) or collapses it (pinned)
            if (_taskCurrent == p && _taskMode != TaskMode.Hidden) HideTaskPane();
            else ShowTaskPane(p, focus: true);
        };
        _taskPanes.Add(p);
        TaskStrip.Children.Add(p.Tab);
        return p;
    }

    /// <summary>Opens a pane: pinned or sliding out, as the pin was last.</summary>
    void ShowTaskPane(TaskPane p, bool focus)
    {
        _taskCurrent = p;
        var content = p.Content();
        if (content.Parent is ContentControl old && old != _taskBody) old.Content = null;
        _taskBody.Content = content;
        _taskTitle.Text = p.Title;
        _taskPopOut.IsVisible = p.PopOut != null;
        PlaceTaskPane(UserSettings.TaskPinned ? TaskMode.Pinned : TaskMode.Overlay);
        if (focus) Avalonia.Threading.Dispatcher.UIThread.Post(() => (content as InputElement)?.Focus());
        if (p == _partsPane && focus) Avalonia.Threading.Dispatcher.UIThread.Post(FocusPartsList);
    }

    void HideTaskPane(bool focusModel = true)
    {
        PlaceTaskPane(TaskMode.Hidden);
        if (focusModel) Viewport.Focus();
    }

    /// <summary>Puts the pane where the mode says: over the graphics area, in its column, or nowhere.</summary>
    void PlaceTaskPane(TaskMode mode)
    {
        var cols = MainGrid.ColumnDefinitions;
        if (_taskMode == TaskMode.Pinned && mode != TaskMode.Pinned) UserSettings.TaskWidth = (int)cols[4].ActualWidth;
        _taskMode = mode;
        // out of where it was
        TaskOverlay.Children.Remove(_taskPanel);
        if (TaskPinHost.Content == _taskPanel) TaskPinHost.Content = null;
        var width = Math.Clamp(UserSettings.TaskWidth, 180, 900);
        bool pinned = mode == TaskMode.Pinned;
        cols[3].Width = new GridLength(pinned ? 4 : 0);
        cols[4].Width = new GridLength(pinned ? width : 0);
        cols[4].MinWidth = pinned ? 180 : 0;
        TaskSplitter.IsVisible = pinned;
        if (pinned)
        {
            // the whole column (not the list's own width at the right, as when sliding out)
            _taskPanel.Width = double.NaN;
            _taskPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _taskPanel.BoxShadow = default;
            TaskPinHost.Content = _taskPanel;
        }
        else if (mode == TaskMode.Overlay)
        {
            _taskPanel.Width = width;
            _taskPanel.HorizontalAlignment = HorizontalAlignment.Right;
            _taskPanel.BoxShadow = TaskShadow;
            TaskOverlay.Children.Add(_taskPanel);
        }
        _taskPin.Content = UI.Icons.Make(pinned ? "pinned" : "unpinned", 14);
        ToolTip.SetTip(_taskPin, pinned ? "Auto-hide the pane" : "Pin the pane open");
        foreach (var p in _taskPanes)
        {
            var on = mode != TaskMode.Hidden && p == _taskCurrent;
            p.Tab.Background = on ? TaskTabActive : Brushes.Transparent;
            p.Tab.BorderBrush = on ? Palette.HoverBorder : Brushes.Transparent;
        }
        UserSettings.TaskOpen = mode != TaskMode.Hidden;
        UserSettings.Save();
    }

    /// <summary>The task pane shows Open Parts (pinned or sliding out).</summary>
    bool PartsInTaskPane => _taskMode != TaskMode.Hidden && _taskCurrent == _partsPane;
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace FeatherCad.App.UI;

/// <summary>How a card is accented: a problem (red, warning sign), a question (blue), or information (gray-blue).</summary>
public enum CardKind { Error, Question, Info }

/// <summary>
/// FeatherCAD's message card: a white card with rounded corners and a soft shadow, an accented border, an icon and a
/// title, the text, and buttons at the bottom right. Shown over the view for a part that could not be opened, and,
/// as an in-window dialog (<see cref="ShowAsync"/>), for the questions and messages of <see cref="Dialogs"/>.
/// </summary>
public static class Card
{
    public static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FFC62828"));
    public static readonly IBrush QuestionBrush = new SolidColorBrush(Color.Parse("#FF2F7BD6"));
    public static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#FF5A6B85"));

    public static IBrush AccentOf(CardKind kind) => kind switch { CardKind.Error => ErrorBrush, CardKind.Question => QuestionBrush, _ => InfoBrush };

    /// <summary>The card: the icon and title on top, then the body, then the buttons (none: no button row).</summary>
    public static Border Make(CardKind kind, string title, IEnumerable<Control> body, IReadOnlyList<Button>? buttons, out TextBlock titleBlock)
    {
        var accent = AccentOf(kind);
        titleBlock = new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0),
        };
        if (kind == CardKind.Error) titleBlock.Foreground = accent; // a problem's title in red
        var icon = Icons.Make(kind == CardKind.Error ? "warning" : kind == CardKind.Question ? "help" : "propsheet", 22, accent);
        var head = new DockPanel { Children = { icon, titleBlock } };
        DockPanel.SetDock(icon, Dock.Left);
        var stack = new StackPanel { Spacing = 6, Children = { head } };
        foreach (var c in body) stack.Children.Add(c);
        if (buttons is { Count: > 0 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var b in buttons) { b.MinWidth = 80; b.HorizontalContentAlignment = HorizontalAlignment.Center; row.Children.Add(b); }
            stack.Children.Add(row);
        }
        return new Border
        {
            Background = Brushes.White, BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12), MaxWidth = 520, MinWidth = 320, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #30000000"),
            Child = stack,
        };
    }

    /// <summary>Text for a card's body (wrapped).</summary>
    public static TextBlock Text(string text, bool dim = false)
    {
        var t = new TextBlock { Text = text, FontSize = dim ? 11 : 12, TextWrapping = TextWrapping.Wrap };
        if (dim) t.Foreground = Brushes.Gray;
        return t;
    }

    /// <summary>A dialog card is up in some window: the main window's shortcuts wait.</summary>
    public static bool IsOpen => _open > 0;
    static int _open;

    /// <summary>
    /// Shows a card as a dialog inside the window (over a dimmed backdrop that takes the clicks) and waits for one of
    /// its buttons: the index of the button chosen; Enter chooses <paramref name="defaultIndex"/>, Esc
    /// <paramref name="cancelIndex"/>. Null when the window cannot show it (the caller falls back to a dialog window).
    /// </summary>
    public static Task<int>? ShowAsync(Window owner, CardKind kind, string title, IEnumerable<Control> body, IReadOnlyList<string> buttons,
        int defaultIndex = 0, int cancelIndex = -1, Control? focus = null)
    {
        if (!owner.IsVisible || OverlayLayer.GetOverlayLayer(owner) is not { } layer) return null;
        var tcs = new TaskCompletionSource<int>();
        var btns = buttons.Select(text => new Button { Content = text }).ToList();
        if (defaultIndex >= 0 && defaultIndex < btns.Count) btns[defaultIndex].Classes.Add("accent"); // the default answer stands out
        var card = Make(kind, title, body, btns, out _);
        var backdrop = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#26000000")),
            Width = layer.Bounds.Width, Height = layer.Bounds.Height,
            Children = { card },
        };
        void Fit(object? s, AvaloniaPropertyChangedEventArgs e) { if (e.Property == Visual.BoundsProperty) { backdrop.Width = layer.Bounds.Width; backdrop.Height = layer.Bounds.Height; } }
        layer.PropertyChanged += Fit;
        var previousFocus = owner.FocusManager?.GetFocusedElement();
        void Close(int i)
        {
            if (tcs.Task.IsCompleted) return;
            layer.PropertyChanged -= Fit;
            layer.Children.Remove(backdrop);
            _open--;
            (previousFocus as InputElement)?.Focus();
            tcs.SetResult(i);
        }
        for (int i = 0; i < btns.Count; i++) { var k = i; btns[i].Click += (_, _) => Close(k); }
        // keys stay in the card: Enter, Esc, Tab between its buttons; nothing reaches the window's shortcuts
        backdrop.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is not TextBox { AcceptsReturn: true } && e.Source is not Button) { Close(defaultIndex); e.Handled = true; }
            else if (e.Key == Key.Escape && cancelIndex >= 0) { Close(cancelIndex); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        _open++;
        layer.Children.Add(backdrop);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (focus ?? (defaultIndex >= 0 && defaultIndex < btns.Count ? btns[defaultIndex] : null))?.Focus(), Avalonia.Threading.DispatcherPriority.Input);
        return tcs.Task;
    }
}

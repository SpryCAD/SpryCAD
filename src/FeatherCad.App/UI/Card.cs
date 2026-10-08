using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace FeatherCad.App.UI;

/// <summary>How a card is accented: a problem (red, warning sign), a question (blue), or information (gray-blue).</summary>
public enum CardKind { Error, Question, Info }

/// <summary>
/// SpryCAD's message card: a white card with rounded corners and a soft shadow, an accented border, an icon and a
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
            foreach (var b in buttons)
            {
                b.MinWidth = 80; b.HorizontalContentAlignment = HorizontalAlignment.Center;
                b.Classes.Add("card"); // a clear ring when it has the keyboard (Styles.axaml)
                row.Children.Add(b);
            }
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

        // the keyboard is the card's: what can take it, in order (the input box first, then the buttons)
        var stops = new List<Control>();
        if (focus != null) stops.Add(focus);
        stops.AddRange(btns);
        void Move(int step)
        {
            var at = stops.FindIndex(c => c.IsFocused);
            var next = at < 0 ? 0 : ((at + step) % stops.Count + stops.Count) % stops.Count;
            stops[next].Focus(NavigationMethod.Tab); // Tab: the focus outline shows
        }
        // the keys are taken at the window, wherever the focus is (a focus the card did not get must not leave
        // the keys with the control behind it)
        void OnKey(object? _, KeyEventArgs e)
        {
            var inText = e.Source is TextBox tbox && stops.Contains(tbox);
            switch (e.Key)
            {
                case Key.Enter:
                    // the focused button; from the input box (or nowhere), the default one
                    Close(e.Source is Button b && btns.IndexOf(b) is var i and >= 0 ? i : defaultIndex);
                    break;
                case Key.Escape when cancelIndex >= 0: Close(cancelIndex); break;
                // between the card's buttons (not out of the card into the window behind it)
                case Key.Tab: Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); break;
                case Key.Right or Key.Down when !inText: Move(1); break;
                case Key.Left or Key.Up when !inText: Move(-1); break;
                default:
                    // typing goes to the card's input box only; other keys go nowhere while the card is up
                    if (!inText && !(e.Source is Control c && card.IsVisualAncestorOf(c))) break;
                    return;
            }
            e.Handled = true;
        }
        owner.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        tcs.Task.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => owner.RemoveHandler(InputElement.KeyDownEvent, OnKey)));
        _open++;
        layer.Children.Add(backdrop);
        // the keyboard goes to this window (it may be with another one, e.g. the popped-out list of open parts)
        if (!owner.IsActive) owner.Activate();
        // the default answer (or the input box) has the keyboard: when the card is laid out, and again once it is
        // drawn (a focus asked for too early does not take)
        var first = focus ?? (defaultIndex >= 0 && defaultIndex < btns.Count ? btns[defaultIndex] : null);
        void TakeFocus()
        {
            if (first == null || tcs.Task.IsCompleted || stops.Any(s => s.IsFocused)) return;
            first.Focus(NavigationMethod.Tab);
            if (first is TextBox tb) tb.SelectAll();
        }
        card.Loaded += (_, _) => TakeFocus();
        Avalonia.Threading.Dispatcher.UIThread.Post(TakeFocus, Avalonia.Threading.DispatcherPriority.Background);
        return tcs.Task;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace SpryCad.App.UI;

/// <summary>
/// While a name is typed in a value field ("W" of "L/2 + W"), the equations' names starting so drop below it, each with
/// its value: Up and Down pick one, Enter or Tab (or a click) puts it in, Esc closes the list. The window leaves those
/// keys to the field while the list is open (<see cref="IsOpen"/>).
/// </summary>
public static class NameSuggest
{
    /// <summary>A list is open under a field (Enter, Tab, Esc, Up and Down are its).</summary>
    public static bool IsOpen => _open is { IsOpen: true };
    static Popup? _open;

    /// <summary>Suggests <paramref name="names"/> (name, value shown) in <paramref name="tb"/> as names are typed.</summary>
    public static void Attach(TextBox tb, Func<IEnumerable<(string name, string shown)>>? names, Panel? host = null)
    {
        if (names == null) return;
        var list = new ListBox { Classes = { "suggest" }, MaxHeight = 180, MinWidth = 160 };
        var popup = new Popup
        {
            PlacementTarget = tb, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 1,
            Child = new Border { Classes = { "cmdgrid" }, Padding = new Thickness(2), Child = list },
        };
        // the popup in the field's logical tree (its styles and theme), taking no room: in the nearest panel up from the
        // field that lays out by position (a grid, a canvas, a plain panel; not a dock panel, where it would take the
        // field's place, nor a stack panel, which would space it)
        void Host()
        {
            if (popup.Parent != null) return;
            if (host != null) { host.Children.Add(popup); return; }
            for (var p = tb.Parent; p != null; p = p.Parent)
                if (p is Grid or Canvas || p.GetType() == typeof(Panel)) { ((Panel)p).Children.Add(popup); break; }
        }
        tb.AttachedToVisualTree += (_, _) => Host();
        if (tb.IsAttachedToVisualTree()) Host();
        // (the field gone, its list with it: a grid makes a field for each cell edited)
        tb.DetachedFromVisualTree += (_, _) => { popup.IsOpen = false; if (_open == popup) _open = null; (popup.Parent as Panel)?.Children.Remove(popup); };
        List<string> shown = new();
        (int start, int length) token = default;

        void Close() { popup.IsOpen = false; if (_open == popup) _open = null; }
        void Accept()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) { Close(); return; }
            var name = shown[list.SelectedIndex];
            var t = tb.Text ?? "";
            tb.Text = t[..token.start] + name + t[(token.start + token.length)..];
            tb.CaretIndex = token.start + name.Length;
            Close();
        }
        void Update()
        {
            var t = tb.Text ?? "";
            var caret = Math.Clamp(tb.CaretIndex, 0, t.Length);
            int s = caret;
            while (s > 0 && (char.IsLetterOrDigit(t[s - 1]) || t[s - 1] == '_')) s--;
            // a name starts with a letter, and is not a number's unit ("10mm")
            if (s == caret || !(char.IsLetter(t[s]) || t[s] == '_') || (s > 0 && (char.IsDigit(t[s - 1]) || t[s - 1] == '.'))) { Close(); return; }
            var typed = t[s..caret];
            var all = names().ToList();
            var hits = all.Where(n => n.name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && !n.name.Equals(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.name, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (hits.Count == 0) { Close(); return; }
            token = (s, caret - s);
            shown = hits.Select(h => h.name).ToList();
            list.ItemsSource = hits.Select(h => $"{h.name}   {h.shown}").ToList();
            list.SelectedIndex = 0;
            if (!popup.IsOpen) { popup.IsOpen = true; _open = popup; }
        }
        tb.TextChanged += (_, _) => { if (tb.IsKeyboardFocusWithin) Update(); };
        tb.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!tb.IsKeyboardFocusWithin) Close(); });
        // (before the field and the window see them: the list's keys while it is open)
        tb.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!popup.IsOpen) return;
            switch (e.Key)
            {
                case Key.Down: list.SelectedIndex = Math.Min(list.SelectedIndex + 1, shown.Count - 1); e.Handled = true; break;
                case Key.Up: list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0); e.Handled = true; break;
                case Key.Enter or Key.Tab: Accept(); e.Handled = true; break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        }, RoutingStrategies.Tunnel);
        // a name clicked: put in (pressed, before the field loses its focus to the list)
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item)
            {
                list.SelectedIndex = list.IndexFromContainer(item);
                Accept();
                tb.Focus();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    static T? FindAncestorOfType<T>(this Visual v, bool includeSelf) where T : class
    {
        for (Visual? x = includeSelf ? v : Avalonia.VisualTree.VisualExtensions.GetVisualParent(v); x != null; x = Avalonia.VisualTree.VisualExtensions.GetVisualParent(x))
            if (x is T t) return t;
        return null;
    }
}

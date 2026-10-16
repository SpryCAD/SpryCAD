using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Model;

namespace SpryCad.App;

// the quick configurator: the part's user-facing parameters (its user-facing design tables and variables, as Configure
// shows them) in a small panel beside the tree's Configuration row, each change applied as it is made (one undo step)
public partial class MainWindow
{
    Flyout? _configurator;

    /// <summary>Opens the quick configurator beside <paramref name="at"/> (the tree's Configuration row).</summary>
    void ShowConfigurator(Control? at)
    {
        if (Asm != null) return;
        var ps = Session.Doc.UserParameters();
        if (ps.Count == 0) return;
        _configurator?.Hide();

        // a value given: the part's own (its default, as assemblies start from it), the material its rows say with it
        void Set(string name, string value)
        {
            var doc = Session.Doc;
            if (doc.Check(name, value) is { } problem) { Status(problem); return; }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value };
            if (doc.Normalize(values) == null) return; // (the value it has)
            Session.Change(() => { doc.Apply(values); TableConfig.ApplyMaterial(doc); });
            Status($"{name}: {value}.");
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,10,*"), RowSpacing = 6 };
        int row = 0;
        foreach (var p in ps)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = p.Description ?? p.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(label, p.Name);
            Control field;
            var name = p.Name;
            if (p.IsChoice)
            {
                var box = new ComboBox { ItemsSource = p.Choices, SelectedItem = p.Default, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Stretch };
                box.SelectionChanged += (_, _) => { if (box.SelectedItem is string v) Set(name, v); };
                field = box;
            }
            else if (p.IsToggle)
            {
                var on = Expr.TryEval(p.Default, null, out var b, out _) && Expr.Truthy(b);
                var check = new CheckBox { IsChecked = on, VerticalAlignment = VerticalAlignment.Center };
                check.IsCheckedChanged += (_, _) => Set(name, check.IsChecked == true ? "1" : "0");
                field = check;
            }
            else
            {
                var tb = new TextBox { Text = p.Default, MinWidth = 130 };
                ToolTip.SetTip(tb, (p.Min, p.Max) switch
                {
                    ({ } lo, { } hi) => $"From {Expr.Format(lo)} to {Expr.Format(hi)}. Enter or leave the field to apply.",
                    ({ } lo, null) => $"At least {Expr.Format(lo)}. Enter or leave the field to apply.",
                    (null, { } hi) => $"At most {Expr.Format(hi)}. Enter or leave the field to apply.",
                    _ => "Enter or leave the field to apply.",
                });
                void Commit() { if (!string.IsNullOrWhiteSpace(tb.Text)) Set(name, tb.Text.Trim()); }
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
                tb.LostFocus += (_, _) => Commit();
                field = tb;
            }
            Grid.SetRow(label, row); Grid.SetRow(field, row); Grid.SetColumn(field, 2);
            grid.Children.Add(label); grid.Children.Add(field);
            row++;
        }
        var parameters = new Button { Content = "Parameters…", HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(parameters, "Equations and design tables, every variable");
        parameters.Click += (_, _) => { _configurator?.Hide(); _ = ParametersDialog.Show(this, tables: true); };
        var panel = new StackPanel
        {
            Spacing = 8, MinWidth = 260,
            Children =
            {
                new TextBlock { Text = "Configuration", FontWeight = FontWeight.SemiBold, Foreground = Palette.Heading },
                grid,
                parameters,
            },
        };
        _configurator = new Flyout { Content = panel, Placement = PlacementMode.RightEdgeAlignedTop, ShowMode = FlyoutShowMode.Standard };
        _configurator.ShowAt(at ?? Tree);
    }
}

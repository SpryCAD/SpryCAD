using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SpryCad.App.UI;
using SpryCad.Core.Assembly;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// Motor (SolidWorks' rotary motor): a round face of a floating component picked for the axis, a speed and a
/// direction; Play turns the component about that axis and the mates carry everything with it (a gear drives its
/// mate, a crank its linkage); Pause stops it where it is, Reset puts everything back where it started. OK keeps the
/// assembly where the motor left it (an undo point), Cancel puts it back. The motor is not saved with the assembly.
/// </summary>
public partial class MainWindow
{
    /// <summary>The Motor page's state while it is open.</summary>
    sealed class Motoring
    {
        /// <summary>The round face it turns about (its component turns).</summary>
        public MateRef? Axis;
        public double Rpm = 30;
        public bool Reverse;
        public DispatcherTimer? Timer;
        public DateTime Last;
        /// <summary>Where everything was when the page opened (Reset, Cancel), and the turns the mates had counted.</summary>
        public Dictionary<string, Placement> Start = new();
        public Dictionary<string, (double a, double b)> Turns = new();
        /// <summary>How far it has turned (degrees, for the readout).</summary>
        public double Turned;
        public Action? Update;
    }

    Motoring? _motor;

    /// <summary>The faces the open page has picked (Mate's two, or the Motor's axis): drawn highlighted.</summary>
    List<MateRef>? PagePicks => _mating?.Picks ?? (_motor?.Axis is { } ax ? new List<MateRef> { ax } : null);

    void MotorCommand()
    {
        if (Asm is not { } a) return;
        if (_mating != null) { Status("Finish the open Mate page first (OK or Cancel)."); return; }
        if (_motor != null) return;
        if (a.Model.Doc.Mates.Count == 0) { Status("Add mates first: the motor turns a component and its mates carry the rest."); return; }
        a.BeginChange();
        var st = new Motoring
        {
            Start = a.Model.Doc.Components.ToDictionary(c => c.Id, c => c.Placement),
            Turns = a.Model.Doc.Mates.ToDictionary(m => m.Id, m => (m.AngleA, m.AngleB)),
        };
        _motor = st;
        void Restore()
        {
            foreach (var c in a.Model.Doc.Components) if (st.Start.TryGetValue(c.Id, out var p)) c.Placement = p;
            // the gears' and screws' counted turns as they were (else they would count from where the motor left them)
            foreach (var m in a.Model.Doc.Mates) if (st.Turns.TryGetValue(m.Id, out var t)) (m.AngleA, m.AngleB) = t;
            st.Turned = 0;
        }

        var page = new PmPage("Motor", "motor");
        var comp = page.Section("Component/Direction");
        var axisLbl = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = TypeScale.Body };
        comp.Children.Add(new Border { Classes = { "pmsection" }, Child = axisLbl, Padding = new Thickness(6, 4) });
        page.Info(comp, "Click a round face (a bore, a shaft) of a floating component: it turns about that face's axis.");
        page.Check(comp, "Reverse direction", () => st.Reverse, v => st.Reverse = v);

        var motion = page.Section("Motion");
        page.Number(motion, "Speed", () => st.Rpm.ToString(CultureInfo.InvariantCulture),
            v => { if (Core.Expr.TryEval(v, _ => null, out var x, out _)) st.Rpm = x; }, null, "RPM");
        var turnedLbl = new TextBlock { FontSize = TypeScale.Small, Foreground = Palette.TextMuted };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
        Button Btn(string icon, string text, string tip, Action click)
        {
            var b = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Icons.Make(icon, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } },
                Padding = new Thickness(8, 3),
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        var play = Btn("play", "Play", "Turn the component at the speed set; the mates carry the rest", () => Run(st.Timer?.IsEnabled != true));
        Btn("reset", "Reset", "Stop, and put everything back where it was when the page opened", () =>
        {
            Run(false);
            Restore();
            RefreshAssemblyScene();
            st.Update?.Invoke();
        });
        motion.Children.Add(buttons);
        motion.Children.Add(turnedLbl);

        void Run(bool on)
        {
            if (on && st.Axis == null) { Status("Pick a round face to turn about first."); return; }
            if (on)
            {
                st.Timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Tick());
                st.Last = DateTime.UtcNow;
                st.Timer.Start();
            }
            else st.Timer?.Stop();
            st.Update?.Invoke();
        }

        // a step: the component turned about its axis by what the speed gives for the time gone by, held there while
        // the mates move everything else
        void Tick()
        {
            if (_motor != st || st.Axis is not { Component: { } id } ax || FindComponent(id) is not { } c) { Run(false); return; }
            var now = DateTime.UtcNow;
            var dt = Math.Min(0.1, (now - st.Last).TotalSeconds);
            st.Last = now;
            // (a step under a third of a turn: the gear and screw mates count turns from one solve to the next)
            var step = Math.Clamp(st.Rpm * 2 * Math.PI / 60 * dt, -2.0, 2.0) * (st.Reverse ? -1 : 1);
            if (step == 0) return;
            var g = a.Model.World(ax);
            c.Placement = Placement.From(Transform.Rotation(g.P, g.D, step) * c.Placement.ToTransform());
            var others = a.Model.Doc.Components.Where(x => !x.Fixed && x.Id != id).Select(x => x.Id).ToHashSet();
            var res = a.Model.SolveMates(others);
            st.Turned += step * 180 / Math.PI;
            if (!res.Satisfied)
            {
                // the mates do not let it turn (it is held some other way, or the mechanism locks up): back a step
                c.Placement = Placement.From(Transform.Rotation(g.P, g.D, -step) * c.Placement.ToTransform());
                a.Model.SolveMates(others);
                st.Turned -= step * 180 / Math.PI;
                Run(false);
                var failed = res.Failed().Select(m => a.Model.Doc.Mates.FirstOrDefault(x => x.Id == m)?.Name).Where(n => n != null);
                Status($"The motor stopped: the mates do not let {c.Name} turn further ({string.Join(", ", failed)}).");
            }
            RefreshAssemblyScene();
            turnedLbl.Text = $"Turned {st.Turned:0.#}°";
        }

        st.Update = () =>
        {
            if (st.Axis is { } ax)
            {
                var c = FindComponent(ax.Component);
                axisLbl.Text = $"{c?.Name}: turning about the axis of the face picked";
            }
            else axisLbl.Text = "(no face picked)";
            var running = st.Timer?.IsEnabled == true;
            ((StackPanel)play.Content!).Children[0] = Icons.Make(running ? "pause" : "play", 16);
            ((TextBlock)((StackPanel)play.Content!).Children[1]).Text = running ? "Pause" : "Play";
            turnedLbl.Text = $"Turned {st.Turned:0.#}°";
            RefreshAssemblyScene();
        };

        void Close()
        {
            st.Timer?.Stop();
            _motor = null; _hoverFace = null;
            ShowPage(null); HideConfirmCorner();
            RefreshAssemblyScene();
            RebuildTree();
        }
        void Ok()
        {
            var moved = a.Model.Doc.Components.Any(c => st.Start.TryGetValue(c.Id, out var p) && !ReferenceEquals(p, c.Placement));
            Close();
            // where the motor left it: an undo point (nothing moved: none)
            if (moved) { a.EndChange(); Status("Kept where the motor left it."); }
            else a.AbandonChange();
        }
        void Cancel()
        {
            st.Timer?.Stop();
            Restore();
            Close();
            a.AbandonChange();
        }
        page.OnOk = Ok;
        page.OnCancel = Cancel;
        ShowPage(page);
        ShowConfirmCorner(Ok, Cancel, "OK: keep the assembly where the motor left it (Enter)", "Cancel: put it back (Esc)");
        st.Update();
        Status("Motor: click a round face of the component to turn, then Play.");
    }

    /// <summary>A face clicked while the Motor page is open: the axis to turn about, if it is round and its component floats.</summary>
    void MotorPick(string component, string face)
    {
        if (_motor is not { } st || Asm is not { } a) return;
        var c = FindComponent(component);
        if (a.Model.RefTo(component, face) is not { Kind: MateGeomKind.Axis } r) { Status($"{c?.Name}: pick a round face (a bore or a shaft): the motor turns about its axis."); return; }
        if (c is { Fixed: true }) { Status($"{c.Name} is fixed: pick a face of a floating component."); return; }
        st.Timer?.Stop();
        st.Axis = r;
        st.Update?.Invoke();
        Status($"Motor: {c?.Name} turns about that axis. Play to run it.");
    }
}

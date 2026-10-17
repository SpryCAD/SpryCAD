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
        public PoseState? Start;
        /// <summary>How far it has turned (degrees, for the readout).</summary>
        public double Turned;
        public Action? Update;
    }

    Motoring? _motor;

    /// <summary>The faces the open page has picked (Mate's two, or the Motor's axis): drawn highlighted.</summary>
    List<MateRef>? PagePicks => _mating?.Picks ?? (_motor?.Axis is { } ax ? new List<MateRef> { ax } : _fastening?.Hole is { } hole ? new List<MateRef> { hole } : null);

    void MotorCommand()
    {
        if (Asm is not { } a) return;
        if (_mating != null) { Status("Close the open Mate PropertyManager first."); return; }
        if (_motor != null) return;
        if (!a.Model.HasMates) { Status("Add mates before using a motor."); return; }
        a.BeginChange();
        // (where everything is, flexible sub-assemblies' parts too, and the turns the gears and screws have counted)
        var st = new Motoring { Start = a.Model.CapturePoses() };
        _motor = st;
        void Restore()
        {
            a.Model.Restore(st.Start!);
            st.Turned = 0;
        }

        var page = new PmPage("Motor", "motor");
        var comp = page.Section("Component/Direction");
        var axisLbl = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = TypeScale.Body };
        comp.Children.Add(new Border { Classes = { "pmsection" }, Child = axisLbl, Padding = new Thickness(6, 4) });
        page.Info(comp, "Select a cylindrical or conical face of a floating component. The component rotates about the face's axis.");
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
        var play = Btn("play", "Play", "Rotates the component at the set speed", () => Run(st.Timer?.IsEnabled != true));
        Btn("reset", "Reset", "Stops the motor and returns components to their starting positions", () =>
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
            if (on && st.Axis == null) { Status("Select a cylindrical or conical face first."); return; }
            if (on)
            {
                st.Timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => Tick());
                st.Last = DateTime.UtcNow;
                st.Timer.Start();
            }
            else { st.Timer?.Stop(); a.Model.EndMotion(); }
            st.Update?.Invoke();
        }

        // a step: the component turned about its axis by what the speed gives for the time gone by, held there while
        // the mates move everything else
        void Tick()
        {
            if (_motor != st || st.Axis is not { Component: { } id } ax || a.Model.Movable(id) is not { } body) { Run(false); return; }
            var now = DateTime.UtcNow;
            var dt = Math.Min(0.1, (now - st.Last).TotalSeconds);
            st.Last = now;
            // (a step under a third of a turn: the gear and screw mates count turns from one solve to the next)
            var step = Math.Clamp(st.Rpm * 2 * Math.PI / 60 * dt, -2.0, 2.0) * (st.Reverse ? -1 : 1);
            if (step == 0) return;
            var g = a.Model.World(ax);
            // (what turns: the component, or the rigid sub-assembly it is part of; what moves with it solved on a solve
            // set up once while it runs)
            if (a.Model.MotionHeld != body) { a.Model.EndMotion(); a.Model.BeginMotion(body); }
            a.Model.SetWorld(body, Transform.Rotation(g.P, g.D, step) * a.Model.WorldOf(body));
            var res = a.Model.SolveMotion();
            st.Turned += step * 180 / Math.PI;
            if (!res.Satisfied)
            {
                // the mates do not let it turn (it is held some other way, or the mechanism locks up): back a step
                a.Model.SetWorld(body, Transform.Rotation(g.P, g.D, -step) * a.Model.WorldOf(body));
                a.Model.SolveMotion();
                st.Turned -= step * 180 / Math.PI;
                Run(false);
                Status($"Motor stopped. {a.Model.NameAt(body)} is blocked by {string.Join(", ", res.Failed().Select(m => MateName(a.Model, m)))}.");
            }
            RefreshAssemblyScene();
            turnedLbl.Text = $"Turned {st.Turned:0.#}°";
        }

        st.Update = () =>
        {
            if (st.Axis is { } ax)
            {
                axisLbl.Text = $"{(ax.Component != null ? a.Model.NameAt(ax.Component) : "")}: face axis";
            }
            else axisLbl.Text = "No face selected";
            var running = st.Timer?.IsEnabled == true;
            ((StackPanel)play.Content!).Children[0] = Icons.Make(running ? "pause" : "play", 16);
            ((TextBlock)((StackPanel)play.Content!).Children[1]).Text = running ? "Pause" : "Play";
            turnedLbl.Text = $"Turned {st.Turned:0.#}°";
            RefreshAssemblyScene();
        };

        void Close()
        {
            st.Timer?.Stop();
            a.Model.EndMotion();
            _motor = null; _hoverFace = null;
            ShowPage(null); HideConfirmCorner();
            RefreshAssemblyScene();
            RebuildTree();
        }
        void Ok()
        {
            var moved = st.Start!.Moved(a.Model);
            Close();
            // where the motor left it: an undo point (nothing moved: none)
            if (moved) { a.EndChange(); Status("Motor positions kept."); }
            else a.AbandonChange();
        }
        void Cancel()
        {
            st.Timer?.Stop();
            a.Model.EndMotion();
            Restore();
            Close();
            a.AbandonChange();
        }
        page.OnOk = Ok;
        page.OnCancel = Cancel;
        ShowPage(page);
        ShowConfirmCorner(Ok, Cancel, "OK (Enter)", "Cancel (Esc)");
        st.Update();
        Status("Select a cylindrical or conical face of the component to rotate, then click Play.");
    }

    /// <summary>A mate by its id (the assembly's, or a flexible sub-assembly's by its path: "C3/M1") as named in the tree.</summary>
    static string MateName(AssemblyModel m, string id)
    {
        if (m.Doc.Mates.FirstOrDefault(x => x.Id == id) is { } own) return own.Name;
        var i = id.LastIndexOf('/');
        if (i > 0 && m.Find(id[..i]) is { } o && o.Owner.SubOf(o.Component)?.Model?.Doc.Mates.FirstOrDefault(x => x.Id == id[(i + 1)..]) is { } sub)
            return $"{sub.Name} in {m.NameAt(id[..i])}";
        return id;
    }

    /// <summary>A face clicked while the Motor page is open: the axis to turn about, if it is round and its component floats.</summary>
    void MotorPick(string component, string face)
    {
        if (_motor is not { } st || Asm is not { } a) return;
        var name = a.Model.NameAt(component);
        if (a.Model.RefTo(component, face) is not { Kind: MateGeomKind.Axis } r) { Status($"{name}: select a cylindrical or conical face for the motor axis."); return; }
        if (a.Model.Movable(component) is not { } body) { Status($"{name} is fixed. Select a face of a floating component."); return; }
        st.Timer?.Stop();
        a.Model.EndMotion();
        st.Axis = r;
        st.Update?.Invoke();
        Status($"{a.Model.NameAt(body)} rotates about the selected axis. Click Play to run the motor.");
    }
}

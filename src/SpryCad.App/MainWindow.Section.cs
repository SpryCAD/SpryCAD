using SpryCad.App.Session;
using SpryCad.App.UI;
using SpryCad.Core;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.App;

/// <summary>
/// Section View, as SolidWorks': the model (a part's bodies, or an assembly's components) cut along a plane, what is
/// on one side taken away and the cut filled, so the inside shows. The plane: one of the origin planes, a reference
/// plane or a flat face (a part), moved along by an offset, the side kept flipped if wanted. A view setting: nothing in
/// the model changes. The heads-up bar's section button (and View > Section View) opens the page, and turns it off.
/// </summary>
public partial class MainWindow
{
    bool _sectionOn;
    SelItem? _sectionRef;          // a part's plane or face (null: the origin plane below)
    string _sectionStd = "Front";  // an origin plane (an assembly's, or a part's when nothing is picked)
    string _sectionOffset = "0";
    bool _sectionFlip;
    PmPage? _sectionPage;

    /// <summary>The section plane as set: the chosen plane moved by the offset along its normal, the side its normal points to cut away (flipped: the other).</summary>
    Plane SectionPlane()
    {
        var f = Asm == null && _sectionRef != null && TryPlaneOf(_sectionRef, out _, out var fr) ? fr : Resolver.StdPlane(_sectionStd);
        var off = Expr.TryEval(_sectionOffset, Session.Lookup, out var v, out string? _) ? v : 0;
        var n = f.Normal.Normalized;
        return Plane.FromPointNormal(f.Origin + n * off, _sectionFlip ? -n : n);
    }

    void ApplySection()
    {
        Viewport.Section = _sectionOn ? SectionPlane() : null;
        Viewport.Redraw();
    }

    /// <summary>The section button: on, the page to choose the plane (live); already on, off again.</summary>
    void SectionCommand()
    {
        if (_sectionPage != null && _page == _sectionPage) return;
        if (_sectionOn) { _sectionOn = false; ApplySection(); Status("Section view off."); return; }
        if (_sketch != null || _edit != null) { Status("Finish the current edit before sectioning."); return; }
        var before = (_sectionRef, _sectionStd, _sectionOffset, _sectionFlip);
        var page = new PmPage("Section View", "section");
        var s = page.Section("Section Plane");
        if (Asm == null)
        {
            var box = page.Selection(s, "Plane", SelFilter.Plane | SelFilter.PlanarFace, "A plane or a flat face to cut along (the Front Plane when none is picked).", 1);
            box.Items.Add(_sectionRef ?? new SelItem { Kind = SelKind.Plane, Name = _sectionStd, Frame = Resolver.StdPlane(_sectionStd) });
            box.Changed += () => { _sectionRef = box.Items.FirstOrDefault(); if (_sectionRef == null) _sectionStd = "Front"; ApplySection(); };
        }
        else
            page.Combo(s, "Plane", new List<(string, string)> { ("Front", "Front Plane"), ("Top", "Top Plane"), ("Right", "Right Plane") },
                () => _sectionStd, v => { _sectionStd = v; ApplySection(); }, "plane");
        page.Number(s, "Offset", () => _sectionOffset, v => { _sectionOffset = v; ApplySection(); }, "dimension");
        page.Check(s, "Flip the side cut away", () => _sectionFlip, v => { _sectionFlip = v; ApplySection(); });
        page.Info(s, "A view only: the model is not changed. Click the section button again to turn it off.");
        page.OnOk = () => { _sectionPage = null; ShowPage(null); Status("Section view on (the section button turns it off)."); };
        page.OnCancel = () =>
        {
            (_sectionRef, _sectionStd, _sectionOffset, _sectionFlip) = before;
            _sectionOn = false; _sectionPage = null;
            ApplySection();
            ShowPage(null);
        };
        _sectionPage = page;
        _sectionOn = true;
        ShowPage(page);
        ApplySection();
    }

    /// <summary>Another document shown: its own model, so the section goes.</summary>
    void EndSection()
    {
        if (!_sectionOn && _sectionPage == null) return;
        _sectionOn = false; _sectionRef = null; _sectionPage = null;
        Viewport.Section = null;
    }
}

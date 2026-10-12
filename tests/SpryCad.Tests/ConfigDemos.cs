using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>Demos of configurable parts: user-facing parameters, design tables, features suppressed by conditions.</summary>
static partial class Demos
{
    /// <summary>
    /// The spur gear, configurable: module, tooth count, face width and bore user-facing (the rest internal), the web
    /// recesses suppressed when the web between hub and rim is too narrow for them (small gears are solid), the
    /// lightening holes when they would be smaller than two modules.
    /// </summary>
    internal static CadDocument ConfigGear()
    {
        var d = Gear();
        void User(string name, string description, double min, double max)
        {
            var e = d.Equations.First(q => q.Name == name);
            e.Exposed = true; e.Description = description; e.Min = min; e.Max = max;
        }
        User("m", "Module (mm)", 0.5, 10);
        User("z", "Number of teeth", 12, 200);
        User("b", "Face width (mm)", 2, 100);
        User("Bore", "Bore diameter (mm)", 3, 100);
        foreach (var f in d.Features)
            f.SuppressWhen = f.Name switch
            {
                "Web recess" or "Web recess (back)" => "RimD - HubD < 4*m",
                "Lightening hole" => "LH < 2*m",
                _ => null,
            };
        return d;
    }

    /// <summary>
    /// An ISO 4762 socket head cap screw, M3 to M12 in one file: a design table of the sizes (thread D and pitch P,
    /// head diameter dk and height k, socket across flats s and depth t), the length L under the head and whether
    /// the thread is shown user-facing (cosmetic: drawn as a helix, modeled in exports). The head on the Front plane, the shank on the plane at its underside.
    /// </summary>
    internal static CadDocument SocketScrew()
    {
        var d = Doc(("L", "20"), ("Threaded", "1"));
        d.Equations[0].Exposed = true; d.Equations[0].Description = "Length under the head (mm)"; d.Equations[0].Min = 4; d.Equations[0].Max = 200;
        d.Equations[1].Exposed = true; d.Equations[1].Description = "Thread shown"; d.Equations[1].Min = 0; d.Equations[1].Max = 1;
        d.Tables = new()
        {
            new DesignTable
            {
                Name = "Size", Description = "Thread size", Columns = { "D", "P", "dk", "k", "s", "t" }, Selected = "M6",
                Rows =
                {
                    new DesignRow { Key = "M3", Values = { "3", "0.5", "5.5", "3", "2.5", "1.3" } },
                    new DesignRow { Key = "M4", Values = { "4", "0.7", "7", "4", "3", "2" } },
                    new DesignRow { Key = "M5", Values = { "5", "0.8", "8.5", "5", "4", "2.5" } },
                    new DesignRow { Key = "M6", Values = { "6", "1", "10", "6", "5", "3" } },
                    new DesignRow { Key = "M8", Values = { "8", "1.25", "13", "8", "6", "4" } },
                    new DesignRow { Key = "M10", Values = { "10", "1.5", "16", "10", "8", "5" } },
                    new DesignRow { Key = "M12", Values = { "12", "1.75", "18", "12", "10", "6" } },
                },
            },
        };
        // head, and the hexagon socket in its top (the Front plane side)
        var h = new Sk(d);
        h.Circle(Sketch.OriginId, "dk");
        var hs = d.AddFeature(new SketchFeature { Name = "Head", Plane = PlaneRef.Front, Sketch = h.S });
        d.AddFeature(new ExtrudeFeature { Name = "Head", SketchId = hs.Id, Depth = "k" });
        var so = new Sk(d);
        so.Poly(new[]
        {
            so.P("s/2", "s/(2*sqrt(3))"), so.P("0", "s/sqrt(3)"), so.P("-s/2", "s/(2*sqrt(3))"),
            so.P("-s/2", "-s/(2*sqrt(3))"), so.P("0", "-s/sqrt(3)"), so.P("s/2", "-s/(2*sqrt(3))"),
        });
        var ss = d.AddFeature(new SketchFeature { Name = "Socket", Plane = PlaneRef.Front, Sketch = so.S });
        d.AddFeature(new ExtrudeFeature { Name = "Socket", SketchId = ss.Id, IsCut = true, Depth = "t" });
        // shank under the head, threaded up to the head
        var under = d.AddFeature(new PlaneFeature { Name = "Under head", Base = PlaneRef.Front, Offset = "k" });
        var sh = new Sk(d);
        var shc = sh.Circle(Sketch.OriginId, "D");
        var shs = d.AddFeature(new SketchFeature { Name = "Shank", Plane = new PlaneRef { Feature = under.Id }, Sketch = sh.S });
        var shank = d.AddFeature(new ExtrudeFeature { Name = "Shank", SketchId = shs.Id, Depth = "L" });
        d.AddFeature(new ThreadFeature
        {
            Name = "Thread", End = ThreadEnd.UpToNext, Cosmetic = true, SuppressWhen = "not Threaded",
            Face = new FaceRef { Name = $"{shank.Id}.S.C{Ids.Name(shc)}", Hint = new Vec3(sh.Eval("D/2"), 0, sh.Eval("k+L-1")) },
        });
        d.Color = new[] { 0.30, 0.31, 0.34 };
        return d;
    }

    /// <summary>The socket screw's sizes side by side: one part file (embedded once), a component per size and length.</summary>
    internal static AssemblyModel ScrewSizes()
    {
        var screw = SocketScrew();
        return Embedded(new[] { ("Socket screw", screw) }, (m, paths) =>
        {
            var part = CadDocument.Load(paths["Socket screw"]);
            double x = 0;
            foreach (var (size, length) in new[] { ("M3", "10"), ("M4", "16"), ("M5", "20"), ("M6", "25"), ("M8", "30"), ("M10", "40"), ("M12", "50") })
            {
                var c = m.Insert(paths["Socket screw"], Transform.Translation(new Vec3(x, 0, 0)), embed: true);
                m.Configure(new[] { c }, part, new Dictionary<string, string> { ["Size"] = size, ["L"] = length });
                x += 25;
            }
        });
    }
}

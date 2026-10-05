using FeatherCad.Core.Model;
using FeatherCad.Core.Regen;
using FeatherCad.Kernel;

namespace FeatherCad.Tests;

/// <summary><c>-- validate file.fcad out.step</c>: rebuild, report watertightness/volumes, write the exact STEP.</summary>
static class Validate
{
    public static int Run(string file, string step)
    {
        var doc = CadDocument.FromJson(File.ReadAllText(file));
        var r = new Regenerator().Rebuild(doc);
        foreach (var f in doc.Features) if (!r.Status[f.Id].Ok) Console.WriteLine($"  {f.Name}: {r.Status[f.Id].Error}");
        foreach (var b in r.State.Bodies)
            Console.WriteLine($"  body {b.Id}: native={b.Solid.Native != null} open={b.Solid.Topology.OpenHalfEdges} meshVolume={b.Mesh.Volume:0.###} exact={KernelTests.Vex(b.Solid):0.###}");
        foreach (var b in r.State.Bodies)
        {
            try
            {
                var brep = ExactBrep.Build(b.Solid);
                Console.WriteLine($"  exact B-rep: {brep.Faces.Count} faces, warnings: {string.Join("; ", brep.Warnings)}");
            }
            catch (Exception ex) { Console.WriteLine("  exact B-rep failed: " + ex); }
        }
        using var fs = File.Create(step);
        StepWriter.Write(fs, r.State.Bodies.Select(b => (b.Id, b.Solid)), Path.GetFileNameWithoutExtension(file));
        if (StepWriter.LastError != null) Console.WriteLine("  STEP exact export failed: " + StepWriter.LastError);
        return 0;
    }
}

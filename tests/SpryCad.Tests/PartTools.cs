using System.Globalization;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Looking into a part's features (blends especially), on a part file or a blend case (by part of its name):
/// <list type="bullet">
/// <item><c>-- zoom part feature|- x,y,z r eye-x,eye-y,eye-z up(x|y|z) out.png [features;…]</c>: a close-up within r
/// of a point, seen from a direction, after the feature (- the whole part), faces in their feature's color (Color by
/// Feature's; only the named features' when given, the rest grey); the faces near it listed.</item>
/// <item><c>-- blenddbg part feature [face names…]</c>: the feature rebuilt with the blend diagnostics on, and the
/// named faces' surfaces and loops.</item>
/// <item><c>-- feattime part reps [features;…]</c>: each feature's best rebuild time over reps cold rebuilds.</item>
/// </list>
/// </summary>
public static class PartTools
{
    static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    static Vec3 V(string s) { var p = s.Split(',').Select(D).ToArray(); return new Vec3(p[0], p[1], p[2]); }

    static CadDocument Part(string s) => File.Exists(s) ? CadDocument.Load(s)
        : Demos.BlendCases.First(c => c.name.Contains(s, StringComparison.OrdinalIgnoreCase)).build();

    /// <summary>The number of features up to and including the named one (all for "-").</summary>
    static int UpTo(CadDocument doc, string feature) => feature == "-" ? doc.Features.Count : doc.Features.FindLastIndex(f => f.Name == feature) + 1;

    public static int Zoom(string[] a)
    {
        if (a.Length < 7) { Console.WriteLine("zoom part feature|- x,y,z r eye-x,eye-y,eye-z up(x|y|z) out.png [features;…]"); return 1; }
        var doc = Part(a[0]);
        var res = new Regenerator().Rebuild(doc, UpTo(doc, a[1]));
        foreach (var f in doc.Features.Take(UpTo(doc, a[1])))
            if (res.Status[f.Id] is { } st && (!st.Ok || st.Warnings.Count > 0)) Console.WriteLine($"{f.Name}: {st.Error} {string.Join("; ", st.Warnings)}");
        var c = V(a[2]); var r = D(a[3]); var eye = V(a[4]).Normalized;
        var up = a[5] == "z" ? Vec3.UnitZ : a[5] == "x" ? Vec3.UnitX : Vec3.UnitY;
        // the rotation taking the view (right, up, towards the eye) to the thumbnail's isometric one
        var right = Vec3.Cross(up, eye).Normalized; var top = Vec3.Cross(eye, right);
        var te = new Vec3(1, 1, 1).Normalized; var tr = Vec3.Cross(Vec3.UnitY, te).Normalized; var tu = Vec3.Cross(te, tr);
        double M(Func<Vec3, double> i, Func<Vec3, double> j) => i(tr) * j(right) + i(tu) * j(top) + i(te) * j(eye);
        Func<Vec3, double> X = v => v.X, Y = v => v.Y, Z = v => v.Z;
        var rot = new Transform(M(X, X), M(X, Y), M(X, Z), M(Y, X), M(Y, Y), M(Y, Z), M(Z, X), M(Z, Y), M(Z, Z), Vec3.Zero);
        var place = Transform.Translation(-rot.Point(c)) * rot;
        var colors = FeatureColors.Of(doc);
        var only = a.Length > 7 ? a[7].Split(';').Select(n => doc.Features.LastOrDefault(f => f.Name == n && FeatureColors.Colored(f))?.Id ?? n).ToHashSet() : null;
        var grey = new[] { 0.6, 0.6, 0.6 };
        var near = new Box3 { Min = c - new Vec3(r, r, r), Max = c + new Vec3(r, r, r) };
        var parts = new List<(DisplayMesh, double[])>();
        foreach (var b in res.State.Bodies)
            foreach (var face in b.Mesh.Faces)
            {
                var feature = FeatureColors.FeatureOf(face.Name);
                var color = feature != null && (only == null || only.Contains(feature)) && colors.TryGetValue(feature, out var fc) ? fc : grey;
                var mesh = AssemblyModel.PlacedMesh(new DisplayMesh { Positions = b.Mesh.Positions, Normals = b.Mesh.Normals, Indices = b.Mesh.Indices[face.IndexStart..(face.IndexStart + face.IndexCount)] }, place);
                // (the view framed on the box about the point; what lies outside it is drawn, clipped by the picture)
                mesh.Bounds = new Box3 { Min = new Vec3(-r, -r, -r), Max = new Vec3(r, r, r) };
                parts.Add((mesh, color));
                if (face.Bounds.Overlaps(near))
                    Console.WriteLine($"{face.Name,-32} {doc.Features.FirstOrDefault(f => f.Id == feature)?.Name ?? "",-24} {face.Surface.Kind,-9} {face.Bounds.Min} .. {face.Bounds.Max}");
            }
        File.WriteAllBytes(a[6], Thumbnail.Render(parts, 800));
        return 0;
    }

    public static int BlendDebug(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("blenddbg part feature [face names…]"); return 1; }
        var doc = Part(a[0]);
        int k = UpTo(doc, a[1]) - 1;
        if (k < 0) { Console.WriteLine($"no feature {a[1]}"); return 1; }
        var reg = new Regenerator();
        reg.Rebuild(doc, k);
        NativeBlend.Debug = s => Console.WriteLine("NB " + s); DirectBlend.Debug = s => Console.WriteLine("DB " + s);
        RollingBall.Debug = s => Console.WriteLine("RB " + s); SweptChamfer.Debug = s => Console.WriteLine("SC " + s);
        if (Environment.GetEnvironmentVariable("FC_SEAM_DEBUG") != null) Kernel.Native.NSeamSplit.Debug = s => Console.WriteLine("SS " + s);
        Console.WriteLine("---- " + a[1]);
        // (FC_TIME_PROF: where the feature's time goes)
        bool prof = Environment.GetEnvironmentVariable("FC_TIME_PROF") != null;
        if (prof) { Prof.Reset(); Prof.Enabled = true; }
        var r = reg.Rebuild(doc, k + 1);
        NativeBlend.Debug = null; DirectBlend.Debug = null; RollingBall.Debug = null; SweptChamfer.Debug = null;
        if (prof)
        {
            Prof.Enabled = false;
            foreach (var (pn, pms, calls) in Prof.Results().OrderByDescending(x => x.ms).Take(80))
                Console.WriteLine($"PROF {pn,-36} {pms,9:0.0} ms x{calls}");
        }
        var st = r.Status[doc.Features[k].Id];
        Console.WriteLine($"status ok={st.Ok} {st.Error} {string.Join("; ", st.Warnings)}");
        foreach (var b in r.State.Bodies.Where(b => b.Solid.Native != null))
            foreach (var f in b.Solid.Native!.Faces.Where(f => a[2..].Contains(f.Tag.Name)))
            {
                Console.WriteLine($"{f.Tag.Name} {f.Surface.Kind} sense={f.Sense} {(f.Surface.Plane is { } pl ? $"n={pl.Normal} d={pl.Distance(Vec3.Zero)}" : "")}");
                foreach (var l in f.Loops) Console.WriteLine("   loop: " + string.Join(" ", l.Coedges.Select(c => $"{(c.Forward ? c.Edge.V0 : c.Edge.V1).P}")));
            }
        return 0;
    }

    /// <summary><c>-- corners part fillet Auto|ConvexFirst|ConcaveFirst out.sprt</c>: a fillet's order at mixed corners set, saved as a copy.</summary>
    public static int Corners(string[] a)
    {
        if (a.Length < 4) { Console.WriteLine("corners part fillet Auto|ConvexFirst|ConcaveFirst out.sprt"); return 1; }
        var doc = Part(a[0]);
        var fi = doc.Features.OfType<FilletFeature>().First(f => f.Name == a[1]);
        fi.MixedCorners = Enum.Parse<MixedCorners>(a[2]);
        doc.Save(a[3]);
        return 0;
    }

    public static int FeatureTimes(string[] a)
    {
        if (a.Length < 2) { Console.WriteLine("feattime part reps [features;…]"); return 1; }
        var names = a.Length > 2 ? a[2].Split(';') : null;
        var best = new Dictionary<string, double>();
        for (int i = 0; i < int.Parse(a[1]); i++)
        {
            var doc = Part(a[0]);
            Csg.ClearCache();
            var res = new Regenerator().Rebuild(doc);
            foreach (var f in doc.Features.Where(f => names == null || names.Contains(f.Name)))
                best[f.Name] = Math.Min(best.GetValueOrDefault(f.Name, double.MaxValue), res.Status[f.Id].Milliseconds);
        }
        foreach (var (n, ms) in best) Console.WriteLine($"{n,-32} {ms,8:0} ms");
        return 0;
    }
}

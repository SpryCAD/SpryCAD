using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>
/// Display cache sizes, the format before (version 3: floats and ints) against now (version 4: quantized), raw and
/// as the container stores them (deflate: Fastest for a part's display.mesh, Optimal for an assembly's caches), and
/// the error decoding brings. <c>meshsize file.sprt|file.sasm ...</c> (the parts of an assembly measured each).
/// </summary>
static class MeshSizeBench
{
    public static int Run(IEnumerable<string> files)
    {
        Console.WriteLine($"{"part",-28} {"tris",8} {"v3 raw",9} {"v3 zip",9} {"v4 raw",9} {"v4 zip",9} {"ratio",6} {"pos err",8} {"nrm err",8} {"decode",7}");
        long t3 = 0, t4 = 0;
        foreach (var (name, doc) in Parts(files))
        {
            var r = new Regenerator().Rebuild(doc);
            var mesh = AssemblyModel.Merge(r.State.Bodies.Select(b => b.Mesh));
            if (mesh.Indices.Length == 0) continue;
            var threads = r.State.Cosmetics.Values.ToList();
            var v3 = WriteV3(mesh, doc.Color, threads);
            var v4 = MeshCache.Write(mesh, doc.Color, threads);
            var sw = Stopwatch.StartNew();
            var back = MeshCache.Read(v4).Mesh;
            var ms = sw.Elapsed.TotalMilliseconds;
            var (pe, ne) = Errors(mesh, back);
            long z3 = Deflated(v3, CompressionLevel.Fastest), z4 = Deflated(v4, CompressionLevel.Fastest);
            t3 += z3; t4 += z4;
            Console.WriteLine($"{name,-28} {mesh.Indices.Length / 3,8} {Kb(v3.Length),9} {Kb(z3),9} {Kb(v4.Length),9} {Kb(z4),9} {(double)z3 / z4,6:0.00} {pe,8:0.00} {ne,7:0.0000}° {ms,5:0.0}ms");
        }
        Console.WriteLine($"{"total (zip, Fastest)",-28} {"",8} {"",9} {Kb(t3),9} {"",9} {Kb(t4),9} {(double)t3 / Math.Max(1, t4),6:0.00}");
        Console.WriteLine("pos err: largest position error in quantization steps (a step = box extent / 65535); nrm err: largest normal angle error");
        return 0;
    }

    static IEnumerable<(string, CadDocument)> Parts(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            if (f.EndsWith(".sasm", StringComparison.OrdinalIgnoreCase))
            {
                var asm = AssemblyDocument.Load(f);
                foreach (var (n, bytes) in asm.Files.Where(kv => kv.Key.EndsWith(".sprt", StringComparison.OrdinalIgnoreCase)))
                    yield return ($"{Path.GetFileNameWithoutExtension(f)}/{Path.GetFileName(n)}", DocumentContainer.Load(bytes, n));
            }
            else yield return (Path.GetFileName(f), CadDocument.Load(f));
        }
    }

    /// <summary>Position error in quantization steps (per axis, the largest), normal error in degrees (the largest).</summary>
    static (double pos, double nrm) Errors(DisplayMesh a, DisplayMesh b)
    {
        var q = PointQuantizer.Around(a.Positions, a.EdgeLines);
        var step = (q.Max - q.Min) * (1 / 65535.0);
        double pe = 0, ne = 0;
        for (int i = 0; i < a.Positions.Length; i += 3)
        {
            if (step.X > 0) pe = Math.Max(pe, Math.Abs(a.Positions[i] - b.Positions[i]) / step.X);
            if (step.Y > 0) pe = Math.Max(pe, Math.Abs(a.Positions[i + 1] - b.Positions[i + 1]) / step.Y);
            if (step.Z > 0) pe = Math.Max(pe, Math.Abs(a.Positions[i + 2] - b.Positions[i + 2]) / step.Z);
            var na = new Vec3(a.Normals[i], a.Normals[i + 1], a.Normals[i + 2]);
            var nb = new Vec3(b.Normals[i], b.Normals[i + 1], b.Normals[i + 2]);
            if (na.Length > 0.5) ne = Math.Max(ne, MeshQuantTests.Angle(na, nb));
        }
        return (pe, ne);
    }

    static long Deflated(byte[] data, CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, level, leaveOpen: true)) z.Write(data);
        return ms.Length;
    }

    static string Kb(long n) => $"{n / 1024.0:0.0}K";

    /// <summary>The version 3 layout (floats and ints), for comparison.</summary>
    static byte[] WriteV3(DisplayMesh m, double[] color, IReadOnlyList<CosmeticThread> threads)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes("FCMESH3"));
        w.Write(GeometryVersion.Current);
        for (int i = 0; i < 3; i++) w.Write((float)color[i]);
        foreach (var v in new[] { m.Bounds.Min, m.Bounds.Max }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write(m.Volume);
        void F(float[] a) { w.Write(a.Length); foreach (var f in a) w.Write(f); }
        F(m.Positions); F(m.Normals);
        w.Write(m.Indices.Length); foreach (var i in m.Indices) w.Write(i);
        w.Write(m.Faces.Count);
        foreach (var f in m.Faces)
        {
            w.Write(f.Name); w.Write(f.IndexStart); w.Write(f.IndexCount);
            var s = f.Surface;
            w.Write((int)s.Kind);
            foreach (var v in new[] { s.Origin, s.Axis, f.FirstPlane.Origin, f.FirstPlane.Normal }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(s.Radius); w.Write(s.Param2);
        }
        F(m.EdgeLines);
        w.Write(m.Edges.Count);
        foreach (var e in m.Edges) { w.Write(e.EdgeIndex); w.Write(e.VertexStart); w.Write(e.VertexCount); }
        w.Write(threads.Count);
        foreach (var t in threads)
        {
            foreach (var v in new[] { t.Origin, t.Axis, t.RefDir }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(t.Radius); w.Write(t.Lead); w.Write(t.Length); w.Write(t.External);
        }
        w.Flush();
        return ms.ToArray();
    }
}

using SpryCad.Core.Assembly;
using SpryCad.Core.Model;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Tests;

/// <summary>The saved display mesh's quantized points and normals, and the cache written and read back.</summary>
static class MeshQuantTests
{
    static string Model(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "samples"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "tests", "SpryCad.Tests", "models", name);
    }

    /// <summary>The angle between two directions in degrees (atan2 of cross and dot: exact for small angles, as acos is not).</summary>
    public static double Angle(Vec3 a, Vec3 b) => Math.Atan2(Vec3.Cross(a, b).Length, Vec3.Dot(a, b)) * 180 / Math.PI;

    public static void Run(TestRunner t)
    {
        t.Test("Octahedral normals decode within 0.01° of the normal encoded", () =>
        {
            var rnd = new Random(7);
            double worst = 0;
            var axes = new[] { (1f, 0f, 0f), (0f, 1f, 0f), (0f, 0f, 1f), (-1f, 0f, 0f), (0f, -1f, 0f), (0f, 0f, -1f), (0.577f, -0.577f, 0.577f) };
            var normals = axes.Concat(Enumerable.Range(0, 200000).Select(_ =>
            {
                double x = rnd.NextDouble() * 2 - 1, y = rnd.NextDouble() * 2 - 1, z = rnd.NextDouble() * 2 - 1;
                var l = Math.Sqrt(x * x + y * y + z * z);
                return ((float)(x / l), (float)(y / l), (float)(z / l));
            }));
            foreach (var (x, y, z) in normals)
            {
                var (a, b) = OctNormal.Encode(x, y, z);
                var (dx, dy, dz) = OctNormal.Decode(a, b);
                worst = Math.Max(worst, Angle(new Vec3(x, y, z), new Vec3(dx, dy, dz)));
            }
            t.True(worst < 0.01, $"worst {worst:0.00000}°");
        });

        t.Test("Quantized points decode within a 65535th of the box per axis; a flat axis keeps its value", () =>
        {
            var q = new PointQuantizer(new Vec3(-10, 5, 3), new Vec3(30, 5, 4));
            var rnd = new Random(3);
            double worst = 0;
            for (int i = 0; i < 10000; i++)
            {
                double x = -10 + 40 * rnd.NextDouble(), z = 3 + rnd.NextDouble();
                worst = Math.Max(worst, Math.Abs(q.DX(q.X(x)) - x) / (40 / 65535.0));
                worst = Math.Max(worst, Math.Abs(q.DZ(q.Z(z)) - z) / (1 / 65535.0));
                t.True(q.DY(q.Y(5)) == 5, "flat axis");
            }
            t.True(worst <= 1, $"worst {worst:0.000} steps");
        });

        t.Test("Display cache written and read back: same triangles, faces, hashes, surfaces and threads", () =>
        {
            var doc = CadDocument.Load(Model("shelled-bracket.sprt"));
            var r = new Regenerator().Rebuild(doc);
            var m = AssemblyModel.Merge(r.State.Bodies.Select(b => b.Mesh));
            var e = MeshCache.Read(MeshCache.Write(m, doc.Color, r.State.Cosmetics.Values.ToList()));
            t.True(e.Current, "made by this code");
            t.Eq(e.Mesh.Indices.Length, m.Indices.Length);
            t.True(e.Mesh.Indices.SequenceEqual(m.Indices), "indices");
            t.Eq(e.Mesh.Faces.Count, m.Faces.Count);
            t.True(m.Faces.All(f => f.GeometryHash != 0), "every face has a geometry hash");
            t.True(e.Mesh.Faces.Zip(m.Faces).All(p => p.First.Name == p.Second.Name && p.First.GeometryHash == p.Second.GeometryHash
                && p.First.IndexStart == p.Second.IndexStart && p.First.Surface.Kind == p.Second.Surface.Kind && p.First.Surface.Radius == p.Second.Surface.Radius), "faces");
            t.True(e.Mesh.Edges.Count == m.Edges.Count && e.Mesh.EdgeLines.Length == m.EdgeLines.Length, "edges");
            t.True(Math.Abs(e.Mesh.Volume - m.Volume) < 1e-9, "volume kept exactly");
        });

        t.Test("Geometry hashes: an edit changes the faces it touches, the rest keep theirs", () =>
        {
            var doc = CadDocument.Load(Model("shelled-bracket.sprt"));
            Dictionary<string, ulong> Hashes(CadDocument d) => AssemblyModel.Merge(new Regenerator().Rebuild(d).State.Bodies.Select(b => b.Mesh)).Faces
                .GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().GeometryHash);
            var before = Hashes(doc);
            t.True(Hashes(doc.Clone()).All(kv => before[kv.Key] == kv.Value), "the same model gives the same hashes");
            var fillet = doc.Features.OfType<FilletFeature>().First();
            var edited = doc.Clone();
            ((FilletFeature)edited.Find(fillet.Id)!).Radius = "(" + fillet.Radius + ")*1.5";
            var after = Hashes(edited);
            var changed = after.Count(kv => before.TryGetValue(kv.Key, out var h) && h != kv.Value);
            var kept = after.Count(kv => before.TryGetValue(kv.Key, out var h) && h == kv.Value);
            t.True(changed > 0 && kept > 0, $"{changed} faces changed, {kept} kept");
        });
    }
}

using System.Text;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Assembly;

/// <summary>
/// A part's display as an assembly keeps it: the display mesh (triangles, normals, face ranges by name, edge lines),
/// its bounds and volume, and the part's color. What a component shows when its part is not loaded (lightweight),
/// cannot be found (missing) or is not shared (an assembly packed for viewing). Binary, little-endian:
/// <code>
///   "FCMESH1"  color r g b (float)  bounds min max (6 double)  volume (double)
///   positions (count, floats)  normals (count, floats)  indices (count, ints)
///   faces (count; name, index start, index count)  edge lines (count, floats)  edges (count; index, start, count)
/// </code>
/// Face ranges keep their names (selection, mates); their surfaces are not kept (a plane placeholder).
/// </summary>
public static class MeshCache
{
    const string Magic = "FCMESH1";

    public sealed record Entry(DisplayMesh Mesh, double[] Color);

    public static byte[] Write(DisplayMesh m, double[] color)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        for (int i = 0; i < 3; i++) w.Write((float)(i < color.Length ? color[i] : 0.75));
        foreach (var v in new[] { m.Bounds.Min, m.Bounds.Max }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write(m.Volume);
        Floats(w, m.Positions); Floats(w, m.Normals);
        w.Write(m.Indices.Length); foreach (var i in m.Indices) w.Write(i);
        w.Write(m.Faces.Count);
        foreach (var f in m.Faces) { w.Write(f.Name); w.Write(f.IndexStart); w.Write(f.IndexCount); }
        Floats(w, m.EdgeLines);
        w.Write(m.Edges.Count);
        foreach (var e in m.Edges) { w.Write(e.EdgeIndex); w.Write(e.VertexStart); w.Write(e.VertexCount); }
        w.Flush();
        return ms.ToArray();
    }

    public static Entry Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        if (Encoding.ASCII.GetString(r.ReadBytes(Magic.Length)) != Magic) throw new InvalidDataException("Not a FeatherCAD display cache.");
        var color = new double[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
        Vec3 V() => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        var m = new DisplayMesh();
        var lo = V(); var hi = V();
        m.Bounds = new Box3 { Min = lo, Max = hi };
        m.Volume = r.ReadDouble();
        m.Positions = ReadFloats(r); m.Normals = ReadFloats(r);
        var ni = r.ReadInt32(); m.Indices = new int[ni];
        for (int i = 0; i < ni; i++) m.Indices[i] = r.ReadInt32();
        var nf = r.ReadInt32();
        var placeholder = Plane.FromPointNormal(Vec3.Zero, Vec3.UnitZ);
        for (int i = 0; i < nf; i++)
        {
            var name = r.ReadString(); int start = r.ReadInt32(), count = r.ReadInt32();
            // bounds of the face's triangles (picking culls by them)
            var box = Box3.Empty;
            for (int k = start; k < start + count; k++) { var v = m.Indices[k]; box.Add(new Vec3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2])); }
            m.Faces.Add(new FaceRange(name, start, count, SurfaceDef.PlaneDef, placeholder, box));
        }
        m.EdgeLines = ReadFloats(r);
        var ne = r.ReadInt32();
        for (int i = 0; i < ne; i++) m.Edges.Add(new EdgeRange(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        return new Entry(m, color);
    }

    static void Floats(BinaryWriter w, float[] a) { w.Write(a.Length); foreach (var f in a) w.Write(f); }

    static float[] ReadFloats(BinaryReader r)
    {
        var n = r.ReadInt32(); var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
        return a;
    }
}

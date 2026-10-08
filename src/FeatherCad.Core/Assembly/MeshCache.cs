using System.Text;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Assembly;

/// <summary>
/// A part's display as an assembly keeps it: the display mesh (triangles, normals, face ranges by name, edge lines),
/// its bounds and volume, and the part's color. What a component shows when its part is not loaded (lightweight),
/// cannot be found (missing) or is not shared (an assembly packed for viewing). Binary, little-endian:
/// <code>
///   "FCMESH2"  color r g b (float)  bounds min max (6 double)  volume (double)
///   positions (count, floats)  normals (count, floats)  indices (count, ints)
///   faces (count; name, index start, index count, surface kind, surface origin and axis, plane point and
///          normal, radius, second parameter)  edge lines (count, floats)  edges (count; index, start, count)
/// </code>
/// Face ranges keep their names and their surfaces (planes, axes and centres for mates; other kinds by kind only).
/// Version 1 caches (no surfaces) are still read.
/// </summary>
public static class MeshCache
{
    // version 2 keeps each face's surface (planes, cylinders, cones, spheres): mates work on lightweight parts
    const string Magic = "FCMESH2", Magic1 = "FCMESH1";

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
        foreach (var f in m.Faces)
        {
            w.Write(f.Name); w.Write(f.IndexStart); w.Write(f.IndexCount);
            var s = f.Surface;
            w.Write((int)s.Kind);
            foreach (var v in new[] { s.Origin, s.Axis, f.FirstPlane.Origin, f.FirstPlane.Normal }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(s.Radius); w.Write(s.Param2);
        }
        Floats(w, m.EdgeLines);
        w.Write(m.Edges.Count);
        foreach (var e in m.Edges) { w.Write(e.EdgeIndex); w.Write(e.VertexStart); w.Write(e.VertexCount); }
        w.Flush();
        return ms.ToArray();
    }

    public static Entry Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
        if (magic != Magic && magic != Magic1) throw new InvalidDataException("Not a FeatherCAD display cache.");
        bool surfaces = magic == Magic;
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
            var surf = SurfaceDef.PlaneDef; var plane = placeholder;
            if (surfaces)
            {
                var kind = (SurfaceKind)r.ReadInt32();
                Vec3 o = V(), ax = V(), po = V(), pn = V();
                double rad = r.ReadDouble(), p2 = r.ReadDouble();
                surf = kind == SurfaceKind.Plane ? SurfaceDef.PlaneDef : new SurfaceDef { Kind = kind, Origin = o, Axis = ax, Radius = rad, Param2 = p2 };
                plane = pn.Length > 0.5 ? Plane.FromPointNormal(po, pn) : placeholder;
            }
            // bounds of the face's triangles (picking culls by them)
            var box = Box3.Empty;
            for (int k = start; k < start + count; k++) { var v = m.Indices[k]; box.Add(new Vec3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2])); }
            m.Faces.Add(new FaceRange(name, start, count, surf, plane, box));
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

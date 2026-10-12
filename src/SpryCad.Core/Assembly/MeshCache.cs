using System.Text;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// A part's display as an assembly keeps it: the display mesh (triangles, normals, face ranges by name, edge lines),
/// its bounds and volume, and the part's color. What a component shows when its part is not loaded (lightweight),
/// cannot be found (missing) or is not shared (an assembly packed for viewing). Binary, little-endian:
/// <code>
///   "FCMESH3"  geometry version (int)  color r g b (float)  bounds min max (6 double)  volume (double)
///   positions (count, floats)  normals (count, floats)  indices (count, ints)
///   faces (count; name, index start, index count, surface kind, surface origin and axis, plane point and
///          normal, radius, second parameter)  edge lines (count, floats)  edges (count; index, start, count)
///   cosmetic threads (count; origin, axis, reference direction, radius, lead, length, external)
/// </code>
/// Face ranges keep their names and their surfaces (planes, axes and centres for mates; other kinds by kind only).
/// The geometry version (<see cref="GeometryVersion"/>) tells a cache made by this code (as good as rebuilding the
/// part) from an older one (shown until the part is rebuilt). Version 1 and 2 caches (no geometry version: 0; no
/// surfaces in version 1; no threads) are still read.
/// </summary>
public static class MeshCache
{
    // version 2 keeps each face's surface (planes, cylinders, cones, spheres): mates work on lightweight parts;
    // version 3 the geometry version and the cosmetic threads
    const string Magic = "FCMESH3", Magic2 = "FCMESH2", Magic1 = "FCMESH1";

    /// <summary>A cache read: its mesh, color, cosmetic threads and the geometry version that made it (0: unknown, older).</summary>
    public sealed record Entry(DisplayMesh Mesh, double[] Color, IReadOnlyList<CosmeticThread> Threads, int Version)
    {
        /// <summary>Made by this code: what rebuilding the part would give.</summary>
        public bool Current => Version == GeometryVersion.Current;
    }

    /// <param name="version">The geometry version to record (null: this code's): a cache rewritten as it was keeps its own.</param>
    public static byte[] Write(DisplayMesh m, double[] color, IReadOnlyList<CosmeticThread>? threads = null, int? version = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(version ?? GeometryVersion.Current);
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
        threads ??= Array.Empty<CosmeticThread>();
        w.Write(threads.Count);
        foreach (var t in threads)
        {
            foreach (var v in new[] { t.Origin, t.Axis, t.RefDir }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(t.Radius); w.Write(t.Lead); w.Write(t.Length); w.Write(t.External);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A rebuilt part's display as a cache: its bodies as one mesh, its cosmetic threads.</summary>
    public static byte[] Of(RegenResult r, double[] color) =>
        Write(AssemblyModel.Merge(r.State.Bodies.Select(b => b.Mesh)), color, r.State.Cosmetics.Values.ToList());

    /// <summary>How long building the whole part took (each feature's last build, cached ones included), in seconds.</summary>
    public static double BuildSeconds(RegenResult r) => r.Status.Values.Sum(s => s.Milliseconds) / 1000;

    /// <summary>The geometry version a cache was made by (0: an older cache, or not one), without reading it all.</summary>
    public static int VersionOf(byte[] data) =>
        data.Length >= Magic.Length + 4 && Encoding.ASCII.GetString(data, 0, Magic.Length) == Magic ? BitConverter.ToInt32(data, Magic.Length) : 0;

    public static Entry Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
        if (magic != Magic && magic != Magic2 && magic != Magic1) throw new InvalidDataException("Not a SpryCAD display cache.");
        bool surfaces = magic != Magic1, v3 = magic == Magic;
        var version = v3 ? r.ReadInt32() : 0;
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
        var threads = new List<CosmeticThread>();
        if (v3)
        {
            var nt = r.ReadInt32();
            for (int i = 0; i < nt; i++)
            {
                Vec3 o = V(), ax = V(), rd = V();
                threads.Add(new CosmeticThread(o, ax, rd, r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadBoolean()));
            }
        }
        return new Entry(m, color, threads, version);
    }

    /// <summary>A cache with its faces renamed (a part re-id'd: <see cref="Model.LegacyIds"/>), made by the same geometry version.</summary>
    public static byte[] Renamed(byte[] data, Func<string, string> rename)
    {
        var e = Read(data);
        for (int i = 0; i < e.Mesh.Faces.Count; i++) e.Mesh.Faces[i] = e.Mesh.Faces[i] with { Name = rename(e.Mesh.Faces[i].Name) };
        return Write(e.Mesh, e.Color, e.Threads, e.Version);
    }

    static void Floats(BinaryWriter w, float[] a) { w.Write(a.Length); foreach (var f in a) w.Write(f); }

    static float[] ReadFloats(BinaryReader r)
    {
        var n = r.ReadInt32(); var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
        return a;
    }
}

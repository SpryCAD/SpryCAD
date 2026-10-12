using System.Text;
using SpryCad.Core.Regen;
using SpryCad.Kernel;

namespace SpryCad.Core.Assembly;

/// <summary>
/// A part's display as an assembly keeps it: the display mesh (triangles, normals, face ranges by name, edge lines),
/// its bounds and volume, and the part's color. What a component shows when its part is not loaded (lightweight),
/// cannot be found (missing) or is not shared (an assembly packed for viewing). Binary, little-endian:
/// <code>
///   "FCMESH4"  geometry version (int)  color r g b (float)  bounds min max (6 double)  volume (double)
///   quantization box min max (6 double)
///   vertices (count); positions: x, y, z (planar ushort runs)  normals: a, b (planar ushort runs, octahedral)
///   indices (count; planar uint run of their zigzag differences, not differenced again)
///   faces (count; name, index start, index count, geometry hash (ulong), surface kind, surface origin and axis,
///          plane point and normal, radius, second parameter)
///   edge line vertices (count); x, y, z (planar ushort runs)  edges (count; index, start, count)
///   cosmetic threads (count; origin, axis, reference direction, radius, lead, length, external)
/// </code>
/// Positions and edge lines are kept as their place in the quantization box, 65535 steps per axis
/// (<see cref="PointQuantizer"/>), normals in two numbers (<see cref="OctNormal"/>): for display (and picking on a
/// part shown from its cache), never for measuring. Laid out for the container's compressor: a planar run is one
/// value per vertex (one coordinate of each, say) as differences from the one before, all low bytes, then the
/// next byte of each, and so on; neighbouring vertices are close, so the high bytes are mostly the same and
/// compress to almost nothing (about half the size of the values interleaved). Face ranges keep their names, their surfaces exactly (planes,
/// axes and centres for mates; other kinds by kind only) and their geometry hashes (the visual diff).
/// The geometry version (<see cref="GeometryVersion"/>) tells a cache made by this code (as good as rebuilding the
/// part) from an older one (shown until the part is rebuilt). Version 3 caches (floats and ints throughout), and
/// version 1 and 2 ones (no geometry version: 0; no surfaces in version 1; no threads) are still read.
/// </summary>
public static class MeshCache
{
    // version 2 keeps each face's surface (planes, cylinders, cones, spheres): mates work on lightweight parts;
    // version 3 the geometry version and the cosmetic threads; version 4 quantized points and normals, face hashes
    const string Magic = "FCMESH4", Magic3 = "FCMESH3", Magic2 = "FCMESH2", Magic1 = "FCMESH1";

    /// <summary>A cache read: its mesh, color, cosmetic threads and the geometry version that made it (0: unknown, older).</summary>
    public sealed record Entry(DisplayMesh Mesh, double[] Color, IReadOnlyList<CosmeticThread> Threads, int Version)
    {
        /// <summary>Made by this code: what rebuilding the part would give.</summary>
        public bool Current => Version == GeometryVersion.Current;
    }

    public static byte[] Write(DisplayMesh m, double[] color, IReadOnlyList<CosmeticThread>? threads = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(GeometryVersion.Current);
        for (int i = 0; i < 3; i++) w.Write((float)(i < color.Length ? color[i] : 0.75));
        foreach (var v in new[] { m.Bounds.Min, m.Bounds.Max }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        w.Write(m.Volume);
        var q = PointQuantizer.Around(m.Positions, m.EdgeLines);
        foreach (var v in new[] { q.Min, q.Max }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        int nv = m.Positions.Length / 3;
        w.Write(nv);
        Points(w, q, m.Positions);
        var na = new uint[nv]; var nb = new uint[nv];
        for (int i = 0; i < nv; i++)
        {
            var (a, b) = OctNormal.Encode(m.Normals[3 * i], m.Normals[3 * i + 1], m.Normals[3 * i + 2]);
            na[i] = (ushort)a; nb[i] = (ushort)b;
        }
        Planar(w, na, 2); Planar(w, nb, 2);
        var di = new uint[m.Indices.Length];
        for (int i = 0; i < di.Length; i++) { var d = m.Indices[i] - (i > 0 ? m.Indices[i - 1] : 0); di[i] = (uint)((d << 1) ^ (d >> 31)); }
        w.Write(di.Length);
        Planar(w, di, 4, delta: false);
        w.Write(m.Faces.Count);
        foreach (var f in m.Faces)
        {
            w.Write(f.Name); w.Write(f.IndexStart); w.Write(f.IndexCount); w.Write(f.GeometryHash);
            var s = f.Surface;
            w.Write((int)s.Kind);
            foreach (var v in new[] { s.Origin, s.Axis, f.FirstPlane.Origin, f.FirstPlane.Normal }) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(s.Radius); w.Write(s.Param2);
        }
        w.Write(m.EdgeLines.Length / 3);
        Points(w, q, m.EdgeLines);
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
        data.Length >= Magic.Length + 4 && Encoding.ASCII.GetString(data, 0, Magic.Length) is Magic or Magic3 ? BitConverter.ToInt32(data, Magic.Length) : 0;

    public static Entry Read(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var magic = Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
        if (magic != Magic && magic != Magic3 && magic != Magic2 && magic != Magic1) throw new InvalidDataException("Not a SpryCAD display cache.");
        bool v4 = magic == Magic, surfaces = magic != Magic1, versioned = v4 || magic == Magic3;
        var version = versioned ? r.ReadInt32() : 0;
        var color = new double[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
        Vec3 V() => new(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        var m = new DisplayMesh();
        var lo = V(); var hi = V();
        m.Bounds = new Box3 { Min = lo, Max = hi };
        m.Volume = r.ReadDouble();
        var q = v4 ? new PointQuantizer(V(), V()) : default;
        if (v4)
        {
            var nv = r.ReadInt32();
            m.Positions = ReadPoints(r, q, nv);
            var na = ReadPlanar(r, nv, 2); var nb = ReadPlanar(r, nv, 2);
            m.Normals = new float[3 * nv];
            for (int i = 0; i < nv; i++) (m.Normals[3 * i], m.Normals[3 * i + 1], m.Normals[3 * i + 2]) = OctNormal.Decode((short)na[i], (short)nb[i]);
            var di = ReadPlanar(r, r.ReadInt32(), 4, delta: false);
            m.Indices = new int[di.Length];
            for (int i = 0, prev = 0; i < di.Length; i++) m.Indices[i] = prev += (int)(di[i] >> 1) ^ -(int)(di[i] & 1);
        }
        else
        {
            m.Positions = ReadFloats(r); m.Normals = ReadFloats(r);
            var ni = r.ReadInt32(); m.Indices = new int[ni];
            for (int i = 0; i < ni; i++) m.Indices[i] = r.ReadInt32();
        }
        var nf = r.ReadInt32();
        var placeholder = Plane.FromPointNormal(Vec3.Zero, Vec3.UnitZ);
        for (int i = 0; i < nf; i++)
        {
            var name = r.ReadString(); int start = r.ReadInt32(), count = r.ReadInt32();
            var hash = v4 ? r.ReadUInt64() : 0;
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
            m.Faces.Add(new FaceRange(name, start, count, surf, plane, box, hash));
        }
        m.EdgeLines = v4 ? ReadPoints(r, q, r.ReadInt32()) : ReadFloats(r);
        var ne = r.ReadInt32();
        for (int i = 0; i < ne; i++) m.Edges.Add(new EdgeRange(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        var threads = new List<CosmeticThread>();
        if (versioned)
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

    /// <summary>Points as three planar runs (x, y, z), quantized in the box.</summary>
    static void Points(BinaryWriter w, PointQuantizer q, float[] xyz)
    {
        int n = xyz.Length / 3;
        var c = new uint[n];
        for (int i = 0; i < n; i++) c[i] = q.X(xyz[3 * i]);
        Planar(w, c, 2);
        for (int i = 0; i < n; i++) c[i] = q.Y(xyz[3 * i + 1]);
        Planar(w, c, 2);
        for (int i = 0; i < n; i++) c[i] = q.Z(xyz[3 * i + 2]);
        Planar(w, c, 2);
    }

    static float[] ReadPoints(BinaryReader r, PointQuantizer q, int n)
    {
        var a = new float[3 * n];
        var x = ReadPlanar(r, n, 2);
        for (int i = 0; i < n; i++) a[3 * i] = q.DX((ushort)x[i]);
        x = ReadPlanar(r, n, 2);
        for (int i = 0; i < n; i++) a[3 * i + 1] = q.DY((ushort)x[i]);
        x = ReadPlanar(r, n, 2);
        for (int i = 0; i < n; i++) a[3 * i + 2] = q.DZ((ushort)x[i]);
        return a;
    }

    /// <summary>
    /// A planar run: the values as differences from the one before (wrapping in <paramref name="bytes"/> bytes;
    /// as they are when not <paramref name="delta"/>), all their low bytes, then all their next bytes, up to
    /// <paramref name="bytes"/>.
    /// </summary>
    static void Planar(BinaryWriter w, uint[] values, int bytes, bool delta = true)
    {
        var buf = new byte[values.Length];
        for (int b = 0; b < bytes; b++)
        {
            uint prev = 0;
            for (int i = 0; i < values.Length; i++) { buf[i] = (byte)((values[i] - prev) >> (8 * b)); if (delta) prev = values[i]; }
            w.Write(buf);
        }
    }

    static uint[] ReadPlanar(BinaryReader r, int n, int bytes, bool delta = true)
    {
        var d = new uint[n];
        for (int b = 0; b < bytes; b++)
        {
            var buf = r.ReadBytes(n);
            if (buf.Length < n) throw new EndOfStreamException();
            for (int i = 0; i < n; i++) d[i] |= (uint)buf[i] << (8 * b);
        }
        if (!delta) return d;
        uint mask = bytes == 4 ? uint.MaxValue : (1u << (8 * bytes)) - 1, prev = 0;
        for (int i = 0; i < n; i++) d[i] = prev = (prev + d[i]) & mask;
        return d;
    }

    static float[] ReadFloats(BinaryReader r)
    {
        var n = r.ReadInt32(); var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadSingle();
        return a;
    }
}

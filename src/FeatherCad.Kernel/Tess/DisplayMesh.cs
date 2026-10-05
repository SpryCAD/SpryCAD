namespace FeatherCad.Kernel;

/// <summary>Contiguous triangle range of one named (UI) face.</summary>
public sealed record FaceRange(string Name, int IndexStart, int IndexCount, SurfaceDef Surface, Plane FirstPlane, Box3 Bounds);

/// <summary>Contiguous line range of one model edge.</summary>
public sealed record EdgeRange(int EdgeIndex, int VertexStart, int VertexCount);

/// <summary>GPU ready tessellation of a solid with face/edge ranges for highlighting and picking.</summary>
public sealed class DisplayMesh
{
    public float[] Positions = Array.Empty<float>();
    public float[] Normals = Array.Empty<float>();
    public int[] Indices = Array.Empty<int>();
    public List<FaceRange> Faces = new();
    /// <summary>Edge line vertices (pairs, xyz).</summary>
    public float[] EdgeLines = Array.Empty<float>();
    public List<EdgeRange> Edges = new();
    public Box3 Bounds = Box3.Empty;

    public static DisplayMesh Build(Solid solid)
    {
        var m = new DisplayMesh();
        var topo = solid.Topology;
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        // order faces by name so each UI face is contiguous
        var order = Enumerable.Range(0, solid.Faces.Count).OrderBy(i => solid.Faces[i].Tag.Name, StringComparer.Ordinal).ToList();
        string? curName = null; int curStart = 0; SurfaceDef? curSurf = null; Plane? curPlane = null; var curBox = Box3.Empty;
        void Flush()
        {
            if (curName != null && idx.Count > curStart)
                m.Faces.Add(new FaceRange(curName, curStart, idx.Count - curStart, curSurf!, curPlane!, curBox));
        }
        foreach (var fi in order)
        {
            var f = solid.Faces[fi];
            if (f.Tag.Name != curName)
            {
                Flush();
                curName = f.Tag.Name; curStart = idx.Count; curSurf = f.Tag.Surface; curPlane = f.Plane; curBox = Box3.Empty;
            }
            var loops3 = topo.FaceLoops[fi].Select(l => l.Select(v => topo.Vertices[v]).ToArray()).ToList();
            var tris = Triangulator.TriangulateLoops(loops3, f.Plane.Normal);
            var fn = f.Plane.Normal;
            var curved = f.Tag.Surface.IsCurved;
            foreach (var t in tris)
            {
                foreach (var p in new[] { t.A, t.B, t.C })
                {
                    idx.Add(pos.Count / 3);
                    pos.Add((float)p.X); pos.Add((float)p.Y); pos.Add((float)p.Z);
                    var n = curved ? f.Tag.Surface.NormalAt(p, fn) : fn;
                    nrm.Add((float)n.X); nrm.Add((float)n.Y); nrm.Add((float)n.Z);
                    curBox.Add(p);
                    m.Bounds.Add(p);
                }
            }
        }
        Flush();
        m.Positions = pos.ToArray(); m.Normals = nrm.ToArray(); m.Indices = idx.ToArray();

        var lines = new List<float>();
        foreach (var e in topo.Edges)
        {
            int start = lines.Count / 3;
            foreach (var s in e.Segs)
            {
                lines.Add((float)s.A.X); lines.Add((float)s.A.Y); lines.Add((float)s.A.Z);
                lines.Add((float)s.B.X); lines.Add((float)s.B.Y); lines.Add((float)s.B.Z);
            }
            m.Edges.Add(new EdgeRange(e.Index, start, lines.Count / 3 - start));
        }
        m.EdgeLines = lines.ToArray();
        return m;
    }
}

namespace FeatherCad.Kernel;

/// <summary>
/// Small preview image of a part, rendered on the CPU from its display meshes (isometric view, shaded, with a
/// transparent background): independent of the OpenGL viewport, so it can be made anywhere, e.g. when saving.
/// </summary>
public static class Thumbnail
{
    /// <summary>PNG of the meshes seen from the isometric direction (+X, +Y, +Z towards the viewer; Y up).</summary>
    public static byte[] Render(IEnumerable<(DisplayMesh mesh, double[] color)> parts, int size = 256)
    {
        var list = parts.Where(p => p.mesh.Indices.Length > 0).ToList();
        const int ss = 2; // supersampling
        int n = size * ss;
        var rgb = new float[n * n * 3];
        var cover = new float[n * n];
        var depth = new float[n * n];
        Array.Fill(depth, float.NegativeInfinity);
        // view basis
        var toEye = new Vec3(1, 1, 1).Normalized;
        var right = Vec3.Cross(Vec3.UnitY, toEye).Normalized;
        var up = Vec3.Cross(toEye, right);
        var light = (toEye * 0.6 + up * 0.6 + right * -0.3).Normalized;
        var box = Box3.Empty;
        foreach (var (m, _) in list) box.Add(m.Bounds);
        if (list.Count == 0 || box.IsEmpty) return Png.Encode(size, size, new byte[size * size * 4]);
        // fit the projected bounds (corners of the box) with a margin
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int c = 0; c < 8; c++)
        {
            var p = new Vec3((c & 1) == 0 ? box.Min.X : box.Max.X, (c & 2) == 0 ? box.Min.Y : box.Max.Y, (c & 4) == 0 ? box.Min.Z : box.Max.Z);
            var x = Vec3.Dot(p, right); var y = Vec3.Dot(p, up);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        var scale = 0.9 * n / Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        foreach (var (m, color) in list)
        {
            var pos = m.Positions; var nrm = m.Normals; var idx = m.Indices;
            int vc = pos.Length / 3;
            var sx = new float[vc]; var sy = new float[vc]; var sz = new float[vc];
            for (int i = 0; i < vc; i++)
            {
                var p = new Vec3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
                sx[i] = (float)((Vec3.Dot(p, right) - cx) * scale + n / 2.0);
                sy[i] = (float)(n / 2.0 - (Vec3.Dot(p, up) - cy) * scale);
                sz[i] = (float)Vec3.Dot(p, toEye);
            }
            float r0 = (float)color[0], g0 = (float)color[1], b0 = (float)color[2];
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                float ax = sx[a], ay = sy[a], bx = sx[b], by = sy[b], qx = sx[c], qy = sy[c];
                var area = (bx - ax) * (qy - ay) - (by - ay) * (qx - ax);
                if (Math.Abs(area) < 1e-12f) continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, qx)))), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, qx))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, qy)))), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, qy))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        var w0 = ((bx - px) * (qy - py) - (by - py) * (qx - px)) / area;
                        var w1 = ((qx - px) * (ay - py) - (qy - py) * (ax - px)) / area;
                        var w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        var z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                        int k = y * n + x;
                        if (z <= depth[k]) continue;
                        depth[k] = z;
                        // smooth normal, flipped towards the viewer
                        var nx = w0 * nrm[3 * a] + w1 * nrm[3 * b] + w2 * nrm[3 * c];
                        var ny = w0 * nrm[3 * a + 1] + w1 * nrm[3 * b + 1] + w2 * nrm[3 * c + 1];
                        var nz = w0 * nrm[3 * a + 2] + w1 * nrm[3 * b + 2] + w2 * nrm[3 * c + 2];
                        var nv = new Vec3(nx, ny, nz).Normalized;
                        if (Vec3.Dot(nv, toEye) < 0) nv = -nv;
                        var diffuse = Math.Max(0, Vec3.Dot(nv, light));
                        var spec = Math.Pow(Math.Max(0, Vec3.Dot(nv, (light + toEye).Normalized)), 30) * 0.25;
                        var shade = (float)(0.35 + 0.65 * diffuse);
                        rgb[3 * k] = Math.Min(1, r0 * shade + (float)spec);
                        rgb[3 * k + 1] = Math.Min(1, g0 * shade + (float)spec);
                        rgb[3 * k + 2] = Math.Min(1, b0 * shade + (float)spec);
                        cover[k] = 1;
                    }
            }
        }
        // downsample with coverage as alpha
        var outPx = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int dy = 0; dy < ss; dy++)
                    for (int dx = 0; dx < ss; dx++)
                    {
                        int k = (y * ss + dy) * n + x * ss + dx;
                        r += rgb[3 * k]; g += rgb[3 * k + 1]; b += rgb[3 * k + 2]; a += cover[k];
                    }
                int o = 4 * (y * size + x);
                if (a > 0) { outPx[o] = (byte)(255 * r / a); outPx[o + 1] = (byte)(255 * g / a); outPx[o + 2] = (byte)(255 * b / a); }
                outPx[o + 3] = (byte)(255 * a / (ss * ss));
            }
        return Png.Encode(size, size, outPx);
    }
}

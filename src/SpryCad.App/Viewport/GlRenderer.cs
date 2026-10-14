using System.Numerics;
using System.Runtime.InteropServices;
using SpryCad.Kernel;
using Silk.NET.OpenGL;

namespace SpryCad.App.Viewport;

/// <summary>RGBA color in 0..1.</summary>
public readonly record struct Rgba(float R, float G, float B, float A = 1)
{
    public static Rgba FromBytes(byte r, byte g, byte b, byte a = 255) => new(r / 255f, g / 255f, b / 255f, a / 255f);
    public Rgba WithAlpha(float a) => this with { A = a };
}

/// <summary>Immediate-mode style geometry rebuilt every frame (sketches, planes, previews, overlays).</summary>
public sealed class DynamicGeometry
{
    // x y z r g b a
    public readonly List<float> Lines = new();
    public readonly List<float> Tris = new();
    public readonly List<float> TopLines = new();   // drawn without depth test
    public readonly List<float> TopTris = new();
    public readonly List<float> TransTris = new();  // transparent, depth tested, no depth write

    public void Clear() { Lines.Clear(); Tris.Clear(); TopLines.Clear(); TopTris.Clear(); TransTris.Clear(); }

    static void V(List<float> l, Vec3 p, Rgba c)
    {
        int n = l.Count;
        CollectionsMarshal.SetCount(l, n + 7);
        var s = CollectionsMarshal.AsSpan(l).Slice(n, 7);
        s[0] = (float)p.X; s[1] = (float)p.Y; s[2] = (float)p.Z; s[3] = c.R; s[4] = c.G; s[5] = c.B; s[6] = c.A;
    }

    /// <summary>What was drawn between a <see cref="Mark"/> and now, kept to be drawn again as it is.</summary>
    public sealed record Piece(float[] Lines, float[] Tris, float[] TopLines, float[] TopTris, float[] TransTris);

    public (int, int, int, int, int) Mark() => (Lines.Count, Tris.Count, TopLines.Count, TopTris.Count, TransTris.Count);

    public Piece Since((int l, int t, int tl, int tt, int tr) m)
    {
        static float[] Tail(List<float> list, int from) => CollectionsMarshal.AsSpan(list)[from..].ToArray();
        return new Piece(Tail(Lines, m.l), Tail(Tris, m.t), Tail(TopLines, m.tl), Tail(TopTris, m.tt), Tail(TransTris, m.tr));
    }

    public void Add(Piece p)
    {
        Lines.AddRange(p.Lines); Tris.AddRange(p.Tris); TopLines.AddRange(p.TopLines); TopTris.AddRange(p.TopTris); TransTris.AddRange(p.TransTris);
    }

    public void Line(Vec3 a, Vec3 b, Rgba c, bool top = false) { var l = top ? TopLines : Lines; V(l, a, c); V(l, b, c); }

    public void Tri(Vec3 a, Vec3 b, Vec3 c, Rgba col, bool top = false, bool transparent = false)
    {
        var l = transparent ? TransTris : top ? TopTris : Tris;
        V(l, a, col); V(l, b, col); V(l, c, col);
    }

    public void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Rgba col, bool top = false, bool transparent = false)
    {
        Tri(a, b, c, col, top, transparent); Tri(a, c, d, col, top, transparent);
    }

    /// <summary>Dashed line (dash lengths in world units).</summary>
    public void Dashed(Vec3 a, Vec3 b, Rgba c, double dash, bool top = false)
    {
        var len = Vec3.Distance(a, b);
        if (dash <= 0 || len / dash > 2000) { Line(a, b, c, top); return; }
        var d = (b - a) / len;
        for (double t = 0; t < len; t += dash * 2)
            Line(a + d * t, a + d * Math.Min(t + dash, len), c, top);
    }
}

/// <summary>GPU buffers for one body's display mesh.</summary>
public sealed class GpuMesh : IDisposable
{
    public uint Vao, VboPos, VboNrm, Ebo, EdgeVao, EdgeVbo;
    public int IndexCount, EdgeVertexCount;
    public DisplayMesh Source = null!;
    GL? _gl;

    public static unsafe GpuMesh Create(GL gl, DisplayMesh m)
    {
        var g = new GpuMesh { Source = m, _gl = gl, IndexCount = m.Indices.Length, EdgeVertexCount = m.EdgeLines.Length / 3 };
        g.Vao = gl.GenVertexArray();
        gl.BindVertexArray(g.Vao);
        g.VboPos = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.VboPos);
        fixed (float* p = m.Positions) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Math.Max(1, m.Positions.Length) * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        g.VboNrm = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.VboNrm);
        fixed (float* p = m.Normals) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Math.Max(1, m.Normals.Length) * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        g.Ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, g.Ebo);
        fixed (int* p = m.Indices) gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Math.Max(1, m.Indices.Length) * 4), p, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);

        g.EdgeVao = gl.GenVertexArray();
        gl.BindVertexArray(g.EdgeVao);
        g.EdgeVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.EdgeVbo);
        fixed (float* p = m.EdgeLines) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Math.Max(1, m.EdgeLines.Length) * 4), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.BindVertexArray(0);
        return g;
    }

    public void Dispose()
    {
        if (_gl == null) return;
        _gl.DeleteVertexArray(Vao); _gl.DeleteVertexArray(EdgeVao);
        _gl.DeleteBuffer(VboPos); _gl.DeleteBuffer(VboNrm); _gl.DeleteBuffer(Ebo); _gl.DeleteBuffer(EdgeVbo);
        _gl = null;
    }
}

/// <summary>Owns shaders and draws meshes, edges and dynamic geometry.</summary>
public sealed unsafe class GlRenderer : IDisposable
{
    readonly GL _gl;
    readonly bool _es;
    uint _vcol;
    int _vcolVP;

    // the shaded and flat programs, plain and cutting at the section plane: a fragment shader that may discard turns off
    // early depth rejection, so every hidden fragment of every triangle is shaded (slow with large models, and very slow
    // on a software renderer): the cutting ones only while there is a section
    sealed record LitProgram(uint P, int VP, int View, int Color, int Persp, int Clip);
    sealed record FlatProgram(uint P, int VP, int Color, int Clip);
    LitProgram _litPlain = null!, _litCut = null!;
    FlatProgram _flatPlain = null!, _flatCut = null!;
    LitProgram Lit => Clip != null ? _litCut : _litPlain;
    FlatProgram Flat => Clip != null ? _flatCut : _flatPlain;

    /// <summary>
    /// The section plane (a, b, c, d in world coordinates: what is past it, where ax + by + cz + d > 0, is cut away), or
    /// null for none. Faces and edges are cut; the cut faces are filled by <see cref="DrawCap"/>.
    /// </summary>
    public Vector4? Clip { get; set; }
    /// <summary>Where the meshes drawn next are placed (an assembly instance's placement; identity for a part's bodies): the clip plane is moved into them.</summary>
    public Matrix4x4 Model { get; set; } = Matrix4x4.Identity;

    // the clip plane in the coordinates of the mesh drawn (none: one that cuts nothing)
    Vector4 ModelClip => Clip is { } c ? Vector4.Transform(c, Matrix4x4.Transpose(Model)) : new Vector4(0, 0, 0, -1);
    uint _dynVao, _dynVbo;
    uint _fbo, _colorRb, _depthRb;
    int _fbW, _fbH, _samples;

    public GlRenderer(GL gl, bool es)
    {
        _gl = gl; _es = es;
        Init();
    }

    string Header => _es ? "#version 300 es\nprecision highp float;\n" : "#version 150\n";

    void Init()
    {
        const string litVs = """
            in vec3 aPos; in vec3 aNormal;
            uniform mat4 uViewProj; uniform mat4 uView; uniform vec4 uClip;
            out vec3 vN; out vec3 vPosV; out float vClip;
            void main() {
              vClip = dot(vec4(aPos, 1.0), uClip);
              vN = mat3(uView) * aNormal;
              vPosV = (uView * vec4(aPos, 1.0)).xyz;
              gl_Position = uViewProj * vec4(aPos, 1.0);
            }
            """;
        const string litFs = """
            in vec3 vN; in vec3 vPosV; in float vClip;
            uniform vec4 uColor; uniform float uPersp;
            out vec4 oColor;
            void main() {
            #ifdef SECTION
              // the section: past the plane cut away
              if (vClip > 0.0) discard;
            #endif
              vec3 n = normalize(vN);
              vec3 v = uPersp > 0.5 ? normalize(-vPosV) : vec3(0.0, 0.0, 1.0);
              if (dot(n, v) < 0.0) n = -n;
              // camera attached lights: a headlight slightly above/left plus two fills
              vec3 l1 = normalize(vec3(-0.15, 0.25, 1.0));
              vec3 l2 = normalize(vec3(-0.8, 0.7, 0.4));
              vec3 l3 = normalize(vec3(0.8, -0.3, 0.5));
              float d = 0.55 * max(dot(n, l1), 0.0) + 0.18 * max(dot(n, l2), 0.0) + 0.12 * max(dot(n, l3), 0.0) + 0.30;
              vec3 h = normalize(l1 + v);
              float s = pow(max(dot(n, h), 0.0), 60.0) * 0.22;
              oColor = vec4(uColor.rgb * d + vec3(s), uColor.a);
            }
            """;
        const string flatVs = """
            in vec3 aPos;
            uniform mat4 uViewProj; uniform vec4 uClip;
            out float vClip;
            void main() { vClip = dot(vec4(aPos, 1.0), uClip); gl_Position = uViewProj * vec4(aPos, 1.0); }
            """;
        const string flatFs = """
            uniform vec4 uColor;
            in float vClip;
            out vec4 oColor;
            void main() {
            #ifdef SECTION
              if (vClip > 0.0) discard;
            #endif
              oColor = uColor;
            }
            """;
        const string vcolVs = """
            in vec3 aPos; in vec4 aColor;
            uniform mat4 uViewProj;
            out vec4 vColor;
            void main() { vColor = aColor; gl_Position = uViewProj * vec4(aPos, 1.0); }
            """;
        const string vcolFs = """
            in vec4 vColor;
            out vec4 oColor;
            void main() { oColor = vColor; }
            """;
        LitProgram MakeLit(string defines)
        {
            var p = Program(defines + litVs, defines + litFs, ("aPos", 0), ("aNormal", 1));
            return new LitProgram(p, _gl.GetUniformLocation(p, "uViewProj"), _gl.GetUniformLocation(p, "uView"),
                _gl.GetUniformLocation(p, "uColor"), _gl.GetUniformLocation(p, "uPersp"), _gl.GetUniformLocation(p, "uClip"));
        }
        FlatProgram MakeFlat(string defines)
        {
            var p = Program(defines + flatVs, defines + flatFs, ("aPos", 0));
            return new FlatProgram(p, _gl.GetUniformLocation(p, "uViewProj"), _gl.GetUniformLocation(p, "uColor"), _gl.GetUniformLocation(p, "uClip"));
        }
        const string section = "#define SECTION\n";
        _litPlain = MakeLit(""); _litCut = MakeLit(section);
        _flatPlain = MakeFlat(""); _flatCut = MakeFlat(section);
        _vcol = Program(vcolVs, vcolFs, ("aPos", 0), ("aColor", 1));
        _vcolVP = _gl.GetUniformLocation(_vcol, "uViewProj");

        _dynVao = _gl.GenVertexArray();
        _gl.BindVertexArray(_dynVao);
        _dynVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 28, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 28, (void*)12);
        _gl.BindVertexArray(0);

        _gl.GetInteger((GetPName)0x8D57, out int maxSamples);
        _samples = Math.Clamp(maxSamples, 0, 4);
    }

    uint Program(string vs, string fs, params (string name, uint loc)[] attribs)
    {
        uint Compile(ShaderType t, string src)
        {
            var s = _gl.CreateShader(t);
            _gl.ShaderSource(s, Header + src);
            _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException("Shader compile failed: " + _gl.GetShaderInfoLog(s));
            return s;
        }
        var v = Compile(ShaderType.VertexShader, vs);
        var f = Compile(ShaderType.FragmentShader, fs);
        var p = _gl.CreateProgram();
        _gl.AttachShader(p, v); _gl.AttachShader(p, f);
        foreach (var (name, loc) in attribs) _gl.BindAttribLocation(p, loc, name);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("Program link failed: " + _gl.GetProgramInfoLog(p));
        _gl.DeleteShader(v); _gl.DeleteShader(f);
        return p;
    }

    public GL Gl => _gl;

    /// <summary>Binds the multisampled offscreen target (created/resized as needed).</summary>
    public void BeginFrame(int w, int h)
    {
        if (w != _fbW || h != _fbH || _fbo == 0)
        {
            if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteRenderbuffer(_colorRb); _gl.DeleteRenderbuffer(_depthRb); }
            _fbW = w; _fbH = h;
            _fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _colorRb = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _colorRb);
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.Rgba8, (uint)w, (uint)h);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _colorRb);
            _depthRb = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
            // (with a stencil: the section's caps)
            _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRb);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(1, 1, 1, 1);
        _gl.ClearDepth(1);
        _gl.DepthMask(true);
        _gl.ColorMask(true, true, true, true);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public void EndFrame(int targetFb)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)targetFb);
        _gl.BlitFramebuffer(0, 0, _fbW, _fbH, 0, 0, _fbW, _fbH, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)targetFb);
    }

    public void Background(Rgba top, Rgba bottom)
    {
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        var data = new float[]
        {
            -1, -1, 0, bottom.R, bottom.G, bottom.B, 1,
             1, -1, 0, bottom.R, bottom.G, bottom.B, 1,
             1,  1, 0, top.R, top.G, top.B, 1,
            -1, -1, 0, bottom.R, bottom.G, bottom.B, 1,
             1,  1, 0, top.R, top.G, top.B, 1,
            -1,  1, 0, top.R, top.G, top.B, 1,
        };
        DrawDynamic(data, PrimitiveType.Triangles, Matrix4x4.Identity);
        _gl.DepthMask(true);
    }

    public void SetupDepth(bool depthTest, bool depthWrite)
    {
        if (depthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(depthWrite);
    }

    public void DrawMesh(GpuMesh m, Rgba color, Matrix4x4 viewProj, Matrix4x4 view, bool persp, int start = 0, int count = -1)
    {
        if (m.IndexCount == 0) return;
        var lp = Lit;
        _gl.UseProgram(lp.P);
        SetMat(lp.VP, viewProj); SetMat(lp.View, view);
        _gl.Uniform4(lp.Color, color.R, color.G, color.B, color.A);
        _gl.Uniform1(lp.Persp, persp ? 1f : 0f);
        var clip = ModelClip;
        _gl.Uniform4(lp.Clip, clip.X, clip.Y, clip.Z, clip.W);
        _gl.BindVertexArray(m.Vao);
        if (count < 0) count = m.IndexCount;
        _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * 4));
        _gl.BindVertexArray(0);
    }

    /// <summary>Solid color (unlit) mesh draw, used for selection overlays.</summary>
    public void DrawMeshFlat(GpuMesh m, Rgba color, Matrix4x4 viewProj, int start, int count)
    {
        var fp = Flat;
        _gl.UseProgram(fp.P);
        SetMat(fp.VP, viewProj);
        var fc = ModelClip;
        _gl.Uniform4(fp.Clip, fc.X, fc.Y, fc.Z, fc.W);
        _gl.Uniform4(fp.Color, color.R, color.G, color.B, color.A);
        _gl.BindVertexArray(m.Vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * 4));
        _gl.BindVertexArray(0);
    }

    public void DrawEdges(GpuMesh m, Rgba color, Matrix4x4 viewProj, int start = 0, int count = -1)
    {
        if (m.EdgeVertexCount == 0) return;
        var fp = Flat;
        _gl.UseProgram(fp.P);
        SetMat(fp.VP, viewProj);
        var fc = ModelClip;
        _gl.Uniform4(fp.Clip, fc.X, fc.Y, fc.Z, fc.W);
        _gl.Uniform4(fp.Color, color.R, color.G, color.B, color.A);
        _gl.BindVertexArray(m.EdgeVao);
        _gl.DrawArrays(PrimitiveType.Lines, start, (uint)(count < 0 ? m.EdgeVertexCount : count));
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// A closed mesh's cap where the section cuts it: its surface left by the cut drawn into the stencil, each pixel
    /// flipped once for each crossing, so a pixel left odd is one where the section plane is inside the solid; there
    /// <paramref name="cap"/> (triangles on the plane, in world coordinates, colored, as <see cref="DrawDynamic(float[], PrimitiveType, Matrix4x4)"/>
    /// takes them) is drawn, depth tested like any face. <paramref name="meshViewProj"/> places the mesh (its
    /// <see cref="Model"/> set to match, so the cut follows it).
    /// </summary>
    public void DrawCap(GpuMesh m, Matrix4x4 meshViewProj, float[] cap, Matrix4x4 viewProj)
    {
        if (m.IndexCount == 0 || Clip == null) return;
        _gl.Enable(EnableCap.StencilTest);
        _gl.StencilMask(0xFF);
        _gl.ClearStencil(0);
        _gl.Clear(ClearBufferMask.StencilBufferBit);
        // the count: every remaining surface, nothing drawn, nothing hidden
        _gl.StencilFunc(StencilFunction.Always, 0, 1);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Invert);
        _gl.ColorMask(false, false, false, false);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.DepthTest);
        DrawMeshFlat(m, new Rgba(0, 0, 0, 1), meshViewProj, 0, m.IndexCount);
        // the cap where it came out odd
        _gl.ColorMask(true, true, true, true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.StencilFunc(StencilFunction.Equal, 1, 1);
        _gl.StencilOp(StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
        PolygonOffset(true, 1, 1); // (behind the cut's outline, drawn on it)
        DrawDynamic(cap, PrimitiveType.Triangles, viewProj);
        PolygonOffset(false);
        _gl.Disable(EnableCap.StencilTest);
    }

    public void PolygonOffset(bool on, float factor = 1, float units = 1)
    {
        if (on) { _gl.Enable(EnableCap.PolygonOffsetFill); _gl.PolygonOffset(factor, units); }
        else _gl.Disable(EnableCap.PolygonOffsetFill);
    }

    public void Blend(bool on)
    {
        if (on) { _gl.Enable(EnableCap.Blend); _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
        else _gl.Disable(EnableCap.Blend);
    }

    public void DrawDynamic(List<float> data, PrimitiveType mode, Matrix4x4 viewProj)
    {
        if (data.Count == 0) return;
        var arr = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data);
        fixed (float* p = arr) Upload(p, arr.Length);
        Draw(mode, data.Count / 7, viewProj);
    }

    public void DrawDynamic(float[] data, PrimitiveType mode, Matrix4x4 viewProj)
    {
        fixed (float* p = data) Upload(p, data.Length);
        Draw(mode, data.Length / 7, viewProj);
    }

    void Upload(float* p, int n)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynVbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(n * 4), p, BufferUsageARB.StreamDraw);
    }

    void Draw(PrimitiveType mode, int count, Matrix4x4 viewProj)
    {
        _gl.UseProgram(_vcol);
        SetMat(_vcolVP, viewProj);
        _gl.BindVertexArray(_dynVao);
        _gl.DrawArrays(mode, 0, (uint)count);
        _gl.BindVertexArray(0);
    }

    public void Viewport(int x, int y, int w, int h) => _gl.Viewport(x, y, (uint)w, (uint)h);

    public void ClearDepth() { _gl.DepthMask(true); _gl.Clear(ClearBufferMask.DepthBufferBit); }

    void SetMat(int loc, Matrix4x4 m) => _gl.UniformMatrix4(loc, 1, false, (float*)&m);

    public void Dispose()
    {
        foreach (var p in new[] { _litPlain.P, _litCut.P, _flatPlain.P, _flatCut.P, _vcol }) _gl.DeleteProgram(p);
        _gl.DeleteVertexArray(_dynVao); _gl.DeleteBuffer(_dynVbo);
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _gl.DeleteRenderbuffer(_colorRb); _gl.DeleteRenderbuffer(_depthRb); }
    }
}

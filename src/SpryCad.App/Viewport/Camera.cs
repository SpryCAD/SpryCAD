using System.Numerics;
using SpryCad.Kernel;

namespace SpryCad.App.Viewport;

/// <summary>
/// Orbit camera (orthographic by default, like SolidWorks). All screen math is in device independent
/// pixels; Y grows downward on screen.
/// </summary>
public sealed class Camera
{
    /// <summary>Camera orientation: maps camera axes (X right, Y up, Z toward viewer) to world.</summary>
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Target = Vector3.Zero;
    /// <summary>Half of the visible height in world units.</summary>
    public float HalfHeight = 60;
    public bool Perspective;
    public float Fov = 30 * MathF.PI / 180;
    public float Width = 800, Height = 600;
    public float SceneRadius = 200;

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);
    public Vector3 Back => Vector3.Transform(Vector3.UnitZ, Rotation);

    float Distance => Perspective ? HalfHeight / MathF.Tan(Fov / 2) : SceneRadius * 4 + 1000;
    public Vector3 Eye => Target + Back * Distance;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Up);

    public Matrix4x4 Projection
    {
        get
        {
            var aspect = Width / Math.Max(1, Height);
            var d = Distance;
            var near = Math.Max(0.01f, d - SceneRadius * 4 - 10);
            var far = d + SceneRadius * 4 + 10;
            if (Perspective)
                return Matrix4x4.CreatePerspectiveFieldOfView(Fov, aspect, Math.Max(0.1f, d * 0.01f), far);
            return Matrix4x4.CreateOrthographic(HalfHeight * 2 * aspect, HalfHeight * 2, near, far);
        }
    }

    public Matrix4x4 ViewProj => Cached().ViewProj;

    // the view-projection and eye of the camera as it last was: picking projects every edge's points, and each
    // would rebuild both matrices (the fields are set directly: the state they were made for is compared)
    sealed record Derived((Quaternion, Vector3, float, bool, float, float, float, float) Key, Matrix4x4 ViewProj, Vector3 Eye, Vector3 Back);
    Derived? _derived;
    Derived Cached()
    {
        var key = (Rotation, Target, HalfHeight, Perspective, Fov, Width, Height, SceneRadius);
        var d = _derived;
        if (d != null && d.Key.Equals(key)) return d;
        return _derived = new Derived(key, View * Projection, Eye, Back);
    }

    /// <summary>World units per screen pixel at the target depth.</summary>
    public float PixelSize => HalfHeight * 2 / Math.Max(1, Height);

    /// <summary>Ray through a screen point (DIPs).</summary>
    public (Vec3 origin, Vec3 dir) Ray(double sx, double sy)
    {
        var nx = (float)(sx / Width * 2 - 1);
        var ny = (float)(1 - sy / Height * 2);
        var aspect = Width / Math.Max(1, Height);
        if (Perspective)
        {
            var t = MathF.Tan(Fov / 2);
            var dir = Vector3.Normalize(Right * (nx * t * aspect) + Up * (ny * t) - Back);
            return (V(Eye), V(dir));
        }
        var o = Target + Right * (nx * HalfHeight * aspect) + Up * (ny * HalfHeight) + Back * (Distance);
        return (V(o), V(-Back));
    }

    /// <summary>Projects a world point to screen DIPs; Z is view depth (larger = farther).</summary>
    public Vector3 Project(Vec3 p)
    {
        var v = Vector4.Transform(new Vector4((float)p.X, (float)p.Y, (float)p.Z, 1), ViewProj);
        if (MathF.Abs(v.W) < 1e-9f) return new Vector3(-1e6f, -1e6f, 0);
        var ndc = new Vector3(v.X / v.W, v.Y / v.W, v.Z / v.W);
        return new Vector3((ndc.X + 1) / 2 * Width, (1 - ndc.Y) / 2 * Height, ndc.Z);
    }

    /// <summary>Depth of a world point along the view direction.</summary>
    public double DepthOf(Vec3 p) { var d = Cached(); return -Vec3.Dot(p - V(d.Eye), V(d.Back)); }

    public static Vec3 V(Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector3 F(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    // ---- navigation ----

    public void Orbit(float dxPixels, float dyPixels, Vector3 pivot)
    {
        var speed = 0.008f;
        var qy = Quaternion.CreateFromAxisAngle(Up, -dxPixels * speed);
        var qx = Quaternion.CreateFromAxisAngle(Right, -dyPixels * speed);
        var q = Quaternion.Normalize(qy * qx);
        // rotate target around pivot, rotate orientation
        Target = pivot + Vector3.Transform(Target - pivot, q);
        Rotation = Quaternion.Normalize(q * Rotation);
    }

    public void Roll(float angle)
    {
        var q = Quaternion.CreateFromAxisAngle(Back, angle);
        Rotation = Quaternion.Normalize(q * Rotation);
    }

    public void Pan(float dxPixels, float dyPixels)
    {
        var s = PixelSize;
        Target += -Right * dxPixels * s + Up * dyPixels * s;
    }

    /// <summary>Zooms keeping the world point under the cursor fixed.</summary>
    public void ZoomAt(float factor, float sx, float sy)
    {
        var aspect = Width / Math.Max(1, Height);
        var nx = sx / Width * 2 - 1; var ny = 1 - sy / Height * 2;
        var before = Right * (nx * HalfHeight * aspect) + Up * (ny * HalfHeight);
        HalfHeight = Math.Clamp(HalfHeight * factor, 1e-3f, 1e6f);
        var after = Right * (nx * HalfHeight * aspect) + Up * (ny * HalfHeight);
        Target += before - after;
    }

    public void Fit(Box3 box)
    {
        if (box.IsEmpty) { Target = Vector3.Zero; HalfHeight = 60; return; }
        var c = box.Center; var r = (float)Math.Max(box.Diagonal / 2, 1);
        Target = F(c);
        var aspect = Width / Math.Max(1, Height);
        HalfHeight = r * 1.15f / Math.Min(1, aspect);
        SceneRadius = Math.Max(SceneRadius, r);
    }

    /// <summary>Orientation looking along -back with the given up vector.</summary>
    public static Quaternion LookRotation(Vector3 back, Vector3 up)
    {
        back = Vector3.Normalize(back);
        var right = Vector3.Normalize(Vector3.Cross(up, back));
        up = Vector3.Cross(back, right);
        var m = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            back.X, back.Y, back.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public static readonly Dictionary<string, Quaternion> StandardViews = new()
    {
        ["Front"] = LookRotation(Vector3.UnitZ, Vector3.UnitY),
        ["Back"] = LookRotation(-Vector3.UnitZ, Vector3.UnitY),
        ["Left"] = LookRotation(-Vector3.UnitX, Vector3.UnitY),
        ["Right"] = LookRotation(Vector3.UnitX, Vector3.UnitY),
        ["Top"] = LookRotation(Vector3.UnitY, -Vector3.UnitZ),
        ["Bottom"] = LookRotation(-Vector3.UnitY, Vector3.UnitZ),
        ["Isometric"] = LookRotation(new Vector3(1, 1, 1), Vector3.UnitY),
        ["Trimetric"] = LookRotation(new Vector3(0.6f, 0.45f, 1f), Vector3.UnitY),
        ["Dimetric"] = LookRotation(new Vector3(0.45f, 0.4f, 1f), Vector3.UnitY),
    };
}

namespace SpryCad.Kernel.Native;

/// <summary>
/// Draft (neutral plane): faces tilted to the pull direction by an angle, each about where it meets the neutral plane
/// (it keeps its place there), so the part narrows going along the pull direction and comes out of a mould that way.
/// A planar face becomes the plane the angle from the pull direction through that line; a cylinder along the pull
/// direction the cone with the same radius at the neutral plane (a boss narrows, a hole widens). The other faces keep
/// their surfaces; the edges and corners are made again where the faces now meet, so the topology must not change
/// (a draft that would make a face vanish fails). The faces keep their names: what refers to them still does.
/// </summary>
public static class NDraft
{
    /// <param name="draft">The faces to draft.</param>
    /// <param name="neutral">The neutral plane.</param>
    /// <param name="pull">The pull direction (unit); the part narrows going along it.</param>
    /// <param name="angle">The draft angle (radians, below 90°); negative: the part widens along the pull direction.</param>
    public static NBody Apply(NBody body, Func<NFace, bool> draft, Plane neutral, Vec3 pull, double angle)
    {
        if (Math.Abs(angle) >= Math.PI / 2 - 1e-9) throw new InvalidOperationException("draft: the angle must be below 90 degrees");
        return NShell.Rebuild(body,
            f => draft(f) ? Drafted(f, neutral, pull, angle) : f.Surface,
            (f, s) => draft(f) ? new FaceTag(f.Tag.Name, s.Kind == SurfaceKind.Plane ? f.Tag.Surface : s.Def) : f.Tag,
            "draft");
    }

    /// <summary>A drafted face's new surface.</summary>
    public static NSurface Drafted(NFace f, Plane neutral, Vec3 pull, double angle)
    {
        var s = f.Surface;
        double sense = f.Sense ? 1 : -1;
        switch (s.Kind)
        {
            case SurfaceKind.Plane:
            {
                // the outward normal turned towards the pull direction until the face is the angle from it
                var n = s.Plane!.Normal * sense;
                var across = n - pull * Vec3.Dot(n, pull);
                if (across.Length < 1e-6) throw new InvalidOperationException($"draft: face {f.Tag.Name} is parallel to the neutral plane");
                var n2 = (across.Normalized * Math.Cos(angle) + pull * Math.Sin(angle)).Normalized;
                // through the line where the face meets the neutral plane
                var hinge = PlanesMeet(s.Plane!, neutral)
                    ?? throw new InvalidOperationException($"draft: face {f.Tag.Name} does not meet the neutral plane");
                return NSurface.FromPlane(Plane.FromPointNormal(hinge, n2 * sense));
            }
            case SurfaceKind.Cylinder:
            {
                var d = s.Def;
                var along = Vec3.Dot(d.Axis, pull);
                if (Math.Abs(along) < 1 - 1e-9) throw new InvalidOperationException($"draft: round face {f.Tag.Name} is not along the pull direction");
                // its axis where it meets the neutral plane: the cone keeps the radius there
                var t = (neutral.D - Vec3.Dot(neutral.Normal, d.Origin)) / Vec3.Dot(neutral.Normal, d.Axis);
                var origin = d.Origin + d.Axis * t;
                // outward away from the axis (a boss): narrowing along the pull direction; towards it (a hole): widening
                var half = f.Sense ? -angle : angle;
                var refDir = d.RefDir - pull * Vec3.Dot(d.RefDir, pull);
                return new NSurface(d with { Kind = SurfaceKind.Cone, Origin = origin, Axis = pull, RefDir = refDir.Normalized, Param2 = half });
            }
            default:
                throw new InvalidOperationException($"draft: face {f.Tag.Name} is {s.Kind.ToString().ToLowerInvariant()}: only planar faces and round faces along the pull direction can be drafted yet");
        }
    }

    /// <summary>A point on the line where two planes meet (the one nearest the origin); null for parallel planes.</summary>
    static Vec3? PlanesMeet(Plane a, Plane b)
    {
        var c = Vec3.Dot(a.Normal, b.Normal);
        var det = 1 - c * c;
        if (det < 1e-12) return null;
        return (a.Normal * (a.D - b.D * c) + b.Normal * (b.D - a.D * c)) / det;
    }
}

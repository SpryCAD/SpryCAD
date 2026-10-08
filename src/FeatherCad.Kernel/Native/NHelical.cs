namespace FeatherCad.Kernel.Native;

/// <summary>
/// Helical sweep: profile regions carried by a screw motion (turning about an axis while advancing a lead per turn).
/// Each profile line or arc sweeps one screw face over all the turns, bounded by the segment at the start, the
/// helices its ends follow and the segment carried to the end; planar caps close the ends. The screw-motion
/// counterpart of <see cref="NBuild.Revolve"/>: threads, springs, worms.
/// </summary>
public static class NHelical
{
    /// <summary>
    /// Sweeps the regions (in <paramref name="frame"/>'s plane) about the axis through <paramref name="axisOrigin"/>
    /// along <paramref name="axisDir"/>, turning <paramref name="angle"/> radians (positive) while advancing
    /// <paramref name="lead"/> per turn along the axis (negative: a left-hand helix, advancing along the axis all the same). Faces: prefix.H.&lt;segment&gt;,
    /// caps prefix.Start and prefix.End.
    /// <para>
    /// A run-out: the end runs on in a straight line, <paramref name="exitLength"/> long, or until the whole profile
    /// lies outside <paramref name="exitRadius"/> from the axis (a thread running out of the side of a shaft). The
    /// profile's segments sweep faces prefix.X.&lt;segment&gt; (planes; cylinders for arcs) and the end cap moves out
    /// with them. The line follows the helix's tangent at the profile's centre; a profile with arcs leaves along the
    /// end cap's normal instead (an arc carried along a slanted line would sweep an elliptic cylinder), which differs
    /// from the tangent by the helix angle.
    /// </para>
    /// </summary>
    public static NBody Sweep(IEnumerable<ProfileRegion> regions, Frame frame, Vec3 axisOrigin, Vec3 axisDir, double lead, double angle, string prefix,
        double exitRadius = 0, double exitLength = 0)
    {
        if (!(angle > 0)) throw new ArgumentException("the sweep needs a positive number of turns");
        var regs = regions.Select(r => r.Normalized()).ToList();
        var n = frame.Normal;
        var a = axisDir.Normalized;
        var loops = regs.SelectMany(r => r.AllLoops()).Select(l => (loop: l, segs: NBuild.Segments(l))).Where(x => x.segs.Count > 0).ToList();
        if (loops.Count == 0) throw new ArgumentException("the profile is empty");
        // the profile's distance from the axis (it must not touch it) and its height along it
        double rMin = double.MaxValue, hMin = double.MaxValue, hMax = double.MinValue;
        Vec3 refDir = Vec3.Zero; double rRef = -1;
        foreach (var (_, segs) in loops)
            foreach (var s in segs)
                foreach (var q in s.Arc == null ? new[] { s.A } : ArcPoints(s))
                {
                    var w = frame.ToWorld(q); var d = w - axisOrigin; var h = Vec3.Dot(d, a);
                    var radial = d - a * h; var r = radial.Length;
                    rMin = Math.Min(rMin, r); hMin = Math.Min(hMin, h); hMax = Math.Max(hMax, h);
                    if (r > rRef) { rRef = r; refDir = radial; }
                }
        var scale = Math.Max(1, rRef);
        if (rMin < 1e-6 * scale) throw new ArgumentException("the profile touches the axis of the helix");
        // a profile in a plane through the axis taller than the lead would run into its next turn
        if (Math.Abs(Vec3.Dot(n, a)) < 1e-9 && hMax - hMin >= Math.Abs(lead) - 1e-9 * scale && angle > 2 * Math.PI - 1e-9)
            throw new ArgumentException("the profile is taller than the pitch: neighbouring turns would overlap");
        // left hand: turning the other way (the mirrored frame) while advancing the same way (a plain negative lead
        // would turn the same way and advance backwards: a left-hand helix too, but behind the profile)
        var motion = ScrewMotion.About(axisOrigin, a, Math.Abs(lead), refDir);
        if (lead < 0) motion = motion with { Y = -motion.Y };
        var end = motion.At(angle);
        // the start cap faces against the motion, the end cap with it
        var centre = frame.ToWorld(loops[0].segs[0].A);
        bool forward = Vec3.Dot(motion.Velocity(0, centre), n) > 0;
        var outStart = forward ? -n : n;

        var body = new NBody();
        var startFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(frame.Origin, outStart)), true, new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef));
        var endN = end.Vector(-outStart).Normalized;
        // the run-out's direction and length (the motion moves nothing radially, so along the tangent the profile's
        // point nearest the axis is the last one out)
        Vec3 exitDir = Vec3.Zero; double exitLen = 0;
        if (exitRadius > 0 || exitLength > 0)
        {
            bool arcs = loops.Any(l => l.segs.Any(s => s.Arc != null));
            var c0 = Vec3.Zero; int nc = 0;
            foreach (var (_, segs) in loops) foreach (var s in segs) { c0 += frame.ToWorld(s.A); nc++; }
            c0 /= nc;
            exitDir = arcs ? endN : motion.Velocity(angle, c0).Normalized;
            if (exitLength > 0) exitLen = exitLength;
            else
            {
                var dp = exitDir - a * Vec3.Dot(exitDir, a);
                foreach (var (_, segs) in loops)
                    foreach (var s in segs)
                        foreach (var q2 in s.Arc == null ? new[] { s.A } : ArcPoints(s))
                        {
                            var q = end.Point(frame.ToWorld(q2)) - axisOrigin; var rq = q - a * Vec3.Dot(q, a);
                            double qa = dp.LengthSq, qb = 2 * Vec3.Dot(rq, dp), qc = rq.LengthSq - exitRadius * exitRadius;
                            if (qc >= 0) continue;
                            if (qa < 1e-18) throw new ArgumentException("the run-out runs along the axis: it never leaves the radius");
                            exitLen = Math.Max(exitLen, (-qb + Math.Sqrt(qb * qb - 4 * qa * qc)) / (2 * qa));
                        }
                // already clear: a short run-out all the same (the cap would otherwise sit on the part's surface)
                exitLen = Math.Max(exitLen, 0.05 * scale);
            }
        }
        var endAt = end.Point(frame.Origin) + exitDir * exitLen;
        var endFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(endAt, endN)), true, new FaceTag(prefix + ".End", SurfaceDef.PlaneDef));
        var tags = new Dictionary<string, FaceTag>();
        foreach (var (_, segs) in loops)
        {
            int m = segs.Count;
            var v0 = new NVertex[m]; var v1 = new NVertex[m];
            for (int i = 0; i < m; i++) { var w = frame.ToWorld(segs[i].A); v0[i] = new NVertex(w); v1[i] = new NVertex(end.Point(w)); }
            // the helices the profile's vertices follow
            var helix = new NEdge[m];
            for (int i = 0; i < m; i++) helix[i] = new NEdge(new NHelix(motion, v0[i].P), 0, angle, v0[i], v1[i]);
            // the profile's segments at the start and carried to the end
            var bottom = new (NEdge e, bool fwd)[m]; var top = new (NEdge e, bool fwd)[m];
            var gens = new NCurve[m];
            for (int i = 0; i < m; i++)
            {
                int j = (i + 1) % m;
                var s = segs[i];
                if (s.Arc == null)
                {
                    var d = v0[j].P - v0[i].P;
                    var line = new NLine(v0[i].P, d);
                    gens[i] = line;
                    bottom[i] = (new NEdge(line, 0, d.Length, v0[i], v0[j]), true);
                    top[i] = (new NEdge(line.Transformed(end), 0, d.Length, v1[i], v1[j]), true);
                }
                else
                {
                    var circle = new NCircle(frame.ToWorld(s.Arc.Center), n, frame.XDir, s.Arc.Radius);
                    gens[i] = circle;
                    bottom[i] = NBuild.ArcEdge(circle, v0[i], v0[j], s.Ccw);
                    var be = bottom[i].e;
                    // the same arc moved: the same parameters on the moved circle
                    var topEdge = new NEdge(circle.Transformed(end), be.T0, be.T1, ReferenceEquals(be.V0, v0[i]) ? v1[i] : v1[j], ReferenceEquals(be.V0, v0[i]) ? v1[j] : v1[i]);
                    top[i] = (topEdge, bottom[i].fwd);
                }
            }
            for (int i = 0; i < m; i++)
            {
                int j = (i + 1) % m;
                var s = segs[i];
                var be = bottom[i].e;
                // the generator's parameters at the segment's start (a) and end (b)
                double va = bottom[i].fwd ? be.T0 : be.T1, vb = bottom[i].fwd ? be.T1 : be.T0;
                var sc = new ScrewSurface(motion, gens[i], Math.Min(va, vb), Math.Max(va, vb), 0, angle);
                var surf = NSurface.ScrewOf(sc);
                var name = prefix + ".H." + s.Name;
                if (!tags.TryGetValue(name, out var tag)) tags[name] = tag = new FaceTag(name, surf.Def);
                // outward at the start: across the segment, away from the region (interior on the segment's left)
                var vm = 0.5 * (va + vb);
                var tangent = gens[i].Deriv(vm) * (vb > va ? 1 : -1);
                var outward = Vec3.Cross(tangent, n);
                bool sense = Vec3.Dot(sc.NormalAt(0, vm), outward) > 0;
                var face = new NFace(surf, sense, tag);
                // bottom (a→b), b's helix, top (b→a), a's helix back: in (u, v) counter-clockwise when va > vb
                var loop = new NLoop();
                loop.Coedges.Add(new NCoedge(bottom[i].e, bottom[i].fwd));
                loop.Coedges.Add(new NCoedge(helix[j], true));
                loop.Coedges.Add(new NCoedge(top[i].e, !top[i].fwd));
                loop.Coedges.Add(new NCoedge(helix[i], false));
                bool ccwUV = va > vb;
                // the loop runs counter-clockwise about the outward normal: in (u, v) that is counter-clockwise when the
                // face's sense is the natural one
                if (ccwUV != sense) Reverse(loop);
                face.Loops.Add(loop);
                body.Faces.Add(face);
            }
            if (exitLen > 0)
            {
                // the run-out: each segment of the end profile carried straight out, a plane (a cylinder for an arc)
                // between it and its copy
                var shift = Transform.Translation(exitDir * exitLen);
                var v2 = new NVertex[m];
                for (int i = 0; i < m; i++) v2[i] = new NVertex(v1[i].P + exitDir * exitLen);
                var side = new NEdge[m];
                for (int i = 0; i < m; i++) side[i] = new NEdge(new NLine(v1[i].P, exitDir), 0, exitLen, v1[i], v2[i]);
                var nEnd = end.Vector(n).Normalized;
                var far = new (NEdge e, bool fwd)[m];
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    var te = top[i].e;
                    far[i] = (new NEdge(te.Curve.Transformed(shift), te.T0, te.T1, ReferenceEquals(te.V0, v1[i]) ? v2[i] : v2[j], ReferenceEquals(te.V0, v1[i]) ? v2[j] : v2[i]), top[i].fwd);
                    var s = segs[i];
                    var name = prefix + ".X." + s.Name;
                    NSurface surf; bool sense; Vec3? anchor = null;
                    if (s.Arc == null)
                    {
                        // outward: across the line, away from the region (interior on the line's left about the moved normal)
                        var d = v1[j].P - v1[i].P;
                        var pn = Vec3.Cross(d, exitDir).Normalized;
                        if (Vec3.Dot(pn, Vec3.Cross(d, nEnd)) < 0) pn = -pn;
                        surf = NSurface.FromPlane(Plane.FromPointNormal(v1[i].P, pn));
                        sense = true;
                    }
                    else
                    {
                        // exitDir is the cap's normal here: a right cylinder about the moved arc's centre
                        var circle = (NCircle)te.Curve;
                        var def = new SurfaceDef
                        {
                            Kind = SurfaceKind.Cylinder, Origin = circle.C, Axis = exitDir, RefDir = end.Vector(frame.XDir),
                            Radius = s.Arc.Radius, Segments = s.Arc.Segments,
                        };
                        surf = new NSurface(def);
                        sense = s.Ccw; // convex arcs face away from the axis
                        anchor = te.Curve.Eval(0.5 * (te.T0 + te.T1)) + exitDir * (exitLen / 2);
                    }
                    if (!tags.TryGetValue(name, out var tag)) tags[name] = tag = new FaceTag(name, s.Arc == null ? SurfaceDef.PlaneDef : surf.Def);
                    var face = new NFace(surf, sense, tag, anchor);
                    // as an extrusion's sides: the segment (a → b), b's line out, the copy back, a's line in; counter-
                    // clockwise about the outward normal when the run-out leaves along the moved sketch normal
                    var loop = new NLoop();
                    loop.Coedges.Add(new NCoedge(top[i].e, top[i].fwd));
                    loop.Coedges.Add(new NCoedge(side[j], true));
                    loop.Coedges.Add(new NCoedge(far[i].e, !far[i].fwd));
                    loop.Coedges.Add(new NCoedge(side[i], false));
                    if (Vec3.Dot(exitDir, nEnd) < 0) Reverse(loop);
                    face.Loops.Add(loop);
                    body.Faces.Add(face);
                }
                top = far;
            }
            // caps: the profile loops (counter-clockwise about the sketch normal for outer loops) about each cap's outward normal
            var sl = new NLoop(); var el = new NLoop();
            for (int i = 0; i < m; i++) { sl.Coedges.Add(new NCoedge(bottom[i].e, bottom[i].fwd)); el.Coedges.Add(new NCoedge(top[i].e, top[i].fwd)); }
            if (Vec3.Dot(outStart, n) < 0) Reverse(sl); else Reverse(el);
            startFace.Loops.Add(sl);
            endFace.Loops.Add(el);
        }
        body.Faces.Add(startFace);
        body.Faces.Add(endFace);
        body.Link();
        return body;
    }

    static void Reverse(NLoop l)
    {
        var rev = l.Coedges.Select(c => new NCoedge(c.Edge, !c.Forward)).Reverse().ToList();
        l.Coedges.Clear(); l.Coedges.AddRange(rev);
    }

    // points along an arc segment (for the profile's extent)
    static IEnumerable<Vec2> ArcPoints(NBuild.Seg s)
    {
        var c = s.Arc!.Center; var r = s.Arc.Radius;
        double a0 = (s.A - c).Angle, a1 = (s.B - c).Angle, d = a1 - a0;
        if (s.Ccw) { while (d <= 0) d += 2 * Math.PI; } else { while (d >= 0) d -= 2 * Math.PI; }
        for (int k = 0; k <= 32; k++) yield return c + Vec2.FromAngle(a0 + d * k / 32, r);
    }
}

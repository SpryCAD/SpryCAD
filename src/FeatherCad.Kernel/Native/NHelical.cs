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
    /// <paramref name="lead"/> per turn along the axis (negative: a left-hand helix). Faces: prefix.H.&lt;segment&gt;,
    /// caps prefix.Start and prefix.End.
    /// </summary>
    public static NBody Sweep(IEnumerable<ProfileRegion> regions, Frame frame, Vec3 axisOrigin, Vec3 axisDir, double lead, double angle, string prefix)
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
        var motion = ScrewMotion.About(axisOrigin, a, lead, refDir);
        var end = motion.At(angle);
        // the start cap faces against the motion, the end cap with it
        var centre = frame.ToWorld(loops[0].segs[0].A);
        bool forward = Vec3.Dot(motion.Velocity(0, centre), n) > 0;
        var outStart = forward ? -n : n;

        var body = new NBody();
        var startFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(frame.Origin, outStart)), true, new FaceTag(prefix + ".Start", SurfaceDef.PlaneDef));
        var endN = end.Vector(-outStart).Normalized;
        var endFace = new NFace(NSurface.FromPlane(Plane.FromPointNormal(end.Point(frame.Origin), endN)), true, new FaceTag(prefix + ".End", SurfaceDef.PlaneDef));
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

using System.Diagnostics;
using System.Text.Json;
using FeatherCad.Core.Model;
using FeatherCad.Core.Sketching;
using FeatherCad.Kernel;

namespace FeatherCad.Core.Regen;

/// <summary>
/// Rebuilds the feature history. Results are cached per feature, keyed by a hash of the feature
/// definition and everything before it, so editing a late feature only rebuilds from that point.
/// </summary>
public sealed class Regenerator
{
    sealed record CacheEntry(ulong Key, ModelState State, FeatureStatus Status);
    readonly List<CacheEntry> _cache = new();

    public RegenResult Rebuild(CadDocument doc, int? upTo = null)
    {
        var sw = Stopwatch.StartNew();
        var (eqValues, eqErrors) = EvaluateEquations(doc);
        Func<string, double?> lookup = name => eqValues.TryGetValue(name, out var v) ? v : null;

        ulong key = Hash(0xcbf29ce484222325UL, JsonSerializer.Serialize(doc.Equations, Serialization.Options));
        var state = ModelState.Initial();
        var states = new List<ModelState>();
        var status = new Dictionary<string, FeatureStatus>();
        int count = Math.Min(doc.ActiveCount, upTo ?? int.MaxValue);
        int rebuilt = 0;
        for (int i = 0; i < count; i++)
        {
            var f = doc.Features[i];
            key = Hash(key, JsonSerializer.Serialize(f, typeof(Feature), Serialization.Options));
            if (i < _cache.Count && _cache[i].Key == key)
            {
                state = _cache[i].State;
                status[f.Id] = _cache[i].Status;
                states.Add(state);
                continue;
            }
            if (_cache.Count > i) _cache.RemoveRange(i, _cache.Count - i);
            var st = new FeatureStatus();
            var fsw = Stopwatch.StartNew();
            if (!f.Suppressed)
            {
                try
                {
                    var ctx = new Ctx(doc, state.Copy(), lookup, st, f);
                    using (Prof.Time("feature." + f.GetType().Name)) ctx.Execute();
                    state = ctx.State;
                }
                catch (FeatureException ex) { st.Error = ex.Message; }
                catch (Expr.ExprException ex) { st.Error = ex.Message; }
                catch (Exception ex) { st.Error = "Rebuild error: " + ex.Message; }
            }
            st.Milliseconds = fsw.Elapsed.TotalMilliseconds;
            rebuilt++;
            _cache.Add(new CacheEntry(key, state, st));
            status[f.Id] = st;
            states.Add(state);
        }
        return new RegenResult
        {
            State = state,
            Status = status,
            States = states,
            Milliseconds = sw.Elapsed.TotalMilliseconds,
            Rebuilt = rebuilt,
            EquationValues = eqValues,
            EquationErrors = eqErrors,
        };
    }

    public void Invalidate() => _cache.Clear();

    static ulong Hash(ulong h, string s)
    {
        foreach (var c in s) { h ^= c; h *= 0x100000001b3UL; }
        h ^= 0xff; h *= 0x100000001b3UL;
        return h;
    }

    public static (Dictionary<string, double> values, Dictionary<string, string> errors) EvaluateEquations(CadDocument doc)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>();
        var pending = doc.Equations.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
        for (int pass = 0; pass < pending.Count + 1 && pending.Count > 0; pass++)
        {
            var next = new List<Equation>();
            foreach (var e in pending)
            {
                if (Expr.TryEval(e.Expr, n => values.TryGetValue(n, out var v) ? v : null, out var val, out var err))
                    values[e.Name.Trim()] = val;
                else { next.Add(e); errors[e.Name] = err ?? "error"; }
            }
            foreach (var e in pending.Except(next)) errors.Remove(e.Name);
            if (next.Count == pending.Count) break;
            pending = next;
        }
        return (values, errors);
    }

    // ------------------------------------------------------------------------------------------

    sealed class Ctx(CadDocument doc, ModelState state, Func<string, double?> lookup, FeatureStatus status, Feature f)
    {
        public ModelState State = state;

        double Num(string? expr, string what)
        {
            if (!Expr.TryEval(expr, lookup, out var v, out var err)) throw new FeatureException($"{what}: {err}");
            return v;
        }

        double Positive(string? expr, string what)
        {
            var v = Num(expr, what);
            if (v <= 0) throw new FeatureException($"{what} must be greater than zero.");
            return v;
        }

        public void Execute()
        {
            switch (f)
            {
                case SketchFeature s: DoSketch(s); break;
                case ExtrudeFeature e: DoExtrude(e); break;
                case RevolveFeature r: DoRevolve(r); break;
                case FilletFeature fi: DoBlend(fi.Edges, Positive(fi.Radius, "Radius"), true, fi.TangentPropagation, fi.Overflow); break;
                case ChamferFeature ch: { var cs = ChamferSizeOf(ch); DoBlend(ch.Edges, cs.D1, false, ch.TangentPropagation, ch.Overflow, cs); break; }
                case HoleFeature h: DoHole(h); break;
                case ShellFeature sh: DoShell(sh); break;
                case LinearPatternFeature lp: DoLinearPattern(lp); break;
                case CircularPatternFeature cp: DoCircularPattern(cp); break;
                case MirrorFeature m: DoMirror(m); break;
                case PlaneFeature p: DoPlane(p); break;
                case CombineFeature c: DoCombine(c); break;
                case ImportFeature im: DoImport(im); break;
            }
        }

        // ---- imported solids ----

        static readonly Lru<(string hash, string prefix, double scale), StepImportResult> ImportCache = new(4);

        void DoImport(ImportFeature im)
        {
            if (!doc.Files.TryGetValue(im.File, out var data))
                throw new FeatureException($"The imported file \"{im.File}\" is not stored in this part.");
            var hash = DocumentContainer.Hash(data);
            var scale = doc.Units == UnitSystem.IPS ? 1 / 25.4 : 1;
            var key = (hash, f.Id + ".", scale);
            if (!ImportCache.TryGet(key, out var r))
            {
                try { r = StepImport.Read(data, f.Id + ".", scale); }
                catch (InvalidDataException ex) { throw new FeatureException(ex.Message); }
                ImportCache.Add(key, r);
            }
            status.Warnings.AddRange(r.Warnings);
            var bodies = new List<Body>(State.Bodies);
            for (int i = 0; i < r.Solids.Count; i++)
                bodies.Add(new Body { Id = i == 0 ? f.Id : $"{f.Id}-{i + 1}", Solid = Kernel.Native.NTess.ToSolid(r.Solids[i].Body.Clone()) });
            State = State.With(bodies);
        }

        // ---- sketches ----

        void DoSketch(SketchFeature s)
        {
            var frame = Resolver.ResolvePlane(s.Plane, State);
            var sk = s.Sketch.Clone();
            SketchReferences.Update(sk, frame, State, status.Warnings);
            foreach (var c in sk.Constraints)
            {
                if (!c.IsDimension || c.Driven || string.IsNullOrWhiteSpace(c.Expr)) continue;
                c.Value = Num(c.Expr, "Dimension");
            }
            var res = SketchSolver.Solve(sk);
            if (res.Status == SketchStatus.OverDefined) status.Warnings.Add("Sketch is over defined or cannot be solved.");
            var regions = ProfileExtractor.Extract(sk, out var warns);
            status.Warnings.AddRange(warns);
            State.Sketches[s.Id] = new SketchResult { Frame = frame, Sketch = sk, Regions = regions, Solve = res };
            State.Planes[s.Id] = frame;
        }

        SketchResult GetSketch(string id)
        {
            if (!State.Sketches.TryGetValue(id, out var r))
                throw new FeatureException("The sketch for this feature is missing or failed.");
            return r;
        }

        // ---- extrude / revolve ----

        double BigDepth(Frame frame)
        {
            var b = State.Bounds;
            if (b.IsEmpty) return 1000;
            return (b.Diagonal + (b.Center - frame.Origin).Length) * 2 + 10;
        }

        void DoExtrude(ExtrudeFeature e)
        {
            var sk = GetSketch(e.SketchId);
            if (sk.Regions.Count == 0) throw new FeatureException("The sketch has no closed profile.");
            double dir = e.Reverse ? -1 : 1;
            double z0, z1;
            switch (e.End)
            {
                case EndCondition.Blind: z0 = 0; z1 = Positive(e.Depth, "Depth") * dir; break;
                case EndCondition.MidPlane: { var d = Positive(e.Depth, "Depth"); z0 = -d / 2; z1 = d / 2; break; }
                case EndCondition.ThroughAll: z0 = 0; z1 = BigDepth(sk.Frame) * dir; break;
                default: { var d = BigDepth(sk.Frame); z0 = -d; z1 = d; break; }
            }
            if (!string.IsNullOrWhiteSpace(e.Depth2) && e.End == EndCondition.Blind)
            {
                var d2 = Num(e.Depth2, "Depth (direction 2)");
                if (d2 > 0) z0 = -d2 * dir;
            }
            var tool = Builders.Extrude(sk.Regions, sk.Frame, Math.Min(z0, z1), Math.Max(z0, z1), f.Id);
            ApplyTool(tool, e.IsCut, e.Merge);
            State.Tools[f.Id] = new ToolInfo(tool, e.IsCut, e.Merge);
        }

        void DoRevolve(RevolveFeature r)
        {
            var sk = GetSketch(r.SketchId);
            SkLine? axis = r.AxisEntity != 0 ? sk.Sketch.Entity(r.AxisEntity) as SkLine : null;
            axis ??= sk.Sketch.Entities.OfType<SkLine>().FirstOrDefault(l => l.Construction);
            if (axis == null) throw new FeatureException("Select an axis of revolution (a centerline).");
            var regions = axis.Construction ? sk.Regions : ProfileExtractor.Extract(sk.Sketch, out _);
            if (regions.Count == 0) throw new FeatureException("The sketch has no closed profile.");
            var a = sk.Sketch.P(axis.P1); var b = sk.Sketch.P(axis.P2);
            var ang = Positive(r.Angle, "Angle") * Math.PI / 180;
            var dir = (b - a).Normalized;
            if (r.Reverse) dir = -dir;
            // profile must stay on one side of the axis
            double minS = double.MaxValue, maxS = double.MinValue;
            foreach (var reg in regions) foreach (var l in reg.AllLoops()) foreach (var v in l.Verts)
                    { var s = Vec2.Cross(dir, v.P - a); minS = Math.Min(minS, s); maxS = Math.Max(maxS, s); }
            if (minS < -1e-6 && maxS > 1e-6) throw new FeatureException("The profile crosses the axis of revolution.");
            var tool = Builders.Revolve(regions, sk.Frame, a, dir, Math.Min(ang, 2 * Math.PI), f.Id);
            if (tool.Native == null && tool.Volume() < 0) tool = tool.Flipped(); // native revolves are oriented by construction
            ApplyTool(tool, r.IsCut, r.Merge);
            State.Tools[f.Id] = new ToolInfo(tool, r.IsCut, r.Merge);
        }

        /// <summary>Whether a boolean changed the body (native: topology counts, so no tessellation is needed).</summary>
        static bool Changed(Solid before, Solid after)
        {
            if (before.Native != null && after.Native != null)
                return before.Native.Faces.Count != after.Native.Faces.Count
                    || before.Native.Edges.Count() != after.Native.Edges.Count()
                    || !before.Bounds.Equals(after.Bounds);
            return after.Faces.Count != before.Faces.Count || after.Volume() < before.Volume() - 1e-9;
        }

        /// <summary>Adds (fuses) or removes (cuts) a tool solid from the bodies.</summary>
        void ApplyTool(Solid tool, bool cut, bool merge)
        {
            if (tool.IsEmpty) throw new FeatureException("The feature produced no geometry.");
            var bodies = new List<Body>();
            if (cut)
            {
                bool any = false;
                foreach (var b in State.Bodies)
                {
                    if (!b.Solid.Bounds.Overlaps(tool.Bounds, 1e-6)) { bodies.Add(b); continue; }
                    var r = Csg.Subtract(b.Solid, tool);
                    if (Changed(b.Solid, r)) any = true;
                    if (!r.IsEmpty) bodies.Add(new Body { Id = b.Id, Solid = r });
                }
                if (State.Bodies.Count == 0) throw new FeatureException("There is no body to cut.");
                if (!any) status.Warnings.Add("The cut does not intersect the model.");
            }
            else if (merge)
            {
                Solid acc = tool; string? id = null;
                foreach (var b in State.Bodies)
                {
                    if (b.Solid.Bounds.Overlaps(tool.Bounds, 1e-6))
                    {
                        acc = Csg.Union(b.Solid, acc);
                        id ??= b.Id;
                    }
                    else bodies.Add(b);
                }
                var nb = new Body { Id = id ?? f.Id, Solid = acc };
                // keep original body order
                var idx = State.Bodies.FindIndex(b => b.Id == nb.Id);
                bodies.Insert(idx >= 0 ? Math.Min(idx, bodies.Count) : bodies.Count, nb);
            }
            else
            {
                bodies.AddRange(State.Bodies);
                bodies.Add(new Body { Id = f.Id, Solid = tool });
            }
            State = State.With(bodies);
        }

        // ---- fillet / chamfer ----

        ChamferSize ChamferSizeOf(ChamferFeature ch)
        {
            switch (ch.Type)
            {
                case ChamferType.AngleDistance:
                    {
                        var d = Positive(ch.Distance, "Distance");
                        var a = Num(ch.Angle, "Angle");
                        if (a <= 0 || a >= 180) throw new FeatureException("Angle must be between 0 and 180 degrees.");
                        return new ChamferSize(d, 0, a * Math.PI / 180, ch.Flip);
                    }
                case ChamferType.DistanceDistance:
                    return new ChamferSize(Positive(ch.Distance, "Distance 1"), Positive(ch.Distance2, "Distance 2"), 0, ch.Flip);
                default:
                    return ChamferSize.Equal(Positive(ch.Distance, "Distance"));
            }
        }

        void DoBlend(List<EdgeRef> refs, double size, bool fillet, bool propagate, bool overflow = true, ChamferSize? chamfer = null)
        {
            if (refs.Count == 0) throw new FeatureException("No edges selected.");
            if (TryNativeBlend(refs, size, fillet, propagate, overflow, chamfer)) return;
            var byBody = new Dictionary<string, List<TopoEdge>>();
            foreach (var r in refs)
            {
                try
                {
                    var (b, e) = Resolver.ResolveEdge(r, State);
                    if (!byBody.TryGetValue(b.Id, out var l)) byBody[b.Id] = l = new List<TopoEdge>();
                    if (!l.Contains(e)) l.Add(e);
                }
                catch (FeatureException ex) { status.Warnings.Add(ex.Message); }
            }
            if (byBody.Count == 0) throw new FeatureException("None of the selected edges could be found.");
            if (propagate)
                foreach (var (bid, list) in byBody)
                {
                    var topo = State.FindBody(bid)!.Solid.Topology;
                    foreach (var e in TangentChain(topo, list)) if (!list.Contains(e)) list.Add(e);
                }
            var bodies = new List<Body>();
            var errors = new List<string>();
            foreach (var b in State.Bodies)
            {
                if (!byBody.TryGetValue(b.Id, out var edges)) { bodies.Add(b); continue; }
                var r = EdgeBlend.Apply(b.Solid, edges, size, fillet, f.Id, errors, chamfer);
                bodies.Add(new Body { Id = b.Id, Solid = r });
            }
            status.Warnings.AddRange(errors);
            if (errors.Count > 0 && errors.Count >= refs.Count) throw new FeatureException(errors[0]);
            State = State.With(bodies);
        }

        /// <summary>
        /// Fillet/chamfer on native bodies: edges resolved and tangent-chained on the exact B-rep, tools built
        /// from exact geometry. False (state untouched) when a body is not native or an edge is not found.
        /// </summary>
        bool TryNativeBlend(List<EdgeRef> refs, double size, bool fillet, bool propagate, bool overflow, ChamferSize? chamfer)
        {
            if (!Csg.UseNative || State.Bodies.Any(b => b.Solid.Native == null)) return false;
            var byBody = new Dictionary<string, List<Kernel.Native.NEdge>>();
            foreach (var r in refs)
            {
                var hit = Resolver.ResolveNativeEdge(r, State);
                if (hit == null) return false;
                var (b, e) = hit.Value;
                if (!byBody.TryGetValue(b.Id, out var l)) byBody[b.Id] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
            var bodies = new List<Body>();
            var errors = new List<string>();
            var applied = new List<(Solid tool, bool cut)>();
            foreach (var b in State.Bodies)
            {
                if (!byBody.TryGetValue(b.Id, out var seeds)) { bodies.Add(b); continue; }
                var edges = NativeBlend.Expand(b.Solid.Native!, seeds, propagate);
                var r = NativeBlend.Apply(b.Solid, edges, size, fillet, f.Id, errors, overflow, status.ProblemEdges, applied, chamfer);
                bodies.Add(new Body { Id = b.Id, Solid = r });
            }
            // a blend on one body with tools of one kind can be patterned/mirrored like a cut or boss (its copies
            // blend the copies of the geometry it was made on)
            if (errors.Count == 0 && byBody.Count == 1 && applied.Count > 0 && applied.All(t => t.cut == applied[0].cut))
            {
                var tools = applied.Select(t => t.tool).ToList();
                State.Tools[f.Id] = new ToolInfo(() => tools.Count == 1 ? tools[0] : Csg.UnionAll(tools), applied[0].cut, true);
            }
            // all or nothing (as SolidWorks): a fillet that cannot be made on every edge fails as a whole,
            // naming the edges (highlighted in the viewport) instead of silently skipping some
            if (errors.Count > 0)
                throw new FeatureException(errors.Count == 1 ? errors[0]
                    : $"{errors.Count} edges cannot be {(fillet ? "filleted" : "chamfered")}: " + string.Join(" | ", errors));
            State = State.With(bodies);
            return true;
        }

        /// <summary>Edges connected to the seeds through smooth (tangent) vertices.</summary>
        static IEnumerable<TopoEdge> TangentChain(Topology topo, List<TopoEdge> seeds)
        {
            const double cosTol = 0.97; // ~14°: facet chords at arc ends deviate from the true tangent
            static (Vec3 p, Vec3 dir) EndOf(TopoEdge e, bool atStart) => atStart
                ? (e.Start, -(e.Segs[0].B - e.Segs[0].A).Normalized)
                : (e.End, (e.Segs[^1].B - e.Segs[^1].A).Normalized);
            var result = new HashSet<TopoEdge>(seeds);
            var queue = new Queue<TopoEdge>(seeds);
            while (queue.Count > 0)
            {
                var e = queue.Dequeue();
                if (e.Closed) continue;
                foreach (var atStart in new[] { true, false })
                {
                    var (p, dirOut) = EndOf(e, atStart);
                    foreach (var o in topo.Edges)
                    {
                        if (result.Contains(o) || o.Closed) continue;
                        foreach (var oStart in new[] { true, false })
                        {
                            var (q, oDirOut) = EndOf(o, oStart);
                            // continuing through the vertex: the other edge leaves where this one arrives
                            if (Vec3.Distance(p, q) < 1e-6 && Vec3.Dot(dirOut, -oDirOut) > cosTol)
                            {
                                result.Add(o); queue.Enqueue(o);
                                break;
                            }
                        }
                    }
                }
            }
            return result;
        }

        // ---- hole wizard ----

        void DoHole(HoleFeature h)
        {
            var sk = GetSketch(h.SketchId);
            var pts = sk.Sketch.Entities.OfType<SkPointEntity>().Where(p => !p.Construction).Select(p => sk.Sketch.P(p.P)).ToList();
            if (pts.Count == 0) throw new FeatureException("Place at least one hole position point.");
            var d = Positive(h.Diameter, "Diameter"); var r = d / 2;
            double depth = h.ThroughAll ? BigDepth(sk.Frame) : Positive(h.Depth, "Depth");
            const double lift = 1e-3; // start slightly above the face
            var prof = new ProfileLoop();
            void V(double x, double y, string n) => prof.Verts.Add(new ProfileVertex(new Vec2(x, y), n));
            double topR = r;
            switch (h.Type)
            {
                case HoleType.Counterbore:
                    {
                        var cbr = Positive(h.CbDiameter, "Counterbore diameter") / 2;
                        var cbd = Positive(h.CbDepth, "Counterbore depth");
                        if (cbr <= r) throw new FeatureException("Counterbore diameter must be larger than the hole.");
                        V(0, -lift, "Top"); V(cbr, -lift, "CB"); V(cbr, cbd, "CBF"); V(r, cbd, "Hole");
                        topR = cbr;
                        break;
                    }
                case HoleType.Countersink:
                    {
                        var csr = Positive(h.CsDiameter, "Countersink diameter") / 2;
                        var ang = Positive(h.CsAngle, "Countersink angle") * Math.PI / 180;
                        if (csr <= r) throw new FeatureException("Countersink diameter must be larger than the hole.");
                        var csDepth = (csr - r) / Math.Tan(ang / 2);
                        var liftR = csr + lift * Math.Tan(ang / 2);
                        V(0, -lift, "Top"); V(liftR, -lift, "CS"); V(r, csDepth, "Hole");
                        topR = csr;
                        break;
                    }
                default:
                    V(0, -lift, "Top"); V(r, -lift, "Hole");
                    break;
            }
            // hole wall to the bottom (+ drill point for blind holes)
            if (!h.ThroughAll && h.DrillPoint)
            {
                V(r, depth, "Tip");
                V(0, depth + r / Math.Tan(59 * Math.PI / 180), "Axis");
            }
            else
            {
                V(r, depth, "Bottom");
                V(0, depth, "Axis");
            }
            // the profile is in (radius, depth) coordinates; loop orientation is normalized by the builder
            var tools = new List<Solid>();
            int k = 0;
            var drill = -sk.Frame.Normal;
            foreach (var p in pts)
            {
                var origin = sk.Frame.ToWorld(p);
                var x = sk.Frame.XDir;
                var frame = new Frame(origin, x, drill, Vec3.Cross(x, drill));
                var tool = Builders.Revolve(new[] { new ProfileRegion(prof) }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, $"{f.Id}.{k++}",
                    SurfaceDef.SegmentsFor(r));
                if (tool.Native == null && tool.Volume() < 0) tool = tool.Flipped(); // native revolves are oriented by construction
                tools.Add(tool);
            }
            Solid all;
            using (Prof.Time("hole.toolunion")) all = Csg.UnionAll(tools);
            ApplyTool(all, cut: true, merge: true);
            State.Tools[f.Id] = new ToolInfo(all, true, true);
        }

        // ---- shell ----

        void DoShell(ShellFeature s)
        {
            var t = Positive(s.Thickness, "Thickness");
            if (s.Faces.Count == 0) throw new FeatureException("Select the faces to remove.");
            var byBody = new Dictionary<string, HashSet<string>>();
            foreach (var fr in s.Faces)
            {
                var (b, name) = Resolver.ResolveFaceName(fr, State);
                if (!byBody.TryGetValue(b.Id, out var set)) byBody[b.Id] = set = new HashSet<string>();
                set.Add(name);
            }
            List<Body> bodies;
            try
            {
                bodies = State.Bodies.Select(b => byBody.TryGetValue(b.Id, out var names)
                    ? new Body { Id = b.Id, Solid = ShellOp.Apply(b.Solid, names, t, f.Id) } : b).ToList();
            }
            catch (ShellFailedException ex) { throw new FeatureException(ex.Message); }
            State = State.With(bodies);
        }

        // ---- patterns / mirror ----

        void ApplyInstances(IEnumerable<string> seeds, IReadOnlyList<Transform> transforms)
        {
            if (!seeds.Any()) throw new FeatureException("Select the features to pattern.");
            if (transforms.Count == 0) throw new FeatureException("The pattern has no instances.");
            var everything = new List<Solid>(); bool? cutKind = null; bool mixed = false;
            foreach (var seedId in seeds)
            {
                if (!State.Tools.TryGetValue(seedId, out var tool))
                    throw new FeatureException("Only extrude, revolve, hole, fillet, chamfer, pattern and mirror features can be patterned.");
                var copies = new List<Solid>();
                for (int i = 0; i < transforms.Count; i++)
                    copies.Add(tool.Tool.Transformed(transforms[i], $"{f.Id}.{i + 1}."));
                var all = Csg.UnionAll(copies);
                ApplyTool(all, tool.IsCut, true);
                everything.Add(tool.Tool); everything.Add(all);
                if (cutKind == null) cutKind = tool.IsCut; else if (cutKind != tool.IsCut) mixed = true;
            }
            // the pattern/mirror as a whole (seeds + copies) can itself be patterned or mirrored
            // (built only when used; a cut and the fillet on its edges touch along the fillet's tangent faces, which
            // a union may not resolve: such a pattern works, it just cannot be patterned again)
            if (!mixed && cutKind is bool isCut)
                State.Tools[f.Id] = new ToolInfo(() =>
                {
                    var old = Csg.NoFallback;
                    Csg.NoFallback = true;
                    try { return Csg.UnionAll(everything); }
                    catch (InvalidOperationException ex) { throw new FeatureException("This pattern cannot be patterned or mirrored again: " + ex.Message); }
                    finally { Csg.NoFallback = old; }
                }, isCut, true);
        }

        void DoLinearPattern(LinearPatternFeature lp)
        {
            var (_, d1) = Resolver.ResolveDir(lp.Direction, State);
            if (lp.Reverse) d1 = -d1;
            var s1 = Num(lp.Spacing, "Spacing"); var n1 = (int)Math.Round(Num(lp.Count, "Instances"));
            int n2 = 1; double s2 = 0; Vec3 d2 = Vec3.Zero;
            if (lp.Direction2 != null)
            {
                (_, d2) = Resolver.ResolveDir(lp.Direction2, State);
                if (lp.Reverse2) d2 = -d2;
                s2 = Num(lp.Spacing2, "Spacing 2"); n2 = (int)Math.Round(Num(lp.Count2, "Instances 2"));
            }
            if (n1 < 1 || n2 < 1 || n1 * n2 > 2000) throw new FeatureException("Instance count must be between 1 and 2000.");
            var ts = new List<Transform>();
            for (int i = 0; i < n1; i++)
                for (int j = 0; j < n2; j++)
                    if (i != 0 || j != 0) ts.Add(Transform.Translation(d1 * (s1 * i) + d2 * (s2 * j)));
            ApplyInstances(lp.Seeds, ts);
        }

        void DoCircularPattern(CircularPatternFeature cp)
        {
            var (o, ax) = Resolver.ResolveDir(cp.Axis, State);
            if (cp.Reverse) ax = -ax;
            var n = (int)Math.Round(Num(cp.Count, "Instances"));
            var total = Num(cp.Angle, "Angle") * Math.PI / 180;
            if (n < 2 || n > 2000) throw new FeatureException("Instance count must be between 2 and 2000.");
            var step = cp.EqualSpacing ? (Math.Abs(total - 2 * Math.PI) < 1e-9 ? total / n : total / (n - 1)) : total;
            var ts = new List<Transform>();
            for (int i = 1; i < n; i++) ts.Add(Transform.Rotation(o, ax, step * i));
            ApplyInstances(cp.Seeds, ts);
        }

        void DoMirror(MirrorFeature m)
        {
            var frame = Resolver.ResolvePlane(m.Plane, State);
            ApplyInstances(m.Seeds, new[] { Transform.Mirror(frame.Plane) });
        }

        void DoPlane(PlaneFeature p)
        {
            if (p.Mode == PlaneMode.NormalToCurve)
            {
                if (p.Curve == null) throw new FeatureException("Select the curve (edge or sketch line/arc) the plane is normal to.");
                if (p.Point == null) throw new FeatureException("Select the point on the curve (e.g. its end) the plane passes through.");
                var (pt, n) = Resolver.CurveTangent(p.Curve, Resolver.ResolvePoint(p.Point, State), State);
                // in-plane X axis: horizontal (perpendicular to Z) unless the curve is vertical
                var up = Math.Abs(n.Z) < 0.9 ? Vec3.UnitZ : Vec3.UnitY;
                var x = Vec3.Cross(up, n).Normalized;
                var nf = new Frame(pt, x, Vec3.Cross(n, x), n);
                State.Planes[f.Id] = p.Flip ? nf.Flipped() : nf;
                return;
            }
            var frame = Resolver.ResolvePlane(p.Base, State);
            switch (p.Mode)
            {
                case PlaneMode.Offset:
                    frame = frame.Offset(Num(p.Offset, "Offset"));
                    break;
                case PlaneMode.ThroughPoint:
                    if (p.Point == null) throw new FeatureException("Select a vertex or sketch point for the plane to pass through.");
                    frame = Through(frame, Resolver.ResolvePoint(p.Point, State));
                    break;
                case PlaneMode.Angle:
                    {
                        if (p.Axis == null) throw new FeatureException("Select the axis (an edge, or X, Y or Z) to rotate the plane about.");
                        var (o, axis) = Resolver.ResolveDir(p.Axis, State);
                        var rot = Transform.Rotation(o, axis, Num(p.Angle, "Angle") * Math.PI / 180);
                        frame = new Frame(rot.Point(frame.Origin), rot.Vector(frame.XDir), rot.Vector(frame.YDir), rot.Vector(frame.Normal));
                        if (p.Point != null) frame = Through(frame, Resolver.ResolvePoint(p.Point, State));
                        break;
                    }
            }
            if (p.Flip) frame = frame.Flipped();
            State.Planes[f.Id] = frame;

            // same orientation, moved along the normal so the plane contains q (origin projected onto it)
            static Frame Through(Frame fr, Vec3 q) => fr.Offset(Vec3.Dot(q - fr.Origin, fr.Normal));
        }

        void DoCombine(CombineFeature c)
        {
            var main = State.FindBody(c.MainBody) ?? throw new FeatureException("Select the main body.");
            var tools = c.ToolBodies.Select(id => State.FindBody(id)).Where(b => b != null && b.Id != main.Id).ToList();
            if (tools.Count == 0) throw new FeatureException("Select the bodies to combine.");
            var acc = main.Solid;
            foreach (var t in tools)
                acc = c.Op switch
                {
                    CombineOp.Add => Csg.Union(acc, t!.Solid),
                    CombineOp.Subtract => Csg.Subtract(acc, t!.Solid),
                    _ => Csg.Intersect(acc, t!.Solid),
                };
            var used = tools.Select(t => t!.Id).ToHashSet();
            var bodies = new List<Body>();
            foreach (var b in State.Bodies)
            {
                if (b.Id == main.Id) { if (!acc.IsEmpty) bodies.Add(new Body { Id = main.Id, Solid = acc }); }
                else if (!used.Contains(b.Id)) bodies.Add(b);
            }
            State = State.With(bodies);
        }
    }
}

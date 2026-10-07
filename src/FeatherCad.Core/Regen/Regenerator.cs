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

    /// <summary>
    /// Rebuilds the document (features already built with the same inputs come from the cache).
    /// <paramref name="progress"/> is told which feature is being built (and, within patterns, which instance);
    /// <paramref name="cancel"/> stops the rebuild between features and pattern instances with
    /// <see cref="OperationCanceledException"/> (the features completed so far stay cached).
    /// </summary>
    public RegenResult Rebuild(CadDocument doc, int? upTo = null, CancellationToken cancel = default, Action<RegenProgress>? progress = null)
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
            // a flat pattern also depends on the part's sheet metal settings
            if (f is FlatPatternFeature) key = Hash(key, JsonSerializer.Serialize(doc.SheetMetal, Serialization.Options));
            if (i < _cache.Count && _cache[i].Key == key)
            {
                state = _cache[i].State;
                status[f.Id] = _cache[i].Status;
                states.Add(state);
                continue;
            }
            if (_cache.Count > i) _cache.RemoveRange(i, _cache.Count - i);
            cancel.ThrowIfCancellationRequested();
            var st = new FeatureStatus();
            var fsw = Stopwatch.StartNew();
            progress?.Invoke(new RegenProgress(i, count, f.Id, f.Name, null));
            if (!f.Suppressed)
            {
                try
                {
                    var ctx = new Ctx(doc, state.Copy(), lookup, st, f)
                    {
                        Cancel = cancel,
                        Detail = progress == null ? null : d => progress(new RegenProgress(i, count, f.Id, f.Name, d)),
                    };
                    using (Prof.Time("feature." + f.GetType().Name)) ctx.Execute();
                    state = ctx.State;
                }
                catch (OperationCanceledException) { throw; }
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

    /// <summary>
    /// The bend rule of a flat pattern: its own method and K-factor where it sets them, else the part's
    /// (<see cref="CadDocument.SheetMetal"/>, K-factor 0.5 when unset).
    /// </summary>
    public static BendRule RuleOf(CadDocument doc, FlatPatternFeature fp, Func<string?, string, double> eval)
    {
        var part = doc.SheetMetal ?? new SheetMetalSettings();
        var method = fp.Method ?? part.Method;
        var kExpr = string.IsNullOrWhiteSpace(fp.KFactor) ? part.KFactor : fp.KFactor;
        var k = method == BendAllowanceMethod.KFactor ? eval(kExpr, "K-factor") : 0.5;
        if (k < 0 || k > 1) throw new FeatureException("The K-factor must be between 0 and 1.");
        return new BendRule(method, k);
    }

    /// <summary>
    /// The transforms of a linear or circular pattern's instances 2, 3, … (the seed is instance 1), evaluated
    /// against the model state before the pattern; null for other features. Throws <see cref="FeatureException"/>
    /// for invalid parameters.
    /// </summary>
    public static List<Transform>? PatternTransforms(Feature f, ModelState state, Func<string, double?> lookup)
    {
        double Num(string? expr, string what)
        {
            if (!Expr.TryEval(expr, lookup, out var v, out var err)) throw new FeatureException($"{what}: {err}");
            return v;
        }
        var ts = new List<Transform>();
        switch (f)
        {
            case LinearPatternFeature lp:
                {
                    var (_, d1) = Resolver.ResolveDir(lp.Direction, state);
                    if (lp.Reverse) d1 = -d1;
                    var s1 = Num(lp.Spacing, "Spacing"); var n1 = (int)Math.Round(Num(lp.Count, "Instances"));
                    int n2 = 1; double s2 = 0; Vec3 d2 = Vec3.Zero;
                    if (lp.Direction2 != null)
                    {
                        (_, d2) = Resolver.ResolveDir(lp.Direction2, state);
                        if (lp.Reverse2) d2 = -d2;
                        s2 = Num(lp.Spacing2, "Spacing 2"); n2 = (int)Math.Round(Num(lp.Count2, "Instances 2"));
                    }
                    if (n1 < 1 || n2 < 1 || n1 * n2 > 2000) throw new FeatureException("Instance count must be between 1 and 2000.");
                    for (int i = 0; i < n1; i++)
                        for (int j = 0; j < n2; j++)
                            if (i != 0 || j != 0) ts.Add(Transform.Translation(d1 * (s1 * i) + d2 * (s2 * j)));
                    return ts;
                }
            case CircularPatternFeature cp:
                {
                    var (o, ax) = Resolver.ResolveDir(cp.Axis, state);
                    if (cp.Reverse) ax = -ax;
                    var n = (int)Math.Round(Num(cp.Count, "Instances"));
                    var total = Num(cp.Angle, "Angle") * Math.PI / 180;
                    if (n < 2 || n > 2000) throw new FeatureException("Instance count must be between 2 and 2000.");
                    // equal spacing spreads the instances over the total angle: past a full turn the last instance would
                    // land on (or just past) the seed
                    if (cp.EqualSpacing && !(total > 0 && total <= 2 * Math.PI + 1e-9))
                        throw new FeatureException("With equal spacing the total angle must be more than 0° and at most 360°.");
                    var step = cp.EqualSpacing ? (Math.Abs(total - 2 * Math.PI) < 1e-9 ? total / n : total / (n - 1)) : total;
                    for (int i = 1; i < n; i++) ts.Add(Transform.Rotation(o, ax, step * i));
                    return ts;
                }
            default: return null;
        }
    }

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
        public CancellationToken Cancel;
        /// <summary>Progress within the feature (e.g. "instance 7 of 24").</summary>
        public Action<string>? Detail;

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
                case FlatPatternFeature fp: DoFlatPattern(fp); break;
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
                (Body, Kernel.Native.NEdge)? hit; using (Prof.Time("blend.resolve")) hit = Resolver.ResolveNativeEdge(r, State);
                if (hit == null) return false;
                var (b, e) = hit.Value;
                if (!byBody.TryGetValue(b.Id, out var l)) byBody[b.Id] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
            var bodies = new List<Body>();
            var errors = new List<string>();
            var applied = new List<(Solid tool, bool cut)>();
            var blended = new List<Vec3[]>(); double blendedLength = 0;
            foreach (var b in State.Bodies)
            {
                if (!byBody.TryGetValue(b.Id, out var seeds)) { bodies.Add(b); continue; }
                List<Kernel.Native.NEdge> edges; using (Prof.Time("blend.expand")) edges = NativeBlend.Expand(b.Solid.Native!, seeds, propagate);
                using (Prof.Time("blend.snapshot")) { blended.AddRange(edges.Select(EdgePoints)); blendedLength += edges.Sum(TrueLength); }
                using var _bt = Prof.Time("blend.total");
                var r = NativeBlend.Apply(b.Solid, edges, size, fillet, f.Id, errors, overflow, status.ProblemEdges, applied, chamfer);
                bodies.Add(new Body { Id = b.Id, Solid = r });
            }
            // a blend on one body with tools of one kind can be patterned/mirrored like a cut or boss (its copies
            // blend the copies of the geometry it was made on)
            if (errors.Count == 0 && byBody.Count == 1 && applied.Count > 0 && applied.All(t => t.cut == applied[0].cut))
            {
                var tools = applied.Select(t => t.tool).ToList();
                State.Tools[f.Id] = new ToolInfo(() => tools.Count == 1 ? tools[0] : Csg.UnionAll(tools), applied[0].cut, true)
                {
                    Blend = new BlendSpec(refs.Select(r => r.Clone()).ToList(), size, fillet, propagate, overflow, chamfer, blended, blendedLength),
                };
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

        // ---- sheet metal ----

        void DoFlatPattern(FlatPatternFeature fp)
        {
            var rule = RuleOf(doc, fp, Num);
            if (State.Bodies.Count == 0) throw new FeatureException("There is no body to flatten.");
            var targets = new List<(Body body, Kernel.Native.NFace? face)>();
            if (fp.FixedFace != null)
            {
                var (b, name) = Resolver.ResolveFaceName(fp.FixedFace, State);
                var face = b.Solid.Native?.Faces.FirstOrDefault(x => x.Tag.Name == name);
                if (face != null && face.Surface.Kind != SurfaceKind.Plane) throw new FeatureException("The fixed face must be planar.");
                targets.Add((b, face));
            }
            else targets.AddRange(State.Bodies.Select(b => (b, (Kernel.Native.NFace?)null)));

            var bodies = new List<Body>(State.Bodies);
            foreach (var (body, face) in targets)
            {
                var native = body.Solid.Native ?? throw new FeatureException("The body has no exact geometry to unfold.");
                FlatPattern flat;
                try { flat = SheetUnfold.Unfold(native, rule, face, f.Id); }
                catch (UnfoldException ex) { throw new FeatureException(targets.Count > 1 ? $"{body.Id}: {ex.Message}" : ex.Message); }
                State.Flats[body.Id] = flat;
                if (fp.Flatten) bodies[bodies.IndexOf(body)] = new Body { Id = body.Id, Solid = Kernel.Native.NTess.ToSolid(flat.Body) };
            }
            if (fp.Flatten) State = State.With(bodies);
        }

        // ---- patterns / mirror ----

        /// <summary>
        /// Applies the seeds' tools at each transform (instance i is number i + 2, the seed being 1). Instances listed
        /// in <paramref name="skip"/> are left out; an instance whose tool cannot be applied is left out with a warning.
        /// </summary>
        void ApplyInstances(IEnumerable<string> seeds, IReadOnlyList<Transform> transforms, IEnumerable<int>? skip = null)
        {
            var skipped = new HashSet<int>((skip ?? Enumerable.Empty<int>()).Select(n => n - 2));
            if (!seeds.Any()) throw new FeatureException("Select the features to pattern.");
            if (transforms.Count == 0) throw new FeatureException("The pattern has no instances.");
            var everything = new List<Solid>(); bool? cutKind = null; bool mixed = false;
            foreach (var seedId in seeds)
            {
                if (!State.Tools.TryGetValue(seedId, out var tool))
                    throw new FeatureException("Only extrude, revolve, hole, fillet, chamfer, pattern and mirror features can be patterned.");
                var copies = new List<(int i, Solid tool)>();
                // instances whose blend is solved again (their edges are not the seed's edges moved)
                var toSolve = new List<(int i, Body body, List<Kernel.Native.NEdge> edges, double tol)>();
                for (int i = 0; i < transforms.Count; i++)
                {
                    if (skipped.Contains(i)) continue;
                    Cancel.ThrowIfCancellationRequested();
                    Detail?.Invoke($"{Describe(seedId)}, instance {i + 2} of {transforms.Count + 1}");
                    if (tool.Blend is { } bs0 && InstanceEdges(bs0, seeds, transforms[i], i) is { } inst && !inst.same)
                    {
                        toSolve.Add((i, inst.body, inst.edges, inst.tol));
                        continue;
                    }
                    copies.Add((i, tool.Tool.Transformed(transforms[i], $"{f.Id}.{i + 1}.")));
                }
                // instances solved again: one solve per distinct configuration (instances whose edges are another's
                // moved reuse its tools), solved in parallel, each on its own copy of the body (nothing shared is
                // written), then applied below with the copies' one-at-a-time fallback
                var resolved = new List<(int i, List<(Solid tool, bool cut)> tools, bool trimmed)>();
                if (toSolve.Count > 0 && tool.Blend is { } bs)
                {
                    var rep = new int[toSolve.Count];
                    var points = toSolve.Select(x => x.edges.SelectMany(EdgePoints).ToList()).ToList();
                    var lengths = toSolve.Select(x => x.edges.Sum(TrueLength)).ToList();
                    for (int k = 0; k < toSolve.Count; k++)
                    {
                        rep[k] = k;
                        for (int q = 0; q < k; q++)
                        {
                            if (rep[q] != q) continue;
                            var rel = transforms[toSolve[k].i] * transforms[toSolve[q].i].RigidInverse();
                            if (MatchEdges(toSolve[k].edges, points[q].Select(pt => rel.Point(pt)), lengths[q], toSolve[k].tol)) { rep[k] = q; break; }
                        }
                    }
                    var reps = Enumerable.Range(0, toSolve.Count).Where(k => rep[k] == k).ToList();
                    var built = new (List<(Solid tool, bool cut)>? tools, string? error, List<Vec3[]> problems, bool trimmed)[toSolve.Count];
                    var opts = new ParallelOptions { CancellationToken = Cancel };
                    using var _ps = Prof.Time("pattern.solve");
                    Parallel.ForEach(reps, opts, k =>
                    {
                        var (i, body, edges, _) = toSolve[k];
                        Detail?.Invoke($"{Describe(seedId)}: solving instance {i + 2} ({reps.Count} distinct of {toSolve.Count})");
                        var problems = new List<Vec3[]>();
                        Csg.NoFallback = true; // this worker's thread: exact or skipped, never the slow faceted fallback
                        try
                        {
                            var copy = body.Solid.Native!.Clone(out var map);
                            var mine = edges.Select(e => map.TryGetValue(e, out var m) ? m : null).ToList();
                            if (mine.Any(e => e == null)) { built[k] = (null, "its edges are not on the body", problems, false); return; }
                            var errors = new List<string>();
                            var applied = new List<(Solid tool, bool cut)>();
                            var trimmed = new System.Runtime.CompilerServices.StrongBox<bool>();
                            NativeBlend.Apply(Kernel.Native.NTess.ToSolid(copy), mine!, bs.Size, bs.Fillet, $"{f.Id}.{i + 1}.{seedId}", errors, bs.Overflow,
                                problems, applied, bs.Chamfer, buildOnly: true, trimmedOut: trimmed);
                            built[k] = errors.Count > 0 ? (null, errors[0], problems, false) : (applied, null, problems, trimmed.Value);
                        }
                        catch (Exception ex) when (ex is FeatureException or InvalidOperationException or Kernel.Native.NBooleanException)
                        {
                            built[k] = (null, ex.Message, problems, false);
                        }
                        finally { Csg.NoFallback = false; }
                    });
                    for (int k = 0; k < toSolve.Count; k++)
                    {
                        var (i, _, _, _) = toSolve[k];
                        var b = built[rep[k]];
                        if (b.tools == null)
                        {
                            status.Warnings.Add($"Instance {i + 2} is not {(bs.Fillet ? "filleted" : "chamfered")}: {b.error}");
                            if (rep[k] == k) status.ProblemEdges.AddRange(b.problems);
                            continue;
                        }
                        if (rep[k] == k) { resolved.Add((i, b.tools, b.trimmed)); continue; }
                        var rel = transforms[i] * transforms[toSolve[rep[k]].i].RigidInverse();
                        var moved = b.tools.Select(x => (x.tool.Transformed(rel, $"{f.Id}.{i + 1}."), x.cut)).ToList();
                        // the same blend moved: with the copies (one boolean for all), unless its tools must go one at a time
                        if (!b.trimmed && moved.All(x => x.cut == tool.IsCut)) copies.AddRange(moved.Select(x => (i, x.Item1)));
                        else resolved.Add((i, moved, b.trimmed));
                    }
                }
                // (the instances solved again first, as when they were solved one by one)
                if (resolved.Count > 0) ApplySolved(resolved, seedId, transforms.Count + 1, everything);
                everything.Add(tool.Tool);
                if (copies.Count > 0)
                {
                    var before = State;
                    // exact only: where the copies cannot be applied together they are tried one at a time below (a
                    // faceted fallback of the whole pattern is slow and loses the exact geometry)
                    var oldAll = Csg.NoFallback;
                    try
                    {
                        Csg.NoFallback = true;
                        var all = Csg.UnionAll(copies.Select(c => c.tool).ToList());
                        ApplyTool(all, tool.IsCut, true);
                        everything.Add(all);
                    }
                    catch (Exception ex) when (ex is FeatureException or InvalidOperationException or Kernel.Native.NBooleanException)
                    {
                        // all copies at once failed: one at a time, leaving out (with a warning) the ones that fail; no
                        // slow faceted fallback for these (an instance that fails exactly is skipped, not approximated)
                        State = before;
                        var oldFallback = Csg.NoFallback;
                        Csg.NoFallback = true;
                        try
                        {
                            foreach (var g in copies.GroupBy(c => c.i))
                            {
                                Cancel.ThrowIfCancellationRequested();
                                Detail?.Invoke($"{Describe(seedId)}, instance {g.Key + 2} of {transforms.Count + 1} (one at a time)");
                                var at = State;
                                try
                                {
                                    var one = g.Count() == 1 ? g.First().tool : Csg.UnionAll(g.Select(c => c.tool).ToList());
                                    ApplyTool(one, tool.IsCut, true);
                                    everything.Add(one);
                                }
                                catch (Exception ex2) when (ex2 is FeatureException or InvalidOperationException or Kernel.Native.NBooleanException)
                                {
                                    State = at;
                                    status.Warnings.Add($"Instance {g.Key + 2} of {Describe(seedId)} is skipped: {ex2.Message}");
                                }
                            }
                        }
                        finally { Csg.NoFallback = oldFallback; }
                    }
                    finally { Csg.NoFallback = oldAll; }
                }
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

        /// <summary>
        /// The edges a blend seed resolves to on pattern instance i (its references renamed to the instance's faces,
        /// hints moved, tangent chains as for the seed), and whether they are the seed's edges moved. Null when the
        /// references do not resolve (the tool is copied then, as before).
        /// </summary>
        (Body body, List<Kernel.Native.NEdge> edges, bool same, double tol)? InstanceEdges(BlendSpec bs, IEnumerable<string> seeds, Transform t, int i)
        {
            if (!Csg.UseNative || State.Bodies.Any(b => b.Solid.Native == null)) return null;
            var seedIds = seeds.ToList();
            string Map(string name) => seedIds.Any(s => name.StartsWith(s + ".")) ? $"{f.Id}.{i + 1}." + name : name;
            Body? body = null;
            var found = new List<Kernel.Native.NEdge>();
            foreach (var r in bs.Refs)
            {
                var hit = Resolver.ResolveNativeEdge(new EdgeRef { A = Map(r.A), B = Map(r.B), Body = r.Body, Hint = t.Point(r.Hint) }, State);
                if (hit == null)
                {
                    if (Environment.GetEnvironmentVariable("FC_PATDBG") != null) Console.WriteLine($"  instance {i + 2}: {Map(r.A)} / {Map(r.B)} not found");
                    return null;
                }
                if (body != null && hit.Value.body.Id != body.Id) return null;
                body = hit.Value.body;
                if (!found.Contains(hit.Value.edge)) found.Add(hit.Value.edge);
            }
            if (body == null) return null;
            var edges = NativeBlend.Expand(body.Solid.Native!, found, bs.Propagate);
            // the same curves (however the faces split them into edges): points of the moved seed edges lie on the
            // instance's edges and the other way round
            // traced edges are fitted curves: the same intersection traced on two instances differs by the fit tolerance
            var tol = 1e-5 * Math.Max(1, body.Solid.Native!.Bounds.Diagonal);
            bool same = MatchEdges(edges, bs.Edges.SelectMany(w => w).Select(p => t.Point(p)), bs.Length, tol);
            if (Environment.GetEnvironmentVariable("FC_PATDBG") != null) Console.WriteLine($"  instance {i + 2}: {edges.Count} edges (seed {bs.Edges.Count}) -> {(same ? "copy" : "solve")}");
            return (body, edges, same, tol);
        }

        /// <summary>Whether edges are the curves through the given points with the given total length (however split).</summary>
        static bool MatchEdges(List<Kernel.Native.NEdge> edges, IEnumerable<Vec3> want, double length, double tol)
        {
            static double Dist(Kernel.Native.NEdge e, Vec3 p)
            {
                double lo = Math.Min(e.T0, e.T1), hi = Math.Max(e.T0, e.T1);
                var u = e.Curve.Param(p, e.Curve.Periodic ? 0.5 * (lo + hi) : null);
                for (int k = 0; k < 6; k++)
                {
                    var d = e.Curve.Deriv(u); var dd = Vec3.Dot(d, d);
                    if (!(dd > 1e-300)) break;
                    u -= Vec3.Dot(e.Curve.Eval(u) - p, d) / dd;
                }
                if (e.Curve.Periodic) { while (u < lo - 1e-9) u += e.Curve.Period; while (u > hi + 1e-9) u -= e.Curve.Period; }
                return Math.Min(Vec3.Distance(e.Curve.Eval(Math.Clamp(u, lo, hi)), p), Math.Min(Vec3.Distance(e.V0.P, p), Vec3.Distance(e.V1.P, p)));
            }
            var le = edges.Sum(TrueLength);
            return Math.Abs(le - length) <= 1e-4 * Math.Max(length, 1e-9) && want.All(p => edges.Any(e => Dist(e, p) < tol));
        }

        static double TrueLength(Kernel.Native.NEdge e)
        {
            var q = e.Samples(1e-5);
            double l = 0; for (int k = 1; k < q.Count; k++) l += Vec3.Distance(q[k - 1], q[k]);
            return l;
        }

        static Vec3[] EdgePoints(Kernel.Native.NEdge e) => Enumerable.Range(0, 9).Select(k => k == 0 ? e.V0.P : k == 8 ? e.V1.P : e.At(k / 8.0)).ToArray();

        /// <summary>
        /// Applies blends solved for pattern instances, each the way the blend itself applies its tools: trimmed tools
        /// (they may touch each other) one at a time, the instance failing as a whole if one fails; other tools as
        /// one union per kind; instance by instance, as before they were solved in parallel (a failed attempt to apply
        /// them all at once costs more than it saves). An instance that fails is left out with a warning. Exact
        /// booleans only.
        /// </summary>
        void ApplySolved(List<(int i, List<(Solid tool, bool cut)> tools, bool trimmed)> resolved, string seedId, int count, List<Solid> everything)
        {
            var old = Csg.NoFallback;
            Csg.NoFallback = true;
            try
            {
                bool Union(IEnumerable<(Solid tool, bool cut)> ts)
                {
                    var at = State;
                    try
                    {
                        foreach (var kind in new[] { true, false })
                        {
                            var group = ts.Where(x => x.cut == kind).Select(x => x.tool).ToList();
                            if (group.Count > 0) ApplyTool(group.Count == 1 ? group[0] : Csg.UnionAll(group), kind, true);
                        }
                        return true;
                    }
                    catch (Exception ex) when (ex is FeatureException or InvalidOperationException or Kernel.Native.NBooleanException)
                    {
                        State = at;
                        return false;
                    }
                }
                bool OneByOne(List<(Solid tool, bool cut)> ts)
                {
                    var at = State;
                    // cuts first, then additions, as the blend applies them
                    foreach (var t in ts.Where(x => x.cut).Concat(ts.Where(x => !x.cut)))
                        if (!Union(new[] { t })) { State = at; return false; }
                    return true;
                }
                var kindName = doc.Features.FirstOrDefault(x => x.Id == seedId) is ChamferFeature ? "chamfered" : "filleted";
                foreach (var (i, tools, trimmed) in resolved)
                {
                    Cancel.ThrowIfCancellationRequested();
                    Detail?.Invoke($"{Describe(seedId)}, instance {i + 2} of {count}");
                    if (trimmed ? OneByOne(tools) : Union(tools)) everything.AddRange(tools.Select(x => x.tool));
                    else status.Warnings.Add(trimmed
                        ? $"Instance {i + 2} is not {kindName}: the blends that run into neighbouring geometry also run into each other where both are tangent to the same face; that corner cannot be blended yet."
                        : $"Instance {i + 2} of {Describe(seedId)} is skipped: its blend cannot be applied.");
                }
            }
            finally { Csg.NoFallback = old; }
        }

        string Describe(string featureId) => doc.Features.FirstOrDefault(x => x.Id == featureId)?.Name ?? featureId;

        void DoLinearPattern(LinearPatternFeature lp) => ApplyInstances(lp.Seeds, PatternTransforms(lp, State, lookup)!, lp.Skip);

        void DoCircularPattern(CircularPatternFeature cp) => ApplyInstances(cp.Seeds, PatternTransforms(cp, State, lookup)!, cp.Skip);

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

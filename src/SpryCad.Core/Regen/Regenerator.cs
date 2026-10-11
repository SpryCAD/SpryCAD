using System.Diagnostics;
using System.Text.Json;
using SpryCad.Core.Model;
using SpryCad.Core.Sketching;
using SpryCad.Kernel;

namespace SpryCad.Core.Regen;

/// <summary>
/// Rebuilds the feature history. Results are cached per feature, keyed by a hash of the feature
/// definition and everything before it, so editing a late feature only rebuilds from that point.
/// </summary>
public sealed class Regenerator
{
    // results by the hash of a feature and everything before it, not only the latest history: undo, redo and a
    // preview's way back find what they built before. Kept: the least recently used dropped beyond two entries
    // per feature (the history being built is always the most recent). Invalidate may come from another thread
    // than the rebuild's: the cache is used under its lock, and a rebuild under way then adds nothing (Epoch)
    sealed class CacheEntry(ulong key, ModelState state, FeatureStatus status)
    {
        public readonly ulong Key = key;
        public readonly ModelState State = state;
        public readonly FeatureStatus Status = status;
        public long Used;
    }
    readonly Dictionary<ulong, CacheEntry> _cache = new();
    long _uses;
    int _epoch;

    /// <summary>Threads as each is set, or all modeled (exports) / all cosmetic (a lightweight view).</summary>
    public ThreadDisplay ThreadOverride { get; set; }

    /// <summary>
    /// Rebuilds the document (features already built with the same inputs come from the cache).
    /// <paramref name="progress"/> is told which feature is being built (and, within patterns, which instance);
    /// <paramref name="cancel"/> stops the rebuild between features and pattern instances with
    /// <see cref="OperationCanceledException"/> (the features completed so far stay cached).
    /// </summary>
    public RegenResult Rebuild(CadDocument doc, int? upTo = null, CancellationToken cancel = default, Action<RegenProgress>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        int epoch;
        lock (_cache) epoch = _epoch;
        var (eqValues, eqErrors) = EvaluateEquations(doc);
        Func<string, double?> lookup = name => eqValues.TryGetValue(name, out var v) ? v : null;

        // features see the equations (and design tables) only through their values: a description, a variable made
        // user-facing or an expression giving the same value rebuilds nothing
        ulong key = Hash(0xcbf29ce484222325UL, string.Join(";", eqValues.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        key = Hash(key, string.Join(";", eqErrors.Keys.Order(StringComparer.OrdinalIgnoreCase)));
        var state = ModelState.Initial();
        var states = new List<ModelState>();
        var status = new Dictionary<string, FeatureStatus>();
        int count = Math.Min(doc.ActiveCount, upTo ?? int.MaxValue);
        int rebuilt = 0;
        // features not built (suppressed, by condition, or with a suppressed feature they use), by id and name
        var off = new Dictionary<string, string>();
        for (int i = 0; i < count; i++)
        {
            var f = doc.Features[i];
            var json = CadDocument.JsonOf(f);
            key = Hash(key, json);
            // (the condition and what it uses are covered by the key: the equations and every feature before)
            var (suppressed, by, condError) = SuppressionOf(f, json, off, lookup);
            if (suppressed) off[f.Id] = f.Name;
            // a flat pattern also depends on the part's sheet metal settings
            if (f is FlatPatternFeature) key = Hash(key, JsonSerializer.Serialize(doc.SheetMetal, Serialization.Compact));
            // a thread also on how threads are shown
            if (f is ThreadFeature) key = Hash(key, ThreadOverride.ToString());
            CacheEntry? hit;
            lock (_cache) if (_cache.TryGetValue(key, out hit) && epoch == _epoch) hit.Used = ++_uses; else hit = null;
            if (hit != null)
            {
                state = hit.State;
                status[f.Id] = hit.Status;
                states.Add(state);
                continue;
            }
            cancel.ThrowIfCancellationRequested();
            var st = new FeatureStatus { Suppressed = suppressed, SuppressedBy = by, Error = condError };
            var fsw = Stopwatch.StartNew();
            progress?.Invoke(new RegenProgress(i, count, f.Id, f.Name, null));
            if (!suppressed && condError == null)
            {
                try
                {
                    var ctx = new Ctx(doc, state.Copy(), lookup, st, f)
                    {
                        Cancel = cancel,
                        ThreadMode = ThreadOverride,
                        Earlier = status,
                        Detail = progress == null ? null : d => progress(new RegenProgress(i, count, f.Id, f.Name, d)),
                    };
                    // (the kernel's long operations stop between their stages too)
                    using (Kernel.Cancellation.Use(cancel))
                    using (Prof.Time("feature." + f.GetType().Name)) ctx.Execute();
                    // an exact body left faceted (a boolean fell back to facets): said, or nothing shows why everything
                    // after it is slow and approximate
                    if (state.Bodies.All(b => b.Solid.Native != null) && ctx.State.Bodies.Any(b => b.Solid.Native == null))
                        st.Warnings.Add("The result is no longer exact (an exact boolean failed" + (Csg.LastFallbackReason is { } why ? ": " + why : "")
                            + "), so the features after it are slower and approximate.");
                    state = ctx.State;
                }
                catch (OperationCanceledException) { throw; }
                catch (FeatureException ex) { st.Error = ex.Message; }
                catch (Expr.ExprException ex) { st.Error = ex.Message; }
                catch (Exception ex) { st.Error = "Rebuild error: " + ex.Message; }
            }
            st.Milliseconds = fsw.Elapsed.TotalMilliseconds;
            rebuilt++;
            lock (_cache)
                if (epoch == _epoch)
                {
                    _cache[key] = new CacheEntry(key, state, st) { Used = ++_uses };
                    Trim(2 * doc.Features.Count + 8);
                }
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

    public void Invalidate()
    {
        lock (_cache) { _cache.Clear(); _epoch++; }
    }

    // drops the least recently used entries beyond max (called under the lock)
    void Trim(int max)
    {
        if (_cache.Count <= max) return;
        foreach (var e in _cache.Values.OrderBy(e => e.Used).Take(_cache.Count - max).ToList()) _cache.Remove(e.Key);
    }

    /// <summary>
    /// Whether a feature is built: not when suppressed by the user, while its Suppress When condition holds, or
    /// when it uses a feature not built (<paramref name="off"/>, by id: name), found in its definition
    /// (<paramref name="json"/>) as <see cref="CadDocument.DependentsOf"/> does; for a sketch, in its plane only
    /// (converted model edges of a suppressed feature are left dangling). A condition that cannot be
    /// evaluated is an error (the feature is not built either).
    /// </summary>
    static (bool suppressed, string? by, string? error) SuppressionOf(Feature f, string json, Dictionary<string, string> off, Func<string, double?> lookup)
    {
        if (f.Suppressed) return (true, null, null);
        if (!string.IsNullOrWhiteSpace(f.SuppressWhen))
        {
            if (!Expr.TryEval(f.SuppressWhen, lookup, out var v, out var err)) return (false, null, "Suppress when: " + err);
            if (Expr.Truthy(v)) return (true, "condition", null);
        }
        if (off.Count == 0) return (false, null, null);
        // a sketch goes with the plane or face it is on; geometry it took from the model stays, dangling
        if (f is SketchFeature sk) json = JsonSerializer.Serialize(sk.Plane, Serialization.Compact);
        // a pattern or mirror goes on without its suppressed seeds (it says so), suppressed only with all of them
        if (SeedsOf(f) is { Count: > 0 } seeds)
        {
            if (seeds.All(off.ContainsKey)) return (true, off[seeds[0]], null);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            foreach (var k in node.Select(kv => kv.Key).Where(k => k.Equals("Seeds", StringComparison.OrdinalIgnoreCase)).ToList()) node.Remove(k);
            json = node.ToJsonString();
        }
        foreach (var (id, name) in off)
            if (CadDocument.Refers(json, id)) return (true, name, null);
        return (false, null, null);
    }

    /// <summary>The features a pattern or mirror copies; null for other features.</summary>
    static List<string>? SeedsOf(Feature f) => f switch
    {
        LinearPatternFeature lp => lp.Seeds,
        CircularPatternFeature cp => cp.Seeds,
        MirrorFeature m => m.Seeds,
        _ => null,
    };

    /// <summary>
    /// Which features are not built, and why (null: by the user; "condition"; or the suppressed feature used),
    /// without rebuilding: for the tree and dialogs before a rebuild has run.
    /// </summary>
    public static Dictionary<string, string?> SuppressedFeatures(CadDocument doc, Func<string, double?>? lookup = null)
    {
        lookup ??= EquationLookup(doc);
        var off = new Dictionary<string, string>();
        var result = new Dictionary<string, string?>();
        foreach (var f in doc.Features)
        {
            var (s, by, _) = SuppressionOf(f, CadDocument.JsonOf(f), off, lookup);
            if (!s) continue;
            off[f.Id] = f.Name; result[f.Id] = by;
        }
        return result;
    }

    /// <summary>The document's equation values as an expression lookup.</summary>
    public static Func<string, double?> EquationLookup(CadDocument doc)
    {
        var values = EvaluateEquations(doc).values;
        return n => values.TryGetValue(n, out var v) ? v : null;
    }

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
        // design tables first: each its row number, and its columns the row's values; then the equations. A name
        // given twice is an error at the second (the first keeps its value).
        var all = new List<Equation>();
        foreach (var t in doc.Tables ?? new())
        {
            if (string.IsNullOrWhiteSpace(t.Name) || t.Current is not { } row) continue;
            all.Add(new Equation { Name = t.Name.Trim(), Expr = (t.Rows.IndexOf(row) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) });
            for (int c = 0; c < t.Columns.Count; c++)
                if (!string.IsNullOrWhiteSpace(t.Columns[c])) all.Add(new Equation { Name = t.Columns[c].Trim(), Expr = row.ValueAt(c) });
        }
        all.AddRange(doc.Equations.Where(e => !string.IsNullOrWhiteSpace(e.Name)));
        var pending = new List<Equation>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var twice = new Dictionary<string, string>();
        foreach (var e in all)
            if (names.Add(e.Name.Trim())) pending.Add(e);
            else twice[e.Name] = $"{e.Name.Trim()} is set twice (by a design table or another equation)";
        // in passes until none succeeds. An equation naming another one still pending waits for it without being
        // evaluated (an unknown name throws, and equations listed before what they use cost one exception per pass
        // each); the pass that changes nothing evaluates every one left, for its error
        var waiting = new HashSet<string>(pending.Select(e => e.Name.Trim()), StringComparer.OrdinalIgnoreCase);
        var uses = pending.ToDictionary(e => e, e => string.IsNullOrWhiteSpace(e.Expr) ? new List<string>() : Expr.Identifiers(e.Expr).ToList());
        // (each pass that evaluates one more shortens the list: the passes end; no count limit, which used to stop a
        // chain of equations listed after the ones they use before its last was reached)
        bool last = false;
        while (pending.Count > 0)
        {
            var next = new List<Equation>();
            foreach (var e in pending)
            {
                if (!last && uses[e].Any(n => waiting.Contains(n) && !values.ContainsKey(n))) { next.Add(e); continue; }
                if (Expr.TryEval(e.Expr, n => values.TryGetValue(n, out var v) ? v : null, out var val, out var err))
                {
                    values[e.Name.Trim()] = val;
                    waiting.Remove(e.Name.Trim());
                }
                else { next.Add(e); errors[e.Name] = err ?? "error"; }
            }
            foreach (var e in pending.Except(next)) errors.Remove(e.Name);
            if (next.Count == pending.Count)
            {
                // nothing more evaluates: once more with every one left tried, for the errors (as each pass did)
                if (last) break;
                last = true;
            }
            pending = next;
        }
        foreach (var (n, msg) in twice) errors[n] = msg;
        return (values, errors);
    }

    /// <summary>
    /// Per-edge blend errors ("Edge A / B: why") with the same reason given once, the edges after it; else as they are.
    /// </summary>
    static string BlendErrors(List<string> errors)
    {
        var parsed = errors.Select(e =>
        {
            var i = e.StartsWith("Edge ", StringComparison.Ordinal) ? e.IndexOf(": ", StringComparison.Ordinal) : -1;
            return i < 0 ? (edge: (string?)null, why: e) : (edge: e[5..i], why: e[(i + 2)..]);
        }).ToList();
        return string.Join(" | ", parsed.GroupBy(x => x.why).Select(g =>
            g.All(x => x.edge != null) && g.Count() > 1 ? $"{g.Key} ({string.Join(", ", g.Select(x => x.edge))})"
            : string.Join(" | ", g.Select(x => x.edge == null ? x.why : $"Edge {x.edge}: {x.why}"))));
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Tests: whether unions of touching pattern copies may reuse a rotated earlier union, and how many did.</summary>
    public static bool ReusePatternUnions = true;
    public static int PatternUnionsReused;

    sealed class Ctx(CadDocument doc, ModelState state, Func<string, double?> lookup, FeatureStatus status, Feature f)
    {
        public ModelState State = state;
        public CancellationToken Cancel;
        public ThreadDisplay ThreadMode;
        /// <summary>The features before this one: whether each was built, and why not.</summary>
        public IReadOnlyDictionary<string, FeatureStatus> Earlier = new Dictionary<string, FeatureStatus>();
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
                case SweepFeature sw: DoSweep(sw); break;
                case HelicalSweepFeature hs: DoHelical(hs); break;
                case ThreadFeature th: DoThread(th); break;
                case FilletFeature fi: DoBlend(fi.Edges, Positive(fi.Radius, "Radius"), true, fi.TangentPropagation, fi.Overflow); break;
                case ChamferFeature ch: { var cs = ChamferSizeOf(ch); DoBlend(ch.Edges, cs.D1, false, ch.TangentPropagation, ch.Overflow, cs); break; }
                case HoleFeature h: DoHole(h); break;
                case ShellFeature sh: DoShell(sh); break;
                case DraftFeature dr: DoDraft(dr); break;
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
            SketchConvert.Update(sk, frame, State, status.Warnings); // converted entities follow their model edges
            foreach (var c in sk.Constraints)
            {
                if (!c.IsDimension || c.Driven || string.IsNullOrWhiteSpace(c.Expr)) continue;
                c.Value = Num(c.Expr, "Dimension");
            }
            var res = SketchSolver.Solve(sk);
            if (res.Status == SketchStatus.OverDefined) status.Warnings.Add("Sketch is over defined or cannot be solved.");
            var regions = ProfileExtractor.Extract(sk, out var warns);
            // a sweep's path is open by design: no profile warnings for it
            if (!doc.Features.OfType<SweepFeature>().Any(w => w.PathSketchId == s.Id)) status.Warnings.AddRange(warns);
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

        /// <summary>How far the bodies reach from the sketch's plane along its normal (dir: +1 or -1); 0 when nothing is that side.</summary>
        double Reach(Frame frame, double dir)
        {
            var b = State.Bounds;
            if (b.IsEmpty) return 0;
            double far = 0;
            for (int c = 0; c < 8; c++)
            {
                var p = new Vec3((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z);
                far = Math.Max(far, dir * Vec3.Dot(p - frame.Origin, frame.Normal));
            }
            return far;
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
            // a draft: the side walls tapered from the sketch plane, narrowing along the extrusion (outward: widening);
            // in two directions (Mid Plane, a second depth, Through All - Both) each side narrowing away from the sketch
            // plane: the two halves drafted on their own and joined there
            if (!string.IsNullOrWhiteSpace(e.Draft) && Num(e.Draft, "Draft angle") is var draft && draft != 0)
            {
                if (Math.Abs(draft) >= 89) throw new FeatureException("The draft angle must be below 89 degrees.");
                // Through All: just past the far side of the part (drafted walls taken far enough would meet and cross)
                var margin = Math.Max(0.1, 1e-3 * State.Bounds.Diagonal);
                if (e.End == EndCondition.ThroughAll)
                {
                    var reach = Reach(sk.Frame, dir);
                    if (reach <= 1e-9) throw new FeatureException("There is nothing beyond the sketch in that direction to go through.");
                    z1 = (reach + margin) * dir;
                }
                else if (e.End == EndCondition.ThroughAllBoth)
                {
                    double up = Reach(sk.Frame, 1), down = Reach(sk.Frame, -1);
                    if (up <= 1e-9 && down <= 1e-9) throw new FeatureException("There is nothing on either side of the sketch to go through.");
                    z1 = up > 1e-9 ? up + margin : 0;
                    z0 = down > 1e-9 ? -(down + margin) : 0;
                }
                double lo = Math.Min(z0, z1), hi = Math.Max(z0, z1);
                var neutral = Plane.FromPointNormal(sk.Frame.ToWorld(0, 0, 0), sk.Frame.Normal);
                var angle = (e.DraftOutward ? -1 : 1) * draft * Math.PI / 180;
                // one half: extruded from the sketch plane to depth z, its sides drafted pulling away from the plane
                Solid Half(double z, string prefix)
                {
                    var half = Builders.Extrude(sk.Regions, sk.Frame, Math.Min(0, z), Math.Max(0, z), prefix);
                    var sides = (half.Native?.Faces.Select(x => x.Tag.Name) ?? Enumerable.Empty<string>())
                        .Where(n => n != prefix + ".Start" && n != prefix + ".End").ToHashSet();
                    try { return DraftOp.Apply(half, sides, neutral, sk.Frame.Normal * Math.Sign(z), angle); }
                    catch (DraftFailedException ex)
                    {
                        // (faces that cannot be drafted say so; otherwise the walls met: the profile closed up on the way)
                        if (ex.Message.Contains("can be drafted")) throw new FeatureException(ex.Message);
                        throw new FeatureException($"The drafted side walls meet before the end of the extrude ({Math.Abs(z):0.###} mm from the sketch): " +
                            "the profile closes up at this angle. Use a smaller angle or depth, or Draft outward.");
                    }
                }
                // (a single direction keeps the extrude's face names; with two, the second half's are F<n>.2.…)
                if (hi > 1e-9 && lo < -1e-9) tool = Csg.Union(Half(hi, f.Id), Half(lo, f.Id + ".2"));
                else tool = Half(hi > 1e-9 ? hi : lo, f.Id);
            }
            ApplyTool(tool, e.IsCut, e.Merge);
            State.Tools[f.Id] = new ToolInfo(tool, e.IsCut, e.Merge);
        }

        void DoRevolve(RevolveFeature r)
        {
            var sk = GetSketch(r.SketchId);
            List<ProfileRegion> regions;
            Vec2 a, dir;
            if (r.Axis != null)
            {
                // an axis from the model (a cylindrical or conical face, an edge, X / Y / Z): in the sketch's plane
                var (o, d) = Resolver.ResolveDir(r.Axis, State);
                var n = sk.Frame.Normal;
                if (Math.Abs(Vec3.Dot(d, n)) > 1e-6 || Math.Abs(Vec3.Dot(o - sk.Frame.Origin, n)) > 1e-6)
                    throw new FeatureException("The axis of revolution must lie in the sketch's plane.");
                a = sk.Frame.ToLocal(o);
                dir = (sk.Frame.ToLocal(o + d) - a).Normalized;
                regions = sk.Regions;
            }
            else
            {
                SkLine? axis = r.AxisEntity != 0 ? sk.Sketch.Entity(r.AxisEntity) as SkLine : null;
                axis ??= sk.Sketch.Entities.OfType<SkLine>().FirstOrDefault(l => l.Construction);
                if (axis == null) throw new FeatureException("Select an axis of revolution: a centerline or line in the sketch, an edge, a cylindrical or conical face, or X / Y / Z.");
                // a solid line too: often the side of a half profile that lies on the axis (part of the profile)
                regions = axis.Construction ? sk.Regions : ProfileExtractor.Extract(sk.Sketch, out _);
                a = sk.Sketch.P(axis.P1);
                dir = (sk.Sketch.P(axis.P2) - a).Normalized;
            }
            if (regions.Count == 0) throw new FeatureException("The sketch has no closed profile.");
            var ang = Positive(r.Angle, "Angle") * Math.PI / 180;
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

        void DoHelical(HelicalSweepFeature h)
        {
            var sk = GetSketch(h.SketchId);
            List<ProfileRegion> regions;
            Vec3 a, dir;
            if (h.Axis != null)
            {
                // an axis from the model (a cylindrical face, an edge, X / Y / Z)
                (a, dir) = Resolver.ResolveDir(h.Axis, State);
                regions = sk.Regions;
            }
            else
            {
                SkLine? axis = h.AxisEntity != 0 ? sk.Sketch.Entity(h.AxisEntity) as SkLine : null;
                axis ??= sk.Sketch.Entities.OfType<SkLine>().FirstOrDefault(l => l.Construction);
                if (axis == null) throw new FeatureException("Select the helix's axis: a centerline in the profile's sketch, a cylindrical face or an edge.");
                regions = axis.Construction ? sk.Regions : ProfileExtractor.Extract(sk.Sketch, out _, new HashSet<int> { axis.Id });
                a = sk.Frame.ToWorld(sk.Sketch.P(axis.P1)); var b = sk.Frame.ToWorld(sk.Sketch.P(axis.P2));
                dir = (b - a).Normalized;
            }
            if (regions.Count == 0) throw new FeatureException("The sketch has no closed profile.");
            var pitch = Positive(h.Pitch, "Pitch");
            var turns = Positive(h.Revolutions, "Revolutions");
            if (h.Reverse) dir = -dir;
            Solid tool;
            try
            {
                // a run-out: a given length, or (a cut) out past the farthest corner of the model's bounds from the axis
                double exitR = 0, exitL = 0;
                if (h.RunOut)
                {
                    if (!string.IsNullOrWhiteSpace(h.RunOutLength)) exitL = Positive(h.RunOutLength, "Run-out length");
                    else if (!h.IsCut) exitL = pitch; // a boss has nothing to run out of: a pitch long
                    else
                    {
                        foreach (var body in State.Bodies)
                        {
                            var bb = body.Solid.Bounds;
                            for (int k = 0; k < 8; k++)
                            {
                                var c = new Vec3((k & 1) == 0 ? bb.Min.X : bb.Max.X, (k & 2) == 0 ? bb.Min.Y : bb.Max.Y, (k & 4) == 0 ? bb.Min.Z : bb.Max.Z) - a;
                                exitR = Math.Max(exitR, (c - dir * Vec3.Dot(c, dir)).Length);
                            }
                        }
                        if (exitR == 0) throw new FeatureException("There is no body to run out of: give the run-out's length.");
                        exitR *= 1.01;
                    }
                }
                var nb = Kernel.Native.NHelical.Sweep(regions, sk.Frame, a, dir, h.LeftHand ? -pitch : pitch, turns * 2 * Math.PI, f.Id, exitR, exitL);
                if (nb.Validate() is { } bad) throw new FeatureException("The helical sweep is not a valid solid: " + bad);
                tool = Kernel.Native.NTess.ToSolid(nb);
            }
            catch (ArgumentException ex) { throw new FeatureException(char.ToUpper(ex.Message[0]) + ex.Message[1..] + "."); }
            ApplyTool(tool, h.IsCut, h.Merge);
            State.Tools[f.Id] = new ToolInfo(tool, h.IsCut, h.Merge);
        }

        void DoThread(ThreadFeature t)
        {
            if (string.IsNullOrEmpty(t.Face.Name)) throw new FeatureException("Select the cylindrical face to thread (a shaft or a hole).");
            var hit = Resolver.NativeFace(t.Face, State) ?? throw new FeatureException("The face to thread could not be found.");
            var face = hit.face;
            if (face.Surface.Kind != SurfaceKind.Cylinder) throw new FeatureException("Threads go on cylindrical faces: select a shaft or a hole.");
            var s = face.Surface;
            var axisDir = s.Axis.Normalized; var axisO = s.Origin; var surfaceR = s.R;
            // a shaft's face looks away from the axis, a hole's towards it
            bool external = face.Sense;
            // the face's extent along the axis
            double hmin = double.MaxValue, hmax = double.MinValue;
            foreach (var c in face.Coedges)
                foreach (var p in c.Edge.Samples(1e-3)) { var hh = Vec3.Dot(p - axisO, axisDir); hmin = Math.Min(hmin, hh); hmax = Math.Max(hmax, hh); }
            // size: a standard one (or the one fitting the face), or the face itself with a given pitch
            double major, pitch;
            if (t.Size == "Custom")
            {
                pitch = Positive(t.Pitch, "Pitch");
                major = external ? surfaceR : surfaceR + 5 * Threads.H(pitch) / 8;
            }
            else
            {
                var std = t.Size == "Auto" ? Threads.Fit(2 * surfaceR, external) : Threads.MetricCoarse.FirstOrDefault(x => x.name == t.Size);
                if (std.name == null) throw new FeatureException($"Unknown thread size '{t.Size}'.");
                pitch = std.p;
                major = std.d / 2;
                if (external && Math.Abs(major - surfaceR) > 0.15 * major)
                    status.Warnings.Add($"The shaft is {2 * surfaceR:0.###} mm across, {std.name} is {std.d:0.###} mm: the thread follows the shaft.");
                if (external) major = surfaceR;
            }
            var clearance = Num(t.Clearance, "Clearance");
            if (clearance < 0) throw new FeatureException("The clearance cannot be negative.");
            var faceLen = hmax - hmin;
            // from the end nearest the picked point (or the other one), running into the face
            var hs = Vec3.Dot(t.Face.Hint - axisO, axisDir);
            bool fromMin = Math.Abs(hs - hmin) <= Math.Abs(hs - hmax);
            if (t.Reverse) fromMin = !fromMin;
            var dir = fromMin ? axisDir : -axisDir;
            var hStart = fromMin ? hmin : hmax;
            // the tool starts a pitch outside the end (the thread runs out cleanly through it)
            var origin = axisO + axisDir * hStart - dir * pitch;
            var radial = s.F.XDir;
            var frame = new Frame(origin, radial, dir, Vec3.Cross(radial, dir));
            var (groove, turn) = Threads.Groove(external, surfaceR, major, pitch, clearance);
            var region = new ProfileRegion(ProfileLoop.Polygon(groove, "G"));
            // in the tool's frame (x: radius, y: along the thread from the tool's start), the start end is at y = P and
            // the far end at y = P + the face's length; the groove's centre runs from y = 0 to y = run, its turns
            // reaching half their widest width either side
            var halfWidth = groove.Max(q => Math.Abs(q.Y));
            bool through = t.End == ThreadEnd.ThroughAll;
            var runout = through ? ThreadRunout.None : t.Runout;
            if (runout == ThreadRunout.Tangent && !external)
            {
                status.Warnings.Add("A thread runs out of the side of a shaft only: a hole's thread ends plainly.");
                runout = ThreadRunout.None;
            }
            // the groove's deepest radius, and the radius the run-out leaves through
            var grooveRoot = external ? groove.Min(q => q.X) : groove.Max(q => q.X);
            var exitR = surfaceR + 0.1 * pitch;
            // the relief groove: 2 P wide at the bottom, a little below the thread's root; the thread ends in its middle
            const double reliefWidth = 2.0;
            // up to the far end: the groove stops short of it, or ends inside the relief against it, or (running out)
            // short of it by the run-out's advance along the axis as well
            double upTo = runout switch
            {
                ThreadRunout.Relief => faceLen - reliefWidth / 2 * pitch,
                ThreadRunout.Tangent => faceLen - halfWidth - 0.05 * pitch
                    - Math.Sqrt(exitR * exitR - grooveRoot * grooveRoot) * pitch / (2 * Math.PI * groove.Average(q => q.X)),
                _ => faceLen - halfWidth - 0.05 * pitch,
            };
            double threadLen = t.End switch
            {
                ThreadEnd.ThroughAll => faceLen + pitch,
                ThreadEnd.Blind when !string.IsNullOrWhiteSpace(t.Length) => Positive(t.Length, "Length"),
                _ => upTo,
            };
            if (threadLen <= 0) throw new FeatureException("The face is too short for this thread.");
            var run = threadLen + pitch;
            double yStart = pitch, yFar = pitch + faceLen;
            bool cosmetic = ThreadMode switch { ThreadDisplay.Modeled => false, ThreadDisplay.Cosmetic => true, _ => t.Cosmetic };
            Solid? tool = null;
            try
            {
                // a clearance too large for the standard crest: the shaft turned down (the hole bored out) over the
                // whole groove first (the groove would otherwise run on below the surface where the turning ends,
                // a closed tunnel), not past the far end unless the thread runs out through it
                if (turn is { } rt && !cosmetic)
                {
                    double r0 = external ? rt : surfaceR - 0.1 * pitch, r1 = external ? surfaceR + 0.1 * pitch : rt;
                    var y1 = run + halfWidth + 0.05 * pitch;
                    if (!through) y1 = Math.Min(y1, yFar);
                    var ring = new ProfileRegion(ProfileLoop.Polygon(new[] { new Vec2(r0, 0), new Vec2(r1, 0), new Vec2(r1, y1), new Vec2(r0, y1) }, "T"));
                    var turned = Kernel.Native.NBuild.Revolve(new[] { ring }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, f.Id + ".Turn");
                    ApplyTool(Kernel.Native.NTess.ToSolid(turned), true, true, warn: false);
                }
                // lead-in chamfers: 45° from the root (a shaft) or to the root (a countersunk hole), past the end and
                // the surface (clean crossings)
                if (t.Chamfer)
                {
                    // from a little past the root: a circle on the root itself would touch the groove's root edge
                    var rootR = external ? groove.Min(q => q.X) - 0.1 * pitch : groove.Max(q => q.X) + 0.1 * pitch;
                    var e = 0.2 * pitch;
                    List<Vec2> Corner(double yEnd, double inward)
                    {
                        // inward: +1 when the part lies at larger y than the end (the start end), -1 at the far end
                        Vec2 P(double r, double y) => new(r, yEnd + inward * y);
                        if (external)
                        {
                            var big = surfaceR + e;
                            return new List<Vec2> { P(rootR - e, -e), P(big, -e), P(big, big - rootR) };
                        }
                        var small = Math.Max(surfaceR - e, 1e-3 * surfaceR);
                        return new List<Vec2> { P(rootR + e, -e), P(small, -e), P(small, rootR - small) };
                    }
                    var ends = through ? new[] { (yStart, 1.0), (yFar, -1.0) } : new[] { (yStart, 1.0) };
                    int k = 0;
                    foreach (var (ye, inward) in ends)
                    {
                        var c = new ProfileRegion(ProfileLoop.Polygon(Corner(ye, inward), "K"));
                        var cone = Kernel.Native.NBuild.Revolve(new[] { c }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, f.Id + ".Chamfer" + (k++ == 0 ? "" : "2"));
                        ApplyTool(Kernel.Native.NTess.ToSolid(cone), true, true, warn: false);
                    }
                }
                // the relief: a ring from a little below the root out past the surface, square against the far end (or
                // a pitch past the thread's end), its thread side sloping at 30° back to the surface
                if (runout == ThreadRunout.Relief)
                {
                    var yb = Math.Min(run + reliefWidth / 2 * pitch, yFar);
                    var ya = yb - reliefWidth * pitch;
                    var rg = external ? grooveRoot - 0.15 * pitch : grooveRoot + 0.15 * pitch;
                    var rs = external ? surfaceR + 0.1 * pitch : Math.Max(surfaceR - 0.1 * pitch, 1e-3 * surfaceR);
                    var slope = Math.Abs(rs - rg) * Math.Tan(Math.PI / 6);
                    var poly = new List<Vec2> { new(rg, ya), new(rg, yb), new(rs, yb), new(rs, ya - slope) };
                    if (!external) poly.Reverse();
                    var rr = new ProfileRegion(ProfileLoop.Polygon(poly, "R"));
                    var relief = Kernel.Native.NBuild.Revolve(new[] { rr }, frame, Vec2.Zero, new Vec2(0, 1), 2 * Math.PI, f.Id + ".Relief");
                    ApplyTool(Kernel.Native.NTess.ToSolid(relief), true, true, warn: false);
                }
                if (!cosmetic)
                {
                    var nb = Kernel.Native.NHelical.Sweep(new[] { region }, frame, origin, dir, t.LeftHand ? -pitch : pitch, run / pitch * 2 * Math.PI, f.Id,
                        exitRadius: runout == ThreadRunout.Tangent ? exitR : 0);
                    if (nb.Validate() is { } bad) throw new FeatureException("The thread is not a valid solid: " + bad);
                    tool = Kernel.Native.NTess.ToSolid(nb);
                }
            }
            catch (ArgumentException ex) { throw new FeatureException(char.ToUpper(ex.Message[0]) + ex.Message[1..] + "."); }
            if (tool == null)
            {
                // cosmetic: the face as it is (the shaft at the major diameter, the hole at the minor), the thread drawn
                // as a helix on it over the thread's length
                State.Cosmetics[f.Id] = new CosmeticThread(axisO + axisDir * hStart, dir, radial, surfaceR, t.LeftHand ? -pitch : pitch,
                    Math.Min(threadLen, faceLen), external);
                return;
            }
            ApplyTool(tool, true, true);
            State.Tools[f.Id] = new ToolInfo(tool, true, true);
        }

        void DoSweep(SweepFeature s)
        {
            if (string.IsNullOrEmpty(s.ProfileSketchId)) throw new FeatureException("Select the profile sketch.");
            if (string.IsNullOrEmpty(s.PathSketchId)) throw new FeatureException("Select the path sketch.");
            if (s.ProfileSketchId == s.PathSketchId) throw new FeatureException("The profile and the path must be different sketches.");
            var prof = GetSketch(s.ProfileSketchId);
            var path = GetSketch(s.PathSketchId);
            var regions = prof.Regions;
            if (regions.Count == 0) throw new FeatureException("The profile sketch has no closed profile.");
            var frame = prof.Frame;
            // the profile's centre (in world space): picks where a closed path or a circle starts
            var centre = Vec3.Zero; int n = 0;
            foreach (var r in regions) foreach (var v in r.Outer.Verts) { centre += frame.ToWorld(v.P); n++; }
            centre /= Math.Max(1, n);
            var segs = SweepPath.Build(path, frame.Plane, centre);
            var t0 = segs[0].StartTangent;
            var dot = Vec3.Dot(t0, frame.Normal);
            if (Math.Abs(dot) < 1 - 1e-6) throw new FeatureException("The profile's sketch must be perpendicular to the path where the path starts.");
            if (dot < 0)
            {
                // the path leaves against the sketch normal: the same profile seen from the other side (x and y swapped)
                frame = new Frame(frame.Origin, frame.YDir, frame.XDir, -frame.Normal);
                regions = regions.Select(r => new ProfileRegion(r.Outer.Transformed(p => new Vec2(p.Y, p.X), true), r.Holes.Select(h => h.Transformed(p => new Vec2(p.Y, p.X), true)))).ToList();
            }
            Solid tool;
            try
            {
                var nb = Kernel.Native.NSweep.Sweep(regions, frame, segs, f.Id);
                if (nb.Validate() is { } bad) throw new FeatureException("The sweep is not a valid solid: " + bad);
                tool = Kernel.Native.NTess.ToSolid(nb);
            }
            catch (ArgumentException ex) { throw new FeatureException(char.ToUpper(ex.Message[0]) + ex.Message[1..] + "."); }
            ApplyTool(tool, s.IsCut, s.Merge);
            State.Tools[f.Id] = new ToolInfo(tool, s.IsCut, s.Merge);
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
        void ApplyTool(Solid tool, bool cut, bool merge, bool warn = true)
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
                if (!any && warn) status.Warnings.Add("The cut does not intersect the model.");
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
                // (not on to the faceted blend, which cannot blend most exact edges: the edge is missing, said)
                if (hit == null) throw new FeatureException($"Edge between '{r.A}' and '{r.B}' could not be found.");
                var (b, e) = hit.Value;
                if (!byBody.TryGetValue(b.Id, out var l)) byBody[b.Id] = l = new();
                if (!l.Contains(e)) l.Add(e);
            }
            var bodies = new List<Body>();
            var errors = new List<string>();
            var applied = new List<(Solid tool, bool cut)>();
            var oneAtATime = new System.Runtime.CompilerServices.StrongBox<bool>();
            var blended = new List<Vec3[]>(); double blendedLength = 0;
            foreach (var b in State.Bodies)
            {
                if (!byBody.TryGetValue(b.Id, out var seeds)) { bodies.Add(b); continue; }
                List<Kernel.Native.NEdge> edges; using (Prof.Time("blend.expand")) edges = NativeBlend.Expand(b.Solid.Native!, seeds, propagate);
                using (Prof.Time("blend.snapshot")) { blended.AddRange(edges.Select(EdgePoints)); blendedLength += edges.Sum(TrueLength); }
                using var _bt = Prof.Time("blend.total");
                var r = NativeBlend.Apply(b.Solid, edges, size, fillet, f.Id, errors, overflow, status.ProblemEdges, applied, chamfer, trimmedOut: oneAtATime);
                bodies.Add(new Body { Id = b.Id, Solid = r });
            }
            // a blend on one body with tools of one kind can be patterned/mirrored like a cut or boss (its copies
            // blend the copies of the geometry it was made on)
            if (errors.Count == 0 && byBody.Count == 1 && applied.Count > 0 && applied.All(t => t.cut == applied[0].cut))
            {
                var tools = applied.Select(t => t.tool).ToList();
                // (joined on first use, by a pattern's worker threads too: exact or failed, never faceted)
                State.Tools[f.Id] = new ToolInfo(() =>
                {
                    if (tools.Count == 1) return tools[0];
                    var old = Csg.NoFallback;
                    Csg.NoFallback = true;
                    try { return Csg.UnionAll(tools); }
                    catch (InvalidOperationException ex) { throw new FeatureException($"The {(fillet ? "fillet" : "chamfer")}'s pieces cannot be joined to be copied: {ex.Message}"); }
                    finally { Csg.NoFallback = old; }
                }, applied[0].cut, true)
                {
                    Blend = new BlendSpec(refs.Select(r => r.Clone()).ToList(), size, fillet, propagate, overflow, chamfer, blended, blendedLength) { OneAtATime = oneAtATime.Value },
                };
            }
            // all or nothing (as SolidWorks): a fillet that cannot be made on every edge fails as a whole,
            // naming the edges (highlighted in the viewport) instead of silently skipping some
            if (errors.Count > 0)
                throw new FeatureException(errors.Count == 1 ? errors[0]
                    : $"{errors.Count} edges cannot be {(fillet ? "filleted" : "chamfered")}: " + BlendErrors(errors));
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

        void DoDraft(DraftFeature d)
        {
            var angle = Positive(d.Angle, "Angle");
            if (angle >= 89) throw new FeatureException("The draft angle must be below 89 degrees.");
            if (d.Faces.Count == 0) throw new FeatureException("Select the faces to draft.");
            var frame = Resolver.ResolvePlane(d.Neutral, State);
            var neutral = Plane.FromPointNormal(frame.ToWorld(0, 0, 0), frame.Normal);
            // a face as the neutral plane: pulled into the part (its outward normal the other way); a plane: along its normal
            var pull = (d.Neutral.Face != null ? -frame.Normal : frame.Normal) * (d.Reverse ? -1 : 1);
            var byBody = new Dictionary<string, HashSet<string>>();
            foreach (var fr in d.Faces)
            {
                var (b, name) = Resolver.ResolveFaceName(fr, State);
                if (!byBody.TryGetValue(b.Id, out var set)) byBody[b.Id] = set = new HashSet<string>();
                set.Add(name);
            }
            List<Body> bodies;
            try
            {
                bodies = State.Bodies.Select(b => byBody.TryGetValue(b.Id, out var names)
                    ? new Body { Id = b.Id, Solid = DraftOp.Apply(b.Solid, names, neutral, pull, angle * Math.PI / 180) } : b).ToList();
            }
            catch (DraftFailedException ex) { throw new FeatureException(ex.Message); }
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
            // suppressed seeds left out, said (the feature itself is suppressed when all of them are)
            var offSeeds = seeds.Where(s => !State.Tools.ContainsKey(s) && Earlier.TryGetValue(s, out var ss) && ss.Suppressed).ToList();
            if (offSeeds.Count > 0 && offSeeds.Count < seeds.Count())
            {
                status.Warnings.Add($"{string.Join(", ", offSeeds.Select(Describe))} {(offSeeds.Count == 1 ? "is" : "are")} suppressed, so left out of the {(f is MirrorFeature ? "mirror" : "pattern")}.");
                seeds = seeds.Except(offSeeds).ToList();
            }
            var everything = new List<Solid>(); bool? cutKind = null; bool mixed = false;
            foreach (var seedId in seeds)
            {
                if (!State.Tools.TryGetValue(seedId, out var tool))
                {
                    // a seed that could have been patterned but was not built: say why, not that it is the wrong kind
                    if (Earlier.TryGetValue(seedId, out var seedStatus) && seedStatus.Error != null)
                        throw new FeatureException($"{Describe(seedId)} failed: {seedStatus.Error}");
                    if (seedStatus is { Suppressed: true }) throw new FeatureException($"{Describe(seedId)} is suppressed.");
                    throw new FeatureException("Only extrude, revolve, sweep, helical sweep, thread, hole, fillet, chamfer, pattern and mirror features can be patterned.");
                }
                var copies = new List<(int i, Solid tool)>();
                var plain = new List<int>();
                // instances whose blend is solved again (their edges are not the seed's edges moved)
                var toSolve = new List<(int i, Body body, List<Kernel.Native.NEdge> edges, double tol)>();
                for (int i = 0; i < transforms.Count; i++)
                {
                    if (skipped.Contains(i)) continue;
                    Cancel.ThrowIfCancellationRequested();
                    Detail?.Invoke($"{Describe(seedId)}, instance {i + 2} of {transforms.Count + 1}");
                    // (a blend whose tools go one at a time: solved again even where its edges are the seed's moved,
                    // its copies cannot be joined)
                    if (tool.Blend is { } bs0 && InstanceEdges(bs0, seeds, transforms[i], i) is { } inst && (!inst.same || bs0.OneAtATime))
                    {
                        toSolve.Add((i, inst.body, inst.edges, inst.tol));
                        continue;
                    }
                    plain.Add(i);
                }
                // the seed's tool moved to each plain instance: independent copies, made in parallel (in order)
                var moved0 = new Solid[plain.Count];
                Parallel.For(0, plain.Count, k => moved0[k] = tool.Tool.Transformed(transforms[plain[k]], $"{f.Id}.{plain[k] + 1}."));
                for (int k = 0; k < plain.Count; k++) copies.Add((plain[k], moved0[k]));
                if (tool.Blend is { } bsO && plain.Count > 0) WarnOverlap(tool.Tool, plain, moved0, bsO.Fillet);
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
                            // (its edges cut short by the seed's own blend: the instances are too close for the size)
                            var into = toSolve[k].edges.Any(e => e.Uses.Any(u => u.Face.Tag.Name.StartsWith(seedId + ".", StringComparison.Ordinal)))
                                ? $"it runs into the {(bs.Fillet ? "fillet" : "chamfer")} of instance 1 (use a smaller size)" : b.error;
                            status.Warnings.Add($"Instance {i + 2} is not {(bs.Fillet ? "filleted" : "chamfered")}: {into}");
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
                // (a blend whose tools go one at a time is not joined: such a pattern cannot be patterned again)
                if (tool.Blend is { OneAtATime: true }) mixed = true;
                else everything.Add(tool.Tool);
                if (copies.Count > 0)
                {
                    var before = State;
                    // exact only: where the copies cannot be applied together they are tried one at a time below (a
                    // faceted fallback of the whole pattern is slow and loses the exact geometry)
                    var oldAll = Csg.NoFallback;
                    try
                    {
                        Csg.NoFallback = true;
                        var all = PatternUnion(copies, transforms, tool.Tool);
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

        /// <summary>
        /// Warns when a copy of a blend's tool overlaps the seed's: copies are not solved against each other (a fillet
        /// round a spoke's foot reaching round the hub past the next spoke's), so the pattern would quietly overlap
        /// them. The seed against each copy covers every spacing of a regular pattern.
        /// </summary>
        void WarnOverlap(Solid seedTool, List<int> plain, Solid[] moved, bool fillet)
        {
            var box = seedTool.Bounds;
            var v0 = Math.Abs(seedTool.Volume());
            var old = Csg.NoFallback;
            Csg.NoFallback = true;
            try
            {
                for (int k = 0; k < plain.Count; k++)
                {
                    if (!box.Overlaps(moved[k].Bounds)) continue;
                    try
                    {
                        var x = Csg.Intersect(seedTool, moved[k]);
                        if (x.IsEmpty || Math.Abs(x.Volume()) <= 1e-6 * v0) continue;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Kernel.Native.NBooleanException) { continue; }
                    var what = fillet ? "fillet" : "chamfer";
                    status.Warnings.Add($"The {what} of instance {plain[k] + 2} overlaps the seed's (the instances are too close for its size): use a smaller {what}.");
                    return;
                }
            }
            finally { Csg.NoFallback = old; }
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
                string? why = null;
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
                        why = ex.Message;
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
                        ? $"Instance {i + 2} is not {kindName}: its blend, cut back where it runs into neighbouring geometry, cannot be joined to the body ({why})."
                        : $"Instance {i + 2} of {Describe(seedId)} is skipped: its blend cannot be applied ({why}).");
                }
            }
            finally { Csg.NoFallback = old; }
        }

        /// <summary>
        /// The union of pattern copies (instance i is the seed tool moved by transforms[i]), as a balanced tree like
        /// <see cref="Csg.UnionAll"/>; a union whose instances are those of a union already made, all moved by one
        /// rigid motion (copies 2-3 are copies 0-1 turned by two steps), is that union moved, its faces renamed to
        /// the instances it stands for, instead of a boolean.
        /// </summary>
        Solid PatternUnion(List<(int i, Solid tool)> copies, IReadOnlyList<Transform> transforms, Solid seed)
        {
            var solids = copies.Select(c => c.tool).ToList();
            // copies apart from each other (or not all exact, or mirrored): nothing to reuse
            if (!ReusePatternUnions || copies.Count < 3 || solids.Any(x => x.Native == null) || copies.Any(c => transforms[c.i].IsMirror)
                || !Enumerable.Range(0, solids.Count - 1).Any(k => solids[k].Bounds.Overlaps(solids[k + 1].Bounds, 1e-6)))
                return Csg.UnionAll(solids);
            // probe points of the seed (its box corners): two placements agree when they move these alike
            var sb = seed.Bounds;
            var probes = new List<Vec3>();
            for (int k = 0; k < 8; k++) probes.Add(new Vec3((k & 1) == 0 ? sb.Min.X : sb.Max.X, (k & 2) == 0 ? sb.Min.Y : sb.Max.Y, (k & 4) == 0 ? sb.Min.Z : sb.Max.Z));
            var tol = 1e-9 * Math.Max(1, sb.Diagonal + transforms.Max(t => t.T.Length));
            bool Same(Transform a, Transform b) => probes.All(q => Vec3.Distance(a.Point(q), b.Point(q)) <= tol);
            var prefix = f.Id + ".";
            var made = new List<(List<int> members, Solid solid)>();
            (List<int> members, Solid solid) Join((List<int> members, Solid solid) a, (List<int> members, Solid solid) b)
            {
                var members = a.members.Concat(b.members).ToList();
                foreach (var (ms, solid) in made)
                {
                    if (ms.Count != members.Count || solid.Native == null) continue;
                    // the motion taking the made union's first instance onto this one's, and every other instance alike
                    var g = transforms[members[0]] * transforms[ms[0]].RigidInverse();
                    if (!Enumerable.Range(0, ms.Count).All(k => Same(g * transforms[ms[k]], transforms[members[k]]))) continue;
                    // faces of instance ms[k] (named "<pattern>.<ms[k] + 1>.…") become those of members[k]
                    var map = Enumerable.Range(0, ms.Count).ToDictionary(k => ms[k] + 1, k => members[k] + 1);
                    bool ok = true;
                    string Rename(string name)
                    {
                        if (name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            int dot = name.IndexOf('.', prefix.Length);
                            if (dot > 0 && int.TryParse(name.AsSpan(prefix.Length, dot - prefix.Length), out var n) && map.TryGetValue(n, out var m))
                                return prefix + m + name[dot..];
                        }
                        ok = false;
                        return name;
                    }
                    var moved = solid.Native.Transformed(g, Rename);
                    if (!ok) break; // a face not named after an instance: make the union instead
                    Interlocked.Increment(ref PatternUnionsReused);
                    return (members, Kernel.Native.NTess.ToSolid(moved));
                }
                var u = Csg.Union(a.solid, b.solid);
                made.Add((members, u));
                return (members, u);
            }
            var level = copies.Select(c => (members: new List<int> { c.i }, solid: c.tool)).ToList();
            while (level.Count > 1)
            {
                var next = new List<(List<int> members, Solid solid)>((level.Count + 1) / 2);
                for (int k = 0; k < level.Count; k += 2) next.Add(k + 1 < level.Count ? Join(level[k], level[k + 1]) : level[k]);
                level = next;
            }
            return level[0].solid;
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

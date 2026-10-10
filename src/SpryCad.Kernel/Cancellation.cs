namespace SpryCad.Kernel;

/// <summary>
/// The rebuild's cancellation, for long kernel operations (booleans, blends) to stop between their stages instead of
/// running on after an edit has superseded them. Ambient (flows into parallel loops); checked only on the thread that
/// runs the operation, between stages, so a cancellation surfaces as an <see cref="OperationCanceledException"/> and
/// is never mistaken for a failure: catch-alls on these paths let it through.
/// </summary>
public static class Cancellation
{
    static readonly AsyncLocal<CancellationToken> _token = new();

    public static CancellationToken Token => _token.Value;

    public static void Check() => _token.Value.ThrowIfCancellationRequested();

    /// <summary>Sets the token for the current flow until the result is disposed.</summary>
    public static Scope Use(CancellationToken token)
    {
        var prev = _token.Value;
        _token.Value = token;
        return new Scope(prev);
    }

    public readonly struct Scope : IDisposable
    {
        readonly CancellationToken _prev;
        internal Scope(CancellationToken prev) { _prev = prev; }
        public void Dispose() => _token.Value = _prev;
    }
}

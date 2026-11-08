namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>
/// Thread-safe single-slot queue for a pending CD+G presentation request.
/// Coalesces repeated requests while one is outstanding: dirty-region consumption
/// is OR-merged (once any request asks to consume dirty regions, the coalesced
/// request does too) and retained-refresh is AND-merged (the coalesced request is
/// a retained refresh only if every merged request was). This preserves the
/// original coordinator semantics exactly, in a small testable unit.
/// </summary>
internal sealed class CdgPresentationRequestQueue
{
    private const string DefaultReason = "cdg pending";

    private readonly Lock _gate = new();
    private bool _pending;
    private bool _consumeDirtyRegions;
    private bool _retainedRefresh;
    private string _reason = DefaultReason;

    /// <summary>
    /// Records a pending presentation request, coalescing it with any request that
    /// is already pending.
    /// </summary>
    public void Enqueue(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        lock (_gate)
        {
            _consumeDirtyRegions |= consumeDirtyRegions;
            _retainedRefresh = _pending
                ? _retainedRefresh && retainedRefresh
                : retainedRefresh;
            _reason = reason;
            _pending = true;
        }
    }

    /// <summary>
    /// If a request is pending, atomically clears it and returns its coalesced
    /// parameters. Returns <see langword="false"/> (with default outputs) when no
    /// request is pending.
    /// </summary>
    public bool TryDequeue(out bool consumeDirtyRegions, out bool retainedRefresh, out string reason)
    {
        lock (_gate)
        {
            if (!_pending)
            {
                consumeDirtyRegions = false;
                retainedRefresh = false;
                reason = DefaultReason;
                return false;
            }

            consumeDirtyRegions = _consumeDirtyRegions;
            retainedRefresh = _retainedRefresh;
            reason = _reason;
            ClearNoLock();
            return true;
        }
    }

    /// <summary>Clears any pending request.</summary>
    public void Clear()
    {
        lock (_gate)
            ClearNoLock();
    }

    private void ClearNoLock()
    {
        _pending = false;
        _consumeDirtyRegions = false;
        _retainedRefresh = false;
        _reason = DefaultReason;
    }
}

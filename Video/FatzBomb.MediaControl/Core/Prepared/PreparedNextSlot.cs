namespace FatzBomb.MediaControl.Core.Prepared;

/// <summary>
/// A slot state change that the owner publishes outside the slot lock.
/// </summary>
internal readonly record struct PreparedNextChange(PreparedNextInfo Previous, PreparedNextInfo Current);

/// <summary>
/// Accepted preparation work. <see cref="Token"/> is cancelled when the candidate is cleared,
/// replaced, or the caller's token is cancelled.
/// </summary>
internal sealed record PreparedNextTicket(long Generation, MediaInputSource Source, CancellationToken Token);

/// <summary>
/// A claimed transition of the prepared item into the current media.
/// </summary>
internal sealed record PreparedNextTransition(long Generation, MediaInputSource Source, MediaAnalysisResult? Analysis);

/// <summary>
/// Depth-one prepared-next state machine.
/// </summary>
/// <remarks>
/// States: Empty -> Analyzing -> Ready -> Transitioning -> Empty, with Stale entered from Ready
/// when the prepared file's identity changed before a transition. Every accepted candidate gets a
/// new generation; completions from older generations are ignored, so a replaced or cleared
/// preparation can never resurrect the slot. The slot owns no media resources: replacing or
/// clearing it only cancels analysis and drops references.
/// </remarks>
internal sealed class PreparedNextSlot : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<MediaInputSource, string?> _identityProvider;
    private PreparedNextInfo _info = PreparedNextInfo.Empty;
    private CancellationTokenSource? _cts;
    private string? _identity;
    private long _generation;
    private bool _disposed;

    public PreparedNextSlot(Func<MediaInputSource, string?>? identityProvider = null)
    {
        _identityProvider = identityProvider ?? CreateIdentity;
    }

    public PreparedNextInfo Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _info;
            }
        }
    }

    internal long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// Accepts a candidate, cancelling and replacing any previous one. The slot never holds more
    /// than one item.
    /// </summary>
    public PreparedNextTicket Begin(MediaInputSource source, CancellationToken cancellationToken, out PreparedNextChange change)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The identity is a file stat, which can block on a slow share: it is taken outside the
        // lock that UI-thread getters take.
        var identity = _identityProvider(source);
        CancellationTokenSource? previous;
        PreparedNextTicket ticket;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _cts;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _generation++;
            _identity = identity;
            change = SetInfo(new PreparedNextInfo(PreparedNextState.Analyzing, source, null));
            ticket = new PreparedNextTicket(_generation, source, _cts.Token);
        }

        CancelAndDispose(previous);
        return ticket;
    }

    /// <summary>Moves a current Analyzing candidate to Ready.</summary>
    public bool TryComplete(long generation, MediaAnalysisResult? analysis, out PreparedNextChange change)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _info.State != PreparedNextState.Analyzing)
            {
                change = default;
                return false;
            }

            change = SetInfo(new PreparedNextInfo(PreparedNextState.Ready, _info.Source, analysis));
            return true;
        }
    }

    /// <summary>Empties the slot when a current Analyzing candidate failed or was cancelled.</summary>
    public bool TryFail(long generation, out PreparedNextChange change)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed || generation != _generation || _info.State != PreparedNextState.Analyzing)
            {
                change = default;
                return false;
            }

            cts = _cts;
            _cts = null;
            _identity = null;
            change = SetInfo(PreparedNextInfo.Empty);
        }

        CancelAndDispose(cts);
        return true;
    }

    /// <summary>
    /// Discards the prepared item and cancels its analysis. Returns <see langword="false"/> when the
    /// slot was already empty.
    /// </summary>
    public bool Clear(out PreparedNextChange change)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_info.State == PreparedNextState.Empty)
            {
                change = default;
                return false;
            }

            cts = _cts;
            _cts = null;
            _identity = null;
            _generation++;
            change = SetInfo(PreparedNextInfo.Empty);
        }

        CancelAndDispose(cts);
        return true;
    }

    /// <summary>Returns whether the slot is Ready, optionally for a specific generation.</summary>
    public bool IsReady(long? generation = null)
    {
        lock (_gate)
        {
            return _info.State == PreparedNextState.Ready
                && (generation is null || generation == _generation);
        }
    }

    /// <summary>
    /// Claims the Ready item for transition. A Ready item whose file identity changed becomes Stale
    /// instead, and the claim fails.
    /// </summary>
    public bool TryBeginTransition(
        long? expectedGeneration,
        out PreparedNextTransition? transition,
        out PreparedNextChange? change)
    {
        transition = null;
        change = null;
        MediaInputSource source;
        long generation;
        lock (_gate)
        {
            if (_disposed
                || _info.State != PreparedNextState.Ready
                || _info.Source is null
                || (expectedGeneration is { } expected && expected != _generation))
            {
                return false;
            }

            source = _info.Source;
            generation = _generation;
        }

        // Stat outside the lock (see Begin), then claim only if the slot did not move meanwhile.
        var currentIdentity = _identityProvider(source);
        lock (_gate)
        {
            if (_disposed || generation != _generation || _info.State != PreparedNextState.Ready)
                return false;

            if (!string.Equals(_identity, currentIdentity, StringComparison.Ordinal))
            {
                change = SetInfo(new PreparedNextInfo(PreparedNextState.Stale, _info.Source, _info.Analysis));
                return false;
            }

            transition = new PreparedNextTransition(_generation, _info.Source, _info.Analysis);
            change = SetInfo(new PreparedNextInfo(PreparedNextState.Transitioning, _info.Source, _info.Analysis));
            return true;
        }
    }

    /// <summary>
    /// Finishes a claimed transition. The slot empties whether or not the load succeeded so a
    /// failing item can never be retried silently; a newer candidate is left untouched.
    /// </summary>
    public bool TryCompleteTransition(long generation, out PreparedNextChange change)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed || generation != _generation || _info.State != PreparedNextState.Transitioning)
            {
                change = default;
                return false;
            }

            cts = _cts;
            _cts = null;
            _identity = null;
            change = SetInfo(PreparedNextInfo.Empty);
        }

        CancelAndDispose(cts);
        return true;
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            cts = _cts;
            _cts = null;
            _identity = null;
            _generation++;
            _info = PreparedNextInfo.Empty;
        }

        CancelAndDispose(cts);
    }

    /// <summary>
    /// Local-file identity: normalized full path, length, and UTC last-write ticks, matching the
    /// media-analysis cache key so a prepared analysis is never applied to a changed file.
    /// Non-file sources have no identity and never become stale.
    /// </summary>
    internal static string? CreateIdentity(MediaInputSource source)
    {
        if (!source.IsFile)
            return null;

        try
        {
            var info = new FileInfo(source.FilePath!);
            return info.Exists
                ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : $"{info.FullName}|missing";
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return $"{source.FilePath}|unavailable";
        }
    }

    private PreparedNextChange SetInfo(PreparedNextInfo next)
    {
        PreparedNextInfo previous = _info;
        _info = next;
        return new PreparedNextChange(previous, next);
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (AggregateException)
        {
            // Cancellation callbacks belong to analysis work that is being discarded.
        }

        cts.Dispose();
    }
}

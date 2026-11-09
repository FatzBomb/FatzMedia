namespace FatzMedia.Video.Core.Network;

/// <summary>The result of one network open attempt.</summary>
internal enum NetworkAttemptResult
{
    /// <summary>Connected and opened.</summary>
    Opened,

    /// <summary>Could not connect (refused, timed out, HTTP error): worth another attempt.</summary>
    Failed,

    /// <summary>Connected, but the content cannot be played (no usable stream): not retried.</summary>
    Unusable,
}

/// <summary>How a bounded network open ended.</summary>
internal enum NetworkOpenOutcome
{
    /// <summary>An attempt connected.</summary>
    Opened,

    /// <summary>Every attempt failed, or one connected to content that cannot be played.</summary>
    Failed,

    /// <summary>The open was cancelled (Close, Dispose) during an attempt or a retry delay.</summary>
    Cancelled,
}

/// <summary>
/// Runs a network open under the host's <see cref="NetworkRetryPolicy"/> (Stage 039A): up to
/// <see cref="NetworkRetryPolicy.MaxAttempts"/> attempts with the policy's delays between them.
/// Unlike the 014A audio open, the delays and the attempts are cancellable.
/// </summary>
internal static class NetworkOpenRetry
{
    /// <param name="policy">The bounded retry policy.</param>
    /// <param name="cancellation">Cancels the attempt in progress and any pending delay.</param>
    /// <param name="attempt">Makes one attempt (1-based number).</param>
    /// <param name="beforeRetry">Called before each retry delay with the retry number (1-based) and the delay.</param>
    /// <param name="wait">Waits for a delay; returns <see langword="true"/> when cancelled. Defaults to the token's wait handle.</param>
    /// <param name="attempts">The number of attempts made.</param>
    public static NetworkOpenOutcome Run(
        NetworkRetryPolicy policy,
        CancellationToken cancellation,
        Func<int, NetworkAttemptResult> attempt,
        Action<int, TimeSpan>? beforeRetry,
        out int attempts,
        Func<TimeSpan, CancellationToken, bool>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(attempt);
        wait ??= static (delay, token) => token.WaitHandle.WaitOne(delay);

        attempts = 0;
        for (var number = 1; number <= policy.MaxAttempts; number++)
        {
            if (cancellation.IsCancellationRequested)
                return NetworkOpenOutcome.Cancelled;

            if (number > 1)
            {
                TimeSpan delay = policy.GetDelayBeforeRetry(number - 1);
                beforeRetry?.Invoke(number - 1, delay);
                if (delay > TimeSpan.Zero && wait(delay, cancellation))
                    return NetworkOpenOutcome.Cancelled;
                if (cancellation.IsCancellationRequested)
                    return NetworkOpenOutcome.Cancelled;
            }

            attempts = number;
            switch (attempt(number))
            {
                case NetworkAttemptResult.Opened:
                    return NetworkOpenOutcome.Opened;
                case NetworkAttemptResult.Unusable:
                    return NetworkOpenOutcome.Failed;
            }
        }

        return cancellation.IsCancellationRequested ? NetworkOpenOutcome.Cancelled : NetworkOpenOutcome.Failed;
    }
}

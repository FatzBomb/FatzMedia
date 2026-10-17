#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Bounded, explicit retry policy for network opens. The default performs a single attempt;
/// retries happen only when a host opts in. There is no infinite retry.
/// </summary>
public sealed record NetworkRetryPolicy
{
    /// <summary>Gets the hard upper bound on retries.</summary>
    public const int MaximumRetries = 10;

    /// <summary>Gets the default policy: one attempt, no retries, 15 s connect timeout.</summary>
    public static NetworkRetryPolicy Default { get; } = new();

    /// <summary>Gets the number of retries after the first attempt, clamped to <c>0..10</c>.</summary>
    public int MaxRetries
    {
        get;
        init => field = Math.Clamp(value, 0, MaximumRetries);
    }

    /// <summary>Gets the delay before the first retry (clamped to <c>0..30 s</c>).</summary>
    public TimeSpan InitialDelay
    {
        get;
        init => field = ClampTime(value, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the multiplier applied to each subsequent delay (clamped to <c>1..4</c>).</summary>
    public double BackoffMultiplier
    {
        get;
        init => field = double.IsFinite(value) ? Math.Clamp(value, 1, 4) : 2;
    } = 2;

    /// <summary>Gets the cap for any single delay (clamped to <c>0..60 s</c>).</summary>
    public TimeSpan MaxDelay
    {
        get;
        init => field = ClampTime(value, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the per-attempt connect timeout (clamped to <c>1..60 s</c>).</summary>
    public TimeSpan ConnectTimeout
    {
        get;
        init => field = ClampTime(value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
    } = TimeSpan.FromSeconds(15);

    /// <summary>Gets the total number of attempts.</summary>
    public int MaxAttempts => MaxRetries + 1;

    /// <summary>
    /// Gets the delay before retry <paramref name="retryNumber"/> (1-based).
    /// </summary>
    public TimeSpan GetDelayBeforeRetry(int retryNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);
        var seconds = InitialDelay.TotalSeconds * Math.Pow(BackoffMultiplier, retryNumber - 1);
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}

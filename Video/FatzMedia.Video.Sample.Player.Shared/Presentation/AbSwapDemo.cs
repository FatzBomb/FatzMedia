using System.Globalization;

namespace FatzMedia.Video.Sample.Player.Shared.Presentation;

/// <summary>
/// The A/B swap demo's command line (Stage 051A; the WPF player hosts the demo window):
/// <c>--ab-demo=&lt;file or folder&gt;</c> (repeatable), <c>--ab-swaps=N</c> (default 20),
/// <c>--ab-interval=seconds</c> between swaps (default 3), <c>--ab-mode=composited|native</c>
/// (default composited), <c>--ab-collapsed</c> (hide the waiting surface with Collapsed, the
/// counter-check: its first frame is rendered at the wrong size) and <c>--ab-exit</c> (close the
/// player when the demo is done, for scripts).
/// </summary>
public sealed record AbSwapDemoOptions
{
    public const int DefaultSwaps = 20;
    public const double DefaultIntervalSeconds = 3;
    public const int MaxSwaps = 1000;

    /// <summary>Gets the files or folders given with <c>--ab-demo=</c>; the demo runs when there is one.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public int Swaps { get; init; } = DefaultSwaps;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(DefaultIntervalSeconds);

    /// <summary>Gets whether the surfaces use the native window instead of WPF composition.</summary>
    public bool Native { get; init; }

    /// <summary>Gets whether the waiting surface is Collapsed instead of Hidden.</summary>
    public bool Collapsed { get; init; }

    /// <summary>Gets whether the player closes when the demo is done.</summary>
    public bool ExitWhenDone { get; init; }

    public bool IsEnabled => Sources.Count > 0;

    public static AbSwapDemoOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var mode = PlayerStartupOptions.Values(args, "--ab-mode=", trimQuotes: false).LastOrDefault();
        return new AbSwapDemoOptions
        {
            Sources = [.. PlayerStartupOptions.Values(args, "--ab-demo=").Select(static value => value.Trim()).Where(static value => value.Length > 0)],
            Swaps = PlayerStartupOptions.Values(args, "--ab-swaps=", trimQuotes: false)
                .Select(static value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var swaps) ? Math.Clamp(swaps, 1, MaxSwaps) : (int?)null)
                .LastOrDefault(static swaps => swaps is not null) ?? DefaultSwaps,
            Interval = TimeSpan.FromSeconds(PlayerStartupOptions.Values(args, "--ab-interval=", trimQuotes: false)
                .Select(static value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) ? Math.Clamp(seconds, 0.5, 60) : (double?)null)
                .LastOrDefault(static seconds => seconds is not null) ?? DefaultIntervalSeconds),
            Native = mode is not null && mode.Trim().Equals("native", StringComparison.OrdinalIgnoreCase),
            Collapsed = PlayerStartupOptions.HasFlag(args, "--ab-collapsed"),
            ExitWhenDone = PlayerStartupOptions.HasFlag(args, "--ab-exit"),
        };
    }

    /// <summary>
    /// Gets the clips to swap between: each source file, and each folder's files with a video
    /// extension (sorted by name, not recursive). <paramref name="listFolder"/> lists a folder's
    /// files, or returns null when <paramref name="isFolder"/> said it is one but it cannot be read.
    /// </summary>
    public IReadOnlyList<string> ResolveClips(Func<string, bool> isFolder, Func<string, IEnumerable<string>?> listFolder)
    {
        ArgumentNullException.ThrowIfNull(isFolder);
        ArgumentNullException.ThrowIfNull(listFolder);
        List<string> clips = [];
        foreach (var source in Sources)
        {
            if (!isFolder(source))
            {
                clips.Add(source);
                continue;
            }

            clips.AddRange((listFolder(source) ?? [])
                .Where(static file => VideoExtensions.Contains(Path.GetExtension(file)))
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        return clips;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".wmv",
    };
}

/// <summary>Sums up the demo's swaps for its last log line (Stage 051A).</summary>
public static class AbSwapDemoSummary
{
    /// <summary>
    /// Gets <c>{done} of {planned} swaps, {missed} without a first frame; ready after min {a} ms,
    /// median {b} ms, max {c} ms</c> (invariant culture) from the load-to-ready times of the swaps
    /// that got their first frame.
    /// </summary>
    public static string Describe(int planned, IReadOnlyList<double> readyMilliseconds, int missed)
    {
        ArgumentNullException.ThrowIfNull(readyMilliseconds);
        var text = string.Create(CultureInfo.InvariantCulture, $"{readyMilliseconds.Count} of {planned} swaps, {missed} without a first frame");
        if (readyMilliseconds.Count == 0)
            return text;

        double[] sorted = [.. readyMilliseconds.Order()];
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"{text}; ready after min {sorted[0]:F0} ms, median {median:F0} ms, max {sorted[^1]:F0} ms");
    }
}

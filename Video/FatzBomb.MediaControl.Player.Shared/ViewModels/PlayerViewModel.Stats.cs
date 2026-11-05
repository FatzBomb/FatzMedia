using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Player.Shared.ViewModels;

/// <summary>The per-player stats line (Stage 046A slice 3: one line in every test player).</summary>
public partial class PlayerViewModel
{
    private readonly PresentedFrameCounter _frameCounter = new();

    /// <summary>
    /// Gets the stats line (<see cref="PlayerStatsLine.Format"/>): state, presented frames per
    /// second, process CPU and memory, and what the host's surface does. Empty until the host's
    /// first <see cref="UpdateStats"/> after the engine exists.
    /// </summary>
    [ObservableProperty]
    public partial string StatsText { get; private set; } = string.Empty;

    /// <summary>
    /// Refreshes <see cref="StatsText"/> with <paramref name="process"/> (one reading shared by the
    /// window's players) and <paramref name="surface"/> (the host's surface description, for example
    /// <see cref="PlayerStatsLine.DescribeSurface"/>). Call about once a second on the UI thread.
    /// </summary>
    public void UpdateStats(ProcessSample process, string surface)
    {
        if (Player is null)
            return;

        StatsText = PlayerStatsLine.Format(Label ?? "Player", PlaybackState, _frameCounter.TakeFramesPerSecond(), process, surface);

        // Every fifth line goes to the debug log too, so scripts (scripts/smoke-backlog.ps1) can read
        // fps and the presentation path without screen scraping.
        if (++_statsTraceCount % 5 == 1)
            Trace.WriteLine($"[Stats] {StatsText}");
    }

    private int _statsTraceCount;

    // The status line goes to the debug log as well ("Loaded: ...", "Could not load: ..."), for scripts.
    partial void OnStatusTextChanged(string value) =>
        Trace.WriteLine(Label is { Length: > 0 } label ? $"[Status] {label} {value}" : $"[Status] {value}");
}

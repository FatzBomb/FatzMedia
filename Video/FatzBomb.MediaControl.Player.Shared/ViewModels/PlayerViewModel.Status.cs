using System.Diagnostics;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Player.Shared.ViewModels;

/// <summary>
/// The 200 ms position refresh. <see cref="MediaControlPlayer.State"/>, <see cref="MediaControlPlayer.Duration"/>
/// and <see cref="MediaControlPlayer.Position"/> are read on the thread pool, never on the UI
/// thread (ENG-035: a network source can hold them), and a tick is skipped while a read is still
/// out or a seek drag is active. A read slower than 100 ms, blocked for a second, or failing (once
/// until a read succeeds) is logged as a warning.
/// </summary>
public partial class PlayerViewModel
{
    private static readonly TimeSpan PositionRefreshInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SlowStatusRead = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BlockedStatusRead = TimeSpan.FromSeconds(1);

    private Task? _statusRead;
    private long _statusReadStarted;
    private bool _statusReadStallLogged;
    private bool _statusReadFailureLogged;

    /// <summary>Gets the transport state from the latest status read.</summary>
    public MediaPlayerState PlaybackState
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(IsPlaying));
        }
    }

    /// <summary>Gets whether the latest status read found the player playing.</summary>
    public bool IsPlaying => PlaybackState == MediaPlayerState.Playing;

    // A drag owns the seek bar: no reads while it scrubs (they would only be thrown away).
    private void OnPositionTimerTick()
    {
        if (!_isSeekPreviewActive)
            RefreshPlaybackPosition();
    }

    /// <summary>Starts a status read unless one is out (UI thread); the result is applied on the UI thread.</summary>
    private void RefreshPlaybackPosition()
    {
        if (_disposed)
            return;

        if (Player is not { } player)
        {
            ApplyStatus(null, default);
            return;
        }

        if (_statusRead is { IsCompleted: false })
        {
            // The previous read has not returned: skip this one (logged once) instead of piling up.
            TimeSpan blocked = Stopwatch.GetElapsedTime(_statusReadStarted);
            if (!_statusReadStallLogged && blocked > BlockedStatusRead)
            {
                _statusReadStallLogged = true;
                HandleDiagnostic(MediaDiagnosticLevel.Warning, "Player", $"Status read blocked for {blocked.TotalSeconds:F1} s (State/Duration/Position); the UI stays live");
            }

            return;
        }

        _statusReadStarted = Stopwatch.GetTimestamp();
        _statusRead = ReadStatusAsync(player, _statusReadStarted);
    }

    private async Task ReadStatusAsync(MediaControlPlayer player, long started)
    {
        PlaybackStatus status;
        try
        {
            status = await Task.Run(() => new PlaybackStatus(player.State, player.Duration.TotalSeconds, player.Position.TotalSeconds)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Logged once until a read succeeds again: a failing read repeats every tick.
            RunOnUiThread(() =>
            {
                _statusReadStallLogged = false;
                if (_statusReadFailureLogged)
                    return;

                _statusReadFailureLogged = true;
                HandleDiagnostic(MediaDiagnosticLevel.Warning, "Player", $"Status read failed: {ex.GetType().Name}: {ex.Message}");
            });
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        RunOnUiThread(() =>
        {
            if (elapsed > SlowStatusRead)
                HandleDiagnostic(MediaDiagnosticLevel.Warning, "Player", $"Status read took {elapsed.TotalMilliseconds:F0} ms (State/Duration/Position)");
            _statusReadStallLogged = false;
            _statusReadFailureLogged = false;
            if (ReferenceEquals(player, Player))
                ApplyStatus(player, status);
        });
    }

    private void ApplyStatus(MediaControlPlayer? player, PlaybackStatus status)
    {
        if (player is not null)
            PlaybackState = status.State;
        else
            PlaybackState = MediaPlayerState.Null;

        // A drag or a recent seek owns the seek bar: the engine may not be there yet.
        if (_isSeekPreviewActive || DateTime.UtcNow < _suppressPositionRefresh)
            return;

        if (player is null)
        {
            PositionSeconds = 0;
            SeekPositionSeconds = 0;
            DurationSeconds = 0;
            return;
        }

        DurationSeconds = status.Duration;
        PositionSeconds = DurationSeconds > 0 ? Math.Clamp(status.Position, 0, DurationSeconds) : CleanSeconds(status.Position);
        SeekPositionSeconds = PositionSeconds;
    }

    private readonly record struct PlaybackStatus(MediaPlayerState State, double Duration, double Position);
}

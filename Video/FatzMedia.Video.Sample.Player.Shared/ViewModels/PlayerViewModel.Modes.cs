using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Player modes (Stage 043A): the mode the engine is created in (<c>--mode=</c>) and switching on
/// the fly with <see cref="PlayerCore.SetModeAsync"/>. Background mode forces looping, so
/// <see cref="LoopPlayback"/> follows the engine's <see cref="PlayerCore.LoopMedia"/>.
/// </summary>
public partial class PlayerViewModel
{
    private bool _syncingFromPlayer;

    public IReadOnlyList<MediaPlayerMode> PlayerModes { get; } = Enum.GetValues<MediaPlayerMode>();

    /// <summary>
    /// Gets or sets the player's mode. Before the engine exists it is the mode it is created in;
    /// afterwards a change switches the engine (a short gap, like a seek).
    /// </summary>
    public MediaPlayerMode PlayerMode
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _syncingFromPlayer)
                return;

            if (Player is { } player && player.Mode != value)
                _ = SwitchModeAsync(player, value);
        }
    }

    /// <summary>Gets whether a mode switch is running.</summary>
    public bool IsSwitchingMode
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
                OnPropertyChanged(nameof(CanSwitchMode));
        }
    }

    /// <summary>Gets whether the mode can be switched now (an engine exists and no switch is running).</summary>
    public bool CanSwitchMode => Player is not null && !IsSwitchingMode;

    private void InitializeModeControls() => PlayerMode = Options.Mode;

    private void StartModeTracking(PlayerCore player)
    {
        player.ModeChanged += OnPlayerModeChanged;
        player.LoopMediaChanged += OnPlayerLoopMediaChanged;
    }

    private void StopModeTracking(PlayerCore player)
    {
        player.ModeChanged -= OnPlayerModeChanged;
        player.LoopMediaChanged -= OnPlayerLoopMediaChanged;
    }

    private async Task SwitchModeAsync(PlayerCore player, MediaPlayerMode mode)
    {
        IsSwitchingMode = true;
        StatusText = $"Switching to {mode} mode...";
        HandleDiagnostic(MediaDiagnosticLevel.Info, "Player", $"Switching to {mode} mode");
        try
        {
            var switched = await player.SetModeAsync(mode);
            StatusText = switched ? $"{player.Mode} mode" : $"Could not switch to {mode} mode (see the log).";
            if (!switched)
                HandleDiagnostic(MediaDiagnosticLevel.Warning, "Player", $"Could not switch to {mode} mode");
        }
        catch (Exception ex)
        {
            HandleDiagnostic(MediaDiagnosticLevel.Error, "Player", $"Mode switch failed: {ex.Message}");
        }
        finally
        {
            IsSwitchingMode = false;
            if (!_disposed && ReferenceEquals(player, Player))
                SyncFromPlayer(player);
        }
    }

    private void OnPlayerModeChanged(object? sender, MediaValueChangedEventArgs<MediaPlayerMode> e)
    {
        if (sender is PlayerCore player)
            RunOnUiThread(() => SyncFromPlayer(player));
    }

    private void OnPlayerLoopMediaChanged(object? sender, MediaValueChangedEventArgs<bool> e)
        => RunOnUiThread(() => SetLoopFromPlayer(e.CurrentValue));

    /// <summary>Shows the engine's mode and loop setting without sending them back to it.</summary>
    private void SyncFromPlayer(PlayerCore player)
    {
        if (!ReferenceEquals(player, Player))
            return;

        _syncingFromPlayer = true;
        try
        {
            PlayerMode = player.Mode;
        }
        finally
        {
            _syncingFromPlayer = false;
        }

        SetLoopFromPlayer(player.LoopMedia);
        RefreshCommandStates();
    }

    private void SetLoopFromPlayer(bool loop)
    {
        if (LoopPlayback == loop)
            return;

        _syncingFromPlayer = true;
        try
        {
            LoopPlayback = loop;
        }
        finally
        {
            _syncingFromPlayer = false;
        }
    }
}

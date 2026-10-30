using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Stage 043A player modes and the presentation options of Full mode. Everything a mode changes in
/// the session graph is decided here, from the configuration that <c>CreateSessionGraph</c>
/// builds with, so <see cref="SetModeAsync"/> rebuilds the graph for another mode.
/// </summary>
public partial class MediaControlPlayer
{
    private MediaPlayerConfiguration _configuration;
    private IReadOnlyList<string> _nativeLibraryPaths = [];
    private bool? _autoBackground;
    private bool _transparentBackground;

    /// <summary>
    /// Raised on the control queue after <see cref="SetModeAsync"/> switched to another mode, after
    /// the events of the reopen (Stage 043A slice 2).
    /// </summary>
    public event EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>? ModeChanged;

    /// <summary>
    /// Gets the player's mode: the one it was created in (see <see cref="MediaPlayerConfiguration.Mode"/>)
    /// until <see cref="SetModeAsync"/> switches it.
    /// </summary>
    public MediaPlayerMode Mode => _configuration.Mode;

    /// <summary>
    /// Raised on the control queue after <see cref="ModeChanged"/> when a switch turned
    /// presentation on or off (into or out of <see cref="MediaPlayerMode.Audio"/>; Stage 046A).
    /// </summary>
    public event EventHandler<MediaValueChangedEventArgs<bool>>? HasPresentationChanged;

    /// <summary>
    /// Gets whether a renderer presents to the attached surfaces: <see langword="false"/> in
    /// <see cref="MediaPlayerMode.Audio"/> mode, which keeps the surfaces but presents nothing.
    /// </summary>
    public bool HasPresentation => MediaPlayerModePolicy.UsesPresentation(Mode);

    /// <summary>
    /// Switches the player to <paramref name="mode"/> on the control queue (Stage 043A slice 2): the
    /// new mode's graph is built and the old one freed, then the open media is reopened at its
    /// position and in its transport state (playing, paused or stopped), so there is a short gap,
    /// like a seek. The host's settings carry over: loop (Background mode's forced loop ends with
    /// it), volume, pitch, tempo, normalization, equalizer, sync offsets, display options and the
    /// prepared-next item. Surfaces are kept while in Audio mode and attached again on return.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the player is in <paramref name="mode"/>, including when it already
    /// was (nothing is rebuilt). Media the new mode does not accept is not reopened: the player ends
    /// closed with a <c>PlayerMode</c> warning, and the switch still returns <see langword="true"/>.
    /// <see langword="false"/> when the new mode could not be built; the player is rebuilt in its
    /// previous mode.
    /// </returns>
    public Task<bool> SetModeAsync(MediaPlayerMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown player mode.");

        return EnqueueControlOperation(() => SwitchMode(mode));
    }

    /// <summary>
    /// Gets or sets whether loads search for sidecar and archive backgrounds (default
    /// <see langword="true"/>). <see langword="false"/> skips the search; an explicit background
    /// passed to <c>Load</c> still applies. Audio mode never loads backgrounds and reads
    /// <see langword="false"/>.
    /// </summary>
    public bool AutoBackground
    {
        get => File.BackgroundAutoDetect;
        set
        {
            _autoBackground = value;
            File.BackgroundAutoDetect = value;
        }
    }

    /// <summary>
    /// Gets or sets whether the output is transparent where nothing is drawn (default
    /// <see langword="false"/>, black): no background, letterbox bars, and the CD+G background
    /// colour. An app layer below shows through on hosts that composite (WPF composited,
    /// Avalonia, frame sinks).
    /// </summary>
    public bool TransparentBackground
    {
        get => _transparentBackground;
        set
        {
            if (_transparentBackground == value)
                return;

            _transparentBackground = value;
            if (_disposed)
                return;

            ApplyTransparentBackground();
            RequestPresentationRefresh();
        }
    }

    /// <summary>
    /// Audio mode creates no presentation coordinator (no renderer, GPU device or surfaces); the
    /// other modes ask the platform for one.
    /// </summary>
    private IMediaPresentationCoordinator CreatePresentationCoordinator(MediaPlayerConfiguration configuration) =>
        MediaPlayerModePolicy.UsesPresentation(configuration.Mode)
            ? _platformFactory.CreatePresentationCoordinator(_mediaEngine, configuration)
            : new AudioOnlyPresentationCoordinator();

    /// <summary>Background mode has no audio output, so it runs no audio device monitor.</summary>
    private IAudioDeviceMonitor? ResolveAudioDeviceMonitor() =>
        MediaPlayerModePolicy.UsesAudioOutput(Mode) ? _serviceProvider.GetService<IAudioDeviceMonitor>() : null;

    /// <summary>
    /// Applies the mode and the options the host set to a newly built session graph (creation,
    /// Reset). Options never set leave the graph's defaults untouched.
    /// </summary>
    private void ApplyModeToSessionGraph()
    {
        if (_autoBackground is { } autoBackground)
            File.BackgroundAutoDetect = autoBackground;
        if (_transparentBackground)
            ApplyTransparentBackground();
        Controls.ForcesLoop = MediaPlayerModePolicy.ForcesLoop(Mode);
    }

    private void ApplyTransparentBackground()
    {
        CdgVisuals.ForceTransparentBackground = _transparentBackground;
        if (_presentationCoordinator is MediaPresentationCoordinator coordinator)
            coordinator.TransparentBackground = _transparentBackground;
    }

    /// <summary>
    /// Runs on the control queue, so a Reset or Dispose that arrives meanwhile waits for it (bounded,
    /// see <see cref="Dispose()"/>). Never calls through the admission gate: the caller is already in
    /// the queue.
    /// </summary>
    private bool SwitchMode(MediaPlayerMode mode)
    {
        MediaPlayerMode previousMode = Mode;
        if (mode == previousMode)
            return true;

        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        ModeSwitchState state = CaptureModeSwitchState();
        MediaPlayerConfiguration previousConfiguration = _configuration;
        var switched = true;
        try
        {
            RebuildSessionGraph(previousConfiguration with { Mode = mode }, _nativeLibraryPaths, keepOperationQueue: true, () => RestoreSettings(state));
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Error,
                MediaPlayerModePolicy.DiagnosticCategory,
                $"Could not switch to {mode} mode ({ex.Message}); the player stays in {previousMode} mode.",
                innerException: ex);

            // The old graph is gone: build the previous mode again. If that fails too, the
            // exception faults the task and the player is left as a failed Reset leaves it.
            RebuildSessionGraph(previousConfiguration, _nativeLibraryPaths, keepOperationQueue: true, () => RestoreSettings(state));
            switched = false;
        }

        ReopenMedia(state, MediaPlayerModePolicy.DiagnosticCategory, $"{Mode} mode");
        if (Controls.LoopMedia != state.LoopMedia)
        {
            HostEventDispatcher.Raise(
                this,
                LoopMediaChanged,
                new MediaValueChangedEventArgs<bool>(state.LoopMedia, Controls.LoopMedia),
                nameof(LoopMediaChanged));
        }

        if (switched)
        {
            var args = new MediaValueChangedEventArgs<MediaPlayerMode>(previousMode, mode);
            _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, ModeChanged, args, nameof(ModeChanged)));
            var hadPresentation = MediaPlayerModePolicy.UsesPresentation(previousMode);
            var hasPresentation = MediaPlayerModePolicy.UsesPresentation(mode);
            if (hadPresentation != hasPresentation)
            {
                var presentationArgs = new MediaValueChangedEventArgs<bool>(hadPresentation, hasPresentation);
                _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, HasPresentationChanged, presentationArgs, nameof(HasPresentationChanged)));
            }
        }

        return switched;
    }

    private ModeSwitchState CaptureModeSwitchState() => new(
        File.CurrentLoad,
        _mediaEngine.Position,
        _mediaEngine.State,
        Controls.LoopMedia,
        Controls.HostLoopMedia,
        File.PlayOnLoad,
        File.ResetPitchOnMediaLoad,
        File.ResetTempoOnMediaLoad,
        Effects.Volume,
        Effects.Pitch,
        Effects.Tempo,
        Effects.Normalize,
        Effects.Equalizer,
        _mediaEngine.AudioVideoOffsetSeconds,
        _mediaEngine.CdgOffsetSeconds,
        _mediaEngine.SubtitleOffsetSeconds,
        _mediaEngine.ShowMidiLyrics,
        _presentationCoordinator.VideoDisplayMode,
        _presentationCoordinator.BaseVisualTransform,
        CdgVisuals.BackgroundOpacity);

    /// <summary>
    /// Carries the host's settings into a new graph, before its events reach the host, so the
    /// switch raises no change events for values that did not change.
    /// </summary>
    private void RestoreSettings(ModeSwitchState state)
    {
        File.PlayOnLoad = state.PlayOnLoad;
        File.ResetPitchOnMediaLoad = state.ResetPitchOnMediaLoad;
        File.ResetTempoOnMediaLoad = state.ResetTempoOnMediaLoad;
        // The host's own value; Background mode then forces looping on top of it.
        Controls.LoopMedia = state.HostLoopMedia;
        Effects.Volume = state.Volume;
        Effects.Pitch = state.Pitch;
        Effects.Tempo = state.Tempo;
        Effects.Normalize = state.Normalize;
        Effects.Equalizer = state.Equalizer;
        _mediaEngine.ShowMidiLyrics = state.ShowMidiLyrics;
        _presentationCoordinator.VideoDisplayMode = state.VideoDisplayMode;
        _presentationCoordinator.BaseVisualTransform = state.BaseVisualTransform;
        CdgVisuals.BackgroundOpacity = state.CdgBackgroundOpacity;
    }

    /// <summary>
    /// Opens the media of <paramref name="state"/> again as the same item (no autoplay and no
    /// pitch/tempo reset), then restores its sync offsets, position and transport state: after a
    /// mode switch, and after a GPU device recovery (ENG-039). On the control queue.
    /// </summary>
    /// <param name="diagnosticCategory">The category of the warning when it cannot be reopened.</param>
    /// <param name="context">Who could not reopen it, for that warning (for example "Audio mode").</param>
    private void ReopenMedia(ModeSwitchState state, string diagnosticCategory, string context)
    {
        if (state.Load is not { } load)
            return;

        // A Dispose or Reset during the switch cancels the reopen (ENG-024): the player ends closed.
        bool reopened;
        try
        {
            reopened = File.Reopen(load, _loadLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!reopened)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                diagnosticCategory,
                $"{context} could not reopen {load.Source.DisplayName}; the player is closed.",
                sourcePath: load.Source.FilePath ?? load.Source.Uri?.AbsoluteUri);
            return;
        }

        // Loading clears the per-item offsets; this is still the same item.
        _ = _mediaEngine.SetAudioVideoOffset(state.AudioVideoOffsetSeconds);
        _ = _mediaEngine.SetCdgOffset(state.CdgOffsetSeconds);
        _ = _mediaEngine.SetSubtitleOffset(state.SubtitleOffsetSeconds);

        if (state.Position > SeekImmediateCompletionToleranceSeconds)
            _ = Controls.Seek(state.Position);

        switch (state.State)
        {
            case MediaPlayerState.Playing or MediaPlayerState.Buffering:
                _ = Controls.Play();
                break;
            case MediaPlayerState.Paused:
                RestorePaused();
                break;
        }

        _presentationCoordinator.RefreshCurrentPosition();
    }

    /// <summary>
    /// Pauses freshly opened media. An audio stream that never played cannot pause, so it plays
    /// muted for an instant first.
    /// </summary>
    private void RestorePaused()
    {
        if (Controls.Pause())
            return;

        Effects.SetScrubMuted(true, TimeSpan.Zero);
        try
        {
            if (Controls.Play())
                _ = Controls.Pause();
        }
        finally
        {
            Effects.SetScrubMuted(false, TimeSpan.Zero);
        }
    }

    /// <summary>What a mode switch carries from the old graph to the new one.</summary>
    private sealed record ModeSwitchState(
        MediaFile.LoadRequest? Load,
        double Position,
        MediaPlayerState State,
        bool LoopMedia,
        bool HostLoopMedia,
        bool PlayOnLoad,
        bool ResetPitchOnMediaLoad,
        bool ResetTempoOnMediaLoad,
        double Volume,
        int Pitch,
        int Tempo,
        bool Normalize,
        AudioEqualizerSettings Equalizer,
        double AudioVideoOffsetSeconds,
        double CdgOffsetSeconds,
        double SubtitleOffsetSeconds,
        bool ShowMidiLyrics,
        VideoDisplayMode VideoDisplayMode,
        BaseVisualTransform BaseVisualTransform,
        byte CdgBackgroundOpacity);
}

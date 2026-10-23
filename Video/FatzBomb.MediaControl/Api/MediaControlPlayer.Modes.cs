using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Stage 043A player modes and the presentation options of Full mode. Everything a mode changes in
/// the session graph is decided here, from the configuration that <c>CreateSessionGraph</c>
/// builds with, so a later <c>SetModeAsync</c> can rebuild the graph for another mode.
/// </summary>
public partial class MediaControlPlayer
{
    private MediaPlayerConfiguration _configuration;
    private bool? _autoBackground;
    private bool _transparentBackground;

    /// <summary>Gets the mode the player was created in (see <see cref="MediaPlayerConfiguration.Mode"/>).</summary>
    public MediaPlayerMode Mode => _configuration.Mode;

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
            _presentationCoordinator.RefreshCurrentPosition();
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
}

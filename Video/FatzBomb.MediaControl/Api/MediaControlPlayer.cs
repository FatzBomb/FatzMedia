using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Core-owned media-control session. Framework adapters supply surfaces; this object owns playback,
/// layer composition, native target fan-out, and presentation coordination.
/// </summary>
public class MediaControlPlayer : IMediaControlSession
{
    private static readonly TimeSpan SyncAdjustmentStep = TimeSpan.FromMilliseconds(100);
    private readonly IMediaControlPlatformFactory _platformFactory;
    private IServiceProvider _serviceProvider;
    private IMediaEngine _mediaEngine;
    private NativeMediaPresentationCoordinator _presentationCoordinator;
    private MediaControlOperationQueue _controlOperations;
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _presentationSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private IAudioDeviceMonitor? _audioDeviceMonitor;
    private bool _disposed;

    public MediaControlPlayer(
        IMediaControlPlatformFactory platformFactory,
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths)
    {
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        _serviceProvider = _platformFactory.CreateServiceProvider(configuration, resolvedNativeLibraryPaths);

        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

        _acceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        _presentationCoordinator = new NativeMediaPresentationCoordinator(_mediaEngine, configuration);
        Controls = new MediaControls(_mediaEngine, _presentationCoordinator);
        File = new MediaFile(_mediaEngine, _presentationCoordinator, Controls, Effects, CdgVisuals, archiveService);
        _controlOperations = new MediaControlOperationQueue("MediaControl");

        Controls.MediaEnded += OnControlsMediaEnded;
        Controls.MediaStarted += OnControlsMediaStarted;
        Controls.MediaStopped += OnControlsMediaStopped;
        _presentationCoordinator.FramePresented += OnPresentationFramePresented;

        if (_mediaEngine is IMediaErrorSource errorSource)
            errorSource.ErrorOccurred += OnEngineErrorOccurred;

        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

        try
        {
            _audioDeviceMonitor = _serviceProvider.GetService<IAudioDeviceMonitor>();
            if (_audioDeviceMonitor != null)
            {
                _audioDeviceMonitor.DeviceChanged += OnAudioDeviceMonitorChanged;
                _audioDeviceMonitor.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Audio device monitor failed to start: {ex.Message}");
        }
    }

    public MediaControls Controls { get; private set; }

    private MediaFile File { get; set; }

    public MediaEffects Effects { get; private set; }

    public CdgVisuals CdgVisuals { get; private set; }

    private AcceptedMediaTypes _acceptedTypes;

    public string OpenFileDialogFilter => _acceptedTypes.OpenFileDialogFilter;

    public static string LastNativeLoadMessage => Bootstrap.Message;

    public bool PlayOnLoad
    {
        get => File.PlayOnLoad;
        set => File.PlayOnLoad = value;
    }

    public bool LoopMedia
    {
        get => Controls.LoopMedia;
        set => Controls.LoopMedia = value;
    }

    public VideoDisplayMode VideoDisplayMode
    {
        get => _presentationCoordinator.VideoDisplayMode;
        set => _presentationCoordinator.VideoDisplayMode = value;
    }

    public bool Normalize
    {
        get => Effects.Normalize;
        set => Effects.Normalize = value;
    }

    public bool IsOpen => _mediaEngine.IsOpen;

    public MediaPlayerState State => _mediaEngine.State;

    public MediaContent Content => _mediaEngine.Content;

    public bool HasVideo => Content.HasFlag(MediaContent.Video);

    public bool HasAudio => Content.HasFlag(MediaContent.Audio);

    public bool HasCdg => Content.HasFlag(MediaContent.Cdg);

    public bool HasSubtitle => Content.HasFlag(MediaContent.Subtitle);

    public IReadOnlyList<SubtitleTrack> SubtitleTracks => _mediaEngine.SubtitleTracks;

    public string? SelectedSubtitleTrackId => _mediaEngine.SelectedSubtitleTrackId;

    public bool ShowMidiLyrics
    {
        get => _mediaEngine.ShowMidiLyrics;
        set
        {
            if (_mediaEngine.ShowMidiLyrics == value)
                return;

            _mediaEngine.ShowMidiLyrics = value;
            _presentationCoordinator.RefreshCurrentPosition();
        }
    }

    public TimeSpan AudioSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.AudioVideoOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetAudioVideoOffset(value.TotalSeconds));
    }

    public TimeSpan CdgSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.CdgOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetCdgOffset(value.TotalSeconds));
    }

    public TimeSpan SubtitleSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.SubtitleOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetSubtitleOffset(value.TotalSeconds));
    }

    public IVideoEngine? Video => _mediaEngine.Video;

    public IAudioEngine Audio => _mediaEngine.Audio;

    public ICdgEngine? Cdg => _mediaEngine.Cdg;

    public ISubtitleEngine? Subtitle => _mediaEngine.Subtitle;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_mediaEngine.Position);
        set => Seek(value);
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);

    public bool Load(string path) => File.Load(path);

    public bool Load(string path, bool ignoreSidecar = false) => File.Load(path, ignoreSidecar);

    public bool Load(MediaInputSource source, bool ignoreSidecar = false) => File.Load(source, ignoreSidecar);

    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) => File.Load(path, backgroundPath, ignoreSidecar);

    public bool Load(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) => File.Load(path, backgroundPaths, ignoreSidecar);

    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        File.Load(source, backgroundSource, ignoreSidecar);

    public Task<bool> LoadAsync(string path) => _controlOperations.Enqueue(() => File.LoadAsync(path));

    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(path, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(source, ignoreSidecar));

    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(path, backgroundPath, ignoreSidecar));

    public Task<bool> LoadAsync(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(path, backgroundPaths, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(source, backgroundSource, ignoreSidecar));

    public bool Play() => Controls.Play();

    public Task<bool> PlayAsync() => _controlOperations.Enqueue(Controls.Play);

    public bool Pause() => Controls.Pause();

    public Task<bool> PauseAsync() => _controlOperations.Enqueue(Controls.Pause);

    public bool Stop() => Controls.Stop();

    public Task<bool> StopAsync() => _controlOperations.Enqueue(Controls.Stop);

    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    public bool Seek(double seconds)
    {
        try
        {
            var success = Controls.Seek(seconds);
            if (success)
                _presentationCoordinator.RefreshCurrentPosition();

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    public bool SelectSubtitleTrack(string? trackId)
    {
        var selected = _mediaEngine.SelectSubtitleTrack(trackId);
        if (selected)
            _presentationCoordinator.RefreshCurrentPosition();

        return selected;
    }

    public TimeSpan AdjustAudioSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustAudioVideoOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public TimeSpan AdjustCdgSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustCdgOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public TimeSpan AdjustSubtitleSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustSubtitleOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public void ResetSyncOffsets()
    {
        _mediaEngine.ResetSyncOffsets();
        _presentationCoordinator.RefreshCurrentPosition();
    }

    public void Close() => File.Close();

    private double ApplySyncOffset(double seconds)
    {
        _presentationCoordinator.RefreshCurrentPosition();
        return seconds;
    }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid)
            return false;

        var attached = _presentationCoordinator.AttachPresentationSurface(surface);
        if (attached)
            _presentationSurfaces[surface.Id] = surface;

        return attached;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
            return false;

        if (!_presentationSurfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
            return false;

        if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
            return true;

        surface = surface.WithSize(pixelWidth, pixelHeight);
        var resized = _presentationCoordinator.ResizePresentationSurface(surface.Id, pixelWidth, pixelHeight);
        if (resized)
            _presentationSurfaces[surface.Id] = surface;

        return resized;
    }

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) =>
        !_disposed && !string.IsNullOrWhiteSpace(surfaceId) && _presentationSurfaces.ContainsKey(surfaceId) && _presentationCoordinator.SetPresentationSurfaceLiveResize(surfaceId, isResizing);

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        var detached = _presentationCoordinator.DetachPresentationSurface(surfaceId);
        if (detached)
            _ = _presentationSurfaces.Remove(surfaceId);

        return detached;
    }

    public event EventHandler? MediaEnded;

    public event EventHandler? MediaStarted;

    public event EventHandler? PresentationFramePresented;

    public event EventHandler? MediaStopped;

    public event EventHandler<MediaErrorEventArgs>? MediaError;

    public event EventHandler<MediaDiagnosticEventArgs>? MediaDiagnostic;

    public event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;

    private void OnControlsMediaEnded(object? sender, EventArgs e)
    {
        EventHandler? handler = MediaEnded;
        if (handler == null)
            return;

        _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));
    }

    private void OnControlsMediaStarted(object? sender, EventArgs e)
    {
        EventHandler? handler = MediaStarted;
        if (handler == null)
            return;

        _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));
    }

    private void OnControlsMediaStopped(object? sender, EventArgs e)
    {
        EventHandler? handler = MediaStopped;
        if (handler == null)
            return;

        _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));
    }

    private void OnPresentationFramePresented(object? sender, EventArgs e) => PresentationFramePresented?.Invoke(this, EventArgs.Empty);

    private void OnEngineErrorOccurred(MediaErrorInfo info)
    {
        EventHandler<MediaErrorEventArgs>? handler = MediaError;
        if (handler == null)
            return;

        var args = new MediaErrorEventArgs(info.Code, info.Message, info.SourcePath, info.InnerException);
        _ = _controlOperations.Enqueue(() => handler.Invoke(this, args));
    }

    private void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        EventHandler<MediaDiagnosticEventArgs>? handler = MediaDiagnostic;
        if (handler == null)
            return;

        var args = new MediaDiagnosticEventArgs(info.Level, info.Category, info.Message, info.SourcePath, info.InnerException);
        _ = _controlOperations.Enqueue(() => handler.Invoke(this, args));
    }

    private void OnAudioDeviceMonitorChanged(object? sender, AudioDeviceChangeInfo info) =>
        _ = _controlOperations.Enqueue(() => RaiseAudioDeviceChangeNotice(info));

    private void RaiseAudioDeviceChangeNotice(AudioDeviceChangeInfo info)
    {
        if (_disposed)
            return;

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "AudioDeviceMonitor",
            info.FailureMessage is { Length: > 0 }
                ? info.FailureMessage
                : $"Audio device change detected: kind={info.Kind}, name={info.DeviceName ?? "(unknown)"}; leaving playback under BASS/Windows control.",
            innerException: null);

        try
        {
            EventHandler<AudioDeviceChangedEventArgs>? handler = AudioDeviceChanged;
            if (handler != null)
            {
                var args = new AudioDeviceChangedEventArgs(info);
                handler.Invoke(this, args);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] AudioDeviceChanged dispatch failed: {ex.Message}");
        }
    }

    private static string[] ResolveNativeLibraryPaths(IEnumerable<string> nativeLibraryPaths)
    {
        var resolvedPaths = nativeLibraryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in resolvedPaths)
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Native library path does not exist: {path}");

        return resolvedPaths;
    }

    public bool Reset(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths)
    {
        if (!_disposed)
        {
            // 1. Stop all playback and close media
            try { File.Close(); } catch { }

            // 2. Unsubscribe internal events
            try { Controls.MediaEnded -= OnControlsMediaEnded; } catch { }
            try { Controls.MediaStarted -= OnControlsMediaStarted; } catch { }
            try { Controls.MediaStopped -= OnControlsMediaStopped; } catch { }
            try { _presentationCoordinator.FramePresented -= OnPresentationFramePresented; } catch { }
            try { DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred; } catch { }
            try
            {
                if (_mediaEngine is IMediaErrorSource errorSource)
                    errorSource.ErrorOccurred -= OnEngineErrorOccurred;
            }
            catch { }

            // 3. Disconnect audio device monitor
            if (_audioDeviceMonitor != null)
            {
                try { _audioDeviceMonitor.DeviceChanged -= OnAudioDeviceMonitorChanged; } catch { }
                try { _audioDeviceMonitor.Stop(); } catch { }
                try { _audioDeviceMonitor.Dispose(); } catch { }
                _audioDeviceMonitor = null;
            }

            // 4. Preserve presentation surfaces for reattachment
            var surfaces = new Dictionary<string, MediaPresentationSurfaceDescriptor>(_presentationSurfaces, StringComparer.OrdinalIgnoreCase);
            _presentationSurfaces.Clear();

            // 5. Dispose old dependencies
            try { Controls.Dispose(); } catch { }
            try { _presentationCoordinator.Dispose(); } catch { }
            try { _mediaEngine.Dispose(); } catch { }
            try { (_serviceProvider as IDisposable)?.Dispose(); } catch { }
            try { _controlOperations.Dispose(); } catch { }

            // 6. Reinitialize with new configuration
            var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
            _serviceProvider = _platformFactory.CreateServiceProvider(configuration, resolvedNativeLibraryPaths);

            _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
            IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

            _acceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
            Effects = new MediaEffects(_mediaEngine);
            CdgVisuals = new CdgVisuals(_mediaEngine);
            _presentationCoordinator = new NativeMediaPresentationCoordinator(_mediaEngine, configuration);
            Controls = new MediaControls(_mediaEngine, _presentationCoordinator);
            File = new MediaFile(_mediaEngine, _presentationCoordinator, Controls, Effects, CdgVisuals, archiveService);
            _controlOperations = new MediaControlOperationQueue("MediaControl");

            // 7. Re-subscribe internal events
            Controls.MediaEnded += OnControlsMediaEnded;
            Controls.MediaStarted += OnControlsMediaStarted;
            Controls.MediaStopped += OnControlsMediaStopped;
            _presentationCoordinator.FramePresented += OnPresentationFramePresented;
            if (_mediaEngine is IMediaErrorSource reinitErrorSource)
                reinitErrorSource.ErrorOccurred += OnEngineErrorOccurred;
            DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

            // 8. Reattach preserved presentation surfaces
            foreach (KeyValuePair<string, MediaPresentationSurfaceDescriptor> kvp in surfaces)
                try { _ = AttachPresentationSurface(kvp.Value); } catch { }

            // 9. Reconnect audio device monitor
            try
            {
                _audioDeviceMonitor = _serviceProvider.GetService<IAudioDeviceMonitor>();
                if (_audioDeviceMonitor != null)
                {
                    _audioDeviceMonitor.DeviceChanged += OnAudioDeviceMonitorChanged;
                    _audioDeviceMonitor.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControlPlayer] Audio device monitor failed to start during reset: {ex.Message}");
            }

            return true;
        }

        throw new ObjectDisposedException(nameof(MediaControlPlayer));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _ = Stop();
            File.Close();
            Controls.MediaEnded -= OnControlsMediaEnded;
            Controls.MediaStarted -= OnControlsMediaStarted;
            Controls.MediaStopped -= OnControlsMediaStopped;
            _presentationCoordinator.FramePresented -= OnPresentationFramePresented;
            DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred;
            if (_mediaEngine is IMediaErrorSource errorSource)
                errorSource.ErrorOccurred -= OnEngineErrorOccurred;
            if (_audioDeviceMonitor != null)
            {
                _audioDeviceMonitor.DeviceChanged -= OnAudioDeviceMonitorChanged;
                try { _audioDeviceMonitor.Stop(); } catch { }
                try { _audioDeviceMonitor.Dispose(); } catch { }
            }
            Controls.Dispose();
            _presentationCoordinator.Dispose();
            _mediaEngine.Dispose();
            (_serviceProvider as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Dispose failed: {ex.Message}");
        }
        finally
        {
            _controlOperations.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

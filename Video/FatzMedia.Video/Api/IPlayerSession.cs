using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Subtitle;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Framework-neutral public playback/control contract for a FatzMedia.Video media-control instance.
/// </summary>
/// <remarks>
/// <para>
/// UI adapters such as WPF, WinForms, WinUI, Avalonia, and MAUI should expose this session instead
/// of re-implementing playback/controller logic. Adapters may add tiny convenience pass-throughs,
/// but playback, rendering, retained layers, swap chains, and target fan-out remain owned by the
/// shared media-control implementation.
/// </para>
/// <para>
/// Threading contract (ENG-024). Control calls (load, transport, seek, close, mode switch) run one
/// at a time on the session's control queue. The <c>Async</c> methods only queue: they never wait
/// for another call, so a UI thread should use them. The synchronous methods wait for their turn
/// and run on the caller's thread, without a bound; their events are raised on that thread.
/// <see cref="Reset"/> and <see cref="IDisposable.Dispose"/> cancel loads, then wait for the calls
/// queued before them with a bound (see each). The surface methods are not queued either; during a
/// <see cref="Reset"/> or a mode switch the surfaces are held and attached to the new engine.
/// Property getters and setters and the sync-offset and subtitle methods are not queued: they act
/// on the current engine at once, also while a rebuild replaces it, so a value set during a
/// rebuild may be lost (set it again once the rebuild finished). Events fire on background threads
/// (or on the caller's thread for a synchronous call); handlers must marshal to the UI thread, and
/// a handler that throws is reported as a <c>HostEvent</c> Warning diagnostic without affecting
/// the other handlers. A handler raised inside a control call counts as part of it: the
/// synchronous methods called from it run at once, but <see cref="Reset"/> and Dispose throw
/// <see cref="InvalidOperationException"/>. Work the handler starts elsewhere (a UI-thread
/// callback, a task) is outside it once it runs on another thread or after the handler returned.
/// </para>
/// </remarks>
public interface IPlayerSession : IDisposable
{
    /// <summary>Gets codec, container, and hardware-decode capabilities for the active platform.</summary>
    VideoDecodeCapabilities VideoDecodeCapabilities { get; }

    /// <summary>Gets or sets whether loading media should automatically begin playback.</summary>
    bool PlayOnLoad { get; set; }

    /// <summary>Gets or sets whether pitch resets to zero after each successful media load.</summary>
    bool ResetPitchOnMediaLoad { get; set; }

    /// <summary>Gets or sets whether tempo resets to 100 percent after each successful media load.</summary>
    bool ResetTempoOnMediaLoad { get; set; }

    /// <summary>Gets or sets whether playback loops when the end of media is reached.</summary>
    bool LoopMedia { get; set; }

    /// <summary>Gets whether media is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the current playback state.</summary>
    MediaPlayerState State { get; }

    /// <summary>Gets the content roles present in the loaded media.</summary>
    MediaContent Content { get; }

    /// <summary>Gets whether the loaded media has video content.</summary>
    bool HasVideo { get; }

    /// <summary>Gets whether the loaded media has audio content.</summary>
    bool HasAudio { get; }

    /// <summary>Gets whether the loaded media has CDG content.</summary>
    bool HasCdg { get; }

    /// <summary>Gets whether the loaded media has subtitle content.</summary>
    bool HasSubtitle { get; }

    /// <summary>Gets whether Core has visual main content or a resolved composition background.</summary>
    bool HasVisualContent { get; }

    /// <summary>Gets the available embedded and sidecar subtitle tracks for the current media.</summary>
    IReadOnlyList<SubtitleTrack> SubtitleTracks { get; }

    /// <summary>Gets the selected subtitle track ID, or <see langword="null"/> when subtitles are disabled.</summary>
    string? SelectedSubtitleTrackId { get; }

    /// <summary>Gets or sets whether embedded MIDI/KAR lyrics are displayed when available.</summary>
    bool ShowMidiLyrics { get; set; }

    /// <summary>
    /// Gets or sets the equalizer applied to local and network audio streams and to the audio
    /// of video files (Stages 020A, 033A).
    /// Disabled by default; disabled or all-neutral settings attach no native filter.
    /// </summary>
    AudioEqualizerSettings Equalizer { get; set; }

    /// <summary>Gets or sets the video offset relative to the physical audio clock.</summary>
    TimeSpan AudioSyncOffset { get; set; }

    /// <summary>Gets or sets the CDG timeline offset relative to the playback clock.</summary>
    TimeSpan CdgSyncOffset { get; set; }

    /// <summary>Gets or sets the subtitle timeline offset relative to the playback clock.</summary>
    TimeSpan SubtitleSyncOffset { get; set; }

    /// <summary>Gets or sets how primary video content maps into the presentation surface.</summary>
    VideoDisplayMode VideoDisplayMode { get; set; }

    /// <summary>Gets or sets the color transform applied only to the base visual layer.</summary>
    BaseVisualTransform BaseVisualTransform { get; set; }

    /// <summary>Gets or sets the current playback position.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Gets the loaded media duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Loads media from a file path.</summary>
    bool Load(string path);

    /// <summary>Loads media from a file path, optionally ignoring matching sidecar/archive backgrounds.</summary>
    bool Load(string path, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source.</summary>
    bool Load(MediaInputSource source, bool ignoreSidecar = false);

    /// <summary>Loads media with an optional explicit background. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    bool Load(string path, string? backgroundPath, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source.</summary>
    bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false);

    /// <summary>Loads media from a file path asynchronously.</summary>
    Task<bool> LoadAsync(string path);

    /// <summary>Loads media from a file path asynchronously, optionally ignoring matching sidecar/archive backgrounds.</summary>
    Task<bool> LoadAsync(string path, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source asynchronously.</summary>
    Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false);

    /// <summary>Loads media with an optional explicit background asynchronously. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source asynchronously.</summary>
    Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false);

    /// <summary>
    /// Loads media from a file path asynchronously; <paramref name="cancellationToken"/> cancels the
    /// load (ENG-024; see <see cref="LoadAsync(MediaInputSource, MediaInputSource?, bool, CancellationToken)"/>).
    /// </summary>
    Task<bool> LoadAsync(string path, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled<bool>(cancellationToken) : LoadAsync(path);

    /// <summary>
    /// Loads media asynchronously; <paramref name="cancellationToken"/> cancels the load (ENG-024).
    /// A load cancelled before it started changes nothing; one cancelled during the open leaves the
    /// session closed. Either way the task is cancelled. Sessions without load cancellation only
    /// check the token before the load starts.
    /// </summary>
    Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<bool>(cancellationToken)
            : LoadAsync(source, backgroundSource, ignoreSidecar);

    /// <summary>Starts or resumes playback.</summary>
    bool Play();

    /// <summary>Starts or resumes playback asynchronously on the Core control queue.</summary>
    Task<bool> PlayAsync();

    /// <summary>Pauses playback.</summary>
    bool Pause();

    /// <summary>Pauses playback asynchronously on the Core control queue.</summary>
    Task<bool> PauseAsync();

    /// <summary>Stops playback and resets position.</summary>
    bool Stop();

    /// <summary>Stops playback asynchronously on the Core control queue.</summary>
    Task<bool> StopAsync();

    /// <summary>Seeks to a playback position.</summary>
    bool Seek(TimeSpan position);

    /// <summary>Seeks to a playback position in seconds.</summary>
    bool Seek(double seconds);

    /// <summary>
    /// Seeks to a playback position in seconds asynchronously on the Core control queue, in order
    /// with other control operations; the newest request replaces one still waiting to run. Hosts
    /// use it for seek-bar drags so the UI thread does no seek work. Sessions without a control
    /// queue seek synchronously.
    /// </summary>
    Task<bool> SeekAsync(double seconds) => Task.FromResult(Seek(seconds));

    /// <summary>
    /// Starts an interactive scrub (seek-bar drag). Playback carries on and each seek moves it,
    /// but a seek repeating the previous one of the scrub (the release seek at the last drag
    /// position) is ignored, so release never jumps back. Sessions without scrub support return
    /// <see langword="false"/>, and hosts then seek as before.
    /// </summary>
    bool BeginScrub() => false;

    /// <summary>Ends a scrub. Send the release seek first.</summary>
    bool EndScrub() => false;

    /// <summary>
    /// Gets or sets what audio does during a scrub: <see cref="MediaScrubAudio.Audible"/> (the
    /// default) plays from every drag position; <see cref="MediaScrubAudio.SilentWhileMoving"/>
    /// mutes while the drag moves and plays again once it rests briefly or ends. User volume is
    /// unchanged. Sessions without scrub support keep <see cref="MediaScrubAudio.Audible"/>.
    /// </summary>
    MediaScrubAudio ScrubAudio
    {
        get => MediaScrubAudio.Audible;
        set { }
    }

    /// <summary>
    /// Gets or sets how video frames are timed (Stage 017A): <see cref="VideoFrameTiming.SourceCadence"/>
    /// (the default) or <see cref="VideoFrameTiming.DisplayLocked"/>, which follows each display's
    /// refreshes where the renderer reports their timing. Sessions without the choice keep source
    /// cadence.
    /// </summary>
    VideoFrameTiming VideoFrameTiming
    {
        get => VideoFrameTiming.SourceCadence;
        set { }
    }

    /// <summary>
    /// Gets the session's mode (Stage 043A): the one it was created in until
    /// <see cref="SetModeAsync"/> switches it. Sessions without modes are
    /// <see cref="MediaPlayerMode.Full"/>.
    /// </summary>
    MediaPlayerMode Mode => MediaPlayerMode.Full;

    /// <summary>
    /// Switches the session to another mode on the fly (Stage 043A slice 2), reopening the open
    /// media at its position and in its transport state. Returns <see langword="true"/> when the
    /// session is in <paramref name="mode"/> afterwards. Sessions without modes return
    /// <see langword="true"/> only for their current mode.
    /// </summary>
    Task<bool> SetModeAsync(MediaPlayerMode mode) => Task.FromResult(mode == Mode);

    /// <summary>
    /// Raised after <see cref="SetModeAsync"/> switched to another mode. Sessions without modes
    /// never raise it.
    /// </summary>
    event EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>? ModeChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets whether a renderer presents to the session's surfaces (Stage 046A).
    /// <see langword="false"/> in <see cref="MediaPlayerMode.Audio"/> mode: the surfaces stay
    /// attached but nothing presents to them, so an adapter shows its background instead of the
    /// last frame. Sessions without modes always present.
    /// </summary>
    bool HasPresentation => MediaPlayerModePolicy.UsesPresentation(Mode);

    /// <summary>
    /// Raised when <see cref="HasPresentation"/> changes: on the control queue, after the
    /// <see cref="ModeChanged"/> of the switch. Like every session event it arrives on a
    /// background thread; read <see cref="HasPresentation"/> again after marshaling, since a
    /// quick switch back may already have changed it. Sessions without modes never raise it.
    /// </summary>
    event EventHandler<MediaValueChangedEventArgs<bool>>? HasPresentationChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Gets or sets whether loads search for sidecar and archive backgrounds (Stage 043A, default
    /// <see langword="true"/>). An explicit background passed to a load still applies. Sessions
    /// without the choice always search.
    /// </summary>
    bool AutoBackground
    {
        get => true;
        set { }
    }

    /// <summary>
    /// Gets or sets whether the output is transparent where nothing is drawn, including the CD+G
    /// background colour (Stage 043A, default <see langword="false"/>: black). Sessions without the
    /// choice stay black.
    /// </summary>
    bool TransparentBackground
    {
        get => false;
        set { }
    }

    /// <summary>Selects an available subtitle track. Pass <see langword="null"/> to disable subtitles.</summary>
    bool SelectSubtitleTrack(string? trackId);

    /// <summary>Adjusts the audio/video offset by 100ms per step and returns the resulting offset.</summary>
    TimeSpan AdjustAudioSyncOffset(int steps);

    /// <summary>Adjusts the CDG offset by 100ms per step and returns the resulting offset.</summary>
    TimeSpan AdjustCdgSyncOffset(int steps);

    /// <summary>Adjusts the subtitle offset by 100ms per step and returns the resulting offset.</summary>
    TimeSpan AdjustSubtitleSyncOffset(int steps);

    /// <summary>Resets all per-item synchronization offsets to zero.</summary>
    void ResetSyncOffsets();

    /// <summary>Closes the currently loaded media.</summary>
    void Close();

    /// <summary>
    /// Gets a snapshot of the depth-one prepared-next slot. The host owns its playlist; Core keeps at
    /// most one item analyzed and ready.
    /// </summary>
    PreparedNextInfo PreparedNext { get; }

    /// <summary>
    /// Gets or sets whether a Ready prepared item starts automatically when the current media ends
    /// naturally. Ignored while <see cref="LoopMedia"/> is enabled. Disabled by default.
    /// </summary>
    bool AutoTransitionToPreparedNext { get; set; }

    /// <summary>
    /// Analyzes a local media file without preparing or loading it, and warms the shared analysis
    /// cache. Returns <see langword="null"/> when the source is not analyzable.
    /// </summary>
    Task<MediaAnalysisResult?> AnalyzeAsync(MediaInputSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes and prepares a candidate into the depth-one slot, replacing any previously
    /// prepared item. Current playback is unaffected. Returns <see langword="true"/> when the
    /// candidate reached Ready, or <see langword="false"/> if it was invalid, cleared, replaced,
    /// or cancelled first.
    /// </summary>
    Task<bool> PrepareNextAsync(MediaInputSource source, CancellationToken cancellationToken = default);

    /// <summary>Discards the prepared item and cancels its analysis.</summary>
    bool ClearPreparedNext();

    /// <summary>
    /// Loads the Ready prepared item as the current media and starts playback. Returns
    /// <see langword="false"/> when no item is Ready, the prepared file changed (Stale), or the
    /// load failed. The slot is empty afterwards in every case except Stale.
    /// </summary>
    Task<bool> TransitionToPreparedNextAsync();

    /// <summary>
    /// Gets or sets the fade applied by prepared-next transitions. Disabled by default. When
    /// enabled, a manual transition fades the current audio out, waits the incoming envelope's
    /// muted lead time, loads and starts the prepared item with its fade gain primed, then fades
    /// it in. An automatic transition (the outgoing item already ended) applies only the incoming
    /// fade. The outgoing and incoming items do not overlap.
    /// </summary>
    AudioCrossfadeOptions PreparedNextTransitionFade { get; set; }

    /// <summary>
    /// Fades the current audio, including a video's soundtrack, with an outgoing envelope
    /// (default: full to muted). Only the sound fades; video frames are unchanged. Playback
    /// continues; the host decides whether to stop. Returns <see langword="false"/> when the media
    /// has no audio stream, the duration is zero, or the fade was cancelled or superseded. Stop,
    /// load, close, and a natural end restore unity fade gain.
    /// </summary>
    Task<bool> FadeOutAsync(TimeSpan duration, AudioFadeEnvelope? envelope = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fades the current audio with an incoming envelope (default: muted to full), starting
    /// at the envelope's audible start. Same return and cancellation rules as <see cref="FadeOutAsync"/>.
    /// </summary>
    Task<bool> FadeInAsync(TimeSpan duration, AudioFadeEnvelope? envelope = null, CancellationToken cancellationToken = default);

    /// <summary>Cancels the running fade, leaving the gain where it reached.</summary>
    void CancelFade();

    /// <summary>
    /// Gets the capability row of the open source (see <see cref="MediaSourceCapabilityMatrix"/>),
    /// or <see langword="null"/> when nothing is open.
    /// </summary>
    MediaSourceCapabilities? CurrentSourceCapabilities { get; }

    /// <summary>
    /// Stops all running processes, disposes internal resources, then reinitializes the entire
    /// engine with the supplied configuration and native-library paths. Existing presentation
    /// surface registrations are preserved and reattached. No media is loaded after reset; the
    /// caller is responsible for re-loading and resuming playback on the new device. The runtime
    /// settings start from their defaults again; each one that changed raises its change event with
    /// <see cref="MediaChangeReason.PlayerReset"/>.
    /// </summary>
    /// <remarks>
    /// Blocks the caller. Loads in progress or queued are cancelled, then Reset waits at most 5 s for
    /// the calls queued before it; past that it returns <see langword="false"/> and changes nothing.
    /// Throws <see cref="InvalidOperationException"/> from inside a control call or its event handler.
    /// </remarks>
    /// <param name="configuration">New immutable configuration to apply (device, fallback mode, etc.).</param>
    /// <param name="nativeLibraryPaths">Native-library search paths (BASS/FFmpeg).</param>
    /// <returns>True if the engine was successfully reinitialized.</returns>
    bool Reset(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths);

    /// <summary>Attaches or updates a framework-provided presentation surface.</summary>
    bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface);

    /// <summary>Resizes a previously attached framework-provided presentation surface.</summary>
    bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight);

    /// <summary>Notifies Core that a framework-provided presentation surface is in an active host resize/move loop.</summary>
    bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing);

    /// <summary>Detaches a previously attached framework-provided presentation surface.</summary>
    bool DetachPresentationSurface(string surfaceId);

    /// <summary>Raised when media reaches the end.</summary>
    event EventHandler? MediaEnded;

    /// <summary>Raised after transport starts or resumes and before the first coordinated playback presentation.</summary>
    event EventHandler? MediaStarted;

    /// <summary>Raised after Core presents a frame to an attached presentation surface.</summary>
    event EventHandler? PresentationFramePresented;

    /// <summary>Raised when media playback is stopped.</summary>
    event EventHandler? MediaStopped;

    /// <summary>Raised after a media source is loaded successfully and before optional autoplay.</summary>
    event EventHandler<MediaLoadedEventArgs>? MediaLoaded;

    /// <summary>Raised after the active media source is closed or replaced.</summary>
    event EventHandler<MediaClosedEventArgs>? MediaClosed;

    /// <summary>Raised after playback pauses.</summary>
    event EventHandler<MediaPausedEventArgs>? MediaPaused;

    /// <summary>Raised after loop playback commits its next lap.</summary>
    event EventHandler<MediaLoopedEventArgs>? MediaLooped;

    /// <summary>Raised when the public playback state changes.</summary>
    event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    /// <summary>Raised at a throttled cadence while playing and immediately after position transitions.</summary>
    event EventHandler<MediaPositionChangedEventArgs>? PositionChanged;

    /// <summary>Raised after an accepted seek settles on its resulting position.</summary>
    event EventHandler<MediaSeekCompletedEventArgs>? SeekCompleted;

    /// <summary>Raised when a buffered (network) source starts or stops buffering, or its fill moves.</summary>
    event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;

    /// <summary>Raised when a network source delivers new in-stream metadata.</summary>
    event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    /// <summary>Raised when the prepared-next slot changes state.</summary>
    event EventHandler<PreparedNextChangedEventArgs>? PreparedNextChanged;

    /// <summary>Raised after a prepared item became the current media.</summary>
    event EventHandler<PreparedNextTransitionedEventArgs>? PreparedNextTransitioned;

    /// <summary>Raised when the effective user volume changes.</summary>
    event EventHandler<MediaValueChangedEventArgs<double>>? VolumeChanged;

    /// <summary>Raised when pitch changes.</summary>
    event EventHandler<MediaValueChangedEventArgs<int>>? PitchChanged;

    /// <summary>Raised when tempo changes.</summary>
    event EventHandler<MediaValueChangedEventArgs<int>>? TempoChanged;

    /// <summary>Raised when normalization is enabled or disabled.</summary>
    event EventHandler<MediaNormalizeChangedEventArgs>? NormalizeChanged;

    /// <summary>
    /// Raised when <see cref="SubtitleTracks"/> or <see cref="SelectedSubtitleTrackId"/> change
    /// after a load completed: the embedded text subtitle tracks of a local video are decoded in
    /// the background and arrive after <see cref="MediaLoaded"/>. The first embedded track is
    /// selected then unless the host already selected a track (or none). Not raised when a newer
    /// load or a close made the result stale. Fires on a background thread; hosts must marshal to
    /// the UI thread before touching view state.
    /// </summary>
    event EventHandler<SubtitleTracksChangedEventArgs>? SubtitleTracksChanged;

    /// <summary>Raised when the equalizer settings change.</summary>
    event EventHandler<MediaValueChangedEventArgs<AudioEqualizerSettings>>? EqualizerChanged;

    /// <summary>Raised when loop playback is enabled or disabled.</summary>
    event EventHandler<MediaValueChangedEventArgs<bool>>? LoopMediaChanged;

    /// <summary>Raised when the base-visual color transform changes.</summary>
    event EventHandler<MediaValueChangedEventArgs<BaseVisualTransform>>? BaseVisualTransformChanged;

    /// <summary>
    /// Raised when a high-value failure occurs inside the media engine. Carries typed
    /// error information so hosts can diagnose failures without relying on <c>false</c>
    /// returns or Debug-only output. May fire from background threads; hosts must marshal
    /// to the UI thread before touching view state.
    /// </summary>
    event EventHandler<MediaErrorEventArgs>? MediaError;

    /// <summary>
    /// Raised when an engine component produces a host-routable diagnostic. Carries
    /// a severity level and category so hosts can filter and route diagnostics without
    /// relying on Debug-only output. Contains non-error informational messages such as
    /// decoder configuration, native library load outcomes, and state transitions in
    /// addition to warning- and error-level diagnostics. Fires in both Debug and Release
    /// builds. May fire from background threads; hosts must marshal to the UI thread
    /// before touching view state.
    /// </summary>
    event EventHandler<MediaDiagnosticEventArgs>? MediaDiagnostic;

    /// <summary>
    /// Raised when the active audio output device is lost, the system default device changes,
    /// or the audio device state otherwise changes. Core does not change playback state from
    /// this notification; BASS/Windows remain responsible for active stream migration. Hosts
    /// should surface a user-visible diagnostic when needed. When BASS loses the output (another
    /// application takes the device exclusively, or it is unplugged), every player raises it with
    /// <see cref="AudioDeviceChangeKind.DeviceRemoved"/> and a failure message while Core retries
    /// once a second, then with <see cref="AudioDeviceChangeKind.DeviceAdded"/> once the output is
    /// back and playing channels resumed. May fire from background threads;
    /// hosts must marshal to the UI thread before touching view state.
    /// </summary>
    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}

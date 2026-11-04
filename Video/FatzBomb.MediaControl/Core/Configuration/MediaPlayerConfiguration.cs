using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Core.Configuration;


/// <summary>
/// Video decoding methods available for MediaPlayer instances.
/// Auto is recommended - it automatically selects the best available method.
/// </summary>
public enum VideoDecodingMethod
{
    /// <summary>
    /// Automatically detect and use the best available method (GPU > CPU).
    /// This is the recommended setting for best quality and performance.
    /// </summary>
    Auto,

    /// <summary>
    /// Force hardware-accelerated video decoding (D3D11VA/D3D12VA/DXVA2 where available).
    /// Falls back to software if hardware acceleration fails.
    /// </summary>
    Hardware,

    /// <summary>
    /// Force CPU-based software video decoding.
    /// Always available but uses more CPU resources.
    /// </summary>
    Software
}

/// <summary>
/// Controls what happens when an explicitly selected audio output device cannot be initialized.
/// Runtime device changes are diagnostic-only; this policy is applied at player creation/open time.
/// </summary>
public enum AudioOutputFallbackMode
{
    /// <summary>
    /// Fall back to the system primary/default audio device when the requested explicit device is unavailable.
    /// </summary>
    FallbackToPrimary = 0,

    /// <summary>
    /// Do not route audio to a different output device when the requested explicit device is unavailable.
    /// </summary>
    NoFallback = 1,
}

/// <summary>
/// Immutable configuration for a MediaPlayer instance.
/// These settings are determined at creation time and cannot be changed during playback.
/// This ensures consistent performance and avoids mid-stream resource switching.
/// </summary>
public readonly record struct MediaPlayerConfiguration
{
    /// <summary>
    /// Video decoding method for this MediaPlayer instance.
    /// </summary>
    public VideoDecodingMethod VideoDecoding { get; init; }

    /// <summary>
    /// Whether useful state-change and warning logging is enabled for this instance.
    /// This is intended to show backend changes, decode fallbacks, surface creation, seek recovery,
    /// and other information that helps diagnose playback without flooding the debug output.
    /// </summary>
    public bool EnableLogging { get; init; }

    /// <summary>
    /// Whether periodic performance summaries are written. Enable this while comparing presentation
    /// modes or diagnosing stutter. Routine decoder, renderer, and presentation-loop counters use
    /// this flag instead of the general logging flag.
    /// </summary>
    public bool EnablePerformanceLogging { get; init; }

    /// <summary>
    /// Enables very noisy diagnostic details such as decoder candidate dumps, individual FFmpeg packet
    /// errors, host callback cadence, and seek preview details. Leave false for normal debugging.
    /// </summary>
    public bool EnableVerboseLogging { get; init; }

    /// <summary>
    /// Initial presentation lead, measured in display refresh intervals. The pipeline now treats this
    /// as the starting point for an adaptive controller rather than a fixed forever value. It nudges
    /// the active lead up when frames arrive late or are dropped, and nudges it down if playback is
    /// consistently early. Use 0.75 as the normal default for 23.976/24fps material on 60Hz displays.
    /// </summary>
    public double PresentationLeadRefreshIntervals { get; init; }

    /// <summary>
    /// When true, the audio output latency provider augments a small BASS-reported latency value
    /// with a conservative per-class offset for USB DACs and Bluetooth endpoints whose internal
    /// buffering is not visible to BASS. Off by default because the heuristic is name-pattern based
    /// and inherently best-effort.
    /// </summary>
    public bool EnableUsbBluetoothLatencyHeuristic { get; init; }

    /// <summary>
    /// BASS output device index to use when creating this player. Use -1 for Primary (system default),
    /// which lets BASS follow Windows default-device changes. Explicit device indexes are creation-time
    /// choices; changing devices at runtime should create a new player/control instance.
    /// </summary>
    public int AudioOutputDeviceIndex { get; init; }

    /// <summary>
    /// Determines whether an unavailable explicit audio device falls back to Primary during initialization.
    /// </summary>
    public AudioOutputFallbackMode AudioOutputFallbackMode { get; init; }

    /// <summary>
    /// Visual style used for Core-rendered text subtitles. This is fixed for the player instance;
    /// create a new player to apply a different style.
    /// </summary>
    public SubtitleTextStyle SubtitleStyle { get; init; }

    /// <summary>
    /// Visual style used for MIDI/KAR lyrics, including the independent sung-text wipe colors.
    /// </summary>
    public KaraokeTextStyle KaraokeStyle { get; init; }

    /// <summary>
    /// Path to a MIDI soundfont (.sf2 or .dls) to use for MIDI synthesis. When set, this path
    /// is used regardless of any auto-detected system soundfont. When null/empty, the engine
    /// auto-detects a suitable soundfont (e.g. Windows GS Wavetable gm.dls, or bundled fallback).
    /// </summary>
    public string? MidiSoundfontPath { get; init; }

    /// <summary>
    /// Whether pitch returns to the neutral value of 0 after each successful media load.
    /// </summary>
    public bool ResetPitchOnMediaLoad { get; init; }

    /// <summary>
    /// Whether tempo returns to the neutral value of 100 after each successful media load.
    /// </summary>
    public bool ResetTempoOnMediaLoad { get; init; }

    /// <summary>
    /// Configures optional leading and trailing silence trimming for analyzed plain audio.
    /// Trimming is disabled by default and is not applied to synchronized media.
    /// </summary>
    public SilenceTrimOptions SilenceTrim { get; init; }

    /// <summary>
    /// Loudness (LUFS) normalization on the FatzAudio engine: the target, the limiter ceiling and
    /// the cache folder. The default is -14 LUFS with peaks limited to
    /// -1 dBTP. The BASS engine ignores it and keeps its peak rule.
    /// </summary>
    public NormalizationOptions Normalization { get; init; }

    /// <summary>
    /// Bounded retry policy for network sources. <see langword="null"/> uses
    /// <see cref="NetworkRetryPolicy.Default"/> (one attempt, no retry).
    /// </summary>
    public NetworkRetryPolicy? NetworkRetry { get; init; }

    /// <summary>
    /// Certificate checks for <c>https</c> network video. <see langword="null"/> uses
    /// <see cref="NetworkTlsPolicy.Default"/> (certificates verified).
    /// </summary>
    public NetworkTlsPolicy? NetworkTls { get; init; }

    /// <summary>
    /// How far network video reads ahead of playback. <see langword="null"/> uses
    /// <see cref="NetworkReadAheadPolicy.Default"/> (20 s or 64 MB).
    /// </summary>
    public NetworkReadAheadPolicy? NetworkReadAhead { get; init; }

    /// <summary>
    /// How HDR video is shown on SDR output. The default tone maps HDR10 (PQ) and HLG video to SDR.
    /// </summary>
    public HdrToneMappingOptions HdrToneMapping { get; init; }

    /// <summary>
    /// What the player loads and accepts (Stage 043A). The default, <see cref="MediaPlayerMode.Full"/>,
    /// is the complete player; <see cref="MediaPlayerMode.Background"/> and
    /// <see cref="MediaPlayerMode.Audio"/> build only what they need.
    /// </summary>
    public MediaPlayerMode Mode { get; init; }

    /// <summary>
    /// The engine for audio files and archive entries (037A slice 2). <see langword="null"/>, the
    /// default, uses the built-in BASS engine. Set it to the FatzAudio engine package's factory to
    /// opt in to FatzAudio; video audio stays on BASS. When the factory cannot create its engine,
    /// the player warns and uses BASS.
    /// </summary>
    public IAudioEngineFactory? AudioEngineFactory { get; init; }

    /// <summary>
    /// Creates a default configuration with optimal settings for best quality.
    /// Uses automatic detection for video decoding.
    /// </summary>
    public MediaPlayerConfiguration()
    {
        VideoDecoding = VideoDecodingMethod.Auto;
        EnableLogging = false;
        EnablePerformanceLogging = false;
        EnableVerboseLogging = false;
        PresentationLeadRefreshIntervals = 0.75;
        EnableUsbBluetoothLatencyHeuristic = false;
        AudioOutputDeviceIndex = -1;
        AudioOutputFallbackMode = AudioOutputFallbackMode.FallbackToPrimary;
        SubtitleStyle = new SubtitleTextStyle();
        KaraokeStyle = new KaraokeTextStyle();
        ResetPitchOnMediaLoad = false;
        ResetTempoOnMediaLoad = false;
        SilenceTrim = default;
        Mode = MediaPlayerMode.Full;
    }

    /// <summary>
    /// Creates a configuration with a specific video decoding method.
    /// </summary>
    public MediaPlayerConfiguration(VideoDecodingMethod videoDecoding)
        : this()
    {
        VideoDecoding = videoDecoding;
    }

    /// <summary>
    /// Auto configuration - same as default constructor for compatibility.
    /// </summary>
    public static MediaPlayerConfiguration Auto => new();
}

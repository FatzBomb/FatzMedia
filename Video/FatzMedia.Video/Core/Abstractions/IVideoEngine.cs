using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Video;

namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// One decoded or supplied frame delivered to a synchronous consumer.
/// </summary>
/// <remarks>
/// Lifetime contract: the event object and its payloads are valid only for the duration of
/// the callback (or the renderer call that receives it). Consumers copy what they keep and must
/// not store the event object. <see cref="PixelData"/> may be retained only when
/// <see cref="PixelDataCanBeRetained"/> is true; <see cref="NativeResource"/> is always
/// borrowed. Producers may re-raise a cached retainable instance (image frames, the loop-start
/// snapshot) but never mutate an instance after raising it. See roadmap stage 019A.
/// </remarks>
public sealed class VideoFrameEventArgs : EventArgs
{
    /// <summary>Gets the BGRA pixels; valid during the callback only unless <see cref="PixelDataCanBeRetained"/>.</summary>
    public ReadOnlyMemory<byte> PixelData { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// True when <see cref="PixelData"/> is backed by stable Core-owned memory that can be
    /// retained after the event returns. Leave false for decoder-pooled frame buffers.
    /// </summary>
    public bool PixelDataCanBeRetained { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required double PtsSeconds { get; init; }
    public double FramesPerSecond { get; init; }
    /// <summary>
    /// Optional producer-owned native resource. Consumers must not release or retain its handles
    /// after Core replaces the current decoded frame.
    /// </summary>
    public NativeVideoFrameResource? NativeResource { get; init; }

    public bool IsNativeHardwareFrame => NativeResource is { IsValid: true };

    /// <summary>
    /// The presentation targets this frame is for, or null for all (Stage 017A slice 5:
    /// display-locked video on several monitors raises a frame once per display).
    /// </summary>
    internal Core.Graphics.Composition.MediaCompositionTargetFilter? Targets { get; init; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Stop is the natural name; a C#-only API where the VB keyword clash does not matter and a rename would break every host.")]

public interface IVideoEngine : IDisposable
{
    bool Open(string path);

    /// <summary>
    /// Opens a video source from an archive entry.
    /// </summary>
    bool Open(IArchiveEntryInfo archiveEntry);

    void Close();
    bool Play();
    bool Pause();
    bool Stop();
    bool Seek(double seconds);
    double Duration { get; }
    double Position { get; set; }
    bool IsOpen { get; }
    double FramesPerSecond { get; }

    /// <summary>
    /// Gets the audio engine decoded from the current video source, if it has one.
    /// </summary>
    IAudioEngine? Audio { get; }

    /// <summary>
    /// Gets whether the current video source exposes an audio stream.
    /// </summary>
    bool HasAudio { get; }

    /// <summary>
    /// Gets or sets whether playback restarts seamlessly at the end of the source.
    /// </summary>
    bool LoopPlayback { get; set; }

    /// <summary>
    /// Gets the video width in pixels.
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Gets the video height in pixels.
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Gets the video codec name if available.
    /// </summary>
    string? CodecName { get; }

    /// <summary>
    /// Gets whether the current video stream is using FFmpeg hardware decoding.
    /// </summary>
    bool IsHardwareAccelerated { get; }

    /// <summary>
    /// Gets the active hardware decoding method, or null when software decoding is active.
    /// </summary>
    string? HardwareAccelerationMethod { get; }

    /// <summary>
    /// Supplies the actual refresh rate (Hz) of the monitor displaying this video, so the
    /// presentation thread's tick rate matches the real display instead of a DWM-wide guess.
    /// Particularly relevant on multi-monitor setups with mismatched refresh rates, since
    /// DWM's global composition timing does not reliably reflect any one specific monitor.
    /// Pass null or a non-positive value to fall back to automatic detection.
    /// </summary>
    void SetDisplayRefreshRate(double? refreshHz);


    /// <summary>
    /// Reissues the current decoded frame for a newly attached or resized
    /// presentation surface. Implementations may ignore the request when no frame
    /// is available or while playback is active and <paramref name="allowWhilePlaying"/>
    /// is <see langword="false"/>.
    /// </summary>
    void RenderCurrentFrame(bool allowWhilePlaying = false, bool forceReissue = false);

    event EventHandler<VideoFrameEventArgs>? FrameReady;
    event EventHandler<VideoEngineState>? StateChanged;
    event EventHandler? PlaybackEnded;
    event EventHandler? Opened;
    event EventHandler? Closed;
    event EventHandler<double>? PositionChanged;
}

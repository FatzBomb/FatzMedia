using FatzBomb.MediaControl.Core.Video;

namespace FatzBomb.MediaControl.Core.Abstractions;

public sealed class VideoFrameEventArgs : EventArgs
{
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
    public IntPtr D3D11DevicePointer { get; init; }
    public IntPtr D3D11DeviceContextPointer { get; init; }
    public IntPtr D3D11TexturePointer { get; init; }
    public int D3D11ArraySlice { get; init; }
    public bool IsD3D11HardwareFrame => D3D11TexturePointer != IntPtr.Zero && D3D11DevicePointer != IntPtr.Zero;
}

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
    /// When enabled, a UI adapter render event drives frame presentation instead of the
    /// pipeline's independent presentation thread. This is intended for UI-backed interop
    /// paths so DirectX surface updates are synchronized with host composition. Native HWND/swap-chain
    /// presentation should disable this.
    /// </summary>
    void SetHostRenderEventDrivenPresentation(bool enabled);

    event EventHandler<VideoFrameEventArgs>? FrameReady;
    event EventHandler<VideoEngineState>? StateChanged;
    event EventHandler? PlaybackEnded;
    event EventHandler? Opened;
    event EventHandler? Closed;
    event EventHandler<double>? PositionChanged;
}

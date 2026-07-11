using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Subtitle;

namespace FatzBombMedia.MediaControl.Core.Abstractions;

[Flags]
public enum MediaContent
{
    None = 0,
    Audio = 1 << 0,
    Video = 1 << 1,
    Cdg = 1 << 2,
    Image = 1 << 3,
    Archive = 1 << 4,
    Subtitle = 1 << 5
}

public interface IMediaEngine : IDisposable
{
    // Engine access
    IVideoEngine? Video { get; }
    ICdgEngine? Cdg { get; }
    ISubtitleEngine? Subtitle { get; }
    IReadOnlyList<SubtitleTrack> SubtitleTracks { get; }
    string? SelectedSubtitleTrackId { get; }
    double AudioVideoOffsetSeconds { get; }
    double CdgOffsetSeconds { get; }
    double SubtitleOffsetSeconds { get; }
    IAudioEngine Audio { get; } // Add audio engine access
    string? ImagePath { get; }

    /// <summary>Gets or sets whether Core should automatically resolve sidecar/archive backgrounds for overlay media.</summary>
    bool BackgroundAutoDetect { get; set; }

    /// <summary>Gets the Core-owned background candidate currently selected for overlay composition, if any.</summary>
    MediaBackgroundCandidate? Background { get; }

    /// <summary>
    /// Supplies the actual monitor refresh rate (Hz) for video presentation timing, independent
    /// of whether a file is currently open. See IVideoEngine.SetDisplayRefreshRate for details.
    /// </summary>
    void SetDisplayRefreshRate(double? refreshHz);

    /// <summary>Sets the Core-owned overlay background candidate. The engine loads/loops it as required.</summary>
    bool SetBackground(MediaBackgroundCandidate? background);

    /// <summary>Clears the Core-owned overlay background candidate and any retained background resources.</summary>
    void ClearBackground();

    // Media operations
    bool Open(string path);

    /// <summary>Opens media from a framework-neutral Core input source.</summary>
    bool Open(MediaInputSource source);
    bool Play();
    bool Pause();
    bool Stop();
    void Close();
    bool Seek(double seconds);
    bool SelectSubtitleTrack(string? trackId);
    double SetAudioVideoOffset(double seconds);
    double SetCdgOffset(double seconds);
    double SetSubtitleOffset(double seconds);
    double AdjustAudioVideoOffset(double seconds);
    double AdjustCdgOffset(double seconds);
    double AdjustSubtitleOffset(double seconds);
    void ResetSyncOffsets();
    Task<bool> SeekAsync(double seconds, TimeSpan? timeout = null);


    // State and properties
    double Position { get; set; }
    double Duration { get; }
    MediaPlayerState State { get; }
    bool IsOpen { get; }
    MediaContent Content { get; }

    /// <summary>
    /// Raised when the active media reaches its natural end.
    /// </summary>
    event EventHandler? MediaEnded;

    /// <summary>Raised when Core has an image-only frame ready for the compositor bridge.</summary>
    event EventHandler<VideoFrameEventArgs>? ImageFrameReady;

    /// <summary>Returns the current image-only frame if one is retained by Core.</summary>
    bool TryGetCurrentImageFrame(out VideoFrameEventArgs? frame);

    /// <summary>Raised when Core has a background frame ready for the compositor bridge.</summary>
    event EventHandler<VideoFrameEventArgs>? BackgroundFrameReady;

    /// <summary>Raised when Core background state has been cleared.</summary>
    event EventHandler? BackgroundCleared;
}

internal interface ILoopPlaybackControl
{
    bool LoopPlayback { get; set; }
}

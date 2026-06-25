namespace FatzBombMedia.MediaControl.Core.Abstractions;

[Flags]
public enum MediaContent
{
    None = 0,
    Audio = 1 << 0,
    Video = 1 << 1,
    Cdg = 1 << 2,
    Image = 1 << 3,
    Archive = 1 << 4
}

public interface IMediaEngine : IDisposable
{
    // Engine access
    IVideoEngine? Video { get; }
    ICdgEngine? Cdg { get; }
    IAudioEngine Audio { get; } // Add audio engine access
    string? ImagePath { get; }

    /// <summary>
    /// Supplies the actual monitor refresh rate (Hz) for video presentation timing, independent
    /// of whether a file is currently open. See IVideoEngine.SetDisplayRefreshRate for details.
    /// </summary>
    void SetDisplayRefreshRate(double? refreshHz);

    // Media operations
    bool Open(string path);
    bool Play();
    bool Pause();
    bool Stop();
    void Close();
    bool Seek(double seconds);
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
}

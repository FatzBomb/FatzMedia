using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// The CD+G engine when the audio engine (FatzAudio, which draws CD+G) did not start: it shows nothing and says
/// so once, with one Warning diagnostic, the first time a CD+G file is loaded.
/// </summary>
sealed class UnavailableCdgEngine : ICdgEngine
{
    bool _warned;

    public int FrameWidth => CdgConstants.FrameWidth;
    public int FrameHeight => CdgConstants.FrameHeight;
    public uint BackgroundWashArgb => 0;
    public double TotalDurationSeconds => 0;
    public byte BackgroundOpacity { get; set; }
    public uint? BackgroundOverrideArgb { get; set; }

    // Part of the ICdgEngine contract; this engine never raises them.
    public event EventHandler? FrameInvalidated { add { } remove { } }
    public event EventHandler? BackgroundWashChanged { add { } remove { } }
    public event EventHandler<CdgEngineState>? StateChanged { add { } remove { } }
    public event EventHandler? Opened { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    public event EventHandler? PlaybackStarted { add { } remove { } }

    public void Load(string filePath) => WarnOnce(filePath);

    public void Load(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        WarnOnce(archiveEntry.Key);
    }

    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Reset() { }
    public void RenderAtSeconds(double seconds) { }
    public void RenderAtSeconds(double seconds, bool forceRebuild) { }
    public void Dispose() { }

    void WarnOnce(string source)
    {
        if (_warned)
            return;

        _warned = true;
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "CDG",
            "CD+G graphics and MIDI lyrics are drawn by the audio engine (FatzAudio), which is not running, so they are not shown.",
            sourcePath: source);
    }
}

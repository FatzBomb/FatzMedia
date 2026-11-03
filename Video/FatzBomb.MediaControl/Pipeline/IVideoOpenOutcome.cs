namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Why a video engine's last local open failed, where the caller can act on it.
/// </summary>
internal interface IVideoOpenOutcome
{
    /// <summary>
    /// The last local open failed because the file has no video stream (audio only, or only cover
    /// art), so the caller can play it as audio.
    /// </summary>
    bool LastOpenHadNoVideo { get; }
}

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Lets the presentation coordinator hand the video pipeline the refresh rate of the display
/// showing the primary surface, as reported by the renderer (Stage 017A slice 3). Internal so
/// the public engine contract does not change.
/// </summary>
interface IRendererRefreshRateSink
{
    /// <summary>
    /// Sets the source of the primary display's refresh rate in Hz (null when unknown), or
    /// clears it with null. The source is called from presentation threads and must be cheap.
    /// </summary>
    void SetRendererRefreshRateSource(Func<double?>? source);
}

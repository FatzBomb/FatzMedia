namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Framework-neutral bridge used by core playback/file controllers to coordinate rendering.
/// </summary>
/// <remarks>
/// UI adapters implement this when rendering work must be marshaled to a framework thread. The
/// core controllers depend on this small contract instead of referencing framework-specific
/// scheduling, visual-surface, or renderer classes.
/// </remarks>
public interface IMediaRenderCoordinator
{
    /// <summary>Starts render coordination for the current media.</summary>
    void StartRendering();

    /// <summary>Stops render coordination for the current media.</summary>
    void StopRendering();

    /// <summary>Refreshes media engine attachments after a media load or close.</summary>
    void RefreshAttachments();
}

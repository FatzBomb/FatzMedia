namespace FatzMedia.Video.Core.Graphics.Rendering;

/// <summary>
/// Reports a renderer failure once per episode (ENG-026). Failures that repeat every frame (a
/// readback that keeps failing, a present that keeps being refused) raise one diagnostic through
/// <see cref="DiagnosticRouter"/>, then stay quiet until <see cref="Reset"/> marks the episode
/// over, normally on the next success. Thread-safe; one instance per failure site.
/// </summary>
public sealed class RendererDiagnosticLatch
{
    int _latched;

    /// <summary>Gets whether the current episode has already been reported.</summary>
    public bool IsLatched => Volatile.Read(ref _latched) != 0;

    /// <summary>
    /// Raises the diagnostic unless this episode was already reported. Returns whether it was
    /// raised.
    /// </summary>
    public bool Raise(MediaDiagnosticLevel level, string category, string message, Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _latched, 1) != 0)
            return false;

        DiagnosticRouter.Raise(level, category, message, innerException: exception);
        return true;
    }

    /// <summary>Ends the episode: the next failure is reported again.</summary>
    public void Reset() => Volatile.Write(ref _latched, 0);
}

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Static sink through which engine and platform components can raise host-routable
/// diagnostics. <see cref="MediaControlPlayer"/> subscribes and re-marshals to the
/// public <see cref="IMediaControlSession.MediaDiagnostic"/> event.
/// </summary>
/// <remarks>
/// May fire from background threads (BASS callbacks, decode loops, presentation
/// loop, etc.). Subscribers must marshal to the consuming thread as needed.
/// </remarks>
public static class DiagnosticRouter
{
    /// <summary>
    /// Raised when an engine component emits a host-routable diagnostic. Core owns the
    /// subscription because <see cref="MediaDiagnosticInfo"/> remains an internal transport type.
    /// </summary>
    internal static event Action<MediaDiagnosticInfo>? DiagnosticOccurred;

    // The player a diagnostic belongs to. Set while a player builds its engine, so it flows (with
    // the execution context) into every thread and task that player starts; native callbacks
    // and process-wide code (native loading) raise without one.
    private static readonly AsyncLocal<object?> CurrentOwner = new();

    /// <summary>The owner diagnostics raised on this execution context are attributed to, or null.</summary>
    internal static object? Owner => CurrentOwner.Value;

    /// <summary>
    /// Attributes diagnostics raised on this execution context, and on threads and tasks started
    /// from it, to <paramref name="owner"/> until the returned scope is disposed.
    /// </summary>
    internal static OwnerScope EnterOwner(object? owner)
    {
        var previous = CurrentOwner.Value;
        CurrentOwner.Value = owner;
        return new OwnerScope(previous);
    }

    public static void Raise(MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null) =>
        DiagnosticOccurred?.Invoke(new MediaDiagnosticInfo(level, category, message, sourcePath, innerException) { Owner = CurrentOwner.Value });

    /// <summary>
    /// Raises a diagnostic that belongs to <paramref name="owner"/>: only that session's
    /// <see cref="IMediaControlSession.MediaDiagnostic"/> reports it. For adapter and host code
    /// that raises outside the engine (a surface on the UI thread knows the player it is attached
    /// to). A null <paramref name="owner"/> raises a process-wide diagnostic, which every player reports.
    /// </summary>
    public static void Raise(IMediaControlSession? owner, MediaDiagnosticLevel level, string category, string message, string? sourcePath = null, Exception? innerException = null) =>
        DiagnosticOccurred?.Invoke(new MediaDiagnosticInfo(level, category, message, sourcePath, innerException) { Owner = owner });

    internal readonly struct OwnerScope(object? previous) : IDisposable
    {
        public void Dispose() => CurrentOwner.Value = previous;
    }
}

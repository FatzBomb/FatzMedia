#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Internal payload used by <see cref="DiagnosticRouter"/> to communicate typed
/// diagnostic details from engine components to the host-facing
/// <see cref="MediaControlPlayer"/> without exposing internals publicly.
/// </summary>
internal sealed record MediaDiagnosticInfo(MediaDiagnosticLevel Level, string Category, string Message, string? SourcePath = null, Exception? InnerException = null)
{
    /// <summary>The player it came from (see <see cref="DiagnosticRouter.EnterOwner"/>); null for process-wide diagnostics.</summary>
    public object? Owner { get; init; }

    /// <summary>Whether a subscriber owned by <paramref name="owner"/> should see it: its own, or a process-wide one.</summary>
    public bool IsFor(object? owner) => Owner is null || ReferenceEquals(Owner, owner);
}

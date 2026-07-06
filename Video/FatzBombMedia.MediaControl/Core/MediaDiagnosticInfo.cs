#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Internal payload used by <see cref="DiagnosticRouter"/> to communicate typed
/// diagnostic details from engine components to the host-facing
/// <see cref="MediaControlPlayer"/> without exposing internals publicly.
/// </summary>
internal sealed record MediaDiagnosticInfo(MediaDiagnosticLevel Level, string Category, string Message, string? SourcePath = null, Exception? InnerException = null);

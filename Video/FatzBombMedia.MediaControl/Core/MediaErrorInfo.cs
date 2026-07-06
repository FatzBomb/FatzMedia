#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Internal payload used by <see cref="Core.Abstractions.IMediaErrorSource"/> to
/// communicate typed failure details from the pipeline to the host-facing
/// <see cref="MediaControlPlayer"/> without exposing pipeline internals publicly.
/// </summary>
internal sealed record MediaErrorInfo(MediaErrorCode Code, string Message, string? SourcePath = null, Exception? InnerException = null);

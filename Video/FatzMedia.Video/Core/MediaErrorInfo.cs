#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Internal payload used by <see cref="Core.Abstractions.IMediaErrorSource"/> to
/// communicate typed failure details from the pipeline to the host-facing
/// <see cref="PlayerCore"/> without exposing pipeline internals publicly.
/// </summary>
sealed record MediaErrorInfo(MediaErrorCode Code, string Message, string? SourcePath = null, Exception? InnerException = null);

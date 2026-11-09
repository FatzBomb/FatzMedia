namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Analyzes a local media file ahead of playback without touching the current media.
/// </summary>
internal interface IMediaFileAnalyzer
{
    /// <summary>
    /// Analyzes the file and warms the shared media-analysis cache. Returns <see langword="null"/>
    /// when the file is not covered by the analyzer or cannot be analyzed.
    /// </summary>
    Task<MediaAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>
/// Analyzer used when the audio engine cannot analyze files; every request reports no analysis.
/// </summary>
internal sealed class UnavailableMediaFileAnalyzer : IMediaFileAnalyzer
{
    public static UnavailableMediaFileAnalyzer Instance { get; } = new();

    private UnavailableMediaFileAnalyzer()
    {
    }

    public Task<MediaAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MediaAnalysisResult?>(null);
    }
}

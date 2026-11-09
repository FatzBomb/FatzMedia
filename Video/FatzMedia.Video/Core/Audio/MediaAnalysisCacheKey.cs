namespace FatzMedia.Video.Core.Audio;

readonly record struct MediaAnalysisCacheKey(
    string MediaIdentity,
    int AnalyzerVersion,
    SilenceTrimOptions TrimOptions)
{
    public static bool TryCreateForFile(
        string filePath,
        int analyzerVersion,
        SilenceTrimOptions trimOptions,
        out MediaAnalysisCacheKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(filePath) || analyzerVersion <= 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return false;
            }

            var normalizedPath = OperatingSystem.IsWindows()
                ? info.FullName.ToUpperInvariant()
                : info.FullName;
            key = new MediaAnalysisCacheKey(
                $"file|{normalizedPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}",
                analyzerVersion,
                trimOptions);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

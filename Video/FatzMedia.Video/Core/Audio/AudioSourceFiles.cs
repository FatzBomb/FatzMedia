using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Audio;

/// <summary>Source rules of the audio engine: tracker files and reading an archive entry into memory.</summary>
static class AudioSourceFiles
{
    static readonly HashSet<string> TrackerExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mod", ".it", ".s3m", ".xm", ".mo3", ".mtm", ".umx" };

    /// <summary>True for a tracker module (never silence-trimmed: its patterns may start or end quietly on purpose).</summary>
    internal static bool IsTrackerFormat(string filePath) =>
        TrackerExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Reads an archive entry into memory once. The stream's own buffer is used when the entry's size was known,
    /// instead of copying it out with <c>ToArray</c> (ENG-039 P5-9: the audio was held twice while opening); a
    /// wrong or missing size keeps the copy, so no oversized buffer is held.
    /// </summary>
    internal static (byte[] Data, int Length) ReadArchiveEntry(IArchiveEntryInfo archiveEntry)
    {
        var estimatedSize = (int)Math.Min(archiveEntry.Size ?? 0, int.MaxValue);
        using var memoryStream = new MemoryStream(estimatedSize);
        archiveEntry.WriteTo(memoryStream);

        var buffer = memoryStream.GetBuffer();
        return buffer.Length == memoryStream.Length
            ? (buffer, buffer.Length)
            : (memoryStream.ToArray(), (int)memoryStream.Length);
    }
}

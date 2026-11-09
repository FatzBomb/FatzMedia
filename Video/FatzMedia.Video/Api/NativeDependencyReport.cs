using System.Text.RegularExpressions;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>Outcome of locating and loading one native library at startup.</summary>
public enum NativeLibraryStatus
{
    /// <summary>Found and loaded.</summary>
    Loaded,

    /// <summary>Not found in any search path.</summary>
    Missing,

    /// <summary>Found, but not loaded because a library it needs alongside it is missing.</summary>
    NotLoaded,

    /// <summary>Found, but loading it failed (wrong architecture, wrong version, or a missing dependency).</summary>
    FailedToLoad,
}

/// <summary>One native library the platform looked for.</summary>
/// <param name="Component">The library family, such as <c>BASS</c> or <c>FFmpeg</c>.</param>
/// <param name="FileName">The exact file name the control needs.</param>
/// <param name="IsRequired">
/// Whether the player needs it to play anything (FFmpeg). BASS is never required: without its
/// core libraries audio is unavailable (<see cref="NativeDependencyReport.IsAudioAvailable"/>),
/// and its plugins only add formats.
/// </param>
/// <param name="Status">What happened when the platform looked for it.</param>
/// <param name="Path">Where it was found, when it was found.</param>
/// <param name="Hint">
/// A likely cause when the file is missing, such as another version of the same library in a
/// search path (for example <c>avcodec-62.dll</c> where <c>avcodec-63.dll</c> is needed).
/// </param>
public sealed record NativeLibraryCheck(
    string Component,
    string FileName,
    bool IsRequired,
    NativeLibraryStatus Status,
    string? Path = null,
    string? Hint = null);

/// <summary>
/// The native libraries the platform looked for at startup, so a host can tell the user exactly
/// which file is missing or the wrong version instead of a format silently failing to open.
/// </summary>
public sealed partial class NativeDependencyReport(IReadOnlyList<NativeLibraryCheck> libraries, IReadOnlyList<string> searchPaths)
{
    /// <summary>A report for a platform that does not check native libraries.</summary>
    public static NativeDependencyReport Empty { get; } = new([], []);

    public IReadOnlyList<NativeLibraryCheck> Libraries { get; } = libraries ?? throw new ArgumentNullException(nameof(libraries));

    /// <summary>The folders searched, in priority order.</summary>
    public IReadOnlyList<string> SearchPaths { get; } = searchPaths ?? throw new ArgumentNullException(nameof(searchPaths));

    /// <summary>
    /// Whether the audio engine's libraries (BASS) loaded. When false the player still starts:
    /// video plays without sound and media that needs audio does not open. True for a platform
    /// that does not check native libraries.
    /// </summary>
    public bool IsAudioAvailable { get; init; } = true;

    /// <summary>True when every required library loaded.</summary>
    public bool RequiredLoaded => Libraries.Where(static library => library.IsRequired).All(static library => library.Status == NativeLibraryStatus.Loaded);

    /// <summary>Required libraries that did not load: playback of their media type is unavailable.</summary>
    public IReadOnlyList<NativeLibraryCheck> RequiredProblems => [.. Libraries.Where(static library => library.IsRequired && library.Status != NativeLibraryStatus.Loaded)];

    /// <summary>Optional libraries that did not load: only the formats they add are affected.</summary>
    public IReadOnlyList<NativeLibraryCheck> OptionalProblems => [.. Libraries.Where(static library => !library.IsRequired && library.Status != NativeLibraryStatus.Loaded)];

    /// <summary>
    /// One readable line per library that did not load, required ones first; when audio is
    /// unavailable, a line saying so comes before the optional ones.
    /// </summary>
    public IReadOnlyList<string> Describe() =>
    [
        .. RequiredProblems.Select(DescribeLine),
        .. (IsAudioAvailable ? Array.Empty<string>() : new[] { AudioUnavailableLine }),
        .. OptionalProblems.Select(DescribeLine),
    ];

    /// <summary>The <see cref="Describe"/> line for a report without audio.</summary>
    public const string AudioUnavailableLine = "Audio is unavailable: the BASS libraries did not load. Video plays without sound; audio media will not open.";

    /// <summary>
    /// Returns a hint when <paramref name="expectedFileName"/> is versioned (<c>avcodec-63.dll</c>,
    /// <c>libavcodec.so.63</c>) and another version of the same library is among
    /// <paramref name="presentFileNames"/>.
    /// </summary>
    public static string? FindVersionHint(string expectedFileName, IEnumerable<string> presentFileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFileName);
        ArgumentNullException.ThrowIfNull(presentFileNames);
        if (VersionedName().Match(expectedFileName) is not { Success: true } expected)
            return null;

        foreach (var present in presentFileNames.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (VersionedName().Match(present) is { Success: true } candidate
                && string.Equals(candidate.Groups["stem"].Value, expected.Groups["stem"].Value, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Groups["suffix"].Value, expected.Groups["suffix"].Value, StringComparison.OrdinalIgnoreCase)
                && candidate.Groups["major"].Value != expected.Groups["major"].Value)
            {
                return $"Found {present}, a different version; this control needs {expectedFileName}.";
            }
        }

        return null;
    }

    private static string DescribeLine(NativeLibraryCheck library)
    {
        var kind = library.IsRequired ? "required" : "optional";
        var what = library.Status switch
        {
            NativeLibraryStatus.Missing => "is missing",
            NativeLibraryStatus.NotLoaded => $"was found at {library.Path} but not loaded because other {library.Component} files are missing",
            NativeLibraryStatus.FailedToLoad => $"was found at {library.Path} but failed to load",
            _ => "loaded",
        };
        var line = $"{library.Component} {library.FileName} ({kind}) {what}.";
        return library.Hint is null ? line : $"{line} {library.Hint}";
    }

    // name-63.dll (Windows) or libname.so.63 (Linux).
    [GeneratedRegex(@"^(?:(?<stem>.+?)-(?<major>\d+)(?<suffix>\.dll)|(?<stem>lib.+?)(?<suffix>\.so)\.(?<major>\d+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionedName();
}

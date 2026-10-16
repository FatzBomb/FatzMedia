#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Source classes of the capability matrix.
/// </summary>
public enum MediaSourceClass
{
    /// <summary>A local file.</summary>
    LocalFile = 0,

    /// <summary>An entry inside a local archive.</summary>
    Archive = 1,

    /// <summary>An in-memory image supplied by the host.</summary>
    InMemoryImage = 2,

    /// <summary>A finite network file with a known length.</summary>
    NetworkFile = 3,

    /// <summary>A live or unknown-length network stream (internet radio, live feed).</summary>
    NetworkLive = 4
}

/// <summary>
/// What a loaded source supports. Hosts and Core branch on these values instead of on the
/// source's address text.
/// </summary>
public sealed record MediaSourceCapabilities
{
    /// <summary>Gets the matrix row this source belongs to.</summary>
    public required MediaSourceClass SourceClass { get; init; }

    /// <summary>Gets whether Seek is accepted.</summary>
    public required bool IsSeekable { get; init; }

    /// <summary>Gets whether <c>Duration</c> is meaningful. Live sources report zero duration.</summary>
    public required bool IsDurationKnown { get; init; }

    /// <summary>Gets whether the source has no defined end (natural end means the stream dropped).</summary>
    public required bool IsLive { get; init; }

    /// <summary>Gets whether playback depends on a network buffer and raises buffering events.</summary>
    public required bool IsBuffered { get; init; }

    /// <summary>Gets whether the open applies the host's bounded <see cref="NetworkRetryPolicy"/>.</summary>
    public required bool SupportsRetry { get; init; }

    /// <summary>Gets whether the source can carry in-stream metadata (ICY/Ogg tags).</summary>
    public required bool HasStreamMetadata { get; init; }

    /// <summary>Gets whether sidecar/background auto-detection applies.</summary>
    public required bool SupportsSidecars { get; init; }

    /// <summary>Gets whether silence-trim and prepared-next analysis apply.</summary>
    public required bool SupportsAnalysis { get; init; }
}

/// <summary>
/// Result of connecting to a network source, used to pick its capability row.
/// </summary>
/// <param name="ContentLength">Total length in bytes, or <see langword="null"/> when the server did not report one.</param>
/// <param name="SupportsRangeRequests">Whether byte-range requests (seeking) are available.</param>
/// <param name="HasIcyMetadata">Whether the server announced ICY/Shoutcast metadata.</param>
public readonly record struct NetworkStreamProbe(long? ContentLength, bool SupportsRangeRequests, bool HasIcyMetadata);

/// <summary>
/// The source capability matrix. Rows: local file, archive entry, in-memory image, network
/// file, network live stream.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>Row</term><description>Seek / duration / live / buffered / retry / metadata / sidecars / analysis - events</description></listheader>
/// <item><term>LocalFile</term><description>yes / yes / no / no / no / no / yes / yes - existing events only.</description></item>
/// <item><term>Archive</term><description>yes / yes / no / no / no / no / yes (archive members) / no - existing events only.</description></item>
/// <item><term>InMemoryImage</term><description>no / no / no / no / no / no / no / no - existing events only.</description></item>
/// <item><term>NetworkFile</term><description>range-dependent / yes / no / yes / yes / ICY-dependent / no / no -
/// <c>BufferingChanged</c>, retry diagnostics, <c>MediaEnded</c> at end of file.</description></item>
/// <item><term>NetworkLive</term><description>no / no / yes / yes / yes / yes / no / no -
/// <c>BufferingChanged</c>, <c>StreamMetadataChanged</c>, retry diagnostics; <c>MediaEnded</c> when the stream drops.</description></item>
/// </list>
/// Cancellation: once a source is open, every row is stopped by Stop, Close, or a new Load. A
/// network open in progress is not interruptible: it runs on the control queue for at most
/// <see cref="NetworkRetryPolicy.ConnectTimeout"/> per attempt plus the retry delays, and
/// commands issued meanwhile run after it.
/// </remarks>
public static class MediaSourceCapabilityMatrix
{
    /// <summary>Gets the local-file row.</summary>
    public static MediaSourceCapabilities LocalFile { get; } = new()
    {
        SourceClass = MediaSourceClass.LocalFile,
        IsSeekable = true,
        IsDurationKnown = true,
        IsLive = false,
        IsBuffered = false,
        SupportsRetry = false,
        HasStreamMetadata = false,
        SupportsSidecars = true,
        SupportsAnalysis = true,
    };

    /// <summary>Gets the archive-entry row.</summary>
    public static MediaSourceCapabilities Archive { get; } = LocalFile with
    {
        SourceClass = MediaSourceClass.Archive,
        SupportsAnalysis = false,
    };

    /// <summary>Gets the in-memory image row.</summary>
    public static MediaSourceCapabilities InMemoryImage { get; } = new()
    {
        SourceClass = MediaSourceClass.InMemoryImage,
        IsSeekable = false,
        IsDurationKnown = false,
        IsLive = false,
        IsBuffered = false,
        SupportsRetry = false,
        HasStreamMetadata = false,
        SupportsSidecars = false,
        SupportsAnalysis = false,
    };

    /// <summary>
    /// Gets the capabilities a network source is assumed to have before it connects: the most
    /// conservative (live) row.
    /// </summary>
    public static MediaSourceCapabilities NetworkUnprobed { get; } = ForNetwork(new NetworkStreamProbe(null, false, false));

    /// <summary>Resolves the row for a source before any network probe.</summary>
    public static MediaSourceCapabilities For(MediaInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            MediaInputSourceKind.FilePath => LocalFile,
            MediaInputSourceKind.Bgra32Image => InMemoryImage,
            MediaInputSourceKind.NetworkUri => NetworkUnprobed,
            _ => InMemoryImage,
        };
    }

    /// <summary>
    /// Resolves the network row from what the connection reported: a known positive length makes
    /// a finite network file (seekable only with range support); anything else is live.
    /// </summary>
    public static MediaSourceCapabilities ForNetwork(NetworkStreamProbe probe)
    {
        var finite = probe.ContentLength is > 0;
        return new MediaSourceCapabilities
        {
            SourceClass = finite ? MediaSourceClass.NetworkFile : MediaSourceClass.NetworkLive,
            IsSeekable = finite && probe.SupportsRangeRequests,
            IsDurationKnown = finite,
            IsLive = !finite,
            IsBuffered = true,
            SupportsRetry = true,
            HasStreamMetadata = !finite || probe.HasIcyMetadata,
            SupportsSidecars = false,
            SupportsAnalysis = false,
        };
    }
}

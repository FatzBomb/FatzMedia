#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// How far network video reads ahead of playback (Stage 039A slice 2). A background reader
/// downloads and demuxes packets into a bounded queue, up to <see cref="MaxDuration"/> of media or
/// <see cref="MaxBytes"/> of packets, whichever is reached first, so a short network stall does
/// not interrupt playback. The buffer only reads forward: a seek inside it is served without a
/// network request, any other seek empties it and refills from the new position. Local files are
/// not affected. Network audio played through BASS (014A) keeps BASS's own buffer.
/// </summary>
public sealed record NetworkReadAheadPolicy
{
    /// <summary>Gets the default policy: 20 s or 64 MB ahead, buffering ends once 2 s are buffered.</summary>
    public static NetworkReadAheadPolicy Default { get; } = new();

    /// <summary>Gets the most media time read ahead (clamped to <c>1 s..10 min</c>; default 20 s).</summary>
    public TimeSpan MaxDuration
    {
        get;
        init => field = ClampTime(value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
    } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the most packet data held ahead (clamped to <c>1 MB..1 GB</c>; default 64 MB).</summary>
    public long MaxBytes
    {
        get;
        init => field = Math.Clamp(value, 1L << 20, 1L << 30);
    } = 64L << 20;

    /// <summary>
    /// Gets how much media must be buffered again before a buffering spell ends (clamped to
    /// <c>0.1 s..</c><see cref="MaxDuration"/>; default 2 s). A quarter of <see cref="MaxBytes"/>,
    /// or a full buffer, also ends it.
    /// </summary>
    public TimeSpan ResumeDuration
    {
        get => field < MaxDuration ? field : MaxDuration;
        init => field = ClampTime(value, TimeSpan.FromSeconds(0.1), TimeSpan.FromMinutes(10));
    } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how long the reader keeps reconnecting after the network drops mid-stream (clamped to
    /// <c>0..5 min</c>; default 30 s). Playback goes on from the buffer meanwhile, then reports
    /// buffering; once the source answers again the reader resumes where it stopped. Only a
    /// seekable source (a file with a known duration, an HLS/DASH VOD) can resume. Zero ends the
    /// stream at the first failed read, unless FFmpeg's own reconnects (<see cref="NetworkRetryPolicy"/>)
    /// recover it.
    /// </summary>
    public TimeSpan ReconnectFor
    {
        get;
        init => field = ClampTime(value, TimeSpan.Zero, TimeSpan.FromMinutes(5));
    } = TimeSpan.FromSeconds(30);

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}

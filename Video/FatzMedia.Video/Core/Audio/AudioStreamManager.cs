using System.Runtime.CompilerServices;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Threading;
using ManagedBass;
using ManagedBass.Fx;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Manages BASS audio stream lifecycle including creation, validation, and cleanup.
/// Handles multiple stream types (decode, source, tempo) and their complex interactions.
/// </summary>
sealed class AudioStreamManager : IDisposable
{
    #region Constants

    // ManagedBass omits BASS_POS_FLUSH from PositionFlags; bass.h defines it as 0x1000000.
    const PositionFlags FlushPositionFlag = (PositionFlags)0x1000000;

    // FxFreeSource (0x10000) does nothing for most streams (it is a TempoCreate flag), but it is
    // also BASS_MIDI_NOTEOFF1, which MIDI streams have always been created with: kept as it was
    // (ENG-028) until MIDI can be checked against a change.
    const BassFlags CreateStreamFlags =
        BassFlags.FxFreeSource | BassFlags.Decode | BassFlags.Float | BassFlags.Default;

    internal const BassFlags DecodeFlags =
        BassFlags.Decode | BassFlags.Float | BassFlags.Prescan;

    // Tracker byte seeking requires Prescan on the decode source wrapped by BASS_FX.
    const BassFlags TrackerPlaybackSourceFlags =
        BassFlags.Decode
        | BassFlags.Float
        | BassFlags.Prescan
        | BassFlags.MusicPositionReset
        | BassFlags.MusicStopBack;

    const BassFlags TrackerDecodeFlags =
        DecodeFlags | BassFlags.MusicPositionReset | BassFlags.MusicStopBack;

    const BassFlags TempoCreateFlags =
        BassFlags.Default | BassFlags.Float;

    #endregion

    #region Private Fields

    int _sourceHandle;
    bool _sourceHandleIsMusic;
    bool _decodeHandleIsMusic;
    ConvertedAudio? _converted;
    bool _loopPlayback;
    readonly TrackerPositionTimeline _trackerPositionTimeline = new();

    // Cached values to avoid repeated calculations
    volatile bool _disposed;

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the primary playback handle (tempo stream or source stream).
    /// </summary>
    public int PlayHandle { get; private set; }

    /// <summary>
    /// Gets the decode handle for analysis operations.
    /// </summary>
    public int DecodeHandle { get; private set; }

    /// <summary>
    /// Gets whether any streams are currently open.
    /// </summary>
    public bool IsOpen => PlayHandle != 0 || _sourceHandle != 0 || DecodeHandle != 0;

    /// <summary>
    /// Gets the cached duration in seconds, or calculates it if not cached.
    /// </summary>
    public double DurationSeconds
    {
        get
        {
            // Use cached value if available and valid
            if (field >= 0) return field;

            field = CalculateDuration();
            return field;
        }

        private set;
    } = -1;

    #endregion

    #region Public Methods

    static readonly HashSet<string> TrackerExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mod", ".it", ".s3m", ".xm", ".mo3", ".mtm", ".umx" };

    internal static bool IsTrackerFormat(string filePath) =>
        TrackerExtensions.Contains(Path.GetExtension(filePath));

    internal static BassFlags GetPlaybackSourceFlags(string filePath) =>
        IsTrackerFormat(filePath) ? TrackerPlaybackSourceFlags : CreateStreamFlags;

    /// <summary>
    /// Opens audio streams from a file path.
    /// Creates both decode and playback streams with tempo support.
    /// Tracker formats (MOD, IT, S3M, XM, etc.) use BASS_MusicLoad instead of BASS_StreamCreateFile.
    /// </summary>
    /// <param name="filePath">Path to the audio file</param>
    /// <returns>True if streams were created successfully</returns>
    public bool OpenFromPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        Close();
        if (FfmpegAudioFallback.RequiresConversion(filePath))
            return OpenConverted(filePath);

        try
        {
            if (IsTrackerFormat(filePath))
            {
                DecodeHandle = MBass.MusicLoad(filePath, 0, 0, TrackerDecodeFlags, 0);
                _sourceHandle = MBass.MusicLoad(filePath, 0, 0, GetPlaybackSourceFlags(filePath), 0);
                _decodeHandleIsMusic = true;
                _sourceHandleIsMusic = true;
            }
            else
            {
                DecodeHandle = MBass.CreateStream(filePath, Flags: DecodeFlags);
                _sourceHandle = MBass.CreateStream(filePath, Flags: CreateStreamFlags);
                _decodeHandleIsMusic = false;
                _sourceHandleIsMusic = false;
            }

            if (!ValidateStreamHandles())
            {
                // A claimed extension BASS cannot decode (e.g. ALAC in .m4a without bassalac).
                return FfmpegAudioFallback.CanRetryWithFfmpeg(filePath) && OpenConverted(filePath);
            }

            AttachTempoProcessor();
            ApplyLoopPlayback();
            ResetState();

            return PlayHandle != 0;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"OpenFromPath failed for '{filePath}': {ex.Message}", sourcePath: filePath, innerException: ex);
            Close(); // Ensure cleanup on failure
            return false;
        }
    }

    /// <summary>
    /// Opens a file no BASS plugin can decode through the FFmpeg fallback (Stage 033A): the
    /// converted float WAV gets the same decode and tempo streams as any other file.
    /// </summary>
    bool OpenConverted(string filePath)
    {
        try
        {
            // The load's token: Reset, Dispose and the host can stop a long conversion.
            var converted = FfmpegAudioFallback.Take(filePath, MediaOpenCancellation.Current);
            if (converted is null)
                return false;

            _converted = converted;
            DecodeHandle = converted.CreateStream(DecodeFlags);
            _sourceHandle = converted.CreateStream(CreateStreamFlags);
            _decodeHandleIsMusic = false;
            _sourceHandleIsMusic = false;
            if (!ValidateStreamHandles())
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"BASS could not open the FFmpeg-decoded audio of '{filePath}': {MBass.LastError}", sourcePath: filePath);
                Close();
                return false;
            }

            AttachTempoProcessor();
            ApplyLoopPlayback();
            ResetState();
            return PlayHandle != 0;
        }
        catch (OperationCanceledException)
        {
            // The load was cancelled; the caller reports it, not as a failure.
            Close();
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"FFmpeg fallback failed for '{filePath}': {ex.Message}", sourcePath: filePath, innerException: ex);
            Close();
            return false;
        }
    }

    /// <summary>
    /// Opens audio streams from an archive entry.
    /// Streams the entry to memory and creates BASS streams from the buffer.
    /// </summary>
    /// <param name="archiveEntry">Archive entry containing audio data</param>
    /// <returns>True if streams were created successfully</returns>
    public bool OpenFromArchive(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);

        Close();
        if (FfmpegAudioFallback.RequiresConversion(archiveEntry.Key))
            return OpenConvertedArchiveEntry(archiveEntry);

        try
        {
            (var audioData, var length) = ReadArchiveEntry(archiveEntry);

            var isTracker = IsTrackerFormat(archiveEntry.Key);

            if (isTracker)
            {
                DecodeHandle = MBass.MusicLoad(audioData, 0, length, TrackerDecodeFlags, 0);
                _sourceHandle = MBass.MusicLoad(audioData, 0, length, GetPlaybackSourceFlags(archiveEntry.Key), 0);
                _decodeHandleIsMusic = true;
                _sourceHandleIsMusic = true;
            }
            else
            {
                DecodeHandle = MBass.CreateStream(audioData, 0, length, DecodeFlags);
                _sourceHandle = MBass.CreateStream(audioData, 0, length, CreateStreamFlags);
                _decodeHandleIsMusic = false;
                _sourceHandleIsMusic = false;
            }

            if (!ValidateStreamHandles())
            {
                // A claimed extension BASS cannot decode, as for files on disk.
                return FfmpegAudioFallback.CanRetryWithFfmpeg(archiveEntry.Key) && OpenConvertedArchiveEntry(archiveEntry);
            }

            AttachTempoProcessor();
            ApplyLoopPlayback();
            ResetState();

            return PlayHandle != 0;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"OpenFromArchive failed: {ex.Message}", innerException: ex);
            Close(); // Ensure cleanup on failure
            return false;
        }
    }

    /// <summary>
    /// Reads an archive entry into memory once, for both BASS handles (each pins the array until
    /// it is freed). The stream's own buffer is used when the entry's size was known, instead of
    /// copying it out with <c>ToArray</c> (ENG-039 P5-9: the audio was held twice while opening);
    /// a wrong or missing size keeps the copy, so no oversized buffer stays pinned.
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

    /// <summary>
    /// Opens archived audio no BASS plugin decodes through the FFmpeg fallback, as for files on
    /// disk: the entry is copied to a scratch file for FFmpeg, which is deleted once converted
    /// (the converted WAV holds its own data).
    /// </summary>
    bool OpenConvertedArchiveEntry(IArchiveEntryInfo archiveEntry)
    {
        string tempPath;
        try
        {
            tempPath = TempMediaFile.CreateFromArchiveEntry(archiveEntry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"Extracting '{archiveEntry.Key}' for the FFmpeg fallback failed: {ex.Message}", innerException: ex);
            Close();
            return false;
        }

        try
        {
            return OpenConverted(tempPath);
        }
        finally
        {
            TempMediaFile.TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Opens a direct HTTP/HTTPS audio stream. Only the playback source is created: a second
    /// decode connection for analysis would double the network load, so network sources have no
    /// analysis handle. The caller sets BASS network timeouts before calling.
    /// </summary>
    /// <returns>True if the stream connected and the playback chain was created.</returns>
    public bool OpenFromUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        Close();

        try
        {
            _sourceHandle = MBass.CreateStream(url, 0, CreateStreamFlags, null, IntPtr.Zero);
            _sourceHandleIsMusic = false;
            DecodeHandle = 0;
            _decodeHandleIsMusic = false;
            if (_sourceHandle == 0)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioStreamManager", $"Network connect failed: {MBass.LastError}", sourcePath: url);
                return false;
            }

            IsNetworkSource = true;
            AttachTempoProcessor();
            ResetState();
            return PlayHandle != 0;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioStreamManager", $"OpenFromUrl failed: {ex.Message}", sourcePath: url, innerException: ex);
            Close();
            return false;
        }
    }

    /// <summary>Gets whether the open source is a network stream.</summary>
    public bool IsNetworkSource { get; private set; }

    long? _networkContentLength;

    /// <summary>
    /// Describes the connected network stream for the capability matrix. A stream is finite only
    /// when the server sent <c>Content-Length</c> or BASS reports a channel length; BASS returns
    /// the download-buffer size as <c>FILEPOS_END</c> for block-streamed (unknown-length) sources,
    /// so that value alone is not trusted. Range support comes from <c>Accept-Ranges: bytes</c>.
    /// </summary>
    public NetworkStreamProbe ProbeNetworkSource()
    {
        if (!IsNetworkSource || _sourceHandle == 0)
            return new NetworkStreamProbe(null, false, false);

        var headers = ReadMultiStringTag(_sourceHandle, TagType.HTTP);
        var icyHeaders = ReadMultiStringTag(_sourceHandle, TagType.ICY);
        var contentLength = TryReadContentLength(headers);
        if (contentLength is null && MBass.ChannelGetLength(_sourceHandle) > 0)
        {
            var end = MBass.StreamGetFilePosition(_sourceHandle, FileStreamPosition.End);
            contentLength = end > 0 ? end : null;
        }

        var acceptsRanges = headers.Any(static header =>
            header.StartsWith("Accept-Ranges:", StringComparison.OrdinalIgnoreCase)
            && header.Contains("bytes", StringComparison.OrdinalIgnoreCase));
        var hasIcy = icyHeaders.Count > 0
            || headers.Any(static header => header.StartsWith("icy-", StringComparison.OrdinalIgnoreCase));
        _networkContentLength = contentLength;
        return new NetworkStreamProbe(contentLength, acceptsRanges, hasIcy);
    }

    internal static long? TryReadContentLength(IEnumerable<string> headers)
    {
        foreach (var header in headers)
        {
            const string prefix = "Content-Length:";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(header.AsSpan(prefix.Length).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
                && length > 0)
            {
                return length;
            }
        }

        return null;
    }

    /// <summary>
    /// Samples the network buffer: bytes buffered ahead of decoding, the buffer target in bytes
    /// (network buffer length x stream bitrate), and whether the whole source is downloaded.
    /// </summary>
    public (long BufferedBytes, long TargetBytes, bool DownloadComplete) SampleNetworkBuffer()
    {
        if (!IsNetworkSource || _sourceHandle == 0)
            return (0, 0, true);

        var buffered = MBass.StreamGetFilePosition(_sourceHandle, FileStreamPosition.Buffer);
        var download = MBass.StreamGetFilePosition(_sourceHandle, FileStreamPosition.Download);
        var complete = _networkContentLength is { } length && download >= length;

        var bitrateKbps = MBass.ChannelGetAttribute(_sourceHandle, ChannelAttribute.Bitrate);
        if (!double.IsFinite(bitrateKbps) || bitrateKbps <= 0)
            bitrateKbps = 128;
        var targetBytes = (long)(bitrateKbps * 1000 / 8 * (MBass.NetBufferLength / 1000.0));
        return (buffered < 0 ? 0 : buffered, targetBytes, complete);
    }

    /// <summary>Gets the current ICY <c>StreamTitle</c> block, or <see langword="null"/>.</summary>
    public string? ReadIcyMetadata()
    {
        if (!IsNetworkSource || _sourceHandle == 0)
            return null;

        var pointer = MBass.ChannelGetTags(_sourceHandle, TagType.META);
        return pointer == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(pointer);
    }

    /// <summary>Gets the current Ogg comments of a chained Ogg stream.</summary>
    public IReadOnlyList<string> ReadOggComments() =>
        IsNetworkSource && _sourceHandle != 0 ? ReadMultiStringTag(_sourceHandle, TagType.OGG, utf8: true) : [];

    /// <summary>Gets the handle that receives network syncs (the connected source stream).</summary>
    public int NetworkSyncHandle => IsNetworkSource ? _sourceHandle : 0;

    static IReadOnlyList<string> ReadMultiStringTag(int handle, TagType tagType, bool utf8 = false)
    {
        var pointer = MBass.ChannelGetTags(handle, tagType);
        if (pointer == IntPtr.Zero)
            return [];

        var values = utf8 ? Extensions.ExtractMultiStringUtf8(pointer) : Extensions.ExtractMultiStringAnsi(pointer);
        return values ?? [];
    }

    /// <summary>
    /// Gets the appropriate handle for analysis operations (decode preferred).
    /// </summary>
    /// <returns>BASS handle for analysis, or 0 if none available</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetAnalysisHandle() => DecodeHandle != 0 ? DecodeHandle : PlayHandle;

    /// <summary>
    /// Gets the appropriate handle for position/duration operations.
    /// </summary>
    /// <returns>BASS handle for position operations, or 0 if none available</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetPositionHandle() => PlayHandle != 0 ? PlayHandle : DecodeHandle;

    public void SetLoopPlayback(bool enabled)
    {
        _loopPlayback = enabled;
        ApplyLoopPlayback();
    }

    /// <summary>
    /// Resets the position of all streams to the beginning.
    /// Safe operation that ignores failures during cleanup scenarios.
    /// </summary>
    public void ResetPosition()
    {
        _trackerPositionTimeline.Reset();
        ResetPosition(PlayHandle);
        if (DecodeHandle != 0 && DecodeHandle != PlayHandle)
        {
            ResetPosition(DecodeHandle);
        }
    }

    /// <summary>
    /// Calculates the current position in seconds from the playback handle.
    /// </summary>
    /// <returns>Position in seconds, or 0.0 if no streams available</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double GetCurrentPosition()
    {
        var handle = GetPositionHandle();
        if (handle == 0) return 0.0;

        var position = MBass.ChannelGetPosition(handle);
        var seconds = MBass.ChannelBytes2Seconds(handle, position);

        if (_sourceHandleIsMusic && TryGetTrackerOrderRow(out var order, out var row))
        {
            seconds = _trackerPositionTimeline.Resolve(order, row, seconds);
        }

        return double.IsFinite(seconds) ? Math.Max(0.0, seconds) : 0.0;
    }

    /// <summary>
    /// Sets the current position in seconds on the playback handle.
    /// </summary>
    /// <param name="seconds">Target position in seconds</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCurrentPosition(double seconds, bool flush = false)
    {
        var handle = GetPositionHandle();
        if (handle == 0) return;

        _trackerPositionTimeline.Reset();
        var bytes = MBass.ChannelSeconds2Bytes(handle, Math.Max(0.0, seconds));
        var positionFlags = flush
            ? PositionFlags.Bytes | FlushPositionFlag
            : PositionFlags.Bytes;
        if (!MBass.ChannelSetPosition(handle, bytes, positionFlags))
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "AudioStreamManager",
                $"Audio seek failed at {seconds:F3}s: {MBass.LastError}");
        }
    }

    /// <summary>
    /// Closes all streams and resets state.
    /// Safe operation that handles cleanup failures gracefully.
    /// </summary>
    public void Close()
    {
        if (_disposed) return;

        // Stop playback first
        if (PlayHandle != 0)
        {
            SafeExecute(() => MBass.ChannelStop(PlayHandle));

            // Free tempo stream if it's different from source
            if (PlayHandle != _sourceHandle)
            {
                SafeExecute(() => MBass.StreamFree(PlayHandle));
            }
        }

        // Free source and decode streams
        if (_sourceHandle != 0)
        {
            var sourceHandle = _sourceHandle;
            var sourceHandleIsMusic = _sourceHandleIsMusic;
            SafeExecute(() => FreeSourceHandle(sourceHandle, sourceHandleIsMusic));
        }

        if (DecodeHandle != 0)
        {
            var decodeHandle = DecodeHandle;
            var decodeHandleIsMusic = _decodeHandleIsMusic;
            SafeExecute(() => FreeSourceHandle(decodeHandle, decodeHandleIsMusic));
        }

        // The converted WAV backs the streams just freed.
        _converted?.Dispose();
        _converted = null;

        // Reset all handles and state
        PlayHandle = _sourceHandle = DecodeHandle = 0;
        IsNetworkSource = false;
        _networkContentLength = null;
        _sourceHandleIsMusic = false;
        _decodeHandleIsMusic = false;
        _trackerPositionTimeline.Reset();
        DurationSeconds = -1;
    }

    /// <summary>
    /// Disposes of all resources and closes streams.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
bool ValidateStreamHandles()
    {
        if (_sourceHandle != 0)
            return true;

        if (DecodeHandle != 0)
        {
            // Source failed but decode succeeded - cleanup and fail
            _ = FreeSourceHandle(DecodeHandle, _decodeHandleIsMusic);
            DecodeHandle = 0;
            _decodeHandleIsMusic = false;
        }

        _sourceHandleIsMusic = false;
        return false;
    }

    void AttachTempoProcessor()
    {
        if (_sourceHandle == 0)
        {
            PlayHandle = 0;
            return;
        }

        // Attempt to create tempo stream for pitch/tempo control
        PlayHandle = BassFx.TempoCreate(_sourceHandle, TempoCreateFlags);

        // Fallback to source stream if tempo creation fails
        if (PlayHandle == 0)
        {
            PlayHandle = _sourceHandle;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioStreamManager", "Tempo stream creation failed, using source stream");
        }
    }

    void ApplyLoopPlayback()
    {
        // A live stream cannot restart: network sources never get the native loop flag.
        var enabled = _loopPlayback && !IsNetworkSource;
        SetLoopFlag(_sourceHandle, enabled);
        if (PlayHandle != _sourceHandle)
            SetLoopFlag(PlayHandle, enabled);
    }

    static void SetLoopFlag(int handle, bool enabled)
    {
        if (handle == 0)
            return;

        var requested = enabled ? BassFlags.Loop : BassFlags.Default;
        var updated = MBass.ChannelFlags(handle, requested, BassFlags.Loop);
        if (updated.HasFlag(BassFlags.Loop) != enabled)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "AudioStreamManager",
                $"Could not {(enabled ? "enable" : "disable")} native audio looping: {MBass.LastError}");
        }
    }

    bool TryGetTrackerOrderRow(out int order, out int row)
    {
        var positionHandle = GetPositionHandle();
        var orderRow = MBass.ChannelGetPosition(positionHandle, PositionFlags.MusicOrders);
        if (orderRow < 0 && positionHandle != _sourceHandle)
        {
            orderRow = MBass.ChannelGetPosition(_sourceHandle, PositionFlags.MusicOrders);
        }

        if (orderRow < 0)
        {
            order = 0;
            row = 0;
            return false;
        }

        order = (int)(orderRow & 0xFFFF);
        row = (int)((orderRow >> 16) & 0xFFFF);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
void ResetState() => DurationSeconds = -1; // Invalidate duration cache

    double CalculateDuration()
    {
        if (PlayHandle == 0 && DecodeHandle == 0) return 0.0;

        var handle = PlayHandle != 0 ? PlayHandle : DecodeHandle;
        var length = MBass.ChannelGetLength(handle);

        // Fallback to decode handle if play handle length is invalid
        if (length <= 0 && DecodeHandle != 0 && DecodeHandle != handle)
        {
            length = MBass.ChannelGetLength(DecodeHandle);
            handle = DecodeHandle;
        }

        if (length <= 0) return 0.0;

        var seconds = MBass.ChannelBytes2Seconds(handle, length);
        return double.IsFinite(seconds) ? Math.Max(0.0, seconds) : 0.0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
static void ResetPosition(int handle)
    {
        if (handle != 0)
        {
            try { _ = MBass.ChannelSetPosition(handle, 0); }
            catch { /* Ignore reset failures during cleanup */ }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
static void SafeExecute(Action action)
    {
        try { action(); }
        catch { /* Ignore cleanup failures */ }
    }

    static bool FreeSourceHandle(int handle, bool isMusic) =>
        isMusic ? MBass.MusicFree(handle) : MBass.StreamFree(handle);

    #endregion
}

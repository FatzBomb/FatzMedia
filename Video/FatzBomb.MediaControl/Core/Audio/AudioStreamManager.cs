using FatzBomb.MediaControl.Core.Abstractions;
using ManagedBass;
using ManagedBass.Fx;
using System.Runtime.CompilerServices;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Manages BASS audio stream lifecycle including creation, validation, and cleanup.
/// Handles multiple stream types (decode, source, tempo) and their complex interactions.
/// </summary>
internal sealed class AudioStreamManager : IDisposable
{
    #region Constants

    // ManagedBass omits BASS_POS_FLUSH from PositionFlags; bass.h defines it as 0x1000000.
    private const PositionFlags FlushPositionFlag = (PositionFlags)0x1000000;

    private const BassFlags CreateStreamFlags =
        BassFlags.FxFreeSource | BassFlags.Decode | BassFlags.Float | BassFlags.Default;

    internal const BassFlags DecodeFlags =
        BassFlags.Decode | BassFlags.Float | BassFlags.Prescan;

    // Tracker byte seeking requires Prescan on the decode source wrapped by BASS_FX.
    private const BassFlags TrackerPlaybackSourceFlags =
        BassFlags.Decode
        | BassFlags.Float
        | BassFlags.Prescan
        | BassFlags.MusicPositionReset
        | BassFlags.MusicStopBack;

    private const BassFlags TrackerDecodeFlags =
        DecodeFlags | BassFlags.MusicPositionReset | BassFlags.MusicStopBack;

    private const BassFlags TempoCreateFlags =
        BassFlags.Default | BassFlags.Float;

    #endregion

    #region Private Fields

    private int _sourceHandle;
    private bool _sourceHandleIsMusic;
    private bool _decodeHandleIsMusic;
    private bool _loopPlayback;
    private readonly TrackerPositionTimeline _trackerPositionTimeline = new();

    // Cached values to avoid repeated calculations
    private volatile bool _disposed;

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

    private static readonly HashSet<string> TrackerExtensions = new(StringComparer.OrdinalIgnoreCase)
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
                return false;
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
    /// Opens audio streams from an archive entry.
    /// Streams the entry to memory and creates BASS streams from the buffer.
    /// </summary>
    /// <param name="archiveEntry">Archive entry containing audio data</param>
    /// <returns>True if streams were created successfully</returns>
    public bool OpenFromArchive(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);

        Close();

        try
        {
            var estimatedSize = (int)Math.Min(archiveEntry.Size ?? 0, int.MaxValue);
            using var memoryStream = new MemoryStream(estimatedSize);

            archiveEntry.WriteTo(memoryStream);
            var audioData = memoryStream.ToArray();

            var isTracker = IsTrackerFormat(archiveEntry.Key);

            if (isTracker)
            {
                DecodeHandle = MBass.MusicLoad(audioData, 0, audioData.Length, TrackerDecodeFlags, 0);
                _sourceHandle = MBass.MusicLoad(audioData, 0, audioData.Length, GetPlaybackSourceFlags(archiveEntry.Key), 0);
                _decodeHandleIsMusic = true;
                _sourceHandleIsMusic = true;
            }
            else
            {
                DecodeHandle = MBass.CreateStream(audioData, 0, audioData.Length, DecodeFlags);
                _sourceHandle = MBass.CreateStream(audioData, 0, audioData.Length, CreateStreamFlags);
                _decodeHandleIsMusic = false;
                _sourceHandleIsMusic = false;
            }

            if (!ValidateStreamHandles())
            {
                return false;
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

        if (_sourceHandleIsMusic && TryGetTrackerOrderRow(out int order, out int row))
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
            int sourceHandle = _sourceHandle;
            bool sourceHandleIsMusic = _sourceHandleIsMusic;
            SafeExecute(() => FreeSourceHandle(sourceHandle, sourceHandleIsMusic));
        }

        if (DecodeHandle != 0)
        {
            int decodeHandle = DecodeHandle;
            bool decodeHandleIsMusic = _decodeHandleIsMusic;
            SafeExecute(() => FreeSourceHandle(decodeHandle, decodeHandleIsMusic));
        }

        // Reset all handles and state
        PlayHandle = _sourceHandle = DecodeHandle = 0;
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
    private bool ValidateStreamHandles()
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

    private void AttachTempoProcessor()
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

    private void ApplyLoopPlayback()
    {
        SetLoopFlag(_sourceHandle, _loopPlayback);
        if (PlayHandle != _sourceHandle)
            SetLoopFlag(PlayHandle, _loopPlayback);
    }

    private static void SetLoopFlag(int handle, bool enabled)
    {
        if (handle == 0)
            return;

        BassFlags requested = enabled ? BassFlags.Loop : BassFlags.Default;
        BassFlags updated = MBass.ChannelFlags(handle, requested, BassFlags.Loop);
        if (updated.HasFlag(BassFlags.Loop) != enabled)
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "AudioStreamManager",
                $"Could not {(enabled ? "enable" : "disable")} native audio looping: {MBass.LastError}");
        }
    }

    private bool TryGetTrackerOrderRow(out int order, out int row)
    {
        int positionHandle = GetPositionHandle();
        long orderRow = MBass.ChannelGetPosition(positionHandle, PositionFlags.MusicOrders);
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
    private void ResetState() => DurationSeconds = -1; // Invalidate duration cache

    private double CalculateDuration()
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
    private static void ResetPosition(int handle)
    {
        if (handle != 0)
        {
            try { _ = MBass.ChannelSetPosition(handle, 0); }
            catch { /* Ignore reset failures during cleanup */ }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SafeExecute(Action action)
    {
        try { action(); }
        catch { /* Ignore cleanup failures */ }
    }

    private static bool FreeSourceHandle(int handle, bool isMusic) =>
        isMusic ? MBass.MusicFree(handle) : MBass.StreamFree(handle);

    #endregion
}

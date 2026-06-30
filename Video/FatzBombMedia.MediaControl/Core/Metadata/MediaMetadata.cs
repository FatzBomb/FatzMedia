namespace FatzBombMedia.MediaControl.Core.Metadata;

/// <summary>
/// Comprehensive media file metadata information.
/// Populated by engines during file opening for debugging, UI display, and diagnostics.
/// </summary>
public sealed class MediaMetadata
{
    #region Audio Metadata

    /// <summary>
    /// Audio sample rate in Hz (e.g., 44100, 48000, 96000).
    /// </summary>
    public int? AudioSampleRate { get; set; }

    /// <summary>
    /// Number of audio channels (1=mono, 2=stereo, 6=5.1, etc.).
    /// </summary>
    public int? AudioChannels { get; set; }

    /// <summary>
    /// Audio bitrate in kbps (e.g., 320, 128).
    /// </summary>
    public int? AudioBitrate { get; set; }

    /// <summary>
    /// Audio codec name (e.g., "MP3", "FLAC", "AAC").
    /// </summary>
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Audio duration in seconds (from audio stream).
    /// </summary>
    public double? AudioDurationSeconds { get; set; }

    #endregion

    #region Video Metadata

    /// <summary>
    /// Video width in pixels.
    /// </summary>
    public int? VideoWidth { get; set; }

    /// <summary>
    /// Video height in pixels.
    /// </summary>
    public int? VideoHeight { get; set; }

    /// <summary>
    /// Video frame rate (e.g., 23.976, 25.0, 29.97, 60.0).
    /// </summary>
    public double? VideoFrameRate { get; set; }

    /// <summary>
    /// Video codec name (e.g., "H.264", "H.265", "VP9").
    /// </summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Video duration in seconds (from video stream).
    /// </summary>
    public double? VideoDurationSeconds { get; set; }

    /// <summary>
    /// Video aspect ratio (e.g., "16:9", "4:3").
    /// </summary>
    public string? AspectRatio { get; set; }

    #endregion

    #region CDG Metadata

    /// <summary>
    /// Whether CDG graphics are present.
    /// </summary>
    public bool HasCdg { get; set; }

    /// <summary>
    /// CDG track duration in seconds.
    /// </summary>
    public double? CdgDurationSeconds { get; set; }

    /// <summary>
    /// Number of CDG instructions/commands.
    /// </summary>
    public int? CdgCommandCount { get; set; }

    #endregion

    #region File Metadata

    /// <summary>
    /// Original file path.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// File format/container (e.g., "MP4", "AVI", "MKV", "MP3+G").
    /// </summary>
    public string? FileFormat { get; set; }

    /// <summary>
    /// Overall file duration in seconds (longest stream).
    /// </summary>
    public double? TotalDurationSeconds { get; set; }

    #endregion

    #region Engine Information

    /// <summary>
    /// Which engine is handling audio playback.
    /// </summary>
    public string? AudioEngineUsed { get; set; }

    /// <summary>
    /// Which engine is handling video playback.
    /// </summary>
    public string? VideoEngineUsed { get; set; }

    /// <summary>
    /// Device sample rate actually used for audio playback.
    /// </summary>
    public int? DeviceSampleRate { get; set; }

    /// <summary>
    /// Whether resampling is occurring (file rate != device rate).
    /// </summary>
    public bool IsResampling => AudioSampleRate.HasValue && DeviceSampleRate.HasValue
        && AudioSampleRate.Value != DeviceSampleRate.Value;

    #endregion

    #region Duration Analysis

    /// <summary>
    /// Longest available stream duration.
    /// </summary>
    public double MasterDurationSeconds
    {
        get
        {
            var durations = new[]
            {
                AudioDurationSeconds ?? 0,
                VideoDurationSeconds ?? 0,
                CdgDurationSeconds ?? 0
            };
            return durations.Max();
        }
    }

    /// <summary>
    /// Gets duration mismatch information for diagnostics.
    /// </summary>
    public string GetDurationMismatchInfo()
    {
        var info = new List<string>();

        if (AudioDurationSeconds.HasValue)
            info.Add($"Audio: {AudioDurationSeconds:F2}s");
        if (VideoDurationSeconds.HasValue)
            info.Add($"Video: {VideoDurationSeconds:F2}s");
        if (CdgDurationSeconds.HasValue)
            info.Add($"CDG: {CdgDurationSeconds:F2}s");

        if (info.Count <= 1) return "No duration mismatches";

        var master = MasterDurationSeconds;
        info.Add($"Master: {master:F2}s");

        return string.Join(", ", info);
    }

    #endregion

    #region Additional Properties

    /// <summary>
    /// Custom metadata fields for engine-specific information.
    /// </summary>
    public Dictionary<string, object> ExtendedProperties { get; } = [];

    /// <summary>
    /// Timestamp when metadata was populated.
    /// </summary>
    public DateTime PopulatedAt { get; set; } = DateTime.UtcNow;

    #endregion

    #region Hardware and Performance Metadata

    /// <summary>
    /// Whether hardware acceleration is being used for video decoding.
    /// </summary>
    public bool VideoHardwareAccelerated { get; set; }

    /// <summary>
    /// Hardware acceleration method used (e.g., "D3D11VA", "D3D12VA", "DXVA2").
    /// </summary>
    public string? VideoHardwareMethod { get; set; }

    /// <summary>
    /// Current video frame buffer size (number of frames buffered).
    /// </summary>
    public int VideoBufferedFrameCount { get; set; }

    /// <summary>
    /// Maximum video frame buffer size.
    /// </summary>
    public int VideoMaxBufferSize { get; set; } = 16;

    /// <summary>
    /// Whether video is currently buffering.
    /// </summary>
    public bool VideoIsBuffering { get; set; }

    /// <summary>
    /// Average decoding time in milliseconds per frame.
    /// </summary>
    public double? AverageDecodeTimeMs { get; set; }

    /// <summary>
    /// Whether audio normalization/gain is being applied.
    /// </summary>
    public bool AudioNormalizationEnabled { get; set; }

    /// <summary>
    /// Current audio normalization gain in dB.
    /// </summary>
    public double? AudioNormalizationGainDb { get; set; }

    /// <summary>
    /// Whether tempo adjustment is being used.
    /// </summary>
    public bool AudioTempoAdjustmentEnabled { get; set; }

    /// <summary>
    /// Current tempo adjustment percentage (100 = normal speed).
    /// </summary>
    public double? AudioTempoPercent { get; set; }

    #endregion

    #region System Performance

    /// <summary>
    /// Actual rendering FPS being achieved.
    /// </summary>
    public double? ActualRenderingFPS { get; set; }

    /// <summary>
    /// Target rendering FPS based on content.
    /// </summary>
    public double? TargetRenderingFPS { get; set; }

    /// <summary>
    /// Whether the system is experiencing frame drops.
    /// </summary>
    public bool HasFrameDrops { get; set; }

    /// <summary>
    /// Current memory usage estimate in MB.
    /// </summary>
    public double? EstimatedMemoryUsageMB { get; set; }

    /// <summary>
    /// System's performance mode (e.g., "Power Saver", "Balanced", "High Performance").
    /// </summary>
    public string? PowerMode { get; set; }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets a formatted string representation for debugging/logging.
    /// </summary>
    public string GetSummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(FileFormat))
            parts.Add($"Format: {FileFormat}");

        if (AudioSampleRate.HasValue)
            parts.Add($"Audio: {AudioSampleRate}Hz");

        if (VideoFrameRate.HasValue)
            parts.Add($"Video: {VideoFrameRate:F2}fps");

        if (HasCdg)
            parts.Add("CDG: Yes");

        if (IsResampling)
            parts.Add($"Resampling: {AudioSampleRate}→{DeviceSampleRate}Hz");

        if (VideoHardwareAccelerated && !string.IsNullOrEmpty(VideoHardwareMethod))
            parts.Add($"HW Accel: {VideoHardwareMethod}");

        if (VideoBufferedFrameCount > 0)
            parts.Add($"Buffer: {VideoBufferedFrameCount}/{VideoMaxBufferSize}");

        if (AudioNormalizationEnabled && AudioNormalizationGainDb.HasValue)
            parts.Add($"Gain: {AudioNormalizationGainDb:F1}dB");

        return string.Join(", ", parts);
    }

    #endregion
}

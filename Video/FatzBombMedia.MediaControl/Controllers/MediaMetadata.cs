using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Provides access to media file metadata and information.
/// </summary>
public sealed class MediaMetadata
{
    private readonly IMediaEngine _mediaEngine;
    private Core.Metadata.MediaMetadata? _cachedMetadata;

    internal MediaMetadata(IMediaEngine mediaEngine)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
    }

    /// <summary>
    /// Gets the current media metadata. Returns null if no media is loaded.
    /// </summary>
    public Core.Metadata.MediaMetadata? Current
    {
        get
        {
            if (!_mediaEngine.IsOpen) return null;
            
            return _cachedMetadata;
        }
    }

    /// <summary>
    /// Gets a formatted summary string of the current media.
    /// </summary>
    public string? Summary => Current?.GetSummary();

    /// <summary>
    /// Gets whether the current media requires resampling.
    /// </summary>
    public bool IsResampling => Current?.IsResampling ?? false;

    /// <summary>
    /// Refreshes metadata by analyzing the current media.
    /// Called internally when media is loaded.
    /// </summary>
    internal void Refresh()
    {
        if (!_mediaEngine.IsOpen)
        {
            _cachedMetadata = null;
            return;
        }

        // Create new metadata and populate from engines
        _cachedMetadata = new Core.Metadata.MediaMetadata();
        PopulateFromEngines(_cachedMetadata);
    }

    private void PopulateFromEngines(Core.Metadata.MediaMetadata metadata)
    {
        // Populate basic file info
        metadata.PopulatedAt = DateTime.UtcNow;

        // Populate from audio engine
        if (_mediaEngine.Audio != null && _mediaEngine.Audio.PlayHandle != 0)
        {
            metadata.AudioEngineUsed = "BASS";
            metadata.AudioDurationSeconds = _mediaEngine.Audio.Duration;
            
            // Try to get more detailed audio information from BASS
            try
            {
                var channelInfo = ManagedBass.Bass.ChannelGetInfo(_mediaEngine.Audio.PlayHandle);
                metadata.AudioSampleRate = (int)channelInfo.Frequency;
                metadata.AudioChannels = channelInfo.Channels;
                
                // Simple codec detection based on what BASS reports
                metadata.AudioCodec = "Unknown";

                // Check if resampling is occurring
                if (ManagedBass.Bass.GetInfo(out var deviceInfo) && metadata.AudioSampleRate.HasValue)
                {
                    metadata.DeviceSampleRate = (int)deviceInfo.SampleRate;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaMetadata] Could not get detailed audio info: {ex.Message}");
            }
        }

        // Populate from video engine
        if (_mediaEngine.Video != null)
        {
            metadata.VideoEngineUsed = "FFmpeg";
            metadata.VideoDurationSeconds = _mediaEngine.Video.Duration;
            metadata.VideoFrameRate = _mediaEngine.Video.FramesPerSecond;
            metadata.VideoWidth = _mediaEngine.Video.Width;
            metadata.VideoHeight = _mediaEngine.Video.Height;
            metadata.VideoCodec = _mediaEngine.Video.CodecName;
            metadata.VideoHardwareAccelerated = _mediaEngine.Video.IsHardwareAccelerated;
            metadata.VideoHardwareMethod = _mediaEngine.Video.HardwareAccelerationMethod;
            
            // Calculate aspect ratio
            if (metadata.VideoWidth > 0 && metadata.VideoHeight > 0)
            {
                var gcd = CalculateGCD(metadata.VideoWidth.Value, metadata.VideoHeight.Value);
                var aspectW = metadata.VideoWidth.Value / gcd;
                var aspectH = metadata.VideoHeight.Value / gcd;
                metadata.AspectRatio = $"{aspectW}:{aspectH}";
            }
            
            System.Diagnostics.Debug.WriteLine($"[MediaMetadata] Video: {metadata.VideoWidth}x{metadata.VideoHeight}, {metadata.VideoFrameRate:F2}fps, {metadata.VideoCodec}, {metadata.AspectRatio}");
        }

        // Populate from CDG engine
        if (_mediaEngine.Cdg != null)
        {
            metadata.HasCdg = true;
            metadata.CdgDurationSeconds = _mediaEngine.Cdg.TotalDurationSeconds;
            
            // Count CDG commands/packets for complexity assessment
            // This is an approximation based on duration
            if (metadata.CdgDurationSeconds > 0)
            {
                metadata.CdgCommandCount = (int)(metadata.CdgDurationSeconds.Value * 300); // 300 packets/second
            }
        }

        // Determine master duration (longest stream)
        var durations = new[] 
        { 
            metadata.AudioDurationSeconds ?? 0, 
            metadata.VideoDurationSeconds ?? 0, 
            metadata.CdgDurationSeconds ?? 0 
        };
        metadata.TotalDurationSeconds = durations.Max();

        // Set content type based on loaded engines
        var contentParts = new List<string>();
        if (_mediaEngine.Content.HasFlag(MediaContent.Audio)) contentParts.Add("Audio");
        if (_mediaEngine.Content.HasFlag(MediaContent.Video)) contentParts.Add("Video");  
        if (_mediaEngine.Content.HasFlag(MediaContent.Cdg)) contentParts.Add("CDG");
        
        metadata.FileFormat = contentParts.Count > 0 ? string.Join("+", contentParts) : "Unknown";
        
        System.Diagnostics.Debug.WriteLine($"[MediaMetadata] Populated: {metadata.GetSummary()}");
    }
    
    /// <summary>
    /// Calculates the Greatest Common Divisor for aspect ratio calculation.
    /// </summary>
    private static int CalculateGCD(int a, int b)
    {
        while (b != 0)
        {
            int temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }
}

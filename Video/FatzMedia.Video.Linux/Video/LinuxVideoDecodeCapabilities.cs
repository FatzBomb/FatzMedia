using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Video;

/// <summary>
/// Software-decode capabilities from the loaded FFmpeg build. Hardware decode (VA-API/VDPAU)
/// is a later slice, so every codec reports software decode only.
/// </summary>
static unsafe class LinuxVideoDecodeCapabilities
{
    static readonly Lock CacheLock = new();
    static VideoDecodeCapabilities? _cached;

    public static VideoDecodeCapabilities GetOrScan(bool ffmpegLoaded)
    {
        if (!ffmpegLoaded)
            return VideoDecodeCapabilities.Create([], []);

        if (_cached is not null)
            return _cached;

        lock (CacheLock)
            return _cached ??= Scan();
    }

    static VideoDecodeCapabilities Scan()
    {
        var capabilities = new List<CodecCapability>();
        var codecIds = new List<(AVCodecID CodecId, CodecCapability Capability)>();
        var seen = new HashSet<AVCodecID>();
        void* codecOpaque = null;
        while (true)
        {
            var codec = ffmpeg.av_codec_iterate(&codecOpaque);
            if (codec == null)
                break;

            if (ffmpeg.av_codec_is_decoder(codec) == 0 || !seen.Add(codec->id))
                continue;

            var decoderName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            var descriptor = ffmpeg.avcodec_descriptor_get(codec->id);
            var canonicalName = descriptor != null ? Marshal.PtrToStringAnsi((IntPtr)descriptor->name) ?? decoderName : decoderName;
            var friendlyName = descriptor != null ? Marshal.PtrToStringAnsi((IntPtr)descriptor->long_name) ?? canonicalName : canonicalName;
            var capability = new CodecCapability(canonicalName, friendlyName, true, decoderName, false, null);
            capabilities.Add(capability);
            codecIds.Add((codec->id, capability));
        }

        var containers = new Dictionary<string, ContainerCapability>(StringComparer.OrdinalIgnoreCase);
        void* demuxerOpaque = null;
        while (true)
        {
            var format = ffmpeg.av_demuxer_iterate(&demuxerOpaque);
            if (format == null)
                break;

            var formatName = Marshal.PtrToStringAnsi((IntPtr)format->name) ?? "unknown";
            var friendlyName = Marshal.PtrToStringAnsi((IntPtr)format->long_name) ?? formatName;
            var extensions = Marshal.PtrToStringAnsi((IntPtr)format->extensions);
            if (string.IsNullOrWhiteSpace(extensions))
                continue;

            var possible = new List<CodecCapability>();
            if (format->codec_tag != null)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach ((var codecId, var capability) in codecIds)
                {
                    uint tag = 0;
                    if (ffmpeg.av_codec_get_tag2(format->codec_tag, codecId, &tag) != 0 && names.Add(capability.CodecName))
                        possible.Add(capability);
                }
            }

            foreach (var extension in extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
                if (normalized == ".*")
                    continue;

                containers[normalized] = containers.TryGetValue(normalized, out var existing)
                    ? new ContainerCapability(
                        existing.Extension,
                        existing.FormatName + ", " + formatName,
                        existing.FriendlyName + ", " + friendlyName,
                        [.. existing.PossibleCodecs.Union(possible).DistinctBy(static c => c.CodecName, StringComparer.OrdinalIgnoreCase)],
                        false)
                    : new ContainerCapability(normalized, formatName, friendlyName, possible, false);
            }
        }

        return VideoDecodeCapabilities.Create(capabilities, containers.Values);
    }
}

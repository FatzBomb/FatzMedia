using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Windows.Video;

internal static unsafe class HardwareDecoderScanner
{
    internal static VideoDecodeCapabilities Scan()
    {
        HashSet<Guid> supportedDxvaProfiles = D3D11ProfileEnumerator.EnumerateProfiles();
        Debug.WriteLine($"[HardwareDecoderScanner] DXVA profiles enumerated: {supportedDxvaProfiles.Count}");
        foreach (Guid profile in supportedDxvaProfiles)
            Debug.WriteLine($"[HardwareDecoderScanner]   Profile: {profile:B}");

        (List<CodecCapability>? capabilities, List<(AVCodecID codecId, CodecCapability capability)>? codecIds) = ScanDecoders(supportedDxvaProfiles);

        var hwCodecs = capabilities.Where(c => c.HardwareDecodeAvailable).ToList();
        var swCodecs = capabilities.Where(c => !c.HardwareDecodeAvailable).ToList();
        Debug.WriteLine($"[HardwareDecoderScanner] Scan complete: {hwCodecs.Count} HW-accelerated, {swCodecs.Count} software-only");
        foreach (CodecCapability? c in hwCodecs)
            Debug.WriteLine($"[HardwareDecoderScanner]   HW: {c.CodecName} ({c.FriendlyName}) via {c.HardwareMethod}");
        foreach (CodecCapability? c in swCodecs.Where(c => c.HardwareMethod != null))
            Debug.WriteLine($"[HardwareDecoderScanner]   SW(HW-candidate): {c.CodecName} ({c.FriendlyName}) via {c.HardwareMethod}");

        Dictionary<string, ContainerCapability> containerCapabilities = ScanDemuxers(codecIds);

        return new VideoDecodeCapabilities(
            capabilities.ToDictionary(c => c.CodecName.ToLowerInvariant()),
            containerCapabilities
        );
    }

    private static (List<CodecCapability> capabilities, List<(AVCodecID codecId, CodecCapability capability)> codecIds)
        ScanDecoders(HashSet<Guid> dxvaProfiles)
    {
        var capabilities = new List<CodecCapability>();
        var codecIds = new List<(AVCodecID, CodecCapability)>();
        var capabilityByCodecId = new Dictionary<AVCodecID, CodecCapability>();
        var capabilityIndex = new Dictionary<AVCodecID, int>();
        void* opaque = null;

        while (true)
        {
            AVCodec* codec = ffmpeg.av_codec_iterate(&opaque);
            if (codec == null)
                break;

            if (ffmpeg.av_codec_is_decoder(codec) == 0)
                continue;

            AVCodecID codecId = codec->id;
            var decoderName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";
            var hasD3D11HwConfig = HasD3D11HardwareConfig(codec);
            var hardwareDecodeAvailable = hasD3D11HwConfig && IsDxvaProfileAvailable(codecId, dxvaProfiles);
            var hardwareMethod = hardwareDecodeAvailable ? "d3d11va" :
                hasD3D11HwConfig && HasD3D12HardwareConfig(codec) ? "d3d12va" : null;

            if (capabilityByCodecId.TryGetValue(codecId, out CodecCapability? existing))
            {
                var needsUpgrade = false;
                var newSwDecoder = existing.SoftwareDecoderName;
                var newHwAvailable = existing.HardwareDecodeAvailable;
                var newHwMethod = existing.HardwareMethod;

                if (!existing.HardwareDecodeAvailable && hardwareDecodeAvailable)
                {
                    Debug.WriteLine($"[HardwareDecoderScanner] Upgrading codec {existing.CodecName}: HW available via decoder {decoderName} (hasD3D11={hasD3D11HwConfig}, dxvaProfile={hardwareDecodeAvailable})");
                    newHwAvailable = true;
                    newHwMethod = hardwareMethod;
                    needsUpgrade = true;
                }
                else if (existing.HardwareDecodeAvailable && !hardwareDecodeAvailable)
                {
                    Debug.WriteLine($"[HardwareDecoderScanner] Decoder {decoderName} for {existing.CodecName}: no HW (hasD3D11={hasD3D11HwConfig}, dxvaProfiles={dxvaProfiles.Count})");
                }

                if (string.IsNullOrWhiteSpace(existing.SoftwareDecoderName) && !hasD3D11HwConfig && decoderName != existing.CodecName)
                {
                    Debug.WriteLine($"[HardwareDecoderScanner] Filling SW decoder name for {existing.CodecName}: {decoderName}");
                    newSwDecoder = decoderName;
                    needsUpgrade = true;
                }

                if (needsUpgrade)
                {
                    CodecCapability upgraded = existing with
                    {
                        HardwareDecodeAvailable = newHwAvailable,
                        HardwareMethod = newHwMethod,
                        SoftwareDecoderName = newSwDecoder
                    };
                    capabilityByCodecId[codecId] = upgraded;
                    var idx = capabilityIndex[codecId];
                    capabilities[idx] = upgraded;
                    codecIds[idx] = (codecId, upgraded);
                    Debug.WriteLine($"[HardwareDecoderScanner]   => HW={upgraded.HardwareDecodeAvailable} SW={upgraded.SoftwareDecoderName} Method={upgraded.HardwareMethod}");
                }
                continue;
            }

            AVCodecDescriptor* codecDescr = ffmpeg.avcodec_descriptor_get(codecId);
            var canonicalName = codecDescr != null ? Marshal.PtrToStringAnsi((IntPtr)codecDescr->name) ?? decoderName : decoderName;
            var friendlyName = codecDescr != null ? Marshal.PtrToStringAnsi((IntPtr)codecDescr->long_name) ?? canonicalName : canonicalName;

            var capability = new CodecCapability(
                canonicalName,
                friendlyName,
                true,
                hasD3D11HwConfig ? null : decoderName,
                hardwareDecodeAvailable,
                hardwareMethod);

            Debug.WriteLine($"[HardwareDecoderScanner] First decoder for {canonicalName}: {decoderName} HW={hardwareDecodeAvailable} (hasD3D11={hasD3D11HwConfig}, dxvaProfiles={dxvaProfiles.Count})");

            capabilityByCodecId[codecId] = capability;
            capabilityIndex[codecId] = capabilities.Count;
            capabilities.Add(capability);
            codecIds.Add((codecId, capability));
        }

        return (capabilities, codecIds);
    }

    private static bool HasD3D11HardwareConfig(AVCodec* codec)
    {
        for (var i = 0; ; i++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                return false;

            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0)
                continue;

            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                return true;
        }
    }

    private static bool HasD3D12HardwareConfig(AVCodec* codec)
    {
        for (var i = 0; ; i++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                return false;

            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0)
                continue;

            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA)
                return true;
        }
    }

    private static bool IsDxvaProfileAvailable(AVCodecID codecId, HashSet<Guid> dxvaProfiles)
    {
        if (dxvaProfiles.Count == 0)
            return false;

        IReadOnlyList<Guid> expectedProfiles = DxvaProfileGuids.GetProfilesForCodec(codecId);
        if (expectedProfiles.Count == 0)
            return false;

        foreach (Guid profile in expectedProfiles)
        {
            if (dxvaProfiles.Contains(profile))
            {
                Debug.WriteLine($"[HardwareDecoderScanner] DXVA profile matched for {codecId}: {profile:B}");
                return true;
            }
        }

        Debug.WriteLine($"[HardwareDecoderScanner] No DXVA profile match for {codecId} (expected: {expectedProfiles.Count}, driver reports: {dxvaProfiles.Count})");
        return false;
    }

    private static Dictionary<string, ContainerCapability> ScanDemuxers(
        List<(AVCodecID codecId, CodecCapability capability)> codecIds)
    {
        var containers = new Dictionary<string, ContainerCapability>(StringComparer.OrdinalIgnoreCase);
        void* opaque = null;

        while (true)
        {
            AVInputFormat* fmt = ffmpeg.av_demuxer_iterate(&opaque);
            if (fmt == null)
                break;

            try
            {
                var formatName = Marshal.PtrToStringAnsi((IntPtr)fmt->name) ?? "unknown";
                var friendlyName = Marshal.PtrToStringAnsi((IntPtr)fmt->long_name) ?? formatName;
                var extensionsStr = Marshal.PtrToStringAnsi((IntPtr)fmt->extensions);

                if (string.IsNullOrWhiteSpace(extensionsStr))
                    continue;

                var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (extensions.Length == 0)
                    continue;

                AVCodecTag** codecTags = fmt->codec_tag;
                var possibleCodecs = new List<CodecCapability>();
                var seenCodecNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (codecTags != null)
                {
                    foreach ((AVCodecID cid, CodecCapability? cap) in codecIds)
                    {
                        uint tag = 0;
                        var found = ffmpeg.av_codec_get_tag2(codecTags, cid, &tag) != 0;
                        if (found && seenCodecNames.Add(cap.CodecName))
                            possibleCodecs.Add(cap);
                    }
                }

                foreach (var ext in extensions)
                {
                    var normalized = NormalizeExtension(ext);
                    if (string.IsNullOrWhiteSpace(normalized) || normalized == "*")
                        continue;

                    var anyHw = possibleCodecs.Count > 0 && possibleCodecs.Any(c => c.HardwareDecodeAvailable);

                    if (!containers.TryGetValue(normalized, out ContainerCapability? existing))
                        containers[normalized] = new ContainerCapability(normalized, formatName, friendlyName, possibleCodecs, anyHw);
                    else
                    {
                        var mergedCodecs = existing.PossibleCodecs
                            .Union(possibleCodecs)
                            .DistinctBy(c => c.CodecName, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var mergedAnyHw = existing.AnyHardwareDecodeAvailable || anyHw;

                        containers[normalized] = new ContainerCapability(
                            existing.Extension,
                            existing.FormatName + ", " + formatName,
                            existing.FriendlyName + ", " + friendlyName,
                            mergedCodecs,
                            mergedAnyHw);
                    }
                }
            }
            catch { }
        }

        return containers;
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}

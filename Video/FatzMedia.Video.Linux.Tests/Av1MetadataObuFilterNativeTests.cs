using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Linux.Runtime;
using FFmpeg.AutoGen;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check (set <c>FATZBOMB_LINUX_FFMPEG_DIR</c>, as for
/// <see cref="LinuxNativeSmokeTests"/>): AV1 metadata OBUs are removed before the native av1
/// decoder sees them, even when malformed (ENG-011, <c>Scream 7.mkv</c>: "trailing_zero_bit out
/// of range" in an OBU of type 5).
/// </summary>
public sealed unsafe class Av1MetadataObuFilterNativeTests
{
    // OBU headers: type << 3 | has_size_field (0x02). 0x12 = temporal delimiter, 0x2A = metadata,
    // 0x32 = frame. The metadata payload is junk with bad trailing bits; the frame is opaque.
    static readonly byte[] TemporalDelimiter = [0x12, 0x00];
    static readonly byte[] MalformedMetadata = [0x2A, 0x03, 0x04, 0xFF, 0x7F];
    static readonly byte[] Frame = [0x32, 0x02, 0xAB, 0xCD];

    [Fact]
    public void MetadataObus_AreRemovedAndOtherObusKeptInOrder()
    {
        if (!TryActivateFfmpeg())
            return;

        using var filter = CreateFilter();
        var packet = CreatePacket([.. TemporalDelimiter, .. MalformedMetadata, .. Frame, .. MalformedMetadata]);
        try
        {
            Assert.True(filter.Filter(packet));
            Assert.Equal([.. TemporalDelimiter, .. Frame], new ReadOnlySpan<byte>(packet->data, packet->size).ToArray());
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    [Fact]
    public void PacketWithOnlyMetadata_LeavesNothingToDecode()
    {
        if (!TryActivateFfmpeg())
            return;

        using var filter = CreateFilter();
        var packet = CreatePacket(MalformedMetadata);
        try
        {
            Assert.False(filter.Filter(packet));
            Assert.Equal(0, packet->size);

            // The filter keeps working after a dropped packet.
            var next = CreatePacket([.. TemporalDelimiter, .. Frame]);
            Assert.True(filter.Filter(next));
            Assert.Equal(TemporalDelimiter.Length + Frame.Length, next->size);
            ffmpeg.av_packet_free(&next);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }

    [Fact]
    public void OtherCodecs_GetNoFilter()
    {
        if (!TryActivateFfmpeg())
            return;

        var parameters = ffmpeg.avcodec_parameters_alloc();
        try
        {
            parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            parameters->codec_id = AVCodecID.AV_CODEC_ID_H264;
            Assert.Null(Av1MetadataObuFilter.TryCreate(parameters, new AVRational { num = 1, den = 1000 }));
        }
        finally
        {
            ffmpeg.avcodec_parameters_free(&parameters);
        }
    }

    static Av1MetadataObuFilter CreateFilter()
    {
        var parameters = ffmpeg.avcodec_parameters_alloc();
        try
        {
            parameters->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            parameters->codec_id = AVCodecID.AV_CODEC_ID_AV1;
            var filter = Av1MetadataObuFilter.TryCreate(parameters, new AVRational { num = 1, den = 1000 });
            Assert.NotNull(filter);
            return filter;
        }
        finally
        {
            ffmpeg.avcodec_parameters_free(&parameters);
        }
    }

    static AVPacket* CreatePacket(byte[] data)
    {
        var packet = ffmpeg.av_packet_alloc();
        Assert.True(ffmpeg.av_new_packet(packet, data.Length) >= 0);
        data.CopyTo(new Span<byte>(packet->data, data.Length));
        packet->pts = 1;
        packet->dts = 1;
        return packet;
    }

    static bool TryActivateFfmpeg()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return false;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _), $"dlopen failed for {path}");
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
        return true;
    }
}

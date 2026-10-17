using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// Removes AV1 metadata OBUs (type 5) from packets before they reach FFmpeg's native
/// <c>av1</c> decoder, which drives hardware decoding. That decoder parses every metadata OBU
/// strictly and drops the whole packet when an encoder wrote one with bad trailing bits;
/// <c>libdav1d</c> ignores them. Metadata OBUs carry HDR10+, ITU-T T.35, and timecode extras,
/// none of which the picture needs.
/// </summary>
/// <remarks>
/// Uses FFmpeg's <c>filter_units</c> bitstream filter, which splits a packet into OBUs by
/// their size fields without parsing their contents, so the malformed bytes are never read.
/// </remarks>
internal sealed unsafe class Av1MetadataObuFilter : IDisposable
{
    private const string RemovedObuTypes = "5";
    private AVBSFContext* _context;

    private Av1MetadataObuFilter(AVBSFContext* context) => _context = context;

    /// <summary>
    /// Creates the filter for an AV1 stream, or returns null for other codecs or when this
    /// FFmpeg build lacks <c>filter_units</c>.
    /// </summary>
    public static Av1MetadataObuFilter? TryCreate(AVCodecParameters* parameters, AVRational timeBase)
    {
        if (parameters == null || parameters->codec_id != AVCodecID.AV_CODEC_ID_AV1)
            return null;

        AVBitStreamFilter* filter = ffmpeg.av_bsf_get_by_name("filter_units");
        if (filter == null)
            return null;

        AVBSFContext* context = null;
        if (ffmpeg.av_bsf_alloc(filter, &context) < 0 || context == null)
            return null;

        if (ffmpeg.avcodec_parameters_copy(context->par_in, parameters) < 0
            || ffmpeg.av_opt_set(context, "remove_types", RemovedObuTypes, ffmpeg.AV_OPT_SEARCH_CHILDREN) < 0)
        {
            ffmpeg.av_bsf_free(&context);
            return null;
        }

        context->time_base_in = timeBase;
        if (ffmpeg.av_bsf_init(context) < 0)
        {
            ffmpeg.av_bsf_free(&context);
            return null;
        }

        return new Av1MetadataObuFilter(context);
    }

    /// <summary>
    /// Filters <paramref name="packet"/> in place. Returns false when nothing is left to decode
    /// (the packet held only metadata) or the filter failed; the packet is then empty.
    /// </summary>
    public bool Filter(AVPacket* packet)
    {
        if (_context == null)
            return true;

        if (ffmpeg.av_bsf_send_packet(_context, packet) < 0)
        {
            ffmpeg.av_packet_unref(packet);
            return false;
        }

        // filter_units returns at most one packet per input.
        return ffmpeg.av_bsf_receive_packet(_context, packet) >= 0;
    }

    public void Flush()
    {
        if (_context != null)
            ffmpeg.av_bsf_flush(_context);
    }

    public void Dispose()
    {
        if (_context == null)
            return;

        fixed (AVBSFContext** context = &_context)
            ffmpeg.av_bsf_free(context);
    }
}

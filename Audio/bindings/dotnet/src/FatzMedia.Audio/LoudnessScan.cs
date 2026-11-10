using System.Runtime.InteropServices;
using FatzMedia.Audio.Native;

namespace FatzMedia.Audio;

/// <summary>
/// Measures the loudness of audio your app feeds, for songs FatzMedia.Audio cannot open itself (a track your demuxer reads
/// out of a video file, a codec you decode yourself). The result is what <see cref="AudioEngine.ScanLoudness"/>
/// gives for a file, stored in the same cache under the song's <see cref="AudioFingerprint"/>. Playing streams of
/// the same song (<see cref="StreamOptions.SourceIdentity"/>) pick the result up from the cache, or from the scan while
/// it runs, so the start wait (<see cref="AudioEngine.SetNormalizationStartWait"/>) and the provisional value work as
/// they do for a file.
/// </summary>
/// <remarks>
/// <para>
/// The scan runs on the thread that feeds it: each push decodes and measures at once (far faster than real time:
/// 180 s of PCM measure in 0.15 s on one core, plus the codec's decode time), and FatzMedia.Audio starts no thread for it. Feed the track from its start, in order, once. Calls on
/// one scan must not overlap; different scans may run on different threads. Start the scan before playback (when the
/// song is added to the library, or for the next item in a queue): a song that starts playing at once only gains from the
/// start wait if its scan is already well on its way.
/// </para>
/// <para>
/// Open one with <see cref="AudioEngine.OpenLoudnessScan(CodecParameters, AudioFingerprint, LoudnessScanOptions?)"/>
/// (compressed packets) or <see cref="AudioEngine.OpenLoudnessScan(AudioFormat, AudioFingerprint, LoudnessScanOptions?)"/>
/// (decoded frames).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var id = AudioFingerprint.Compute(path);                       // of the original file
/// using var scan = engine.OpenLoudnessScan(parameters, id, new LoudnessScanOptions { Duration = duration });
/// if (scan.WantsMore)                                            // false: cached, or a Dolby dialogue level
///     while (ffmpeg.av_read_frame(fmt, pkt) >= 0 &amp;&amp; scan.WantsMore)
///     {
///         if (pkt->stream_index == audioIndex) scan.PushPacket((nint)pkt->data, pkt->size);
///         ffmpeg.av_packet_unref(pkt);
///     }
/// Loudness result = scan.Finish();
/// </code>
/// </example>
public sealed unsafe class LoudnessScan : IDisposable
{
    readonly ScanHandle _handle;
    readonly bool _packets;

    internal LoudnessScan(ScanHandle handle, bool packets)
    {
        _handle = handle;
        _packets = packets;
    }

    /// <summary>
    /// False when feeding more is pointless because the result is known already: the song is in the cache, or
    /// (<see cref="AudioEngine.SetDialnorm"/> on) a Dolby stream's first unit gave its dialogue level. Check it after
    /// opening and now and then while feeding, and skip the rest of the file. <see cref="Finish"/> still gives the result.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The scan was disposed.</exception>
    public bool WantsMore => NativeMethods.fa_scan_wants_more(_handle) != 0;

    /// <summary>Feeds one compressed packet, in order. Packet scans only.</summary>
    /// <param name="data">The packet's bytes (<c>AVPacket.data</c>); copied if needed, free to reuse after the call.</param>
    /// <returns>True when the packet was taken (or ignored because <see cref="WantsMore"/> is false); false when it could not be
    /// decoded and was skipped, which is not fatal: keep feeding.</returns>
    /// <exception cref="InvalidOperationException">This is a frame scan.</exception>
    /// <exception cref="AudioException">The scan was finished (<see cref="ErrorCode.InvalidArgument"/>), the codec's output changed
    /// rate or channels (<see cref="ErrorCode.UnsupportedFormat"/>), or memory ran out.</exception>
    public bool PushPacket(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) return PushPacketCore(p, data.Length);
    }

    /// <summary>Feeds one compressed packet from native memory (<c>AVPacket.data</c>), in order. Packet scans only.</summary>
    /// <param name="data">Pointer to the packet's bytes.</param>
    /// <param name="size">Size in bytes (<c>AVPacket.size</c>).</param>
    /// <returns>As <see cref="PushPacket(ReadOnlySpan{byte})"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="size"/> is negative, or <paramref name="data"/> is zero with a non-zero size.</exception>
    /// <exception cref="InvalidOperationException">This is a frame scan.</exception>
    /// <exception cref="AudioException">As <see cref="PushPacket(ReadOnlySpan{byte})"/>.</exception>
    public bool PushPacket(nint data, int size)
    {
        if (size < 0 || (data == 0 && size != 0)) throw new ArgumentException("Invalid packet buffer.", nameof(size));
        return PushPacketCore((void*)data, size);
    }

    bool PushPacketCore(void* data, int size)
    {
        if (!_packets) throw new InvalidOperationException("This scan takes decoded frames: use PushFrame.");
        var p = new FaPacket { Data = data, Size = (nuint)size, Pts = long.MinValue, Duration = 0 };
        var rc = NativeMethods.fa_scan_push_packet(_handle, &p);
        if (rc == 0) return true;
        if (rc is (int)ErrorCode.InvalidArgument or (int)ErrorCode.UnsupportedFormat or (int)ErrorCode.OutOfMemory)
            throw new AudioException((ErrorCode)rc);
        return false;
    }

    /// <summary>Feeds one decoded frame as it is (any sample format, planar or interleaved), in order. Frame scans only.</summary>
    /// <param name="frameFormat">The frame's format (<see cref="FFmpeg.ToSampleFormat"/> maps <c>AVFrame.format</c>). Its rate and channel
    /// count must be those the scan was opened with.</param>
    /// <param name="data">Pointer to the array of plane pointers: <c>AVFrame.extended_data</c> (one per channel when planar, one for interleaved data).</param>
    /// <param name="frames">Samples per channel (<c>AVFrame.nb_samples</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="frameFormat"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> is zero or <paramref name="frames"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">This is a packet scan.</exception>
    /// <exception cref="AudioException">The scan was finished or the format is invalid (<see cref="ErrorCode.InvalidArgument"/>), or the rate or channel count differs (<see cref="ErrorCode.UnsupportedFormat"/>).</exception>
    public void PushFrame(AudioFormat frameFormat, nint data, int frames)
    {
        ArgumentNullException.ThrowIfNull(frameFormat);
        if (_packets) throw new InvalidOperationException("This scan takes packets: use PushPacket.");
        if (data == 0) throw new ArgumentException("The plane pointer array is null.", nameof(data));
        if (frames < 0) throw new ArgumentException("Frame count must not be negative.", nameof(frames));
        var f = new FaAudioFrame
        {
            Format = frameFormat.ToNative(),
            Data = (void**)data,
            Frames = frames,
            Pts = long.MinValue,
        };
        AudioException.ThrowIfFailed(NativeMethods.fa_scan_push_frame(_handle, &f));
    }

    /// <summary>
    /// Ends the feed: gives back what the decoder still held, computes the result, stores it in the cache
    /// (<see cref="AudioEngineOptions.CacheDirectory"/>) and hands it to the playing streams of the song, which glide to it.
    /// Calling it again returns the same result.
    /// </summary>
    /// <returns>The loudness (<see cref="LoudnessSource.Scan"/>, <see cref="LoudnessSource.Cache"/> or <see cref="LoudnessSource.Dialnorm"/>).</returns>
    /// <exception cref="AudioException">Nothing was fed (<see cref="ErrorCode.EndOfStream"/>).</exception>
    public Loudness Finish()
    {
        var l = NativeSized.New<FaLoudness>();
        AudioException.ThrowIfFailed(NativeMethods.fa_scan_finish(_handle, ref l));
        return Loudness.From(l);
    }

    /// <summary>Releases the scan. Without <see cref="Finish"/> nothing is stored, and streams waiting for it stop waiting.</summary>
    public void Dispose() => _handle.Dispose();
}

/// <summary>Options for <see cref="AudioEngine.OpenLoudnessScan(CodecParameters, AudioFingerprint, LoudnessScanOptions?)"/>.</summary>
public sealed record LoudnessScanOptions
{
    /// <summary>The track in the original file, -1 (the default) for its default track. Use -1 when FatzMedia.Audio could open the same
    /// file itself, so a scan by path and a fed scan share one cache entry; it must match <see cref="StreamOptions.AudioTrack"/>
    /// of the streams that play the song.</summary>
    public int AudioTrack { get; init; } = -1;
    /// <summary>The track's length, if known. With it the scan reports its progress, which the start wait uses to decide whether to wait
    /// longer for the final value, and a value so far is offered after a quarter of the song.</summary>
    public TimeSpan? Duration { get; init; }
}

public sealed partial class AudioEngine
{
    /// <summary>Starts a loudness scan of compressed packets that you feed (see <see cref="LoudnessScan"/>); FatzMedia.Audio decodes them
    /// with its own decoders, the ones <see cref="OpenPackets"/> has.</summary>
    /// <param name="parameters">The track: codec name, rate, channels, codec configuration.</param>
    /// <param name="identity">Which song this is: <see cref="AudioFingerprint.Compute(string)"/> of the original file.</param>
    /// <param name="options">Track and duration; null uses the defaults.</param>
    /// <returns>The scan; dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is null.</exception>
    /// <exception cref="AudioException">FatzMedia.Audio does not decode the codec (<see cref="ErrorCode.UnsupportedFormat"/>; decode it
    /// yourself and use the frame overload), or a parameter is invalid.</exception>
    public unsafe LoudnessScan OpenLoudnessScan(CodecParameters parameters, AudioFingerprint identity, LoudnessScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Codec);
        var h = Handle;
        var codec = (byte*)Marshal.StringToCoTaskMemUTF8(parameters.Codec);
        try
        {
            fixed (byte* extra = parameters.ExtraData)
            {
                FaCodecParams p;
                NativeMethods.fa_codec_params_init(&p);
                p.Codec = codec;
                p.SampleRate = (uint)Math.Max(0, parameters.SampleRate);
                p.Channels = (uint)Math.Max(0, parameters.Channels);
                p.Extradata = extra;
                p.ExtradataSize = (nuint)(parameters.ExtraData?.Length ?? 0);
                p.BitRate = parameters.BitRate;
                p.BlockAlign = (uint)Math.Max(0, parameters.BlockAlign);
                p.BitsPerCodedSample = (uint)Math.Max(0, parameters.BitsPerCodedSample);
                p.TimeBase = parameters.TimeBase.ToNative();
                return OpenScan(h, true, &p, null, identity, options);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)codec);
        }
    }

    /// <summary>Starts a loudness scan of decoded frames that you feed (see <see cref="LoudnessScan"/>).</summary>
    /// <param name="format">The rate and channel count of every frame to come (the sample format is not used).</param>
    /// <param name="identity">Which song this is: <see cref="AudioFingerprint.Compute(string)"/> of the original file.</param>
    /// <param name="options">Track and duration; null uses the defaults.</param>
    /// <returns>The scan; dispose it when done.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="format"/> is null.</exception>
    /// <exception cref="AudioException">The rate or channel count is invalid (<see cref="ErrorCode.InvalidArgument"/>).</exception>
    public unsafe LoudnessScan OpenLoudnessScan(AudioFormat format, AudioFingerprint identity, LoudnessScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        var h = Handle;
        var nf = format.ToNative();
        return OpenScan(h, false, null, &nf, identity, options);
    }

    static unsafe LoudnessScan OpenScan(EngineHandle h, bool packets, FaCodecParams* cp, FaAudioFormat* af,
        AudioFingerprint identity, LoudnessScanOptions? options)
    {
        var id = NativeSized.New<FaFingerprint>();
        id.Key = identity.Key;
        id.AudioBytes = identity.AudioBytes;
        FaScanOptions so;
        NativeMethods.fa_scan_options_init(&so);
        so.Identity = &id;
        so.AudioTrack = options?.AudioTrack ?? -1;
        so.DurationSeconds = options?.Duration is { } d ? Math.Max(0, d.TotalSeconds) : 0;
        var rc = packets ? NativeMethods.fa_scan_open_packets(h, cp, &so, out var feed)
                         : NativeMethods.fa_scan_open_push(h, af, &so, out feed);
        AudioException.ThrowIfFailed(rc);
        var sh = new ScanHandle();
        sh.Initialize(feed, h);
        return new LoudnessScan(sh, packets);
    }
}

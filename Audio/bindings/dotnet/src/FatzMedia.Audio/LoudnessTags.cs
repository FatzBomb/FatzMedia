using FatzMedia.Audio.Native;

namespace FatzMedia.Audio;

/// <summary>What <see cref="AudioEngine.WriteLoudnessTags(IReadOnlyList{string}, bool)"/> measured and wrote for one file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Error"><see cref="ErrorCode.Ok"/> when the tags were written; otherwise why not (the file is unchanged).</param>
/// <param name="IntegratedLufs">The measured loudness (NaN when it was not measured).</param>
/// <param name="TruePeakDbtp">The measured true peak.</param>
/// <param name="TrackGainDb">The gain written: to -18 LUFS (ReplayGain 2), for Opus to -23 LUFS (R128). NaN when not written.</param>
/// <param name="AlbumLufs">The album's loudness over all its files; NaN without <c>album</c>.</param>
/// <param name="AlbumPeakDbtp">The album's true peak; NaN without <c>album</c>.</param>
/// <param name="AlbumGainDb">The album gain written; NaN without <c>album</c> or when not written.</param>
/// <param name="TagFormat">The tag written (ID3v2, Vorbis comments, MP4 or APEv2); null when nothing was written.</param>
public sealed record LoudnessTagResult(string Path, ErrorCode Error, double IntegratedLufs, double TruePeakDbtp,
    double TrackGainDb, double AlbumLufs, double AlbumPeakDbtp, double AlbumGainDb, TagFormat? TagFormat)
{
    /// <summary>The tags were written.</summary>
    public bool Written => Error == ErrorCode.Ok;

    internal static LoudnessTagResult From(string path, in FaLoudnessTagResult r) =>
        new(path, (ErrorCode)r.Result, r.IntegratedLufs, r.TruePeakDbtp, r.TrackGainDb, r.AlbumLufs, r.AlbumPeakDbtp,
            r.AlbumGainDb, r.TagFormat == 0 ? null : (TagFormat)r.TagFormat);
}

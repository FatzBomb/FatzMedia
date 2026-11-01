using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Engine-neutral level meter over a decoded audio stream (037A slice 1): what normalization's
/// peak analysis (<see cref="AudioNormalizer"/>) and silence analysis
/// (<see cref="AudioSilenceAnalyzer"/>) read, so neither needs an audio engine handle. Reading a
/// level advances the stream. <see cref="BassChannelLevelSource"/> is the BASS implementation.
/// </summary>
internal interface IAudioLevelSource
{
    /// <summary>
    /// Reads the next <paramref name="seconds"/> of audio and returns the larger of the left and
    /// right peak levels (0..1, unclamped); <see langword="false"/> at the end or on failure.
    /// </summary>
    bool TryReadPeak(float seconds, out double peak);

    /// <summary>Gets the stream length in seconds; <see langword="false"/> when the length is unknown or zero.</summary>
    bool TryGetDuration(out double seconds);

    /// <summary>Gets the stream position in seconds; <see langword="false"/> when it cannot be read.</summary>
    bool TryGetPosition(out double seconds);

    /// <summary>Moves to <paramref name="seconds"/>.</summary>
    bool TrySeek(double seconds);

    /// <summary>Moves to the start.</summary>
    bool TrySeekToStart();

    /// <summary>Describes the last failure for diagnostics (BASS: its error code).</summary>
    string LastError { get; }
}

/// <summary>
/// <see cref="IAudioLevelSource"/> on a BASS decode (or play) channel. Each member makes the same
/// BASS calls, with the same arguments, that the analyzers made on the handle before 037A slice 1.
/// </summary>
internal sealed class BassChannelLevelSource : IAudioLevelSource
{
    private const LevelRetrievalFlags Flags = LevelRetrievalFlags.Stereo;
    private static readonly ThreadLocal<float[]> LevelBuffer = new(() => new float[2]);
    private readonly int _handle;

    private BassChannelLevelSource(int handle) => _handle = handle;

    /// <summary>Returns a source on <paramref name="handle"/>, or <see langword="null"/> for 0 (no channel).</summary>
    public static BassChannelLevelSource? ForHandle(int handle) => handle == 0 ? null : new BassChannelLevelSource(handle);

    public string LastError => MBass.LastError.ToString();

    public bool TryReadPeak(float seconds, out double peak)
    {
        var levelBuffer = LevelBuffer.Value!;
        if (!MBass.ChannelGetLevel(_handle, levelBuffer, seconds, Flags))
        {
            peak = 0;
            return false;
        }

        peak = Math.Max(levelBuffer[0], levelBuffer[1]);
        return true;
    }

    public bool TryGetDuration(out double seconds)
    {
        var lengthBytes = MBass.ChannelGetLength(_handle);
        seconds = lengthBytes > 0 ? MBass.ChannelBytes2Seconds(_handle, lengthBytes) : 0;
        return lengthBytes > 0;
    }

    public bool TryGetPosition(out double seconds)
    {
        var bytes = MBass.ChannelGetPosition(_handle);
        if (bytes < 0)
        {
            seconds = double.NaN;
            return false;
        }

        seconds = MBass.ChannelBytes2Seconds(_handle, bytes);
        return true;
    }

    public bool TrySeek(double seconds) =>
        MBass.ChannelSetPosition(_handle, MBass.ChannelSeconds2Bytes(_handle, seconds));

    public bool TrySeekToStart() => MBass.ChannelSetPosition(_handle, 0);
}

using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using Fa = global::FatzAudio;

namespace FatzBomb.MediaControl.Audio.FatzAudio;

/// <summary>How MediaControl's audio settings map onto FatzAudio's (037A slice 2). Pure functions.</summary>
internal static class FatzAudioMappings
{
    /// <summary>
    /// The shortest audible run that counts as sound, not silence (FatzAudio's default, agreed in
    /// our rc.1 reply): clicks and count-in ticks at the edges are trimmed, a soft fade-in is not.
    /// </summary>
    public static readonly TimeSpan SilenceMinSound = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Maps the equalizer to FatzAudio's built-in bands: one peaking band per band at its centre
    /// and gain, with the bandwidth in octaves turned into Q (one octave is Q 1.414, as BASS_FX's
    /// PEAKEQ). A disabled or all-flat equalizer is an empty list, which turns FatzAudio's off.
    /// </summary>
    public static Fa.EqualizerBand[] ToEqualizerBands(AudioEqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.IsEffective
            ? [.. settings.Bands.Select(static band => Fa.EqualizerBand.Peak((float)band.CenterHz, (float)band.GainDb, (float)BandwidthToQ(band.BandwidthOctaves)))]
            : [];
    }

    /// <summary>Q of a peaking band <paramref name="octaves"/> wide (RBJ cookbook): sqrt(2^N) / (2^N - 1).</summary>
    public static double BandwidthToQ(double octaves)
    {
        var ratio = Math.Pow(2, octaves);
        return Math.Sqrt(ratio) / (ratio - 1);
    }

    /// <summary>Our tempo percentage (0 = normal, -95..5000) as FatzAudio's multiplier (1 = normal; it clamps to 0.1..10).</summary>
    public static float ToTempo(int percent) => (float)(1 + (Math.Clamp(percent, -95, 5000) / 100.0));

    /// <summary>
    /// Our trim settings as FatzAudio silence detection: RMS over 50 ms windows (its default, as we
    /// asked in request 9), our threshold and minimum silence, and sounds from 200 ms
    /// (<see cref="SilenceMinSound"/>). Padding and the leading/trailing switches stay ours
    /// (<see cref="SilenceTrimCalculator"/>).
    /// </summary>
    public static Fa.SilenceOptions ToSilenceOptions(SilenceTrimOptions options) => new()
    {
        Mode = Fa.SilenceMode.Rms,
        ThresholdDb = (float)options.ThresholdDecibels,
        MinLength = options.MinimumSilenceDuration,
        MinSound = SilenceMinSound,
    };

    /// <summary>
    /// The edge silences of a detection as source-time regions: from the start to the first audible
    /// frame, and from the frame after the last audible one to the end; the whole source when all
    /// of it is silent.
    /// </summary>
    public static IReadOnlyList<SilenceRegion> ToEdgeRegions(long? firstAudible, long? lastAudible, long lengthFrames, int sampleRate, double durationSeconds)
    {
        if (sampleRate <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            return [];

        if (firstAudible is null || lastAudible is null)
            return [new SilenceRegion(0, durationSeconds)];

        List<SilenceRegion> regions = [];
        if (firstAudible.Value > 0)
            regions.Add(new SilenceRegion(0, Math.Min(durationSeconds, firstAudible.Value / (double)sampleRate)));
        if (lengthFrames > 0 && lastAudible.Value < lengthFrames)
            regions.Add(new SilenceRegion(Math.Min(durationSeconds, lastAudible.Value / (double)sampleRate), durationSeconds));
        return regions;
    }

    /// <summary>FatzAudio's <c>DeviceChanged</c> value as our device-change kind: 0 the default moved, 1 the device went away.</summary>
    public static AudioDeviceChangeKind ToDeviceChangeKind(long value) =>
        value == 0 ? AudioDeviceChangeKind.DefaultDeviceChanged : AudioDeviceChangeKind.DeviceRemoved;
}

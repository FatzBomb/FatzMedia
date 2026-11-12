using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Configuration;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>How MediaControl's audio settings map onto FatzAudio's (037A slice 2). Pure functions.</summary>
static class FatzAudioMappings
{
    /// <summary>
    /// The shortest audible run that counts as sound, not silence (FatzAudio's default, agreed in
    /// our rc.1 reply): clicks and count-in ticks at the edges are trimmed, a soft fade-in is not.
    /// </summary>
    public static readonly TimeSpan SilenceMinSound = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Maps the equalizer to FatzAudio's built-in bands: one peaking band per band at its centre
    /// and gain, with the bandwidth in octaves turned into Q (one octave is Q 1.414, the usual
    /// peaking-filter rule). A disabled or all-flat equalizer is an empty list, which turns FatzAudio's off.
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

    /// <summary>
    /// The folder FatzAudio keeps measured loudness (and its other analysis) in:
    /// <see langword="null"/> uses the per-user default, an empty or blank string turns the cache off
    /// (returns <see langword="null"/>), anything else is used, made absolute against the current folder.
    /// </summary>
    public static string? ResolveCacheDirectory(string? configured, bool isWindows, Func<string, string?> environment, string localApplicationData, string userProfile)
    {
        if (configured is not null)
            return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);

        if (isWindows)
            return string.IsNullOrEmpty(localApplicationData) ? null : Path.Combine(localApplicationData, "FatzMedia.Video", "AudioCache");

        // XDG: only an absolute XDG_CACHE_HOME counts; otherwise ~/.cache.
        var xdg = environment("XDG_CACHE_HOME");
        var root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : string.IsNullOrEmpty(userProfile) ? null : Path.Combine(userProfile, ".cache");
        return root is null ? null : Path.Combine(root, "fatzmedia-video", "audio");
    }

    /// <summary><see cref="ResolveCacheDirectory(string?, bool, Func{string, string?}, string, string)"/> for this machine and user.</summary>
    public static string? ResolveCacheDirectory(string? configured) =>
        ResolveCacheDirectory(
            configured,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The track gain FatzAudio's normalization gives a song of <paramref name="integratedLufs"/>:
    /// target minus loudness, limited to -30..+12 dB; 0 until the loudness is final or for silence.
    /// </summary>
    public static double TrackGainDecibels(double integratedLufs, bool isFinal, double targetLufs) =>
        isFinal && double.IsFinite(integratedLufs) ? Math.Clamp(targetLufs - integratedLufs, -30, 12) : 0;

    /// <summary>FatzAudio's normalization gain in dB as the linear multiplier <c>NormalizationGain</c> reports (1 when unknown).</summary>
    public static double GainFromDecibels(double gainDb) => double.IsFinite(gainDb) ? Math.Pow(10, gainDb / 20) : 1;

    /// <summary>Our CD+G background opacity (0..100 %) as alpha 0..255, as <c>CdgColorManager</c> maps it (100 % is exactly 255).</summary>
    public static byte ToCdgOpacityAlpha(byte percent) =>
        percent >= 100 ? (byte)255 : (byte)Math.Round(percent * 255.0 / 100.0);

    /// <summary>
    /// A FatzAudio CD+G palette as the ARGB colours our renderers look indices up in, with the
    /// presentation settings applied as our <c>CdgColorManager</c> applies them: colour 0,
    /// the border colour and the background colour take <paramref name="backgroundOverride"/>'s colour
    /// when set, and their alpha is the override's alpha (else 255) times <paramref name="opacityAlpha"/>;
    /// every other colour is opaque.
    /// </summary>
    public static void ToCdgPaletteArgb(ReadOnlySpan<Fa.CdgColor> colors, int backgroundIndex, int borderIndex, byte opacityAlpha, uint? backgroundOverride, Span<int> destination)
    {
        var count = Math.Min(colors.Length, destination.Length);
        for (var i = 0; i < count; i++)
        {
            var color = colors[i];
            var rgb = (uint)((color.R << 16) | (color.G << 8) | color.B);
            var alpha = 255u;
            if (i == 0 || i == borderIndex || i == backgroundIndex)
            {
                if (backgroundOverride is { } overrideArgb)
                    rgb = overrideArgb & 0xFFFFFF;
                alpha = (backgroundOverride is { } o ? o >> 24 : 255u) * opacityAlpha / 255;
            }

            destination[i] = (int)((alpha << 24) | rgb);
        }
    }

    /// <summary>
    /// The full-viewport background wash for a FatzAudio CD+G frame, as our
    /// <c>CdgColorManager</c> works it out: transparent until the first screen clear unless an
    /// override is set, then the background colour (or the override's) at the override's alpha (else
    /// 255) times <paramref name="opacityAlpha"/>.
    /// </summary>
    public static uint ToCdgBackgroundWashArgb(ReadOnlySpan<Fa.CdgColor> colors, int backgroundIndex, byte opacityAlpha, uint? backgroundOverride)
    {
        if (backgroundIndex < 0 && backgroundOverride is null)
            return 0;

        uint rgb;
        if (backgroundOverride is { } overrideArgb)
        {
            rgb = overrideArgb & 0xFFFFFF;
        }
        else
        {
            var color = colors[backgroundIndex];
            rgb = (uint)((color.R << 16) | (color.G << 8) | color.B);
        }

        var alpha = (backgroundOverride is { } o ? o >> 24 : 255u) * opacityAlpha / 255;
        return (alpha << 24) | rgb;
    }

    /// <summary>
    /// Our karaoke lyric look as a FatzAudio lyric style: FatzAudio's Classic preset (white, sung yellow, a black
    /// outline and shadow, a cross-fading wipe: the look our own renderer draws) with our font, colours, outline,
    /// shadow and the opacity of the next line. Our pixel sizes are for <see cref="KaraokeTextStyle.FontSize"/> on a
    /// 1080-pixel-high canvas; FatzAudio's effect sizes are fractions of the em (an outline of 1 = 0.12 em, a shadow
    /// distance of 1 = 0.15 em), so they convert through the font size. A width or colour of 0 turns that effect off.
    /// </summary>
    public static Fa.LyricStyle ToLyricStyle(KaraokeTextStyle style)
    {
        var preset = Fa.LyricStyle.FromPreset(Fa.LyricPreset.Classic);
        var em = style.FontSize > 0 ? style.FontSize : 72;
        var outline = style.OutlineWidth > 0 && (style.OutlineArgb >> 24) > 0
            ? new Fa.OutlineEffect
            {
                Color = ToColor(style.OutlineArgb),
                SungColor = ToColor(style.WipeOutlineArgb),
                Size = Math.Clamp(style.OutlineWidth / em / 0.12f, 0, 1),
            }
            : null;
        var distance = MathF.Sqrt((style.ShadowOffsetX * style.ShadowOffsetX) + (style.ShadowOffsetY * style.ShadowOffsetY));
        var shadow = (style.ShadowArgb >> 24) > 0 && distance > 0
            ? new Fa.ShadowEffect
            {
                Color = ToColor(style.ShadowArgb),
                SungColor = ToColor(style.WipeShadowArgb),
                Opacity = 1,
                Distance = Math.Clamp(distance / em / 0.15f, 0, 1),
                // Ours is x right, y down; FatzAudio's angle is counter-clockwise from the right.
                AngleDegrees = MathF.Atan2(-style.ShadowOffsetY, style.ShadowOffsetX) * 180 / MathF.PI,
            }
            : null;
        return preset with
        {
            FontFamily = string.IsNullOrWhiteSpace(style.FontFamily) ? null : style.FontFamily,
            TextColor = ToColor(style.TextArgb),
            SungColor = ToColor(style.WipeTextArgb),
            NextLineOpacity = Math.Clamp(style.NextLineOpacity, 0, 1),
            Effects = (preset.Effects ?? new Fa.SurfaceEffects()) with { Outline = outline, Shadow = shadow },
        };
    }

    /// <summary>An ARGB colour as FatzAudio's straight-alpha colour.</summary>
    public static Fa.CdgColor ToColor(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    /// <summary>Our scaled CD+G options as FatzAudio's presentation: scaled, with the zone and the style.</summary>
    public static Fa.CdgPresentation ToCdgPresentation(CdgScalingOptions options) => new()
    {
        Scale = true,
        SafetyZone = options.SafetyZone switch
        {
            CdgSafetyZone.Remove => Fa.SafetyZone.Remove,
            CdgSafetyZone.Auto => Fa.SafetyZone.Auto,
            CdgSafetyZone.Super => Fa.SafetyZone.Super,
            _ => Fa.SafetyZone.Show,
        },
        // The traced levels look alike from 6 up (owner, ENG-064): 6 for straight slopes, 10 for curves; 1-4 blur.
        Smoothness = options.Style switch
        {
            CdgScaleStyle.Traced => 6,
            CdgScaleStyle.Curved => 10,
            _ => 0,
        },
        EdgeBlend = options.Style == CdgScaleStyle.AntiAliased,
        PixelArt = options.Style == CdgScaleStyle.PixelArt,
    };

    /// <summary>
    /// The scaled CD+G effects of <paramref name="options"/>, or null for none: a black outline, a soft black shadow down
    /// right and a bevel lit from the top left, each off at 0. Our sizes count half CD+G pixels; FatzAudio's units are 1.5
    /// CD+G pixels for the outline, 2 for the shadow and 1 for the bevel.
    /// </summary>
    public static Fa.SurfaceEffects? ToCdgEffects(CdgScalingOptions options) => options.HasEffects
        ? new Fa.SurfaceEffects
        {
            Outline = options.OutlineSize > 0 ? new Fa.OutlineEffect { Size = options.OutlineSize / 3f } : null,
            Shadow = options.ShadowSize > 0 || options.ShadowDistance > 0
                ? new Fa.ShadowEffect { Size = options.ShadowSize / 4f, Distance = options.ShadowDistance / 4f, Fade = 0.5f }
                : null,
            Bevel = options.BevelStrength > 0 && options.BevelSize > 0
                ? new Fa.BevelEffect { Strength = options.BevelStrength / (float)CdgScalingOptions.MaximumEffect, Size = options.BevelSize / 2f }
                : null,
        }
        : null;

    /// <summary>The largest multiple of the CD+G screen drawn (20x, 4320 high), so an 8K screen does not draw past it.</summary>
    public const int MaximumCdgMultiple = 20;

    /// <summary>
    /// The size the scaled CD+G picture is drawn at for <paramref name="scale"/> on a screen <paramref name="displayHeight"/>
    /// high (0 or less: 1080): a whole multiple of the 216-pixel CD+G screen, at 16:9, which FatzAudio fills exactly and
    /// the renderers fit to the screen.
    /// </summary>
    public static Fa.PixelSize ToCdgRenderSize(CdgScale scale, int displayHeight)
    {
        const int ScreenHeight = CdgConstants.FrameHeight;
        var display = displayHeight > 0 ? displayHeight : 1080;
        var multiple = scale switch
        {
            CdgScale.Low => 2,
            CdgScale.Medium => Math.Max(2, display / 2 / ScreenHeight), // never below Low on a small screen
            _ => display / ScreenHeight,
        };
        var height = Math.Clamp(multiple, 1, MaximumCdgMultiple) * ScreenHeight;
        return new Fa.PixelSize((int)Math.Round(height * 16 / 9.0), height);
    }

    // Codecs FatzAudio decodes itself on every platform (no system decoder needed), by FFmpeg's names.
    static readonly HashSet<string> PacketCodecs = new(StringComparer.Ordinal) { "mp3", "mp2", "aac", "ac3", "eac3", "flac", "opus", "vorbis" };

    /// <summary>
    /// Whether FatzAudio decodes a soundtrack of FFmpeg's <paramref name="codec"/> from its packets on every platform:
    /// MP3, MP2, AAC, AC-3, E-AC-3, FLAC, Opus, Vorbis. Others (DTS, TrueHD, PCM in odd layouts...) stay decoded by FFmpeg.
    /// </summary>
    public static bool DecodesPackets(string codec) => PacketCodecs.Contains(codec);
}

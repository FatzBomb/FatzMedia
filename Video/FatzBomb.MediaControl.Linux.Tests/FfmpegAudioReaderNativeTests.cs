using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Linux.Runtime;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in native check of the FFmpeg audio path used for video-audio normalization (Stage 033A).
/// Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> as for <see cref="LinuxNativeSmokeTests"/>; the test
/// writes its own WAV, so no sample file is needed. Without the variable it returns immediately.
/// </summary>
public sealed class FfmpegAudioReaderNativeTests
{
    [Fact]
    public void RealFfmpeg_ReadsPcmAndFindsTheKnownPeak()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _), $"dlopen failed for {path}");
        }

        Assert.True(operations.TryActivateFfmpeg(directory));

        var wav = Path.Combine(Path.GetTempPath(), $"fatzbomb-peak-{Guid.NewGuid():N}.wav");
        try
        {
            // 20 s of a 440 Hz tone at half scale, with one louder 0.9 burst at 15 s that only the
            // deep pass (which samples the whole file) can see.
            WriteStereoTone(wav, sampleRate: 44100, seconds: 20, amplitude: 0.5, burstAtSeconds: 15, burstAmplitude: 0.9);

            using FfmpegAudioReader? reader = FfmpegAudioReader.Open(wav);
            Assert.NotNull(reader);
            Assert.Equal(44100, reader.SampleRate);
            Assert.Equal(2, reader.Channels);
            Assert.InRange(reader.DurationSeconds, 19.9, 20.1);

            var quickPeak = FfmpegAudioPeakScanner.ScanQuick(reader, CancellationToken.None);
            Assert.InRange(quickPeak, 0.49, 0.51);

            var deepPeak = FfmpegAudioPeakScanner.ScanDeep(reader, CancellationToken.None);
            Assert.InRange(deepPeak, 0.89, 0.91);
            Assert.InRange(AudioNormalizer.CalculateGainFromPeak(deepPeak), 0.87, 0.91);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    private static void WriteStereoTone(string path, int sampleRate, int seconds, double amplitude, int burstAtSeconds, double burstAmplitude)
    {
        const short channels = 2;
        const short bitsPerSample = 16;
        var frames = sampleRate * seconds;
        var dataBytes = frames * channels * (bitsPerSample / 8);

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var frame = 0; frame < frames; frame++)
        {
            var time = frame / (double)sampleRate;
            var level = time >= burstAtSeconds && time < burstAtSeconds + 1 ? burstAmplitude : amplitude;
            var sample = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * time) * level * short.MaxValue);
            writer.Write(sample);
            writer.Write(sample);
        }
    }
}

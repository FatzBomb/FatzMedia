using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Linux.Runtime;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check of the FFmpeg audio paths of Stage 033A: reading PCM and the unsupported-format
/// conversion.
/// Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> as for <see cref="LinuxNativeSmokeTests"/>; the test
/// writes its own WAV, so no sample file is needed. Without the variable it returns immediately.
/// </summary>
public sealed class FfmpegAudioReaderNativeTests
{
    [Fact]
    public void RealFfmpeg_ReadsPcm()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        ActivateFfmpeg(directory);
        var wav = Path.Combine(Path.GetTempPath(), $"fatzbomb-peak-{Guid.NewGuid():N}.wav");
        try
        {
            // 20 s of a 440 Hz tone at half scale, with one louder burst at 15 s.

            WriteStereoTone(wav, sampleRate: 44100, seconds: 20, amplitude: 0.5, burstAtSeconds: 15, burstAmplitude: 0.9);

            using var reader = FfmpegAudioReader.Open(wav);
            Assert.NotNull(reader);
            Assert.Equal(44100, reader.SampleRate);
            Assert.Equal(2, reader.Channels);
            Assert.InRange(reader.DurationSeconds, 19.9, 20.1);

        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void RealFfmpeg_Pcm16Source_ReadsAsTheSame16BitSamples_AndFloatOnRequest()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        ActivateFfmpeg(directory);
        var wav = Path.Combine(Path.GetTempPath(), $"fatzbomb-pcm16-{Guid.NewGuid():N}.wav");
        try
        {
            WriteStereoTone(wav, sampleRate: 44100, seconds: 1, amplitude: 0.5, burstAtSeconds: 5, burstAmplitude: 0.5);

            using (var integers = FfmpegAudioReader.Open(wav, allowInt16Pcm: true))
            {
                Assert.NotNull(integers);
                Assert.True(integers.IsInt16);
                Assert.Equal(sizeof(short), integers.BytesPerSample);
                var first = integers.ReadFrameBytes();
                Assert.False(first.IsEmpty);
                // The tone's first samples, rounded to 16 bits exactly as the file holds them.
                var left = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(first);
                Assert.Equal((short)Math.Round(Math.Sin(2 * Math.PI * 440 * 1 / 44100d) * 0.5 * short.MaxValue), left[2]);
            }

            using var floats = FfmpegAudioReader.Open(wav);
            Assert.NotNull(floats);
            Assert.False(floats.IsInt16);
            Assert.Equal(sizeof(float), floats.BytesPerSample);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var asIntegers = FfmpegAudioReader.Open(wav, allowInt16Pcm: true)!;
                _ = asIntegers.ReadFrame();
            });
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public void RealFfmpeg_CancelledConversion_Throws()
    {
        // The load's token reaches the conversion: Reset, Dispose or the host stop it.
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        ActivateFfmpeg(directory);
        var source = Path.Combine(Path.GetTempPath(), $"fatzbomb-cancel-{Guid.NewGuid():N}.wav");
        try
        {
            WriteStereoTone(source, sampleRate: 48000, seconds: 2, amplitude: 0.5, burstAtSeconds: 1, burstAmplitude: 0.75);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            _ = Assert.ThrowsAny<OperationCanceledException>(() => FfmpegAudioFallback.Take(source, cancelled.Token));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void RealFfmpeg_ConvertsAPcm16Source_To16BitWavFfmpegReadsBack_InMemoryAndAsTempFile()
    {
        var directory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsLinux())
            return;

        ActivateFfmpeg(directory);
        var source = Path.Combine(Path.GetTempPath(), $"fatzbomb-convert-{Guid.NewGuid():N}.wav");
        var roundTrip = Path.ChangeExtension(source, ".roundtrip.wav");
        try
        {
            WriteStereoTone(source, sampleRate: 48000, seconds: 3, amplitude: 0.5, burstAtSeconds: 2, burstAmplitude: 0.75);

            using (var inMemory = FfmpegAudioFallback.Take(source, CancellationToken.None))
            {
                Assert.NotNull(inMemory);
                Assert.True(inMemory.IsInMemory);
                // A 16-bit source stays 16-bit: half the size of float, the same samples.
                Assert.Equal(Pcm16Wav.HeaderBytes + (48000L * 3 * 2 * sizeof(short)), inMemory.Length);
                File.WriteAllBytes(roundTrip, inMemory.InMemoryData.ToArray());
                AssertReadsBack(roundTrip);
            }

            var previousLimit = FfmpegAudioFallback.MaxInMemoryBytes;
            FfmpegAudioFallback.MaxInMemoryBytes = 64 * 1024;
            try
            {
                string tempPath;
                using (var spilled = FfmpegAudioFallback.Take(source, CancellationToken.None))
                {
                    Assert.NotNull(spilled);
                    Assert.False(spilled.IsInMemory);
                    tempPath = spilled.TempPath!;
                    Assert.Equal(spilled.Length, new FileInfo(tempPath).Length);
                    AssertReadsBack(tempPath);
                }

                Assert.False(File.Exists(tempPath), "the temp WAV is deleted on dispose");

                // A prepared conversion is kept for the transition until released.
                var prepared = FfmpegAudioFallback.Prepare(source, CancellationToken.None);
                Assert.NotNull(prepared);
                Assert.True(File.Exists(prepared.TempPath));
                FfmpegAudioFallback.Release(source);
                Assert.False(File.Exists(prepared.TempPath), "a released prepared conversion is deleted");
                using var reconverted = FfmpegAudioFallback.Take(source, CancellationToken.None);
                Assert.NotSame(prepared, reconverted);
            }
            finally
            {
                FfmpegAudioFallback.MaxInMemoryBytes = previousLimit;
            }
        }
        finally
        {
            File.Delete(source);
            File.Delete(roundTrip);
        }

        static void AssertReadsBack(string path)
        {
            using var reader = FfmpegAudioReader.Open(path);
            Assert.NotNull(reader);
            Assert.Equal(48000, reader.SampleRate);
            Assert.Equal(2, reader.Channels);
            Assert.InRange(reader.DurationSeconds, 2.99, 3.01);
        }
    }

    internal static void ActivateFfmpeg(string directory)
    {
        var operations = new LinuxNativeOperations();
        foreach (var fileName in LinuxNativeLibraryCatalog.FfmpegInLoadOrder)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                Assert.True(operations.TryLoad(path, out _), $"dlopen failed for {path}");
        }

        Assert.True(operations.TryActivateFfmpeg(directory));
    }

    static void WriteStereoTone(string path, int sampleRate, int seconds, double amplitude, int burstAtSeconds, double burstAmplitude)
    {
        const short Channels = 2;
        const short BitsPerSample = 16;
        var frames = sampleRate * seconds;
        var dataBytes = frames * Channels * (BitsPerSample / 8);

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(Channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * Channels * (BitsPerSample / 8));
        writer.Write((short)(Channels * (BitsPerSample / 8)));
        writer.Write(BitsPerSample);
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

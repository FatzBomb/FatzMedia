using System.Globalization;
using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

/// <summary>
/// 037A slice 1: normalization peak analysis and silence analysis read levels through
/// <see cref="IAudioLevelSource"/>, not a BASS handle. The recorded reads pin the order the
/// analyzers made their BASS calls in before the seam (each read maps to the same calls in
/// <see cref="BassChannelLevelSource"/>).
/// </summary>
public sealed class AudioLevelSourceSeamTests
{
    [Fact]
    public async Task QuickPeak_ReadsUpToTwentyFourQuarterSecondBlocks()
    {
        var source = new FakeLevelSource(durationSeconds: 60, peaks: [0.2, 0.5, 0.4]);

        var peak = await new AudioNormalizer().AnalyzePeakQuickAsync(source, updateGain: false);

        Assert.Equal(0.5, peak);
        Assert.Equal(Enumerable.Repeat("Read:0.25", 24), source.Calls);
    }

    [Fact]
    public async Task FullPeak_WithKnownLength_SeeksEachWindow()
    {
        var source = new FakeLevelSource(durationSeconds: 2, peaks: [0.1, 1.5, 0.3, 0.2]);

        var peak = await new AudioNormalizer().AnalyzePeakAsync(source, updateGain: false);

        // Clamped to 1 as before.
        Assert.Equal(1.0, peak);
        Assert.Equal(
            ["Duration", "Seek:0", "Read:0.5", "Seek:0.5", "Read:0.5", "Seek:1", "Read:0.5", "Seek:1.5", "Read:0.5"],
            source.Calls);
    }

    [Fact]
    public async Task FullPeak_WithUnknownLength_ReadsSequentiallyUntilTheEnd()
    {
        var source = new FakeLevelSource(durationSeconds: 0, peaks: [0.3, 0.6]) { ReadsAvailable = 2 };

        var peak = await new AudioNormalizer().AnalyzePeakAsync(source, updateGain: false);

        Assert.Equal(0.6, peak, 6);
        Assert.Equal(["Duration", "Read:0.5", "Read:0.5", "Read:0.5"], source.Calls);
    }

    [Fact]
    public async Task NoSource_ReadsAsZeroPeak_AsHandleZeroDid()
    {
        var normalizer = new AudioNormalizer();

        Assert.Equal(0, await normalizer.AnalyzePeakQuickAsync(null));
        Assert.Equal(0, await normalizer.AnalyzePeakAsync(null));
        var scan = await new AudioSilenceAnalyzer().ScanAsync(null, 10, -60, CancellationToken.None);
        Assert.Equal(10, scan.SourceDuration);
        Assert.Empty(scan.SilenceRegions);
    }

    [Fact]
    public async Task SilenceScan_ReadsBlocksFromTheStart()
    {
        var source = new FakeLevelSource(durationSeconds: 0.25, peaks: [0.5, 0, 0.5]);

        var scan = await new AudioSilenceAnalyzer().ScanAsync(source, 0.25, -60, CancellationToken.None);

        Assert.Equal(
            ["SeekStart", "Position", "Read:0.1", "Position", "Position", "Read:0.1", "Position", "Position", "Read:0.05", "Position", "Position"],
            source.Calls);
        SilenceLevelSample[] expected = [new(0, 0.1, 0.5), new(0.1, 0.2, 0), new(0.2, 0.25, 0.5)];
        Assert.Equal(SilenceRegionDetector.Detect(expected, -60), scan.SilenceRegions);
    }

    [Fact]
    public async Task SilenceScan_ReportsTheSourceError()
    {
        var source = new FakeLevelSource(durationSeconds: 1, peaks: [0.5]) { FailSeekToStart = true };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AudioSilenceAnalyzer().ScanAsync(source, 1, -60, CancellationToken.None));

        Assert.Equal("Unable to seek the analysis stream to its start: Handle", error.Message);
    }

    sealed class FakeLevelSource(double durationSeconds, double[] peaks) : IAudioLevelSource
    {
        double _position;
        int _reads;

        public List<string> Calls { get; } = [];
        public int ReadsAvailable { get; init; } = int.MaxValue;
        public bool FailSeekToStart { get; init; }
        public string LastError => "Handle";

        public bool TryReadPeak(float seconds, out double peak)
        {
            Calls.Add(string.Create(CultureInfo.InvariantCulture, $"Read:{Math.Round(seconds, 4)}"));
            if (_reads >= ReadsAvailable)
            {
                peak = 0;
                return false;
            }

            peak = peaks[_reads++ % peaks.Length];
            _position += Math.Round(seconds, 4);
            return true;
        }

        public bool TryGetDuration(out double seconds)
        {
            Calls.Add("Duration");
            seconds = durationSeconds;
            return durationSeconds > 0;
        }

        public bool TryGetPosition(out double seconds)
        {
            Calls.Add("Position");
            seconds = Math.Round(_position, 6);
            return true;
        }

        public bool TrySeek(double seconds)
        {
            Calls.Add(string.Create(CultureInfo.InvariantCulture, $"Seek:{seconds}"));
            _position = seconds;
            return true;
        }

        public bool TrySeekToStart()
        {
            Calls.Add("SeekStart");
            _position = 0;
            return !FailSeekToStart;
        }
    }
}

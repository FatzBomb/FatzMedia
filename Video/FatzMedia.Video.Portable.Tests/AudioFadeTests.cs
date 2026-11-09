using FatzMedia.Video.Core.Audio.Fade;

namespace FatzMedia.Video.Tests;

public sealed class AudioFadeEnvelopeTests
{
    [Fact]
    public void Defaults_AreFullToMutedAndMutedToFull()
    {
        Assert.Equal([new AudioFadePoint(0, 1), new AudioFadePoint(1, 0)], AudioFadeEnvelope.Outgoing.Points);
        Assert.Equal([new AudioFadePoint(0, 0), new AudioFadePoint(1, 1)], AudioFadeEnvelope.Incoming.Points);
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(1.01, 0.5)]
    [InlineData(0.5, -0.1)]
    [InlineData(0.5, 1.1)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void Point_OutOfRange_Throws(double position, double gain) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFadePoint(position, gain));

    [Fact]
    public void Point_NegativeZero_NormalizesToZero()
    {
        var point = new AudioFadePoint(-0.0, 0.5);

        Assert.False(double.IsNegative(point.Position));
    }

    [Fact]
    public void WithPoints_UpsertsExactPositionsLastWriteWins_IncludingDefaults()
    {
        var envelope = AudioFadeEnvelope.CreateOutgoing([new(0, 0.8), new(0.5, 0.4), new(0.5, 0.6), new(1, 0.1)]);

        Assert.Equal([new AudioFadePoint(0, 0.8), new AudioFadePoint(0.5, 0.6), new AudioFadePoint(1, 0.1)], envelope.Points);
    }

    [Fact]
    public void WithPoints_DoesNotMergeNearPositions()
    {
        var envelope = AudioFadeEnvelope.Outgoing.WithPoints([new(0.5, 0.5), new(0.5000001, 0.4)]);

        Assert.Equal(4, envelope.Points.Count);
    }

    [Fact]
    public void WithPoint_ReturnsNewSnapshotAndLeavesOriginalUntouched()
    {
        var original = AudioFadeEnvelope.Incoming;

        var changed = original.WithPoint(new AudioFadePoint(0.5, 0.25));

        Assert.NotSame(original, changed);
        Assert.Equal(2, original.Points.Count);
        Assert.Equal(3, changed.Points.Count);
    }

    [Fact]
    public void Points_AreSorted()
    {
        var envelope = AudioFadeEnvelope.Incoming.WithPoints([new(0.75, 0.9), new(0.25, 0.1)]);

        Assert.Equal([0, 0.25, 0.75, 1], envelope.Points.Select(static point => point.Position));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.25, 0.75)]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 0)]
    [InlineData(-5, 1)]
    [InlineData(5, 0)]
    public void Evaluate_InterpolatesLinearlyAndClamps(double position, double expected) =>
        Assert.Equal(expected, AudioFadeEnvelope.Outgoing.Evaluate(position), precision: 12);

    [Fact]
    public void Evaluate_HoldsOuterPointsWhenEndsAreOverridden()
    {
        // Upserting at 0 and 1 is the only way to change the ends; interior evaluation interpolates.
        var envelope = AudioFadeEnvelope.CreateIncoming([new(0, 0.2), new(1, 0.6)]);

        Assert.Equal(0.4, envelope.Evaluate(0.5), precision: 12);
    }

    [Fact]
    public void AudibleStart_DefaultIncomingIsZero() =>
        Assert.Equal(0, AudioFadeEnvelope.Incoming.AudibleStart);

    [Fact]
    public void AudibleStart_LeadingZeroPlateauDelaysStart()
    {
        var envelope = AudioFadeEnvelope.CreateIncoming([new(0.2, 0), new(0.4, 0)]);

        Assert.Equal(0.4, envelope.AudibleStart);
    }

    [Fact]
    public void AudibleStart_AllZeroIsNeverAudible()
    {
        var envelope = AudioFadeEnvelope.CreateIncoming([new(1, 0)]);

        Assert.Equal(1, envelope.AudibleStart);
    }

    [Fact]
    public void AudibleStart_AudibleFirstPointIsZero() =>
        Assert.Equal(0, AudioFadeEnvelope.Outgoing.AudibleStart);

    [Fact]
    public void CrossfadeOptions_DefaultDisabledWithDefaultEnvelopes()
    {
        var options = new AudioCrossfadeOptions();

        Assert.False(options.IsEnabled);
        Assert.Same(AudioFadeEnvelope.Outgoing, options.Outgoing);
        Assert.Same(AudioFadeEnvelope.Incoming, options.Incoming);
    }

    [Fact]
    public void CrossfadeOptions_NormalizesDuration()
    {
        Assert.Equal(TimeSpan.Zero, new AudioCrossfadeOptions { Duration = TimeSpan.FromSeconds(-1) }.Duration);
        Assert.Equal(AudioCrossfadeOptions.MaximumDuration, new AudioCrossfadeOptions { Duration = TimeSpan.FromHours(1) }.Duration);
        Assert.True(new AudioCrossfadeOptions { Duration = TimeSpan.FromSeconds(3) }.IsEnabled);
    }

    [Fact]
    public void CrossfadeOptions_NullEnvelopeFallsBackToDefault()
    {
        var options = new AudioCrossfadeOptions { Incoming = null!, Outgoing = null! };

        Assert.Same(AudioFadeEnvelope.Incoming, options.Incoming);
        Assert.Same(AudioFadeEnvelope.Outgoing, options.Outgoing);
    }
}

public sealed class AudioFadeScheduleTests
{
    [Fact]
    public void Build_DefaultOutgoing_IsOneRamp()
    {
        var segments = AudioFadeSchedule.Build(AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(4));

        var segment = Assert.Single(segments);
        Assert.Equal(TimeSpan.Zero, segment.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(4), segment.Duration);
        Assert.Equal(1, segment.FromGain);
        Assert.Equal(0, segment.ToGain);
    }

    [Fact]
    public void Build_ChainsRampsBetweenPoints()
    {
        var envelope = AudioFadeEnvelope.Outgoing.WithPoint(new AudioFadePoint(0.25, 0.5));

        var segments = AudioFadeSchedule.Build(envelope, TimeSpan.FromSeconds(4));

        Assert.Equal(
            [
                new AudioFadeSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), 1, 0.5),
                new AudioFadeSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 0.5, 0),
            ],
            segments);
    }

    [Fact]
    public void Build_FromAudibleStart_SkipsMutedPlateauTime()
    {
        var envelope = AudioFadeEnvelope.CreateIncoming([new(0.5, 0)]);

        var segments = AudioFadeSchedule.Build(envelope, TimeSpan.FromSeconds(4), envelope.AudibleStart);

        var segment = Assert.Single(segments);
        Assert.Equal(TimeSpan.Zero, segment.StartOffset);
        Assert.Equal(TimeSpan.FromSeconds(2), segment.Duration);
        Assert.Equal(0, segment.FromGain);
        Assert.Equal(1, segment.ToGain);
    }

    [Fact]
    public void Build_ZeroDuration_IsEmpty() =>
        Assert.Empty(AudioFadeSchedule.Build(AudioFadeEnvelope.Outgoing, TimeSpan.Zero));

    [Fact]
    public void Build_SegmentsSumToRemainingDuration()
    {
        var envelope = AudioFadeEnvelope.Incoming.WithPoints([new(0.1, 0.3), new(0.6, 0.7), new(0.9, 0.95)]);

        var segments = AudioFadeSchedule.Build(envelope, TimeSpan.FromSeconds(10), 0.1);

        Assert.Equal(TimeSpan.FromSeconds(9), TimeSpan.FromTicks(segments.Sum(static s => s.Duration.Ticks)));
    }

    [Theory]
    [InlineData(0.5, 0.8, 1.5, 0.6)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(0, 1, 2, 0)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(1, 1, 20, 8)]
    [InlineData(double.NaN, 1, 1, 0)]
    public void ComposeGain_IsMultiplicativeAndClamped(double envelope, double volume, double normalization, double expected) =>
        Assert.Equal(expected, AudioFadeSchedule.ComposeGain(envelope, volume, normalization), precision: 12);
}

public sealed class AudioFadeRunnerTests
{
    [Fact]
    public async Task Run_AppliesInitialGainThenEachRamp()
    {
        var target = new RecordingTarget();
        var delays = new List<TimeSpan>();
        var runner = new AudioFadeRunner((delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var envelope = AudioFadeEnvelope.Outgoing.WithPoint(new AudioFadePoint(0.5, 0.25));

        Assert.True(await runner.RunAsync(target, envelope, TimeSpan.FromSeconds(2), 0, CancellationToken.None));

        Assert.Equal([(1d, TimeSpan.Zero), (0.25, TimeSpan.FromSeconds(1)), (0d, TimeSpan.FromSeconds(1))], target.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Run_UnavailableTarget_ReturnsFalseWithoutCalls()
    {
        var target = new RecordingTarget { IsFadeAvailable = false };
        var runner = new AudioFadeRunner((_, _) => Task.CompletedTask);

        Assert.False(await runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));
        Assert.Empty(target.Calls);
    }

    [Fact]
    public async Task Run_RejectedRamp_ReturnsFalse()
    {
        var target = new RecordingTarget { RejectAfter = 1 };
        var runner = new AudioFadeRunner((_, _) => Task.CompletedTask);

        Assert.False(await runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));
    }

    [Fact]
    public async Task Run_NewFadeSupersedesRunningFade()
    {
        var target = new RecordingTarget();
        var firstDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCount = 0;
        var runner = new AudioFadeRunner(async (_, token) =>
        {
            if (Interlocked.Increment(ref delayCount) == 1)
            {
                firstDelay.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        });

        var first = runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(5), 0, CancellationToken.None);
        await firstDelay.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = runner.RunAsync(target, AudioFadeEnvelope.Incoming, TimeSpan.FromSeconds(1), 0, CancellationToken.None);

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));

        // The superseding fade ramps from wherever the first one had reached; it does not
        // snap to its own start gain (0) first.
        Assert.Equal([(1d, TimeSpan.Zero), (0d, TimeSpan.FromSeconds(5)), (1d, TimeSpan.FromSeconds(1))], target.Calls);
    }

    [Fact]
    public async Task Cancel_StopsRunningFade()
    {
        var target = new RecordingTarget();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AudioFadeRunner(async (_, token) =>
        {
            delayStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        var run = runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(5), 0, CancellationToken.None);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Cancel();

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Run_SessionEndedByTransport_StopsAtNextRamp()
    {
        var target = new RecordingTarget();
        var runner = new AudioFadeRunner((_, _) =>
        {
            target.EndSession();
            return Task.CompletedTask;
        });
        var envelope = AudioFadeEnvelope.Outgoing.WithPoint(new AudioFadePoint(0.5, 0.5));

        Assert.False(await runner.RunAsync(target, envelope, TimeSpan.FromSeconds(2), 0, CancellationToken.None));
        Assert.Equal(2, target.Calls.Count);
    }

    [Fact]
    public async Task Run_CallerCancellation_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new AudioFadeRunner((_, token) => Task.Delay(1, token));

        Assert.False(await runner.RunAsync(new RecordingTarget(), AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, cts.Token));
    }

    [Fact]
    public async Task RunWithOutcome_DistinguishesUnavailableFromInterrupted()
    {
        var runner = new AudioFadeRunner((_, _) => Task.CompletedTask);

        Assert.Equal(
            AudioFadeOutcome.Unavailable,
            await runner.RunWithOutcomeAsync(new RecordingTarget { IsFadeAvailable = false }, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));
        Assert.Equal(
            AudioFadeOutcome.Interrupted,
            await runner.RunWithOutcomeAsync(new RecordingTarget { RejectAfter = 1 }, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));
        Assert.Equal(
            AudioFadeOutcome.Completed,
            await runner.RunWithOutcomeAsync(new RecordingTarget(), AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreUnityIfIdle_RampsToFullGainOnlyWhenNoFadeRuns()
    {
        var target = new RecordingTarget();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new AudioFadeRunner(async (_, token) =>
        {
            delayStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });

        var running = runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(5), 0, CancellationToken.None);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(runner.RestoreUnityIfIdle(target, TimeSpan.FromMilliseconds(250)));

        runner.Cancel();
        Assert.False(await running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(runner.RestoreUnityIfIdle(target, TimeSpan.FromMilliseconds(250)));
        Assert.Equal((1d, TimeSpan.FromMilliseconds(250)), target.Calls[^1]);
    }

    [Fact]
    public async Task Cancel_RacingWithCompletion_NeverThrows()
    {
        var target = new RecordingTarget();
        var runner = new AudioFadeRunner(async (_, _) => await Task.Yield());

        for (var i = 0; i < 200; i++)
        {
            var run = Task.Run(() => runner.RunAsync(target, AudioFadeEnvelope.Outgoing, TimeSpan.FromMilliseconds(1), 0, CancellationToken.None));
            runner.Cancel();
            _ = await run.WaitAsync(TimeSpan.FromSeconds(10));
            runner.Cancel();
        }
    }

    sealed class RecordingTarget : IAudioFadeGainTarget
    {
        int _session;

        public List<(double Gain, TimeSpan Ramp)> Calls { get; } = [];
        public bool IsFadeAvailable { get; init; } = true;
        public int RejectAfter { get; init; } = int.MaxValue;

        public int BeginFadeSession() => IsFadeAvailable ? ++_session : 0;

        public bool SetFadeGain(int session, double gain, TimeSpan ramp)
        {
            if (session != _session || Calls.Count >= RejectAfter)
                return false;

            Calls.Add((gain, ramp));
            return true;
        }

        /// <summary>Simulates Stop/Open/Close ending the session.</summary>
        public void EndSession() => _session++;
    }
}

using FatzBomb.MediaControl.Core.Audio;

namespace FatzBomb.MediaControl.Tests;

public class AudioOutputRecoveryTrackerTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    private static AudioOutputObservation Healthy(int playing = 1) => new(BassOutputStatus.Started, playing, 0);

    private static AudioOutputObservation OutputStopped(int playing = 1, int parked = 1) => new(BassOutputStatus.Stopped, playing, parked);

    private static AudioOutputObservation Parked(BassOutputStatus output = BassOutputStatus.Started, int playing = 1, int parked = 1) => new(output, playing, parked);

    private static AudioOutputRecoveryTracker LostTracker()
    {
        var tracker = new AudioOutputRecoveryTracker();
        Assert.Equal(AudioOutputRecoveryAction.ReportLost, tracker.Decide(OutputStopped(), T0));
        tracker.Record(AudioOutputRecoveryAction.ReportLost, true, T0);
        return tracker;
    }

    [Fact]
    public void Decide_HealthyOutput_DoesNothing()
    {
        var tracker = new AudioOutputRecoveryTracker();

        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(Healthy(), T0));
        Assert.False(tracker.OutageReported);
    }

    [Fact]
    public void Decide_NothingIntendsToPlay_IgnoresStoppedOutput()
    {
        var tracker = new AudioOutputRecoveryTracker();

        // A host-paused or stopped player is not ours to resume, and a stopped output with
        // nothing playing is not an outage.
        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(new AudioOutputObservation(BassOutputStatus.Stopped, 0, 0), T0));
    }

    [Fact]
    public void Decide_OutputStoppedWhilePlaying_ReportsLost()
    {
        var tracker = new AudioOutputRecoveryTracker();

        Assert.Equal(AudioOutputRecoveryAction.ReportLost, tracker.Decide(OutputStopped(parked: 0), T0));
    }

    [Fact]
    public void Decide_ChannelPausedByDeviceWithStartedOutput_ReportsLost()
    {
        var tracker = new AudioOutputRecoveryTracker();

        Assert.Equal(AudioOutputRecoveryAction.ReportLost, tracker.Decide(Parked(), T0));
    }

    [Fact]
    public void Decide_ChannelPausedByDeviceWithUnknownOutput_ReportsLost()
    {
        var tracker = new AudioOutputRecoveryTracker();

        Assert.Equal(AudioOutputRecoveryAction.ReportLost, tracker.Decide(Parked(BassOutputStatus.Unknown), T0));
    }

    [Fact]
    public void Decide_AfterReportLost_StartsOutputImmediately()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();

        Assert.True(tracker.OutageReported);
        Assert.Equal(AudioOutputRecoveryAction.Start, tracker.Decide(OutputStopped(), T0));
    }

    [Fact]
    public void Decide_LossIsReportedOncePerOutage()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();

        for (var second = 0; second < 5; second++)
        {
            TimeSpan now = T0 + TimeSpan.FromSeconds(second);
            AudioOutputRecoveryAction action = tracker.Decide(OutputStopped(), now);
            Assert.NotEqual(AudioOutputRecoveryAction.ReportLost, action);
            if (action != AudioOutputRecoveryAction.None)
                tracker.Record(action, succeeded: false, now);
        }
    }

    [Fact]
    public void Decide_RetriesAboutOnceASecond()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: false, T0);

        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromMilliseconds(500)));
        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromMilliseconds(999)));
        Assert.NotEqual(AudioOutputRecoveryAction.None, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Decide_FailedStart_ReinitializesNext()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: false, T0);

        Assert.Equal(AudioOutputRecoveryAction.Reinit, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Decide_FailedReinit_AlternatesWithStart()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: false, T0);
        tracker.Record(AudioOutputRecoveryAction.Reinit, succeeded: false, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(AudioOutputRecoveryAction.Start, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Decide_OutputBackWithChannelsParked_ResumesChannels()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: true, T0);

        Assert.Equal(AudioOutputRecoveryAction.ResumeChannels, tracker.Decide(Parked(), T0));
    }

    [Fact]
    public void Decide_ResumeThatLeavesChannelsParked_ReinitializesAtNextRetry()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: true, T0);
        tracker.Record(AudioOutputRecoveryAction.ResumeChannels, succeeded: false, T0);

        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(Parked(), T0 + TimeSpan.FromMilliseconds(100)));
        Assert.Equal(AudioOutputRecoveryAction.Reinit, tracker.Decide(Parked(), T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Decide_AfterReinit_ResumesParkedChannelsAgain()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: true, T0);
        tracker.Record(AudioOutputRecoveryAction.ResumeChannels, succeeded: false, T0);
        tracker.Record(AudioOutputRecoveryAction.Reinit, succeeded: true, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(AudioOutputRecoveryAction.ResumeChannels, tracker.Decide(Parked(), T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Decide_HealthyAfterOutage_ReportsRecoveredOnce()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: true, T0);

        Assert.Equal(AudioOutputRecoveryAction.ReportRecovered, tracker.Decide(Healthy(), T0));
        tracker.Record(AudioOutputRecoveryAction.ReportRecovered, true, T0);

        Assert.False(tracker.OutageReported);
        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(Healthy(), T0 + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Decide_SecondOutage_IsReportedAgain()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.ReportRecovered, true, T0);

        Assert.Equal(AudioOutputRecoveryAction.ReportLost, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Decide_SecondOutage_StartsWithPlainRestart()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: false, T0);
        tracker.Record(AudioOutputRecoveryAction.ReportRecovered, true, T0 + TimeSpan.FromSeconds(1));
        tracker.Record(AudioOutputRecoveryAction.ReportLost, true, T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(AudioOutputRecoveryAction.Start, tracker.Decide(OutputStopped(), T0 + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Decide_OutageWithEveryPlayerPaused_StopsRetrying()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();

        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(new AudioOutputObservation(BassOutputStatus.Stopped, 0, 0), T0 + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CanAttempt_ExternalPlayRestart_SharesTheRateLimit()
    {
        var tracker = new AudioOutputRecoveryTracker();
        Assert.True(tracker.CanAttempt(T0));

        tracker.RecordExternalAttempt(reinit: false, succeeded: false, T0);

        Assert.False(tracker.CanAttempt(T0 + TimeSpan.FromMilliseconds(200)));
        Assert.True(tracker.CanAttempt(T0 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Reset_ForgetsTheOutage()
    {
        AudioOutputRecoveryTracker tracker = LostTracker();
        tracker.Record(AudioOutputRecoveryAction.Start, succeeded: false, T0);

        tracker.Reset();

        Assert.False(tracker.OutageReported);
        Assert.True(tracker.CanAttempt(T0));
        Assert.Equal(AudioOutputRecoveryAction.None, tracker.Decide(Healthy(), T0));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveRetryInterval() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioOutputRecoveryTracker(TimeSpan.Zero));

    [Fact]
    public void Decide_FullOutageScenario_RecoversThroughReinit()
    {
        // Another application holds the device exclusively for three seconds: BASS_Start fails,
        // the reinit fails while the device is busy, and the next reinit brings it back.
        AudioOutputRecoveryTracker tracker = LostTracker();
        var outputUp = false;
        var steps = new List<AudioOutputRecoveryAction>();
        for (var second = 0; second <= 4 && tracker.OutageReported; second++)
        {
            TimeSpan now = T0 + TimeSpan.FromSeconds(second);
            for (var step = 0; step < 8; step++)
            {
                AudioOutputObservation observation = outputUp ? Healthy() : OutputStopped();
                AudioOutputRecoveryAction action = tracker.Decide(observation, now);
                if (action == AudioOutputRecoveryAction.None)
                    break;

                steps.Add(action);
                var succeeded = action switch
                {
                    AudioOutputRecoveryAction.Start => false,
                    AudioOutputRecoveryAction.Reinit => second >= 3,
                    _ => true,
                };
                if (action == AudioOutputRecoveryAction.Reinit && succeeded)
                    outputUp = true;
                tracker.Record(action, succeeded, now);
            }
        }

        Assert.Equal(
            [
                AudioOutputRecoveryAction.Start,
                AudioOutputRecoveryAction.Reinit,
                AudioOutputRecoveryAction.Start,
                AudioOutputRecoveryAction.Reinit,
                AudioOutputRecoveryAction.ReportRecovered,
            ],
            steps);
        Assert.False(tracker.OutageReported);
    }
}

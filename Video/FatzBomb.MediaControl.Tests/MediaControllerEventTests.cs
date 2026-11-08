using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Tests;

public sealed class MediaControllerEventTests
{
    [Fact]
    public void MediaEffects_RaisesOnlyActualValueChanges()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        var effects = new MediaEffects(engine);
        var volumeEvents = new List<MediaValueChangedEventArgs<double>>();
        var pitchEvents = new List<MediaValueChangedEventArgs<int>>();
        var tempoEvents = new List<MediaValueChangedEventArgs<int>>();
        var normalizeEvents = new List<MediaNormalizeChangedEventArgs>();
        effects.VolumeChanged += (_, e) => volumeEvents.Add(e);
        effects.PitchChanged += (_, e) => pitchEvents.Add(e);
        effects.TempoChanged += (_, e) => tempoEvents.Add(e);
        effects.NormalizeChanged += (_, e) => normalizeEvents.Add(e);

        effects.Volume = 0.5;
        effects.Volume = 0.5;
        effects.Pitch = 3;
        effects.Pitch = 3;
        effects.Tempo = 125;
        effects.Tempo = 125;
        effects.Normalize = false;
        effects.Normalize = false;

        MediaValueChangedEventArgs<double> volume = Assert.Single(volumeEvents);
        Assert.Equal(1, volume.PreviousValue);
        Assert.Equal(0.5, volume.CurrentValue);
        MediaValueChangedEventArgs<int> pitch = Assert.Single(pitchEvents);
        Assert.Equal(0, pitch.PreviousValue);
        Assert.Equal(3, pitch.CurrentValue);
        Assert.Equal(MediaChangeReason.User, pitch.Reason);
        MediaValueChangedEventArgs<int> tempo = Assert.Single(tempoEvents);
        Assert.Equal(100, tempo.PreviousValue);
        Assert.Equal(125, tempo.CurrentValue);
        Assert.Equal(MediaChangeReason.User, tempo.Reason);
        MediaNormalizeChangedEventArgs normalize = Assert.Single(normalizeEvents);
        Assert.True(normalize.PreviousValue);
        Assert.False(normalize.CurrentValue);
        Assert.Equal(1, normalize.NormalizationGain);
    }

    [Fact]
    public void MediaControls_PauseRaisesPausedBeforeStateChanged()
    {
        var engine = new TestMediaEngine();
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        _ = engine.Play();
        engine.Position = 12.5;
        var controls = new MediaControls(engine);
        var order = new List<string>();
        MediaPausedEventArgs? paused = null;
        MediaStateChangedEventArgs? state = null;
        controls.MediaPaused += (_, e) => { paused = e; order.Add("paused"); };
        controls.StateChanged += (_, e) => { state = e; order.Add("state"); };

        Assert.True(controls.Pause());

        Assert.Equal(["paused", "state"], order);
        Assert.Equal(TimeSpan.FromSeconds(12.5), paused!.Position);
        Assert.Equal(MediaPlayerState.Playing, state!.PreviousState);
        Assert.Equal(MediaPlayerState.Paused, state.CurrentState);
    }

    [Fact]
    public void MediaControls_InternalLoopRaisesEndedThenLooped()
    {
        var engine = new TestMediaEngine { HandlesLoopInternally = true };
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        _ = engine.Play();
        var controls = new MediaControls(engine) { LoopMedia = true };
        var order = new List<string>();
        controls.MediaEnded += (_, _) => order.Add("ended");
        controls.MediaLooped += (_, _) => order.Add("looped");

        engine.RaiseMediaEnded();

        Assert.Equal(["ended", "looped"], order);
    }

    [Fact]
    public void MediaControls_InternalVideoLoopRaisesLoopedOnlyFromEngineSuccessSignal()
    {
        var engine = new TestMediaEngine
        {
            Content = MediaContent.Video,
            HandlesLoopInternally = true
        };
        _ = engine.Open(MediaInputSource.FromFile("video.mp4"));
        var controls = new MediaControls(engine) { LoopMedia = true };
        var order = new List<string>();
        controls.MediaEnded += (_, _) => order.Add("ended");
        controls.MediaLooped += (_, _) => order.Add("looped");

        engine.RaiseMediaEnded();
        Assert.Equal(["ended"], order);

        engine.RaiseMediaLooped();

        Assert.Equal(["ended", "looped"], order);
    }

    [Fact]
    public void MediaControls_LoopSettingUsesActualChangeGuard()
    {
        var engine = new TestMediaEngine();
        var controls = new MediaControls(engine);
        var changes = new List<MediaValueChangedEventArgs<bool>>();
        controls.LoopMediaChanged += (_, e) => changes.Add(e);

        controls.LoopMedia = true;
        controls.LoopMedia = true;

        MediaValueChangedEventArgs<bool> change = Assert.Single(changes);
        Assert.False(change.PreviousValue);
        Assert.True(change.CurrentValue);
        Assert.True(engine.LoopPlayback);
    }

    [Fact]
    public void MediaFile_RaisesLoadedReplacementClosedAndExplicitClosed()
    {
        var engine = new TestMediaEngine();
        var controls = new MediaControls(engine);
        var file = new MediaFile(engine, mediaControls: controls);
        var events = new List<string>();
        file.MediaLoaded += (_, e) => events.Add($"loaded:{e.Source.DisplayName}");
        file.MediaClosed += (_, e) => events.Add($"closed:{e.Source.DisplayName}");

        Assert.True(file.Load(MediaInputSource.FromFile("first.mp3"), null, false, CancellationToken.None, out _));
        Assert.True(file.Load(MediaInputSource.FromFile("second.mp3"), null, false, CancellationToken.None, out _));
        file.Close();

        Assert.Equal(
            ["loaded:first.mp3", "closed:first.mp3", "loaded:second.mp3", "closed:second.mp3"],
            events);
    }

    [Fact]
    public void NewControllerEvent_IsolatesThrowingHandler()
    {
        var engine = new TestMediaEngine();
        var controls = new MediaControls(engine);
        var secondHandlerRan = false;
        controls.LoopMediaChanged += (_, _) => throw new InvalidOperationException("host failure");
        controls.LoopMediaChanged += (_, _) => secondHandlerRan = true;

        controls.LoopMedia = true;

        Assert.True(secondHandlerRan);
    }

    [Fact]
    public void SessionEventPayloads_AreImmutable()
    {
        Type[] payloadTypes =
        [
            typeof(MediaSourceInfo),
            typeof(MediaLoadedEventArgs),
            typeof(MediaClosedEventArgs),
            typeof(MediaPausedEventArgs),
            typeof(MediaLoopedEventArgs),
            typeof(MediaStateChangedEventArgs),
            typeof(MediaPositionChangedEventArgs),
            typeof(MediaSeekCompletedEventArgs),
            typeof(MediaNormalizeChangedEventArgs),
            typeof(MediaValueChangedEventArgs<int>)
        ];

        foreach (Type payloadType in payloadTypes)
            Assert.All(payloadType.GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void MediaEffects_LoadResetUsesReasonAndActualChangeGuards()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        _ = engine.Open(MediaInputSource.FromFile("song.mp3"));
        var effects = new MediaEffects(engine)
        {
            Pitch = 5,
            Tempo = 125
        };
        var pitchEvents = new List<MediaValueChangedEventArgs<int>>();
        var tempoEvents = new List<MediaValueChangedEventArgs<int>>();
        effects.PitchChanged += (_, e) => pitchEvents.Add(e);
        effects.TempoChanged += (_, e) => tempoEvents.Add(e);

        effects.ApplyLoadReset(resetPitch: true, resetTempo: true);
        effects.ApplyLoadReset(resetPitch: true, resetTempo: true);

        MediaValueChangedEventArgs<int> pitch = Assert.Single(pitchEvents);
        Assert.Equal(5, pitch.PreviousValue);
        Assert.Equal(0, pitch.CurrentValue);
        Assert.Equal(MediaChangeReason.LoadReset, pitch.Reason);
        MediaValueChangedEventArgs<int> tempo = Assert.Single(tempoEvents);
        Assert.Equal(125, tempo.PreviousValue);
        Assert.Equal(100, tempo.CurrentValue);
        Assert.Equal(MediaChangeReason.LoadReset, tempo.Reason);
    }
}

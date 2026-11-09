using FatzMedia.Audio;
using Xunit;

namespace FatzMedia.Audio.Tests;

/// <summary>Sounds: overlapping voices, the voice limit and steal rules, voice reuse, on the null output.</summary>
public class SoundTests
{
    private static AudioEngine CreateHeadless() => AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true });

    private static float[] Tone(int frames, float level = 0.2f)
    {
        var s = new float[frames];
        Array.Fill(s, level);
        return s;
    }

    [Fact]
    public void LoadSound_FromWav_ReportsItsFormat()
    {
        using var engine = AudioEngine.Create();
        using var sound = engine.LoadSound(TestAudio.SineWav(0.5, 22050));
        Assert.Equal(22050, sound.Info.SampleRate);
        Assert.Equal(11025, sound.Info.Frames);
        Assert.Equal(11025L * sound.Info.Channels * 4, sound.Info.MemoryBytes);
        Assert.Equal(TimeSpan.FromSeconds(0.5), sound.Info.Duration);
        Assert.Equal(8, sound.MaxVoices);
        Assert.Equal(SoundSteal.Oldest, sound.Steal);
        Assert.Equal(0, sound.VoiceCount);
    }

    [Fact]
    public void Play_OverlappingCopies_EachHaveTheirOwnVolumeAndPan()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        using var sound = engine.CreateSound(Tone(48000), 48000, 1);
        var a = sound.Play(output, 1.0f, -0.5f);
        var b = sound.Play(output, 0.25f, 0.5f);
        Assert.NotSame(a, b);
        Assert.Equal(1.0f, a.Volume);
        Assert.Equal(0.25f, b.Volume);
        Assert.Equal(-0.5f, a.Pan);
        Assert.Equal(0.5f, b.Pan);
        Assert.Equal(2, sound.VoiceCount);
        Assert.Equal(2, sound.PlayingCount);
        var playing = sound.GetPlayingVoices();
        Assert.Equal(2, playing.Count);
        Assert.Same(a, playing[0]);   // oldest first
        Assert.Same(b, playing[1]);
        sound.Stop();
        Assert.Equal(0, sound.PlayingCount);
        Assert.Equal(StreamState.Stopped, a.State);
    }

    [Fact]
    public void Play_AtTheLimit_StealsByTheRule()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        using var sound = engine.CreateSound(Tone(96000), 48000, 1, new SoundOptions { MaxVoices = 2 });
        var a = sound.Play(output);
        var b = sound.Play(output, 0.1f);
        Assert.Same(a, sound.Play(output));            // the oldest
        sound.Steal = SoundSteal.Quietest;
        Assert.Same(b, sound.Play(output, 0.5f));      // the quietest (0.1)
        sound.Steal = SoundSteal.None;
        var e = Assert.Throws<FatzAudioException>(() => sound.Play(output));
        Assert.Equal(ErrorCode.Busy, e.ErrorCode);
        a.Stop();
        Assert.Same(a, sound.Play(output));            // a stopped voice is free again
    }

    [Fact]
    public void MaxVoices_Lowered_RetiresVoices()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        using var sound = engine.CreateSound(Tone(96000), 48000, 1);
        var first = sound.Play(output);
        for (int i = 0; i < 3; i++) sound.Play(output);
        Assert.Equal(4, sound.VoiceCount);
        sound.MaxVoices = 1;
        Assert.Equal(StreamState.Stopped, first.State);   // retired: stopped, still usable
        Assert.Equal(1, sound.MaxVoices);
        Assert.Equal(1, sound.VoiceCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => sound.MaxVoices = 0);
    }

    [Fact]
    public void DisposedVoice_IsReplaced_AndDisposeClosesEverything()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        var sound = engine.CreateSound(Tone(4800), 48000, 1);
        var a = sound.Play(output);
        a.Dispose();
        Assert.Equal(0, sound.VoiceCount);
        var b = sound.Play(output);
        Assert.NotSame(a, b);
        sound.Dispose();
        Assert.Throws<ObjectDisposedException>(() => b.Volume);
        Assert.Throws<ObjectDisposedException>(() => sound.Play(output));
    }

    [Fact]
    public async Task EndedVoice_IsReused()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        using var sound = engine.CreateSound(Tone(480), 48000, 1);   // 10 ms
        var a = sound.Play(output);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (a.State != StreamState.Ended && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(StreamState.Ended, a.State);
        Assert.Same(a, sound.Play(output, 0.5f));
        Assert.Equal(0.5f, a.Volume);
        Assert.Equal(1, sound.VoiceCount);
    }

    // Final audit T2: Dispose closed the voices under the sound's lock while an Ended handler waited for that lock in
    // Play, and closing a voice waits for its handlers: a deadlock.
    [Fact]
    public void Dispose_WhileEndedHandlersPlay_DoesNotDeadlock()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        for (int round = 0; round < 20; round++)
        {
            var sound = engine.CreateSound(Tone(96), 48000, 1, new SoundOptions { MaxVoices = 4 });   // 2 ms
            int ended = 0;
            void Again(object? sender, EventArgs e)
            {
                Interlocked.Increment(ref ended);
                try
                {
                    var v = sound.Play(output);
                    v.Ended -= Again;
                    v.Ended += Again;
                }
                catch (ObjectDisposedException) { }
                catch (FatzAudioException) { }
            }
            for (int i = 0; i < 4; i++) sound.Play(output).Ended += Again;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref ended) < 8 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            var dispose = Task.Run(sound.Dispose);
            Assert.True(dispose.Wait(TimeSpan.FromSeconds(10)), $"Dispose hung in round {round}");
        }
    }

    // Final audit T1: disposing voices on one thread while another plays the sound. A voice whose close had begun
    // could be handed out again and wrapped a second time, so it was closed twice.
    [Fact]
    public void DisposingVoices_WhilePlaying_IsSafe()
    {
        using var engine = CreateHeadless();
        using var output = engine.OpenOutput();
        using var sound = engine.CreateSound(Tone(4800), 48000, 1, new SoundOptions { MaxVoices = 2 });
        var played = new System.Collections.Concurrent.BlockingCollection<AudioStream>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        var player = Task.Run(() =>
        {
            while (DateTime.UtcNow < until) played.Add(sound.Play(output));
            played.CompleteAdding();
        });
        int disposed = 0;
        foreach (var v in played.GetConsumingEnumerable())
        {
            v.Dispose();
            disposed++;
        }
        player.Wait();
        Assert.True(disposed > 0);
        Assert.InRange(sound.VoiceCount, 0, 2);
        Assert.Equal(1.0f, sound.Play(output).Volume);
    }

    [Fact]
    public void CreateSound_RejectsBadInput()
    {
        using var engine = AudioEngine.Create();
        Assert.Throws<ArgumentException>(() => engine.CreateSound(new float[3], 48000, 2));
        Assert.Throws<ArgumentException>(() => engine.CreateSound(ReadOnlySpan<float>.Empty, 48000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.CreateSound(new float[4], 48000, 1, new SoundOptions { MaxVoices = 0 }));
        var e = Assert.Throws<FatzAudioException>(() => engine.CreateSound(new float[4], 10, 1));
        Assert.Equal(ErrorCode.InvalidArgument, e.ErrorCode);
    }
}

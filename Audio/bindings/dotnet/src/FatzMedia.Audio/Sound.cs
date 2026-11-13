using FatzMedia.Audio.Native;

namespace FatzMedia.Audio;

/// <summary>Which voice a <see cref="Sound"/> takes over when all of them are busy.</summary>
public enum SoundSteal
{
    /// <summary>The voice that started longest ago restarts as the new copy (the default).</summary>
    Oldest = 0,
    /// <summary>The voice with the lowest volume right now; the older one on a tie.</summary>
    Quietest = 1,
    /// <summary>None: <see cref="Sound.Play(OutputDevice, float, float)"/> throws <see cref="AudioException"/>
    /// with <see cref="ErrorCode.Busy"/>.</summary>
    None = 2,
}

/// <summary>Options for <see cref="AudioEngine.LoadSound(string, SoundOptions?)"/> and the other sound loaders.</summary>
public sealed class SoundOptions
{
    /// <summary>Copies that can play at once, 1 to 1024. Default 8.</summary>
    public int MaxVoices { get; init; } = 8;

    /// <summary>What a play does when every voice is busy. Default <see cref="SoundSteal.Oldest"/>.</summary>
    public SoundSteal Steal { get; init; } = SoundSteal.Oldest;

    /// <summary>How the file is opened (audio track, soundfont, MIME hint); null = defaults. Flags such as
    /// <see cref="StreamOptions.Loop"/> are ignored.</summary>
    public StreamOptions? Open { get; init; }
}

/// <summary>Information about a <see cref="Sound"/>.</summary>
/// <param name="SampleRate">The sound's own rate (each voice converts to its device).</param>
/// <param name="Channels">Its channel count.</param>
/// <param name="Frames">Its length in frames.</param>
/// <param name="MemoryBytes">Bytes of PCM (32-bit float) held in memory, shared by all voices.</param>
public sealed record SoundInfo(int SampleRate, int Channels, long Frames, long MemoryBytes)
{
    /// <summary>The length as time.</summary>
    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)Frames / SampleRate) : TimeSpan.Zero;
}

/// <summary>
/// A short sound decoded once into memory and played as many overlapping copies (voices) as you like, each with
/// its own volume and pan: jingles, applause, air horns, sound-effect pads. All voices read the one
/// copy of the audio.
/// </summary>
/// <remarks>
/// Each voice is an ordinary <see cref="AudioStream"/>, owned by the sound: stop it, change its volume, pan or
/// rate, ramp it, put effects on it or listen for <see cref="AudioStream.Ended"/>. A voice that ended or was
/// stopped is reused by a later <see cref="Play(OutputDevice, float, float)"/>, so the same object may by then be
/// playing a newer copy; a reused voice starts at its play's volume and pan with looping off. Disposing a voice is
/// allowed (the sound makes a new one when it needs it). At most <see cref="MaxVoices"/> copies play at once; a
/// play beyond that takes a voice over by <see cref="Steal"/> (it restarts at once, without a fade).
/// Disposing the sound stops and disposes its voices. All members are thread-safe.
/// </remarks>
/// <example>
/// <code>
/// using var horn = engine.LoadSound("horn.wav");
/// horn.Play(output);                       // fire and forget
/// horn.Play(output, volume: 0.5f, pan: -0.8f);   // a second copy over it, to the left
/// </code>
/// </example>
public sealed unsafe class Sound : IDisposable
{
    readonly SoundHandle _handle;
    readonly Dictionary<nint, AudioStream> _voices = [];   // also the lock of the members below
    // Voices whose wrapper is being disposed: the native close has not finished, and until it has started the
    // native pool may still hand the voice out. Such a voice is never wrapped again (it is about to be freed).
    readonly HashSet<nint> _closing = [];
    bool _disposed;   // no new voices once Dispose has taken the list

    Sound(AudioEngine engine, SoundHandle handle)
    {
        Engine = engine;
        _handle = handle;
        var n = ReadInfo();
        Info = new SoundInfo((int)n.SampleRate, (int)n.Channels, n.Frames, (long)n.MemoryBytes);
    }

    internal static Sound Wrap(AudioEngine engine, int result, nint native)
    {
        AudioException.ThrowIfFailed(result);
        var h = new SoundHandle();
        h.Initialize(native, engine.Handle);
        return new Sound(engine, h);
    }

    /// <summary>The native <c>fa_sound_options</c> for a call (Open is filled in by the caller).</summary>
    internal static FaSoundOptions ToNative(SoundOptions? options)
    {
        FaSoundOptions o;
        NativeMethods.fa_sound_options_init(&o);
        if (options is not null)
        {
            if (options.MaxVoices is < 1 or > 1024)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxVoices must be 1 to 1024.");
            o.MaxVoices = (uint)options.MaxVoices;
            o.Steal = (int)options.Steal;
        }
        return o;
    }

    SoundHandle Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle;
        }
    }

    /// <summary>The engine the sound belongs to.</summary>
    public AudioEngine Engine { get; }

    /// <summary>The sound's format, length and memory use.</summary>
    public SoundInfo Info { get; }

    FaSoundInfo ReadInfo()
    {
        var n = NativeSized.New<FaSoundInfo>();
        AudioException.ThrowIfFailed(NativeMethods.fa_sound_get_info(Handle, &n));
        return n;
    }

    /// <summary>Copies that can play at once, 1 to 1024. Lowering it retires the voices over the limit (free ones
    /// first, then the oldest playing): they stop and the sound no longer uses them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 1024.</exception>
    public int MaxVoices
    {
        get => (int)ReadInfo().MaxVoices;
        set
        {
            if (value is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(value), "MaxVoices must be 1 to 1024.");
            SetOptions((uint)value, null);
        }
    }

    /// <summary>What a play does when every voice is busy.</summary>
    public SoundSteal Steal
    {
        get => (SoundSteal)ReadInfo().Steal;
        set => SetOptions(null, value);
    }

    void SetOptions(uint? maxVoices, SoundSteal? steal)
    {
        lock (_voices)
        {
            var n = ReadInfo();
            FaSoundOptions o;
            NativeMethods.fa_sound_options_init(&o);
            o.MaxVoices = maxVoices ?? n.MaxVoices;
            o.Steal = steal is { } s ? (int)s : n.Steal;
            AudioException.ThrowIfFailed(NativeMethods.fa_sound_set_options(Handle, &o));
        }
    }

    /// <summary>Voices made so far, playing or free.</summary>
    public int VoiceCount => (int)ReadInfo().Voices;

    /// <summary>Voices busy now: playing, paused or scheduled.</summary>
    public int PlayingCount => (int)ReadInfo().Playing;

    /// <summary>Plays a copy now on <paramref name="output"/>'s master mix (starting the device if needed). The
    /// first audio is buffered before the call returns.</summary>
    /// <param name="output">The device to play on.</param>
    /// <param name="volume">Linear gain, 0 or more (1 = as recorded).</param>
    /// <param name="pan">-1 (left) to 1 (right).</param>
    /// <returns>The voice playing the copy. The sound owns it: there is no need to dispose it.</returns>
    /// <exception cref="AudioException">Every voice is busy and <see cref="Steal"/> is <see cref="SoundSteal.None"/>
    /// (<see cref="ErrorCode.Busy"/>), or the device belongs to another engine (<see cref="ErrorCode.InvalidArgument"/>).</exception>
    public AudioStream Play(OutputDevice output, float volume = 1.0f, float pan = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(output);
        return PlayOn(output, null, volume, pan);
    }

    /// <summary>Plays a copy now through <paramref name="bus"/> (starting its device if needed).</summary>
    /// <param name="bus">The bus to play through.</param>
    /// <param name="volume">Linear gain, 0 or more.</param>
    /// <param name="pan">-1 (left) to 1 (right).</param>
    /// <returns>The voice playing the copy (owned by the sound).</returns>
    /// <exception cref="AudioException">See <see cref="Play(OutputDevice, float, float)"/>.</exception>
    public AudioStream Play(MixBus bus, float volume = 1.0f, float pan = 0.0f)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return PlayOn(bus.Device, bus, volume, pan);
    }

    AudioStream PlayOn(OutputDevice output, MixBus? bus, float volume, float pan)
    {
        if (!(volume >= 0)) throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be 0 or more.");
        if (float.IsNaN(pan)) throw new ArgumentOutOfRangeException(nameof(pan));
        var dh = output.Handle;
        var bh = bus?.Handle;
        output.Start();
        var spin = new SpinWait();
        var giveUp = Environment.TickCount64 + 1000;
        while (true)
        {
            lock (_voices)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var added = false;
                nint native;
                int rc;
                try
                {
                    bh?.DangerousAddRef(ref added);
                    rc = NativeMethods.fa_sound_play(Handle, dh, bh?.DangerousGetHandle() ?? 0, volume, pan, out native);
                }
                finally
                {
                    if (added) bh!.DangerousRelease();
                }
                AudioException.ThrowIfFailed(rc);
                if (!_closing.Contains(native))
                {
                    var voice = VoiceFor(native);
                    voice.JoinedVoice(output, bus);
                    return voice;
                }
            }
            // A voice being disposed on another thread, whose native close had not reached the pool yet: it is
            // closed in a moment (which cuts this copy off), so play again. The lock is let go meanwhile, so that
            // close can finish.
            if (Environment.TickCount64 > giveUp) throw new AudioException(ErrorCode.Busy);
            spin.SpinOnce();
        }
    }

    // The wrapper of a native voice: one per voice, made the first time it is seen (lock held).
    AudioStream VoiceFor(nint native)
    {
        if (_voices.TryGetValue(native, out var known) && !known.IsDisposed) return known;
        var h = new StreamHandle();
        h.Initialize(native, Engine.Handle);
        h.SetOwner(_handle);   // the sound stays open while a voice wrapper is alive
        h.SetVoiceOf(this);
        var voice = new AudioStream(Engine, h, null);
        _voices[native] = voice;
        return voice;
    }

    /// <summary>Stops every copy.</summary>
    public void Stop() => AudioException.ThrowIfFailed(NativeMethods.fa_sound_stop(Handle));

    /// <summary>The voices busy now, oldest first.</summary>
    /// <returns>A snapshot; voices may end at any time after.</returns>
    public IReadOnlyList<AudioStream> GetPlayingVoices()
    {
        lock (_voices)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var h = Handle;
            AudioException.ThrowIfFailed(NativeMethods.fa_sound_get_voices(h, null, 0, out var count));
            if (count == 0) return [];
            var buf = new nint[count];
            fixed (nint* p = buf)
                AudioException.ThrowIfFailed(NativeMethods.fa_sound_get_voices(h, p, count, out count));
            var n = (int)Math.Min(count, (uint)buf.Length);   // fewer if some ended between the calls
            var list = new List<AudioStream>(n);
            for (var i = 0; i < n; i++)
                if (!_closing.Contains(buf[i])) list.Add(VoiceFor(buf[i]));   // not one being disposed
            return list;
        }
    }

    // A voice wrapper's handle is being disposed (StreamHandle.Dispose): see _closing.
    internal void VoiceClosing(nint native)
    {
        lock (_voices) _closing.Add(native);
    }

    // Its native close is over: the pointer may now name a new voice.
    internal void VoiceClosed(nint native)
    {
        lock (_voices)
        {
            _closing.Remove(native);
            if (_voices.TryGetValue(native, out var v) && v.IsDisposed) _voices.Remove(native);
        }
    }

    /// <summary>Stops and disposes every voice, then frees the sound.</summary>
    public void Dispose()
    {
        AudioStream[] voices;
        lock (_voices)
        {
            _disposed = true;   // a Play racing this (an Ended handler, say) must not add a voice nobody disposes
            voices = [.. _voices.Values];
            _voices.Clear();
        }
        // Outside the lock: closing a voice waits for its event handlers, and an Ended handler may be in Play.
        foreach (var v in voices) v.Dispose();
        _handle.Dispose();
    }
}

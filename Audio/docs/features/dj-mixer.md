# DJ mixer

A **DJ mixer** puts two decks on one output device, the way a two-channel DJ mixer does: each deck has a channel fader, a trim, a three-band isolator EQ with kills and a meter; a crossfader sits between them; then a master gain and the output limiter, which is always on. A headphone **cue** lets you hear a deck before the crowd does.

```
 deck: your stream (decode, tempo/pitch, its own EQ, volume, fades)
   -> trim -> isolator (low / mid / high) -+-> cue send (pre-fader)
                                           '-> fader -> meter -> crossfader side
 decks -> master gain -> limiter -> master output (channels 1-2)
 cue = (1 - cue mix) x cue sends + cue mix x master, x cue gain -> headphones
```

Decks hold ordinary streams. Play, pause, seek, tempo, pitch, markers, CD+G and lyrics work on them as on any stream, and both decks share the master device's clock, so `fa_stream_play_at` on two decks starts them on the same frame. The mixer opens and owns its output device: use `fa_mixer_get_device` for the clock, levels, spectrum and recording of the master mix.

## C

```c
fa_mixer_config cfg;
fa_mixer_config_init(&cfg);           /* 2 decks, default device, low latency, no cue */
cfg.cue_mode = FA_CUE_CHANNELS;       /* headphones on outputs 3-4 of a 4-output interface */

fa_mixer* mixer;
if (fa_mixer_create(engine, &cfg, &mixer) == FA_ERROR_NOT_ENOUGH_CHANNELS)
    cfg.cue_mode = FA_CUE_SPLIT, fa_mixer_create(engine, &cfg, &mixer);   /* Y-cable */

fa_deck *a, *b;
fa_mixer_get_deck(mixer, 0, &a);      /* side A of the crossfader */
fa_mixer_get_deck(mixer, 1, &b);      /* side B */

fa_deck_load(a, song_a, 0);           /* the deck doesn't own the stream */
fa_deck_load(b, song_b, 0);
fa_stream_play(song_a);

/* Pre-listen deck B in the headphones, bass cut, then bring it in. */
fa_deck_set_param(b, FA_DECK_CUE, 1);
fa_deck_set_param(b, FA_DECK_EQ_LOW_DB, FA_DECK_EQ_KILL_DB);
fa_stream_play(song_b);
fa_ramp shape = { FA_CURVE_LINEAR };
fa_mixer_ramp_param(mixer, FA_MIX_CROSSFADER, 1.0f, 8000000000LL, 0, &shape);   /* 8 s to B */
fa_deck_ramp_param(b, FA_DECK_EQ_LOW_DB, 0.0f, 8000000000LL, 0, &shape);         /* bass back in */

/* A four-beat loop at 128 BPM from the current position. */
fa_frames pos; fa_stream_get_position(song_b, &pos);
fa_frames beat = 44100 * 60 / 128;    /* the stream's sample rate (fa_stream_get_info) */
fa_deck_set_loop(b, pos, pos + 4 * beat);
fa_deck_set_loop(b, 0, 0);            /* off: plays on from where it is */

fa_mixer_destroy(mixer);              /* unloads the decks; close the streams yourself */
```

## C#

```csharp
using var mixer = engine.CreateDjMixer(new DjMixerOptions { Cue = CueOutput.Channels34 });
DjDeck a = mixer.Decks[0], b = mixer.Decks[1];

a.Load(songA);
b.Load(songB);
songA.Play();

b.Cue = true;                         // pre-listen B
b.Eq.Low = DeckEq.Kill;
songB.Play();
mixer.Crossfader.Ramp(1f, TimeSpan.FromSeconds(8));   // Crossfader is a DjCrossfader; Crossfader.Curve takes a CrossfaderCurve
b.Ramp(DeckParameter.EqLowDb, 0f, TimeSpan.FromSeconds(8));

b.CuePoint = songB.PositionFrames;    // CDJ cue: ReturnToCuePoint() pauses and goes back
long beat = songB.Info.Format.SampleRate * 60 / 128;
b.Loop = new DeckLoop(songB.PositionFrames, songB.PositionFrames + 4 * beat);
b.Loop = null;                        // off

Span<float> peak = stackalloc float[2], rms = stackalloc float[2];
a.GetLevels(peak, rms);               // the channel meter, per frame without allocating (GetLevels() allocates)
```

`DjMixer.Dispose` unloads the decks (even playing ones) and closes the output; the streams stay open for you to dispose. A stream that is disposed, or played on another device or bus, leaves its deck by itself.

## Cue (headphones)

| Mode | C | .NET | What you get |
|---|---|---|---|
| None | `FA_CUE_NONE` | `CueOutput.None` | Master only |
| Channels 3-4 | `FA_CUE_CHANNELS` | `CueOutput.Channels34`, `CueOutput.Channels(n)` | Master on outputs 1-2, cue on two more outputs of the same device (DJ controllers, 4-output USB interfaces). Same clock: no drift, no delay. `cue_channel` picks the first cue output (counted from 0, default 2). `FA_ERROR_NOT_ENOUGH_CHANNELS` when the device has too few |
| Split | `FA_CUE_SPLIT` | `CueOutput.Split` | One stereo output and a Y-cable: master in mono on the left, cue in mono on the right |
| Second device | `FA_CUE_DEVICE` | `CueOutput.Device(headphones)` | The cue on another output device, such as USB headphones or the laptop's own jack, chosen by `cue_device_id` (NULL = the default output). Its own clock, bridged and lined up with the master ([below](#second-device-cue)). Not on iOS |

The cue send is taken before the channel fader, so a deck with its fader down can still be heard in the headphones. `FA_MIX_CUE_MIX` blends from cue only (0, the default) to master only (1), and `FA_MIX_CUE_GAIN` sets the headphone level. The master part of the blend is taken after the limiter, as the crowd hears it.

### Second-device cue

```c
fa_mixer_config c;
fa_mixer_config_init(&c);
c.cue_mode = FA_CUE_DEVICE;
c.cue_device_id = headphones->id;              /* NULL = the default output */
fa_mixer* mixer;
fa_mixer_create(engine, &c, &mixer);
fa_mixer_set_param(mixer, FA_MIX_CUE_TRIM_MS, 8.0f);   /* the headphones sounded early */
fa_mixer_latency l = { sizeof l };
fa_mixer_get_latency(mixer, &l);               /* l.cue_align, l.cue_alignment */
```

```csharp
var headphones = engine.OutputDevices.First(d => d.Name.Contains("USB"));
using var mixer = engine.CreateDjMixer(new DjMixerOptions { Cue = CueOutput.Device(headphones) });
mixer.CueTrim = TimeSpan.FromMilliseconds(8);
Console.WriteLine($"{mixer.Latency.CueAligned}: {mixer.Latency.CueAlignment.TotalMilliseconds:F1} ms");
mixer.CueLost += (_, _) => Console.WriteLine("headphones gone; call SetCueDevice when they are back");
```

- **Drift.** The headphones run on their own crystal. The cue reaches them through a small buffer that a resampler keeps at a steady fill, moving its rate by at most 0.05% (about 1 cent, never heard), whatever the drift between the two devices or their rates (a 48 kHz master and 44.1 kHz headphones). The buffer holds one master period, one headphone period, the resampler's look-ahead and a millisecond; a gap in the headphones raises it by 2 ms (up to 50 ms), so a system with uneven callbacks settles at what it needs. The mic inputs use the same bridge.
- **Lining up.** The two outputs have different latencies, so the master heard in the headphones (`FA_MIX_CUE_MIX`) would come early or late against the speakers. The mixer delays the faster output by the difference (at most 500 ms): `fa_mixer_latency.cue_align` (`Latency.CueAligned`) says which (`FA_CUE_ALIGN_CUE` the headphones, `FA_CUE_ALIGN_MASTER` the master) and `cue_alignment` by how much. Within 1 ms nothing is delayed (`FA_CUE_ALIGN_NONE`). `FA_MIXER_CUE_LOWEST_LATENCY` (`CueOutput.Device(lineUp: false)`) delays neither (`FA_CUE_ALIGN_OFF`), for the lowest latency on both.
- **Monitored mics come first.** While any mic is monitored (`FA_MIC_MONITOR`), the master is never delayed: a singer must not hear their voice later. When the master is the faster output, alignment is skipped (`FA_CUE_ALIGN_SKIPPED`; the headphones are `cue` − `master` late), and a delay already on the master comes off the moment a mic is monitored. Delaying the headphones goes on as usual.
- **Heard positions.** A delay on the master is part of what the crowd hears, so it is part of `fa_stream_get_clock` (`Clock`), and with it CD+G and lyric surfaces and video that follows the clock, deck meters, `fa_device_get_latency` and `mic_monitor`. `fa_stream_get_position`, which counts frames handed to the device, has no output latency in it.
- **Trim.** The latencies the system reports are not exact. `FA_MIX_CUE_TRIM_MS` (`CueTrim`, −250..250 ms) is added to the master's: raise it when the headphones sound early against the speakers, lower it when late. It takes effect at once and can't be ramped.
- **Changes.** A change of alignment (a trim, a mic turned to monitored, other headphones) makes a short gap in the output it delays (5 ms fades on the master); changes under 5 ms in the headphones glide in.
- **Mode.** The headphones open in the mixer's latency mode (low when it is exclusive, which a second device can't share) and move with `fa_mixer_set_latency_mode` (`fa_mixer_get_latency_mode`, `LatencyMode`).
- **Lost headphones.** Headphones that go away go silent and raise `FA_EVENT_CUE_LOST` (`CueLost`) once; the master plays on untouched, keeping any delay. `fa_mixer_set_cue_device` (`SetCueDevice`) opens the same or other headphones (lined up anew). It is `FA_ERROR_INVALID_ARG` on a mixer made without `FA_CUE_DEVICE`.
- **Systems.** Windows, macOS, Linux and Android (where the system may route both outputs to one place). iOS is `FA_ERROR_NOT_IMPLEMENTED`: use `FA_CUE_SPLIT`.

## Parameters

| Parameter | Range | Default |
|---|---|---|
| `FA_MIX_CROSSFADER` / `Crossfader.Position` | 0 (A) .. 1 (B) | 0.5 |
| `FA_MIX_MASTER_GAIN` / `MasterGain` | 0..4, linear, before the limiter | 1 |
| `FA_MIX_CUE_MIX` / `CueMix` | 0 (cue) .. 1 (master) | 0 |
| `FA_MIX_CUE_GAIN` / `CueGain` | 0..4, linear | 1 |
| `FA_MIX_CUE_TRIM_MS` / `CueTrim` | −250..250 ms, [second-device cue](#second-device-cue) only, not rampable | 0 |
| `fa_mixer_set_crossfader_curve` / `fa_mixer_get_crossfader_curve`, `Crossfader.Curve` | constant power (each side −3 dB in the middle, steady loudness) or linear (−6 dB, a slight dip); a change mid-set glides over 5 ms | constant power |
| `FA_DECK_GAIN` / `Gain` | 0..1, the channel fader | 1 |
| `FA_DECK_TRIM_DB` / `TrimDb` | −12..+12 dB, on top of loudness normalization | 0 |
| `FA_DECK_EQ_LOW_DB`, `_MID_DB`, `_HIGH_DB` / `Eq.Low`, `Eq.Mid`, `Eq.High` | `FA_DECK_EQ_KILL_DB` (−60, silent) .. +6 dB | 0 |
| `FA_DECK_XFADER_SIDE` / `Side` | thru (0), A (1), B (2) | deck 1 A, deck 2 B |
| `FA_DECK_CUE` / `Cue` | off / on | off |
| `FA_DECK_SYNC_RANGE` / `SyncRange` | 0.01..1: the most [sync](#sync-and-beat-loops) moves the deck's tempo away from 1, not rampable | 0.16 (±16%) |

Every parameter has a getter: `fa_mixer_get_param`, `fa_deck_get_param` and `fa_mic_get_param` return the current value. `fa_mixer_get_output_channels` gives the device's output channel count and the first cue channel (-1 when there are no cue channels; .NET `OutputChannels`, `CueChannel`). `fa_mic_get_index` gives a mic's slot, 0 or 1, as used in `fa_mixer_latency.mic_*` and `fa_mixer_event.index` (.NET `Mic.Index`).

## Rules

- Sets glide over 5 ms; ramps take any curve and an optional start time on the device clock, like stream ramps ([ramps](ramps.md)). Out-of-range values are clamped. Side and cue switch at once and can't be ramped. Mixer and deck ramps raise no events.
- The isolator splits the deck at 250 Hz and 2.5 kHz with fourth-order Linkwitz-Riley crossovers, so with every band at 0 dB the bands add back to the original exactly in level (the phase turns gently around the crossovers, as on hardware isolators). It runs always, so moving a knob never clicks.
- `fa_deck_load` attaches the stream to the mixer's device through the deck, from wherever it was, keeping its position. Loading over a stream that is playing and heard (fader up and the crossfader not closed on its side) is `FA_ERROR_BUSY` unless `FA_DECK_LOAD_FORCE` (`force: true`). Loading a stream that isn't playing also shortens its decode-ahead to 60 ms, so tempo and pitch moves are heard quickly.
- A deck holds one stream. `fa_crossfade` or `fa_stream_set_next` from a deck's stream to a stream that isn't attached yet is `FA_ERROR_INVALID_ARG`: load the next song on the other deck and use the crossfader. A crossfade between the streams of two decks works.
- A deck's stream may be closed (disposed) from any thread, also while it is loaded: it leaves its deck first, under the mixer's lock, so a deck call on another thread (or the engine's event thread driving [sync](#sync-and-beat-loops) and [auto-mix](#auto-mix)) finishes before it goes.
- The device mute (`fa_device_set_mute` / `fa_device_get_mute`, `Output.Muted`) silences the headphones too.
- The cue point (`fa_deck_set_cue_point` / `fa_deck_get_cue_point`, `CuePoint`) defaults to the stream's CUE_IN marker, else 0. `fa_deck_cue` (`ReturnToCuePoint`) pauses the deck and goes back to it.
- The meter (`fa_deck_get_levels`) shows the deck after trim, EQ and fader, before the crossfader, as heard now.
- `latency` defaults to `FA_LATENCY_LOW`: on Windows the WASAPI low-latency shared mode at the device's own rate (needs a driver that offers small periods, most do), about 5 ms periods elsewhere. `FA_LATENCY_NORMAL` uses the engine's period. `FA_LATENCY_EXCLUSIVE` (opt-in) is Windows WASAPI exclusive with 3 ms periods for the output and the mics: other apps can't use those devices meanwhile and the Windows volume no longer applies (the mixer's gains and `fa_device_set_volume` still do); `FA_ERROR_NOT_IMPLEMENTED` on other systems, `FA_ERROR_DEVICE` when another app holds the device. `fa_mixer_set_latency_mode` (`LatencyMode`) moves a running mixer to another mode: the devices close and open again (a moment of silence) and playback carries on. When the new mode can't be had the mixer stays in its own and returns that error; a mic input that won't open in the new mode raises `FA_EVENT_INPUT_LOST`.
- Closing the mixer's device, or the engine, destroys the mixer. In .NET, `DjMixer` keeps its `OutputDevice`, so dispose the mixer, not its `Output`.

## Deck loops

`fa_deck_set_loop(deck, in, out)` (`Loop`) loops between two source frames, at any time, also while the deck plays. The loop is made where the deck is decoded, so it is seamless (no gap, no fade), follows tempo and pitch, raises `FA_EVENT_LOOPED` (`Looped`) at each turn, and the position and clock report it as heard.

- Setting an out point the deck has only just passed (up to 0.5 s ago, as a "loop out" button does) loops back at once and keeps the loop's phase. An out point further behind jumps to the in point.
- `out <= in` turns the loop off; playback carries on from where it is. A seek past the out point also leaves the loop; a seek inside it keeps it.
- While on, the deck loop replaces the CUE_IN..CUE_OUT loop of `fa_stream_set_loop`. Unloading the deck clears it.
- A deck loop may lie outside the cue markers: the deck plays on past CUE_OUT (reversed: down past CUE_IN) to reach it, also when it had already decoded up to that marker and was about to end there.
- Only seekable streams loop (`FA_ERROR_NOT_SEEKABLE`): files, memory and discs, not web radio.
- If the source fails to seek while a loop is set or moved (an I/O error), the call returns that error and `fa_deck_get_loop` still reports the old loop. As after any failed seek, the deck stops once it has played what it already decoded.

## Microphones

Up to two karaoke mics join the mixer's master before the master gain and the limiter, each with a low cut, gain, gate, EQ, mute, pan and sends to a shared reverb and echo. They have their own page: [Microphones](microphones.md), which also covers [ducking](microphones.md#ducking) the music under the singer, [recording the vocals](microphones.md#recording), the [latency report](microphones.md#latency) and the measured [setup check](microphones.md#setup-check).

## Sync and beat loops

Sync needs a beat grid on both decks: one from [track analysis](analysis.md) set with `fa_stream_set_beat_grid` (`AudioStream.BeatGrid`), one the user corrected, or the exact grid of a MIDI file or tracker module (no analysis needed). Without one the calls return `FA_ERROR_NOT_FOUND`. A module's grid is a silent render of the whole song, worked out in the background from `fa_deck_load` on (a few hundred ms for a long module): until it is ready sync, beat loops and beat jumps on that deck are `FA_ERROR_BUSY` (try again a moment later) and `fa_deck_get_beat_info` is `FA_ERROR_NOT_FOUND`, so no deck call and no engine event waits for it. Beats are measured where the decks are heard (the presentation clock), so they line up in the room, including any delay on the master.

```c
fa_stream_set_beat_grid(song_a, &grid_a, NULL, 0);   /* from fa_analysis_get_beat_grid */
fa_stream_set_beat_grid(song_b, &grid_b, NULL, 0);
fa_stream_play(song_a);

/* B starts on A's next downbeat, at A's tempo, and stays locked to it. */
fa_deck_play_synced(b, a, FA_SYNC_TEMPO | FA_SYNC_BAR | FA_SYNC_LOCK);

fa_deck_set_loop_beats(b, 4);         /* a four-beat loop from the beat heard now */
fa_deck_set_loop_beats(b, 2);         /* halved: the same start */
fa_deck_set_loop_beats(b, 0);         /* off */
fa_deck_beat_jump(b, 16);             /* 16 beats on, at once, still on A's beat */
fa_deck_beat_jump(b, -4);             /* 4 back */

fa_deck_beat_info info = { sizeof(info) };
fa_deck_get_beat_info(b, &info);      /* bpm, beat, bar, phase_error_ms, locked */
fa_deck_sync(b, NULL, 0);             /* unlock: the tempo stays */
```

```csharp
songA.BeatGrid = (await engine.AnalyzeAsync(pathA)).Beats;
songB.BeatGrid = (await engine.AnalyzeAsync(pathB)).Beats;
songA.Play();
b.PlaySynced(a, SyncMode.Tempo | SyncMode.Bar | SyncMode.Lock);
b.LoopBeats(4);
b.BeatJump(16);                       // a loop that is on moves along
var info = b.BeatInfo;                // Bpm, Beat, Bar, PhaseErrorMs, Locked, Synced
b.Unsync();
```

- **Tempo** (`FA_SYNC_TEMPO`): the deck's `FA_PARAM_TEMPO` becomes the leader's heard BPM (its grid's BPM where it plays × its tempo × its rate) over the deck's grid BPM × its rate. The pitch stays (the stretcher). When that is more than `FA_DECK_SYNC_RANGE` from 1, half or double time is tried (a 170 BPM leader plays an 85 BPM deck at its own tempo); still out of reach is `FA_ERROR_OUT_OF_RANGE` (`ErrorCode.OutOfRange`) and nothing changes.
- **Phase** (`FA_SYNC_BEAT`, `FA_SYNC_BAR`): on a playing deck, an error under 30 ms is pulled in by bending the tempo by up to 2% for about two beats (not audible); a larger one is fixed by a jump on the deck's next beat, joined seamlessly like a loop. `FA_SYNC_BAR` lines up downbeats (it implies BEAT); bars are known for exact grids and for analysed grids flagged `FA_GRID_BARS` (found in the audio), others count beat 0 as a downbeat. Grids with a tempo map (a song that changes tempo or a drummer who drifts, from an analysis or a MIDI file) are followed: the heard BPM is the map's tempo where the deck is, and a locked deck's tempo follows the leader's map as it plays. On a deck that isn't playing, `fa_deck_sync` sets the tempo and the phase follows once it plays; `fa_deck_play_synced` starts it in phase instead.
- **`fa_deck_play_synced`** (`PlaySynced`) starts a stopped deck from its cue point (a paused one from where it is) so that its next beat (BAR: downbeat) lands exactly on one of the leader's, at least 50 ms ahead. It is scheduled on the device clock, so it is exact to the frame. The leader must be playing (`FA_ERROR_INVALID_ARG`); a deck that plays already is synced as `fa_deck_sync` does.
- **Lock** (`FA_SYNC_LOCK`): the engine's event thread looks about 20 times a second and keeps the deck on the leader's beat: it follows the leader's tempo moves and tempo-mapped songs, with a target error under 1 ms. The deck's `FA_PARAM_TEMPO` then moves by itself; set it by hand only after unlocking. Without LOCK a sync is done once (a phase pull ends within 1 ms, or after about eight beats).
- **Ending it**: `fa_deck_sync(deck, NULL, 0)` (`Unsync`), or loading a stream on either deck. The tempo stays where it is.
- **Beat loops** (`fa_deck_set_loop_beats`, `LoopBeats`): 1/16..32 beats on the grid, from the beat heard now (a loop shorter than a beat starts on the last whole part of the beat). While a deck loop is on the new one keeps its in point, so halving and doubling work like a DJ's loop buttons. On a deck playing reversed it is mirrored: the loop ends at the beat heard and lies below it, and halving and doubling keep its end. 0 turns the loop off. It is an ordinary [deck loop](#deck-loops), read back with `fa_deck_get_loop` (`Loop`) in frames.
- **Beat jumps** (`fa_deck_beat_jump`, `BeatJump`): moves the deck by whole grid beats (negative is back; 1/16..1024 either way, e.g. 1, 4, 16, 32) at once, joined seamlessly like a loop. The distance is counted on the grid from the beat heard now, so a jump of whole beats keeps the beat exactly where it was (also on a tempo map) and a synced deck stays on the leader's beat. While a deck loop is on the loop moves along by the same beats, as a CDJ's loop move does (kept inside the song). On a paused or stopped deck the position moves. A jump past the end ends the song; one before the start lands on frame 0. A locked deck is pulled back on its next beat unless the jump keeps what the lock aligns: whole beats with `FA_SYNC_BEAT | FA_SYNC_LOCK`, whole bars with `FA_SYNC_BAR | FA_SYNC_LOCK`. `FA_ERROR_BUSY` while sync's own jump or the previous jump still waits to be decoded (try again a moment later); `FA_ERROR_NOT_SEEKABLE` for live streams.
- **Reverse**: `fa_stream_set_reverse` on the deck's stream (`AudioStream.Reverse`) plays it backwards from where it is, at once (the audio just heard goes back out of the stream's buffer, so there is no gap), and switching it off plays on forward from where it got to. While reversed, everything works mirrored: sync pulls the deck onto the leader's beat with its beats counting down, deck loops turn at their in point, beat loops lie below the beat heard, and loop changes and beat jumps join without a gap. See [reverse playback](tempo-pitch.md#reverse-playback).
- **Beat info** (`fa_deck_get_beat_info`, `BeatInfo`): the heard BPM, the position in beats and bars, and while synced the phase error against the leader in ms (positive: this deck is ahead). `FA_ERROR_NOT_FOUND` (null in .NET) for an empty deck or one without a grid.

## Slip mode

Slip mode is a CDJ's SLIP button: while it is on, reversing, looping and beat jumping are excursions, and the track keeps running underneath. The deck counts where it would be playing from where it was heard when the excursion began, at the speed it plays (TEMPO × RATE, ramps and sync moves included), and when the excursion ends it joins there, 15 ms ahead of the mix and without a gap. It lands exactly on the beat it would have been on, so a synced deck is still on the leader's beat.

```c
fa_deck_set_slip(deck, 1);
fa_stream_set_reverse(song, 1);          /* a "censor" or a spinback ... */
fa_stream_set_reverse(song, 0);          /* ... and back to where the song got to */
fa_deck_set_loop_beats(deck, 1.0);       /* a one-beat roll ... */
fa_deck_set_loop_beats(deck, 0.0);       /* ... and out where the track would be */
fa_deck_beat_jump(deck, -4.0);           /* a jump back ... */
fa_deck_slip_return(deck);               /* ... and the button released */
int32_t on;
fa_deck_get_slip(deck, &on);                /* whether slip mode is on */
fa_frames shadow;
fa_deck_get_slip_position(deck, &shadow);   /* FA_FRAMES_UNSET while no slip runs */
```

```csharp
deck.Slip = true;
song.Reverse = true;
song.Reverse = false;
deck.LoopBeats(1);
deck.LoopBeats(0);
deck.BeatJump(-4);
deck.SlipReturn();
long? shadow = deck.SlipPosition;     // the "ghost" playhead for a waveform display
```

- **What starts one**: `fa_stream_set_reverse` on the deck's stream, a deck loop (`fa_deck_set_loop`, `fa_deck_set_loop_beats`) and `fa_deck_beat_jump`, while the deck plays. Further ones during a slip join it: the count goes on from the first start.
- **What ends one**: the excursions ending, that is the direction back to what it was with no deck loop on (the last of the two to end returns), or `fa_deck_slip_return` (`SlipReturn`), which also ends the reverse and the loop. A beat jump has no end of its own, so it needs `fa_deck_slip_return` (the moment the jump button is let go). Without a slip running, `fa_deck_slip_return` does nothing.
- **What forgets one**: a seek, a cue (`fa_deck_cue`), a stop, loading the deck, or turning slip mode off: playback then carries on from where it is. Pausing pauses the count, as it would have paused the track.
- The slip position wraps the stream's own `fa_stream_set_loop` loop like playback would, and stops at the song's end: returning past it ends the song.
- Sync's own phase jumps don't move the slip position; a locked deck pulls itself back onto the beat after the return anyway.

## Auto-mix

Auto-mix makes the two decks take turns: while one plays, the other starts at the playing deck's mix point and the crossfader moves across. Everything is scheduled on the device clock a couple of seconds ahead, so it is sample-accurate however busy the app's UI thread is. The app owns the playlist and loads the next song when asked.

```c
static void FA_CALL on_mixer(const fa_mixer_event* e, void* user)
{
    if (e->type == FA_EVENT_DECK_EMPTY) {          /* the idle deck needs the next song */
        fa_deck* d; fa_mixer_get_deck(e->mixer, e->index, &d);
        fa_stream* old; fa_deck_get_stream(d, &old);
        fa_deck_load(d, next_song(user), FA_DECK_LOAD_FORCE);
        if (old) fa_stream_close(old);
    }
}

fa_mixer_set_event_callback(mixer, on_mixer, playlist);
fa_automix_options o;
fa_automix_options_init(&o);          /* 8 s, match tempo, align beats */
o.length_beats = 32;                  /* when both grids are good */
o.flags |= FA_AUTOMIX_BASS_SWAP | FA_AUTOMIX_RESTORE_TEMPO;
fa_mixer_set_automix(mixer, &o);
fa_stream_play(first_song);           /* on either deck: auto-mix takes it from here */
fa_mixer_automix_now(mixer);          /* skip: the next transition now */
fa_mixer_set_automix(mixer, NULL);    /* off */
```

```csharp
mixer.AutoMix.DeckEmpty += (s, e) =>   // mixer.AutoMix is a DjAutoMix
{
    var old = e.Deck.Stream;
    e.Deck.Load(engine.OpenFile(queue.Dequeue()), force: true);
    old?.Dispose();
};
mixer.AutoMix.Start(new AutoMixOptions { LengthBeats = 32, BassSwap = true, RestoreTempo = true });
mixer.AutoMix.Skip();
```

- **The mix point** follows the rules of `fa_stream_set_next`: the outgoing song's `MIX_POINT` marker, else its `FADE_OUT_START`, else its end (`CUE_OUT`, or the length) minus the transition's length. The incoming deck starts at its cue point (`CUE_IN` unless set).
- **Beat-aligned** when `FA_AUTOMIX_ALIGN_BEATS` is set, both grids have at least `min_confidence` and the tempo is within reach: the incoming deck starts on its first downbeat at or after its cue point, on the outgoing deck's downbeat nearest the mix point, and the transition lasts whole bars. With `FA_AUTOMIX_MATCH_TEMPO` the incoming deck is first brought to the outgoing BPM (within `max_tempo_change`, half or double time allowed), and it stays locked to the outgoing deck until the transition is done. Without MATCH_TEMPO beats align only when the tempos already match within 0.2%.
- **Otherwise** (no grid, a low confidence, tempos too far apart) it is a plain timed crossfade over `length`: auto-mix never fails unattended.
- **The crossfader** is ramped over the transition toward the incoming deck's side, so it follows the crossfader curve. Decks on the same side, or thru, are started but not faded.
- **Bass swap** (`FA_AUTOMIX_BASS_SWAP`, `BassSwap`): two basslines at once sound muddy, so the incoming deck's low band is killed until the middle of the transition, and then the lows swap: the outgoing deck's low goes to `FA_DECK_EQ_KILL_DB` while the incoming one's comes back to its setting, over one beat. When beat-aligned the swap starts on the outgoing deck's beat nearest the middle; otherwise it lasts 0.5 s (a quarter of a transition shorter than 2 s). It is scheduled on the device clock like the crossfader. Auto-mix owns both decks' `FA_DECK_EQ_LOW_DB` (`Eq.Low`) during the transition; afterwards (or when it is cut short by a load or a stop) both go back to their settings from before. Only for an incoming deck that auto-mix starts itself.
- **Tempo glide back** (`FA_AUTOMIX_RESTORE_TEMPO`, `RestoreTempo`): when the transition is done, an incoming deck whose tempo `MATCH_TEMPO` moved glides back to the tempo it had before, over `restore_time` (`RestoreTime`, default 20 s, slow enough not to be noticed; 0 jumps back at once). It is a ramp of the stream's `FA_PARAM_TEMPO` that raises no `FA_EVENT_RAMP_DONE`; setting the tempo, a sync with `FA_SYNC_TEMPO` or a new stream takes over from it (a sync without TEMPO leaves the glide running). Without the flag the incoming deck keeps the tempo it was mixed in at.
- **When it is done** the outgoing deck is stopped.
- **Events** (mixer events, on the engine's event thread): `FA_EVENT_AUTOMIX_STARTED` when the incoming deck is heard and `FA_EVENT_AUTOMIX_DONE` when the crossfader is across (index and value: the incoming deck; `Started`, `Done`), and `FA_EVENT_DECK_EMPTY` (index: the idle deck; `DeckEmpty`) when a transition is done, and again 30 s before the next mix point while the idle deck still holds the song that was mixed out, or nothing. If nothing new is loaded, the outgoing song plays to its end.
- **Skip** (`fa_mixer_automix_now`, `Skip`): the next transition now (on the next bar when beat-aligned). `FA_ERROR_NOT_FOUND` when auto-mix is off, no deck plays, or the idle deck has nothing new; `FA_ERROR_BUSY` while a transition runs.
- Auto-mix needs the outgoing song's length (a file, memory or disc track, not a live web stream) unless it has a `MIX_POINT` or `FADE_OUT_START` marker. Turning it off (`NULL`, `Stop`) lets a transition already scheduled finish, without events.

| Option | Range | Default |
|---|---|---|
| `length` / `Length` | 0.5..60 s | 8 s |
| `length_beats` / `LengthBeats` | 0 (use `length`) or 1..256, when both grids are good | 0 |
| `flags` / `MatchTempo`, `AlignBeats`, `BassSwap`, `RestoreTempo` | `FA_AUTOMIX_MATCH_TEMPO`, `FA_AUTOMIX_ALIGN_BEATS`, `FA_AUTOMIX_BASS_SWAP`, `FA_AUTOMIX_RESTORE_TEMPO` | MATCH_TEMPO and ALIGN_BEATS |
| `max_tempo_change` / `MaxTempoChange` | 0..0.5 | 0.08 |
| `min_confidence` / `MinConfidence` | 0..1 | 0.5 |
| `restore_time` / `RestoreTime` | 0..300 s (RESTORE_TEMPO's glide) | 20 s |

## Possible future additions

None of these is built, and none is needed for a release. The API keeps struct sizes, enum values and flags open for them.

- **More than two decks.** `fa_mixer_create` returns `FA_ERROR_NOT_IMPLEMENTED` above 2 decks. Two decks cover the karaoke and DJ use this library is for.
- **Quantized seeks**, and **automatic key matching in an auto-mix** (the singer's key is the app's choice, so the app shifts pitch itself with `FA_PARAM_PITCH`).
- **More crossfader curves** (full-middle and cut). Today: constant power and linear.
- **Echo synced to the beat** (the mic echo time is set in milliseconds).
- **A per-deck filter knob** (the isolator EQ with kills covers most uses).
- **CD+G scroll shift in the frame** (`scroll_dx` / `scroll_dy`), if profiling ever asks for it.
- **Out of scope:** DVS, controller mapping, samplers and effect racks, scratching, streaming, ASIO, feedback suppression, stem separation, Ableton Link.

Done since the plan was written, so no longer future: tempo maps for songs that change tempo, bar detection and bar sync, beat jump, the auto-mix bass swap and restore-tempo glide, manual cue trim (`FA_MIX_CUE_TRIM_MS`), and separate recording stems (music and vocals).

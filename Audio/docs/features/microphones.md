# Microphones

Karaoke microphones live on the [DJ mixer](dj-mixer.md): `fa_mixer_add_mic` adds a mic to a mixer, and the mixer corrects the drift between the input and output devices and keeps the latency low. You do not need to use the decks to sing through it; a mixer with no deck loaded is a karaoke vocal chain. This page covers the mic channel, its effects, ducking the music under the singer, recording the vocals and the latency check.

Up to two karaoke mics (`FA_MIC_MAX`), mixed into the master before the master gain and the limiter, so the limiter catches a shout:

```
 input device -> bridge (drift-corrected) -> low cut -> gain -> gate -> EQ -> meter
   -> mute -> pan -+-> master (when monitored)
                   +-> reverb send -> shared reverb -+-> master
                   '-> echo send ---> shared echo ---'
 host mics -> ducker -> gain on the music (the decks)
 mics with their effects -> vocals recording tap
```

```c
fa_mic_config mc;
fa_mic_config_init(&mc);            /* the default input, all its channels summed, heard */
mc.device_id = info.id;             /* an FA_DEVICE_CAPTURE device from fa_device_get_info */
mc.channel = 0;                     /* input 1 of a 2-input interface; a second mic takes 1 */

fa_mic* mic;
fa_result r = fa_mixer_add_mic(mixer, &mc, &mic);   /* FA_ERROR_PERMISSION_DENIED: ask for it first */
fa_mic_set_param(mic, FA_MIC_GAIN_DB, 6);
fa_eq_band eq[10]; uint32_t n;
fa_eq_get_preset(FA_EQ_PRESET_LIVE_VOCAL, eq, &n);
fa_mic_set_eq(mic, eq, n);

float peak, rms;
fa_mic_get_levels(mic, 50000000, &peak, &rms);       /* the singer's meter */

fa_mixer_latency lat = { sizeof lat };
fa_mixer_get_latency(mixer, &lat);  /* lat.mic_monitor[0]: voice to speaker, in ns */
fa_mic_remove(mic);
```

```csharp
var input = engine.InputDevices.First(d => d.Name.Contains("Scarlett"));
using var mic = mixer.AddMic(new MicOptions { Device = input, Channel = 0 });
mic.GainDb = 6;
mic.SetEqualizer(EqualizerPreset.LiveVocal);
mixer.InputLost += (_, e) => e.Mic.Dispose();   // unplugged: add it again when it is back
MicLevels level = mic.GetLevels();
TimeSpan delay = mic.MonitorLatency;            // voice to speaker
```

| Setting | C | .NET | Range | Default |
|---|---|---|---|---|
| Gain | `FA_MIC_GAIN_DB` | `GainDb` | −20..+40 dB | 0 |
| Mute | `FA_MIC_MUTE` | `Muted` | off / on, glides over 5 ms | off |
| Pan | `FA_MIC_PAN` | `Pan` | −1 (left) .. 1 (right), balance | 0 |
| Gate | `FA_MIC_GATE_DB` | `GateDb` | −100 (off) .. 0 dB | −50 |
| Low cut | `FA_MIC_LOW_CUT` | `LowCut` | an 80 Hz high-pass, on / off | on |
| Monitor | `FA_MIC_MONITOR`, `fa_mic_config.monitor` | `Monitor`, `MicOptions.Monitor` | heard through the master, or metered only (a mic that goes through a hardware mixer) | heard |
| EQ | `fa_mic_set_eq` / `fa_mic_get_eq` | `SetEqualizer` / `GetEqualizer` | the stream EQ's bands and presets ([equalizer](equalizer.md)); `FA_EQ_PRESET_LIVE_VOCAL` is made for mics | off |
| Reverb send | `FA_MIC_REVERB_SEND` | `ReverbSend` | 0..1 into the shared reverb | 0.2 |
| Echo send | `FA_MIC_ECHO_SEND` | `EchoSend` | 0..1 into the shared echo | 0 |

- **Inputs.** A mic reads one channel of a capture device (`channel`, from 0) or all of them summed (−1, the default). Two mics on one 2-input interface share the device: it is opened once. A channel the device does not have is `FA_ERROR_NOT_ENOUGH_CHANNELS`; a third mic is `FA_ERROR_BUSY`.
- **Clocks.** The input runs on its own clock and often at its own rate (a USB mic at 44.1 kHz into a 48 kHz master). It reaches the master through a small buffer that a resampler keeps at a steady fill, adjusting the speed by at most 0.2% (not audible), so drift never builds up into a delay or a click. If the input stalls, the buffer grows by 2 ms (up to 50 ms) and starts again.
- **The gate** opens at the threshold within 1 ms, stays open while the voice is at most 6 dB below it, and closes over 150 ms after 100 ms below that, so breaths and room noise stay out between phrases without chopping words.
- **The meter** (`fa_mic_get_levels`, `GetLevels`) reads the mic after gain, gate and EQ, before mute and pan, so a muted singer still sees their level. The first call starts measuring and reads silence.
- **Fade-in.** A new mic is silent for 50 ms, then fades in over 10 ms, so adding one never pops.
- **Unprocessed capture.** Mics ask for the input without the system's voice processing (automatic gain, noise suppression, echo cancelling: made for calls, they spoil singing and add latency). That is WASAPI's raw mode on Windows (`AUDCLNT_STREAMOPTIONS_RAW`, where the driver offers it) and Android's UNPROCESSED input preset; macOS, iOS and Linux give the input as the system delivers it. `FA_MIC_OS_PROCESSING` (`KeepOsProcessing`) keeps the processing; two mics on one device share its input, so the first mic added decides for both.
- **Permission.** The app asks for the microphone permission before adding a mic: Android needs `RECORD_AUDIO` in the manifest and the runtime prompt; macOS and iOS need `NSMicrophoneUsageDescription` (and a sandboxed macOS app the audio-input entitlement). Where the system refuses at open, adding a mic is `FA_ERROR_PERMISSION_DENIED` (`ErrorCode.PermissionDenied`); some systems deliver silence instead.
- **Input lost.** An input that goes away (unplugged, taken by another app) goes silent, the mix carries on, and the mixer raises `FA_EVENT_INPUT_LOST` once per mic on `fa_mixer_set_event_callback` (.NET `mixer.InputLost`, whose `MicEventArgs.Mic` is the mic that lost its input), on the engine's event thread, once the input has been gone for 300 ms. A mic on the default input carries on when the system's default recording device changes (the backend stops it for a moment and moves it): that is not reported, and neither is a device the backend starts again within the 300 ms. A loss after one that came back is reported again. Remove the mic there or later and add it again when the device is back. `fa_mic_remove` works inside the callback, also for the event's own mic.
- **Host mics.** `FA_MIC_DUCKS_MUSIC` (`DucksMusic`) marks a host's mic: its voice ducks the music (below). Leave it off for singers.

## Reverb and echo

Both mics share one reverb and one echo, like the effects unit of a karaoke mixer; each mic sets how much it sends (`FA_MIC_REVERB_SEND`, `FA_MIC_ECHO_SEND`). Their returns join the mics in the master, in the middle. They take what is heard: a mic with monitor off sends nothing.

```c
fa_mic_effects fx;
fa_mic_effects_init(&fx);              /* the defaults below */
fx.reverb_size = 0.7f;
fx.echo_time_ms = 180;
fa_mixer_set_mic_effects(mixer, &fx);   /* fa_mixer_get_mic_effects reads them back */
fa_mic_set_param(mic, FA_MIC_ECHO_SEND, 0.3f);
```

```csharp
mixer.MicEffects = new MicEffects { ReverbSize = 0.7f, EchoTime = TimeSpan.FromMilliseconds(180) };
mic.EchoSend = 0.3f;
```

| Setting | C (`fa_mic_effects`) | .NET (`MicEffects`) | Range | Default |
|---|---|---|---|---|
| Reverb size | `reverb_size` | `ReverbSize` | 0 (small room) .. 1 (hall) | 0.5 |
| Reverb damping | `reverb_damping` | `ReverbDamping` | 0 (bright) .. 1 (dark) | 0.5 |
| Echo time | `echo_time_ms` | `EchoTime` | 20 ms .. 1 s | 250 ms |
| Echo feedback | `echo_feedback` | `EchoFeedback` | 0 .. 0.9, how much of each repeat comes back | 0.35 |
| Echo tone | `echo_tone` | `EchoTone` | 0 (dark) .. 1 (bright): a low-pass in the feedback from 1 to 16 kHz, so later repeats get duller | 0.6 |

The reverb is the one the MIDI synthesizer uses (eight combs and four allpasses per side, after the published Freeverb design), with Freeverb's size and damping ranges. An echo time change glides over about 50 ms, bending the repeats for a moment as a tape echo does, so it never clicks. Out-of-range values are clamped. The tails ring out after a mic is removed.

## Ducking

While a host mic speaks above the threshold, the music (the decks after the crossfader, and any other stream on the mixer's device) goes down by the depth; it comes back after the hold and the release. It is on with these defaults from the start, so marking a mic `FA_MIC_DUCKS_MUSIC` is enough; `fa_mixer_set_ducking(mixer, NULL)` (`Ducking = null`) turns it off. `fa_ducking_init` fills a `fa_ducking` with the defaults below; `fa_mixer_get_ducking` reads the setting back.

| Setting | C (`fa_ducking`) | .NET (`Ducking`) | Range | Default |
|---|---|---|---|---|
| Depth | `depth_db` | `DepthDb` | −40..0 dB (0 = off) | −12 dB |
| Attack | `attack` | `Attack` | 1 ms .. 1 s | 50 ms |
| Hold | `hold` | `Hold` | 0 .. 5 s | 300 ms |
| Release | `release` | `Release` | 10 ms .. 5 s | 600 ms |
| Threshold | `threshold_db` | `ThresholdDb` | −60..0 dB, the voice level that ducks | −40 dB |

The music is everything the decks and other streams on the mixer's device play; the mics and their effects are not ducked. The ducker follows the host's voice after the gate, so room noise below the gate never ducks. A muted host does not duck; a host with monitor off (heard through a hardware mixer) still does. Singers are never marked, so the music never dips under a singer.

## Recording

A mixer has two recording taps. Encoders attached to them record nothing until `fa_mixer_start_recording` starts them together on one device frame, so the files line up in any editor:

| Tap | C | .NET | What it records |
|---|---|---|---|
| Master | `FA_MIXER_TAP_MASTER` | `MixerTap.Master` | what the speakers play, after the limiter |
| Vocals | `FA_MIXER_TAP_VOCALS` | `MixerTap.Vocals` | both mics with their reverb and echo, before the master gain; a mic with monitor off is recorded dry |

```c
fa_encoder *master, *vocals;
fa_encoder_create_path("night.flac", &flac_options, &master);
fa_encoder_create_path("vocals.wav", NULL, &vocals);
fa_encoder_attach_mixer(master, mixer, FA_MIXER_TAP_MASTER);
fa_encoder_attach_mixer(vocals, mixer, FA_MIXER_TAP_VOCALS);
fa_encoder* both[] = { master, vocals };
fa_mixer_start_recording(mixer, both, 2, 0, 0);   /* 0 = with the next audio block */
/* ... the show ... */
fa_encoder_close(master);
fa_encoder_close(vocals);
```

```csharp
using var master = AudioEncoder.Create("night.flac", new EncoderOptions { Format = EncodeFormat.Flac });
using var vocals = AudioEncoder.Create("vocals.wav");
master.Record(mixer, MixerTap.Master);
vocals.Record(mixer, MixerTap.Vocals);
mixer.StartRecording([master, vocals]);
```

- **Lined up as the singer heard it.** A singer sings along with music that reached the speakers a little late, and the voice reaches the mixer a little late again. So the vocals file starts later than the master file by that round trip (the mic's input latency plus the master's output latency, the larger of two mics): put both files at 0 in an editor and the voice sits on the beat it was sung to. `FA_MIXER_REC_RAW_TIMING` (`rawTiming: true`) starts both with what the mixer made on the same frame instead (the voice as it reached the mixer). The round trip is the reported latency until the [setup check](#setup-check) has measured that mic in that mode; then it is the measured one.
- `device_time` (`startAt`) starts on a frame of the mixer device's clock (`fa_device_get_time`, `Output.Time`). 0, or a time less than one mixer block (2048 frames, about 43 ms at 48 kHz) ahead, starts one block ahead, so a block already in the audio thread never splits the stems.
- Formats, options and closing are those of every encoder ([recording and encoding](recording-encoding.md)). Destroying the mixer ends the recordings' input; close the encoders to complete the files.

## Latency

`fa_mixer_get_latency` (`Latency`, `Mic.InputLatency`, `Mic.MonitorLatency`) reports, in ns:

| Field | What it is |
|---|---|
| `master` | the master output's buffer plus the limiter's look-ahead (about 2 ms) and any alignment delay on it |
| `cue` | the headphones: the output's buffer (the cue is not limited, so it has no look-ahead); on a second device also the bridge and any alignment delay |
| `cue_alignment` | the delay added to line up a [second-device cue](dj-mixer.md#second-device-cue) |
| `cue_align` | which output `cue_alignment` delays: `FA_CUE_ALIGN_NONE`, `_CUE`, `_MASTER`, `_SKIPPED` (a mic is monitored) or `_OFF` |
| `mic_input[i]` | the input device's buffer plus the bridge's fill; −1 when slot `i` has no mic |
| `mic_monitor[i]` | voice to speaker: measured by the setup check, else `mic_input[i]` plus `master` (with any delay on it); −1 when no mic |

Times the system does not report are estimated from the buffer sizes. Below about 20 ms voice to speaker a singer does not notice; at 30 ms and more it sounds like a slap-back. With the low-latency default the bridge holds about one input period, one master period and a millisecond; after an underrun it holds 2 ms more (up to 50 ms in all), and gives a millisecond back after each 30 s without one. The [setup check](#setup-check) measures the real total; after it, `mic_monitor[i]` is the measured time for that mic in the mode measured, with the bridge as it holds now (`mic_input[i]` stays the reported one).

## Setup check

`fa_mixer_measure_latency` (`MeasureLatencyAsync`) is a test mode for the app's setup screen. With the mic where the singer will stand, it plays short test sounds through the speakers, listens on the mic, and reports for each latency mode it measured how long the mic takes to reach the speakers, and how much mic gain the room takes before it feeds back. Then it advises whether to monitor the mics, and in which mode.

```c
fa_latency_check_config cc;
fa_latency_check_config_init(&cc);        /* -12 dBFS at most, 25 ms still feels live */
cc.flags = FA_CHECK_ALL_MODES;            /* normal, low and (Windows) exclusive */
fa_latency_check res = { sizeof res };
fa_result r = fa_mixer_measure_latency(mixer, mic, &cc, &res);   /* blocks 3-6 s per mode */
if (r == FA_OK && res.advice == FA_CHECK_MONITOR) {
    fa_mixer_set_latency_mode(mixer, res.advised_mode);
    fa_mic_set_param(mic, FA_MIC_MONITOR, 1);
}
/* res.modes[i]: .result, .mic_to_speaker, .spread, .reported, .max_gain_db, .level_db */
```

```csharp
var check = await mixer.MeasureLatencyAsync(mic, new LatencyCheckOptions { AllModes = true });   // a LatencyCheck
if (check.Advice == CheckAdvice.Monitor)
{
    mixer.LatencyMode = check.AdvisedMode;
    mic.Monitor = true;
}
foreach (var m in check.Modes)
    Console.WriteLine($"{m.Mode}: {m.Error?.ToString() ?? $"{m.MicToSpeaker.TotalMilliseconds:F1} ms, gain up to {m.MaxGainDb:F0} dB"}");
```

- **What it plays.** The music fades out over 10 ms and the mics are silenced while the test sounds play, then everything fades back in. After a switch of mode it first waits until the input has run for half a second without a gap (at most 3 s), and a mode not heard right after a switch is tried once more. First a probe: three 5 ms chirps (1-6 kHz), 150 ms apart. It starts at −40 dBFS and gets 10 dB louder each time until the mic hears every chirp clearly (or 10 dB more no longer makes them clearer: a room's reflections set a limit, and the quieter level is kept), up to `max_level_db` (`MaxLevelDb`, −40..−6, default −12). Then at that level five chirps and 1.5 s of pink noise 10 dB below them. A mode takes 3-6 s.
- **The time.** `mic_to_speaker` (`MicToSpeaker`) is the median of the chirps' times, mic to speaker: the input, the bridge, the limiter, the output, and the air back to the mic (3 ms per metre). `spread` is how far the chirps differed (a steady system: well under 1 ms); `reported` is what the system's figures add up to, for comparison. The mixer uses the measured time for that mic and mode from then on (`fa_mixer_get_latency`, the vocals' recording alignment), until the mic is removed. While a check runs the bridge holds its speed, so its drift correction can't bend the time.
- **The gain.** From the pink noise, the loudest 1/24 octave of the loop speaker to mic between 100 Hz and 10 kHz (the room's worst resonance) gives `max_gain_db` (`MaxGainDb`): the most mic gain that stays 6 dB clear of feedback, with the master gain as it is now and the mic's EQ flat (an EQ boost takes that much off), −20..40.
- **The advice.** `FA_CHECK_MONITOR` (`Monitor`): monitor in `advised_mode`, the first of low, normal and exclusive that measured within `max_monitor` (`MaxMonitor`, default 25 ms), so shared modes come before exclusive, which takes the devices from other apps. `FA_CHECK_FEEDBACK`: that mode is fast enough, but the room feeds back below 0 dB of gain: move the mic away from the speakers or point it away. `FA_CHECK_SLOW`: every mode measured is slower than `max_monitor` (an audible slap-back), `advised_mode` is the fastest; leave the mics unmonitored or use a hardware mixer. `FA_CHECK_NOT_HEARD`: the mic didn't hear the speakers (is it on and unmuted, are the speakers up, is it a headset?).
- **Each mode.** In .NET each entry of `check.Modes` is a `LatencyCheckMode` record (`Mode`, `Error`, `MicToSpeaker`, `Spread`, `Reported`, `MaxGainDb`, `LevelDb`); `Error` is null when the mode was measured. In C, `modes[i].result` is `FA_OK`, `FA_ERROR_NOT_IMPLEMENTED` (exclusive off Windows), `FA_ERROR_DEVICE` (it would not open, or the output isn't running) or `FA_ERROR_NOT_FOUND` (not heard). `FA_CHECK_ALL_MODES` switches the mixer to each mode with `fa_mixer_set_latency_mode`, waits half a second for it to settle, measures, and returns to the mixer's own mode (if that fails, the mixer stays in the last mode measured and the call returns the error, with the measurements filled in). Without it only the mixer's own mode is measured, and a failure other than not heard is also the call's result.
- **Trying a rig.** `tools/mic-check/fa_mic_check` runs the check from a command line: `fa_mic_check --list`, then `fa_mic_check --mic Yeti --all` (`--out NAME` for the output, `--level DB`, `--repeat N`).
- **Rules.** The call blocks (in .NET it runs on a thread pool thread); call it from a thread of your own, not the event callback. A second check, or `fa_mixer_set_latency_mode`, while one runs is `FA_ERROR_BUSY`; a mic of another mixer is `FA_ERROR_INVALID_ARG`. Keep the mic until it returns. Exclusive mode ignores the Windows volume, so a level that was quiet in a shared mode can be much louder there.

# Configuration

<xref:FatzMedia.Video.Core.Configuration.MediaPlayerConfiguration> collects the settings that are fixed when a player is created: how video is decoded, which audio device is used, subtitle and lyrics styles, network behavior, HDR handling and what the player loads. This page lists every option with its type, default and meaning.

The configuration is an immutable value (`readonly record struct`). Build it with `new` and an object initializer, pass it to the player's constructor or `CreateAsync`, or to `Reset` to rebuild a running player. It cannot change while the player lives. For a different configuration, create another player or call `Reset(configuration, nativeLibraryPaths)`.

```csharp
var configuration = new MediaPlayerConfiguration
{
    VideoDecoding = VideoDecodingMethod.Auto,
    AudioOutputDeviceId = null,   // the system default
    NetworkRetry = new NetworkRetryPolicy { MaxRetries = 3 },
    HdrToneMapping = new HdrToneMappingOptions(ReferenceWhiteNits: 250),
};

var player = await Player.CreateAsync(configuration, nativePaths, platformFactory);
```

> [!IMPORTANT]
> Always start from `new MediaPlayerConfiguration()` (or `MediaPlayerConfiguration.Auto`, which is the same). The constructor sets the defaults listed here. A `default(MediaPlayerConfiguration)` has every field zeroed instead, for example a presentation lead of 0.

Two shortcuts exist: `new MediaPlayerConfiguration(VideoDecodingMethod.Software)` sets the decoding method and keeps all other defaults, and `MediaPlayerConfiguration.Auto` returns the default configuration.

## Options

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `VideoDecoding` | <xref:FatzMedia.Video.Core.Configuration.VideoDecodingMethod> | `Auto` | `Auto`, `Hardware` or `Software` video decoding. See [Hardware decoding](../video/hardware-decoding.md). |
| `Mode` | <xref:FatzMedia.Video.Core.Configuration.MediaPlayerMode> | `Full` | What the player loads and accepts: `Full`, `Background` (a looping picture source, no audio engine) or `Audio` (audio output only, no renderer). The mode can be changed later with `SetModeAsync`. See [Player modes](../playback/player-modes.md). |
| `EnableLogging` | `bool` | `false` | Backend changes, decode fallbacks, surface creation and seek recovery are written to the debug output. Use the `MediaDiagnostic` event for diagnostics you want to read in a release build. |
| `EnablePerformanceLogging` | `bool` | `false` | Periodic performance summaries (frames presented, dropped, late; tick gaps) written to `System.Diagnostics.Trace`. For diagnosing stutter and comparing presentation modes. |
| `EnableVerboseLogging` | `bool` | `false` | Very noisy detail: decoder candidates, individual FFmpeg packet errors, host callback cadence, seek previews. |
| `PresentationLeadRefreshIntervals` | `double` | `0.75` | Starting point, in display refresh intervals, of the adaptive presentation lead. See [Frame timing](../video/frame-timing.md). |
| `AudioOutputDeviceId` | `string?` | `null` | The output device: an id from `FatzAudioOutputDevices.Enumerate()` or a device name (case ignored). `null` is the system default, which follows default-device changes. An explicit device that goes away while playing moves to the default. See [Output devices](../audio/output-devices.md). |
| `AudioOutputFallbackMode` | <xref:FatzMedia.Video.Core.Configuration.AudioOutputFallbackMode> | `FallbackToPrimary` | What happens when an explicit device cannot be opened: switch to the primary device (with a warning), or `NoFallback`. |
| `SubtitleStyle` | <xref:FatzMedia.Video.Core.Configuration.SubtitleTextStyle> | See below | Look of text subtitles. |
| `KaraokeStyle` | <xref:FatzMedia.Video.Core.Configuration.KaraokeTextStyle> | See below | Look of MIDI/KAR lyrics, including the sung-text wipe colors. |
| `MidiSoundfontPath` | `string?` | `null` | Path to a `.sf2`, `.sf3` or `.dls` soundfont for MIDI synthesis. When set it is used instead of the system bank. When `null` the operating system's General MIDI bank is used where there is one; see [Native dependencies](../getting-started/native-dependencies.md#midi-soundfont). |
| `ResetPitchOnMediaLoad` | `bool` | `false` | Pitch returns to 0 after each successful load. |
| `ResetTempoOnMediaLoad` | `bool` | `false` | Tempo returns to 100 after each successful load. |
| `SilenceTrim` | <xref:FatzMedia.Video.SilenceTrimOptions> | Disabled | Leading and trailing silence trimming for analyzed plain audio. Not applied to synchronized media (video, CD+G). See below. |
| `NetworkRetry` | <xref:FatzMedia.Video.NetworkRetryPolicy>? | `null` (the default policy) | Retries when opening a network source. See below. |
| `NetworkTls` | <xref:FatzMedia.Video.NetworkTlsPolicy>? | `null` (the default policy) | Certificate checks for `https` network video. |
| `NetworkReadAhead` | <xref:FatzMedia.Video.NetworkReadAheadPolicy>? | `null` (the default policy) | How far network video reads ahead of playback. |
| `HdrToneMapping` | <xref:FatzMedia.Video.Core.Configuration.HdrToneMappingOptions> | Tone mapping on | How HDR video is shown on SDR output. See [Display and color](../video/display-and-color.md#hdr-video). |

## Subtitle style

<xref:FatzMedia.Video.Core.Configuration.SubtitleTextStyle> is used when the player rasterizes text subtitles. Colors are ARGB values (`0xAARRGGBB`). Out-of-range values are clamped.

| Property | Default | Notes |
| --- | --- | --- |
| `FontFamily` | `Segoe UI` | A blank name falls back to `Segoe UI`. |
| `FontSize` | `44` | Clamped to 6 to 240. |
| `TextArgb` | `0xFFFFFFFF` | |
| `OutlineArgb` | `0xC0000000` | |
| `OutlineWidth` | `1.25` | Clamped to 0 to 32. |
| `ShadowArgb` | `0x80000000` | |
| `ShadowOffsetX`, `ShadowOffsetY` | `2`, `2` | Clamped to -128 to 128. |

## Karaoke style

<xref:FatzMedia.Video.Core.Configuration.KaraokeTextStyle> styles MIDI and KAR lyrics. The plain properties describe lyrics not yet sung; the `Wipe` properties describe lyrics whose marker has passed. Colors are ARGB.

| Property | Default | Notes |
| --- | --- | --- |
| `FontFamily` | `Segoe UI` | |
| `FontSize` | `72` | Clamped to 6 to 240. |
| `TextArgb` / `WipeTextArgb` | `0xFFFFFFFF` / `0xFFFFFF00` | Unsung / sung text color. |
| `OutlineArgb` / `WipeOutlineArgb` | `0xE0000000` | |
| `OutlineWidth` / `WipeOutlineWidth` | `1.75` | Clamped to 0 to 32. |
| `ShadowArgb` / `WipeShadowArgb` | `0xA0000000` | |
| `ShadowOffsetX`, `ShadowOffsetY` | `2`, `2` | Clamped to -128 to 128. |
| `LineTransitionMilliseconds` | `1000` | Clamped to 0 to 4000. |
| `WipeTransitionMilliseconds` | `600` | Clamped to 0 to 2000. |
| `NextLineOpacity` | `0.65` | Clamped to 0 to 1. |

Both styles are rendered by the Direct3D11 renderer only; see [Renderers](../video/renderers.md).

## Silence trimming

<xref:FatzMedia.Video.SilenceTrimOptions> trims leading and trailing silence from plain audio files after an analysis pass.

| Property | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Whether analyzed silence changes the playable window. |
| `ThresholdDecibels` | `-50` | Level (dBFS) at or below which audio counts as silent. Clamped to -96 to 0. |
| `MinimumSilenceDuration` | 0.5 s | Silence shorter than this is kept. Clamped to 0 to 30 s. |
| `Padding` | 0.1 s | Silence kept at each trimmed edge. Clamped to 0 to 30 s. |
| `TrimLeading`, `TrimTrailing` | `true` | Which ends are trimmed when enabled. |

## Network policies

These apply to network sources, which open through FFmpeg.

### NetworkRetryPolicy

The default is one attempt and no retry. There is no unbounded retry.

| Property | Default | Range |
| --- | --- | --- |
| `MaxRetries` | `0` | 0 to 10 |
| `InitialDelay` | 1 s | 0 to 30 s |
| `BackoffMultiplier` | `2` | 1 to 4 |
| `MaxDelay` | 10 s | 0 to 60 s |
| `ConnectTimeout` | 15 s | 1 to 60 s |

### NetworkTlsPolicy

By default the server certificate must chain to a trusted root and match the host name.

| Property | Default | Meaning |
| --- | --- | --- |
| `AllowInvalidCertificates` | `false` | Accept invalid, expired, self-signed or mismatched certificates. The connection stays encrypted but tampering is not detected. Use only for servers you control. |
| `CertificateAuthorityFile` | `null` | A PEM file of trusted CA certificates used instead of the default roots. Honored by OpenSSL, GnuTLS and mbedTLS builds of FFmpeg. A Schannel (Windows) build ignores it and uses the Windows certificate store. |

### NetworkReadAheadPolicy

A background reader keeps a bounded queue of packets ahead of playback, so a short network stall does not interrupt it. Local files are not affected.

| Property | Default | Range |
| --- | --- | --- |
| `MaxDuration` | 20 s | 1 s to 10 min |
| `MaxBytes` | 64 MB | 1 MB to 1 GB |
| `ResumeDuration` | 2 s | 0.1 s to `MaxDuration` |
| `ReconnectFor` | 30 s | 0 to 5 min |

`ResumeDuration` is how much media must be buffered again before a buffering spell ends. `ReconnectFor` is how long the reader keeps reconnecting after the network drops mid-stream; playback continues from the buffer meanwhile.

## Settings outside the configuration

Some options are set elsewhere because they belong to the platform or the renderer:

| Setting | Where |
| --- | --- |
| Renderer, device selection, present mode, HDR output | `Direct3D11PresentationOptions`, the renderer factories. See [Renderers](../video/renderers.md). |
| Display refresh rate and device polling on Linux | `LinuxPlatformOptions`. See [Frame timing](../video/frame-timing.md). |
| Native library folders | The `nativeLibraryPaths` argument of the player. |
| Display mode, color transform, frame timing, volume, pitch, tempo, equalizer | Properties of the running player. |
| WPF presentation mode, AutoHide, role | Properties of the `MediaSurface`. See [Surfaces](../presentation/surfaces.md). |

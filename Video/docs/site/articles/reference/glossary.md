# Glossary

Short definitions of terms used across this guide.

## Media formats

**CD+G**
: A karaoke format. The audio is ordinary music and a companion `.cdg` file carries the lyrics graphics as a stream of small drawing commands for a 300 by 216 pixel, 16-color screen. The library draws them as an overlay over a background.

**KAR**
: A MIDI karaoke file. A MIDI file whose lyric events carry the words, so the library can highlight each word as it is sung.

**MIDI**
: A file of musical instructions, not recorded sound. The library plays it with a soundfont (`.sf2` or `.dls`) through BASS.

**Sidecar**
: A companion file that sits next to a media file and shares its name: `.srt`, `.ass` or `.ssa` subtitles next to a video, or a `.cdg` file next to an audio file. The player picks it up automatically unless you load with `ignoreSidecar: true`.

**HDR10, PQ, HLG**
: Two HDR video encodings. HDR10 uses the PQ (SMPTE ST 2084) curve, in which the signal means absolute brightness. HLG (hybrid log-gamma) is the broadcast encoding. Both are wider than SDR and must be tone mapped to show correctly on an SDR monitor.

**Tone mapping**
: Compressing HDR brightness into the range of an SDR display so the picture is neither washed out nor clipped.

## Rendering and presentation

**Renderer**
: The component that draws each composed frame: Direct3D11, Vulkan or Software.

**Platform factory**
: The object that combines operating-system services (native libraries, timers, audio device monitoring) with a renderer. For example `WindowsDirect3D11MediaControlPlatformFactory`.

**Surface**
: The UI control (`MediaSurface`) that the renderer draws into or that shows its frames.

**Presentation path**
: How frames get from the renderer to a surface: native window, frame sink or shared GPU image.

**Native window**
: A window handle (HWND) the surface owns, which the renderer presents to with a swap chain. Fast, but it draws above the UI framework's own content.

**Swap chain**
: A set of buffers a GPU API flips to show a frame in a window.

**Frame sink**
: A managed callback (`IMediaPresentationFrameSink`) that receives each finished frame as BGRA pixels in CPU memory. The surface then shows them as a bitmap. Works with every renderer; costs a copy per frame.

**Shared GPU image**
: A GPU texture that the renderer exports and the UI framework's compositor imports directly, so frames reach the screen without a trip through the CPU. Used by the Avalonia surface.

**WARP**
: Windows Advanced Rasterization Platform, Windows' built-in software implementation of Direct3D. The Direct3D11 renderer uses it when no GPU device is available.

**TDR**
: Timeout Detection and Recovery, the Windows mechanism that resets the GPU driver when the GPU stops responding. It makes the graphics device disappear until it is recreated. The player recovers from it automatically. See [GPU device loss](../presentation/gpu-device-loss.md).

**Projector role**
: The role (`MediaPresentationSurfaceRole.Projector`) of an extra surface that shows the player's output on another screen. Only the primary surface controls the display mode.

**AutoHide**
: A surface setting (default on) that hides the video until a frame of the current media has been shown, and again after stop, so a stale frame is never left on screen.

**Player mode**
: What a player loads and accepts: `Full`, `Background` (a looping picture source with no audio engine) or `Audio` (audio output only, no renderer).

**DirectComposition**
: A Windows API for composing visuals. The Direct3D11 renderer can use it internally for layered overlays; hosts do not configure it.

## Decoding and timing

**D3D11VA**
: Direct3D 11 Video Acceleration, the GPU video decoding API the Windows platform uses with FFmpeg.

**Vulkan Video**
: The GPU video decoding extension of Vulkan, used by the Vulkan renderer on GPUs that have a decode queue.

**Hardware decoding / software decoding**
: Decoding video on the GPU's fixed-function decoder, or on the CPU.

**Display-locked timing**
: A frame timing mode (`VideoFrameTiming.DisplayLocked`) that presents each frame for a specific refresh of the monitor, instead of at the video's own frame times.

**Cadence, pulldown, judder**
: Cadence is the pattern of how many refreshes each video frame stays on screen. 24 fps film on a 60 Hz display shows frames alternately 3 and 2 refreshes (3:2 pulldown), which can look uneven (judder) on slow pans.

**Vertical blank (vblank), VSync**
: The moment a display starts a new refresh. VSync presents frames in step with it to avoid tearing.

## Audio

**BASS**
: A third-party audio library (Un4seen) the library uses for audio playback, MIDI and effects. You deploy and license it yourself.

**FFmpeg**
: The open-source media library used to demux and decode video, and as a fallback for audio. You deploy it as shared libraries.

**Normalization**
: Adjusting gain so tracks play at a similar loudness.

**Soundfont**
: A file (`.sf2` or `.dls`) that holds instrument sounds for MIDI playback.

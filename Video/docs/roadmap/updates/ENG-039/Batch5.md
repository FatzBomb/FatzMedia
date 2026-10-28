# ENG-039 batch 5: performance (P5-1 to P5-11)

2026-10-28. Branch `claude/festive-newton-yvvib5-b5` (on top of 4c). Status: implemented,
pending manual smoke (the owner's tests 9 and 10 plus the steps below).

## What changed, with numbers (Linux unless noted)

- **P5-2 KAR raster** (smoke test 9 measured ~234 MB/s). A new 3.6 MB pixel array per wipe step
  plus new DirectWrite formats, layouts, brushes, WIC bitmap and render target every frame.
  Now: `ISubtitleRasterizer` gains default overloads that write into a buffer Core rents from a
  pool (`SubtitleOverlayPool`, reused while no composition holds it); the Direct3D11 rasterizer
  caches formats, layouts and brushes per frame and the bitmap/target per size, measures the
  wipe by hit-testing, and drops its target after a failed frame; Core compares cue references
  and skips unchanged rasters. Core side: 216 B -> 0 B per unchanged call, 0 B per raster.
  Windows expected well under 1 MB/s (not measured here). The wipe edge may move by a sub-pixel
  where kerning or a ligature crosses a syllable.
- **P5-1 embedded subtitles** (test 10 read 1.95 GB on open). Tracks decode on a background
  scan after the open, cancelled by every open, close and dispose; a late result is dropped.
  New public event `SubtitleTracksChanged` (`IMediaControlSession`, `MediaControlPlayer`,
  `SubtitleTracksChangedEventArgs`), raised through the control queue after `MediaLoaded`;
  `PlayerViewModel` refreshes its picker. The first embedded track is selected unless the host
  chose one (a request for an embedded track that has not arrived yet is kept and honoured).
  With a sidecar, the sidecar shows first and the embedded track takes over when the scan ends.
  264 MiB MKV: load 133-249 ms -> ~40 ms, bytes read during load 264 MiB -> 0.2 MiB.
- **P5-3** Direct3D11 shader bytecode compiled once per process (9 compiles per media open
  before); shader objects stay per device.
- **P5-4** Direct3D11 video-processor input views cached per decoder array slice; released on a
  new source texture, a failed blit and every presenter reset (processor rebuild, resize,
  device change or loss).
- **P5-5** Local video: the picture's demuxer discards the streams it does not play and the dead
  local fan-out in `SharedDemuxer` (including a loop-wrap drain that read to the end of the
  file) is gone. 10 s 1080p MP4: packets read 770 -> 301, demux 3.3 -> 1.7 ms. Network
  read-ahead unchanged.
- **P5-6** The decoder's frame queue is a `Queue` under a `Lock` (seek checks 144 KB -> 0 per
  1000); display-locked passes allocation-free on one display (39.7 KB -> 0 over 240 passes).
- **P5-7** `HostEventDispatcher` walks the invocation list in place (40-48 B -> 0 per multicast
  raise); WinForms surface brush cached; single `Marshal.Copy` for packed rows; WPF native
  background uses the stock black brush.
- **P5-8** Without BASS, a player with the same search paths reuses the earlier result
  (0.09-0.33 -> 0.007-0.027 ms). BASS copied in later is picked up after a restart
  (`NATIVE_DEPENDENCIES.md`).
- **P5-9** Archive audio read into one array (4 MiB entry: 8.4 MB -> 4.2 MB allocated).
- **P5-10** Vulkan: plane views cached per decoder image (376 B and two view create/destroy
  pairs per converted frame -> 0.2 B, 0 views); host-visible buffers stay mapped. The
  compositor keeps the decoder's frames context alive until the next one, a video-layer clear
  or dispose.
- **P5-11** Measured, skipped: the schedule is already a struct; the remaining ~2 KB/frame is
  public record layers on the Direct3D11 path, not worth a breaking change.

## Manual smoke (Windows)

1. Test 9: play the KAR with a steady wipe; allocation rate under 10 MB/s. Wipe edge, colour
   blend, outline, countdown arrows, scroll and page transitions, intro and end cards look as
   before; seek; plain SRT over video still updates and clears.
2. Test 10: open the large MKV with embedded subtitles; the load returns at once without reading
   the file; the picker fills with "Embedded ..." and the first is shown, also paused. With a
   sidecar: sidecar first, then embedded. Choose the sidecar or Off before the scan ends: it
   stays. Open another file at once: the old tracks never appear.
3. Direct3D11, hardware decoding: open 4-5 media in a row (MP4, CD+G, HDR10): correct each time,
   HDR still tone-maps. Seek a lot, change resolution and fps, resize, full screen, hardware
   background under CD+G; switch between two files: GPU memory does not keep growing. TDR:
   recovers, no repeated "input view" failures.
4. Local MP4/MKV with Loop on: wrap is gapless, A/V sync after seek. Network HLS/MP4 unchanged.
5. Display-locked on: 60 s of playback, no new stutter; also with two monitors.
6. `native/` with FFmpeg only: two players start without audio, each with a warning.
7. MP3, a tracker module and a CD+G+MP3 pair from a zip: play, seek, Stop and reopen.
8. WinForms frame sink: no flicker, BackColor change shows; `FromBitmap` with a 24 bpp,
   odd-width image is correct. WPF native surface paints black while idle.
9. Vulkan (Avalonia `--renderer=vulkan`, GPU with Vulkan Video): H.264 then HEVC 10-bit, seek,
   next file, close: no hang, memory steady.

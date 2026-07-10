<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: DONE — implemented, automated gates pass, manual on-device smoke clean (2026-07-09). See docs/roadmap/updates/012A_S1/StageUpdate.md.

# 012A.S1 — Introduce `MediaDemuxer` behind `VideoDecoder`

Parent: `docs/roadmap/012A_FUTURE_SharedDemuxerInputLayer.md`
Plan: `docs/roadmap/012A.2_PLAN_SharedDemuxerImplementation.md` (sub-stage S1)
Mapping: `docs/roadmap/012A_NOTE_SharedDemuxerInputLayerMap.md`

## Status

Backlog. First source-editing sub-stage of the shared-demuxer work. Scope is
deliberately tiny and behavior-preserving.

## Goal

Extract container open + packet read out of `VideoDecoder` into a new internal
`MediaDemuxer` type, and have `VideoDecoder` delegate to it. **Audio is not
touched. Output must be byte-identical to today.** This creates the seam the
later sub-stages (S2–S5) build on, with zero user-visible change.

## Activation preconditions (must all hold before editing)

- [ ] Error reporting / diagnostics not mid-refactor.
- [ ] Device handling stable — hardware fallback + device-lost recovery
      (`SwapVideoDecoder`, `VideoPipeline.cs:1393`) not being changed concurrently.
- [ ] State ownership stable.
- [ ] Working tree clean — commit first (currently dirty with the 009A move).

If any is unchecked: stop, report, do not edit source.

## Scope (in)

- New file `FatzBombMedia.MediaControl/Core/Media/MediaDemuxer.cs` — internal,
  unsafe, `IDisposable`. Owns exactly one `AVFormatContext`. Responsibilities:
  - `Open(string path)`: `avformat_open_input` + `avformat_find_stream_info`
    (mirrors `VideoDecoder.cs:107-116`).
  - Stream discovery: expose `FindBestStream(AVMediaType)` and per-stream access
    so the decoder keeps `av_find_best_stream(VIDEO)` semantics
    (`VideoDecoder.cs:118`) and the non-video `AVDISCARD_ALL` optimization
    (`VideoDecoder.cs:128-132`).
  - `ReadNextPacket(AVPacket* packet)` primitive wrapping `av_read_frame`
    (mirrors `VideoDecoder.cs:163`), returning the same result codes so EOF
    handling (`AVERROR_EOF`, `VideoDecoder.cs:166`) is unchanged.
  - `Seek(streamIndex, timestamp, flags)` wrapping `av_seek_frame`
    (mirrors `VideoDecoder.cs:1042`).
  - `Close()` / `Dispose()`: `avformat_close_input` (mirrors
    `VideoDecoder.cs:937-941`).
- `VideoDecoder.cs`: replace its inline `_fmt` ownership and inline
  open/read/seek/close calls with delegation to a `MediaDemuxer` instance.
  Keep `_videoStream`, `_videoIndex`, `_packet` and all decode logic exactly as
  is — only the *format-context plumbing* moves.

## Scope (out) — do NOT do in S1

- Do not touch `VideoAudioEngine` (that is S2).
- Do not introduce a shared cursor or packet fan-out (that is S3).
- Do not change any seek/loop/discard **semantics** (S4).
- Do not change `VideoDecoder`'s public or internal method signatures used by
  `VideoPipeline` (`Open`, `DecodeNextFrame`, `Seek`, `Flush`, `GetNextFrame`,
  `PeekNextFrame`, `Close`, `Dispose`, properties). Callers must not change.
- Do not remove the non-video-stream `AVDISCARD_ALL` optimization.
- No public API changes; no WPF adapter changes (Core-only).

## Must Not Break

- Decoded video frame **PTS sequence** (byte-identical to S0 baseline).
- HW/SW decoder selection and `IsHardwareAccelerated` / duration derivation
  (`VideoDecoder.cs:143-151`).
- Seek behavior (`VideoDecoder.Seek`, backward flag, discard-to-target).
- Device-lost recovery: `SwapVideoDecoder` disposes/recreates a `VideoDecoder`
  and reopens — the new `MediaDemuxer` must be fully torn down/rebuilt on
  `Close`/`Open` so a swap behaves exactly as before.
- `HardwareDecodeDeviceAvailable` throwaway-probe decoder (`VideoPipeline.cs:1373`)
  must still open cleanly.

## Implementation notes

- Keep it the *smallest edit*: prefer moving the existing `_fmt` field and its
  four call sites into `MediaDemuxer`, not a rewrite of decode flow.
- `MediaDemuxer` owns the `AVFormatContext` lifetime; `VideoDecoder` continues to
  own `_packet`, codec context, frames, scaler, and the frame queue.
- Preserve the exact EOF/error return contract of `av_read_frame` so
  `DecodeNextFrame` (`VideoDecoder.cs:157-184`) is unchanged apart from where the
  read comes from.
- Match repo style: file-scoped namespace, `Lock` type if any sync is needed
  (none expected — single-threaded per decoder), `unsafe`, C# 14.

## Validation (risk level: renderer/timing/media/native lifetime)

1. `dotnet build FatzBombMedia.sln` (Debug) — clean.
2. `dotnet build -c Release FatzBombMedia.sln` — **0 warnings** (warnings-as-errors).
3. `scripts/check-wpf-boundary.ps1` and `scripts/check-presentation-parity.ps1` — pass.
4. Manual smoke on S0 baseline files (H.264+AAC, HEVC hardware, AV1 software,
   AVI): video frame PTS sequence must match the S0 capture; A/V sync, seek
   (preview+settle), stop, media switch, gapless loop, secondary output,
   device-lost recovery — no visual regression.

If the S0 baseline was not captured yet, capture it (plan S0) before editing.

## Stop conditions

- Any PTS / duration / HW-path divergence from S0 → revert, do not proceed to S2.
- If moving the format context forces a `VideoDecoder` signature change →
  stop and re-scope; S1 must be caller-invisible.
- If device-lost recovery or the throwaway probe regresses → revert.

## Definition of done

- `MediaDemuxer.cs` exists and is the sole owner of `VideoDecoder`'s
  `AVFormatContext`.
- `VideoDecoder` public/internal surface unchanged; all callers compile untouched.
- Debug + Release clean, scripts pass, manual smoke matches S0.
- Update note written under `docs/roadmap/updates/012A_S1/`.
- Next: promote S2 only after this lands and is confirmed on-device.

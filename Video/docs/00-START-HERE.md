# Start Here

This solution is being shaped into a reusable media playback library plus a basic WPF host player.

## Current Goal

Build a clean `FatzBombMedia.Player` host app that uses `FatzBombMedia.MediaControl` without baking a full player UI into the media library.

Current rewrite status: the active `FatzBombMedia.MediaControl` project now runs through the new `Pipeline/` path for audio, CDG, video, archives, direct images, host-owned rendering, seek, and the test player surface. The `legacy/` folder is the reference copy for old behavior. Do not assume older flow/audit docs describe the current active code unless they explicitly mention the clean-slate migration.

The host app should own:

- XAML layout.
- Buttons, sliders, menus, overlays, and diagnostics.
- The visual display surface.
- View models and commands for host/player UI behavior.
- Native DLL deployment and the configured native library path.

`FatzBombMedia.MediaControl` should own:

- Media loading and file/archive detection.
- BASS audio playback and effects.
- FFmpeg video decode.
- CDG packet playback.
- Audio-clock-based video/CDG sync.
- Automatic render output.
- A thin WPF surface adapter that attaches render output and optional backgrounds to host-provided surfaces.

## Primary Design Decisions

Rendering selection is automatic. Users should not choose DirectX, software, hardware decode, or similar implementation details.

The host owns the display surface. The media library provides small WPF adapters, `AttachSurface(Image image)` and `AttachSurface(Image image, Panel backgroundHost)`, but consumers should not be required to use a packaged `MediaPlayerControl`.

The WPF host should use MVVM from the start. Code-behind should stay limited to view-only concerns such as file dialogs, WPF surface attachment, and disposal.

Video audio is processed through BASS. FFmpeg decodes video frames; audio from video files should be decoded separately into the BASS path so volume, pitch, tempo, and normalization remain available.

## Protected Features

Do not break these:

- CDG playback.
- MP3+CDG / audio+CDG companion matching.
- ZIP/archive media detection.
- BASS audio playback.
- BASS_FX pitch and tempo.
- Audio normalization.
- Video playback where FFmpeg decodes video and BASS handles audio separately.
- Audio-clock-based video sync.
- Automatic renderer fallback.

## Current Remaining Work

1. Keep the active control project compiling and smoke-tested in Visual Studio.
2. Continue seek/scrub tuning, especially video seek and frame catch-up behavior.
3. Decide whether video-audio normalization should remain unsupported or get its own FFmpeg/BASS analysis path.
4. Validate FFmpeg hardware decode, D3DImage presentation, and the D3D11 no-copy decode-to-surface path.
5. Fix MIDI playback and add the planned trim-silence, fade/crossfade, and visual color controls.
6. Continue performance passes for video frame copies, background video cost, archive memory use, and dispatcher frequency.
7. Keep docs current after each verified smoke-test pass.

## Cleanup Rule

The active project now favors clean-slate progression. Old orchestration can be deleted from the active project once the new pipeline has a replacement boundary. Use `legacy/FatzBombMedia.MediaControl/` as the reference copy, and import only audited snippets or low-level files that survive review.

## Current Docs

- `01-Architecture-And-Usage.md`
- `02-Features-And-Backlog.md`
- `03-CDG-Protection.md`
- `04-Audio-Video-Rendering.md`
- `05-Native-Libraries-And-NuGet.md`
- `06-Validation-And-Agent-Safety.md`

# FatzBombMedia — Active Cleanup Tracker

Updated 2026-06-30. Completed cleanup stages and historical audit details were removed from this file. Items that were concretely decided to keep because they are live in the current codebase are also omitted.

This file tracks only unresolved cleanup candidates that are deferred or still need design judgment.

---

## Rules

- Trace from the repo root before changing or deleting any symbol.
- Use whole-repo searches across C#, XAML, project files, scripts, docs, JSON/config, reflection strings, `nameof`, DI registrations, resources, and bindings.
- If any unexpected live usage appears, stop and report it before editing.
- Make one coherent cleanup stage at a time.
- Run `dotnet build FatzBombMedia.sln --no-restore` and `dotnet build -c Release FatzBombMedia.sln --no-restore` after implementation.
- Do not combine cleanup with renderer, WPF adapter, threading, or architecture changes unless the selected item explicitly requires it.

---

## Deferred / Unsure Items

### S6 — `RenderPipeline` Concrete `VideoPipeline` Cast

**Status:** Deferred / unsure.

**Current location:** `FatzBombMedia.MediaControl/Pipeline/RenderPipeline.cs`

**Current shape:** `RenderPipeline.RenderVideoAtCurrentPosition(...)` casts `_video is VideoPipeline` to call `RenderCurrentFrame(...)`.

**Why deferred:** Removing the cast cleanly likely requires either exposing a render-current-frame concept through an abstraction or moving the responsibility so `RenderPipeline` does not need a concrete `VideoPipeline`. Adding `RenderCurrentFrame(...)` to public `IVideoEngine` is not automatically safe because Core is packable and this would expose an internal rendering concern.

**Important current facts:**

- `PlaybackCoordinator` currently constructs the only in-repo video pipeline as `VideoPipeline`.
- The cast is not known to break current behavior.
- This should be treated as architecture cleanup, not dead-code deletion.

**Trace guards before any future work:**

```powershell
git grep -n -E "_video\s+is\s+VideoPipeline|RenderCurrentFrame|IVideoEngine|RenderPipeline|new\s+VideoPipeline|Attach\(" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "FatzBombMedia\.MediaControl\.Core\.(Pipeline|DxCdg|Graphics|Video)|Core\.Pipeline|Core\.DxCdg|Core\.Graphics|Core\.Video" -- FatzBombMedia.MediaControl.Wpf
```

**Do not do without explicit approval:**

- Do not add renderer-specific members to public `IVideoEngine` just to remove the cast.
- Do not broaden WPF adapter access to Core renderer/pipeline types.

---

### S7 — Possible `IAudioEngine` Role Split For `ClockAudioEngine`

**Status:** Deferred / unsure; architecture decision required.

**Current locations:**

- `FatzBombMedia.MediaControl/Pipeline/ClockAudioEngine.cs`
- `FatzBombMedia.MediaControl/Core/Abstractions/IAudioEngine.cs`
- `FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs`
- `FatzBombMedia.MediaControl/Pipeline/MediaSession.cs`

**Current shape:** `ClockAudioEngine` implements `IAudioEngine` so standalone CDG and video-without-audio paths can expose clock-backed position, duration, tempo, and transport state through the same active-audio facade. Several file/audio-specific `IAudioEngine` members throw `NotSupportedException`.

**Why deferred:** A proper cleanup would split transport/clock/effects/file-audio responsibilities or introduce a narrower clock-backed interface. That is an architecture change, not a dead-code removal. The current class is live and should not be deleted wholesale.

**Important current facts:**

- `MediaSession` constructs `ClockAudioEngine`.
- `PlaybackCoordinator.ActiveAudio` returns `ClockAudioEngine` for standalone CDG and video-without-audio cases.
- `MediaEffects` avoids applying audio effects unless current content has audio.

**Trace guards before any future work:**

```powershell
git grep -n -E "ClockAudioEngine|ActiveAudio|IAudioEngine|MediaEffects|SetVolume|SetPitch|SetTempo|Normalize|NormalizationGain|AnalyzePeak|PlayHandle|PlaybackEnded|PositionChanged" -- "*.cs" "*.xaml" "*.csproj" "*.props" "*.targets" "*.sln" "*.slnx" "*.ps1" "*.md" "*.json" "*.config" "*.xml"
git grep -n -E "new\s+ClockAudioEngine|ClockAudio\b|_session\.ClockAudio|Content\.HasFlag\(MediaContent\.Audio\)" -- FatzBombMedia.MediaControl
```

**Do not do without explicit approval:**

- Do not delete `ClockAudioEngine`; it is live.
- Do not remove unsupported members by breaking `IAudioEngine` implementers in one broad pass.
- Do not perform a large `IAudioEngine` split unless this becomes an approved architecture stage.

---

## Not Tracked Here

- Completed cleanup stages are intentionally removed from this file.
- Items already proven stale and absent from source are intentionally removed.
- Items reviewed and concretely kept because they are live in the current codebase are intentionally removed.
- Historical details remain available in git history.

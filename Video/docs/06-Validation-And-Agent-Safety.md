# Validation And Agent Safety

## Local Build

From the solution root on Windows:

```powershell
dotnet restore .\FatzBombMedia.sln
dotnet build .\FatzBombMedia.sln -c Debug
```

In this Codex environment, `dotnet build` may be blocked by SDK probing access to the local Microsoft SDK folder. Visual Studio/MSBuild validation on the developer machine remains the source of truth.

## Runtime Native Setup

Runtime playback requires latest matching x64 native DLLs.

The host player resolves its built-in native DLL folder in code. Put DLLs in:

```text
FatzBombMedia.Player/native/
```

The player project copies those DLLs into this runtime layout:

```text
FatzBombMedia.Player.exe
FatzBombMedia.MediaControl.dll
native/
  bass.dll
  bass_fx.dll
  FFmpeg DLLs matching the referenced FFmpeg.AutoGen package
```

## Smoke Checklist

Use this short checklist before deeper cleanup:

| Area | Check |
|---|---|
| Native loading | Missing DLLs show readable required/optional BASS and FFmpeg diagnostics. |
| Native path | Player sets `DLLPath.Bass` and `DLLPath.FFmpeg` from its host-owned native folder. |
| File picker | Open dialog uses `MediaPlayer.OpenFileDialogFilter`. |
| Audio | Audio file plays. |
| Audio effects | Volume, pitch, tempo, and normalization work on audio-only files. |
| Normalization | Stale analysis from a prior file cannot update the current file. |
| CDG | MP3+CDG and ZIP+CDG render lyrics on the host-owned surface. |
| CDG visuals | Background opacity and 16:9 side fill work. |
| Backgrounds | Same-name sidecar and internal zip backgrounds resolve in the expected priority. |
| Backgrounds | Background video loops silently and does not seek with audio/CDG. |
| Video | Video file plays. |
| Video effects | Volume, pitch, and tempo affect video audio. |
| Video normalization | Behavior is either implemented or clearly not claimed. |
| Seek | Audio/CDG live scrubbing does not glitch excessively; final committed seek lands correctly. |
| Surface | Repeated load/close keeps the display surface alive and updates correctly. |

## Agent Safety Rules

- Do not delete CDG code without CDG regression checks.
- Do not remove BASS_FX tempo/pitch code.
- Do not route video audio directly through FFmpeg output.
- Do not reintroduce hard-coded developer paths.
- Do not make FFmpeg required for audio/CDG-only playback unless necessary.
- Do not expose user-facing renderer toggles in new UI.
- Do not make the reusable library own the full host player UI.
- Do not mark crossfade, MIDI, trim silence, video-audio normalization, or true hardware presentation as fully supported until verified on representative files.

## Good Next Agent Tasks

1. Improve video playback smoothness and seek catch-up behavior.
2. Validate and tune true D3D11 hardware decode/presentation without changing public renderer selection.
3. Fix MIDI playback, including sound-font setup and accepted file filters.
4. Add trim-silence analysis/playback behavior.

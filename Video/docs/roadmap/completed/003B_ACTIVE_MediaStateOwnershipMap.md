<!-- Command-driven stage file. Use with AGENTS_WORKING_NOTES.md and this file only. -->

Stage file status: COMPLETED. See `completed/STAGE_003B-003E_MediaStateOwnershipThroughMarshaling.completed.md`.

# Stage 003B — MediaState Ownership Map

## Status

Active.

## Goal

Document `MediaState` ownership before changing state/event/public behavior.

This is a mapping stage only.

## Allowed Work

- Find every `MediaState` writer.
- Find every final playback-state origin.
- Find public event sources and likely thread context.
- Identify places where more than one system can appear to own final state.
- Recommend the next implementation stage.

## Not Allowed

- No source edits.
- No API cleanup.
- No event-threading changes.
- No seek, timing, renderer, decoder, or state behavior changes.
- No broad refactor.

## Likely Focus Files

```text
FatzBombMedia.MediaControl/Pipeline/MediaState.cs
FatzBombMedia.MediaControl/Pipeline/MediaSession.cs
FatzBombMedia.MediaControl/Pipeline/PlaybackCoordinator.cs
FatzBombMedia.MediaControl/Pipeline/AudioPipeline.cs
FatzBombMedia.MediaControl/Pipeline/CdgPipeline.cs
FatzBombMedia.MediaControl/Pipeline/VideoPipeline.cs
FatzBombMedia.MediaControl/Pipeline/PipelineMediaEngine.cs
FatzBombMedia.MediaControl/Api/MediaControlPlayer.cs
FatzBombMedia.MediaControl/Controllers/MediaFile.cs
FatzBombMedia.MediaControl/Controllers/MediaEffects.cs
FatzBombMedia.MediaControl/Core/Graphics/NativeMediaPresentationCoordinator.cs
```

## Required Output

- Table of every `MediaState` writer.
- Table of public event origins and thread context.
- Notes on state ownership conflicts or ambiguity.
- Recommendation for the next implementation stage.
- Confirmation that no source files were changed.

## Validation

Docs/mapping only. No build required unless source files are changed accidentally.

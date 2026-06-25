# CDG Protection

CDG playback is a highest-value protected area in this project. The code is low-level and easy to mistake for old rendering logic, but it is essential for karaoke playback.

## Protect These Areas

```text
Core/Cdg/CdgConstants.cs
Core/Cdg/CdgColorManager.cs
Core/Cdg/CdgPacketProcessor.cs
Core/Cdg/CdgEngine.cs
Core/Cdg/CdgEngineState.cs
Core/Files/FileTree.cs
Core/Files/Filetypes.cs
Core/Files/DirectoryScanner.cs
Controllers/CdgVisuals.cs
Controllers/MediaRenderer.cs
Core/Graphics/OptimizedGraphicsRenderer.cs
```

## Current CDG Behavior

- MP3+CDG side-by-side pairs load.
- ZIP audio+CDG bundles load.
- Standalone `.cdg` loads.
- Paired CDG follows active audio position.
- Standalone CDG uses `PlaybackClock`.
- Backward seek rebuilds packet state from the beginning.
- CDG background opacity is host-facing.
- CDG 16:9 presentation defaults on and fills side areas using the current CDG background color/opacity.
- CDG remains CPU bitmap based for correctness.

## Regression Checks

Before deleting or changing CDG code:

1. MP3+CDG side-by-side pair loads.
2. ZIP containing audio+CDG loads.
3. Standalone `.cdg` loads.
4. CDG renders at 0 seconds.
5. CDG renders after forward seek.
6. CDG rebuilds correctly after backward seek.
7. Stop resets CDG state.
8. Reloading a different CDG does not leak palette/frame state.
9. Opacity setting changes visible background alpha.
10. 16:9 mode fills side areas with CDG background color/opacity.

## Suggested Golden-Frame Test

Create a small known CDG fixture and render frames at fixed timestamps.

```text
testdata/cdg/sample/frame-00000.png
testdata/cdg/sample/frame-00005.png
testdata/cdg/sample/frame-00030.png
```

Start with tolerant hashes or manual visual checks if exact pixel format differences make strict PNG matching noisy.


# FatzBomb.MediaControl

Host-agnostic media engine core for FatzBomb.

This project intentionally contains no WPF types and does not set `UseWPF`. It owns shared media-engine state, audio/video/CD+G input handling, DirectX Cdg rendering, FFmpeg/BASS integration, Direct3D composition primitives, neutral configuration types, retained layer state, target planning, and final presentation.

## Boundary rule

Core must not reference framework-specific UI/bitmap types such as `System.Windows`, `ImageSource`, `BitmapSource`, `WriteableBitmap`, `D3DImage`, `Dispatcher`, or WPF pixel formats.

UI frameworks should integrate through adapter projects, for example:

- `FatzBomb.MediaControl.Wpf` for WPF control/HWND lifecycle interop.
- Future adapters such as WinForms, Avalonia, WinUI, or a custom native-HWND host.

## Current visual pipeline rule

All visual media continues to route through the single media compositor model:

- Layer 0: background/base visual, such as video, image, or a visualizer.
- Layer 1: optional wash/dim/color overlay.
- Layer 2: optional overlay graphics, such as Cdg lyrics.
- Audio-only/no-visual media is valid and should clear or transparent-clear through the compositor.

Media types provide optional layers. They do not independently present final frames.

## Current Presentation State

The current presentation architecture includes:

- retained Layer 0, Layer 1, and Layer 2 state
- layer invalidation and lifecycle metadata
- target registry/foundation
- real multi-target compositor fan-out
- host-usable secondary native HWND swap-chain activation through the strict WPF adapter

The Core project provides the media-control implementation and presentation ownership. Public WPF-facing helpers live in the strict WPF adapter and should not require Core internals.

Current architecture and validation docs:

- `docs/CURRENT_ARCHITECTURE.md`
- `docs/ProjectAnalysisRoadmap [2026-06-30].md`
- `docs/VALIDATION.md`

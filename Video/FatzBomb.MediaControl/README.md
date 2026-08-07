# FatzBomb.MediaControl

OS- and UI-agnostic media engine core for FatzBomb (`net10.0`).

This project intentionally contains no Win32, Vortice, WPF, or renderer implementation types. It owns shared media-engine state, portable audio/video/CD+G input handling, FFmpeg/BASS integration, neutral configuration and composition models, retained layer state, and platform/renderer contracts. Final GPU composition and native presentation belong to renderer packages selected by platform composition.

## Boundary rule

Core must not reference Win32/PInvoke, Vortice, framework-specific UI/bitmap types, renderer projects, or platform projects.

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

Core provides the media-control session and neutral contracts. Platform packages select renderer-owned presentation; public WPF-facing helpers remain in the strict WPF adapter and should not require Core internals.

Current architecture and validation docs:

- `docs/CURRENT_ARCHITECTURE.md`
- `docs/roadmap/STAGE_MANIFEST.md`
- `docs/VALIDATION.md`

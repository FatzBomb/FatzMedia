<!-- Conditional future stage. Promote only when its activation condition is met. -->

Stage file status: FUTURE.

# Future Stage 021A — Cross-Platform Renderer Abstraction


## Status

Future. Proposed order: 021A.

## Why This Exists

Future adapters such as Avalonia, WinUI, or non-Windows hosts may need renderer abstraction. Today, the Windows D3D11 path is the product strength and should not be weakened prematurely.

## Not Active Until

Promote only when a real second backend is being built or a concrete adapter requires it.

## Intended Direction

Preserve UI-agnostic Core boundaries now; introduce backend abstraction only when driven by a real backend.

## Must Not Break

- D3D11/native HWND/DirectComposition path.
- Core ownership of final presentation.
- WPF adapter boundary.
- Performance of current Windows karaoke host.

## First Safe Step

Map the minimal backend-independent concepts already present: surfaces, targets, composed frames, layers, presentation timing, and resource lifetime.

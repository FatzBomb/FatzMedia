# Active Stage 036A - Renderer Fallback Chain

Prepared: 2026-10-18
Status: slice 1 implemented, pending manual smoke (`updates/036A_1`).

## Why This Is Worth Doing

Before this stage the Direct3D11 renderer only ever asked for a hardware device
(`DriverType.Hardware`) and nothing chose another renderer: on a machine where no GPU device
can be created (some VMs and remote sessions, a broken or missing driver, a server without a
display driver) video simply did not show unless the host knew to pass `--software`. The
software renderer is also the slowest way to fill a large window on Windows (ENG-017: WPF's
bitmap path dominates once our renderer is fast), while Windows ships a fast multi-core
software Direct3D driver (WARP) that keeps the native swap-chain path.

## Tiers (pinned)

1. **Direct3D11 on the GPU** - the normal path, unchanged.
2. **Direct3D11 on WARP** - same renderer, same native-window presentation; the device is
   created with `DriverType.Warp` when no hardware device can be created. Logged as a
   warning with the hardware failure.
3. **Software renderer** - only when Direct3D11 cannot create any device (WARP included);
   needs a frame-sink surface and software decoding, as `--software` already does.

## Slice 1 Acceptance Criteria

1. `Direct3D11PresentationOptions.DeviceSelection` (`Auto` default, `Hardware`, `Warp`).
   `Auto` tries the GPU, then WARP. Every device creation raises one diagnostic naming the
   kind (hardware/WARP), the adapter and the feature level; falling back to WARP is a
   warning that quotes the hardware failure.
2. Core: optional `IMediaPresentationAvailabilityProbe` on a coordinator factory, and
   `FallbackPresentationCoordinatorFactory`, which picks the first available candidate (a
   candidate without a probe is always available; when none is, the last one is used) once,
   on first use, and records each probed candidate's result in `Selection`; the selected
   probe's `RequiresSoftwareDecoding` / `RequiresFrameSinkSurface` say what the host must
   pair with it. Pure logic, unit-tested (shared test folder, so it runs in the portable
   project too).
3. `Direct3D11PresentationCoordinatorFactory` implements the probe: it creates and releases
   a device with its configured selection and reports what it got.
4. Player: `--renderer=auto|gpu|warp|software` (`--software` stays as an alias). `auto`
   (default) is Direct3D11 `Auto` then software. The chain is resolved at startup; the
   selected renderer's requirements pick `WpfComposited` and software decoding (the software
   renderer; Direct3D11 on a software rasterizer, since hardware-decoded frames carry their
   GPU device, which the renderer would adopt). Forced WARP uses software decoding. The
   status line and the Log panel show the selection and why.
5. No change to Core boundaries: Core gains contracts only; Direct3D11 references Core only;
   the Player composes the chain.

## Explicit Non-Goals

- Switching renderer mid-session (a lost device recovers within Direct3D11 as before).
- Automatic WPF presentation-mode selection inside the WPF adapter (the host sets it).
- Making the software renderer accept hardware frames.
- A Linux chain (Vulkan then software); the same Core types are meant to serve it later.

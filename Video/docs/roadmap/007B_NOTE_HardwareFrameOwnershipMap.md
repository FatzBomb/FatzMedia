<!-- Design note for stage 007B. Pure mapping + release-safety ruling. No behavior change. -->

# 007B — Hardware Frame Ownership / Release Safety Map

Stage file: `docs/roadmap/007B_BACKLOG_HardwareFrameReleaseSafety.md`
Executes the stage's *First Safe Step*: "Audit hardware frame ownership from
decoder queue through composition/present release." This note is analysis
only. No code was changed in this sub-stage.

## 1. Hardware frame ownership chain

```
VideoDecoder.DecodeNextFrame()
  → FFmpeg av_hwframe_get_buffer() allocates an AVFrame backed by a D3D11
    texture from the decoder's hw_frames_ctx pool.
  → VideoFrameData { AVFrame* HardwareFrame, D3D11TexturePointer,
      D3D11ArraySlice, D3D11DevicePointer, D3D11DeviceContextPointer, ... }
    is enqueued in ConcurrentQueue<VideoFrameData> (VideoDecoder.cs:63).

VideoPipeline.PresentationLoop (dedicated thread)
  → lock (_presentationLock)
  → PresentFrameForCurrentPosition → PresentFrame (VideoPipeline.cs:2062)
  → _currentFrame = frame; RaiseFrameReady(frame) [synchronous event]
  → FrameReady?.Invoke → coordinator.OnVideoFrameReady
  → GPU present (blit decoder texture → compositor texture → swap chain Present)
  → finally: ReturnFrame(previousFrame) [the OLD frame, not the one just presented]

VideoDecoder.ReturnFrame (VideoDecoder.cs:958)
  → hardware frame: frame.ReleaseHardwareFrame() → av_frame_free()
  → returns the D3D11 texture to FFmpeg's hw_frames_ctx pool for reuse.
```

The just-presented frame stays in `_currentFrame` until the **next**
`PresentFrame` call replaces it and returns the old one. This is the
existing one-frame retention.

## 2. The D3D11 texture pointer boundary

`VideoFrameEventArgs` (`IVideoEngine.cs:5`) carries the raw
`D3D11TexturePointer` as an `IntPtr` — no ref-count, no release callback,
no ownership. This is the boundary 007A.2 explicitly left under `_lock`
and deferred to 007B.

Two coordinator paths consume this pointer:

| Path | Entry | What it does with the texture |
|---|---|---|
| Direct hardware present | `PresentPrimaryVideoFrame` hardware branch (`NativeMediaPresentationCoordinator.cs:633-639`) → `_presenter.PresentHardwareFrame(e)` | Video-processor blit from decoder texture to presentation target, then swap-chain Present. Under `_lock`. |
| Composition hardware present | `RenderHardwareVideoCompositionFrame` (`:856-895`) → builds `MediaCompositionFrame.BackgroundD3D11TexturePointer = frame.D3D11TexturePointer` → `PresentMediaComposition` | `MediaCompositor.PresentToTargets` → `SetHardwareBackgroundFrame` → video-processor blit from decoder texture to compositor's own render-target texture. Under `_lock`. |

Both paths **blit** (GPU-copy) the decoder texture into the compositor's
own texture. The decoder texture is the *source* of a one-shot GPU copy,
not directly presented to the swap chain.

## 3. The shared-device invariant — the actual safety mechanism

The critical safety property is that the coordinator and the decoder share
the **same D3D11 device and immediate context**. This is established by
`EnsureD3D11ForHardwareFrame` (`NativeMediaPresentationCoordinator.cs:357-362`):

```csharp
if (_presenter.NativeDevicePointer == frame.D3D11DevicePointer)
    return true;  // already sharing the decoder's device

var initialized = _presenter.InitializeFromNativeDevice(
    frame.D3D11DevicePointer, frame.D3D11DeviceContextPointer);
```

`D3D11DeviceResources.InitializeFromNativeDevice` (`D3D11DeviceResources.cs:81-98`)
AddRef's and wraps the decoder's device **and immediate context**:

```csharp
Context = deviceContextPointer != IntPtr.Zero
    ? AddRefAndWrap(deviceContextPointer, static pointer => new ID3D11DeviceContext(pointer))
    : Device.ImmediateContext;
```

Consequence: the coordinator's `deviceResources.Context` **is** the
decoder's immediate context. All GPU work — decode, video-processor blit,
swap-chain Present — runs on the **same D3D11 immediate context**, which is
**sequential** by the D3D11 contract. The decoder cannot submit a new frame
write to a texture until the blit has finished executing, because both
operations are on the same sequential command stream.

This is the real safety mechanism. It is **not** a fence, not a deferred
release, not a ref-count — it is the D3D11 immediate context's sequential
execution guarantee, combined with the shared-device invariant.

## 4. Additional safety layers

### 4.1 AddRef on the source texture

`D3D11VideoProcessorPresenter.PresentHardwareFrame` (`:64`) calls
`AddRefAndWrap(sourceTexturePointer, ...)` which calls `Marshal.AddRef` on
the decoder's D3D11 texture. The `using` ensures `Release()` is called when
the method returns. This prevents the texture from being freed during the
blit call itself. It does **not** prevent FFmpeg from recycling the texture
after the method returns — that's the shared-context's job (§3).

### 4.2 Flush after blit

`D3D11VideoProcessorPresenter.BlitTextureToPresentation` (`:135`) calls
`deviceResources.Context.Flush()` after `VideoProcessorBlt`. This submits
the command buffer to the GPU driver. It does **not** wait for the GPU to
finish — but since the decoder is on the same immediate context, the next
decode command can't be submitted until the blit is done.

### 4.3 One-frame retention in PresentFrame

`VideoPipeline.PresentFrame` (`:2062-2083`) retains the just-presented frame
in `_currentFrame` and returns the *previous* frame in the `finally`. This
provides one full frame interval of additional margin — the previous frame's
GPU work had a whole frame to complete before `av_frame_free` recycles it.
This is belt-and-suspenders on top of the shared-context serialization.

### 4.4 D3D11 internal resource tracking

D3D11 holds internal references to resources bound to the pipeline until
the GPU finishes using them. Even if all app references are released, the
texture won't be freed until the GPU completes the blit. Combined with the
shared context, this means the texture is safe from use-after-free at the
D3D11 object level.

## 5. Ruling

**The current hardware frame release design is safe.** The safety rests on
the shared D3D11 device + immediate context invariant (§3), which serializes
all GPU work (decode + blit + present) on a single sequential command stream.
The AddRef (§4.1), Flush (§4.2), one-frame retention (§4.3), and D3D11
internal resource tracking (§4.4) are additional safety layers.

**No fence, deferred-release ring, or retained-reference boundary is
required for correctness in the current architecture.**

The stage's three options (fence, deferred release, retained reference)
were evaluated against the actual safety mechanism:

- **D3D11 fence** (`ID3D11Fence`): would provide an explicit GPU completion
  boundary, but is unnecessary because the shared immediate context already
  serializes decode behind blit. A fence would add overhead (device query,
  completion-check loop) with no correctness benefit. **Not recommended.**
- **Deferred-release ring**: would make the one-frame retention (§4.3)
  explicit and tunable (N frames instead of 1). Harmless but unnecessary
  given the shared-context serialization. **Optional belt-and-suspenders.**
- **Retained reference (AddRef/Release on the texture)**: already done
  inside `PresentHardwareFrame` (§4.1). Extending it to the composition
  path would require `VideoFrameEventArgs` to carry a release callback,
  which is `019A` (VideoFrameEventArgs reuse API redesign) scope.
  **Not recommended for 007B.**

## 6. What should be done in 007B

The audit reveals that the safety is **implicit** — it depends on the
shared-device invariant, which is established at runtime by
`EnsureD3D11ForHardwareFrame` but is not documented or asserted. The
recommended work for 007B is:

1. **Document the shared-device invariant** as the explicit safety boundary
   in `EnsureD3D11ForHardwareFrame` and `InitializeFromNativeDevice`. A
   comment explaining that the coordinator must use the decoder's D3D11
   immediate context to serialize decode + blit + present.

2. **Add a debug assertion** that the coordinator's device pointer matches
   the frame's device pointer after `EnsureD3D11ForHardwareFrame` returns
   true. If a future change breaks the shared-device invariant, this
   assertion fires immediately rather than producing silent artifacts.

3. **No fence, no deferred-release ring, no API changes.** The current
   design is correct; the work is making the invariant explicit.

If the display-rate compositor rewrite (`017A`) ever decouples the blit
from the swap-chain Present (e.g., by using a deferred context or a
separate command queue), the shared-context serialization breaks and a
fence or deferred-release ring becomes mandatory. That escalation is
`017A`'s responsibility, not `007B`'s.

## 7. Validation

This sub-stage is analysis-only — no code changed, no build or smoke needed.

If the recommended documentation + debug assertion (§6) is implemented in a
follow-up sub-stage (007B.2), validation is:

- `dotnet build FatzBombMedia.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test` — no regressions.
- Manual smoke: hardware video playback, seek (rapid scrubbing), stop,
  media switch, secondary output, device-lost recovery. Watch for visual
  artifacts (tearing, flicker, stale frames) — none expected since no
  behavior changes.

## 8. Stop conditions for 007B.2

- If the debug assertion fires during smoke, the shared-device invariant
  is broken — stop and investigate before continuing.
- If the documentation reveals a path where the coordinator uses a
  different device than the decoder, stop — that's a real safety bug that
  needs a fence or deferred-release ring, not just documentation.
- Do not change `VideoFrameEventArgs`' public shape — that's `019A`.
- Do not change the decoder or hwframe pool — that's out of scope.
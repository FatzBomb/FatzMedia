# Stage 049A (planned) - Present-time feedback on Direct3D11

Owner decision 2026-11-03: planned work, after 037A slice 3.

Today the time a frame will be shown is an estimate: on Direct3D11 the next vblank from
`MonitorVblankClocks` (`IDXGIOutput.WaitForVBlank`, in `Stopwatch` ticks); elsewhere "now + the
presentation lead". Video frame choice and, from 037A slice 3, the karaoke draw time
(`GetClockAt` through `AudioEngine.MonotonicTimeFromStopwatch`) both use it.

The swap chain's present statistics (`IDXGISwapChain::GetFrameStatistics`: `PresentCount`,
`SyncQPCTime`, in the same QPC ticks) say when a frame actually reached the screen. Feeding that
back corrects the estimate for queued frames and DWM latency, so video and karaoke land on the
moment the frame is seen, not the next blank.

Scope when selected: read the statistics per surface (HWND and DirectComposition swap chains),
measure the difference from the estimate, and use it for the target time only where it
improves A/V skew in the measurements. Vulkan (`VK_GOOGLE_display_timing` /
`VK_KHR_present_wait`) only where the host presents itself; the Avalonia and WinForms hosts'
compositors do not report it.

using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;

/// <summary>
/// Applies <see cref="HdrOutputPolicy"/> to the primary target before each frame is drawn (Stage
/// 042A slice 2): reads the cached HDR state of the primary window's monitor, decides, switches
/// the swap chain when the mode changes and reports each change once (<c>D3D11.HdrOutput</c>).
/// Off unless <see cref="Rendering.Direct3D11.Direct3D11PresentationOptions.HdrOutput"/> is set:
/// then it never queries Windows and every target stays SDR. Called under the renderer's
/// presentation lock.
/// </summary>
sealed class D3D11HdrOutputController(bool enabled, Func<nint, HdrDisplayInfo?> query, Func<long>? timestamp = null)
{
    /// <summary>The reason given when the primary surface is offscreen (WPF composited, shared textures).</summary>
    internal const string OffscreenSurfaceReason = "this surface shows SDR only (WPF composited or shared-texture mode)";

    /// <summary>The reason given while a secondary window shows the same composition.</summary>
    internal const string SecondaryTargetReason = "another window shows the video (secondary windows are SDR)";

    /// <summary>The reason given while a video effect (base visual transform) is on.</summary>
    internal const string VideoEffectReason = "a video effect is on (effects are SDR)";

    readonly MonitorQueryCache<HdrDisplayInfo>? _displays = enabled ? new MonitorQueryCache<HdrDisplayInfo>(query, timestamp) : null;
    DecisionInputs? _lastInputs;
    HdrOutputDecision _lastDecision;
    HdrOutputDecision? _lastScRgb;

    // A primary whose switch back to SDR failed with its buffers still scRGB, at that size: not
    // retried every frame, only after a resize (or a re-created swap chain, which is SDR).
    (ID3D11PresentationBackend Backend, int Width, int Height)? _failedRevert;

    public bool IsEnabled => enabled;

    /// <summary>The last decision reported (tests and diagnostics).</summary>
    internal HdrOutputDecision? LastReported { get; private set; }

    /// <summary>Queries a newly attached primary window's monitor (on the host's thread), so frames never do.</summary>
    public void PrimeDisplay(nint hwnd) => _displays?.PrimeIfMissing(hwnd);

    /// <summary>Forgets a detached window.</summary>
    public void ForgetDisplay(nint hwnd) => _displays?.Remove(hwnd);

    /// <summary>Forgets every window.</summary>
    public void Clear() => _displays?.Clear();

    /// <summary>
    /// Decides the primary target's output for a frame of <paramref name="content"/> (null: SDR
    /// content), unless <paramref name="blocker"/> rules HDR out, and switches the swap chain to
    /// match. Returns the scRGB decision when the primary target holds scRGB for this frame, else
    /// null: the caller draws whatever the target holds.
    /// </summary>
    public HdrOutputDecision? PreparePrimary(ID3D11PresentationBackend? primary, HdrToneMapRequest? content, string? blocker)
    {
        if (!enabled || primary == null)
            return null;

        if (primary is not ID3D11HdrOutputBackend backend)
        {
            // The monitor does not matter here: HDR video gets the surface as its reason.
            Report(Decide(content, new HdrDisplayInfo(HdrEnabled: true, 0, 0), OffscreenSurfaceReason), failed: false);
            return null;
        }

        var display = _displays!.Get(backend.Hwnd) ?? default;
        var failed = blocker == null && backend.HdrOutputFailure != null;
        var decision = Decide(content, display, blocker ?? backend.HdrOutputFailure);
        var revertFailedHere = decision.Mode == HdrOutputMode.Sdr
            && _failedRevert is { } stuck
            && ReferenceEquals(stuck.Backend, primary)
            && stuck.Width == primary.Width
            && stuck.Height == primary.Height;
        if (!revertFailedHere && (decision.Mode != backend.OutputMode || decision.Mode == HdrOutputMode.ScRgb))
        {
            Hdr10MetadataValues? metadata = decision.Mode == HdrOutputMode.ScRgb ? CreateMetadata(content, decision) : null;
            if (!backend.TrySetOutputMode(decision.Mode, metadata))
            {
                // The change failed: SDR from now on for this swap chain, reported as a warning.
                failed = true;
                decision = Decide(content, display, backend.HdrOutputFailure ?? "the swap chain could not change its format");
                if (backend.OutputMode == HdrOutputMode.ScRgb)
                    _failedRevert = (primary, primary.Width, primary.Height);
            }
        }

        Report(decision, failed && decision.Mode == HdrOutputMode.Sdr && content != null);
        if (decision.Mode == HdrOutputMode.ScRgb)
            _lastScRgb = decision;

        if (backend.OutputMode != HdrOutputMode.ScRgb)
            return null;

        // The target is scRGB although the decision is SDR (going back failed): draw scRGB, so
        // the picture matches the buffer, with the last scRGB parameters.
        return decision.Mode == HdrOutputMode.ScRgb
            ? decision
            : _lastScRgb ?? HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, display with { HdrEnabled = true });
    }

    /// <summary>
    /// The HDR10 metadata for the swap chain: the brightest light the output holds, which is the
    /// content's peak for PQ (no brighter than the display) and the display's peak for HLG.
    /// </summary>
    internal static Hdr10MetadataValues CreateMetadata(HdrToneMapRequest? content, HdrOutputDecision decision) =>
        HdrOutputPolicy.CreateMetadata(content is { Transfer: HdrTransferFunction.Pq } request
            ? Math.Min(request.SourcePeakNits, decision.DisplayPeakNits)
            : decision.DisplayPeakNits);

    // Cached by input, so a steady state costs no allocation per frame.
    HdrOutputDecision Decide(HdrToneMapRequest? content, HdrDisplayInfo display, string? blocker)
    {
        var inputs = new DecisionInputs(content?.Transfer, display, blocker);
        if (_lastInputs == inputs)
            return _lastDecision;

        _lastInputs = inputs;
        _lastDecision = HdrOutputPolicy.Restrict(HdrOutputPolicy.Choose(true, content?.Transfer, display), blocker);
        return _lastDecision;
    }

    void Report(HdrOutputDecision decision, bool failed)
    {
        if (LastReported is { } previous && previous.Mode == decision.Mode && string.Equals(previous.Reason, decision.Reason, StringComparison.Ordinal))
            return;

        LastReported = decision;
        DiagnosticRouter.Raise(
            failed ? MediaDiagnosticLevel.Warning : MediaDiagnosticLevel.Info,
            "D3D11.HdrOutput",
            $"{(decision.Mode == HdrOutputMode.ScRgb ? "scRGB" : "SDR")} ({decision.Reason}).");
    }

    readonly record struct DecisionInputs(HdrTransferFunction? Transfer, HdrDisplayInfo Display, string? Blocker);
}

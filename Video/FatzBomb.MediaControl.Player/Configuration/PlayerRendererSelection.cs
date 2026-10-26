using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Player.Configuration;

/// <summary>The renderer the Player is asked to use (<c>--renderer=</c>).</summary>
internal enum PlayerRendererChoice
{
    /// <summary>Direct3D11 on the GPU, then WARP, then the software renderer (the default).</summary>
    Auto,

    /// <summary>Direct3D11 on the GPU only.</summary>
    Gpu,

    /// <summary>Direct3D11 on WARP only, with software decoding.</summary>
    Warp,

    /// <summary>The software renderer, with software decoding (also <c>--software</c>).</summary>
    Software
}

/// <summary>
/// Stage 036A: builds the presentation factory for a <see cref="PlayerRendererChoice"/>, resolves
/// the fallback chain at startup (the surface mode and decoding depend on it), and keeps what was
/// found for the status line and the Log panel.
/// </summary>
internal sealed class PlayerRendererSelection
{
    private readonly List<(MediaDiagnosticLevel Level, string Message)> _logLines;

    private PlayerRendererSelection(
        IMediaPresentationCoordinatorFactory factory,
        bool usesSoftwareRenderer,
        bool usesSoftwareDecoding,
        string summary,
        List<(MediaDiagnosticLevel Level, string Message)> logLines)
    {
        Factory = factory;
        UsesSoftwareRenderer = usesSoftwareRenderer;
        UsesSoftwareDecoding = usesSoftwareDecoding;
        Summary = summary;
        _logLines = logLines;
    }

    public IMediaPresentationCoordinatorFactory Factory { get; }

    /// <summary>Gets whether the renderer needs a frame-sink (WPF-composited) surface: the software renderer.</summary>
    public bool UsesSoftwareRenderer { get; }

    /// <summary>Gets whether video is decoded in software (software renderer, or Direct3D11 on a software rasterizer).</summary>
    public bool UsesSoftwareDecoding { get; }

    /// <summary>Gets a one-line description for the status line.</summary>
    public string Summary { get; }

    /// <summary>Gets what was probed and chosen, for the Log panel.</summary>
    public IReadOnlyList<(MediaDiagnosticLevel Level, string Message)> LogLines => _logLines;

    public void AddLogLine(MediaDiagnosticLevel level, string message) => _logLines.Insert(0, (level, message));

    public static bool TryParse(string value, out PlayerRendererChoice choice)
    {
        choice = value.Trim().ToLowerInvariant() switch
        {
            "auto" => PlayerRendererChoice.Auto,
            "gpu" or "d3d11" or "hardware" => PlayerRendererChoice.Gpu,
            "warp" => PlayerRendererChoice.Warp,
            "software" or "cpu" => PlayerRendererChoice.Software,
            _ => (PlayerRendererChoice)(-1)
        };
        return Enum.IsDefined(choice);
    }

    /// <param name="hdrOutput">Stage 042A slice 2: HDR output on HDR displays (<c>--hdr-output</c>).</param>
    public static PlayerRendererSelection Resolve(PlayerRendererChoice choice, bool hdrOutput = false)
    {
        Direct3D11PresentationCoordinatorFactory Direct3D11(Direct3D11DeviceSelection selection) =>
            WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(
                new Direct3D11PresentationOptions { DeviceSelection = selection, HdrOutput = hdrOutput });

        switch (choice)
        {
            // Forced tiers skip the probe: the session's own device diagnostics name what it got.
            case PlayerRendererChoice.Gpu:
                return new PlayerRendererSelection(
                    Direct3D11(Direct3D11DeviceSelection.Hardware),
                    usesSoftwareRenderer: false,
                    usesSoftwareDecoding: false,
                    "Direct3D11 GPU (forced)",
                    [(MediaDiagnosticLevel.Info, "Direct3D11 forced to the GPU (--renderer=gpu).")]);

            case PlayerRendererChoice.Warp:
                return new PlayerRendererSelection(
                    Direct3D11(Direct3D11DeviceSelection.Warp),
                    usesSoftwareRenderer: false,
                    usesSoftwareDecoding: true,
                    "Direct3D11 WARP (forced)",
                    [(MediaDiagnosticLevel.Info, "Direct3D11 forced to WARP (--renderer=warp), with software decoding.")]);

            case PlayerRendererChoice.Software:
                return new PlayerRendererSelection(
                    new SoftwarePresentationCoordinatorFactory(),
                    usesSoftwareRenderer: true,
                    usesSoftwareDecoding: true,
                    "Software (forced)",
                    [(MediaDiagnosticLevel.Info, "Software renderer forced (--renderer=software or --software).")]);

            default:
                {
                    var chain = new FallbackPresentationCoordinatorFactory(
                        Direct3D11(Direct3D11DeviceSelection.Auto),
                        new SoftwarePresentationCoordinatorFactory());
                    MediaPresentationAvailability selected = chain.SelectedAvailability;
                    var lines = chain.Selection
                        .Select(result => (
                            result.IsSelected
                                ? result.IsAvailable ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Error
                                : MediaDiagnosticLevel.Warning,
                            $"{result.BackendName}: {(result.IsAvailable ? "available" : "unavailable")} - {result.Detail}{(result.IsSelected ? " (selected)" : "")}"))
                        .ToList();
                    return new PlayerRendererSelection(
                        chain,
                        usesSoftwareRenderer: selected.RequiresFrameSinkSurface,
                        usesSoftwareDecoding: selected.RequiresSoftwareDecoding,
                        $"{chain.BackendName} (auto): {selected.Detail}",
                        lines);
                }
        }
    }

    /// <summary>Used when building the chain itself failed (for example a missing renderer assembly).</summary>
    public static PlayerRendererSelection SoftwareAfterFailure(Exception exception) => new(
        new SoftwarePresentationCoordinatorFactory(),
        usesSoftwareRenderer: true,
        usesSoftwareDecoding: true,
        "Software (renderer selection failed)",
        [(MediaDiagnosticLevel.Error, $"Renderer selection failed ({exception.Message}); using the software renderer.")]);
}

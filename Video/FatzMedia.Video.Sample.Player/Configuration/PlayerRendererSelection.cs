using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Sample.Player.Shared;
using FatzMedia.Video.Sample.Player.Shared.Hosting;
using FatzMedia.Video.Direct3D11;
using FatzMedia.Video.Software;
using FatzMedia.Video.Vulkan;

namespace FatzMedia.Video.Sample.Player.Configuration;

/// <summary>
/// The Windows players' renderer choice (Stage 036A, shared by the WPF and WinForms players since
/// Stage 046A slice 3; the WinForms project compiles this file by link): maps the shared
/// <see cref="PlayerRendererRequest"/> to a presentation factory, resolves the GPU-first
/// <see cref="PlayerRendererRequest.Auto"/> chain at startup (the surface path and the decoding
/// depend on it), and keeps what was found for the status line and the Log panel.
/// </summary>
/// <remarks>
/// <see cref="PlayerRendererRequest.Auto"/> is <see cref="PlayerRendererRules.AutoOrder"/> on
/// Windows: <c>FallbackPresentationCoordinatorFactory(Direct3D11 Auto, Software)</c>, Direct3D11's
/// Auto device being the GPU, then WARP. Each player gets its own factory
/// (<see cref="CreateFactory"/>) of the chosen kind; nothing is shared between players.
/// </remarks>
internal sealed class PlayerRendererSelection
{
    /// <summary>
    /// The renderers the Windows players' toolbar offers (the Avalonia player's toolbar lists its
    /// probes). A choice that is not usable here falls back to auto, with a warning in the Log panel.
    /// </summary>
    public static IReadOnlyList<(PlayerRendererRequest Request, string Label)> Choices { get; } =
    [
        (PlayerRendererRequest.Auto, "Auto (GPU first)"),
        (PlayerRendererRequest.Gpu, "Direct3D11 GPU"),
        (PlayerRendererRequest.Warp, "Direct3D11 WARP"),
        (PlayerRendererRequest.Software, "Software"),
        (PlayerRendererRequest.Vulkan, "Vulkan"),
        (PlayerRendererRequest.Direct3D11, "Direct3D11 (GPU, else WARP)"),
    ];

    private readonly Func<IMediaPresentationCoordinatorFactory> _createFactory;
    private readonly List<(MediaDiagnosticLevel Level, string Message)> _logLines;

    private PlayerRendererSelection(
        Func<IMediaPresentationCoordinatorFactory> createFactory,
        bool usesFrameSinkRenderer,
        bool usesSoftwareDecoding,
        string summary,
        List<(MediaDiagnosticLevel Level, string Message)> logLines)
    {
        _createFactory = createFactory;
        UsesSoftwareRenderer = usesFrameSinkRenderer;
        UsesSoftwareDecoding = usesSoftwareDecoding;
        Summary = summary;
        _logLines = logLines;
    }

    /// <summary>
    /// Gets whether the renderer presents through frame sinks only (software, Vulkan): the WPF
    /// surface is then WPF-composited, the WinForms surface uses its frame sink.
    /// </summary>
    public bool UsesSoftwareRenderer { get; }

    /// <summary>Gets whether video is decoded in software (software or Vulkan renderer, or Direct3D11 on a software rasterizer).</summary>
    public bool UsesSoftwareDecoding { get; }

    /// <summary>Gets a one-line description for the status line.</summary>
    public string Summary { get; }

    /// <summary>Gets what was probed and chosen, for the Log panel.</summary>
    public IReadOnlyList<(MediaDiagnosticLevel Level, string Message)> LogLines => _logLines;

    /// <summary>Creates a presentation factory of the chosen renderer, one per player.</summary>
    public IMediaPresentationCoordinatorFactory CreateFactory() => _createFactory();

    public void AddLogLine(MediaDiagnosticLevel level, string message) => _logLines.Insert(0, (level, message));

    /// <summary>
    /// Gets what one player's view model needs from this choice: the decoding the renderer can
    /// present, or software with <c>--decoding=software</c> (<see cref="PlayerStartupOptions.ApplyDecoding"/>),
    /// the surface kind, and the log lines followed by <paramref name="extraLines"/>.
    /// </summary>
    public PlayerRendererInfo ToRendererInfo(PlayerStartupOptions options, IEnumerable<(MediaDiagnosticLevel Level, string Message)>? extraLines = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        (VideoDecodingMethod decoding, var forced) = options.ApplyDecoding(
            UsesSoftwareDecoding ? VideoDecodingMethod.Software : VideoDecodingMethod.Hardware);
        List<(MediaDiagnosticLevel Level, string Message)> lines = [.. _logLines];
        if (forced is not null)
            lines.Add((MediaDiagnosticLevel.Info, $"Video decoding: {forced}."));
        if (extraLines is not null)
            lines.AddRange(extraLines);
        return new PlayerRendererInfo(Summary, decoding, UsesSoftwareRenderer, lines);
    }

    /// <param name="request">The shared renderer request (<c>--renderer=</c>, <c>--software</c>).</param>
    /// <param name="hdrOutput">Stage 042A slice 2: HDR output on HDR displays (<c>--hdr-output</c>, Direct3D11 only).</param>
    public static PlayerRendererSelection Resolve(PlayerRendererRequest request, bool hdrOutput = false)
    {
        Func<IMediaPresentationCoordinatorFactory> Direct3D11(Direct3D11DeviceSelection selection) =>
            () => WindowsDirect3D11MediaControlPlatformFactory.CreatePresentationFactory(
                new Direct3D11PresentationOptions { DeviceSelection = selection, HdrOutput = hdrOutput });

        switch (request)
        {
            // Forced Direct3D11 tiers skip the probe: the session's own device diagnostics name what it got.
            case PlayerRendererRequest.Gpu:
                return new PlayerRendererSelection(
                    Direct3D11(Direct3D11DeviceSelection.Hardware),
                    usesFrameSinkRenderer: false,
                    usesSoftwareDecoding: false,
                    "Direct3D11 GPU (forced)",
                    [(MediaDiagnosticLevel.Info, "Direct3D11 forced to the GPU (--renderer=gpu).")]);

            case PlayerRendererRequest.Warp:
                return new PlayerRendererSelection(
                    Direct3D11(Direct3D11DeviceSelection.Warp),
                    usesFrameSinkRenderer: false,
                    usesSoftwareDecoding: true,
                    "Direct3D11 WARP (forced)",
                    [(MediaDiagnosticLevel.Info, "Direct3D11 forced to WARP (--renderer=warp), with software decoding.")]);

            case PlayerRendererRequest.Direct3D11:
                return new PlayerRendererSelection(
                    Direct3D11(Direct3D11DeviceSelection.Auto),
                    usesFrameSinkRenderer: false,
                    usesSoftwareDecoding: false,
                    "Direct3D11 (GPU, else WARP; forced)",
                    [(MediaDiagnosticLevel.Info, "Direct3D11 forced (--renderer=direct3d11): the GPU, else WARP, without the software fallback.")]);

            case PlayerRendererRequest.Software:
                return new PlayerRendererSelection(
                    static () => new SoftwarePresentationCoordinatorFactory(),
                    usesFrameSinkRenderer: true,
                    usesSoftwareDecoding: true,
                    "Software (forced)",
                    [(MediaDiagnosticLevel.Info, "Software renderer forced (--renderer=software or --software), shown through frame sinks, with software decoding.")]);

            case PlayerRendererRequest.Vulkan:
                {
                    MediaPresentationAvailability vulkan = new VulkanPresentationCoordinatorFactory().ProbeAvailability();
                    if (vulkan.IsAvailable)
                    {
                        // Stage 045A slice 3: hardware decoding (Vulkan Video on the renderer's
                        // device) when the device has it, software decoding otherwise.
                        (_, var decoding) = PlayerRendererRules.DescribeDecoding(PlayerRendererRequest.Vulkan, vulkan.RequiresSoftwareDecoding);
                        return new PlayerRendererSelection(
                            static () => new VulkanPresentationCoordinatorFactory(),
                            usesFrameSinkRenderer: true,
                            usesSoftwareDecoding: vulkan.RequiresSoftwareDecoding,
                            "Vulkan (forced)",
                            [(MediaDiagnosticLevel.Info, $"Vulkan forced (--renderer=vulkan): {vulkan.Detail}; shown through frame sinks, with {decoding}.")]);
                    }

                    PlayerRendererSelection auto = Resolve(PlayerRendererRequest.Auto, hdrOutput);
                    auto.AddLogLine(MediaDiagnosticLevel.Warning, $"--renderer=vulkan is not usable here ({vulkan.Detail}); using auto.");
                    return auto;
                }

            default:
                {
                    var chain = new FallbackPresentationCoordinatorFactory(
                        Direct3D11(Direct3D11DeviceSelection.Auto)(),
                        new SoftwarePresentationCoordinatorFactory());
                    MediaPresentationAvailability selected = chain.SelectedAvailability;
                    var lines = chain.Selection
                        .Select(result => (
                            result.IsSelected
                                ? result.IsAvailable ? MediaDiagnosticLevel.Info : MediaDiagnosticLevel.Error
                                : MediaDiagnosticLevel.Warning,
                            $"{result.BackendName}: {(result.IsAvailable ? "available" : "unavailable")} - {result.Detail}{(result.IsSelected ? " (selected)" : "")}"))
                        .ToList();
                    Func<IMediaPresentationCoordinatorFactory> create = chain.Selected is SoftwarePresentationCoordinatorFactory
                        ? static () => new SoftwarePresentationCoordinatorFactory()
                        : Direct3D11(Direct3D11DeviceSelection.Auto);
                    return new PlayerRendererSelection(
                        create,
                        usesFrameSinkRenderer: selected.RequiresFrameSinkSurface,
                        usesSoftwareDecoding: selected.RequiresSoftwareDecoding,
                        $"{chain.BackendName} (auto): {selected.Detail}",
                        lines);
                }
        }
    }

    /// <summary>
    /// Resolves <paramref name="options"/>' renderer off the calling thread (the auto chain creates a
    /// probe device), never throwing: a failure gives the software renderer. The command line's
    /// unknown values are logged first.
    /// </summary>
    public static Task<PlayerRendererSelection> ResolveInBackground(PlayerStartupOptions options) =>
        ResolveInBackground(options, options.RendererRequest);

    /// <summary>
    /// Resolves <paramref name="request"/> (the toolbar's choice, or the command line's) off the
    /// calling thread, as <see cref="ResolveInBackground(PlayerStartupOptions)"/> does.
    /// </summary>
    public static Task<PlayerRendererSelection> ResolveInBackground(PlayerStartupOptions options, PlayerRendererRequest request) => Task.Run(() =>
    {
        PlayerRendererSelection selection;
        try
        {
            selection = Resolve(request, options.HdrOutput);
        }
        catch (Exception ex)
        {
            selection = SoftwareAfterFailure(ex);
        }

        foreach (var warning in options.DescribeUnknownValues().Reverse())
            selection.AddLogLine(MediaDiagnosticLevel.Warning, warning);
        return selection;
    });

    /// <summary>Used when building the chain itself failed (for example a missing renderer assembly).</summary>
    public static PlayerRendererSelection SoftwareAfterFailure(Exception exception) => new(
        static () => new SoftwarePresentationCoordinatorFactory(),
        usesFrameSinkRenderer: true,
        usesSoftwareDecoding: true,
        "Software (renderer selection failed)",
        [(MediaDiagnosticLevel.Error, $"Renderer selection failed ({exception.Message}); using the software renderer.")]);
}

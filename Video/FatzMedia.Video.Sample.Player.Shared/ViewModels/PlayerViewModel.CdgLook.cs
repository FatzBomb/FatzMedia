using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>A choice of <see cref="CdgScalingOptions.RenderHeight"/> in the CD+G look controls.</summary>
/// <param name="Height">The height, or 0 for the presentation canvas.</param>
/// <param name="Label">What the list shows.</param>
public sealed record CdgRenderHeightChoice(int Height, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The scaled CD+G look (ENG-064): scaling on or off, smoothness, edge blend, outline, shadow, bevel and the height the
/// picture is drawn at, applied live through <see cref="PlayerCore.CdgScaling"/>. <c>--cdg-scale[=n]</c> and
/// <c>--cdg-effects</c> set the starting values. Any player can bind these; the WPF player shows them in the Video flyout.
/// </summary>
public partial class PlayerViewModel
{
    const double DefaultBevelSize = 0.5;
    bool _suppressCdgLookApply;

    /// <summary>The heights the scaled picture can be drawn at.</summary>
    public IReadOnlyList<CdgRenderHeightChoice> CdgRenderHeights { get; } =
    [
        new(0, "Canvas (1080)"),
        new(432, "432 (2x)"),
        new(648, "648 (3x)"),
        new(864, "864 (4x)"),
        new(1080, "1080 (5x)"),
        new(1440, "1440 (6x)"),
        new(2160, "2160 (10x)"),
    ];

    public RelayCommand ResetCdgLookCommand { get; private set; } = null!;

    public bool CdgScaled
    {
        get;
        set => SetCdgLook(ref field, value);
    }

    public int CdgSmoothness
    {
        get;
        set => SetCdgLook(ref field, Math.Clamp(value, 0, 10));
    } = PlayerStartupOptions.DefaultCdgSmoothness;

    public bool CdgEdgeBlend
    {
        get;
        set => SetCdgLook(ref field, value);
    } = true;

    public double CdgOutlineSize
    {
        get;
        set => SetCdgLook(ref field, ToUnit(value));
    }

    public double CdgShadowSize
    {
        get;
        set => SetCdgLook(ref field, ToUnit(value));
    }

    public double CdgShadowDistance
    {
        get;
        set => SetCdgLook(ref field, ToUnit(value));
    }

    public double CdgBevelStrength
    {
        get;
        set => SetCdgLook(ref field, ToUnit(value));
    }

    public double CdgBevelSize
    {
        get;
        set => SetCdgLook(ref field, ToUnit(value));
    } = DefaultBevelSize;

    public CdgRenderHeightChoice CdgRenderHeight
    {
        get;
        set => SetCdgLook(ref field, value ?? CdgRenderHeights[0]);
    } = null!;

    void InitializeCdgLookControls()
    {
        ResetCdgLookCommand = new RelayCommand(ResetCdgLook);
        var options = Options.CdgScaling;
        _suppressCdgLookApply = true;
        CdgRenderHeight = CdgRenderHeights[0];
        if (options.Enabled)
        {
            CdgScaled = true;
            CdgSmoothness = options.Smoothness;
            CdgEdgeBlend = options.EdgeBlend;
            CdgOutlineSize = options.OutlineSize;
            CdgShadowSize = options.ShadowSize;
            CdgShadowDistance = options.ShadowDistance;
            CdgBevelStrength = options.BevelStrength;
            if (options.BevelSize > 0)
                CdgBevelSize = options.BevelSize;
            CdgRenderHeight = CdgRenderHeights.FirstOrDefault(choice => choice.Height == options.RenderHeight) ?? CdgRenderHeights[0];
        }

        _suppressCdgLookApply = false;
    }

    /// <summary>The look as the player's options; the safety zone stays the startup one.</summary>
    CdgScalingOptions BuildCdgScaling() => Options.CdgScaling with
    {
        Enabled = CdgScaled,
        Smoothness = CdgSmoothness,
        EdgeBlend = CdgEdgeBlend,
        OutlineSize = (float)CdgOutlineSize,
        ShadowSize = (float)CdgShadowSize,
        ShadowDistance = (float)CdgShadowDistance,
        BevelStrength = (float)CdgBevelStrength,
        BevelSize = (float)CdgBevelSize,
        RenderHeight = CdgRenderHeight.Height,
    };

    void SetCdgLook<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
            ApplyCdgLook();
    }

    void ApplyCdgLook()
    {
        if (_suppressCdgLookApply || Player is not { } player)
            return;

        player.CdgScaling = BuildCdgScaling();
    }

    /// <summary>The owner's pick (smoothness 6, edge blend) with no effects, drawn on the canvas; scaling stays as it is.</summary>
    void ResetCdgLook()
    {
        _suppressCdgLookApply = true;
        CdgSmoothness = PlayerStartupOptions.DefaultCdgSmoothness;
        CdgEdgeBlend = true;
        CdgOutlineSize = 0;
        CdgShadowSize = 0;
        CdgShadowDistance = 0;
        CdgBevelStrength = 0;
        CdgBevelSize = DefaultBevelSize;
        CdgRenderHeight = CdgRenderHeights[0];
        _suppressCdgLookApply = false;
        ApplyCdgLook();
        StatusText = "CD+G look reset.";
    }

    // Two decimals, as the sliders show them.
    static double ToUnit(double value) => double.IsFinite(value) ? Math.Round(Math.Clamp(value, 0, 1), 2) : 0;
}

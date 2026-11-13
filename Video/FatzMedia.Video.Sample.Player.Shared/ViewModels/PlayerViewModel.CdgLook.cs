using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>A choice in one of the CD+G look lists: the value and what the list shows.</summary>
/// <typeparam name="T">The option's type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Label">What the list shows.</param>
public sealed record CdgLookChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The scaled CD+G look (ENG-064): the scale (None, Low, Med, High, which follow the screen), the style, and outline,
/// shadow and bevel on 0-10 steps of half a CD+G pixel, applied live through <see cref="PlayerCore.CdgScaling"/>.
/// <c>--cdg-scale[=..]</c>, <c>--cdg-style=</c> and <c>--cdg-effects</c> set the starting values. Any player can bind
/// these; the WPF player shows them in the Video flyout.
/// </summary>
public partial class PlayerViewModel
{
    const int DefaultBevelSize = 2;
    bool _suppressCdgLookApply;

    /// <summary>The scales, None (the classic picture) first.</summary>
    public IReadOnlyList<CdgLookChoice<CdgScale>> CdgScales { get; } =
    [
        new(CdgScale.None, "None"),
        new(CdgScale.Low, "Low (2x)"),
        new(CdgScale.Medium, "Med (half screen)"),
        new(CdgScale.High, "High (screen)"),
    ];

    /// <summary>The styles.</summary>
    public IReadOnlyList<CdgLookChoice<CdgScaleStyle>> CdgStyles { get; } =
    [
        new(CdgScaleStyle.Pixel, "Pixel"),
        new(CdgScaleStyle.Clean, "Clean"),
        new(CdgScaleStyle.PixelArt, "Pixel art"),
    ];

    public RelayCommand ResetCdgLookCommand { get; private set; } = null!;

    public CdgLookChoice<CdgScale> CdgScaleChoice
    {
        get;
        // A list that loses its selection (null) keeps the current scale rather than turning scaling off.
        set => SetCdgLook(ref field, value ?? field ?? CdgScales[0]);
    } = null!;

    public CdgLookChoice<CdgScaleStyle> CdgStyleChoice
    {
        get;
        set => SetCdgLook(ref field, value ?? field ?? StyleChoice(PlayerStartupOptions.DefaultCdgStyle));
    } = null!;

    public int CdgOutlineSize
    {
        get;
        set => SetCdgLook(ref field, ToStep(value));
    }

    public int CdgShadowSize
    {
        get;
        set => SetCdgLook(ref field, ToStep(value));
    }

    public int CdgShadowDistance
    {
        get;
        set => SetCdgLook(ref field, ToStep(value));
    }

    public int CdgBevelStrength
    {
        get;
        set => SetCdgLook(ref field, ToStep(value));
    }

    public int CdgBevelSize
    {
        get;
        set => SetCdgLook(ref field, ToStep(value));
    } = DefaultBevelSize;

    void InitializeCdgLookControls()
    {
        ResetCdgLookCommand = new RelayCommand(ResetCdgLook);
        var options = Options.CdgScaling;
        _suppressCdgLookApply = true;
        CdgScaleChoice = CdgScales.FirstOrDefault(choice => choice.Value == options.Scale) ?? CdgScales[0];
        CdgStyleChoice = StyleChoice(options.Style);
        CdgOutlineSize = options.OutlineSize;
        CdgShadowSize = options.ShadowSize;
        CdgShadowDistance = options.ShadowDistance;
        CdgBevelStrength = options.BevelStrength;
        if (options.BevelSize > 0)
            CdgBevelSize = options.BevelSize;
        _suppressCdgLookApply = false;
    }

    CdgLookChoice<CdgScaleStyle> StyleChoice(CdgScaleStyle style) =>
        CdgStyles.FirstOrDefault(choice => choice.Value == style) ?? CdgStyles[0];

    /// <summary>The look as the player's options; the safety zone stays the startup one.</summary>
    CdgScalingOptions BuildCdgScaling() => Options.CdgScaling with
    {
        Scale = CdgScaleChoice.Value,
        Style = CdgStyleChoice.Value,
        OutlineSize = CdgOutlineSize,
        ShadowSize = CdgShadowSize,
        ShadowDistance = CdgShadowDistance,
        BevelStrength = CdgBevelStrength,
        BevelSize = CdgBevelSize,
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

    /// <summary>The default style with no outline, shadow or bevel; the scale stays as it is.</summary>
    void ResetCdgLook()
    {
        _suppressCdgLookApply = true;
        CdgStyleChoice = StyleChoice(PlayerStartupOptions.DefaultCdgStyle);
        CdgOutlineSize = 0;
        CdgShadowSize = 0;
        CdgShadowDistance = 0;
        CdgBevelStrength = 0;
        CdgBevelSize = DefaultBevelSize;
        _suppressCdgLookApply = false;
        ApplyCdgLook();
        StatusText = "CD+G look reset.";
    }

    static int ToStep(int value) => Math.Clamp(value, 0, CdgScalingOptions.MaximumEffect);
}

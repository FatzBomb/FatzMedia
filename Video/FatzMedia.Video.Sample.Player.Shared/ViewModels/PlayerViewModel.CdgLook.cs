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
/// The scaled CD+G look (ENG-064): one Enhance setting (Off, Low, Med, High; the multiples follow the screen) and outline,
/// shadow and bevel on 0-10 steps of half a CD+G pixel, applied live through <see cref="PlayerCore.CdgScaling"/>.
/// Off is plain pixels (the classic picture); Low, Med and High draw the Clean look. An outline, shadow or bevel needs
/// the scaled picture, so with Enhance Off and any of them on, the picture is drawn at 2x as plain pixels.
/// <c>--cdg-enhance=off|low|med|high</c> (or the older <c>--cdg-scale[=..]</c>) and <c>--cdg-effects</c> set the starting
/// values. Any player can bind these; the WPF player shows them in the Video flyout.
/// </summary>
public partial class PlayerViewModel
{
    const int DefaultBevelSize = 2;
    bool _suppressCdgLookApply;

    /// <summary>The Enhance levels, Off (plain pixels) first.</summary>
    public IReadOnlyList<CdgLookChoice<CdgScale>> CdgEnhances { get; } =
    [
        new(CdgScale.None, "Off"),
        new(CdgScale.Low, "Low"),
        new(CdgScale.Medium, "Med"),
        new(CdgScale.High, "High"),
    ];

    public RelayCommand ResetCdgLookCommand { get; private set; } = null!;

    public CdgLookChoice<CdgScale> CdgEnhanceChoice
    {
        get;
        // A list that loses its selection (null) keeps the current level rather than turning Enhance off.
        set => SetCdgLook(ref field, value ?? field ?? CdgEnhances[0]);
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
        CdgEnhanceChoice = CdgEnhances.FirstOrDefault(choice => choice.Value == options.Scale) ?? CdgEnhances[0];
        CdgOutlineSize = options.OutlineSize;
        CdgShadowSize = options.ShadowSize;
        CdgShadowDistance = options.ShadowDistance;
        CdgBevelStrength = options.BevelStrength;
        if (options.BevelSize > 0)
            CdgBevelSize = options.BevelSize;
        _suppressCdgLookApply = false;
    }

    bool CdgHasEffects => CdgOutlineSize > 0 || CdgShadowSize > 0 || CdgShadowDistance > 0 || CdgBevelStrength > 0;

    /// <summary>The look as the player's options; the safety zone stays the startup one.</summary>
    CdgScalingOptions BuildCdgScaling()
    {
        // Off is plain pixels; effects need the scaled picture, so they bring Off to 2x. Low and up are Clean.
        var scale = CdgEnhanceChoice.Value;
        if (scale == CdgScale.None && CdgHasEffects)
            scale = CdgScale.Low;
        return Options.CdgScaling with
        {
            Scale = scale,
            Style = CdgEnhanceChoice.Value == CdgScale.None ? CdgScaleStyle.Pixel : CdgScaleStyle.Clean,
            OutlineSize = CdgOutlineSize,
            ShadowSize = CdgShadowSize,
            ShadowDistance = CdgShadowDistance,
            BevelStrength = CdgBevelStrength,
            BevelSize = CdgBevelSize,
        };
    }

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

    /// <summary>No outline, shadow or bevel; Enhance stays as it is.</summary>
    void ResetCdgLook()
    {
        _suppressCdgLookApply = true;
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

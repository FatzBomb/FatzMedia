using CommunityToolkit.Mvvm.Input;

namespace FatzBomb.MediaControl.Player.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _autoHideSurfaces;
    private bool _includeOverlays;
    private bool _resetPitchOnMediaLoad;
    private bool _resetTempoOnMediaLoad;
    private double _redGain = 1d;
    private double _greenGain = 1d;
    private double _blueGain = 1d;
    private double _hueDegrees;
    private double _brightness;
    private double _contrast = 1d;
    private double _saturation = 1d;

    public string BaseVisualTransformState => CreateBaseVisualTransform().IsIdentity ? "IDENTITY" : "ACTIVE";

    public bool AutoHideSurfaces
    {
        get => _autoHideSurfaces;
        set => _ = SetProperty(ref _autoHideSurfaces, value);
    }

    public bool IncludeOverlays
    {
        get => _includeOverlays;
        set
        {
            if (!SetProperty(ref _includeOverlays, value)) return;
            ApplyBaseVisualTransform();
        }
    }

    public bool ResetPitchOnMediaLoad
    {
        get => _resetPitchOnMediaLoad;
        set
        {
            if (!SetProperty(ref _resetPitchOnMediaLoad, value))
            {
                return;
            }

            if (Player is { } player)
            {
                player.ResetPitchOnMediaLoad = value;
            }
        }
    }

    public bool ResetTempoOnMediaLoad
    {
        get => _resetTempoOnMediaLoad;
        set
        {
            if (!SetProperty(ref _resetTempoOnMediaLoad, value))
            {
                return;
            }

            if (Player is { } player)
            {
                player.ResetTempoOnMediaLoad = value;
            }
        }
    }

    public double RedGain
    {
        get => _redGain;
        set => SetTransformValue(ref _redGain, value, 0d, 2d, 1d, nameof(RedGain));
    }

    public double GreenGain
    {
        get => _greenGain;
        set => SetTransformValue(ref _greenGain, value, 0d, 2d, 1d, nameof(GreenGain));
    }

    public double BlueGain
    {
        get => _blueGain;
        set => SetTransformValue(ref _blueGain, value, 0d, 2d, 1d, nameof(BlueGain));
    }

    public double HueDegrees
    {
        get => _hueDegrees;
        set => SetTransformValue(ref _hueDegrees, value, -180d, 180d, 0d, nameof(HueDegrees));
    }

    public double Brightness
    {
        get => _brightness;
        set => SetTransformValue(ref _brightness, value, -0.25d, 0.25d, 0d, nameof(Brightness));
    }

    public double Contrast
    {
        get => _contrast;
        set => SetTransformValue(ref _contrast, value, 0.5d, 1.5d, 1d, nameof(Contrast));
    }

    public double Saturation
    {
        get => _saturation;
        set => SetTransformValue(ref _saturation, value, 0d, 2d, 1d, nameof(Saturation));
    }

    private void InitializeValidationControls()
    {
        _autoHideSurfaces = App.AutoHideSurfaces;
        _resetPitchOnMediaLoad = App.ResetPitchOnMediaLoad;
        _resetTempoOnMediaLoad = App.ResetTempoOnMediaLoad;

        if (!App.UseVisualTransformDemo)
        {
            return;
        }

        _redGain = 0.85d;
        _greenGain = 0.95d;
        _blueGain = 1.2d;
        _hueDegrees = 30d;
        _brightness = 0.05d;
        _contrast = 1.15d;
        _saturation = 1.2d;
    }

    private void ApplyValidationControls(FatzBomb.MediaControl.Wpf.Player player)
    {
        player.ResetPitchOnMediaLoad = ResetPitchOnMediaLoad;
        player.ResetTempoOnMediaLoad = ResetTempoOnMediaLoad;
        player.BaseVisualTransform = CreateBaseVisualTransform();
    }

    private void SetTransformValue(
        ref double field,
        double value,
        double minimum,
        double maximum,
        double fallback,
        string propertyName)
    {
        var normalized = double.IsNaN(value) ? fallback : Math.Clamp(value, minimum, maximum);
        if (!SetProperty(ref field, normalized, propertyName))
        {
            return;
        }

        ApplyBaseVisualTransform();
    }

    private void ApplyBaseVisualTransform()
    {
        OnPropertyChanged(nameof(BaseVisualTransformState));
        if (Player is { } player)
        {
            player.BaseVisualTransform = CreateBaseVisualTransform();
        }
    }

    private BaseVisualTransform CreateBaseVisualTransform() => new()
    {
        RedGain = (float)RedGain,
        GreenGain = (float)GreenGain,
        BlueGain = (float)BlueGain,
        HueDegrees = (float)HueDegrees,
        Brightness = (float)Brightness,
        Contrast = (float)Contrast,
        Saturation = (float)Saturation,
        IncludeOverlays = IncludeOverlays
    };

    [RelayCommand]
    private void ResetBaseVisualTransform()
    {
        _includeOverlays = false;
        _redGain = 1d;
        _greenGain = 1d;
        _blueGain = 1d;
        _hueDegrees = 0d;
        _brightness = 0d;
        _contrast = 1d;
        _saturation = 1d;

        OnPropertyChanged(nameof(IncludeOverlays));
        OnPropertyChanged(nameof(RedGain));
        OnPropertyChanged(nameof(GreenGain));
        OnPropertyChanged(nameof(BlueGain));
        OnPropertyChanged(nameof(HueDegrees));
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Saturation));
        ApplyBaseVisualTransform();
    }
}

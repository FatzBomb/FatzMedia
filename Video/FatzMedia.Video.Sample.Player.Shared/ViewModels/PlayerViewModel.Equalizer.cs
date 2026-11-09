using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>One slider of the 10-band graphic equalizer (Stage 020A) in the Audio flyout.</summary>
public sealed class EqualizerBandViewModel(string label, Action changed) : ObservableObject
{
    public const double MinimumGainDb = -12;
    public const double MaximumGainDb = 12;

    public string Label { get; } = label;

    public string ToolTipText => $"{Label} Hz band, {GainDb:+0;-0;0} dB";

    public double GainDb
    {
        get;
        set
        {
            if (!SetProperty(ref field, Math.Round(Math.Clamp(value, MinimumGainDb, MaximumGainDb), 1)))
                return;

            OnPropertyChanged(nameof(ToolTipText));
            changed();
        }
    }
}

/// <summary>
/// Validation-host equalizer: ten ISO octave bands plus an on/off switch, applied live through
/// <see cref="MediaControlPlayer.Equalizer"/>. <c>--eq=g1,...,g10</c> pre-sets the bands and enables it.
/// </summary>
public partial class PlayerViewModel
{
    private bool _suppressEqualizerApply;

    public IReadOnlyList<EqualizerBandViewModel> EqualizerBands { get; private set; } = [];

    public RelayCommand FlattenEqualizerCommand { get; private set; } = null!;

    public bool EqualizerEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                ApplyEqualizer();
        }
    }

    private void InitializeEqualizerControls()
    {
        EqualizerBands = [.. AudioEqualizerSettings.Graphic10BandCentersHz.Select(center => new EqualizerBandViewModel(FormatBandLabel(center), ApplyEqualizer))];
        FlattenEqualizerCommand = new RelayCommand(FlattenEqualizer);

        if (Options.EqualizerGains is not { } gains)
            return;

        _suppressEqualizerApply = true;
        for (var band = 0; band < EqualizerBands.Count && band < gains.Count; band++)
            EqualizerBands[band].GainDb = gains[band];

        EqualizerEnabled = true;
        _suppressEqualizerApply = false;
    }

    private AudioEqualizerSettings BuildEqualizerSettings() => EqualizerEnabled
        ? AudioEqualizerSettings.CreateGraphic10Band([.. EqualizerBands.Select(static band => band.GainDb)])
        : AudioEqualizerSettings.Disabled;

    private void ApplyEqualizer()
    {
        if (_suppressEqualizerApply || Player is not { } player)
            return;

        player.Equalizer = BuildEqualizerSettings();
    }

    private void FlattenEqualizer()
    {
        _suppressEqualizerApply = true;
        foreach (EqualizerBandViewModel band in EqualizerBands)
            band.GainDb = 0;

        _suppressEqualizerApply = false;
        ApplyEqualizer();
        StatusText = "Equalizer flattened.";
    }

    private static string FormatBandLabel(double centerHz) => centerHz >= 1000
        ? $"{centerHz / 1000:0.#}k"
        : $"{Math.Floor(centerHz):0}";
}

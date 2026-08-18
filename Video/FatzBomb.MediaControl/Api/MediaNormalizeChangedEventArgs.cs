#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public sealed class MediaNormalizeChangedEventArgs(
    bool previousValue,
    bool currentValue,
    double normalizationGain) : EventArgs
{
    public bool PreviousValue { get; } = previousValue;
    public bool CurrentValue { get; } = currentValue;
    public double NormalizationGain { get; } = normalizationGain;
}

namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// Collects the per-target results of presenting one composition and decides whether the frame
/// counts as presented, with the rule every renderer shares: when a primary target takes part,
/// success is the primary's present, so a secondary that presented never hides a failed primary
/// frame; with no primary, any presented target is success. A mutable value: keep it in a local.
/// </summary>
public struct CompositionPresentOutcome
{
    private bool _primaryExpected;
    private bool _primaryPresented;
    private bool _anyPresented;

    /// <summary>Records a target that took part in the present, and whether it presented.</summary>
    public void Record(bool isPrimary, bool presented)
    {
        _primaryExpected |= isPrimary;
        _primaryPresented |= isPrimary && presented;
        _anyPresented |= presented;
    }

    /// <summary>Gets whether the recorded targets make the frame a successful present.</summary>
    public readonly bool Succeeded => _primaryExpected ? _primaryPresented : _anyPresented;
}

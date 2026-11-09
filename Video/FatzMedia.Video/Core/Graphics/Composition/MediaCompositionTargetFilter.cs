namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// The presentation targets one composition is for (Stage 017A slice 5): display-locked video on
/// several monitors presents each display's surfaces at that display's own refreshes. A
/// composition without a filter goes to every target, as before.
/// </summary>
public sealed class MediaCompositionTargetFilter
{
    private readonly HashSet<string> _secondarySurfaceIds;

    public MediaCompositionTargetFilter(bool includesPrimary, IEnumerable<string> secondarySurfaceIds)
    {
        ArgumentNullException.ThrowIfNull(secondarySurfaceIds);
        IncludesPrimary = includesPrimary;
        _secondarySurfaceIds = new HashSet<string>(secondarySurfaceIds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets whether the primary surface is one of the targets.</summary>
    public bool IncludesPrimary { get; }

    /// <summary>Gets the ids of the secondary surfaces that are targets.</summary>
    public IReadOnlyCollection<string> SecondarySurfaceIds => _secondarySurfaceIds;

    /// <summary>Whether a target is included: the primary by role, a secondary by surface id.</summary>
    public bool Includes(bool isPrimary, string surfaceId) =>
        isPrimary ? IncludesPrimary : _secondarySurfaceIds.Contains(surfaceId);

    public override string ToString() =>
        $"{(IncludesPrimary ? "primary" : "no primary")}, secondaries [{string.Join(",", _secondarySurfaceIds)}]";
}

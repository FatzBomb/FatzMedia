using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// Uses the first usable renderer from an ordered list, for example Direct3D11 then the software
/// renderer (Stage 036A). A candidate implementing <see cref="IMediaPresentationAvailabilityProbe"/>
/// is usable when its probe says so; one without a probe always is. When no candidate is usable
/// the last one is used, so the host still gets a session and its diagnostics. The choice is made
/// once, on first use, and <see cref="Selection"/> records why.
/// </summary>
/// <remarks>
/// Hosts pair the surface and the decoding with the renderer before building them: the selected
/// candidate's <see cref="MediaPresentationAvailability.RequiresFrameSinkSurface"/> and
/// <see cref="MediaPresentationAvailability.RequiresSoftwareDecoding"/> say what it needs.
/// </remarks>
public sealed class FallbackPresentationCoordinatorFactory : IMediaPresentationCoordinatorFactory
{
    readonly Lazy<SelectionResult> _selection;

    public FallbackPresentationCoordinatorFactory(params IMediaPresentationCoordinatorFactory[] candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Length == 0)
            throw new ArgumentException("At least one renderer candidate is required.", nameof(candidates));
        if (Array.Exists(candidates, static candidate => candidate is null))
            throw new ArgumentException("Renderer candidates cannot be null.", nameof(candidates));

        IMediaPresentationCoordinatorFactory[] ordered = [.. candidates];
        _selection = new Lazy<SelectionResult>(() => Select(ordered));
    }

    /// <summary>Gets the renderer factory in use, choosing it on first access.</summary>
    public IMediaPresentationCoordinatorFactory Selected => _selection.Value.Selected;

    /// <summary>
    /// Gets each probed candidate in order, up to and including the selected one, with what its
    /// probe found. Candidates after the selected one are not probed.
    /// </summary>
    public IReadOnlyList<MediaPresentationCandidateResult> Selection => _selection.Value.Results;

    /// <summary>Gets what the selected renderer's probe reported, including what it needs.</summary>
    public MediaPresentationAvailability SelectedAvailability => _selection.Value.Results[^1].Availability;

    /// <summary>Gets the selected renderer's name.</summary>
    public string BackendName => Selected.BackendName;

    public IMediaPresentationCoordinator Create(
        IMediaEngine mediaEngine,
        MediaPlayerConfiguration configuration) => Selected.Create(mediaEngine, configuration);

    static SelectionResult Select(IMediaPresentationCoordinatorFactory[] candidates)
    {
        var results = new List<MediaPresentationCandidateResult>(candidates.Length);
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var availability = Probe(candidate);
            if (availability.IsAvailable)
            {
                results.Add(new MediaPresentationCandidateResult(candidate.BackendName, availability, IsSelected: true));
                return new SelectionResult(candidate, results);
            }

            results.Add(new MediaPresentationCandidateResult(candidate.BackendName, availability, IsSelected: false));
        }

        // Nothing is usable: keep the last candidate so the session still starts and reports its
        // own failure through the normal diagnostics.
        results[^1] = results[^1] with { IsSelected = true };
        return new SelectionResult(candidates[^1], results);
    }

    static MediaPresentationAvailability Probe(IMediaPresentationCoordinatorFactory candidate)
    {
        if (candidate is not IMediaPresentationAvailabilityProbe probe)
            return MediaPresentationAvailability.Available("no availability probe");

        try
        {
            return probe.ProbeAvailability();
        }
        catch (Exception ex)
        {
            return MediaPresentationAvailability.Unavailable($"probe failed: {ex.Message}");
        }
    }

    sealed record SelectionResult(
        IMediaPresentationCoordinatorFactory Selected,
        IReadOnlyList<MediaPresentationCandidateResult> Results);
}

/// <summary>One renderer candidate considered by <see cref="FallbackPresentationCoordinatorFactory"/>.</summary>
/// <param name="BackendName">The candidate's renderer name.</param>
/// <param name="Availability">What its probe reported (always available without a probe).</param>
/// <param name="IsSelected">Whether this candidate is the one in use.</param>
public sealed record MediaPresentationCandidateResult(
    string BackendName,
    MediaPresentationAvailability Availability,
    bool IsSelected)
{
    /// <summary>Gets whether the probe reported the renderer usable.</summary>
    public bool IsAvailable => Availability.IsAvailable;

    /// <summary>Gets what the probe found, or why the renderer cannot run.</summary>
    public string Detail => Availability.Detail;
}

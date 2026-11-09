#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// The presentation-path bookkeeping every adapter's media surface shares (ENG-039, from the
/// Stage 046A copies in the WPF, Avalonia and WinForms surfaces): a process-wide surface number
/// for diagnostics, the effective surface id rule, the current
/// <see cref="MediaSurfacePresentationPath"/> and its reason with the Info diagnostic owned by
/// the attached player, and the attach-refusal Warning raised once until the next successful
/// attach or session change.
/// </summary>
/// <remarks>
/// Not thread-safe: the adapter calls it on its UI thread. The adapter keeps its own public
/// properties and raises its own <c>PresentationPathChanged</c> event when <see cref="SetPath"/>
/// returns <see langword="true"/>.
/// </remarks>
public sealed class MediaSurfaceBookkeeping
{
    private static int s_nextSurfaceNumber;

    private readonly string _diagnosticCategory;
    private bool _attachRefusalReported;

    /// <param name="diagnosticCategory">The category of the surface's diagnostics (for example <c>Wpf.MediaSurface</c>).</param>
    public MediaSurfaceBookkeeping(string diagnosticCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCategory);
        _diagnosticCategory = diagnosticCategory;
        SurfaceNumber = Interlocked.Increment(ref s_nextSurfaceNumber);
    }

    /// <summary>Gets the surface's number in this process, for diagnostics.</summary>
    public int SurfaceNumber { get; }

    /// <summary>Gets how frames reach the screen now.</summary>
    public MediaSurfacePresentationPath Path { get; private set; }

    /// <summary>Gets why <see cref="Path"/> was chosen, or <see langword="null"/>.</summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// Gets the id a surface attaches with: <paramref name="surfaceId"/> trimmed, or when it is
    /// blank, <see cref="MediaPresentationSurfaceDescriptor.PrimarySurfaceId"/> for a primary
    /// surface and <paramref name="defaultSecondarySurfaceId"/> for any other role.
    /// </summary>
    public static string ResolveSurfaceId(string? surfaceId, MediaPresentationSurfaceRole role, string defaultSecondarySurfaceId) =>
        string.IsNullOrWhiteSpace(surfaceId)
            ? role == MediaPresentationSurfaceRole.Primary ? MediaPresentationSurfaceDescriptor.PrimarySurfaceId : defaultSecondarySurfaceId
            : surfaceId.Trim();

    /// <summary>Gets how diagnostics name the surface: <c>Surface #{number} '{id}'</c>.</summary>
    public string Describe(string effectiveSurfaceId) => $"Surface #{SurfaceNumber} '{effectiveSurfaceId}'";

    /// <summary>
    /// Records the surface's path and reason (an empty reason counts as none). A change to a path
    /// other than <see cref="MediaSurfacePresentationPath.None"/> raises an Info diagnostic owned
    /// by <paramref name="session"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the path or reason changed: the adapter raises its change event.</returns>
    public bool SetPath(MediaSurfacePresentationPath path, string? reason, IPlayerSession? session, string effectiveSurfaceId)
    {
        reason = string.IsNullOrEmpty(reason) ? null : reason;
        if (path == Path && reason == Reason)
            return false;

        Path = path;
        Reason = reason;
        if (path != MediaSurfacePresentationPath.None)
        {
            DiagnosticRouter.Raise(
                session,
                MediaDiagnosticLevel.Info,
                _diagnosticCategory,
                MediaSurfacePresentationPathText.DescribeChoice(Describe(effectiveSurfaceId), path, reason));
        }

        return true;
    }

    /// <summary>
    /// Raises the Warning that <paramref name="session"/> did not accept the surface, once until
    /// <see cref="ResetAttachRefusal"/> (layout retries the attach on every resize).
    /// </summary>
    public void ReportAttachRefused(IPlayerSession session, string effectiveSurfaceId)
    {
        if (_attachRefusalReported)
            return;

        _attachRefusalReported = true;
        DiagnosticRouter.Raise(
            session,
            MediaDiagnosticLevel.Warning,
            _diagnosticCategory,
            MediaSurfacePresentationPathText.DescribeAttachRefused(Describe(effectiveSurfaceId)));
    }

    /// <summary>Lets the next refusal warn again (after a successful attach or a session change).</summary>
    public void ResetAttachRefusal() => _attachRefusalReported = false;
}

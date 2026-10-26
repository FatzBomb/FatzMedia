namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Presentation-path reporting (Stage 046A), the same as the Avalonia and WinForms surfaces:
/// <see cref="PresentationPath"/>, its reason, a change event and an Info diagnostic owned by the
/// attached player, plus a Warning when the player does not accept the surface.
/// </summary>
public sealed partial class MediaSurface
{
    /// <summary>The diagnostic category of the surface's path decisions and attach problems.</summary>
    public const string DiagnosticCategory = "Wpf.MediaSurface";

    private static int s_nextSurfaceNumber;

    private readonly int _surfaceNumber = Interlocked.Increment(ref s_nextSurfaceNumber);
    private bool _attachRefusalReported;

    /// <summary>Raised on the UI thread when <see cref="PresentationPath"/> or its reason changes.</summary>
    public event EventHandler? PresentationPathChanged;

    /// <summary>
    /// Gets how frames reach the screen now: <see cref="MediaSurfacePresentationPath.NativeWindow"/>
    /// in <see cref="WpfPresentationMode.NativeHwndOverlay"/>, <see cref="MediaSurfacePresentationPath.FrameSink"/>
    /// in <see cref="WpfPresentationMode.WpfComposited"/>, <see cref="MediaSurfacePresentationPath.None"/>
    /// while detached.
    /// </summary>
    public MediaSurfacePresentationPath PresentationPath { get; private set; }

    /// <summary>Gets why the current path was chosen.</summary>
    public string? PresentationPathReason { get; private set; }

    private string DescribeSurface() => $"Surface #{_surfaceNumber} '{EffectiveSurfaceId}'";

    private void ReportAttachResult(IMediaControlSession session, bool attached)
    {
        if (attached)
        {
            _attachRefusalReported = false;
            MediaSurfacePresentationPath path = PresentationMode == WpfPresentationMode.WpfComposited
                ? MediaSurfacePresentationPath.FrameSink
                : MediaSurfacePresentationPath.NativeWindow;
            SetPresentationPath(path, $"PresentationMode is {PresentationMode}", session);
            return;
        }

        // Layout retries the attach on every resize: report a refusal once per session.
        if (_attachRefusalReported)
            return;

        _attachRefusalReported = true;
        DiagnosticRouter.Raise(
            session,
            MediaDiagnosticLevel.Warning,
            DiagnosticCategory,
            MediaSurfacePresentationPathText.DescribeAttachRefused(DescribeSurface()));
    }

    private void SetPresentationPath(MediaSurfacePresentationPath path, string? reason, IMediaControlSession? session)
    {
        reason = string.IsNullOrEmpty(reason) ? null : reason;
        if (path == PresentationPath && reason == PresentationPathReason)
            return;

        PresentationPath = path;
        PresentationPathReason = reason;
        if (path != MediaSurfacePresentationPath.None)
        {
            DiagnosticRouter.Raise(
                session,
                MediaDiagnosticLevel.Info,
                DiagnosticCategory,
                MediaSurfacePresentationPathText.DescribeChoice(DescribeSurface(), path, reason));
        }

        PresentationPathChanged?.Invoke(this, EventArgs.Empty);
    }
}

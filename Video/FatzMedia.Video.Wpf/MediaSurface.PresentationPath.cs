namespace FatzMedia.Video.Wpf;

/// <summary>
/// Presentation-path reporting (Stage 046A), the same as the Avalonia and WinForms surfaces
/// through Core's <see cref="MediaSurfaceBookkeeping"/>: <see cref="PresentationPath"/>, its
/// reason, a change event and an Info diagnostic owned by the attached player, plus a Warning when
/// the player does not accept the surface.
/// </summary>
public sealed partial class MediaSurface
{
    /// <summary>The diagnostic category of the surface's path decisions and attach problems.</summary>
    public const string DiagnosticCategory = "Wpf.MediaSurface";

    private readonly MediaSurfaceBookkeeping _bookkeeping = new(DiagnosticCategory);

    /// <summary>Raised on the UI thread when <see cref="PresentationPath"/> or its reason changes.</summary>
    public event EventHandler? PresentationPathChanged;

    /// <summary>
    /// Gets how frames reach the screen now: <see cref="MediaSurfacePresentationPath.NativeWindow"/>
    /// in <see cref="WpfPresentationMode.NativeHwndOverlay"/>, <see cref="MediaSurfacePresentationPath.FrameSink"/>
    /// in <see cref="WpfPresentationMode.WpfComposited"/>, <see cref="MediaSurfacePresentationPath.None"/>
    /// while detached.
    /// </summary>
    public MediaSurfacePresentationPath PresentationPath => _bookkeeping.Path;

    /// <summary>Gets why the current path was chosen.</summary>
    public string? PresentationPathReason => _bookkeeping.Reason;

    private string DescribeSurface() => _bookkeeping.Describe(EffectiveSurfaceId);

    private void ReportAttachResult(IMediaControlSession session, bool attached)
    {
        if (!attached)
        {
            _bookkeeping.ReportAttachRefused(session, EffectiveSurfaceId);
            return;
        }

        _bookkeeping.ResetAttachRefusal();
        MediaSurfacePresentationPath path = PresentationMode == WpfPresentationMode.WpfComposited
            ? MediaSurfacePresentationPath.FrameSink
            : MediaSurfacePresentationPath.NativeWindow;
        SetPresentationPath(path, $"PresentationMode is {PresentationMode}", session);
    }

    private void SetPresentationPath(MediaSurfacePresentationPath path, string? reason, IMediaControlSession? session)
    {
        if (_bookkeeping.SetPath(path, reason, session, EffectiveSurfaceId))
            PresentationPathChanged?.Invoke(this, EventArgs.Empty);
    }
}

using System.Windows;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Content auto-hide and the Audio-mode clear. The rules live in Core's
/// <see cref="MediaSurfaceContentTracker"/> (Stage 046A), shared with the Avalonia and WinForms
/// surfaces; this partial only shows or hides the internal presentation host on the UI thread.
/// </summary>
public sealed partial class MediaSurface
{
    private readonly MediaSurfaceContentTracker _contentTracker = new();
    private int _contentStateQueued;
    private bool _presentationCleared;

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.Register(
        nameof(AutoHide),
        typeof(bool),
        typeof(MediaSurface),
        new PropertyMetadata(true, OnAutoHideChanged));

    private static readonly DependencyPropertyKey IsContentVisiblePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsContentVisible),
            typeof(bool),
            typeof(MediaSurface),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsContentVisibleProperty =
        IsContentVisiblePropertyKey.DependencyProperty;

    /// <summary>
    /// Gets or sets whether the presentation host hides while no frame of the current media is
    /// shown (default <see langword="true"/>). It never changes this control's own
    /// <see cref="UIElement.Visibility"/>.
    /// </summary>
    public bool AutoHide
    {
        get => (bool)GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    /// <summary>Gets whether auto-hide shows the content now (always false while <see cref="AutoHide"/> is off).</summary>
    public bool IsContentVisible => (bool)GetValue(IsContentVisibleProperty);

    /// <summary>
    /// Gets whether the surface shows only its background because nothing presents to it: the
    /// session is in <see cref="MediaPlayerMode.Audio"/> mode (Stage 046A). The last video frame
    /// is not left on screen.
    /// </summary>
    public bool IsPresentationSuspended => _presentationCleared;

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface._contentTracker.AutoHide = e.NewValue is true;
        surface.ApplyContentState(forceVisibilityWrite: true);

        // The second screen hides and shows like this surface.
        if (surface._secondaryWindow is { } secondary)
            secondary.Surface.AutoHide = e.NewValue is true;
    }

    private void InitializeAutoHide()
    {
        _contentTracker.AutoHide = AutoHide;
        _contentTracker.Changed += CreateWeakContentHandler(this);
    }

    // The tracker follows the session while Player is set, also while this surface is unloaded.
    // The session must not keep an unloaded, dropped surface alive through it, so the tracker
    // holds the surface weakly; once the surface is collected, the tracker stops following.
    private static EventHandler CreateWeakContentHandler(MediaSurface surface)
    {
        var weakSurface = new WeakReference<MediaSurface>(surface);
        return (sender, e) =>
        {
            if (weakSurface.TryGetTarget(out MediaSurface? target))
                target.OnContentTrackerChanged(sender, e);
            else
                (sender as MediaSurfaceContentTracker)?.Dispose();
        };
    }

    // Raised on the thread that changed the state (often Core's render or event thread): marshal,
    // coalesce, then read the tracker's current state.
    private void OnContentTrackerChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyContentState(forceVisibilityWrite: false);
            return;
        }

        if (Interlocked.Exchange(ref _contentStateQueued, 1) != 0)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            Volatile.Write(ref _contentStateQueued, 0);
            ApplyContentState(forceVisibilityWrite: false);
        }));
    }

    private void ApplyContentState(bool forceVisibilityWrite)
    {
        var isVisible = _contentTracker.IsContentVisible;
        var visibilityChanged = IsContentVisible != isVisible;
        if (visibilityChanged)
            SetValue(IsContentVisiblePropertyKey, isVisible);

        UpdatePresentationCleared();
        if (AutoHide)
            ApplyAutoHideVisibility(forceVisibilityWrite);
        else
            UpdateNativeSurfaceVisibility();

        if (visibilityChanged)
            ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    // Audio mode (Stage 043A) keeps the surface attached, but whatever drew the last
    // frame is gone: the host hides (ShowsContent is false) and a composited host drops its frame.
    private void UpdatePresentationCleared()
    {
        var cleared = _contentTracker.Session is not null && !_contentTracker.HasPresentation;
        if (cleared == _presentationCleared)
            return;

        _presentationCleared = cleared;
        if (!cleared)
            return;

        _compositedHost?.Clear();
        DiagnosticRouter.Raise(
            _contentTracker.Session,
            MediaDiagnosticLevel.Info,
            DiagnosticCategory,
            MediaSurfacePresentationPathText.DescribeAudioModeCleared(DescribeSurface()));
    }

    private void ApplyAutoHideVisibility(bool forceVisibilityWrite)
    {
        var shouldEnableHost = _contentTracker.ShowsContent;
        var visibility = shouldEnableHost ? Visibility.Visible : Visibility.Hidden;
        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            if (_compositedHost != null && (forceVisibilityWrite || _compositedHost.Visibility != visibility))
                _compositedHost.Visibility = visibility;
            return;
        }

        if (_surfaceHost.Visibility != visibility)
            _surfaceHost.Visibility = visibility;
        _surfaceHost.SetNativeVisible(shouldEnableHost);
    }
}

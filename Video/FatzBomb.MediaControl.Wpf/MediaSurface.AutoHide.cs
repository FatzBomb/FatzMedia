using System.Windows;
using System.Windows.Threading;

namespace FatzBomb.MediaControl.Wpf;

public sealed partial class MediaSurface
{
    private readonly SurfaceAutoHidePolicy _autoHidePolicy = new();
    private int _autoHideFramePresented;
    private int _autoHideEnabled;
    private int _autoHideHasVisualContent;
    private int _autoHidePresentationQueued;
    private int _autoHideSessionGeneration;

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

    public bool AutoHide
    {
        get => (bool)GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    public bool IsContentVisible => (bool)GetValue(IsContentVisibleProperty);

    public event EventHandler? ContentVisibilityChanged;

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (MediaSurface)d;
        surface.InitializeAutoHide();
    }

    private void InitializeAutoHide()
    {
        Volatile.Write(ref _autoHideEnabled, AutoHide ? 1 : 0);
        ResetAutoHidePolicy(Player);
        if (AutoHide)
            ApplyAutoHideVisibility(forceVisibilityWrite: true);
    }

    private void OnAutoHideSessionChanged(IMediaControlSession? session)
    {
        _autoHideSessionGeneration++;
        _ = Interlocked.Exchange(ref _autoHideFramePresented, 0);
        Volatile.Write(ref _autoHideHasVisualContent, session?.HasVisualContent == true ? 1 : 0);
        _ = Interlocked.Exchange(ref _autoHidePresentationQueued, 0);
        ResetAutoHidePolicy(session);
    }

    private bool IsAutoHideEnabled => Volatile.Read(ref _autoHideEnabled) != 0;

    private bool IsAutoHidePresentationArmed =>
        _autoHidePolicy.State == SurfaceAutoHideState.PendingShow
        && Volatile.Read(ref _autoHideHasVisualContent) != 0;

    private void ResetAutoHidePolicy(IMediaControlSession? session)
    {
        var wasVisible = _autoHidePolicy.IsContentVisible;
        _ = _autoHidePolicy.Reset();
        if (AutoHide && session?.IsOpen == true)
        {
            _ = _autoHidePolicy.MediaLoaded();
            if (Volatile.Read(ref _autoHideHasVisualContent) != 0
                && Volatile.Read(ref _autoHideFramePresented) != 0)
            {
                _ = _autoHidePolicy.FramePresented();
            }
        }

        ApplyAutoHideTransition(wasVisible);
    }

    private void OnAutoHideMediaLoaded(object? sender, MediaLoadedEventArgs e)
    {
        Volatile.Write(
            ref _autoHideHasVisualContent,
            sender is IMediaControlSession { HasVisualContent: true } ? 1 : 0);
        var generation = Volatile.Read(ref _autoHideSessionGeneration);
        QueueAutoHideTransition(generation, () =>
        {
            _ = _autoHidePolicy.MediaLoaded();
            if (Volatile.Read(ref _autoHideHasVisualContent) != 0
                && Volatile.Read(ref _autoHideFramePresented) != 0)
            {
                _ = _autoHidePolicy.FramePresented();
            }
        });
    }

    private void OnAutoHideMediaClosed(object? sender, MediaClosedEventArgs e)
    {
        QueueAutoHideClosed();
        _ = Dispatcher.BeginInvoke(new Action(() => CoerceValue(PresentationModeProperty)));
    }

    private void OnAutoHideMediaPaused(object? sender, MediaPausedEventArgs e) =>
        QueueAutoHideTransportSignal(static policy => policy.MediaPaused());

    private void OnAutoHideMediaLooped(object? sender, MediaLoopedEventArgs e) =>
        QueueAutoHideTransportSignal(static policy => policy.MediaLooped());

    private void OnAutoHideMediaError(object? sender, MediaErrorEventArgs e) =>
        QueueAutoHideError();

    private void QueueAutoHideClosed()
    {
        Volatile.Write(ref _autoHideHasVisualContent, 0);
        QueueAutoHideHidden(SurfaceAutoHideHideReason.Closed);
    }

    private void QueueAutoHideError()
    {
        Volatile.Write(ref _autoHideHasVisualContent, 0);
        QueueAutoHideHidden(SurfaceAutoHideHideReason.Error);
    }

    private void QueueAutoHideHidden(SurfaceAutoHideHideReason reason)
    {
        _ = Interlocked.Exchange(ref _autoHideFramePresented, 0);
        var generation = Volatile.Read(ref _autoHideSessionGeneration);
        QueueAutoHideTransition(generation, () =>
        {
            _ = reason switch
            {
                SurfaceAutoHideHideReason.Stopped => _autoHidePolicy.MediaStopped(),
                SurfaceAutoHideHideReason.Closed => _autoHidePolicy.MediaClosed(),
                SurfaceAutoHideHideReason.Error => _autoHidePolicy.MediaError(),
                _ => false
            };
        });
    }

    private void QueueAutoHideMediaEnded(bool isLooping)
    {
        if (!isLooping)
            _ = Interlocked.Exchange(ref _autoHideFramePresented, 0);

        var generation = Volatile.Read(ref _autoHideSessionGeneration);
        QueueAutoHideTransition(generation, () => _ = _autoHidePolicy.MediaEnded(isLooping));
    }

    private void QueueAutoHideTransportSignal(Func<SurfaceAutoHidePolicy, bool> transition)
    {
        var generation = Volatile.Read(ref _autoHideSessionGeneration);
        QueueAutoHideTransition(generation, () => _ = transition(_autoHidePolicy));
    }

    private void QueueAutoHideFramePresented()
    {
        _ = Interlocked.Exchange(ref _autoHideFramePresented, 1);
        if (Interlocked.Exchange(ref _autoHidePresentationQueued, 1) != 0)
            return;

        var generation = Volatile.Read(ref _autoHideSessionGeneration);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _ = Interlocked.Exchange(ref _autoHidePresentationQueued, 0);
            if (generation != _autoHideSessionGeneration || !AutoHide)
                return;

            // A stale clear/present from the replaced item must not reveal a new
            // audio-only session. Visual capability is updated by MediaLoaded.
            if (Volatile.Read(ref _autoHideHasVisualContent) == 0)
                return;

            var wasVisible = _autoHidePolicy.IsContentVisible;
            _ = _autoHidePolicy.FramePresented();
            ApplyAutoHideTransition(wasVisible);
        }));
    }

    private void QueueAutoHideTransition(int generation, Action transition) =>
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != _autoHideSessionGeneration || !AutoHide)
                return;

            var wasVisible = _autoHidePolicy.IsContentVisible;
            transition();
            ApplyAutoHideTransition(wasVisible);
        }));

    private void ApplyAutoHideTransition(bool wasVisible)
    {
        var isVisible = _autoHidePolicy.IsContentVisible;
        if (wasVisible == isVisible)
        {
            if (!AutoHide)
                SetValue(IsContentVisiblePropertyKey, false);
            else
                ApplyAutoHideVisibility(forceVisibilityWrite: false);
            return;
        }

        SetValue(IsContentVisiblePropertyKey, isVisible);
        ApplyAutoHideVisibility(forceVisibilityWrite: false);
        ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAutoHideVisibility(bool forceVisibilityWrite)
    {
        if (!AutoHide)
            return;

        var shouldEnableHost = _autoHidePolicy.IsContentVisible || IsAutoHidePresentationArmed;
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

    private enum SurfaceAutoHideHideReason
    {
        Stopped,
        Closed,
        Error
    }
}

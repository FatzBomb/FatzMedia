using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBombMedia.MediaControl.Controllers;
using FatzMediaPlayer = FatzBombMedia.MediaControl.MediaPlayer;

namespace FatzBombMedia.MediaControl.Wpf;

/// <summary>
/// Drop-in WPF media surface for FatzBombMedia.
/// <para>
/// Hosts bind a <see cref="FatzBombMedia.MediaControl.MediaPlayer"/> to this control instead of manually wiring an
/// <see cref="ImageSource"/>. The control owns the WPF presentation surface and keeps the public
/// API independent from the underlying bitmap/D3DImage implementation.
/// </para>
/// </summary>
public sealed class FatzBombMediaSurface : Grid, IDisposable
{
    private readonly Grid _wpfSurfaceHost;
    private readonly Image _wpfSurface;
    private readonly NativeHwndSurfaceHost _nativeSurfaceHost;
    private Window? _overlayWindow;
    private Window? _overlayOwner;
    private bool _overlayBoundsUpdateQueued;
    private Rect _lastOverlayBounds = Rect.Empty;
    private bool _isSynchronizingPresentationProperties;
    private IDisposable? _surfaceAttachment;
    private MediaRenderer? _layoutSubscribedRenderer;
    private bool _layoutUpdateQueued;
    private bool _reconnectQueued;
    private bool _nativePresentationActive;
    private int _lastLayoutMediaWidth;
    private int _lastLayoutMediaHeight;
    private HwndSource? _topLevelHwndSource;
    private DispatcherTimer? _nativeResizeSettleTimer;
    private bool _nativeResizeInProgress;
    private bool _overlayHiddenForNativeResize;
    private bool _disposed;

    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private static readonly TimeSpan NativeResizeSettleDelay = TimeSpan.FromMilliseconds(120);

    public FatzBombMediaSurface()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        Focusable = false;

        _wpfSurfaceHost = new Grid
        {
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        _wpfSurface = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(_wpfSurface, BitmapScalingMode.HighQuality);

        _nativeSurfaceHost = new NativeHwndSurfaceHost
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _nativeSurfaceHost.NativeHandleChanged += OnNativeHandleChanged;
        _nativeSurfaceHost.NativeBoundsChanged += OnNativeBoundsChanged;

        _wpfSurfaceHost.Children.Add(_wpfSurface);
        Children.Add(_wpfSurfaceHost);
        Children.Add(_nativeSurfaceHost);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSurfaceSizeChanged;
        LayoutUpdated += OnLayoutUpdated;
        DataContextChanged += OnSurfaceDataContextChanged;
    }

    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(
        nameof(Player),
        typeof(FatzMediaPlayer),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(null, OnPlayerChanged));

    /// <summary>
    /// Gets or sets the player whose renderer should present into this surface.
    /// </summary>
    public FatzMediaPlayer? Player
    {
        get => (FatzMediaPlayer?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch),
        typeof(Stretch),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(Stretch.Uniform, OnStretchChanged));

    /// <summary>
    /// Gets or sets how the media surface should fit inside the control.
    /// </summary>
    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public static readonly DependencyProperty BitmapScalingModeProperty = DependencyProperty.Register(
        nameof(BitmapScalingMode),
        typeof(BitmapScalingMode),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(BitmapScalingMode.HighQuality, OnBitmapScalingModeChanged));

    /// <summary>
    /// Gets or sets the WPF bitmap scaling mode used only when the active presentation path exposes
    /// bitmap-backed content. D3DImage frames are already presented through the D3D surface.
    /// </summary>
    public BitmapScalingMode BitmapScalingMode
    {
        get => (BitmapScalingMode)GetValue(BitmapScalingModeProperty);
        set => SetValue(BitmapScalingModeProperty, value);
    }

    public static readonly DependencyProperty PresentationModeProperty = DependencyProperty.Register(
        nameof(PresentationMode),
        typeof(MediaPresentationMode),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(MediaPresentationMode.NativeHwndOverlay, OnPresentationModeChanged));

    /// <summary>
    /// Gets or sets the preferred presentation mode. The default is <see cref="MediaPresentationMode.NativeHwndOverlay"/>
    /// because native HWND/DXGI presentation with the owned overlay window is the high-performance
    /// product path. D3DImage is retained as an emergency/diagnostic fallback, not the normal path.
    /// </summary>
    public MediaPresentationMode PresentationMode
    {
        get => (MediaPresentationMode)GetValue(PresentationModeProperty);
        set => SetValue(PresentationModeProperty, value);
    }

    public static readonly DependencyProperty OverlayContentProperty = DependencyProperty.Register(
        nameof(OverlayContent),
        typeof(object),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(null, OnOverlayContentChanged));

    /// <summary>
    /// Gets or sets host-provided overlay content for <see cref="MediaPresentationMode.NativeHwndOverlay"/>.
    /// The content is hosted in a transparent owned WPF window so it can appear above the native video HWND.
    /// </summary>
    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    public static readonly DependencyProperty OverlayHitTestVisibleProperty = DependencyProperty.Register(
        nameof(OverlayHitTestVisible),
        typeof(bool),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(true, OnOverlayHitTestVisibleChanged));

    /// <summary>
    /// Gets or sets whether the transparent native-overlay window should receive mouse input.
    /// Leave this true for interactive overlay controls. Set it false for diagnostic/passive overlays
    /// so double-click and mouse gestures continue to reach the media surface.
    /// </summary>
    public bool OverlayHitTestVisible
    {
        get => (bool)GetValue(OverlayHitTestVisibleProperty);
        set => SetValue(OverlayHitTestVisibleProperty, value);
    }

    public static readonly DependencyProperty UseNativeHwndProperty = DependencyProperty.Register(
        nameof(UseNativeHwnd),
        typeof(bool),
        typeof(FatzBombMediaSurface),
        new PropertyMetadata(true, OnUseNativeHwndChanged));

    /// <summary>
    /// Legacy convenience bool for XAML checkboxes. False = WPF fallback path. True = native HWND overlay requested.
    /// Normal hosts should prefer PresentationMode=NativeHwndOverlay and OverlayContent.
    /// </summary>
    public bool UseNativeHwnd
    {
        get => (bool)GetValue(UseNativeHwndProperty);
        set => SetValue(UseNativeHwndProperty, value);
    }

    private static void OnPlayerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((FatzBombMediaSurface)d).ReconnectSurface();
    }

    private static void OnStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        surface.UpdatePresentationSurfaceLayout();
    }

    private static void OnBitmapScalingModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        RenderOptions.SetBitmapScalingMode(surface._wpfSurface, (BitmapScalingMode)e.NewValue);
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        var mode = (MediaPresentationMode)e.NewValue;
        surface.LogSurface($"PresentationMode={mode}");

        if (!surface._isSynchronizingPresentationProperties)
        {
            surface._isSynchronizingPresentationProperties = true;
            try
            {
                surface.SetCurrentValue(UseNativeHwndProperty, surface.IsNativePresentationMode(mode));
            }
            finally
            {
                surface._isSynchronizingPresentationProperties = false;
            }
        }

        surface.ReconnectSurface();
    }

    private static void OnUseNativeHwndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        if (!surface._isSynchronizingPresentationProperties)
        {
            surface._isSynchronizingPresentationProperties = true;
            try
            {
                surface.SetCurrentValue(PresentationModeProperty, (bool)e.NewValue ? MediaPresentationMode.NativeHwndOverlay : MediaPresentationMode.D3DImage);
            }
            finally
            {
                surface._isSynchronizingPresentationProperties = false;
            }
        }

        surface.ReconnectSurface();
    }

    private static void OnOverlayContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        surface.LogSurface(e.NewValue == null ? "Overlay content cleared" : "Overlay content attached");
        surface.UpdateOverlayContent();
        surface.UpdateOverlayVisibility();
    }

    private static void OnOverlayHitTestVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (FatzBombMediaSurface)d;
        surface.ApplyOverlayWindowStyles();
        surface.LogSurface($"OverlayHitTestVisible={(bool)e.NewValue}");
    }

    private void LogSurface(string message)
    {
        if (Player?.Configuration.EnableLogging == true)
        {
            Debug.WriteLine($"[FatzBombMediaSurface] {message}");
        }
    }

    private bool IsNativePresentationMode(MediaPresentationMode mode)
    {
        return mode == MediaPresentationMode.NativeHwnd || mode == MediaPresentationMode.NativeHwndOverlay;
    }

    private bool ShouldUseNativePresentation()
    {
        // Native presentation is now the normal visual path for video, CDG, still images, and
        // CDG backgrounds. The renderer's native compositor owns those pixels; WPF/D3DImage is
        // retained only when the host explicitly selects D3DImage/WPF fallback.
        return IsNativePresentationMode(PresentationMode);
    }

    private bool ShouldUseNativeOverlay()
    {
        return PresentationMode == MediaPresentationMode.NativeHwndOverlay && ShouldUseNativePresentation();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOverlayOwner();
        AttachNativeResizeHook();
        ReconnectSurface();
        ApplyPresentationMode();
        QueueOverlayBoundsUpdate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachNativeResizeHook();
        DisconnectSurface();
        HideOverlayWindow();
        DetachOverlayOwner();
    }

    private void ReconnectSurface()
    {
        if (_disposed || !IsLoaded)
        {
            return;
        }

        DisconnectSurface(resetNativeTarget: false);

        var renderer = Player?.Renderer;
        if (renderer == null)
        {
            _nativeSurfaceHost.SetPresenterOwnsPixels(false);
            return;
        }

        ApplyPresentationMode();

        if (ShouldUseNativePresentation())
        {
            LogSurface($"Connecting native target: mode={PresentationMode}");
            var hwnd = _nativeSurfaceHost.WindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                LogSurface("Native target requested, but child HWND is not ready yet");
                renderer.SetNativePresentationWindow(IntPtr.Zero);
                return;
            }

            SubscribeSurfaceLayout(renderer);
            renderer.SetNativePresentationWindow(hwnd);
            UpdatePresentationSurfaceLayout();
            UpdateOverlayVisibility();
            QueueOverlayBoundsUpdate();
            return;
        }

        UnsubscribeSurfaceLayout();
        SubscribeSurfaceLayout(renderer);
        renderer.SetNativePresentationWindow(IntPtr.Zero);
        renderer.SetWpfPresentationSize(GetWpfPresentationPixelWidth(renderer), GetWpfPresentationPixelHeight(renderer));
        _surfaceAttachment = renderer.AttachSurface(_wpfSurface, _wpfSurfaceHost);
        UpdatePresentationSurfaceLayout();
    }

    private void DisconnectSurface(bool resetNativeTarget = true)
    {
        _surfaceAttachment?.Dispose();
        _surfaceAttachment = null;
        UnsubscribeSurfaceLayout();

        if (resetNativeTarget)
        {
            _nativeSurfaceHost.SetPresenterOwnsPixels(false);
            Player?.Renderer.SetNativePresentationWindow(IntPtr.Zero);
        }
    }

    private void OnNativeHandleChanged(object? sender, EventArgs e)
    {
        if (!_disposed && IsLoaded && ShouldUseNativePresentation())
        {
            ReconnectSurface();
        }
    }

    private void OnNativeBoundsChanged(object? sender, EventArgs e)
    {
        if (_disposed || !IsLoaded || !ShouldUseNativePresentation())
        {
            return;
        }

        if (_nativeResizeInProgress)
        {
            QueueOverlayBoundsUpdate();
            return;
        }

        var renderer = Player?.Renderer;
        renderer?.SetNativePresentationSize(_nativeSurfaceHost.NativeClientWidth, _nativeSurfaceHost.NativeClientHeight);
        QueueOverlayBoundsUpdate();
    }

    private void AttachNativeResizeHook()
    {
        if (_topLevelHwndSource != null)
        {
            return;
        }

        var window = Window.GetWindow(this);
        if (window == null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _topLevelHwndSource = HwndSource.FromHwnd(handle);
        _topLevelHwndSource?.AddHook(OnTopLevelWindowMessage);
    }

    private void DetachNativeResizeHook()
    {
        if (_topLevelHwndSource != null)
        {
            _topLevelHwndSource.RemoveHook(OnTopLevelWindowMessage);
            _topLevelHwndSource = null;
        }

        _nativeResizeSettleTimer?.Stop();
        _nativeResizeSettleTimer = null;
    }

    private IntPtr OnTopLevelWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed || !ShouldUseNativePresentation())
        {
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                BeginNativeResizeFreeze();
                break;

            case WM_EXITSIZEMOVE:
                CompleteNativeResizeFreeze(force: true);
                break;
        }

        return IntPtr.Zero;
    }

    private void BeginNativeResizeFreeze()
    {
        if (_disposed || !IsLoaded || !ShouldUseNativePresentation())
        {
            return;
        }

        if (!_nativeResizeInProgress)
        {
            _nativeResizeInProgress = true;
            _nativeSurfaceHost.SetLiveResizeSuspended(true);
            Player?.Renderer.SetNativeResizeInProgress(true);

            // The transparent WPF overlay is an owned layered window.  Moving/resizing it
            // alongside a native DXGI child during manual window resize is expensive and
            // can leave small edge artifacts around the media surface.  Hide it during
            // live resize and restore it after the final settled bounds are known.
            if (_overlayWindow?.IsVisible == true)
            {
                _overlayHiddenForNativeResize = true;
                _overlayWindow.Hide();
            }

            LogSurface("Native resize freeze started");
        }
    }

    private void RestartNativeResizeSettleTimer()
    {
        _nativeResizeSettleTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = NativeResizeSettleDelay
        };

        _nativeResizeSettleTimer.Tick -= OnNativeResizeSettleTimerTick;
        _nativeResizeSettleTimer.Tick += OnNativeResizeSettleTimerTick;
        _nativeResizeSettleTimer.Stop();
        _nativeResizeSettleTimer.Start();
    }

    private void OnNativeResizeSettleTimerTick(object? sender, EventArgs e)
    {
        CompleteNativeResizeFreeze(force: true);
    }

    private void CompleteNativeResizeFreeze(bool force)
    {
        _nativeResizeSettleTimer?.Stop();

        if (!_nativeResizeInProgress && !force)
        {
            return;
        }

        if (_nativeResizeInProgress || force)
        {
            var wasInProgress = _nativeResizeInProgress;
            _nativeResizeInProgress = false;
            _nativeSurfaceHost.SetLiveResizeSuspended(false);
            Player?.Renderer.SetNativeResizeInProgress(false);
            UpdatePresentationSurfaceLayout();

            // Do not force UpdateLayout synchronously here.  That blocks the mouse-up/resize
            // completion path and makes the window appear to keep resizing after the user lets go.
            // Let WPF apply the final HwndHost size naturally, then apply one final native size
            // and refresh for static CDG/image content.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _nativeResizeInProgress || !ShouldUseNativePresentation())
                {
                    return;
                }

                Player?.Renderer.SetNativePresentationSize(_nativeSurfaceHost.NativeClientWidth, _nativeSurfaceHost.NativeClientHeight);
                Player?.Renderer.RefreshNativePresentation();
                QueueOverlayBoundsUpdate();
            }), DispatcherPriority.Render);

            if (_overlayHiddenForNativeResize)
            {
                _overlayHiddenForNativeResize = false;
                UpdateOverlayVisibility();
            }
            QueueOverlayBoundsUpdate();

            if (wasInProgress)
            {
                LogSurface("Native resize freeze ended");
            }
        }
        else
        {
            _nativeSurfaceHost.SetLiveResizeSuspended(false);
        }
    }

    private void ApplyPresentationMode()
    {
        if (_disposed)
        {
            return;
        }

        if (ShouldUseNativePresentation())
        {
            _nativePresentationActive = true;
            LogSurface($"Applying native presentation mode: {PresentationMode}");
            _wpfSurface.Source = null;
            _nativeSurfaceHost.Visibility = Visibility.Visible;
            _wpfSurfaceHost.Visibility = Visibility.Collapsed;
            UpdatePresentationSurfaceLayout();
            UpdateOverlayVisibility();
            QueueOverlayBoundsUpdate();
            return;
        }

        _nativePresentationActive = false;
        _nativeSurfaceHost.SetPresenterOwnsPixels(false);
        ResetNativeSurfaceHostLayout();
        LogSurface("Applying WPF fallback presentation mode");
        _nativeSurfaceHost.Visibility = Visibility.Collapsed;
        _wpfSurfaceHost.Visibility = Visibility.Visible;
        HideOverlayWindow();
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShouldUseNativePresentation())
        {
            // WM_ENTERSIZEMOVE is not enough for WPF/HwndHost: the control can receive
            // many layout/size changes without the top-level hook being in the expected state.
            // Treat native surface size changes as live-resize activity, keep only the final
            // size, and settle once changes stop. This prevents DXGI/CDG work from catching up
            // through every intermediate drag size after the mouse is released.
            BeginNativeResizeFreeze();
            // Keep the native viewport visually tracking the window during live resize.
            // DXGI ResizeBuffers and CDG uploads are still deferred, so Windows/DWM scales
            // the existing backbuffer while the renderer keeps video on the fast path.
            // This preserves the visible resize behavior without re-entering the expensive
            // CDG compositor path.
            UpdatePresentationSurfaceLayout();
            RestartNativeResizeSettleTimer();
            return;
        }

        UpdatePresentationSurfaceLayout();
        QueueOverlayBoundsUpdate();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_nativeResizeInProgress)
        {
            return;
        }

        if (ShouldUseNativeOverlay())
        {
            QueueOverlayBoundsUpdate();
        }
    }

    private void OnSurfaceDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.DataContext = DataContext;
        }
    }

    private void SubscribeSurfaceLayout(MediaRenderer renderer)
    {
        if (ReferenceEquals(_layoutSubscribedRenderer, renderer))
        {
            return;
        }

        UnsubscribeSurfaceLayout();
        _layoutSubscribedRenderer = renderer;
        renderer.FramePresented += OnRendererFramePresented;
        renderer.ImageSourceChanged += OnRendererImageSourceChanged;
        renderer.AttachmentsChanged += OnRendererAttachmentsChanged;
    }

    private void UnsubscribeSurfaceLayout()
    {
        if (_layoutSubscribedRenderer != null)
        {
            _layoutSubscribedRenderer.FramePresented -= OnRendererFramePresented;
            _layoutSubscribedRenderer.ImageSourceChanged -= OnRendererImageSourceChanged;
            _layoutSubscribedRenderer.AttachmentsChanged -= OnRendererAttachmentsChanged;
            _layoutSubscribedRenderer = null;
        }

        _layoutUpdateQueued = false;
        _reconnectQueued = false;
    }

    private void OnRendererImageSourceChanged(object? sender, EventArgs e)
    {
        if (!_nativePresentationActive)
        {
            return;
        }

        // When playback is cleared or a file switch destroys the native swap-chain,
        // there may be no immediate frame to repaint the HWND.  Return ownership to
        // the native host so WM_PAINT fills black instead of leaving an uninitialized
        // gray/white client area behind.  The next real D3D present flips this back
        // to true in OnRendererFramePresented.
        var renderer = _layoutSubscribedRenderer;
        if (renderer?.ImageSource == null
            && string.Equals(renderer.PresentationBackend, "None", StringComparison.OrdinalIgnoreCase))
        {
            _nativeSurfaceHost.SetPresenterOwnsPixels(false);
        }
    }

    private void OnRendererAttachmentsChanged(object? sender, EventArgs e)
    {
        if (_nativePresentationActive)
        {
            // Attachment refresh usually means old D3D resources were cleared.
            // Paint black until the new media presents a real frame.
            _nativeSurfaceHost.SetPresenterOwnsPixels(false);
        }

        QueueReconnectSurface();
    }

    private void QueueReconnectSurface()
    {
        if (_disposed || _reconnectQueued)
        {
            return;
        }

        _reconnectQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _reconnectQueued = false;
            ReconnectSurface();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnRendererFramePresented(object? sender, EventArgs e)
    {
        if (_nativePresentationActive != ShouldUseNativePresentation())
        {
            QueueReconnectSurface();
            return;
        }

        if (_nativePresentationActive)
        {
            // Once DXGI has presented at least one real frame, the native child HWND owns
            // its pixels. From this point on WM_PAINT/WM_ERASEBKGND must not draw GDI
            // black frames between swap-chain presents during live resize.
            _nativeSurfaceHost.SetPresenterOwnsPixels(true);
        }

        // FramePresented fires for every video frame. Layout only needs to update when media
        // dimensions first become known or change between files. Updating on every frame caused
        // unnecessary WPF/D3DImage size checks during playback and made resize behavior noisy.
        if (_nativeResizeInProgress)
        {
            return;
        }

        var renderer = _layoutSubscribedRenderer;
        var mediaWidth = renderer?.VideoWidth ?? 0;
        var mediaHeight = renderer?.VideoHeight ?? 0;
        if (mediaWidth != _lastLayoutMediaWidth || mediaHeight != _lastLayoutMediaHeight)
        {
            QueuePresentationSurfaceLayoutUpdate();
        }
    }

    private void QueuePresentationSurfaceLayoutUpdate()
    {
        if (_disposed || _layoutUpdateQueued)
        {
            return;
        }

        _layoutUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutUpdateQueued = false;
            UpdatePresentationSurfaceLayout();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void UpdatePresentationSurfaceLayout()
    {
        if (_disposed)
        {
            return;
        }

        var renderer = Player?.Renderer;
        var mediaWidth = renderer?.VideoWidth ?? 0;
        var mediaHeight = renderer?.VideoHeight ?? 0;
        _lastLayoutMediaWidth = mediaWidth;
        _lastLayoutMediaHeight = mediaHeight;

        if (ShouldUseNativePresentation())
        {
            ApplyNativeSurfaceHostLayout(mediaWidth, mediaHeight);
            _nativeSurfaceHost.SetMediaLayout(mediaWidth, mediaHeight, Stretch);
            renderer?.SetNativePresentationStretch(Stretch);

            if (!_nativeResizeInProgress)
            {
                renderer?.SetNativePresentationSize(_nativeSurfaceHost.NativeClientWidth, _nativeSurfaceHost.NativeClientHeight);
            }

            QueueOverlayBoundsUpdate();
            return;
        }

        ApplyWpfSurfaceLayout(renderer, mediaWidth, mediaHeight);
    }

    private void ApplyNativeSurfaceHostLayout(int mediaWidth, int mediaHeight)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || mediaWidth <= 0 || mediaHeight <= 0)
        {
            ResetNativeSurfaceHostLayout();
            return;
        }

        // During resize freeze, the renderer intentionally does not redraw every intermediate
        // size. If the native HWND continues to fill the whole control, Windows/DWM stretches
        // the last presented swap-chain frame to the transient window shape, which visibly
        // distorts aspect ratio. Size the HwndHost element itself to the same Uniform/None
        // rectangle WPF would use, and let the parent Grid's black background provide the
        // letterbox/pillarbox area. WPF owns the HwndHost position, so this does not reintroduce
        // the old manual SetWindowPos-to-(0,0) jitter.
        if (Stretch is not (Stretch.Uniform or Stretch.None))
        {
            ResetNativeSurfaceHostLayout();
            return;
        }

        var bounds = CalculateFitBounds(ActualWidth, ActualHeight, mediaWidth, mediaHeight, Stretch, includeOffsets: false);
        var width = Math.Max(1.0, bounds.width);
        var height = Math.Max(1.0, bounds.height);

        _nativeSurfaceHost.HorizontalAlignment = HorizontalAlignment.Center;
        _nativeSurfaceHost.VerticalAlignment = VerticalAlignment.Center;
        _nativeSurfaceHost.Width = width;
        _nativeSurfaceHost.Height = height;
    }

    private void ResetNativeSurfaceHostLayout()
    {
        _nativeSurfaceHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _nativeSurfaceHost.VerticalAlignment = VerticalAlignment.Stretch;
        _nativeSurfaceHost.Width = double.NaN;
        _nativeSurfaceHost.Height = double.NaN;
    }

    private void ApplyWpfSurfaceLayout(MediaRenderer? renderer, int mediaWidth, int mediaHeight)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || mediaWidth <= 0 || mediaHeight <= 0)
        {
            _wpfSurface.Width = double.NaN;
            _wpfSurface.Height = double.NaN;
            _wpfSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
            _wpfSurface.VerticalAlignment = VerticalAlignment.Stretch;
            _wpfSurface.Stretch = Stretch.Fill;
            renderer?.SetWpfPresentationSize(0, 0);
            return;
        }

        var bounds = CalculateFitBounds(ActualWidth, ActualHeight, mediaWidth, mediaHeight, Stretch, includeOffsets: false);

        _wpfSurface.HorizontalAlignment = HorizontalAlignment.Center;
        _wpfSurface.VerticalAlignment = VerticalAlignment.Center;
        _wpfSurface.Width = Math.Max(1.0, bounds.width);
        _wpfSurface.Height = Math.Max(1.0, bounds.height);
        _wpfSurface.Stretch = Stretch.Fill;

        renderer?.SetWpfPresentationSize(GetWpfPresentationPixelWidth(renderer), GetWpfPresentationPixelHeight(renderer));
    }

    private int GetWpfPresentationPixelWidth(MediaRenderer? renderer)
    {
        var mediaWidth = renderer?.VideoWidth ?? 0;
        var mediaHeight = renderer?.VideoHeight ?? 0;
        if (ActualWidth <= 0 || ActualHeight <= 0 || mediaWidth <= 0 || mediaHeight <= 0)
        {
            return 0;
        }

        var bounds = CalculateFitBounds(ActualWidth, ActualHeight, mediaWidth, mediaHeight, Stretch, includeOffsets: false);
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(1, (int)Math.Round(bounds.width * dpi.DpiScaleX));
    }

    private int GetWpfPresentationPixelHeight(MediaRenderer? renderer)
    {
        var mediaWidth = renderer?.VideoWidth ?? 0;
        var mediaHeight = renderer?.VideoHeight ?? 0;
        if (ActualWidth <= 0 || ActualHeight <= 0 || mediaWidth <= 0 || mediaHeight <= 0)
        {
            return 0;
        }

        var bounds = CalculateFitBounds(ActualWidth, ActualHeight, mediaWidth, mediaHeight, Stretch, includeOffsets: false);
        var dpi = VisualTreeHelper.GetDpi(this);
        return Math.Max(1, (int)Math.Round(bounds.height * dpi.DpiScaleY));
    }

    private static (double x, double y, double width, double height) CalculateFitBounds(
        double hostWidth,
        double hostHeight,
        int mediaWidth,
        int mediaHeight,
        Stretch stretch,
        bool includeOffsets)
    {
        hostWidth = Math.Max(1.0, hostWidth);
        hostHeight = Math.Max(1.0, hostHeight);

        if (mediaWidth <= 0 || mediaHeight <= 0 || stretch == Stretch.Fill)
        {
            return (0, 0, hostWidth, hostHeight);
        }

        var scaleX = hostWidth / mediaWidth;
        var scaleY = hostHeight / mediaHeight;
        var scale = stretch switch
        {
            Stretch.None => 1.0,
            Stretch.UniformToFill => Math.Max(scaleX, scaleY),
            _ => Math.Min(scaleX, scaleY)
        };

        var width = Math.Max(1.0, mediaWidth * scale);
        var height = Math.Max(1.0, mediaHeight * scale);

        if (stretch == Stretch.None)
        {
            width = Math.Min(width, hostWidth);
            height = Math.Min(height, hostHeight);
        }

        var x = includeOffsets ? (hostWidth - width) / 2.0 : 0.0;
        var y = includeOffsets ? (hostHeight - height) / 2.0 : 0.0;
        return (x, y, width, height);
    }


    private void AttachOverlayOwner()
    {
        var owner = Window.GetWindow(this);
        if (ReferenceEquals(_overlayOwner, owner))
        {
            return;
        }

        DetachOverlayOwner();
        _overlayOwner = owner;
        if (_overlayOwner != null)
        {
            _overlayOwner.LocationChanged += OnOverlayOwnerMoved;
            _overlayOwner.SizeChanged += OnOverlayOwnerMoved;
            _overlayOwner.StateChanged += OnOverlayOwnerStateChanged;
            _overlayOwner.Activated += OnOverlayOwnerActivated;
            _overlayOwner.Deactivated += OnOverlayOwnerDeactivated;
            _overlayOwner.Closed += OnOverlayOwnerClosed;
        }

        if (_overlayWindow != null)
        {
            _overlayWindow.Owner = _overlayOwner;
        }
    }

    private void DetachOverlayOwner()
    {
        if (_overlayOwner == null)
        {
            return;
        }

        _overlayOwner.LocationChanged -= OnOverlayOwnerMoved;
        _overlayOwner.SizeChanged -= OnOverlayOwnerMoved;
        _overlayOwner.StateChanged -= OnOverlayOwnerStateChanged;
        _overlayOwner.Activated -= OnOverlayOwnerActivated;
        _overlayOwner.Deactivated -= OnOverlayOwnerDeactivated;
        _overlayOwner.Closed -= OnOverlayOwnerClosed;
        _overlayOwner = null;
    }

    private void OnOverlayOwnerMoved(object? sender, EventArgs e) => QueueOverlayBoundsUpdate();

    private void OnOverlayOwnerActivated(object? sender, EventArgs e) => UpdateOverlayVisibility();

    private void OnOverlayOwnerDeactivated(object? sender, EventArgs e)
    {
        // Keep the owned overlay visible while the owner is deactivated. Owned windows remain above
        // their owner and should not disappear just because focus moved to a menu/dialog.
        UpdateOverlayVisibility();
    }

    private void OnOverlayOwnerStateChanged(object? sender, EventArgs e) => UpdateOverlayVisibility();

    private void OnOverlayOwnerClosed(object? sender, EventArgs e) => DestroyOverlayWindow();

    private void EnsureOverlayWindow()
    {
        if (_overlayWindow != null)
        {
            return;
        }

        AttachOverlayOwner();
        _overlayWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.Manual,
            Topmost = false,
            Focusable = false,
            DataContext = DataContext,
            Owner = _overlayOwner
        };
        _overlayWindow.SourceInitialized += OnOverlayWindowSourceInitialized;
        LogSurface("Native overlay window created");
        UpdateOverlayContent();
    }

    private void OnOverlayWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (_overlayWindow == null)
        {
            return;
        }

        ApplyOverlayWindowStyles();
    }

    private void ApplyOverlayWindowStyles()
    {
        if (_overlayWindow == null || PresentationSource.FromVisual(_overlayWindow) is not HwndSource source)
        {
            return;
        }

        var hwnd = source.Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;

        if (OverlayHitTestVisible)
        {
            exStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        _overlayWindow.IsHitTestVisible = OverlayHitTestVisible;
    }

    private void UpdateOverlayContent()
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.Content = OverlayContent;
        _overlayWindow.DataContext = DataContext;
        LogSurface(OverlayContent == null ? "Overlay window content cleared" : "Overlay window content set");
    }

    private void UpdateOverlayVisibility()
    {
        if (_disposed)
        {
            return;
        }

        var shouldShow = IsLoaded
            && IsVisible
            && ShouldUseNativeOverlay()
            && OverlayContent != null
            && _overlayOwner != null
            && _overlayOwner.WindowState != WindowState.Minimized;

        if (!shouldShow)
        {
            HideOverlayWindow();
            return;
        }

        EnsureOverlayWindow();
        QueueOverlayBoundsUpdate();
        if (_overlayWindow != null && !_overlayWindow.IsVisible)
        {
            _overlayWindow.Show();
            LogSurface("Native overlay window shown");
        }
    }

    private void HideOverlayWindow()
    {
        if (_overlayWindow?.IsVisible == true)
        {
            _overlayWindow.Hide();
            LogSurface("Native overlay window hidden");
        }
    }

    private void DestroyOverlayWindow()
    {
        if (_overlayWindow == null)
        {
            return;
        }

        _overlayWindow.SourceInitialized -= OnOverlayWindowSourceInitialized;
        _overlayWindow.Content = null;
        _overlayWindow.Close();
        _overlayWindow = null;
        LogSurface("Native overlay window destroyed");
    }

    private void QueueOverlayBoundsUpdate()
    {
        if (_disposed || _overlayBoundsUpdateQueued)
        {
            return;
        }

        if (_nativeResizeInProgress && ShouldUseNativeOverlay())
        {
            return;
        }

        _overlayBoundsUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _overlayBoundsUpdateQueued = false;
            UpdateOverlayBounds();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void UpdateOverlayBounds()
    {
        if (_overlayWindow == null || !_overlayWindow.IsVisible || !ShouldUseNativeOverlay())
        {
            return;
        }

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            HideOverlayWindow();
            return;
        }

        AttachOverlayOwner();
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            return;
        }

        var topLeftDevice = PointToScreen(new Point(0, 0));
        var bottomRightDevice = PointToScreen(new Point(ActualWidth, ActualHeight));
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(topLeftDevice);
        var bottomRight = fromDevice.Transform(bottomRightDevice);
        var bounds = new Rect(topLeft, bottomRight);

        if (bounds.Width < 1 || bounds.Height < 1)
        {
            HideOverlayWindow();
            return;
        }

        if (Math.Abs(bounds.X - _lastOverlayBounds.X) < 0.5
            && Math.Abs(bounds.Y - _lastOverlayBounds.Y) < 0.5
            && Math.Abs(bounds.Width - _lastOverlayBounds.Width) < 0.5
            && Math.Abs(bounds.Height - _lastOverlayBounds.Height) < 0.5)
        {
            return;
        }

        _lastOverlayBounds = bounds;
        _overlayWindow.Left = bounds.X;
        _overlayWindow.Top = bounds.Y;
        _overlayWindow.Width = bounds.Width;
        _overlayWindow.Height = bounds.Height;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSurfaceSizeChanged;
        LayoutUpdated -= OnLayoutUpdated;
        _nativeSurfaceHost.NativeHandleChanged -= OnNativeHandleChanged;
        _nativeSurfaceHost.NativeBoundsChanged -= OnNativeBoundsChanged;
        DataContextChanged -= OnSurfaceDataContextChanged;
        DetachNativeResizeHook();
        DisconnectSurface();
        DestroyOverlayWindow();
        _disposed = true;
    }

    private sealed class NativeHwndSurfaceHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int SS_BLACKRECT = 0x00000004;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_SIZE = 0x0005;
        private const int WM_SHOWWINDOW = 0x0018;
        private const int WM_WINDOWPOSCHANGED = 0x0047;

        private IntPtr _hostHwnd;
        private IntPtr _videoHwnd;
        private int _lastHostWidth;
        private int _lastHostHeight;
        private bool _boundsUpdateQueued;
        private int _mediaWidth;
        private int _mediaHeight;
        private Stretch _mediaStretch = Stretch.Uniform;
        private bool _presenterOwnsPixels;

        /// <summary>
        /// The single native HWND used as the DXGI swap-chain output target.
        /// Aspect ratio and letterboxing are handled inside the D3D renderer, VLC-style,
        /// instead of moving an aspect-sized child HWND during live resize.
        /// </summary>
        public IntPtr WindowHandle => _hostHwnd;

        public int NativeClientWidth => GetCurrentClientSize().width;

        public int NativeClientHeight => GetCurrentClientSize().height;

        public event EventHandler? NativeHandleChanged;
        public event EventHandler? NativeBoundsChanged;

        public void SetPresenterOwnsPixels(bool ownsPixels)
        {
            if (_presenterOwnsPixels == ownsPixels)
            {
                return;
            }

            _presenterOwnsPixels = ownsPixels;
            if (!ownsPixels)
            {
                RedrawHostBackground(forceSynchronous: true);
            }
        }

        public void SetLiveResizeSuspended(bool suspended)
        {
            // Retained for compatibility with older patches. The VLC-like native path no longer
            // freezes or suspends bounds updates during resize; it keeps one HWND filling the host
            // and lets the D3D renderer update viewport/letterboxing internally.
        }

        public void SetMediaLayout(int mediaWidth, int mediaHeight, Stretch stretch)
        {
            mediaWidth = Math.Max(0, mediaWidth);
            mediaHeight = Math.Max(0, mediaHeight);

            var mediaChanged = _mediaWidth != mediaWidth || _mediaHeight != mediaHeight || _mediaStretch != stretch;
            _mediaWidth = mediaWidth;
            _mediaHeight = mediaHeight;
            _mediaStretch = stretch;

            // Always refresh bounds. Parent/control size changes do not necessarily change the
            // media dimensions or Stretch value, but they do change the native HWND client rectangle.
            QueueApplyWindowBounds();
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            var hostWidth = Math.Max(1, (int)Math.Round(ActualWidth));
            var hostHeight = Math.Max(1, (int)Math.Round(ActualHeight));

            // Single native child HWND fills the full WPF allocation. The DXGI swap-chain owns
            // painting while media is active; letterbox/pillarbox black is drawn by D3D, not GDI.
            _hostHwnd = NativeMethods.CreateWindowEx(
                0,
                "static",
                string.Empty,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0,
                0,
                hostWidth,
                hostHeight,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            // Do not create an aspect-sized child video HWND. VLC-style resize keeps a single
            // HWND as the swap-chain target and updates the D3D viewport/quad placement instead.
            _videoHwnd = IntPtr.Zero;

            ApplyWindowBounds(immediate: true);
            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
            NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
            return new HandleRef(this, _hostHwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_videoHwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_videoHwnd);
                _videoHwnd = IntPtr.Zero;
            }

            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
            }

            _hostHwnd = IntPtr.Zero;
            NativeHandleChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            QueueApplyWindowBounds();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_ERASEBKGND)
            {
                // Before the first DXGI frame, paint black so an empty native surface is never white.
                // After DXGI owns the pixels, do what VLC does: suppress background erase so GDI
                // cannot inject black frames between swap-chain presents during live resize.
                if (!_presenterOwnsPixels && wParam != IntPtr.Zero)
                {
                    FillNativeBackground(hwnd, wParam);
                }

                handled = true;
                return new IntPtr(1);
            }

            if (msg == WM_PAINT)
            {
                if (NativeMethods.BeginPaint(hwnd, out var paint) != IntPtr.Zero)
                {
                    if (!_presenterOwnsPixels)
                    {
                        FillNativeBackground(hwnd, paint.Hdc);
                    }

                    NativeMethods.EndPaint(hwnd, ref paint);
                }

                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SIZE || msg == WM_WINDOWPOSCHANGED)
            {
                // WPF/HwndHost owns the actual SetWindowPos call for this HWND. Use the
                // resulting Win32 size notifications only to refresh renderer/output sizing.
                QueueApplyWindowBounds();
            }

            handled = false;
            return IntPtr.Zero;
        }

        private void QueueApplyWindowBounds()
        {
            if (_hostHwnd == IntPtr.Zero)
            {
                return;
            }

            if (_boundsUpdateQueued)
            {
                return;
            }

            _boundsUpdateQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _boundsUpdateQueued = false;
                ApplyWindowBounds(immediate: false);
            }), DispatcherPriority.Render);
        }

        private void ApplyWindowBounds(bool immediate)
        {
            if (_hostHwnd == IntPtr.Zero)
            {
                return;
            }

            // IMPORTANT: do not call SetWindowPos on the HWND returned from HwndHost.
            // WPF owns the position and size of that returned child window. Moving it to
            // (0,0) here makes it snap to the top-left of the parent/top-level window during
            // live resize, which shows as video jitter and host-window artifacts.
            var (hostWidth, hostHeight) = GetCurrentClientSize();
            if (hostWidth == _lastHostWidth && hostHeight == _lastHostHeight)
            {
                return;
            }

            _lastHostWidth = hostWidth;
            _lastHostHeight = hostHeight;
            NativeBoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        private (int width, int height) GetCurrentClientSize()
        {
            if (_hostHwnd != IntPtr.Zero && NativeMethods.GetClientRect(_hostHwnd, out var rect))
            {
                return (
                    Math.Max(1, rect.Right - rect.Left),
                    Math.Max(1, rect.Bottom - rect.Top));
            }

            return (
                Math.Max(1, (int)Math.Round(ActualWidth)),
                Math.Max(1, (int)Math.Round(ActualHeight)));
        }

        private void ApplyVideoVisibility()
        {
            // Single-HWND native presentation: the host HWND is always visible while native
            // presentation is active. Media visibility is controlled by renderer content, not by
            // showing/hiding an inner child window.
        }

        private void RedrawHostBackground(bool forceSynchronous = false)
        {
            if (_hostHwnd == IntPtr.Zero || _presenterOwnsPixels)
            {
                return;
            }

            NativeMethods.InvalidateRect(_hostHwnd, IntPtr.Zero, true);
            if (forceSynchronous)
            {
                NativeMethods.UpdateWindow(_hostHwnd);
            }
        }

        private (int x, int y, int width, int height) CalculateNativeBounds(int hostWidth, int hostHeight)
        {
            hostWidth = Math.Max(1, hostWidth);
            hostHeight = Math.Max(1, hostHeight);

            if (_mediaWidth <= 0 || _mediaHeight <= 0 || _mediaStretch == Stretch.Fill)
            {
                return (0, 0, hostWidth, hostHeight);
            }

            var scaleX = hostWidth / (double)_mediaWidth;
            var scaleY = hostHeight / (double)_mediaHeight;
            var scale = _mediaStretch switch
            {
                Stretch.None => 1.0,
                Stretch.UniformToFill => Math.Max(scaleX, scaleY),
                _ => Math.Min(scaleX, scaleY)
            };

            var width = Math.Max(1, (int)Math.Round(_mediaWidth * scale));
            var height = Math.Max(1, (int)Math.Round(_mediaHeight * scale));

            if (_mediaStretch == Stretch.None)
            {
                width = Math.Min(width, hostWidth);
                height = Math.Min(height, hostHeight);
            }

            var x = (hostWidth - width) / 2;
            var y = (hostHeight - height) / 2;
            return (x, y, width, height);
        }

        private static void FillNativeBackground(IntPtr hwnd, IntPtr hdc)
        {
            if (!NativeMethods.GetClientRect(hwnd, out var rect))
            {
                return;
            }

            var brush = NativeMethods.CreateSolidBrush(0x000000);
            if (brush == IntPtr.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.FillRect(hdc, ref rect, brush);
            }
            finally
            {
                NativeMethods.DeleteObject(brush);
            }
        }
    }
}

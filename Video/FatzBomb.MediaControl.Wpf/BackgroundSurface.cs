using FatzBomb.MediaControl.Core.Abstractions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Self-contained background player for solid colors, images, and silent looping video.
/// </summary>
public sealed class BackgroundSurface : Grid, IDisposable
{
    private readonly MediaSurface _surface;
    private readonly LightweightPlayerController _playerController;
    private readonly Border _colorLayer;
    private readonly ContentPresenter _solidOverlay;
    private bool _showingSolidColor = true;
    private int _sourceReloadQueued;
    private int _sourceReloadVersion;
    private bool _restoringPlatformFactory;
    private WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;
    private bool _disposed;

    public BackgroundSurface()
    {
        ClipToBounds = true;
        _surface = new MediaSurface();
        _surface.ContentVisibilityChanged += OnInnerContentVisibilityChanged;
        _playerController = new LightweightPlayerController(this, _surface, ConfigurePlayer);
        _playerController.MediaClosed += OnPlayerMediaClosed;
        _colorLayer = new Border { Background = new SolidColorBrush(BackgroundColor) };
        _ = Children.Add(_colorLayer);
        _ = Children.Add(_surface);
        _solidOverlay = new ContentPresenter
        {
            IsHitTestVisible = OverlayHitTestVisible
        };
        _ = Children.Add(_solidOverlay);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateOverlayRoute();
    }

    public BackgroundSurface(IMediaControlPlatformFactory platformFactory)
        : this()
    {
        PlatformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
    }

    public IMediaControlSession? Session => _playerController.Session;

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.Register(
        nameof(AutoHide),
        typeof(bool),
        typeof(BackgroundSurface),
        new PropertyMetadata(true, OnAutoHideChanged));

    private static readonly DependencyPropertyKey IsContentVisiblePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsContentVisible),
            typeof(bool),
            typeof(BackgroundSurface),
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

    public static readonly DependencyProperty PlatformFactoryProperty = DependencyProperty.Register(
        nameof(PlatformFactory),
        typeof(IMediaControlPlatformFactory),
        typeof(BackgroundSurface),
        new PropertyMetadata(null, OnPlatformFactoryChanged));

    public IMediaControlPlatformFactory? PlatformFactory
    {
        get => (IMediaControlPlatformFactory?)GetValue(PlatformFactoryProperty);
        set => SetValue(PlatformFactoryProperty, value);
    }

    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
        nameof(AutoPlay),
        typeof(bool),
        typeof(BackgroundSurface),
        new PropertyMetadata(true, OnAutoPlayChanged));

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public static readonly DependencyProperty PresentationModeProperty = DependencyProperty.Register(
        nameof(PresentationMode),
        typeof(WpfPresentationMode),
        typeof(BackgroundSurface),
        new PropertyMetadata(
            WpfPresentationMode.NativeHwndOverlay,
            OnPresentationModeChanged,
            CoercePresentationMode));

    public WpfPresentationMode PresentationMode
    {
        get => (WpfPresentationMode)GetValue(PresentationModeProperty);
        set => SetValue(PresentationModeProperty, value);
    }

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(object),
        typeof(BackgroundSurface),
        new PropertyMetadata(null, OnSourceChanged));

    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty BackgroundColorProperty = DependencyProperty.Register(
        nameof(BackgroundColor),
        typeof(Color),
        typeof(BackgroundSurface),
        new PropertyMetadata(Colors.Black, OnBackgroundColorChanged));

    public Color BackgroundColor
    {
        get => (Color)GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    public static readonly DependencyProperty OverlayContentProperty = DependencyProperty.Register(
        nameof(OverlayContent),
        typeof(object),
        typeof(BackgroundSurface),
        new PropertyMetadata(null, OnOverlayChanged));

    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    public static readonly DependencyProperty OverlayHitTestVisibleProperty = DependencyProperty.Register(
        nameof(OverlayHitTestVisible),
        typeof(bool),
        typeof(BackgroundSurface),
        new PropertyMetadata(false, OnOverlayChanged));

    public bool OverlayHitTestVisible
    {
        get => (bool)GetValue(OverlayHitTestVisibleProperty);
        set => SetValue(OverlayHitTestVisibleProperty, value);
    }

    public void SetColor(Color color)
    {
        SetCurrentValue(BackgroundColorProperty, color);
        SetCurrentValue(SourceProperty, color);
    }

    public Task<bool> LoadAsync(string path) => LoadMediaAsync(path, ++_sourceReloadVersion);

    public Task<bool> LoadAsync(ImageSource image) => LoadMediaAsync(image, ++_sourceReloadVersion);

    public Task<bool> PlayAsync() => _playerController.PlayAsync();
    public Task<bool> PauseAsync() => _playerController.PauseAsync();
    public Task<bool> StopAsync() => _playerController.StopAsync();

    private static void OnAutoPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BackgroundSurface)d)._playerController.SetAutoPlay(e.NewValue is true);

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (BackgroundSurface)d;
        surface._surface.AutoHide = e.NewValue is true;
        if (surface._showingSolidColor)
            surface._surface.Visibility = Visibility.Collapsed;
    }

    private static void OnPlatformFactoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (BackgroundSurface)d;
        if (surface._restoringPlatformFactory
            || ReferenceEquals(e.OldValue, e.NewValue)
            || !surface._playerController.HasPlayerOrPendingCreation)
        {
            return;
        }

        surface._restoringPlatformFactory = true;
        try
        {
            surface.SetCurrentValue(PlatformFactoryProperty, e.OldValue);
        }
        finally
        {
            surface._restoringPlatformFactory = false;
        }

        throw new InvalidOperationException("PlatformFactory can only be changed before the player is created.");
    }

    private static object CoercePresentationMode(DependencyObject d, object baseValue)
    {
        var surface = (BackgroundSurface)d;
        if (surface._playerController.IsMediaOpen)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[{nameof(BackgroundSurface)}] PresentationMode change deferred until media is closed.");
            return surface._appliedPresentationMode;
        }

        return baseValue;
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (BackgroundSurface)d;
        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface._playerController.SetPresentationMode(surface._appliedPresentationMode);
        surface.UpdateOverlayRoute();
    }

    private static void ConfigurePlayer(Player player)
    {
        player.LoopMedia = true;
        player.Effects.Volume = 0;
        player.VideoDisplayMode = VideoDisplayMode.Zoom;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BackgroundSurface)d).QueueSourceReload();

    private static void OnBackgroundColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (BackgroundSurface)d;
        Color color = (Color)e.NewValue;
        color.A = byte.MaxValue;
        control._colorLayer.Background = new SolidColorBrush(color);
    }

    private static void OnOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BackgroundSurface)d).UpdateOverlayRoute();

    private void QueueSourceReload()
    {
        if (_disposed || !IsLoaded)
            return;

        if (Interlocked.Exchange(ref _sourceReloadQueued, 1) == 1)
            return;

        _ = Dispatcher.BeginInvoke(async () =>
        {
            _sourceReloadQueued = 0;
            if (_disposed || !IsLoaded)
                return;

            try
            {
                _ = await ReloadSourceAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{nameof(BackgroundSurface)}] Reload failed: {ex.Message}");
            }
        });
    }

    private async Task<bool> ReloadSourceAsync()
    {
        // A newer Source can arrive while a load awaits: each reload checks it is still the
        // latest after every await, so a slow load never lands over a later one.
        var reload = ++_sourceReloadVersion;
        return Source switch
        {
            null => ShowSolidColor(BackgroundColor),
            Color color => ShowSolidColor(color),
            SolidColorBrush brush => ShowSolidColor(brush.Color),
            string path => await LoadMediaAsync(path, reload),
            ImageSource image => await LoadMediaAsync(image, reload),
            _ => throw new NotSupportedException($"Background source type {Source.GetType().Name} is not supported.")
        };
    }

    private bool ShowSolidColor(Color color)
    {
        color.A = byte.MaxValue;
        SetCurrentValue(BackgroundColorProperty, color);
        _playerController.ReleasePlayer();
        _showingSolidColor = true;
        _surface.Visibility = Visibility.Collapsed;
        _colorLayer.Visibility = Visibility.Visible;
        UpdateOverlayRoute();
        return true;
    }

    private bool IsCurrentReload(int reload) => !_disposed && reload == _sourceReloadVersion;

    private async Task<bool> LoadMediaAsync(string path, int reload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Player? player = await _playerController.EnsurePlayerAsync(PlatformFactory, AutoPlay, PresentationMode);
        if (player == null || !IsCurrentReload(reload))
            return false;

        PrepareMediaLayerForLoad();
        player.PlayOnLoad = false;
        var loaded = await player.LoadAsync(path, ignoreSidecar: true);
        if (!IsCurrentReload(reload))
            return false;

        var valid = FinalizeMediaLoad(player, loaded);
        if (valid && AutoPlay && player.Content.HasFlag(MediaContent.Video))
            _ = await player.PlayAsync();
        player.PlayOnLoad = AutoPlay;
        return valid;
    }

    private async Task<bool> LoadMediaAsync(ImageSource image, int reload)
    {
        ArgumentNullException.ThrowIfNull(image);
        Player? player = await _playerController.EnsurePlayerAsync(PlatformFactory, AutoPlay, PresentationMode);
        if (player == null || !IsCurrentReload(reload))
            return false;

        PrepareMediaLayerForLoad();
        player.PlayOnLoad = false;
        var loaded = await player.LoadAsync(image, ignoreSidecar: true);
        if (!IsCurrentReload(reload))
            return false;

        var valid = FinalizeMediaLoad(player, loaded);
        player.PlayOnLoad = AutoPlay;
        return valid;
    }

    private bool FinalizeMediaLoad(Player player, bool loaded)
    {
        var valid = loaded
            && (player.Content.HasFlag(MediaContent.Image) || player.Content.HasFlag(MediaContent.Video))
            && !player.Content.HasFlag(MediaContent.Cdg)
            && !player.Content.HasFlag(MediaContent.Subtitle);
        if (!valid)
        {
            if (loaded)
                player.Close();
            return false;
        }

        _showingSolidColor = false;
        _colorLayer.Visibility = Visibility.Visible;
        if (!AutoHide)
            _surface.Visibility = Visibility.Visible;
        UpdateOverlayRoute();
        return true;
    }

    private void PrepareMediaLayerForLoad() => _surface.Visibility = Visibility.Visible;

    private void OnInnerContentVisibilityChanged(object? sender, EventArgs e)
    {
        var isVisible = _surface.IsContentVisible;
        if (IsContentVisible == isVisible)
            return;

        SetValue(IsContentVisiblePropertyKey, isVisible);
        ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerMediaClosed(object? sender, EventArgs e) =>
        CoerceValue(PresentationModeProperty);

    private void UpdateOverlayRoute()
    {
        if (_showingSolidColor)
        {
            _surface.OverlayContent = null;
            _solidOverlay.Content = OverlayContent;
            _solidOverlay.IsHitTestVisible = OverlayHitTestVisible;
            _solidOverlay.Visibility = OverlayContent == null ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        _solidOverlay.Content = null;
        _solidOverlay.Visibility = Visibility.Collapsed;
        _surface.OverlayContent = OverlayContent;
        _surface.OverlayHitTestVisible = OverlayHitTestVisible;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        QueueSourceReload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _playerController.ReleasePlayer();

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        _sourceReloadQueued = 0;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _surface.ContentVisibilityChanged -= OnInnerContentVisibilityChanged;
        _playerController.MediaClosed -= OnPlayerMediaClosed;
        _playerController.Dispose();
    }
}

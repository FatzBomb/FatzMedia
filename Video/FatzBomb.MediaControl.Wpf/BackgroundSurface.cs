using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Self-contained background player for solid colors, images, and silent looping video.
/// </summary>
/// <remarks>
/// Stage 046A: the player runs in <see cref="Core.Configuration.MediaPlayerMode.Background"/> mode, so a background
/// never starts the audio engine (before, a Full-mode player was muted). The rules (what is
/// accepted, creation, reload and release) are Core's <see cref="LightweightPlayerPolicy"/> and
/// <see cref="LightweightPlayerController"/>, shared with the Avalonia and WinForms
/// <c>BackgroundSurface</c>. A solid colour is a fill, without a player.
/// </remarks>
public sealed class BackgroundSurface : Grid, IDisposable
{
    /// <summary>The diagnostic category of this control's own notices (Stage 046A).</summary>
    public const string DiagnosticCategory = "Wpf.BackgroundSurface";

    private readonly MediaSurface _surface;
    private readonly LightweightPlayerController _playerController;
    private readonly Border _colorLayer;
    private readonly ContentPresenter _solidOverlay;
    private bool _showingSolidColor = true;
    private bool _restoringPlatformFactory;
    private WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;
    private bool _disposed;

    public BackgroundSurface()
    {
        ClipToBounds = true;
        _surface = new MediaSurface();
        _surface.ContentVisibilityChanged += OnInnerContentVisibilityChanged;
        _playerController = new LightweightPlayerController(LightweightPlayerKind.Background, DiagnosticCategory);
        _playerController.PlayerChanged += OnPlayerChanged;
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

    /// <summary>
    /// Gets or sets the native library folders (BASS, FFmpeg) the control's player is created with;
    /// <see langword="null"/> (the default) uses <see cref="DLLPath"/> and the platform's default
    /// folders. Read when the player is created.
    /// </summary>
    public IReadOnlyList<string>? NativeLibraryPaths
    {
        get => _playerController.NativeLibraryPaths;
        set => _playerController.NativeLibraryPaths = value;
    }

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

    public Task<bool> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadMediaAsync(MediaInputSource.FromFile(path));
    }

    public Task<bool> LoadAsync(ImageSource image) => LoadMediaAsync(WpfMediaInputSources.FromImageSource(image));

    /// <summary>Loads a Core source (a file, a BGRA image such as <see cref="MediaInputSource.FromColor"/>, network video).</summary>
    public Task<bool> LoadAsync(MediaInputSource source) => LoadMediaAsync(source);

    public Task<bool> PlayAsync() => _playerController.PlayAsync();
    public Task<bool> PauseAsync() => _playerController.PauseAsync();
    public Task<bool> StopAsync() => _playerController.StopAsync();

    private static void OnAutoPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((BackgroundSurface)d)._playerController.AutoPlay = e.NewValue is true;

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
            if (!Equals(baseValue, surface._appliedPresentationMode))
            {
                DiagnosticRouter.Raise(
                    surface._playerController.Session,
                    MediaDiagnosticLevel.Info,
                    DiagnosticCategory,
                    $"PresentationMode {baseValue} is deferred until the media is closed.");
            }

            return surface._appliedPresentationMode;
        }

        return baseValue;
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (BackgroundSurface)d;
        if (surface._playerController.IsMediaOpen)
            throw new InvalidOperationException("PresentationMode can only be changed before media is loaded.");

        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface._surface.PresentationMode = surface._appliedPresentationMode;
        surface.UpdateOverlayRoute();
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
        if (_disposed || !IsLoaded || !_playerController.TryQueueReload())
            return;

        _ = Dispatcher.BeginInvoke(() => _playerController.RunQueuedReloadAsync(() => !_disposed && IsLoaded, ReloadSourceAsync));
    }

    private async Task<bool> ReloadSourceAsync()
    {
        // A newer Source abandons a load in progress: Core's controller checks after every await,
        // and a solid colour releases the player.
        return Source switch
        {
            null => ShowSolidColor(BackgroundColor),
            Color color => ShowSolidColor(color),
            SolidColorBrush brush => ShowSolidColor(brush.Color),
            string path => await LoadAsync(path),
            ImageSource image => await LoadAsync(image),
            MediaInputSource source => await LoadAsync(source),
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

    private async Task<bool> LoadMediaAsync(MediaInputSource source)
    {
        // The controller raises PlayerChanged where this call resumes: it must be the UI thread.
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DesignerProperties.GetIsInDesignMode(this))
            return false;

        var valid = await _playerController.LoadAsync(source, PlatformFactory, _ => PrepareMediaLayerForLoad());
        if (valid)
            ShowMediaLayer();

        return valid;
    }

    private void ShowMediaLayer()
    {
        _showingSolidColor = false;
        _colorLayer.Visibility = Visibility.Visible;
        if (!AutoHide)
            _surface.Visibility = Visibility.Visible;
        UpdateOverlayRoute();
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

    // UI thread: bind the adopted player, or unbind the released one before it is disposed.
    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        IMediaControlSession? session = _playerController.Session;
        if (session is not null)
            _surface.PresentationMode = PresentationMode;

        _surface.Player = session;
        if (session is null)
            CoerceValue(PresentationModeProperty);
    }

    // The player's event thread.
    private void OnPlayerMediaClosed(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            CoerceValue(PresentationModeProperty);
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
                CoerceValue(PresentationModeProperty);
        });
    }

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
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _playerController.MediaClosed -= OnPlayerMediaClosed;
        // Disposing releases the player: PlayerChanged unbinds the surface first.
        _playerController.Dispose();
        _playerController.PlayerChanged -= OnPlayerChanged;
        _surface.ContentVisibilityChanged -= OnInnerContentVisibilityChanged;
    }
}

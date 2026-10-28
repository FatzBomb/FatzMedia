using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Self-contained 4:3 karaoke player for CDG and MIDI/KAR media.
/// </summary>
/// <remarks>
/// Stage 046A: the rules (what is accepted, the player's settings, creation, reload and release)
/// are Core's <see cref="LightweightPlayerPolicy"/> and <see cref="LightweightPlayerController"/>,
/// shared with the Avalonia and WinForms <c>KaraokeSurface</c>; this control owns the WPF
/// properties, the 4:3 layout and the surface.
/// </remarks>
public sealed class KaraokeSurface : Grid, IDisposable
{
    /// <summary>The diagnostic category of this control's own notices (Stage 046A).</summary>
    public const string DiagnosticCategory = "Wpf.KaraokeSurface";

    private readonly MediaSurface _surface;
    private readonly LightweightPlayerController _playerController;
    private bool _restoringPlatformFactory;
    private WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;
    private bool _disposed;
    // The player holds a song the Source loaded (not one LoadAsync loaded directly).
    private bool _sourceLoaded;

    public KaraokeSurface()
    {
        ClipToBounds = true;
        Background = CreateOpaqueBrush(CanvasColor);
        _surface = new MediaSurface();
        _surface.ContentVisibilityChanged += OnInnerContentVisibilityChanged;
        _playerController = new LightweightPlayerController(LightweightPlayerKind.Karaoke, DiagnosticCategory);
        _playerController.PlayerChanged += OnPlayerChanged;
        _playerController.MediaClosed += OnPlayerMediaClosed;
        _ = Children.Add(_surface);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public KaraokeSurface(IMediaControlPlatformFactory platformFactory)
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
        typeof(KaraokeSurface),
        new PropertyMetadata(true, OnAutoHideChanged));

    private static readonly DependencyPropertyKey IsContentVisiblePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsContentVisible),
            typeof(bool),
            typeof(KaraokeSurface),
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
        typeof(KaraokeSurface),
        new PropertyMetadata(null, OnPlatformFactoryChanged));

    public IMediaControlPlatformFactory? PlatformFactory
    {
        get => (IMediaControlPlatformFactory?)GetValue(PlatformFactoryProperty);
        set => SetValue(PlatformFactoryProperty, value);
    }

    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
        nameof(AutoPlay),
        typeof(bool),
        typeof(KaraokeSurface),
        new PropertyMetadata(true, OnAutoPlayChanged));

    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    public static readonly DependencyProperty PresentationModeProperty = DependencyProperty.Register(
        nameof(PresentationMode),
        typeof(WpfPresentationMode),
        typeof(KaraokeSurface),
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
        typeof(string),
        typeof(KaraokeSurface),
        new PropertyMetadata(null, OnSourceChanged));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty CanvasColorProperty = DependencyProperty.Register(
        nameof(CanvasColor),
        typeof(Color),
        typeof(KaraokeSurface),
        new PropertyMetadata(Colors.Black, OnCanvasColorChanged));

    public Color CanvasColor
    {
        get => (Color)GetValue(CanvasColorProperty);
        set => SetValue(CanvasColorProperty, value);
    }

    public Task<bool> LoadAsync(string path) => LoadKaraokeAsync(path);

    public Task<bool> PlayAsync() => _playerController.PlayAsync();
    public Task<bool> PauseAsync() => _playerController.PauseAsync();
    public Task<bool> StopAsync() => _playerController.StopAsync();

    private static void OnAutoPlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d)._playerController.AutoPlay = e.NewValue is true;

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d)._surface.AutoHide = e.NewValue is true;

    private static void OnPlatformFactoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (KaraokeSurface)d;
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
        var surface = (KaraokeSurface)d;
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
        var surface = (KaraokeSurface)d;
        if (surface._playerController.IsMediaOpen)
            throw new InvalidOperationException("PresentationMode can only be changed before media is loaded.");

        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface._surface.PresentationMode = surface._appliedPresentationMode;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _ = base.ArrangeOverride(arrangeSize);
        (double x, double y, double width, double height) = KaraokeLayout.Fit(arrangeSize.Width, arrangeSize.Height);
        _surface.Arrange(new Rect(x, y, width, height));
        return arrangeSize;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d).QueueSourceReload();

    private static void OnCanvasColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d).Background = CreateOpaqueBrush((Color)e.NewValue);

    private void QueueSourceReload()
    {
        if (_disposed || !IsLoaded || !_playerController.TryQueueReload())
            return;

        _ = Dispatcher.BeginInvoke(async () =>
        {
            _playerController.ClearQueuedReload();
            if (_disposed || !IsLoaded)
                return;

            try
            {
                // Clearing the source releases the player, so the previous song does not keep
                // playing; a reload without one (Loaded) leaves a song LoadAsync loaded alone.
                if (!string.IsNullOrWhiteSpace(Source))
                {
                    _sourceLoaded = true;
                    _ = await LoadKaraokeAsync(Source);
                }
                else if (_sourceLoaded)
                {
                    _sourceLoaded = false;
                    _playerController.ReleasePlayer();
                }
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(
                    _playerController.Session,
                    MediaDiagnosticLevel.Warning,
                    DiagnosticCategory,
                    $"Reloading the source failed: {ex.Message}",
                    innerException: ex);
            }
        });
    }

    private Task<bool> LoadKaraokeAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The controller raises PlayerChanged where this call resumes: it must be the UI thread.
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DesignerProperties.GetIsInDesignMode(this))
            return Task.FromResult(false);

        // A newer Source abandons this load (Core's controller checks after every await).
        return _playerController.LoadAsync(path, PlatformFactory);
    }

    private static SolidColorBrush CreateOpaqueBrush(Color color)
    {
        color.A = byte.MaxValue;
        return new SolidColorBrush(color);
    }

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

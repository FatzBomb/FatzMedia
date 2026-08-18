using FatzBomb.MediaControl.Core.Abstractions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FatzBomb.MediaControl.Wpf;

/// <summary>
/// Self-contained 4:3 karaoke player for CDG and MIDI/KAR media.
/// </summary>
public sealed class KaraokeSurface : Grid, IDisposable
{
    private static readonly HashSet<string> MidiKaraokeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".kar", ".mid", ".midi", ".rmi"
    };

    private readonly MediaSurface _surface;
    private readonly LightweightPlayerController _playerController;
    private int _sourceReloadQueued;
    private bool _restoringPlatformFactory;
    private WpfPresentationMode _appliedPresentationMode = WpfPresentationMode.NativeHwndOverlay;
    private bool _disposed;

    public KaraokeSurface()
    {
        ClipToBounds = true;
        Background = CreateOpaqueBrush(CanvasColor);
        _surface = new MediaSurface();
        _surface.ContentVisibilityChanged += OnInnerContentVisibilityChanged;
        _playerController = new LightweightPlayerController(this, _surface, ConfigurePlayer);
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
        ((KaraokeSurface)d)._playerController.SetAutoPlay(e.NewValue is true);

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
            System.Diagnostics.Debug.WriteLine(
                $"[{nameof(KaraokeSurface)}] PresentationMode change deferred until media is closed.");
            return surface._appliedPresentationMode;
        }

        return baseValue;
    }

    private static void OnPresentationModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (KaraokeSurface)d;
        surface._appliedPresentationMode = (WpfPresentationMode)e.NewValue;
        surface._playerController.SetPresentationMode(surface._appliedPresentationMode);
    }

    private static void ConfigurePlayer(Player player)
    {
        player.LoopMedia = false;
        player.ShowMidiLyrics = true;
        player.VideoDisplayMode = VideoDisplayMode.Zoom;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _ = base.ArrangeOverride(arrangeSize);
        var width = arrangeSize.Width;
        var height = width * 0.75;
        if (height > arrangeSize.Height)
        {
            height = arrangeSize.Height;
            width = height * (4.0 / 3.0);
        }

        _surface.Arrange(new Rect(
            Math.Max(0, (arrangeSize.Width - width) / 2),
            Math.Max(0, (arrangeSize.Height - height) / 2),
            Math.Max(0, width),
            Math.Max(0, height)));
        return arrangeSize;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d).QueueSourceReload();

    private static void OnCanvasColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeSurface)d).Background = CreateOpaqueBrush((Color)e.NewValue);

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
                if (!string.IsNullOrWhiteSpace(Source))
                    _ = await LoadKaraokeAsync(Source);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{nameof(KaraokeSurface)}] Reload failed: {ex.Message}");
            }
        });
    }

    private async Task<bool> LoadKaraokeAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Player? player = await _playerController.EnsurePlayerAsync(PlatformFactory, AutoPlay, PresentationMode);
        if (player == null)
            return false;

        player.PlayOnLoad = false;
        var loaded = await player.LoadAsync(path, ignoreSidecar: true);
        var isMidiKaraoke = MidiKaraokeExtensions.Contains(System.IO.Path.GetExtension(path));
        var valid = loaded
            && !player.Content.HasFlag(MediaContent.Video)
            && (player.Content.HasFlag(MediaContent.Cdg)
                || (isMidiKaraoke && player.Content.HasFlag(MediaContent.Subtitle)));
        if (!valid && loaded)
            player.Close();
        else if (valid && AutoPlay)
            _ = await player.PlayAsync();

        player.PlayOnLoad = AutoPlay;
        return valid;
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

    private void OnPlayerMediaClosed(object? sender, EventArgs e) =>
        CoerceValue(PresentationModeProperty);

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

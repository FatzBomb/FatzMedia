using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// What <see cref="KaraokeSurface"/> and <see cref="BackgroundSurface"/> share (Stage 046A): Core's
/// <see cref="LightweightPlayerController"/> bound to an inner <see cref="MediaSurface"/>, the
/// platform factory rule, and the coalesced source reload on the UI thread. Attachment to the
/// visual tree stands for the WPF control's Loaded state: attached, the source loads; detached,
/// the player is released.
/// </summary>
internal sealed class LightweightSurfaceHost : IDisposable
{
    private readonly Func<Task> _reload;
    private IMediaControlPlatformFactory? _platformFactory;
    private bool _isAttached;
    private bool _isContentVisible;
    private bool _disposed;

    public LightweightSurfaceHost(MediaSurface surface, LightweightPlayerKind kind, string diagnosticCategory, Func<Task> reload)
    {
        _reload = reload;
        Surface = surface;
        Controller = new LightweightPlayerController(kind, diagnosticCategory);
        Controller.PlayerChanged += OnPlayerChanged;
        Surface.ContentVisibilityChanged += OnSurfaceContentVisibilityChanged;
    }

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    public MediaSurface Surface { get; }

    public LightweightPlayerController Controller { get; }

    public bool IsDisposed => _disposed;

    public bool IsContentVisible => _isContentVisible;

    public IMediaControlPlatformFactory? PlatformFactory
    {
        get => _platformFactory;
        set
        {
            if (ReferenceEquals(_platformFactory, value))
                return;

            if (Controller.HasPlayerOrPendingCreation)
                throw new InvalidOperationException("PlatformFactory can only be changed before the player is created.");

            _platformFactory = value;
        }
    }

    /// <summary>Schedules one reload of the source on the UI thread (several changes reload once).</summary>
    public void QueueReload()
    {
        if (_disposed || !_isAttached || !Controller.TryQueueReload())
            return;

        Dispatcher.UIThread.Post(RunQueuedReload);
    }

    public void OnAttachedToVisualTree()
    {
        _isAttached = true;
        QueueReload();
    }

    public void OnDetachedFromVisualTree()
    {
        _isAttached = false;
        if (!_disposed)
            Controller.ReleasePlayer();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        // Releases the player: PlayerChanged unbinds the surface first.
        Controller.Dispose();
        Controller.PlayerChanged -= OnPlayerChanged;
        Surface.ContentVisibilityChanged -= OnSurfaceContentVisibilityChanged;
    }

    private async void RunQueuedReload()
    {
        await Controller.RunQueuedReloadAsync(() => !_disposed && _isAttached, _reload);
    }

    // UI thread: bind the adopted player, or unbind the released one before it is disposed.
    private void OnPlayerChanged(object? sender, EventArgs e) => Surface.Player = Controller.Session;

    private void OnSurfaceContentVisibilityChanged(object? sender, EventArgs e)
    {
        var isVisible = Surface.IsContentVisible;
        if (isVisible == _isContentVisible)
            return;

        _isContentVisible = isVisible;
        ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}

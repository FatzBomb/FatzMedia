namespace FatzMedia.Video.WinForms;

/// <summary>
/// What <see cref="KaraokeSurface"/> and <see cref="BackgroundSurface"/> share (Stage 046A): Core's
/// <see cref="LightweightPlayerController"/> bound to an inner <see cref="MediaSurface"/>, the
/// platform factory rule, and the coalesced source reload on the UI thread. The control's handle
/// stands for the WPF control's Loaded state: created, the source loads; destroyed, the player is
/// released (not when the handle is only recreated).
/// </summary>
internal sealed class LightweightSurfaceHost : IDisposable
{
    private readonly Control _owner;
    private readonly Func<Task> _reload;
    private IPlatformFactory? _platformFactory;
    private bool _isContentVisible;
    private bool _reloadScheduled;
    private bool _reloadDropped;
    private bool _recreatingHandle;
    private bool _disposed;

    public LightweightSurfaceHost(Control owner, MediaSurface surface, LightweightPlayerKind kind, string diagnosticCategory, Func<Task> reload)
    {
        _owner = owner;
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

    public IPlatformFactory? PlatformFactory
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
        if (_disposed || !_owner.IsHandleCreated || !Controller.TryQueueReload())
            return;

        try
        {
            _ = _owner.BeginInvoke(new Action(RunQueuedReload));
            _reloadScheduled = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away; the next handle reloads.
            Controller.ClearQueuedReload();
        }
    }

    public void OnHandleCreated()
    {
        // A recreated handle (RecreateHandle: a style change of this control or its form) keeps
        // the player and its media; only a reload dropped with the old handle runs again.
        if (_recreatingHandle)
        {
            _recreatingHandle = false;
            var reloadDropped = _reloadDropped;
            _reloadDropped = false;
            if (!reloadDropped && Controller.Player is not null)
                return;
        }

        QueueReload();
    }

    public void OnHandleDestroyed()
    {
        // A reload queued on the old handle is dropped with it.
        _reloadDropped = _reloadScheduled;
        _reloadScheduled = false;
        Controller.ClearQueuedReload();
        if (_disposed)
            return;

        if (_owner.RecreatingHandle)
        {
            _recreatingHandle = true;
            return;
        }

        Controller.ReleasePlayer();
    }

    public void Dispose()
    {
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
        _reloadScheduled = false;
        await Controller.RunQueuedReloadAsync(() => !_disposed && _owner.IsHandleCreated, _reload);
    }

    // UI thread: bind the adopted player, or unbind the released one before it is disposed.
    private void OnPlayerChanged(object? sender, EventArgs e) => Surface.Player = Controller.Session;

    private void OnSurfaceContentVisibilityChanged(object? sender, EventArgs e)
    {
        var isVisible = Surface.IsContentVisible;
        if (isVisible == _isContentVisible)
            return;

        _isContentVisible = isVisible;
        ContentVisibilityChanged?.Invoke(_owner, EventArgs.Empty);
    }
}

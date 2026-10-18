using FatzBomb.MediaControl.Core.Configuration;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace FatzBomb.MediaControl.Wpf;

internal sealed class LightweightPlayerController(
    FrameworkElement owner,
    MediaSurface surface,
    Action<Player> configurePlayer) : IDisposable
{
    private Player? _player;
    private Task<Player?>? _playerCreationTask;
    private bool _disposed;

    public IMediaControlSession? Session => _player;
    public bool HasPlayerOrPendingCreation => _player != null || _playerCreationTask != null;
    public bool IsMediaOpen => _player?.IsOpen == true;

    public event EventHandler? MediaClosed;

    public async Task<Player?> EnsurePlayerAsync(
        IMediaControlPlatformFactory? platformFactory,
        bool autoPlay,
        WpfPresentationMode presentationMode)
    {
        owner.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (DesignerProperties.GetIsInDesignMode(owner))
            return null;

        if (_player != null)
            return _player;

        if (platformFactory == null)
        {
            Debug.WriteLine($"[{owner.GetType().Name}] PlatformFactory must be set before media is loaded.");
            return null;
        }

        var startedWhileLoaded = owner.IsLoaded;
        Task<Player?> creation = _playerCreationTask ??= CreatePlayerAsync(platformFactory);
        Player? player = await creation;

        // Concurrent callers await the same creation; continuations run on the dispatcher one at
        // a time, so the first to resume takes the result and the others return what it decided.
        if (!ReferenceEquals(_playerCreationTask, creation))
            return _player;

        _playerCreationTask = null;
        if (_disposed || (startedWhileLoaded && !owner.IsLoaded) || player == null)
        {
            player?.Dispose();
            return null;
        }

        _player = player;
        player.MediaClosed += OnPlayerMediaClosed;
        configurePlayer(player);
        player.PlayOnLoad = autoPlay;
        surface.PresentationMode = presentationMode;
        surface.Player = player;
        return player;
    }

    public void SetAutoPlay(bool autoPlay)
    {
        if (_player != null)
            _player.PlayOnLoad = autoPlay;
    }

    public void SetPresentationMode(WpfPresentationMode presentationMode)
    {
        if (IsMediaOpen)
            throw new InvalidOperationException("PresentationMode can only be changed before media is loaded.");

        surface.PresentationMode = presentationMode;
    }

    public Task<bool> PlayAsync() => _player?.PlayAsync() ?? Task.FromResult(false);
    public Task<bool> PauseAsync() => _player?.PauseAsync() ?? Task.FromResult(false);
    public Task<bool> StopAsync() => _player?.StopAsync() ?? Task.FromResult(false);

    public void ReleasePlayer()
    {
        owner.Dispatcher.VerifyAccess();
        surface.Player = null;
        Player? player = _player;
        _player = null;
        if (player != null)
            player.MediaClosed -= OnPlayerMediaClosed;
        player?.Dispose();
        MediaClosed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlayerMediaClosed(object? sender, MediaClosedEventArgs e)
    {
        if (_disposed)
            return;

        if (owner.Dispatcher.CheckAccess())
        {
            MediaClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
                MediaClosed?.Invoke(this, EventArgs.Empty);
        }));
    }

    private async Task<Player?> CreatePlayerAsync(IMediaControlPlatformFactory platformFactory)
    {
        try
        {
            return await Player.CreateAsync(
                new MediaPlayerConfiguration(VideoDecodingMethod.Hardware),
                DLLPath.GetConfiguredNativeLibraryPaths(),
                platformFactory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{owner.GetType().Name}] Player initialization failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        owner.Dispatcher.VerifyAccess();
        if (_disposed)
            return;

        _disposed = true;
        ReleasePlayer();
    }
}

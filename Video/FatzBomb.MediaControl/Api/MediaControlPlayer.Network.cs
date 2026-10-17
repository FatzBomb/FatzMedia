using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Network source surface (Stage 014A): capabilities of the open source plus buffering and
/// stream-metadata events published through the control queue.
/// </summary>
public partial class MediaControlPlayer
{
    private INetworkAudioSource? _networkAudioSource;

    public event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;

    public event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    public MediaSourceCapabilities? CurrentSourceCapabilities =>
        IsOpen ? (_mediaEngine as IMediaSourceCapabilityProvider)?.CurrentCapabilities : null;

    private void SubscribeNetworkEvents()
    {
        _networkAudioSource = _serviceProvider.GetService<IAudioEngine>() as INetworkAudioSource;
        if (_networkAudioSource is null)
            return;

        _networkAudioSource.BufferingChanged += OnNetworkBufferingChanged;
        _networkAudioSource.StreamMetadataChanged += OnNetworkStreamMetadataChanged;
    }

    private void UnsubscribeNetworkEvents()
    {
        if (_networkAudioSource is null)
            return;

        _networkAudioSource.BufferingChanged -= OnNetworkBufferingChanged;
        _networkAudioSource.StreamMetadataChanged -= OnNetworkStreamMetadataChanged;
        _networkAudioSource = null;
    }

    private void OnNetworkBufferingChanged(object? sender, MediaBufferingChangedEventArgs e) =>
        RaiseOnControlQueue(() =>
        {
            // Queued behind a Close or a Load of another source, the old stream's state is stale.
            if (CurrentSourceCapabilities is { IsBuffered: true })
                HostEventDispatcher.Raise(this, BufferingChanged, e, nameof(BufferingChanged));
        });

    /// <remarks>
    /// Always queued, never raised inline: the engine publishes the first title while the load
    /// operation is still running, and hosts must see it after that load's <c>MediaLoaded</c>.
    /// </remarks>
    private void OnNetworkStreamMetadataChanged(object? sender, MediaStreamMetadataChangedEventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            _ = _controlOperations.Enqueue(() =>
            {
                // As for buffering: a title queued behind a switch to a non-stream source is stale.
                if (CurrentSourceCapabilities is { HasStreamMetadata: true })
                    HostEventDispatcher.Raise(this, StreamMetadataChanged, e, nameof(StreamMetadataChanged));
            });
        }
        catch (ObjectDisposedException)
        {
            // The player is resetting or disposing; the notification is dropped with it.
        }
        catch (InvalidOperationException)
        {
            // The queue stopped accepting work during teardown.
        }
    }
}

using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Network source surface (Stage 014A): capabilities of the open source plus buffering and
/// stream-metadata events published through the control queue. Network video (Stage 039A) adds
/// buffering from the engine's FFmpeg readers, and Close and Dispose cancel a network video open
/// in progress instead of queueing behind it. https and HLS audio (ENG-034) play through the same
/// FFmpeg path, whose stream titles arrive from the engine as well.
/// </summary>
public partial class PlayerCore
{
    private INetworkAudioSource? _networkAudioSource;
    private INetworkPlayer? _networkMediaControl;

    public event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;

    public event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    public MediaSourceCapabilities? CurrentSourceCapabilities =>
        IsOpen ? (_mediaEngine as IMediaSourceCapabilityProvider)?.CurrentCapabilities : null;

    private void SubscribeNetworkEvents()
    {
        _networkMediaControl = _mediaEngine as INetworkPlayer;
        if (_networkMediaControl is not null)
        {
            _networkMediaControl.NetworkBufferingChanged += OnNetworkBufferingChanged;
            _networkMediaControl.NetworkStreamMetadataChanged += OnNetworkStreamMetadataChanged;
        }

        _networkAudioSource = _serviceProvider.GetService<IAudioEngine>() as INetworkAudioSource;
        if (_networkAudioSource is null)
            return;

        _networkAudioSource.BufferingChanged += OnNetworkBufferingChanged;
        _networkAudioSource.StreamMetadataChanged += OnNetworkStreamMetadataChanged;
    }

    private void UnsubscribeNetworkEvents()
    {
        if (_networkMediaControl is not null)
        {
            _networkMediaControl.NetworkBufferingChanged -= OnNetworkBufferingChanged;
            _networkMediaControl.NetworkStreamMetadataChanged -= OnNetworkStreamMetadataChanged;
            _networkMediaControl = null;
        }

        if (_networkAudioSource is null)
            return;

        _networkAudioSource.BufferingChanged -= OnNetworkBufferingChanged;
        _networkAudioSource.StreamMetadataChanged -= OnNetworkStreamMetadataChanged;
        _networkAudioSource = null;
    }

    /// <summary>
    /// Cancels a network video open in progress (Stage 039A) so a Close or Dispose issued while it
    /// connects runs at once instead of waiting for the connect timeout and retries.
    /// </summary>
    private void CancelPendingNetworkOpen()
    {
        try
        {
            (_mediaEngine as INetworkPlayer)?.CancelPendingNetworkOpen();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerCore] Cancelling the network open failed: {ex.Message}");
        }
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

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
        RaiseOnControlQueue(() => HostEventDispatcher.Raise(this, BufferingChanged, e, nameof(BufferingChanged)));

    private void OnNetworkStreamMetadataChanged(object? sender, MediaStreamMetadataChangedEventArgs e) =>
        RaiseOnControlQueue(() => HostEventDispatcher.Raise(this, StreamMetadataChanged, e, nameof(StreamMetadataChanged)));
}

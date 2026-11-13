using Avalonia.Controls;

namespace FatzMedia.Video.WebView2.Avalonia;

/// <summary>
/// Avalonia control that hosts a <see cref="NativeWebView"/> and an <see cref="AvaloniaWebMediaPlayer"/>
/// (stage 060A); the Avalonia counterpart of the WPF and WinForms web media controls. The control owns only the
/// web view's lifecycle; playback is <see cref="Player"/>.
/// </summary>
public sealed class WebMediaView : UserControl, IAsyncDisposable
{
    readonly NativeWebView _web = new();
    AvaloniaWebMediaPlayer? _player;

    /// <summary>Creates the control.</summary>
    public WebMediaView() => Content = _web;

    /// <summary>
    /// Gets the player, created on first use. Load media only after the control has been added to the visual tree
    /// (the web view creates its native window then).
    /// </summary>
    public IWebMediaPlayer Player => _player ??= new AvaloniaWebMediaPlayer(_web);

    /// <summary>Opens an item and (optionally) starts it.</summary>
    public async Task OpenAsync(string address, bool autoplay = false, CancellationToken cancellationToken = default)
    {
        var source = MediaInputSource.Parse(address);
        if (source.WebMedia is not { } item)
            throw new NotSupportedException("Only web media addresses (YouTube) play in the web view.");
        await Player.LoadAsync(item, autoplay, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_player is not null)
            await _player.DisposeAsync().ConfigureAwait(true);
        _player = null;
    }
}

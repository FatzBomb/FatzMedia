using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;

namespace FatzMedia.Video.WebView2.Avalonia;

/// <summary>
/// Plays a web media item (YouTube, stage 060A) in an Avalonia <see cref="NativeWebView"/> through the service's
/// official embedded player. The player page comes from a loopback server (a real origin; without one YouTube
/// answers error 153). Create it on the Avalonia UI thread; members and events then run on that thread. FatzMedia
/// never sees a password or token: <see cref="SignInAsync"/> only shows the service's own sign-in page in the web
/// view, and <see cref="SignOutAsync"/> deletes the web view's cookies.
/// </summary>
public sealed class AvaloniaWebMediaPlayer : IWebMediaPlayer
{
    readonly NativeWebView _web;
    readonly LoopbackPageServer _server;
    TaskCompletionSource? _ready;
    double[] _rates = [];
    bool _disposed;

    /// <summary>Creates a player on a web view that is in the visual tree.</summary>
    public AvaloniaWebMediaPlayer(NativeWebView web)
    {
        ArgumentNullException.ThrowIfNull(web);
        _web = web;
        _server = new LoopbackPageServer(PageBytes.Value);
        _web.WebMessageReceived += OnWebMessageReceived;
    }

    /// <inheritdoc />
    public WebMediaReference? Current { get; private set; }

    /// <inheritdoc />
    public WebMediaState State { get; private set; }

    /// <inheritdoc />
    public TimeSpan Position { get; private set; }

    /// <inheritdoc />
    public TimeSpan Duration { get; private set; }

    /// <inheritdoc />
    public double Volume { get; private set; } = 1;

    /// <inheritdoc />
    public bool IsMuted { get; private set; }

    /// <inheritdoc />
    public double Rate { get; private set; } = 1;

    /// <inheritdoc />
    public IReadOnlyList<double> AvailableRates => _rates;

    /// <inheritdoc />
    public event EventHandler<WebMediaStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler<WebMediaErrorEventArgs>? Error;

    /// <inheritdoc />
    public async Task LoadAsync(WebMediaReference item, bool autoplay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (item.ProviderId != WebMediaProviders.YouTubeId)
            throw new NotSupportedException($"{item.ProviderId} is not supported yet; only YouTube is.");

        Current = item;
        State = WebMediaState.Unstarted;
        Position = Duration = TimeSpan.Zero;
        _ready?.TrySetCanceled(CancellationToken.None);
        var ready = _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));

        _web.Navigate(_server.PageUri($"?v={Uri.EscapeDataString(item.ItemId)}&autoplay={(autoplay ? 1 : 0)}"));
        await ready.Task.ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task PlayAsync() => Send("play");

    /// <inheritdoc />
    public Task PauseAsync() => Send("pause");

    /// <inheritdoc />
    public Task SeekAsync(TimeSpan position) => Send("seek", Math.Max(0, position.TotalSeconds));

    /// <inheritdoc />
    public Task SetVolumeAsync(double volume)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(volume, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(volume, 1);
        return Send("volume", Math.Round(volume * 100));
    }

    /// <inheritdoc />
    public Task SetMutedAsync(bool muted) => Send(muted ? "mute" : "unmute");

    /// <inheritdoc />
    public Task SetRateAsync(double rate)
    {
        if (_rates.Length > 0 && !_rates.Contains(rate))
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The service offers only: " + string.Join(", ", _rates.Select(r => r.ToString(CultureInfo.InvariantCulture))));
        return Send("rate", rate);
    }

    /// <inheritdoc />
    public Task SignInAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Current = null;
        State = WebMediaState.Unstarted;
        _ready?.TrySetCanceled(CancellationToken.None);
        _web.Navigate(new Uri("https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F"));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_web.TryGetCookieManager() is not { } cookies)
            throw new NotSupportedException("This platform's web view does not offer cookie management, so it cannot sign out.");

        foreach (var cookie in await cookies.GetCookiesAsync().ConfigureAwait(true))
            cookies.DeleteCookie(cookie);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _web.WebMessageReceived -= OnWebMessageReceived;
        _ready?.TrySetCanceled(CancellationToken.None);
        _server.Dispose();
        return ValueTask.CompletedTask;
    }

    static readonly Lazy<byte[]> PageBytes = new(LoadPage);

    async Task Send(string cmd, double? value = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = YouTubePlayerMessage.Command(cmd, value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
        await Dispatcher.UIThread.InvokeAsync(() => _web.InvokeScript($"fmCommand('{json}')")).ConfigureAwait(true);
    }

    void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        if (Current is null || e.Body is not { } body)
            return;

        var message = YouTubePlayerMessage.Parse(body);
        switch (message.Type)
        {
            case YouTubePlayerMessageType.Ready:
                Duration = Seconds(message.DurationSeconds);
                Volume = (message.Volume ?? 100) / 100;
                IsMuted = message.Muted ?? false;
                Rate = message.Rate ?? 1;
                _rates = [.. message.Rates];
                SetState(WebMediaState.Cued, Position);
                _ready?.TrySetResult();
                break;
            case YouTubePlayerMessageType.State when message.StateCode is { } code:
                Position = Seconds(message.Seconds);
                if (message.DurationSeconds is { } d && d > 0)
                    Duration = Seconds(d);
                SetState(YouTubePlayerMessage.MapState(code), Position);
                break;
            case YouTubePlayerMessageType.Time:
                Position = Seconds(message.Seconds);
                if (message.DurationSeconds is { } dur && dur > 0)
                    Duration = Seconds(dur);
                PositionChanged?.Invoke(this, Position);
                break;
            case YouTubePlayerMessageType.Rate when message.Rate is { } rate:
                Rate = rate;
                break;
            case YouTubePlayerMessageType.Volume:
                Volume = (message.Volume ?? Volume * 100) / 100;
                IsMuted = message.Muted ?? IsMuted;
                break;
            case YouTubePlayerMessageType.Error when message.ErrorCode is { } code:
                var (kind, text) = WebMediaErrors.FromYouTubeCode(code);
                RaiseError(kind, text, code);
                break;
            case YouTubePlayerMessageType.LoadFailed:
                RaiseError(WebMediaErrorKind.PlayerFailure, "The YouTube player could not be loaded; check the network connection.", null);
                break;
        }
    }

    void RaiseError(WebMediaErrorKind kind, string text, int? code)
    {
        _ready?.TrySetException(new InvalidOperationException(text));
        Error?.Invoke(this, new WebMediaErrorEventArgs(kind, text, code));
    }

    void SetState(WebMediaState state, TimeSpan position)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(this, new WebMediaStateChangedEventArgs(state, position));
    }

    static TimeSpan Seconds(double? value) =>
        value is { } v and >= 0 and < 1e9 ? TimeSpan.FromSeconds(v) : TimeSpan.Zero;

    static byte[] LoadPage()
    {
        using var stream = typeof(AvaloniaWebMediaPlayer).Assembly.GetManifestResourceStream("youtube-player.html")
            ?? throw new InvalidOperationException("The embedded YouTube player page is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

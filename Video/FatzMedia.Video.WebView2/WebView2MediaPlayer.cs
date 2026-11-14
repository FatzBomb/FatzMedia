using System.Globalization;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace FatzMedia.Video.WebView2;

/// <summary>
/// Plays a web media item (YouTube, stage 060A) in a <see cref="CoreWebView2"/> through the service's official
/// embedded player. The player page is served from a virtual https host name, because YouTube rejects embeds that
/// carry no https origin (error 153). Create it on the UI thread that owns the web view; the members and events
/// then run on that thread. FatzMedia never sees a password or token: <see cref="SignInAsync"/> only shows the
/// service's own sign-in page in the web view's persistent profile.
/// </summary>
public sealed class WebView2MediaPlayer : IWebMediaPlayer
{
    /// <summary>The virtual https host the player page is served from.</summary>
    public const string PageHost = "web.fatzmedia.test";

    /// <summary>
    /// The web view environment options the player needs. The host starts playback from its own controls, which the
    /// web page cannot see as a user gesture, so the web view must allow playback that starts without one.
    /// </summary>
    public static CoreWebView2EnvironmentOptions EnvironmentOptions() =>
        new("--autoplay-policy=no-user-gesture-required");

    static readonly Lazy<byte[]> PageBytes = new(LoadPage);

    readonly CoreWebView2 _web;
    readonly SynchronizationContext? _context = SynchronizationContext.Current;
    TaskCompletionSource? _ready;
    double[] _rates = [];
    bool _disposed;

    /// <summary>Creates a player on a web view whose <c>CoreWebView2</c> is initialised.</summary>
    public WebView2MediaPlayer(CoreWebView2 web)
    {
        ArgumentNullException.ThrowIfNull(web);
        _web = web;
        _web.AddWebResourceRequestedFilter($"https://{PageHost}/*", CoreWebView2WebResourceContext.All);
        _web.WebResourceRequested += OnWebResourceRequested;
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

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken));

        // The web view belongs to the UI thread; callers on other threads (the Core control queue) are marshalled.
        await OnUiThread(() =>
        {
            Current = item;
            State = WebMediaState.Unstarted;
            Position = Duration = TimeSpan.Zero;
            _ready?.TrySetCanceled(CancellationToken.None);
            _ready = ready;
            _web.Navigate($"https://{PageHost}/youtube-player.html?v={Uri.EscapeDataString(item.ItemId)}&autoplay={(autoplay ? 1 : 0)}");
        }).ConfigureAwait(false);
        await ready.Task.ConfigureAwait(false);
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
        return OnUiThread(() =>
        {
            Current = null;
            State = WebMediaState.Unstarted;
            _ready?.TrySetCanceled(CancellationToken.None);
            _web.Navigate("https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F");
        });
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnUiThread(async () =>
        {
            try
            {
                await _web.Profile.ClearBrowsingDataAsync().ConfigureAwait(true);
                clear.SetResult();
            }
            catch (Exception ex)
            {
                clear.SetException(ex);
            }
        }).ConfigureAwait(false);
        await clear.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _web.WebResourceRequested -= OnWebResourceRequested;
        _web.WebMessageReceived -= OnWebMessageReceived;
        _web.RemoveWebResourceRequestedFilter($"https://{PageHost}/*", CoreWebView2WebResourceContext.All);
        _ready?.TrySetCanceled(CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    Task Send(string cmd, double? value = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var json = YouTubePlayerMessage.Command(cmd, value);
        return OnUiThread(() => _web.PostWebMessageAsJson(json));
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread the web view lives on, without blocking the caller.</summary>
    Task OnUiThread(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            _ =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            },
            null);
        return done.Task;
    }

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (!string.Equals(uri.Host, PageHost, StringComparison.OrdinalIgnoreCase))
            return;

        if (uri.AbsolutePath == "/youtube-player.html")
        {
            var body = new MemoryStream(PageBytes.Value, writable: false);
            e.Response = _web.Environment.CreateWebResourceResponse(body, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
        }
        else
        {
            e.Response = _web.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }

    void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (Current is null || !Uri.TryCreate(e.Source, UriKind.Absolute, out var from)
            || !string.Equals(from.Host, PageHost, StringComparison.OrdinalIgnoreCase))
            return;

        var message = YouTubePlayerMessage.Parse(e.WebMessageAsJson);
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
        using var stream = typeof(WebView2MediaPlayer).Assembly.GetManifestResourceStream("youtube-player.html")
            ?? throw new InvalidOperationException("The embedded YouTube player page is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

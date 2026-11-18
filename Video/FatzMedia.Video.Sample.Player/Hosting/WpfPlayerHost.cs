using System.Windows;
using System.Windows.Threading;
using FatzMedia.Video.Sample.Player.Shared.Hosting;
using FatzMedia.Video.WebView2;
using Microsoft.Win32;

namespace FatzMedia.Video.Sample.Player.Hosting;

/// <summary>The WPF application dispatcher; with no application (designer) everything runs inline.</summary>
sealed class WpfPlayerDispatcher : IPlayerDispatcher
{
    public bool CheckAccess() => Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            _ = dispatcher.BeginInvoke(action);
        else
            action();
    }

    public IPlayerTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    sealed class Timer : IPlayerTimer
    {
        readonly DispatcherTimer _timer;
        readonly Action _tick;

        public Timer(TimeSpan interval, Action tick)
        {
            _tick = tick;
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        void OnTick(object? sender, EventArgs e) => _tick();
    }
}

/// <summary>The WPF clipboard; a COM failure (another app holds the clipboard) reaches the status line.</summary>
sealed class WpfPlayerClipboard : IPlayerClipboard
{
    public Task SetTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }
}

/// <summary>The WPF open-file dialog, owned by the main window.</summary>
sealed class WpfPlayerFilePicker(Window owner) : IPlayerFilePicker
{
    public Task<string?> PickFileAsync(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return Task.FromResult(dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }
}

/// <summary>
/// The WPF web view (Stage 060A): the <c>WebMediaView</c> shares the media surface's cell; showing it hides the
/// surface (hidden, so its layout and swap chain keep their size) and the other way round. The web view starts on
/// first use, shown, because WebView2 creates its window when it is on screen.
/// </summary>
sealed class WpfPlayerWebView(WebMediaView view, UIElement surface) : IPlayerWebView
{
    Task<IWebMediaPlayer>? _ready;

    public Task<IWebMediaPlayer> EnsureReadyAsync() => _ready ??= StartAsync();

    public void SetVisible(bool visible)
    {
        // A file replaced the web item while it was on the second screen: bring the web view home.
        if (!visible && view.IsFullScreen)
            view.ExitFullScreen();
        view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        surface.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
    }

    async Task<IWebMediaPlayer> StartAsync()
    {
        try
        {
            SetVisible(true);
            await view.InitializeAsync();
            return view.Player;
        }
        catch
        {
            // The control keeps a failed start; restart the player after installing the WebView2 runtime.
            SetVisible(false);
            throw;
        }
    }
}

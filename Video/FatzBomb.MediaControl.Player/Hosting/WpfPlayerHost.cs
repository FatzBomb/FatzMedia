using System.Windows;
using System.Windows.Threading;
using FatzBomb.MediaControl.Player.Shared.Hosting;
using Microsoft.Win32;

namespace FatzBomb.MediaControl.Player.Hosting;

/// <summary>The WPF application dispatcher; with no application (designer) everything runs inline.</summary>
internal sealed class WpfPlayerDispatcher : IPlayerDispatcher
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

    private sealed class Timer : IPlayerTimer
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _tick;

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

        private void OnTick(object? sender, EventArgs e) => _tick();
    }
}

/// <summary>The WPF clipboard; a COM failure (another app holds the clipboard) reaches the status line.</summary>
internal sealed class WpfPlayerClipboard : IPlayerClipboard
{
    public Task SetTextAsync(string text)
    {
        Clipboard.SetText(text);
        return Task.CompletedTask;
    }
}

/// <summary>The WPF open-file dialog, owned by the main window.</summary>
internal sealed class WpfPlayerFilePicker(Window owner) : IPlayerFilePicker
{
    public Task<string?> PickFileAsync(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return Task.FromResult(dialog.ShowDialog(owner) == true ? dialog.FileName : null);
    }
}

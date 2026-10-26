using FatzBomb.MediaControl.Player.Shared.Hosting;

namespace FatzBomb.MediaControl.Player.Shared.Presentation;

/// <summary>
/// The full-screen chrome rule every test player shares (Stage 046A slice 3): in full screen the
/// bars and the cursor hide <see cref="HideDelay"/> after the pointer last moved, and a real pointer
/// move shows them again. The host draws (<c>showChrome</c>) and says when the chrome must stay
/// (<c>keepChrome</c>: the pointer is on the bars, a flyout is open, a drag is on); the timer then
/// looks again after another delay instead of hiding.
/// </summary>
/// <remarks>
/// Hiding the bars resizes the picture under a still pointer, and the windowing system then sends a
/// pointer move at the same place (WinForms always, others sometimes): a move to the position the
/// pointer had when the chrome hid is not a real move and is ignored. UI thread only.
/// </remarks>
public sealed class FullScreenChromeController : IDisposable
{
    /// <summary>How long the chrome stays after the last pointer move.</summary>
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(2.5);

    private readonly IPlayerTimer _timer;
    private readonly Action<bool> _showChrome;
    private readonly Func<bool> _keepChrome;
    private readonly Func<(double X, double Y)?>? _pointerPosition;
    private (double X, double Y)? _lastPointer;
    private bool _disposed;

    /// <param name="dispatcher">The host's UI thread; its timer runs the delay.</param>
    /// <param name="showChrome">Shows (true) or hides (false) the bars and the cursor; called only on a change.</param>
    /// <param name="keepChrome">Whether the chrome must stay now (pointer on the bars, a flyout open, a drag).</param>
    /// <param name="pointerPosition">
    /// The pointer's current position, in the same coordinates as <see cref="PointerMoved"/>, or null
    /// when the host cannot tell; read when the chrome hides. Without it the last reported position
    /// is used.
    /// </param>
    public FullScreenChromeController(
        IPlayerDispatcher dispatcher,
        Action<bool> showChrome,
        Func<bool> keepChrome,
        Func<(double X, double Y)?>? pointerPosition = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _showChrome = showChrome ?? throw new ArgumentNullException(nameof(showChrome));
        _keepChrome = keepChrome ?? throw new ArgumentNullException(nameof(keepChrome));
        _pointerPosition = pointerPosition;
        _timer = dispatcher.CreateTimer(HideDelay, OnTick);
    }

    /// <summary>Gets whether full screen is on (the chrome auto-hides).</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>Gets whether the chrome is shown.</summary>
    public bool IsChromeVisible { get; private set; } = true;

    /// <summary>Full screen starts: the chrome shows and hides after the delay.</summary>
    public void Enter()
    {
        if (_disposed)
            return;

        IsFullScreen = true;
        ShowAndRestart();
    }

    /// <summary>Full screen ends: the chrome shows and stays.</summary>
    public void Exit()
    {
        IsFullScreen = false;
        _timer.Stop();
        SetChrome(true);
    }

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>), in any coordinates the
    /// host keeps the same (screen coordinates are safest). A move to where the pointer already was
    /// is ignored. Outside full screen it only records the position.
    /// </summary>
    public void PointerMoved(double x, double y)
    {
        if (_lastPointer is { } last && last.X == x && last.Y == y)
            return;

        _lastPointer = (x, y);
        if (IsFullScreen && !_disposed)
            ShowAndRestart();
    }

    /// <summary>Shows the chrome now and restarts the delay (for example after a key press in full screen).</summary>
    public void Show()
    {
        if (IsFullScreen && !_disposed)
            ShowAndRestart();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Dispose();
    }

    private void ShowAndRestart()
    {
        SetChrome(true);
        _timer.Stop();
        _timer.Start();
    }

    private void OnTick()
    {
        _timer.Stop();
        if (!IsFullScreen || _disposed)
            return;

        // The pointer is on the bars, a flyout is open or a drag is on: look again later.
        if (_keepChrome())
        {
            _timer.Start();
            return;
        }

        if (_pointerPosition?.Invoke() is { } pointer)
            _lastPointer = pointer;
        SetChrome(false);
    }

    private void SetChrome(bool visible)
    {
        if (IsChromeVisible == visible)
            return;

        IsChromeVisible = visible;
        _showChrome(visible);
    }
}

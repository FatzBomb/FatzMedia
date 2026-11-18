namespace FatzMedia.Video.WebView2;

/// <summary>Decides whether a left-button press is the second of a double-click. Pure logic, no input hook.</summary>
sealed class DoubleClickDetector
{
    uint _lastTime;
    int _lastX;
    int _lastY;
    bool _hasLast;

    /// <summary>Forgets the previous press (the pointer left the area).</summary>
    public void Reset() => _hasLast = false;

    /// <summary>
    /// Registers a press at (<paramref name="x"/>, <paramref name="y"/>) at <paramref name="timeMs"/> and returns
    /// whether it completes a double-click: within <paramref name="maxMs"/> of the previous press and within
    /// <paramref name="maxDx"/> by <paramref name="maxDy"/> pixels of it. A completed double-click resets, so a
    /// third press starts a new pair.
    /// </summary>
    public bool Press(int x, int y, uint timeMs, uint maxMs, int maxDx, int maxDy)
    {
        var second = _hasLast
            && unchecked(timeMs - _lastTime) <= maxMs
            && Math.Abs(x - _lastX) <= maxDx
            && Math.Abs(y - _lastY) <= maxDy;
        _hasLast = !second;
        _lastTime = timeMs;
        _lastX = x;
        _lastY = y;
        return second;
    }
}

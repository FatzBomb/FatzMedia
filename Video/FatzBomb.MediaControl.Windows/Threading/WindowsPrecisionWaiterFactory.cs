using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Windows.Threading;

/// <summary>Creates Windows high-resolution waitable-timer instances.</summary>
internal sealed class WindowsPrecisionWaiterFactory : IPrecisionWaiterFactory
{
    public IPrecisionWaiter Create() => new WindowsPrecisionWaiter();
}

using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Windows.Threading;

/// <summary>Creates Windows high-resolution waitable-timer instances.</summary>
public sealed class WindowsPrecisionWaiterFactory : IPrecisionWaiterFactory
{
    public IPrecisionWaiter Create() => new WindowsPrecisionWaiter();
}

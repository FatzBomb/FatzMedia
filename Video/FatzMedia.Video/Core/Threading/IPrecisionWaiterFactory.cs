namespace FatzMedia.Video.Core.Threading;

/// <summary>Creates a cancellable waiter suitable for fixed-cadence media loops.</summary>
public interface IPrecisionWaiterFactory
{
    IPrecisionWaiter Create();
}

/// <summary>A single-threaded, cancellable media-loop waiter.</summary>
public interface IPrecisionWaiter : IDisposable
{
    bool IsHighResolution { get; }
    void Wait(TimeSpan duration, WaitHandle cancellationHandle);
}

/// <summary>
/// Portable fallback based on the runtime wait-handle implementation. Platforms
/// can supply a higher-resolution implementation through their composition factory.
/// </summary>
public sealed class SystemPrecisionWaiterFactory : IPrecisionWaiterFactory
{
    public IPrecisionWaiter Create() => new SystemPrecisionWaiter();

    sealed class SystemPrecisionWaiter : IPrecisionWaiter
    {
        public bool IsHighResolution => false;

        public void Wait(TimeSpan duration, WaitHandle cancellationHandle)
        {
            ArgumentNullException.ThrowIfNull(cancellationHandle);
            if (duration > TimeSpan.Zero)
                _ = cancellationHandle.WaitOne(duration);
        }

        public void Dispose()
        {
        }
    }
}

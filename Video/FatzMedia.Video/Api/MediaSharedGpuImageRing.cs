#pragma warning disable IDE0130
using System.Diagnostics;

namespace FatzMedia.Video;

/// <summary>
/// Where the producer may write next: the image index, the keyed-mutex key to acquire it with,
/// and whether a consumer-release semaphore signal is pending (the producer must wait on it
/// before writing).
/// </summary>
public readonly record struct MediaSharedGpuImageWrite(int BufferIndex, ulong AcquireKey, bool WaitForConsumerRelease);

/// <summary>
/// Producer-side bookkeeping of a shared-image set (Stage 027A slice 2), the same for every
/// renderer: which images are free, being written, or in flight to the consumer, plus the
/// keyed-mutex key each image was last released with and whether its consumer-release semaphore
/// has a pending signal. Thread-safe: the renderer writes on its presentation thread while the
/// consumer releases frames from the compositor's threads.
/// </summary>
/// <remarks>
/// Protocol: the producer takes a free image (<see cref="TryBeginWrite"/>; the least recently
/// presented one, so images alternate), acquires its keyed mutex with
/// <see cref="MediaSharedGpuImageWrite.AcquireKey"/> (or waits on the consumer-release semaphore
/// when <see cref="MediaSharedGpuImageWrite.WaitForConsumerRelease"/> is set), writes, releases
/// <see cref="ConsumerKey"/> (or signals render-complete) and calls <see cref="CompleteWrite"/>.
/// The consumer acquires <see cref="ConsumerKey"/>, reads, releases <see cref="ProducerKey"/> and
/// calls <see cref="Release(long, int, bool)"/> with <c>consumed: true</c>. An image the consumer never used comes
/// back with <c>consumed: false</c>: its mutex still holds <see cref="ConsumerKey"/>, which the
/// producer then acquires instead, and no semaphore signal is pending. No image is written while
/// the consumer may read it, so the compositor never samples a half-written frame.
/// </remarks>
public sealed class MediaSharedGpuImageRing : IDisposable
{
    /// <summary>The key the producer acquires a keyed mutex with (and the consumer releases).</summary>
    public const ulong ProducerKey = 0;

    /// <summary>The key the consumer acquires a keyed mutex with (and the producer releases).</summary>
    public const ulong ConsumerKey = 1;

    readonly Lock _gate = new();
    readonly ManualResetEventSlim _available = new(true);
    readonly BufferState[] _states;
    readonly ulong[] _keys;
    readonly bool[] _consumerSignalPending;
    readonly long[] _lastPresented;
    long _generation;
    long _frameNumber;
    long _presentSequence;
    bool _disposed;

    public MediaSharedGpuImageRing(int bufferCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCount);
        _states = new BufferState[bufferCount];
        _keys = new ulong[bufferCount];
        _consumerSignalPending = new bool[bufferCount];
        _lastPresented = new long[bufferCount];
    }

    enum BufferState
    {
        Free,
        Writing,
        InFlight,
    }

    public int BufferCount => _states.Length;

    /// <summary>Gets the generation of the current image set.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    /// <summary>Gets how many images are free to write.</summary>
    public int FreeCount => Count(BufferState.Free);

    /// <summary>Gets how many images are in flight to the consumer.</summary>
    public int InFlightCount => Count(BufferState.InFlight);

    /// <summary>
    /// Starts a new image set: every image free, its keyed mutex at <see cref="ProducerKey"/> (as
    /// created), no semaphore signal pending. Releases for older generations are ignored.
    /// </summary>
    public void Reset(long generation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation = generation;
            Array.Fill(_states, BufferState.Free);
            Array.Fill(_keys, ProducerKey);
            Array.Fill(_consumerSignalPending, false);
            Array.Fill(_lastPresented, 0);
            _available.Set();
        }
    }

    /// <summary>
    /// Takes the least recently presented free image, waiting up to <paramref name="timeout"/> for
    /// the consumer to release one. Returns <see langword="false"/> when none became free (the
    /// caller drops the frame: the consumer is behind, so a newer frame will follow).
    /// </summary>
    public bool TryBeginWrite(TimeSpan timeout, out MediaSharedGpuImageWrite write)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, timeout.TotalSeconds) * Stopwatch.Frequency);
        while (true)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var index = -1;
                for (var i = 0; i < _states.Length; i++)
                {
                    if (_states[i] == BufferState.Free && (index < 0 || _lastPresented[i] < _lastPresented[index]))
                        index = i;
                }

                if (index >= 0)
                {
                    _states[index] = BufferState.Writing;
                    write = new MediaSharedGpuImageWrite(index, _keys[index], _consumerSignalPending[index]);
                    return true;
                }

                _available.Reset();
            }

            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0 || !WaitAvailable(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency)))
            {
                write = default;
                return false;
            }
        }
    }

    /// <summary>
    /// The producer finished writing <paramref name="bufferIndex"/> (and released its mutex with
    /// <see cref="ConsumerKey"/> or signalled render-complete). Returns the frame to announce.
    /// </summary>
    public MediaSharedGpuImageFrame CompleteWrite(int bufferIndex)
    {
        lock (_gate)
        {
            RequireState(bufferIndex, BufferState.Writing);
            _states[bufferIndex] = BufferState.InFlight;
            _keys[bufferIndex] = ConsumerKey;
            // A pending consumer signal was waited on by this write.
            _consumerSignalPending[bufferIndex] = false;
            _lastPresented[bufferIndex] = ++_presentSequence;
            return new MediaSharedGpuImageFrame(_generation, bufferIndex, ++_frameNumber, ConsumerKey, ProducerKey);
        }
    }

    /// <summary>
    /// The producer gave up writing <paramref name="bufferIndex"/> before announcing it. It must
    /// have released the mutex with the key it acquired, so the key is unchanged.
    /// </summary>
    public void AbortWrite(int bufferIndex)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            RequireState(bufferIndex, BufferState.Writing);
            _states[bufferIndex] = BufferState.Free;
            _available.Set();
        }
    }

    /// <summary>
    /// The consumer is done with a frame. <paramref name="consumed"/> says whether it used the
    /// image (acquired and released its mutex, or waited and signalled its semaphores). Returns
    /// <see langword="false"/> for a stale generation or an image that is not in flight.
    /// </summary>
    public bool Release(long generation, int bufferIndex, bool consumed)
    {
        lock (_gate)
        {
            if (_disposed
                || generation != _generation
                || (uint)bufferIndex >= (uint)_states.Length
                || _states[bufferIndex] != BufferState.InFlight)
            {
                return false;
            }

            _states[bufferIndex] = BufferState.Free;
            if (consumed)
            {
                _keys[bufferIndex] = ProducerKey;
                _consumerSignalPending[bufferIndex] = true;
            }

            _available.Set();
            return true;
        }
    }

    /// <summary>Convenience overload for <see cref="Release(long, int, bool)"/>.</summary>
    public bool Release(MediaSharedGpuImageFrame frame, bool consumed) => Release(frame.Generation, frame.BufferIndex, consumed);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            // Wakes a waiting producer, which then observes the disposal.
            _available.Set();
        }

        _available.Dispose();
    }

    bool WaitAvailable(TimeSpan timeout)
    {
        try
        {
            return _available.Wait(timeout);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while waiting: the next pass throws with this ring as the object.
            return true;
        }
    }

    int Count(BufferState state)
    {
        lock (_gate)
            return _states.Count(s => s == state);
    }

    void RequireState(int bufferIndex, BufferState expected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bufferIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(bufferIndex, _states.Length);
        if (_states[bufferIndex] != expected)
            throw new InvalidOperationException($"Shared image {bufferIndex} is {_states[bufferIndex]}, not {expected}.");
    }
}

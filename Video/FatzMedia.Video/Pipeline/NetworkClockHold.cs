using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Pipeline;

/// <summary>
/// When the playback clock of a network video without sound stops for the network (ENG-034).
/// With sound the audio output waits for data by itself; a picture on the playback clock did
/// not, so an outage longer than the buffer skipped what played meanwhile (and could run the
/// clock to the end). Pure, so the decision is tested without a pipeline.
/// </summary>
internal static class NetworkClockHold
{
    /// <summary>How long the picture's packet queue must have been empty before the clock is held.</summary>
    public static readonly TimeSpan StarvedFor = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether the clock should be held. It is held once no decoded frame is left and the picture
    /// has found its packet queue empty for <see cref="StarvedFor"/>, and released once a frame is
    /// ready again and the read-ahead reached its resume threshold (as buffering ends), or the
    /// source reached its end.
    /// </summary>
    /// <param name="held">Whether the clock is held now.</param>
    /// <param name="frameReady">Whether a decoded frame is queued.</param>
    /// <param name="starved">How long the picture has found its packet queue empty.</param>
    /// <param name="resumeFill">The read-ahead's progress towards its resume threshold, <c>0..1</c>.</param>
    /// <param name="atEnd">Whether the reader or the decoder reached the end of the source.</param>
    public static bool ShouldHold(bool held, bool frameReady, TimeSpan starved, double resumeFill, bool atEnd)
    {
        if (atEnd)
            return false;

        return held
            ? !frameReady || resumeFill < 1
            : !frameReady && starved >= StarvedFor;
    }
}

/// <summary>
/// The video pipeline's view of the playback clock (ENG-034): the presentation loop can hold it
/// while a network picture waits for data (<see cref="Hold"/>, <see cref="Release"/>). Every
/// transport change (start, pause, stop, seek) goes through here and ends a hold, so a hold never
/// outlives the playback it was taken for. Reads and the tempo pass straight through.
/// </summary>
internal sealed class HoldableClock(IClockSource inner) : IClockSource
{
    private readonly IClockSource _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private volatile bool _held;

    /// <summary>Whether the presentation loop holds the clock.</summary>
    public bool IsHeld => _held;

    public double DurationSeconds
    {
        get => _inner.DurationSeconds;
        set => _inner.DurationSeconds = value;
    }

    public double PositionSeconds => _inner.PositionSeconds;
    public bool IsRunning => _inner.IsRunning;
    public int TempoPercent => _inner.TempoPercent;

    /// <summary>Stops a running clock where it is, until <see cref="Release"/> or a transport change. Returns whether it held it.</summary>
    public bool Hold()
    {
        if (_held || !_inner.IsRunning)
            return false;

        _inner.Pause();
        _held = true;
        return true;
    }

    /// <summary>Restarts a held clock from where it stopped. Returns whether it was held.</summary>
    public bool Release()
    {
        if (!_held)
            return false;

        _held = false;
        _inner.Start();
        return true;
    }

    public void Start(double? positionSeconds = null)
    {
        _held = false;
        _inner.Start(positionSeconds);
    }

    public void Pause()
    {
        _held = false;
        _inner.Pause();
    }

    public void Stop()
    {
        _held = false;
        _inner.Stop();
    }

    public void Seek(double positionSeconds)
    {
        // A seek keeps the running state; a held clock stays stopped at the target until the next start.
        _held = false;
        _inner.Seek(positionSeconds);
    }

    public void SetTempo(int tempoPercent) => _inner.SetTempo(tempoPercent);
}

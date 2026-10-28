namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>
/// Lets the presentation side hand a hardware decoder back to software (ENG-037): set by the
/// producer on each hardware frame, and requested when the renderer cannot take the decoder's
/// frames for a reason other than a lost device (a failed device adoption, a device without
/// video support, a renderer on another device). The producer then reopens the video in software
/// at the current position, off the presentation path. One token per decoder; thread-safe.
/// </summary>
public sealed class VideoDecodeFallback
{
    private readonly Action<string>? _onRequested;
    private string? _reason;

    public VideoDecodeFallback()
    {
    }

    /// <param name="onRequested">Called once, on the requesting thread, with the first reason; must not block.</param>
    internal VideoDecodeFallback(Action<string> onRequested) => _onRequested = onRequested;

    /// <summary>Gets whether software decoding was requested.</summary>
    public bool IsRequested => Volatile.Read(ref _reason) is not null;

    /// <summary>Why software decoding was requested, or null.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>Asks the decoder to switch to software; only the first request (and its reason) counts.</summary>
    public void RequestSoftwareDecoding(string reason)
    {
        // The reason is the flag, so a reader that sees the request also sees why.
        reason ??= string.Empty;
        if (Interlocked.CompareExchange(ref _reason, reason, null) is not null)
            return;

        _onRequested?.Invoke(reason);
    }
}

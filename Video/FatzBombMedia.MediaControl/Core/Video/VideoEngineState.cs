namespace FatzBombMedia.MediaControl.Core.Video;

public enum VideoEngineState
{
    Null = 0,
    Ready,
    Playing,
    Paused,
    Stopped,
    Closed,
    /// <summary>
    /// The engine is in a non-recoverable error state and cannot present further frames until
    /// the host closes and reopens the source. Raised alongside an <c>Error</c>-level diagnostic
    /// (e.g. <c>Decoder.DeviceLost.RecoveryFailed</c> when hardware-decode device loss cannot
    /// be resolved via software fallback).
    /// </summary>
    Error
}

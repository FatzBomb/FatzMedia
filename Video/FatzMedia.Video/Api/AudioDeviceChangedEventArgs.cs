#pragma warning disable IDE0130
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video;

/// <summary>
/// Provides typed details for the <see cref="IMediaControlSession.AudioDeviceChanged"/> event.
/// Fires from a background thread; hosts must marshal to the UI thread before touching view state.
/// </summary>
public sealed class AudioDeviceChangedEventArgs(AudioDeviceChangeInfo info) : EventArgs
{

    /// <summary>Gets the change kind (default changed, state changed, removed, added, unrecoverable).</summary>
    public AudioDeviceChangeKind Kind => Info.Kind;

    /// <summary>Gets the Win32 device id of the affected endpoint, if known.</summary>
    public string? DeviceId => Info.DeviceId;

    /// <summary>Gets the friendly name of the affected endpoint, if known.</summary>
    public string? DeviceName => Info.DeviceName;

    /// <summary>Gets a human-readable diagnostic string, or null when not applicable.</summary>
    public string? FailureMessage => Info.FailureMessage;

    /// <summary>Gets the Win32 HRESULT associated with the change, or 0 when none.</summary>
    public int HResult => Info.HResult;

    /// <summary>Gets the underlying internal change info.</summary>
    public AudioDeviceChangeInfo Info { get; } = info;
}

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Describes an output device that can be selected when creating a player: a BASS device (by
/// <see cref="DeviceIndex"/>) or a FatzAudio device (by <see cref="Id"/>).
/// </summary>
public sealed record class AudioOutputDeviceInfo(
    int DeviceIndex,
    string Name,
    string? Driver,
    bool IsPrimary,
    bool IsEnabled,
    bool IsInitialized)
{
    /// <summary>Gets the synthetic device index used for the system primary/default output.</summary>
    public const int PrimaryDeviceIndex = -1;

    /// <summary>
    /// Gets the FatzAudio device id to pass as <c>MediaPlayerConfiguration.AudioOutputDeviceId</c>;
    /// <see langword="null"/> for the system default and for BASS devices (which use <see cref="DeviceIndex"/>).
    /// </summary>
    public string? Id { get; init; }

    /// <summary>Gets the display text hosts can show in a device selector.</summary>
    public string DisplayName => IsPrimary ? "Primary (device default)" : Name;

    /// <summary>Gets the synthetic primary/default output device.</summary>
    public static AudioOutputDeviceInfo Primary { get; } = new(
        PrimaryDeviceIndex,
        "Primary (device default)",
        Driver: null,
        IsPrimary: true,
        IsEnabled: true,
        IsInitialized: false);

    public override string ToString() => DisplayName;
}

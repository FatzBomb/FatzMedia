#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// An output device a host can offer when creating a player: pass <see cref="Id"/> as
/// <c>MediaPlayerConfiguration.AudioOutputDeviceId</c>. Listed by <c>FatzAudioOutputDevices.Enumerate()</c>.
/// </summary>
/// <param name="Id">The device id; <see langword="null"/> for the system default, which the player follows.</param>
/// <param name="Name">The device's name.</param>
/// <param name="IsPrimary">True for the system default entry.</param>
public sealed record class AudioOutputDeviceInfo(string? Id, string Name, bool IsPrimary)
{
    /// <summary>Gets the display text hosts can show in a device selector.</summary>
    public string DisplayName => IsPrimary ? "Primary (device default)" : Name;

    /// <summary>Gets the system primary/default output device.</summary>
    public static AudioOutputDeviceInfo Primary { get; } = new(Id: null, "Primary (device default)", IsPrimary: true);

    public override string ToString() => DisplayName;
}

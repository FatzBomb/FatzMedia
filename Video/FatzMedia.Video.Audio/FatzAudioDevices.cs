using FatzMedia.Video.Core.Abstractions;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// The FatzAudio output devices a host can offer (037A, BASS retirement): what to pass as
/// <c>MediaPlayerConfiguration.AudioOutputDeviceId</c>. Needs the FatzAudio native library, not BASS.
/// </summary>
public static class FatzAudioOutputDevices
{
    /// <summary>
    /// The system default (<see cref="AudioOutputDeviceInfo.Primary"/>, id <see langword="null"/>: follows the
    /// default) followed by every playback device FatzAudio sees now, each with its <see cref="AudioOutputDeviceInfo.Id"/>.
    /// </summary>
    /// <exception cref="DllNotFoundException">The FatzAudio native library is missing.</exception>
    public static IReadOnlyList<AudioOutputDeviceInfo> Enumerate()
    {
        using var engine = Fa.AudioEngine.Create(new Fa.AudioEngineOptions { NoCache = true });
        return FatzAudioDevices.ToOutputDeviceInfos(engine.OutputDevices);
    }
}

/// <summary>Device rules of the FatzAudio engine: selection and the device-list diff. Pure functions.</summary>
static class FatzAudioDevices
{
    /// <summary>One device as the watcher compares it.</summary>
    internal readonly record struct Snapshot(string Id, string Name, bool IsDefault);

    /// <summary>The default entry, then each device in FatzAudio's order (its index is its place in that list).</summary>
    public static IReadOnlyList<AudioOutputDeviceInfo> ToOutputDeviceInfos(IReadOnlyList<Fa.AudioDeviceInfo> devices) =>
    [
        AudioOutputDeviceInfo.Primary,
        .. devices.Select(static (device, index) => new AudioOutputDeviceInfo(index, device.Name, Driver: null, IsPrimary: false, IsEnabled: true, IsInitialized: false)
        {
            Id = device.Id,
        }),
    ];

    /// <summary>The device a configuration names: its id exactly, else its name ignoring case; null when none matches.</summary>
    public static Fa.AudioDeviceInfo? Find(IReadOnlyList<Fa.AudioDeviceInfo> devices, string idOrName) =>
        devices.FirstOrDefault(device => string.Equals(device.Id, idOrName, StringComparison.Ordinal))
        ?? devices.FirstOrDefault(device => string.Equals(device.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Snapshot> ToSnapshots(IReadOnlyList<Fa.AudioDeviceInfo> devices) =>
        [.. devices.Select(static device => new Snapshot(device.Id, device.Name, device.IsDefault))];

    /// <summary>
    /// What changed between two looks at the playback devices: each device that went away, each new one, then a
    /// new default (one event per change, in that order).
    /// </summary>
    public static IReadOnlyList<AudioDeviceChangeInfo> Diff(IReadOnlyList<Snapshot> before, IReadOnlyList<Snapshot> after)
    {
        List<AudioDeviceChangeInfo> changes = [];
        foreach (var gone in before.Where(old => !after.Any(now => now.Id == old.Id)))
            changes.Add(new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceRemoved, gone.Id, gone.Name, null, 0));
        foreach (var added in after.Where(now => !before.Any(old => old.Id == now.Id)))
            changes.Add(new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceAdded, added.Id, added.Name, null, 0));

        var oldDefault = before.FirstOrDefault(static device => device.IsDefault);
        var newDefault = after.FirstOrDefault(static device => device.IsDefault);
        if (newDefault.Id is not null && newDefault.Id != oldDefault.Id)
            changes.Add(new AudioDeviceChangeInfo(AudioDeviceChangeKind.DefaultDeviceChanged, newDefault.Id, newDefault.Name, null, 0));
        return changes;
    }
}

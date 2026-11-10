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

    /// <summary>
    /// The default entry, then each device in FatzAudio's order. A FatzAudio device is chosen by <see cref="AudioOutputDeviceInfo.Id"/>;
    /// its <see cref="AudioOutputDeviceInfo.DeviceIndex"/> is the default's, so a host that passes it to BASS by mistake gets the default.
    /// </summary>
    public static IReadOnlyList<AudioOutputDeviceInfo> ToOutputDeviceInfos(IReadOnlyList<Fa.AudioDeviceInfo> devices) =>
    [
        AudioOutputDeviceInfo.Primary,
        .. devices.Select(static device => new AudioOutputDeviceInfo(AudioOutputDeviceInfo.PrimaryDeviceIndex, device.Name, Driver: null, IsPrimary: false, IsEnabled: true, IsInitialized: false)
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
}

/// <summary>
/// The player's device events from two FatzAudio sources, each change once (BASS retirement): looks at the device
/// list (<see cref="OnList"/>) and FatzAudio's own notice that it moved the output (<see cref="OnOutputMoved"/>),
/// which also catches a move the list cannot see (a device unplugged and back within one look, an exclusive-mode
/// reset). Kinds follow <c>IPlayerSession.AudioDeviceChanged</c>: <see cref="AudioDeviceChangeKind.DeviceRemoved"/>
/// only for the device the player plays on; another device going away is a
/// <see cref="AudioDeviceChangeKind.DeviceStateChanged"/>. Not thread-safe: the engine holds its watch lock.
/// </summary>
/// <param name="explicitDeviceId">The device the player opened by id; null when it follows the default.</param>
sealed class FatzAudioDeviceEvents(string? explicitDeviceId)
{
    IReadOnlyList<FatzAudioDevices.Snapshot>? _last;   // null until a look succeeded
    string? _explicitDeviceId = explicitDeviceId;
    string? _reportedDefaultId;
    readonly HashSet<string> _reportedRemoved = [];

    /// <summary>A look at the device list; the first one only sets the baseline.</summary>
    public IReadOnlyList<AudioDeviceChangeInfo> OnList(IReadOnlyList<FatzAudioDevices.Snapshot> now)
    {
        if (_last is not { } last)
        {
            _last = now;
            _reportedDefaultId = DefaultOf(now)?.Id;
            return [];
        }

        var active = ActiveId(last);
        List<AudioDeviceChangeInfo> changes = [];
        foreach (var gone in last.Where(old => !now.Any(device => device.Id == old.Id)))
        {
            if (gone.Id == _explicitDeviceId)
                _explicitDeviceId = null; // FatzAudio moved the output to the default
            if (_reportedRemoved.Remove(gone.Id))
                continue; // the output's own notice told it already

            changes.Add(gone.Id == active
                ? new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceRemoved, gone.Id, gone.Name, null, 0)
                : new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceStateChanged, gone.Id, gone.Name, $"Audio device {gone.Name} went away (not the one playing).", 0));
        }

        foreach (var added in now.Where(device => !last.Any(old => old.Id == device.Id)))
        {
            _ = _reportedRemoved.Remove(added.Id);
            changes.Add(new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceAdded, added.Id, added.Name, null, 0));
        }

        if (DefaultOf(now) is { } newDefault && newDefault.Id != _reportedDefaultId)
        {
            _reportedDefaultId = newDefault.Id;
            changes.Add(new AudioDeviceChangeInfo(AudioDeviceChangeKind.DefaultDeviceChanged, newDefault.Id, newDefault.Name, null, 0));
        }

        _last = now;
        return changes;
    }

    /// <summary>
    /// FatzAudio moved the output: to the new default (<paramref name="deviceWentAway"/> false), or onto the default
    /// because its device went away. <paramref name="now"/> is a fresh look when one could be read.
    /// </summary>
    public IReadOnlyList<AudioDeviceChangeInfo> OnOutputMoved(bool deviceWentAway, IReadOnlyList<FatzAudioDevices.Snapshot>? now)
    {
        if (deviceWentAway)
        {
            var active = _last is { } last ? ActiveId(last) : _explicitDeviceId;
            _explicitDeviceId = null;
            if (active is null || !_reportedRemoved.Add(active))
                return [];

            var name = _last?.FirstOrDefault(device => device.Id == active).Name;
            return [new AudioDeviceChangeInfo(AudioDeviceChangeKind.DeviceRemoved, active, name, null, 0)];
        }

        if (now is null || DefaultOf(now) is not { } newDefault || newDefault.Id == _reportedDefaultId)
            return [];

        _reportedDefaultId = newDefault.Id;
        return [new AudioDeviceChangeInfo(AudioDeviceChangeKind.DefaultDeviceChanged, newDefault.Id, newDefault.Name, null, 0)];
    }

    string? ActiveId(IReadOnlyList<FatzAudioDevices.Snapshot> list) => _explicitDeviceId ?? DefaultOf(list)?.Id;

    static FatzAudioDevices.Snapshot? DefaultOf(IReadOnlyList<FatzAudioDevices.Snapshot> list) =>
        list.Any(static device => device.IsDefault) ? list.First(static device => device.IsDefault) : null;
}

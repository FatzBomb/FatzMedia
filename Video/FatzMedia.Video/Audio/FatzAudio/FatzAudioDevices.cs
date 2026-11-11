using FatzMedia.Video.Core.Abstractions;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// The output devices a host can offer: what to pass as <c>MediaPlayerConfiguration.AudioOutputDeviceId</c>.
/// Needs the FatzAudio native library.
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
    /// The default entry, then each device in FatzAudio's order, chosen by <see cref="AudioOutputDeviceInfo.Id"/>.
    /// </summary>
    public static IReadOnlyList<AudioOutputDeviceInfo> ToOutputDeviceInfos(IReadOnlyList<Fa.AudioDeviceInfo> devices) =>
    [
        AudioOutputDeviceInfo.Primary,
        .. devices.Select(static device => new AudioOutputDeviceInfo(device.Id, device.Name, IsPrimary: false)),
    ];

    /// <summary>The device a configuration names: its id exactly, else its name ignoring case; null when none matches.</summary>
    public static Fa.AudioDeviceInfo? Find(IReadOnlyList<Fa.AudioDeviceInfo> devices, string idOrName) =>
        devices.FirstOrDefault(device => string.Equals(device.Id, idOrName, StringComparison.Ordinal))
        ?? devices.FirstOrDefault(device => string.Equals(device.Name, idOrName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<Snapshot> ToSnapshots(IReadOnlyList<Fa.AudioDeviceInfo> devices) =>
        [.. devices.Select(static device => new Snapshot(device.Id, device.Name, device.IsDefault))];
}

/// <summary>
/// The player's device events from two FatzAudio sources, each change once: looks at the device
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

    /// <summary>The engine moved the output itself: to <paramref name="deviceId"/>, or to the default (null).</summary>
    public void OnMovedTo(string? deviceId) => _explicitDeviceId = deviceId;

    string? ActiveId(IReadOnlyList<FatzAudioDevices.Snapshot> list) => _explicitDeviceId ?? DefaultOf(list)?.Id;

    static FatzAudioDevices.Snapshot? DefaultOf(IReadOnlyList<FatzAudioDevices.Snapshot> list) =>
        list.Any(static device => device.IsDefault) ? list.First(static device => device.IsDefault) : null;
}

/// <summary>
/// Brings the sound back to the device the player was created on (<c>AudioOutputDeviceId</c>) when it returns after going
/// away (ENG-065, owner design 2026-11-11). While it is away the player plays on the default. A change of the Windows
/// default after <see cref="Grace"/> means the user chose another device, so the player stops waiting; changes within
/// the grace are Windows replacing a lost default by itself. Pure: the engine calls it on each look at the device list.
/// Not thread-safe: the engine holds its watch lock.
/// </summary>
sealed class ChosenDeviceReturn(string chosenId, string chosenName)
{
    /// <summary>How long after the loss a change of the default is taken as Windows', not the user's.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    bool _away;
    bool _stopped;
    TimeSpan _lostAt;
    string? _defaultId;
    HashSet<string> _idsAtLoss = [];

    /// <summary>What the engine should do after a look at the list.</summary>
    public enum Step
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>The chosen device went away: play on the default now.</summary>
        MoveToDefault,

        /// <summary>The chosen device is back (<see cref="Decision.Device"/>): move the output to it.</summary>
        MoveBack,

        /// <summary>The user chose another default while waiting: stop waiting for good.</summary>
        StopWaiting,
    }

    /// <summary>A step, with the returned device for <see cref="Step.MoveBack"/>.</summary>
    public readonly record struct Decision(Step Step, FatzAudioDevices.Snapshot? Device = null);

    /// <summary>The chosen device's name, for reports.</summary>
    public string ChosenName => chosenName;

    /// <summary>The move back after <see cref="Step.MoveBack"/> failed: keep waiting, so the next look tries again.</summary>
    public void MoveBackFailed() => _away = true;

    /// <summary>One look at the device list at <paramref name="time"/> (any steady clock).</summary>
    public Decision OnList(IReadOnlyList<FatzAudioDevices.Snapshot> now, TimeSpan time)
    {
        if (_stopped)
            return default;

        if (!_away)
        {
            if (now.Any(device => device.Id == chosenId))
                return default;

            _away = true;
            _lostAt = time;
            _defaultId = DefaultId(now);
            _idsAtLoss = [.. now.Select(static device => device.Id)];
            return new Decision(Step.MoveToDefault);
        }

        if (FindReturned(now) is { } back)
        {
            _away = false;
            return new Decision(Step.MoveBack, back);
        }

        var defaultId = DefaultId(now);
        if (time - _lostAt <= Grace)
        {
            _defaultId = defaultId; // Windows replacing a lost default by itself
            return default;
        }

        if (defaultId is not null && defaultId != _defaultId)
        {
            _stopped = true;
            return new Decision(Step.StopWaiting);
        }

        return default;
    }

    // By id; a device that came back with another id is matched by its name, among the devices that were not there when
    // it went away (an identical second device that stayed plugged in is not it), when exactly one has it.
    FatzAudioDevices.Snapshot? FindReturned(IReadOnlyList<FatzAudioDevices.Snapshot> list)
    {
        foreach (var device in list)
        {
            if (device.Id == chosenId)
                return device;
        }

        var byName = list.Where(device => !_idsAtLoss.Contains(device.Id)
            && string.Equals(device.Name, chosenName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    static string? DefaultId(IReadOnlyList<FatzAudioDevices.Snapshot> list) =>
        list.FirstOrDefault(static device => device.IsDefault).Id;
}

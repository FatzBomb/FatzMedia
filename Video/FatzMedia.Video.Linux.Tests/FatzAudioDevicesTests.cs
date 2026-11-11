using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Output devices on the FatzAudio engine: choosing one, and the device-list watch that is the player's
/// device monitor. The rules are pure; the native checks run where FatzAudio's library is built and look at
/// whatever devices this machine has (they never need a particular one).
/// </summary>
[Collection(FatzAudioNativeGroup.Name)]
public sealed class FatzAudioDevicesTests
{
    static readonly Fa.AudioDeviceInfo Speakers = new("id-speakers", "Speakers (Realtek)", Fa.DeviceKind.Playback, true, 48000, 2);
    static readonly Fa.AudioDeviceInfo Usb = new("id-usb", "USB Audio", Fa.DeviceKind.Playback, false, 44100, 2);

    [Fact]
    public void Find_ById_ThenByNameIgnoringCase()
    {
        Assert.Same(Usb, FatzAudioDevices.Find([Speakers, Usb], "id-usb"));
        Assert.Same(Speakers, FatzAudioDevices.Find([Speakers, Usb], "speakers (realtek)"));
        Assert.Null(FatzAudioDevices.Find([Speakers, Usb], "HDMI"));
    }

    // ENG-065: the chosen device ("b", USB) goes away and comes back; "a" is the default.
    static readonly FatzAudioDevices.Snapshot Default = new("a", "Speakers", true);
    static readonly FatzAudioDevices.Snapshot Chosen = new("b", "USB", false);
    static readonly FatzAudioDevices.Snapshot Other = new("c", "HDMI", false);

    static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ChosenDevice_GoneThenBack_PlaysOnTheDefault_ThenMovesBack_EveryTime()
    {
        var chosen = new ChosenDeviceReturn("b", "USB");

        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([Default, Chosen], At(0)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.MoveToDefault, chosen.OnList([Default], At(1)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([Default], At(60)).Step);   // minutes later is fine
        var back = chosen.OnList([Default, Chosen], At(120));
        Assert.Equal(ChosenDeviceReturn.Step.MoveBack, back.Step);
        Assert.Equal("b", back.Device?.Id);

        Assert.Equal(ChosenDeviceReturn.Step.MoveToDefault, chosen.OnList([Default], At(130)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.MoveBack, chosen.OnList([Default, Chosen], At(131)).Step);
    }

    [Fact]
    public void ChosenDevice_ADefaultChangeRightAfterTheLoss_IsWindows_AndKeepsWaiting()
    {
        var chosen = new ChosenDeviceReturn("b", "USB");
        var hdmiDefault = Other with { IsDefault = true };
        var speakers = Default with { IsDefault = false };

        Assert.Equal(ChosenDeviceReturn.Step.MoveToDefault, chosen.OnList([Default], At(10)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([speakers, hdmiDefault], At(10 + 2)).Step);   // within the grace
        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([speakers, hdmiDefault], At(10 + 30)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.MoveBack, chosen.OnList([speakers, hdmiDefault, Chosen], At(10 + 40)).Step);
    }

    [Fact]
    public void ChosenDevice_ADefaultChosenByTheUserWhileAway_StopsWaitingForGood()
    {
        var chosen = new ChosenDeviceReturn("b", "USB");
        var hdmiDefault = Other with { IsDefault = true };
        var speakers = Default with { IsDefault = false };

        Assert.Equal(ChosenDeviceReturn.Step.MoveToDefault, chosen.OnList([Default, Other], At(0)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.StopWaiting, chosen.OnList([speakers, hdmiDefault], At(20)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([speakers, hdmiDefault, Chosen], At(30)).Step);
        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([speakers, hdmiDefault], At(40)).Step);
    }

    [Fact]
    public void ChosenDevice_BackUnderANewId_IsFoundByItsName_WhenOnlyOneHasIt()
    {
        var chosen = new ChosenDeviceReturn("b", "USB");
        Assert.Equal(ChosenDeviceReturn.Step.MoveToDefault, chosen.OnList([Default], At(0)).Step);

        Assert.Equal(ChosenDeviceReturn.Step.None, chosen.OnList([Default, new("b2", "usb", false), new("b3", "USB", false)], At(5)).Step);
        var back = chosen.OnList([Default, new("b2", "usb", false)], At(6));
        Assert.Equal(ChosenDeviceReturn.Step.MoveBack, back.Step);
        Assert.Equal("b2", back.Device?.Id);
    }

    [Fact]
    public void OutputDeviceInfos_StartWithTheDefault_ThenCarryIds()
    {
        var list = FatzAudioDevices.ToOutputDeviceInfos([Speakers, Usb]);

        Assert.Equal(3, list.Count);
        Assert.True(list[0].IsPrimary);
        Assert.Null(list[0].Id);
        Assert.Equal(("id-usb", "USB Audio"), (list[2].Id, list[2].Name));
    }

    static readonly FatzAudioDevices.Snapshot A = new("a", "Speakers", true);
    static readonly FatzAudioDevices.Snapshot B = new("b", "USB", false);
    static readonly FatzAudioDevices.Snapshot C = new("c", "HDMI", false);

    static (AudioDeviceChangeKind, string)[] Kinds(IReadOnlyList<AudioDeviceChangeInfo> changes) =>
        [.. changes.Select(static change => (change.Kind, change.DeviceId!))];

    [Fact]
    public void Events_FromTheList_RemovedAddedThenANewDefault_RemovedOnlyForTheActiveDevice()
    {
        var events = new FatzAudioDeviceEvents(explicitDeviceId: null);
        Assert.Empty(events.OnList([A, B])); // the baseline
        Assert.Empty(events.OnList([A, B]));

        // Another device goes away and one arrives, which becomes the default.
        Assert.Equal(
            [(AudioDeviceChangeKind.DeviceStateChanged, "b"), (AudioDeviceChangeKind.DeviceAdded, "c"), (AudioDeviceChangeKind.DefaultDeviceChanged, "c")],
            Kinds(events.OnList([A with { IsDefault = false }, C with { IsDefault = true }])));

        // The default (the one playing) goes away.
        Assert.Equal(
            [(AudioDeviceChangeKind.DeviceRemoved, "c"), (AudioDeviceChangeKind.DefaultDeviceChanged, "a")],
            Kinds(events.OnList([A])));
    }

    [Fact]
    public void Events_TheOutputsOwnNotice_IsReportedOnce_WithTheList()
    {
        // Playing on B by choice: B goes away, FatzAudio says so before the list does.
        var events = new FatzAudioDeviceEvents(explicitDeviceId: "b");
        Assert.Empty(events.OnList([A, B]));
        Assert.Equal([(AudioDeviceChangeKind.DeviceRemoved, "b")], Kinds(events.OnOutputMoved(deviceWentAway: true, [A])));
        Assert.Empty(events.OnList([A]));

        // Now on the default: a new default reported by the notice is not reported again by the list.
        Assert.Equal([(AudioDeviceChangeKind.DefaultDeviceChanged, "c")], Kinds(events.OnOutputMoved(deviceWentAway: false, [A with { IsDefault = false }, C with { IsDefault = true }])));
        Assert.Equal([(AudioDeviceChangeKind.DeviceAdded, "c")], Kinds(events.OnList([A with { IsDefault = false }, C with { IsDefault = true }])));

        // Unplugged and back within one look: only the notice sees it.
        Assert.Equal([(AudioDeviceChangeKind.DeviceRemoved, "c")], Kinds(events.OnOutputMoved(deviceWentAway: true, null)));
        Assert.Empty(events.OnList([A with { IsDefault = false }, C with { IsDefault = true }]));
    }

    [Fact]
    public void Events_WithoutABaseline_TheFirstGoodLookIsTheBaseline()
    {
        var events = new FatzAudioDeviceEvents(explicitDeviceId: null);
        Assert.Empty(events.OnOutputMoved(deviceWentAway: false, null));
        Assert.Empty(events.OnList([A, B, C]));
    }

    [Fact]
    public void Engine_PlaysOnTheNamedDevice_OrTheDefault_OrFailsWithoutFallback()
    {
        // Real devices (nothing is played): Windows, where a desktop has them; Linux CI has none.
        if (!FatzAudioNativeGroup.CanRun || !OperatingSystem.IsWindows())
            return;

        var devices = FatzAudioOutputDevices.Enumerate();
        Assert.True(devices[0].IsPrimary);
        if (devices.Count < 2)
            return; // no playback device on this machine (CI): nothing to open

        var chosen = devices[^1];
        using (var engine = Create(new MediaPlayerConfiguration { AudioOutputDeviceId = chosen.Id }))
            Assert.Equal(chosen.Id, engine.OutputDeviceInfo?.Id);

        // By name only when the name is unique here (by name, the first match wins).
        if (devices.Count(device => string.Equals(device.Name, chosen.Name, StringComparison.OrdinalIgnoreCase)) == 1)
        {
            using var engine = Create(new MediaPlayerConfiguration { AudioOutputDeviceId = chosen.Name });
            Assert.Equal(chosen.Id, engine.OutputDeviceInfo?.Id);
        }

        using (var engine = Create(new MediaPlayerConfiguration { AudioOutputDeviceId = "no such device" }))
            Assert.Null(engine.OutputDeviceInfo);

        _ = Assert.Throws<InvalidOperationException>(() => Create(new MediaPlayerConfiguration
        {
            AudioOutputDeviceId = "no such device",
            AudioOutputFallbackMode = AudioOutputFallbackMode.NoFallback,
        }));
    }

    [Fact]
    public void Engine_WatchesTheDeviceList_OnlyWhileSomeoneListens_AndReportsIt()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        using var engine = (FatzAudioEngine)new FatzAudioEngineFactory(nullOutput: true).Create(new MediaPlayerConfiguration
        {
            Normalization = new NormalizationOptions { CacheDirectory = string.Empty },
        });
        engine.DeviceWatchInterval = TimeSpan.FromMilliseconds(50);
        IAudioDeviceChangeSource source = engine;

        // Nothing changes here: a quiet list gives no events, and listening starts and stops the watch.
        List<AudioDeviceChangeInfo> changes = [];
        void OnChanged(object? sender, AudioDeviceChangeInfo info) => changes.Add(info);
        source.DeviceChanged += OnChanged;
        Assert.True(engine.IsWatchingDevices);
        Thread.Sleep(300);
        source.DeviceChanged -= OnChanged;
        Assert.False(engine.IsWatchingDevices);
        Assert.Empty(changes);
    }

    static FatzAudioEngine Create(MediaPlayerConfiguration configuration) =>
        (FatzAudioEngine)new FatzAudioEngineFactory().Create(configuration with
        {
            Normalization = configuration.Normalization with { CacheDirectory = string.Empty },
        });
}

using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Output devices on the FatzAudio engine: choosing one, and the device-list watch that replaces BASS's
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

    [Fact]
    public void OutputDeviceInfos_StartWithTheDefault_ThenCarryIds()
    {
        var list = FatzAudioDevices.ToOutputDeviceInfos([Speakers, Usb]);

        Assert.Equal(3, list.Count);
        Assert.True(list[0].IsPrimary);
        Assert.Null(list[0].Id);
        Assert.Equal(("id-usb", "USB Audio"), (list[2].Id, list[2].Name));
    }

    [Fact]
    public void Diff_ReportsRemovedAddedAndANewDefault_InThatOrder()
    {
        FatzAudioDevices.Snapshot speakers = new("a", "Speakers", true);
        FatzAudioDevices.Snapshot usb = new("b", "USB", false);
        FatzAudioDevices.Snapshot hdmi = new("c", "HDMI", false);

        Assert.Empty(FatzAudioDevices.Diff([speakers, usb], [speakers, usb]));

        var changes = FatzAudioDevices.Diff([speakers, usb], [speakers with { IsDefault = false }, hdmi with { IsDefault = true }]);
        Assert.Equal(
            [
                (AudioDeviceChangeKind.DeviceRemoved, "b"),
                (AudioDeviceChangeKind.DeviceAdded, "c"),
                (AudioDeviceChangeKind.DefaultDeviceChanged, "c"),
            ],
            changes.Select(static change => (change.Kind, change.DeviceId!)));

        // The default device unplugged, the other one becomes the default.
        changes = FatzAudioDevices.Diff([speakers, usb], [usb with { IsDefault = true }]);
        Assert.Equal(
            [(AudioDeviceChangeKind.DeviceRemoved, "a"), (AudioDeviceChangeKind.DefaultDeviceChanged, "b")],
            changes.Select(static change => (change.Kind, change.DeviceId!)));
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

        using (var engine = Create(new MediaPlayerConfiguration { AudioOutputDeviceId = chosen.Name }))
            Assert.Equal(chosen.Id, engine.OutputDeviceInfo?.Id);

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
        Assert.True(source.ReportsDeviceList);

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

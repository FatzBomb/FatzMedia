using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Audio;

namespace FatzBombMedia.MediaControl.Tests;

public class PollingAudioDeviceMonitorTests
{
    [Fact]
    public void DetectChange_NoChange_ReturnsNull()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            currentSampleRate: 44100,
            currentDevice: 0,
            currentDeviceName: "Speakers");

        Assert.Null(result);
    }

    [Fact]
    public void DetectChange_DeviceIndexChanged_ReturnsDefaultDeviceChanged()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            currentSampleRate: 44100,
            currentDevice: 1,
            currentDeviceName: "Headphones");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, result.Kind);
        Assert.Equal("Headphones", result.DeviceName);
    }

    [Fact]
    public void DetectChange_SampleRateDroppedToZero_ReturnsDeviceRemoved()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            currentSampleRate: 0,
            currentDevice: 0,
            currentDeviceName: "Speakers");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DeviceRemoved, result.Kind);
        Assert.Equal("Speakers", result.DeviceName);
        Assert.NotNull(result.FailureMessage);
    }

    [Fact]
    public void DetectChange_SampleRateRoseFromZero_ReturnsDeviceAdded()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 0,
            lastDevice: 0,
            lastDeviceName: null,
            currentSampleRate: 48000,
            currentDevice: 0,
            currentDeviceName: "Speakers");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DeviceAdded, result.Kind);
        Assert.Equal("Speakers", result.DeviceName);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public void DetectChange_SampleRateChangedBetweenNonZero_ReturnsDeviceStateChanged()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            currentSampleRate: 48000,
            currentDevice: 0,
            currentDeviceName: "Speakers");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DeviceStateChanged, result.Kind);
        Assert.Contains("44100", result.FailureMessage);
        Assert.Contains("48000", result.FailureMessage);
    }

    [Fact]
    public void DetectChange_OnlyNameChanged_ReturnsDefaultDeviceChanged()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers (Realtek)",
            currentSampleRate: 44100,
            currentDevice: 0,
            currentDeviceName: "Speakers (USB)");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, result.Kind);
    }

    [Fact]
    public void DetectChange_InitialStateFromZeroToNonZero_ReturnsDeviceAdded()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 0,
            lastDevice: 0,
            lastDeviceName: null,
            currentSampleRate: 44100,
            currentDevice: 0,
            currentDeviceName: "Speakers");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DeviceAdded, result.Kind);
    }

    [Fact]
    public void DetectChange_DeviceIndexTakesPrecedenceOverSampleRateChange()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            currentSampleRate: 48000,
            currentDevice: 1,
            currentDeviceName: "Headphones");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, result.Kind);
    }

    [Fact]
    public void DetectChange_DefaultEndpointIdChanged_ReturnsDefaultDeviceChanged()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            lastDefaultEndpointId: "endpoint-a",
            lastDefaultEndpointName: "Speakers",
            currentSampleRate: 44100,
            currentDevice: 0,
            currentDeviceName: "Speakers",
            currentDefaultEndpointId: "endpoint-b",
            currentDefaultEndpointName: "Headphones");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, result.Kind);
        Assert.Equal("endpoint-b", result.DeviceId);
        Assert.Equal("Headphones", result.DeviceName);
        Assert.Contains("leaving playback under BASS/Windows control", result.FailureMessage);
    }

    [Fact]
    public void DetectChange_DefaultEndpointNameChangedWithoutIds_ReturnsDefaultDeviceChanged()
    {
        AudioDeviceChangeInfo? result = PollingAudioDeviceMonitor.DetectChange(
            lastSampleRate: 44100,
            lastDevice: 0,
            lastDeviceName: "Speakers",
            lastDefaultEndpointId: null,
            lastDefaultEndpointName: "Speakers",
            currentSampleRate: 44100,
            currentDevice: 0,
            currentDeviceName: "Speakers",
            currentDefaultEndpointId: null,
            currentDefaultEndpointName: "Headphones");

        Assert.NotNull(result);
        Assert.Equal(AudioDeviceChangeKind.DefaultDeviceChanged, result.Kind);
        Assert.Equal("Headphones", result.DeviceName);
    }

    [Fact]
    public void HasDefaultEndpointChanged_WhenOnlyCurrentEndpointAvailable_ReturnsFalse()
    {
        var result = PollingAudioDeviceMonitor.HasDefaultEndpointChanged(
            lastEndpointId: null,
            lastEndpointName: null,
            currentEndpointId: "endpoint-b",
            currentEndpointName: "Headphones");

        Assert.False(result);
    }
}

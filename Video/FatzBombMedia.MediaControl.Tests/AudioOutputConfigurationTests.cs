using FatzBombMedia.MediaControl.Core.Configuration;

namespace FatzBombMedia.MediaControl.Tests;

public class AudioOutputConfigurationTests
{
    [Fact]
    public void MediaPlayerConfiguration_DefaultsToPrimaryWithFallback()
    {
        var configuration = new MediaPlayerConfiguration();

        Assert.Equal(AudioOutputDeviceInfo.PrimaryDeviceIndex, configuration.AudioOutputDeviceIndex);
        Assert.Equal(AudioOutputFallbackMode.FallbackToPrimary, configuration.AudioOutputFallbackMode);
    }

    [Fact]
    public void AudioOutputDeviceInfo_PrimaryFormatsForSelectors()
    {
        AudioOutputDeviceInfo primary = AudioOutputDeviceInfo.Primary;

        Assert.True(primary.IsPrimary);
        Assert.Equal(AudioOutputDeviceInfo.PrimaryDeviceIndex, primary.DeviceIndex);
        Assert.Equal("Primary (device default)", primary.DisplayName);
        Assert.Equal(primary.DisplayName, primary.ToString());
    }
}

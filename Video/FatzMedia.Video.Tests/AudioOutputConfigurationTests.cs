using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

public class AudioOutputConfigurationTests
{
    [Fact]
    public void MediaPlayerConfiguration_DefaultsToPrimaryWithFallback()
    {
        var configuration = new MediaPlayerConfiguration();

        Assert.Equal(AudioOutputDeviceInfo.PrimaryDeviceIndex, configuration.AudioOutputDeviceIndex);
        Assert.Equal(AudioOutputFallbackMode.FallbackToPrimary, configuration.AudioOutputFallbackMode);
        Assert.False(configuration.ResetPitchOnMediaLoad);
        Assert.False(configuration.ResetTempoOnMediaLoad);
    }

    [Fact]
    public void MediaPlayerConfiguration_LoadResetOptionsAreIndependent()
    {
        var pitchOnly = new MediaPlayerConfiguration { ResetPitchOnMediaLoad = true };
        var tempoOnly = new MediaPlayerConfiguration { ResetTempoOnMediaLoad = true };

        Assert.True(pitchOnly.ResetPitchOnMediaLoad);
        Assert.False(pitchOnly.ResetTempoOnMediaLoad);
        Assert.False(tempoOnly.ResetPitchOnMediaLoad);
        Assert.True(tempoOnly.ResetTempoOnMediaLoad);
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

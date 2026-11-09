using ManagedBass;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// The channel volume attribute (user volume, and the scrub mute applied over it). It acts at
/// playback, after the playback buffer, so a change is heard at once, unlike an FX stage.
/// </summary>
static class BassChannelVolume
{
    /// <summary>Sets the volume now, ending any slide still running toward an older level.</summary>
    public static bool Set(int playHandle, double volume01)
    {
        var volume = (float)Math.Clamp(volume01, 0.0, 1.0);
        if (MBass.ChannelIsSliding(playHandle, ChannelAttribute.Volume))
            _ = MBass.ChannelSlideAttribute(playHandle, ChannelAttribute.Volume, volume, 0);

        return MBass.ChannelSetAttribute(playHandle, ChannelAttribute.Volume, volume);
    }

    /// <summary>Ramps the volume linearly to <paramref name="volume01"/>, replacing a running slide.</summary>
    public static bool Slide(int playHandle, double volume01, TimeSpan duration)
    {
        var milliseconds = (int)Math.Clamp(Math.Ceiling(duration.TotalMilliseconds), 0, int.MaxValue);
        return milliseconds == 0
            ? Set(playHandle, volume01)
            : MBass.ChannelSlideAttribute(playHandle, ChannelAttribute.Volume, (float)Math.Clamp(volume01, 0.0, 1.0), milliseconds);
    }
}
